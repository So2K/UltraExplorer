namespace UltraExplorer.Services;

/// <summary>
/// Where one folder's sub-folders sit inside the folder's own cell, in the
/// cell's unit frame: the cell is one unit wide and <see cref="NestedLayout.CellHeight"/>
/// tall, and every number here is a fraction of that width.
///
/// A child's cell is the parent's cell scaled by <see cref="Scale"/>, so the
/// same grid inside the child describes the grandchildren, and so on down.
/// Nothing about a child's position depends on what is inside it - only on how
/// many siblings it has and where it is among them - which is what lets a
/// folder be placed before anything below it has been read, and never move
/// when it is.
/// </summary>
/// <param name="Count">How many children the grid holds.</param>
/// <param name="Columns">Children per row.</param>
/// <param name="Rows">Number of rows.</param>
/// <param name="Scale">A child's size as a fraction of the parent's.</param>
/// <param name="Left">Left edge of the first column.</param>
/// <param name="Top">Top edge of the first row.</param>
/// <param name="DownFirst">
/// Whether the children fill the first column top to bottom, then the next
/// one, as Explorer's List view reads - the last column possibly short -
/// rather than the first row left to right, then the next, the last row
/// possibly short.  Only which place an index gets depends on it; the size
/// of the grid and of its cells does not.
/// </param>
public readonly record struct NestedGrid(
    int Count,
    int Columns,
    int Rows,
    double Scale,
    double Left,
    double Top,
    bool DownFirst = false)
{
    public static readonly NestedGrid Empty = new(0, 0, 0, 0, 0, 0);

    public bool IsEmpty => Count == 0;

    /// <summary>Clear space between two neighbouring children.</summary>
    public double Gap => NestedLayout.Gap * Scale;

    /// <summary>Distance from one column's left edge to the next one's.</summary>
    public double StepX => Scale + Gap;

    /// <summary>Distance from one row's top edge to the next one's.</summary>
    public double StepY => Scale * NestedLayout.CellHeight + Gap;

    /// <summary>Width of the widest row.</summary>
    public double Width => Columns == 0 ? 0 : Columns * Scale + (Columns - 1) * Gap;

    /// <summary>Height of every row together.</summary>
    public double Height => Rows == 0 ? 0 : Rows * Scale * NestedLayout.CellHeight + (Rows - 1) * Gap;

    /// <summary>
    /// How many children follow one another along one line of the reading
    /// order: a column's worth when the grid is read down first, a row's
    /// otherwise.  Child <c>n</c> is at place <c>n % Stride</c> on line <c>n / Stride</c>.
    /// </summary>
    public int Stride => DownFirst ? Rows : Columns;

    /// <summary>Top-left of child <paramref name="index"/> in the parent's unit frame.</summary>
    public (double X, double Y) Origin(int index) =>
        DownFirst
            ? (Left + index / Rows * StepX, Top + index % Rows * StepY)
            : (Left + index % Columns * StepX, Top + index / Columns * StepY);

    /// <summary>
    /// The child at a row and a column, which may be past the last one - the
    /// caller compares with <see cref="Count"/>.  Along a row it only ever
    /// grows with the column, whichever way the grid is read, so a walk
    /// along a row can stop at the first place past the end.
    /// </summary>
    public int IndexOf(int row, int column) => DownFirst ? column * Rows + row : row * Columns + column;

    /// <summary>The row and the column of child <paramref name="index"/>.</summary>
    public (int Row, int Column) PlaceOf(int index) =>
        DownFirst ? (index % Rows, index / Rows) : (index / Columns, index % Columns);

    /// <summary>
    /// The child a point in the parent's unit frame falls in, or -1 when it is
    /// in a gap, the header, the margin or past the last child.  Constant time:
    /// a grid is inverted, not searched.
    /// </summary>
    public int IndexAt(double x, double y)
    {
        if (Count == 0)
        {
            return -1;
        }

        var column = (int)Math.Floor((x - Left) / StepX);
        var row = (int)Math.Floor((y - Top) / StepY);
        if (column < 0 || column >= Columns || row < 0 || row >= Rows)
        {
            return -1;
        }

        // Inside the step but past the child itself is the gap.
        if (x - Left - column * StepX > Scale || y - Top - row * StepY > Scale * NestedLayout.CellHeight)
        {
            return -1;
        }

        var index = IndexOf(row, column);
        return index < Count ? index : -1;
    }

    /// <summary>A block of places (see <see cref="Touching"/>) as lines of the reading order: see <see cref="GridBlock.InReadingOrder"/>.</summary>
    public GridBlock InReadingOrder(GridBlock block) => block.InReadingOrder(DownFirst);

    /// <summary>Whether child <paramref name="index"/> sits in a block of places.</summary>
    public bool Holds(GridBlock block, int index) => InReadingOrder(block).Contains(index, Stride);

    /// <summary>How many of the children sit in a block of places.</summary>
    public int CountIn(GridBlock block) => InReadingOrder(block).CountIn(Count, Stride);

    /// <summary>
    /// Every child that could touch the rectangle, as a range of rows and
    /// columns.  Drawing walks only these, so a folder of fifty thousand
    /// entries costs what is on screen, not what is in it.
    /// </summary>
    public (int FirstColumn, int LastColumn, int FirstRow, int LastRow) Overlapping(
        double left,
        double top,
        double right,
        double bottom)
    {
        if (Count == 0)
        {
            return (0, -1, 0, -1);
        }

        var firstColumn = Math.Max(0, (int)Math.Floor((left - Left) / StepX));
        var lastColumn = Math.Min(Columns - 1, (int)Math.Floor((right - Left) / StepX));
        var firstRow = Math.Max(0, (int)Math.Floor((top - Top) / StepY));
        var lastRow = Math.Min(Rows - 1, (int)Math.Floor((bottom - Top) / StepY));
        return (firstColumn, lastColumn, firstRow, lastRow);
    }

    /// <summary>
    /// Exactly the children a rectangle touches - a selection rectangle's hits
    /// - as a block of rows and columns, in constant time however many there
    /// are.  See <see cref="GridBlock.Touching"/> for the rule.
    /// </summary>
    public GridBlock Touching(double left, double top, double right, double bottom) =>
        GridBlock.Touching(Count, Columns, Rows, Left, Top, StepX, StepY, Scale, Scale * NestedLayout.CellHeight, left, top, right, bottom);
}

