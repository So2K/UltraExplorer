using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The canvas's layers, each switched off and on again: the names written
/// into the workspace and read back; the files gone from every folder - not
/// drawn, not laid out, not hit, selected or stepped to - each folder's
/// sub-folders filling the whole of it, the count still on its title, and
/// everything exactly where it was once they are back; and the icons, the
/// details, the counts and the marks gone from the label calls that both
/// renderers draw from, and from WPF's picture.  The graphics card's side is
/// in the GPU label checks, which draw it.
/// </summary>
internal static partial class Program
{
    private static async Task LayerChecks()
    {
        LayerModelChecks();
        await LayerStoreChecks();
        RunOnSta("canvas layers", LayersOnStaAsync);
    }

    // ---- the setting -------------------------------------------------------------------

    private static void LayerModelChecks()
    {
        Section("layers: the setting");
        Check("every layer is on unless the workspace says otherwise",
            CanvasLayers.Parse(null) == CanvasLayer.All && CanvasLayers.Parse([]) == CanvasLayer.All && CanvasLayers.OffSetting(CanvasLayer.All) is null);
        var shown = CanvasLayer.All & ~CanvasLayer.Files & ~CanvasLayer.Marks;
        var written = CanvasLayers.OffSetting(shown);
        Check("the layers switched off are written by name, and read back as they were",
            written is ["Files", "Marks"] && CanvasLayers.Parse(written) == shown);
        Check("each layer on its own goes and comes back",
            CanvasLayers.Each.All(layer => CanvasLayers.Parse(CanvasLayers.OffSetting(CanvasLayer.All & ~layer)) == (CanvasLayer.All & ~layer)));
        Check("a name not understood switches nothing off, whatever its case",
            CanvasLayers.Parse(["Sparkles", "", "icons"]) == (CanvasLayer.All & ~CanvasLayer.Icons));
        Check("every layer has a name and words for the menus",
            CanvasLayers.Each.Select(CanvasLayers.Describe).SequenceEqual(["Files", "Icons", "Details", "Folder counts", "Marks and notes"])
            && CanvasLayers.Each.All(layer => CanvasLayers.Explain(layer).Length > 0));
    }

    /// <summary>The layers through the workspace file and back, and a workspace from before there were any.</summary>
    private static async Task LayerStoreChecks()
    {
        var folder = Path.Combine(Path.GetTempPath(), "UltraExplorerLayerStore", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(folder, "workspace.json");
            var store = new WorkspaceStore(path);
            var shown = CanvasLayer.All & ~CanvasLayer.Files & ~CanvasLayer.Details;
            await store.SaveAsync(new WorkspaceState { CanvasLayersOff = CanvasLayers.OffSetting(shown) });
            var text = await File.ReadAllTextAsync(path);
            var back = CanvasLayers.Parse((await store.LoadAsync())?.CanvasLayersOff);
            Check("the layers switched off go into workspace.json and come back from it",
                back == shown && text.Contains("\"CanvasLayersOff\"", StringComparison.Ordinal) && text.Contains("\"Details\"", StringComparison.Ordinal));

            await File.WriteAllTextAsync(path, """{ "SchemaVersion": 2, "CanvasSort": "Name" }""");
            Check("a workspace from before there were layers shows every one",
                CanvasLayers.Parse((await store.LoadAsync())?.CanvasLayersOff) == CanvasLayer.All);
        }
        finally
        {
            TryDelete(folder);
        }
    }

    // ---- on a tree and a canvas --------------------------------------------------------

