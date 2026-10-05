using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using UltraExplorer;
using UltraExplorer.Models;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// What the 2026-10-02 fix of I002 left: as a window starts, its tree asks
/// every drive whether it is ready - and for its label, format and sizes - one
/// after the other, and went on only once the last had answered.  A drive
/// mapped to a server that is off answers after some twenty seconds, so the
/// window Explorer's replacement made was closed by the broker's fifteen, and
/// a dialog worker's prepared picker was not ready in time.  Here one drive is
/// held from answering (<see cref="ViewAllFileSystemService.DescribeDrive"/>),
/// with no network involved: the tree, a folder window and a dialog's picker
/// must be ready well within those deadlines with every other drive listed,
/// and the held drive must come in where a start that waited for it would have
/// put it, named and described the same, once it answers.
///
/// <para>The navigation pane lists its drives apart, through
/// <see cref="FileSystemService.FolderExists"/>, which is held for the same
/// drive: the window must not wait for its pane's drives either.</para>
///
/// <para>The windows need the app, which lives on the thread that made it;
/// once another group has made it, these run in a process of their own.</para>
/// </summary>
internal static partial class Program
{
    /// <summary>How long the held drive is kept from answering at most: longer than the broker waits for a window it made.</summary>
    private static readonly TimeSpan DriveGateHoldLimit = TimeSpan.FromSeconds(20);

    /// <summary>How soon a tree or a window must be ready while a drive is held: well within the worker's twelve seconds and the broker's fifteen.</summary>
    private const int DriveGateReadyLimit = 6_000;

    private static async Task DriveGateChecks()
    {
        if (Application.Current is not null)
        {
            await DriveGateChecksInOwnProcessAsync();
            return;
        }

        RunOnSta("drive gate", DriveGateOnStaAsync);
    }

