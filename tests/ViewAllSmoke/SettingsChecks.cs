using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Controls;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// The Settings window, built and laid out but never shown: every way in
/// opens it (the gears, the More menu, Canvas options and Ctrl+,), each of
/// its controls shows the setting the menus and headers use and changes it,
/// a change made elsewhere shows in it at once, and Reset all asks before it
/// lets every folder's own order go.  With it, the menus built but never
/// opened: the layers menu against the Layers section, and a folder's own
/// menu; and which folder the headers sort.  Set SETTINGS_SHOTS to a folder
/// to keep its pictures.
///
/// Runs last: the window needs the app's theme, and so the app itself, of
/// which a process can only ever have one.
/// </summary>
internal static partial class Program
{
    private static Task SettingsChecks()
    {
        RunOnSta("settings window", SettingsOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task SettingsOnStaAsync()
    {
        if (Application.Current is null)
        {
            new App().InitializeComponent();
        }

        var rendererBefore = GpuBootstrap.Preference;
        try
        {
            var main = new MainWindow();
            var shell = (MainViewModel)main.DataContext;
            var settings = SettingsEntryChecks(main);
            if (settings is not null)
            {
                await SettingsControlChecks(main, shell, settings);
                await LayerMenuChecks(main, shell, settings);
                settings.Settings.Dispose();
            }

            SortFolderChecks(main, shell);
            FolderMenuChecks(main, shell);
            await ShellMenuWindowChecks(main, shell);

            await SettingsResetChecks(shell);
            SettingsWordsChecks();
            ChromeFocusChecks(main);
            await SplitPaneWindowChecks(main, shell);
            await SplitViewWindowChecks(main, shell);
            shell.Dispose();
        }
        finally
        {
            // The renderer is the process's; the checks after these must find it as it was.
            if (GpuBootstrap.ExplicitPreference is null)
            {
                GpuBootstrap.UseSavedPreference(rendererBefore);
            }
        }
    }

    // ---- the ways in ------------------------------------------------------------------

    private static SettingsWindow? SettingsEntryChecks(MainWindow main)
    {
        Section("settings: the ways in");
        var shown = new List<SettingsWindow>();
        main.SettingsPresenter = shown.Add;

        var gear = main.SettingsButton;
        Check("the command bar has a gear that says what it is and its shortcut",
            gear.ToolTip as string == "Settings (Ctrl+,)"
            && System.Windows.Automation.AutomationProperties.GetName(gear) == "Settings"
            && gear.Content is TextBlock { Text: "\uE713" });

        gear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, gear));
        Check("the gear opens Settings", shown.Count == 1 && main.OpenSettingsWindow == shown[0]);

        var more = main.BuildOverflowMenu(gear);
        var first = more.Items[0] as MenuItem;
        Check("the More menu starts with Settings and its shortcut",
            first is { Header: "Settings", InputGestureText: "Ctrl+,", Icon: TextBlock { Text: "\uE713" } });
        first?.Command?.Execute(null);
        Check("the More menu's Settings opens it", shown.Count == 2);

        var options = main.BuildCanvasOptionsMenu(gear);
        var last = options.Items.OfType<MenuItem>().LastOrDefault();
        Check("Canvas options ends with Settings…", last is { Header: "Settings…", InputGestureText: "Ctrl+," });
        last?.Command?.Execute(null);
        Check("Canvas options' Settings… opens it", shown.Count == 3);

        Check("Ctrl+, opens it", main.TryOpenSettingsFromKey(Key.OemComma, ModifierKeys.Control) && shown.Count == 4);
        Check("a comma alone, Ctrl+Shift+, and Ctrl+. do not",
            !main.TryOpenSettingsFromKey(Key.OemComma, ModifierKeys.None)
            && !main.TryOpenSettingsFromKey(Key.OemComma, ModifierKeys.Control | ModifierKeys.Shift)
            && !main.TryOpenSettingsFromKey(Key.OemPeriod, ModifierKeys.Control)
            && shown.Count == 4);
        Check("asked again, the one window open is brought back, not a second one", shown.Distinct().Count() == 1);

        // The bottom corner: the canvas's own buttons and the status bar's.
        foreach (var corner in new[] { main.CanvasSettingsButton, main.StatusSettingsButton })
        {
            var before = shown.Count;
            Check($"the {(corner == main.CanvasSettingsButton ? "canvas's" : "status bar's")} bottom-right corner has the gear too, and it opens Settings",
                corner.ToolTip as string == "Settings (Ctrl+,)"
                && System.Windows.Automation.AutomationProperties.GetName(corner) == "Settings"
                && corner.Content is TextBlock { Text: "\uE713" }
                && Click(corner) && shown.Count == before + 1);
        }

        Check("beside each, the layers button says what it is",
            new[] { main.CanvasLayersButton, main.StatusLayersButton }.All(button =>
                System.Windows.Automation.AutomationProperties.GetName(button) == "Layers"
                && button.Content is TextBlock { Text: "\uE81E" }
                && (button.ToolTip as string)?.StartsWith("Layers", StringComparison.Ordinal) == true));

        return shown.FirstOrDefault();
    }

