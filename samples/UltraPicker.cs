// Two ways for a .NET program to use UltraExplorer as its file dialog.
//
//   UltraPicker.Pick    - runs "UltraExplorer.exe --pick" and reads the answer.
//                         Works from any process, needs no registration.
//
//   UltraPicker.PickCom - creates the dialog COM object and drives it through
//                         the standard IFileOpenDialog, exactly as it would
//                         drive the Windows one.  Needs --register-picker once.
//
// Drop this file into a project; it has no dependencies beyond the framework.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace UltraExplorer.Samples;

public enum PickMode
{
    Open,
    Save,
    Folder
}

public sealed class PickRequest
{
    public PickMode Mode { get; set; } = PickMode.Open;

    public string? Title { get; set; }

    /// <summary>"Component|*.component;*.json|All Files|*.*"</summary>
    public string? Filter { get; set; }

    public string? FileName { get; set; }

    public string? DefaultExtension { get; set; }

    public string? StartFolder { get; set; }

    public string? OkButtonLabel { get; set; }

    public bool MultiSelect { get; set; }

    public bool ShowHidden { get; set; }

    /// <summary>Identifies this caller so its last folder comes back next time.</summary>
    public Guid ClientGuid { get; set; }

    public IntPtr Owner { get; set; }
}

public static class UltraPicker
{
    public static string Executable { get; set; } = "UltraExplorer.exe";

    /// <summary>
    /// Shows the picker in its own process.  Returns the chosen paths, or an
    /// empty array when the user cancelled.
    /// </summary>
    public static string[] Pick(PickRequest request)
    {
        var resultFile = Path.Combine(Path.GetTempPath(), $"ultrapick-{Guid.NewGuid():N}.json");
        var start = new ProcessStartInfo(Executable) { UseShellExecute = false };

        // ArgumentList quotes each entry, which matters: a filter is full of
        // spaces, semicolons and parentheses.
        start.ArgumentList.Add("--pick");
        start.ArgumentList.Add("--mode");
        start.ArgumentList.Add(request.Mode.ToString().ToLowerInvariant());
        start.ArgumentList.Add("--result");
        start.ArgumentList.Add(resultFile);

        Add("--title", request.Title);
        Add("--filter", request.Filter);
        Add("--file-name", request.FileName);
        Add("--ext", request.DefaultExtension);
        Add("--start", request.StartFolder);
        Add("--ok-label", request.OkButtonLabel);

        if (request.MultiSelect)
        {
            start.ArgumentList.Add("--multiselect");
        }

        if (request.ShowHidden)
        {
            start.ArgumentList.Add("--flag");
            start.ArgumentList.Add("ForceShowHidden");
        }

        if (request.ClientGuid != Guid.Empty)
        {
            start.ArgumentList.Add("--client-guid");
            start.ArgumentList.Add(request.ClientGuid.ToString("D"));
        }

        if (request.Owner != IntPtr.Zero)
        {
            start.ArgumentList.Add("--owner");
            start.ArgumentList.Add(request.Owner.ToInt64().ToString());
        }

        try
        {
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException($"Could not start {Executable}.");
            process.WaitForExit();

            if (process.ExitCode != 0 || !File.Exists(resultFile))
            {
                return [];
            }

            using var document = JsonDocument.Parse(File.ReadAllText(resultFile));
            var root = document.RootElement;
            if (!root.GetProperty("accepted").GetBoolean())
            {
                return [];
            }

            var paths = new List<string>();
            foreach (var element in root.GetProperty("paths").EnumerateArray())
            {
                if (element.GetString() is { Length: > 0 } path)
                {
                    paths.Add(path);
                }
            }

            return [.. paths];
        }
        finally
        {
            try
            {
                if (File.Exists(resultFile))
                {
                    File.Delete(resultFile);
                }
            }
            catch (IOException)
            {
                // A stale temp file is harmless.
            }
        }
    }

