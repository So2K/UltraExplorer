using System.Runtime.InteropServices;

namespace UltraExplorer.Services;

// Independently declared ABI bindings to exdisp.h and shobjidl_core.h. Dual
// interfaces include the runtime's IDispatch slots; methods below retain SDK
// vtable order and HRESULTs. No implementation from another file manager.
[ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IFolderShellWindows
{
    [PreserveSig] int get_Count(out int count);
    [PreserveSig] int Item([MarshalAs(UnmanagedType.Struct)] object index, [MarshalAs(UnmanagedType.IDispatch)] out object? window);
    [PreserveSig] int _NewEnum([MarshalAs(UnmanagedType.IUnknown)] out object? enumerator);
    [PreserveSig] int Register([MarshalAs(UnmanagedType.IDispatch)] object window, int hwnd, int kind, out int cookie);
    [PreserveSig] int RegisterPending(int thread, [MarshalAs(UnmanagedType.Struct)] ref object location, [MarshalAs(UnmanagedType.Struct)] ref object? root, int kind, out int cookie);
    [PreserveSig] int Revoke(int cookie);
    [PreserveSig] int OnNavigate(int cookie, [MarshalAs(UnmanagedType.Struct)] ref object location);
    [PreserveSig] int OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool active);
    [PreserveSig] int FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object location, [MarshalAs(UnmanagedType.Struct)] ref object? root, int kind, out int hwnd, int flags, [MarshalAs(UnmanagedType.IDispatch)] out object? window);
    [PreserveSig] int OnCreated(int cookie, [MarshalAs(UnmanagedType.IUnknown)] object window);
    [PreserveSig] int ProcessAttachDetach([MarshalAs(UnmanagedType.VariantBool)] bool attach);
}

