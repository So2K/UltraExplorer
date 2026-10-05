using System.Runtime.InteropServices;
using System.IO;
using System.Text.Json;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Com;

namespace ViewAllSmoke;

/// <summary>A separate program using the real Windows COM dialogs, not
/// UltraExplorer's COM classes. The replacement must hand results back to these.</summary>
internal static class NativeDialogFixture
{
    private static bool _signalled;

    public static int Run(string[] args)
    {
        var index = Array.IndexOf(args, "--native-dialog-fixture");
        var root = args[index + 1];
        var output = args[index + 2];
        var mode = args[index + 3];
        if (mode == "save-repeat")
        {
            // Two native requests in the same application process. The pause
            // leaves the resident worker time to finish the first handoff.
            var repeated = (string[])args.Clone();
            repeated[index + 3] = "save";
            repeated[index + 2] = output + ".first.json";
            var first = Run(repeated);
            if (first != 0) return first;
            Thread.Sleep(300);
            repeated[index + 2] = output;
            return Run(repeated);
        }
        if (mode == "save-sequence")
        {
            // Several Save dialogs in a row, each in a folder of its own, while
            // the replacement keeps running: each is served by a picker the
            // worker prepared after the one before. The pause leaves it the
            // time to prepare the next one.
            string[] folders = ["one", "two", "three"];
            var sequence = (string[])args.Clone();
            sequence[index + 3] = "save";
            for (var step = 0; step < folders.Length; step++)
            {
                if (step > 0) Thread.Sleep(1500);
                sequence[index + 1] = Path.Combine(root, folders[step]);
                sequence[index + 2] = step == folders.Length - 1 ? output : output + "." + (step + 1) + ".json";
                var code = Run(sequence);
                if (code != 0) return code;
            }
            return 0;
        }
        // "-nofilter": no file-type list at all, as ShareX's "File upload" and
        // any WinForms OpenFileDialog without a Filter.
        var noFilter = mode.EndsWith("-nofilter", StringComparison.Ordinal);
        if (noFilter) mode = mode[..^"-nofilter".Length];
        // Edge cases real programs give this dialog (see FileTypes and the
        // folder below): "open-types24", "open-psdlabel", "save-defaultname",
        // "open-cyrillic", "folder-driveroot", "save-overwrite-prompt".
        var save = mode.StartsWith("save", StringComparison.Ordinal);
        var folder = mode.StartsWith("folder", StringComparison.Ordinal);
        // The user's own folder, which a dialog's address shows by the
        // user's name alone ("Address: User"), never as a path.
        var inProfile = mode == "open-profile";
        if (inProfile) mode = "open";
        // Before the dialog exists: it must open on a secondary monitor and
        // never take the keyboard from whatever the user is doing.
        using var guard = new FixtureWindowGuard(output + ".fixture.log");
        var id = new Guid(save ? "C0B4E2F3-BA21-4773-8DBA-335EC946EB8B" : "DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
        var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(id, true)!)!;
        var dialog = (IFileDialog)instance;
        try
        {
            dialog.GetOptions(out var options);
            options |= (uint)(FileDialogOptions.ForceFileSystem | FileDialogOptions.PathMustExist);
            if (folder) options |= (uint)FileDialogOptions.PickFolders;
            if (mode == "multi") options |= (uint)FileDialogOptions.AllowMultiSelect;
            // Asked for by the program itself (a Save dialog has it by default).
            if (mode == "save-overwrite-prompt") options |= (uint)FileDialogOptions.OverwritePrompt;
            dialog.SetOptions(options);
            dialog.SetTitle("UltraExplorer integration fixture " + mode);
            var shellId = ShellNative.ShellItemId;
            var folderPath = mode switch
            {
                "save-downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                _ when inProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                // A path with spaces and Cyrillic letters, as a Russian user's folders have.
                "open-cyrillic" => Path.Combine(root, "Папка с пробелами", "Мои документы 2026"),
                // A folder picker that starts in a drive's root (its address reads "C:\").
                "folder-driveroot" => Path.GetPathRoot(Environment.SystemDirectory)!,
                _ => root
            };
            var shellFolder = ShellNative.SHCreateItemFromParsingName(folderPath, 0, ref shellId);
            dialog.SetFolder(shellFolder);
            Marshal.ReleaseComObject(shellFolder);
            if (!folder && !noFilter)
            {
                var (types, chosen, extension) = FileTypes(mode);
                dialog.SetFileTypes((uint)types.Length, types);
                dialog.SetFileTypeIndex(chosen);
                dialog.SetDefaultExtension(extension);
            }
            if (save) dialog.SetFileName(mode switch
            {
                "save-downloads" => "UltraExplorer test.html",
                // A proposed name with no extension, accepted as the dialog shows it.
                "save-defaultname" => "Quarterly report",
                // The name of a file that is already there.
                "save-overwrite-prompt" => "sample.txt",
                _ => "export"
            });
            var custom = (INativeFileDialogCustomize)instance;
            if (mode.Contains("custom", StringComparison.Ordinal) || mode is "unsupported" or "save-hidden-options")
            {
                custom.AddCheckButton(500, "Embed sRGB profile", false);
                custom.AddCheckButton(501, "Keep layers", true);
                custom.AddText(600, "Export quality");
                custom.AddComboBox(502);
                custom.SetControlLabel(502, "Export quality");
                custom.AddControlItem(502, 1, "Full quality");
                custom.AddControlItem(502, 2, "Small file");
                custom.SetSelectedControlItem(502, 1);
                custom.AddText(601, "Author");
                custom.AddEditBox(503, "Original author");
                custom.SetControlLabel(503, "Author");
                custom.AddRadioButtonList(504);
                custom.AddControlItem(504, 1, "Layered");
                custom.AddControlItem(504, 2, "Flattened");
                custom.SetSelectedControlItem(504, 1);
                if (mode == "save-hidden-options") custom.SetControlState(501, 1); // enabled, not visible
            }
            if (mode == "unsupported") custom.AddPushButton(505, "Advanced export options");
            var events = new FixtureEvents(custom, mode == "save-hidden-options");
            dialog.Advise(events, out var cookie);
            using var optionProbe = mode.Contains("custom", StringComparison.Ordinal) ? new FixtureOptionProbe(dialog, custom, output) : null;
            File.WriteAllText(output + ".ready", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // A dialog that appears while the replacement is already waiting
            // for it, as in real use: shown only once the runner says so (the
            // first dialog of the process; later ones follow as they come).
            if (Environment.GetEnvironmentVariable("NATIVE_DIALOG_FIXTURE_SHOW_SIGNAL") is { Length: > 0 } signal && !_signalled)
            {
                _signalled = true;
                using var show = EventWaitHandle.OpenExisting(signal);
                if (!show.WaitOne(TimeSpan.FromMinutes(2))) throw new TimeoutException("The runner never asked for the dialog.");
            }
            var result = dialog.Show(guard.Owner);
            dialog.Unadvise(cookie);
            var paths = new List<string>();
            if (result == 0)
            {
                if (!save && instance is IFileOpenDialog open && open.GetResults(out var array) == 0)
                {
                    try
                    {
                        array.GetCount(out var count);
                        for (uint i = 0; i < count; i++) { array.GetItemAt(i, out var item); paths.Add(ReadPath(item)); Marshal.ReleaseComObject(item); }
                    }
                    finally { Marshal.ReleaseComObject(array); }
                }
                else { dialog.GetResult(out var item); paths.Add(ReadPath(item)); Marshal.ReleaseComObject(item); }
            }
            dialog.GetFileTypeIndex(out var typeIndex);
            custom.GetCheckButtonState(500, out var profile);
            custom.GetCheckButtonState(501, out var layers);
            custom.GetSelectedControlItem(502, out var quality);
            custom.GetSelectedControlItem(504, out var radio);
            custom.GetEditBoxText(503, out var text);
            var author = text == 0 ? "" : Marshal.PtrToStringUni(text) ?? "";
            if (text != 0) Marshal.FreeCoTaskMem(text);
            File.WriteAllText(output, JsonSerializer.Serialize(new { result, paths, typeIndex, profile, layers, quality, radio, author, processId = Environment.ProcessId,
                onAccept = events.Accepted, events.TypeChanges }));
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(output, JsonSerializer.Serialize(new { error = ex.ToString() })); return 2; }
        finally { Marshal.ReleaseComObject(instance); }
    }

    /// <summary>The file types a mode's program gives its dialog, the one chosen (one-based) and the default extension.</summary>
    private static (ComDlgFilterSpec[] Types, uint Chosen, string Extension) FileTypes(string mode)
    {
        ComDlgFilterSpec[] usual =
        [
            new() { Name = "Text documents (*.txt)", Spec = "*.txt" },
            new() { Name = "Images (*.png;*.jpg)", Spec = "*.png;*.jpg" },
            new() { Name = "All files (*.*)", Spec = "*.*" }
        ];
        return mode switch
        {
            "save-downloads" => ([
                new() { Name = "Webpage, Complete (*.htm;*.html)", Spec = "*.htm;*.html" },
                new() { Name = "Webpage, HTML Only (*.htm;*.html)", Spec = "*.htm;*.html" },
                new() { Name = "All files (*.*)", Spec = "*.*" }
            ], 1, "html"),
            "save-extension" => (usual, 3, "custom"),
            // An image editor's list: 24 types, the chosen one far down it.
            "open-types24" => ([
                new() { Name = "PNG image (*.png)", Spec = "*.png" },
                new() { Name = "JPEG image (*.jpg;*.jpeg)", Spec = "*.jpg;*.jpeg" },
                new() { Name = "Bitmap (*.bmp)", Spec = "*.bmp" },
                new() { Name = "GIF animation (*.gif)", Spec = "*.gif" },
                new() { Name = "TIFF image (*.tif;*.tiff)", Spec = "*.tif;*.tiff" },
                new() { Name = "WebP image (*.webp)", Spec = "*.webp" },
                new() { Name = "HEIF image (*.heic;*.heif)", Spec = "*.heic;*.heif" },
                new() { Name = "AVIF image (*.avif)", Spec = "*.avif" },
                new() { Name = "Icon (*.ico)", Spec = "*.ico" },
                new() { Name = "Targa (*.tga)", Spec = "*.tga" },
                new() { Name = "DirectDraw surface (*.dds)", Spec = "*.dds" },
                new() { Name = "OpenEXR (*.exr)", Spec = "*.exr" },
                new() { Name = "Radiance HDR (*.hdr)", Spec = "*.hdr" },
                new() { Name = "PCX (*.pcx)", Spec = "*.pcx" },
                new() { Name = "Portable bitmap (*.pbm)", Spec = "*.pbm" },
                new() { Name = "Portable graymap (*.pgm)", Spec = "*.pgm" },
                new() { Name = "Portable pixmap (*.ppm)", Spec = "*.ppm" },
                new() { Name = "JPEG 2000 (*.jp2;*.j2k)", Spec = "*.jp2;*.j2k" },
                new() { Name = "JPEG XL (*.jxl)", Spec = "*.jxl" },
                new() { Name = "Scalable vector graphics (*.svg)", Spec = "*.svg" },
                new() { Name = "Enhanced metafile (*.emf)", Spec = "*.emf" },
                new() { Name = "Windows metafile (*.wmf)", Spec = "*.wmf" },
                new() { Name = "All images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp)", Spec = "*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp" },
                new() { Name = "All files (*.*)", Spec = "*.*" }
            ], 17, "ppm"),
            // A type named without its pattern in parentheses: Windows shows
            // it as "Photoshop image (*.psd)" (seen here with extensions shown).
            "open-psdlabel" => ([
                new() { Name = "PNG image (*.png)", Spec = "*.png" },
                new() { Name = "Photoshop image", Spec = "*.psd" },
                new() { Name = "All files (*.*)", Spec = "*.*" }
            ], 2, "psd"),
            "save-defaultname" => ([
                new() { Name = "CSV (comma delimited) (*.csv)", Spec = "*.csv" },
                new() { Name = "All files (*.*)", Spec = "*.*" }
            ], 1, "csv"),
            _ => (usual, 1, "txt")
        };
    }

    private static string ReadPath(IShellItem item)
    {
        item.GetDisplayName(ShellItemDisplayName.FileSystemPath, out var name);
        try { return Marshal.PtrToStringUni(name) ?? ""; }
        finally { Marshal.FreeCoTaskMem(name); }
    }

    /// <summary>Read-only fixture observation on the dialog's own STA. It lets
    /// recovery tests compare the caller's actual COM option state without
    /// adding UI Automation traffic to an unfinished provider read.</summary>
    private sealed class FixtureOptionProbe : IDisposable
    {
        private delegate void TimerProc(nint window, uint message, nuint timer, uint tick);
        private readonly IFileDialog _dialog;
        private readonly INativeFileDialogCustomize _controls;
        private readonly string _output;
        private readonly TimerProc _tick;
        private readonly nuint _timer;
        public FixtureOptionProbe(IFileDialog dialog, INativeFileDialogCustomize controls, string output)
        {
            _dialog = dialog; _controls = controls; _output = output; _tick = Tick;
            Write(output + ".options-before.json");
            _timer = SetTimer(0, 0, 25, _tick);
        }
        private void Tick(nint window, uint message, nuint timer, uint tick)
        {
            var request = _output + ".inspect-options";
            if (!File.Exists(request)) return;
            try { File.Delete(request); Write(_output + ".options-after.json"); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { File.WriteAllText(_output + ".options-after.json", JsonSerializer.Serialize(new { error = ex.ToString() })); }
        }
        private void Write(string file)
        {
            _dialog.GetOptions(out var options); _dialog.GetFileTypeIndex(out var typeIndex);
            var profileCode = _controls.GetCheckButtonState(500, out var profile);
            var layersCode = _controls.GetCheckButtonState(501, out var layers);
            var qualityCode = _controls.GetSelectedControlItem(502, out var quality);
            var radioCode = _controls.GetSelectedControlItem(504, out var radio);
            var authorCode = _controls.GetEditBoxText(503, out var pointer);
            var author = pointer == 0 ? "" : Marshal.PtrToStringUni(pointer) ?? "";
            if (pointer != 0) Marshal.FreeCoTaskMem(pointer);
            var temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { options, typeIndex, profile, layers, quality, radio, author,
                profileCode, layersCode, qualityCode, radioCode, authorCode, processId = Environment.ProcessId, threadId = GetCurrentThreadId() }));
            File.Move(temporary, file, true);
        }
        public void Dispose() { if (_timer != 0) KillTimer(0, _timer); GC.KeepAlive(_tick); }
        [DllImport("user32.dll")] private static extern nuint SetTimer(nint window, nuint id, uint milliseconds, TimerProc callback);
        [DllImport("user32.dll")] private static extern bool KillTimer(nint window, nuint id);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class FixtureEvents(INativeFileDialogCustomize controls, bool dynamicVisibility) : IFileDialogEvents
    {
        public object? Accepted { get; private set; }
        public int TypeChanges { get; private set; }
        public int OnFileOk(IFileDialog dialog)
        {
            var profileCode = controls.GetCheckButtonState(500, out var profile);
            controls.GetCheckButtonState(501, out var layers);
            var qualityCode = controls.GetSelectedControlItem(502, out var quality);
            controls.GetSelectedControlItem(504, out var radio);
            var authorCode = controls.GetEditBoxText(503, out var text);
            var author = text == 0 ? "" : Marshal.PtrToStringUni(text) ?? "";
            if (text != 0) Marshal.FreeCoTaskMem(text);
            Accepted = new { profile, layers, quality, radio, author, profileCode, qualityCode, authorCode };
            return 0;
        }
        public int OnFolderChanging(IFileDialog dialog, IShellItem folder) => 0;
        public int OnFolderChange(IFileDialog dialog) => 0;
        public int OnSelectionChange(IFileDialog dialog) => 0;
        public int OnShareViolation(IFileDialog dialog, IShellItem item, out ShareViolationResponse response) { response = ShareViolationResponse.Default; return 0; }
        public int OnTypeChange(IFileDialog dialog)
        {
            TypeChanges++;
            if (dynamicVisibility)
            {
                dialog.GetFileTypeIndex(out var type);
                controls.SetControlState(501, type == 2 ? 3u : 1u);
            }
            return 0;
        }
        public int OnOverwrite(IFileDialog dialog, IShellItem item, out OverwriteResponse response) { response = OverwriteResponse.Default; return 0; }
    }
}

/// <summary>Keeps the fixture's Windows dialog away from the user. Its owner is
/// an invisible tool window in the middle of a secondary monitor, so the dialog
/// (and any warning it raises) centres there; and a hook on this thread only
/// marks every top-level window the thread creates WS_EX_NOACTIVATE and refuses
/// its activation, so showing the dialog cannot take the user's keyboard. Every
/// refusal is written to the log: an activation asked for by another process
/// (SetForegroundWindow) is visible there too, although the hook cannot stop
/// Windows moving the foreground before it is asked. Not every way of showing a
/// dialog centres it on its owner (GetOpenFileNameW and WPF's dialogs put it at
/// the owner's corner): a producer of those asks the guard to keep every
/// top-level dialog the thread creates inside that monitor's work area as well,
/// before it is placed - a move that would take it past an edge is shortened to
/// the edge, and written to the log. The COM dialogs centre on the owner and go
/// without it (it subclasses the dialog, which is not free on every message).</summary>
internal sealed class FixtureWindowGuard : IDisposable
{
    private const int CbtHook = 5, CbtCreateWindow = 3, CbtActivate = 5;
    private const long ChildStyle = 0x40000000, NoActivate = 0x08000000, ToolWindow = 0x80;
    private const uint PopupStyle = 0x80000000;
    private delegate nint HookProc(int code, nint wparam, nint lparam);
    private delegate nint SubclassProc(nint window, uint message, nint wparam, nint lparam, nuint id, nuint data);
    private readonly HookProc _procedure;
    private readonly SubclassProc _keeper;
    private readonly nint _hook;
    private readonly string _log;
    private TestScreen.Box? _work;

