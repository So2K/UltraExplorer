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

    /// <summary>
    /// Explorer's hidden items: whether hidden files and folders are listed.
    /// Missing - a workspace written before it was remembered - they are not,
    /// as Explorer starts.
    /// </summary>
    public bool ShowHiddenItems { get; set; }

    /// <summary>"Nested" or "Tree": which picture of the drives the canvas shows.</summary>
    public string CanvasLayout { get; set; } = "Nested";

    /// <summary>
    /// The order folders and files are shown in when a folder has none of
    /// its own (<see cref="FolderOrders.Default"/>), as <see cref="ItemSort.ToSetting"/>
    /// writes it - "Name", "Modified-desc" and so on.  Missing or not
    /// understood, it is names from A, which is what every workspace written
    /// before there was a choice meant.  A workspace written before folders
    /// had their own orders called this the one order of every folder, and
    /// it still is for every folder of such a workspace.
    /// </summary>
    public string? CanvasSort { get; set; } = ItemSort.Default.ToSetting();

    /// <summary>
    /// "PerFolder" - a header sorts the folder it is used on - or
    /// "AllFolders", one order for every folder as before (<see cref="FolderOrders.Scope"/>).
    /// Missing or not understood, it is PerFolder.
    /// </summary>
    public string? CanvasSortScope { get; set; }

    /// <summary>
    /// "DownThenAcross" - a folder's items fill a column at a time - or
    /// "AcrossThenDown", a row at a time as before (<see cref="FolderOrders.Flow"/>).
    /// Missing or not understood, it is DownThenAcross.
    /// </summary>
    public string? CanvasLayoutOrder { get; set; }

    /// <summary>The folders sorted on their own, the least recently sorted first; at most <see cref="FolderOrders.MaximumFolders"/>.</summary>
    public List<FolderSortState>? FolderSorts { get; set; }

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

    /// <summary>Optional round favorite links above This PC; missing settings keep the existing view.</summary>
    public bool ShowFavoriteLinks { get; set; }

    /// <summary>Show a content thumbnail when hovering a file; older workspaces enable it by default.</summary>
    public bool ShowHoverPreviews { get; set; } = true;

    /// <summary>Optional PR4 features are opt-in; older workspaces keep their existing behavior.</summary>
    public bool BrowseArchives { get; set; }
    public bool ShowDropShelf { get; set; }
    public bool ShowCopyPathButton { get; set; }

    /// <summary>
    /// The canvas layers switched off, by name - "Files", "Icons", "Details",
    /// "FolderCounts", "Marks" (see <see cref="CanvasLayers"/>).  Missing or
    /// empty, every layer shows; a name not understood is ignored.
    /// </summary>
    public List<string>? CanvasLayersOff { get; set; }

    /// <summary>
    /// Whether the nested canvas is split into two panes (see
    /// <see cref="SplitLayout"/>).  Missing - a workspace written before
    /// there was a split - it is not.
    /// </summary>
    public bool IsSplit { get; set; }

    /// <summary>
    /// "SideBySide" or "Stacked": how the two panes of a split view are laid
    /// out.  Missing or not understood, side by side.
    /// </summary>
    public string? SplitOrientation { get; set; }

    /// <summary>
    /// The first pane's share of the room a split view has, from
    /// <see cref="SplitLayout.MinimumRatio"/> to <see cref="SplitLayout.MaximumRatio"/>.
    /// Missing, half; outside that, brought back inside it.
    /// </summary>
    public double SplitRatio { get; set; } = SplitLayout.DefaultRatio;

    /// <summary>Which pane of a split view was being worked with: 0 the first, 1 the second.</summary>
    public int ActivePane { get; set; }

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
