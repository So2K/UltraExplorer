using System.Globalization;
using System.Windows;

namespace UltraExplorer.Picker.Integration;

internal static class DialogIntegrationRuntime
{
    public static bool IsRole(string[] args) => args.Any(arg => arg is "--dialog-agent" or "--dialog-guardian" or "--dialog-proxy" or "--dialog-recover" or "--dialog-worker" or "--shortcut-agent");

    public static bool TryRun(Application app, string[] args)
    {
        if (!IsRole(args)) return false;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // A glitch in a picker - a handler that throws - is survived: the
        // dialog goes back to Windows and the process makes way for a fresh
        // one, rather than ending on the spot and pausing the replacement.
        if (args.Contains("--dialog-worker") || args.Contains("--dialog-proxy"))
            UltraExplorer.Infrastructure.CrashReporter.SurviveQuietly(() =>
            {
                if (!NativeDialogProxy.ReturnServedDialogAfterFailure()) app.Shutdown();
            });
        // Start once the dispatcher is pumping, with its synchronization context
        // installed. Awaiting an adapter during OnStartup must not resume WPF
        // window creation on a pool thread.
        _ = app.Dispatcher.InvokeAsync(() => RunAsync(app, args)).Task.Unwrap();
        return true;
    }

    /// <summary>
    /// <c>--dialog-recover</c>, which the uninstaller runs too: every dialog
    /// handed back to Windows, the replacement paused and Win+E given back.
    /// Windows' folder-opening defaults are put back as well, even when the
    /// settings already say the mode is off: with the state folder deleted
    /// while it was on, its receipt went with it, the switch read off, and
    /// nothing else would ever take UltraExplorer's commands off folders -
    /// after an uninstall every folder double-click named a deleted program.
    /// Without a receipt, the registration lets go of the commands that are
    /// UltraExplorer's own (ShellRegistrationTransaction).
    /// </summary>
    internal static void Recover()
    {
        DialogIntegrationStore.Update(settings => settings with { WinEEnabled = false });
        DialogGuardian.RestoreAll(true, "Windows dialogs restored. Replacement is paused.");
        try { ReconcileFolderVerbs(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or System.ComponentModel.Win32Exception)
        { DialogIntegrationStore.Log("The Windows folder-opening defaults could not be restored", ex); }
    }

    /// <summary>How Windows' folder-opening defaults are put back or taken over; a test sees here what is asked.</summary>
    internal static Action<bool> ReconcileFolderVerbs { get; set; } = UltraExplorer.Services.ShellReplacementRegistration.Reconcile;

    private static async Task RunAsync(Application app, string[] args)
    {
        try
        {
            if (args.Contains("--shortcut-agent"))
            {
                await WinEShortcutAgent.RunAsync(app);
                app.Shutdown();
                return;
            }
            if (args.Contains("--dialog-agent"))
            {
                var agent = new DialogAgent(app);
                app.Exit += (_, _) => agent.Dispose();
                agent.Start();
                return;
            }
            if (args.Contains("--dialog-guardian")) await DialogGuardian.RunAsync();
            else if (args.Contains("--dialog-recover")) await Task.Run(Recover);
            else if (args.Contains("--dialog-worker"))
            {
                var workerIndex = Array.IndexOf(args, "--dialog-worker");
                if (workerIndex + 1 >= args.Length) throw new ArgumentException("A dialog worker channel is required.");
                await DialogWarmWorker.RunAsync(app, args[workerIndex + 1]);
            }
            else
            {
                var index = Array.IndexOf(args, "--dialog-proxy");
                if (index < 0 || index + 1 >= args.Length || !long.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var handle))
                    throw new ArgumentException("A native dialog handle is required.");
                var at = Array.IndexOf(args, "--recognised");
                var recognised = at >= 0 && at + 1 < args.Length
                    && long.TryParse(args[at + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) ? ticks : 0;
                await new NativeDialogProxy(app, (nint)handle, recognised).RunAsync();
            }

            // A picker this process showed may still be saving on its way out.
            await MainWindow.WhenClosingWindowsClosedAsync(app, TimeSpan.FromSeconds(3));
            app.Shutdown();
        }
        catch (Exception ex)
        {
            DialogIntegrationStore.Log("Dialog integration stopped", ex);
            try
            {
                if (args.Contains("--shortcut-agent")) WinEShortcutAgent.RecordFailure(ex.Message);
                else DialogGuardian.RestoreAll(true, "Windows dialogs restored after an integration error.");
            }
            catch (Exception recovery) when (recovery is IOException or UnauthorizedAccessException)
            { DialogIntegrationStore.Log("Integration recovery could not be saved", recovery); }
            app.Shutdown(2);
        }
    }
}