    public nint Owner { get; }

    public FixtureWindowGuard(string log, bool keepDialogsInside = false)
    {
        _log = log;
        _procedure = OnCbt;
        _keeper = KeepOnTarget;
        _hook = SetWindowsHookEx(CbtHook, _procedure, 0, GetCurrentThreadId());
        Write(_hook == 0 ? $"activation guard FAILED ({Marshal.GetLastWin32Error()})" : "activation guard installed");
        if (TestScreen.Target() is not { } target)
        {
            Write("no secondary monitor: the dialog has no owner");
            return;
        }
        const int width = 640, height = 480;
        var work = target.Work;
        if (keepDialogsInside) _work = work;
        Owner = CreateWindowEx((uint)(ToolWindow | NoActivate), "STATIC", "UltraExplorer fixture owner", PopupStyle,
            work.Left + Math.Max(0, (work.Width - width) / 2), work.Top + Math.Max(0, (work.Height - height) / 2),
            width, height, 0, 0, 0, 0);
        Write(Owner == 0 ? $"owner window FAILED ({Marshal.GetLastWin32Error()})"
            : $"owner {Owner:X} (hidden) {TestScreen.Describe(Owner)}; target monitor {target.Bounds}");
    }

    private nint OnCbt(int code, nint wparam, nint lparam)
    {
        try
        {
            if (code == CbtCreateWindow && lparam != 0)
            {
                // CBT_CREATEWND.lpcs -> CREATESTRUCT.style (offset 48 on x64).
                var create = Marshal.ReadIntPtr(lparam);
                var style = (uint)Marshal.ReadInt32(create, 48);
                if ((style & ChildStyle) == 0)
                    SetWindowLongPtr(wparam, -20, GetWindowLongPtr(wparam, -20) | (nint)NoActivate);
                if ((style & ChildStyle) == 0 && _work is not null && UltraExplorer.Picker.Integration.DialogNative.ClassName(wparam) == "#32770")
                {
                    // CREATESTRUCT cy, cx, y, x at 32, 36, 40, 44; then every later move.
                    int x = Marshal.ReadInt32(create, 44), y = Marshal.ReadInt32(create, 40);
                    int cx = Marshal.ReadInt32(create, 36), cy = Marshal.ReadInt32(create, 32);
                    if (x != UseDefault && cx > 0 && cy > 0 && Inside(x, y, cx, cy) is var (left, top) && (left != x || top != y))
                    {
                        Marshal.WriteInt32(create, 44, left);
                        Marshal.WriteInt32(create, 40, top);
                        Write($"kept {wparam:X} on the secondary monitor at creation: ({x},{y}) -> ({left},{top})");
                    }
                    if (!SetWindowSubclass(wparam, _keeper, 1, 0)) Write($"placement keeper FAILED for {wparam:X}");
                }
            }
            else if (code == CbtActivate)
            {
                Write($"refused activation of {wparam:X} {UltraExplorer.Picker.Integration.DialogNative.ClassName(wparam)}");
                return 1;
            }
        }
        catch (Exception ex) { Write("activation guard error " + ex.Message); }
        return CallNextHookEx(_hook, code, wparam, lparam);
    }

