using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace UltraExplorer.Picker.Integration;

internal static class DialogNative
{
    internal const uint ForegroundEvent = 3, ObjectShowEvent = 0x8002, ObjectDestroyEvent = 0x8001;
    internal const string LeaseProperty = "UltraExplorer.NativeDialogLease";
    internal static ushort ReserveLeasePropertyAtom()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var atom = GlobalAddAtom(LeaseProperty);
            if (atom != 0) return atom;
            if (Marshal.GetLastWin32Error() != 8) break;
            Thread.Sleep(50);
        }
        throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not reserve the dialog recovery marker.");
    }
    internal static void ReleaseLeasePropertyAtom(ushort atom) { if (atom != 0) GlobalDeleteAtom(atom); }
    internal delegate void EventCallback(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time);
    internal delegate bool EnumCallback(nint window, nint value);
    private const uint QueryProcess = 0x1000;
    private static readonly uint OwnIntegrity = Integrity(GetCurrentProcess());

    internal static string ClassName(nint window)
    {
        var text = new StringBuilder(256);
        GetClassName(window, text, text.Capacity);
        return text.ToString();
    }

    internal static string Title(nint window)
    {
        var text = new StringBuilder(2048);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    internal static uint ProcessId(nint window) { GetWindowThreadProcessId(window, out var id); return id; }

    internal static bool IsForegroundDialog(nint dialog)
    {
        var foreground = GetForegroundWindow();
        if (foreground == dialog) return true;
        if (ProcessId(foreground) != ProcessId(dialog)) return false;
        // UI Automation may materialise a native tooltip/drop-down. It is part
        // of the same dialog, not evidence the user switched applications.
        return ClassName(foreground) is "tooltips_class32" or "ComboLBox"
            && IsWindowVisible(dialog) && IsWindowEnabled(dialog);
    }

    internal static string? AccessibleApplication(nint window)
    {
        var process = OpenProcess(QueryProcess, false, ProcessId(window));
        if (process == 0) return null;
        try
        {
            var integrity = Integrity(process);
            if (integrity == 0 || integrity > OwnIntegrity) return null;
            var path = new StringBuilder(32768);
            var length = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : null;
        }
        finally { CloseHandle(process); }
    }

    internal static string? KnownFolderPath(Guid folder)
    {
        var result = SHGetKnownFolderPath(ref folder, 0, 0, out var pointer);
        if (result < 0 || pointer == 0) return null;
        try
        {
            var path = Marshal.PtrToStringUni(pointer);
            return path is not null && Directory.Exists(path) ? path : null;
        }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }

    private static uint Integrity(nint process)
    {
        if (!OpenProcessToken(process, 8, out var token)) return 0;
        try
        {
            GetTokenInformation(token, 25, 0, 0, out var size);
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!GetTokenInformation(token, 25, buffer, size, out _)) return 0;
                var sid = Marshal.ReadIntPtr(buffer);
                var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return count == 0 ? 0 : (uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)count - 1));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    internal static bool LooksLikeFileDialog(nint window)
    {
        if (!IsWindowVisible(window) || ClassName(window) != "#32770" || ProcessId(window) == Environment.ProcessId) return false;
        var shell = false;
        var name = false;
        var accept = false;
        EnumChildWindows(window, (child, _) =>
        {
            var className = ClassName(child);
            var id = GetDlgCtrlID(child);
            shell |= className is "SHELLDLL_DefView" or "NamespaceTreeControl";
            name |= className == "Edit" && (id is 1148 or 1152 or 1001);
            accept |= className == "Button" && id == 1;
            return true;
        }, 0);
        return shell && name && accept;
    }

    internal static nint FindChild(nint window, int id, string className)
    {
        nint found = 0;
        EnumChildWindows(window, (child, _) =>
        {
            if (GetDlgCtrlID(child) == id && ClassName(child) == className) found = child;
            return found == 0;
        }, 0);
        return found;
    }

    internal static nint FindVisibleChild(nint window, int id, string className)
    {
        nint found = 0;
        EnumChildWindows(window, (child, _) =>
        {
            if (GetDlgCtrlID(child) == id && ClassName(child) == className && IsWindowVisible(child)) found = child;
            return found == 0;
        }, 0);
        return found;
    }

    internal static bool IsCloaked(nint window) => DwmGetWindowAttribute(window, 14, out var flags, sizeof(int)) == 0 && flags != 0;
    internal static bool Hide(nint window)
    {
        var cloak = 1;
        // Cloaking preserves the dialog's UI Automation tree and its layout.
        // Unlike moving it offscreen, it cannot strand an overwrite prompt.
        if (DwmSetWindowAttribute(window, 13, ref cloak, sizeof(int)) == 0) return true;
        // Windows may refuse to cloak a window in another process. A layered
        // alpha-zero window retains its UIA tree and is transparent to hit tests.
        var style = GetWindowLongPtr(window, -20);
        SetWindowLongPtr(window, -20, style | 0x80000);
        return SetLayeredWindowAttributes(window, 0, 0, 2);
    }

    internal static bool IsHidden(nint window)
    {
        if (IsCloaked(window)) return true;
        return (GetWindowLongPtr(window, -20).ToInt64() & 0x80000) != 0
            && GetLayeredWindowAttributes(window, out _, out var alpha, out var flags) && (flags & 2) != 0 && alpha == 0;
    }
    internal static bool HasVisibleControlStyle(nint window) => IsWindow(window)
        && (GetWindowLongPtr(window, -16).ToInt64() & 0x10000000) != 0;
    internal static (bool Layered, uint Colour, byte Alpha, uint Flags) Transparency(nint window)
    {
        var layered = (GetWindowLongPtr(window, -20).ToInt64() & 0x80000) != 0;
        var known = GetLayeredWindowAttributes(window, out var colour, out var alpha, out var flags);
        return (layered, known ? colour : 0, known ? alpha : (byte)255, known ? flags : 2);
    }
    internal static void RestoreTransparency(nint window, bool layered, uint colour, byte alpha, uint flags)
    {
        var cloak = 0;
        DwmSetWindowAttribute(window, 13, ref cloak, sizeof(int));
        if (layered) SetLayeredWindowAttributes(window, colour, alpha, flags);
        else SetWindowLongPtr(window, -20, GetWindowLongPtr(window, -20) & ~((nint)0x80000));
    }
    /// <summary>False in a test copy (ULTRAEXPLORER_TEST_WINDOW=1): its windows,
    /// and the fixture dialogs it hands results back to, sit on a monitor nobody
    /// is working on and must never take the user's foreground or keyboard.</summary>
    internal static bool MayActivate { get; } = Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1";

    internal static void Restore(nint window, bool activate = false)
    {
        if (!IsWindow(window)) return;
        var cloak = 0;
        DwmSetWindowAttribute(window, 13, ref cloak, sizeof(int));
        ShowWindowAsync(window, 4); // SW_SHOWNOACTIVATE; retain the original location.
        if (activate && MayActivate) SetForegroundWindow(window);
    }

    /// <summary>
    /// Moves the keyboard from the (now transparent) original dialog to the
    /// replacement. The replacement's process was started in the background and
    /// Windows normally refuses it the foreground; the original dialog's thread
    /// owns it, so for the hand-over the two threads share input state for the
    /// one call, which is what makes SetForegroundWindow succeed. Nothing is
    /// done in a test copy.
    /// </summary>
    internal static bool BringForward(nint window, nint from)
    {
        if (!MayActivate || !IsWindow(window)) return false;
        var foreground = GetForegroundWindow();
        if (foreground == window) return true;
        // Only hand over from the dialog being replaced (or something its own
        // thread shows, a tooltip): never pull focus from another application
        // the user has gone to meanwhile, not even by asking Windows first.
        if (foreground == 0 || GetWindowThreadProcessId(foreground, out _) != GetWindowThreadProcessId(from, out _)) return false;
        if (SetForegroundWindow(window) && GetForegroundWindow() == window) return true;
        var target = GetWindowThreadProcessId(foreground, out _);
        var own = GetCurrentThreadId();
        if (target == 0 || target == own || !AttachThreadInput(own, target, true)) return false;
        try { SetForegroundWindow(window); }
        finally { AttachThreadInput(own, target, false); }
        return GetForegroundWindow() == window;
    }

    internal static NativeRect? WindowBounds(nint window) =>
        IsWindow(window) && GetWindowRect(window, out var rect) && rect.Width > 0 && rect.Height > 0 ? rect : null;

    /// <summary>The monitor a window is on (the nearest one), its work area and
    /// its scale against 96 DPI.</summary>
    internal static (NativeRect Work, bool Primary, double Scale)? MonitorOf(nint window)
    {
        var monitor = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return null;
        var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
        return (info.Work, (info.Flags & 1) != 0, scale);
    }

    internal static void Place(nint window, NativeRect bounds) =>
        SetWindowPos(window, 0, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0004 | 0x0010); // no z-order change, no activation

    /// <summary>The monitor nearest a rectangle, as <see cref="MonitorOf"/>; the primary one for null.</summary>
    internal static (NativeRect Work, bool Primary, double Scale)? MonitorAt(NativeRect? rectangle)
    {
        var area = rectangle ?? new NativeRect(0, 0, 1, 1);
        var monitor = MonitorFromRect(ref area, rectangle is null ? 1u : 2u);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return null;
        var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
        return (info.Work, (info.Flags & 1) != 0, scale);
    }

    internal static long Style(nint window) => GetWindowLongPtr(window, -16).ToInt64();

    /// <summary>DWM cloaking of one of this process's own windows: it stays
    /// shown, laid out and rendered, and is simply not composed on screen.</summary>
    internal static bool CloakOwn(nint window, bool cloaked)
    {
        var value = cloaked ? 1 : 0;
        return DwmSetWindowAttribute(window, 13, ref value, sizeof(int)) == 0;
    }

    /// <summary>Waits for the desktop compositor's next frame: what was drawn before is on screen after.</summary>
    internal static void WaitForComposition() { try { _ = DwmFlush(); } catch (DllNotFoundException) { } }

    /// <summary>WS_EX_NOACTIVATE on or off: a window that must not take the
    /// keyboard when shown, or clicked, until it is handed over on purpose.</summary>
    internal static void SetNoActivate(nint window, bool on) => SetExStyle(window, 0x08000000, on);

    internal static void SetExStyle(nint window, nint bits, bool on)
    {
        var style = GetWindowLongPtr(window, -20);
        var wanted = on ? style | bits : style & ~bits;
        if (wanted != style) SetWindowLongPtr(window, -20, wanted);
    }

    /// <summary>Whether <paramref name="upper"/> comes before <paramref name="lower"/> in the z-order (is drawn over it).</summary>
    internal static bool IsAbove(nint upper, nint lower)
    {
        // A bounded walk: GetWindow in a loop can come round to windows again
        // while other programs reorder theirs (the documented risk of calling
        // it so), and this runs on the picker's own thread. No desktop has
        // this many top-level windows (a few hundred is usual).
        var steps = 0;
        for (var window = GetWindow(upper, 2); window != 0 && steps++ < MaximumZOrderSteps; window = GetWindow(window, 2)) // GW_HWNDNEXT
            if (window == lower) return true;
        return false;
    }

    internal const int MaximumZOrderSteps = 65536;

    /// <summary>
    /// Directly over <paramref name="below"/>, the dialog a picker covers, in
    /// the z-order: placed after the window just above that dialog. HWND_TOP
    /// is not asked for: from a process that is not in the foreground Windows
    /// does not always honour it, and it left the picker under the dialog it
    /// was to cover (the provisional picker of 1 early case in 4 on the test
    /// machine; a probe with two windows of other threads saw HWND_TOP leave a
    /// window where it was while this placement moved it). After the last
    /// topmost window a window stays an ordinary one. Without activation (the
    /// dialog keeps the keyboard) and without the owner: raising an owned
    /// window raises its owner - the application's window - along with it
    /// unless told not to, which waits for that application's thread, busy
    /// with the dialog it has just shown, for a tenth of a second or two
    /// (measured 110-200 ms). Nothing moves when it is over the dialog
    /// already. False when it could not be put there (either window gone).
    /// </summary>
    internal static bool RaiseAbove(nint window, nint below)
    {
        // Again should another window come in between meanwhile.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!IsWindow(window) || !IsWindow(below)) return false;
            if (IsAbove(window, below)) return true;
            // GW_HWNDPREV (none: the dialog is first, and so is this window after HWND_TOP);
            // no move, no size, no activation, not the owner.
            SetWindowPos(window, GetWindow(below, 3), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);
        }
        return IsAbove(window, below);
    }

    internal static void Click(nint dialog, int id)
    {
        var button = FindChild(dialog, id, "Button");
        if (button == 0 || !PostMessage(dialog, 0x111, id, button))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The original dialog could not receive the action.");
    }

    /// <summary>
    /// The message the application raised over its dialog (a file-exists or
    /// sharing warning, its own validation) brought to the front. Only a
    /// visible dialog-class window the original owns: the tooltips and helper
    /// windows a file dialog keeps hidden stay exactly as they are.
    /// </summary>
    internal static void BringOwnedPromptForward(nint dialog)
    {
        if (!MayActivate || !IsWindow(dialog)) return;
        // Only from the dialog itself or from this process: never out of
        // another application the user has moved on to.
        var foreground = GetForegroundWindow();
        if (foreground != 0 && foreground != dialog && ProcessId(foreground) != Environment.ProcessId) return;
        var process = ProcessId(dialog);
        nint prompt = 0;
        EnumWindows((window, _) =>
        {
            if (GetWindow(window, 4) == dialog && IsWindowVisible(window) && ClassName(window) == "#32770"
                && ProcessId(window) == process) prompt = window;
            return prompt == 0;
        }, 0);
        if (prompt != 0 && GetForegroundWindow() != prompt) SetForegroundWindow(prompt);
    }

    internal static (string[] Labels, int Selected)? ReadCombo(nint combo, int maximum = 100)
    {
        if (combo == 0 || ClassName(combo) != "ComboBox") return null;
        var style = GetWindowLongPtr(combo, -16).ToInt64();
        if ((style & 0x30) != 0 && (style & 0x200) == 0) return null; // owner-drawn, without CBS_HASSTRINGS
        if (!TryMessage(combo, 0x146, 0, 0, out var count) || count < 0 || count > maximum) return null;
        if (count == 0) return ([], -1); // an empty list is read, not unreadable
        var labels = new string[(int)count];
        for (var i = 0; i < labels.Length; i++)
        {
            if (!TryMessage(combo, 0x149, i, 0, out var length) || length < 0 || length > 2048) return null;
            // CB_GETLBTEXT takes no buffer size: Windows copies the label as
            // the list has it when it answers, which a program refilling its
            // list can have made longer than the length just asked for. The
            // buffer holds the longest label read at all, and a label that
            // grew in between is not read.
            var text = new StringBuilder(2049);
            if (SendTextMessage(combo, 0x148, i, text, 2, 250, out var read) == 0 || read.ToInt64() < 0 || read.ToInt64() > length) return null;
            labels[i] = text.ToString();
        }
        if (!TryMessage(combo, 0x147, 0, 0, out var selected)) return null;
        return (labels, (int)selected);
    }

    internal static bool SelectCombo(nint combo, int index)
    {
        if (combo == 0 || ClassName(combo) != "ComboBox") return false;
        if (!TryMessage(combo, 0x147, 0, 0, out var current)) return false;
        if (current == index) return true;
        if (!TryMessage(combo, 0x14E, index, 0, out var selected) || selected != index) return false;
        // CB_SETCURSEL does not send CBN_SELCHANGE. Send it to the real parent
        // so the calling app receives OnTypeChange/OnItemSelected as normal.
        var parent = GetParent(combo);
        var id = GetDlgCtrlID(combo) & 0xffff;
        return PostMessage(parent, 0x111, id | (1 << 16), combo)
            && PostMessage(parent, 0x111, id | (9 << 16), combo); // CBN_SELENDOK commits an app-added choice.
    }

    internal static (bool Radio, int State)? ReadCheck(nint button)
    {
        if (button == 0 || ClassName(button) != "Button") return null;
        var type = GetWindowLongPtr(button, -16).ToInt64() & 0xf;
        if (type is not (2 or 3 or 4 or 5 or 6 or 9) || !TryMessage(button, 0xf0, 0, 0, out var state)) return null;
        return (type is 4 or 9, (int)state);
    }

    internal static void SetCheck(nint button, bool? value)
    {
        var wanted = value is null ? 2 : value.Value ? 1 : 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (ReadCheck(button) is not { } check) throw new IOException("The native checkbox is unavailable.");
            if (check.State == wanted) return;
            if (!TryMessage(button, 0xf5, 0, 0, out _)) throw new IOException("The native checkbox did not respond.");
        }
        if (ReadCheck(button)?.State != wanted) throw new IOException("The native checkbox did not accept its new state.");
    }

    internal static string? ReadEdit(nint edit)
    {
        if (edit == 0 || ClassName(edit) != "Edit") return null;
        var style = GetWindowLongPtr(edit, -16).ToInt64();
        if ((style & (0x20 | 0x4)) != 0) throw new NotSupportedException("A password or multiline field needs its original dialog.");
        var text = new StringBuilder(2049);
        if (SendTextMessage(edit, 0xd, text.Capacity, text, 2, 250, out var length) == 0)
            throw new IOException("The application's text option cannot be read.");
        if (length >= 2048)
        {
            // Filled: a longer text, such as a few dozen files handed back at
            // once ("a.jpg" "b.jpg" ...), read again in a buffer sized by the
            // field's own length. One character over it shows a text that
            // grew in between, and so may have been cut short.
            if (!TryMessage(edit, 0xe, 0, 0, out var longer) || longer is < 0 or > LongestEditText)
                throw new IOException("The application's text option cannot be read.");
            text = new StringBuilder((int)longer + 2);
            if (SendTextMessage(edit, 0xd, text.Capacity, text, 2, 250, out length) == 0 || length >= text.Capacity - 1)
                throw new IOException("The application's text option cannot be read.");
        }
        return text.ToString();
    }

    /// <summary>The longest text read from a field: the names of far more
    /// files than one selection hands back, and never a buffer as large as
    /// whatever length an application's field claims.</summary>
    private const int LongestEditText = 1 << 20;

    internal static void SetEdit(nint edit, string text)
    {
        if (SendInputMessage(edit, 0xc, 0, text, 2, 250, out var accepted) == 0 || accepted == 0 || ReadEdit(edit) != text)
            throw new IOException("The application's text option did not accept its new value.");
    }

    internal static void SetFileNameEdit(nint edit, string text)
    {
        var parent = GetParent(edit);
        if (ClassName(parent) != "ComboBox") { SetEdit(edit, text); return; }
        if (SendInputMessage(parent, 0xc, 0, text, 2, 250, out var accepted) == 0 || accepted == 0 || ReadEdit(edit) != text)
            throw new IOException("The application's filename control did not accept the selected name.");
        // Writing the inner edit does not necessarily invalidate the common
        // dialog's cached filename after a type change. Commit the combo edit
        // through its normal CBN_EDITCHANGE notification before native OK.
        if (!TryMessage(GetParent(parent), 0x111, (GetDlgCtrlID(parent) & 0xffff) | (5 << 16), parent, out _))
            throw new IOException("The application's filename change did not respond.");
    }

    private static bool TryMessage(nint window, uint message, nint wparam, nint lparam, out long result)
    {
        var sent = SendMessageTimeout(window, message, wparam, lparam, 2, 250, out var value);
        result = value.ToInt64();
        return sent != 0;
    }

    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint window, uint colour, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(nint window, out uint colour, out byte alpha, out uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeout(nint window, uint message, nint wparam, nint lparam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)] private static extern nint SendTextMessage(nint window, uint message, nint wparam, StringBuilder text, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern nint SendInputMessage(nint window, uint message, nint wparam, string text, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumCallback callback, nint value);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(nint window, EnumCallback callback, nint value);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder name, int maximum);
    [DllImport("user32.dll")] internal static extern int GetDlgCtrlID(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ShowWindowAsync(nint window, int show);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool join);
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Bounds, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromRect(ref NativeRect rectangle, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint window, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll", EntryPoint = "SetPropW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool SetProp(nint window, string name, nint value);
    [DllImport("user32.dll", EntryPoint = "GetPropW", ExactSpelling = true, CharSet = CharSet.Unicode)] internal static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll", EntryPoint = "RemovePropW", ExactSpelling = true, CharSet = CharSet.Unicode)] internal static extern nint RemoveProp(nint window, string name);
    [DllImport("kernel32.dll", EntryPoint = "GlobalAddAtomW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort GlobalAddAtom(string name);
    [DllImport("kernel32.dll", EntryPoint = "GlobalDeleteAtom", ExactSpelling = true)] private static extern ushort GlobalDeleteAtom(ushort atom);
    [DllImport("shell32.dll", EntryPoint = "SHGetKnownFolderPath", ExactSpelling = true)] private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, nint token, out nint path);
    [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint minimum, uint maximum, nint module, EventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref int length);
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(nint token, int kind, nint buffer, uint length, out uint needed);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthority(nint sid, uint index);
}
