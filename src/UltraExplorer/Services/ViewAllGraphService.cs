using System.Windows;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

/// <summary>
/// Owns the lazily materialized View All tree.  No method in this class scans
/// descendants: a branch exists only after its parent has been expanded.
///
/// Nodes and edges are plain lists rather than observable collections: opening a
/// folder with thousands of entries would otherwise raise one change
/// notification per entry.  The canvas binds to the render set instead, which is
/// rebuilt in one pass from <see cref="Index"/>.
/// </summary>
public sealed class ViewAllGraphService : IDisposable
{
    private readonly ViewAllFileSystemService _fileSystem;
    private readonly ViewAllLayoutService _layout;
    private readonly List<ViewAllNodeViewModel> _nodes = [];
    private readonly List<ViewAllEdgeViewModel> _edges = [];
    private readonly List<ViewAllNodeViewModel> _roots = [];
    private readonly Dictionary<string, ViewAllNodeViewModel> _nodesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, ViewAllEdgeViewModel> _incomingEdges = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _loads = [];
    private readonly Dictionary<string, ViewAllNodeState> _restoredStates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Folders the user hid, by path.  A path outlives the node: a hidden folder
    /// inside a branch that is refreshed is destroyed and recreated, and this is
    /// what makes the hide stick across that, across restarts, and across a
    /// change to the enumeration options.
    /// </summary>
    private readonly HashSet<string> _hiddenPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set while a layout pass is moving nodes, so each move is not
    /// separately indexed and announced - the index is rebuilt in one go after.</summary>
    private bool _arranging;

    /// <summary>Depth of nested <see cref="SuspendLayout"/> scopes.</summary>
    private int _layoutSuspended;
    private bool _layoutPending;
    private bool _disposed;

    public ViewAllGraphService(
        ViewAllGraphOptions? options = null,
        ViewAllFileSystemService? fileSystem = null,
        ViewAllLayoutService? layout = null)
    {
        Options = options ?? new ViewAllGraphOptions();
        _fileSystem = fileSystem ?? new ViewAllFileSystemService();
        _layout = layout ?? new ViewAllLayoutService();
    }

    public ViewAllGraphOptions Options { get; private set; }

    public IReadOnlyList<ViewAllNodeViewModel> Nodes => _nodes;

    public IReadOnlyList<ViewAllEdgeViewModel> Edges => _edges;

    public IReadOnlyList<ViewAllNodeViewModel> Roots => _roots;

    /// <summary>Grid over placed nodes; culling and layout both query it.</summary>
    public ViewAllSpatialIndex Index { get; } = new();

    /// <summary>Raised after a structural or logical visibility change.</summary>
    public event EventHandler? GraphChanged;

    /// <summary>
    /// How far the node the user just acted on was carried by the layout.  The
    /// canvas pans by the same amount, so opening a folder never yanks the thing
    /// under the cursor out from under it - the tree grows around it instead.
    /// </summary>
    public event Action<Vector>? LayoutShifted;

    /// <summary>
    /// Raised once per node, right after it is created.  The view model uses it
    /// to attach the Shell icon and any colour label / note stored for the path,
    /// which keeps those concerns out of the graph itself.
    /// </summary>
    public event Action<ViewAllNodeViewModel>? NodeCreated;

    /// <summary>
    /// Raised when a node moved.  Dragging changes positions without changing
    /// structure, so anything caching geometry has to hear about it separately
    /// from <see cref="GraphChanged"/>.
    /// </summary>
    public event Action? LayoutChanged;

