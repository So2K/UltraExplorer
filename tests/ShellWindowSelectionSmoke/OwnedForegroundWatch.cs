using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

/// <summary>Observes foreground metadata without activating or manipulating any
/// window. Unrelated user applications can change foreground freely. A client
/// PID is owned only while its process creation time still matches the launch.</summary>
internal sealed class OwnedForegroundWatch : IDisposable
{
    private readonly nint _ownedWindow;
    private readonly uint _sourceProcess;
    private readonly long _sourceStarted;
    private readonly Dictionary<uint, long> _clients = [];
    private readonly List<Activation> _activations = [];
    private readonly ForegroundCallback _callback;
    private readonly nint _hook;
    private readonly DispatcherTimer _sample;
    internal string DesktopName { get; }

    internal OwnedForegroundWatch(nint ownedWindow)
    {
        _ownedWindow = ownedWindow;
        using (var source = Process.GetCurrentProcess())
        { _sourceProcess = checked((uint)source.Id); _sourceStarted = source.StartTime.ToUniversalTime().Ticks; }
        var name = new StringBuilder(256);
        if (!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, name,
                checked((uint)name.Capacity * 2), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        DesktopName = name.ToString();
        _callback = (_, _, window, _, _, _, _) => Observe(window);
        _hook = SetWinEventHook(3, 3, 0, _callback, 0, 0, 0); // OUTOFCONTEXT, metadata only.
        if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _sample = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        _sample.Tick += (_, _) => Observe(GetForegroundWindow());
        _sample.Start();
        Observe(GetForegroundWindow());
    }

    internal void TrackClient(Process process)
    {
        _clients.Add(checked((uint)process.Id), process.StartTime.ToUniversalTime().Ticks);
        Observe(GetForegroundWindow());
    }

    internal bool NoOwnedActivation { get { Observe(GetForegroundWindow()); return _activations.Count == 0; } }

    internal string FailureDetails() => _activations.Count == 0 ? "" : JsonSerializer.Serialize(new
    {
        scope = "Main/DefaultDesktopNoOwnActivation",
        ownedWindow = _ownedWindow.ToInt64(),
        sourceProcess = _sourceProcess,
        sourceStarted = _sourceStarted,
        observedOwnedActivations = _activations,
        exactOwnWindowForegroundAtFailure = GetForegroundWindow() == _ownedWindow
    });

    private void Observe(nint window)
    {
        if (window == 0) return;
        GetWindowThreadProcessId(window, out var pid);
        if (window == _ownedWindow)
        {
            Add(new(window.ToInt64(), pid, _sourceStarted, "exact test HWND/source PID/start"));
            return;
        }
        if (!_clients.TryGetValue(pid, out var expectedStarted)) return;
        try
        {
            using var process = Process.GetProcessById(checked((int)pid));
            var started = process.StartTime.ToUniversalTime().Ticks;
            if (started == expectedStarted) Add(new(window.ToInt64(), pid, started, "verified native-client PID/start"));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception) { }
    }

    private void Add(Activation activation)
    {
        if (!_activations.Contains(activation)) _activations.Add(activation);
    }

    public void Dispose()
    {
        _sample.Stop();
        UnhookWinEvent(_hook);
        GC.KeepAlive(_callback);
    }

    private sealed record Activation(long Window, uint Process, long? Started, string Ownership);
    private delegate void ForegroundCallback(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(nint handle, int kind, StringBuilder name, uint bytes, out uint needed);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWinEventHook(uint minimum, uint maximum, nint module, ForegroundCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
