using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Com;
using UltraExplorer.Picker.Integration;
using ViewAllSmoke;

namespace NativeDialogProxySmoke;

/// <summary>A modern Save dialog with an application's own Win32 child dialog
/// under its FloatNotifySink. REAPER uses this topology instead of putting its
/// controls in IFileDialogCustomize's AppControlsModuleInner subtree. Everything
/// here is created by this disposable fixture on its own guarded STA.</summary>
internal static class ReaperSaveOptionsFixture
{
    internal const string CreateLabel = "Create subdirectory for project";
    internal const string CopyLabel = "Copy all media into project directory, using:";
    internal const string ConvertLabel = "Convert media";
    internal const string TrimLabel = "Trim media, tail size:";
    internal const string MoveLabel = "Move all media into project directory";
    internal const string CopyRatherLabel = "Copy rather than move source media if not in old project media path";
    internal const string FormatTitle = "REAPER format fixture";
    private const int CreateId = 1042, CopyId = 1043, ConvertId = 1057, TrimId = 1058,
        MoveId = 1044, CopyRatherId = 1045, FormatId = 1056, TailId = 1577;
    private const string Anchor = "UltraExplorer REAPER fixture anchor\n \n \n ";

    internal static bool Handles(string[] args)
    {
        var index = Array.IndexOf(args, "--native-dialog-fixture");
        return index >= 0 && index + 3 < args.Length && args[index + 3] is "reaper-save-options" or "reaper-save-format";
    }