    /// <summary>
    /// Applies new enumeration options and re-reads only the branches that are
    /// already open, keeping their expansion and any manual positions.
    /// </summary>
    public async Task ApplyOptionsAsync(ViewAllGraphOptions options, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Options = options;
        foreach (var root in _roots.Where(root => root.IsExpanded).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshBranchAsync(root, cancellationToken);
        }

        GraphChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task InitializeAsync(
        ViewAllWorkspaceState? restoredState = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ClearGraph();

        _restoredStates.Clear();
        _hiddenPaths.Clear();

        // Seeded before the first node is created, so a hidden folder is never
        // briefly visible and never briefly indexed.
        foreach (var hidden in restoredState?.HiddenPaths ?? [])
        {
            try
            {
                _hiddenPaths.Add(ViewAllPath.Normalize(hidden));
            }
            catch
            {
                // Ignore obsolete or malformed paths in a saved workspace.
            }
        }

        if (restoredState?.SchemaVersion == 1)
        {
            foreach (var state in restoredState.Nodes)
            {
                try
                {
                    _restoredStates[ViewAllPath.Normalize(state.Path)] = state;
                }
                catch
                {
                    // Ignore obsolete or malformed paths in a saved workspace.
                }
            }
        }

        var driveEntries = await _fileSystem.GetDriveRootsAsync(cancellationToken);
        foreach (var entry in driveEntries)
        {
            var root = CreateNode(entry, depth: 0, parent: null);
            RestorePosition(root);
        }

        Reflow();

        if (restoredState is null)
        {
            GraphChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // WSL distributions and UNC shares are not drives, so nothing would
        // rediscover them; the workspace lists them explicitly.
        foreach (var extraRoot in restoredState.ExtraRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AddRootAsync(extraRoot, cancellationToken);
        }

        GraphChanged?.Invoke(this, EventArgs.Empty);

        // Parents sort before descendants. Each expansion therefore creates the
        // node needed by the following saved path without a recursive scan.
        var expandedPaths = restoredState.Nodes
            .Where(state => state.IsExpanded)
            .OrderBy(state => PathDepth(state.Path))
            .ThenBy(state => state.Path, StringComparer.OrdinalIgnoreCase)
            .Select(state => state.Path)
            .ToArray();
        using (SuspendLayout())
        {
            foreach (var path in expandedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryGetNode(path, out var node))
                {
                    await ExpandAsync(node, cancellationToken);
                }
            }
        }
    }

    /// <summary>
    /// Lays the whole tree out again.  It is a whole-tree operation on purpose:
    /// opening a folder changes how much room its branch needs, and a tidy tree
    /// is only tidy if its siblings then move over.  The pass is deterministic,
    /// so a refresh, a restart or a second call leaves the canvas exactly as it
    /// was; only a real change to the tree changes the picture.
    /// </summary>
    private void Reflow()
    {
        if (_layoutSuspended > 0)
        {
            _layoutPending = true;
            return;
        }

        _arranging = true;
        try
        {
            _layout.Arrange(_roots);
        }
        finally
        {
            _arranging = false;
        }

        RebuildIndex();

        // Which links the harness covers depends on where the children ended up,
        // so it can only be worked out once they have been placed.  Deciding it
        // before the pass - which is what happened while the layout was held back
        // for a batch of expansions - left every child drawing its own line on
        // top of the harness that already carried it.
        UpdateEdgeVisibility();
        LayoutChanged?.Invoke();
    }

    /// <summary>
    /// Lays out again and reports how far <paramref name="anchor"/> travelled, so
    /// the canvas can pan by the same amount and leave it where the user is
    /// looking.
    /// </summary>
    private void ReflowAnchoredOn(ViewAllNodeViewModel? anchor)
    {
        var before = anchor?.Location ?? default;
        Reflow();
        if (anchor is null)
        {
            return;
        }

        var shift = anchor.Location - before;
        if (Math.Abs(shift.X) > 0.001 || Math.Abs(shift.Y) > 0.001)
        {
            LayoutShifted?.Invoke(shift);
        }
    }

    /// <summary>
    /// Holds the layout back until the scope closes.  Restoring a saved session
    /// expands dozens of folders one after another; laying the tree out once at
    /// the end is the difference between one pass and dozens.
    /// </summary>
    private IDisposable SuspendLayout() => new LayoutScope(this);

    private sealed class LayoutScope : IDisposable
    {
        private readonly ViewAllGraphService _graph;
        private bool _closed;

        public LayoutScope(ViewAllGraphService graph)
        {
            _graph = graph;
            _graph._layoutSuspended++;
        }

        public void Dispose()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _graph._layoutSuspended--;
            if (_graph._layoutSuspended == 0 && _graph._layoutPending)
            {
                _graph._layoutPending = false;
                _graph.Reflow();
            }
        }
    }

    /// <summary>
    /// Refills the grid from what is actually on the canvas.  The index stopped
    /// being an input to the layout when the layout became a tree pass, so it is
    /// now purely what culling, hit testing and drop targets read - and it holds
    /// visible nodes only.  A collapsed branch used to stay in it and quietly
    /// reserve room for nodes nobody could see.
    /// </summary>
    private void RebuildIndex()
    {
        Index.Clear();
        foreach (var node in _nodes)
        {
            if (node.HasLayoutPosition && node.IsTreeVisible && !node.IsUserHidden)
            {
                Index.AddOrUpdate(node);
            }
        }
    }

