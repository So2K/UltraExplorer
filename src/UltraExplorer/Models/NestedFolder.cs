using UltraExplorer.Services;

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
    {
        Name = name;
        IsHidden = isHidden;
        Length = length;
        Extension = ExtensionOf(name);
    }

    public string Name { get; }

    public bool IsHidden { get; }

    public long Length { get; }

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
        return Extensions.GetOrAdd(extension, extension);
    }
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
/// One folder on the nested canvas.  Deliberately a plain object rather than a
/// view model: a drive seen whole is tens of thousands of these, and none of
/// them binds to anything - the canvas draws them itself.
///
/// It is owned by the UI thread.  Background readers see only the path, which
/// never changes; everything else is written when a finished listing is
/// handed back to the dispatcher.
/// </summary>
public sealed class NestedFolder
{
    private static readonly NestedFolder[] None = [];

    internal NestedFolder(
        string fullPath,
        string name,
        NestedFolderKind kind,
        NestedFolder? parent,
        bool isHidden = false,
        bool isReparsePoint = false,
        string secondaryText = "")
    {
        FullPath = fullPath;
        Name = name;
        Kind = kind;
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
        IsHidden = isHidden;
        IsReparsePoint = isReparsePoint;
        SecondaryText = secondaryText;
        Hue = HueFor(fullPath);
    }

    public string FullPath { get; }
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

    public bool IsComputer => Kind == NestedFolderKind.Computer;

    /// <summary>Whether the canvas may read this folder's contents when it comes into view.</summary>
    public bool CanLoad => !IsComputer && !IsReparsePoint;

    public NestedLoadState LoadState { get; internal set; }

    /// <summary>When the last read failed, as a <see cref="System.Diagnostics.Stopwatch"/> timestamp.</summary>
    internal long FailedAt { get; set; }

    /// <summary>The last failure may pass if tried again.</summary>
    internal bool IsRetryable { get; set; }

    /// <summary>On a share: read one at a time, so a share that hangs cannot starve the local drives.</summary>
    public bool IsNetwork => FullPath.StartsWith(@"\\", StringComparison.Ordinal);

    public bool IsLoaded => LoadState == NestedLoadState.Loaded;

    public string ErrorMessage { get; internal set; } = string.Empty;

    /// <summary>Every sub-folder that was read, hidden ones included.</summary>
    internal NestedFolder[] AllChildren { get; set; } = None;

    /// <summary>The sub-folders on the canvas, in order; empty until read.</summary>
    public IReadOnlyList<NestedFolder> Children { get; internal set; } = None;

    /// <summary>Where <see cref="Children"/> sit inside this cell.</summary>
    public NestedGrid Grid { get; internal set; } = NestedGrid.Empty;

    /// <summary>Every file that was read, hidden ones included, in order.</summary>
    internal NestedFile[] AllFiles { get; set; } = [];

    /// <summary>The files on the canvas, in order; empty until read.</summary>
    public IReadOnlyList<NestedFile> Files { get; internal set; } = [];

    /// <summary>Where <see cref="Files"/> sit inside this cell, below the sub-folders.</summary>
    public NestedFileGrid FileGrid { get; internal set; } = NestedFileGrid.Empty;

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
    public double Hue { get; }

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
    private static double HueFor(string path)
    {
        var hash = 2166136261u;
        foreach (var character in path)
        {
            hash = (hash ^ char.ToLowerInvariant(character)) * 16777619u;
        }

        return hash % 360u;
    }
}
