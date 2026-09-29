using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace UltraExplorer;

/// <summary>
/// The nested canvas's side of the window.
///
/// The canvas draws folders; everything a folder can be asked to do - select
/// it, open its menu, copy it, rename it, show its files in the list - still
/// goes through the tree view model, exactly as a click on a tree node does.
/// Clicking a cell selects its path there, and whatever the rest of the window
/// selects (the address bar, the list, back and forward, a search result) is
/// mirrored back onto the canvas and flown to.  So the two pictures are two
/// views of one selection, and every command works in both.
/// </summary>
public partial class MainWindow
{
    private static readonly Color NoteBeaconColour = Color.FromRgb(0xC8, 0xD2, 0xDC);
    private static readonly Color PinBeaconColour = Color.FromRgb(0xFF, 0xD6, 0x6B);
    private static readonly Color ActiveBeaconColour = Color.FromRgb(0x60, 0xCD, 0xFF);
    private static readonly Color SearchBeaconColour = Color.FromRgb(0x4C, 0xC9, 0xD8);
    private const int SearchBeaconLimit = 100;

    private readonly NestedTree _nestedTree = new();
    private readonly List<NestedRoot> _nestedDrives = [];
    private DispatcherTimer? _nestedSaveTimer;
    private DispatcherTimer? _beaconTimer;
    private string[]? _nestedDragPaths;
    private bool _nestedReady;
    private DispatcherTimer? _filterTimer;
    private const int FilterBeaconLimit = 150;
    private bool _nestedCameraRestored;

    /// <summary>Slices of the nested tree's background pass, waiting for frames to run in (see <see cref="PostSortSlice"/>).</summary>
    private readonly Queue<Action> _sortSlices = new();
    private bool _sortSlicesHooked;
    private TimeSpan _lastSortSliceFrame = TimeSpan.MinValue;

    private bool IsNested => _viewModel.IsNestedLayout;

    /// <summary>
    /// The canvas that is showing, for keyboard focus.  A test copy or a
    /// diagnostics run that is not the active window only has the canvas
    /// remembered as where the keyboard goes when it is clicked into: moving
    /// the keyboard there now would activate the window, and take the
    /// keyboard from whatever the user is typing into on the other screen.
    /// </summary>
    private void FocusCanvas()
    {
        UIElement canvas = IsNested ? Nested : Editor;
        if (!IsActive && (IsTestWindow || IsDiagnosticsRun))
        {
            FocusManager.SetFocusedElement(this, canvas);
            return;
        }

        canvas.Focus();
    }

