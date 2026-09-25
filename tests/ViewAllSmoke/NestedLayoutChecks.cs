using System.Diagnostics;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The geometry of the nested canvas, checked as geometry: every child inside
/// its parent's content area, no two children on top of each other, a grid
/// that can be inverted exactly (a point finds the child it is in and a gap
/// finds nothing), and a visible-range query that never misses anything.  The
/// whole canvas - drawing, hit testing, the camera - is built on these, so
/// they are checked over every child count up to 200 and a few huge ones, and
/// over many mixes of folders and files sharing one cell.
/// </summary>
internal static partial class Program
{
    private const double LayoutEpsilon = 1e-9;

    private static Task NestedLayoutChecks()
    {
        Section("nested layout");

        var tally = new LayoutTally();
        var random = new Random(20260925);

        Check("a lone sub-folder is half its parent", NestedLayout.GridFor(1).Scale == NestedLayout.MaximumScale);
        Check("no sub-folders is an empty grid", NestedLayout.GridFor(0).IsEmpty && NestedLayout.GridFor(0).IndexAt(0.5, 0.3) == -1);

        // ---- folders alone -------------------------------------------------
        int[] counts = [.. Enumerable.Range(0, 201), 1_000, 20_000, 100_000];
        foreach (var count in counts)
        {
            var grid = NestedLayout.GridFor(count);
            tally.Count("deterministic");
            if (grid != NestedLayout.GridFor(count) || grid != NestedLayout.GridFor(count, NestedLayout.ContentHeight))
            {
                tally.Fail("deterministic", $"GridFor({count}) differs between calls");
            }

            VerifyFolderGrid(grid, count, NestedLayout.ContentHeight, $"GridFor({count})", tally, random);
        }

        // ---- folders and files sharing a cell ----------------------------------
        // The same arithmetic the tree uses to place a folder's contents: the
        // folders get their share, the files everything the folders left.
        int[] folderCounts = [0, 1, 2, 3, 5, 8, 13, 30, 64, 65, 100, 200, 1_000, 20_000];
        int[] fileCounts = [0, 1, 2, 5, 10, 50, 64, 65, 300, 1_000, 5_000, 50_000];
        foreach (var folders in folderCounts)
        {
            foreach (var files in fileCounts)
            {
                var label = $"{folders} folders + {files} files";
                var (folderHeight, fileHeight) = NestedLayout.Split(folders, files);
                VerifySplit(folders, files, folderHeight, fileHeight, label, tally);

                var grid = NestedLayout.GridFor(folders, folderHeight);
                tally.Count("deterministic");
                if (grid != NestedLayout.GridFor(folders, folderHeight))
                {
                    tally.Fail("deterministic", $"GridFor({folders}, {folderHeight}) differs between calls");
                }

                VerifyFolderGrid(grid, folders, folderHeight, label, tally, random);

                var used = grid.IsEmpty ? 0 : grid.Height + NestedLayout.ZoneGap;
                var top = NestedLayout.HeaderHeight + used;
                var height = NestedLayout.ContentHeight - used;
                var fileGrid = NestedLayout.FileGridFor(files, top, height);
                tally.Count("deterministic");
                if (fileGrid != NestedLayout.FileGridFor(files, top, height))
                {
                    tally.Fail("deterministic", $"FileGridFor({files}) differs between calls");
                }

                VerifyFileGrid(fileGrid, files, top, height, grid, label, tally, random);
            }
        }

        tally.Emit("the zones split the space under the header, each at least a readable strip", "split");
        tally.Emit("every grid has the count asked for, in full rows", "shape");
        tally.Emit("a sub-folder is never more than half its parent", "scale");
        tally.Emit("every sub-folder lies inside the parent's content area", "inside");
        tally.Emit("no two sub-folders overlap", "disjoint");
        tally.Emit("the centre of sub-folder i is found as i", "centre");
        tally.Emit("a point in a gap, the header, a margin or past the last child finds nothing", "gap");
        tally.Emit("the visible range holds every sub-folder that touches the view", "overlap-cover");
        tally.Emit("and nothing more than one gap away from it", "overlap-tight");
        tally.Emit("the block of sub-folders is centred", "centred");
        tally.Emit("sub-folders get the largest size at which all of them fit", "optimal");
        tally.Emit("and no more columns than that size needs", "compact");
        tally.Emit("the grids are deterministic", "deterministic");
        tally.Emit("every file tile lies inside the content area", "file-inside");
        tally.Emit("file tiles sit below the sub-folders, clear of them", "file-below");
        tally.Emit("no two file tiles overlap", "file-disjoint");
        tally.Emit("the centre of tile i is found as i", "file-centre");
        tally.Emit("a point between tiles finds no file", "file-gap");
        tally.Emit("the visible range holds every tile that touches the view", "file-overlap-cover");
        tally.Emit("and nothing more than one gap away from it", "file-overlap-tight");
        tally.Emit("a tile is never wider than its cap", "file-width");
        tally.Emit("tiles get the largest size at which every file fits", "file-optimal");
        tally.Emit("file grids have the count asked for, in full rows", "file-shape");

        // ---- cost ------------------------------------------------------------
        // A folder of a hundred thousand is laid out every time it is read or
        // filtered; the search is a window, not a scan.
        NestedLayout.GridFor(100_000);
        var single = Stopwatch.StartNew();
        NestedLayout.GridFor(100_000);
        single.Stop();
        Report("GridFor(100,000) once", single.ElapsedMilliseconds, 3);

        var many = Stopwatch.StartNew();
        for (var index = 0; index < 1_000; index++)
        {
            NestedLayout.GridFor(100_000 - index);
        }

        many.Stop();
        Report("GridFor(~100,000) 1,000 times", many.ElapsedMilliseconds, 60);

        var fileLayouts = Stopwatch.StartNew();
        for (var index = 0; index < 1_000; index++)
        {
            NestedLayout.FileGridFor(50_000 - index, NestedLayout.HeaderHeight, NestedLayout.ContentHeight);
        }

        fileLayouts.Stop();
        Report("FileGridFor(~50,000) 1,000 times", fileLayouts.ElapsedMilliseconds, 60);
        return Task.CompletedTask;
    }

