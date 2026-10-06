using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using UltraExplorer;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// What the review found that took more than one part of the program to put
/// right.  Of the tree and the canvas alone: a file written while its folder
/// is first read, a folder opened by name below one hidden from the canvas,
/// names ending in a dot or a space, and a favourite link's tip.  Of the
/// windows: a replaced dialog whose folder follows the camera the user
/// moved, drives that changed before the canvas was ready, Shift+F5 or
/// Shift+F6 pressed again while the first is still asking, and a flight
/// superseded while it asks the disk.
///
/// <para>The windows need the app, which lives on the thread that made it.
/// Run after the Settings checks, whose thread has finished with it, these
/// run in a process of their own, asked for alone, where they make it.</para>
/// </summary>
internal static partial class Program
{
    private static async Task CrossClusterReviewChecks()
    {
        if (Application.Current is not null)
        {
            await CrossClusterChecksInOwnProcessAsync();
            return;
        }

        RunOnSta("cross-cluster review: the tree", CrossClusterTreeChecksAsync);
        RunOnSta("cross-cluster review: the windows", CrossClusterOnStaAsync);
    }

    /// <summary>The checks of the nested tree alone, with real folders and a watch of their own.</summary>
    private static async Task CrossClusterTreeChecksAsync()
    {
        await FirstReadChangeChecksAsync();
        await HiddenByNameChecksAsync();
        await NameEndPathChecksAsync();
        await FavoriteTipChecksAsync();
    }

    // ---- a favourite link's tip (I124) ------------------------------------------------------

