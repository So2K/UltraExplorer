using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The second review's findings about the tree canvas's graph
/// (<see cref="ViewAllGraphService"/>), each checked in a section of its own
/// below, named by the finding it is about.
/// </summary>
internal static partial class Program
{
    private static Task GraphReviewFixChecks()
    {
        var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "UltraExplorerGraphReview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RunOnSta("graph review: load more merge", () => GraphLoadMoreMergeChecksAsync(root));
            RunOnSta("graph review: load more on a closed folder", () => GraphLoadMoreClosedChecksAsync(root));
            RunOnSta("graph review: layout during a refresh", () => GraphLayoutDuringRefreshChecksAsync(root));
        }
        finally
        {
            TryDelete(root);
        }

        return Task.CompletedTask;
    }

    // ---- J047: a page of Load more merged in one pass ------------------------------------

    /// <summary>
    /// Load more reads the folder again with a higher cap and merges the
    /// listing into what is there: every entry already loaded was looked for
    /// in the folder's children one by one, so a page into a folder of tens of
    /// thousands held the window for seconds.  Here a listing of 20,000 is
    /// merged into a folder that already holds every entry of it - all of the
    /// merge and none of the rest - and must take a few milliseconds, not
    /// hundreds; then the whole Load more is timed for the record.
    /// </summary>
    private static async Task GraphLoadMoreMergeChecksAsync(string root)
    {
        Section("graph review: a page of Load more is merged in one pass (J047)");
        const int count = 20_000;
        var folder = Path.Combine(root, "pages");
        Directory.CreateDirectory(folder);
        for (var index = 0; index < count; index++)
        {
            File.WriteAllBytes(Path.Combine(folder, $"p{index:D5}.bin"), []);
        }

        using var graph = new ViewAllGraphService();
        var node = (await graph.AddRootAsync(folder))!;
        await graph.ExpandAsync(node);
        var pages = new List<string>();
        while (node.IsTruncated)
        {
            var watch = Stopwatch.StartNew();
            await graph.LoadMoreAsync(node);
            pages.Add($"{node.Children.Count:N0} in {watch.ElapsedMilliseconds} ms");
        }

        Console.WriteLine($"  note  Load more pages: {string.Join(", ", pages)}");
        Check($"Load more brings in all {count:N0}", node.Children.Count == count && !node.IsTruncated);

        var snapshot = await new ViewAllFileSystemService().GetChildrenAsync(
            folder, graph.Options with { MaximumChildrenPerFolder = count }, CancellationToken.None, graph.Sort);
        var apply = typeof(ViewAllGraphService).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var added = new List<ViewAllNodeViewModel>();
        var best = double.MaxValue;
        for (var round = 0; round < 3; round++)
        {
            var merge = Stopwatch.StartNew();
            apply.Invoke(graph, [node, snapshot, added]);
            best = Math.Min(best, merge.Elapsed.TotalMilliseconds);
        }

        Check($"merging a listing of {count:N0} into a folder already holding them takes {best:F1} ms (budget 60 ms)", best <= 60);
        Check("and adds nothing twice", added.Count == 0 && node.Children.Count == count
            && node.Children.Distinct().Count() == count);
    }

    // ---- J052: Load more on a closed folder ------------------------------------------------

    /// <summary>
    /// Load more on a folder that is closed - its button is on the folder,
    /// open or not - with a child of the next page placed by hand last
    /// session: the page came in on the tree under a folder the layout does
    /// not measure, and every layout pass after that threw.
    /// </summary>
    private static async Task GraphLoadMoreClosedChecksAsync(string root)
    {
        Section("graph review: Load more on a closed folder keeps what it brings in off the tree (J052)");
        var folder = Path.Combine(root, "closed-pages");
        var other = Path.Combine(root, "closed-other");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "one.txt"), "x");
        for (var index = 0; index < 100; index++)
        {
            File.WriteAllBytes(Path.Combine(folder, $"f{index:D3}.bin"), []);
        }

        var pinned = Path.Combine(folder, "f050.bin");
        var describe = ViewAllFileSystemService.DescribeDrive;
        ViewAllFileSystemService.DescribeDrive = _ => null;
        try
        {
            using var graph = new ViewAllGraphService(new ViewAllGraphOptions(MaximumChildrenPerFolder: 32));
            await graph.InitializeAsync(new ViewAllWorkspaceState
            {
                ExtraRoots = [folder],
                Nodes =
                [
                    new ViewAllNodeState(folder, 0, 0, HasManualPosition: false, IsExpanded: false),
                    new ViewAllNodeState(pinned, 5_000, 9_000, HasManualPosition: true, IsExpanded: false)
                ]
            });
            if (!graph.TryGetNode(folder, out var node))
            {
                Check("the folder is a root", false);
                return;
            }

            await graph.ExpandAsync(node);
            graph.Collapse(node);
            Exception? failure = null;
            try
            {
                await graph.LoadMoreAsync(node, additionalChildren: 32);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            Check($"Load more on the closed folder goes through ({failure?.GetType().Name ?? "no error"}, {node.Children.Count} children)",
                failure is null && node.Children.Count == 64);
            Check("and what it brought in is off the tree with the rest of the closed folder",
                node.Children.All(child => !child.IsTreeVisible) && graph.TryGetNode(pinned, out var placed) && !placed.IsTreeVisible);

            failure = null;
            try
            {
                var otherNode = await graph.AddRootAsync(other);
                await graph.ExpandAsync(otherNode!);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            Check($"a folder opened afterwards is laid out as ever ({failure?.GetType().Name ?? "no error"})", failure is null);

            await graph.ExpandAsync(node);
            Check("opened again, the folder shows all 64, each placed",
                node.Children.Count == 64 && node.Children.All(child => child.IsTreeVisible && child.HasLayoutPosition));
        }
        finally
        {
            ViewAllFileSystemService.DescribeDrive = describe;
        }
    }

    // ---- J048: the layout while a folder is read again -------------------------------------

    /// <summary>
    /// A folder read again keeps the layout back until it has opened again
    /// everything that was open in it - but the hold was the whole graph's,
    /// so a folder opened meanwhile somewhere else got no places until the
    /// other's last sub-folder was read, and then jumped.
    /// </summary>
    private static async Task GraphLayoutDuringRefreshChecksAsync(string root)
    {
        Section("graph review: a folder opened while another is read again is laid out at once (J048)");
        var busy = Path.Combine(root, "busy");
        var small = Path.Combine(root, "small");
        Directory.CreateDirectory(small);
        File.WriteAllText(Path.Combine(small, "a.txt"), "x");
        File.WriteAllText(Path.Combine(small, "b.txt"), "x");
        for (var index = 0; index < 60; index++)
        {
            var sub = Path.Combine(busy, $"s{index:D2}");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "inside.txt"), "x");
        }

        using var graph = new ViewAllGraphService();
        var busyNode = (await graph.AddRootAsync(busy))!;
        await graph.ExpandAsync(busyNode);
        foreach (var sub in busyNode.Children.ToArray())
        {
            await graph.ExpandAsync(sub);
        }

        var smallNode = (await graph.AddRootAsync(small))!;
        var refresh = graph.RefreshBranchAsync(busyNode);
        await graph.ExpandAsync(smallNode);
        var placed = smallNode.Children.Count(child => child.IsTreeVisible && child.HasLayoutPosition);
        var during = !refresh.IsCompleted;
        await refresh;
        Check($"opened while the other is still being read again ({during}), its children are placed at once ({placed} of 2)",
            during && placed == 2);
        Check("and the folder read again is back as it was, every sub-folder open",
            graph.TryGetNode(busy, out var busyAgain)
            && busyAgain.Children.Count == 60
            && busyAgain.Children.All(sub => sub.IsExpanded && sub.Children.Count == 1 && sub.Children[0].HasLayoutPosition));
    }
}
