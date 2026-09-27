using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.ViewModels;

namespace UltraExplorer;

/// <summary>
/// The one selection, through the window.  The view model's
/// <see cref="ItemSelection"/> is the selection; the nested canvas and the
/// folder list each show it and each change it, and every command acts on
/// it - so a rectangle drawn on the canvas is what the list lights, what Copy
/// copies, what a drag carries and what the menu is for.
///
/// <para>A change made in one view reaches the model as one edit and comes
/// back to the other views as one notification.  The view that made it
/// already shows it, and ignores its own echo: the canvas by the version it
/// is told it now holds, the list because the change says it came from the
/// list.  Only what the user does to the list box counts as the list's
/// change - never what the list box does to its selection while its rows are
/// replaced under it.</para>
/// </summary>
public partial class MainWindow
{
    private bool _applyingCanvasSelection;
    private bool _applyingListSelection;
    private bool _listUserInput;

    private void AttachSelection()
    {
        Nested.SelectionCommitted += OnNestedSelectionCommitted;
        Nested.MarqueePreview += OnNestedMarqueePreview;
        Nested.MarqueeStarted += OnNestedMarqueeStarted;
        _viewModel.Tree.Selection.Changed += OnSharedSelectionChanged;
        _viewModel.Tree.FolderList.SelectionRowsChanged += ApplySelectionToList;
        _viewModel.Tree.FolderList.RowsReplaced += ApplySelectionToList;
        _viewModel.PropertyChanged += OnShellPropertyChangedForSelection;
        FolderListItems.PreviewKeyDown += OnFolderListPreviewKeyDown;
        FolderListItems.PreviewMouseRightButtonDown += OnFolderListPreviewMouseDown;
    }

    private void DetachSelection()
    {
        Nested.SelectionCommitted -= OnNestedSelectionCommitted;
        Nested.MarqueePreview -= OnNestedMarqueePreview;
        Nested.MarqueeStarted -= OnNestedMarqueeStarted;
        _viewModel.Tree.Selection.Changed -= OnSharedSelectionChanged;
        _viewModel.Tree.FolderList.SelectionRowsChanged -= ApplySelectionToList;
        _viewModel.Tree.FolderList.RowsReplaced -= ApplySelectionToList;
        _viewModel.PropertyChanged -= OnShellPropertyChangedForSelection;
        FolderListItems.PreviewKeyDown -= OnFolderListPreviewKeyDown;
        FolderListItems.PreviewMouseRightButtonDown -= OnFolderListPreviewMouseDown;
    }

    /// <summary>
    /// A file dialog that returns one item keeps one selected: the list picks
    /// one row at a time and the selection holds one path.
    /// </summary>
    private void ConfigurePickerSelection()
    {
        if (_picker is { AllowsMultipleSelection: false })
        {
            _viewModel.Tree.Selection.MaxCount = 1;
            FolderListItems.SelectionMode = SelectionMode.Single;
        }
    }

    // ---- the canvas ------------------------------------------------------------

    /// <summary>
    /// A gesture on the canvas: applied to the shared selection in the same
    /// call, so a drag or a menu straight after it already acts on it, and
    /// the version it made noted as the canvas's own.
    /// </summary>
    private void OnNestedSelectionCommitted(SelectionEdit edit)
    {
        var selection = _viewModel.Tree.Selection;
        _applyingCanvasSelection = true;
        try
        {
            selection.Apply(edit);
        }
        finally
        {
            _applyingCanvasSelection = false;
        }

        Nested.AcknowledgeSelection(selection.Version);
    }

    /// <summary>Any other change: the canvas takes it in, unless it is not the picture on show.</summary>
    private void OnSharedSelectionChanged(ItemSelection selection)
    {
        // The headers are for the folder selected, or the one the selection is in.
        UpdateSortHeaders();

        if (!_applyingCanvasSelection && IsNested && _nestedReady)
        {
            Nested.LoadSelection(selection);
        }
    }

    private void OnNestedMarqueePreview(int count) => _viewModel.Tree.ShowSelectionPreview(count);

