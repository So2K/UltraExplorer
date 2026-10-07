using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace UltraExplorer;

/// <summary>
/// Returns an active modeless preview to its owner before Win32 destroys its
/// HWND. Destroying an active window otherwise chooses the next ALT+ESC window,
/// which can put the explorer behind another application.
/// </summary>
internal static class PreviewOwnerActivation
{
    internal static void Attach(Window preview, Window owner, Func<bool> ownerClosing,
        Func<nint>? foregroundForChecks = null, Action? activateForChecks = null)
    {
        // Attach after the preview's own save/cancel handler. A close which is
        // still saving a note must neither change activation nor lose its buffer.
        preview.Closing += Closing;
        preview.Closed += Closed;

        void Closing(object? sender, CancelEventArgs args)
        {
            if (args.Cancel || ownerClosing() || !ReferenceEquals(preview.Owner, owner)
                || !owner.IsVisible || !owner.IsEnabled || owner.WindowState == WindowState.Minimized) return;
            var ownerHandle = new WindowInteropHelper(owner).Handle;
            var previewHandle = new WindowInteropHelper(preview).Handle;
            if (ownerHandle == 0 || previewHandle == 0 || !IsWindowVisible(ownerHandle) || IsIconic(ownerHandle)) return;

            // GA_ROOT also recognizes a focused embedded F3D/mpv child. Do not
            // take focus if the user has switched to a different application.
            var foreground = foregroundForChecks?.Invoke() ?? GetForegroundWindow();
            if (foreground == 0 || GetAncestor(foreground, 2) != previewHandle) return;
            if (activateForChecks is not null) activateForChecks();
            else owner.Activate();
        }

        void Closed(object? sender, EventArgs args)
        {
            preview.Closing -= Closing;
            preview.Closed -= Closed;
        }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
}
