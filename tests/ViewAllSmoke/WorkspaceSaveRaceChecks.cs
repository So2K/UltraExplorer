using System.IO;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The workspace, saved by two stores over one file at the same moment - the
/// window's and a picker's in one process, or two processes': each save goes
/// through a temporary file of its own and is moved onto the workspace in the
/// file's turn, so both land and neither deletes the other's.
/// </summary>
internal static partial class Program
{
    private static async Task WorkspaceSaveRaceChecks()
    {
        Section("workspace: two saves of one file at once both land");
        var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "UltraExplorerWorkspaceRace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // The everyday window and a picker in the same process each have
            // a store of their own over workspace.json; so do two processes.
            var path = Path.Combine(root, "workspace.json");
            var window = new WorkspaceStore(path);
            var picker = new WorkspaceStore(path);
            var failed = new List<string>();
            for (var round = 0; round < 20; round++)
            {
                Task[] saves =
                [
                    window.SaveAsync(new WorkspaceState { SidebarWidth = 200 + round }),
                    picker.SaveAsync(new WorkspaceState { SidebarWidth = 300 + round })
                ];
                foreach (var save in saves)
                {
                    try
                    {
                        await save;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failed.Add(ex.GetType().Name);
                    }
                }
            }

            Check($"two stores saving one workspace at once both succeed ({failed.Count} of 40 failed{(failed.Count > 0 ? ": " + failed[0] : "")})", failed.Count == 0);

            // From threads of their own, as processes would.
            failed.Clear();
            WorkspaceStore[] stores = [window, picker, new WorkspaceStore(path), new WorkspaceStore(path)];
            await Task.WhenAll(stores.Select((store, index) => Task.Run(async () =>
            {
                for (var round = 0; round < 25; round++)
                {
                    try
                    {
                        await store.SaveAsync(new WorkspaceState { SidebarWidth = 200 + index * 50 + round });
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        lock (failed)
                        {
                            failed.Add(ex.GetType().Name);
                        }
                    }
                }
            })));
            Check($"and so do saves from four threads at once ({failed.Count} of 100 failed{(failed.Count > 0 ? ": " + failed[0] : "")})", failed.Count == 0);
            Check("the workspace left is one of them, whole", await window.LoadAsync() is { SidebarWidth: >= 200 and < 400 });
            Check("and no temporary file is left behind", Directory.GetFiles(root, "*.tmp").Length == 0);
        }
        finally
        {
            TryDelete(root);
        }
    }
}
