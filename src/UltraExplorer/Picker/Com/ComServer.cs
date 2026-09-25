using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using UltraExplorer.Infrastructure;

namespace UltraExplorer.Picker.Com;

/// <summary>
/// The open dialog, as a COM class of its own.  A program that already calls
/// <c>CoCreateInstance(CLSID_FileOpenDialog, …)</c> reaches UltraExplorer by
/// changing that one GUID and nothing else.
/// </summary>
[ComVisible(true)]
[Guid(ComServerRegistration.OpenDialogClsid)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class UltraFileOpenDialog : FileDialogComBase, IFileOpenDialog
{
}

/// <summary>The save dialog, the counterpart of <see cref="UltraFileOpenDialog"/>.</summary>
[ComVisible(true)]
[Guid(ComServerRegistration.SaveDialogClsid)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class UltraFileSaveDialog : FileDialogComBase, IFileSaveDialog
{
    public UltraFileSaveDialog()
    {
        // A save dialog that did not ask before replacing a file would be a
        // trap; the system one turns this on for the same reason.
        Request.Mode = FileDialogMode.Save;
        Request.Options |= FileDialogOptions.OverwritePrompt;
    }
}

/// <summary>Hands out one dialog object per <c>CoCreateInstance</c>.</summary>
internal sealed class FileDialogClassFactory(Func<object> create) : IClassFactory
{
    public int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr instance)
    {
        instance = IntPtr.Zero;

        // Aggregation is not supported, which is what the SDK says to answer.
        if (outer != IntPtr.Zero)
        {
            return Hresult.NonNullAggregation;
        }

        var unknown = IntPtr.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(create());
            var result = Marshal.QueryInterface(unknown, in riid, out instance);
            if (result == Hresult.Ok)
            {
                // The server keeps this reference and watches it: when the count
                // falls back to one, the client that asked for the object has
                // let go and the reference is dropped here.
                ComServerHost.Track(unknown);
                unknown = IntPtr.Zero;
            }

            return result;
        }
        catch (Exception exception)
        {
            return Marshal.GetHRForException(exception);
        }
        finally
        {
            if (unknown != IntPtr.Zero)
            {
                Marshal.Release(unknown);
            }
        }
    }

    public int LockServer(bool @lock)
    {
        ComServerHost.Lock(@lock);
        return Hresult.Ok;
    }
}

/// <summary>
/// Keeps the process alive for as long as COM needs it.  A managed object's
/// reference count is not observable from here, so the server watches for its
/// objects to be finalized and leaves once nothing is left holding it.
/// </summary>
internal static class ComServerHost
{
    private static readonly List<uint> Cookies = [];
    private static readonly List<IntPtr> Live = [];
    private static readonly object Gate = new();

    private static int _locks;
    private static int _showing;
    private static DispatcherTimer? _idle;

    /// <summary>
    /// Takes over one reference to a handed-out object.  Managed finalization
    /// is no signal here - a window keeps enough of WPF alive that the object
    /// stays reachable long after the client is gone - but the wrapper's own
    /// reference count says exactly when the client let go.
    /// </summary>
    public static void Track(IntPtr unknown)
    {
        lock (Gate)
        {
            Live.Add(unknown);
        }
    }

    public static void Lock(bool acquire)
    {
        if (acquire)
        {
            Interlocked.Increment(ref _locks);
        }
        else
        {
            Interlocked.Decrement(ref _locks);
        }
    }

    /// <summary>A dialog is on screen, so the process is busy however idle it looks.</summary>
    public static IDisposable EnterShow()
    {
        Interlocked.Increment(ref _showing);
        return new ShowScope();
    }

    /// <summary>
    /// Registers both classes and starts the idle watch.  Returns false when
    /// registration fails, which is a reason to exit rather than sit there
    /// invisible and useless.
    /// </summary>
    public static bool Start(Application application)
    {
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (!Register(new Guid(ComServerRegistration.OpenDialogClsid), () => new UltraFileOpenDialog())
            || !Register(new Guid(ComServerRegistration.SaveDialogClsid), () => new UltraFileSaveDialog()))
        {
            Stop();
            return false;
        }

        _idle = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(20)
        };
        _idle.Tick += (_, _) => CheckIdle(application);
        _idle.Start();

