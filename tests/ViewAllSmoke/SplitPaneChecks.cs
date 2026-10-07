using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// Two nested canvases in one window, each drawing a tree of its own - the
/// panes of a split view - before any pane is shown to the user: a slot two
/// canvases drive goes to the other when one lets go; two canvases on one
/// graphics card keep buffers of their own and copy nothing twice; and on a
/// dispatcher, with real folders and the real change hub, both panes hear of
/// the icons either asked for, a change on disk reaches only the tree whose
/// folder changed, exactly one pane takes the hub's changes in, a pane left
/// still keeps reading while the other moves, and closing one pane leaves
/// the other working - hub, icons and all.  With them, the window's own
/// pane before any split (<see cref="SplitPaneWindowChecks"/>, run with the
/// Settings checks, which have the window).
/// </summary>
internal static partial class Program
{
    private static Task SplitPaneChecks()
    {
        Section("split panes");
        SplitSlotChecks();
        SplitGpuBufferChecks();
        RunOnSta("split panes: slots on canvases", SplitCanvasSlotChecksAsync);
        RunOnSta("split panes: one pane idle", SplitIdleReadChecksAsync);
        RunOnSta("split panes: hub, icons and closing", SplitLiveChecksAsync);
        SplitModelChecks();
        RunOnSta("split panes: what the workspace keeps", SplitStateChecksAsync);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Each pane's own Back and Forward, and the words its header names its
    /// folder in: the drive first and the last three steps of a deep one.
    /// </summary>
    private static void SplitModelChecks()
    {
        Section("split panes: history and the header");
        var history = new NavigationHistory();
        history.Record(@"C:\a");
        history.Record(@"C:\a");
        history.Record(@"C:\b");
        history.Record(@"C:\c");
        Check("a pane's history records each place once in a row, and steps back and forward through them",
            history.Count == 3 && history.Back() == @"C:\b" && history.Back() == @"C:\a" && history.Back() is null
            && history.Forward() == @"C:\b" && history.Current == @"C:\b");
        history.Record(@"C:\d");
        Check("going somewhere after a step back lets go of what was ahead, as a browser does",
            history.Count == 3 && !history.CanGoForward && history.Recent().SequenceEqual([@"C:\d", @"C:\b", @"C:\a"]));

        Check("the header names a folder by its steps, the drive first",
            NestedPane.ShortLocation(@"C:\Users\Me") == "C:  ›  Users  ›  Me");
        Check("a deep one by the drive and its last three, so the folder itself is never what is cut",
            NestedPane.ShortLocation(@"C:\Users\Me\Documents\Deep\Deeper") == "C:  ›  …  ›  Documents  ›  Deep  ›  Deeper");
        Check("and no folder at all is This PC", NestedPane.ShortLocation(null) == "This PC" && NestedPane.ShortLocation(@"D:\") == "D:");
    }

    /// <summary>
    /// What the workspace keeps of a split view, and that a workspace from
    /// before there was one still loads: the split itself with the window's
    /// settings, the second pane's place and selection with the canvas's -
    /// each pane's own, whichever was being worked with - and a pane not
    /// being worked with losing what is deleted or found gone, as the one
    /// being worked with does.  A file dialog never splits.
    /// </summary>
    private static async Task SplitStateChecksAsync()
    {
        Section("split panes: what the workspace keeps");
        var baseDirectory = Path.Combine(Path.GetTempPath(), "UltraExplorerSplitState", Guid.NewGuid().ToString("N"));
        var left = Path.Combine(baseDirectory, "left");
        var right = Path.Combine(baseDirectory, "right");
        var gone = Path.Combine(left, "gone.txt");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(Path.Combine(right, "inner"));
        File.WriteAllText(gone, "g");
        try
        {
            var store = new WorkspaceStore(Path.Combine(baseDirectory, "workspace.json"));
            await store.SaveAsync(new WorkspaceState { IsSplit = true, SplitOrientation = "Stacked", SplitRatio = 0.3, ActivePane = 1 });
            var loaded = await store.LoadAsync();
            Check("the workspace keeps the split: on, stacked, the divider where it was and the pane being worked with",
                loaded is { IsSplit: true, SplitOrientation: "Stacked", ActivePane: 1 } && Math.Abs(loaded.SplitRatio - 0.3) < 1e-9);

            var oldPath = Path.Combine(baseDirectory, "old-workspace.json");
            File.WriteAllText(oldPath, "{ \"SchemaVersion\": 2, \"SidebarWidth\": 260, \"CanvasLayout\": \"Nested\" }");
            var old = await new WorkspaceStore(oldPath).LoadAsync();
            Check("a workspace from before the split loads unsplit: side by side, half each, the first pane worked with",
                old is { IsSplit: false, ActivePane: 0, SidebarWidth: 260 }
                && SplitLayout.ParseOrientation(old.SplitOrientation) == SplitOrientation.SideBySide
                && SplitLayout.ClampRatio(old.SplitRatio) == SplitLayout.DefaultRatio);
            Check("the divider is kept between 0.15 and 0.85 of the room, and a ratio that is no number is half",
                SplitLayout.ClampRatio(0.05) == 0.15 && SplitLayout.ClampRatio(0.95) == 0.85
                && SplitLayout.ClampRatio(double.NaN) == 0.5 && SplitLayout.ClampRatio(0.4) == 0.4);
            Check("stacked however it is spelled, and side by side for anything else",
                SplitLayout.ParseOrientation(" stacked ") == SplitOrientation.Stacked
                && SplitLayout.ParseOrientation("diagonal") == SplitOrientation.SideBySide
                && SplitLayout.ParseOrientation(null) == SplitOrientation.SideBySide);

            var shell = new FakeShell();
            using var icons = new ShellIconService(shell.Extract, Dispatcher.CurrentDispatcher);
            var marks = new FolderMarkService(Path.Combine(baseDirectory, "marks.json"));
            var treePath = Path.Combine(baseDirectory, "view-all.workspace.json");
            var camera = new NestedCameraState(right, 0.1, -0.2, 0.9);
            using (var tree = new ViewAllViewModel(marks, icons, treePath) { PreferLightReveal = true })
            {
                await tree.InitializeAsync(left);

                // The second pane is being worked with: the selection is its,
                // and the first pane says what it has.
                tree.SecondPane = new NestedPaneState(null, camera);
                tree.OtherPanePath = () => left;
                tree.IsSecondPaneActive = true;
                tree.Selection.ReplaceSingle(right, true, 0, SelectionSource.Navigation);
                await LiveWait(() => ViewAllPath.Equals(tree.ActivePath, right), 5_000);

                // A pane not being worked with keeps its selection, which a
                // delete and a folder found without the item leave.
                var kept = new ItemSelection();
                kept.ReplaceSingle(gone, false, 1, SelectionSource.Navigation);
                tree.AddKeptSelection(kept);
                tree.AddKeptSelection(kept);
                Check("a pane's kept selection is followed once", tree.KeptSelectionCount == 1);
                File.Delete(gone);
                await tree.RefreshPathAsync(left);
                Check("an item found gone leaves the selection of the pane not being worked with", kept.Count == 0);
                kept.ReplaceSingle(Path.Combine(right, "inner"), true, 0, SelectionSource.Navigation);
                tree.ForgetSelected([Path.Combine(right, "inner")]);
                Check("and so does one deleted or moved away from the pane being worked with", kept.Count == 0);
                tree.RemoveKeptSelection(kept);
                Check("a pane activated or closed is followed no more", tree.KeptSelectionCount == 0);

                await tree.SaveAsync();
            }

            var written = await new ViewAllWorkspaceStore(treePath).LoadAsync();
            Check("the canvas workspace keeps each pane's own selection whichever was worked with: the first's as it always has, the second's with its camera",
                written is { SecondPane: { } saved }
                && ViewAllPath.Equals(written.ActivePath, left) && ViewAllPath.Equals(saved.ActivePath ?? string.Empty, right) && saved.NestedCamera == camera);

            using (var again = new ViewAllViewModel(marks, icons, treePath) { PreferLightReveal = true, RestoresSecondPane = true })
            {
                await again.InitializeAsync();
                Check("read back, the second pane's place and camera return, and what it had selected is found - a folder",
                    again.RestoredSecondPane?.NestedCamera == camera
                    && again.RestoredSecondPaneItem is { IsDirectory: true } item && ViewAllPath.Equals(item.Path, right)
                    && again.SecondPane == again.RestoredSecondPane);
                Check("and the first pane's selection is the first pane's", ViewAllPath.Equals(again.ActivePath, left));
            }

            using (var unsplit = new ViewAllViewModel(marks, icons, treePath) { PreferLightReveal = true })
            {
                await unsplit.InitializeAsync();
                Check("a window that opens unsplit keeps the second pane's place for its next split, without looking for its selection",
                    unsplit.RestoredSecondPane?.NestedCamera == camera && unsplit.RestoredSecondPaneItem is null);
            }

            var oldTree = Path.Combine(baseDirectory, "old-view-all.workspace.json");
            File.WriteAllText(oldTree, "{ \"schemaVersion\": 1, \"nodes\": [], \"activePath\": \"\", \"nestedCamera\": { \"anchorPath\": \"\", \"x\": 0, \"y\": 0, \"width\": 1 } }");
            var oldState = await new ViewAllWorkspaceStore(oldTree).LoadAsync();
            Check("a canvas workspace from before the split loads, its camera the first pane's and no second pane",
                oldState is { SecondPane: null, NestedCamera: { Width: 1 } });

            using var picker = new MainViewModel(Path.Combine(baseDirectory, "picker.workspace.json"));
            picker.IsSplit = true;
            picker.SplitOrientation = SplitOrientation.Stacked;
            Check("a file dialog never splits", !picker.IsSplit && picker.SplitOrientation == SplitOrientation.SideBySide);
        }
        finally
        {
            TryDelete(baseDirectory);
        }
    }

    /// <summary>
    /// A slot several drivers take: the last to take it is woken, one that
    /// gives it back hands it to the one before it rather than to the
    /// fallback, and one that yields - a hidden pane - lets the other be
    /// woken while it keeps its place.  A driver alone in the slot is the
    /// single field it always was.
    /// </summary>
    private static void SplitSlotChecks()
    {
        var fallback = new CountingDriver();
        var first = new CountingDriver();
        var second = new CountingDriver();
        var slot = new FrameDriverSlot(fallback);

        slot.Claim(first);
        slot.Claim(second);
        slot.Wake();
        Check("two drivers take a slot: the last to take it is woken",
            ReferenceEquals(slot.Active, second) && slot.DriverCount == 2 && second.Wakes == 1 && first.Wakes == 0 && fallback.Wakes == 0);

        slot.Release(second);
        slot.Wake();
        Check("the last lets go: the one before it is woken, not the fallback",
            ReferenceEquals(slot.Active, first) && first.Wakes == 1 && fallback.Wakes == 0);

        slot.Claim(second);
        slot.Yield(second);
        Check("a driver that yields keeps the slot, and the other is woken", ReferenceEquals(slot.Active, first) && slot.IsClaimedBy(second));
        slot.Claim(second);
        Check("and it takes the slot back when it claims it again", ReferenceEquals(slot.Active, second) && slot.DriverCount == 2);

        slot.Release(first);
        slot.Release(second);
        slot.Wake();
        Check("with every driver gone the fallback is woken", slot.Active is null && fallback.Wakes == 1);

        slot.Claim(first);
        slot.Yield(first);
        slot.Release(second);
        Check("a driver alone in a slot stays its driver when it yields, and a release by one without it changes nothing",
            ReferenceEquals(slot.Active, first) && slot.DriverCount == 1);

        slot.Active = second;
        Check("setting the active driver makes it the only one", ReferenceEquals(slot.Active, second) && slot.DriverCount == 1 && !slot.IsClaimedBy(first));
        slot.Active = null;
        Check("and setting none leaves the slot to its fallback", slot.Active is null && slot.DriverCount == 0);
    }

    /// <summary>
    /// Two canvases on one card, each with buffers of its own, draw in turn:
    /// neither copies a list again that it copied last time, and the picture
    /// is the one the renderer's own buffers give - where the two taking
    /// turns with one set of buffers would copy everything every time.
    /// </summary>
    private static void SplitGpuBufferChecks()
    {
        CompiledShaders shaders;
        GpuDeviceSet set;
        try
        {
            shaders = ShaderCache.Load(null);
            set = GpuDeviceSet.CreateWarp();
        }
        catch (Exception ex)
        {
            Check($"a WARP set draws the split panes' buffer checks ({ex.Message})", false);
            return;
        }

        using (set)
        {
            const int size = 128;
            var renderer = NestedGpuRenderer.For(set, shaders);
            using var target = set.CreateOffscreenTarget(size, size);
            using var left = SplitGpuFrame(size, 0xFF3A4450);
            using var right = SplitGpuFrame(size, 0xFF60CDFF);
            using var leftBuffers = new NestedGpuRenderer.Instances(set);
            using var rightBuffers = new NestedGpuRenderer.Instances(set);

            renderer.Draw(target.RenderTargetView, size, size, left, leftBuffers);
            var leftFirst = renderer.LastUploadBytes;
            var leftPixels = target.ReadPixels();
            renderer.Draw(target.RenderTargetView, size, size, right, rightBuffers);
            var rightFirst = renderer.LastUploadBytes;
            renderer.Draw(target.RenderTargetView, size, size, left, leftBuffers);
            var leftAgain = renderer.LastUploadBytes;
            var leftPixelsAgain = target.ReadPixels();
            renderer.Draw(target.RenderTargetView, size, size, right, rightBuffers);
            var rightAgain = renderer.LastUploadBytes;
            Check($"two canvases on one card with buffers of their own copy their lists once ({leftFirst} and {rightFirst} bytes) and then nothing ({leftAgain}, {rightAgain})",
                leftFirst > 0 && rightFirst > 0 && leftAgain == 0 && rightAgain == 0);

            renderer.Draw(target.RenderTargetView, size, size, left);
            renderer.Draw(target.RenderTargetView, size, size, right);
            renderer.Draw(target.RenderTargetView, size, size, left);
            var sharedAgain = renderer.LastUploadBytes;
            var sharedPixels = target.ReadPixels();
            Check($"where taking turns with one set of buffers copies everything again ({sharedAgain} bytes)", sharedAgain == leftFirst);
            Check("and the picture is the same pixel for pixel either way",
                leftPixels.AsSpan().SequenceEqual(leftPixelsAgain) && leftPixels.AsSpan().SequenceEqual(sharedPixels));

            using var elsewhere = GpuDeviceSet.CreateWarp();
            using var foreign = new NestedGpuRenderer.Instances(elsewhere);
            var refused = false;
            try
            {
                renderer.Draw(target.RenderTargetView, size, size, left, foreign);
            }
            catch (ArgumentException)
            {
                refused = true;
            }

            Check("buffers made on another card's set are refused", refused);
        }
    }

    /// <summary>A frame of one cell, in <paramref name="body"/>, filling most of a <paramref name="size"/> square.</summary>
    private static NestedGpuFrame SplitGpuFrame(int size, uint body)
    {
        var frame = new NestedGpuFrame(64, 16, 16);
        var sink = new GpuSink();
        sink.Begin(frame.SceneRects, size, size, 0xFF111315);
        sink.Clear(0xFF111315);
        sink.Cell(4.5, 4.5, size - 8.5, size - 20.5, 6, 22.5, true, 10, 9, 13, 19, body, 0xFF1C2127, 0xFF252B33, 0xFF60CDFF);
        frame.ClearColour = sink.ClearColour;
        frame.SceneChanged();
        return frame;
    }

    /// <summary>
    /// Real canvases driving one slot, as the two panes drive the change
    /// hub's: the second to take it is its driver, and when it lets go the
    /// first is again, rather than the slot falling back to nobody.
    /// </summary>
    private static Task SplitCanvasSlotChecksAsync()
    {
        var fallback = new CountingDriver();
        var slot = new FrameDriverSlot(fallback);
        var first = new NestedCanvas { FramesByHandForTests = true };
        var second = new NestedCanvas { FramesByHandForTests = true };
        first.Drive(slot);
        second.Drive(slot);
        Check("two canvases drive one slot: the second is its driver", ReferenceEquals(slot.Active, second) && slot.DriverCount == 2);
        var fallbackWakes = fallback.Wakes;
        second.StopDriving(slot);
        Check("the second lets go: the first drives the slot again, and the fallback is not woken for it",
            ReferenceEquals(slot.Active, first) && slot.DriverCount == 1 && fallback.Wakes == fallbackWakes);
        first.StopDriving(slot);
        Check("the first lets go too: the slot falls back, and its fallback takes what waits", slot.Active is null && fallback.Wakes == fallbackWakes + 1);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Pane B asks for twenty folders and is then left still - no frames at
    /// all - while pane A flies across its own tree.  B's tree counts only
    /// B's frames, so its queued reads are still wanted when a slot frees up,
    /// and all twenty are read and applied without B drawing a single frame.
    /// With one tree for both panes A's flight would have made them look
    /// abandoned, and they would have been dropped unread.
    /// </summary>
    private static async Task SplitIdleReadChecksAsync()
    {
        using var hold = new ManualResetEventSlim(true);
        var diskA = SplitFakeDisk();
        var diskB = SplitFakeDisk();
        diskB.Hook = (path, token) =>
        {
            if (path.StartsWith(@"Q:\b\", StringComparison.OrdinalIgnoreCase))
            {
                hold.Wait(token);
            }

            return null;
        };

        using var treeA = new NestedTree(diskA.Read);
        using var treeB = new NestedTree(diskB.Read);
        var drive = new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive);
        treeA.SetRoots([drive]);
        treeB.SetRoots([drive]);
        await treeA.LoadAsync(treeA.Find(@"Q:\")!);
        await treeB.LoadAsync(treeB.Find(@"Q:\")!);
        var a = treeA.Find(@"Q:\a")!;
        var b = treeB.Find(@"Q:\b")!;
        await treeA.LoadAsync(a);
        await treeB.LoadAsync(b);

        // From here B's twenty folders are held on the disk: a canvas laid
        // out in no window draws there and then, and may ask for them already.
        hold.Reset();
        var paneA = SplitCanvas(treeA, 800, 500);
        var paneB = SplitCanvas(treeB, 800, 500);
        paneA.FlyTo(a, 0.9, animated: false);
        paneB.FlyTo(b, 0.9, animated: false);
        var time = TimeSpan.FromSeconds(100);
        paneA.RunFrameForTests(time);

        // B draws once more, on its folder: its twenty folders are asked for.
        paneB.RunFrameForTests(time);
        var asked = b.AllChildren.Count(child => child.LoadState is NestedLoadState.Queued or NestedLoadState.Loading);
        var bFrame = treeB.Frame;
        var bFrames = paneB.LoopFrameCount;
        Check($"pane B draws its folder once and asks for its twenty folders ({asked} waiting)", asked == 20);

        // A flies across the whole of its tree while B is left still.
        var aFrame = treeA.Frame;
        paneA.FlyTo(treeA.Root, 1, animated: true);
        for (var frame = 0; frame < 30; frame++)
        {
            await Task.Delay(1);
            time += TimeSpan.FromMilliseconds(8.33);
            paneA.RunFrameForTests(time);
        }

        Check($"pane A drew thirty frames of its flight ({treeA.Frame - aFrame} pictures) and B's tree counted none of them ({treeB.Frame - bFrame})",
            treeA.Frame - aFrame >= 20 && treeB.Frame == bFrame);

        hold.Set();
        var children = b.AllChildren;
        var took = await LiveWait(() => children.All(child => child.IsLoaded), 5_000);
        Check($"once the disk answers, all twenty of B's folders are read and applied ({children.Count(child => child.IsLoaded)} of {children.Length}, {took} ms)",
            took >= 0 && children.Length == 20);
        Check("without pane B drawing a frame", paneB.LoopFrameCount == bFrames);
    }

    /// <summary>Q:\a with files and a folder of its own, and Q:\b with twenty folders, one file in each.</summary>
    private static FakeDisk SplitFakeDisk()
    {
        var disk = new FakeDisk();
        disk.Folder(@"Q:\a\inner");
        disk.AddFiles(@"Q:\a", 6, "note");
        for (var index = 0; index < 20; index++)
        {
            var folder = $@"Q:\b\s{index:D2}";
            disk.Folder(folder);
            disk.AddFile(folder, "inside.txt", 10);
        }

        return disk;
    }

    /// <summary>A window-less canvas drawing <paramref name="tree"/>, its frames run by hand, laid out at the given size.</summary>
    private static NestedCanvas SplitCanvas(NestedTree tree, double width, double height)
    {
        var canvas = new NestedCanvas { FramesByHandForTests = true, DpiOverride = new DpiScale(1, 1), Tree = tree };
        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        canvas.UpdateLayout();
        return canvas;
    }

    /// <summary>
    /// The two panes as the window will wire them: one change hub, one
    /// view model handing its changes on, one icon service, and a tree and a
    /// canvas per pane - pane A in <c>left</c>, pane B in <c>right</c>, far
    /// apart among thirty other folders so neither ever draws the other's -
    /// on real folders the hub watches.  Frames are run by hand for both, at
    /// the same moments, as WPF runs them for every canvas in a window.
    /// </summary>
    private static async Task SplitLiveChecksAsync()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "UltraExplorerSplit", Guid.NewGuid().ToString("N"));
        var left = Path.Combine(baseDirectory, "left");
        var right = Path.Combine(baseDirectory, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        for (var index = 0; index < 30; index++)
        {
            Directory.CreateDirectory(Path.Combine(baseDirectory, $"m{index:D2}"));
        }

        for (var index = 0; index < 3; index++)
        {
            Directory.CreateDirectory(Path.Combine(right, $"r{index}"));
        }

        for (var index = 0; index < 8; index++)
        {
            File.WriteAllText(Path.Combine(left, $"left{index}.splitpane"), "l");
            File.WriteAllText(Path.Combine(right, $"right{index}.splitpane"), "r");
        }

        NestedTree? treeA = null;
        NestedTree? treeB = null;
        using var gate = new ManualResetEventSlim(false);
        try
        {
            var shell = new FakeShell { Gate = gate };
            using var icons = new ShellIconService(shell.Extract, Dispatcher.CurrentDispatcher);
            using var hub = new ChangeHub(TimeProvider.System);
            var marks = new FolderMarkService(Path.Combine(baseDirectory, "marks.json"));
            using var sink = new ViewAllViewModel(marks, icons, Path.Combine(baseDirectory, "tree.json"));
            var drive = new NestedRoot(baseDirectory, "S", NestedFolderKind.Drive);
            treeA = new NestedTree();
            treeB = new NestedTree();
            foreach (var tree in new[] { treeA, treeB })
            {
                tree.Changes = hub;
                sink.AddNestedChanges(tree);
                tree.SetRoots([drive]);
            }

            var paneA = SplitCanvas(treeA, 900, 600);
            var paneB = SplitCanvas(treeB, 900, 600);
            var inboxB = icons.SubscribeCanvas();
            icons.CanvasArrivals.Driver.Fallback = new CountingDriver();
            inboxB.Driver.Fallback = new CountingDriver();
            paneA.IconLookup = icons.GetForCanvas;
            paneB.IconLookup = icons.GetForCanvas;
            paneA.IconArrivals = icons.CanvasArrivals;
            paneB.IconArrivals = inboxB;
            paneA.AttachChanges(hub, sink);
            paneB.AttachChanges(hub, sink);
            Check("both panes' trees hear from the view model, and both canvases have an inbox for icons",
                sink.NestedChanges.Count == 2 && icons.CanvasInboxCount == 2);
            Check("the pane given the hub last takes its changes in, and only it", paneB.DrainsChanges && !paneA.DrainsChanges);

            var leftA = await treeA.RevealAsync(left);
            var rightB = await treeB.RevealAsync(right);
            if (leftA is null || rightB is null)
            {
                Check("each pane's folder is in its tree", false);
                return;
            }

            await treeA.LoadAsync(leftA);
            await treeB.LoadAsync(rightB);
            paneA.FlyTo(leftA, 0.9, animated: false);
            paneB.FlyTo(rightB, 0.9, animated: false);
            var time = TimeSpan.FromSeconds(100);
            void Frame(params NestedCanvas[] panes)
            {
                time += TimeSpan.FromMilliseconds(8.33);
                foreach (var pane in panes)
                {
                    pane.RunFrameForTests(time);
                }
            }

            async Task<long> FramesUntil(Func<bool> done, int timeoutMilliseconds, params NestedCanvas[] panes)
            {
                var watch = Stopwatch.StartNew();
                while (!done())
                {
                    if (watch.ElapsedMilliseconds > timeoutMilliseconds)
                    {
                        return -1;
                    }

                    await Task.Delay(8);
                    Frame(panes);
                }

                return watch.ElapsedMilliseconds;
            }

            // ---- icons: A asks, the Shell is held; B draws the same type ----
            Frame(paneA);
            IconSpinUntil(() => shell.Asked.Count == 1);
            Frame(paneB);
            var namesBefore = paneB.LabelLayerCount;
            gate.Set();
            IconSpinUntil(() => icons.PendingCount == 0 && icons.CanvasArrivalsPosted == 1);
            Check($"the Shell is asked once for both panes ({shell.Asked.Count} question), and its answer arrives in each pane's inbox ({icons.CanvasArrivals.Count} and {inboxB.Count})",
                shell.Asked.Count == 1 && icons.CanvasArrivals.Count == 1 && inboxB.Count == 1);
            time += TimeSpan.FromSeconds(1);
            Frame(paneA, paneB);
            Check($"each pane takes it in ({paneA.IconArrivalsTaken} and {paneB.IconArrivalsTaken}), and the pane that never asked draws its names again for it",
                paneA.IconArrivalsTaken == 1 && paneB.IconArrivalsTaken == 1 && paneB.LabelLayerCount > namesBefore
                && icons.GetForCanvas(rightB, 0) is not null);

            await FramesUntil(() => treeA.PendingCount == 0 && treeB.PendingCount == 0, 3_000, paneA, paneB);
            Check("neither pane ever drew the other's folder, so neither tree read it",
                treeB.Find(left) is not { IsLoaded: true } && treeA.Find(right) is not { IsLoaded: true });

            // ---- the hub: one pane drains it, every tree takes its own ----
            var takenA = treeA.LiveChangesTaken;
            var takenB = treeB.LiveChangesTaken;
            hub.Touch(baseDirectory, immediate: true);
            await LiveWait(() => hub.HasWork, 1_000);
            Frame(paneA);
            Check($"a change due waits for the pane that drains the hub: the other's frame takes nothing ({paneA.LastFrameStats.HubItems} taken)",
                hub.HasWork && paneA.LastFrameStats.HubItems == 0 && treeA.LiveChangesTaken == takenA);
            Frame(paneB);
            Check($"that pane's frame takes it, and the folder both trees read is one change to each ({treeA.LiveChangesTaken - takenA} and {treeB.LiveChangesTaken - takenB})",
                !hub.HasWork && treeA.LiveChangesTaken == takenA + 1 && treeB.LiveChangesTaken == takenB + 1);

            takenA = treeA.LiveChangesTaken;
            takenB = treeB.LiveChangesTaken;
            hub.Touch(left, immediate: true);
            await LiveWait(() => hub.HasWork, 1_000);
            Frame(paneA, paneB);
            Check($"a change to a folder only pane A shows reaches A's tree and not B's ({treeA.LiveChangesTaken - takenA} and {treeB.LiveChangesTaken - takenB})",
                !hub.HasWork && treeA.LiveChangesTaken == takenA + 1 && treeB.LiveChangesTaken == takenB);

            takenB = treeB.LiveChangesTaken;
            var change = new FolderChange(leftA.FullPath, ChangeKinds.Structural, 0, default, default);
            ((IChangeSink)treeB).FolderChanged(ChangeConsumer.Nested, leftA, change);
            Check("a tree handed another tree's folder leaves it alone", treeA.Owns(leftA) && !treeB.Owns(leftA) && treeB.LiveChangesTaken == takenB);

            // ---- the disk itself, both panes drawing ----
            File.WriteAllText(Path.Combine(left, "made-in-left.splitpane"), "new");
            Directory.CreateDirectory(Path.Combine(right, "made-in-right"));
            var reached = await FramesUntil(
                () => leftA.AllFiles.Any(file => file.Name == "made-in-left.splitpane") && rightB.AllChildren.Any(child => child.Name == "made-in-right"),
                3_000,
                paneA,
                paneB);
            Check($"a file made in pane A's folder and a folder made in pane B's reach their own panes in {reached} ms (3,000)", reached >= 0);
            Check("and B's tree still has not read A's folder", treeB.Find(left) is not { IsLoaded: true });

            // ---- pane B closes while it drains the hub ----
            var arrivalsB = inboxB.Count;
            paneB.IconArrivals = null;
            icons.UnsubscribeCanvas(inboxB);
            paneB.AttachChanges(null, null);
            sink.RemoveNestedChanges(treeB);
            paneB.Tree = null;
            treeB.Changes = null;
            treeB.Dispose();
            treeB = null;
            Check("pane B closed: pane A takes the hub's changes in again",
                ReferenceEquals(hub.Driver.Active, paneA) && paneA.DrainsChanges);
            Check("and the view model and the icon service hear of one pane", sink.NestedChanges.Count == 1 && ReferenceEquals(sink.NestedChanges[0], treeA) && icons.CanvasInboxCount == 1);

            takenA = treeA.LiveChangesTaken;
            hub.Touch(left, immediate: true);
            await LiveWait(() => hub.HasWork, 1_000);
            Frame(paneA);
            Check($"a change due is taken by pane A's next frame ({treeA.LiveChangesTaken - takenA})", !hub.HasWork && treeA.LiveChangesTaken == takenA + 1);

            var arrivalsA = paneA.IconArrivalsTaken;
            File.WriteAllText(Path.Combine(left, "late.afterclose"), "late");
            var late = await FramesUntil(
                () => leftA.AllFiles.Any(file => file.Name == "late.afterclose") && paneA.IconArrivalsTaken > arrivalsA,
                3_000,
                paneA);
            Check($"a file made after, of a new type, reaches pane A and its icon arrives there in {late} ms, and nothing more in the closed pane's inbox",
                late >= 0 && shell.Asked.Count == 2 && inboxB.Count == arrivalsB);

            paneA.IconArrivals = null;
            paneA.AttachChanges(null, null);
            sink.RemoveNestedChanges(treeA);
            paneA.Tree = null;
        }
        finally
        {
            treeA?.Dispose();
            treeB?.Dispose();
            TryDelete(baseDirectory);
        }
    }

    /// <summary>
    /// The window as it is before any split: one pane, the one being worked
    /// with, whose canvas is the one the bench and the checks have always
    /// called Nested, drawing a tree of its own that hears the changes, with
    /// the icon service's one inbox; its header kept for a split but
    /// collapsed, so it looks as the window's one canvas did; the names
    /// automation finds its parts by unchanged; and the strip over it wired
    /// to it.  Run from the Settings checks: the window needs the app.
    /// </summary>
    private static async Task SplitPaneWindowChecks(MainWindow main, MainViewModel shell)
    {
        Section("split panes: the window's one pane");
        var pane = main.ActivePane;
        var view = pane.View;
        Check("the window has one pane, the one being worked with, and Nested is its canvas",
            main.Panes.Count == 1 && ReferenceEquals(pane, main.FirstPane) && ReferenceEquals(main.Nested, pane.Canvas)
            && main.NestedHost.Children.Count == 1 && ReferenceEquals(main.NestedHost.Children[0], view));
        Check("its canvas draws a tree of its own, which hears the changes, and its icons arrive in the icon service's one inbox",
            ReferenceEquals(pane.Canvas.Tree, pane.Tree) && shell.Tree.NestedChanges.Count == 1 && ReferenceEquals(shell.Tree.NestedChanges[0], pane.Tree)
            && ReferenceEquals(pane.Canvas.IconArrivals, shell.Icons.CanvasArrivals) && shell.Icons.CanvasInboxCount == 1);
        Check("the pane's header is kept for a split but collapsed, so one pane looks as the window's canvas always did",
            view.PaneHeader.Visibility == Visibility.Collapsed);
        Check("automation finds the canvas, the strip, the filter box and the headers by the names they always had",
            AutomationProperties.GetAutomationId(pane.Canvas) == "Nested" && AutomationProperties.GetName(pane.Canvas) == "Folders"
            && view.NestedStrip.Name == "NestedStrip" && view.CanvasFilterBox.Name == "CanvasFilterBox" && view.SortByName.Name == "SortByName");

        view.CanvasFilterBox.Text = "no-such-name-anywhere";
        var filtered = await SettingsWaitFor(() => pane.Canvas.IsFiltering);
        Check("typing in the pane's filter box narrows its canvas a moment later, and the strip says so",
            filtered && view.CanvasFilterHint.Visibility == Visibility.Collapsed && view.CanvasFilterClear.Visibility == Visibility.Visible
            && view.CanvasFilterCount.Text is { Length: > 0 });

        Click(view.CanvasFilterClear);
        Check("and its Clear lets the filter go, the strip with it",
            !pane.Canvas.IsFiltering && view.CanvasFilterBox.Text.Length == 0 && view.CanvasFilterClear.Visibility == Visibility.Collapsed
            && view.CanvasFilterHint.Visibility == Visibility.Visible && view.CanvasFilterCount.Text.Length == 0);
    }

    /// <summary>
    /// The split view in the window, laid out but never shown: the split the
    /// workspace left put back once the panes have their drives, the second
    /// pane made with a canvas, tree and inbox of its own and let go of whole
    /// when the split closes; each pane's camera its own; clicking between
    /// them - F6 - swapping the window's selection, address and history
    /// without a step for Back; the commands, the zoom buttons and the search
    /// acting on the pane being worked with; a delete leaving the other
    /// pane's selection too; side by side and stacked, the divider kept
    /// within its limits; the tree canvas never split; and the button, the
    /// keys and the menus that do it all.
    /// </summary>
    private static async Task SplitViewWindowChecks(MainWindow main, MainViewModel shell)
    {
        Section("split panes: the split view");
        var baseDirectory = SplitWindowFixture("UltraExplorerSplitView");
        var left = Path.Combine(baseDirectory, "left");
        var right = Path.Combine(baseDirectory, "right");
        var fileA = Path.Combine(left, "a.txt");
        var fileB = Path.Combine(right, "b.txt");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        for (var index = 0; index < 12; index++)
        {
            Directory.CreateDirectory(Path.Combine(baseDirectory, $"m{index:D2}"));
        }

        File.WriteAllText(fileA, "a");
        File.WriteAllText(fileB, "b");
        var first = main.FirstPane;
        var host = main.NestedHost;
        var accent = ((SolidColorBrush)Application.Current.FindResource("AccentBrush")).Color;
        bool Lit(NestedPane pane) => pane.View.PaneAccent.Background is SolidColorBrush { Color: var colour } && colour == accent;
        try
        {
            LayOutWindow(main, 1400, 900);
            shell.Tree.PreferLightReveal = true;
            shell.Layout = CanvasLayout.Nested;
            await SplitSettleAsync(main);
            Check("unsplit, the window has one pane, no divider, no second canvas, and its button is not lit",
                main.Panes.Count == 1 && main.SecondPane is null && main.PaneSplitter is null
                && host.ColumnDefinitions.Count == 0 && host.RowDefinitions.Count == 0
                && main.SplitButton.ReadLocalValue(Control.ForegroundProperty) == DependencyProperty.UnsetValue);

            // The workspace left the view split, with the second pane worked with.
            shell.IsSplit = true;
            shell.ActivePaneIndex = 1;
            Check("split before the panes have their drives, nothing is made yet", main.Panes.Count == 1);
            await main.StartNestedForChecksAsync();
            await SettingsSettle();
            var second = main.SecondPane;
            Check("once they have, the split is put back: a second pane, the one being worked with",
                second is not null && main.Panes.Count == 2 && ReferenceEquals(main.ActivePane, second) && ReferenceEquals(shell.History, second.History));
            if (second is null)
            {
                return;
            }

            Check("it has a canvas, a tree and an icon inbox of its own, and its tree hears the changes too",
                !ReferenceEquals(second.Canvas, first.Canvas) && !ReferenceEquals(second.Tree, first.Tree) && ReferenceEquals(second.Canvas.Tree, second.Tree)
                && shell.Tree.NestedChanges.Count == 2 && shell.Icons.CanvasInboxCount == 2
                && !ReferenceEquals(second.Canvas.IconArrivals, shell.Icons.CanvasArrivals) && shell.Tree.KeptSelectionCount == 1);
            Check("the two lie side by side, half each, with the divider between them",
                host.ColumnDefinitions.Count == 3 && host.Children.Count == 3
                && ReferenceEquals(host.Children[0], main.FirstPaneView) && ReferenceEquals(host.Children[2], second.View)
                && host.ColumnDefinitions[0].Width == new GridLength(0.5, GridUnitType.Star) && host.ColumnDefinitions[1].Width.Value == 5
                && Grid.GetColumn(second.View) == 2 && main.PaneSplitter is { Focusable: false, ResizeDirection: GridResizeDirection.Columns });
            Check("both headers show, the accent on the pane being worked with, and the button is lit",
                first.View.PaneHeader.Visibility == Visibility.Visible && second.View.PaneHeader.Visibility == Visibility.Visible
                && Lit(second) && !Lit(first) && main.SplitButton.Foreground is SolidColorBrush { Color: var lit } && lit == accent);
            Check("automation tells the canvases apart", AutomationProperties.GetAutomationId(first.Canvas) == "Nested"
                && AutomationProperties.GetAutomationId(second.Canvas) == "NestedSecond");
            LayOutWindow(main, 1400, 900);
            Check($"each canvas has half the room ({first.Canvas.ActualWidth:0} and {second.Canvas.ActualWidth:0})",
                first.Canvas.ActualWidth > 500 && Math.Abs(first.Canvas.ActualWidth - second.Canvas.ActualWidth) < 2);

            // ---- each pane at its own place ----
            main.ActivatePane(first);
            var drive = new NestedRoot(baseDirectory, "S", NestedFolderKind.Drive);
            first.Tree.SetRoots([drive]);
            second.Tree.SetRoots([drive]);
            await first.FlyToAsync(left, gentle: false, animated: false);
            await second.FlyToAsync(right, gentle: false, animated: false);
            var cameraFirst = first.Canvas.CaptureCamera();
            var cameraSecond = second.Canvas.CaptureCamera();
            Check("each pane's camera is its own: the first on its folder, the second on another",
                cameraFirst?.AnchorPath == left && cameraSecond?.AnchorPath == right);
            first.UpdateHeader();
            second.UpdateHeader();
            Check($"each header names the folder its pane has in view ({first.View.PaneLocation.Text} | {second.View.PaneLocation.Text})",
                first.View.PaneLocation.Text.EndsWith("left", StringComparison.Ordinal) && second.View.PaneLocation.Text.EndsWith("right", StringComparison.Ordinal));
            shell.ZoomInCommand.Execute(null);
            Check("the zoom buttons zoom the pane being worked with alone",
                first.Canvas.CaptureCamera() is { } zoomed && zoomed.Width > cameraFirst!.Width && second.Canvas.CaptureCamera() == cameraSecond);

            // ---- the selection, the address and the history swap ----
            var selection = shell.Tree.Selection;
            selection.ReplaceSingle(fileA, false, 1, SelectionSource.Navigation);
            await SettingsWaitFor(() => ViewAllPath.Equals(shell.Tree.ActivePath, fileA));
            var firstSteps = first.History.Count;
            var secondSteps = second.History.Count;
            main.ActivatePane(second);
            Check("activating the other pane: the window's selection is its - nothing yet - and the first keeps its own",
                ReferenceEquals(main.ActivePane, second) && selection.Count == 0 && first.KeptSelection.Contains(fileA)
                && Lit(second) && !Lit(first) && shell.ActivePaneIndex == 1 && shell.Tree.IsSecondPaneActive);
            Check("with nothing selected there, the address bar goes to the folder it has in view",
                await SettingsWaitFor(() => ViewAllPath.Equals(shell.Address.CurrentPath, right)));
            selection.ReplaceSingle(fileB, false, 1, SelectionSource.Navigation);
            await SettingsWaitFor(() => ViewAllPath.Equals(shell.Tree.ActivePath, fileB));
            Check("a pick there is the window's selection, its address and where a new item goes",
                ViewAllPath.Equals(shell.Address.CurrentPath, fileB) && shell.Tree.SelectedPaths.SequenceEqual([fileB])
                && ViewAllPath.Equals(shell.Tree.TargetDirectory ?? string.Empty, right));
            Check("the headers and the search sort and look in that pane's folder", ViewAllPath.Equals(main.SortFolder() ?? string.Empty, right));
            Check("Back and Forward are that pane's: the pick is in its history, and the other's is as it was",
                ReferenceEquals(shell.History, second.History) && ViewAllPath.Equals(second.History.Current ?? string.Empty, fileB) && first.History.Count == firstSteps
                && second.History.Count == secondSteps + 1);
            main.ActivatePane(first);
            await SettingsWaitFor(() => ViewAllPath.Equals(shell.Tree.ActivePath, fileA));
            Check("back in the first pane its selection and address come back, and the other keeps its pick",
                selection.Count == 1 && selection.Contains(fileA) && ViewAllPath.Equals(shell.Address.CurrentPath, fileA)
                && second.KeptSelection.Contains(fileB) && ReferenceEquals(shell.History, first.History));
            Check("going between the panes is no step for Back and Forward",
                first.History.Count == firstSteps && second.History.Count == secondSteps + 1);
            shell.Tree.ForgetSelected([fileB]);
            Check("an item deleted or moved away leaves the other pane's selection too", second.KeptSelection.Count == 0);

            // ---- F6, and closing with the second pane worked with ----
            // Explicit chords are authoritative here. SetKeyboardState is tied
            // to the active desktop/input queue and cannot reliably make
            // WPF's global Keyboard.Modifiers report a synthetic physical key
            // in this inactive test process.
            var beforeKey = main.ActivePane;
            Check("an explicitly shifted F6 is not the unmodified pane-switch chord",
                !PressKey(main, Key.F6, ModifierKeys.Shift) && ReferenceEquals(main.ActivePane, beforeKey));
            PressKey(main, Key.F6);
            Check("F6 goes to the other pane", ReferenceEquals(main.ActivePane, second) && Lit(second));
            PressKey(main, Key.F6);
            Check("and back", ReferenceEquals(main.ActivePane, first) && Lit(first));
            PressKey(main, Key.F6);
            var closedSecond = second;
            Check("Ctrl+\\ closes the split", main.TryHandleSplitKey(Key.Oem5, ModifierKeys.Control) && !shell.IsSplit);
            Check("the first pane is the one left, worked with again, with its selection back",
                main.Panes.Count == 1 && ReferenceEquals(main.ActivePane, first) && selection.Contains(fileA)
                && ReferenceEquals(shell.History, first.History) && shell.ActivePaneIndex == 0 && !shell.Tree.IsSecondPaneActive);
            Check("the second pane is let go of whole: its canvas off its tree and out of the window, its tree off the hub and its inbox gone",
                closedSecond.Canvas.Tree is null && host.Children.Count == 1 && host.ColumnDefinitions.Count == 0 && main.PaneSplitter is null
                && shell.Tree.NestedChanges.Count == 1 && shell.Icons.CanvasInboxCount == 1
                && shell.Tree.OtherPanePath is null && first.Canvas.DrainsChanges);
            Check("only what it had selected is still followed, for the next split", shell.Tree.KeptSelectionCount == 1);
            Check("the first pane's header is gone with it, so one pane looks as it always did",
                first.View.PaneHeader.Visibility == Visibility.Collapsed && main.SplitButton.ReadLocalValue(Control.ForegroundProperty) == DependencyProperty.UnsetValue);
            Check("and where the second pane was is kept for the next split",
                shell.Tree.SecondPane is { } place && ViewAllPath.Equals(place.ActivePath ?? string.Empty, right) && place.NestedCamera?.AnchorPath == right);

            // ---- split again, stacked ----
            Check("Ctrl+Shift+\\ splits it again, stacked", main.TryHandleSplitKey(Key.OemBackslash, ModifierKeys.Control | ModifierKeys.Shift)
                && shell.IsSplit && shell.SplitOrientation == SplitOrientation.Stacked);
            var again = main.SecondPane;
            Check("a new second pane, where the last one was, and the first still worked with",
                again is not null && !ReferenceEquals(again, closedSecond) && ViewAllPath.Equals(again.FocusPath ?? string.Empty, right)
                && ReferenceEquals(main.ActivePane, first) && Lit(first));
            Check("the selection kept for it is its own now, followed once", shell.Tree.KeptSelectionCount == 1);
            Check("one above the other: rows, no columns, a divider across",
                host.RowDefinitions.Count == 3 && host.ColumnDefinitions.Count == 0 && Grid.GetRow(again!.View) == 2
                && main.PaneSplitter is { ResizeDirection: GridResizeDirection.Rows });
            LayOutWindow(main, 1400, 900);
            shell.SplitRatio = 0.3;
            Check("the divider set to 0.3 gives the first pane that share", host.RowDefinitions[0].Height == new GridLength(0.3, GridUnitType.Star)
                && Math.Abs(host.RowDefinitions[2].Height.Value - 0.7) < 1e-9);
            shell.SplitRatio = 0.99;
            Check("and no pane is given less than 0.15: the divider stops there, dragged or set",
                shell.SplitRatio == 0.85 && host.RowDefinitions[0].MinHeight > 0 && host.RowDefinitions[2].MinHeight == host.RowDefinitions[0].MinHeight);

            // ---- the menus ----
            MenuItem? Item(ItemsControl menu, string header) => menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Header as string == header);
            var splitMenu = main.BuildSplitMenu(main.SplitButton);
            Check("the button's drop-down: the split with its key, and the two layouts, stacked chosen",
                Item(splitMenu, "Split view") is { IsChecked: true, InputGestureText: "Ctrl+\\" }
                && Item(splitMenu, "Stacked") is { IsChecked: true } && Item(splitMenu, "Side by side") is { IsChecked: false });
            var sideBySide = Item(splitMenu, "Side by side")!;
            sideBySide.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, sideBySide));
            Check("choosing Side by side lays them out side by side", shell.SplitOrientation == SplitOrientation.SideBySide && host.ColumnDefinitions.Count == 3);
            var options = main.BuildCanvasOptionsMenu(main.SplitButton);
            Check("Canvas options has the split too", Item(options, "Split view") is { } splitOptions && Item(splitOptions, "Split view") is { IsChecked: true });

            // ---- the tree canvas is never split ----
            shell.Layout = CanvasLayout.Tree;
            shell.IsSplit = false;
            Check("closed from the tree canvas, the split goes", main.Panes.Count == 1);
            Click(main.SplitButton);
            Check("and splitting from the tree canvas goes to the nested canvas first", shell.IsSplit && shell.Layout == CanvasLayout.Nested && main.Panes.Count == 2);
            Click(main.SplitButton);
            Check("the button closes it again", !shell.IsSplit && main.Panes.Count == 1);
        }
        finally
        {
            shell.IsSplit = false;
            shell.SplitOrientation = SplitOrientation.SideBySide;
            shell.SplitRatio = SplitLayout.DefaultRatio;
        }
    }

    /// <summary>
    /// Working between the panes, on real files in a folder of the temp
    /// folder, the window laid out but never shown: a folder's menu opening
    /// it in the other pane - splitting the view the first time, flying the
    /// other pane there after - while the pane being worked with stays; the
    /// other pane's folder found by its sort headers' rule; Copy and Move to
    /// other pane in the item menu, named after that folder and not offered
    /// for what cannot go there, copying and moving for real from the menu
    /// and from Shift+F5 and Shift+F6, a move let go of in both panes'
    /// selections; a drag found in the coordinates of the canvas it is over,
    /// lit there and not in the pane it left, Explorer's move and copy as
    /// ever, refused on its own folder, and dropped for real; neither pane
    /// lit once a drag ends; and nothing of it on the tree canvas.
    /// </summary>
    private static async Task SplitBetweenPanesWindowChecks(MainWindow main, MainViewModel shell)
    {
        Section("split panes: between the panes");
        var baseDirectory = SplitWindowFixture("UltraExplorerSplitBetween");
        var left = Path.Combine(baseDirectory, "left");
        var right = Path.Combine(baseDirectory, "right");
        var sub = Path.Combine(left, "sub");
        var inner = Path.Combine(right, "inner");
        var copyMe = Path.Combine(left, "copy-me.txt");
        var moveMe = Path.Combine(left, "move-me.txt");
        var dragMe = Path.Combine(left, "drag-me.txt");
        var fileB = Path.Combine(right, "b.txt");
        var deep = Path.Combine(inner, "deep.txt");
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(inner);
        for (var index = 0; index < 12; index++)
        {
            Directory.CreateDirectory(Path.Combine(baseDirectory, $"m{index:D2}"));
        }

        foreach (var file in new[] { copyMe, moveMe, dragMe, fileB, deep })
        {
            File.WriteAllText(file, Path.GetFileName(file));
        }

        var first = main.FirstPane;
        var selection = shell.Tree.Selection;
        var host = (INestedPaneHost)main;
        var drive = new NestedRoot(baseDirectory, "S", NestedFolderKind.Drive);
        string[] Labels(IEnumerable<ShellMenuEntry> entries) => [.. entries.Where(entry => !entry.IsSeparator).Select(entry => entry.Label)];
        ShellMenuEntry? Entry(IEnumerable<ShellMenuEntry> entries, string label) => entries.FirstOrDefault(entry => entry.Label == label);
        IReadOnlyList<NestedRoot>? machineDrives = null;
        try
        {
            // The checks before went to the tree canvas and back, which
            // reveals what was selected there: that stands down now, rather
            // than selecting it over the selections made here.
            shell.Tree.BeginNavigation();
            shell.IsSplit = false;
            shell.Tree.PreferLightReveal = true;
            shell.Layout = CanvasLayout.Nested;
            LayOutWindow(main, 1400, 900);
            await SplitSettleAsync(main);

            // The fixture is the one drive of every pane, the second too once it is made.
            machineDrives = main.UseNestedDrivesForChecks([drive]);
            await first.FlyToAsync(left, gentle: false, animated: false);
            selection.ReplaceSingle(sub, true, 0, SelectionSource.Navigation);

            // ---- one pane ----
            var alone = main.ItemMenuEntries([sub]);
            Check("unsplit, a folder's menu offers first to open it in the other pane, and nothing is copied or moved to a pane that is not there",
                Labels(alone).FirstOrDefault() == "Open in other pane" && Entry(alone, "Open in other pane") is { Glyph: "" }
                && !Labels(alone).Any(label => label.Contains("to other pane", StringComparison.Ordinal)));
            Check("the open space of a folder offers it too, after the folder's own settings; a file does not",
                Labels(main.FolderAreaEntries(left, forShell: true)) is var area && area.Contains("Open in other pane")
                && Array.IndexOf(area, "Open in other pane") == Array.IndexOf(area, "Show in File Explorer") + 1
                && !Labels(main.ItemMenuEntries([copyMe])).Contains("Open in other pane"));
            Check("Shift+F5 with no other pane is taken, copies nothing, and says why",
                main.TryHandlePaneTransferKey(Key.F5, ModifierKeys.Shift) && shell.Toast.Message.Contains("split the view", StringComparison.Ordinal)
                && Directory.EnumerateFileSystemEntries(sub).Any() is false);
            Check("F5 and F6 alone are not the other pane's: they read again and go between the panes",
                !main.TryHandlePaneTransferKey(Key.F5, ModifierKeys.None) && !main.TryHandlePaneTransferKey(Key.F6, ModifierKeys.None)
                && !main.TryHandlePaneTransferKey(Key.F7, ModifierKeys.Shift));

            // ---- Open in other pane splits the view ----
            await main.OpenInOtherPaneAsync(right, animated: false);
            var second = main.SecondPane;
            Check("Open in other pane splits the view, the pane being worked with still the first, with its selection as it was",
                shell.IsSplit && second is not null && ReferenceEquals(main.ActivePane, first) && selection.Paths.SequenceEqual([sub]));
            if (second is null)
            {
                return;
            }

            Check("and the new pane has the folder selected, as a step of its own history",
                second.KeptSelection.Count == 1 && second.KeptSelection.Contains(right) && ViewAllPath.Equals(second.KeptSelection.Focus ?? string.Empty, right)
                && ViewAllPath.Equals(second.History.Current ?? string.Empty, right) && ViewAllPath.Equals(second.FocusPath ?? string.Empty, right));

            // Laid out with the split, the new pane has its room.
            LayOutWindow(main, 1400, 900);
            await main.OpenInOtherPaneAsync(right, animated: false);
            second.UpdateHeader();
            Check("split, Open in other pane flies the other pane there and its header names it, while the first stays where it was",
                second.Canvas.CaptureCamera()?.AnchorPath == right && second.Canvas.FolderInView?.FullPath == right
                && second.View.PaneLocation.Text.EndsWith("right", StringComparison.Ordinal)
                && first.Canvas.CaptureCamera()?.AnchorPath == left && ReferenceEquals(main.ActivePane, first) && selection.Paths.SequenceEqual([sub]));

            // ---- the other pane's folder: its sort headers' rule ----
            Check("the folder things go to is the one the other pane has selected", ViewAllPath.Equals(main.OtherPaneFolder() ?? string.Empty, right));
            second.KeptSelection.ReplaceSingle(inner, true, 0, SelectionSource.Navigation);
            Check("a folder selected there inside the one in view is that folder", ViewAllPath.Equals(main.OtherPaneFolder() ?? string.Empty, inner));
            second.KeptSelection.ReplaceSingle(fileB, false, 1, SelectionSource.Navigation);
            Check("a file selected there, the folder it is in", ViewAllPath.Equals(main.OtherPaneFolder() ?? string.Empty, right));
            second.KeptSelection.ReplaceSingle(moveMe, false, 1, SelectionSource.Navigation);
            Check("and a file selected somewhere the pane is not looking, the folder it has in view",
                ViewAllPath.Equals(main.OtherPaneFolder() ?? string.Empty, right));

            // ---- the item menu's entries ----
            selection.ReplaceSingle(copyMe, false, 1, SelectionSource.Navigation);
            var entries = main.ItemMenuEntries([copyMe]);
            var copy = Entry(entries, "Copy to other pane (right)");
            Check("a file's menu offers first to copy it and to move it to the other pane, named after its folder, with their keys",
                copy is { IsEnabled: true, Shortcut: "Shift+F5", Glyph: "" }
                && Entry(entries, "Move to other pane (right)") is { IsEnabled: true, Shortcut: "Shift+F6", Glyph: "" }
                && Labels(entries).Take(3).SequenceEqual(["Copy to other pane (right)", "Move to other pane (right)", "Colour"]));
            var already = main.ItemMenuEntries([fileB]);
            Check("not to be chosen for what is in that folder already",
                Entry(already, "Copy to other pane (right)") is { IsEnabled: false } && Entry(already, "Move to other pane (right)") is { IsEnabled: false });
            Check("nor for the folder itself, or one it is inside",
                Entry(main.ItemMenuEntries([right]), "Copy to other pane (right)") is { IsEnabled: false }
                && Entry(main.ItemMenuEntries([baseDirectory]), "Move to other pane (right)") is { IsEnabled: false });
            Check("a folder's menu has Open in other pane above them",
                Labels(main.ItemMenuEntries([sub])).Take(3).SequenceEqual(["Open in other pane", "Copy to other pane (right)", "Move to other pane (right)"]));

            // ---- copied and moved, for real ----
            copy!.Execute();
            Check("Copy to other pane copies the file into the other pane's folder and leaves it where it was",
                await LiveWait(() => File.Exists(Path.Combine(right, "copy-me.txt")), 10_000) >= 0 && File.Exists(copyMe));
            Check("and the toast says where it went", await LiveWait(() => shell.Toast.Message == "Copied 1 item(s) to right", 5_000) >= 0);

            selection.ReplaceSingle(moveMe, false, 1, SelectionSource.Navigation);
            Check("Shift+F6 is taken where the selection is", main.TryHandlePaneTransferKey(Key.F6, ModifierKeys.Shift));
            Check("and moves what is selected into the other pane's folder",
                await LiveWait(() => File.Exists(Path.Combine(right, "move-me.txt")) && !File.Exists(moveMe), 10_000) >= 0);
            Check("letting go of it in both panes' selections: the one being worked with, and the other's, which had it too",
                await LiveWait(() => !selection.Contains(moveMe) && !second.KeptSelection.Contains(moveMe), 5_000) >= 0);
            await LiveWait(() => shell.Toast.Message == "Moved 1 item(s) to right", 5_000);

            // File effects and the toast arrive before the asynchronous
            // transfer's finally releases its duplicate-command guard.
            var pendingSends = (HashSet<NestedPane>)typeof(MainWindow)
                .GetField("_paneSendsAsking", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            Check("the preceding pane transfer finishes before the next independent command",
                await LiveWait(() => pendingSends.Count == 0, 5_000) >= 0);

            second.KeptSelection.ReplaceSingle(right, true, 0, SelectionSource.Navigation);
            await main.SendToOtherPaneAsync([Path.Combine(right, "copy-me.txt")], move: true);
            Check("what is in the other pane's folder already goes nowhere, and the toast says why",
                File.Exists(Path.Combine(right, "copy-me.txt")) && shell.Toast.Message.StartsWith("Nothing to move", StringComparison.Ordinal));

            // ---- dragged from one pane onto a folder of the other ----
            await first.Tree.LoadAsync(first.Tree.Find(left)!);
            await second.Tree.LoadAsync(second.Tree.Find(right)!);
            LayOutWindow(main, 1400, 900);
            var root = SettingsHosts[main];
            Point? Centre(NestedPane pane, string path) =>
                pane.Tree.Find(path) is { } folder && pane.Canvas.ScreenRectOf(folder) is { } cell
                    ? pane.Canvas.TranslatePoint(new Point(cell.X + cell.Width / 2, cell.Y + cell.Height / 2), root)
                    : null;
            if (Centre(first, sub) is not { } overSub || Centre(second, inner) is not { } overInner)
            {
                Check("each pane's folder is on its canvas", false);
                return;
            }

            DragEventArgs Drag(NestedPane pane, RoutedEvent routed, IDataObject data, Point at, DragDropKeyStates keys = DragDropKeyStates.None)
            {
                var args = SplitDragArgs(routed, data, keys, root, at);
                pane.Canvas.RaiseEvent(args);
                return args;
            }

            var carried = new DataObject(DataFormats.FileDrop, new[] { dragMe });
            var overFirst = Drag(first, DragDrop.DragOverEvent, carried, overSub);
            Check("a drag over the first pane lights the folder under the pointer there",
                first.Canvas.DropTarget?.FullPath == sub && second.Canvas.DropTarget is null && overFirst is { Handled: true, Effects: DragDropEffects.Move });
            Drag(first, DragDrop.DragLeaveEvent, carried, overInner);
            var overSecond = Drag(second, DragDrop.DragOverEvent, carried, overInner);
            Check("on over the other pane: the folder under the pointer there is lit, found in that pane's own coordinates, and the pane it left is not",
                second.Canvas.DropTarget?.FullPath == inner && first.Canvas.DropTarget is null && overSecond.Effects == DragDropEffects.Move);
            Check("Explorer's rules as ever: on the same drive a move, with Ctrl a copy, with Shift a move",
                Drag(second, DragDrop.DragOverEvent, carried, overInner, DragDropKeyStates.ControlKey).Effects == DragDropEffects.Copy
                && Drag(second, DragDrop.DragOverEvent, carried, overInner, DragDropKeyStates.ShiftKey).Effects == DragDropEffects.Move
                && second.Canvas.DropTarget?.FullPath == inner);

            host.NestedDragPaths = [deep];
            var ownFolder = Drag(second, DragDrop.DragOverEvent, new DataObject(DataFormats.FileDrop, new[] { deep }), overInner);
            var intoItself = Drag(second, DragDrop.DragOverEvent, new DataObject(DataFormats.FileDrop, new[] { right }), overInner);
            host.NestedDragPaths = null;
            Check("refused on the folder a drag from the window carries its item out of, and on a folder inside what it carries",
                ownFolder.Effects == DragDropEffects.None && intoItself.Effects == DragDropEffects.None && second.Canvas.DropTarget is null);

            selection.ReplaceSingle(dragMe, false, 1, SelectionSource.Navigation);
            host.NestedDragPaths = [dragMe];
            Drag(second, DragDrop.DragOverEvent, carried, overInner);
            var dropped = Drag(second, DragDrop.DropEvent, carried, overInner);
            host.NestedDragPaths = null;
            host.ClearDropTargets();
            Check("dropped there it is moved into that folder, the drag's source told nothing is left for it to do, and the folder is lit no more",
                dropped.Effects == DragDropEffects.None && second.Canvas.DropTarget is null
                && await LiveWait(() => File.Exists(Path.Combine(inner, "drag-me.txt")) && !File.Exists(dragMe), 10_000) >= 0);
            Check("and it is let go of in the pane it came from", await LiveWait(() => !selection.Contains(dragMe), 5_000) >= 0);
            await LiveWait(() => shell.Toast.Message == "Moved 1 item(s) to inner", 5_000);

            first.Canvas.DropTarget = first.Tree.Find(sub);
            second.Canvas.DropTarget = second.Tree.Find(inner);
            host.ClearDropTargets();
            Check("when a drag ends, neither pane's folder is lit any more", first.Canvas.DropTarget is null && second.Canvas.DropTarget is null);

            // ---- the tree canvas has no other pane ----
            shell.Layout = CanvasLayout.Tree;
            Check("on the tree canvas there is no other pane's folder, and the menus offer nothing of the split",
                main.OtherPaneFolder() is null && !Labels(main.ItemMenuEntries([sub])).Any(label => label.Contains("other pane", StringComparison.Ordinal))
                && !Labels(main.FolderAreaEntries(left, forShell: false)).Any(label => label.Contains("other pane", StringComparison.Ordinal)));
            await main.SendToOtherPaneAsync([copyMe], move: false);
            Check("and Copy to other pane there says so", shell.Toast.Message.Contains("nested canvas", StringComparison.Ordinal));
        }
        finally
        {
            host.NestedDragPaths = null;
            host.ClearDropTargets();
            shell.IsSplit = false;
            shell.Layout = CanvasLayout.Nested;
            if (machineDrives is not null)
            {
                main.UseNestedDrivesForChecks(machineDrives);
            }

            selection.Clear(SelectionSource.Navigation);
            await LiveWait(() => !shell.Toast.IsBusy, 5_000);
        }
    }

    /// <summary>
    /// What the review of the split view found, on real folders in the temp
    /// folder, the window laid out but never shown: a press on a part of the
    /// other pane that cannot take the keyboard - a sort header, its header
    /// - brings the keyboard to that pane's canvas, while its filter box and
    /// canvas take it themselves; a flight asked for while an earlier one
    /// still reads its way down is where the camera goes, whichever gets
    /// there first; a navigation still reading its way down when the other
    /// pane is clicked ends in the pane it was asked in, its selection,
    /// history and camera, and leaves the other alone; a pane activated
    /// hands its place to the commands at once, before the graph has found
    /// it; what the closed second pane had selected is followed until the
    /// next split, and what went meanwhile is not selected in it; on the
    /// tree canvas a split left on is not shown as one, and the button, the
    /// keys and the menu bring it back - the tree only passed through, not
    /// taken for the picture on show once its reveal ends; and the search
    /// panel keeps clear of the second pane's header and strip.
    /// </summary>
    private static async Task SplitFollowWindowChecks(MainWindow main, MainViewModel shell)
    {
        Section("split panes: the keyboard, navigations and the tree canvas");
        var baseDirectory = SplitWindowFixture("UltraExplorerSplitFollow");
        var left = Path.Combine(baseDirectory, "left");
        var right = Path.Combine(baseDirectory, "right");
        var fileA = Path.Combine(left, "a.txt");
        var hidden = Path.Combine(right, "deep", "deeper");
        var fileB = Path.Combine(hidden, "b.txt");
        var fileC = Path.Combine(right, "c.txt");
        var fileD = Path.Combine(right, "d.txt");
        var far = Path.Combine(baseDirectory, "far");
        var deep = Path.Combine(far, "a", "b", "c", "d");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(hidden);
        Directory.CreateDirectory(deep);
        for (var index = 0; index < 400; index++)
        {
            Directory.CreateDirectory(Path.Combine(far, $"n{index:D3}"));
        }

        foreach (var file in new[] { fileA, fileB, fileC, fileD })
        {
            File.WriteAllText(file, Path.GetFileName(file));
        }

        var first = main.FirstPane;
        var selection = shell.Tree.Selection;
        var drive = new NestedRoot(baseDirectory, "S", NestedFolderKind.Drive);
        var accent = ((SolidColorBrush)Application.Current.FindResource("AccentBrush")).Color;
        bool Lit() => main.SplitButton.Foreground is SolidColorBrush { Color: var colour } && colour == accent
            && main.SplitButton.ReadLocalValue(Control.ForegroundProperty) != DependencyProperty.UnsetValue;
        MenuItem? Item(ItemsControl menu, string header) => menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Header as string == header);
        IReadOnlyList<NestedRoot>? machineDrives = null;
        var testWindow = Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW");
        try
        {
            // What the checks before revealed stands down, rather than
            // selecting over the selections made here.
            shell.Tree.BeginNavigation();
            shell.IsSplit = false;
            shell.Tree.PreferLightReveal = true;
            shell.Layout = CanvasLayout.Nested;
            shell.SplitOrientation = SplitOrientation.SideBySide;
            LayOutWindow(main, 1400, 900);
            await SplitSettleAsync(main);
            var margin = main.SearchPanel.Margin;
            machineDrives = main.UseNestedDrivesForChecks([drive]);
            shell.IsSplit = true;
            LayOutWindow(main, 1400, 900);
            await SettingsSettle();
            var second = main.SecondPane;
            if (second is null)
            {
                Check("the view splits", false);
                return;
            }

            await first.FlyToAsync(left, gentle: false, animated: false);
            await second.FlyToAsync(right, gentle: false, animated: false);

            // ---- the last flight asked for is where the camera goes ----
            // The deep folder is four reads away; the first pane's own folder
            // has been read.  The flight asked for second finishes first.
            var earlier = first.FlyToAsync(deep, gentle: false, animated: false);
            await first.FlyToAsync(left, gentle: false, animated: false);
            var stillReading = !earlier.IsCompleted;
            await earlier;
            Check($"a flight asked for while an earlier one still reads its way down ({stillReading}) is where the camera goes, though the earlier one gets there after it",
                stillReading && first.Canvas.CaptureCamera()?.AnchorPath == left && first.FlightsUnderWay == 0);

            // ---- the keyboard comes with a press on the pane ----
            // As a test copy, the window notes where it would put the
            // keyboard rather than taking it, which a window never shown
            // could not: the checks read that.
            Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", "1");
            main.ActivatePane(second);
            FocusManager.SetFocusedElement(main, second.Canvas);
            Press(first.View.SortByName);
            Check("a press on a sort header of the other pane, which never takes the keyboard, makes that pane the one worked with and sends the keyboard to its canvas",
                ReferenceEquals(main.ActivePane, first) && ReferenceEquals(FocusManager.GetFocusedElement(main), first.Canvas));
            Press(second.View.PaneLocation);
            Check("and so does a press on the other pane's header",
                ReferenceEquals(main.ActivePane, second) && ReferenceEquals(FocusManager.GetFocusedElement(main), second.Canvas));
            Press(first.View.CanvasFilterBox);
            Check("a press in its filter box makes it the one worked with and leaves the keyboard to the box, which takes it itself",
                ReferenceEquals(main.ActivePane, first) && ReferenceEquals(FocusManager.GetFocusedElement(main), second.Canvas));
            FocusManager.SetFocusedElement(main, first.Canvas);
            Press(second.Canvas);
            Check("and a press on its canvas leaves it to the canvas",
                ReferenceEquals(main.ActivePane, second) && ReferenceEquals(FocusManager.GetFocusedElement(main), first.Canvas));
            Press(second.View.SortBySize);
            Check("a press on the pane already worked with moves nothing",
                ReferenceEquals(main.ActivePane, second) && ReferenceEquals(FocusManager.GetFocusedElement(main), first.Canvas));
            FocusManager.SetFocusedElement(main, null);
            Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", testWindow);

            // ---- a navigation ends in the pane it was asked in ----
            main.ActivatePane(first);
            selection.ReplaceSingle(fileA, false, 1, SelectionSource.Navigation);
            await SettingsWaitFor(() => ViewAllPath.Equals(shell.Tree.ActivePath, fileA));
            main.ActivatePane(second);
            selection.ReplaceSingle(fileC, false, 1, SelectionSource.Navigation);
            await SettingsWaitFor(() => ViewAllPath.Equals(shell.Tree.ActivePath, fileC));
            main.ActivatePane(first);
            await SettingsWaitFor(() => ViewAllPath.Equals(shell.Tree.ActivePath, fileA));
            var firstSteps = first.History.Count;
            var secondSteps = second.History.Count;
            var secondCamera = second.Canvas.CaptureCamera();
            var reveal = shell.Tree.RevealPathAsync(deep);
            var reading = !reveal.IsCompleted;
            main.ActivatePane(second);
            await reveal;
            await SettingsSettle();
            Check($"a navigation asked for in the first pane, still reading its way down ({reading}) when the second is clicked, leaves the second as it was: its selection, its history and its camera",
                reading && ReferenceEquals(main.ActivePane, second) && selection.Paths.SequenceEqual([fileC])
                && ViewAllPath.Equals(shell.Tree.ActivePath, fileC) && second.History.Count == secondSteps
                && second.Canvas.CaptureCamera() == secondCamera);
            Check("it ends in the first pane: what it has selected, and a step of its history",
                first.KeptSelection.Paths.SequenceEqual([deep]) && ViewAllPath.Equals(first.History.Current ?? string.Empty, deep)
                && first.History.Count == firstSteps + 1);
            Check("and the first pane's camera goes there",
                await LiveWait(() => first.Canvas.CaptureCamera()?.AnchorPath is { } anchor && ViewAllPath.Equals(anchor, deep), 5_000) >= 0);

            // ---- the place handed over at once ----
            main.ActivatePane(first);
            second.KeptSelection.ReplaceSingle(fileB, false, 1, SelectionSource.Navigation);
            main.ActivatePane(second);
            var stale = !ViewAllPath.Equals(shell.Tree.ActivePath, fileB);
            Check($"a pane activated with a file the graph has not found yet ({stale}): Paste, New folder and Up act on its place at once, not on the other pane's",
                stale && ViewAllPath.Equals(shell.Tree.TargetDirectory ?? string.Empty, hidden)
                && ViewAllPath.Equals(shell.Tree.FocusedPath, fileB) && shell.Tree.SelectedOrActivePaths.SequenceEqual([fileB]));
            Check("and once the graph has found it, the address bar names it too",
                await SettingsWaitFor(() => ViewAllPath.Equals(shell.Tree.ActivePath, fileB))
                && ViewAllPath.Equals(shell.Tree.TargetDirectory ?? string.Empty, hidden));

            // ---- the closed pane's selection ----
            main.ActivatePane(first);
            second.KeptSelection.Apply(new SelectionEdit
            {
                Clear = true,
                Added = [new SelectionItem(fileB, false, 1), new SelectionItem(fileC, false, 1), new SelectionItem(fileD, false, 1)],
                Anchor = fileD,
                Focus = fileD,
                Source = SelectionSource.Navigation
            });
            shell.IsSplit = false;
            shell.Tree.ForgetSelected([fileB]);
            File.Delete(fileB);
            File.WriteAllText(fileB, "made again");
            File.Delete(fileC);
            shell.IsSplit = true;
            var again = main.SecondPane;
            Check("split again, the second pane has what was selected in it before, less what the window deleted or moved meanwhile, though a file of that name is there again",
                again is not null && !again.KeptSelection.Contains(fileB) && again.KeptSelection.Contains(fileD)
                && ViewAllPath.Equals(again.KeptSelection.Focus ?? string.Empty, fileD));
            Check("and what went from the disk some other way is let go of a moment later",
                again is not null && await LiveWait(() => !again.KeptSelection.Contains(fileC), 5_000) >= 0 && again.KeptSelection.Contains(fileD));

            // ---- the search panel ----
            LayOutWindow(main, 1400, 900);
            shell.Search.Text = "split-follow-no-such-name";
            var below = again is null ? 0 : again.View.PaneHeader.ActualHeight + again.View.NestedStrip.ActualHeight;
            Check($"side by side, the search panel opens below the second pane's header and strip ({main.SearchPanel.Margin.Top:0} of {margin.Top:0} + {below:0})",
                below > 40 && Math.Abs(main.SearchPanel.Margin.Top - (margin.Top + below)) < 0.5 && main.SearchPanel.Margin.Right == margin.Right);
            shell.Search.Close();
            shell.IsSplit = false;
            await SettingsSettle();
            shell.Search.Text = "split-follow-no-such-name";
            Check("and with one pane where it always was", main.SearchPanel.Margin == margin);
            shell.Search.Close();

            // ---- the tree canvas with the split left on ----
            shell.IsSplit = true;
            shell.Layout = CanvasLayout.Tree;
            var treeEntry = main.TreeEntryForChecks;
            Check("on the tree canvas a split left on is not shown as one: the button is not lit and the menu does not check it",
                shell.IsSplit && !Lit() && Item(main.BuildSplitMenu(main.SplitButton), "Split view") is { IsChecked: false }
                && Item(main.BuildSplitMenu(main.SplitButton), "Side by side") is { IsChecked: false });
            Click(main.SplitButton);
            Check("the button brings back the nested canvas and its panes, split as they were, rather than closing them",
                shell.Layout == CanvasLayout.Nested && shell.IsSplit && main.Panes.Count == 2 && Lit());
            var passedThrough = !treeEntry.IsCompleted;
            await treeEntry;
            Check($"the tree only passed through, its reveal still reading ({passedThrough}): once that ends the tree is not taken for the picture on show",
                passedThrough && !shell.Tree.IsCanvasShown && shell.Layout == CanvasLayout.Nested);
            shell.Layout = CanvasLayout.Tree;
            Check("so does Ctrl+\\", main.TryHandleSplitKey(Key.Oem5, ModifierKeys.Control) && shell.Layout == CanvasLayout.Nested && shell.IsSplit);
            shell.Layout = CanvasLayout.Tree;
            Check("and Ctrl+Shift+\\ turns the layout and shows it",
                main.TryHandleSplitKey(Key.OemBackslash, ModifierKeys.Control | ModifierKeys.Shift)
                && shell.Layout == CanvasLayout.Nested && shell.IsSplit && shell.SplitOrientation == SplitOrientation.Stacked);
            shell.Layout = CanvasLayout.Tree;
            var sideBySide = Item(main.BuildSplitMenu(main.SplitButton), "Side by side")!;
            sideBySide.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, sideBySide));
            Check("as does a layout chosen from the menu", shell.Layout == CanvasLayout.Nested && shell.IsSplit && shell.SplitOrientation == SplitOrientation.SideBySide);
            Click(main.SplitButton);
            Check("and on show, the button closes it", !shell.IsSplit && main.Panes.Count == 1 && !Lit());

            // Close the new pane before its sparse path description returns.
            // Its queued Enter and the late flight must not revive a camera
            // or ask the surviving window to add roots for a disposed tree.
            shell.IsSplit = true;
            var closingPane = main.SecondPane!;
            closingPane.Tree.SetRoots([drive]);
            var closingFlight = closingPane.FlyToAsync(deep, gentle: false, animated: false);
            var flightPendingAtClose = !closingFlight.IsCompleted;
            shell.IsSplit = false;
            await closingFlight;
            await SettingsSettle();
            Check("closing a pane while its destination is still being read cancels the late flight and drops its canvas tree",
                flightPendingAtClose && closingPane.FlightsUnderWay == 0 && !closingPane.IsReady
                && closingPane.Canvas.Tree is null && closingPane.Canvas.CaptureCamera() is null && main.Panes.Count == 1);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", testWindow);
            FocusManager.SetFocusedElement(main, null);
            shell.Search.Close();
            shell.IsSplit = false;
            shell.Layout = CanvasLayout.Nested;
            shell.SplitOrientation = SplitOrientation.SideBySide;
            if (machineDrives is not null)
            {
                main.UseNestedDrivesForChecks(machineDrives);
            }

            selection.Clear(SelectionSource.Navigation);
        }
    }

    /// <summary>
    /// The folders the split view's checks in the window work in, deleted
    /// once the window is done with (<see cref="DeleteSplitWindowFixtures"/>)
    /// rather than as each section ends.  Deleted there and then, the folder
    /// the folder list was showing went from disk while the next section ran,
    /// and the window - as it should - took its selection and the camera of
    /// the pane being worked with to the nearest folder still there, over
    /// whatever that section had just selected and flown to.
    /// </summary>
    private static readonly List<string> SplitWindowFixtures = [];

    /// <summary>A new folder in the temp folder's <paramref name="name"/> for a section of the split view's checks in the window.</summary>
    private static string SplitWindowFixture(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));
        SplitWindowFixtures.Add(path);
        return path;
    }

    /// <summary>The split view's checks' folders deleted: the window is done with.</summary>
    private static void DeleteSplitWindowFixtures()
    {
        foreach (var path in SplitWindowFixtures)
        {
            TryDelete(path);
        }

        SplitWindowFixtures.Clear();
    }

    /// <summary>
    /// What the checks before left under way, finished before a section of
    /// the split view starts: a pane entered when the nested canvas came back
    /// from the tree goes to its place behind layout, and its flight reads
    /// the folders on the way - through a temp folder of thousands - for a
    /// while.  Left to run, it ended in the middle of the section, after the
    /// section's own flights had been asked for.
    /// </summary>
    private static async Task SplitSettleAsync(MainWindow main)
    {
        await SettingsSettle();
        await LiveWait(() => main.Panes.All(pane => pane.FlightsUnderWay == 0), 10_000);
        await SettingsSettle();
    }

    /// <summary>The mouse's left button pressed on <paramref name="element"/>: only the press's way down, which is where a pane takes it.</summary>
    private static void Press(UIElement element) =>
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });

    /// <summary>
    /// A drag event as the window's drop target raises it, at a point of
    /// <paramref name="relativeTo"/>: WPF makes them only for a real drag,
    /// so the checks make their own with the constructor it uses.
    /// </summary>
    private static DragEventArgs SplitDragArgs(RoutedEvent routed, IDataObject data, DragDropKeyStates keys, DependencyObject relativeTo, Point point)
    {
        var constructor = typeof(DragEventArgs).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)],
            null) ?? throw new MissingMethodException(nameof(DragEventArgs), ".ctor");
        var args = (DragEventArgs)constructor.Invoke([data, keys, DragDropEffects.Copy | DragDropEffects.Move, relativeTo, point]);
        args.RoutedEvent = routed;
        return args;
    }

    /// <summary>
    /// An owned shortcut with an explicit modifier chord, through the same
    /// split-key dispatcher the window calls. Raising KeyEventArgs does not
    /// carry modifiers: that route reads whatever the desktop holds at the
    /// time and used to turn this fixture's F6 into Shift+F6. This helper
    /// tests the product's chord routing directly, not physical key injection.
    /// </summary>
    private static bool PressKey(Window window, Key key, ModifierKeys modifiers = ModifierKeys.None) =>
        window is MainWindow main
            ? main.TryHandleSplitKey(key, modifiers)
            : throw new ArgumentException("The split shortcut fixture requires its own MainWindow.", nameof(window));

}