    // ---- the controls -----------------------------------------------------------------

    private static async Task SettingsControlChecks(MainWindow main, MainViewModel shell, SettingsWindow window)
    {
        Section("settings: the controls and the menus agree");
        LayOutWindow(window, 720, 780);
        await SettingsSettle();

        // Canvas: the view.
        Check("the view shows the nested canvas chosen",
            window.NestedLayoutChoice.IsChecked == true && window.TreeLayoutChoice.IsChecked == false);
        Choose(window.TreeLayoutChoice);
        await SettingsSettle();
        Check("choosing Tree canvas switches the window to it",
            shell.Layout == CanvasLayout.Tree && window.NestedLayoutChoice.IsChecked == false);
        Check("the minimap can be switched on the tree canvas", window.MinimapSwitch.IsEnabled);
        var minimapBefore = shell.IsMinimapVisible;
        Toggle(window.MinimapSwitch);
        Check("the minimap switch shows and hides the minimap",
            shell.IsMinimapVisible != minimapBefore && window.MinimapSwitch.IsChecked == shell.IsMinimapVisible);
        shell.Layout = CanvasLayout.Nested;
        await SettingsSettle();
        Check("the view changed elsewhere shows at once, and the minimap is the tree's only",
            window.NestedLayoutChoice.IsChecked == true && window.TreeLayoutChoice.IsChecked == false
            && !window.MinimapSwitch.IsEnabled);

        // Canvas: the split view.
        Check("the split view shows off, side by side", window.SplitSwitch.IsChecked == false && window.SplitSideBySideChoice.IsChecked == true);
        Toggle(window.SplitSwitch);
        await SettingsSettle();
        Check("its switch splits the view, as Ctrl+\\ does", shell.IsSplit && window.SplitSwitch.IsChecked == true);
        Choose(window.SplitStackedChoice);
        await SettingsSettle();
        Check("choosing Stacked lays the panes one above the other", shell.SplitOrientation == SplitOrientation.Stacked && window.SplitSideBySideChoice.IsChecked == false);
        shell.IsSplit = false;
        shell.SplitOrientation = SplitOrientation.SideBySide;
        await SettingsSettle();
        Check("the split changed elsewhere shows at once", window.SplitSwitch.IsChecked == false && window.SplitSideBySideChoice.IsChecked == true);

        // Canvas: the renderer.
        if (GpuBootstrap.ExplicitPreference is null)
        {
            Check("the renderer shows the setting in force", window.RendererAutoChoice.IsChecked == (shell.Renderer == RendererPreference.Auto));
            Choose(window.RendererCpuChoice);
            await SettingsSettle();
            Check("choosing Processor sets the renderer the menu sets",
                shell.Renderer == RendererPreference.Cpu && GpuBootstrap.Preference == RendererPreference.Cpu
                && window.RendererCpuChoice.IsChecked == true && window.RendererAutoChoice.IsChecked == false);
            var rendererMenu = main.BuildCanvasOptionsMenu(main.SettingsButton).Items.OfType<MenuItem>()
                .FirstOrDefault(item => item.Header as string == "Renderer");
            Check("and Canvas options says the same",
                rendererMenu?.Items.OfType<MenuItem>().FirstOrDefault(item => item.Header as string == "Processor")?.IsChecked == true);
        }
        else
        {
            Check("a renderer chosen on the command line cannot be changed here", !window.RendererCpuChoice.IsEnabled);
        }

        Check("the renderer says what draws the canvas now", window.Settings.RendererStatus.StartsWith("Now: ", StringComparison.Ordinal));

        // Canvas: hidden items.
        var hiddenBefore = shell.Tree.ShowHiddenItems;
        Toggle(window.HiddenItemsSwitch);
        Check("the hidden items switch shows the new choice at once", window.HiddenItemsSwitch.IsChecked == !hiddenBefore);
        Check("and the canvas follows it", await SettingsWaitFor(() => shell.Tree.ShowHiddenItems == !hiddenBefore));
        await shell.Tree.SetShowHiddenItemsAsync(hiddenBefore);
        await SettingsSettle();
        Check("hidden items changed elsewhere shows at once", window.HiddenItemsSwitch.IsChecked == hiddenBefore);

        // Sorting: the default order.
        var orders = shell.Orders;
        Check("the default order shows names, A to Z",
            window.DefaultColumnBox.SelectedIndex == (int)SortColumn.Name && window.AscendingChoice.IsChecked == true
            && window.DefaultColumnBox.Items.Count == 4 && window.DefaultColumnBox.Items[1] as string == "Date modified");
        window.DefaultColumnBox.SetCurrentValue(Selector.SelectedIndexProperty, (int)SortColumn.Modified);
        await SettingsSettle();
        Check("choosing Date modified sorts every folder newest first, as a header would",
            orders.Default == new ItemSort(SortColumn.Modified, true) && window.DescendingChoice.IsChecked == true);
        Choose(window.AscendingChoice);
        await SettingsSettle();
        Check("Ascending turns it round", orders.Default == new ItemSort(SortColumn.Modified, false));
        orders.SetDefault(new ItemSort(SortColumn.Size, true));
        await SettingsSettle();
        Check("an order chosen with a header shows at once",
            window.DefaultColumnBox.SelectedIndex == (int)SortColumn.Size && window.DescendingChoice.IsChecked == true
            && window.AscendingChoice.IsChecked == false);
        orders.SetDefault(ItemSort.Default);

        // Sorting: the scope.
        Check("sorting shows each folder separately", window.PerFolderChoice.IsChecked == true);
        Choose(window.AllFoldersChoice);
        await SettingsSettle();
        var sortMenu = main.BuildCanvasOptionsMenu(main.SettingsButton).Items.OfType<MenuItem>()
            .FirstOrDefault(item => item.Header as string == "Sort");
        Check("All folders the same sorts every folder alike, and Canvas options says so",
            orders.Scope == SortScope.AllFolders
            && sortMenu?.Items.OfType<MenuItem>().FirstOrDefault(item => item.Header as string == "All folders the same")?.IsChecked == true);
        orders.Scope = SortScope.PerFolder;
        await SettingsSettle();
        Check("the scope changed from the menu shows at once",
            window.PerFolderChoice.IsChecked == true && window.AllFoldersChoice.IsChecked == false);

        // Sorting: the fill order.
        Check("the fill order shows down, then across", window.DownThenAcrossChoice.IsChecked == true);
        Choose(window.AcrossThenDownChoice);
        await SettingsSettle();
        Check("Across, then down fills along the rows", orders.Flow == LayoutOrder.AcrossThenDown && window.DownThenAcrossChoice.IsChecked == false);
        orders.Flow = LayoutOrder.DownThenAcross;
        await SettingsSettle();
        Check("the fill order changed from the menu shows at once", window.DownThenAcrossChoice.IsChecked == true);

        // Mouse.
        Check("left drag shows selecting an area", window.SelectAreaChoice.IsChecked == true);
        Choose(window.PanChoice);
        await SettingsSettle();
        Check("Move the canvas makes left drag pan, and says how to select instead",
            shell.LeftDrag == NestedLeftDrag.Pan && window.Settings.LeftDragDescription.Contains("Shift+drag", StringComparison.Ordinal));
        shell.LeftDrag = NestedLeftDrag.SelectArea;
        await SettingsSettle();
        Check("left drag changed from the menu shows at once", window.SelectAreaChoice.IsChecked == true && window.PanChoice.IsChecked == false);

        // About.
        Check("About names the version, the program's folder and the state folder",
            window.Settings.Version.StartsWith("Version ", StringComparison.Ordinal)
            && window.Settings.InstallFolder.Length > 0
            && window.Settings.StateFolder == AppPaths.StateDirectory);

        // The page itself.
        Section("settings: the page");
        var picture = LayOutWindow(window, 720, 780);
        SaveSettingsShot(picture, "settings-720.png");
        Check("the page draws its cards", CountColour(picture, 0xFF2B2B2B) > picture.PixelWidth * picture.PixelHeight / 10);
        Check("at its usual width every choice sits beside its words", CardPanels(SettingsHosts[window]).All(panel => !panel.IsStacked));
        var narrow = LayOutWindow(window, 560, 780);
        SaveSettingsShot(narrow, "settings-560.png");
        Check("at its narrowest the wide choices move under their words rather than squeeze them",
            CardPanels(SettingsHosts[window]).Any(panel => panel.IsStacked));

        ScrollPage(window, 1);
        SaveSettingsShot(LayOutWindow(window, 720, 780), "settings-720-end.png");
        ScrollPage(window, 0);
    }