        application.Exit += (_, _) => Stop();
        return true;
    }

    private static bool Register(Guid classId, Func<object> create)
    {
        var factory = new FileDialogClassFactory(create);
        var unknown = Marshal.GetIUnknownForObject(factory);
        try
        {
            var result = ShellNative.CoRegisterClassObject(
                ref classId,
                unknown,
                ShellNative.ClsCtxLocalServer,
                ShellNative.RegClsMultipleUse,
                out var cookie);

            if (result != Hresult.Ok)
            {
                return false;
            }

            lock (Gate)
            {
                Cookies.Add(cookie);
            }

            return true;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private static void Stop()
    {
        lock (Gate)
        {
            foreach (var cookie in Cookies)
            {
                _ = ShellNative.CoRevokeClassObject(cookie);
            }

            Cookies.Clear();
        }
    }

    /// <summary>
    /// Nothing locked, nothing on screen and no live objects means no client is
    /// left.  Finalizers have to be pushed through first, because that is the
    /// only signal a CCW gives when its last reference goes.
    /// </summary>
    private static void CheckIdle(Application application)
    {
        if (Volatile.Read(ref _locks) > 0 || Volatile.Read(ref _showing) > 0)
        {
            return;
        }

        int remaining;
        lock (Gate)
        {
            for (var index = Live.Count - 1; index >= 0; index--)
            {
                var unknown = Live[index];

                // AddRef then Release reports the count without changing it;
                // one is the reference this server itself is holding.
                Marshal.AddRef(unknown);
                if (Marshal.Release(unknown) <= 1)
                {
                    Marshal.Release(unknown);
                    Live.RemoveAt(index);
                }
            }

            remaining = Live.Count;
        }

        if (remaining > 0)
        {
            return;
        }

        ComTrace.Write("no clients left, shutting down");
        _idle?.Stop();
        application.Shutdown(0);
    }

    private sealed class ShowScope : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref _showing);
    }
}

/// <summary>
/// A COM server has no console and no window to complain in, so what goes wrong
/// inside one is otherwise invisible.  This writes it down.
/// </summary>
internal static class ComTrace
{
    private static readonly object Gate = new();

    public static string LogPath { get; } = AppPaths.State("com-server.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Diagnostics are never worth failing a call over.
        }
    }
}

/// <summary>
/// Per-user registration.  Everything goes under <c>HKCU\Software\Classes</c>,
/// so no elevation is involved and nothing outside this account changes.
/// </summary>
internal static class ComServerRegistration
{
    public const string OpenDialogClsid = "A1D3B6E4-59C7-4E1B-9F2A-7C61D8E04B31";
    public const string SaveDialogClsid = "A1D3B6E4-59C7-4E1B-9F2A-7C61D8E04B32";

    public const string ComServerSwitch = "--com-server";

    private const string OpenDialogName = "UltraExplorer Open Dialog";
    private const string SaveDialogName = "UltraExplorer Save Dialog";

    public static string Register(string executablePath)
    {
        var command = $"\"{executablePath}\" {ComServerSwitch}";
        Write(OpenDialogClsid, OpenDialogName, command);
        Write(SaveDialogClsid, SaveDialogName, command);
        return string.Join(Environment.NewLine,
            "Registered for this user only:",
            "  CLSID_UltraExplorerOpenDialog = {" + OpenDialogClsid + "}",
            "  CLSID_UltraExplorerSaveDialog = {" + SaveDialogClsid + "}",
            "  LocalServer32 = " + command,
            string.Empty,
            "Create either of those instead of CLSID_FileOpenDialog or",
            "CLSID_FileSaveDialog and ask for IFileOpenDialog / IFileSaveDialog",
            "as usual.  Undo with --unregister-picker.");
    }

    public static string Unregister()
    {
        Remove(OpenDialogClsid);
        Remove(SaveDialogClsid);
        return "Removed both dialog class registrations for this user.";
    }

    /// <summary>True when both classes are registered and point at this build.</summary>
    public static bool IsRegistered(string executablePath)
    {
        foreach (var clsid in new[] { OpenDialogClsid, SaveDialogClsid })
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{{{clsid}}}\LocalServer32");
            if (key?.GetValue(null) is not string command
                || !command.Contains(executablePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static void Write(string clsid, string name, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{{{clsid}}}");
        key.SetValue(null, name);

        using var server = key.CreateSubKey("LocalServer32");
        server.SetValue(null, command);

        // Out-of-process servers are apartment-threaded from COM's point of
        // view; the window this one shows has to be on an STA anyway.
        server.SetValue("ThreadingModel", "Apartment");
    }

    private static void Remove(string clsid)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{{{clsid}}}", throwOnMissingSubKey: false);
        }
        catch (UnauthorizedAccessException)
        {
            // Nothing to undo that this account is allowed to touch.
        }
    }
}