    /// <summary>
    /// The first rectangle ever drawn says, once, that left-drag used to pan
    /// and where the old behaviour is: the muscle memory is the pan.
    /// </summary>
    private void OnNestedMarqueeStarted()
    {
        if (_viewModel.LeftDragHintShown || IsDiagnosticsRun)
        {
            return;
        }

        _viewModel.LeftDragHintShown = true;
        _ = _viewModel.Toast.ShowSuccessAsync(
            "Left-drag now selects. Pan with the right or middle button, Space+drag or the wheel — Canvas options ▸ Left drag.");
    }

    private void OnShellPropertyChangedForSelection(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.LeftDrag))
        {
            Nested.LeftDrag = _viewModel.LeftDrag;
        }
    }

    /// <summary>
    /// Canvas options ▸ Left drag: select an area (the default; the right or
    /// middle button, Space or the wheel pan) or pan (Shift+drag selects).
    /// </summary>
    private void AddLeftDragItems(ItemsControl menu)
    {
        var group = new MenuItem { Header = "Left drag" };
        foreach (var (choice, name, tip) in new[]
        {
            (NestedLeftDrag.SelectArea, "Select area", "Right, middle or Space+drag pans"),
            (NestedLeftDrag.Pan, "Pan", "Shift+drag selects an area")
        })
        {
            var item = new MenuItem
            {
                Header = name,
                IsCheckable = true,
                IsChecked = _viewModel.LeftDrag == choice,
                InputGestureText = tip
            };
            var chosen = choice;
            item.Click += (_, _) => _viewModel.LeftDrag = chosen;
            group.Items.Add(item);
        }

        menu.Items.Add(group);
    }

    /// <summary>
    /// A right-click on an item of the canvas: an item outside the selection
    /// becomes the selection, as in Explorer; one inside it keeps the whole
    /// set and takes the focus.  The menu then acts on everything selected -
    /// the Shell's own for items in one folder.  Its entries of our own read
    /// the focus's node, so that is waited for, and the menu is not shown if
    /// something else was selected meanwhile.
    /// </summary>
    private async Task ShowNestedItemMenuAsync(NestedHit target, Point point)
    {
        var tree = _viewModel.Tree;
        var selection = tree.Selection;
        var path = target.Path;
        if (!selection.Contains(path))
        {
            selection.ReplaceSingle(path, !target.IsFile, target.IsFile ? target.File.Length : 0, SelectionSource.Canvas);
        }
        else
        {
            selection.Apply(new SelectionEdit { Focus = path, Source = SelectionSource.Canvas });
        }

        var version = selection.Version;
        if (await tree.MaterializeAsync(path) is null)
        {
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                selection.Remove([path], SelectionSource.Command);
                await RefreshStaleAsync(path);
            }

            return;
        }

        if (selection.Version != version && !selection.Contains(path))
        {
            return;
        }

        _viewModel.ShowContextMenuFor(selection.Paths, Nested, point);
    }

    /// <summary>What a drag of <paramref name="path"/> carries: the whole selection if it is part of it, else the item alone.</summary>
    private string[] NestedDragPaths(string path)
    {
        var selection = _viewModel.Tree.Selection;
        return selection.Count > 1 && selection.Contains(path) ? [.. selection.Paths] : [path];
    }

    // ---- the folder list ------------------------------------------------------------

    /// <summary>
    /// The list box's selection changed.  Only a change the user made - a
    /// click, a key, Select-all - is the list's: it becomes one edit of the
    /// shared selection, held so the list does not wander into a folder it
    /// picked.  Without Ctrl the rows selected are the selection; with Ctrl
    /// only what the click added or removed changes.
    /// </summary>
    private void FolderListItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var dragging = _folderListMouseDown && Mouse.LeftButton == MouseButtonState.Pressed;
        if (_applyingListSelection || !_listUserInput && !dragging)
        {
            return;
        }

        var list = _viewModel.Tree.FolderList;
        var folder = list.FolderPath;
        if (folder.Length == 0 || e.AddedItems.Count == 0 && e.RemovedItems.Count == 0)
        {
            return;
        }

        var anchor = (FolderListItems.AnchorRow as FolderListItem)?.FullPath;
        var focus = FocusedListRow()?.FullPath
            ?? (e.AddedItems.Count > 0 ? (e.AddedItems[e.AddedItems.Count - 1] as FolderListItem)?.FullPath : null)
            ?? anchor;
        var edit = (Keyboard.Modifiers & ModifierKeys.Control) != 0
            ? new SelectionEdit
            {
                Container = folder,
                Added = ItemsOf(e.AddedItems),
                Removed = [.. e.RemovedItems.OfType<FolderListItem>().Select(row => row.FullPath)],
                Anchor = anchor,
                Focus = focus,
                Source = SelectionSource.List
            }
            : new SelectionEdit
            {
                Clear = true,
                Container = folder,
                Added = ItemsOf(FolderListItems.SelectedItems),
                Anchor = anchor,
                Focus = focus,
                Source = SelectionSource.List
            };

        using (list.HoldFolder())
        {
            _viewModel.Tree.Selection.Apply(edit);
        }
    }

    private static SelectionItem[] ItemsOf(IList rows)
    {
        var items = new SelectionItem[rows.Count];
        var count = 0;
        foreach (var row in rows)
        {
            if (row is FolderListItem item)
            {
                items[count++] = new SelectionItem(item.FullPath, item.IsDirectory, item.Entry.SizeBytes ?? 0);
            }
        }

        return count == items.Length ? items : items[..count];
    }

    /// <summary>The row with the keyboard focus in the list, if the focus is on a row.</summary>
    private FolderListItem? FocusedListRow() =>
        Keyboard.FocusedElement is ListBoxItem { DataContext: FolderListItem row }
        && ItemsControl.ItemsControlFromItemContainer((DependencyObject)Keyboard.FocusedElement) == FolderListItems
            ? row
            : null;

    /// <summary>
    /// The shared selection's rows lit in the list box, in one go: after a
    /// change made elsewhere, and after the rows were replaced - another
    /// folder, a filter, another order.
    /// </summary>
    private void ApplySelectionToList()
    {
        var list = _viewModel.Tree.FolderList;
        var rows = list.SelectedRows();
        var anchor = list.RowFor(_viewModel.Tree.Selection.Anchor);
        _applyingListSelection = true;
        try
        {
            FolderListItems.ApplySelection(rows as IList ?? rows.ToList(), anchor);
        }
        finally
        {
            _applyingListSelection = false;
        }

        // The row with the focus is brought into view, as Explorer's list follows its caret.
        if (list.RowFor(_viewModel.Tree.Selection.Focus) is { } focus)
        {
            FolderListItems.ScrollIntoView(focus);
        }
    }

    /// <summary>
    /// Marks the list box's next selection change as the user's: a press or
    /// a key on the list, noted until the input has been handled.
    /// </summary>
    private void NoteListInput()
    {
        _listUserInput = true;
        Dispatcher.InvokeAsync(() => _listUserInput = false, DispatcherPriority.Input);
    }

    private void OnFolderListPreviewKeyDown(object sender, KeyEventArgs e) => NoteListInput();

    private void OnFolderListPreviewMouseDown(object sender, MouseButtonEventArgs e) => NoteListInput();

    // ---- a look at it, for a test copy ---------------------------------------------

    /// <summary>
    /// A development aid, and only for a test copy of the window
    /// (<c>ULTRAEXPLORER_TEST_WINDOW=1</c>, which opens on a monitor nobody is
    /// using and never takes the keyboard): <c>--selection-demo &lt;folder&gt;
    /// [--selection-demo-path &lt;path&gt;]</c> draws a selection rectangle over
    /// a folder with the canvas's own synthetic mouse, lets it go, looks
    /// closer and further out, and picks a range and a toggle - saving a
    /// picture of the window at each step into the folder, so what the user
    /// would see can be looked at without anyone touching the mouse.
    /// </summary>
    private async Task RunSelectionDemoAsync()
    {
        if (!IsTestWindow || SwitchValue("--selection-demo") is not { Length: > 0 } output)
        {
            return;
        }

        Directory.CreateDirectory(output);
        var steps = new List<string>();

        // Never on the monitor the user is working on: a window found there
        // - moved, maximised - is hidden and the demo stops.
        bool Picture(string name)
        {
            if (!IsOffPrimaryMonitor())
            {
                Hide();
                steps.Add($"stopped before {name}: the window was on the primary monitor");
                File.WriteAllLines(Path.Combine(output, "done.txt"), steps);
                return false;
            }

            SaveDemoPicture(output, name);
            return true;
        }

        var path = SwitchValue("--selection-demo-path") ?? Environment.GetFolderPath(Environment.SpecialFolder.System);
        await Task.Delay(1500);
        if (!IsNested || await _nestedTree.RevealAsync(path) is not { } folder)
        {
            return;
        }

        // The list beside the canvas, to show the same selection.
        _viewModel.Tree.FolderList.IsVisible = true;
        Nested.FlyTo(folder, 0.92, animated: false);
        await Task.Delay(2500);
        if (!Picture("00-before.png"))
        {
            return;
        }

        // A rectangle from the folder's left margin, drawn slowly.
        var pointer = Nested.Pointer;
        if (Nested.ScreenRectOf(folder) is not { } cell)
        {
            return;
        }

        Point At(double x, double y) => new(cell.X + x * cell.Width, cell.Y + y * cell.Width);
        var start = At(0.012, 0.3);
        var end = At(0.58, 0.47);
        pointer.Down(MouseButton.Left, start);
        for (var step = 1; step <= 30; step++)
        {
            pointer.Move(start + (end - start) * step / 30);
            await Task.Delay(16);
        }

        await Task.Delay(400);
        if (!Picture("01-drawing.png"))
        {
            return;
        }
        pointer.Up(MouseButton.Left, end);
        steps.Add($"rectangle: {Nested.SelectedCount}");
        await Task.Delay(800);
        if (!Picture("02-selected.png"))
        {
            return;
        }

        // Closer: every selected tile outlined; further: the selection as a block.
        Nested.ZoomAt(At(0.3, 0.4), 4);
        await Task.Delay(1200);
        if (!Picture("03-closer.png"))
        {
            return;
        }
        Nested.FitAll(animated: false);
        Nested.ZoomAt(new Point(Nested.ActualWidth / 2, Nested.ActualHeight / 2), 1);
        await Task.Delay(1200);
        if (!Picture("04-overview.png"))
        {
            return;
        }

        // Close up again: a click, a Shift range, a Ctrl toggle.
        Nested.FlyTo(folder, 0.92, animated: false);
        Nested.ZoomAt(At(0.3, 0.4), 4);
        await Task.Delay(1200);
        if (Nested.ScreenRectOf(folder) is { } near && folder.FileGrid is { IsEmpty: false } files)
        {
            Point Tile(int index)
            {
                var (x, y) = files.Origin(index);
                return new Point(near.X + (x + files.TileWidth / 2) * near.Width, near.Y + (y + files.TileHeight / 2) * near.Width);
            }

            // Tiles well inside the view, clear of the trail along its top.
            var inner = new Rect(40, 80, Nested.ActualWidth - 80, Nested.ActualHeight - 160);
            var visible = Enumerable.Range(0, files.Count).Where(index => inner.Contains(Tile(index))).ToList();
            if (visible.Count > 12)
            {
                pointer.Click(Tile(visible[2]));
                steps.Add($"click: {Nested.SelectedCount}");
                pointer.Click(Tile(visible[9]), ModifierKeys.Shift);
                steps.Add($"shift+click: {Nested.SelectedCount}");
                pointer.Click(Tile(visible[5]), ModifierKeys.Control);
                steps.Add($"ctrl+click: {Nested.SelectedCount}");
                await Task.Delay(800);
                if (!Picture("05-range-and-toggle.png"))
        {
            return;
        }
            }
        }

        steps.Add(_viewModel.Tree.StatusCountText);
        steps.Add(Nested.RendererReason);
        steps.Add($"window {Left:0},{Top:0} {ActualWidth:0}x{ActualHeight:0} {WindowState}, off the primary monitor: {IsOffPrimaryMonitor()}");
        File.WriteAllLines(Path.Combine(output, "done.txt"), steps);
    }

    /// <summary>Whether the window is on a monitor other than the primary one.</summary>
    private bool IsOffPrimaryMonitor()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        return handle != IntPtr.Zero && GetMonitorInfo(MonitorFromWindow(handle, 2), ref info) && (info.Flags & 1) == 0;
    }

    /// <summary>The window as it is drawn now, at its own scale, into <paramref name="folder"/>.</summary>
    private void SaveDemoPicture(string folder, string name)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name));
        encoder.Save(stream);
    }
}
