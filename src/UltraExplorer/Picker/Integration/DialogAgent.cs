using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace UltraExplorer.Picker.Integration;

internal sealed class DialogAgent : IDisposable
{
    private static string MutexName => @"Local\UltraExplorer.DialogAgent." + DialogIntegrationStore.InstanceKey;
    public static bool IsRunning
    {
        get { if (!Mutex.TryOpenExisting(MutexName, out var mutex)) return false; mutex.Dispose(); return true; }
    }
    private readonly Application _app;
    private readonly Mutex _mutex = new(false, MutexName);
    private readonly DispatcherTimer _poll = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DialogNative.EventCallback _callback;
    private readonly Dictionary<nint, Process?> _seen = [];
    /// <summary>The windows of <see cref="_seen"/>, for the listener's thread to look up.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, byte> _seenWindows = new();
    private DialogEventThread? _listener;
    private readonly uint _onlyProcess;
    private readonly DialogFixtureProcessScope _fixtureScope;
    private DialogAgentTray? _tray;
    private ushort _leaseAtom;
    private long _ticks;
    private DialogWarmClient? _warmWorker;
    private long _lastWorkerStart;
    private bool _ownsMutex, _disposed;
    private UltraExplorer.Services.ExplorerReplacementCoordinator? _folderReplacement;

    public DialogAgent(Application app)
    {
        _app = app;
        _callback = OnEvent;
        // Test copies watch their fixture only, never the user's applications.
        uint.TryParse(Environment.GetEnvironmentVariable("ULTRAEXPLORER_DIALOG_TEST_ONLY_PROCESS"), out _onlyProcess);
        _fixtureScope = new(_onlyProcess);
    }

    public void Start()
    {
        try { _ownsMutex = _mutex.WaitOne(0); } catch (AbandonedMutexException) { _ownsMutex = true; }
        if (!_ownsMutex) { _app.Shutdown(); return; }
        // Whatever a previous session left hidden comes back first, on or off.
        DialogGuardian.Heal();
        var initialSettings = ReconcileRegistrations();
        DialogIntegrationController.EnsureWinEShortcutStarted();
        if (!initialSettings.Enabled)
        {
            // Started at sign-in although the mode was switched off or paused
            // meanwhile: the start entry goes with the setting.
            _app.Shutdown();
            return;
        }
        _leaseAtom = DialogNative.ReserveLeasePropertyAtom();
        DialogGuardian.EnsureRunning();
        _listener = new DialogEventThread(_callback);
        if (!_listener.Started) throw new IOException("Windows could not start the dialog listener.");
        // A test copy's listener keeps out of the user's notification area.
        _tray = new(OpenSettings, Recover, showIcon: DialogNative.MayActivate);
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
        DialogIntegrationStore.Log("Dialog replacement listener started.");
        PrepareWorker();
        if (DialogStartup.IsAllowed)
        {
            try
            {
                if (!ReconcileRegistrations().Enabled) { Dispose(); _app.Shutdown(); return; }
                _folderReplacement = new();
                _folderReplacement.Start();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
            {
                DialogIntegrationStore.Log("Windows folder integration could not be started", ex);
                DialogGuardian.RestoreAll(true, "Folder replacement could not start. Windows is restored.");
            }
        }
    }

    /// <summary>
    /// The sign-in start and the folder verbs made to agree with the setting,
    /// and the setting. Another process holding the settings for longer than
    /// the lock waits - one switching the mode, which applies them itself, or
    /// everything starting at once at sign-in - is no failure of this
    /// listener: it goes on with the setting as it is.
    /// </summary>
    private static DialogIntegrationSettings ReconcileRegistrations()
    {
        try { return DialogIntegrationStore.ReconcileRegistrations(); }
        catch (IOException ex)
        {
            DialogIntegrationStore.Log("The integration settings are busy; the listener goes on with them as they are", ex);
            return DialogIntegrationStore.Read();
        }
    }

    /// <summary>
    /// On the listener's own thread (<see cref="DialogEventThread"/>), for
    /// every window shown, destroyed or brought to the front anywhere: only a
    /// top-level dialog window - of the one process a test copy watches - is
    /// handed to the dispatcher, at once, with the moment its event arrived as
    /// the moment it was recognised. Nothing here waits for another
    /// application's thread: the class, the root and the process of a window
    /// are the window manager's, and UI Automation comes much later.
    /// </summary>
    private void OnEvent(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || window == 0 || objectId != 0 || childId != 0) return;
        if (kind == DialogNative.ObjectDestroyEvent)
        {
            if (_seenWindows.ContainsKey(window)) _ = _app.Dispatcher.BeginInvoke(() => Forget(window), DispatcherPriority.Background);
            return;
        }
        var at = Stopwatch.GetTimestamp();
        if ((_onlyProcess != 0 && !_fixtureScope.Allows(DialogNative.ProcessId(window)))
            || DialogNative.GetAncestor(window, 2) != window || DialogNative.ClassName(window) != "#32770") return;
        _ = _app.Dispatcher.BeginInvoke(() => Probe(window, at), DispatcherPriority.Send);
    }

