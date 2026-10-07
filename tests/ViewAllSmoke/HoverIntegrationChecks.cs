using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UltraExplorer;
using UltraExplorer.Controls;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task HoverIntegrationChecks()
    {
        // Settings creates the one Application on its own STA during a full
        // run. This window group needs a fresh owning dispatcher, as the
        // existing publication and cross-file integration groups do.
        if (_only is not [var alone] || !alone.Equals(nameof(HoverIntegrationChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(HoverIntegrationChecks));
            return Task.CompletedTask;
        }
        RunOnSta("hover integration with focus and picker input", HoverIntegrationOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task HoverIntegrationOnStaAsync()
    {
        Section("hover integration: owned cloaked fixtures and preserved interaction state");
        var requestedState = Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable);
        var everydayState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer");
        var isolated = !string.IsNullOrWhiteSpace(requestedState)
            && ViewAllPath.Equals(requestedState!, AppPaths.StateDirectory)
            && !ViewAllPath.Equals(AppPaths.StateDirectory, everydayState)
            && Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1";
        Check("hover integration requires genuinely isolated state and test-window mode", isolated);
        if (!isolated) return;

        if (Application.Current is null)
        {
            // App's constructor queues product OnStartup even without Run().
            // Reuse the product themes without its default window or broker.
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        }
        var app = Application.Current ?? throw new InvalidOperationException("The owned preview Application was not initialized.");
        var priorShutdown = app.ShutdownMode;
        var priorMain = app.MainWindow;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var owned = Path.Combine(AppPaths.StateDirectory, "hover-integration-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(owned, "data");
        var folder = Path.Combine(data, "images");
        var state = Path.Combine(owned, "state");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(state);
        var first = Path.Combine(folder, "hovered.png");
        var selected = Path.Combine(folder, "selected.png");
        HoverSaveImage(HoverImage(160, 80, Colors.Coral), first);
        HoverSaveImage(HoverImage(80, 160, Colors.CornflowerBlue), selected);
        var workspace = AppPaths.State("workspace.json");
        var original = File.Exists(workspace) ? File.ReadAllBytes(workspace) : null;
        var originalTime = original is null ? (DateTime?)null : File.GetLastWriteTimeUtc(workspace);
        try
        {
            await SettingsSettle();
            Check("the resource-only application pumps startup without a product window or folder broker",
                app.GetType() == typeof(Application) && HoverIntegrationOwnsOnly());
            await HoverFocusInteractionAsync(data, folder, state, first, selected);
            await HoverPickerInputInteractionAsync(data, folder, state, first);
            await SettingsSettle();
            Check("both owned fixtures leave no window or folder broker behind", HoverIntegrationOwnsOnly());
        }
        finally
        {
            app.MainWindow = priorMain;
            app.ShutdownMode = priorShutdown;
            if (original is not null)
            {
                await File.WriteAllBytesAsync(workspace, original);
                File.SetLastWriteTimeUtc(workspace, originalTime!.Value);
            }
            else if (File.Exists(workspace)) File.Delete(workspace);
            TryDelete(owned);
        }
        Check("the fixture restores the isolated workspace bytes and timestamp exactly",
            original is null ? !File.Exists(workspace)
                : File.ReadAllBytes(workspace).SequenceEqual(original)
                    && File.GetLastWriteTimeUtc(workspace) == originalTime);
    }

    private static async Task HoverFocusInteractionAsync(string data, string folder, string state, string first, string selected)
    {
        Section("hover integration: history and the existing smooth F flight");
        var main = new MainWindow(null, Path.Combine(state, "normal.workspace.json"))
        {
            Width = 1200, Height = 700, WindowState = WindowState.Normal
        };
        var shell = (MainViewModel)main.DataContext;
        var closed = false;
        main.Closed += (_, _) => closed = true;
        try
        {
            shell.SuppressShellWrites = true;
            main.ActivePane.Tree.IsReadingOnDemand = false;
            if (!FolderCommandLine.TryParse(["--open-folder", folder], out var invocation, out _))
                throw new InvalidOperationException("The owned hover focus folder invocation is invalid.");
            main.PrepareFolderInvocation(invocation);
            main.PrepareAsNativeProxy(handle => DialogNative.CloakOwn(handle, true));
            using (ActivationGuard.GuardWindowsCreated()) main.Show();
            var ready = await HoverWait(() => main.IsFolderWindowReady, 6000);
            Check("the cloaked focus fixture completes startup in its owned folder", ready);
            if (!ready) return;
            Check("focus startup creates only its owned window and no folder broker", HoverIntegrationOwnsOnly(main));
            await shell.Tree.SelectPathAsync(selected);
            shell.Tree.FolderList.IsVisible = false;
            shell.ShowHoverPreviews = true;
            main.UseNestedDrivesForChecks([new NestedRoot(data, "Owned hover fixture", NestedFolderKind.Drive)]);
            var pane = main.ActivePane;
            await FocusLoadAsync(pane, data, folder);
            HoverIntegrationManualFrames(pane.Canvas);
            var content = (Grid)main.Content;
            HoverLayout(content);
            shell.Tree.Selection.Apply(new SelectionEdit
            {
                Clear = true,
                Added = [new SelectionItem(first, false, new FileInfo(first).Length), new SelectionItem(selected, false, new FileInfo(selected).Length)],
                Anchor = first, Focus = selected, Source = SelectionSource.Canvas
            });
            pane.SyncSelection();
            pane.History.Record(data);
            pane.History.Record(folder);
            pane.Canvas.FitAll(animated: false);
            await SettingsSettle();
            var before = HoverInteractionState(main, shell);
            main.SetHoverPreviewTarget(first, new Point(450, 220), pane.Canvas);
            var previewReady = await HoverWait(() => main.HoverPreviewsForChecks.Result is not null);
            Check("a real content preview targets the hovered member rather than the selection focus",
                previewReady && main.PreviewCardForChecks.Visibility == Visibility.Visible
                && ViewAllPath.Equals(main.HoverPreviewsForChecks.Path ?? string.Empty, first)
                && shell.Tree.Selection.Focus == selected);
            Check("a live preview preserves nonempty history, selected paths, anchor, focus, version and keyboard",
                before.HistoryCount >= 2 && SameHoverInteraction(before, HoverInteractionState(main, shell)));
            var menu = main.BuildLayersMenu(main.CanvasLayersButton);
            var toggle = menu.Items.OfType<MenuItem>().Single(item => item.Header as string == "Hover previews");
            toggle.SetCurrentValue(MenuItem.IsCheckedProperty, false);
            await SettingsSettle();
            Check("the live menu switch hides the card while leaving the complete interaction state unchanged",
                !shell.ShowHoverPreviews && main.PreviewCardForChecks.Visibility == Visibility.Collapsed
                && main.HoverPreviewsForChecks.Path is null
                && SameHoverInteraction(before, HoverInteractionState(main, shell)));
            toggle.SetCurrentValue(MenuItem.IsCheckedProperty, true);
            await SettingsSettle();
            main.SetHoverPreviewTarget(first, new Point(450, 220), pane.Canvas);
            Check("re-enabling the switch restores a live preview without recording navigation",
                await HoverWait(() => main.HoverPreviewsForChecks.Result is not null)
                && SameHoverInteraction(before, HoverInteractionState(main, shell)));

            var takeoff = pane.Canvas.CaptureCamera();
            var key = HoverIntegrationKey(main, pane.Canvas);
            var handled = main.TryHandleFocusSelectionKey(Key.F, ModifierKeys.None, inputOrigin: pane.Canvas,
                markHandled: () => key.Handled = true);
            // Invoke the real preview key handler after the explicit-modifier
            // F arbitration seam; no test relies on physical Keyboard.Modifiers.
            HoverIntegrationClearForKey(main, key);
            var flightStarted = await HoverWait(() => FocusFlightOf(pane.Canvas) is not null);
            Check("F dismisses the live preview and begins the current active pane's smooth flight",
                handled && key.Handled && flightStarted && ReferenceEquals(main.ActivePane, pane)
                && pane.Canvas.CaptureCamera() == takeoff && main.PreviewCardForChecks.Visibility == Visibility.Collapsed
                && main.HoverPreviewsForChecks.Path is null);
            SetFocusFlightProgress(pane.Canvas, 0.5);
            pane.Canvas.RunFrameForTests(TimeSpan.FromSeconds(100));
            Check("the existing F flight advances through an intermediate frame instead of cutting to the leaf",
                pane.Canvas.CaptureCamera() != takeoff && FocusFlightOf(pane.Canvas) is not null
                && !(FocusFileRect(pane.Canvas, pane.Tree, selected) is { } middle && FocusCentred(middle, pane.Canvas, 0.75)));
            SetFocusFlightProgress(pane.Canvas, 2);
            pane.Canvas.RunFrameForTests(TimeSpan.FromSeconds(100) + TimeSpan.FromMilliseconds(16));
            Check("F lands on the selected leaf rather than the previously hovered file",
                FocusFlightOf(pane.Canvas) is null
                && FocusFileRect(pane.Canvas, pane.Tree, selected) is { } final && FocusCentred(final, pane.Canvas, 0.75));
            Check("preview dismissal and the completed F flight preserve selection, history and keyboard",
                SameHoverInteraction(before, HoverInteractionState(main, shell)));
        }
        finally
        {
            main.Close();
            Check("the cloaked focus fixture closes and releases its preview controller", await HoverWait(() => closed, 2500));
            shell.Dispose();
        }
    }

    private static async Task HoverPickerInputInteractionAsync(string data, string folder, string state, string file)
    {
        Section("hover integration: F remains available to the editable picker name");
        var request = new FileDialogRequest
        {
            IsNativeProxy = true, Mode = FileDialogMode.Save, InitialFolder = folder, FileName = "draft.png",
            Options = FileDialogOptions.ForceFileSystem | FileDialogOptions.NoTestFileCreate | FileDialogOptions.DontAddToRecent
        };
        request.Filters.Add(new FileDialogFilterSpec("Images", "*.png"));
        var session = new FileDialogSession(request, new FileDialogClientStore(Path.Combine(state, "clients.json")));
        var picker = new MainWindow(session) { Width = 1200, Height = 700, WindowState = WindowState.Normal };
        var shell = (MainViewModel)picker.DataContext;
        var closed = false;
        picker.Closed += (_, _) => closed = true;
        try
        {
            shell.SuppressShellWrites = true;
            picker.PrepareAsCloakedPicker(_ => { });
            using (ActivationGuard.GuardWindowsCreated()) picker.Show();
            var pickerReady = typeof(MainWindow).GetField("_pickerNestedReady", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var ready = await HoverWait(() => (bool)pickerReady.GetValue(picker)! && !shell.Tree.IsBusy, 6000);
            Check("the cloaked Save picker completes its owned image-folder startup", ready);
            if (!ready) return;
            Check("picker startup creates only its owned window and no folder broker", HoverIntegrationOwnsOnly(picker));
            await shell.Tree.SelectPathAsync(file);
            shell.Tree.FolderList.IsVisible = false;
            picker.UseNestedDrivesForChecks([new NestedRoot(data, "Owned picker fixture", NestedFolderKind.Drive)]);
            var pane = picker.ActivePane;
            await FocusLoadAsync(pane, data, folder);
            HoverIntegrationManualFrames(pane.Canvas);
            pane.Canvas.FitAll(animated: false);
            HoverLayout((Grid)picker.Content);
            shell.ShowHoverPreviews = true;
            pane.History.Record(data);
            pane.History.Record(folder);
            session.FileNameText = "draft.png";
            await SettingsSettle();
            var editor = picker.PickerNameBox.Template.FindName("PART_EditableTextBox", picker.PickerNameBox) as TextBox;
            Check("the picker fixture uses its actual editable name text box", picker.PickerNameBox.IsEditable && editor is not null);
            if (editor is null) return;
            var before = HoverInteractionState(picker, shell);
            picker.SetHoverPreviewTarget(file, new Point(420, 180), pane.Canvas);
            Check("the typed-F fixture starts with a real visible content preview",
                await HoverWait(() => picker.HoverPreviewsForChecks.Result is not null)
                && picker.PreviewCardForChecks.Visibility == Visibility.Visible);
            var takeoff = pane.Canvas.CaptureCamera();
            var key = HoverIntegrationKey(picker, editor);
            var claimed = picker.TryHandleFocusSelectionKey(Key.F, ModifierKeys.None, inputOrigin: editor,
                markHandled: () => key.Handled = true);
            HoverIntegrationClearForKey(picker, key);
            Check("F in the actual picker name editor stays unhandled and starts no camera flight",
                !claimed && !key.Handled && FocusFlightOf(pane.Canvas) is null && pane.Canvas.CaptureCamera() == takeoff);
            Check("typing arbitration dismisses the preview without changing the picker name, history, selection or keyboard",
                picker.PreviewCardForChecks.Visibility == Visibility.Collapsed && picker.HoverPreviewsForChecks.Path is null
                && session.FileNameText == "draft.png" && SameHoverInteraction(before, HoverInteractionState(picker, shell)));
        }
        finally
        {
            picker.Close();
            Check("the cloaked picker fixture closes and releases its preview controller", await HoverWait(() => closed, 2500));
            shell.Dispose();
        }
    }

    private static KeyEventArgs HoverIntegrationKey(MainWindow window, DependencyObject origin) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.F)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = origin };

    private static void HoverIntegrationClearForKey(MainWindow window, KeyEventArgs key) =>
        typeof(MainWindow).GetMethod("PreviewHoverKey", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window, key]);

    private static void HoverIntegrationManualFrames(NestedCanvas canvas)
    {
        // These fixtures were shown to create real, cloaked HWNDs. Detach any
        // Rendering subscription created at startup before freezing the flight
        // clock; subsequent frame requests use the existing hand-driven seam.
        canvas.FramesByHandForTests = true;
        typeof(NestedCanvas).GetMethod("UnhookFrame", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, null);
    }

    private static bool HoverIntegrationOwnsOnly(params MainWindow[] owned)
    {
        var windows = Application.Current.Windows.Cast<Window>().ToArray();
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var host = typeof(ExplorerLaunchRouter).GetField("_host", flags)!.GetValue(null);
        var destinations = (IEnumerable<MainWindow>)typeof(ExplorerLaunchRouter).GetField("Windows", flags)!.GetValue(null)!;
        return windows.Length == owned.Length && owned.All(window => windows.Contains(window))
            && host is null && !destinations.Any();
    }

    private static HoverInteractionSnapshot HoverInteractionState(MainWindow window, MainViewModel shell) =>
        new(shell.History.Count, shell.History.Current, shell.Tree.Selection.Version, shell.Tree.Selection.Paths.ToArray(),
            shell.Tree.Selection.Anchor, shell.Tree.Selection.Focus, Keyboard.FocusedElement, window.ActivePane);

    private static bool SameHoverInteraction(HoverInteractionSnapshot before, HoverInteractionSnapshot after) =>
        before.HistoryCount == after.HistoryCount && before.HistoryCurrent == after.HistoryCurrent
        && before.SelectionVersion == after.SelectionVersion && before.Paths.SequenceEqual(after.Paths, StringComparer.OrdinalIgnoreCase)
        && before.Anchor == after.Anchor && before.Focus == after.Focus && ReferenceEquals(before.Keyboard, after.Keyboard)
        && ReferenceEquals(before.Pane, after.Pane);

    private sealed record HoverInteractionSnapshot(int HistoryCount, string? HistoryCurrent, long SelectionVersion,
        string[] Paths, string? Anchor, string? Focus, IInputElement? Keyboard, NestedPane Pane);
}
