using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The icon fixes of the review of 2026-10-02: two canvases drawing from the
/// one GPU icon atlas no longer evict each other's icons on screen in a loop
/// (I099), a dot-file is drawn with its type's icon on the GPU canvas (I132),
/// Internet shortcuts, cursors, consoles and ClickOnce references have icons of
/// their own everywhere, not the first file's (I101), a shortcut to a server
/// that is offline no longer holds up every other icon of the folder list and
/// the CPU canvas (I100), and past 20,000 programs the CPU canvas still asks
/// for new icons while the icons of single files are let go of rather than
/// kept for the window's life (I133, I134).
///
/// Headless: the Shell is a stand-in throughout, no device is made and
/// nothing on disk is read or written.
/// </summary>
internal static partial class Program
{
    private static Task IconReviewFixChecks()
    {
        Section("icon review fixes");
        try
        {
            IconTwoCanvasEvictionChecks();
            IconDotFileTypeChecks();
            IconPerFileTypeSetChecks();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FATAL {ex}");
            Check("icon review fix checks ran to the end", false);
        }

        RunOnSta("icon service review fixes on a dispatcher", IconServiceReviewFixChecks);
        return Task.CompletedTask;
    }

    private static async Task IconServiceReviewFixChecks()
    {
        await IconPerFileServiceChecks();
        IconStuckWorkerChecks();
        await IconCanvasAskBoundChecks();
    }

    // ---- I099: two canvases, one atlas --------------------------------------------------

    /// <summary>
    /// Two canvases draw from the one atlas, the panes of a split view or two
    /// windows, with more programs between them than the per-file region
    /// holds.  Each one's frame counts as a frame of the atlas, so by the time
    /// one canvas's next frame takes an arrival in, the icons it drew last are
    /// two frames old: they must still count as on screen, or the canvases take
    /// each other's slots in turn, for good, and the Shell is never left alone.
    /// A canvas alone still gives up a slot it stopped drawing two frames ago.
    /// </summary>
    private static void IconTwoCanvasEvictionChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerIconReview");
        IconAtlasOptions Options(IconFakeSource source) => new()
        {
            CachePath = null,
            AutoSaveInterval = TimeSpan.Zero,
            PerFileCapacity = 4,
            MaximumSlices = 16,
            InitialSlices = 4,
            Source = source.Find
        };

        var source = new IconFakeSource();
        using var atlas = new IconAtlas(Options(source));
        Action left = () => { };
        Action right = () => { };
        atlas.ArrivalsPending += left;
        atlas.ArrivalsPending += right;
        var leftPrograms = Enumerable.Range(1, 3).Select(index => Path.Combine(root, "left", $"tool-{index}.exe")).ToArray();
        var rightPrograms = Enumerable.Range(1, 2).Select(index => Path.Combine(root, "right", $"tool-{index}.exe")).ToArray();

        void Frame(IconAtlas on, string[] programs)
        {
            on.ProcessArrivals(null, null);
            foreach (var program in programs)
            {
                on.SlotFor(program, "exe");
            }
        }

        void Settle(IconAtlas on)
        {
            IconWaitFor(() => on.PendingKeyCount == 0 || on.HasPendingArrivals);
            Thread.Sleep(20);
        }

        // The left pane asks for its three programs and draws them; then the
        // right pane asks for its two: five programs, room for four.
        Frame(atlas, leftPrograms);
        Settle(atlas);
        Frame(atlas, leftPrograms);
        Frame(atlas, rightPrograms);
        Settle(atlas);

        long settled = 0;
        for (var pass = 0; pass < 40; pass++)
        {
            // Every arrival wakes both canvases, which draw one after the other.
            Frame(atlas, leftPrograms);
            Frame(atlas, rightPrograms);
            Settle(atlas);
            if (pass == 4)
            {
                settled = atlas.ExtractionCount;
            }
        }

        var more = atlas.ExtractionCount - settled;
        var type = atlas.SlotFor("exe");
        var leftSlots = leftPrograms.Select(program => atlas.SlotFor(program, "exe")).ToArray();
        Console.WriteLine($"  two canvases, five programs, four slots: {more} more extractions in the last 35 passes; left slots {string.Join(", ", leftSlots)}, the type's {type}");
        Check("two canvases at rest stop taking each other's icons on screen, and the Shell is left alone", more == 0);
        Check("the first canvas's programs keep icons of their own",
            type >= 0 && leftSlots.All(slot => slot >= 0 && slot != type) && leftSlots.Distinct().Count() == 3 && atlas.FileSlotCount == 4);

