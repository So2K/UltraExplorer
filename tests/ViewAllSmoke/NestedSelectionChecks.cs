using System.Diagnostics;
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
/// Selecting several items on the nested canvas: the block arithmetic a
/// selection rectangle rests on, what a press turns into, the rectangle's
/// three modes and its Esc, ranges and the anchor under every order, the
/// keys, the filter, the view sliding on at an edge and the wheel mid-drag,
/// the right button, the "pan" setting, the pixels, and what ten thousand
/// selected items cost.  All of it driven through the canvas's own mouse
/// handlers with synthetic points (<see cref="NestedPointer"/>) and, where
/// time matters, frames on a clock of the test's own.
/// </summary>
internal static partial class Program
{
    private const string SelectionRoot = @"Q:\sel";

    /// <summary>Time the checks' stand-in for the window spent applying the canvas's edits: the window's side, not the canvas's.</summary>
    private static double _sharedApplyMilliseconds;

    /// <summary>Budgets are for an optimised build; a debug build of the app gets this many times as long.</summary>
#if DEBUG
    private const double SelectionBudgetFactor = 5;
#else
    private const double SelectionBudgetFactor = 1;
#endif

    private static Task NestedSelectionChecks()
    {
        GridBlockChecks();
        RunOnSta("nested selection", NestedSelectionOnStaAsync);
        return Task.CompletedTask;
    }

    // ---- 1. block arithmetic ----------------------------------------------------------

    /// <summary>
    /// <see cref="GridBlock.Touching"/> against brute force: grids of every
    /// shape the layout makes, rectangles anywhere - no area, sharing only an
    /// edge, a million grids away - and the count of what is inside.
    /// </summary>
    private static void GridBlockChecks()
    {
        Section("nested selection: blocks");
        var random = new Random(4242);
        int[] counts = [1, 2, 3, 7, 64, 65, 999, 1003, 2500, 50_000];
        var cases = 0;
        var mismatches = 0;
        var countMismatches = 0;
        string? first = null;
        foreach (var count in counts)
        {
            var grid = NestedLayout.GridFor(count);
            var files = NestedLayout.FileGridFor(count, NestedLayout.HeaderHeight + 0.1, NestedLayout.ContentHeight - 0.1);
            for (var trial = 0; trial < 200; trial++)
            {
                var (left, top, right, bottom) = RandomUnitRect(random, trial);
                foreach (var zone in new[] { 0, 1 })
                {
                    cases++;
                    GridBlock block;
                    int columns;
                    Func<int, (double X, double Y)> origin;
                    double width, height;
                    if (zone == 0)
                    {
                        block = grid.Touching(left, top, right, bottom);
                        columns = grid.Columns;
                        origin = grid.Origin;
                        width = grid.Scale;
                        height = grid.Scale * NestedLayout.CellHeight;
                    }
                    else
                    {
                        block = files.Touching(left, top, right, bottom);
                        columns = files.Columns;
                        origin = files.Origin;
                        width = files.TileWidth;
                        height = files.TileHeight;
                    }

                    var expected = 0;
                    var wrong = false;
                    var hasArea = right > left && bottom > top;
                    for (var index = 0; index < count; index++)
                    {
                        var (x, y) = origin(index);
                        var touches = hasArea && x < right && x + width > left && y < bottom && y + height > top;
                        expected += touches ? 1 : 0;
                        if (touches != block.Contains(index, columns))
                        {
                            wrong = true;
                        }
                    }

                    if (wrong)
                    {
                        mismatches++;
                        first ??= $"{count} items, zone {zone}, rect ({left:0.####}, {top:0.####})-({right:0.####}, {bottom:0.####}) gave {block}";
                    }

                    if (block.CountIn(count, columns) != expected)
                    {
                        countMismatches++;
                    }
                }
            }
        }

        Check($"a rectangle's block is exactly the items it touches, in {cases:N0} cases over grids of 1 to 50,000 ({mismatches} wrong)", mismatches == 0);
        if (first is not null)
        {
            Console.WriteLine($"        first: {first}");
        }

        Check($"and counting a block, short last row and all, is exact ({countMismatches} wrong)", countMismatches == 0);

        // Exact binary fractions, so the edges really are shared.
        var gap = GridBlock.Touching(10, 5, 2, 0.25, 0.5, 0.25, 0.25, 0.125, 0.125, 0.375, 0.5, 0.5, 0.625);
        var rowGap = GridBlock.Touching(10, 5, 2, 0.25, 0.5, 0.25, 0.25, 0.125, 0.125, 0.25, 0.625, 0.375, 0.75);
        var corner = GridBlock.Touching(10, 5, 2, 0.25, 0.5, 0.25, 0.25, 0.125, 0.125, 0.25, 0.5, 0.375, 0.625);
        Check("a rectangle that only shares an edge with an item, across or down, touches nothing", gap.IsEmpty && rowGap.IsEmpty);
        Check("one that covers the item exactly touches it and nothing else", corner == new GridBlock(0, 0, 0, 0));
        Check("a rectangle with no area touches nothing",
            GridBlock.Touching(10, 5, 2, 0.25, 0.5, 0.25, 0.25, 0.125, 0.125, 0.3, 0.55, 0.3, 0.7).IsEmpty);
        var far = GridBlock.Touching(50_000, 300, 167, 0.025, 0.1, 0.003, 0.002, 0.0025, 0.0015, -1e12, -1e12, 1e12, 1e12);
        var beyond = GridBlock.Touching(50_000, 300, 167, 0.025, 0.1, 0.003, 0.002, 0.0025, 0.0015, 1e12, 1e12, 2e12, 2e12);
        Check("a rectangle a million grids across holds everything, and one a million grids away nothing, without overflowing",
            far == new GridBlock(0, 299, 0, 166) && far.CountIn(50_000, 300) == 50_000 && beyond.IsEmpty);
    }

