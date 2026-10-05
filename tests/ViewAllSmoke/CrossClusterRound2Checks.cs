using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// What the second round of the review put right across more than one part
/// of the program: each fix needed a file of another part than the one whose
/// issue it was.
/// A crumb's chevron left hidden folders out whatever the window showed (I078).
///
/// <para>The windows need the app, of which a process can only ever have the
/// one, on the thread that made it: these checks always run in a process of
/// their own, so that the Settings checks after them still make theirs.  The
/// windows that are shown open on a monitor that is not the primary one,
/// cloaked and never active.</para>
/// </summary>
internal static partial class Program
{
    private static async Task CrossClusterRound2Checks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(CrossClusterRound2Checks), StringComparison.OrdinalIgnoreCase))
        {
            await CrossRound2InOwnProcessAsync();
            return;
        }

        RunOnSta("cross-cluster review, round 2", CrossRound2OnStaAsync);
    }

    /// <summary>
    /// The checks run by this program again, alone, in a process of their
    /// own: each of its results is counted here.  One that hangs is ended
    /// after ten minutes and counted as a failure, so the rest of the run
    /// goes on.
    /// </summary>
    private static async Task CrossRound2InOwnProcessAsync()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!, "--only " + nameof(CrossClusterRound2Checks))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
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

            Check("the round-2 cross-cluster checks' own process finished within ten minutes", false);
            return;
        }

        Check("the round-2 cross-cluster checks' own process ran to the end", finished);
    }

    private static async Task CrossRound2OnStaAsync()
    {
        new App().InitializeComponent();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var stateOverride = Environment.GetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR");
        var isolated = !string.IsNullOrWhiteSpace(stateOverride)
            && Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1"
            && !Path.GetFullPath(stateOverride).Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer"), StringComparison.OrdinalIgnoreCase);
        Check("the round-2 cross-cluster checks require isolated state and test-window mode", isolated);
        if (!isolated)
        {
            return;
        }

        // The windows read the drives as the app does; what that throws on
        // the interface thread outside these checks is written down and
        // survived, rather than ending them half way.
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
        {
            Console.WriteLine($"  note: the interface thread threw, outside these checks: {e.Exception}");
            e.Handled = true;
        };

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerCrossRound2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (ActivationGuard.GuardWindowsCreated())
            {
                await Round2ChevronHiddenChecksAsync(root);
                await Round2PromptSelectionChecksAsync(root);
                await Round2CopyGoneChecksAsync(root);
                Round2ConfirmButtonChecks();
                await Round2UnshownSelectionChecksAsync(root);
                await Round2SwitchOrderChecksAsync();
                await Round2OperationExistsChecksAsync(root);
                Round2RecoverVerbChecks();
                await Round2StaleTemporaryChecksAsync(root);
                await Round2CloseSaveChecksAsync(root);
                await Round2ExplorerByNameChecksAsync(root);
                await Round2AbandonedHandoffChecksAsync();
                await Round2DiscardWindowChecksAsync(root);

                // Last: it leaves the crash reporter installed in this process.
                await Round2QuietSurvivalChecksAsync();
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- a crumb's chevron follows the window's hidden items (I078) -------------------

    /// <summary>
    /// The window's address bar is told whether the window shows hidden
    /// items: a crumb's chevron lists hidden and system folders when it does,
    /// and neither when it does not, where it left out system folders alone
    /// whatever the window showed.
    /// </summary>
    private static async Task Round2ChevronHiddenChecksAsync(string root)
    {
        Section("cross-cluster round 2: a crumb's chevron follows the window's hidden items (I078)");
        var veiled = Path.Combine(root, "veiled");
        Directory.CreateDirectory(Path.Combine(veiled, "plain"));
        File.SetAttributes(Directory.CreateDirectory(Path.Combine(veiled, "hidden")).FullName, FileAttributes.Directory | FileAttributes.Hidden);
        File.SetAttributes(Directory.CreateDirectory(Path.Combine(veiled, "system")).FullName, FileAttributes.Directory | FileAttributes.System);
        static string[] Names(IEnumerable<AddressSuggestion> found) => found.Select(item => item.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();

        using var shell = new MainViewModel(Path.Combine(root, "chevron.view-all.json"));
        shell.Tree.RestoreShowHiddenItems(false);
        shell.Address.SetPath(veiled);
        var last = shell.Address.Breadcrumbs[^1];
        shell.Address.ToggleSegmentMenuCommand.Execute(last);
        await LiveWait(() => last.IsMenuOpen, 5_000);
        Check($"with hidden items off, a chevron leaves hidden and system folders out ({string.Join(", ", Names(last.Children))})",
            Names(last.Children).SequenceEqual(["plain"]));

        last.IsMenuOpen = false;
        shell.Tree.RestoreShowHiddenItems(true);
        shell.Address.ToggleSegmentMenuCommand.Execute(last);
        await LiveWait(() => last.IsMenuOpen, 5_000);
        Check($"with them on, it lists them both ({string.Join(", ", Names(last.Children))})",
            Names(last.Children).SequenceEqual(["hidden", "plain", "system"]));
        last.IsMenuOpen = false;
    }

    // ---- a prompt picks out a folder's or a note's whole text (I152) ------------------

    /// <summary>
    /// Each question the window asks says whether its text is a file's name,
    /// whose extension typing keeps: renaming "Photos 2024.06" by typing
    /// "Archive" gave "Archive.06", and a note's text was picked out up to
    /// its last dot.  Every prompt is answered here; no dialog is shown.
    /// </summary>
    private static async Task Round2PromptSelectionChecksAsync(string root)
    {
        Section("cross-cluster round 2: a prompt picks out a folder's or a note's whole text, and a file's name without its extension (I152)");
        var folder = Path.Combine(root, "prompts");
        var dotted = Path.Combine(folder, "Photos 2024.06");
        var letter = Path.Combine(folder, "letter.txt");
        Directory.CreateDirectory(dotted);
        File.WriteAllText(letter, "x");

        // A file dialog's model: nothing of it is written back.
        using var model = new MainViewModel(Path.Combine(root, "prompts.view-all.json"));
        await model.InitializeAsync(folder);
        var asked = new List<(string Title, bool Stem)>();
        model.PromptRequested += (title, _, _, stem) =>
        {
            asked.Add((title, stem));
            return null;
        };

        await model.Tree.RevealPathAsync(folder);
        await LiveWait(() => ViewAllPath.Equals(model.Tree.TargetDirectory ?? string.Empty, folder), 5_000);
        model.NewFolderCommand.Execute(null);
        await LiveWait(() => asked.Count == 1, 5_000);
        model.NewTextFileCommand.Execute(null);
        await LiveWait(() => asked.Count == 2, 5_000);

        void Select(string path, bool isDirectory) => model.Tree.Selection.Apply(new SelectionEdit
        {
            Clear = true,
            Container = folder,
            Added = [new SelectionItem(path, isDirectory, 1)],
            Source = SelectionSource.Canvas
        });

        Select(dotted, true);
        model.RenameCommand.Execute(null);
        await LiveWait(() => asked.Count == 3, 5_000);
        Select(letter, false);
        model.RenameCommand.Execute(null);
        await LiveWait(() => asked.Count == 4, 5_000);
        model.EditNoteOf(dotted, Path.GetFileName(dotted));

        Check($"every prompt was asked ({string.Join(", ", asked.Select(item => $"{item.Title}: {item.Stem}"))})", asked.Count == 5);
        Check("a new folder's name is picked out whole", asked is [("New folder", false), ..]);
        Check("a new text file's name without its extension", asked is [_, ("New text file", true), ..]);
        Check("a folder renamed has its whole name picked out, dots and all", asked is [_, _, ("Rename", false), ..]);
        Check("a file renamed, its name without its extension", asked is [_, _, _, ("Rename", true), ..]);
        Check("a note has its whole text picked out", asked is [.., ("Note", false)]);
    }

    // ---- Ctrl+C on items that are gone says so (I153) --------------------------------

    /// <summary>
    /// Copy or Cut of selected items deleted meanwhile from outside: nothing
    /// reaches the clipboard, and the message says the items are gone, where
    /// it said another application held the clipboard - which trying again
    /// never helps.
    /// </summary>
    private static async Task Round2CopyGoneChecksAsync(string root)
    {
        Section("cross-cluster round 2: Copy of items that are gone says they are gone (I153)");
        var folder = Path.Combine(root, "copy-gone");
        Directory.CreateDirectory(folder);
        var gone = new[] { Path.Combine(folder, "report.txt"), Path.Combine(folder, "summary.txt") };

        // A file dialog's model: nothing of it is written back.
        using var model = new MainViewModel(Path.Combine(root, "copy-gone.view-all.json"));
        await model.InitializeAsync(folder);
        void Select(IEnumerable<string> paths) => model.Tree.Selection.Apply(new SelectionEdit
        {
            Clear = true,
            Container = folder,
            Added = [.. paths.Select(path => new SelectionItem(path, false, 1))],
            Source = SelectionSource.Canvas
        });

        Select(gone.Take(1));
        model.CopyCommand.Execute(null);
        await Until(() => model.CopyCommand.CanExecute(null), 5_000);
        Check($"Copy of one file that is gone names it ({model.Toast.Message})",
            model.Toast.Message == "report.txt is no longer there.");

        Select(gone);
        model.CutCommand.Execute(null);
        await Until(() => model.CutCommand.CanExecute(null), 5_000);
        Check($"Cut of several says they are gone ({model.Toast.Message})",
            model.Toast.Message == "The selected items are no longer there.");
    }

    // ---- a question that opens files is not drawn as a deletion (I075) ---------------

    /// <summary>
    /// The window's question before something is done: its confirming button
    /// is red only for a deletion.  "Open all 16 selected files?" was asked
    /// with a red Delete button that opened them.  The questions themselves
    /// and their labels are MainViewModelReviewChecks'; here the dialog.
    /// </summary>
    private static void Round2ConfirmButtonChecks()
    {
        Section("cross-cluster round 2: only a deletion's question has a red button (I075)");
        var open = new UltraExplorer.Dialogs.ConfirmDialog("Open", "Open all 16 selected files?", "Open", danger: false);
        var delete = new UltraExplorer.Dialogs.ConfirmDialog("Delete", "Delete all 3?", "Delete", danger: true);
        try
        {
            var danger = Application.Current.FindResource("DangerBrush");
            Check("a deletion's button is red",
                ReferenceEquals(delete.ConfirmButton.Background, danger) && (string)delete.ConfirmButton.Content == "Delete");
            Check($"the button that opens files is the usual accent button, labelled Open ({open.ConfirmButton.Background})",
                !ReferenceEquals(open.ConfirmButton.Background, danger)
                && open.ConfirmButton.ReadLocalValue(System.Windows.Controls.Control.BackgroundProperty) == DependencyProperty.UnsetValue
                && open.ConfirmButton.ReadLocalValue(System.Windows.Controls.Control.ForegroundProperty) == DependencyProperty.UnsetValue
                && (string)open.ConfirmButton.Content == "Open");
        }
        finally
        {
            open.Close();
            delete.Close();
        }
    }

    // ---- Delete with selected items the canvas leaves out (I105) ----------------------

    /// <summary>
    /// The window tells Delete how many selected items its canvas shows.
    /// Three files selected in a folder the canvas has read, one of them
    /// hidden while hidden items are hidden: Delete names the one not shown
    /// and asks first.  Files selected in a folder the canvas has not read -
    /// a search result elsewhere, a folder the list went into - are not
    /// known to be left out, and ask nothing more than ever.  Every question
    /// is answered No here, so nothing is deleted.
    /// </summary>
    private static async Task Round2UnshownSelectionChecksAsync(string root)
    {
        Section("cross-cluster round 2: Delete asks first when the canvas leaves selected items out (I105)");
        var drive = Path.Combine(root, "unshown-drive");
        var shown = Path.Combine(drive, "shown");
        var unread = Path.Combine(drive, "unread");
        Directory.CreateDirectory(shown);
        Directory.CreateDirectory(unread);
        var files = new[] { "a.txt", "b.txt", "desktop.ini" }.Select(name => Path.Combine(shown, name)).ToArray();
        foreach (var file in files)
        {
            File.WriteAllText(file, "x");
        }

        File.SetAttributes(files[2], FileAttributes.Hidden);
        var elsewhere = new[] { "c.txt", "d.txt" }.Select(name => Path.Combine(unread, name)).ToArray();
        foreach (var file in elsewhere)
        {
            File.WriteAllText(file, "x");
        }

        var main = ProxyWindow(out var shell);
        try
        {
            await main.StartNestedForChecksAsync();
            main.UseNestedDrivesForChecks([new NestedRoot(drive, "U", NestedFolderKind.Drive)]);
            var pane = main.FirstPane;
            await pane.FlyToAsync(shown, gentle: false, animated: false, isDirectory: true);
            if (await pane.Tree.MaterializePathAsync(shown) is not { } folder)
            {
                Check("the canvas finds the folder the files are in", false);
                return;
            }

            await pane.Tree.LoadAsync(folder);
            // The window's own dialogs are answered here: none is ever shown.
            var asked = new List<string>();
            typeof(MainViewModel).GetField(nameof(MainViewModel.ConfirmRequested), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(shell, new Func<string, string, string, bool>((title, _, _) =>
                {
                    asked.Add(title);
                    return false;
                }));

            void Select(string container, IEnumerable<string> paths) => shell.Tree.Selection.Apply(new SelectionEdit
            {
                Clear = true,
                Container = container,
                Added = [.. paths.Select(path => new SelectionItem(path, false, 1))],
                Source = SelectionSource.Command
            });

            Select(shown, files);
            shell.PermanentDeleteCommand.Execute(null);
            await LiveWait(() => asked.Count > 0 && shell.PermanentDeleteCommand.CanExecute(null), 10_000);
            Check($"with a hidden file among three selected, Delete first asks about the one not shown ({string.Join(", ", asked)})",
                asked is ["Delete"]);

            asked.Clear();
            Select(unread, elsewhere);
            shell.PermanentDeleteCommand.Execute(null);
            await LiveWait(() => asked.Count > 0 && shell.PermanentDeleteCommand.CanExecute(null), 10_000);
            Check($"files in a folder the canvas has not read ask only the usual question ({string.Join(", ", asked)})",
                asked is ["Permanently delete"]);

            // A folder with more files than the canvas lists - fifty thousand -
            // cannot say which of its selected files it leaves out.
            asked.Clear();
            folder.IsTruncated = true;
            Select(shown, files);
            shell.PermanentDeleteCommand.Execute(null);
            await LiveWait(() => asked.Count > 0 && shell.PermanentDeleteCommand.CanExecute(null), 10_000);
            folder.IsTruncated = false;
            Check($"nor do files in a folder too large for the canvas to list whole ({string.Join(", ", asked)})",
                asked is ["Permanently delete"]);
            Check("nothing was deleted", files.Concat(elsewhere).All(File.Exists));
        }
        finally
        {
            shell.Dispose();
        }
    }

    // ---- Restore and pause waits for a switch still being applied (I164) --------------

    /// <summary>
    /// Replacement switched on while another window holds the integration
    /// settings for a moment, then "Restore and pause" pressed before that is
    /// through: the pause is the last thing asked, and is what is left.  The
    /// pause found the mode still off, did nothing, and the On written after
    /// it switched the mode on against it.  Nothing registers anything here:
    /// this is a test copy, and every registration is left alone.
    /// </summary>
    private static async Task Round2SwitchOrderChecksAsync()
    {
        Section("cross-cluster round 2: Restore and pause waits for a switch still being applied (I164)");
        Directory.CreateDirectory(AppPaths.StateDirectory);
        var path = DialogIntegrationStore.SettingsPath;
        var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new DialogIntegrationSettings { Enabled = false }));
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(false, @"Local\UltraExplorer.DialogSettings." + DialogIntegrationStore.InstanceKey);
            mutex.WaitOne();
            held.Set();
            release.Wait(TimeSpan.FromSeconds(2));
            mutex.ReleaseMutex();
        }) { IsBackground = true, Name = "another window's settings change" };
        try
        {
            holder.Start();
            held.Wait(TimeSpan.FromSeconds(5));
            var controller = DialogIntegrationController.Shared;
            var on = controller.SetEnabledAsync(true);
            await Task.Delay(150);
            var recover = controller.RecoverAsync();
            await Task.Delay(300);
            release.Set();
            await Task.WhenAll(on, recover).WaitAsync(TimeSpan.FromSeconds(20));
            Check($"the pause asked last is what is left ({(DialogIntegrationStore.Read().Enabled ? "on" : "off")})",
                !DialogIntegrationStore.Read().Enabled);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            Check($"the switch and the pause finished ({exception.Message})", false);
        }
        finally
        {
            release.Set();
            holder.Join(TimeSpan.FromSeconds(5));
            if (before is null)
            {
                File.Delete(path);
            }
            else
            {
                File.WriteAllBytes(path, before);
            }
        }
    }

    // ---- a copy, a move or a delete asks the disk off the UI thread (I030) -----------

    /// <summary>
    /// The Shell's own copy, move and delete ask whether each item they were
    /// given is still there on their own thread: on the UI thread, thousands
    /// of items on a share held up every window of the process for seconds,
    /// minutes over a VPN.  Every item is answered gone here, so the Shell is
    /// never asked to do anything, and no folder is made for a copy of
    /// nothing.
    /// </summary>
    private static async Task Round2OperationExistsChecksAsync(string root)
    {
        Section("cross-cluster round 2: a copy, a move or a delete asks whether its items are there off the UI thread (I030)");
        var ui = Thread.CurrentThread;
        var asked = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        var exists = NativeShellService.OperationItemExists;
        NativeShellService.OperationItemExists = _ =>
        {
            asked.Enqueue(Thread.CurrentThread == ui);
            return false;
        };
        try
        {
            var folder = Path.Combine(root, "operations");
            var target = Path.Combine(folder, "target");
            var items = Enumerable.Range(0, 3).Select(index => Path.Combine(folder, $"item{index}.txt")).ToArray();
            var shell = new NativeShellService();
            await shell.CopyOrMoveAsync(items, target, move: false).WaitAsync(TimeSpan.FromSeconds(10));
            await shell.CopyOrMoveAsync(items, target, move: true).WaitAsync(TimeSpan.FromSeconds(10));
            await shell.DeleteAsync(items, permanently: true).WaitAsync(TimeSpan.FromSeconds(10));
            Check($"each item was asked about by copy, move and delete ({asked.Count} asked)", asked.Count == 9);
            Check($"none on the UI thread ({asked.Count(onUi => onUi)} on it)", asked.All(onUi => !onUi));
            Check("a copy of nothing makes no folder for it", !Directory.Exists(target));
        }
        finally
        {
            NativeShellService.OperationItemExists = exists;
        }
    }

    // ---- --dialog-recover and the uninstaller give folders back to Windows (I093) -----

    /// <summary>
    /// <c>--dialog-recover</c> - the uninstaller's last step - puts Windows'
    /// folder-opening defaults back even when the settings already say the
    /// mode is off.  With the state folder deleted while it was on, the
    /// receipt went with it: the settings read off, nothing was put back, and
    /// after the uninstall every folder double-click named a deleted program.
    /// What would be put back is only asked for here; nothing is registered.
    /// </summary>
    private static void Round2RecoverVerbChecks()
    {
        Section("cross-cluster round 2: --dialog-recover gives folders back to Windows even when the mode reads off (I093)");
        Directory.CreateDirectory(AppPaths.StateDirectory);
        var path = DialogIntegrationStore.SettingsPath;
        var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new DialogIntegrationSettings { Enabled = false }));
        var asked = new List<bool>();
        var reconcile = DialogIntegrationRuntime.ReconcileFolderVerbs;
        DialogIntegrationRuntime.ReconcileFolderVerbs = enabled => asked.Add(enabled);
        try
        {
            DialogIntegrationRuntime.Recover();
            Check($"the folder verbs are put back ({string.Join(", ", asked)})", asked is [false]);
            Check("and the mode stays off", !DialogIntegrationStore.Read().Enabled);
        }
        finally
        {
            DialogIntegrationRuntime.ReconcileFolderVerbs = reconcile;
            if (before is null)
            {
                File.Delete(path);
            }
            else
            {
                File.WriteAllBytes(path, before);
            }
        }
    }

    // ---- a save cut off leaves no temporary file behind for good (I141) ---------------

    /// <summary>
    /// A picker that answered its caller, or a worker that was ended, while
    /// its save was being written leaves the save's temporary file beside the
    /// state file: six of 622,592 bytes each sat in the user's folder.  Each
    /// store deletes its own as it loads, once they are old enough that no
    /// write can still be under way, and nothing else.
    /// </summary>
    private static async Task Round2StaleTemporaryChecksAsync(string root)
    {
        Section("cross-cluster round 2: the stores clear the temporary files a cut-off save left behind (I141)");
        var folder = Path.Combine(root, "stale-temporaries");
        Directory.CreateDirectory(folder);
        string Temporary(string name, bool old)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, "partial");
            if (old)
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
            }

            return path;
        }

        string Id() => Guid.NewGuid().ToString("N");
        string[] stale =
        [
            Temporary($".marks.json.{Id()}.tmp", old: true),
            Temporary($".tree.json.{Id()}.tmp", old: true),
            Temporary($"workspace.json.{Id()}.tmp", old: true)
        ];
        string[] kept =
        [
            Temporary($".marks.json.{Id()}.tmp", old: false),
            Temporary($"workspace.json.{Id()}.tmp", old: false),
            Temporary("marks.json.notes.tmp", old: true),
            Temporary($".other.json.{Id()}.tmp", old: true),
            Temporary($".workspace.json.{Id()}.tmp", old: true),
            Temporary($"settings.json.{Id()}.tmp", old: true)
        ];

        await new FolderMarkService(Path.Combine(folder, "marks.json")).LoadAsync();
        await new ViewAllWorkspaceStore(Path.Combine(folder, "tree.json")).LoadAsync();
        await new WorkspaceStore(Path.Combine(folder, "workspace.json")).LoadAsync();
        var cleared = await LiveWait(() => !stale.Any(File.Exists), 5_000) >= 0;
        Check($"each store deletes its own temporary files left from long ago ({stale.Count(File.Exists)} of {stale.Length} left)", cleared);
        Check($"a recent one, still being written, and every other file stay ({string.Join(", ", kept.Where(path => !File.Exists(path)).Select(Path.GetFileName))})",
            kept.All(File.Exists));
    }

    /// <summary>
    /// A window closed as a picker that has answered is: its close-time save
    /// is written before the process is let go.  Application.Shutdown called
    /// straight after the answer ended the dispatcher with the save half
    /// written.  The wait comes back once the window has closed, its save
    /// on disk.
    /// </summary>
    private static async Task Round2CloseSaveChecksAsync(string root)
    {
        Section("cross-cluster round 2: the process waits for a closing window's save before it ends (I141)");
        var main = ProxyWindow(out _);
        new System.Windows.Interop.WindowInteropHelper(main).EnsureHandle();
        var closed = false;
        main.Closed += (_, _) => closed = true;
        main.Close();
        var closing = Application.Current.Windows.OfType<MainWindow>().Contains(main) && main.IsFolderWindowClosing;
        await MainWindow.WhenClosingWindowsClosedAsync(Application.Current, TimeSpan.FromSeconds(10));
        Check($"a window saving on its way out is waited for ({(closing ? "it was closing" : "it was not closing")})", closing && closed);
        Check("and nothing is waited for once every window has closed",
            MainWindow.WhenClosingWindowsClosedAsync(Application.Current, TimeSpan.FromSeconds(10)).IsCompleted);
    }

    // ---- "Show in File Explorer" shows Windows Explorer (I036) -------------------------

    /// <summary>
    /// With folders opening through UltraExplorer, the commands that name
    /// Explorer - "Show in File Explorer", "Open in Windows Explorer" - start
    /// Windows Explorer itself, and the frame it opens for them is marked to
    /// stay with Windows before the observer would hand it over.  They went
    /// to the window instead: navigated it, forced its layout and layers.
    /// No Explorer is started here: the start is answered by a hidden window
    /// of Explorer's class in this process, as Explorer's new frame.
    /// </summary>
    private static async Task Round2ExplorerByNameChecksAsync(string root)
    {
        Section("cross-cluster round 2: the commands that name Explorer show Windows Explorer (I036)");
        var folder = Path.Combine(root, "shown in explorer");
        var file = Path.Combine(folder, "report.txt");
        var other = Path.Combine(folder, "summary.txt");
        Directory.CreateDirectory(folder);
        File.WriteAllText(file, "x");
        File.WriteAllText(other, "x");
        Directory.CreateDirectory(AppPaths.StateDirectory);
        var settings = DialogIntegrationStore.SettingsPath;
        var before = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        File.WriteAllText(settings, System.Text.Json.JsonSerializer.Serialize(new DialogIntegrationSettings { Enabled = true }));

        var started = new List<string>();
        var frames = new List<nint>();
        var startExplorer = ExplorerLaunchRouter.StartExplorer;
        ExplorerLaunchRouter.StartExplorer = start =>
        {
            started.Add(start.Arguments);
            frames.Add(Round2ExplorerFrame.Create());
            return null;
        };

        // The window a routed folder would go to: never shown, it would only
        // wait for its start, and say it has a folder request pending.
        var main = ProxyWindow(out var shell);
        var mainBefore = Application.Current.MainWindow;
        Application.Current.MainWindow = main;
        try
        {
            async Task<bool> Marked(int index) => await LiveWait(() => frames.Count > index
                && ExplorerWindowInterop.GetWindowThreadProcessId(frames[index], out var process) != 0
                && NativeExplorerSessionMarker.IsMarked(frames[index], process, Process.GetCurrentProcess().StartTime.ToUniversalTime()), 3_000) >= 0;

            shell.ShowInExplorer(file);
            Check($"Show in File Explorer on a file starts Explorer with it selected ({string.Join(" | ", started)})",
                started is [var selectFile] && selectFile == "/select,\"" + file + "\"");
            Check("and the frame Explorer opens for it stays with Windows", await Marked(0));

            shell.Address.SetPath(folder);
            shell.Address.OpenInExplorerCommand.Execute(null);
            Check($"Open in Windows Explorer starts Explorer on the folder ({string.Join(" | ", started)})",
                started is [_, var openFolder] && openFolder == "\"" + folder + "\"");
            Check("and its frame stays with Windows too", await Marked(1));

            shell.Tree.Selection.Apply(new SelectionEdit
            {
                Clear = true,
                Container = folder,
                Added = [new SelectionItem(file, false, 1), new SelectionItem(other, false, 1)],
                Source = SelectionSource.Command
            });
            shell.ShowInExplorerCommand.Execute(null);
            Check($"with several items selected, Explorer is started on the first ({string.Join(" | ", started)})",
                started is [_, _, var selectFirst] && selectFirst == "/select,\"" + file + "\"");
            Check("and none of them was sent to the window", !main.IsFolderInvocationPending);
        }
        finally
        {
            ExplorerLaunchRouter.StartExplorer = startExplorer;
            Application.Current.MainWindow = mainBefore;
            foreach (var frame in frames)
            {
                Round2ExplorerFrame.Destroy(frame);
            }

            shell.Dispose();
            if (before is null)
            {
                File.Delete(settings);
            }
            else
            {
                File.WriteAllBytes(settings, before);
            }
        }
    }

    // ---- a handoff given up closes the window it opened (I090) ------------------------

    /// <summary>
    /// An Explorer window being handed over: its UltraExplorer window has
    /// opened, and the handoff is then given up - the user went on in the
    /// Explorer window, or closed it.  That window is to be closed, unless the
    /// user has used it; it stayed beside Explorer's as a second one nobody
    /// asked for.  A handoff that goes through, or that never asked for a
    /// window, closes none.  Window handles here are not windows: nothing
    /// can reach a real Explorer frame.
    /// </summary>
    private static async Task Round2AbandonedHandoffChecksAsync()
    {
        Section("cross-cluster round 2: an Explorer handoff given up closes the window it opened, if unused (I090)");
        var folder = Path.Combine(Path.GetTempPath(), "UltraExplorerRound2Handoff");
        var snapshot = new ShellFolderSnapshot(0x7FF0_0021, 0x7FF0_0023, 0x7FF0_0025, 4, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 6,
            folder, new byte[] { 2, 0, 0, 0 }, Array.AsReadOnly(Array.Empty<string>()), null, 1);

        async Task<(List<FolderInvocation> Opened, List<FolderInvocation> Discarded, int Closed)> RunAsync(ShellFolderSnapshot source, Func<int, bool> sourceValid)
        {
            var opened = new List<FolderInvocation>();
            var discarded = new List<FolderInvocation>();
            var closed = 0;
            var validations = 0;
            var actions = new ExplorerTransferActions(
                () => true,
                (_, _) => Task.FromResult(sourceValid(++validations)),
                (invocation, _) =>
                {
                    opened.Add(invocation);
                    return Task.FromResult<FolderRouteReceipt?>(new FolderRouteReceipt(Guid.NewGuid(), true, true, 1, 1, 1,
                        invocation.DestinationId, invocation.FolderPath, Array.Empty<string>()));
                },
                (_, _, _) => Task.FromResult(true),
                _ => true,
                _ => { closed++; return true; },
                Timeout: TimeSpan.FromSeconds(5),
                DiscardDestination: invocation =>
                {
                    discarded.Add(invocation);
                    return Task.CompletedTask;
                });
            using var coordinator = new ExplorerReplacementCoordinator(actions);
            await coordinator.TransferForChecksAsync(source).WaitAsync(TimeSpan.FromSeconds(10));
            // Keep the assertion, but allow the coordinator's real 600 ms
            // observer-debounce grace to elapse before checking retirement.
            await LiveWait(() => discarded.Count > 0, 2000);
            return (opened, discarded, closed);
        }

        // The Explorer window changed while its UltraExplorer window was being made.
        var abandoned = await RunAsync(snapshot, validation => validation == 1);
        Check($"given up after its window opened, that window is to be closed ({abandoned.Discarded.Count})",
            abandoned.Opened.Count == 1 && abandoned.Closed == 0
            && abandoned.Discarded is [var discard] && discard.DestinationId == abandoned.Opened[0].DestinationId);

        var handedOff = await RunAsync(snapshot with { RootHwnd = 0x7FF0_0031 }, _ => true);
        Check("a handoff that goes through closes none", handedOff.Closed == 1 && handedOff.Discarded.Count == 0);

        var neverOpened = await RunAsync(snapshot with { RootHwnd = 0x7FF0_0041 }, _ => false);
        Check("nor does one given up before it asked for a window", neverOpened.Opened.Count == 0 && neverOpened.Discarded.Count == 0);
    }

    /// <summary>
    /// The broker's side: a window it made for a handoff closes when the
    /// handoff is given up, while nobody has used it; once the user has
    /// clicked in it, it stays.  The windows open on a monitor that is not
    /// the primary one, never active.
    /// </summary>
    private static async Task Round2DiscardWindowChecksAsync(string root)
    {
        Section("cross-cluster round 2: the window made for a handoff given up closes, unless used (I090)");
        var folder = Path.Combine(root, "handed over");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(AppPaths.StateDirectory);
        var settings = DialogIntegrationStore.SettingsPath;
        var before = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        File.WriteAllText(settings, System.Text.Json.JsonSerializer.Serialize(new DialogIntegrationSettings { Enabled = true }));
        var made = new List<MainWindow>();
        try
        {
            async Task<MainWindow?> HandOverAsync()
            {
                FolderCommandLine.TryOpenFolder(folder, out var open, out _);
                var invocation = open with { OriginIsShell = true, DestinationId = Guid.NewGuid() };
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var receipt = await ExplorerLaunchRouter.SendAsync(new FolderRouteRequest(Guid.NewGuid(), invocation, true), deadline.Token);
                var window = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(candidate => candidate.FolderDestinationId == invocation.DestinationId);
                if (window is not null)
                {
                    made.Add(window);
                }

                return ExplorerLaunchRouter.ReceiptMatches(receipt, invocation) ? window : null;
            }

            async Task DiscardAsync(MainWindow window)
            {
                FolderCommandLine.TryOpenFolder(folder, out var open, out _);
                await ExplorerLaunchRouter.DiscardIfUntouchedAsync(open with { OriginIsShell = true, DestinationId = window.FolderDestinationId });
            }

            bool Open(MainWindow window) => Application.Current.Windows.OfType<MainWindow>().Contains(window);

            var unused = await HandOverAsync();
            Check("a window is made for the handoff and opens its folder", unused is not null && unused.IsUntouchedHandoff);
            if (unused is not null)
            {
                await DiscardAsync(unused);
                var closed = await LiveWait(() => !Open(unused), 10_000) >= 0;
                Check("given up, the window nobody used closes", closed);
            }

            var used = await HandOverAsync();
            if (used is not null)
            {
                used.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseDownEvent
                });
                await DiscardAsync(used);
                await Task.Delay(500);
                Check("a window the user has clicked in stays", Open(used) && !used.IsUntouchedHandoff);
            }
            else
            {
                Check("a second window is made for a second handoff", false);
            }
        }
        finally
        {
            foreach (var window in made.Where(window => Application.Current.Windows.OfType<MainWindow>().Contains(window)))
            {
                window.Close();
            }

            await LiveWait(() => !made.Any(window => Application.Current.Windows.OfType<MainWindow>().Contains(window)), 10_000);
            if (before is null)
            {
                File.Delete(settings);
            }
            else
            {
                File.WriteAllBytes(settings, before);
            }
        }
    }

    // ---- a dialog worker survives a glitch in its picker (I196) -----------------------

    /// <summary>
    /// A dialog worker - or a process serving one dialog - survives an
    /// exception on its UI thread without a word: it is written down, the
    /// dialog being served goes back to Windows, and the worker makes way for
    /// a fresh one.  It ended on the spot, and the guardian then paused the
    /// whole replacement.  Here the reporter is told to survive as a worker's
    /// does, and what the worker would do afterwards is counted instead.
    /// </summary>
    private static async Task Round2QuietSurvivalChecksAsync()
    {
        Section("cross-cluster round 2: a dialog worker survives a glitch in its picker quietly (I196)");

        // The reporter is installed already: the app's start does that in
        // every process, this one too, as for a test copy.
        var survived = 0;
        CrashReporter.SurviveQuietly(() => survived++);
        var logged = File.Exists(CrashReporter.LogPath) ? new FileInfo(CrashReporter.LogPath).Length : 0;
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(() => throw new InvalidOperationException("a picker glitch, for the round-2 checks"));
        await LiveWait(() => survived > 0, 3_000);
        Check($"an exception on the UI thread is survived, and the worker's own recovery runs once ({survived})", survived == 1);
        Check("and it is written down",
            File.Exists(CrashReporter.LogPath) && new FileInfo(CrashReporter.LogPath).Length > logged
            && File.ReadAllText(CrashReporter.LogPath).Contains("a picker glitch, for the round-2 checks", StringComparison.Ordinal));
        Check("with no dialog being served, there is none to give back", !NativeDialogProxy.ReturnServedDialogAfterFailure());
    }

    /// <summary>
    /// A hidden top-level window of Explorer's frame class in this process:
    /// what Explorer opening a window looks like to one that waits for it.
    /// Nothing but this test process owns it; nothing that looks for
    /// Explorer's own frames takes it for one, as it is not explorer.exe's.
    /// </summary>
    private static class Round2ExplorerFrame
    {
        private static bool _registered;

        internal static nint Create()
        {
            var module = GetModuleHandle(null);
            if (!_registered)
            {
                var info = new WindowClass
                {
                    Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WindowClass>(),
                    Procedure = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"),
                    Instance = module,
                    ClassName = "CabinetWClass"
                };
                _registered = RegisterClassEx(ref info) != 0 || System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 1410;
            }

            // WS_POPUP, never shown.
            return CreateWindowEx(0, "CabinetWClass", "Round 2 Explorer frame", unchecked((int)0x80000000), 0, 0, 10, 10, 0, 0, module, 0);
        }

        internal static void Destroy(nint window)
        {
            if (window != 0)
            {
                DestroyWindow(window);
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct WindowClass
        {
            public uint Size;
            public uint Style;
            public nint Procedure;
            public int ClassExtra;
            public int WindowExtra;
            public nint Instance;
            public nint Icon;
            public nint Cursor;
            public nint Background;
            public string? MenuName;
            public string ClassName;
            public nint SmallIcon;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WindowClass info);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowEx(int extendedStyle, string className, string title, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyWindow(nint window);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern nint GetModuleHandle(string? module);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private static extern nint GetProcAddress(nint module, string name);
    }
}