        // One canvas alone: what it stopped drawing two frames ago gives way.
        var lone = new IconFakeSource();
        using var single = new IconAtlas(Options(lone));
        single.ArrivalsPending += left;
        var programs = Enumerable.Range(1, 5).Select(index => Path.Combine(root, "single", $"tool-{index}.exe")).ToArray();
        Frame(single, programs[..4]);
        Settle(single);
        Frame(single, programs[..4]);
        Frame(single, []);
        Frame(single, []);
        Frame(single, programs[4..]);
        Settle(single);
        Frame(single, []);
        var singleType = single.SlotFor("exe");
        var newcomer = single.SlotFor(programs[4], "exe");
        Check("a canvas alone still gives a slot it stopped drawing two frames ago to a newcomer",
            singleType >= 0 && newcomer >= 0 && newcomer != singleType && single.SlotFor(programs[0], "exe") == singleType);
    }

    // ---- I132: dot-files on the GPU canvas ---------------------------------------------

    /// <summary>
    /// A name that starts with its only dot has no extension in the tree, but
    /// the Shell reads ".gitignore" as its type, and so do Explorer and the
    /// folder list: the GPU canvas asks for that type's icon too, not for the
    /// icon of files without a type.
    /// </summary>
    private static void IconDotFileTypeChecks()
    {
        var source = new IconFakeSource();
        using var atlas = new IconAtlas(new IconAtlasOptions { CachePath = null, AutoSaveInterval = TimeSpan.Zero, Source = source.Find });
        var folder = Path.Combine(Path.GetTempPath(), "UltraExplorerIconReview", "repository");
        var dotted = new NestedFile(".gitignore", false, 1);
        var bare = new NestedFile("LICENSE", false, 1);
        atlas.SlotFor(folder, dotted.Name, dotted.Extension);
        atlas.SlotFor(folder, bare.Name, bare.Extension);
        IconSettle(atlas, null, null, () => atlas.PendingKeyCount == 0);
        var askedAboutType = source.Requests.Any(request => request.Kind == IconKeyKind.Type && request.Key == "gitignore");

        var shown = atlas.SlotFor(folder, dotted.Name, dotted.Extension);
        var typed = atlas.SlotFor("gitignore");
        var none = atlas.SlotFor("");
        Console.WriteLine($"  .gitignore: slot {shown}; the gitignore type's {typed}; files without a type {none}");
        Check("the tree gives a dot-file no extension", dotted.Extension.Length == 0);
        Check("the GPU canvas asks the Shell about a dot-file's type", askedAboutType);
        Check("and draws the dot-file with that type's icon, not the icon of files without a type",
            shown >= 0 && shown == typed && shown != none);
        Check("a name without any dot still shows the icon of files without a type",
            none >= 0 && atlas.SlotFor(folder, bare.Name, bare.Extension) == none);
    }

    // ---- I101: which types have icons of their own --------------------------------------

    /// <summary>One list of the types whose every file has an icon of its own, shared by the folder list's service and the GPU canvas's atlas.</summary>
    private static void IconPerFileTypeSetChecks()
    {
        string[] own = [".url", "URL", "ani", ".cur", ".msc", "appref-ms", ".exe", "lnk", ".ICO"];
        Check("Internet shortcuts, cursors, consoles and ClickOnce references have icons of their own, as programs do",
            own.All(type => ShellIconService.IsPathSpecificIcon(type))
            && !ShellIconService.IsPathSpecificIcon(".txt") && !ShellIconService.IsPathSpecificIcon("urls") && !ShellIconService.IsPathSpecificIcon(""));
        Check("each of them is cached under its path",
            ShellIconService.KeyOf(@"C:\x\News.URL", false) == @"C:\x\News.URL" && ShellIconService.KeyOf(@"C:\x\devmgmt.msc", false) == @"C:\x\devmgmt.msc");

        using var atlas = new IconAtlas(new IconAtlasOptions { CachePath = null, AutoSaveInterval = TimeSpan.Zero, Source = new IconFakeSource().Find });
        Check("the GPU canvas's atlas draws exactly the same types file by file",
            own.All(atlas.UsesPerFileIcon) && !atlas.UsesPerFileIcon("txt") && !atlas.UsesPerFileIcon("urls") && !atlas.UsesPerFileIcon(""));
    }

    /// <summary>
    /// A desktop of Internet shortcuts, consoles and animated cursors, each
    /// with an icon of its own: the CPU canvas and the folder list show each
    /// file's, not the first answer's for all of them.
    /// </summary>
    private static async Task IconPerFileServiceChecks()
    {
        var disk = new FakeDisk();
        disk.Folder(@"Q:\desktop");
        string[] names = ["game.url", "news.url", "compmgmt.msc", "devmgmt.msc", "busy.ani", "wait.ani"];
        foreach (var name in names)
        {
            disk.AddFile(@"Q:\desktop", name);
        }

        using var tree = await IconTreeAsync(disk, @"Q:\desktop");
        var desktop = tree.Find(@"Q:\desktop")!;
        var shell = new FakeShell(".url", ".msc", ".ani");
        using var icons = new ShellIconService(shell.Extract, Dispatcher.CurrentDispatcher);
        icons.CanvasArrivals.Driver.Fallback = new CountingDriver();
        foreach (var name in names)
        {
            icons.GetForCanvas(desktop, IconIndexOf(desktop, name));
        }

        IconSpinUntil(() => icons.PendingCount == 0);
        ImageSource Own(string name) => FakeShell.Picture(StringComparer.OrdinalIgnoreCase.GetHashCode(@"Q:\desktop\" + name));
        var drawn = names.Select(name => icons.GetForCanvas(desktop, IconIndexOf(desktop, name))).ToArray();
        var right = names.Zip(drawn).Count(pair => IconSamePicture(pair.Second, Own(pair.First)));
        Check($"on the CPU canvas every shortcut, console and cursor shows its own icon ({right} of {names.Length})", right == names.Length);

        ImageSource? listed = null;
        icons.Request(@"Q:\desktop\news.url", false, icon => listed = icon);
        var answered = await IconWaitUntilAsync(() => listed is not null);
        Check("and so does the folder list, for the second shortcut as much as the first",
            answered && IconSamePicture(listed, Own("news.url")));
    }

    // ---- I100: a shortcut to a server that is offline -------------------------------------

    /// <summary>
    /// The Shell hangs on a shortcut to a share that does not answer: every
    /// other icon must not wait behind it for the network's timeout.  Another
    /// worker takes the rest after a few seconds, without anything new asked.
    /// </summary>
    private static void IconStuckWorkerChecks()
    {
        using var release = new ManualResetEventSlim(false);
        var asked = new ConcurrentQueue<string>();
        ImageSource? Extract(string path, bool isDirectory)
        {
            asked.Enqueue(path);
            if (path.EndsWith("offline.lnk", StringComparison.OrdinalIgnoreCase))
            {
                release.Wait(TimeSpan.FromSeconds(30));
            }

            return FakeShell.Picture(StringComparer.OrdinalIgnoreCase.GetHashCode(path));
        }

        var icons = new ShellIconService(Extract, Dispatcher.CurrentDispatcher);
        try
        {
            icons.Request(@"\\offline\share\Desktop\offline.lnk", false, _ => { });
            var stuck = IconSpinUntil(() => !asked.IsEmpty);
            icons.Request(@"Q:\notes\after.txt", false, _ => { });
            var clock = Stopwatch.StartNew();
            var answered = IconSpinUntil(() => icons.GetCached(@"Q:\notes\after.txt", false) is not null);
            clock.Stop();
            Console.WriteLine($"  with the worker hung on an offline shortcut, the next icon was answered after {clock.Elapsed.TotalSeconds:0.0} s ({(answered ? "answered" : "not answered")})");
            Check("a shortcut to a server that is offline does not hold up every other icon",
                stuck && answered && clock.Elapsed < TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.Set();
            icons.Dispose();
        }
    }

    // ---- I133, I134: past 20,000 programs -----------------------------------------------

    /// <summary>
    /// The CPU canvas draws 20,001 programs, one after the other: the last is
    /// still asked about, the icons of single files the service keeps stay
    /// within their limit - the oldest let go of rather than kept for the
    /// window's life - and one let go of is asked about again when drawn again.
    /// </summary>
    private static async Task IconCanvasAskBoundChecks()
    {
        const int programs = 20_001;
        var disk = new FakeDisk();
        disk.Folder(@"Q:\many");
        for (var index = 0; index < programs; index++)
        {
            disk.AddFile(@"Q:\many", $"tool{index:D5}.exe");
        }

        using var tree = await IconTreeAsync(disk, @"Q:\many");
        var many = tree.Find(@"Q:\many")!;
        var shell = new FakeShell();
        using var icons = new ShellIconService(shell.Extract, Dispatcher.CurrentDispatcher);
        icons.CanvasArrivals.Driver.Fallback = new CountingDriver();
        Check($"the folder lists all {programs} programs", many.Files.Count == programs);

        var clock = Stopwatch.StartNew();
        for (var index = 0; index < programs - 1; index++)
        {
            icons.GetForCanvas(many, index);
        }

        IconSpinUntil(() => icons.PendingCount == 0);
        var first = many.PathOf(many.Files[0]);
        var newest = many.PathOf(many.Files[programs - 1]);
        var firstKnown = icons.GetCached(first, false) is not null;
        icons.GetForCanvas(many, programs - 1);
        var answered = IconSpinUntil(() => icons.PendingCount == 0 && icons.GetCached(newest, false) is not null);
        Console.WriteLine($"  {programs} programs drawn on the CPU canvas: {shell.Asked.Count} asked about in {clock.Elapsed.TotalSeconds:0.0} s");
        Check("past 20,000 programs drawn on the CPU canvas, a new one still gets its icon",
            answered && shell.Asked.Count == programs);
        Check("the icons of single files are let go of past their limit rather than kept for the window's life",
            firstKnown && icons.GetCached(first, false) is null && icons.GetCached(newest, false) is not null);

        icons.GetForCanvas(many, 0);
        var again = IconSpinUntil(() => icons.PendingCount == 0 && icons.GetCached(first, false) is not null);
        Check("one let go of is asked about again when the canvas draws it again",
            again && shell.Asked.Count == programs + 1);
    }
}
