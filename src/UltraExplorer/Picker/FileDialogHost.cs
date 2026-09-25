using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using UltraExplorer.Infrastructure;

namespace UltraExplorer.Picker;

/// <summary>
/// Runs UltraExplorer as somebody else's file dialog: builds the window from a
/// request, keeps it above the calling program, and hands the answer back
/// through a file, the parent console, and the process exit code.
/// </summary>
public static class FileDialogHost
{
    /// <summary>
    /// A picker session lays the canvas out for the caller's folder, so it gets
    /// its own workspace file and cannot disturb the one the user arranged.
    /// </summary>
    public static string WorkspacePath { get; } = AppPaths.State("picker.workspace.json");

    /// <summary>
    /// Puts the picker above the window that asked for it.  The owner is not
    /// disabled: it belongs to another process, and a picker that died would
    /// leave it permanently unusable.
    /// </summary>
    public static void AttachOwner(Window window, nint ownerHandle)
    {
        if (ownerHandle == 0 || !IsWindow(ownerHandle))
        {
            return;
        }

        try
        {
            new WindowInteropHelper(window).Owner = ownerHandle;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        catch (InvalidOperationException)
        {
            // The window already has a source; ownership is a nicety, not a
            // reason to fail the request.
        }
    }

    public static int ExitCodeFor(FileDialogResult result) => result switch
    {
        { Error: not null } => FileDialogCommandLine.ExitError,
        { Accepted: true } => FileDialogCommandLine.ExitAccepted,
        _ => FileDialogCommandLine.ExitCancelled
    };

    /// <summary>
    /// Delivers the answer: a JSON file when one was asked for, and the paths
    /// (or the whole result as JSON) on the parent process's console.
    /// </summary>
    public static void Deliver(PickerInvocation invocation, FileDialogResult result)
    {
        if (!string.IsNullOrWhiteSpace(invocation.ResultPath))
        {
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(invocation.ResultPath));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(invocation.ResultPath, FileDialogJson.WriteResult(result), Encoding.UTF8);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                WriteConsole($"Could not write {invocation.ResultPath}: {exception.Message}");
            }
        }

        if (invocation.PrintJson)
        {
            WriteConsole(FileDialogJson.WriteResult(result));
            return;
        }

        // Printing the paths is the default, because a caller that runs this
        // from a shell wants a line per file and nothing else.
        if (result.Accepted)
        {
            foreach (var path in result.Paths)
            {
                WriteConsole(path);
            }
        }
        else if (result.Error is { Length: > 0 } error)
        {
            WriteConsole(error);
        }
    }

    /// <summary>
    /// Writes one line to whatever the caller can read: its redirected pipe if
    /// there is one, otherwise the console it launched us from.  A GUI process
    /// has no console of its own, so it borrows the parent's.
    /// </summary>
    public static void WriteConsole(string line)
    {
        try
        {
            var standard = Console.OpenStandardOutput();
            if (standard != Stream.Null)
            {
                using var writer = new StreamWriter(standard, new UTF8Encoding(false)) { AutoFlush = true };
                writer.WriteLine(line);
                return;
            }
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            // Fall through to the parent console.
        }

        try
        {
            if (!AttachConsole(AttachParentProcess))
            {
                return;
            }

            using var handle = CreateFile(
                "CONOUT$",
                GenericWrite,
                FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                return;
            }

            using var stream = new FileStream(handle, FileAccess.Write);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(line);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // No console to write to; the result file and exit code remain.
        }
    }

    private const int AttachParentProcess = -1;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string fileName,
        uint access,
        uint share,
        IntPtr security,
        uint creationDisposition,
        uint flags,
        IntPtr template);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint handle);
}