    /// <summary>
    /// Hovered, a favourite link's circle says what it is, where it goes and
    /// that a double-click goes there - three lines, each drawn as its own,
    /// where a laid-out text holds one line and the tip showed only the first.
    /// </summary>
    private static async Task FavoriteTipChecksAsync()
    {
        Section("cross-cluster: a favourite link's tip");
        var disk = new FakeDisk();
        disk.Folder(@"Q:\Users\User\Downloads");
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Local Disk (Q:)", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        var canvas = new NestedCanvas { Tree = tree };
        canvas.Measure(new Size(1600, 1000));
        canvas.Arrange(new Rect(0, 0, 1600, 1000));
        canvas.UpdateLayout();
        canvas.FitAll(animated: false);
        canvas.SetFavoriteLinks([new NestedFavoriteLink(@"Q:\Users\User\Downloads", "Downloads", System.Windows.Media.Color.FromRgb(230, 178, 83))]);
        canvas.ShowFavoriteLinks = true;
        canvas.RenderNow();
        var links = canvas.FavoriteLinkPositions();
        if (links.Count != 1)
        {
            Check("the favourite link is drawn", false);
            return;
        }

        new NestedPointer(canvas).Move(links[0].Centre);
        var overlay = (System.Windows.Media.DrawingVisual)typeof(NestedCanvas)
            .GetField("_overlay", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(canvas)!;
        // The tip's text as drawn, a line for each baseline: a line can be
        // drawn as several runs of glyphs ("Double", "-", "click ...").
        var runs = new List<(double Y, double X, string Text)>();
        void Collect(System.Windows.Media.Drawing? drawing)
        {
            switch (drawing)
            {
                case System.Windows.Media.DrawingGroup group:
                    foreach (var child in group.Children)
                    {
                        Collect(child);
                    }

                    break;
                case System.Windows.Media.GlyphRunDrawing { GlyphRun: { Characters: { } characters } run }:
                    runs.Add((Math.Round(run.BaselineOrigin.Y), run.BaselineOrigin.X, new string([.. characters])));
                    break;
            }
        }

        Collect(System.Windows.Media.VisualTreeHelper.GetDrawing(overlay));
        var lines = runs.GroupBy(run => run.Y).OrderBy(line => line.Key)
            .Select(line => string.Concat(line.OrderBy(run => run.X).Select(run => run.Text)).TrimEnd()).ToList();
        Check($"hovered, a favourite link's tip names it, says where it goes and how to go there ({string.Join(" | ", lines)})",
            lines.Count == 3 && lines[0] == "Downloads" && lines[1] == @"Q:\Users\User\Downloads"
            && lines[2].StartsWith("Double-click", StringComparison.Ordinal));
    }

    // ---- paths whose names end in a dot or a space (I045) --------------------------------

    /// <summary>
    /// A folder named "backup." beside "backup", and one named "notes " alone,
    /// as WSL, git or a share can leave them.  The one spelling of a path
    /// every map uses keeps such a name as it is; describing it by name - the
    /// way a path is brought in without reading the folders on the way -
    /// describes it and not its neighbour, or nothing; and a folder opened by
    /// name through one is reached.  Every other spelling is normalised as
    /// ever.
    /// </summary>
    private static async Task NameEndPathChecksAsync()
    {
        Section("cross-cluster: paths whose names end in a dot or a space");
        Check("a name ending in a dot keeps it in the one spelling of its path",
            ViewAllPath.Normalize(@"C:\data\backup.") == @"C:\data\backup."
            && ViewAllPath.Normalize(@"C:\data\backup.\inside\") == @"C:\data\backup.\inside"
            && ViewAllPath.Normalize(@"\\server\share\backup.") == @"\\server\share\backup.");
        Check("and so does a name ending in a space", ViewAllPath.Normalize(@"C:\data\notes ") == @"C:\data\notes ");
        Check("such a path is not the same as its neighbour's", !ViewAllPath.Equals(@"C:\data\backup.", @"C:\data\backup"));
        Check("any other path is normalised as ever",
            ViewAllPath.Normalize(@"C:\data\.\x\..\backup\") == @"C:\data\backup"
            && ViewAllPath.Normalize(@"C:/data//backup") == @"C:\data\backup"
            && ViewAllPath.Normalize("  \"C:\\data\\backup\"  ") == @"C:\data\backup"
            && ViewAllPath.Normalize(@"C:\") == @"C:\");

        var baseDirectory = Path.Combine(Path.GetTempPath(), "UltraExplorerNameEnds", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(baseDirectory, "data");
        var backup = Path.Combine(data, "backup");
        var dotted = backup + ".";
        var notes = Path.Combine(data, "notes ");
        Directory.CreateDirectory(Path.Combine(backup, "plain"));
        // Made through the extended-length form, which keeps the names' ends.
        const string ExtendedLength = @"\\?\";
        Directory.CreateDirectory(ExtendedLength + Path.Combine(dotted, "inside"));
        Directory.CreateDirectory(ExtendedLength + notes);
        try
        {
            var files = new ViewAllFileSystemService();
            var chain = await files.DescribeChainAsync(data, [dotted, Path.Combine(dotted, "inside")]);
            Check($"described by name, a folder ending in a dot is itself, not its neighbour ({string.Join(" | ", chain.Select(entry => $"'{entry.DisplayName}'"))})",
                chain.Count == 2 && chain[0].FullPath == dotted && chain[0].DisplayName == "backup."
                && chain[1].FullPath == Path.Combine(dotted, "inside"));
            var spaced = await files.DescribeChainAsync(data, [notes]);
            Check($"and one ending in a space with no neighbour is described at all ({spaced.Count})",
                spaced.Count == 1 && spaced[0].FullPath == notes && spaced[0].DisplayName == "notes " && spaced[0].Kind == ViewAllEntryKind.Folder);
            var entry = await files.DescribeEntryAsync(dotted);
            Check("so is one described on its own", entry.FullPath == dotted && entry.DisplayName == "backup.");
            var stale = await files.FindStaleAsync([dotted, notes, backup]);
            Check($"and none of them is taken for gone ({string.Join(", ", stale)})", !stale.Any(gone => gone));

            using var tree = new NestedTree();
            tree.SetRoots([new NestedRoot(baseDirectory, "N", NestedFolderKind.Drive)]);
            var inside = await tree.MaterializePathAsync(Path.Combine(dotted, "inside"));
            Check($"a folder opened by name through one is reached through it ({(inside is null ? "nothing" : $"'{inside.Parent?.Name}' / '{inside.Name}'")})",
                inside is not null && inside.FullPath == Path.Combine(dotted, "inside") && inside.Parent?.Name == "backup.");
        }
        finally
        {
            try
            {
                Directory.Delete(ExtendedLength + baseDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left in the temp folder.
            }
        }
    }

    // ---- a folder hidden from the canvas, and a path below it opened by name (I067) ------

    /// <summary>
    /// The user hid AppData from the canvas; a program then opens a folder
    /// inside it by name.  Going there is the user's word too, and the later
    /// one: the folder is reached and on the canvas, whether or not AppData's
    /// parent had been read.  A beacon's chain - a mark, a search result -
    /// does not open it, and hiding again hides it again.
    /// </summary>
    private static async Task HiddenByNameChecksAsync()
    {
        Section("cross-cluster: a folder hidden from the canvas, and a path below it opened by name");
        var baseDirectory = Path.Combine(Path.GetTempPath(), "UltraExplorerHiddenByName", Guid.NewGuid().ToString("N"));
        var me = Path.Combine(baseDirectory, "Users", "me");
        var appData = Path.Combine(me, "AppData");
        var foo = Path.Combine(appData, "Roaming", "Foo");
        Directory.CreateDirectory(Path.Combine(foo, "inner"));
        Directory.CreateDirectory(Path.Combine(me, "Documents"));
        try
        {
            NestedTree Tree()
            {
                var tree = new NestedTree();
                tree.SetRoots([new NestedRoot(baseDirectory, "H", NestedFolderKind.Drive)]);
                tree.SetUserHidden([appData]);
                return tree;
            }

            using (var tree = Tree())
            {
                var opened = await tree.MaterializePathAsync(foo);
                Check($"a folder inside one hidden from the canvas, opened by name before anything was read, is reached ({(opened is null ? "nothing" : Path.GetRelativePath(baseDirectory, opened.FullPath))})",
                    opened is not null && ViewAllPath.Equals(opened.FullPath, foo) && NestedTree.IsOnCanvas(opened));
                Check("and the folder hidden stays on the user's hidden list", tree.IsUserHidden(appData));

                tree.SetUserHidden([appData]);
                Check("passive synchronization of the same hidden set keeps the explicitly opened folder visible",
                    opened is not null && NestedTree.IsOnCanvas(opened));
                tree.HideFromCanvas([appData]);
                Check("hiding it again hides it again, with the folder opened inside it",
                    tree.Find(appData) is { Index: < 0 } && tree.Find(foo) is { } hidden && !NestedTree.IsOnCanvas(hidden));
            }

            using (var tree = Tree())
            {
                await tree.LoadAsync(tree.Root.Children.Single());
                await tree.LoadAsync(tree.Find(Path.Combine(baseDirectory, "Users"))!);
                await tree.LoadAsync(tree.Find(me)!);
                var filtered = tree.Find(appData) is { Index: < 0 };
                var opened = await tree.MaterializePathAsync(foo);
                Check($"and so is one opened by name after its parent was read and the folder hidden left out ({(opened is null ? "nothing" : Path.GetRelativePath(baseDirectory, opened.FullPath))})",
                    filtered && opened is not null && ViewAllPath.Equals(opened.FullPath, foo) && NestedTree.IsOnCanvas(opened)
                    && tree.Find(appData) is { Index: >= 0 });
            }

            using (var tree = Tree())
            {
                var beacon = await tree.MaterializeContainerAsync(foo);
                Check("a beacon's chain below the folder hidden leaves it hidden", beacon is null && tree.Find(appData) is not { Index: >= 0 });
            }
        }
        finally
        {
            TryDelete(baseDirectory);
        }
    }

    // ---- a change heard while a folder is first read (I069) ------------------------------

    /// <summary>
    /// A file written in a folder while its first read is under way - after
    /// the listing was taken, before it was applied - is shown once the
    /// folder is drawn again, whether the read was asked for by the canvas
    /// drawing the folder or by name.  The reads are held between the two
    /// so the file always comes in that moment.
    /// </summary>
    private static async Task FirstReadChangeChecksAsync()
    {
        Section("cross-cluster: a file written while its folder is first read");
        var baseDirectory = Path.Combine(Path.GetTempPath(), "UltraExplorerFirstRead", Guid.NewGuid().ToString("N"));
        var drawn = Path.Combine(baseDirectory, "drawn");
        var named = Path.Combine(baseDirectory, "named");
        Directory.CreateDirectory(drawn);
        Directory.CreateDirectory(named);
        File.WriteAllText(Path.Combine(drawn, "before.txt"), "before");
        File.WriteAllText(Path.Combine(named, "before.txt"), "before");
        var holds = new System.Collections.Concurrent.ConcurrentDictionary<string, (ManualResetEventSlim Listed, ManualResetEventSlim Release)>(StringComparer.OrdinalIgnoreCase);
        NestedListing Read(string path, CancellationToken cancellationToken)
        {
            var listing = NestedDirectoryReader.Read(path, cancellationToken);
            if (holds.TryRemove(path, out var hold))
            {
                hold.Listed.Set();
                hold.Release.Wait(TimeSpan.FromSeconds(10));
            }

            return listing;
        }

        try
        {
            using var hub = new ChangeHub(TimeProvider.System);
            var watch = hub.AddRootForTests(baseDirectory);
            using var tree = new NestedTree(Read);
            hub.Driver.Fallback = DispatcherFrameDriver.ForCurrentThread((ref FrameBudget budget) => hub.Drain(ref budget, tree), () => hub.HasWork);
            tree.Changes = hub;
            tree.SetRoots([new NestedRoot(baseDirectory, "W", NestedFolderKind.Drive)]);
            await tree.LoadAsync(tree.Root.Children.Single());
            var armed = await LiveWait(() => watch.State == WatchState.Armed, 3_000) >= 0;
            Check("the folders are listed and watched", armed && tree.Find(drawn) is { IsLoaded: false } && tree.Find(named) is { IsLoaded: false });

            async Task<bool> DrawUntil(string path, Func<NestedFolder, bool> condition)
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 3_000)
                {
                    tree.BeginFrame();
                    if (tree.Find(path) is { } folder)
                    {
                        tree.Request(folder, 400);
                        if (condition(folder))
                        {
                            return true;
                        }
                    }

                    await Task.Delay(8);
                }

                return false;
            }

            // A file written while the read is held, then the read let go.
            async Task WriteWhileHeld(string folder, ManualResetEventSlim listed, ManualResetEventSlim release)
            {
                await LiveWait(() => listed.IsSet, 5_000);
                File.WriteAllText(Path.Combine(folder, "during.txt"), "written while the folder was read");
                await Task.Delay(500);
                release.Set();
            }

            static bool Shows(NestedFolder folder) => folder.IsLoaded && folder.AllFiles.Any(file => file.Name == "during.txt");

            // The canvas draws the folder for the first time.
            using (var listed = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                holds[drawn] = (listed, release);
                tree.BeginFrame();
                tree.Request(tree.Find(drawn)!, 400);
                await WriteWhileHeld(drawn, listed, release);
                var shown = await DrawUntil(drawn, Shows);
                Check($"a folder first read for being drawn shows the file written during its read ({string.Join(", ", tree.Find(drawn)?.AllFiles.Select(file => file.Name) ?? [])})",
                    listed.IsSet && shown);
            }

            // The folder is read by name.
            using (var listed = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                holds[named] = (listed, release);
                var loading = tree.LoadAsync(tree.Find(named)!);
                await WriteWhileHeld(named, listed, release);
                await loading;
                var shown = await DrawUntil(named, Shows);
                Check($"and so does one first read by name ({string.Join(", ", tree.Find(named)?.AllFiles.Select(file => file.Name) ?? [])})",
                    listed.IsSet && shown);
            }
        }
        finally
        {
            foreach (var (_, hold) in holds)
            {
                hold.Release.Set();
            }

            TryDelete(baseDirectory);
        }
    }

    /// <summary>
    /// The checks run by this program again, alone, in a process of their
    /// own: each of its results is counted here.  One that hangs is ended
    /// after ten minutes and counted as a failure, so the rest of the run
    /// goes on.
    /// </summary>
    private static async Task CrossClusterChecksInOwnProcessAsync()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!, "--only CrossClusterReviewChecks")
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

            Check("the cross-cluster review checks' own process finished within ten minutes", false);
            return;
        }

        Check("the cross-cluster review checks' own process ran to the end", finished);
    }

    private static async Task CrossClusterOnStaAsync()
    {
        new App().InitializeComponent();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var stateOverride = Environment.GetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR");
        var isolated = !string.IsNullOrWhiteSpace(stateOverride)
            && Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1"
            && !Path.GetFullPath(stateOverride).Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer"), StringComparison.OrdinalIgnoreCase);
        Check("the cross-cluster review windows require isolated state and test-window mode", isolated);
        if (!isolated)
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerCrossCluster", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await PickerCameraFolderChecksAsync(root);
            await EarlyDriveChangeChecksAsync();
            await RepeatedPaneTransferChecksAsync(root);
            await SupersededFlightChecksAsync(root);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- a replaced dialog's folder is the one the user zoomed into (I014) ---------------

    /// <summary>
    /// A replaced Save As opened at Documents: the user zooms into Invoices
    /// and types a name, and the file is saved in Invoices.  A Select Folder
    /// zoomed into Invoices the same way answers Invoices.  The camera moved
    /// by the program - the dialog framing its folder, a flight - moves the
    /// dialog nowhere, however wide the window is.
    /// </summary>
    private static async Task PickerCameraFolderChecksAsync(string root)
    {
        Section("cross-cluster: a replaced dialog's folder follows the camera the user moves");
        var documents = Path.Combine(root, "Documents");
        var invoices = Path.Combine(documents, "Invoices");
        foreach (var name in new[] { "Invoices", "Letters", "Photos", "Taxes", "Recipes", "Travel" })
        {
            Directory.CreateDirectory(Path.Combine(documents, name));
        }

        File.WriteAllText(Path.Combine(documents, "notes.txt"), "notes");
        File.WriteAllText(Path.Combine(invoices, "january.txt"), "invoice");

        static FileDialogRequest Request(string folder, FileDialogMode mode)
        {
            var request = new FileDialogRequest { IsNativeProxy = true, Mode = mode, InitialFolder = folder };
            request.Filters.Add(new FileDialogFilterSpec("All files (*.*)", "*.*"));
            return request;
        }

        MainWindow? picker = null;
        try
        {
            var session = new FileDialogSession(Request(documents, FileDialogMode.Save));
            picker = new MainWindow(session) { Width = 1100, Height = 760, WindowState = WindowState.Normal };
            picker.PrepareAsCloakedPicker(_ => { });
            using (ActivationGuard.GuardWindowsCreated())
            {
                picker.Show();
            }

            await picker.WhenFolderDrawnAsync(TimeSpan.FromSeconds(5));

            // A Save As bound to the prepared picker: zoomed by hand into Invoices.
            await picker.RebindAsync(session = new FileDialogSession(Request(documents, FileDialogMode.Save)));
            picker.ConfirmContract();
            var drawn = await picker.WhenFolderDrawnAsync(TimeSpan.FromSeconds(5));
            Check("a Save As bound to the picker opens at Documents", drawn && ViewAllPath.Equals(session.CurrentFolder, documents));
            var framed = await ZoomIntoByHandAsync(picker, invoices, keys: false);
            Check("the user zooms into Invoices with a drag and the wheel", framed);
            var followed = await LiveWait(() => ViewAllPath.Equals(session.CurrentFolder, invoices), 3_000) >= 0;
            Check($"the Save As is then in Invoices ({Path.GetFileName(session.CurrentFolder)})", followed);
            session.FileNameText = "march.txt";
            var save = session.Prepare(picker.PickerSelection);
            Check($"and a name typed there is saved in Invoices ({string.Join(" | ", save.Paths.Select(path => Path.GetRelativePath(documents, path)))})",
                save.Kind == FileDialogActionKind.Accept && save.Paths.Count == 1
                && string.Equals(save.Paths[0], Path.Combine(invoices, "march.txt"), StringComparison.OrdinalIgnoreCase));

            // A Select Folder, zoomed into Invoices with the zoom keys.
            await picker.RebindAsync(session = new FileDialogSession(Request(documents, FileDialogMode.PickFolder)));
            picker.ConfirmContract();
            await picker.WhenFolderDrawnAsync(TimeSpan.FromSeconds(5));
            framed = await ZoomIntoByHandAsync(picker, invoices, keys: true);
            Check("the user zooms into Invoices of a Select Folder with the zoom keys", framed);
            followed = await LiveWait(() => ViewAllPath.Equals(session.CurrentFolder, invoices), 3_000) >= 0;
            var chosen = session.Prepare(picker.PickerSelection);
            Check($"Select Folder then answers Invoices, not the folder it opened at ({string.Join(" | ", chosen.Paths.Select(Path.GetFileName))})",
                followed && chosen.Kind == FileDialogActionKind.Accept && chosen.Paths.Count == 1
                && ViewAllPath.Equals(chosen.Paths[0], invoices));

            // A scroll of the wheel within Documents leaves the dialog there:
            // no folder inside it fills the view.
            await picker.RebindAsync(session = new FileDialogSession(Request(documents, FileDialogMode.Save)));
            picker.ConfirmContract();
            await picker.WhenFolderDrawnAsync(TimeSpan.FromSeconds(5));
            var canvas = picker.ActivePane.Canvas;
            new NestedPointer(canvas).Wheel(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2), -120);
            await Task.Delay(600);
            Check($"a scroll of the wheel within Documents leaves the dialog there ({Path.GetFileName(session.CurrentFolder)})",
                ViewAllPath.Equals(session.CurrentFolder, documents));

            // The program's own moves: a flight into Invoices moves the dialog nowhere.
            if (picker.ActivePane.Tree.Find(invoices) is { } flown)
            {
                picker.ActivePane.Canvas.FlyTo(flown, 0.92, animated: false);
            }

            await Task.Delay(600);
            Check($"a flight the program makes into Invoices leaves the dialog at Documents ({Path.GetFileName(session.CurrentFolder)})",
                ViewAllPath.Equals(session.CurrentFolder, documents));

            // Bound in a window far wider than it is tall, where the folder
            // framed is not the one covering the whole view.
            picker.Width = 2200;
            picker.Height = 640;
            await picker.RebindAsync(session = new FileDialogSession(Request(documents, FileDialogMode.Save)));
            picker.ConfirmContract();
            await picker.WhenFolderDrawnAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(600);
            Check($"bound in a wide window and left alone, the dialog stays at Documents ({Path.GetFileName(session.CurrentFolder)})",
                ViewAllPath.Equals(session.CurrentFolder, documents));
        }
        finally
        {
            picker?.CloseFromCaller();
        }
    }

    // ---- drives that changed before the nested canvas was ready (I147) -----------------

    /// <summary>
    /// A stick plugged in or pulled out while the window is still starting:
    /// the drives are listed again before the nested canvas is ready, which
    /// leaves its panes alone - and once it is ready, they are listed again
    /// for it, so no pane misses the drive until the next device message.
    /// </summary>
    private static async Task EarlyDriveChangeChecksAsync()
    {
        Section("cross-cluster: drives that changed before the nested canvas was ready");
        var main = ProxyWindow(out var shell);
        try
        {
            var rescan = typeof(MainWindow).GetMethod("RescanDrivesAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task)rescan.Invoke(main, null)!;
            Check("the drives listed again while the nested canvas is starting leave its panes alone", main.DriveRescans == 0);
            await main.StartNestedForChecksAsync();
            var listed = await LiveWait(() => main.DriveRescans >= 1, 5_000) >= 0;
            var drives = await new ViewAllFileSystemService().GetDriveRootsAsync();
            var shown = main.FirstPane.Tree.Root.AllChildren.Select(folder => folder.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Check($"once the canvas is ready the drives are listed again for it ({main.DriveRescans} rescans; {string.Join(", ", shown)})",
                listed && drives.All(drive => shown.Contains(drive.FullPath)));
        }
        finally
        {
            shell.Dispose();
        }
    }

    // ---- Shift+F5 or Shift+F6 pressed again while the first still asks (I143b) ------------

    /// <summary>
    /// Copy or Move to other pane asks off the interface thread whether the
    /// other pane's folder is there - a share gone to sleep takes its time -
    /// and the window goes on taking keys meanwhile.  The same key pressed
    /// again in that moment is let go, rather than copying everything twice
    /// or moving it twice.  The items sent are not there, so nothing is
    /// handed to the Shell: each transfer only says so on the toast, which
    /// is what is counted.
    /// </summary>
    private static async Task RepeatedPaneTransferChecksAsync(string root)
    {
        Section("cross-cluster: Shift+F6 pressed again while the first is still asking");
        var drive = Path.Combine(root, "transfer-drive");
        var target = Path.Combine(drive, "target");
        Directory.CreateDirectory(target);
        var missing = Path.Combine(drive, "gone.txt");
        var main = ProxyWindow(out var shell);
        try
        {
            await main.StartNestedForChecksAsync();
            main.UseNestedDrivesForChecks([new NestedRoot(drive, "T", NestedFolderKind.Drive)]);
            shell.Tree.SecondPane = new NestedPaneState(null, new NestedCameraState(string.Empty, 0, 0, 1));
            shell.IsSplit = true;
            if (main.SecondPane is not { } second)
            {
                Check("split, there is a second pane", false);
                return;
            }

            second.KeptSelection.ReplaceSingle(target, true, 0, SelectionSource.Navigation);
            var transfers = 0;
            void OnToast(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(OperationToastService.IsVisible) && shell.Toast.IsVisible
                    && shell.Toast.Message.StartsWith("This drop target is not valid", StringComparison.Ordinal))
                {
                    transfers++;
                    shell.Toast.Hide();
                }
            }

            shell.Toast.PropertyChanged += OnToast;
            try
            {
                var overlapped = false;
                for (var attempt = 0; attempt < 3 && !overlapped; attempt++)
                {
                    transfers = 0;
                    var first = main.SendToOtherPaneAsync([missing], move: true);
                    overlapped = !first.IsCompleted;
                    var again = main.SendToOtherPaneAsync([missing], move: true);
                    await Task.WhenAll(first, again);
                }

                Check($"pressed again while the first still asks, the same transfer goes once ({transfers} transfers)", overlapped && transfers == 1);
                transfers = 0;
                await main.SendToOtherPaneAsync([missing], move: true);
                Check($"and once it has asked, the key works again ({transfers} transfer)", transfers == 1);
            }
            finally
            {
                shell.Toast.PropertyChanged -= OnToast;
            }
        }
        finally
        {
            shell.Dispose();
        }
    }

    // ---- a flight superseded while it asks whether its path is a folder (I031b) ----------

    /// <summary>
    /// A flight to a path the tree does not have yet asks the disk, off the
    /// interface thread, whether it is a folder.  Another flight asked for
    /// meanwhile is the one that counts: the first brings nothing of its
    /// path into the tree once it hears back, and the camera goes where the
    /// later one went.
    /// </summary>
    private static async Task SupersededFlightChecksAsync(string root)
    {
        Section("cross-cluster: a flight superseded while it asks whether its path is a folder");
        var drive = Path.Combine(root, "flight-drive");
        var asked = Path.Combine(drive, "asked", "deeper", "deepest");
        var other = Path.Combine(drive, "other");
        Directory.CreateDirectory(asked);
        Directory.CreateDirectory(other);
        var main = ProxyWindow(out var shell);
        try
        {
            await main.StartNestedForChecksAsync();
            main.UseNestedDrivesForChecks([new NestedRoot(drive, "F", NestedFolderKind.Drive)]);
            var pane = main.FirstPane;
            var first = pane.FlyToAsync(asked, gentle: false, animated: false);
            var overlapped = !first.IsCompleted;
            await pane.FlyToAsync(other, gentle: false, animated: false, isDirectory: true);
            await first;
            Check($"a flight superseded while it asked brings nothing of its path into the tree ({(pane.Tree.FindNearest(asked) is { } nearest ? Path.GetRelativePath(drive, nearest.FullPath) : "nothing")})",
                overlapped && pane.Tree.Find(asked) is null && pane.Tree.Find(Path.Combine(drive, "asked")) is null);
            Check("and the camera is where the later one went", pane.Canvas.Anchor?.FullPath == other);
        }
        finally
        {
            shell.Dispose();
        }
    }

    /// <summary>
    /// Zooms the picker's canvas into <paramref name="path"/> the way a user
    /// does: a middle-button drag brings it to the middle of the view, then
    /// the wheel with Ctrl - or the zoom-in command the keys run - makes it
    /// fill most of the view.  True once it does.
    /// </summary>
    private static async Task<bool> ZoomIntoByHandAsync(MainWindow picker, string path, bool keys)
    {
        var canvas = picker.ActivePane.Canvas;
        if (picker.ActivePane.Tree.Find(path) is not { } folder || canvas.ScreenRectOf(folder) is not { } cell)
        {
            return false;
        }

        var pointer = new NestedPointer(canvas);
        var middle = new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2);
        var from = new Point(cell.X + cell.Width / 2, cell.Y + cell.Height / 2);
        pointer.Down(MouseButton.Middle, from);
        pointer.Move(from + (middle - from) / 2);
        pointer.Move(middle);
        pointer.Up(MouseButton.Middle, middle);
        await Task.Delay(50);

        var shell = (MainViewModel)picker.DataContext;
        for (var step = 0; step < 40; step++)
        {
            if (canvas.ScreenRectOf(folder) is not { } now)
            {
                return false;
            }

            if (now.Width >= canvas.ActualWidth * 0.8 || now.Height >= canvas.ActualHeight * 0.8)
            {
                return now.Contains(middle);
            }

            if (keys)
            {
                shell.ZoomInCommand.Execute(null);
            }
            else
            {
                pointer.Wheel(middle, 120, ModifierKeys.Control);
            }

            await Task.Delay(20);
        }

        return false;
    }
}
