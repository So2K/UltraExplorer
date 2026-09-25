using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// Revealing a path by name instead of by opening its ancestors - what the tree
/// view model does while the nested canvas is the one on screen - and holding
/// back the tree canvas's own work while it is not.  What matters is that the
/// cheap way leaves the tree exactly as the full way would once the tree comes
/// back, that nothing it made outlives the folder it stood for, and that it is
/// actually cheap.
///
/// These run on a dispatcher of their own, like the app: the view model keeps
/// timers, and awaiting on a thread-pool thread would measure something the app
/// never does.
/// </summary>
internal static partial class Program
{
    private static Task LightReveal(string fixtureRoot) => OnDispatcher(() => LightRevealAsync(fixtureRoot));

    private static async Task LightRevealAsync(string fixtureRoot)
    {
        Section("light reveal");

        var root = Path.Combine(fixtureRoot, "light-reveal");
        var outer = Path.Combine(root, "outer");
        var middle = Path.Combine(outer, "middle");
        var inner = Path.Combine(middle, "inner");
        var scratch = Path.Combine(fixtureRoot, "light-reveal-state");
        Directory.CreateDirectory(inner);
        Directory.CreateDirectory(Path.Combine(middle, "sibling-a"));
        Directory.CreateDirectory(Path.Combine(middle, "sibling-b"));
        Directory.CreateDirectory(Path.Combine(outer, "other"));
        Directory.CreateDirectory(scratch);
        File.WriteAllText(Path.Combine(middle, "file-1.txt"), "one");
        File.WriteAllText(Path.Combine(inner, "deep.txt"), "deep");
        var drive = Path.GetPathRoot(root)!;

        using var icons = new ShellIconService();
        try
        {
            // ---- the defaults are the tree's ---------------------------------
            using (var plain = NewTree(scratch, icons))
            {
                Check("revealing by name is off unless asked for", !plain.PreferLightReveal);
                Check("the tree canvas counts as showing unless told otherwise", plain.IsCanvasShown);
            }

            // ---- only the chain is created ------------------------------------
            using var light = NewTree(scratch, icons);
            light.PreferLightReveal = true;
            light.IsCanvasShown = false;
            await light.InitializeAsync(drive);

            var before = CountNodes(light.Roots);
            var steps = ViewAllPath.AncestorChain(inner).Count - 1;
            var cold = Stopwatch.StartNew();
            var selected = await light.SelectPathAsync(inner);
            cold.Stop();
            Report(
                $"first reveal by name in this process, compiler included ({cold.Elapsed.TotalMilliseconds:F2} ms exactly)",
                cold.ElapsedMilliseconds,
                1_000);

            Check("a path revealed by name gets its node", selected is not null && ViewAllPath.Equals(selected.FullPath, inner));
            Check("exactly one node per missing step is created", CountNodes(light.Roots) == before + steps);
            Check("the selection is that node",
                light.SelectedNodes.Count == 1 && ReferenceEquals(light.SelectedNodes[0], selected));
            Check("it is the active node", ReferenceEquals(light.ActiveNode, selected)
                && ViewAllPath.Equals(light.ActivePath, inner));
            Check("the list follows it", ViewAllPath.Equals(light.FolderList.FolderPath, inner)
                && light.FolderList.Title == "inner");
            Check("the status bar names it", ViewAllPath.Equals(light.StatusPathText, inner));
            var lightStatus = light.StatusCountText;

            light.TryGetNode(middle, out var middleNode);
            Check("the folder above is not read", middleNode is { AreChildrenLoaded: false, IsExpanded: false });
            Check("so its siblings are not created", middleNode?.Children.Count == 1
                && !light.TryGetNode(Path.Combine(middle, "sibling-a"), out _)
                && !light.TryGetNode(Path.Combine(middle, "file-1.txt"), out _));
            Check("a node under a closed folder is not on the tree", selected is { IsTreeVisible: false, HasLayoutPosition: false });
            Check("nor is any step above it but the drive",
                Ancestors(selected!).Where(node => node.Parent is not null).All(node => !node.IsTreeVisible));
            Check("the drive stays on the tree", Ancestors(selected!).Last() is { Parent: null, IsTreeVisible: true });
            Check("the node count does not count what is off the tree",
                light.LogicalNodeCount == CountNodes(light.Roots, visibleOnly: true));
            Check("nothing is handed to a canvas that is not showing", light.RenderNodes.Count == 0);

            var again = await light.SelectPathAsync(inner);
            Check("revealing it again finds the same node", ReferenceEquals(again, selected)
                && CountNodes(light.Roots) == before + steps);

            // ---- spelled the disk's way ---------------------------------------
            using (var typed = NewTree(scratch, icons))
            {
                typed.PreferLightReveal = true;
                typed.IsCanvasShown = false;
                await typed.InitializeAsync(drive);
                var shouted = await typed.SelectPathAsync(Path.Combine(outer, "MIDDLE", "SIBLING-A"));
                Check("a name typed in another case is spelled the way the disk spells it",
                    shouted?.DisplayName == "sibling-a" && shouted.Parent?.DisplayName == "middle"
                    && shouted.FullPath.EndsWith(@"\outer\middle\sibling-a", StringComparison.Ordinal));
            }

            // ---- missing paths ------------------------------------------------
            var messages = new List<string>();
            light.MessageRequested += (message, _) => messages.Add(message);
            var missing = await light.RevealPathAsync(Path.Combine(middle, "not-there", "deeper"), focus: false);
            Check("a path that is not there reveals the deepest step that is",
                ReferenceEquals(missing, middleNode) && ReferenceEquals(light.ActiveNode, middleNode));
            Check("and says what is missing, as the full reveal does",
                messages.LastOrDefault() == "not-there is no longer inside middle.");

            // ---- opening the folder later keeps the node ------------------------
            await light.SelectPathAsync(inner);
            light.PreferLightReveal = false;
            light.IsCanvasShown = true;
            await light.RevealPathAsync(inner, focus: false);
            light.TryGetNode(inner, out var innerAfter);
            Check("opening the folders later keeps the node that was there", ReferenceEquals(innerAfter, selected));
            Check("and puts it on the tree", selected is { IsTreeVisible: true, HasLayoutPosition: true });
            Check("beside the siblings it did not have before",
                middleNode!.AreChildrenLoaded && middleNode.Children.Count == 4
                && middleNode.Children.Select(child => child.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4);

            using (var full = NewTree(scratch, icons))
            {
                await full.InitializeAsync(drive);
                await full.RevealPathAsync(inner, focus: false);
                var expected = Placed(full.Roots);
                var actual = Placed(light.Roots);
                Check("and the tree it ends up with is the one the full reveal builds",
                    expected.Count == actual.Count
                    && expected.All(pair => actual.TryGetValue(pair.Key, out var at) && (at - pair.Value).Length < 1e-6));
            }

            // ---- deleted and renamed folders do not linger ----------------------
            using (var prune = NewTree(scratch, icons))
            {
                prune.PreferLightReveal = true;
                prune.IsCanvasShown = false;
                await prune.InitializeAsync(drive);

                var doomed = Path.Combine(outer, "doomed");
                Directory.CreateDirectory(Path.Combine(doomed, "leaf"));
                var leaf = await prune.SelectPathAsync(Path.Combine(doomed, "leaf"));
                prune.TryGetNode(outer, out var outerNode);
                Check("a folder selected by name has a node", leaf is not null && prune.TryGetNode(doomed, out _));

                // The folder watcher follows the selection; moving it off the
                // folder first keeps the delete from waiting on its handle.
                prune.SelectOnly(outerNode!);
                Directory.Delete(doomed, recursive: true);
                await prune.RefreshPathAsync(outer);
                Check("re-reading a folder that was never read drops a child that was deleted",
                    !prune.TryGetNode(doomed, out _) && outerNode!.Children.All(child => !child.FullPath.EndsWith("doomed")));
                Check("with everything under it", !prune.TryGetNode(Path.Combine(doomed, "leaf"), out _));
                Check("and leaves the folder itself unread", outerNode is { AreChildrenLoaded: false });

                var pruneMessages = new List<string>();
                prune.MessageRequested += (message, _) => pruneMessages.Add(message);
                var after = await prune.RevealPathAsync(doomed, focus: false);
                Check("so asking for it again does not find a ghost",
                    ReferenceEquals(after, outerNode) && pruneMessages.LastOrDefault() == "doomed is no longer inside outer.");

                var kept = await prune.SelectPathAsync(Path.Combine(outer, "other"));
                await prune.RefreshPathAsync(outer);
                Check("a child that is still there is kept", kept is not null
                    && prune.TryGetNode(Path.Combine(outer, "other"), out var stillThere) && ReferenceEquals(stillThere, kept));

                var shouting = Path.Combine(outer, "Shouting");
                Directory.CreateDirectory(shouting);
                var loud = await prune.SelectPathAsync(shouting);
                prune.SelectOnly(outerNode!);
                var renamed = false;
                try
                {
                    Directory.Move(shouting, Path.Combine(outer, "shouting"));
                    renamed = true;
                }
                catch (IOException)
                {
                }

                if (renamed)
                {
                    await prune.RefreshPathAsync(outer);
                    var quiet = await prune.RevealPathAsync(Path.Combine(outer, "shouting"), focus: false);
                    Check("a rename to another spelling replaces the node",
                        loud is not null && quiet is not null && !ReferenceEquals(loud, quiet) && quiet.DisplayName == "shouting");
                }
                else
                {
                    Check("a case-only rename is not possible here", true);
                }
            }

            // ---- the full reveal is what it was ---------------------------------
            using (var tree = NewTree(scratch, icons))
            {
                await tree.InitializeAsync(drive);
                var node = await tree.RevealPathAsync(inner, focus: false);
                tree.TryGetNode(middle, out var parent);
                Check("without it, revealing opens every folder on the way",
                    parent is { AreChildrenLoaded: true, IsExpanded: true } && parent.Children.Count == 4);
                Check("and the node is on the tree", node is { IsTreeVisible: true, HasLayoutPosition: true });
                Check("and handed to the canvas", node is not null && tree.RenderNodes.Contains(node));
                Check("and counted", tree.LogicalNodeCount == CountNodes(tree.Roots, visibleOnly: true));
                Check("the status bar reads the same however the node was made",
                    tree.StatusCountText == lightStatus && ViewAllPath.Equals(tree.StatusPathText, inner));
            }

            // ---- a canvas that is away catches up when it comes back ------------
            using (var away = NewTree(scratch, icons))
            using (var shown = NewTree(scratch, icons))
            {
                await away.InitializeAsync(drive);
                await shown.InitializeAsync(drive);
                await Settle(away);
                var invalidated = 0;
                away.GraphInvalidated += () => invalidated++;

                away.IsCanvasShown = false;
                var rendered = away.RenderNodes.ToArray();
                await away.RevealPathAsync(inner, focus: false);
                away.UpdateViewport(new Point(40, 30), new Size(1200, 800), 1);
                await Task.Delay(80);
                Check("nothing is culled or handed over while the canvas is away",
                    away.RenderNodes.SequenceEqual(rendered) && invalidated == 0);
                Check("but the count still is", away.LogicalNodeCount == CountNodes(away.Roots, visibleOnly: true));

                away.IsCanvasShown = true;
                Check("coming back rebuilds the batched layers once", invalidated == 1);
                await Settle(away);

                await shown.RevealPathAsync(inner, focus: false);
                shown.UpdateViewport(new Point(40, 30), new Size(1200, 800), 1);
                await Task.Delay(80);
                await Settle(shown);
                Check("and hands the canvas what it would have had all along",
                    away.RenderNodes.Count > 0
                    && away.RenderNodes.Select(node => node.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase)
                        .SetEquals(shown.RenderNodes.Select(node => node.FullPath)));
            }

            // ---- a folder opened while the tree was away ------------------------
            using (var opened = NewTree(scratch, icons))
            {
                opened.PreferLightReveal = true;
                opened.IsCanvasShown = false;
                await opened.InitializeAsync(drive);
                var folder = await opened.SelectPathAsync(middle);
                await opened.ExpandAsync(folder!);
                opened.IsCanvasShown = true;
                Check("coming back leaves nothing on the tree under a folder that is not",
                    Everything(opened.Roots).All(node => node.IsTreeVisible == ShouldBeOnTree(node)));
                Check("so what the folder holds is not counted", opened.LogicalNodeCount == CountNodes(opened.Roots, visibleOnly: true)
                    && folder!.Children.All(child => !child.IsTreeVisible));
            }

            // ---- how long it takes ----------------------------------------------
            var etc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers", "etc");
            if (!Directory.Exists(etc))
            {
                Check("no drivers\\etc folder to measure on this machine", true);
                return;
            }

            var etcDrive = Path.GetPathRoot(etc)!;
            using (var quick = NewTree(scratch, icons))
            {
                quick.PreferLightReveal = true;
                quick.IsCanvasShown = false;
                await quick.InitializeAsync(etcDrive);
                var watch = Stopwatch.StartNew();
                var node = await quick.SelectPathAsync(etc);
                watch.Stop();
                Check("drivers\\etc is revealed by name", node is not null && ViewAllPath.Equals(quick.ActivePath, etc));
                Report(
                    $"revealing drivers\\etc by name on a fresh graph ({watch.Elapsed.TotalMilliseconds:F2} ms exactly)",
                    watch.ElapsedMilliseconds,
                    60);
            }

            using (var slow = NewTree(scratch, icons))
            {
                await slow.InitializeAsync(etcDrive);
                var watch = Stopwatch.StartNew();
                var node = await slow.SelectPathAsync(etc);
                watch.Stop();
                Check("drivers\\etc is revealed by opening its folders", node is not null && ViewAllPath.Equals(slow.ActivePath, etc));
                Report(
                    $"revealing drivers\\etc by opening every folder on a fresh graph ({watch.Elapsed.TotalMilliseconds:F2} ms exactly)",
                    watch.ElapsedMilliseconds,
                    8_000);
            }
        }
        finally
        {
            TryDelete(root);
            TryDelete(scratch);
        }
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>
    /// A tree view model whose workspace and marks live in the scratch folder,
    /// never in the user's own state.
    /// </summary>
    private static ViewAllViewModel NewTree(string scratch, ShellIconService icons)
    {
        var marks = new FolderMarkService(Path.Combine(scratch, $"marks-{Guid.NewGuid():N}.json"));
        return new ViewAllViewModel(marks, icons, Path.Combine(scratch, $"tree-{Guid.NewGuid():N}.json"));
    }

    /// <summary>Lets the canvas's fill timer hand over everything it is holding back.</summary>
    private static async Task Settle(ViewAllViewModel tree)
    {
        var stable = 0;
        var last = -1;
        for (var attempt = 0; attempt < 400 && stable < 5; attempt++)
        {
            await Task.Delay(10);
            stable = tree.RenderNodes.Count == last ? stable + 1 : 0;
            last = tree.RenderNodes.Count;
        }
    }

    private static IEnumerable<ViewAllNodeViewModel> Everything(IEnumerable<ViewAllNodeViewModel> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var descendant in Everything(root.Children))
            {
                yield return descendant;
            }
        }
    }