    private void AttachNested()
    {
        _nestedTree.PostBackground = PostSortSlice;

        // Changes on disk: before the drives go in, so every drive takes its
        // watch from the hub.  The tree registers what it reads; the canvas
        // takes the hub's changes in at the start of its frames and hands them
        // to the tree view model, which gives the tree its own.
        _nestedTree.Changes = _viewModel.Changes;
        _viewModel.Tree.NestedChanges = _nestedTree;
        Nested.AttachChanges(_viewModel.Changes, _viewModel.Tree);
        AttachDevices();

        Nested.Tree = _nestedTree;
        Nested.MarkLookup = _viewModel.Marks.Get;
        Nested.IconLookup = LookUpFileIcon;
        Nested.IconArrivals = _viewModel.Icons.CanvasArrivals;
        _nestedTree.FolderLoaded += OnFolderLoadedForIcons;
        Nested.OpenRequested += OnNestedOpenRequested;
        Nested.ContextMenuRequested += OnNestedContextMenuRequested;
        Nested.ContextMenuPressed += OnNestedContextMenuPressed;
        Nested.DragRequested += OnNestedDragRequested;
        Nested.CameraChanged += OnNestedCameraChanged;
        Nested.FilterChanged += OnNestedFilterChanged;

        _filterTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(140) };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            Nested.SetFilter(CanvasFilterBox.Text);
        };

        _nestedSaveTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(400) };
        _nestedSaveTimer.Tick += (_, _) =>
        {
            _nestedSaveTimer.Stop();
            _viewModel.Tree.NestedCamera = Nested.CaptureCamera() ?? _viewModel.Tree.NestedCamera;
            _viewModel.Tree.ScheduleSave();
        };

        _beaconTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _beaconTimer.Tick += (_, _) =>
        {
            _beaconTimer.Stop();
            RebuildBeacons();
        };

        _viewModel.PropertyChanged += OnShellPropertyChangedForNested;
        _viewModel.Tree.PropertyChanged += OnTreePropertyChangedForNested;
        _viewModel.Tree.DeepRefreshRequested += OnTreeDeepRefreshRequested;
        _viewModel.QuickAccess.CollectionChanged += OnBeaconSourceChanged;
        _viewModel.Search.PropertyChanged += OnSearchPropertyChangedForNested;
        _viewModel.Marks.MarkChanged += OnMarkChangedForNested;
        UpdateSortHeaders();
    }

    private void DetachNested()
    {
        _nestedSaveTimer?.Stop();
        _beaconTimer?.Stop();
        _viewModel.PropertyChanged -= OnShellPropertyChangedForNested;
        _viewModel.Tree.PropertyChanged -= OnTreePropertyChangedForNested;
        _viewModel.Tree.DeepRefreshRequested -= OnTreeDeepRefreshRequested;
        _viewModel.QuickAccess.CollectionChanged -= OnBeaconSourceChanged;
        _viewModel.Search.PropertyChanged -= OnSearchPropertyChangedForNested;
        _viewModel.Marks.MarkChanged -= OnMarkChangedForNested;
        _nestedTree.FolderLoaded -= OnFolderLoadedForIcons;
        Nested.IconArrivals = null;
        Nested.AttachChanges(null, null);
        DetachDevices();
        _viewModel.Tree.NestedChanges = null;
        _nestedTree.Changes = null;
        _sortSlices.Clear();
        UnhookSortSlices();
        _nestedTree.Dispose();
    }

    /// <summary>
    /// How the nested tree's background pass after a change of order runs in
    /// the window: one slice per frame, straight after the frame is drawn,
    /// rather than in whatever time is left between frames.  A slice queued
    /// below rendering fills every gap there is, so when a frame falls due
    /// one is often half way through, and the frame waits for it: over the
    /// bench's clicks through the headers on System32 that made about twenty
    /// of its hundred and twenty frames late by a whole refresh.  Right after
    /// a frame, a slice of a few milliseconds is done long before the next
    /// one is due.
    ///
    /// After the frame rather than inside it, because the frame places what
    /// it draws itself (see <see cref="NestedCanvas"/>): the slice then comes
    /// second every time, and the tree takes what the frame spent placing off
    /// the slice's allowance, so the two together stay within one slice's
    /// worth.  Inside the frame they ran in whichever order their handlers
    /// happened to be hooked, and a slice that came first was a whole slice
    /// on top of the frame's own placing.
    ///
    /// While the nested canvas is not on screen the slices wait: nothing is
    /// drawn from the tree then, and whatever is drawn when it comes back is
    /// placed as it is drawn.  A pass of a few dozen slices takes that many
    /// frames; nobody waits for it.
    /// </summary>
    private void PostSortSlice(Action slice)
    {
        _sortSlices.Enqueue(slice);
        HookSortSlices();
    }

    /// <summary>Starts running waiting slices, one a frame, if the nested canvas is on screen to run them for.</summary>
    private void HookSortSlices()
    {
        if (!_sortSlicesHooked && IsNested && _sortSlices.Count > 0)
        {
            _sortSlicesHooked = true;
            CompositionTarget.Rendering += OnSortSliceFrame;
        }
    }

    private void UnhookSortSlices()
    {
        if (_sortSlicesHooked)
        {
            _sortSlicesHooked = false;
            CompositionTarget.Rendering -= OnSortSliceFrame;
        }
    }

    private void OnSortSliceFrame(object? sender, EventArgs e)
    {
        // WPF raises Rendering more than once a frame when asked to: one
        // slice per real frame, told apart by the frame's time.
        if (e is RenderingEventArgs { RenderingTime: var time })
        {
            if (time == _lastSortSliceFrame)
            {
                return;
            }

            _lastSortSliceFrame = time;
        }

        // Gone to the tree canvas: the rest waits for the nested one to be back.
        if (!IsNested)
        {
            UnhookSortSlices();
            return;
        }

        if (_sortSlices.TryDequeue(out var slice))
        {
            // Queued at render priority from inside the frame, it runs as soon
            // as the frame has been drawn and handed to the screen, before
            // anything else waiting.  Queues the next slice itself while there
            // is work left.
            Dispatcher.InvokeAsync(slice, DispatcherPriority.Render);
        }

        if (_sortSlices.Count == 0)
        {
            UnhookSortSlices();
        }
    }

    /// <summary>
    /// Runs once the shell has loaded its state: the drives go in as the first
    /// row of cells, the hidden-folder rules are copied over, and the camera is
    /// put back where the last session left it.
    /// </summary>
    private Task InitializeNestedAsync()
    {
        // A file dialog always shows the tree; it never needs this canvas.
        if (IsPickerMode)
        {
            return Task.CompletedTask;
        }

        // The drives the tree already found, not a second scan of them: a
        // drive that is slow to answer would otherwise hold up startup twice.
        _nestedDrives.Clear();
        _nestedDrives.AddRange(_viewModel.Tree.Roots
            .Where(root => root.IsDrive)
            .Select(root => new NestedRoot(root.FullPath, root.DisplayName, NestedFolderKind.Drive, root.SecondaryText)));

        // The remembered orders before the first drive goes in, so nothing is
        // ever placed in name order only to be placed again.  The same orders
        // as the tree and the list: a folder sorted anywhere is sorted everywhere.
        _nestedTree.Orders = _viewModel.Orders;
        SyncNestedRoots();
        _nestedTree.IncludeHidden = _viewModel.Tree.ShowHiddenItems;
        _nestedTree.SetUserHidden(_viewModel.Tree.HiddenPaths);
        SyncNestedSelection();
        _nestedReady = true;
        _viewModel.NestedZoomLabel = Nested.ZoomText;
        if (IsNested)
        {
            EnterNested(fromStartup: true);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The nested canvas coming into view, at startup or from the tree.  Its
    /// marks are gathered only now - while it is hidden it reads nothing - and
    /// the camera goes back to where it was, or to the selection.  Deferred to
    /// after layout, so the view is framed for the size it really has.
    /// </summary>
    private void EnterNested(bool fromStartup)
    {
        if (!_nestedReady)
        {
            return;
        }

        SyncNestedSelection();
        RebuildBeacons();
        Dispatcher.InvokeAsync(() =>
        {
            Nested.UpdateLayout();
            if (!_nestedCameraRestored && _viewModel.Tree.RestoredNestedCamera is { } camera)
            {
                // Not awaited: the window is usable while the folders on the
                // way are read, and the view jumps there once they have been.
                _nestedCameraRestored = true;
                _ = Nested.RestoreCameraAsync(camera);
            }
            else if (!string.IsNullOrEmpty(_viewModel.Tree.ActivePath))
            {
                _ = FlyNestedToAsync(_viewModel.Tree.ActivePath, gentle: !fromStartup, animated: false);
            }

            FocusCanvas();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>A share or distribution the workspace lists answered after the start: the nested canvas shows it too.</summary>
    private void OnExtraRootAdded(ViewAllNodeViewModel root)
    {
        if (_nestedReady)
        {
            SyncNestedRoots();
        }
    }

    /// <summary>Drives, plus every share and WSL distribution the tree has as a root of its own.</summary>
    private void SyncNestedRoots()
    {
        var roots = new List<NestedRoot>(_nestedDrives);
        foreach (var root in _viewModel.Tree.Roots.Where(root => !root.IsDrive))
        {
            if (roots.All(existing => !ViewAllPath.Equals(existing.FullPath, root.FullPath)))
            {
                roots.Add(new NestedRoot(root.FullPath, root.DisplayName, NestedFolderKind.Drive));
            }
        }

        _nestedTree.SetRoots(roots);
    }

    // ---- selection, both ways ---------------------------------------------
    //
    // The canvas's gestures reach the shared selection as edits, and every
    // other change reaches the canvas, in MainWindow.Selection.cs.

    private async Task RefreshStaleAsync(string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } parent && _nestedTree.Find(parent) is { } folder)
        {
            try
            {
                await _nestedTree.RefreshAsync(folder);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
            {
            }
        }
    }

    /// <summary>
    /// A double-click on a folder has already flown into it.  A file opens -
    /// through the tree, so a file dialog's own "this is the answer" still
    /// applies.  A link has nothing inside it to fly into, so it goes where it
    /// points instead.
    /// </summary>
    private async void OnNestedOpenRequested(NestedHit hit)
    {
        if (hit.IsFile)
        {
            try
            {
                if (await _viewModel.Tree.SelectPathAsync(hit.Path) is { } node)
                {
                    await _viewModel.Tree.ToggleAsync(node);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _viewModel.Toast.ShowError(ex.Message);
            }

            return;
        }

        var folder = hit.Folder;
        if (!folder.IsReparsePoint)
        {
            return;
        }

        try
        {
            var target = Directory.ResolveLinkTarget(folder.FullPath, returnFinalTarget: true)?.FullName;
            if (!string.IsNullOrEmpty(target) && Directory.Exists(target))
            {
                await _viewModel.Tree.RevealPathAsync(target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _viewModel.Toast.ShowError($"Could not follow {folder.Name}: {ex.Message}");
        }
    }

    private void OnTreePropertyChangedForNested(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewAllViewModel.ActivePath):
                SyncNestedSelection();
                ScheduleBeacons();

                // On the tree the active folder is the current one when nothing is selected.
                UpdateSortHeaders();
                break;
            case nameof(ViewAllViewModel.HiddenPaths):
                _nestedTree.SetUserHidden(_viewModel.Tree.HiddenPaths);
                break;
            case nameof(ViewAllViewModel.ShowHiddenItems):
                _nestedTree.IncludeHidden = _viewModel.Tree.ShowHiddenItems;
                break;
        }
    }

    private void OnShellPropertyChangedForNested(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsNestedLayout):
                // Switching pictures keeps the place: the other canvas opens on
                // whatever is selected.
                if (IsNested)
                {
                    _viewModel.Tree.PreferLightReveal = true;
                    _viewModel.Tree.IsCanvasShown = false;
                    EnterNested(fromStartup: false);

                    // An order chosen while the tree canvas was showing: its
                    // pass over the nested tree waited, and goes on now.
                    HookSortSlices();
                }
                else
                {
                    _ = EnterTreeAsync();
                }

                // The other picture decides the current folder its own way.
                UpdateSortHeaders();
                break;
            case nameof(MainViewModel.Sort):
                // Any order changed.  The tree follows the orders itself: the
                // canvas keeps what it is looking at where it is, and the tree
                // places what is on screen at once and the rest behind it.
                UpdateSortHeaders();
                break;
            case nameof(MainViewModel.Layers):
                // The files come and go through the tree's pass, like a change
                // of the way grids fill; the rest is only drawn again.
                Nested.ShownLayers = _viewModel.Layers;
                ScheduleBeacons();
                break;
        }
    }

    // ---- the order ---------------------------------------------------------------

    /// <summary>
    /// A header over the canvas: sort the current folder (<see cref="SortFolder"/>)
    /// by its column, or turn its order round if it already is - or every
    /// folder, when folders are all sorted the same or there is no folder.
    /// </summary>
    private void SortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<SortColumn>(tag, out var column))
        {
            var folder = SortFolder();
            var orders = _viewModel.Orders;
            orders.Choose(folder, orders.SortOf(folder).Click(column));
        }
    }

    /// <summary>
    /// The folder the headers show and change the order of, and Canvas
    /// options' Sort by with them.  With several things selected, the one
    /// with the focus decides.  Null for none - This PC in view with nothing
    /// selected - where a header orders every folder.
    ///
    /// On the nested canvas: the folder being worked with - the folder
    /// selected, or the one the selected file is in - else the folder in
    /// view (see <see cref="NestedSortFolder"/>).  A sub-folder picked is
    /// the one sorted, not the folder around it: whoever selects a folder
    /// and clicks a header means that folder's contents.
    ///
    /// On the tree: the folder selected, whose children open out around it,
    /// else the folder the selected files are in, else the active one.
    /// </summary>
    internal string? SortFolder()
    {
        // Worked out once per change of the selection: this is asked after
        // every move of the camera.
        var selection = _viewModel.Tree.Selection;
        if (selection.Version != _sortSelectionVersion)
        {
            _sortSelectionVersion = selection.Version;
            _sortSelectionFolder = null;
            if (selection.Count > 0)
            {
                var primary = selection.Focus is { } focus && selection.Contains(focus) ? focus : selection.Paths[0];
                if (selection.TryGetItem(primary, out var item))
                {
                    _sortSelectionFolder = item.IsDirectory ? primary : Path.GetDirectoryName(primary);
                }
            }
        }

        if (IsNested)
        {
            return NestedSortFolder(_sortSelectionFolder, Nested.FolderInView?.FullPath);
        }

        if (_sortSelectionFolder is { } selected)
        {
            return selected;
        }

        return _viewModel.Tree.ActiveNode is { } node
            ? node.IsDirectory ? node.FullPath : node.Parent?.FullPath
            : null;
    }

    /// <summary>
    /// The nested canvas's rule for the folder the headers sort, from the
    /// folder the selection names - the folder selected, or the one the
    /// selected file is in - and the folder in view: the selection's while
    /// it is the folder in view or inside it, the folder in view otherwise.
    /// A drive selected at the start, or a file picked in another folder an
    /// hour ago, is not what anybody sorting the folder in front of them
    /// means.  With no folder in view - an overview of This PC - the
    /// selection's, or null, every folder, when nothing is selected.
    /// </summary>
    internal static string? NestedSortFolder(string? selected, string? inView) =>
        selected is not null && (inView is null || IsSameOrInside(selected, inView)) ? selected : inView;

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or somewhere inside it.</summary>
    private static bool IsSameOrInside(string path, string folder) =>
        path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
        && (path.Length == folder.Length || folder.EndsWith(Path.DirectorySeparatorChar) || path[folder.Length] == Path.DirectorySeparatorChar);

    private long _sortSelectionVersion = -1;

    /// <summary>The selected folder, or the folder the selected file is in: what the headers sort, on the nested canvas while it is in view.</summary>
    private string? _sortSelectionFolder;

    // What the headers last showed: they are brought up to date on every
    // move of the camera, and nearly always nothing they show has changed.
    private string? _headerFolder;
    private ItemSort _headerSort;
    private SortScope _headerScope;
    private bool _headersShown;

    /// <summary>
    /// Lights the header the current folder is ordered by and points its
    /// arrow the way the order runs - up for A to Z, oldest or smallest
    /// first; down for the other way - as Explorer's column headers do.
    /// Called whenever the folder or an order may have changed; does nothing
    /// when neither did.
    /// </summary>
    private void UpdateSortHeaders()
    {
        // The search puts first what is in the folder these headers are for.
        if (_viewModel.Search.IsOpen)
        {
            _viewModel.Search.NoteFolder(SortFolder());
        }

        var orders = _viewModel.Orders;
        var folder = orders.Scope == SortScope.AllFolders ? null : SortFolder();
        var sort = orders.SortOf(folder);
        if (_headersShown && sort == _headerSort && orders.Scope == _headerScope
            && string.Equals(folder, _headerFolder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _headersShown = true;
        _headerFolder = folder;
        _headerSort = sort;
        _headerScope = orders.Scope;
        var where = folder is null
            ? orders.Scope == SortScope.AllFolders ? string.Empty : " in every folder"
            : $" in {FolderName(folder)}";
        (Button Header, TextBlock Arrow, SortColumn Column)[] headers =
        [
            (SortByName, SortByNameArrow, SortColumn.Name),
            (SortByModified, SortByModifiedArrow, SortColumn.Modified),
            (SortByType, SortByTypeArrow, SortColumn.Type),
            (SortBySize, SortBySizeArrow, SortColumn.Size)
        ];

        foreach (var (header, arrow, column) in headers)
        {
            var name = ItemSort.Describe(column);
            if (column == sort.Column)
            {
                header.Foreground = (Brush)FindResource("TextBrush");
                arrow.Text = sort.Descending ? "\uE70D" : "\uE70E";
                arrow.Visibility = Visibility.Visible;
                header.ToolTip = $"Sorted by {name}, {ItemSort.DescribeDirection(column, sort.Descending)}{where} (click to reverse)";
            }
            else
            {
                // Back to the style's muted text, which its hover can light.
                header.ClearValue(ForegroundProperty);
                arrow.Visibility = Visibility.Hidden;
                header.ToolTip = $"Sort by {name}{where} (click again to reverse)";
            }
        }
    }

    /// <summary>A folder's name as a menu or a tip says it: its own name, or the whole of a drive's.</summary>
    private static string FolderName(string path) =>
        Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : path;

    /// <summary>
    /// Back to the tree: what is selected was only brought in by name, so it
    /// is revealed properly - its folders opened - before the tree is shown,
    /// and the reveal itself brings it into view.
    /// </summary>
    private async Task EnterTreeAsync()
    {
        var tree = _viewModel.Tree;
        tree.PreferLightReveal = false;
        try
        {
            if (!string.IsNullOrEmpty(tree.ActivePath))
            {
                await tree.RevealPathAsync(tree.ActivePath);
            }
        }
        finally
        {
            tree.IsCanvasShown = true;
        }

        await Dispatcher.InvokeAsync(FocusCanvas, DispatcherPriority.Input);
    }

    private void SyncNestedSelection() => Nested.LoadSelection(_viewModel.Tree.Selection);

    /// <summary>
    /// The nested version of "bring this node into view".  Navigation - the
    /// address bar, the sidebar, back and forward - flies to the folder.  A row
    /// picked in the list only needs to be visible, and usually already is.
    /// </summary>
    private async Task FlyNestedToAsync(string path, bool gentle, bool animated = true)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var folderPath = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        var folder = await _nestedTree.RevealAsync(folderPath);
        if (folder is null)
        {
            // A share or a WSL distribution the tree has only just added.
            SyncNestedRoots();
            folder = await _nestedTree.RevealAsync(folderPath);
            if (folder is null)
            {
                return;
            }
        }

        var view = new Rect(0, 0, Nested.ActualWidth, Nested.ActualHeight);
        if (Nested.ScreenRectOf(folder) is { } rect)
        {
            if (gentle && rect.Width >= 24 && view.IntersectsWith(rect))
            {
                return;
            }

            if (!gentle && view.Contains(rect) && rect.Width >= view.Width * 0.45)
            {
                return;
            }
        }

        if (gentle && folder.Parent is { IsComputer: false } parent)
        {
            Nested.FlyTo(parent, 0.92, animated);
        }
        else
        {
            Nested.FlyTo(folder, 0.8, animated);
        }
    }

    private void OnNestedCameraChanged()
    {
        _viewModel.NestedZoomLabel = Nested.ZoomText;

        // With nothing selected, the headers are for the folder in view.
        UpdateSortHeaders();
        _nestedSaveTimer?.Stop();
        _nestedSaveTimer?.Start();
    }

    private void CaptureNestedCamera()
    {
        if (Nested.CaptureCamera() is { } camera)
        {
            _viewModel.Tree.NestedCamera = camera;
        }
    }

    /// <summary>
    /// F5: the folder is read again at once, and whatever the canvas read below
    /// it is out of date too - read again as it is drawn.  A change on disk, or
    /// a file operation of the window's own, needs nothing from here: the
    /// change hub brings it to the tree like any other.
    /// </summary>
    private void OnTreeDeepRefreshRequested(string path)
    {
        if (_nestedTree.Find(path) is { } folder)
        {
            _nestedTree.RefreshDeep(folder);
        }
    }

    // ---- beacons -------------------------------------------------------------

    private void OnBeaconSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleBeacons();

    /// <summary>The search's results are beacons on the canvas while its panel is up.</summary>
    private void OnSearchPropertyChangedForNested(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SearchViewModel.IsOpen) or nameof(SearchViewModel.Results))
        {
            ScheduleBeacons();
        }
    }

    private void OnMarkChangedForNested(string path, FolderMark mark)
    {
        // Raised on whichever thread set the mark.
        Dispatcher.InvokeAsync(() =>
        {
            Nested.InvalidateMarks();
            ScheduleBeacons();
        });
    }

    private void ScheduleBeacons()
    {
        // Hidden, the canvas has no use for marks, and gathering them would
        // read every folder on the way to each one for nothing.
        if (!IsNested || !_nestedReady)
        {
            return;
        }

        _beaconTimer?.Stop();
        _beaconTimer?.Start();
    }

    /// <summary>
    /// Everything the user has put on a folder, gathered into one list of
    /// beacons: colours and notes from the mark store, folders pinned to Home,
    /// the folder that is selected, and while a search is open, what it found.
    /// A mark on a file shows on the folder it is in.  With the marks layer
    /// off, the colours, notes and pins are left out.
    /// </summary>
    private void RebuildBeacons()
    {
        var marks = _viewModel.IsLayerShown(CanvasLayer.Marks);
        var beacons = new Dictionary<string, (NestedBeaconKind Kind, Color Colour, string Label, string Note)>(StringComparer.OrdinalIgnoreCase);

        void Add(string path, NestedBeaconKind kind, Color? colour, string label, string note = "")
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (beacons.TryGetValue(path, out var existing))
            {
                beacons[path] = (
                    existing.Kind | kind,
                    existing.Kind.HasFlag(NestedBeaconKind.Colour) ? existing.Colour : colour ?? existing.Colour,
                    existing.Label,
                    existing.Note.Length > 0 ? existing.Note : note);
                return;
            }

            beacons[path] = (kind, colour ?? NoteBeaconColour, label, note);
        }

        foreach (var (path, mark) in marks ? _viewModel.Marks.Snapshot() : [])
        {
            var kind = NestedBeaconKind.None;
            Color? colour = null;
            if (!string.IsNullOrEmpty(mark.AccentHex) && TryParse(mark.AccentHex, out var parsed))
            {
                kind |= NestedBeaconKind.Colour;
                colour = parsed;
            }

            if (!string.IsNullOrWhiteSpace(mark.Note))
            {
                kind |= NestedBeaconKind.Note;
            }

            if (kind != NestedBeaconKind.None)
            {
                Add(path, kind, colour, LeafName(path), mark.Note);
            }
        }

        foreach (var pinned in _viewModel.QuickAccess.Where(item => marks && item.IsCustom))
        {
            Add(pinned.Path, NestedBeaconKind.Pinned, PinBeaconColour, pinned.Name);
        }

        if (Nested.IsFiltering)
        {
            foreach (var match in Nested.FilterMatches.Take(FilterBeaconLimit))
            {
                Add(match, NestedBeaconKind.Search, SearchBeaconColour, LeafName(match));
            }
        }

        if (_viewModel.Search.IsOpen)
        {
            foreach (var result in _viewModel.Search.Results.Take(SearchBeaconLimit))
            {
                Add(result.FullPath, NestedBeaconKind.Search, SearchBeaconColour, result.Name);
            }
        }

        if (_viewModel.Tree.ActiveNode is { } active)
        {
            Add(active.FullPath, NestedBeaconKind.Active, ActiveBeaconColour, active.DisplayName);
        }

        Nested.SetBeacons([.. beacons.Select(pair => new NestedBeacon(pair.Key, pair.Value.Kind, pair.Value.Colour, pair.Value.Label, pair.Value.Note))]);
    }

    private static string LeafName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static bool TryParse(string hex, out Color colour)
    {
        colour = default;
        if (!NestedCanvas.IsHexColour(hex))
        {
            return false;
        }

        try
        {
            colour = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    // ---- menus -----------------------------------------------------------------

    /// <summary>
    /// Right-click on a folder's title is the folder's own Windows menu.
    /// Right-click in the open space of a big folder is "in" that folder: the
    /// Windows menu of its open space - New, Paste - with the folder's own
    /// settings.  Outside every cell is the canvas's own menu.
    /// </summary>
    private async void OnNestedContextMenuRequested(NestedHit? hit, bool onBackground, Point point)
    {
        var tree = _viewModel.Tree;
        try
        {
            if (hit is not { } target)
            {
                DropPreparedMenu();
                ShowFolderAreaMenu(Nested, null);
                return;
            }

            if (onBackground)
            {
                // The folder clicked in becomes the selection, as a click on the
                // empty part of an Explorer window makes it the current folder.
                var folder = target.Folder.FullPath;
                tree.Selection.ReplaceSingle(folder, true, 0, SelectionSource.Canvas);
                if (ShowFolderAreaShellMenu(folder, Nested, point))
                {
                    return;
                }

                var area = await tree.MaterializeAsync(folder);
                ShowFolderAreaMenu(Nested, area);
                return;
            }

            // The whole selection when the item is part of it (see
            // MainWindow.Selection.cs).
            await ShowNestedItemMenuAsync(target, point);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _viewModel.Toast.ShowError(ex.Message);
        }
    }

    /// <summary>
    /// The right button went down on the canvas: the menu its release would
    /// show - the selection's when the item is part of it, the item's alone
    /// otherwise, or the open space of the folder - is built while it is held.
    /// </summary>
    private void OnNestedContextMenuPressed(NestedHit? hit, bool onBackground, Point point)
    {
        if (hit is not { } target)
        {
            return;
        }

        if (onBackground)
        {
            PrepareShellMenu(background: true, [target.Folder.FullPath]);
            return;
        }

        var selection = _viewModel.Tree.Selection;
        PrepareShellMenu(background: false, selection.Contains(target.Path) ? selection.Paths : [target.Path]);
    }

    // ---- drag and drop ---------------------------------------------------------

    /// <summary>A folder or file picked up: the selection if it is part of one, otherwise just it.</summary>
    private void OnNestedDragRequested(string path)
    {
        if (IsPickerMode)
        {
            return;
        }

        var paths = NestedDragPaths(path);
        _nestedDragPaths = paths;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, paths);
            DragDrop.DoDragDrop(Nested, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            _nestedDragPaths = null;
            Nested.DropTarget = null;
        }

        // Moved somewhere else - into Explorer, onto the desktop - the items
        // are gone from where they were, and nothing else will say so.
        // Explorer often finishes a move after the drop has returned, so the
        // folders are looked at again now and once more a little later.
        _ = RefreshSourcesAsync(paths);
    }

    private async Task RefreshSourcesAsync(IReadOnlyList<string> paths)
    {
        var parents = paths
            .Select(Path.GetDirectoryName)
            .Where(parent => !string.IsNullOrEmpty(parent))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var delay in new[] { 300, 1500 })
        {
            await Task.Delay(delay);
            foreach (var parent in parents)
            {
                await _viewModel.Tree.RefreshPathAsync(parent!);
            }
        }
    }

    private void Nested_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!TryGetDropPaths(e.Data, out var paths) || ResolveNestedDropTarget(e, paths) is not { } target)
        {
            Nested.DropTarget = null;
            e.Effects = DragDropEffects.None;
            return;
        }

        // The keys and what the source allows, as the tree canvas has them (see DropEffectFor).
        var effect = DropEffectFor(e, paths, target.FullPath);
        Nested.DropTarget = effect == DragDropEffects.None ? null : target;
        e.Effects = effect;
    }

    private void Nested_DragLeave(object sender, DragEventArgs e)
    {
        ForgetDropPaths();
        if (!Nested.IsMouseOver)
        {
            Nested.DropTarget = null;
        }
    }

    private async void Nested_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        Nested.DropTarget = null;
        var carriesPaths = TryGetDropPaths(e.Data, out var paths);
        ForgetDropPaths();
        if (!carriesPaths || ResolveNestedDropTarget(e, paths) is not { } target)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        var effect = DropEffectFor(e, paths, target.FullPath);
        e.Effects = ReportedDropEffect(effect);
        if (effect == DragDropEffects.None)
        {
            return;
        }

        await _viewModel.DropIntoPathAsync(paths, target.FullPath, move: effect == DragDropEffects.Move);
    }

    /// <summary>The innermost folder under the pointer, unless it is one of the things being dropped or inside one.</summary>
    private NestedFolder? ResolveNestedDropTarget(DragEventArgs e, IReadOnlyList<string> paths)
    {
        if (Nested.HitTest(e.GetPosition(Nested)) is not { } hit || hit.Folder.IsComputer)
        {
            return null;
        }

        var target = hit.Folder;
        return IsDropRefused(target.FullPath, () => paths.Any(path =>
            ViewAllPath.Equals(path, target.FullPath)
            || NativeShellService.IsInvalidMoveTarget(path, target.FullPath)
            || ViewAllPath.Equals(Path.GetDirectoryName(path) ?? string.Empty, target.FullPath) && _nestedDragPaths is not null))
            ? null
            : target;
    }

    /// <summary>
    /// A file's icon for the canvas, the file named by its folder and its
    /// index among the folder's shown files: whatever the icon service already
    /// has, and a question for the rest, whose answer arrives in the canvas's
    /// icon inbox (<see cref="ShellIconService.GetForCanvas"/>).  Icons are
    /// per type, looked up by the file's shared extension, so a folder of a
    /// thousand photos asks once and a frame of their names builds nothing;
    /// only programs, shortcuts and icon files are asked about one by one.
    /// </summary>
    private ImageSource? LookUpFileIcon(NestedFolder folder, int index) => _viewModel.Icons.GetForCanvas(folder, index);

    /// <summary>
    /// A folder was read: the types among its files are asked for behind
    /// everything on screen, so their icons are usually there before any of
    /// its names is big enough to carry one.
    /// </summary>
    private void OnFolderLoadedForIcons(NestedFolder folder) => _viewModel.Icons.Prefetch(folder);

    // ---- the name filter ---------------------------------------------------------

    /// <summary>Typing narrows the canvas a moment after the last key, not on every one.</summary>
    private void CanvasFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        CanvasFilterHint.Visibility = CanvasFilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _filterTimer?.Stop();
        _filterTimer?.Start();
    }

    /// <summary>Enter goes to the next match, Shift+Enter the previous, Escape clears and goes back to the canvas.</summary>
    private void CanvasFilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                ApplyFilterNow();
                Nested.GoToMatch((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
                e.Handled = true;
                break;
            case Key.Down:
                ApplyFilterNow();
                Nested.GoToMatch(1);
                e.Handled = true;
                break;
            case Key.Up:
                ApplyFilterNow();
                Nested.GoToMatch(-1);
                e.Handled = true;
                break;
            case Key.Escape:
                ClearCanvasFilter();
                e.Handled = true;
                break;
        }
    }

    private void ApplyFilterNow()
    {
        if (_filterTimer is { IsEnabled: true })
        {
            _filterTimer.Stop();
            Nested.SetFilter(CanvasFilterBox.Text);
        }
    }

    private void ClearCanvasFilter()
    {
        CanvasFilterBox.Text = string.Empty;
        _filterTimer?.Stop();
        Nested.SetFilter(null);
        FocusCanvas();
    }

    private void CanvasFilterPrevious_Click(object sender, RoutedEventArgs e) => Nested.GoToMatch(-1);

    private void CanvasFilterNext_Click(object sender, RoutedEventArgs e) => Nested.GoToMatch(1);

    private void CanvasFilterClear_Click(object sender, RoutedEventArgs e) => ClearCanvasFilter();

    private void FocusCanvasFilter()
    {
        CanvasFilterBox.Focus();
        CanvasFilterBox.SelectAll();
    }

    /// <summary>
    /// The count beside the strip, and the matches as beacons: a match deep
    /// in the tree is a speck, and a speck has to be findable like any mark.
    /// </summary>
    private void OnNestedFilterChanged()
    {
        var matches = Nested.FilterMatches;
        var active = Nested.IsFiltering;
        CanvasFilterCount.Text = !active
            ? string.Empty
            : matches.Count == 0
                ? "No matches among the folders read so far"
                : Nested.FilterCursor >= 0
                    ? $"{Nested.FilterCursor + 1:N0} of {matches.Count:N0}"
                    : matches.Count == 1 ? "1 match" : $"{matches.Count:N0} matches";
        var navigation = active && matches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CanvasFilterPrevious.Visibility = navigation;
        CanvasFilterNext.Visibility = navigation;
        CanvasFilterClear.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        ScheduleBeacons();
    }

    // ---- keyboard --------------------------------------------------------------

    /// <summary>The keys the nested canvas answers itself: the arrows, Enter and Backspace.</summary>
    private bool TryHandleNestedKey(Key key, ModifierKeys modifiers)
    {
        if (!IsNested || !Nested.IsKeyboardFocusWithin)
        {
            return false;
        }

        return Nested.HandleKey(key, modifiers);
    }
}
