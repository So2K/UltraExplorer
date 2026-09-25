using System.IO.Enumeration;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

/// <summary>What one read of one directory found.</summary>
public sealed record NestedListing(
    IReadOnlyList<NestedEntry> Folders,
    int FileCount,
    int HiddenFileCount,
    bool IsTruncated,
    string ErrorMessage = "")
{
    /// <summary>The files by name, up to <see cref="NestedTree.MaximumFiles"/>; the counts cover all of them.</summary>
    public IReadOnlyList<NestedFile> Files { get; init; } = [];

    /// <summary>The failure may pass - a network or device error rather than access denied.</summary>
    public bool IsRetryable { get; init; }

    public static NestedListing Failed(string message) => new([], 0, 0, false, message);
}

public readonly record struct NestedEntry(string Name, bool IsHidden, bool IsReparsePoint);

/// <summary>A drive or extra root, as the canvas should name it.</summary>
public sealed record NestedRoot(string FullPath, string Name, NestedFolderKind Kind, string SecondaryText = "");

/// <summary>
/// The folder tree behind the nested canvas.
///
/// Nothing is read up front.  The canvas asks for a folder the first time it is
/// drawn large enough for its contents to matter, and the reads that are still
/// wanted run a few at a time in the background, biggest on screen first.  A
/// request that stops being wanted - the view moved on before its turn came -
/// is dropped rather than read.  Because a folder's place depends only on its
/// parent's listing, this is invisible from the outside: every folder is
/// always "on the canvas", some are just too small to have been looked at yet.
///
/// The tree belongs to the thread that created it.  Only the directory reads
/// leave it; their results come back through the synchronisation context and
/// are applied there.
/// </summary>
public sealed class NestedTree : IDisposable
{
    /// <summary>Sub-folders held for one folder.  WinSxS has tens of thousands; this holds all of them.</summary>
    public const int MaximumChildren = 100_000;

    /// <summary>Files listed by name for one folder; past this they are only counted.</summary>
    public const int MaximumFiles = 50_000;

    /// <summary>Reads in flight at once.  More than this only makes a spinning disk seek.</summary>
    public const int MaximumConcurrentReads = 3;

    private readonly HashSet<string> _userHidden = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _forcedVisible = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Folders that have at least one child in either set above.  Only their
    /// children need looking up at all; every other folder's children are
    /// filtered by their attributes alone, without hashing a single path.
    /// </summary>
    private readonly HashSet<string> _filterParents = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<NestedFolder> _queue = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly Func<string, CancellationToken, NestedListing> _reader;
    private int _running;
    private int _networkRunning;
    private bool _disposed;
    private int _knownCount;
    private bool _includeHidden;

    public NestedTree(Func<string, CancellationToken, NestedListing>? reader = null)
    {
        _reader = reader ?? NestedDirectoryReader.Read;

        // Taken once: a disposed source throws on every later read of its token.
        _lifetimeToken = _lifetime.Token;
        Root = new NestedFolder(string.Empty, "This PC", NestedFolderKind.Computer, null)
        {
            LoadState = NestedLoadState.Loaded
        };
    }

    /// <summary>The cell every drive is inside.</summary>
    public NestedFolder Root { get; }

    /// <summary>
    /// Advanced by the canvas once per picture.  A queued read whose folder was
    /// not asked for again in the last two pictures is no longer on screen.
    /// </summary>
    public long Frame { get; private set; }

    /// <summary>Folders known to the tree so far: every one that has been listed by its parent.</summary>
    public int LoadedCount => _knownCount;

    /// <summary>Reads waiting or running.</summary>
    public int PendingCount => _queue.Count + _running;

    /// <summary>
    /// Bumped on every change to the shape of the tree.  The canvas compares it
    /// with the one its last picture was made from to know whether anything it
    /// drew could have moved.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>Raised on the owning thread whenever the shape of the tree changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when a folder's listing was applied, including a refresh.</summary>
    public event Action<NestedFolder>? FolderLoaded;

