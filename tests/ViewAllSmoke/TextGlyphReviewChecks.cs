using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// What the review found in the names' text and glyphs, and what was done
/// about it: the shaping worker wakes every canvas it shaped for, a soft
/// hyphen and a line break in a name draw as Explorer draws them, a glyph too
/// big for the largest tier keeps a smaller one rather than vanishing, and
/// the glyph cache neither keeps the room of glyphs it cannot name again nor
/// leaves files of old fonts behind.  On the canvas: a file's size or date
/// still being made never lends the name room it takes back a frame later,
/// names asked of the text worker before a DPI change are asked again at the
/// new scale, WPF's trimmed names stay drawn through a zoom past the frame's
/// allowance for text, and a folder's redraw draws the names again on the
/// GPU when another canvas on the card has grown the atlases they sample.
/// </summary>
internal static partial class Program
{
    private static Task TextGlyphReviewChecks()
    {
        Section("text and glyph review");
        var faces = FaceRegistry.Shared;
        TextGlyphShaperWakeChecks(faces);
        TextGlyphSoftHyphenChecks(faces);
        TextGlyphLineBreakChecks(faces);
        TextGlyphTooBigChecks(faces);
        TextGlyphCacheRoomChecks(faces);
        TextGlyphCacheFilesChecks(faces);
        RunOnSta("text review: a size still being made", TextGlyphPendingDetailChecksAsync);
        RunOnSta("text review: a DPI change while the text worker works", TextGlyphDpiAsksChecksAsync);
        RunOnSta("text review: trimmed names through a zoom", TextGlyphZoomTrimChecksAsync);
        RunOnSta("text review: an atlas grown by another canvas", TextGlyphAtlasGrownChecksAsync);
        return Task.CompletedTask;
    }

    // ---- the shaping worker wakes every canvas ---------------------------------------------------

    /// <summary>
    /// Two canvases each wait for one name the worker shapes, the second's
    /// job queued right behind the first's - a split view, or two windows.
    /// Both must be told their name arrived, or the first one's label stays
    /// blank until something else redraws it.
    /// </summary>
    private static void TextGlyphShaperWakeChecks(FaceRegistry faces)
    {
        const int rounds = 10;
        var woken = 0;
        for (var round = 0; round < rounds; round++)
        {
            var busy = new TextShaper(faces);
            var first = new TextShaper(faces);
            var second = new TextShaper(faces);
            var firstArrived = 0;
            var secondArrived = 0;
            first.Arrived += () => Interlocked.Increment(ref firstArrived);
            second.Arrived += () => Interlocked.Increment(ref secondArrived);

            // A long job keeps the worker busy while the two single names are
            // queued behind it, the second right behind the first.
            busy.Prefetch(Enumerable.Range(0, 600).Select(index => TextGlyphCjk(round * 1000 + index, 6)), FaceRegistry.Regular);
            first.TryGet(TextGlyphCjk(round * 7 + 50_000, 4), FaceRegistry.Regular, out _);
            second.TryGet(TextGlyphCjk(round * 7 + 60_000, 4), FaceRegistry.Regular, out _);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (Volatile.Read(ref secondArrived) == 0 && clock.ElapsedMilliseconds < 15_000)
            {
                Thread.Sleep(2);
            }

            first.Drain();
            woken += Volatile.Read(ref firstArrived) > 0 && first.PendingCount == 0 ? 1 : 0;
        }

        Check($"text review: a canvas whose one name is shaped just before another canvas's is told it arrived ({woken} of {rounds} rounds)", woken == rounds);
    }

    /// <summary>A name of <paramref name="length"/> CJK ideographs, different for every <paramref name="seed"/>: the UI font has none, so the worker shapes it.</summary>
    private static string TextGlyphCjk(int seed, int length)
    {
        var characters = new char[length];
        for (var index = 0; index < length; index++)
        {
            characters[index] = (char)(0x4E00 + (seed * length + index) % 0x5000);
        }

        return new string(characters);
    }

    // ---- a soft hyphen in a name -----------------------------------------------------------------