    private static async Task LayersOnStaAsync()
    {
        var disk = BuildOrderWorld();
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive, "1 TB free")]);
        await LoadEverythingAsync(tree, _ => true);
        var canvas = new NestedCanvas { Tree = tree, DpiOverride = new DpiScale(1, 1) };
        canvas.Measure(new Size(ViewWidth, ViewHeight));
        canvas.Arrange(new Rect(0, 0, ViewWidth, ViewHeight));
        canvas.UpdateLayout();
        try
        {
            await FilesLayerChecksAsync(canvas, tree, disk);
            LabelLayerChecks(canvas, tree);
            MarkLayerChecks(canvas, tree);
        }
        finally
        {
            canvas.ShownLayers = CanvasLayer.All;
            canvas.Tree = null;
        }
    }

    /// <summary>Where everything in a folder was placed: its sub-folders and their places, its files and both grids.</summary>
    private sealed record LayerPlacement(NestedFolder[] Children, (double X, double Y, double Scale)[] Places, NestedGrid Grid, NestedFile[] Files, NestedFileGrid FileGrid);

    private static LayerPlacement PlacementOf(NestedFolder folder) => new(
        [.. folder.Children],
        [.. folder.Children.Select(child => (child.OffsetX, child.OffsetY, child.Scale))],
        folder.Grid,
        [.. folder.Files],
        folder.FileGrid);

    private static bool SamePlacement(LayerPlacement left, LayerPlacement right) =>
        left.Children.SequenceEqual(right.Children) && left.Places.SequenceEqual(right.Places) && left.Grid == right.Grid
        && left.Files.SequenceEqual(right.Files) && left.FileGrid == right.FileGrid;

    /// <summary>What is wrong with a folder with the files layer off: a file shown, its count lost, or its sub-folders not filling it.</summary>
    private static string? FilesOffProblem(NestedFolder folder, int fileCount)
    {
        if (folder.Files.Count != 0 || !folder.FileGrid.IsEmpty)
        {
            return $"{folder}: {folder.Files.Count} files still shown";
        }

        if (folder.FileCount != fileCount)
        {
            return $"{folder}: counts {folder.FileCount} files, read {fileCount}";
        }

        if (!folder.IsComputer && folder.Grid != NestedLayout.GridFor(folder.Children.Count, NestedLayout.ContentHeight, folder.Grid.DownFirst))
        {
            return $"{folder}: its sub-folders do not fill the whole of it";
        }

        return PlacementProblem(folder);
    }

    private static async Task FilesLayerChecksAsync(NestedCanvas canvas, NestedTree tree, FakeDisk disk)
    {
        Section("layers: files");
        var p = tree.Find(@"Q:\p")!;
        var s = tree.Find(@"Q:\s")!;
        var big = tree.Find(@"Q:\big")!;
        var folders = Descendants(tree.Root, includeHidden: true).Prepend(tree.Root).Where(folder => folder.LayoutSortGeneration >= 0).ToList();
        var before = folders.ToDictionary(folder => folder, PlacementOf);
        var counts = folders.ToDictionary(folder => folder, folder => folder.FileCount);
        var pGrid = p.Grid;
        var pFiles = p.FileGrid;
        var firstFile = p.Files[0].Name;
        canvas.FlyTo(p, 0.9, animated: false);
        Render(canvas);
        var pRect = canvas.ScreenRectOf(p)!.Value;

        var sortEvents = 0;
        void OnSort() => sortEvents++;
        tree.SortChanged += OnSort;
        try
        {
            canvas.ShownLayers = CanvasLayer.All & ~CanvasLayer.Files;
            var passStarted = tree.IsSorting;
            await tree.WhenSortIdleAsync();
            Render(canvas);
            Check("the files switched off, the tree places every folder again in the background, as for a change of fill order, and the folder in view stays put",
                !tree.ShowFiles && passStarted && sortEvents == 1 && SameRect(canvas.ScreenRectOf(p), pRect, 1e-9));
        }
        finally
        {
            tree.SortChanged -= OnSort;
        }

        var wrong = folders.Select(folder => FilesOffProblem(folder, counts[folder])).FirstOrDefault(problem => problem is not null);
        Check($"no folder shows a file, keeps its count and is placed right, its sub-folders in the whole of it ({folders.Count} folders){(wrong is null ? string.Empty : ": " + wrong)}",
            wrong is null);
        Check("the sub-folders of a folder that held files grow into the room the files had",
            p.Grid.Scale > pGrid.Scale && p.Grid == NestedLayout.GridFor(p.Children.Count, NestedLayout.ContentHeight, downFirst: true));
        Check("its title still counts its files", canvas.DetailText(p) == "13 folders  ·  40 files");
        Shoot(canvas, "layers-files-off.png");

        // Where the first file was is a sub-folder or the folder now, never a file.
        var (fileX, fileY) = pFiles.Origin(0);
        var where = At(pRect, fileX + pFiles.TileWidth / 2, fileY + pFiles.TileHeight / 2);
        Check("a click where a file was finds no file", canvas.HitTest(where) is { IsFile: false });

        string? selected = null;
        void OnSelect(string path, bool _) => selected = path;
        canvas.SelectRequested += OnSelect;
        try
        {
            canvas.SetSelection([p.Children[^1].FullPath], p.Children[^1].FullPath);
            canvas.HandleKey(Key.Down, ModifierKeys.None);
            Check("Down from the last sub-folder steps to no file", selected is null || tree.Find(selected) is not null);

            canvas.Pointer.Drag(At(pRect, MarginX, NestedLayout.HeaderHeight + 0.01), At(pRect, 1 - MarginX, NestedLayout.CellHeight - 0.01));
            var picked = Picked(canvas);
            Check($"a rectangle over the whole folder takes its sub-folders and nothing else ({picked.Count})",
                picked.Count == p.Children.Count && picked.All(path => tree.Find(path) is not null));
            Clear(canvas);
        }
        finally
        {
            canvas.SelectRequested -= OnSelect;
        }

        // A folder of files alone says what it holds in its middle.
        canvas.FlyTo(big, 0.9, animated: false);
        Render(canvas);
        var bigNote = $"{10_000:N0} files";
        Check("a folder of nothing but files says how many it has where they would be",
            canvas.RecordLabelCallsForTests().Texts.Any(text => text.Text == bigNote));

        // Read again while the files are off: counted, not shown.
        disk.AddFile(@"Q:\s", "late.txt", 10);
        await tree.RefreshAsync(s);
        Check("a folder read again with the files off counts the new file and shows none",
            s.FileCount == 10 && s.Files.Count == 0 && s.FileGrid.IsEmpty && PlacementProblem(s) is null);
        disk.Folder(@"Q:\s").Files.RemoveAll(file => file.Name == "late.txt");
        await tree.RefreshAsync(s);

        canvas.ShownLayers = CanvasLayer.All;
        await tree.WhenSortIdleAsync();
        var moved = folders.Where(folder => !SamePlacement(PlacementOf(folder), before[folder])).Select(folder => folder.FullPath).FirstOrDefault();
        Check($"the files switched back on, every folder is placed exactly as it was{(moved is null ? string.Empty : ": not " + moved)}",
            tree.ShowFiles && moved is null);
        canvas.FlyTo(p, 0.9, animated: false);
        Render(canvas);
        pRect = canvas.ScreenRectOf(p)!.Value;
        Shoot(canvas, "layers-files-on.png");
        Check("and a click on the first tile finds the first file again",
            canvas.HitTest(At(pRect, fileX + pFiles.TileWidth / 2, fileY + pFiles.TileHeight / 2)) is { IsFile: true } hit && hit.FileName == firstFile);

        // A file selected when the files go keeps waiting in the window's
        // selection, and is drawn selected again once they are back.
        var chosen = p.PathOf(p.Files[0]);
        canvas.SetSelection([chosen], chosen);
        Render(canvas);
        canvas.ShownLayers = CanvasLayer.All & ~CanvasLayer.Files;
        await tree.WhenSortIdleAsync();
        Render(canvas);
        var whileOff = Picked(canvas);
        canvas.ShownLayers = CanvasLayer.All;
        await tree.WhenSortIdleAsync();
        Render(canvas);
        Check("a file selected before the files went is not drawn while they are off, and is selected again once they are back",
            !whileOff.Contains(chosen) && Picked(canvas).SetEquals([chosen]));
        Clear(canvas);

        // A filter judges the files again as they go and come back: none of
        // them matches while they are off, and all of them do again after.
        canvas.SetFilter("*.png");
        var pngs = canvas.FilterMatches.Count;
        canvas.ShownLayers = CanvasLayer.All & ~CanvasLayer.Files;
        await tree.WhenSortIdleAsync();
        await Task.Delay(50);
        var pngsOff = canvas.FilterMatches.Count;
        canvas.ShownLayers = CanvasLayer.All;
        await tree.WhenSortIdleAsync();
        await Task.Delay(50);
        Check($"a filter matches no file while the files are off, and the files again once they are back ({pngs}, {pngsOff}, {canvas.FilterMatches.Count})",
            pngs == 10 && pngsOff == 0 && canvas.FilterMatches.Count == pngs);
        canvas.SetFilter(null);
    }

    /// <summary>What the names say with each layer off: the same calls go to WPF's labels and to the graphics card's.</summary>
    private static void LabelLayerChecks(NestedCanvas canvas, NestedTree tree)
    {
        Section("layers: icons, details and counts");
        var s = tree.Find(@"Q:\s")!;
        var icon = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        icon.Freeze();
        canvas.IconLookup = (_, _) => icon;
        canvas.FlyTo(s, 0.9, animated: false);
        Render(canvas);

        var counts = canvas.DetailText(s);
        var sizes = s.Files.Select(file => canvas.FileDetailText(SortColumn.Name, file, dateOnly: false)).ToHashSet(StringComparer.Ordinal);
        var names = s.Files.Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
        (bool Icons, bool Glyphs, bool Sizes, bool Counts, bool Names) Say(CanvasLayer shown)
        {
            canvas.ShownLayers = shown;
            Render(canvas);
            var calls = canvas.RecordLabelCallsForTests();
            return (
                calls.Icons > 0,
                calls.Texts.Any(text => text.Face == LabelFace.Icons && text.Text == "\uE8B7"),
                calls.Texts.Any(text => text.Face == LabelFace.Regular && sizes.Contains(text.Text)),
                calls.Texts.Any(text => text.Text == counts),
                names.All(name => calls.Texts.Any(text => text.Text == name)));
        }

        Check($"with every layer on, the files have icons and sizes, the folders glyphs, and the title its counts ({counts})",
            Say(CanvasLayer.All) == (true, true, true, true, true));
        Check("Icons off: no icon on a file and no glyph on a title, every name and size still there",
            Say(CanvasLayer.All & ~CanvasLayer.Icons) == (false, false, true, true, true));
        Check("Details off: no size on a tile, the rest still there",
            Say(CanvasLayer.All & ~CanvasLayer.Details) == (true, true, false, true, true));
        Check("Folder counts off: no counts on a title, the rest still there",
            Say(CanvasLayer.All & ~CanvasLayer.FolderCounts) == (true, true, true, false, true));

        var p = tree.Find(@"Q:\p")!;
        var child = p.Children.First(folder => folder.ModifiedTicks > 0);
        tree.Orders.SetFolder(p.FullPath, new ItemSort(SortColumn.Modified, true));
        var dated = canvas.TitleDetailForTests(child);
        canvas.ShownLayers = CanvasLayer.All;
        var withCounts = canvas.TitleDetailForTests(child);
        tree.Orders.ResetFolder(p.FullPath);
        Check("without the counts, a title keeps the date a date order is about",
            dated == canvas.DateText(child.ModifiedTicks, dateOnly: false) && withCounts.StartsWith("2 files", StringComparison.Ordinal) && withCounts.EndsWith(dated, StringComparison.Ordinal));

        // WPF's own labels, as a snapshot draws them.
        canvas.RenderLabelsForTests(inMotion: false, asInFrameLoop: false);
        var iconsOn = canvas.LabelIconsDrawn;
        canvas.ShownLayers = CanvasLayer.All & ~CanvasLayer.Icons;
        Render(canvas);
        canvas.RenderLabelsForTests(inMotion: false, asInFrameLoop: false);
        var iconsOff = canvas.LabelIconsDrawn;
        canvas.ShownLayers = CanvasLayer.All;
        Render(canvas);
        Check($"WPF's labels draw the icons ({iconsOn}) and leave them out with the layer off ({iconsOff})", iconsOn > 0 && iconsOff == 0);

        var everything = Shoot(canvas, "layers-all.png");
        canvas.ShownLayers = CanvasLayer.All & ~CanvasLayer.Details & ~CanvasLayer.FolderCounts;
        var fewer = Shoot(canvas, "layers-no-details.png");
        canvas.ShownLayers = CanvasLayer.All;
        Check("and the picture of the canvas loses the words", CountDifferent(everything, fewer) > 200);
        canvas.IconLookup = null;
    }

    private static int CountDifferent(BitmapSource left, BitmapSource right)
    {
        var width = left.PixelWidth;
        var height = left.PixelHeight;
        var a = new uint[width * height];
        var b = new uint[width * height];
        left.CopyPixels(a, width * 4, 0);
        right.CopyPixels(b, width * 4, 0);
        var different = 0;
        for (var index = 0; index < a.Length; index++)
        {
            different += a[index] != b[index] ? 1 : 0;
        }

        return different;
    }

    /// <summary>Colours, notes and pins: on the cells, the titles and the tiles, and as beacons; the selection's beacon stays.</summary>
    private static void MarkLayerChecks(NestedCanvas canvas, NestedTree tree)
    {
        Section("layers: marks and notes");
        var s = tree.Find(@"Q:\s")!;
        var big = tree.Find(@"Q:\big")!;
        var marked = s.Children[0];
        var pinned = s.Children[1];
        var file = s.PathOf(s.Files[0]);
        var marks = new Dictionary<string, FolderMark>(StringComparer.OrdinalIgnoreCase)
        {
            [marked.FullPath] = new("#EF5A68", "look here"),
            [file] = new("#4ED6A0", "and here")
        };
        canvas.MarkLookup = path => marks.TryGetValue(path, out var mark) ? mark : FolderMark.None;
        canvas.SetBeacons([new NestedBeacon(pinned.FullPath, NestedBeaconKind.Pinned, Colors.Gold, pinned.Name)]);
        canvas.FlyTo(s, 0.9, animated: false);
        Render(canvas);

        (bool Coloured, bool Noted, bool Pinned, bool Edge, int Names) Say()
        {
            Render(canvas);
            var calls = canvas.RecordLabelCallsForTests();
            return (
                marked.HasLabel,
                calls.Texts.Any(text => text.Face == LabelFace.Icons && text.Text.Contains('\uE70B')),
                calls.Texts.Any(text => text.Face == LabelFace.Icons && text.Text.Contains('\uE735')),
                calls.Rectangles > 0,
                calls.Texts.Count(text => text.Face != LabelFace.Icons));
        }

        var on = Say();
        var stripe = marked.StripeColour;
        canvas.ShownLayers = CanvasLayer.All & ~CanvasLayer.Marks;
        var off = Say();
        Check("with marks on, the marked folder has its colour, the titles their note and pin, the file its colour down its edge",
            on is { Coloured: true, Noted: true, Pinned: true, Edge: true });
        Check("marks off: no colour on the cell, no note or pin on a title, no colour on a tile - and every name still written",
            off is { Coloured: false, Noted: false, Pinned: false, Edge: false } && off.Names == on.Names && marked.StripeColour != stripe);

        // Beacons, over a view where the files are specks.
        canvas.ShownLayers = CanvasLayer.All;
        canvas.FitAll(animated: false);
        canvas.SetBeacons(
        [
            new NestedBeacon(big.PathOf(big.Files[5]), NestedBeaconKind.Colour, Colors.Red, "marked"),
            new NestedBeacon(big.PathOf(big.Files[9_000]), NestedBeaconKind.Active, Colors.DeepSkyBlue, "selected")
        ]);
        Render(canvas);
        var beaconsOn = canvas.BeaconsPlaced;
        canvas.ShownLayers = CanvasLayer.All & ~CanvasLayer.Marks;
        Render(canvas);
        var beaconsOff = canvas.BeaconsPlaced;
        canvas.ShownLayers = CanvasLayer.All;
        Render(canvas);
        Check($"a mark's beacon goes with the marks and the selection's stays ({beaconsOn} with them, {beaconsOff} without)",
            beaconsOn == 2 && beaconsOff == 1 && canvas.BeaconsPlaced == 2);
        canvas.SetBeacons([]);
        canvas.MarkLookup = null;
    }
}
