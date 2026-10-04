using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The icon and glyph findings of the second review (2026-10-04): the GPU
/// icon atlas settles at rest with more programs on screen than its file
/// region holds (J014), a program denied a slot held for another canvas gets
/// it once the hold is over without anything else drawn (J087), copies of
/// the same programs past the file limit do not each scan every file (J119);
/// a glyph too big for the largest tier gets the middle one even when first
/// drawn large (J122), and a long queue of new glyphs is announced as it is
/// made (J085); the icon service answers a list's rows top to bottom (J016),
/// does not let four hung shortcuts hold up every other icon (J037), gives
/// drives icons of their own (J118) and lets only its oldest per-file icons
/// go when it has too many (J120); and waiting for the D3DImage lock no
/// longer allocates on every poll (J077).
///
/// Headless: the Shell is a stand-in, the GPU surface is never shown, and
/// nothing on disk is read or written.
/// </summary>
internal static partial class Program
{
    private static Task IconGlyphRound2Checks()
    {
        Section("icon and glyph round 2");
        try
        {
            Round2IconRestChecks();
            Round2IconHoldWakeChecks();
            Round2IconTrimChecks();
            Round2GlyphTierChecks(FaceRegistry.Shared);
            Round2GlyphAnnounceChecks(FaceRegistry.Shared);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FATAL {ex}");
            Check("icon and glyph round 2 checks ran to the end", false);
        }

        RunOnSta("icon service round 2 on a dispatcher", async () =>
        {
            await Round2IconOrderChecks();
            await Round2IconDriveKeyChecks();
            Round2IconHungChecks();
        });
        return Task.CompletedTask;
    }

    private static readonly string Round2Root = Path.Combine(Path.GetTempPath(), "UltraExplorerIconRound2");

    // ---- J014: more programs on screen than the file region holds --------------------------

    /// <summary>
    /// 672 programs on screen and 512 file slots.  The canvas draws a frame
    /// whenever the atlas wakes it, as NestedCanvas does.  Once the region is
    /// full of what is on screen the other 160 show their type's icon, and
    /// the atlas must settle: no Shell question and no frame at rest.
    /// </summary>
    private static void Round2IconRestChecks()
    {
        var fake = new IconFakeSource();
        IconFound Shell(IconRequest request)
        {
            // About what the Shell takes for a program: a few milliseconds.
            var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3 / 1000;
            while (Stopwatch.GetTimestamp() < until)
            {
            }

            return fake.Find(request);
        }

        using var atlas = new IconAtlas(new IconAtlasOptions { CachePath = null, AutoSaveInterval = TimeSpan.Zero, Source = Shell });
        using var woken = new AutoResetEvent(false);
        Action canvas = () => woken.Set();
        atlas.ArrivalsPending += canvas;
        var programs = Enumerable.Range(0, 672).Select(index => Path.Combine(Round2Root, "rest", $"tool-{index:D3}.exe")).ToArray();
        var frames = 0;
        void Frame()
        {
            atlas.ProcessArrivals(null, null);
            foreach (var program in programs)
            {
                atlas.SlotFor(program, "exe");
            }

            frames++;
        }

        // A zoom into the folder: frame after frame for a second and a half.
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 1500)
        {
            Frame();
        }

        var moving = frames;

        // At rest: a frame only when woken, and another at once while arrivals wait.
        void Rest(int milliseconds)
        {
            var rest = Stopwatch.StartNew();
            while (rest.ElapsedMilliseconds < milliseconds)
            {
                if (woken.WaitOne(20) || atlas.HasPendingArrivals)
                {
                    Frame();
                }
            }
        }

