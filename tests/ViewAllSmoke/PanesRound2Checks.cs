using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
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
/// (J131).
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
        new App().InitializeComponent();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
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
            Check($"on the tree, its folders opened down to the focus, all four are still selected ({shell.Tree.Selection.Count} selected)",
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