    /// <summary>Where a window of this size at (x, y) stays wholly inside the target work area (its top-left corner when it is larger).</summary>
    private (int Left, int Top) Inside(int x, int y, int width, int height)
    {
        var work = _work!.Value;
        return (Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width)), Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height)));
    }

    private nint KeepOnTarget(nint window, uint message, nint wparam, nint lparam, nuint id, nuint data)
    {
        try
        {
            if (message == 0x0046 && lparam != 0) // WM_WINDOWPOSCHANGING
            {
                var position = Marshal.PtrToStructure<WindowPosition>(lparam);
                if ((position.Flags & (NoMove | NoSize)) != (NoMove | NoSize))
                {
                    GetWindowRect(window, out var now);
                    var x = (position.Flags & NoMove) != 0 ? now.Left : position.X;
                    var y = (position.Flags & NoMove) != 0 ? now.Top : position.Y;
                    var width = (position.Flags & NoSize) != 0 ? now.Width : position.Width;
                    var height = (position.Flags & NoSize) != 0 ? now.Height : position.Height;
                    var (left, top) = Inside(x, y, width, height);
                    if (width > 0 && height > 0 && (left != x || top != y))
                    {
                        position.X = left;
                        position.Y = top;
                        position.Flags &= ~NoMove;
                        Marshal.StructureToPtr(position, lparam, false);
                        Write($"kept {window:X} on the secondary monitor: ({x},{y}) -> ({left},{top})");
                    }
                }
            }
            else if (message == 0x0082) RemoveWindowSubclass(window, _keeper, id); // WM_NCDESTROY
        }
        catch (Exception ex) { Write("placement keeper error " + ex.Message); }
        return DefSubclassProc(window, message, wparam, lparam);
    }

    private const int UseDefault = unchecked((int)0x80000000);
    private const uint NoSize = 0x1, NoMove = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition { public nint Window, After; public int X, Y, Width, Height; public uint Flags; }

    private void Write(string line)
    {
        try { File.AppendAllText(_log, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); }
        catch (IOException) { }
    }

    public void Dispose()
    {
        if (_hook != 0) UnhookWindowsHookEx(_hook);
        if (Owner != 0) DestroyWindow(Owner);
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern nint SetWindowsHookEx(int kind, HookProc procedure, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wparam, nint lparam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out TestScreen.Box box);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, SubclassProc procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProc procedure, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nint wparam, nint lparam);
}