    /// <summary>
    /// A soft hyphen is a place a word may be broken, not a character: WPF,
    /// DirectWrite's layout and Explorer show "Präsentation", not
    /// "Prä-sentation".  The GPU's name must be as wide as the name without it
    /// and draw no hyphen.
    /// </summary>
    private static void TextGlyphSoftHyphenChecks(FaceRegistry faces)
    {
        var shaper = new TextShaper(faces);
        var plain = TextGlyphShape(shaper, "Präsentation.pptx");
        var soft = TextGlyphShape(shaper, "Prä\u00ADsentation.pptx");
        var hyphens = faces[FaceRegistry.Regular].Face.GetGlyphIndices([(uint)'-', 0x2010u, 0x00ADu]);
        var hyphenGlyph = false;
        if (soft is not null)
        {
            for (var index = 0; index < soft.Count; index++)
            {
                var advance = soft.PrefixAdvance[index + 1] - soft.PrefixAdvance[index];
                hyphenGlyph |= soft.Faces[index] == FaceRegistry.Regular && hyphens.Contains(soft.Glyphs[index]) && hyphens[0] != 0 && advance > 0.01f;
            }
        }

        Check($"text review: a soft hyphen in a name draws nothing and takes no room ({soft?.Width:F3} em against {plain?.Width:F3} without it, hyphen drawn: {hyphenGlyph})",
            soft is not null && plain is not null && Math.Abs(soft.Width - plain.Width) < 0.01f && !hyphenGlyph);
    }

    /// <summary>The name shaped by <paramref name="shaper"/>, waiting for the worker when it goes there; null when it never came back.</summary>
    private static ShapedText? TextGlyphShape(TextShaper shaper, string text)
    {
        if (shaper.TryGet(text, FaceRegistry.Regular, out var shaped))
        {
            return shaped;
        }

        return shaper.WaitForPending(TimeSpan.FromSeconds(10)) && shaper.TryGet(text, FaceRegistry.Regular, out shaped) ? shaped : null;
    }

    // ---- a line break in a name ------------------------------------------------------------------

    /// <summary>
    /// U+0085 and U+2028/2029 may be in a file name.  DirectWrite's layout
    /// breaks the line there, and a name is one line: what comes after the
    /// break must follow what comes before it, not be drawn over its start.
    /// </summary>
    private static void TextGlyphLineBreakChecks(FaceRegistry faces)
    {
        var capture = new TextLayoutCapture(faces);
        var reference = capture.Shape("Song title .mp3", FaceRegistry.Regular, "en-us");
        foreach (var breaking in new[] { '\u0085', '\u2028', '\u2029' })
        {
            var shaped = capture.Shape($"Song title{breaking}.mp3", FaceRegistry.Regular, "en-us");
            var stacked = 0;
            if (shaped is not null)
            {
                // The last four glyphs are ".mp3": each must start right of the one before.
                for (var index = Math.Max(1, shaped.Count - 4); index < shaped.Count; index++)
                {
                    stacked += shaped.PrefixAdvance[index] <= shaped.PrefixAdvance[index - 1] + 0.01f ? 1 : 0;
                }
            }

            Check($"text review: a name with U+{(int)breaking:X4} stays on one line ({shaped?.Width:F3} em against {reference?.Width:F3} with a space, {stacked} glyphs drawn over others)",
                shaped is not null && reference is not null && stacked == 0 && Math.Abs(shaped.Width - reference.Width) < 0.05f);
        }
    }

    // ---- a glyph too big for the largest tier ----------------------------------------------------

    /// <summary>
    /// A glyph wider than four em - a few ornate ligatures and cuneiform
    /// signs - has no field at the 64 px tier, but has one at 32 px.  Drawn
    /// bigger than 28 px it must keep the 32 px field, as a glyph whose
    /// field is still being made does, rather than vanish.
    /// </summary>
    private static void TextGlyphTooBigChecks(FaceRegistry faces)
    {
        var capture = new TextLayoutCapture(faces);
        ShapedText? found = null;
        var description = string.Empty;
        foreach (var candidate in new[] { "\uFDFD", "\U0001242B", "\U00012219", "\U0001241D", "\uFDFA", "\u0BF5", "\uA9C1" })
        {
            var shaped = capture.Shape(candidate, FaceRegistry.Regular, "en-us");
            if (shaped is not { Count: 1 })
            {
                continue;
            }

            var info = faces[shaped.Faces[0]];
            var metrics = new Vortice.DirectWrite.GlyphMetrics[1];
            info.Face.GetDesignGlyphMetrics([shaped.Glyphs[0]], metrics, false);
            var inkWidth = ((int)metrics[0].AdvanceWidth - metrics[0].LeftSideBearing - metrics[0].RightSideBearing) / info.UnitsPerEm;
            var inkHeight = ((int)metrics[0].AdvanceHeight - metrics[0].TopSideBearing - metrics[0].BottomSideBearing) / info.UnitsPerEm;
            var side = Math.Max(inkWidth, inkHeight);
            if (side * GlyphAtlas.Tiers[2].Em > GlyphRasterizer.MaximumSide + 2 && side * GlyphAtlas.Tiers[1].Em < GlyphRasterizer.MaximumSide - 8)
            {
                found = shaped;
                description = $"U+{char.ConvertToUtf32(candidate, 0):X4} in {info.FamilyName}, {side:F2} em";
                break;
            }
        }

        if (found is null)
        {
            Console.WriteLine("  (no glyph between four and eight em found on this machine: skipped)");
            return;
        }

        using var atlas = new GlyphAtlas(faces);
        var face = found.Faces[0];
        var glyph = found.Glyphs[0];
        atlas.TryGet(face, glyph, 1, out _);
        atlas.WaitForPending(TimeSpan.FromSeconds(10));
        var middle = new TextGlyphCountingSink();
        atlas.Emit(ref middle, found, found.Whole, 10, 40, 20, 0xFFFFFFFF, 0, snap: false);

        // Drawn at 40 px: the 64 px field is asked for and found too big.
        var large = new TextGlyphCountingSink();
        atlas.Emit(ref large, found, found.Whole, 10, 80, 40, 0xFFFFFFFF, 0, snap: false);
        atlas.WaitForPending(TimeSpan.FromSeconds(10));
        large = new TextGlyphCountingSink();
        var missing = atlas.Emit(ref large, found, found.Whole, 10, 80, 40, 0xFFFFFFFF, 0, snap: false);
        Check($"text review: a glyph too big for the 64 px tier ({description}) is drawn from the 32 px one above 28 px ({middle.Quads} quad at 20 px, {large.Quads} at 40 px, {missing} still missing)",
            middle.Quads == 1 && large.Quads == 1 && missing == 0);
    }

