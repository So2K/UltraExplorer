using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Dialogs;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// What the 2026-10-02 review found in the right-click menus and the file
/// operations behind them.  A copy pasted into the folder it came from made
/// nothing and said it had (I007).  The Shell menu's Rename on something
/// other than the selection - a search result - renamed the selection
/// (I016).  A right press on a share built the Shell's menu there and then,
/// which on a share that has gone away holds the window for the network's
/// timeout (I033).  A name ending in a dot or a space was dropped to its
/// neighbour's by Windows, so Delete, Rename and Move acted on the
/// neighbour (I044).  Open on a folder in the Shell's menu went to another
/// window (I073).  A drive root and a share's root were taken for one
/// folder's items (I150).  The New text file name was not checked (I151),
/// and the rename prompt could only pick out the text before the last dot,
/// of a folder's name or a note too (I152).
///
/// <para>The window's checks need the app, which a process has only one of
/// and the Settings checks make later: they run in a process of their own.
/// Nothing here invokes one of the Shell's own items - an item picked is
/// handed over with an id no handler of the menu owns, so where the app
/// does not take it, nothing runs at all.</para>
/// </summary>
internal static partial class Program
{
    /// <summary>Set for the copy of this harness that runs the window's checks.</summary>
    private const string MenuFileOpsWindowVariable = "ULTRAEXPLORER_MENU_FILEOPS_WINDOW";

    private static async Task MenuFileOpsReviewChecks()
    {
        if (Environment.GetEnvironmentVariable(MenuFileOpsWindowVariable) == "1")
        {
            RunOnSta("menus and file operations review: the window", MenuFileOpsWindowOnStaAsync);
            return;
        }

        RunOnSta("menus and file operations review", MenuFileOpsOnStaAsync);
        await MenuFileOpsWindowInOwnProcessAsync();
    }

