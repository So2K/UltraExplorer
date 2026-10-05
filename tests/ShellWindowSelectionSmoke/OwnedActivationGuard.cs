using System.ComponentModel;
using System.Runtime.InteropServices;

/// <summary>A callback inside this process, bound only to its fixture UI thread.
/// Blocks activation/focus only for the exact fixture HWND or its own children.
/// No DLL injection and no hook in any external thread or process.</summary>
internal sealed class OwnedActivationGuard : IDisposable
{
    private readonly nint _window;
    private readonly uint _process;
    private readonly CbtCallback _callback;
    private readonly nint _hook;
    internal int BlockedAttempts { get; private set; }

    internal OwnedActivationGuard(nint window)
    {
        _window = window;
        GetWindowThreadProcessId(window, out _process);
        _callback = OnCbt;
        _hook = SetWindowsHookEx(5, _callback, 0, GetCurrentThreadId()); // WH_CBT, own thread, own module.
        if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private nint OnCbt(int code, nint target, nint detail)
    {
        if (code is 5 or 9 && (target == _window || IsChild(_window, target))) // HCBT_ACTIVATE/HCBT_SETFOCUS.
        {
            GetWindowThreadProcessId(target, out var process);
            if (process == _process) { BlockedAttempts++; return 1; }
        }
        return CallNextHookEx(_hook, code, target, detail);
    }

    public void Dispose() { UnhookWindowsHookEx(_hook); GC.KeepAlive(_callback); }
    private delegate nint CbtCallback(int code, nint target, nint detail);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern nint SetWindowsHookEx(int kind, CbtCallback callback, nint module, uint thread);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint target, nint detail);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
}
