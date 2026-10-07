using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task HoverPreviewPreferenceChecks()
    {
        RunOnSta("hover preview preferences", HoverPreviewPreferenceOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task HoverPreviewPreferenceOnStaAsync()
    {
        Section("hover previews: remembered preference and independent window saves");
        Check("new and older workspaces enable hover previews",
            new WorkspaceState().ShowHoverPreviews
            && JsonSerializer.Deserialize<WorkspaceState>("{\"SchemaVersion\":2}")!.ShowHoverPreviews);
        Check("an explicitly disabled preference survives JSON",
            !JsonSerializer.Deserialize<WorkspaceState>(JsonSerializer.Serialize(
                new WorkspaceState { ShowHoverPreviews = false }))!.ShowHoverPreviews);

        var stateOverride = Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable);
        var everydayState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer");
        var isolated = !string.IsNullOrWhiteSpace(stateOverride)
            && ViewAllPath.Equals(stateOverride!, AppPaths.StateDirectory)
            && !ViewAllPath.Equals(AppPaths.StateDirectory, everydayState)
            && Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1";
        Check("hover preference checks require isolated state and test-window mode", isolated);
        if (!isolated) return;

        Directory.CreateDirectory(AppPaths.StateDirectory);
        var workspacePath = AppPaths.State("workspace.json");
        var original = File.Exists(workspacePath) ? File.ReadAllBytes(workspacePath) : null;
        var originalTime = original is null ? (DateTime?)null : File.GetLastWriteTimeUtc(workspacePath);
        var rendererBefore = GpuBootstrap.Preference;
        var fixture = Path.Combine(AppPaths.StateDirectory, "hover-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var store = new WorkspaceStore();
        var models = new List<MainViewModel>();
        try
        {
            await store.SaveAsync(new WorkspaceState
            {
                ShowHoverPreviews = false, CanvasRenderer = "Cpu", CanvasLayout = "Tree",
                SidebarWidth = 286, IsSplit = true, CanvasLayersOff = ["Details"]
            });
            var first = new MainViewModel();
            models.Add(first);
            using var settings = new SettingsViewModel(first, true, null, (_, _, _) => false);
            var mainNotices = 0;
            var settingsNotices = 0;
            first.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.ShowHoverPreviews)) mainNotices++; };
            settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SettingsViewModel.ShowHoverPreviews)) settingsNotices++; };
            await first.InitializeAsync(fixture);
            Check("the real model restores a disabled preference and notifies existing Settings bindings",
                !first.ShowHoverPreviews && !settings.ShowHoverPreviews && mainNotices > 0 && settingsNotices > 0);

            var stale = new MainViewModel();
            models.Add(stale);
            await stale.InitializeAsync(fixture);
            settings.ShowHoverPreviews = true;
            await first.SaveNowAsync();
            Check("changing the Settings property applies and persists the shared preference",
                first.ShowHoverPreviews && (await store.LoadAsync())?.ShowHoverPreviews == true);
            stale.SidebarWidth = 278;
            await stale.SaveNowAsync();
            Check("an older window changing another setting preserves the latest hover preference",
                await store.LoadAsync() is { ShowHoverPreviews: true, SidebarWidth: 278 });

            // Explicitly return to this window's original value after another
            // window changed the file: ownership records the deliberate edit.
            stale.ShowHoverPreviews = true;
            stale.ShowHoverPreviews = false;
            await stale.SaveNowAsync();
            Check("a deliberate edit back to the original value is still saved over another window's choice",
                (await store.LoadAsync())?.ShowHoverPreviews == false);
            await first.SaveNowAsync();
            Check("an unchanged older window does not undo that explicit edit",
                (await store.LoadAsync())?.ShowHoverPreviews == false);

            var reopened = new MainViewModel();
            models.Add(reopened);
            await reopened.InitializeAsync(fixture);
            Check("the next real window restores the latest disabled value", !reopened.ShowHoverPreviews);

            await store.UpdateAsync(state => { state!.ShowHoverPreviews = true; return state; });
            var beforeEarlyChoice = File.ReadAllBytes(workspacePath);
            var beforeEarlyChoiceTime = File.GetLastWriteTimeUtc(workspacePath);
            var early = new MainViewModel { ShowHoverPreviews = false };
            models.Add(early);
            Check("a preference click before initialization writes no defaults over the workspace",
                File.ReadAllBytes(workspacePath).SequenceEqual(beforeEarlyChoice)
                && File.GetLastWriteTimeUtc(workspacePath) == beforeEarlyChoiceTime);
            await early.InitializeAsync(fixture);
            Check("a deliberate choice made before loading finishes survives workspace restore", !early.ShowHoverPreviews);
            Check("initialization automatically remembers that early choice without a manual save",
                (await store.LoadAsync())?.ShowHoverPreviews == false);

            var picker = new MainViewModel(Path.Combine(fixture, "picker.tree.json"), nestedPicker: true)
            {
                SuppressShellWrites = true
            };
            picker.Tree.SuppressWrites = true;
            models.Add(picker);
            await picker.InitializeAsync(fixture);
            picker.ShowHoverPreviews = true;
            await picker.SaveNowAsync();
            var afterPicker = await store.LoadAsync();
            Check("a picker saves a deliberate hover preference without writing its own canvas settings",
                afterPicker is { ShowHoverPreviews: true, CanvasLayout: "Tree", CanvasRenderer: "Cpu", IsSplit: true, SidebarWidth: 278 }
                && afterPicker.CanvasLayersOff?.SequenceEqual(["Details"]) == true
                && !File.Exists(Path.Combine(fixture, "picker.tree.json")));

            await store.UpdateAsync(state => { state!.ShowHoverPreviews = false; return state; });
            var beforeRefresh = File.ReadAllBytes(workspacePath);
            var beforeRefreshTime = File.GetLastWriteTimeUtc(workspacePath);
            await picker.RefreshPickerNavigationPreferencesAsync();
            Check("a prepared picker refreshes a newer external preference after its own save",
                !picker.ShowHoverPreviews);
            await picker.SaveNowAsync();
            Check("refreshing or closing an unedited picker leaves workspace bytes and timestamp intact",
                File.ReadAllBytes(workspacePath).SequenceEqual(beforeRefresh)
                && File.GetLastWriteTimeUtc(workspacePath) == beforeRefreshTime);
            picker.ShowHoverPreviews = true;
            picker.ShowHoverPreviews = false;
            picker.ShowHoverPreviews = true;
            await picker.SaveNowAsync();
            Check("queued autosaves retain the last hover-preview choice",
                (await store.LoadAsync())?.ShowHoverPreviews == true);
        }
        finally
        {
            foreach (var model in models) model.Dispose();
            GpuBootstrap.UseSavedPreference(rendererBefore);
            if (original is not null)
            {
                await File.WriteAllBytesAsync(workspacePath, original);
                File.SetLastWriteTimeUtc(workspacePath, originalTime!.Value);
            }
            else if (File.Exists(workspacePath)) File.Delete(workspacePath);
            TryDelete(fixture);
        }
        Check("the original isolated workspace bytes and timestamp are restored exactly",
            original is null ? !File.Exists(workspacePath)
                : File.ReadAllBytes(workspacePath).SequenceEqual(original)
                    && File.GetLastWriteTimeUtc(workspacePath) == originalTime);

        await HoverPreviewPreferenceWindowChecksAsync();
    }

    private static async Task HoverPreviewPreferenceWindowChecksAsync()
    {
        Section("hover previews: Settings and the Layers menu agree");
        if (Application.Current is null) new App().InitializeComponent();
        var main = new MainWindow();
        var shell = (MainViewModel)main.DataContext;
        using var settings = new SettingsViewModel(shell, true, null, (_, _, _) => false);
        var window = new SettingsWindow(settings);
        try
        {
            LayOutWindow(window, 720, 780);
            await SettingsSettle();
            var menu = main.BuildLayersMenu(main.CanvasLayersButton);
            var item = menu.Items.OfType<MenuItem>().Single(entry => entry.Header as string == "Hover previews");
            Check("Settings has an accessible, enabled hover-preview switch with a plain explanation",
                window.HoverPreviewsSwitch.IsEnabled && window.HoverPreviewsSwitch.IsChecked == true
                && AutomationProperties.GetName(window.HoverPreviewsSwitch) == "Hover previews"
                && AutomationProperties.GetHelpText(window.HoverPreviewsSwitch).Contains("thumbnail", StringComparison.Ordinal));
            Check("Layers offers an independent accessible checkable switch which stays open",
                item.IsEnabled && item.IsCheckable && item.StaysOpenOnClick && item.IsChecked
                && AutomationProperties.GetName(item) == "Hover previews"
                && (item.ToolTip as string)?.Contains("selected", StringComparison.Ordinal) == true);
            Toggle(window.HoverPreviewsSwitch);
            await SettingsSettle();
            Check("the Settings switch updates both the model and an already-built menu",
                !shell.ShowHoverPreviews && !item.IsChecked);
            item.SetCurrentValue(MenuItem.IsCheckedProperty, true);
            await SettingsSettle();
            Check("the menu's checkbox binding updates the model and Settings switch",
                shell.ShowHoverPreviews && window.HoverPreviewsSwitch.IsChecked == true);
            shell.ShowHoverPreviews = false;
            shell.Layers = CanvasLayer.All;
            await SettingsSettle();
            Check("Show all layers leaves the independent hover-preview choice disabled",
                !shell.ShowHoverPreviews && !item.IsChecked && window.HoverPreviewsSwitch.IsChecked == false);
            shell.Layout = CanvasLayout.Tree;
            var treeMenu = main.BuildLayersMenu(main.StatusLayersButton);
            Check("hover previews remain available on the tree canvas",
                treeMenu.Items.OfType<MenuItem>().Single(entry => entry.Header as string == "Hover previews") is { IsEnabled: true, IsChecked: false });
        }
        finally
        {
            window.Close();
            main.Close();
            shell.Dispose();
        }
    }
}
