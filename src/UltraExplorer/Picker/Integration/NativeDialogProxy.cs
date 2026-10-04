using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Threading;

namespace UltraExplorer.Picker.Integration;

/// <summary>
/// One replacement: reads another program's open Windows file dialog, arms a
/// lease with the watchdog, shows UltraExplorer's picker over it and hides the
/// original (transparent, never closed, never moved), then hands the answer,
/// the file type and the caller's own options back to that original, which
/// stays the one that validates and returns them. Anything unexpected puts the
/// original back as it was.
///
/// <para>In a prepared worker the presentation is staged. At recognition the
/// dialog is read with plain Win32 (<see cref="FastDialogRead"/>: folder,
/// mode, title, labels, rectangle, then its name and file types from its own
/// thread, ~10 ms); the prepared picker (<see cref="PreparedPicker"/>) is
/// bound to that, navigated while still cloaked, placed over the dialog and
/// uncloaked - on screen tens of milliseconds after recognition. UI
/// Automation reads the full contract meanwhile (types, the caller's options,
/// multiple selection). A protected lease hides the original before this
/// provisional picker appears; OK waits for the full contract. Unsupported
/// contracts restore the original and dismiss the picker. Without a prepared window the
/// picker is built after the read, as a separate process does it.</para>
/// </summary>
internal sealed class NativeDialogProxy
{
    private readonly Application _app;
    private readonly nint _original;
    private readonly uint _originalProcess;
    private readonly PreparedPicker? _prepared;
    private readonly AutomationThread _thread = new();
    private readonly ObservableCollection<NativeOptionViewModel> _options = [];
    private readonly DispatcherTimer _pulse = new(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(200) };
    private long _recognised;
    private NativeDialogAutomation? _automation;
    private NativeDialogSnapshot? _snapshot;
    private Task<NativeDialogSnapshot>? _capture;
    private DialogLease? _lease;
    private MainWindow? _window;
    /// <summary>The prepared picker taken for this dialog and put over it, not yet bound (and never shown unbound).</summary>
    private (MainWindow Window, NativeOptionsView Controls, nint Handle)? _placed;
    private nint _placedHandle;
    private NativeOptionsView? _controls;
    private FileDialogSession? _session;
    private Task _pending = Task.CompletedTask;
    private bool _fallback, _finishing, _refreshing, _recoveryPending, _handedBack;
    private bool _isPrepared, _hidden, _filterWatched, _pulseStarted;
    private nint _handle;
    private long _lastRefresh;
    private int _generation;
    private double _frameMs = double.NaN, _readyMs = double.NaN, _readMs = double.NaN;
    private int _roots = -1;
    private string _rootDiagnostics = string.Empty;
    private string _path = "built";
    private readonly System.Text.StringBuilder _stages = new();

    /// <summary>For the log: when each stage of the presentation ended, from recognition.</summary>
    private void Stage(string name) => _stages.Append(CultureInfo.InvariantCulture, $" {name}={Since:F0}");

