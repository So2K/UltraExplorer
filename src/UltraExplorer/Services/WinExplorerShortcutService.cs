using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UltraExplorer.Services;

/// <summary>Own-process Win+E override. The hook exists only between Start and
/// Dispose, and its dedicated message thread never runs application/UI work.
/// No keyboard history, window inspection, DLL injection or registry changes.</summary>
internal sealed class WinExplorerShortcutService : IDisposable
{
    private const int KeyboardHook = 13;
    private const uint QuitMessage = 0x12;
    private const uint LaunchMessage = 0x8000 + 0x45;
    private const uint KeyDownMessage = 0x100, KeyUpMessage = 0x101;
    private const uint SystemKeyDownMessage = 0x104, SystemKeyUpMessage = 0x105;
    private const uint InjectedFlag = 0x10;
    private readonly object _lifetime = new();
    private readonly Action _launch;
    private readonly Thread _thread;
    private readonly HookProcedure _hookProcedure;
    private readonly ManualResetEventSlim _startedSignal = new();
    private readonly WinExplorerShortcutPolicy _policy = new();
    private readonly bool _testOnlyMessagePump;
    private readonly bool _testOnlyStartupFailure;
    private NativeInput _menuDismissInput = CreateMenuDismissInput();
    private nint _hook;
    private uint _threadId;
    private int _started, _disposed, _ready, _launchQueued;
    private int _nativeFailureKind, _nativeFailureCode;
    private string? _lastError;

    internal WinExplorerShortcutService(Action launch) : this(launch, false, false) { }

    private WinExplorerShortcutService(Action launch, bool testOnlyMessagePump, bool testOnlyStartupFailure)
    {
        ArgumentNullException.ThrowIfNull(launch);
        _launch = launch;
        _testOnlyMessagePump = testOnlyMessagePump;
        _testOnlyStartupFailure = testOnlyStartupFailure;
        _hookProcedure = OnKeyboard;
        _thread = new Thread(Run) { IsBackground = true, Name = "UltraExplorer Win+E" };
    }

    /// <summary>Installation and message-pump readiness, not a promise that
    /// Windows has never silently removed a timed-out low-level hook.</summary>
    internal bool IsReady => Volatile.Read(ref _ready) != 0 && Volatile.Read(ref _disposed) == 0;

    internal string? LastError
    {
        get
        {
            if (Volatile.Read(ref _lastError) is { } error) return error;
            var kind = Volatile.Read(ref _nativeFailureKind);
            if (kind == 0) return null;
            var operation = kind switch
            {
                1 => "SetWindowsHookEx", 2 => "PostThreadMessage", 3 => "SendInput",
                4 => "GetMessage", _ => "UnhookWindowsHookEx"
            };
            var code = Volatile.Read(ref _nativeFailureCode);
            return code == 0 ? $"{operation} was rejected by Windows."
                : $"{operation}: {new Win32Exception(code).Message} ({code}).";
        }
    }

