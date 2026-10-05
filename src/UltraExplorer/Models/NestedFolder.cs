using UltraExplorer.Services;
using UltraExplorer.Services.Watch;

namespace UltraExplorer.Models;

public enum NestedFolderKind
{
    /// <summary>The one cell everything else is inside: every drive and extra root.</summary>
    Computer,
    Drive,
    Folder
}

/// <summary>
/// Where the nested canvas's camera is, as a folder and that folder's place on
/// screen.  A folder rather than a coordinate: a coordinate precise enough for
/// a folder thirty levels down would need more digits than a double has.
/// </summary>
/// <param name="AnchorPath">The folder the camera is fixed to.</param>
/// <param name="X">Its left edge, from the view's centre, in view widths.</param>
/// <param name="Y">Its top edge, from the view's centre, in view widths.</param>
/// <param name="Width">Its width, in view widths.</param>
public sealed record NestedCameraState(string AnchorPath, double X, double Y, double Width);

/// <summary>
/// A file inside a folder on the nested canvas: a name to draw and a size to
/// show.  A value, not an object - a folder can hold a hundred thousand.
/// </summary>
public readonly record struct NestedFile
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Extensions = new(StringComparer.Ordinal);

    public NestedFile(string name, bool isHidden, long length)
        : this(name, isHidden, length, 0)
    {
    }

    public NestedFile(string name, bool isHidden, long length, long modifiedTicks)
    {
        Name = name;
        IsHidden = isHidden;
        Length = length;
        ModifiedTicks = modifiedTicks;
        Extension = ExtensionOf(name);
    }

    public string Name { get; }

    public bool IsHidden { get; }

    public long Length { get; }

    /// <summary>
    /// When the file was last written, as UTC ticks; zero when the reader did
    /// not say.  Kept as a number rather than a date because ordering a folder
    /// by it compares fifty thousand of them.
    /// </summary>
    public long ModifiedTicks { get; }

    /// <summary>
    /// Lower-case extension without the dot, or empty.  Worked out once, when
    /// the folder is read, and shared: drawing a folder of ten thousand DLLs
    /// looks their colour up by one string, not ten thousand new ones a frame.
    /// </summary>
    public string Extension { get; }

    private static string ExtensionOf(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1 || name.Length - dot > 33)
        {
            return string.Empty;
        }

        Span<char> lower = stackalloc char[name.Length - dot - 1];
        name.AsSpan(dot + 1).ToLowerInvariant(lower);
        var lookup = Extensions.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(lower, out var shared))
        {
            return shared;
        }

        var extension = lower.ToString();
        var added = Extensions.GetOrAdd(extension, extension);
        if (ReferenceEquals(added, extension))
        {
            // The first file of its kind anyone has read: have the Shell name
            // the type now, in the background, rather than on the UI thread
            // the first time the canvas is ordered by type.
            FileTypeNames.Prefetch(extension);
        }

        return added;
    }
}

/// <summary>
/// A folder's shown files in an order of their own - by date, size, type or
/// names from Z, or only some of them in name order - kept as the listing
/// and the order to walk it in rather than as a copy of every file.  Placing
/// a whole tree again after a change of order makes one of these for nearly
/// every folder, and an index per file is a tenth of what a copy would leave
/// the garbage collector to carry.  Never changed once made; a folder read
/// or placed again gets a new one.
/// </summary>
internal sealed class NestedFileOrder : IReadOnlyList<NestedFile>
{
    private readonly NestedFile[] _files;
    private readonly int[] _order;
    private readonly bool _isNameOrder;
    private int[]? _positions;

    /// <param name="files">Every file the folder read, in name order.</param>
    /// <param name="order">For each place, the index among <paramref name="files"/> of the file shown there.</param>
    /// <param name="isNameOrder">Whether <paramref name="order"/> only leaves files out and never reorders them.</param>
    public NestedFileOrder(NestedFile[] files, int[] order, bool isNameOrder)
    {
        _files = files;
        _order = order;
        _isNameOrder = isNameOrder;
    }

    public NestedFile this[int index] => _files[_order[index]];

