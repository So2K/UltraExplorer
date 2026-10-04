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
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FATAL {ex}");
            Check("icon and glyph round 2 checks ran to the end", false);
        }

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
}
