using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Interop;
using ViewAllSmoke;
using Forms = System.Windows.Forms;

namespace NativeDialogProxySmoke;

/// <summary>
/// The fixture's other producers: the ways real programs show the same Windows
/// dialogs, beside the COM calls of <see cref="NativeDialogFixture"/> - WinForms'
/// OpenFileDialog, SaveFileDialog and FolderBrowserDialog, WPF's
/// Microsoft.Win32 dialogs, the Win32 GetOpenFileNameW / GetSaveFileNameW
/// functions, and the legacy SHBrowseForFolder tree. Each shows its dialog on
/// this thread, owned by the same invisible window on a secondary monitor and
/// under the same activation guard, and writes what the program received in
/// the same shape: result (0, or the cancellation HRESULT), paths, typeIndex.
/// </summary>
internal static class DialogProducers
{
    private const int Cancelled = unchecked((int)0x800704C7);

    public static bool Handles(string[] args)
    {
        var index = Array.IndexOf(args, "--native-dialog-fixture");
        if (index < 0 || index + 3 >= args.Length) return false;
        var mode = args[index + 3];
        return mode.StartsWith("winforms-", StringComparison.Ordinal) || mode.StartsWith("wpf-", StringComparison.Ordinal)
            || mode.StartsWith("win32-", StringComparison.Ordinal) || mode == "shbrowse";
    }