[ComVisible(true), Guid("0002DF05-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IFolderWebBrowserApp
{
    [PreserveSig] int GoBack();
    [PreserveSig] int GoForward();
    [PreserveSig] int GoHome();
    [PreserveSig] int GoSearch();
    [PreserveSig] int Navigate([MarshalAs(UnmanagedType.BStr)] string url, nint flags, nint frame, nint data, nint headers);
    [PreserveSig] int Refresh();
    [PreserveSig] int Refresh2(nint level);
    [PreserveSig] int Stop();
    [PreserveSig] int get_Application([MarshalAs(UnmanagedType.IDispatch)] out object? value);
    [PreserveSig] int get_Parent([MarshalAs(UnmanagedType.IDispatch)] out object? value);
    [PreserveSig] int get_Container([MarshalAs(UnmanagedType.IDispatch)] out object? value);
    [PreserveSig] int get_Document([MarshalAs(UnmanagedType.IDispatch)] out object? value);
    [PreserveSig] int get_TopLevelContainer([MarshalAs(UnmanagedType.VariantBool)] out bool value);
    [PreserveSig] int get_Type([MarshalAs(UnmanagedType.BStr)] out string? value);
    [PreserveSig] int get_Left(out int value);
    [PreserveSig] int put_Left(int value);
    [PreserveSig] int get_Top(out int value);
    [PreserveSig] int put_Top(int value);
    [PreserveSig] int get_Width(out int value);
    [PreserveSig] int put_Width(int value);
    [PreserveSig] int get_Height(out int value);
    [PreserveSig] int put_Height(int value);
    [PreserveSig] int get_LocationName([MarshalAs(UnmanagedType.BStr)] out string? value);
    [PreserveSig] int get_LocationURL([MarshalAs(UnmanagedType.BStr)] out string? value);
    [PreserveSig] int get_Busy([MarshalAs(UnmanagedType.VariantBool)] out bool value);
    [PreserveSig] int Quit();
    [PreserveSig] int ClientToWindow(ref int width, ref int height);
    [PreserveSig] int PutProperty([MarshalAs(UnmanagedType.BStr)] string name, [MarshalAs(UnmanagedType.Struct)] object value);
    [PreserveSig] int GetProperty([MarshalAs(UnmanagedType.BStr)] string name, [MarshalAs(UnmanagedType.Struct)] out object? value);
    [PreserveSig] int get_Name([MarshalAs(UnmanagedType.BStr)] out string? value);
    [PreserveSig] int get_HWND(out nint value);
    [PreserveSig] int get_FullName([MarshalAs(UnmanagedType.BStr)] out string? value);
    [PreserveSig] int get_Path([MarshalAs(UnmanagedType.BStr)] out string? value);
    [PreserveSig] int get_Visible([MarshalAs(UnmanagedType.VariantBool)] out bool value);
    [PreserveSig] int put_Visible([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [PreserveSig] int get_StatusBar([MarshalAs(UnmanagedType.VariantBool)] out bool value);
    [PreserveSig] int put_StatusBar([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [PreserveSig] int get_StatusText([MarshalAs(UnmanagedType.BStr)] out string? value);
    [PreserveSig] int put_StatusText([MarshalAs(UnmanagedType.BStr)] string value);
    [PreserveSig] int get_ToolBar(out int value);
    [PreserveSig] int put_ToolBar(int value);
    [PreserveSig] int get_MenuBar([MarshalAs(UnmanagedType.VariantBool)] out bool value);
    [PreserveSig] int put_MenuBar([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [PreserveSig] int get_FullScreen([MarshalAs(UnmanagedType.VariantBool)] out bool value);
    [PreserveSig] int put_FullScreen([MarshalAs(UnmanagedType.VariantBool)] bool value);
}

[ComVisible(true), Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IFolderServiceProvider
{
    [PreserveSig] int QueryService(ref Guid service, ref Guid requestedInterface, out nint result);
}

[ComVisible(true), Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IFolderShellView
{
    [PreserveSig] int GetWindow(out nint window);
    [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enter);
    [PreserveSig] int TranslateAccelerator(nint message);
    [PreserveSig] int EnableModeless([MarshalAs(UnmanagedType.Bool)] bool enable);
    [PreserveSig] int UIActivate(uint state);
    [PreserveSig] int Refresh();
    [PreserveSig] int CreateViewWindow(nint previous, nint settings, nint browser, nint rectangle, out nint window);
    [PreserveSig] int DestroyViewWindow();
    [PreserveSig] int GetCurrentInfo(nint settings);
    [PreserveSig] int AddPropertySheetPages(uint reserved, nint callback, nint parameter);
    [PreserveSig] int SaveViewState();
    [PreserveSig] int SelectItem(nint relativeItem, uint flags);
    [PreserveSig] int GetItemObject(uint item, ref Guid requestedInterface, out nint result);
}

internal static class FolderShellNative
{
    internal const int Ok = 0, NotImplemented = unchecked((int)0x80004001), NoInterface = unchecked((int)0x80004002), Fail = unchecked((int)0x80004005);
    internal static readonly Guid FolderView = new("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
    internal static readonly Guid ShellView = new("000214E3-0000-0000-C000-000000000046");
    internal static readonly Guid ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

    /// <summary>The object's server has gone - explorer.exe ended or restarted -
    /// so this proxy refuses every call from now on: RPC_E_DISCONNECTED,
    /// RPC_E_SERVER_DIED(_DNE), RPC_S_SERVER_UNAVAILABLE, RPC_S_CALL_FAILED(_DNE)
    /// and CO_E_OBJNOTCONNECTED.</summary>
    internal static bool IsDisconnected(int result) => unchecked((uint)result)
        is 0x80010108 or 0x80010007 or 0x80010012 or 0x800706BA or 0x800706BE or 0x800706BF or 0x800401FD;

    internal static object PidlVariant(nint pidl)
    {
        var size = ILGetSize(pidl);
        if (size < 2 || size > 1024 * 1024) throw new ArgumentException("Invalid folder PIDL.");
        var bytes = new byte[size];
        Marshal.Copy(pidl, bytes, 0, bytes.Length);
        return bytes;
    }

    internal static string? FileSystemPath(nint pidl)
    {
        if (SHGetNameFromIDList(pidl, 0x80058000, out var text) < 0 || text == 0) return null;
        try { return Marshal.PtrToStringUni(text); }
        finally { Marshal.FreeCoTaskMem(text); }
    }

    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern int SHParseDisplayName(string name, nint context, out nint pidl, uint requestedAttributes, out uint attributes);
    [DllImport("shell32.dll")] internal static extern uint ILGetSize(nint pidl);
    [DllImport("shell32.dll")] internal static extern nint ILCombine(nint parent, nint child);
    [DllImport("shell32.dll")] private static extern int SHGetNameFromIDList(nint pidl, uint kind, out nint name);
}
