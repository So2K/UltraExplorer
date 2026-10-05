using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The icon inbox: the Shell's answers for the canvas reach it through one
/// queue - many arrivals are one redraw of the names, at most fifteen a second
/// at rest, and no dispatcher operation per icon - the folder list's callbacks
/// come in batches, what is on screen is answered before what a prefetch
/// asked for, a file named in capitals (.EXE) finds its icon, and a prefetched
/// type's icon gives way to the one of the first file of it drawn.  The Shell
/// is a stand-in throughout, so the checks count its calls and choose its pictures.
/// </summary>
internal static partial class Program
{
    private static Task IconInboxChecks()
    {
        Section("icon inbox");
        IconKeyChecks();
        RunOnSta("icon inbox on a dispatcher", IconInboxDispatcherChecks);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The Shell for the checks: records every path it is asked about, in
    /// order, can hold the first question until released, and paints a
    /// 16-pixel square whose colour comes from the file's type - or from the
    /// whole path for the types listed as having an icon per file, the way
    /// some icon handlers read every file of their type.
    /// </summary>
    private sealed class FakeShell(params string[] perFileTypes)
    {
        private readonly HashSet<string> _perFile = new(perFileTypes, StringComparer.OrdinalIgnoreCase);

        public ConcurrentQueue<string> Asked { get; } = new();

        /// <summary>Set: the first question waits for it.</summary>
        public ManualResetEventSlim? Gate { get; set; }

        /// <summary>How long each answer takes, spun, in milliseconds.</summary>
        public double DelayMilliseconds { get; set; }

        public ImageSource? Extract(string path, bool isDirectory)
        {
            var first = Asked.IsEmpty;
            Asked.Enqueue(path);
            if (first)
            {
                Gate?.Wait(TimeSpan.FromSeconds(10));
            }

            if (DelayMilliseconds > 0)
            {
                var until = Stopwatch.GetTimestamp() + (long)(DelayMilliseconds * Stopwatch.Frequency / 1000);
                while (Stopwatch.GetTimestamp() < until)
                {
                }
            }

            var type = Path.GetExtension(path);
            return Picture(StringComparer.OrdinalIgnoreCase.GetHashCode(_perFile.Contains(type) ? path : type));
        }

        public static BitmapSource Picture(int seed)
        {
            var pixels = new uint[16 * 16];
            Array.Fill(pixels, 0xFF000000u | (uint)seed & 0x00FFFFFFu);
            var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Pbgra32, null, pixels, 16 * 4);
            bitmap.Freeze();
            return bitmap;
        }
    }

    /// <summary>Whether two icons are the same picture, pixel for pixel.</summary>
    private static bool IconSamePicture(ImageSource? first, ImageSource? second)
    {
        if (first is not BitmapSource a || second is not BitmapSource b || a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight)
        {
            return false;
        }

        var left = new byte[a.PixelWidth * a.PixelHeight * 4];
        var right = new byte[left.Length];
        a.CopyPixels(left, a.PixelWidth * 4, 0);
        b.CopyPixels(right, b.PixelWidth * 4, 0);
        return left.AsSpan().SequenceEqual(right);
    }

    /// <summary>Waits, without letting the dispatcher run, until <paramref name="done"/> or ten seconds.</summary>
    private static bool IconSpinUntil(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.Elapsed.TotalSeconds > 10)
            {
                return false;
            }

