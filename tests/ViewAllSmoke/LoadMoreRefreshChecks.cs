using System.IO;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;

namespace ViewAllSmoke;

/// <summary>
/// What Load more brought into a folder stays there when the folder is read
/// again.  Before, a refresh read it back at the 5,000-item cap, so a file in
/// a folder of more than 5,000 items growing on disk - a download, a log -
/// took everything past the first 5,000 away again, and so did F5 on the
/// folder or on any folder open above it.
/// </summary>
internal static partial class Program
{
    private static async Task LoadMoreRefreshChecks()
    {
        var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "UltraExplorerLoadMoreRefresh", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await LoadMoreRefreshGraphAsync(root);
            await OnDispatcher(() => LoadMoreRefreshOnChangeAsync(root));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// The graph on its own, with a small cap: the folder read again, a folder
    /// above it read again, and a folder paged to the end read again.
    /// </summary>
    private static async Task LoadMoreRefreshGraphAsync(string root)
    {
        Section("load more kept by a refresh: the graph");
        var top = Path.Combine(root, "graph");
        var paged = Path.Combine(top, "paged");
        Directory.CreateDirectory(paged);
        for (var index = 0; index < 100; index++)
        {
            File.WriteAllText(Path.Combine(paged, $"item-{index:D3}.bin"), "x");
        }

        using var graph = new ViewAllGraphService(new ViewAllGraphOptions(MaximumChildrenPerFolder: 32));
        await graph.InitializeAsync();
        var topNode = (await graph.AddRootAsync(top))!;
        await graph.ExpandAsync(topNode);
        var pagedNode = topNode.Children.First(child => child.DisplayName == "paged");
        await graph.ExpandAsync(pagedNode);
        await graph.LoadMoreAsync(pagedNode, additionalChildren: 32);
        Check($"the folder is paged to 64 of 100 ({pagedNode.Children.Count})",
            pagedNode.Children.Count == 64 && pagedNode.IsTruncated && pagedNode.ChildLoadLimit == 64);

        await graph.RefreshBranchAsync(pagedNode);
        Check($"read again, it keeps the 64 Load more brought in and still offers the rest ({pagedNode.Children.Count})",
            pagedNode.Children.Count == 64 && pagedNode.IsTruncated && pagedNode.ChildLoadLimit == 64);

        await graph.RefreshBranchAsync(topNode);
        var reopened = graph.TryGetNode(paged, out var live) ? live : null;
        Check($"a folder above it read again opens it again with the 64 too ({reopened?.Children.Count})",
            reopened is { IsExpanded: true, IsTruncated: true, ChildLoadLimit: 64, Children.Count: 64 });
        Check("and the folder above, never paged, is read at the plain cap", topNode.ChildLoadLimit == 32);
        if (reopened is null)
        {
            return;
        }

        await graph.LoadMoreAsync(reopened, additionalChildren: 64);
        await graph.RefreshBranchAsync(reopened);
        Check($"paged to the end and read again, all 100 stay with nothing more to load ({reopened.Children.Count})",
            reopened.Children.Count == 100 && !reopened.IsTruncated);
    }

    /// <summary>
    /// The user's scenario end to end: a folder of more than 5,000 files on
    /// the tree canvas, Load more, then one of its files growing on disk,
    /// heard by the real change hub.
    /// </summary>
    private static async Task LoadMoreRefreshOnChangeAsync(string root)
    {
        Section("load more kept by a refresh: a file growing in a folder of more than 5,000");
        const int count = 5_050;
        var folder = Path.Combine(root, "big");
        var scratch = Path.Combine(root, "big-state");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(scratch);
        for (var index = 0; index < count; index++)
        {
            File.WriteAllText(Path.Combine(folder, $"f{index:D5}.txt"), "x");
        }

        var grown = Path.Combine(folder, "f00000.txt");
        var last = Path.Combine(folder, $"f{count - 1:D5}.txt");

        using var icons = new ShellIconService();
        using var hub = new ChangeHub(TimeProvider.System);
        using var tree = NewTree(scratch, icons);
        hub.Driver.Fallback = DispatcherFrameDriver.ForCurrentThread((ref FrameBudget budget) => hub.Drain(ref budget, tree), () => hub.HasWork);
        tree.Changes = hub;
        tree.PreferLightReveal = true;
        await tree.InitializeAsync(folder);
        if (!tree.TryGetNode(folder, out var node))
        {
            Check("the folder is on the tree", false);
            return;
        }

        await tree.ExpandAsync(node);
        Check($"opened, the folder shows the first 5,000 ({node.Children.Count}) and offers more",
            node.Children.Count == 5_000 && node.IsTruncated);
        await tree.LoadMoreAsync(node);
        Check($"Load more brings in all {count:N0} ({node.Children.Count})", node.Children.Count == count && !node.IsTruncated);

        // Whatever node the graph has for the folder by then: a refresh of a
        // folder above it would build it anew.
        ViewAllNodeViewModel? Live() => tree.TryGetNode(folder, out var live) ? live : null;
        var armed = await LiveWait(() => hub.RootFor(folder) is { State: WatchState.Armed }, 5_000) >= 0;
        File.AppendAllText(grown, "grown");
        var read = await LiveWait(
            () => Live() is { AreChildrenLoaded: true, IsLoading: false }
                && tree.TryGetNode(grown, out var file) && file.Entry.SizeBytes == 6,
            10_000) >= 0;
        Check("a file growing on disk has the folder read again", armed && read);
        var after = Live();
        Check($"and every item Load more brought in is still there ({after?.Children.Count} of {count:N0})",
            after is { IsTruncated: false } && after.Children.Count == count && tree.TryGetNode(last, out _));
    }
}