    /// <summary>Counts the quads the atlas emits.</summary>
    private struct TextGlyphCountingSink : IGlyphSink
    {
        public int Quads;

        public void Add(in GlyphQuad quad) => Quads++;
    }

    // ---- the glyph cache keeps only what it can name again ---------------------------------------

    /// <summary>
    /// Runs that each meet names in CJK, whose glyphs come from fallback
    /// fonts: the cache file names only the three fixed fonts' glyphs, so it
    /// must not keep the room the others took either - or every run adds
    /// their room again, the atlas fills its sixteen pages over a few runs,
    /// and new glyphs stop drawing for good.  The glyphs it does keep must
    /// read back texel for texel.  And a file written before this, already
    /// mostly room nothing names, is made again rather than read.
    /// </summary>
    private static void TextGlyphCacheRoomChecks(FaceRegistry faces)
    {
        var folder = Path.Combine(Path.GetTempPath(), "UltraExplorerGlyphRoom-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(folder, $"glyphs-v{GlyphAtlas.FormatVersion}-{faces.FontHash}.bin");
            using var first = new GlyphAtlas(faces);
            var written = first.WarmUp(path);
            var freshBytes = new FileInfo(path).Length;

            // How much of a file's shelves its glyphs fill when every glyph is
            // named: far more than the third below which a file is made again.
            var snapshot = first.Snapshot();
            var shelved = snapshot.Shelves.Sum(page => page.Sum(shelf => (long)shelf.Used * shelf.Height));
            var named = snapshot.Entries.Sum(item => item.Entry.Width == 0 ? 0 : (long)(item.Entry.Width + 1) * (item.Entry.Height + 1));
            var filled = (double)named / Math.Max(1, shelved);
            Check($"text review: the glyphs of a fresh cache fill {filled:P0} of its shelves", filled > 0.5);

            // The CJK glyphs a run meets: their fallback font's ids are this
            // process's, so the file cannot name them.
            var capture = new TextLayoutCapture(faces);
            var cjk = capture.Shape(TextGlyphCjk(7, 240), FaceRegistry.Regular, "en-us");
            var sizes = new List<long>();
            var pages = new List<int>();
            for (var run = 0; run < 4; run++)
            {
                using var atlas = new GlyphAtlas(faces);
                atlas.WarmUp(path);
                for (var index = 0; cjk is not null && index < cjk.Count; index++)
                {
                    for (var tier = 0; tier < GlyphAtlas.TierCount; tier++)
                    {
                        atlas.TryGet(cjk.Faces[index], cjk.Glyphs[index], tier, out _);
                    }
                }

                atlas.WaitForPending(TimeSpan.FromSeconds(30));
                GlyphCacheFile.TrySave(atlas, path);
                sizes.Add(new FileInfo(path).Length);
                pages.Add(atlas.PageCount);
            }

            Check($"text review: runs that meet CJK names leave the glyph cache its size ({freshBytes / 1024} KB fresh, then {string.Join(", ", sizes.Select(size => size / 1024))} KB; pages {string.Join(", ", pages)})",
                cjk is { Count: > 200 } && cjk.Faces.All(face => face >= FaceRegistry.FirstFallback) && !written.FromCache
                && sizes.Max() - sizes.Min() <= GlyphAtlas.PageSize * 32 && sizes[0] <= freshBytes * 1.15 && pages.All(count => count == pages[0]));

            using var last = new GlyphAtlas(faces);
            var read = last.WarmUp(path);
            var keys = first.WarmItems();
            var same = read.FromCache && keys.Count > 3000;
            foreach (var key in keys)
            {
                same &= first.TryGet(key.Face, key.Glyph, key.Tier, out var a) & last.TryGet(key.Face, key.Glyph, key.Tier, out var b)
                    && TextGlyphTexelsEqual(first, a, last, b);
            }

            Check($"text review: and every glyph it keeps reads back texel for texel ({keys.Count} glyphs, from the cache: {read.FromCache})", same);

            // A file as the old cache wrote it after many such runs: the fixed
            // fonts' glyphs on a page or two, and pages of room nothing names.
            TextGlyphPadCache(path, extraPages: 6);
            using var bloated = new GlyphAtlas(faces);
            var bloatedRead = bloated.WarmUp(path);
            using var after = new GlyphAtlas(faces);
            var afterRead = after.WarmUp(path);
            Check($"text review: a cache file mostly of room no glyph names is made again, not read ({(bloatedRead.FromCache ? "read" : "made again")}, {bloated.PageCount} pages; next start {(afterRead.FromCache ? "read" : "made again")}, {after.PageCount} pages)",
                !bloatedRead.FromCache && bloated.PageCount <= first.PageCount && afterRead.FromCache && after.PageCount == first.PageCount);
        }
        finally
        {
            TextGlyphDelete(folder);
        }
    }