/// <summary>The monitors, and where a test's windows belong: the first monitor
/// that is not the primary one (the choice UltraExplorer's own test windows
/// make, or the one ULTRAEXPLORER_DIAGNOSTICS_MONITOR names), never the primary.</summary>
internal static class TestScreen
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Box
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly bool Overlaps(Box other) => Left < other.Right && other.Left < Right && Top < other.Bottom && other.Top < Bottom;
        public override readonly string ToString() => $"[{Left},{Top} {Width}x{Height}]";
    }

    internal readonly record struct Monitor(nint Handle, Box Bounds, Box Work, bool Primary);

    public static List<Monitor> All()
    {
        var monitors = new List<Monitor>();
        EnumDisplayMonitors(0, 0, (handle, _, _, _) =>
        {
            if (Info(handle) is { } monitor) monitors.Add(monitor);
            return true;
        }, 0);
        return monitors;
    }

    public static Monitor? Target()
    {
        var monitors = All();
        var chosen = monitors.FindIndex(monitor => !monitor.Primary);
        if (int.TryParse(Environment.GetEnvironmentVariable("ULTRAEXPLORER_DIAGNOSTICS_MONITOR"), out var asked)
            && asked >= 0 && asked < monitors.Count) chosen = asked;
        return chosen >= 0 && !monitors[chosen].Primary ? monitors[chosen] : null;
    }

    public static Box WindowRect(nint window) { GetWindowRect(window, out var box); return box; }

    /// <summary>True when the window is wholly off the primary monitor and on a secondary one.</summary>
    public static bool OnSecondary(nint window)
    {
        var box = WindowRect(window);
        var primary = All().Where(monitor => monitor.Primary).ToArray();
        return box.Width > 0 && Info(MonitorFromWindow(window, 2)) is { Primary: false }
            && !primary.Any(monitor => monitor.Bounds.Overlaps(box));
    }

    public static string Describe(nint window)
    {
        var box = WindowRect(window);
        var monitor = Info(MonitorFromWindow(window, 2));
        return $"rect {box} on {(monitor is { Primary: true } ? "PRIMARY" : "secondary")} monitor {monitor?.Bounds}";
    }

    private static Monitor? Info(nint handle)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return handle != 0 && GetMonitorInfo(handle, ref info) ? new Monitor(handle, info.Bounds, info.Work, (info.Flags & 1) != 0) : null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Box Bounds, Work; public uint Flags; }
    private delegate bool MonitorProc(nint monitor, nint context, nint rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint context, nint clip, MonitorProc callback, nint data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Box box);
}

