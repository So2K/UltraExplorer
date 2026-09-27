using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// Each folder's own order, and grids read down each column first.  What is
/// checked is what a person would notice: sorting one folder rearranging
/// another, a folder sorted yesterday coming back in name order, an item
/// drawn in one place and clicked, culled, stepped to or swept up in
/// another, and a tile not saying what its folder is ordered by.
///
/// Grid places are checked against brute force from each item's own
/// rectangle, orders against the brute-force orders the sort checks use,
/// and reading across is checked to be exactly what it was before there
/// was a choice.
/// </summary>
internal static partial class Program
{
    private static async Task FolderOrderChecks()
    {
        FolderOrdersModelChecks();
        await FolderOrdersStoreChecks();
        DownFirstGridChecks();
        KeyStepChecks();
        FolderListDetailChecks();
        await FolderListOwnOrderChecks();
        RunOnSta("folders' own orders", FolderOrdersOnStaAsync);
    }

    // ---- the orders themselves ----------------------------------------------------------

    private static void FolderOrdersModelChecks()
    {
        Section("orders: each folder its own");
        var byDate = new ItemSort(SortColumn.Modified, true);
        var bySize = new ItemSort(SortColumn.Size, true);
        var byType = new ItemSort(SortColumn.Type, false);
        var orders = new FolderOrders();
        Check("a new set of orders is names from A everywhere, each folder its own, read down first",
            orders.Default == ItemSort.Default && orders.Scope == SortScope.PerFolder && orders.Flow == LayoutOrder.DownThenAcross
            && orders.Count == 0 && orders.SortOf(@"C:\a") == ItemSort.Default && orders.DownFirst);

        var events = new List<string?>();
        orders.Changed += events.Add;
        orders.Choose(@"C:\a", byDate);
        Check("sorting one folder orders that folder and no other, whatever the case of its path",
            orders.SortOf(@"C:\a") == byDate && orders.SortOf(@"c:\A") == byDate && orders.SortOf(@"C:\b") == ItemSort.Default
            && events.SequenceEqual([@"C:\a"]));
        Check("and nothing below it takes its order", orders.SortOf(@"C:\a\inside") == ItemSort.Default);

        events.Clear();
        orders.Choose(null, bySize);
        Check("with no folder, a choice is every folder's default, and the folder sorted keeps its own",
            orders.Default == bySize && orders.SortOf(@"C:\b") == bySize && orders.SortOf(@"C:\a") == byDate && events.SequenceEqual([null]));

        orders.SetFolder(@"C:\b", bySize);
        Check("a folder's own order that is the default is not kept", orders.Count == 1 && !orders.HasOwnOrder(@"C:\b"));
        orders.SetFolder(@"C:\a", bySize);
        Check("and one set back to the default is let go of", orders.Count == 0 && orders.SortOf(@"C:\a") == bySize);

        orders.SetFolder(@"C:\a", byDate);
        orders.Scope = SortScope.AllFolders;
        Check("all folders the same: every folder in the default, the folder's own order kept for later",
            orders.SortOf(@"C:\a") == bySize && orders.Count == 1 && !orders.HasOwnOrder(@"C:\a"));
        orders.Choose(@"C:\a", byType);
        Check("and a choice made on a folder then orders every folder", orders.Default == byType && orders.SortOf(@"C:\b") == byType);
        orders.Scope = SortScope.PerFolder;
        Check("back to each folder its own, the folder is in its own order again", orders.SortOf(@"C:\a") == byDate);

        orders.ResetFolder(@"C:\a");
        Check("resetting a folder puts it back in the default", orders.SortOf(@"C:\a") == byType && !orders.HasOwnOrder(@"C:\a"));

        orders.SetFolder(@"C:\a", byDate);
        orders.SetFolder(@"C:\b", ItemSort.Default);
        orders.UseEverywhere(byDate);
        Check("using a folder's order for all folders makes it the default and lets every folder's own go",
            orders.Default == byDate && orders.Count == 0 && orders.SortOf(@"C:\b") == byDate);

        orders.SetFolder(@"C:\t", byType);
        var typed = orders.Uses(SortColumn.Type);
        orders.Scope = SortScope.AllFolders;
        var typedAll = orders.Uses(SortColumn.Type);
        orders.Scope = SortScope.PerFolder;
        orders.SetDefault(byType);
        Check("whether any folder is ordered by type is known, and a folder keeps its own order when the default comes to match it",
            typed && !typedAll && orders.Count == 1 && orders.HasOwnOrder(@"C:\t") && orders.Uses(SortColumn.Type));

        // A folder sorted by date, then every folder by date and by name
        // again - from the headers with nothing in view, or with every folder
        // the same - is still by date.
        var pinned = new FolderOrders();
        pinned.SetFolder(@"C:\Downloads", byDate);
        pinned.Choose(null, byDate);
        pinned.Choose(null, ItemSort.Default);
        pinned.Scope = SortScope.AllFolders;
        pinned.Choose(@"C:\Downloads", byDate);
        pinned.Choose(@"C:\Downloads", ItemSort.Default);
        pinned.Scope = SortScope.PerFolder;
        Check("a folder's own order outlives a default that matched it for a while, whichever scope set it",
            pinned.SortOf(@"C:\Downloads") == byDate && pinned.SortOf(@"C:\other") == ItemSort.Default && pinned.Count == 1);
        pinned.SetFolder(@"C:\Downloads", ItemSort.Default);
        Check("and sorting the folder itself into the default lets its own go", pinned.Count == 0);

        var renamed = new FolderOrders();
        renamed.SetFolder(@"C:\a\photos", byDate);
        renamed.SetFolder(@"C:\a\photos\2024", bySize);
        renamed.SetFolder(@"C:\a\photos old", byType);
        renamed.SetFolder(@"C:\b", byType);
        var moves = new List<string?>();
        renamed.Changed += moves.Add;
        renamed.Move(@"C:\a\photos", @"C:\a\pictures");
        Check("a folder renamed takes its own order, and those of the folders inside it, to its new name - and nothing else",
            renamed.SortOf(@"C:\a\pictures") == byDate && renamed.SortOf(@"C:\a\pictures\2024") == bySize
            && renamed.SortOf(@"C:\a\photos") == ItemSort.Default && renamed.SortOf(@"C:\a\photos\2024") == ItemSort.Default
            && renamed.SortOf(@"C:\a\photos old") == byType && renamed.Count == 4 && renamed.Uses(SortColumn.Size)
            && moves.SequenceEqual([@"C:\a\pictures"]));
        Check("each as recently sorted as it was",
            renamed.Saved().Select(state => state.Path).SequenceEqual([@"C:\a\pictures", @"C:\a\pictures\2024", @"C:\a\photos old", @"C:\b"]));
        renamed.Move(@"C:\nothing", @"C:\else");
        Check("a rename of a folder with no order of its own changes nothing", moves.Count == 1 && renamed.Count == 4);

        var bounded = new FolderOrders();
        var boundedEvents = new List<string?>();
        bounded.Changed += boundedEvents.Add;
        for (var index = 0; index < FolderOrders.MaximumFolders + 10; index++)
        {
            bounded.SetFolder($@"C:\f{index}", byDate);
        }

        var oldestGone = Enumerable.Range(0, 10).All(index => bounded.SortOf($@"C:\f{index}") == ItemSort.Default);
        Check("a folder let go of to make room is said to have changed, so whatever shows it puts it back in the default",
            Enumerable.Range(0, 10).All(index => boundedEvents.Count(path => path == $@"C:\f{index}") == 2)
            && boundedEvents.Count(path => path == @"C:\f10") == 1);
        bounded.SetFolder(@"C:\f10", bySize);
        bounded.SetFolder(@"C:\new", byDate);
        var saved = bounded.Saved();
        Check($"only the {FolderOrders.MaximumFolders:N0} folders sorted most recently are remembered, the oldest let go first",
            bounded.Count == FolderOrders.MaximumFolders && oldestGone
            && bounded.SortOf(@"C:\f10") == bySize && bounded.SortOf(@"C:\f11") == ItemSort.Default
            && bounded.SortOf($@"C:\f{FolderOrders.MaximumFolders + 9}") == byDate
            && saved.Count == FolderOrders.MaximumFolders && saved[^1].Path == @"C:\new" && saved[^2].Path == @"C:\f10");

        Check("the scope and the way grids fill read back by name, and anything else is the default",
            FolderOrders.ParseScope("allfolders ") == SortScope.AllFolders && FolderOrders.ParseScope("1") == SortScope.PerFolder
            && FolderOrders.ParseScope(null) == SortScope.PerFolder && FolderOrders.ParseFlow("AcrossThenDown") == LayoutOrder.AcrossThenDown
            && FolderOrders.ParseFlow("sideways") == LayoutOrder.DownThenAcross);

        var loaded = new FolderOrders();
        var loads = 0;
        loaded.Changed += _ => loads++;
        loaded.Load(bySize, SortScope.AllFolders, LayoutOrder.AcrossThenDown,
        [
            new FolderSortState(@"C:\a", "Modified-desc"),
            new FolderSortState(@"C:\b", "Size-desc"),
            new FolderSortState("", "Name"),
            new FolderSortState(@"C:\c", "nonsense")
        ]);
        loaded.Scope = SortScope.PerFolder;
        Check("loading keeps what it can: a folder in the default keeps it, one with no path is skipped, one not understood is names from A",
            loads == 2 && loaded.Count == 3 && loaded.SortOf(@"C:\a") == byDate && loaded.HasOwnOrder(@"C:\b")
            && loaded.SortOf(@"C:\c") == ItemSort.Default && loaded.Flow == LayoutOrder.AcrossThenDown);
    }

