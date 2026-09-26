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
/// </summary>
internal sealed class RasterSink(NestedRaster raster) : SceneSink
{
    public override void Clear(uint colour) => raster.Clear(colour);

    public override void Fill(int x0, int y0, int x1, int y1, uint colour) => raster.Fill(x0, y0, x1, y1, colour);

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
        raster.FillFramed(left, top, right, bottom, radius, rim, body);
        if (double.IsNaN(headerBottom))
        {
            return;
        }

        raster.FillRounded(left + 1, top + 1, right - 1, headerBottom, Math.Max(0, radius - 1), header, roundBottom: false);
        if (hasStripe)
        {
            raster.Fill(stripeLeft, stripeTop, stripeRight, stripeBottom, stripeColour);
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
        raster.FillRounded(left, top, right, bottom, radius, body);
        raster.Fill(stripeLeft, stripeTop, stripeRight, stripeBottom, stripe);
    }
}
