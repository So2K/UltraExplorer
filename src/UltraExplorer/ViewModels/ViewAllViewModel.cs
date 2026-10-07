using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;

namespace UltraExplorer.ViewModels;

/// <summary>
/// The View All canvas: every drive is an independent root and one graph node
/// is exactly one file-system object.  Expansion is lazy, layout is incremental
/// and only the nodes inside the viewport are handed to the editor.
///
/// It is also where changes on disk come in for the whole window (see
/// <see cref="IChangeSink"/>): the change hub hands each changed folder to
/// whoever registered it - the nested canvas's tree, the folder list, the
/// tree canvas - and each of them brings itself up to date on its own, none
/// waiting for another.  What is selected has no say in what is watched; the
/// selection only follows what changed, letting go of what went and
/// following what was renamed.
/// </summary>
public sealed class ViewAllViewModel : ObservableObject, IDisposable, IChangeSink
{
    private readonly ViewAllGraphService _graph;
    private readonly ViewAllViewportService _viewportService = new();
    private readonly ViewAllWorkspaceStore _store;
    private readonly FolderMarkService _marks;
    private readonly ShellIconService _icons;
    private readonly DispatcherTimer _renderThrottle;
    private readonly DispatcherTimer _saveDebounce;
    private ChangeHub? _changes;

    /// <summary>The tree canvas's folders registered with the hub, by path: the ones with children on the tree.</summary>
    private Dictionary<string, ViewAllNodeViewModel> _graphInterest = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tree canvas folders that changed while nothing needed them current: read again when they are next shown.</summary>
    private readonly HashSet<string> _graphStale = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tree canvas folders being read again for a change, and the ones changed again meanwhile.</summary>
    private readonly HashSet<string> _graphRefreshing = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _graphChangedAgain = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The change last taken for the selection, so one change handed to three consumers is taken once.</summary>
    private (string Key, long First, ChangeKinds Kinds) _lastChange;

    private bool _isInitialized;

    /// <summary>
    /// Set once the saved workspace has been read and put back.  Until then
    /// nothing is written over it: a save of a half-restored graph - the
    /// window closed, or a save come due, while folders were still being
    /// opened again - would keep only what was back so far.
    /// </summary>
    private bool _isStateRestored;

    /// <summary>A save was asked for before the workspace was back; one is made once it is.</summary>
    private bool _saveHeldForRestore;

    /// <summary>
    /// The folder selected last time, when it lies in a share or distribution
    /// not asked for yet: selected when that root answers, unless something
    /// else has been selected meanwhile (<see cref="_heldActiveVersion"/>).
    /// </summary>
    private string? _heldActivePath;
    private long _heldActiveVersion;

    private bool _isDisposed;
    public bool BrowseArchives { get; set; }
    private bool _isBusy;
    private bool _isSyncingSelection;
    private bool _treeSelectionPending;
    private int _selectTicket;

    /// <summary>Which navigation through <see cref="RevealPathAsync"/> is the latest; an older one still reading does not select.</summary>
    private int _revealTicket;

    /// <summary>
    /// The latest of those tickets taken in each pane of a split view - by
    /// its <see cref="SelectionHolder"/>, this view model standing for none -
    /// and so whose <see cref="Selection"/> each was taken for: a navigation
    /// that waits before it reveals - a path typed into the address bar,
    /// checked first - goes where it was asked for, whichever pane is being
    /// worked with by the time it reveals.  A navigation gives way only to a
    /// newer one in its own pane; one in the other pane meanwhile leaves it
    /// to land in its pane (<see cref="NavigationLandedAway"/>), whose history
    /// has already taken the step.  Weak on the pane, so one closed is let go.
    /// </summary>
    private readonly ConditionalWeakTable<object, StrongBox<int>> _latestReveals = new();

    /// <summary>
    /// The selection's focus while the graph is still finding its node
    /// (<see cref="FocusAsync"/>): what a command acts on meanwhile, not the
    /// node before it (see <see cref="FocusedPath"/>).  Null once the focus
    /// has its node, or there is none.
    /// </summary>
    private string? _pendingFocus;

    private int _marqueePreview = -1;
    private bool _focusRecordsNavigation = true;
    private ViewAllNodeViewModel? _activeNode;
    private ViewAllNodeViewModel? _dropTarget;
    private ViewAllNodeViewModel? _runTarget;
    private ViewAllDetailLevel _detailLevel = ViewAllDetailLevel.Detailed;
    private Point _viewportLocation;
    private Size _viewportSize = new(1200, 800);
    private double _viewportZoom = 1;
    private int _logicalNodeCount;
    private int _visibleNodeCount;
    private bool _isOverviewActive;
    private bool _isOverviewStale;
    private bool _isCanvasShown = true;
    private bool _graphChangedWhileHidden;

    /// <summary>The order changed while the tree canvas was away, and the tree is still laid out in the one before.</summary>
    private bool _isSortBehind;

    /// <summary>
    /// Node containers added per pass.  Measured: a container costs about a
    /// millisecond and a half to inflate, so four of them plus the edges that
    /// come with them is comfortably inside one frame at sixty a second.  The
    /// rest arrive on the next tick, eight milliseconds later.
    /// </summary>
    private const int FillBudget = 4;


    private readonly DispatcherTimer _fillTimer;
    private IReadOnlyList<ViewAllTrailStep> _trail = [];
    private readonly List<ViewAllNodeViewModel> _trailProbe = [];
    private string _statusCountText = string.Empty;
    private string _statusPathText = string.Empty;

