using System.IO;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task TreeWatchStabilityChecks()
    {
        RunOnSta("failed directory watch recovery", TreeFailedWatchRecoveryChecks);
        Section("stability: overlapping canvas saves");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerTreeStability", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var systemFolder = Path.Combine(root, "system-only");
            Directory.CreateDirectory(systemFolder);
            File.SetAttributes(systemFolder, FileAttributes.System | FileAttributes.Directory);
            var fs = new ViewAllFileSystemService();
            var describedFolder = await fs.DescribeDirectoryAsync(systemFolder);
            var describedEntry = await fs.DescribeEntryAsync(systemFolder);
            Check("a system directory has the same hidden classification through both description routes",
                describedFolder.IsHidden && describedFolder.IsHidden == describedEntry.IsHidden);
            File.SetAttributes(systemFolder, FileAttributes.Directory);

            var store = new ViewAllWorkspaceStore(Path.Combine(root, "canvas.json"));
            // Large restored canvases take several asynchronous writes. A newer
            // close/save request must not be replaced by that older snapshot.
            var old = new ViewAllWorkspaceState
            {
                ActivePath = @"Q:\old",
                Nodes = Enumerable.Range(0, 30_000)
                    .Select(index => new ViewAllNodeState(@"Q:\" + new string('n', 240) + index, index, index, true, true))
                    .ToList()
            };
            var recent = new ViewAllWorkspaceState { ActivePath = @"Q:\latest", ViewportZoom = 4.5 };
            var savingOld = store.SaveAsync(old);
            var savingRecent = store.SaveAsync(recent);
            await Task.WhenAll(savingOld, savingRecent).WaitAsync(TimeSpan.FromSeconds(20));
            var written = await store.LoadAsync();
            Check("a close save issued during a large older save keeps the latest selection and camera",
                written is { ActivePath: @"Q:\latest", ViewportZoom: 4.5 } && written.Nodes.Count == 0);
            Check("overlapping canvas saves leave no temporary files", Directory.GetFiles(root, "*.tmp").Length == 0);

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var canceledSave = false;
            try { await store.SaveAsync(old, canceled.Token); }
            catch (OperationCanceledException) { canceledSave = true; }
            Check("a canceled save is canceled and leaves the previous canvas intact", canceledSave
                && (await store.LoadAsync())?.ActivePath == @"Q:\latest");
            await store.SaveAsync(new() { ActivePath = @"Q:\after-cancel" });
            Check("a canceled save does not block later saves", (await store.LoadAsync())?.ActivePath == @"Q:\after-cancel");
            Check("a canceled save leaves no temporary files", Directory.GetFiles(root, "*.tmp").Length == 0);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TreeFailedWatchRecoveryChecks()
    {
        Section("stability: a failed directory recovers after a real disk change");
        foreach (var retryable in new[] { false, true })
        {
            var fail = false;
            var fresh = false;
            var reads = 0;
            using var tree = new NestedTree((_, _) =>
            {
                Interlocked.Increment(ref reads);
                return fail
                    ? NestedListing.Failed("Fixture failure") with { IsRetryable = retryable }
                    : new([], 1, 0, false) { Files = [new(fresh ? "fresh.txt" : "old.txt", false, 1)] };
            });
            tree.SetRoots([new(@"Q:\", "Fixture", NestedFolderKind.Drive)]);
            var folder = tree.Root.Children.Single();
            await tree.LoadAsync(folder);
            tree.Request(folder, 800);
            fail = true;
            await tree.RefreshAsync(folder);
            var failedReads = Volatile.Read(ref reads);
            tree.Request(folder, 800);
            await Task.Delay(30);
            Check($"a failed directory does not hammer the disk without changes (retryable={retryable})",
                folder.LoadState == NestedLoadState.Failed && Volatile.Read(ref reads) == failedReads);
            fail = false;
            fresh = true;
            tree.OnFolderChanged(folder, new(folder.FullPath, ChangeKinds.Structural, 0, default, default));
            for (var attempt = 0; attempt < 100 && !folder.IsLoaded; attempt++) await Task.Delay(10);
            Check($"a real structural event retries a recently failed visible directory (retryable={retryable})",
                folder.IsLoaded && folder.Files is [var file] && file.Name == "fresh.txt"
                && Volatile.Read(ref reads) == failedReads + 1);

            fail = true;
            await tree.RefreshAsync(folder);
            var offscreenReads = Volatile.Read(ref reads);
            for (var frame = 0; frame <= NestedTree.ExpireAfterFrames; frame++) tree.BeginFrame();
            fail = false;
            tree.OnFolderChanged(folder, new(folder.FullPath, ChangeKinds.Structural, 0, default, default));
            await Task.Delay(30);
            Check($"a failed off-screen directory stays lazy after a change (retryable={retryable})",
                Volatile.Read(ref reads) == offscreenReads && folder.LoadState == NestedLoadState.Failed);
            tree.Request(folder, 800);
            for (var attempt = 0; attempt < 100 && !folder.IsLoaded; attempt++) await Task.Delay(10);
            Check($"returning to the changed failed directory recovers without a manual click (retryable={retryable})",
                folder.IsLoaded && Volatile.Read(ref reads) == offscreenReads + 1);
        }
    }
}
