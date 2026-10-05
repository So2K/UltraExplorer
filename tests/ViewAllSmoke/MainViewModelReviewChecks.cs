using System.IO;
using UltraExplorer.Controls;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// What the review found in the window's own view model, and what was done
/// about it: a file command is ready for the next key as soon as it is done,
/// not when its message goes; a folder window keeps its pins, orders, colours
/// and settings where every window reads them; two windows - in one process
/// or two - save only what each changed, so neither puts back what it read
/// at its start over the other's change, and a setting changed while a
/// drive is slow to answer is still written; a pin made in a dialog survives
/// the window's next pin; a colour set in a replaced dialog just before it is
/// confirmed is written; Delete and a drop look for their items off the UI
/// thread; and Delete asks first when selected items are not shown.  Every
/// file these touch is in the isolated state folder, put back as it was at
/// the end.
/// </summary>
internal static partial class Program
{
    private static Task MainViewModelReviewChecks()
    {
        RunOnSta("main view model review", MainViewModelReviewOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task MainViewModelReviewOnStaAsync()
    {
        Section("main view model review: isolated state");
        var isolated = Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1"
            && Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable) is { Length: > 0 } state
            && ViewAllPath.Equals(state, AppPaths.StateDirectory)
            && !ViewAllPath.Equals(AppPaths.StateDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer"));
        Check("the main view model review requires isolated state and test-window mode", isolated);
        if (!isolated)
        {
            return;
        }

        Directory.CreateDirectory(AppPaths.StateDirectory);
        string[] owned = [AppPaths.State("workspace.json"), AppPaths.State("folder-marks.json"), AppPaths.State("view-all.workspace.json")];
        var originals = owned.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        var rendererBefore = GpuBootstrap.Preference;
        var fixture = Path.Combine(Path.GetTempPath(), "UltraExplorerMainViewModelReview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            await MainReviewToastChecksAsync(fixture);
            await MainReviewFolderWindowChecksAsync(fixture);
            await MainReviewTwoWindowsChecksAsync(fixture);
            await MainReviewSlowDriveChecksAsync(fixture);
            await MainReviewPinChecksAsync(fixture);
            await MainReviewDialogMarkChecksAsync(fixture);
            await MainReviewExistenceChecksAsync(fixture);
            await MainReviewUnshownSelectionChecksAsync(fixture);
        }
        finally
        {
            // Every model here applied the fixture's processor choice to the
            // process; the checks after these must find it as it was.
            GpuBootstrap.UseSavedPreference(rendererBefore);
            foreach (var (path, bytes) in originals)
            {
                if (bytes is not null)
                {
                    File.WriteAllBytes(path, bytes);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            TryDelete(fixture);
        }
    }

    /// <summary>
    /// The shared state as a test starts it: a workspace with the processor
    /// as its renderer - so no model starts the graphics card - and nothing
    /// else, no marks and no canvas layout.
    /// </summary>
    private static async Task MainReviewResetAsync(WorkspaceState? workspace = null)
    {
        foreach (var name in new[] { "folder-marks.json", "view-all.workspace.json" })
        {
            if (File.Exists(AppPaths.State(name)))
            {
                File.Delete(AppPaths.State(name));
            }
        }

        workspace ??= new WorkspaceState();
        workspace.CanvasRenderer ??= "Cpu";
        await new WorkspaceStore().SaveAsync(workspace);
    }

    // ---- I026: the next key press after a file command ---------------------------

    private static async Task MainReviewToastChecksAsync(string fixture)
    {
        Section("main view model review: a file command is ready again as soon as it is done (I026)");
        await MainReviewResetAsync();
        var folder = Path.Combine(fixture, "toast");
        Directory.CreateDirectory(folder);

        // A file dialog's model: nothing of it is written back.
        using var model = new MainViewModel(Path.Combine(fixture, "toast.tree.json"));
        await model.InitializeAsync(folder);
        await model.Tree.RevealPathAsync(folder);
        await Until(() => ViewAllPath.Equals(model.Tree.TargetDirectory ?? string.Empty, folder), 5_000);
        model.PromptRequested += (_, _, _, _) => "made";

        model.NewFolderCommand.Execute(null);
        var created = await Until(() => Directory.Exists(Path.Combine(folder, "made"))
            && model.Toast.Message.StartsWith("Created", StringComparison.Ordinal), 10_000);
        var ready = await Until(() => model.NewFolderCommand.CanExecute(null), 1_000);
        Check("New folder is done and says so", created);
        Check("and it can be used again at once, not only once its message has gone", ready);
        Check("the message is still showing", model.Toast.IsVisible);
    }

    // ---- I009: what a folder window changes is kept where every window reads it ----

    private static async Task MainReviewFolderWindowChecksAsync(string fixture)
    {
        Section("main view model review: a folder window keeps its pins, orders, colours and settings (I009)");
        var folder = Path.Combine(fixture, "adopted");
        var pinned = Path.Combine(folder, "pinned");
        var sorted = Path.Combine(folder, "sorted");
        var marked = Path.Combine(folder, "marked.txt");
        Directory.CreateDirectory(pinned);
        Directory.CreateDirectory(sorted);
        File.WriteAllText(marked, "x");
        await MainReviewResetAsync(new WorkspaceState { IsSplit = false, SplitRatio = 0.6, NestedLeftDrag = "select" });

        // As the folder router makes one for an adopted Explorer window.
        var windowWorkspace = Path.Combine(fixture, "shell-windows", Guid.NewGuid().ToString("N") + ".workspace.json");
        using (var window = new MainViewModel(windowWorkspace + ".tree.json", false, windowWorkspace))
        {
            await window.InitializeAsync(folder);
            Check("the folder window opens with one pane", !window.IsSplit);
            window.PinPath(pinned);
            window.Orders.SetFolder(sorted, new ItemSort(SortColumn.Size, true));
            window.LeftDrag = NestedLeftDrag.Pan;
            window.Tree.ApplyAccent([marked], "#FF8800");
            window.IsSplit = true;
            window.SplitRatio = 0.3;
            await window.SaveNowAsync();
        }

        var saved = await new WorkspaceStore().LoadAsync();
        Check("its pin is in the workspace every window reads",
            saved?.Favorites.Any(item => ViewAllPath.Equals(item.Path, pinned)) == true);
        Check("so is the order it gave a folder",
            saved?.FolderSorts?.Any(item => ViewAllPath.Equals(item.Path, sorted)) == true);
        Check("and its left-drag setting", saved?.NestedLeftDrag == "pan");
        Check("but not its own split: the everyday window's is kept",
            saved is { IsSplit: false, SplitRatio: 0.6 });

        var marks = new FolderMarkService();
        await marks.LoadAsync();
        Check("its colour is in the marks every window reads", marks.Get(marked).AccentHex == "#FF8800");

        using var everyday = new MainViewModel();
        await everyday.InitializeAsync(folder);
        Check("the everyday window, opened next, has the pin",
            everyday.QuickAccess.Any(item => item.IsCustom && ViewAllPath.Equals(item.Path, pinned)));
        Check("the folder's order", everyday.Orders.SortOf(sorted) == new ItemSort(SortColumn.Size, true));
        Check("and the colour", everyday.Marks.Get(marked).AccentHex == "#FF8800");
    }

    // ---- I047: two windows, each saving only what it changed ----------------------

    private static async Task MainReviewTwoWindowsChecksAsync(string fixture)
    {
        Section("main view model review: two windows keep each other's settings (I047)");
        var folder = Path.Combine(fixture, "two");
        var first = Path.Combine(folder, "first");
        var second = Path.Combine(folder, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        await MainReviewResetAsync(new WorkspaceState { NestedLeftDrag = "select", SidebarWidth = 250 });

        using var older = new MainViewModel();
        await older.InitializeAsync(folder);
        using var newer = new MainViewModel();
        await newer.InitializeAsync(folder);

        newer.Orders.SetFolder(first, new ItemSort(SortColumn.Modified, true));
        newer.SetLayer(CanvasLayer.Details, false);
        await newer.SaveNowAsync();

        older.LeftDrag = NestedLeftDrag.Pan;
        await older.SaveNowAsync();
        var afterToggle = await new WorkspaceStore().LoadAsync();
        Check("a toggle in a window loaded earlier keeps the other window's folder order",
            afterToggle?.FolderSorts?.Any(item => ViewAllPath.Equals(item.Path, first)) == true);
        Check("and its layers", afterToggle?.CanvasLayersOff?.Contains("Details") == true);
        Check("and saves its own toggle", afterToggle?.NestedLeftDrag == "pan");

        older.Orders.SetFolder(second, new ItemSort(SortColumn.Size, true));
        older.SidebarWidth = 300;
        await older.SaveNowAsync();
        var afterSort = await new WorkspaceStore().LoadAsync();
        Check("a folder sorted in each window: both orders are kept",
            afterSort?.FolderSorts?.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([first, second]) == true);

        // Closing: each window saves once more, with nothing new of its own.
        await newer.SaveNowAsync();
        await older.SaveNowAsync();
        var afterClose = await new WorkspaceStore().LoadAsync();
        Check("closing both keeps every change either made",
            afterClose is { NestedLeftDrag: "pan", SidebarWidth: 300 }
            && afterClose.CanvasLayersOff?.Contains("Details") == true
            && afterClose.FolderSorts?.Count == 2);

        newer.Orders.ResetFolder(first);
        await newer.SaveNowAsync();
        var afterReset = await new WorkspaceStore().LoadAsync();
        Check("a folder's order let go of in one window goes, and the other window's stays",
            afterReset?.FolderSorts?.Select(item => item.Path).SequenceEqual([second], StringComparer.OrdinalIgnoreCase) == true);
    }

    // ---- I047: a setting changed while a drive is slow to answer -------------------

    /// <summary>
    /// The window is shown and usable while a drive mapped to a server that
    /// is off takes some twenty seconds to say it is not ready.  Here every
    /// such question waits until settings have been changed in the window;
    /// its next save must write them, not take them for what the file had.
    /// </summary>
    private static async Task MainReviewSlowDriveChecksAsync(string fixture)
    {
        Section("main view model review: a setting changed while a drive is slow to answer is written (I047)");
        var folder = Path.Combine(fixture, "slow");
        var sorted = Path.Combine(folder, "sorted");
        Directory.CreateDirectory(sorted);
        await MainReviewResetAsync(new WorkspaceState { NestedLeftDrag = "select" });

        using var asked = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        var exists = FileSystemService.FolderExists;
        FileSystemService.FolderExists = path =>
        {
            asked.Set();
            released.Wait(TimeSpan.FromSeconds(15));
            return exists(path);
        };
        try
        {
            using var window = new MainViewModel();
            var initializing = window.InitializeAsync(folder);
            var held = await Until(() => asked.IsSet, 10_000);
            Check("the window is still asking its drives whether they are ready", held && !initializing.IsCompleted);
            window.LeftDrag = NestedLeftDrag.Pan;
            window.SetLayer(CanvasLayer.Details, false);
            window.Orders.SetFolder(sorted, new ItemSort(SortColumn.Size, true));
            released.Set();
            await initializing.WaitAsync(TimeSpan.FromSeconds(60));
            FileSystemService.FolderExists = exists;

            await window.SaveNowAsync();
            var saved = await new WorkspaceStore().LoadAsync();
            Check("its next save writes the left-drag setting changed meanwhile", saved?.NestedLeftDrag == "pan");
            Check("and the layers", saved?.CanvasLayersOff?.Contains("Details") == true);
            Check("and the folder's order",
                saved?.FolderSorts?.Any(item => ViewAllPath.Equals(item.Path, sorted) && item.Sort == new ItemSort(SortColumn.Size, true).ToSetting()) == true);
        }
        finally
        {
            released.Set();
            FileSystemService.FolderExists = exists;
        }
    }

    // ---- I046: pins made elsewhere survive the window's own -------------------------

    private static async Task MainReviewPinChecksAsync(string fixture)
    {
        Section("main view model review: a pin made in a dialog survives the window's next pin (I046)");
        var folder = Path.Combine(fixture, "pins");
        var kept = Path.Combine(folder, "kept");
        var dialog = Path.Combine(folder, "dialog");
        var added = Path.Combine(folder, "added");
        foreach (var path in new[] { kept, dialog, added })
        {
            Directory.CreateDirectory(path);
        }

        await MainReviewResetAsync(new WorkspaceState { Favorites = [new FavoriteState("kept", kept, "", "#E3B341")] });
        using var window = new MainViewModel();
        await window.InitializeAsync(folder);

        // What a replaced Save dialog does when the user pins there.
        using (var picker = new MainViewModel(Path.Combine(fixture, "pins.tree.json"), nestedPicker: true) { SuppressShellWrites = true })
        {
            await picker.InitializeAsync(folder);
            picker.PinPath(dialog);
            await picker.SaveNowAsync();
        }

        window.PinPath(added);
        await window.SaveNowAsync();
        var afterPin = await new WorkspaceStore().LoadAsync();
        Check("a pin in the window keeps the one made in the dialog meanwhile",
            afterPin?.Favorites.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([kept, dialog, added]) == true);

        window.UnpinPath(kept);
        await window.SaveNowAsync();
        var afterUnpin = await new WorkspaceStore().LoadAsync();
        Check("an unpin in the window takes off only that pin",
            afterUnpin?.Favorites.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([dialog, added]) == true);
    }

    // ---- I030: what a file command is given is looked for off the UI thread ----------

    private static async Task MainReviewExistenceChecksAsync(string fixture)
    {
        Section("main view model review: Delete and a drop look for their items off the UI thread (I030)");
        await MainReviewResetAsync();
        var folder = Path.Combine(fixture, "exists");
        var target = Path.Combine(folder, "target");
        Directory.CreateDirectory(target);
        var files = Enumerable.Range(0, 3).Select(index => Path.Combine(folder, $"item{index}.txt")).ToArray();
        foreach (var file in files)
        {
            File.WriteAllText(file, "x");
        }

        // A file dialog's model: nothing of it is written back.
        using var model = new MainViewModel(Path.Combine(fixture, "exists.tree.json"));
        await model.InitializeAsync(folder);
        model.Tree.Selection.Apply(new SelectionEdit
        {
            Clear = true,
            Container = folder,
            Added = [.. files.Select(file => new SelectionItem(file, false, 1))],
            Source = SelectionSource.Canvas
        });

        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var ui = Thread.CurrentThread;
        var exists = MainViewModel.ItemExists;
        try
        {
            foreach (var command in new[] { "Delete", "a drop" })
            {
                var asked = 0;
                var askedOnUi = 0;
                using var askedEvent = new ManualResetEventSlim();
                using var released = new ManualResetEventSlim();

                // Every item found gone, as on a share that has dropped: no
                // command goes on to the Shell.
                MainViewModel.ItemExists = path =>
                {
                    Interlocked.Increment(ref asked);
                    if (Thread.CurrentThread == ui)
                    {
                        Interlocked.Increment(ref askedOnUi);
                    }

                    askedEvent.Set();
                    released.Wait(TimeSpan.FromSeconds(15));
                    return false;
                };

                var answered = TopReviewAnswersWhileHeld(dispatcher, askedEvent, released);
                Task done;
                if (command == "Delete")
                {
                    model.DeleteCommand.Execute(null);
                    done = Until(() => model.DeleteCommand.CanExecute(null), 20_000);
                }
                else
                {
                    done = model.DropIntoPathAsync(files, target, move: false);
                }

                var uiAnswered = await answered;
                await done.WaitAsync(TimeSpan.FromSeconds(20));
                Check($"{command} looks for its items off the UI thread ({Volatile.Read(ref askedOnUi)} of {Volatile.Read(ref asked)} on it)",
                    Volatile.Read(ref asked) > 0 && Volatile.Read(ref askedOnUi) == 0);
                Check($"and the UI thread answers while {command} waits for a share", uiAnswered);
            }

            Check("Delete lets go of the items it found gone", model.Tree.Selection.Count == 0);
            Check("and the drop says there was nothing to copy", model.Toast.Message.StartsWith("This drop target is not valid", StringComparison.Ordinal));
            Check("nothing was deleted or copied", files.All(File.Exists) && !Directory.EnumerateFileSystemEntries(target).Any());
        }
        finally
        {
            MainViewModel.ItemExists = exists;
        }
    }

    // ---- I105: Delete with selected items the canvas does not show ----------------

    private static async Task MainReviewUnshownSelectionChecksAsync(string fixture)
    {
        Section("main view model review: Delete asks first when selected items are not shown (I105)");
        await MainReviewResetAsync();
        var folder = Path.Combine(fixture, "unshown");
        Directory.CreateDirectory(folder);
        var files = new[] { "a.txt", "b.txt", "desktop.ini" }.Select(name => Path.Combine(folder, name)).ToArray();
        foreach (var file in files)
        {
            File.WriteAllText(file, "x");
        }

        // A file dialog's model: nothing of it is written back.
        using var model = new MainViewModel(Path.Combine(fixture, "unshown.tree.json"));
        await model.InitializeAsync(folder);
        model.Tree.Selection.Apply(new SelectionEdit
        {
            Clear = true,
            Container = folder,
            Added = [.. files.Select(file => new SelectionItem(file, false, 1))],
            Source = SelectionSource.Canvas
        });

        // Every question answered No: nothing reaches the Shell.
        var asked = new List<(string Title, string Message)>();
        var labels = new List<string>();
        model.ConfirmRequested += (title, message, label) =>
        {
            asked.Add((title, message));
            labels.Add(label);
            return false;
        };

        // Hidden items switched off after Ctrl+A: two of the three drawn.
        model.ShownSelectionCount = () => 2;
        model.DeleteCommand.Execute(null);
        await Until(() => model.DeleteCommand.CanExecute(null), 10_000);
        Check("Delete asks first, naming how many selected items are not shown",
            asked.Count == 1 && asked[0].Message.Contains("1 of the 3 selected items", StringComparison.Ordinal));
        Check("and, told No, deletes nothing and keeps the selection", files.All(File.Exists) && model.Tree.Selection.Count == 3);

        asked.Clear();
        model.ShownSelectionCount = () => 3;
        model.PermanentDeleteCommand.Execute(null);
        await Until(() => model.PermanentDeleteCommand.CanExecute(null), 10_000);
        Check("with every selected item shown, only the usual question is asked",
            asked.Count == 1 && asked[0].Title == "Permanently delete");

        asked.Clear();
        model.ShownSelectionCount = () => null;
        model.PermanentDeleteCommand.Execute(null);
        await Until(() => model.PermanentDeleteCommand.CanExecute(null), 10_000);
        Check("and so where the window cannot say what is shown",
            asked.Count == 1 && asked[0].Title == "Permanently delete" && files.All(File.Exists));
        Check("every Delete question confirms with Delete", labels.All(label => label == "Delete"));

        // I075: opening more than fifteen files asks with Open, not Delete.
        var many = Enumerable.Range(0, 16).Select(index => Path.Combine(folder, $"open{index:D2}.txt")).ToArray();
        foreach (var file in many)
        {
            File.WriteAllText(file, "x");
        }

        model.Tree.Selection.Apply(new SelectionEdit
        {
            Clear = true,
            Container = folder,
            Added = [.. many.Select(file => new SelectionItem(file, false, 1))],
            Source = SelectionSource.Canvas
        });
        asked.Clear();
        labels.Clear();
        model.OpenCommand.Execute(null);
        Check("opening sixteen files asks first, and its button says Open (I075)",
            asked.Count == 1 && asked[0].Title == "Open" && labels.SequenceEqual(["Open"]));
    }

    // ---- I049: a colour set in a replaced dialog just before it is confirmed ---------

    private static async Task MainReviewDialogMarkChecksAsync(string fixture)
    {
        Section("main view model review: a colour set in a replaced dialog just before OK is written (I049)");
        var folder = Path.Combine(fixture, "dialog-mark");
        var file = Path.Combine(folder, "report.txt");
        Directory.CreateDirectory(folder);
        File.WriteAllText(file, "x");
        await MainReviewResetAsync();
        var workspaceBefore = File.ReadAllBytes(AppPaths.State("workspace.json"));
        var treeState = Path.Combine(fixture, "dialog-mark.tree.json");

        // As the replacement makes it: nothing of the user's is written but a
        // mark changed in it.
        var picker = new MainViewModel(treeState, nestedPicker: true) { SuppressShellWrites = true };
        picker.Tree.SuppressWrites = true;
        try
        {
            await picker.InitializeAsync(folder);
            picker.Tree.ApplyAccent([file], "#33AA55");

            // OK at once: the window's save on closing, then it is let go of.
            await picker.SaveNowAsync();
        }
        finally
        {
            picker.Dispose();
        }

        var marks = new FolderMarkService();
        await marks.LoadAsync();
        Check("the colour is in the marks", marks.Get(file).AccentHex == "#33AA55");
        Check("the dialog wrote no layout of its own", !File.Exists(treeState));
        Check("and left the workspace as it was", File.ReadAllBytes(AppPaths.State("workspace.json")).SequenceEqual(workspaceBefore));
    }
}
