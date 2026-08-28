using System.Runtime.InteropServices;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Com;

namespace ViewAllSmoke;

/// <summary>
/// A caller, written the way a real program would write it: create the CLSID,
/// ask for <c>IFileOpenDialog</c>, set it up, subscribe to its events, call
/// <c>Show</c>, read the result back as shell items.  Nothing here knows that
/// UltraExplorer is on the other end.
/// </summary>
internal static class PickerComClient
{
    public static int Run(string[] args)
    {
        var folder = args.FirstOrDefault(argument => !argument.StartsWith('-'))
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var wantsSave = args.Any(argument => argument.Equals("--save", StringComparison.OrdinalIgnoreCase));

        var classId = new Guid(wantsSave
            ? ComServerRegistration.SaveDialogClsid
            : ComServerRegistration.OpenDialogClsid);

        var type = Type.GetTypeFromCLSID(classId);
        if (type is null)
        {
            Console.WriteLine("FAIL  no type for the dialog CLSID");
            return 2;
        }

        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(type);
            if (instance is null)
            {
                Console.WriteLine("FAIL  CoCreateInstance returned nothing");
                return 2;
            }

            Console.WriteLine("ok    created the dialog object");

            return wantsSave
                ? RunSave((IFileSaveDialog)instance, folder)
                : RunOpen((IFileOpenDialog)instance, folder);
        }
        catch (COMException exception)
        {
            Console.WriteLine($"FAIL  {exception.Message} (0x{exception.HResult:X8})");
            return 2;
        }
        catch (InvalidCastException exception)
        {
            Console.WriteLine($"FAIL  QueryInterface: {exception.Message}");
            return 2;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                Marshal.FinalReleaseComObject(instance);
            }
        }
    }

    private static int RunOpen(IFileOpenDialog dialog, string folder)
    {
        Console.WriteLine("ok    QueryInterface for IFileOpenDialog");

        Check("SetTitle", dialog.SetTitle("Import through COM"));
        Check("SetOkButtonLabel", dialog.SetOkButtonLabel("Import"));
        Check("SetOptions", dialog.SetOptions((uint)(FileDialogOptions.FileMustExist
            | FileDialogOptions.PathMustExist
            | FileDialogOptions.AllowMultiSelect)));

        ComDlgFilterSpec[] specs =
        [
            new() { Name = "Text", Spec = "*.txt" },
            new() { Name = "All Files", Spec = "*.*" }
        ];
        Check("SetFileTypes", dialog.SetFileTypes((uint)specs.Length, specs));
        Check("SetFileTypeIndex", dialog.SetFileTypeIndex(1));

        if (ShellNative.ItemFor(folder) is { } start)
        {
            Check("SetFolder", dialog.SetFolder(start));
        }

        Check("GetOptions", dialog.GetOptions(out var options));
        Console.WriteLine($"ok    options round-tripped as 0x{options:X}");

        var sink = new EventLog();
        Check("Advise", dialog.Advise(sink, out var cookie));

        var shown = dialog.Show(IntPtr.Zero);
        Console.WriteLine($"ok    Show returned 0x{shown:X8}");

        Check("Unadvise", dialog.Unadvise(cookie));
        Console.WriteLine($"ok    events seen: {sink.Summary}");

        if (shown != 0)
        {
            Console.WriteLine("cancelled");
            return 1;
        }

        if (dialog.GetResult(out var item) != 0)
        {
            Console.WriteLine("FAIL  GetResult");
            return 2;
        }

        Console.WriteLine($"result {ShellNative.PathOf(item)}");

        if (dialog.GetResults(out var items) == 0)
        {
            items.GetCount(out var count);
            for (uint index = 0; index < count; index++)
            {
                items.GetItemAt(index, out var each);
                Console.WriteLine($"result[{index}] {ShellNative.PathOf(each)}");
            }
        }

        dialog.GetFileTypeIndex(out var typeIndex);
        Console.WriteLine($"fileTypeIndex {typeIndex}");
        return 0;
    }

    private static int RunSave(IFileSaveDialog dialog, string folder)
    {
        Console.WriteLine("ok    QueryInterface for IFileSaveDialog");

        Check("SetTitle", dialog.SetTitle("Export through COM"));
        Check("SetDefaultExtension", dialog.SetDefaultExtension("txt"));
        Check("SetFileName", dialog.SetFileName("exported"));

        ComDlgFilterSpec[] specs = [new() { Name = "Text", Spec = "*.txt" }];
        Check("SetFileTypes", dialog.SetFileTypes((uint)specs.Length, specs));

        if (ShellNative.ItemFor(folder) is { } start)
        {
            Check("SetFolder", dialog.SetFolder(start));
        }

        var shown = dialog.Show(IntPtr.Zero);
        Console.WriteLine($"ok    Show returned 0x{shown:X8}");

        if (shown != 0)
        {
            Console.WriteLine("cancelled");
            return 1;
        }

        var got = dialog.GetResult(out var item);
        Console.WriteLine(got == 0
            ? $"result {ShellNative.PathOf(item)}"
            : $"FAIL  GetResult 0x{got:X8}");
        return got == 0 ? 0 : 2;
    }

    private static void Check(string name, int hresult) =>
        Console.WriteLine(hresult == 0 ? $"ok    {name}" : $"FAIL  {name} 0x{hresult:X8}");

    /// <summary>Records what the dialog reported while it was open.</summary>
    private sealed class EventLog : IFileDialogEvents
    {
        private readonly List<string> _seen = [];

        public string Summary => _seen.Count == 0 ? "(none)" : string.Join(", ", _seen.Distinct());

        public int OnFileOk(IFileDialog dialog)
        {
            _seen.Add("FileOk");
            return 0;
        }

        public int OnFolderChanging(IFileDialog dialog, IShellItem folder)
        {
            _seen.Add("FolderChanging");
            return 0;
        }

        public int OnFolderChange(IFileDialog dialog)
        {
            _seen.Add("FolderChange");
            return 0;
        }

        public int OnSelectionChange(IFileDialog dialog)
        {
            _seen.Add("SelectionChange");
            return 0;
        }

        public int OnShareViolation(IFileDialog dialog, IShellItem item, out ShareViolationResponse response)
        {
            _seen.Add("ShareViolation");
            response = ShareViolationResponse.Default;
            return 0;
        }

        public int OnTypeChange(IFileDialog dialog)
        {
            _seen.Add("TypeChange");
            return 0;
        }

        public int OnOverwrite(IFileDialog dialog, IShellItem item, out OverwriteResponse response)
        {
            _seen.Add("Overwrite");
            response = OverwriteResponse.Default;
            return 0;
        }
    }
}
