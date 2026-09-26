using System.Numerics;
using System.Runtime.InteropServices;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// One rectangle of the nested canvas as the GPU draws it: a folder's cell
/// with its rim, body, title band and stripe; a file's tile with its coloured
/// edge; a speck or a wash; or, for the label layer, a translucent rounded
/// rectangle such as the pill behind a small folder's name.  The rectangle
/// pipeline draws a whole frame of them with one instanced draw: four
/// vertices per instance, their corners taken from the vertex id, and the
/// shape worked out per pixel in the pixel shader (Shaders/Nested.hlsl).
///
/// <para>Sixty-four bytes, a cache line, and laid out exactly as the input
/// layout in <see cref="NestedGpuRenderer"/> reads it.  Coordinates are
/// device pixels from the top-left of the frame.  Everything the CPU raster
/// snaps to a pixel is snapped already, by <see cref="GpuSink"/>, with the
/// raster's own <see cref="Controls.NestedRaster.Px"/>, so the GPU draws the
/// same straight edges to the pixel and only the anti-aliasing of the
/// rounded corners can differ.  Each edge is clamped on its own to a guard
/// band around the frame before it becomes a float: a clamped edge is still
/// off screen, and deep in, where a parent's edge can be trillions of pixels
/// away, a float could not hold it at all.</para>
///
/// <para>Colours are 0xAARRGGBB, the packing the raster uses; the shader
/// unpacks them with shifts.</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct RectInstance
{
    /// <summary>
    /// Left, top, right, bottom: the quad drawn, and the rectangle whose
    /// rounded outline is the cell's rim or the tile's body.
    /// </summary>
    public Vector4 Outer;

    /// <summary>
    /// A cell's title stripe, or a file's type stripe: left, top, right,
    /// bottom, painted opaque over the shape (see <see cref="RectFlags.HasStripe"/>).
    /// </summary>
    public Vector4 Feature;

    /// <summary>Where a cell's title band ends: an absolute edge, never a height, so a clamped top cannot move it.</summary>
    public float HeaderBottom;

    /// <summary>The outer corners' radius in pixels, already reduced to fit and zero where the raster draws square corners.</summary>
    public float Radius;

    /// <summary>
    /// A cell's body radius in the low half and its title band's in the high
    /// half, each a half-precision float, reduced and squared off the way the
    /// raster does it for those rectangles' own sizes.
    /// </summary>
    public uint InnerRadii;

    /// <summary>The <see cref="RectKind"/> in the low byte and <see cref="RectFlags"/> above it.</summary>
    public uint Kind;

    /// <summary>A cell's body, a tile's body, a plain fill's colour, or a label rectangle's straight colour with its alpha.</summary>
    public uint Body;

    /// <summary>A cell's rim.</summary>
    public uint Rim;

    /// <summary>A cell's title band.</summary>
    public uint Header;

    /// <summary>The stripe's colour: the folder's own for a cell, the type's for a file.</summary>
    public uint Accent;

    /// <summary>The size the input layout and the upload assume.</summary>
    public const int Size = 64;
}

/// <summary>What a <see cref="RectInstance"/> is, in its <see cref="RectInstance.Kind"/>'s low byte.</summary>
internal enum RectKind : uint
{
    /// <summary>An opaque rectangle with pixel edges: a speck, a file wash, a stripe that stands alone.</summary>
    Plain = 0,

    /// <summary>A folder's cell: rounded rim, body one pixel inside, title band, title stripe.</summary>
    Cell = 1,

    /// <summary>A file's tile: a rounded body and its type stripe.</summary>
    File = 2,

    /// <summary>
    /// A rounded rectangle with edges anywhere, anti-aliased all round, in a
    /// colour that may be translucent: what the label layer draws in DIPs -
    /// the pill behind a name, a mark down a file's edge.
    /// </summary>
    Rounded = 3,

    /// <summary>Kept for outlines, should selection and filter outlines ever move onto the GPU.</summary>
    Stroke = 4
}

/// <summary>Flags above the kind in <see cref="RectInstance.Kind"/>.</summary>
[Flags]
internal enum RectFlags : uint
{
    None = 0,

    /// <summary>The cell's body rectangle is not empty.</summary>
    HasBody = 1u << 8,

    /// <summary>The cell has a title band, ending at <see cref="RectInstance.HeaderBottom"/>.</summary>
    HasHeader = 1u << 9,

    /// <summary><see cref="RectInstance.Feature"/> holds a stripe to paint.</summary>
    HasStripe = 1u << 10

    // Bits 12 to 19 hold the cell body's insets from the outer rectangle, two
    // bits a side (left, top, right, bottom).  The raster finds the body as
    // Px(left + 1) and so on, and Math.Round rounds halves to even, so a
    // side sits 0, 1 or 2 pixels in - not always the 1 it looks like.
}

/// <summary>
/// One file icon: a square of device pixels and the slice of the icon atlas
/// (<see cref="IconAtlas"/>) to draw into it.  Twenty-four bytes, laid out
/// as the icon pipeline's input layout in <see cref="NestedGpuRenderer"/>
/// reads it; <see cref="GpuLabelTarget"/> writes one per file name with an
/// icon.  Glyphs have theirs in <see cref="GlyphQuad"/>, the design's
/// GlyphInstance.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct IconInstance
{
    /// <summary>Left, top, right, bottom in device pixels, the origin snapped to a whole pixel.</summary>
    public Vector4 Rect;

    /// <summary>The Texture2DArray slice.</summary>
    public uint Slot;

    /// <summary>A premultiplied multiplier, 0xFFFFFFFF to draw the icon as it is.</summary>
    public uint Tint;

    public const int Size = 24;
}
