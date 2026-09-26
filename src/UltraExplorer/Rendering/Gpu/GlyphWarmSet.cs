namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// The characters whose glyphs are rasterised before the canvas first draws
/// on the GPU, so no name the user is likely to meet waits for its glyphs.
///
/// For the two text faces: printable ASCII (digits included), Latin-1,
/// Latin Extended-A, all of Cyrillic (U+0400-U+04FF), General Punctuation
/// and the ellipsis trimming ends lines with.  For the icon face: exactly
/// the Segoe Fluent Icons glyphs the nested canvas draws - its badges,
/// folder, drive, network drive, computer and link glyphs, the note mark
/// and the beacon glyphs.  Each at all three tiers.
///
/// Glyphs shaping picks that are not the characters' own (ligatures,
/// contextual forms, localised Cyrillic forms), and anything from fallback
/// fonts, are rasterised the first time they are drawn.
/// </summary>
internal static class GlyphWarmSet
{
    /// <summary>
    /// The icon font's code points NestedCanvas uses (Badges, Glyph,
    /// BeaconGlyph and the note mark), found by listing the private-use
    /// characters in NestedCanvas.cs:
    /// E70B note, E71B link, E721 search, E72E the could-not-read badge,
    /// E735 pinned star, E7F4 computer, E8B7 folder, E8CE network drive,
    /// EDA2 drive.
    /// </summary>
    public static readonly int[] IconCodePoints =
    [
        0xE70B, 0xE71B, 0xE721, 0xE72E, 0xE735, 0xE7F4, 0xE8B7, 0xE8CE, 0xEDA2
    ];

    /// <summary>The text faces' warm characters, in code point order, without duplicates.</summary>
    public static int[] TextCodePoints { get; } = BuildTextCodePoints();

    private static int[] BuildTextCodePoints()
    {
        var points = new SortedSet<int>();
        AddRange(points, 0x0020, 0x007E); // ASCII, digits included
        AddRange(points, 0x00A0, 0x00FF); // Latin-1 Supplement
        AddRange(points, 0x0100, 0x017F); // Latin Extended-A
        AddRange(points, 0x0400, 0x04FF); // Cyrillic
        AddRange(points, 0x2000, 0x206F); // General Punctuation
        points.Add(0x2026);               // the trimming ellipsis
        return [.. points];
    }

    private static void AddRange(SortedSet<int> points, int first, int last)
    {
        for (var point = first; point <= last; point++)
        {
            points.Add(point);
        }
    }
}
