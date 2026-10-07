using System.Diagnostics;
using UltraExplorer.Models;
using UltraExplorer.Services.Watch;

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
/// <param name="IsArchive">An archive file shown as a folder (see <see cref="Archives.ArchiveService"/>).</param>
public readonly record struct NestedEntry(string Name, bool IsHidden, bool IsReparsePoint, long ModifiedTicks = 0, bool IsArchive = false);

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
/// Every folder's sub-folders and files are placed in its own order,
/// <see cref="SortOf"/> - the folder's own if it was sorted by itself, the
/// default otherwise (see <see cref="FolderOrders"/>) - filling its grids a
/// column or a row at a time as <see cref="Orders"/> says.  Sorting one
/// folder places that one folder again, there and then.  A change that can
/// reach every folder - the default, the scope, the way grids fill -
/// re-places nothing at once: a tree read deep can be tens of thousands of
/// folders, and doing them all in one go would stall the window.  Instead
/// each folder remembers which order it was placed for, anything about to
/// be drawn or looked at is placed again first (<see cref="EnsureLayout"/>),
/// and a pass in small background slices brings the rest up to date behind
/// it.  The listings themselves stay in name order for ever, which is what
/// keeps finding a folder or a file by name a binary search whatever the
/// canvas shows.
/// </summary>
public sealed partial class NestedTree : IDisposable
{
    /// <summary>Sub-folders held for one folder.  WinSxS has tens of thousands; this holds all of them.</summary>
    public const int MaximumChildren = 100_000;

    /// <summary>Files listed by name for one folder; past this they are only counted.</summary>
    public const int MaximumFiles = 50_000;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;

    private bool _disposed;
    private int _knownCount;
    private bool _includeHidden;
    private Func<string, bool>? _fileNameFilter;
    private bool _archivesEnabled;
    private int _archivesGeneration;

    public NestedTree(Func<string, CancellationToken, NestedListing>? reader = null)
    {
        _reader = reader ?? ((path, cancellation) => NestedDirectoryReader.Read(path, cancellation, _archivesEnabled));

        // Taken once: a disposed source throws on every later read of its token.
        _lifetimeToken = _lifetime.Token;
        Root = new NestedFolder(string.Empty, "This PC", NestedFolderKind.Computer, null)
        {
            LoadState = NestedLoadState.Loaded
        };
        PostBackground = DefaultPostBackground();
        _orders = new FolderOrders();
        _orders.Changed += OnOrdersChanged;
        InitialiseReading();
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

    /// <summary>
    /// The time anything the tree stamps is measured by - when a folder's
    /// transition started, say: the frame clock of the canvas drawing the
    /// tree (<see cref="Controls.FrameClock.Now"/>), so what the tree stamps
    /// and what the canvas draws agree to the frame; <see cref="StopwatchClock"/>
    /// while no canvas does; a test's own clock in a test.
    /// </summary>
    internal Func<TimeSpan> Clock { get; set; } = StopwatchClock;

    /// <summary>The clock of a tree no canvas draws: the time since the first tree asked for it.</summary>
    internal static readonly Func<TimeSpan> StopwatchClock = static () => Stopwatch.GetElapsedTime(ClockOrigin);

    private static readonly long ClockOrigin = Stopwatch.GetTimestamp();

    /// <summary>
    /// Finds the watch on disk that covers a path, for the drives and roots
    /// the tree is given: every folder below one takes its root's watch from
    /// its parent.  Null - no watching - until the change hub is wired in.
    /// </summary>
    internal Func<string, WatchRoot?>? WatchRootFor { get; set; }

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
    /// Raised the moment any order changes - a folder's own, the default, the
    /// scope or the way grids fill - or files are shown or hidden
    /// (<see cref="ShowFiles"/>), before a single folder has been placed
    /// for it: every folder is still where the last picture showed it, which
    /// is when the canvas has to note what it is looking at if the view is to
    /// stay still while the contents move.
    /// </summary>
    public event Action? SortChanged;

    /// <summary>
    /// Which order each folder is shown in, and which way its grids fill.
    /// The tree's own until the window hands it the one the rest of the
    /// window shares; every change to it places folders again here.
    /// </summary>
    public FolderOrders Orders
    {
        get => _orders;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_orders, value))
            {
                return;
            }

