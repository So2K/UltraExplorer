using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
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
/// The canvas is shown in panes (<see cref="NestedPane"/>), each a canvas with
/// a tree of its own and the strip over it; everything that is one pane's -
/// its camera, filter, headers, beacons, menus and drags - is the pane's.
/// What is left here is the window's: which pane is being worked with
/// (<see cref="ActivePane"/>), which every window-wide part follows - the
/// selection's mirror, navigation, the zoom buttons, the keys - and what
/// every pane shares - the drives, the orders and hidden rules, the layers,
/// the marks and the background pass.  A window has one pane until its view
/// is split.
/// </summary>
public partial class MainWindow : INestedPaneHost
{
    private readonly List<NestedPane> _panes = [];
    private readonly List<NestedRoot> _nestedDrives = [];
    private string[]? _nestedDragPaths;
    private bool _nestedReady;

    /// <summary>Slices of the nested trees' background pass, waiting for frames to run in (see <see cref="PostSortSlice"/>).</summary>
    private readonly Queue<Action> _sortSlices = new();
    private bool _sortSlicesHooked;
    private TimeSpan _lastSortSliceFrame = TimeSpan.MinValue;

    private bool IsNested => _viewModel.IsNestedLayout;

    /// <summary>The panes of the nested canvas, in the order they are laid out: one until the view is split.</summary>
    internal IReadOnlyList<NestedPane> Panes => _panes;

    /// <summary>
    /// The pane being worked with: the one the address bar, the status bar,
    /// the zoom buttons, the keys and every command follow.
    /// </summary>
    internal NestedPane ActivePane { get; private set; } = null!;

    /// <summary>The first pane: the one a window that is not split shows, and the one the bench, the snapshots and the demos drive.</summary>
    internal NestedPane FirstPane => _panes[0];

    /// <summary>The first pane's canvas, as the bench, the snapshots, the demos and the checks have always known it.</summary>
    internal NestedCanvas Nested => FirstPane.Canvas;

    /// <summary>The canvas that is showing - the active pane's, or the tree - for keyboard focus.</summary>
    private void FocusCanvas() => FocusCanvas(ActivePane);

    /// <summary>
    /// The keyboard to <paramref name="pane"/>'s canvas, or to the tree when
    /// it is the picture on show.  A test copy or a diagnostics run that is
    /// not the active window only has the canvas remembered as where the
    /// keyboard goes when it is clicked into: moving the keyboard there now
    /// would activate the window, and take the keyboard from whatever the
    /// user is typing into on the other screen.
    /// </summary>
    private void FocusCanvas(NestedPane pane)
    {
        UIElement canvas = IsNested ? pane.Canvas : Editor;
        if (!IsActive && (IsTestWindow || IsDiagnosticsRun))
        {
            FocusManager.SetFocusedElement(this, canvas);
            return;
        }

        canvas.Focus();
    }

    private void AttachNested()
    {
        // The first pane takes the icon service's own inbox; a pane made
        // later has one of its own (ShellIconService.SubscribeCanvas).
        var first = new NestedPane(this, _viewModel, FirstPaneView, _viewModel.Icons.CanvasArrivals, index: 0, history: _viewModel.History);
        _panes.Add(first);
        ActivePane = first;
        first.Attach();
        AttachDevices();
        AttachSplit();

        _viewModel.PropertyChanged += OnShellPropertyChangedForNested;
        _viewModel.Tree.PropertyChanged += OnTreePropertyChangedForNested;
        _viewModel.Tree.DeepRefreshRequested += OnTreeDeepRefreshRequested;
        _viewModel.QuickAccess.CollectionChanged += OnBeaconSourceChanged;
        _viewModel.Search.PropertyChanged += OnSearchPropertyChangedForNested;
        _viewModel.Marks.MarkChanged += OnMarkChangedForNested;
    }

    private void DetachNested()
    {
        _viewModel.PropertyChanged -= OnShellPropertyChangedForNested;
        _viewModel.Tree.PropertyChanged -= OnTreePropertyChangedForNested;
        _viewModel.Tree.DeepRefreshRequested -= OnTreeDeepRefreshRequested;
        _viewModel.QuickAccess.CollectionChanged -= OnBeaconSourceChanged;
        _viewModel.Search.PropertyChanged -= OnSearchPropertyChangedForNested;
        _viewModel.Marks.MarkChanged -= OnMarkChangedForNested;
        DetachSplit();
        foreach (var pane in _panes)
        {
            pane.Detach();
        }

        DetachDevices();
        _sortSlices.Clear();
        UnhookSortSlices();
    }