    private static int CountNodes(IEnumerable<ViewAllNodeViewModel> roots, bool visibleOnly = false)
        => Everything(roots).Count(node => !visibleOnly || node.IsTreeVisible);

    private static Dictionary<string, Point> Placed(IEnumerable<ViewAllNodeViewModel> roots)
        => Everything(roots)
            .Where(node => node.IsTreeVisible && node.HasLayoutPosition)
            .ToDictionary(node => node.FullPath, node => node.Location, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<ViewAllNodeViewModel> Ancestors(ViewAllNodeViewModel node)
    {
        for (var walk = node.Parent; walk is not null; walk = walk.Parent)
        {
            yield return walk;
        }
    }

    /// <summary>The rule every node on the tree follows: its folder is on it and open, and it is not hidden.</summary>
    private static bool ShouldBeOnTree(ViewAllNodeViewModel node)
        => !node.IsUserHidden && (node.Parent is null || (node.Parent.IsTreeVisible && node.Parent.IsExpanded));

    /// <summary>
    /// Runs an async check on an STA thread with a dispatcher pumping, so its
    /// awaits come back to that thread as they do in the app.
    /// </summary>
    private static Task OnDispatcher(Func<Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            var task = Task.Factory.StartNew(
                body,
                CancellationToken.None,
                TaskCreationOptions.None,
                TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();
            task.ContinueWith(_ => dispatcher.InvokeAsync(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            failure = task.Exception?.GetBaseException();
            dispatcher.InvokeShutdown();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            Console.WriteLine($"  FAIL  light reveal threw: {failure}");
            _checks++;
            _failures++;
        }

        return Task.CompletedTask;
    }
}
