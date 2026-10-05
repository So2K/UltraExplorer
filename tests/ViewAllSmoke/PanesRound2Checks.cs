using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// What the second round of the review found in the window's panes - the
/// nested canvas's side of the window, the split view and each pane - and
/// what was done about it.  A camera the last session left on a share that
/// answers after the start is put back once it does, and kept meanwhile
/// rather than the overview shown while it waits (J041, J124).  A colour or
/// a note on a drive or share that answers late is drawn once it does (J126).
/// A new window gathers its beacons once, when its panes have their drives,
/// not first for nothing as it reads its workspace (J079).  Switching to the
/// tree keeps a selection of several items, and a navigation under way
/// (J131).  Shift+F5 or Shift+F6 pressed again while a copy or a move to the
/// other pane is on its way sends nothing more (J065).  Zoomed out of a
/// folder opened by name, the folder stays where it is when its parent is
/// listed, rather than jumping to a speck elsewhere (J026).  A drag of fifty
/// thousand items goes from folder to folder without a stall at each (J103).
///
/// <para>The windows need the app, of which a process can only ever have the
/// one, on the thread that made it: these checks always run in a process of
/// their own.  No window is ever shown.</para>
/// </summary>
internal static partial class Program
{
    private static async Task PanesRound2Checks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(PanesRound2Checks), StringComparison.OrdinalIgnoreCase))
        {
            await PanesRound2InOwnProcessAsync();
            return;
        }

        RunOnSta("panes, review round 2", PanesRound2OnStaAsync);
    }

    /// <summary>
    /// The checks run by this program again, alone, in a process of their
    /// own: each of its results is counted here.  One that hangs is ended
    /// after ten minutes and counted as a failure, so the rest of the run
    /// goes on.
    /// </summary>
    private static async Task PanesRound2InOwnProcessAsync()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!, "--only " + nameof(PanesRound2Checks))
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

            Check("the panes' round-2 checks' own process finished within ten minutes", false);
            return;
        }

        Check("the panes' round-2 checks' own process ran to the end", finished);
    }

    private static async Task PanesRound2OnStaAsync()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        var stateOverride = Environment.GetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR");
        var isolated = !string.IsNullOrWhiteSpace(stateOverride)
            && Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1"
            && !Path.GetFullPath(stateOverride).Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer"), StringComparison.OrdinalIgnoreCase);
        Check("the panes' round-2 checks require isolated state and test-window mode", isolated);
        if (!isolated)
        {
            return;
        }

        // What the windows' own work throws on the interface thread outside
        // these checks is written down and survived, rather than ending them
        // half way.
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
        {
            Console.WriteLine($"  note: the interface thread threw, outside these checks: {e.Exception}");
            e.Handled = true;
        };

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerPanesRound2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (ActivationGuard.GuardWindowsCreated())
            {
                await PanesLateShareCameraChecksAsync(root);
                await PanesLateRootMarksChecksAsync(root);
                await PanesStartBeaconChecksAsync(root);
                await PanesTreeSwitchChecksAsync(root);
                await PanesSendTwiceChecksAsync(root);
                await PanesSparseParentChecksAsync(root);
                await PanesBigDragChecksAsync(root);
                await PanesFilterBeaconChecksAsync(root);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- a camera left on a share that answers late (J041, J124) ---------------------

    /// <summary>
    /// The last session left the camera deep in a share.  Shares are looked
    /// for only once the window is up, so the camera, put back before, has
    /// no cell to go to and waits for the share.  Closed meanwhile - the
    /// window, or the split - the pane keeps that place, not the overview of
    /// This PC it shows while it waits; and once the share answers and joins
    /// the canvas, the camera goes there.  It stayed on the overview, and the
    /// next close saved that over the place for good.
    /// </summary>
    private static async Task PanesLateShareCameraChecksAsync(string root)
    {
        Section("panes round 2: a camera left on a share that answers late is kept, and put back when it does (J041, J124)");
        var share = Path.Combine(root, "late-share");
        var deep = Path.Combine(share, "projects", "deep");
        var other = Path.Combine(root, "late-other");
        var otherDeep = Path.Combine(other, "inside");
        Directory.CreateDirectory(deep);
        Directory.CreateDirectory(otherDeep);
        var camera = new NestedCameraState(deep, 0.01, -0.02, 0.9);
        var secondCamera = new NestedCameraState(otherDeep, 0.0, 0.0, 0.8);
        var main = ProxyWindow(out var shell);
        try
        {
            // What the last session left, as the workspace hands it over.
            typeof(ViewAllViewModel).GetProperty(nameof(ViewAllViewModel.RestoredNestedCamera))!.SetValue(shell.Tree, camera);
            shell.Tree.NestedCamera = camera;
            await main.StartNestedForChecksAsync();
            var pane = main.FirstPane;
            Check("started before the share has answered, the camera waits for it",
                await LiveWait(() => PanesLateCamera(pane) == camera, 5_000) >= 0);

            // A frame drawn meanwhile shows the overview: the window is closed.
            pane.Canvas.ScreenRectOf(pane.Tree.Root);
            var meanwhile = pane.Canvas.CaptureCamera();
            PanesCaptureNestedCamera(main);
            Check($"closed while it waits, the window keeps the camera on the share, not the overview it shows meanwhile ({PanesPlace(meanwhile)} shown, {PanesPlace(shell.Tree.NestedCamera)} kept)",
                meanwhile is { AnchorPath: "" } && shell.Tree.NestedCamera == camera);

            // So does the split: its second pane waits for a share as well.
            shell.Tree.SecondPane = new NestedPaneState(null, secondCamera);
            shell.IsSplit = true;
            var second = main.SecondPane;
            var waits = second is not null && await LiveWait(() => PanesLateCamera(second) == secondCamera, 5_000) >= 0;
            Check("split, the second pane's camera on a share not here yet waits for it too", waits);
            if (second is not null)
            {
                second.Canvas.ScreenRectOf(second.Tree.Root);
                var secondMeanwhile = second.Canvas.CaptureCamera();
                shell.IsSplit = false;
                Check($"closed while it waits, the split keeps that camera for the next split ({PanesPlace(secondMeanwhile)} shown, {PanesPlace(shell.Tree.SecondPane?.NestedCamera)} kept)",
                    main.Panes.Count == 1 && secondMeanwhile is { AnchorPath: "" } && shell.Tree.SecondPane?.NestedCamera == secondCamera);
            }

            // The share answers: a root of the tree, and so one of the canvas's.
            main.UseNestedDrivesForChecks([new NestedRoot(share, "late-share", NestedFolderKind.Drive)]);
            PanesExtraRootAdded(main, share);
            Rect? Cell() => pane.Tree.Find(deep) is { } folder ? pane.Canvas.ScreenRectOf(folder) : null;
            bool Back() => Cell() is { } rect && pane.Canvas.ActualWidth is > 0 and var width && rect.Width / width is > 0.6 and < 1.3;
            var back = await LiveWait(Back, 5_000) >= 0;
            Check($"once the share answers, the camera the last session left there is put back (its folder {PanesRect(Cell())} in a view {pane.Canvas.ActualWidth:0} wide)",
                back && PanesLateCamera(pane) is null);
        }
        finally
        {
            shell.IsSplit = false;
            shell.Dispose();
        }
    }

    // ---- colours and notes on a drive or share that answers late (J126) --------------

    /// <summary>
    /// A colour on a folder of a share and a note on a folder of a drive,
    /// neither of which had answered as the window started: the pane looked
    /// for both among its cells, found neither and set them aside as nowhere
    /// to be found.  Once each answers and joins the canvas, its mark is
    /// looked for again, and found; they stayed undrawn for the session.
    /// </summary>
    private static async Task PanesLateRootMarksChecksAsync(string root)
    {
        Section("panes round 2: colours and notes on a drive or share that answers late are drawn once it does (J126)");
        var share = Path.Combine(root, "marks-share");
        var drive = Path.Combine(root, "marks-drive");
        var onShare = Path.Combine(share, "coloured");
        var onDrive = Path.Combine(drive, "noted");
        Directory.CreateDirectory(onShare);
        Directory.CreateDirectory(onDrive);
        var main = ProxyWindow(out var shell);
        try
        {
            shell.Marks.Seed(onShare, "#FF8800", string.Empty);
            shell.Marks.Seed(onDrive, string.Empty, "a note");
            await main.StartNestedForChecksAsync();
            var pane = main.FirstPane;
            Check("at the start, neither mark is on a drive or share among the cells: both are set aside as nowhere",
                await LiveWait(() => PanesUnresolvable(pane.Canvas).Contains(onShare) && PanesUnresolvable(pane.Canvas).Contains(onDrive), 5_000) >= 0);

            // The share answers, as a root of the tree and so of the canvas.
            main.UseNestedDrivesForChecks([new NestedRoot(share, "marks-share", NestedFolderKind.Drive)]);
            PanesExtraRootAdded(main, share);
            Check("once the share answers, the colour on it is looked for again and found",
                await LiveWait(() => pane.Tree.Find(onShare) is not null && !PanesUnresolvable(pane.Canvas).Contains(onShare), 3_000) >= 0);

            // The drive answers, as the tree hears of a drive start-up went on without.
            PanesDriveAdded(main, drive);
            Check("and once the drive answers, so is the note on it",
                await LiveWait(() => pane.Tree.Find(onDrive) is not null && !PanesUnresolvable(pane.Canvas).Contains(onDrive), 3_000) >= 0);
        }
        finally
        {
            shell.Dispose();
        }
    }

    // ---- a new window gathers its beacons once (J079) ----------------------------------

    /// <summary>
    /// A window reads its workspace before its panes have their drives, and
    /// the favourite links setting read with it is said to have changed,
    /// whatever it was.  Each pane gathered every mark into beacons for it
    /// and looked for each one in a tree with nothing in it, only to do it
    /// all again once it had its drives.  It gathers them then alone; a
    /// change of the setting once the pane has its drives gathers them again
    /// at once, as ever.
    /// </summary>
    private static async Task PanesStartBeaconChecksAsync(string root)
    {
        Section("panes round 2: a new window gathers its beacons once, when its panes have their drives (J079)");
        var main = ProxyWindow(out var shell);
        try
        {
            for (var index = 0; index < 2_000; index++)
            {
                shell.Marks.Seed(Path.Combine(root, "many-marks", $"folder{index / 50:D2}", $"file{index:D4}.txt"), "#3A96DD", string.Empty);
            }

            // As reading the workspace says it, before the panes have drives.
            var pane = main.FirstPane;
            var before = PanesBeacons(pane.Canvas);
            var watch = Stopwatch.StartNew();
            PanesRaisePropertyChanged(shell, nameof(MainViewModel.ShowFavoriteLinks));
            var raised = watch.Elapsed.TotalMilliseconds;
            await LiveWait(() => !PanesResolving(pane.Canvas), 10_000);
            var looked = watch.Elapsed.TotalMilliseconds;
            Check($"read before the panes have their drives, the favourite links setting gathers no beacons ({PanesBeacons(pane.Canvas).Count} gathered in {raised:0.0} ms, looked for until {looked:0.0} ms)",
                !pane.IsReady && ReferenceEquals(PanesBeacons(pane.Canvas), before));

            await main.StartNestedForChecksAsync();
            Check($"once they have, the pane gathers every mark ({PanesBeacons(pane.Canvas).Count} beacons)", PanesBeacons(pane.Canvas).Count >= 2_000);

            var gathered = PanesBeacons(pane.Canvas);
            var shown = shell.ShowFavoriteLinks;
            shell.ShowFavoriteLinks = !shown;
            var regathered = !ReferenceEquals(PanesBeacons(pane.Canvas), gathered) && pane.Canvas.ShowFavoriteLinks == !shown;
            shell.ShowFavoriteLinks = shown;
            Check("and a change of the setting from then on shows or hides the links and gathers the beacons again at once, as ever", regathered);
            await LiveWait(() => !PanesResolving(pane.Canvas), 10_000);
        }
        finally
        {
            shell.Dispose();
        }
    }

    // ---- the tree canvas keeps the selection (J131) -----------------------------------

    /// <summary>
    /// Four files selected on the nested canvas, and the picture switched to
    /// the tree: the tree opens its folders down to the one with the focus,
    /// and the four are still what is selected - it selected the focused
    /// file alone, and Delete then acted on one of the four.  Nor does that
    /// reveal stand in for a navigation asked for just before it.
    /// </summary>
    private static async Task PanesTreeSwitchChecksAsync(string root)
    {
        Section("panes round 2: switching to the tree keeps the whole selection, and a navigation under way (J131)");
        var folder = Path.Combine(root, "switch");
        Directory.CreateDirectory(folder);
        var files = new[] { "a.txt", "b.txt", "c.txt", "d.txt" }.Select(name => Path.Combine(folder, name)).ToArray();
        foreach (var file in files)
        {
            File.WriteAllText(file, "x");
        }

        var main = ProxyWindow(out var shell);
        try
        {
            await main.StartNestedForChecksAsync();
            shell.Tree.Selection.Apply(new SelectionEdit
            {
                Clear = true,
                Container = folder,
                Added = [.. files.Select(file => new SelectionItem(file, false, 1))],
                Anchor = files[0],
                Focus = files[0],
                Source = SelectionSource.Canvas
            });
            Check("four files are selected on the nested canvas, the first with the focus",
                await LiveWait(() => ViewAllPath.Equals(shell.Tree.ActivePath, files[0]), 5_000) >= 0 && shell.Tree.Selection.Count == 4);

            var navigation = shell.Tree.BeginNavigation();
            shell.Layout = CanvasLayout.Tree;
            await main.TreeEntryForChecks;
            Check($"on the tree, its folders opened down to the focus, all four are still selected ({shell.Tree.Selection.Count} selected; active={shell.Tree.ActivePath}; parent={shell.Tree.ActiveNode?.Parent?.FullPath}; expanded={shell.Tree.ActiveNode?.Parent?.IsExpanded})",
                shell.Tree.Selection.Count == 4 && files.All(shell.Tree.Selection.Contains) && ViewAllPath.Equals(shell.Tree.ActivePath, files[0])
                && shell.Tree.ActiveNode?.Parent is { IsExpanded: true });
            Check("and a navigation asked for just before is still the one that counts", shell.Tree.IsLatestNavigation(navigation));
        }
        finally
        {
            shell.Layout = CanvasLayout.Nested;
            shell.Dispose();
        }
    }

    // ---- Shift+F5 pressed again while a copy to the other pane is on its way (J065) ----

    /// <summary>
    /// Shift+F5 on a file of a share that takes its time to say it is there:
    /// nothing shows while the copy asks, and Shift+F5 pressed again - or
    /// Shift+F6, or the key held down - started a second copy beside it, or
    /// a move racing it.  Pressed again before the first is done, nothing
    /// more is sent; once it is done, the keys send again.  The file is
    /// found not there in the end, so nothing is copied for real.
    /// </summary>
    private static async Task PanesSendTwiceChecksAsync(string root)
    {
        Section("panes round 2: Shift+F5 pressed again while a copy to the other pane is on its way sends nothing more (J065)");
        var drive = Path.Combine(root, "send-drive");
        var left = Path.Combine(drive, "left");
        var right = Path.Combine(drive, "right");
        var file = Path.Combine(left, "slow.txt");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        File.WriteAllText(file, "on a share that takes its time");
        var existsBefore = MainViewModel.ItemExists;
        using var held = new ManualResetEventSlim(false);
        var asked = 0;
        var main = ProxyWindow(out var shell);
        try
        {
            await main.StartNestedForChecksAsync();
            main.UseNestedDrivesForChecks([new NestedRoot(drive, "T", NestedFolderKind.Drive)]);
            shell.Tree.SecondPane = new NestedPaneState(null, null);
            shell.IsSplit = true;
            var second = main.SecondPane;
            second?.KeptSelection.ReplaceSingle(right, true, 0, SelectionSource.Navigation);
            shell.Tree.Selection.ReplaceSingle(file, false, 1, SelectionSource.Navigation);
            Check("split, with the file selected in the first pane and the other pane's folder to send it to",
                second is not null && ViewAllPath.Equals(main.OtherPaneFolder() ?? string.Empty, right)
                && await LiveWait(() => shell.Tree.SelectedPaths.SequenceEqual([file]), 5_000) >= 0);

            // Each copy or move asks whether the file is there as it starts:
            // counted, held as a share that takes its time holds it, and
            // answered no.
            MainViewModel.ItemExists = path =>
            {
                if (!ViewAllPath.Equals(path, file))
                {
                    return existsBefore(path);
                }

                Interlocked.Increment(ref asked);
                held.Wait(TimeSpan.FromSeconds(20));
                return false;
            };

            main.TryHandlePaneTransferKey(Key.F5, ModifierKeys.Shift);
            Check("Shift+F5 starts a copy, which asks whether the file is there", await LiveWait(() => Volatile.Read(ref asked) == 1, 5_000) >= 0);
            main.TryHandlePaneTransferKey(Key.F5, ModifierKeys.Shift);
            main.TryHandlePaneTransferKey(Key.F5, ModifierKeys.Shift);
            main.TryHandlePaneTransferKey(Key.F6, ModifierKeys.Shift);
            await LiveWait(() => Volatile.Read(ref asked) > 1, 1_000);
            var sent = Volatile.Read(ref asked);
            held.Set();
            Check($"pressed again, held down or Shift+F6, while it is on its way: nothing more is sent ({sent} copies and moves started)", sent == 1);

            var done = await LiveWait(() => shell.Toast.Message.StartsWith("This drop target", StringComparison.Ordinal), 5_000) >= 0;
            await SettingsSettle();
            Interlocked.Exchange(ref asked, 0);
            main.TryHandlePaneTransferKey(Key.F6, ModifierKeys.Shift);
            Check("once it is done, Shift+F6 sends again", done && await LiveWait(() => Volatile.Read(ref asked) == 1, 5_000) >= 0);
            await LiveWait(() => shell.Toast.Message.StartsWith("This drop target", StringComparison.Ordinal), 5_000);
            await SettingsSettle();
        }
        finally
        {
            MainViewModel.ItemExists = existsBefore;
            held.Set();
            shell.IsSplit = false;
            shell.Dispose();
        }
    }

    // ---- zooming out of a folder opened by name (J026) --------------------------------

    /// <summary>
    /// A folder opened by name - a folder launch, a mark, the address bar -
    /// is reached through its parent known only by the way to it: the folder
    /// fills it.  Zoomed out with the wheel to see the parent, the folder
    /// stood where it was until the camera came to rest and the parent was
    /// listed, and then shrank to a speck among its sisters elsewhere on
    /// screen.  It stays where it is, as it is, while they come in around it.
    /// A flight to such a parent, which goes to the parent itself, keeps it
    /// framed as ever while its folders come in inside it.
    /// </summary>
    private static async Task PanesSparseParentChecksAsync(string root)
    {
        Section("panes round 2: zooming out of a folder opened by name keeps it in place when its parent is listed (J026)");
        var drive = Path.Combine(root, "sparse-drive");
        var parent = Path.Combine(drive, "projects");
        var opened = Path.Combine(parent, "m-opened");
        var flownTo = Path.Combine(drive, "archive");
        var flownToChild = Path.Combine(flownTo, "m-kept");
        for (var index = 0; index < 40; index++)
        {
            Directory.CreateDirectory(Path.Combine(parent, $"sister{index:D2}"));
            Directory.CreateDirectory(Path.Combine(flownTo, $"sister{index:D2}"));
        }

        Directory.CreateDirectory(Path.Combine(opened, "inside"));
        Directory.CreateDirectory(flownToChild);
        File.WriteAllText(Path.Combine(opened, "notes.txt"), "opened by name");
        var main = ProxyWindow(out var shell);
        try
        {
            await main.StartNestedForChecksAsync();
            var pane = main.FirstPane;
            var tree = pane.Tree;
            var canvas = pane.Canvas;
            canvas.FramesByHandForTests = true;
            var time = TimeSpan.FromSeconds(500);
            async Task Frames(Func<bool> until, int most)
            {
                for (var frame = 0; frame < most && !until(); frame++)
                {
                    await Task.Delay(10);
                    canvas.RunFrameForTests(time += TimeSpan.FromMilliseconds(16));
                }
            }

            // Nothing read for being drawn until the folder has landed, as a
            // folder launch keeps the drives unread until it has.
            tree.IsReadingOnDemand = false;
            main.UseNestedDrivesForChecks([new NestedRoot(drive, "S", NestedFolderKind.Drive)]);

            // Opened by name, as a folder launch lands: framed, and read.
            var folder = await tree.MaterializePathAsync(opened);
            if (folder is not null)
            {
                await tree.LoadAsync(folder);
                canvas.FlyTo(folder, 0.92, animated: false);
            }

            tree.IsReadingOnDemand = true;
            await Task.Delay(180);
            await Frames(() => false, 5);
            var sparse = tree.Find(parent);
            Check("landed on the folder, its parent known only by the way to it",
                folder is not null && ReferenceEquals(canvas.Anchor, folder) && sparse is { HasPartialListing: true, IsLoaded: false });
            if (folder is null || sparse is null)
            {
                return;
            }

            // Zoomed out with the wheel, about the middle, until the parent
            // holds the view.
            var middle = new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2);
            for (var step = 0; step < 4; step++)
            {
                canvas.Pointer.Wheel(middle, -120, ModifierKeys.Control);
            }

            canvas.RenderNow();
            var before = canvas.ScreenRectOf(folder);
            Check($"zoomed out, the parent holds the view, still unlisted, with the folder {PanesRect(before)} in it",
                ReferenceEquals(canvas.Anchor, sparse) && sparse is { IsLoaded: false } && before is { Width: > 300 });

            // The camera comes to rest: the parent is listed.
            await Task.Delay(180);
            await Frames(() => sparse.IsLoaded, 200);
            await Frames(() => false, 3);
            var after = canvas.ScreenRectOf(folder);
            var steady = before is { } b && after is { } a
                && Math.Abs(a.X - b.X) < 1 && Math.Abs(a.Y - b.Y) < 1 && Math.Abs(a.Width - b.Width) < 1;
            var view = new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight);
            var sistersInView = sparse.Children.Count(child => !ReferenceEquals(child, folder)
                && canvas.ScreenRectOf(child) is { } cell && cell.IntersectsWith(view));
            Check($"once the parent is listed ({sparse.Children.Count} folders in it), the folder stays where it was, as it was ({PanesRect(before)} -> {PanesRect(after)}), its sisters around it ({sistersInView} in view)",
                sparse.IsLoaded && sparse.Children.Count == 41 && steady && sistersInView > 0);

            // A flight to a parent known only by the way to a folder in it:
            // the parent is framed, and stays framed as it is listed.
            var kept = await tree.MaterializePathAsync(flownToChild);
            var flown = tree.Find(flownTo);
            if (flown is not null)
            {
                canvas.FlyTo(flown, 0.92, animated: false);
            }

            canvas.RenderNow();
            var framed = flown is null ? null : canvas.ScreenRectOf(flown);
            var keptBefore = kept is null ? null : canvas.ScreenRectOf(kept);
            await Task.Delay(180);
            await Frames(() => flown?.IsLoaded == true, 200);
            await Frames(() => false, 3);
            var framedAfter = flown is null ? null : canvas.ScreenRectOf(flown);
            var keptAfter = kept is null ? null : canvas.ScreenRectOf(kept);
            Check($"flown to a parent known only by the way, it stays framed as it is listed ({PanesRect(framed)} -> {PanesRect(framedAfter)}), its folder one cell of {flown?.Children.Count} ({PanesRect(keptBefore)} -> {PanesRect(keptAfter)})",
                flown is { IsLoaded: true, Children.Count: 41 } && framed is { } f && framedAfter is { } fa
                && Math.Abs(f.X - fa.X) < 1 && Math.Abs(f.Y - fa.Y) < 1 && Math.Abs(f.Width - fa.Width) < 1
                && keptBefore is { } kb && keptAfter is { } ka && ka.Width < kb.Width / 2);
        }
        finally
        {
            shell.Dispose();
        }
    }

    // ---- a drag of fifty thousand photos over the folders (J103) ------------------------

    /// <summary>
    /// Ctrl+A over fifty thousand photos, dragged across the canvas: each
    /// folder the pointer came to compared every one of them with it, by
    /// their full names worked out afresh - a tenth of a second for each
    /// folder, a stutter at every one - and every DragOver while it stayed
    /// there asked again whether all of them were on its drive.  What the
    /// drag carries is looked up by name instead, gathered once for the
    /// drag; what is refused is refused as before.
    /// </summary>
    private static async Task PanesBigDragChecksAsync(string root)
    {
        Section("panes round 2: a drag of fifty thousand items goes over folder after folder without a stall (J103)");
        var drive = Path.Combine(root, "drag-drive");
        var photos = Path.Combine(drive, "photos");
        var carriedFolder = Path.Combine(drive, "carried");
        var insideCarried = Path.Combine(carriedFolder, "inside");
        var targets = Enumerable.Range(0, 8).Select(index => Path.Combine(drive, $"target{index}")).ToArray();
        foreach (var folder in targets.Append(photos).Append(insideCarried))
        {
            Directory.CreateDirectory(folder);
        }

        string[] carried = [.. Enumerable.Range(0, 50_000).Select(index => Path.Combine(photos, $"IMG_{index:D5}.jpg")), carriedFolder];
        var main = ProxyWindow(out var shell);
        var host = (INestedPaneHost)main;
        try
        {
            await main.StartNestedForChecksAsync();
            main.UseNestedDrivesForChecks([new NestedRoot(drive, "D", NestedFolderKind.Drive)]);
            var pane = main.FirstPane;
            var tree = pane.Tree;
            await tree.LoadAsync(tree.Find(drive)!);
            await tree.LoadAsync(tree.Find(carriedFolder)!);
            pane.Canvas.FlyTo(tree.Find(drive)!, 0.92, animated: false);
            pane.Canvas.RenderNow();
            var surface = SettingsHosts[main];
            Point? Centre(string path) =>
                tree.Find(path) is { } folder && pane.Canvas.ScreenRectOf(folder) is { } cell
                    ? pane.Canvas.TranslatePoint(new Point(cell.X + cell.Width / 2, cell.Y + cell.Height / 2), surface)
                    : null;
            var points = targets.Select(Centre).ToArray();
            if (points.Any(point => point is null) || Centre(photos) is not { } overPhotos || Centre(insideCarried) is not { } overInside)
            {
                Check("every folder is on the canvas", false);
                return;
            }

            var data = new DataObject(DataFormats.FileDrop, carried);
            DragEventArgs Over(Point at)
            {
                var args = SplitDragArgs(DragDrop.DragOverEvent, data, DragDropKeyStates.None, surface, at);
                pane.Canvas.RaiseEvent(args);
                return args;
            }

            // The pointer comes onto the canvas, then goes from folder to
            // folder, and stays a while on the last.
            var watch = Stopwatch.StartNew();
            var first = Over(points[0]!.Value);
            var entered = watch.Elapsed.TotalMilliseconds;
            var lit = first.Effects == DragDropEffects.Move && pane.Canvas.DropTarget?.FullPath == targets[0];
            watch.Restart();
            for (var index = 1; index < targets.Length; index++)
            {
                var over = Over(points[index]!.Value);
                lit &= over.Effects == DragDropEffects.Move && pane.Canvas.DropTarget?.FullPath == targets[index];
            }

            var perFolder = watch.Elapsed.TotalMilliseconds / (targets.Length - 1);
            watch.Restart();
            for (var repeat = 0; repeat < 20; repeat++)
            {
                lit &= Over(points[^1]!.Value).Effects == DragDropEffects.Move;
            }

            var perStay = watch.Elapsed.TotalMilliseconds / 20;
            Check($"each folder the pointer comes to is lit at once ({entered:0.0} ms coming onto the canvas, then {perFolder:0.00} ms for each new folder and {perStay:0.000} ms for each DragOver on the same one)",
                lit && perFolder < 40 && perStay < 1);

            // Refused as ever: inside a folder it carries, and - from this
            // window - the folder its items come out of.
            var intoCarried = Over(overInside);
            var backAccepted = Over(overPhotos).Effects == DragDropEffects.Move;
            host.NestedDragPaths = carried;
            var elsewhere = Over(points[1]!.Value);
            var outOfItsFolder = Over(overPhotos);
            host.NestedDragPaths = null;
            Check("still refused inside a folder it carries, and on the folder a drag from this window carries its items out of; taken anywhere else",
                intoCarried.Effects == DragDropEffects.None && backAccepted && outOfItsFolder.Effects == DragDropEffects.None
                && elsewhere.Effects == DragDropEffects.Move);
            host.ForgetDropPaths();
            host.ClearDropTargets();
        }
        finally
        {
            host.NestedDragPaths = null;
            shell.Dispose();
        }
    }

    // ---- the filter's matches changing frame after frame while folders are read (J140) --

    /// <summary>
    /// With a filter on, flying into folders not read yet: each frame that
    /// takes in read folders says the matches changed, and each time the
    /// pane put off gathering its beacons - the matches among them, the
    /// marks - another 120 ms, so none was gathered for as long as the
    /// reading went on, seconds of it.  They are gathered again as the timer
    /// comes round, and once more after the last change.
    /// </summary>
    private static async Task PanesFilterBeaconChecksAsync(string root)
    {
        Section("panes round 2: matches changing frame after frame while folders are read still have their beacons gathered (J140)");
        var drive = Path.Combine(root, "filter-drive");
        Directory.CreateDirectory(Path.Combine(drive, "read-later"));
        var main = ProxyWindow(out var shell);
        try
        {
            await main.StartNestedForChecksAsync();
            main.UseNestedDrivesForChecks([new NestedRoot(drive, "F", NestedFolderKind.Drive)]);
            var pane = main.FirstPane;
            var canvas = pane.Canvas;
            await Task.Delay(400);
            var raise = typeof(NestedCanvas).GetMethod("RaiseFilterChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;

            // A frame each 16 ms takes in folders read and says so, for
            // 1.2 seconds.
            var gathered = 0;
            var firstGathered = -1.0;
            var last = PanesBeacons(canvas);
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 1_200)
            {
                raise.Invoke(canvas, null);
                await Task.Delay(16);
                if (!ReferenceEquals(PanesBeacons(canvas), last))
                {
                    last = PanesBeacons(canvas);
                    gathered++;
                    if (firstGathered < 0)
                    {
                        firstGathered = watch.Elapsed.TotalMilliseconds;
                    }
                }
            }

            Check($"while the matches change frame after frame, the beacons are gathered as the timer comes round ({gathered} times in 1.2 s, first at {firstGathered:0} ms)",
                pane.IsReady && gathered >= 4 && firstGathered is >= 0 and < 400);

            // The last frame that takes any in.
            var stopped = PanesBeacons(canvas);
            raise.Invoke(canvas, null);
            Check("and once more after the last change",
                await LiveWait(() => !ReferenceEquals(PanesBeacons(canvas), stopped), 2_000) >= 0);
        }
        finally
        {
            shell.Dispose();
        }
    }

    // ---- helpers -----------------------------------------------------------------------

    /// <summary>The beacons <paramref name="canvas"/> was last given, as given: a new list each time they are gathered.</summary>
    private static IReadOnlyList<NestedBeacon> PanesBeacons(NestedCanvas canvas) =>
        (IReadOnlyList<NestedBeacon>)typeof(NestedCanvas).GetField("_beacons", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas)!;

    /// <summary>Whether <paramref name="canvas"/> is still looking for the folders of its beacons.</summary>
    private static bool PanesResolving(NestedCanvas canvas) =>
        (bool)typeof(NestedCanvas).GetField("_beaconResolverRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas)!;

    /// <summary>A property of the window's model said to have changed, as the model says it when it reads its workspace.</summary>
    private static void PanesRaisePropertyChanged(MainViewModel shell, string property) =>
        typeof(UltraExplorer.Infrastructure.ObservableObject).GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string)])!.Invoke(shell, [property]);

    /// <summary>The beacons <paramref name="canvas"/> looked for and found nowhere, not to be looked for again until it is told to.</summary>
    private static IReadOnlySet<string> PanesUnresolvable(NestedCanvas canvas) =>
        (HashSet<string>)typeof(NestedCanvas).GetField("_unresolvable", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas)!;

    /// <summary>A drive the window started without answered, as the window hears of it from its tree.</summary>
    private static void PanesDriveAdded(MainWindow main, string drive) =>
        typeof(MainWindow).GetMethod("OnDriveAddedForNested", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [PanesRootNode(drive, ViewAllEntryKind.Drive)]);

    /// <summary>The camera <paramref name="pane"/> is waiting to put back once its drive or share answers, if any.</summary>
    private static NestedCameraState? PanesLateCamera(NestedPane pane) =>
        (NestedCameraState?)typeof(NestedPane).GetField("_cameraOnLateDrive", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pane);

    /// <summary>Where a camera is, as a check's message says it: its folder's name, This PC, or none.</summary>
    private static string PanesPlace(NestedCameraState? camera) => camera switch
    {
        null => "none",
        { AnchorPath: "" } => "This PC",
        _ => Path.GetFileName(camera.AnchorPath)
    };

    /// <summary>A cell on screen as a check's message says it.</summary>
    private static string PanesRect(Rect? rect) => rect is { } cell ? $"{cell.Width:0}x{cell.Height:0} at ({cell.X:0},{cell.Y:0})" : "nowhere";

    /// <summary>What the window keeps of every pane's camera as it closes.</summary>
    private static void PanesCaptureNestedCamera(MainWindow main) =>
        typeof(MainWindow).GetMethod("CaptureNestedCamera", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);

    /// <summary>A node of the window's tree for a root at <paramref name="path"/>: a share, or with <paramref name="kind"/> a drive.</summary>
    private static ViewAllNodeViewModel PanesRootNode(string path, ViewAllEntryKind kind = ViewAllEntryKind.Folder) =>
        new(new ViewAllEntryDescriptor(path, Path.GetFileName(path), kind, false, false, null, DateTime.UtcNow), 0);

    /// <summary>A share the workspace lists answered after the start, as the window hears of it.</summary>
    private static void PanesExtraRootAdded(MainWindow main, string share) =>
        typeof(MainWindow).GetMethod("OnExtraRootAdded", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [PanesRootNode(share)]);
}
