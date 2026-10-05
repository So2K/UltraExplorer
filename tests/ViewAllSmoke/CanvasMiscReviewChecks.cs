using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// What the review found in the canvas's filter, frame and scene, and what
/// was done about it: a filter over a tree read for hours is judged a slice
/// at a time rather than in one long stall, with the same matches in the
/// same order and the same lights and fades at the end; the last match
/// under a folder deleted takes the light off the folders above it too;
/// a folder above the one in view read again, which moves everything around
/// that one, redraws the whole picture rather than its old cell; and a
/// folder's redraw that finds the GPU let go draws the names again with WPF.
/// </summary>
internal static partial class Program
{
    private static Task CanvasMiscReviewChecks()
    {
        RunOnSta("canvas review: a filter over a big tree", CanvasMiscFilterSlicesAsync);
        RunOnSta("canvas review: the filter's last match gone", CanvasMiscLastMatchGoneAsync);
        RunOnSta("canvas review: a folder above the view read again", CanvasMiscAncestorReadAsync);
        RunOnSta("canvas review: the GPU let go in a folder's redraw", CanvasMiscGpuLetGoAsync);
        return Task.CompletedTask;
    }

    // ---- a filter over a big tree ----------------------------------------------------------

    private const int CanvasMiscGroups = 40;
    private const int CanvasMiscBoxes = 50;
    private const int CanvasMiscFiles = 2_000;

    /// <summary>
    /// Forty folders of fifty, each holding two thousand files - four million
    /// names, as a window that has browsed a few big drives holds.  The names
    /// are the same in every folder, which keeps the test's memory small; the
    /// filter judges each one all the same.
    /// </summary>
    private static async Task CanvasMiscFilterSlicesAsync()
    {
        Section("canvas review: a filter over four million names is judged a slice at a time");
        var shared = Enumerable.Range(0, CanvasMiscFiles).Select(index => new NestedFile($"n{index:D4}.dat", false, index)).ToArray();
        var changed = new ConcurrentDictionary<string, NestedFile[]>(StringComparer.OrdinalIgnoreCase);
        var removed = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var disk = new FakeDisk
        {
            Hook = (path, _) =>
            {
                var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 1)
                {
                    return new NestedListing(Enumerable.Range(0, CanvasMiscGroups).Select(group => new NestedEntry($"g{group:D2}", false, false)).ToList(), 0, 0, false);
                }

                if (parts.Length == 2)
                {
                    var boxes = Enumerable.Range(0, CanvasMiscBoxes)
                        .Select(box => $"b{box:D2}")
                        .Where(box => !removed.ContainsKey(path + @"\" + box))
                        .Select(box => new NestedEntry(box, false, false))
                        .ToList();
                    return new NestedListing(boxes, 0, 0, false);
                }

                var files = changed.TryGetValue(path, out var own) ? own : shared;
                return new NestedListing([], files.Length, 0, false) { Files = files };
            }
        };

        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        var canvas = new NestedCanvas { Tree = tree };
        canvas.Measure(new Size(1200, 800));
        canvas.Arrange(new Rect(0, 0, 1200, 800));
        canvas.UpdateLayout();
        canvas.FitAll(animated: false);
        const string pattern = "*77.dat";
        static bool Matches(string name) => name.EndsWith("77.dat", StringComparison.OrdinalIgnoreCase);
        try
        {
            // ---- the thread is held for a slice, not for the whole tree --------
            // The first filter in the process also makes its pattern and the
            // code that runs it: that judging is not the one timed.
            var names = CanvasMiscGroups * CanvasMiscBoxes * (CanvasMiscFiles + 1);
            canvas.SetFilter(pattern);
            await CanvasMiscJudgedAsync(canvas);
            canvas.SetFilter(null);
            var started = Stopwatch.GetTimestamp();
            canvas.SetFilter(pattern);
            var held = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var widestGap = await CanvasMiscJudgedAsync(canvas);
            var total = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Check($"a filter over {names:N0} names holds the thread {held:F0} ms of the {total:F0} ms it takes, the rest judged in slices with the thread free at least every {widestGap:F0} ms",
                total > 3 * held && widestGap < 100);

            var expected = new List<string>();
            CanvasMiscExpectedMatches(tree.Root, Matches, expected);
            Check($"and the matches are the ones one pass finds, in its order ({canvas.FilterMatches.Count:N0} of {expected.Count:N0})",
                canvas.FilterMatches.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase));
            var judged = CanvasMiscFilterStates(tree, stamped: true);
            canvas.FolderLoadedForTests(tree.Root);
            Check("and every folder is lit or faded as one pass judges it",
                CanvasMiscSameStates(judged, CanvasMiscFilterStates(tree, stamped: true)) && canvas.FilterMatches.Count == expected.Count);

            // ---- reads that land while it is judged ------------------------------
            // One folder early in the walk loses its matches, one late in it
            // gains one, and a folder in the middle loses a sub-folder.
            canvas.SetFilter(null);
            canvas.SetFilter(pattern);
            var early = tree.Find(@"Q:\g00\b01")!;
            var late = tree.Find(@"Q:\g39\b48")!;
            var middle = tree.Find(@"Q:\g20")!;
            changed[early.FullPath] = shared.Where(file => !Matches(file.Name)).ToArray();
            changed[late.FullPath] = [.. shared, new NestedFile("extra-77.dat", false, 1)];
            removed[@"Q:\g20\b10"] = true;
            var pendingBefore = canvas.HasPendingWork;
            await Task.WhenAll(tree.RefreshAsync(early), tree.RefreshAsync(late), tree.RefreshAsync(middle));
            var pendingAfter = canvas.HasPendingWork;
            await CanvasMiscJudgedAsync(canvas);
            expected.Clear();
            CanvasMiscExpectedMatches(tree.Root, Matches, expected);
            var found = canvas.FilterMatches.ToHashSet(StringComparer.OrdinalIgnoreCase);
            Check($"reads landing while it is judged ({pendingBefore}, {pendingAfter}) leave exactly the matches there are ({found.Count:N0} of {expected.Count:N0})",
                pendingBefore && pendingAfter && found.SetEquals(expected) && canvas.FilterMatches.Count == expected.Count);
            judged = CanvasMiscFilterStates(tree, stamped: true);
            canvas.FolderLoadedForTests(tree.Root);
            Check("and every folder lit or faded as one pass over the tree as it now is judges it",
                CanvasMiscSameStates(judged, CanvasMiscFilterStates(tree, stamped: true)));
        }
        finally
        {
            canvas.SetFilter(null);
            canvas.Tree = null;
        }
    }