/// <summary>
/// A block of a grid: every item whose row and column both fall in these
/// ranges.  What a selection rectangle drawn over a folder touches is always
/// such a block, because the items are laid out on a regular grid - so a
/// rectangle over fifty thousand tiles is four numbers, whatever it covers,
/// and whether a tile is in it is a division and four comparisons.
/// </summary>
/// <param name="FirstColumn">The first column in the block.</param>
/// <param name="LastColumn">The last column in the block; less than <paramref name="FirstColumn"/> when it is empty.</param>
/// <param name="FirstRow">The first row in the block.</param>
/// <param name="LastRow">The last row in the block; less than <paramref name="FirstRow"/> when it is empty.</param>
public readonly record struct GridBlock(int FirstColumn, int LastColumn, int FirstRow, int LastRow)
{
    public static readonly GridBlock Empty = new(0, -1, 0, -1);

    public bool IsEmpty => LastColumn < FirstColumn || LastRow < FirstRow;

    /// <summary>
    /// The block as lines of a grid's reading order, for <see cref="Contains"/>
    /// and <see cref="CountIn"/>, which read a grid row by row.  A grid read
    /// down first is a grid read across whose rows are its columns: the same
    /// block with its rows and columns swapped, measured by a column's length
    /// rather than a row's (the grid's <see cref="NestedGrid.Stride"/>).
    /// </summary>
    public GridBlock InReadingOrder(bool downFirst) =>
        downFirst ? new GridBlock(FirstRow, LastRow, FirstColumn, LastColumn) : this;

    /// <summary>Whether item <paramref name="index"/> of a row-major grid <paramref name="columns"/> wide is in the block.</summary>
    public bool Contains(int index, int columns)
    {
        if (IsEmpty || columns <= 0 || index < 0)
        {
            return false;
        }

        var row = index / columns;
        var column = index - row * columns;
        return row >= FirstRow && row <= LastRow && column >= FirstColumn && column <= LastColumn;
    }

    /// <summary>
    /// How many of a row-major grid's <paramref name="count"/> items are in
    /// the block: its rows times its columns, less what the last row - which
    /// may be short - does not have.
    /// </summary>
    public int CountIn(int count, int columns)
    {
        if (IsEmpty || count <= 0 || columns <= 0)
        {
            return 0;
        }

        var lastRow = (count - 1) / columns;
        var lastRowItems = count - lastRow * columns;
        var total = 0;
        var fullEnd = Math.Min(LastRow, lastRow - 1);
        if (fullEnd >= FirstRow)
        {
            total += (fullEnd - FirstRow + 1) * (LastColumn - FirstColumn + 1);
        }

        if (FirstRow <= lastRow && lastRow <= LastRow)
        {
            total += Math.Max(0, Math.Min(LastColumn, lastRowItems - 1) - FirstColumn + 1);
        }

        return total;
    }

    /// <summary>
    /// The block of a regular grid's items that a rectangle touches.  An item
    /// is touched when the two overlap with some area: an item from
    /// <c>a</c> to <c>a + w</c> along an axis touches the rectangle's
    /// <c>[low, high]</c> when <c>a &lt; high</c> and <c>a + w &gt; low</c>,
    /// so sharing only an edge does not count, and a rectangle with no area
    /// touches nothing.  Worked out per axis from the step, not by trying
    /// items: the first column is the first whose right edge is past the
    /// rectangle's left, the last the last whose left edge is before its
    /// right.  Positions far outside the grid are clamped before they are
    /// turned into whole numbers, so a pointer a million grids away never
    /// overflows one.
    /// </summary>
    public static GridBlock Touching(
        int count,
        int columns,
        int rows,
        double gridLeft,
        double gridTop,
        double stepX,
        double stepY,
        double itemWidth,
        double itemHeight,
        double left,
        double top,
        double right,
        double bottom)
    {
        if (count <= 0 || columns <= 0 || rows <= 0 || !(right > left) || !(bottom > top) || !(stepX > 0) || !(stepY > 0))
        {
            return Empty;
        }

        var firstColumn = Clamp(Math.Floor((left - gridLeft - itemWidth) / stepX) + 1, columns);
        var lastColumn = Clamp(Math.Ceiling((right - gridLeft) / stepX) - 1, columns);
        var firstRow = Clamp(Math.Floor((top - gridTop - itemHeight) / stepY) + 1, rows);
        var lastRow = Clamp(Math.Ceiling((bottom - gridTop) / stepY) - 1, rows);
        if (firstColumn > lastColumn || firstRow > lastRow)
        {
            return Empty;
        }

        var block = new GridBlock(
            Math.Max(0, firstColumn),
            Math.Min(columns - 1, lastColumn),
            Math.Max(0, firstRow),
            Math.Min(rows - 1, lastRow));
        return block.IsEmpty ? Empty : block;

        static int Clamp(double value, int size) => (int)Math.Clamp(value, -1, size);
    }
}

