using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// The Shell's context menus, built and never shown: the dark menus asked
/// for safely; the open space of a folder with New - Folder and Text
/// Document once it opens - Paste, the extensions and Properties; a folder's
/// own New near the top of its menu; the app's items appended with ids of
/// their own that come back to the right handler; the messages that fill a
/// submenu reaching only the handler it belongs to; New run for real inside
/// a test folder, the new item found; the cases the Shell has no menu for,
/// where the app's own takes over; and what building a menu costs.  With the
/// window, in the Settings group (<see cref="ShellMenuWindowChecks"/>): the
/// same entries in both menus, and a new item selected and renamed.
/// </summary>
internal static partial class Program
{
    private static Task ShellMenuChecks()
    {
        RunOnSta("shell menus", ShellMenusOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task ShellMenusOnStaAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerShellMenus", Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "Folder");
        var file = Path.Combine(root, "note.txt");
        var other = Path.Combine(root, "other.txt");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "inside.txt"), "inside");
        File.WriteAllText(file, "note");
        File.WriteAllText(other, "other");

        // A window to own the menus that is never on screen.
        using var owner = new HwndSource(new HwndSourceParameters("UltraExplorer shell menu checks") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        try
        {
            DarkMenuChecks(owner.Handle);
            BackgroundMenuChecks(folder, owner.Handle);
            FolderItemMenuChecks(folder, file, other, owner.Handle);
            AppEntryChecks(folder, owner.Handle);
            NewInvokeChecks(folder, owner.Handle);
            ShellMenuFallbackChecks(root, file, owner.Handle);
            await ShellMenuTimingChecks(root, folder, file, owner.Handle);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- dark menus ---------------------------------------------------------------------

    private static void DarkMenuChecks(IntPtr owner)
    {
        Section("shell menus: dark");
        var first = DarkMenus.UseForProcess();
        var again = DarkMenus.UseForProcess();
        Check("asking for dark menus is safe, and a second time says the same",
            first == again && DarkMenus.IsOn == first);
        Check($"on this Windows (build {Environment.OSVersion.Version.Build}) the menus are dark exactly when it has the exports",
            first == OperatingSystem.IsWindowsVersionAtLeast(10, 0, DarkMenus.FirstBuild));
        DarkMenus.AllowForWindow(owner);
        DarkMenus.AllowForWindow(IntPtr.Zero);
        Check("letting a window's menus be dark is safe, and a missing window is ignored", true);
    }

    // ---- the open space of a folder -----------------------------------------------------

    private static void BackgroundMenuChecks(string folder, IntPtr owner)
    {
        Section("shell menus: a folder's open space");
        using var menu = ShellContextMenu.ForFolderBackground(folder, owner, extended: false);
        Check("the Shell makes the menu of a folder's open space", menu is not null);
        if (menu is null)
        {
            return;
        }

        var items = menu.ReadItems(menu.Handle);
        Check("it is for that folder, and New makes things in it", menu.IsBackground && menu.NewTarget == folder);
        Check("Paste and Paste shortcut come first, as in Explorer's window, with the app's ids",
            items.Count > 2 && items[0].Label == "Paste" && items[1].Label == "Paste shortcut"
            && items[0].Id >= ShellContextMenu.AppCommandFirst && items[1].Id >= ShellContextMenu.AppCommandFirst
            && items[0].Text.EndsWith("\tCtrl+V", StringComparison.Ordinal));
        Check("Properties is the Shell's own, by its verb",
            items.Any(item => item.Id is > 0 and < ShellContextMenu.AppCommandFirst && menu.VerbOf(item.Id) == "properties"));

        // New is filled when it opens: it is found by what it holds.
        List<ShellMenuItem>? newItems = null;
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index].Submenu == IntPtr.Zero)
            {
                continue;
            }

            menu.InitializePopup(items[index].Submenu, index);
            var inside = menu.ReadItems(items[index].Submenu);
            if (inside.Any(item => menu.VerbOf(item.Id) == "NewFolder"))
            {
                newItems = [.. inside];
            }
        }