        Rest(1500);
        var extractions = atlas.ExtractionCount;
        var framesBefore = frames;
        Rest(2000);
        var extra = atlas.ExtractionCount - extractions;
        var framesAtRest = frames - framesBefore;
        var type = atlas.SlotFor("exe");
        var own = programs.Count(program => atlas.SlotFor(program, "exe") is var slot && slot >= 0 && slot != type);
        Console.WriteLine($"  672 programs, 512 file slots: {moving} frames moving; in 2 s at rest {extra} Shell questions and {framesAtRest} frames; {own} with an icon of their own in {atlas.FileSlotCount} file slots");
        Check("J014: with more programs on screen than file slots, the atlas settles at rest: no Shell question and no frame",
            extra == 0 && framesAtRest == 0);
        Check("J014: and the file slots stay full of programs on screen", own == 512 && atlas.FileSlotCount == 512);
    }

    // ---- J087: a slot held for another canvas -------------------------------------------------

    /// <summary>
    /// Two canvases draw from the atlas; one pans from four programs to four
    /// others, whose icons arrive while the first four were drawn a moment
    /// ago - held, since the other canvas might still show them - and then
    /// it rests.  Once the hold is over the four must get icons of their own
    /// without anything else drawing a frame.
    /// </summary>
    private static void Round2IconHoldWakeChecks()
    {
        var source = new IconFakeSource();
        using var atlas = new IconAtlas(new IconAtlasOptions
        {
            CachePath = null,
            AutoSaveInterval = TimeSpan.Zero,
            PerFileCapacity = 4,
            MaximumSlices = 16,
            InitialSlices = 4,
            Source = source.Find
        });
        using var woken = new AutoResetEvent(false);
        Action paneA = () => woken.Set();
        Action paneB = () => { };
        atlas.ArrivalsPending += paneA;
        atlas.ArrivalsPending += paneB;
        var first = Enumerable.Range(1, 4).Select(index => Path.Combine(Round2Root, "hold", $"first-{index}.exe")).ToArray();
        var next = Enumerable.Range(1, 4).Select(index => Path.Combine(Round2Root, "hold", $"next-{index}.exe")).ToArray();
        void Frame(string[] programs)
        {
            atlas.ProcessArrivals(null, null);
            foreach (var program in programs)
            {
                atlas.SlotFor(program, "exe");
            }
        }

        int Own(string[] programs)
        {
            var type = atlas.SlotFor("exe");
            return programs.Count(program => atlas.SlotFor(program, "exe") is var slot && slot >= 0 && slot != type);
        }

        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 5000 && Own(first) < 4)
        {
            Frame(first);
            Thread.Sleep(1);
        }

        var firstOwn = Own(first);
        Frame(first);

        // The pan: the next four only, until their answers are all in.
        clock.Restart();
        do
        {
            Frame(next);
            Thread.Sleep(1);
        }
        while (clock.ElapsedMilliseconds < 5000 && (atlas.PendingKeyCount > 0 || atlas.HasPendingArrivals));

        var held = next.Length - Own(next);

        // At rest the canvas draws only when woken.
        clock.Restart();
        while (clock.ElapsedMilliseconds < 2500)
        {
            if (woken.WaitOne(20) || atlas.HasPendingArrivals)
            {
                Frame(next);
            }
        }

        var own = Own(next);
        Console.WriteLine($"  two canvases, a pan from four programs to four others: {held} held after the pan, {own} with icons of their own 2.5 s into the rest");
        Check("J087: a program denied a slot held for another canvas gets one once the hold is over, with nothing else drawn",
            firstOwn == 4 && held == 4 && own == 4);
    }

    // ---- J119: copies of the same programs past the file limit ---------------------------------

    /// <summary>
    /// Ten thousand copies of one program, each in a folder of its own: one
    /// picture, so one slot every copy shows, and nothing the trim of files
    /// could forget.  Past the limit of 8,192 files a new copy must cost
    /// what one cost below it, not a scan of every file.
    /// </summary>
    private static void Round2IconTrimChecks()
    {
        var source = new IconFakeSource();
        using var atlas = new IconAtlas(new IconAtlasOptions { CachePath = null, AutoSaveInterval = TimeSpan.Zero, Source = source.Find });
        string Copy(int index) => Path.Combine(Round2Root, "copies", $"copy-{index:D5}", "same-tool.exe");

        var below = TimeSpan.Zero;
        for (var start = 0; start < 8192; start += 512)
        {
            var asking = Stopwatch.StartNew();
            for (var index = start; index < start + 512; index++)
            {
                atlas.SlotFor(Copy(index), "exe");
            }

            if (start < 2048)
            {
                below += asking.Elapsed;
            }

            IconSettle(atlas, null, null, () => atlas.PendingKeyCount == 0);
        }

        var shared = atlas.FileSlotCount;
        var past = Stopwatch.StartNew();
        for (var index = 8192; index < 8192 + 2048; index++)
        {
            atlas.SlotFor(Copy(index), "exe");
        }

        past.Stop();
        Console.WriteLine($"  copies of one program in {shared} file slot: 2,048 new copies took {below.TotalMilliseconds:F1} ms below the file limit, {past.Elapsed.TotalMilliseconds:F1} ms past it");
        Check("J119: past the file limit, copies of programs that share a slot cost what they cost below it",
            shared == 1 && past.Elapsed.TotalMilliseconds < below.TotalMilliseconds * 4 + 10);
    }

    // ---- J122: a glyph too big for the largest tier, first drawn large --------------------------

    /// <summary>
    /// A glyph wider than four em first drawn at 40 px: its 64 px field is
    /// too big, and with no 32 px field asked for before, the atlas must ask
    /// for that one rather than leave the glyph out for good.
    /// </summary>
    private static void Round2GlyphTierChecks(FaceRegistry faces)
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
        var quads = new List<int>();
        var missing = new List<int>();
        for (var emit = 0; emit < 3; emit++)
        {
            var sink = new TextGlyphCountingSink();
            missing.Add(atlas.Emit(ref sink, found, found.Whole, 10, 80, 40, 0xFFFFFFFF, 0, snap: false));
            quads.Add(sink.Quads);
            atlas.WaitForPending(TimeSpan.FromSeconds(10));
        }

        Console.WriteLine($"  {description}, first drawn at 40 px: quads {string.Join(", ", quads)}, missing {string.Join(", ", missing)} over three frames");
        Check("J122: a glyph too big for the 64 px tier, first drawn above 28 px, is drawn from the 32 px one and counted missing until then",
            quads[^1] == 1 && missing[^1] == 0 && missing[1] > 0);
    }

    // ---- J085: new glyphs announced as they are made --------------------------------------------

    /// <summary>
    /// Three thousand glyphs not made yet, asked for at once - the first view
    /// of names in a script the warm set does not hold.  The canvases must
    /// hear of the first ones while the rest are still being made, not only
    /// once the whole queue is done.
    /// </summary>
    private static void Round2GlyphAnnounceChecks(FaceRegistry faces)
    {
        using var atlas = new GlyphAtlas(faces);
        var clock = Stopwatch.StartNew();
        var announcements = 0;
        long firstAt = -1;
        var pendingAtFirst = -1;
        atlas.GlyphsArrived += () =>
        {
            if (Interlocked.Increment(ref announcements) == 1)
            {
                firstAt = clock.ElapsedMilliseconds;
                pendingAtFirst = atlas.PendingCount;
            }
        };

        var asked = 0;
        foreach (var face in new[] { FaceRegistry.Regular, FaceRegistry.SemiBold })
        {
            var count = Math.Min(1500, (int)faces[face].GlyphCount - 1);
            for (var glyph = 1; glyph <= count; glyph++)
            {
                atlas.TryGet(face, (ushort)glyph, 2, out _);
                asked++;
            }
        }

        atlas.WaitForPending(TimeSpan.FromSeconds(60));
        var drained = clock.ElapsedMilliseconds;
        Thread.Sleep(100);
        Console.WriteLine($"  {asked} glyphs made in {drained} ms: {announcements} announcement(s), the first at {firstAt} ms with {pendingAtFirst} still queued");
        Check("J085: a long queue of new glyphs is announced as it is made, not only once all are made",
            drained > 100 && pendingAtFirst > 0 && firstAt < drained / 2 && announcements >= 3);
    }

    // ---- J016: a list's rows answered top to bottom ---------------------------------------------

    /// <summary>
    /// The folder list asks for its first rows top to bottom, in one go, and
    /// the Shell's worker is busy meanwhile: the top row must be the first of
    /// them it answers.  A newer list - a search run again - still goes
    /// before an older one, its rows top to bottom too.
    /// </summary>
    private static async Task Round2IconOrderChecks()
    {
        using var gate = new ManualResetEventSlim(false);
        var shell = new FakeShell(".exe") { Gate = gate };
        using var icons = new ShellIconService(shell.Extract, Dispatcher.CurrentDispatcher);
        icons.Request(Path.Combine(Round2Root, "order", "hold.aaa"), false, _ => { });
        IconSpinUntil(() => !shell.Asked.IsEmpty);
        await Task.Yield();

        var answered = 0;
        var rows = Enumerable.Range(0, 300).Select(index => Path.Combine(Round2Root, "order", "list", $"tool{index:D3}.exe")).ToArray();
        foreach (var row in rows)
        {
            icons.Request(row, false, _ => answered++);
        }

        await Task.Yield();
        var newer = new[] { "found-a.exe", "found-b.exe", "found-c.exe" }.Select(name => Path.Combine(Round2Root, "order", "search", name)).ToArray();
        foreach (var row in newer)
        {
            icons.Request(row, false, _ => answered++);
        }

        gate.Set();
        var done = await IconWaitUntilAsync(() => answered == rows.Length + newer.Length);
        var asked = shell.Asked.Skip(1).ToArray();
        var expected = newer.Concat(rows).ToArray();
        var topRank = Array.IndexOf(asked, rows[0]);
        Console.WriteLine($"  300 rows asked top to bottom, then 3 of a newer list: the top row was the Shell's question {topRank + 1} of {asked.Length}; the first three asked: {string.Join(", ", asked.Take(3).Select(Path.GetFileName))}");
        Check("J016: a list's rows are answered top to bottom, the top row first of them", done && topRank == newer.Length && asked.Skip(newer.Length).SequenceEqual(rows, StringComparer.OrdinalIgnoreCase));
        Check("J016: and a newer list's rows go before an older one's, top to bottom too", done && asked.Take(newer.Length).SequenceEqual(newer, StringComparer.OrdinalIgnoreCase));
    }

    // ---- J118: drives have icons of their own ---------------------------------------------------

    /// <summary>
    /// Two drives, a folder and a WSL distribution: each drive shows its own
    /// icon, not the folder's.  The navigation pane, which asks on the UI
    /// thread, draws a drive with its glyph rather than ask the Shell about it
    /// there - a drive mapped to a server that is off can take the network's
    /// timeout to answer.
    /// </summary>
    private static async Task Round2IconDriveKeyChecks()
    {
        var asked = new ConcurrentQueue<string>();
        ImageSource? Extract(string path, bool isDirectory)
        {
            asked.Enqueue(path);
            return FakeShell.Picture(StringComparer.OrdinalIgnoreCase.GetHashCode(path));
        }

        using var icons = new ShellIconService(Extract, Dispatcher.CurrentDispatcher);
        ImageSource? q = null, r = null, folder = null, wsl = null;
        icons.Request(@"Q:\", true, icon => q = icon);
        icons.Request(@"R:\", true, icon => r = icon);
        icons.Request(@"Q:\folder", true, icon => folder = icon);
        icons.Request(@"\\wsl$\Ubuntu", true, icon => wsl = icon);
        var done = await IconWaitUntilAsync(() => q is not null && r is not null && folder is not null && wsl is not null);
        Console.WriteLine($"  drives Q: and R:, a folder and \\\\wsl$\\Ubuntu: the Shell was asked about {string.Join(", ", asked)}");
        Check("J118: each drive and WSL distribution has an icon of its own, not the folder's",
            done && !IconSamePicture(q, r) && !IconSamePicture(q, folder) && !IconSamePicture(r, folder) && !IconSamePicture(wsl, folder));

        var paneDrive = icons.GetSmallIcon(@"S:\", true);
        var paneFolder = icons.GetSmallIcon(@"S:\Users", true);
        Check("J118: the navigation pane draws a drive with its glyph, without asking the Shell about it on the UI thread, and a folder still with the folder icon",
            paneDrive is null && !asked.Contains(@"S:\") && IconSamePicture(paneFolder, folder));
    }

    // ---- J037: four hung shortcuts -------------------------------------------------------------

    /// <summary>
    /// Four shortcuts to a server that is off, each holding a worker for the
    /// network's timeout: the next icon asked for must still be answered
    /// within seconds, not when the network gives up.
    /// </summary>
    private static void Round2IconHungChecks()
    {
        using var release = new ManualResetEventSlim(false);
        var asked = new ConcurrentQueue<string>();
        ImageSource? Extract(string path, bool isDirectory)
        {
            asked.Enqueue(path);
            if (path.Contains("offline", StringComparison.OrdinalIgnoreCase))
            {
                release.Wait(TimeSpan.FromSeconds(90));
            }

            return FakeShell.Picture(StringComparer.OrdinalIgnoreCase.GetHashCode(path));
        }

        static bool Within(TimeSpan limit, Func<bool> done)
        {
            var clock = Stopwatch.StartNew();
            while (!done())
            {
                if (clock.Elapsed > limit)
                {
                    return false;
                }

                Thread.Sleep(5);
            }

            return true;
        }

        var icons = new ShellIconService(Extract, Dispatcher.CurrentDispatcher);
        try
        {
            for (var index = 0; index < 4; index++)
            {
                icons.Request($@"\\offline\share\Desktop\offline{index}.lnk", false, _ => { });
            }

            var hung = Stopwatch.StartNew();
            var allHung = Within(TimeSpan.FromSeconds(40), () => asked.Count(path => path.Contains("offline", StringComparison.OrdinalIgnoreCase)) >= 4);
            hung.Stop();
            var healthy = Path.Combine(Round2Root, "hung", "healthy.txt");
            icons.Request(healthy, false, _ => { });
            var clock = Stopwatch.StartNew();
            var answered = Within(TimeSpan.FromSeconds(15), () => icons.GetCached(healthy, false) is not null);
            clock.Stop();
            Console.WriteLine($"  four workers hung on shortcuts after {hung.Elapsed.TotalSeconds:F1} s; the next icon {(answered ? "was answered" : "was still not answered")} after {clock.Elapsed.TotalSeconds:F1} s");
            Check("J037: with four workers hung on shortcuts to a server that is off, the next icon is answered within seconds",
                allHung && answered && clock.Elapsed < TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.Set();
            icons.Dispose();
        }
    }
}
