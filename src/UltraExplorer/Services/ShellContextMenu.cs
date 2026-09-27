using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace UltraExplorer.Services;

/// <summary>
/// One item the app puts on a menu: a command, a switch, a submenu or a line
/// between groups.  The same entries make the Shell's menu (appended to it
/// by <see cref="ShellContextMenu"/>) and the app's own WPF menu, so the two
/// can never drift apart.
/// </summary>
/// <param name="Label">What the item says.  An ampersand is shown as one; it is not a mnemonic.</param>
/// <param name="Run">What choosing it does, when <see cref="Command"/> is not set.</param>
/// <param name="Children">A non-empty list makes the item a submenu, which runs nothing itself.</param>
public sealed record ShellMenuEntry(string Label, Action? Run = null, IReadOnlyList<ShellMenuEntry>? Children = null)
{
    /// <summary>A line between two groups of items.</summary>
    public static ShellMenuEntry Separator { get; } = new(string.Empty) { IsSeparator = true };

    public bool IsSeparator { get; init; }

    /// <summary>Null for a plain command; otherwise a switch, ticked when true.</summary>
    public bool? Checked { get; init; }

    /// <summary>One choice of several: the Shell's menu marks it with a dot rather than a tick.</summary>
    public bool IsRadio { get; init; }

    public bool IsEnabled { get; init; } = true;

    /// <summary>The shortcut, or a word such as "Current", shown at the right of the item.</summary>
    public string Shortcut { get; init; } = string.Empty;

    /// <summary>A Segoe Fluent Icons glyph shown beside the item.</summary>
    public string? Glyph { get; init; }

    /// <summary>A colour swatch shown beside the item: "#RRGGBB", or empty for "no colour".</summary>
    public string? Swatch { get; init; }

    public string? ToolTip { get; init; }

    /// <summary>A command run instead of <see cref="Run"/>; whether it can run enables the item.</summary>
    public ICommand? Command { get; init; }

    /// <summary>Whether the item can be chosen now: its own say, and its command's.</summary>
    public bool CanRun => IsEnabled && (Command?.CanExecute(null) ?? true);

    /// <summary>Does what the item is for.</summary>
    public void Execute()
    {
        if (Command is { } command)
        {
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }

            return;
        }

        Run?.Invoke();
    }
}

/// <summary>What was picked on a Shell menu: nothing, one of the Shell's own items, or one of the app's.</summary>
internal enum ShellMenuPick
{
    None,
    Shell,
    App
}

/// <summary>
/// What <see cref="ShellContextMenu.Show"/> handed back: the item picked, the
/// Shell's name for it (<c>open</c>, <c>delete</c>, <c>rename</c>, <c>.txt</c>...)
/// and whether it makes a new item, as the entries of New do.
/// </summary>
internal readonly record struct ShellMenuChoice(ShellMenuPick Pick, uint Id, string Verb, bool IsNew, ShellMenuEntry? Entry)
{
    public static ShellMenuChoice Nothing { get; } = new(ShellMenuPick.None, 0, string.Empty, false, null);
}

/// <summary>
/// The real Explorer context menu - for one or more items, or for the empty
/// part of a folder - built but not yet shown, so it can be made while the
/// mouse button is still down and shown the moment it comes up.
///
/// Built, shown, invoked and released on one thread: the Shell's menu
/// handlers belong to the apartment that made them, and the messages that
/// fill their submenus and draw their icons arrive on the thread that shows
/// the menu.  Every PIDL and COM object is created and released here in a
/// fixed order - a wrapper that owned these lifetimes implicitly was once the
/// source of a 0xC0000374 heap corruption, so the interop stays explicit.
///
/// Three kinds of item share the menu handle, told apart by their ids: the
/// Shell's (<see cref="ShellFirst"/> to <see cref="ShellLast"/>), those of a
/// New submenu the app adds for a folder (<see cref="NewFirst"/> to
/// <see cref="NewLast"/>, from Windows' own New menu handler, which fills it
/// from the ShellNew templates), and the app's own from
/// <see cref="AppCommandFirst"/>, which never reach the Shell.  The messages
/// that fill and draw a submenu go to the handler whose submenu or item it is,
/// and only to it: the New handler fills whichever submenu it is handed.
/// </summary>
internal sealed class ShellContextMenu : IDisposable
{
    private const uint ShellFirst = 0x0001;
    private const uint ShellLast = 0x6FFF;
    private const uint NewFirst = 0x7000;
    private const uint NewLast = 0x7FFF;

    /// <summary>Ids at or above this belong to the app, not to the Shell, and come back to the caller untouched.</summary>
    public const uint AppCommandFirst = 0x8000;

    private const uint AppCommandLast = 0xEFFF;

    /// <summary>
    /// For tests and the test copy's measurements: called with the menu handle
    /// the moment the menu is handed to Windows to show.
    /// </summary>
    internal static Action<IntPtr>? Tracking;