    private static (double Left, double Top, double Right, double Bottom) RandomUnitRect(Random random, int trial)
    {
        double Coordinate(double span) => trial % 17 == 0 ? (random.NextDouble() - 0.5) * 1e6 : random.NextDouble() * span * 1.2 - 0.1;
        var x0 = Coordinate(1);
        var y0 = Coordinate(NestedLayout.CellHeight);
        var x1 = trial % 23 == 0 ? x0 : Coordinate(1);
        var y1 = trial % 29 == 0 ? y0 : Coordinate(NestedLayout.CellHeight);
        return (Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
    }

    // ---- on a canvas ------------------------------------------------------------------

    private static async Task NestedSelectionOnStaAsync()
    {
        var disk = BuildSelectionWorld();
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots(
        [
            new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive, "1 TB free"),
            new NestedRoot(@"R:\", "R:", NestedFolderKind.Drive, "1 TB free")
        ]);
        await LoadEverythingAsync(tree, _ => true);

        var canvas = new NestedCanvas { Tree = tree, DpiOverride = new DpiScale(1, 1) };
        canvas.Measure(new Size(ViewWidth, ViewHeight));
        canvas.Arrange(new Rect(0, 0, ViewWidth, ViewHeight));
        canvas.UpdateLayout();

        // The window's side: every edit the canvas makes is applied to one
        // shared selection, and the canvas told the version is its own.
        var shared = new ItemSelection();
        var edits = 0;
        canvas.SelectionCommitted += edit =>
        {
            edits++;
            var applying = Stopwatch.GetTimestamp();
            shared.Apply(edit);
            _sharedApplyMilliseconds += Stopwatch.GetElapsedTime(applying).TotalMilliseconds;
            canvas.AcknowledgeSelection(shared.Version);
        };

        try
        {
            await SelectionHitChecks(canvas, tree);
            SelectionPressChecks(canvas, tree);
            await SelectionModeChecks(canvas, tree, disk, shared);
            await SelectionRangeChecks(canvas, tree);
            SelectionAnchorChecks(canvas, tree, shared);
            SelectionKeyChecks(canvas, tree);
            SelectionFilterChecks(canvas, tree);
            SelectionEdgeChecks(canvas, tree);
            SelectionWheelChecks(canvas, tree);
            SelectionButtonChecks(canvas, tree);
            SelectionPixelChecks(canvas, tree);
            SelectionRoundTripChecks(canvas, tree, shared, ref edits);
            SelectionScaleChecks(canvas, tree);
            await SelectionGoneChecks(canvas, tree, disk);
            SelectionPendingChecks(tree);
            SelectionShareRootChecks();
        }
        finally
        {
            canvas.SetFilter(null);
            canvas.Tree = null;
        }
    }

    /// <summary>
    /// Q:\sel: 37 sub-folders and 1,003 files of six kinds, with dates and
    /// sizes out of name order and a few of each hidden; Q:\big: ten thousand
    /// files; Q:\few: five files, tall enough to pick up; Q:\other and
    /// Q:\doomed for selections elsewhere and a folder that goes away.
    /// </summary>
    private static FakeDisk BuildSelectionWorld()
    {
        var disk = new FakeDisk();
        disk.Folder(@"Q:\");
        disk.Folder(@"R:\");
        string[] kinds = ["txt", "log", "png", "cs", "md", "dat"];
        for (var index = 0; index < 37; index++)
        {
            var folder = disk.Folder($@"{SelectionRoot}\d{index:D2}");
            folder.Modified = SortEpoch.AddDays(index * 13 % 37);
            folder.IsHidden = index % 12 == 11;
            disk.AddFiles($@"{SelectionRoot}\d{index:D2}", 3, "x");
            disk.Folder($@"{SelectionRoot}\d{index:D2}\inner");
        }

        for (var index = 0; index < 1_003; index++)
        {
            disk.AddFile(
                SelectionRoot,
                $"f{index:D4}.{kinds[index % kinds.Length]}",
                1_000L * (index * 7919 % 1_003),
                hidden: index % 97 == 5,
                modified: SortEpoch.AddMinutes(index * 31 % 1_003));
        }

        for (var index = 0; index < 10_000; index++)
        {
            disk.AddFile(@"Q:\big", $"b{index:D5}.e{index % 40:D2}", index * 3L, modified: SortEpoch.AddHours(index * 7 % 26_280));
        }

        disk.AddFiles(@"Q:\few", 5, "few");
        disk.AddFiles(@"Q:\other", 12, "o");
        disk.Folder(@"Q:\other\o-folder");
        disk.AddFiles(@"Q:\doomed", 20, "z");
        return disk;
    }

    private static HashSet<string> Picked(NestedCanvas canvas) =>
        canvas.SelectionState.Items().Select(key => key.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static Point At(Rect cell, double x, double y) => new(cell.X + x * cell.Width, cell.Y + y * cell.Width);

    /// <summary>What a rectangle over <paramref name="folder"/> between two points of its unit frame touches, by brute force.</summary>
    private static HashSet<string> Touched(NestedFolder folder, double x0, double y0, double x1, double y1, Func<string, bool>? matches = null)
    {
        var (left, right) = (Math.Min(x0, x1), Math.Max(x0, x1));
        var (top, bottom) = (Math.Min(y0, y1), Math.Max(y0, y1));
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!(right > left) || !(bottom > top))
        {
            return touched;
        }

        var grid = folder.Grid;
        for (var index = 0; index < folder.Children.Count; index++)
        {
            var (x, y) = grid.Origin(index);
            if (x < right && x + grid.Scale > left && y < bottom && y + grid.Scale * NestedLayout.CellHeight > top
                && (matches is null || matches(folder.Children[index].Name)))
            {
                touched.Add(folder.Children[index].FullPath);
            }
        }

        var files = folder.FileGrid;
        for (var index = 0; index < folder.Files.Count; index++)
        {
            var (x, y) = files.Origin(index);
            if (x < right && x + files.TileWidth > left && y < bottom && y + files.TileHeight > top
                && (matches is null || matches(folder.Files[index].Name)))
            {
                touched.Add(folder.PathOf(folder.Files[index]));
            }
        }

        return touched;
    }

    /// <summary>A point in the container's left margin: inside it, on nothing of its own, below its title.</summary>
    private static double MarginX => NestedLayout.Padding / 2;

    private static void Clear(NestedCanvas canvas)
    {
        canvas.HandleKey(Key.Escape, ModifierKeys.None);
        Render(canvas);
    }

    // ---- 2. what a rectangle touches -------------------------------------------------

    private static async Task SelectionHitChecks(NestedCanvas canvas, NestedTree tree)
    {
        Section("nested selection: what a rectangle touches");
        var sel = tree.Find(SelectionRoot)!;
        canvas.FlyTo(sel, 0.9, animated: false);
        Render(canvas);
        var cell = canvas.ScreenRectOf(sel)!.Value;
        var pointer = canvas.Pointer;
        var random = new Random(11);
        var wrong = 0;
        var inside = 0;
        string? firstWrong = null;
        for (var sweep = 0; sweep < 20; sweep++)
        {
            var fromRight = sweep % 4 == 3;
            var startX = fromRight ? 1 - MarginX : MarginX;
            var startY = NestedLayout.HeaderHeight + 0.02 + random.NextDouble() * (NestedLayout.ContentHeight - 0.04);
            var endX = 0.05 + random.NextDouble() * 0.9;
            var endY = NestedLayout.HeaderHeight + random.NextDouble() * NestedLayout.ContentHeight;
            pointer.Drag(At(cell, startX, startY), At(cell, endX, endY));
            var picked = Picked(canvas);
            var expected = Touched(sel, startX, startY, endX, endY);
            if (!picked.SetEquals(expected))
            {
                wrong++;
                firstWrong ??= $"sweep {sweep}: {picked.Count} picked, {expected.Count} touched";
            }

            if (canvas.SelectionState.Items().Any(key => !ReferenceEquals(key.Container, sel)))
            {
                inside++;
            }
        }

        Check($"twenty sweeps over 37 sub-folders and 1,003 files select exactly what they touch ({wrong} wrong)", wrong == 0);
        if (firstWrong is not null)
        {
            Console.WriteLine($"        first: {firstWrong}");
        }

        Check("and never anything inside the sub-folders", inside == 0);

        // Moving the corner with the camera still: no new scene, and a frame
        // whose blocks did not change records nothing again.  Long enough
        // after the flight for the frames to be at rest.
        await Task.Delay(400);
        var start = At(cell, MarginX, 0.2);
        pointer.Down(MouseButton.Left, start);
        pointer.Move(At(cell, 0.5, 0.4));
        canvas.RunFrameForTests(TimeSpan.FromSeconds(1000));
        var scenes = canvas.RenderCount;
        var recorded = canvas.SelectionRecordCount;
        var block = canvas.Marquee!.Files;
        var nudge = At(cell, 0.5, 0.4) + new Vector(0.01, 0.01);
        pointer.Move(nudge);
        canvas.RunFrameForTests(TimeSpan.FromSeconds(1000.01));
        Check("a corner moved within the same items redraws no scene and records no selection",
            canvas.RenderCount == scenes && canvas.SelectionRecordCount == recorded && canvas.Marquee!.Files == block);
        pointer.Move(At(cell, 0.8, 0.55));
        canvas.RunFrameForTests(TimeSpan.FromSeconds(1000.02));
        Check("one moved onto new items records the selection again, and still no scene",
            canvas.RenderCount == scenes && canvas.SelectionRecordCount == recorded + 1);
        pointer.Up(MouseButton.Left, At(cell, 0.8, 0.55));
        Clear(canvas);
    }

    // ---- 3. what a press turns into -------------------------------------------------------

    private static void SelectionPressChecks(NestedCanvas canvas, NestedTree tree)
    {
        Section("nested selection: what a press turns into");
        var sel = tree.Find(SelectionRoot)!;
        var pointer = canvas.Pointer;
        string? dragged = null;
        void OnDrag(string path) => dragged = path;
        canvas.DragRequested += OnDrag;
        try
        {
            // Close enough in for the sub-folders to have open space and a
            // title band big enough to grab (under 133 DIPs a cell is all title).
            var child = sel.Children[0];
            canvas.FlyTo(child, 0.25, animated: false);
            Render(canvas);
            var childCell = canvas.ScreenRectOf(child)!.Value;
            Check($"flown near d00, it is {childCell.Width:0} DIPs wide", childCell.Width * NestedLayout.HeaderHeight >= NestedCanvas.HeaderGrabPixels);

            // On a sub-folder's open space: a rectangle in that sub-folder.
            var body = At(childCell, MarginX, 0.45);
            pointer.Down(MouseButton.Left, body);
            pointer.Move(body + new Vector(12, 9));
            Check($"a drag on a sub-folder's open space draws a rectangle in that sub-folder ({pointer.State})",
                ReferenceEquals(canvas.Marquee?.Container, child));
            pointer.Up(MouseButton.Left, body + new Vector(12, 9));

            // In the gap between two sub-folders: the folder around them.
            var next = canvas.ScreenRectOf(sel.Children[sel.Grid.IndexOf(0, 1)])!.Value;
            var gap = new Point((childCell.Right + next.Left) / 2, childCell.Top + childCell.Height / 2);
            pointer.Down(MouseButton.Left, gap);
            pointer.Move(gap + new Vector(0, 20));
            Check("one in the gap between sub-folders draws it in their parent", ReferenceEquals(canvas.Marquee?.Container, sel));
            pointer.Up(MouseButton.Left, gap + new Vector(0, 20));

            // A title band big enough to grab: the folder is picked up.
            dragged = null;
            var title = new Point(childCell.X + childCell.Width / 2, childCell.Y + childCell.Width * NestedLayout.HeaderHeight / 2);
            pointer.Down(MouseButton.Left, title);
            pointer.Move(title + new Vector(10, 10));
            Check("a drag on a folder's title picks the folder up, and draws nothing",
                dragged == child.FullPath && canvas.Marquee is null && pointer.State == "idle");
            pointer.Up(MouseButton.Left, title + new Vector(10, 10));

            // A file tile too small to carry its name: a rectangle in its folder.
            canvas.FlyTo(sel, 0.9, animated: false);
            Render(canvas);
            var cell = canvas.ScreenRectOf(sel)!.Value;
            var files = sel.FileGrid;
            var tile = At(cell, files.Origin(0).X + files.TileWidth / 2, files.Origin(0).Y + files.TileHeight / 2);
            Check($"at nine tenths the files of Q:\\sel are too small to pick up ({files.TileHeight * cell.Width:0.0} DIP)",
                files.TileHeight * cell.Width < NestedCanvas.FileLabelPixels);
            dragged = null;
            pointer.Down(MouseButton.Left, tile);
            pointer.Move(tile + new Vector(30, 20));
            Check("a drag from a small tile draws a rectangle in its folder, and the tile is taken at once",
                ReferenceEquals(canvas.Marquee?.Container, sel) && dragged is null);
            pointer.Up(MouseButton.Left, tile + new Vector(30, 20));
            Check("which it is, once let go", Picked(canvas).Contains(sel.PathOf(sel.Files[0])));

            // A tile tall enough to carry its name is picked up.
            var few = tree.Find(@"Q:\few")!;
            canvas.FlyTo(few, 0.9, animated: false);
            Render(canvas);
            var fewCell = canvas.ScreenRectOf(few)!.Value;
            var bigTile = At(fewCell, few.FileGrid.Origin(1).X + few.FileGrid.TileWidth / 2, few.FileGrid.Origin(1).Y + few.FileGrid.TileHeight / 2);
            dragged = null;
            pointer.Down(MouseButton.Left, bigTile);
            pointer.Move(bigTile + new Vector(10, 0));
            Check("a drag on a tile big enough to read picks the file up",
                dragged == few.PathOf(few.Files[1]) && canvas.Marquee is null);
            pointer.Up(MouseButton.Left, bigTile + new Vector(10, 0));

            // Outside This PC: the view pans by exactly the drag.
            canvas.FitAll(animated: false);
            Render(canvas);
            var before = canvas.ScreenRectOf(tree.Root)!.Value;
            pointer.Drag(new Point(4, 4), new Point(4 + 37.25, 4 + 21.5), steps: 5);
            var after = canvas.ScreenRectOf(tree.Root)!.Value;
            Check("a drag outside This PC pans by exactly the drag, selecting nothing",
                Near(after.X - before.X, 37.25, 1e-9) && Near(after.Y - before.Y, 21.5, 1e-9) && canvas.Marquee is null);
            canvas.FitAll(animated: false);
            Render(canvas);

            // A speck: a rectangle in the folder around it, the speck taken.
            var speck = sel.Children[5];
            var speckRect = canvas.ScreenRectOf(speck)!.Value;
            var selRect = canvas.ScreenRectOf(sel)!.Value;
            Check($"at fit-all the sub-folders of Q:\\sel are too small to grab or draw in ({speckRect.Width:0.0} DIP) and Q:\\sel is not ({selRect.Width:0} DIP)",
                speckRect.Width < NestedCanvas.MinimumContainerPixels && selRect.Width >= NestedCanvas.MinimumContainerPixels);
            var onSpeck = new Point(speckRect.X + speckRect.Width / 2, speckRect.Y + speckRect.Height / 2);
            pointer.Down(MouseButton.Left, onSpeck);
            pointer.Move(onSpeck + new Vector(6, 6));
            Check("a drag from a speck draws a rectangle in the folder around it", ReferenceEquals(canvas.Marquee?.Container, sel));
            pointer.Up(MouseButton.Left, onSpeck + new Vector(6, 6));
            Check("and the speck it started on is selected", Picked(canvas).Contains(speck.FullPath));

            // A folder under 64 DIPs hands the rectangle to the one around it.
            canvas.FlyTo(sel, 0.9, animated: false);
            canvas.ZoomAt(new Point(ViewWidth / 2.0, ViewHeight / 2.0), 0.3);
            Render(canvas);
            var small = sel.Children[0];
            var smallRect = canvas.ScreenRectOf(small)!.Value;
            var smallFile = At(smallRect, small.FileGrid.Origin(0).X + small.FileGrid.TileWidth / 2, small.FileGrid.Origin(0).Y + small.FileGrid.TileHeight / 2);
            var hit = canvas.HitTest(smallFile);
            pointer.Down(MouseButton.Left, smallFile);
            pointer.Move(smallFile + new Vector(8, 8));
            Check($"a drag from a file of a folder {smallRect.Width:0} DIPs wide draws the rectangle in the folder around it",
                smallRect.Width < NestedCanvas.MinimumContainerPixels && hit is { IsFile: true } && ReferenceEquals(hit.Value.Folder, small)
                && ReferenceEquals(canvas.Marquee?.Container, sel));
            pointer.Up(MouseButton.Left, smallFile + new Vector(8, 8));

            // This PC's open space selects drives.
            canvas.FitAll(animated: false);
            Render(canvas);
            var root = canvas.ScreenRectOf(tree.Root)!.Value;
            pointer.Drag(At(root, MarginX, 0.5), At(root, 0.99, 0.2));
            Check("a rectangle over This PC's open space selects drives", Picked(canvas).SetEquals([@"Q:\", @"R:\"]));
            Clear(canvas);
        }
        finally
        {
            canvas.DragRequested -= OnDrag;
        }
    }

    // ---- 4. modes, Esc and the folder going away ----------------------------------------

    private static async Task SelectionModeChecks(NestedCanvas canvas, NestedTree tree, FakeDisk disk, ItemSelection shared)
    {
        Section("nested selection: replace, add, toggle and Esc");
        var sel = tree.Find(SelectionRoot)!;
        var other = tree.Find(@"Q:\other")!;
        canvas.FlyTo(sel, 0.9, animated: false);
        Render(canvas);
        var cell = canvas.ScreenRectOf(sel)!.Value;
        var pointer = canvas.Pointer;

        HashSet<string> Base()
        {
            var baseline = new ItemSelection();
            var chosen = new List<SelectionItem>
            {
                new(other.PathOf(other.Files[2]), false, 0),
                new(other.Children[0].FullPath, true, 0),
                new(sel.Children[1].FullPath, true, 0),
                new(sel.Children[8].FullPath, true, 0)
            };
            for (var index = 0; index < sel.Files.Count; index += 7)
            {
                chosen.Add(new SelectionItem(sel.PathOf(sel.Files[index]), false, 0));
            }

            baseline.Apply(new SelectionEdit { Clear = true, Added = chosen });
            canvas.LoadSelection(baseline);
            Render(canvas);
            return chosen.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        // Every rectangle starts in the folder's margin, on nothing of its own.
        var x0 = MarginX;
        const double y0 = 0.25, x1 = 0.72, y1 = 0.52;
        var touched = Touched(sel, x0, y0, x1, y1);
        Check($"the test rectangle touches sub-folders and files ({touched.Count})", touched.Count > 50);

        // Replace: other folders' items go the moment it starts.
        var before = Base();
        pointer.Down(MouseButton.Left, At(cell, x0, y0));
        pointer.Move(At(cell, x1, y1));
        Render(canvas);
        pointer.Up(MouseButton.Left, At(cell, x1, y1));
        Check("a plain rectangle replaces everything, other folders included", Picked(canvas).SetEquals(touched));
        Check("and the shared selection is the same, as one edit that clears",
            shared.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(Picked(canvas)) && shared.Container == sel.FullPath);

        // Add: what the rectangle leaves goes back to how it was.
        before = Base();
        pointer.Down(MouseButton.Left, At(cell, x0, y0), ModifierKeys.Shift);
        pointer.Move(At(cell, 0.95, 0.6));
        Render(canvas);
        pointer.Move(At(cell, x1, y1));
        Render(canvas);
        pointer.Up(MouseButton.Left, At(cell, x1, y1));
        var added = new HashSet<string>(before, StringComparer.OrdinalIgnoreCase);
        added.UnionWith(touched);
        Check("Shift adds what the rectangle ends on to what was there, and gives back what it passed over",
            Picked(canvas).SetEquals(added));

        // Toggle: base XOR hit, item by item.
        before = Base();
        pointer.Down(MouseButton.Left, At(cell, x0, y0), ModifierKeys.Control);
        pointer.Move(At(cell, x1, y1));
        Render(canvas);
        pointer.Up(MouseButton.Left, At(cell, x1, y1));
        var toggled = new HashSet<string>(before, StringComparer.OrdinalIgnoreCase);
        toggled.SymmetricExceptWith(touched);
        Check("Ctrl flips exactly what the rectangle touches against what was there, other folders kept",
            Picked(canvas).SetEquals(toggled));

        // Esc: everything as it was, other folders included.
        before = Base();
        pointer.Down(MouseButton.Left, At(cell, x0, y0));
        pointer.Move(At(cell, x1, y1));
        Render(canvas);
        var handled = canvas.HandleKey(Key.Escape, ModifierKeys.None);
        pointer.Move(At(cell, 0.9, 0.6));
        pointer.Up(MouseButton.Left, At(cell, 0.9, 0.6));
        Check("Esc during a rectangle lets go of it and brings back exactly the selection from before",
            handled && canvas.Marquee is null && Picked(canvas).SetEquals(before));

        // Losing the mouse keeps what is shown.
        Base();
        pointer.Down(MouseButton.Left, At(cell, x0, y0));
        pointer.Move(At(cell, x1, y1));
        pointer.LoseCapture();
        Check("losing the mouse mid-rectangle commits what it shows", Picked(canvas).SetEquals(touched));

        // Ctrl pressed half way - for the wheel - changes nothing.
        Base();
        pointer.Down(MouseButton.Left, At(cell, x0, y0));
        pointer.Move(At(cell, x1, y1));
        pointer.Wheel(At(cell, x1, y1), 0, ModifierKeys.Control);
        Check("Ctrl pressed during a rectangle does not make it a toggle", canvas.Marquee?.Mode == NestedSelectMode.Replace);
        pointer.Up(MouseButton.Left, At(cell, x1, y1));

        // The folder read away mid-drag.
        var doomed = tree.Find(@"Q:\doomed")!;
        canvas.FlyTo(doomed, 0.9, animated: false);
        Render(canvas);
        var doomedCell = canvas.ScreenRectOf(doomed)!.Value;
        before = Base();
        Render(canvas);
        pointer.Down(MouseButton.Left, At(doomedCell, MarginX, 0.2));
        pointer.Move(At(doomedCell, 0.8, 0.5));
        Check("a rectangle starts in Q:\\doomed", ReferenceEquals(canvas.Marquee?.Container, doomed));
        disk.Remove(@"Q:\doomed");
        await tree.RefreshAsync(tree.Find(@"Q:\")!);
        Render(canvas);
        pointer.Up(MouseButton.Left, At(doomedCell, 0.8, 0.5));
        Check("a folder read away under a rectangle cancels it, and the selection from before stays",
            canvas.Marquee is null && Picked(canvas).SetEquals(before));
        Clear(canvas);
    }

    // ---- 5. ranges under every order ----------------------------------------------------------

    private static async Task SelectionRangeChecks(NestedCanvas canvas, NestedTree tree)
    {
        Section("nested selection: ranges in the order on screen");
        var sel = tree.Find(SelectionRoot)!;

        // Ordering by type waits for the Shell's names for the kinds; they
        // are in before anything is clicked.
        await FileTypeNames.WhenPrefetchedAsync();
        var pointer = canvas.Pointer;
        var failures = new List<string>();
        foreach (var hidden in new[] { false, true })
        {
            tree.IncludeHidden = hidden;
            foreach (var sort in EverySort)
            {
                tree.SetSort(sort);
                tree.FlushSortWork();
                canvas.FlyTo(sel, 0.9, animated: false);
                Render(canvas);
                await tree.WhenSortIdleAsync();
                var cell = canvas.ScreenRectOf(sel)!.Value;
                var k = 3;
                var m = Math.Min(40, sel.Files.Count - 1);
                var expected = sel.Children.Skip(k).Select(child => child.FullPath)
                    .Concat(sel.Files.Take(m + 1).Select(file => sel.PathOf(file)))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var childTitle = TitlePoint(canvas.ScreenRectOf(sel.Children[k])!.Value);
                var fileTile = TilePoint(cell, sel, m);

                pointer.Click(childTitle);
                pointer.Click(fileTile, ModifierKeys.Shift);
                var forward = Picked(canvas);
                pointer.Click(fileTile);
                pointer.Click(childTitle, ModifierKeys.Shift);
                var backward = Picked(canvas);
                if (!forward.SetEquals(expected) || !backward.SetEquals(expected))
                {
                    failures.Add($"{DescribeSort(sort)}{(hidden ? ", hidden shown" : string.Empty)}: {forward.Count}/{backward.Count} of {expected.Count}");
                }
            }
        }

        Check($"from a sub-folder to a file and back is the same range in the order on screen, under all eight orders with hidden items shown and not ({failures.Count} wrong)",
            failures.Count == 0);
        foreach (var failure in failures.Take(3))
        {
            Console.WriteLine($"        {failure}");
        }

        tree.IncludeHidden = false;
        tree.SetSort(ItemSort.Default);
        tree.FlushSortWork();
        await tree.WhenSortIdleAsync();
        Clear(canvas);
    }

    private static Point TitlePoint(Rect cell) => new(cell.X + cell.Width * 0.7, cell.Y + cell.Width * NestedLayout.HeaderHeight / 2);

    private static Point TilePoint(Rect cell, NestedFolder folder, int index)
    {
        var (x, y) = folder.FileGrid.Origin(index);
        return At(cell, x + folder.FileGrid.TileWidth / 2, y + folder.FileGrid.TileHeight / 2);
    }

    // ---- 6. the anchor ---------------------------------------------------------------------

    private static void SelectionAnchorChecks(NestedCanvas canvas, NestedTree tree, ItemSelection shared)
    {
        Section("nested selection: the anchor");
        var sel = tree.Find(SelectionRoot)!;
        canvas.FlyTo(sel, 0.9, animated: false);
        Render(canvas);
        var cell = canvas.ScreenRectOf(sel)!.Value;
        var pointer = canvas.Pointer;
        string File(int index) => sel.PathOf(sel.Files[index]);
        HashSet<string> Files(int from, int to) =>
            Enumerable.Range(Math.Min(from, to), Math.Abs(to - from) + 1).Select(File).ToHashSet(StringComparer.OrdinalIgnoreCase);

        pointer.Click(TilePoint(cell, sel, 10));
        pointer.Click(TilePoint(cell, sel, 20), ModifierKeys.Shift);
        Check("click A, Shift+click B: A to B", Picked(canvas).SetEquals(Files(10, 20)));
        pointer.Click(TilePoint(cell, sel, 5), ModifierKeys.Shift);
        Check("then Shift+click C: A to C, the anchor still A", Picked(canvas).SetEquals(Files(5, 10)));
        Check("the shared selection has the anchor at A and the focus at C", shared.Anchor == File(10) && shared.Focus == File(5));

        pointer.Click(TilePoint(cell, sel, 30), ModifierKeys.Control);
        pointer.Click(TilePoint(cell, sel, 34), ModifierKeys.Shift);
        Check("Ctrl+click D moves the anchor: Shift+click E is exactly D to E", Picked(canvas).SetEquals(Files(30, 34)));
        pointer.Click(TilePoint(cell, sel, 60), ModifierKeys.Control | ModifierKeys.Shift);
        Check("Ctrl+Shift+click F adds D to F to what was there", Picked(canvas).SetEquals(Files(30, 60)));

        // After a change of order, the new order.
        pointer.Click(TilePoint(cell, sel, 12));
        var anchorName = sel.Files[12].Name;
        var bySize = new ItemSort(SortColumn.Size, true);
        tree.SetSort(bySize);
        tree.FlushSortWork();
        Render(canvas);
        cell = canvas.ScreenRectOf(sel)!.Value;
        var anchorAt = sel.Files.Select((file, index) => (file, index)).First(pair => pair.file.Name == anchorName).index;
        var target = anchorAt > 50 ? anchorAt - 25 : anchorAt + 25;
        pointer.Click(TilePoint(cell, sel, target), ModifierKeys.Shift);
        Check("after a change of order, Shift+click runs in the new order", Picked(canvas).SetEquals(Files(anchorAt, target)));
        tree.SetSort(ItemSort.Default);
        tree.FlushSortWork();
        Render(canvas);
        cell = canvas.ScreenRectOf(sel)!.Value;

        // An anchor in another folder: a plain click.
        pointer.Click(TilePoint(cell, sel, 3));
        var child = sel.Children[2];
        var childCell = canvas.ScreenRectOf(child)!.Value;
        pointer.Click(TilePoint(childCell, child, 1), ModifierKeys.Shift);
        Check("Shift+click with the anchor in another folder selects the item alone",
            Picked(canvas).SetEquals([child.PathOf(child.Files[1])]));

        // After a rectangle: anchor nearest where it started, focus nearest where it ended.
        var (_, sy) = sel.FileGrid.Origin(0);
        var (ex, ey) = sel.FileGrid.Origin(sel.FileGrid.Columns * 3 + 4);
        pointer.Drag(At(cell, MarginX, sy + sel.FileGrid.TileHeight / 2), At(cell, ex + sel.FileGrid.TileWidth / 2, ey + sel.FileGrid.TileHeight / 2));
        Check("after a rectangle the anchor is the item nearest its start and the focus the one nearest its end",
            canvas.SelectionState.Anchor is { } anchor && anchor.FileName == sel.Files[0].Name
            && canvas.SelectionState.Active is { } active && active.FileName == sel.Files[sel.FileGrid.Columns * 3 + 4].Name);
        Clear(canvas);
    }

    // ---- 7. keys ----------------------------------------------------------------------------

    private static void SelectionKeyChecks(NestedCanvas canvas, NestedTree tree)
    {
        Section("nested selection: keys");

        // Rows first, as the grids were read before there was a choice; the
        // same keys read down first are checked with the orders of folders.
        tree.Orders.Flow = LayoutOrder.AcrossThenDown;
        tree.FlushSortWork();
        try
        {
            SelectionKeyChecksAcross(canvas, tree);
        }
        finally
        {
            tree.Orders.Flow = LayoutOrder.DownThenAcross;
            tree.FlushSortWork();
            Render(canvas);
        }
    }

    private static void SelectionKeyChecksAcross(NestedCanvas canvas, NestedTree tree)
    {
        var sel = tree.Find(SelectionRoot)!;
        canvas.FlyTo(sel, 0.9, animated: false);
        Render(canvas);
        var cell = canvas.ScreenRectOf(sel)!.Value;
        var pointer = canvas.Pointer;

        pointer.Click(TilePoint(cell, sel, 100));
        for (var step = 0; step < 3; step++)
        {
            canvas.HandleKey(Key.Right, ModifierKeys.Shift);
        }

        Check("Shift+Right three times extends by three items",
            Picked(canvas).SetEquals(Enumerable.Range(100, 4).Select(index => sel.PathOf(sel.Files[index]))));

        // Down from the last row of sub-folders goes into the files.
        var grid = sel.Grid;
        var lastRowFirst = (grid.Rows - 1) * grid.Columns;
        pointer.Click(TitlePoint(canvas.ScreenRectOf(sel.Children[lastRowFirst])!.Value));
        canvas.HandleKey(Key.Down, ModifierKeys.Shift);
        var landed = canvas.SelectionState.Active is { IsFile: true } focus
            ? sel.Files.Select((file, index) => (file, index)).First(pair => pair.file.Name == focus.FileName).index
            : -1;
        var crossed = sel.Children.Skip(lastRowFirst).Select(child => child.FullPath)
            .Concat(sel.Files.Take(landed + 1).Select(file => sel.PathOf(file)));
        Check("Shift+Down from the last row of sub-folders runs on into the files below it",
            landed >= 0 && Picked(canvas).SetEquals(crossed));

        // Ctrl+A, for each way the current folder is decided.
        var all = sel.Children.Count + sel.Files.Count;
        pointer.Drag(At(cell, MarginX, 0.3), At(cell, 0.4, 0.35));
        canvas.HandleKey(Key.A, ModifierKeys.Control);
        Check($"Ctrl+A after a rectangle selects everything in its folder ({Picked(canvas).Count} of {all})", Picked(canvas).Count == all);

        var child = sel.Children[4];
        var childCell = canvas.ScreenRectOf(child)!.Value;
        pointer.Click(TilePoint(childCell, child, 0));
        canvas.HandleKey(Key.A, ModifierKeys.Control);
        Check("after a click on an item, everything in the item's folder", Picked(canvas).Count == child.Children.Count + child.Files.Count);

        canvas.FlyTo(child, 0.5, animated: false);
        Render(canvas);
        pointer.Click(At(canvas.ScreenRectOf(child)!.Value, MarginX, 0.45));
        canvas.HandleKey(Key.A, ModifierKeys.Control);
        Check("after a click on a folder's open space, everything in that folder",
            Picked(canvas).Count == child.Children.Count + child.Files.Count);

        canvas.FlyTo(sel, 0.9, animated: false);
        Render(canvas);
        pointer.Click(TitlePoint(canvas.ScreenRectOf(sel.Children[6])!.Value));
        canvas.HandleKey(Key.Right, ModifierKeys.None);
        canvas.HandleKey(Key.A, ModifierKeys.Control);
        Check("after the keys, everything in the folder they moved in", Picked(canvas).Count == all);

        var few = tree.Find(@"Q:\few")!;
        canvas.FlyTo(few, 0.9, animated: false);
        Render(canvas);
        canvas.HandleKey(Key.A, ModifierKeys.Control);
        Check("with the current folder off screen, everything in the folder the camera is on",
            Picked(canvas).SetEquals(few.Files.Select(file => few.PathOf(file))));

        Check("Esc lets go of the selection", canvas.HandleKey(Key.Escape, ModifierKeys.None) && Picked(canvas).Count == 0);
        Check("and a second Esc is left to the window", !canvas.HandleKey(Key.Escape, ModifierKeys.None));
    }

    // ---- 8. the filter ---------------------------------------------------------------------

    private static void SelectionFilterChecks(NestedCanvas canvas, NestedTree tree)
    {
        Section("nested selection: with the filter on");
        var sel = tree.Find(SelectionRoot)!;
        canvas.FlyTo(sel, 0.9, animated: false);
        canvas.SetFilter("*.log");
        Render(canvas);
        var cell = canvas.ScreenRectOf(sel)!.Value;
        var pointer = canvas.Pointer;
        static bool IsLog(string name) => name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);

        pointer.Down(MouseButton.Left, At(cell, MarginX, 0.3));
        pointer.Move(At(cell, 0.9, 0.55));
        canvas.RunFrameForTests(TimeSpan.FromSeconds(2000));
        var marquee = canvas.Marquee!;
        var matching = sel.Files.Count(file => IsLog(file.Name));
        Check($"the count by the pointer reads N of M, M the matches in the folder ({marquee.HitCount} of {marquee.MatchCount})",
            marquee.MatchCount == matching && marquee.HitCount > 0 && marquee.HitCount < matching);
        pointer.Up(MouseButton.Left, At(cell, 0.9, 0.55));
        Check("a rectangle takes only what matches",
            Picked(canvas).SetEquals(Touched(sel, MarginX, 0.3, 0.9, 0.55, IsLog)));

        canvas.HandleKey(Key.A, ModifierKeys.Control);
        Check("Ctrl+A takes every match in the folder and nothing else", Picked(canvas).Count == matching && Picked(canvas).All(IsLog));

        pointer.Click(TilePoint(cell, sel, 1));
        pointer.Click(TilePoint(cell, sel, 49), ModifierKeys.Shift);
        Check("a range skips what does not match",
            Picked(canvas).SetEquals(Enumerable.Range(1, 49).Select(index => sel.Files[index]).Where(file => IsLog(file.Name)).Select(file => sel.PathOf(file))));
        canvas.SetFilter(null);
        Clear(canvas);
    }

    // ---- 9. sliding at the edge ---------------------------------------------------------------

    private static void SelectionEdgeChecks(NestedCanvas canvas, NestedTree tree)
    {
        Section("nested selection: the view slides at the edge");
        var big = tree.Find(@"Q:\big")!;
        canvas.FlyTo(big, 0.9, animated: false);
        canvas.ZoomAt(new Point(200, 300), 2);
        Render(canvas);
        var pointer = canvas.Pointer;
        var cell = canvas.ScreenRectOf(big)!.Value;
        Check($"zoomed into Q:\\big it reaches far past the right edge ({cell.Right:0} DIPs)", cell.Right > ViewWidth + 1_000);

        var frameTime = TimeSpan.FromSeconds(3000);
        var tick = TimeSpan.FromMilliseconds(1000.0 / 120);
        void Frames(int count)
        {
            for (var index = 0; index < count; index++)
            {
                frameTime += tick;
                canvas.RunFrameForTests(frameTime);
            }
        }

        // Inside the band: nothing for the first hundred milliseconds.  The
        // press is in the gap between two rows of tiles: on the folder.
        var files = big.FileGrid;
        var start = At(cell, files.Left + files.StepX * 3 + files.TileWidth / 2, files.Top + files.StepY * 12 - files.Gap / 2);
        pointer.Down(MouseButton.Left, start);
        pointer.Move(start + new Vector(20, 10));
        Frames(2);
        var band = Math.Min(32, 0.1 * Math.Min(ViewWidth, ViewHeight));
        pointer.Move(new Point(ViewWidth - band / 2, 420));
        var before = canvas.ScreenRectOf(big)!.Value.X;
        Frames(11);
        var afterWait = canvas.ScreenRectOf(big)!.Value.X;
        Check($"in the band at the edge, the view waits a moment before it slides ({before - afterWait:0.000} DIPs in 92 ms)", afterWait == before);
        Frames(30);
        Check("and then slides", canvas.ScreenRectOf(big)!.Value.X < afterWait - 1);
        pointer.Up(MouseButton.Left, new Point(ViewWidth - band / 2, 420));
        Clear(canvas);

        // Outside the view: at once, ramping up; the distance is the integral.
        canvas.FlyTo(big, 0.9, animated: false);
        canvas.ZoomAt(new Point(200, 300), 2);
        Render(canvas);
        pointer.Down(MouseButton.Left, start);
        pointer.Move(start + new Vector(20, 10));
        Frames(2);
        var outside = new Point(ViewWidth + 20, 420);
        pointer.Move(outside);
        var hitsBefore = -1;
        var from = canvas.ScreenRectOf(big)!.Value.X;
        var expected = 0.0;
        var speed = NestedMarquee.PanSpeed(band + 20, band);
        for (var index = 0; index < 60; index++)
        {
            frameTime += tick;
            canvas.RunFrameForTests(frameTime);
            if (index > 0)
            {
                expected += speed * NestedMarquee.Ramp(tick * index) * tick.TotalSeconds;
            }

            if (index == 10)
            {
                hitsBefore = canvas.Marquee!.CountSelected();
            }
        }

        var moved = from - canvas.ScreenRectOf(big)!.Value.X;
        var continuous = speed * (NestedMarquee.EdgeRamp.TotalSeconds / 2 + (60 * tick - NestedMarquee.EdgeRamp).TotalSeconds);
        Check($"20 DIPs outside for 500 ms the view slides {moved:0.0} DIPs, within 3% of the speed times the ramp ({continuous:0.0})",
            Math.Abs(moved - continuous) <= 0.03 * continuous && Math.Abs(moved - expected) <= 0.01 * expected);
        Check("and the rectangle takes more as it goes, the pointer held still", canvas.Marquee!.CountSelected() > hitsBefore && hitsBefore > 0);

        // Far outside, for long enough: it stops with the rim 24 DIPs inside.
        pointer.Move(new Point(ViewWidth + 300, 420));
        Frames(600);
        var rim = canvas.ScreenRectOf(big)!.Value.Right;
        Check($"sliding stops with the folder's rim 24 DIPs inside the view ({ViewWidth - rim:0.000})", Near(rim, ViewWidth - 24, 1e-6));
        pointer.Up(MouseButton.Left, new Point(ViewWidth + 300, 420));
        Clear(canvas);
    }

    // ---- 10. the wheel mid-drag --------------------------------------------------------------

    private static void SelectionWheelChecks(NestedCanvas canvas, NestedTree tree)
    {
        Section("nested selection: the wheel during a rectangle");
        var sel = tree.Find(SelectionRoot)!;
        canvas.FlyTo(sel, 0.9, animated: false);
        Render(canvas);
        var cell = canvas.ScreenRectOf(sel)!.Value;
        var pointer = canvas.Pointer;
        var start = At(cell, MarginX, 0.28);
        var corner = At(cell, 0.55, 0.45);
        pointer.Down(MouseButton.Left, start);
        pointer.Move(corner);
        Render(canvas);
        pointer.Wheel(corner, 120 * 4, ModifierKeys.Control);
        pointer.Wheel(corner, -120, ModifierKeys.None);
        Render(canvas);
        var factor = Math.Pow(1.2, 4);
        var expectedStart = new Point(corner.X + (start.X - corner.X) * factor, corner.Y + (start.Y - corner.Y) * factor - 120 * 0.8);
        var now = canvas.ScreenRectOf(sel)!.Value;
        var marquee = canvas.Marquee!;
        var startNow = At(now, marquee.StartX, marquee.StartY);
        Check($"a zoom and a pan mid-drag keep the start on its content ({(startNow - expectedStart).Length:0.###e0} DIPs off)",
            (startNow - expectedStart).Length <= 1e-9 * Math.Max(1, now.Width));
        pointer.Up(MouseButton.Left, corner);
        var afterWheel = Picked(canvas);

        // The same rectangle drawn afresh in the new view.
        Clear(canvas);
        var startCell = canvas.ScreenRectOf(sel)!.Value;
        Check("and what it selects is what a rectangle drawn afresh in the new view touches",
            afterWheel.SetEquals(Touched(sel, marquee.StartX, marquee.StartY, (corner.X - startCell.X) / startCell.Width, (corner.Y - startCell.Y) / startCell.Width)));
        Clear(canvas);
    }

    // ---- 11 and 12. the right button, and the "pan" setting --------------------------------

    private static void SelectionButtonChecks(NestedCanvas canvas, NestedTree tree)
    {
        Section("nested selection: the right button and the pan setting");
        var sel = tree.Find(SelectionRoot)!;
        canvas.FlyTo(sel, 0.9, animated: false);
        Render(canvas);
        var cell = canvas.ScreenRectOf(sel)!.Value;
        var pointer = canvas.Pointer;
        var menus = 0;
        (NestedHit? Hit, bool Background)? menu = null;
        void OnMenu(NestedHit? hit, bool background, Point point)
        {
            menus++;
            menu = (hit, background);
        }

        var presses = 0;
        (NestedHit? Hit, bool Background)? pressed = null;
        void OnPress(NestedHit? hit, bool background, Point point)
        {
            presses++;
            pressed = (hit, background);
        }

        canvas.ContextMenuRequested += OnMenu;
        canvas.ContextMenuPressed += OnPress;
        try
        {
            var child = sel.Children[3];
            var title = TitlePoint(canvas.ScreenRectOf(child)!.Value);
            pointer.Down(MouseButton.Right, title);
            Check("the right button going down says what its menu would be for, before any menu is asked for",
                presses == 1 && menus == 0 && pressed?.Hit is { } early && ReferenceEquals(early.Folder, child) && pressed?.Background == false);
            pointer.Move(title + new Vector(2, 0));
            pointer.Up(MouseButton.Right, title + new Vector(2, 0));
            Check("a right click that moves 2 DIPs opens the menu for what is under it, as before",
                menus == 1 && menu?.Hit is { } hit && ReferenceEquals(hit.Folder, child) && menu?.Background == false);
            Check("- the same thing the press said", pressed?.Hit is { } same && menu?.Hit is { } asked && same.Path == asked.Path && pressed?.Background == menu?.Background);

            var before = canvas.ScreenRectOf(sel)!.Value;
            pointer.Down(MouseButton.Right, title);
            pointer.Move(title + new Vector(8, 0));
            pointer.Move(title + new Vector(30, 12));
            pointer.Up(MouseButton.Right, title + new Vector(30, 12));
            var after = canvas.ScreenRectOf(sel)!.Value;
            Check("one that moves 8 DIPs pans the view by the drag, and opens no menu",
                menus == 1 && Near(after.X - before.X, 30, 1e-9) && Near(after.Y - before.Y, 12, 1e-9));

            // The pan setting: the canvas as it always was.
            canvas.LeftDrag = NestedLeftDrag.Pan;
            canvas.FlyTo(sel, 0.9, animated: false);
            Render(canvas);
            cell = canvas.ScreenRectOf(sel)!.Value;
            before = cell;
            pointer.Drag(At(cell, MarginX, 0.3), At(cell, MarginX, 0.3) + new Vector(-40.5, 17.25));
            after = canvas.ScreenRectOf(sel)!.Value;
            Check("with left drag set to pan, a drag on a folder's open space pans by exactly the drag",
                canvas.Marquee is null && Near(after.X - before.X, -40.5, 1e-9) && Near(after.Y - before.Y, 17.25, 1e-9));

            string? dragged = null;
            void OnDrag(string path) => dragged = path;
            canvas.DragRequested += OnDrag;
            canvas.FlyTo(sel.Children[1], 0.25, animated: false);
            Render(canvas);
            var childCell = canvas.ScreenRectOf(sel.Children[1])!.Value;
            pointer.Drag(TitlePoint(childCell), TitlePoint(childCell) + new Vector(15, 15), steps: 3);
            canvas.DragRequested -= OnDrag;
            Check("a drag on a folder's title still picks it up", dragged == sel.Children[1].FullPath);

            pointer.Click(TitlePoint(canvas.ScreenRectOf(sel.Children[1])!.Value), ModifierKeys.Control);
            pointer.Click(TitlePoint(canvas.ScreenRectOf(sel.Children[2])!.Value), ModifierKeys.Control);
            Check("Ctrl+click still toggles", Picked(canvas).Contains(sel.Children[2].FullPath) && Picked(canvas).Contains(sel.Children[1].FullPath));

            canvas.FlyTo(sel, 0.9, animated: false);
            Render(canvas);
            cell = canvas.ScreenRectOf(sel)!.Value;
            pointer.Down(MouseButton.Left, At(cell, MarginX, 0.3), ModifierKeys.Shift);
            pointer.Move(At(cell, 0.6, 0.5));
            Check("and Shift+drag draws a rectangle, replacing", canvas.Marquee is { Mode: NestedSelectMode.Replace });
            pointer.Up(MouseButton.Left, At(cell, 0.6, 0.5));
            Check("which selects what it touches", Picked(canvas).SetEquals(Touched(sel, MarginX, 0.3, 0.6, 0.5)));
        }
        finally
        {
            canvas.LeftDrag = NestedLeftDrag.SelectArea;
            canvas.ContextMenuRequested -= OnMenu;
            canvas.ContextMenuPressed -= OnPress;
            Clear(canvas);
        }
    }

    // ---- 13. pixels --------------------------------------------------------------------------

    private static void SelectionPixelChecks(NestedCanvas canvas, NestedTree tree)
    {
        Section("nested selection: pixels");
        var sel = tree.Find(SelectionRoot)!;
        canvas.FlyTo(sel, 0.9, animated: false);
        Render(canvas);
        var cell = canvas.ScreenRectOf(sel)!.Value;
        var pointer = canvas.Pointer;
        var plain = Shoot(canvas, "nested-selection-before.png");

        var start = At(cell, MarginX, 0.3);
        var end = At(cell, 0.62, 0.52);
        pointer.Down(MouseButton.Left, start);
        pointer.Move(end);
        var drawing = Shoot(canvas, "nested-selection-marquee.png");
        var left = (int)Math.Round(start.X);
        var top = (int)Math.Round(start.Y);
        var right = (int)Math.Round(end.X);
        var bottom = (int)Math.Round(end.Y);
        var border = Pixel(drawing, left, (top + bottom) / 2);
        Check($"the rectangle's edge is one pixel of #60CDFF (#{border:X8})", border == 0xFF60CDFF);

        // A point of the fill over the folder's own body, clear of any item.
        var inside = new Point(start.X + 3, (top + bottom) / 2.0);
        var under = Pixel(plain, (int)inside.X, (int)inside.Y);
        var over = Pixel(drawing, (int)inside.X, (int)inside.Y);
        var blended = Blend(under, 0x60CDFF, 0x33);
        Check($"inside it the fill is #60CDFF at a fifth over what is under it (#{over:X8} against #{blended:X8})",
            SameWithin(over, blended, 2));
        pointer.Up(MouseButton.Left, end);

        var selected = Shoot(canvas, "nested-selection-selected.png");
        var picked = Picked(canvas);
        var first = Enumerable.Range(0, sel.Files.Count).First(index => picked.Contains(sel.PathOf(sel.Files[index])));
        var tile = TilePoint(cell, sel, first);
        Check("selected tiles are tinted", Pixel(selected, (int)tile.X, (int)tile.Y) != Pixel(plain, (int)tile.X, (int)tile.Y));
        Check($"drawn as runs: {canvas.LastSelectionPrimitives} shapes for {Picked(canvas).Count} items",
            canvas.LastSelectionPrimitives <= 2 * sel.FileGrid.Rows + sel.Children.Count);

        // Readable tiles, one by one, capped.
        var few = tree.Find(@"Q:\few")!;
        canvas.FlyTo(few, 0.9, animated: false);
        Render(canvas);
        canvas.HandleKey(Key.A, ModifierKeys.Control);
        Render(canvas);
        Check($"tiles big enough to read are outlined one by one ({canvas.LastSelectionPrimitives} shapes for 5)",
            canvas.LastSelectionPrimitives == 5);
        Clear(canvas);
    }

    private static uint Pixel(BitmapSource bitmap, int x, int y)
    {
        var pixels = new uint[1];
        bitmap.CopyPixels(new Int32Rect(Math.Clamp(x, 0, bitmap.PixelWidth - 1), Math.Clamp(y, 0, bitmap.PixelHeight - 1), 1, 1), pixels, 4, 0);
        return pixels[0];
    }

    private static uint Blend(uint under, uint colour, int alpha)
    {
        uint Channel(int shift) =>
            (uint)Math.Round(((colour >> shift) & 0xFF) * alpha / 255.0 + ((under >> shift) & 0xFF) * (1 - alpha / 255.0));
        return 0xFF000000 | Channel(16) << 16 | Channel(8) << 8 | Channel(0);
    }

    private static bool SameWithin(uint first, uint second, int tolerance)
    {
        for (var shift = 0; shift < 32; shift += 8)
        {
            if (Math.Abs((int)((first >> shift) & 0xFF) - (int)((second >> shift) & 0xFF)) > tolerance)
            {
                return false;
            }
        }

        return true;
    }

    // ---- 15. one selection, both ways --------------------------------------------------------

    private static void SelectionRoundTripChecks(NestedCanvas canvas, NestedTree tree, ItemSelection shared, ref int edits)
    {
        Section("nested selection: one selection, both ways");
        var sel = tree.Find(SelectionRoot)!;
        canvas.FlyTo(sel, 0.9, animated: false);
        Render(canvas);
        var cell = canvas.ScreenRectOf(sel)!.Value;
        var changes = 0;
        void OnChanged(ItemSelection _) => changes++;
        shared.Changed += OnChanged;
        try
        {
            var editsBefore = edits;
            canvas.Pointer.Drag(At(cell, MarginX, 0.3), At(cell, 0.7, 0.55));
            Check("a rectangle reaches the shared selection as one edit and one change",
                edits == editsBefore + 1 && changes == 1
                && shared.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(Picked(canvas)));

            var version = canvas.SelectionState.Version;
            canvas.LoadSelection(shared);
            Check("its echo, loaded back, changes nothing", canvas.SelectionState.Version == version);

            // The list selecting a range: the canvas shows the same items, anchor and focus.
            var rows = Enumerable.Range(200, 30).Select(index => sel.Files[index]).ToArray();
            shared.Apply(new SelectionEdit
            {
                Clear = true,
                Container = sel.FullPath,
                Added = [.. rows.Select(file => new SelectionItem(sel.PathOf(file), false, file.Length))],
                Anchor = sel.PathOf(rows[0]),
                Focus = sel.PathOf(rows[^1]),
                Source = SelectionSource.List
            });
            canvas.LoadSelection(shared);
            Check("a range picked in the list is the canvas's selection", Picked(canvas).SetEquals(rows.Select(file => sel.PathOf(file))));
            Check("with the list's anchor and focus",
                canvas.SelectionState.Anchor?.FileName == rows[0].Name && canvas.SelectionState.Active?.FileName == rows[^1].Name);
            canvas.Pointer.Click(TilePoint(cell, sel, 260), ModifierKeys.Shift);
            Check("so a Shift+click on the canvas extends from the anchor picked in the list",
                Picked(canvas).SetEquals(Enumerable.Range(200, 61).Select(index => sel.PathOf(sel.Files[index])))
                && shared.Anchor == sel.PathOf(rows[0]));
        }
        finally
        {
            shared.Changed -= OnChanged;
            Clear(canvas);
        }
    }

    // ---- 16. ten thousand ---------------------------------------------------------------------

    private static void SelectionScaleChecks(NestedCanvas canvas, NestedTree tree)
    {
        Section("nested selection: ten thousand items");
        var big = tree.Find(@"Q:\big")!;
        canvas.FlyTo(big, 0.96, animated: false);
        Render(canvas);
        var cell = canvas.ScreenRectOf(big)!.Value;
        var pointer = canvas.Pointer;

        pointer.Click(At(cell, MarginX, 0.5));
        var selectAll = Stopwatch.StartNew();
        canvas.HandleKey(Key.A, ModifierKeys.Control);
        selectAll.Stop();
        Check($"Ctrl+A takes all 10,000 in {selectAll.Elapsed.TotalMilliseconds:0.0} ms (budget {8 * SelectionBudgetFactor} ms)",
            canvas.SelectedCount == 10_000 && selectAll.Elapsed.TotalMilliseconds <= 8 * SelectionBudgetFactor);

        canvas.RecordSelectionForTests();
        var (milliseconds, bytes) = canvas.RecordSelectionForTests();
        Check($"with 10,000 selected on screen the selection records in {milliseconds:0.000} ms and {bytes / 1024.0:0.0} KB, {canvas.LastSelectionPrimitives} shapes (budget {SelectionBudgetFactor:0} ms, 100 KB)",
            milliseconds <= 1 * SelectionBudgetFactor && bytes <= 100 * 1024);

        // From the bottom right margin, up past the first row: all of it,
        // three times over, the best let-go counted - a loaded machine
        // makes any one of them slow.
        var bottom = NestedLayout.CellHeight - NestedLayout.Padding / 2;
        var sweepMilliseconds = double.MaxValue;
        var canvasSide = double.MaxValue;
        var applySide = 0.0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            // Start from nothing, so the let-go really adds ten thousand.
            canvas.HandleKey(Key.Escape, ModifierKeys.None);
            var sweep = Stopwatch.StartNew();
            pointer.Down(MouseButton.Left, At(cell, 1 - MarginX, bottom));
            for (var step = 1; step <= 60; step++)
            {
                pointer.Move(At(cell, 1 - MarginX - (1 - 2 * MarginX) * step / 60, bottom - (bottom - NestedLayout.HeaderHeight * 0.9) * step / 60));
                canvas.RunFrameForTests(TimeSpan.FromSeconds(5000 + attempt + step / 120.0));
            }

            sweep.Stop();
            sweepMilliseconds = Math.Min(sweepMilliseconds, sweep.Elapsed.TotalMilliseconds);
            _sharedApplyMilliseconds = 0;
            var commit = Stopwatch.StartNew();
            pointer.Up(MouseButton.Left, At(cell, MarginX, NestedLayout.HeaderHeight * 0.9));
            commit.Stop();
            if (commit.Elapsed.TotalMilliseconds - _sharedApplyMilliseconds < canvasSide)
            {
                canvasSide = commit.Elapsed.TotalMilliseconds - _sharedApplyMilliseconds;
                applySide = _sharedApplyMilliseconds;
            }
        }

        Check($"a sweep over all of them, sixty frames in {sweepMilliseconds:0} ms, lets go of 10,000 in {canvasSide:0.0} ms on the canvas's side and {applySide:0.0} ms to apply (best of 3, budget {8 * SelectionBudgetFactor} ms)",
            canvas.SelectedCount == 10_000 && canvasSide <= 8 * SelectionBudgetFactor);

        var model = new ItemSelection();
        model.Apply(new SelectionEdit
        {
            Clear = true,
            Added = [.. big.Files.Select(file => new SelectionItem(big.PathOf(file), false, file.Length))]
        });
        var other = new NestedCanvas { Tree = tree };
        other.Measure(new Size(ViewWidth, ViewHeight));
        other.Arrange(new Rect(0, 0, ViewWidth, ViewHeight));
        other.UpdateLayout();
        other.LoadSelection(model);
        model.Apply(new SelectionEdit { Removed = [big.PathOf(big.Files[0])] });
        var load = Stopwatch.StartNew();
        other.LoadSelection(model);
        load.Stop();
        Check($"loading 10,000 paths from the window takes {load.Elapsed.TotalMilliseconds:0.00} ms (budget {3 * SelectionBudgetFactor} ms)",
            other.SelectedCount == 9_999 && load.Elapsed.TotalMilliseconds <= 3 * SelectionBudgetFactor);
        other.Tree = null;

        var escape = Stopwatch.StartNew();
        canvas.HandleKey(Key.Escape, ModifierKeys.None);
        escape.Stop();
        Check($"Esc lets go of 10,000 in {escape.Elapsed.TotalMilliseconds:0.00} ms (budget {3 * SelectionBudgetFactor} ms)",
            canvas.SelectedCount == 0 && escape.Elapsed.TotalMilliseconds <= 3 * SelectionBudgetFactor);
        Clear(canvas);
    }

    // ---- gone from disk --------------------------------------------------------------------

    private static async Task SelectionGoneChecks(NestedCanvas canvas, NestedTree tree, FakeDisk disk)
    {
        Section("nested selection: items that go away");
        var other = tree.Find(@"Q:\other")!;
        canvas.FlyTo(other, 0.9, animated: false);
        Render(canvas);
        var removals = new List<string>();
        void OnEdit(SelectionEdit edit) => removals.AddRange(edit.Removed);
        canvas.HandleKey(Key.A, ModifierKeys.Control);
        canvas.SelectionCommitted += OnEdit;
        try
        {
            var gone = other.Files[3];
            var directory = disk.Folder(@"Q:\other");
            directory.Files.RemoveAll(file => file.Name == gone.Name);
            await tree.RefreshAsync(other);
            Render(canvas);
            Check("a selected file read away drops out, and the window is told in one edit",
                !Picked(canvas).Contains(other.PathOf(gone)) && removals.SequenceEqual([other.PathOf(gone)]));
        }
        finally
        {
            canvas.SelectionCommitted -= OnEdit;
            Clear(canvas);
        }
    }

    // ---- waiting for a folder ----------------------------------------------------------------

    /// <summary>
    /// A selection the canvas holds only as items waiting for a folder it has
    /// not read - nothing of it on screen - is still the window's, which a
    /// Delete would act on: Esc lets go of it.  On a canvas of its own, which
    /// has never been handed a version of any selection.
    /// </summary>
    private static void SelectionPendingChecks(NestedTree tree)
    {
        Section("nested selection: items waiting for their folder");
        var canvas = new NestedCanvas { Tree = tree };
        canvas.Measure(new Size(ViewWidth, ViewHeight));
        canvas.Arrange(new Rect(0, 0, ViewWidth, ViewHeight));
        canvas.UpdateLayout();
        var model = new ItemSelection();
        model.Apply(new SelectionEdit
        {
            Clear = true,
            Added = [new SelectionItem(@"Q:\not-read-yet\waiting.txt", false, 1)],
            Source = SelectionSource.List
        });
        var clears = 0;
        void OnEdit(SelectionEdit edit) => clears += edit.Clear ? 1 : 0;
        canvas.SelectionCommitted += OnEdit;
        try
        {
            canvas.LoadSelection(model);
            var waiting = canvas.SelectedCount == 0 && canvas.SelectionState.HasPending;
            var handled = canvas.HandleKey(Key.Escape, ModifierKeys.None);
            Check("Esc lets go of a selection held only as waiting for a folder not read yet, and tells the window to clear it",
                waiting && handled && clears == 1 && !canvas.SelectionState.HasPending);
            Check("and a second Esc, with nothing left at all, is the window's",
                !canvas.HandleKey(Key.Escape, ModifierKeys.None) && clears == 1);
        }
        finally
        {
            canvas.SelectionCommitted -= OnEdit;
            canvas.Tree = null;
        }
    }

    /// <summary>
    /// A share or a WSL distribution is a root of its own, in This PC beside
    /// the drives: selected anywhere else, the canvas finds it there by its
    /// whole path - \\ and all - rather than waiting for a folder called
    /// \\server that is never read.
    /// </summary>
    private static void SelectionShareRootChecks()
    {
        Section("nested selection: a share beside the drives");
        const string sharePath = @"\\wsl.localhost\Ubuntu";
        using var tree = new NestedTree(new FakeDisk().Read) { IsReadingOnDemand = false };
        tree.SetRoots(
        [
            new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive),
            new NestedRoot(sharePath, "Ubuntu", NestedFolderKind.Drive)
        ]);
        var share = tree.Find(sharePath);
        var model = new ItemSelection();
        model.Apply(new SelectionEdit
        {
            Clear = true,
            Added = [new SelectionItem(sharePath, true, 0)],
            Focus = sharePath,
            Source = SelectionSource.List
        });
        var selection = new NestedSelection();
        selection.Load(model, tree);
        Check("a share selected elsewhere is selected on the canvas, with the focus on it, and waits for nothing",
            share is not null && selection.IsSelected(share) && !selection.HasPending
            && selection.Active is { Folder: { } focused } && ReferenceEquals(focused, share));
    }
}
