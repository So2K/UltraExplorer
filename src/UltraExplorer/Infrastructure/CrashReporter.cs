using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace UltraExplorer.Infrastructure;

/// <summary>
/// The last line of defence for an exception nothing else caught.  Without it
/// any such exception - from an <c>async void</c> event handler, a command, a
/// frame - ends the process on the spot: no message, the session unsaved,
/// nothing on disk to say why.
///
/// Every one is written to <c>crash.log</c> in the state folder.  One on the
/// UI thread while a window is on screen is then survived: the user is told
/// once, and the window carries on, which for a file manager beats losing the
/// window and whatever it was about to save.  Two cases still end the process
/// as before, because carrying on would be worse: nothing is on screen (a
/// failure during start-up would otherwise leave an invisible process
/// behind), or the same thing keeps failing (an exception on every frame or
/// every event is not something to click through).
/// </summary>
internal static class CrashReporter
{
    /// <summary>More than this many in <see cref="StormWindow"/> is a storm.</summary>
    private const int StormLimit = 10;

    /// <summary>The log is started afresh (the old one kept once) past this size.</summary>
    private const long MaximumLogBytes = 1024 * 1024;

    private static readonly TimeSpan StormWindow = TimeSpan.FromSeconds(30);
    private static readonly object Gate = new();
    private static readonly Queue<DateTime> Recent = new();
    private static bool _showing;

    public static string LogPath => AppPaths.State("crash.log");

    public static void Install(Application application)
    {
        application.DispatcherUnhandledException += OnDispatcherUnhandledException;

        // A background thread's exception cannot be survived, only recorded.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log(e.IsTerminating ? "unhandled on a background thread; the process ends" : "unhandled on a background thread",
                e.ExceptionObject as Exception);

        // A faulted task nobody awaited ends nothing, but is worth knowing about.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("in a task nobody awaited", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Writes one exception to the log; never throws.</summary>
    public static void Log(string where, Exception? exception)
    {
        try
        {
            var entry = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture))
                .Append("  ").Append(where)
                .Append("  UltraExplorer ").Append(Version)
                .AppendLine()
                .AppendLine(exception?.ToString() ?? "(no exception object)")
                .AppendLine()
                .ToString();

            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.StateDirectory);
                var log = new FileInfo(LogPath);
                if (log.Exists && log.Length > MaximumLogBytes)
                {
                    File.Move(LogPath, AppPaths.State("crash.old.log"), overwrite: true);
                }

                File.AppendAllText(LogPath, entry, new UTF8Encoding(false));
            }
        }
        catch (Exception logging) when (logging is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            // A log that cannot be written is no reason to fail twice.
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("on the UI thread", e.Exception);

        if (!IsAnyWindowShown() || IsStorm())
        {
            return;
        }

        e.Handled = true;
        Tell(e.Exception);
    }

    private static bool IsAnyWindowShown()
    {
        if (Application.Current is not { } application)
        {
            return false;
        }

        foreach (Window window in application.Windows)
        {
            if (window.IsVisible)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsStorm()
    {
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            Recent.Enqueue(now);
            while (Recent.Count > 0 && now - Recent.Peek() > StormWindow)
            {
                Recent.Dequeue();
            }

            return Recent.Count > StormLimit;
        }
    }

    /// <summary>
    /// One message at a time: the box runs a message loop of its own, and a
    /// second failure while it is up is logged rather than stacked on top.
    /// </summary>
    private static void Tell(Exception exception)
    {
        if (_showing)
        {
            return;
        }

        _showing = true;
        try
        {
            var owner = Application.Current?.MainWindow is { IsVisible: true } main ? main : null;
            var text = $"Something went wrong: {exception.Message}{Environment.NewLine}{Environment.NewLine}"
                + $"UltraExplorer kept running. If it misbehaves, restart it. The details are in {LogPath}";
            if (owner is not null)
            {
                MessageBox.Show(owner, text, "UltraExplorer", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(text, "UltraExplorer", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception showing) when (showing is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The dispatcher may be shutting down; the log already has it.
        }
        finally
        {
            _showing = false;
        }
    }

    private static string Version =>
        typeof(CrashReporter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CrashReporter).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
