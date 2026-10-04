using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// The second review's issues with the folder list (J005, J015, J027, J088,
/// J114 and J115 of coordination/review/20261004-claude-review2-full.md),
/// each replayed as it went wrong: the list's folder deleted elsewhere
/// selected the folder above on the canvas, for the next Delete to recycle;
/// rows past the three hundredth never got an icon; the folder before stayed
/// under the next one's name while that was read, its rows answering Enter
/// and a double-click; every refill asked the canvas about every row for a
/// value nothing showed; a filter typed in a folder that could not be read
/// said it was empty; and a row clicked while another row's reveal was under
/// way was dropped.
/// </summary>
internal static partial class Program
{
    private static Task FolderListRound2Checks()
    {
        Section("folder list: second review");
        RunOnSta("list leaves a deleted folder without the canvas", ListGoneLeavesCanvasAsync);
        RunOnSta("list leaves a folder found gone as it is read", ListGoneOnReadAsync);
        RunOnSta("list leaves a deleted folder, end to end", ListGoneEndToEndAsync);
        return Task.CompletedTask;
    }

    /// <summary>
    /// J005: the list's folder deleted outside the app took the list to the
    /// folder above - and the canvas with it, selecting that folder, so the
    /// next Delete recycled the whole parent.  Hidden, the list did the same.
    /// Only the list moves; what the canvas selects is the tree's to prune.
    /// </summary>
    private static async Task ListGoneLeavesCanvasAsync()
    {
        Section("folder list round 2: its folder deleted, the canvas left alone");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerListGone2", Guid.NewGuid().ToString("N"));
        var doomed = Path.Combine(root, "doomed");
        var hidden = Path.Combine(root, "hidden");
        Directory.CreateDirectory(doomed);
        Directory.CreateDirectory(hidden);
        try
        {
            using var icons = new ShellIconService();
            var activated = new List<string>();
            var list = new FolderListViewModel(
                (path, _, _) => Directory.Exists(path)
                    ? Task.FromResult(ReviewSnapshot(path, false, "a.txt", "sub"))
                    : Task.FromException<ViewAllDirectorySnapshot>(new DirectoryNotFoundException(path)),
                (path, _) => { activated.Add(path); return Task.CompletedTask; },
                _ => false,
                icons) { IsVisible = true };

            await list.NavigateAsync(doomed);
            Check("the list's own step into a folder takes the canvas there", activated.Count == 1 && ViewAllPath.Equals(activated[0], doomed));
            activated.Clear();
            Directory.Delete(doomed);
            list.OnFolderChanged(ReviewChange(doomed, ChangeKinds.Gone));
            var up = await LiveWait(() => ViewAllPath.Equals(list.FolderPath, root) && !list.IsLoading, 2_000);
            await Task.Delay(50);
            Check($"its folder deleted, the list goes to the folder above ({up} ms, {list.Items.Count} rows)", up >= 0 && list.Items.Count == 2);
            Check($"and asks the canvas to go nowhere - it would select the folder above for Delete ({activated.Count} asked: {string.Join(", ", activated.Select(Path.GetFileName))})",
                activated.Count == 0);

            await list.NavigateAsync(hidden);
            list.IsVisible = false;
            activated.Clear();
            Directory.Delete(hidden);
            list.OnFolderChanged(ReviewChange(hidden, ChangeKinds.Gone));
            up = await LiveWait(() => ViewAllPath.Equals(list.FolderPath, root), 2_000);
            await Task.Delay(50);
            Check($"hidden, it goes up too ({up} ms), and the canvas is not asked either ({activated.Count} asked)",
                up >= 0 && activated.Count == 0);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// J005: a folder already gone when the list is pointed at it - the
    /// canvas still holding its node - failed its read and the list stayed
    /// on "This folder could not be read." for good: the change that said
    /// it went had come before the list was there to hear it.  It goes to
    /// the folder above, as a folder deleted under it does, and only it.
    /// </summary>
    private static async Task ListGoneOnReadAsync()
    {
        Section("folder list round 2: a folder found gone as it is read");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerListGoneRead", Guid.NewGuid().ToString("N"));
        var gone = Path.Combine(root, "gone");
        var other = Path.Combine(root, "other");
        Directory.CreateDirectory(other);
        try
        {
            using var icons = new ShellIconService();
            var activated = new List<string>();
            var list = new FolderListViewModel(
                (path, _, _) => Directory.Exists(path)
                    ? Task.FromResult(ReviewSnapshot(path, false, "a.txt", "sub"))
                    : Task.FromException<ViewAllDirectorySnapshot>(new DirectoryNotFoundException(path)),
                (path, _) => { activated.Add(path); return Task.CompletedTask; },
                _ => false,
                icons) { IsVisible = true };

            await list.NavigateAsync(other);
            activated.Clear();
            list.SetTarget(gone, null);
            var up = await LiveWait(() => ViewAllPath.Equals(list.FolderPath, root) && !list.IsLoading && list.Items.Count == 2, 3_000);
            Check($"pointed at a folder that is gone, the list goes to the folder above and reads it ({up} ms, in {Path.GetFileName(list.FolderPath)}, {list.EmptyText})",
                up >= 0 && list.EmptyText.Length == 0);
            Check($"without asking the canvas to go there ({activated.Count} asked)", activated.Count == 0);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// J005 end to end: the canvas has a file selected in the list's folder,
    /// and the folder is deleted by another program.  The list goes up, and
    /// the folder above it must not end up selected.
    /// </summary>
    private static async Task ListGoneEndToEndAsync()
    {
        Section("folder list round 2: its folder deleted elsewhere, end to end");
        var baseDirectory = Path.Combine(Path.GetTempPath(), "UltraExplorerListGoneLive", Guid.NewGuid().ToString("N"));
        var parent = Path.Combine(baseDirectory, "J", "parent");
        var doomed = Path.Combine(parent, "doomed");
        var inside = Path.Combine(doomed, "a.txt");
        Directory.CreateDirectory(doomed);
        File.WriteAllText(inside, "a");
        File.WriteAllText(Path.Combine(parent, "keep.txt"), "k");
        try
        {
            using var icons = new ShellIconService();
            var marks = new FolderMarkService(Path.Combine(baseDirectory, "marks.json"));
            using var hub = new ChangeHub(TimeProvider.System);
            using var tree = new ViewAllViewModel(marks, icons, Path.Combine(baseDirectory, "tree.json")) { PreferLightReveal = true };
            hub.Driver.Fallback = DispatcherFrameDriver.ForCurrentThread((ref FrameBudget budget) => hub.Drain(ref budget, tree), () => hub.HasWork);
            tree.Changes = hub;
            tree.IsCanvasShown = false;
            await tree.InitializeAsync(parent);
            tree.FolderList.IsVisible = true;

            await tree.SelectPathAsync(inside);
            var there = await LiveWait(() => ViewAllPath.Equals(tree.FolderList.FolderPath, doomed) && !tree.FolderList.IsLoading, 3_000);
            Check($"a file selected, the list shows its folder ({there} ms)", there >= 0);

            Directory.Delete(doomed, recursive: true);
            var up = await LiveWait(() => ViewAllPath.Equals(tree.FolderList.FolderPath, parent), 3_000);
            await Task.Delay(600);
            Check($"the folder deleted elsewhere, the list goes up ({up} ms, in {Path.GetFileName(tree.FolderList.FolderPath)}, {tree.FolderList.EmptyText})", up >= 0);
            Check($"and the folder above is not selected for the next Delete ({tree.Selection.Count} selected: {string.Join(", ", tree.Selection.Paths.Select(Path.GetFileName))})",
                !tree.Selection.Contains(parent));
        }
        finally
        {
            TryDelete(baseDirectory);
        }
    }
}
