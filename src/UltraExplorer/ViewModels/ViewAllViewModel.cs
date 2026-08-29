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
        _marks.MarkChanged += OnMarkChanged;

        SelectedNodes.CollectionChanged += OnSelectedNodesChanged;

        _renderThrottle = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(35)
        };
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

    /// <summary>Grid the overview layer draws from.</summary>
    public ViewAllSpatialIndex SpatialIndex => _graph.Index;

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

    public ViewAllNodeViewModel? ActiveNode
    {
        get => _activeNode;
        private set
        {
            if (SetProperty(ref _activeNode, value))
            {
                OnPropertyChanged(nameof(ActivePath));
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
        if (_graph.TryGetNode(path, out var node))
        {
            node.AccentHex = mark.AccentHex;
            node.Note = mark.Note;
        }
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

        var viewport = new Rect(_viewportLocation, _viewportSize);
        var set = _viewportService.BuildRenderSet(
            _graph.Index,
            _graph.Edges,
            SelectedNodes,
            viewport,
            _viewportZoom,
            _visibleNodeCount);
        Sync(RenderNodes, set.Nodes);
        Sync(RenderEdges, set.Edges);
        DetailLevel = set.DetailLevel;
        LogicalNodeCount = set.LogicalNodeCount;
        IsOverviewActive = ViewAllViewportService.UsesOverview(set.DetailLevel);
        RequestIcons(set.Nodes, set.DetailLevel);

        // Rebuilding is only worth it while the batched layer is on screen; if
        // it is not, the flag survives until the canvas zooms back out.
        if (_isOverviewStale && IsOverviewActive)
        {
            _isOverviewStale = false;
            GraphInvalidated?.Invoke();
        }
    }

    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
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
        foreach (var item in desired)
        {
            if (present.Add(item))
            {
                target.Add(item);
            }
        }
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
