using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The nested canvas itself, on a thread of its own with a dispatcher: the
/// camera, hit testing, forty levels of zoom, and what a frame costs.  The
/// trees are read in full before a canvas sees them and are told not to read
/// on demand, so drawing never starts a read and every picture is the same on
/// every run.  Set NESTED_SHOTS to a folder to keep the pictures.
/// </summary>
internal static partial class Program
{
    private const int ViewWidth = 1600;
    private const int ViewHeight = 1000;
    private const uint CanvasBackground = 0xFF111315;

    /// <summary>What FitAll gives This PC in a 1600 x 1000 view: the view less 18 px all round, at the cell's aspect.</summary>
    private const double FitWidth = (ViewHeight - 36) * NestedLayout.Aspect;

    private static readonly List<string> ShotsWritten = [];

    private static Task NestedCanvasChecks()
    {
        RunOnSta("nested canvas", async () =>
        {
            await NestedQueueChecks();
            await NestedCanvasWorldChecks();
            await NestedPerformanceChecks();
        });

        // A run of its own: ordering swaps the process-wide type names for a
        // test table, and anything that stops it half way is reported by name.
        RunOnSta("nested sort", NestedSortChecks);

        if (ShotsWritten.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  pictures:");
            foreach (var shot in ShotsWritten)
            {
                Console.WriteLine($"    {shot}");
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a new STA thread inside a running
    /// dispatcher, so awaits come back to that thread the way they do in the app.
    /// </summary>
    private static void RunOnSta(string name, Func<Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await body();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = name
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(5)))
        {
            Check($"{name} finished within five minutes", false);
            return;
        }

        if (failure is not null)
        {
            Console.WriteLine($"  FATAL {failure}");
            Check($"{name} ran to the end", false);
        }
    }

    // ---- the read queue --------------------------------------------------------------

    /// <summary>
    /// What the canvas relies on when it asks for folders while drawing: a few
    /// reads at a time, the most wanted first, and requests that stopped being
    /// repeated dropped rather than read.
    /// </summary>
    private static async Task NestedQueueChecks()
    {
        Section("nested read queue");

        // A slot for each of the first reads, and seven more folders to wait.
        const int Slots = NestedTree.LocalReadSlots;
        var disk = new FakeDisk();
        for (var index = 0; index < Slots + 7; index++)
        {
            disk.Folder($@"Q:\c{index:D2}\sub");
        }

        using var gate = new SemaphoreSlim(0);
        var gated = 0;
        disk.Hook = (_, token) =>
        {
            if (Volatile.Read(ref gated) == 1)
            {
                gate.Wait(token);
            }

            return null;
        };

        using var tree = new NestedTree(disk.Read);
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        var q = tree.Find(@"Q:\")!;
        await tree.LoadAsync(q);
        var c = q.Children.ToArray();
        Volatile.Write(ref gated, 1);

        for (var index = 0; index < c.Length; index++)
        {
            tree.Request(c[index], priority: index);
        }

        Check($"{Slots} reads run at once and the rest wait",
            c.Take(Slots).All(folder => folder.LoadState == NestedLoadState.Loading)
            && c.Skip(Slots).All(folder => folder.LoadState == NestedLoadState.Queued));
        Check("the tree counts all of them as pending", tree.PendingCount == c.Length);

        // Three pictures go by in which only the two most wanted are asked for again.
        var second = c[^2];
        var first = c[^1];
        tree.BeginFrame();
        tree.BeginFrame();
        tree.BeginFrame();
        tree.Request(second, c.Length - 2);
        tree.Request(first, c.Length - 1);

        gate.Release(1);
        await WaitUntil(() => c.Take(Slots).Count(folder => folder.IsLoaded) == 1, 3_000);
        Check("when a read finishes, the most wanted request still being asked for goes next",
            first.LoadState == NestedLoadState.Loading && second.LoadState == NestedLoadState.Queued);
        Check("requests not repeated for two pictures are dropped, not read",
            c.Skip(Slots).Take(5).All(folder => folder.LoadState == NestedLoadState.NotLoaded));
        Check("and leave the queue", tree.PendingCount == Slots + 1);

        gate.Release(100);
        await WaitUntil(() => tree.PendingCount == 0, 5_000);
        Check("everything still wanted is read",
            c.Take(Slots).All(folder => folder.IsLoaded) && second.IsLoaded && first.IsLoaded);

        Volatile.Write(ref gated, 0);
        var dropped = c[Slots + 1];
        tree.Request(dropped, 1);
        await WaitUntil(() => dropped.IsLoaded, 3_000);
        Check("a dropped folder can be asked for again", dropped.IsLoaded);
        Check("the disk was read exactly for what was wanted", disk.Reads == 1 + Slots + 2 + 1);

        tree.IsReadingOnDemand = false;
        tree.Request(c[Slots + 2], 100);
        Check("with reading on demand off, asking reads nothing", c[Slots + 2].LoadState == NestedLoadState.NotLoaded && tree.PendingCount == 0);
    }

    // ---- the canvas over a small world -------------------------------------------------

    private static async Task NestedCanvasWorldChecks()
    {
        Section("nested canvas");

        var disk = BuildNestedWorld(out var chain);
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots(
        [
            new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive, "1.2 TB free"),
            new NestedRoot(@"R:\", "R:", NestedFolderKind.Drive, "80 GB free")
        ]);

        // Everything but the thirty-five siblings at each level of the chain.
        var onChain = new HashSet<string>(chain, StringComparer.OrdinalIgnoreCase);
        await LoadEverythingAsync(tree, folder =>
            !folder.FullPath.StartsWith(@"Q:\chain\", StringComparison.OrdinalIgnoreCase) || onChain.Contains(folder.FullPath));
        var loadedFolders = Descendants(tree.Root, includeHidden: true).Where(folder => folder.IsLoaded).ToList();
        var misplaced = loadedFolders.Select(PlacementProblem).FirstOrDefault(problem => problem is not null);
        Check($"every folder the tree placed is inside its parent, clear of siblings and files ({loadedFolders.Count:N0} read)",
            misplaced is null && loadedFolders.Count > 2_000);
        if (misplaced is not null)
        {
            Console.WriteLine($"        {misplaced}");
        }

        var readsBefore = disk.Reads;
        var canvas = new NestedCanvas { Tree = tree };
        canvas.Measure(new Size(ViewWidth, ViewHeight));
        canvas.Arrange(new Rect(0, 0, ViewWidth, ViewHeight));
        canvas.UpdateLayout();
        Check("the canvas is laid out at the size asked for", canvas.ActualWidth == ViewWidth && canvas.ActualHeight == ViewHeight);

        // ---- fit all ------------------------------------------------------------------
        canvas.FitAll(animated: false);
        var fitShot = Shoot(canvas, "nested-fit-all.png");
        var root = canvas.ScreenRectOf(tree.Root);
        Check("fit-all shows all of This PC, centred with an 18 px margin",
            SameRect(root, new Rect((ViewWidth - FitWidth) / 2, 18, FitWidth, FitWidth * NestedLayout.CellHeight), 1e-12));
        Check($"and calls that ×1 (it says {canvas.ZoomText})", canvas.ZoomText == "×1");
        Check($"drawing it draws many cells ({canvas.DrawnCellCount:N0})", canvas.DrawnCellCount > 2_000);
        Check("drawing reads nothing when reading on demand is off", disk.Reads == readsBefore && tree.PendingCount == 0);
        Check("outside This PC is the canvas colour", SameColour(fitShot, new Point(5, 5), CanvasBackground));
        Check("inside it, clear of the drives, is This PC's own colour",
            SameColour(fitShot, InBottomRightMargin(root!.Value), tree.Root.BodyColour));
        Check("the picture is not blank", DistinctColours(fitShot) > 30);
        Check("a point outside This PC hits nothing", canvas.HitTest(new Point(5, 5)) is null);
        Check("a point in This PC between the drives hits This PC",
            canvas.HitTest(InBottomRightMargin(root.Value)) is { Folder.IsComputer: true });

        var fitHits = HitTestFolders(canvas, tree.Root, minimumWidth: 4);
        Check($"a point on a folder's title band hits that folder ({fitHits.Tested:N0} folders on screen)",
            fitHits.Tested >= 150 && fitHits.HeaderMisses == 0);
        Check("and its centre hits it or something inside it", fitHits.CentreMisses == 0);
        if (fitHits.FirstMiss.Length > 0)
        {
            Console.WriteLine($"        first miss: {fitHits.FirstMiss}");
        }

        // ---- flying to a folder -----------------------------------------------------------
        var mixed = tree.Find(@"Q:\mixed")!;
        Check("FlyTo a folder succeeds", canvas.FlyTo(mixed, 0.9, animated: false));
        var ninety = new Rect(ViewWidth * 0.05, (ViewHeight - ViewWidth * 0.9 * NestedLayout.CellHeight) / 2, ViewWidth * 0.9, ViewWidth * 0.9 * NestedLayout.CellHeight);
        Check("and puts it at 90% of the view, centred", SameRect(canvas.ScreenRectOf(mixed), ninety, 1e-9));
        Check("with the camera fixed to it", ReferenceEquals(canvas.Anchor, mixed));
        Shoot(canvas, "nested-mid-zoom.png");
        Check("mid zoom reads nothing either", disk.Reads == readsBefore);

        var midHits = HitTestFolders(canvas, mixed, minimumWidth: 4);
        Check($"at mid zoom every folder on screen is hit by its title band ({midHits.Tested:N0})",
            midHits.Tested >= 250 && midHits.HeaderMisses == 0 && midHits.CentreMisses == 0);
        var first = canvas.ScreenRectOf(mixed.Children[0])!.Value;
        var second = canvas.ScreenRectOf(mixed.Children[mixed.Grid.IndexOf(0, 1)])!.Value;
        Check("a point between two sub-folders hits their parent",
            ReferenceEquals(canvas.HitTest(new Point((first.Right + second.Left) / 2, first.Top + first.Height / 2))?.Folder, mixed));
        Check("a point on the title band is on the header",
            canvas.HitTest(new Point(ninety.X + ninety.Width / 2, ninety.Y + 4)) is { IsOnHeader: true } header && ReferenceEquals(header.Folder, mixed));
        var mixedFiles = HitTestFiles(canvas, mixed);
        Check($"every file tile is hit as that file of that folder ({mixedFiles.Tested})",
            mixedFiles.Tested == mixed.Files.Count && mixedFiles.Misses == 0);

        // ---- zoom and pan keep the world where it was ---------------------------------------
        var cursor = new Point(437.25, 612.5);
        var under = canvas.HitTest(cursor)!.Value.Folder;
        var underBefore = canvas.ScreenRectOf(under)!.Value;
        canvas.ZoomAt(cursor, 1.7);
        var underAfter = canvas.ScreenRectOf(under)!.Value;
        Check("zooming in keeps the spot under the cursor on the same spot of the folder",
            SamePoint(Local(cursor, underBefore), Local(cursor, underAfter), 1e-9) && Near(underAfter.Width, underBefore.Width * 1.7, 1e-9 * underAfter.Width));
        Check("and what is under the cursor is that folder or inside it",
            canvas.HitTest(cursor) is { } afterHit && under.Contains(afterHit.Folder));
        canvas.ZoomAt(cursor, 1 / 1.7);
        Check("zooming back out returns everything to where it was", SameRect(canvas.ScreenRectOf(under), underBefore, 1e-9));
        canvas.Pan(new Vector(-123.25, 77.5));
        var panned = canvas.ScreenRectOf(under)!.Value;
        Check("panning moves the world by exactly the drag",
            Near(panned.X - underBefore.X, -123.25, 1e-9) && Near(panned.Y - underBefore.Y, 77.5, 1e-9) && Near(panned.Width, underBefore.Width, 1e-9));
        canvas.Pan(new Vector(123.25, -77.5));
        Check("and panning back returns it", SameRect(canvas.ScreenRectOf(under), underBefore, 1e-9));

        // ---- files, named ---------------------------------------------------------------------
        var filesFolder = tree.Find(@"Q:\files")!;
        canvas.FlyTo(filesFolder, 0.9, animated: false);
        var filesShot = Shoot(canvas, "nested-files.png");
        var filesRect = canvas.ScreenRectOf(filesFolder)!.Value;
        Check($"zoomed in, a file tile is tall enough to carry its name ({filesFolder.FileGrid.TileHeight * filesRect.Width:0.0} px)",
            filesFolder.FileGrid.TileHeight * filesRect.Width >= NestedCanvas.FileLabelPixels);
        var fileHits = HitTestFiles(canvas, filesFolder);
        Check($"every one of the {filesFolder.Files.Count} files is hit as itself",
            fileHits.Tested == filesFolder.Files.Count && fileHits.Misses == 0);
        if (fileHits.FirstMiss.Length > 0)
        {
            Console.WriteLine($"        first miss: {fileHits.FirstMiss}");
        }

        Check("file names are drawn", BrightPixels(filesShot, FileZone(filesFolder, filesRect)) > 500);
        Check("the folder's title is drawn over its title band",
            CountDifferent(filesShot, new Int32Rect((int)filesRect.X + 70, (int)filesRect.Y + 10, 300, (int)(filesRect.Width * NestedLayout.HeaderHeight) - 20),
                filesFolder.HeaderColour) > 200);
        Check("and its body is its own colour", SameColour(filesShot, InBottomRightMargin(filesRect), filesFolder.BodyColour));

        var hiddenFilePath = Path.Combine(filesFolder.FullPath, "desktop.ini");
        Check("a hidden file cannot be resolved while hidden items are hidden", canvas.Resolve(hiddenFilePath) is null);
        tree.IncludeHidden = true;
        Check("and can once they are shown", canvas.Resolve(hiddenFilePath) is { } shownHidden && shownHidden.Folder == filesFolder
            && filesFolder.Files[shownHidden.FileIndex].Name == "desktop.ini");
        tree.IncludeHidden = false;
        Check("a folder resolves to itself", canvas.Resolve(@"Q:\mixed\m03") is { FileIndex: -1 } resolved && resolved.Folder == tree.Find(@"Q:\mixed\m03"));
        Check("a path in a folder not read resolves to nothing", canvas.Resolve(@"Q:\chain\s00\x.txt") is null);

        // ---- limits on the camera --------------------------------------------------------------
        var empty = tree.Find(@"Q:\empty")!;
        canvas.FlyTo(empty, 0.9, animated: false);
        canvas.ZoomBy(10);
        Check("an empty folder cannot be zoomed past three views wide",
            Near(canvas.ScreenRectOf(empty)!.Value.Width, 3 * ViewWidth, 1e-6));
        canvas.FitAll(animated: false);
        canvas.ZoomBy(0.1);
        Check($"This PC cannot shrink below a quarter of the fit (zoom reads {canvas.ZoomText})",
            Near(canvas.ScreenRectOf(tree.Root)!.Value.Width, FitWidth / 4, 1e-9) && Near(ParseZoom(canvas.ZoomText), 0.25, 1e-9));
        canvas.FitAll(animated: false);
        canvas.Pan(new Vector(5_000, 0));
        Check("panning cannot push This PC off the screen", Near(canvas.ScreenRectOf(tree.Root)!.Value.X, ViewWidth - 48, 1e-9));
        canvas.FitAll(animated: false);

        var hiddenFolder = tree.Find(@"Q:\$hidden")!;
        Check("a folder off the canvas has no rectangle and cannot be flown to",
            canvas.ScreenRectOf(hiddenFolder) is null && !canvas.FlyTo(hiddenFolder, 0.9, animated: false));

        // ---- the folder the view is in goes away ------------------------------------------------
        var m03 = tree.Find(@"Q:\mixed\m03")!;
        var n05 = tree.Find(@"Q:\mixed\m03\n05")!;
        canvas.FlyTo(n05, 0.9, animated: false);
        Render(canvas);
        var m03Before = canvas.ScreenRectOf(m03)!.Value;
        tree.SetUserHidden([n05.FullPath]);
        Render(canvas);
        Check("hiding the folder the camera is fixed to moves the camera off it",
            canvas.Anchor is { } afterOwnHide && !ReferenceEquals(afterOwnHide, n05) && NestedTree.IsOnCanvas(afterOwnHide)
            && canvas.ScreenRectOf(n05) is null);
        Check("without moving its parent on screen", SameRect(canvas.ScreenRectOf(m03), m03Before, 1e-9));
        tree.SetUserHidden([]);

        canvas.FlyTo(n05, 0.9, animated: false);
        Render(canvas);
        tree.SetUserHidden([m03.FullPath]);
        Render(canvas);
        Check("hiding a folder the view is inside takes the folders in it off the canvas too",
            canvas.ScreenRectOf(n05) is null);
        Check("and the camera moves to a folder that is still on the canvas",
            canvas.Anchor is { } anchorAfterHide && NestedTree.IsOnCanvas(anchorAfterHide));
        tree.SetUserHidden([]);
        canvas.FitAll(animated: false);

        // ---- forty levels down ------------------------------------------------------------------
        Section("nested canvas: forty levels down");
        var deepest = tree.Find(chain[^1])!;
        var relative = 1.0;
        for (var folder = deepest; folder.Parent is not null; folder = folder.Parent)
        {
            relative *= folder.Scale;
        }

        Check("the deepest folder is forty levels below the chain's top", deepest.Depth == 42 && chain.Count == 41);
        Check($"it is {relative.ToString("0.##e0", CultureInfo.InvariantCulture)} of This PC's width, far below what one double coordinate can place",
            relative < 1e-25);

        Check("FlyTo reaches it", canvas.FlyTo(deepest, 0.9, animated: false));
        var deepRect = canvas.ScreenRectOf(deepest);
        Check("and puts it at 90% of the view to within a millionth", SameRect(deepRect, ninety, 1e-6));
        var ancestorsExact = true;
        var expectedWidth = ninety.Width;
        for (var folder = deepest; folder.Parent is not null && !folder.Parent.IsComputer; folder = folder.Parent)
        {
            expectedWidth /= folder.Scale;
            if (canvas.ScreenRectOf(folder.Parent) is not { } parentRect || !Near(parentRect.Width, expectedWidth, 1e-9 * expectedWidth))
            {
                ancestorsExact = false;
            }
        }

        Check("every folder above it is exactly its size divided by its scale", ancestorsExact);
        var deepCentre = new Point(ninety.X + ninety.Width / 2, ninety.Y + ninety.Height / 2);
        Check("a hit at its centre finds it", canvas.HitTest(deepCentre)?.Folder == deepest);
        Check("a hit on its title band finds it, on the header",
            canvas.HitTest(new Point(deepCentre.X, ninety.Y + 5)) is { IsFile: false, IsOnHeader: true } deepHit && deepHit.Folder == deepest);
        var deepFiles = HitTestFiles(canvas, deepest);
        Check($"and so are its files ({deepFiles.Tested})", deepFiles.Tested == deepest.Files.Count && deepFiles.Misses == 0);

        var magnification = canvas.ScreenRectOf(tree.Root)!.Value.Width / FitWidth;
        Check($"the zoom reads {canvas.ZoomText} for a magnification of {magnification.ToString("0.###e0", CultureInfo.InvariantCulture)}",
            Near(ParseZoom(canvas.ZoomText) / magnification, 1, 0.06));

        var deepShot = Shoot(canvas, "nested-deep-zoom.png");
        Check("the deep folder is painted exactly where the camera says",
            SameColour(deepShot, InBottomRightMargin(ninety), deepest.BodyColour));
        Check("and its parent shows around it",
            SameColour(deepShot, new Point(ninety.X - ninety.Width * NestedLayout.Gap / 2, ninety.Y + ninety.Height / 2), deepest.Parent!.BodyColour));
        Check("the trail of folders above the view is drawn",
            CountDifferent(deepShot, new Int32Rect(16, 14, 200, 18), deepest.Parent.BodyColour) > 100);

        var spot = new Point(611.5, 437.25);
        var spotOnDeep = Local(spot, ninety);
        canvas.ZoomAt(spot, 0.3);
        var outRect = canvas.ScreenRectOf(deepest)!.Value;
        Check("zooming out forty levels down keeps the spot under the cursor",
            SamePoint(Local(spot, outRect), spotOnDeep, 1e-9) && Near(outRect.Width, ninety.Width * 0.3, 1e-9 * ninety.Width));
        Check("and fixes the camera to a bigger folder around it",
            canvas.Anchor is { } bigger && !ReferenceEquals(bigger, deepest) && bigger.Contains(deepest));
        var siblingHits = HitTestFolders(canvas, deepest.Parent!, minimumWidth: 4);
        Check($"its neighbours, forty levels down, are hit as themselves ({siblingHits.Tested})",
            siblingHits.Tested >= 4 && siblingHits.HeaderMisses == 0 && siblingHits.CentreMisses == 0);
        canvas.ZoomAt(spot, 1 / 0.3);
        Check("zooming back in lands on the same rectangle", SameRect(canvas.ScreenRectOf(deepest), ninety, 1e-9));
        canvas.ZoomAt(spot, 2);
        var inRect = canvas.ScreenRectOf(deepest)!.Value;
        Check("zooming further in keeps the spot too",
            SamePoint(Local(spot, inRect), spotOnDeep, 1e-9) && Near(inRect.Width, ninety.Width * 2, 1e-9 * ninety.Width));
        canvas.ZoomAt(spot, 0.5);
        canvas.Pan(new Vector(250.5, -120.25));
        var deepPanned = canvas.ScreenRectOf(deepest)!.Value;
        Check("panning forty levels down moves by exactly the drag",
            Near(deepPanned.X - ninety.X, 250.5, 1e-6) && Near(deepPanned.Y - ninety.Y, -120.25, 1e-6));
        canvas.Pan(new Vector(-250.5, 120.25));
        Check("and back", SameRect(canvas.ScreenRectOf(deepest), ninety, 1e-9));

        // ---- the camera across a restart ------------------------------------------------------
        canvas.FlyTo(deepest, 0.9, animated: false);
        var camera = canvas.CaptureCamera();
        Check("the camera is captured as a folder and its place",
            camera is not null && ViewAllPath.Equals(camera.AnchorPath, deepest.FullPath) && Near(camera.Width, 0.9, 1e-12));
        canvas.FitAll(animated: false);
        await canvas.RestoreCameraAsync(camera!);
        Check("restoring it puts the folder back exactly", SameRect(canvas.ScreenRectOf(deepest), ninety, 1e-9));

        var other = new NestedCanvas { Tree = tree };
        other.Measure(new Size(1200, 800));
        other.Arrange(new Rect(0, 0, 1200, 800));
        other.UpdateLayout();
        await other.RestoreCameraAsync(camera!);
        Check("a new canvas of another size restores to the same share of its width",
            SameRect(other.ScreenRectOf(deepest), new Rect(60, 62.5, 1080, 1080 * NestedLayout.CellHeight), 1e-9));
        other.Tree = null;
        Check("fit-all from forty levels down is ×1 again", FitAllZoomText(canvas) == "×1");

        // ---- half way down --------------------------------------------------------------------
        var middle = tree.Find(chain[20])!;
        canvas.FlyTo(middle, 0.9, animated: false);
        var middleHits = HitTestFolders(canvas, middle, minimumWidth: 4);
        var middleFiles = HitTestFiles(canvas, middle);
        Check($"twenty levels down, every sub-folder and file is hit as itself ({middleHits.Tested} + {middleFiles.Tested})",
            middleHits.Tested >= 30 && middleHits.HeaderMisses == 0 && middleHits.CentreMisses == 0
            && middleFiles.Tested == middle.Files.Count && middleFiles.Misses == 0);

        // ---- keys ---------------------------------------------------------------------------
        canvas.FlyTo(mixed, 0.9, animated: false);
        string? selected = null;
        canvas.SelectRequested += (path, _) => selected = path;
        canvas.SetSelection([mixed.Children[0].FullPath], mixed.Children[0].FullPath);
        Check("Right selects the sub-folder to its right",
            canvas.HandleKey(Key.Right, ModifierKeys.None) && selected == mixed.Children[mixed.Grid.IndexOf(0, 1)].FullPath);
        Check("Down selects the one a row below",
            canvas.HandleKey(Key.Down, ModifierKeys.None) && selected == mixed.Children[mixed.Grid.IndexOf(1, 1)].FullPath);
        Check("Backspace selects the parent", canvas.HandleKey(Key.Back, ModifierKeys.None) && selected == mixed.FullPath);
        canvas.SetSelection([mixed.PathOf(mixed.Files[0])], mixed.PathOf(mixed.Files[0]));
        Check("Right on a file selects the file to its right",
            canvas.HandleKey(Key.Right, ModifierKeys.None) && selected == mixed.PathOf(mixed.Files[mixed.FileGrid.IndexOf(0, 1)]));
        Check("a key with a modifier is not the canvas's", !canvas.HandleKey(Key.Right, ModifierKeys.Control));

        // An animated flight runs on render ticks.  A flight lasts at most 1.1 s;
        // one that moved but has not landed after three is broken, one that never
        // moved means this machine gives a windowless thread no ticks.
        canvas.FitAll(animated: false);
        var takeOff = canvas.ScreenRectOf(filesFolder)!.Value;
        Check("an animated flight starts", canvas.FlyTo(filesFolder, 0.9, animated: true));
        await WaitUntil(() => SameRect(canvas.ScreenRectOf(filesFolder), ninety, 1e-6), 3_000);
        if (SameRect(canvas.ScreenRectOf(filesFolder), takeOff, 1e-12))
        {
            Console.WriteLine("  note  no render ticks without a window; the animated flight is not exercised");
        }
        else
        {
            Check("an animated flight lands where an instant one does", SameRect(canvas.ScreenRectOf(filesFolder), ninety, 1e-6));
        }

        canvas.FitAll(animated: false);
        canvas.Tree = null;
    }

    // ---- what a frame costs -------------------------------------------------------------------

    private static async Task NestedPerformanceChecks()
    {
        Section("nested performance");

        using var tree = new NestedTree(PerformanceRead) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"P:\", "P:", NestedFolderKind.Drive, "4 TB free")]);
        var reading = Stopwatch.StartNew();
        await LoadEverythingAsync(tree, _ => true);
        reading.Stop();

        var folders = Descendants(tree.Root, includeHidden: true).ToList();
        var files = folders.Sum(folder => folder.Files.Count);
        Check($"the synthetic disk holds {folders.Count:N0} folders and {files:N0} files",
            folders.Count >= 50_000 && files >= 190_000);
        Report($"reading {folders.Count:N0} folders into the tree", reading.ElapsedMilliseconds, 30_000);

        var canvas = new NestedCanvas { Tree = tree };
        canvas.Measure(new Size(ViewWidth, ViewHeight));
        canvas.Arrange(new Rect(0, 0, ViewWidth, ViewHeight));
        canvas.UpdateLayout();

        canvas.FitAll(animated: false);
        var fit = TimeFrames(canvas, tree, 20);
        Check("every timed fit-all frame was really drawn", fit.Frames == 20);
        Report($"fit-all frame, mean of 20 (worst {fit.Worst} ms, {fit.Cells:N0} cells)", fit.Mean, 60);
        Shoot(canvas, "nested-perf-fit-all.png");

        canvas.FlyTo(tree.Find(@"P:\a17")!, 0.9, animated: false);
        var mid = TimeFrames(canvas, tree, 20);
        Check("every timed mid-zoom frame was really drawn", mid.Frames == 20);
        Report($"mid-zoom frame, mean of 20 (worst {mid.Worst} ms, {mid.Cells:N0} cells)", mid.Mean, 60);
        Shoot(canvas, "nested-perf-mid-zoom.png");

        canvas.FlyTo(tree.Find(@"P:\a17\b20")!, 0.9, animated: false);
        var close = TimeFrames(canvas, tree, 20);
        Report($"close-up frame, mean of 20 (worst {close.Worst} ms, {close.Cells:N0} cells)", close.Mean, 60);

        canvas.FitAll(animated: false);
        canvas.ZoomAt(new Point(533, 377), 6);
        var zoomed = TimeFrames(canvas, tree, 20);
        Report($"frame zoomed ×6 between folders, mean of 20 (worst {zoomed.Worst} ms, {zoomed.Cells:N0} cells)", zoomed.Mean, 60);

        canvas.FitAll(animated: false);
        Render(canvas);
        var capture = Stopwatch.StartNew();
        var bitmap = new RenderTargetBitmap(ViewWidth, ViewHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        capture.Stop();
        Report("composing one fit-all frame into a bitmap (software, test only)", capture.ElapsedMilliseconds, 500);

        var random = new Random(7);
        var hits = 0;
        var hitting = Stopwatch.StartNew();
        for (var index = 0; index < 10_000; index++)
        {
            if (canvas.HitTest(new Point(random.NextDouble() * ViewWidth, random.NextDouble() * ViewHeight)) is not null)
            {
                hits++;
            }
        }

        hitting.Stop();
        Check("random points over the fit land on folders", hits > 9_000);
        Report("10,000 hit tests at fit-all", hitting.ElapsedMilliseconds, 300);
        canvas.Tree = null;
    }

    /// <summary>
    /// A disk computed rather than stored: 40 folders of 35 of 35, about fifty
    /// thousand folders in all, each with up to eight files.
    /// </summary>
    private static NestedListing PerformanceRead(string path, CancellationToken cancellationToken)
    {
        var depth = path.Length <= 3 ? 0 : path[3..].Count(character => character == Path.DirectorySeparatorChar) + 1;
        var (count, prefix) = depth switch
        {
            0 => (40, "a"),
            1 => (35, "b"),
            2 => (35, "c"),
            _ => (0, "")
        };

        var folders = new List<NestedEntry>(count);
        for (var index = 0; index < count; index++)
        {
            folders.Add(new NestedEntry($"{prefix}{index:D2}", false, false));
        }

        var hash = 2166136261u;
        foreach (var character in path)
        {
            hash = (hash ^ character) * 16777619u;
        }

        string[] extensions = ["txt", "png", "cs", "mp3", "pdf", "zip", "json", ""];
        var fileCount = depth == 0 ? 0 : (int)(hash % 9);
        var files = new List<NestedFile>(fileCount);
        for (var index = 0; index < fileCount; index++)
        {
            var extension = extensions[(hash >> 4) % (uint)extensions.Length];
            files.Add(new NestedFile(extension.Length == 0 ? $"file{index}" : $"file{index}.{extension}", false, 1000L * index));
        }

        files.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
        return new NestedListing(folders, fileCount, 0, false) { Files = files };
    }

    // ---- the world ---------------------------------------------------------------------------

    /// <summary>
    /// Two drives.  Q: holds a chain forty folders deep - every level thirty-six
    /// folders and a dozen files, so each is about a ninth of the one above - a
    /// folder of three hundred files, an empty one, a hidden one, a link, a
    /// folder of two thousand, and a few ordinary branches.
    /// </summary>
    private static FakeDisk BuildNestedWorld(out List<string> chain)
    {
        var disk = new FakeDisk();
        disk.Folder(@"Q:\");
        disk.Folder(@"R:\");

        chain = [];
        var current = @"Q:\chain";
        for (var level = 0; level < 40; level++)
        {
            disk.AddFiles(current, 12, $"level{level:D2}-");
            chain.Add(current);
            for (var sibling = 0; sibling < 36; sibling++)
            {
                disk.Folder(Path.Combine(current, $"s{sibling:D2}"));
            }

            current = Path.Combine(current, $"s{(level + 1) * 7 % 36:D2}");
        }

        disk.AddFiles(current, 12, "level40-");
        chain.Add(current);

        string[] stems = ["report", "photo", "notes", "build", "track"];
        string[] extensions = ["txt", "png", "md", "cs", "wav", "pdf"];
        for (var index = 0; index < 300; index++)
        {
            disk.AddFile(@"Q:\files", $"{stems[index % stems.Length]}-{index:D3}.{extensions[index % extensions.Length]}", 1024L * index);
        }

        disk.AddFile(@"Q:\files", "desktop.ini", 120, hidden: true);
        disk.AddFile(@"Q:\files", "thumbs.db", 4096, hidden: true);
        for (var index = 0; index < 6; index++)
        {
            disk.AddFiles($@"Q:\files\sub{index}", 3, "s");
        }

        disk.Folder(@"Q:\empty");

        disk.AddFiles(@"Q:\mixed", 40, "mixed-");
        for (var outer = 0; outer < 25; outer++)
        {
            disk.AddFiles($@"Q:\mixed\m{outer:D2}", 8, "m");
            for (var inner = 0; inner < 10; inner++)
            {
                disk.AddFiles($@"Q:\mixed\m{outer:D2}\n{inner:D2}", 4, "n");
            }
        }

        for (var index = 0; index < 2_000; index++)
        {
            disk.Folder($@"Q:\wide\w{index:D4}");
        }

        disk.Folder(@"Q:\$hidden").IsHidden = true;
        disk.Folder(@"Q:\$hidden\inside");
        disk.Folder(@"Q:\link").IsReparsePoint = true;

        for (var outer = 0; outer < 8; outer++)
        {
            for (var inner = 0; inner < 12; inner++)
            {
                disk.AddFiles($@"Q:\g{outer:D2}\h{inner:D2}", 6, "g");
            }
        }

        foreach (var name in new[] { "one", "two", "three" })
        {
            disk.AddFiles($@"R:\{name}", 5, name);
        }

        return disk;
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static IEnumerable<NestedFolder> Descendants(NestedFolder folder, bool includeHidden)
    {
        var stack = new Stack<NestedFolder>();
        stack.Push(folder);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var child in includeHidden ? current.AllChildren : [.. current.Children])
            {
                yield return child;
                stack.Push(child);
            }
        }
    }

    private static void Render(NestedCanvas canvas)
    {
        canvas.InvalidateVisual();
        canvas.UpdateLayout();
    }

    /// <summary>Draws the canvas into a bitmap, and keeps it as a PNG when NESTED_SHOTS names a folder.</summary>
    private static RenderTargetBitmap Shoot(NestedCanvas canvas, string name)
    {
        Render(canvas);
        var bitmap = new RenderTargetBitmap((int)canvas.ActualWidth, (int)canvas.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        bitmap.Freeze();

        if (Environment.GetEnvironmentVariable("NESTED_SHOTS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
            ShotsWritten.Add(path);
        }

        return bitmap;
    }

    private static (long Mean, long Worst, int Cells, long Frames) TimeFrames(NestedCanvas canvas, NestedTree tree, int count)
    {
        Render(canvas);
        var startFrame = tree.Frame;
        var worst = 0.0;
        var total = Stopwatch.StartNew();
        for (var index = 0; index < count; index++)
        {
            var started = Stopwatch.GetTimestamp();
            Render(canvas);
            worst = Math.Max(worst, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        total.Stop();
        return ((long)Math.Ceiling(total.Elapsed.TotalMilliseconds / count), (long)Math.Ceiling(worst), canvas.DrawnCellCount, tree.Frame - startFrame);
    }

    private static string FitAllZoomText(NestedCanvas canvas)
    {
        canvas.FitAll(animated: false);
        return canvas.ZoomText;
    }

    /// <summary>
    /// Hit tests every folder at least <paramref name="minimumWidth"/> wide and
    /// wholly on screen, from <paramref name="start"/> down: a point on its
    /// title band must hit exactly it, and its centre it or something inside it.
    /// </summary>
    private static (int Tested, int HeaderMisses, int CentreMisses, string FirstMiss) HitTestFolders(
        NestedCanvas canvas,
        NestedFolder start,
        double minimumWidth)
    {
        var view = new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight);
        var (tested, headerMisses, centreMisses, firstMiss) = (0, 0, 0, string.Empty);
        var stack = new Stack<NestedFolder>();
        stack.Push(start);
        while (stack.Count > 0 && tested < 5_000)
        {
            var folder = stack.Pop();
            if (canvas.ScreenRectOf(folder) is not { } rect || rect.Width < minimumWidth)
            {
                continue;
            }

            foreach (var child in folder.Children)
            {
                stack.Push(child);
            }

            var title = new Point(rect.X + rect.Width / 2, rect.Y + rect.Width * NestedLayout.HeaderHeight / 2);
            var centre = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            if (folder.IsComputer || !view.Contains(title) || !view.Contains(centre))
            {
                continue;
            }

            tested++;
            if (canvas.HitTest(title) is not { IsFile: false } hit || !ReferenceEquals(hit.Folder, folder))
            {
                headerMisses++;
                firstMiss = firstMiss.Length > 0 ? firstMiss : $"title of {folder} at {title} hit {canvas.HitTest(title)?.Path ?? "nothing"}";
            }

            if (canvas.HitTest(centre) is not { } inner || !folder.Contains(inner.Folder))
            {
                centreMisses++;
                firstMiss = firstMiss.Length > 0 ? firstMiss : $"centre of {folder} at {centre} hit {canvas.HitTest(centre)?.Path ?? "nothing"}";
            }
        }

        return (tested, headerMisses, centreMisses, firstMiss);
    }

    /// <summary>Hit tests the centre of every tile of <paramref name="folder"/> that is on screen, and resolves each file's path.</summary>
    private static (int Tested, int Misses, string FirstMiss) HitTestFiles(NestedCanvas canvas, NestedFolder folder)
    {
        var view = new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight);
        var rect = canvas.ScreenRectOf(folder)!.Value;
        var grid = folder.FileGrid;
        var (tested, misses, firstMiss) = (0, 0, string.Empty);
        for (var index = 0; index < folder.Files.Count; index++)
        {
            var (fx, fy) = grid.Origin(index);
            var tile = new Rect(rect.X + fx * rect.Width, rect.Y + fy * rect.Width, grid.TileWidth * rect.Width, grid.TileHeight * rect.Width);
            var centre = new Point(tile.X + tile.Width / 2, tile.Y + tile.Height / 2);
            if (!view.Contains(centre))
            {
                continue;
            }

            tested++;
            var path = folder.PathOf(folder.Files[index]);
            var hit = canvas.HitTest(centre);
            var resolved = canvas.Resolve(path);
            var ok = hit is { IsFile: true } h
                     && ReferenceEquals(h.Folder, folder)
                     && h.FileIndex == index
                     && h.Path == path
                     && SameRect(h.Bounds, tile, 1e-9)
                     && resolved is { } r && ReferenceEquals(r.Folder, folder) && r.FileIndex == index;
            if (!ok)
            {
                misses++;
                firstMiss = firstMiss.Length > 0 ? firstMiss : $"{path}: hit {hit?.Path ?? "nothing"}, resolved to {resolved?.FileIndex}";
            }
        }

        return (tested, misses, firstMiss);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMilliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            await Task.Delay(5);
        }
    }

    /// <summary>"×12", "×1.5k", "×2.3e38" and the like, read back as a number, in the culture it was written in.</summary>
    private static double ParseZoom(string text)
    {
        var body = text.TrimStart('×');
        var multiplier = 1.0;
        if (body.EndsWith('k'))
        {
            multiplier = 1e3;
            body = body[..^1];
        }
        else if (body.EndsWith('M'))
        {
            multiplier = 1e6;
            body = body[..^1];
        }

        return double.Parse(body, NumberStyles.Float, CultureInfo.CurrentCulture) * multiplier;
    }

    private static bool Near(double actual, double expected, double tolerance) => Math.Abs(actual - expected) <= tolerance;

    /// <summary>Equal to within <paramref name="relative"/> of the expected width, in every coordinate.</summary>
    private static bool SameRect(Rect? actual, Rect expected, double relative)
    {
        if (actual is not { } rect)
        {
            return false;
        }

        var tolerance = Math.Max(relative * expected.Width, 1e-12);
        return Near(rect.X, expected.X, tolerance)
               && Near(rect.Y, expected.Y, tolerance)
               && Near(rect.Width, expected.Width, tolerance)
               && Near(rect.Height, expected.Height, tolerance);
    }

    private static bool SamePoint(Point actual, Point expected, double tolerance) =>
        Near(actual.X, expected.X, tolerance) && Near(actual.Y, expected.Y, tolerance);

    /// <summary>Where a screen point is inside a folder's cell, in units of the cell's width.</summary>
    private static Point Local(Point screen, Rect cell) => new((screen.X - cell.X) / cell.Width, (screen.Y - cell.Y) / cell.Width);

    /// <summary>A point in a cell's bottom-right margin, where nothing of its contents is ever drawn.</summary>
    private static Point InBottomRightMargin(Rect cell) =>
        new(cell.Right - cell.Width * NestedLayout.Padding / 2, cell.Bottom - cell.Width * NestedLayout.Padding / 2);

    private static Int32Rect FileZone(NestedFolder folder, Rect cell)
    {
        var grid = folder.FileGrid;
        var top = cell.Y + grid.Top * cell.Width;
        var height = (grid.Rows * grid.StepY - grid.Gap) * cell.Width;
        var left = cell.X + grid.Left * cell.Width;
        var width = (grid.Columns * grid.StepX - grid.Gap) * cell.Width;
        return new Int32Rect(
            Math.Max(0, (int)left),
            Math.Max(0, (int)top),
            Math.Min(ViewWidth - Math.Max(0, (int)left), (int)width),
            Math.Min(ViewHeight - Math.Max(0, (int)top), (int)height));
    }

    private static uint[] PixelsOf(BitmapSource bitmap, Int32Rect area)
    {
        var pixels = new uint[area.Width * area.Height];
        bitmap.CopyPixels(area, pixels, area.Width * 4, 0);
        return pixels;
    }

    private static bool SameColour(BitmapSource bitmap, Point at, uint expected)
    {
        var pixel = PixelsOf(bitmap, new Int32Rect((int)at.X, (int)at.Y, 1, 1))[0];
        var ok = true;
        for (var shift = 0; shift < 32; shift += 8)
        {
            ok &= Math.Abs((int)((pixel >> shift) & 0xFF) - (int)((expected >> shift) & 0xFF)) <= 2;
        }

        if (!ok)
        {
            Console.WriteLine($"        pixel at {at} is #{pixel:X8}, expected #{expected:X8}");
        }

        return ok;
    }

    private static int DistinctColours(BitmapSource bitmap) =>
        PixelsOf(bitmap, new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight)).Distinct().Count();

    /// <summary>Near-white pixels: text, since no cell or tile is anywhere near that light.</summary>
    private static int BrightPixels(BitmapSource bitmap, Int32Rect area) =>
        PixelsOf(bitmap, area).Count(pixel => ((pixel >> 16) & 0xFF) > 200 && ((pixel >> 8) & 0xFF) > 200 && (pixel & 0xFF) > 200);

    private static int CountDifferent(BitmapSource bitmap, Int32Rect area, uint colour) =>
        PixelsOf(bitmap, area).Count(pixel => (pixel | 0xFF000000) != (colour | 0xFF000000));
}