            Thread.Sleep(1);
        }

        return true;
    }

    /// <summary>Waits, letting the dispatcher run, until <paramref name="done"/> or ten seconds.</summary>
    private static async Task<bool> IconWaitUntilAsync(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.Elapsed.TotalSeconds > 10)
            {
                return false;
            }

            await Task.Delay(5);
        }

        return true;
    }

    /// <summary>What an icon is cached under, and which files make a path to be looked up.</summary>
    private static void IconKeyChecks()
    {
        Check("programs, shortcuts and icon files have icons of their own, in any case and with or without the dot",
            ShellIconService.IsPathSpecificIcon(".EXE") && ShellIconService.IsPathSpecificIcon("exe")
            && ShellIconService.IsPathSpecificIcon(".Lnk") && ShellIconService.IsPathSpecificIcon("ICO")
            && !ShellIconService.IsPathSpecificIcon(".txt") && !ShellIconService.IsPathSpecificIcon("")
            && !ShellIconService.IsPathSpecificIcon("exe2"));

        Check("a program is cached under its path, .EXE as much as .exe",
            ShellIconService.KeyOf(@"C:\x\ARP.EXE", false) == @"C:\x\ARP.EXE" && ShellIconService.KeyOf(@"C:\x\calc.exe", false) == @"C:\x\calc.exe");
        Check("any other file under its type: the extension, lower case, without the dot",
            ShellIconService.KeyOf(@"C:\x\README.TXT", false) == "txt" && ShellIconService.KeyOf(@"C:\x\hosts", false) == ""
            && ShellIconService.KeyOf(@"C:\x\.gitignore", false) == "gitignore");
        Check("and every folder under one key", ShellIconService.KeyOf(@"C:\x", true) == ShellIconService.KeyOf(@"D:\y\z", true));

        var readme = new NestedFile("README.TXT", false, 1);
        var program = new NestedFile("ARP.EXE", false, 1);
        var bare = new NestedFile("hosts", false, 1);
        var dotted = new NestedFile(".gitignore", false, 1);
        var longType = new NestedFile("x." + new string('e', 40), false, 1);
        Check("the canvas looks a typed file up by its shared extension, the very string",
            ReferenceEquals(ShellIconService.TypeKeyOf(readme), readme.Extension) && ShellIconService.TypeKeyOf(bare) == "");
        Check("and by its path a program, a dot-file and an extension longer than the tree keeps",
            ShellIconService.TypeKeyOf(program) is null && ShellIconService.TypeKeyOf(dotted) is null && ShellIconService.TypeKeyOf(longType) is null);
    }

    private static async Task IconInboxDispatcherChecks()
    {
        await IconLaneChecks();
        await IconCanvasKeyChecks();
        await IconPrefetchChecks();
        await IconCallbackBatchChecks();
        await IconRedrawChecks();
        await IconHiddenCanvasChecks();
        await IconEndToEndChecks();
    }

    /// <summary>A tree over <paramref name="disk"/>, with <paramref name="paths"/> read.</summary>
    private static async Task<NestedTree> IconTreeAsync(FakeDisk disk, params string[] paths)
    {
        var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await tree.LoadAsync(tree.Find(@"Q:\")!);
        foreach (var path in paths)
        {
            await tree.LoadAsync(tree.Find(path)!);
        }

        return tree;
    }

    /// <summary>The index among <paramref name="folder"/>'s shown files of the one named <paramref name="name"/>.</summary>
    private static int IconIndexOf(NestedFolder folder, string name)
    {
        for (var index = 0; index < folder.Files.Count; index++)
        {
            if (folder.Files[index].Name == name)
            {
                return index;
            }
        }

        throw new InvalidOperationException($"{name} is not in {folder.FullPath}");
    }

    /// <summary>What is on screen is answered newest first, before anything a prefetch asked for.</summary>
    private static async Task IconLaneChecks()
    {
        var disk = new FakeDisk();
        disk.Folder(@"Q:\lanes");
        disk.AddFile(@"Q:\lanes", "first.aaa");
        foreach (var name in new[] { "one.exe", "two.exe", "three.exe", "early.ppp", "late.qqq" })
        {
            disk.AddFile(@"Q:\lanes", name);
        }

        using var tree = await IconTreeAsync(disk, @"Q:\lanes");
        var lanes = tree.Find(@"Q:\lanes")!;
        using var gate = new ManualResetEventSlim(false);
        var shell = new FakeShell { Gate = gate };
        using var icons = new ShellIconService(shell.Extract, Dispatcher.CurrentDispatcher);

        // The first question holds the worker; everything after it queues.
        icons.GetForCanvas(lanes, IconIndexOf(lanes, "first.aaa"));
        IconSpinUntil(() => !shell.Asked.IsEmpty);
        icons.Prefetch(lanes);
        foreach (var name in new[] { "one.exe", "two.exe", "three.exe" })
        {
            icons.GetForCanvas(lanes, IconIndexOf(lanes, name));
        }

        gate.Set();
        IconSpinUntil(() => icons.PendingCount == 0);
        var order = shell.Asked.Select(Path.GetFileName).ToArray();
        Console.WriteLine($"  asked, in order: {string.Join(", ", order)}");
        Check("on screen, the newest question is answered first, and a prefetch waits behind all of them",
            order is ["first.aaa", "three.exe", "two.exe", "one.exe", "early.ppp", "late.qqq"]);
    }

    /// <summary>
    /// The canvas's lookup: a file named in capitals finds its icon, programs
    /// are asked about one by one and every other type once, and looking a
    /// typed file up makes nothing once its icon is known.
    /// </summary>
    private static async Task IconCanvasKeyChecks()
    {
        var disk = new FakeDisk();
        disk.Folder(@"Q:\progs");
        string[] names = ["ARP.EXE", "PING.EXE", "calc.exe", "notes.txt", "README.TXT", "hosts", ".gitignore"];
        foreach (var name in names)
        {
            disk.AddFile(@"Q:\progs", name);
        }

        using var tree = await IconTreeAsync(disk, @"Q:\progs");
        var progs = tree.Find(@"Q:\progs")!;
        using var gate = new ManualResetEventSlim(false);
        var shell = new FakeShell { Gate = gate };
        using var icons = new ShellIconService(shell.Extract, Dispatcher.CurrentDispatcher);
        var counting = new CountingDriver();
        icons.CanvasArrivals.Driver.Fallback = counting;

        // The Shell holds its first answer until every file has asked once.
        var first = names.Select(name => icons.GetForCanvas(progs, IconIndexOf(progs, name))).ToArray();
        gate.Set();
        IconSpinUntil(() => icons.PendingCount == 0);
        var second = names.Select(name => icons.GetForCanvas(progs, IconIndexOf(progs, name))).ToArray();
        var asked = shell.Asked.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        Check("nothing is known before the Shell answers", first.All(icon => icon is null));
        Check($"then every file has its icon, ARP.EXE and PING.EXE included ({second.Count(icon => icon is not null)} of {names.Length})",
            second.All(icon => icon is not null));
        Check($"the Shell is asked once per program and once per type: 6 questions ({shell.Asked.Count}), about each program",
            shell.Asked.Count == 6 && asked.IsSupersetOf(["ARP.EXE", "PING.EXE", "calc.exe", ".gitignore"]));
        Check($"each answer the canvas waited for is one arrival in its inbox, and one wake ({icons.CanvasArrivals.Count} arrivals, {counting.Wakes} wake)",
            icons.CanvasArrivals.Count == 6 && icons.CanvasArrivalsPosted == 6 && counting.Wakes == 1);

        var notes = IconIndexOf(progs, "notes.txt");
        icons.GetForCanvas(progs, notes);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var repeat = 0; repeat < 10_000; repeat++)
        {
            icons.GetForCanvas(progs, notes);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check($"looking a typed file's icon up ten thousand times makes nothing ({allocated} bytes)", allocated == 0);

        // With no canvas driving the inbox, what arrives is let go of.
        var loose = new ShellIconService(new FakeShell().Extract, Dispatcher.CurrentDispatcher);
        using (loose)
        {
            loose.GetForCanvas(progs, notes);
            IconSpinUntil(() => loose.PendingCount == 0 && loose.CanvasArrivalsPosted == 1);
            Check("with no canvas to take them in, arrivals are let go of rather than kept", loose.CanvasArrivals.IsEmpty);
        }
    }

    /// <summary>
    /// A prefetch asks the Shell about the first file of each type a folder
    /// lists; the type's icon then only holds its place until a file of it is
    /// on screen, which is asked about in turn and replaces it when it looks
    /// different - as when the icon of a type was always the first shown file's.
    /// </summary>
    private static async Task IconPrefetchChecks()
    {
        var disk = new FakeDisk();
        disk.Folder(@"Q:\consoles");
        foreach (var name in new[] { "a.console", "b.console", "c.txt", "d.txt", "e.anim", "f.anim", "g.exe" })
        {
            disk.AddFile(@"Q:\consoles", name);
        }

        using var tree = await IconTreeAsync(disk, @"Q:\consoles");
        var consoles = tree.Find(@"Q:\consoles")!;
        var shell = new FakeShell(".console", ".anim");
        using var icons = new ShellIconService(shell.Extract, Dispatcher.CurrentDispatcher);
        icons.CanvasArrivals.Driver.Fallback = new CountingDriver();

        icons.Prefetch(consoles);
        IconSpinUntil(() => icons.PendingCount == 0);
        var prefetched = shell.Asked.Select(Path.GetFileName).ToArray();
        Check($"a prefetch asks about the first file of each type, and not about programs ({string.Join(", ", prefetched)})",
            prefetched is ["a.console", "c.txt", "e.anim"]);
        var aPicture = icons.GetCached(@"Q:\consoles\a.console", false);
        Check("what it found is known at once", IconSamePicture(aPicture, FakeShell.Picture(StringComparer.OrdinalIgnoreCase.GetHashCode(@"Q:\consoles\a.console"))));

        var b = IconIndexOf(consoles, "b.console");
        var shownFirst = icons.GetForCanvas(consoles, b);
        Check("a console drawn after the prefetch shows the prefetched icon meanwhile", ReferenceEquals(shownFirst, aPicture));
        IconSpinUntil(() => icons.PendingCount == 0);
        var shownAfter = icons.GetForCanvas(consoles, b);
        Check("then the icon of the console drawn, which differs, and says so in one arrival",
            IconSamePicture(shownAfter, FakeShell.Picture(StringComparer.OrdinalIgnoreCase.GetHashCode(@"Q:\consoles\b.console")))
            && icons.CanvasArrivalsPosted == 1);

        var d = IconIndexOf(consoles, "d.txt");
        var textFirst = icons.GetForCanvas(consoles, d);
        IconSpinUntil(() => icons.PendingCount == 0);
        var textAfter = icons.GetForCanvas(consoles, d);
        Check("a text file drawn after the prefetch is asked about too, looks the same, and keeps the very icon with no arrival",
            textFirst is not null && ReferenceEquals(textFirst, textAfter) && icons.CanvasArrivalsPosted == 1
            && shell.Asked.Select(Path.GetFileName).Contains("d.txt"));

        // The folder list asks the same way: a prefetched type waits for the list's own file.
        ImageSource? listed = null;
        var synchronous = icons.Request(@"Q:\consoles\f.anim", false, icon => listed = icon);
        var answered = await IconWaitUntilAsync(() => listed is not null);
        Check("the folder list, asking about a type only a prefetch knows, is answered about its own file",
            !synchronous && answered && IconSamePicture(listed, FakeShell.Picture(StringComparer.OrdinalIgnoreCase.GetHashCode(@"Q:\consoles\f.anim"))));
    }

    /// <summary>The folder list's callbacks come in batches, at most one every sixteen milliseconds, however fast the answers come.</summary>
    private static async Task IconCallbackBatchChecks()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var shell = new FakeShell { DelayMilliseconds = 0.25 };
        using var icons = new ShellIconService(shell.Extract, dispatcher);
        var backgroundPosts = 0;
        DispatcherHookEventHandler posted = (_, e) =>
        {
            if (e.Operation.Priority == DispatcherPriority.Background)
            {
                backgroundPosts++;
            }
        };

        dispatcher.Hooks.OperationPosted += posted;
        var called = new List<long>();
        const int count = 250;
        var clock = Stopwatch.StartNew();
        for (var index = 0; index < count; index++)
        {
            icons.Request($@"Q:\list\program{index:D3}.exe", false, _ => called.Add(Stopwatch.GetTimestamp()));
        }

        var all = await IconWaitUntilAsync(() => called.Count == count);
        var elapsed = clock.Elapsed.TotalMilliseconds;
        dispatcher.Hooks.OperationPosted -= posted;

        // Batches are told apart by the gaps between callbacks.
        called.Sort();
        var starts = new List<long> { called.FirstOrDefault() };
        for (var index = 1; index < called.Count; index++)
        {
            if ((called[index] - called[index - 1]) * 1000.0 / Stopwatch.Frequency > 4)
            {
                starts.Add(called[index]);
            }
        }

        var closest = starts.Zip(starts.Skip(1), (earlier, later) => (later - earlier) * 1000.0 / Stopwatch.Frequency).DefaultIfEmpty(double.PositiveInfinity).Min();
        Console.WriteLine($"  {count} answers over {elapsed:0} ms: {icons.CallbackBatches} batches, {backgroundPosts} dispatcher operations, closest batches {closest:0.0} ms apart");
        Check($"every one of {count} callbacks is called", all && called.Count == count);
        Check("in batches, one dispatcher operation each, not one per icon",
            icons.CallbackBatches == backgroundPosts && backgroundPosts <= elapsed / 16 + 2 && backgroundPosts < count / 10);
        Check("at most one batch every sixteen milliseconds", closest >= 15);
    }

    /// <summary>
    /// A canvas with a small folder of files on screen, laid out, and at rest:
    /// frames are run by hand, a 120 Hz frame apart on the frames' clock, until
    /// the flight there has settled - which takes a third of a second of
    /// stillness on the stopwatch.  Returns the frames' clock where it stopped.
    /// </summary>
    private static async Task<(NestedTree Tree, NestedCanvas Canvas, NestedFolder Folder, TimeSpan Time)> IconCanvasAsync(int programs)
    {
        var disk = new FakeDisk();
        disk.Folder(@"Q:\files");
        for (var index = 0; index < programs; index++)
        {
            disk.AddFile(@"Q:\files", $"program{index:D3}.exe", 1000 + index);
        }

        disk.AddFiles(@"Q:\files", 20, "note");
        var tree = await IconTreeAsync(disk, @"Q:\files");
        var folder = tree.Find(@"Q:\files")!;
        var canvas = new NestedCanvas { Tree = tree };
        canvas.Measure(new Size(ViewWidth, ViewHeight));
        canvas.Arrange(new Rect(0, 0, ViewWidth, ViewHeight));
        canvas.UpdateLayout();
        canvas.FlyTo(folder, 0.9, animated: false);
        var time = TimeSpan.FromSeconds(100);
        var clock = Stopwatch.StartNew();
        do
        {
            await Task.Delay(15);
            time += TimeSpan.FromTicks(83_333);
            canvas.RunFrameForTests(time);
        }
        while (!canvas.IsIdle && clock.Elapsed.TotalSeconds < 5);

        return (tree, canvas, folder, time);
    }

    /// <summary>
    /// The canvas's side, frames run by hand: everything waiting is one redraw
    /// of the names, a stream of arrivals at rest is at most fifteen redraws a
    /// second, a frame that draws the names anyway takes them along, and a
    /// held redraw keeps the loop from resting until it is made.
    /// </summary>
    private static async Task IconRedrawChecks()
    {
        var (tree, canvas, _, time) = await IconCanvasAsync(10);
        using var _tree = tree;
        var driver = new CountingDriver();
        var inbox = new FrameInbox<IconArrival>(new FrameDriverSlot(driver));
        canvas.IconArrivals = inbox;
        Check("the canvas drives the inbox it is given", ReferenceEquals(inbox.Driver.Active, canvas));
        Check("at rest to begin with", canvas.IsIdle && canvas.IconRedraws == 0);

        var dispatcher = Dispatcher.CurrentDispatcher;
        var posts = 0;
        DispatcherHookEventHandler counted = (_, _) => posts++;
        dispatcher.Hooks.OperationPosted += counted;
        for (var index = 0; index < 250; index++)
        {
            inbox.Post(new IconArrival($"k{index}", null));
        }

        dispatcher.Hooks.OperationPosted -= counted;
        Check($"250 arrivals ask the dispatcher for one wake of the frame loop ({posts} operation)", posts == 1);

        time += TimeSpan.FromMilliseconds(500);
        canvas.RunFrameForTests(time);
        Check($"the next frame takes all 250 in, and draws the names once for them ({canvas.IconArrivalsTaken} taken, {canvas.IconRedraws} redraw)",
            canvas.IconArrivalsTaken == 250 && canvas.IconRedraws == 1 && inbox.IsEmpty && canvas.LastFrameStats is { Skipped: false });
        Check("and with nothing else to do, the canvas is at rest again", canvas.IsIdle);

        // A second at 120 Hz with icons arriving before every frame.
        var redrawsBefore = canvas.IconRedraws;
        var mostInOneFrame = 0;
        var heldKeptBusy = true;
        for (var frame = 0; frame < 120; frame++)
        {
            inbox.Post(new IconArrival("a", null));
            inbox.Post(new IconArrival("b", null));
            time += TimeSpan.FromTicks(83_333);
            var before = canvas.IconRedraws;
            canvas.RunFrameForTests(time);
            var made = canvas.IconRedraws - before;
            mostInOneFrame = Math.Max(mostInOneFrame, made);
            heldKeptBusy &= made == 1 || !canvas.IsIdle;
        }

        var inSecond = canvas.IconRedraws - redrawsBefore;
        Check($"arrivals before every frame for a second at rest: at most 15 redraws of the names ({inSecond})", inSecond <= 15 && inSecond >= 10);
        Check("never more than one in a frame", mostInOneFrame == 1);
        Check("and a redraw held back keeps the canvas from resting", heldKeptBusy);

        // The arrivals stop: the held redraw is made within its interval, then the canvas rests.
        var framesToRest = 0;
        while (!canvas.IsIdle && framesToRest < 20)
        {
            time += TimeSpan.FromTicks(83_333);
            canvas.RunFrameForTests(time);
            framesToRest++;
        }

        Check($"once they stop, the last redraw comes within {FrameBudgets.IconRefreshMinMs} ms and the canvas rests ({framesToRest} frames)",
            canvas.IsIdle && framesToRest <= 9);

        // A frame that draws the names anyway takes the icons along.
        var alongBefore = canvas.IconRedraws;
        inbox.Post(new IconArrival("c", null));
        canvas.Redraw();
        time += TimeSpan.FromTicks(83_333);
        canvas.RunFrameForTests(time);
        Check("a frame that draws everything anyway takes arrived icons along, with no redraw of their own",
            canvas.IconRedraws == alongBefore && canvas.IsIdle);

        // Long after the last, one arrival is drawn in the very next frame.
        time += TimeSpan.FromSeconds(1);
        inbox.Post(new IconArrival("d", null));
        canvas.RunFrameForTests(time);
        Check("after a rest, a single arrival is drawn in the next frame, not held", canvas.IconRedraws == alongBefore + 1 && canvas.IsIdle);

        canvas.IconArrivals = null;
        Check("given back, the inbox wakes its fallback again", inbox.Driver.Active is null);
    }

    /// <summary>A canvas in no window cannot draw: the service's own fallback lets arrivals go, and the canvas's loop is never hooked for them.</summary>
    private static async Task IconHiddenCanvasChecks()
    {
        var disk = new FakeDisk();
        disk.Folder(@"Q:\files");
        for (var index = 0; index < 5; index++)
        {
            disk.AddFile(@"Q:\files", $"program{index:D3}.exe");
        }

        using var tree = await IconTreeAsync(disk, @"Q:\files");
        var folder = tree.Find(@"Q:\files")!;
        var canvas = new NestedCanvas();
        using var icons = new ShellIconService(new FakeShell().Extract, Dispatcher.CurrentDispatcher);
        canvas.IconArrivals = icons.CanvasArrivals;
        for (var index = 0; index < 5; index++)
        {
            icons.GetForCanvas(folder, IconIndexOf(folder, $"program{index:D3}.exe"));
        }

        var settled = await IconWaitUntilAsync(() => icons.PendingCount == 0 && icons.CanvasArrivalsPosted == 5 && icons.CanvasArrivals.IsEmpty);
        await Task.Delay(30);
        Check("a canvas off screen hands its icon arrivals to the service's fallback, which lets them go",
            settled && icons.CanvasArrivals.IsEmpty && canvas.IconArrivalsTaken == 0);
        Check("and never hooks its frame loop for them", !canvas.IsFrameHooked && canvas.IconRedraws == 0);
        canvas.IconArrivals = null;
    }

    /// <summary>
    /// The whole path on the CPU canvas: the names ask for 250 programs' icons
    /// as they are drawn, the Shell answers each, and the canvas hears of all
    /// of them through one wake of its frame loop - where one dispatcher
    /// operation per icon, each redrawing every name, used to be.
    /// </summary>
    private static async Task IconEndToEndChecks()
    {
        const int programs = 250;
        var (tree, canvas, _, time) = await IconCanvasAsync(programs);
        using var _tree = tree;
        var shell = new FakeShell { DelayMilliseconds = 0.05 };
        using var icons = new ShellIconService(shell.Extract, Dispatcher.CurrentDispatcher);
        icons.CanvasArrivals.Driver.Fallback = new CountingDriver();
        canvas.IconLookup = icons.GetForCanvas;
        canvas.IconArrivals = icons.CanvasArrivals;

        var dispatcher = Dispatcher.CurrentDispatcher;
        var posts = 0;
        DispatcherHookEventHandler counted = (_, _) => posts++;
        dispatcher.Hooks.OperationPosted += counted;

        // Drawn on demand, as a snapshot is: every name on screen asks.
        canvas.RenderNow();
        var answered = IconSpinUntil(() => icons.PendingCount == 0);
        dispatcher.Hooks.OperationPosted -= counted;
        var asked = shell.Asked.ToArray();
        var posted = icons.CanvasArrivalsPosted;
        var programsAsked = asked.Count(path => path.EndsWith(".exe", StringComparison.Ordinal));
        Console.WriteLine($"  one frame of names asked about {asked.Length} icons ({programsAsked} programs); {posted} answers arrived with {posts} dispatcher operation(s)");
        Check($"a frame of names asks about every program on screen, each by its path ({programsAsked})", programsAsked >= 100);
        Check($"their answers ask the dispatcher for one wake of the canvas, not one operation per icon ({posts} for {posted})",
            answered && posted == asked.Length && posts == 1);

        canvas.RunFrameForTests(time += TimeSpan.FromSeconds(1));
        Check($"the next frame takes every answer in and draws the names once ({canvas.IconArrivalsTaken} taken, {canvas.IconRedraws} redraw)",
            canvas.IconArrivalsTaken == posted && canvas.IconRedraws == 1);
        var missing = asked.Count(path => icons.GetCached(path, false) is null);
        Check($"after which every file asked about has its icon ({missing} missing), and the canvas rests", missing == 0 && canvas.IsIdle);
        canvas.IconArrivals = null;
    }
}
