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
/// even while the folder above it is not read; Delete counts as shown only
/// what the canvas has on it - not a hidden folder, nor files in a folder
/// off screen that hidden items or the Files layer took away; an added
/// rectangle's count includes what waits for a folder not read; and Ctrl+A
/// and Shift ranges take the matching sub-folders while a big tree's filter
/// is still judged.
/// </summary>
internal static partial class Program
{
    private static Task SelectionReview2Checks()
    {
        RunOnSta("selection review 2: the folder gone into, its parent not read", SelReview2PendingGoneIntoAsync);
        RunOnSta("selection review 2: what Delete counts as shown", SelReview2ShownCountAsync);
        RunOnSta("selection review 2: an added rectangle's count", SelReview2MarqueeCountAsync);
        RunOnSta("selection review 2: gestures while the filter is judged", SelReview2FilterSlicesAsync);
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

    // ---- J055: what Delete counts as shown -------------------------------------------------

    /// <summary>
    /// Q:\proj: src, a hidden .git with two files, a.txt, b.txt and a hidden
    /// desktop.ini; Q:\other: thirty files.  Selected with hidden items
    /// shown, then hidden items - or the Files layer - switched off: the
    /// canvas counts for Delete only what it still has on it, whether the
    /// folder is on screen or not, so Delete asks about the rest.
    /// </summary>
    private static async Task SelReview2ShownCountAsync()
    {
        Section("selection review 2: Delete counts as shown only what the canvas still has on it (J055)");
        var disk = new FakeDisk();
        disk.Folder(@"Q:\");
        disk.Folder(@"Q:\proj\src");
        disk.Folder(@"Q:\proj\.git").IsHidden = true;
        disk.AddFile(@"Q:\proj\.git", "HEAD", 10);
        disk.AddFile(@"Q:\proj\.git", "config", 10);
        disk.AddFile(@"Q:\proj", "a.txt", 10);
        disk.AddFile(@"Q:\proj", "b.txt", 10);
        disk.AddFile(@"Q:\proj", "desktop.ini", 10, hidden: true);
        disk.AddFiles(@"Q:\other", 30, "o");
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false, IncludeHidden = true };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive, "1 TB free")]);
        await LoadEverythingAsync(tree, _ => true);
        var (canvas, shared) = SelReview2Canvas(tree);
        var proj = tree.Find(@"Q:\proj")!;
        var git = tree.Find(@"Q:\proj\.git")!;
        var other = tree.Find(@"Q:\other")!;
        var view = new Rect(0, 0, ViewWidth, ViewHeight);
        bool OffScreen(NestedFolder folder) => canvas.ScreenRectOf(folder) is not { } rect || !rect.IntersectsWith(view);
        void Hidden(bool shown)
        {
            tree.IncludeHidden = shown;
            Render(canvas);
            Render(canvas);
        }

        try
        {
            // On screen: .git is still selected, and not shown.
            SelReview2GoTo(canvas, shared, proj);
            canvas.HandleKey(Key.A, ModifierKeys.Control);
            var all = shared.Count;
            Hidden(false);
            var shown = canvas.ShownOfSelection(shared.Version);
            Check($"Ctrl+A in a folder with hidden items shown, then hidden items off: {shown?.ToString() ?? "null"} of {all} counted shown (src, a.txt and b.txt are)",
                all == 5 && shown == 3);
            Hidden(true);
            Check("and nothing was let go: hidden items on again show all five selected", shared.Count == 5 && Picked(canvas).Count == 5);

            // Off screen: the folder's files were never caught up.
            SelReview2GoTo(canvas, shared, proj);
            canvas.HandleKey(Key.A, ModifierKeys.Control);
            canvas.FlyTo(other, 0.95, animated: false);
            Render(canvas);
            var away = OffScreen(proj);
            Hidden(false);
            shown = canvas.ShownOfSelection(shared.Version);
            Check($"the same with the folder off screen ({away}): {shown?.ToString() ?? "null"} of {shared.Count} counted shown",
                away && shared.Count == 5 && shown == 3);
            Hidden(true);

            // Inside a hidden folder: its files are not on the canvas either.
            SelReview2GoTo(canvas, shared, git);
            canvas.HandleKey(Key.A, ModifierKeys.Control);
            canvas.FlyTo(proj, 0.9, animated: false);
            Render(canvas);
            Hidden(false);
            shown = canvas.ShownOfSelection(shared.Version);
            Check($"files selected inside a folder that hidden items off took away: {shown?.ToString() ?? "null"} of {shared.Count} counted shown",
                shared.Count == 2 && shown == 0);
            Hidden(true);

            // The Files layer off, the folder off screen.
            SelReview2GoTo(canvas, shared, other);
            canvas.HandleKey(Key.A, ModifierKeys.Control);
            canvas.FlyTo(proj, 0.95, animated: false);
            Render(canvas);
            away = OffScreen(other);
            canvas.ShownLayers = CanvasLayer.All & ~CanvasLayer.Files;
            Render(canvas);
            shown = canvas.ShownOfSelection(shared.Version);
            canvas.ShownLayers = CanvasLayer.All;
            Render(canvas);
            Check($"thirty files selected in a folder off screen, then the Files layer off ({away}): {shown?.ToString() ?? "null"} of {shared.Count} counted shown",
                away && shared.Count == 30 && shown == 0);
            Check("and the Files layer on again shows all thirty selected", shared.Count == 30 && Picked(canvas).Count == 30);
        }
        finally
        {
            canvas.Tree = null;
        }
    }

    // ---- J117: an added rectangle's count ---------------------------------------------------

    /// <summary>
    /// Five files selected in a folder the canvas has not read - search
    /// results - and a rectangle drawn with Shift to add to them: the count
    /// by the pointer is what will be selected when it is let go, the five
    /// included.
    /// </summary>
    private static async Task SelReview2MarqueeCountAsync()
    {
        Section("selection review 2: an added rectangle counts what waits for a folder not read (J117)");
        var disk = new FakeDisk();
        disk.Folder(@"Q:\");
        disk.AddFiles(@"Q:\docs", 40, "d");
        disk.AddFiles(@"Q:\unread", 5, "u");
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive, "1 TB free")]);
        await LoadEverythingAsync(tree, folder => !folder.FullPath.EndsWith("unread", StringComparison.OrdinalIgnoreCase));
        var (canvas, shared) = SelReview2Canvas(tree);
        var docs = tree.Find(@"Q:\docs")!;
        try
        {
            shared.Apply(new SelectionEdit
            {
                Clear = true,
                Added = [.. Enumerable.Range(0, 5).Select(index => new SelectionItem($@"Q:\unread\u{index:D3}.txt", false, 100))],
                Source = SelectionSource.Navigation
            });
            canvas.LoadSelection(shared);
            canvas.FlyTo(docs, 0.9, animated: false);
            Render(canvas);
            var waiting = canvas.SelectionState.HasPending && canvas.SelectedCount == 0;
            var cell = canvas.ScreenRectOf(docs)!.Value;
            var pointer = canvas.Pointer;
            var end = At(cell, 0.6, 0.45);
            pointer.Down(MouseButton.Left, At(cell, MarginX, 0.2), ModifierKeys.Shift);
            pointer.Move(end);
            canvas.RunFrameForTests(TimeSpan.FromSeconds(4000));
            var drawing = canvas.Marquee?.HitCount ?? -1;
            pointer.Up(MouseButton.Left, end);
            Check($"the count while it is drawn is what is selected once it is let go ({drawing}, then {shared.Count}; five waiting: {waiting})",
                waiting && shared.Count > 5 && drawing == shared.Count);
        }
        finally
        {
            canvas.Tree = null;
        }
    }

    // ---- J137: gestures while the filter is judged -------------------------------------------

    private const int SelReview2Boxes = 140;
    private const int SelReview2BoxFiles = 2_000;

    /// <summary>
    /// Q:\a-bulk holds 140 folders of two thousand files - more names than a
    /// filter judges before it returns - and beside it Q:\match-1, Q:\match-2
    /// and Q:\zeta.  With the filter "match" still being judged a slice at a
    /// time, the two matches beside the bulk are not judged yet: Ctrl+A and a
    /// Shift range over them take them all the same, as they do once it is.
    /// </summary>
    private static async Task SelReview2FilterSlicesAsync()
    {
        Section("selection review 2: Ctrl+A and Shift ranges take the matching folders while the filter is still judged (J137)");
        var files = Enumerable.Range(0, SelReview2BoxFiles).Select(index => new NestedFile($"n{index:D4}.dat", false, index)).ToArray();
        var disk = new FakeDisk
        {
            Hook = (path, _) =>
            {
                var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 1)
                {
                    return new NestedListing(new[] { "a-bulk", "match-1", "match-2", "zeta" }.Select(name => new NestedEntry(name, false, false)).ToList(), 0, 0, false);
                }

                if (parts[1] != "a-bulk")
                {
                    return new NestedListing([], 0, 0, false);
                }

                return parts.Length == 2
                    ? new NestedListing(Enumerable.Range(0, SelReview2Boxes).Select(box => new NestedEntry($"b{box:D3}", false, false)).ToList(), 0, 0, false)
                    : new NestedListing([], files.Length, 0, false) { Files = files };
            }
        };

        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        var (canvas, shared) = SelReview2Canvas(tree);
        var drive = tree.Find(@"Q:\")!;
        var bulk = tree.Find(@"Q:\a-bulk")!;
        var zeta = tree.Find(@"Q:\zeta")!;
        string[] matching = [@"Q:\match-1", @"Q:\match-2"];
        try
        {
            SelReview2GoTo(canvas, shared, drive);
            canvas.SetFilter("match");
            var judging = canvas.HasPendingWork && tree.Find(@"Q:\match-1")!.FilterStamp != bulk.Children[0].FilterStamp;
            canvas.HandleKey(Key.A, ModifierKeys.Control);
            var all = Picked(canvas);
            Check($"Ctrl+A while the filter is still judged ({judging}) takes both matching folders ({string.Join(", ", all)})",
                judging && all.SetEquals(matching) && SelReview2Paths(shared).SetEquals(matching));

            var pointer = canvas.Pointer;
            pointer.Click(TitlePoint(canvas.ScreenRectOf(bulk)!.Value));
            pointer.Click(TitlePoint(canvas.ScreenRectOf(zeta)!.Value), ModifierKeys.Shift);
            var range = Picked(canvas);
            Check($"and so does a Shift range over them ({string.Join(", ", range)})",
                range.SetEquals(matching) && SelReview2Paths(shared).SetEquals(matching));

            await CanvasMiscJudgedAsync(canvas);
            canvas.HandleKey(Key.A, ModifierKeys.Control);
            Check("the same as Ctrl+A takes once the whole tree is judged", Picked(canvas).SetEquals(matching));
        }
        finally
        {
            canvas.SetFilter(null);
            canvas.Tree = null;
        }
    }
}