    // ---- verifying one grid ----------------------------------------------------

    private static void VerifySplit(int folders, int files, double folderHeight, double fileHeight, string label, LayoutTally tally)
    {
        tally.Count("split");
        var ok = (folders, files) switch
        {
            (_, <= 0) => folderHeight == NestedLayout.ContentHeight && fileHeight == 0,
            (<= 0, _) => folderHeight == 0 && fileHeight == NestedLayout.ContentHeight,
            _ => Math.Abs(folderHeight + fileHeight + NestedLayout.ZoneGap - NestedLayout.ContentHeight) < LayoutEpsilon
                 && fileHeight / (NestedLayout.ContentHeight - NestedLayout.ZoneGap) >= 0.14 - LayoutEpsilon
                 && fileHeight / (NestedLayout.ContentHeight - NestedLayout.ZoneGap) <= 0.72 + LayoutEpsilon
        };

        if (!ok)
        {
            tally.Fail("split", $"{label}: folders {folderHeight}, files {fileHeight}");
        }
    }

    private static void VerifyFolderGrid(NestedGrid grid, int count, double height, string label, LayoutTally tally, Random random)
    {
        tally.Count("scale");
        if (count > 0 && (grid.Scale <= 0 || grid.Scale > NestedLayout.MaximumScale + LayoutEpsilon))
        {
            tally.Fail("scale", $"{label}: scale {grid.Scale}");
        }

        var area = new Area(
            NestedLayout.Padding,
            NestedLayout.HeaderHeight,
            1 - NestedLayout.Padding,
            Math.Min(NestedLayout.HeaderHeight + height, NestedLayout.CellHeight - NestedLayout.Padding));
        VerifyCells(CellGrid.Of(grid), count, area, "", label, tally, random);
        if (count == 0)
        {
            return;
        }

        tally.Count("centred");
        var leftMargin = grid.Left - NestedLayout.Padding;
        var rightMargin = 1 - NestedLayout.Padding - (grid.Left + grid.Width);
        if (Math.Abs(leftMargin - rightMargin) > LayoutEpsilon)
        {
            tally.Fail("centred", $"{label}: margins {leftMargin} and {rightMargin}");
        }

        // The largest size at which count equal cells fit, found by trying
        // every column count: the fitting constraint written out directly.
        tally.Count("optimal");
        var best = 0.0;
        var bestColumns = 0;
        for (var columns = 1; columns <= count; columns++)
        {
            var scale = FolderScaleThatFits(columns, count, height);
            if (scale > best)
            {
                best = scale;
                bestColumns = columns;
            }
        }

        var expected = Math.Min(NestedLayout.MaximumScale, best);
        if (grid.Scale < expected * (1 - 1e-12))
        {
            tally.Fail("optimal",
                $"{label}: scale {grid.Scale:R} with {grid.Columns} columns, but {expected:R} fits with {bestColumns} "
                + $"({(expected / grid.Scale - 1) * 100:0.00}% larger)");
        }

        tally.Count("compact");
        for (var columns = 1; columns < grid.Columns; columns++)
        {
            if (FolderScaleThatFits(columns, count, height) >= grid.Scale - 1e-12)
            {
                tally.Fail("compact", $"{label}: {grid.Columns} columns where {columns} hold the same size");
                break;
            }
        }
    }

