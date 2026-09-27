namespace UltraExplorer.Models;

/// <summary>
/// Shell-level state only.  Graph layout and viewport live in the separate
/// View All workspace file.
/// </summary>
public sealed class WorkspaceState
{
    public int SchemaVersion { get; set; } = 2;
    public List<FavoriteState> Favorites { get; set; } = [];
    public double SidebarWidth { get; set; } = 240;
    public bool IsMinimapVisible { get; set; }

    /// <summary>Whether the current folder is also shown as a list.</summary>
    public bool IsFolderListVisible { get; set; } = true;

    /// <summary>"Nested" or "Tree": which picture of the drives the canvas shows.</summary>
    public string CanvasLayout { get; set; } = "Nested";

    /// <summary>
    /// The order folders and files are shown in, as <see cref="ItemSort.ToSetting"/>
    /// writes it - "Name", "Modified-desc" and so on.  Missing or not
    /// understood, it is names from A, which is what every workspace written
    /// before there was a choice meant.
    /// </summary>
    public string? CanvasSort { get; set; } = ItemSort.Default.ToSetting();

    /// <summary>
    /// What draws the nested canvas: "Auto" - the GPU wherever it can be
    /// used, the CPU elsewhere - "Gpu" or "Cpu".  Missing or not understood,
    /// it is Auto.  The <c>--renderer</c> switch and ULTRAEXPLORER_RENDERER
    /// win over it for the run they are given to.
    /// </summary>
    public string? CanvasRenderer { get; set; }

    /// <summary>
    /// What a left drag on the nested canvas does where it picks nothing up:
    /// "select" draws a selection rectangle, "pan" moves the view as it used
    /// to.  Missing or not understood, it is select.
    /// </summary>
    public string? NestedLeftDrag { get; set; } = LeftDragSetting(Controls.NestedLeftDrag.SelectArea);

    /// <summary>Whether the one-time hint that left-drag now selects has been shown.</summary>
    public bool NestedLeftDragHintShown { get; set; }

    public static Controls.NestedLeftDrag ParseLeftDrag(string? setting) =>
        string.Equals(setting?.Trim(), "pan", StringComparison.OrdinalIgnoreCase)
            ? Controls.NestedLeftDrag.Pan
            : Controls.NestedLeftDrag.SelectArea;

    public static string LeftDragSetting(Controls.NestedLeftDrag drag) =>
        drag == Controls.NestedLeftDrag.Pan ? "pan" : "select";

    /// <summary>
    /// Version 1 stored a colour label and a note per canvas node.  They are
    /// migrated into the path-keyed folder mark store on first load and then
    /// stop being written.
    /// </summary>
    public List<LegacyNodeState> Nodes { get; set; } = [];
}

public sealed record FavoriteState(string Name, string Path, string Glyph, string AccentHex);

public sealed record LegacyNodeState(string Path, string AccentHex, string Note);
