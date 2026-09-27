using System.Runtime.InteropServices;

namespace UltraExplorer.Services;

/// <summary>
/// Dark native menus - the Shell's context menus, their submenus and
/// whatever the installed extensions add to them - so they sit in the app's
/// colours instead of standing out white.  Windows has no documented switch
/// for this; Explorer and Notepad++ use three exports of uxtheme.dll known
/// only by ordinal since Windows 10 1903 (build 18362):
/// SetPreferredAppMode (135), FlushMenuThemes (136) and
/// AllowDarkModeForWindow (133).  The app is always dark, so its preferred
/// mode is ForceDark, whatever the system's own apps use.
///
/// Asked for once, before any menu is made.  An older Windows, a missing
/// export or a failure leaves the menus as Windows draws them - never a crash.
/// </summary>
internal static class DarkMenus
{
    /// <summary>The first build whose ordinal 135 is SetPreferredAppMode (before it, the same ordinal took a flag).</summary>
    public const int FirstBuild = 18362;

    private const int ForceDark = 2;
    private const uint LoadLibrarySearchSystem32 = 0x00000800;

    private static readonly object Gate = new();
    private static int _state;
    private static IntPtr _allowForWindow;
    private static IntPtr _flushMenuThemes;

    /// <summary>Whether this process asked for dark menus and Windows took the request.</summary>
    public static bool IsOn => Volatile.Read(ref _state) == 1;

    /// <summary>
    /// Asks Windows for dark menus in this process: true when it could.
    /// Only the first call does anything; the rest say what it found.
    /// </summary>
    public static bool UseForProcess()
    {
        lock (Gate)
        {
            if (_state != 0)
            {
                return _state == 1;
            }

            _state = -1;
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, FirstBuild))
            {
                return false;
            }

            try
            {
                var uxtheme = LoadLibraryExW("uxtheme.dll", IntPtr.Zero, LoadLibrarySearchSystem32);
                if (uxtheme == IntPtr.Zero)
                {
                    return false;
                }

                var setPreferredAppMode = GetProcAddress(uxtheme, new IntPtr(135));
                _flushMenuThemes = GetProcAddress(uxtheme, new IntPtr(136));
                _allowForWindow = GetProcAddress(uxtheme, new IntPtr(133));
                if (setPreferredAppMode == IntPtr.Zero || _flushMenuThemes == IntPtr.Zero)
                {
                    return false;
                }

                unsafe
                {
                    ((delegate* unmanaged[Stdcall]<int, int>)setPreferredAppMode)(ForceDark);
                    ((delegate* unmanaged[Stdcall]<void>)_flushMenuThemes)();
                }

                _state = 1;
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException or ExternalException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Lets a window's menus be dark: the one that owns the context menus.
    /// Does nothing unless <see cref="UseForProcess"/> worked, or for no window.
    /// </summary>
    public static void AllowForWindow(IntPtr window)
    {
        if (!IsOn || window == IntPtr.Zero || _allowForWindow == IntPtr.Zero)
        {
            return;
        }

        try
        {
            unsafe
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)_allowForWindow)(window, 1);
                ((delegate* unmanaged[Stdcall]<void>)_flushMenuThemes)();
            }
        }
        catch (Exception ex) when (ex is SEHException or ExternalException)
        {
            // The window keeps the menus Windows gives it.
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);
}
