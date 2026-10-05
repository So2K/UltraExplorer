using System.Runtime.InteropServices;
using System.Text;
using UltraExplorer.Picker.Com;

namespace UltraExplorer.Services;

/// <summary>Documented out-of-process Shell interfaces. Vtable methods preceding
/// the read methods are retained in their SDK order; none changes a source window.</summary>
internal static class ExplorerWindowInterop
{
    internal static readonly Guid TopLevelBrowser = new("4c96be40-915c-11cf-99d3-00aa004ae837");
    internal static readonly Guid ShellBrowserId = new("000214e2-0000-0000-c000-000000000046");
    internal static readonly Guid PersistFolderId = new("1ac3d9f0-175c-11d1-95be-00609797ea4f");
    internal static readonly Guid ShellItemArrayId = typeof(IShellItemArray).GUID;
    internal static readonly Guid ShellItemId = typeof(IShellItem).GUID;
    internal const uint FileSystem = 0x40000000;
    internal const uint Folder = 0x20000000;
    internal const uint Selection = 1;

    [ComImport, Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ServiceProvider
    {
        [PreserveSig] int QueryService(in Guid service, in Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }

    [ComImport, Guid("000214e2-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ShellBrowser
    {
        [PreserveSig] int GetWindow(out nint window);
        [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enter);
        [PreserveSig] int InsertMenus(nint menu, nint widths);
        [PreserveSig] int SetMenu(nint menu, nint oleMenu, nint activeWindow);
        [PreserveSig] int RemoveMenus(nint menu);
        [PreserveSig] int SetStatusText(nint text);
        [PreserveSig] int EnableModeless([MarshalAs(UnmanagedType.Bool)] bool enable);
        [PreserveSig] int TranslateAccelerator(nint message, ushort id);
        [PreserveSig] int BrowseObject(nint pidl, uint flags);
        [PreserveSig] int GetViewStateStream(uint mode, out nint stream);
        [PreserveSig] int GetControlWindow(uint id, out nint window);
        [PreserveSig] int SendControlMessage(uint id, uint message, nuint wParam, nint lParam, out nint result);
        [PreserveSig] int QueryActiveShellView(out ShellView view);
        [PreserveSig] int OnViewWindowActive(ShellView view);
        [PreserveSig] int SetToolbarItems(nint buttons, uint count, uint flags);
    }

    [ComImport, Guid("000214e3-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ShellView
    {
        [PreserveSig] int GetWindow(out nint window);
        [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enter);
        [PreserveSig] int TranslateAccelerator(nint message);
        [PreserveSig] int EnableModeless([MarshalAs(UnmanagedType.Bool)] bool enable);
        [PreserveSig] int UIActivate(uint state);
        [PreserveSig] int Refresh();
        [PreserveSig] int CreateViewWindow(nint previous, nint settings, nint browser, nint rect, out nint window);
        [PreserveSig] int DestroyViewWindow();
        [PreserveSig] int GetCurrentInfo(nint settings);
        [PreserveSig] int AddPropertySheetPages(uint reserved, nint callback, nint lParam);
        [PreserveSig] int SaveViewState();
        [PreserveSig] int SelectItem(nint pidl, uint flags);
        [PreserveSig] int GetItemObject(uint flags, in Guid iid, out nint result);
    }

    [ComImport, Guid("cde725b0-ccc9-4519-917e-325d72fab4ce"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface FolderView
    {
        [PreserveSig] int GetCurrentViewMode(out uint mode);
        [PreserveSig] int SetCurrentViewMode(uint mode);
        [PreserveSig] int GetFolder(in Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object folder);
        [PreserveSig] int Item(int index, out nint pidl);
        [PreserveSig] int ItemCount(uint flags, out int count);
        [PreserveSig] int Items(uint flags, in Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object items);
        [PreserveSig] int GetSelectionMarkedItem(out int item);
        [PreserveSig] int GetFocusedItem(out int item);
        [PreserveSig] int GetItemPosition(nint pidl, nint point);
        [PreserveSig] int GetSpacing(nint point);
        [PreserveSig] int GetDefaultSpacing(nint point);
        [PreserveSig] int GetAutoArrange();
        [PreserveSig] int SelectItem(int item, uint flags);
        [PreserveSig] int SelectAndPositionItems(uint count, nint items, nint points, uint flags);
    }

    [ComImport, Guid("1ac3d9f0-175c-11d1-95be-00609797ea4f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface PersistFolder2
    {
        [PreserveSig] int GetClassID(out Guid id);
        [PreserveSig] int Initialize(nint pidl);
        [PreserveSig] int GetCurFolder(out nint pidl);
    }

    internal delegate void WinEventCallback(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time);
    internal delegate bool EnumWindowCallback(nint window, nint parameter);

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        internal uint Size, Flags;
        internal nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal int CaretLeft, CaretTop, CaretRight, CaretBottom;
    }

    [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint minimum, uint maximum, nint module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] internal static extern nint GetLastActivePopup(nint window);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumChildWindows(nint window, EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("shell32.dll")] internal static extern uint ILGetSize(nint pidl);
    [DllImport("shell32.dll")] internal static extern nint ILCombine(nint parent, nint child);
    [DllImport("shell32.dll")] internal static extern int SHCreateItemFromIDList(nint pidl, in Guid iid, out IShellItem item);

    internal static string ClassName(nint window)
    {
        var text = new StringBuilder(256);
        return GetClassName(window, text, text.Capacity) > 0 ? text.ToString() : string.Empty;
    }

    internal static void Release(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.ReleaseComObject(value); }
        catch (InvalidComObjectException) { }
    }
}
