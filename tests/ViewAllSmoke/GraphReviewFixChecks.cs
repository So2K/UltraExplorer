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
            RunOnSta("graph review: nested refreshes", () => GraphNestedRefreshChecksAsync(root));
            RunOnSta("graph review: a closed folder read again", () => GraphClosedRefreshChecksAsync(root));
            RunOnSta("graph review: hidden folders above the selection", () => GraphHiddenAncestorChecksAsync(root));
            RunOnSta("graph review: a refresh that cannot read its folder", () => GraphUnreadableRefreshChecksAsync(root));
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

    // ---- J049: a folder read again while its open sub-folder is ------------------------------

    /// <summary>
    /// F5 on a folder and then on the folder above it - or the watch reading
    /// both, in that order, during a build: the folder above found the
    /// sub-folder already emptied and closed by its own refresh, and brought
    /// it back closed, with everything open in it gone.
    /// </summary>
    private static async Task GraphNestedRefreshChecksAsync(string root)
    {
        Section("graph review: a folder read again while its open sub-folder is keeps the sub-folder open (J049)");
        var top = Path.Combine(root, "nested-refresh");
        var child = Path.Combine(top, "child");
        var grand = Path.Combine(child, "grand");
        Directory.CreateDirectory(grand);
        File.WriteAllText(Path.Combine(grand, "g.txt"), "x");
        File.WriteAllText(Path.Combine(child, "c.txt"), "x");

        using var graph = new ViewAllGraphService();
        var topNode = (await graph.AddRootAsync(top))!;
        await graph.ExpandAsync(topNode);
        graph.TryGetNode(child, out var childNode);
        await graph.ExpandAsync(childNode);
        graph.TryGetNode(grand, out var grandNode);
        await graph.ExpandAsync(grandNode);

        // Both begun before either is answered, as on the window's thread.
        var first = graph.RefreshBranchAsync(childNode);
        var second = graph.RefreshBranchAsync(topNode);
        await Task.WhenAll(first, second);
        Check("the sub-folder is open again",
            graph.TryGetNode(child, out var childAgain) && childAgain.IsExpanded && childAgain.Children.Count == 2);
        Check("and so is the folder open inside it, with what it holds",
            graph.TryGetNode(grand, out var grandAgain) && grandAgain.IsExpanded
            && graph.TryGetNode(Path.Combine(grand, "g.txt"), out var leaf) && leaf.IsTreeVisible);
    }

    // ---- J045: a closed folder read again ----------------------------------------------------

    /// <summary>
    /// A folder that has been read and closed, read again for a change in it -
    /// a download finishing, a file operation: the refresh always ended by
    /// opening it.  Told it is for a change, the refresh reads it, opens
    /// again what was open inside it, and leaves it closed.
    /// </summary>
    private static async Task GraphClosedRefreshChecksAsync(string root)
    {
        Section("graph review: a closed folder read again for a change stays closed (J045)");
        var top = Path.Combine(root, "shut");
        var inner = Path.Combine(top, "inner");
        var deep = Path.Combine(inner, "deep");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "d.txt"), "x");

        using var graph = new ViewAllGraphService();
        var topNode = (await graph.AddRootAsync(top))!;
        await graph.ExpandAsync(topNode);
        graph.TryGetNode(inner, out var innerNode);
        await graph.ExpandAsync(innerNode);
        graph.TryGetNode(deep, out var deepNode);
        await graph.ExpandAsync(deepNode);
        graph.Collapse(innerNode);

        File.WriteAllText(Path.Combine(inner, "new.txt"), "x");
        await graph.RefreshBranchAsync(innerNode, forChange: true);
        Check($"the folder read again is still closed (open: {innerNode.IsExpanded})", !innerNode.IsExpanded);
        Check("and read: it holds the new file, off the tree",
            innerNode.AreChildrenLoaded
            && graph.TryGetNode(Path.Combine(inner, "new.txt"), out var added) && !added.IsTreeVisible);
        Check("with the folder open inside it still open, for when it is opened",
            graph.TryGetNode(deep, out var deepAgain) && deepAgain.IsExpanded && !deepAgain.IsTreeVisible
            && deepAgain.Children.All(item => !item.IsTreeVisible));

        await graph.ExpandAsync(innerNode);
        Check("opened, it shows all of it",
            innerNode.IsExpanded && innerNode.Children.All(item => item.IsTreeVisible && item.HasLayoutPosition)
            && graph.TryGetNode(Path.Combine(deep, "d.txt"), out var leaf) && leaf.IsTreeVisible);

        // An open folder read for a change stays open, as ever; and a
        // closed one read for a caller about to show it opens, as ever.
        await graph.RefreshBranchAsync(innerNode, forChange: true);
        Check("an open folder read again for a change stays open", innerNode.IsExpanded && innerNode.Children.All(item => item.IsTreeVisible));
        graph.Collapse(innerNode);
        await graph.RefreshBranchAsync(innerNode);
        Check("and a closed one read again not for a change opens, as before", innerNode.IsExpanded);
    }

    // ---- J057: hidden folders above what is selected -----------------------------------------

    /// <summary>
    /// Hidden items hidden while something inside a hidden folder is
    /// selected - a file in ProgramData or AppData: the refresh of the open
    /// folders left the hidden folder out of the listing and let go of
    /// everything under it, the selection with it.  The folders above what is
    /// selected are kept by name, as a folder typed into the address bar is;
    /// what is selected is listed again if it may be shown, and a hidden item
    /// itself, or a hidden folder nothing is selected in, still goes.
    /// </summary>
    private static async Task GraphHiddenAncestorChecksAsync(string root)
    {
        Section("graph review: hiding hidden items keeps the hidden folders above the selection (J057)");
        var top = Path.Combine(root, "hidden-above");
        var cache = Path.Combine(top, ".cache");
        var inner = Path.Combine(cache, "inner.txt");
        var otherHidden = Path.Combine(top, ".other");
        var secret = Path.Combine(top, "secret.txt");
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(otherHidden);
        File.WriteAllText(inner, "x");
        File.WriteAllText(Path.Combine(otherHidden, "x.txt"), "x");
        File.WriteAllText(secret, "x");
        File.WriteAllText(Path.Combine(top, "plain.txt"), "x");
        File.SetAttributes(cache, File.GetAttributes(cache) | FileAttributes.Hidden);
        File.SetAttributes(otherHidden, File.GetAttributes(otherHidden) | FileAttributes.Hidden);
        File.SetAttributes(secret, File.GetAttributes(secret) | FileAttributes.Hidden);

        using var graph = new ViewAllGraphService(new ViewAllGraphOptions(IncludeHidden: true));
        var topNode = (await graph.AddRootAsync(top))!;
        await graph.ExpandAsync(topNode);
        graph.TryGetNode(cache, out var cacheNode);
        graph.TryGetNode(otherHidden, out var otherNode);
        await graph.ExpandAsync(cacheNode);
        await graph.ExpandAsync(otherNode);
        graph.TryGetNode(inner, out var innerNode);
        graph.TryGetNode(secret, out var secretNode);
        innerNode.IsSelected = true;
        secretNode.IsSelected = true;

        await graph.ApplyOptionsAsync(graph.Options with { IncludeHidden = false });
        Check("the file selected inside the hidden folder is still there, on the tree",
            graph.TryGetNode(inner, out var innerAgain) && innerAgain.IsTreeVisible);
        Check("under its hidden folder, kept open",
            graph.TryGetNode(cache, out var cacheAgain) && cacheAgain.IsExpanded && cacheAgain.IsTreeVisible
            && innerAgain is not null && ReferenceEquals(innerAgain.Parent, cacheAgain));
        Check("while a hidden file selected itself goes, as ever", !graph.TryGetNode(secret, out _));
        Check("and so does a hidden folder nothing is selected in", !graph.TryGetNode(otherHidden, out _));
    }

    // ---- J162: a refresh that cannot read its folder ------------------------------------------

    /// <summary>
    /// A folder read again at a moment it cannot be listed - a share that
    /// blinks; here a folder whose listing is denied for a moment: the refresh
    /// emptied and closed it before it read, so everything that was open in it
    /// was gone for good, and no longer watched.  Read first, it keeps all of
    /// it and says the folder could not be read; once it can be, it is read as
    /// ever.
    /// </summary>
    private static async Task GraphUnreadableRefreshChecksAsync(string root)
    {
        Section("graph review: a refresh that cannot read its folder keeps everything open in it (J162)");
        var top = Path.Combine(root, "blink");
        var sub = Path.Combine(top, "sub");
        var deep = Path.Combine(sub, "deep");
        var leafPath = Path.Combine(deep, "d.txt");
        Directory.CreateDirectory(deep);
        File.WriteAllText(leafPath, "x");
        File.WriteAllText(Path.Combine(top, "t.txt"), "x");

        using var graph = new ViewAllGraphService();
        var topNode = (await graph.AddRootAsync(top))!;
        await graph.ExpandAsync(topNode);
        graph.TryGetNode(sub, out var subNode);
        await graph.ExpandAsync(subNode);
        graph.TryGetNode(deep, out var deepNode);
        await graph.ExpandAsync(deepNode);

        var directory = new DirectoryInfo(top);
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = directory.GetAccessControl();
        security.AddAccessRule(deny);
        directory.SetAccessControl(security);
        try
        {
            try
            {
                _ = Directory.GetFileSystemEntries(top);
                Console.WriteLine("  note  listing could not be denied here; the check is not run");
                return;
            }
            catch (UnauthorizedAccessException)
            {
            }

            await graph.RefreshBranchAsync(topNode, forChange: true);
            Check($"the folder that could not be read stays open with what it held (open: {topNode.IsExpanded}, {topNode.Children.Count} of 2)",
                topNode.IsExpanded && topNode.Children.Count == 2);
            Check("and so does the folder open in it, with the one open in that, all of it on the tree",
                graph.TryGetNode(sub, out var subKept) && subKept.IsExpanded
                && graph.TryGetNode(deep, out var deepKept) && deepKept.IsExpanded
                && graph.TryGetNode(leafPath, out var leafKept) && leafKept is { IsTreeVisible: true, HasLayoutPosition: true });
            Check($"while it says it could not be read ({topNode.ErrorMessage})", topNode.ErrorMessage == "Access denied");
        }
        finally
        {
            var restore = directory.GetAccessControl();
            restore.RemoveAccessRule(deny);
            directory.SetAccessControl(restore);
        }

        File.WriteAllText(Path.Combine(top, "new.txt"), "x");
        await graph.RefreshBranchAsync(topNode, forChange: true);
        Check("read again once it can be, it has what is new, with all that was open still open and the error gone",
            topNode.IsExpanded && topNode.Children.Count == 3 && topNode.ErrorMessage.Length == 0
            && graph.TryGetNode(sub, out var subAgain) && subAgain.IsExpanded
            && graph.TryGetNode(deep, out var deepAgain) && deepAgain.IsExpanded
            && graph.TryGetNode(leafPath, out var leafAgain) && leafAgain.IsTreeVisible);
    }
}
