using System.Numerics;
using System.Runtime.CompilerServices;
using UltraExplorer.Controls;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// The canvas's scene as GPU instances: each shape the walk hands over
/// becomes one <see cref="RectInstance"/> in the frame's list, for
/// <see cref="NestedGpuRenderer"/> to draw with one instanced call.
///
/// The sink does on the CPU, once per shape, every piece of arithmetic the
/// raster does before it touches a pixel, with the raster's own functions:
/// the outline snapped with <see cref="NestedRaster.Px"/>, the radius reduced
/// to fit and squared off below 1.5 pixels, the body found as
/// <c>Px(left + 1)</c> and so on (which is not always one pixel in - halves
/// round to even), the title band's own radius for its own height.  The
/// shader is left only the per-pixel part, so every straight edge lands on
/// exactly the pixel the raster fills and only the anti-aliasing of the
/// rounded corners can differ.
///
/// Shapes wholly outside the frame are dropped, as the raster clips them
/// away.  Every edge is clamped on its own to a guard band of
/// <see cref="GuardBand"/> pixels around the frame before it becomes a
/// float: still off screen, and far from the corners that are on it.
///
/// <see cref="Begin"/> points the sink at a list for a frame; after that
/// nothing it does allocates.
/// </summary>
internal sealed class GpuSink : SceneSink
{
    /// <summary>How far outside the frame an edge may lie before it is clamped.  Radii are far smaller.</summary>
    public const int GuardBand = 1024;

    private InstanceList<RectInstance>? _into;
    private int _width;
    private int _height;
    private int _highX;
    private int _highY;

    /// <summary>The colour the frame starts from: the background given to <see cref="Begin"/>, or what <see cref="Clear"/> asked for.</summary>
    public uint ClearColour { get; private set; }

    /// <summary>
    /// Starts a frame of <paramref name="width"/> x <paramref name="height"/>
    /// device pixels: empties <paramref name="into"/> and sends every shape
    /// after this there.
    /// </summary>
    public void Begin(InstanceList<RectInstance> into, int width, int height, uint background)
    {
        into.Clear();
        _into = into;
        _width = width;
        _height = height;
        _highX = width + GuardBand;
        _highY = height + GuardBand;
        ClearColour = background;
    }

