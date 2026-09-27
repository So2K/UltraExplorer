using System.Diagnostics;
using System.IO;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// The selection the window shares (<see cref="ItemSelection"/>): one change
/// per gesture however many items it names, totals kept as it goes, the
/// folder a selection sits in, pruning what a refresh found gone, a file
/// dialog's single item - and, through the view model and the folder list,
/// one focus change, one watcher and the list lighting the rows it holds.
/// </summary>
internal static partial class Program
{
    private static async Task ItemSelectionChecks()
    {
        Section("item selection");
        ItemSelectionModelChecks();
        await OnDispatcher(ItemSelectionViewModelChecksAsync);
    }

    private static void ItemSelectionModelChecks()
    {
        // ---- one change, whole --------------------------------------------------------
        var selection = new ItemSelection();
        var changes = 0;
        selection.Changed += _ => changes++;
        var items = new SelectionItem[10_000];
        long bytes = 0;
        for (var index = 0; index < items.Length; index++)
        {
            var isDirectory = index % 10 == 0;
            var size = isDirectory ? 0 : 1_000L + index;
            bytes += size;
            items[index] = new SelectionItem($@"Q:\big\item{index:D5}{(isDirectory ? string.Empty : ".bin")}", isDirectory, size);
        }

        // Once to have everything compiled, then timed.
        new ItemSelection().Apply(new SelectionEdit { Clear = true, Added = items, Container = @"Q:\big" });
        var timer = Stopwatch.StartNew();
        selection.Apply(new SelectionEdit
        {
            Clear = true,
            Container = @"Q:\big",
            Added = items,
            Anchor = items[0].Path,
            Focus = items[^1].Path,
            Source = SelectionSource.Canvas
        });
        timer.Stop();
        Check($"one edit of 10,000 items is one change ({changes}) in {timer.Elapsed.TotalMilliseconds:0.00} ms", changes == 1);
        Check("with the totals exact: 10,000 items, 1,000 folders, 9,000 files and their bytes",
            selection.Count == 10_000 && selection.FolderCount == 1_000 && selection.FileCount == 9_000 && selection.TotalBytes == bytes);
        Check("the folder they are all in is the container", selection.Container == @"Q:\big");
        Check("the anchor and the focus are where the edit put them",
            selection.Anchor == items[0].Path && selection.Focus == items[^1].Path);

        var paths = selection.Paths;
        Check("the paths are one list until the next change", ReferenceEquals(paths, selection.Paths) && paths.Count == 10_000);
        var version = selection.Version;
        selection.Apply(new SelectionEdit
        {
            Clear = true,
            Container = @"Q:\big",
            Added = items,
            Anchor = items[0].Path,
            Focus = items[^1].Path,
            Source = SelectionSource.List
        });
        Check("the same selection again is no change at all: no event, no new version",
            changes == 1 && selection.Version == version && ReferenceEquals(paths, selection.Paths));

        selection.Apply(new SelectionEdit { Removed = [items[1].Path, items[2].Path], Source = SelectionSource.Canvas });
        Check("taking two out is one change, and a new list of paths",
            changes == 2 && selection.Count == 9_998 && !ReferenceEquals(paths, selection.Paths)
            && selection.TotalBytes == bytes - items[1].Size - items[2].Size);
        Check("a path is found whatever its case", selection.Contains(items[3].Path.ToUpperInvariant()));

        // ---- the container ------------------------------------------------------------
        var spread = new ItemSelection();
        spread.Apply(new SelectionEdit { Added = [new SelectionItem(@"Q:\a\one.txt", false, 1), new SelectionItem(@"Q:\a\two.txt", false, 2)] });
        Check("items in one folder, their folder read off their paths, have it as the container", spread.Container == @"Q:\a");
        spread.Apply(new SelectionEdit { Added = [new SelectionItem(@"Q:\b\three.txt", false, 3)] });
        Check("items in two folders have none", spread.Container is null);
        spread.Remove([@"Q:\b\three.txt"], SelectionSource.Command);
        Check("and one again once the other folder's item goes", spread.Container == @"Q:\a");
        var drives = new ItemSelection();
        drives.Apply(new SelectionEdit { Added = [new SelectionItem(@"Q:\", true, 0), new SelectionItem(@"R:\", true, 0)] });
        Check("drives are in This PC, which is no folder to paste into", drives.Container is null && drives.Count == 2);
        var inDrive = new ItemSelection();
        inDrive.Apply(new SelectionEdit { Added = [new SelectionItem(@"Q:\x", true, 0), new SelectionItem(@"Q:\y.txt", false, 0)] });
        Check("items at the top of a drive are in the drive itself", inDrive.Container == @"Q:\");
        Check("a path's folder is read off it without a copy: C:\\Windows is in C:\\, C:\\ in This PC",
            ItemSelection.ParentOf(@"C:\Windows").SequenceEqual(@"C:\") && ItemSelection.ParentOf(@"C:\").Length == 0
            && ItemSelection.ParentOf(@"C:\Windows\System32\").SequenceEqual(@"C:\Windows"));
        Check("a share or a WSL distribution is in This PC beside the drives, and what is in one is in it",
            ItemSelection.ParentOf(@"\\wsl.localhost\Ubuntu").Length == 0 && ItemSelection.ParentOf(@"\\server\share\").Length == 0
            && ItemSelection.ParentOf(@"\\server\share\docs").SequenceEqual(@"\\server\share")
            && ItemSelection.ParentOf(@"\\server\share\docs\a.txt").SequenceEqual(@"\\server\share\docs"));
        var share = new ItemSelection();
        share.Apply(new SelectionEdit { Added = [new SelectionItem(@"\\wsl.localhost\Ubuntu", true, 0)] });
        Check("so a share selected is, like a drive, in no folder to paste into - not in \\\\wsl.localhost", share.Container is null && share.Count == 1);

        // ---- clearing keeps the focus ------------------------------------------------------
        spread.Apply(new SelectionEdit { Anchor = @"Q:\a\one.txt", Focus = @"Q:\a\two.txt" });
        spread.Clear(SelectionSource.Canvas);
        Check("clearing leaves nothing selected but the focus and the anchor where they were",
            spread.Count == 0 && spread.Focus == @"Q:\a\two.txt" && spread.Anchor == @"Q:\a\one.txt" && spread.Container is null);

        // ---- pruning ------------------------------------------------------------------------
        var pruned = new ItemSelection();
        pruned.Apply(new SelectionEdit
        {
            Added =
            [
                new SelectionItem(@"Q:\p\keep.txt", false, 1),
                new SelectionItem(@"Q:\p\gone.txt", false, 2),
                new SelectionItem(@"Q:\p\gone-folder", true, 0),
                new SelectionItem(@"Q:\elsewhere\gone.txt", false, 4)
            ]
        });
        var prunedChanges = 0;
        pruned.Changed += _ => prunedChanges++;
        var removed = pruned.RemoveMissingUnder(@"Q:\p", path => path.EndsWith("keep.txt", StringComparison.Ordinal));
        Check("a folder read again drops, in one change, only what was directly in it and is gone",
            removed == 2 && prunedChanges == 1 && pruned.Count == 2 && pruned.Contains(@"Q:\p\keep.txt") && pruned.Contains(@"Q:\elsewhere\gone.txt"));
        Check("and a folder with nothing selected in it costs nothing", pruned.RemoveMissingUnder(@"Q:\none", _ => false) == 0 && prunedChanges == 1);

        // ---- a file dialog that returns one item ------------------------------------------
        var single = new ItemSelection { MaxCount = 1 };
        single.Apply(new SelectionEdit
        {
            Added = [new SelectionItem(@"Q:\s\a.txt", false, 1), new SelectionItem(@"Q:\s\b.txt", false, 2), new SelectionItem(@"Q:\s\c.txt", false, 3)],
            Focus = @"Q:\s\b.txt"
        });
        Check("with room for one, a change that picks three keeps the focus alone",
            single.Count == 1 && single.Contains(@"Q:\s\b.txt") && single.TotalBytes == 2);
        single.Apply(new SelectionEdit { Added = [new SelectionItem(@"Q:\s\d.txt", false, 4)], Focus = @"Q:\s\d.txt" });
        Check("and the next pick replaces it", single.Count == 1 && single.Contains(@"Q:\s\d.txt"));

        var replaced = new ItemSelection();
        var replaceChanges = 0;
        replaced.Changed += _ => replaceChanges++;
        replaced.ReplaceSingle(@"Q:\r\x.txt", false, 5, SelectionSource.Navigation);
        replaced.ReplaceSingle(@"q:\R\X.TXT", false, 5, SelectionSource.Navigation);
        Check("one item alone is a navigation, and the same item again is no change",
            replaceChanges == 1 && replaced.LastRecordsNavigation && replaced.Anchor == @"Q:\r\x.txt");
    }

    /// <summary>
    /// Through the view model: a canvas's change of ten thousand items is one
    /// focus change, one watcher, and the list shows the folder they are in
    /// with exactly those rows lit.
    /// </summary>
    private static async Task ItemSelectionViewModelChecksAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerSelection", Guid.NewGuid().ToString("N"));
        var scratch = Path.Combine(root, "state");
        var folder = Path.Combine(root, "many");
        Directory.CreateDirectory(scratch);
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "inner"));
        for (var index = 0; index < 40; index++)
        {
            File.WriteAllText(Path.Combine(folder, $"file{index:D2}.txt"), new string('x', index + 1));
        }

        using var icons = new ShellIconService();
        try
        {
            using var tree = NewTree(scratch, icons);
            tree.PreferLightReveal = true;
            tree.IsCanvasShown = false;
            tree.FolderList.IsVisible = true;
            await tree.InitializeAsync(root);

            var focusChanges = 0;
            var navigations = 0;
            tree.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ViewAllViewModel.ActivePath))
                {
                    focusChanges++;
                    if (tree.FocusRecordsNavigation)
                    {
                        navigations++;
                    }
                }
            };

            var picked = Enumerable.Range(5, 20)
                .Select(index => new SelectionItem(Path.Combine(folder, $"file{index:D2}.txt"), false, index + 1))
                .Append(new SelectionItem(Path.Combine(folder, "inner"), true, 0))
                .ToArray();
            tree.Selection.Apply(new SelectionEdit
            {
                Clear = true,
                Container = folder,
                Added = picked,
                Anchor = picked[0].Path,
                Focus = picked[^1].Path,
                Source = SelectionSource.Canvas
            });

            var settled = await Until(() => tree.ActivePath.EndsWith("inner", StringComparison.OrdinalIgnoreCase)
                && string.Equals(tree.FolderList.FolderPath, folder, StringComparison.OrdinalIgnoreCase)
                && !tree.FolderList.IsLoading && tree.FolderList.SelectedRows().Count == 21, 5_000);
            Check("a canvas selection of 21 items in a folder: the focus is given its node once, as no navigation",
                settled && focusChanges == 1 && navigations == 0);
            Check("the list shows the folder they are all in, not the focused sub-folder", settled
                && string.Equals(tree.FolderList.FolderPath, folder, StringComparison.OrdinalIgnoreCase));
            Check("and lights exactly the 21 selected rows", tree.FolderList.SelectedRows().Select(row => row.FullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(picked.Select(item => item.Path)));
            Check("the status bar counts from the running totals",
                tree.StatusCountText.StartsWith("21 items selected", StringComparison.Ordinal)
                && tree.StatusCountText.Contains("1 folders", StringComparison.Ordinal));
            Check("paste and new folder go to that folder", string.Equals(tree.TargetDirectory, folder, StringComparison.OrdinalIgnoreCase));
            Check("the commands see all of it", tree.SelectedPaths.Count == 21 && ReferenceEquals(tree.SelectedPaths, tree.Selection.Paths));

            // A click on one item is going somewhere again.
            var one = await tree.SelectPathAsync(Path.Combine(folder, "file30.txt"));
            Check("selecting one path alone replaces the set and is a navigation",
                one is not null && tree.Selection.Count == 1 && navigations == 1
                && tree.SelectedNodes.Count == 1 && ReferenceEquals(tree.SelectedNodes[0], one));

            // Deleted from outside: a refresh of its folder lets it go.
            File.Delete(Path.Combine(folder, "file30.txt"));
            tree.Selection.Apply(new SelectionEdit
            {
                Added = [new SelectionItem(Path.Combine(folder, "file31.txt"), false, 32)],
                Source = SelectionSource.Canvas
            });
            await tree.RefreshPathAsync(folder);
            Check("a refresh of the folder drops what is gone from disk and keeps the rest",
                !tree.Selection.Contains(Path.Combine(folder, "file30.txt")) && tree.Selection.Contains(Path.Combine(folder, "file31.txt")));

            // What the list selects is the selection.
            tree.Selection.Apply(new SelectionEdit
            {
                Clear = true,
                Container = folder,
                Added = [new SelectionItem(Path.Combine(folder, "file01.txt"), false, 2), new SelectionItem(Path.Combine(folder, "file02.txt"), false, 3)],
                Anchor = Path.Combine(folder, "file01.txt"),
                Focus = Path.Combine(folder, "file02.txt"),
                Source = SelectionSource.List
            });
            Check("a change the list made is not handed back to it", tree.Selection.LastSource == SelectionSource.List
                && tree.SelectedPaths.Count == 2);
        }
        finally
        {
            TryDelete(root);
        }
    }
}
