using System.Buffers;
using SharpGen.Runtime;
using Vortice.DCommon;
using Vortice.DirectWrite;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// The fallback shaping path: a name the direct path cannot handle - right
/// to left, missing from the font, surrogate pairs - is laid out by a
/// DirectWrite text layout at <see cref="LayoutSize"/> DIPs, and the glyph
/// runs it would draw are caught here instead of drawn.  DirectWrite does
/// the bidi ordering, the system font fallback and the clusters; this keeps
/// what it produced, turned into the same em-unit <see cref="ShapedText"/>
/// the direct path makes.
///
/// A layout costs about 12 microseconds a name, over twice the direct path,
/// and it can load fallback fonts from disk, so it runs on the shaper's
/// background worker, never inside a frame.
///
/// One instance per worker thread; not shared.
/// </summary>
internal sealed class TextLayoutCapture : TextRendererBase
{
    /// <summary>The size the layout is made at; everything captured is divided by it to get em.</summary>
    public const float LayoutSize = 64;

    private readonly FaceRegistry _faces;
    private readonly List<CapturedRun> _runs = [];
    private bool _failed;

    public TextLayoutCapture(FaceRegistry faces)
    {
        _faces = faces;
    }

    /// <summary>
    /// Shapes <paramref name="text"/> in <paramref name="face"/> with
    /// DirectWrite's layout and fallback, or returns null when DirectWrite
    /// fails or produces a face beyond the registry's 256.
    /// </summary>
    public ShapedText? Shape(string text, byte face, string locale)
    {
        var info = _faces[face];
        _runs.Clear();
        _failed = false;
        using (var format = _faces.Factory.CreateTextFormat(info.FamilyName, _faces.Collection, info.Weight, info.Style, info.Stretch, LayoutSize, locale))
        {
            format.WordWrapping = WordWrapping.NoWrap;
            using var layout = _faces.Factory.CreateTextLayout(OneLine(text), format, 1e7f, 1e7f);
            layout.Draw(IntPtr.Zero, this, 0, 0);
            if (_failed)
            {
                return null;
            }

            return Build(text, face, info, layout.Metrics.Width / LayoutSize);
        }
    }

    /// <summary>
    /// The characters DirectWrite always breaks a line at, even with
    /// wrapping off.  A file name may hold U+0085 or U+2028/2029; laid out as
    /// they are, what follows starts a second line at the left, and only the
    /// runs' left edges are kept - its glyphs would be drawn over the start
    /// of the name, and the width would be the first line's alone.
    /// </summary>
    private static readonly SearchValues<char> LineBreaks = SearchValues.Create(['\n', '\v', '\f', '\r', (char)0x0085, (char)0x2028, (char)0x2029]);

