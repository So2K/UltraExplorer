using System.Windows;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

/// <summary>
/// What a rectangle drawn with the mouse does to what was selected before it,
/// fixed when the button goes down - so Ctrl pressed half way to zoom with
/// the wheel does not turn a replacement into a toggle.  These are the rules
/// Explorer has, as the Files app reproduces them.
/// </summary>
internal enum NestedSelectMode
{
    /// <summary>No modifier: what the rectangle touches, and nothing else anywhere.</summary>
    Replace,

    /// <summary>Shift: the selection from before, plus what the rectangle touches.</summary>
    Add,

    /// <summary>Ctrl, with or without Shift: what the rectangle touches flips against the selection from before.</summary>
    Toggle
}

/// <summary>
/// A selection rectangle being drawn over one folder's sub-folders and files
/// - its <see cref="Container"/>.  Only that folder's own items can be
/// touched, never what is inside them, which is the rule Explorer and Figma
/// have: a rectangle started inside a big sub-folder selects in there.
///
/// <para>Where it started is kept in the container's own unit frame, where
/// the container is one unit wide, so a zoom or a pan during the drag - the
/// wheel, or the view sliding on by itself at an edge - keeps the start on
/// the same spot of the same content, however deep the folder is.  The other
/// corner is the pointer, in the view.</para>
///
/// <para>What it touches is worked out once a frame, never per mouse move,
/// as a block of rows and columns per zone (<see cref="GridBlock"/>): a
/// thousand-hertz mouse costs one update per screen refresh, and a rectangle
/// over ten thousand tiles is eight numbers.  Whether an item is lit is then
/// a division, four comparisons and a bit: <see cref="IsSelected"/>.</para>
/// </summary>
internal sealed class NestedMarquee
{
    public NestedMarquee(NestedFolder container, double startX, double startY, Point pointer, NestedSelectMode mode)
    {
        Container = container;
        StartX = startX;
        StartY = startY;
        Pointer = pointer;
        Mode = mode;
    }

    public NestedFolder Container { get; }

    /// <summary>The start, in the container's unit frame: (press − cell origin) / cell width.</summary>
    public double StartX { get; }

    public double StartY { get; }

    /// <summary>The other corner: the pointer, in view DIPs, where it last was - outside the view too while the mouse is captured.</summary>
    public Point Pointer { get; set; }

    public NestedSelectMode Mode { get; }

    /// <summary>The sub-folders touched, as the container's grid was placed when they were worked out.</summary>
    public GridBlock Folders { get; private set; } = GridBlock.Empty;

    /// <summary>The file tiles touched.</summary>
    public GridBlock Files { get; private set; } = GridBlock.Empty;

    /// <summary>The children and files lists the blocks and base bits were worked out against.</summary>
    public object? ChildrenRef { get; private set; }

    public object? FilesRef { get; private set; }

    /// <summary>What was selected in the container before the drag, sub-folders by their place in the grid.</summary>
    public ulong[] BaseFolders { get; private set; } = [];

    /// <summary>What was selected in the container before the drag, files by their tile.</summary>
    public ulong[] BaseFiles { get; private set; } = [];

    /// <summary>Which sub-folders match the name filter, by place; null while there is no filter.</summary>
    public ulong[]? FolderMatches { get; private set; }

    /// <summary>Which files match the name filter, by tile; null while there is no filter.</summary>
    public ulong[]? FileMatches { get; private set; }

    /// <summary>The filter stamp <see cref="FolderMatches"/> and <see cref="FileMatches"/> were made for.</summary>
    public int MatchStamp { get; private set; } = -1;

    /// <summary>Bumped whenever what the rectangle lights changed: the layer that draws the selection redraws on it.</summary>
    public long Version { get; private set; }

    /// <summary>How many items will be selected when it is let go, as last counted; -1 before the first count.</summary>
    public int HitCount { get; set; } = -1;

    /// <summary>How many of the container's items match the filter, for "N of M"; -1 with no filter.</summary>
    public int MatchCount { get; set; } = -1;

    /// <summary>When the count was last handed on, on the frame clock.</summary>
    public TimeSpan CountRaisedAt { get; set; } = TimeSpan.MinValue;

    public bool CountStale { get; set; } = true;

    /// <summary>When the pointer came into the band along an edge where the view slides by itself; null while it is not in one.</summary>
    public TimeSpan? EdgeSince { get; set; }

    /// <summary>When the last step of sliding was taken, for the next one's length of time.</summary>
    public TimeSpan LastEdgeStep { get; set; }

    /// <summary>Whether the view is sliding by itself because the pointer is at an edge.</summary>
    public bool AutoPanning { get; set; }

