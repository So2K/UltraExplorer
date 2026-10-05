using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UltraExplorer;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// What Delete, Cut and Move act on is what the user picked, never a folder
/// that only stands near it: a file deleted does not hand the selection to
/// its folder, an item added inside the folder gone into lets go of that
/// folder, Ctrl+A fills the folder on screen, the list's rows say which
/// folder they are in and where its Shift ranges run from, a reveal still
/// reading its way down does not overrule a newer pick, a double-click opens
/// only what both of its clicks were on, the arrows stay in the folder of an
/// item that went, and the status bar names the item that is selected.
/// </summary>
internal static partial class Program
{
    private const string SafetyWork = @"Q:\work";
    private const string SafetyDocs = @"Q:\work\docs";

    private static async Task SelectionSafetyChecks()
    {
        RunOnSta("selection safety: the canvas", SelectionSafetyCanvasAsync);
        RunOnSta("selection safety: the list", () =>
        {
            SelectionSafetyListChecks();
            return Task.CompletedTask;
        });
        await OnDispatcher(SelectionSafetyViewModelAsync);
    }

    /// <summary>
    /// Q:\work, with a folder of its own and six files; Q:\work\docs, the
    /// folder gone into, with one sub-folder and twenty files; R:\elsewhere.
    /// </summary>
    private static FakeDisk BuildSafetyWorld()
    {
        var disk = new FakeDisk();
        disk.Folder(@"Q:\");
        disk.Folder(@"R:\");
        disk.Folder($@"{SafetyDocs}\inner");
        disk.AddFiles(SafetyDocs, 20, "d");
        disk.AddFiles($@"{SafetyWork}\music", 4, "m");
        disk.AddFiles(SafetyWork, 6, "p");
        disk.AddFiles(@"R:\elsewhere", 3, "e");
        return disk;
    }

    // ---- on a canvas ---------------------------------------------------------------------

    private static async Task SelectionSafetyCanvasAsync()
    {
        var disk = BuildSafetyWorld();
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots(
        [
            new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive, "1 TB free"),
            new NestedRoot(@"R:\", "R:", NestedFolderKind.Drive, "1 TB free")
        ]);
        await LoadEverythingAsync(tree, _ => true);

        var canvas = new NestedCanvas { Tree = tree, DpiOverride = new DpiScale(1, 1) };
        canvas.Measure(new Size(ViewWidth, ViewHeight));
        canvas.Arrange(new Rect(0, 0, ViewWidth, ViewHeight));
        canvas.UpdateLayout();

        // The window's side, as a pane has it: the edit applied to the shared
        // selection, and the canvas told the version is its own - or, when
        // the two no longer hold as many items, handed the shared one.
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

        var work = tree.Find(SafetyWork)!;
        var docs = tree.Find(SafetyDocs)!;
        var pointer = canvas.Pointer;

        // Going somewhere - the address bar, Back, the sidebar - selects the
        // folder gone to, and the canvas flies there.
        void GoTo(NestedFolder folder)
        {
            shared.ReplaceSingle(folder.FullPath, true, 0, SelectionSource.Navigation);
            canvas.LoadSelection(shared);
            canvas.FlyTo(folder, 0.9, animated: false);
            Render(canvas);
        }

        HashSet<string> Shared() => shared.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string Doc(int index) => docs.PathOf(docs.Files[index]);

        try
        {
            // ---- adding inside the folder gone into ------------------------------------------
            Section("selection safety: adding inside the folder gone into");
            GoTo(docs);
            var cell = canvas.ScreenRectOf(docs)!.Value;
            pointer.Click(TilePoint(cell, docs, 3), ModifierKeys.Control);
            pointer.Click(TilePoint(cell, docs, 5), ModifierKeys.Control);
            string[] two = [Doc(3), Doc(5)];
            Check("Ctrl+clicking two files in the folder gone into selects the two files, not the folder around them as well",
                Shared().SetEquals(two) && Picked(canvas).SetEquals(two));
            Check("and they are counted in their own folder, where Paste and New folder then go",
                string.Equals(shared.Container, docs.FullPath, StringComparison.OrdinalIgnoreCase)
                && shared.CountIn(docs.FullPath) == 2 && shared.CountIn(work.FullPath) == 0);

            shared.Apply(new SelectionEdit
            {
                Clear = true,
                Added = [new SelectionItem(work.FullPath, true, 0), new SelectionItem(Doc(1), false, 101)],
                Anchor = Doc(1),
                Focus = Doc(1),
                Source = SelectionSource.Navigation
            });
            canvas.LoadSelection(shared);
            Render(canvas);
            pointer.Click(TilePoint(cell, docs, 4), ModifierKeys.Control | ModifierKeys.Shift);
            var range = Enumerable.Range(1, 4).Select(Doc).ToArray();
            Check("Ctrl+Shift+click adding a range lets go of a selected folder further up as well",
                Shared().SetEquals(range) && Picked(canvas).SetEquals(range) && shared.CountIn(@"Q:\") == 0);

            GoTo(docs);
            var (_, firstY) = docs.FileGrid.Origin(0);
            pointer.Drag(At(cell, MarginX, firstY + docs.FileGrid.TileHeight / 2), TilePoint(cell, docs, 2), ModifierKeys.Shift);
            Check($"a rectangle added with Shift inside the folder gone into lets go of the folder ({shared.Count} selected)",
                shared.Count > 0 && !shared.Contains(docs.FullPath) && !Picked(canvas).Contains(docs.FullPath)
                && shared.Paths.All(path => ItemSelection.ParentOf(path).Equals(docs.FullPath, StringComparison.OrdinalIgnoreCase)));

            // ---- Ctrl+A in the folder on screen -------------------------------------------------
            Section("selection safety: Ctrl+A fills the folder on screen");
            var inDocs = docs.Children.Select(child => child.FullPath).Concat(docs.Files.Select(file => docs.PathOf(file))).ToArray();
            canvas.FlyTo(work, 0.9, animated: false);
            Render(canvas);
            pointer.Click(TilePoint(canvas.ScreenRectOf(work)!.Value, work, 1));
            GoTo(docs);
            canvas.HandleKey(Key.A, ModifierKeys.Control);
            Check($"after going to a folder by its path, Ctrl+A selects what is in it, not in the folder clicked in before ({shared.Count} selected)",
                Picked(canvas).SetEquals(inDocs) && Shared().SetEquals(inDocs));

            pointer.Click(TilePoint(cell, docs, 7));
            canvas.HandleKey(Key.Back, ModifierKeys.None);
            canvas.FlyTo(docs, 0.9, animated: false);
            Render(canvas);
            canvas.HandleKey(Key.A, ModifierKeys.Control);
            Check($"after Backspace up to a folder, Ctrl+A selects what is in that folder ({shared.Count} selected)",
                Picked(canvas).SetEquals(inDocs) && Shared().SetEquals(inDocs));

            // A delete leaves the view where it was: with the camera on the
            // parent, Ctrl+A stays in the folder the deleted file was in.
            canvas.FlyTo(work, 0.9, animated: false);
            Render(canvas);
            pointer.Click(TilePoint(canvas.ScreenRectOf(docs)!.Value, docs, 3));
            shared.Remove([.. shared.Paths], SelectionSource.Command);
            canvas.LoadSelection(shared);
            Render(canvas);
            canvas.HandleKey(Key.A, ModifierKeys.Control);
            Check($"after the file clicked is deleted, Ctrl+A selects what is in its folder, not in the folder around it on screen ({shared.Count} selected in {shared.Container})",
                string.Equals(shared.Container, docs.FullPath, StringComparison.OrdinalIgnoreCase)
                && Picked(canvas).SetEquals(inDocs) && Shared().SetEquals(inDocs));

            // ---- double-clicks ----------------------------------------------------------------
            Section("selection safety: a double-click opens what both clicks were on");
            var opened = new List<string>();
            canvas.OpenRequested += hit => opened.Add(hit.Path);
            GoTo(docs);
            cell = canvas.ScreenRectOf(docs)!.Value;
            var first = TilePoint(cell, docs, 3);
            var next = TilePoint(cell, docs, 4);
            pointer.Click(first);
            canvas.Pan(first - next);
            Render(canvas);
            pointer.Down(MouseButton.Left, first, clickCount: 2);
            pointer.Up(MouseButton.Left, first);
            Check("a second press that lands on another item - the view moved between the clicks - opens nothing", opened.Count == 0);
            opened.Clear();
            pointer.Click(first);
            pointer.Down(MouseButton.Left, first, clickCount: 2);
            pointer.Up(MouseButton.Left, first);
            Check("a double-click on one item opens that item", opened.SequenceEqual([Doc(4)]));

            opened.Clear();
            var navigated = 0;
            canvas.FavoriteLinkRequested += _ => navigated++;
            canvas.SetFavoriteLinks([new NestedFavoriteLink(SafetyWork, "Work", Color.FromRgb(230, 178, 83))]);
            canvas.ShowFavoriteLinks = true;
            try
            {
                canvas.FitAll(animated: false);
                canvas.RenderNow();
                var circle = canvas.FavoriteLinkPositions().Single().Centre;
                pointer.Click(circle);

                // The view slides on under the pointer, as a beacon's flight
                // does: a drive comes to where the circle was.
                var drive = canvas.ScreenRectOf(tree.Find(@"R:\")!)!.Value;
                canvas.Pan(circle - new Point(drive.X + drive.Width / 2, drive.Y + drive.Width * NestedLayout.CellHeight * 0.6));
                canvas.RenderNow();
                var under = canvas.HitTest(circle);
                pointer.Down(MouseButton.Left, circle, clickCount: 2);
                pointer.Up(MouseButton.Left, circle);
                Check($"a double-click begun on a circle or a beacon opens nothing that slid under it ({under?.Folder.FullPath ?? "nothing"} under it)",
                    under is { Folder.IsComputer: false } && opened.Count == 0 && navigated == 0);
            }
            finally
            {
                canvas.ShowFavoriteLinks = false;
                canvas.SetFavoriteLinks([]);
            }

            // ---- the arrows after the focused item went ----------------------------------------
            Section("selection safety: the arrows after the focused item went");
            GoTo(docs);
            cell = canvas.ScreenRectOf(docs)!.Value;
            pointer.Click(TilePoint(cell, docs, 9));
            var gone = docs.Files[9].Name;
            disk.Folder(SafetyDocs).Files.RemoveAll(file => file.Name == gone);
            await tree.RefreshAsync(docs);
            Render(canvas);
            canvas.FitAll(animated: false);
            Render(canvas);
            string? moved = null;
            canvas.SelectRequested += (path, _) => moved = path;
            canvas.HandleKey(Key.Down, ModifierKeys.None);
            Check($"Down after the focused file was deleted moves within its folder, not to the overview's first folder ({moved ?? "nothing"})",
                moved is not null && ItemSelection.ParentOf(moved).Equals(docs.FullPath, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            canvas.Tree = null;
        }
    }

    // ---- the folder list ---------------------------------------------------------------------

    private static FolderListItem SafetyRow(string path, bool isDirectory = false) => new(new ViewAllEntryDescriptor(
        path,
        Path.GetFileName(path),
        isDirectory ? ViewAllEntryKind.Folder : ViewAllEntryKind.File,
        false,
        false,
        isDirectory ? null : 10,
        DateTime.UtcNow));

    private static void SelectionSafetyListChecks()
    {
        Section("selection safety: the folder list");
        const string folder = @"C:\x\n";
        var rows = new[] { SafetyRow($@"{folder}\a.txt"), SafetyRow($@"{folder}\b.txt"), SafetyRow($@"{folder}\c.txt") };
        HashSet<string> PathsOf(ItemSelection selection) => selection.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Ctrl+clicks in the list of the folder gone into.
        var shared = new ItemSelection();
        shared.ReplaceSingle(folder, true, 0, SelectionSource.Navigation);
        shared.Apply(MainWindow.ListEdit(shared, folder, new[] { rows[0] }, Array.Empty<object>(), new[] { rows[0] }, null, rows[0], ModifierKeys.Control, false));
        shared.Apply(MainWindow.ListEdit(shared, folder, new[] { rows[1] }, Array.Empty<object>(), new[] { rows[0], rows[1] }, rows[0], rows[1], ModifierKeys.Control, false));
        Check("Ctrl+clicking two rows of the folder gone into selects the two rows, not the folder around them as well",
            PathsOf(shared).SetEquals([rows[0].FullPath, rows[1].FullPath])
            && string.Equals(shared.Container, folder, StringComparison.OrdinalIgnoreCase) && shared.CountIn(@"C:\x") == 0);

        shared.ReplaceSingle(@"C:\x", true, 0, SelectionSource.Navigation);
        shared.Apply(MainWindow.ListEdit(shared, folder, new[] { rows[2] }, Array.Empty<object>(), new[] { rows[2] }, null, rows[2], ModifierKeys.Control, false));
        Check("and a selected folder further up goes too", PathsOf(shared).SetEquals([rows[2].FullPath]) && shared.CountIn(@"C:\") == 0);

        // Ctrl+A in the list, with something selected elsewhere as well.
        shared.Apply(new SelectionEdit
        {
            Clear = true,
            Added = [new SelectionItem(@"C:\y\keep.txt", false, 1), new SelectionItem(folder, true, 0)],
            Source = SelectionSource.Canvas
        });
        shared.Apply(MainWindow.ListEdit(shared, folder, rows, Array.Empty<object>(), rows, null, rows[0], ModifierKeys.Control, true));
        Check("Ctrl+A in the list selects its rows in place of whatever was selected",
            PathsOf(shared).SetEquals(rows.Select(row => row.FullPath)) && shared.CountIn(folder) == 3);

        // The rows of the folder before, still on show while the next is read.
        const string before = @"C:\x\o";
        var old = new[] { SafetyRow($@"{before}\one.txt"), SafetyRow($@"{before}\two.txt") };
        var counted = new ItemSelection();
        counted.Apply(MainWindow.ListEdit(counted, folder, new[] { old[0] }, Array.Empty<object>(), new[] { old[0] }, null, old[0], ModifierKeys.None, false));
        counted.Apply(MainWindow.ListEdit(counted, folder, new[] { old[1] }, Array.Empty<object>(), old, old[0], old[1], ModifierKeys.Control, false));
        Check("rows of the folder before, picked while the next one is read, are counted in their own folder",
            string.Equals(counted.Container, before, StringComparison.OrdinalIgnoreCase)
            && counted.CountIn(before) == 2 && counted.CountIn(folder) == 0);

        // Where the list's Shift ranges run from.
        var plain = MainWindow.ListEdit(new ItemSelection(), folder, new[] { rows[2] }, new[] { rows[0] }, new[] { rows[2] }, rows[0], rows[2], ModifierKeys.None, false);
        var toggle = MainWindow.ListEdit(new ItemSelection(), folder, new[] { rows[1] }, Array.Empty<object>(), new[] { rows[0], rows[1] }, rows[0], rows[1], ModifierKeys.Control, false);
        var extend = MainWindow.ListEdit(new ItemSelection(), folder, new[] { rows[1], rows[2] }, Array.Empty<object>(), rows, rows[0], rows[2], ModifierKeys.Shift, false);
        Check("a click or a Ctrl+click in the list moves the shared anchor to its row, a Shift+click keeps it",
            plain.Anchor == rows[2].FullPath && toggle.Anchor == rows[1].FullPath && extend.Anchor == rows[0].FullPath);

        var items = Enumerable.Range(0, 400).Select(index => $"row {index:D3}").ToList();
        var box = new SelectionListBox();
        box.BeginInit();
        box.SelectionMode = SelectionMode.Extended;
        box.ItemsSource = items;
        box.EndInit();
        box.Measure(new Size(400, 300));
        box.Arrange(new Rect(0, 0, 400, 300));
        box.UpdateLayout();
        box.ApplySelection(new List<string> { items[2] }, items[2]);
        Check($"the row a list box runs Shift ranges from is handed on as the row itself ({box.AnchorRow?.GetType().Name ?? "null"})",
            ReferenceEquals(box.AnchorRow, items[2]));
        box.ApplySelection(new List<string> { items[300] }, items[300]);
        Check("an anchor whose row is scrolled far away leaves no older anchor for a Shift range to run from",
            box.AnchorRow is null && box.SelectedItems.Count == 1 && ReferenceEquals(box.SelectedItems[0], items[300]));
    }

    // ---- through the view model ---------------------------------------------------------------

    private static async Task SelectionSafetyViewModelAsync()
    {
        Section("selection safety: through the view model");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerSelectionSafety", Guid.NewGuid().ToString("N"));
        var scratch = Path.Combine(root, "state");
        var folder = Path.Combine(root, "work");
        Directory.CreateDirectory(scratch);
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt", "r1.txt", "r2.txt" })
        {
            File.WriteAllText(Path.Combine(folder, name), name);
        }

        var hidden = Path.Combine(folder, "h.txt");
        File.WriteAllText(hidden, "h");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        using var icons = new ShellIconService();
        try
        {
            // ---- a delete does not hand the selection to the folder ----------------------------
            using (var tree = NewTree(scratch, icons))
            {
                tree.PreferLightReveal = true;
                tree.IsCanvasShown = false;
                tree.FolderList.IsVisible = true;
                await tree.InitializeAsync(root);
                var navigations = 0;
                tree.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ViewAllViewModel.ActivePath) && tree.FocusRecordsNavigation)
                    {
                        navigations++;
                    }
                };

                var a = Path.Combine(folder, "a.txt");
                var picked = await tree.SelectPathAsync(a);
                var before = navigations;
                File.Delete(a);
                tree.ForgetSelected([a]);
                await tree.RefreshPathAsync(folder);
                Check("deleting the one selected file leaves nothing selected, so a second Delete has nothing to act on",
                    picked is not null && tree.Selection.Count == 0 && !tree.Selection.Contains(folder));
                Check("the focus moves to its folder - what the address bar and the list then show - as no step for Back",
                    ViewAllPath.Equals(tree.ActivePath, folder) && navigations == before);
            }

            using (var tree = NewTree(scratch, icons))
            {
                tree.IsCanvasShown = false;
                await tree.InitializeAsync(root);
                var kept = await tree.SelectPathAsync(hidden);
                await tree.RefreshPathAsync(folder);
                Check("a read that leaves out a selected hidden file still on disk keeps the file selected, never its folder instead",
                    kept is not null && tree.Selection.Count == 1 && tree.Selection.Contains(hidden) && !tree.Selection.Contains(folder));
            }

            // ---- the status bar names what is selected -----------------------------------------
            using (var tree = NewTree(scratch, icons))
            {
                tree.PreferLightReveal = true;
                tree.IsCanvasShown = false;
                await tree.InitializeAsync(root);
                var b = Path.Combine(folder, "b.txt");
                var c = Path.Combine(folder, "c.txt");
                tree.Selection.Apply(new SelectionEdit
                {
                    Clear = true,
                    Container = folder,
                    Added = [new SelectionItem(b, false, 5), new SelectionItem(c, false, 5)],
                    Anchor = b,
                    Focus = c,
                    Source = SelectionSource.Canvas
                });
                var focused = await Until(() => tree.ActivePath.EndsWith("c.txt", StringComparison.OrdinalIgnoreCase), 5_000);
                tree.Selection.Apply(new SelectionEdit
                {
                    Container = folder,
                    Removed = [c],
                    Anchor = c,
                    Focus = c,
                    Source = SelectionSource.Canvas
                });
                await Task.Delay(100);
                Check($"with one item left selected and the focus on one just let go, the status bar names the selected one ({tree.StatusPathText})",
                    focused && tree.Selection.Count == 1 && string.Equals(tree.StatusPathText, b, StringComparison.OrdinalIgnoreCase));
            }

            // ---- a row's reveal and a newer pick -------------------------------------------------
            using (var tree = NewTree(scratch, icons))
            {
                tree.PreferLightReveal = true;
                tree.IsCanvasShown = false;
                tree.FolderList.IsVisible = true;
                await tree.InitializeAsync(root);
                var activate = typeof(ViewAllViewModel).GetMethod("ActivateListItemAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var r1 = Path.Combine(folder, "r1.txt");
                var r2 = Path.Combine(folder, "r2.txt");
                var slow = (Task)activate.Invoke(tree, [r1, false])!;
                var waiting = !slow.IsCompleted;
                tree.Selection.Apply(new SelectionEdit
                {
                    Clear = true,
                    Container = folder,
                    Added = [new SelectionItem(r2, false, 6)],
                    Anchor = r2,
                    Focus = r2,
                    Source = SelectionSource.List
                });
                await slow;
                Check("a row's reveal still reading its way down does not take the selection back from a row picked after it",
                    waiting && tree.Selection.Count == 1 && tree.Selection.Contains(r2));

                await (Task)activate.Invoke(tree, [r1, false])!;
                Check("with nothing picked after it, a row's reveal selects the row",
                    tree.Selection.Count == 1 && tree.Selection.Contains(r1));
            }
        }
        finally
        {
            if (File.Exists(hidden))
            {
                File.SetAttributes(hidden, FileAttributes.Normal);
            }

            TryDelete(root);
        }
    }
}