/// <summary>
/// Where a folder's files sit: a grid of flat tiles - icon and name, like a row
/// of a list - below the sub-folders.  Files never hold anything, so unlike a
/// folder they need no room inside themselves and are packed far more densely.
/// </summary>
/// <param name="Count">How many files the grid holds.</param>
/// <param name="Columns">Tiles per row.</param>
/// <param name="Rows">Number of rows.</param>
/// <param name="TileWidth">A tile's width, as a fraction of the folder's width.</param>
/// <param name="Left">Left edge of the first column.</param>
/// <param name="Top">Top edge of the first row.</param>
/// <param name="DownFirst">Whether files fill each column top to bottom before the next, as <see cref="NestedGrid.DownFirst"/>.</param>
public readonly record struct NestedFileGrid(
    int Count,
    int Columns,
    int Rows,
    double TileWidth,
    double Left,
    double Top,
    bool DownFirst = false)
{
    public static readonly NestedFileGrid Empty = new(0, 0, 0, 0, 0, 0);

    public bool IsEmpty => Count == 0;

    public double TileHeight => TileWidth / NestedLayout.FileAspect;

    public double Gap => TileHeight * NestedLayout.FileGap;

    public double StepX => TileWidth + Gap;

    public double StepY => TileHeight + Gap;

    /// <summary>Tiles along one line of the reading order, as <see cref="NestedGrid.Stride"/>.</summary>
    public int Stride => DownFirst ? Rows : Columns;

    public (double X, double Y) Origin(int index) =>
        DownFirst
            ? (Left + index / Rows * StepX, Top + index % Rows * StepY)
            : (Left + index % Columns * StepX, Top + index / Columns * StepY);

    /// <summary>The tile at a row and a column, possibly past the last, as <see cref="NestedGrid.IndexOf"/>.</summary>
    public int IndexOf(int row, int column) => DownFirst ? column * Rows + row : row * Columns + column;

    /// <summary>The row and the column of tile <paramref name="index"/>.</summary>
    public (int Row, int Column) PlaceOf(int index) =>
        DownFirst ? (index % Rows, index / Rows) : (index / Columns, index % Columns);

    public int IndexAt(double x, double y)
    {
        if (Count == 0)
        {
            return -1;
        }

        var column = (int)Math.Floor((x - Left) / StepX);
        var row = (int)Math.Floor((y - Top) / StepY);
        if (column < 0 || column >= Columns || row < 0 || row >= Rows)
        {
            return -1;
        }

        if (x - Left - column * StepX > TileWidth || y - Top - row * StepY > TileHeight)
        {
            return -1;
        }

        var index = IndexOf(row, column);
        return index < Count ? index : -1;
    }

    public GridBlock InReadingOrder(GridBlock block) => block.InReadingOrder(DownFirst);

    public bool Holds(GridBlock block, int index) => InReadingOrder(block).Contains(index, Stride);

    public int CountIn(GridBlock block) => InReadingOrder(block).CountIn(Count, Stride);

    public (int FirstColumn, int LastColumn, int FirstRow, int LastRow) Overlapping(
        double left,
        double top,
        double right,
        double bottom)
    {
        if (Count == 0)
        {
            return (0, -1, 0, -1);
        }

        var firstColumn = Math.Max(0, (int)Math.Floor((left - Left) / StepX));
        var lastColumn = Math.Min(Columns - 1, (int)Math.Floor((right - Left) / StepX));
        var firstRow = Math.Max(0, (int)Math.Floor((top - Top) / StepY));
        var lastRow = Math.Min(Rows - 1, (int)Math.Floor((bottom - Top) / StepY));
        return (firstColumn, lastColumn, firstRow, lastRow);
    }

    /// <summary>
    /// Exactly the tiles a rectangle touches, as a block of rows and columns;
    /// see <see cref="GridBlock.Touching"/>.
    /// </summary>
    public GridBlock Touching(double left, double top, double right, double bottom) =>
        GridBlock.Touching(Count, Columns, Rows, Left, Top, StepX, StepY, TileWidth, TileHeight, left, top, right, bottom);
}