    /// <summary>The orders through the workspace file and back, and a workspace from before folders had their own.</summary>
    private static async Task FolderOrdersStoreChecks()
    {
        Section("orders: the workspace file");
        var folder = Path.Combine(Path.GetTempPath(), "UltraExplorerOrderStore", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(folder, "workspace.json");
            var store = new WorkspaceStore(path);
            var orders = new FolderOrders();
            orders.SetDefault(new ItemSort(SortColumn.Size, true));
            orders.SetFolder(@"C:\Users\me\Downloads", new ItemSort(SortColumn.Modified, true));
            orders.SetFolder(@"D:\", new ItemSort(SortColumn.Type, false));
            orders.Flow = LayoutOrder.AcrossThenDown;
            orders.Scope = SortScope.AllFolders;
            await store.SaveAsync(new WorkspaceState
            {
                CanvasSort = orders.Default.ToSetting(),
                CanvasSortScope = FolderOrders.ScopeSetting(orders.Scope),
                CanvasLayoutOrder = FolderOrders.FlowSetting(orders.Flow),
                FolderSorts = orders.Saved()
            });

            var state = await store.LoadAsync();
            var back = new FolderOrders();
            back.Load(
                ItemSort.FromSetting(state?.CanvasSort),
                FolderOrders.ParseScope(state?.CanvasSortScope),
                FolderOrders.ParseFlow(state?.CanvasLayoutOrder),
                state?.FolderSorts);
            var scopeBack = back.Scope;
            back.Scope = SortScope.PerFolder;
            orders.Scope = SortScope.PerFolder;
            Check("the default, the scope, the way grids fill and every folder's own order come back from the file as they went in",
                back.Default == orders.Default && scopeBack == SortScope.AllFolders && back.Flow == LayoutOrder.AcrossThenDown
                && back.Saved().SequenceEqual(orders.Saved())
                && back.SortOf(@"c:\users\me\downloads") == new ItemSort(SortColumn.Modified, true));

            await File.WriteAllTextAsync(path, """{ "SchemaVersion": 2, "CanvasSort": "Modified-desc" }""");
            state = await store.LoadAsync();
            var old = new FolderOrders();
            old.Load(
                ItemSort.FromSetting(state?.CanvasSort),
                FolderOrders.ParseScope(state?.CanvasSortScope),
                FolderOrders.ParseFlow(state?.CanvasLayoutOrder),
                state?.FolderSorts);
            Check("a workspace from before keeps its one order as every folder's default, each folder free to have its own, read down first",
                old.Default == new ItemSort(SortColumn.Modified, true) && old.Scope == SortScope.PerFolder
                && old.Flow == LayoutOrder.DownThenAcross && old.Count == 0);
        }
        finally
        {
            TryDelete(folder);
        }
    }

    // ---- grids read down first -----------------------------------------------------------

    /// <summary>
    /// Every place of grids of every size the layout makes, both zones, read
    /// down first: the same rows and cells as across, only as many columns
    /// as the rows need, each index in the column and row the reading order
    /// gives it and found there again by a point; culling walks exactly the
    /// items whose steps touch the view; a rectangle's block holds exactly
    /// what it touches, however the grid is read.  Across stays what it was.
    /// </summary>
    private static void DownFirstGridChecks()
    {
        Section("orders: down, then across");
        int[] counts = [1, 2, 3, 4, 5, 7, 10, 11, 37, 64, 65, 100, 999, 1003, 2500, 10_000, 50_000];
        var random = new Random(9191);
        var shapeWrong = new List<string>();
        var placeWrong = new List<string>();
        var readingWrong = new List<string>();
        var cullWrong = new List<string>();
        var blockWrong = new List<string>();
        var acrossWrong = new List<string>();
        var cases = 0;
        foreach (var count in counts)
        {
            var height = NestedLayout.ContentHeight * (0.4 + count % 5 * 0.15);
            var across = NestedLayout.GridFor(count, height);
            var down = NestedLayout.GridFor(count, height, downFirst: true);
            var acrossFiles = NestedLayout.FileGridFor(count, NestedLayout.HeaderHeight + 0.1, height * 0.8);
            var downFiles = NestedLayout.FileGridFor(count, NestedLayout.HeaderHeight + 0.1, height * 0.8, downFirst: true);

            if (NestedLayout.GridFor(count, height, downFirst: false) != across
                || NestedLayout.FileGridFor(count, NestedLayout.HeaderHeight + 0.1, height * 0.8, downFirst: false) != acrossFiles)
            {
                acrossWrong.Add($"{count}: across is not the grid it was");
            }

            for (var index = 0; index < count; index++)
            {
                var expected = (across.Left + index % across.Columns * across.StepX, across.Top + index / across.Columns * across.StepY);
                var expectedFile = (acrossFiles.Left + index % acrossFiles.Columns * acrossFiles.StepX, acrossFiles.Top + index / acrossFiles.Columns * acrossFiles.StepY);
                if (across.Origin(index) != expected || acrossFiles.Origin(index) != expectedFile)
                {
                    acrossWrong.Add($"{count}: item {index} moved");
                    break;
                }
            }

            foreach (var zone in new[] { 0, 1 })
            {
                var isFiles = zone == 1;
                var reference = isFiles ? (acrossFiles.Rows, acrossFiles.Columns) : (across.Rows, across.Columns);
                var rows = isFiles ? downFiles.Rows : down.Rows;
                var columns = isFiles ? downFiles.Columns : down.Columns;
                var width = isFiles ? downFiles.TileWidth : down.Scale;
                var itemHeight = isFiles ? downFiles.TileHeight : down.Scale * NestedLayout.CellHeight;
                var stepX = isFiles ? downFiles.StepX : down.StepX;
                var stepY = isFiles ? downFiles.StepY : down.StepY;
                var left = isFiles ? downFiles.Left : down.Left;
                var top = isFiles ? downFiles.Top : down.Top;
                (double X, double Y) Origin(int index) => isFiles ? downFiles.Origin(index) : down.Origin(index);
                int IndexAt(double x, double y) => isFiles ? downFiles.IndexAt(x, y) : down.IndexAt(x, y);
                int IndexOf(int row, int column) => isFiles ? downFiles.IndexOf(row, column) : down.IndexOf(row, column);
                (int Row, int Column) PlaceOf(int index) => isFiles ? downFiles.PlaceOf(index) : down.PlaceOf(index);

                var sizeSame = isFiles
                    ? downFiles.TileWidth == acrossFiles.TileWidth && downFiles.DownFirst
                    : down.Scale == across.Scale && down.DownFirst && Math.Abs(down.Left - (NestedLayout.Padding + (NestedLayout.ContentWidth - down.Width) / 2)) < 1e-12;
                if (rows != reference.Rows || columns != (count + rows - 1) / rows || columns > reference.Columns || !sizeSame)
                {
                    shapeWrong.Add($"{count} zone {zone}: {rows}x{columns} against {reference.Rows}x{reference.Columns}");
                }

                for (var index = 0; index < count; index++)
                {
                    var (x, y) = Origin(index);
                    var (row, column) = PlaceOf(index);
                    if (row != index % rows || column != index / rows || IndexOf(row, column) != index
                        || Math.Abs(x - (left + column * stepX)) > 1e-12 || Math.Abs(y - (top + row * stepY)) > 1e-12
                        || IndexAt(x + width / 2, y + itemHeight / 2) != index || column >= columns)
                    {
                        placeWrong.Add($"{count} zone {zone} item {index}");
                        break;
                    }

                    if (index > 0)
                    {
                        var (px, py) = Origin(index - 1);
                        var nextDown = row > 0 && Math.Abs(x - px) < 1e-12 && y > py;
                        var nextColumn = row == 0 && x > px && y <= py;
                        if (!nextDown && !nextColumn)
                        {
                            readingWrong.Add($"{count} zone {zone} item {index}");
                            break;
                        }
                    }
                }

                for (var trial = 0; trial < 40; trial++)
                {
                    cases++;

                    // A view somewhere over the grid, for culling; a rectangle
                    // anywhere at all, for what it touches.
                    var (rl, rt, rr, rb) = RandomViewRect(random);

                    // Culling: every place the ranges give, walked as drawing
                    // walks them, stopping along a row at the first past the end.
                    var (firstColumn, lastColumn, firstRow, lastRow) = isFiles
                        ? downFiles.Overlapping(rl, rt, rr, rb)
                        : down.Overlapping(rl, rt, rr, rb);
                    var walked = new HashSet<int>();
                    var duplicate = false;
                    for (var row = firstRow; row <= lastRow; row++)
                    {
                        for (var column = firstColumn; column <= lastColumn; column++)
                        {
                            var index = IndexOf(row, column);
                            if (index >= count)
                            {
                                break;
                            }

                            duplicate |= !walked.Add(index);
                        }
                    }

                    var cullOk = !duplicate;
                    for (var index = 0; index < count && cullOk; index++)
                    {
                        var (x, y) = Origin(index);
                        var visible = x <= rr && x + width >= rl && y <= rb && y + itemHeight >= rt;
                        var inSteps = x <= rr && x + stepX > rl && y <= rb && y + stepY > rt;
                        cullOk = (!visible || walked.Contains(index)) && (!walked.Contains(index) || inSteps);
                    }

                    if (!cullOk)
                    {
                        cullWrong.Add($"{count} zone {zone} rect ({rl:0.###}, {rt:0.###})-({rr:0.###}, {rb:0.###})");
                    }

                    // A rectangle's block, read down first and across.
                    (rl, rt, rr, rb) = RandomUnitRect(random, trial);
                    foreach (var downFirst in new[] { true, false })
                    {
                        var block = (zone, downFirst) switch
                        {
                            (0, true) => down.Touching(rl, rt, rr, rb),
                            (0, false) => across.Touching(rl, rt, rr, rb),
                            (_, true) => downFiles.Touching(rl, rt, rr, rb),
                            _ => acrossFiles.Touching(rl, rt, rr, rb)
                        };
                        var touched = 0;
                        var wrong = false;
                        var hasArea = rr > rl && rb > rt;
                        for (var index = 0; index < count; index++)
                        {
                            var (x, y) = (zone, downFirst) switch
                            {
                                (0, true) => down.Origin(index),
                                (0, false) => across.Origin(index),
                                (_, true) => downFiles.Origin(index),
                                _ => acrossFiles.Origin(index)
                            };
                            var w = zone == 0 ? down.Scale : downFiles.TileWidth;
                            var h = zone == 0 ? down.Scale * NestedLayout.CellHeight : downFiles.TileHeight;
                            var hit = hasArea && x < rr && x + w > rl && y < rb && y + h > rt;
                            touched += hit ? 1 : 0;
                            var held = (zone, downFirst) switch
                            {
                                (0, true) => down.Holds(block, index),
                                (0, false) => across.Holds(block, index),
                                (_, true) => downFiles.Holds(block, index),
                                _ => acrossFiles.Holds(block, index)
                            };
                            wrong |= hit != held;
                        }

                        var counted = (zone, downFirst) switch
                        {
                            (0, true) => down.CountIn(block),
                            (0, false) => across.CountIn(block),
                            (_, true) => downFiles.CountIn(block),
                            _ => acrossFiles.CountIn(block)
                        };
                        if (wrong || counted != touched)
                        {
                            blockWrong.Add($"{count} zone {zone} {(downFirst ? "down" : "across")}: {block}, counted {counted} of {touched}");
                        }
                    }
                }
            }
        }

        void Verdict(string what, List<string> wrong)
        {
            Check($"{what} ({wrong.Count} wrong)", wrong.Count == 0);
            if (wrong.Count > 0)
            {
                Console.WriteLine($"        first: {wrong[0]}");
            }
        }

        Verdict("read across, every grid and every item's place is exactly what it was before there was a choice", acrossWrong);
        Verdict("read down first, a grid has the same rows and cells as across, and only the columns the rows need", shapeWrong);
        Verdict("every item is in the row and column the order gives it, and a point on it finds it", placeWrong);
        Verdict("each item is under the one before it, or at the top of the next column", readingWrong);
        Verdict($"culling walks every item on screen once and nothing whose step is off it, in {cases:N0} views", cullWrong);
        Verdict("a rectangle's block holds exactly what it touches, and counts it, read down first and across", blockWrong);
    }

    private static (double Left, double Top, double Right, double Bottom) RandomViewRect(Random random)
    {
        var x0 = random.NextDouble() * 1.2 - 0.1;
        var x1 = random.NextDouble() * 1.2 - 0.1;
        var y0 = random.NextDouble() * NestedLayout.CellHeight * 1.2 - 0.05;
        var y1 = random.NextDouble() * NestedLayout.CellHeight * 1.2 - 0.05;
        return (Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
    }

    /// <summary>Where an arrow goes in a grid, read down first and across.</summary>
    private static void KeyStepChecks()
    {
        Section("orders: arrows");

        // Ten items in columns of four: 0-3, 4-7, 8-9.
        int Down(int index, Key key) => NestedCanvas.Step(index, 3, 4, downFirst: true, 10, key);
        Check("read down first, Down and Up are the next and the one before, over the foot of a column too",
            Down(0, Key.Down) == 1 && Down(1, Key.Up) == 0 && Down(3, Key.Down) == 4 && Down(4, Key.Up) == 3);
        Check("Right and Left are a column over",
            Down(1, Key.Right) == 5 && Down(5, Key.Left) == 1 && Down(2, Key.Left) < 0);
        Check("Right from a row the short last column does not reach is its last item, and from the last column nowhere",
            Down(6, Key.Right) == 9 && Down(7, Key.Right) == 9 && Down(9, Key.Right) >= 10 && Down(8, Key.Right) >= 10);

        int Across(int index, Key key) => NestedCanvas.Step(index, 3, 4, downFirst: false, 10, key);
        Check("read across, the arrows are what they were",
            Across(5, Key.Right) == 6 && Across(5, Key.Left) == 4 && Across(5, Key.Down) == 8 && Across(5, Key.Up) == 2 && Across(8, Key.Down) == 11);
    }

    // ---- the list ---------------------------------------------------------------------------

    private static void FolderListDetailChecks()
    {
        Section("orders: what a row says");
        FileTypeNames.Resolver = SortTypeName;
        try
        {
            FolderListRowChecks();
        }
        finally
        {
            FileTypeNames.Resolver = null;
        }
    }

    private static void FolderListRowChecks()
    {
        var modified = new DateTime(2025, 3, 14, 9, 26, 0, DateTimeKind.Utc);
        var file = new FolderListItem(new ViewAllEntryDescriptor(@"C:\x\report.txt", "report.txt", ViewAllEntryKind.File, false, false, 1536, modified, "TXT file  ·  1.5 KB"));
        var folder = new FolderListItem(new ViewAllEntryDescriptor(@"C:\x\sub", "sub", ViewAllEntryKind.Folder, false, false, null, modified, "old words"));
        var date = modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        var byName = (file.Detail, folder.Detail);
        file.DetailColumn = SortColumn.Modified;
        folder.DetailColumn = SortColumn.Modified;
        var byDate = (file.Detail, folder.Detail);
        file.DetailColumn = SortColumn.Size;
        folder.DetailColumn = SortColumn.Size;
        var bySize = (file.Detail, folder.Detail);
        file.DetailColumn = SortColumn.Type;
        folder.DetailColumn = SortColumn.Type;
        var byType = (file.Detail, folder.Detail);
        FileTypeNames.TryGet("txt", out var txt);
        Check("a row says what the list is ordered by: its date, its size, its type - and by name what it always said",
            byName == ("TXT file  ·  1.5 KB", "old words") && byDate == (date, date) && bySize == ("1.5 KB", "old words")
            && byType == (txt, FileTypeNames.Folder));

        // Ordering and the tiles give ".gitignore" no extension, and a name
        // ending in a dot or with an extension too long to be one none either.
        string[] odd = [".gitignore", "notes.", "archive." + new string('x', 40)];
        FileTypeNames.TryGet(string.Empty, out var plainFile);
        var said = odd.Select(name => new FolderListItem(
            new ViewAllEntryDescriptor($@"C:\x\{name}", name, ViewAllEntryKind.File, false, false, 10, modified, "old words"))
        {
            DetailColumn = SortColumn.Type
        }.Detail).ToList();
        Check("a row names the kind its file is ranked and drawn as, by the same reading of its extension",
            said.All(text => text == plainFile) && odd.All(name => FileTypeNames.Of(new NestedFile(name, false, 0).Extension) == plainFile));
    }

    /// <summary>The list takes each folder's own order as it goes to the folder, before reading it.</summary>
    private static async Task FolderListOwnOrderChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerOrderList", Guid.NewGuid().ToString("N"));
        try
        {
            WriteViewEntries(root);
            var inside = Directory.GetDirectories(root)[0];
            var files = new ViewAllFileSystemService();
            using var icons = new ShellIconService();
            var options = new ViewAllGraphOptions();
            var largest = new ItemSort(SortColumn.Size, true);
            var list = new FolderListViewModel(
                (path, cancellation) => files.GetChildrenAsync(path, options, cancellation),
                (_, _) => Task.CompletedTask,
                _ => false,
                icons)
            {
                IsVisible = true,
                SortOf = path => ViewAllPath.Equals(path, root) ? largest : ItemSort.Default
            };

            await list.NavigateAsync(root);
            var shown = list.Items.Select(item => item.DisplayName).ToList();
            var expected = ExpectedEntryOrder(list.Items.Select(item => item.Entry), largest).Select(entry => entry.DisplayName).ToList();
            var saysSize = list.Items.Where(item => !item.IsDirectory).All(item => item.DetailColumn == SortColumn.Size);
            await list.NavigateAsync(inside);
            Check("the list goes to a folder in that folder's own order, its rows saying it, and to the next in that one's",
                list.Sort == ItemSort.Default && shown.SequenceEqual(expected) && saysSize);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- on a tree and a canvas ------------------------------------------------------------

    /// <summary>
    /// Q:\p: thirteen sub-folders and forty files with dates, sizes and kinds
    /// out of name order; Q:\s beside it; Q:\big with ten thousand files.
    /// </summary>
    private static FakeDisk BuildOrderWorld()
    {
        var disk = new FakeDisk();
        disk.Folder(@"Q:\");
        string[] kinds = ["txt", "png", "cs", "md"];
        for (var index = 0; index < 13; index++)
        {
            disk.Folder($@"Q:\p\sub{index:D2}").Modified = SortEpoch.AddDays(index * 5 % 13);
            disk.AddFiles($@"Q:\p\sub{index:D2}", 2, "in");
        }

        for (var index = 0; index < 40; index++)
        {
            disk.AddFile(@"Q:\p", $"file{index:D2}.{kinds[index % kinds.Length]}", 1_000L * (index * 17 % 40), modified: SortEpoch.AddHours(index * 11 % 40));
        }

        for (var index = 0; index < 9; index++)
        {
            disk.Folder($@"Q:\s\s{index}");
            disk.AddFile(@"Q:\s", $"s{index}.txt", index, modified: SortEpoch.AddDays(9 - index));
        }

        for (var index = 0; index < 10_000; index++)
        {
            disk.AddFile(@"Q:\big", $"b{index:D5}.e{index % 30:D2}", index * 7L % 9_973, modified: SortEpoch.AddMinutes(index * 7919 % 10_000));
        }

        return disk;
    }

    private static async Task FolderOrdersOnStaAsync()
    {
        FileTypeNames.Resolver = SortTypeName;
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
            TreeOwnOrderChecks(tree);
            await TreeFlowChecks(tree);
            CanvasDownFirstChecks(canvas, tree);
            TileDetailChecks(canvas, tree);
        }
        finally
        {
            canvas.Tree = null;
            FileTypeNames.Resolver = null;
        }
    }

    /// <summary>Sorting one folder places that folder at once, and nothing else.</summary>
    private static void TreeOwnOrderChecks(NestedTree tree)
    {
        Section("orders: one folder sorted");
        var p = tree.Find(@"Q:\p")!;
        var s = tree.Find(@"Q:\s")!;
        var big = tree.Find(@"Q:\big")!;
        var pChildrenByName = p.Children.ToArray();
        var pFilesByName = p.Files.ToArray();
        var sChildren = s.Children;
        var sFiles = s.Files;
        var sGrid = s.Grid;
        Check("grids are read down first unless asked otherwise, and This PC's drives along a row",
            p.Grid.DownFirst && p.FileGrid.DownFirst && !tree.Root.Grid.DownFirst);

        var sortEvents = 0;
        var changes = 0;
        void OnSort() => sortEvents++;
        void OnChanged(object? sender, EventArgs e) => changes++;
        tree.SortChanged += OnSort;
        tree.Changed += OnChanged;
        var byDate = new ItemSort(SortColumn.Modified, true);
        try
        {
            tree.Orders.SetFolder(p.FullPath, byDate);
            Check("sorting one folder places it there and then, with no pass over the tree to wait for",
                !tree.IsSorting && p.LayoutSortGeneration == tree.SortGeneration && tree.SortOf(p) == byDate
                && p.Children.SequenceEqual(ExpectedSortedChildren(p, pChildrenByName, byDate))
                && p.Files.SequenceEqual(ExpectedSortedFiles(pFilesByName, byDate))
                && sortEvents == 1 && changes >= 1);
            Check("and nothing else moves: the folder beside it keeps its very lists and grid",
                ReferenceEquals(s.Children, sChildren) && ReferenceEquals(s.Files, sFiles) && s.Grid == sGrid && tree.SortOf(s) == ItemSort.Default);
            tree.EnsureLayout(s);
            Check("even once it is looked at again, only its stamp moving on",
                ReferenceEquals(s.Children, sChildren) && ReferenceEquals(s.Files, sFiles) && s.LayoutSortGeneration == tree.SortGeneration);
            Check("and a folder inside the one sorted keeps names from A", tree.SortOf(p.Children[0]) == ItemSort.Default);

            tree.Orders.ResetFolder(p.FullPath);
            Check("reset, the folder is back in names from A at once",
                p.Children.SequenceEqual(pChildrenByName) && p.Files.SequenceEqual(pFilesByName));

            var bySize = new ItemSort(SortColumn.Size, true);
            var started = Stopwatch.GetTimestamp();
            tree.Orders.SetFolder(big.FullPath, bySize);
            var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Check("a folder of ten thousand files sorted by size is in its order at once",
                big.Files.SequenceEqual(ExpectedSortedFiles([.. big.Files.OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase)], bySize)));
            Report("sorting that one folder", elapsed, 150);
            tree.Orders.ResetFolder(big.FullPath);
        }
        finally
        {
            tree.SortChanged -= OnSort;
            tree.Changed -= OnChanged;
        }
    }

    /// <summary>The way grids fill is a change to every folder, placed by the pass over the tree like a change of the default.</summary>
    private static async Task TreeFlowChecks(NestedTree tree)
    {
        var p = tree.Find(@"Q:\p")!;
        tree.Orders.Flow = LayoutOrder.AcrossThenDown;
        var sorting = tree.IsSorting;
        await tree.WhenSortIdleAsync();
        var (folderHeight, _) = NestedLayout.Split(p.Children.Count, p.Files.Count);
        var across = NestedLayout.GridFor(p.Children.Count, folderHeight);
        var used = across.Height + NestedLayout.ZoneGap;
        Check("rows first again, the pass over the tree places every folder as it was placed before there was a choice",
            sorting && !p.Grid.DownFirst && p.Grid == across && !p.FileGrid.DownFirst
            && p.FileGrid == NestedLayout.FileGridFor(p.Files.Count, NestedLayout.HeaderHeight + used, NestedLayout.ContentHeight - used));
        tree.Orders.Flow = LayoutOrder.DownThenAcross;
        tree.FlushSortWork();
        Check("and down first again after", p.Grid.DownFirst && p.FileGrid.DownFirst);
    }

    /// <summary>Arrows, Shift and clicks on a canvas whose grids are read down first.</summary>
    private static void CanvasDownFirstChecks(NestedCanvas canvas, NestedTree tree)
    {
        Section("orders: on the canvas, down first");
        var p = tree.Find(@"Q:\p")!;
        canvas.FlyTo(p, 0.9, animated: false);
        Render(canvas);
        string? selected = null;
        void OnSelect(string path, bool _) => selected = path;
        canvas.SelectRequested += OnSelect;
        try
        {
            var grid = p.Grid;
            var files = p.FileGrid;
            canvas.SetSelection([p.Children[0].FullPath], p.Children[0].FullPath);
            var down = canvas.HandleKey(Key.Down, ModifierKeys.None) && selected == p.Children[1].FullPath;
            var right = canvas.HandleKey(Key.Right, ModifierKeys.None) && selected == p.Children[1 + grid.Rows].FullPath;
            Check($"Down is the next sub-folder in the order and Right the one a column over ({grid.Rows} rows)", down && right);

            canvas.SetSelection([p.Children[^1].FullPath], p.Children[^1].FullPath);
            var intoFiles = canvas.HandleKey(Key.Down, ModifierKeys.None) && selected == p.PathOf(p.Files[0]);
            var backUp = canvas.HandleKey(Key.Up, ModifierKeys.None) && selected == p.Children[^1].FullPath;
            Check("Down from the last sub-folder is the first file, and Up from it the last sub-folder again", intoFiles && backUp);

            canvas.SetSelection([p.PathOf(p.Files[0])], p.PathOf(p.Files[0]));
            var fileDown = canvas.HandleKey(Key.Down, ModifierKeys.None) && selected == p.PathOf(p.Files[1]);
            var fileRight = canvas.HandleKey(Key.Right, ModifierKeys.None) && selected == p.PathOf(p.Files[1 + files.Rows]);
            Check($"on the files too ({files.Rows} rows of {files.Columns} columns)", fileDown && fileRight);

            // Shift runs through the order, down each column and on into the next.
            Render(canvas);
            var cell = canvas.ScreenRectOf(p)!.Value;
            var first = files.Rows - 2;
            canvas.Pointer.Click(TilePoint(cell, p, first));
            for (var step = 0; step < 3; step++)
            {
                canvas.HandleKey(Key.Down, ModifierKeys.Shift);
            }

            var run = Enumerable.Range(first, 4).Select(index => p.PathOf(p.Files[index])).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Check("Shift+Down three times takes the next three in the order, over the foot of the column into the next",
                Picked(canvas).SetEquals(run));
            canvas.HandleKey(Key.Right, ModifierKeys.Shift);
            Check("and Shift+Right a whole column more",
                Picked(canvas).SetEquals(Enumerable.Range(first, 4 + files.Rows).Select(index => p.PathOf(p.Files[index]))));

            // A click lands on the file drawn there.
            var clicks = 0;
            for (var index = 0; index < p.Files.Count; index += 3)
            {
                canvas.Pointer.Click(TilePoint(cell, p, index));
                clicks += Picked(canvas).SetEquals([p.PathOf(p.Files[index])]) ? 1 : 0;
            }

            Check("a click on a tile picks the file drawn in it", clicks == (p.Files.Count + 2) / 3);

            // A rectangle over the files takes exactly what it touches.
            var touched = Touched(p, MarginX, files.Top + files.StepY * 1.5, files.Left + files.StepX * 1.6, files.Top + files.StepY * 4.2);
            canvas.Pointer.Drag(
                At(cell, MarginX, files.Top + files.StepY * 1.5),
                At(cell, files.Left + files.StepX * 1.6, files.Top + files.StepY * 4.2));
            Check($"a rectangle over the tiles selects exactly the ones it touches ({touched.Count})",
                touched.Count > 4 && Picked(canvas).SetEquals(touched));
            Clear(canvas);
        }
        finally
        {
            canvas.SelectRequested -= OnSelect;
        }
    }

    /// <summary>What a tile and a title say, by what their folder is ordered by.</summary>
    private static void TileDetailChecks(NestedCanvas canvas, NestedTree tree)
    {
        Section("orders: what a tile says");
        var p = tree.Find(@"Q:\p")!;
        var file = p.Files.First(item => item.Length > 2048 && item.ModifiedTicks > 0);
        var local = new DateTime(file.ModifiedTicks, DateTimeKind.Utc).ToLocalTime();
        var name = canvas.FileDetailText(SortColumn.Name, file, dateOnly: false);
        var size = canvas.FileDetailText(SortColumn.Size, file, dateOnly: false);
        var date = canvas.FileDetailText(SortColumn.Modified, file, dateOnly: false);
        var day = canvas.FileDetailText(SortColumn.Modified, file, dateOnly: true);
        var type = canvas.FileDetailText(SortColumn.Type, file, dateOnly: false);
        Check("a tile says its size by name or size, its date and time by date - the date alone where narrow - and its type by type",
            name == size && size.EndsWith("KB", StringComparison.Ordinal)
            && date == local.ToString("g", CultureInfo.CurrentCulture) && day == local.ToString("d", CultureInfo.CurrentCulture)
            && type == FileTypeNames.Of(file.Extension));
        Check("each made once and kept, not once a frame",
            ReferenceEquals(date, canvas.FileDetailText(SortColumn.Modified, file, dateOnly: false))
            && ReferenceEquals(type, canvas.FileDetailText(SortColumn.Type, file, dateOnly: false))
            && ReferenceEquals(size, canvas.FileDetailText(SortColumn.Size, file, dateOnly: false)));
        Check("a file with no date says none", canvas.FileDetailText(SortColumn.Modified, new NestedFile("x.txt", false, 1), dateOnly: false).Length == 0);
        Check("nor does one the file system kept no date for - a FILETIME of zero, 1 January 1601",
            canvas.DateText(DateTime.FromFileTimeUtc(0).Ticks, dateOnly: false).Length == 0);
        Check("a date outside the calendar of the culture - Umm al-Qura spans 1900 to 2077 - is still written, never thrown inside a frame",
            DatesOutsideCalendarWritten(canvas));

        var child = p.Children[0];
        var folderDate = canvas.DateText(child.ModifiedTicks, dateOnly: false);
        var plain = canvas.DetailText(child);
        tree.Orders.SetFolder(p.FullPath, new ItemSort(SortColumn.Modified, true));
        var inDated = canvas.DetailText(child);
        var ownDated = canvas.DetailText(p);
        tree.Orders.ResetFolder(p.FullPath);
        Check("a folder's title says when it was written where the folder around it is ordered by date",
            !plain.Contains(folderDate, StringComparison.Ordinal) && inDated.EndsWith(folderDate, StringComparison.Ordinal)
            && inDated.StartsWith(plain, StringComparison.Ordinal));
        Check("but not where only its own contents are, which would leave the date behind a write inside it",
            p.ModifiedTicks == 0 || !ownDated.Contains(canvas.DateText(p.ModifiedTicks, false), StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether dates on either side of what Saudi Arabia's calendar can say
    /// come out as text under that culture, rather than as the exception its
    /// calendar throws for them.  The culture of the moment is put back after.
    /// </summary>
    private static bool DatesOutsideCalendarWritten(NestedCanvas canvas)
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var early = new DateTime(1850, 3, 1, 12, 0, 0, DateTimeKind.Utc).Ticks;
            var late = new DateTime(2100, 6, 1, 12, 0, 0, DateTimeKind.Utc).Ticks;
            var inside = new DateTime(2020, 6, 1, 12, 0, 0, DateTimeKind.Utc).Ticks;
            return canvas.DateText(early, dateOnly: false).Length > 0 && canvas.DateText(late, dateOnly: true).Length > 0
                && canvas.DateText(inside, dateOnly: false).Length > 0

                // The lists' and the tree's dates, written the same way.
                && UltraExplorer.Infrastructure.CultureDates.Format(new DateTime(1850, 3, 1), "g").Length > 0
                && UltraExplorer.Infrastructure.CultureDates.Format(new DateTime(2100, 6, 1), "g").Length > 0
                && UltraExplorer.Infrastructure.CultureDates.Format(DateTime.FromFileTimeUtc(0).ToLocalTime(), "g").Length == 0;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
}
