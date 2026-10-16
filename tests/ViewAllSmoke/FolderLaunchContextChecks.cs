using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private const string LaunchContextFixtureVariable = "ULTRAEXPLORER_LAUNCH_CONTEXT_FIXTURE";

    private static async Task FolderLaunchContextChecks()
    {
        if (Environment.GetEnvironmentVariable(LaunchContextFixtureVariable) is { Length: > 0 } fixture)
        {
            FolderLaunchContextFixture.Run(fixture);
            return;
        }

        Section("fresh folder windows and independent tab routing on an inactive desktop");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerLaunchContexts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var desktopName = "UltraExplorerLaunchContexts-" + Guid.NewGuid().ToString("N");
        var desktop = CreateDesktop(desktopName, 0, 0, 0, 0x01ff, 0);
        if (desktop == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var startup = new FixtureStartup { Size = Marshal.SizeOf<FixtureStartup>(), Desktop = desktopName };
        var executable = Environment.ProcessPath!;
        var arguments = NativeShellService.BuildCommandLine([executable, "--only", nameof(FolderLaunchContextChecks)]);
        FixtureProcess process = default;
        try
        {
            Environment.SetEnvironmentVariable(LaunchContextFixtureVariable, root);
            try
            {
                if (!CreateProcess(executable, new System.Text.StringBuilder(arguments), 0, 0, false, 0x08000000,
                    0, Path.GetDirectoryName(executable), ref startup, out process))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Environment.SetEnvironmentVariable(LaunchContextFixtureVariable, null); }
            using var child = Process.GetProcessById(checked((int)process.Id));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(240));
            var results = Path.Combine(root, "results.txt");
            foreach (var line in File.Exists(results) ? File.ReadAllLines(results) : [])
                Check(line.StartsWith("ok ") ? line[3..] : line.StartsWith("FAIL ") ? line[5..] : line, line.StartsWith("ok "));
            Check("the isolated context fixture wrote results and exited", File.Exists(results) && child.ExitCode == 0);
        }
        finally
        {
            if (process.Handle != 0 && GetExitCodeProcess(process.Handle, out var code) && code == 259)
            { TerminateProcess(process.Handle, 1); WaitForSingleObject(process.Handle, 1000); }
            if (process.Thread != 0) CloseHandle(process.Thread);
            if (process.Handle != 0) CloseHandle(process.Handle);
            CloseDesktop(desktop);
            TryDelete(root);
        }
    }
}

internal static class FolderLaunchContextFixture
{
    private static string _results = string.Empty;

    internal static void Run(string root)
    {
        _results = Path.Combine(root, "results.txt");
        Environment.SetEnvironmentVariable(AppPaths.StateDirectoryVariable, Path.Combine(root, "state"));
        Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", "1");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var frame = new DispatcherFrame();
        _ = dispatcher.InvokeAsync(async () =>
        {
            try { await ChecksAsync(root); }
            catch (Exception ex) { Check("context fixture completed without an exception: " + ex, false); }
            finally
            {
                foreach (var window in Hosts()) window.Close();
                await MainWindow.WhenClosingWindowsClosedAsync(app, TimeSpan.FromSeconds(10));
                frame.Continue = false;
            }
        });
        Dispatcher.PushFrame(frame);
    }

    private static void Check(string name, bool passed)
        => File.AppendAllText(_results, (passed ? "ok " : "FAIL ") + name + Environment.NewLine);

    private static void Mode(FolderOpenMode mode)
    {
        Directory.CreateDirectory(AppPaths.StateDirectory);
        // Only this fixture's JSON: no controller or association registration.
        File.WriteAllText(DialogIntegrationStore.SettingsPath, JsonSerializer.Serialize(new DialogIntegrationSettings
        { Enabled = true, FolderOpenMode = mode.ToString() }));
    }