    internal static int Run(string[] args)
    {
        var index = Array.IndexOf(args, "--native-dialog-fixture");
        var root = args[index + 1];
        var output = args[index + 2];
        var mode = args[index + 3];
        object? instance = null;
        try
        {
            using var guard = new FixtureWindowGuard(output + ".fixture.log", keepDialogsInside: true);
            instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B"), true)!)!;
            var dialog = (IFileDialog)instance;
            dialog.GetOptions(out var flags);
            dialog.SetOptions(flags | (uint)(FileDialogOptions.ForceFileSystem | FileDialogOptions.PathMustExist));
            dialog.SetTitle("UltraExplorer integration fixture " + mode);
            dialog.SetFileTypes(1, [new ComDlgFilterSpec { Name = "REAPER Project files (*.RPP)", Spec = "*.RPP" }]);
            dialog.SetFileTypeIndex(1);
            dialog.SetDefaultExtension("rpp");
            dialog.SetFileName("export");
            var shellId = ShellNative.ShellItemId;
            var folder = ShellNative.SHCreateItemFromParsingName(root, 0, ref shellId);
            try { dialog.SetFolder(folder); }
            finally { Marshal.ReleaseComObject(folder); }
            // Reserve native layout space, then replace this text with the
            // empty Static sibling that the real REAPER dialog also contains.
            ((INativeFileDialogCustomize)instance).AddText(6100, Anchor);
            using var panel = new OptionsPanel(instance, mode == "reaper-save-format", output);
            var events = new SaveEvents(panel);
            dialog.Advise(events, out var cookie);
            File.WriteAllText(output + ".ready", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            if (Environment.GetEnvironmentVariable("NATIVE_DIALOG_FIXTURE_SHOW_SIGNAL") is { Length: > 0 } signal)
            {
                using var show = EventWaitHandle.OpenExisting(signal);
                if (!show.WaitOne(TimeSpan.FromMinutes(2))) throw new TimeoutException("The runner never asked for the dialog.");
            }
            var result = dialog.Show(guard.Owner);
            dialog.Unadvise(cookie);
            var paths = new List<string>();
            if (result == 0)
            {
                dialog.GetResult(out var item);
                try
                {
                    item.GetDisplayName(ShellItemDisplayName.FileSystemPath, out var name);
                    try { paths.Add(Marshal.PtrToStringUni(name) ?? ""); }
                    finally { Marshal.FreeCoTaskMem(name); }
                }
                finally { Marshal.ReleaseComObject(item); }
            }
            dialog.GetFileTypeIndex(out var typeIndex);
            File.WriteAllText(output, JsonSerializer.Serialize(new { result, paths, typeIndex, onAccept = events.Accepted,
                panelReady = panel.Ready, panel.Error, panel.Commands, panel.FormatClicks, processId = Environment.ProcessId, producer = mode }));
            return panel.Error is null ? 0 : 2;
        }
        catch (Exception ex) { File.WriteAllText(output, JsonSerializer.Serialize(new { error = ex.ToString() })); return 2; }
        finally { if (instance is not null) Marshal.ReleaseComObject(instance); }
    }

    private sealed class OptionsPanel : IDisposable
    {
        private readonly object _dialog;
        private readonly bool _format;
        private readonly string _output;
        private readonly TimerProc _tick;
        private readonly DialogProc _procedure;
        private readonly nuint _timer;
        private nint _panel;
        private nint _template;
        public bool Ready { get; private set; }
        public string? Error { get; private set; }
        public int FormatClicks { get; private set; }
        public List<string> Commands { get; } = [];

        public OptionsPanel(object dialog, bool format, string output)
        {
            _dialog = dialog; _format = format; _output = output;
            _tick = Tick; _procedure = Procedure;
            _timer = SetTimer(0, 0, 10, _tick);
            if (_timer == 0) throw new IOException("The fixture's panel timer could not be created.");
        }

        private void Tick(nint window, uint message, nuint timer, uint time)
        {
            if (Ready || Error is not null) return;
            try
            {
                if (((IOleWindow)_dialog).GetWindow(out var native) != 0 || native == 0) return;
                nint anchor = 0;
                DialogNative.EnumChildWindows(native, (child, _) =>
                {
                    if (DialogNative.ClassName(child) == "Static" && DialogNative.Title(child) == Anchor) anchor = child;
                    return true;
                }, 0);
                if (anchor == 0) return;
                var host = GetParent(anchor);
                // A native child #32770 directly under FloatNotifySink, beside
                // the blank Static, reproduces the observed REAPER hierarchy.
                if (DialogNative.ClassName(host) != "FloatNotifySink")
                    throw new InvalidOperationException("The fixture's app-controls host is not FloatNotifySink: " + DialogNative.ClassName(host));
                var bytes = Template(_format);
                _template = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, _template, bytes.Length);
                _panel = CreateDialogIndirectParam(0, _template, host, _procedure, 0);
                if (_panel == 0) throw new IOException("The fixture's Win32 options panel could not be created: " + Marshal.GetLastWin32Error());
                GetWindowRect(anchor, out var box);
                var point = new Point { X = box.Left, Y = box.Top };
                ScreenToClient(host, ref point);
                SetWindowPos(_panel, 0, point.X, point.Y, Math.Max(960, box.Right - box.Left), Math.Max(100, box.Bottom - box.Top), 0x0010 | 0x0004);
                SetWindowText(anchor, " ");
                // The public test signal is written only after the exact
                // native controls are present, on the dialog's own thread.
                Ready = true;
                File.WriteAllText(_output + ".panel-ready", _panel.ToString("X"));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Error = ex.ToString();
                File.WriteAllText(_output + ".panel-error", Error);
            }
        }

        private nint Procedure(nint window, uint message, nint wparam, nint lparam)
        {
            try
            {
                if (message == 0x0110) { _panel = window; UpdateDependencies(); return 1; }
                if (message != 0x0111) return 0;
                var id = (int)(wparam.ToInt64() & 0xffff);
                var notification = (int)((wparam.ToInt64() >> 16) & 0xffff);
                if (notification == 0 && id is CreateId or CopyId or ConvertId or TrimId or MoveId or CopyRatherId)
                {
                    Commands.Add(id.ToString(CultureInfo.InvariantCulture));
                    if (id == CopyId && Checked(CopyId)) Check(MoveId, false);
                    if (id == MoveId && Checked(MoveId)) Check(CopyId, false);
                    UpdateDependencies();
                    return 1;
                }
                if (id == TailId && notification == 0x0300) { Commands.Add("tail-change"); return 1; }
                if (id == FormatId && notification == 0)
                {
                    FormatClicks++;
                    Commands.Add("format");
                    // A real nested native dialog: the adapter must return
                    // keyboard access to it while the app's action is pending.
                    MessageBox(window, "This is the fixture's media format dialog.", FormatTitle, 1);
                    return 1;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Error = ex.ToString(); }
            return 0;
        }

        private void UpdateDependencies()
        {
            var copy = Checked(CopyId);
            foreach (var id in new[] { ConvertId, TrimId, TailId, FormatId })
                if (GetDlgItem(_panel, id) is var control && control != 0) EnableWindow(control, copy);
            EnableWindow(GetDlgItem(_panel, CopyRatherId), Checked(MoveId));
        }
        private bool Checked(int id) => SendMessage(GetDlgItem(_panel, id), 0x00f0, 0, 0) == 1;
        private void Check(int id, bool value) => SendMessage(GetDlgItem(_panel, id), 0x00f1, value ? 1 : 0, 0);

        public object Read() => new { create = Checked(CreateId), copy = Checked(CopyId), convert = Checked(ConvertId),
            trim = Checked(TrimId), move = Checked(MoveId), copyRather = Checked(CopyRatherId),
            tail = DialogNative.ReadEdit(GetDlgItem(_panel, TailId)), FormatClicks, commandCount = Commands.Count };

        public void Dispose()
        {
            if (_timer != 0) KillTimer(0, _timer);
            if (_panel != 0 && DialogNative.IsWindow(_panel)) DestroyWindow(_panel);
            if (_template != 0) Marshal.FreeHGlobal(_template);
            GC.KeepAlive(_tick); GC.KeepAlive(_procedure);
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class SaveEvents(OptionsPanel panel) : IFileDialogEvents
    {
        public object? Accepted { get; private set; }
        public int OnFileOk(IFileDialog dialog) { Accepted = panel.Read(); return 0; }
        public int OnFolderChanging(IFileDialog dialog, IShellItem folder) => 0;
        public int OnFolderChange(IFileDialog dialog) => 0;
        public int OnSelectionChange(IFileDialog dialog) => 0;
        public int OnShareViolation(IFileDialog dialog, IShellItem item, out ShareViolationResponse response) { response = ShareViolationResponse.Default; return 0; }
        public int OnTypeChange(IFileDialog dialog) => 0;
        public int OnOverwrite(IFileDialog dialog, IShellItem item, out OverwriteResponse response) { response = OverwriteResponse.Default; return 0; }
    }

    /// <summary>A standard DLGTEMPLATE, as an application embeds in its own
    /// common dialog. Win32 creates and processes the checkbox/edit/button
    /// HWNDs; no proxy or fake UI Automation provider is used by this fixture.</summary>
    private static byte[] Template(bool format)
    {
        const uint ChildVisible = 0x50000000, TabStop = 0x00010000;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode);
        writer.Write(ChildVisible | 0x04000000u | 0x0400u | 0x0004u | 0x0040u); // CLIPSIBLINGS, CONTROL, 3DLOOK, SETFONT
        writer.Write(0u);
        writer.Write((ushort)(format ? 9 : 8));
        foreach (short coordinate in new short[] { 0, 0, 470, 58 }) writer.Write(coordinate);
        writer.Write((ushort)0); writer.Write((ushort)0); Text("");
        writer.Write((ushort)9); Text("Segoe UI");
        Item(CreateId, 0x80, CreateLabel, 4, 4, 210, 12, ChildVisible | TabStop | 3);
        Item(CopyId, 0x80, CopyLabel, 4, 21, 220, 12, ChildVisible | TabStop | 3);
        Item(ConvertId, 0x80, ConvertLabel, 228, 21, 72, 12, ChildVisible | TabStop | 3);
        if (format) Item(FormatId, 0x80, "Format...", 302, 20, 48, 14, ChildVisible | TabStop);
        Item(TrimId, 0x80, TrimLabel, 354, 21, 92, 12, ChildVisible | TabStop | 3);
        Item(TailId, 0x81, "500", 447, 20, 28, 14, ChildVisible | TabStop | 0x00800000u | 0x2000u); // BORDER, NUMBER
        Item(1578, 0x82, "ms", 478, 21, 15, 12, ChildVisible);
        Item(MoveId, 0x80, MoveLabel, 4, 38, 218, 12, ChildVisible | TabStop | 3);
        Item(CopyRatherId, 0x80, CopyRatherLabel, 228, 38, 300, 12, ChildVisible | TabStop | 3);
        return stream.ToArray();

        void Text(string value) { writer.Write(Encoding.Unicode.GetBytes(value)); writer.Write((ushort)0); }
        void Item(int id, ushort kind, string text, short x, short y, short width, short height, uint style)
        {
            while (stream.Position % 4 != 0) writer.Write((byte)0);
            writer.Write(style); writer.Write(0u);
            foreach (var coordinate in new[] { x, y, width, height }) writer.Write(coordinate);
            writer.Write((ushort)id); writer.Write((ushort)0xffff); writer.Write(kind); Text(text); writer.Write((ushort)0);
        }
    }

    [ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleWindow { [PreserveSig] int GetWindow(out nint window); [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enter); }
    private delegate void TimerProc(nint window, uint message, nuint timer, uint tick);
    private delegate nint DialogProc(nint window, uint message, nint wparam, nint lparam);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern nuint SetTimer(nint window, nuint id, uint milliseconds, TimerProc callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint window, nuint id);
    [DllImport("user32.dll", EntryPoint = "CreateDialogIndirectParamW", SetLastError = true)] private static extern nint CreateDialogIndirectParam(nint instance, nint template, nint parent, DialogProc procedure, nint parameter);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint window, int id);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern bool EnableWindow(nint window, bool enabled);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect box);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint window, ref Point point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(nint window, string text);
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)] private static extern int MessageBox(nint owner, string text, string caption, uint type);
}
