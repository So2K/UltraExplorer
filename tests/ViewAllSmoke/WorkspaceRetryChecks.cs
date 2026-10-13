using System.IO;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task WorkspaceRetryChecks()
    {
        Section("workspace: recover from a transient unreadable file without restarting");
        var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "UltraExplorerWorkspaceRetry", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "workspace.json");
            var original = new WorkspaceState
            {
                SidebarWidth = 286, CanvasSort = "Modified-desc", CanvasLayersOff = ["Details"],
                Favorites = [new FavoriteState("Saved project", @"C:\Work\saved", "\uE8B7", "#E3B341")]
            };
            var writer = new WorkspaceStore(path);
            await writer.SaveAsync(original);
            var retrying = new WorkspaceStore(path);
            var bytes = File.ReadAllBytes(path);
            var timestamp = File.GetLastWriteTimeUtc(path);
            var merges = 0;
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Check("a store cannot load a profile held exclusively by another process", await retrying.LoadAsync() is null);
                Exception? failure = null;
                try
                {
                    await retrying.UpdateAsync(current => { merges++; return new WorkspaceState(); });
                }
                catch (IOException ex) { failure = ex; }
                Check("a still-unreadable profile blocks the merge rather than writing defaults", failure is not null && merges == 0);
            }
            Check("the denied update preserves saved bytes and timestamp",
                File.ReadAllBytes(path).SequenceEqual(bytes) && File.GetLastWriteTimeUtc(path) == timestamp);

            Exception? recoveredFailure = null;
            WorkspaceState? observed = null;
            try
            {
                await retrying.UpdateAsync(current =>
                {
                    observed = current;
                    current!.SidebarWidth = 278;
                    return current;
                });
            }
            catch (IOException ex) { recoveredFailure = ex; }
            Check("the same store retries its read and update after the temporary lock is released",
                recoveredFailure is null && observed is { SidebarWidth: 278 });
            var recovered = await new WorkspaceStore(path).LoadAsync();
            Check("the retry preserves the existing pins, sort and layer choice",
                recovered is { SidebarWidth: 278, CanvasSort: "Modified-desc" }
                && recovered.Favorites.SequenceEqual(original.Favorites)
                && recovered.CanvasLayersOff?.SequenceEqual(["Details"]) == true);

            // Cancellation marks the file as unknown too. It must protect the
            // cancelled operation without permanently disabling later saves.
            merges = 0;
            bytes = File.ReadAllBytes(path);
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(75)))
            {
                Exception? failure = null;
                try { await retrying.UpdateAsync(current => { merges++; return current; }, cancellation.Token); }
                catch (OperationCanceledException ex) { failure = ex; }
                Check("cancelling a read retry invokes no merge", failure is not null && merges == 0);
            }
            Check("the cancelled operation preserves the saved profile", File.ReadAllBytes(path).SequenceEqual(bytes));

            await writer.UpdateAsync(current =>
            {
                current!.ShowHoverPreviews = false;
                current.Favorites.Add(new FavoriteState("Newer pin", @"C:\Work\newer", "\uE8B7", "#E3B341"));
                return current;
            });
            observed = null;
            recoveredFailure = null;
            try
            {
                await retrying.UpdateAsync(current =>
                {
                    observed = current;
                    current!.SidebarWidth = 300;
                    return current;
                });
            }
            catch (IOException ex) { recoveredFailure = ex; }
            Check("retrying after cancellation reads another window's newer settings and pins",
                recoveredFailure is null && observed is { ShowHoverPreviews: false, SidebarWidth: 300 }
                && observed.Favorites.Count == 2 && observed.Favorites.Any(favorite => favorite.Name == "Newer pin"));
            Check("the recovered update writes the requested change beside those newer choices",
                await new WorkspaceStore(path).LoadAsync() is { ShowHoverPreviews: false, SidebarWidth: 300, Favorites.Count: 2 });

            await WorkspaceQuarantineRetryChecks(root);
            Check("no retry leaves temporary files behind", Directory.GetFiles(root, "*.tmp").Length == 0);
        }
        finally { TryDelete(root); }
    }

    private static async Task WorkspaceQuarantineRetryChecks(string root)
    {
        var path = Path.Combine(root, "damaged.workspace.json");
        const string damaged = "{ saved-but-damaged-workspace";
        File.WriteAllText(path, damaged);
        var retrying = new WorkspaceStore(path);
        var merges = 0;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            await retrying.LoadAsync();
            Exception? failure = null;
            try { await retrying.UpdateAsync(current => { merges++; return new WorkspaceState(); }); }
            catch (IOException ex) { failure = ex; }
            Check("a damaged file that cannot be backed up still prevents merging", failure is not null && merges == 0);
        }
        Check("the failed quarantine keeps the damaged original", File.ReadAllText(path) == damaged);
        Exception? recoveredFailure = null;
        try
        {
            await retrying.UpdateAsync(current =>
            {
                merges++;
                return current ?? new WorkspaceState { SidebarWidth = 277 };
            });
        }
        catch (IOException ex) { recoveredFailure = ex; }
        var backups = Directory.GetFiles(root, "damaged.workspace.json.corrupt-*");
        Check("a later retry backs up the now-accessible damaged file before creating a replacement",
            recoveredFailure is null && merges == 1 && backups.Length == 1
            && File.ReadAllText(backups[0]) == damaged
            && await new WorkspaceStore(path).LoadAsync() is { SidebarWidth: 277 });
    }
}