    /// <summary>Waits for the canvas to finish judging its filter; returns the longest the thread was kept from this loop meanwhile, in milliseconds.</summary>
    private static async Task<double> CanvasMiscJudgedAsync(NestedCanvas canvas)
    {
        var widest = 0.0;
        var limit = Stopwatch.GetTimestamp() + 60 * Stopwatch.Frequency;
        var last = Stopwatch.GetTimestamp();
        while (canvas.HasPendingWork && Stopwatch.GetTimestamp() < limit)
        {
            await Task.Delay(1);
            var now = Stopwatch.GetTimestamp();
            widest = Math.Max(widest, (now - last) * 1000.0 / Stopwatch.Frequency);
            last = now;
        }

        return widest;
    }

    /// <summary>What one pass over the tree finds, in its order: each folder, then its files, then its sub-folders.</summary>
    private static void CanvasMiscExpectedMatches(NestedFolder folder, Func<string, bool> matches, List<string> into)
    {
        if (!folder.IsComputer && matches(folder.Name))
        {
            into.Add(folder.FullPath);
        }

        foreach (var file in folder.Files)
        {
            if (matches(file.Name))
            {
                into.Add(folder.PathOf(file));
            }
        }

        foreach (var child in folder.Children)
        {
            CanvasMiscExpectedMatches(child, matches, into);
        }
    }

    /// <summary>
    /// How the filter judged every folder on the canvas, This PC included -
    /// and, when <paramref name="stamped"/>, under which filter, so a folder
    /// left unjudged since an earlier one is told from one judged anew.
    /// </summary>
    private static Dictionary<NestedFolder, (int Stamp, int State)> CanvasMiscFilterStates(NestedTree tree, bool stamped = false)
    {
        var states = new Dictionary<NestedFolder, (int Stamp, int State)>();
        var pending = new Stack<NestedFolder>([tree.Root]);
        while (pending.TryPop(out var folder))
        {
            states[folder] = (stamped ? folder.FilterStamp : 0, folder.FilterState);
            foreach (var child in folder.Children)
            {
                pending.Push(child);
            }
        }

        return states;
    }