        Check("it has a New submenu, filled once it opens", newItems is { Count: > 2 });
        var names = newItems?.Select(item => (Label: item.Label, Verb: menu.VerbOf(item.Id))).ToList() ?? [];
        Console.WriteLine($"  new:  {string.Join(", ", names.Select(name => $"{name.Label} [{name.Verb}]"))}");
        Check("New has Folder and Text Document",
            names.Any(name => name.Verb == "NewFolder" && name.Label.Contains("Folder", StringComparison.OrdinalIgnoreCase))
            && names.Any(name => name.Verb == ".txt" && name.Label.Contains("Text", StringComparison.OrdinalIgnoreCase)));
        var folderItem = newItems?.First(item => menu.VerbOf(item.Id) == "NewFolder");
        Check("an item of New is known as one of New's, with its verb",
            folderItem is { } found && menu.Resolve(found.Id) is { Pick: ShellMenuPick.Shell, IsNew: true, Verb: "NewFolder" });
        var properties = items.First(item => item.Id is > 0 and < ShellContextMenu.AppCommandFirst && menu.VerbOf(item.Id) == "properties");
        Check("an item outside New is not", menu.Resolve(properties.Id) is { Pick: ShellMenuPick.Shell, IsNew: false, Verb: "properties" });
        Console.WriteLine($"  menu: {string.Join(" | ", items.Where(item => !item.IsSeparator).Select(item => item.Label))}");
    }

    // ---- a folder's own menu ------------------------------------------------------------

    private static void FolderItemMenuChecks(string folder, string file, string other, IntPtr owner)
    {
        Section("shell menus: a folder and files");
        using (var menu = ShellContextMenu.ForItems([folder], owner, extended: false, offerNew: true))
        {
            Check("the Shell makes a folder's menu", menu is not null);
            if (menu is not null)
            {
                var items = menu.ReadItems(menu.Handle);
                var at = items.ToList().FindIndex(item => item.Submenu == menu.NewPopup);
                Check("a folder's menu has New for that folder, near the top",
                    menu.NewPopup != IntPtr.Zero && menu.NewTarget == folder && at is >= 0 and <= 5);
                Check("right after the Shell's own items that open it, with a line after it",
                    at > 0 && items[at - 1].Label.Length > 0 && !items[at - 1].IsSeparator && items[at + 1].IsSeparator);
                menu.InitializePopup(menu.NewPopup, at);
                var inside = menu.ReadItems(menu.NewPopup);
                Check("its New fills when it opens, with Folder and Text Document",
                    inside.Any(item => menu.VerbOf(item.Id) == "NewFolder") && inside.Any(item => menu.VerbOf(item.Id) == ".txt"));
                Check("New's items have ids of their own, apart from the Shell's and the app's",
                    inside.Where(item => !item.IsSeparator).All(item => item.Id is >= 0x7000 and <= 0x7FFF)
                    && items.Where(item => item.Submenu == IntPtr.Zero && !item.IsSeparator).All(item => item.Id is > 0 and < 0x7000));
                Check("Rename is on it, for the app to do", items.Any(item => menu.VerbOf(item.Id) == "rename"));

                // Only New's own handler fills New; the Shell's handler is not handed it.
                var before = inside.Count;
                menu.InitializePopup(menu.NewPopup, at);
                Check("opening New again does not fill it twice", menu.ReadItems(menu.NewPopup).Count == before);
            }
        }

        using (var menu = ShellContextMenu.ForItems([file], owner, extended: false, offerNew: true))
        {
            Check("a file's menu has no New", menu is not null && menu.NewPopup == IntPtr.Zero && menu.NewTarget is null);
        }

        using (var menu = ShellContextMenu.ForItems([file, other], owner, extended: false, offerNew: true))
        {
            Check("two files of one folder have one menu between them",
                menu is not null && menu.Paths.Count == 2 && menu.ReadItems(menu.Handle).Any(item => menu.VerbOf(item.Id) == "delete"));
        }

        // Drives have no name inside This PC to be found by: each is parsed whole.
        string[] drives = [.. DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady).Select(drive => drive.RootDirectory.FullName).Take(2)];
        if (drives.Length == 2)
        {
            using var menu = ShellContextMenu.ForItems(drives, owner, extended: false, offerNew: true);
            Check("two drives have one menu between them, and no New", menu is { NewPopup: 0 } && menu.Paths.Count == 2);
        }
    }

    // ---- the app's entries --------------------------------------------------------------

    private static void AppEntryChecks(string folder, IntPtr owner)
    {
        Section("shell menus: the app's entries");
        using var menu = ShellContextMenu.ForItems([folder], owner, extended: false, offerNew: true);
        if (menu is null)
        {
            Check("a folder's menu to add to", false);
            return;
        }

        var ran = new List<string>();
        var entries = new List<ShellMenuEntry>
        {
            new("Fit all & more", () => ran.Add("fit")) { Glyph = "\uE9A6", Shortcut = "Shift+1" },
            new("Colour", Children:
            [
                new("Default", () => ran.Add("default")) { Swatch = string.Empty },
                new("Red", () => ran.Add("red")) { Swatch = "#EF5A68", Shortcut = "Current" }
            ]),
            new("Sort by", Children:
            [
                new("Name", () => ran.Add("name")) { Checked = true, IsRadio = true },
                new("Size", () => ran.Add("size")) { Checked = false, IsRadio = true },
                ShellMenuEntry.Separator,
                new("Reset", () => ran.Add("reset")) { IsEnabled = false }
            ]),
            ShellMenuEntry.Separator,
            new("Never", () => ran.Add("never")) { Command = new RelayCommand(() => ran.Add("never"), () => false) },
            new("Switch", () => ran.Add("switch")) { Checked = true }
        };

        var shellCount = menu.ReadItems(menu.Handle).Count;
        menu.AppendEntries(entries);
        var top = menu.ReadItems(menu.Handle);
        var added = top.Skip(shellCount).ToList();
        var colour = added.First(item => item.Label == "Colour");
        var sort = added.First(item => item.Label == "Sort by");
        var all = added.Concat(menu.ReadItems(colour.Submenu)).Concat(menu.ReadItems(sort.Submenu))
            .Where(item => item.Submenu == IntPtr.Zero && !item.IsSeparator).ToList();

        Check("the app's section starts after a line, below every item of the Shell's",
            added.Count > 0 && added[0].IsSeparator
            && top.Take(shellCount).Where(item => item.Submenu == IntPtr.Zero && !item.IsSeparator).All(item => item.Id < ShellContextMenu.AppCommandFirst));
        Check("every item of the app's has an id of its own at or above the app's first",
            all.Count == 8 && all.All(item => item.Id >= ShellContextMenu.AppCommandFirst) && all.Select(item => item.Id).Distinct().Count() == all.Count);
        Check("an ampersand is shown, not taken as a mnemonic, and the shortcut sits at the right",
            added.Any(item => item.Text == "Fit all && more\tShift+1" && item.Label == "Fit all & more"));
        Check("a choice of several is a radio mark, ticked where it is the one",
            menu.ReadItems(sort.Submenu) is var sorts && sorts[0] is { IsRadio: true, IsChecked: true } && sorts[1] is { IsRadio: true, IsChecked: false });
        Check("what cannot be run now is greyed: said so, or its command says so",
            all.First(item => item.Label == "Reset").IsEnabled == false && all.First(item => item.Label == "Never").IsEnabled == false
            && all.First(item => item.Label == "Name").IsEnabled);
        Check("a switch is ticked; a swatch and a glyph are pictures beside their items",
            all.First(item => item.Label == "Switch").IsChecked
            && all.First(item => item.Label == "Red").HasBitmap && all.First(item => item.Label == "Default").HasBitmap
            && all.First(item => item.Label.StartsWith("Fit all", StringComparison.Ordinal)).HasBitmap);

        foreach (var item in all)
        {
            if (menu.Resolve(item.Id) is { Pick: ShellMenuPick.App, Entry: { } entry })
            {
                entry.Execute();
            }
        }

        Check("each id comes back to its own entry, and runs its own handler - a command that cannot run does not",
            ran.SequenceEqual(["fit", "switch", "default", "red", "name", "size", "reset"]));

        // The messages that fill and draw a submenu never reach the Shell for one of the app's.
        Check("the app's submenus and items are not handed to the Shell's handlers",
            !menu.HandleMenuMessage(0x0117, colour.Submenu, IntPtr.Zero, out _) && !menu.InitializePopup(sort.Submenu, 0));
        Check("an id the menu never had is nothing", menu.Resolve(ShellContextMenu.AppCommandFirst + 500).Pick == ShellMenuPick.None && menu.Resolve(0).Pick == ShellMenuPick.None);
    }

    // ---- New, run -----------------------------------------------------------------------

    private static void NewInvokeChecks(string folder, IntPtr owner)
    {
        Section("shell menus: New, run");
        using (var menu = ShellContextMenu.ForItems([folder], owner, extended: false, offerNew: true))
        {
            if (menu?.NewPopup is not { } popup || popup == IntPtr.Zero)
            {
                Check("a folder's New to run", false);
                return;
            }

            menu.InitializePopup(popup, 0);
            var newFolder = menu.ReadItems(popup).First(item => menu.VerbOf(item.Id) == "NewFolder");
            var before = MainWindow.FolderEntries(folder);
            var choice = menu.Resolve(newFolder.Id);
            menu.Invoke(choice, 0, 0);
            var made = MainWindow.FindNewEntry(folder, before);
            Check("New > Folder on a folder's menu makes a folder in it, there by the time it returns, and it is found",
                choice.IsNew && made is not null && Directory.Exists(made) && ViewAllPath.Equals(Path.GetDirectoryName(made)!, folder));
        }

        using (var menu = ShellContextMenu.ForFolderBackground(folder, owner, extended: false))
        {
            var items = menu?.ReadItems(menu.Handle) ?? [];
            ShellMenuItem? text = null;
            for (var index = 0; index < items.Count && text is null && menu is not null; index++)
            {
                if (items[index].Submenu != IntPtr.Zero)
                {
                    menu.InitializePopup(items[index].Submenu, index);
                    text = menu.ReadItems(items[index].Submenu).Where(item => menu.VerbOf(item.Id) == ".txt").Cast<ShellMenuItem?>().FirstOrDefault();
                }
            }

            if (menu is null || text is not { } found)
            {
                Check("New > Text Document on the open space to run", false);
                return;
            }

            var before = MainWindow.FolderEntries(folder);
            var choice = menu.Resolve(found.Id);
            menu.Invoke(choice, 0, 0);
            var made = MainWindow.FindNewEntry(folder, before);
            Check("New > Text Document on the open space makes a .txt file in the folder, known as New's",
                choice is { IsNew: true, Verb: ".txt" } && made is not null && File.Exists(made) && made.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
            Check("nothing new is found where nothing was made", MainWindow.FindNewEntry(folder, MainWindow.FolderEntries(folder)) is null);
        }
    }

    // ---- where the Shell has none -------------------------------------------------------

    private static void ShellMenuFallbackChecks(string root, string file, IntPtr owner)
    {
        Section("shell menus: where the Shell has none");
        var missing = Path.Combine(root, "gone", "nothing.txt");
        Check("an item that is not there has no Shell menu, so the app's own is shown",
            ShellContextMenu.ForItems([missing], owner, extended: false, offerNew: true) is null);
        Check("nor does the open space of a folder that is not there",
            ShellContextMenu.ForFolderBackground(Path.Combine(root, "gone"), owner, extended: false) is null);
        Check("nor items from different folders",
            ShellContextMenu.ForItems([file, Path.Combine(root, "Folder", "inside.txt")], owner, extended: false, offerNew: true) is null);
        Check("nor nothing", ShellContextMenu.ForItems([], owner, extended: false, offerNew: true) is null);
        using var fine = ShellContextMenu.ForItems([file], owner, extended: true, offerNew: true);
        Check("Shift's extended menu is built too", fine is { IsExtended: true });
    }

    // ---- what it costs ------------------------------------------------------------------

    private static async Task ShellMenuTimingChecks(string root, string folder, string file, IntPtr owner)
    {
        Section("shell menus: what building one costs");
        var warm = Path.Combine(root, "warm");
        await ShellMenuWarmUp.Start(warm);
        Check($"the handlers are loaded on a thread of their own and kept: {ShellMenuWarmUp.MenusBuilt} menus in {ShellMenuWarmUp.Elapsed.TotalMilliseconds:F0} ms",
            ShellMenuWarmUp.MenusBuilt >= 4 && File.Exists(Path.Combine(warm, "Text.txt")) && Directory.Exists(Path.Combine(warm, "Folder")));
        Check("started again, it is the same run", ShellMenuWarmUp.Start(warm).IsCompleted);

        double Best(Func<ShellContextMenu?> build)
        {
            var best = double.MaxValue;
            for (var round = 0; round < 4; round++)
            {
                var watch = Stopwatch.StartNew();
                using var menu = build();
                best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
            }

            return best;
        }

        Report("a folder's menu, warm", (long)Best(() => ShellContextMenu.ForItems([folder], owner, extended: false, offerNew: true)), 400);
        Report("a file's menu, warm", (long)Best(() => ShellContextMenu.ForItems([file], owner, extended: false, offerNew: true)), 400);
        Report("a folder's open space, warm", (long)Best(() => ShellContextMenu.ForFolderBackground(folder, owner, extended: false)), 400);
    }

    // ---- with the window ----------------------------------------------------------------

    /// <summary>
    /// The window's side, run with the Settings group because it needs the
    /// app: the entries of a folder's open space as the Shell's menu gets
    /// them and as the app's own menu gets them, the entries for items, and
    /// what follows a New - the new item selected and its rename opened.
    /// </summary>
    private static async Task ShellMenuWindowChecks(MainWindow main, MainViewModel shell)
    {
        Section("shell menus: the window's entries");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerShellMenuWindow", Guid.NewGuid().ToString("N"));
        var docs = Path.Combine(root, "Docs");
        Directory.CreateDirectory(docs);
        try
        {
            var forShell = main.FolderAreaEntries(docs, forShell: true);
            var own = main.FolderAreaEntries(docs, forShell: false);
            string[] Labels(IEnumerable<ShellMenuEntry> entries) => [.. entries.Where(entry => !entry.IsSeparator).Select(entry => entry.Label)];
            Check("for the Shell's menu, New, Paste and Properties are left to the Shell",
                !Labels(forShell).Any(label => label.StartsWith("New ", StringComparison.Ordinal) || label.StartsWith("Paste", StringComparison.Ordinal) || label == "Properties"));
            Check("and the rest is the app's own menu's, in its order",
                Labels(forShell).SequenceEqual(Labels(own).Where(label => !label.StartsWith("New ", StringComparison.Ordinal) && !label.StartsWith("Paste", StringComparison.Ordinal) && label != "Properties")));
            Check("the folder's settings, the canvas's commands, its layers and both canvases are there",
                Labels(forShell).Take(5).SequenceEqual(["Sort Docs by", "Colour", "Add note\u2026", "Pin to Home", "Show in File Explorer"])
                && Labels(forShell).Contains("Fit all") && Labels(forShell).Contains("Folder list") && Labels(forShell).Contains("Layers")
                && Labels(forShell).Contains("Nested canvas") && Labels(forShell).Contains("Tree canvas"));
            var sort = forShell.First(entry => entry.Label == "Sort Docs by");
            Check("Sort Docs by is a choice of columns and of ways round, each marked as a radio",
                sort.Children!.Where(entry => entry.Checked is not null).All(entry => entry.IsRadio)
                && sort.Children!.Count(entry => entry.Checked == true) == 2);
            var layers = forShell.First(entry => entry.Label == "Layers").Children!;
            Check("Layers has every layer as a switch, and hidden items",
                CanvasLayers.Each.All(layer => layers.Any(entry => entry.Label == CanvasLayers.Describe(layer) && entry.Checked is not null))
                && layers.Any(entry => entry.Label == "Hidden items"));

            // The app's own menu from the same entries.
            var menu = new ContextMenu();
            MainWindow.AddEntries(menu, forShell);
            var colour = menu.Items.OfType<MenuItem>().First(item => item.Header as string == "Colour");
            Check("the same entries make the app's own menu: a submenu, swatches, glyphs, radios as ticks",
                colour.Items.OfType<MenuItem>().All(item => item.Icon is Border)
                && menu.Items.OfType<MenuItem>().First(item => item.Header as string == "Add note\u2026").Icon is TextBlock { Text: "\uE70B" }
                && menu.Items.OfType<MenuItem>().First(item => item.Header as string == "Nested canvas") is { IsCheckable: true }
                && menu.Items[0] is MenuItem && menu.Items[^1] is MenuItem);

            // After a New: the new item selected, and renamed.
            var before = MainWindow.FolderEntries(docs);
            var made = Path.Combine(docs, "New folder");
            Directory.CreateDirectory(made);
            var renamed = new List<string>();
            var followed = await main.FollowNewItemAsync(docs, before, () => renamed.AddRange(shell.Tree.Selection.Paths));
            Check("what a New made becomes the selection, and its rename opens on it",
                followed is not null && ViewAllPath.Equals(followed, made) && renamed.Count == 1 && ViewAllPath.Equals(renamed[0], made));
            Check("nothing made, nothing followed", await main.FollowNewItemAsync(docs, MainWindow.FolderEntries(docs), () => renamed.Add("again")) is null && renamed.Count == 1);
            shell.Tree.Selection.Clear(SelectionSource.Navigation);
        }
        finally
        {
            TryDelete(root);
        }
    }
}
