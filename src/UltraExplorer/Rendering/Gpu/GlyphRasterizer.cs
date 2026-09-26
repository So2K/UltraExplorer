using System.Runtime.CompilerServices;
using Vortice;
using Vortice.DCommon;
using Vortice.DirectWrite;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// Makes one glyph's distance field: DirectWrite rasterises the glyph at the
/// tier's size, then <see cref="SdfGenerator"/> turns the coverage into a
/// field.
///
/// The rasteriser is asked for exactly what an SDF wants:
/// - IDWriteFactory2.CreateGlyphRunAnalysis at the tier's em in pixels with
///   no transform, so one texel is one pixel of the reference size;
/// - rendering mode NaturalSymmetric and grid fitting disabled: the true
///   outline, with no hinting to snap stems to this one size, since the
///   field is drawn at every size;
/// - grayscale antialiasing, for which CreateAlphaTexture's Aliased1x1
///   texture holds 8-bit coverage.
/// DirectWrite resolves the outline's winding itself, so the overlapping
/// contours of the variable Segoe UI come out whole - what outline-based
/// MSDF generators get wrong.
///
/// One instance per thread (<see cref="ForThread"/>): the buffers and the
/// glyph run are reused from glyph to glyph.
/// </summary>
internal sealed class GlyphRasterizer
{
    /// <summary>
    /// Glyphs taller or wider than this many texels at the tier size - four
    /// em at the largest tier - are not put in the atlas.  Only a handful of
    /// ornate ligatures are that big, and the distance transform's scratch
    /// grows with the square of it on every worker thread.
    /// </summary>
    public const int MaximumSide = 256;

    [ThreadStatic]
    private static GlyphRasterizer? t_instance;

    private readonly ushort[] _indices = new ushort[1];
    private readonly float[] _advances = new float[1];
    private readonly GlyphOffset[] _offsets = new GlyphOffset[1];
    private byte[] _coverage = new byte[64 * 96];
    private byte[] _field = new byte[96 * 128];

    public static GlyphRasterizer ForThread => t_instance ??= new GlyphRasterizer();

    /// <summary>The field of the last glyph rasterised, rows packed, <see cref="FieldWidth"/> texels wide.</summary>
    public ReadOnlySpan<byte> Field => _field.AsSpan(0, FieldWidth * FieldHeight);

    public int FieldWidth { get; private set; }

    public int FieldHeight { get; private set; }

    /// <summary>The field's box in em relative to the pen on the baseline, y down.</summary>
    public float Left { get; private set; }

    public float Top { get; private set; }

    public float Right { get; private set; }

    public float Bottom { get; private set; }

    /// <summary>
    /// Rasterises <paramref name="glyph"/> of <paramref name="face"/> at
    /// <paramref name="tier"/> into <see cref="Field"/>.  Returns false when
    /// the glyph has no ink (the field is then empty) or is too big to keep;
    /// DirectWrite failures surface as exceptions for the caller to log.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool Rasterise(IDWriteFactory2 factory, FaceInfo face, ushort glyph, GlyphTier tier)
    {
        FieldWidth = FieldHeight = 0;
        Left = Top = Right = Bottom = 0;
        _indices[0] = glyph;
        _advances[0] = 0;
        _offsets[0] = default;

        // The run borrows the face without owning a reference of its own;
        // it is cleared before it is dropped so it never releases the
        // registry's face.
        var run = new GlyphRun
        {
            FontFace = face.Face,
            FontEmSize = tier.Em,
            Indices = _indices,
            Advances = _advances,
            Offsets = _offsets,
            IsSideways = false,
            BidiLevel = 0
        };

        RawRect bounds;
        using (var analysis = factory.CreateGlyphRunAnalysis(
                   run,
                   null,
                   RenderingMode.NaturalSymmetric,
                   MeasuringMode.Natural,
                   GridFitMode.Disabled,
                   Vortice.DirectWrite.TextAntialiasMode.Grayscale,
                   0,
                   0))
        {
            run.FontFace = null;
            bounds = analysis.GetAlphaTextureBounds(TextureType.Aliased1x1);
            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            if (width > MaximumSide || height > MaximumSide)
            {
                return false;
            }

            var coverageSize = width * height;
            if (_coverage.Length < coverageSize)
            {
                _coverage = new byte[Math.Max(coverageSize, _coverage.Length * 2)];
            }

            analysis.CreateAlphaTexture(TextureType.Aliased1x1, bounds, _coverage, (uint)coverageSize);

            var spread = tier.Spread;
            FieldWidth = width + 2 * spread;
            FieldHeight = height + 2 * spread;
            var fieldSize = FieldWidth * FieldHeight;
            if (_field.Length < fieldSize)
            {
                _field = new byte[Math.Max(fieldSize, _field.Length * 2)];
            }

            SdfGenerator.ForThread.Generate(_coverage.AsSpan(0, coverageSize), width, height, spread, _field.AsSpan(0, fieldSize));

            float em = tier.Em;
            Left = (bounds.Left - spread) / em;
            Top = (bounds.Top - spread) / em;
            Right = (bounds.Right + spread) / em;
            Bottom = (bounds.Bottom + spread) / em;
            return true;
        }
    }
}
