using System.IO;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// The folder list: the current directory as a plain list beside the canvas.
/// What matters here is that typing finds the thing you meant, that walking up
/// and back behaves like a file manager, and that reading a row never quietly
/// drives the canvas somewhere.
/// </summary>
internal static partial class Program
{
    private static async Task FolderList()
    {
        Section("folder list");

        // ---- what typing finds -------------------------------------------
        Check("an empty query matches everything",
            FolderListMatch.Score("AssetRipper", string.Empty) == 0);
        Check("a prefix is the best answer",
            FolderListMatch.Score("AssetRipper", "asset") == 0);
        Check("a prefix beats the same letters later on",
            FolderListMatch.Score("AssetRipper", "asset") < FolderListMatch.Score("MyAssetBundle", "asset"));
        Check("earlier in the name beats later in it",
            FolderListMatch.Score("xAssetY", "asset") < FolderListMatch.Score("xxxxxxxxAssetY", "asset"));
        Check("scattered letters still match, but come last",
            FolderListMatch.Score("AssetRipper", "asrip") > FolderListMatch.Score("MyAssetBundle", "asset")
            && FolderListMatch.Score("AssetRipper", "asrip") != FolderListMatch.NoMatch);
        Check("letters out of order do not match",
            FolderListMatch.Score("AssetRipper", "ripas") == FolderListMatch.NoMatch);
        Check("matching ignores case",
            FolderListMatch.Score("AssetRipper", "ASSET") == 0);

        // ---- a real folder, read through the view model --------------------
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerList", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "alpha"));
            Directory.CreateDirectory(Path.Combine(root, "beta"));
            Directory.CreateDirectory(Path.Combine(root, "alpha", "inner"));
            File.WriteAllText(Path.Combine(root, "readme.txt"), "hello");
            File.WriteAllText(Path.Combine(root, "notes.md"), "notes");

            var files = new ViewAllFileSystemService();
            using var icons = new ShellIconService();
            var options = new ViewAllGraphOptions();
            var opened = new List<(string Path, bool Open)>();

            var list = new FolderListViewModel(
                (path, cancellation) => files.GetChildrenAsync(path, options, cancellation),
                (path, open) =>
                {
                    opened.Add((path, open));
                    return Task.CompletedTask;
                },
                _ => false,
                icons)
            {
                IsVisible = true
            };

            await list.NavigateAsync(root);
            Check("the folder is read", list.Items.Count == 4);
            Check("it counts what it read", list.CountText == "4");
            Check("folders and files are all there",
                list.Items.Any(item => item.DisplayName == "alpha")
                && list.Items.Any(item => item.DisplayName == "readme.txt"));

            // ---- typing narrows it -----------------------------------------
            list.Filter = "read";
            Check("the filter narrows the list", list.Items.Count == 1);
            Check("and finds the right one", list.Items[0].DisplayName == "readme.txt");
            Check("a match leaves no empty message",
                list.Items.Count == 1 && list.EmptyText.Length == 0);

            list.Filter = "zzzz";
            Check("nothing matching is explained rather than blank",
                list.Items.Count == 0 && list.EmptyText.Contains("zzzz"));

            list.Filter = string.Empty;
            Check("clearing the filter brings the folder back", list.Items.Count == 4);

            // ---- reading is not navigating ----------------------------------
            var beforeHighlight = opened.Count;
            list.Selected = list.Items.First(item => item.DisplayName == "beta");
            Check("highlighting a row does not touch the canvas", opened.Count == beforeHighlight);
            list.Highlight(list.Items.First(item => item.DisplayName == "alpha"));
            Check("nor does highlighting it explicitly", opened.Count == beforeHighlight);
            Check("but the highlight moved", list.Selected?.DisplayName == "alpha");

            // ---- walking the tree -------------------------------------------
            Check("there is somewhere above a temp folder", list.CanGoUp);
            Check("nowhere to go back to yet", !list.CanGoBack);

            await list.NavigateAsync(Path.Combine(root, "alpha"));
            Check("going in reads the new folder", list.Items.Count == 1);
            Check("the title follows", list.Title == "alpha");
            Check("and back is now possible", list.CanGoBack);

            list.Filter = "inner";
            await list.NavigateAsync(Path.Combine(root, "alpha", "inner"));
            Check("a filter does not follow into the next folder", list.Filter.Length == 0);

            var beforeUp = opened.Count;
            await list.UpCommand.ExecuteAsync();
            Check("up goes to the parent", list.Title == "alpha");
            Check("and takes the canvas with it", opened.Count > beforeUp);

            await list.BackCommand.ExecuteAsync();
            Check("back retraces where it came from",
                string.Equals(list.FolderPath, Path.Combine(root, "alpha", "inner"), StringComparison.OrdinalIgnoreCase));

            // ---- the drive root has nothing above it -------------------------
            var drive = Path.GetPathRoot(root)!;
            await list.NavigateAsync(drive);
            Check("a drive root is the top", !list.CanGoUp);

            // ---- a folder that is not there ----------------------------------
            await list.NavigateAsync(Path.Combine(root, "does-not-exist"));
            Check("a missing folder is explained, not thrown",
                list.Items.Count == 0 && list.EmptyText.Length > 0);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// Runs a command and waits for the work it starts.  The commands are
    /// fire-and-forget by design - a button click has nothing to await - so the
    /// checks need a way to let the read finish.
    /// </summary>
    private static async Task ExecuteAsync(this System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await Task.Delay(5);
            if (command.CanExecute(null))
            {
                return;
            }
        }
    }
}
