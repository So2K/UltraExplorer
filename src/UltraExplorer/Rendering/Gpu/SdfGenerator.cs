using System.Runtime.CompilerServices;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// A signed distance field from a glyph's coverage, in the manner of Mapbox's
/// TinySDF (BSD-2-Clause, https://github.com/mapbox/tiny-sdf): the
/// Felzenszwalb-Huttenlocher exact Euclidean distance transform over the
/// raster, seeded at sub-pixel precision from the antialiased edge pixels.
///
/// The coverage comes from DirectWrite's grayscale rasteriser, so the
/// outline is known to better than a pixel: a partly covered pixel says how
/// much of it is ink, and the coverage around it says which way the edge
/// faces.  Measured against distance fields made from an eight-times finer
/// raster, TinySDF's own seeding - (0.5 - coverage) squared, added to the
/// squared distance between pixel centres - is off by 0.17 texel on average
/// near the outline and by up to 0.76, worst beside edges that fall on a
/// pixel boundary, where it reads a whole texel for half of one.  That error
/// does not move the outline, but it halves or doubles how fast the field
/// changes across it, and so how soft that edge is drawn.  So here the
/// transform only finds, for every texel, the nearest edge pixel; the
/// distance is then measured exactly to the pieces of outline in the pixels
/// around it:
/// - in a partly covered pixel, a straight edge whose direction is the
///   Sobel gradient of the coverage and whose offset reproduces the
///   pixel's coverage (Gustavson and Strand's anti-aliased distance
///   transform, 2011), clipped to the pixel;
/// - where a fully covered pixel meets an empty one, the side they share.
///
/// Output: one byte per texel, 255 * (0.5 - d / (2 * spread)), clamped, where
/// d is the signed distance in texels (positive outside).  So the edge is
/// 127.5, ink is above it, and the field reaches 0 and 255 exactly
/// <c>spread</c> texels out and in - the shader turns it back into
/// coverage with its pixel range 2 * spread * fontPx / tierEm.  The field is
/// <c>spread</c> texels larger than the glyph on every side so the outer
/// glow has room.
///
/// One instance per thread: the scratch arrays are reused between glyphs.
/// </summary>
internal sealed class SdfGenerator
{
    private const double Infinity = 1e20;
    private const byte Paper = 0;
    private const byte Ink = 1;
    private const byte Edge = 2;

    /// <summary>Squared distance from the nearest edge pixel's centre within which all its neighbours' outline is measured.</summary>
    private const double NearBand = 2.5 * 2.5;

    [ThreadStatic]
    private static SdfGenerator? t_instance;

    private double[] _grid = [];
    private int[] _row = [];
    private int[] _nearest = [];
    private byte[] _kind = [];
    private byte[] _sides = [];
    private double[] _segments = [];
    private double[] _edgeDistance = [];
    private double[] _f = [];
    private double[] _z = [];
    private int[] _v = [];

    /// <summary>The calling thread's generator.</summary>
    public static SdfGenerator ForThread => t_instance ??= new SdfGenerator();