    public int Count => _order.Length;

    /// <summary>
    /// For each of the listing's files, its place here, or -1 when it is not
    /// shown; null when the places are in name order and a binary search over
    /// this list itself finds a file.  Worked out on first use.
    /// </summary>
    public int[]? Positions
    {
        get
        {
            if (_isNameOrder)
            {
                return null;
            }

            if (_positions is null)
            {
                var positions = new int[_files.Length];
                if (_order.Length < _files.Length)
                {
                    Array.Fill(positions, -1);
                }

                for (var place = 0; place < _order.Length; place++)
                {
                    positions[_order[place]] = place;
                }

                _positions = positions;
            }

            return _positions;
        }
    }

    public IEnumerator<NestedFile> GetEnumerator()
    {
        for (var index = 0; index < _order.Length; index++)
        {
            yield return _files[_order[index]];
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public enum NestedLoadState
{
    NotLoaded,
    Queued,
    Loading,
    Loaded,
    Failed
}

/// <summary>
/// Which read a folder is waiting for or being read by: its first, which
/// takes it from <see cref="NestedLoadState.NotLoaded"/> to loaded, or a
/// refresh of one already loaded - drawn with what it had until the new
/// listing is applied, and so never shown as reading.
/// </summary>
internal enum ReadKind : byte
{
    None,
    Load,
    Refresh
}

/// <summary>
/// One folder on the nested canvas.  Deliberately a plain object rather than a
/// view model: a drive seen whole is tens of thousands of these, and none of
/// them binds to anything - the canvas draws them itself.
///
/// It is owned by the UI thread.  The read queue's workers see the path, the
/// watch and what the folder held when its read began; its place in the
/// queue and the stamps a read takes (<see cref="QueuedRead"/>,
/// <see cref="ReadEpoch"/>, <see cref="IsStale"/>) change under the queue's
/// lock; everything else is written when a finished listing is applied, at
/// the start of a frame.
/// </summary>
public sealed class NestedFolder
{
    private static readonly NestedFolder[] None = [];

    /// <summary>Where FNV-1a starts: the hash of an empty path.</summary>
    private const uint HashOrigin = 2166136261u;

    private const uint HashPrime = 16777619u;

    /// <summary>
    /// Set for a folder made with its path; for one made from its parent's
    /// listing, joined from the parent's the first time anything asks
    /// (<see cref="FullPath"/>).
    /// </summary>
    private string? _fullPath;

    /// <summary>FNV-1a over the lower-cased path, which is what <see cref="Hue"/> is made of.</summary>
    private readonly uint _pathHash;

    private volatile bool _isStale;

    internal NestedFolder(
        string fullPath,
        string name,
        NestedFolderKind kind,
        NestedFolder? parent,
        bool isHidden = false,
        bool isReparsePoint = false,
        string secondaryText = "",
        long modifiedTicks = 0)
    {
        _fullPath = fullPath;
        Name = name;
        Kind = kind;
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
        IsHidden = isHidden;
        IsReparsePoint = isReparsePoint;
        SecondaryText = secondaryText;
        ModifiedTicks = modifiedTicks;
        Watch = parent?.Watch;
        _pathHash = Hash(HashOrigin, fullPath);
    }

    /// <summary>A sub-folder as its parent's listing names it, with no path of its own until one is asked for.</summary>
    private NestedFolder(NestedFolder parent, string name, bool isHidden, bool isReparsePoint, long modifiedTicks)
    {
        Name = name;
        Kind = NestedFolderKind.Folder;
        Parent = parent;
        Depth = parent.Depth + 1;
        IsHidden = isHidden;
        IsReparsePoint = isReparsePoint;
        SecondaryText = string.Empty;
        ModifiedTicks = modifiedTicks;
        Watch = parent.Watch;
        _pathHash = ChildHash(parent, name);
    }

    /// <summary>
    /// A sub-folder of <paramref name="parent"/> from its listing.  Reading a
    /// folder of twenty-five thousand sub-folders makes twenty-five thousand of
    /// these, most of them never drawn large enough to be named or looked up:
    /// each one's path is joined only when something needs it, and its hue
    /// comes from the parent's hash carried on over the name - the same number
    /// the whole path gives.
    /// </summary>
    internal static NestedFolder ChildOf(NestedFolder parent, string name, bool isHidden, bool isReparsePoint, long modifiedTicks) =>
        new(parent, name, isHidden, isReparsePoint, modifiedTicks);

    /// <summary>The folder's path; for a sub-folder, its parent's joined with its name the first time it is asked for.</summary>
    public string FullPath => _fullPath ??= Path.Combine(Parent!.FullPath, Name);

    public string Name { get; }
    public NestedFolderKind Kind { get; }
    public NestedFolder? Parent { get; }
    public int Depth { get; }

    /// <summary>Hidden or system: shown only while hidden items are.</summary>
    public bool IsHidden { get; }

    /// <summary>
    /// A junction or symbolic link.  Its contents are somewhere else on the
    /// canvas already, and following it blindly is how a tree becomes a cycle,
    /// so it is never read on its own.
    /// </summary>
    public bool IsReparsePoint { get; }

    /// <summary>Free space for a drive; empty for a folder.</summary>
    public string SecondaryText { get; internal set; }

    /// <summary>
    /// When the folder was last written, as UTC ticks, from its parent's
    /// listing; zero for a drive or when the reader did not say.  Settable
    /// because a folder that is still there after its parent is read again
    /// keeps its object - with everything read below it - but not its date.
    /// </summary>
    public long ModifiedTicks { get; internal set; }

    public bool IsComputer => Kind == NestedFolderKind.Computer;

    /// <summary>Whether the canvas may read this folder's contents when it comes into view.</summary>
    public bool CanLoad => !IsComputer && !IsReparsePoint;

    public NestedLoadState LoadState { get; internal set; }

    /// <summary>Only named descendants are known; the contents have not been
    /// listed. The camera can still reach them through their physical parents.</summary>
    public bool HasPartialListing { get; internal set; }

    /// <summary>When the last read failed, as a <see cref="System.Diagnostics.Stopwatch"/> timestamp.</summary>
    internal long FailedAt { get; set; }

    /// <summary>The last failure may pass if tried again.</summary>
    internal bool IsRetryable { get; set; }

    /// <summary>
    /// On a share: read one at a time per share, so a share that hangs cannot
    /// starve the local drives.  A share is whatever the watch on it says, or
    /// else a UNC path or a drive letter mapped to one - a mapped J: included,
    /// which a test for a leading pair of separators alone took for a local disk.
    /// </summary>
    public bool IsNetwork => Watch?.IsNetwork ?? VolumeKinds.IsNetwork(FullPath);

    // ---- the read queue --------------------------------------------------------------

    /// <summary>
    /// The watch on the volume or share the folder is on, taken from its parent
    /// when it is made; a drive or root is given its own by the tree.  Null
    /// while nothing watches for changes.
    /// </summary>
    internal WatchRoot? Watch { get; init; }

    /// <summary>
    /// <see cref="WatchRoot.Epoch"/> as it was when the folder's last read
    /// began.  An epoch that has moved on since means changes under the root
    /// may have been missed - the watch overflowed, or was armed again - and
    /// the folder is read again the next time it is drawn.
    /// </summary>
    internal int ReadEpoch { get; set; }

    /// <summary>
    /// Something in the folder changed after its last read began: it is read
    /// again the next time it is drawn, and drawn as it was until then.
    /// Cleared when a read begins, so a change that arrives while the read is
    /// still going sets it again and is not lost.  Written from any thread.
    /// </summary>
    internal bool IsStale
    {
        get => _isStale;
        set => _isStale = value;
    }

    /// <summary>Whether what was read is out of date: a change was seen, or may have been missed.</summary>
    internal bool NeedsRefresh => _isStale || Watch is { } watch && ReadEpoch != Volatile.Read(ref watch.Epoch);

    /// <summary>The read the folder is queued for or being read by; <see cref="ReadKind.None"/> when neither.</summary>
    internal ReadKind QueuedRead { get; set; }

    /// <summary>Where the folder is in the read queue's list of waiting folders, or -1 when it is not waiting.</summary>
    internal int QueueIndex { get; set; } = -1;

    /// <summary>
    /// Counts the reads begun for the folder, and the reads given up on: a
    /// finished read carries the count it began with, and one that no longer
    /// matches is dropped rather than applied.
    /// </summary>
    internal int ReadTicket { get; set; }

    /// <summary>What <see cref="LastDrawnFrame"/> is before the folder is drawn: far enough back that no frame counts it as recent.</summary>
    internal const long NeverDrawn = long.MinValue / 2;

    /// <summary>
    /// The last picture (<see cref="NestedTree.Frame"/>) that drew the folder
    /// large enough to be read: whether a change to it is worth reading now.
    /// </summary>
    internal long LastDrawnFrame { get; set; } = NeverDrawn;

    /// <summary>How wide <see cref="LastDrawnFrame"/> drew it, in pixels: how soon a change to it should be read.</summary>
    internal float LastDrawnWidth { get; set; }

    /// <summary>
    /// The folder's own last-write time, as UTC ticks, taken just before its
    /// last read - on a volume that is polled rather than watched, or a share,
    /// where a directory whose time has moved on since is read again.  Zero
    /// when it was not taken.
    /// </summary>
    internal long DirWriteTicks { get; set; }

    public bool IsLoaded => LoadState == NestedLoadState.Loaded;

    public string ErrorMessage { get; internal set; } = string.Empty;

    /// <summary>
    /// Every sub-folder that was read, hidden ones included, in name order
    /// whatever order the canvas shows them in: finding one by name is a
    /// binary search over this.
    /// </summary>
    internal NestedFolder[] AllChildren { get; set; } = None;

    /// <summary>The sub-folders on the canvas, in the order they are shown, as <see cref="Grid"/> reads; empty until read.</summary>
    public IReadOnlyList<NestedFolder> Children { get; internal set; } = None;

    /// <summary>Where <see cref="Children"/> sit inside this cell.</summary>
    public NestedGrid Grid { get; internal set; } = NestedGrid.Empty;

    /// <summary>Every file that was read, hidden ones included, in name order.</summary>
    internal NestedFile[] AllFiles { get; set; } = [];

    /// <summary>The files on the canvas, in the order they are shown, as <see cref="FileGrid"/> reads; empty until read.</summary>
    public IReadOnlyList<NestedFile> Files { get; internal set; } = [];

    /// <summary>Where <see cref="Files"/> sit inside this cell, below the sub-folders.</summary>
    public NestedFileGrid FileGrid { get; internal set; } = NestedFileGrid.Empty;

    /// <summary>
    /// Where each of <see cref="AllFiles"/> ended up in <see cref="Files"/>,
    /// or -1 for one that is not shown; null while <see cref="Files"/> is in
    /// name order, the order of <see cref="AllFiles"/> itself.  Finding a file
    /// by name is a binary search over the names, which only name order
    /// allows; under any other order this is how the search's answer is
    /// turned into the tile that holds the file.  Worked out the first time
    /// it is asked for, which is seldom - placing a folder does not need it.
    /// </summary>
    internal int[]? FilePositions => Files is NestedFileOrder order ? order.Positions : null;

    /// <summary>
    /// The <see cref="NestedTree.SortGeneration"/> this folder's children and
    /// files were last placed for, or -1 before they have been placed at all.
    /// A change of order only bumps the generation; each folder is placed
    /// again when something is about to look at it, or when the background
    /// pass over the whole tree reaches it, whichever comes first.
    /// </summary>
    internal int LayoutSortGeneration { get; set; } = -1;

    /// <summary>
    /// The order this folder's children and files were last placed in.  A
    /// change of order anywhere moves every folder's stamp on, and a folder
    /// whose own order is still the one it was placed in - most of them,
    /// when one folder is sorted - needs only the stamp, not placing again.
    /// </summary>
    internal ItemSort PlacedSort { get; set; }

    /// <summary>Whether this folder's grids were last made to fill a column at a time (see <see cref="NestedGrid.DownFirst"/>).</summary>
    internal bool PlacedDownFirst { get; set; }

    /// <summary>Whether this folder was last placed with its files shown (see <see cref="NestedTree.ShowFiles"/>).</summary>
    internal bool PlacedShowFiles { get; set; } = true;

    /// <summary>Files that were counted but not listed, past <see cref="NestedTree.MaximumFiles"/>.</summary>
    public int UnlistedFileCount { get; internal set; }

    /// <summary>The path of one of this folder's files.</summary>
    public string PathOf(NestedFile file) => Path.Combine(FullPath, file.Name);

    /// <summary>Position among the parent's children, or -1 when not among them.</summary>
    public int Index { get; internal set; } = -1;

    /// <summary>Top-left inside the parent's unit frame.</summary>
    public double OffsetX { get; internal set; }

    public double OffsetY { get; internal set; }

    /// <summary>Size relative to the parent.</summary>
    public double Scale { get; internal set; } = 1;

    /// <summary>Files directly inside, counted when the folder was read.</summary>
    public int FileCount { get; internal set; }

    /// <summary>Hidden and system files among <see cref="FileCount"/>.</summary>
    public int HiddenFileCount { get; internal set; }

    /// <summary>More entries than one listing is allowed to hold.</summary>
    public bool IsTruncated { get; internal set; }

    /// <summary>A stable hue from the path, so a folder is the same colour every session.</summary>
    public double Hue => _pathHash % 360u;

    /// <summary>Last frame the canvas wanted this folder read.  Stale requests are dropped.</summary>
    internal long RequestedFrame { get; set; }

    /// <summary>Read even when off screen: something is waiting for its place (a mark, a reveal).</summary>
    internal bool IsSticky { get; set; }

    internal double Priority { get; set; }

    /// <summary>Dropped from the tree by a refresh; nothing may be attached to it any more.</summary>
    internal bool IsForgotten { get; set; }

    // The canvas's colours for this cell, worked out once per change of marks
    // rather than once per frame: a frame can hold tens of thousands of cells.
    internal int PaletteStamp { get; set; } = -1;
    internal uint BodyColour { get; set; }
    internal uint HeaderColour { get; set; }
    internal uint RimColour { get; set; }
    internal uint StripeColour { get; set; }
    internal bool HasLabel { get; set; }
    internal bool HasNote { get; set; }

    // What the canvas's name filter made of this folder, and for which filter.
    internal int FilterStamp { get; set; } = -1;
    internal int FilterState { get; set; }

    /// <summary>Whether <paramref name="other"/> is this folder or somewhere inside it.</summary>
    public bool Contains(NestedFolder other)
    {
        for (var current = other; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, this))
            {
                return true;
            }
        }

        return false;
    }

    public override string ToString() => FullPath;

    /// <summary>FNV-1a over the lower-cased path: string.GetHashCode changes every run.</summary>
    internal static double HueFor(string path) => Hash(HashOrigin, path) % 360u;

    private static uint Hash(uint hash, string text)
    {
        foreach (var character in text)
        {
            hash = (hash ^ char.ToLowerInvariant(character)) * HashPrime;
        }

        return hash;
    }

    /// <summary>
    /// The hash of the path <see cref="Path.Combine(string, string)"/> makes
    /// of the parent's and <paramref name="name"/>, without making it: the
    /// parent's hash carried on over a separator where Combine puts one, then
    /// over the name - or the name's alone, where Combine returns it alone.
    /// </summary>
    private static uint ChildHash(NestedFolder parent, string name)
    {
        var parentPath = parent.FullPath;
        if (name.Length == 0)
        {
            return parent._pathHash;
        }

        var rooted = IsSeparator(name[0]) || name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':';
        if (parentPath.Length == 0 || rooted)
        {
            return Hash(HashOrigin, name);
        }

        var hash = parent._pathHash;
        if (!IsSeparator(parentPath[^1]))
        {
            hash = (hash ^ char.ToLowerInvariant(Path.DirectorySeparatorChar)) * HashPrime;
        }

        return Hash(hash, name);
    }

    private static bool IsSeparator(char character) =>
        character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar;
}
