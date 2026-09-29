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
    /// How many starts in a row each extra root that is not here has been out
    /// of reach at.  One out of reach at
    /// <see cref="Services.ViewAllGraphService.MaximumRootMisses"/> in a row is
    /// taken for gone and forgotten; one that answers is taken off.
    /// </summary>
    public Dictionary<string, int> ExtraRootMisses { get; set; } = [];

    /// <summary>
    /// Folders the user hid from the canvas, with everything under them.  Kept
    /// by path rather than per node, because a hidden folder inside a branch
    /// that is refreshed loses its node and would lose the flag with it.
    /// </summary>
    public List<string> HiddenPaths { get; set; } = [];

    /// <summary>
    /// Where the nested canvas was looking, if it was the one in use: its
    /// first pane's, when the view is split, as <see cref="ActivePath"/> is
    /// the first pane's selection.
    /// </summary>
    public NestedCameraState? NestedCamera { get; set; }

    /// <summary>
    /// The second pane of a split view: where it was looking and what it had
    /// selected.  Kept while the view is not split, so splitting it again
    /// goes back there; missing from a workspace that was never split, which
    /// an older build reads as it always did.
    /// </summary>
    public NestedPaneState? SecondPane { get; set; }
}

/// <summary>One pane of a split view as the workspace keeps it: what it had selected, and where its camera was.</summary>
public sealed record NestedPaneState(string? ActivePath, NestedCameraState? NestedCamera);

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

/// <summary>
/// How far bringing a path into the graph got.  <see cref="Node"/> is the
/// deepest step that exists - the destination itself when nothing is missing -
/// and is null only when not even the root could be found.
/// <see cref="MissingStep"/> is the first step that could not be found, spelled
/// as it was asked for, so the message about it names what the user typed.
/// </summary>
public sealed record ViewAllChainResult(ViewAllNodeViewModel? Node, string? MissingStep)
{
    public bool IsComplete => Node is not null && MissingStep is null;
}

public sealed record ViewAllRenderSet(
    IReadOnlyList<ViewAllNodeViewModel> Nodes,
    IReadOnlyList<ViewAllEdgeViewModel> Edges,
    ViewAllDetailLevel DetailLevel,
    int LogicalNodeCount);
