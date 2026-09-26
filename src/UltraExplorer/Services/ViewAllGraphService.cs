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

    /// <summary>The node the layout pass under way is to keep in view, if any (see <see cref="ReflowAnchoredOn"/>).</summary>
    private ViewAllNodeViewModel? _reflowAnchor;

    /// <summary>
    /// The node to keep in view when the tree is arranged again because some
    /// files were ordered by type by a stand-in for a name not looked up yet,
    /// and whether that is already waiting for the names.
    /// </summary>
    private ViewAllNodeViewModel? _typeNamesAnchor;
    private bool _typeNamesWaiting;

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

        if (_layout.TypeNamesPending)
        {
            _typeNamesAnchor = _reflowAnchor ?? _typeNamesAnchor;
            _ = ReflowWhenTypeNamesArriveAsync();
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
        _reflowAnchor = anchor;
        try
        {
            Reflow();
        }
        finally
        {
            _reflowAnchor = null;
        }

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
    /// Arranges the tree again once the Shell has named every kind of file
    /// the last arrangement had to order by a stand-in (see
    /// <see cref="ViewAllLayoutService.TypeNamesPending"/>): a click on the
    /// Type header, or a folder opened while ordered by type, never waits on
    /// the Shell, and the few files whose kind was new move to their places a
    /// moment later.  The node that was being kept in view is kept in view
    /// again.  Only on a thread that can be come back to - the window's - so
    /// the tree is never touched from anywhere else.
    /// </summary>
    private async Task ReflowWhenTypeNamesArriveAsync()
    {
        if (_typeNamesWaiting || SynchronizationContext.Current is null)
        {
            return;
        }

        _typeNamesWaiting = true;
        try
        {
            // Never straight back into the arrangement that asked: the names
            // may all be in already, and a layout pass inside a layout pass
            // would lay out a tree half way through being laid out.
            await Task.Yield();
            await FileTypeNames.WhenPrefetchedAsync();
        }
        finally
        {
            _typeNamesWaiting = false;
        }

        var anchor = _typeNamesAnchor;
        _typeNamesAnchor = null;
        if (_disposed || _layout.Sort.Column != SortColumn.Type)
        {
            return;
        }

        ReflowAnchoredOn(anchor);
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
            ShowOrHideLoadedBranch(node);
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
            var snapshot = await _fileSystem.GetChildrenAsync(node.FullPath, Options, loadCancellation.Token, _layout.Sort);
            loadCancellation.Token.ThrowIfCancellationRequested();

            var added = new List<ViewAllNodeViewModel>(snapshot.Entries.Count);
            ApplySnapshot(node, snapshot, added);

            node.AreChildrenLoaded = true;
            node.IsExpanded = true;
            node.IsTruncated = snapshot.IsTruncated;
            node.ChildLoadLimit = Options.SafeMaximumChildren;
            node.NotifyChildrenChanged();
            ShowOrHideLoadedBranch(node);
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
            var snapshot = await _fileSystem.GetChildrenAsync(node.FullPath, pageOptions, loadCancellation.Token, _layout.Sort);
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

        // Described the way the light reveal describes a step, so the node has
        // the name the disk gives it and not whatever was typed: a later prune
        // compares the two, and a typed "child" for a disk "Child" would be
        // taken for a rename and removed while it still exists.
        ViewAllEntryDescriptor descriptor;
        try
        {
            var described = await _fileSystem.DescribeChainAsync(parent.FullPath, [normalized], cancellationToken);
            if (described.Count == 0)
            {
                return null;
            }

            descriptor = described[0];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or DirectoryNotFoundException or FileNotFoundException)
        {
            return null;
        }

        if (_nodesByPath.TryGetValue(descriptor.FullPath, out var raced))
        {
            return raced;
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
    /// Brings <paramref name="path"/> into the graph without opening anything
    /// on the way: every step that has no node yet gets exactly one, for itself,
    /// and no folder on the chain is read.  It is the counterpart of revealing a
    /// path by expanding its ancestors, for when the tree is not on screen to
    /// show them - the nested canvas has its own picture, and all it needs from
    /// the graph is a node to select.  Expanding the ancestors of
    /// <c>C:\Windows\System32\drivers\etc</c> creates five thousand nodes for
    /// System32 alone and lays the whole tree out again; this creates four.
    ///
    /// The folders on the way are left exactly as they were: not loaded, not
    /// expanded.  A node brought in under a folder that is not open on the tree
    /// is not on the tree either - <see cref="ViewAllNodeViewModel.IsTreeVisible"/>
    /// is false, so it is not counted, indexed, laid out or drawn - and the tree
    /// is only laid out again when one of them lands somewhere it can be seen.
    /// Reading such a folder later keeps the node that is already there, same
    /// object and same identity, and shows it like any of its siblings.
    ///
    /// Everything that exists is described in one trip to the thread pool, and
    /// the graph hears about the result once.  A name the disk spells
    /// differently is taken the disk's way, and a step that already has a node -
    /// under another spelling, or as a root of its own - reuses it.
    /// </summary>
    public async Task<ViewAllChainResult> MaterializeChainAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        IReadOnlyList<string> chain;
        try
        {
            chain = ViewAllPath.AncestorChain(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new ViewAllChainResult(null, path);
        }

        if (chain.Count == 0)
        {
            return new ViewAllChainResult(null, path);
        }

        // A UNC share or a WSL distribution is not a drive, so it only enters
        // the graph when it is asked for.
        if (!_nodesByPath.ContainsKey(chain[0]))
        {
            await AddRootAsync(chain[0], cancellationToken);
        }

        // The graph can change while the disk is being asked - a refresh can
        // take the folder the chain was hanging from away - and then the walk
        // starts again from whatever is there now.  Three goes is plenty: it
        // takes a refresh landing inside a couple of milliseconds each time.
        ViewAllNodeViewModel? reached = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ViewAllNodeViewModel? node = null;
            var next = 0;
            while (next < chain.Count && _nodesByPath.TryGetValue(chain[next], out var known))
            {
                node = known;
                next++;
            }

            if (node is null)
            {
                return new ViewAllChainResult(null, chain[0]);
            }

            if (next == chain.Count)
            {
                return new ViewAllChainResult(node, null);
            }

            reached = node;
            var steps = chain.Skip(next).ToArray();
            var described = await _fileSystem.DescribeChainAsync(node.FullPath, steps, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed)
            {
                return new ViewAllChainResult(null, null);
            }

            if (!IsLive(node))
            {
                continue;
            }

            return AdoptChain(node, steps, described);
        }

        return new ViewAllChainResult(reached is not null && IsLive(reached) ? reached : null, path);
    }

    /// <summary>
    /// The graph half of <see cref="MaterializeChainAsync"/>: turns what the disk
    /// said into nodes, under one layout scope and with one announcement.
    /// </summary>
    private ViewAllChainResult AdoptChain(
        ViewAllNodeViewModel from,
        IReadOnlyList<string> steps,
        IReadOnlyList<ViewAllEntryDescriptor> described)
    {
        var node = from;
        var edges = new List<ViewAllEdgeViewModel>(described.Count);
        var onTree = false;
        string? missing = null;

        using (SuspendLayout())
        {
            for (var index = 0; index < steps.Count; index++)
            {
                if (index >= described.Count)
                {
                    missing = steps[index];
                    break;
                }

                var entry = described[index];
                if (_nodesByPath.TryGetValue(entry.FullPath, out var existing))
                {
                    node = existing;
                    continue;
                }

                if (!node.IsDirectory)
                {
                    missing = steps[index];
                    break;
                }

                var child = CreateNode(entry, node.Depth + 1, node);

                // On the tree only if its folder is open on the tree.  A hidden
                // folder was already taken off it by CreateNode.
                if (!child.IsUserHidden)
                {
                    child.IsTreeVisible = node.IsTreeVisible && node.IsExpanded;
                }

                RestorePosition(child);
                if (!child.IsTreeVisible)
                {
                    // A restored position indexes the node as it lands; one
                    // that is not on the tree has no business in the grid.
                    Index.Remove(child);
                }

                node.Children.Add(child);
                var edge = new ViewAllEdgeViewModel(node, child);
                _edges.Add(edge);
                _incomingEdges[child.Id] = edge;
                edges.Add(edge);
                node.NotifyChildrenChanged();

                if (child.IsTreeVisible)
                {
                    onTree = true;
                    Reflow();
                }

                node = child;
            }
        }

        if (edges.Count == 0)
        {
            return new ViewAllChainResult(node, missing);
        }

        // Laying the tree out settles every link.  Without a layout nothing that
        // was already there moved or changed visibility, so only the new links
        // need deciding.
        if (!onTree)
        {
            foreach (var edge in edges)
            {
                ApplyEdgeVisibility(edge);
            }
        }

        GraphChanged?.Invoke(this, EventArgs.Empty);
        return new ViewAllChainResult(node, missing);
    }

    /// <summary>
    /// Drops the children of a folder that has not been read which are no longer
    /// what the disk has: deleted, moved away, or renamed to another spelling.
    /// Such a folder's children are only the ones brought in by name, and
    /// nothing else would ever notice them going - a folder that has been read
    /// is re-read by <see cref="RefreshBranchAsync"/>, which rebuilds the list
    /// from the disk, but one that has not has no list to rebuild.  Left alone,
    /// a folder deleted after it was selected would keep its node, and asking
    /// for its path again would find it.
    ///
    /// A pruned node goes the way a refreshed-away descendant goes: its whole
    /// subtree leaves the index, the lookup and the edge list.  Returns how many
    /// children went.
    /// </summary>
    public async Task<int> PruneMissingChildrenAsync(
        ViewAllNodeViewModel node,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (node.AreChildrenLoaded || node.Children.Count == 0)
        {
            return 0;
        }

        var candidates = node.Children.ToArray();
        var stale = await _fileSystem.FindStaleAsync(
            [.. candidates.Select(child => child.FullPath)],
            cancellationToken);

        // Read while the disk was being asked: the folder is now the business of
        // RefreshBranchAsync, which does this and more.
        if (_disposed || !IsLive(node) || node.AreChildrenLoaded)
        {
            return 0;
        }

        var doomed = candidates
            .Where((child, index) => stale[index] && node.Children.Contains(child))
            .ToList();
        if (doomed.Count == 0)
        {
            return 0;
        }

        var wasOnTree = doomed.Any(child => child.IsTreeVisible);
        RemoveSubtrees(node, doomed);
        node.NotifyChildrenChanged();
        if (wasOnTree)
        {
            Reflow();
        }

        GraphChanged?.Invoke(this, EventArgs.Empty);
        return doomed.Count;
    }

    /// <summary>
    /// Works out again, from the roots down, which nodes are on the tree: a node
    /// is on it when its folder is on it and open, and it has not been hidden.
    /// Opening a folder decides that for its own children only, so opening one
    /// that is itself off the tree - as happens while the tree is not on screen
    /// and a node was brought in by name, not by opening its ancestors - leaves
    /// children marked as shown under a folder that is not.  They are laid out
    /// nowhere, but they count, and any that kept an old position would be
    /// indexed at it.  This puts that right in one pass, and lays out and
    /// announces only if something was wrong.
    /// </summary>
    /// <returns>True when anything changed.</returns>
    public bool SettleVisibility()
    {
        ThrowIfDisposed();
        var changed = false;
        foreach (var root in _roots)
        {
            changed |= Settle(root, !root.IsUserHidden);
        }

        if (!changed)
        {
            return false;
        }

        Reflow();
        GraphChanged?.Invoke(this, EventArgs.Empty);
        return true;

        static bool Settle(ViewAllNodeViewModel node, bool onTree)
        {
            var changed = node.IsTreeVisible != onTree;
            node.IsTreeVisible = onTree;
            var childrenOnTree = onTree && node.IsExpanded;
            foreach (var child in node.Children)
            {
                changed |= Settle(child, childrenOnTree && !child.IsUserHidden);
            }

            return changed;
        }
    }

    /// <summary>True while <paramref name="node"/> is still the graph's node for its path.</summary>
    private bool IsLive(ViewAllNodeViewModel node)
        => _nodesByPath.TryGetValue(node.FullPath, out var current) && ReferenceEquals(current, node);

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

    /// <summary>
    /// Reads a directory without touching the canvas, under the same enumeration
    /// options the graph uses - so the folder list hides what the canvas hides
    /// and the picker's file filter applies to both.
    /// </summary>
    /// <param name="shownIn">
    /// The order the entries are to be shown in, which decides which of them
    /// are kept when there are more than the options allow (see
    /// <see cref="ViewAllFileSystemService.GetChildrenAsync"/>).
    /// </param>
    public Task<ViewAllDirectorySnapshot> ReadDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken = default,
        ItemSort shownIn = default)
        => _fileSystem.GetChildrenAsync(directoryPath, Options, cancellationToken, shownIn, keepFirstShown: true);

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

    /// <summary>The order each folder's children are laid out in, folders before files.</summary>
    public ItemSort Sort => _layout.Sort;

    /// <summary>
    /// Orders every folder's children by <paramref name="sort"/> from now on
    /// and lays the tree out again.  Unlike <see cref="Relayout"/> nothing the
    /// user placed by hand is let go of: a change of order moves children
    /// within their blocks, it does not tidy the canvas.  The layout is the
    /// same deterministic pass as ever, so going back to names from A puts
    /// every node back exactly where it was.
    /// </summary>
    /// <param name="anchor">
    /// The node the view is on, if any: how far the new layout carried it is
    /// raised through <see cref="LayoutShifted"/>, so the canvas can follow
    /// and it stays where the user was looking.
    /// </param>
    public void SetSort(ItemSort sort, ViewAllNodeViewModel? anchor = null)
    {
        ThrowIfDisposed();
        if (_layout.Sort == sort)
        {
            return;
        }

        _layout.Sort = sort;
        ReflowAnchoredOn(anchor);
        GraphChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Puts hand-placed nodes back into the flow.  A node the user dragged is an
    /// island: it keeps its place, its slot is not reserved, and it is the one
    /// thing on the canvas that can end up on top of something else.  This is the
    /// way back for one node, where Tidy is the way back for all of them.
    /// </summary>
    public bool ReleasePositions(IEnumerable<ViewAllNodeViewModel> nodes)
    {
        ThrowIfDisposed();
        var released = false;
        foreach (var node in nodes)
        {
            if (!node.HasManualPosition)
            {
                continue;
            }

            node.ReleaseManualPosition();
            released = true;
        }

        if (!released)
        {
            return false;
        }

        Reflow();
        GraphChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Works out which links the harness carries and which draw their own line.
    /// Dragging a node changes that - a node pulled out of its block is no longer
    /// one of the children the block was laid out for - and a drag does not go
    /// through any of the graph operations that recompute it.  Without this the
    /// node was left with no line at all: the harness had let go of it, and its
    /// own connection was still hidden from back when the harness carried it.
    /// </summary>
    public void RefreshLinks()
    {
        ThrowIfDisposed();
        UpdateEdgeVisibility();
        LayoutChanged?.Invoke();
    }

    /// <summary>
    /// The folder whose area a point falls in: the folder itself if the point is
    /// on its node, otherwise the innermost folder whose block of children
    /// surrounds it.  Blocks nest, so a click inside a sub-folder's frame belongs
    /// to that sub-folder and not to the drive three levels above it.
    /// </summary>
    public ViewAllNodeViewModel? FolderAt(Point graphPoint)
    {
        ViewAllNodeViewModel? area = null;
        foreach (var node in _nodes)
        {
            if (!node.IsTreeVisible || node.IsUserHidden)
            {
                continue;
            }

            if (node.IsDirectory && node.HasLayoutPosition && node.Bounds.Contains(graphPoint))
            {
                return node;
            }

            if (node.ChildBlock is not { } block
                || !block.BoundsFor(node.Location).Contains(graphPoint))
            {
                continue;
            }

            if (area is null || node.Depth > area.Depth)
            {
                area = node;
            }
        }

        return area;
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

    /// <summary>
    /// Opening a folder shows what is in it - unless the folder is not on the
    /// tree itself, which happens when it was only brought in by name while
    /// the nested canvas was showing.  Its children then stay off the tree too.
    /// </summary>
    private void ShowOrHideLoadedBranch(ViewAllNodeViewModel node)
    {
        if (node.IsTreeVisible && !node.IsUserHidden)
        {
            RevealLoadedBranch(node);
        }
        else
        {
            HideDescendants(node);
        }
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
            ApplyEdgeVisibility(edge);
        }
    }

    private static void ApplyEdgeVisibility(ViewAllEdgeViewModel edge)
    {
        edge.IsTreeVisible = edge.Source.IsTreeVisible
            && edge.Source.IsExpanded
            && edge.Target.IsTreeVisible
            && !IsCoveredByHarness(edge.Source, edge.Target);
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

        Forget(doomed);
        parent.Children.Clear();
    }

    /// <summary>
    /// Removes some of a folder's children, each with everything under it, the
    /// same way a refresh removes all of them.
    /// </summary>
    private void RemoveSubtrees(ViewAllNodeViewModel parent, IReadOnlyCollection<ViewAllNodeViewModel> children)
    {
        var doomed = new List<ViewAllNodeViewModel>();
        foreach (var child in children)
        {
            doomed.Add(child);
            doomed.AddRange(EnumerateDescendants(child));
        }

        var leaving = new HashSet<ViewAllNodeViewModel>(children);
        Forget(doomed);
        for (var index = parent.Children.Count - 1; index >= 0; index--)
        {
            if (leaving.Contains(parent.Children[index]))
            {
                parent.Children.RemoveAt(index);
            }
        }
    }

    /// <summary>
    /// Takes nodes out of every structure the graph keeps: the index, the path
    /// lookup, the node and edge lists, and any read still in flight.
    /// </summary>
    private void Forget(List<ViewAllNodeViewModel> doomed)
    {
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