    private static MainWindow[] Hosts() => Application.Current.Windows.OfType<MainWindow>()
        .Where(window => ReferenceEquals(window, window.ShellWindow) && !window.IsFolderWindowClosing).ToArray();
    private static long Handle(MainWindow window) => new WindowInteropHelper(window.ShellWindow).Handle.ToInt64();
    private static FolderInvocation Open(string root, string name)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        FolderCommandLine.TryOpenFolder(folder, out var invocation, out _);
        return invocation;
    }
    private static FolderInvocation Reveal(string root, string name)
    {
        var open = Open(root, name);
        var first = Path.Combine(open.FolderPath, "selected one Ж.txt");
        var second = Path.Combine(open.FolderPath, "selected two.txt");
        File.WriteAllText(first, "one"); File.WriteAllText(second, "two");
        FolderCommandLine.TryReveal([first, second], out var invocation, out _);
        return invocation;
    }
    private static async Task<FolderRouteReceipt> Route(FolderInvocation invocation, Guid? request = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return await ExplorerLaunchRouter.SendAsync(new(request ?? Guid.NewGuid(), invocation, true), deadline.Token);
    }
    private static async Task Close(MainWindow window)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task ChecksAsync(string root)
    {
        Mode(FolderOpenMode.SeparateWindow);
        var initial = Reveal(root, "initial selected folder") with { DestinationId = Guid.NewGuid() };
        var host = new MainWindow(null, ExplorerLaunchRouter.FolderWorkspacePath(initial.DestinationId)) { ShowActivated = false };
        var initialContext = host.EnableFolderTabs(initial);
        ExplorerLaunchRouter.Attach(host); host.Show();
        Check("initial embedded context becomes exactly ready", await initialContext.ApplyFolderInvocationAsync(initial).WaitAsync(TimeSpan.FromSeconds(60)));
        var initialCamera = initialContext.ActivePane.Canvas.CaptureCamera();

        var first = Open(root, "fresh one");
        var second = Reveal(root, "fresh two");
        var firstReceipt = await Route(first);
        var secondReceipt = await Route(second);
        Check("consecutive ordinary launches each create a real distinct window",
            ExplorerLaunchRouter.ReceiptMatches(firstReceipt, first) && ExplorerLaunchRouter.ReceiptMatches(secondReceipt, second)
            && firstReceipt.Window != secondReceipt.Window && firstReceipt.Window != Handle(host) && secondReceipt.Window != Handle(host));
        Check("each new window has its own stable context identity", firstReceipt.DestinationId != Guid.Empty
            && secondReceipt.DestinationId != Guid.Empty && firstReceipt.DestinationId != secondReceipt.DestinationId);
        Check("the previous window keeps its folder, selection and camera", initialContext.MatchesFolderInvocation(initial)
            && initialContext.ActivePane.Canvas.CaptureCamera() == initialCamera);
        Check("the older new window can be verified after a later launch", await ExplorerLaunchRouter.ValidateReadyAsync(firstReceipt, first, CancellationToken.None));
        foreach (var other in Hosts().Where(window => !ReferenceEquals(window, host))) await Close(other);

        Mode(FolderOpenMode.NewTab);
        var tabOne = Reveal(root, "first independent tab") with { OriginIsShell = true, DestinationId = Guid.NewGuid() };
        var request = Guid.NewGuid();
        var tabOneReceipt = await Route(tabOne, request);
        var one = host.FindFolderContext(tabOne.DestinationId);
        var tabTwo = Reveal(root, "second independent tab") with { OriginIsShell = true, DestinationId = Guid.NewGuid() };
        var tabTwoReceipt = await Route(tabTwo);
        var two = host.FindFolderContext(tabTwo.DestinationId);
        Check("tab mode changes launches immediately without a restart", Hosts().Length == 1
            && tabOneReceipt.Window == Handle(host) && tabTwoReceipt.Window == Handle(host)
            && ExplorerLaunchRouter.ReceiptMatches(tabOneReceipt, tabOne) && ExplorerLaunchRouter.ReceiptMatches(tabTwoReceipt, tabTwo));
        Check("tabs retain independent folder views and complete selections", one is not null && two is not null
            && !ReferenceEquals(one, two) && one.MatchesFolderInvocation(tabOne) && two.MatchesFolderInvocation(tabTwo)
            && initialContext.MatchesFolderInvocation(initial));
        Check("an inactive tab's readiness is verified by its context identity", one is not null && !ReferenceEquals(host.ActiveTabContext, one)
            && await ExplorerLaunchRouter.ValidateReadyAsync(tabOneReceipt, tabOne, CancellationToken.None));
        var replayed = await Route(tabOne, request);
        Check("replaying the same committed request keeps the same tab and receipt", one is not null
            && ReferenceEquals(one, host.FindFolderContext(tabOne.DestinationId)) && replayed.DestinationId == tabOneReceipt.DestinationId
            && replayed.Window == tabOneReceipt.Window && ExplorerLaunchRouter.ReceiptMatches(replayed, tabOne));

        var forced = Open(root, "explicit separate window");
        Check("new-window parsing survives rebuilding with its fixed destination", FolderCommandLine.TryParse(
            [FolderCommandLine.NewWindowSwitch, FolderCommandLine.OpenFolderSwitch, forced.FolderPath], out forced, out _)
            && forced.ForceNewWindow && forced.DestinationId != Guid.Empty
            && FolderCommandLine.TryParse(FolderCommandLine.BuildArguments(forced), out var rebuilt, out _)
            && rebuilt.ForceNewWindow && rebuilt.DestinationId == forced.DestinationId);
        var forcedReceipt = await Route(forced);
        Check("an explicit new window overrides tab mode", ExplorerLaunchRouter.ReceiptMatches(forcedReceipt, forced)
            && forcedReceipt.Window != Handle(host) && Hosts().Length == 2);
        var forcedHost = Hosts().Single(window => Handle(window) == forcedReceipt.Window);
        await Close(forcedHost);

        var modal = Open(root, "opened while the available tab shell is disabled");
        FolderRouteReceipt modalReceipt;
        var prompt = new Window { Owner = host, Width = 200, Height = 120, ShowActivated = false, Title = "Owned modal fixture" };
        var showingPrompt = host.Dispatcher.InvokeAsync(() => prompt.ShowDialog());
        await Wait(() => prompt.IsVisible);
        Check("the fixture prompt really disables its own native owner", prompt.IsVisible && !DialogNative.IsWindowEnabled((nint)Handle(host)));
        try { modalReceipt = await Route(modal); }
        finally { prompt.Close(); }
        await showingPrompt.Task;
        var modalHost = Hosts().Single(window => Handle(window) == modalReceipt.Window);
        Check("a disabled tab shell cannot trap a new folder behind its modal dialog", ExplorerLaunchRouter.ReceiptMatches(modalReceipt, modal)
            && modalReceipt.Window != Handle(host) && modalHost.IsEnabled && initialContext.MatchesFolderInvocation(initial)
            && two is not null && two.MatchesFolderInvocation(tabTwo));
        await Close(modalHost);

        if (one is null) throw new InvalidOperationException("First tab unavailable.");
        await Close(one);
        Check("closing one tab invalidates only its exact ready receipt", host.FindFolderContext(tabOne.DestinationId) is null
            && !await ExplorerLaunchRouter.ValidateReadyAsync(tabOneReceipt, tabOne, CancellationToken.None)
            && two is not null && two.MatchesFolderInvocation(tabTwo) && Hosts().Length == 1);

        var abandoned = Open(root, "abandoned adopted tab") with { OriginIsShell = true, DestinationId = Guid.NewGuid() };
        var abandonedReceipt = await Route(abandoned);
        Check("an untouched handoff tab is ready before native-source cleanup", ExplorerLaunchRouter.ReceiptMatches(abandonedReceipt, abandoned)
            && host.FindFolderContext(abandoned.DestinationId) is { IsUntouchedHandoff: true });
        await ExplorerLaunchRouter.DiscardIfUntouchedAsync(abandoned);
        await Wait(() => host.FindFolderContext(abandoned.DestinationId) is null);
        Check("discarding an abandoned handoff closes that tab and leaves other contexts intact", host.FindFolderContext(abandoned.DestinationId) is null
            && host.FindFolderContext(tabTwo.DestinationId) is not null && initialContext.MatchesFolderInvocation(initial) && Hosts().Length == 1);

        var direct = Open(root, "in-process shell action");
        Mode(FolderOpenMode.SeparateWindow);
        Check("an in-process shell action is accepted", ExplorerLaunchRouter.TryOpenFolder(direct.FolderPath));
        await Wait(() => Hosts().Any(window => Handle(window) != Handle(host) && window.ActiveTabContext.MatchesFolderInvocation(direct)));
        Check("in-process shell actions also open a fresh context", Hosts().Length == 2 && initialContext.MatchesFolderInvocation(initial)
            && two is not null && two.MatchesFolderInvocation(tabTwo));

        using var self = Process.GetCurrentProcess();
        var source = new FolderInvocationSource(Handle(host), checked((uint)self.Id), self.StartTime.ToUniversalTime().ToFileTimeUtc());
        var from = abandoned with { Source = source };
        Check("source frame identity survives command-line bootstrapping", FolderCommandLine.TryParse(FolderCommandLine.BuildArguments(from), out var sourceRoundtrip, out _)
            && sourceRoundtrip.Source == source && sourceRoundtrip.DestinationId == from.DestinationId);
        Check("foreign or stale foreground hints are refused", !ExplorerLaunchRouter.ForegroundSourceMatches(source)
            && !ExplorerLaunchRouter.ForegroundSourceMatches(source with { Started = source.Started + 1 })
            && !ExplorerLaunchRouter.ForegroundSourceMatches(source with { Window = 0 }));
        Check("malformed source hints and duplicate forcing are refused", !FolderCommandLine.TryParse(
            [FolderCommandLine.SourceSwitch, "0:1:2", FolderCommandLine.ShellRequestSwitch, FolderCommandLine.DestinationSwitch,
                Guid.NewGuid().ToString("D"), FolderCommandLine.OpenFolderSwitch, direct.FolderPath], out _, out _)
            && !FolderCommandLine.TryParse([FolderCommandLine.NewWindowSwitch, FolderCommandLine.NewWindowSwitch,
                FolderCommandLine.OpenFolderSwitch, direct.FolderPath], out _, out _));
        Check("fixtures cannot activate real desktop windows", !DialogNative.MayActivate);
    }

    private static async Task Wait(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 60_000;
        while (!condition() && Environment.TickCount64 < deadline) await Task.Delay(20);
    }
}