    private static bool CanvasMiscSameStates(Dictionary<NestedFolder, (int Stamp, int State)> a, Dictionary<NestedFolder, (int Stamp, int State)> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var state) && state == pair.Value);

    // ---- the last match under a folder gone --------------------------------------------------

    /// <summary>
    /// The only match under a folder deleted - a file, then a whole folder -
    /// and the folders above it judged as a filter typed afresh judges them:
    /// faded, unless they still hold another match.
    /// </summary>
    private static async Task CanvasMiscLastMatchGoneAsync()
    {
        Section("canvas review: the folders above the filter's last match fade with it");
        var disk = new FakeDisk();
        disk.AddFile(@"Q:\photos\deep", "only-one.png", 10);
        disk.AddFile(@"Q:\photos", "notes.txt", 10);
        disk.AddFile(@"Q:\albums\kept", "only-two.png", 10);
        disk.AddFile(@"Q:\other", "plain.txt", 10);
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        var canvas = new NestedCanvas { Tree = tree };
        canvas.Measure(new Size(1200, 800));
        canvas.Arrange(new Rect(0, 0, 1200, 800));
        canvas.UpdateLayout();
        try
        {
            canvas.SetFilter("only");
            var before = canvas.FilterMatches.Count;
            disk.Folder(@"Q:\photos\deep").Files.Clear();
            await tree.RefreshAsync(tree.Find(@"Q:\photos\deep")!);
            var afterFile = CanvasMiscFilterStates(tree);
            var leftAfterFile = canvas.FilterMatches.Count;
            canvas.SetFilter(null);
            canvas.SetFilter("only");
            Check($"a folder's only match deleted takes the light off the folders above it that held nothing else ({before} matches, then {leftAfterFile})",
                before == 2 && leftAfterFile == 1 && CanvasMiscSameStates(afterFile, CanvasMiscFilterStates(tree)));

            disk.Remove(@"Q:\albums");
            await tree.RefreshAsync(tree.Find(@"Q:\")!);
            var afterFolder = CanvasMiscFilterStates(tree);
            var leftAfterFolder = canvas.FilterMatches.Count;
            canvas.SetFilter(null);
            canvas.SetFilter("only");
            Check($"and the folder holding the last match deleted takes it off every folder above ({leftAfterFolder} left)",
                leftAfterFolder == 0 && CanvasMiscSameStates(afterFolder, CanvasMiscFilterStates(tree)));
        }
        finally
        {
            canvas.SetFilter(null);
            canvas.Tree = null;
        }
    }

    // ---- a folder above the view read again ----------------------------------------------------

    /// <summary>
    /// The view on a folder that is its parent's only one, its top left
    /// corner near the middle of the view, so the parent's cell covers less
    /// than half of it: the parent read again with a second sub-folder places
    /// the one in view smaller, and the camera, holding that folder still,
    /// puts the parent and everything round it somewhere else.  The frame
    /// that follows paints what a whole frame paints, and draws the names and
    /// marks again with it.
    /// </summary>
    private static async Task CanvasMiscAncestorReadAsync()
    {
        Section("canvas review: a folder above the view read again redraws the whole picture");
        var disk = new FakeDisk();
        disk.AddFiles(@"Q:\holder\view", 40, "v");
        disk.AddFiles(@"Q:\side", 12, "s");
        disk.AddFiles(@"Q:\", 6, "q");
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);

        var canvas = new NestedCanvas { FramesByHandForTests = true, DpiOverride = new DpiScale(1.5, 1.5), Tree = tree };
        canvas.Measure(new Size(800, 1000));
        canvas.Arrange(new Rect(0, 0, 800, 1000));
        canvas.UpdateLayout();
        var time = TimeSpan.FromSeconds(200);
        try
        {
            var holder = tree.Find(@"Q:\holder")!;
            var view = tree.Find(@"Q:\holder\view")!;
            canvas.FlyTo(view, 0.52, animated: false);
            canvas.Pan(new Vector(200, 125));
            time = await RunUntilStillAsync(canvas, time);
            var holderBefore = canvas.ScreenRectOf(holder)!.Value;
            var viewBefore = canvas.ScreenRectOf(view)!.Value;
            var shown = new Rect(0, 0, 800, 1000);
            var holderShown = Rect.Intersect(holderBefore, shown);
            Check($"the view is on {view.Name}, its parent's cell less than half the view ({holderShown.Width * holderShown.Height / (800 * 1000):P0})",
                ReferenceEquals(canvas.Anchor, view) && holderShown.Width * holderShown.Height * 2 < 800 * 1000);

            disk.AddFiles(@"Q:\holder\second", 3, "w");
            await tree.RefreshAsync(holder);
            canvas.RunFrameForTests(time += TimeSpan.FromMilliseconds(40));
            var layers = canvas.LastFrameLayers;
            var partial = ScenePixels(canvas);
            var holderAfter = canvas.ScreenRectOf(holder)!.Value;
            var viewAfter = canvas.ScreenRectOf(view)!.Value;
            canvas.Redraw();
            canvas.RunFrameForTests(time += Frame);
            var full = ScenePixels(canvas);
            var differ = 0;
            for (var index = 0; index < full.Pixels.Length; index++)
            {
                differ += full.Pixels[index] != partial.Pixels[index] ? 1 : 0;
            }

            Check($"read again with a second sub-folder, the parent moves round the folder held still ({holderBefore} to {holderAfter})",
                viewAfter == viewBefore && holderAfter != holderBefore);
            Check($"and the frame after paints what a whole frame paints ({differ} pixels differ) and draws every layer ({layers})",
                differ == 0 && layers == NestedCanvas.Layers.All);

            // Read again with nothing new, the parent places the folder in
            // view where it was, and nothing round it moves.
            await tree.RefreshAsync(holder);
            canvas.RunFrameForTests(time += TimeSpan.FromMilliseconds(40));
            Check($"read again with nothing new, it moves nothing, and the frame draws only what any folder read draws ({canvas.LastFrameLayers})",
                canvas.ScreenRectOf(holder) == holderAfter && (canvas.LastFrameLayers & NestedCanvas.Layers.Scene) != 0
                && canvas.LastFrameLayers != NestedCanvas.Layers.All);
        }
        finally
        {
            canvas.Tree = null;
        }
    }

    // ---- the GPU let go in a folder's redraw ---------------------------------------------------

    /// <summary>
    /// A canvas in a window - cloaked, never activated, on the secondary
    /// monitor - drawing its cells and names on the GPU.  The GPU is then let
    /// go of between two frames without a word to the canvas, as when the
    /// other pane's card is lost a third time and the CPU draws for the rest
    /// of the session; the next frame is only a folder's redraw, which draws
    /// no names of its own.  The names, which were only in the GPU's picture,
    /// are drawn again with WPF in that same frame.
    /// </summary>
    private static async Task CanvasMiscGpuLetGoAsync()
    {
        Section("canvas review: the names stay when the GPU is let go in a folder's redraw");
        if (TestScreen.Target() is not { } target)
        {
            Console.WriteLine("  (no secondary monitor: skipped)");
            return;
        }

        var disk = new FakeDisk();
        disk.AddFiles(@"Q:\named", 12, "n");
        for (var index = 0; index < 120; index++)
        {
            disk.Folder($@"Q:\big\s{index:D3}\a");
            disk.AddFiles($@"Q:\big\s{index:D3}", 4, "f");
        }

        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await tree.LoadAsync(tree.Find(@"Q:\")!);
        await tree.LoadAsync(tree.Find(@"Q:\big")!);
        await tree.LoadAsync(tree.Find(@"Q:\named")!);

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
            Title = "UltraExplorer canvas review: the GPU let go",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Width = 800,
            Height = 500
        };

        var rendererBefore = GpuBootstrap.Preference;
        try
        {
            using (ActivationGuard.GuardWindowsCreated())
            {
                var handle = new WindowInteropHelper(window).EnsureHandle();
                DialogNative.CloakOwn(handle, true);
                SetWindowPos(handle, 0, target.Work.Left + 40, target.Work.Top + 40, 800, 500, 0x0004 | 0x0010); // no z-order change, no activation
                window.Show();
            }

            canvas.FlyTo(tree.Find(@"Q:\big")!, 0.92, animated: false);
            if (!await Until(() => canvas.IsSceneOnGpu && canvas.AreLabelsOnGpu && canvas.IsIdle, 30_000))
            {
                Console.WriteLine($"  (the GPU did not draw the window's names - {canvas.RendererReason}: skipped)");
                return;
            }

            var small = tree.Find(@"Q:\big\s005")!;
            var names = canvas.LabelLayerCount;
            GpuBootstrap.UseSavedPreference(RendererPreference.Cpu);
            await tree.LoadAsync(small);
            var drawn = await Until(() => !canvas.IsSceneOnGpu && canvas.IsIdle, 5_000);
            Check($"a folder read once the GPU is let go is drawn on the CPU, and the names with it ({canvas.LastFrameLayers}, names drawn {canvas.LabelLayerCount - names} times, still on the GPU: {canvas.AreLabelsOnGpu})",
                drawn && !canvas.AreLabelsOnGpu && canvas.LabelLayerCount > names);
        }
        finally
        {
            GpuBootstrap.UseSavedPreference(rendererBefore);
            window.Close();
            canvas.Tree = null;
        }
    }
}
