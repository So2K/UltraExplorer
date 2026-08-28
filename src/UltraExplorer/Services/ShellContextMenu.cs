using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace UltraExplorer.Services;

/// <summary>
/// Shows the real Explorer context menu for one or more items.
///
/// Every PIDL and COM object is created and released here in a fixed order, and
/// the IContextMenu2/3 owner-draw messages are forwarded only while the popup is
/// on screen.  A wrapper that owns these lifetimes implicitly was the source of
/// a 0xC0000374 heap corruption, so the interop is deliberately explicit.
/// </summary>
internal static class ShellContextMenu
{
    private const uint CmdFirst = 0x0001;
    private const uint CmdLast = 0x7FFF;

    private const int WmInitMenuPopup = 0x0117;
    private const int WmDrawItem = 0x002B;
    private const int WmMeasureItem = 0x002C;
    private const int WmMenuChar = 0x0120;

    private const uint CmfNormal = 0x00000000;
    private const uint CmfExtendedVerbs = 0x00000100;

    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmLeftAlign = 0x0000;
    private const uint TpmTopAlign = 0x0000;

    private const int CmicMaskUnicode = 0x00004000;
    private const int CmicMaskPtInvoke = 0x20000000;
    private const int SwShowNormal = 1;

    private static readonly Guid IidShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IidContextMenu = new("000214E4-0000-0000-C000-000000000046");

