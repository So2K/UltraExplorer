using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using UltraExplorer;
using UltraExplorer.Controls;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// The window-side fixes of the 2026-10-09 review: a cancelled shutdown
/// (WM_ENDSESSION FALSE) gives the windows back, Quick Look keeps a clean
/// text's editor across activations, a drive being removed closes the media
/// preview on it, WM_DEVICECHANGE reads no bogus pointer, the video scrub
/// ends safely on a disposed player, the node drag handle disarms, and the
/// archive beside a drive root keeps its name.
/// </summary>
internal static partial class Program
{
    private static Task Review1009UiChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(Review1009UiChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(Review1009UiChecks));
            return Task.CompletedTask;
        }
        if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable))
            || !DialogFixtureProcessScope.IsIsolatedDirectory(AppPaths.StateDirectory))
            throw new InvalidOperationException("Review 2026-10-09 UI checks require isolated state and test-window mode.");
        Review1009PureChecks();
        RunOnSta("review 2026-10-09: window fixes", Review1009UiOnStaAsync);
        return Task.CompletedTask;
    }

    private static void Review1009PureChecks()
    {
        Section("review 2026-10-09: archive beside a drive root, device message pointers");
        var outermost = typeof(NestedPane).GetMethod("OutermostArchive", BindingFlags.Static | BindingFlags.NonPublic)!;
        string? Outermost(string path, string folder) => (string?)outermost.Invoke(null, [path, folder]);
        Check("the archive in a drive's root keeps its first letter (D:\\backup.zip, not D:\\ackup.zip)",
            Outermost(@"D:\backup.zip\inner\a.txt", @"D:\") == @"D:\backup.zip" && Outermost(@"D:\backup.zip", @"D:\") == @"D:\backup.zip");
        Check("and one in a folder is found as before",
            Outermost(@"D:\x\b.zip\in\c.txt", @"D:\x") == @"D:\x\b.zip" && Outermost(@"D:\x", @"D:\x") is null);

        using var hub = new ChangeHub(TimeProvider.System);
        using var notifications = new VolumeNotifications(hub, Dispatcher.CurrentDispatcher);
        Check("a WM_DEVICECHANGE whose lParam points nowhere in this process is ignored, not read",
            notifications.OnDeviceChange(0x8001, (IntPtr)0x10) == DeviceChange.None
            && notifications.OnDeviceChange(0x8000, new IntPtr(0x7FFF_FFFF_0000)) == DeviceChange.None);
        var shortHandle = Marshal.AllocHGlobal(12);
        try
        {
            Marshal.WriteInt32(shortHandle, 0, 12);
            Marshal.WriteInt32(shortHandle, 4, 6);
            Marshal.WriteInt32(shortHandle, 8, 0);
            Check("a handle message claiming a size too short for its notification is not read past it",
                notifications.OnDeviceChange(0x8001, shortHandle) == DeviceChange.None);
        }
        finally
        {
            Marshal.FreeHGlobal(shortHandle);
        }
    }

    private static async Task Review1009UiOnStaAsync()
    {
        if (Application.Current is null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        }
        var root = Path.Combine(AppPaths.StateDirectory, "review1009-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Review1009QuickLookChecksAsync(root);
            Review1009ControlChecks();
            await Review1009SessionEndChecksAsync(root);
        }
        finally
        {
            await SettingsSettle();
            TryDelete(root);
        }
    }

    private static async Task Review1009QuickLookChecksAsync(string root)
    {
        Section("review 2026-10-09: Quick Look keeps a clean text across activations");
        var path = Path.Combine(root, "note.txt");
        var text = string.Concat(Enumerable.Range(0, 200).Select(line => $"line {line}\r\n"));
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
        var window = new QuickPreviewWindow();
        try
        {
            window.OpenFile(path);
            await window.Loading;
            var editor = QuickDescendants((DependencyObject)window.Content).OfType<TextEditor>().Single();
            var generation = OwnerCloseField(typeof(QuickPreviewWindow), "_generation");
            var before = (int)generation.GetValue(window)!;
            editor.CaretOffset = 42;
            await window.RefreshTextAfterActivationAsync();
            Check("activation over an unchanged file keeps the editor: nothing read again, the caret where it was",
                (int)generation.GetValue(window)! == before && editor.CaretOffset == 42 && editor.Text == text && !editor.IsReadOnly);

            editor.Text = text + "typed\r\n";
            Check("a save of the editor's own is committed", await window.CommitForCloseAsync() && File.ReadAllText(path) == text + "typed\r\n");
            var afterSave = (int)generation.GetValue(window)!;
            editor.CaretOffset = 50;
            await window.RefreshTextAfterActivationAsync();
            Check("its own save does not count as a change from outside", (int)generation.GetValue(window)! == afterSave && editor.CaretOffset == 50);

            await File.WriteAllTextAsync(path, text + "typed\r\nexternal editor\r\n", new UTF8Encoding(false));
            await window.RefreshTextAfterActivationAsync();
            Check("a file changed outside is read again, with the caret kept",
                (int)generation.GetValue(window)! != afterSave && editor.Text == text + "typed\r\nexternal editor\r\n" && editor.CaretOffset == 50);

            Section("review 2026-10-09: a drive being removed closes the media preview on it");
            var native = new NativePreviewHost();
            OwnerCloseField(typeof(QuickPreviewWindow), "_native").SetValue(window, native);
            OwnerCloseField(typeof(QuickPreviewWindow), "_path").SetValue(window, @"Q:\clips\video.mp4");
            Check("a removal of another drive leaves the player alone", !window.ReleaseMediaOnVolume(@"R:\")
                && ReferenceEquals(OwnerCloseField(typeof(QuickPreviewWindow), "_native").GetValue(window), native));
            Check("the removal of its own drive closes it",
                window.ReleaseMediaOnVolume(@"Q:\") && OwnerCloseField(typeof(QuickPreviewWindow), "_native").GetValue(window) is null
                && (bool)OwnerCloseField(typeof(NativePreviewHost), "_disposed").GetValue(native)!);
        }
        finally
        {
            window.Close();
        }
    }

    private static void Review1009ControlChecks()
    {
        Section("review 2026-10-09: video scrub on a disposed player, the node drag handle");
        var host = new NativePreviewHost();
        var timeline = (Slider)OwnerCloseField(typeof(NativePreviewHost), "_timeline").GetValue(host)!;
        OwnerCloseField(typeof(NativePreviewHost), "_pipeName").SetValue(host, "UltraExplorerPreview-review1009");
        timeline.RaiseEvent(new DragStartedEventArgs(0, 0));
        var scrubbing = OwnerCloseField(typeof(NativePreviewHost), "_scrubbing");
        var started = (bool)scrubbing.GetValue(host)!;
        host.Dispose();
        Exception? failure = null;
        try
        {
            timeline.RaiseEvent(new DragCompletedEventArgs(0, 0, false));
            timeline.RaiseEvent(new DragStartedEventArgs(0, 0));
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        Check($"a scrub ended after the player was disposed throws nothing ({failure?.GetType().Name ?? "none"}), and leaves no scrub",
            started && failure is null && !(bool)scrubbing.GetValue(host)!);

        var node = new ViewAllNodeView();
        var armed = OwnerCloseField(typeof(ViewAllNodeView), "_dragArmed");
        armed.SetValue(node, true);
        node.DragHandle.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });
        Check("the drag handle losing the mouse lets go of the armed drag", !node.IsDragArmedForChecks);
        armed.SetValue(node, true);
        node.DragHandle.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
        Check("and so does the button let go over it", !node.IsDragArmedForChecks);
    }

    private static async Task Review1009SessionEndChecksAsync(string root)
    {
        Section("review 2026-10-09: a shutdown cancelled after the windows agreed gives them back");
        var app = Application.Current!;
        var priorMain = app.MainWindow;
        var renderer = GpuBootstrap.Preference;
        var path = Path.Combine(root, "session.txt");
        await File.WriteAllTextAsync(path, "session baseline\r\n");
        var owner = new MainWindow(null, Path.Combine(root, "session-window")) { SuppressQuickPreviewPresentationForChecks = true };
        var closed = false;
        owner.Closed += (_, _) => closed = true;
        var model = (MainViewModel)owner.DataContext;
        try
        {
            await new WorkspaceStore().SaveAsync(new WorkspaceState { CanvasRenderer = "Cpu" });
            await model.InitializeAsync(root);
            await owner.StartNestedForChecksAsync();
            await model.SaveNowAsync();
            var hwnd = new WindowInteropHelper(owner).EnsureHandle();
            owner.OpenQuickPreview(path);
            var preview = (QuickPreviewWindow)OwnerCloseField(typeof(MainWindow), "_quickPreview").GetValue(owner)!;
            await preview.Loading.WaitAsync(TimeSpan.FromSeconds(10));

            var renameTimer = (DispatcherTimer)OwnerCloseField(typeof(MainWindow), "_folderListRenameTimer").GetValue(owner)!;
            renameTimer.Start();
            var args = (SessionEndingCancelEventArgs)Activator.CreateInstance(typeof(SessionEndingCancelEventArgs),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [ReasonSessionEnding.Shutdown], null)!;
            typeof(MainWindow).GetMethod("OnSessionEnding", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, [null, args]);
            var transaction = OwnerCloseField(typeof(MainWindow), "_sessionCloseTransaction").GetValue(owner);
            Check("the window agrees to the shutdown, saved, its preview frozen and its rename timer stopped",
                !args.Cancel && (bool)OwnerCloseField(typeof(MainWindow), "_allowClose").GetValue(owner)!
                && (bool)OwnerCloseField(typeof(MainWindow), "_closeRequested").GetValue(owner)!
                && preview.IsOwnerCloseFrozen && transaction is not null && !renameTimer.IsEnabled);

            var watch = (DispatcherTimer)transaction!.GetType().GetField("_watch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transaction)!;
            object[] message = [hwnd, 0x0016, IntPtr.Zero, IntPtr.Zero, false];
            typeof(MainWindow).GetMethod("HandleWindowMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, message);
            Check("WM_ENDSESSION FALSE: the window and its preview work again, and the transaction's watch stops",
                !(bool)OwnerCloseField(typeof(MainWindow), "_allowClose").GetValue(owner)!
                && !(bool)OwnerCloseField(typeof(MainWindow), "_closeRequested").GetValue(owner)!
                && !preview.IsOwnerCloseFrozen && preview.IsEnabled
                && OwnerCloseField(typeof(MainWindow), "_sessionCloseTransaction").GetValue(owner) is null
                && !watch.IsEnabled && !args.Cancel);

            var editor = QuickDescendants((DependencyObject)preview.Content).OfType<TextEditor>().Single();
            editor.Text = "after the cancelled shutdown\r\n";
            owner.Close();
            Check("and the next close saves the note before the window goes, as any close does",
                await HoverWait(() => closed, 5000) && File.ReadAllText(path) == "after the cancelled shutdown\r\n");
        }
        finally
        {
            if (!closed)
            {
                ReleasePreview(owner);
                owner.Close();
                await HoverWait(() => closed, 5000);
            }
            model.Dispose();
            app.MainWindow = priorMain;
            GpuBootstrap.UseSavedPreference(renderer);
        }

        static void ReleasePreview(MainWindow window)
        {
            if (OwnerCloseField(typeof(MainWindow), "_quickPreview").GetValue(window) is QuickPreviewWindow open) open.EndOwnerClose();
        }
    }
}
