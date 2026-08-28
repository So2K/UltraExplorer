using System.Collections.ObjectModel;
using System.Windows;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

/// <summary>
/// Owns the lazily materialized View All tree.  No method in this class scans
/// descendants: a branch exists only after its parent has been expanded.
/// </summary>
public sealed class ViewAllGraphService : IDisposable
{
    private readonly ViewAllFileSystemService _fileSystem;
    private readonly ViewAllLayoutService _layout;
    private readonly Dictionary<string, ViewAllNodeViewModel> _nodesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, ViewAllEdgeViewModel> _incomingEdges = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _loads = [];
    private readonly Dictionary<string, ViewAllNodeState> _restoredStates = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// Applies new enumeration options and re-reads only the branches that are
    /// already open, keeping their expansion and any manual positions.
    /// </summary>
    public async Task ApplyOptionsAsync(ViewAllGraphOptions options, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Options = options;
        foreach (var root in Roots.Where(root => root.IsExpanded).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshBranchAsync(root, cancellationToken);
        }

        GraphChanged?.Invoke(this, EventArgs.Empty);
    }

    public ObservableCollection<ViewAllNodeViewModel> Nodes { get; } = [];
    public ObservableCollection<ViewAllEdgeViewModel> Edges { get; } = [];
    public IEnumerable<ViewAllNodeViewModel> Roots => Nodes.Where(node => node.Parent is null);

    /// <summary>Raised after a structural or logical visibility change.</summary>
    public event EventHandler? GraphChanged;

    /// <summary>
    /// Raised once per node, right after it is created.  The view model uses it
    /// to attach the Shell icon and any colour label / note stored for the path,
    /// which keeps those concerns out of the graph itself.
    /// </summary>
    public event Action<ViewAllNodeViewModel>? NodeCreated;

    public async Task InitializeAsync(
        ViewAllWorkspaceState? restoredState = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ClearGraph();

        _restoredStates.Clear();
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

        _layout.PlaceRoots(Roots, Nodes);

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
        foreach (var path in expandedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryGetNode(path, out var node))
            {
                await ExpandAsync(node, cancellationToken);
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
            _layout.PlaceChildren(node, node.Children, Nodes);
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
        int additionalChildren = 750,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!node.IsDirectory || !node.IsTruncated)
        {
            return new ViewAllExpansionResult(node, [], WasLoaded: false, node.IsTruncated);
        }

        var currentLimit = Math.Max(node.ChildLoadLimit, Options.SafeMaximumChildren);
        var nextLimit = Math.Clamp(currentLimit + Math.Max(32, additionalChildren), 32, 10_000);
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
            _layout.PlaceChildren(node, added, Nodes);
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
            _restoredStates[descendant.FullPath] = new ViewAllNodeState(
                descendant.FullPath,
                descendant.Location.X,
                descendant.Location.Y,
                descendant.HasManualPosition,
                descendant.IsExpanded);
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
        var result = await ExpandAsync(node, cancellationToken);

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

        return result;
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
            if (_nodesByPath.ContainsKey(descriptor.FullPath))
            {
                return _nodesByPath[descriptor.FullPath];
            }

            var root = CreateNode(descriptor, depth: 0, parent: null);
            RestorePosition(root);
            _layout.PlaceRoots([root], Nodes);
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
        ViewAllNodeViewModel? nearest = null;
        var nearestDistance = double.MaxValue;
        foreach (var node in Nodes.Where(node => node.IsTreeVisible && node.IsDirectory))
        {
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

    public ViewAllWorkspaceState CaptureState(ViewAllViewportState viewport)
        => new()
        {
            ViewportX = viewport.Location.X,
            ViewportY = viewport.Location.Y,
            ViewportZoom = viewport.Zoom,
            ExtraRoots = Roots.Where(node => !node.IsDrive).Select(node => node.FullPath).ToList(),
            Nodes = Nodes.Select(node => new ViewAllNodeState(
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
        var node = new ViewAllNodeViewModel(entry, depth, parent);
        Nodes.Add(node);
        _nodesByPath[entry.FullPath] = node;
        NodeCreated?.Invoke(node);
        return node;
    }

    /// <summary>Collapses every expanded root branch without discarding loaded data.</summary>
    public void CollapseAll()
    {
        ThrowIfDisposed();
        foreach (var node in Nodes.Where(node => node.IsExpanded).ToArray())
        {
            CancelLoad(node);
            node.IsExpanded = false;
        }

        foreach (var root in Roots)
        {
            HideDescendants(root);
        }

        UpdateEdgeVisibility();
        GraphChanged?.Invoke(this, EventArgs.Empty);
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
            Edges.Add(edge);
            _incomingEdges[child.Id] = edge;
            added.Add(child);
        }
    }

    private void RestorePosition(ViewAllNodeViewModel node)
    {
        if (_restoredStates.TryGetValue(node.FullPath, out var state))
        {
            node.RestoreLocation(new Point(state.X, state.Y), state.HasManualPosition);
        }
    }

    private void RevealLoadedBranch(ViewAllNodeViewModel parent)
    {
        foreach (var child in parent.Children)
        {
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
        foreach (var edge in Edges)
        {
            edge.IsTreeVisible = edge.Source.IsTreeVisible
                && edge.Source.IsExpanded
                && edge.Target.IsTreeVisible;
        }
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
        foreach (var child in parent.Children.ToArray())
        {
            CancelLoad(child);
            RemoveDescendants(child);
            if (_incomingEdges.Remove(child.Id, out var edge))
            {
                Edges.Remove(edge);
                edge.Dispose();
            }

            _nodesByPath.Remove(child.FullPath);
            Nodes.Remove(child);
        }

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

        foreach (var edge in Edges)
        {
            edge.Dispose();
        }
        Edges.Clear();
        Nodes.Clear();
        _incomingEdges.Clear();
        _nodesByPath.Clear();
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
