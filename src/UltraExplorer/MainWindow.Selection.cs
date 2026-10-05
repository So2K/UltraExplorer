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
    private bool _applyingListSelection;
    private bool _listUserInput;
    private bool _listSelectAll;

    private void AttachSelection()
    {
        _viewModel.Tree.Selection.Changed += OnSharedSelectionChanged;
        _viewModel.Tree.FolderList.SelectionRowsChanged += ApplySelectionToList;
        _viewModel.Tree.FolderList.RowsReplaced += ApplySelectionToList;
        _viewModel.PropertyChanged += OnShellPropertyChangedForSelection;
        FolderListItems.PreviewKeyDown += OnFolderListPreviewKeyDown;
        FolderListItems.PreviewMouseRightButtonDown += OnFolderListPreviewMouseDown;
    }

    private void DetachSelection()
    {
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
    //
    // A gesture on a canvas is applied to the shared selection by its pane
    // (NestedPane), in the same call.

    /// <summary>
    /// Any change of the shared selection: the pane being worked with, whose
    /// selection it is, takes it in - its headers, and its canvas unless the
    /// change is the canvas's own or the canvas is not the picture on show.
    /// </summary>
    private void OnSharedSelectionChanged(ItemSelection selection)
    {
        ActivePane.OnSharedSelectionChanged(selection);
        if (IsPickerMode && IsNested && _picker is { } session)
        {
            if (selection.Focus is { } focus && selection.TryGetItem(focus, out var item))
            {
                if (!item.IsDirectory) session.CurrentFolder = Path.GetDirectoryName(item.Path) ?? session.CurrentFolder;
                else if (selection.LastSource == SelectionSource.Navigation) session.CurrentFolder = item.Path;
            }
            session.ReportSelection(selection.Items);
        }
    }

    private void OnShellPropertyChangedForSelection(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.LeftDrag))
        {
            foreach (var pane in _panes)
            {
                pane.Canvas.LeftDrag = _viewModel.LeftDrag;
            }
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

        var edit = ListEdit(
            _viewModel.Tree.Selection,
            folder,
            e.AddedItems,
            e.RemovedItems,
            FolderListItems.SelectedItems,
            FolderListItems.AnchorRow as FolderListItem,
            FocusedListRow(),
            Keyboard.Modifiers,
            _listSelectAll);

        using (list.HoldFolder())
        {
            _viewModel.Tree.Selection.Apply(edit);
        }
    }

    /// <summary>
    /// The edit one change of the list box's selection makes of the shared
    /// <paramref name="selection"/>: the rows selected, with Ctrl only what
    /// was added and removed.  Select-all is every row in place of what was
    /// selected, Ctrl or not.
    ///
    /// <para>The rows are counted in <paramref name="folder"/>, the list's,
    /// only while they are in it: the next folder's name is up before its
    /// rows are read, and a row of the one before picked meanwhile is in its
    /// own folder.</para>
    /// </summary>
    internal static SelectionEdit ListEdit(
        ItemSelection selection,
        string folder,
        IList added,
        IList removed,
        IList selected,
        FolderListItem? anchorRow,
        FolderListItem? focusedRow,
        ModifierKeys modifiers,
        bool selectAll)
    {
        // The list box moves its anchor only once the click or the key has
        // been handled, after it has told of the change: a click, a
        // Ctrl+click or an arrow puts it on the row with the focus, while
        // Shift and Select-all leave it where it is.
        var keepsAnchor = (modifiers & ModifierKeys.Shift) != 0 || selectAll;
        var anchor = (keepsAnchor ? anchorRow : focusedRow ?? anchorRow)?.FullPath;
        var focus = focusedRow?.FullPath
            ?? (added.Count > 0 ? (added[added.Count - 1] as FolderListItem)?.FullPath : null)
            ?? anchor;
        if ((modifiers & ModifierKeys.Control) == 0 || selectAll)
        {
            return new SelectionEdit
            {
                Clear = true,
                Container = RowsAreIn(folder, selected) ? folder : null,
                Added = ItemsOf(selected),
                Anchor = anchor,
                Focus = focus,
                Source = SelectionSource.List
            };
        }

        // Rows added inside a folder that is selected itself - the folder
        // gone into, which going there selects - take that folder and every
        // selected folder above it out: kept, a Delete or a Move would act on
        // the whole folder rather than on what was picked in it.
        List<string> taken = [.. removed.OfType<FolderListItem>().Select(row => row.FullPath)];
        var above = added.Count > 0 && TakeFoldersAbove(selection, folder, taken);
        return new SelectionEdit
        {
            Container = !above && RowsAreIn(folder, added) && RowsAreIn(folder, removed) ? folder : null,
            Added = ItemsOf(added),
            Removed = taken,
            Anchor = anchor,
            Focus = focus,
            Source = SelectionSource.List
        };
    }

    /// <summary>
    /// Adds <paramref name="folder"/> and every folder above it that
    /// <paramref name="selection"/> holds to <paramref name="removed"/>;
    /// true when it held any.
    /// </summary>
    private static bool TakeFoldersAbove(ItemSelection selection, string folder, List<string> removed)
    {
        var any = false;
        for (var at = folder; at.Length > 0; at = ItemSelection.ParentOf(at).ToString())
        {
            if (selection.Contains(at))
            {
                removed.Add(at);
                any = true;
            }
        }

        return any;
    }

    /// <summary>Whether every row is directly inside <paramref name="folder"/>.</summary>
    private static bool RowsAreIn(string folder, IList rows)
    {
        foreach (var row in rows)
        {
            if (row is FolderListItem item && !ItemSelection.ParentOf(item.FullPath).Equals(folder, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
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
        Dispatcher.InvokeAsync(() => _listUserInput = _listSelectAll = false, DispatcherPriority.Input);
    }

    /// <summary>A key on the list, noted as the user's; Ctrl+A is the list box's Select-all.</summary>
    private void OnFolderListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        NoteListInput();
        _listSelectAll = e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control;
    }

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
        if (!IsNested || await FirstPane.Tree.RevealAsync(path) is not { } folder)
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
