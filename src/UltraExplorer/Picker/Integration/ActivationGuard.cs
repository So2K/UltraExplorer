using System.Runtime.InteropServices;

namespace UltraExplorer.Picker.Integration;

/// <summary>
/// Refuses activation of this thread's guarded windows. A prepared picker,
/// shown (cloaked) long before any dialog and uncloaked over one, must never
/// become the active window by itself: WPF moves a window it has just made
/// onto a monitor of another scale with a SetWindowPos that does not say
/// "no activation" (HwndTarget.OnDpiChanged), and Windows grants a background
/// process the foreground once the user has been idle a while.
/// WS_EX_NOACTIVATE only stops clicks. A hook on this thread alone (WH_CBT,
/// thread-local: nothing is loaded into another process) answers "no" to
/// every attempt, from the moment the window is created, until it is released
/// on purpose - when the keyboard is handed over in real use.
/// </summary>
internal static class ActivationGuard
{
    private const int CbtHook = 5, CbtCreateWindow = 3, CbtDestroyWindow = 4, CbtActivate = 5;
    private static readonly HashSet<nint> Guarded = [];
    private static HookProc? _procedure;
    private static nint _hook;
    private static int _creating, _refused;

    /// <summary>How many activations were refused in this process, for the log.</summary>
    internal static int Refused => _refused;

    /// <summary>
    /// Every window this thread creates until the returned scope ends is
    /// guarded from its creation: the picker being prepared and whatever WPF
    /// makes along with it (its hidden owner, for one).
    /// </summary>
    public static IDisposable GuardWindowsCreated()
    {
        Install();
        _creating++;
        return new Scope();
    }

    public static void Guard(nint window)
    {
        if (window == 0) return;
        Install();
        Guarded.Add(window);
    }

    public static void Release(nint window) => Guarded.Remove(window);

    public static bool IsGuarded(nint window) => Guarded.Contains(window);

    private static void Install()
    {
        if (_hook != 0) return;
        _procedure = OnCbt;
        _hook = SetWindowsHookEx(CbtHook, _procedure, 0, GetCurrentThreadId());
        if (_hook == 0) DialogIntegrationStore.Log($"The activation guard could not be installed (error {Marshal.GetLastWin32Error()}).");
    }

    private static nint OnCbt(int code, nint wparam, nint lparam)
    {
        if (code == CbtCreateWindow && _creating > 0) Guarded.Add(wparam);
        else if (code == CbtDestroyWindow) Guarded.Remove(wparam);
        else if (code == CbtActivate && Guarded.Contains(wparam))
        {
            _refused++;
            return 1;
        }
        return CallNextHookEx(_hook, code, wparam, lparam);
    }

    private sealed class Scope : IDisposable
    {
        private bool _ended;
        public void Dispose() { if (!_ended) { _ended = true; _creating--; } }
    }

    private delegate nint HookProc(int code, nint wparam, nint lparam);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int kind, HookProc procedure, nint module, uint thread);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wparam, nint lparam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
