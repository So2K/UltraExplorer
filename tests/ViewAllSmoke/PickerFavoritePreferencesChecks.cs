using System.IO;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task PickerFavoritePreferencesChecks()
    {
        RunOnSta("prepared picker navigation preferences", PickerFavoritePreferencesOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task PickerFavoritePreferencesOnStaAsync()
    {
        Section("prepared picker: fresh favorite preferences and safe saves");
        var stateOverride = Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable);
        var everydayState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer");
        var isolated = !string.IsNullOrWhiteSpace(stateOverride)
            && SameNavigationPath(stateOverride!, AppPaths.StateDirectory)
            && !SameNavigationPath(AppPaths.StateDirectory, everydayState)
            && Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1";
        Check("favorite preference checks require isolated state and test-window mode", isolated);
        if (!isolated) return;

        var workspacePath = AppPaths.State("workspace.json");
        var original = File.Exists(workspacePath) ? File.ReadAllBytes(workspacePath) : null;
        var originalTime = original is null ? (DateTime?)null : File.GetLastWriteTimeUtc(workspacePath);
        var rendererBefore = GpuBootstrap.Preference;
        var fixture = Path.Combine(AppPaths.StateDirectory, "favorite-preferences-" + Guid.NewGuid().ToString("N"));
        var oldFavorite = Path.Combine(fixture, "old-pin");
        var newFavorite = Path.Combine(fixture, "new-pin");
        var latestFavorite = Path.Combine(fixture, "latest-pin");
        foreach (var path in new[] { oldFavorite, newFavorite, latestFavorite }) Directory.CreateDirectory(path);
        var store = new WorkspaceStore();
        MainViewModel? warm = null;
        MainViewModel? stale = null;
        MainViewModel? edited = null;
        MainViewModel? normal = null;
        try
        {
            await store.SaveAsync(FavoritePreferenceState(false, oldFavorite));
            warm = new MainViewModel(Path.Combine(fixture, "warm.tree.json"), nestedPicker: true) { SuppressShellWrites = true };
            await warm.InitializeAsync(fixture);
            Check("a prepared picker initially keeps the preferences present at warm-up",
                !warm.ShowFavoriteLinks && HasOnlyFavorite(warm, oldFavorite));

            await store.SaveAsync(FavoritePreferenceState(true, newFavorite));
            var beforeRefresh = File.ReadAllBytes(workspacePath);
            var timeBeforeRefresh = File.GetLastWriteTimeUtc(workspacePath);
            await warm.RefreshPickerNavigationPreferencesAsync();
            Check("binding a prepared picker refreshes the latest round-link flag and custom pin",
                warm.ShowFavoriteLinks && HasOnlyFavorite(warm, newFavorite));
            Check("refresh reads preferences without rewriting the workspace",
                File.ReadAllBytes(workspacePath).SequenceEqual(beforeRefresh)
                && File.GetLastWriteTimeUtc(workspacePath) == timeBeforeRefresh);

            await store.SaveAsync(FavoritePreferenceState(false, oldFavorite));
            stale = new MainViewModel(Path.Combine(fixture, "stale.tree.json"), nestedPicker: true) { SuppressShellWrites = true };
            await stale.InitializeAsync(fixture);
            await store.SaveAsync(FavoritePreferenceState(true, newFavorite));
            var beforeStaleSave = File.ReadAllBytes(workspacePath);
            var timeBeforeStaleSave = File.GetLastWriteTimeUtc(workspacePath);
            // The same production save used when a picker closes, before its
            // stale collections have had a chance to refresh at another bind.
            await stale.SaveNowAsync();
            var afterStaleSave = await store.LoadAsync();
            Check("an unedited stale picker save preserves a newer external flag and favorites",
                afterStaleSave is { ShowFavoriteLinks: true }
                && afterStaleSave.Favorites.Select(item => item.Path).SequenceEqual([newFavorite], StringComparer.OrdinalIgnoreCase));
            Check("an unedited native picker does not rewrite any workspace bytes or timestamp",
                File.ReadAllBytes(workspacePath).SequenceEqual(beforeStaleSave)
                && File.GetLastWriteTimeUtc(workspacePath) == timeBeforeStaleSave);

            edited = new MainViewModel(Path.Combine(fixture, "edited.tree.json"), nestedPicker: true) { SuppressShellWrites = true };
            await edited.InitializeAsync(fixture);
            edited.ShowFavoriteLinks = false;
            await edited.SaveNowAsync();
            Check("a deliberate picker flag edit is persisted",
                await store.LoadAsync() is { ShowFavoriteLinks: false });
            edited.PinPath(oldFavorite);
            await edited.SaveNowAsync();
            var afterPin = await store.LoadAsync();
            Check("a deliberate picker pin keeps the existing favorite and saves the new one",
                afterPin is not null && afterPin.Favorites.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals([newFavorite, oldFavorite]));
            edited.UnpinPath(newFavorite);
            await edited.SaveNowAsync();
            var afterUnpin = await store.LoadAsync();
            Check("a deliberate picker unpin is persisted",
                afterUnpin is not null && afterUnpin.Favorites.Select(item => item.Path).SequenceEqual([oldFavorite], StringComparer.OrdinalIgnoreCase));
            Check("native preference edits preserve layout, renderer, sidebar, split and layer settings",
                afterUnpin is { CanvasLayout: "Tree", CanvasRenderer: "Cpu", SidebarWidth: 286,
                    IsSplit: true, IsMinimapVisible: true, NestedLeftDrag: "pan", SplitRatio: 0.63 }
                && afterUnpin.CanvasLayersOff?.SequenceEqual(["Details"]) == true);
            Check("native preference edits do not write their private tree state",
                !File.Exists(Path.Combine(fixture, "edited.tree.json")));
            edited.ShowFavoriteLinks = true;
            edited.ShowFavoriteLinks = false;
            edited.ShowFavoriteLinks = true;
            await edited.SaveNowAsync();
            Check("rapid preference changes and queued autosaves persist the final checkbox value",
                await store.LoadAsync() is { ShowFavoriteLinks: true });

            await store.SaveAsync(FavoritePreferenceState(false, latestFavorite));
            await edited.RefreshPickerNavigationPreferencesAsync();
            Check("successful saves release edit ownership so a later bind can take newer preferences",
                !edited.ShowFavoriteLinks && HasOnlyFavorite(edited, latestFavorite));
            Check("standard Windows quick-access links survive custom favorite replacement",
                edited.QuickAccess.Any(item => !item.IsCustom && item.Name == "Downloads")
                    == new FileSystemService(edited.Icons).GetQuickAccess().Any(item => item.Name == "Downloads"));

            await store.SaveAsync(FavoritePreferenceState(false, oldFavorite));
            normal = new MainViewModel();
            await normal.InitializeAsync(fixture);
            await store.SaveAsync(FavoritePreferenceState(true, newFavorite));
            await normal.SaveNowAsync();
            var afterNormalSave = await store.LoadAsync();
            Check("an older unedited normal window preserves a newer native-picker flag and custom pin when saving",
                afterNormalSave is { ShowFavoriteLinks: true }
                && afterNormalSave.Favorites.Select(item => item.Path).SequenceEqual([newFavorite], StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            warm?.Dispose();
            stale?.Dispose();
            edited?.Dispose();
            normal?.Dispose();
            // Both native and normal model initialization apply the saved
            // renderer choice; the preference fixture must leave it as found.
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
    }

    private static WorkspaceState FavoritePreferenceState(bool showLinks, string favorite)
        => new()
        {
            ShowFavoriteLinks = showLinks,
            Favorites = [new FavoriteState(Path.GetFileName(favorite), favorite, "\uE8B7", "#E3B341")],
            CanvasLayout = "Tree", CanvasRenderer = "Cpu", SidebarWidth = 286,
            IsSplit = true, IsMinimapVisible = true, NestedLeftDrag = "pan", SplitRatio = 0.63,
            CanvasLayersOff = ["Details"]
        };

    private static bool HasOnlyFavorite(MainViewModel viewModel, string path)
        => viewModel.QuickAccess.Where(item => item.IsCustom).Select(item => item.Path)
            .SequenceEqual([path], StringComparer.OrdinalIgnoreCase);
}