    private void Poll()
    {
        // The settings file and the leases once a second; the foreground on
        // every tick, since that is how quickly a dialog should be noticed.
        if (++_ticks % 5 == 0)
        {
            if (!DialogIntegrationStore.Read().Enabled) { Dispose(); _app.Shutdown(); return; }
            foreach (var window in _seen.Keys.ToArray())
                if (!DialogNative.IsWindow(window)) Forget(window);
            // The guardian restores a dialog whose replacement stopped; should
            // the guardian itself be gone as well, this puts it back.
            if (_ticks % 10 == 0) DialogGuardian.Heal();
        }
        if ((_warmWorker is null || _warmWorker.NeedsRestart) && Environment.TickCount64 - _lastWorkerStart > 3000)
            PrepareWorker();
        if (_onlyProcess == 0) Probe(DialogNative.GetForegroundWindow());
        else DialogNative.EnumWindows((window, _) => { if (_fixtureScope.Allows(DialogNative.ProcessId(window))) Probe(window); return true; }, 0);
    }

    /// <param name="recognisedAt">When the event that brought the window arrived (<see cref="Stopwatch.GetTimestamp"/>); now for a poll.</param>
    private void Probe(nint window, long recognisedAt = 0)
    {
        if (_onlyProcess != 0 && Environment.GetEnvironmentVariable("ULTRAEXPLORER_DIALOG_TEST_WAIT_FOR_WARM") == "1"
            && _warmWorker?.IsAvailable != true) return;
        if (_disposed || _seen.ContainsKey(window) || (_onlyProcess != 0 && !_fixtureScope.Allows(DialogNative.ProcessId(window)))
            || (_onlyProcess == 0 && DialogNative.GetForegroundWindow() != window) || DialogNative.GetProp(window, DialogNative.LeaseProperty) != 0
            || DialogNative.IsHidden(window) || !DialogNative.LooksLikeFileDialog(window)) return;
        var application = DialogNative.AccessibleApplication(window);
        if (application is null || DialogIntegrationStore.IsExcluded(application)
            || Path.GetFileName(application).Equals(Path.GetFileName(DialogSelfProcess.ExecutablePath), StringComparison.OrdinalIgnoreCase)) return;
        var recognised = recognisedAt != 0 ? recognisedAt : Stopwatch.GetTimestamp();
        try
        {
            var warmId = _warmWorker?.ProcessId ?? 0;
            var active = _seen.Values.Count(process => process is not null && !process.HasExited && process.Id != warmId)
                + (_warmWorker?.IsBusy == true ? 1 : 0);
            if (active >= 8) return;
            _seen[window] = null;
            _seenWindows[window] = 0;
            var arguments = new List<string> { "--dialog-proxy", window.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--recognised", recognised.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (_onlyProcess != 0 && Environment.GetEnvironmentVariable("ULTRAEXPLORER_DIALOG_TEST_CAPTURE_DIR") is { Length: > 0 } capture)
            {
                Directory.CreateDirectory(capture);
                arguments.Add("--capture");
                arguments.Add(Path.Combine(capture, "proxy.png"));
            }
            _seen[window] = _warmWorker?.TryDispatch(window, recognised) ?? DialogSelfProcess.Start([.. arguments]);
        }
        catch (Exception ex) { DialogIntegrationStore.Log("A proxy could not be started; the original dialog is visible.", ex); }
    }

    private void Forget(nint window)
    {
        _seenWindows.TryRemove(window, out _);
        if (_seen.Remove(window, out var process)) process?.Dispose();
    }

    private void PrepareWorker()
    {
        _lastWorkerStart = Environment.TickCount64;
        _warmWorker?.Dispose();
        _warmWorker = new DialogWarmClient(RetryUnclaimedRequest);
        _ = _warmWorker.StartAsync();
    }

    private void RetryUnclaimedRequest(nint window, uint process)
    {
        if (_disposed || !DialogIntegrationStore.Read().Enabled || !DialogNative.IsWindow(window)
            || DialogNative.ProcessId(window) != process || DialogNative.IsHidden(window)
            || DialogNative.GetProp(window, DialogNative.LeaseProperty) != 0) return;
        Forget(window);
        Probe(window);
    }

    private static void OpenSettings() { using var process = DialogSelfProcess.Start("--settings"); }
    private void Recover()
    {
        try { DialogGuardian.RestoreAll(true, "Windows dialogs restored. Replacement is paused."); }
        catch (Exception ex) { DialogIntegrationStore.Log("Restoring Windows dialogs", ex); }
        Dispose();
        _app.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _poll.Stop();
        _listener?.Dispose();
        _tray?.Dispose();
        _warmWorker?.Dispose();
        _folderReplacement?.Dispose();
        foreach (var process in _seen.Values) process?.Dispose();
        _seen.Clear();
        _seenWindows.Clear();
        DialogNative.ReleaseLeasePropertyAtom(_leaseAtom);
        _leaseAtom = 0;
        if (_ownsMutex) { _ownsMutex = false; _mutex.ReleaseMutex(); }
        _mutex.Dispose();
    }
}

/// <summary>
/// The listener's WinEvent hooks - windows shown and destroyed, and the
/// foreground - on a thread of their own that only pumps their events, so
/// an event is taken in the moment it arrives, whatever the listener's
/// dispatcher is doing. Out of context: nothing is loaded into another
/// process.
/// </summary>
internal sealed class DialogEventThread : IDisposable
{
    private readonly DialogNative.EventCallback _callback;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;

