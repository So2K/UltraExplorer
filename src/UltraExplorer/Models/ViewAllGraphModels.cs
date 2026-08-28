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
/// Caps a single expansion without ever walking descendants.  Increasing the
/// cap and refreshing the branch is an explicit user action.
/// </summary>
public sealed record ViewAllGraphOptions(
    bool IncludeHidden = false,
    bool FollowReparsePoints = false,
    int MaximumChildrenPerFolder = 750)
{
    public int SafeMaximumChildren => Math.Clamp(MaximumChildrenPerFolder, 32, 10_000);
}

public sealed class ViewAllWorkspaceState
{
    public int SchemaVersion { get; set; } = 1;
    public List<ViewAllNodeState> Nodes { get; set; } = [];
    public double ViewportX { get; set; }
    public double ViewportY { get; set; }
    public double ViewportZoom { get; set; } = 1;
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
