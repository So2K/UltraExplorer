using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using UltraExplorer;
using UltraExplorer.Controls;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task PreviewOwnerActivationChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(PreviewOwnerActivationChecks), StringComparison.OrdinalIgnoreCase))
        { RunGroupInOwnProcess(nameof(PreviewOwnerActivationChecks)); return Task.CompletedTask; }
        if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1"
            || !DialogFixtureProcessScope.IsIsolatedDirectory(AppPaths.StateDirectory))
            throw new InvalidOperationException("Preview owner checks require isolated state and test-window mode.");
        RunOnSta("modeless preview owner activation", PreviewOwnerActivationOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task PreviewOwnerActivationOnStaAsync()
    {
        Section("Quick Look close: owned HWND state, native teardown and scoped activation");
        if (Application.Current is null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("/UltraExplorer;component/Themes/UltraTheme.xaml", UriKind.Relative) });
        }
        var app = Application.Current!;
        var priorMain = app.MainWindow;
        // Thread-local CBT only: even DPI initialization of a cloaked HWND
        // cannot activate a fixture window on the user's desktop.
        using var activationGuard = ActivationGuard.GuardWindowsCreated();
        var root = Path.Combine(AppPaths.StateDirectory, "preview-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var image = Path.Combine(root, "picture.png");
        HoverSaveImage(HoverImage(320, 180, Colors.Coral), image);
        var audio = Path.Combine(root, "audio.wav"); PreviewToolsWriteWave(audio);
        var model = Path.Combine(root, "model.obj");
        await File.WriteAllTextAsync(model, "v 0 0 0\nv 1 0 0\nv 0 1 0\nv 0 0 1\nf 1 3 2\nf 1 2 4\nf 1 4 3\nf 2 3 4\n");
        var note = Path.Combine(root, "note.txt"); await File.WriteAllTextAsync(note, "initial note\r\n");
        var owners = new List<Window>();
        var previews = new List<QuickPreviewWindow>();
        Window MakeOwner(WindowState state = WindowState.Normal)
        {
            var owner = new Window { Content = new TextBlock { Text = "Isolated preview owner" }, Width = 600, Height = 400,
                ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -30000, Top = -30000 };
            owner.SourceInitialized += (_, _) => DialogNative.CloakOwn(new WindowInteropHelper(owner).Handle, true);
            owners.Add(owner); owner.Show();
            // WPF forbids initially showing Maximized + ShowActivated=false.
            // Show the cloaked normal HWND first, then maximize that HWND;
            // the thread-local CBT guard still rejects any activation attempt.
            owner.WindowState = state;
            return owner;
        }
        QuickPreviewWindow MakePreview(Window owner)
        {
            var preview = new QuickPreviewWindow { Owner = owner, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000 };
            preview.SourceInitialized += (_, _) => DialogNative.CloakOwn(new WindowInteropHelper(preview).Handle, true);
            previews.Add(preview);
            return preview;
        }
        try
        {
            Check("actual pinned native engines are available for owner-close fixtures", PreviewTools.FindMpv() is not null && PreviewTools.FindF3d() is not null);
            foreach (var state in new[] { WindowState.Normal, WindowState.Maximized })
            foreach (var path in new[] { image, audio, model })
            {
                var owner = MakeOwner(state);
                var preview = MakePreview(owner);
                var ownerHandle = new WindowInteropHelper(owner).Handle;
                var returned = 0;
                nint foreground = 0;
                PreviewOwnerActivation.Attach(preview, owner, () => false, () => foreground, () => returned++);
                var loadingClock = Stopwatch.StartNew();
                preview.OpenFile(path); preview.Show(); await preview.Loading.WaitAsync(TimeSpan.FromSeconds(25));
                // Loading assigns the decoded image and adds its ScrollViewer.
                // A cloaked HWND can still have that viewer's template pending;
                // complete real layout before inspecting its visual descendants.
                preview.UpdateLayout();
                if (path == image)
                {
                    var picture = (ScrollViewer)typeof(QuickPreviewWindow)
                        .GetField("_picture", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview)!;
                    picture.ApplyTemplate();
                    for (var layoutPass = 0; layoutPass < 2; layoutPass++)
                    {
                        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        preview.UpdateLayout();
                    }
                }
                var native = QuickDescendants((DependencyObject)preview.Content).OfType<NativePreviewHost>().SingleOrDefault();
                var nativePid = native?.OwnedProcessId;
                var bodyForDiagnostic = (Grid)(typeof(QuickPreviewWindow).GetField("_body", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException("_body")).GetValue(preview)!;
                var expectsNative = path != image;
                var actualRenderer = expectsNative
                    ? native is not null && nativePid is not null && native.LastError is null && native.ViewportHandle != 0
                        && (path == model ? native.ModelWindowHandle != 0 : native.IsMediaLoaded)
                    : QuickDescendants((DependencyObject)preview.Content).OfType<Image>()
                        .Any(picture => picture.Source is System.Windows.Media.Imaging.BitmapSource { PixelWidth: 320, PixelHeight: 180 });
                actualRenderer &= !bodyForDiagnostic.Children.OfType<TextBlock>().Any();
                if (!actualRenderer)
                {
                    var retainedNative = (NativePreviewHost?)typeof(QuickPreviewWindow)
                        .GetField("_native", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview);
                    var status = (TextBlock)typeof(QuickPreviewWindow)
                        .GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview)!;
                    var privateImage = (Image)typeof(QuickPreviewWindow)
                        .GetField("_image", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview)!;
                    var bitmap = privateImage.Source as System.Windows.Media.Imaging.BitmapSource;
                    Console.WriteLine($"Owner renderer diagnostic: state={state}; kind={Path.GetExtension(path)}; elapsedMs={loadingClock.ElapsedMilliseconds}; "
                        + $"nativeInBody={native is not null}; pid={retainedNative?.OwnedProcessId}; viewport={retainedNative?.ViewportHandle}; "
                        + $"modelHwnd={retainedNative?.ModelWindowHandle}; error={retainedNative?.LastError}; attach={retainedNative?.LastNativeDiagnostic}; "
                        + $"status={status.Text}; body={string.Join(',', bodyForDiagnostic.Children.Cast<UIElement>().Select(child => child.GetType().Name))}; "
                        + $"privateBitmap={bitmap?.PixelWidth}x{bitmap?.PixelHeight}; frozen={bitmap?.IsFrozen}; "
                        + $"imageVisualCount={QuickDescendants((DependencyObject)preview.Content).OfType<Image>().Count()}");
                }
                foreground = native is null ? new WindowInteropHelper(preview).Handle
                    : native.ModelWindowHandle != 0 ? native.ModelWindowHandle : native.ViewportHandle;
                Check($"{state} {Path.GetExtension(path)} fixture uses real owned HWNDs and the actual preview renderer",
                    foreground != 0 && PreviewOwnerGetAncestor(foreground, 2) == new WindowInteropHelper(preview).Handle
                    && PreviewOwnerIsZoomed(ownerHandle) == (state == WindowState.Maximized)
                    && actualRenderer);
                if (!actualRenderer)
                    throw new InvalidOperationException($"The {state} {Path.GetExtension(path)} owner fixture did not load its required real renderer; error previews cannot qualify owner/native lifecycle checks.");
                if (state == WindowState.Normal && path == audio && native is not null)
                {
                    var inputSource = HwndSource.FromHwnd(new WindowInteropHelper(preview).Handle)
                        ?? throw new InvalidOperationException("The real audio preview did not expose its HWND input source.");
                    void KeyDiagnostic(string phase, KeyEventArgs? key = null, System.Text.Json.JsonElement? pause = null)
                    {
                        var closedFlag = typeof(QuickPreviewWindow).GetField("_closed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview);
                        Console.WriteLine($"Owner audio key diagnostic: phase={phase}; modifiers={Keyboard.Modifiers}; "
                            + $"handled={key?.Handled}; repeat={key?.IsRepeat}; source={key?.Source?.GetType().Name}; originalSource={key?.OriginalSource?.GetType().Name}; "
                            + $"previewVisible={preview.IsVisible}; closed={closedFlag}; returned={returned}; "
                            + $"mediaLoaded={native.IsMediaLoaded}; controlsEnabled={native.PlaybackControls.IsEnabled}; pid={native.OwnedProcessId}; "
                            + $"startedPid={native.LastStartedProcessId}; observer={native.IsMediaObserverRunning}; pause={pause?.GetRawText()}; "
                            + $"error={native.LastError}; diagnostic={native.LastNativeDiagnostic}");
                    }
                    KeyEventArgs SpaceFrom(DependencyObject source, string phase, bool repeated = false)
                    {
                        var args = new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, Environment.TickCount, Key.Space)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = source };
                        if (repeated)
                            (typeof(KeyEventArgs).GetMethod("SetRepeat", BindingFlags.Instance | BindingFlags.NonPublic)
                                ?? throw new MissingMethodException("WPF KeyEventArgs.SetRepeat")).Invoke(args, [true]);
                        KeyDiagnostic(phase + " before dispatch", args);
                        preview.RaiseEvent(args);
                        KeyDiagnostic(phase + " after dispatch", args);
                        return args;
                    }
                    async Task<bool> Paused(string phase)
                    {
                        var response = await native.MpvCommandAsync(["get_property", "pause"]);
                        KeyDiagnostic(phase, pause: response);
                        if (response is not { } reply || !reply.TryGetProperty("data", out var value)
                            || value.ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
                            throw new IOException($"The owned audio engine did not return an actual pause state at {phase}.");
                        return value.ValueKind == System.Text.Json.JsonValueKind.True;
                    }
                    await native.MpvCommandAsync(["set_property", "mute", true]);
                    var mute = await native.MpvCommandAsync(["get_property", "mute"]);
                    Check("audio shortcut fixtures mute the actual owned engine before any playback",
                        mute is { } muted && muted.GetProperty("data").ValueKind == System.Text.Json.JsonValueKind.True && await Paused("initial before any key"));
                    var toolbar = new[] { "_open", "_openWith", "_delete" }
                        .Select(name => (Button)(typeof(QuickPreviewWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? throw new MissingFieldException(name)).GetValue(preview)!).ToArray();
                    foreach (var button in toolbar)
                    {
                        var phase = "toolbar " + button.ToolTip;
                        var pausedBefore = await Paused(phase + " before key");
                        var key = SpaceFrom(button, phase);
                        var pausedAfter = await Paused(phase + " after key");
                        Check($"Space on the audio toolbar {button.ToolTip} remains its button's own key",
                            pausedBefore && pausedAfter && !key.Handled && returned == 0 && preview.IsVisible);
                    }
                    var playbackSources = new DependencyObject[] { native.TimelineControl, native.VolumeControl,
                        native.SpeedControl, (ComboBoxItem)native.SpeedControl.Items[0], native.PlayPauseControl, native.MuteControl };
                    foreach (var source in playbackSources)
                    {
                        var phase = "owned control " + source.GetType().Name;
                        var pausedBefore = await Paused(phase + " before key");
                        var key = SpaceFrom(source, phase);
                        var pausedAfter = await Paused(phase + " after key");
                        Check($"Space on an owned {source.GetType().Name} playback control remains the control's key",
                            pausedBefore && pausedAfter && native.OwnsPlaybackControl(source) && !key.Handled && returned == 0 && preview.IsVisible);
                    }
                    var body = (Grid)(typeof(QuickPreviewWindow).GetField("_body", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new MissingFieldException("_body")).GetValue(preview)!;
                    _ = await Paused("body before first key");
                    var bodySpace = SpaceFrom(body, "body first Space");
                    var playing = false;
                    for (var attempt = 0; attempt < 40 && !playing; attempt++)
                    { playing = !await Paused("body after first key " + attempt); if (!playing) await Task.Delay(25); }
                    Check("Space on the actual audio preview body toggles its owned player instead of closing",
                        bodySpace.Handled && playing && returned == 0 && preview.IsVisible && native.IsMediaLoaded);
                    _ = await Paused("body before repeated key");
                    var repeatedSpace = SpaceFrom(body, "body repeated Space", repeated: true);
                    await Task.Delay(150);
                    var repeatedPaused = await Paused("body after repeated key");
                    Check("holding Space consumes repeats without repeatedly toggling playback",
                        repeatedSpace.IsRepeat && repeatedSpace.Handled && !repeatedPaused && returned == 0 && preview.IsVisible);
                    await native.StopPlaybackAsync();
                }
                // The same WM_CLOSE delivered by the window caption's X.
                PreviewOwnerSendMessage(new WindowInteropHelper(preview).Handle, 0x0010, 0, 0);
                Check($"closing {Path.GetExtension(path)} returns to its {state} owner before HWND teardown",
                    returned == 1 && owner.IsVisible && owner.WindowState == state && PreviewOwnerIsWindowVisible(ownerHandle)
                    && !PreviewOwnerIsIconic(ownerHandle) && PreviewOwnerIsZoomed(ownerHandle) == (state == WindowState.Maximized)
                    && !app.Windows.OfType<QuickPreviewWindow>().Contains(preview));
                if (nativePid is { } pid)
                    Check($"closing {Path.GetExtension(path)} disposes only its preview engine", await PreviewToolsWaitForExit(pid) && native!.OwnedProcessId is null);
                owner.Close();
            }

            var policyOwner = MakeOwner();
            var defaultGuard = MakePreview(policyOwner);
            PreviewOwnerActivation.Attach(defaultGuard, policyOwner, () => false);
            defaultGuard.OpenFile(image); defaultGuard.Show(); await defaultGuard.Loading;
            defaultGuard.Close();
            Check("the production foreground reader leaves an offscreen inactive owner's HWND visible and normal",
                policyOwner.IsVisible && policyOwner.WindowState == WindowState.Normal
                && !PreviewOwnerIsIconic(new WindowInteropHelper(policyOwner).Handle));
            var otherWindow = MakeOwner();
            var otherHandle = new WindowInteropHelper(otherWindow).Handle;
            var external = MakePreview(policyOwner);
            var externalActivations = 0;
            PreviewOwnerActivation.Attach(external, policyOwner, () => false, () => otherHandle, () => externalActivations++);
            external.OpenFile(image); external.Show(); await external.Loading;
            external.Close();
            Check("closing an inactive preview after switching windows does not take focus", externalActivations == 0 && policyOwner.IsVisible && policyOwner.WindowState == WindowState.Normal);

            var cancelled = MakePreview(policyOwner);
            var cancel = true;
            cancelled.Closing += (_, args) => args.Cancel = cancel;
            var cancelActivations = 0;
            PreviewOwnerActivation.Attach(cancelled, policyOwner, () => false,
                () => new WindowInteropHelper(cancelled).Handle, () => cancelActivations++);
            cancelled.OpenFile(image); cancelled.Show(); await cancelled.Loading;
            cancelled.Close();
            Check("a declined preview close leaves activation and both windows unchanged", cancelActivations == 0 && cancelled.IsVisible && policyOwner.WindowState == WindowState.Normal);
            cancel = false; cancelled.Close();
            Check("the accepted retry returns to the owner exactly once", cancelActivations == 1 && !cancelled.IsVisible);

            foreach (var unavailable in new[] { "minimized", "hidden", "disabled", "closing" })
            {
                var owner = MakeOwner();
                var preview = MakePreview(owner);
                var activations = 0;
                PreviewOwnerActivation.Attach(preview, owner, () => unavailable == "closing",
                    () => new WindowInteropHelper(preview).Handle, () => activations++);
                preview.OpenFile(image); preview.Show(); await preview.Loading;
                if (unavailable == "minimized") owner.WindowState = WindowState.Minimized;
                if (unavailable == "hidden") owner.Hide();
                if (unavailable == "disabled") owner.IsEnabled = false;
                preview.Close();
                Check($"closing a preview does not restore or activate a {unavailable} owner", activations == 0
                    && (unavailable != "minimized" || owner.WindowState == WindowState.Minimized)
                    && (unavailable != "hidden" || !owner.IsVisible));
                owner.Close();
            }

            var noteOwner = MakeOwner();
            var notePreview = MakePreview(noteOwner);
            var noteReturns = 0;
            PreviewOwnerActivation.Attach(notePreview, noteOwner, () => false,
                () => new WindowInteropHelper(notePreview).Handle, () => noteReturns++);
            notePreview.OpenFile(note); notePreview.Show(); await notePreview.Loading;
            var editor = QuickDescendants((DependencyObject)notePreview.Content).OfType<TextEditor>().Single();
            editor.Text += "saved while closing\r\n";
            var expected = editor.Text;
            notePreview.Close();
            Check("a dirty note's asynchronous save decline does not prematurely activate its owner", noteReturns == 0 && notePreview.IsVisible);
            Check("after the actual note save completes, close returns once and preserves the note and owner",
                await HoverWait(() => !notePreview.IsVisible, 5000) && noteReturns == 1 && File.ReadAllText(note) == expected
                && noteOwner.IsVisible && noteOwner.WindowState == WindowState.Normal);
            noteOwner.Close();

            var ownerClosing = MakeOwner();
            var ownedPreview = MakePreview(ownerClosing);
            var closingReturns = 0;
            PreviewOwnerActivation.Attach(ownedPreview, ownerClosing, () => true,
                () => new WindowInteropHelper(ownedPreview).Handle, () => closingReturns++);
            ownedPreview.OpenFile(image); ownedPreview.Show(); await ownedPreview.Loading;
            ownerClosing.Close();
            Check("real owner HWND shutdown closes its modeless preview without reactivation", closingReturns == 0
                && !ownerClosing.IsVisible && !app.Windows.OfType<QuickPreviewWindow>().Contains(ownedPreview));
            Check("offscreen fixture windows did not become the desktop foreground", !owners.Concat<Window>(previews)
                .Select(window => new WindowInteropHelper(window).Handle).Where(handle => handle != 0)
                .Contains(PreviewOwnerGetAncestor(PreviewOwnerGetForegroundWindow(), 2)));
        }
        finally
        {
            foreach (var preview in previews.Where(window => app.Windows.OfType<QuickPreviewWindow>().Contains(window)))
            { preview.EndOwnerClose(); await preview.CommitForCloseAsync(); preview.Close(); }
            foreach (var owner in owners.Where(window => app.Windows.OfType<Window>().Contains(window))) owner.Close();
            app.MainWindow = priorMain;
            TryDelete(root);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetAncestor")] private static extern nint PreviewOwnerGetAncestor(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern nint PreviewOwnerGetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint PreviewOwnerSendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")] private static extern bool PreviewOwnerIsWindowVisible(nint window);
    [DllImport("user32.dll", EntryPoint = "IsIconic")] private static extern bool PreviewOwnerIsIconic(nint window);
    [DllImport("user32.dll", EntryPoint = "IsZoomed")] private static extern bool PreviewOwnerIsZoomed(nint window);
}
