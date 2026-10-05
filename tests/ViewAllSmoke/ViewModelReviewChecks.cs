using System.Diagnostics;
using System.IO;
using System.Windows;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// What the review of the canvas's view model found, and what was done about
/// it: a focus in a share that does not answer is not taken for gone, nor
/// asked about on the interface thread; an editor's safe-save leaves the
/// mark and the selection on the document; a folder opened from the list
/// that is still being read when the user moves on stays where it is; a
/// reveal through a folder read again meanwhile hangs its node from the
/// folder the graph has now; F5 and the hidden items toggle leave no node
/// the graph let go of as the focus or on the canvas; a navigation in one
/// pane of a split view does not cancel the other pane's; a location added
/// from the navigation pane lands in the pane it was asked in and ends
/// quietly in a closed window; and the lines out of a large folder are let
/// go of in linear time.
/// </summary>
internal static partial class Program
{
    private static async Task ViewModelReviewChecks()
    {
        var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "UltraExplorerViewModelReview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await OnDispatcher(() => ReviewUnreachableFocusAsync(root));
            await OnDispatcher(() => ReviewSafeSaveAsync(root));
            await OnDispatcher(() => ReviewListOpenAsync(root));
            await OnDispatcher(() => ReviewRevealRaceAsync(root));
            await OnDispatcher(() => ReviewRebuiltNodesAsync(root));
            await OnDispatcher(() => ReviewPaneNavigationsAsync(root));
            await OnDispatcher(() => ReviewAddedRootAsync(root));
            await OnDispatcher(ReviewEdgeRelease);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- a focus that cannot be reached ----------------------------------------------

    private static async Task ReviewUnreachableFocusAsync(string root)
    {
        Section("view model review: a focus that cannot be reached");
        var folder = Path.Combine(root, "focus");
        var scratch = Path.Combine(root, "focus-state");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(scratch);

        using var icons = new ShellIconService();
        using var tree = NewTree(scratch, icons);
        tree.PreferLightReveal = true;
        tree.IsCanvasShown = false;
        await tree.InitializeAsync(folder);
        var messages = new List<string>();
        tree.MessageRequested += (message, _) => messages.Add(message);

        // A share the server says it does not have fails the way one that has
        // stopped answering does: neither there nor "not found".
        var unreachable = $@"\\localhost\ultraexplorer-unreachable-{Guid.NewGuid():N}\report.docx";
        tree.Selection.ReplaceSingle(unreachable, false, 0, SelectionSource.Canvas);
        await WaitUntil(() => messages.Count > 0, 10_000);
        await Task.Delay(300);
        Check("an item in a share that does not answer is not taken for gone: it stays selected",
            messages.Count > 0 && tree.Selection.Count == 1 && tree.Selection.Contains(unreachable));

        var gone = Path.Combine(folder, "gone.txt");
        tree.Selection.ReplaceSingle(gone, false, 0, SelectionSource.Canvas);
        await WaitUntil(() => !tree.Selection.Contains(gone), 3_000);
        Check("an item that is not on disk is let go of, as ever", !tree.Selection.Contains(gone));
    }

    // ---- an editor's safe-save ---------------------------------------------------------

    private static async Task ReviewSafeSaveAsync(string root)
    {
        Section("view model review: an editor's safe-save");
        var folder = Path.Combine(root, "safe-save");
        var scratch = Path.Combine(root, "safe-save-state");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(scratch);
        var report = Path.Combine(folder, "Report.docx");
        var backup = Path.Combine(folder, "~WRL0001.tmp");
        File.WriteAllText(report, "saved again");
        File.WriteAllText(backup, "as it was");

        using var icons = new ShellIconService();
        var marks = new FolderMarkService(Path.Combine(scratch, "marks.json"));
        using var tree = new ViewAllViewModel(marks, icons, Path.Combine(scratch, "tree.json"));
        tree.PreferLightReveal = true;
        tree.IsCanvasShown = false;
        await tree.InitializeAsync(folder);
        var sink = (IChangeSink)tree;
        void Renamed(params RenamePair[] renames) =>
            sink.FolderChanged(ChangeConsumer.Nested, folder, new FolderChange(folder, ChangeKinds.Structural, Stopwatch.GetTimestamp(), renames, default));

        marks.SetAccent(report, "#EF5A68");
        marks.SetNote(report, "the one to send");
        tree.Selection.ReplaceSingle(report, false, 11, SelectionSource.Canvas);

        // Word: the document to a backup name, then its new copy to the document's name.
        Renamed(new RenamePair("Report.docx", "~WRL0001.tmp"), new RenamePair("~WRD0000.tmp", "Report.docx"));
        await Task.Delay(200);
        Check("an editor's safe-save leaves the colour and the note on the document, not on its backup",
            marks.Get(report).AccentHex == "#EF5A68" && marks.Get(report).Note == "the one to send" && marks.Get(backup).IsEmpty);
        Check("and the document selected", tree.Selection.Count == 1 && tree.Selection.Contains(report));

        // A rename on its own still takes both along.
        var final = Path.Combine(folder, "Final.docx");
        marks.SetAccent(report, "#EF5A68");
        tree.Selection.ReplaceSingle(report, false, 11, SelectionSource.Canvas);
        File.Move(report, final);
        Renamed(new RenamePair("Report.docx", "Final.docx"));
        await Task.Delay(200);
        Check("a rename on its own still takes the mark and the selection along",
            marks.Get(final).AccentHex == "#EF5A68" && marks.Get(report).IsEmpty && tree.Selection.Contains(final) && !tree.Selection.Contains(report));

        // A change of case made through a temporary name: the new spelling.
        var cased = Path.Combine(folder, "final.docx");
        File.Move(final, cased);
        Renamed(new RenamePair("Final.docx", "~case.tmp"), new RenamePair("~case.tmp", "final.docx"));
        await Task.Delay(200);
        Check("a change of case made through a temporary name keeps the mark, and the selection takes the new spelling",
            marks.Get(cased).AccentHex == "#EF5A68" && tree.Selection.Count == 1 && tree.Selection.Paths[0] == cased);

        // A batch renamer's two passes: 2 and 3 through temporary names to 1 and 2.
        string In(string name) => Path.Combine(folder, name);
        void Moved(params RenamePair[] renames)
        {
            foreach (var pair in renames)
            {
                File.Move(In(pair.OldName), In(pair.NewName));
            }

            Renamed(renames);
        }

        File.WriteAllText(In("2.txt"), "two");
        File.WriteAllText(In("3.txt"), "three");
        marks.SetAccent(In("2.txt"), "#3FA34D");
        marks.SetAccent(In("3.txt"), "#4A7BD0");
        tree.Selection.Apply(new SelectionEdit
        {
            Clear = true,
            Added = [new SelectionItem(In("2.txt"), false, 3), new SelectionItem(In("3.txt"), false, 5)],
            Source = SelectionSource.Canvas,
        });
        Moved(new RenamePair("2.txt", "t2.tmp"), new RenamePair("3.txt", "t3.tmp"), new RenamePair("t2.tmp", "1.txt"), new RenamePair("t3.tmp", "2.txt"));
        await Task.Delay(200);
        Check("a batch rename's two passes through temporary names take each mark along with its file",
            marks.Get(In("1.txt")).AccentHex == "#3FA34D" && marks.Get(In("2.txt")).AccentHex == "#4A7BD0" && marks.Get(In("3.txt")).IsEmpty);
        Check("and the selection with them",
            tree.Selection.Count == 2 && tree.Selection.Contains(In("1.txt")) && tree.Selection.Contains(In("2.txt")));

        // A shift: 1, 2 and 3 down to 0, 1 and 2.
        File.WriteAllText(In("3.txt"), "three");
        marks.SetAccent(In("3.txt"), "#D9A13B");
        Moved(new RenamePair("1.txt", "0.txt"), new RenamePair("2.txt", "1.txt"), new RenamePair("3.txt", "2.txt"));
        await Task.Delay(200);
        Check("a shift takes each mark along with its file",
            marks.Get(In("0.txt")).AccentHex == "#3FA34D" && marks.Get(In("1.txt")).AccentHex == "#4A7BD0"
            && marks.Get(In("2.txt")).AccentHex == "#D9A13B" && marks.Get(In("3.txt")).IsEmpty);
        Check("and the selection with them",
            tree.Selection.Count == 2 && tree.Selection.Contains(In("0.txt")) && tree.Selection.Contains(In("1.txt")));

        // A swap through a temporary name.
        tree.Selection.ReplaceSingle(In("0.txt"), false, 3, SelectionSource.Canvas);
        Moved(new RenamePair("0.txt", "swap.tmp"), new RenamePair("1.txt", "0.txt"), new RenamePair("swap.tmp", "1.txt"));
        await Task.Delay(200);
        Check("a swap through a temporary name keeps each mark on its file",
            marks.Get(In("1.txt")).AccentHex == "#3FA34D" && marks.Get(In("0.txt")).AccentHex == "#4A7BD0" && marks.Get(In("swap.tmp")).IsEmpty);
        Check("and the selection on the file it was on", tree.Selection.Count == 1 && tree.Selection.Contains(In("1.txt")));
    }

    // ---- a folder opened from the list ------------------------------------------------

    private static async Task ReviewListOpenAsync(string root)
    {
        Section("view model review: a folder opened from the list");
        var folder = Path.Combine(root, "list-open");
        var scratch = Path.Combine(root, "list-open-state");
        var slow = Path.Combine(folder, "slow");
        var picked = Path.Combine(folder, "other", "picked");
        var plain = Path.Combine(folder, "plain");
        Directory.CreateDirectory(Path.Combine(slow, "inside"));
        Directory.CreateDirectory(picked);
        Directory.CreateDirectory(Path.Combine(plain, "inside"));
        Directory.CreateDirectory(scratch);

        using var icons = new ShellIconService();
        using var tree = NewTree(scratch, icons);
        tree.PreferLightReveal = true;
        tree.IsCanvasShown = false;
        await tree.InitializeAsync(folder);
        var slowNode = await tree.MaterializeAsync(slow);
        var pickedNode = await tree.MaterializeAsync(picked);
        var plainNode = await tree.MaterializeAsync(plain);
        if (slowNode is null || pickedNode is null || plainNode is null)
        {
            Check("the folders to open are on the graph", false);
            return;
        }

        // Double-clicked in the list; while it is read, something else is picked.
        tree.FolderList.ActivateCommand.Execute(new FolderListItem(slowNode.Entry));
        var stillReading = !slowNode.AreChildrenLoaded;
        tree.Selection.ReplaceSingle(picked, true, 0, SelectionSource.Canvas);
        await WaitUntil(() => slowNode.AreChildrenLoaded, 3_000);
        await Task.Delay(100);
        Check("a folder opened from the list and picked away from while it is read leaves the focus and the list on what was picked",
            stillReading && slowNode.AreChildrenLoaded
            && ViewAllPath.Equals(tree.ActivePath, picked)
            && ViewAllPath.Equals(tree.FolderList.FolderPath, picked));

        // Left alone, it is gone into as ever.
        tree.FolderList.ActivateCommand.Execute(new FolderListItem(plainNode.Entry));
        await WaitUntil(() => ViewAllPath.Equals(tree.FolderList.FolderPath, plain), 3_000);
        Check("left alone, a folder opened from the list is gone into, the focus with it",
            ViewAllPath.Equals(tree.FolderList.FolderPath, plain) && ViewAllPath.Equals(tree.ActivePath, plain));
    }

    // ---- a reveal through a folder read again meanwhile --------------------------------

    private static async Task ReviewRevealRaceAsync(string root)
    {
        Section("view model review: a reveal through a folder read again meanwhile");
        var folder = Path.Combine(root, "race");
        var scratch = Path.Combine(root, "race-state");
        var busy = Path.Combine(folder, "busy");
        var middle = Path.Combine(busy, "middle");
        var target = Path.Combine(middle, "target");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(scratch);

        using var icons = new ShellIconService();
        using var tree = NewTree(scratch, icons);
        tree.IsCanvasShown = false;
        await tree.InitializeAsync(busy);
        if (!tree.TryGetNode(busy, out var busyNode))
        {
            Check("the folder is on the tree", false);
            return;
        }

        await tree.ExpandAsync(busyNode);

        // The reveal waits for "middle" to be read; F5 on "busy" builds "middle" anew meanwhile.
        var reveal = tree.RevealAsync(target, focus: false);
        var refresh = tree.RefreshAsync(busyNode);
        await reveal;
        await refresh;
        var found = tree.TryGetNode(middle, out var middleNode) & tree.TryGetNode(target, out var targetNode);
        if (found)
        {
            await tree.ExpandAsync(middleNode);
        }

        Check("a folder revealed through one read again meanwhile hangs from the node the graph has now, and is shown in it",
            found && ReferenceEquals(targetNode.Parent, middleNode) && middleNode.Children.Contains(targetNode));
    }

    // ---- the focus after a folder is built anew ---------------------------------------

    private static async Task ReviewRebuiltNodesAsync(string root)
    {
        Section("view model review: the focus after a folder is built anew");
        var folder = Path.Combine(root, "rebuilt");
        var scratch = Path.Combine(root, "rebuilt-state");
        var shown = Path.Combine(folder, "shown.txt");
        var secret = Path.Combine(folder, "secret.txt");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(scratch);
        File.WriteAllText(shown, "shown");
        File.WriteAllText(secret, "secret");
        File.SetAttributes(secret, FileAttributes.Hidden);

        using var icons = new ShellIconService();
        using var tree = NewTree(scratch, icons);
        tree.RestoreShowHiddenItems(true);
        await tree.InitializeAsync(folder);
        bool Live(ViewAllNodeViewModel? node) => node is not null && tree.TryGetNode(node.FullPath, out var live) && ReferenceEquals(live, node);

        await tree.RevealPathAsync(shown, focus: false);
        if (!tree.TryGetNode(folder, out var folderNode))
        {
            Check("the folder is on the tree", false);
            return;
        }

        // F5 on the folder of the file selected.
        await tree.RefreshAsync(folderNode);
        await Settle(tree);
        Check("after F5 the focus is the node the graph has now for the file, not the one it let go of",
            ViewAllPath.Equals(tree.ActivePath, shown) && Live(tree.ActiveNode) && tree.SelectedNodes.All(Live));
        Check("and nothing the graph let go of is on the canvas", tree.RenderNodes.All(Live));

        // Hidden items hidden: every open folder is read again.
        await tree.SetShowHiddenItemsAsync(false);
        await Settle(tree);
        Check("after hidden items are hidden the focus is the file's new node, and nothing let go of is on the canvas",
            ViewAllPath.Equals(tree.ActivePath, shown) && Live(tree.ActiveNode) && tree.SelectedNodes.All(Live) && tree.RenderNodes.All(Live));

        // A hidden file selected when hidden items are hidden: no tile is left where it was.
        await tree.SetShowHiddenItemsAsync(true);
        await tree.RevealPathAsync(secret, focus: false);
        await tree.SetShowHiddenItemsAsync(false);
        await Settle(tree);
        Check("a hidden file selected when hidden items are hidden leaves no tile behind",
            !tree.TryGetNode(secret, out _) && tree.RenderNodes.All(Live) && tree.SelectedNodes.All(Live));
    }

    // ---- navigations in the panes of a split view -------------------------------------

    private static async Task ReviewPaneNavigationsAsync(string root)
    {
        Section("view model review: navigations in the panes of a split view");
        var folder = Path.Combine(root, "panes");
        var scratch = Path.Combine(root, "panes-state");
        var left = Path.Combine(folder, "left", "deep");
        var right = Path.Combine(folder, "right");
        var one = Path.Combine(folder, "again", "one");
        var two = Path.Combine(folder, "again", "two");
        foreach (var path in new[] { left, right, one, two, scratch })
        {
            Directory.CreateDirectory(path);
        }

        using var icons = new ShellIconService();
        using var tree = NewTree(scratch, icons);
        tree.PreferLightReveal = true;
        tree.IsCanvasShown = false;
        var first = new object();
        var second = new object();
        tree.SelectionHolder = first;
        await tree.InitializeAsync(folder);
        var landings = new List<NavigationLanding>();
        tree.NavigationLandedAway += landings.Add;

        // Back in the first pane, still reading its way down when the second
        // pane is clicked and sent elsewhere.
        var back = tree.RevealAsync(left, records: false);
        tree.SelectionHolder = second;
        await tree.RevealAsync(right);
        var landed = await back;
        Check("a navigation in one pane does not cancel the other pane's: that one lands in the pane it was asked in",
            landed.Superseded && landings.Count == 1 && ReferenceEquals(landings[0].Holder, first)
            && ViewAllPath.Equals(landings[0].Node.FullPath, left) && !landings[0].Records);
        Check("and the pane being worked with keeps its own",
            ViewAllPath.Equals(tree.ActivePath, right) && tree.Selection.Count == 1 && tree.Selection.Contains(right));

        // Two in the same pane: the later one wins, as ever.
        landings.Clear();
        var earlier = tree.RevealAsync(one);
        var later = tree.RevealAsync(two);
        var outcomes = await Task.WhenAll(earlier, later);
        Check("two in the same pane: the earlier gives way to the later, and lands nowhere",
            outcomes[0].Superseded && !outcomes[1].Superseded && landings.Count == 0 && ViewAllPath.Equals(tree.ActivePath, two));

        // A ticket from one pane is not made stale by the other's.
        tree.SelectionHolder = first;
        var begun = tree.BeginNavigation();
        tree.SelectionHolder = second;
        var other = tree.BeginNavigation();
        Check("a navigation begun in one pane is still the latest there when the other pane begins one",
            tree.IsLatestNavigation(begun) && tree.IsLatestNavigation(other));
        tree.SelectionHolder = first;
        tree.BeginNavigation();
        Check("and gives way to the next one begun in its own pane", !tree.IsLatestNavigation(begun) && tree.IsLatestNavigation(other));
    }

    // ---- a location added from the navigation pane ------------------------------------

    private static async Task ReviewAddedRootAsync(string root)
    {
        Section("view model review: a location added from the navigation pane");
        var folder = Path.Combine(root, "roots");
        var scratch = Path.Combine(root, "roots-state");
        var share = Path.Combine(folder, "share-like");
        var closed = Path.Combine(folder, "share-closed");
        Directory.CreateDirectory(Path.Combine(share, "inside"));
        Directory.CreateDirectory(Path.Combine(closed, "inside"));
        Directory.CreateDirectory(scratch);

        using var icons = new ShellIconService();
        using var tree = NewTree(scratch, icons);
        tree.PreferLightReveal = true;
        tree.IsCanvasShown = false;
        var first = new object();
        var second = new object();
        tree.SelectionHolder = first;
        await tree.InitializeAsync(folder);
        var landings = new List<NavigationLanding>();
        tree.NavigationLandedAway += landings.Add;
        var before = tree.ActivePath;

        // Added in the first pane; the second is clicked while it answers.
        var adding = tree.AddRootAsync(share);
        tree.SelectionHolder = second;
        var added = await adding;
        Check("a location added while the other pane was clicked lands in the pane it was asked in",
            added is not null && landings.Count == 1 && ReferenceEquals(landings[0].Holder, first)
            && ViewAllPath.Equals(landings[0].Node.FullPath, share) && landings[0].Select && landings[0].Focus);
        Check("and the pane being worked with keeps its selection",
            before.Length > 0 && ViewAllPath.Equals(tree.ActivePath, before) && !tree.Selection.Contains(share));

        // The window closed while it answered.
        var closing = NewTree(scratch, icons);
        closing.PreferLightReveal = true;
        closing.IsCanvasShown = false;
        await closing.InitializeAsync(folder);
        var late = closing.AddRootAsync(closed);
        closing.Dispose();
        Exception? thrown = null;
        ViewAllNodeViewModel? result = null;
        try
        {
            result = await late;
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        Check($"a location added in a window closed while it answered ends quietly ({thrown?.GetType().Name ?? "nothing thrown"})",
            thrown is null && result is null);
    }

    // ---- the lines out of a large folder ----------------------------------------------

    private static Task ReviewEdgeRelease()
    {
        Section("view model review: the lines out of a large folder");
        static ViewAllEntryDescriptor Entry(string path, ViewAllEntryKind kind) =>
            new(path, Path.GetFileName(path), kind, false, false, null, DateTime.UtcNow);

        const int count = 40_000;
        var folder = new ViewAllNodeViewModel(Entry(@"C:\edges", ViewAllEntryKind.Folder), 0);
        var edges = new List<ViewAllEdgeViewModel>(count);
        for (var index = 0; index < count; index++)
        {
            edges.Add(new ViewAllEdgeViewModel(folder, new ViewAllNodeViewModel(Entry($@"C:\edges\item-{index:D5}.bin", ViewAllEntryKind.File), 1, folder)));
        }

        // Each line still hears of its folder moving; one let go of does not.
        var kept = edges[^1];
        var released = edges[0];
        var keptHeard = 0;
        var releasedHeard = 0;
        kept.PropertyChanged += (_, e) => keptHeard += e.PropertyName == nameof(ViewAllEdgeViewModel.SourceAnchor) ? 1 : 0;
        released.PropertyChanged += (_, e) => releasedHeard += e.PropertyName == nameof(ViewAllEdgeViewModel.SourceAnchor) ? 1 : 0;
        released.Dispose();
        folder.SetAutomaticLocation(new Point(10, 20));
        Check("a line hears of its folder moving, and one let go of does not", keptHeard > 0 && releasedHeard == 0);

        var watch = Stopwatch.StartNew();
        foreach (var edge in edges)
        {
            edge.Dispose();
        }

        watch.Stop();
        Report($"letting go of the lines out of a folder of {count:N0}, in the order they were made", watch.ElapsedMilliseconds, 250);

        var heard = keptHeard;
        folder.SetAutomaticLocation(new Point(30, 40));
        Check("once every line is let go of, none hears of the folder moving", keptHeard == heard);
        return Task.CompletedTask;
    }
}
