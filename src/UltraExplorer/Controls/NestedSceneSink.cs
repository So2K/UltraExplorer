namespace UltraExplorer.Controls;

/// <summary>
/// Where one frame of the nested canvas's cells and file tiles goes.
///
/// The walk - which folders are on screen and big enough to see, where each
/// one is, what colour it is, how its corners, title band and stripe are
/// sized and rounded to pixels - is the canvas's, and is the same whatever
/// draws the result.  What the walk ends in is one call here per shape, with
/// every edge already worked out in device pixels, so a sink only has to put
/// those shapes down: into a pixel buffer, as <see cref="RasterSink"/> does
/// today, or as instances for a GPU to draw.
///
/// <para>Coordinates are device pixels from the top-left of the frame.  They
/// are doubles where the canvas leaves the snapping to pixels to the sink -
/// the outline of a cell or a tile, which must be snapped with
/// <see cref="NestedRaster.Px"/> exactly as the raster snaps it, or the
/// picture moves by a pixel against the names on it - and ints where the
/// canvas has snapped them already: specks, washes and stripes.  Either can
/// lie far outside the frame - deep in, a parent's edge is trillions of
/// pixels away, and the ints are clamped to a hundred million - so a sink
/// clips.
/// Colours are opaque 0xAARRGGBB, the packing <see cref="NestedRaster"/>
/// uses.</para>
///
/// <para>Calls arrive in painting order, and a later shape covers an earlier
/// one where they overlap: a folder before its files and sub-folders, a
/// parent before its children.  A sink must keep that order.</para>
/// </summary>
internal abstract class SceneSink
{
    /// <summary>
    /// Fills the whole frame with one colour: the bare canvas, when no folder
    /// covers the view.  Comes first, before any other call of the frame.
    /// </summary>
    public abstract void Clear(uint colour);

    /// <summary>
    /// Fills [x0, x1) x [y0, y1) with one colour: a folder or a file too small
    /// to be anything but a speck, or the faint wash that stands for a strip
    /// of files too small to tell apart.
    /// </summary>
    public abstract void Fill(int x0, int y0, int x1, int y1, uint colour);

    /// <summary>
    /// A folder's cell: a rounded rectangle of <paramref name="rim"/> with a
    /// body of <paramref name="body"/> one pixel inside it, and over the top
    /// of the body a title band and a stripe down the title.
    /// </summary>
    /// <param name="radius">The outer corners' radius; the body's is one pixel less.</param>
    /// <param name="headerBottom">
    /// Where the title band ends, or NaN for a cell too small to have one.
    /// The band starts one pixel inside the rim, shares the body's rounded top
    /// corners and ends square.
    /// </param>
    /// <param name="hasStripe">
    /// Whether the title carries the stripe in the folder's own colour, over
    /// [<paramref name="stripeLeft"/>, <paramref name="stripeRight"/>) x
    /// [<paramref name="stripeTop"/>, <paramref name="stripeBottom"/>).  Only
    /// a cell with a title band has one.
    /// </param>
    public abstract void Cell(
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
        uint stripeColour);

    /// <summary>
    /// A file's tile: a rounded rectangle of <paramref name="body"/>, and the
    /// coloured edge down its left that says what kind of file it is, over
    /// [<paramref name="stripeLeft"/>, <paramref name="stripeRight"/>) x
    /// [<paramref name="stripeTop"/>, <paramref name="stripeBottom"/>).
    /// </summary>
    public abstract void File(
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
        uint stripe);
}