/// <summary>
/// The nested canvas: every folder is a cell, and what is in it - sub-folders
/// above, files below - is laid out beneath its header, inside it.
///
/// A sub-folder is at most half its parent (<see cref="MaximumScale"/>), and
/// smaller when it has to be for all of its siblings to fit - that is the one
/// rule, and everything else follows from it.  Because every child is inside
/// its parent, a whole disk has a finite size however deep it goes, so the
/// entire tree is on the canvas at once and "fit all" really shows all of it.
/// Deep folders are correspondingly tiny; zooming is how you get to them, and
/// marks are how you find them without zooming (see the beacons on the canvas).
///
/// Siblings all get the same size.  Sizing them by content, as a disk-usage
/// treemap does, would need every subtree read before its parent could be
/// drawn, and would move a folder every time something below it changed.  An
/// equal share depends only on the parent's own listing, so a folder's place
/// is known the moment its parent is read and does not change after.
///
/// When a folder holds both, the space under the header is split: sub-folders
/// get the top, files the bottom, each in proportion to how much they need.  A
/// sub-folder is a whole world and a file is one line, so a folder counts four
/// times as much as a file, and neither side is ever squeezed below a
/// readable strip.
/// </summary>
public static class NestedLayout
{
    /// <summary>Width over height of every cell.  Close to a screen, so "zoom into this folder" fills one.</summary>
    public const double Aspect = 1.6;

    /// <summary>Height of a cell whose width is one.</summary>
    public const double CellHeight = 1 / Aspect;