    /// <summary>
    /// Writes the field of a <paramref name="width"/> x <paramref name="height"/>
    /// coverage bitmap (one byte per pixel, rows packed) into
    /// <paramref name="field"/>, which is (width + 2 * spread) x (height + 2 * spread),
    /// rows packed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Generate(ReadOnlySpan<byte> coverage, int width, int height, int spread, Span<byte> field)
    {
        var fieldWidth = width + 2 * spread;
        var fieldHeight = height + 2 * spread;
        var size = fieldWidth * fieldHeight;
        if (field.Length < size || coverage.Length < width * height)
        {
            throw new ArgumentException("The buffers are smaller than the glyph.");
        }

        Ensure(size, Math.Max(fieldWidth, fieldHeight));
        var kind = _kind;
        var sides = _sides;
        var grid = _grid;
        Array.Clear(kind, 0, size);
        Array.Clear(sides, 0, size);
        Array.Fill(grid, Infinity, 0, size);

        // What each texel is: paper (the border is all paper), ink, or an
        // edge pixel with its piece of outline.
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var alpha = coverage[y * width + x];
                if (alpha == 0)
                {
                    continue;
                }

                var index = (y + spread) * fieldWidth + x + spread;
                if (alpha == 255)
                {
                    kind[index] = Ink;
                    continue;
                }

                kind[index] = Edge;
                grid[index] = 0;
                StoreEdge(coverage, width, height, x, y, index, x + spread, y + spread);
            }
        }

        // Where ink meets paper at a pixel side, that side is outline.
        for (var y = spread; y < spread + height; y++)
        {
            for (var x = spread; x < spread + width; x++)
            {
                var index = y * fieldWidth + x;
                if (kind[index] != Ink)
                {
                    continue;
                }

                var mask = (kind[index - 1] == Paper ? 1 : 0)
                    | (kind[index - fieldWidth] == Paper ? 2 : 0)
                    | (kind[index + 1] == Paper ? 4 : 0)
                    | (kind[index + fieldWidth] == Paper ? 8 : 0);
                if (mask != 0)
                {
                    sides[index] = (byte)mask;
                    grid[index] = 0;
                }
            }
        }

        // The nearest edge pixel of every texel, by the distance between
        // centres; then the exact distance to the outline around it.
        Transform(grid, fieldWidth, fieldHeight);
        var nearest = _nearest;
        var perTexel = 255.0 / (2 * spread);

        // A texel further than this from every edge pixel's centre is more
        // than the spread from the outline (each piece of outline lies within
        // its pixel, at most 0.71 from the centre), so its value is 0 or 255
        // whatever the exact distance.
        var reach = (spread + 0.75) * (spread + 0.75);
        for (var y = 0; y < fieldHeight; y++)
        {
            var rowStart = y * fieldWidth;
            for (var x = 0; x < fieldWidth; x++)
            {
                var index = rowStart + x;
                var texelKind = kind[index];
                var inside = texelKind == Ink || (texelKind == Edge && _edgeDistance[index] < 0);
                var squared = grid[index];
                if (squared > reach)
                {
                    field[index] = inside ? (byte)255 : (byte)0;
                    continue;
                }

                // Within a few texels of the outline, where the shader draws
                // the edge, every piece of outline around the nearest edge
                // pixel is measured; further out the nearest pixel's own
                // piece is close enough.
                var distance = DistanceToOutline(x + 0.5, y + 0.5, nearest[index], fieldWidth, fieldHeight, squared <= NearBand ? 1 : 0);
                var value = 127.5 + (inside ? distance : -distance) * perTexel;
                field[index] = value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)(int)(value + 0.5);
            }
        }
    }

    /// <summary>
    /// The piece of outline in a partly covered pixel: a straight edge facing
    /// the coverage gradient, placed so the pixel is covered as much as the
    /// rasteriser says, clipped to the pixel's square.
    /// </summary>
    private void StoreEdge(ReadOnlySpan<byte> coverage, int width, int height, int x, int y, int index, int fieldX, int fieldY)
    {
        const double Root2 = 1.4142135623730951;
        var alpha = At(coverage, width, height, x, y);
        var gx = -At(coverage, width, height, x - 1, y - 1) - Root2 * At(coverage, width, height, x - 1, y) - At(coverage, width, height, x - 1, y + 1)
            + At(coverage, width, height, x + 1, y - 1) + Root2 * At(coverage, width, height, x + 1, y) + At(coverage, width, height, x + 1, y + 1);
        var gy = -At(coverage, width, height, x - 1, y - 1) - Root2 * At(coverage, width, height, x, y - 1) - At(coverage, width, height, x + 1, y - 1)
            + At(coverage, width, height, x - 1, y + 1) + Root2 * At(coverage, width, height, x, y + 1) + At(coverage, width, height, x + 1, y + 1);
        var length = Math.Sqrt(gx * gx + gy * gy);
        var distance = OffsetFromCoverage(alpha, length == 0 ? 0 : gx / length, length == 0 ? 0 : gy / length);
        _edgeDistance[index] = distance;

        var segment = index * 4;
        var centreX = fieldX + 0.5;
        var centreY = fieldY + 0.5;
        if (length == 0)
        {
            // No direction: a lone speck, smaller than a pixel.  Its outline
            // is taken to be its centre.
            _segments[segment] = _segments[segment + 2] = centreX;
            _segments[segment + 1] = _segments[segment + 3] = centreY;
            return;
        }

        // The edge passes through the centre moved the offset towards the
        // ink, at right angles to the gradient.
        var normalX = gx / length;
        var normalY = gy / length;
        var pointX = centreX + distance * normalX;
        var pointY = centreY + distance * normalY;
        var alongX = -normalY;
        var alongY = normalX;
        double low = -2, high = 2;
        Clip(pointX, alongX, fieldX, fieldX + 1, ref low, ref high);
        Clip(pointY, alongY, fieldY, fieldY + 1, ref low, ref high);
        if (low > high)
        {
            low = high = 0;
        }

        _segments[segment] = pointX + low * alongX;
        _segments[segment + 1] = pointY + low * alongY;
        _segments[segment + 2] = pointX + high * alongX;
        _segments[segment + 3] = pointY + high * alongY;
    }

    private static void Clip(double start, double step, double minimum, double maximum, ref double low, ref double high)
    {
        if (Math.Abs(step) < 1e-12)
        {
            if (start < minimum || start > maximum)
            {
                low = 1;
                high = 0;
            }

            return;
        }

        var a = (minimum - start) / step;
        var b = (maximum - start) / step;
        if (a > b)
        {
            (a, b) = (b, a);
        }

        low = Math.Max(low, a);
        high = Math.Min(high, b);
    }

    /// <summary>
    /// How far a pixel's centre is from a straight edge that leaves
    /// <paramref name="alpha"/> of the pixel covered, positive when the
    /// centre is on the paper side; (nx, ny) is the edge's normal, pointing
    /// into the ink.  Gustavson and Strand's closed form: a corner triangle,
    /// a band across the pixel, or the complementary triangle, by how much
    /// is covered.  With no direction it falls back to TinySDF's 0.5 - alpha.
    /// </summary>
    private static double OffsetFromCoverage(double alpha, double nx, double ny)
    {
        if (nx == 0 || ny == 0)
        {
            return 0.5 - alpha;
        }

        var gx = Math.Abs(nx);
        var gy = Math.Abs(ny);
        if (gx < gy)
        {
            (gx, gy) = (gy, gx);
        }

        var a1 = 0.5 * gy / gx;
        if (alpha < a1)
        {
            return 0.5 * (gx + gy) - Math.Sqrt(2 * gx * gy * alpha);
        }

        if (alpha < 1 - a1)
        {
            return (0.5 - alpha) * gx;
        }

        return -0.5 * (gx + gy) + Math.Sqrt(2 * gx * gy * (1 - alpha));
    }

    /// <summary>
    /// The distance from (px, py) to the outline pieces in the pixels within
    /// <paramref name="radius"/> of the nearest edge pixel.  Every piece lies
    /// inside its own pixel, and the nearest edge pixel by centre is next to
    /// the one holding the nearest piece; measured against a finer raster,
    /// looking further than one pixel out changes nothing visible and costs
    /// three times as much.  Written out with squared distances and no
    /// calls, because the smoke tests run it unoptimised.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private double DistanceToOutline(double px, double py, int seed, int fieldWidth, int fieldHeight, int radius)
    {
        var seedX = seed % fieldWidth;
        var seedY = seed / fieldWidth;
        var best = double.MaxValue;
        var kinds = _kind;
        var segments = _segments;
        var sides = _sides;
        var top = Math.Max(0, seedY - radius);
        var bottom = Math.Min(fieldHeight - 1, seedY + radius);
        var left = Math.Max(0, seedX - radius);
        var right = Math.Min(fieldWidth - 1, seedX + radius);
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var index = y * fieldWidth + x;
                var kind = kinds[index];
                if (kind == Edge)
                {
                    var segment = index * 4;
                    var x0 = segments[segment];
                    var y0 = segments[segment + 1];
                    var dx = segments[segment + 2] - x0;
                    var dy = segments[segment + 3] - y0;
                    var lengthSquared = dx * dx + dy * dy;
                    var t = lengthSquared == 0 ? 0 : ((px - x0) * dx + (py - y0) * dy) / lengthSquared;
                    t = t < 0 ? 0 : t > 1 ? 1 : t;
                    var ex = x0 + t * dx - px;
                    var ey = y0 + t * dy - py;
                    var squared = ex * ex + ey * ey;
                    if (squared < best)
                    {
                        best = squared;
                    }
                }
                else if (kind == Ink && sides[index] != 0)
                {
                    // A pixel side: the distance across it, and past its
                    // ends the distance to the nearer corner.
                    var mask = sides[index];
                    var clampedX = px < x ? x : px > x + 1 ? x + 1 : px;
                    var clampedY = py < y ? y : py > y + 1 ? y + 1 : py;
                    if ((mask & 1) != 0)
                    {
                        var squared = (px - x) * (px - x) + (py - clampedY) * (py - clampedY);
                        best = squared < best ? squared : best;
                    }

                    if ((mask & 2) != 0)
                    {
                        var squared = (px - clampedX) * (px - clampedX) + (py - y) * (py - y);
                        best = squared < best ? squared : best;
                    }

                    if ((mask & 4) != 0)
                    {
                        var squared = (px - x - 1) * (px - x - 1) + (py - clampedY) * (py - clampedY);
                        best = squared < best ? squared : best;
                    }

                    if ((mask & 8) != 0)
                    {
                        var squared = (px - clampedX) * (px - clampedX) + (py - y - 1) * (py - y - 1);
                        best = squared < best ? squared : best;
                    }
                }
            }
        }

        return Math.Sqrt(best);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double At(ReadOnlySpan<byte> coverage, int width, int height, int column, int row) =>
        column < 0 || row < 0 || column >= width || row >= height ? 0 : coverage[row * width + column] / 255.0;

    private void Ensure(int size, int side)
    {
        if (_grid.Length < size)
        {
            _grid = new double[size];
            _row = new int[size];
            _nearest = new int[size];
            _kind = new byte[size];
            _sides = new byte[size];
            _segments = new double[size * 4];
            _edgeDistance = new double[size];
        }

        if (_f.Length < side + 1)
        {
            _f = new double[side + 1];
            _z = new double[side + 2];
            _v = new int[side + 1];
        }
    }

    /// <summary>
    /// The 2D transform, every column then every row, keeping for each texel
    /// which seed is nearest: the column pass records the nearest row in
    /// each column, the row pass picks the column, and the two give the seed.
    /// </summary>
    private void Transform(double[] grid, int width, int height)
    {
        var row = _row;
        var nearest = _nearest;
        for (var x = 0; x < width; x++)
        {
            Transform1D(grid, x, width, height, row, x, width);
        }

        for (var y = 0; y < height; y++)
        {
            Transform1D(grid, y * width, 1, width, nearest, y * width, 1);
        }

        // nearest holds the nearest column for each texel; look its row up.
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                var column = nearest[index];
                nearest[index] = row[y * width + column] * width + column;
            }
        }
    }

    /// <summary>
    /// Felzenszwalb and Huttenlocher's 1D squared distance transform - the
    /// lower envelope of the parabolas rooted at each sample - writing also
    /// the sample each result came from.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Transform1D(double[] grid, int offset, int stride, int length, int[] source, int sourceOffset, int sourceStride)
    {
        var f = _f;
        var z = _z;
        var v = _v;
        v[0] = 0;
        z[0] = -Infinity;
        z[1] = Infinity;
        f[0] = grid[offset];

        for (int q = 1, k = 0; q < length; q++)
        {
            f[q] = grid[offset + q * stride];
            var q2 = (double)q * q;
            double s;
            do
            {
                var r = v[k];
                s = (f[q] - f[r] + q2 - (double)r * r) / (q - r) / 2;
            }
            while (s <= z[k] && --k > -1);

            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = Infinity;
        }

        for (int q = 0, k = 0; q < length; q++)
        {
            while (z[k + 1] < q)
            {
                k++;
            }

            var r = v[k];
            var qr = q - r;
            grid[offset + q * stride] = f[r] + (double)qr * qr;
            source[sourceOffset + q * sourceStride] = r;
        }
    }
}
