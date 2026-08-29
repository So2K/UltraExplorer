using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace UltraExplorer.ViewModels;

/// <summary>
/// The View All canvas: every drive is an independent root and one graph node
/// is exactly one file-system object.  Expansion is lazy, layout is incremental
/// and only the nodes inside the viewport are handed to the editor.
/// </summary>
public sealed class ViewAllViewModel : ObservableObject, IDisposable
{
    private readonly ViewAllGraphService _graph;
    private readonly ViewAllViewportService _viewportService = new();
    private readonly ViewAllWorkspaceStore _store;
    private readonly FolderMarkService _marks;
    private readonly ShellIconService _icons;
    private readonly DispatcherTimer _renderThrottle;
    private readonly DispatcherTimer _saveDebounce;
    private readonly DispatcherTimer _watcherDebounce;
    private FileSystemWatcher? _activeWatcher;
    private string _watchedPath = string.Empty;

    private bool _isInitialized;
    private bool _isDisposed;
    private bool _isBusy;
    private bool _isSyncingSelection;
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
        _marks.MarkChanged += OnMarkChanged;

        SelectedNodes.CollectionChanged += OnSelectedNodesChanged;

        FolderList = new FolderListViewModel(
            (path, cancellation) => _graph.ReadDirectoryAsync(path, cancellation),
            ActivateListItemAsync,
            path => _graph.TryGetNode(path, out _),
            icons);

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

        _watcherDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _watcherDebounce.Tick += async (_, _) =>
        {
            _watcherDebounce.Stop();
            if (!string.IsNullOrEmpty(_watchedPath))
            {
                await RefreshPathAsync(_watchedPath);
            }
        };

