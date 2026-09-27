using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace UltraExplorer.Services;

/// <summary>
/// Development aid enabled only by <c>--perf &lt;path&gt;</c>: writes how long the
/// expensive parts of a frame took, so a stall can be attributed instead of
/// guessed at.  Every call is a no-op and allocates nothing when it is off.
/// </summary>
public static class PerfLog
{
    private const string Switch = "--perf";

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Gate = new();
    private static readonly StringBuilder Buffer = new();
    private static string? _path;
    private static long _lastFlushMs;

    public static bool IsEnabled { get; private set; }

    /// <summary>Turns the log on when the command line asks for it.</summary>
    public static void Configure(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (!string.Equals(arguments[index], Switch, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _path = arguments[index + 1];
            IsEnabled = true;
            try
            {
                File.WriteAllText(_path, "ms\tspan\tvalue\n");
            }
            catch (Exception ex) when (IsUnwritable(ex))
            {
                // An empty, malformed or protected path turns the log off; it
                // is a development aid, not a reason for the app not to start.
                IsEnabled = false;
            }

            return;
        }
    }

    /// <summary>Times a block; the returned scope writes the line when disposed.</summary>
    public static Scope Measure(string span) => new(span);

    /// <summary>Records a plain number - a count, a size - rather than a duration.</summary>
    public static void Value(string span, double value)
    {
        if (!IsEnabled)
        {
            return;
        }

        Write(span, value);
    }

    private static void Write(string span, double value)
    {
        lock (Gate)
        {
            Buffer.Append(Clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture))
                .Append('\t')
                .Append(span)
                .Append('\t')
                .Append(value.ToString("F2", CultureInfo.InvariantCulture))
                .Append('\n');

            // Flushed on a timer rather than per line: writing to disk inside the
            // thing being measured would be measuring the measurement.
            if (Clock.ElapsedMilliseconds - _lastFlushMs < 250 && Buffer.Length < 64 * 1024)
            {
                return;
            }

            _lastFlushMs = Clock.ElapsedMilliseconds;
            try
            {
                File.AppendAllText(_path!, Buffer.ToString());
            }
            catch (Exception ex) when (IsUnwritable(ex))
            {
                // A log that cannot be written is not worth a crash.
            }

            Buffer.Clear();
        }
    }

    /// <summary>Every way a log file can refuse to be written: in use, read-only or protected, or not a path at all.</summary>
    private static bool IsUnwritable(Exception exception)
        => exception is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException;

    public readonly struct Scope : IDisposable
    {
        private readonly string? _span;
        private readonly long _startTicks;

        public Scope(string span)
        {
            if (!IsEnabled)
            {
                _span = null;
                _startTicks = 0;
                return;
            }

            _span = span;
            _startTicks = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_span is null)
            {
                return;
            }

            Write(_span, Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds);
        }
    }
}
