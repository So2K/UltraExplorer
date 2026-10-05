using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The review fixes of the folder routing (2026-10-02, cluster x-explorer-routing).
/// Every check runs in a second copy of this program, started on a desktop of
/// its own that is never switched to, with a fresh state folder: it starts
/// real windows and the folder broker of that state folder, and replaces how
/// a missing broker and Windows Explorer are started, so that no Explorer
/// window, no second UltraExplorer and no registry value is touched.
/// </summary>
internal static partial class Program
{
    private const string RoutingFixtureVariable = "ULTRAEXPLORER_ROUTING_FIXTURE";

    private static async Task ExplorerRoutingReviewChecks()
    {
        if (Environment.GetEnvironmentVariable(RoutingFixtureVariable) is { Length: > 0 } fixture)
        {
            ExplorerRoutingFixture.Run(fixture);
            return;
        }

        Section("explorer routing review fixes, in a copy of its own on an inactive desktop");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerRoutingReview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var desktopName = "UltraExplorerRoutingReview-" + Guid.NewGuid().ToString("N");
        var desktop = CreateDesktop(desktopName, 0, 0, 0, 0x01ff, 0);
        if (desktop == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var startup = new FixtureStartup { Size = Marshal.SizeOf<FixtureStartup>(), Desktop = desktopName };
        var executable = Environment.ProcessPath!;
        var arguments = NativeShellService.BuildCommandLine([executable, "--only", nameof(ExplorerRoutingReviewChecks)]);
        FixtureProcess process = default;
        try
        {
            // Inherited by the copy, which reads it before anything else.
            Environment.SetEnvironmentVariable(RoutingFixtureVariable, root);
            try
            {
                if (!CreateProcess(executable, new System.Text.StringBuilder(arguments), 0, 0, false, 0x08000000,
                    0, Path.GetDirectoryName(executable), ref startup, out process))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Environment.SetEnvironmentVariable(RoutingFixtureVariable, null); }
            using var child = Process.GetProcessById(checked((int)process.Id));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(240));
            var results = Path.Combine(root, "results.txt");
            foreach (var line in File.Exists(results) ? File.ReadAllLines(results) : [])
                Check(line.StartsWith("ok ") ? line[3..] : line.StartsWith("FAIL ") ? line[5..] : line, line.StartsWith("ok "));
            Check("the routing fixture wrote its results and exited", File.Exists(results) && child.ExitCode == 0);
        }
        finally
        {
            if (process.Handle != 0 && GetExitCodeProcess(process.Handle, out var code) && code == 259)
            { TerminateProcess(process.Handle, 1); WaitForSingleObject(process.Handle, 1000); }
            if (process.Thread != 0) CloseHandle(process.Thread);
            if (process.Handle != 0) CloseHandle(process.Handle);
            CloseDesktop(desktop);
            try { Directory.Delete(@"\\?\" + root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>The copy's side: its own WPF application and state folder.</summary>
internal static class ExplorerRoutingFixture
{
    private static string _results = string.Empty;

    internal static void Run(string root)
    {
        _results = Path.Combine(root, "results.txt");
        Environment.SetEnvironmentVariable(AppPaths.StateDirectoryVariable, Path.Combine(root, "state"));
        Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", "1");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Nodify;component/Themes/Dark.xaml", UriKind.Relative) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/UltraExplorer;component/Themes/UltraTheme.xaml", UriKind.Relative) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/UltraExplorer;component/Themes/PickerControls.xaml", UriKind.Relative) });
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var frame = new DispatcherFrame();
        _ = dispatcher.InvokeAsync(async () =>
        {
            try { await ChecksAsync(root); }
            catch (Exception ex) { Check("the routing fixture completed without an exception: " + ex, false); }
            finally
            {
                foreach (var window in app.Windows.OfType<UltraExplorer.MainWindow>().ToArray()) window.Close();
                frame.Continue = false;
            }
        });
        Dispatcher.PushFrame(frame);
    }

    private static void Check(string name, bool passed)
        => File.AppendAllText(_results, (passed ? "ok " : "FAIL ") + name + Environment.NewLine);

    /// <summary>The replacement switch, written as Settings would leave it; nothing registers anything here.</summary>
    private static void Mode(bool enabled)
    {
        Directory.CreateDirectory(AppPaths.StateDirectory);
        File.WriteAllText(DialogIntegrationStore.SettingsPath, JsonSerializer.Serialize(new DialogIntegrationSettings { Enabled = enabled }));
    }

    private static async Task ChecksAsync(string root)
    {
        var folders = Path.Combine(root, "folders");
        Directory.CreateDirectory(folders);
        // Nothing this copy does may open Windows Explorer.
        var explorer = new System.Collections.Concurrent.ConcurrentQueue<ProcessStartInfo>();
        ExplorerLaunchRouter.StartExplorer = start => { explorer.Enqueue(start); return null; };

        DriveErrorModeChecks();

        // First, while no window of this copy has started its broker.
        BrokerIdentityChecks();
        await OffModeFallbackChecksAsync(folders, explorer);
        await BrokerStartChecksAsync(folders);
        NameEndChecks(folders);

        // Then real windows, whose first one starts this copy's broker.
        Mode(false);
        var home = Path.Combine(folders, "main window folder");
        Directory.CreateDirectory(home);
        FolderCommandLine.TryOpenFolder(home, out var open, out _);
        var main = new UltraExplorer.MainWindow { ShowActivated = false };
        ExplorerLaunchRouter.Attach(main);
        main.Show();
        Check("the main window opens its folder", await main.ApplyFolderInvocationAsync(open).WaitAsync(TimeSpan.FromSeconds(60)));
        FolderCommandLine.TryOpenFolder(Path.Combine(folders, "backup."), out var dotted, out _);
        var dottedReceipt = await RouteAsync(dotted);
        Check($"a folder named with a trailing dot is opened and framed in the window ({main.CurrentFolderPath})",
            ExplorerLaunchRouter.ReceiptMatches(dottedReceipt, dotted) && main.CurrentFolderPath == Path.Combine(folders, "backup."));
        await ClosingWindowChecksAsync(main, folders);
        await FoldersTogetherChecksAsync(main, folders);
        await SlowStartChecksAsync(folders);
        await FolderWorkspaceLifetimeChecksAsync(main, folders);
        await RevealLayerChecksAsync(main, folders);
        Check("nothing here opened Windows Explorer but the off-mode fallback", explorer.Count == 1);
        await ColdStartChecksAsync(root, folders);
        await SettingsLaunchChecksAsync(root, folders);
    }

    /// <summary>
    /// I116: a folder window's own file - its view, since I009 shares the rest
    /// with every window - is read by that window only, and its destination is
    /// never given to a window again once it closes, so it goes with it.
    /// Leftovers from before, long untouched,
    /// are cleared when a broker starts; the main workspace is never touched.
    /// </summary>
    private static async Task FolderWorkspaceLifetimeChecksAsync(UltraExplorer.MainWindow main, string folders)
    {
        var target = Path.Combine(folders, "adopted and closed");
        Directory.CreateDirectory(target);
        FolderCommandLine.TryOpenFolder(target, out var invocation, out _);
        invocation = invocation with { DestinationId = Guid.NewGuid() };
        var receipt = await RouteAsync(invocation);
        var window = Windows().Single(candidate => candidate.FolderDestinationId == invocation.DestinationId);
        await ((UltraExplorer.ViewModels.MainViewModel)window.DataContext).SaveNowAsync();
        var workspace = ExplorerLaunchRouter.FolderWorkspacePath(invocation.DestinationId);
        // Since I009 a folder window shares the user's settings, pins and
        // marks, and keeps only its view - its camera - in a file of its own.
        string[] own = [workspace + ".tree.json"];
        string[] shared = [workspace, workspace + ".marks.json"];
        ((UltraExplorer.ViewModels.MainViewModel)window.DataContext).Marks.SetNote(target, "a note made in the adopted window");
        await ((UltraExplorer.ViewModels.MainViewModel)window.DataContext).SaveNowAsync();
        await ((UltraExplorer.ViewModels.MainViewModel)main.DataContext).SaveNowAsync();
        var mainFiles = Directory.GetFiles(AppPaths.StateDirectory, "*.json");
        Check($"an adopted folder window keeps its own view while open, and its settings and marks with the user's ({string.Join(", ", own.Concat(shared).Where(File.Exists).Select(Path.GetFileName))})",
            ExplorerLaunchRouter.ReceiptMatches(receipt, invocation) && own.All(File.Exists) && !shared.Any(File.Exists));
        await CloseAsync([window]);
        await Task.Delay(500);
        Check($"and they are deleted when it closes ({own.Count(File.Exists)} left)", !own.Any(File.Exists));
        Check($"the main window's own state stays ({mainFiles.Length} files)", mainFiles.Length > 0 && mainFiles.All(File.Exists));

        var shellWindows = Path.GetDirectoryName(workspace)!;
        var stale = Path.Combine(shellWindows, Guid.NewGuid().ToString("N") + ".workspace.json");
        var recent = Path.Combine(shellWindows, Guid.NewGuid().ToString("N") + ".workspace.json");
        var other = Path.Combine(shellWindows, "notes.txt");
        string[] staleFiles = [stale, stale + ".tree.json", stale + ".marks.json"];
        foreach (var path in staleFiles.Append(recent).Append(other)) File.WriteAllText(path, "{}");
        foreach (var path in staleFiles.Append(other)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        var prune = typeof(ExplorerLaunchRouter).GetMethod("PruneFolderWorkspaces", BindingFlags.Static | BindingFlags.NonPublic);
        prune?.Invoke(null, null);
        Check($"folder window files untouched for weeks are cleared ({staleFiles.Count(File.Exists)} left)", !staleFiles.Any(File.Exists));
        Check("a recent one, and anything that is not a folder window's, stays", File.Exists(recent) && File.Exists(other));
    }

    /// <summary>
    /// I148: someone who turned the Files layer off and uses a browser's "Show
    /// in folder" sees the file the reveal is for, in that pane, but the
    /// layer they chose is not switched back on, nor saved so for every pane.
    /// </summary>
    private static async Task RevealLayerChecksAsync(UltraExplorer.MainWindow main, string folders)
    {
        var shell = (UltraExplorer.ViewModels.MainViewModel)main.DataContext;
        var layers = shell.Layers;
        var folder = Path.Combine(folders, "downloads shown in folder");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "downloaded.zip");
        File.WriteAllText(file, "a download");
        FolderCommandLine.TryReveal([file], out var reveal, out _);
        try
        {
            shell.Layers = layers & ~UltraExplorer.Models.CanvasLayer.Files;
            var shown = await main.ApplyFolderInvocationAsync(reveal).WaitAsync(TimeSpan.FromSeconds(60));
            Check("a reveal with the Files layer off still shows and selects its file",
                shown && main.CurrentFolderSelection.SequenceEqual([file])
                && (main.ActivePane.Canvas.ShownLayers & UltraExplorer.Models.CanvasLayer.Files) != 0);
            Check($"and leaves the layer the user chose off ({shell.Layers})", (shell.Layers & UltraExplorer.Models.CanvasLayer.Files) == 0);
            shell.Layers = shell.Layers | UltraExplorer.Models.CanvasLayer.Details;
            shell.Layers = shell.Layers & ~UltraExplorer.Models.CanvasLayer.Details;
            Check("the next change of layers puts the pane back to them", (main.ActivePane.Canvas.ShownLayers & UltraExplorer.Models.CanvasLayer.Files) == 0);
        }
        finally { shell.Layers = layers; }
    }

    /// <summary>A real copy of UltraExplorer on this desktop, kept to a state folder of its own.</summary>
    private static Process StartCopy(string state, params string[] arguments)
    {
        var start = new ProcessStartInfo(DialogSelfProcess.ExecutablePath) { UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment[AppPaths.StateDirectoryVariable] = state;
        start.Environment["ULTRAEXPLORER_TEST_WINDOW"] = "1";
        start.Environment.Remove(RoutingFixtureVariable);
        return Process.Start(start)!;
    }

    private const string RoutingFixtureVariable = "ULTRAEXPLORER_ROUTING_FIXTURE";

    private static void EndCopies(IEnumerable<Process> copies)
    {
        foreach (var copy in copies)
        {
            try { if (!copy.HasExited) { copy.Kill(); copy.WaitForExit(5000); } }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            copy.Dispose();
        }
    }

    /// <summary>
    /// I048: the tray's "Open settings" starts UltraExplorer with --settings.
    /// With a window open, that window shows Settings and the launch ends,
    /// rather than a second whole UltraExplorer starting on the same workspace
    /// and marks, whose saves the first window's would later overwrite.
    /// </summary>
    private static async Task SettingsLaunchChecksAsync(string root, string folders)
    {
        var state = Path.Combine(root, "settings-state");
        Directory.CreateDirectory(state);
        var folder = Path.Combine(folders, "open while settings are asked for");
        Directory.CreateDirectory(folder);
        var copies = new List<Process>();
        try
        {
            var running = StartCopy(state, FolderCommandLine.OpenFolderSwitch, folder);
            copies.Add(running);
            var clock = Stopwatch.StartNew();
            while (ShownWindows(running.Id) < 1 && clock.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(100);
            await Task.Delay(1000);
            var before = ShownWindows(running.Id);
            var settings = StartCopy(state, "--settings");
            copies.Add(settings);
            clock.Restart();
            while (!settings.HasExited && clock.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(100);
            Check($"with a window open, the tray's Open settings starts no second UltraExplorer ({(settings.HasExited ? "exit " + settings.ExitCode : "still running")})",
                settings.HasExited && settings.ExitCode == 0);
            var after = 0;
            while ((after = ShownWindows(running.Id)) <= before && clock.Elapsed < TimeSpan.FromSeconds(45)) await Task.Delay(100);
            Check($"the open window shows Settings instead ({before} windows, then {after})", before >= 1 && after == before + 1);
        }
        finally { EndCopies(copies); }
    }

    /// <summary>
    /// I035, from cold: the Shell starts one UltraExplorer per folder of a
    /// selection opened with Enter, and none finds a window listening.  The
    /// first builds its window; the others wait for its broker and hand their
    /// folders to it, rather than each building a whole window process on the
    /// same state.  Real copies of UltraExplorer are started here, on this
    /// desktop, with a state folder of their own that keeps them away from
    /// sign-in entries, the registry and dialog listeners.
    /// </summary>
    private static async Task ColdStartChecksAsync(string root, string folders)
    {
        var state = Path.Combine(root, "cold-state");
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "dialog-integration.json"), JsonSerializer.Serialize(new DialogIntegrationSettings { Enabled = true }));
        var targets = Enumerable.Range(1, 3).Select(index => Path.Combine(folders, "selected and opened with Enter " + index)).ToArray();
        var copies = new List<Process>();
        try
        {
            foreach (var target in targets) Directory.CreateDirectory(target);
            foreach (var target in targets) copies.Add(StartCopy(state, FolderCommandLine.ShellRequestSwitch, FolderCommandLine.OpenFolderSwitch, target));
            var clock = Stopwatch.StartNew();
            while (copies.Count(copy => copy.HasExited) < 2 && clock.Elapsed < TimeSpan.FromSeconds(45)) await Task.Delay(100);
            var handedOver = copies.Where(copy => copy.HasExited).ToArray();
            Check($"three folders opened together from cold leave one UltraExplorer running ({copies.Count - handedOver.Length} running)",
                handedOver.Length == 2 && handedOver.All(copy => copy.ExitCode == 0));
            var running = copies.FirstOrDefault(copy => !copy.HasExited);
            var shown = 0;
            while (running is not null && clock.Elapsed < TimeSpan.FromSeconds(90) && (shown = ShownWindows(running.Id)) < 3) await Task.Delay(200);
            Check($"and it shows a window for each folder ({shown} windows)", running is not null && shown >= 3);
        }
        finally { EndCopies(copies); }
    }

    /// <summary>The visible top-level WPF windows of a process on this desktop.</summary>
    private static int ShownWindows(int process)
    {
        var count = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            var name = new System.Text.StringBuilder(256);
            if (owner == process && IsWindowVisible(window) && GetClassName(window, name, name.Capacity) > 0
                && name.ToString().StartsWith("HwndWrapper[UltraExplorer", StringComparison.Ordinal)) count++;
            return true;
        }, 0);
        return count;
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, System.Text.StringBuilder name, int capacity);

