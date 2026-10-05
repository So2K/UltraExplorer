using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task FastEntryPointChecks()
    {
        Section("fast process entry and Win+E forwarding (J098)");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable))
            || Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1")
        {
            Console.WriteLine("  note: fast-entry IPC checks require isolated state and ULTRAEXPLORER_TEST_WINDOW=1; skipped");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerFastEntry", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var originalStarter = WinEShortcutAgent.StartHomeProcess;
        try
        {
            var folder = Path.Combine(root, "forwarded folder");
            Directory.CreateDirectory(folder);
            FastEntryPoint.ResetForChecks();
            var folderServer = ReceiveOneFastRequestAsync();
            var forwarded = FastEntryPoint.TryForwardBeforeApp([FolderCommandLine.OpenFolderSwitch, folder]);
            var folderRequest = await folderServer;
            Check("a recognized folder reaches the existing listener before WPF App construction",
                forwarded && folderRequest is { Settings: false, Invocation.Kind: FolderInvocationKind.OpenFolder }
                && ViewAllPath.Equals(folderRequest.Invocation.FolderPath, folder));

            FastEntryPoint.ResetForChecks();
            var coldFolder = Path.Combine(root, "cold folder");
            Directory.CreateDirectory(coldFolder);
            var coldArguments = new[] { FolderCommandLine.OpenFolderSwitch, coldFolder };
            var coldForwarded = FastEntryPoint.TryForwardBeforeApp(coldArguments);
            var prepared = FastEntryPoint.TryTakePreparedFolder(coldArguments, out var preparedInvocation);
            Check("without a listener the validated folder remains for ordinary cold App startup",
                !coldForwarded && prepared && preparedInvocation.Kind == FolderInvocationKind.OpenFolder
                && ViewAllPath.Equals(preparedInvocation.FolderPath, coldFolder));

            var specialLaunches = new[]
            {
                Array.Empty<string>(),
                new[] { "--dialog-agent" },
                new[] { "--dialog-agent", "--settings" },
                new[] { "--pick", "--mode", "open" },
                new[] { "--register-picker" },
                new[] { "-Embedding" },
                new[] { FolderCommandLine.OpenFolderSwitch }
            };
            var everySpecialLaunchStayedWithApp = true;
            foreach (var arguments in specialLaunches)
            {
                FastEntryPoint.ResetForChecks();
                everySpecialLaunchStayedWithApp &= !FastEntryPoint.TryForwardBeforeApp(arguments)
                    && !FastEntryPoint.TryTakePreparedFolder(arguments, out _);
            }
            Check("roles, picker, COM, registration, empty and invalid command lines retain their original App path",
                everySpecialLaunchStayedWithApp);

            FastEntryPoint.ResetForChecks();
            var settingsServer = ReceiveOneFastRequestAsync();
            var settingsForwarded = FastEntryPoint.TryForwardBeforeApp(["--settings"]);
            var settingsRequest = await settingsServer;
            Check("Settings is forwarded without constructing a second App",
                settingsForwarded && settingsRequest.Settings && settingsRequest.Invocation is null);

            Directory.CreateDirectory(AppPaths.StateDirectory);
            WriteWinEPreference(enabled: true);
            var fallbackStarts = 0;
            string[]? fallbackArguments = null;
            WinEShortcutAgent.StartHomeProcess = arguments =>
            {
                Interlocked.Increment(ref fallbackStarts);
                fallbackArguments = arguments;
                return Process.GetCurrentProcess();
            };

            var homeServer = ReceiveOneFastRequestAsync();
            WinEShortcutAgent.LaunchHome();
            var homeRequest = await homeServer;
            Check("Win+E sends Home to an existing listener without starting a process",
                fallbackStarts == 0 && homeRequest.Invocation is
                {
                    Kind: FolderInvocationKind.OpenFolder,
                    SelectedPaths.Count: 0
                }
                && ViewAllPath.Equals(homeRequest.Invocation.FolderPath,
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

            WinEShortcutAgent.LaunchHome();
            Check("Win+E starts the normal Home launcher only when no listener answers",
                fallbackStarts == 1 && fallbackArguments is [FolderCommandLine.HomeSwitch]);

            WriteWinEPreference(enabled: false);
            WinEShortcutAgent.LaunchHome();
            Check("turning Win+E off suppresses both broker delivery and process fallback", fallbackStarts == 1);

            WriteWinEPreference(enabled: true);
            WinEShortcutAgent.StartHomeProcess = _ => throw new IOException("owned launch failure");
            var propagated = false;
            try { WinEShortcutAgent.LaunchHome(); }
            catch (IOException error) when (error.Message == "owned launch failure") { propagated = true; }
            Check("a Win+E fallback launch error still reaches the shortcut service failure path", propagated);
        }
        finally
        {
            WinEShortcutAgent.StartHomeProcess = originalStarter;
            FastEntryPoint.ResetForChecks();
            TryDelete(root);
        }
    }

    private static void WriteWinEPreference(bool enabled)
    {
        File.WriteAllText(DialogIntegrationStore.SettingsPath, JsonSerializer.Serialize(
            new DialogIntegrationSettings { Enabled = true, WinEEnabled = enabled }));
    }

    private static async Task<FolderRouteRequest> ReceiveOneFastRequestAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = new NamedPipeServerStream(
            ExplorerLaunchRouter.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await server.WaitForConnectionAsync(deadline.Token);
        var request = await ExplorerLaunchRouter.ReadPacketAsync<FolderRouteRequest>(server, deadline.Token);
        using var process = Process.GetCurrentProcess();
        var invocation = request.Invocation;
        var receipt = new FolderRouteReceipt(
            Request: request.Id,
            Accepted: true,
            Ready: false,
            Process: checked((uint)process.Id),
            Started: process.StartTime.ToUniversalTime().ToFileTimeUtc(),
            Window: 0,
            DestinationId: invocation?.DestinationId ?? Guid.Empty,
            FolderPath: invocation?.FolderPath ?? string.Empty,
            SelectedPaths: invocation?.SelectedPaths ?? Array.Empty<string>());
        await ExplorerLaunchRouter.WritePacketAsync(server, receipt, deadline.Token);
        var commit = new byte[1];
        await server.ReadExactlyAsync(commit, deadline.Token);
        return request;
    }
}
