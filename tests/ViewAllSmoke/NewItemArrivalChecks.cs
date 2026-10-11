using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;

namespace ViewAllSmoke;

/// <summary>Files really added to disposable directories reach the tree through
/// its normal refresh/watch contract, then are drawn by manual canvas frames.
/// No arrival is inserted directly into the animation's internal state.</summary>
internal static partial class Program
{
    private static Task NewItemArrivalChecks()
    {
        RunOnSta("new items appear once on the nested canvas", async () =>
        {
            await ArrivalIntakeChecksAsync();
            await ArrivalIncompleteAndArchiveChecksAsync();
            await ArrivalVisiblePulseChecksAsync();
            await ArrivalOffscreenPaneChecksAsync();
            await ArrivalDeferredVisibilityChecksAsync();
            await ArrivalTinyTileChecksAsync();
            await ArrivalActualWatchChecksAsync();
        });
        return Task.CompletedTask;
    }

    private static async Task ArrivalIntakeChecksAsync()
    {
        Section("new-item arrivals: real refresh distinguishes additions from existing entries");
        var root = ArrivalDirectory();
        try
        {
            var existingFile = Path.Combine(root, "existing.txt");
            var existingFolder = Path.Combine(root, "existing-folder");
            File.WriteAllText(existingFile, "old");
            for (var index = 0; index < 32; index++) File.WriteAllText(Path.Combine(root, $"old-name-{index:D2}.txt"), "baseline");
            Directory.CreateDirectory(existingFolder);
            File.WriteAllText(Path.Combine(existingFolder, "already-there.txt"), "initial");
            using var tree = new NestedTree { IsReadingOnDemand = false, PostBackground = null };
            var arrivals = new List<NestedArrival>();
            tree.ItemsAdded += batch => arrivals.AddRange(batch);
            tree.SetRoots([new NestedRoot(root, "arrival fixture", NestedFolderKind.Drive)]);
            var top = tree.Find(root)!;
            await tree.LoadAsync(top);
            Check("the initial complete listing never marks its existing file or folder as new", arrivals.Count == 0
                && top.Files.Any(file => file.Name == "existing.txt") && top.Children.Any(folder => folder.Name == "existing-folder"));
            await tree.LoadAsync(tree.Find(existingFolder)!);
            Check("first opening an unread child does not flash its pre-existing contents", arrivals.Count == 0);
            await tree.RefreshAsync(top);
            File.WriteAllText(existingFile, "changed existing contents");
            await tree.RefreshAsync(top);
            Check("unchanged F5 and an existing file's metadata change produce no arrivals", arrivals.Count == 0
                && top.Files.Single(file => file.Name == "existing.txt").Length == new FileInfo(existingFile).Length);

            var addedFile = Path.Combine(root, "extracted.txt");
            var addedFolder = Path.Combine(root, "dropped-folder");
            File.WriteAllText(addedFile, "new file");
            Directory.CreateDirectory(addedFolder);
            await tree.RefreshAsync(top);
            Check("a real new file and directory arrive once through the ordinary refresh", arrivals.Count == 2
                && arrivals.Any(item => ReferenceEquals(item.Parent, top) && item.Name == "extracted.txt" && !item.IsDirectory)
                && arrivals.Any(item => ReferenceEquals(item.Parent, top) && item.Name == "dropped-folder" && item.IsDirectory));
            await tree.RefreshAsync(top);
            Check("a second refresh does not announce the same additions again", arrivals.Count == 2);
            arrivals.Clear();

            var hiddenFile = Path.Combine(root, "hidden-new.txt");
            var hiddenFolder = Path.Combine(root, "hidden-new-folder");
            File.WriteAllText(hiddenFile, "hidden");
            File.SetAttributes(hiddenFile, FileAttributes.Hidden);
            Directory.CreateDirectory(hiddenFolder).Attributes |= FileAttributes.Hidden;
            await tree.RefreshAsync(top);
            Check("hidden additions remain arrivals even while their tiles are not displayed", arrivals.Count == 2
                && arrivals.Any(item => item.Name == "hidden-new.txt" && !item.IsDirectory)
                && arrivals.Any(item => item.Name == "hidden-new-folder" && item.IsDirectory)
                && top.Files.All(file => file.Name != "hidden-new.txt") && top.Children.All(folder => folder.Name != "hidden-new-folder"));
            arrivals.Clear();
            tree.IncludeHidden = true;
            tree.IncludeHidden = false;
            tree.FileNameFilter = name => name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
            tree.FileNameFilter = null;
            Check("show/hide and file-type filters never invent a new arrival", arrivals.Count == 0);

            File.Move(existingFile, Path.Combine(root, "renamed.txt"));
            Directory.Move(existingFolder, Path.Combine(root, "renamed-folder"));
            tree.OnFolderChanged(top, new FolderChange(root, ChangeKinds.Structural, 0,
                new RenamePair[] { new("existing.txt", "renamed.txt"), new("existing-folder", "renamed-folder") }, default));
            await tree.RefreshAsync(top);
            Check("real renames delivered by the watcher contract do not flash as additions", arrivals.Count == 0
                && top.Files.Any(file => file.Name == "renamed.txt") && top.Children.Any(folder => folder.Name == "renamed-folder"));

            File.Move(Path.Combine(root, "renamed.txt"), Path.Combine(root, "rename-temp.txt"));
            File.Move(Path.Combine(root, "rename-temp.txt"), Path.Combine(root, "rename-final.txt"));
            File.WriteAllText(Path.Combine(root, "genuinely-new.txt"), "new amid renames");
            tree.OnFolderChanged(top, new FolderChange(root, ChangeKinds.Structural, 0,
                new RenamePair[] { new("renamed.txt", "rename-temp.txt"), new("rename-temp.txt", "rename-final.txt") }, default));
            await tree.RefreshAsync(top);
            Check("a rename chain mixed with a new file reports only the genuinely new file", arrivals.Count == 1
                && arrivals[0].Name == "genuinely-new.txt" && !arrivals[0].IsDirectory);
            arrivals.Clear();
            var largeRenames = new List<RenamePair>();
            for (var index = 0; index < 32; index++)
            {
                var oldName = $"old-name-{index:D2}.txt";
                var newName = $"new-name-{index:D2}.txt";
                File.Move(Path.Combine(root, oldName), Path.Combine(root, newName));
                largeRenames.Add(new(oldName, newName));
            }
            File.WriteAllText(Path.Combine(root, "added-amid-32-renames.txt"), "genuinely added");
            tree.OnFolderChanged(top, new FolderChange(root, ChangeKinds.Structural, 0, largeRenames.ToArray(), default));
            await tree.RefreshAsync(top);
            Check("a watcher batch beyond the old sixteen-rename bound still flashes only its one genuine addition", arrivals.Count == 1
                && arrivals[0].Name == "added-amid-32-renames.txt");
        }
        finally { TryDelete(root); }
    }