    public static int Run(string[] args)
    {
        var index = Array.IndexOf(args, "--native-dialog-fixture");
        var root = args[index + 1];
        var output = args[index + 2];
        var mode = args[index + 3];
        try
        {
            // As the program is from its start: WinForms' ApplicationConfiguration
            // and WPF both make the process system-DPI-aware before its first
            // window. Before the guard measures the monitors and makes its owner,
            // so all of them are measured one way: WPF switching an unaware
            // process at its first window re-measured the owner already made,
            // and its dialog opened partly on the primary monitor.
            if (mode.StartsWith("winforms-", StringComparison.Ordinal))
            {
                Forms.Application.SetHighDpiMode(Forms.HighDpiMode.SystemAware);
                Forms.Application.EnableVisualStyles();
            }
            else if (mode.StartsWith("wpf-", StringComparison.Ordinal) && !SetProcessDpiAwarenessContext(-2))
                throw new InvalidOperationException($"The process could not be made system-DPI-aware ({Marshal.GetLastWin32Error()}).");
            using var guard = new FixtureWindowGuard(output + ".fixture.log", keepDialogsInside: true);
            File.WriteAllText(output + ".ready", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            if (Environment.GetEnvironmentVariable("NATIVE_DIALOG_FIXTURE_SHOW_SIGNAL") is { Length: > 0 } signal)
            {
                using var show = EventWaitHandle.OpenExisting(signal);
                if (!show.WaitOne(TimeSpan.FromMinutes(2))) throw new TimeoutException("The runner never asked for the dialog.");
            }
            var title = "UltraExplorer integration fixture " + mode;
            var (result, paths, typeIndex) = mode switch
            {
                "winforms-folder" => WinFormsFolder(root, guard.Owner),
                _ when mode.StartsWith("winforms-save", StringComparison.Ordinal) => WinFormsSave(mode, root, title, guard.Owner),
                _ when mode.StartsWith("winforms-", StringComparison.Ordinal) => WinFormsOpen(mode, root, title, guard.Owner),
                _ when mode.StartsWith("wpf-", StringComparison.Ordinal) => Wpf(mode, root, title, guard.Owner),
                _ when mode.StartsWith("win32-", StringComparison.Ordinal) => Win32(mode, root, title, guard.Owner),
                "shbrowse" => BrowseForFolder(root, guard.Owner),
                _ => throw new ArgumentException("Unknown producer " + mode)
            };
            File.WriteAllText(output, JsonSerializer.Serialize(new { result, paths, typeIndex, processId = Environment.ProcessId, producer = mode }));
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(output, JsonSerializer.Serialize(new { error = ex.ToString() })); return 2; }
    }

    // ---- WinForms (System.Windows.Forms), the upgraded (Vista) dialogs ----

    private sealed class Owner(nint handle) : Forms.IWin32Window
    {
        public nint Handle => handle;
    }

    private static (int, string[], int) WinFormsOpen(string mode, string root, string title, nint owner)
    {
        using var dialog = new Forms.OpenFileDialog
        {
            Title = title, AutoUpgradeEnabled = true, Multiselect = mode == "winforms-open-multi",
            InitialDirectory = mode == "winforms-open-initialdir" ? Path.Combine(root, "Initial folder") : root
        };
        // "winforms-open": no Filter at all, so no file-type list either.
        if (mode == "winforms-open-filter")
        {
            dialog.Filter = "Text documents (*.txt)|*.txt|Images (*.png;*.jpg)|*.png;*.jpg|All files (*.*)|*.*";
            dialog.FilterIndex = 2;
        }
        else if (mode != "winforms-open") dialog.Filter = "Text documents (*.txt)|*.txt|All files (*.*)|*.*";
        return dialog.ShowDialog(new Owner(owner)) == Forms.DialogResult.OK
            ? (0, dialog.FileNames, dialog.FilterIndex) : (Cancelled, [], dialog.FilterIndex);
    }

    private static (int, string[], int) WinFormsSave(string mode, string root, string title, nint owner)
    {
        using var dialog = new Forms.SaveFileDialog
        {
            Title = title, AutoUpgradeEnabled = true, InitialDirectory = root, FileName = "export",
            AddExtension = true, OverwritePrompt = true
        };
        if (mode == "winforms-save-overwrite")
        {
            dialog.Filter = "Text documents (*.txt)|*.txt|All files (*.*)|*.*";
            dialog.DefaultExt = "txt";
        }
        // "winforms-save": no Filter, only a default extension to add.
        else dialog.DefaultExt = "log";
        return dialog.ShowDialog(new Owner(owner)) == Forms.DialogResult.OK
            ? (0, [dialog.FileName], dialog.FilterIndex) : (Cancelled, [], dialog.FilterIndex);
    }

    private static (int, string[], int) WinFormsFolder(string root, nint owner)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "UltraExplorer integration fixture: choose the export folder", UseDescriptionForTitle = true,
            AutoUpgradeEnabled = true, InitialDirectory = root, ShowNewFolderButton = true
        };
        return dialog.ShowDialog(new Owner(owner)) == Forms.DialogResult.OK ? (0, [dialog.SelectedPath], 0) : (Cancelled, [], 0);
    }

    // ---- WPF (Microsoft.Win32, the common item dialog since .NET 8) ----

    private static (int, string[], int) Wpf(string mode, string root, string title, nint owner)
    {
        // WPF's dialogs take a Window for their owner: an unshown one, owned
        // by the fixture's owner and where it is, so the dialog opens there.
        var window = new System.Windows.Window
        {
            Title = "UltraExplorer fixture WPF owner", WindowStyle = System.Windows.WindowStyle.None,
            ShowInTaskbar = false, ShowActivated = false, ResizeMode = System.Windows.ResizeMode.NoResize
        };
        var helper = new WindowInteropHelper(window) { Owner = owner };
        var handle = helper.EnsureHandle();
        GetWindowRect(owner, out var box);
        SetWindowPos(handle, 0, box.Left, box.Top, box.Right - box.Left, box.Bottom - box.Top, 0x0010 | 0x0004 | 0x0200); // NOACTIVATE | NOZORDER | NOOWNERZORDER
        try
        {
            switch (mode)
            {
                case "wpf-open":
                {
                    var dialog = new Microsoft.Win32.OpenFileDialog
                    { Title = title, InitialDirectory = root, Filter = "Text documents (*.txt)|*.txt|All files (*.*)|*.*" };
                    return dialog.ShowDialog(window) == true ? (0, dialog.FileNames, dialog.FilterIndex) : (Cancelled, [], dialog.FilterIndex);
                }
                case "wpf-save":
                {
                    var dialog = new Microsoft.Win32.SaveFileDialog
                    {
                        Title = title, InitialDirectory = root, FileName = "export", AddExtension = true, OverwritePrompt = true,
                        Filter = "Text documents (*.txt)|*.txt|CSV (comma delimited) (*.csv)|*.csv", FilterIndex = 2, DefaultExt = ".csv"
                    };
                    return dialog.ShowDialog(window) == true ? (0, [dialog.FileName], dialog.FilterIndex) : (Cancelled, [], dialog.FilterIndex);
                }
                case "wpf-folder":
                {
                    var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title, InitialDirectory = root };
                    return dialog.ShowDialog(window) == true ? (0, [dialog.FolderName], 0) : (Cancelled, [], 0);
                }
                default: throw new ArgumentException("Unknown WPF producer " + mode);
            }
        }
        finally { window.Close(); }
    }

    // ---- Win32 GetOpenFileNameW / GetSaveFileNameW, OFN_EXPLORER, no hook or template ----

    /// <summary>Ten types with long Russian labels, as a Russian program's filter string gives them.</summary>
    private static readonly (string Label, string Pattern)[] RussianTypes =
    [
        ("Текстовые документы (*.txt)", "*.txt"),
        ("Изображения PNG (*.png)", "*.png"),
        ("Изображения JPEG — фотографии с камеры и из интернета (*.jpg;*.jpeg)", "*.jpg;*.jpeg"),
        ("Документы Microsoft Word, включая старые версии формата (*.doc;*.docx)", "*.doc;*.docx"),
        ("Электронные таблицы Microsoft Excel (*.xls;*.xlsx)", "*.xls;*.xlsx"),
        ("Архивы ZIP (*.zip)", "*.zip"),
        ("Веб-страницы (*.htm;*.html)", "*.htm;*.html"),
        ("Файлы журнала (*.log)", "*.log"),
        ("Данные, разделённые запятыми (*.csv)", "*.csv"),
        ("Все файлы (*.*)", "*.*")
    ];

    private static (int, string[], int) Win32(string mode, string root, string title, nint owner)
    {
        const int Explorer = 0x00080000, FileMustExist = 0x1000, PathMustExist = 0x800, HideReadOnly = 0x4,
            NoChangeDir = 0x8, AllowMultiSelect = 0x200, OverwritePrompt = 0x2, EnableHook = 0x20;
        const int capacity = 32768;
        // "win32-hook": a hook procedure, as programs with a preview pane or
        // extra controls of their own have; Windows then shows its older
        // Explorer-style dialog instead of the common item dialog.
        var hooked = mode == "win32-hook";
        OpenFileHook hook = (_, _, _, _) => 0;
        var save = mode == "win32-save";
        var filter = string.Concat(RussianTypes.Select(type => type.Label + "\0" + type.Pattern + "\0")) + "\0";
        var name = new char[capacity];
        if (save) "export".CopyTo(0, name, 0, "export".Length);
        var file = GCHandle.Alloc(name, GCHandleType.Pinned);
        var strings = new[] { filter, root, title, "txt" }.Select(Marshal.StringToHGlobalUni).ToArray();
        try
        {
            var dialog = new OpenFileName
            {
                Size = Marshal.SizeOf<OpenFileName>(), Owner = owner, Filter = strings[0],
                FilterIndex = mode == "win32-open" ? 2 : 1, File = file.AddrOfPinnedObject(), MaxFile = capacity,
                InitialDir = strings[1], Title = strings[2], DefaultExtension = save ? strings[3] : 0,
                Flags = Explorer | PathMustExist | HideReadOnly | NoChangeDir
                    | (save ? OverwritePrompt : FileMustExist) | (mode == "win32-open-multi" ? AllowMultiSelect : 0)
                    | (hooked ? EnableHook : 0),
                Hook = hooked ? Marshal.GetFunctionPointerForDelegate(hook) : 0
            };
            var accepted = save ? GetSaveFileName(ref dialog) : GetOpenFileName(ref dialog);
            GC.KeepAlive(hook);
            if (!accepted)
            {
                var error = CommDlgExtendedError();
                if (error != 0) throw new InvalidOperationException($"The Win32 dialog failed (CommDlgExtendedError {error:X}).");
                return (Cancelled, [], dialog.FilterIndex);
            }
            // "dir\0name\0name\0\0" for several files, "path\0\0" for one.
            var parts = new string(name).Split('\0').TakeWhile(part => part.Length > 0).ToArray();
            string[] paths = parts.Length <= 1 ? parts : [.. parts.Skip(1).Select(part => Path.Combine(parts[0], part))];
            return (0, paths, dialog.FilterIndex);
        }
        finally
        {
            file.Free();
            foreach (var pointer in strings) Marshal.FreeHGlobal(pointer);
        }
    }

    private delegate nint OpenFileHook(nint dialog, uint message, nint wparam, nint lparam);

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenFileName
    {
        public int Size;
        public nint Owner, Instance, Filter, CustomFilter;
        public int MaxCustomFilter, FilterIndex;
        public nint File;
        public int MaxFile;
        public nint FileTitle;
        public int MaxFileTitle;
        public nint InitialDir, Title;
        public int Flags;
        public short FileOffset, FileExtension;
        public nint DefaultExtension, CustomData, Hook, Template, Reserved;
        public int Reserved2, FlagsEx;
    }

    // ---- SHBrowseForFolder: the legacy tree, which the integration leaves alone ----

    private static (int, string[], int) BrowseForFolder(string root, nint owner)
    {
        const uint ReturnOnlyFileSystem = 0x1, EditBox = 0x10, NewDialogStyle = 0x40;
        var start = Marshal.StringToHGlobalUni(root);
        var display = Marshal.AllocHGlobal(520);
        BrowseCallback callback = (window, message, _, _) =>
        {
            if (message == 1) SendMessage(window, 0x0467, 1, start); // BFFM_INITIALIZED: BFFM_SETSELECTIONW
            return 0;
        };
        try
        {
            var info = new BrowseInfo
            {
                Owner = owner, DisplayName = display, Title = "UltraExplorer integration fixture shbrowse",
                Flags = ReturnOnlyFileSystem | EditBox | NewDialogStyle, Callback = callback
            };
            var list = SHBrowseForFolder(ref info);
            GC.KeepAlive(callback);
            if (list == 0) return (Cancelled, [], 0);
            try
            {
                var path = new StringBuilder(1024);
                return SHGetPathFromIDList(list, path) ? (0, [path.ToString()], 0) : (0, [], 0);
            }
            finally { Marshal.FreeCoTaskMem(list); }
        }
        finally
        {
            Marshal.FreeHGlobal(start);
            Marshal.FreeHGlobal(display);
        }
    }

    private delegate int BrowseCallback(nint window, uint message, nint parameter, nint data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseInfo
    {
        public nint Owner, Root, DisplayName;
        public string Title;
        public uint Flags;
        public BrowseCallback Callback;
        public nint Data;
        public int Image;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW", CharSet = CharSet.Unicode)] private static extern bool GetOpenFileName(ref OpenFileName dialog);
    [DllImport("comdlg32.dll", EntryPoint = "GetSaveFileNameW", CharSet = CharSet.Unicode)] private static extern bool GetSaveFileName(ref OpenFileName dialog);
    [DllImport("comdlg32.dll")] private static extern uint CommDlgExtendedError();
    [DllImport("shell32.dll", EntryPoint = "SHBrowseForFolderW", CharSet = CharSet.Unicode)] private static extern nint SHBrowseForFolder(ref BrowseInfo info);
    [DllImport("shell32.dll", EntryPoint = "SHGetPathFromIDListW", CharSet = CharSet.Unicode)] private static extern bool SHGetPathFromIDList(nint list, StringBuilder path);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect box);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetProcessDpiAwarenessContext(nint context);
}