    /// <param name="statePath">
    /// Where the canvas layout is persisted.  Picker sessions pass their own
    /// file so that being a file dialog for somebody else never disturbs the
    /// workspace the user arranged for themselves.
    /// </param>
    public ViewAllViewModel(FolderMarkService marks, ShellIconService icons, string? statePath = null)
    {
        _marks = marks;
        _icons = icons;
        _store = new ViewAllWorkspaceStore(statePath);
        _graph = new ViewAllGraphService();
        _graph.GraphChanged += OnGraphChanged;
        _graph.NodeCreated += OnNodeCreated;
        _graph.LayoutChanged += OnLayoutChanged;
        _graph.LayoutShifted += delta => ViewShiftRequested?.Invoke(delta);
        _graph.DriveAdded += OnDriveAdded;
        _graph.SortOf = Orders.SortOf;
        _marks.MarkChanged += OnMarkChanged;
        Orders.Changed += OnOrdersChanged;

        SelectedNodes.CollectionChanged += OnSelectedNodesChanged;
        Selection.Changed += OnSelectionChanged;

        FolderList = new FolderListViewModel(
            (path, sort, cancellation) => _graph.ReadDirectoryAsync(path, cancellation, sort),
            ActivateListItemAsync,
            path => _graph.TryGetNode(path, out _),
            icons)
        {
            SharedSelection = Selection,
            SortOf = Orders.SortOf
        };

        _renderThrottle = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(35)
        };
        _fillTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(8)
        };
        _fillTimer.Tick += (_, _) => RebuildRenderSet();

        _renderThrottle.Tick += (_, _) =>
        {
            _renderThrottle.Stop();
            RebuildRenderSet();
        };

        _saveDebounce = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(900)
        };
        _saveDebounce.Tick += async (_, _) =>
        {
            _saveDebounce.Stop();
            await SaveAsync();
        };

        ToggleSelectedCommand = new AsyncRelayCommand(ToggleSelectedAsync);
        CollapseAllCommand = new RelayCommand(CollapseAll);
        RefreshSelectedCommand = new AsyncRelayCommand(RefreshSelectedAsync);
        LoadMoreCommand = new AsyncRelayCommand(LoadMoreForSelectionAsync);
    }

    /// <summary>Nodes and edges the editor should actually realize right now.</summary>
    public ObservableCollection<ViewAllNodeViewModel> RenderNodes { get; } = [];

    public ObservableCollection<ViewAllEdgeViewModel> RenderEdges { get; } = [];

    /// <summary>
    /// The selection the whole window shares - both canvases, the folder
    /// list and every command.  Paths, not nodes: a rectangle over ten
    /// thousand files on the nested canvas selects ten thousand paths the
    /// graph has no nodes for, and only the focus is ever given one.
    /// </summary>
    public ItemSelection Selection { get; } = new();

    /// <summary>
    /// The selection as the tree canvas's editor sees it: the nodes of the
    /// selected paths that have nodes, bound to Nodify.  Kept whole only
    /// while the tree canvas is on screen; while it is not, only the focus,
    /// and it is filled again when the tree comes back.  What Nodify itself
    /// puts in it - a click, a rubber band - becomes one change of
    /// <see cref="Selection"/>.
    /// </summary>
    public ObservableCollection<ViewAllNodeViewModel> SelectedNodes { get; } = [];

    public ICommand ToggleSelectedCommand { get; }
    public ICommand CollapseAllCommand { get; }
    public ICommand RefreshSelectedCommand { get; }
    public ICommand LoadMoreCommand { get; }

    /// <summary>Raised when the canvas should fly to a node.</summary>
    public event Action<ViewAllNodeViewModel, bool>? FocusNodeRequested;

    /// <summary>
    /// Raised in place of selecting and flying there when a navigation ends
    /// after <see cref="Selection"/> has changed hands: it was started in a
    /// pane of a split view - a favourite, Back, a path typed in - and the
    /// other pane was clicked while it read its way down a slow share.  The
    /// place is that pane's, which goes there itself; the pane being worked
    /// with keeps its selection, its history and its view.
    /// </summary>
    public event Action<NavigationLanding>? NavigationLandedAway;

    /// <summary>Raised when a message belongs on the shell toast.</summary>
    public event Action<string, bool>? MessageRequested;

    /// <summary>Exact successfully resolved folder paths explicitly hidden by a user command, including an already-hidden path reopened by name.</summary>
    public event Action<IReadOnlyList<string>>? FoldersHiddenByUser;

    /// <summary>Raised when the graph structure changed and cached drawing is stale.</summary>
    public event Action? GraphInvalidated;

    /// <summary>
    /// Raised after a directory was read again because the window changed
    /// something in it - a file operation - or F5 asked.  What else shows the
    /// folder hears of it through the change hub (<see cref="ChangeHub.Touch"/>),
    /// like any change on disk; this is for whoever wants to know as well.
    /// </summary>
    public event Action<string>? PathRefreshed;

    /// <summary>Explicit filesystem rename, independent of which item is selected.</summary>
    public event Action<string, string>? RenameFollowed;

    /// <summary>
    /// F5 on a folder: everything read in and below it is to be taken as out
    /// of date, not only the folder itself.  The nested canvas's tree listens,
    /// and reads again what is on screen (<see cref="NestedTree.RefreshDeep"/>).
    /// </summary>
    public event Action<string>? DeepRefreshRequested;

    /// <summary>
    /// The change hub the window watches the disk through, owned by the
    /// window's view model, or null (a test's view model).  The folder list's
    /// folder and every folder the tree canvas shows the children of are
    /// registered with it; the nested canvas's tree registers its own.
    /// </summary>
    public ChangeHub? Changes
    {
        get => _changes;
        set
        {
            if (ReferenceEquals(_changes, value))
            {
                return;
            }

            if (_changes is { } previous)
            {
                foreach (var (path, node) in _graphInterest)
                {
                    previous.Unregister(ChangeConsumer.Graph, path, node);
                }

                _graphInterest.Clear();
            }

            _changes = value;
            FolderList.Changes = value;
            SyncGraphInterest();
        }
    }

    /// <summary>
    /// Where the hub's changes to the nested canvas's folders go: the nested
    /// tree of every pane the window draws, added and taken away by the
    /// window (<see cref="AddNestedChanges"/>).  Every change goes to every
    /// one of them, and a tree takes only its own folders' (see
    /// <see cref="NestedTree.Owns"/>); a missed change or a poll is every
    /// tree's.  Empty while there is none; one with a single pane.
    /// </summary>
    public IReadOnlyList<IChangeSink> NestedChanges => _nestedChanges;

    /// <summary>The sinks of <see cref="NestedChanges"/>, replaced whole on a change so a delivery in progress walks the list it began with.</summary>
    private IChangeSink[] _nestedChanges = [];

    /// <summary>A pane's tree is to hear of changes to its folders from now on; nothing if it already does.</summary>
    public void AddNestedChanges(IChangeSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (Array.IndexOf(_nestedChanges, sink) < 0)
        {
            _nestedChanges = [.. _nestedChanges, sink];
        }
    }

    /// <summary>A pane's tree hears of no more changes: its pane closed, or the window is going.</summary>
    public void RemoveNestedChanges(IChangeSink sink)
    {
        if (Array.IndexOf(_nestedChanges, sink) >= 0)
        {
            _nestedChanges = [.. _nestedChanges.Where(kept => !ReferenceEquals(kept, sink))];
        }
    }

    /// <summary>
    /// How far the layout carried the folder the user just opened or closed.
    /// The canvas pans by the same amount, so the tree grows around the thing
    /// under the cursor instead of sliding out from under it.
    /// </summary>
    public event Action<Vector>? ViewShiftRequested;

    /// <summary>The current folder as a list, beside the canvas.</summary>
    public FolderListViewModel FolderList { get; }

    /// <summary>Grid the overview layer draws from.</summary>
    public ViewAllSpatialIndex SpatialIndex => _graph.Index;

    /// <summary>
    /// The chain of folders whose blocks the middle of the canvas is inside,
    /// outermost first.  Empty at the top of a tree, where the folder's own
    /// label is on screen anyway.
    /// </summary>
    public IReadOnlyList<ViewAllTrailStep> Trail
    {
        get => _trail;
        private set
        {
            _trail = value;
            OnPropertyChanged(nameof(Trail));
            OnPropertyChanged(nameof(HasTrail));
        }
    }

    public bool HasTrail => _trail.Count > 0;

    /// <summary>
    /// True while the canvas is far enough out that the graph is drawn as
    /// batched geometry rather than as one control per node.
    /// </summary>
    public bool IsOverviewActive
    {
        get => _isOverviewActive;
        private set => SetProperty(ref _isOverviewActive, value);
    }

    /// <summary>
    /// Whether a path is brought into the graph by name rather than by opening
    /// its ancestors.  Off, revealing a path expands every folder on the way to
    /// it, which is what the tree canvas needs: the path has to be on the tree to
    /// be seen there.  On, only the steps that have no node yet get one, and no
    /// folder is read - see <see cref="ViewAllGraphService.MaterializeChainAsync"/>.
    /// A first click deep inside System32 goes from half a second to a couple of
    /// milliseconds, because nothing in System32 but the one folder on the way
    /// is created.
    ///
    /// It is meant for while the tree canvas is not the one on screen.  The
    /// nested canvas draws from its own tree and selects through this view model
    /// by path, so the selection, the commands, the list, the address and the
    /// status bar all work from a node that is not on the tree - and none of
    /// them notices that the folders above it were never opened.
    ///
    /// Everything that reveals goes through it: <see cref="RevealPathAsync"/>
    /// itself, and so <see cref="SelectPathAsync"/>,
    /// <see cref="ToggleSelectionAsync"/> and <see cref="MaterializeAsync"/>.
    /// What happens once the node exists does not change - it is still selected,
    /// still flown to through <see cref="FocusNodeRequested"/>, still saved, and
    /// a path that is not there still gets the same message.
    ///
    /// Turning it off does not put the tree right by itself: nodes brought in by
    /// name stay off the tree until their folder is opened.  Switching back to the
    /// tree should turn this off and reveal <see cref="ActivePath"/> again,
    /// preferably before <see cref="IsCanvasShown"/> is turned back on, so the
    /// canvas comes up already open at the selection.  To have a selection that
    /// only existed this way picked up again after a restart, turn it on before
    /// <see cref="InitializeAsync"/>.
    /// </summary>
    public bool PreferLightReveal { get; set; }

    /// <summary>
    /// Whether the tree canvas is on screen.  Most of what this view model does
    /// after the graph changes is for that canvas alone: culling the nodes the
    /// editor should realize, handing them to it a few per frame, asking for
    /// their icons, working out the trail and telling the batched layers to
    /// rebuild.  While the nested canvas is the one showing, the editor is
    /// collapsed and all of that is work for a picture nobody can see, so it is
    /// not done; the view model only remembers that it is behind.
    ///
    /// The rest carries on exactly as before: the selection,
    /// <see cref="ActiveNode"/> and the status texts, the folder list, hidden
    /// folders and saving.  A change on disk in a folder the tree shows is
    /// read at once only when a selected item is in it; the rest are read
    /// again when the tree comes back.  The node count keeps being kept
    /// too - it is one pass over the graph, and the status bar reads it whenever
    /// nothing is selected, whichever canvas is showing.
    ///
    /// Turning it back on catches up in one go: anything opened while the tree
    /// was away is checked against the tree (see
    /// <see cref="ViewAllGraphService.SettleVisibility"/>), an order chosen
    /// meanwhile is laid out (see <see cref="Sort"/>), the render set is
    /// rebuilt once and <see cref="GraphInvalidated"/> is raised, so the editor,
    /// the harness and the batched overview all come up current.
    /// </summary>
    public bool IsCanvasShown
    {
        get => _isCanvasShown;
        set
        {
            if (!SetProperty(ref _isCanvasShown, value))
            {
                return;
            }

            if (!value)
            {
                _renderThrottle.Stop();
                _fillTimer.Stop();
                SyncSelectionMirror();
                return;
            }

            // The editor's selection is the whole of it again.
            SyncSelectionMirror();

            // Settling announces a change when it finds one, and that already
            // rebuilds everything below; otherwise it is done here.
            var settled = _graphChangedWhileHidden && !_isDisposed && _graph.SettleVisibility();
            _graphChangedWhileHidden = false;

            // An order chosen while the tree was away is laid out now, before
            // anything is drawn, and announced like settling.  Held on the node
            // selected: coming back, that is what the view is brought to.
            if (_isSortBehind && !_isDisposed)
            {
                _isSortBehind = false;
                _graph.Resort(Orders.Default, _activeNode);
                settled = true;
            }

            if (!settled)
            {
                _isOverviewStale = true;
                RebuildRenderSet();
            }

            // Folders that changed on disk while the tree was away.
            RefreshStaleGraph();
        }
    }

    /// <summary>The visible node under a graph-space point.</summary>
    public ViewAllNodeViewModel? HitTest(Point graphPoint) => _graph.HitTest(graphPoint);

    public ViewAllNodeViewModel? HitTest(Point graphPoint, Predicate<ViewAllNodeViewModel> exclude)
        => _graph.HitTest(graphPoint, exclude);

    /// <summary>
    /// What a row in the folder list does.  Picking one takes the canvas to it,
    /// which is why the list is worth having at all; opening one is the ordinary
    /// double-click, and for a folder that means the list goes in as well.
    /// </summary>
    private async Task ActivateListItemAsync(string path, bool open)
    {
        RevealOutcome outcome;

        // Held while the canvas is being driven from a row: selecting a folder on
        // the canvas would otherwise take the list into it, which would make a
        // single click open the folder.  A single click selects; it does not open.
        // Only the row's own item is selected - a row whose item went meanwhile
        // must not select its folder - and only while nothing newer was asked for:
        // no other navigation, and no other pick, a row clicked meanwhile included.
        var version = Selection.Version;
        using (FolderList.HoldFolder())
        {
            outcome = await RevealAsync(path, exact: true, selectionVersion: version);
        }

        var node = outcome.IsExact ? outcome.Node : null;
        if (node is null)
        {
            // Given way to on its way down, it stopped short of the row: not gone.
            if (open && !outcome.Superseded)
            {
                MessageRequested?.Invoke($"{Path.GetFileName(path)} could not be shown", true);
            }

            return;
        }

        if (!open || outcome.Superseded)
        {
            return;
        }

        if (node.IsDirectory)
        {
            // A folder on a share can take its time to read, and the user may
            // have gone somewhere else meanwhile - another navigation, the
            // other pane, a click elsewhere: the focus, the address and the
            // list stay with that, not jump back to this folder.
            var ticket = _revealTicket;
            var holder = SelectionHolder;
            await ExpandAsync(node);
            if (ticket != _revealTicket
                || !ReferenceEquals(holder, SelectionHolder)
                || Selection.Focus is not { } focus
                || !ViewAllPath.Equals(focus, node.FullPath))
            {
                return;
            }

            ActiveNode = node;
            await FolderList.NavigateAsync(path);
            return;
        }

        OpenInDefaultApplication(node);
    }

    public ViewAllNodeViewModel? ActiveNode
    {
        get => _activeNode;
        private set
        {
            // A node given to the focus is newer than one still being looked for.
            _pendingFocus = null;
            if (SetProperty(ref _activeNode, value))
            {
                OnPropertyChanged(nameof(ActivePath));
                RetargetFolderList();
                UpdateStatus();
            }
        }
    }

    public string ActivePath => _activeNode?.FullPath ?? string.Empty;

    /// <summary>
    /// Whether the focus moving right now is going somewhere, for back and
    /// forward: true except while a selection that is not a navigation - a
    /// range, a rectangle, a pick in the list - moves it.  Read while
    /// <see cref="ActivePath"/> is being announced.
    /// </summary>
    public bool FocusRecordsNavigation => _focusRecordsNavigation;

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public ViewAllDetailLevel DetailLevel
    {
        get => _detailLevel;
        private set => SetProperty(ref _detailLevel, value);
    }

    public int LogicalNodeCount
    {
        get => _logicalNodeCount;
        private set
        {
            if (SetProperty(ref _logicalNodeCount, value))
            {
                UpdateStatus();
            }
        }
    }

    public string StatusCountText
    {
        get => _statusCountText;
        private set => SetProperty(ref _statusCountText, value);
    }

    public string StatusPathText
    {
        get => _statusPathText;
        private set => SetProperty(ref _statusPathText, value);
    }

    public Point ViewportLocation => _viewportLocation;

    public double ViewportZoom => _viewportZoom;

    public string ZoomLabel => $"{_viewportZoom:P0}";

    /// <summary>Viewport restored from the saved workspace, applied once on load.</summary>
    public Point RestoredViewportLocation { get; private set; }

    public double RestoredViewportZoom { get; private set; } = 1;

    public bool HasRestoredViewport { get; private set; }

    /// <summary>Where the nested canvas was looking last session.</summary>
    public NestedCameraState? RestoredNestedCamera { get; private set; }

    /// <summary>Where the nested canvas is looking now - its first pane - written with the rest of the canvas state.</summary>
    public NestedCameraState? NestedCamera { get; set; }

    /// <summary>Where the second pane of a split view was looking last session, and what it had selected; null when the view was never split.</summary>
    public NestedPaneState? RestoredSecondPane { get; private set; }

    /// <summary>
    /// What the second pane had selected last session, found the way the
    /// first pane's is found (see <see cref="InitializeAsync"/>): the item and
    /// whether it is a folder, or null when it was nothing, is gone, lies in
    /// a share not asked for yet, or the view does not open split.
    /// </summary>
    public SelectionItem? RestoredSecondPaneItem { get; private set; }

    /// <summary>
    /// Set before <see cref="InitializeAsync"/> when the window opens split:
    /// only then is the second pane's selection looked for.  A view split
    /// later starts from the place its second pane had, without it.
    /// </summary>
    public bool RestoresSecondPane { get; set; }

    /// <summary>
    /// The second pane of a split view as the canvas workspace keeps it: its
    /// camera, and what it had selected when it last stopped being the pane
    /// worked with.  The window keeps it up to date, and keeps it while the
    /// view is not split, so a split opens where the last one was.
    /// </summary>
    public NestedPaneState? SecondPane { get; set; }

    /// <summary>
    /// Whether the second pane of a split view is the one being worked with.
    /// <see cref="Selection"/> and <see cref="ActivePath"/> are then its, and
    /// the first pane's selected path - what the workspace writes as
    /// <see cref="ViewAllWorkspaceState.ActivePath"/> - is the other pane's
    /// (<see cref="OtherPanePath"/>).
    /// </summary>
    public bool IsSecondPaneActive { get; set; }

    /// <summary>
    /// While the view is split: what the pane not being worked with has
    /// selected, asked as the workspace is written.  Null while there is one pane.
    /// </summary>
    public Func<string?>? OtherPanePath { get; set; }

    /// <summary>
    /// Whose <see cref="Selection"/> is now: in a split view, the pane being
    /// worked with, set by the window each time the panes change places;
    /// never changed while the window has one pane.  A navigation notes it
    /// as it starts, and one that ends after it has changed lands with the
    /// pane it was started for (<see cref="NavigationLandedAway"/>), not in
    /// the selection that is by then another pane's.
    /// </summary>
    public object? SelectionHolder { get; set; }

    /// <summary>
    /// The selections the panes of a split view keep while another pane's is
    /// <see cref="Selection"/> (see <see cref="AddKeptSelection"/>).  Replaced
    /// whole on a change.
    /// </summary>
    private ItemSelection[] _keptSelections = [];

    /// <summary>
    /// A pane that is not being worked with keeps its selection here: what
    /// is moved away or deleted leaves it (<see cref="ForgetSelected"/>), and
    /// what is found gone or renamed on disk leaves it or takes the new name,
    /// as in <see cref="Selection"/>.
    /// </summary>
    public void AddKeptSelection(ItemSelection kept)
    {
        ArgumentNullException.ThrowIfNull(kept);
        if (!ReferenceEquals(kept, Selection) && Array.IndexOf(_keptSelections, kept) < 0)
        {
            _keptSelections = [.. _keptSelections, kept];
        }
    }

    /// <summary>The pane is being worked with again, or has closed: its kept selection is no longer followed.</summary>
    public void RemoveKeptSelection(ItemSelection kept)
    {
        if (Array.IndexOf(_keptSelections, kept) >= 0)
        {
            _keptSelections = [.. _keptSelections.Where(other => !ReferenceEquals(other, kept))];
        }
    }

    /// <summary>For the checks: how many kept selections are followed.</summary>
    internal int KeptSelectionCount => _keptSelections.Length;

    /// <summary>
    /// Paths that were moved away or deleted, let go of by every pane's
    /// selection: the one being worked with and every one kept.
    /// </summary>
    public void ForgetSelected(IReadOnlyList<string> paths)
    {
        Selection.Remove(paths, SelectionSource.Command);
        foreach (var kept in _keptSelections)
        {
            kept.Remove(paths, SelectionSource.Command);
        }
    }

    /// <summary>Whether any pane's selection holds something directly inside <paramref name="folder"/>.</summary>
    private bool AnySelectedIn(string folder)
    {
        if (Selection.CountIn(folder) > 0)
        {
            return true;
        }

        foreach (var kept in _keptSelections)
        {
            if (kept.CountIn(folder) > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The graph's roots: every drive, and any WSL distribution or share added as one.</summary>
    public IReadOnlyList<ViewAllNodeViewModel> Roots => _graph.Roots;

    /// <param name="initialPath">
    /// Opened instead of the profile folder on a first run, which is how a file
    /// dialog lands on the folder its caller asked for.
    /// </param>
    public async Task InitializeAsync(string? initialPath = null)
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        IsBusy = true;
        try
        {
            var state = await _store.LoadAsync();
            RestoredNestedCamera = state?.NestedCamera;
            NestedCamera = RestoredNestedCamera;
            RestoredSecondPane = state?.SecondPane;
            SecondPane = RestoredSecondPane;

            // Shares and distributions are asked for once the window is up
            // (ProbeExtraRootsAsync): one whose server is off would hold the
            // whole window up for as long as the network takes to say so.
            await _graph.InitializeAsync(state, deferExtraRoots: true);

            if (state is { Nodes.Count: > 0 })
            {
                RestoredViewportLocation = new Point(state.ViewportX, state.ViewportY);
                RestoredViewportZoom = Math.Clamp(state.ViewportZoom, 0.05, 4);
                HasRestoredViewport = true;

                if (!string.IsNullOrWhiteSpace(state.ActivePath) && _graph.TryGetNode(state.ActivePath, out var active))
                {
                    SelectOnly(active);
                }
                else if (!string.IsNullOrWhiteSpace(state.ActivePath) && _graph.IsInPendingRoot(state.ActivePath))
                {
                    // In a share not asked for yet, or on a drive that has not
                    // answered yet: the first drive stands in, and the folder
                    // is selected when its share or drive answers.
                    if (_graph.Roots.FirstOrDefault() is { } standIn)
                    {
                        SelectOnly(standIn);
                    }

                    _heldActivePath = state.ActivePath;
                    _heldActiveVersion = Selection.Version;
                }
                else if (PreferLightReveal
                    && !string.IsNullOrWhiteSpace(state.ActivePath)
                    && await _graph.MaterializeChainAsync(state.ActivePath) is { IsComplete: true, Node: { } named })
                {
                    // Selected by name last time, so no opened folder brought it
                    // back: the workspace only reopens what was open.
                    SelectOnly(named);
                }
                else if (_graph.Roots.FirstOrDefault() is { } firstRoot)
                {
                    SelectOnly(firstRoot);
                }
            }
            else
            {
                // First run: open a branch so the canvas is never empty.
                await RevealPathAsync(
                    string.IsNullOrWhiteSpace(initialPath)
                        ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                        : initialPath,
                    focus: false);
            }

            if (RestoresSecondPane)
            {
                RestoredSecondPaneItem = await FindSecondPaneItemAsync(state?.SecondPane?.ActivePath);
            }

            RebuildRenderSet();

            // Put back: saves may write it from now on.  One asked for while
            // it was being put back is made now.  A restore that failed never
            // gets here, and leaves the file as it was for the next start.
            _isStateRestored = true;
            if (_saveHeldForRestore)
            {
                _saveHeldForRestore = false;
                ScheduleSave();
            }
        }
        catch (Exception ex)
        {
            MessageRequested?.Invoke($"Could not build the drive graph: {ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The second pane's selection of last session, found as the first
    /// pane's is: a node the restored graph has, or a path brought in by name
    /// - never one in a share not asked for yet, or on a drive that has not
    /// answered yet, whose server may take a minute to say it is off.  Null
    /// for nothing found.
    /// </summary>
    private async Task<SelectionItem?> FindSecondPaneItemAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            if (_graph.TryGetNode(path, out var known))
            {
                return new SelectionItem(known.FullPath, known.IsDirectory, known.Entry.SizeBytes ?? 0);
            }

            if (!_graph.IsInPendingRoot(path)
                && PreferLightReveal
                && await _graph.MaterializeChainAsync(path) is { IsComplete: true, Node: { } named })
            {
                return new SelectionItem(named.FullPath, named.IsDirectory, named.Entry.SizeBytes ?? 0);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        return null;
    }

    /// <summary>Raised when a saved share or distribution answered and became a root, after the start.</summary>
    public event Action<ViewAllNodeViewModel>? ExtraRootAdded;

    /// <summary>Raised when a drive that had not answered as the window started has, and is a root now.</summary>
    public event Action<ViewAllNodeViewModel>? DriveAdded;

    /// <summary>
    /// Asks for the shares and WSL distributions the workspace lists, all at
    /// once, once the window is up: each that answers becomes a root as it
    /// does, with what was open in it opened again - and the folder selected
    /// last time selected, if it is in one and nothing else has been selected
    /// since.  Each that does not is kept for the next start (see
    /// <see cref="ViewAllGraphService.ProbeExtraRootsAsync"/>).
    /// </summary>
    public async Task ProbeExtraRootsAsync()
    {
        if (!_isStateRestored || _isDisposed || _graph.PendingExtraRoots.Count == 0)
        {
            return;
        }

        try
        {
            await _graph.ProbeExtraRootsAsync(ViewAllGraphService.ExtraRootTimeout, added: OnExtraRootAdded);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (_isDisposed)
        {
            return;
        }

        // A folder selected last time in a root that did not answer stays
        // what is saved as selected - to be tried again at the next start -
        // until something else is selected.
        RebuildRenderSet();

        // What was found out - which roots answered, which missed once more -
        // is written down now rather than at the next change.
        ScheduleSave();
    }

    private void OnExtraRootAdded(ViewAllNodeViewModel root)
    {
        if (_isDisposed)
        {
            return;
        }

        RebuildRenderSet();
        ExtraRootAdded?.Invoke(root);
        SelectHeldActiveIn(root);
    }

    /// <summary>A drive start-up went on without has answered: the window shows it too, and the folder selected last time, if it is on it.</summary>
    private void OnDriveAdded(ViewAllNodeViewModel drive)
    {
        if (_isDisposed)
        {
            return;
        }

        DriveAdded?.Invoke(drive);
        SelectHeldActiveIn(drive);
    }

    /// <summary>The folder selected last time, if it is in <paramref name="root"/>, which has just come.</summary>
    private void SelectHeldActiveIn(ViewAllNodeViewModel root)
    {
        if (_heldActivePath is { } held && IsInRoot(held, root))
        {
            _heldActivePath = null;
            _ = SelectHeldActiveAsync(held, _heldActiveVersion);
        }
    }

    /// <summary>The folder selected last time, now that its root is here - unless the user has selected something since.</summary>
    private async Task SelectHeldActiveAsync(string path, long version)
    {
        ViewAllNodeViewModel? node;
        try
        {
            node = TryGetNode(path, out var known)
                ? known
                : PreferLightReveal && await _graph.MaterializeChainAsync(path) is { IsComplete: true, Node: { } named } ? named : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return;
        }

        if (node is not null && !_isDisposed && Selection.Version == version)
        {
            SelectOnly(node);
        }
    }

    private static bool IsInRoot(string path, ViewAllNodeViewModel root)
    {
        try
        {
            var chain = ViewAllPath.AncestorChain(path);
            return chain.Any(step => ViewAllPath.Equals(step, root.FullPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public bool TryGetNode(string path, out ViewAllNodeViewModel node)
        => _graph.TryGetNode(path, out node);

    /// <summary>True while <paramref name="node"/> is still the graph's node for its path: a folder read again has its children built anew.</summary>
    private bool IsLive(ViewAllNodeViewModel node)
        => _graph.TryGetNode(node.FullPath, out var live) && ReferenceEquals(live, node);

    /// <summary>Adds a non-drive location (WSL, UNC share) as its own root.</summary>
    public async Task<ViewAllNodeViewModel?> AddRootAsync(string path)
    {
        // A navigation like any other: a share slow to answer does not take
        // the selection back from somewhere the user went meanwhile, lands in
        // the pane it was asked in (see RevealAsync), and finds nothing to do
        // in a window closed while it waited.
        var holder = SelectionHolder;
        var ticket = TakeRevealTicket(holder);
        var node = await _graph.AddRootAsync(path);
        if (_isDisposed)
        {
            return null;
        }

        if (node is null)
        {
            MessageRequested?.Invoke($"Could not open {path}", true);
            return null;
        }

        await _graph.ExpandAsync(node);
        if (_isDisposed)
        {
            return null;
        }

        var superseded = ticket != LatestRevealOf(holder);
        if (!superseded && !ReferenceEquals(holder, SelectionHolder))
        {
            RebuildRenderSet();
            NavigationLandedAway?.Invoke(new NavigationLanding(holder, node, Select: true, Focus: true, Records: true));
            ScheduleSave();
            return node;
        }

        if (!superseded)
        {
            SelectOnly(node);
        }

        RebuildRenderSet();
        if (!superseded)
        {
            FocusNodeRequested?.Invoke(node, true);
        }

        ScheduleSave();
        return node;
    }

    public async Task ToggleAsync(ViewAllNodeViewModel node)
    {
        if (ActivationOverride is { } activate && activate(node))
        {
            return;
        }

        if (!node.IsDirectory)
        {
            OpenInDefaultApplication(node);
            return;
        }

        // Opening a folder that changed on disk while it was not wanted
        // current reads it again, which opens it.
        if (!node.IsExpanded && await RefreshIfStaleAsync(node))
        {
            ScheduleSave();
            return;
        }

        var result = await _graph.ToggleAsync(node);
        if (result.IsTruncated)
        {
            MessageRequested?.Invoke(
                $"{node.DisplayName} has more than {node.ChildLoadLimit:N0} items — use Load more to continue.",
                false);
        }

        ScheduleSave();
    }

    public async Task<ViewAllNodeViewModel?> ExpandAsync(ViewAllNodeViewModel node)
    {
        if (!node.IsDirectory)
        {
            return node;
        }

        // A folder that changed on disk while it was not wanted current is
        // read again, which opens it.
        var refreshed = !node.IsExpanded && await RefreshIfStaleAsync(node);
        if (!refreshed)
        {
            await _graph.ExpandAsync(node);
        }

        ScheduleSave();
        return node;
    }

    /// <summary>Explorer's View / Hidden items toggle.</summary>
    public bool ShowHiddenItems
    {
        get => _graph.Options.IncludeHidden;
    }

    /// <summary>
    /// The hidden items choice remembered from last time.  Only before
    /// <see cref="InitializeAsync"/>, so the drives are read that way from the
    /// start; nothing has read the choice yet, so nothing is told it changed.
    /// Later it is <see cref="SetShowHiddenItemsAsync"/>.
    /// </summary>
    public void RestoreShowHiddenItems(bool include)
    {
        if (_isInitialized || _isDisposed || _graph.Options.IncludeHidden == include)
        {
            return;
        }

        _graph.PresetOptions(_graph.Options with { IncludeHidden = include });
    }

    public async Task SetShowHiddenItemsAsync(bool include)
    {
        if (_graph.Options.IncludeHidden == include)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _graph.ApplyOptionsAsync(_graph.Options with { IncludeHidden = include });
            FollowRebuiltBranches();
            OnPropertyChanged(nameof(ShowHiddenItems));
            MessageRequested?.Invoke(include ? "Hidden items shown" : "Hidden items hidden", false);
        }
        finally
        {
            IsBusy = false;
        }

        ScheduleSave();
    }

    /// <summary>
    /// Applies the entry rules a file dialog imposes: whether files are listed
    /// at all, and which names survive the selected file type.  Branches that
    /// are already open are re-read so the change is visible immediately.
    /// </summary>
    public async Task ApplyEntryRulesAsync(bool showFiles, IEntryNameFilter? fileFilter, bool? includeHidden = null)
    {
        var options = _graph.Options with
        {
            ShowFiles = showFiles,
            FileFilter = fileFilter,
            IncludeHidden = includeHidden ?? _graph.Options.IncludeHidden
        };

        if (options == _graph.Options)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _graph.ApplyOptionsAsync(options);
            FollowRebuiltBranches();
            OnPropertyChanged(nameof(ShowHiddenItems));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Every open branch was read again under other rules - hidden items
    /// shown or hidden, a file dialog's type changed - and its nodes built
    /// anew: the focus takes the node its path has now, and the editor's copy
    /// of the selection the new nodes.  A focus the rules no longer show
    /// keeps its old node, which is no longer drawn (see <see cref="AlwaysRealized"/>).
    /// </summary>
    private void FollowRebuiltBranches()
    {
        if (_activeNode is { } active && !IsLive(active) && _graph.TryGetNode(active.FullPath, out var replacement))
        {
            SetActive(replacement, records: false);
        }

        SyncSelectionMirror();
    }

    /// <summary>
    /// Replaces what double-clicking a node does.  A picker returns the file to
    /// its caller instead of launching it.
    /// </summary>
    public Func<ViewAllNodeViewModel, bool>? ActivationOverride { get; set; }

    /// <summary>Folders currently hidden from the canvas, for the restore menu.</summary>
    public IReadOnlyList<string> HiddenPaths =>
        [.. _graph.HiddenPaths.OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)];

    public int HiddenCount => _graph.HiddenPaths.Count;

    /// <summary>
    /// Takes the selected folders and everything under them off the canvas.
    /// Only folders can be hidden, so only they are given nodes - a selection
    /// of ten thousand files and three folders makes three.
    ///
    /// The selection is cleared afterwards, and not as a courtesy: a selected
    /// node is realized whatever the culling says, so a folder hidden while it
    /// is still selected would stay on screen.
    /// </summary>
    public async Task HideSelectedAsync()
    {
        var folders = Selection.Items.Where(item => item.IsDirectory).Select(item => item.Path).ToArray();
        var resolved = new List<string>(folders.Length);
        foreach (var path in folders)
        {
            if (TryGetNode(path, out var known) && known.IsDirectory)
            {
                resolved.Add(known.FullPath);
            }
            else if (await MaterializeAsync(path) is { IsDirectory: true } made)
            {
                resolved.Add(made.FullPath);
            }
        }

        if (_isDisposed) return;
        // A later materialization may have yielded while an ancestor refresh
        // replaced an earlier object. Only current graph objects may be hidden.
        var targets = new List<ViewAllNodeViewModel>(resolved.Count);
        foreach (var path in resolved)
            if (TryGetNode(path, out var live) && live.IsDirectory)
            {
                _graph.Hide(live);
                targets.Add(live);
            }

        if (targets.Count == 0)
        {
            MessageRequested?.Invoke("Select a folder to hide.", false);
            return;
        }

        var parent = targets[0].Parent;
        Selection.Apply(new SelectionEdit
        {
            Clear = true,
            Anchor = parent?.FullPath,
            Focus = parent?.FullPath,
            Source = SelectionSource.Command
        });
        OnPropertyChanged(nameof(HiddenPaths));
        OnPropertyChanged(nameof(HiddenCount));
        FoldersHiddenByUser?.Invoke([.. targets.Select(node => node.FullPath)]);
        MessageRequested?.Invoke(
            targets.Count == 1
                ? $"{targets[0].DisplayName} hidden — bring it back from the canvas menu"
                : $"{targets.Count} folders hidden — bring them back from the canvas menu",
            false);
        RebuildRenderSet();
        InvalidateCanvas();
        ScheduleSave();
    }

    public void ShowHidden(string path)
    {
        if (!_graph.Show(path))
        {
            return;
        }

        OnPropertyChanged(nameof(HiddenPaths));
        OnPropertyChanged(nameof(HiddenCount));
        RebuildRenderSet();
        InvalidateCanvas();
        ScheduleSave();
    }

    public void ShowAllHidden()
    {
        if (!_graph.ShowAllHidden())
        {
            MessageRequested?.Invoke("Nothing is hidden.", false);
            return;
        }

        OnPropertyChanged(nameof(HiddenPaths));
        OnPropertyChanged(nameof(HiddenCount));
        RebuildRenderSet();
        InvalidateCanvas();
        ScheduleSave();
    }

    /// <summary>True when something in the selection was placed by hand.</summary>
    public bool HasHandPlacedSelection => SelectedNodes.Any(node => node.HasManualPosition);

    /// <summary>
    /// Puts the selected nodes back into the layout, leaving the rest of the
    /// canvas alone.
    /// </summary>
    public void ReturnSelectionToLayout()
    {
        var targets = SelectedNodes.Where(node => node.HasManualPosition).ToArray();
        if (!_graph.ReleasePositions(targets))
        {
            MessageRequested?.Invoke("Nothing here was placed by hand.", false);
            return;
        }

        RebuildRenderSet();
        InvalidateCanvas();
        MessageRequested?.Invoke(
            targets.Length == 1
                ? $"{targets[0].DisplayName} is back in the layout"
                : $"{targets.Length} items are back in the layout",
            false);
        ScheduleSave();
    }

    /// <summary>
    /// Told after a drag has finished, so the links can be worked out again for
    /// wherever the nodes ended up.
    /// </summary>
    public void NotifyNodesMoved()
    {
        _graph.RefreshLinks();
        RebuildRenderSet();
        InvalidateCanvas();
        ScheduleSave();
    }

    /// <summary>Re-reads the folder the list is showing, if it is showing one.</summary>
    public Task RefreshFolderListAsync() => FolderList.ReloadAsync();

    /// <summary>The folder whose area on the canvas a point falls in.</summary>
    public ViewAllNodeViewModel? FolderAt(Point graphPoint) => _graph.FolderAt(graphPoint);

    /// <summary>Makes a folder the current one, as clicking it would.</summary>
    public void SetActiveFolder(ViewAllNodeViewModel node)
    {
        if (node.IsDirectory)
        {
            ActiveNode = node;
        }
    }

    /// <summary>
    /// Which order each folder's children are laid out in on the tree,
    /// folders before files as always, and the rows of the folder list are
    /// in: each folder's own, or the default (see <see cref="FolderOrders"/>).
    /// The window shares these with the nested canvas.
    /// </summary>
    public FolderOrders Orders { get; } = new();

    /// <summary>
    /// The default order (<see cref="FolderOrders.Default"/>): what every
    /// folder without an order of its own is shown in.
    /// </summary>
    public ItemSort Sort
    {
        get => Orders.Default;
        set => Orders.SetDefault(value);
    }

    /// <summary>
    /// An order changed.  The list takes its folder's order, reordering the
    /// rows already read without reading anything again.  The tree is laid
    /// out again the way opening a folder does - every position the user
    /// chose kept, and the view panned by however far the folder being
    /// looked at was carried, so it stays where it was and its children
    /// change places beneath it.
    ///
    /// While the tree canvas is not on screen the tree is left as it is and
    /// laid out in the new orders when it comes back (see <see cref="IsCanvasShown"/>):
    /// a tree opened wide is milliseconds to lay out again, which a click on a
    /// header over the nested canvas has no business spending on a picture
    /// nobody can see.
    /// </summary>
    private void OnOrdersChanged(string? folder)
    {
        FolderList.Sort = Orders.SortOf(FolderList.FolderPath);
        OnPropertyChanged(nameof(Sort));
        if (_isDisposed)
        {
            return;
        }

        if (!_isCanvasShown)
        {
            _isSortBehind = true;
            return;
        }

        // The folder in view is the open one selected, or else the folder
        // the selection sits in: that is what reorders, so that is what
        // is held still.
        var held = _activeNode is { IsDirectory: true, IsExpanded: true } open ? open : _activeNode?.Parent ?? _activeNode;
        _graph.Resort(Orders.Default, held);
        InvalidateCanvas();
    }

    /// <summary>Throws away every hand-placed position and rebuilds the tree.</summary>
    public void RelayoutCanvas()
    {
        _graph.Relayout();
        RebuildRenderSet();
        InvalidateCanvas();
        MessageRequested?.Invoke("Canvas tidied", false);
        ScheduleSave();
    }

    public void CollapseAll()
    {
        _graph.CollapseAll();
        ScheduleSave();
    }

    /// <summary>
    /// F5 on a folder.  The tree canvas reads the branch again here and now;
    /// everything else that shows the folder hears of it through the change
    /// hub at once, like any change on disk - the list merges it in, the
    /// nested canvas reads it again - and the nested canvas's tree is asked
    /// to take everything it read below the folder as out of date too
    /// (<see cref="DeepRefreshRequested"/>).
    /// </summary>
    public async Task RefreshAsync(ViewAllNodeViewModel node)
    {
        if (!node.IsDirectory)
        {
            return;
        }

        _changes?.Touch(node.FullPath, immediate: true);
        DeepRefreshRequested?.Invoke(node.FullPath);
        NoteGraphReadDirectly(node.FullPath);
        await _graph.RefreshBranchAsync(node);
        ReleaseRemovedFocus(node);
        ScheduleSave();
        PathRefreshed?.Invoke(node.FullPath);
    }

    /// <summary>
    /// Re-reads a directory the window has just changed - a file operation.
    /// Everything else that shows it - the list, the nested canvas - hears of
    /// it through the change hub at once (<see cref="ChangeHub.Touch"/>), as
    /// it hears of any change on disk, and with the hub's spacing between two
    /// reads of one folder when the watch reports the same change a moment
    /// later.  The tree canvas reads it here and now if it has it: what the
    /// operation does next - reveal what it made - finds it there.
    /// One that has never been read has nothing to re-read, but it can still
    /// hold children brought in by name - a folder selected on the nested
    /// canvas, say - and those are checked against the disk instead, so
    /// deleting or renaming one does not leave its node behind for the next
    /// reveal to find.
    /// </summary>
    public async Task RefreshPathAsync(string path)
    {
        _changes?.Touch(path, immediate: true);
        if (_graph.TryGetNode(path, out var node))
        {
            NoteGraphReadDirectly(path);
            if (node.AreChildrenLoaded)
            {
                await _graph.RefreshBranchAsync(node);
                ScheduleSave();
            }
            else if (await _graph.PruneMissingChildrenAsync(node) > 0)
            {
                ScheduleSave();
            }

            ReleaseRemovedFocus(node);
        }

        await PruneSelectionAsync(path);

        // With no hub the list is not told of the change by anything else.
        if (_changes is null && string.Equals(path, FolderList.FolderPath, StringComparison.OrdinalIgnoreCase))
        {
            await FolderList.ReloadAsync();
        }

        PathRefreshed?.Invoke(path);
    }

    /// <summary>
    /// After a refresh built a branch's nodes anew, the focus may be a node
    /// the graph no longer has: the one with its path takes its place, and
    /// when there is none - it is gone from disk, or the read left it out -
    /// and nothing else is selected, the focus moves to the folder that was
    /// refreshed.  Only the focus, as Explorer's caret moves: selected, the
    /// folder would be what a second Delete recycles.  What is still selected
    /// stays, for the folder's own pruning to let go of once the disk says it
    /// is gone.  The editor's copy of the selection is brought up to date
    /// with the new nodes.
    /// </summary>
    private void ReleaseRemovedFocus(ViewAllNodeViewModel refreshed)
    {
        if (ActiveNode is { } active
            && !(_graph.TryGetNode(active.FullPath, out var replacement) && ReferenceEquals(replacement, active)))
        {
            if (replacement is not null)
            {
                SetActive(replacement, records: false);
            }
            else if (Selection.Count == 0 || Selection.Count == 1 && Selection.Contains(active.FullPath))
            {
                if (_graph.TryGetNode(refreshed.FullPath, out var folder))
                {
                    Selection.Apply(new SelectionEdit { Focus = folder.FullPath, Source = SelectionSource.Command });
                    if (!ReferenceEquals(ActiveNode, folder))
                    {
                        SetActive(folder, records: false);
                    }
                }
            }
        }

        SyncSelectionMirror();
    }

    /// <summary>
    /// A folder was read again: whatever was selected directly in it and is
    /// no longer on disk is let go, in one change.  The folder is listed once,
    /// off the UI thread - a folder of ten thousand selected files is one
    /// read, not ten thousand questions - and only when something in it is
    /// selected at all.  Only what was selected when the listing began can be
    /// missing from it: a folder made or an item renamed, and selected, while
    /// a share took seconds to list is not in the listing, and stays.
    /// </summary>
    private async Task PruneSelectionAsync(string folder)
    {
        if (!AnySelectedIn(folder))
        {
            return;
        }

        var asked = SelectedIn(folder);
        HashSet<string> present;
        try
        {
            var normalized = ViewAllPath.Normalize(folder);
            present = await Task.Run(() => new HashSet<string>(
                new DirectoryInfo(ViewAllFileSystemService.ForWindows(normalized)).EnumerateFileSystemInfos()
                    .Select(entry => Path.Combine(normalized, entry.Name)), StringComparer.OrdinalIgnoreCase));
        }
        catch (DirectoryNotFoundException)
        {
            present = [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unreadable is not the same as empty: nothing is let go.
            return;
        }

        bool Stays(string path) => present.Contains(path) || !asked.Contains(path);
        Selection.RemoveMissingUnder(folder, Stays);
        foreach (var kept in _keptSelections)
        {
            kept.RemoveMissingUnder(folder, Stays);
        }
    }

    /// <summary>What every pane's selection holds directly inside <paramref name="folder"/>.</summary>
    private HashSet<string> SelectedIn(string folder)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Collect(ItemSelection selection)
        {
            if (selection.CountIn(folder) == 0)
            {
                return;
            }

            foreach (var path in selection.Paths)
            {
                if (ItemSelection.ParentOf(path).Equals(folder.AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    selected.Add(path);
                }
            }
        }

        Collect(Selection);
        foreach (var kept in _keptSelections)
        {
            Collect(kept);
        }

        return selected;
    }

    public async Task LoadMoreAsync(ViewAllNodeViewModel node)
    {
        var result = await _graph.LoadMoreAsync(node);
        if (result.WasLoaded)
        {
            MessageRequested?.Invoke($"Loaded {node.LoadedChildCount:N0} items in {node.DisplayName}", false);
        }

        ScheduleSave();
    }

    /// <summary>
    /// Expands every ancestor of <paramref name="path"/> and selects it.  Only
    /// the folders on the way are read, never their siblings' subtrees.
    ///
    /// With <see cref="PreferLightReveal"/> on, the ancestors are not expanded:
    /// the path is brought in by name, one node per missing step, and the rest -
    /// selecting, flying there, saving, the message for a path that is not
    /// there - is the same.
    /// </summary>
    public async Task<ViewAllNodeViewModel?> RevealPathAsync(string path, bool focus = true, bool select = true)
        => (await RevealAsync(path, focus, select)).Node;

    /// <summary>
    /// A navigation's place in line, taken before whatever it has to wait for
    /// first: a path typed into the address bar is checked off the interface
    /// thread before it is revealed, and anything asked for meanwhile - a
    /// favourite, Back - is newer and must win.  Hand it to
    /// <see cref="RevealAsync"/>.  The navigation is for the selection as it
    /// is now (<see cref="SelectionHolder"/>): the pane it is asked in.
    /// </summary>
    public int BeginNavigation() => TakeRevealTicket(SelectionHolder);

    /// <summary>Whether no navigation has started since <paramref name="ticket"/> was taken in the pane it was taken in.</summary>
    public bool IsLatestNavigation(int ticket) => TryGetRevealHolder(ticket, out _);

    /// <summary>A new ticket, the latest in <paramref name="holder"/>'s pane (see <see cref="_latestReveals"/>).</summary>
    private int TakeRevealTicket(object? holder)
    {
        var ticket = ++_revealTicket;
        _latestReveals.AddOrUpdate(holder ?? this, new StrongBox<int>(ticket));
        return ticket;
    }

    /// <summary>The latest ticket taken in <paramref name="holder"/>'s pane, or 0 before any.</summary>
    private int LatestRevealOf(object? holder)
        => _latestReveals.TryGetValue(holder ?? this, out var latest) ? latest.Value : 0;

    /// <summary>The pane <paramref name="ticket"/> was taken in, while no newer one has been taken there.</summary>
    private bool TryGetRevealHolder(int ticket, out object? holder)
    {
        foreach (var (key, latest) in _latestReveals)
        {
            if (latest.Value == ticket)
            {
                holder = ReferenceEquals(key, this) ? null : key;
                return true;
            }
        }

        holder = null;
        return false;
    }

    /// <summary>
    /// <see cref="RevealPathAsync"/>, with what a caller that must not act on
    /// the wrong thing needs to know about how it ended.
    /// </summary>
    /// <param name="ticket">
    /// From <see cref="BeginNavigation"/>, when the navigation was asked for
    /// before this call; otherwise a reveal that selects takes a new one.
    /// </param>
    /// <param name="records">
    /// Whether the selection is a step Back and Forward can retrace; a history
    /// step's own selection is not.
    /// </param>
    /// <param name="exact">
    /// Select and fly only when the path itself was found.  A path that is
    /// gone reveals the deepest folder still there, and selecting that for a
    /// search result or a row that vanished would point Delete at the folder.
    /// </param>
    /// <param name="selectionVersion">
    /// The <see cref="ItemSelection.Version"/> the reveal was asked at, when
    /// any pick made since is newer than it - a row clicked while the one
    /// before still reads its way down: then, as when superseded, nothing is
    /// selected or flown to.
    /// </param>
    /// <param name="holdGone">
    /// Handed the message that the path is no longer inside the folder the
    /// reveal got to, in place of the toast: for a caller that says so only
    /// once the disk has.  A share that did not answer for a moment has not
    /// lost anything.
    /// </param>
    public async Task<RevealOutcome> RevealAsync(
        string path,
        bool focus = true,
        bool select = true,
        int? ticket = null,
        bool records = true,
        bool exact = false,
        long? selectionVersion = null,
        Action<string>? holdGone = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return default;
        }

        // The pane of a split view this is for: the one being worked with
        // when it was asked for, which it goes on being for however long the
        // way down takes.
        var holder = ticket is { } begun && TryGetRevealHolder(begun, out var askedIn) ? askedIn : SelectionHolder;

        // Going somewhere while an earlier navigation is still reading its way
        // down - Back twice in quick succession, a second favourite clicked
        // while a share takes its time - must end where the last one asked
        // to, whichever finishes last.  Only a reveal that selects takes a
        // new ticket; one that flies there without selecting stands down for
        // any that starts after it.  Either only in its own pane.
        var taken = ticket ?? (select ? TakeRevealTicket(holder) : LatestRevealOf(holder));

        IReadOnlyList<string> chain;
        string normalized;
        try
        {
            normalized = ViewAllPath.Normalize(path);
            chain = ViewAllPath.AncestorChain(normalized);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            MessageRequested?.Invoke($"That path cannot be opened: {ex.Message}", true);
            return default;
        }

        ViewAllNodeViewModel? node = null;
        if (PreferLightReveal)
        {
            var result = await _graph.MaterializeChainAsync(normalized);
            node = result.Node;
            if (result.MissingStep is { } missing)
            {
                ReportMissing(missing, node, holdGone);
            }
        }
        else
        {
            // A UNC share or a WSL distribution is not a DriveInfo root, so it only
            // enters the graph when it is explicitly added.
            if (chain.Count > 0 && !_graph.TryGetNode(chain[0], out _))
            {
                await _graph.AddRootAsync(chain[0]);
            }

            // A folder on the way can be read again while this waits for it -
            // a refresh of a busy folder above builds its children anew - and
            // the next step adopted under the node it let go of would hang
            // from nothing: the folder, read again, would never show it.  The
            // walk then starts again from what is there now; the last of
            // three goes walks on as ever, as the light reveal gives up.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                node = null;
                var lost = false;
                foreach (var step in chain)
                {
                    // Given way to - Back twice on a slow share - it opens no
                    // more folders on its way: they would only reflow the tree
                    // under the navigation that came after it.
                    if (select && node is not null && taken != LatestRevealOf(holder))
                    {
                        break;
                    }

                    if (!_graph.TryGetNode(step, out var found))
                    {
                        // The step may simply be filtered out of its parent - hidden,
                        // or not of the file type on offer.  Asking for it by name
                        // outranks that.
                        var adopted = node is null ? null : await _graph.AdoptChildAsync(node, step);
                        if (adopted is null)
                        {
                            // Not given because the folder was read again while
                            // the disk was asked, and is another node now: lost,
                            // like a folder read again while it was opened.
                            if (attempt < 2 && node is not null && !IsLive(node))
                            {
                                lost = true;
                                break;
                            }

                            ReportMissing(step, node, holdGone);
                            break;
                        }

                        found = adopted;
                    }

                    node = found;
                    var isDestination = string.Equals(step, normalized, StringComparison.OrdinalIgnoreCase);
                    if (!isDestination && node.IsDirectory)
                    {
                        await _graph.ExpandAsync(node);
                        if (attempt < 2 && !IsLive(node))
                        {
                            lost = true;
                            break;
                        }
                    }
                }

                if (!lost)
                {
                    break;
                }
            }
        }

        if (node is null)
        {
            return default;
        }

        // Superseded: the folders on the way are open, as far as it got before
        // it was given way to, and the node exists, but the selection, the
        // view and the history belong to the navigation that came after.
        // Short of the path, with only the exact path wanted: the message
        // above has said it is gone, and the selection stays where it was.
        var superseded = taken != LatestRevealOf(holder) || selectionVersion is { } version && version != Selection.Version;
        var isExact = ViewAllPath.Equals(node.FullPath, normalized);
        var acts = !superseded && (isExact || !exact);

        // Asked for in a pane of a split view that has stopped being the one
        // worked with meanwhile: the selection is another pane's by now, and
        // so are the history and the camera a navigation moves.  The pane it
        // was asked in goes there itself, and to its caller it is as good as
        // superseded - nothing here was selected or flown to.
        if (acts && (select || focus) && !ReferenceEquals(holder, SelectionHolder))
        {
            RebuildRenderSet();
            NavigationLandedAway?.Invoke(new NavigationLanding(holder, node, select, focus, records));
            ScheduleSave();
            return new RevealOutcome(node, isExact, Superseded: true);
        }

        if (select && acts)
        {
            SelectOnly(node, records);
        }

        RebuildRenderSet();
        if (focus && acts)
        {
            // A light by-name node can be replaced while its ancestors are
            // expanded for the tree canvas. Keep the focus on the live node
            // without turning a focus-only reveal into a single selection or
            // a new navigation (especially when several files are selected).
            if (!select && Selection.Focus is { } currentFocus && ViewAllPath.Equals(currentFocus, node.FullPath))
                SetActive(node, records: false);
            FocusNodeRequested?.Invoke(node, true);
        }

        ScheduleSave();
        return new RevealOutcome(node, isExact, superseded);
    }

    /// <summary>
    /// Says that a path asked for is not there: its drive, or the step of it
    /// no longer inside the folder the reveal got to - that one handed to
    /// <paramref name="holdGone"/> instead when there is one (see <see cref="RevealAsync"/>).
    /// </summary>
    private void ReportMissing(string missing, ViewAllNodeViewModel? reached, Action<string>? holdGone)
    {
        if (reached is null)
        {
            MessageRequested?.Invoke("That drive is not available on this machine.", true);
            return;
        }

        var message = $"{Path.GetFileName(missing)} is no longer inside {reached.DisplayName}.";
        if (holdGone is not null)
        {
            holdGone(message);
            return;
        }

        MessageRequested?.Invoke(message, true);
    }

    /// <summary>
    /// Selects a path picked somewhere other than this graph - on the nested
    /// canvas - bringing its node into being first if it has to.  Two clicks in
    /// quick succession both have to read their way down, and the second one
    /// must win even if the first finishes later.
    /// </summary>
    public async Task<ViewAllNodeViewModel?> SelectPathAsync(string path)
    {
        var ticket = ++_selectTicket;
        var node = TryGetNode(path, out var known)
            ? known
            : await RevealPathAsync(path, focus: false, select: false);

        // Superseded - something else was selected while this was reading its
        // way down - or not the thing asked for: a path that is gone reveals
        // its parent, and selecting that instead would point Delete at it.
        if (node is null || ticket != _selectTicket || !IsExactly(node, path))
        {
            return null;
        }

        SelectOnly(node);
        ScheduleSave();
        return node;
    }

    /// <summary>
    /// Adds a path to the selection, or takes it out, with the anchor and the
    /// focus on it - Ctrl+click.  One change; the focus is given a node the
    /// way every focus is (see <see cref="OnSelectionChanged"/>).
    /// </summary>
    public Task ToggleSelectionAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.CompletedTask;
        }

        var selected = Selection.Contains(path);
        var isDirectory = TryGetNode(path, out var known) ? known.IsDirectory : BrowseArchives
            ? Services.Archives.ArchiveService.IsFolderLike(path, BrowseArchives) : Directory.Exists(path);
        Selection.Apply(new SelectionEdit
        {
            Added = selected ? [] : [new SelectionItem(path, isDirectory, known?.Entry.SizeBytes ?? 0)],
            Removed = selected ? [path] : [],
            Anchor = path,
            Focus = path,
            Source = SelectionSource.Canvas
        });
        return Task.CompletedTask;
    }

    private static bool IsExactly(ViewAllNodeViewModel node, string path)
    {
        try
        {
            return ViewAllPath.Equals(node.FullPath, ViewAllPath.Normalize(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Makes sure a path has a node, without selecting it or moving the canvas.</summary>
    public Task<ViewAllNodeViewModel?> MaterializeAsync(string path) => MaterializeBarrierForChecks is { } barrier
        ? MaterializeAfterBarrierAsync(path, barrier) : MaterializeAsync(path, holdGone: null);

    /// <summary>Owned deterministic concurrency checks only; absent in ordinary use.</summary>
    internal Func<string, Task>? MaterializeBarrierForChecks { get; set; }
    private async Task<ViewAllNodeViewModel?> MaterializeAfterBarrierAsync(string path, Func<string, Task> barrier)
    {
        await barrier(path);
        return await MaterializeAsync(path, holdGone: null);
    }

    /// <param name="path">The path to give a node.</param>
    /// <param name="holdGone">Handed the message that the path is gone instead of the toast (see <see cref="RevealAsync"/>).</param>
    private async Task<ViewAllNodeViewModel?> MaterializeAsync(string path, Action<string>? holdGone)
    {
        var node = TryGetNode(path, out var known)
            ? known
            : (await RevealAsync(path, focus: false, select: false, holdGone: holdGone)).Node;
        return node is not null && IsExactly(node, path) ? node : null;
    }

    /// <summary>
    /// One node alone, anchor and focus on it: a click on the tree, a row
    /// picked, going somewhere.  One change of <see cref="Selection"/>, and a
    /// navigation for back and forward.
    /// </summary>
    public void SelectOnly(ViewAllNodeViewModel node) => SelectOnly(node, records: true);

    /// <param name="records">Whether going there is a step for Back and Forward; a history step's own is not.</param>
    private void SelectOnly(ViewAllNodeViewModel node, bool records)
    {
        // Any selection made now outranks a click still reading its way down.
        _selectTicket++;
        Selection.ReplaceSingle(node.FullPath, node.IsDirectory, node.Entry.SizeBytes ?? 0, SelectionSource.Navigation, records);

        // The same node again, or the path spelled another way: the focus is
        // this node whatever the selection made of it.
        SetActive(node, records);
        SyncSelectionMirror();
    }

    public void Activate(ViewAllNodeViewModel node)
    {
        ActiveNode = node;
        UpdateStatus();
    }

    /// <summary>
    /// Opens a file the way a double-click in Explorer does.  Whether it is a
    /// folder after all is asked on the thread pool, and the Shell is handed
    /// the file there too: a file on a share that has gone to sleep holds
    /// whoever asks about it for twenty seconds, and then the Shell for as
    /// long again, and this thread is every window's.  A folder - a link to
    /// one, opened as a file - goes to <see cref="NativeShellService.Open"/>
    /// here, which hands it to this app's own windows when they stand in for
    /// Explorer.  Nothing is opened for a window closed meanwhile.
    /// </summary>
    public void OpenInDefaultApplication(ViewAllNodeViewModel node) => _ = OpenInDefaultApplicationAsync(node);

    private async Task OpenInDefaultApplicationAsync(ViewAllNodeViewModel node)
    {
        var path = node.FullPath;
        try
        {
            if (await Task.Run(() => Directory.Exists(path)))
            {
                if (!_isDisposed)
                {
                    NativeShellService.Open(path, isDirectory: true);
                }

                return;
            }

            if (_isDisposed)
            {
                return;
            }

            // Off an STA thread the runtime makes one of its own for the Shell.
            await Task.Run(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose());
        }
        catch (Exception ex)
        {
            if (!_isDisposed)
            {
                MessageRequested?.Invoke($"Could not open {node.DisplayName}: {ex.Message}", true);
            }
        }
    }

    /// <summary>
    /// Strictly what the user selected.  Destructive commands use this, so an
    /// empty canvas selection can never be turned into "delete the folder I am
    /// merely looking at".  The same list until the selection changes.
    /// </summary>
    public IReadOnlyList<string> SelectedPaths => Selection.Paths;

    /// <summary>Selection, falling back to the focus for read-only commands (see <see cref="FocusedPath"/>).</summary>
    public IReadOnlyList<string> SelectedOrActivePaths
        => Selection.Count > 0
            ? Selection.Paths
            : _pendingFocus is { } pending ? [pending] : ActiveNode is null ? [] : [ActiveNode.FullPath];

    /// <summary>
    /// Where the focus is, for a command: <see cref="ActivePath"/>, or while
    /// the graph is still finding the node of a focus it has only just been
    /// given, that focus.  A pane of a split view just clicked hands the
    /// window a selection the graph may not have read its way down to yet,
    /// and Up, New folder or Paste in the moment before it has must act on
    /// that pane's place, not on the other pane's.
    /// </summary>
    public string FocusedPath => _pendingFocus ?? ActivePath;

    /// <summary>
    /// The folder that a new item or a paste should land in: with several
    /// items selected in one folder, that folder, as in an Explorer window;
    /// otherwise the focus - a folder itself, a file's folder.
    /// </summary>
    public string? TargetDirectory
    {
        get
        {
            if (Selection.Count > 1 && Selection.Container is { } container)
            {
                return container;
            }

            if (_pendingFocus is { } pending)
            {
                // Not given its node yet: what the selection says it is, or
                // else what the disk does.
                var isDirectory = Selection.TryGetItem(pending, out var item) ? item.IsDirectory : BrowseArchives
                    ? Services.Archives.ArchiveService.IsFolderLike(pending, BrowseArchives) : Directory.Exists(pending);
                return isDirectory ? pending : Path.GetDirectoryName(pending);
            }

            var node = ActiveNode;
            if (node is null)
            {
                return null;
            }

            return node.IsDirectory ? node.FullPath : Path.GetDirectoryName(node.FullPath);
        }
    }

    public ViewAllNodeViewModel? FindNearestDropTarget(Point graphPoint, double maximumDistance)
        => _graph.FindNearestDropTarget(graphPoint, maximumDistance);

    public void SetDropTarget(ViewAllNodeViewModel? target)
    {
        if (ReferenceEquals(_dropTarget, target))
        {
            return;
        }

        if (_dropTarget is not null)
        {
            _dropTarget.IsDropTarget = false;
        }

        _dropTarget = target;
        if (_dropTarget is not null)
        {
            _dropTarget.IsDropTarget = true;
        }
    }

    /// <summary>The program a dragged file would be handed to.</summary>
    public void SetRunTarget(ViewAllNodeViewModel? target)
    {
        if (ReferenceEquals(_runTarget, target))
        {
            return;
        }

        if (_runTarget is not null)
        {
            _runTarget.IsRunTarget = false;
        }

        _runTarget = target;
        if (_runTarget is not null)
        {
            _runTarget.IsRunTarget = true;
        }
    }

    /// <summary>Colours every path given - the selection's, which need not have nodes.</summary>
    public void ApplyAccent(IEnumerable<string> paths, string? accentHex)
    {
        BeginMarkBatch();
        try
        {
            foreach (var path in paths)
            {
                bool? isDirectory = TryGetNode(path, out var node)
                    ? node.IsDirectory
                    : Selection.TryGetItem(path, out var selected) ? selected.IsDirectory : null;
                _marks.SetAccent(path, accentHex, isDirectory);
            }
        }
        finally
        {
            EndMarkBatch();
        }

        ScheduleSave();
    }

    /// <summary>
    /// How many batches of marks are being changed right now (see
    /// <see cref="BeginMarkBatch"/>), and whether one of them has recoloured
    /// a node of the graph.
    /// </summary>
    private int _markBatches;
    private bool _markBatchRecoloured;

    /// <summary>
    /// Many marks about to change at once - a selection of thousands coloured
    /// or cleared, a folder renamed with marks inside it.  Each node still
    /// takes its colour as its mark changes, but the tree canvas's batched
    /// layers, which are drawn again whole, are told once, after the last
    /// (<see cref="EndMarkBatch"/>), rather than once a mark.
    /// </summary>
    private void BeginMarkBatch() => _markBatches++;

    private void EndMarkBatch()
    {
        if (--_markBatches > 0 || !_markBatchRecoloured)
        {
            return;
        }

        _markBatchRecoloured = false;
        InvalidateCanvas();
    }

    public void ApplyNote(ViewAllNodeViewModel node, string? note) => ApplyNote(node.FullPath, note);

    /// <summary>Keeps a note on a path, which need not be on the canvas: a folder's own menu writes one for the folder it names.</summary>
    public void ApplyNote(string path, string? note)
    {
        _marks.SetNote(path, note);
        ScheduleSave();
    }

    /// <summary>Called by the editor whenever the viewport moved, zoomed or resized.</summary>
    public void UpdateViewport(Point location, Size size, double zoom)
    {
        var zoomChanged = Math.Abs(_viewportZoom - zoom) > double.Epsilon;
        _viewportLocation = location;
        _viewportSize = size.Width > 0 && size.Height > 0 ? size : _viewportSize;
        _viewportZoom = zoom;

        if (zoomChanged)
        {
            OnPropertyChanged(nameof(ViewportZoom));
            OnPropertyChanged(nameof(ZoomLabel));
        }

        OnPropertyChanged(nameof(ViewportLocation));

        // Restarting on every viewport event would starve the tick during a
        // continuous pan and leave the culling on a stale viewport.  A canvas
        // that is not showing is culled once, when it comes back.
        if (_isCanvasShown && !_renderThrottle.IsEnabled)
        {
            _renderThrottle.Start();
        }

        ScheduleSave();
    }

    /// <summary>Bounds of the current selection, or empty when nothing is selected.</summary>
    public Rect GetSelectionExtent()
    {
        var selected = SelectedNodes.Where(node => node.HasLayoutPosition).ToArray();
        if (selected.Length == 0)
        {
            return Rect.Empty;
        }

        var extent = selected[0].Bounds;
        foreach (var node in selected.Skip(1))
        {
            extent.Union(node.Bounds);
        }

        extent.Inflate(200, 200);
        return extent;
    }

    public Rect GetContentExtent()
    {
        var visible = _graph.Nodes.Where(node => node.IsTreeVisible && node.HasLayoutPosition).ToArray();
        if (visible.Length == 0)
        {
            return new Rect(0, 0, ViewAllNodeViewModel.DefaultWidth, ViewAllNodeViewModel.DefaultHeight);
        }

        var extent = visible[0].Bounds;
        foreach (var node in visible.Skip(1))
        {
            extent.Union(node.Bounds);
        }

        extent.Inflate(80, 80);
        return extent;
    }

    /// <summary>
    /// Set for the replacement of another program's Windows file dialog: its
    /// camera and layout are never written, and the marks - a file the
    /// everyday window writes too - only when a mark was changed in it, so a
    /// dialog left open cannot put back an older copy of them.
    /// </summary>
    internal bool SuppressWrites
    {
        get => _suppressWrites;
        set
        {
            if (value && !_suppressWrites)
            {
                _marks.MarkChanged += OnMarkChangedHere;
            }

            _suppressWrites = value;
        }
    }

    private bool _suppressWrites;
    private bool _marksChangedHere;

    private void OnMarkChangedHere(string key, FolderMark mark) => _marksChangedHere = true;

    public async Task SaveAsync()
    {
        if (!_isInitialized || _isDisposed)
        {
            return;
        }

        if (_suppressWrites)
        {
            if (_marksChangedHere)
            {
                try
                {
                    await _marks.SaveAsync();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            return;
        }

        try
        {
            // Not over a workspace still being put back (see _isStateRestored);
            // the marks are a file of their own, and go as always.
            if (_isStateRestored)
            {
                var state = _graph.CaptureState(new ViewAllViewportState(_viewportLocation, _viewportZoom));

                // Selected last time in a share not here yet: still that.
                var active = _heldActivePath is { } held && Selection.Version == _heldActiveVersion
                    ? held
                    : ActiveNode?.FullPath ?? string.Empty;

                // Split: each pane's own.  The selection is the pane being
                // worked with's, and the other pane says what it has.
                var other = OtherPanePath?.Invoke();
                state.ActivePath = IsSecondPaneActive ? other ?? string.Empty : active;
                state.NestedCamera = NestedCamera;
                state.SecondPane = SecondPane is not { } second || OtherPanePath is null
                    ? SecondPane
                    : second with { ActivePath = IsSecondPaneActive ? active : other };
                await _store.SaveAsync(state);
            }
            else
            {
                _saveHeldForRestore = true;
            }

            await _marks.SaveAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a layout snapshot must never interrupt file navigation.
        }
    }

    public void ScheduleSave()
    {
        if (!_isInitialized || _isDisposed)
        {
            return;
        }

        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _renderThrottle.Stop();
        _saveDebounce.Stop();
        Changes = null;
        _graph.GraphChanged -= OnGraphChanged;
        _graph.NodeCreated -= OnNodeCreated;
        _graph.DriveAdded -= OnDriveAdded;
        _fillTimer.Stop();
        _graph.LayoutChanged -= OnLayoutChanged;
        _marks.MarkChanged -= OnMarkChanged;
        SelectedNodes.CollectionChanged -= OnSelectedNodesChanged;
        Selection.Changed -= OnSelectionChanged;
        _graph.Dispose();
    }

    private async Task ToggleSelectedAsync()
    {
        if (ActiveNode is { } target)
        {
            await ToggleAsync(target);
        }
    }

    private async Task RefreshSelectedAsync()
    {
        if (ActiveNode is { } target)
        {
            await RefreshAsync(target.IsDirectory ? target : target.Parent ?? target);
        }
    }

    private async Task LoadMoreForSelectionAsync()
    {
        var target = SelectedNodes.LastOrDefault(node => node.IsTruncated) ?? ActiveNode;
        if (target is { IsTruncated: true })
        {
            await LoadMoreAsync(target);
        }
    }

    // ---- changes on disk -----------------------------------------------------------
    //
    // The change hub hands each changed folder, once per burst of changes, to
    // whoever registered it.  The three consumers are independent: the nested
    // canvas's tree reads again what is on screen, the list merges a new read
    // into its rows, the tree canvas reads the branch again when anybody can
    // see it.  Before any of them, the selection follows the change once.

    void IChangeSink.FolderChanged(ChangeConsumer consumer, object target, in FolderChange change)
    {
        if (_isDisposed)
        {
            return;
        }

        if (_lastChange != (change.Key, change.FirstTicks, change.Kinds))
        {
            _lastChange = (change.Key, change.FirstTicks, change.Kinds);
            FollowChangeWithSelection(change);
        }

        switch (consumer)
        {
            case ChangeConsumer.Nested:
                foreach (var sink in _nestedChanges)
                {
                    sink.FolderChanged(consumer, target, change);
                }

                break;
            case ChangeConsumer.List when ReferenceEquals(target, FolderList):
                FolderList.OnFolderChanged(change);
                break;
            case ChangeConsumer.Graph when target is ViewAllNodeViewModel node:
                OnGraphFolderChanged(node, change);
                break;
        }
    }

    /// <summary>
    /// Changes under <paramref name="root"/> may have been missed - its watch
    /// overflowed or was armed again - so everything that shows a folder under
    /// it takes itself as out of date: the nested canvas's tree by its epoch,
    /// the list by reading its folder again, the tree canvas by reading the
    /// branches it shows under the root, now if it is on screen and otherwise
    /// when it comes back.
    /// </summary>
    void IChangeSink.EpochBumped(WatchRoot root)
    {
        if (_isDisposed)
        {
            return;
        }

        foreach (var sink in _nestedChanges)
        {
            sink.EpochBumped(root);
        }

        FolderList.OnEpochBumped(root);
        foreach (var (path, node) in _graphInterest)
        {
            if (IsUnder(path, root))
            {
                MarkGraphChanged(node);
            }
        }
    }

    /// <summary>A watch that is polled is due its look: each nested canvas's tree looks at what it has on screen, the list at its folder.</summary>
    void IChangeSink.PollDue(WatchRoot root)
    {
        if (_isDisposed)
        {
            return;
        }

        foreach (var sink in _nestedChanges)
        {
            sink.PollDue(root);
        }

        FolderList.OnPollDue(root);
    }

    private static bool IsUnder(string path, WatchRoot root)
    {
        foreach (var prefix in root.Prefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && (path.Length == prefix.Length || prefix.EndsWith(Path.DirectorySeparatorChar) || path[prefix.Length] == Path.DirectorySeparatorChar))
            {
                return true;
            }

            // The root itself, as the graph spells it: a share's root is
            // \\server\share, without the separator its prefix ends in.
            if (prefix.EndsWith(Path.DirectorySeparatorChar)
                && prefix.AsSpan(0, prefix.Length - 1).Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The selection's part in a change, taken once however many consumers the
    /// change goes to.  Something selected in the folder that is no longer on
    /// disk is let go (<see cref="PruneSelectionAsync"/>, one listing off this
    /// thread); something selected that was renamed within the folder is
    /// selected under its new name - as Explorer keeps a renamed item selected
    /// - and whatever mark the old name carried moves with it.
    ///
    /// The renames are taken as the chain they are.  A name a safe-save gives
    /// back later in the same change is still the item the user knows by it:
    /// an editor's safe-save renames the document to a backup and its new
    /// copy to the document's name, and the selection and the mark stay with
    /// the name, not with the backup about to be deleted - only taking the
    /// spelling it was given back in, should that differ.  Any other chain -
    /// a swap, a batch rename's shift - is items moving, and they are followed.
    /// </summary>
    private void FollowChangeWithSelection(in FolderChange change)
    {
        if (!change.Renames.IsEmpty)
        {
            var renames = change.Renames.Span;
            for (var index = 0; index < renames.Length; index++)
            {
                var pair = renames[index];
                if (NamedAgain(renames, index, change.Key) is { } again)
                {
                    if (!string.Equals(again, pair.OldName, StringComparison.Ordinal))
                    {
                        FollowRename(Path.Combine(change.Key, pair.OldName), Path.Combine(change.Key, again));
                    }

                    continue;
                }

                FollowRename(Path.Combine(change.Key, pair.OldName), Path.Combine(change.Key, pair.NewName));
            }
        }

        if ((change.Kinds & (ChangeKinds.Structural | ChangeKinds.Gone)) != 0 && AnySelectedIn(change.Key))
        {
            _ = PruneSelectionAsync(change.Key);
        }
    }

    /// <summary>
    /// The name a later rename of the same change gives back to the old name
    /// of rename <paramref name="index"/>, spelt as it gives it, when the two
    /// are a safe-save; null otherwise.  A safe-save's backup stays where it
    /// was put and its new copy comes from nowhere else in the change: a
    /// backup that moves on, or a copy that was itself renamed in, is an item
    /// moving - a swap, or a batch rename's shift or its two passes through
    /// temporary names - and the mark and the selection follow the item.
    /// So is a copy that was selected or marked before the change: an editor's
    /// copy is a new file nobody has picked yet, and one somebody had is an
    /// item of its own - a renumber, IMG_2 to IMG_3 and then IMG_1 to IMG_2.
    /// </summary>
    private string? NamedAgain(ReadOnlySpan<RenamePair> renames, int index, string folder)
    {
        var backup = renames[index].NewName;
        for (var other = 0; other < renames.Length; other++)
        {
            if (other != index && string.Equals(renames[other].OldName, backup, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        for (var later = index + 1; later < renames.Length; later++)
        {
            if (!string.Equals(renames[later].NewName, renames[index].OldName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var copy = renames[later].OldName;
            for (var other = 0; other < renames.Length; other++)
            {
                if (other != later && string.Equals(renames[other].NewName, copy, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            if (WasPicked(Path.Combine(folder, copy)))
            {
                return null;
            }

            return renames[later].NewName;
        }

        return null;
    }

    /// <summary>Whether <paramref name="path"/> is selected in any pane or carries a mark.</summary>
    private bool WasPicked(string path)
    {
        if (Selection.Contains(path) || !_marks.Get(path).IsEmpty)
        {
            return true;
        }

        foreach (var kept in _keptSelections)
        {
            if (kept.Contains(path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What <paramref name="path"/> is called once <paramref name="oldPath"/> is <paramref name="newPath"/>: null when it is not that or inside it.</summary>
    internal static string? Renamed(string path, string oldPath, string newPath) =>
        string.Equals(path, oldPath, StringComparison.OrdinalIgnoreCase)
            ? newPath
            : path.Length > oldPath.Length
                && path.StartsWith(oldPath, StringComparison.OrdinalIgnoreCase)
                && path[oldPath.Length] == Path.DirectorySeparatorChar
                    ? string.Concat(newPath, path.AsSpan(oldPath.Length))
                    : null;

    /// <summary>
    /// One selection's part in a rename: the item itself, and for a folder
    /// anything selected inside it, take the new name in one change.  Only a
    /// selection that could hold something of it is looked through: the item
    /// itself, something directly in it, or a selection small enough to look
    /// through for something deeper.
    /// </summary>
    private void FollowRenameIn(ItemSelection selection, string oldPath, string newPath)
    {
        if (selection.Count == 0 || !(selection.Contains(oldPath) || selection.CountIn(oldPath) > 0 || selection.Count <= 4096))
        {
            return;
        }

        List<string>? removed = null;
        List<SelectionItem>? added = null;
        foreach (var path in selection.Paths)
        {
            if (Renamed(path, oldPath, newPath) is { } moved && selection.TryGetItem(path, out var item))
            {
                (removed ??= []).Add(path);
                (added ??= []).Add(item with { Path = moved });
            }
        }

        if (removed is not null)
        {
            RenamesFollowed++;
            selection.Apply(new SelectionEdit
            {
                Added = added!,
                Removed = removed,
                Anchor = selection.Anchor is { } anchor ? Renamed(anchor, oldPath, newPath) ?? anchor : null,
                Focus = selection.Focus is { } focus ? Renamed(focus, oldPath, newPath) ?? focus : null,
                Source = SelectionSource.Command
            });
        }
    }

    /// <summary>
    /// <paramref name="oldPath"/> is <paramref name="newPath"/> now: every
    /// pane's selection - the item itself, and for a folder anything selected
    /// inside it - and the marks on them take the new name, in one change each.
    /// </summary>
    private void FollowRename(string oldPath, string newPath)
    {
        // Every pane's selection: the one being worked with, and each kept.
        FollowRenameIn(Selection, oldPath, newPath);
        foreach (var kept in _keptSelections)
        {
            FollowRenameIn(kept, oldPath, newPath);
        }

        BeginMarkBatch();
        try
        {
            _marks.Move(oldPath, newPath);
        }
        finally
        {
            EndMarkBatch();
        }

        RenameFollowed?.Invoke(oldPath, newPath);
    }

    /// <summary>Queues the app's known rename pair before an immediate reread can overtake the watcher.</summary>
    internal void NoteOwnRename(string oldPath, string newPath)
    {
        _changes?.TouchRename(oldPath, newPath);
        FollowRename(oldPath, newPath);
    }

    /// <summary>Renames the selection followed, for tests.</summary>
    internal int RenamesFollowed { get; private set; }

    // ---- the tree canvas's part ------------------------------------------------------

    /// <summary>
    /// Registers with the hub every folder the tree canvas holds children of -
    /// read, or brought in by name - and takes off the ones it no longer does,
    /// after every change of the graph.  One pass over the nodes, which the
    /// graph's change already makes to count them.
    /// </summary>
    private void SyncGraphInterest()
    {
        if (_changes is not { } hub || _isDisposed)
        {
            return;
        }

        var wanted = new Dictionary<string, ViewAllNodeViewModel>(Math.Max(_graphInterest.Count, 16), StringComparer.OrdinalIgnoreCase);
        foreach (var node in _graph.Nodes)
        {
            if (node.IsDirectory && (node.AreChildrenLoaded || node.Children.Count > 0))
            {
                wanted[node.FullPath] = node;
            }
        }

        foreach (var (path, node) in _graphInterest)
        {
            if (!wanted.TryGetValue(path, out var kept) || !ReferenceEquals(kept, node))
            {
                hub.Unregister(ChangeConsumer.Graph, path, node);
            }
        }

        foreach (var (path, node) in wanted)
        {
            if (!_graphInterest.TryGetValue(path, out var had) || !ReferenceEquals(had, node))
            {
                hub.Register(ChangeConsumer.Graph, path, node);
            }
        }

        _graphInterest = wanted;
    }

    /// <summary>Tree canvas folders registered with the hub right now, for tests.</summary>
    internal int GraphInterestCount => _graphInterest.Count;

    /// <summary>
    /// When the tree canvas last read a folder here and now - F5, a file
    /// operation - on the <see cref="System.Diagnostics.Stopwatch"/>'s clock:
    /// the change the hub brings a moment later for the same thing is already
    /// in what was read.
    /// </summary>
    private readonly Dictionary<string, long> _graphReadDirectly = new(StringComparer.OrdinalIgnoreCase);

    private void NoteGraphReadDirectly(string path)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_graphReadDirectly.Count > 64)
        {
            foreach (var (key, at) in _graphReadDirectly)
            {
                if (System.Diagnostics.Stopwatch.GetElapsedTime(at, now) > GraphEchoWindow)
                {
                    _graphReadDirectly.Remove(key);
                }
            }
        }

        _graphReadDirectly[path] = now;
        _graphStale.Remove(path);
    }

    /// <summary>How far back a change may have begun and still be taken as what a direct read already has.</summary>
    private static readonly TimeSpan GraphEchoWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A folder the tree canvas shows the children of changed on disk.  It is
    /// read again now if anybody can see the difference - the tree canvas is on
    /// screen, or something selected is in the folder, where a node gone or
    /// renamed would leave a command aiming at nothing - and otherwise marked,
    /// and read again when the tree comes back or the folder is opened.  A
    /// change that began before the folder was last read here and now is
    /// already in what was read.
    /// </summary>
    private void OnGraphFolderChanged(ViewAllNodeViewModel node, in FolderChange change)
    {
        if (!_graph.TryGetNode(node.FullPath, out var live) || !ReferenceEquals(live, node))
        {
            return;
        }

        if ((change.Kinds & ~ChangeKinds.DirDate) == 0 && Orders.SortOf(node.FullPath).Column != SortColumn.Modified)
        {
            return;
        }

        // A folder never read holds only what was brought in by name, and
        // reading it again is asking the disk about each of those - a log
        // growing beside four thousand files picked on the nested canvas was
        // four thousand questions a write.  Only something added, removed or
        // renamed can have taken one of them away.
        if (!node.AreChildrenLoaded && (change.Kinds & (ChangeKinds.Structural | ChangeKinds.Gone)) == 0)
        {
            return;
        }

        if (_graphReadDirectly.TryGetValue(node.FullPath, out var readAt)
            && change.FirstTicks <= readAt
            && System.Diagnostics.Stopwatch.GetElapsedTime(change.FirstTicks, readAt) <= GraphEchoWindow)
        {
            return;
        }

        MarkGraphChanged(node);
    }

    private void MarkGraphChanged(ViewAllNodeViewModel node)
    {
        if (_isCanvasShown || Selection.CountIn(node.FullPath) > 0)
        {
            _ = RefreshGraphNodeAsync(node);
        }
        else
        {
            _graphStale.Add(node.FullPath);
        }
    }

    /// <summary>
    /// Reads a tree canvas folder again for a change: the branch, keeping what
    /// was open below it, or - for a folder only holding children brought in
    /// by name - those checked against the disk.  Never inside the change's
    /// frame, and one at a time per folder: a change during the read has it
    /// read once more afterwards.
    /// </summary>
    private async Task RefreshGraphNodeAsync(ViewAllNodeViewModel node)
    {
        var path = node.FullPath;
        _graphStale.Remove(path);
        if (!_graphRefreshing.Add(path))
        {
            _graphChangedAgain.Add(path);
            return;
        }

        try
        {
            do
            {
                _graphChangedAgain.Remove(path);
                await Task.Yield();
                if (_isDisposed || !_graph.TryGetNode(path, out var live))
                {
                    return;
                }

                GraphRefreshesForChanges++;
                if (live.AreChildrenLoaded)
                {
                    await _graph.RefreshBranchAsync(live);
                    ScheduleSave();
                }
                else if (await _graph.PruneMissingChildrenAsync(live) > 0)
                {
                    ScheduleSave();
                }

                ReleaseRemovedFocus(live);
            }
            while (_graphChangedAgain.Contains(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException)
        {
            // Gone, or the window is closing: its parent's change takes it off the tree.
        }
        finally
        {
            _graphRefreshing.Remove(path);
        }
    }

    /// <summary>Tree canvas folders read again for changes on disk, for tests.</summary>
    internal int GraphRefreshesForChanges { get; private set; }

    /// <summary>Tree canvas folders that changed while nothing needed them, for tests.</summary>
    internal int GraphStaleCount => _graphStale.Count;

    /// <summary>The folders that changed while the tree canvas was away, read again now that it is back.</summary>
    private void RefreshStaleGraph()
    {
        if (_graphStale.Count == 0 || _isDisposed)
        {
            return;
        }

        foreach (var path in _graphStale.ToArray())
        {
            if (_graph.TryGetNode(path, out var node))
            {
                _ = RefreshGraphNodeAsync(node);
            }
        }

        _graphStale.Clear();
    }

    /// <summary>A folder about to be opened that changed while it was not wanted current: read again, which opens it; false when it had not changed.</summary>
    private async Task<bool> RefreshIfStaleAsync(ViewAllNodeViewModel node)
    {
        if (!node.AreChildrenLoaded || !_graphStale.Remove(node.FullPath))
        {
            return false;
        }

        await _graph.RefreshBranchAsync(node);
        return true;
    }

    /// <summary>
    /// Works out where the canvas is by asking what is nearest the middle of it
    /// and reading that node's ancestors.  A small probe first, because the grid
    /// answers a small query in constant time and the middle of the canvas
    /// almost always has something in it; the whole viewport only when it does
    /// not, which happens in the gap between two trees.
    /// </summary>
    private void UpdateTrail(Rect viewport)
    {
        var centre = new Point(
            viewport.X + viewport.Width / 2,
            viewport.Y + viewport.Height / 2);

        var reach = Math.Min(
            Math.Max(viewport.Width, viewport.Height) / 2,
            ViewAllSpatialIndex.CellExtent * 1.5);
        var probe = new Rect(centre.X - reach, centre.Y - reach, reach * 2, reach * 2);

        _trailProbe.Clear();
        _graph.Index.Query(probe, _trailProbe);
        if (_trailProbe.Count == 0)
        {
            _graph.Index.Query(viewport, _trailProbe);
        }

        ViewAllNodeViewModel? nearest = null;
        var best = double.MaxValue;
        foreach (var node in _trailProbe)
        {
            if (!node.IsTreeVisible)
            {
                continue;
            }

            var middle = new Point(
                node.Location.X + node.Width / 2,
                node.Location.Y + node.Height / 2);
            var distance = (middle - centre).LengthSquared;
            if (distance < best)
            {
                best = distance;
                nearest = node;
            }
        }

        if (nearest?.Parent is null)
        {
            if (_trail.Count > 0)
            {
                Trail = [];
            }

            return;
        }

        var chain = new List<ViewAllNodeViewModel>();
        for (var walk = nearest.Parent; walk is not null; walk = walk.Parent)
        {
            chain.Add(walk);
        }

        chain.Reverse();

        // Rebuilding the list on every pan would repaint the overlay constantly;
        // the chain only changes when the canvas crosses into another folder.
        if (chain.Count == _trail.Count
            && chain.Select((step, index) => step.DisplayName == _trail[index].Name).All(same => same))
        {
            return;
        }

        Trail = chain
            .Select((step, index) => new ViewAllTrailStep(
                step.DisplayName,
                step.BranchBrush,
                index == 0 ? string.Empty : "  \u203A  "))
            .ToArray();
    }

    private void OnGraphChanged(object? sender, EventArgs e)
    {
        // Counting once per structural change keeps it off the pan path.
        var visible = 0;
        foreach (var node in _graph.Nodes)
        {
            if (node.IsTreeVisible)
            {
                visible++;
            }
        }

        _visibleNodeCount = visible;
        _isOverviewStale = true;
        SyncGraphInterest();

        if (!_isCanvasShown)
        {
            // Nothing below runs for a canvas that is not showing, and the count
            // normally reaches the status bar through the render set.
            _graphChangedWhileHidden = true;
            LogicalNodeCount = visible;
            return;
        }

        RebuildRenderSet();
    }

    /// <summary>
    /// A drag moves nodes without changing the tree, so the cached drawing has
    /// to be rebuilt on movement too.  The throttle is not restarted on every
    /// move: during a drag that would postpone the redraw until the user let go.
    /// </summary>
    private void OnLayoutChanged()
    {
        _isOverviewStale = true;
        if (_isCanvasShown && !_renderThrottle.IsEnabled)
        {
            _renderThrottle.Start();
        }
    }

    /// <summary>
    /// Tells the tree canvas's batched layers that what they drew is out of date
    /// - or, while it is not showing, remembers to tell them when it is.
    /// </summary>
    private void InvalidateCanvas()
    {
        if (!_isCanvasShown)
        {
            _isOverviewStale = true;
            return;
        }

        GraphInvalidated?.Invoke();
    }

    private void OnNodeCreated(ViewAllNodeViewModel node)
    {
        var mark = _marks.Get(node.FullPath);
        node.AccentHex = mark.AccentHex;
        node.Note = mark.Note;

        // Cached only. Resolving through the Shell here would add milliseconds
        // per distinct executable to every expansion, on the UI thread.
        node.Icon = _icons.GetCached(node.FullPath, node.IsDirectory);
    }

    /// <summary>
    /// Icons are fetched for what is actually on screen, and only once the nodes
    /// are large enough for an icon to be visible at all.
    /// </summary>
    private void RequestIcons(IReadOnlyList<ViewAllNodeViewModel> nodes, ViewAllDetailLevel detail)
    {
        if (detail < ViewAllDetailLevel.Compact)
        {
            return;
        }

        foreach (var node in nodes)
        {
            if (node.Icon is not null)
            {
                continue;
            }

            var target = node;
            _icons.Request(target.FullPath, target.IsDirectory, icon =>
            {
                if (!_isDisposed && icon is not null)
                {
                    target.Icon = icon;
                }
            });
        }
    }

    private void OnMarkChanged(string path, FolderMark mark)
    {
        if (!_graph.TryGetNode(path, out var node))
        {
            return;
        }

        node.AccentHex = mark.AccentHex;
        node.Note = mark.Note;

        // Zoomed out the canvas is one cached geometry per colour, so a recolour
        // is not visible until it is rebuilt - once for a whole batch of marks.
        if (_markBatches > 0)
        {
            _markBatchRecoloured = true;
            return;
        }

        InvalidateCanvas();
    }

    /// <summary>
    /// The editor's copy of the selection changed.  Changed from here - the
    /// mirror being brought up to date - there is nothing to do; changed by
    /// Nodify itself - a click, Ctrl+click, a rubber band adding nodes one at
    /// a time - the whole of it becomes one change of <see cref="Selection"/>,
    /// once the band has finished adding, rather than one per node.
    /// </summary>
    private void OnSelectedNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isSyncingSelection)
        {
            return;
        }

        foreach (var removed in e.OldItems?.OfType<ViewAllNodeViewModel>() ?? [])
        {
            removed.IsSelected = false;
        }

        foreach (var added in e.NewItems?.OfType<ViewAllNodeViewModel>() ?? [])
        {
            added.IsSelected = true;
        }

        if (_treeSelectionPending)
        {
            return;
        }

        _treeSelectionPending = true;
        Dispatcher.CurrentDispatcher.InvokeAsync(TakeTreeSelection, DispatcherPriority.Background);
    }

    /// <summary>What Nodify selected, as one replacement of the shared selection.</summary>
    private void TakeTreeSelection()
    {
        _treeSelectionPending = false;
        if (_isDisposed)
        {
            return;
        }

        var nodes = SelectedNodes.ToArray();
        var focus = nodes.Length > 0 ? nodes[^1].FullPath : null;
        Selection.Apply(new SelectionEdit
        {
            Clear = true,
            Added = [.. nodes.Select(node => new SelectionItem(node.FullPath, node.IsDirectory, node.Entry.SizeBytes ?? 0))],
            Anchor = focus,
            Focus = focus,
            RecordsNavigation = nodes.Length == 1,
            Source = SelectionSource.Tree
        });
    }

    /// <summary>
    /// The selection changed, once for the whole of a gesture.  A click
    /// still reading its way down is outranked; the focus gets its node -
    /// at once when the graph has one, otherwise by a light reveal that the
    /// next change outranks in turn; the status bar and the list follow; the
    /// editor's copy is brought up to date.  What is watched on disk does not
    /// depend on any of it.  Nothing here
    /// costs more than the change itself, however much is selected.
    /// </summary>
    private void OnSelectionChanged(ItemSelection selection)
    {
        var ticket = ++_selectTicket;
        var records = selection.LastRecordsNavigation;
        _pendingFocus = null;
        if (selection.Focus is { } focus && !(_activeNode is { } current && string.Equals(current.FullPath, focus, StringComparison.OrdinalIgnoreCase)))
        {
            if (TryGetNode(focus, out var node))
            {
                SetActive(node, records);
            }
            else
            {
                // Before the look, which may find the node at once.
                _pendingFocus = focus;
                _ = FocusAsync(focus, ticket, records, holdList: selection.LastSource == SelectionSource.List);
            }
        }
        else
        {
            RetargetFolderList();
        }

        UpdateStatus();
        SyncSelectionMirror();
        FolderList.OnSelectionChanged(selection);
        ScheduleSave();
    }

    /// <summary>
    /// Gives the focus its node, without selecting anything or moving a
    /// canvas.  While the list is what picked it, the list keeps its folder
    /// meanwhile, as a click on a row always has.  A path found gone is let
    /// go of, its folder read again, and said to be gone - once, and only
    /// when the disk says so: a share that did not answer for a moment has
    /// lost nothing.  The focus goes to the folder with it, as when a
    /// refresh finds the focus gone (see <see cref="ReleaseRemovedFocus"/>):
    /// left on the gone path, letting go of it was a change of the selection
    /// that asked for the path all over again, and said so twice.
    /// </summary>
    private async Task FocusAsync(string path, int ticket, bool records, bool holdList)
    {
        using var hold = holdList ? FolderList.HoldFolder() : null;
        ViewAllNodeViewModel? node;
        string? gone = null;
        try
        {
            node = await MaterializeAsync(path, message => gone = message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return;
        }

        if (ticket != _selectTicket || _isDisposed)
        {
            return;
        }

        if (node is null)
        {
            // Asked off this thread, and gone only when the disk says it is
            // not there: a share that has stopped answering would hold the
            // window up for as long as the network takes to say so, and what
            // is selected in it is not to be let go of meanwhile.  Inside an
            // archive nothing is on disk, and the tree has no node for it:
            // it is still there, and stays selected.
            if (await IsGoneAsync(path) && !Services.Archives.ArchiveService.IsInsideArchive(path) && ticket == _selectTicket && !_isDisposed)
            {
                var parent = Path.GetDirectoryName(path);
                var focused = ViewAllPath.Equals(Selection.Focus ?? string.Empty, path);
                Selection.Apply(new SelectionEdit
                {
                    Removed = [path],
                    Focus = focused && !string.IsNullOrEmpty(parent) ? parent : null,
                    Source = SelectionSource.Command
                });
                if (gone is not null)
                {
                    MessageRequested?.Invoke(gone, true);
                }

                if (!string.IsNullOrEmpty(parent))
                {
                    await RefreshPathAsync(parent);
                }
            }

            return;
        }

        SetActive(node, records);
        SyncSelectionMirror();
    }

    /// <summary>
    /// Whether <paramref name="path"/> is certainly no longer on disk, asked
    /// on the thread pool.  Only "not found" says so: a share that does not
    /// answer, or an item that may not be looked at, is still there as far
    /// as anyone can tell - where Exists would answer false for both.
    /// </summary>
    private static Task<bool> IsGoneAsync(string path) => Task.Run(() =>
    {
        try
        {
            _ = File.GetAttributes(ViewAllFileSystemService.ForWindows(path));
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    });

    /// <summary>The focus moves to <paramref name="node"/>, as a navigation or not.</summary>
    private void SetActive(ViewAllNodeViewModel node, bool records)
    {
        _focusRecordsNavigation = records;
        try
        {
            ActiveNode = node;
        }
        finally
        {
            _focusRecordsNavigation = true;
        }
    }

    /// <summary>
    /// Points the list at what is selected: the folder several selected
    /// items share, with them lit; otherwise the focus, as it always was - a
    /// folder is gone into, a file's folder shown with its row lit.
    /// </summary>
    private void RetargetFolderList()
    {
        if (Selection.Count > 1 && Selection.Container is { } container)
        {
            FolderList.SetTarget(container, _activeNode);
            return;
        }

        FolderList.SetTarget(_activeNode);
    }

    /// <summary>
    /// Brings the editor's copy of the selection up to date, one node at a
    /// time and only what changed: every selected path the graph has a node
    /// for while the tree canvas is on screen, only the focus while it is not.
    /// </summary>
    private void SyncSelectionMirror()
    {
        var wanted = new HashSet<ViewAllNodeViewModel>();
        if (_isCanvasShown)
        {
            foreach (var path in Selection.Paths)
            {
                if (TryGetNode(path, out var node))
                {
                    wanted.Add(node);
                }
            }
        }
        else if (Selection.Focus is { } focus && Selection.Contains(focus) && TryGetNode(focus, out var focused))
        {
            wanted.Add(focused);
        }

        if (wanted.Count == SelectedNodes.Count && SelectedNodes.All(wanted.Contains))
        {
            return;
        }

        _isSyncingSelection = true;
        try
        {
            var present = new HashSet<ViewAllNodeViewModel>();
            for (var index = SelectedNodes.Count - 1; index >= 0; index--)
            {
                var node = SelectedNodes[index];
                if (!wanted.Contains(node) || !present.Add(node))
                {
                    SelectedNodes.RemoveAt(index);
                    node.IsSelected = wanted.Contains(node);
                }
            }

            foreach (var node in wanted)
            {
                node.IsSelected = true;
                if (present.Add(node))
                {
                    SelectedNodes.Add(node);
                }
            }
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    /// <summary>
    /// While a rectangle is drawn on the nested canvas, the status bar says
    /// how many items it would select; -1 when it is let go, and the status
    /// is the selection's again.
    /// </summary>
    public void ShowSelectionPreview(int count)
    {
        if (_marqueePreview == count)
        {
            return;
        }

        _marqueePreview = count;
        UpdateStatus();
    }

    private void RebuildRenderSet()
    {
        if (_isDisposed)
        {
            return;
        }

        // Everything below feeds the tree canvas and nothing else: the editor's
        // containers, their icons, the trail and the batched layers.  While the
        // tree is not on screen it is skipped outright, and done once when it
        // comes back (see IsCanvasShown).
        if (!_isCanvasShown)
        {
            _fillTimer.Stop();
            return;
        }

        using var frame = PerfLog.Measure("renderset");
        var viewport = new Rect(_viewportLocation, _viewportSize);
        var set = _viewportService.BuildRenderSet(
            _graph.Index,
            _graph.Edges,
            AlwaysRealized(viewport),
            viewport,
            _viewportZoom,
            _visibleNodeCount,
            _graph.IncomingEdges);
        PerfLog.Value("renderset.nodes", set.Nodes.Count);
        int pending;
        using (PerfLog.Measure("renderset.sync"))
        {
            var budget = FillBudget;
            pending = Sync(RenderNodes, set.Nodes, ref budget);
            pending += Sync(RenderEdges, set.Edges, ref budget);
        }

        PerfLog.Value("renderset.pending", pending);

        if (pending > 0)
        {
            _fillTimer.Start();
        }
        else
        {
            _fillTimer.Stop();
        }

        DetailLevel = set.DetailLevel;
        LogicalNodeCount = set.LogicalNodeCount;
        // The batched picture may STAY up while the containers it is standing in
        // for arrive, but it must never come up because of them.  Coming out of
        // the batched view is the case worth covering - a screenful at once, and
        // the slabs are already on screen so nothing changes visually.  Panning
        // also brings in a hundred nodes at a time, and covering the canvas with
        // slabs for those was a flash of colour on every scroll: the cure looking
        // worse than the thing it cured.
        IsOverviewActive = ViewAllViewportService.UsesOverview(set.DetailLevel)
            || (IsOverviewActive && pending > 0);
        using (PerfLog.Measure("renderset.icons"))
        {
            RequestIcons(set.Nodes, set.DetailLevel);
        }

        using (PerfLog.Measure("renderset.trail"))
        {
            UpdateTrail(viewport);
        }


        // Raised at every zoom, not only while the batched slab layer is on
        // screen: the links are drawn by a layer of their own that is always
        // visible, and it has no other way to learn that the tree moved.
        if (_isOverviewStale)
        {
            _isOverviewStale = false;
            GraphInvalidated?.Invoke();
        }
    }

    /// <summary>
    /// The nodes the editor keeps whatever the culling says: the selected
    /// ones in view - a rubber band over thousands of nodes must not force a
    /// container for each one off screen - and the focus.  Only the graph's
    /// own: a node its folder let go of when it was read again would stay on
    /// the canvas as a ghost where the item used to be.
    /// </summary>
    private List<ViewAllNodeViewModel> AlwaysRealized(Rect viewport)
    {
        var realized = new List<ViewAllNodeViewModel>();
        foreach (var node in SelectedNodes)
        {
            if (node.IsTreeVisible && node.HasLayoutPosition && viewport.IntersectsWith(node.Bounds) && IsLive(node))
            {
                realized.Add(node);
            }
        }

        if (_activeNode is { IsTreeVisible: true } active && IsLive(active) && !realized.Contains(active))
        {
            realized.Add(active);
        }

        return realized;
    }

    /// <summary>
    /// Brings <paramref name="target"/> towards <paramref name="desired"/>, adding
    /// at most <paramref name="budget"/> items and returning how many are still
    /// waiting.
    ///
    /// Removals are not budgeted - tearing a container down is cheap.  Adding one
    /// is not: the editor inflates a whole node control per item, about a
    /// millisecond and a half each, and the canvas crossing out of the batched
    /// view wanted a hundred of them in a single frame.  That was a third of a
    /// second of nothing moving, measured, every time the zoom came back in.
    /// </summary>
    private static int Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> desired, ref int budget)
        where T : class
    {
        var wanted = new HashSet<T>(desired);
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!wanted.Contains(target[index]))
            {
                target.RemoveAt(index);
            }
        }

        var present = new HashSet<T>(target);
        var pending = 0;
        foreach (var item in desired)
        {
            if (!present.Add(item))
            {
                continue;
            }

            if (budget <= 0)
            {
                pending++;
                continue;
            }

            target.Add(item);
            budget--;
        }

        return pending;
    }

    /// <summary>
    /// The status bar, from the selection's running totals: nothing here
    /// walks the selection, so it costs the same for one item as for ten
    /// thousand.  While a rectangle is being drawn, what it would select.
    /// </summary>
    private void UpdateStatus()
    {
        if (_marqueePreview >= 0)
        {
            StatusCountText = _marqueePreview == 1 ? "1 item selected" : $"{_marqueePreview:N0} items selected";
            StatusPathText = ActiveNode?.FullPath ?? string.Empty;
            return;
        }

        var selected = Selection.Count;
        if (selected > 1)
        {
            var folders = Selection.FolderCount;
            var bytes = Selection.TotalBytes;
            StatusCountText = folders > 0
                ? $"{selected:N0} items selected  ·  {folders:N0} folders  ·  {FileSystemService.FormatSize(bytes)}"
                : $"{selected:N0} items selected  ·  {FileSystemService.FormatSize(bytes)}";
            StatusPathText = ActiveNode?.FullPath ?? string.Empty;
            return;
        }

        var node = selected == 1 && TryGetNode(Selection.Paths[0], out var only) ? only : ActiveNode;
        if (selected == 1 && node is not null && !ViewAllPath.Equals(node.FullPath, Selection.Paths[0])
            && Selection.TryGetItem(Selection.Paths[0], out var item))
        {
            // The one item selected has no node - picked on the nested canvas -
            // and the focus is on another, one a Ctrl+click let go of: the bar
            // names the item Delete and F2 act on.
            StatusCountText = item.IsDirectory ? "1 item selected" : $"1 item selected  ·  {FileSystemService.FormatSize(item.Size)}";
            StatusPathText = item.Path;
            return;
        }

        if (node is null)
        {
            StatusCountText = $"{LogicalNodeCount:N0} nodes";
            StatusPathText = string.Empty;
            return;
        }

        StatusCountText = node.IsDirectory
            ? node.AreChildrenLoaded
                ? $"{node.LoadedChildCount:N0} items{(node.IsTruncated ? "+" : string.Empty)}"
                : "Folder"
            : node.SecondaryText;
        StatusPathText = node.FullPath;
    }
}

/// <summary>
/// How a <see cref="ViewAllViewModel.RevealAsync"/> ended: the node it got
/// to (the deepest folder still there when the path is gone), whether that is
/// the path itself, and whether a newer navigation took over meanwhile, or
/// the pane it was for stopped being the one worked with (see
/// <see cref="ViewAllViewModel.NavigationLandedAway"/>) - in which case
/// nothing was selected or flown to here.
/// </summary>
public readonly record struct RevealOutcome(ViewAllNodeViewModel? Node, bool IsExact, bool Superseded);

/// <summary>
/// Where a navigation ended that was started for a selection no longer the
/// window's (see <see cref="ViewAllViewModel.NavigationLandedAway"/>): the
/// pane it was started in - the <see cref="ViewAllViewModel.SelectionHolder"/>
/// of the time - the node it got to, whether it was to select it and fly
/// there, and whether going there is a step for Back and Forward.
/// </summary>
public readonly record struct NavigationLanding(object? Holder, ViewAllNodeViewModel Node, bool Select, bool Focus, bool Records);
