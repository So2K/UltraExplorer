using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace UltraExplorer.Picker.Integration;

/// <summary>
/// The one picker window a resident dialog worker keeps ready before any
/// dialog needs it: made, shown and DWM-cloaked, so its surface, renderer,
/// GPU and first layout already exist; never activated, out of the taskbar,
/// bound to nothing (a placeholder session in the application's own folder,
/// <see cref="PlaceholderFolder"/>), on the monitor the last dialog was on - or the
/// primary one, and a test copy's own monitor, which is never the primary.
/// A dialog takes it (<see cref="Take"/>), binds it
/// (<see cref="MainWindow.RebindAsync"/>) and uncloaks it over the original:
/// on screen 1-2 ms after the uncloak, with the finished picture as its first
/// frame. After the dialog the used window is closed and the next one made
/// once the worker is idle.
/// </summary>
internal sealed class PreparedPicker : IDisposable
{
    private readonly Application _app;
    private MainWindow? _window;
    private NativeOptionsView? _controls;
    private nint _handle;
    private bool _preparing, _disposed;
    private NativeRect? _lastDialog;

    public PreparedPicker(Application app) => _app = app;

    private static KnownFolderNames? _names;

    /// <summary>Known folders by the names dialogs show for them; built once per worker, in the background.</summary>
    internal static KnownFolderNames? Names
    {
        get => Volatile.Read(ref _names);
        set => Volatile.Write(ref _names, value);
    }

    public bool IsReady => _window is not null && DialogNative.IsWindow(_handle);

    /// <summary>
    /// The folder a prepared window shows until a dialog binds it: the
    /// application's own, a handful of files that never change. It only warms
    /// the window up (its tiles, icons and labels drawn once); a dialog's
    /// folder replaces it before the window is ever on screen. Neither the
    /// profile nor the last dialog's folder: a hidden window reads and draws
    /// again whatever changes in the folder it shows, and in the folders
    /// nested in it, for as long as it waits.
    /// </summary>
    internal static string PlaceholderFolder => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary>
    /// Makes the window and shows it cloaked; it is ready from then on (a
    /// dialog's folder replaces the placeholder anyway). Then waits, at most
    /// <paramref name="settle"/>, for the placeholder to be drawn, so the
    /// worker has settled before it says it is ready.
    /// </summary>
    public async Task PrepareAsync(TimeSpan settle)
    {
        if (_disposed || _preparing || IsReady) return;
        _preparing = true;
        MainWindow? window = null;
        try
        {
            var request = new FileDialogRequest
            {
                IsNativeProxy = true, InitialFolder = PlaceholderFolder,
                Options = FileDialogOptions.NoTestFileCreate | FileDialogOptions.DontAddToRecent
            };
            // A file type, as nearly every dialog has: its row is made and
            // laid out now rather than for the first dialog.
            request.Filters.Add(new FileDialogFilterSpec("All files (*.*)", "*.*"));
            window = new MainWindow(new FileDialogSession(request)) { ShowActivated = false };
            AutomationProperties.SetAutomationId(window, "UltraExplorerNativeDialogProxy");
            var controls = new NativeOptionsView();
            window.AttachNativeDialogControls(controls);
            nint handle = 0;
            window.PrepareAsCloakedPicker(created => { handle = created; PlaceWhereDialogsOpen(created); });
            var made = window;
            made.Closed += (_, _) =>
            {
                if (!ReferenceEquals(_window, made)) return;
                (_window, _controls, _handle) = (null, null, 0);
            };
            // Guarded from creation: WPF's first placement may ask for activation.
            using (ActivationGuard.GuardWindowsCreated()) window.Show();
            if (!DialogNative.IsCloaked(handle))
            {
                // Never on screen unbidden: a window Windows would not cloak is not kept.
                DialogIntegrationStore.Log("A prepared picker could not be cloaked; dialogs build their own window.");
                window.CloseFromCaller();
                return;
            }
            if (_disposed || !DialogNative.IsWindow(handle)) { window.CloseFromCaller(); return; }
            // Shown, cloaked, rendered once: ready. The placeholder's drawing
            // goes on; a dialog that comes meanwhile replaces it.
            (_window, _controls, _handle) = (window, controls, handle);
            _preparing = false;
            WarmFirstDialog(controls);
            if (!await window.WhenFolderDrawnAsync(settle) && ReferenceEquals(_window, window))
                DialogIntegrationStore.Log("A prepared picker's first folder was slow to draw; it is kept all the same.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DialogIntegrationStore.Log("A prepared picker could not be made; dialogs build their own window", ex);
            try { window?.CloseFromCaller(); } catch (InvalidOperationException) { }
        }
        finally { _preparing = false; }
    }

    private static bool _warmed;

    /// <summary>
    /// What a prepared window does not do while it waits, but every dialog
    /// then does, done once per worker ahead of the first dialog: naming the
    /// caller in the footer (its file's version resource), and watching a
    /// folder on disk - a change hub of its own, made, watching the
    /// placeholder for a moment and gone. The first dialog's picker would
    /// otherwise spend some ten milliseconds on it before its first frame.
    /// </summary>
    private static void WarmFirstDialog(NativeOptionsView controls)
    {
        if (_warmed) return;
        _warmed = true;
        try
        {
            controls.SetOptions(Environment.ProcessPath ?? PlaceholderFolder, FileDialogMode.Open, []);
            using var hub = new Services.Watch.ChangeHub(TimeProvider.System);
            var folder = new object();
            _ = hub.RootFor(PlaceholderFolder);
            hub.Register(Services.Watch.ChangeConsumer.Nested, PlaceholderFolder, folder);
            hub.Unregister(Services.Watch.ChangeConsumer.Nested, PlaceholderFolder, folder);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { DialogIntegrationStore.Log("A warm-up for the first dialog was skipped", ex); }
    }

    /// <summary>The prepared window, now the caller's to bind, show and close; null when none is ready.</summary>
    public (MainWindow Window, NativeOptionsView Controls, nint Handle)? Take()
    {
        if (!IsReady) return null;
        var taken = (_window!, _controls!, _handle);
        (_window, _controls, _handle) = (null, null, 0);
        return taken;
    }

    /// <summary>Where the dialog just served was: the next window waits on its monitor.</summary>
    public void Remember(NativeRect? dialog)
    {
        if (dialog is not null) _lastDialog = dialog;
    }

    /// <summary>The next window, once the worker has nothing else to do.</summary>
    public void PrepareWhenIdle()
    {
        if (_disposed || IsReady) return;
        _ = _app.Dispatcher.InvokeAsync(() => PrepareAsync(TimeSpan.FromSeconds(4)), DispatcherPriority.ApplicationIdle).Task.Unwrap();
    }

    private void PlaceWhereDialogsOpen(nint handle)
    {
        // A test copy already stands on its own monitor (never the primary
        // one) and only moves to where a dialog of its fixture was.
        var monitor = _lastDialog is not null ? DialogNative.MonitorAt(_lastDialog)
            : DialogNative.MayActivate ? DialogNative.MonitorAt(null) : DialogNative.MonitorOf(handle);
        if (monitor is not { } target || (!DialogNative.MayActivate && target.Primary)) return;
        var bounds = NativeDialogRules.ProxyBounds(_lastDialog ?? default, target.Work, target.Scale);
        DialogNative.Place(handle, bounds);
        DialogNative.Place(handle, bounds);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var window = _window;
        (_window, _controls, _handle) = (null, null, 0);
        try { window?.CloseFromCaller(); } catch (InvalidOperationException) { }
    }
}
