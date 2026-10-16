using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task FolderListTabDragChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(FolderListTabDragChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(FolderListTabDragChecks));
            return Task.CompletedTask;
        }
        if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable))
            || !DialogFixtureProcessScope.IsIsolatedDirectory(AppPaths.StateDirectory))
            throw new InvalidOperationException("Folder list drag checks require isolated state and test-window mode.");
        RunOnSta("folder list drag source without native input", FolderListTabDragOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task FolderListTabDragOnStaAsync()
    {
        Section("folder list drag: retained selection, source snapshot and release arbitration");
        if (Application.Current is null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        }
        var app = Application.Current!;
        var previousMain = app.MainWindow;
        var previousShutdown = app.ShutdownMode;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var owned = Path.Combine(AppPaths.StateDirectory, "folder-list-drag-" + Guid.NewGuid().ToString("N"));
        var left = Path.Combine(owned, "left");
        var right = Path.Combine(owned, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        var alpha = Path.Combine(left, "alpha.txt");
        var beta = Path.Combine(left, "beta.txt");
        var other = Path.Combine(left, "other.txt");
        var incoming = Path.Combine(owned, "incoming.txt");
        await File.WriteAllTextAsync(alpha, "selected alpha");
        await File.WriteAllTextAsync(beta, "selected beta");
        await File.WriteAllTextAsync(other, "unselected canary");
        await File.WriteAllTextAsync(incoming, "outside destination canary");
        Directory.CreateDirectory(Path.Combine(left, "child"));
        MainWindow? main = null;
        MainWindow? picker = null;
        try
        {
            main = new MainWindow(null, Path.Combine(owned, "workspace.json"));
            var shell = (MainViewModel)main.DataContext;
            shell.SuppressShellWrites = true;
            shell.Tree.IsCanvasShown = false;
            shell.Tree.PreferLightReveal = true;
            var list = shell.Tree.FolderList;
            list.IsVisible = true;
            await list.NavigateAsync(left, moveCanvas: false);
            var selection = shell.Tree.Selection;
            var sourceHost = (INestedPaneHost)main;
            var sourceControl = (ListBox)main.FindName("FolderListItems");
            var runnerCalls = 0;
            string[]? offered = null;
            string[]? sourceWhileRunning = null;
            main.FolderListDragRunnerForChecks = (source, data, allowed) =>
            {
                runnerCalls++;
                offered = ((string[])data.GetData(DataFormats.FileDrop)).ToArray();
                sourceWhileRunning = sourceHost.NestedDragPaths?.ToArray();
                Check("the list is the OLE source and offers both copy and move",
                    ReferenceEquals(source, sourceControl)
                    && (allowed & (DragDropEffects.Copy | DragDropEffects.Move)) == (DragDropEffects.Copy | DragDropEffects.Move));
                return DragDropEffects.None; // A canceled native drag: no transfer or desktop input.
            };
            var press = new Point(20, 20);
            var threshold = new Point(press.X + SystemParameters.MinimumHorizontalDragDistance + 2,
                press.Y + SystemParameters.MinimumVerticalDragDistance + 2);
            FolderListItem Row(string path) => list.RowFor(path) ?? throw new InvalidOperationException("The owned drag row is missing: " + path);
            void SelectPair()
            {
                main.CancelFolderListDragPress();
                selection.Apply(new SelectionEdit
                {
                    Clear = true, Container = left,
                    Added = [new SelectionItem(alpha, false, 14), new SelectionItem(beta, false, 13)],
                    Anchor = alpha, Focus = beta, Source = SelectionSource.List
                });
                list.OnSelectionChanged(selection);
            }
            bool Unchanged(ListDragSelectionSnapshot before) => selection.Version == before.Version
                && selection.Focus == before.Focus && selection.Anchor == before.Anchor
                && selection.Items.SequenceEqual(before.Items);

            Check("the drag fixture owns only unshown windows with no native handle",
                app.GetType() == typeof(Application) && !main.IsVisible && new WindowInteropHelper(main).Handle == IntPtr.Zero);
            SelectPair();
            var before = ListDragSelectionSnapshot.Capture(selection);
            var row = Row(alpha);
            Check("pressing an already selected row defers the ordinary list collapse",
                main.BeginFolderListDragPress(row, press, ModifierKeys.None) && Unchanged(before));
            Check("a stationary press does not launch OLE or alter paths, caret, anchor or version",
                !main.TryStartFolderListDrag(press, leftPressed: true) && runnerCalls == 0 && Unchanged(before));
            Check("movement below both drag thresholds still preserves the complete selection",
                !main.TryStartFolderListDrag(new Point(press.X + SystemParameters.MinimumHorizontalDragDistance / 2,
                    press.Y + SystemParameters.MinimumVerticalDragDistance / 2), leftPressed: true)
                && runnerCalls == 0 && Unchanged(before));
            Check("crossing the drag threshold invokes the source exactly once",
                main.TryStartFolderListDrag(threshold, leftPressed: true) && runnerCalls == 1);
            Check("the copied FileDrop payload and shared cross-tab source contain every selected path",
                offered is not null && sourceWhileRunning is not null
                && offered.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(new[] { alpha, beta }.Order(StringComparer.OrdinalIgnoreCase))
                && sourceWhileRunning.SequenceEqual(offered));
            Check("canceling the drag preserves the exact selection and clears its transient shared source",
                Unchanged(before) && sourceHost.NestedDragPaths is null);
            Check("the release following a drag is consumed instead of revealing or renaming a row",
                main.FinishFolderListDragPress(row, ModifierKeys.None) && Unchanged(before)
                && !main.TryStartFolderListDrag(threshold, leftPressed: true) && runnerCalls == 1);

            SelectPair();
            before = ListDragSelectionSnapshot.Capture(selection);
            _ = main.BeginFolderListDragPress(row, press, ModifierKeys.None);
            main.CancelFolderListDragPress();
            Check("canceling a pending gesture cannot start it later and leaves selection untouched",
                !main.TryStartFolderListDrag(threshold, leftPressed: true) && runnerCalls == 1 && Unchanged(before));

            SelectPair();
            _ = main.BeginFolderListDragPress(row, press, ModifierKeys.None);
            Check("a release before moving collapses the deferred choice and permits the normal reveal handler",
                !main.FinishFolderListDragPress(row, ModifierKeys.None)
                && selection.Count == 1 && selection.Contains(alpha) && selection.Focus == alpha
                && ViewAllPath.Equals(list.FolderPath, left) && runnerCalls == 1);

            SelectPair();
            _ = main.BeginFolderListDragPress(row, press, ModifierKeys.None);
            Check("a released mouse button cannot launch a deferred drag",
                !main.TryStartFolderListDrag(threshold, leftPressed: false) && runnerCalls == 1);

            SelectPair();
            _ = main.BeginFolderListDragPress(row, press, ModifierKeys.None);
            selection.ReplaceSingle(other, false, 17, SelectionSource.Command);
            var changedSelection = ListDragSelectionSnapshot.Capture(selection);
            Check("a selection changed after the press rejects the old drag snapshot without reverting the new choice",
                !main.TryStartFolderListDrag(threshold, leftPressed: true)
                && runnerCalls == 1 && Unchanged(changedSelection));

            SelectPair();
            _ = main.BeginFolderListDragPress(row, press, ModifierKeys.None);
            await list.NavigateAsync(right, moveCanvas: false);
            Check("navigation after pressing a row prevents a stale folder drag",
                !main.TryStartFolderListDrag(threshold, leftPressed: true) && runnerCalls == 1);
            Check("a row from the previous folder cannot arm another gesture",
                !main.BeginFolderListDragPress(row, press, ModifierKeys.None)
                && !main.TryStartFolderListDrag(threshold, leftPressed: true) && runnerCalls == 1);
            await list.NavigateAsync(left, moveCanvas: false);
            var replacement = Row(alpha);
            Check("re-reading a folder gives a replacement row whose stale predecessor cannot start a drag",
                !ReferenceEquals(row, replacement) && !main.BeginFolderListDragPress(row, press, ModifierKeys.None)
                && !main.TryStartFolderListDrag(threshold, leftPressed: true) && runnerCalls == 1);

            SelectPair();
            before = ListDragSelectionSnapshot.Capture(selection);
            Check("Ctrl pressing a selected row preserves the complete set for a copy drag",
                main.BeginFolderListDragPress(replacement, press, ModifierKeys.Control) && Unchanged(before));
            Check("Ctrl drag exports the full frozen selection and canceling keeps its caret and anchor",
                main.TryStartFolderListDrag(threshold, leftPressed: true) && runnerCalls == 2
                && offered is not null && offered.Order(StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(new[] { alpha, beta }.Order(StringComparer.OrdinalIgnoreCase))
                && main.FinishFolderListDragPress(replacement, ModifierKeys.Control) && Unchanged(before));
            SelectPair();
            _ = main.BeginFolderListDragPress(replacement, press, ModifierKeys.Control);
            Check("an ordinary Ctrl release toggles only the pressed row and keeps the remaining item selected",
                !main.FinishFolderListDragPress(replacement, ModifierKeys.Control)
                && selection.Count == 1 && selection.Contains(beta) && !selection.Contains(alpha)
                && selection.Anchor == alpha && selection.Focus == alpha && runnerCalls == 2);

            var child = Path.Combine(left, "child");
            var childRow = Row(child);
            Check("a directory row resolves to that exact child rather than the list's parent folder",
                main.TryResolveFolderListDropTarget(childRow, new[] { incoming }, DragDropEffects.Copy, out var directoryTarget)
                && ViewAllPath.Equals(directoryTarget, child));
            Check("a file row resolves to the currently displayed folder",
                main.TryResolveFolderListDropTarget(replacement, new[] { incoming }, DragDropEffects.Copy, out var fileTarget)
                && ViewAllPath.Equals(fileTarget, left));
            Check("the empty list background also resolves to the currently displayed folder",
                main.TryResolveFolderListDropTarget(null, new[] { incoming }, DragDropEffects.Copy, out var backgroundTarget)
                && ViewAllPath.Equals(backgroundTarget, left));
            Check("a source cannot be dropped onto itself or a descendant folder",
                !main.TryResolveFolderListDropTarget(childRow, new[] { child }, DragDropEffects.Move, out _)
                && !main.TryResolveFolderListDropTarget(childRow, new[] { left }, DragDropEffects.Move, out _));
            Check("a same-folder move cannot be accepted as a successful transfer",
                !main.TryResolveFolderListDropTarget(null, new[] { alpha }, DragDropEffects.Move, out _));
            Check("a replaced row and a denied effect do not retain a destination",
                !main.TryResolveFolderListDropTarget(row, new[] { incoming }, DragDropEffects.Copy, out _)
                && !main.TryResolveFolderListDropTarget(childRow, new[] { incoming }, DragDropEffects.None, out _));

            var request = new FileDialogRequest { InitialFolder = left, Mode = FileDialogMode.Open,
                IsNativeProxy = true, Options = FileDialogOptions.AllowMultiSelect | FileDialogOptions.DontAddToRecent };
            picker = new MainWindow(new FileDialogSession(request, new FileDialogClientStore(Path.Combine(owned, "clients.json"))));
            var pickerShell = (MainViewModel)picker.DataContext;
            pickerShell.SuppressShellWrites = true;
            picker.FolderListDragRunnerForChecks = (_, _, _) => { runnerCalls++; return DragDropEffects.None; };
            pickerShell.Tree.FolderList.IsVisible = true;
            await pickerShell.Tree.FolderList.NavigateAsync(left, moveCanvas: false);
            var pickerRow = pickerShell.Tree.FolderList.RowFor(alpha)!;
            pickerShell.Tree.Selection.ReplaceSingle(alpha, false, 14, SelectionSource.List);
            Check("file picker rows keep their selection behavior without exporting an OLE source",
                !picker.BeginFolderListDragPress(pickerRow, press, ModifierKeys.None)
                && !picker.TryStartFolderListDrag(threshold, leftPressed: true) && runnerCalls == 2
                && !picker.TryResolveFolderListDropTarget(null, new[] { incoming }, DragDropEffects.Copy, out _));
            Check("source arbitration never shows a window, creates a handle or transfers the owned files",
                !main.IsVisible && !picker.IsVisible && new WindowInteropHelper(main).Handle == IntPtr.Zero
                && new WindowInteropHelper(picker).Handle == IntPtr.Zero && Directory.GetFileSystemEntries(right).Length == 0
                && await File.ReadAllTextAsync(alpha) == "selected alpha" && await File.ReadAllTextAsync(beta) == "selected beta"
                && await File.ReadAllTextAsync(other) == "unselected canary");
            await FolderListRoutedDropChecksAsync(main, owned, left, alpha);
        }
        finally
        {
            main?.CancelFolderListDragPress();
            picker?.CancelFolderListDragPress();
            main?.Close();
            picker?.CloseFromCaller();
            if (main is not null) SettingsHosts.Remove(main);
            app.MainWindow = previousMain;
            app.ShutdownMode = previousShutdown;
            await SettingsSettle();
            TryDelete(owned);
        }
    }

    private static async Task FolderListRoutedDropChecksAsync(MainWindow main, string owned, string destination, string fileRowPath)
    {
        Section("folder list targets: routed drag events, real Shell transfers and restored outlines");
        var sourceWindow = main;
        var foregroundBefore = DialogNative.GetForegroundWindow();
        var invocation = TabRequest(destination);
        main = new MainWindow(null, Path.Combine(owned, "target.workspace.json"))
            { Width = 1300, Height = 820, ShowActivated = false };
        var closed = false;
        main.Closed += (_, _) => closed = true;
        main.PrepareFolderInvocation(invocation);
        main.PrepareAsNativeProxy(handle => DialogNative.CloakOwn(handle, true));
        try
        {
            var shell = (MainViewModel)main.DataContext;
            shell.SuppressShellWrites = true;
            var list = shell.Tree.FolderList;
            var control = (ListBox)main.FindName("FolderListItems");
            var panel = (Border)main.FindName("FolderListPanel");
            var child = Path.Combine(destination, "child");
            var sources = Path.Combine(owned, "routed-sources");
            Directory.CreateDirectory(sources);
            var directorySource = Path.Combine(sources, "directory-copy.txt");
            var fileSource = Path.Combine(sources, "file-row-copy.txt");
            var backgroundSource = Path.Combine(sources, "background-move.txt");
            await File.WriteAllTextAsync(directorySource, "copy into directory row");
            await File.WriteAllTextAsync(fileSource, "copy into file row parent");
            await File.WriteAllTextAsync(backgroundSource, "move onto list background");
            using (ActivationGuard.GuardWindowsCreated()) main.Show();
            var ready = await LiveWait(() => main.IsFolderWindowReady, 15000) >= 0;
            Check("the cloaked destination initializes without taking the foreground", ready
                && DialogNative.GetForegroundWindow() == foregroundBefore);
            if (!ready) return;
            var applied = await main.ApplyFolderInvocationAsync(invocation);
            Check("the cloaked destination opens the exact owned folder", applied);
            if (!applied) return;
            list.IsVisible = true;
            await list.ReloadAsync();
            main.UpdateLayout();
            await SettingsSettle();
            control.UpdateLayout();
            Check("the cloaked destination list realizes its real row templates", control.ActualWidth > 0
                && control.ActualHeight > 160 && control.ItemContainerGenerator.ContainerFromItem(list.RowFor(child)!) is ListBoxItem);
            if (control.ActualWidth <= 0 || control.ActualHeight <= 160) return;

            (ListBoxItem Container, Point Point) At(string path)
            {
                var row = list.RowFor(path) ?? throw new InvalidOperationException("The owned destination row is missing: " + path);
                control.ScrollIntoView(row);
                control.UpdateLayout();
                var container = control.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem
                    ?? throw new InvalidOperationException("The owned destination row was not realized.");
                container.ApplyTemplate();
                return (container, container.TranslatePoint(new Point(14, container.ActualHeight / 2), control));
            }
            DragEventArgs Raise(RoutedEvent routed, string source, DragDropKeyStates keys, Point point)
            {
                var args = SplitDragArgs(routed, new DataObject(DataFormats.FileDrop, new[] { source }), keys, control, point);
                control.RaiseEvent(args);
                return args;
            }
            var directory = At(child);
            var chrome = (Border)directory.Container.Template.FindName("Chrome", directory.Container);
            var oldChromeBrush = chrome.BorderBrush;
            var oldChromeThickness = chrome.BorderThickness;
            var oldChromePadding = chrome.Padding;
            Console.WriteLine($"        directory before Enter: containerPath={(directory.Container.DataContext as FolderListItem)?.FullPath}, point={directory.Point}, rowSize={directory.Container.RenderSize}, rowVisible={directory.Container.IsVisible}, listSize={control.RenderSize}, hit={control.InputHitTest(directory.Point)?.GetType().FullName}");
            var enter = Raise(DragDrop.DragEnterEvent, directorySource, DragDropKeyStates.ControlKey, directory.Point);
            var enteredPoint = enter.GetPosition(control);
            var resolvedDirectoryRow = typeof(MainWindow).GetMethod("DropRow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(main, [enter]) as FolderListItem;
            Console.WriteLine($"        directory after Enter: eventPoint={enteredPoint}, hit={control.InputHitTest(enteredPoint)?.GetType().FullName}, resolvedPath={resolvedDirectoryRow?.FullPath}, target={main.CurrentFolderListDropTarget}, effects={enter.Effects}, chromeThickness={chrome.BorderThickness}");
            Check("native-style DragEnter resolves the actual directory row and immediately shows Copy feedback",
                enter.Handled && enter.Effects == DragDropEffects.Copy && ViewAllPath.Equals(main.CurrentFolderListDropTarget ?? string.Empty, child)
                && chrome.BorderThickness == new Thickness(1));
            Raise(DragDrop.DragLeaveEvent, directorySource, DragDropKeyStates.ControlKey, directory.Point);
            Check("leaving a directory row restores its original brush, border and padding",
                main.CurrentFolderListDropTarget is null && ReferenceEquals(chrome.BorderBrush, oldChromeBrush)
                && chrome.BorderThickness == oldChromeThickness && chrome.Padding == oldChromePadding);
            _ = Raise(DragDrop.DragOverEvent, directorySource, DragDropKeyStates.ControlKey, directory.Point);
            var directoryDrop = Raise(DragDrop.DropEvent, directorySource, DragDropKeyStates.ControlKey, directory.Point);
            var directoryCopied = Path.Combine(child, Path.GetFileName(directorySource));
            Check("the actual directory-row Drop copies exact bytes into that child and preserves its source",
                directoryDrop.Handled && directoryDrop.Effects == DragDropEffects.Copy
                && await LiveWait(() => File.Exists(directoryCopied), 10000) >= 0
                && await File.ReadAllTextAsync(directoryCopied) == "copy into directory row" && File.Exists(directorySource));

            var file = At(fileRowPath);
            var bindingSource = new Border { BorderBrush = panel.BorderBrush };
            panel.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Border.BorderBrush)) { Source = bindingSource });
            var oldPanelBrush = panel.BorderBrush;
            var originalBinding = BindingOperations.GetBindingExpression(panel, Border.BorderBrushProperty);
            var originalBase = DependencyPropertyHelper.GetValueSource(panel, Border.BorderBrushProperty).BaseValueSource;
            var fileOver = Raise(DragDrop.DragEnterEvent, fileSource, DragDropKeyStates.ControlKey, file.Point);
            Check("hovering a real file row highlights its displayed parent folder rather than accepting a file as a directory",
                fileOver.Handled && fileOver.Effects == DragDropEffects.Copy
                && ViewAllPath.Equals(main.CurrentFolderListDropTarget ?? string.Empty, destination));
            Raise(DragDrop.DragLeaveEvent, fileSource, DragDropKeyStates.ControlKey, file.Point);
            Check("leaving the list restores the same panel brush and preserves its live binding and value source",
                main.CurrentFolderListDropTarget is null && ReferenceEquals(panel.BorderBrush, oldPanelBrush)
                && ReferenceEquals(BindingOperations.GetBindingExpression(panel, Border.BorderBrushProperty), originalBinding)
                && DependencyPropertyHelper.GetValueSource(panel, Border.BorderBrushProperty).BaseValueSource == originalBase);
            var changedBrush = (Brush)main.FindResource("SurfaceRaisedBrush");
            bindingSource.BorderBrush = changedBrush;
            await SettingsSettle();
            Check("the restored panel binding still follows subsequent source changes", ReferenceEquals(panel.BorderBrush, changedBrush));
            _ = Raise(DragDrop.DragOverEvent, fileSource, DragDropKeyStates.ControlKey, file.Point);
            var fileDrop = Raise(DragDrop.DropEvent, fileSource, DragDropKeyStates.ControlKey, file.Point);
            var fileCopied = Path.Combine(destination, Path.GetFileName(fileSource));
            Check("dropping onto a file row copies into its displayed folder without changing that file",
                fileDrop.Handled && fileDrop.Effects == DragDropEffects.Copy && await LiveWait(() => File.Exists(fileCopied), 10000) >= 0
                && await File.ReadAllTextAsync(fileCopied) == "copy into file row parent" && File.Exists(fileSource)
                && await File.ReadAllTextAsync(fileRowPath) == "selected alpha");

            control.UpdateLayout();
            var blank = new Point(Math.Max(12, control.ActualWidth / 2), control.ActualHeight - 24);
            var blankOver = Raise(DragDrop.DragOverEvent, backgroundSource, DragDropKeyStates.ShiftKey, blank);
            Check("empty list space offers Move into the currently displayed folder", blankOver.Handled
                && blankOver.Effects == DragDropEffects.Move && ViewAllPath.Equals(main.CurrentFolderListDropTarget ?? string.Empty, destination));
            var backgroundDrop = Raise(DragDrop.DropEvent, backgroundSource, DragDropKeyStates.ShiftKey, blank);
            var backgroundMoved = Path.Combine(destination, Path.GetFileName(backgroundSource));
            Check("the actual background Drop completes the owned move and removes only its source",
                backgroundDrop.Handled && backgroundDrop.Effects == DragDropEffects.None
                && await LiveWait(() => File.Exists(backgroundMoved) && !File.Exists(backgroundSource), 10000) >= 0
                && await File.ReadAllTextAsync(backgroundMoved) == "move onto list background");
            var destinationHandle = new WindowInteropHelper(main).Handle;
            var sourceHandle = new WindowInteropHelper(sourceWindow).Handle;
            var foregroundAfter = DialogNative.GetForegroundWindow();
            var transferStayedHidden = main.CurrentFolderListDropTarget is null && main.IsVisible && !main.IsActive
                && destinationHandle != IntPtr.Zero && DialogNative.IsCloaked(destinationHandle)
                && foregroundAfter == foregroundBefore && !sourceWindow.IsVisible && sourceHandle == IntPtr.Zero;
            if (!transferStayedHidden)
                Console.WriteLine("List transfer window diagnostic: " + System.Text.Json.JsonSerializer.Serialize(new {
                    DropTargetCleared = main.CurrentFolderListDropTarget is null,
                    DestinationVisible = main.IsVisible, DestinationActive = main.IsActive,
                    DestinationHandle = destinationHandle.ToInt64(),
                    DestinationCloaked = destinationHandle != IntPtr.Zero && DialogNative.IsCloaked(destinationHandle),
                    ForegroundBefore = foregroundBefore.ToInt64(), ForegroundAfter = foregroundAfter.ToInt64(),
                    SourceVisible = sourceWindow.IsVisible, SourceHandle = sourceHandle.ToInt64()
                }));
            Check("routed list transfers leave the destination cloaked, the source unshown and the foreground unchanged",
                transferStayedHidden);
        }
        finally
        {
            main.Close();
            Check("the owned cloaked destination closes after its transfers", await LiveWait(() => closed, 15000) >= 0);
        }
    }

    private readonly record struct ListDragSelectionSnapshot(SelectionItem[] Items, long Version, string? Focus, string? Anchor)
    {
        internal static ListDragSelectionSnapshot Capture(ItemSelection selection)
            => new(selection.Items.ToArray(), selection.Version, selection.Focus, selection.Anchor);
    }
}
