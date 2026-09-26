using System.Buffers;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Windows.Threading;
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

/// <summary>A sub-folder as a listing names it.</summary>
/// <param name="ModifiedTicks">When it was last written, as UTC ticks; zero when the reader did not say.</param>
public readonly record struct NestedEntry(string Name, bool IsHidden, bool IsReparsePoint, long ModifiedTicks = 0);

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
///
/// Every folder's sub-folders and files are placed in one order, <see cref="Sort"/>,
/// row by row.  Changing it re-places nothing at once - a tree read deep can
/// be tens of thousands of folders, and doing them all in one go would stall
/// the window.  Instead each folder remembers which order it was placed for,
/// anything about to be drawn or looked at is placed again first
/// (<see cref="EnsureLayout"/>), and a pass in small background slices brings
/// the rest up to date behind it.  The listings themselves stay in name order
/// for ever, which is what keeps finding a folder or a file by name a binary
/// search whatever the canvas shows.
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

    /// <summary>
    /// Budget for one background slice of re-placing folders after a change of
    /// order: well inside a frame, so the canvas keeps drawing while it runs.
    /// Shared with whatever was placed on demand since the slice before (see
    /// <see cref="_placedOnDemandTicks"/>), so a frame and its slice together
    /// stay within it.
    /// </summary>
    private static readonly long SortSliceTicks = Stopwatch.Frequency * 4 / 1000;

    /// <summary>
    /// Less than this left of a slice's allowance and the slice waits for the
    /// next frame instead: a sliver of time places next to nothing, and the
    /// check it takes to find that out is not free either.
    /// </summary>
    private static readonly long MinimumSliceTicks = Stopwatch.Frequency / 2000;

    private ItemSort _sort = ItemSort.Default;

    /// <summary>
    /// The order each <see cref="SortGeneration"/> stood for, by generation.
    /// A folder placed for an order that has since come back - names from A,
    /// after a look at the dates - needs only its stamp brought up to date,
    /// not placing again.
    /// </summary>
    private readonly List<ItemSort> _sortHistory = [ItemSort.Default];

    /// <summary>Folders the background pass after a change of order has still to visit, shallowest first.</summary>
    private readonly Queue<NestedFolder> _sortSweep = new();

    /// <summary>
    /// Folders the canvas drew in their previous order because its frame had
    /// no allowance left to place them: the next slice places these before
    /// going on down the tree, so what is on screen catches up first and the
    /// thousands of folders off it wait.
    /// </summary>
    private readonly Queue<NestedFolder> _sortFirst = new();

    private bool _sortSlicePosted;
    private List<TaskCompletionSource>? _sortIdleWaiters;

    /// <summary>
    /// Whether the pass under way has placed again a folder on the canvas.
    /// Said once, when the pass is done, rather than after every slice: the
    /// canvas places for itself whatever it draws, and a slice moving folders
    /// nobody is looking at is no reason to draw the whole picture again.
    /// </summary>
    private bool _sortMovedCanvas;

    /// <summary>
    /// Time spent placing folders on demand - for the canvas's picture, a
    /// click, a key - since the last slice of the background pass.  The next
    /// slice takes it off its own allowance, so a frame that had to place a
    /// lot for the picture is not given a whole slice on top.
    /// </summary>
    private long _placedOnDemandTicks;

    /// <summary>
    /// Whether reads look up their files' type names before they come back,
    /// which is while the order is by type: read by the reading threads.
    /// </summary>
    private volatile bool _warmTypeNames;

    // Scratch for ordering one folder's files by type, reused from folder to
    // folder: placing a tree is tens of thousands of folders, each a handful
    // of kinds, and none of it needs to become garbage.
    private readonly List<string> _typeKinds = [];
    private readonly Dictionary<string, int> _typeSlots = new(ReferenceEqualityComparer.Instance);

    public NestedTree(Func<string, CancellationToken, NestedListing>? reader = null)
    {
        _reader = reader ?? NestedDirectoryReader.Read;

        // Taken once: a disposed source throws on every later read of its token.
        _lifetimeToken = _lifetime.Token;
        Root = new NestedFolder(string.Empty, "This PC", NestedFolderKind.Computer, null)
        {
            LoadState = NestedLoadState.Loaded
        };
        PostBackground = DefaultPostBackground();
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

    /// <summary>
    /// Raised by <see cref="SetSort"/> the moment the order changes, before a
    /// single folder has been placed for it: every folder is still where the
    /// last picture showed it, which is when the canvas has to note what it is
    /// looking at if the view is to stay still while the contents move.
    /// </summary>
    public event Action? SortChanged;

    /// <summary>The order sub-folders and files are placed in, row by row, inside every folder but This PC.</summary>
    public ItemSort Sort => _sort;

    /// <summary>
    /// Bumped by every change of <see cref="Sort"/>.  A folder whose stamp is
    /// this number has been placed for the current order; any other stamp
    /// means it is placed for an earlier one and must be brought up to date,
    /// by <see cref="EnsureLayout"/>, before anything reads where its
    /// children are.
    /// </summary>
    public int SortGeneration { get; private set; }

    /// <summary>Whether the background pass after a change of order still has folders to visit.</summary>
    public bool IsSorting => _sortSweep.Count > 0;

    /// <summary>
    /// How the background pass after a change of order schedules its slices.
    /// By default a slice is queued on the creating thread's dispatcher below
    /// input and rendering, or posted to its synchronisation context when it
    /// has another kind; null runs the whole pass at once, which is what a
    /// thread with neither gets.  A test can put its own queue here to run
    /// the slices when and how it likes.
    /// </summary>
    public Action<Action>? PostBackground { get; set; }

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

        // Nothing left to order: like a folder never read, until it is read again.
        folder.LayoutSortGeneration = -1;
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

    /// <summary>
    /// Orders every folder's sub-folders and files by <paramref name="sort"/>
    /// from now on.  Nothing is placed again here: <see cref="SortChanged"/>
    /// is raised, and a background pass starts that re-places every folder
    /// read so far, shallowest first, a few milliseconds at a time, and raises
    /// <see cref="Changed"/> once at the end if it moved something on the
    /// canvas.  Whatever is drawn or looked at before the pass gets to it is
    /// placed on the spot by <see cref="EnsureLayout"/>.  With no
    /// <see cref="PostBackground"/> - a thread with nothing to post to - the
    /// whole pass runs here, straight after the event.
    /// </summary>
    public void SetSort(ItemSort sort)
    {
        if (sort == _sort)
        {
            return;
        }

        _sort = sort;
        SortGeneration++;
        _sortHistory.Add(sort);
        _warmTypeNames = sort.Column == SortColumn.Type;

        // A pass already under way starts again from the top; a slice it has
        // already queued simply carries on with the new list.  Queued before
        // the event, so whoever handles it already sees the tree sorting.
        // What the last picture asked to have placed first was asked for the
        // order before; the next picture asks again.
        _sortSweep.Clear();
        _sortFirst.Clear();
        _sortMovedCanvas = false;
        _placedOnDemandTicks = 0;
        if (!_disposed)
        {
            _sortSweep.Enqueue(Root);
        }

        SortChanged?.Invoke();
        ScheduleSortSlice();
    }

    /// <summary>
    /// Places the folder's sub-folders and files for the current order if they
    /// are not yet; costs a comparison of two numbers when they are.  Anything
    /// that reads a folder's <see cref="NestedFolder.Children"/>, grids or
    /// <see cref="NestedFolder.Files"/>, or its children's places, calls this
    /// first.  A folder never placed - one not read yet - is left alone: it
    /// has nothing to order.
    /// </summary>
    public void EnsureLayout(NestedFolder folder)
    {
        var stamp = folder.LayoutSortGeneration;
        if (stamp == SortGeneration || stamp < 0)
        {
            return;
        }

        // Timed, because the background pass shares its allowance with
        // whatever had to be placed on demand since its last slice.
        var started = Stopwatch.GetTimestamp();
        if (Relayout(folder))
        {
            _placedOnDemandTicks += Stopwatch.GetTimestamp() - started;
        }
    }

    /// <summary>
    /// Has the next slice of the background pass place these folders before
    /// anything else: the canvas drew them in their previous order, its
    /// frame's allowance for placing spent.  Replaces what the picture before
    /// asked for - whatever of that is still out of date and on screen is in
    /// this list again.
    /// </summary>
    internal void PlaceFirst(List<NestedFolder> folders)
    {
        _sortFirst.Clear();
        if (!IsSorting)
        {
            return;
        }

        foreach (var folder in folders)
        {
            _sortFirst.Enqueue(folder);
        }
    }

    /// <summary>
    /// <see cref="EnsureLayout"/> for every folder from the top down to this
    /// one: what a camera fixed to a folder needs before it works out where
    /// that folder's ancestors, and their other children, are.
    /// </summary>
    public void EnsurePathLayout(NestedFolder folder)
    {
        // Nearly always every folder on the way is up to date already, and
        // this is a walk of number comparisons that allocates nothing.
        var stale = false;
        for (var current = folder; current is not null; current = current.Parent)
        {
            if (current.LayoutSortGeneration >= 0 && current.LayoutSortGeneration != SortGeneration)
            {
                stale = true;
                break;
            }
        }

        if (!stale)
        {
            return;
        }

        var path = new List<NestedFolder>(folder.Depth + 1);
        for (var current = folder; current is not null; current = current.Parent)
        {
            path.Add(current);
        }

        for (var index = path.Count - 1; index >= 0; index--)
        {
            EnsureLayout(path[index]);
        }
    }

    /// <summary>
    /// Finishes the background pass after a change of order here and now, for
    /// a test or a snapshot that wants every folder placed before it looks.
    /// </summary>
    public void FlushSortWork()
    {
        if (_sortSweep.Count == 0)
        {
            return;
        }

        SweepSort(budgetTicks: 0);
        FinishSortPass();
    }

    /// <summary>Completes once the background pass after a change of order has visited every folder.</summary>
    public Task WhenSortIdleAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSorting)
        {
            return Task.CompletedTask;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        (_sortIdleWaiters ??= []).Add(waiter);
        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            _ = waiter.Task.ContinueWith(
                _ => registration.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return waiter.Task;
    }

    /// <summary>
    /// Where one of the folder's files is among its <see cref="NestedFolder.Files"/>,
    /// or -1 when it is not among them - not listed, or hidden.  A binary
    /// search by name either way: over the shown files themselves while they
    /// are in name order, otherwise over every file read, which stays in name
    /// order, and from there to the tile the current order gave it.  A name
    /// culture order treats oddly falls back to a plain scan rather than
    /// being reported missing.
    /// </summary>
    public int FindFileIndex(NestedFolder folder, string name)
    {
        EnsureLayout(folder);
        return FileIndexAsPlaced(folder, name);
    }

    /// <summary>
    /// <see cref="FindFileIndex"/> among the tiles as the folder is placed
    /// right now, whichever order that was for, without placing it first:
    /// for drawing a mark over the tile the picture shows, which is the tile
    /// the folder had when it was drawn.
    /// </summary>
    internal static int FileIndexAsPlaced(NestedFolder folder, string name)
    {
        var positions = folder.FilePositions;
        if (positions is null)
        {
            return SearchFiles(folder.Files, name);
        }

        var all = folder.AllFiles;
        var found = SearchFiles(all, name);
        if (found < 0 || positions[found] >= 0)
        {
            return found < 0 ? -1 : positions[found];
        }

        // The name belongs to a file that is not shown.  Only a folder whose
        // names differ in case alone could also have a shown one it matches,
        // and in name order that one sits right beside it.
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        for (var index = found - 1; index >= 0 && comparer.Compare(all[index].Name, name) == 0; index--)
        {
            if (positions[index] >= 0 && string.Equals(all[index].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return positions[index];
            }
        }

        for (var index = found + 1; index < all.Length && comparer.Compare(all[index].Name, name) == 0; index++)
        {
            if (positions[index] >= 0 && string.Equals(all[index].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return positions[index];
            }
        }

        return -1;
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

        // Nobody will look at the folders again; whoever waits for the order
        // to settle need not wait for ever.
        _sortSweep.Clear();
        _sortFirst.Clear();
        CompleteSortIdle();
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
            if (!string.IsNullOrEmpty(listing.ErrorMessage))
            {
                return new Read(listing, basis, [], basis);
            }

            // Ordered by type, placing the folder asks the Shell for the name
            // of every kind of file in it.  A kind seen for the first time is
            // asked about here, off the UI thread, so placing it there finds
            // every answer waiting.
            if (_warmTypeNames)
            {
                FileTypeNames.Warm(listing.Files);
            }

            return Prepare(folder, basis, listing);
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
                entry.IsReparsePoint,
                modifiedTicks: entry.ModifiedTicks);
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

        // A folder still there keeps its object, with everything read below
        // it, but its date is whatever the new listing says.  Set here on the
        // UI thread, which owns the folders, rather than while preparing.
        var entries = listing.Folders;
        for (var index = 0; index < read.Children.Length; index++)
        {
            read.Children[index].ModifiedTicks = entries[index].ModifiedTicks;
        }

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

    /// <summary>
    /// Decides which children are cells and where each one goes, in the
    /// current <see cref="Sort"/>, and stamps the folder as placed for it.
    /// Under names from A this is exactly what it always was: the listing's
    /// own order, and the listing itself as the files whenever none is hidden.
    /// </summary>
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
        bool IsShown(NestedFile file) => !file.IsHidden || hasRules && _forcedVisible.Contains(folder.PathOf(file));

        // This PC's drives keep the order they were given in; everywhere else
        // the chosen order applies.  Names from A is the order the listings
        // already have, so it asks for nothing at all.
        IReadOnlyList<NestedFolder> children = visible;
        IReadOnlyList<NestedFile> files;
        if (!_sort.IsDefault && !folder.IsComputer)
        {
            // Only which cell and which tile each one gets changes: the grids
            // depend on how many there are, not on which is which.  A folder
            // with no sub-folders shares one empty list: re-placing a tree is
            // mostly such folders, and every object kept is one more for the
            // garbage collector to carry.
            OrderFolders(visible);
            if (visible.Count == 0)
            {
                children = [];
            }

            files = OrderFiles(
                folder.AllFiles,
                _includeHidden || folder.HiddenFileCount == 0 ? null : ShownIndices(folder.AllFiles, IsShown));
        }
        else if (_includeHidden || folder.HiddenFileCount == 0)
        {
            files = folder.AllFiles;
        }
        else
        {
            NestedFile[] shown = [.. folder.AllFiles.Where(IsShown)];
            files = shown;
        }

        // Sub-folders take the top of the cell and files what is left under
        // them: the folder grid is fitted into its share first, then the files
        // get every bit of height the folders did not actually use.
        var (folderHeight, _) = NestedLayout.Split(visible.Count, files.Count);
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
        folder.Children = children;
        folder.Files = files;
        folder.FileGrid = NestedLayout.FileGridFor(
            files.Count,
            NestedLayout.HeaderHeight + used,
            NestedLayout.ContentHeight - used);
        folder.UnlistedFileCount = Math.Max(0, folder.FileCount - folder.AllFiles.Length);
        folder.LayoutSortGeneration = SortGeneration;
    }

    // ---- ordering ------------------------------------------------------------

    /// <summary>
    /// The default poster for the background pass: the creating thread's
    /// dispatcher, below input and rendering, when that thread is running
    /// one; otherwise its synchronisation context; otherwise none, and the
    /// pass runs at once.  A dispatcher only counts when it is the context
    /// too - a thread can have a dispatcher that nobody runs, and a slice
    /// queued there would wait for ever.
    /// </summary>
    private static Action<Action>? DefaultPostBackground()
    {
        var context = SynchronizationContext.Current;
        if (context is DispatcherSynchronizationContext && Dispatcher.FromThread(Thread.CurrentThread) is { } dispatcher)
        {
            return action => dispatcher.InvokeAsync(action, DispatcherPriority.Background);
        }

        if (context is not null)
        {
            return action => context.Post(static state => ((Action)state!).Invoke(), action);
        }

        return null;
    }

    /// <summary>
    /// Places a folder for the current order if it is not yet; true when it
    /// actually had to be placed again, rather than being up to date or only
    /// needing its stamp moved on.
    /// </summary>
    private bool Relayout(NestedFolder folder)
    {
        var stamp = folder.LayoutSortGeneration;
        if (stamp == SortGeneration || stamp < 0)
        {
            return false;
        }

        // Placed for an order that is the current one again, or with too
        // little in it for any order to differ - one sub-folder and one file
        // go where they go - or This PC, whose drives are never reordered:
        // the places are right, only the stamp is old.
        if (_sortHistory[stamp] == _sort
            || folder.IsComputer
            || folder.Children.Count < 2 && folder.Files.Count < 2)
        {
            folder.LayoutSortGeneration = SortGeneration;
            return false;
        }

        ApplyVisibleChildren(folder);
        return true;
    }

    private void ScheduleSortSlice()
    {
        if (_sortSlicePosted || _sortSweep.Count == 0)
        {
            return;
        }

        if (PostBackground is not { } post)
        {
            FlushSortWork();
            return;
        }

        _sortSlicePosted = true;
        post(RunSortSlice);
    }

    /// <summary>
    /// One slice of the background pass: a few milliseconds of folders, less
    /// whatever was placed on demand since the slice before, then the rest
    /// queued again.  A slice with next to nothing left of its allowance
    /// leaves its turn to the next frame.
    /// </summary>
    private void RunSortSlice()
    {
        _sortSlicePosted = false;
        if (_disposed)
        {
            _sortSweep.Clear();
            _sortFirst.Clear();
            CompleteSortIdle();
            return;
        }

        // Flushed meanwhile: nothing left for this slice to do.
        if (_sortSweep.Count == 0)
        {
            return;
        }

        // Taken, not just read: the placing it counts is charged to this
        // slice alone, and the next one starts again from what happens after.
        var budget = SortSliceTicks - _placedOnDemandTicks;
        _placedOnDemandTicks = 0;
        if (budget >= MinimumSliceTicks)
        {
            SweepSort(budget);
        }

        if (_sortSweep.Count > 0)
        {
            ScheduleSortSlice();
        }
        else
        {
            FinishSortPass();
        }
    }

    /// <summary>
    /// The pass has visited every folder: the one announcement it makes, if
    /// it moved anything on the canvas, and whoever waits for it let go.
    /// </summary>
    private void FinishSortPass()
    {
        _sortFirst.Clear();
        if (_sortMovedCanvas)
        {
            _sortMovedCanvas = false;
            RaiseChanged();
        }

        CompleteSortIdle();
    }

    /// <summary>
    /// Visits folders of the background pass until the budget is spent (none:
    /// until there are none left): first those the canvas asked to have placed
    /// first (see <see cref="PlaceFirst"/>), then the tree shallowest first.
    /// Notes whether one it placed again is on the canvas, for the
    /// announcement at the end of the pass.
    /// </summary>
    private void SweepSort(long budgetTicks)
    {
        var started = Stopwatch.GetTimestamp();
        bool Spent() => budgetTicks > 0 && Stopwatch.GetTimestamp() - started >= budgetTicks;

        // Only placed here, not walked from: the pass reaches their children
        // in its own time, and anything of theirs on screen is on the list too.
        while (_sortFirst.TryDequeue(out var first))
        {
            if (!first.IsForgotten && Relayout(first))
            {
                _sortMovedCanvas = true;
            }

            if (Spent())
            {
                return;
            }
        }

        while (_sortSweep.TryDequeue(out var folder))
        {
            // Dropped by a refresh since it was queued: nobody can reach it.
            if (folder.IsForgotten)
            {
                continue;
            }

            if (Relayout(folder) && !_sortMovedCanvas)
            {
                _sortMovedCanvas = IsOnCanvas(folder);
            }

            // Hidden ones too: showing hidden items should not find them in
            // an old order.  A child never placed was never read, and neither
            // was anything below it, so it has nothing to visit.
            foreach (var child in folder.AllChildren)
            {
                if (child.LayoutSortGeneration >= 0)
                {
                    _sortSweep.Enqueue(child);
                }
            }

            if (Spent())
            {
                break;
            }
        }
    }

    private void CompleteSortIdle()
    {
        if (_sortIdleWaiters is not { Count: > 0 } waiters)
        {
            return;
        }

        _sortIdleWaiters = null;
        foreach (var waiter in waiters)
        {
            waiter.TrySetResult();
        }
    }

    /// <summary>
    /// Puts the shown sub-folders in the current order.  Only names from Z
    /// and dates move folders: a folder has no size and every folder has the
    /// same type, so under those two they tie, and ties stay in name order.
    /// </summary>
    private void OrderFolders(List<NestedFolder> visible)
    {
        var count = visible.Count;
        if (count < 2)
        {
            return;
        }

        if (_sort.Column == SortColumn.Name)
        {
            // Only names from Z get here, and they are the listing backwards.
            visible.Reverse();
            return;
        }

        if (_sort.Column != SortColumn.Modified)
        {
            return;
        }

        var keys = ArrayPool<long>.Shared.Rent(count);
        var order = ArrayPool<int>.Shared.Rent(count);
        try
        {
            for (var index = 0; index < count; index++)
            {
                keys[index] = KeyOf(visible[index].ModifiedTicks);
            }

            if (!SortByKeys(keys, order, count))
            {
                return;
            }

            var before = ArrayPool<NestedFolder>.Shared.Rent(count);
            visible.CopyTo(before);
            for (var position = 0; position < count; position++)
            {
                visible[position] = before[order[position]];
            }

            // Cleared on the way back: a pool holding folders would keep a
            // tree that was let go of alive.
            ArrayPool<NestedFolder>.Shared.Return(before, clearArray: true);
        }
        finally
        {
            ArrayPool<long>.Shared.Return(keys);
            ArrayPool<int>.Shared.Return(order);
        }
    }

    /// <summary>The indices among <paramref name="files"/> of the ones shown.</summary>
    private static int[] ShownIndices(NestedFile[] files, Func<NestedFile, bool> isShown)
    {
        var buffer = ArrayPool<int>.Shared.Rent(files.Length);
        var count = 0;
        for (var index = 0; index < files.Length; index++)
        {
            if (isShown(files[index]))
            {
                buffer[count++] = index;
            }
        }

        var shown = buffer.AsSpan(0, count).ToArray();
        ArrayPool<int>.Shared.Return(buffer);
        return shown;
    }

    /// <summary>
    /// The shown files in the current order: the listing itself when every
    /// file is shown and the order comes out as name order anyway - every
    /// file the same type, or the same date - and otherwise the listing with
    /// the order to walk it in.  An index per file rather than a copy of every
    /// file: re-placing a whole tree makes one of these per folder, and the
    /// garbage collector has a tenth as much to carry.
    /// </summary>
    /// <param name="all">Every file the folder read, in name order.</param>
    /// <param name="shownFrom">The indices of the shown ones, in name order; null when all of them are.</param>
    private IReadOnlyList<NestedFile> OrderFiles(NestedFile[] all, int[]? shownFrom)
    {
        var count = shownFrom?.Length ?? all.Length;
        int[]? order = null;
        if (count >= 2)
        {
            switch (_sort.Column)
            {
                case SortColumn.Name:
                    // Only names from Z get here, and they are the listing backwards.
                    order = new int[count];
                    for (var position = 0; position < count; position++)
                    {
                        order[position] = count - 1 - position;
                    }

                    break;

                case SortColumn.Modified:
                case SortColumn.Size:
                    var byDate = _sort.Column == SortColumn.Modified;
                    var keys = ArrayPool<long>.Shared.Rent(count);
                    order = new int[count];
                    for (var index = 0; index < count; index++)
                    {
                        var file = all[shownFrom is null ? index : shownFrom[index]];
                        keys[index] = KeyOf(byDate ? file.ModifiedTicks : file.Length);
                    }

                    if (!SortByKeys(keys, order, count))
                    {
                        order = null;
                    }

                    ArrayPool<long>.Shared.Return(keys);
                    break;

                default:
                    order = TypeOrder(all, shownFrom, count);
                    break;
            }
        }

        if (order is null)
        {
            // Name order after all: the listing, or the part of it shown.
            return shownFrom is null ? all : new NestedFileOrder(all, shownFrom, isNameOrder: true);
        }

        if (shownFrom is not null)
        {
            // From places among the shown files to places in the listing.
            for (var position = 0; position < count; position++)
            {
                order[position] = shownFrom[order[position]];
            }
        }

        return new NestedFileOrder(all, order, isNameOrder: false);
    }

    /// <summary>A key that sorts the current way round: largest first by flipping every bit, which cannot overflow.</summary>
    private long KeyOf(long value) => _sort.Descending ? ~value : value;

    /// <summary>
    /// Fills <paramref name="order"/> with the first <paramref name="count"/>
    /// indices smallest key first, equal keys in index order, which is name
    /// order; false when that is the order they were in already.  The keys
    /// are sorted in place.
    /// </summary>
    private static bool SortByKeys(long[] keys, int[] order, int count)
    {
        for (var index = 0; index < count; index++)
        {
            order[index] = index;
        }

        // Sorting plain numbers with their indices riding along never calls a
        // comparer; that sort does not keep ties in order, so each run of
        // equal keys has its indices put back in order afterwards.
        Array.Sort(keys, order, 0, count);
        var start = 0;
        while (start < count)
        {
            var end = start + 1;
            while (end < count && keys[end] == keys[start])
            {
                end++;
            }

            if (end - start > 1)
            {
                Array.Sort(order, start, end - start);
            }

            start = end;
        }

        return !IsIdentity(order, count);
    }

    /// <summary>
    /// The order of the shown files by type name, ties by name, as places
    /// among the shown files; null when that is name order.  Each kind of file
    /// is named and ranked once - a folder of fifty thousand files is a
    /// handful of kinds - and then the files are sorted by rank and index
    /// packed into one number, so no string is compared per file at all.
    /// </summary>
    private int[]? TypeOrder(NestedFile[] all, int[]? shownFrom, int count)
    {
        var kinds = _typeKinds;
        var slots = _typeSlots;
        var slotOf = ArrayPool<int>.Shared.Rent(count);
        try
        {
            // Extensions are shared strings, so a kind is found by reference,
            // and a run of the same kind skips even that.
            string? lastExtension = null;
            var lastSlot = -1;
            for (var index = 0; index < count; index++)
            {
                var extension = all[shownFrom is null ? index : shownFrom[index]].Extension ?? string.Empty;
                if (!ReferenceEquals(extension, lastExtension))
                {
                    ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(slots, extension, out var exists);
                    if (!exists)
                    {
                        slot = kinds.Count;
                        kinds.Add(extension);
                    }

                    lastExtension = extension;
                    lastSlot = slot;
                }

                slotOf[index] = lastSlot;
            }

            if (kinds.Count < 2)
            {
                return null;
            }

            // Two extensions can share a name - "jpg" and "jpeg" are both a
            // JPEG image - and then they share a rank too.
            var names = new string[kinds.Count];
            var byName = new int[kinds.Count];
            for (var kind = 0; kind < kinds.Count; kind++)
            {
                names[kind] = FileTypeNames.Of(kinds[kind]);
                byName[kind] = kind;
            }

            var comparer = StringComparer.CurrentCultureIgnoreCase;
            Array.Sort(names, byName, comparer);
            var ranks = new int[kinds.Count];
            var rank = 0;
            for (var position = 0; position < names.Length; position++)
            {
                if (position > 0 && comparer.Compare(names[position - 1], names[position]) != 0)
                {
                    rank++;
                }

                ranks[byName[position]] = rank;
            }

            if (rank == 0)
            {
                return null;
            }

            var keys = ArrayPool<long>.Shared.Rent(count);
            var descending = _sort.Descending;
            for (var index = 0; index < count; index++)
            {
                var kindRank = ranks[slotOf[index]];
                keys[index] = ((long)(descending ? rank - kindRank : kindRank) << 32) | (uint)index;
            }

            Array.Sort(keys, 0, count);
            var order = new int[count];
            for (var position = 0; position < count; position++)
            {
                order[position] = (int)(keys[position] & 0xFFFF_FFFF);
            }

            ArrayPool<long>.Shared.Return(keys);
            return IsIdentity(order, count) ? null : order;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(slotOf);
            kinds.Clear();
            slots.Clear();
        }
    }

    private static bool IsIdentity(int[] order, int count)
    {
        for (var index = 0; index < count; index++)
        {
            if (order[index] != index)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A file by name among files in name order: a binary search, and a plain
    /// scan for a name culture order and the file system disagree about.
    /// </summary>
    private static int SearchFiles(IReadOnlyList<NestedFile> files, string name)
    {
        var low = 0;
        var high = files.Count - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var order = StringComparer.CurrentCultureIgnoreCase.Compare(files[middle].Name, name);
            if (order == 0)
            {
                return middle;
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

        for (var index = 0; index < files.Count; index++)
        {
            if (string.Equals(files[index].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
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
            // The date is in the same buffer as the name and attributes: taking
            // it costs no further call to the file system.
            var entries = new FileSystemEnumerable<(string Name, FileAttributes Attributes, long Length, long ModifiedTicks, bool IsDirectory, bool IsLink)>(
                ExtendedLength(path),
                static (ref FileSystemEntry entry) => (
                    entry.FileName.ToString(),
                    entry.Attributes,
                    entry.IsDirectory ? 0 : entry.Length,
                    entry.LastWriteTimeUtc.UtcTicks,
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

            foreach (var (name, attributes, length, modifiedTicks, isDirectory, isLink) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isHidden = (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
                if (!isDirectory)
                {
                    listed.Add(new NestedFile(name, isHidden, length, modifiedTicks));
                    continue;
                }

                if (folders.Count >= NestedTree.MaximumChildren)
                {
                    truncated = true;
                    continue;
                }

                folders.Add(new NestedEntry(name, isHidden, isLink, modifiedTicks));
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