    /// <summary>Whether two entries hold the same texels, each on its own atlas's pages.</summary>
    private static bool TextGlyphTexelsEqual(GlyphAtlas a, GlyphEntry first, GlyphAtlas b, GlyphEntry second)
    {
        if (first.Width != second.Width || first.Height != second.Height || first.Left != second.Left || first.Top != second.Top
            || first.Right != second.Right || first.Bottom != second.Bottom)
        {
            return false;
        }

        var left = new byte[first.Width];
        var right = new byte[second.Width];
        for (var row = 0; row < first.Height; row++)
        {
            System.Runtime.InteropServices.Marshal.Copy(a.PagePointer(first.Page) + (nint)((long)(first.Y + row) * GlyphAtlas.PageSize + first.X), left, 0, left.Length);
            System.Runtime.InteropServices.Marshal.Copy(b.PagePointer(second.Page) + (nint)((long)(second.Y + row) * GlyphAtlas.PageSize + second.X), right, 0, right.Length);
            if (!left.AsSpan().SequenceEqual(right))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Rewrites the cache file at <paramref name="path"/> with
    /// <paramref name="extraPages"/> more pages, each one full shelf of
    /// texels no entry names - what the old cache kept of fallback fonts'
    /// glyphs - and the hash made again, so the file is valid but bloated.
    /// </summary>
    private static void TextGlyphPadCache(string path, int extraPages)
    {
        var file = File.ReadAllBytes(path);
        using var reader = new BinaryReader(new MemoryStream(file, 0, file.Length - 32), Encoding.UTF8);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write(reader.ReadUInt32());
        writer.Write(reader.ReadInt32());
        writer.Write(reader.ReadInt32());
        writer.Write(reader.ReadString());
        writer.Write(reader.ReadInt32());
        var tiers = reader.ReadInt32();
        writer.Write(tiers);
        for (var tier = 0; tier < tiers * 2; tier++)
        {
            writer.Write(reader.ReadInt32());
        }

        var pageCount = reader.ReadInt32();
        writer.Write(pageCount + extraPages);
        var heights = new int[pageCount];
        for (var page = 0; page < pageCount; page++)
        {
            heights[page] = reader.ReadInt32();
            var shelves = reader.ReadInt32();
            writer.Write(heights[page]);
            writer.Write(shelves);
            for (var shelf = 0; shelf < shelves * 3; shelf++)
            {
                writer.Write(reader.ReadInt32());
            }
        }

        for (var page = 0; page < extraPages; page++)
        {
            writer.Write(GlyphAtlas.PageSize);
            writer.Write(1);
            writer.Write(0);
            writer.Write(GlyphAtlas.PageSize);
            writer.Write(GlyphAtlas.PageSize);
        }

        var entries = reader.ReadInt32();
        writer.Write(entries);
        writer.Write(reader.ReadBytes(entries * (1 + 2 + 1 + 2 * 5 + 4 * 4)));
        writer.Write(reader.ReadBytes((int)(reader.BaseStream.Length - reader.BaseStream.Position)));
        var noise = new byte[GlyphAtlas.PageSize * GlyphAtlas.PageSize];
        for (var index = 0; index < noise.Length; index += 97)
        {
            noise[index] = 200;
        }

        for (var page = 0; page < extraPages; page++)
        {
            writer.Write(noise);
        }

        writer.Flush();
        var payload = output.ToArray();
        File.WriteAllBytes(path, [.. payload, .. SHA256.HashData(payload)]);
    }

    private static void TextGlyphDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ---- glyph cache files of old fonts ----------------------------------------------------------

    /// <summary>
    /// Every font or format update names a new cache file.  Once the
    /// current one has been read or written, the others - and any temporary
    /// file a crash left - go, and nothing else in the folder is touched.
    /// </summary>
    private static void TextGlyphCacheFilesChecks(FaceRegistry faces)
    {
        var folder = Path.Combine(Path.GetTempPath(), "UltraExplorerGlyphFiles-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"glyphs-v{GlyphAtlas.FormatVersion}-{faces.FontHash}.bin");
            string[] old = ["glyphs-v1-0123456789abcdef.bin", "glyphs-v1-0123456789abcdef.bin.tmp", "glyphs-v0-fedcba9876543210.bin"];
            string[] kept = ["icons-v1.bin", "shaders-v3.bin", "glyphs-notes.txt"];
            foreach (var name in old.Concat(kept))
            {
                File.WriteAllBytes(Path.Combine(folder, name), [1, 2, 3]);
            }

            using (var made = new GlyphAtlas(faces))
            {
                made.WarmUp(path);
            }

            var leftAfterSave = old.Count(name => File.Exists(Path.Combine(folder, name)));
            foreach (var name in old)
            {
                File.WriteAllBytes(Path.Combine(folder, name), [1, 2, 3]);
            }

            using (var read = new GlyphAtlas(faces))
            {
                read.WarmUp(path);
            }

            var leftAfterLoad = old.Count(name => File.Exists(Path.Combine(folder, name)));
            Check($"text review: glyph cache files of other fonts and versions go once the current one is written ({leftAfterSave} of {old.Length} left) or read ({leftAfterLoad} left), and nothing else does",
                leftAfterSave == 0 && leftAfterLoad == 0 && File.Exists(path) && kept.All(name => File.Exists(Path.Combine(folder, name))));
        }
        finally
        {
            TextGlyphDelete(folder);
        }
    }

    // ---- a size or date still being made ---------------------------------------------------------

    /// <summary>
    /// A file's label whose size or date the target has not made yet - the
    /// frame's budget for new text spent - and the same label a frame later
    /// with it.  Over a sweep of tile widths, by size and by date, the name
    /// must never have less room once the detail arrives than it had while it
    /// was missing: no name drawn longer for a frame, then cut back.
    /// </summary>
    private static async Task TextGlyphPendingDetailChecksAsync()
    {
        Section("text review: a size or date still being made");
        var disk = new FakeDisk();
        string[] names = ["TULA0057-LAV-field-recording.WAV", "a-very-long-recording-name-that-must-stay-readable.wav", "short.wav", "Zapis-dlinnoe-imya-zvuka-2024.wav"];
        foreach (var name in names)
        {
            disk.AddFile(@"Q:\audio", name, 123_456_789, modified: new DateTime(2024, 9, 2, 13, 50, 0, DateTimeKind.Utc));
        }

        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive, "1 TB free")]);
        await LoadEverythingAsync(tree, _ => true);
        var folder = tree.Find(@"Q:\audio")!;
        var canvas = new NestedCanvas { Tree = tree, DpiOverride = new DpiScale(1, 1) };
        var previousCulture = CultureInfo.CurrentCulture;
        var cases = 0;
        var cutBack = 0;
        string? first = null;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            foreach (var column in new[] { SortColumn.Name, SortColumn.Modified })
            {
                tree.Orders.SetFolder(folder.FullPath, new ItemSort(column, true));
                foreach (var name in names)
                {
                    var index = Enumerable.Range(0, folder.Files.Count).First(place => folder.Files[place].Name == name);
                    for (var width = 100.0; width <= 480; width += 2.5)
                    {
                        var pending = new TextGlyphDetailTarget(name, detailReady: false);
                        canvas.DrawFileLabelForTests(pending, folder, index, width, 40);
                        var ready = new TextGlyphDetailTarget(name, detailReady: true);
                        canvas.DrawFileLabelForTests(ready, folder, index, width, 40);
                        cases++;
                        if (ready.EffectiveRoom + 1e-7 < pending.EffectiveRoom)
                        {
                            cutBack++;
                            first ??= $"{name} by {column} at {width} DIPs: {pending.EffectiveRoom:F1} then {ready.EffectiveRoom:F1}";
                        }
                    }
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            canvas.Tree = null;
        }

        Check($"text review: a name never loses room when its size or date arrives a frame late ({cutBack} of {cases} layouts cut back{(first is null ? string.Empty : $"; first {first}")})",
            cases > 500 && cutBack == 0);
    }

