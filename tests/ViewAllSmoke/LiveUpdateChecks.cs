using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using UltraExplorer;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// Changes on disk reaching the canvas, the folder list and the tree canvas,
/// whatever is selected - the bug this is for: with an archive's tile
/// selected, extracting it next to itself showed nothing, because the only
/// watcher followed the selection and a file selected turned it off.  The
/// rows of the live-update table are replayed against the real view model,
/// the real nested tree and a window-less canvas drawing it, on real folders,
/// with the real change hub watching the disk; then what the table does not
/// cover: folders off screen, renames, deletes, a burst, missed changes,
/// polling, F5, and a drive asked whether it may go.
/// </summary>
internal static partial class Program
{
    private static Task LiveUpdateChecks()
    {
        Section("live updates");
        RunOnSta("live updates on a dispatcher", LiveScenarioChecks);
        RunOnSta("live updates in the canvas's frame", LiveFrameChecks);
        RunOnSta("live updates when polled", LivePollChecks);
        RunOnSta("live updates for a drive about to go", LiveDeviceChecks);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The user's scenario and the rest of the table, end to end: a view model
    /// and a nested tree wired to one hub the way the window wires them, a
    /// canvas with no window drawing the tree - its frames run by a timer at
    /// about 120 Hz - and changes made on disk as another program would make
    /// them.  Every time is from the change on disk to the moment the tree,
    /// or the list, has it.
    /// </summary>
    private static async Task LiveScenarioChecks()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "UltraExplorerLive", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(baseDirectory, "J");
        var granny = Path.Combine(root, "Granny2Work");
        var clamp = Path.Combine(granny, "clampchair");
        var other = Path.Combine(granny, "otherchair");
        var far = Path.Combine(root, "far");
        Directory.CreateDirectory(Path.Combine(clamp, "tex", "maps"));
        Directory.CreateDirectory(other);
        Directory.CreateDirectory(far);
        File.WriteAllBytes(Path.Combine(clamp, "model.7z"), new byte[1024]);
        File.WriteAllBytes(Path.Combine(clamp, "TEX.spp"), new byte[2048]);
        File.WriteAllText(Path.Combine(clamp, "notes.txt"), "notes");
        File.WriteAllText(Path.Combine(clamp, "tex", "albedo.png"), "png");
        File.WriteAllText(Path.Combine(far, "old.txt"), "old");

        var rig = new LiveRig();
        try
        {
            using var icons = new ShellIconService();
            var marks = new FolderMarkService(Path.Combine(baseDirectory, "marks.json"));
            using var hub = new ChangeHub(TimeProvider.System);
            using var tree = new ViewAllViewModel(marks, icons, Path.Combine(baseDirectory, "tree.json")) { PreferLightReveal = true };
            hub.Driver.Fallback = DispatcherFrameDriver.ForCurrentThread((ref FrameBudget budget) => hub.Drain(ref budget, tree), () => hub.HasWork);
            tree.Changes = hub;
            tree.IsCanvasShown = false;
            await tree.InitializeAsync(clamp);
            tree.FolderList.IsVisible = true;

            using var nested = new NestedTree();
            nested.Changes = hub;
            tree.NestedChanges = nested;
            nested.SetRoots([new NestedRoot(root, "J", NestedFolderKind.Drive)]);
            var reads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            nested.FolderLoaded += folder => reads[folder.FullPath] = reads.GetValueOrDefault(folder.FullPath) + 1;
            int ReadsOf(string path) => reads.GetValueOrDefault(path);

            var canvas = new NestedCanvas { FramesByHandForTests = true, DpiOverride = new DpiScale(1, 1), Tree = nested };
            canvas.AttachChanges(hub, tree);
            canvas.Measure(new Size(1200, 800));
            canvas.Arrange(new Rect(0, 0, 1200, 800));
            canvas.UpdateLayout();
            rig.Start(canvas);

            var clampFolder = await nested.RevealAsync(clamp);
            Check("the scenario's folder is in the nested tree", clampFolder is not null && clampFolder.FullPath.Equals(clamp, StringComparison.OrdinalIgnoreCase));
            if (clampFolder is null)
            {
                return;
            }

            canvas.FlyTo(clampFolder, 0.8, animated: false);
            var texPath = Path.Combine(clamp, "tex");
            await LiveWait(() => clampFolder.IsLoaded && nested.Find(texPath) is { IsLoaded: true } && nested.PendingCount == 0, 5_000);
            Check("drawn, it is read", clampFolder.IsLoaded);
            Check("drawn, the folder's sub-folder is read too", nested.Find(texPath) is { IsLoaded: true });
            Check("every folder read is registered with the hub", nested.LiveRegisteredCount >= 4);
            Check("the list shows the folder the tree selected", tree.FolderList.FolderPath.Equals(clamp, StringComparison.OrdinalIgnoreCase));

            bool NestedHas(string parent, string name) =>
                nested.Find(parent) is { } folder && folder.AllChildren.Any(child => child.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            bool NestedHasFile(string parent, string name) =>
                nested.Find(parent) is { } folder && folder.AllFiles.Any(file => file.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            bool ListHas(string name) => tree.FolderList.Items.Any(item => item.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));

            // ---- A: the folder selected, a sub-folder made -----------------------------
            await tree.SelectPathAsync(clamp);
            Directory.CreateDirectory(Path.Combine(clamp, "made-while-folder-selected"));
            var times = await LiveTimes(3_000, () => NestedHas(clamp, "made-while-folder-selected"), () => ListHas("made-while-folder-selected"));
            Check($"A: the folder selected, a new sub-folder is on the canvas in {times[0]} ms (1,000)", times[0] is >= 0 and <= 1_000);
            Check($"A: and in the list in {times[1]} ms (1,000)", times[1] is >= 0 and <= 1_000);

            // ---- B: the archive's tile selected, 'model' and four files made ------------
            await LiveWait(() => nested.PendingCount == 0 && !tree.FolderList.IsLoading, 2_000);
            var registeredBefore = nested.LiveRegisteredCount;
            var graphBefore = tree.GraphInterestCount;
            await tree.SelectPathAsync(Path.Combine(clamp, "model.7z"));
            await LiveWait(() => nested.PendingCount == 0 && !tree.FolderList.IsLoading, 2_000);
            Check("B: a file selected, the list shows the file's folder", tree.FolderList.FolderPath.Equals(clamp, StringComparison.OrdinalIgnoreCase));
            Check($"B: selecting a file changes nothing that is watched ({registeredBefore} canvas folders, {graphBefore} tree folders)",
                nested.LiveRegisteredCount == registeredBefore && tree.GraphInterestCount >= graphBefore);
            var model = Path.Combine(clamp, "model");
            var extracted = Stopwatch.StartNew();
            Directory.CreateDirectory(model);
            for (var index = 0; index < 4; index++)
            {
                File.WriteAllBytes(Path.Combine(model, $"part{index}.fbx"), new byte[4096]);
            }

            times = await LiveTimes(3_000,
                () => NestedHas(clamp, "model"),
                () => nested.Find(model) is { IsLoaded: true } folder && folder.AllFiles.Length == 4,
                () => ListHas("model"));
            Check($"B: the file selected, 'model' is on the canvas in {times[0]} ms (1,000)", times[0] is >= 0 and <= 1_000);
            Check($"B: with its four files in {times[1]} ms (1,000)", times[1] is >= 0 and <= 1_000);
            Check($"B: and 'model' is in the list in {times[2]} ms (1,000)", times[2] is >= 0 and <= 1_000);
            Check($"B: the selection is still the file ({extracted.ElapsedMilliseconds} ms after the extraction began)", tree.Selection.Count == 1 && tree.Selection.Contains(Path.Combine(clamp, "model.7z")));

            // ---- C: another folder, and back: nothing to catch up on ---------------------
            var clampReads = ReadsOf(clamp);
            await tree.SelectPathAsync(other);
            await LiveWait(() => tree.FolderList.FolderPath.Equals(other, StringComparison.OrdinalIgnoreCase) && !tree.FolderList.IsLoading, 2_000);
            await tree.SelectPathAsync(clamp);
            await LiveWait(() => tree.FolderList.FolderPath.Equals(clamp, StringComparison.OrdinalIgnoreCase) && !tree.FolderList.IsLoading && ListHas("model"), 2_000);
            Check("C: back on the folder, the canvas already has 'model'", NestedHas(clamp, "model"));
            Check("C: and the list has it", ListHas("model"));
            Check("C: and the folder was not read again for it", ReadsOf(clamp) == clampReads);

            // ---- D: a later change in the folder ----------------------------------------------
            Directory.CreateDirectory(Path.Combine(clamp, "later"));
            times = await LiveTimes(3_000, () => NestedHas(clamp, "later"), () => ListHas("later"));
            Check($"D: a later change is on the canvas in {times[0]} ms and in the list in {times[1]} ms", times[0] is >= 0 and <= 1_000 && times[1] is >= 0 and <= 1_000);

            // ---- E: six files in a sub-folder that is read and drawn ------------------------
            var modelReads = ReadsOf(model);
            for (var index = 4; index < 10; index++)
            {
                File.WriteAllBytes(Path.Combine(model, $"part{index}.fbx"), new byte[4096]);
            }

            times = await LiveTimes(3_000, () => nested.Find(model) is { } folder && folder.AllFiles.Length == 10);
            Check($"E: files added in a drawn sub-folder are on the canvas in {times[0]} ms (1,000)", times[0] is >= 0 and <= 1_000);
            Check("E: by reading that sub-folder again", ReadsOf(model) > modelReads);

            // ---- F: a file every 200 ms for 3 s -------------------------------------------------
            clampReads = ReadsOf(clamp);
            var stream = Stopwatch.StartNew();
            var writer = Task.Run(() =>
            {
                for (var index = 0; index < 15; index++)
                {
                    File.WriteAllBytes(Path.Combine(clamp, $"stream{index:D2}.bin"), new byte[64]);
                    Thread.Sleep(200);
                }
            });
            times = await LiveTimes(3_000, () => NestedHasFile(clamp, "stream00.bin"));
            await writer;
            await LiveWait(() => NestedHasFile(clamp, "stream14.bin"), 2_000);
            var streamReads = ReadsOf(clamp) - clampReads;
            Check($"F: under a steady stream the first change is on the canvas in {times[0]} ms (600)", times[0] is >= 0 and <= 600);
            Check($"F: and the folder keeps up: {streamReads} reads over {stream.ElapsedMilliseconds} ms, the last change shown", NestedHasFile(clamp, "stream14.bin") && streamReads is >= 3 and <= 16);

            // ---- off screen: marked, not read, until drawn --------------------------------------
            var farFolder = nested.Find(far)!;
            await nested.LoadAsync(farFolder);
            var farReads = ReadsOf(far);
            var marked = nested.LiveMarkedOnly;
            File.WriteAllText(Path.Combine(far, "new.txt"), "new");
            await LiveWait(() => nested.LiveMarkedOnly > marked, 2_000);
            await Task.Delay(300);
            Check("a folder off screen that changed is only marked", nested.LiveMarkedOnly > marked && farFolder.NeedsRefresh);
            Check("and not read", ReadsOf(far) == farReads && farFolder.AllFiles.Length == 1);
            canvas.FlyTo(farFolder, 0.8, animated: false);
            times = await LiveTimes(3_000, () => farFolder.AllFiles.Length == 2);
            Check($"drawn, it is read in {times[0]} ms (1,000)", times[0] is >= 0 and <= 1_000);
            canvas.FlyTo(clampFolder, 0.8, animated: false);
            await LiveWait(() => nested.PendingCount == 0, 2_000);

            // ---- a sub-folder renamed takes what was read in it along ------------------------
            var renamedPath = Path.Combine(clamp, "tex2");
            var carried = nested.LiveCarriedOver;
            Directory.Move(texPath, renamedPath);
            await LiveWait(() => nested.Find(renamedPath) is { IsLoaded: true }, 3_000);
            var renamed = nested.Find(renamedPath);
            Check("a renamed sub-folder is on the canvas under its new name", renamed is not null && !NestedHas(clamp, "tex"));
            Check("it kept its listing: its files and its sub-folders", nested.LiveCarriedOver > carried
                && renamed is { IsLoaded: true } && renamed.AllChildren.Any(child => child.Name == "maps") && renamed.AllFiles.Any(file => file.Name == "albedo.png"));

            // ---- a selected file renamed stays selected, and its mark goes with it ------------
            var notes = Path.Combine(clamp, "notes.txt");
            var notesRenamed = Path.Combine(clamp, "notes-renamed.txt");
            marks.SetAccent(notes, "#EF5A68");
            await tree.SelectPathAsync(notes);
            File.Move(notes, notesRenamed);
            times = await LiveTimes(3_000, () => tree.Selection.Contains(notesRenamed));
            Check($"a selected file renamed elsewhere is selected under its new name in {times[0]} ms", times[0] is >= 0 and <= 1_000 && !tree.Selection.Contains(notes));
            Check("and its mark moved with it", marks.Get(notesRenamed).AccentHex == "#EF5A68" && marks.Get(notes).IsEmpty);

            // ---- a selected file deleted elsewhere is let go ------------------------------------
            await tree.SelectPathAsync(notesRenamed);
            File.Delete(notesRenamed);
            times = await LiveTimes(3_000, () => tree.Selection.Count == 0 || !tree.Selection.Contains(notesRenamed));
            Check($"a selected file deleted elsewhere is let go in {times[0]} ms", times[0] is >= 0 and <= 1_000);

            // ---- a subtree deleted is forgotten, and taken off the hub ---------------------------
            await LiveWait(() => nested.PendingCount == 0, 2_000);
            var registered = nested.LiveRegisteredCount;
            var maps = renamed?.AllChildren.FirstOrDefault(child => child.Name == "maps");
            if (maps is not null)
            {
                await nested.LoadAsync(maps);
            }

            registered = nested.LiveRegisteredCount;
            Directory.Delete(renamedPath, recursive: true);
            await LiveWait(() => !NestedHas(clamp, "tex2") && nested.LiveRegisteredCount <= registered - 2, 3_000);
            Check("a deleted sub-folder leaves the canvas", !NestedHas(clamp, "tex2") && renamed is not null && NestedTree.IsDetached(renamed));
            Check($"and it and what was read below it leave the hub ({registered} registered before, {nested.LiveRegisteredCount} after)", nested.LiveRegisteredCount <= registered - 2);

            // ---- the view inside a folder that is deleted --------------------------------------
            var doomed = Path.Combine(clamp, "doomed");
            Directory.CreateDirectory(Path.Combine(doomed, "inner"));
            File.WriteAllText(Path.Combine(doomed, "inner", "a.txt"), "a");
            await LiveWait(() => nested.Find(doomed) is { IsLoaded: true }, 3_000);
            var doomedFolder = nested.Find(doomed);
            if (doomedFolder is not null)
            {
                canvas.FlyTo(doomedFolder, 0.8, animated: false);
                await LiveWait(() => nested.PendingCount == 0, 2_000);
                await Task.Delay(100);
                var parentBefore = canvas.ScreenRectOf(clampFolder);
                Directory.Delete(doomed, recursive: true);
                await LiveWait(() => canvas.CaptureCamera() is { } camera && !camera.AnchorPath.StartsWith(doomed, StringComparison.OrdinalIgnoreCase), 3_000);
                var parentAfter = canvas.ScreenRectOf(clampFolder);
                var anchorAfter = canvas.CaptureCamera()?.AnchorPath ?? string.Empty;
                Check($"the view's folder deleted, the view is fixed to its parent or a folder still in it ({Path.GetFileName(anchorAfter)})",
                    (anchorAfter.Equals(clamp, StringComparison.OrdinalIgnoreCase) || anchorAfter.StartsWith(clamp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    && !anchorAfter.StartsWith(doomed, StringComparison.OrdinalIgnoreCase));
                Check("and the parent has not moved on screen", parentBefore is { } before && parentAfter is { } after
                    && Math.Abs(before.X - after.X) < 0.5 && Math.Abs(before.Y - after.Y) < 0.5 && Math.Abs(before.Width - after.Width) < 0.5);
                canvas.FlyTo(clampFolder, 0.8, animated: false);
            }

            // ---- changes missed: the epoch moves on; what is drawn is read, the rest waits -----
            await LiveWait(() => nested.PendingCount == 0, 2_000);
            if (clampFolder.Watch is { } watch)
            {
                // Three pictures on, far drawn earlier is well off the screen.
                nested.BeginFrame();
                nested.BeginFrame();
                nested.BeginFrame();
                clampReads = ReadsOf(clamp);
                farReads = ReadsOf(far);
                Interlocked.Increment(ref watch.Epoch);
                ((IChangeSink)tree).EpochBumped(watch);
                times = await LiveTimes(3_000, () => ReadsOf(clamp) > clampReads);
                await Task.Delay(300);
                Check($"after changes were missed, the folder on screen is read again in {times[0]} ms", times[0] is >= 0 and <= 1_000);
                Check("and the one off screen is not, until it is drawn", ReadsOf(far) == farReads && farFolder.NeedsRefresh);
            }
            else
            {
                Check("the tree's drives have a watch", false);
            }

            // ---- F5: the folder, and what was read below it as it is drawn --------------------
            await LiveWait(() => nested.PendingCount == 0, 2_000);
            clampReads = ReadsOf(clamp);
            modelReads = ReadsOf(model);
            nested.RefreshDeep(clampFolder);
            times = await LiveTimes(3_000, () => ReadsOf(clamp) > clampReads, () => ReadsOf(model) > modelReads);
            Check($"F5 reads the folder again ({times[0]} ms) and the sub-folders drawn in it ({times[1]} ms)", times[0] >= 0 && times[1] >= 0);

            // F5 on the tree view model asks the nested tree for the same.
            string? deepAsked = null;
            tree.DeepRefreshRequested += path => deepAsked = path;
            if (tree.TryGetNode(clamp, out var clampNode))
            {
                await tree.RefreshAsync(clampNode);
            }

            Check("F5 on a folder asks the nested canvas to read it and what is below it again", string.Equals(deepAsked, clamp, StringComparison.OrdinalIgnoreCase));

            // ---- sizes and dates only: put in with no read -------------------------------------
            await LiveWait(() => nested.PendingCount == 0, 2_000);
            clampReads = ReadsOf(clamp);
            var patches = nested.LivePatches;
            var written = DateTime.UtcNow.Ticks;
            nested.OnFolderChanged(clampFolder, new FolderChange(clampFolder.FullPath, ChangeKinds.Content, Stopwatch.GetTimestamp(), default, new[] { new FileDelta("TEX.spp", 8192, written) }));
            var patched = clampFolder.AllFiles.FirstOrDefault(file => file.Name == "TEX.spp");
            Check("a file's new size from the watch is put in without reading the folder",
                nested.LivePatches == patches + 1 && patched.Length == 8192 && patched.ModifiedTicks == written && ReadsOf(clamp) == clampReads);
            var rereads = nested.LiveRereadsAsked;
            nested.OnFolderChanged(clampFolder, new FolderChange(clampFolder.FullPath, ChangeKinds.Content, Stopwatch.GetTimestamp(), default, new[] { new FileDelta("not-there.bin", 1, written) }));
            Check("a file the listing does not have is a change to read instead", nested.LivePatches == patches + 1 && nested.LiveRereadsAsked == rereads + 1);
            await LiveWait(() => nested.PendingCount == 0 && !clampFolder.NeedsRefresh, 2_000);

            // ---- a burst of two thousand files -------------------------------------------------
            clampReads = ReadsOf(clamp);
            var merges = tree.FolderList.LiveMerges;
            var burst = Stopwatch.StartNew();
            var burstWriter = Task.Run(() =>
            {
                for (var index = 0; index < 2_000; index++)
                {
                    File.WriteAllBytes(Path.Combine(clamp, $"burst{index:D4}.dat"), []);
                }
            });
            await burstWriter;
            var written2k = burst.ElapsedMilliseconds;
            int BurstFiles() => nested.Find(clamp)?.AllFiles.Count(file => file.Name.StartsWith("burst", StringComparison.Ordinal)) ?? 0;
            int BurstRows() => tree.FolderList.Items.Count(item => item.DisplayName.StartsWith("burst", StringComparison.Ordinal));
            times = await LiveTimes(5_000, () => BurstFiles() == 2_000, () => BurstRows() == 2_000);
            var burstReads = ReadsOf(clamp) - clampReads;
            Check($"a burst of 2,000 files (written in {written2k} ms) is all on the canvas {times[0]} ms after the last was written", times[0] is >= 0 and <= 3_000);
            Check($"and all in the list {times[1]} ms after", times[1] is >= 0 and <= 3_000);
            Check($"with the folder read {burstReads} times and the list merged {tree.FolderList.LiveMerges - merges} times", burstReads is >= 1 and <= 12);

            // ---- the list's own folder deleted: it goes up ---------------------------------------
            var goneList = Path.Combine(other, "list-here");
            Directory.CreateDirectory(goneList);
            File.WriteAllText(Path.Combine(goneList, "x.txt"), "x");
            await tree.FolderList.NavigateAsync(goneList);
            await LiveWait(() => !tree.FolderList.IsLoading && tree.FolderList.Items.Count == 1, 2_000);
            Directory.Delete(goneList, recursive: true);
            times = await LiveTimes(3_000, () => tree.FolderList.FolderPath.Equals(other, StringComparison.OrdinalIgnoreCase));
            Check($"the list's folder deleted, the list goes to the folder above in {times[0]} ms", times[0] is >= 0 and <= 1_500 && tree.FolderList.LeftGoneFolders >= 1);

            // ---- the list merges: rows kept are the same rows, new ones fade in ----------------
            await tree.FolderList.NavigateAsync(clamp);
            await LiveWait(() => !tree.FolderList.IsLoading && ListHas("model"), 2_000);
            var kept = tree.FolderList.Items.First(item => item.DisplayName == "model");
            File.WriteAllText(Path.Combine(clamp, "fresh.txt"), "fresh");
            await LiveWait(() => ListHas("fresh.txt"), 2_000);
            var fresh = tree.FolderList.Items.FirstOrDefault(item => item.DisplayName == "fresh.txt");
            Check("a change merged into the list keeps the rows it had", ReferenceEquals(tree.FolderList.Items.First(item => item.DisplayName == "model"), kept) && !kept.IsNew);
            Check("and the new row fades in", fresh is { IsNew: true });
            await LiveWait(() => fresh is { IsNew: false }, 2_000);
            Check("for a moment only", fresh is { IsNew: false });

            // ---- the tree canvas: what it shows is registered, and follows ---------------------
            Check("the tree canvas's folders with children are registered", tree.GraphInterestCount > 0);

            rig.Stop();
            Check("the canvas's frames ran without an error", rig.Failure is null);
            if (rig.Failure is { } failure)
            {
                Console.WriteLine($"  {failure}");
            }
        }
        finally
        {
            rig.Stop();
            TryDelete(baseDirectory);
        }
    }

    /// <summary>
    /// The hub's changes taken in by the canvas's own frame, in phase 3: with
    /// nothing else to drain the hub, a change waits for the frame, keeps the
    /// canvas from resting while it waits, and one frame takes it in.
    /// </summary>
    private static async Task LiveFrameChecks()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "UltraExplorerLiveFrame", Guid.NewGuid().ToString("N"));
        var inner = Path.Combine(baseDirectory, "inner");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "a.txt"), "a");
        try
        {
            using var hub = new ChangeHub(TimeProvider.System);
            using var tree = new NestedTree();
            tree.Changes = hub;
            tree.SetRoots([new NestedRoot(baseDirectory, "L", NestedFolderKind.Drive)]);
            var folder = await tree.RevealAsync(inner);
            if (folder is not null)
            {
                await tree.LoadAsync(folder);
            }

            Check("a folder read is registered, with its drive", folder is { IsLoaded: true } && tree.LiveRegisteredCount == 2);

            var canvas = new NestedCanvas { FramesByHandForTests = true, DpiOverride = new DpiScale(1, 1), Tree = tree };
            canvas.AttachChanges(hub, tree);
            canvas.Measure(new Size(800, 500));
            canvas.Arrange(new Rect(0, 0, 800, 500));
            canvas.UpdateLayout();
            var time = TimeSpan.FromSeconds(10);
            canvas.RunFrameForTests(time);

            var taken = tree.LiveChangesTaken;
            hub.Touch(inner, immediate: true);
            await LiveWait(() => hub.HasWork, 1_000);
            Check("a change due waits for the canvas's frame", hub.HasWork && canvas.HasPendingWork && !canvas.IsIdle);
            canvas.RunFrameForTests(time += TimeSpan.FromMilliseconds(8.33));
            Check($"one frame takes it in ({canvas.LastFrameStats.HubItems} taken, {canvas.LastFrameStats.HubMs:F3} ms)",
                !hub.HasWork && canvas.LastFrameStats.HubItems >= 1 && tree.LiveChangesTaken == taken + 1);
            Check("within the frame's allowance for changes", canvas.LastFrameStats.HubMs <= FrameBudgets.HubMs + 0.5);

            canvas.AttachChanges(null, null);
            Check("let go, the canvas takes no changes in", canvas.Changes is null);
            tree.Changes = null;
            Check("a tree let go of the hub takes its folders off it", tree.LiveRegisteredCount == 0);
        }
        finally
        {
            TryDelete(baseDirectory);
        }
    }

    /// <summary>
    /// A volume that refuses a watch is polled: the widest drawn folders have
    /// their directory's time compared with the one taken when they were read,
    /// and a folder that moved on is read again.
    /// </summary>
    private static async Task LivePollChecks()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "UltraExplorerLivePoll", Guid.NewGuid().ToString("N"));
        var inner = Path.Combine(baseDirectory, "inner");
        Directory.CreateDirectory(inner);
        try
        {
            using var hub = new ChangeHub(TimeProvider.System);
            using var tree = new NestedTree();
            hub.Driver.Fallback = DispatcherFrameDriver.ForCurrentThread((ref FrameBudget budget) => hub.Drain(ref budget, tree), () => hub.HasWork);
            tree.Changes = hub;
            var polled = new WatchRoot(baseDirectory, WatchKind.Polling, isNetwork: false);
            tree.WatchRootFor = _ => polled;
            tree.SetRoots([new NestedRoot(baseDirectory, "P", NestedFolderKind.Drive)]);
            var folder = await tree.RevealAsync(inner);
            if (folder is not null)
            {
                await tree.LoadAsync(folder);
            }

            Check("a folder on a polled volume keeps its directory's time", folder is { IsLoaded: true, DirWriteTicks: not 0 });
            if (folder is null)
            {
                return;
            }

            tree.Request(folder, 600);
            var reads = 0;
            tree.FolderLoaded += loaded => reads += ReferenceEquals(loaded, folder) ? 1 : 0;
            ((IChangeSink)tree).PollDue(polled);
            await LiveWait(() => tree.LivePolls.Looked >= 1, 2_000);
            Check("a poll with nothing changed looks, and reads nothing", tree.LivePolls is { Looked: >= 1, Changed: 0 } && reads == 0);

            await Task.Delay(30);
            File.WriteAllText(Path.Combine(inner, "new.txt"), "new");
            ((IChangeSink)tree).PollDue(polled);
            var times = await LiveTimes(3_000, () => folder.AllFiles.Length == 1);
            Check($"a poll that finds the directory moved on has it read again in {times[0]} ms", tree.LivePolls.Changed >= 1 && times[0] >= 0);
        }
        finally
        {
            TryDelete(baseDirectory);
        }
    }

    /// <summary>
    /// "Safely remove" with a folder of the drive on screen: the window hears
    /// the drive is about to go on the watch's own handle and closes it, and
    /// opens it again when the removal is called off.  The messages are the
    /// ones Windows sends, handed to the window's handler directly - there is
    /// no drive to pull out in a test - through a message-only window that is
    /// never shown.
    /// </summary>
    private static async Task LiveDeviceChecks()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "UltraExplorerLiveDevice", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDirectory);
        HwndSource? window = null;
        try
        {
            using var hub = new ChangeHub(TimeProvider.System);
            using var notifications = new VolumeNotifications(hub, System.Windows.Threading.Dispatcher.CurrentDispatcher);
            window = new HwndSource(new HwndSourceParameters("UltraExplorer live device check") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
            notifications.SetWindow(window.Handle);

            var root = hub.RootFor(baseDirectory);
            Check("a local volume gets a watch", root is { Kind: WatchKind.Local });
            if (root is null)
            {
                return;
            }

            await LiveWait(() => root.Handle is { IsInvalid: false } && notifications.RegisteredCount > 0, 3_000);
            Check("armed, its handle is registered for the device's messages", root.Handle is { IsInvalid: false } && notifications.RegisteredCount == 1);
            var notification = notifications.NotificationFor(root);
            using (var message = new HandleMessage(notification))
            {
                var outcome = notifications.OnDeviceChange(0x8001, message.Pointer);
                Check("asked whether the drive may go, the watch is suspended", outcome == DeviceChange.WatchSuspended);
                Check("and its handle is closed before the answer", root.Handle is null && root.State == WatchState.Suspended);

                outcome = notifications.OnDeviceChange(0x8002, message.Pointer);
                Check("the removal called off, the watch is armed again", outcome == DeviceChange.WatchRearmed);
            }

            await LiveWait(() => root.Handle is { IsInvalid: false } && notifications.RegisteredCount == 1, 3_000);
            Check("on a new handle, registered afresh", root.State == WatchState.Armed && notifications.RegisteredCount == 1 && notifications.NotificationFor(root) != notification);

            using (var volume = new VolumeMessage())
            {
                Check("a volume arriving has the drives listed again", notifications.OnDeviceChange(0x8000, volume.Pointer) == DeviceChange.VolumesChanged);
            }
        }
        finally
        {
            window?.Dispose();
            TryDelete(baseDirectory);
        }
    }

    // ---- helpers -------------------------------------------------------------------------

    /// <summary>Waits for <paramref name="condition"/>, polling every few milliseconds; the time it took, or -1.</summary>
    private static async Task<long> LiveWait(Func<bool> condition, int timeoutMilliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMilliseconds)
            {
                return -1;
            }

            await Task.Delay(4);
        }

        return watch.ElapsedMilliseconds;
    }

    /// <summary>When each of <paramref name="conditions"/> first held, in milliseconds from now; -1 for one that never did.</summary>
    private static async Task<long[]> LiveTimes(int timeoutMilliseconds, params Func<bool>[] conditions)
    {
        var times = new long[conditions.Length];
        Array.Fill(times, -1);
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds <= timeoutMilliseconds)
        {
            var waiting = false;
            for (var index = 0; index < conditions.Length; index++)
            {
                if (times[index] < 0)
                {
                    if (conditions[index]())
                    {
                        times[index] = watch.ElapsedMilliseconds;
                    }
                    else
                    {
                        waiting = true;
                    }
                }
            }

            if (!waiting)
            {
                break;
            }

            await Task.Delay(4);
        }

        return times;
    }

    /// <summary>A window-less canvas's frames, run about every 8 ms on the test's dispatcher as WPF's Rendering would.</summary>
    private sealed class LiveRig
    {
        private CancellationTokenSource? _stop;

        public Exception? Failure { get; private set; }

        public void Start(NestedCanvas canvas)
        {
            _stop = new CancellationTokenSource();
            _ = RunAsync(canvas, _stop.Token);
        }

        public void Stop() => _stop?.Cancel();

        private async Task RunAsync(NestedCanvas canvas, CancellationToken token)
        {
            var time = TimeSpan.FromSeconds(100);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(8, token);
                    time += TimeSpan.FromMilliseconds(8.33);
                    canvas.RunFrameForTests(time);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Failure ??= ex;
                }
            }
        }
    }

    /// <summary>A DEV_BROADCAST_HANDLE naming a notification, as Windows sends with a device's messages.</summary>
    private sealed class HandleMessage : IDisposable
    {
        public HandleMessage(IntPtr notification)
        {
            var message = new VolumeNotifications.BroadcastHandle
            {
                Size = Marshal.SizeOf<VolumeNotifications.BroadcastHandle>(),
                DeviceType = 6,
                Notification = notification
            };
            Pointer = Marshal.AllocHGlobal(message.Size);
            Marshal.StructureToPtr(message, Pointer, fDeleteOld: false);
        }

        public IntPtr Pointer { get; }

        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }

    /// <summary>A DEV_BROADCAST_VOLUME's header, as Windows sends when a volume arrives or leaves.</summary>
    private sealed class VolumeMessage : IDisposable
    {
        public VolumeMessage()
        {
            Pointer = Marshal.AllocHGlobal(20);
            Marshal.WriteInt32(Pointer, 0, 20);
            Marshal.WriteInt32(Pointer, 4, 2);
            Marshal.WriteInt32(Pointer, 8, 0);
            Marshal.WriteInt32(Pointer, 12, 1 << 4);
            Marshal.WriteInt32(Pointer, 16, 0);
        }

        public IntPtr Pointer { get; }

        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }
}
