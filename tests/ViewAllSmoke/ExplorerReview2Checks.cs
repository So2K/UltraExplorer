using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The fixes of the 2026-10-04 review's Explorer replacement findings
/// (cluster k-explorer): the folder broker, the handoff of Explorer windows
/// and the folder launches.  The checks run in a copy of this program started
/// on a desktop of its own that is never switched to, with a state folder of
/// its own.  Its windows and its folder broker are real; how UltraExplorer and
/// Windows Explorer are started is replaced, and the Explorer frames are
/// stand-ins, so that no Explorer window, no second UltraExplorer and no
/// registry value is touched.
/// </summary>
internal static partial class Program
{
    private const string ExplorerReview2Variable = "ULTRAEXPLORER_EXPLORER_REVIEW2_RESULTS";
    private const string ExplorerReview2Finished = "  (explorer review 2: the copy's checks ran to the end)";

    private static async Task ExplorerReview2Checks()
    {
        if (Environment.GetEnvironmentVariable(ExplorerReview2Variable) is { Length: > 0 } results)
        {
            // The copy: what it reports goes to the file its starter reads.
            var console = Console.Out;
            using (var writer = new StreamWriter(results, append: true) { AutoFlush = true })
            {
                Console.SetOut(writer);
                try { RunOnSta("explorer review 2", ExplorerReview2OnStaAsync); }
                finally { Console.SetOut(console); }
            }

            return;
        }

        Section("Explorer replacement, review 2: in a copy of its own on an inactive desktop");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerExplorerReview2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var report = Path.Combine(root, "results.txt");
        var desktopName = "UltraExplorerExplorerReview2-" + Guid.NewGuid().ToString("N");
        var desktop = CreateDesktop(desktopName, 0, 0, 0, 0x01ff, 0);
        if (desktop == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var startup = new FixtureStartup { Size = Marshal.SizeOf<FixtureStartup>(), Desktop = desktopName };
        var executable = Environment.ProcessPath!;
        var arguments = NativeShellService.BuildCommandLine([executable, "--only", nameof(ExplorerReview2Checks)]);
        var state = Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable);
        FixtureProcess process = default;
        try
        {
            // Inherited by the copy, which reads them before anything else.
            Environment.SetEnvironmentVariable(ExplorerReview2Variable, report);
            Environment.SetEnvironmentVariable(AppPaths.StateDirectoryVariable, Path.Combine(root, "state"));
            try
            {
                if (!CreateProcess(executable, new System.Text.StringBuilder(arguments), 0, 0, false, 0x08000000,
                    0, Path.GetDirectoryName(executable), ref startup, out process))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                Environment.SetEnvironmentVariable(ExplorerReview2Variable, null);
                Environment.SetEnvironmentVariable(AppPaths.StateDirectoryVariable, state);
            }

            using var child = Process.GetProcessById(checked((int)process.Id));
            var ended = true;
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(6)); }
            catch (TimeoutException) { ended = false; }
            var finished = false;
            foreach (var line in File.Exists(report) ? File.ReadAllLines(report) : [])
            {
                if (line.StartsWith("  ok    ", StringComparison.Ordinal)) Check(line[8..], true);
                else if (line.StartsWith("  FAIL  ", StringComparison.Ordinal)) Check(line[8..], false);
                else if (line == ExplorerReview2Finished) finished = true;
                else if (line.Length > 0) Console.WriteLine(line);
            }