    // ---- layers ------------------------------------------------------------------------

    /// <summary>The layers menu and the Layers section: each switch in one shows in the other, and in the canvas.</summary>
    private static async Task LayerMenuChecks(MainWindow main, MainViewModel shell, SettingsWindow window)
    {
        Section("settings: layers");
        MenuItem? Item(ItemsControl menu, string header) => menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Header as string == header);
        string?[] Headers(ItemsControl menu) => [.. menu.Items.OfType<MenuItem>().Select(item => item.Header as string)];
        string[] switches = ["Files", "Icons", "Details", "Folder counts", "Hidden items", "Marks and notes"];

        var menu = main.BuildLayersMenu(main.CanvasLayersButton);
        Check("the layers button's menu is a switch for every layer, hidden items among them, then the minimap and Show all layers",
            Headers(menu).SequenceEqual([.. switches, "Minimap", "Show all layers"])
            && switches.Where(name => name != "Hidden items").All(name => Item(menu, name) is { IsCheckable: true, IsChecked: true, StaysOpenOnClick: true })
            && Item(menu, "Hidden items")?.IsChecked == shell.Tree.ShowHiddenItems
            && Item(menu, "Minimap") is { IsEnabled: false }
            && Item(menu, "Show all layers") is { IsEnabled: false }
            && menu.Placement == System.Windows.Controls.Primitives.PlacementMode.Top);
        Check("and every layer switch says what it shows", switches.All(name => Item(menu, name)?.ToolTip is string { Length: > 0 }));