    /// <summary>
    /// Takes this frame's blocks.  True when they or the lists under them
    /// changed - the lit items are different, and the selection's layer has
    /// to be drawn again.
    /// </summary>
    public bool SetBlocks(GridBlock folders, GridBlock files)
    {
        if (folders == Folders && files == Files
            && ReferenceEquals(ChildrenRef, Container.Children) && ReferenceEquals(FilesRef, Container.Files))
        {
            return false;
        }

        Folders = folders;
        Files = files;
        Version++;
        CountStale = true;
        return true;
    }

    /// <summary>
    /// Takes what was selected in the container before the drag, in the
    /// order its items are placed now: once at the start, and again whenever
    /// the container was sorted or read again during the drag.
    /// </summary>
    public void SetBase(ulong[] folders, ulong[] files)
    {
        ChildrenRef = Container.Children;
        FilesRef = Container.Files;
        BaseFolders = folders;
        BaseFiles = files;
        Version++;
        CountStale = true;
    }

    /// <summary>Whether the lists the base was taken against are still the container's.</summary>
    public bool IsBaseCurrent =>
        ReferenceEquals(ChildrenRef, Container.Children) && ReferenceEquals(FilesRef, Container.Files);

    public void SetMatches(ulong[]? folders, ulong[]? files, int stamp)
    {
        FolderMatches = folders;
        FileMatches = files;
        MatchStamp = stamp;
        Version++;
        CountStale = true;
    }

    /// <summary>Whether the rectangle touches sub-folder <paramref name="index"/>, and it matches the filter if there is one.</summary>
    public bool HitsFolder(int index) =>
        Container.Grid.Holds(Folders, index) && (FolderMatches is null || Bit(FolderMatches, index));

    /// <summary>Whether the rectangle touches file tile <paramref name="index"/>, and it matches the filter if there is one.</summary>
    public bool HitsFile(int index) =>
        Container.FileGrid.Holds(Files, index) && (FileMatches is null || Bit(FileMatches, index));

    /// <summary>Whether a sub-folder of the container is lit: its state before the drag and the rectangle, by the mode.</summary>
    public bool IsFolderSelected(int index) => IsSelected(Bit(BaseFolders, index), HitsFolder(index));

    /// <summary>Whether a file tile of the container is lit.</summary>
    public bool IsFileSelected(int index) => IsSelected(Bit(BaseFiles, index), HitsFile(index));

    /// <summary>The mode applied to one item: selected before, touched now.</summary>
    public bool IsSelected(bool before, bool touched) => Mode switch
    {
        NestedSelectMode.Replace => touched,
        NestedSelectMode.Add => before || touched,
        _ => before ^ touched
    };

    /// <summary>
    /// How many of the container's items will be selected when the drag is
    /// let go: in constant time for a replacement with no filter, otherwise
    /// line by line of the reading order over the blocks with the base bits
    /// beside them - each line of a block is one run of places in a row.
    /// </summary>
    public int CountSelected()
    {
        var grid = Container.Grid;
        var tiles = Container.FileGrid;
        var total = 0;
        total += CountZone(grid.InReadingOrder(Folders), grid.Stride, Container.Children.Count, BaseFolders, FolderMatches);
        total += CountZone(tiles.InReadingOrder(Files), tiles.Stride, Container.Files.Count, BaseFiles, FileMatches);
        return total;
    }

    /// <param name="block">The block as lines of the reading order (<see cref="GridBlock.InReadingOrder"/>).</param>
    /// <param name="columns">Places along one line: the grid's <see cref="NestedGrid.Stride"/>.</param>
    private int CountZone(GridBlock block, int columns, int count, ulong[] before, ulong[]? matches)
    {
        var baseCount = PopCount(before, 0, count);
        if (block.IsEmpty || count == 0)
        {
            return Mode == NestedSelectMode.Replace ? 0 : baseCount;
        }

        if (Mode == NestedSelectMode.Replace && matches is null)
        {
            return block.CountIn(count, columns);
        }

        var touched = 0;
        var touchedBefore = 0;
        for (var row = block.FirstRow; row <= block.LastRow; row++)
        {
            var from = row * columns + block.FirstColumn;
            var to = Math.Min(count, row * columns + block.LastColumn + 1);
            if (from >= to)
            {
                continue;
            }

            if (matches is null)
            {
                touched += to - from;
                touchedBefore += PopCount(before, from, to);
            }
            else
            {
                for (var index = from; index < to; index++)
                {
                    if (Bit(matches, index))
                    {
                        touched++;
                        if (Bit(before, index))
                        {
                            touchedBefore++;
                        }
                    }
                }
            }
        }

        return Mode switch
        {
            NestedSelectMode.Replace => touched,
            NestedSelectMode.Add => baseCount + touched - touchedBefore,
            _ => baseCount + touched - 2 * touchedBefore
        };
    }

