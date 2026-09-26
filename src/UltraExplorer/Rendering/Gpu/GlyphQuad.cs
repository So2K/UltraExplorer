using System.Numerics;
using System.Runtime.InteropServices;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// One glyph as the GPU draws it: a quad in device pixels and where its
/// distance field sits in the atlas.  The layout is the design's
/// GlyphInstance (40 bytes, Pack = 4) field for field, so a sink can copy it
/// into the instance buffer as it is, or reinterpret one as the other.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct GlyphQuad
{
    /// <summary>Left, top, right, bottom in device pixels relative to the surface; sub-pixel while the camera moves.</summary>
    public Vector4 Rect;

    /// <summary>Top-left atlas texel, u | v &lt;&lt; 16.  The shader divides by the page size.</summary>
    public uint UV0;

    /// <summary>Bottom-right atlas texel (exclusive), u | v &lt;&lt; 16.</summary>
    public uint UV1;

    /// <summary>Straight 0xAARRGGBB text colour.</summary>
    public uint Colour;

    /// <summary>Atlas page | tier &lt;&lt; 8.</summary>
    public uint PageTier;

    /// <summary>2 * spread * fontPx / tierEm: how many screen pixels the field's 0-to-1 range spans.</summary>
    public float PxRange;

    /// <summary>Threshold bias, a touch of stem darkening for small text.</summary>
    public float Bias;
}

/// <summary>
/// Where <see cref="GlyphAtlas.Emit{TSink}"/> writes its quads.  A struct
/// implementing it is passed by reference and the call is specialised for
/// it, so emitting a label calls no interface and allocates nothing: the
/// label target's sink appends to its instance list, a test's sink to an
/// array.
/// </summary>
internal interface IGlyphSink
{
    void Add(in GlyphQuad quad);
}

/// <summary>
/// One of the atlas's sizes.  Glyphs are rasterised at <see cref="Em"/>
/// pixels per em and their field reaches <see cref="Spread"/> texels past
/// the outline.  Drawn text picks the tier by its size in device pixels, so
/// a glyph is never shrunk more than about 2.2 times nor its field range
/// squeezed under 2.5 screen pixels, where the edge would turn soft.
/// </summary>
internal readonly record struct GlyphTier(int Em, int Spread);

/// <summary>
/// A glyph placed in the atlas: its texels and, in em, the box its field
/// covers relative to the pen on the baseline (y down, so the top is
/// negative for anything above the baseline).  A glyph with no ink - a
/// space - has an empty box and is not drawn.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct GlyphEntry
{
    public GlyphEntry(ushort page, ushort x, ushort y, ushort width, ushort height, float left, float top, float right, float bottom)
    {
        Page = page;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
        UV0 = x | (uint)y << 16;
        UV1 = (uint)(x + width) | (uint)(y + height) << 16;
    }

    public ushort Page { get; }

    public ushort X { get; }

    public ushort Y { get; }

    public ushort Width { get; }

    public ushort Height { get; }

    public float Left { get; }

    public float Top { get; }

    public float Right { get; }

    public float Bottom { get; }

    public uint UV0 { get; }

    public uint UV1 { get; }

    public bool IsEmpty => Width == 0;
}

/// <summary>A rectangle of an atlas page that changed, for the GPU copies to upload.</summary>
internal readonly record struct GlyphPlacement(int Page, int X, int Y, int Width, int Height);