            Check("the copy ran its checks to the end and exited", ended && finished);
        }
        finally
        {
            if (process.Handle != 0 && GetExitCodeProcess(process.Handle, out var code) && code == 259)
            { TerminateProcess(process.Handle, 1); WaitForSingleObject(process.Handle, 1000); }
            if (process.Thread != 0) CloseHandle(process.Thread);
            if (process.Handle != 0) CloseHandle(process.Handle);
            CloseDesktop(desktop);
            TryDelete(@"\\?\" + root);
        }
    }

    /// <summary>The copy's side: its own WPF application, folder broker and state folder.</summary>
    private static async Task ExplorerReview2OnStaAsync()
    {
        // Application's constructor posts OnStartup to the dispatcher, even
        // without Run(). App.OnStartup would make its default window/broker
        // on the first await, invalidating the later "no broker" scenario.
        // Keep the old setup only for an explicit isolated diagnostic replay.
        if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_EXPLORER_STARTUP_DIAGNOSTIC") == "1")
            new App().InitializeComponent();
        else
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            // Exact App.xaml merged dictionaries, with relative theme URIs
            // qualified to their owning assembly rather than this harness.
            foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        }
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
        {
            Console.WriteLine($"  note: the interface thread threw, outside these checks: {e.Exception}");
            e.Handled = true;
        };

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerExplorerReview2Folders", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var startSelf = ExplorerLaunchRouter.StartSelf;
        var startExplorer = ExplorerLaunchRouter.StartExplorer;
        var explorer = new ConcurrentQueue<ProcessStartInfo>();
        // Nothing this copy does may open Windows Explorer.
        ExplorerLaunchRouter.StartExplorer = start => { explorer.Enqueue(start); return null; };
        try
        {
            using (ActivationGuard.GuardWindowsCreated())
            {
                if (Environment.GetEnvironmentVariable(ExplorerFollowupOnlyVariable) == "1")
                {
                    await ExplorerIntegrationFollowupOnStaAsync(root);
                    Check($"nothing here opened Windows Explorer ({explorer.Count})", explorer.IsEmpty);
                    return;
                }
                ExplorerReview2TrailingNameChecks(root);

                // First, while no window of this copy has started its broker.
                await ExplorerReview2NoBrokerChecksAsync(root);
                await ExplorerReview2EndedCopyChecksAsync(root, explorer);
                await ExplorerReview2RetryChecksAsync(root);

                // Then real windows, whose first one starts this copy's broker.
                ExplorerReview2Mode(false);
                var home = Path.Combine(root, "home");
                Directory.CreateDirectory(home);
                FolderCommandLine.TryOpenFolder(home, out var open, out _);
                var main = new MainWindow { ShowActivated = false };
                ExplorerLaunchRouter.Attach(main);
                main.Show();
                Check("the main window opens its folder", await main.ApplyFolderInvocationAsync(open).WaitAsync(TimeSpan.FromSeconds(60)));
                await ExplorerReview2LateStateChecksAsync(root);
                await ExplorerReview2UsedWindowChecksAsync(root);
                await ExplorerReview2MinimizedChecksAsync(main, root);
                await ExplorerReview2RevealReadChecksAsync(main, root);
                Check($"nothing here opened Windows Explorer ({explorer.Count})", explorer.IsEmpty);
            }
        }
        finally
        {
            ExplorerLaunchRouter.StartSelf = startSelf;
            ExplorerLaunchRouter.StartExplorer = startExplorer;
            ExplorerReview2ForgetStarted();
            foreach (var window in Application.Current.Windows.OfType<MainWindow>().ToArray()) window.Close();
            await MainWindow.WhenClosingWindowsClosedAsync(Application.Current, TimeSpan.FromSeconds(10));
            TryDelete(@"\\?\" + root);
            Console.WriteLine(ExplorerReview2Finished);
        }
    }

    /// <summary>The replacement switch, written as Settings would leave it; nothing registers anything here.</summary>
    private static void ExplorerReview2Mode(bool enabled)
    {
        Directory.CreateDirectory(AppPaths.StateDirectory);
        File.WriteAllText(DialogIntegrationStore.SettingsPath, JsonSerializer.Serialize(new DialogIntegrationSettings { Enabled = enabled }));
    }

    /// <summary>The UltraExplorer the router started for an earlier check is not this check's.</summary>
    private static void ExplorerReview2ForgetStarted() =>
        typeof(ExplorerLaunchRouter).GetField("_started", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);

    private static bool ExplorerReview2Shown(MainWindow window) =>
        Application.Current.Windows.OfType<MainWindow>().Contains(window) && !window.IsFolderWindowClosing;

    // ---- J150: the Shell verb's "%1\." -----------------------------------------------------

    /// <summary>
    /// The folder verb UltraExplorer registers passes <c>"%1\."</c>, so that a
    /// drive's <c>C:\</c> cannot end its quoted argument.  Normalising that cut
    /// the dot or the space off the end of the folder's own name before the
    /// I045 follow-up could keep it: <c>backup.</c> opened its neighbour
    /// <c>backup</c>, and <c>notes </c> nothing.  The Shell's own parse makes
    /// <c>backup</c> of <c>backup.</c> as well, so a Windows Explorer window
    /// kept with Windows is not navigated to it.
    /// </summary>
    private static void ExplorerReview2TrailingNameChecks(string root)
    {
        Section("folder launches: the Shell verb's \"%1\\.\" keeps the dot or space ending a folder's name (J150)");
        var names = Path.Combine(root, "names");
        var dotted = Path.Combine(names, "backup.");
        var spaced = Path.Combine(names, "notes ");
        Directory.CreateDirectory(Path.Combine(names, "backup"));
        Directory.CreateDirectory(@"\\?\" + dotted);
        Directory.CreateDirectory(@"\\?\" + spaced);
        static string[] Verb(string folder) => [FolderCommandLine.ShellRequestSwitch, FolderCommandLine.OpenFolderSwitch, folder + @"\."];
        static FolderInvocation? Parse(string folder) => FolderCommandLine.TryParse(Verb(folder), out var invocation, out _) ? invocation : null;

        var backup = Parse(dotted);
        Check($"the verb's \"backup.\\.\" opens \"backup.\" itself, not its neighbour \"backup\" ({backup?.FolderPath ?? "refused"})",
            backup?.FolderPath == dotted);
        var notes = Parse(spaced);
        Check($"and \"notes \\.\" opens \"notes \" ({notes?.FolderPath ?? "refused"})", notes?.FolderPath == spaced);
        var drive = Path.GetPathRoot(root)!;
        Check($"an ordinary folder and a drive open through the verb as ever ({Parse(names)?.FolderPath}, {Parse(drive)?.FolderPath})",
            Parse(names)?.FolderPath == names && Parse(drive)?.FolderPath == drive);
        Check("a Windows Explorer window kept with Windows is not navigated by the Shell's parse of \"backup.\", which is \"backup\"",
            backup is not null && !NativeExplorerNavigation.IsEligible(backup, Verb(dotted))
            && Parse(names) is { } plain && NativeExplorerNavigation.IsEligible(plain, Verb(names)));
    }

    // ---- J018: no broker, no 250 ms connecting to it --------------------------------------

    /// <summary>
    /// With no UltraExplorer window open there is no broker pipe at all.  A
    /// cold folder launch, the tray's Open settings and an Explorer handoff
    /// each spent the 250 ms of a connection attempt finding that out before
    /// going on - a handoff twice, in the transfer and in the UltraExplorer it
    /// then started.
    /// </summary>
    private static async Task ExplorerReview2NoBrokerChecksAsync(string root)
    {
        Section("with no window open, nothing waits 250 ms for a broker that is not there (J018)");
        ExplorerReview2Mode(false);
        var folder = Path.Combine(root, "launched cold");
        Directory.CreateDirectory(folder);
        FolderCommandLine.TryOpenFolder(folder, out var open, out _);

        // Each is asked twice: the first time pays for loading its code.
        ExplorerLaunchRouter.TryForward(open);
        var clock = Stopwatch.StartNew();
        var forwarded = ExplorerLaunchRouter.TryForward(open);
        var forward = clock.ElapsedMilliseconds;
        Check($"a folder launch learns at once that no window takes its folder ({forward} ms)", !forwarded && forward < 100);
        ExplorerLaunchRouter.TryForwardSettings();
        clock.Restart();
        var settings = ExplorerLaunchRouter.TryForwardSettings();
        var settingsTook = clock.ElapsedMilliseconds;
        Check($"and so does the tray's Open settings ({settingsTook} ms)", !settings && settingsTook < 100);

        var handoff = open with { DestinationId = Guid.NewGuid() };
        _ = JsonSerializer.SerializeToUtf8Bytes(new FolderRouteRequest(Guid.NewGuid(), handoff, true));
        var starts = new ConcurrentQueue<long>();
        ExplorerReview2ForgetStarted();
        // A stand-in that never exits: this copy itself.
        ExplorerLaunchRouter.StartSelf = _ => { starts.Enqueue(clock.ElapsedMilliseconds); return Process.GetCurrentProcess(); };
        try
        {
            using var patience = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            clock.Restart();
            try { await ExplorerLaunchRouter.OpenAndWaitAsync(handoff, patience.Token); }
            catch (Exception) when (patience.IsCancellationRequested) { }
            var started = starts.TryPeek(out var at) ? at : -1;
            Check($"an Explorer handoff with no window open starts UltraExplorer at once ({(started < 0 ? "never" : started + " ms")})",
                starts.Count == 1 && started is >= 0 and < 100);
        }
        finally
        {
            ExplorerReview2ForgetStarted();
        }
    }

    // ---- J149: a started copy that ended ----------------------------------------------------

    /// <summary>
    /// The UltraExplorer a handoff started ended with exit code 0 and left no
    /// broker - its only window was closed or discarded - and the transfer
    /// waited for it for 18 s, taking it for one that had handed its folder
    /// on.  A copy started for a handoff that finds the mode off on its way
    /// opened the folder in Windows Explorer, a second window beside the
    /// Explorer window it came from.
    /// </summary>
    private static async Task ExplorerReview2EndedCopyChecksAsync(string root, ConcurrentQueue<ProcessStartInfo> explorer)
    {
        Section("an Explorer handoff whose UltraExplorer has ended (J149)");
        ExplorerReview2Mode(false);
        var folder = Path.Combine(root, "handed to a copy that ended");
        Directory.CreateDirectory(folder);
        FolderCommandLine.TryOpenFolder(folder, out var open, out _);
        ExplorerReview2ForgetStarted();
        var hasBroker = typeof(ExplorerLaunchRouter).GetMethod("BrokerPipeExists", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, null);
        Console.WriteLine($"  note: zero-exit precondition: application={Application.Current.GetType().Name}, "
            + $"PID={Environment.ProcessId}, owned Windows={Application.Current.Windows.Count}, "
            + $"broker={hasBroker}");
        // Ends at once with exit code 0 and leaves no broker, as a copy does whose only window was closed.
        ExplorerLaunchRouter.StartSelf = _ => Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        try
        {
            using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var clock = Stopwatch.StartNew();
            FolderRouteReceipt? receipt = null;
            var canceled = false;
            try { receipt = await ExplorerLaunchRouter.OpenAndWaitAsync(open with { DestinationId = Guid.NewGuid() }, patience.Token); }
            catch (Exception) when (patience.IsCancellationRequested) { canceled = true; }
            Check($"the handoff is given up once that copy has ended, not after 18 s ({clock.ElapsedMilliseconds} ms; receipt: {receipt?.Accepted}/{receipt?.Ready}; "
                + $"server PID/HWND: {receipt?.Process}/{receipt?.Window}; caller: {Environment.ProcessPath})",
                receipt is null && !canceled && clock.ElapsedMilliseconds < 3000);
        }
        finally
        {
            ExplorerReview2ForgetStarted();
        }

        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            // Held as Settings holds it while it switches the mode off: the
            // copy's attempt to put the registrations right gives up after its wait.
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
        try
        {
            handled = await Task.Run(() => ExplorerLaunchRouter.TryHandleShellFallback([FolderCommandLine.DestinationSwitch, Guid.NewGuid().ToString("D"),
                FolderCommandLine.ShellRequestSwitch, FolderCommandLine.OpenFolderSwitch, folder]));
        }
        finally
        {
            release.Set();
            holder.Join();
        }

        Check($"a handoff's UltraExplorer that finds the mode off opens no second Explorer window for its folder ({explorer.Count} opened)",
            handled && explorer.IsEmpty);
        explorer.Clear();
    }

    // ---- J070: a retried state that gives up before opening --------------------------------

    /// <summary>
    /// A newer state of the Explorer window arrived during its handoff, so the
    /// first handoff left its window for the newer one to use.  That one gave
    /// up before asking for it - the user typed in the address bar, or closed
    /// the frame - and nothing closed the window the first one had opened.
    /// Window handles here are not windows: nothing can reach a real Explorer frame.
    /// </summary>
    private static async Task ExplorerReview2RetryChecksAsync(string root)
    {
        Section("Explorer handoff: a newer state that gives up before opening closes the window the first one opened (J070)");
        var folder = Path.Combine(root, "handed over twice");
        var file = Path.Combine(folder, "picked.txt");
        var first = new ShellFolderSnapshot(0x7FF0_0071, 0x7FF0_0073, 0x7FF0_0075, 4, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 6,
            folder, new byte[] { 2, 0, 0, 0 }, Array.AsReadOnly(Array.Empty<string>()), null, 1);
        var second = first with { SelectedPaths = Array.AsReadOnly(new[] { file }), FocusedPath = file, Generation = 2 };
        var firstChecks = 0;
        var closed = 0;
        var opened = new ConcurrentQueue<FolderInvocation>();
        var discarded = new ConcurrentQueue<FolderInvocation>();
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = new ExplorerTransferActions(
            () => true,
            // The first state passes its first check and fails its last, as
            // the newer one arrived; the newer one fails its first.
            (snapshot, _) => Task.FromResult(snapshot.Generation == 1 && Interlocked.Increment(ref firstChecks) == 1),
            async (invocation, _) =>
            {
                opened.Enqueue(invocation);
                opening.TrySetResult();
                await release.Task;
                return new FolderRouteReceipt(Guid.NewGuid(), true, true, 1, 1, 1, invocation.DestinationId, invocation.FolderPath, Array.Empty<string>());
            },
            (_, _, _) => Task.FromResult(true),
            _ => true,
            _ => { Interlocked.Increment(ref closed); return true; },
            Timeout: TimeSpan.FromSeconds(5),
            DiscardDestination: invocation => { discarded.Enqueue(invocation); return Task.CompletedTask; });
        using var coordinator = new ExplorerReplacementCoordinator(actions);
        var handoff = coordinator.TransferForChecksAsync(first);
        await opening.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.TransferForChecksAsync(second);
        release.TrySetResult();
        await handoff.WaitAsync(TimeSpan.FromSeconds(5));
        await LiveWait(() => !discarded.IsEmpty, 3000);
        await Task.Delay(100);
        Check($"the first state's window is closed once the newer one gives up ({discarded.Count} discards, {opened.Count} opened)",
            opened.Count == 1 && closed == 0 && discarded.Count == 1 && discarded.TryPeek(out var discard)
            && opened.TryPeek(out var asked) && discard.DestinationId == asked.DestinationId);
    }

    // ---- J069: a state arriving just after a handoff was given up ---------------------------

    /// <summary>
    /// A browser's "Show in folder": Explorer shows the folder, then selects
    /// the file.  The handoff of the folder alone fails its last check against
    /// the selection, while the observer has not yet handed the selection over
    /// (it waits for a state to hold still for 250 ms).  The window opened for
    /// the frame was closed at once, and the selection's handoff then landed in
    /// that closing window and failed: the window flashed shut and Explorer
    /// stayed.  The frame here is a window of this thread that is never shown;
    /// only its identity is read, and closing it is the check's own record.
    /// </summary>
    private static async Task ExplorerReview2LateStateChecksAsync(string root)
    {
        Section("Explorer handoff: a state arriving just after a handoff was given up goes to the window it opened (J069)");
        ExplorerReview2Mode(true);
        var folder = Path.Combine(root, "shown in folder");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "download.zip");
        File.WriteAllText(file, "a download");
        using var frame = new HwndSource(new HwndSourceParameters("UltraExplorer review 2 frame stand-in")
        {
            WindowStyle = unchecked((int)0x80000000),
            Width = 1,
            Height = 1
        });
        var thread = ExplorerWindowInterop.GetWindowThreadProcessId(frame.Handle, out var processId);
        using var self = Process.GetCurrentProcess();
        var shown = new ShellFolderSnapshot(frame.Handle, frame.Handle, frame.Handle, processId, self.StartTime.ToUniversalTime(), thread,
            folder, new byte[] { 2, 0, 0, 0 }, Array.AsReadOnly(Array.Empty<string>()), null, 1);
        var selected = shown with { SelectedPaths = Array.AsReadOnly(new[] { file }), FocusedPath = file, Generation = 2 };
        var current = shown;
        var opened = new ConcurrentQueue<FolderInvocation>();
        var closed = new ConcurrentQueue<ShellFolderSnapshot>();
        var discarded = new ConcurrentQueue<FolderInvocation>();
        var actions = new ExplorerTransferActions(
            () => DialogIntegrationStore.Read().Enabled,
            (snapshot, _) => Task.FromResult(snapshot.Generation == Volatile.Read(ref current).Generation),
            async (invocation, cancellation) =>
            {
                opened.Enqueue(invocation);
                var receipt = await ExplorerLaunchRouter.OpenAndWaitAsync(invocation, cancellation);
                // Explorer selects the file while its window is being made.
                Volatile.Write(ref current, selected);
                return receipt;
            },
            ExplorerLaunchRouter.ValidateReadyAsync,
            _ => true,
            snapshot => { closed.Enqueue(snapshot); return true; },
            Timeout: TimeSpan.FromSeconds(60),
            NativeSession: _ => false,
            DiscardDestination: invocation => { discarded.Enqueue(invocation); return ExplorerLaunchRouter.DiscardIfUntouchedAsync(invocation); });
        MainWindow? window = null;
        try
        {
            using var coordinator = new ExplorerReplacementCoordinator(actions);
            await coordinator.TransferForChecksAsync(shown).WaitAsync(TimeSpan.FromSeconds(90));
            var destination = opened.TryPeek(out var asked) ? asked.DestinationId : Guid.Empty;
            window = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(candidate => candidate.FolderDestinationId == destination);
            var gaveUp = closed.IsEmpty && window is not null;
            // The observer hands the next state over once it has held still for 250 ms.
            await Task.Delay(300);
            await coordinator.TransferForChecksAsync(selected).WaitAsync(TimeSpan.FromSeconds(90));
            await LiveWait(() => !closed.IsEmpty, 3000);
            var windows = Application.Current.Windows.OfType<MainWindow>().Where(candidate => candidate.FolderDestinationId == destination).ToArray();
            Check($"the selection is handed over in the window the folder's handoff opened ({(gaveUp ? "given up first" : "not given up")}, "
                + $"{closed.Count} handed over, {discarded.Count} discards, {windows.Length} windows for it, "
                + $"{(window is null ? "none" : ExplorerReview2Shown(window) ? "still open" : "closed")})",
                gaveUp && window is not null && closed.TryPeek(out var handed) && handed.Generation == 2
                && windows.Length == 1 && ReferenceEquals(windows[0], window) && ExplorerReview2Shown(window)
                && window.CurrentFolderSelection.SequenceEqual([file], StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (var made in Application.Current.Windows.OfType<MainWindow>()
                .Where(candidate => opened.Any(invocation => invocation.DestinationId == candidate.FolderDestinationId)).ToArray())
                made.Close();
            ExplorerReview2Mode(false);
        }
    }

    // ---- J147: a window the broker made, used before its folder failed ----------------------

    /// <summary>
    /// The broker closes a window it has just made when that window cannot
    /// open the folder it was made for.  It did so even after the user had
    /// started using it.  Here the folder goes while the window starts; one
    /// window is clicked in meanwhile, the other is not.
    /// </summary>
    private static async Task ExplorerReview2UsedWindowChecksAsync(string root)
    {
        Section("folder broker: a window it made stays once the user has used it, though its folder could not be opened (J147)");
        ExplorerReview2Mode(false);
        async Task<(MainWindow? Window, bool Early, FolderRouteReceipt? Receipt)> FailAsync(string name, bool click)
        {
            var folder = Path.Combine(root, name);
            Directory.CreateDirectory(folder);
            FolderCommandLine.TryOpenFolder(folder, out var open, out _);
            var invocation = open with { DestinationId = Guid.NewGuid() };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var routed = ExplorerLaunchRouter.SendAsync(new FolderRouteRequest(Guid.NewGuid(), invocation, true), deadline.Token);
            MainWindow? made = null;
            await LiveWait(() => (made = Application.Current.Windows.OfType<MainWindow>()
                .FirstOrDefault(candidate => candidate.FolderDestinationId == invocation.DestinationId)) is not null, 30_000);
            var early = made is { IsFolderWindowReady: false };
            if (click)
            {
                made?.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseDownEvent
                });
            }

            // The folder goes before the window has opened it.
            Directory.Delete(folder);
            FolderRouteReceipt? receipt = null;
            try { receipt = await routed; }
            catch (IOException) { }
            await Task.Delay(500);
            return (made, early, receipt);
        }

        var used = await FailAsync("removed while a used window opens it", click: true);
        Check($"a window clicked in while it opened stays when its folder cannot be opened (made before ready: {used.Early}, "
            + $"ready: {used.Receipt?.Ready}, {(used.Window is null ? "none" : ExplorerReview2Shown(used.Window) ? "open" : "closed")})",
            used is { Early: true, Receipt.Ready: false, Window: { } kept } && ExplorerReview2Shown(kept));
        var unused = await FailAsync("removed while an unused window opens it", click: false);
        Check($"one nobody used still closes, as before (made before ready: {unused.Early}, "
            + $"{(unused.Window is null ? "none" : ExplorerReview2Shown(unused.Window) ? "open" : "closed")})",
            unused is { Early: true, Window: { } gone } && !ExplorerReview2Shown(gone));
        foreach (var window in new[] { used.Window, unused.Window }.OfType<MainWindow>().Where(ExplorerReview2Shown)) window.Close();
    }

    // ---- J097: a minimized window shown before its folder has loaded ------------------------

    /// <summary>
    /// A folder opened from the desktop or by Win+E while the window is
    /// minimized: the window was restored, and brought to the front, only
    /// once the folder had loaded, by when its launcher's right to bring it
    /// to the front had lapsed.  A test window is never brought to the
    /// front; its restoring is what is watched here.
    /// </summary>
    private static async Task ExplorerReview2MinimizedChecksAsync(MainWindow main, string root)
    {
        Section("folder launches: a minimized window is shown as its folder is asked for, not once it has loaded (J097)");
        var folder = Path.Combine(root, "opened while minimized");
        Directory.CreateDirectory(folder);
        for (var index = 0; index < 50; index++) File.WriteAllText(Path.Combine(folder, $"note {index:D2}.txt"), "a note");
        FolderCommandLine.TryOpenFolder(folder, out var open, out _);
        main.WindowState = WindowState.Minimized;
        await LiveWait(() => main.WindowState == WindowState.Minimized, 2000);
        var clock = Stopwatch.StartNew();
        var applying = main.ApplyFolderInvocationAsync(open);
        long restoredAt = -1;
        var loadedWhenShown = false;
        await LiveWait(() =>
        {
            if (main.WindowState == WindowState.Minimized) return applying.IsCompleted;
            restoredAt = clock.ElapsedMilliseconds;
            loadedWhenShown = applying.IsCompleted;
            return true;
        }, 30_000);
        var opened = await applying.WaitAsync(TimeSpan.FromSeconds(60));
        Check($"the window is restored while its folder is still loading (restored at {restoredAt} ms, loaded by then: {loadedWhenShown}, "
            + $"loaded at {clock.ElapsedMilliseconds} ms)", opened && restoredAt >= 0 && !loadedWhenShown);
    }

    // ---- J152: a large reveal reading the settings once per file ----------------------------

    /// <summary>
    /// A reveal the Shell asked for checks, before each of its files, that
    /// the replacement is still on - and read the integration settings file
    /// on the interface thread each time: 2,000 reads for 2,000 files.  The
    /// process's own count of read operations shows it, against the same
    /// reveal asked for by UltraExplorer itself, which never reads them.
    /// </summary>
    private static async Task ExplorerReview2RevealReadChecksAsync(MainWindow main, string root)
    {
        Section("folder launches: a reveal the Shell asks for reads the integration settings about once, not once per file (J152)");
        ExplorerReview2Mode(true);
        try
        {
            var folder = Path.Combine(root, "two thousand files");
            Directory.CreateDirectory(folder);
            var files = Enumerable.Range(0, 2000).Select(index => Path.Combine(folder, $"file {index:D4}.txt")).ToArray();
            foreach (var file in files) File.WriteAllText(file, "x");
            FolderCommandLine.TryReveal(files, out var reveal, out _);
            async Task<(bool Opened, long Reads, long Took)> RevealAsync(FolderInvocation invocation)
            {
                var before = ExplorerReview2ReadOperations();
                var clock = Stopwatch.StartNew();
                var opened = await main.ApplyFolderInvocationAsync(invocation).WaitAsync(TimeSpan.FromSeconds(120));
                return (opened, ExplorerReview2ReadOperations() - before, clock.ElapsedMilliseconds);
            }

            // First, the folder is read and laid out.
            await RevealAsync(reveal);
            var plain = await RevealAsync(reveal);
            var shell = await RevealAsync(reveal with { OriginIsShell = true });
            var plainAgain = await RevealAsync(reveal);
            var shellAgain = await RevealAsync(reveal with { OriginIsShell = true });
            var extra = Math.Min(shell.Reads - plain.Reads, shellAgain.Reads - plainAgain.Reads);
            Check($"the Shell's reveal of 2,000 files reads hardly more than UltraExplorer's own ({extra} more read operations; "
                + $"{shell.Took}/{shellAgain.Took} ms against {plain.Took}/{plainAgain.Took} ms)",
                plain.Opened && shell.Opened && plainAgain.Opened && shellAgain.Opened && extra < 400);
        }
        finally
        {
            ExplorerReview2Mode(false);
        }
    }

    private static long ExplorerReview2ReadOperations() =>
        ExplorerReview2IoCounters(ExplorerReview2CurrentProcess(), out var counters) ? (long)counters.ReadOperationCount : 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct ExplorerReview2Io
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetProcessIoCounters")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExplorerReview2IoCounters(nint process, out ExplorerReview2Io counters);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    private static extern nint ExplorerReview2CurrentProcess();
}