    /// <summary>
    /// Lays texts out with WPF, as the canvas's own target would, except
    /// that every text but the file's name is not ready yet when
    /// <paramref name="detailReady"/> is false - the empty text a target hands
    /// back past its budget.  Notes the room the name was given.
    /// </summary>
    private sealed class TextGlyphDetailTarget(string filename, bool detailReady) : LabelTarget
    {
        private readonly Dictionary<object, double> _rooms = new(ReferenceEqualityComparer.Instance);
        private double _natural;
        private double _room = double.NaN;

        /// <summary>What the name could show: its own width, or less when it was given less room.</summary>
        public double EffectiveRoom => double.IsNaN(_room) ? 0 : Math.Min(_natural, _room);

        public override LabelText Text(string text, double size, Color ink, double maxWidth, LabelFace face, bool scaled)
        {
            if (text != filename && !detailReady)
            {
                return default;
            }

            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(face == LabelFace.Icons ? "Segoe Fluent Icons" : "Segoe UI Variable Text"), size, Brushes.White, 1)
            { MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            if (text == filename)
            {
                _natural = formatted.Width;
            }

            if (maxWidth < 10_000)
            {
                formatted.MaxTextWidth = Math.Max(1, maxWidth);
            }

            _rooms[formatted] = maxWidth;
            return new LabelText(formatted, formatted.Width, formatted.Height);
        }