    public bool CanExpand(ViewAllNodeViewModel node)
        => node.IsDirectory && (Options.FollowReparsePoints || !node.IsReparsePoint);

    public async Task<ViewAllExpansionResult> ToggleAsync(
        ViewAllNodeViewModel node,
        CancellationToken cancellationToken = default)
    {
        if (node.IsExpanded)
        {
            Collapse(node);
            return new ViewAllExpansionResult(node, [], WasLoaded: false, node.IsTruncated);
        }

        return await ExpandAsync(node, cancellationToken);
    }

    public async Task<ViewAllExpansionResult> ExpandAsync(
        ViewAllNodeViewModel node,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!CanExpand(node))
        {
            return new ViewAllExpansionResult(node, [], WasLoaded: false, node.IsTruncated);
        }

        if (node.AreChildrenLoaded)
        {
            node.IsExpanded = true;
            RevealLoadedBranch(node);
            ReflowAnchoredOn(node);
            UpdateEdgeVisibility();
            GraphChanged?.Invoke(this, EventArgs.Empty);
            return new ViewAllExpansionResult(node, [], WasLoaded: false, node.IsTruncated);
        }

        CancelLoad(node);
        var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loads[node.Id] = loadCancellation;
        node.IsLoading = true;
        node.ErrorMessage = string.Empty;

        try
        {
            var snapshot = await _fileSystem.GetChildrenAsync(node.FullPath, Options, loadCancellation.Token);
            loadCancellation.Token.ThrowIfCancellationRequested();

            var added = new List<ViewAllNodeViewModel>(snapshot.Entries.Count);
            ApplySnapshot(node, snapshot, added);

            node.AreChildrenLoaded = true;
            node.IsExpanded = true;
            node.IsTruncated = snapshot.IsTruncated;
            node.ChildLoadLimit = Options.SafeMaximumChildren;
            node.NotifyChildrenChanged();
            RevealLoadedBranch(node);
            ReflowAnchoredOn(node);
            UpdateEdgeVisibility();
            GraphChanged?.Invoke(this, EventArgs.Empty);
            return new ViewAllExpansionResult(node, added, WasLoaded: true, snapshot.IsTruncated);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ViewAllExpansionResult(node, [], WasLoaded: false, node.IsTruncated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            node.ErrorMessage = ex is UnauthorizedAccessException ? "Access denied" : ex.Message;
            return new ViewAllExpansionResult(node, [], WasLoaded: false, node.IsTruncated);
        }
        finally
        {
            if (_loads.TryGetValue(node.Id, out var active) && ReferenceEquals(active, loadCancellation))
            {
                _loads.Remove(node.Id);
                node.IsLoading = false;
                loadCancellation.Dispose();
            }
        }
    }

