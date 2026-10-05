using System.Collections.Specialized;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task ViewModelLifetimeChecks()
    {
        RunOnSta("main model lifetime", MainModelLifetimeChecksAsync);
        return Task.CompletedTask;
    }

    private static async Task MainModelLifetimeChecksAsync()
    {
        Section("main model lifetime: initialization cannot outlive its window");
        var owned = Path.Combine(Path.GetTempPath(), "UltraExplorerModelLifetime", Guid.NewGuid().ToString("N"));
        var rendererBefore = GpuBootstrap.Preference;
        Directory.CreateDirectory(owned);
        try
        {
            foreach (var disposeDuringLoad in new[] { false, true })
            {
                var folder = Path.Combine(owned, disposeDuringLoad ? "closing" : "closed");
                Directory.CreateDirectory(folder);
                var workspace = Path.Combine(folder, "workspace.json");
                var treeState = Path.Combine(folder, "view-all.json");
                var marks = workspace + ".marks.json";
                await new WorkspaceStore(workspace).SaveAsync(new WorkspaceState
                {
                    CanvasRenderer = "Cpu",
                    Favorites = [new FavoriteState("Owned lifetime fixture", folder, "", "#60CDFF")]
                });
                // DeserializeAsync must cross real IO boundaries. This is one
                // owned valid note rather than thousands of objects or an
                // artificial implementation hook.
                File.WriteAllText(marks, JsonSerializer.Serialize(new Dictionary<string, FolderMark>
                {
                    [folder] = new FolderMark("", new string('x', disposeDuringLoad ? 8 * 1024 * 1024 : 1))
                }));
                var workspaceBytes = SHA256.HashData(File.ReadAllBytes(workspace));
                var model = new MainViewModel(treeState, false, workspace);
                var collectionChanges = 0;
                NotifyCollectionChangedEventHandler changed = (_, _) => collectionChanges++;
                model.HomeItems.CollectionChanged += changed;
                model.QuickAccess.CollectionChanged += changed;
                model.Drives.CollectionChanged += changed;
                model.NetworkLocations.CollectionChanged += changed;
                var propertyChanges = 0;
                model.PropertyChanged += (_, _) => propertyChanges++;
                try
                {
                    Task loading;
                    if (disposeDuringLoad)
                    {
                        loading = model.InitializeAsync(folder);
                        Check("the owned large marks file leaves initialization pending at disposal", !loading.IsCompleted);
                        model.Dispose();
                    }
                    else
                    {
                        model.Dispose();
                        loading = model.InitializeAsync(folder);
                    }
                    var propertiesAtClose = propertyChanges;
                    var collectionsAtClose = collectionChanges;
                    var countsAtClose = (model.HomeItems.Count, model.QuickAccess.Count, model.Drives.Count, model.NetworkLocations.Count);
                    Exception? error = null;
                    try { await loading.WaitAsync(TimeSpan.FromSeconds(20)); }
                    catch (Exception ex) { error = ex; }
                    Check($"initialization {(disposeDuringLoad ? "already reading at close" : "called after disposal")} ends without an exception ({error?.GetType().Name ?? "none"})", error is null);
                    if (error is not null) Console.WriteLine($"        {error}");
                    Console.WriteLine($"        disposed model: Home={model.HomeItems.Count}, QuickAccess={model.QuickAccess.Count}, Drives={model.Drives.Count}, Network={model.NetworkLocations.Count}, Roots={model.Tree.Roots.Count}, CollectionChanges={collectionChanges}, LateProperties={propertyChanges - propertiesAtClose}");
                    Check("closed model receives no late sidebar entries or collection mutations",
                        collectionChanges == collectionsAtClose
                        && countsAtClose == (model.HomeItems.Count, model.QuickAccess.Count, model.Drives.Count, model.NetworkLocations.Count)
                        && (disposeDuringLoad || countsAtClose == (0, 0, 0, 0)));
                    Check("closed model receives no late graph roots or property notifications",
                        model.Tree.Roots.Count == 0 && propertyChanges == propertiesAtClose);
                    Check("early closure preserves the owned existing workspace bytes",
                        SHA256.HashData(File.ReadAllBytes(workspace)).SequenceEqual(workspaceBytes));

                    var propertiesBeforeOrder = propertyChanges;
                    model.Orders.SetDefault(new ItemSort(SortColumn.Modified, true));
                    Check("a released window no longer handles its former tree's sort notifications",
                        propertyChanges == propertiesBeforeOrder);
                }
                finally
                {
                    model.Dispose();
                }
            }

            var readyFolder = Path.Combine(owned, "ready");
            Directory.CreateDirectory(readyFolder);
            var readyWorkspace = Path.Combine(readyFolder, "workspace.json");
            await new WorkspaceStore(readyWorkspace).SaveAsync(new WorkspaceState { CanvasRenderer = "Cpu", SidebarWidth = 287 });
            File.WriteAllText(readyWorkspace + ".marks.json", "{}");
            using var readyModel = new MainViewModel(Path.Combine(readyFolder, "view-all.json"), false, readyWorkspace);
            await readyModel.InitializeAsync(readyFolder).WaitAsync(TimeSpan.FromSeconds(20));

            // The pane's drives come in once they have answered, not before the window is ready.
            await LiveWait(() => readyModel.Drives.Count > 0, 10_000);
            Check("the completed-lifetime fixture really initialized before closure", readyModel.HomeItems.Count > 0 && readyModel.Drives.Count > 0);
            var readyBytes = SHA256.HashData(File.ReadAllBytes(readyWorkspace));
            readyModel.Dispose();
            var formerNotifications = 0;
            readyModel.PropertyChanged += (_, _) => formerNotifications++;
            readyModel.Orders.SetDefault(new ItemSort(SortColumn.Size, true));
            await readyModel.SaveNowAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check("an initialized released window no longer processes former sort notifications", formerNotifications == 0);
            Check("saving after disposal preserves the existing initialized workspace bytes",
                SHA256.HashData(File.ReadAllBytes(readyWorkspace)).SequenceEqual(readyBytes));
        }
        finally
        {
            // Restore fixture state without starting a device warm-up that
            // no visible canvas requested (as in the Settings fixture).
            GpuBootstrap.UseSavedPreference(rendererBefore);
            TryDelete(owned);
        }
    }
}
