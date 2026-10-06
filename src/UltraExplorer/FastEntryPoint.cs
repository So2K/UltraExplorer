using System.Runtime.InteropServices;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;

namespace UltraExplorer;

/// <summary>
/// The process entry point.  A launcher whose folder or Settings can be handed
/// to an existing window exits before constructing WPF's Application and its
/// resource dictionaries; every other command continues through App exactly as
/// before.
/// </summary>
internal static class FastEntryPoint
{
    private static readonly Lock PreparedGate = new();
    private static PreparedFolder? _preparedFolder;

    [STAThread]
    public static int Main(string[] args)
    {
        SuppressDriveErrorBoxes();
        if (TryForwardBeforeApp(args))
        {
            return FileDialogCommandLine.ExitAccepted;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>
    /// True only when this process's whole job has already been accepted by an
    /// existing window (or by the existing native-Explorer fallback).  False
    /// means the ordinary App startup remains responsible for the invocation.
    /// </summary>
    internal static bool TryForwardBeforeApp(string[] args)
        => TryForward(args, rememberFolderForApp: true);

    /// <summary>Win+E's resident agent uses the same broker without spawning a launcher.</summary>
    internal static bool TryForwardHome()
        => TryForward([FolderCommandLine.HomeSwitch], rememberFolderForApp: false);

    private static bool TryForward(string[] args, bool rememberFolderForApp)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || DialogIntegrationRuntime.IsRole(args)
            || FileDialogCommandLine.IsPickerInvocation(args)
            || HasSwitch(args, "register-picker") || HasSwitch(args, "unregister-picker")
            || HasSwitch(args, "com-server") || HasSwitch(args, "embedding"))
        {
            return false;
        }

        if (FolderCommandLine.IsInvocation(args))
        {
            // An immediate child opened inside a native Explorer frame stays
            // in that exact frame; with replacement disabled this also gives
            // the request straight back to Windows.  This precedes broker
            // forwarding just as it does in App.OnStartup.
            if (ExplorerLaunchRouter.TryHandleShellFallback(args))
            {
                return true;
            }

            if (!FolderCommandLine.TryParse(args, out var invocation, out _))
            {
                // App prints the existing command-line error and exit code.
                return false;
            }

            if (ExplorerLaunchRouter.TryForward(invocation))
            {
                return true;
            }

            if (rememberFolderForApp)
            {
                lock (PreparedGate)
                {
                    _preparedFolder = new([.. args], invocation);
                }
            }

            return false;
        }

        // The tray's Settings command is the only non-folder launch that an
        // ordinary window accepts.  All other switches retain App startup.
        return args.Contains("--settings") && ExplorerLaunchRouter.TryForwardSettings();
    }

    /// <summary>
    /// Hands App the validated invocation whose early broker probe found no
    /// listener.  App still takes the cold-start mutex and retries the broker,
    /// but does not repeat path parsing or the first native fallback probe.
    /// </summary>
    internal static bool TryTakePreparedFolder(IReadOnlyList<string> args, out FolderInvocation invocation)
    {
        lock (PreparedGate)
        {
            var prepared = _preparedFolder;
            _preparedFolder = null;
            if (prepared is not null && prepared.Arguments.SequenceEqual(args, StringComparer.Ordinal))
            {
                invocation = prepared.Invocation;
                return true;
            }
        }

        invocation = null!;
        return false;
    }

    internal static void ResetForChecks()
    {
        lock (PreparedGate)
        {
            _preparedFolder = null;
        }
    }

    private static bool HasSwitch(IReadOnlyList<string> args, string name)
        => args.Any(argument => argument.Length > 1
            && argument[0] is '-' or '/'
            && argument.TrimStart('-', '/').Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The fast path can touch a drive before App.OnStartup repeats this process setting.</summary>
    private static void SuppressDriveErrorBoxes()
    {
        const uint FailCriticalErrors = 0x0001, NoOpenFileErrorBox = 0x8000;
        _ = SetErrorMode(GetErrorMode() | FailCriticalErrors | NoOpenFileErrorBox);
    }

    private sealed record PreparedFolder(string[] Arguments, FolderInvocation Invocation);

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();
}
