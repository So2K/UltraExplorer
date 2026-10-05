// Diagnostic dependency seams: never load the product UI, edit registry,
// install hooks or address any HWND. Calls into native behavior are forbidden.
namespace UltraExplorer { internal sealed class App { } }
namespace UltraExplorer.Services
{
    internal static class ShellReplacementRegistration
    {
        internal static Action<bool>? OnReconcile;
        internal static bool Applied;
        internal static void Reconcile(bool enabled) { OnReconcile?.Invoke(enabled); Applied = enabled; }
    }
}
namespace UltraExplorer.Picker.Integration
{
    internal static class WinEShortcutStartup
    {
        internal static bool Applied;
        internal static void Reconcile(bool enabled) => Applied = enabled;
    }
    internal static class DialogNative
    {
        internal static bool MayActivate => false;
        internal static string LeaseProperty => "UltraExplorer.ForbiddenProbeWindow";
        private static Exception Forbidden() => new InvalidOperationException("This probe cannot operate any native window.");
        internal static (bool Layered, uint Colour, byte Alpha, uint Flags) Transparency(nint window) => throw Forbidden();
        internal static bool IsWindow(nint window) => throw Forbidden();
        internal static bool IsWindowEnabled(nint window) => throw Forbidden();
        internal static bool IsHidden(nint window) => throw Forbidden();
        internal static uint ProcessId(nint window) => throw Forbidden();
        internal static nint GetProp(nint window, string name) => throw Forbidden();
        internal static nint RemoveProp(nint window, string name) => throw Forbidden();
        internal static bool SetProp(nint window, string name, nint value) => throw Forbidden();
        internal static void Restore(nint window, bool activate = false) => throw Forbidden();
        internal static void RestoreTransparency(nint window, bool layered, uint colour, byte alpha, uint flags) => throw Forbidden();
        internal static void BringOwnedPromptForward(nint window) => throw Forbidden();
    }
}
