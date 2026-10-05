using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using UltraExplorer.Infrastructure;
using UltraExplorer.Rendering.Gpu;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace ViewAllSmoke;

/// <summary>
/// The GPU text path's CPU side, headless: the fonts it resolves against
/// what WPF resolves, its shaping and trimming against FormattedText on real
/// file names, the fallback path for scripts the UI font lacks, the distance
/// fields, the atlas's start-up, its cache file, its upload to a graphics
/// card, and that drawing a frame's labels allocates nothing.  The GPU text
/// must look like the WPF text it replaces, so WPF is the reference
/// throughout: same typeface strings, same size, same culture.
/// </summary>
internal static partial class Program
{
    private const double GpuTextSize = 12;

    private static readonly Typeface GpuTextFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface GpuTextFaceBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface GpuIconFace = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static Task GpuTextChecks()
    {
        Section("gpu text");
        var faces = FaceRegistry.Shared;
        var corpus = GpuTextCorpus();

        GpuTextFaceChecks(faces);

        // The atlas first: its timing is the one most disturbed by what ran
        // before (see GpuTextAtlasChecks).
        using var atlas = GpuTextAtlasChecks(faces);
        GpuTextZoomChecks(faces, atlas);
        GpuTextShapingChecks(faces, corpus);
        GpuTextTrimmingChecks(faces, corpus);
        GpuTextSdfChecks(faces);
        GpuTextComplexChecks(faces, atlas);
        GpuTextRightToLeftTrimmingChecks(faces, atlas);
        GpuTextUploadChecks(faces, atlas);
        GpuTextGrowChecks(faces);
        GpuTextEmissionChecks(faces, atlas, corpus);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Real names: this machine's Windows folder and the first few hundred
    /// entries of System32, plus Latin and Cyrillic names of the kind a
    /// Russian-speaking user's disk holds, with kerning-heavy capitals and
    /// spaces where trimming has to decide.
    /// </summary>
    private static List<string> GpuTextCorpus()
    {
        var names = new List<string>
        {
            "Новая папка", "Отчёт за 2024 год.docx", "Фотографии с отпуска — Крым", "Документы", "Загрузки",
            "Съёмка_ЁЖИК.jpg", "Рабочий стол", "Музыка (сборник) Vol. 2", "Проект «Альфа» — черновик.txt",
            "Счёт-фактура №125 от 12.03.2025.pdf", "ЮРИДИЧЕСКИЕ ДОКУМЕНТЫ", "Щит и меч.mkv", "Ёлка.png",
            "Wave AVAVAV To Ty.txt", "LTAVATAR.png", "Yearly Report (final) v2.xlsx", "Downloaded Program Files",
            "Tasks", "Temp", "WAVE.wav", "Typography Today.pdf", "VAT return — Q3.xlsx", "Łódź Żółć.txt",
            "naïve café résumé.doc", "Ångström Ölfass Überlänge.txt", "  leading and trailing  ", "a", "W",
            "1234567890", "__init__.py", "package-lock.json", "node_modules", ".gitignore", "README.md",
        };

        foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.SystemDirectory })
        {
            try
            {
                names.AddRange(Directory.GetFileSystemEntries(folder)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .Order(StringComparer.Ordinal)
                    .Take(400)!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    // ---- faces and metrics ---------------------------------------------------------

    private static void GpuTextFaceChecks(FaceRegistry faces)
    {
        // The same font as WPF's: same file weight and same advance for every
        // glyph of a sample, in both weights.  (Names alone do not show it:
        // WPF lists the variable font's typographic family "Segoe UI
        // Variable", DirectWrite the named instance "Segoe UI Variable Text".)
        foreach (var (face, typeface) in new[] { (FaceRegistry.Regular, GpuTextFace), (FaceRegistry.SemiBold, GpuTextFaceBold), (FaceRegistry.Icons, GpuIconFace) })
        {
            typeface.TryGetGlyphTypeface(out var wpfGlyphs);
            var info = faces[face];
            var sample = face == FaceRegistry.Icons ? "\uE70B\uE8B7\uEDA2" : "AgWfЖя…0";
            var same = wpfGlyphs is not null && wpfGlyphs.Weight.ToOpenTypeWeight() == (int)info.Weight;
            foreach (var character in sample)
            {
                var glyph = info.Face.GetGlyphIndices([(uint)character])[0];
                var advances = new int[1];
                info.Face.GetDesignGlyphAdvances(1, [glyph], advances, false);
                same &= wpfGlyphs is not null
                    && wpfGlyphs.CharacterToGlyphMap.TryGetValue(character, out var wpfGlyph)
                    && wpfGlyph == glyph
                    && Math.Abs(wpfGlyphs.AdvanceWidths[wpfGlyph] - advances[0] / info.UnitsPerEm) < 1e-6;
            }

            Check($"gpu text: face {face} is the font WPF draws that typeface with ({info.FamilyName} {(int)info.Weight}, {Path.GetFileName(wpfGlyphs?.FontUri.LocalPath)})", same);
        }

        Check("gpu text: SemiBold is its own weight", (int)faces[FaceRegistry.SemiBold].Weight == 600);
        Check("gpu text: the icon face has the note glyph", faces[FaceRegistry.Icons].Face.GetGlyphIndices([0xE70Bu])[0] != 0);

        // Height and baseline must be FormattedText's, or every label would
        // move off the centre the WPF path puts it on.
        var worst = 0.0;
        foreach (var (face, typeface) in new[] { (FaceRegistry.Regular, GpuTextFace), (FaceRegistry.SemiBold, GpuTextFaceBold), (FaceRegistry.Icons, GpuIconFace) })
        {
            foreach (var size in new[] { 7.5, 9.5, 12, 13.37, 30 })
            {
                var formatted = new FormattedText(face == FaceRegistry.Icons ? "\uE70B" : "Ag", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, Brushes.White, 1.25);
                worst = Math.Max(worst, Math.Abs(formatted.Height - faces[face].LineHeight * size));
                worst = Math.Max(worst, Math.Abs(formatted.Baseline - faces[face].Baseline * size));
            }
        }

        Check($"gpu text: line height and baseline match FormattedText for all three faces (worst {worst:F4} DIP)", worst < 0.01);
        Check($"gpu text: the cache file is named by the fonts ({Path.GetFileName(GlyphAtlas.DefaultCachePath(faces))})",
            GlyphAtlas.DefaultCachePath(faces).StartsWith(AppPaths.StateDirectory, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(GlyphAtlas.DefaultCachePath(faces)) == $"glyphs-v1-{faces.FontHash}.bin");
    }

    // ---- shaping -------------------------------------------------------------------

    private static void GpuTextShapingChecks(FaceRegistry faces, List<string> corpus)
    {
        var shaper = new TextShaper(faces);
        var worst = 0.0;
        var worstName = string.Empty;
        var shaped = 0;
        var direct = 0;
        foreach (var (face, typeface) in new[] { (FaceRegistry.Regular, GpuTextFace), (FaceRegistry.SemiBold, GpuTextFaceBold) })
        {
            foreach (var name in corpus)
            {
                if (!shaper.TryGet(name, face, out var text))
                {
                    continue;
                }

                shaped++;
                direct += text.Complex ? 0 : 1;
                var formatted = new FormattedText(name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, GpuTextSize, Brushes.White, 1.25);
                if (formatted.Width <= 0)
                {
                    continue;
                }

                var error = Math.Abs(text.Width * GpuTextSize - formatted.Width) / formatted.Width;
                if (error > worst)
                {
                    worst = error;
                    worstName = name;
                }
            }
        }

        // The canvas's icon strings - badges, folder and drive glyphs, beacons -
        // shape directly in the icon face, and every glyph they use is in the
        // warm set, so a badge never waits for the atlas.
        var iconText = new string([.. GlyphWarmSet.IconCodePoints.Select(point => (char)point)]);
        var iconShaped = shaper.TryGet(iconText, FaceRegistry.Icons, out var icons) && !icons.Complex && icons.Count == iconText.Length;
        var warmIcons = faces[FaceRegistry.Icons].Face.GetGlyphIndices([.. GlyphWarmSet.IconCodePoints.Select(point => (uint)point)]);
        Check("gpu text: the canvas's icon glyphs shape directly, each to its own warm glyph",
            iconShaped && icons.Glyphs.SequenceEqual(warmIcons) && warmIcons.All(glyph => glyph != 0));

        Check($"gpu text: every Latin and Cyrillic name shapes on the direct path ({direct} of {corpus.Count * 2})", direct == corpus.Count * 2 && shaped == direct);
        Check($"gpu text: widths within 0.5 % of FormattedText at 12 DIP over {shaped} names (worst {worst:P3}, {worstName})", worst <= 0.005 && shaped > 100);

        // What a name costs when a frame has to shape it: the direct path on a
        // fresh shaper, so nothing is cached.
        var timing = new TextShaper(faces);
        var clock = Stopwatch.StartNew();
        foreach (var name in corpus)
        {
            timing.TryGet(name, FaceRegistry.Regular, out _);
        }

        var perName = clock.Elapsed.TotalMicroseconds / corpus.Count;
        Check($"gpu text: the direct path shapes a name in {perName:F1} microseconds", perName < 100);

        var hits = Stopwatch.StartNew();
        for (var round = 0; round < 10; round++)
        {
            foreach (var name in corpus)
            {
                timing.TryGet(name, FaceRegistry.Regular, out _);
            }
        }

        Check($"gpu text: a cached name is found in {hits.Elapsed.TotalMicroseconds / (corpus.Count * 10.0) * 1000:F0} ns", hits.Elapsed.TotalMicroseconds / (corpus.Count * 10.0) < 5);
    }

    // ---- trimming ------------------------------------------------------------------

    /// <summary>
    /// The cut WPF's CharacterEllipsis makes, read from WPF itself: the line
    /// FormattedText would build (TextFormatter, ideal mode, no wrapping),
    /// collapsed with a TextTrailingCharacterEllipsis, and the first
    /// character of its collapsed range.  Returns -1 when the text fits.
    /// </summary>
    private static (int Cut, double Width) WpfEllipsisCut(string text, Typeface typeface, double size, double maxWidth)
    {
        var run = new GpuTextRunProperties(typeface, size);
        var source = new GpuTextSource(text, run);
        var paragraph = new GpuTextParagraphProperties(run);
        using var line = TextFormatter.Create(TextFormattingMode.Ideal).FormatLine(source, 0, maxWidth, paragraph, null);
        if (line.Width <= maxWidth)
        {
            return (-1, line.Width);
        }

        using var collapsed = line.Collapse(new TextTrailingCharacterEllipsis(maxWidth, run));
        var ranges = collapsed.GetTextCollapsedRanges();
        return (ranges is { Count: > 0 } ? ranges[0].TextSourceCharacterIndex : text.Length, collapsed.Width);
    }

    private static void GpuTextTrimmingChecks(FaceRegistry faces, List<string> corpus)
    {
        var shaper = new TextShaper(faces);
        int cases = 0, equal = 0, withinOne = 0, widthsEqual = 0;
        var mismatches = new List<string>();
        foreach (var (face, typeface) in new[] { (FaceRegistry.Regular, GpuTextFace), (FaceRegistry.SemiBold, GpuTextFaceBold) })
        {
            foreach (var name in corpus)
            {
                if (!shaper.TryGet(name, face, out var text) || text.Complex || text.Count != name.Length || name.Length < 2)
                {
                    continue;
                }

                var natural = text.Width * GpuTextSize;
                for (var fraction = 0.05; fraction < 1.0; fraction += 0.083)
                {
                    var maxWidth = Math.Round(natural * fraction, 2);
                    var (wpfCut, wpfWidth) = WpfEllipsisCut(name, typeface, GpuTextSize, maxWidth);
                    if (wpfCut < 0)
                    {
                        continue;
                    }

                    var cut = text.Trim((float)(maxWidth / GpuTextSize));
                    var ours = cut.Kept >= text.Count ? name.Length : text.GlyphCharacter[cut.Kept];
                    if (cut.Kept == 0)
                    {
                        ours = 0;
                    }

                    cases++;
                    if (ours == wpfCut)
                    {
                        equal++;
                        // WPF rounds every advance to its ideal units, so long
                        // names drift by a few hundredths; kerning differences
                        // would be tenths.
                        if (Math.Abs(cut.Width * GpuTextSize - wpfWidth) <= 0.01 + 0.0005 * wpfWidth)
                        {
                            widthsEqual++;
                        }
                    }
                    else if (mismatches.Count < 3)
                    {
                        mismatches.Add($"'{name}' at {maxWidth}: WPF {wpfCut}, ours {ours}");
                    }

                    withinOne += Math.Abs(ours - wpfCut) <= 1 ? 1 : 0;
                }
            }
        }

        // WPF's line services sometimes shape the kept text again when the
        // cut splits a kerned pair (an "AV" or a "Te"), and sometimes not, in
        // a way its public surface does not reveal; those few cases may land
        // one character apart.  Everything else must be the same cut.
        Check($"gpu text: CharacterEllipsis cut equals WPF's in {equal} of {cases} cases ({(double)equal / Math.Max(1, cases):P2})"
            + (mismatches.Count > 0 ? $"; e.g. {string.Join("; ", mismatches)}" : string.Empty),
            cases > 1000 && equal >= cases * 0.995);
        Check($"gpu text: every trimming cut is within one character of WPF's ({withinOne} of {cases})", withinOne == cases);
        Check($"gpu text: the trimmed width equals FormattedText's where the cut agrees ({widthsEqual} of {equal})", widthsEqual == equal);

        // The edges: a room narrower than the ellipsis keeps only the
        // ellipsis; a room for the ellipsis keeps at least one character.
        shaper.TryGet("Documents", FaceRegistry.Regular, out var documents);
        var tiny = documents.Trim(documents.EllipsisAdvance * 0.9f);
        var small = documents.Trim(documents.EllipsisAdvance * 1.05f);
        Check("gpu text: a room narrower than the ellipsis shows the ellipsis alone", tiny.Kept == 0 && tiny.Ellipsis);
        Check("gpu text: a room just wider than the ellipsis keeps one character", small.Kept == 1 && small.Visible == 1);
        Check("gpu text: text that fits is not cut", documents.Trim(documents.Width + 0.01f) is { Ellipsis: false } whole && whole.Visible == documents.Count);
    }

    // ---- distance fields -----------------------------------------------------------

    private static void GpuTextSdfChecks(FaceRegistry faces)
    {
        // A disc of radius 9 in a 24 x 24 box, its coverage from 16 x 16
        // supersampling, so edge pixels are partly covered as DirectWrite's are.
        const int size = 24;
        const int spread = 4;
        const double radius = 9;
        var centre = size / 2.0;
        var coverage = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var inside = 0;
                for (var sy = 0; sy < 16; sy++)
                {
                    for (var sx = 0; sx < 16; sx++)
                    {
                        var dx = x + (sx + 0.5) / 16 - centre;
                        var dy = y + (sy + 0.5) / 16 - centre;
                        inside += dx * dx + dy * dy <= radius * radius ? 1 : 0;
                    }
                }

                coverage[y * size + x] = (byte)Math.Round(inside * 255.0 / 256);
            }
        }

        var fieldSide = size + 2 * spread;
        var field = new byte[fieldSide * fieldSide];
        SdfGenerator.ForThread.Generate(coverage, size, size, spread, field);

        int signWrong = 0, farWrong = 0;
        var worstError = 0.0;
        var worstEdge = 0.0;
        for (var y = 0; y < fieldSide; y++)
        {
            for (var x = 0; x < fieldSide; x++)
            {
                var dx = x - spread + 0.5 - centre;
                var dy = y - spread + 0.5 - centre;
                var distance = Math.Sqrt(dx * dx + dy * dy) - radius;
                var value = field[y * fieldSide + x];
                if ((distance < -0.75 && value <= 128) || (distance > 0.75 && value >= 127))
                {
                    signWrong++;
                }

                if (Math.Abs(distance) < spread - 0.5)
                {
                    var decoded = (0.5 - value / 255.0) * 2 * spread;
                    worstError = Math.Max(worstError, Math.Abs(decoded - distance));
                }
                else if (distance >= spread + 0.5 && value != 0)
                {
                    farWrong++;
                }
            }
        }

        // Where the field crosses the edge value along each row is where the
        // shader draws the outline: it must be where the disc's edge is.
        for (var y = spread; y < spread + size; y++)
        {
            var dy = y + 0.5 - spread - centre;
            if (Math.Abs(dy) > radius - 1)
            {
                continue;
            }

            for (var x = 1; x < fieldSide; x++)
            {
                double left = field[y * fieldSide + x - 1], right = field[y * fieldSide + x];
                if (left < 127.5 && right >= 127.5)
                {
                    // Measured across the outline, not along the row.
                    var crossing = x - 1 + 0.5 + (127.5 - left) / (right - left) - spread - centre;
                    worstEdge = Math.Max(worstEdge, Math.Abs(Math.Sqrt(crossing * crossing + dy * dy) - radius));
                    break;
                }
            }
        }

        Check($"gpu text: the field is above the edge inside and below it outside (wrong: {signWrong})", signWrong == 0);
        Check($"gpu text: the field's edge is where the outline is within a tenth of a texel (worst {worstEdge:F3})", worstEdge < 0.1);

        // TinySDF measures to texel centres, so next to an edge that falls on
        // a texel boundary a texel reads half a texel off; within that the
        // field is the true distance.
        Check($"gpu text: the field is the true distance within about half a texel (worst {worstError:F3})", worstError < 0.6);
        Check($"gpu text: the field is empty beyond its spread (wrong: {farWrong})", farWrong == 0);

        // Along the row through the centre the field rises to the middle and
        // falls after it: never the wrong way.
        var row = (int)(spread + centre);
        var monotone = true;
        for (var x = 1; x < fieldSide; x++)
        {
            var step = field[row * fieldSide + x] - field[row * fieldSide + x - 1];
            if (x <= fieldSide / 2 ? step < 0 : step > 0)
            {
                monotone = false;
            }
        }

        Check("gpu text: the field rises towards the ink and falls away from it", monotone);

        // A real glyph: an 'O' at the largest tier keeps its counter open.
        var rasterizer = GlyphRasterizer.ForThread;
        var inked = rasterizer.Rasterise(faces.Factory, faces[FaceRegistry.Regular], faces[FaceRegistry.Regular].Face.GetGlyphIndices([(uint)'O'])[0], GlyphAtlas.Tiers[2]);
        var glyph = rasterizer.Field;
        var middle = glyph[rasterizer.FieldHeight / 2 * rasterizer.FieldWidth + rasterizer.FieldWidth / 2];
        var ring = glyph[rasterizer.FieldHeight / 2 * rasterizer.FieldWidth + GlyphAtlas.Tiers[2].Spread + 2];
        Check($"gpu text: a rasterised 'O' has ink on its ring ({ring}) and a hole in its counter ({middle})", inked && ring > 127 && middle < 128);
        Check("gpu text: a space has no field", !rasterizer.Rasterise(faces.Factory, faces[FaceRegistry.Regular], faces[FaceRegistry.Regular].Face.GetGlyphIndices([(uint)' '])[0], GlyphAtlas.Tiers[1]));
    }

    // ---- the atlas -----------------------------------------------------------------

    private static GlyphAtlas GpuTextAtlasChecks(FaceRegistry faces)
    {
        // The first run pays for the JIT; the best of the next four is what a
        // start-up with warm code costs.  Repeated because a run can be
        // slowed three times over by the runtime rather than by the atlas:
        // after a burst of WPF text work the tiered JIT promotes hundreds of
        // methods, and each batch briefly suspends every managed thread -
        // measured, and gone with DOTNET_TieredCompilation=0.  None of the
        // runs touches the cache file.
        TimeSpan first;
        using (var cold = new GlyphAtlas(faces))
        {
            first = cold.WarmUp(null).Elapsed;
        }

        var second = TimeSpan.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            using var again = new GlyphAtlas(faces);
            var elapsed = again.WarmUp(null).Elapsed;
            second = elapsed < second ? elapsed : second;
        }

        var atlas = new GlyphAtlas(faces);
        var result = atlas.WarmUp(null);
        second = result.Elapsed < second ? result.Elapsed : second;

        var items = atlas.WarmItems();
        var present = items.Count(key => atlas.TryGet(key.Face, key.Glyph, key.Tier, out _));
        Check($"gpu text: the warm set is {result.Glyphs} glyphs (both weights and the icons, three tiers) and all are placed ({present})",
            result.Glyphs > 3000 && present == items.Count && atlas.PendingCount == 0);
        Check($"gpu text: the warm set rasterises in parallel in {second.TotalMilliseconds:F1} ms (first run with the JIT: {first.TotalMilliseconds:F1} ms) on {Environment.ProcessorCount} threads",
            second.TotalMilliseconds < 60);
        Check($"gpu text: it fits in {atlas.PageCount} pages of 2048 x 2048", atlas.PageCount <= 4);

        // The cache file: written on a miss, read on the next start, and read
        // back exactly - entries and texels.
        var folder = Path.Combine(Path.GetTempPath(), "UltraExplorerGpuText-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(folder, $"glyphs-v1-{faces.FontHash}.bin");
            using var writer = new GlyphAtlas(faces);
            var written = writer.WarmUp(path);
            using var reader = new GlyphAtlas(faces);
            var read = reader.WarmUp(path);
            var identical = read.FromCache && reader.EntryCount == writer.EntryCount && reader.PageCount == writer.PageCount;
            if (identical)
            {
                foreach (var key in items)
                {
                    writer.TryGet(key.Face, key.Glyph, key.Tier, out var a);
                    reader.TryGet(key.Face, key.Glyph, key.Tier, out var b);
                    identical &= a.Page == b.Page && a.UV0 == b.UV0 && a.UV1 == b.UV1 && a.Left == b.Left && a.Bottom == b.Bottom;
                }

                identical &= GpuTextPagesEqual(writer, reader);
            }

            Check($"gpu text: the atlas is saved on a miss ({written.Saving.TotalMilliseconds:F1} ms, {new FileInfo(path).Length / 1024} KB) and read back the same next time in {read.Elapsed.TotalMilliseconds:F1} ms",
                !written.FromCache && File.Exists(path) && identical);

            // One texel changed, or the file cut short: only the hash can tell
            // the first, and both are turned away before anything is restored.
            var good = File.ReadAllBytes(path);
            var changed = (byte[])good.Clone();
            changed[changed.Length / 2] ^= 0x40;
            File.WriteAllBytes(path, changed);
            using var altered = new GlyphAtlas(faces);
            var alteredRead = altered.WarmUp(path);
            File.WriteAllBytes(path, good[..(good.Length / 2)]);
            using var shortened = new GlyphAtlas(faces);
            var shortenedRead = shortened.WarmUp(path);
            Check("gpu text: a cache file with one byte changed, or cut short, is ignored and the glyphs made again",
                alteredRead is { FromCache: false } && alteredRead.Glyphs == result.Glyphs
                && shortenedRead is { FromCache: false } && shortenedRead.Glyphs == result.Glyphs);

            File.WriteAllBytes(path, [1, 2, 3]);
            using var damaged = new GlyphAtlas(faces);
            Check("gpu text: a damaged cache file is ignored and the glyphs made again", damaged.WarmUp(path) is { FromCache: false } again && again.Glyphs == result.Glyphs);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        return atlas;
    }

    private static bool GpuTextPagesEqual(GlyphAtlas a, GlyphAtlas b)
    {
        for (var page = 0; page < a.PageCount; page++)
        {
            if (!GpuTextPage(a.PagePointer(page), GlyphAtlas.PageSize).AsSpan().SequenceEqual(GpuTextPage(b.PagePointer(page), GlyphAtlas.PageSize)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A page of texels copied out of native memory, <paramref name="rowPitch"/> bytes apart.</summary>
    private static byte[] GpuTextPage(nint pointer, long rowPitch)
    {
        var page = new byte[GlyphAtlas.PageSize * GlyphAtlas.PageSize];
        for (var row = 0; row < GlyphAtlas.PageSize; row++)
        {
            System.Runtime.InteropServices.Marshal.Copy(pointer + (nint)(row * rowPitch), page, row * GlyphAtlas.PageSize, GlyphAtlas.PageSize);
        }

        return page;
    }

    // ---- complex text --------------------------------------------------------------

    private static void GpuTextComplexChecks(FaceRegistry faces, GlyphAtlas atlas)
    {
        var shaper = new TextShaper(faces);
        var arrived = 0;
        shaper.Arrived += () => Interlocked.Increment(ref arrived);
        string[] names = ["تقرير سنوي 2024.pdf", "项目计划书.docx", "写真フォルダ", "Party 🎉 photos", "מסמך חשוב.txt", "한국어 문서"];
        var queued = names.Count(name => !shaper.TryGet(name, FaceRegistry.Regular, out _));
        Check($"gpu text: text the UI font cannot show goes to the worker ({queued} of {names.Length})", queued == names.Length);
        Check("gpu text: the worker finishes the fallback names", shaper.WaitForPending(TimeSpan.FromSeconds(10)) && Volatile.Read(ref arrived) > 0);

        foreach (var name in names)
        {
            if (!shaper.TryGet(name, FaceRegistry.Regular, out var text))
            {
                Check($"gpu text: '{name}' has been shaped", false);
                continue;
            }

            var fallback = text.Faces.Any(face => face >= FaceRegistry.FirstFallback);
            var sink = new GpuTextQuadSink(new GlyphQuad[256]);
            atlas.Emit(ref sink, text, text.Whole, 0, 20, 16, 0xFFFFFFFF, 0, snap: false);
            atlas.WaitForPending(TimeSpan.FromSeconds(10));
            sink.Count = 0;
            var missing = atlas.Emit(ref sink, text, text.Whole, 0, 20, 16, 0xFFFFFFFF, 0, snap: false);
            Check($"gpu text: '{name}' shapes to {text.Count} glyphs (fallback font {(fallback ? "used" : "not needed")}) and draws {sink.Count} quads",
                text.Complex && text.Count > 0 && text.Glyphs.All(glyph => glyph != 0) && text.Width > 0 && missing == 0 && sink.Count > 0);
        }

        shaper.TryGet("项目计划书.docx", FaceRegistry.Regular, out var cjk);
        shaper.TryGet("Party 🎉 photos", FaceRegistry.Regular, out var emoji);
        Check("gpu text: CJK and emoji come from fallback fonts", cjk.Faces.Any(face => face >= FaceRegistry.FirstFallback) && emoji.Faces.Any(face => face >= FaceRegistry.FirstFallback));

        shaper.TryGet("تقرير سنوي 2024.pdf", FaceRegistry.Regular, out var arabic);
        var ordered = true;
        for (var index = 1; index <= arabic.Count; index++)
        {
            ordered &= arabic.PrefixAdvance[index] >= arabic.PrefixAdvance[index - 1];
        }

        Check("gpu text: right-to-left text is laid out left to right with rising pen positions", ordered && arabic.PrefixAdvance[^1] > 0);
    }

    /// <summary>
    /// Names with right-to-left text are cut as WPF cuts them: the logical
    /// start kept, whichever side of the line it is drawn on - not the
    /// visually leftmost glyphs, which for Hebrew or Arabic are the name's
    /// end.  Held against WPF's own collapsed line at many widths, and the
    /// glyphs drawn for a cut are exactly those of the kept clusters.
    /// </summary>
    private static void GpuTextRightToLeftTrimmingChecks(FaceRegistry faces, GlyphAtlas atlas)
    {
        var shaper = new TextShaper(faces);
        string[] names =
        [
            "דוח שנתי סופי 2024.pdf",
            "מסמכים חשובים של המשרד",
            "תמונות מהחופשה באילת",
            "تقرير سنوي 2024.pdf",
            "المستندات الخاصة بالمشروع",
            "صور العطلة الصيفية",
            "Report_שנתי_סופי_מאושר.pdf",
            "Проект — مشروع كبير جدا.docx",
        ];

        foreach (var name in names)
        {
            shaper.TryGet(name, FaceRegistry.Regular, out _);
        }

        shaper.WaitForPending(TimeSpan.FromSeconds(10));
        int cases = 0, equal = 0, withinOne = 0, visualEqual = 0, logical = 0;
        var mismatches = new List<string>();
        foreach (var name in names)
        {
            if (!shaper.TryGet(name, FaceRegistry.Regular, out var text) || text.Count == 0)
            {
                continue;
            }

            logical += text.TrimsLogically ? 1 : 0;
            var natural = text.Width * GpuTextSize;
            for (var fraction = 0.08; fraction < 1.0; fraction += 0.061)
            {
                var maxWidth = Math.Round(natural * fraction, 2);
                var (wpfCut, _) = WpfEllipsisCut(name, GpuTextFace, GpuTextSize, maxWidth);
                if (wpfCut < 0)
                {
                    continue;
                }

                var cut = text.Trim((float)(maxWidth / GpuTextSize));
                var ours = !cut.Ellipsis ? name.Length : Math.Min(cut.Kept, name.Length);
                cases++;
                equal += ours == wpfCut ? 1 : 0;
                withinOne += Math.Abs(ours - wpfCut) <= 1 ? 1 : 0;
                if (ours != wpfCut && mismatches.Count < 3)
                {
                    mismatches.Add($"'{name}' at {maxWidth}: WPF {wpfCut}, ours {ours}");
                }

                // Whether cutting at the visual prefix, as left-to-right text
                // is cut, would have kept the same characters as WPF.
                var visualKept = 0;
                var room = maxWidth / GpuTextSize - text.EllipsisAdvance;
                while (visualKept < text.Count && text.PrefixAdvance[visualKept + 1] <= room)
                {
                    visualKept++;
                }

                var sameCharacters = true;
                for (var glyph = 0; glyph < text.Count; glyph++)
                {
                    sameCharacters &= glyph < visualKept == text.GlyphCharacter[glyph] < wpfCut;
                }

                visualEqual += sameCharacters ? 1 : 0;
            }
        }

        Check($"gpu text: every name with right-to-left text cuts in logical order ({logical} of {names.Length})", logical == names.Length);
        Check($"gpu text: right-to-left and mixed names are cut where WPF cuts them in {equal} of {cases} cases ({(double)equal / Math.Max(1, cases):P1}; the visual cut agreed in {visualEqual})"
            + (mismatches.Count > 0 ? $"; e.g. {string.Join("; ", mismatches)}" : string.Empty),
            cases >= 40 && equal >= cases * 0.9 && withinOne >= cases * 0.97);

        // The glyphs a cut draws: those of the clusters before its first
        // character left out, packed from the origin, then the ellipsis.
        shaper.TryGet("מסמכים חשובים של המשרד", FaceRegistry.Regular, out var hebrew);
        var fontPx = GpuTextSize * 1.25;
        var tier = GlyphAtlas.TierFor(fontPx);
        var trimmed = hebrew.Trim(hebrew.Width * 0.55f);
        var quads = new GlyphQuad[256];
        var sink = new GpuTextQuadSink(quads);
        atlas.Emit(ref sink, hebrew, trimmed, 100, 50, fontPx, 0xFFFFFFFF, 0, snap: false);
        atlas.WaitForPending(TimeSpan.FromSeconds(10));
        sink = new GpuTextQuadSink(quads);
        var missing = atlas.Emit(ref sink, hebrew, trimmed, 100, 50, fontPx, 0xFFFFFFFF, 0, snap: false);

        var expected = new List<uint>();
        for (var glyph = 0; glyph < hebrew.Count; glyph++)
        {
            if (hebrew.GlyphCharacter[glyph] < trimmed.Visible && atlas.TryGet(hebrew.Faces[glyph], hebrew.Glyphs[glyph], tier, out var entry) && !entry.IsEmpty)
            {
                expected.Add(entry.UV0);
            }
        }

        atlas.TryGet(hebrew.Face, faces[hebrew.Face].EllipsisGlyph, tier, out var ellipsis);
        expected.Add(ellipsis.UV0);
        var drawn = quads.Take(sink.Count).Select(quad => quad.UV0).ToList();
        var rightmost = quads.Take(sink.Count).MaxBy(quad => quad.Rect.Z);
        var leftmost = quads.Take(sink.Count).Min(quad => quad.Rect.X);
        Check($"gpu text: a cut Hebrew name draws its first {trimmed.Visible} characters' glyphs, packed from the start, then the ellipsis ({sink.Count} quads)",
            missing == 0 && trimmed.Ellipsis && trimmed.Visible > 0
            && drawn.Order().SequenceEqual(expected.Order())
            && rightmost.UV0 == ellipsis.UV0
            && leftmost >= 100 - fontPx * 0.2 && rightmost.Rect.Z <= 100 + trimmed.Width * fontPx + fontPx * 0.2);
    }

    /// <summary>
    /// The glyph atlas outgrowing its texture on a card: the larger array is
    /// made on the GPU - what the card had copied across, the new slices
    /// empty - not again from the CPU pages, and every page reads back equal
    /// to the CPU copy afterwards.
    /// </summary>
    private static void GpuTextGrowChecks(FaceRegistry faces)
    {
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        var driver = "hardware";
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        if (D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out device, out context).Failure)
        {
            driver = "WARP";
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out device, out context).CheckError();
        }

        using (device)
        using (context)
        using (var atlas = new GlyphAtlas(faces))
        {
            // Two full pages of a pattern, as a cache file would put them back.
            var pattern = new byte[GlyphAtlas.PageSize * GlyphAtlas.PageSize];
            List<ShelfPacker.Shelf>[] full = [[new ShelfPacker.Shelf(0, GlyphAtlas.PageSize, GlyphAtlas.PageSize)], [new ShelfPacker.Shelf(0, GlyphAtlas.PageSize, GlyphAtlas.PageSize)]];
            var restored = atlas.Restore(2, full, [], (page, pointer) =>
            {
                for (var index = 0; index < pattern.Length; index++)
                {
                    pattern[index] = (byte)((index * 7 + index / GlyphAtlas.PageSize * 3 + page * 101) | 1);
                }

                System.Runtime.InteropServices.Marshal.Copy(pattern, 0, pointer, pattern.Length);
                return true;
            });

            using var texture = new GlyphAtlasTexture(device!, atlas);
            var capacity = texture.Capacity;

            // A glyph with nowhere to go but a third page.
            var glyph = faces[FaceRegistry.Regular].Face.GetGlyphIndices([(uint)'W'])[0];
            atlas.TryGet(FaceRegistry.Regular, glyph, 2, out _);
            atlas.WaitForPending(TimeSpan.FromSeconds(10));
            var clock = Stopwatch.StartNew();
            var copied = texture.Upload(context!);
            clock.Stop();

            var description = texture.Texture.Description;
            description.Usage = ResourceUsage.Staging;
            description.BindFlags = BindFlags.None;
            description.CPUAccessFlags = CpuAccessFlags.Read;
            using var staging = device!.CreateTexture2D(in description);
            context!.CopyResource(staging, texture.Texture);
            var pagesMatch = true;
            var sliceEmpty = true;
            for (var slice = 0; slice < texture.Capacity; slice++)
            {
                var subresource = D3D11.CalculateSubResourceIndex(0, (uint)slice, 1);
                var mapped = context.Map(staging, subresource, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var read = GpuTextPage(mapped.DataPointer, mapped.RowPitch);
                    if (slice < atlas.PageCount)
                    {
                        pagesMatch &= read.AsSpan().SequenceEqual(GpuTextPage(atlas.PagePointer(slice), GlyphAtlas.PageSize));
                    }
                    else
                    {
                        sliceEmpty &= read.AsSpan().IndexOfAnyExcept((byte)0) < 0;
                    }
                }
                finally
                {
                    context.Unmap(staging, subresource);
                }
            }

            Check($"gpu text: a glyph on a third page grows the {driver} texture from {capacity} to {texture.Capacity} slices on the card ({clock.Elapsed.TotalMilliseconds:F2} ms, {copied} copied after)",
                restored && atlas.PageCount == 3 && capacity == 2 && texture.Capacity == 4 && texture.Grown == 1 && copied >= 1);
            Check("gpu text: after growing, every page reads back equal to the CPU copy and the spare slice is empty", pagesMatch && sliceEmpty);
        }
    }

    // ---- upload to a graphics card --------------------------------------------------

    private static void GpuTextUploadChecks(FaceRegistry faces, GlyphAtlas atlas)
    {
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        var driver = "hardware";
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        if (D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out device, out context).Failure)
        {
            driver = "WARP";
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out device, out context).CheckError();
        }

        using (device)
        using (context)
        using (var texture = new GlyphAtlasTexture(device!, atlas))
        {
            // Glyphs placed after the texture was made reach it through Upload.
            var before = atlas.EntryCount;
            foreach (var character in "ΑΒΓΔΩαβγδωЀЁЂ")
            {
                var glyph = faces[FaceRegistry.Regular].Face.GetGlyphIndices([(uint)character])[0];
                for (var tier = 0; tier < GlyphAtlas.TierCount; tier++)
                {
                    atlas.TryGet(FaceRegistry.Regular, glyph, tier, out _);
                }
            }

            atlas.WaitForPending(TimeSpan.FromSeconds(10));
            var uploaded = texture.Upload(context!);

            var description = texture.Texture.Description;
            description.Usage = ResourceUsage.Staging;
            description.BindFlags = BindFlags.None;
            description.CPUAccessFlags = CpuAccessFlags.Read;
            using var staging = device!.CreateTexture2D(in description);
            context!.CopyResource(staging, texture.Texture);

            var matches = true;
            for (var page = 0; page < atlas.PageCount && matches; page++)
            {
                var subresource = D3D11.CalculateSubResourceIndex(0, (uint)page, 1);
                var mapped = context.Map(staging, subresource, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    matches = GpuTextPage(mapped.DataPointer, mapped.RowPitch).AsSpan()
                        .SequenceEqual(GpuTextPage(atlas.PagePointer(page), GlyphAtlas.PageSize));
                }
                finally
                {
                    context.Unmap(staging, subresource);
                }
            }

            Check($"gpu text: {atlas.EntryCount - before} new glyphs reach the {driver} texture in {uploaded} copies and every page reads back equal to the CPU copy",
                atlas.EntryCount > before && uploaded > 0 && matches);
        }
    }

    // ---- per-frame emission ----------------------------------------------------------

    private static void GpuTextEmissionChecks(FaceRegistry faces, GlyphAtlas atlas, List<string> corpus)
    {
        var shaper = new TextShaper(faces);
        var quads = new GlyphQuad[64 * 1024];
        var labels = Enumerable.Range(0, 2000).Select(index => corpus[index % corpus.Count]).ToArray();

        // One frame: look every label up, trim it to a room that changes
        // with the zoom, and emit it.  Everything a real frame of the label
        // target does with text, minus the instance buffer.
        int Frame(double zoom)
        {
            var sink = new GpuTextQuadSink(quads);
            var missing = 0;
            for (var index = 0; index < labels.Length; index++)
            {
                if (!shaper.TryGet(labels[index], (byte)(index & 1), out var text))
                {
                    continue;
                }

                var fontPx = 9 + zoom * 20;
                var cut = text.Trim((float)((40 + (index % 7) * 30) * zoom / fontPx));
                missing += atlas.Emit(ref sink, text, cut, 10.25 + index % 50, 30.5 + index % 40, fontPx, 0xFFF2F2F2, GlyphAtlas.BiasFor(fontPx), snap: (index & 2) != 0);
            }

            return missing + (sink.Count > quads.Length ? 1 : 0) + (sink.Count == 0 ? 1 : 0);
        }

        // Warm: shape everything and let the atlas make what these sizes need.
        for (var zoom = 0.1; zoom < 2; zoom += 0.3)
        {
            Frame(zoom);
        }

        shaper.WaitForPending(TimeSpan.FromSeconds(10));
        atlas.WaitForPending(TimeSpan.FromSeconds(10));
        for (var zoom = 0.1; zoom < 2; zoom += 0.3)
        {
            Frame(zoom);
        }

        atlas.WaitForPending(TimeSpan.FromSeconds(10));

        var missingTotal = 0;
        var clock = new Stopwatch();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        clock.Start();
        const int frames = 20;
        for (var frame = 0; frame < frames; frame++)
        {
            missingTotal += Frame(0.1 + frame * 0.09);
        }

        clock.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Check($"gpu text: {frames} frames of {labels.Length} labels look up, trim and emit with {allocated} bytes allocated", allocated == 0 && missingTotal == 0);
        Check($"gpu text: a glyph quad is the design's 40-byte GlyphInstance ({System.Runtime.CompilerServices.Unsafe.SizeOf<GlyphQuad>()} bytes)", System.Runtime.CompilerServices.Unsafe.SizeOf<GlyphQuad>() == 40);
        Check($"gpu text: a frame of {labels.Length} labels costs {clock.Elapsed.TotalMilliseconds / frames:F2} ms", clock.Elapsed.TotalMilliseconds / frames < 5);
    }

    private struct GpuTextQuadSink(GlyphQuad[] quads) : IGlyphSink
    {
        public int Count;

        public void Add(in GlyphQuad quad)
        {
            if (Count < quads.Length)
            {
                quads[Count] = quad;
            }

            Count++;
        }
    }

    // ---- the WPF reference line --------------------------------------------------------

    private sealed class GpuTextSource(string text, TextRunProperties properties) : TextSource
    {
        public override TextRun GetTextRun(int textSourceCharacterIndex) =>
            textSourceCharacterIndex < text.Length
                ? new TextCharacters(text, textSourceCharacterIndex, text.Length - textSourceCharacterIndex, properties)
                : new TextEndOfParagraph(1);

        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int textSourceCharacterIndexLimit) =>
            new(0, new CultureSpecificCharacterBufferRange(CultureInfo.CurrentUICulture, new CharacterBufferRange(string.Empty, 0, 0)));

        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int textSourceCharacterIndex) => textSourceCharacterIndex;
    }

    private sealed class GpuTextRunProperties : TextRunProperties
    {
        public GpuTextRunProperties(Typeface typeface, double size)
        {
            Typeface = typeface;
            FontRenderingEmSize = size;
            FontHintingEmSize = size;
            PixelsPerDip = 1.25;
        }

        public override Typeface Typeface { get; }

        public override double FontRenderingEmSize { get; }

        public override double FontHintingEmSize { get; }

        public override TextDecorationCollection? TextDecorations => null;

        public override Brush ForegroundBrush => Brushes.White;

        public override Brush? BackgroundBrush => null;

        public override CultureInfo CultureInfo => CultureInfo.CurrentUICulture;

        public override TextEffectCollection? TextEffects => null;
    }

    private sealed class GpuTextParagraphProperties(TextRunProperties run) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => FlowDirection.LeftToRight;

        public override TextAlignment TextAlignment => TextAlignment.Left;

        public override double LineHeight => 0;

        public override bool FirstLineInParagraph => true;

        public override TextRunProperties DefaultTextRunProperties => run;

        public override TextWrapping TextWrapping => TextWrapping.NoWrap;

        public override TextMarkerProperties? TextMarkerProperties => null;

        public override double Indent => 0;
    }
}