    /// <summary>Returns false with LastError if installation fails. A failed
    /// service leaves the normal Windows shortcut untouched. Create a new
    /// instance to retry after disposal or failure.</summary>
    internal bool Start()
    {
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_started == 0)
            {
                _started = 1;
                _thread.Start();
            }
        }
        if (!_startedSignal.Wait(TimeSpan.FromSeconds(3)))
        {
            Volatile.Write(ref _lastError, "The Win+E message thread did not become ready.");
            Dispose();
        }
        return IsReady;
    }

    private void Run()
    {
        try
        {
            // PostThreadMessage requires the receiving thread's queue to exist.
            PeekMessage(out _, 0, 0, 0, 0);
            Volatile.Write(ref _threadId, GetCurrentThreadId());
            if (Volatile.Read(ref _disposed) != 0) return;
            if (_testOnlyStartupFailure)
            {
                Volatile.Write(ref _lastError, "Owned fixture startup failure.");
                return;
            }
            if (!_testOnlyMessagePump)
            {
                // Do not reinterpret an E already held when the feature starts.
                _policy.PrimeE(IsDown(0x45));
                var hook = SetWindowsHookEx(KeyboardHook, _hookProcedure, GetModuleHandle(null), 0);
                if (hook == 0)
                {
                    RecordNativeFailure(1, Marshal.GetLastWin32Error());
                    return;
                }
                lock (_lifetime)
                {
                    if (_disposed != 0) UnhookWindowsHookEx(hook);
                    else _hook = hook;
                }
            }
            lock (_lifetime)
            {
                if (_disposed != 0) return;
                Volatile.Write(ref _ready, 1);
            }
            _startedSignal.Set();
            while (Volatile.Read(ref _disposed) == 0)
            {
                var result = GetMessage(out var message, 0, 0, 0);
                if (result <= 0)
                {
                    if (result < 0) RecordNativeFailure(4, Marshal.GetLastWin32Error());
                    break;
                }
                if (message.Id == LaunchMessage) QueueLaunch();
                else
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Volatile.Write(ref _lastError, error.Message);
        }
        finally
        {
            Volatile.Write(ref _ready, 0);
            RemoveHook();
            _startedSignal.Set();
        }
    }

    private nint OnKeyboard(int code, nint message, nint data)
    {
        if (code < 0 || !IsReady || data == 0) return CallNextHookEx(0, code, message, data);
        var kind = unchecked((uint)message);
        if (kind is not (KeyDownMessage or KeyUpMessage or SystemKeyDownMessage or SystemKeyUpMessage)
            || Marshal.ReadInt32(data) != 0x45) return CallNextHookEx(0, code, message, data);
        // Inspect only E and current modifier bits. GetAsyncKeyState is safe for
        // these modifiers: the event being processed changes E, not a modifier.
        var injected = (unchecked((uint)Marshal.ReadInt32(data, 8)) & InjectedFlag) != 0;
        var down = kind is KeyDownMessage or SystemKeyDownMessage;
        var decision = _policy.Process(0x45, down, injected, ReadModifiers());
        if (decision == WinExplorerShortcutDecision.Pass) return CallNextHookEx(0, code, message, data);
        if (decision == WinExplorerShortcutDecision.Launch)
        {
            // PowerToys' VK 0xFF key-up keeps Win release from opening Start.
            // It is a single non-text event with our origin marker, emitted only
            // for the exact claimed chord. Win and every unrelated key pass on.
            if (SendInput(1, ref _menuDismissInput, Marshal.SizeOf<NativeInput>()) != 1)
            {
                FailOpenFromHook(3, Marshal.GetLastWin32Error());
                return CallNextHookEx(0, code, message, data);
            }
            if (!PostThreadMessage(Volatile.Read(ref _threadId), LaunchMessage, 0, 0))
            {
                FailOpenFromHook(2, Marshal.GetLastWin32Error());
                return CallNextHookEx(0, code, message, data);
            }
        }
        // OFF wins even when it races a callback already in progress.
        return IsReady ? 1 : CallNextHookEx(0, code, message, data);
    }

    private void FailOpenFromHook(int kind, int error)
    {
        _policy.CancelClaim();
        RecordNativeFailure(kind, error);
        PostThreadMessage(Volatile.Read(ref _threadId), QuitMessage, 0, 0);
    }

    private void RecordNativeFailure(int kind, int error)
    {
        Volatile.Write(ref _ready, 0);
        Volatile.Write(ref _nativeFailureCode, error);
        Volatile.Write(ref _nativeFailureKind, kind);
    }

    private void QueueLaunch()
    {
        if (!IsReady || Interlocked.Exchange(ref _launchQueued, 1) != 0) return;
        ThreadPool.QueueUserWorkItem(static service =>
        {
            try { if (service.IsReady) service._launch(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Volatile.Write(ref service._lastError, error.Message);
                service.Dispose();
            }
            finally { Interlocked.Exchange(ref service._launchQueued, 0); }
        }, this, preferLocal: false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _ready, 0);
        // Gate callbacks first, then remove the native hook on the calling
        // thread immediately. Launch work cannot hold up keyboard restoration.
        RemoveHook();
        var threadId = Volatile.Read(ref _threadId);
        if (threadId != 0) PostThreadMessage(threadId, QuitMessage, 0, 0);
        if (Volatile.Read(ref _started) != 0 && Thread.CurrentThread != _thread)
            _thread.Join(TimeSpan.FromMilliseconds(500));
    }

    private void RemoveHook()
    {
        nint hook;
        lock (_lifetime) { hook = _hook; _hook = 0; }
        if (hook != 0 && !UnhookWindowsHookEx(hook)) RecordNativeFailure(5, Marshal.GetLastWin32Error());
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    private static WinExplorerShortcutModifiers ReadModifiers() =>
        (IsDown(0x5B) || IsDown(0x5C) ? WinExplorerShortcutModifiers.Windows : 0)
        | (IsDown(0x11) ? WinExplorerShortcutModifiers.Control : 0)
        | (IsDown(0x10) ? WinExplorerShortcutModifiers.Shift : 0)
        | (IsDown(0x12) ? WinExplorerShortcutModifiers.Alt : 0);

    // Reference (MIT), KeyboardHookProc, "prevent Start Menu from activating":
    // https://github.com/microsoft/PowerToys/blob/1f9bae97f26788ccaa5eb6830655c67a399e073d/src/runner/centralized_kb_hook.cpp
    internal static NativeInput CreateMenuDismissInput() => new()
    {
        Type = 1,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput { VirtualKey = 0xFF, Flags = 2, ExtraInfo = unchecked((nuint)0x554C54524157494EUL) }
        }
    };

    // These fixtures never install a keyboard hook or synthesize system input.
    internal static WinExplorerShortcutService CreateForChecks(Action launch, bool failStartup = false) =>
        new(launch, testOnlyMessagePump: true, testOnlyStartupFailure: failStartup);
    internal bool PostLaunchForChecks() => _testOnlyMessagePump && IsReady
        && PostThreadMessage(Volatile.Read(ref _threadId), LaunchMessage, 0, 0);
    internal bool ThreadStoppedForChecks => !_thread.IsAlive;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeInput { internal uint Type; internal InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] internal KeyboardInput Keyboard;
        [FieldOffset(0)] internal MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput { internal ushort VirtualKey, Scan; internal uint Flags, Time; internal nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput { internal int X, Y; internal uint Data, Flags, Time; internal nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage { internal nint Window; internal uint Id; internal nuint WParam; internal nint LParam; internal uint Time; internal int X, Y; internal uint Private; }
    private delegate nint HookProcedure(int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern nint SetWindowsHookEx(int kind, HookProcedure callback, nint module, uint thread);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, ref NativeInput input, int size);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out NativeMessage message, nint window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)] private static extern int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint thread, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref NativeMessage message);
}