            _orders.Changed -= OnOrdersChanged;
            _orders = value;
            _orders.Changed += OnOrdersChanged;
            OnOrdersChanged(null);
        }
    }

    /// <summary>The default order: what every folder without one of its own is placed in (<see cref="FolderOrders.Default"/>).</summary>
    public ItemSort Sort => _orders.Default;

    /// <summary>
    /// Bumped by every change of order.  A folder whose stamp is this number
    /// has been placed for the current orders; any other stamp means it may
    /// be placed for an earlier one and must be brought up to date, by
    /// <see cref="EnsureLayout"/>, before anything reads where its children
    /// are - which for a folder whose own order did not change is a look at
    /// what it was placed in, and the stamp moved on.
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

    /// <summary>Opt-in archive folders. Existing physical branches are refreshed without losing their expansion.</summary>
    public bool ArchivesEnabled
    {
        get => _archivesEnabled;
        set
        {
            if (_archivesEnabled == value || _disposed) return;
            _archivesEnabled = value;
            _archivesGeneration++;
            // Generation stamps make visible/explicitly opened folders refresh lazily.
            // Toggling a tree with thousands of cached branches queues no blanket reread.
            RaiseChanged();
        }
    }

    /// <summary>A picker's selected file type. Folder tiles remain visible;
    /// switching types reuses listings already read from disk.  The same
    /// filter's method bound again - a new delegate each time the window
    /// applies the dialog's rules - is the filter the tree has, and does not
    /// filter again every folder the picker has ever read.</summary>
    public Func<string, bool>? FileNameFilter
    {
        get => _fileNameFilter;
        set
        {
            if (Equals(_fileNameFilter, value)) return;
            _fileNameFilter = value;
            RefilterAll();
        }
    }

    /// <summary>
    /// Whether folders show their files - the Files layer of the canvas.
    /// Switched off, no folder has files on the canvas: none are drawn, hit,
    /// selected or stepped to, and each folder's sub-folders are placed in
    /// the whole of it rather than in their share above the files.  Every
    /// folder read keeps its files all the same, and says how many it has
    /// (<see cref="NestedFolder.FileCount"/>).  Placed again like a change
    /// of the way grids fill: the folder being looked at is held still (see
    /// <see cref="SortChanged"/>), and the background pass places the rest
    /// a few milliseconds at a time.
    /// </summary>
    public bool ShowFiles
    {
        get => _showFiles;
        set
        {
            if (_showFiles == value)
            {
                return;
            }

            _showFiles = value;
            OnOrdersChanged(null);
        }
    }

    private bool _showFiles = true;

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
        OnRootsChanging(roots);
        var existing = Root.AllChildren.ToDictionary(child => child.FullPath, StringComparer.OrdinalIgnoreCase);
        var children = new List<NestedFolder>(roots.Count);
        foreach (var root in roots)
        {
            // A drive unplugged and plugged back in before this ran has a new
            // watch: the old cell, still on the dropped one, would never hear
            // of a change on it again, so it is made afresh.
            if (existing.Remove(root.FullPath, out var kept) && kept.Name == root.Name && kept.Kind == root.Kind
                && kept.Watch is not { IsDropped: true })
            {
                kept.SecondaryText = root.SecondaryText;
                children.Add(kept);
                continue;
            }

            if (kept is not null)
            {
                Forget(kept);
            }

            children.Add(CreateRoot(root));
            _knownCount++;
        }

        foreach (var removed in existing.Values)
        {
            Forget(removed);
        }

        Root.AllChildren = [.. children];
        ApplyVisibleChildren(Root);
        OnRootsChanged();
        RaiseRootsApplied();
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
    /// A case-sensitive folder (a WSL tree) can hold names that differ in case
    /// alone, "Build" and "build": the one spelt exactly as asked is the one
    /// meant, and only a name spelt like neither takes the first of them.
    /// The name of one of its files is not scanned for.
    /// </summary>
    internal static NestedFolder? FindChild(NestedFolder folder, string name)
    {
        var children = folder.AllChildren;
        if (!folder.IsComputer && children.Length >= 16)
        {
            var comparer = StringComparer.CurrentCultureIgnoreCase;
            var low = 0;
            var high = children.Length - 1;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                var order = comparer.Compare(children[middle].Name, name);
                if (order == 0)
                {
                    // Names culture order calls equal sit side by side, and
                    // the search can land on any of them: look through the run.
                    var first = middle;
                    while (first > 0 && comparer.Compare(children[first - 1].Name, name) == 0)
                    {
                        first--;
                    }

                    NestedFolder? caseless = null;
                    for (var index = first; index < children.Length && comparer.Compare(children[index].Name, name) == 0; index++)
                    {
                        var candidate = children[index];
                        if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
                        {
                            return candidate;
                        }

                        if (caseless is null && string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            caseless = candidate;
                        }
                    }

                    if (caseless is not null)
                    {
                        return caseless;
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

            // A name that is one of the folder's files is no sub-folder's - a
            // folder cannot hold a file and a folder of one name - and the
            // path of a file always comes this way: a file's beacon, looked
            // up every frame, scanned all twenty-five thousand sub-folders of
            // its folder for nothing.  Its files are in name order too.
            if (SearchFiles(folder.AllFiles, name) >= 0)
            {
                return null;
            }
        }

        NestedFolder? match = null;
        foreach (var child in children)
        {
            if (string.Equals(child.Name, name, StringComparison.Ordinal)
                || folder.IsComputer && string.Equals(child.FullPath, name, StringComparison.Ordinal))
            {
                return child;
            }

            if (match is null
                && (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase)
                    || folder.IsComputer && string.Equals(child.FullPath, name, StringComparison.OrdinalIgnoreCase)))
            {
                match = child;
            }
        }

        return match;
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

    /// <summary>Whether a folder read holds a file of this name, shown or not.</summary>
    internal static bool HoldsFile(NestedFolder folder, string name) => SearchFiles(folder.AllFiles, name) >= 0;

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

        // The orders are the window's and outlive the tree.
        _orders.Changed -= OnOrdersChanged;

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

    /// <summary>
    /// A path as the tree keys it: the one spelling every map uses, a name
    /// ending in a dot or a space included (see <see cref="ViewAllPath.KeepNameEnds"/>).
    /// A path with a '~' in it that is spelt that way already - a folder's
    /// own path, as every mark, beacon and selection hands it back - is
    /// taken as it is: normalising it would change nothing, and a '~'
    /// anywhere makes <see cref="Path.GetFullPath(string)"/> ask the file
    /// system for the long form of every name on the way, in case one is a
    /// short 8.3 alias - about a millisecond a look on a local disk, a round
    /// trip per name on a share, and the beacons look theirs up every frame.
    /// One with a name that may be such an alias ("PROGRA~1") is normalised
    /// as ever.
    /// </summary>
    private static string Key(string path)
    {
        if (path.Contains('~') && IsKeyedAlready(path))
        {
            return path;
        }

        try
        {
            return ViewAllPath.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>
    /// Whether <see cref="ViewAllPath.Normalize"/> would give <paramref name="path"/>
    /// back as it is but for expanding short names, and no name in it may be
    /// one: a drive's path (<c>C:\a\b</c>) or a share's (<c>\\server\share\a</c>),
    /// with single backslashes between names, none of them "." or "..", none
    /// ending in a dot or a space, none a device's (<c>CON</c>, <c>NUL</c>...),
    /// and none shaped like a short name Windows makes - at most eight
    /// characters ending in '~' and a number, with at most three after a dot.
    /// Anything else is left to <see cref="ViewAllPath.Normalize"/>.
    /// </summary>
    private static bool IsKeyedAlready(string path)
    {
        int start;
        if (path.Length > 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
        {
            start = 3;
        }
        else if (path.Length > 4 && path[0] == '\\' && path[1] == '\\' && path[2] is not ('\\' or '?' or '.'))
        {
            start = 2;
        }
        else
        {
            return false;
        }

        var names = 0;
        var remaining = path.AsSpan(start);
        while (true)
        {
            var end = remaining.IndexOf('\\');
            var name = end < 0 ? remaining : remaining[..end];
            if (name.IsEmpty || name is "." or ".." || name[^1] == '.' || char.IsWhiteSpace(name[^1])
                || name.IndexOfAny('/', ':', '"') >= 0 || IsDeviceName(name) || MayBeShortName(name))
            {
                return false;
            }

            names++;
            if (end < 0)
            {
                break;
            }

            remaining = remaining[(end + 1)..];
        }

        // A share's path holds its server and its share at least.
        return start == 3 || names >= 2;
    }

    /// <summary>Whether a name is shaped like a short name Windows makes: "PROGRA~1", "AB12C~10.TXT".</summary>
    private static bool MayBeShortName(ReadOnlySpan<char> name)
    {
        var dot = name.IndexOf('.');
        var stem = dot < 0 ? name : name[..dot];
        if (stem.Length > 8 || dot >= 0 && (name.Length - dot - 1 > 3 || name[(dot + 1)..].Contains('.')))
        {
            return false;
        }

        var tilde = stem.LastIndexOf('~');
        return tilde >= 0 && tilde < stem.Length - 1 && !stem[(tilde + 1)..].ContainsAnyExceptInRange('0', '9');
    }

    /// <summary>Whether a name, up to its first dot, is one Windows keeps for a device.</summary>
    private static bool IsDeviceName(ReadOnlySpan<char> name)
    {
        var dot = name.IndexOf('.');
        var stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
        if (stem.Length is < 3 or > 7)
        {
            return false;
        }

        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && (char.IsAsciiDigit(stem[3]) || stem[3] is '\u00B9' or '\u00B2' or '\u00B3');
    }
}