/// <summary>
/// The scene painted by hand into a pixel buffer - the canvas's picture as it
/// always was, and the one every other way of drawing it is measured against.
/// Each call is exactly the <see cref="NestedRaster"/> calls the canvas made
/// before sinks existed, with the same arguments in the same order, so the
/// pixels are the same to the bit.
///
/// <para>It can paint part of a frame, too: <see cref="Begin"/> with a clip
/// paints only the pixels inside it and leaves the rest of the buffer alone -
/// for a frame after some folders were read, when only their cells changed
/// and everything else in the bitmap is already the picture.</para>
/// </summary>
internal sealed class RasterSink(NestedRaster raster) : SceneSink
{
    // Where the clip starts in the frame: the raster is attached to the clip
    // alone, so every coordinate is moved by this much on the way in.
    private int _clipLeft;
    private int _clipTop;
    private bool _clipped;

    /// <summary>
    /// Attaches the raster to <paramref name="clip"/> of the frame in
    /// <paramref name="buffer"/>: every call up to <see cref="End"/> paints
    /// the pixels inside it, exactly as a frame painted whole paints them
    /// there, and nothing outside it.
    ///
    /// <para>The raster already clips everything to its own bounds; attached
    /// at the clip's corner, those bounds are the clip, and the shapes are
    /// moved by the corner to meet it.  Moving a shape leaves its pixels as
    /// they were because the corner is at an even number of pixels (the
    /// canvas aligns clips to eight): edges are snapped with
    /// <see cref="NestedRaster.Px"/>, whose <see cref="Math.Round(double)"/>
    /// takes halves to the even neighbour, and an even shift keeps which
    /// neighbour that is.  Corners and shading come from differences of
    /// snapped edges, which no shift changes.  The one thing a shift can
    /// change is an edge within a rounding error of a half pixel - about
    /// 10^-15 of one - which the subtraction can carry across it: a pixel's
    /// difference at odds far below anything a frame will ever meet, and gone
    /// at the next whole frame.  A clip that is the whole frame moves nothing,
    /// and is the frame as it always was.</para>
    /// </summary>
    public void Begin(IntPtr buffer, int strideBytes, System.Windows.Int32Rect clip)
    {
        raster.Attach(buffer + ((nint)clip.Y * strideBytes + (nint)clip.X * 4), clip.Width, clip.Height, strideBytes);
        _clipLeft = clip.X;
        _clipTop = clip.Y;
        _clipped = clip.X != 0 || clip.Y != 0;
    }

    /// <summary>Lets go of the buffer <see cref="Begin"/> attached.</summary>
    public void End()
    {
        raster.Detach();
        _clipLeft = 0;
        _clipTop = 0;
        _clipped = false;
    }

    public override void Clear(uint colour) => raster.Clear(colour);

    public override void Fill(int x0, int y0, int x1, int y1, uint colour)
    {
        if (_clipped)
        {
            x0 -= _clipLeft;
            x1 -= _clipLeft;
            y0 -= _clipTop;
            y1 -= _clipTop;
        }

        raster.Fill(x0, y0, x1, y1, colour);
    }

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
        // Unclipped, both are nought and nothing moves.  The title band's
        // edges are worked out where the whole frame works them out, and only
        // then moved to the clip.
        double dx = _clipLeft, dy = _clipTop;
        raster.FillFramed(left - dx, top - dy, right - dx, bottom - dy, radius, rim, body);
        if (double.IsNaN(headerBottom))
        {
            return;
        }

        if (_clipped)
        {
            raster.FillRounded(left + 1 - dx, top + 1 - dy, right - 1 - dx, headerBottom - dy, Math.Max(0, radius - 1), header, roundBottom: false);
        }
        else
        {
            raster.FillRounded(left + 1, top + 1, right - 1, headerBottom, Math.Max(0, radius - 1), header, roundBottom: false);
        }

        if (hasStripe)
        {
            Fill(stripeLeft, stripeTop, stripeRight, stripeBottom, stripeColour);
        }
    }

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
        double dx = _clipLeft, dy = _clipTop;
        raster.FillRounded(left - dx, top - dy, right - dx, bottom - dy, radius, body);
        Fill(stripeLeft, stripeTop, stripeRight, stripeBottom, stripe);
    }
}
