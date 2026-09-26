namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// Places rectangles on one atlas page in shelves: horizontal strips, each
/// as tall as the tallest glyph it was opened for, filled left to right.
/// Glyphs of one tier are all about the same height, which is the case
/// shelves suit best - Firefox's WebRender came to the same conclusion for
/// its glyph cache - and a shelf never needs to move anything, so a placed
/// glyph keeps its texels for the life of the atlas.
///
/// A new glyph goes on the shelf that wastes least height among those tall
/// enough and with room left; failing that a new shelf opens under the last,
/// its height rounded up to a multiple of four so glyphs a texel or two
/// taller still fit later.  Every rectangle gets one texel of gap on its
/// right and bottom, so bilinear sampling at a glyph's edge reads the empty
/// gap and never the next glyph.
///
/// Not thread-safe: the atlas calls it under its lock.
/// </summary>
internal sealed class ShelfPacker
{
    private const int Gap = 1;
    private const int HeightStep = 4;

    private readonly List<Shelf> _shelves = [];

    public ShelfPacker(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The first row no shelf has reached: everything below it is empty.</summary>
    public int UsedHeight => _shelves.Count == 0 ? 0 : _shelves[^1].Top + _shelves[^1].Height;

    public IReadOnlyList<Shelf> Shelves => _shelves;

    /// <summary>Finds room for a <paramref name="width"/> x <paramref name="height"/> rectangle; false when the page is full.</summary>
    public bool TryAllocate(int width, int height, out int x, out int y)
    {
        var needWidth = width + Gap;
        var needHeight = height + Gap;
        x = y = 0;
        if (needWidth > Width || needHeight > Height)
        {
            return false;
        }

        var best = -1;
        var bestWaste = int.MaxValue;
        for (var index = 0; index < _shelves.Count; index++)
        {
            var shelf = _shelves[index];
            if (shelf.Height < needHeight || Width - shelf.Used < needWidth)
            {
                continue;
            }

            var waste = shelf.Height - needHeight;

            // A shelf far taller than the glyph wastes a column of texels
            // for every glyph put there; open a fitting one instead while
            // the page has room.
            if (waste > needHeight / 2 + HeightStep && UsedHeight + RoundUp(needHeight) <= Height)
            {
                continue;
            }

            if (waste < bestWaste)
            {
                best = index;
                bestWaste = waste;
            }
        }

        if (best < 0)
        {
            var top = UsedHeight;
            var shelfHeight = Math.Min(RoundUp(needHeight), Height - top);
            if (shelfHeight < needHeight)
            {
                return false;
            }

            _shelves.Add(new Shelf(top, shelfHeight, 0));
            best = _shelves.Count - 1;
        }

        var chosen = _shelves[best];
        x = chosen.Used;
        y = chosen.Top;
        _shelves[best] = chosen with { Used = chosen.Used + needWidth };
        return true;
    }

    /// <summary>Puts back shelves read from the glyph cache file, so placing continues where it left off.</summary>
    public void Restore(IEnumerable<Shelf> shelves)
    {
        _shelves.Clear();
        _shelves.AddRange(shelves);
    }

    private static int RoundUp(int height) => (height + HeightStep - 1) / HeightStep * HeightStep;

    /// <summary>One strip: its top row, height, and how far along it is filled.</summary>
    internal readonly record struct Shelf(int Top, int Height, int Used);
}