    private static void VerifyFileGrid(
        NestedFileGrid grid,
        int count,
        double top,
        double height,
        NestedGrid folders,
        string label,
        LayoutTally tally,
        Random random)
    {
        var area = new Area(NestedLayout.Padding, top, 1 - NestedLayout.Padding, top + height);
        VerifyCells(CellGrid.Of(grid), count, area, "file-", label, tally, random);
        if (count == 0)
        {
            return;
        }

        tally.Count("file-inside");
        if (top + height > NestedLayout.CellHeight - NestedLayout.Padding + LayoutEpsilon)
        {
            tally.Fail("file-inside", $"{label}: the file strip runs to {top + height}, past the bottom margin");
        }

        tally.Count("file-below");
        if (!folders.IsEmpty && grid.Top < folders.Top + folders.Height - LayoutEpsilon)
        {
            tally.Fail("file-below", $"{label}: files start at {grid.Top}, the sub-folders end at {folders.Top + folders.Height}");
        }

        tally.Count("file-width");
        if (grid.TileWidth <= 0 || grid.TileWidth > NestedLayout.MaximumFileWidth + LayoutEpsilon)
        {
            tally.Fail("file-width", $"{label}: tile width {grid.TileWidth}");
        }

        tally.Count("file-optimal");
        var best = 0.0;
        var bestColumns = 0;
        for (var columns = 1; columns <= count; columns++)
        {
            var width = TileWidthThatFits(columns, count, height);
            if (width > best)
            {
                best = width;
                bestColumns = columns;
            }
        }

        var expected = Math.Min(NestedLayout.MaximumFileWidth, best);
        if (grid.TileWidth < expected * (1 - 1e-12))
        {
            tally.Fail("file-optimal",
                $"{label}: tile width {grid.TileWidth:R} with {grid.Columns} columns, but {expected:R} fits with {bestColumns} "
                + $"({(expected / grid.TileWidth - 1) * 100:0.00}% larger)");
        }
    }