    private static async Task MenuFileOpsOnStaAsync()
    {
        var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "UltraExplorerMenuFileOps", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var owner = new HwndSource(new HwndSourceParameters("UltraExplorer menu review checks") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        try
        {
            await SameFolderCopyChecksAsync(root);
            NewTextFileNameChecks(root);
            await NameEndOperationChecksAsync(root, owner.Handle);
            MixedRootMenuChecks(owner.Handle);
        }
        finally
        {
            DeleteWithNameEnds(root);
        }
    }

    // ---- I007: a copy pasted into its own folder ----------------------------------------

    /// <summary>
    /// Ctrl+C then Ctrl+V on a file makes "report - Copy.docx" beside it, as
    /// in Explorer; a folder likewise.  A paste that also brings something
    /// from elsewhere copies that as well.  A cut pasted where it already is
    /// stays as it is.
    /// </summary>
    private static async Task SameFolderCopyChecksAsync(string root)
    {
        Section("menus and file operations: a copy pasted into its own folder (I007)");
        var shell = new NativeShellService();
        var work = Path.Combine(root, "Work");
        var report = Path.Combine(work, "report.docx");
        var photos = Path.Combine(work, "Photos");
        Directory.CreateDirectory(photos);
        File.WriteAllText(report, "report");
        File.WriteAllText(Path.Combine(photos, "one.jpg"), "one");

        var failure = await Attempt(() => shell.CopyOrMoveAsync([report, photos], work, move: false));
        Check($"a file and a folder copied into the folder they are in are copied beside themselves ({failure ?? string.Join(", ", Directory.EnumerateFileSystemEntries(work).Select(Path.GetFileName))})",
            failure is null
            && TextOf(Path.Combine(work, "report - Copy.docx")) == "report"
            && File.Exists(Path.Combine(work, "Photos - Copy", "one.jpg"))
            && File.Exists(report) && File.Exists(Path.Combine(photos, "one.jpg")));

        var mixed = Path.Combine(root, "Mixed");
        var elsewhere = Path.Combine(root, "Elsewhere");
        Directory.CreateDirectory(mixed);
        Directory.CreateDirectory(elsewhere);
        var memo = Path.Combine(elsewhere, "memo.txt");
        var notes = Path.Combine(mixed, "notes.txt");
        File.WriteAllText(memo, "memo");
        File.WriteAllText(notes, "notes");
        failure = await Attempt(() => shell.CopyOrMoveAsync([memo, notes], mixed, move: false));
        Check($"a paste of one item from elsewhere and one from the folder itself copies both ({failure ?? string.Join(", ", Directory.EnumerateFileSystemEntries(mixed).Select(Path.GetFileName))})",
            failure is null && TextOf(Path.Combine(mixed, "memo.txt")) == "memo"
            && TextOf(Path.Combine(mixed, "notes - Copy.txt")) == "notes" && File.Exists(memo));

        var before = Directory.EnumerateFileSystemEntries(work).Count();
        failure = await Attempt(() => shell.CopyOrMoveAsync([report], work, move: true));
        Check("a cut pasted into the folder it is in is left as it is",
            failure is null && File.Exists(report) && Directory.EnumerateFileSystemEntries(work).Count() == before);
    }

    // ---- I151: the name of a new text file -------------------------------------------------

    /// <summary>
    /// The New text file name is a file name: a colon, a separator or a
    /// path is refused, as a folder's name is; and a name whose last dot is
    /// not an extension's - "Notes 10.03" - still gets ".txt".
    /// </summary>
    private static void NewTextFileNameChecks(string root)
    {
        Section("menus and file operations: the name of a new text file (I151)");
        var folder = Path.Combine(root, "Notes");
        var outside = Path.Combine(root, "escaped.txt");
        Directory.CreateDirectory(folder);

        string? Made(string name)
        {
            try
            {
                return Path.GetFileName(NativeShellService.CreateNoteFile(folder, name));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        Check("a name with a colon is refused, and no file or stream is made",
            Made("readme:x") is null && !Directory.EnumerateFileSystemEntries(folder).Any());
        Check("a name with a path in it is refused, and nothing is written outside the folder",
            Made(@"..\escaped.txt") is null && Made(outside) is null && Made("sub/escaped.txt") is null
            && !File.Exists(outside) && !Directory.EnumerateFileSystemEntries(folder).Any());
        Check("a name whose last dot is no extension's gets .txt", Made("Notes 10.03") == "Notes 10.03.txt");
        Check("so does a name with a space after its last dot", Made("Plan v2. draft") == "Plan v2. draft.txt");
        Check("a name without a dot gets .txt, and one with an extension keeps it",
            Made("plain") == "plain.txt" && Made("song.md") == "song.md" && Made("archive.7z") == "archive.7z");
        Check("an empty name is the default", Made(" ") == "New note.txt");
    }

    // ---- I044: names that end in a dot or a space ------------------------------------------

    /// <summary>
    /// A folder holds "dup." beside "dup", and "foo " beside "foo".  Windows
    /// drops the dot or the space from a name it is handed, so an operation
    /// on "dup." reaches "dup": each one is refused with a reason, and the
    /// neighbour is left alone.  The Shell parses the name the same way, so
    /// there is no Shell menu for such an item, nor for such a folder's open
    /// space; the app's own menu, whose operations refuse, is shown.
    /// </summary>
    private static async Task NameEndOperationChecksAsync(string root, IntPtr owner)
    {
        Section("menus and file operations: names that end in a dot or a space (I044)");
        var shell = new NativeShellService();
        var folder = Path.Combine(root, "Ends");
        var into = Path.Combine(root, "Into");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(into);
        var dotted = Path.Combine(folder, "dup.");
        var plain = Path.Combine(folder, "dup");
        var spaced = Path.Combine(folder, "foo ");
        var unspaced = Path.Combine(folder, "foo");
        var dottedFolder = Path.Combine(folder, "box.");
        var plainFolder = Path.Combine(folder, "box");
        var source = Path.Combine(root, "paste-me.txt");
        File.WriteAllText(source, "paste");

        // Each operation starts from the same folder: one that went wrong
        // takes nothing with it into the next.
        void Reset()
        {
            DeleteWithNameEnds(folder);
            DeleteWithNameEnds(into);
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(into);
            File.WriteAllText(@"\\?\" + dotted, "dotted");
            File.WriteAllText(plain, "plain");
            File.WriteAllText(@"\\?\" + spaced, "spaced");
            File.WriteAllText(unspaced, "unspaced");
            Directory.CreateDirectory(@"\\?\" + dottedFolder);
            Directory.CreateDirectory(plainFolder);
        }

        bool Intact() => TextOf(plain) == "plain"
            && TextOf(unspaced) == "unspaced"
            && File.Exists(@"\\?\" + dotted) && File.Exists(@"\\?\" + spaced)
            && Directory.Exists(plainFolder) && !Directory.EnumerateFileSystemEntries(plainFolder).Any()
            && Directory.EnumerateFileSystemEntries(folder).Count() == 6 && !Directory.EnumerateFileSystemEntries(into).Any();

        Reset();
        var deleted = await Attempt(() => shell.DeleteAsync([dotted], permanently: true));
        Check($"deleting dup. permanently is refused with a reason, and dup is still there ({deleted})",
            deleted is { Length: > 0 } && deleted.Contains("dup.", StringComparison.Ordinal) && Intact());
        Reset();
        deleted = await Attempt(() => shell.DeleteAsync([spaced], permanently: true));
        Check("so is deleting a name that ends in a space, and its neighbour stays", deleted is { Length: > 0 } && Intact());

        Reset();
        var renamed = await Attempt(() => Task.FromResult(NativeShellService.Rename(dotted, "renamed.txt")));
        Check($"renaming dup. is refused, and dup keeps its name ({renamed})", renamed is { Length: > 0 } && Intact());

        Reset();
        var moved = await Attempt(() => shell.CopyOrMoveAsync([dotted], into, move: true));
        Check($"moving dup. is refused, and dup stays where it is ({moved})", moved is { Length: > 0 } && Intact());
        Reset();
        var pasted = await Attempt(() => shell.CopyOrMoveAsync([source], dottedFolder, move: false));
        Check($"pasting into box. is refused, and nothing lands in box ({pasted})", pasted is { Length: > 0 } && Intact());
        Reset();
        var duplicated = await Attempt(() => shell.DuplicateAsync(dotted));
        Check($"duplicating dup. is refused ({duplicated})", duplicated is { Length: > 0 } && Intact());
        Reset();
        var made = await Attempt(() => Task.FromResult(NativeShellService.CreateFolder(dottedFolder, "inside")));
        var note = await Attempt(() => Task.FromResult(NativeShellService.CreateNoteFile(dottedFolder, "inside")));
        Check("a new folder or text file in box. is refused, and nothing is made in box",
            made is { Length: > 0 } && note is { Length: > 0 } && Intact());

        Reset();
        using (var menu = ShellContextMenu.ForItems([dotted], owner, extended: false, offerNew: true))
        {
            Check("dup. has no Shell menu, which would be dup's", menu is null);
        }

        using (var menu = ShellContextMenu.ForItems([plain, dotted], owner, extended: false, offerNew: true))
        {
            Check("nor does a selection with dup. in it", menu is null);
        }

        using (var menu = ShellContextMenu.ForFolderBackground(dottedFolder, owner, extended: false))
        {
            Check("nor the open space of box., whose New would make things in box", menu is null);
        }

        using (var menu = ShellContextMenu.ForItems([plain], owner, extended: false, offerNew: true))
        {
            Check("dup itself still has the Shell's menu", menu is not null);
        }
    }

    // ---- I150: a drive and a share selected together ---------------------------------------

    /// <summary>
    /// C:\ and \\localhost\C$ have no folder in common for the Shell to make
    /// one menu in: the share was handed to This PC as one of its drives.
    /// Two drives still share This PC's menu (see FolderItemMenuChecks).
    /// </summary>
    private static void MixedRootMenuChecks(IntPtr owner)
    {
        Section("menus and file operations: a drive and a share selected together (I150)");
        const string share = @"\\localhost\C$";
        var reachable = Task.Run(() => Directory.Exists(share));
        if (!reachable.Wait(TimeSpan.FromSeconds(10)) || !reachable.Result)
        {
            Console.WriteLine($"  note  {share} cannot be reached here; a drive and a share are not checked");
            return;
        }

        var drive = Path.GetPathRoot(Environment.SystemDirectory)!;
        using (var menu = ShellContextMenu.ForItems([drive, share], owner, extended: false, offerNew: true))
        {
            Check("a drive and a share's root have no Shell menu between them", menu is null);
        }

        using (var menu = ShellContextMenu.ForItems([share, drive], owner, extended: false, offerNew: true))
        {
            Check("whichever comes first", menu is null);
        }

        using (var menu = ShellContextMenu.ForItems([share], owner, extended: false, offerNew: true))
        {
            Check("a share's root alone has its menu", menu is not null);
        }
    }

    // ---- the window ------------------------------------------------------------------------

    /// <summary>The window's checks, run by this program again with <see cref="MenuFileOpsWindowVariable"/> set: each result is counted here.</summary>
    private static async Task MenuFileOpsWindowInOwnProcessAsync()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add("--only");
        start.ArgumentList.Add(nameof(MenuFileOpsReviewChecks));
        start.Environment[MenuFileOpsWindowVariable] = "1";
        using var child = Process.Start(start)!;
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var finished = false;
        try
        {
            while (await child.StandardOutput.ReadLineAsync(limit.Token) is { } line)
            {
                if (line.StartsWith("  ok    ", StringComparison.Ordinal))
                {
                    Check(line[8..], true);
                }
                else if (line.StartsWith("  FAIL  ", StringComparison.Ordinal))
                {
                    Check(line[8..], false);
                }
                else if (line.EndsWith(" checks passed", StringComparison.Ordinal))
                {
                    finished = true;
                }
                else if (line.Length > 0)
                {
                    Console.WriteLine(line);
                }
            }

            await child.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            try
            {
                child.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Gone by itself meanwhile.
            }

            Check("the menu review's window checks finished within ten minutes", false);
            return;
        }

        Check("the menu review's window checks ran to the end", finished);
    }

    private static async Task MenuFileOpsWindowOnStaAsync()
    {
        new App().InitializeComponent();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var stateOverride = Environment.GetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR");
        var isolated = !string.IsNullOrWhiteSpace(stateOverride)
            && Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1"
            && !Path.GetFullPath(stateOverride).Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer"), StringComparison.OrdinalIgnoreCase);
        Check("the menu review's window checks require isolated state and test-window mode", isolated);
        if (!isolated)
        {
            return;
        }

        // Everything the checks work on is there before the tree first reads
        // the folder: an item made while a check runs could be found gone by a
        // read the folder's watch started a moment before, and let go of.
        var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "UltraExplorerMenuWindow", Guid.NewGuid().ToString("N"));
        foreach (var verb in new[] { "open", "explore" })
        {
            Directory.CreateDirectory(Path.Combine(root, $"Into-{verb}"));
            File.WriteAllText(Path.Combine(root, $"Into-{verb}", "inside.txt"), "inside");
        }

        File.WriteAllText(Path.Combine(root, "result-a.txt"), "a");
        File.WriteAllText(Path.Combine(root, "result-b.txt"), "b");
        File.WriteAllText(Path.Combine(root, "open-me.txt"), "file");
        File.WriteAllText(Path.Combine(root, "on-share.txt"), "share");
        using var owner = new HwndSource(new HwndSourceParameters("UltraExplorer menu review window checks") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        var main = new MainWindow();
        var shell = (MainViewModel)main.DataContext;
        var prompt = typeof(MainViewModel).GetField(nameof(MainViewModel.PromptRequested), BindingFlags.Instance | BindingFlags.NonPublic)!;
        var ownPrompt = prompt.GetValue(shell);
        try
        {
            new WindowInteropHelper(main).EnsureHandle();
            await shell.Tree.InitializeAsync(root);
            await ShellRenameTargetChecksAsync(main, shell, prompt, root, owner.Handle);
            await ShellOpenFolderChecksAsync(main, shell, root, owner.Handle);
            await PressOnShareChecksAsync(main, root);
            RenamePromptSelectionChecks();
        }
        finally
        {
            // Closed as the user closes it - its session saved, its model let
            // go of - while the thread still runs: a window whose thread ends
            // under it can be handed a message it no longer has a tree for.
            prompt.SetValue(shell, ownPrompt);
            var closed = new TaskCompletionSource();
            main.Closed += (_, _) => closed.TrySetResult();
            main.Close();
            await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            TryDelete(root);
        }
    }

    /// <summary>
    /// The Shell's menu of a search result - not the selection - and its
    /// Rename picked: the prompt is for that result, and the result is what
    /// is renamed.  The canvas's selection, another result revealed a moment
    /// ago, is left with its name.
    /// </summary>
    private static async Task ShellRenameTargetChecksAsync(MainWindow main, MainViewModel shell, FieldInfo prompt, string root, IntPtr owner)
    {
        Section("menus and file operations: the Shell menu's Rename on a search result (I016)");
        var first = Path.Combine(root, "result-a.txt");
        var second = Path.Combine(root, "result-b.txt");
        var renamed = Path.Combine(root, "result-b-renamed.txt");
        await shell.Tree.RevealPathAsync(first);
        var selected = shell.Tree.Selection.Count == 1 && shell.Tree.Selection.Contains(first);

        var asked = new List<string>();
        prompt.SetValue(shell, (Func<string, string, string, bool, string?>)((_, _, initial, _) =>
        {
            asked.Add(initial);
            return Path.GetFileName(renamed);
        }));

        using var menu = ShellContextMenu.ForItems([second], owner, extended: false, offerNew: false);
        if (menu is null)
        {
            Check("the Shell's menu of a search result to pick Rename on", false);
            return;
        }

        // An id no handler of this menu owns: were the Shell's item run, nothing would happen.
        var toast = shell.Toast.Message;
        main.RunShellChoice(menu, new ShellMenuChoice(ShellMenuPick.Shell, 0x7000, "rename", false, null), 0, 0);
        var done = await LiveWait(() => File.Exists(renamed) || !File.Exists(first), 5_000) >= 0;

        // The rename then shows what it renamed on the canvas; the checks after
        // these go somewhere else, and must not race it there.
        await LiveWait(() => shell.Toast.Message != toast, 10_000);
        Check($"result A selected, Rename on result B's menu asks about B ({string.Join(", ", asked)})",
            selected && asked is [var name] && name == Path.GetFileName(second));
        Check("and renames B, not A",
            done && TextOf(renamed) == "b" && File.Exists(first) && !File.Exists(second));
    }

    /// <summary>
    /// Open and Explore on a folder's Shell menu go into the folder in this
    /// window, as a double-click does - the Shell has no window of
    /// Explorer's to open it in, and would hand it to whatever opens folders.
    /// A file's Open is still the Shell's.
    /// </summary>
    private static async Task ShellOpenFolderChecksAsync(MainWindow main, MainViewModel shell, string root, IntPtr owner)
    {
        Section("menus and file operations: Open on a folder's Shell menu (I073)");
        foreach (var verb in new[] { "open", "explore" })
        {
            var folder = Path.Combine(root, $"Into-{verb}");
            using var menu = ShellContextMenu.ForItems([folder], owner, extended: false, offerNew: false);
            if (menu is null)
            {
                Check($"a folder's Shell menu to pick {verb} on", false);
                continue;
            }

            main.RunShellChoice(menu, new ShellMenuChoice(ShellMenuPick.Shell, 0x7000, verb, false, null), 0, 0);
            var went = await LiveWait(() => shell.Tree.Selection.Focus is { } focus && ViewAllPath.Equals(focus, folder), 10_000) >= 0;
            Check($"'{verb}' on a folder goes into it in this window ({Path.GetFileName(shell.Tree.Selection.Focus)})", went);
        }

        var file = Path.Combine(root, "open-me.txt");
        using (var menu = ShellContextMenu.ForItems([file], owner, extended: false, offerNew: false))
        {
            if (menu is not null)
            {
                main.RunShellChoice(menu, new ShellMenuChoice(ShellMenuPick.Shell, 0x7000, "open", false, null), 0, 0);
            }

            var gone = await LiveWait(() => shell.Tree.Selection.Focus is { } focus && ViewAllPath.Equals(focus, file), 1_000) >= 0;
            Check("'open' on a file is left to the Shell: the window does not go to it", menu is not null && !gone);
        }
    }

    /// <summary>
    /// A right press on an item of a share, or of a drive mapped to one, does
    /// not build the Shell's menu while the button is down - a share that has
    /// gone away holds the window there for the network's timeout, and the
    /// press may only be the start of a pan.  A local item's still is.
    /// </summary>
    private static async Task PressOnShareChecksAsync(MainWindow main, string root)
    {
        Section("menus and file operations: a right press on a share (I033)");
        var file = Path.Combine(root, "on-share.txt");
        var prepared = typeof(MainWindow).GetField("_preparedMenu", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var drop = typeof(MainWindow).GetMethod("DropPreparedMenu", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var letter = char.ToUpperInvariant(root[0]);
        try
        {
            VolumeKinds.ResolveLetter = asked => asked == letter ? @"\\menu-review-server\share" : null;
            main.PrepareShellMenu(background: false, [file]);
            await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check("a right press on an item of a drive mapped to a share builds no Shell menu", prepared.GetValue(main) is null);
            main.PrepareShellMenu(background: true, [root]);
            await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check("nor on its open space", prepared.GetValue(main) is null);
        }
        finally
        {
            VolumeKinds.ResolveLetter = null;
            drop.Invoke(main, null);
        }

        main.PrepareShellMenu(background: false, [file]);
        // The saved review2 policy waits 50 ms so the press can become a pan.
        await Task.Delay(80);
        await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Check("a right press on a local item still builds its menu while the button is down", prepared.GetValue(main) is not null);
        drop.Invoke(main, null);
    }

    /// <summary>
    /// The prompt picks out a file's name without its extension, so typing
    /// replaces the name; a folder's name or a note it picks out whole, so
    /// "Photos 2024.06" renamed by typing "Archive" is "Archive".
    /// </summary>
    private static void RenamePromptSelectionChecks()
    {
        Section("menus and file operations: what the rename prompt picks out (I152)");
        (int Start, int Length) Picked(InputDialog dialog)
        {
            dialog.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            return (dialog.ValueTextBox.SelectionStart, dialog.ValueTextBox.SelectionLength);
        }

        Check("a file's name is picked out without its extension",
            Picked(new InputDialog("Rename", "Enter a new name", "report.docx")) == (0, "report".Length));
        Check("a folder's name is picked out whole, dot and all",
            Picked(new InputDialog("Rename", "Enter a new name", "Photos 2024.06", selectStem: false)) == (0, "Photos 2024.06".Length));
        Check("and so is a note",
            Picked(new InputDialog("Note", "Note for Photos", "Due 10.03, then archive", selectStem: false)) == (0, "Due 10.03, then archive".Length));
    }

    // ---- helpers ---------------------------------------------------------------------------

    /// <summary>What <paramref name="operation"/> failed with - its message - or null when it did not.</summary>
    private static async Task<string?> Attempt<T>(Func<T> operation) where T : Task
    {
        try
        {
            await operation();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message.Length > 0 ? ex.Message : ex.GetType().Name;
        }
    }

    /// <summary>What a file holds, or null when it is not there.</summary>
    private static string? TextOf(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    /// <summary>Deletes a test folder whose names may end in a dot or a space, which only the long form reaches.</summary>
    private static void DeleteWithNameEnds(string root)
    {
        try
        {
            Directory.Delete(@"\\?\" + root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
