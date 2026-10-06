using System.IO;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task RefreshIdentityConsumerChecks()
    {
        RunOnSta("current graph objects at consumer commit", RefreshIdentityConsumerOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task RefreshIdentityConsumerOnStaAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerRefreshConsumers", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RefreshHideConsumerAsync(root);
            await RefreshRevealConsumerAsync(root, supersede: false);
            await RefreshRevealConsumerAsync(root, supersede: true);
        }
        finally { TryDelete(root); }
    }

    private static async Task RefreshHideConsumerAsync(string root)
    {
        Section("Hide selected re-resolves earlier nodes after a later materialization yields");
        var folder = Path.Combine(root, "hide");
        var first = Path.Combine(folder, "first");
        var second = Path.Combine(folder, "second-hidden");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.SetAttributes(second, FileAttributes.Directory | FileAttributes.Hidden);
        using var icons = new ShellIconService();
        using var tree = NewTree(root, icons);
        await tree.InitializeAsync(folder);
        if (!tree.TryGetNode(folder, out var parent)) throw new InvalidOperationException("Owned parent was not materialized.");
        await tree.ExpandAsync(parent);
        if (!tree.TryGetNode(first, out var oldFirst)) throw new InvalidOperationException("Owned first folder was not listed.");
        Check("the fixture's second folder genuinely needs by-name materialization", !tree.TryGetNode(second, out _));
        tree.Selection.Apply(new SelectionEdit
        {
            Clear = true, Added = [new(first, true, 0), new(second, true, 0)],
            Focus = first, Anchor = first, Source = SelectionSource.Canvas
        });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tree.MaterializeBarrierForChecks = path => path == second ? Block() : Task.CompletedTask;
        async Task Block() { entered.TrySetResult(); await release.Task; }
        IReadOnlyList<string>? hiddenPaths = null;
        tree.FoldersHiddenByUser += paths => hiddenPaths = paths;
        var hiding = tree.HideSelectedAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await tree.RefreshAsync(parent).WaitAsync(TimeSpan.FromSeconds(10));
            Check("the intervening refresh really replaced the first selected node",
                tree.TryGetNode(first, out var replaced) && !ReferenceEquals(replaced, oldFirst));
            release.TrySetResult();
            await hiding.WaitAsync(TimeSpan.FromSeconds(15));
            Check("the LIVE replacement of the earlier folder is hidden, not only its detached old object",
                tree.TryGetNode(first, out var live) && live.IsUserHidden && !live.IsTreeVisible);
            Check("the later materialized folder is also hidden", tree.TryGetNode(second, out var late) && late.IsUserHidden && !late.IsTreeVisible);
            Check("the explicit-hide bridge retains both resolved paths after the selection is cleared",
                tree.Selection.Count == 0 && hiddenPaths is not null && hiddenPaths.SequenceEqual([first, second], StringComparer.OrdinalIgnoreCase));
        }
        finally { release.TrySetResult(); tree.MaterializeBarrierForChecks = null; }
    }

    private static async Task RefreshRevealConsumerAsync(string root, bool supersede)
    {
        Section(supersede ? "a newer navigation cancels the stale consumer commit" : "multi-reveal commits current focus and fresh metadata after a rebuild");
        var folder = Path.Combine(root, supersede ? "reveal-canceled" : "reveal");
        Directory.CreateDirectory(folder);
        var first = Path.Combine(folder, "first.txt");
        var second = Path.Combine(folder, "second.txt");
        File.WriteAllText(first, "a");
        File.WriteAllText(second, "b");
        using var icons = new ShellIconService();
        using var tree = NewTree(root, icons);
        await tree.InitializeAsync(folder);
        if (!tree.TryGetNode(folder, out var parent)) throw new InvalidOperationException("Owned reveal parent was not materialized.");
        await tree.ExpandAsync(parent);
        if (!tree.TryGetNode(first, out var oldFirst)) throw new InvalidOperationException("Owned first file was not listed.");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tree.MaterializeBarrierForChecks = path => path == second ? Block() : Task.CompletedTask;
        async Task Block() { entered.TrySetResult(); await release.Task; }
        if (!FolderCommandLine.TryReveal([first, second], out var invocation, out _)) throw new InvalidOperationException("Owned multi-reveal was invalid.");
        var applying = FolderInvocationNavigation.ApplyAsync(tree, invocation);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            File.WriteAllText(first, new string('x', 73));
            await tree.RefreshAsync(parent).WaitAsync(TimeSpan.FromSeconds(10));
            Check("the interleaving replaces first-file identity and changes its current metadata",
                tree.TryGetNode(first, out var rebuilt) && !ReferenceEquals(rebuilt, oldFirst) && rebuilt.Entry.SizeBytes == 73);
            if (supersede) tree.BeginNavigation();
            release.TrySetResult();
            var applied = await applying.WaitAsync(TimeSpan.FromSeconds(15));
            if (supersede)
            {
                Check("a superseded invocation never publishes its captured selection", !applied && !tree.Selection.Contains(first) && !tree.Selection.Contains(second));
                return;
            }
            Check("multi-reveal completes with both intended files, in their original order",
                applied && tree.Selection.Paths.SequenceEqual([first, second], StringComparer.OrdinalIgnoreCase));
            Check("the selected first file takes freshly rebuilt metadata, not the pre-await size",
                tree.Selection.TryGetItem(first, out var selected) && selected.Size == 73 && !selected.IsDirectory);
            Check("the active/list focus is the graph's LIVE first-file node after commit",
                tree.TryGetNode(first, out var live) && ReferenceEquals(tree.ActiveNode, live)
                && tree.Selection.Focus == first && tree.FolderList.FolderPath == folder);
            Check("the successful invocation retains its navigation-history intent", tree.Selection.LastRecordsNavigation);
        }
        finally { release.TrySetResult(); tree.MaterializeBarrierForChecks = null; }
    }
}
