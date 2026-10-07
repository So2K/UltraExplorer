using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using UltraExplorer;
using UltraExplorer.Controls;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task SpacePreviewWindowChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(SpacePreviewWindowChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(SpacePreviewWindowChecks));
            return Task.CompletedTask;
        }
        if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable))
            || !DialogFixtureProcessScope.IsIsolatedDirectory(AppPaths.StateDirectory))
            throw new InvalidOperationException("Space preview window checks require isolated state and test-window mode.");
        RunOnSta("Space preview, unshown resource-only window", SpacePreviewWindowOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task SpacePreviewWindowOnStaAsync()
    {
        Section("selected-file Space tap: window input arbitration and pan preservation");
        if (Application.Current is null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        }
        var app = Application.Current ?? throw new InvalidOperationException("The test Application was not initialized.");
        var previousMain = app.MainWindow;
        var previousShutdown = app.ShutdownMode;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var owned = Path.Combine(AppPaths.StateDirectory, "space-preview-" + Guid.NewGuid().ToString("N"));
        var drive = Path.Combine(owned, "drive");
        var folder = Path.Combine(drive, "documents");
        Directory.CreateDirectory(folder);
        var selected = Path.Combine(folder, "selected-A.txt");
        var hovered = Path.Combine(folder, "hovered-B.txt");
        await File.WriteAllTextAsync(selected, "selected A");
        await File.WriteAllTextAsync(hovered, "hovered B");
        MainWindow? main = null;
        MainViewModel? shell = null;
        try
        {
            main = new MainWindow(null, Path.Combine(owned, "workspace.json"));
            shell = (MainViewModel)main.DataContext;
            shell.SuppressShellWrites = true;
            shell.Layout = CanvasLayout.Nested;
            await main.StartNestedForChecksAsync();
            main.UseNestedDrivesForChecks([new NestedRoot(drive, "Owned Space fixture", NestedFolderKind.Drive)]);
            LayOutWindow(main, 1200, 760);
            var first = main.FirstPane;
            await FocusLoadAsync(first, drive, folder);
            first.Canvas.FramesByHandForTests = true;
            first.Canvas.FitAll(animated: false);
            await SettingsSettle();
            var selection = shell.Tree.Selection;
            long clock = 1_000;
            main.SpacePreviewGestureForChecks = new PreviewSpaceGesture(() => clock);
            void SelectA()
            {
                main.CancelSpacePreview(disarm: true);
                main.ActivatePane(first);
                selection.ReplaceSingle(selected, false, 10, SelectionSource.Canvas);
                first.SyncSelection();
                clock += 1_000;
            }
            bool Begin(DependencyObject? origin = null, bool repeat = false) => main.TryBeginSpacePreview(
                Key.Space, ModifierKeys.None, repeat, origin ?? first.Canvas);
            string? Finish(ModifierKeys modifiers = ModifierKeys.None, DependencyObject? origin = null)
                => main.FinishSpacePreview(modifiers, origin ?? first.Canvas);

            Check("Space fixtures use a resource-only Application and an unshown window without a native HWND",
                app.GetType() == typeof(Application) && !main.IsVisible && new WindowInteropHelper(main).Handle == IntPtr.Zero);
            SelectA();
            shell.ShowHoverPreviews = true;
            main.SetHoverPreviewTarget(hovered, new Point(240, 120), first.Canvas);
            var paths = selection.Paths.ToArray();
            var version = selection.Version;
            var focus = selection.Focus;
            var anchor = selection.Anchor;
            var camera = first.Canvas.CaptureCamera();
            var history = first.History.Count;
            var marked = 0;
            var taken = main.TryBeginSpacePreview(Key.Space, ModifierKeys.None, inputOrigin: first.Canvas,
                markHandled: () => marked++);
            Check("a selected-file Space down is synchronously claimed and immediately arms the pan hand",
                taken && marked == 1 && first.Canvas.IsSpacePanArmed && main.SpacePreviewGestureForChecks.IsPressed);
            clock += 199;
            Check("a short Space tap opens the exact selected A even while the pointer hovers over B",
                Finish() == selected && !first.Canvas.IsSpacePanArmed && !main.SpacePreviewGestureForChecks.IsPressed);
            Check("classifying a Space tap preserves selected paths, focus, anchor, version, history and camera",
                selection.Paths.SequenceEqual(paths) && selection.Version == version && selection.Focus == focus
                && selection.Anchor == anchor && first.History.Count == history && first.Canvas.CaptureCamera() == camera);

            SelectA();
            selection.Clear(SelectionSource.Command);
            Check("a cleared selection retains its caret but has no file eligible for Space preview",
                selection.Focus == selected && MainWindow.SelectedPreviewFile(selection) is null);
            _ = Begin();
            clock += 40;
            Check("a short Space tap cannot reopen an unselected remembered caret", Finish() is null);
            selection.ReplaceSingle(folder, true, 0, SelectionSource.Canvas);
            Check("a selected folder is not treated as a file preview", MainWindow.SelectedPreviewFile(selection) is null);
            _ = Begin();
            clock += 40;
            Check("folder-only Space still leaves the canvas pan gesture available and finishes without preview",
                first.Canvas.IsSpacePanArmed && Finish() is null && !first.Canvas.IsSpacePanArmed);

            SelectA();
            _ = Begin();
            clock += 190;
            _ = Begin(repeat: true);
            clock += 9;
            Check("auto-repeat preserves the original press and exact short-tap target", Finish() == selected);
            SelectA();
            _ = Begin();
            clock += 190;
            _ = Begin(repeat: true);
            clock += 10;
            Check("exactly 200 ms remains a hold even if auto-repeat occurred near release", Finish() is null);

            SelectA();
            var inputs = new DependencyObject[] { new TextBox(), new RichTextBox(), new PasswordBox(),
                new ComboBox { IsEditable = true }, new Button() };
            Check("Space remains available to text, password, editable combo and button controls",
                inputs.All(input => !Begin(input)) && !main.SpacePreviewGestureForChecks.IsPressed
                && !first.Canvas.IsSpacePanArmed);
            Check("modified Space chords and unrelated keys do not start the plain Space gesture",
                !main.TryBeginSpacePreview(Key.Space, ModifierKeys.Control, inputOrigin: first.Canvas)
                && !main.TryBeginSpacePreview(Key.Space, ModifierKeys.Alt, inputOrigin: first.Canvas)
                && !main.TryBeginSpacePreview(Key.F, ModifierKeys.None, inputOrigin: first.Canvas));

            var list = shell.Tree.FolderList;
            var rowsField = typeof(FolderListViewModel).GetField("_rowsVersion", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var folderField = typeof(FolderListViewModel).GetField("_folderVersion", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var rowsBefore = rowsField.GetValue(list);
            var folderBefore = folderField.GetValue(list);
            try
            {
                rowsField.SetValue(list, 0L);
                folderField.SetValue(list, 1L);
                Check("obsolete folder-list rows cannot start previewing the window selection",
                    !list.HasCurrentRows && !Begin(main.FolderListItems));
            }
            finally { rowsField.SetValue(list, rowsBefore); folderField.SetValue(list, folderBefore); }

            SelectA();
            _ = Begin();
            main.CancelSpacePreview();
            clock += 40;
            Check("a mouse or wheel cancellation suppresses the tap while keeping held-Space panning armed",
                first.Canvas.IsSpacePanArmed && main.SpacePreviewGestureForChecks.IsPressed
                && Finish() is null && !first.Canvas.IsSpacePanArmed);
            SelectA();
            _ = Begin();
            main.CancelSpacePreview(disarm: true);
            Check("deactivation or another key clears both the candidate and the held pan state",
                !main.SpacePreviewGestureForChecks.IsPressed && !first.Canvas.IsSpacePanArmed && Finish() is null);

            SelectA();
            _ = Begin();
            clock += 30;
            selection.ReplaceSingle(hovered, false, 9, SelectionSource.Canvas);
            Check("selection changes during Space invalidate the original exact-file target", Finish() is null);
            SelectA();
            main.SetHoverPreviewTarget(null, default);
            _ = Begin();
            clock += 30;
            first.Canvas.Pan(new Vector(19, -7));
            Check("a real canvas camera move cancels a short-tap preview without requiring a hover subscription",
                main.HoverPreviewsForChecks.Path is null && Finish() is null);

            SelectA();
            var previewFolder = first.Tree.Find(folder)!;
            var beforePointer = first.Canvas.ScreenRectOf(previewFolder)!.Value;
            _ = Begin();
            var pointerAnchor = main.SpacePreviewPointerAnchorForChecks;
            main.SpacePreviewPointerMoved(pointerAnchor + new Vector(.125, .25), capture: false);
            var afterPointer = first.Canvas.ScreenRectOf(previewFolder)!.Value;
            Check("held Space accepts the first pointer movement without requiring a mouse button or delay",
                Near(afterPointer.X - beforePointer.X, .125, 1e-8) && Near(afterPointer.Y - beforePointer.Y, .25, 1e-8));
            clock += 30;
            Check("pointer motion converts even a short Space press into pan instead of file preview", Finish() is null);
            var stoppedPointer = first.Canvas.ScreenRectOf(previewFolder)!.Value;
            Check("Space release stops button-free pointer pan with no follow-on camera motion",
                !main.SpacePreviewPointerMoved(pointerAnchor + new Vector(50, 40), capture: false)
                && SameRect(first.Canvas.ScreenRectOf(previewFolder), stoppedPointer, 1e-8));

            SelectA();
            _ = Begin();
            clock += 30;
            Check("release into another control cancels the candidate and disarms the pan hand",
                Finish(origin: new TextBox()) is null && !first.Canvas.IsSpacePanArmed);
            SelectA();
            _ = Begin();
            clock += 30;
            Check("Space release while Alt is held never previews or leaves the pan hand latched",
                Finish(ModifierKeys.Alt) is null && !first.Canvas.IsSpacePanArmed);

            shell.IsSplit = true;
            var second = main.SecondPane;
            Check("the Space input fixture can create its independent second pane", second is not null);
            if (second is not null)
            {
                second.Canvas.FramesByHandForTests = true;
                SelectA();
                _ = Begin();
                clock += 30;
                main.ActivatePane(second);
                Check("switching active panes during Space cannot open the previous pane's file",
                    main.FinishSpacePreview(ModifierKeys.None, second.Canvas) is null
                    && !first.Canvas.IsSpacePanArmed && !second.Canvas.IsSpacePanArmed);
            }
            Check("Space arbitration never creates or displays a Quick Look or product startup window",
                !app.Windows.OfType<QuickPreviewWindow>().Any() && app.Windows.OfType<MainWindow>().All(window => ReferenceEquals(window, main))
                && !main.IsVisible && new WindowInteropHelper(main).Handle == IntPtr.Zero);
        }
        finally
        {
            if (main is not null)
            {
                main.CancelSpacePreview(disarm: true);
                typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, true);
                main.Close();
            }
            else shell?.Dispose();
            app.MainWindow = previousMain;
            app.ShutdownMode = previousShutdown;
            TryDelete(owned);
        }
        Check("the unshown Space fixture closes without leaving an owned window behind",
            !app.Windows.OfType<MainWindow>().Any() && !app.Windows.OfType<QuickPreviewWindow>().Any());
    }
}