    public void Collapse(ViewAllNodeViewModel node)
    {
        ThrowIfDisposed();
        CancelLoad(node);
        node.IsExpanded = false;
        HideDescendants(node);
        ReflowAnchoredOn(node);
        UpdateEdgeVisibility();
        GraphChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Collapses every expanded root branch without discarding loaded data.</summary>
    public void CollapseAll()
    {
        ThrowIfDisposed();
        foreach (var node in _nodes.Where(node => node.IsExpanded).ToArray())
        {
            CancelLoad(node);
            node.IsExpanded = false;
        }

        foreach (var root in _roots)
        {
            HideDescendants(root);
        }

        Reflow();
        UpdateEdgeVisibility();
        GraphChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Explicit progressive loading for unusually large folders. It re-reads
    /// only this one directory with a larger cap and appends previously unseen
    /// objects; descendants are still never scanned.
    /// </summary>
    public async Task<ViewAllExpansionResult> LoadMoreAsync(
        ViewAllNodeViewModel node,
        int additionalChildren = 5_000,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!node.IsDirectory || !node.IsTruncated)
        {
            return new ViewAllExpansionResult(node, [], WasLoaded: false, node.IsTruncated);
        }

        var currentLimit = Math.Max(node.ChildLoadLimit, Options.SafeMaximumChildren);
        var nextLimit = Math.Clamp(
            currentLimit + Math.Max(32, additionalChildren),
            32,
            ViewAllGraphOptions.MaximumChildrenCeiling);
        var pageOptions = Options with { MaximumChildrenPerFolder = nextLimit };
        CancelLoad(node);
        var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loads[node.Id] = loadCancellation;
        node.IsLoading = true;

        try
        {
            var snapshot = await _fileSystem.GetChildrenAsync(node.FullPath, pageOptions, loadCancellation.Token);
            var added = new List<ViewAllNodeViewModel>();
            ApplySnapshot(node, snapshot, added);
            node.ChildLoadLimit = nextLimit;
            node.IsTruncated = snapshot.IsTruncated;
            node.NotifyChildrenChanged();
            ReflowAnchoredOn(node);
            UpdateEdgeVisibility();
            GraphChanged?.Invoke(this, EventArgs.Empty);
            return new ViewAllExpansionResult(node, added, WasLoaded: true, snapshot.IsTruncated);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ViewAllExpansionResult(node, [], WasLoaded: false, node.IsTruncated);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            node.ErrorMessage = ex is UnauthorizedAccessException ? "Access denied" : ex.Message;
            return new ViewAllExpansionResult(node, [], WasLoaded: false, node.IsTruncated);
        }
        finally
        {
            if (_loads.TryGetValue(node.Id, out var active) && ReferenceEquals(active, loadCancellation))
            {
                _loads.Remove(node.Id);
                node.IsLoading = false;
                loadCancellation.Dispose();
            }
        }
    }

    /// <summary>
    /// Re-enumerates only this folder. Saved child coordinates and the
    /// expansion state of every descendant are retained, so refreshing a branch
    /// never silently collapses the tree the user had opened.
    /// </summary>
    public async Task<ViewAllExpansionResult> RefreshBranchAsync(
        ViewAllNodeViewModel node,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var previouslyExpanded = new List<string>();
        foreach (var descendant in EnumerateDescendants(node))
        {
            // Only a position the user chose is worth carrying across the
            // rebuild.  An automatic one is reproduced exactly by the layout,
            // which is why F5 does not rearrange the canvas.
            if (descendant.HasManualPosition)
            {
                _restoredStates[descendant.FullPath] = new ViewAllNodeState(
                    descendant.FullPath,
                    descendant.Location.X,
                    descendant.Location.Y,
                    true,
                    descendant.IsExpanded);
            }

            if (descendant.IsExpanded)
            {
                previouslyExpanded.Add(descendant.FullPath);
            }
        }

        RemoveDescendants(node);
        node.AreChildrenLoaded = false;
        node.IsExpanded = false;
        node.IsTruncated = false;
        node.NotifyChildrenChanged();

        ViewAllExpansionResult result;
        using (SuspendLayout())
        {
            result = await ExpandAsync(node, cancellationToken);

            // Parents sort before descendants, so each re-expansion has already
            // created the node the next path needs.
            foreach (var path in previouslyExpanded
                         .OrderBy(PathDepth)
                         .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryGetNode(path, out var restored) && !restored.IsExpanded)
                {
                    await ExpandAsync(restored, cancellationToken);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Materializes one child that enumeration left out: a hidden folder on a
    /// path that was typed or asked for by a caller, or a file the current file
    /// type filters away.  Naming something is a stronger statement than any
    /// display rule, which is how the address bar has always behaved.
    /// </summary>
    public async Task<ViewAllNodeViewModel?> AdoptChildAsync(
        ViewAllNodeViewModel parent,
        string childPath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!parent.IsDirectory)
        {
            return null;
        }

        string normalized;
        try
        {
            normalized = ViewAllPath.Normalize(childPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (_nodesByPath.TryGetValue(normalized, out var existing))
        {
            return existing;
        }

        if (!ViewAllPath.Equals(Path.GetDirectoryName(normalized) ?? string.Empty, parent.FullPath))
        {
            return null;
        }

        ViewAllEntryDescriptor descriptor;
        try
        {
            descriptor = await _fileSystem.DescribeEntryAsync(normalized, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or DirectoryNotFoundException or FileNotFoundException)
        {
            return null;
        }

        var child = CreateNode(descriptor, parent.Depth + 1, parent);
        RestorePosition(child);
        parent.Children.Add(child);
        var edge = new ViewAllEdgeViewModel(parent, child);
        _edges.Add(edge);
        _incomingEdges[child.Id] = edge;
        parent.NotifyChildrenChanged();
        Reflow();
        UpdateEdgeVisibility();
        GraphChanged?.Invoke(this, EventArgs.Empty);
        return child;
    }

    /// <summary>
    /// Adds an extra top-level root for a directory that is not a local drive
    /// (a WSL distribution or a UNC share).  Existing roots are left alone.
    /// </summary>
    public async Task<ViewAllNodeViewModel?> AddRootAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (TryGetNode(directoryPath, out var existing))
        {
            return existing;
        }

        try
        {
            var descriptor = await _fileSystem.DescribeDirectoryAsync(directoryPath, cancellationToken);
            if (_nodesByPath.TryGetValue(descriptor.FullPath, out var alreadyKnown))
            {
                return alreadyKnown;
            }

            var root = CreateNode(descriptor, depth: 0, parent: null);
            RestorePosition(root);
            Reflow();
            GraphChanged?.Invoke(this, EventArgs.Empty);
            return root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public bool TryGetNode(string path, out ViewAllNodeViewModel node)
    {
        try
        {
            return _nodesByPath.TryGetValue(ViewAllPath.Normalize(path), out node!);
        }
        catch
        {
            node = null!;
            return false;
        }
    }

    public ViewAllNodeViewModel? FindNearestDropTarget(Point graphPoint, double maximumDistance = 96)
    {
        var probe = new Rect(
            graphPoint.X - maximumDistance,
            graphPoint.Y - maximumDistance,
            maximumDistance * 2,
            maximumDistance * 2);
        var candidates = new List<ViewAllNodeViewModel>(32);
        Index.Query(probe, candidates);

        ViewAllNodeViewModel? nearest = null;
        var nearestDistance = double.MaxValue;
        foreach (var node in candidates)
        {
            if (!node.IsTreeVisible || !node.IsDirectory)
            {
                continue;
            }

            var bounds = node.Bounds;
            var dx = Math.Max(bounds.Left - graphPoint.X, Math.Max(0, graphPoint.X - bounds.Right));
            var dy = Math.Max(bounds.Top - graphPoint.Y, Math.Max(0, graphPoint.Y - bounds.Bottom));
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = node;
            }
        }

        return nearestDistance <= maximumDistance ? nearest : null;
    }

    /// <summary>The visible node under a graph-space point, if any.</summary>
    /// <summary>
    /// Lays the whole canvas out again from scratch, dropping every position -
    /// including the ones the user dragged.  A graph that has been rearranged by
    /// hand over a long session ends up with anchors nobody remembers placing,
    /// and every later expansion has to route around them; this is the way back
    /// to a clean tree.
    /// </summary>
    public void Relayout()
    {
        ThrowIfDisposed();

        foreach (var node in _nodes)
        {
            node.ReleaseManualPosition();
        }

        Reflow();
        GraphChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Folders the user has hidden, whether or not their node exists.</summary>
    public IReadOnlyCollection<string> HiddenPaths => _hiddenPaths;

    /// <summary>
    /// Takes a folder and everything under it off the canvas.  The nodes are
    /// kept - the branch is not collapsed and nothing is re-read - but they stop
    /// being drawn and, just as importantly, stop occupying space: they leave the
    /// spatial index, so the room they took is available to everything else.
    /// </summary>
    public void Hide(ViewAllNodeViewModel node)
    {
        ThrowIfDisposed();
        if (node.IsUserHidden)
        {
            return;
        }

        _hiddenPaths.Add(node.FullPath);
        node.IsUserHidden = true;
        node.IsTreeVisible = false;
        Index.Remove(node);

        foreach (var descendant in EnumerateDescendants(node))
        {
            descendant.IsTreeVisible = false;
            Index.Remove(descendant);
        }

        node.Parent?.NotifyChildrenChanged();
        Reflow();
        UpdateEdgeVisibility();
        GraphChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Puts a hidden folder back.  Its automatic position is released first: the
    /// space it used was given away while it was gone, so it is laid out afresh
    /// rather than dropped on top of whatever moved in.  A position the user
    /// chose is kept, because that one was a decision.
    /// </summary>
    public bool Show(string path)
    {
        ThrowIfDisposed();
        string normalized;
        try
        {
            normalized = ViewAllPath.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!_hiddenPaths.Remove(normalized))
        {
            return false;
        }

        if (!_nodesByPath.TryGetValue(normalized, out var node))
        {
            // The node is gone - the branch was refreshed away, say.  Dropping
            // the path is enough; it will come back visible when it is next read.
            GraphChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        node.IsUserHidden = false;
        node.IsTreeVisible = node.Parent is null || (node.Parent.IsTreeVisible && node.Parent.IsExpanded);

        if (node.IsExpanded)
        {
            RevealLoadedBranch(node);
        }

        Reflow();
        UpdateEdgeVisibility();
        GraphChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool ShowAllHidden()
    {
        ThrowIfDisposed();
        if (_hiddenPaths.Count == 0)
        {
            return false;
        }

        foreach (var path in _hiddenPaths.ToArray())
        {
            Show(path);
        }

        _hiddenPaths.Clear();
        return true;
    }

    public ViewAllNodeViewModel? HitTest(Point graphPoint)
        => Index.HitTest(graphPoint, node => node.IsTreeVisible);

    /// <summary>
    /// The node under a point, ignoring the ones being dragged.  Without the
    /// exclusion a drag would immediately land on itself.
    /// </summary>
    public ViewAllNodeViewModel? HitTest(Point graphPoint, Predicate<ViewAllNodeViewModel> exclude)
        => Index.HitTest(graphPoint, node => node.IsTreeVisible && !exclude(node));

    public ViewAllWorkspaceState CaptureState(ViewAllViewportState viewport)
        => new()
        {
            ViewportX = viewport.Location.X,
            ViewportY = viewport.Location.Y,
            ViewportZoom = viewport.Zoom,
            ExtraRoots = _roots.Where(node => !node.IsDrive).Select(node => node.FullPath).ToList(),
            HiddenPaths = [.. _hiddenPaths],
            Nodes = _nodes.Select(node => new ViewAllNodeState(
                    node.FullPath,
                    node.Location.X,
                    node.Location.Y,
                    node.HasManualPosition,
                    node.IsExpanded))
                .ToList()
        };

    private ViewAllNodeViewModel CreateNode(
        ViewAllEntryDescriptor entry,
        int depth,
        ViewAllNodeViewModel? parent)
    {
        var node = new ViewAllNodeViewModel(entry, depth, parent)
        {
            LocationObserver = OnNodeMoved
        };

        // A folder that was hidden comes back hidden, however it was recreated.
        if (_hiddenPaths.Contains(entry.FullPath))
        {
            node.IsUserHidden = true;
            node.IsTreeVisible = false;
        }

        _nodes.Add(node);
        _nodesByPath[entry.FullPath] = node;
        if (parent is null)
        {
            _roots.Add(node);
        }

        NodeCreated?.Invoke(node);
        return node;
    }

    private void OnNodeMoved(ViewAllNodeViewModel node)
    {
        // A layout pass moves every node on the canvas; indexing and announcing
        // each one separately would cost thousands of redraws for one pass.  The
        // index is refilled and the change announced once, when the pass ends.
        if (_arranging)
        {
            return;
        }

        // A hidden node must stay out of the grid however it moves.  Dragging an
        // ancestor offsets every descendant, hidden ones included, and would
        // otherwise quietly put the whole hidden spread back in the way.
        if (node.IsUserHidden)
        {
            return;
        }

        // A node without a layout position has not been placed yet; indexing it
        // at the origin would make the origin look occupied to the layout.
        if (node.HasLayoutPosition)
        {
            Index.AddOrUpdate(node);
            LayoutChanged?.Invoke();
        }
    }

    private void ApplySnapshot(
        ViewAllNodeViewModel parent,
        ViewAllDirectorySnapshot snapshot,
        ICollection<ViewAllNodeViewModel> added)
    {
        foreach (var entry in snapshot.Entries)
        {
            if (_nodesByPath.TryGetValue(entry.FullPath, out var existing))
            {
                if (existing.Parent == parent && !parent.Children.Contains(existing))
                {
                    parent.Children.Add(existing);
                }
                continue;
            }

            var child = CreateNode(entry, parent.Depth + 1, parent);
            RestorePosition(child);
            parent.Children.Add(child);
            var edge = new ViewAllEdgeViewModel(parent, child);
            _edges.Add(edge);
            _incomingEdges[child.Id] = edge;
            added.Add(child);
        }
    }

    private void RestorePosition(ViewAllNodeViewModel node)
    {
        if (!_restoredStates.TryGetValue(node.FullPath, out var state))
        {
            return;
        }

        // Only a position the user chose is restored.  An automatic one is not
        // worth saving: the layout is deterministic, so it reproduces the same
        // coordinate from the shape of the tree alone - and a saved coordinate
        // would freeze a canvas into the way an older build laid it out.
        if (!state.HasManualPosition)
        {
            return;
        }

        node.RestoreLocation(new Point(state.X, state.Y), true);
    }

    private void RevealLoadedBranch(ViewAllNodeViewModel parent)
    {
        foreach (var child in parent.Children)
        {
            // Expanding the parent must not undo a hide.
            if (child.IsUserHidden)
            {
                continue;
            }

            child.IsTreeVisible = true;
            if (child.IsExpanded)
            {
                RevealLoadedBranch(child);
            }
            else
            {
                HideDescendants(child);
            }
        }
    }

    private static void HideDescendants(ViewAllNodeViewModel parent)
    {
        foreach (var child in parent.Children)
        {
            child.IsTreeVisible = false;
            HideDescendants(child);
        }
    }

    private void UpdateEdgeVisibility()
    {
        foreach (var edge in _edges)
        {
            edge.IsTreeVisible = edge.Source.IsTreeVisible
                && edge.Source.IsExpanded
                && edge.Target.IsTreeVisible
                && !IsCoveredByHarness(edge.Source, edge.Target);
        }
    }

    /// <summary>
    /// Whether the harness already draws this link.  A child on a multi-row
    /// lattice hangs off its folder's trunk, so its own connection would be a
    /// second line to the same place - and it is exactly those second lines,
    /// crossing the block, that made a folder unreadable.  A child the user
    /// dragged has left the lattice and keeps its line, which is what makes it
    /// visibly an exception.
    /// </summary>
    private static bool IsCoveredByHarness(ViewAllNodeViewModel parent, ViewAllNodeViewModel child)
    {
        return parent.ChildBlock is { Rows: > 1 } block
            && block.Holds(parent.Location, child);
    }

    private static IEnumerable<ViewAllNodeViewModel> EnumerateDescendants(ViewAllNodeViewModel node)
    {
        foreach (var child in node.Children)
        {
            yield return child;
            foreach (var descendant in EnumerateDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    private void RemoveDescendants(ViewAllNodeViewModel parent)
    {
        var doomed = new List<ViewAllNodeViewModel>();
        Collect(parent);

        void Collect(ViewAllNodeViewModel node)
        {
            foreach (var child in node.Children)
            {
                doomed.Add(child);
                Collect(child);
            }
        }

        if (doomed.Count == 0)
        {
            parent.Children.Clear();
            return;
        }

        var doomedSet = new HashSet<ViewAllNodeViewModel>(doomed);
        foreach (var node in doomed)
        {
            CancelLoad(node);
            node.LocationObserver = null;
            Index.Remove(node);
            if (_incomingEdges.Remove(node.Id, out var edge))
            {
                edge.Dispose();
            }

            _nodesByPath.Remove(node.FullPath);
            node.Children.Clear();
        }

        // One compacting pass instead of a removal scan per node.
        _nodes.RemoveAll(doomedSet.Contains);
        _edges.RemoveAll(edge => doomedSet.Contains(edge.Target) || doomedSet.Contains(edge.Source));
        parent.Children.Clear();
    }

    private void CancelLoad(ViewAllNodeViewModel node)
    {
        if (_loads.Remove(node.Id, out var cancellation))
        {
            cancellation.Cancel();
            cancellation.Dispose();
            node.IsLoading = false;
        }
    }

    private void ClearGraph()
    {
        foreach (var cancellation in _loads.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        _loads.Clear();

        foreach (var edge in _edges)
        {
            edge.Dispose();
        }

        foreach (var node in _nodes)
        {
            node.LocationObserver = null;
        }

        _edges.Clear();
        _nodes.Clear();
        _roots.Clear();
        _incomingEdges.Clear();
        _nodesByPath.Clear();
        Index.Clear();
    }

    private static int PathDepth(string path)
        => path.Count(character => character is '\\' or '/');

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearGraph();
    }
}
