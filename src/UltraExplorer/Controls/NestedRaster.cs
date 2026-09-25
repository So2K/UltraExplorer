using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace UltraExplorer.Controls;

/// <summary>
/// A plain pixel buffer and the three shapes the nested canvas is made of:
/// filled rectangles, rounded ones, and their outlines.
///
/// WPF would draw these too, but a folder of thirty thousand sub-folders is
/// thirty thousand rectangles on screen at once, and a drawing context charges
/// per call where a pixel loop charges per pixel.  Everything with an edge
/// that matters - text, selection, beacons - is still drawn by WPF on top.
///
/// Colours are premultiplied BGRA, which for the opaque colours used here is
/// the same as plain BGRA.
/// </summary>
internal sealed unsafe class NestedRaster
{
    private uint* _pixels;
    private int _stride;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public void Attach(IntPtr buffer, int width, int height, int strideBytes)
    {
        _pixels = (uint*)buffer;
        Width = width;
        Height = height;
        _stride = strideBytes / 4;
    }

    public void Detach() => _pixels = null;

    public void Clear(uint colour)
    {
        for (var y = 0; y < Height; y++)
        {
            new Span<uint>(_pixels + y * _stride, Width).Fill(colour);
        }
    }

    /// <summary>Fills [x0, x1) x [y0, y1), clipped to the buffer.</summary>
    public void Fill(int x0, int y0, int x1, int y1, uint colour)
    {
        if (x0 < 0) x0 = 0;
        if (y0 < 0) y0 = 0;
        if (x1 > Width) x1 = Width;
        if (y1 > Height) y1 = Height;
        if (x0 >= x1 || y0 >= y1)
        {
            return;
        }

        var length = x1 - x0;
        if (length == 1)
        {
            for (var y = y0; y < y1; y++)
            {
                _pixels[y * _stride + x0] = colour;
            }

            return;
        }

        for (var y = y0; y < y1; y++)
        {
            new Span<uint>(_pixels + y * _stride + x0, length).Fill(colour);
        }
    }

    /// <summary>
    /// Fills a rectangle whose corners are rounded by <paramref name="radius"/>
    /// pixels.  The curve is anti-aliased one pixel deep - enough to stop the
    /// corners of a large cell looking like steps, without paying for coverage
    /// anywhere else.
    /// </summary>
    public void FillRounded(double left, double top, double right, double bottom, double radius, uint colour, bool roundBottom = true)
        => FillRounded(left, top, right, bottom, radius, colour, roundBottom, default);

    /// <summary>
    /// A cell's rim and body in one: exactly the pixels of the rounded rim
    /// rectangle filled first and the body filled over it one pixel in - but
    /// without painting the rim colour under the body only to paint over it.
    /// A cell that covers the screen is the whole screen, so that was a whole
    /// screen of pixels written twice every frame.  The body's plain rows are
    /// left out of the rim's fill; they are the only pixels the body is certain
    /// to cover opaquely, so nothing else about the result changes.
    /// </summary>
    public void FillFramed(double left, double top, double right, double bottom, double radius, uint rim, uint body)
    {
        var innerRadius = Math.Max(0, radius - 1);
        var hole = PlainArea(left + 1, top + 1, right - 1, bottom - 1, innerRadius, roundBottom: true);
        FillRounded(left, top, right, bottom, radius, rim, roundBottom: true, hole);
        FillRounded(left + 1, top + 1, right - 1, bottom - 1, innerRadius, body, roundBottom: true, default);
    }

    /// <summary>The rows and columns a rounded fill paints with one plain fill, corners excluded.</summary>
    private static Hole PlainArea(double left, double top, double right, double bottom, double radius, bool roundBottom)
    {
        var x0 = Px(left);
        var y0 = Px(top);
        var x1 = Px(right);
        var y1 = Px(bottom);
        if (x1 <= x0 || y1 <= y0)
        {
            return default;
        }

        var r = Math.Min(radius, Math.Min(x1 - x0, y1 - y0) / 2.0);
        if (r < 1.5)
        {
            return new Hole(x0, y0, x1, y1);
        }

        var band = (int)Math.Ceiling(r);
        return new Hole(x0, y0 + band, x1, roundBottom ? y1 - band : y1);
    }

    private void FillRounded(double left, double top, double right, double bottom, double radius, uint colour, bool roundBottom, Hole hole)
    {
        var x0 = Px(left);
        var y0 = Px(top);
        var x1 = Px(right);
        var y1 = Px(bottom);
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        var r = Math.Min(radius, Math.Min(x1 - x0, y1 - y0) / 2.0);
        if (r < 1.5)
        {
            FillExcept(x0, y0, x1, y1, colour, hole);
            return;
        }

        var band = (int)Math.Ceiling(r);

        // The straight middle first, in one go.
        var middleTop = y0 + band;
        var middleBottom = roundBottom ? y1 - band : y1;
        FillExcept(x0, middleTop, x1, middleBottom, colour, hole);

        for (var row = 0; row < band; row++)
        {
            // Distance of this row's centre from the corner circle's centre.
            var dy = r - (row + 0.5);
            var inset = r - Math.Sqrt(Math.Max(0, r * r - dy * dy));
            RoundedRow(x0, x1, y0 + row, inset, colour);
            if (roundBottom)
            {
                RoundedRow(x0, x1, y1 - 1 - row, inset, colour);
            }
        }
    }