    public bool Started { get; private set; }

    public DialogEventThread(DialogNative.EventCallback callback)
    {
        _callback = callback;
        _thread = new Thread(Run) { IsBackground = true, Name = "UltraExplorer dialog listener" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        var foreground = DialogNative.SetWinEventHook(DialogNative.ForegroundEvent, DialogNative.ForegroundEvent, 0, _callback, 0, 0, 2);
        var shown = DialogNative.SetWinEventHook(DialogNative.ObjectDestroyEvent, DialogNative.ObjectShowEvent, 0, _callback, 0, 0, 2);
        Started = foreground != 0 && shown != 0;
        _ready.Set();
        try
        {
            if (Started)
                while (GetMessage(out var message, 0, 0, 0) > 0) DispatchMessage(ref message);
        }
        finally
        {
            if (foreground != 0) DialogNative.UnhookWinEvent(foreground);
            if (shown != 0) DialogNative.UnhookWinEvent(shown);
        }
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, 0x12, 0, 0); // WM_QUIT
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nint W, L; public uint Time; public int X, Y; }
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, nint wparam, nint lparam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}

/// <summary>Visible control of the resident integration, including recovery
/// when the foreground application is in its modal file-dialog loop.</summary>
internal sealed class DialogAgentTray : IDisposable
{
    private readonly HwndSource _source;
    private readonly Action _settings, _recover;
    private NotifyIconData _data;
    private const uint CallbackMessage = 0x8000 + 42;

    /// <summary>Sent to every top-level window once the taskbar exists: at
    /// sign-in when it comes up after this listener, and whenever Explorer
    /// restarts, which takes every notification icon with the old one.</summary>
    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    public DialogAgentTray(Action settings, Action recover, bool showIcon = true)
    {
        _settings = settings; _recover = recover;
        _source = new(new HwndSourceParameters("UltraExplorer dialog integration") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(Message);
        RegisterHotKey(_source.Handle, 1, 1 | 2 | 4 | 0x4000, 0x1B); // Alt+Ctrl+Shift+Esc, no autorepeat.
        if (!showIcon) { _data = new() { Tip = "", Info = "", InfoTitle = "" }; return; }
        ExtractIconEx(DialogSelfProcess.ExecutablePath, 0, out var large, out var small, 1);
        if (large != 0) DestroyIcon(large);
        _data = new() { Size = Marshal.SizeOf<NotifyIconData>(), Window = _source.Handle, Id = 1,
            Flags = 1 | 2 | 4, Callback = CallbackMessage, Icon = small,
            Tip = "UltraExplorer is showing Open and Save dialogs. Right-click to restore Windows dialogs.", Info = "", InfoTitle = "" };
        // At sign-in the taskbar may not be there yet, or too busy to answer.
        // The listener and its hotkey work without the icon, which is added
        // once the taskbar says it is there.
        if (!ShellNotifyIcon(0, ref _data))
            DialogIntegrationStore.Log($"The integration tray icon is not shown yet (error {Marshal.GetLastWin32Error()}); it is added when the taskbar is ready.");
    }

    private nint Message(nint window, int message, nint wparam, nint lparam, ref bool handled)
    {
        if (message == 0x312) { handled = true; _recover(); }
        else if (TaskbarCreated != 0 && (uint)message == TaskbarCreated)
        {
            if (_data.Window != 0) ShellNotifyIcon(0, ref _data);
        }
        else if ((uint)message == CallbackMessage)
        {
            if (lparam == 0x203) _settings();
            else if (lparam == 0x205)
            {
                GetCursorPos(out var point);
                var menu = CreatePopupMenu();
                try
                {
                    AppendMenu(menu, 0, 1, "Open settings");
                    AppendMenu(menu, 0, 2, "Restore Windows dialogs and pause");
                    DialogNative.SetForegroundWindow(_source.Handle);
                    var selected = TrackPopupMenu(menu, 0x100 | 0x80, point.X, point.Y, 0, _source.Handle, 0);
                    if (selected == 1) _settings(); else if (selected == 2) _recover();
                }
                finally { DestroyMenu(menu); }
            }
        }
        return 0;
    }

    public void Dispose()
    {
        // Whether or not the taskbar answered when it was added: one it was
        // too busy to confirm can still have appeared.
        if (_data.Window != 0) ShellNotifyIcon(2, ref _data);
        UnregisterHotKey(_source.Handle, 1);
        if (_data.Icon != 0) DestroyIcon(_data.Icon);
        _source.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIconData
    {
        public int Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid ItemGuid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ShellNotifyIcon(uint command, ref NotifyIconData data);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, out nint large, out nint small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rectangle);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
}
