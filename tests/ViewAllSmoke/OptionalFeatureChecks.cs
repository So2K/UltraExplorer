using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltraExplorer;
using UltraExplorer.Controls;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;
using UltraExplorer.Services.Archives;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task OptionalFeatureChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(OptionalFeatureChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(OptionalFeatureChecks));
            return Task.CompletedTask;
        }
        RunOnSta("optional PR4 features", OptionalFeaturesOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task OptionalFeaturesOnStaAsync()
    {
        Section("optional features: opt-in, independent saves and live controls");
        var isolated = Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1"
            && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable))
            && !ViewAllPath.Equals(AppPaths.StateDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer"));
        Check("optional features require an isolated test profile", isolated);
        if (!isolated) return;
        var legacy = JsonSerializer.Deserialize<WorkspaceState>("{\"SchemaVersion\":2}")!;
        Check("older workspaces keep all three optional features disabled", !legacy.BrowseArchives && !legacy.ShowDropShelf && !legacy.ShowCopyPathButton);
        var workspace = AppPaths.State("workspace.json");
        var original = File.Exists(workspace) ? File.ReadAllBytes(workspace) : null;
        var originalTime = original is null ? (DateTime?)null : File.GetLastWriteTimeUtc(workspace);
        var rendererBefore = GpuBootstrap.Preference;
        var archivesBefore = ArchiveService.BrowseArchives;
        var fixture = Path.Combine(AppPaths.StateDirectory, "optional-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var store = new WorkspaceStore();
        var models = new List<MainViewModel>();
        MainWindow? main = null;
        SettingsWindow? window = null;
        DropShelf? shelf = null;
        try
        {
            await store.SaveAsync(new WorkspaceState { CanvasRenderer = "Cpu", ShowHoverPreviews = true });
            async Task<MainViewModel> Model(string name, bool picker = false)
            {
                var model = picker
                    ? new MainViewModel(Path.Combine(fixture, name + ".json"), true) { SuppressShellWrites = true }
                    : new MainViewModel(Path.Combine(fixture, name + ".json"), false, Path.Combine(fixture, name + "-workspace.json"));
                models.Add(model);
                await model.InitializeAsync(fixture);
                return model;
            }
            var first = await Model("first");
            var stale = await Model("stale");
            first.BrowseArchives = true;
            await first.SaveNowAsync();
            stale.ShowDropShelf = true;
            await stale.SaveNowAsync();
            var saved = (await store.LoadAsync())!;
            Check("an older window enables the shelf without undoing archive browsing", saved.BrowseArchives && saved.ShowDropShelf && !saved.ShowCopyPathButton);
            first.ShowCopyPathButton = true;
            await first.SaveNowAsync();
            saved = (await store.LoadAsync())!;
            Check("independent controls preserve the other window's choice", saved.BrowseArchives && saved.ShowDropShelf && saved.ShowCopyPathButton && saved.ShowHoverPreviews);
            var reopened = await Model("reopened");
            Check("all three choices restore after reopening", reopened.BrowseArchives && reopened.ShowDropShelf && reopened.ShowCopyPathButton);
            reopened.BrowseArchives = false;
            await reopened.SaveNowAsync();
            Check("disabling archives leaves shelf and path button enabled", await store.LoadAsync() is { BrowseArchives: false, ShowDropShelf: true, ShowCopyPathButton: true });
            first.ShowCopyPathButton = false;
            first.ShowCopyPathButton = true;
            first.ShowCopyPathButton = false;
            await first.SaveNowAsync();
            Check("rapid queued changes save the final choice without restoring a stale archive flag", await store.LoadAsync() is { BrowseArchives: false, ShowCopyPathButton: false, ShowDropShelf: true });
            var picker = await Model("picker", picker: true);
            picker.BrowseArchives = picker.ShowDropShelf = picker.ShowCopyPathButton = true;
            picker.ShowHoverPreviews = false;
            await picker.SaveNowAsync();
            Check("picker cannot activate filesystem-only features", !picker.BrowseArchives && !picker.ShowDropShelf && !picker.ShowCopyPathButton && !picker.Tree.BrowseArchives);
            Check("picker saves preserve the optional settings", await store.LoadAsync() is { BrowseArchives: false, ShowDropShelf: true, ShowCopyPathButton: false });

            if (Application.Current is null)
            {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
                    application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
            }
            await store.SaveAsync(new WorkspaceState { CanvasRenderer = "Cpu", IsSplit = true });
            main = new MainWindow();
            var shell = (MainViewModel)main.DataContext;
            await shell.InitializeAsync(fixture);
            await main.StartNestedForChecksAsync();
            using var settings = new SettingsViewModel(shell, true, null, (_, _, _) => false);
            window = new SettingsWindow(settings);
            LayOutWindow(window, 720, 780);
            await SettingsSettle();
            Check("Settings offers three accessible switches disabled by default",
                window.BrowseArchivesSwitch.IsChecked == false && window.DropShelfSwitch.IsChecked == false && window.CopyPathButtonSwitch.IsChecked == false
                && AutomationProperties.GetName(window.BrowseArchivesSwitch) == "Browse archives as folders");
            if (Environment.GetEnvironmentVariable("PR4_SHOTS") is { Length: > 0 } shots)
            {
                window.PageScroll.ScrollToVerticalOffset(365);
                await SettingsSettle();
                Directory.CreateDirectory(shots);
                var bitmap = LayOutWindow(window, 720, 780);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(shots, "optional-features-settings.png"));
                encoder.Save(stream);
            }
            Check("both split panes start with optional archive browsing and copy path off",
                main.Panes.Count == 2 && main.Panes.All(p => !p.Tree.ArchivesEnabled && !p.Canvas.ShowCopyPathButton) && !main.Shelf.Enabled);
            Toggle(window.BrowseArchivesSwitch);
            Toggle(window.CopyPathButtonSwitch);
            Toggle(window.DropShelfSwitch);
            await main.Shelf.Ready;
            await SettingsSettle();
            Check("Settings activates both split panes and shelf without reopening", main.Panes.All(p => p.Tree.ArchivesEnabled && p.Canvas.ShowCopyPathButton) && main.Shelf.Enabled);
            shell.BrowseArchives = shell.ShowCopyPathButton = shell.ShowDropShelf = false;
            await SettingsSettle();
            Check("turning features off updates controls and both panes immediately", main.Panes.All(p => !p.Tree.ArchivesEnabled && !p.Canvas.ShowCopyPathButton)
                && main.Shelf.Visibility == Visibility.Collapsed && window.BrowseArchivesSwitch.IsChecked == false && window.DropShelfSwitch.IsChecked == false && window.CopyPathButtonSwitch.IsChecked == false);

            var shelfStore = new ShelfStore(Path.Combine(fixture, "shelf"));
            var sourceFile = Path.Combine(fixture, "keep.txt");
            await File.WriteAllTextAsync(sourceFile, "retained shelf card");
            await shelfStore.AddAsync([sourceFile]);
            var shelfBytes = File.ReadAllBytes(Path.Combine(shelfStore.Root, "shelf.json"));
            shelf = new DropShelf();
            shelf.Initialize(shelfStore, shell);
            shelf.NotifyDragOver();
            Check("disabled shelf ignores drag activation and keeps its cards untouched", !shelf.Enabled && shelf.Visibility == Visibility.Collapsed && shelf.Board.Cards.Count == 0 && shelfStore.Entries.Count == 1);
            Check("an OFF shelf does not write its saved cards", File.ReadAllBytes(Path.Combine(shelfStore.Root, "shelf.json")).SequenceEqual(shelfBytes));
            shelf.Enabled = true;
            await shelf.Ready;
            Check("enabling shelf restores its saved card", shelf.Board.Cards.Count == 1);
            shelfBytes = File.ReadAllBytes(Path.Combine(shelfStore.Root, "shelf.json"));
            shelf.Enabled = false;
            Check("disabling shelf clears previews and preserves saved data and copied file", shelf.Board.Cards.Count == 0 && shelfStore.Entries.Count == 1
                && File.Exists(shelfStore.Entries[0].Path) && File.ReadAllBytes(Path.Combine(shelfStore.Root, "shelf.json")).SequenceEqual(shelfBytes));
            shelf.Enabled = true;
            await shelf.Ready;
            Check("reenabling the shelf brings the same card back", shelf.Board.Cards.Count == 1 && shelf.Board.Cards[0].Entry.Path == shelfStore.Entries[0].Path);
        }
        finally
        {
            shelf?.Dispose();
            window?.Close();
            if (main is not null)
            {
                main.Close();
                await SettingsSettle();
                ((MainViewModel)main.DataContext).Dispose();
            }
            foreach (var model in models) model.Dispose();
            await SettingsSettle();
            ArchiveService.BrowseArchives = archivesBefore;
            GpuBootstrap.UseSavedPreference(rendererBefore);
            if (original is not null) { await File.WriteAllBytesAsync(workspace, original); File.SetLastWriteTimeUtc(workspace, originalTime!.Value); }
            else if (File.Exists(workspace)) File.Delete(workspace);
            TryDelete(fixture);
        }
    }
}