[Flags]
internal enum WinExplorerShortcutModifiers { None = 0, Windows = 1, Control = 2, Shift = 4, Alt = 8 }
internal enum WinExplorerShortcutDecision { Pass, Suppress, Launch }

/// <summary>A one-key latch, not a keyboard recorder. Modifier snapshots are
/// supplied only for E; unrelated keys never change or occupy stored state.</summary>
internal sealed class WinExplorerShortcutPolicy
{
    private bool _eDown, _claimed;
    internal void PrimeE(bool held) { _eDown = held; _claimed = false; }
    internal void CancelClaim() => _claimed = false;

    internal WinExplorerShortcutDecision Process(int virtualKey, bool down, bool injected, WinExplorerShortcutModifiers modifiers)
    {
        if (virtualKey != 0x45 || injected) return WinExplorerShortcutDecision.Pass;
        if (!down)
        {
            var decision = _claimed ? WinExplorerShortcutDecision.Suppress : WinExplorerShortcutDecision.Pass;
            _eDown = _claimed = false;
            return decision;
        }
        if (_eDown) return _claimed ? WinExplorerShortcutDecision.Suppress : WinExplorerShortcutDecision.Pass;
        _eDown = true;
        _claimed = modifiers == WinExplorerShortcutModifiers.Windows;
        return _claimed ? WinExplorerShortcutDecision.Launch : WinExplorerShortcutDecision.Pass;
    }
}
