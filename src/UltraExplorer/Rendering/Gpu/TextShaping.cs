using System.Runtime.CompilerServices;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// A name shaped once, in em units, ready to be drawn at any size.
///
/// Everything is per glyph and in visual order, left to right: the glyph
/// index and the face it comes from, the pen position before it
/// (<see cref="PrefixAdvance"/>, one entry per glyph plus one for the end),
/// and for text that needs them the offsets of marks and the scale of a
/// fallback font.  Multiplying by the font size in pixels gives device
/// pixels, which is why a zoom never shapes anything again: only the
/// multiplier changes.
///
/// Trimming works on the same arrays (<see cref="Trim"/>): the prefix sums
/// are already the width of every possible cut, so finding where
/// CharacterEllipsis would cut is a binary search.  A text with a
/// right-to-left run is cut in logical order instead - the order WPF cuts
/// in - from sums over its clusters in that order, made once when the text
/// is shaped (<see cref="LogicalClusters"/>).
///
/// Immutable after it is made; safe to read from any thread.
/// </summary>
internal sealed class ShapedText
{
    private const byte ClusterStartFlag = 1;
    private const byte WhitespaceFlag = 2;

    private readonly byte[] _flags;
    private readonly ushort[] _visibleEnd;

    public ShapedText(
        byte face,
        bool complex,
        ushort[] glyphs,
        byte[] faces,
        float[] prefixAdvance,
        float[]? offsetX,
        float[]? offsetY,
        float[]? scales,
        ushort[] glyphCharacter,
        ReadOnlySpan<bool> clusterStart,
        ReadOnlySpan<bool> whitespace,
        float width,
        float ellipsisAdvance,
        bool rightToLeft = false)
    {
        Face = face;
        Complex = complex;
        Glyphs = glyphs;
        Faces = faces;
        PrefixAdvance = prefixAdvance;
        OffsetX = offsetX;
        OffsetY = offsetY;
        Scales = scales;
        GlyphCharacter = glyphCharacter;
        Width = width;
        EllipsisAdvance = ellipsisAdvance;

        var count = glyphs.Length;
        _flags = new byte[count];
        for (var index = 0; index < count; index++)
        {
            _flags[index] = (byte)((clusterStart[index] ? ClusterStartFlag : 0) | (whitespace[index] ? WhitespaceFlag : 0));
        }

        // Where the visible text ends for a cut before glyph k: whitespace
        // just before a cut hangs past the end of the line, the way WPF lets
        // trailing spaces hang - it neither counts against the room nor
        // pushes the ellipsis to the right.
        _visibleEnd = new ushort[count + 1];
        for (var cut = 0; cut <= count; cut++)
        {
            var end = cut;
            while (end > 0 && whitespace[end - 1])
            {
                end--;
            }

            _visibleEnd[cut] = (ushort)end;
        }

        if (rightToLeft && count > 0)
        {
            _logical = new LogicalClusters(glyphCharacter, prefixAdvance, whitespace);
        }
    }

    private readonly LogicalClusters? _logical;

    /// <summary>
    /// True when the text holds a right-to-left run, so its visual order is
    /// not its logical one and a trimmed cut (<see cref="Trim"/>) counts
    /// characters, not glyphs: <see cref="TextCut.Kept"/> and
    /// <see cref="TextCut.Visible"/> are then the first character left out,
    /// and every glyph of a cluster that starts before
    /// <see cref="TextCut.Visible"/> is drawn, packed left to right
    /// (<see cref="GlyphAtlas.Emit{TSink}"/>).
    /// </summary>
    public bool TrimsLogically => _logical is not null;

    /// <summary>The face the text was asked for: its metrics place the line and its ellipsis ends a trimmed one.</summary>
    public byte Face { get; }

    /// <summary>True when the text went through DirectWrite's layout (bidi, fallback fonts, clusters) rather than the direct path.</summary>
    public bool Complex { get; }

