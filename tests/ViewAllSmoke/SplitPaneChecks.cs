using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
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
        return Task.CompletedTask;
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
}