    private static async Task ArrivalIncompleteAndArchiveChecksAsync()
    {
        Section("new-item arrivals: incomplete listings and archive presentation never invent additions");
        var disk = new FakeDisk();
        disk.AddFile(@"Q:\limited", "known.txt", 1);
        disk.Folder(@"Q:\limited").IsTruncated = true;
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false, PostBackground = null };
        var arrivals = new List<NestedArrival>();
        tree.ItemsAdded += batch => arrivals.AddRange(batch);
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await tree.LoadAsync(tree.Find(@"Q:\")!);
        var limited = tree.Find(@"Q:\limited")!;
        await tree.LoadAsync(limited);
        disk.AddFile(limited.FullPath, "previously-omitted.txt", 2);
        disk.Folder(limited.FullPath).IsTruncated = false;
        await tree.RefreshAsync(limited);
        Check("recovering an incomplete listing does not flash names it could already have contained", arrivals.Count == 0
            && limited.Files.Any(file => file.Name == "previously-omitted.txt"));
        disk.AddFile(limited.FullPath, "created-after-complete-baseline.txt", 3);
        await tree.RefreshAsync(limited);
        Check("a genuinely new file after the complete recovery baseline does arrive", arrivals.Count == 1
            && arrivals[0].Name == "created-after-complete-baseline.txt");

        var expandedArchive = false;
        using var archiveTree = new NestedTree((_, _) => expandedArchive
            ? new NestedListing([new NestedEntry("existing.zip", false, false, IsArchive: true)], 0, 0, false)
            : new NestedListing([], 1, 0, false) { Files = [new NestedFile("existing.zip", false, 100)] })
            { IsReadingOnDemand = false, PostBackground = null };
        var archiveArrivals = 0;
        archiveTree.ItemsAdded += batch => archiveArrivals += batch.Count;
        archiveTree.SetRoots([new NestedRoot(@"R:\", "R:", NestedFolderKind.Drive)]);
        var archiveRoot = archiveTree.Find(@"R:\")!;
        await archiveTree.LoadAsync(archiveRoot);
        expandedArchive = true;
        await archiveTree.RefreshAsync(archiveRoot);
        Check("presenting an existing archive file as a directory changes its view without a new-item flash", archiveArrivals == 0
            && archiveRoot.Children.Any(child => child.Name == "existing.zip"));
    }

    private static async Task ArrivalVisiblePulseChecksAsync()
    {
        Section("new-item arrivals: real file and folder draw two yellow pulses without navigation or selection");
        var root = ArrivalDirectory();
        try
        {
            var selected = Path.Combine(root, "selected.txt");
            File.WriteAllText(selected, "existing selection");
            Directory.CreateDirectory(Path.Combine(root, "existing-folder"));
            using var tree = await ArrivalTreeAsync(root);
            var top = tree.Find(root)!;
            var canvas = ArrivalCanvas(tree);
            try
            {
                canvas.FlyTo(top, 0.94, animated: false);
                var time = await ArrivalSettleAsync(canvas, TimeSpan.FromSeconds(100));
                canvas.SetSelection([selected], selected);
                canvas.RunFrameForTests(time += TimeSpan.FromMilliseconds(40));
                Check("the initial scene contains no arrival overlay", canvas.LastArrivalHighlights.Count == 0 && canvas.PendingArrivalCount == 0);
                var camera = canvas.CaptureCamera();
                var selectedBefore = canvas.SelectionState.Items().Select(item => item.Path).ToArray();
                var focusBefore = canvas.SelectionState.Active?.Path;
                var commits = 0;
                var navigations = 0;
                canvas.SelectionCommitted += _ => commits++;
                canvas.CameraChanged += () => navigations++;
                var file = Path.Combine(root, "new-file.txt");
                var folder = Path.Combine(root, "new-folder");
                File.WriteAllText(file, "arrived by extraction");
                Directory.CreateDirectory(folder);
                await tree.RefreshAsync(top);
                canvas.RunFrameForTests(time += TimeSpan.FromMilliseconds(40));
                var started = time;
                // Ordinary refresh legitimately remaps selection's file bits
                // to the new listing. Its semantic selected paths stay fixed;
                // the pulse must never change that remapped version again.
                var selection = canvas.SelectionState.Version;
                canvas.RunFrameForTests(started + TimeSpan.FromMilliseconds(300));
                Check("the first peak draws both the added file tile and the added directory", ArrivalPathIsLit(canvas, file)
                    && ArrivalPathIsLit(canvas, folder) && canvas.ActiveArrivalCount == 2);
                Check("the drawing contains the same real yellow paint used by the pulse", ArrivalHasYellowPaint(canvas));
                var fileRect = ArrivalFileRect(canvas, top, "new-file.txt");
                var folderRect = canvas.ScreenRectOf(tree.Find(folder)!)!.Value;
                Check("the yellow commands are placed on the exact new file and directory cells", canvas.LastArrivalHighlights.Any(highlight =>
                        ViewAllPath.Equals(highlight.Path, file) && ArrivalRectsMatch(highlight.Bounds, fileRect))
                    && canvas.LastArrivalHighlights.Any(highlight => ViewAllPath.Equals(highlight.Path, folder) && ArrivalRectsMatch(highlight.Bounds, folderRect)));
                ArrivalSaveShot(canvas, "new-item-first-pulse");
                canvas.RunFrameForTests(started + TimeSpan.FromMilliseconds(600));
                Check("the two flashes have a genuinely dark gap between them", !ArrivalPathIsLit(canvas, file, 0.001)
                    && !ArrivalPathIsLit(canvas, folder, 0.001) && !ArrivalHasYellowPaint(canvas));
                var scenes = canvas.RenderCount;
                var labels = canvas.LabelLayerCount;
                var selections = canvas.SelectionRecordCount;
                canvas.RunFrameForTests(started + TimeSpan.FromMilliseconds(900));
                Check("the second peak is visibly yellow again", ArrivalPathIsLit(canvas, file) && ArrivalPathIsLit(canvas, folder)
                    && ArrivalHasYellowPaint(canvas));
                Check("the pulse animates its overlay without repainting cells, names or the selection", canvas.RenderCount == scenes
                    && canvas.LabelLayerCount == labels && canvas.SelectionRecordCount == selections
                    && canvas.LastFrameLayers == NestedCanvas.Layers.Overlay);
                canvas.RunFrameForTests(time = started + TimeSpan.FromMilliseconds(NestedCanvas.ArrivalPulseMilliseconds + 40));
                Check("after two pulses the overlay is removed and nothing remains active", canvas.LastArrivalHighlights.Count == 0
                    && !ArrivalHasYellowPaint(canvas) && canvas.PendingArrivalCount == 0 && canvas.ActiveArrivalCount == 0);
                time = await ArrivalSettleAsync(canvas, time);
                Check("the canvas returns to idle and releases the frame loop after the pulse", canvas.IsIdle && !canvas.IsFrameHooked && !canvas.HasPendingWork);
                Check("creation and both pulses keep the user's camera and selection unchanged", canvas.CaptureCamera() == camera
                    && canvas.SelectionState.Version == selection && canvas.SelectedCount == 1 && commits == 0 && navigations == 0
                    && canvas.SelectionState.Items().Select(item => item.Path).SequenceEqual(selectedBefore, StringComparer.OrdinalIgnoreCase)
                    && canvas.SelectionState.Active?.Path == focusBefore);
                await tree.RefreshAsync(top);
                canvas.RunFrameForTests(time += TimeSpan.FromMilliseconds(40));
                Check("ordinary F5 never restarts a completed pulse", canvas.LastArrivalHighlights.Count == 0 && canvas.PendingArrivalCount == 0);
            }
            finally { canvas.Tree = null; }
        }
        finally { TryDelete(root); }
    }

    private static async Task ArrivalOffscreenPaneChecksAsync()
    {
        Section("new-item arrivals: an offscreen item waits for its own pane's first visible frame");
        var root = ArrivalDirectory();
        try
        {
            var nearPath = Path.Combine(root, "near");
            var farPath = Path.Combine(root, "far");
            Directory.CreateDirectory(nearPath);
            Directory.CreateDirectory(farPath);
            File.WriteAllText(Path.Combine(nearPath, "old.txt"), "baseline");
            File.WriteAllText(Path.Combine(farPath, "old.txt"), "baseline");
            using var firstTree = await ArrivalTreeAsync(root);
            using var secondTree = await ArrivalTreeAsync(root);
            var first = ArrivalCanvas(firstTree);
            var second = ArrivalCanvas(secondTree);
            try
            {
                first.FlyTo(firstTree.Find(farPath)!, 0.94, animated: false);
                second.FlyTo(secondTree.Find(nearPath)!, 0.94, animated: false);
                var time = await ArrivalSettleAsync(first, TimeSpan.FromSeconds(100));
                time = await ArrivalSettleAsync(second, time);
                var secondCamera = second.CaptureCamera();
                var outside = second.ScreenRectOf(secondTree.Find(farPath)!)!.Value;
                Check("the second pane actually has the destination outside its viewport", !outside.IntersectsWith(new Rect(0, 0, 1200, 800)));
                var added = Path.Combine(farPath, "arrived-offscreen.txt");
                File.WriteAllText(added, "new in both pane trees");
                await firstTree.RefreshAsync(firstTree.Find(farPath)!);
                await secondTree.RefreshAsync(secondTree.Find(farPath)!);
                first.RunFrameForTests(time += TimeSpan.FromMilliseconds(40));
                second.RunFrameForTests(time);
                first.RunFrameForTests(time + TimeSpan.FromMilliseconds(300));
                second.RunFrameForTests(time + TimeSpan.FromMilliseconds(300));
                Check("the visible pane flashes while the other preserves its own unseen arrival", ArrivalPathIsLit(first, added)
                    && second.LastArrivalHighlights.Count == 0 && second.PendingArrivalCount == 1 && second.ActiveArrivalCount == 0);
                Console.WriteLine($"  arrival before completion: first {ArrivalState(first)}; second {ArrivalState(second)}");
                // Render both visible flashes; a single four-second skipped
                // frame deliberately pauses the animation's visible clock.
                first.RunFrameForTests(time + TimeSpan.FromMilliseconds(600));
                first.RunFrameForTests(time + TimeSpan.FromMilliseconds(900));
                first.RunFrameForTests(time + TimeSpan.FromMilliseconds(NestedCanvas.ArrivalPulseMilliseconds + 40));
                first.RunFrameForTests(time + TimeSpan.FromSeconds(4));
                second.RunFrameForTests(time += TimeSpan.FromSeconds(4));
                time = await ArrivalSettleAsync(second, time);
                Console.WriteLine($"  arrival after visible completion/offscreen wait: first {ArrivalState(first)}; second {ArrivalState(second)}");
                Check("waiting offscreen for longer than the pulse consumes no flash or continuous frames", second.PendingArrivalCount == 1
                    && second.LastArrivalHighlights.Count == 0 && second.IsIdle && !second.IsFrameHooked && !second.HasPendingWork
                    && second.CaptureCamera() == secondCamera && first.PendingArrivalCount == 0);
                var renamed = Path.Combine(farPath, "renamed-before-visible.txt");
                File.Move(added, renamed);
                var secondFar = secondTree.Find(farPath)!;
                secondTree.OnFolderChanged(secondFar, new FolderChange(farPath, ChangeKinds.Structural, 0,
                    new RenamePair[] { new(Path.GetFileName(added), Path.GetFileName(renamed)) }, default));
                await secondTree.RefreshAsync(secondFar);
                Check("a new item renamed while unseen keeps one pending signal rather than gaining or losing one", second.PendingArrivalCount == 1);
                second.FlyTo(secondTree.Find(farPath)!, 0.94, animated: false);
                var pulses = ArrivalPulseBands(second, renamed, time += TimeSpan.FromMilliseconds(40));
                Console.WriteLine($"  arrival after second-pane reveal ({pulses} bands): first {ArrivalState(first)}; second {ArrivalState(second)}");
                Check("entering the second pane's viewport later still produces exactly two actual flashes", pulses == 2
                    && second.PendingArrivalCount == 0 && second.LastArrivalHighlights.Count == 0);
                Check("another pane's later pulse does not restart the already consumed arrival", first.PendingArrivalCount == 0 && first.LastArrivalHighlights.Count == 0);
            }
            finally { first.Tree = null; second.Tree = null; }
        }
        finally { TryDelete(root); }
    }

    private static async Task ArrivalDeferredVisibilityChecksAsync()
    {
        Section("new-item arrivals: hidden files, disabled file layer and excluded types wait until genuinely displayed");
        foreach (var mode in new[] { "hidden", "files-off", "type-filter", "name-filter" })
        {
            var root = ArrivalDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "existing.txt"), "baseline");
                using var tree = await ArrivalTreeAsync(root);
                var top = tree.Find(root)!;
                var canvas = ArrivalCanvas(tree);
                try
                {
                    canvas.FlyTo(top, 0.94, animated: false);
                    if (mode == "files-off") canvas.ShownLayers &= ~CanvasLayer.Files;
                    if (mode == "type-filter") tree.FileNameFilter = name => name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
                    if (mode == "name-filter") canvas.SetFilter("existing");
                    var time = await ArrivalSettleAsync(canvas, TimeSpan.FromSeconds(100));
                    var file = Path.Combine(root, mode == "type-filter" ? "new.log" : "new.txt");
                    File.WriteAllText(file, "new but not yet drawable");
                    if (mode == "hidden") File.SetAttributes(file, FileAttributes.Hidden);
                    await tree.RefreshAsync(top);
                    canvas.RunFrameForTests(time += TimeSpan.FromMilliseconds(40));
                    canvas.RunFrameForTests(time += TimeSpan.FromSeconds(4));
                    time = await ArrivalSettleAsync(canvas, time);
                    Check($"{mode}: an undrawn file keeps its one pending arrival without an overlay or busy loop", canvas.PendingArrivalCount == 1
                        && canvas.ActiveArrivalCount == 0 && canvas.LastArrivalHighlights.Count == 0 && canvas.IsIdle && !canvas.IsFrameHooked);
                    if (mode == "hidden") tree.IncludeHidden = true;
                    if (mode == "files-off") canvas.ShownLayers |= CanvasLayer.Files;
                    if (mode == "type-filter") tree.FileNameFilter = null;
                    if (mode == "name-filter") canvas.SetFilter(null);
                    var pulses = ArrivalPulseBands(canvas, file, time += TimeSpan.FromMilliseconds(40));
                    Check($"{mode}: making that same file drawable starts exactly two yellow flashes", pulses == 2 && canvas.PendingArrivalCount == 0);
                }
                finally { canvas.Tree = null; }
            }
            finally { TryDelete(root); }
        }
    }

    private static async Task ArrivalTinyTileChecksAsync()
    {
        Section("new-item arrivals: a subpixel file tile does not spend its flash before zooming in");
        var disk = new FakeDisk();
        for (var index = 0; index < 10000; index++) disk.AddFile($@"Q:\many\folder{index:D4}", "old.txt", 3);
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false, PostBackground = null };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await tree.LoadAsync(tree.Find(@"Q:\")!);
        await tree.LoadAsync(tree.Find(@"Q:\many")!);
        var parent = tree.Find(@"Q:\many\folder0000")!;
        await tree.LoadAsync(parent);
        var canvas = ArrivalCanvas(tree);
        try
        {
            canvas.FlyTo(tree.Find(@"Q:\many")!, 0.94, animated: false);
            var time = await ArrivalSettleAsync(canvas, TimeSpan.FromSeconds(100));
            disk.AddFile(parent.FullPath, "new.txt", 5);
            await tree.RefreshAsync(parent);
            canvas.RunFrameForTests(time += TimeSpan.FromMilliseconds(40));
            var tiny = ArrivalFileRect(canvas, parent, "new.txt");
            Check($"the file is genuinely below drawable tile LOD ({tiny.Width:0.0} x {tiny.Height:0.0} DIPs)", tiny.Height < 2.5);
            canvas.RunFrameForTests(time += TimeSpan.FromSeconds(4));
            Check("a tile too small to draw keeps its pending arrival", canvas.PendingArrivalCount == 1 && canvas.ActiveArrivalCount == 0
                && canvas.LastArrivalHighlights.Count == 0);
            canvas.FlyTo(parent, 0.94, animated: false);
            var pulses = ArrivalPulseBands(canvas, parent.PathOf(parent.Files.Single(file => file.Name == "new.txt")), time += TimeSpan.FromMilliseconds(40));
            Check("zooming in later draws the file's two flashes rather than a parent-folder fallback", pulses == 2 && canvas.PendingArrivalCount == 0);
        }
        finally { canvas.Tree = null; }
    }

    private static async Task ArrivalActualWatchChecksAsync()
    {
        Section("new-item arrivals: a real directory watch automatically flashes an externally copied file and extracted folder");
        var baseDirectory = ArrivalDirectory();
        var root = Path.Combine(baseDirectory, "watched");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "existing.txt"), "baseline");
        var staging = Path.Combine(baseDirectory, "outside-watched-folder.txt");
        File.WriteAllText(staging, "external source payload");
        try
        {
            using var hub = new ChangeHub(TimeProvider.System);
            var watch = hub.AddRootForTests(root);
            var armed = await LiveWait(() => watch.State == WatchState.Armed && watch.Handle is { IsInvalid: false, IsClosed: false }, 5000) >= 0;
            Check("the disposable real directory watcher is armed before any additions", armed);
            if (!armed) return;
            using var tree = new NestedTree { Changes = hub, PostBackground = null };
            var arrivals = new List<NestedArrival>();
            tree.ItemsAdded += batch => arrivals.AddRange(batch);
            tree.SetRoots([new NestedRoot(root, "watched arrival fixture", NestedFolderKind.Drive)]);
            var top = tree.Find(root)!;
            await tree.LoadAsync(top);
            var canvas = ArrivalCanvas(tree);
            try
            {
                canvas.AttachChanges(hub, tree);
                canvas.FlyTo(top, 0.94, animated: false);
                var time = await ArrivalSettleAsync(canvas, TimeSpan.FromSeconds(100));
                Check("the watched parent establishes a complete baseline without a flash", top.IsLoaded && tree.LiveRegisteredCount == 1
                    && arrivals.Count == 0 && canvas.PendingArrivalCount == 0);
                var records = watch.Watcher!.RecordCount;
                var taken = tree.LiveChangesTaken;
                var file = Path.Combine(root, "copied-from-outside.txt");
                var folder = Path.Combine(root, "extracted-directory");
                await Task.Run(() =>
                {
                    File.Copy(staging, file);
                    Directory.CreateDirectory(folder);
                });
                // Only ordinary canvas frames drain the real watch and its
                // resulting read. No Refresh, Touch, synthetic notification
                // or arrival-state insertion participates in this scenario.
                var wait = await ArrivalDriveUntilAsync(canvas, time, () => ArrivalPathIsLit(canvas, file, 0.01)
                    && ArrivalPathIsLit(canvas, folder, 0.01), 5000);
                time = wait.Time;
                Check("real OS notifications are consumed through the normal canvas hub phase", watch.Watcher.RecordCount > records
                    && wait.HubItems > 0 && tree.LiveChangesTaken > taken);
                Check("the watcher-triggered read announces exactly the genuinely new file and folder", arrivals.Count == 2
                    && arrivals.Any(item => ReferenceEquals(item.Parent, top) && item.Name == Path.GetFileName(file) && !item.IsDirectory)
                    && arrivals.Any(item => ReferenceEquals(item.Parent, top) && item.Name == Path.GetFileName(folder) && item.IsDirectory)
                    && top.AllFiles.Any(item => item.Name == Path.GetFileName(file)) && top.AllChildren.Any(item => item.Name == Path.GetFileName(folder)));
                Check("the external copy and extraction become actual yellow overlays without manual refresh", wait.Ready && ArrivalHasYellowPaint(canvas));
                var complete = await ArrivalDriveUntilAsync(canvas, time,
                    () => canvas.PendingArrivalCount == 0 && canvas.IsIdle && !canvas.IsFrameHooked, 5000);
                Check("after that automatic feedback the watched canvas returns to idle without replaying arrivals", complete.Ready
                    && arrivals.Count == 2 && canvas.LastArrivalHighlights.Count == 0 && !canvas.HasPendingWork);
            }
            finally { canvas.AttachChanges(null, null); canvas.Tree = null; tree.Changes = null; }
        }
        finally { TryDelete(baseDirectory); }
    }

    private static async Task<(bool Ready, TimeSpan Time, int HubItems)> ArrivalDriveUntilAsync(NestedCanvas canvas,
        TimeSpan time, Func<bool> ready, int milliseconds)
    {
        var timeout = Stopwatch.StartNew();
        var hubItems = 0;
        do
        {
            canvas.RunFrameForTests(time += TimeSpan.FromMilliseconds(16));
            hubItems += canvas.LastFrameStats.HubItems;
            if (ready()) return (true, time, hubItems);
            await Task.Delay(8);
        } while (timeout.ElapsedMilliseconds < milliseconds);
        return (ready(), time, hubItems);
    }

    private static async Task<NestedTree> ArrivalTreeAsync(string root)
    {
        var tree = new NestedTree { IsReadingOnDemand = false, PostBackground = null };
        tree.SetRoots([new NestedRoot(root, "arrival fixture", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        return tree;
    }

    private static NestedCanvas ArrivalCanvas(NestedTree tree)
    {
        var canvas = new NestedCanvas { Tree = tree, FramesByHandForTests = true, DpiOverride = new DpiScale(1, 1),
            ShownLayers = CanvasLayer.Files | CanvasLayer.Details | CanvasLayer.FolderCounts };
        canvas.Measure(new Size(1200, 800));
        canvas.Arrange(new Rect(0, 0, 1200, 800));
        canvas.UpdateLayout();
        return canvas;
    }

    private static async Task<TimeSpan> ArrivalSettleAsync(NestedCanvas canvas, TimeSpan time)
    {
        canvas.RunFrameForTests(time += TimeSpan.FromMilliseconds(40));
        // Camera motion's existing settled-quality delay uses wall time; the
        // animation assertions below use only the supplied manual frame times.
        await Task.Delay(420);
        for (var frame = 0; frame < 16; frame++)
        {
            canvas.RunFrameForTests(time += TimeSpan.FromMilliseconds(40));
            if (canvas.IsIdle && !canvas.IsFrameHooked) break;
            await Task.Delay(10);
        }
        return time;
    }

    private static int ArrivalPulseBands(NestedCanvas canvas, string path, TimeSpan start)
    {
        var bands = 0;
        var previous = false;
        for (var milliseconds = 0; milliseconds <= NestedCanvas.ArrivalPulseMilliseconds + 80; milliseconds += 20)
        {
            canvas.RunFrameForTests(start + TimeSpan.FromMilliseconds(milliseconds));
            var visible = ArrivalPathIsLit(canvas, path, 0.001);
            if (visible && !previous) bands++;
            previous = visible;
        }
        return bands;
    }

    private static bool ArrivalPathIsLit(NestedCanvas canvas, string path, double opacity = 0.5)
        => canvas.LastArrivalHighlights.Any(highlight => ViewAllPath.Equals(highlight.Path, path) && highlight.Opacity > opacity);

    private static string ArrivalState(NestedCanvas canvas)
    {
        var active = (System.Collections.IEnumerable)typeof(NestedCanvas).GetField("_activeArrivals", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(canvas)!;
        var elapsed = active.Cast<object>().Select(pulse =>
        {
            var type = pulse.GetType();
            return $"{type.GetField("Name")!.GetValue(pulse)}:{(double)type.GetField("Elapsed")!.GetValue(pulse)!:0.0}ms"
                + $"/visible={type.GetField("Visible")!.GetValue(pulse)}";
        });
        return $"pending={canvas.PendingArrivalCount}, active={canvas.ActiveArrivalCount}, highlights={canvas.LastArrivalHighlights.Count}, "
            + $"elapsed=[{string.Join(",", elapsed)}], idle={canvas.IsIdle}, hooked={canvas.IsFrameHooked}, pendingWork={canvas.HasPendingWork}";
    }

    private static Rect ArrivalFileRect(NestedCanvas canvas, NestedFolder parent, string name)
    {
        var index = parent.Files.ToList().FindIndex(file => file.Name == name);
        return (Rect)typeof(NestedCanvas).GetMethod("TargetRect", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(canvas, [parent, index, true])!;
    }

    private static bool ArrivalRectsMatch(Rect highlight, Rect item)
        => item.Contains(new Point(highlight.Left + highlight.Width / 2, highlight.Top + highlight.Height / 2))
            && Math.Abs(highlight.Width - item.Width) < 12 && Math.Abs(highlight.Height - item.Height) < 12;

    private static bool ArrivalHasYellowPaint(NestedCanvas canvas)
    {
        var overlay = (DrawingVisual)typeof(NestedCanvas).GetField("_overlay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas)!;
        return Yellow(overlay.Drawing, 1);
        static bool Yellow(Drawing? drawing, double opacity)
        {
            if (drawing is DrawingGroup group)
                return group.Children.Any(child => Yellow(child, opacity * group.Opacity));
            if (drawing is not GeometryDrawing geometry) return false;
            return IsYellow(geometry.Brush, opacity) || IsYellow(geometry.Pen?.Brush, opacity);
        }
        static bool IsYellow(Brush? brush, double opacity) => brush is SolidColorBrush solid && solid.Color.R == 0xff
            && solid.Color.G == 0xd6 && solid.Color.B == 0x6b && opacity * solid.Opacity * solid.Color.A / 255 > 0.001;
    }

    private static void ArrivalSaveShot(NestedCanvas canvas, string name)
    {
        if (Environment.GetEnvironmentVariable("NEW_ITEM_PULSE_SHOTS") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap(1200, 800, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(stream);
    }

    private static string ArrivalDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerNewItemArrivals", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
