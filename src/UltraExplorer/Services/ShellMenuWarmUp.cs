using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace UltraExplorer.Services;

/// <summary>
/// Keeps the Shell's context menu handlers loaded, so a right-click never
/// waits for them.  Building a menu is quick only while every installed
/// extension's DLL is in the process: the first menu of a session loads them
/// all - half a second for a folder - and a few seconds after the last menu
/// is let go of, Windows unloads them again, so a right-click after any
/// pause paid a fifth of a second all over again.  Measured, building a
/// folder's menu took 40-60 ms while another menu was alive and 200-250 ms
/// after five seconds without one.
///
/// So once the window is up and idle, a background thread of low priority
/// builds - never shows - the menus of a folder, a text file, a program, a
/// folder's open space and a drive, opens their submenus (New, Send to, Open
/// with) without showing them either, and then keeps those menus alive for
/// the rest of the session, pumping messages for whatever the handlers need.
/// A live handler cannot be unloaded, so its DLL stays; the Shell's caches
/// and the app's own code for menus are warm too.  The user's menus are still
/// built on the window's thread, where they are shown.
///
/// The folder, text file and open space are made for the purpose in a folder
/// of the app's state; the program is the app itself and the drive the one
/// that folder is on.  Nothing is invoked.
/// </summary>
internal static class ShellMenuWarmUp
{
    private static readonly object Gate = new();
    private static readonly List<ShellContextMenu> Kept = [];
    private static Task? _task;

    /// <summary>Done when the menus have been built, or at once when none was started.</summary>
    public static Task Completed => _task ?? Task.CompletedTask;

    /// <summary>How long building the menus took, once it has run.</summary>
    public static TimeSpan Elapsed { get; private set; }

    /// <summary>How many menus were built, and so are kept alive.</summary>
    public static int MenusBuilt { get; private set; }

    /// <summary>
    /// Starts it with its items in <paramref name="folder"/>, once per
    /// process; later calls hand back the same run.
    /// </summary>
    public static Task Start(string folder)
    {
        lock (Gate)
        {
            if (_task is not null)
            {
                return _task;
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    Build(folder);
                }
                catch (Exception)
                {
                    // A menu that could not be built is built cold when asked
                    // for.  Everything is caught: what escapes a thread of its
                    // own ends the process, and this is only ever a head start.
                }
                finally
                {
                    done.SetResult();
                }

                // The menus stay alive on this thread, which pumps messages for
                // them until the process ends.
                if (Kept.Count > 0)
                {
                    Dispatcher.Run();
                }
            })
            {
                IsBackground = true,
                Name = "UltraExplorer Shell menu handlers",
                Priority = ThreadPriority.BelowNormal
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return _task = done.Task;
        }
    }

    /// <summary>The items whose menus are built: a folder, a text file, a program, the folder's open space and the drive.</summary>
    internal static (string Folder, string TextFile, string Program, string Background, string Drive) Targets(string folder) => (
        Path.Combine(folder, "Folder"),
        Path.Combine(folder, "Text.txt"),
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "UltraExplorer.exe"),
        folder,
        Path.GetPathRoot(Path.GetFullPath(folder)) ?? folder);

    private static void Build(string folder)
    {
        var watch = Stopwatch.StartNew();
        var targets = Targets(folder);
        Directory.CreateDirectory(targets.Folder);
        if (!File.Exists(targets.TextFile))
        {
            File.WriteAllText(targets.TextFile, string.Empty);
        }

        // An owner for the handlers that want a window, on this thread, never shown.
        var owner = CreateWindowExW(0, "STATIC", "UltraExplorer menu handlers", 0, 0, 0, 0, 0, MessageOnlyParent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        try
        {
            Keep(ShellContextMenu.ForItems([targets.Folder], owner, extended: false, offerNew: true));
            Keep(ShellContextMenu.ForItems([targets.TextFile], owner, extended: false, offerNew: false));
            Keep(ShellContextMenu.ForItems([targets.Program], owner, extended: false, offerNew: false));
            Keep(ShellContextMenu.ForFolderBackground(targets.Background, owner, extended: false));

            // A drive only when it is fixed: a menu kept alive for a removable
            // one could stand in the way of ejecting it.
            if (IsFixedDrive(targets.Drive))
            {
                Keep(ShellContextMenu.ForItems([targets.Drive], owner, extended: false, offerNew: true));
            }
        }
        finally
        {
            MenusBuilt = Kept.Count;
            Elapsed = watch.Elapsed;
        }
    }

    /// <summary>Opens a built menu's submenus, the app's own pictures included, and keeps it.</summary>
    private static void Keep(ShellContextMenu? menu)
    {
        if (menu is null)
        {
            return;
        }

        // Kept first: a submenu that fails to open still leaves its handlers
        // loaded, and the menu held rather than dropped unreleased.
        Kept.Add(menu);
        menu.AppendEntries([new ShellMenuEntry("Colour", Children: [new ShellMenuEntry("Red") { Swatch = "#EF5A68" }]), new ShellMenuEntry("Fit all") { Glyph = "\uE9A6" }]);
        menu.InitializePopups();
    }

    private static bool IsFixedDrive(string root)
    {
        try
        {
            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static readonly IntPtr MessageOnlyParent = new(-3);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int extendedStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
}