    /// <summary>
    /// How the nested trees' background pass after a change of order runs in
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
    /// One queue for every pane's tree: each slice is bound to its own tree,
    /// and one slice a frame across the panes keeps a split view's frames
    /// as short as one pane's.
    ///
    /// While the nested canvas is not on screen the slices wait: nothing is
    /// drawn from the trees then, and whatever is drawn when it comes back is
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
    /// row of cells in every pane, the hidden-folder rules are copied over, and
    /// the camera is put back where the last session left it.
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

        var roots = NestedRoots();
        foreach (var pane in _panes)
        {
            pane.Initialize(roots);
        }

        SyncNestedSelection();
        _nestedReady = true;

        // The split the last session left, before any pane is entered.
        RestoreSplit();
        _viewModel.NestedZoomLabel = ActivePane.Canvas.ZoomText;
        if (IsNested)
        {
            EnterNested(fromStartup: true);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The nested canvas coming into view, at startup or from the tree: the
    /// pane being worked with takes the selection in, gathers its marks and
    /// goes back to where it was (see <see cref="NestedPane.Enter"/>), with
    /// the keyboard.  The other pane of a split view shows what it keeps and
    /// goes back to its camera the first time, and otherwise stays where it
    /// was left.
    /// </summary>
    private void EnterNested(bool fromStartup)
    {
        if (!_nestedReady)
        {
            return;
        }

        foreach (var pane in _panes)
        {
            if (!ReferenceEquals(pane, ActivePane))
            {
                pane.Enter(fromStartup, focus: false, fly: false);
            }
        }

        ActivePane.Enter(fromStartup);
    }

    /// <summary>A share or distribution the workspace lists answered after the start: the nested canvas shows it too.</summary>
    private void OnExtraRootAdded(ViewAllNodeViewModel root)
    {
        if (_nestedReady)
        {
            SyncNestedRoots();
        }
    }

    /// <summary>Drives, plus every share and WSL distribution the tree has as a root of its own, in every pane.</summary>
    private void SyncNestedRoots()
    {
        var roots = NestedRoots();
        foreach (var pane in _panes)
        {
            pane.Tree.SetRoots(roots);
        }
    }

    /// <summary>The first row of cells: the drives, then every share and WSL distribution the tree has as a root of its own.</summary>
    private List<NestedRoot> NestedRoots()
    {
        var roots = new List<NestedRoot>(_nestedDrives);
        foreach (var root in _viewModel.Tree.Roots.Where(root => !root.IsDrive))
        {
            if (roots.All(existing => !ViewAllPath.Equals(existing.FullPath, root.FullPath)))
            {
                roots.Add(new NestedRoot(root.FullPath, root.DisplayName, NestedFolderKind.Drive));
            }
        }

        return roots;
    }

    // ---- selection, both ways ---------------------------------------------
    //
    // The canvas's gestures reach the shared selection as edits (NestedPane),
    // and every other change reaches the active pane's canvas, in
    // MainWindow.Selection.cs.

    private void OnTreePropertyChangedForNested(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewAllViewModel.ActivePath):
                SyncNestedSelection();
                ActivePane.ScheduleBeacons();

                // On the tree the active folder is the current one when nothing is selected.
                ActivePane.UpdateSortHeaders();
                break;
            case nameof(ViewAllViewModel.HiddenPaths):
                foreach (var pane in _panes)
                {
                    pane.Tree.SetUserHidden(_viewModel.Tree.HiddenPaths);
                }

                break;
            case nameof(ViewAllViewModel.ShowHiddenItems):
                foreach (var pane in _panes)
                {
                    pane.Tree.IncludeHidden = _viewModel.Tree.ShowHiddenItems;
                }

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
                    // pass over the nested trees waited, and goes on now.
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
                // Any order changed.  The trees follow the orders themselves:
                // each canvas keeps what it is looking at where it is, and its
                // tree places what is on screen at once and the rest behind it.
                UpdateSortHeaders();
                break;
            case nameof(MainViewModel.Layers):
                // The files come and go through the tree's pass, like a change
                // of the way grids fill; the rest is only drawn again.
                foreach (var pane in _panes)
                {
                    pane.Canvas.ShownLayers = _viewModel.Layers;
                }

                ScheduleBeacons();
                break;
        }
    }

    // ---- the order ---------------------------------------------------------------

    /// <summary>
    /// The folder the headers of the pane being worked with show and change
    /// the order of, and Canvas options' Sort by with them, and the folder a
    /// search puts first (see <see cref="NestedPane.SortFolder"/>).
    /// </summary>
    internal string? SortFolder() => ActivePane.SortFolder();

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

    /// <summary>Every pane's headers brought up to date: an order, or the picture on show, changed.</summary>
    private void UpdateSortHeaders()
    {
        foreach (var pane in _panes)
        {
            pane.UpdateSortHeaders();
        }
    }

    /// <summary>A folder's name as a menu or a tip says it: its own name, or the whole of a drive's.</summary>
    internal static string FolderName(string path) =>
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

    private void SyncNestedSelection() => ActivePane.SyncSelection();

    /// <summary>The nested version of "bring this node into view", in the pane being worked with (see <see cref="NestedPane.FlyToAsync"/>).</summary>
    private Task FlyNestedToAsync(string path, bool gentle, bool animated = true) => ActivePane.FlyToAsync(path, gentle, animated);

    /// <summary>Every pane's camera kept for the next session: the window is closing.</summary>
    private void CaptureNestedCamera()
    {
        foreach (var pane in _panes)
        {
            pane.CaptureCamera();
        }
    }

    /// <summary>F5 on a folder: every pane reads it again, and what it read below it as it is drawn (see <see cref="NestedPane.RefreshDeep"/>).</summary>
    private void OnTreeDeepRefreshRequested(string path)
    {
        foreach (var pane in _panes)
        {
            pane.RefreshDeep(path);
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
            foreach (var pane in _panes)
            {
                pane.Canvas.InvalidateMarks();
            }

            ScheduleBeacons();
        });
    }

    /// <summary>Every pane's beacons gathered again a moment from now: marks, pins or the search's results changed.</summary>
    private void ScheduleBeacons()
    {
        foreach (var pane in _panes)
        {
            pane.ScheduleBeacons();
        }
    }

    // ---- keyboard --------------------------------------------------------------

    /// <summary>The keys a nested canvas answers itself - the arrows, Enter and Backspace - on whichever pane has the keyboard.</summary>
    private bool TryHandleNestedKey(Key key, ModifierKeys modifiers)
    {
        if (!IsNested)
        {
            return false;
        }

        foreach (var pane in _panes)
        {
            if (pane.Canvas.IsKeyboardFocusWithin)
            {
                return pane.Canvas.HandleKey(key, modifiers);
            }
        }

        return false;
    }

    /// <summary>Whether the keyboard is on one of the panes' canvases.</summary>
    private bool IsNestedCanvasFocused()
    {
        foreach (var pane in _panes)
        {
            if (pane.Canvas.IsKeyboardFocusWithin)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The pane whose canvas <paramref name="element"/> is, or is inside.</summary>
    private NestedPane? PaneAt(DependencyObject? element)
    {
        if (FindAncestor<NestedCanvas>(element) is { } canvas)
        {
            foreach (var pane in _panes)
            {
                if (ReferenceEquals(pane.Canvas, canvas))
                {
                    return pane;
                }
            }
        }

        return null;
    }

    private void FocusCanvasFilter() => ActivePane.FocusFilter();

    // ---- what a pane asks of the window ------------------------------------------

    bool INestedPaneHost.IsPickerMode => IsPickerMode;

    string[]? INestedPaneHost.NestedDragPaths
    {
        get => _nestedDragPaths;
        set => _nestedDragPaths = value;
    }

    bool INestedPaneHost.IsActivePane(NestedPane pane) => ReferenceEquals(pane, ActivePane);

    void INestedPaneHost.PostSortSlice(Action slice) => PostSortSlice(slice);

    void INestedPaneHost.SyncNestedRoots() => SyncNestedRoots();

    void INestedPaneHost.FocusCanvas(NestedPane pane) => FocusCanvas(pane);

    void INestedPaneHost.ClearDropTargets()
    {
        foreach (var pane in _panes)
        {
            pane.Canvas.DropTarget = null;
        }
    }

    void INestedPaneHost.DropPreparedMenu() => DropPreparedMenu();

    void INestedPaneHost.PrepareShellMenu(bool background, IReadOnlyList<string> paths) => PrepareShellMenu(background, paths);

    bool INestedPaneHost.ShowFolderAreaShellMenu(string folder, FrameworkElement origin, Point point) => ShowFolderAreaShellMenu(folder, origin, point);

    void INestedPaneHost.ShowFolderAreaMenu(FrameworkElement placementTarget, ViewAllNodeViewModel? area) => ShowFolderAreaMenu(placementTarget, area);

    bool INestedPaneHost.ShowContextMenu(IReadOnlyList<string> paths, FrameworkElement origin, Point point, bool includeCanvasCommands, bool fallBack) =>
        ShowContextMenu(paths, origin, point, includeCanvasCommands, fallBack);

    void INestedPaneHost.ShowSelectionMenu(FrameworkElement placementTarget) => ShowSelectionMenu(placementTarget);

    bool INestedPaneHost.TryGetDropPaths(IDataObject data, out string[] paths) => TryGetDropPaths(data, out paths);

    bool INestedPaneHost.IsDropRefused(string folder, Func<bool> refuses) => IsDropRefused(folder, refuses);

    void INestedPaneHost.ForgetDropPaths() => ForgetDropPaths();
}
