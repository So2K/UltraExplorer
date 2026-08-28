using System.Runtime.InteropServices;

namespace UltraExplorer.Picker.Com;

/// <summary>
/// The dialog interfaces from <c>shobjidl_core.h</c>, declared so UltraExplorer
/// can implement them rather than call them.  Vtable order is the contract, so
/// every method is present and in the order the SDK declares it, including the
/// ones that answer <c>E_NOTIMPL</c>.
/// </summary>
internal static class Hresult
{
    public const int Ok = 0;
    public const int False = 1;
    public const int NotImplemented = unchecked((int)0x80004001);
    public const int NoInterface = unchecked((int)0x80004002);
    public const int InvalidArg = unchecked((int)0x80070057);
    public const int OutOfMemory = unchecked((int)0x8007000E);
    public const int Fail = unchecked((int)0x80004005);

    /// <summary>What <c>IFileDialog::Show</c> returns when the user cancels.</summary>
    public const int Cancelled = unchecked((int)0x800704C7);

    public const int ClassNotAvailable = unchecked((int)0x80040111);
    public const int NonNullAggregation = unchecked((int)0x80040110);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ComDlgFilterSpec
{
    [MarshalAs(UnmanagedType.LPWStr)] public string Name;
    [MarshalAs(UnmanagedType.LPWStr)] public string Spec;
}

internal enum ShellItemDisplayName : uint
{
    /// <summary>SIGDN_FILESYSPATH.</summary>
    FileSystemPath = 0x80058000,

    /// <summary>SIGDN_DESKTOPABSOLUTEPARSING.</summary>
    DesktopAbsoluteParsing = 0x80028000
}

internal enum FileDialogAddPlacement
{
    Bottom = 0,
    Top = 1
}

internal enum ShareViolationResponse
{
    Default = 0,
    Accept = 1,
    Refuse = 2
}

internal enum OverwriteResponse
{
    Default = 0,
    Accept = 1,
    Refuse = 2
}

[ComImport]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr instance);

    [PreserveSig] int GetParent(out IShellItem parent);

    [PreserveSig] int GetDisplayName(ShellItemDisplayName kind, out IntPtr name);

    [PreserveSig] int GetAttributes(uint mask, out uint attributes);

    [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
}

[ComImport]
[Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemArray
{
    [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr instance);

    [PreserveSig] int GetPropertyStore(uint flags, ref Guid riid, out IntPtr instance);

    [PreserveSig] int GetPropertyDescriptionList(IntPtr key, ref Guid riid, out IntPtr instance);

    [PreserveSig] int GetAttributes(uint options, uint mask, out uint attributes);

    [PreserveSig] int GetCount(out uint count);

    [PreserveSig] int GetItemAt(uint index, out IShellItem item);

    [PreserveSig] int EnumItems(out IntPtr enumerator);
}

[ComImport]
[Guid("b4db1657-70d7-485e-8e3e-6fcb5a5c1802")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IModalWindow
{
    [PreserveSig] int Show(IntPtr owner);
}

[ComImport]
[Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileDialog : IModalWindow
{
    [PreserveSig] new int Show(IntPtr owner);

    [PreserveSig] int SetFileTypes(
        uint count,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] ComDlgFilterSpec[] filters);

    [PreserveSig] int SetFileTypeIndex(uint index);

    [PreserveSig] int GetFileTypeIndex(out uint index);

    [PreserveSig] int Advise(IFileDialogEvents events, out uint cookie);

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

    [PreserveSig] int AddPlace(IShellItem place, FileDialogAddPlacement placement);

    [PreserveSig] int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);

    [PreserveSig] int Close(int result);

    [PreserveSig] int SetClientGuid(ref Guid client);

    [PreserveSig] int ClearClientData();

    /// <summary>Deprecated in the SDK itself; answers E_NOTIMPL.</summary>
    [PreserveSig] int SetFilter(IntPtr filter);
}

[ComImport]
[Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOpenDialog : IFileDialog
{
    [PreserveSig] new int Show(IntPtr owner);

    [PreserveSig] new int SetFileTypes(
        uint count,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] ComDlgFilterSpec[] filters);

    [PreserveSig] new int SetFileTypeIndex(uint index);

    [PreserveSig] new int GetFileTypeIndex(out uint index);

    [PreserveSig] new int Advise(IFileDialogEvents events, out uint cookie);

    [PreserveSig] new int Unadvise(uint cookie);

    [PreserveSig] new int SetOptions(uint options);

    [PreserveSig] new int GetOptions(out uint options);

    [PreserveSig] new int SetDefaultFolder(IShellItem folder);

    [PreserveSig] new int SetFolder(IShellItem folder);

    [PreserveSig] new int GetFolder(out IShellItem folder);

    [PreserveSig] new int GetCurrentSelection(out IShellItem item);

    [PreserveSig] new int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);

    [PreserveSig] new int GetFileName(out IntPtr name);

    [PreserveSig] new int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);

    [PreserveSig] new int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);

    [PreserveSig] new int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);

    [PreserveSig] new int GetResult(out IShellItem item);

    [PreserveSig] new int AddPlace(IShellItem place, FileDialogAddPlacement placement);

    [PreserveSig] new int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);

    [PreserveSig] new int Close(int result);

    [PreserveSig] new int SetClientGuid(ref Guid client);

    [PreserveSig] new int ClearClientData();

    [PreserveSig] new int SetFilter(IntPtr filter);

    [PreserveSig] int GetResults(out IShellItemArray items);

    [PreserveSig] int GetSelectedItems(out IShellItemArray items);
}