[ComImport, Guid("E6FDD21A-163F-4975-9C8C-A69F1BA37034"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INativeFileDialogCustomize
{
    [PreserveSig] int EnableOpenDropDown(uint id);
    [PreserveSig] int AddMenu(uint id, [MarshalAs(UnmanagedType.LPWStr)] string label);
    [PreserveSig] int AddPushButton(uint id, [MarshalAs(UnmanagedType.LPWStr)] string label);
    [PreserveSig] int AddComboBox(uint id);
    [PreserveSig] int AddRadioButtonList(uint id);
    [PreserveSig] int AddCheckButton(uint id, [MarshalAs(UnmanagedType.LPWStr)] string label, [MarshalAs(UnmanagedType.Bool)] bool value);
    [PreserveSig] int AddEditBox(uint id, [MarshalAs(UnmanagedType.LPWStr)] string text);
    [PreserveSig] int AddSeparator(uint id);
    [PreserveSig] int AddText(uint id, [MarshalAs(UnmanagedType.LPWStr)] string text);
    [PreserveSig] int SetControlLabel(uint id, [MarshalAs(UnmanagedType.LPWStr)] string label);
    [PreserveSig] int GetControlState(uint id, out uint state);
    [PreserveSig] int SetControlState(uint id, uint state);
    [PreserveSig] int GetEditBoxText(uint id, out nint text);
    [PreserveSig] int SetEditBoxText(uint id, [MarshalAs(UnmanagedType.LPWStr)] string text);
    [PreserveSig] int GetCheckButtonState(uint id, [MarshalAs(UnmanagedType.Bool)] out bool value);
    [PreserveSig] int SetCheckButtonState(uint id, [MarshalAs(UnmanagedType.Bool)] bool value);
    [PreserveSig] int AddControlItem(uint id, uint item, [MarshalAs(UnmanagedType.LPWStr)] string text);
    [PreserveSig] int RemoveControlItem(uint id, uint item);
    [PreserveSig] int RemoveAllControlItems(uint id);
    [PreserveSig] int GetControlItemState(uint id, uint item, out uint state);
    [PreserveSig] int SetControlItemState(uint id, uint item, uint state);
    [PreserveSig] int GetSelectedControlItem(uint id, out uint item);
    [PreserveSig] int SetSelectedControlItem(uint id, uint item);
    [PreserveSig] int StartVisualGroup(uint id, [MarshalAs(UnmanagedType.LPWStr)] string label);
    [PreserveSig] int EndVisualGroup();
    [PreserveSig] int MakeProminent(uint id);
    [PreserveSig] int SetControlItemText(uint id, uint item, [MarshalAs(UnmanagedType.LPWStr)] string text);
}