        public override void DrawText(in LabelText text, Point origin)
        {
            if (text.Handle is FormattedText formatted && _rooms.TryGetValue(formatted, out var room) && formatted.Text == filename)
            {
                _room = room;
            }
        }

        public override void FillRect(Rect bounds, Color colour)
        {
        }

        public override void FillRounded(Rect bounds, double radius, Color colour)
        {
        }

        public override bool DrawIcon(NestedFolder folder, int fileIndex, in NestedFile file, Rect bounds) => false;
    }

    // ---- a DPI change while the text worker works ------------------------------------------------

    /// <summary>
    /// The 4K folder of 1,800 names, every one laid out at 100 %; the window
    /// goes to a monitor of 150 % just as a zoom ends, so the frame that
    /// settles it makes no text and asks the text worker for every name -
    /// queued behind another pane's, which keeps the worker busy.  Before
    /// any of them is back the window goes on to a monitor of 125 %.  What
    /// the worker makes at 150 % is no use at 125 % and is dropped - so the
    /// names must be asked again at 125 % and drawn, with nothing but time
    /// between frames, not left out until something unrelated draws them.
    /// </summary>
    private static async Task TextGlyphDpiAsksChecksAsync()
    {
        Section("text review: a DPI change while the text worker works");
        var disk = new FakeDisk();
        for (var index = 0; index < 1_800; index++)
        {
            disk.AddFile(@"Q:\many", $"file-{index:D4}.dat", 1_000L * index + 7);
            disk.AddFile(@"Q:\other", $"other-{index:D4}.dat", 1_000L * index + 7);
        }

        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive, "1 TB free")]);
        await LoadEverythingAsync(tree, _ => true);
        var busy = LabelCostCanvas(tree, tree.Find(@"Q:\other")!, static (_, _) => null);
        var canvas = LabelCostCanvas(tree, tree.Find(@"Q:\many")!, static (_, _) => null);
        try
        {
            // Long enough after the camera was placed that no frame is drawn for motion.
            Thread.Sleep(200);
            canvas.DpiOverride = new DpiScale(1, 1);
            canvas.RenderLabelsForTests(inMotion: false, withScene: true);
            LabelCostSettle(canvas, limit: 400);
            var settling = System.Diagnostics.Stopwatch.StartNew();
            while ((canvas.TextsAskedOfWorker > 0 || canvas.LabelsLeftWaiting) && settling.ElapsedMilliseconds < 10_000)
            {
                Thread.Sleep(5);
                canvas.RenderLabelsForTests(inMotion: false);
            }

            // A frame drawn for motion, every name found laid out; then the
            // other pane hands the worker its names, and the settle at 150 %
            // asks it for every one of this pane's.
            canvas.RenderLabelsForTests(inMotion: true);
            busy.FramesByHandForTests = true;
            var time = TimeSpan.FromSeconds(2_000);
            busy.RunFrameForTests(time);
            canvas.DpiOverride = new DpiScale(1.5, 1.5);
            canvas.RenderLabelsForTests(inMotion: false);
            var asked = canvas.TextsAskedOfWorker;

            canvas.DpiOverride = new DpiScale(1.25, 1.25);
            canvas.Redraw();
            canvas.FramesByHandForTests = true;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var done = false;
            while (clock.ElapsedMilliseconds < 8_000)
            {
                time += TimeSpan.FromMilliseconds(1_000.0 / 120);
                canvas.RunFrameForTests(time);
                if (!canvas.LabelsLeftWaiting && canvas.TextsAskedOfWorker == 0 && canvas.LastFrameLayers == NestedCanvas.Layers.None)
                {
                    done = true;
                    break;
                }

                Thread.Sleep(8);
            }

            Check($"text review: names asked of the text worker before a DPI change are asked again at the new scale and drawn ({asked} asked at 150 %; after {clock.ElapsedMilliseconds} ms names still waiting: {canvas.LabelsLeftWaiting}, still asked: {canvas.TextsAskedOfWorker})",
                asked > 1_000 && done);
        }
        finally
        {
            busy.Tree = null;
            canvas.Tree = null;
        }
    }

    // ---- WPF's trimmed names through a zoom ------------------------------------------------------

    /// <summary>
    /// The CPU's names in a zoom: file tiles at the largest name size, every
    /// name cut short, so the room of each changes every frame and with it
    /// the layout WPF needs.  Past the frame's allowance for new layouts a
    /// name must still be drawn - from a layout of it already made that fits
    /// - rather than blink out for the frame: always while the room grows,
    /// and while it shrinks until the room is narrower than the last layout
    /// made, which before was most names in nearly every frame.
    /// </summary>
    private static async Task TextGlyphZoomTrimChecksAsync()
    {
        Section("text review: trimmed names through a zoom");
        var disk = new FakeDisk();
        for (var index = 0; index < 240; index++)
        {
            disk.AddFile(@"Q:\takes", $"{index:D4} recording of the morning session in the small hall, take {index}.wav", 1_000L * index + 7);
        }

        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive, "1 TB free")]);
        await LoadEverythingAsync(tree, _ => true);
        var folder = tree.Find(@"Q:\takes")!;
        var canvas = new NestedCanvas { Tree = tree };
        canvas.Measure(new Size(1600, 1000));
        canvas.Arrange(new Rect(0, 0, 1600, 1000));
        canvas.UpdateLayout();
        canvas.DpiOverride = new DpiScale(1, 1);
        var middle = new Point(800, 500);
        try
        {
            canvas.FlyTo(folder, 0.95, animated: false);
            canvas.RenderLabelsForTests(inMotion: false, withScene: true);
            for (var step = 0; step < 40 && canvas.FileLabelHeightForTests < 28; step++)
            {
                canvas.ZoomAt(middle, 1.1);
                canvas.RenderLabelsForTests(inMotion: false, withScene: true);
            }

            // At rest until every name in view is laid out and recorded.
            var settled = LabelCostSettle(canvas, limit: 400);
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while ((canvas.TextsAskedOfWorker > 0 || canvas.LabelsLeftWaiting) && waited.ElapsedMilliseconds < 10_000)
            {
                Thread.Sleep(5);
                canvas.RenderLabelsForTests(inMotion: false);
            }

            var atRest = TextGlyphNamesDrawn(canvas);
            var listed = canvas.FileLabelCount;

            var zoomInMissing = 0;
            var zoomInFrames = 0;
            for (var frame = 0; frame < 30; frame++)
            {
                canvas.ZoomAt(middle, 1.012);
                canvas.RenderLabelsForTests(inMotion: true, withScene: true);
                zoomInMissing += Math.Max(0, canvas.FileLabelCount - TextGlyphNamesDrawn(canvas));
                zoomInFrames++;
            }

            var zoomOutMissing = 0;
            for (var frame = 0; frame < 30; frame++)
            {
                canvas.ZoomAt(middle, 1 / 1.012);
                canvas.RenderLabelsForTests(inMotion: true, withScene: true);
                zoomOutMissing += Math.Max(0, canvas.FileLabelCount - TextGlyphNamesDrawn(canvas));
            }

            Check($"text review: the CPU's trimmed file names stay drawn through a zoom past the frame's allowance ({listed} names on {canvas.FileLabelHeightForTests:F1} DIP tiles, {atRest} drawn at rest after {settled} frames; names missing over {zoomInFrames} frames zooming in {zoomInMissing}, zooming out {zoomOutMissing})",
                listed > 20 && atRest == listed && zoomInMissing == 0 && zoomOutMissing <= listed * 30 / 10);
        }
        finally
        {
            canvas.Tree = null;
        }
    }

    /// <summary>
    /// How many file names WPF's label layer draws: the glyph runs on it,
    /// kept runs and all, whose text starts with a file's four-digit number.
    /// </summary>
    private static int TextGlyphNamesDrawn(NestedCanvas canvas)
    {
        var numbers = new HashSet<string>(StringComparer.Ordinal);
        void Walk(Drawing? drawing)
        {
            switch (drawing)
            {
                case DrawingGroup group:
                    foreach (var child in group.Children)
                    {
                        Walk(child);
                    }

                    break;
                case GlyphRunDrawing { GlyphRun.Characters: { Count: >= 4 } characters }:
                    var start = new string([characters[0], characters[1], characters[2], characters[3]]);
                    if (start.All(char.IsDigit))
                    {
                        numbers.Add(start);
                    }

                    break;
            }
        }

        void Visit(Visual visual)
        {
            Walk(VisualTreeHelper.GetDrawing(visual));
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(visual); index++)
            {
                if (VisualTreeHelper.GetChild(visual, index) is Visual child)
                {
                    Visit(child);
                }
            }
        }

        Visit(canvas.LabelLayer);
        return numbers.Count;
    }

    // ---- an atlas grown by another canvas --------------------------------------------------------

    /// <summary>
    /// A canvas in a window - cloaked, never activated, on the secondary
    /// monitor - drawing its cells and names on the GPU.  The icon array its
    /// names sample, shared by every canvas on the card, is then grown as
    /// another pane's or window's frame grows it, which lets the old view go.
    /// The next frame here is only a folder's redraw, which draws no names of
    /// its own: it must draw them again with the array as it is now, or its
    /// picture is presented with nothing bound and every icon and name on it
    /// vanishes.
    /// </summary>
    private static async Task TextGlyphAtlasGrownChecksAsync()
    {
        Section("text review: an atlas another canvas grew");
        if (TestScreen.Target() is not { } target)
        {
            Console.WriteLine("  (no secondary monitor: skipped)");
            return;
        }

        var disk = new FakeDisk();
        for (var index = 0; index < 120; index++)
        {
            disk.Folder($@"Q:\big\s{index:D3}\a");
            disk.AddFiles($@"Q:\big\s{index:D3}", 4, "f");
        }

        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await tree.LoadAsync(tree.Find(@"Q:\")!);
        await tree.LoadAsync(tree.Find(@"Q:\big")!);

        if (!GpuBootstrap.IsStarted)
        {
            GpuLabelAtlases.RegisterWarmUp();
            NestedGpuRenderer.RegisterWarmUp();
        }

        GpuLabelAtlases.StartWarmUp();
        var canvas = new NestedCanvas { Tree = tree };
        var window = new Window
        {
            Content = canvas,
            Title = "UltraExplorer text review: an atlas grown by another canvas",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Width = 800,
            Height = 500
        };

        try
        {
            using (UltraExplorer.Picker.Integration.ActivationGuard.GuardWindowsCreated())
            {
                var handle = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                UltraExplorer.Picker.Integration.DialogNative.CloakOwn(handle, true);
                SetWindowPos(handle, 0, target.Work.Left + 40, target.Work.Top + 40, 800, 500, 0x0004 | 0x0010); // no z-order change, no activation
                window.Show();
            }

            canvas.FlyTo(tree.Find(@"Q:\big")!, 0.92, animated: false);
            if (!await Until(() => canvas.IsSceneOnGpu && canvas.AreLabelsOnGpu && canvas.IsIdle, 30_000)
                || GpuBootstrap.Decide(canvas).DeviceSet is not { } devices
                || !devices.TryGetAttached<IconAtlasTexture>(out var icons) || icons is null)
            {
                Console.WriteLine($"  (the GPU did not draw the window's names - {canvas.RendererReason}: skipped)");
                return;
            }

            // A folder's redraw on its own draws no names: what is measured.
            var names = canvas.LabelLayerCount;
            await tree.LoadAsync(tree.Find(@"Q:\big\s004")!);
            var quiet = await Until(() => canvas.IsIdle, 5_000) && canvas.LabelLayerCount == names;
            if (!quiet)
            {
                Console.WriteLine($"  (a folder's redraw drew the names here anyway ({canvas.LastFrameLayers}): skipped)");
                return;
            }

            // Another canvas on the card grows the shared icon array.
            var capacity = icons.Capacity;
            icons.Grow(devices.Context, capacity * 2);
            await tree.LoadAsync(tree.Find(@"Q:\big\s005")!);
            var drawn = await Until(() => canvas.IsIdle && canvas.LabelLayerCount > names, 5_000);
            Check($"text review: a folder's redraw after another canvas grew the shared icon array draws the names again with it ({capacity} slices grown to {icons.Capacity}; names drawn {canvas.LabelLayerCount - names} times, still on the GPU: {canvas.AreLabelsOnGpu})",
                drawn && canvas.AreLabelsOnGpu && canvas.IsSceneOnGpu);
        }
        finally
        {
            window.Close();
            canvas.Tree = null;
        }
    }
}