    /// <summary>
    /// The text with every line break a space: one line, as a name is shown,
    /// and of the same length, so the clusters still point at the name's own
    /// characters.
    /// </summary>
    private static string OneLine(string text)
    {
        if (text.AsSpan().IndexOfAny(LineBreaks) < 0)
        {
            return text;
        }

        return string.Create(text.Length, text, static (line, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                line[index] = LineBreaks.Contains(source[index]) ? ' ' : source[index];
            }
        });
    }

    public override void DrawGlyphRun(nint clientDrawingContext, float baselineOriginX, float baselineOriginY, MeasuringMode measuringMode, GlyphRun glyphRun, GlyphRunDescription glyphRunDescription, IUnknown clientDrawingEffect)
    {
        // Vortice hands the run over with a reference of its own on the font
        // face, which nothing else releases; the registry keeps its own
        // (TryRegister queries the face again), so it goes when the call ends.
        try
        {
            var indices = glyphRun.Indices;
            if (indices is null || indices.Length == 0 || glyphRun.FontFace is null)
            {
                return;
            }

            if (!_faces.TryRegister(glyphRun.FontFace, out var faceId))
            {
                _failed = true;
                return;
            }

            var count = indices.Length;
            var advances = glyphRun.Advances is { Length: > 0 } given ? (float[])given.Clone() : new float[count];
            var offsets = glyphRun.Offsets is { Length: > 0 } offsetsGiven ? (GlyphOffset[])offsetsGiven.Clone() : new GlyphOffset[count];

            // The cluster map (character to first glyph) lives only for the call.
            var textLength = glyphRunDescription.Text?.Length ?? 0;
            var clusterMap = new ushort[textLength];
            if (textLength > 0 && glyphRunDescription.ClusterMap != IntPtr.Zero)
            {
                unsafe
                {
                    new ReadOnlySpan<ushort>((void*)glyphRunDescription.ClusterMap, textLength).CopyTo(clusterMap);
                }
            }

            _runs.Add(new CapturedRun(
                baselineOriginX,
                (glyphRun.BidiLevel & 1) != 0,
                faceId,
                glyphRun.FontEmSize,
                (ushort[])indices.Clone(),
                advances,
                offsets,
                (int)glyphRunDescription.TextPosition,
                glyphRunDescription.Text ?? string.Empty,
                clusterMap));
        }
        finally
        {
            glyphRun.Dispose();
        }
    }

    /// <summary>
    /// Lays the captured runs out left to right.  A right-to-left run is
    /// drawn by DirectWrite from its origin leftwards, each glyph at the pen
    /// less its advance, so its glyphs are reversed here to keep one visual
    /// order with non-decreasing pen positions - which is what trimming by
    /// prefix sums needs.
    /// </summary>
    private ShapedText Build(string text, byte face, FaceInfo info, float width)
    {
        var total = 0;
        foreach (var run in _runs)
        {
            total += run.Glyphs.Length;
        }

        // Runs sorted by their left edge: DirectWrite draws them in visual
        // order already, but the sort makes that an invariant here.
        _runs.Sort(static (a, b) => a.Left.CompareTo(b.Left));

        var glyphs = new ushort[total];
        var faces = new byte[total];
        var prefix = new float[total + 1];
        var offsetX = new float[total];
        var offsetY = new float[total];
        var scales = new float[total];
        var glyphCharacter = new ushort[total];
        var clusterStart = new bool[total];
        var whitespace = new bool[total];
        bool anyOffsetX = false, anyOffsetY = false, anyScale = false;

        var at = 0;
        var right = 0f;
        var rightToLeft = false;
        foreach (var run in _runs)
        {
            rightToLeft |= run.RightToLeft;
            var count = run.Glyphs.Length;
            var scale = run.EmSize / LayoutSize;
            var logicalStart = new bool[count + 1];
            var logicalCharacter = new ushort[count];
            var logicalWhitespace = new bool[count];
            MapClusters(run, text.Length, logicalStart, logicalCharacter, logicalWhitespace);

            // Pen position before each logical glyph, measured from the run's left edge.
            var runWidth = 0f;
            for (var index = 0; index < count; index++)
            {
                runWidth += run.Advances[index];
            }

            var left = run.RightToLeft ? run.Origin - runWidth : run.Origin;
            var pen = 0f;
            for (var visual = 0; visual < count; visual++)
            {
                var logical = run.RightToLeft ? count - 1 - visual : visual;
                var advance = run.Advances[logical];
                var offset = run.Offsets[logical];
                var slot = at + visual;
                glyphs[slot] = run.Glyphs[logical];
                faces[slot] = run.Face;
                prefix[slot] = (left + pen) / LayoutSize;
                offsetX[slot] = (run.RightToLeft ? -offset.AdvanceOffset : offset.AdvanceOffset) / LayoutSize;
                offsetY[slot] = offset.AscenderOffset / LayoutSize;
                scales[slot] = scale;
                glyphCharacter[slot] = logicalCharacter[logical];
                whitespace[slot] = logicalWhitespace[logical];

                // A cut before this visual glyph separates it from the one
                // on its left.  Left to right, that is allowed when this
                // glyph starts a cluster; right to left, when the glyph on
                // the left (the next one logically) does.
                clusterStart[slot] = visual == 0 || (run.RightToLeft ? logicalStart[logical + 1] : logicalStart[logical]);

                anyOffsetX |= offsetX[slot] != 0;
                anyOffsetY |= offsetY[slot] != 0;
                anyScale |= scale != 1;
                pen += advance;
            }

            at += count;
            right = Math.Max(right, (left + runWidth) / LayoutSize);
        }

        prefix[total] = right;

        // Guard the invariant trimming relies on, whatever DirectWrite sent.
        for (var index = 1; index <= total; index++)
        {
            if (prefix[index] < prefix[index - 1])
            {
                prefix[index] = prefix[index - 1];
            }
        }

        return new ShapedText(
            face,
            complex: true,
            glyphs,
            faces,
            prefix,
            anyOffsetX ? offsetX : null,
            anyOffsetY ? offsetY : null,
            anyScale ? scales : null,
            glyphCharacter,
            clusterStart,
            whitespace,
            width,
            info.EllipsisAdvance,
            rightToLeft);
    }

    /// <summary>
    /// From the run's cluster map: which logical glyphs start a cluster,
    /// the first character of each glyph's cluster, and whether a cluster is
    /// all whitespace.  A glyph no character maps to (a mark split off by
    /// the font) belongs to the cluster before it.
    /// </summary>
    private static void MapClusters(CapturedRun run, int textLength, bool[] logicalStart, ushort[] logicalCharacter, bool[] logicalWhitespace)
    {
        var count = run.Glyphs.Length;
        var map = run.ClusterMap;
        if (map.Length == 0)
        {
            for (var index = 0; index <= count; index++)
            {
                logicalStart[index] = true;
            }

            for (var index = 0; index < count; index++)
            {
                logicalCharacter[index] = (ushort)Math.Min(run.TextPosition + index, Math.Max(0, textLength - 1));
            }

            return;
        }

        logicalStart[count] = true;
        var firstCharacter = new int[count];
        Array.Fill(firstCharacter, -1);
        var allWhitespace = new bool[count];
        Array.Fill(allWhitespace, true);
        for (var character = 0; character < map.Length; character++)
        {
            var glyph = map[character];
            if (glyph >= count)
            {
                continue;
            }

            if (firstCharacter[glyph] < 0)
            {
                firstCharacter[glyph] = character;
                logicalStart[glyph] = true;
            }

            if (!char.IsWhiteSpace(run.Text[character]))
            {
                allWhitespace[glyph] = false;
            }
        }

        var currentCharacter = 0;
        var currentWhitespace = false;
        for (var glyph = 0; glyph < count; glyph++)
        {
            if (firstCharacter[glyph] >= 0)
            {
                currentCharacter = firstCharacter[glyph];
                currentWhitespace = allWhitespace[glyph];
            }

            logicalCharacter[glyph] = (ushort)Math.Min(run.TextPosition + currentCharacter, ushort.MaxValue);
            logicalWhitespace[glyph] = currentWhitespace;
        }

        logicalStart[0] = true;
    }

    private sealed record CapturedRun(
        float Origin,
        bool RightToLeft,
        byte Face,
        float EmSize,
        ushort[] Glyphs,
        float[] Advances,
        GlyphOffset[] Offsets,
        int TextPosition,
        string Text,
        ushort[] ClusterMap)
    {
        public float Left
        {
            get
            {
                if (!RightToLeft)
                {
                    return Origin;
                }

                var width = 0f;
                foreach (var advance in Advances)
                {
                    width += advance;
                }

                return Origin - width;
            }
        }
    }
}