    public ushort[] Glyphs { get; }

    /// <summary>The face of each glyph: <see cref="Face"/> for most, a fallback face for characters it lacks.</summary>
    public byte[] Faces { get; }

    /// <summary>Pen position before each glyph, in em, plus the end of the last; non-decreasing.</summary>
    public float[] PrefixAdvance { get; }

    /// <summary>Horizontal offset of each glyph from its pen position, in em; null when all are zero.</summary>
    public float[]? OffsetX { get; }

    /// <summary>Vertical offset of each glyph, in em, positive up (DirectWrite's ascender offset); null when all are zero.</summary>
    public float[]? OffsetY { get; }

    /// <summary>Size of each glyph relative to the requested size, for fallback fonts DirectWrite scales; null when all are one.</summary>
    public float[]? Scales { get; }

    /// <summary>The index of the first character of each glyph's cluster in the original string.</summary>
    public ushort[] GlyphCharacter { get; }

    /// <summary>Width in em, not counting trailing whitespace - what FormattedText.Width is per unit of font size.</summary>
    public float Width { get; }

    /// <summary>The advance of the ellipsis that ends a trimmed line, in em.</summary>
    public float EllipsisAdvance { get; }

    public int Count => Glyphs.Length;

    /// <summary>True when the glyph starts a cluster, so a cut may fall just before it.</summary>
    public bool IsClusterStart(int glyph) => (_flags[glyph] & ClusterStartFlag) != 0;

    /// <summary>True when every character of the glyph's cluster is whitespace.</summary>
    public bool IsWhitespace(int glyph) => (_flags[glyph] & WhitespaceFlag) != 0;