    public override void Clear(uint colour) => ClearColour = colour;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override void Fill(int x0, int y0, int x1, int y1, uint colour)
    {
        if (x0 >= x1 || y0 >= y1 || x1 <= 0 || y1 <= 0 || x0 >= _width || y0 >= _height)
        {
            return;
        }

        ref var instance = ref _into!.Add();
        instance.Outer = Clamped(x0, y0, x1, y1);
        instance.Feature = default;
        instance.HeaderBottom = 0;
        instance.Radius = 0;
        instance.InnerRadii = 0;
        instance.Kind = (uint)RectKind.Plain;
        instance.Body = colour;
        instance.Rim = 0;
        instance.Header = 0;
        instance.Accent = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override void Cell(
        double left,
        double top,
        double right,
        double bottom,
        double radius,
        double headerBottom,
        bool hasStripe,
        int stripeLeft,
        int stripeTop,
        int stripeRight,
        int stripeBottom,
        uint rim,
        uint body,
        uint header,
        uint stripeColour)
    {
        var x0 = NestedRaster.Px(left);
        var y0 = NestedRaster.Px(top);
        var x1 = NestedRaster.Px(right);
        var y1 = NestedRaster.Px(bottom);
        var withHeader = !double.IsNaN(headerBottom);
        var stripe = withHeader && hasStripe;
        if (x1 <= x0 || y1 <= y0 || x1 <= 0 || y1 <= 0 || x0 >= _width || y0 >= _height)
        {
            // Nothing of the cell is drawn - the raster's fills of it return
            // or clip away - but its stripe is a fill of its own.
            if (stripe)
            {
                Fill(stripeLeft, stripeTop, stripeRight, stripeBottom, stripeColour);
            }

            return;
        }

        // The body, one pixel in as the raster finds it, and the title band
        // along its top: their radii are the outer one less a pixel, each
        // reduced for its own rectangle's size.
        var innerLeft = NestedRaster.Px(left + 1);
        var innerTop = NestedRaster.Px(top + 1);
        var innerRight = NestedRaster.Px(right - 1);
        var innerBottom = NestedRaster.Px(bottom - 1);
        var innerRadius = Math.Max(0, radius - 1);
        var kind = (uint)RectKind.Cell;
        var bodyRadius = 0.0;
        if (innerRight > innerLeft && innerBottom > innerTop)
        {
            kind |= (uint)RectFlags.HasBody;
            bodyRadius = Fit(innerRadius, innerRight - innerLeft, innerBottom - innerTop);
        }

        var bandRadius = 0.0;
        var bandBottom = 0;
        if (withHeader)
        {
            bandBottom = NestedRaster.Px(headerBottom);
            if (innerRight > innerLeft && bandBottom > innerTop)
            {
                kind |= (uint)RectFlags.HasHeader;
                bandRadius = Fit(innerRadius, innerRight - innerLeft, bandBottom - innerTop);
            }
        }

        var separateStripe = false;
        if (stripe && stripeLeft < stripeRight && stripeTop < stripeBottom)
        {
            if (stripeLeft >= x0 && stripeRight <= x1 && stripeTop >= y0 && stripeBottom <= y1)
            {
                kind |= (uint)RectFlags.HasStripe;
            }
            else
            {
                separateStripe = true;
            }
        }

        kind |= Insets(innerLeft - x0, innerTop - y0, x1 - innerRight, y1 - innerBottom);

        ref var instance = ref _into!.Add();
        instance.Outer = Clamped(x0, y0, x1, y1);
        instance.Feature = (kind & (uint)RectFlags.HasStripe) != 0 ? Clamped(stripeLeft, stripeTop, stripeRight, stripeBottom) : default;
        instance.HeaderBottom = ClampY(bandBottom);
        instance.Radius = (float)Fit(radius, x1 - x0, y1 - y0);
        instance.InnerRadii = HalfBits(bodyRadius) | HalfBits(bandRadius) << 16;
        instance.Kind = kind;
        instance.Body = body;
        instance.Rim = rim;
        instance.Header = header;
        instance.Accent = stripeColour;

        if (separateStripe)
        {
            Fill(stripeLeft, stripeTop, stripeRight, stripeBottom, stripeColour);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override void File(
        double left,
        double top,
        double right,
        double bottom,
        double radius,
        int stripeLeft,
        int stripeTop,
        int stripeRight,
        int stripeBottom,
        uint body,
        uint stripe)
    {
        var x0 = NestedRaster.Px(left);
        var y0 = NestedRaster.Px(top);
        var x1 = NestedRaster.Px(right);
        var y1 = NestedRaster.Px(bottom);
        if (x1 <= x0 || y1 <= y0 || x1 <= 0 || y1 <= 0 || x0 >= _width || y0 >= _height)
        {
            Fill(stripeLeft, stripeTop, stripeRight, stripeBottom, stripe);
            return;
        }

        var kind = (uint)RectKind.File;
        var separateStripe = false;
        if (stripeLeft < stripeRight && stripeTop < stripeBottom)
        {
            if (stripeLeft >= x0 && stripeRight <= x1 && stripeTop >= y0 && stripeBottom <= y1)
            {
                kind |= (uint)RectFlags.HasStripe;
            }
            else
            {
                separateStripe = true;
            }
        }

        ref var instance = ref _into!.Add();
        instance.Outer = Clamped(x0, y0, x1, y1);
        instance.Feature = (kind & (uint)RectFlags.HasStripe) != 0 ? Clamped(stripeLeft, stripeTop, stripeRight, stripeBottom) : default;
        instance.HeaderBottom = 0;
        instance.Radius = (float)Fit(radius, x1 - x0, y1 - y0);
        instance.InnerRadii = 0;
        instance.Kind = kind;
        instance.Body = body;
        instance.Rim = 0;
        instance.Header = 0;
        instance.Accent = stripe;

        if (separateStripe)
        {
            Fill(stripeLeft, stripeTop, stripeRight, stripeBottom, stripe);
        }
    }

    /// <summary>
    /// A radius as the raster uses it for a rectangle of this size: no more
    /// than half the shorter side, and none at all below one and a half
    /// pixels, where the raster fills square corners.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Fit(double radius, int width, int height)
    {
        var fitted = Math.Min(radius, Math.Min(width, height) / 2.0);
        return fitted < 1.5 ? 0 : fitted;
    }

    /// <summary>The body's inset on each side, two bits each from bit 12: left, top, right, bottom.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Insets(int left, int top, int right, int bottom) =>
        (uint)(Math.Clamp(left, 0, 3) | Math.Clamp(top, 0, 3) << 2 | Math.Clamp(right, 0, 3) << 4 | Math.Clamp(bottom, 0, 3) << 6) << 12;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HalfBits(double value) => BitConverter.HalfToUInt16Bits((Half)value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector4 Clamped(int x0, int y0, int x1, int y1) => new(ClampX(x0), ClampY(y0), ClampX(x1), ClampY(y1));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ClampX(int x) => Math.Clamp(x, -GuardBand, _highX);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ClampY(int y) => Math.Clamp(y, -GuardBand, _highY);
}