    /// <summary>
    /// The same thing through COM.  Run "UltraExplorer.exe --register-picker"
    /// once first; after that this is the standard dialog code with one GUID
    /// changed.
    /// </summary>
    public static string[] PickCom(PickRequest request)
    {
        var classId = new Guid(request.Mode == PickMode.Save
            ? "A1D3B6E4-59C7-4E1B-9F2A-7C61D8E04B32"
            : "A1D3B6E4-59C7-4E1B-9F2A-7C61D8E04B31");

        var type = Type.GetTypeFromCLSID(classId)
            ?? throw new InvalidOperationException("The picker is not registered.");

        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(type);
            var dialog = (IFileOpenDialog)instance!;

            uint options = 0x800;                                  // FOS_PATHMUSTEXIST
            if (request.Mode == PickMode.Folder) options |= 0x20;   // FOS_PICKFOLDERS
            if (request.Mode == PickMode.Open) options |= 0x1000;   // FOS_FILEMUSTEXIST
            if (request.MultiSelect) options |= 0x200;              // FOS_ALLOWMULTISELECT
            if (request.ShowHidden) options |= 0x10000000;          // FOS_FORCESHOWHIDDEN
            dialog.SetOptions(options);

            if (request.Title is { Length: > 0 } title)
            {
                dialog.SetTitle(title);
            }

            if (request.OkButtonLabel is { Length: > 0 } ok)
            {
                dialog.SetOkButtonLabel(ok);
            }

            if (request.FileName is { Length: > 0 } name)
            {
                dialog.SetFileName(name);
            }

            if (request.DefaultExtension is { Length: > 0 } extension)
            {
                dialog.SetDefaultExtension(extension);
            }

            if (ParseFilter(request.Filter) is { Length: > 0 } specs)
            {
                dialog.SetFileTypes((uint)specs.Length, specs);
            }

            if (request.StartFolder is { Length: > 0 } folder && ItemFor(folder) is { } startItem)
            {
                dialog.SetFolder(startItem);
            }

            if (request.ClientGuid != Guid.Empty)
            {
                var client = request.ClientGuid;
                dialog.SetClientGuid(ref client);
            }

            if (dialog.Show(request.Owner) != 0)
            {
                return [];
            }

            if (request.MultiSelect && dialog.GetResults(out var many) == 0)
            {
                many.GetCount(out var count);
                var paths = new string[count];
                for (uint index = 0; index < count; index++)
                {
                    many.GetItemAt(index, out var each);
                    paths[index] = PathOf(each) ?? string.Empty;
                }

                return paths;
            }

            return dialog.GetResult(out var single) == 0 && PathOf(single) is { } only
                ? [only]
                : [];
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                Marshal.FinalReleaseComObject(instance);
            }
        }
    }

    private static ComDlgFilterSpec[] ParseFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return [];
        }

        var parts = filter.Split('|');
        var specs = new List<ComDlgFilterSpec>();
        for (var index = 0; index + 1 < parts.Length; index += 2)
        {
            specs.Add(new ComDlgFilterSpec { Name = parts[index], Spec = parts[index + 1] });
        }

        return [.. specs];
    }

    private static IShellItem? ItemFor(string path)
    {
        var id = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        try
        {
            return SHCreateItemFromParsingName(path, IntPtr.Zero, ref id);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static string? PathOf(IShellItem item)
    {
        var buffer = IntPtr.Zero;
        try
        {
            return item.GetDisplayName(0x80058000, out buffer) == 0   // SIGDN_FILESYSPATH
                ? Marshal.PtrToStringUni(buffer)
                : null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern IShellItem SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ComDlgFilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Spec;
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr instance);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint kind, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport]
    [Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr instance);
        [PreserveSig] int GetPropertyStore(uint flags, ref Guid riid, out IntPtr instance);
        [PreserveSig] int GetPropertyDescriptionList(IntPtr key, ref Guid riid, out IntPtr instance);
        [PreserveSig] int GetAttributes(uint options, uint mask, out uint attributes);
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemAt(uint index, out IShellItem item);
        [PreserveSig] int EnumItems(out IntPtr enumerator);
    }

    // Only the methods this sample calls are named; the rest hold their vtable
    // slots, because the order is the contract.
    [ComImport]
    [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        [PreserveSig] int SetFileTypes(uint count, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] ComDlgFilterSpec[] filters);
        [PreserveSig] int SetFileTypeIndex(uint index);
        [PreserveSig] int GetFileTypeIndex(out uint index);
        [PreserveSig] int Advise(IntPtr events, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOptions(uint options);
        [PreserveSig] int GetOptions(out uint options);
        [PreserveSig] int SetDefaultFolder(IShellItem folder);
        [PreserveSig] int SetFolder(IShellItem folder);
        [PreserveSig] int GetFolder(out IShellItem folder);
        [PreserveSig] int GetCurrentSelection(out IShellItem item);
        [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int GetFileName(out IntPtr name);
        [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        [PreserveSig] int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        [PreserveSig] int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        [PreserveSig] int GetResult(out IShellItem item);
        [PreserveSig] int AddPlace(IShellItem place, int placement);
        [PreserveSig] int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        [PreserveSig] int Close(int result);
        [PreserveSig] int SetClientGuid(ref Guid client);
        [PreserveSig] int ClearClientData();
        [PreserveSig] int SetFilter(IntPtr filter);
        [PreserveSig] int GetResults(out IShellItemArray items);
        [PreserveSig] int GetSelectedItems(out IShellItemArray items);
    }
}