    /// <summary>Band at the top of a cell that holds the folder's own name.</summary>
    public const double HeaderHeight = 0.075;

    /// <summary>Margin inside the left, right and bottom edges.</summary>
    public const double Padding = 0.025;

    /// <summary>Space between neighbouring children, as a fraction of a child's width.</summary>
    public const double Gap = 0.08;

    /// <summary>
    /// A child is never more than half its parent.  With one or two children
    /// that is the size they get; with more they get whatever lets all of them
    /// fit under the header.
    /// </summary>
    public const double MaximumScale = 0.5;

    /// <summary>Width over height of a file tile: an icon and a name.</summary>
    public const double FileAspect = 5.5;

    /// <summary>Space between file tiles, as a fraction of a tile's height.</summary>
    public const double FileGap = 0.18;

    /// <summary>A tile is never wider than this share of its folder, so two files do not become two banners.</summary>
    public const double MaximumFileWidth = 0.3;

    /// <summary>Space between the folders and the files under them.</summary>
    public const double ZoneGap = 0.015;

    /// <summary>What a sub-folder weighs against a file when the two share a cell.</summary>
    public const double FolderWeight = 4;

    public const double ContentWidth = 1 - 2 * Padding;

    public const double ContentHeight = CellHeight - HeaderHeight - Padding;

    /// <summary>
    /// How the space under the header is shared between <paramref name="folders"/>
    /// sub-folders and <paramref name="files"/> files: the height of each zone.
    /// </summary>
    public static (double FolderHeight, double FileHeight) Split(int folders, int files)
    {
        if (files <= 0)
        {
            return (ContentHeight, 0);
        }

        if (folders <= 0)
        {
            return (0, ContentHeight);
        }

        var share = files / (files + FolderWeight * folders);
        share = Math.Clamp(share, 0.14, 0.72);
        var available = ContentHeight - ZoneGap;
        var fileHeight = available * share;
        return (available - fileHeight, fileHeight);
    }

    /// <summary>
    /// The grid that gives <paramref name="count"/> equal sub-folders the largest
    /// size at which all of them fit in the whole content area.
    /// </summary>
    public static NestedGrid GridFor(int count) => GridFor(count, ContentHeight);

    /// <summary>
    /// <see cref="GridFor(int, double)"/> read down first when
    /// <paramref name="downFirst"/> says so.  The rows are the same either
    /// way, and so is the size of a child; read down first, the columns are
    /// only as many as the rows need - four children in three columns of two
    /// rows fill two of them, where across they fill a row and a half - and
    /// the block is centred on what it really takes.
    /// </summary>
    public static NestedGrid GridFor(int count, double height, bool downFirst)
    {
        var grid = GridFor(count, height);
        if (!downFirst || grid.IsEmpty)
        {
            return grid;
        }

        var columns = (grid.Count + grid.Rows - 1) / grid.Rows;
        var width = columns * grid.Scale + (columns - 1) * grid.Gap;
        return grid with
        {
            Columns = columns,
            Left = Padding + (ContentWidth - width) / 2,
            DownFirst = true
        };
    }

    /// <summary>
    /// The grid that gives <paramref name="count"/> equal sub-folders the largest
    /// size at which all of them fit in a strip <paramref name="height"/> tall
    /// directly under the header.
    /// </summary>
    public static NestedGrid GridFor(int count, double height)
    {
        if (count <= 0 || height <= 0)
        {
            return NestedGrid.Empty;
        }

        var bestColumns = 1;
        var bestScale = 0.0;

        // The best column count is close to the one that makes the block the
        // shape of the area; searching a window around it finds the exact
        // optimum without trying fifty thousand counts for a folder of fifty
        // thousand entries.
        // Gaps included: without them the estimate drifts from the optimum
        // as the count grows, and past thirty thousand it left the window.
        var estimate = Math.Sqrt(count * (CellHeight + Gap) * ContentWidth / (height * (1 + Gap)));
        var from = count <= 64 ? 1 : Math.Max(1, (int)Math.Floor(estimate) - 4);
        var to = count <= 64 ? count : Math.Min(count, (int)Math.Ceiling(estimate) + 4);

        for (var columns = from; columns <= to; columns++)
        {
            var rows = (count + columns - 1) / columns;
            var scale = ScaleFor(columns, rows, height);
            if (scale > bestScale + 1e-12)
            {
                bestScale = scale;
                bestColumns = columns;
            }
        }

        var scaleUsed = Math.Min(MaximumScale, bestScale);

        // Having settled the size, take the fewest columns that still hold
        // everything in the rows available: with room to spare this keeps a
        // short list of children from spreading into a thin wide strip.
        var bestRows = (count + bestColumns - 1) / bestColumns;
        for (var columns = 1; columns < bestColumns; columns++)
        {
            var rows = (count + columns - 1) / columns;
            if (ScaleFor(columns, rows, height) >= scaleUsed - 1e-12)
            {
                bestColumns = columns;
                bestRows = rows;
                break;
            }
        }

        var gap = Gap * scaleUsed;
        var width = bestColumns * scaleUsed + (bestColumns - 1) * gap;
        var left = Padding + (ContentWidth - width) / 2;
        return new NestedGrid(count, bestColumns, bestRows, scaleUsed, left, HeaderHeight);
    }

