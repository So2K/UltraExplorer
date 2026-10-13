using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private const string ForegroundFixtureVariable = "ULTRAEXPLORER_FOREGROUND_FIXTURE";

    private static async Task FolderForegroundChecks()
    {
        if (Environment.GetEnvironmentVariable(ForegroundFixtureVariable) is { Length: > 0 } fixture)
        {
            FolderForegroundFixture.Run(fixture);
            return;
        }
        Section("foreground intent ownership and native bridge requests on an inactive desktop");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerForeground-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var desktopName = "UltraExplorerForeground-" + Guid.NewGuid().ToString("N");
        var desktop = CreateDesktop(desktopName, 0, 0, 0, 0x01ff, 0);
        if (desktop == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var startup = new FixtureStartup { Size = Marshal.SizeOf<FixtureStartup>(), Desktop = desktopName };
        var executable = Environment.ProcessPath!;
        var arguments = NativeShellService.BuildCommandLine([executable, "--only", nameof(FolderForegroundChecks)]);
        FixtureProcess process = default;
        try
        {
            Environment.SetEnvironmentVariable(ForegroundFixtureVariable, root);
            var originalTestWindow = Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW");
            Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", null);
            try
            {
                if (!CreateProcess(executable, new System.Text.StringBuilder(arguments), 0, 0, false, 0x08000000,
                    0, Path.GetDirectoryName(executable), ref startup, out process))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                Environment.SetEnvironmentVariable(ForegroundFixtureVariable, null);
                Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", originalTestWindow);
            }
            using var child = Process.GetProcessById(checked((int)process.Id));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
            var results = Path.Combine(root, "results.txt");
            foreach (var line in File.Exists(results) ? File.ReadAllLines(results) : [])
                Check(line.StartsWith("ok ") ? line[3..] : line.StartsWith("FAIL ") ? line[5..] : line, line.StartsWith("ok "));
            Check("the foreground fixture stayed on its own desktop and exited", File.Exists(results) && child.ExitCode == 0);
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

/// <summary>Windows exposes no foreground on an inactive desktop. The fixture
/// controls the intent signal and records bridge calls, while the HWNDs, process
/// lifetimes, WPF contexts and IPC are real. It never activates the user's desktop.</summary>
internal static class FolderForegroundFixture
{
    private static string _results = string.Empty;
    private static long _foreground;
    private static int _bridgeCalls;
    private static (nint Target, nint Source) _lastBridge;

    internal static void Run(string root)
    {
        _results = Path.Combine(root, "results.txt");
        Environment.SetEnvironmentVariable(AppPaths.StateDirectoryVariable, Path.Combine(root, "state"));
        Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", null);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var nativeForeground = ExplorerLaunchRouter.NativeForeground;
        var foregroundBridge = ExplorerLaunchRouter.ForegroundBridge;
        ExplorerLaunchRouter.NativeForeground = () => (nint)Volatile.Read(ref _foreground);
        ExplorerLaunchRouter.ForegroundBridge = (target, source) =>
        {
            _lastBridge = (target, source);
            _bridgeCalls++;
            Volatile.Write(ref _foreground, target.ToInt64());
            return true;
        };
        var frame = new DispatcherFrame();
        _ = dispatcher.InvokeAsync(async () =>
        {
            try { await Checks(root); }
            catch (Exception ex) { Check("foreground fixture completed without an exception: " + ex, false); }
            finally
            {
                ExplorerLaunchRouter.NativeForeground = nativeForeground;
                ExplorerLaunchRouter.ForegroundBridge = foregroundBridge;
                foreach (var window in app.Windows.OfType<MainWindow>().Where(window => ReferenceEquals(window, window.ShellWindow)).ToArray()) window.Close();
                await MainWindow.WhenClosingWindowsClosedAsync(app, TimeSpan.FromSeconds(10));
                foreach (var window in app.Windows.Cast<Window>().ToArray()) window.Close();
                frame.Continue = false;
            }
        });
        Dispatcher.PushFrame(frame);
    }

    private static void Check(string name, bool passed)
        => File.AppendAllText(_results, (passed ? "ok " : "FAIL ") + name + Environment.NewLine);
    private static nint Handle(Window window) => new WindowInteropHelper(window).Handle;
    private static async Task Foreground(Window window)
    {
        if (!window.IsVisible) window.Show();
        Volatile.Write(ref _foreground, Handle(window).ToInt64());
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    private static async Task Checks(string root)
    {
        Check("the fixture cannot register production integration", !DialogStartup.IsAllowed && !ShellReplacementRegistration.IsAllowed);
        var source = new Window { Title = "Owned launch source", Width = 260, Height = 180, ShowActivated = false, Content = new TextBlock { Text = "Owned source" } };
        var other = new Window { Title = "Owned later foreground", Width = 260, Height = 180, ShowActivated = false, Content = new TextBlock { Text = "Owned later choice" } };
        await Foreground(source);
        var intent = ExplorerLaunchRouter.CaptureForegroundIntent();
        Check("the controlled foreground signal resolves an exact real owned window lifetime", DialogNative.MayActivate && intent is not null
            && intent.Window == Handle(source).ToInt64() && ExplorerLaunchRouter.ForegroundSourceMatches(intent, requireExplorer: false)
            && !ExplorerLaunchRouter.ForegroundSourceMatches(intent));
        if (intent is null || intent.Window != Handle(source).ToInt64())
            throw new InvalidOperationException("Owned foreground unavailable on fixture desktop.");
        var folder = Path.Combine(root, "hotkey home replacement"); Directory.CreateDirectory(folder);
        FolderCommandLine.TryOpenFolder(folder, out var open, out _);
        var invocation = open with { DestinationId = Guid.NewGuid(), ActivationSource = intent };
        Check("activation identity roundtrips without general shell replacement", FolderCommandLine.TryParse(FolderCommandLine.BuildArguments(invocation), out var rebuilt, out _)
            && !rebuilt.OriginIsShell && rebuilt.ActivationSource == intent && rebuilt.Source is null && rebuilt.DestinationId == invocation.DestinationId);
        var host = new MainWindow(null, ExplorerLaunchRouter.FolderWorkspacePath(invocation.DestinationId)) { ShowActivated = false };
        var context = host.EnableFolderTabs(invocation);
        host.Show();
        Check("a gesture launch opens its exact folder and requests the bridge for the visible shell", await context.ApplyFolderInvocationAsync(invocation).WaitAsync(TimeSpan.FromSeconds(60))
            && _bridgeCalls == 1 && _lastBridge == (Handle(host), Handle(source)));
        await Foreground(source);
        var once = FastEntryPoint.PrepareHomeInvocation();
        Check("Win+E captures one explicit gesture without general integration", !once.OriginIsShell && once.DestinationId != Guid.Empty
            && once.ActivationSource is not null && once.ActivationSource.Window == Handle(source).ToInt64());
        await Foreground(other);
        ExplorerLaunchRouter.PresentFolderInvocationWindow(context, invocation);
        Check("a later foreground intent prevents stale bridge requests", (nint)Volatile.Read(ref _foreground) == Handle(other) && _bridgeCalls == 1
            && !ExplorerLaunchRouter.ForegroundSourceMatches(intent, requireExplorer: false));
        host.WindowState = WindowState.Minimized;
        await Foreground(other);
        ExplorerLaunchRouter.PresentFolderInvocationWindow(context, invocation);
        Check("a stale gesture cannot restore a minimized destination over the later foreground choice",
            host.WindowState == WindowState.Minimized && (nint)Volatile.Read(ref _foreground) == Handle(other) && _bridgeCalls == 1);
        host.WindowState = WindowState.Normal;
        await Foreground(source);
        ExplorerLaunchRouter.PresentFolderInvocationWindow(context, invocation with { ActivationSource = intent with { Started = intent.Started + 1 } });
        Check("a reused or stale source lifetime cannot request the destination bridge", (nint)Volatile.Read(ref _foreground) == Handle(source) && _bridgeCalls == 1);
        var native = invocation with { OriginIsShell = true, Source = intent, ActivationSource = null };
        ExplorerLaunchRouter.PresentFolderInvocationWindow(context, native);
        Check("an ordinary application never masquerades as an adopted native Explorer source", (nint)Volatile.Read(ref _foreground) == Handle(source) && _bridgeCalls == 1);
        Check("conflicting native and explicit gesture hints are rejected", !FolderCommandLine.TryParse(
            FolderCommandLine.BuildArguments(native with { ActivationSource = intent }), out _, out _));

        Directory.CreateDirectory(AppPaths.StateDirectory);
        File.WriteAllText(DialogIntegrationStore.SettingsPath, JsonSerializer.Serialize(new DialogIntegrationSettings
        { Enabled = false, WinEEnabled = true }));
        var startHome = WinEShortcutAgent.StartHomeProcess;
        string[]? fallback = null;
        WinEShortcutAgent.StartHomeProcess = arguments => { fallback = arguments; return Process.GetCurrentProcess(); };
        try
        {
            await Foreground(source);
            var rejected = RejectHomeAsync(Dispatcher.CurrentDispatcher, other);
            await Task.Run(WinEShortcutAgent.LaunchHome).WaitAsync(TimeSpan.FromSeconds(10));
            var original = await rejected;
            Check("a rejected Win+E broker offer preserves one captured identity through fallback", fallback is not null
                && FolderCommandLine.TryParse(fallback, out var homeFallback, out _)
                && homeFallback.DestinationId == original.Invocation.DestinationId
                && homeFallback.ActivationSource == original.Invocation.ActivationSource
                && original.Invocation.ActivationSource?.Window == Handle(source).ToInt64());
            Check("independent Win+E keeps the later foreground choice while general interception is off",
                !original.Invocation.OriginIsShell && !DialogIntegrationStore.Read().Enabled
                && (nint)Volatile.Read(ref _foreground) == Handle(other) && _bridgeCalls == 1);
        }
        finally { WinEShortcutAgent.StartHomeProcess = startHome; }
    }

    private static async Task<FolderRouteRequest> RejectHomeAsync(Dispatcher dispatcher, Window laterForeground)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var pipe = new NamedPipeServerStream(ExplorerLaunchRouter.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.WaitForConnectionAsync(deadline.Token);
        var request = await ExplorerLaunchRouter.ReadPacketAsync<FolderRouteRequest>(pipe, deadline.Token);
        await dispatcher.InvokeAsync(() => Foreground(laterForeground)).Task.Unwrap();
        using var process = Process.GetCurrentProcess();
        var declined = new FolderRouteReceipt(request.Id, false, false, checked((uint)process.Id),
            process.StartTime.ToUniversalTime().ToFileTimeUtc(), 0, Guid.Empty, "", []);
        await ExplorerLaunchRouter.WritePacketAsync(pipe, declined, deadline.Token);
        return request;
    }
}