    /// <summary>
    /// The properties every grid of equal cells must have, whatever it holds:
    /// shape, containment, disjointness, exact inversion and the visible-range query.
    /// </summary>
    private static void VerifyCells(CellGrid grid, int count, Area area, string prefix, string label, LayoutTally tally, Random random)
    {
        tally.Count(prefix + "shape");
        if (grid.Count != count)
        {
            tally.Fail(prefix + "shape", $"{label}: holds {grid.Count}, asked for {count}");
            return;
        }

        if (count == 0)
        {
            var (firstColumn, lastColumn, firstRow, lastRow) = grid.Overlapping(-1, -1, 2, 2);
            if (grid.IndexAt(0.5, 0.3) != -1 || firstColumn <= lastColumn && firstRow <= lastRow)
            {
                tally.Fail(prefix + "shape", $"{label}: an empty grid finds something");
            }

            return;
        }

        if (grid.Columns < 1 || grid.Rows != (count + grid.Columns - 1) / grid.Columns)
        {
            tally.Fail(prefix + "shape", $"{label}: {grid.Columns} columns x {grid.Rows} rows for {count}");
            return;
        }

        var gapX = grid.StepX - grid.CellWidth;
        var gapY = grid.StepY - grid.CellHeight;

        // ---- every cell: inside, found at its centre, not found in its gaps ----
        for (var index = 0; index < count; index++)
        {
            var (x, y) = grid.Origin(index);
            tally.Count(prefix + "inside");
            if (x < area.Left - LayoutEpsilon
                || x + grid.CellWidth > area.Right + LayoutEpsilon
                || y < area.Top - LayoutEpsilon
                || y + grid.CellHeight > area.Bottom + LayoutEpsilon)
            {
                tally.Fail(prefix + "inside",
                    $"{label}: cell {index} spans ({x:R}, {y:R})-({x + grid.CellWidth:R}, {y + grid.CellHeight:R}) outside "
                    + $"({area.Left}, {area.Top})-({area.Right}, {area.Bottom})");
            }

            tally.Count(prefix + "centre");
            var found = grid.IndexAt(x + grid.CellWidth / 2, y + grid.CellHeight / 2);
            if (found != index)
            {
                tally.Fail(prefix + "centre", $"{label}: the centre of cell {index} finds {found}");
            }

            tally.Count(prefix + "gap");
            var right = grid.IndexAt(x + grid.CellWidth + gapX / 2, y + grid.CellHeight / 2);
            var below = grid.IndexAt(x + grid.CellWidth / 2, y + grid.CellHeight + gapY / 2);
            if (right != -1 || below != -1)
            {
                tally.Fail(prefix + "gap", $"{label}: the gap right of / below cell {index} finds {right} / {below}");
            }

            if (index % grid.Columns == 0 && grid.IndexAt(grid.Left - grid.CellWidth * 1e-3, y + grid.CellHeight / 2) != -1)
            {
                tally.Fail(prefix + "gap", $"{label}: the margin left of row {index / grid.Columns} finds a cell");
            }

            if (index < grid.Columns && grid.IndexAt(x + grid.CellWidth / 2, grid.Top - grid.CellHeight * 1e-3) != -1)
            {
                tally.Fail(prefix + "gap", $"{label}: the band above column {index} finds a cell");
            }
        }

        if (count % grid.Columns != 0)
        {
            var (x, y) = (grid.Left + count % grid.Columns * grid.StepX, grid.Top + (grid.Rows - 1) * grid.StepY);
            if (grid.IndexAt(x + grid.CellWidth / 2, y + grid.CellHeight / 2) != -1)
            {
                tally.Fail(prefix + "gap", $"{label}: the empty place after the last cell finds a cell");
            }
        }

        if (grid.IndexAt(0.5, NestedLayout.HeaderHeight / 2) != -1)
        {
            tally.Fail(prefix + "gap", $"{label}: the header finds a cell");
        }

        // ---- disjoint --------------------------------------------------------
        tally.Count(prefix + "disjoint");
        if (gapX <= 0 || gapY <= 0)
        {
            tally.Fail(prefix + "disjoint", $"{label}: no gap between cells ({gapX}, {gapY})");
        }
        else if (count <= 250)
        {
            for (var first = 0; first < count; first++)
            {
                var (x1, y1) = grid.Origin(first);
                for (var second = first + 1; second < count; second++)
                {
                    var (x2, y2) = grid.Origin(second);
                    if (x1 < x2 + grid.CellWidth && x2 < x1 + grid.CellWidth
                        && y1 < y2 + grid.CellHeight && y2 < y1 + grid.CellHeight)
                    {
                        tally.Fail(prefix + "disjoint", $"{label}: cells {first} and {second} overlap");
                    }
                }
            }
        }
        else
        {
            // Too many for every pair; in a regular grid it is enough that each
            // cell clears its right and lower neighbours.
            for (var index = 0; index < count; index++)
            {
                var (x, y) = grid.Origin(index);
                if (index % grid.Columns < grid.Columns - 1 && index + 1 < count && grid.Origin(index + 1).X < x + grid.CellWidth)
                {
                    tally.Fail(prefix + "disjoint", $"{label}: cell {index + 1} starts inside cell {index}");
                }

                if (index + grid.Columns < count && grid.Origin(index + grid.Columns).Y < y + grid.CellHeight)
                {
                    tally.Fail(prefix + "disjoint", $"{label}: cell {index + grid.Columns} starts inside cell {index}");
                }
            }
        }

        // ---- the visible-range query -------------------------------------------
        var samples = count > 5_000 ? 10 : 40;
        for (var sample = 0; sample < samples; sample++)
        {
            var width = SampleSize(random, grid.CellWidth);
            var height = SampleSize(random, grid.CellHeight);
            var left = area.Left - 0.05 - width + random.NextDouble() * (area.Right - area.Left + 0.1 + width);
            var top = area.Top - 0.05 - height + random.NextDouble() * (area.Bottom - area.Top + 0.1 + height);
            var (right, bottom) = (left + width, top + height);
            var (firstColumn, lastColumn, firstRow, lastRow) = grid.Overlapping(left, top, right, bottom);

            tally.Count(prefix + "overlap-cover");
            for (var index = 0; index < count; index++)
            {
                var (x, y) = grid.Origin(index);
                var touches = x < right && x + grid.CellWidth > left && y < bottom && y + grid.CellHeight > top;
                if (!touches)
                {
                    continue;
                }

                var (row, column) = (index / grid.Columns, index % grid.Columns);
                if (row < firstRow || row > lastRow || column < firstColumn || column > lastColumn)
                {
                    tally.Fail(prefix + "overlap-cover",
                        $"{label}: cell {index} touches ({left:R}, {top:R})-({right:R}, {bottom:R}) but is outside "
                        + $"columns {firstColumn}..{lastColumn}, rows {firstRow}..{lastRow}");
                    break;
                }
            }

            tally.Count(prefix + "overlap-tight");
            for (var row = firstRow; row <= lastRow; row++)
            {
                for (var column = firstColumn; column <= lastColumn; column++)
                {
                    var index = row * grid.Columns + column;
                    if (index >= count)
                    {
                        break;
                    }

                    var (x, y) = grid.Origin(index);
                    var near = x - gapX <= right + 1e-12 && x + grid.CellWidth + gapX >= left - 1e-12
                               && y - gapY <= bottom + 1e-12 && y + grid.CellHeight + gapY >= top - 1e-12;
                    if (!near)
                    {
                        tally.Fail(prefix + "overlap-tight", $"{label}: cell {index} is in the range but nowhere near the view");
                        row = lastRow;
                        break;
                    }
                }
            }
        }
    }

