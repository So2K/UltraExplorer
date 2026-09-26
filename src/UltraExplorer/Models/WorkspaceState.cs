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
    /// Version 1 stored a colour label and a note per canvas node.  They are
    /// migrated into the path-keyed folder mark store on first load and then
    /// stop being written.
    /// </summary>
    public List<LegacyNodeState> Nodes { get; set; } = [];
}

public sealed record FavoriteState(string Name, string Path, string Glyph, string AccentHex);

public sealed record LegacyNodeState(string Path, string AccentHex, string Note);