        var files = Item(menu, "Files")!;
        files.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, files));
        await SettingsSettle();
        Check("Files off from the menu: the canvas, the switch in the menu and the one in Settings all say so at once",
            !shell.IsLayerShown(CanvasLayer.Files) && (main.Nested.ShownLayers & CanvasLayer.Files) == 0
            && files.IsChecked == false && window.FilesLayerSwitch.IsChecked == false && Item(menu, "Show all layers")!.IsEnabled);

        Toggle(window.IconsLayerSwitch);
        await SettingsSettle();
        var again = main.BuildLayersMenu(main.StatusLayersButton);
        Check("Icons off in Settings: the canvas and the menu show it",
            !shell.IsLayerShown(CanvasLayer.Icons) && (main.Nested.ShownLayers & CanvasLayer.Icons) == 0
            && Item(again, "Icons")?.IsChecked == false && Item(again, "Files")?.IsChecked == false);

        Toggle(window.DetailsLayerSwitch);
        Toggle(window.FolderCountsLayerSwitch);
        Toggle(window.MarksLayerSwitch);
        await SettingsSettle();
        Check("each of the other switches in Settings takes its layer off",
            shell.Layers == CanvasLayer.None && main.Nested.ShownLayers == CanvasLayer.None && window.ShowAllLayersButton.IsEnabled);

        var options = main.BuildCanvasOptionsMenu(main.SettingsButton);
        var layers = Item(options, "Layers");
        Check("Canvas options has the same switches under Layers, without the minimap",
            layers is not null && Headers(layers).SequenceEqual([.. switches, "Show all layers"])
            && Item(layers, "Marks and notes")?.IsChecked == false && Item(options, "Hidden items") is null);

        Invoke(window.ShowAllLayersButton);
        await SettingsSettle();
        Check("Show all in Settings brings every layer back, and the switches and the menus with it",
            shell.Layers == CanvasLayer.All && main.Nested.ShownLayers == CanvasLayer.All
            && new[] { window.FilesLayerSwitch, window.IconsLayerSwitch, window.DetailsLayerSwitch, window.FolderCountsLayerSwitch, window.MarksLayerSwitch }.All(toggle => toggle.IsChecked == true)
            && !window.ShowAllLayersButton.IsEnabled && Item(main.BuildLayersMenu(main.CanvasLayersButton), "Show all layers")?.IsEnabled == false);

        shell.SetLayer(CanvasLayer.Details, false);
        var menuShowAll = main.BuildLayersMenu(main.CanvasLayersButton);
        var showAll = Item(menuShowAll, "Show all layers")!;
        showAll.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, showAll));
        Check("the menu's Show all layers does the same", shell.Layers == CanvasLayer.All && Item(menuShowAll, "Details")?.IsChecked == true);

        shell.Layout = CanvasLayout.Tree;
        await SettingsSettle();
        var onTree = main.BuildLayersMenu(main.CanvasLayersButton);
        Check("on the tree canvas the menu says whose layers they are, and the minimap can be switched",
            onTree.Items[0] is MenuItem { Header: "Shown on the nested canvas", IsEnabled: false } && Item(onTree, "Minimap")?.IsEnabled == true);
        shell.Layout = CanvasLayout.Nested;
        await SettingsSettle();

        // The section itself, scrolled to, for the pictures.
        if (window.PageScroll.Content is UIElement page)
        {
            window.PageScroll.ScrollToVerticalOffset(window.FilesLayerSwitch.TranslatePoint(new Point(0, 0), page).Y - 90);
            SaveSettingsShot(LayOutWindow(window, 720, 780), "settings-layers.png");
            ScrollPage(window, 0);
        }
    }

    // ---- which folder the headers sort --------------------------------------------------

    private static void SortFolderChecks(MainWindow main, MainViewModel shell)
    {
        Section("settings: the folder the headers sort");
        Check("a sub-folder selected in the folder in view is the one sorted, not the folder around it",
            MainWindow.NestedSortFolder(@"C:\a\b", @"C:\a") == @"C:\a\b");
        Check("the folder in view, selected, is itself sorted", MainWindow.NestedSortFolder(@"C:\a", @"C:\a") == @"C:\a");
        Check("a selection outside the folder in view - or above it, a drive picked at the start - leaves the folder in view sorted",
            MainWindow.NestedSortFolder(@"C:\x", @"C:\a") == @"C:\a" && MainWindow.NestedSortFolder(@"C:\", @"C:\a") == @"C:\a"
            && MainWindow.NestedSortFolder(@"C:\ab", @"C:\a") == @"C:\a");
        Check("nothing selected, the folder in view; nothing in view, the selection; neither, every folder",
            MainWindow.NestedSortFolder(null, @"C:\a") == @"C:\a" && MainWindow.NestedSortFolder(@"C:\a", null) == @"C:\a"
            && MainWindow.NestedSortFolder(null, null) is null);

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerSortFolder", Guid.NewGuid().ToString("N"));
        var inner = Path.Combine(root, "Inner");
        var file = Path.Combine(root, "note.txt");
        var orders = shell.Orders;
        try
        {
            Directory.CreateDirectory(inner);
            File.WriteAllText(file, "x");
            var selection = shell.Tree.Selection;
            var headers = main.ActivePane.View;
            orders.SetFolder(inner, new ItemSort(SortColumn.Modified, true));
            selection.ReplaceSingle(inner, true, 0, SelectionSource.Navigation);
            Check("on the nested canvas a selected folder is what the headers sort, and they show its own order at once",
                main.SortFolder() == inner && headers.SortByModifiedArrow.Visibility == Visibility.Visible
                && headers.SortByModified.ToolTip is string tip && tip.StartsWith("Sorted by Date modified", StringComparison.Ordinal) && tip.Contains("in Inner", StringComparison.Ordinal));

            selection.ReplaceSingle(file, false, 1, SelectionSource.Navigation);
            Check("a selected file's folder is what they sort",
                main.SortFolder() == root && headers.SortByNameArrow.Visibility == Visibility.Visible
                && headers.SortByName.ToolTip is string nameTip && nameTip.Contains($"in {Path.GetFileName(root)}", StringComparison.Ordinal));

            selection.ReplaceSingle(inner, true, 0, SelectionSource.Navigation);
            headers.SortByName.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, headers.SortByName));
            Check("and a header click sorts the selected folder, not the one it is in",
                orders.SortOf(inner).Column == SortColumn.Name && !orders.HasOwnOrder(root));
            selection.Clear(SelectionSource.Navigation);
        }
        finally
        {
            orders.ResetFolder(inner);
            orders.ResetFolder(root);
            TryDelete(root);
        }
    }

    // ---- a folder's own menu ------------------------------------------------------------

    private static void FolderMenuChecks(MainWindow main, MainViewModel shell)
    {
        Section("settings: a folder's own menu");
        MenuItem? Item(ItemsControl menu, string header) => menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Header as string == header);
        // A click as the item makes one: its command where it has one, its handlers otherwise.
        void Choose(MenuItem item)
        {
            if (item.Command is { } command)
            {
                command.Execute(item.CommandParameter);
                return;
            }

            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
        }

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerFolderMenu", Guid.NewGuid().ToString("N"));
        var docs = Path.Combine(root, "Docs");
        var orders = shell.Orders;
        try
        {
            Directory.CreateDirectory(docs);
            var menu = main.BuildFolderAreaMenu(main.SettingsButton, docs);
            string?[] headers = [.. menu.Items.OfType<MenuItem>().Select(item => item.Header as string)];
            Check("a right-click in a folder names it, and offers its own settings between what goes in it and the canvas's commands",
                headers.Take(9).SequenceEqual(
                [
                    "New folder in Docs", "New text file in Docs", "Paste into Docs",
                    "Sort Docs by", "Colour", "Add note\u2026", "Pin to Home", "Show in File Explorer", "Properties"
                ])
                && headers.Contains("Fit all") && headers.Contains("Folder list") && !headers.Contains("Sort by"));

            var sortBy = Item(menu, "Sort Docs by")!;
            string?[] sortHeaders = [.. sortBy.Items.OfType<MenuItem>().Select(item => item.Header as string)];
            Check("Sort Docs by: the four columns, which way round, back to the default, and this order for every folder",
                sortHeaders.SequenceEqual(["Name", "Date modified", "Type", "Size", "Ascending", "Descending", "Reset to the default order", "Use this order for all folders"])
                && Item(sortBy, "Name")?.IsChecked == true && Item(sortBy, "Reset to the default order")?.IsEnabled == false);
            Choose(Item(sortBy, "Date modified")!);
            Check("choosing Date modified sorts that folder alone, newest first", orders.SortOf(docs) == new ItemSort(SortColumn.Modified, true) && orders.SortOf(root) == ItemSort.Default);
            var reset = Item(Item(main.BuildFolderAreaMenu(main.SettingsButton, docs), "Sort Docs by")!, "Reset to the default order")!;
            Check("then it can be reset", reset.IsEnabled);
            Choose(reset);
            Check("and reset, it is on the default again", !orders.HasOwnOrder(docs));

            Check("Properties only says Alt+Enter while the folder is what is selected",
                Item(menu, "Properties") is { InputGestureText: "" } && Item(menu, "Add note\u2026")?.Icon is TextBlock { Text: "\uE70B" });
            shell.Tree.Selection.ReplaceSingle(docs, true, 0, SelectionSource.Navigation);
            Check("- as the right-click that opens the menu leaves it",
                Item(main.BuildFolderAreaMenu(main.SettingsButton, docs), "Properties") is { InputGestureText: "Alt+Enter" });
            shell.Tree.Selection.Clear(SelectionSource.Navigation);

            Choose(Item(menu, "Pin to Home")!);
            var pinned = shell.IsPinned(docs);
            var unpin = Item(main.BuildFolderAreaMenu(main.SettingsButton, docs), "Unpin from Home");
            if (unpin is not null)
            {
                Choose(unpin);
            }

            Check("Pin to Home pins the folder, whatever is selected, and its menu then offers Unpin, which takes it off",
                pinned && unpin is not null && !shell.IsPinned(docs));

            var colour = Item(menu, "Colour")!;
            Choose(colour.Items.OfType<MenuItem>().First(item => item.Header as string == "Red"));
            var red = shell.Marks.Get(docs).AccentHex;
            var again = Item(main.BuildFolderAreaMenu(main.SettingsButton, docs), "Colour")!;
            var current = again.Items.OfType<MenuItem>().Where(item => item.InputGestureText == "Current").Select(item => item.Header as string).ToList();
            Choose(again.Items.OfType<MenuItem>().First(item => item.Header as string == "Default"));
            Check("Colour colours that folder, and its menu then says which colour it has",
                red == "#EF5A68" && current.SequenceEqual(["Red"]) && shell.Marks.Get(docs).AccentHex.Length == 0);

            var outside = main.BuildFolderAreaMenu(main.SettingsButton, null);
            Check("outside every folder there is no folder to set: Sort by orders them all",
                Item(outside, "Sort by") is not null && Item(outside, "Properties") is null && Item(outside, "Colour") is null);
        }
        finally
        {
            orders.ResetFolder(docs);
            shell.UnpinPath(docs);
            TryDelete(root);
        }
    }

    // ---- reset all --------------------------------------------------------------------

    private static async Task SettingsResetChecks(MainViewModel shell)
    {
        Section("settings: reset all");
        var answer = false;
        var asked = new List<string>();
        var opened = new List<string>();

        // Opened while every folder is sorted by something other than the
        // list's first column: the list must start on it, not on Name.
        shell.Orders.SetDefault(new ItemSort(SortColumn.Size, true));
        var settings = new SettingsViewModel(
            shell,
            canChooseLayout: true,
            rendererNow: null,
            confirm: (title, message, label) =>
            {
                asked.Add($"{title}|{message}|{label}");
                return answer;
            },
            openFolder: opened.Add);
        var window = new SettingsWindow(settings);
        LayOutWindow(window, 720, 780);
        await SettingsSettle();
        Check("a window opened on an order by size, largest first, shows it",
            window.DefaultColumnBox.SelectedIndex == (int)SortColumn.Size && window.DescendingChoice.IsChecked == true
            && shell.Orders.Default == new ItemSort(SortColumn.Size, true));

        var orders = shell.Orders;
        orders.UseEverywhere(ItemSort.Default);
        await SettingsSettle();
        Check("with no folder sorted on its own there is nothing to reset",
            !window.ResetOrdersButton.IsEnabled && settings.OwnOrdersTitle == "Folders with their own order: 0");

        orders.SetFolder(@"C:\Downloads", new ItemSort(SortColumn.Modified, true));
        orders.SetFolder(@"C:\Music", new ItemSort(SortColumn.Size, true));
        await SettingsSettle();
        Check("two folders sorted on their own are counted, and can be reset",
            settings.OwnOrdersTitle == "Folders with their own order: 2" && window.ResetOrdersButton.IsEnabled);

        Invoke(window.ResetOrdersButton);
        await SettingsSettle();
        Check("Reset all asks first, and a No keeps every order",
            asked.Count == 1 && asked[0].Contains("Reset all", StringComparison.Ordinal) && orders.Count == 2
            && orders.SortOf(@"C:\Downloads") == new ItemSort(SortColumn.Modified, true));

        answer = true;
        Invoke(window.ResetOrdersButton);
        await SettingsSettle();
        Check("a Yes lets every folder's own order go, and the default stays",
            asked.Count == 2 && orders.Count == 0 && orders.Default == ItemSort.Default
            && orders.SortOf(@"C:\Downloads") == ItemSort.Default);
        Check("and the count and the button say so at once",
            settings.OwnOrdersTitle == "Folders with their own order: 0" && !window.ResetOrdersButton.IsEnabled);

        settings.OpenStateFolderCommand.Execute(null);
        Check("Open shows the state folder in Explorer", opened.SequenceEqual([AppPaths.StateDirectory]));

        using (var picker = new SettingsViewModel(shell, canChooseLayout: false, rendererNow: null, confirm: (_, _, _) => false))
        {
            Check("a file dialog's page does not promise to remember what a file dialog never writes",
                settings.Intro.EndsWith("is remembered.", StringComparison.Ordinal)
                && picker.Intro.Contains("file dialog does not remember", StringComparison.Ordinal));
        }

        var raised = 0;
        var closed = false;
        settings.PropertyChanged += (_, _) => raised++;
        window.Closed += (_, _) => closed = true;
        PressEscape(window);
        Check("Esc puts the window away", closed);
        orders.Flow = LayoutOrder.AcrossThenDown;
        orders.Flow = LayoutOrder.DownThenAcross;
        Check("a closed window's settings stop listening", raised == 0);
    }

    /// <summary>
    /// The window's own buttons never take the keyboard, so the selection's
    /// keys still work once one has been clicked; the dialogs' buttons still
    /// do, so a dialog can be answered from the keyboard.
    /// </summary>
    private static void ChromeFocusChecks(MainWindow main)
    {
        Section("the window's buttons and the keyboard");
        var chrome = new (string Name, UIElement Button)[]
        {
            ("Settings", main.SettingsButton),
            ("the canvas's Settings", main.CanvasSettingsButton),
            ("the status bar's Settings", main.StatusSettingsButton),
            ("the canvas's Layers", main.CanvasLayersButton),
            ("New folder", main.NewFolderButton),
            ("Split view", main.SplitButton),
            ("Split view's options", main.SplitMenuButton),
            ("the filter's Previous", main.ActivePane.View.CanvasFilterPrevious),
            ("the filter's Next", main.ActivePane.View.CanvasFilterNext),
            ("Maximize", main.MaximizeButton)
        };
        var taking = chrome.Where(item => item.Button.Focusable).Select(item => item.Name).ToArray();
        Check($"no button of the window takes the keyboard when clicked ({(taking.Length == 0 ? "none does" : string.Join(", ", taking))})",
            taking.Length == 0);

        var dialogButton = new System.Windows.Controls.Button { Style = (Style)Application.Current.FindResource("FlatButton") };
        var accent = new System.Windows.Controls.Button { Style = (Style)Application.Current.FindResource("AccentButton") };
        Check("a dialog's buttons still do", dialogButton.Focusable && accent.Focusable);
    }

    private static void SettingsWordsChecks()
    {
        Section("settings: what draws the canvas, in words");
        Check("the graphics card by name",
            SettingsViewModel.DescribeRendererNow(true, true, "NVIDIA GeForce RTX 4090", GpuBootstrap.ReasonReady)
                == "Now: graphics card (NVIDIA GeForce RTX 4090)");
        Check("the processor, and why",
            SettingsViewModel.DescribeRendererNow(true, false, string.Empty, GpuBootstrap.ReasonRemoteSession)
                == "Now: processor — remote session");
        Check("nothing while the tree canvas shows",
            SettingsViewModel.DescribeRendererNow(false, false, string.Empty, GpuBootstrap.ReasonNotOnScreen).Contains("tree canvas", StringComparison.Ordinal));
    }

    // ---- helpers ----------------------------------------------------------------------

    /// <summary>A click on a radio button, as UI Automation makes one.</summary>
    private static void Choose(RadioButton radio) =>
        ((ISelectionItemProvider)UIElementAutomationPeer.CreatePeerForElement(radio).GetPattern(PatternInterface.SelectionItem)).Select();

    /// <summary>A click on a switch, as UI Automation makes one.</summary>
    private static void Toggle(ToggleButton toggle) =>
        ((IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(toggle).GetPattern(PatternInterface.Toggle)).Toggle();

    /// <summary>A click on a button, raised on it directly: its handler runs at once.</summary>
    private static bool Click(ButtonBase button)
    {
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        return true;
    }

    /// <summary>A click on a button, as UI Automation makes one: it runs on the dispatcher, so settle after.</summary>
    private static void Invoke(Button button) =>
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke)).Invoke();

    /// <summary>
    /// Esc pressed in the window, as the keyboard sends it: down from the
    /// window first.  A key needs a source to come from; a message-only
    /// window is one that never appears.
    /// </summary>
    private static void PressEscape(Window window)
    {
        using var source = new HwndSource(new HwndSourceParameters("UltraExplorer settings key check") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }

    private static async Task SettingsSettle() => await Dispatcher.Yield(DispatcherPriority.ContextIdle);

    private static async Task<bool> SettingsWaitFor(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        return condition();
    }

    /// <summary>
    /// Lays the window's page out at a size and draws it.  A window that was
    /// never shown draws nothing, so the page moves - the first time - into a
    /// plain element that carries what the window gave it: its resources,
    /// its data, its font and colours and its background.
    /// </summary>
    private static RenderTargetBitmap LayOutWindow(Window window, double width, double height, double scale = 1)
    {
        if (window.Content is FrameworkElement page)
        {
            window.Content = null;
            var host = new Border
            {
                Background = window.Background,
                Child = page,
                DataContext = window.DataContext,
                SnapsToDevicePixels = true
            };
            host.Resources.MergedDictionaries.Add(window.Resources);
            TextElement.SetFontFamily(host, window.FontFamily);
            TextElement.SetFontSize(host, window.FontSize);
            TextElement.SetForeground(host, window.Foreground);
            TextOptions.SetTextFormattingMode(host, TextOptions.GetTextFormattingMode(window));
            TextOptions.SetTextRenderingMode(host, TextOptions.GetTextRenderingMode(window));
            SettingsHosts[window] = host;
        }

        var root = SettingsHosts[window];
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        var picture = new RenderTargetBitmap(
            (int)Math.Round(width * scale), (int)Math.Round(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        picture.Render(root);
        return picture;
    }

    private static readonly Dictionary<Window, FrameworkElement> SettingsHosts = [];

    /// <summary>Scrolls the page: 0 the top, 1 the bottom.</summary>
    private static void ScrollPage(SettingsWindow window, double at)
    {
        window.PageScroll.ScrollToVerticalOffset(at * window.PageScroll.ScrollableHeight);
        window.UpdateLayout();
    }

    private static IEnumerable<SettingCardPanel> CardPanels(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is SettingCardPanel panel)
            {
                yield return panel;
            }

            foreach (var inner in CardPanels(child))
            {
                yield return inner;
            }
        }
    }

    private static int CountColour(BitmapSource picture, uint argb)
    {
        var pixels = new uint[picture.PixelWidth * picture.PixelHeight];
        picture.CopyPixels(pixels, picture.PixelWidth * 4, 0);
        return pixels.Count(pixel => pixel == argb);
    }

    private static void SaveSettingsShot(BitmapSource picture, string name)
    {
        if (Environment.GetEnvironmentVariable("SETTINGS_SHOTS") is not { Length: > 0 } folder)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using var stream = File.Create(Path.Combine(folder, name));
        encoder.Save(stream);
    }
}
