using System.Windows;

namespace UltraExplorer.Models;

/// <summary>
/// The compact object types rendered by the View All graph.  A directory is a
/// branch, a file is a leaf and every ready drive is an independent root.
/// </summary>
public enum ViewAllEntryKind
{
    Drive,
    Folder,
    File
}

/// <summary>
/// Semantic zoom levels.  The graph keeps the same geometry at every zoom;
/// only the amount of content inside a node changes.
/// </summary>
public enum ViewAllDetailLevel
{
    /// <summary>So far out that individual objects are aggregated into blocks.</summary>
    Cluster,
    Dot,
    Glyph,
    Compact,
    Detailed
}

public sealed record ViewAllEntryDescriptor(
    string FullPath,
    string DisplayName,
    ViewAllEntryKind Kind,
    bool IsHidden,
    bool IsReparsePoint,
    long? SizeBytes,
    DateTime ModifiedUtc,
    string SecondaryText = "");

public sealed record ViewAllDirectorySnapshot(
    IReadOnlyList<ViewAllEntryDescriptor> Entries,
    bool IsTruncated,
    int LoadedCount);

/// <summary>
/// Decides which names survive enumeration.  The picker implements it over the
/// selected "Files of type" entry; the graph knows nothing about dialogs.
/// </summary>
public interface IEntryNameFilter
{
    bool Matches(string fileName);
}

/// <summary>
/// Caps a single expansion without ever walking descendants.  Increasing the
/// cap and refreshing the branch is an explicit user action.
/// </summary>
public sealed record ViewAllGraphOptions(
    bool IncludeHidden = false,
    bool FollowReparsePoints = false,
    int MaximumChildrenPerFolder = 5_000,
    bool ShowFiles = true,
    IEntryNameFilter? FileFilter = null)
{
    /// <summary>Absolute ceiling for one folder, however often Load more is used.</summary>
    public const int MaximumChildrenCeiling = 250_000;

    public int SafeMaximumChildren => Math.Clamp(MaximumChildrenPerFolder, 32, MaximumChildrenCeiling);
}

public sealed class ViewAllWorkspaceState
{
    public int SchemaVersion { get; set; } = 1;
    public List<ViewAllNodeState> Nodes { get; set; } = [];
    public double ViewportX { get; set; }
    public double ViewportY { get; set; }
    public double ViewportZoom { get; set; } = 1;

    /// <summary>Reselected on load so the address and status bars are never blank.</summary>
    public string ActivePath { get; set; } = string.Empty;

    /// <summary>
    /// Roots that are not local drives — a WSL distribution or a UNC share.
    /// They have to be re-added explicitly because only drives are discovered.
    /// </summary>
    public List<string> ExtraRoots { get; set; } = [];

    /// <summary>
    /// Folders the user hid from the canvas, with everything under them.  Kept
    /// by path rather than per node, because a hidden folder inside a branch
    /// that is refreshed loses its node and would lose the flag with it.
    /// </summary>
    public List<string> HiddenPaths { get; set; } = [];

    /// <summary>Where the nested canvas was looking, if it was the one in use.</summary>
    public NestedCameraState? NestedCamera { get; set; }
}

public sealed record ViewAllNodeState(
    string Path,
    double X,
    double Y,
    bool HasManualPosition,
    bool IsExpanded);

public sealed record ViewAllViewportState(Point Location, double Zoom);

public sealed record ViewAllExpansionResult(
    ViewAllNodeViewModel Node,
    IReadOnlyList<ViewAllNodeViewModel> AddedNodes,
    bool WasLoaded,
    bool IsTruncated);

public sealed record ViewAllRenderSet(
    IReadOnlyList<ViewAllNodeViewModel> Nodes,
    IReadOnlyList<ViewAllEdgeViewModel> Edges,
    ViewAllDetailLevel DetailLevel,
    int LogicalNodeCount);
