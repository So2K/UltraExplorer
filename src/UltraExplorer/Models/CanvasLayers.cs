namespace UltraExplorer.Models;

/// <summary>
/// The layers of the nested canvas that can be switched off, each on its
/// own, to see less at once: the files, their icons, what a file's tile says
/// beside its name, what a folder's title says it holds, and the marks the
/// user put on things.  Hidden items are Explorer's own setting and are not
/// one of these, though the menus and Settings show them alongside.
/// </summary>
[Flags]
public enum CanvasLayer
{
    None = 0,

    /// <summary>File tiles.  Off, they are neither drawn nor laid out: each folder's sub-folders take the whole of it.</summary>
    Files = 1,

    /// <summary>The icons of files on their tiles and of folders on their titles.</summary>
    Icons = 2,

    /// <summary>The size, date or type a file's tile shows beside its name.</summary>
    Details = 4,

    /// <summary>"3 folders · 12 files" on a folder's title.</summary>
    FolderCounts = 8,

    /// <summary>Colours, notes and pins: on the cells and tiles, as badges on titles, and as beacons.</summary>
    Marks = 16,

    All = Files | Icons | Details | FolderCounts | Marks
}

/// <summary>What the layers are called, and how the ones switched off are written into the workspace.</summary>
public static class CanvasLayers
{
    /// <summary>Every layer, in the order the menus and Settings list them.</summary>
    public static IReadOnlyList<CanvasLayer> Each { get; } =
        [CanvasLayer.Files, CanvasLayer.Icons, CanvasLayer.Details, CanvasLayer.FolderCounts, CanvasLayer.Marks];

    /// <summary>A layer's name as the menus and Settings show it.</summary>
    public static string Describe(CanvasLayer layer) => layer switch
    {
        CanvasLayer.Files => "Files",
        CanvasLayer.Icons => "Icons",
        CanvasLayer.Details => "Details",
        CanvasLayer.FolderCounts => "Folder counts",
        CanvasLayer.Marks => "Marks and notes",
        _ => layer.ToString()
    };

    /// <summary>What a layer shows, for a menu's tip and a Settings card.</summary>
    public static string Explain(CanvasLayer layer) => layer switch
    {
        CanvasLayer.Files => "File tiles. Off, each folder shows only its sub-folders, which then fill it",
        CanvasLayer.Icons => "The icons of files and folders beside their names",
        CanvasLayer.Details => "The size, date or type a file's tile shows beside its name",
        CanvasLayer.FolderCounts => "How many folders and files a folder holds, on its title",
        CanvasLayer.Marks => "Colours, notes and pins on folders and files, and the beacons that find them",
        _ => string.Empty
    };

    /// <summary>
    /// The layers switched off, by name, for <see cref="WorkspaceState.CanvasLayersOff"/>;
    /// null when every layer is on.  Written by what is off rather than by
    /// what is on, so a layer added later starts on for everybody.
    /// </summary>
    public static List<string>? OffSetting(CanvasLayer shown)
    {
        var off = Each.Where(layer => (shown & layer) == 0).Select(layer => layer.ToString()).ToList();
        return off.Count == 0 ? null : off;
    }

    /// <summary>
    /// The layers shown, from the names of those switched off.  Missing or
    /// empty, every layer shows; a name not understood is left out, so a
    /// workspace from a later version never switches off anything by mistake.
    /// </summary>
    public static CanvasLayer Parse(IEnumerable<string>? off)
    {
        var shown = CanvasLayer.All;
        if (off is null)
        {
            return shown;
        }

        foreach (var name in off)
        {
            if (Enum.TryParse<CanvasLayer>(name?.Trim(), ignoreCase: true, out var layer)
                && Each.Contains(layer))
            {
                shown &= ~layer;
            }
        }

        return shown;
    }
}