    /// <summary>
    /// The checks run by this program again, alone, in a process of their
    /// own: each of its results is counted here.  One that hangs is ended
    /// after ten minutes and counted as a failure, so the rest of the run
    /// goes on.
    /// </summary>
    private static async Task DriveGateChecksInOwnProcessAsync()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!, "--only DriveGateChecks")
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

            Check("the drive gate checks' own process finished within ten minutes", false);
            return;
        }

        Check("the drive gate checks' own process ran to the end", finished);
    }

    private static async Task DriveGateOnStaAsync()
    {
        // What a start that waits for every drive lists, as it lists it.
        var drives = await new ViewAllFileSystemService().GetDriveRootsAsync();
        var temp = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()));
        var candidates = drives.Where(drive => !ViewAllPath.Equals(drive.FullPath, temp ?? string.Empty)).ToArray();

        // One with a drive before it, so it has to come in between.
        var held = candidates.FirstOrDefault(drive => !ReferenceEquals(drive, drives[0])) ?? candidates.FirstOrDefault();
        if (held is null)
        {
            Console.WriteLine("  note  no drive here but the one the temporary folder is on; the drive gate is not checked");
            return;
        }

        Console.WriteLine($"  note  held: {held.DisplayName}, of {string.Join(", ", drives.Select(drive => drive.FullPath))}");
        await DriveGateTreeChecksAsync(drives, held);
        await DriveGateRestoredTreeChecksAsync(held);

        new App().InitializeComponent();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var stateOverride = Environment.GetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR");
        var isolated = !string.IsNullOrWhiteSpace(stateOverride)
            && Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1"
            && !Path.GetFullPath(stateOverride).Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer"), StringComparison.OrdinalIgnoreCase);
        Check("the drive gate windows require isolated state and test-window mode", isolated);
        if (!isolated)
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerDriveGate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await DriveGateFolderWindowChecksAsync(root, drives, held);
            await DriveGatePickerChecksAsync(root, drives, held);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- the tree ----------------------------------------------------------------------

    /// <summary>
    /// The tree alone: ready while the drive is held, with the others as its
    /// roots, each asked at the same time rather than after the one before;
    /// then the held one in its place once it answers.
    /// </summary>
    private static async Task DriveGateTreeChecksAsync(IReadOnlyList<ViewAllEntryDescriptor> drives, ViewAllEntryDescriptor held)
    {
        Section("drive gate: the tree starts with the drives that answered, and takes a slow one in when it does (I002)");
        var others = drives.Where(drive => !ReferenceEquals(drive, held)).ToArray();
        using var hold = new HeldDrive(held.FullPath);
        using var graph = new ViewAllGraphService();
        var watch = Stopwatch.StartNew();
        var starting = graph.InitializeAsync();
        var ready = await Task.WhenAny(starting, Task.Delay(DriveGateReadyLimit)) == starting;
        Check($"the tree is ready while {held.DisplayName} is slow to answer ({watch.ElapsedMilliseconds} ms; limit {DriveGateReadyLimit} ms)", ready);
        Check($"its roots are the drives that answered, in their order, with their names ({string.Join(", ", graph.Roots.Select(node => node.FullPath))})",
            ready && graph.Roots.Select(DriveGateLine).SequenceEqual(others.Select(DriveGateLine)));
        Check("every other drive was asked at once, and answered while the slow one was still held",
            others.All(drive => hold.AnsweredWhileHeld(drive.FullPath)));

        hold.Release();
        await starting;
        var arrived = await LiveWait(() => graph.Roots.Any(node => ViewAllPath.Equals(node.FullPath, held.FullPath)), 10_000) >= 0;
        Check($"once it answers, it is a root where a start that waited would have put it, named and described the same ({string.Join(", ", graph.Roots.Select(node => node.FullPath))})",
            arrived && graph.Roots.Select(DriveGateLine).SequenceEqual(drives.Select(DriveGateLine)));
        Check("and nothing else came in with it", graph.Roots.Count == drives.Count);
    }

    /// <summary>
    /// A tree put back from a workspace that had the held drive open: ready
    /// all the same, saving it meanwhile keeps the drive open for next time,
    /// and once the drive answers it is opened again.
    /// </summary>
    private static async Task DriveGateRestoredTreeChecksAsync(ViewAllEntryDescriptor held)
    {
        Section("drive gate: what was open on a slow drive waits for it, and is opened again when it answers (I002)");
        using var hold = new HeldDrive(held.FullPath);
        using var graph = new ViewAllGraphService();
        var state = new ViewAllWorkspaceState
        {
            Nodes = [new ViewAllNodeState(held.FullPath, 0, 0, HasManualPosition: false, IsExpanded: true)],
            ActivePath = held.FullPath
        };
        var starting = graph.InitializeAsync(state, deferExtraRoots: true);
        var ready = await Task.WhenAny(starting, Task.Delay(DriveGateReadyLimit)) == starting;
        Check("a tree put back from a workspace is ready while the drive it had open is slow to answer", ready);
        if (ready)
        {
            var saved = graph.CaptureState(new ViewAllViewportState(default, 1));
            Check("saved meanwhile, the workspace still has that drive open",
                saved.Nodes.Any(node => ViewAllPath.Equals(node.Path, held.FullPath) && node.IsExpanded));
            Check("and does not take it for a share to look for", !saved.ExtraRoots.Any(path => ViewAllPath.Equals(path, held.FullPath)));
            Check("a folder on it counts as not here yet", graph.IsInPendingRoot(Path.Combine(held.FullPath, "folder")));
        }

        hold.Release();
        await starting;
        var opened = await LiveWait(() => graph.TryGetNode(held.FullPath, out var node) && node.IsExpanded && node.AreChildrenLoaded, 15_000) >= 0;
        Check("once it answers, the drive is opened again as it was left", opened);
        Check("and nothing on it counts as not here any more", !graph.IsInPendingRoot(Path.Combine(held.FullPath, "folder")));
    }

    // ---- the windows -------------------------------------------------------------------

    /// <summary>
    /// A folder window as the broker makes one, with a workspace of its own
    /// that had the held drive selected and in view: never shown, so what is
    /// timed is what its Loaded awaits before it says it is ready - the
    /// shell's state, the tree and the nested canvas's drives.
    /// </summary>
    private static async Task DriveGateFolderWindowChecksAsync(string root, IReadOnlyList<ViewAllEntryDescriptor> drives, ViewAllEntryDescriptor held)
    {
        Section("drive gate: a folder window is ready while a drive is slow to answer, and shows it once it does (I002)");
        var folder = Path.Combine(root, "window");
        Directory.CreateDirectory(folder);
        var workspace = Path.Combine(root, "window-workspace.json");
        await new WorkspaceStore(workspace).SaveAsync(new WorkspaceState { CanvasRenderer = "Cpu" });
        File.WriteAllText(workspace + ".marks.json", "{}");
        var camera = new NestedCameraState(held.FullPath, 0.01, -0.02, 0.9);
        await new ViewAllWorkspaceStore(workspace + ".tree.json").SaveAsync(new ViewAllWorkspaceState
        {
            Nodes = [new ViewAllNodeState(held.FullPath, 0, 0, HasManualPosition: false, IsExpanded: false)],
            ActivePath = held.FullPath,
            NestedCamera = camera
        });

        var others = drives.Where(drive => !ReferenceEquals(drive, held)).ToArray();
        var rendererBefore = GpuBootstrap.Preference;
        var hold = new HeldDrive(held.FullPath);
        MainViewModel? shell = null;
        try
        {
            var main = new MainWindow(null, workspace);
            shell = (MainViewModel)main.DataContext;
            LayOutWindow(main, 1400, 900);
            var watch = Stopwatch.StartNew();
            var starting = StartFolderWindowAsync(main, shell, folder);
            var ready = await Task.WhenAny(starting, Task.Delay(DriveGateReadyLimit)) == starting;
            Check($"the window's state, tree and canvas are up while {held.DisplayName} is slow to answer ({watch.ElapsedMilliseconds} ms; limit {DriveGateReadyLimit} ms)", ready);
            Check("its canvas shows the drives that answered, in their order, with their names",
                ready && main.FirstPane.Tree.Root.AllChildren.Select(DriveGateCell).SequenceEqual(others.Select(DriveGateCell)));
            Check("the navigation pane asked every other drive whether it is ready at once, not after the slow one",
                others.All(drive => hold.PaneAnsweredWhileHeld(drive.FullPath)));

            // The camera is put back once the panes are entered; give it the
            // moment it takes, so it has missed the drive by the time it answers.
            await SettingsSettle();
            hold.Release();
            await starting;
            var listed = await LiveWait(() => drives.All(drive => shell.Drives.Any(item => ViewAllPath.Equals(item.Path, drive.FullPath))), 10_000) >= 0;
            Check($"once it answers, the navigation pane lists every drive, it among them ({string.Join(", ", shell.Drives.Select(item => item.Path))})", listed);
            var arrived = await LiveWait(() => main.FirstPane.Tree.Root.AllChildren.Any(drive => ViewAllPath.Equals(drive.FullPath, held.FullPath)), 10_000) >= 0;
            Check($"once it answers, the canvas has it where a start that waited would have, named and described the same ({string.Join(", ", main.FirstPane.Tree.Root.AllChildren.Select(drive => drive.FullPath))})",
                arrived && main.FirstPane.Tree.Root.AllChildren.Select(DriveGateCell).SequenceEqual(drives.Select(DriveGateCell)));
            Check("and so has the tree", shell.Tree.Roots.Select(DriveGateLine).SequenceEqual(drives.Select(DriveGateLine)));
            var selected = await LiveWait(() => shell.Tree.ActiveNode is { } active && ViewAllPath.Equals(active.FullPath, held.FullPath), 5_000) >= 0;
            Check($"and the drive selected last time is selected again ({shell.Tree.ActiveNode?.FullPath})", selected);

            // The saved camera has the drive's cell nine tenths of the view
            // wide, against a fifth or so in the overview; the view may have
            // been laid out again at another width since the camera was put
            // back, which keeps the cell's size.  The camera itself may be
            // kept from a folder around the cell.
            Rect? DriveCell() => main.FirstPane.Tree.Find(held.FullPath) is { } cell ? main.FirstPane.Canvas.ScreenRectOf(cell) : null;
            bool CameraBack() => DriveCell() is { } rect
                && main.FirstPane.Canvas.ActualWidth is > 0 and var width
                && rect.Width / width is > 0.6 and < 1.3;
            var back = await LiveWait(CameraBack, 5_000) >= 0;
            Check($"and the camera the last session left on it is put back (its cell at {DriveCell()} in a view {main.FirstPane.Canvas.ActualWidth:0} wide)", back);
        }
        finally
        {
            hold.Dispose();
            shell?.Dispose();
            GpuBootstrap.UseSavedPreference(rendererBefore);
        }
    }

    /// <summary>What a folder window's Loaded awaits before it says it is ready, but for the layout.</summary>
    private static async Task StartFolderWindowAsync(MainWindow main, MainViewModel shell, string folder)
    {
        await shell.InitializeAsync(folder);
        await main.StartNestedForChecksAsync();
    }

    /// <summary>
    /// A dialog's picker, made and shown cloaked as a dialog worker prepares
    /// one: the window says it is ready, and draws its folder, while the held
    /// drive is slow; the drive is among its tiles once it answers.
    /// </summary>
    private static async Task DriveGatePickerChecksAsync(string root, IReadOnlyList<ViewAllEntryDescriptor> drives, ViewAllEntryDescriptor held)
    {
        Section("drive gate: a dialog's picker is ready while a drive is slow to answer, and shows it once it does (I002)");
        var folder = Path.Combine(root, "picker");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "letter.txt"), "letter");
        var request = new FileDialogRequest { IsNativeProxy = true, InitialFolder = folder };
        request.Filters.Add(new FileDialogFilterSpec("All files (*.*)", "*.*"));
        var others = drives.Where(drive => !ReferenceEquals(drive, held)).ToArray();
        var hold = new HeldDrive(held.FullPath);
        MainWindow? picker = null;
        try
        {
            picker = new MainWindow(new FileDialogSession(request)) { Width = 1100, Height = 760, WindowState = WindowState.Normal };
            picker.PrepareAsCloakedPicker(_ => { });
            var launchReady = ((TaskCompletionSource)typeof(MainWindow)
                .GetField("_folderLaunchReady", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)!).Task;
            var watch = Stopwatch.StartNew();
            using (ActivationGuard.GuardWindowsCreated())
            {
                picker.Show();
            }

            var ready = await Task.WhenAny(launchReady, Task.Delay(DriveGateReadyLimit)) == launchReady;
            Check($"the picker says it is ready while {held.DisplayName} is slow to answer ({watch.ElapsedMilliseconds} ms; limit {DriveGateReadyLimit} ms)", ready);
            var drawn = ready && await picker.WhenFolderDrawnAsync(TimeSpan.FromSeconds(3));
            Check("and has drawn its folder", drawn);
            Check($"its tiles are the drives that answered, in their order ({string.Join(", ", picker.PickerRootPaths)})",
                drawn && picker.ActivePane.Tree.Root.AllChildren.Select(DriveGateCell).SequenceEqual(others.Select(DriveGateCell)));

            hold.Release();
            var arrived = await LiveWait(() => picker.PickerRootPaths.Any(path => ViewAllPath.Equals(path, held.FullPath)), 25_000) >= 0;
            Check($"once it answers, the picker has it where a start that waited would have, named and described the same ({string.Join(", ", picker.PickerRootPaths)})",
                arrived && picker.ActivePane.Tree.Root.AllChildren.Select(DriveGateCell).SequenceEqual(drives.Select(DriveGateCell)));
        }
        finally
        {
            hold.Dispose();
            picker?.CloseFromCaller();
        }
    }

    // ---- helpers -----------------------------------------------------------------------

    /// <summary>
    /// What makes a drive's cell, but for its free space, which can change
    /// between one listing and the next: where it is, its name, that it is a
    /// drive - which picks its icon - its size and its file system.
    /// </summary>
    private static string DriveGateLine(ViewAllEntryDescriptor drive)
        => $"{drive.FullPath}|{drive.DisplayName}|{drive.Kind}|{drive.SizeBytes}|{DriveGateFormat(drive.SecondaryText)}";

    private static string DriveGateLine(ViewAllNodeViewModel node)
        => $"{node.FullPath}|{node.DisplayName}|{node.Kind}|{node.Entry.SizeBytes}|{DriveGateFormat(node.SecondaryText)}";

    /// <summary>The same for a cell of the nested canvas, which does not carry the size.</summary>
    private static string DriveGateCell(ViewAllEntryDescriptor drive)
        => $"{drive.FullPath}|{drive.DisplayName}|{NestedFolderKind.Drive}|{DriveGateFormat(drive.SecondaryText)}";

    private static string DriveGateCell(NestedFolder folder)
        => $"{folder.FullPath}|{folder.Name}|{folder.Kind}|{DriveGateFormat(folder.SecondaryText)}";

    private static string DriveGateFormat(string? secondary) => (secondary ?? string.Empty).Split('·')[0].Trim();

    /// <summary>
    /// Holds one drive from answering - every other one is asked as ever - and
    /// notes which of the others answered while it was held.  Both questions a
    /// window asks a drive as it starts are held: the tree's and the canvas's
    /// (<see cref="ViewAllFileSystemService.DescribeDrive"/>) and the
    /// navigation pane's (<see cref="FileSystemService.FolderExists"/>, asked
    /// of the drive's root alone).  Let go of by <see cref="Release"/>, after
    /// <see cref="DriveGateHoldLimit"/>, or when disposed, which also puts the
    /// real questions back.
    /// </summary>
    private sealed class HeldDrive : IDisposable
    {
        private readonly Func<DriveInfo, ViewAllEntryDescriptor?> _real = ViewAllFileSystemService.DescribeDrive;
        private readonly Func<string, bool> _realExists = FileSystemService.FolderExists;
        private readonly string _held;
        private readonly ManualResetEventSlim _released = new();
        private readonly ConcurrentDictionary<string, bool> _answeredWhileHeld = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _paneAnsweredWhileHeld = new(StringComparer.OrdinalIgnoreCase);

        public HeldDrive(string held)
        {
            _held = held;
            ViewAllFileSystemService.DescribeDrive = Describe;
            FileSystemService.FolderExists = Exists;
        }

        public bool AnsweredWhileHeld(string path) => _answeredWhileHeld.ContainsKey(path);

        /// <summary>Whether the navigation pane's question about this drive's root was answered while the held one was held.</summary>
        public bool PaneAnsweredWhileHeld(string path) => _paneAnsweredWhileHeld.ContainsKey(path);

        public void Release() => _released.Set();

        public void Dispose()
        {
            _released.Set();
            ViewAllFileSystemService.DescribeDrive = _real;
            FileSystemService.FolderExists = _realExists;
        }

        private bool Exists(string path)
        {
            if (ViewAllPath.Equals(ViewAllPath.Normalize(path), _held))
            {
                _released.Wait(DriveGateHoldLimit);
                return _realExists(path);
            }

            var answer = _realExists(path);
            if (!_released.IsSet)
            {
                _paneAnsweredWhileHeld[ViewAllPath.Normalize(path)] = true;
            }

            return answer;
        }

        private ViewAllEntryDescriptor? Describe(DriveInfo drive)
        {
            var path = ViewAllPath.Normalize(drive.RootDirectory.FullName);
            if (ViewAllPath.Equals(path, _held))
            {
                _released.Wait(DriveGateHoldLimit);
                return _real(drive);
            }

            var answer = _real(drive);
            if (!_released.IsSet)
            {
                _answeredWhileHeld[path] = true;
            }

            return answer;
        }
    }
}