    /// <summary>
    /// Returns false when the Shell cannot provide a menu for this selection —
    /// for example when the items do not share a parent folder — so the caller
    /// can fall back to its own menu instead of showing nothing.
    /// </summary>
    public static bool TryShow(
        IReadOnlyList<string> paths,
        HwndSource source,
        int screenX,
        int screenY,
        bool extended)
    {
        var existing = paths
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (existing.Length == 0)
        {
            return false;
        }

        // The Shell builds one menu from items of a single parent folder.
        var parentDirectory = Path.GetDirectoryName(existing[0]);
        if (existing.Length > 1
            && existing.Any(path => !string.Equals(Path.GetDirectoryName(path), parentDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var absolutePidls = new List<IntPtr>(existing.Length);
        IShellFolder? parentFolder = null;
        IContextMenu? contextMenu = null;
        IContextMenu2? contextMenu2 = null;
        IContextMenu3? contextMenu3 = null;
        var menuHandle = IntPtr.Zero;
        HwndSourceHook? hook = null;

        try
        {
            foreach (var path in existing)
            {
                if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                {
                    return false;
                }

                absolutePidls.Add(pidl);
            }

            var folderId = IidShellFolder;
            if (SHBindToParent(absolutePidls[0], ref folderId, out parentFolder, out var firstChild) != 0
                || parentFolder is null
                || firstChild == IntPtr.Zero)
            {
                return false;
            }

            // ILFindLastID points inside the absolute PIDL and must not be freed.
            var childPidls = new IntPtr[absolutePidls.Count];
            childPidls[0] = firstChild;
            for (var index = 1; index < absolutePidls.Count; index++)
            {
                childPidls[index] = ILFindLastID(absolutePidls[index]);
                if (childPidls[index] == IntPtr.Zero)
                {
                    return false;
                }
            }

            var menuId = IidContextMenu;
            if (parentFolder.GetUIObjectOf(source.Handle, (uint)childPidls.Length, childPidls, ref menuId, IntPtr.Zero, out contextMenu) != 0
                || contextMenu is null)
            {
                return false;
            }

            menuHandle = CreatePopupMenu();
            if (menuHandle == IntPtr.Zero)
            {
                return false;
            }

            var flags = extended ? CmfNormal | CmfExtendedVerbs : CmfNormal;
            if (contextMenu.QueryContextMenu(menuHandle, 0, CmdFirst, CmdLast, flags) < 0)
            {
                return false;
            }

            contextMenu2 = contextMenu as IContextMenu2;
            contextMenu3 = contextMenu as IContextMenu3;

            // Owner-draw entries (icons, "Open with" thumbnails, cascading
            // shell extensions) only paint if these messages reach the menu.
            var menu2 = contextMenu2;
            var menu3 = contextMenu3;
            hook = (IntPtr _, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                switch (message)
                {
                    case WmInitMenuPopup:
                    case WmDrawItem:
                    case WmMeasureItem:
                    case WmMenuChar:
                        if (menu3 is not null && menu3.HandleMenuMsg2(message, wParam, lParam, out var result) == 0)
                        {
                            handled = true;
                            return result;
                        }

                        if (menu2 is not null && message != WmMenuChar && menu2.HandleMenuMsg(message, wParam, lParam) == 0)
                        {
                            handled = true;
                            return message == WmInitMenuPopup ? IntPtr.Zero : new IntPtr(1);
                        }

                        break;
                }

                return IntPtr.Zero;
            };
            source.AddHook(hook);

            var command = TrackPopupMenuEx(
                menuHandle,
                TpmReturnCmd | TpmRightButton | TpmLeftAlign | TpmTopAlign,
                screenX,
                screenY,
                source.Handle,
                IntPtr.Zero);

            source.RemoveHook(hook);
            hook = null;

            if (command >= CmdFirst && command <= CmdLast)
            {
                Invoke(contextMenu, source.Handle, command - CmdFirst, parentDirectory, screenX, screenY);
            }

            return true;
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            if (hook is not null)
            {
                source.RemoveHook(hook);
            }

            if (menuHandle != IntPtr.Zero)
            {
                DestroyMenu(menuHandle);
            }

            // contextMenu2 and contextMenu3 are QueryInterface aliases of the very
            // same runtime callable wrapper, so they must NOT be released
            // separately: that over-release is itself a double free.
            contextMenu3 = null;
            contextMenu2 = null;
            ReleaseComObject(contextMenu);
            ReleaseComObject(parentFolder);

            foreach (var pidl in absolutePidls)
            {
                ILFree(pidl);
            }
        }
    }

    private static void Invoke(
        IContextMenu contextMenu,
        IntPtr ownerHandle,
        uint commandOffset,
        string? workingDirectory,
        int screenX,
        int screenY)
    {
        var info = new CmInvokeCommandInfoEx
        {
            cbSize = Marshal.SizeOf<CmInvokeCommandInfoEx>(),
            fMask = CmicMaskUnicode | CmicMaskPtInvoke,
            hwnd = ownerHandle,
            lpVerb = new IntPtr(commandOffset),
            lpVerbW = new IntPtr(commandOffset),
            lpDirectory = workingDirectory,
            lpDirectoryW = workingDirectory,
            nShow = SwShowNormal,
            ptInvoke = new Point32 { X = screenX, Y = screenY }
        };

        try
        {
            contextMenu.InvokeCommand(ref info);
        }
        catch (COMException)
        {
            // A shell extension refusing its own verb is not our problem to fix.
        }
    }

    private static void ReleaseComObject(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            // Final release: the wrapper is dead to us either way, and this also
            // covers the extra reference each QueryInterface alias took.
            Marshal.FinalReleaseComObject(instance);
        }
    }

    // ---- interop -----------------------------------------------------------

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string displayName, ref uint eaten, out IntPtr pidl, ref uint attributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int flags, out IntPtr enumIdList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint attributes);

        [PreserveSig]
        int GetUIObjectOf(
            IntPtr hwndOwner,
            uint cidl,
            [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
            ref Guid riid,
            IntPtr rgfReserved,
            [MarshalAs(UnmanagedType.Interface)] out IContextMenu ppv);

        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr name);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr pidlOut);
    }

    [ComImport]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CmInvokeCommandInfoEx info);
        [PreserveSig] int GetCommandString(IntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint max);
    }

    [ComImport]
    [Guid("000214F4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CmInvokeCommandInfoEx info);
        [PreserveSig] int GetCommandString(IntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint max);
        [PreserveSig] int HandleMenuMsg(int message, IntPtr wParam, IntPtr lParam);
    }

    [ComImport]
    [Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CmInvokeCommandInfoEx info);
        [PreserveSig] int GetCommandString(IntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint max);
        [PreserveSig] int HandleMenuMsg(int message, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(int message, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct CmInvokeCommandInfoEx
    {
        public int cbSize;
        public int fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow;
        public int dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
        public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
        public Point32 ptInvoke;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellFolder parent, out IntPtr lastChild);

    [DllImport("shell32.dll", EntryPoint = "ILFindLastID")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll", EntryPoint = "ILFree")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);
}