    public static bool Bit(ulong[] bits, int index) =>
        (uint)(index >> 6) < (uint)bits.Length && (bits[index >> 6] & (1UL << index)) != 0;

    public static void SetBit(ulong[] bits, int index) => bits[index >> 6] |= 1UL << index;

    /// <summary>Set bits among <c>[from, to)</c>, a word at a time.</summary>
    public static int PopCount(ulong[] bits, int from, int to)
    {
        var total = 0;
        to = Math.Min(to, bits.Length << 6);
        while (from < to)
        {
            var word = from >> 6;
            var offset = from & 63;
            var take = Math.Min(64 - offset, to - from);
            var mask = take == 64 ? ulong.MaxValue : ((1UL << take) - 1) << offset;
            total += System.Numerics.BitOperations.PopCount(bits[word] & mask);
            from += take;
        }

        return total;
    }

    // ---- sliding the view at its edges --------------------------------------------

    /// <summary>The band along the view's edges where the view slides by itself: 32 DIPs, or a tenth of a small view.</summary>
    public static double EdgeBand(double viewWidth, double viewHeight) => Math.Min(32, 0.1 * Math.Min(viewWidth, viewHeight));

    /// <summary>Slowest sliding, in DIPs a second: at the band's inner edge.</summary>
    public const double MinimumPanSpeed = 300;

    /// <summary>Fastest sliding, reached 96 DIPs past the band.</summary>
    public const double MaximumPanSpeed = 1600;

    /// <summary>How far past the band full speed is reached.</summary>
    public const double FullSpeedBeyondBand = 96;

    /// <summary>How long the pointer has to stay in the band inside the view before the view starts to slide.</summary>
    public static readonly TimeSpan EdgeDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>How long sliding takes to reach its full speed once it starts.</summary>
    public static readonly TimeSpan EdgeRamp = TimeSpan.FromMilliseconds(400);

    /// <summary>The longest step a slow frame is allowed to take, so a hitch never throws the view.</summary>
    public static readonly TimeSpan EdgeStepLimit = TimeSpan.FromMilliseconds(50);

    /// <summary>Sliding stops with the container's rim this far inside the view: never on into empty space.</summary>
    public const double ContainerStopMargin = 24;

    /// <summary>
    /// Sliding speed at <paramref name="depth"/> DIPs into the band (or past
    /// the view's edge, which is deeper still): rising with the square of the
    /// depth from 300 to 1600 DIPs a second - the range Android's list
    /// scrolling uses - so a pointer just inside the edge creeps and one well
    /// outside races.  In screen DIPs, so it looks the same at any zoom.
    /// </summary>
    public static double PanSpeed(double depth, double band)
    {
        var t = Math.Clamp(depth / (band + FullSpeedBeyondBand), 0, 1);
        return MinimumPanSpeed + (MaximumPanSpeed - MinimumPanSpeed) * t * t;
    }

    /// <summary>How far into its full speed sliding is, <paramref name="elapsed"/> after it may start: a smoothstep over <see cref="EdgeRamp"/>.</summary>
    public static double Ramp(TimeSpan elapsed)
    {
        var t = Math.Clamp(elapsed.TotalMilliseconds / EdgeRamp.TotalMilliseconds, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// How far the content slides along one axis in <paramref name="seconds"/>,
    /// positive to bring what is past the low edge into view: only while the
    /// pointer is in that edge's band and the container still reaches past
    /// that edge, and never so far that its rim comes more than
    /// <see cref="ContainerStopMargin"/> inside.  Zero otherwise.
    /// </summary>
    public static double EdgeStep(double pointer, double size, double low, double high, double band, double seconds)
    {
        var intoLow = band - pointer;
        if (intoLow > 0 && low < ContainerStopMargin)
        {
            return Math.Min(PanSpeed(intoLow, band) * seconds, ContainerStopMargin - low);
        }

        var intoHigh = pointer - (size - band);
        if (intoHigh > 0 && high > size - ContainerStopMargin)
        {
            return -Math.Min(PanSpeed(intoHigh, band) * seconds, high - (size - ContainerStopMargin));
        }

        return 0;
    }

    /// <summary>Whether the pointer is in the band along any edge where the container reaches past that edge.</summary>
    public static bool WantsToSlide(Point pointer, Size view, Rect cell, double band) =>
        EdgeStep(pointer.X, view.Width, cell.Left, cell.Right, band, 1) != 0
        || EdgeStep(pointer.Y, view.Height, cell.Top, cell.Bottom, band, 1) != 0;

    /// <summary>Whether the pointer is out of the view altogether, where sliding starts at once.</summary>
    public static bool IsOutside(Point pointer, Size view) =>
        pointer.X < 0 || pointer.Y < 0 || pointer.X > view.Width || pointer.Y > view.Height;
}