    private static double SampleSize(Random random, double cell) => random.Next(5) switch
    {
        0 => cell * 0.2,
        1 => cell * 1.5,
        2 => cell * 6,
        3 => 0.25,
        _ => 0.8
    } * (0.5 + random.NextDouble());

    /// <summary>The widest a sub-folder can be with this many columns and every one of the children in the strip.</summary>
    private static double FolderScaleThatFits(int columns, int count, double height)
    {
        var rows = (count + columns - 1) / columns;
        // columns cells and columns-1 gaps across; rows cells and rows-1 gaps down.
        var byWidth = NestedLayout.ContentWidth / (columns + (columns - 1) * NestedLayout.Gap);
        var byHeight = height / (rows * NestedLayout.CellHeight + (rows - 1) * NestedLayout.Gap);
        return Math.Min(byWidth, byHeight);
    }

    private static double TileWidthThatFits(int columns, int count, double height)
    {
        var rows = (count + columns - 1) / columns;
        // A tile is w wide and w/aspect tall; the gap is a share of its height.
        var gap = NestedLayout.FileGap / NestedLayout.FileAspect;
        var byWidth = NestedLayout.ContentWidth / (columns + (columns - 1) * gap);
        var byHeight = height / (rows / NestedLayout.FileAspect + (rows - 1) * gap);
        return Math.Min(byWidth, byHeight);
    }

    private readonly record struct Area(double Left, double Top, double Right, double Bottom);

    /// <summary>A folder grid or a file grid, seen as what both are: equal cells in rows.</summary>
    private readonly record struct CellGrid(
        int Count,
        int Columns,
        int Rows,
        double CellWidth,
        double CellHeight,
        double StepX,
        double StepY,
        double Left,
        double Top,
        Func<int, (double X, double Y)> Origin,
        Func<double, double, int> IndexAt,
        Func<double, double, double, double, (int FirstColumn, int LastColumn, int FirstRow, int LastRow)> Overlapping)
    {
        public static CellGrid Of(NestedGrid grid) => new(
            grid.Count, grid.Columns, grid.Rows, grid.Scale, grid.Scale * NestedLayout.CellHeight,
            grid.StepX, grid.StepY, grid.Left, grid.Top, grid.Origin, grid.IndexAt, grid.Overlapping);

        public static CellGrid Of(NestedFileGrid grid) => new(
            grid.Count, grid.Columns, grid.Rows, grid.TileWidth, grid.TileHeight,
            grid.StepX, grid.StepY, grid.Left, grid.Top, grid.Origin, grid.IndexAt, grid.Overlapping);
    }

    /// <summary>Failures by property, so thousands of grids report as one line each.</summary>
    private sealed class LayoutTally
    {
        private readonly Dictionary<string, (int Checked, int Failed, List<string> Examples)> _entries = [];

        public void Count(string key)
        {
            var entry = Get(key);
            _entries[key] = entry with { Checked = entry.Checked + 1 };
        }

        public void Fail(string key, string example)
        {
            var entry = Get(key);
            if (entry.Examples.Count < 3)
            {
                entry.Examples.Add(example);
            }

            _entries[key] = entry with { Failed = entry.Failed + 1 };
        }

        public void Emit(string description, string key)
        {
            var entry = Get(key);
            var detail = entry.Failed == 0
                ? $" ({entry.Checked:N0} checked)"
                : $" ({entry.Failed:N0} of {entry.Checked:N0} wrong; e.g. {string.Join(" | ", entry.Examples)})";
            Check(description + detail, entry.Failed == 0);
        }

        private (int Checked, int Failed, List<string> Examples) Get(string key)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = (0, 0, []);
                _entries[key] = entry;
            }

            return entry;
        }
    }
}