[ComImport]
[Guid("84bccd23-5fde-4cdb-aea4-af64b83d78ab")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileSaveDialog : IFileDialog
{
    [PreserveSig] new int Show(IntPtr owner);

    [PreserveSig] new int SetFileTypes(
        uint count,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] ComDlgFilterSpec[] filters);

    [PreserveSig] new int SetFileTypeIndex(uint index);

    [PreserveSig] new int GetFileTypeIndex(out uint index);

    [PreserveSig] new int Advise(IFileDialogEvents events, out uint cookie);

    [PreserveSig] new int Unadvise(uint cookie);

    [PreserveSig] new int SetOptions(uint options);

    [PreserveSig] new int GetOptions(out uint options);

    [PreserveSig] new int SetDefaultFolder(IShellItem folder);

    [PreserveSig] new int SetFolder(IShellItem folder);

    [PreserveSig] new int GetFolder(out IShellItem folder);

    [PreserveSig] new int GetCurrentSelection(out IShellItem item);

    [PreserveSig] new int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);

    [PreserveSig] new int GetFileName(out IntPtr name);

    [PreserveSig] new int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);

    [PreserveSig] new int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);

    [PreserveSig] new int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);

    [PreserveSig] new int GetResult(out IShellItem item);

    [PreserveSig] new int AddPlace(IShellItem place, FileDialogAddPlacement placement);

    [PreserveSig] new int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);

    [PreserveSig] new int Close(int result);

    [PreserveSig] new int SetClientGuid(ref Guid client);

    [PreserveSig] new int ClearClientData();

    [PreserveSig] new int SetFilter(IntPtr filter);

    [PreserveSig] int SetSaveAsItem(IShellItem item);

    /// <summary>Property-store methods; this dialog edits no metadata.</summary>
    [PreserveSig] int SetProperties(IntPtr store);

    [PreserveSig] int SetCollectedProperties(IntPtr list, [MarshalAs(UnmanagedType.Bool)] bool appendDefault);

    [PreserveSig] int GetProperties(out IntPtr store);

    [PreserveSig] int ApplyProperties(IShellItem item, IntPtr store, IntPtr owner, IntPtr sink);
}