    /// <summary>Fills [x0, x1) x [y0, y1) except the pixels inside <paramref name="hole"/>.</summary>
    private void FillExcept(int x0, int y0, int x1, int y1, uint colour, Hole hole)
    {
        if (hole.IsEmpty || hole.Right <= x0 || hole.Left >= x1 || hole.Bottom <= y0 || hole.Top >= y1)
        {
            Fill(x0, y0, x1, y1, colour);
            return;
        }

        var top = Math.Max(y0, hole.Top);
        var bottom = Math.Min(y1, hole.Bottom);
        Fill(x0, y0, x1, top, colour);
        Fill(x0, bottom, x1, y1, colour);
        Fill(x0, top, Math.Min(x1, hole.Left), bottom, colour);
        Fill(Math.Max(x0, hole.Right), top, x1, bottom, colour);
    }

    /// <summary>A rectangle of pixels a fill may skip because something opaque will cover it.</summary>
    private readonly record struct Hole(int Left, int Top, int Right, int Bottom)
    {
        public bool IsEmpty => Right <= Left || Bottom <= Top;
    }

    private void RoundedRow(int x0, int x1, int y, double inset, uint colour)
    {
        if (y < 0 || y >= Height)
        {
            return;
        }

        var whole = (int)inset;
        var fraction = inset - whole;
        var left = x0 + whole;
        var right = x1 - whole;
        if (right <= left)
        {
            return;
        }

        if (fraction > 0.02)
        {
            // The pixel the curve passes through gets the share of it that is inside.
            var coverage = 1 - fraction;
            Blend(left, y, colour, coverage);
            Blend(right - 1, y, colour, coverage);
            left++;
            right--;
        }

        Fill(left, y, right, y + 1, colour);
    }

    /// <summary>A rectangle outline <paramref name="thickness"/> pixels wide, inside the rectangle.</summary>
    public void Stroke(int x0, int y0, int x1, int y1, int thickness, uint colour)
    {
        if (x1 - x0 <= 2 * thickness || y1 - y0 <= 2 * thickness)
        {
            Fill(x0, y0, x1, y1, colour);
            return;
        }

        Fill(x0, y0, x1, y0 + thickness, colour);
        Fill(x0, y1 - thickness, x1, y1, colour);
        Fill(x0, y0 + thickness, x0 + thickness, y1 - thickness, colour);
        Fill(x1 - thickness, y0 + thickness, x1, y1 - thickness, colour);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Blend(int x, int y, uint colour, double alpha)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            return;
        }

        ref var target = ref _pixels[y * _stride + x];
        target = Mix(target, colour, alpha);
    }

    /// <summary>
    /// A coordinate as a pixel index.  Deep in, a parent's edge can be
    /// trillions of pixels off screen, and a plain cast of that to int wraps
    /// to a garbage value - the parent's fill then silently goes missing.
    /// Anything that far out is clamped to "far out", which is all it means.
    /// </summary>
    public static int Px(double value) =>
        double.IsNaN(value) ? 0 : value <= -1e8 ? -100_000_000 : value >= 1e8 ? 100_000_000 : (int)Math.Round(value);

    public static int PxFloor(double value) =>
        double.IsNaN(value) ? 0 : value <= -1e8 ? -100_000_000 : value >= 1e8 ? 100_000_000 : (int)Math.Floor(value);

    public static uint Mix(uint under, uint over, double alpha)
    {
        if (alpha >= 1)
        {
            return over;
        }

        if (alpha <= 0)
        {
            return under;
        }

        var a = (uint)(alpha * 256);
        var b = 256 - a;
        var red = (((under >> 16) & 0xFF) * b + ((over >> 16) & 0xFF) * a) >> 8;
        var green = (((under >> 8) & 0xFF) * b + ((over >> 8) & 0xFF) * a) >> 8;
        var blue = ((under & 0xFF) * b + (over & 0xFF) * a) >> 8;
        return 0xFF000000u | (red << 16) | (green << 8) | blue;
    }

    public static uint Pack(Color colour) =>
        0xFF000000u | ((uint)colour.R << 16) | ((uint)colour.G << 8) | colour.B;

    public static Color Unpack(uint colour) =>
        Color.FromRgb((byte)(colour >> 16), (byte)(colour >> 8), (byte)colour);

    public static uint FromHsl(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var sector = (hue % 360 + 360) % 360 / 60.0;
        var second = chroma * (1 - Math.Abs(sector % 2 - 1));
        var (red, green, blue) = (int)sector switch
        {
            0 => (chroma, second, 0.0),
            1 => (second, chroma, 0.0),
            2 => (0.0, chroma, second),
            3 => (0.0, second, chroma),
            4 => (second, 0.0, chroma),
            _ => (chroma, 0.0, second)
        };

        var offset = lightness - chroma / 2;
        return 0xFF000000u
            | (uint)Math.Round((red + offset) * 255) << 16
            | (uint)Math.Round((green + offset) * 255) << 8
            | (uint)Math.Round((blue + offset) * 255);
    }
}
