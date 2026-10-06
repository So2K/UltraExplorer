using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Controls;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// What the review found in the panes of the window and of the dialog that
/// replaces another program's, and what was done about it: a dialog that
/// starts in a junction lists it; a split put back with its second pane
/// worked with lights that pane's selection; a pane closed after a folder
/// launch is let go of, and a launch's unread drives are only ever the pane's
/// it went to, and only while it is under way; a split closed while its
/// camera is still on its way back keeps where it was going; a folder known
/// only by a chain, drawn beside the one in view, is listed like any other;
/// and a flight, or Copy to other pane, never asks a share on the interface
/// thread whether a folder is there.
///
/// <para>The windows need the app, which lives on the thread that made it.
/// Run after the Settings checks, whose thread has finished with it, these
/// run in a process of their own, asked for alone, where they make it.</para>
/// </summary>
internal static partial class Program
{
    private static async Task ProxyPaneChecks()
    {
        if (Application.Current is not null)
        {
            await ProxyPaneChecksInOwnProcessAsync();
            return;
        }

        RunOnSta("proxy panes", ProxyPaneOnStaAsync);
    }

    /// <summary>
    /// The checks run by this program again, alone, in a process of their
    /// own: each of its results is counted here.  One that hangs is ended
    /// after ten minutes and counted as a failure, so the rest of the run
    /// goes on.
    /// </summary>
    private static async Task ProxyPaneChecksInOwnProcessAsync()
    {
        // The broad parent has deliberately changed its workspace many times.
        // This child exercises the real Loaded path, so give it a settled
        // state of its own instead of racing a manual split against whatever
        // the preceding group last saved.
        var childState = Path.Combine(Path.GetTempPath(), "UltraExplorerProxyPaneState", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(childState);
        await new WorkspaceStore(Path.Combine(childState, "workspace.json")).SaveAsync(new WorkspaceState
        {
            CanvasRenderer = "Cpu",
            CanvasLayout = nameof(CanvasLayout.Nested),
            IsSplit = false
        });
        var start = new ProcessStartInfo(Environment.ProcessPath!, "--only ProxyPaneChecks")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        start.Environment[AppPaths.StateDirectoryVariable] = childState;
        start.Environment["ULTRAEXPLORER_TEST_WINDOW"] = "1";
        try
        {
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

                Check("the proxy pane checks' own process finished within ten minutes", false);
                return;
            }

            Check("the proxy pane checks' own process ran to the end", finished);
        }
        finally
        {
            TryDelete(childState);
        }
    }