[ComImport]
[Guid("973510db-7d7f-452b-8975-74a85828d354")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileDialogEvents
{
    [PreserveSig] int OnFileOk(IFileDialog dialog);

    [PreserveSig] int OnFolderChanging(IFileDialog dialog, IShellItem folder);

    [PreserveSig] int OnFolderChange(IFileDialog dialog);

    [PreserveSig] int OnSelectionChange(IFileDialog dialog);

    [PreserveSig] int OnShareViolation(IFileDialog dialog, IShellItem item, out ShareViolationResponse response);

    [PreserveSig] int OnTypeChange(IFileDialog dialog);

    [PreserveSig] int OnOverwrite(IFileDialog dialog, IShellItem item, out OverwriteResponse response);
}

[ComImport]
[Guid("00000001-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IClassFactory
{
    [PreserveSig] int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr instance);

    [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool @lock);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct Win32FindDataW
{
    public uint FileAttributes;
    public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint Reserved0;
    public uint Reserved1;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
}

/// <summary>
/// The shell's own way of naming a file that is not there yet: hand the parser
/// the find data instead of letting it look on disk.
/// </summary>
[ComImport]
[Guid("01e18d10-4d8b-11d2-855d-006008059367")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileSystemBindData
{
    [PreserveSig] int SetFindData(ref Win32FindDataW findData);

    [PreserveSig] int GetFindData(out Win32FindDataW findData);
}

internal sealed class FileSystemBindData : IFileSystemBindData
{
    private Win32FindDataW _findData = new()
    {
        FileAttributes = 0x80, // FILE_ATTRIBUTE_NORMAL
        FileName = string.Empty,
        AlternateFileName = string.Empty
    };

    public int SetFindData(ref Win32FindDataW findData)
    {
        _findData = findData;
        return Hresult.Ok;
    }

    public int GetFindData(out Win32FindDataW findData)
    {
        findData = _findData;
        return Hresult.Ok;
    }
}

internal static class ShellNative
{
    public static readonly Guid ShellItemId = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern IShellItem SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid riid);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern IShellItem SHCreateItemFromParsingName(
        string path,
        System.Runtime.InteropServices.ComTypes.IBindCtx bindContext,
        ref Guid riid);

    [DllImport("ole32.dll", PreserveSig = false)]
    public static extern void CreateBindCtx(
        uint reserved,
        out System.Runtime.InteropServices.ComTypes.IBindCtx bindContext);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr ILCreateFromPath(string path);

    [DllImport("shell32.dll")]
    public static extern void ILFree(IntPtr idList);

    [DllImport("shell32.dll", PreserveSig = false)]
    public static extern IShellItemArray SHCreateShellItemArrayFromIDLists(
        uint count,
        [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] idLists);

    [DllImport("ole32.dll")]
    public static extern int CoRegisterClassObject(
        ref Guid classId,
        IntPtr unknown,
        uint context,
        uint flags,
        out uint cookie);

    [DllImport("ole32.dll")]
    public static extern int CoRevokeClassObject(uint cookie);

    [DllImport("ole32.dll")]
    public static extern int CoResumeClassObjects();

    [DllImport("user32.dll")]
    public static extern bool EnableWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool IsWindowEnabled(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);

    public const uint ClsCtxLocalServer = 0x4;
    public const uint RegClsMultipleUse = 1;
    public const uint RegClsSuspended = 4;

    /// <summary>Reads a shell item's file-system path, or null if it has none.</summary>
    public static string? PathOf(IShellItem? item)
    {
        if (item is null)
        {
            return null;
        }

        var buffer = IntPtr.Zero;
        try
        {
            if (item.GetDisplayName(ShellItemDisplayName.FileSystemPath, out buffer) != Hresult.Ok
                || buffer == IntPtr.Zero)
            {
                return null;
            }

            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }
    }

    /// <summary>
    /// A shell item for a path, whether or not anything is there yet.  Parsing
    /// only works for something that exists, and the whole point of a save
    /// dialog is to name a file that does not - so a missing item is built
    /// relative to the folder that will hold it.
    /// </summary>
    public static IShellItem? ItemFor(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var id = ShellItemId;
        try
        {
            return SHCreateItemFromParsingName(path, IntPtr.Zero, ref id);
        }
        catch (Exception exception) when (exception is COMException or FileNotFoundException
            or DirectoryNotFoundException or ArgumentException)
        {
            // Fall through and try the parent.
        }

        // Nothing is there yet, so parse it with find data supplied instead of
        // read: this is what lets a Save As dialog name a file that does not
        // exist, and it is the shell's own mechanism for it.
        try
        {
            CreateBindCtx(0, out var bindContext);
            bindContext.RegisterObjectParam("File System Bind Data", new FileSystemBindData());
            return SHCreateItemFromParsingName(path, bindContext, ref id);
        }
        catch (Exception exception)
        {
            ComTrace.Write($"ItemFor({path}) failed: {exception.GetType().Name} {exception.Message}");
            return null;
        }
    }

    public static IShellItemArray? ArrayFor(IReadOnlyList<string> paths)
    {
        var idLists = new IntPtr[paths.Count];
        try
        {
            for (var index = 0; index < paths.Count; index++)
            {
                idLists[index] = ILCreateFromPath(paths[index]);
                if (idLists[index] == IntPtr.Zero)
                {
                    return null;
                }
            }

            return SHCreateShellItemArrayFromIDLists((uint)idLists.Length, idLists);
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            foreach (var idList in idLists)
            {
                if (idList != IntPtr.Zero)
                {
                    ILFree(idList);
                }
            }
        }
    }
}
