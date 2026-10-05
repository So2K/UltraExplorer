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

    /// <summary>
    /// Extra roots the saved workspace listed that could not be reached when
    /// they were asked for, as they were saved.  Kept only to be saved again,
    /// so a share that is offline for one session is still there for the next
    /// - until it has been out of reach <see cref="MaximumRootMisses"/> starts
    /// in a row, and is taken for gone.
    /// </summary>
    private readonly List<string> _unavailableRoots = [];

    /// <summary>
    /// Extra roots the saved workspace listed that have not been asked for
    /// yet (see <see cref="ProbeExtraRootsAsync"/>): saved again as they were,
    /// with what was open and placed under them, until they have been.
    /// </summary>
    private readonly List<string> _pendingRoots = [];

    /// <summary>How many starts in a row each extra root has been out of reach at, as saved and as this session found.</summary>
    private readonly Dictionary<string, int> _rootMisses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The folders the saved workspace had, for what lies under a root not here yet: opened when it comes, saved again while it does not.</summary>
    private ViewAllNodeState[] _savedNodes = [];

    /// <summary>An extra root out of reach at this many starts in a row is forgotten: a share or a distribution that is gone for good.</summary>
    internal const int MaximumRootMisses = 10;

    /// <summary>How long an extra root is waited for at start-up before it counts as out of reach this time.</summary>
    internal static readonly TimeSpan ExtraRootTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Drives that had not answered when start-up went on without them, by
    /// the root each would have.  One that answers ready becomes a root in its
    /// place among the drives (see <see cref="AdmitLateDrivesAsync"/>); one
    /// that answers not ready is passed over, as start-up always passed such a
    /// drive over.  Until it answers, what was open on it is saved as it was.
    /// </summary>
    private readonly Dictionary<string, Task<ViewAllEntryDescriptor?>> _pendingDrives = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Starts so far: a drive one start went on without is not brought into the graph a later start built.</summary>
    private int _starts;

    /// <summary>
    /// How long start-up waits for every drive to answer before it goes on
    /// with those that have.  A local disk answers within milliseconds; a
    /// drive mapped to a server that is off only after some twenty seconds,
    /// longer than Explorer's replacement waits for a window it made to be
    /// ready, or the dialog agent for its worker.
    /// </summary>
    internal static readonly TimeSpan DriveAnswerWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Drives found slow to answer, by every graph in the process, by root:
    /// the question still out, or when one that was not waited for answered
    /// not ready.  Each new window - Explorer's replacement, Win+E, a dialog's
    /// picker - waited the whole <see cref="DriveAnswerWait"/> again for a
    /// drive mapped to a server that is off, and asked it again besides; now
    /// it takes the question still out instead, and waits for neither (see
    /// <see cref="AddDriveRootsAsync"/>).
    /// </summary>
    private static readonly Dictionary<string, SlowDrive> SlowDrives = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a drive that answered not ready, too late to be waited for, is still taken for slow: a drive back meanwhile answers its next question at once, and comes in as soon as it does.</summary>
    internal static readonly TimeSpan SlowDriveMemory = TimeSpan.FromMinutes(10);

    /// <summary>A drive found slow: its question still out, or answered not ready at <see cref="NotReadySince"/>.</summary>
    private sealed record SlowDrive(Task<ViewAllEntryDescriptor?> Answer, DateTime? NotReadySince);

    /// <summary>
    /// What refreshes of a folder still under way owe it: the sub-folders that
    /// were open before, to be opened again once the folder has been read.  By
    /// the folder's path, and shared by every refresh of it in flight - a
    /// second refresh arriving while the first is reading finds the branch
    /// already emptied, and without the first one's list it would bring the
    /// folder back with every sub-folder closed.
    /// </summary>
    private readonly Dictionary<string, PendingRefresh> _pendingRefreshes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Nodes brought in by name - a path typed, revealed or asked for by a
    /// caller - that no listing of their folder has had: a hidden folder
    /// while hidden items are off, a file the type filter leaves out, a step
    /// of a path under a folder never read.  A refresh of a folder above one
    /// builds the branch again from a listing, which leaves it out; the
    /// refresh asks for it by name again instead (see <see cref="RefreshBranchAsync"/>).
    /// </summary>
    private readonly HashSet<ViewAllNodeViewModel> _namedOnly = [];

    /// <summary>
    /// Roots that had been read and were closed when the enumeration options
    /// changed: what they hold is what the old options let through, so the
    /// next opening reads them again (see <see cref="ApplyOptionsAsync"/>).
    /// </summary>
    private readonly HashSet<ViewAllNodeViewModel> _staleRoots = [];

    /// <summary>Set while a layout pass is moving nodes, so each move is not
    /// separately indexed and announced - the index is rebuilt in one go after.</summary>
    private bool _arranging;

    /// <summary>
    /// The innermost <see cref="SuspendLayout"/> scope of the operation under
    /// way, if it holds one.  It flows with that operation across its awaits
    /// and into what it calls, and nowhere else: a refresh holds the layout
    /// back while it reads its sub-folders again, and a folder opened by the
    /// user meanwhile is still laid out at once.
    /// </summary>
    private readonly AsyncLocal<LayoutScope?> _layoutScope = new();
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

    /// <summary>Existing incoming-edge index, used to cull work as well as pixels.</summary>
    internal IReadOnlyDictionary<Guid, ViewAllEdgeViewModel> IncomingEdges => _incomingEdges;

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
    /// Raised when a drive start-up went on without has answered ready and is
    /// a root, with what was open on it last time opened again.
    /// </summary>
    public event Action<ViewAllNodeViewModel>? DriveAdded;

    /// <summary>
    /// Applies new enumeration options and re-reads only the branches that are
    /// already open, keeping their expansion and any manual positions.  One
    /// that was read and closed is read again when it next opens.
    /// </summary>
    public async Task ApplyOptionsAsync(ViewAllGraphOptions options, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Options = options;
        foreach (var root in _roots.Where(root => !root.IsExpanded && root.AreChildrenLoaded))
        {
            _staleRoots.Add(root);
        }

        foreach (var root in _roots.Where(root => root.IsExpanded).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshBranchAsync(root, cancellationToken);
        }

        GraphChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Sets the enumeration options before anything has been read: the choices
    /// remembered from last time, handed over at startup so the drives are
    /// read that way from the first rather than read once each way.  Once the
    /// graph holds nodes, options change through <see cref="ApplyOptionsAsync"/>,
    /// which reads again what is open.
    /// </summary>
    public void PresetOptions(ViewAllGraphOptions options)
    {
        ThrowIfDisposed();
        if (_nodes.Count > 0)
        {
            throw new InvalidOperationException("The graph has already been read; options change through ApplyOptionsAsync.");
        }

        Options = options;
    }

    /// <param name="deferExtraRoots">
    /// Leaves the shares and WSL distributions the workspace lists to
    /// <see cref="ProbeExtraRootsAsync"/>, for the window to ask for once it
    /// is up: one whose server is off takes as long as the network allows to
    /// say so, and start-up must not wait on it.  Otherwise they are asked
    /// for here, all at once, each for at most <see cref="ExtraRootTimeout"/>.
    /// </param>
    public async Task InitializeAsync(
        ViewAllWorkspaceState? restoredState = null,
        CancellationToken cancellationToken = default,
        bool deferExtraRoots = false)
    {
        ThrowIfDisposed();
        ClearGraph();

        _restoredStates.Clear();
        _hiddenPaths.Clear();
        _unavailableRoots.Clear();
        _pendingRoots.Clear();
        _rootMisses.Clear();
        _pendingDrives.Clear();
        var start = ++_starts;

        // A workspace file edited by hand, or written by something else, can
        // hold nulls where lists and paths belong; they are passed over like
        // any other path that no longer means anything.
        var savedNodes = (restoredState?.Nodes ?? []).Where(state => state?.Path is not null).ToArray();
        _savedNodes = savedNodes;

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
            foreach (var state in savedNodes)
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

        await AddDriveRootsAsync(cancellationToken);
        Reflow();

        if (restoredState is null)
        {
            GraphChanged?.Invoke(this, EventArgs.Empty);
            _ = AdmitLateDrivesAsync(start);
            return;
        }

        // WSL distributions and UNC shares are not drives, so nothing would
        // rediscover them; the workspace lists them explicitly.  They are
        // asked for later, all at once (see ProbeExtraRootsAsync).
        foreach (var (path, misses) in restoredState.ExtraRootMisses ?? [])
        {
            if (!string.IsNullOrWhiteSpace(path) && misses > 0)
            {
                _rootMisses[path] = misses;
            }
        }

        foreach (var extraRoot in restoredState.ExtraRoots ?? [])
        {
            if (string.IsNullOrWhiteSpace(extraRoot)
                || _pendingRoots.Contains(extraRoot, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                _ = ViewAllPath.Normalize(extraRoot);
                _pendingRoots.Add(extraRoot);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Not a path at all; nothing to retry.
            }
        }

        GraphChanged?.Invoke(this, EventArgs.Empty);

        // Parents sort before descendants. Each expansion therefore creates the
        // node needed by the following saved path without a recursive scan.
        var expandedPaths = savedNodes
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

        // Only now: what was open on a late drive is opened again as it comes,
        // never while the folders above are still being opened here.
        _ = AdmitLateDrivesAsync(start);

        if (!deferExtraRoots)
        {
            await ProbeExtraRootsAsync(ExtraRootTimeout, cancellationToken);
        }
    }

    /// <summary>The saved extra roots not asked for yet.</summary>
    public IReadOnlyList<string> PendingExtraRoots => _pendingRoots;

    /// <summary>Whether <paramref name="path"/> lies in a saved extra root not asked for yet, or on a drive that has not answered yet.</summary>
    public bool IsInPendingRoot(string path)
        => _pendingRoots.Any(root => IsAtOrUnder(path, root)) || _pendingDrives.Keys.Any(drive => IsAtOrUnder(path, drive));

    /// <summary>
    /// The drives, asked all at once (<see cref="ViewAllFileSystemService.AskDriveRoots"/>):
    /// each that has answered ready within <see cref="DriveAnswerWait"/> is
    /// made a root, in name order, as a start that waited for every drive
    /// made them; the rest are left to answer in their own time
    /// (<see cref="_pendingDrives"/>).  A drive another start in the process
    /// already found slow (<see cref="SlowDrives"/>) is not waited for: its
    /// question still out is taken over, and it comes in, or is passed over,
    /// when that answers.
    /// </summary>
    private async Task AddDriveRootsAsync(CancellationToken cancellationToken)
    {
        var asked = _fileSystem.AskDriveRoots(cancellationToken);
        var drives = new List<(string Path, Task<ViewAllEntryDescriptor?> Answer)>(asked.Count);
        var slow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (SlowDrives)
        {
            foreach (var (path, answer) in asked)
            {
                if (SlowDrives.TryGetValue(path, out var known))
                {
                    if (!known.Answer.IsCompleted)
                    {
                        slow.Add(path);
                        drives.Add((path, known.Answer));
                        continue;
                    }

                    if (known.NotReadySince is { } since && DateTime.UtcNow - since < SlowDriveMemory)
                    {
                        slow.Add(path);
                    }
                    else
                    {
                        SlowDrives.Remove(path);
                    }
                }

                drives.Add((path, answer));
            }
        }

        try
        {
            await Task.WhenAll(drives.Where(drive => !slow.Contains(drive.Path)).Select(drive => drive.Answer))
                .WaitAsync(DriveAnswerWait, cancellationToken);
        }
        catch (TimeoutException)
        {
            // Some have not answered yet: start-up goes on without them.
        }

        // A question that failed fails the start as it always did, before
        // anything is made.
        await Task.WhenAll(drives.Where(drive => drive.Answer.IsCompleted).Select(drive => drive.Answer));
        foreach (var (path, answer) in drives)
        {
            if (!answer.IsCompleted)
            {
                _pendingDrives[path] = answer;
                RememberSlowDrive(path, answer);
                continue;
            }

            if (answer.Result is { } entry)
            {
                // One taken for slow that has answered ready all the same is
                // back: the next start waits for it as for any other.
                if (slow.Contains(path))
                {
                    lock (SlowDrives)
                    {
                        SlowDrives.Remove(path);
                    }
                }

                RestorePosition(CreateNode(entry, depth: 0, parent: null));
            }
        }
    }

    /// <summary>
    /// Notes a drive whose question start-up went on without (see
    /// <see cref="SlowDrives"/>), for the next start in the process to take
    /// over: until it answers, then - answered not ready - for
    /// <see cref="SlowDriveMemory"/>.  One that answers ready, or fails, is
    /// asked afresh and waited for as ever by the next start.
    /// </summary>
    private static void RememberSlowDrive(string path, Task<ViewAllEntryDescriptor?> answer)
    {
        lock (SlowDrives)
        {
            if (SlowDrives.TryGetValue(path, out var known) && ReferenceEquals(known.Answer, answer))
            {
                return;
            }

            SlowDrives[path] = new SlowDrive(answer, NotReadySince: null);
        }

        _ = answer.ContinueWith(
            done =>
            {
                lock (SlowDrives)
                {
                    if (!SlowDrives.TryGetValue(path, out var known) || !ReferenceEquals(known.Answer, done))
                    {
                        return;
                    }

                    if (done.IsCompletedSuccessfully && done.Result is null)
                    {
                        SlowDrives[path] = known with { NotReadySince = DateTime.UtcNow };
                    }
                    else
                    {
                        SlowDrives.Remove(path);
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Takes in the drives start-up went on without, as each answers (see
    /// <see cref="AdmitDriveAsync"/>), for as long as this start's graph is
    /// the one there is.
    /// </summary>
    private async Task AdmitLateDrivesAsync(int start)
    {
        try
        {
            while (_pendingDrives.Count > 0 && start == _starts && !_disposed)
            {
                await Task.WhenAny(_pendingDrives.Values);
                foreach (var (path, answer) in _pendingDrives.Where(pending => pending.Value.IsCompleted).ToArray())
                {
                    if (start != _starts || _disposed)
                    {
                        return;
                    }

                    await AdmitDriveAsync(path, answer);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // The window closed while a folder on the drive was being opened again.
        }
    }

    /// <summary>
    /// A drive that answered after start-up went on without it, taken off
    /// <see cref="_pendingDrives"/>.  Ready, it becomes a root where a start
    /// that waited for it would have put it - among the drives, in name order,
    /// ahead of every share and distribution - with what was open on it last
    /// time opened again, and <see cref="DriveAdded"/> is raised.  Returns it,
    /// or null for a drive that is not ready, or that was taken in already.
    /// </summary>
    private async Task<ViewAllNodeViewModel?> AdmitDriveAsync(string path, Task<ViewAllEntryDescriptor?> answer)
    {
        if (_disposed
            || !_pendingDrives.Remove(path)
            || !answer.IsCompletedSuccessfully
            || answer.Result is not { } entry
            || _nodesByPath.ContainsKey(entry.FullPath))
        {
            return null;
        }

        var root = CreateNode(entry, depth: 0, parent: null);
        PlaceAmongDrives(root);
        RestorePosition(root);
        Reflow();
        GraphChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await ReopenSavedUnderAsync(root, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // A folder on it closed or read again meanwhile: left as it is now.
        }

        if (!_disposed)
        {
            DriveAdded?.Invoke(root);
        }

        return root;
    }

    /// <summary>Moves a drive just made a root to where a start that found it would have put it: among the drives, in name order, ahead of every share and distribution.</summary>
    private void PlaceAmongDrives(ViewAllNodeViewModel root)
    {
        _roots.Remove(root);
        var place = _roots.FindIndex(other => !other.IsDrive
            || string.Compare(other.FullPath, root.FullPath, StringComparison.OrdinalIgnoreCase) > 0);
        _roots.Insert(place < 0 ? _roots.Count : place, root);
    }

    /// <summary>The drive start-up went on without whose root is <paramref name="path"/>, if it has not answered or not been taken in yet.</summary>
    private (string Path, Task<ViewAllEntryDescriptor?> Answer)? PendingDrive(string path)
    {
        try
        {
            var normalized = ViewAllPath.Normalize(path);
            return _pendingDrives.TryGetValue(normalized, out var answer) ? (normalized, answer) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks for every saved extra root not asked for yet, all at once, each
    /// for at most <paramref name="timeout"/>.  Each one that answers becomes
    /// a root as it does - <paramref name="added"/> is told - with what was
    /// open under it last time opened again, and its count of starts out of
    /// reach cleared.  Each one that does not - a server that is off, a VPN
    /// not yet connected, a distribution not installed any more - is out of
    /// reach this time, not gone: it is written back with the rest (see
    /// <see cref="CaptureState"/>) and tried again at the next start, unless
    /// that makes <see cref="MaximumRootMisses"/> starts in a row, when it is
    /// forgotten.  Returns how many were added.
    /// </summary>
    public async Task<int> ProbeExtraRootsAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        Action<ViewAllNodeViewModel>? added = null)
    {
        if (_pendingRoots.Count == 0 || _disposed)
        {
            return 0;
        }

        var probes = _pendingRoots
            .Select(path => (Path: path, Answer: ProbeAsync(path, timeout, cancellationToken)))
            .ToList();
        var count = 0;
        while (probes.Count > 0)
        {
            await Task.WhenAny(probes.Select(probe => probe.Answer));
            if (_disposed)
            {
                return count;
            }

            cancellationToken.ThrowIfCancellationRequested();
            for (var index = probes.Count - 1; index >= 0; index--)
            {
                var (path, answer) = probes[index];
                if (!answer.IsCompleted)
                {
                    continue;
                }

                probes.RemoveAt(index);
                if (!_pendingRoots.Remove(path))
                {
                    continue;
                }

                if (answer.Result is not { } descriptor)
                {
                    // Out of reach once more.
                    var misses = _rootMisses.GetValueOrDefault(path) + 1;
                    _rootMisses[path] = misses;
                    if (misses < MaximumRootMisses)
                    {
                        _unavailableRoots.Add(path);
                    }

                    continue;
                }

                _rootMisses.Remove(path);
                if (await AddProbedRootAsync(descriptor, cancellationToken) is { } root)
                {
                    count++;
                    added?.Invoke(root);
                }
            }
        }

        return count;
    }

    /// <summary>What an extra root answers, or null when it does not within <paramref name="timeout"/>.  Never throws but for cancellation.</summary>
    private async Task<ViewAllEntryDescriptor?> ProbeAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await _fileSystem.DescribeDirectoryAsync(path, cancellationToken).WaitAsync(timeout, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException
            or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>An extra root that answered: made a root - unless it already is one, added meanwhile - with its saved folders opened again.</summary>
    private async Task<ViewAllNodeViewModel?> AddProbedRootAsync(ViewAllEntryDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (_nodesByPath.ContainsKey(descriptor.FullPath))
        {
            return null;
        }

        var root = CreateNode(descriptor, depth: 0, parent: null);
        RestorePosition(root);
        Reflow();
        GraphChanged?.Invoke(this, EventArgs.Empty);
        await ReopenSavedUnderAsync(root, cancellationToken);
        return root;
    }

    /// <summary>What was open under a root that has just come, last time, opened again: what cannot be read now stays closed.</summary>
    private async Task ReopenSavedUnderAsync(ViewAllNodeViewModel root, CancellationToken cancellationToken)
    {
        var expandedPaths = _savedNodes
            .Where(state => state.IsExpanded && IsAtOrUnderNormalized(state.Path, root.FullPath))
            .OrderBy(state => PathDepth(state.Path))
            .ThenBy(state => state.Path, StringComparer.OrdinalIgnoreCase)
            .Select(state => state.Path)
            .ToArray();
        using (SuspendLayout())
        {
            foreach (var path in expandedPaths)
            {
                if (_disposed)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (TryGetNode(path, out var node))
                {
                    try
                    {
                        await ExpandAsync(node, cancellationToken);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // What cannot be read now stays closed; the root is here.
                    }
                }
            }
        }
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or inside it, by name alone.</summary>
    private static bool IsAtOrUnder(string path, string root)
    {
        try
        {
            return IsAtOrUnderNormalized(ViewAllPath.Normalize(path), ViewAllPath.Normalize(root));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// <see cref="IsAtOrUnder"/> for two paths already spelled the one way
    /// (<see cref="ViewAllPath.Normalize"/>) - saved folders are, being
    /// written from their nodes - so thousands can be looked through with no
    /// path built.
    /// </summary>
    private static bool IsAtOrUnderNormalized(string path, string root) =>
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
        && (path.Length == root.Length
            || root.EndsWith(Path.DirectorySeparatorChar)
            || path[root.Length] == Path.DirectorySeparatorChar);

    /// <summary><paramref name="paths"/> spelled the one way, leaving out any that are not paths at all.</summary>
    private static List<string> Normalized(IEnumerable<string> paths)
    {
        var normalized = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                normalized.Add(ViewAllPath.Normalize(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        return normalized;
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
        if (_layoutScope.Value is { IsOpen: true } scope)
        {
            scope.Pending = true;
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
        // Whether anything is still ordered by type is not worth working out:
        // a layout pass that finds nothing to move moves nothing.
        if (_disposed)
        {
            return;
        }

        ReflowAnchoredOn(anchor);
    }

    /// <summary>
    /// Holds the layout back until the scope closes.  Restoring a saved session
    /// expands dozens of folders one after another; laying the tree out once at
    /// the end is the difference between one pass and dozens.
    ///
    /// <para>Held back for the operation that takes the scope only, through
    /// every await of it (see <see cref="_layoutScope"/>).  A count for the
    /// whole graph held it back for everything else as well: a folder opened
    /// while another was being read again got no places until the other's
    /// last sub-folder had been read, and then jumped into place.</para>
    /// </summary>
    private IDisposable SuspendLayout() => new LayoutScope(this);

    private sealed class LayoutScope : IDisposable
    {
        private readonly ViewAllGraphService _graph;

        /// <summary>The scope of the same operation this one is inside, which a pass held back here is handed on to.</summary>
        private readonly LayoutScope? _outer;

        public LayoutScope(ViewAllGraphService graph)
        {
            _graph = graph;
            _outer = graph._layoutScope.Value is { IsOpen: true } outer ? outer : null;
            graph._layoutScope.Value = this;
        }

        public bool IsOpen { get; private set; } = true;

        /// <summary>Whether a layout pass was held back while the scope was open.</summary>
        public bool Pending { get; set; }

        public void Dispose()
        {
            if (!IsOpen)
            {
                return;
            }

            IsOpen = false;
            _graph._layoutScope.Value = _outer;
            if (!Pending)
            {
                return;
            }

            if (_outer is { IsOpen: true } outer)
            {
                outer.Pending = true;
            }
            else
            {
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

    /// <summary>
    /// Opens a folder, reading it first when it has not been read.
    /// </summary>
    /// <param name="childLimit">
    /// How many children to read when that is more than the options' cap:
    /// what Load more had brought into the folder before it was read again,
    /// so that a refresh keeps it (see <see cref="RefreshBranchAsync"/>).
    /// </param>
    public Task<ViewAllExpansionResult> ExpandAsync(
        ViewAllNodeViewModel node,
        CancellationToken cancellationToken = default,
        int childLimit = 0)
        => ExpandAsync(node, cancellationToken, childLimit, open: true);

    /// <param name="open">
    /// False to read the folder without opening it, as a refresh asked to
    /// leave a closed folder closed does (see <see cref="RefreshBranchAsync"/>):
    /// its children are made off the tree, as a closed folder's are, and one
    /// already read is left as it is.
    /// </param>
    /// <param name="preRead">
    /// What the folder holds, already read by a refresh before it took the
    /// branch down (see <see cref="RefreshBranchAsync"/>), read with
    /// <paramref name="childLimit"/>: put in without asking the disk again.
    /// </param>
    private async Task<ViewAllExpansionResult> ExpandAsync(
        ViewAllNodeViewModel node,
        CancellationToken cancellationToken,
        int childLimit,
        bool open,
        ViewAllDirectorySnapshot? preRead = null)
    {
        ThrowIfDisposed();
        if (!CanExpand(node))
        {
            return new ViewAllExpansionResult(node, [], WasLoaded: false, node.IsTruncated);
        }

        if (node.AreChildrenLoaded)
        {
            if (!open)
            {
                return new ViewAllExpansionResult(node, [], WasLoaded: false, node.IsTruncated);
            }

            // Closed while the options changed: read again under the new
            // ones, with what was open below it opened again.
            if (_staleRoots.Remove(node))
            {
                return await RefreshBranchAsync(node, cancellationToken);
            }

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

        // The token is taken once, before the read.  Collapsing the folder or
        // refreshing it away cancels this load while it is on the disk, and
        // the source is only disposed below, by the load itself - asking a
        // disposed source for its token throws, and nothing up the chain of
        // callers would catch that.
        var token = loadCancellation.Token;
        var readOptions = ReadOptionsFor(childLimit);
        try
        {
            var snapshot = preRead ?? await _fileSystem.GetChildrenAsync(node.FullPath, readOptions, token, _layout.SortFor(node.FullPath));
            token.ThrowIfCancellationRequested();

            // A folder the graph let go of while it was being read - the
            // whole graph built again, say - is not given children.
            if (_disposed || !IsLive(node))
            {
                return new ViewAllExpansionResult(node, [], WasLoaded: false, node.IsTruncated);
            }

            var added = new List<ViewAllNodeViewModel>(snapshot.Entries.Count);
            ApplySnapshot(node, snapshot, added);

            node.AreChildrenLoaded = true;
            if (open)
            {
                node.IsExpanded = true;
            }

            node.IsTruncated = snapshot.IsTruncated;
            node.ChildLoadLimit = readOptions.SafeMaximumChildren;
            node.NotifyChildrenChanged();
            if (node.IsExpanded)
            {
                ShowOrHideLoadedBranch(node);
            }
            else
            {
                HideDescendants(node);
            }

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

            // A folder that may not be listed may still be passed through - a
            // home share's parent, WindowsApps - and what is named below it is
            // reached that way and laid out under it.  It opens, as it did
            // when it read as empty, but stays unread, so the next opening
            // asks the disk again.  Not one collapsed or let go of meanwhile,
            // nor one only to be read.
            if (ex is UnauthorizedAccessException && open && !token.IsCancellationRequested && !_disposed && IsLive(node))
            {
                node.IsExpanded = true;
                ShowOrHideLoadedBranch(node);
                ReflowAnchoredOn(node);
                UpdateEdgeVisibility();
                GraphChanged?.Invoke(this, EventArgs.Empty);
            }

            return new ViewAllExpansionResult(node, [], WasLoaded: false, node.IsTruncated);
        }
        finally
        {
            if (_loads.TryGetValue(node.Id, out var active) && ReferenceEquals(active, loadCancellation))
            {
                _loads.Remove(node.Id);
                node.IsLoading = false;
            }

            loadCancellation.Dispose();
        }
    }

    /// <summary>The options a folder is read with when <paramref name="childLimit"/> children are wanted: the graph's, with a higher cap when Load more had read further.</summary>
    private ViewAllGraphOptions ReadOptionsFor(int childLimit)
        => childLimit > Options.SafeMaximumChildren
            ? Options with { MaximumChildrenPerFolder = Math.Min(childLimit, ViewAllGraphOptions.MaximumChildrenCeiling) }
            : Options;

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

        // Taken once, before the read, for the reason given in ExpandAsync.
        var token = loadCancellation.Token;
        try
        {
            var snapshot = await _fileSystem.GetChildrenAsync(node.FullPath, pageOptions, token, _layout.SortFor(node.FullPath));
            token.ThrowIfCancellationRequested();

            // A folder refreshed or collapsed away while the page was read is
            // no longer the graph's: its children would go into a node that
            // nothing looks at, and their paths would shadow the live ones.
            if (_disposed || !IsLive(node))
            {
                return new ViewAllExpansionResult(node, [], WasLoaded: false, node.IsTruncated);
            }

            var added = new List<ViewAllNodeViewModel>();
            ApplySnapshot(node, snapshot, added);

            // A new child is made on the tree, which is where it belongs only
            // under a folder that is open on it.  Load more on a closed one
            // left the page on the tree under a folder the layout does not
            // measure, and a child of it placed by hand had every layout pass
            // after that throw.
            if (node.IsExpanded)
            {
                ShowOrHideLoadedBranch(node);
            }
            else
            {
                HideDescendants(node);
            }

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
            }

            loadCancellation.Dispose();
        }
    }

    /// <summary>
    /// Re-enumerates only this folder. Saved child coordinates, the expansion
    /// state of every descendant and how far Load more had read each folder
    /// are retained, so refreshing a branch never silently collapses the tree
    /// the user had opened or takes away what Load more brought in.
    /// </summary>
    /// <param name="forChange">
    /// For a refresh because something in the folder changed - on the disk,
    /// or by a file operation - rather than one asked for, or one for a
    /// folder about to be shown: a folder that is closed is left closed.  It
    /// is read, and what was open in it is opened again for when it is
    /// opened, but nothing of it is shown; without it every collapsed folder
    /// a change touched opened itself.  Otherwise the folder is opened, as F5
    /// and a caller about to show it want.
    /// </param>
    public async Task<ViewAllExpansionResult> RefreshBranchAsync(
        ViewAllNodeViewModel node,
        CancellationToken cancellationToken = default,
        bool forChange = false)
    {
        ThrowIfDisposed();
        _staleRoots.Remove(node);

        // A refresh of this folder already under way has emptied the branch
        // this one is about to look through, so its list is joined rather than
        // replaced by an empty one.  Whichever refresh reads the folder last
        // opens everything on it again.
        if (!_pendingRefreshes.TryGetValue(node.FullPath, out var pending))
        {
            pending = new PendingRefresh();
            _pendingRefreshes[node.FullPath] = pending;
        }

        // Open once read, unless read for a change and closed - as the first
        // refresh of it found it: a later one finds it closed by that one, to
        // be read.
        pending.Open |= !forChange || (pending.Refreshes == 0 && node.IsExpanded);
        pending.Refreshes++;
        try
        {
            // Read before anything is taken down.  A read that failed - a
            // share that blinked, a folder that could not be listed for a
            // moment - left the folder emptied and closed, with everything
            // that was open in it gone and no longer watched; now all of it
            // stays as it was, and the folder only says it could not be read.
            // A folder that is gone is let go of as ever.  Read as ExpandAsync
            // reads, and cancelled as that read is: collapsing the folder, or a
            // refresh of it or above it, meanwhile ends this one with nothing
            // touched - the other has it.  Read here and not in a method of its
            // own, so that nothing can happen between the read coming back and
            // the branch being taken down: an await more let a refresh above
            // replace the folder in between, and this one then took the
            // replacement's read away from it.
            ViewAllDirectorySnapshot? read = null;
            if (CanExpand(node))
            {
                CancelLoad(node);
                var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _loads[node.Id] = reading;
                node.IsLoading = true;
                var token = reading.Token;
                try
                {
                    read = await _fileSystem.GetChildrenAsync(node.FullPath, ReadOptionsFor(node.ChildLoadLimit), token, _layout.SortFor(node.FullPath));
                    token.ThrowIfCancellationRequested();
                    if (_disposed || !IsLive(node))
                    {
                        return new ViewAllExpansionResult(node, [], WasLoaded: false, node.IsTruncated);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new ViewAllExpansionResult(node, [], WasLoaded: false, node.IsTruncated);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && ex is not DirectoryNotFoundException)
                {
                    node.ErrorMessage = ex is UnauthorizedAccessException ? "Access denied" : ex.Message;
                    return new ViewAllExpansionResult(node, [], WasLoaded: false, node.IsTruncated);
                }
                catch (DirectoryNotFoundException)
                {
                    // Gone: taken down below, and the read again says so.
                }
                finally
                {
                    if (_loads.TryGetValue(node.Id, out var active) && ReferenceEquals(active, reading))
                    {
                        _loads.Remove(node.Id);
                        node.IsLoading = false;
                    }

                    reading.Dispose();
                }
            }

            var previouslyExpanded = pending.Expanded;
            var pagedTo = pending.ChildLoadLimits;
            var named = pending.Named;
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
                    pagedTo[descendant.FullPath] = Math.Max(pagedTo.GetValueOrDefault(descendant.FullPath), descendant.ChildLoadLimit);
                }

                // No listing brings back what no listing had: a hidden folder
                // typed into the address bar, say, with hidden items off.
                if (_namedOnly.Contains(descendant))
                {
                    named.Add(descendant.FullPath);
                }

                // Nor the folders above what is selected, once the listing
                // leaves them out - a hidden folder, as hidden items are
                // hidden - which let go of the selection with them: they are
                // asked for by name, as a folder typed into the address bar
                // is.  Not what is selected itself: a hidden item selected
                // goes with the rest.
                if (descendant.IsSelected)
                {
                    for (var folder = descendant.Parent; folder is not null && !ReferenceEquals(folder, node); folder = folder.Parent)
                    {
                        named.Add(folder.FullPath);
                    }
                }
            }

            // A refresh of a folder inside this one still under way has
            // emptied and closed it to read it, so what it is to open again -
            // the folder itself among it, if it is to be open - is this
            // one's to open again too.  Without it the folder came back
            // closed, with everything that was open in it gone.
            foreach (var (path, inner) in _pendingRefreshes)
            {
                if (ReferenceEquals(inner, pending) || !IsAtOrUnderNormalized(path, node.FullPath))
                {
                    continue;
                }

                if (inner.Open)
                {
                    previouslyExpanded.Add(path);
                    pagedTo[path] = Math.Max(pagedTo.GetValueOrDefault(path), inner.OwnLimit);
                }

                previouslyExpanded.UnionWith(inner.Expanded);
                named.UnionWith(inner.Named);
                foreach (var (folder, limit) in inner.ChildLoadLimits)
                {
                    pagedTo[folder] = Math.Max(pagedTo.GetValueOrDefault(folder), limit);
                }
            }

            // Read as far as Load more had read it, or what it brought in
            // would go again with every change on the disk.
            var ownLimit = node.ChildLoadLimit;
            pending.OwnLimit = Math.Max(pending.OwnLimit, ownLimit);
            RemoveDescendants(node);
            node.AreChildrenLoaded = false;
            node.IsExpanded = false;
            node.IsTruncated = false;
            node.NotifyChildrenChanged();

            ViewAllExpansionResult result;
            using (SuspendLayout())
            {
                result = await ExpandAsync(node, cancellationToken, ownLimit, pending.Open, read);

                // Parents sort before descendants, so each re-expansion - and
                // each child brought back by name - has already created the
                // node the next path needs.  Taken as the lists stand now: a
                // refresh that starts meanwhile adds to them, and restores what
                // it added itself.
                var adoptedAny = false;
                foreach (var path in previouslyExpanded
                             .Union(named, StringComparer.OrdinalIgnoreCase)
                             .OrderBy(PathDepth)
                             .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                             .ToArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryGetNode(path, out var restored))
                    {
                        // Brought in by name and left out of the listing: asked
                        // for by name again, under its folder if that is back -
                        // and only if it is still on the disk.
                        if (!named.Contains(path)
                            || !TryGetNode(Path.GetDirectoryName(path) ?? string.Empty, out var folder)
                            || await AdoptChildAsync(folder, path, cancellationToken, announce: false) is not { } adopted)
                        {
                            continue;
                        }

                        restored = adopted;
                        adoptedAny = true;
                    }

                    if (!restored.IsExpanded && previouslyExpanded.Contains(path))
                    {
                        await ExpandAsync(restored, cancellationToken, pagedTo.GetValueOrDefault(path));
                    }
                }

                // What was brought back by name is announced once, not once
                // each: a folder hundreds had been asked for in by name raised
                // as many graph changes, each after a pass over every link.
                if (adoptedAny)
                {
                    Reflow();
                    UpdateEdgeVisibility();
                    GraphChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            return result;
        }
        finally
        {
            if (--pending.Refreshes == 0
                && _pendingRefreshes.TryGetValue(node.FullPath, out var current)
                && ReferenceEquals(current, pending))
            {
                _pendingRefreshes.Remove(node.FullPath);
            }
        }
    }

    /// <summary>The refreshes of one folder in flight, the sub-folders they are to open again and how far Load more had read each, and what they are to ask for by name again (see <see cref="_pendingRefreshes"/>).</summary>
    private sealed class PendingRefresh
    {
        public HashSet<string> Expanded { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> ChildLoadLimits { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Named { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Refreshes { get; set; }

        /// <summary>Whether the folder is to be open once read (see the forChange of <see cref="RefreshBranchAsync"/>).</summary>
        public bool Open { get; set; }

        /// <summary>How far Load more had read the folder itself, for a refresh of a folder above it that starts meanwhile.</summary>
        public int OwnLimit { get; set; }
    }

    /// <summary>
    /// Materializes one child that enumeration left out: a hidden folder on a
    /// path that was typed or asked for by a caller, or a file the current file
    /// type filters away.  Naming something is a stronger statement than any
    /// display rule, which is how the address bar has always behaved.
    /// </summary>
    public Task<ViewAllNodeViewModel?> AdoptChildAsync(
        ViewAllNodeViewModel parent,
        string childPath,
        CancellationToken cancellationToken = default)
        => AdoptChildAsync(parent, childPath, cancellationToken, announce: true);

    /// <param name="announce">
    /// False to leave the layout pass, the links and the announcement to the
    /// caller, who brings many back at once and announces them together (see
    /// <see cref="RefreshBranchAsync"/>).
    /// </param>
    private async Task<ViewAllNodeViewModel?> AdoptChildAsync(
        ViewAllNodeViewModel parent,
        string childPath,
        CancellationToken cancellationToken,
        bool announce)
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

        // A folder the graph let go of while the disk was asked - refreshed
        // away, say - is not given a child: it would be the graph's node for
        // its path, under a folder on no tree, and the folder's next listing
        // would pass over it as one already placed elsewhere.
        if (_disposed || !IsLive(parent))
        {
            return null;
        }

        if (_nodesByPath.TryGetValue(descriptor.FullPath, out var raced))
        {
            return raced;
        }

        var child = CreateNode(descriptor, parent.Depth + 1, parent);
        _namedOnly.Add(child);

        // On the tree only if its folder is open on the tree, as a step of a
        // chain is (see AdoptChain).  A folder that could not be opened - a
        // link, say - is closed, and a child shown under it was laid out
        // nowhere and drawn at the origin; given a place there by a drag, it
        // was arranged as an island of a folder never measured, and every
        // layout pass after that threw.
        if (!child.IsUserHidden)
        {
            child.IsTreeVisible = parent.IsTreeVisible && parent.IsExpanded;
        }

        RestorePosition(child);
        if (!child.IsTreeVisible)
        {
            Index.Remove(child);
        }

        parent.Children.Add(child);
        var edge = new ViewAllEdgeViewModel(parent, child);
        _edges.Add(edge);
        _incomingEdges[child.Id] = edge;
        parent.NotifyChildrenChanged();
        if (announce)
        {
            Reflow();
            UpdateEdgeVisibility();
            GraphChanged?.Invoke(this, EventArgs.Empty);
        }

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
                _namedOnly.Add(child);

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

        // A drive start-up went on without is waited for, not asked again as
        // a folder: it would answer no sooner, and would come in under another
        // name and out of its place among the drives.  One that answers not
        // ready is asked as a folder after all, as it always was.
        if (PendingDrive(directoryPath) is { } pending)
        {
            await Task.WhenAny(pending.Answer).WaitAsync(cancellationToken);
            if (await AdmitDriveAsync(pending.Path, pending.Answer) is { } drive)
            {
                return drive;
            }

            if (TryGetNode(directoryPath, out existing))
            {
                return existing;
            }
        }

        // A drive reached after start-up - a stick plugged in since - is a
        // drive: described as the drive list describes one, and put among the
        // drives.  Asked as a folder it became a plain folder root named
        // "E:\" at the end of the roots.  One that is not ready is out of
        // reach, as a folder that cannot be described is.
        else if (DriveLetterRoot(directoryPath) is { } drivePath)
        {
            if (await DescribeDriveAsync(drivePath, cancellationToken) is not { } driveEntry)
            {
                return null;
            }

            if (_nodesByPath.TryGetValue(driveEntry.FullPath, out var known))
            {
                return known;
            }

            var drive = CreateNode(driveEntry, depth: 0, parent: null);
            PlaceAmongDrives(drive);
            RestorePosition(drive);
            Reflow();
            GraphChanged?.Invoke(this, EventArgs.Empty);
            return drive;
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

    /// <summary><paramref name="path"/> spelled the one way, when it is the root of a drive letter (see <see cref="IsDriveLetterRoot"/>); otherwise null.</summary>
    private static string? DriveLetterRoot(string path)
    {
        try
        {
            var normalized = ViewAllPath.Normalize(path);
            return IsDriveLetterRoot(normalized) ? normalized : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>A drive's cell as the drive list makes it (<see cref="ViewAllFileSystemService.DescribeDrive"/>), asked off the window's thread; null for a drive that is not ready or not there.</summary>
    private static Task<ViewAllEntryDescriptor?> DescribeDriveAsync(string root, CancellationToken cancellationToken)
        => Task.Run<ViewAllEntryDescriptor?>(
            () =>
            {
                try
                {
                    return ViewAllFileSystemService.DescribeDrive(new DriveInfo(root));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    return null;
                }
            },
            cancellationToken);

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

    /// <summary>The order each folder's children are laid out in, folders before files, unless <see cref="SortOf"/> gives it one of its own.</summary>
    public ItemSort Sort => _layout.Sort;

    /// <summary>Each folder's own order, by its path; null, and every folder is in <see cref="Sort"/>.</summary>
    public Func<string, ItemSort>? SortOf
    {
        get => _layout.SortOf;
        set => _layout.SortOf = value;
    }

    /// <summary>
    /// Lays the tree out again after any order changed - the default, or one
    /// folder's own - keeping what <see cref="SetSort"/> keeps: every hand
    /// placed position, and <paramref name="anchor"/> where the view is.
    /// </summary>
    public void Resort(ItemSort sort, ViewAllNodeViewModel? anchor = null)
    {
        ThrowIfDisposed();
        _layout.Sort = sort;
        ReflowAnchoredOn(anchor);
        GraphChanged?.Invoke(this, EventArgs.Empty);
    }

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

        // Its branch comes back only as far as the folder itself does: one
        // shown again under a closed parent is still off the tree, and so is
        // everything under it, however open.
        if (node.IsExpanded)
        {
            ShowOrHideLoadedBranch(node);
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
    {
        // The roots not here - not asked for yet, or out of reach this
        // session - go back in too, unless one has been added again since:
        // an offline share is not a share the user removed.  So do the
        // folders saved under them, open and placed as they were.  A drive
        // letter never does, here or below: the drive list finds a drive at
        // every start, and one reached after startup - a stick plugged in
        // since - became a root of its own, which saved as a share was looked
        // for at every start for MaximumRootMisses starts after it was gone.
        var absent = _pendingRoots.Concat(_unavailableRoots)
            .Where(path => !TryGetNode(path, out _) && !IsDriveLetterRoot(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var nodes = _nodes.Select(node => new ViewAllNodeState(
                node.FullPath,
                node.Location.X,
                node.Location.Y,
                node.HasManualPosition,
                node.IsExpanded))
            .ToList();
        // So do those on a drive that has not answered yet: it is not here yet,
        // not gone, and a start that waited for it would have them.
        var keptRoots = Normalized(absent).Concat(_pendingDrives.Keys).ToList();
        if (keptRoots.Count > 0)
        {
            nodes.AddRange(_savedNodes.Where(state =>
                keptRoots.Any(root => IsAtOrUnderNormalized(state.Path, root)) && !_nodesByPath.ContainsKey(state.Path)));
        }

        return new()
        {
            ViewportX = viewport.Location.X,
            ViewportY = viewport.Location.Y,
            ViewportZoom = viewport.Zoom,
            ExtraRoots = _roots.Where(node => !node.IsDrive && !IsDriveLetterRoot(node.FullPath)).Select(node => node.FullPath)
                .Concat(absent)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ExtraRootMisses = absent
                .Where(path => _rootMisses.GetValueOrDefault(path) > 0)
                .ToDictionary(path => path, path => _rootMisses[path], StringComparer.OrdinalIgnoreCase),
            HiddenPaths = [.. _hiddenPaths],
            Nodes = nodes
        };
    }

    /// <summary>Whether a path, as the graph spells it, is the root of a drive letter - <c>F:\</c> - rather than a share or a folder.</summary>
    private static bool IsDriveLetterRoot(string path) =>
        path.Length == 3
        && char.IsAsciiLetter(path[0])
        && path[1] == Path.VolumeSeparatorChar
        && path[2] == Path.DirectorySeparatorChar;

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
        // The folder's children as a set, made the first time an entry is
        // found already there: a page of Load more lists again every entry
        // the folder holds, and looking each up in the list itself was one
        // pass over tens of thousands of children per entry.
        HashSet<ViewAllNodeViewModel>? held = null;
        foreach (var entry in snapshot.Entries)
        {
            if (_nodesByPath.TryGetValue(entry.FullPath, out var existing))
            {
                if (existing.Parent == parent)
                {
                    // Listed by its folder now: one of the folder's own, which
                    // a refresh brings back by listing it.
                    _namedOnly.Remove(existing);
                    held ??= new HashSet<ViewAllNodeViewModel>(parent.Children, ReferenceEqualityComparer.Instance);
                    if (held.Add(existing))
                    {
                        parent.Children.Add(existing);
                    }
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
            _namedOnly.Remove(node);
            node.Children.Clear();
        }

        // One compacting pass instead of a removal scan per node.
        _nodes.RemoveAll(doomedSet.Contains);
        _edges.RemoveAll(edge => doomedSet.Contains(edge.Target) || doomedSet.Contains(edge.Source));
    }

    /// <summary>
    /// Cancels a load in flight.  The source is not disposed here: the load
    /// that owns it is still awaiting the disk, and disposes it itself when it
    /// comes back (see <see cref="ExpandAsync"/>).
    /// </summary>
    private void CancelLoad(ViewAllNodeViewModel node)
    {
        if (_loads.Remove(node.Id, out var cancellation))
        {
            cancellation.Cancel();
            node.IsLoading = false;
        }
    }

    private void ClearGraph()
    {
        // Cancelled but not disposed, as in CancelLoad: each load disposes its own.
        foreach (var cancellation in _loads.Values)
        {
            cancellation.Cancel();
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
        _namedOnly.Clear();
        _staleRoots.Clear();
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
