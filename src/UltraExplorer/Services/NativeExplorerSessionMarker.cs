using System.Diagnostics;
using System.Runtime.InteropServices;
using UltraExplorer.Picker.Integration;

namespace UltraExplorer.Services;

/// <summary>An explicit native browsing intent belongs to one HWND lifetime.
/// Windows removes its properties when the window is destroyed.</summary>
internal static class NativeExplorerSessionMarker
{
    private static string Name => "UltraExplorer.NativeSession." + DialogIntegrationStore.InstanceKey;

    internal static bool Mark(nint window, uint processId, DateTime startedUtc)
        => Matches(window, processId, startedUtc) && SetProp(window, Name, Stamp(startedUtc));

    internal static bool IsMarked(nint window, uint processId, DateTime startedUtc)
        => Matches(window, processId, startedUtc) && GetProp(window, Name) == Stamp(startedUtc);

    private static nint Stamp(DateTime time) => checked((nint)time.ToUniversalTime().ToFileTimeUtc());

    private static bool Matches(nint window, uint processId, DateTime startedUtc)
    {
        if (window == 0 || processId == 0 || !DialogNative.IsWindow(window)
            || DialogNative.ProcessId(window) != processId) return false;
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return !process.HasExited && process.StartTime.ToUniversalTime() == startedUtc.ToUniversalTime();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or OverflowException) { return false; }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetProp(nint window, string name, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetProp(nint window, string name);
}
