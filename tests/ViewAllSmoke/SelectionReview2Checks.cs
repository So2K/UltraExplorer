using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// What the second review found in the selection, and what was done about
/// it: an item added inside the folder gone into lets go of that folder
/// even while the folder above it is not read.
/// </summary>
internal static partial class Program
{
    private static Task SelectionReview2Checks()
    {
        RunOnSta("selection review 2: the folder gone into, its parent not read", SelReview2PendingGoneIntoAsync);
        return Task.CompletedTask;
    }

    /// <summary>A canvas over <paramref name="tree"/>, with the window's side as a pane has it: each edit applied to one shared selection.</summary>
    private static (NestedCanvas Canvas, ItemSelection Shared) SelReview2Canvas(NestedTree tree)
    {
        var canvas = new NestedCanvas { Tree = tree, DpiOverride = new DpiScale(1, 1) };
        canvas.Measure(new Size(ViewWidth, ViewHeight));
        canvas.Arrange(new Rect(0, 0, ViewWidth, ViewHeight));
        canvas.UpdateLayout();
        var shared = new ItemSelection();
        canvas.SelectionCommitted += edit =>
        {
            shared.Apply(edit);
            if (canvas.SelectedCount != shared.Count)
            {
                canvas.LoadSelection(shared);
            }
            else
            {
                canvas.AcknowledgeSelection(shared.Version);
            }
        };

        return (canvas, shared);
    }

    /// <summary>Going somewhere, as the address bar does: the folder selected, and the canvas there.</summary>
    private static void SelReview2GoTo(NestedCanvas canvas, ItemSelection shared, NestedFolder folder)
    {
        shared.ReplaceSingle(folder.FullPath, true, 0, SelectionSource.Navigation);
        canvas.LoadSelection(shared);
        canvas.FlyTo(folder, 0.9, animated: false);
        Render(canvas);
    }

    private static HashSet<string> SelReview2Paths(ItemSelection shared) => shared.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase);

    // ---- J054: the folder gone into, waiting for its parent ---------------------------------

    /// <summary>
    /// A folder gone into by its path while the folder above it is not read
    /// - one too big to have been listed yet, or behind a link - is selected
    /// in the window and waits on the canvas for that parent.  Ctrl+clicking
    /// two files in it has to let go of it, as it does when the parent is
    /// read: kept, Delete recycled the whole folder without a word.  A real
    /// folder on disk, as the path is found there without its parent read.
    /// </summary>
    private static async Task SelReview2PendingGoneIntoAsync()
    {
        Section("selection review 2: adding inside the folder gone into lets go of it while its parent is not read (J054)");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerSelReview2", Guid.NewGuid().ToString("N"));
        var parentPath = Path.Combine(root, "big");
        var projPath = Path.Combine(parentPath, "proj");
        Directory.CreateDirectory(projPath);
        File.WriteAllText(Path.Combine(parentPath, "other.txt"), "x");
        for (var index = 0; index < 10; index++)
        {
            File.WriteAllText(Path.Combine(projPath, $"f{index:D2}.txt"), "x");
        }

        try
        {
            using var tree = new NestedTree { IsReadingOnDemand = false };
            tree.SetRoots([new NestedRoot(root, "T", NestedFolderKind.Drive)]);
            await tree.LoadAsync(tree.Find(root)!);
            if (await tree.MaterializePathAsync(projPath) is not { } proj || tree.Find(parentPath) is not { } parent)
            {
                Check("the folder is found by its path", false);
                return;
            }

            await tree.LoadAsync(proj);
            var (canvas, shared) = SelReview2Canvas(tree);
            try
            {
                SelReview2GoTo(canvas, shared, proj);
                var waiting = !parent.IsLoaded && canvas.SelectionState.HasPending && canvas.SelectedCount == 0;
                var cell = canvas.ScreenRectOf(proj)!.Value;
                canvas.Pointer.Click(TilePoint(cell, proj, 2), ModifierKeys.Control);
                canvas.Pointer.Click(TilePoint(cell, proj, 4), ModifierKeys.Control);
                string[] two = [proj.PathOf(proj.Files[2]), proj.PathOf(proj.Files[4])];
                Check($"Ctrl+clicking two files in the folder gone into, its parent not read ({waiting}), selects the two files and not the folder ({string.Join(", ", shared.Paths.Select(Path.GetFileName))})",
                    waiting && SelReview2Paths(shared).SetEquals(two) && !shared.Contains(proj.FullPath) && Picked(canvas).SetEquals(two));
                var shown = canvas.ShownOfSelection(shared.Version);
                Check($"and the canvas holds nothing more for Delete to act on ({shown?.ToString() ?? "null"} of {shared.Count} shown)",
                    shown == 2 && shared.Count == 2 && !canvas.SelectionState.HasPending);
            }
            finally
            {
                canvas.Tree = null;
            }
        }
        finally
        {
            TryDelete(root);
        }
    }
}