    private void CaptureRootDiagnostics()
    {
        _roots = _window!.PickerRootCount;
        // Only an owned fixture records paths. Ordinary diagnostics keep their
        // existing count and do not allocate or disclose a path snapshot.
        if (IsFixture)
            _rootDiagnostics = " roots-data=" + Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(_window.PickerRootPaths));
    }
    internal bool CanReuseWorker { get; private set; } = true;

    /// <summary>The replacement this process is serving a dialog with now, if any.  UI thread.</summary>
    private static NativeDialogProxy? _serving;

    /// <summary>
    /// After an exception on the UI thread that this process survived
    /// (<see cref="Infrastructure.CrashReporter.SurviveQuietly"/>): the dialog
    /// being served goes back to Windows with every option of its own, as
    /// "use the Windows dialog" does, and the replacement stays on.  The
    /// worker is not used again: whatever the exception left in it goes with
    /// it.  False when no dialog is being served.  UI thread.
    /// </summary>
    internal static bool ReturnServedDialogAfterFailure()
    {
        if (_serving is not { } proxy)
        {
            return false;
        }

        proxy.CanReuseWorker = false;
        if (!proxy._handedBack)
        {
            DialogIntegrationStore.Log($"{proxy.Application}: returned to its Windows dialog after a problem in the picker.");
            proxy.ReturnToWindows();
        }

        return true;
    }

    /// <param name="recognised">When the listener recognised the dialog (<see cref="Stopwatch.GetTimestamp"/>, which every process shares); 0 for now.</param>
    /// <param name="prepared">A prepared worker's ready picker, when there is one.</param>
    public NativeDialogProxy(Application app, nint original, long recognised = 0, PreparedPicker? prepared = null)
    {
        _app = app;
        _original = original;
        _originalProcess = DialogNative.ProcessId(original);
        _recognised = recognised;
        _prepared = prepared;
    }

    private string Application => _snapshot is null ? "an application" : Path.GetFileName(_snapshot.ApplicationPath);
    private double Since => Stopwatch.GetElapsedTime(_recognised).TotalMilliseconds;
    private bool IsFixture => Environment.GetEnvironmentVariable("ULTRAEXPLORER_DIALOG_TEST_ONLY_PROCESS")
        == _originalProcess.ToString(CultureInfo.InvariantCulture) || DialogTestScope.AllowsDirectChild(_originalProcess);
    private bool OriginalExists => _originalProcess != 0 && DialogNative.IsWindow(_original)
        && DialogNative.ProcessId(_original) == _originalProcess;

    public async Task RunAsync()
    {
        NativeRect? bounds = null;
        _serving = this;
        try
        {
            if (!DialogIntegrationStore.Read().Enabled || !OriginalExists) return;
            if (_recognised == 0 || Stopwatch.GetElapsedTime(_recognised) > TimeSpan.FromMinutes(1)) _recognised = Stopwatch.GetTimestamp();
            var glance = FastDialogRead.Read(_original, PreparedPicker.Names);
            bounds = glance.Bounds;
            // The dialog's own thread answers the Win32 details before UI
            // Automation adds its traffic to that thread; the full read then
            // goes on in the background while the picker is put on screen.
            // Meanwhile the prepared picker, still cloaked, is put in place.
            var early = await ReadEarlyAsync(glance);
            if (early is null && _lease is not null)
            {
                // Failed fast metadata must leave the ordinary full-read path
                // usable, rather than a hidden original with no picker frame.
                _lease.Dispose();
                _lease = null;
                _hidden = false;
            }
            if (_fallback) return;
            // Keep the exact lease in this queued read. Cancellation must not
            // let a delayed provider mutate a restored or subsequent dialog.
            var captureLease = _lease;
            var capture = _capture = _thread.Run(() =>
            {
                // A fixture-only delay exercises input and recovery before
                // the provider finishes. Ordinary integration never uses it.
                if (IsFixture && int.TryParse(Environment.GetEnvironmentVariable("ULTRAEXPLORER_DIALOG_TEST_CAPTURE_DELAY_MS"),
                        out var delay) && delay is > 0 and <= 15000)
                    Thread.Sleep(delay);
                _automation = new NativeDialogAutomation();
                return _automation.Capture(_original, captureLease, () => !_fallback);
            });
            var boundedCapture = capture.WaitAsync(TimeSpan.FromSeconds(8));
            if (early is { } read) await PresentEarlyAsync(glance, read.Details);
            // A cancel in the picker, or its "use the Windows dialog", does
            // not wait for the full read of the dialog.
            if (_window is not null)
            {
                await Task.WhenAny(boundedCapture, _window.PickerResult);
                if (_window.PickerResult.IsCompleted)
                {
                    // The provider is still being read: this worker is not reused,
                    // so nothing of this request can reach a later one.
                    if (!capture.IsCompleted) CanReuseWorker = false;
                    if (!_fallback) await HandBackEarlyAsync(await _window.PickerResult);
                    return;
                }
            }
            _snapshot = await FromDialogAsync(boundedCapture, "its dialog could not be read");
            _readMs = Since;
            if (_fallback) return;
            if (_window?.PickerResult.IsCompleted == true) { await HandBackEarlyAsync(await _window.PickerResult); return; }
            if (!OriginalExists || _snapshot.ProcessId != _originalProcess
                || DialogIntegrationStore.IsExcluded(_snapshot.ApplicationPath) || !InFront())
            { DialogIntegrationStore.Log($"{Application}: its dialog is no longer in front; it stays with Windows."); return; }
            var request = NativeDialogRules.RequestFor(_snapshot);
            if (_window is not null)
            {
                var shown = !double.IsNaN(_frameMs);
                await ConfirmEarlyAsync(request);
                // Bound early but not shown yet (its folder was slow to draw):
                // bound to the full contract first, then a last short wait for
                // the finished picture, then the folder as it is - as a newly
                // built window would show it. Nothing on screen changes after.
                if (!shown)
                {
                    await _window.WhenFolderDrawnAsync(TimeSpan.FromMilliseconds(1500));
                    if (!Uncloak()) { DialogIntegrationStore.Log($"{Application}: its dialog is no longer in front; it stays with Windows."); return; }
                }
            }
            else if ((_placed ?? _prepared?.Take()) is { } prepared)
            {
                _placed = null;
                _path = "prepared";
                await BindPreparedAsync(prepared, new FileDialogSession(request), TimeSpan.FromMilliseconds(1500));
                if (!Uncloak()) { DialogIntegrationStore.Log($"{Application}: its dialog is no longer in front; it stays with Windows."); return; }
            }
            if (_fallback) return;

            if (_isPrepared) await ProtectPreparedAsync();
            else await ShowBuiltAsync(request);
            if (_fallback) return;
            DialogIntegrationStore.Log($"{Application}: {_snapshot.Mode} dialog shown through UltraExplorer ({_snapshot.Options.Length} options of its own;"
                + $" {_path} window; read {_readMs:F0} ms, on screen {_frameMs:F0} ms after recognition).");

            var result = await _window!.PickerResult;
            _finishing = true;
            if (_fallback) return;
            if (_lease is null) { await HandBackEarlyAsync(result); return; }
            if (!DialogLease.BelongsToWindow(_lease.Record)) return;
            await _pending.WaitAsync(TimeSpan.FromSeconds(8));
            if (_fallback || !DialogLease.BelongsToWindow(_lease.Record)) return;
            if (result.Accepted)
                await FromDialogAsync(_thread.Run(() => { _automation!.PrepareSubmit(result); return true; }).WaitAsync(TimeSpan.FromSeconds(4)),
                    "the answer could not be handed to its dialog");
            if (_fallback || !DialogLease.BelongsToWindow(_lease.Record)) return;
            // The original window is visible before the application can open
            // any overwrite/sharing/custom warning. Native validation is final.
            _lease.Restore(true);
            if (result.Accepted)
                await FromDialogAsync(_thread.Run(() => { _automation!.Submit(result); return true; }).WaitAsync(TimeSpan.FromSeconds(4)),
                    "the answer could not be handed to its dialog");
            else DialogNative.Click(_original, 2);
            _handedBack = true;
            await FollowUpAsync(result);
            DialogIntegrationStore.Log($"{Application}: {(result.Accepted ? $"{result.Paths.Count} path(s) handed back" : "cancelled")}"
                + (DialogNative.IsWindow(_original) ? "; its dialog is still open (the application asked something or refused)." : "."));
        }
        catch (NotSupportedException ex)
        {
            // Restore a provisionally hidden original with every native option.
            _fallback = true;
            _lease?.Restore(true);
            DialogIntegrationStore.Log($"{Application}: stays with Windows: {ex.Message}");
        }
        catch (Exception ex)
        {
            _fallback = true;
            if (ex is TimeoutException) CanReuseWorker = false;
            _lease?.Restore(true);
            DialogIntegrationStore.Log(_handedBack
                ? $"{Application}: native follow-up ended; its original dialog is available"
                : $"{Application}: returned to its Windows dialog after a problem", ex);
            if (_lease is not null && !_handedBack)
                try { DialogIntegrationStore.Update(settings => settings with { Enabled = false,
                    LastRecovery = "Dialog replacement encountered a problem. Windows dialogs are restored; you can turn it on again in Settings." }); }
                catch (IOException) { }
        }
        finally
        {
            if (ReferenceEquals(_serving, this)) _serving = null;
            _finishing = true;
            _pulse.Stop();
            _lease?.Dispose();
            if (_session is not null) _session.FilterChanged -= OnFilterChanged;
            if (_controls is not null) _controls.UseWindows -= UseWindows;
            GiveKeyboardBack();
            // A prepared window goes from the screen at once; closing it
            // takes a dispatcher turn or two.
            if (_isPrepared && _handle != 0 && DialogNative.IsWindow(_handle)) DialogNative.CloakOwn(_handle, true);
            _window?.CloseFromCaller();
            // Put in place for this dialog, never bound to it: never shown either.
            _placed?.Window.CloseFromCaller();
            // Do not wait for a stuck provider while recovering a user's window:
            // disposed after the read still queued on its thread, if any.
            _ = _thread.Run(() => _automation?.Dispose());
            _thread.Dispose();
            if (!double.IsNaN(_frameMs))
                DialogIntegrationStore.Log(string.Create(CultureInfo.InvariantCulture,
                    $"{Application}: timing path={_path} recognised->frame={_frameMs:F0} ms, recognised->ready={(double.IsNaN(_readyMs) ? -1 : _readyMs):F0} ms, read={_readMs:F0} ms, roots={_roots}{_rootDiagnostics} recognised-qpc={_recognised};{_stages}"));
            _prepared?.Remember(bounds);
        }
    }

    /// <summary>
    /// Reading the application's dialog, or handing it the answer, fails for
    /// reasons of the application's own: it answers too slowly, closes its
    /// dialog meanwhile, its provider fails. While the watchdog still holds the
    /// lease nothing is in doubt - the dialog goes back as one this cannot
    /// serve, and the mode stays on. A failure once the watchdog has gone is
    /// left to pause it, as any other.
    /// </summary>
    private async Task<T> FromDialogAsync<T>(Task<T> work, string what)
    {
        try { return await work; }
        catch (Exception ex) when (ex is not NotSupportedException && _lease is { IsProtected: true })
        {
            if (ex is TimeoutException) CanReuseWorker = false;
            throw new NotSupportedException($"{what} ({ex.GetType().Name}: {ex.Message})", ex);
        }
    }

    private async Task ProtectEarlyAsync(nint proxy)
    {
        _lease = await DialogLease.ArmAsync(_original, proxy);
        _lastRefresh = Environment.TickCount64;
        StartPulse();
        // The original stays on screen, and keeps the keyboard, until the
        // picker comes over it (Uncloak): a folder slow to draw would leave
        // the user with no dialog at all for as long as the full read takes.
        if (!_lease.IsProtected || !DialogIntegrationStore.Read().Enabled || !OriginalExists
            || !DialogLease.BelongsToWindow(_lease.Record) || !InFront())
        { ReturnToWindows(); return; }
        Stage("early-leased");
    }

    /// <summary>
    /// The stage before UI Automation: when the Win32 read already says what
    /// the picker must show - a file system folder, readable file types, only
    /// controls of kinds it mirrors - and a prepared picker is ready, the
    /// picker is bound to that and put on screen now. Anything uncertain
    /// waits for the full read instead; nothing is shown only to be taken back.
    /// </summary>
    private async Task<(string Application, DialogDetails Details)?> ReadEarlyAsync(DialogGlance glance)
    {
        if (_prepared is not { IsReady: true } || !glance.Recognised || glance.Folder is null || !glance.MirrorsControls
            || DialogNative.IsHidden(_original) || !InFront()) return null;
        var application = DialogNative.AccessibleApplication(_original);
        if (application is null || DialogIntegrationStore.IsExcluded(application)) return null;
        // Off this thread and within a budget: the folder is checked on disk
        // (a mapped drive or a junction can wait on the network), and the
        // dialog's thread answers for its name and types. Too slow is not an
        // error: the full read decides, and the picker is shown after it.
        var folder = glance.Folder;
        var read = Task.Run(() => Directory.Exists(folder) ? FastDialogRead.ReadDetails(glance, TimeSpan.FromMilliseconds(160)) : null);
        Stage("glance");
        // While the dialog answers: the prepared picker taken and put over
        // it, still cloaked - its size and owner settled before binding.
        if (_prepared.Take() is { } prepared)
        {
            _placed = prepared;
            _placedHandle = prepared.Handle;
            await ProtectEarlyAsync(prepared.Handle);
            if (_fallback) return null;
            PlaceOverOriginal(prepared.Handle);
            AttachSameProcessOwner(prepared.Window);
            // The footer names the application (its file's version resource).
            prepared.Controls.SetOptions(application, glance.Mode, _options);
            Stage("placed");
        }
        DialogDetails? details;
        // Nothing here is worth failing the replacement for: unread, the full
        // read decides.
        try { details = await read.WaitAsync(TimeSpan.FromMilliseconds(220)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        Stage("details");
        if (details is null || (glance.Mode != FileDialogMode.PickFolder && details.Filters is not { Length: > 0 })) return null;
        return (application, details);
    }

    private async Task PresentEarlyAsync(DialogGlance glance, DialogDetails details)
    {
        if (_fallback || _placed is not { } prepared) return;
        _placed = null;
        _path = "prepared-early";
        var request = new FileDialogRequest
        {
            Mode = glance.Mode, Title = glance.Title, InitialFolder = glance.Folder!, FileName = details.FileName ?? string.Empty,
            OkButtonLabel = glance.OkLabel, FileNameLabel = glance.NameLabel, FileTypeIndex = details.FilterIndex,
            OwnerHandle = _original, IsNativeProxy = true,
            Options = FileDialogOptions.ForceFileSystem | FileDialogOptions.PathMustExist | FileDialogOptions.NoTestFileCreate
        };
        request.Filters.AddRange(details.Filters ?? []);
        // Its footer already names the application (see ReadEarlyAsync).
        _controls = prepared.Controls;
        // On screen now only with the folder drawn; otherwise after the full read.
        if (await BindPreparedAsync(prepared, new FileDialogSession(request.Normalize()), TimeSpan.FromMilliseconds(700))) Uncloak();
    }

    /// <summary>
    /// Binds the prepared picker to <paramref name="session"/> while it is
    /// still cloaked, puts it over the original (same monitor and scale,
    /// owned like it) and waits for the frame that shows the folder: true
    /// once it is drawn, false after <paramref name="drawLimit"/>. The
    /// original is untouched.
    /// </summary>
    private async Task<bool> BindPreparedAsync((MainWindow Window, NativeOptionsView Controls, nint Handle) prepared, FileDialogSession session,
        TimeSpan drawLimit)
    {
        (_window, _controls, _handle) = prepared;
        _isPrepared = true;
        _session = session;
        _app.MainWindow = _window;
        _controls.UseWindows += UseWindows;
        if (_snapshot is not null) _controls.SetOptions(_snapshot.ApplicationPath, _snapshot.Mode, _options);
        if (_handle != _placedHandle)
        {
            PlaceOverOriginal(_handle);
            AttachSameProcessOwner(_window);
            Stage("placed");
        }
        // The folder is on the canvas when this returns; the tree's reveal of
        // it (address, selection) goes on while the canvas reads and draws.
        var bind = _window.RebindAsync(session);
        Stage("bound");
        var drawn = _window.WhenFolderDrawnAsync(drawLimit);
        await bind;
        Stage("revealed");
        var isDrawn = await drawn;
        Stage(isDrawn ? "drawn" : "not-drawn");
        return isDrawn;
    }

    /// <summary>
    /// The bound picker onto the screen, above the original, not activated:
    /// its first composed frame is the one already drawn. In real use the
    /// keyboard follows what the user now sees (the original, still visible
    /// under it, gets it back should the dialog stay with Windows); OK stays
    /// disabled until the contract is confirmed. False, and nothing shown,
    /// when the dialog is no longer in front or already found not mirrorable.
    /// </summary>
    private bool Uncloak()
    {
        if (_fallback || _window is null || _window.PickerResult.IsCompleted || !OriginalExists || !InFront()
            || (_lease is not null && (!_lease.IsProtected || !DialogLease.BelongsToWindow(_lease.Record)))
            || _capture is { IsFaulted: true } or { IsCanceled: true }) return false;
        // Under a lease the original goes from the screen only now, as the
        // picker comes over it.
        if (_lease is not null)
        {
            if (!DialogNative.IsHidden(_original) && !DialogNative.Hide(_original)) return false;
            _hidden = true;
        }
        // The frame drawn while cloaked is composed; the uncloak shows it,
        // over the original (still cloaked, so its first frame is over it).
        DialogNative.WaitForComposition();
        if (!DialogNative.RaiseAbove(_handle, _original))
        {
            DialogIntegrationStore.Log($"{Application}: the picker could not be put over its dialog.");
            return false;
        }
        DialogNative.CloakOwn(_handle, false);
        DialogNative.WaitForComposition();
        _frameMs = Since;
        CaptureRootDiagnostics();
        HandKeyboardOver();
        StartPulse();
        // The tree behind the canvas is watched on disk once the frame is out.
        var window = _window;
        _ = window.Dispatcher.InvokeAsync(window.WatchChanges, DispatcherPriority.Background);
        return true;
    }

    /// <summary>
    /// Cancels during the provisional read without revealing the original.
    /// A native caller that ignores Cancel is restored by final cleanup.
    /// </summary>
    private async Task HandBackEarlyAsync(FileDialogResult result)
    {
        if (_fallback || _handedBack || result.Accepted) return;
        _fallback = true;
        GiveKeyboardBack();
        if (_isPrepared && _handle != 0 && DialogNative.IsWindow(_handle)) DialogNative.CloakOwn(_handle, true);
        if (_lease is not null)
        {
            if (!_lease.IsProtected || !DialogLease.BelongsToWindow(_lease.Record)) return;
            DialogNative.Click(_original, 2);
            _handedBack = true;
            var cancel = Stopwatch.StartNew();
            while (OriginalExists && DialogLease.BelongsToWindow(_lease.Record) && cancel.ElapsedMilliseconds < 250)
                await Task.Delay(10);
            DialogIntegrationStore.Log($"{Application}: cancelled before it was fully read; the cancel went to its dialog.");
            return;
        }
        if (!OriginalExists || DialogNative.IsHidden(_original)
            || (_lease is null && DialogNative.GetProp(_original, DialogNative.LeaseProperty) != 0))
            return;
        DialogNative.Click(_original, 2);
        _handedBack = true;
        DialogIntegrationStore.Log($"{Application}: cancelled before it was fully read; the cancel went to its dialog.");
    }

    /// <summary>The keyboard, if this window has it, back to the original (real use only).</summary>
    private void GiveKeyboardBack()
    {
        if (!DialogNative.MayActivate || _handle == 0 || !OriginalExists
            || DialogNative.GetForegroundWindow() != _handle) return;
        DialogNative.SetForegroundWindow(_original);
    }

    /// <summary>
    /// The full read of a dialog shown early: the same contract, or the same
    /// dialog with more to it (multiple selection, a translated label, file
    /// types the Win32 read saw otherwise) - the picker is rebound to the
    /// full request keeping its view and what the user did meanwhile - or,
    /// should its folder differ, bound anew there.
    /// </summary>
    private async Task ConfirmEarlyAsync(FileDialogRequest request)
    {
        var early = _session!;
        if (_snapshot!.Mode != early.Request.Mode || !Models.ViewAllPath.Equals(request.InitialFolder, early.Request.InitialFolder))
        {
            var fresh = new FileDialogSession(request);
            _session = fresh;
            _controls!.SetOptions(_snapshot.ApplicationPath, _snapshot.Mode, _options);
            await _window!.RebindAsync(fresh);
            return;
        }
        if (!NativeDialogRules.SameContract(early.Request, request))
        {
            // What the user did in the meantime stays: the name typed (or the
            // type chosen, which renames it), the folder gone to.
            if (NativeDialogRules.SameFilters(early.Request, request)) request.FileTypeIndex = early.FileTypeIndex;
            if (early.HasInteracted || early.FileTypeIndex != _snapshot.FilterIndex) request.FileName = early.FileNameText;
            var carried = new FileDialogSession(request);
            if (!Models.ViewAllPath.Equals(early.CurrentFolder, carried.CurrentFolder)) carried.CurrentFolder = early.CurrentFolder;
            if (early.HasInteracted) carried.MarkInteraction();
            _session = carried;
            await _window!.RebindAsync(carried, sameDialog: true);
            return;
        }
        if (!early.HasInteracted && !string.Equals(early.FileNameText, request.FileName, StringComparison.Ordinal))
            early.FileNameText = request.FileName;
    }

    /// <summary>
    /// A prepared picker on screen and the contract confirmed: the caller's
    /// options come in, its early lease is reused (or armed here), and the
    /// original remains hidden. OK is enabled and,
    /// in real use, the keyboard is handed over.
    /// </summary>
    private async Task ProtectPreparedAsync()
    {
        SynchronizeOptions(_snapshot!.Options);
        if (_window!.PickerResult.IsCompleted || _fallback) return;
        _lease ??= await DialogLease.ArmAsync(_original, _handle);
        _lastRefresh = Environment.TickCount64;
        StartPulse();
        if (_window.PickerResult.IsCompleted || _fallback) return;
        if (!_lease.IsProtected || !DialogIntegrationStore.Read().Enabled || !DialogLease.BelongsToWindow(_lease.Record)
            || !InFront() || !DialogNative.Hide(_original)) { ReturnToWindows(); return; }
        _hidden = true;
        WatchFileTypes();
        _window.ConfirmContract();
        _readyMs = Since;
        CaptureRootDiagnostics();
        DialogIntegrationStore.Log($"{Application}: replacement presented and protected after {_readyMs:F0} ms.");
        HandKeyboardOver();
        _window.CaptureForTests();
        StallIfAsked();
    }

    /// <summary>Without a prepared picker: the lease first, then a new window, hidden original once it has rendered.</summary>
    private Task ShowBuiltAsync(FileDialogRequest request)
    {
        return ShowAsync();
        async Task ShowAsync()
        {
            _lease = await DialogLease.ArmAsync(_original);
            _lastRefresh = Environment.TickCount64;
            StartPulse();
            _session = new(request);
            _window = new(_session);
            AutomationProperties.SetAutomationId(_window, "UltraExplorerNativeDialogProxy");
            // The native dialog remains modal against this application. We do
            // not disable its owner or own the new window through a cloaked HWND.
            FileDialogHost.AttachOwner(_window, DialogNative.GetWindow(_original, 4));
            // The original keeps the keyboard until this window has rendered
            // and the original is hidden; then OnPresented hands it over in one
            // step. Activating on Show would only make the taskbar flash.
            _window.PrepareAsNativeProxy(PlaceOverOriginal);
            _controls = new NativeOptionsView();
            _controls.SetOptions(_snapshot!.ApplicationPath, _snapshot.Mode, _options);
            _controls.UseWindows += UseWindows;
            _window.AttachNativeDialogControls(_controls);
            SynchronizeOptions(_snapshot.Options);
            WatchFileTypes();
            _window.ContentRendered += OnPresented;
            _app.MainWindow = _window;
            _window.Show();
            _handle = new WindowInteropHelper(_window).Handle;
            _lease.AttachProxy(_handle);
        }
    }

    /// <summary>
    /// After OK or Cancel reached the original: a folder dialog needs its
    /// second, verified confirmation once it has navigated into the typed
    /// folder, and a warning the application raises (a file that exists, a
    /// sharing violation, its own validation) is brought forward. Waits until
    /// the dialog closes, or a few seconds when it stays open for the user.
    /// </summary>
    private async Task FollowUpAsync(FileDialogResult result)
    {
        var folder = result.Accepted && _snapshot!.Mode == FileDialogMode.PickFolder;
        var clock = Stopwatch.StartNew();
        var limit = TimeSpan.FromSeconds(folder ? 6 : 2);
        while (DialogNative.IsWindow(_original) && clock.Elapsed < limit)
        {
            await Task.Delay(20);
            if (!DialogNative.IsWindow(_original)) break;
            if (folder && await _thread.Run(() => _automation!.FinishFolderNavigation(result.Paths[0])).WaitAsync(TimeSpan.FromSeconds(2)))
                folder = false;
            DialogNative.BringOwnedPromptForward(_original);
        }
    }

    /// <summary>Over the original dialog, on its monitor, at a size the canvas
    /// can be used at (<see cref="NativeDialogRules.ProxyBounds"/>).</summary>
    private void PlaceOverOriginal(nint handle)
    {
        if (DialogNative.WindowBounds(_original) is not { } original || DialogNative.MonitorOf(_original) is not { } monitor) return;
        // A test copy's windows never go to the primary monitor, whatever the
        // dialog it replaces did; its own placement already put it elsewhere.
        if (!DialogNative.MayActivate && monitor.Primary) return;
        var bounds = NativeDialogRules.ProxyBounds(original, monitor.Work, monitor.Scale);
        // Twice: a move onto a monitor of another scale makes the window
        // rescale itself to what it was on the old one (WM_DPICHANGED); the
        // second call, on the right monitor already, sets the size meant.
        DialogNative.Place(handle, bounds);
        DialogNative.Place(handle, bounds);
    }

    private void AttachSameProcessOwner(Window window)
    {
        var owner = DialogNative.GetWindow(_original, 4);
        // Chrome's utility hosts its chooser for a browser-process owner.
        // Adopting that busy foreign GUI queue delays the first picker frame.
        // The real chooser retains the modal relationship and our source
        // lifetime/foreground checks remain tied to that chooser.
        if (owner != 0 && DialogNative.ProcessId(owner) == _originalProcess)
            FileDialogHost.AttachOwner(window, owner);
    }

    /// <summary>
    /// The dialog (or something of its own, a tooltip or a list it dropped)
    /// is still what the user is working with. A test copy's fixture never is
    /// the foreground, by design, and is not asked.
    /// </summary>
    private bool InFront() => IsFixture || DialogNative.IsForegroundDialog(_original)
        || (_handle != 0 && DialogNative.GetForegroundWindow() == _handle);

    private void OnPresented(object? sender, EventArgs e)
    {
        if (_window is null || _lease is null || _fallback) return;
        _window.ContentRendered -= OnPresented;
        // The frame just rendered reaches the screen before the original goes.
        DialogNative.WaitForComposition();
        _frameMs = Since;
        if (!_lease.IsProtected || !DialogIntegrationStore.Read().Enabled || !DialogLease.BelongsToWindow(_lease.Record)
            || !InFront() || !DialogNative.Hide(_original)) { ReturnToWindows(); return; }
        _hidden = true;
        _readyMs = Since;
        DialogIntegrationStore.Log($"{Application}: replacement presented and protected after {_readyMs:F0} ms.");
        HandKeyboardOver();
        StallIfAsked();
    }

    /// <summary>
    /// The keyboard moves from the original (now transparent) to this window,
    /// only while the original is still the window in front: after an
    /// Alt+Tab to another program meanwhile nothing is taken from it.
    /// </summary>
    private void HandKeyboardOver()
    {
        if (!DialogNative.MayActivate || _window is null) return;
        _window.ReleaseKeyboard();
        DialogNative.BringForward(_handle, _original);
    }

    private void StallIfAsked()
    {
        if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_DIALOG_TEST_STALL_ON_PRESENT") == "1" && IsFixture)
            Thread.Sleep(15000);
    }

    /// <summary>File-type changes go to the original once the contract is
    /// confirmed; one made before then is passed on now.</summary>
    private void WatchFileTypes()
    {
        if (_filterWatched || _session is null) return;
        _filterWatched = true;
        _session.FilterChanged += OnFilterChanged;
        if (_snapshot is not null && _session.FileTypeIndex != _snapshot.FilterIndex) OnFilterChanged();
    }

    private void StartPulse()
    {
        if (_pulseStarted) return;
        _pulseStarted = true;
        _pulse.Tick += OnPulse;
        _pulse.Start();
    }

    private async void OnPulse(object? sender, EventArgs e)
    {
        if (_recoveryPending) return;
        if (_finishing)
        {
            // OK can still wait for a file type or an option to reach the
            // original, which stays hidden meanwhile: the watchdog goes on
            // hearing from this process until it is back on screen.
            _lease?.Pulse();
            return;
        }
        if (_lease is null)
        {
            // The provisional window covers an untouched original. Even
            // while its provider is busy, emergency off or the caller closing
            // must dismiss that overlay without awaiting the full contract.
            if (!OriginalExists || !DialogIntegrationStore.Read().Enabled || !InFront()) ReturnToWindows();
            else if (_window is not null && !double.IsNaN(_frameMs) && !DialogNative.IsAbove(_handle, _original))
            {
                // The original came up over the picker (the application
                // brought its dialog forward, or a click on its window did):
                // back over it, with the keyboard in real use, as at the uncloak.
                DialogNative.RaiseAbove(_handle, _original);
                HandKeyboardOver();
            }
            return;
        }
        _lease.Pulse();
        var protectedNow = _lease.IsProtected;
        var enabled = DialogIntegrationStore.Read().Enabled;
        if (!protectedNow && !File.Exists(_lease.RecoveryPath) && enabled)
        {
            // The watchdog is gone: nothing would put the original back if
            // this process stopped too. Put it back now and pause the mode.
            _recoveryPending = true;
            _lease.Restore(true);
            try
            {
                await Task.Run(() => DialogIntegrationStore.Update(settings => settings with { Enabled = false,
                    LastRecovery = "The recovery process stopped. Windows dialogs are restored and replacement is paused." }));
            }
            catch (Exception ex) { DialogIntegrationStore.Log("Pausing after recovery process failure", ex); }
            DialogIntegrationStore.Log($"{Application}: the watchdog stopped; returned to its Windows dialog.");
            ReturnToWindows();
            return;
        }
        if (!DialogLease.BelongsToWindow(_lease.Record) || !protectedNow || !enabled)
        { ReturnToWindows(); return; }
        // Clicking the application's window (disabled by its modal dialog)
        // activates that dialog, which is transparent: give the keyboard back
        // to this window instead of letting it type into an invisible one.
        if (_window is not null && _hidden && DialogNative.MayActivate && DialogNative.GetForegroundWindow() == _original
            && DialogNative.IsHidden(_original))
            DialogNative.BringForward(_handle, _original);
        // Minimized, this window has no taskbar button and is not in Alt+Tab,
        // and the original under it is hidden: the application would be left
        // blocked by a dialog nobody can reach. Windows' own cannot be
        // minimized; this one comes straight back.
        if (_window is { WindowState: WindowState.Minimized } && _hidden && !_fallback)
        {
            _window.WindowState = WindowState.Normal;
            HandKeyboardOver();
        }
        if (_snapshot is null || _refreshing || !_pending.IsCompleted
            || Environment.TickCount64 - _lastRefresh < 2000) return;
        _refreshing = true;
        _lastRefresh = Environment.TickCount64;
        var generation = _generation;
        try
        {
            var options = await _thread.Run(() => _automation!.ReadOptions()).WaitAsync(TimeSpan.FromSeconds(8));
            if (!_finishing && generation == _generation) SynchronizeOptions(options);
        }
        catch (Exception ex) { if (ex is TimeoutException) CanReuseWorker = false; DialogIntegrationStore.Log($"{Application}: its options changed in a way that needs its Windows dialog", ex); ReturnToWindows(); }
        finally { _refreshing = false; }
    }

    private void SynchronizeOptions(NativeOptionSnapshot[] snapshots)
    {
        for (var i = 0; i < snapshots.Length; i++)
        {
            var snapshot = snapshots[i];
            if (i < _options.Count && _options[i].Key == snapshot.Key) _options[i].Synchronize(snapshot);
            else
            {
                if (i < _options.Count) _options.RemoveAt(i);
                _options.Insert(i, new(snapshot, OnOptionChanged));
            }
        }
        while (_options.Count > snapshots.Length) _options.RemoveAt(_options.Count - 1);
    }

    private void OnOptionChanged(NativeOptionSnapshot option)
    {
        _generation++;
        _pending = ApplyOptionAsync(option);
    }
    private void OnFilterChanged()
    {
        _generation++;
        var index = _session!.SelectedFilterIndex + 1;
        _pending = ApplyAsync(() => _automation!.SetFilter(index));
    }
    private async Task ApplyAsync(Func<NativeOptionSnapshot[]> apply)
    {
        var generation = _generation;
        try
        {
            var options = await _thread.Run(() => _fallback ? [] : apply()).WaitAsync(TimeSpan.FromSeconds(8));
            if (!_finishing && generation == _generation) SynchronizeOptions(options);
        }
        catch (Exception ex) { if (ex is TimeoutException) CanReuseWorker = false; DialogIntegrationStore.Log($"{Application}: a file type could not be passed on", ex); ReturnToWindows(); }
    }

    private async Task ApplyOptionAsync(NativeOptionSnapshot option)
    {
        try { await _thread.Run(() => { if (!_fallback) _automation!.SetOption(option); }).WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (Exception ex) { if (ex is TimeoutException) CanReuseWorker = false; DialogIntegrationStore.Log($"{Application}: an option could not be passed on", ex); ReturnToWindows(); }
    }

    private async void UseWindows(bool exclude)
    {
        var application = _snapshot?.ApplicationPath ?? DialogNative.AccessibleApplication(_original);
        if (exclude && application is not null)
        {
            try
            {
                await Task.Run(() => DialogIntegrationStore.Update(settings => settings with
                {
                    ExcludedApplications = settings.ExcludedApplications.Append(application)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray()
                }));
            }
            catch (Exception ex) { DialogIntegrationStore.Log("The application exception could not be saved", ex); }
        }
        DialogIntegrationStore.Log($"{Application}: the user chose its Windows dialog{(exclude ? " from now on" : " this time")}.");
        ReturnToWindows();
    }

    private void ReturnToWindows()
    {
        if (_fallback) return;
        _fallback = true;
        GiveKeyboardBack();
        _lease?.Restore(true);
        if (_isPrepared && _handle != 0 && DialogNative.IsWindow(_handle)) DialogNative.CloakOwn(_handle, true);
        _window?.CloseFromCaller();
    }
}
