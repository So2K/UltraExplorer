using UltraExplorer.Models;
using UltraExplorer.ViewModels;

namespace UltraExplorer.Services;

/// <summary>The same filesystem navigation for startup and resident requests.</summary>
internal static class FolderInvocationNavigation
{
    public static async Task<bool> ApplyAsync(ViewAllViewModel tree, FolderInvocation invocation, Func<bool>? allowed = null)
    {
        var ticket = tree.BeginNavigation();
        var holder = tree.SelectionHolder;
        bool IsCurrent() => tree.IsLatestNavigation(ticket) && ReferenceEquals(holder, tree.SelectionHolder)
            && (allowed?.Invoke() ?? true);

        // The request may have waited for window initialization or in IPC.
        // Check it again before cached graph nodes can select items now gone,
        // and keep a slow network path off the interface thread.
        var validated = await Task.Run(() =>
        {
            FolderInvocation valid = null!;
            var exists = invocation.Kind switch
            {
                FolderInvocationKind.OpenFolder => FolderCommandLine.TryOpenFolder(invocation.FolderPath, out valid, out _),
                FolderInvocationKind.Reveal => FolderCommandLine.TryReveal(invocation.SelectedPaths, out valid, out _),
                _ => false
            };
            return exists ? valid : null;
        });
        if (validated is null || !IsCurrent()) return false;
        invocation = validated;

        var folder = await tree.RevealAsync(invocation.FolderPath, focus: false,
            select: false, ticket: ticket, exact: true);
        if (!folder.IsExact || folder.Superseded || !IsCurrent()) return false;
        if (invocation.Kind == FolderInvocationKind.OpenFolder)
        {
            tree.Selection.Apply(new SelectionEdit { Clear = true, Anchor = folder.Node!.FullPath,
                Focus = folder.Node.FullPath, RecordsNavigation = true, Source = SelectionSource.Navigation });
            tree.FolderList.SetTarget(folder.Node.FullPath, folder.Node);
            return true;
        }

        foreach (var path in invocation.SelectedPaths)
        {
            var node = await tree.MaterializeAsync(path);
            if (node is null || !IsCurrent()) return false;
        }

        if (!IsCurrent()) return false;
        // Do not retain the first node across later awaits: refresh can replace
        // it with a same-path object. Re-read both list focus and item metadata.
        var items = new List<SelectionItem>(invocation.SelectedPaths.Count);
        ViewAllNodeViewModel? focus = null;
        foreach (var path in invocation.SelectedPaths)
        {
            if (!tree.TryGetNode(path, out var live)) return false;
            focus ??= live;
            items.Add(new(live.FullPath, live.IsDirectory, live.Entry.SizeBytes ?? 0));
        }
        if (focus is null) return false;
        tree.FolderList.SetTarget(invocation.FolderPath, focus);
        // Selecting a directory lights its tile in the parent. It must not
        // immediately navigate the folder list into the selected directory.
        using (tree.FolderList.HoldFolder())
        {
            tree.Selection.Apply(new SelectionEdit
            {
                Clear = true,
                Added = items,
                Anchor = items[0].Path,
                Focus = items[0].Path,
                RecordsNavigation = true,
                Source = SelectionSource.Navigation
            });
        }

        return true;
    }
}