    public bool IncludeHidden
    {
        get => _includeHidden;
        set
        {
            if (_includeHidden == value)
            {
                return;
            }

            _includeHidden = value;
            RefilterAll();
        }
    }

    /// <summary>
    /// Whether drawing a folder may start reading it.  Off only where the tree
    /// is filled explicitly - a test rendering a canvas on a thread with no
    /// dispatcher to bring the results back to.
    /// </summary>
    public bool IsReadingOnDemand { get; set; } = true;

    public void BeginFrame() => Frame++;

    /// <summary>Replaces the drives and extra roots.  Folders already read under a surviving root are kept.</summary>
    public void SetRoots(IReadOnlyList<NestedRoot> roots)
    {
        var existing = Root.AllChildren.ToDictionary(child => child.FullPath, StringComparer.OrdinalIgnoreCase);
        var children = new List<NestedFolder>(roots.Count);
        foreach (var root in roots)
        {
            if (existing.Remove(root.FullPath, out var kept) && kept.Name == root.Name && kept.Kind == root.Kind)
            {
                kept.SecondaryText = root.SecondaryText;
                children.Add(kept);
                continue;
            }

            if (kept is not null)
            {
                Forget(kept);
            }

            children.Add(new NestedFolder(root.FullPath, root.Name, root.Kind, Root, secondaryText: root.SecondaryText));
            _knownCount++;
        }

        foreach (var removed in existing.Values)
        {
            Forget(removed);
        }

        Root.AllChildren = [.. children];
        ApplyVisibleChildren(Root);
        RaiseChanged();
    }

    /// <summary>
    /// The folder with this path, if it has been read into the tree.  Found by
    /// walking down from its drive, one name per level, each a binary search
    /// in its parent's sorted listing - so reading a folder of fifty thousand
    /// sub-folders never has to register fifty thousand paths anywhere.
    /// </summary>
    public NestedFolder? Find(string path)
    {
        var found = Walk(path, nearest: false);
        return found;
    }

    /// <summary>
    /// The deepest folder already in the tree on the way to <paramref name="path"/>:
    /// the folder itself once everything above it has been read, otherwise the
    /// ancestor the reading has got to.
    /// </summary>
    public NestedFolder? FindNearest(string path) => Walk(path, nearest: true);

    private NestedFolder? Walk(string path, bool nearest)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var target = Key(path);
        var current = OwnerRoot(target);
        if (current is null)
        {
            return null;
        }

        if (target.Length > current.FullPath.Length)
        {
            foreach (var segment in target[current.FullPath.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var next = FindChild(current, segment);
                if (next is null)
                {
                    if (!nearest)
                    {
                        return null;
                    }

                    break;
                }

                current = next;
            }
        }

        if (nearest)
        {
            // The deepest one still among the cells.
            while (current.Parent is not null && !IsOnCanvas(current))
            {
                current = current.Parent;
            }

            return current.IsComputer ? null : current;
        }

        return current;
    }