    private static readonly Guid IidShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IidContextMenu = new("000214E4-0000-0000-C000-000000000046");
    private static readonly Guid ClsidNewMenu = new("D969A300-E7FF-11d0-A93B-00A0C90F2719");

    /// <summary>The Shell's own verbs that open a folder, after which a folder's New goes.</summary>
    private static readonly HashSet<string> OpenVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "opennewprocess", "opennewwindow", "opennewtab", "explore", "pintohome", "find"
    };

    private static bool? _backgroundHasNew;

    private readonly IntPtr _owner;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<uint, ShellMenuEntry> _entries = [];
    private readonly HashSet<IntPtr> _appPopups = [];
    private readonly List<IntPtr> _bitmaps = [];
    private readonly Dictionary<IntPtr, bool> _newPopups = [];
    private IContextMenu? _shell;
    private IContextMenu2? _shell2;
    private IContextMenu3? _shell3;
    private IContextMenu? _new;
    private IContextMenu2? _new2;
    private IContextMenu3? _new3;
    private IntPtr _handle;
    private IntPtr _newPopup;
    private uint _nextAppId = AppCommandFirst;
    private bool _disposed;

    private ShellContextMenu(IntPtr owner, IReadOnlyList<string> paths, bool isBackground, bool extended)
    {
        _owner = owner;
        Paths = paths;
        IsBackground = isBackground;
        IsExtended = extended;
    }

    /// <summary>The items the menu is for, or the one folder whose open space it is.</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>Whether this is the menu of a folder's open space rather than of items.</summary>
    public bool IsBackground { get; }

    /// <summary>Whether it was built with Shift held: the Shell's extended verbs are on it.</summary>
    public bool IsExtended { get; }

    /// <summary>The folder its New makes things in: the folder it is for, or null.</summary>
    public string? NewTarget { get; private set; }

    /// <summary>The menu handle, for tests to read.</summary>
    public IntPtr Handle => _handle;

    /// <summary>A New submenu the app added (a folder item's), or zero.</summary>
    public IntPtr NewPopup => _newPopup;

    /// <summary>How long building it took.</summary>
    public TimeSpan BuildTime { get; private set; }

    /// <summary>How long each step of building it took, in words: for measuring.</summary>
    public string BuildSteps { get; private set; } = string.Empty;

    /// <summary>Whether it was built on the thread asking - the only thread that may show it.</summary>
    public bool IsOnThisThread => _thread == Environment.CurrentManagedThreadId;

    /// <summary>
    /// The menu Explorer shows for items, or null when the Shell cannot make
    /// one - the items gone, or not in one folder - so the caller can fall
    /// back to its own.  Nothing is looked up on disk first: the Shell's own
    /// parsing is what finds an item gone.
    /// </summary>
    /// <param name="offerNew">Adds a New submenu near the top when the one item is a folder.</param>
    public static ShellContextMenu? ForItems(IReadOnlyList<string> paths, IntPtr owner, bool extended, bool offerNew)
    {
        var distinct = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (distinct.Length == 0)
        {
            return null;
        }

        // The Shell builds one menu from items of a single parent folder.
        var parentDirectory = Path.GetDirectoryName(distinct[0]);
        for (var index = 1; index < distinct.Length; index++)
        {
            if (!string.Equals(Path.GetDirectoryName(distinct[index]), parentDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        var watch = Stopwatch.StartNew();
        var menu = new ShellContextMenu(owner, distinct, isBackground: false, extended);
        var handedBack = false;
        var absolute = IntPtr.Zero;
        var others = new List<IntPtr>();
        IShellFolder? parent = null;
        try
        {
            if (SHParseDisplayName(distinct[0], IntPtr.Zero, out absolute, 0, out _) != 0 || absolute == IntPtr.Zero)
            {
                return null;
            }

            var folderId = IidShellFolder;
            if (SHBindToParent(absolute, ref folderId, out parent, out var first) != 0 || parent is null || first == IntPtr.Zero)
            {
                return null;
            }

            // The rest by name inside the parent the first one gave: one level
            // parsed, not the whole path again from the desktop down for each.
            // What has no name there - a drive in This PC - is parsed whole,
            // and its last part used.  ILFindLastID points inside the absolute
            // PIDL, which is what gets freed.
            var children = new IntPtr[distinct.Length];
            children[0] = first;
            for (var index = 1; index < distinct.Length; index++)
            {
                if (ParseChild(parent, owner, distinct[index]) is { } child)
                {
                    others.Add(child);
                    children[index] = child;
                    continue;
                }

                if (SHParseDisplayName(distinct[index], IntPtr.Zero, out var whole, 0, out _) != 0 || whole == IntPtr.Zero)
                {
                    return null;
                }

                others.Add(whole);
                children[index] = ILFindLastID(whole);
            }

            var parsed = watch.Elapsed.TotalMilliseconds;
            var menuId = IidContextMenu;
            if (parent.GetUIObjectOf(owner, (uint)children.Length, children, ref menuId, IntPtr.Zero, out var context) != 0 || context is null)
            {
                return null;
            }

            var made = watch.Elapsed.TotalMilliseconds;
            menu.TakeShellMenu(context);
            var flags = CmfNormal | CmfCanRename | (extended ? CmfExtendedVerbs : 0);
            if (!menu.Query(flags))
            {
                return null;
            }

            var queried = watch.Elapsed.TotalMilliseconds;
            if (offerNew && distinct.Length == 1 && IsFileSystemFolder(parent, first))
            {
                menu.AddNewMenu(absolute, menu.PositionAfterOpen());
                menu.NewTarget = menu._newPopup != IntPtr.Zero ? distinct[0] : null;
            }

            menu.BuildTime = watch.Elapsed;
            menu.BuildSteps = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"parse {parsed:F1}, handler {made - parsed:F1}, query {queried - made:F1}, new {menu.BuildTime.TotalMilliseconds - queried:F1}");
            handedBack = true;
            return menu;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
        finally
        {
            // Whatever stopped it - a failure, or an exception on its way out -
            // what was built of the menu goes with it.
            if (!handedBack)
            {
                menu.Dispose();
            }

            ReleaseComObject(parent);
            foreach (var child in others)
            {
                ILFree(child);
            }

            if (absolute != IntPtr.Zero)
            {
                ILFree(absolute);
            }
        }
    }

    /// <summary>
    /// The menu Explorer shows on the empty part of a folder: New and
    /// whatever the installed shell extensions add, from the folder's own view
    /// object, with Paste and Paste shortcut at the top as Explorer's window
    /// puts them there - they come from the window, not the folder, and run
    /// as the Shell's own <c>paste</c> and <c>pastelink</c>.
    /// </summary>
    public static ShellContextMenu? ForFolderBackground(string folderPath, IntPtr owner, bool extended)
    {
        var watch = Stopwatch.StartNew();
        var menu = new ShellContextMenu(owner, [folderPath], isBackground: true, extended) { NewTarget = folderPath };
        var handedBack = false;
        var absolute = IntPtr.Zero;
        IShellFolder? folder = null;
        try
        {
            if (SHParseDisplayName(folderPath, IntPtr.Zero, out absolute, 0, out _) != 0 || absolute == IntPtr.Zero)
            {
                return null;
            }

            var folderId = IidShellFolder;
            if (SHBindToObject(IntPtr.Zero, absolute, IntPtr.Zero, ref folderId, out folder) != 0 || folder is null)
            {
                return null;
            }

            var parsed = watch.Elapsed.TotalMilliseconds;
            var menuId = IidContextMenu;
            if (folder.CreateViewObject(owner, ref menuId, out var pointer) != 0 || pointer == IntPtr.Zero)
            {
                return null;
            }

            var view = Marshal.GetObjectForIUnknown(pointer);
            Marshal.Release(pointer);
            if (view is not IContextMenu context)
            {
                ReleaseComObject(view);
                return null;
            }

            var made = watch.Elapsed.TotalMilliseconds;
            menu.TakeShellMenu(context);
            if (!menu.Query(CmfNormal | (extended ? CmfExtendedVerbs : 0)))
            {
                return null;
            }

            var queried = watch.Elapsed.TotalMilliseconds;

            // Windows registers New for every folder's open space; should a
            // machine have lost it, the app adds its own above Properties.
            if (!BackgroundHasNew)
            {
                menu.AddNewMenu(absolute, menu.PositionOfVerb("properties") ?? GetMenuItemCount(menu._handle));
            }

            menu.AddPasteItems();
            menu.BuildTime = watch.Elapsed;
            menu.BuildSteps = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"parse {parsed:F1}, handler {made - parsed:F1}, query {queried - made:F1}, paste {menu.BuildTime.TotalMilliseconds - queried:F1}");
            handedBack = true;
            return menu;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (!handedBack)
            {
                menu.Dispose();
            }

            ReleaseComObject(folder);
            if (absolute != IntPtr.Zero)
            {
                ILFree(absolute);
            }
        }
    }

    /// <summary>
    /// Adds the app's items below the Shell's, after a line.  Each gets an id
    /// of its own at or above <see cref="AppCommandFirst"/>; a submenu of
    /// ours is never handed to the Shell's handlers.
    /// </summary>
    public void AppendEntries(IReadOnlyList<ShellMenuEntry> entries)
    {
        if (_handle == IntPtr.Zero || entries.Count == 0)
        {
            return;
        }

        var dpi = _owner != IntPtr.Zero && GetDpiForWindow(_owner) is var windowDpi and > 0 ? windowDpi : 96u;
        AppendSeparator(_handle);
        AppendInto(_handle, entries, dpi);
    }

    /// <summary>
    /// Shows the menu at a point on the screen and waits for it to close.
    /// Nothing is invoked here: what was picked comes back, for the caller
    /// to run (<see cref="Invoke"/>, or the entry's own) while this is still
    /// alive.
    /// </summary>
    public ShellMenuChoice Show(HwndSource source, int screenX, int screenY)
    {
        if (_handle == IntPtr.Zero || _disposed)
        {
            return ShellMenuChoice.Nothing;
        }

        HwndSourceHook hook = (IntPtr _, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (HandleMenuMessage(message, wParam, lParam, out var result))
            {
                handled = true;
                return result;
            }

            return IntPtr.Zero;
        };

        uint command;
        source.AddHook(hook);
        try
        {
            Tracking?.Invoke(_handle);
            command = TrackPopupMenuEx(_handle, TpmReturnCmd | TpmRightButton, screenX, screenY, source.Handle, IntPtr.Zero);
        }
        finally
        {
            source.RemoveHook(hook);
        }

        return Resolve(command);
    }

    /// <summary>What an id from the menu is: the app's entry, or the Shell's item, its verb, and whether it is one of New's.</summary>
    public ShellMenuChoice Resolve(uint command)
    {
        if (command >= AppCommandFirst)
        {
            return _entries.TryGetValue(command, out var entry)
                ? new ShellMenuChoice(ShellMenuPick.App, command, string.Empty, false, entry)
                : ShellMenuChoice.Nothing;
        }

        if (command >= NewFirst && command <= NewLast && _new is not null)
        {
            return new ShellMenuChoice(ShellMenuPick.Shell, command, VerbOf(command), true, null);
        }

        if (command >= ShellFirst && command <= ShellLast && _shell is not null)
        {
            return new ShellMenuChoice(ShellMenuPick.Shell, command, VerbOf(command), IsInNewPopup(command), null);
        }

        return ShellMenuChoice.Nothing;
    }

    /// <summary>
    /// Runs the Shell's item that was picked.  A New item runs to the end
    /// before this returns, so what it made is on disk when the caller looks.
    /// </summary>
    public void Invoke(ShellMenuChoice choice, int screenX, int screenY)
    {
        if (choice.Pick != ShellMenuPick.Shell || _disposed)
        {
            return;
        }

        var fromNew = choice.Id >= NewFirst && choice.Id <= NewLast;
        var target = fromNew ? _new : _shell;
        if (target is null)
        {
            return;
        }

        var offset = choice.Id - (fromNew ? NewFirst : ShellFirst);
        InvokeOn(target, new IntPtr(offset), new IntPtr(offset), choice.IsNew, screenX, screenY);
    }

    /// <summary>Runs one of the Shell's verbs by name - <c>paste</c>, <c>pastelink</c> - on the menu's own handler.</summary>
    public void InvokeVerb(string verb, int screenX = 0, int screenY = 0)
    {
        if (_shell is null || _disposed)
        {
            return;
        }

        var ansi = Marshal.StringToHGlobalAnsi(verb);
        var wide = Marshal.StringToHGlobalUni(verb);
        try
        {
            InvokeOn(_shell, ansi, wide, synchronous: false, screenX, screenY);
        }
        finally
        {
            Marshal.FreeHGlobal(ansi);
            Marshal.FreeHGlobal(wide);
        }
    }

    /// <summary>
    /// Hands a message the menu sent its owner to the handler it is for: a
    /// submenu opening, an item to measure or draw, a key pressed.  True when
    /// the handler took it, with what to answer in <paramref name="result"/>.
    /// </summary>
    public bool HandleMenuMessage(int message, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (_disposed)
        {
            return false;
        }

        switch (message)
        {
            case WmInitMenuPopup:
            case WmMenuChar:
            {
                var popup = message == WmInitMenuPopup ? wParam : lParam;
                if (_appPopups.Contains(popup))
                {
                    return false;
                }

                return popup == _newPopup && _newPopup != IntPtr.Zero
                    ? Forward(_new2, _new3, message, wParam, lParam, out result)
                    : Forward(_shell2, _shell3, message, wParam, lParam, out result);
            }

            case WmMeasureItem:
            case WmDrawItem:
            {
                if (lParam == IntPtr.Zero || Marshal.ReadInt32(lParam) != OdtMenu)
                {
                    return false;
                }

                // CtlType, CtlID, then itemID: the same in both structures.
                var id = (uint)Marshal.ReadInt32(lParam, 8);
                if (id >= AppCommandFirst && id <= AppCommandLast)
                {
                    return false;
                }

                if (id >= NewFirst && id <= NewLast)
                {
                    return Forward(_new2, _new3, message, wParam, lParam, out result);
                }

                return Forward(_shell2, _shell3, message, wParam, lParam, out result)
                    || (id > ShellLast && Forward(_new2, _new3, message, wParam, lParam, out result));
            }
        }

        return false;
    }

    /// <summary>Opens a submenu without showing it, as the menu does when the pointer reaches it: for tests and the warm-up.</summary>
    public bool InitializePopup(IntPtr popup, int index) =>
        HandleMenuMessage(WmInitMenuPopup, popup, new IntPtr(index), out _);

    /// <summary>Opens every submenu of the Shell's at the top level, so the handlers behind them fill them once.</summary>
    public void InitializePopups()
    {
        var count = GetMenuItemCount(_handle);
        for (var index = 0; index < count; index++)
        {
            var popup = GetSubMenu(_handle, index);
            if (popup != IntPtr.Zero && !_appPopups.Contains(popup))
            {
                InitializePopup(popup, index);
            }
        }
    }

    /// <summary>The items of a menu as they stand, for tests and for looking a submenu over.</summary>
    public IReadOnlyList<ShellMenuItem> ReadItems(IntPtr menu)
    {
        var items = new List<ShellMenuItem>();
        var count = GetMenuItemCount(menu);
        var text = Marshal.AllocHGlobal(1024);
        try
        {
            for (var index = 0; index < count; index++)
            {
                Marshal.WriteInt16(text, 0);
                var info = new MenuItemInfo
                {
                    cbSize = (uint)Marshal.SizeOf<MenuItemInfo>(),
                    fMask = MiimId | MiimSubmenu | MiimState | MiimFType | MiimString | MiimBitmap,
                    dwTypeData = text,
                    cch = 511
                };
                if (!GetMenuItemInfoW(menu, (uint)index, true, ref info))
                {
                    continue;
                }

                items.Add(new ShellMenuItem(
                    info.wID,
                    (info.fType & MftSeparator) != 0 ? string.Empty : Marshal.PtrToStringUni(text) ?? string.Empty,
                    info.hSubMenu,
                    (info.fType & MftSeparator) != 0,
                    (info.fState & MfsChecked) != 0,
                    (info.fType & MftRadioCheck) != 0,
                    (info.fState & MfsDisabled) == 0,
                    info.hbmpItem != IntPtr.Zero));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }

        return items;
    }

    /// <summary>The Shell's name for one of its items - <c>open</c>, <c>NewFolder</c>, <c>.txt</c> - or empty.</summary>
    public string VerbOf(uint command)
    {
        var fromNew = command >= NewFirst && command <= NewLast;
        var target = fromNew ? _new : command >= ShellFirst && command <= ShellLast ? _shell : null;
        if (target is null)
        {
            return string.Empty;
        }

        var buffer = Marshal.AllocHGlobal(520);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            var offset = command - (fromNew ? NewFirst : ShellFirst);
            return target.GetCommandString(new IntPtr(offset), GcsVerbW, IntPtr.Zero, buffer, 260) == 0
                ? Marshal.PtrToStringUni(buffer) ?? string.Empty
                : string.Empty;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            // A handler that cannot name its own item: it has no verb.
            return string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Releases the menu, then the handlers, on the thread that made them.
    /// Each handler is released once: the <see cref="IContextMenu2"/> and
    /// <see cref="IContextMenu3"/> held beside it are the same wrapper, and
    /// releasing an alias as well would be a double free.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Debug.Assert(IsOnThisThread, "A Shell menu is released on the thread that built it.");
        if (_handle != IntPtr.Zero)
        {
            DestroyMenu(_handle);
            _handle = IntPtr.Zero;
        }

        foreach (var bitmap in _bitmaps)
        {
            DeleteObject(bitmap);
        }

        _bitmaps.Clear();
        _shell2 = null;
        _shell3 = null;
        _new2 = null;
        _new3 = null;
        ReleaseComObject(_new);
        ReleaseComObject(_shell);
        _new = null;
        _shell = null;
    }

    // ---- building ------------------------------------------------------------

    private void TakeShellMenu(IContextMenu context)
    {
        _shell = context;
        _shell2 = context as IContextMenu2;
        _shell3 = context as IContextMenu3;
    }

    private bool Query(uint flags)
    {
        _handle = CreatePopupMenu();
        return _handle != IntPtr.Zero && _shell is not null && _shell.QueryContextMenu(_handle, 0, ShellFirst, ShellLast, flags) >= 0;
    }

    /// <summary>Whether Windows puts New on every folder's open space itself (it does unless someone removed it).</summary>
    private static bool BackgroundHasNew => _backgroundHasNew ??= ReadBackgroundHasNew();

    private static bool ReadBackgroundHasNew()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"Directory\Background\shellex\ContextMenuHandlers\New");
            return key?.GetValue(null) is string clsid && Guid.TryParse(clsid, out var id) && id == ClsidNewMenu;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>A real folder on disk - not a zip file, which the Shell also calls a folder.</summary>
    private static bool IsFileSystemFolder(IShellFolder parent, IntPtr child)
    {
        var attributes = SfgaoFolder | SfgaoFileSystem | SfgaoStream;
        return parent.GetAttributesOf(1, [child], ref attributes) == 0
            && (attributes & (SfgaoFolder | SfgaoFileSystem)) == (SfgaoFolder | SfgaoFileSystem)
            && (attributes & SfgaoStream) == 0;
    }

    /// <summary>One item's PIDL relative to its parent folder, parsed by name; null when the item is not there.</summary>
    private static IntPtr? ParseChild(IShellFolder parent, IntPtr owner, string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        if (name.Length == 0)
        {
            return null;
        }

        uint eaten = 0;
        uint attributes = 0;
        return parent.ParseDisplayName(owner, IntPtr.Zero, name, ref eaten, out var child, ref attributes) == 0 && child != IntPtr.Zero
            ? child
            : null;
    }

    /// <summary>
    /// Adds Windows' own New submenu, made for <paramref name="folderPidl"/>,
    /// at <paramref name="position"/>, with a line after it.  The handler fills
    /// it from the ShellNew templates when it first opens.
    /// </summary>
    private void AddNewMenu(IntPtr folderPidl, int position)
    {
        object? instance = null;
        try
        {
            if (Type.GetTypeFromCLSID(ClsidNewMenu, throwOnError: false) is not { } type)
            {
                return;
            }

            instance = Activator.CreateInstance(type);
            if (instance is not IShellExtInit init || instance is not IContextMenu newMenu
                || init.Initialize(folderPidl, IntPtr.Zero, IntPtr.Zero) != 0)
            {
                return;
            }

            var before = GetMenuItemCount(_handle);
            if (newMenu.QueryContextMenu(_handle, (uint)position, NewFirst, NewLast, CmfNormal) < 0
                || GetMenuItemCount(_handle) <= before
                || GetSubMenu(_handle, position) is var popup && popup == IntPtr.Zero)
            {
                return;
            }

            _new = newMenu;
            _new2 = instance as IContextMenu2;
            _new3 = instance as IContextMenu3;
            _newPopup = popup;
            instance = null;
            if (!IsSeparatorAt(_handle, position + 1) && position + 1 < GetMenuItemCount(_handle))
            {
                InsertMenuW(_handle, (uint)position + 1, MfByPosition | MfSeparator, UIntPtr.Zero, null);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            // No New from this machine's Shell: the menu goes without.
        }
        finally
        {
            ReleaseComObject(instance);
        }
    }

    /// <summary>Just after the Shell's items that open the folder - Open, Open in new window, Pin to Quick access - at the top.</summary>
    private int PositionAfterOpen()
    {
        var count = GetMenuItemCount(_handle);
        var found = (int)GetMenuDefaultItem(_handle, 1, 0);
        var position = found >= 0 && found < count ? found : 0;
        while (position < count && !IsSeparatorAt(_handle, position) && GetSubMenu(_handle, position) == IntPtr.Zero
               && OpenVerbs.Contains(VerbOf(GetMenuItemID(_handle, position))))
        {
            position++;
        }

        return position;
    }

    /// <summary>Where the Shell's item with this verb is, at the top level.</summary>
    private int? PositionOfVerb(string verb)
    {
        var count = GetMenuItemCount(_handle);
        for (var index = count - 1; index >= 0; index--)
        {
            if (GetSubMenu(_handle, index) == IntPtr.Zero && string.Equals(VerbOf(GetMenuItemID(_handle, index)), verb, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return null;
    }

    /// <summary>
    /// Paste and Paste shortcut at the top of a folder's open space, as
    /// Explorer's window adds them: the Shell's own verbs, so the copy runs
    /// in the Shell's copy engine with its progress, its questions and its
    /// undo.  Offered only while the clipboard holds files.
    /// </summary>
    private void AddPasteItems()
    {
        var canPaste = IsClipboardFormatAvailable(ClipboardFileDrop) || IsClipboardFormatAvailable(ShellIdListFormat);
        var entries = new[]
        {
            new ShellMenuEntry("Paste", () => InvokeVerb("paste")) { IsEnabled = canPaste, Shortcut = "Ctrl+V" },
            new ShellMenuEntry("Paste shortcut", () => InvokeVerb("pastelink")) { IsEnabled = canPaste }
        };

        var position = 0u;
        foreach (var entry in entries)
        {
            var id = _nextAppId++;
            _entries[id] = entry;
            InsertItem(_handle, position++, id, entry, IntPtr.Zero, 96);
        }

        if (GetMenuItemCount(_handle) > (int)position && !IsSeparatorAt(_handle, (int)position))
        {
            InsertMenuW(_handle, position, MfByPosition | MfSeparator, UIntPtr.Zero, null);
        }
    }

    private void AppendInto(IntPtr menu, IReadOnlyList<ShellMenuEntry> entries, uint dpi)
    {
        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                AppendSeparator(menu);
                continue;
            }

            if (entry.Children is { Count: > 0 } children)
            {
                var popup = CreatePopupMenu();
                if (popup == IntPtr.Zero)
                {
                    continue;
                }

                _appPopups.Add(popup);
                AppendInto(popup, children, dpi);
                InsertItem(menu, (uint)GetMenuItemCount(menu), 0, entry, popup, dpi);
                continue;
            }

            if (_nextAppId > AppCommandLast)
            {
                break;
            }

            var id = _nextAppId++;
            _entries[id] = entry;
            InsertItem(menu, (uint)GetMenuItemCount(menu), id, entry, IntPtr.Zero, dpi);
        }

        // A group that ended on a line leaves no line hanging at the bottom.
        var count = GetMenuItemCount(menu);
        if (count > 0 && IsSeparatorAt(menu, count - 1))
        {
            DeleteMenu(menu, (uint)count - 1, MfByPosition);
        }
    }

    private void InsertItem(IntPtr menu, uint position, uint id, ShellMenuEntry entry, IntPtr popup, uint dpi)
    {
        // An ampersand in a folder's name is a letter, not a mnemonic.
        var label = entry.Label.Replace("&", "&&", StringComparison.Ordinal);
        if (entry.Shortcut.Length > 0)
        {
            label += "\t" + entry.Shortcut;
        }

        var text = Marshal.StringToHGlobalUni(label);
        try
        {
            var enabled = popup != IntPtr.Zero ? entry.IsEnabled : entry.CanRun;
            var info = new MenuItemInfo
            {
                cbSize = (uint)Marshal.SizeOf<MenuItemInfo>(),
                fMask = MiimString | MiimState | MiimFType,
                fType = entry.IsRadio ? MftRadioCheck : 0,
                fState = (entry.Checked == true ? MfsChecked : 0) | (enabled ? 0 : MfsDisabled),
                dwTypeData = text
            };

            if (popup != IntPtr.Zero)
            {
                info.fMask |= MiimSubmenu;
                info.hSubMenu = popup;
            }
            else
            {
                info.fMask |= MiimId;
                info.wID = id;
            }

            // A switch shows its tick where the picture would be.
            var bitmap = entry.Swatch is { } swatch
                ? MenuBitmaps.Swatch(swatch, dpi)
                : entry.Glyph is { Length: > 0 } glyph && entry.Checked is null ? MenuBitmaps.Glyph(glyph, dpi) : IntPtr.Zero;
            if (bitmap != IntPtr.Zero)
            {
                info.fMask |= MiimBitmap;
                info.hbmpItem = bitmap;
                if (entry.Swatch is not null)
                {
                    // Swatches are made per menu; glyphs are kept for the next one.
                    _bitmaps.Add(bitmap);
                }
            }

            InsertMenuItemW(menu, position, true, ref info);
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    private static void AppendSeparator(IntPtr menu)
    {
        var count = GetMenuItemCount(menu);
        if (count > 0 && !IsSeparatorAt(menu, count - 1))
        {
            AppendMenuW(menu, MfSeparator, UIntPtr.Zero, null);
        }
    }

    private static bool IsSeparatorAt(IntPtr menu, int position)
    {
        if (position < 0 || position >= GetMenuItemCount(menu))
        {
            return false;
        }

        var info = new MenuItemInfo { cbSize = (uint)Marshal.SizeOf<MenuItemInfo>(), fMask = MiimFType };
        return GetMenuItemInfoW(menu, (uint)position, true, ref info) && (info.fType & MftSeparator) != 0;
    }

    // ---- choosing ------------------------------------------------------------

    /// <summary>
    /// Whether one of the Shell's items sits in a New submenu - the one
    /// Windows puts on a folder's open space - which is known by the Folder
    /// item every New has, whose verb is <c>NewFolder</c>.
    /// </summary>
    private bool IsInNewPopup(uint command)
    {
        if (FindPopupOf(_handle, command) is not { } popup)
        {
            return false;
        }

        if (!_newPopups.TryGetValue(popup, out var isNew))
        {
            var count = GetMenuItemCount(popup);
            for (var index = 0; index < count && !isNew; index++)
            {
                var id = GetMenuItemID(popup, index);
                isNew = id != uint.MaxValue && string.Equals(VerbOf(id), "NewFolder", StringComparison.OrdinalIgnoreCase);
            }

            _newPopups[popup] = isNew;
        }

        return isNew;
    }

    /// <summary>The submenu an id is in (not the top level), searched through the Shell's submenus.</summary>
    private IntPtr? FindPopupOf(IntPtr menu, uint command)
    {
        var count = GetMenuItemCount(menu);
        for (var index = 0; index < count; index++)
        {
            var popup = GetSubMenu(menu, index);
            if (popup == IntPtr.Zero || _appPopups.Contains(popup))
            {
                continue;
            }

            var inner = GetMenuItemCount(popup);
            for (var item = 0; item < inner; item++)
            {
                if (GetSubMenu(popup, item) == IntPtr.Zero && GetMenuItemID(popup, item) == command)
                {
                    return popup;
                }
            }

            if (FindPopupOf(popup, command) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    private void InvokeOn(IContextMenu target, IntPtr verb, IntPtr verbW, bool synchronous, int screenX, int screenY)
    {
        // The folder the command runs in: the one whose open space it is, or the one the items are in.
        var directory = IsBackground ? Paths[0] : Path.GetDirectoryName(Paths[0]);
        var info = new CmInvokeCommandInfoEx
        {
            cbSize = Marshal.SizeOf<CmInvokeCommandInfoEx>(),
            fMask = CmicMaskUnicode | CmicMaskPtInvoke | (synchronous ? CmicMaskNoAsync : 0),
            hwnd = _owner,
            lpVerb = verb,
            lpVerbW = verbW,
            lpDirectory = directory,
            lpDirectoryW = directory,
            nShow = SwShowNormal,
            ptInvoke = new Point32 { X = screenX, Y = screenY }
        };

        try
        {
            target.InvokeCommand(ref info);
        }
        catch (COMException)
        {
            // A shell extension refusing its own verb is not ours to fix.
        }
    }

    private static bool Forward(IContextMenu2? menu2, IContextMenu3? menu3, int message, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        try
        {
            if (menu3 is not null && menu3.HandleMenuMsg2(message, wParam, lParam, out result) == 0)
            {
                return true;
            }

            if (menu2 is not null && message != WmMenuChar && menu2.HandleMenuMsg(message, wParam, lParam) == 0)
            {
                result = message == WmInitMenuPopup ? IntPtr.Zero : new IntPtr(1);
                return true;
            }
        }
        catch (COMException)
        {
            // A handler failing to fill or draw its own item leaves it as it is.
        }

        return false;
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

    // ---- interop -------------------------------------------------------------

    private const uint MfSeparator = 0x00000800;
    private const uint MfByPosition = 0x00000400;
    private const uint MiimState = 0x00000001;
    private const uint MiimId = 0x00000002;
    private const uint MiimSubmenu = 0x00000004;
    private const uint MiimString = 0x00000040;
    private const uint MiimBitmap = 0x00000080;
    private const uint MiimFType = 0x00000100;
    private const uint MftSeparator = 0x00000800;
    private const uint MftRadioCheck = 0x00000200;
    private const uint MfsChecked = 0x00000008;
    private const uint MfsDisabled = 0x00000003;
    private const int OdtMenu = 1;

    private const int WmInitMenuPopup = 0x0117;
    private const int WmDrawItem = 0x002B;
    private const int WmMeasureItem = 0x002C;
    private const int WmMenuChar = 0x0120;

    private const uint CmfNormal = 0x00000000;
    private const uint CmfCanRename = 0x00000010;
    private const uint CmfExtendedVerbs = 0x00000100;
    private const uint GcsVerbW = 0x00000004;

    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;

    private const int CmicMaskNoAsync = 0x00000100;
    private const int CmicMaskUnicode = 0x00004000;
    private const int CmicMaskPtInvoke = 0x20000000;
    private const int SwShowNormal = 1;

    private const uint SfgaoStream = 0x00400000;
    private const uint SfgaoFileSystem = 0x40000000;
    private const uint SfgaoFolder = 0x20000000;

    private const uint ClipboardFileDrop = 15;
    private static readonly uint ShellIdListFormat = RegisterClipboardFormatW("Shell IDList Array");

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

    [ComImport]
    [Guid("000214E8-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellExtInit
    {
        [PreserveSig] int Initialize(IntPtr pidlFolder, IntPtr dataObject, IntPtr progIdKey);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MenuItemInfo
    {
        public uint cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public IntPtr dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellFolder parent, out IntPtr lastChild);

    [DllImport("shell32.dll")]
    private static extern int SHBindToObject(IntPtr shellFolder, IntPtr pidl, IntPtr bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellFolder folder);

    [DllImport("shell32.dll", EntryPoint = "ILFindLastID")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll", EntryPoint = "ILFree")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? item);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InsertMenuW(IntPtr menu, uint position, uint flags, UIntPtr id, string? item);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InsertMenuItemW(IntPtr menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition, ref MenuItemInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMenuItemInfoW(IntPtr menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition, ref MenuItemInfo info);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern uint GetMenuItemID(IntPtr menu, int position);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSubMenu(IntPtr menu, int position);

    [DllImport("user32.dll")]
    private static extern uint GetMenuDefaultItem(IntPtr menu, uint byPosition, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteMenu(IntPtr menu, uint position, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string name);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);
}

/// <summary>One item of a native menu as <see cref="ShellContextMenu.ReadItems"/> reads it.</summary>
internal readonly record struct ShellMenuItem(uint Id, string Text, IntPtr Submenu, bool IsSeparator, bool IsChecked, bool IsRadio, bool IsEnabled, bool HasBitmap)
{
    /// <summary>The text without its mnemonic marks or its shortcut: what a person reads.</summary>
    public string Label => Text.Split('\t')[0].Replace("&&", "\u0001", StringComparison.Ordinal).Replace("&", string.Empty, StringComparison.Ordinal).Replace('\u0001', '&');
}
