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

    // ---- helpers -----------------------------------------------------------------------

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