    /// <summary>
    /// A child of <paramref name="folder"/> by name.  Listings are sorted by the
    /// reader in culture order, so this is a binary search; a name culture order
    /// treats oddly falls back to a plain scan rather than being reported missing.
    /// </summary>
    internal static NestedFolder? FindChild(NestedFolder folder, string name)
    {
        var children = folder.AllChildren;
        if (!folder.IsComputer && children.Length >= 16)
        {
            var low = 0;
            var high = children.Length - 1;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                var order = StringComparer.CurrentCultureIgnoreCase.Compare(children[middle].Name, name);
                if (order == 0)
                {
                    if (string.Equals(children[middle].Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return children[middle];
                    }

                    break;
                }

                if (order < 0)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }
        }

        foreach (var child in children)
        {
            if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase)
                || folder.IsComputer && string.Equals(child.FullPath, name, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>Whether the folder is still one of the cells, rather than filtered out or forgotten.</summary>
    public static bool IsOnCanvas(NestedFolder folder)
    {
        for (var current = folder; current.Parent is not null; current = current.Parent)
        {
            if (current.Index < 0 || current.IsForgotten)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the folder, or any folder above it, was dropped by a refresh.
    /// Forgetting marks only the folder that went; what was under it is cut
    /// off with it, and this is how that is noticed.
    /// </summary>
    public static bool IsDetached(NestedFolder folder)
    {
        for (var current = folder; current is not null; current = current.Parent)
        {
            if (current.IsForgotten)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Asks for a folder to be read because the canvas is drawing it.  Cheap to
    /// call every frame for every folder on screen: a folder already read,
    /// reading or queued only has its priority refreshed.
    /// </summary>
    public void Request(NestedFolder folder, double priority)
    {
        if (!folder.CanLoad || !IsReadingOnDemand)
        {
            return;
        }

        if (_disposed)
        {
            return;
        }

        folder.RequestedFrame = Frame;
        folder.Priority = priority;
        if (folder.LoadState == NestedLoadState.Failed && folder.IsRetryable
            && System.Diagnostics.Stopwatch.GetElapsedTime(folder.FailedAt).TotalSeconds > 20)
        {
            // A share that did not answer, a drive that was not ready: worth
            // another try once in a while.  Access denied is not.
            folder.LoadState = NestedLoadState.NotLoaded;
        }

        if (folder.LoadState != NestedLoadState.NotLoaded)
        {
            return;
        }

        folder.LoadState = NestedLoadState.Queued;
        _queue.Add(folder);
        Pump();
    }

    /// <summary>
    /// Reads every folder on the way to <paramref name="path"/> and returns the
    /// folder itself, or the deepest one that could be reached.  A folder asked
    /// for by name is shown even if it is hidden: naming it is the request.
    /// </summary>
    public async Task<NestedFolder?> RevealAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var target = Key(path);
        var current = OwnerRoot(target);
        if (current is null)
        {
            return null;
        }

        ForceVisible([target]);
        if (target.Length > current.FullPath.Length)
        {
            var refreshed = false;
            foreach (var segment in target[current.FullPath.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await LoadAsync(current, cancellationToken);
                var next = FindChild(current, segment);
                if (next is null && !refreshed && current.IsLoaded)
                {
                    // Asked for a folder the listing does not have: it may
                    // have been made since the parent was read.  Read it once
                    // more - but only for a folder that really is there, or
                    // every file mark would re-read its folder on every look.
                    var childPath = Path.Combine(current.FullPath, segment);
                    if (await Task.Run(() => Directory.Exists(childPath), cancellationToken))
                    {
                        refreshed = true;
                        await RefreshAsync(current, cancellationToken);
                        next = FindChild(current, segment);
                    }
                }

                if (next is null || !IsOnCanvas(next))
                {
                    break;
                }

                current = next;
            }
        }

        return current;
    }

    /// <summary>Reads one folder now, unless it already has been.</summary>
    public async Task LoadAsync(NestedFolder folder, CancellationToken cancellationToken = default)
    {
        // Asked for by name, a junction is read like any folder: it is only
        // the canvas wandering into one on its own that is refused.
        if (folder.IsComputer || IsDetached(folder) || _disposed)
        {
            return;
        }

        if (!await WaitForReadAsync(folder, cancellationToken))
        {
            return;
        }

        if (folder.LoadState is NestedLoadState.Loaded or NestedLoadState.Failed)
        {
            return;
        }

        _queue.Remove(folder);
        folder.LoadState = NestedLoadState.Loading;
        try
        {
            var read = await ReadAsync(folder, cancellationToken);
            Apply(folder, read);
        }
        catch
        {
            if (folder.LoadState == NestedLoadState.Loading)
            {
                folder.LoadState = NestedLoadState.NotLoaded;
            }

            throw;
        }
    }

    /// <summary>
    /// Waits out a read of the same folder already in flight; its result is
    /// ours too.  False when the folder was dropped meanwhile or the tree is
    /// going away - then there is nothing to wait for and nothing to read.
    /// </summary>
    private async Task<bool> WaitForReadAsync(NestedFolder folder, CancellationToken cancellationToken)
    {
        while (folder.LoadState is NestedLoadState.Loading)
        {
            if (IsDetached(folder) || _disposed)
            {
                return false;
            }

            await Task.Delay(10, cancellationToken);
        }

        return !IsDetached(folder) && !_disposed;
    }

    /// <summary>
    /// Reads a folder again and keeps every child that is still there - with
    /// everything already read below it - so a refresh moves nothing that did
    /// not change.
    /// </summary>
    public async Task RefreshAsync(NestedFolder folder, CancellationToken cancellationToken = default)
    {
        if (folder.IsComputer || IsDetached(folder) || _disposed || folder.LoadState is NestedLoadState.NotLoaded or NestedLoadState.Queued)
        {
            return;
        }

        if (!await WaitForReadAsync(folder, cancellationToken))
        {
            return;
        }

        var previous = folder.LoadState;
        folder.LoadState = NestedLoadState.Loading;
        try
        {
            var read = await ReadAsync(folder, cancellationToken);
            Apply(folder, read);
        }
        catch
        {
            if (folder.LoadState == NestedLoadState.Loading)
            {
                folder.LoadState = previous;
            }

            throw;
        }
    }

    /// <summary>Forgets what was read below <paramref name="folder"/> so the canvas reads it afresh.</summary>
    public void Invalidate(NestedFolder folder)
    {
        foreach (var child in folder.AllChildren)
        {
            Forget(child);
        }

        folder.AllChildren = [];
        folder.Children = [];
        folder.Grid = NestedGrid.Empty;
        folder.AllFiles = [];
        folder.Files = [];
        folder.FileGrid = NestedFileGrid.Empty;
        folder.LoadState = folder.IsComputer ? NestedLoadState.Loaded : NestedLoadState.NotLoaded;
        folder.ErrorMessage = string.Empty;
        RaiseChanged();
    }

    /// <summary>Folders the user hid from the canvas, with everything under them.</summary>
    public void SetUserHidden(IEnumerable<string> paths)
    {
        _userHidden.Clear();
        foreach (var path in paths)
        {
            _userHidden.Add(Key(path));
        }

        RebuildFilterParents();
        RefilterAll();
    }

    public bool IsUserHidden(string path) => _userHidden.Contains(Key(path));

    /// <summary>
    /// Keeps every folder on the way to these paths on the canvas even when it
    /// is hidden - a marked folder inside AppData still needs a place to be.
    /// </summary>
    public void ForceVisible(IEnumerable<string> paths)
    {
        // Only a folder already read, one of whose children was filtered out
        // and now is not, needs placing again.  A step not read yet needs
        // nothing: its parent consults these sets when it is read.
        var parents = new HashSet<NestedFolder>();
        foreach (var path in paths)
        {
            foreach (var step in Chain(path))
            {
                if (!_forcedVisible.Add(step))
                {
                    continue;
                }

                if (Path.GetDirectoryName(step) is not { Length: > 0 } directory)
                {
                    continue;
                }

                _filterParents.Add(Key(directory));
                if (Find(directory) is not { IsLoaded: true } holder)
                {
                    continue;
                }

                var name = Path.GetFileName(step);
                var child = FindChild(holder, name);
                var isFilteredFolder = child is { Index: < 0 };
                var mayBeHiddenFile = child is null && !_includeHidden && holder.HiddenFileCount > 0;
                if (isFilteredFolder || mayBeHiddenFile)
                {
                    parents.Add(holder);
                }
            }
        }

        foreach (var parent in parents)
        {
            ApplyVisibleChildren(parent);
        }

        if (parents.Count > 0)
        {
            RaiseChanged();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Cancelled but not disposed: reads still in flight observe the token
        // after this, and a disposed source would throw at them instead.
        _lifetime.Cancel();
    }

    private void RaiseChanged()
    {
        Version++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- reading -----------------------------------------------------------

    private void Pump()
    {
        while (_running < MaximumConcurrentReads && _queue.Count > 0)
        {
            NestedFolder? best = null;
            for (var index = _queue.Count - 1; index >= 0; index--)
            {
                var candidate = _queue[index];
                if (candidate.LoadState != NestedLoadState.Queued)
                {
                    _queue.RemoveAt(index);
                    continue;
                }

                // Two pictures without being asked for: it has left the screen.
                // Cut off by a refresh: there is no screen for it to be on.
                if (!candidate.IsSticky && candidate.RequestedFrame < Frame - 2 || IsDetached(candidate) || _disposed)
                {
                    candidate.LoadState = NestedLoadState.NotLoaded;
                    _queue.RemoveAt(index);
                    continue;
                }

                // A share that hangs holds its read for as long as the network
                // takes to give up; one at a time keeps the local drives moving.
                if (candidate.IsNetwork && _networkRunning > 0)
                {
                    continue;
                }

                if (best is null
                    || candidate.IsSticky && !best.IsSticky
                    || candidate.IsSticky == best.IsSticky && candidate.Priority > best.Priority)
                {
                    best = candidate;
                }
            }

            if (best is null)
            {
                return;
            }

            _queue.Remove(best);
            best.LoadState = NestedLoadState.Loading;
            _running++;
            if (best.IsNetwork)
            {
                _networkRunning++;
            }

            _ = RunQueuedAsync(best);
        }
    }

    private async Task RunQueuedAsync(NestedFolder folder)
    {
        try
        {
            var read = await ReadAsync(folder, _lifetimeToken);
            Apply(folder, read);
        }
        catch (OperationCanceledException)
        {
            folder.LoadState = NestedLoadState.NotLoaded;
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the folder must not stay "reading" forever.
            folder.LoadState = NestedLoadState.Failed;
            folder.ErrorMessage = ex.Message;
            folder.FailedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            folder.IsRetryable = true;
        }
        finally
        {
            _running--;
            if (folder.IsNetwork)
            {
                _networkRunning--;
            }

            if (!_disposed)
            {
                Pump();
            }
        }
    }

    /// <summary>What a read brings back: the listing, and the child folders already built from it.</summary>
    private sealed record Read(NestedListing Listing, NestedFolder[] Children, NestedFolder[] Removed, NestedFolder[] Basis);

    /// <summary>
    /// Reads a folder and, still off the UI thread, builds its child objects:
    /// a folder of twenty-five thousand sub-folders is twenty-five thousand
    /// paths to join and objects to make, and doing that in the dispatcher was
    /// a visible hitch in the middle of a zoom.  Children that were there
    /// before are carried over as they are, with everything read below them.
    /// </summary>
    private async Task<Read> ReadAsync(NestedFolder folder, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        var token = linked.Token;
        var basis = folder.AllChildren;
        var path = folder.FullPath;
        return await Task.Run(() =>
        {
            var listing = _reader(path, token);
            return string.IsNullOrEmpty(listing.ErrorMessage)
                ? Prepare(folder, basis, listing)
                : new Read(listing, basis, [], basis);
        }, token).ConfigureAwait(true);
    }

    private static Read Prepare(NestedFolder folder, NestedFolder[] basis, NestedListing listing)
    {
        Dictionary<string, NestedFolder>? existing = null;
        if (basis.Length > 0)
        {
            existing = new Dictionary<string, NestedFolder>(basis.Length, StringComparer.Ordinal);
            foreach (var child in basis)
            {
                existing[child.Name] = child;
            }
        }

        var children = new NestedFolder[listing.Folders.Count];
        List<NestedFolder>? replaced = null;
        for (var index = 0; index < children.Length; index++)
        {
            var entry = listing.Folders[index];
            if (existing is not null && existing.Remove(entry.Name, out var kept))
            {
                if (kept.IsReparsePoint == entry.IsReparsePoint && kept.IsHidden == entry.IsHidden)
                {
                    children[index] = kept;
                    continue;
                }

                // Became a link, or hidden: a different cell now, and the old
                // one - with what was read below it - has to go.
                replaced ??= [];
                replaced.Add(kept);
            }

            children[index] = new NestedFolder(
                Path.Combine(folder.FullPath, entry.Name),
                entry.Name,
                NestedFolderKind.Folder,
                folder,
                entry.IsHidden,
                entry.IsReparsePoint);
        }

        NestedFolder[] removed = existing is null ? [] : [.. existing.Values, .. replaced ?? []];
        return new Read(listing, children, removed, basis);
    }

    private void Apply(NestedFolder folder, Read read)
    {
        folder.IsSticky = false;

        // Its parent was read again while this was in flight and it is gone:
        // nothing may be hung on a folder nobody can reach.
        if (IsDetached(folder))
        {
            // Nothing reads it now; if it is ever reached again, read afresh.
            folder.LoadState = NestedLoadState.NotLoaded;
            return;
        }

        var listing = read.Listing;
        if (!string.IsNullOrEmpty(listing.ErrorMessage))
        {
            folder.LoadState = NestedLoadState.Failed;
            folder.ErrorMessage = listing.ErrorMessage;
            folder.FailedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            folder.IsRetryable = listing.IsRetryable;
            RaiseChanged();
            return;
        }

        // The listing was matched against the children as they were when the
        // read started.  If something replaced them meanwhile, match again
        // against what is there now; it is rare, and correctness beats speed.
        if (!ReferenceEquals(read.Basis, folder.AllChildren))
        {
            read = Prepare(folder, folder.AllChildren, listing);
        }

        foreach (var removed in read.Removed)
        {
            Forget(removed);
        }

        _knownCount += read.Children.Length - read.Basis.Length;
        folder.AllChildren = read.Children;
        folder.AllFiles = listing.Files as NestedFile[] ?? [.. listing.Files];
        folder.FileCount = listing.FileCount;
        folder.HiddenFileCount = listing.HiddenFileCount;
        folder.IsTruncated = listing.IsTruncated;
        folder.ErrorMessage = string.Empty;
        folder.LoadState = NestedLoadState.Loaded;
        ApplyVisibleChildren(folder);

        FolderLoaded?.Invoke(folder);
        RaiseChanged();
    }

    /// <summary>
    /// Drops a folder that is no longer there.  Only the folder itself is
    /// marked: everything below it is unreachable through it anyway, and
    /// walking a subtree of thousands to say so again was the slow part of a
    /// refresh.  <see cref="IsDetached"/> is how the rest find out.
    /// </summary>
    private void Forget(NestedFolder folder)
    {
        folder.Index = -1;
        folder.IsForgotten = true;
        if (folder.LoadState == NestedLoadState.Queued)
        {
            folder.LoadState = NestedLoadState.NotLoaded;
        }
    }

    // ---- filtering and placing ---------------------------------------------

    private void RefilterAll()
    {
        var stack = new Stack<NestedFolder>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            var folder = stack.Pop();

            // Whatever its state - being read again, or failed on a re-read -
            // a folder that has contents gets the rules applied to them.
            if (!folder.IsComputer && folder.AllChildren.Length == 0 && folder.AllFiles.Length == 0)
            {
                continue;
            }

            ApplyVisibleChildren(folder);
            foreach (var child in folder.AllChildren)
            {
                stack.Push(child);
            }
        }

        RaiseChanged();
    }

    private void RebuildFilterParents()
    {
        _filterParents.Clear();
        foreach (var path in _userHidden.Concat(_forcedVisible))
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            {
                _filterParents.Add(Key(directory));
            }
        }
    }

    /// <summary>Decides which children are cells and where each one goes.</summary>
    private void ApplyVisibleChildren(NestedFolder folder)
    {
        // Only folders with a hidden or forced child look paths up; the rest
        // decide by attribute alone, which is what keeps placing a folder of
        // fifty thousand sub-folders cheap.
        var hasRules = !folder.IsComputer && _filterParents.Contains(folder.FullPath);
        var all = folder.AllChildren;
        var visible = new List<NestedFolder>(all.Length);
        foreach (var child in all)
        {
            // "Hide from canvas" is the user's own word and outranks a folder
            // being asked for by name; being asked for outranks only the
            // hidden attribute.
            bool shown;
            if (hasRules && _userHidden.Contains(child.FullPath))
            {
                shown = false;
            }
            else if (hasRules && _forcedVisible.Contains(child.FullPath))
            {
                shown = true;
            }
            else
            {
                shown = _includeHidden || !child.IsHidden;
            }

            if (shown)
            {
                visible.Add(child);
            }
            else
            {
                child.Index = -1;
            }
        }

        // A hidden file someone marked or searched for is shown like a hidden
        // folder on the way to one: asking for it by name outranks the filter.
        NestedFile[] files;
        if (_includeHidden || folder.HiddenFileCount == 0)
        {
            files = folder.AllFiles;
        }
        else
        {
            files = [.. folder.AllFiles.Where(file => !file.IsHidden || hasRules && _forcedVisible.Contains(folder.PathOf(file)))];
        }

        // Sub-folders take the top of the cell and files what is left under
        // them: the folder grid is fitted into its share first, then the files
        // get every bit of height the folders did not actually use.
        var (folderHeight, _) = NestedLayout.Split(visible.Count, files.Length);
        var grid = NestedLayout.GridFor(visible.Count, folderHeight);
        for (var index = 0; index < visible.Count; index++)
        {
            var child = visible[index];
            var (x, y) = grid.Origin(index);
            child.Index = index;
            child.OffsetX = x;
            child.OffsetY = y;
            child.Scale = grid.Scale;
        }

        var used = grid.IsEmpty ? 0 : grid.Height + NestedLayout.ZoneGap;
        folder.Grid = grid;
        folder.Children = visible;
        folder.Files = files;
        folder.FileGrid = NestedLayout.FileGridFor(
            files.Length,
            NestedLayout.HeaderHeight + used,
            NestedLayout.ContentHeight - used);
        folder.UnlistedFileCount = Math.Max(0, folder.FileCount - folder.AllFiles.Length);
    }

    // ---- paths ---------------------------------------------------------------

    /// <summary>The root - a drive or a deeper extra root such as a share - that holds a normalised path.</summary>
    private NestedFolder? OwnerRoot(string target)
    {
        NestedFolder? owner = null;
        foreach (var root in Root.AllChildren)
        {
            if (IsSameOrInside(target, root.FullPath)
                && (owner is null || root.FullPath.Length > owner.FullPath.Length))
            {
                owner = root;
            }
        }

        return owner;
    }

    /// <summary>
    /// The root-first chain of paths from the root that holds <paramref name="path"/>
    /// down to it.  A root can be a drive or a deeper extra root such as a UNC
    /// share, so the chain starts at the longest root that is a prefix.
    /// </summary>
    internal List<string> Chain(string path)
    {
        var target = Key(path);
        var owner = OwnerRoot(target);
        var chain = new List<string>();
        if (owner is null)
        {
            return chain;
        }

        chain.Add(owner.FullPath);
        if (target.Length <= owner.FullPath.Length)
        {
            return chain;
        }

        var start = owner.FullPath.Length;
        var remainder = target[start..].TrimStart(Path.DirectorySeparatorChar);
        var current = owner.FullPath;
        foreach (var segment in remainder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            chain.Add(current);
        }

        return chain;
    }

    private static bool IsSameOrInside(string path, string root)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return root.EndsWith(Path.DirectorySeparatorChar) || path[root.Length] == Path.DirectorySeparatorChar;
    }

    private static string Key(string path)
    {
        try
        {
            return ViewAllPath.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}

/// <summary>
/// Reads one directory in a single pass over its raw entries: the sub-folders,
/// and the files - named up to <see cref="NestedTree.MaximumFiles"/>, counted
/// past it without a string ever being made for them.
/// </summary>
public static class NestedDirectoryReader
{
    private sealed class Counts
    {
        public int Files;
        public int HiddenFiles;
    }

    public static NestedListing Read(string path, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };

        var folders = new List<NestedEntry>();
        var listed = new List<NestedFile>();
        var counts = new Counts();
        var truncated = false;

        try
        {
            var entries = new FileSystemEnumerable<(string Name, FileAttributes Attributes, long Length, bool IsDirectory, bool IsLink)>(
                ExtendedLength(path),
                static (ref FileSystemEntry entry) => (
                    entry.FileName.ToString(),
                    entry.Attributes,
                    entry.IsDirectory ? 0 : entry.Length,
                    entry.IsDirectory,
                    entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) != 0 && IsLink(ref entry)),
                options)
            {
                // Files are counted here, before the transform, so the ones
                // past the cap are never turned into anything.
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    if (entry.IsDirectory)
                    {
                        return true;
                    }

                    counts.Files++;
                    if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    {
                        counts.HiddenFiles++;
                    }

                    return counts.Files <= NestedTree.MaximumFiles;
                }
            };

            foreach (var (name, attributes, length, isDirectory, isLink) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isHidden = (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
                if (!isDirectory)
                {
                    listed.Add(new NestedFile(name, isHidden, length));
                    continue;
                }

                if (folders.Count >= NestedTree.MaximumChildren)
                {
                    truncated = true;
                    continue;
                }

                folders.Add(new NestedEntry(name, isHidden, isLink));
            }
        }
        catch (UnauthorizedAccessException)
        {
            return NestedListing.Failed("Access denied");
        }
        catch (DirectoryNotFoundException)
        {
            return NestedListing.Failed("No longer exists");
        }
        catch (IOException ex)
        {
            // A share that did not answer or a drive that was not ready may
            // well answer later; the canvas tries such a folder again.
            return NestedListing.Failed(ex.Message) with { IsRetryable = true };
        }

        // Culture order as Explorer shows it, and ordinal order between names
        // culture order calls equal, so two folders differing only in case (a
        // WSL tree can have them) always come out the same way round.
        folders.Sort(static (left, right) =>
        {
            var order = StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
            return order != 0 ? order : string.CompareOrdinal(left.Name, right.Name);
        });
        listed.Sort(static (left, right) =>
        {
            var order = StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
            return order != 0 ? order : string.CompareOrdinal(left.Name, right.Name);
        });
        return new NestedListing(folders, counts.Files, counts.HiddenFiles, truncated) { Files = listed.ToArray() };
    }

    /// <summary>
    /// Whether a folder with a reparse point is a link - a junction, a symbolic
    /// link, a mount point - rather than a cloud placeholder (OneDrive files on
    /// demand) or a projected folder, which carry the same attribute but are
    /// ordinary folders to read.  Only links report a target.
    /// </summary>
    private static bool IsLink(ref FileSystemEntry entry)
    {
        try
        {
            return entry.ToFileSystemInfo().LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// The path in its extended-length form for a local drive.  Without it the
    /// enumerator normalises the path, which strips a trailing dot or space -
    /// and a folder named "backup." would be read as its neighbour "backup".
    /// </summary>
    private static string ExtendedLength(string path) =>
        path.Length >= 3 && path[1] == ':' && path[2] == Path.DirectorySeparatorChar
            ? @"\\?\" + path
            : path;
}