    private static async Task ProxyPaneOnStaAsync()
    {
        new App().InitializeComponent();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var stateOverride = Environment.GetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR");
        var isolated = !string.IsNullOrWhiteSpace(stateOverride)
            && Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1"
            && !Path.GetFullPath(stateOverride).Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer"), StringComparison.OrdinalIgnoreCase);
        Check("the proxy pane windows require isolated state and test-window mode", isolated);
        if (!isolated)
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerProxyPanes", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await ProxyPickerChecksAsync(root);
            await ProxySplitStartChecksAsync(root);
            await ProxyLaunchGateChecksAsync(root);
            await ProxyCameraRestoreChecksAsync(root);
            await ProxyPartialSiblingChecksAsync(root);
            await ProxyUnreachableShareChecksAsync(root);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- the dialog ------------------------------------------------------------------

    /// <summary>
    /// A dialog that starts in a junction, made for it or bound to it later,
    /// reads what the junction holds rather than opening empty after its
    /// wait.
    /// </summary>
    private static async Task ProxyPickerChecksAsync(string root)
    {
        Section("proxy panes: a dialog's folder that is a junction");
        var target = Path.Combine(root, "link-target");
        var otherTarget = Path.Combine(root, "other-target");
        var link = Path.Combine(root, "start-link");
        var otherLink = Path.Combine(root, "other-link");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(otherTarget);
        File.WriteAllText(Path.Combine(target, "inside.txt"), "behind the link");
        File.WriteAllText(Path.Combine(otherTarget, "beyond.txt"), "behind the other link");
        var linked = TryCreateJunction(link, target) && TryCreateJunction(otherLink, otherTarget);
        Check("the fixture's junctions are made", linked);
        if (!linked)
        {
            return;
        }

        static FileDialogRequest Request(string folder)
        {
            var request = new FileDialogRequest { IsNativeProxy = true, InitialFolder = folder };
            request.Filters.Add(new FileDialogFilterSpec("All files (*.*)", "*.*"));
            return request;
        }

        MainWindow? picker = null;
        try
        {
            var session = new FileDialogSession(Request(link));
            picker = new MainWindow(session) { Width = 1100, Height = 760, WindowState = WindowState.Normal };
            picker.PrepareAsCloakedPicker(_ => { });
            using (ActivationGuard.GuardWindowsCreated())
            {
                picker.Show();
            }

            var drawn = await picker.WhenFolderDrawnAsync(TimeSpan.FromSeconds(5));
            Check("a dialog made for a folder that is a junction reads it and draws it",
                drawn && picker.ActivePane.Tree.Find(link) is { IsLoaded: true, IsReparsePoint: true } start
                && start.Files.Any(file => file.Name == "inside.txt"));

            await picker.RebindAsync(session = new FileDialogSession(Request(otherLink)));
            picker.ConfirmContract();
            drawn = await picker.WhenFolderDrawnAsync(TimeSpan.FromSeconds(5));
            Check("and so does one bound to another junction later",
                drawn && picker.ActivePane.Tree.Find(otherLink) is { IsLoaded: true } bound
                && bound.Files.Any(file => file.Name == "beyond.txt"));
        }
        finally
        {
            picker?.CloseFromCaller();
        }
    }

    // ---- the window's panes ------------------------------------------------------------

    /// <summary>A window as the split view's checks have it: never shown, laid out, on the nested canvas.</summary>
    private static MainWindow ProxyWindow(out MainViewModel shell)
    {
        var main = new MainWindow();
        shell = (MainViewModel)main.DataContext;
        LayOutWindow(main, 1400, 900);
        shell.Tree.PreferLightReveal = true;
        shell.Layout = CanvasLayout.Nested;
        return main;
    }

    /// <summary>
    /// The split the last session left, put back with the second pane being
    /// worked with and a folder selected in it: the folder is the window's
    /// selection, and the pane's canvas lights it.  Its canvas was told it
    /// held that selection without ever having been given it.
    /// </summary>
    private static async Task ProxySplitStartChecksAsync(string root)
    {
        Section("proxy panes: a split put back");
        var drive = Path.Combine(root, "split-drive");
        var right = Path.Combine(drive, "right");
        Directory.CreateDirectory(right);
        Directory.CreateDirectory(Path.Combine(drive, "left"));
        var main = ProxyWindow(out var shell);
        try
        {
            typeof(ViewAllViewModel).GetProperty(nameof(ViewAllViewModel.RestoredSecondPaneItem))!
                .SetValue(shell.Tree, new SelectionItem(right, true, 0));
            shell.IsSplit = true;
            shell.ActivePaneIndex = 1;
            await main.StartNestedForChecksAsync();
            await SettingsSettle();
            var second = main.SecondPane;
            Check("the split is put back with its second pane worked with, and its folder is the window's selection",
                second is not null && ReferenceEquals(main.ActivePane, second) && shell.Tree.Selection.Contains(right));
            if (second is null)
            {
                return;
            }

            main.UseNestedDrivesForChecks([new NestedRoot(drive, "S", NestedFolderKind.Drive)]);
            await second.Tree.LoadAsync(second.Tree.Find(drive)!);
            Check("and the second pane's canvas lights it once its folder is read",
                await LiveWait(() => second.Canvas.SelectedCount == 1, 3_000) >= 0
                && second.Canvas.SelectionState.Items().Any(item => item.Folder?.FullPath == right));
        }
        finally
        {
            shell.Dispose();
        }
    }

    /// <summary>
    /// A folder launch leaves the drives of the pane it goes to unread until
    /// its folder is framed, and only that pane's: a split put back with the
    /// second pane worked with sends the launch there, and the first pane
    /// reads its drives as ever.  A flight that finds no folder later, in a
    /// pane a launch went to, leaves them to be read too.  And a second pane
    /// a launch went to is let go of when the split closes.
    /// </summary>
    private static async Task ProxyLaunchGateChecksAsync(string root)
    {
        Section("proxy panes: a folder launch's unread drives");
        var drive = Path.Combine(root, "launch-drive");
        var left = Path.Combine(drive, "left");
        var outside = Path.Combine(root, "launch-outside");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(outside);
        var main = ProxyWindow(out var shell);
        try
        {
            // ApplyFolderInvocation shows the window early (J097), which runs
            // its real Loaded path. Let persisted state settle before this
            // fixture deliberately primes a split, so Loaded cannot restore a
            // preceding test group's split over it halfway through the check.
            await shell.InitializeAsync();
            shell.Layout = CanvasLayout.Nested;
            var parsed = FolderCommandLine.TryOpenFolder(left, out var invocation, out _);
            Check("the launch is of a folder", parsed);
            if (!parsed)
            {
                return;
            }

            main.PrepareFolderInvocation(invocation);
            shell.IsSplit = true;
            shell.ActivePaneIndex = 1;
            await main.StartNestedForChecksAsync();
            await SettingsSettle();
            var first = main.FirstPane;
            var second = main.SecondPane;
            Check("started for a launch with the split put back and its second pane worked with, the first pane reads its drives",
                second is not null && ReferenceEquals(main.ActivePane, second) && first.Canvas.LoadUnfocusedRoots);
            if (second is null)
            {
                return;
            }

            main.UseNestedDrivesForChecks([new NestedRoot(drive, "L", NestedFolderKind.Drive)]);
            main.ActivatePane(first);

            // As a launch leaves its pane once its folder is framed.
            first.Canvas.LoadUnfocusedRoots = true;
            await first.FlyToAsync(outside, gentle: false, animated: false, isDirectory: true);
            Check("a flight that finds no folder, in the pane a launch went to, leaves its drives to be read",
                first.Canvas.LoadUnfocusedRoots && first.FlightsUnderWay == 0);

            // A launch into the second pane, which the split then closes. Its
            // sender gives up straight away: J097 has already presented the
            // window, but the request still reports that it was not readied.
            main.ActivatePane(second);
            var fields = BindingFlags.Instance | BindingFlags.NonPublic;
            ((TaskCompletionSource)typeof(MainWindow).GetField("_folderLaunchReady", fields)!.GetValue(main)!).TrySetResult();
            using (var abandoned = new CancellationTokenSource())
            {
                var launch = main.ApplyFolderInvocationAsync(invocation, abandoned.Token);
                abandoned.Cancel();
                Check("a launch given up after early presentation reports not ready without activating the test window",
                    !await launch && main.IsVisible && !main.IsActive);
            }

            var paneRoots = (IDictionary)typeof(MainWindow).GetField("_folderPaneRoots", fields)!.GetValue(main)!;
            var loadOwners = (IDictionary)typeof(MainWindow).GetField("_folderLoadOwners", fields)!.GetValue(main)!;
            Check("a launch into the second pane is the second pane's", paneRoots.Contains(second));
            shell.IsSplit = false;
            Check("closed, the second pane is let go of by the window, not kept for the launch it had",
                main.Panes.Count == 1 && !paneRoots.Contains(second) && !loadOwners.Contains(second));
        }
        finally
        {
            // J097 made this a real, visible window. Close its complete window
            // lifetime; disposing only the model leaves Application.MainWindow
            // and the pane controls alive for the rest of the child process.
            main.Close();
            var closed = await LiveWait(() => Application.Current?.Windows.OfType<MainWindow>().Contains(main) != true, 10_000) >= 0;
            if (!closed)
            {
                shell.Dispose();
            }
        }
    }

    /// <summary>
    /// Split again, the second pane goes back to where it was, which takes a
    /// read of the folders on the way, and meanwhile shows an overview of
    /// This PC.  Closed in that moment, the place it was going to is kept for
    /// the next split, not the overview it showed meanwhile.
    /// </summary>
    private static async Task ProxyCameraRestoreChecksAsync(string root)
    {
        Section("proxy panes: a camera on its way back");
        var drive = Path.Combine(root, "camera-drive");
        var deep = Path.Combine(drive, "deep", "deeper", "deepest");
        Directory.CreateDirectory(deep);
        var main = ProxyWindow(out var shell);
        try
        {
            await main.StartNestedForChecksAsync();
            main.UseNestedDrivesForChecks([new NestedRoot(drive, "C", NestedFolderKind.Drive)]);
            await SettingsSettle();
            var kept = new NestedCameraState(deep, 0.01, -0.02, 0.9);
            shell.Tree.SecondPane = new NestedPaneState(null, kept);
            shell.IsSplit = true;
            var second = main.SecondPane;
            Check("the split opens a second pane bound for where it was", second is not null);
            if (second is null)
            {
                return;
            }

            NestedCameraState? meanwhile = null;
            await main.Dispatcher.InvokeAsync(
                () =>
                {
                    // Straight after the pane set off, its folders still being
                    // read: a frame drawn now shows the overview, and the
                    // split is closed.
                    second.Canvas.ScreenRectOf(second.Tree.Root);
                    meanwhile = second.Canvas.CaptureCamera();
                    shell.IsSplit = false;
                },
                DispatcherPriority.Loaded);
            Check("closed while its camera was still on its way, it was showing the overview",
                meanwhile is { AnchorPath: "" } && main.Panes.Count == 1);
            Check("and the place kept for the next split is the one it was going to",
                shell.Tree.SecondPane?.NestedCamera == kept);
        }
        finally
        {
            shell.Dispose();
        }
    }

    /// <summary>
    /// A folder known only by the way to something in it - a beacon's chain -
    /// drawn beside the folder in view is listed like any folder drawn there.
    /// The folders the view is inside are still not listed for being drawn
    /// around it, until the camera goes into them.  The pane reads nothing
    /// for being drawn until its camera is in place.
    /// </summary>
    private static async Task ProxyPartialSiblingChecksAsync(string root)
    {
        Section("proxy panes: folders known by a chain");
        var drive = Path.Combine(root, "sibling-drive");
        var personal = Path.Combine(drive, "Personal");
        var work = Path.Combine(drive, "Work");
        var client = Path.Combine(work, "ClientA");
        var otherDrive = Path.Combine(root, "sibling-other");
        var outer = Path.Combine(otherDrive, "outer");
        var inner = Path.Combine(outer, "inner");
        var leaf = Path.Combine(inner, "leaf");
        foreach (var folder in new[] { Path.Combine(personal, "Photos"), Path.Combine(personal, "Taxes"), client, Path.Combine(work, "ClientB"), Path.Combine(work, "ClientC"), leaf })
        {
            Directory.CreateDirectory(folder);
        }

        var main = ProxyWindow(out var shell);
        try
        {
            await main.StartNestedForChecksAsync();
            var pane = main.FirstPane;
            var tree = pane.Tree;
            var canvas = pane.Canvas;
            tree.IsReadingOnDemand = false;
            main.UseNestedDrivesForChecks([new NestedRoot(drive, "P", NestedFolderKind.Drive), new NestedRoot(otherDrive, "Q", NestedFolderKind.Drive)]);

            // The view deep in a chain: the folders it is inside stay unlisted.
            await tree.MaterializePathAsync(leaf);
            canvas.FlyTo(tree.Find(leaf)!, 0.9, animated: false);
            await LiveWait(() => !canvas.IsCameraMoving, 2_000);
            Check("the folders the view is inside are still not listed for being drawn around it",
                canvas.Anchor?.FullPath == leaf
                && tree.Find(outer) is { HasPartialListing: true } around && tree.PartialListingReadAllowed?.Invoke(around) == false
                && tree.Find(inner) is { HasPartialListing: true } within && tree.PartialListingReadAllowed?.Invoke(within) == false);

            // Personal in view, and Work beside it known only by a beacon's chain.
            await tree.LoadAsync(tree.Find(drive)!);
            await tree.MaterializePathAsync(client);
            var workFolder = tree.Find(work);
            Check("a beacon's chain leaves Work known only by the way to ClientA",
                workFolder is { HasPartialListing: true, IsLoaded: false } && workFolder.AllChildren.Length == 1);
            if (workFolder is null)
            {
                return;
            }

            canvas.FlyTo(tree.Find(personal)!, 0.6, animated: false);
            await LiveWait(() => !canvas.IsCameraMoving, 2_000);
            canvas.RenderNow();
            var view = new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight);
            var beside = canvas.Anchor?.FullPath == personal && canvas.ScreenRectOf(workFolder) is { } cell
                && cell.Width >= NestedCanvas.LoadPixels && view.IntersectsWith(cell);
            Check("with Personal in view, Work is drawn beside it big enough to be read", beside);
            if (!beside)
            {
                return;
            }

            Check("drawn beside the folder in view, a folder known by a chain may be listed",
                tree.PartialListingReadAllowed?.Invoke(workFolder) == true);
            tree.IsReadingOnDemand = true;
            Check("and is: all its folders come in, not only the chain",
                await LiveWait(() => { canvas.RenderNow(); return workFolder.IsLoaded; }, 3_000) >= 0
                && workFolder.AllChildren.Length == 3);
        }
        finally
        {
            shell.Dispose();
        }
    }