        ToggleSelectedCommand = new AsyncRelayCommand(ToggleSelectedAsync);
        CollapseAllCommand = new RelayCommand(CollapseAll);
        RefreshSelectedCommand = new AsyncRelayCommand(RefreshSelectedAsync);
        LoadMoreCommand = new AsyncRelayCommand(LoadMoreForSelectionAsync);
    }

    /// <summary>Nodes and edges the editor should actually realize right now.</summary>
    public ObservableCollection<ViewAllNodeViewModel> RenderNodes { get; } = [];

    public ObservableCollection<ViewAllEdgeViewModel> RenderEdges { get; } = [];

    public ObservableCollection<ViewAllNodeViewModel> SelectedNodes { get; } = [];

    public ICommand ToggleSelectedCommand { get; }
    public ICommand CollapseAllCommand { get; }
    public ICommand RefreshSelectedCommand { get; }
    public ICommand LoadMoreCommand { get; }

    /// <summary>Raised when the canvas should fly to a node.</summary>
    public event Action<ViewAllNodeViewModel, bool>? FocusNodeRequested;

    /// <summary>Raised when a message belongs on the shell toast.</summary>
    public event Action<string, bool>? MessageRequested;

    /// <summary>Raised when the graph structure changed and cached drawing is stale.</summary>
    public event Action? GraphInvalidated;

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
        ViewAllNodeViewModel? node;

        // Held while the canvas is being driven from a row: selecting a folder on
        // the canvas would otherwise take the list into it, which would make a
        // single click open the folder.  A single click selects; it does not open.
        using (FolderList.HoldFolder())
        {
            node = await RevealPathAsync(path);
            if (node is not null)
            {
                SelectOnly(node);
            }
        }

        if (node is null)
        {
            if (open)
            {
                MessageRequested?.Invoke($"{Path.GetFileName(path)} could not be shown", true);
            }

            return;
        }

        if (!open)
        {
            return;
        }

        if (node.IsDirectory)
        {
            await ExpandAsync(node);
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
            if (SetProperty(ref _activeNode, value))
            {
                OnPropertyChanged(nameof(ActivePath));
                FolderList.SetTarget(_activeNode);
                UpdateStatus();
                AttachWatcher(value);
            }
        }
    }

    public string ActivePath => _activeNode?.FullPath ?? string.Empty;

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
            await _graph.InitializeAsync(state);

            if (state is { Nodes.Count: > 0 })
            {
                RestoredViewportLocation = new Point(state.ViewportX, state.ViewportY);
                RestoredViewportZoom = Math.Clamp(state.ViewportZoom, 0.05, 4);
                HasRestoredViewport = true;

                if (!string.IsNullOrWhiteSpace(state.ActivePath) && _graph.TryGetNode(state.ActivePath, out var active))
                {
                    SelectOnly(active);
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

            RebuildRenderSet();
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

    public bool TryGetNode(string path, out ViewAllNodeViewModel node)
        => _graph.TryGetNode(path, out node);

    /// <summary>Adds a non-drive location (WSL, UNC share) as its own root.</summary>
    public async Task<ViewAllNodeViewModel?> AddRootAsync(string path)
    {
        var node = await _graph.AddRootAsync(path);
        if (node is null)
        {
            MessageRequested?.Invoke($"Could not open {path}", true);
            return null;
        }

        await _graph.ExpandAsync(node);
        SelectOnly(node);
        RebuildRenderSet();
        FocusNodeRequested?.Invoke(node, true);
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

        await _graph.ExpandAsync(node);
        ScheduleSave();
        return node;
    }

    /// <summary>Explorer's View / Hidden items toggle.</summary>
    public bool ShowHiddenItems
    {
        get => _graph.Options.IncludeHidden;
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
            OnPropertyChanged(nameof(ShowHiddenItems));
        }
        finally
        {
            IsBusy = false;
        }
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
    ///
    /// The selection is cleared afterwards, and not as a courtesy: a selected
    /// node is realized whatever the culling says, so a folder hidden while it
    /// is still selected would stay on screen.
    /// </summary>
    public void HideSelected()
    {
        var targets = SelectedNodes.Where(node => node.IsDirectory).ToArray();
        if (targets.Length == 0)
        {
            MessageRequested?.Invoke("Select a folder to hide.", false);
            return;
        }

        SelectedNodes.Clear();
        foreach (var node in targets)
        {
            _graph.Hide(node);
        }

        ActiveNode = targets[0].Parent ?? ActiveNode;
        OnPropertyChanged(nameof(HiddenPaths));
        OnPropertyChanged(nameof(HiddenCount));
        MessageRequested?.Invoke(
            targets.Length == 1
                ? $"{targets[0].DisplayName} hidden — bring it back from the canvas menu"
                : $"{targets.Length} folders hidden — bring them back from the canvas menu",
            false);
        RebuildRenderSet();
        GraphInvalidated?.Invoke();
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
        GraphInvalidated?.Invoke();
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
        GraphInvalidated?.Invoke();
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
        GraphInvalidated?.Invoke();
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
        GraphInvalidated?.Invoke();
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

    /// <summary>Throws away every hand-placed position and rebuilds the tree.</summary>
    public void RelayoutCanvas()
    {
        _graph.Relayout();
        RebuildRenderSet();
        GraphInvalidated?.Invoke();
        MessageRequested?.Invoke("Canvas tidied", false);
        ScheduleSave();
    }

    public void CollapseAll()
    {
        _graph.CollapseAll();
        ScheduleSave();
    }

    public async Task RefreshAsync(ViewAllNodeViewModel node)
    {
        if (!node.IsDirectory)
        {
            return;
        }

        await _graph.RefreshBranchAsync(node);
        ScheduleSave();
    }

    /// <summary>Re-reads a directory if it is currently part of the graph.</summary>
    public async Task RefreshPathAsync(string path)
    {
        if (_graph.TryGetNode(path, out var node) && node.AreChildrenLoaded)
        {
            await _graph.RefreshBranchAsync(node);
            ScheduleSave();
        }

        // The list reads the directory itself, so a change the canvas has just
        // picked up means nothing to it until it is told.  Creating a file and
        // watching it appear on the canvas but not in the list was the giveaway.
        if (string.Equals(path, FolderList.FolderPath, StringComparison.OrdinalIgnoreCase))
        {
            await FolderList.ReloadAsync();
        }
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
    /// </summary>
    public async Task<ViewAllNodeViewModel?> RevealPathAsync(string path, bool focus = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

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
            return null;
        }

        // A UNC share or a WSL distribution is not a DriveInfo root, so it only
        // enters the graph when it is explicitly added.
        if (chain.Count > 0 && !_graph.TryGetNode(chain[0], out _))
        {
            await _graph.AddRootAsync(chain[0]);
        }

        ViewAllNodeViewModel? node = null;
        foreach (var step in chain)
        {
            if (!_graph.TryGetNode(step, out var found))
            {
                // The step may simply be filtered out of its parent - hidden,
                // or not of the file type on offer.  Asking for it by name
                // outranks that.
                var adopted = node is null ? null : await _graph.AdoptChildAsync(node, step);
                if (adopted is null)
                {
                    MessageRequested?.Invoke(
                        node is null
                            ? "That drive is not available on this machine."
                            : $"{Path.GetFileName(step)} is no longer inside {node.DisplayName}.",
                        true);
                    break;
                }

                found = adopted;
            }

            node = found;
            var isDestination = string.Equals(step, normalized, StringComparison.OrdinalIgnoreCase);
            if (!isDestination && node.IsDirectory)
            {
                await _graph.ExpandAsync(node);
            }
        }

        if (node is null)
        {
            return null;
        }

        SelectOnly(node);
        RebuildRenderSet();
        if (focus)
        {
            FocusNodeRequested?.Invoke(node, true);
        }

        ScheduleSave();
        return node;
    }

    public void SelectOnly(ViewAllNodeViewModel node)
    {
        _isSyncingSelection = true;
        try
        {
            foreach (var selected in SelectedNodes.Where(item => item != node).ToArray())
            {
                selected.IsSelected = false;
            }

            SelectedNodes.Clear();
            SelectedNodes.Add(node);
            node.IsSelected = true;
        }
        finally
        {
            _isSyncingSelection = false;
        }

        ActiveNode = node;
    }

    public void Activate(ViewAllNodeViewModel node)
    {
        ActiveNode = node;
        UpdateStatus();
    }

    public void OpenInDefaultApplication(ViewAllNodeViewModel node)
    {
        try
        {
            NativeShellService.Open(node.FullPath);
        }
        catch (Exception ex)
        {
            MessageRequested?.Invoke($"Could not open {node.DisplayName}: {ex.Message}", true);
        }
    }

    /// <summary>
    /// Strictly what the user selected.  Destructive commands use this, so an
    /// empty canvas selection can never be turned into "delete the folder I am
    /// merely looking at".
    /// </summary>
    public IReadOnlyList<string> SelectedPaths
        => SelectedNodes.Count > 0
            ? SelectedNodes.Select(node => node.FullPath).ToArray()
            : [];

    /// <summary>Selection, falling back to the focused node for read-only commands.</summary>
    public IReadOnlyList<string> SelectedOrActivePaths
        => SelectedPaths.Count > 0
            ? SelectedPaths
            : ActiveNode is null ? [] : [ActiveNode.FullPath];

    /// <summary>The folder that a new item or a paste should land in.</summary>
    public string? TargetDirectory
    {
        get
        {
            var node = SelectedNodes.LastOrDefault() ?? ActiveNode;
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

    public void ApplyAccent(IEnumerable<ViewAllNodeViewModel> nodes, string? accentHex)
    {
        foreach (var node in nodes)
        {
            _marks.SetAccent(node.FullPath, accentHex);
        }

        ScheduleSave();
    }

    public void ApplyNote(ViewAllNodeViewModel node, string? note)
    {
        _marks.SetNote(node.FullPath, note);
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
        // continuous pan and leave the culling on a stale viewport.
        if (!_renderThrottle.IsEnabled)
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

    public async Task SaveAsync()
    {
        if (!_isInitialized || _isDisposed)
        {
            return;
        }

        try
        {
            var state = _graph.CaptureState(new ViewAllViewportState(_viewportLocation, _viewportZoom));
            state.ActivePath = ActiveNode?.FullPath ?? string.Empty;
            await _store.SaveAsync(state);
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
        _watcherDebounce.Stop();
        _activeWatcher?.Dispose();
        _activeWatcher = null;
        _graph.GraphChanged -= OnGraphChanged;
        _graph.NodeCreated -= OnNodeCreated;
        _fillTimer.Stop();
        _graph.LayoutChanged -= OnLayoutChanged;
        _marks.MarkChanged -= OnMarkChanged;
        SelectedNodes.CollectionChanged -= OnSelectedNodesChanged;
        _graph.Dispose();
    }

    private async Task ToggleSelectedAsync()
    {
        var target = SelectedNodes.LastOrDefault() ?? ActiveNode;
        if (target is not null)
        {
            await ToggleAsync(target);
        }
    }

    private async Task RefreshSelectedAsync()
    {
        var target = SelectedNodes.LastOrDefault() ?? ActiveNode;
        if (target is not null)
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

    /// <summary>
    /// Only the branch the user is looking at is watched.  One watcher keeps the
    /// open folder current without the cost of watching a whole drive.
    /// </summary>
    private void AttachWatcher(ViewAllNodeViewModel? node)
    {
        var path = node is { IsDirectory: true, AreChildrenLoaded: true } ? node.FullPath : string.Empty;
        if (string.Equals(path, _watchedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _watcherDebounce.Stop();
        _activeWatcher?.Dispose();
        _activeWatcher = null;
        _watchedPath = path;

        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            _activeWatcher = FileSystemService.CreateWatcher(path, OnWatchedPathChanged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _watchedPath = string.Empty;
        }
    }

    private void OnWatchedPathChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || _isDisposed)
        {
            return;
        }

        _ = dispatcher.InvokeAsync(() =>
        {
            if (_isDisposed)
            {
                return;
            }

            _watcherDebounce.Stop();
            _watcherDebounce.Start();
        }, DispatcherPriority.Background);
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
        if (!_renderThrottle.IsEnabled)
        {
            _renderThrottle.Start();
        }
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
        // is not visible until it is rebuilt.
        GraphInvalidated?.Invoke();
    }

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

        if (SelectedNodes.LastOrDefault() is { } last)
        {
            ActiveNode = last;
        }

        UpdateStatus();
    }

    private void RebuildRenderSet()
    {
        if (_isDisposed)
        {
            return;
        }

        using var frame = PerfLog.Measure("renderset");
        var viewport = new Rect(_viewportLocation, _viewportSize);
        var set = _viewportService.BuildRenderSet(
            _graph.Index,
            _graph.Edges,
            SelectedNodes,
            viewport,
            _viewportZoom,
            _visibleNodeCount);
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

    private void UpdateStatus()
    {
        var selected = SelectedNodes.Count;
        if (selected > 1)
        {
            var folders = SelectedNodes.Count(node => node.IsDirectory);
            var bytes = SelectedNodes.Where(node => node.IsFile && node.Entry.SizeBytes.HasValue)
                .Sum(node => node.Entry.SizeBytes ?? 0);
            StatusCountText = folders > 0
                ? $"{selected:N0} items selected  ·  {folders:N0} folders  ·  {FileSystemService.FormatSize(bytes)}"
                : $"{selected:N0} items selected  ·  {FileSystemService.FormatSize(bytes)}";
            StatusPathText = ActiveNode?.FullPath ?? string.Empty;
            return;
        }

        var node = SelectedNodes.FirstOrDefault() ?? ActiveNode;
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