    /// <summary>
    /// Where WPF's CharacterEllipsis would cut this text given
    /// <paramref name="availableEm"/> of room (the maximum width divided by
    /// the font size), without allocating.
    ///
    /// The rules are the ones WPF's TextFormatter applies, checked against it
    /// on real file names:
    /// - text no wider than the room is not cut;
    /// - room no wider than the ellipsis leaves the ellipsis alone;
    /// - otherwise the cut is the last cluster boundary whose text, with
    ///   whitespace before the cut hanging, fits in the room less the
    ///   ellipsis - measured with the kerning of the whole name, as the line
    ///   was shaped;
    /// - and at least one cluster always stays, even when it overflows.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public TextCut Trim(float availableEm)
    {
        var count = Glyphs.Length;
        if (Width <= availableEm)
        {
            return new TextCut(count, count, false, Width);
        }

        var room = availableEm - EllipsisAdvance;
        if (room <= 0)
        {
            return new TextCut(0, 0, true, EllipsisAdvance);
        }

        if (_logical is { } logical)
        {
            return logical.Trim(room, EllipsisAdvance);
        }

        // Largest cut whose visible text fits.  The visible width is
        // non-decreasing in the cut, so a binary search finds it.
        var prefix = PrefixAdvance;
        var visibleEnd = _visibleEnd;
        int low = 0, high = count;
        while (low < high)
        {
            var middle = (low + high + 1) >> 1;
            if (prefix[visibleEnd[middle]] <= room)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        var cut = low;
        while (cut > 0 && cut < count && (_flags[cut] & ClusterStartFlag) == 0)
        {
            cut--;
        }

        if (cut == 0 && count > 0)
        {
            cut = 1;
            while (cut < count && (_flags[cut] & ClusterStartFlag) == 0)
            {
                cut++;
            }
        }

        var visible = visibleEnd[cut];
        return new TextCut(cut, visible, true, prefix[visible] + EllipsisAdvance);
    }

    /// <summary>The cut for text that is drawn whole, whatever its width.</summary>
    public TextCut Whole => new(Glyphs.Length, Glyphs.Length, false, Width);

    /// <summary>A text with no glyphs, for the empty string.</summary>
    public static ShapedText Empty(byte face, float ellipsisAdvance) =>
        new(face, false, [], [], [0], null, null, null, [], [], [], 0, ellipsisAdvance);

    /// <summary>
    /// A text's clusters in logical order - the order its characters come in,
    /// which is how WPF's CharacterEllipsis cuts a line holding right-to-left
    /// text: it keeps the name's logical beginning, whatever side of the line
    /// that is drawn on, and ends the line with the ellipsis.  Cutting such a
    /// text at a visual prefix, as left-to-right text is cut, would keep the
    /// logical end of a Hebrew or Arabic name instead - a different piece of
    /// it than WPF and Explorer show.  Each cluster's width is the sum of its
    /// glyphs' advances as the whole name was shaped, the same measure the
    /// visual cut uses.
    /// </summary>
    private sealed class LogicalClusters
    {
        /// <summary>Stands for "past the last character" in a cut that keeps every cluster.</summary>
        public const int AllCharacters = 1 << 20;

        private readonly int[] _start;
        private readonly float[] _prefix;
        private readonly int[] _visibleEnd;

        public LogicalClusters(ushort[] glyphCharacter, float[] prefixAdvance, ReadOnlySpan<bool> whitespace)
        {
            var count = glyphCharacter.Length;
            var starts = new int[count];
            for (var glyph = 0; glyph < count; glyph++)
            {
                starts[glyph] = glyphCharacter[glyph];
            }

            Array.Sort(starts);
            var clusters = 0;
            for (var index = 0; index < count; index++)
            {
                if (index == 0 || starts[index] != starts[index - 1])
                {
                    starts[clusters++] = starts[index];
                }
            }

            _start = starts.AsSpan(0, clusters).ToArray();
            var width = new float[clusters];
            var blank = new bool[clusters];
            Array.Fill(blank, true);
            for (var glyph = 0; glyph < count; glyph++)
            {
                var cluster = Array.BinarySearch(_start, (int)glyphCharacter[glyph]);
                width[cluster] += Math.Max(0, prefixAdvance[glyph + 1] - prefixAdvance[glyph]);
                blank[cluster] &= whitespace[glyph];
            }

            _prefix = new float[clusters + 1];
            for (var cluster = 0; cluster < clusters; cluster++)
            {
                _prefix[cluster + 1] = _prefix[cluster] + width[cluster];
            }

            // Whitespace at the logical end of a cut hangs, as it does in the
            // visual cut: it neither counts against the room nor is drawn.
            _visibleEnd = new int[clusters + 1];
            for (var cut = 0; cut <= clusters; cut++)
            {
                var end = cut;
                while (end > 0 && blank[end - 1])
                {
                    end--;
                }

                _visibleEnd[cut] = end;
            }
        }

        /// <summary>The largest run of whole clusters from the start whose visible width fits <paramref name="room"/>; at least one.</summary>
        public TextCut Trim(float room, float ellipsisAdvance)
        {
            var clusters = _start.Length;
            int low = 0, high = clusters;
            while (low < high)
            {
                var middle = (low + high + 1) >> 1;
                if (_prefix[_visibleEnd[middle]] <= room)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }

            var kept = Math.Max(1, low);
            var visible = _visibleEnd[kept];
            return new TextCut(
                kept < clusters ? _start[kept] : AllCharacters,
                visible < clusters ? _start[visible] : AllCharacters,
                true,
                _prefix[visible] + ellipsisAdvance);
        }
    }
}

/// <summary>
/// Where a text is cut: <see cref="Kept"/> glyphs belong to the line (the
/// start of WPF's collapsed range), the first <see cref="Visible"/> of them
/// are drawn (trailing whitespace hangs), then the ellipsis when
/// <see cref="Ellipsis"/>.  <see cref="Width"/> is what the line then
/// measures, in em.
/// </summary>
internal readonly record struct TextCut(int Kept, int Visible, bool Ellipsis, float Width);
