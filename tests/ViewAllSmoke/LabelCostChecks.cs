using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// What the names cost, at the size of the 4K stream-in test: a folder of
/// 1,800 files at 2263 x 1123 DIPs drawn at 150 %, with marks, a pin and
/// Shell icons.  A frame at rest that only redraws the names draws them from
/// what the last one kept and hands the collector next to nothing; motion
/// replays recorded names instead of setting them again; the GPU's names
/// allocate nothing; no frame normalises a path to find a mark; the frame's
/// allowance for text holds, the settle frame makes none, and the names
/// nearest the middle are made first; a read under a filter recolours only
/// what it changed and says so once per frame; placing folders makes no
/// list where none is needed.
/// </summary>
internal static partial class Program
{
    private const double LabelCostWidth = 2263;
    private const double LabelCostHeight = 1123;

    private static Task LabelCostChecks()
    {
        RunOnSta("label cost", LabelCostChecksAsync);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The ladder of sizes names are laid out at: a step along it from any
    /// size a name is drawn at lands bit for bit on the level a layout made
    /// there is kept under.  The cache compares levels exactly, and a near
    /// size it misses is a name that blinks out during a zoom.
    /// </summary>
    private static void LabelLevelChecks()
    {
        Section("label levels");
        var cases = 0;
        var misses = 0;
        var multipliedMisses = 0;
        string? first = null;
        for (var size = 1.0; size <= 400; size *= 1.0137)
        {
            var level = NestedCanvas.LevelFor(size);
            for (var rungs = -3; rungs <= 3; rungs++)
            {
                // Well inside the rung that many steps away, found as a name finds its level.
                var there = NestedCanvas.LevelFor(level * Math.Pow(2, rungs / 4.0) * 1.02);
                var stepped = NestedCanvas.LevelAway(level, rungs);
                cases++;
                multipliedMisses += (level * Math.Pow(2, rungs / 4.0)).Equals(there) ? 0 : 1;
                if (!stepped.Equals(there))
                {
                    misses++;
                    first ??= $"size {size:R}, {rungs} rungs: {stepped:R} for {there:R}";
                }
            }
        }

        Check($"a step along the ladder is exactly the level kept for that rung, in all {cases:N0} cases (multiplying the level would miss {multipliedMisses:N0}){(first is null ? string.Empty : $"; first miss {first}")}",
            misses == 0);
    }

    private static async Task LabelCostChecksAsync()
    {
        LabelLevelChecks();
        Section("label cost");
        var disk = new FakeDisk();
        string[] extensions = ["txt", "png", "cs", "mp3", "pdf", "zip", "json", "md", "dll", "xml"];
        for (var index = 0; index < 1_800; index++)
        {
            disk.AddFile(@"Q:\many", $"file-{index:D4}.{extensions[index % extensions.Length]}", 1_000L * index + 7);
        }

        for (var index = 0; index < 12; index++)
        {
            disk.AddFiles($@"Q:\other{index:D2}", 20, "o");
        }

        disk.Folder(@"Q:\hiding\shown");
        disk.Folder(@"Q:\hiding\$secret").IsHidden = true;

        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive, "1 TB free")]);
        await LoadEverythingAsync(tree, _ => true);

        var marks = new FolderMarkService(Path.Combine(Path.GetTempPath(), "marks-" + Guid.NewGuid().ToString("N") + ".json"));
        marks.SetAccent(@"Q:\many\file-0005.png", "#E3B341");
        marks.SetNote(@"Q:\many\file-0105.cs", "a note");
        marks.SetAccent(@"Q:\other03", "#60CDFF");
        marks.SetNote(@"Q:\other03\o003.txt", "a note");

        ImageSource[] icons = [LabelCostIcon(0xFF3C8DDE), LabelCostIcon(0xFFE3B341), LabelCostIcon(0xFF6CCB5F)];
        ImageSource? LookUpIcon(NestedFolder folder, int index) => index % 5 == 4 ? null : icons[index % 3];
        var canvas = new NestedCanvas { Tree = tree, MarkLookup = marks.Get, IconLookup = LookUpIcon };
        canvas.Measure(new Size(LabelCostWidth, LabelCostHeight));
        canvas.Arrange(new Rect(0, 0, LabelCostWidth, LabelCostHeight));
        canvas.UpdateLayout();
        canvas.DpiOverride = new DpiScale(1.5, 1.5);
        canvas.SetBeacons([new NestedBeacon(@"Q:\other05", NestedBeaconKind.Pinned, Colors.Gold, "other05")]);
        Check("a mark service handed over as its lookup comes with it, so the canvas asks it by folder", ReferenceEquals(canvas.Marks, marks));

        try
        {
            var many = tree.Find(@"Q:\many")!;
            LabelCostView(canvas, many);
            Render(canvas);
            var names = canvas.FileLabelCount;
            Check($"a folder of 1,800 files at 4K shows at least 1,740 names on {canvas.FileLabelHeightForTests:F1} DIP tiles, most with an icon ({names:N0} names, {canvas.LabelIconsDrawn:N0} icons, {canvas.FolderLabelCount} folder labels)",
                names >= 1_740 && canvas.LabelIconsDrawn >= names * 3 / 4);

            LabelCostCpuChecks(canvas, names, LookUpIcon, icons);
            LabelCostNormaliseChecks(canvas, tree, marks);
            LabelCostGpuChecks(canvas);
            await LabelCostFilterChecksAsync();
            await FilterRereadChecksAsync();
            LabelCostBudgetChecks(tree, many, LookUpIcon);
            LabelCostPlacingChecks(tree);
        }
        finally
        {
            canvas.SetFilter(null);
            canvas.Tree = null;
        }
    }

    /// <summary>
    /// The folder of 1,800 files with its tiles about 13 DIPs tall - big
    /// enough for an icon beside each name - and 1,768 of them in view: the
    /// stream-in test's jump into a folder of files (L7).
    /// </summary>
    private static void LabelCostView(NestedCanvas canvas, NestedFolder many)
    {
        canvas.FlyTo(many, 1.0, animated: false);
        canvas.ZoomAt(new Point(LabelCostWidth / 2, LabelCostHeight / 2), 1.2);
    }

    /// <summary>A small frozen bitmap of one colour, as a Shell icon comes to the canvas.</summary>
    private static ImageSource LabelCostIcon(uint colour)
    {
        var pixels = Enumerable.Repeat(colour, 16 * 16).ToArray();
        var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Pbgra32, null, pixels, 16 * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The label layer alone, as a snapshot captures it.</summary>
    private static uint[] LabelCostPicture(NestedCanvas canvas)
    {
        var width = canvas.ScenePixelWidth;
        var height = canvas.ScenePixelHeight;
        var bitmap = new RenderTargetBitmap(width, height, 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(canvas.LabelLayer);
        var pixels = new uint[width * height];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    /// <summary>Labels frames of the loop at rest until nothing is left waiting and every run is drawn from what was kept; how many it took.</summary>
    private static int LabelCostSettle(NestedCanvas canvas, int limit = 200)
    {
        for (var frame = 1; frame <= limit; frame++)
        {
            canvas.RenderLabelsForTests(inMotion: false);
            if (!canvas.LabelsLeftWaiting && canvas.LabelRunsRecorded == 0)
            {
                return frame;
            }
        }

        return -1;
    }

    // ---- the CPU's names -------------------------------------------------------------------

    private static void LabelCostCpuChecks(NestedCanvas canvas, int names, Func<NestedFolder, int, ImageSource?> lookUpIcon, ImageSource[] icons)
    {
        // As a snapshot draws them: every name set in place.
        var exactBytes = 0L;
        var exactMs = 0.0;
        for (var frame = 0; frame < 3; frame++)
        {
            canvas.RenderLabelsForTests(inMotion: false, asInFrameLoop: false);
            exactBytes = canvas.LastAllocations.Labels;
            exactMs = canvas.LastLabelsMilliseconds;
        }

        var exact = LabelCostPicture(canvas);

        // At rest in the loop, once every name has been recorded.
        var settled = LabelCostSettle(canvas);
        Check($"at rest in the loop, every name is recorded and kept within {settled} frames", settled > 0);
        long restBytes = 0;
        var restMs = 0.0;
        var recorded = 0;
        var kept = 0;
        const int frames = 10;
        for (var frame = 0; frame < frames; frame++)
        {
            canvas.RenderLabelsForTests(inMotion: false);
            restBytes += canvas.LastAllocations.Labels;
            restMs += canvas.LastLabelsMilliseconds;
            recorded += canvas.LabelRunsRecorded;
            kept = canvas.LabelRunsKept;
        }

        Check($"a labels frame at rest draws its {names:N0} names from {kept} kept runs and records none ({recorded})", recorded == 0 && kept > 0 && kept <= names / 8);
        Check($"and hands the collector {restBytes / frames:N0} bytes, under 50 KB ({restMs / frames:F2} ms; drawn in place: {exactBytes:N0} bytes, {exactMs:F1} ms)",
            restBytes / frames < 50_000);

        var kept1 = LabelCostPicture(canvas);
        var (differing, largest, inked) = LabelCostDifference(exact, kept1);
        Check($"names replayed from their recordings are the names set in place, within one level ({differing} of {inked:N0} inked pixels differ, by at most {largest})",
            largest <= 1 && differing <= inked / 100);

        // One icon arrives: only its run is recorded again.
        canvas.IconLookup = (folder, index) => index == 7 ? icons[2] : lookUpIcon(folder, index);
        canvas.RenderLabelsForTests(inMotion: false);
        var iconRuns = canvas.LabelRunsRecorded;
        var iconBytes = canvas.LastAllocations.Labels;
        Check($"an icon arriving for one name records its run again and no other ({iconRuns} of {canvas.LabelRunsKept + iconRuns}, {iconBytes:N0} bytes)",
            iconRuns == 1 && iconBytes < 50_000);
        canvas.IconLookup = lookUpIcon;
        LabelCostSettle(canvas);

        // In motion: recordings replayed under a transform, nothing set again.
        for (var frame = 0; frame < 3; frame++)
        {
            canvas.RenderLabelsForTests(inMotion: true);
        }

        long motionBytes = 0;
        var motionMs = 0.0;
        for (var frame = 0; frame < frames; frame++)
        {
            canvas.RenderLabelsForTests(inMotion: true);
            motionBytes += canvas.LastAllocations.Labels;
            motionMs += canvas.LastLabelsMilliseconds;
        }

        Check($"a labels frame in motion replays the recorded names: {motionBytes / frames:N0} bytes, {motionMs / frames:F2} ms (set in place: {exactBytes:N0} bytes, {exactMs:F1} ms)",
            motionBytes / frames * 5 < exactBytes);
        LabelCostSettle(canvas);
    }

    /// <summary>Pixels that differ between two pictures, the largest difference in any channel, and the pixels with any ink in the first.</summary>
    private static (int Differing, int Largest, int Inked) LabelCostDifference(uint[] first, uint[] second)
    {
        var differing = 0;
        var largest = 0;
        var inked = 0;
        for (var index = 0; index < first.Length; index++)
        {
            if (first[index] != 0)
            {
                inked++;
            }

            if (first[index] == second[index])
            {
                continue;
            }

            differing++;
            for (var shift = 0; shift < 32; shift += 8)
            {
                largest = Math.Max(largest, Math.Abs((int)((first[index] >> shift) & 0xFF) - (int)((second[index] >> shift) & 0xFF)));
            }
        }

        return (differing, largest, inked);
    }

    // ---- no path normalised --------------------------------------------------------------------

    private static void LabelCostNormaliseChecks(NestedCanvas canvas, NestedTree tree, FolderMarkService marks)
    {
        var other = tree.Find(@"Q:\other03")!;
        Check("the service knows which folders hold a mark, by their paths as the canvas has them",
            marks.MarkedFolders.Contains(@"Q:\many") && marks.MarkedFolders.Contains(@"Q:\") && marks.HasMarksIn(tree.Find(@"Q:\many")!)
            && !marks.HasMarksIn(tree.Find(@"Q:\other01")!) && marks.Get(other).AccentHex == "#60CDFF"
            && marks.GetIn(other, "o003.txt").Note == "a note" && marks.GetIn(other, "o004.txt").IsEmpty);

        var before = FolderMarkService.PathsNormalised;
        canvas.InvalidateMarks();
        canvas.RenderNow();
        for (var frame = 0; frame < 5; frame++)
        {
            canvas.RenderNow();
            canvas.RenderLabelsForTests(inMotion: false);
            canvas.RenderLabelsForTests(inMotion: true);
        }

        canvas.FitAll(animated: false);
        canvas.RenderNow();
        canvas.RenderLabelsForTests(inMotion: false);
        var normalised = FolderMarkService.PathsNormalised - before;
        Check($"drawing the cells and the names, every mark looked up again, normalises no path ({normalised})", normalised == 0);
        LabelCostView(canvas, tree.Find(@"Q:\many")!);
        canvas.RenderNow();
        LabelCostSettle(canvas);
    }

    // ---- the GPU's names -------------------------------------------------------------------------

    private static void LabelCostGpuChecks(NestedCanvas canvas)
    {
        CompiledShaders shaders;
        GpuDeviceSet set;
        try
        {
            shaders = ShaderCache.Load(null);
            set = GpuDeviceSet.CreateWarp();
        }
        catch (Exception ex) when (ex is GpuUnavailableException or SharpGen.Runtime.SharpGenException)
        {
            Check($"a WARP device draws the GPU's names for the cost checks ({ex.Message})", false);
            return;
        }

        var faces = FaceRegistry.Shared;
        using var glyphs = new GlyphAtlas(faces);
        glyphs.WarmUp(null);
        try
        {
            var renderer = NestedGpuRenderer.For(set, shaders);
            var labels = new GpuLabelTarget(faces, new TextShaper(faces), glyphs, icons: null);
            using var frame = new NestedGpuFrame();
            using var target = set.CreateOffscreenTarget(canvas.ScenePixelWidth, canvas.ScenePixelHeight);
            canvas.SetFilter("file-0");
            for (var warm = 0; warm < 6; warm++)
            {
                canvas.RenderOffscreen(renderer, target, frame, labels, inMotion: warm % 2 == 1);
            }

            set.WaitForGpu(5_000, out _);
            const int frames = 10;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < frames; index++)
            {
                canvas.RenderOffscreen(renderer, target, frame, labels, inMotion: index % 2 == 1);
            }

            var allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / frames;
            set.WaitForGpu(5_000, out _);
            Check($"the same view on the GPU, with marks, a pin and a filter, allocates nothing per frame ({allocated} bytes; {canvas.LastGpuGlyphs:N0} glyphs, names {canvas.LastLabelsMilliseconds:F2} ms)",
                allocated == 0 && canvas.LastGpuGlyphs > 10_000);
        }
        finally
        {
            canvas.SetFilter(null);
            set.Dispose();
        }
    }

    // ---- a read under a filter -------------------------------------------------------------------

    private static async Task LabelCostFilterChecksAsync()
    {
        var disk = new FakeDisk();
        for (var index = 0; index < 8; index++)
        {
            disk.AddFile($@"Q:\read{index}", index % 2 == 0 ? "match.txt" : "other.txt");
            disk.AddFiles($@"Q:\kept{index}", 3, "k");
        }

        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        var root = tree.Find(@"Q:\")!;
        await tree.LoadAsync(root);
        var kept = root.Children.Where(folder => folder.Name.StartsWith("kept", StringComparison.Ordinal)).ToList();
        var read = root.Children.Where(folder => folder.Name.StartsWith("read", StringComparison.Ordinal)).ToList();
        await Task.WhenAll(kept.Select(folder => tree.LoadAsync(folder)));

        var canvas = new NestedCanvas { Tree = tree, HoldsFrameEventsForTests = true };
        canvas.Measure(new Size(1200, 800));
        canvas.Arrange(new Rect(0, 0, 1200, 800));
        canvas.UpdateLayout();
        try
        {
            canvas.FitAll(animated: false);
            var changes = 0;
            canvas.FilterChanged += () => changes++;
            canvas.SetFilter("match");
            canvas.RenderNow();
            var stamp = canvas.PaletteStampForTests;
            Check("a new filter is said at once, and every cell drawn takes its colours under the new stamp",
                changes == 1 && kept.All(folder => folder.PaletteStamp == stamp) && root.PaletteStamp == stamp);

            // The folders are read while the canvas is not listening, then
            // handed to it one after another as the tree hands a frame's reads.
            canvas.Tree = null;
            await Task.WhenAll(read.Select(folder => tree.LoadAsync(folder)));
            canvas.Tree = tree;
            canvas.FitAll(animated: false);
            canvas.RenderNow();
            var matchesBefore = canvas.FilterMatches.Count;
            changes = 0;
            foreach (var folder in read)
            {
                canvas.FolderLoadedForTests(folder);
            }

            var readOutOfDate = read.All(folder => folder.PaletteStamp != stamp);
            var others = kept.Count(folder => folder.PaletteStamp != stamp);
            var driveRecoloured = root.PaletteStamp != stamp;
            Check($"eight folders read under the filter recolour themselves and the drive that now holds a match, and no other cell ({others} others recoloured)",
                readOutOfDate && others == 0 && driveRecoloured && canvas.PaletteStampForTests == stamp);
            var heldBack = changes;
            canvas.RunFrameForTests(TimeSpan.FromMilliseconds(1_000));
            canvas.RunFrameForTests(TimeSpan.FromMilliseconds(1_008));
            Check($"and their {canvas.FilterMatches.Count - matchesBefore} new matches are said once, at the end of the frame's intake ({heldBack} before the frame, {changes} after two)",
                heldBack == 0 && changes == 1 && canvas.FilterMatches.Count - matchesBefore == 4);
        }
        finally
        {
            canvas.SetFilter(null);
            canvas.Tree = null;
        }
    }

    /// <summary>
    /// A folder read again under the filter: a match renamed - to a name the
    /// filter still takes - is one match under its new name, not two, and a
    /// match deleted is gone, from the count and from the steps through them.
    /// </summary>
    private static async Task FilterRereadChecksAsync()
    {
        Section("label cost: the filter over a folder read again");
        var disk = new FakeDisk();
        disk.AddFile(@"Q:\photos", "a.png", 10);
        disk.AddFile(@"Q:\photos", "b.png", 20);
        disk.AddFile(@"Q:\photos", "c.txt", 30);
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        var canvas = new NestedCanvas { Tree = tree };
        canvas.Measure(new Size(1200, 800));
        canvas.Arrange(new Rect(0, 0, 1200, 800));
        canvas.UpdateLayout();
        try
        {
            canvas.SetFilter("*.png");
            var photos = tree.Find(@"Q:\photos")!;
            var before = canvas.FilterMatches.Count;
            var files = disk.Folder(@"Q:\photos").Files;
            files.RemoveAll(file => file.Name is "a.png" or "b.png");
            disk.AddFile(@"Q:\photos", "renamed.png", 10);
            await tree.RefreshAsync(photos);
            var renamed = Path.Combine(photos.FullPath, "renamed.png");
            var after = canvas.FilterMatches.ToList();
            Check($"one match renamed and one deleted leave one match, under the new name ({before} before, then {string.Join(", ", after)})",
                before == 2 && after.Count == 1 && string.Equals(after[0], renamed, StringComparison.OrdinalIgnoreCase));
            Check("and a step goes to it, never to one that is gone",
                canvas.GoToMatch(1) && canvas.FilterCursor == 0 && canvas.GoToMatch(1) && canvas.FilterCursor == 0);
        }
        finally
        {
            canvas.SetFilter(null);
            canvas.Tree = null;
        }
    }

    // ---- the frame's allowance for text ----------------------------------------------------------

    /// <summary>A canvas of the cost checks' size, showing <paramref name="folder"/>, and none of whose names has been laid out or recorded yet.</summary>
    private static NestedCanvas LabelCostCanvas(NestedTree tree, NestedFolder folder, Func<NestedFolder, int, ImageSource?> lookUpIcon)
    {
        var canvas = new NestedCanvas { Tree = tree, IconLookup = lookUpIcon };
        canvas.Measure(new Size(LabelCostWidth, LabelCostHeight));
        canvas.Arrange(new Rect(0, 0, LabelCostWidth, LabelCostHeight));
        canvas.UpdateLayout();
        canvas.DpiOverride = new DpiScale(1.5, 1.5);
        LabelCostView(canvas, folder);
        return canvas;
    }

    private static void LabelCostBudgetChecks(NestedTree tree, NestedFolder many, Func<NestedFolder, int, ImageSource?> lookUpIcon)
    {
        // Every text of the view made in one frame, as a snapshot makes them.
        var once = LabelCostCanvas(tree, many, lookUpIcon);
        once.RenderLabelsForTests(inMotion: false, asInFrameLoop: false, withScene: true);
        var allAtOnce = once.LastLabelsMilliseconds;
        var wanted = once.LabelTextsMade;
        once.Tree = null;

        // The same in the loop: motion, then the settle, which makes no text.
        var loop = LabelCostCanvas(tree, many, lookUpIcon);
        try
        {
            loop.RenderLabelsForTests(inMotion: true, withScene: true);
            loop.RenderLabelsForTests(inMotion: true);
            var motionMade = loop.LabelTextsMade;
            loop.RenderLabelsForTests(inMotion: false);
            var settleMade = loop.LabelTextsMade;
            Check($"the first frame at rest after motion makes no text ({settleMade}; the motion frame before it made {motionMade})",
                settleMade == 0 && motionMade > 0 && loop.LabelsLeftWaiting);

            // Then the names nearest the middle first, a frame's allowance at a time.
            var madeBefore = loop.FileNameStatesForTests().Where(state => state.Made).Select(state => state.Index).ToHashSet();
            loop.RenderLabelsForTests(inMotion: false);
            var states = loop.FileNameStatesForTests();
            var newlyMade = states.Where(state => state.Made && !madeBefore.Contains(state.Index)).ToList();
            var waiting = states.Where(state => !state.Made).ToList();
            var nearestWaiting = waiting.Count == 0 ? double.MaxValue : waiting.Min(state => state.Distance);
            var outOfOrder = newlyMade.Count(state => state.Distance > nearestWaiting + 1);
            Check($"the next frame makes the names nearest the middle of the view ({newlyMade.Count} made, {outOfOrder} of them farther out than one still waiting)",
                newlyMade.Count > 0 && outOfOrder == 0);

            var frames = 1;
            var longest = loop.LastLabelsMilliseconds;
            while (loop.LabelsLeftWaiting && frames < 400)
            {
                loop.RenderLabelsForTests(inMotion: false);
                longest = Math.Max(longest, loop.LastLabelsMilliseconds);
                frames++;
            }

            Check($"the view's {wanted:N0} texts are made over {frames} frames of at most {longest:F1} ms, where making them at once took {allAtOnce:F1} ms",
                !loop.LabelsLeftWaiting && longest < allAtOnce / 2);

            // The frames above made the names the worker had not made yet
            // themselves; what it was still making comes back all the same.
            var waited = Stopwatch.StartNew();
            while (loop.TextsAskedOfWorker > 0 && waited.ElapsedMilliseconds < 10_000)
            {
                Thread.Sleep(5);
                loop.RenderLabelsForTests(inMotion: false);
            }

            Check($"the names past the frames' allowance are laid out and recorded on the text worker, and taken in ({loop.PreparedTextsTaken:N0} of them, {loop.TextsAskedOfWorker} still asked)",
                loop.PreparedTextsTaken > 0 && loop.TextsAskedOfWorker == 0);
        }
        finally
        {
            loop.Tree = null;
        }

        LabelCostPacedChecks(tree, many, lookUpIcon);
    }

    /// <summary>
    /// The same view in the canvas's own loop, its frames 8.33 ms apart as at
    /// 120 Hz: the names a frame has no allowance for come from the text
    /// worker, and are drawn in as they come at most once every
    /// <see cref="FrameBudgets.IconRefreshMinMs"/>, not every frame, with the
    /// frames between drawing nothing - once the camera is still, whether the
    /// canvas has settled yet or not.
    /// </summary>
    private static void LabelCostPacedChecks(NestedTree tree, NestedFolder many, Func<NestedFolder, int, ImageSource?> lookUpIcon)
    {
        // The camera still from the first frame: the first label layer leaves
        // names waiting, the next lays out those nearest the middle, and the
        // worker makes the rest.
        var still = LabelCostCanvas(tree, many, lookUpIcon);
        try
        {
            // Long enough after the camera was placed that no frame is drawn for motion.
            Thread.Sleep(200);
            var run = LabelCostPacedRun(still, settle: false);
            var gaps = run.Layers.Zip(run.Layers.Skip(1), (first, second) => second.Time - first.Time).ToList();
            var workerGaps = gaps.Skip(1).ToList();
            var closest = workerGaps.Count == 0 ? double.NaN : workerGaps.Min();
            Check($"in the loop at rest the worker's names are drawn in {run.Layers.Count - 2} label layers at least {closest:F0} ms apart, after the first two {(gaps.Count > 0 ? gaps[0] : double.NaN):F0} ms apart; {run.Drawn} of {run.Frames} frames drew anything, every name was in after {(run.Layers.Count > 0 ? run.Layers[^1].Time : double.NaN):F0} ms, and no layer took over {run.Longest:F1} ms or {run.Largest / 1024.0:N0} KB",
                run.Done && run.Layers.Count >= 3 && gaps[0] < 10
                && workerGaps.All(gap => gap >= FrameBudgets.IconRefreshMinMs - 0.01) && run.Drawn < run.Frames / 2);
        }
        finally
        {
            still.Tree = null;
        }

        // A zoom into the same view: while the camera moves every frame draws
        // the names and makes what its allowance lets it; from the moment it
        // stops, through the frames that keep the motion look up to the settle
        // and on, a layer drawn for the names alone either comes straight after
        // one that asked for it - the settle's, or the first to leave names
        // waiting - or waits its turn behind the worker.
        var zoomed = LabelCostCanvas(tree, many, lookUpIcon);
        try
        {
            var run = LabelCostPacedRun(zoomed, settle: true);
            var stopped = run.Layers.Where(layer => !layer.WhileMoving).ToList();
            var moving = run.Layers.Count - stopped.Count;
            var namesOnly = new List<double>();
            for (var index = 1; index < run.Layers.Count; index++)
            {
                if (!run.Layers[index].WhileMoving && !run.Layers[index].WithScene)
                {
                    namesOnly.Add(run.Layers[index].Time - run.Layers[index - 1].Time);
                }
            }

            var early = namesOnly.Count(gap => gap > 9 && gap < FrameBudgets.IconRefreshMinMs - 0.01);
            Check($"after a zoom, the names are drawn in {moving} layers while the camera moves and {stopped.Count} once it stops, the {namesOnly.Count} of those for the names alone each straight after the layer before or at least {FrameBudgets.IconRefreshMinMs:F0} ms behind it ({early} sooner); every name was in after {(run.Layers.Count > 0 ? run.Layers[^1].Time : double.NaN):F0} ms, and the loop let go",
                run.Done && moving > 0 && stopped.Count is > 0 and <= 8 && early == 0 && !zoomed.IsFrameHooked);
        }
        finally
        {
            zoomed.Tree = null;
        }
    }

    /// <summary>A label layer of a paced run: its frame time from the run's start, whether the camera was moving, and whether the cells were drawn with it.</summary>
    private readonly record struct LabelCostLayer(double Time, bool WhileMoving, bool WithScene);

    /// <summary>What a paced run of the loop drew: its label layers, its frames, how many drew anything, the longest and largest label layer, and whether every name came in.</summary>
    private sealed record LabelCostRun(List<LabelCostLayer> Layers, int Frames, int Drawn, double Longest, long Largest, bool Done);

    /// <summary>
    /// Frames of <paramref name="canvas"/>'s loop 8.33 ms apart, with as much
    /// real time between them - so the worker makes names at its own pace
    /// against the frames' - until every name on screen is in and nothing is
    /// asked of the worker, and with <paramref name="settle"/> until the loop
    /// lets go as well.
    /// </summary>
    private static LabelCostRun LabelCostPacedRun(NestedCanvas canvas, bool settle)
    {
        canvas.FramesByHandForTests = true;
        var start = TimeSpan.FromSeconds(1_000);
        var time = start;
        var layers = new List<LabelCostLayer>();
        var frames = 0;
        var drawn = 0;
        var longest = 0.0;
        var largest = 0L;
        var done = false;
        while (frames < 1_200)
        {
            canvas.RunFrameForTests(time);
            frames++;
            var layered = canvas.LastFrameLayers;
            if ((layered & NestedCanvas.Layers.Labels) != 0)
            {
                layers.Add(new LabelCostLayer((time - start).TotalMilliseconds, canvas.LabelsDrawnWhileMoving, (layered & NestedCanvas.Layers.Scene) != 0));
                longest = Math.Max(longest, canvas.LastLabelsMilliseconds);
                largest = Math.Max(largest, canvas.LastAllocations.Labels);
            }

            if (layered != NestedCanvas.Layers.None)
            {
                drawn++;
            }

            done = layers.Count > 1 && !canvas.LabelsLeftWaiting && canvas.TextsAskedOfWorker == 0;
            if (done && (!settle || !canvas.IsFrameHooked))
            {
                break;
            }

            Thread.Sleep(8);
            time += TimeSpan.FromMilliseconds(1_000.0 / 120);
        }

        return new LabelCostRun(layers, frames, drawn, longest, largest, done);
    }

    // ---- placing folders -------------------------------------------------------------------------

    private static void LabelCostPlacingChecks(NestedTree tree)
    {
        var drive = tree.Find(@"Q:\")!;
        var hiding = tree.Find(@"Q:\hiding")!;
        Check("a folder whose sub-folders are all shown, in the listing's order, shows the listing itself",
            ReferenceEquals(drive.Children, drive.AllChildren) && hiding.Children.Count == 1 && !ReferenceEquals(hiding.Children, hiding.AllChildren));

        var before = hiding.Children;
        tree.IncludeHidden = true;
        var withHidden = hiding.Children;
        tree.IncludeHidden = false;
        Check("and placing folders again keeps the list of one whose shown sub-folders did not change",
            ReferenceEquals(drive.Children, drive.AllChildren) && ReferenceEquals(withHidden, hiding.AllChildren)
            && hiding.Children.Count == 1 && hiding.Children[0].Name == "shown" && !ReferenceEquals(before, withHidden));

        tree.IncludeHidden = true;
        var watch = Stopwatch.StartNew();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        tree.IncludeHidden = false;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Check($"hiding hidden items again across the tree makes no list per folder ({allocated:N0} bytes for {drive.AllChildren.Length + 3} folders, {watch.Elapsed.TotalMilliseconds:F1} ms)",
            allocated < 4_000);
    }
}
