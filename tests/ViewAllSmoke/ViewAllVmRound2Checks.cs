using System.Diagnostics;
using System.IO;
using System.Reflection;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// What the second review of the canvas's view model found, and what was done
/// about it: opening a file asks the disk and the Shell off the interface
/// thread; colouring or clearing a large selection on the tree canvas rebuilds
/// its batched layers once, not once an item; a size changing in a folder the
/// graph has never read does not have what was brought into it by name checked
/// against the disk; a slow listing lets go only of what was selected when it
/// began; and an item found gone is said to be gone once, and only once the
/// disk says so, and the focus goes to its folder.
/// </summary>
internal static partial class Program
{
    private static async Task ViewAllVmRound2Checks()
    {
        var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "UltraExplorerVmRound2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await OnDispatcher(() => VmRound2OpenFileAsync(root));
            await OnDispatcher(() => VmRound2ColourBatchAsync(root));
            await OnDispatcher(() => VmRound2HiddenGraphChangeAsync(root));
            await OnDispatcher(() => VmRound2SlowPruneAsync(root));
            await OnDispatcher(() => VmRound2GoneFocusAsync(root));
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- J035: opening a file -----------------------------------------------------------

    private static async Task VmRound2OpenFileAsync(string root)
    {
        Section("view model round 2: opening a file on a share that does not answer");
        var folder = Path.Combine(root, "open");
        var scratch = Path.Combine(root, "open-state");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(scratch);

        using var icons = new ShellIconService();
        using var tree = NewTree(scratch, icons);
        tree.PreferLightReveal = true;
        tree.IsCanvasShown = false;
        await tree.InitializeAsync(folder);
        var messages = new List<string>();
        tree.MessageRequested += (message, _) => messages.Add(message);

        static ViewAllNodeViewModel FileNode(string path) =>
            new(new ViewAllEntryDescriptor(path, Path.GetFileName(path), ViewAllEntryKind.File, false, false, null, DateTime.UtcNow), 0);

        // An address nothing answers at, from the range kept for examples: the
        // first question about it waits out the network's whole timeout, about
        // twenty seconds.  A fresh one each run, as an answer is remembered.
        var host = $"203.0.113.{Random.Shared.Next(1, 255)}";
        var asleep = FileNode($@"\\{host}\ultraexplorer-unreachable\report.docx");
        var watch = Stopwatch.StartNew();
        tree.OpenInDefaultApplication(asleep);
        watch.Stop();
        Report($"opening a file on a share that does not answer ({host}) holds the interface thread", watch.ElapsedMilliseconds, 250);

        // A file that is not there - nothing to start, and nothing shown but
        // the message - still says it could not be opened.
        var missing = FileNode(Path.Combine(folder, "missing.ultraexplorer-round2"));
        tree.OpenInDefaultApplication(missing);
        await WaitUntil(() => messages.Any(message => message.Contains("missing.ultraexplorer-round2", StringComparison.Ordinal)), 10_000);
        Check("a file that cannot be opened still says so",
            messages.Any(message => message.StartsWith("Could not open missing.ultraexplorer-round2", StringComparison.Ordinal)));
    }

    // ---- J064: colouring a large selection on the tree canvas ----------------------------

    private static async Task VmRound2ColourBatchAsync(string root)
    {
        Section("view model round 2: colouring a large selection on the tree canvas");
        const int count = 2_000;
        var folder = Path.Combine(root, "colours");
        var scratch = Path.Combine(root, "colours-state");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(scratch);
        for (var index = 0; index < count; index++)
        {
            File.WriteAllText(Path.Combine(folder, $"item-{index:D4}.txt"), "x");
        }

        using var icons = new ShellIconService();
        using var tree = NewTree(scratch, icons);
        await tree.InitializeAsync(folder);
        if (!tree.TryGetNode(folder, out var node))
        {
            Check("the folder is on the tree", false);
            return;
        }

        await tree.ExpandAsync(node);
        var extent = tree.GetContentExtent();
        const double zoom = 0.05;
        tree.UpdateViewport(extent.Location, extent.Size, zoom);
        await Settle(tree);

        // The window's two batched layers, rebuilt on every invalidation the
        // way the window rebuilds them.
        var overview = new ViewAllOverviewLayer { Index = tree.SpatialIndex };
        var harness = new ViewAllHarnessLayer { Index = tree.SpatialIndex };
        var invalidations = 0;
        tree.GraphInvalidated += () =>
        {
            invalidations++;
            harness.InvalidateGeometry();
            harness.Update(extent.Location, extent.Size, zoom);
            overview.InvalidateGeometry();
            overview.Update(extent.Location, extent.Size, zoom, tree.DetailLevel);
        };

        var paths = node.Children.Select(child => child.FullPath).ToArray();
        var watch = Stopwatch.StartNew();
        tree.ApplyAccent(paths, "#EF5A68");
        watch.Stop();
        Check($"colouring {paths.Length:N0} items rebuilds the batched layers once ({invalidations:N0} times)", invalidations == 1);
        Check("and every one of them wears the colour", paths.Length == count && node.Children.All(child => child.AccentHex == "#EF5A68"));
        Check("and the layers drew them", overview.DrawnNodeCount > 0);
        Report($"colouring {paths.Length:N0} items on the tree canvas", watch.ElapsedMilliseconds, 1_000);

        invalidations = 0;
        watch.Restart();
        tree.ApplyAccent(paths, null);
        watch.Stop();
        Check($"clearing them rebuilds the layers once too ({invalidations:N0} times)", invalidations == 1);
        Check("and every one of them loses it", node.Children.All(child => string.IsNullOrEmpty(child.AccentHex)));
        Report($"clearing the colour of {paths.Length:N0} items on the tree canvas", watch.ElapsedMilliseconds, 1_000);

        invalidations = 0;
        tree.ApplyAccent([paths[0]], "#3FA34D");
        Check("one item coloured rebuilds the layers at once, as ever",
            invalidations == 1 && node.Children.First(child => child.FullPath == paths[0]).AccentHex == "#3FA34D");
    }

    // ---- J095: a file changing in a folder of the graph, with the tree canvas away --------

    private static async Task VmRound2HiddenGraphChangeAsync(string root)
    {
        Section("view model round 2: a file changing in a folder of the graph, with the tree canvas away");
        const int count = 2_000;
        var folder = Path.Combine(root, "growing");
        var named = Path.Combine(root, "named");
        var scratch = Path.Combine(root, "growing-state");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(named);
        Directory.CreateDirectory(scratch);
        for (var index = 0; index < count; index++)
        {
            File.WriteAllText(Path.Combine(folder, $"item-{index:D4}.bin"), "x");
        }

        var log = Path.Combine(folder, "log.txt");
        File.WriteAllText(log, "start");
        var namedLog = Path.Combine(named, "log.txt");
        var namedPicks = Enumerable.Range(0, 40).Select(index => Path.Combine(named, $"picked-{index:D2}.txt")).ToArray();
        File.WriteAllText(namedLog, "start");
        foreach (var pick in namedPicks)
        {
            File.WriteAllText(pick, "picked");
        }

        using var icons = new ShellIconService();
        using var tree = NewTree(scratch, icons);
        tree.PreferLightReveal = true;
        tree.IsCanvasShown = false;
        await tree.InitializeAsync(folder);
        var sink = (IChangeSink)tree;
        FolderChange Grown(string path)
        {
            File.AppendAllText(path, new string('x', 2_000));
            var info = new FileInfo(path);
            return new FolderChange(Path.GetDirectoryName(path)!, ChangeKinds.Content, Stopwatch.GetTimestamp(), default,
                new[] { new FileDelta(info.Name, info.Length, info.LastWriteTimeUtc.Ticks) });
        }

        // A folder holding only what was brought in by name - items picked
        // on the nested canvas one after another - and one of them selected.
        // A size changing in it cannot have taken any of them away.
        foreach (var pick in namedPicks)
        {
            await tree.MaterializeAsync(pick);
        }

        tree.Selection.ReplaceSingle(namedPicks[0], false, 6, SelectionSource.Canvas);
        await Task.Delay(150);
        if (!tree.TryGetNode(named, out var namedNode) || namedNode.AreChildrenLoaded)
        {
            Check("the named folder is on the graph, not read", false);
            return;
        }

        var before = tree.GraphRefreshesForChanges;
        for (var index = 0; index < 5; index++)
        {
            sink.FolderChanged(ChangeConsumer.Graph, namedNode, Grown(namedLog));
            await Task.Delay(60);
        }

        await Task.Delay(200);
        var looks = tree.GraphRefreshesForChanges - before;
        Check($"a size changing in a folder never read does not have its {namedNode.Children.Count} named items checked against the disk ({looks} looks for 5 changes)",
            looks == 0);

        // Something going from it still is, as ever.
        File.Delete(namedPicks[0]);
        before = tree.GraphRefreshesForChanges;
        sink.FolderChanged(ChangeConsumer.Graph, namedNode,
            new FolderChange(named, ChangeKinds.Structural, Stopwatch.GetTimestamp(), default, default));
        await WaitUntil(() => !tree.TryGetNode(namedPicks[0], out _) && !tree.Selection.Contains(namedPicks[0]), 3_000);
        Check("an item going from such a folder still takes its node and its selection with it",
            tree.GraphRefreshesForChanges - before == 1 && !tree.TryGetNode(namedPicks[0], out _) && !tree.Selection.Contains(namedPicks[0]));

        // A folder read on the tree - opened from the list, say - whose log
        // the status bar names: read again as it grows, as ever, so the bar
        // shows its size now.
        var node = await tree.MaterializeAsync(folder);
        if (node is null)
        {
            Check("the folder is on the graph", false);
            return;
        }

        await tree.ExpandAsync(node);
        tree.Selection.ReplaceSingle(log, false, new FileInfo(log).Length, SelectionSource.Canvas);
        await Task.Delay(150);
        var shown = tree.StatusCountText;
        before = tree.GraphRefreshesForChanges;
        sink.FolderChanged(ChangeConsumer.Graph, node, Grown(log));
        await WaitUntil(() => tree.StatusCountText != shown, 3_000);
        Check($"the item the status bar names, growing in a folder read, is read again and the bar follows it ({shown} to {tree.StatusCountText})",
            tree.GraphRefreshesForChanges - before == 1 && tree.StatusCountText != shown);
    }

    // ---- J129: a slow listing and an item selected since -----------------------------------

    private static async Task VmRound2SlowPruneAsync(string root)
    {
        Section("view model round 2: a slow listing and an item selected since it began");
        var folder = Path.Combine(root, "prune");
        var scratch = Path.Combine(root, "prune-state");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(scratch);
        var first = Path.Combine(folder, "first.txt");
        File.WriteAllText(first, "first");

        using var icons = new ShellIconService();
        using var tree = NewTree(scratch, icons);
        tree.PreferLightReveal = true;
        tree.IsCanvasShown = false;
        await tree.InitializeAsync(folder);
        tree.Selection.ReplaceSingle(first, false, 5, SelectionSource.Canvas);
        await Task.Delay(150);

        // A change in the folder: what is selected in it is checked against
        // a listing made off this thread.  The listing is done before this
        // thread looks at it again - a share taking its time - and meanwhile
        // a folder is made and selected, as New folder does.
        var sink = (IChangeSink)tree;
        sink.FolderChanged(ChangeConsumer.Nested, folder, new FolderChange(folder, ChangeKinds.Structural, Stopwatch.GetTimestamp(), default, default));
        Thread.Sleep(500);
        var made = Path.Combine(folder, "New folder");
        Directory.CreateDirectory(made);
        tree.Selection.ReplaceSingle(made, true, 0, SelectionSource.Command);
        await Task.Delay(400);
        Check($"an item made and selected while an older listing was under way stays selected ({tree.Selection.Count} selected, there: {Directory.Exists(made)})",
            tree.Selection.Count == 1 && tree.Selection.Contains(made));

        // What was selected when the listing began and is not in it still goes.
        var doomed = Path.Combine(folder, "doomed.txt");
        File.WriteAllText(doomed, "doomed");
        tree.Selection.ReplaceSingle(doomed, false, 6, SelectionSource.Canvas);
        await Task.Delay(150);
        File.Delete(doomed);
        sink.FolderChanged(ChangeConsumer.Nested, folder, new FolderChange(folder, ChangeKinds.Structural, Stopwatch.GetTimestamp() + 1, default, default));
        await WaitUntil(() => !tree.Selection.Contains(doomed), 3_000);
        Check("an item selected before the listing and gone from it is let go of, as ever", !tree.Selection.Contains(doomed));
    }

    // ---- J130: an item found gone ----------------------------------------------------------

    private static async Task VmRound2GoneFocusAsync(string root)
    {
        Section("view model round 2: an item found gone");
        var folder = Path.Combine(root, "gone");
        var scratch = Path.Combine(root, "gone-state");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(scratch);
        File.WriteAllText(Path.Combine(folder, "kept.txt"), "kept");

        using var icons = new ShellIconService();
        using var tree = NewTree(scratch, icons);
        tree.PreferLightReveal = true;
        tree.IsCanvasShown = false;
        await tree.InitializeAsync(folder);
        var messages = new List<string>();
        var refreshed = new List<string>();
        tree.MessageRequested += (message, _) => messages.Add(message);
        tree.PathRefreshed += refreshed.Add;
        int NoLongerInside() => messages.Count(message => message.Contains("is no longer inside", StringComparison.Ordinal));

        // A tile for a file deleted from outside, clicked.
        var gone = Path.Combine(folder, "gone.txt");
        tree.Selection.ReplaceSingle(gone, false, 4, SelectionSource.Canvas);
        await WaitUntil(() => !tree.Selection.Contains(gone) && refreshed.Count > 0, 3_000);
        await Task.Delay(500);
        var folderRefreshes = refreshed.Count(path => ViewAllPath.Equals(path, folder));
        Check($"an item found gone is said to be gone once ({NoLongerInside()} times)", NoLongerInside() == 1);
        Check($"and its folder is read again once ({folderRefreshes} times)", folderRefreshes == 1);
        Check("and it is let go of, as ever", !tree.Selection.Contains(gone));
        Check($"and the focus goes to its folder, so nothing asks for it again ({tree.FocusedPath})", ViewAllPath.Equals(tree.FocusedPath, folder));

        // One the disk cannot be asked about - a name no volume takes, as a
        // share that has stopped answering for a moment cannot be asked -
        // is not said to be gone, and stays selected.
        messages.Clear();
        var unreadable = Path.Combine(folder, new string('x', 300) + ".txt");
        tree.Selection.ReplaceSingle(unreadable, false, 0, SelectionSource.Canvas);
        await Task.Delay(800);
        Check($"an item the disk cannot be asked about is not said to be gone ({string.Join(" | ", messages)})", NoLongerInside() == 0);
        Check("and stays selected", tree.Selection.Contains(unreadable));
    }
}