    /// <summary>I045, its folder launches: a folder named "backup." beside
    /// "backup", and "notes " alone, as WSL, git or a share leave them.  Opened
    /// or revealed from the Shell, each is itself, not its neighbour or nothing.</summary>
    private static void NameEndChecks(string folders)
    {
        const string ExtendedLength = @"\\?\";
        var plain = Path.Combine(folders, "backup");
        var dotted = plain + ".";
        var spaced = Path.Combine(folders, "notes ");
        var file = Path.Combine(dotted, "report.");
        Directory.CreateDirectory(plain);
        Directory.CreateDirectory(ExtendedLength + dotted);
        Directory.CreateDirectory(ExtendedLength + spaced);
        File.WriteAllText(ExtendedLength + file, "kept apart");
        Check("a folder named with a trailing dot opens itself, not its neighbour",
            FolderCommandLine.TryOpenFolder(dotted, out var open, out _) && open.FolderPath == dotted);
        Check("a folder named with a trailing space opens at all",
            FolderCommandLine.TryOpenFolder(spaced + @"\", out var spacedOpen, out _) && spacedOpen.FolderPath == spaced);
        Check("a file named with a trailing dot is revealed in its own folder",
            FolderCommandLine.TryReveal([file], out var reveal, out _) && reveal.FolderPath == dotted && reveal.SelectedPaths.SequenceEqual([file]));
        Check("and either request reads back the same through the broker's own parse",
            FolderCommandLine.TryParse(FolderCommandLine.BuildArguments(open with { OriginIsShell = true }), out var again, out _)
            && again.FolderPath == dotted
            && FolderCommandLine.TryParse(FolderCommandLine.BuildArguments(reveal), out var revealAgain, out _)
            && revealAgain.SelectedPaths.SequenceEqual([file]));
        Check("an ordinary folder is spelt as ever", FolderCommandLine.TryOpenFolder(plain + @"\.\", out var ordinary, out _) && ordinary.FolderPath == plain);
    }

    private static UltraExplorer.MainWindow[] Windows() => Application.Current.Windows.OfType<UltraExplorer.MainWindow>().ToArray();

    private static long Handle(Window window) => new System.Windows.Interop.WindowInteropHelper(window).Handle.ToInt64();

    private static async Task<FolderRouteReceipt> RouteAsync(FolderInvocation invocation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return await ExplorerLaunchRouter.SendAsync(new(Guid.NewGuid(), invocation, true), deadline.Token);
    }

    private static async Task CloseAsync(IEnumerable<UltraExplorer.MainWindow> windows)
    {
        foreach (var window in windows)
        {
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>I115: a folder sent while the last window is saving on its way
    /// out is not handed to that window, which would drop it; it opens in a
    /// window of its own.</summary>
    private static async Task ClosingWindowChecksAsync(UltraExplorer.MainWindow main, string folders)
    {
        var closing = typeof(UltraExplorer.MainWindow).GetField("_closeRequested", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var target = Path.Combine(folders, "opened while the last window closes");
        Directory.CreateDirectory(target);
        FolderCommandLine.TryOpenFolder(target, out var invocation, out _);
        var before = Windows();
        FolderRouteReceipt? receipt = null;
        closing.SetValue(main, true);
        try { receipt = await RouteAsync(invocation); }
        finally { closing.SetValue(main, false); }
        var opened = Windows().Except(before).ToArray();
        Check($"a folder sent while the last window is closing opens in a window of its own ({opened.Length} opened, ready: {receipt?.Ready})",
            receipt is not null && ExplorerLaunchRouter.ReceiptMatches(receipt, invocation)
            && opened.Length == 1 && Handle(opened[0]) == receipt.Window && opened[0].CurrentFolderPath == target);
        await CloseAsync(opened);
    }

    /// <summary>I035: several folders opened together from the Shell (Enter on
    /// a selection) each open, in a window of their own, rather than each one
    /// taking the window the one before it was still opening.</summary>
    private static async Task FoldersTogetherChecksAsync(UltraExplorer.MainWindow main, string folders)
    {
        Mode(true);
        var targets = Enumerable.Range(1, 3).Select(index => Path.Combine(folders, "opened together " + index)).ToArray();
        var invocations = targets.Select(target =>
        {
            Directory.CreateDirectory(target);
            FolderCommandLine.TryOpenFolder(target, out var invocation, out _);
            return invocation with { OriginIsShell = true };
        }).ToArray();
        var before = Windows();
        var sends = invocations.Select(invocation => Task.Run(() => RouteAsync(invocation))).ToArray();
        // As the Shell's launchers arrive: all three are with the broker
        // before the window thread looks for a window for the first.
        Thread.Sleep(1500);
        var receipts = await Task.WhenAll(sends);
        var opened = Windows().Except(before).ToArray();
        Check($"three folders opened together are each ready ({string.Join(", ", receipts.Select(receipt => receipt.Ready))})",
            receipts.Zip(invocations).All(pair => ExplorerLaunchRouter.ReceiptMatches(pair.First, pair.Second)));
        Check($"each in a window of its own ({receipts.Select(receipt => receipt.Window).Distinct().Count()} windows)",
            receipts.Select(receipt => receipt.Window).Distinct().Count() == 3);
        Check("and each window still shows its own folder", receipts.Zip(targets).All(pair =>
            Windows().FirstOrDefault(window => Handle(window) == pair.First.Window)?.CurrentFolderPath == pair.Second));
        Check("the window already open took one of them", receipts.Any(receipt => receipt.Window == Handle(main)));
        Mode(false);
        await CloseAsync(opened);
    }

    /// <summary>I072: a second request that gives up while the window is still
    /// starting does not take the first one's place. The folder asked for at
    /// start opens once the window is ready, and the drives off its way are
    /// read again afterwards.</summary>
    private static async Task SlowStartChecksAsync(string folders)
    {
        var first = Path.Combine(folders, "asked for at start");
        var second = Path.Combine(folders, "asked for while starting");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        FolderCommandLine.TryOpenFolder(first, out var startup, out _);
        FolderCommandLine.TryOpenFolder(second, out var later, out _);
        var window = new UltraExplorer.MainWindow(null, ExplorerLaunchRouter.FolderWorkspacePath(Guid.NewGuid())) { ShowActivated = false };
        window.PrepareFolderInvocation(startup);
        var atStart = window.ApplyFolderInvocationAsync(startup);
        using (var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            Check("a request that gives up while the window is still starting fails",
                !await window.ApplyFolderInvocationAsync(later, giveUp.Token));
        window.Show();
        var opened = await atStart.WaitAsync(TimeSpan.FromSeconds(60));
        Check($"the folder asked for at start still opens once the window is ready ({opened}, {window.CurrentFolderPath})",
            opened && window.CurrentFolderPath == first);
        Check("and the drives off its way are read afterwards", window.ActivePane.Canvas.LoadUnfocusedRoots);
        await CloseAsync([window]);
    }

    /// <summary>
    /// I092 and I034: what the Explorer observer's transfer does when no window
    /// answers.  A selection too large for one packet is no reason to start
    /// anything.  With no broker at all, one UltraExplorer is started for all
    /// transfers at once, and it is asked for the folder itself, so it opens
    /// that folder's window and not the main workspace beside it.  A broker that
    /// is only slow to answer is waited for, not joined by another copy.
    /// </summary>
    private static async Task BrokerStartChecksAsync(string folders)
    {
        Mode(true);
        var starts = new System.Collections.Concurrent.ConcurrentQueue<string[]>();
        var startSelf = ExplorerLaunchRouter.StartSelf;
        // A stand-in that never exits: the copy itself.
        ExplorerLaunchRouter.StartSelf = arguments => { starts.Enqueue(arguments); return Process.GetCurrentProcess(); };
        static void ForgetStarted() => typeof(ExplorerLaunchRouter).GetField("_started", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);
        static async Task<(FolderRouteReceipt? Receipt, bool Canceled, TimeSpan Took)> OpenAsync(FolderInvocation invocation, TimeSpan patience)
        {
            using var wait = new CancellationTokenSource(patience);
            var clock = Stopwatch.StartNew();
            try { return (await ExplorerLaunchRouter.OpenAndWaitAsync(invocation, wait.Token), false, clock.Elapsed); }
            catch (Exception) when (wait.IsCancellationRequested) { return (null, true, clock.Elapsed); }
        }
        try
        {
            var large = Path.Combine(folders, "large selection");
            Directory.CreateDirectory(large);
            var many = Enumerable.Range(0, 3000).Select(index => Path.Combine(large, $"selected item {index:D4} Ж.txt")).ToArray();
            var oversize = new FolderInvocation(FolderInvocationKind.Reveal, large, many, true, Guid.NewGuid());
            var huge = await OpenAsync(oversize, TimeSpan.FromSeconds(2));
            Check($"a selection too large for a folder packet starts no UltraExplorer and is given up at once ({starts.Count} started, {huge.Took.TotalMilliseconds:F0} ms)",
                starts.IsEmpty && huge.Receipt is null && !huge.Canceled && huge.Took < TimeSpan.FromSeconds(1));

            ForgetStarted();
            starts.Clear();
            var first = Path.Combine(folders, "first adopted folder");
            var second = Path.Combine(folders, "second adopted folder");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            var one = new FolderInvocation(FolderInvocationKind.OpenFolder, first, [], true, Guid.NewGuid());
            var two = new FolderInvocation(FolderInvocationKind.OpenFolder, second, [], true, Guid.NewGuid());
            await Task.WhenAll(OpenAsync(one, TimeSpan.FromSeconds(1.5)), OpenAsync(two, TimeSpan.FromSeconds(1.5)));
            var started = starts.ToArray();
            Check($"two transfers with no broker start one UltraExplorer between them ({started.Length} started)", started.Length == 1);
            Check($"and it is asked for a transfer's own folder and window ({string.Join(" ", started.FirstOrDefault() ?? [])})",
                started.Length >= 1 && (started[0].SequenceEqual(FolderCommandLine.BuildArguments(one))
                    || started[0].SequenceEqual(FolderCommandLine.BuildArguments(two)))
                && started[0].Contains(FolderCommandLine.DestinationSwitch));

            ForgetStarted();
            starts.Clear();
            await using (var busy = new System.IO.Pipes.NamedPipeServerStream(ExplorerLaunchRouter.PipeName, System.IO.Pipes.PipeDirection.InOut, 1,
                System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly))
            await using (var holder = new System.IO.Pipes.NamedPipeClientStream(".", ExplorerLaunchRouter.PipeName, System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly))
            {
                // Its one instance taken: a broker that cannot answer yet.
                await Task.WhenAll(busy.WaitForConnectionAsync(), holder.ConnectAsync(3000));
                await OpenAsync(one with { DestinationId = Guid.NewGuid() }, TimeSpan.FromSeconds(1.5));
            }
            Check($"a broker that is only slow to answer is waited for, not joined by another UltraExplorer ({starts.Count} started)", starts.IsEmpty);

            await LongSelectionStartChecksAsync(folders, starts);
        }
        finally
        {
            ExplorerLaunchRouter.StartSelf = startSelf;
            ForgetStarted();
            Mode(false);
        }
    }

    /// <summary>
    /// I034, a long selection with no broker: some hundreds of files make a
    /// command line Windows refuses to start a process with, while their
    /// packet is far from too large.  UltraExplorer is started on the folder
    /// alone, for the transfer's own window, and the request retried for that
    /// window then selects the files in it, instead of the start failing and
    /// the Explorer window never being adopted.
    /// </summary>
    private static async Task LongSelectionStartChecksAsync(string folders, System.Collections.Concurrent.ConcurrentQueue<string[]> starts)
    {
        static void ForgetStarted() => typeof(ExplorerLaunchRouter).GetField("_started", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);
        static async Task<FolderRouteReceipt?> OpenAsync(FolderInvocation invocation, TimeSpan patience)
        {
            using var wait = new CancellationTokenSource(patience);
            try { return await ExplorerLaunchRouter.OpenAndWaitAsync(invocation, wait.Token); }
            catch (Exception) when (wait.IsCancellationRequested) { return null; }
        }
        ForgetStarted();
        starts.Clear();
        var folder = Path.Combine(folders, "revealed from a long selection");
        Directory.CreateDirectory(folder);
        // As many files as pass the command line's limit wherever the temporary folder is.
        var count = 36000 / (folder.Length + 20) + 1;
        var photos = Enumerable.Range(0, count).Select(index => Path.Combine(folder, $"photo {index:D4}.jpg")).ToArray();
        foreach (var photo in photos) File.WriteAllText(photo, "a photo");
        var reveal = new FolderInvocation(FolderInvocationKind.Reveal, folder, photos, true, Guid.NewGuid());
        var line = FolderCommandLine.BuildCommandLine(reveal).Length;
        var packet = JsonSerializer.SerializeToUtf8Bytes(new FolderRouteRequest(Guid.NewGuid(), reveal, true)).Length;
        Check($"a selection of {count} files is too long for a command line yet fits one folder packet ({line} characters, {packet} bytes)",
            line > 32767 && packet < ExplorerLaunchRouter.MaximumPacket);

        await OpenAsync(reveal, TimeSpan.FromSeconds(1.5));
        var started = starts.ToArray();
        var arguments = started.FirstOrDefault() ?? [];
        var at = Array.IndexOf(arguments, FolderCommandLine.OpenFolderSwitch);
        Check($"with no broker, it starts one UltraExplorer on its folder alone, for its own window ({started.Length} started: {string.Join(" ", arguments.Take(6))})",
            started.Length == 1 && arguments.SequenceEqual(FolderCommandLine.BuildArguments(
                reveal with { Kind = FolderInvocationKind.OpenFolder, SelectedPaths = Array.Empty<string>() }))
            && arguments.Contains(FolderCommandLine.DestinationSwitch) && at >= 0 && arguments[at + 1] == folder
            && !arguments.Contains(FolderCommandLine.RevealSwitch));

        // Then a real copy of UltraExplorer, on this desktop and this state
        // folder, started with those arguments.  It is not given the Shell's
        // mark, so nothing it reads can make it hand the folder to Explorer.
        ForgetStarted();
        var record = ExplorerLaunchRouter.StartSelf;
        var copies = new List<Process>();
        ExplorerLaunchRouter.StartSelf = given =>
        {
            var copy = StartCopy(AppPaths.StateDirectory, given.Where(argument => argument != FolderCommandLine.ShellRequestSwitch).ToArray());
            copies.Add(copy);
            return copy;
        };
        try
        {
            var transfer = reveal with { DestinationId = Guid.NewGuid() };
            var clock = Stopwatch.StartNew();
            var receipt = await OpenAsync(transfer, TimeSpan.FromSeconds(90));
            var shown = copies.Count == 1 ? ShownWindows(copies[0].Id) : 0;
            Check($"and the request retried for that window selects all {count} files in it ({copies.Count} started, {shown} windows, ready: {receipt?.Ready}, {clock.Elapsed.TotalSeconds:F1} s)",
                copies.Count == 1 && receipt is not null && ExplorerLaunchRouter.ReceiptMatches(receipt, transfer)
                && receipt.Process == copies[0].Id && receipt.SelectedPaths.Count == count && shown == 1);
        }
        finally
        {
            ExplorerLaunchRouter.StartSelf = record;
            ForgetStarted();
            EndCopies(copies);
        }
    }

    /// <summary>I200: with the replacement off, a folder the Shell still sends
    /// here goes back to Explorer. The registrations are put right first, but
    /// when Settings is still switching the mode off and holds their lock, that
    /// is given up after its wait instead of ending the launch with no window.</summary>
    private static async Task OffModeFallbackChecksAsync(string folders, System.Collections.Concurrent.ConcurrentQueue<ProcessStartInfo> explorer)
    {
        Mode(false);
        var folder = Path.Combine(folders, "opened while settings are busy");
        Directory.CreateDirectory(folder);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var settings = new Mutex(false, @"Local\UltraExplorer.DialogSettings." + DialogIntegrationStore.InstanceKey);
            settings.WaitOne();
            held.Set();
            release.Wait();
            settings.ReleaseMutex();
        }) { IsBackground = true };
        holder.Start();
        held.Wait();
        explorer.Clear();
        var handled = false;
        Exception? failure = null;
        try { handled = await Task.Run(() => ExplorerLaunchRouter.TryHandleShellFallback([FolderCommandLine.ShellRequestSwitch, FolderCommandLine.OpenFolderSwitch, folder])); }
        catch (Exception ex) { failure = ex; }
        finally { release.Set(); holder.Join(); }
        Check($"with the mode switching off, a folder still sent here opens in Explorer ({failure?.GetType().Name ?? "no exception"})",
            failure is null && handled && explorer.Count == 1 && explorer.TryPeek(out var start)
            && start.FileName.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase) && start.Arguments.Contains(folder, StringComparison.Ordinal));
    }

    /// <summary>I089: every UltraExplorer process, the resident agents and
    /// workers started at sign-in included, answers a drive with no media in
    /// it with an error of its own instead of Windows' "There is no disk in the
    /// drive" box, which would also hold the thread that asked.</summary>
    private static void DriveErrorModeChecks()
    {
        const uint FailCriticalErrors = 0x0001, NoGpFaultErrorBox = 0x0002, NoOpenFileErrorBox = 0x8000;
        var inherited = GetErrorMode();
        try
        {
            // As a process started at sign-in begins: with Windows' boxes on.
            SetErrorMode(0);
            typeof(UltraExplorer.App).GetMethod("SuppressDriveErrorBoxes", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, null);
            var after = GetErrorMode();
            Check($"the start of every UltraExplorer process turns Windows' drive error boxes off (0x{after:X})",
                after == (FailCriticalErrors | NoOpenFileErrorBox));
            SetErrorMode(NoGpFaultErrorBox);
            typeof(UltraExplorer.App).GetMethod("SuppressDriveErrorBoxes", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, null);
            Check($"and keeps whatever else it was started with (0x{GetErrorMode():X})",
                GetErrorMode() == (FailCriticalErrors | NoGpFaultErrorBox | NoOpenFileErrorBox));
        }
        finally { SetErrorMode(inherited); }
    }

    [DllImport("kernel32.dll")] private static extern uint GetErrorMode();
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);

    /// <summary>I176: the broker that made an offer exits before the launcher
    /// checks who it was. That is a delivery that failed, which the launcher
    /// answers by opening the folder itself, not an exception that ends it.</summary>
    private static void BrokerIdentityChecks()
    {
        var validate = typeof(ExplorerLaunchRouter).GetMethod("ValidateIdentity", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var gone = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit 0")
        { UseShellExecute = false, CreateNoWindow = true })!;
        gone.WaitForExit();
        var server = checked((uint)gone.Id);
        var request = Guid.NewGuid();
        Exception? thrown = null;
        try
        {
            validate.Invoke(null, [new FolderRouteReceipt(request, true, false, server, 1, 0, Guid.Empty, "", []), request, server, false]);
        }
        catch (TargetInvocationException ex) { thrown = ex.InnerException; }
        Check($"a broker gone between its offer and the identity check is a failed delivery, not a crash ({thrown?.GetType().Name ?? "nothing thrown"})",
            thrown is IOException);
    }
}