    /// <summary>
    /// File tiles filling a strip <paramref name="height"/> tall whose top is at
    /// <paramref name="top"/>: as large as they can be with every file in it.
    /// </summary>
    public static NestedFileGrid FileGridFor(int count, double top, double height)
    {
        if (count <= 0 || height <= 0)
        {
            return NestedFileGrid.Empty;
        }

        var bestColumns = 1;
        var bestWidth = 0.0;
        var estimate = Math.Sqrt(count * ContentWidth * (1 + FileGap) / (height * FileAspect * (1 + FileGap / FileAspect)));
        var from = count <= 64 ? 1 : Math.Max(1, (int)Math.Floor(estimate) - 4);
        var to = count <= 64 ? count : Math.Min(count, (int)Math.Ceiling(estimate) + 4);
        for (var columns = from; columns <= to; columns++)
        {
            var rows = (count + columns - 1) / columns;
            var width = TileWidthFor(columns, rows, height);
            if (width > bestWidth + 1e-12)
            {
                bestWidth = width;
                bestColumns = columns;
            }
        }

        var tileWidth = Math.Min(MaximumFileWidth, bestWidth);

        // As with folders: once the size is capped, use no more columns than
        // it takes, so a handful of files reads as a short list.
        for (var columns = 1; columns < bestColumns; columns++)
        {
            var rows = (count + columns - 1) / columns;
            if (TileWidthFor(columns, rows, height) >= tileWidth - 1e-12)
            {
                bestColumns = columns;
                break;
            }
        }

        var bestRows = (count + bestColumns - 1) / bestColumns;
        return new NestedFileGrid(count, bestColumns, bestRows, tileWidth, Padding, top);
    }

    /// <summary>
    /// <see cref="FileGridFor(int, double, double)"/> read down first when
    /// <paramref name="downFirst"/> says so: the same rows and tiles, and
    /// only as many columns as the rows need.
    /// </summary>
    public static NestedFileGrid FileGridFor(int count, double top, double height, bool downFirst)
    {
        var grid = FileGridFor(count, top, height);
        if (!downFirst || grid.IsEmpty)
        {
            return grid;
        }

        return grid with { Columns = (grid.Count + grid.Rows - 1) / grid.Rows, DownFirst = true };
    }

    /// <summary>The largest child size that fits <paramref name="columns"/> by <paramref name="rows"/> in a strip.</summary>
    private static double ScaleFor(int columns, int rows, double height)
    {
        var byWidth = ContentWidth / (columns + (columns - 1) * Gap);
        var byHeight = height / (rows * CellHeight + (rows - 1) * Gap);
        return Math.Min(byWidth, byHeight);
    }

    private static double TileWidthFor(int columns, int rows, double height)
    {
        // A row step is tile + gap, the gap a fraction of the tile's height.
        var tileHeightForWidth = 1 / FileAspect;
        var byWidth = ContentWidth / (columns + (columns - 1) * FileGap * tileHeightForWidth);
        var byHeight = height / (rows * tileHeightForWidth + (rows - 1) * FileGap * tileHeightForWidth);
        return Math.Min(byWidth, byHeight);
    }
}