    /// <summary>
    /// A share that has gone to sleep takes as long as the network allows to
    /// say a folder is not there - twenty seconds and more.  A flight to a
    /// path on one, and Shift+F5 into one, ask off the interface thread, and
    /// the window goes on answering meanwhile.  A file the pane has not read
    /// yet still takes the camera to its folder.  The address is one kept
    /// for documentation, which reaches nothing.
    /// </summary>
    private static async Task ProxyUnreachableShareChecksAsync(string root)
    {
        Section("proxy panes: a share that does not answer");
        var drive = Path.Combine(root, "share-drive");
        var folder = Path.Combine(drive, "folder");
        var file = Path.Combine(folder, "unread.txt");
        const string asleep = @"\\192.0.2.1\ultraexplorer-check\folder";
        const string otherAsleep = @"\\192.0.2.2\ultraexplorer-check\folder";
        Directory.CreateDirectory(folder);
        File.WriteAllText(file, "a file not read yet");
        var main = ProxyWindow(out var shell);
        try
        {
            await main.StartNestedForChecksAsync();
            main.UseNestedDrivesForChecks([new NestedRoot(drive, "U", NestedFolderKind.Drive)]);
            var pane = main.FirstPane;
            await pane.FlyToAsync(file, gentle: false, animated: false);
            Check("a file not read yet still takes the camera to its folder", pane.Canvas.Anchor?.FullPath == folder);

            var watch = Stopwatch.StartNew();
            _ = pane.FlyToAsync(asleep, gentle: false, animated: false);
            var flying = watch.ElapsedMilliseconds;
            Check($"a flight to a share that does not answer leaves the window answering ({flying} ms)", flying < 2_000);

            // The second pane at This PC, with the folder on the share selected.
            shell.Tree.SecondPane = new NestedPaneState(null, new NestedCameraState(string.Empty, 0, 0, 1));
            shell.IsSplit = true;
            var second = main.SecondPane;
            Check("split, there is a second pane", second is not null);
            if (second is null)
            {
                return;
            }

            second.KeptSelection.ReplaceSingle(otherAsleep, true, 0, SelectionSource.Navigation);
            Check("whose folder is the one on the share", main.OtherPaneFolder() == otherAsleep);
            watch.Restart();
            _ = main.SendToOtherPaneAsync([file], move: false);
            var sending = watch.ElapsedMilliseconds;
            Check($"and Shift+F5 into it leaves the window answering too ({sending} ms)", sending < 2_000);
        }
        finally
        {
            shell.Dispose();
        }
    }
}
