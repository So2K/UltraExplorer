using System.IO;
using System.Reflection;
using System.Text.Json;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Search;
using UltraExplorer.Services.Watch;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task Pr4WorkspaceFixChecks()
    {
        Section("PR4: an unread workspace cannot be replaced by defaults");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerPr4Workspace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var original = new WorkspaceState
        {
            SidebarWidth = 327,
            Favorites = [new FavoriteState("Keep", @"Q:\Keep", "pin", "#E3B341")],
            CanvasSort = "Modified-desc",
            FolderSorts = [new FolderSortState(@"Q:\Keep", "Name-desc")]
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(original);
        try
        {
            var retryPath = Path.Combine(root, "retry.json");
            await File.WriteAllBytesAsync(retryPath, bytes);
            var retry = new WorkspaceStore(retryPath);
            using (var held = new FileStream(retryPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var loading = retry.LoadAsync();
                Check("a sharing violation leaves the read pending for another attempt", !loading.IsCompleted);
                held.Dispose();
                Check("a retry loads the original pins and folder orders after the lock is released",
                    await loading is { SidebarWidth: 327, Favorites.Count: 1, FolderSorts.Count: 1 } state
                    && state.Favorites[0].Path == @"Q:\Keep" && state.FolderSorts[0].Sort == "Name-desc");
            }

            var lostPath = Path.Combine(root, "unreadable.json");
            await File.WriteAllBytesAsync(lostPath, bytes);
            var lost = new WorkspaceStore(lostPath);
            using (var held = new FileStream(lostPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var loading = lost.LoadAsync();
                var saving = Pr4WriteRefusedAsync(() => lost.SaveAsync(new WorkspaceState { SidebarWidth = 240 }));
                Check("an exhausted locked-file read opens with no loaded workspace", await loading is null);
                Check("a save already queued behind the failed read is refused", await saving);
            }
            Check("a direct save after the file becomes readable still protects the unread original",
                await Pr4WriteRefusedAsync(() => lost.SaveAsync(new WorkspaceState()))
                && (await File.ReadAllBytesAsync(lostPath)).AsSpan().SequenceEqual(bytes));
            var called = false;
            Check("the actual update path refuses to merge defaults over an unread workspace",
                await Pr4WriteRefusedAsync(() => lost.UpdateAsync(_ => { called = true; return new WorkspaceState(); })) && !called);
            Check("a successful explicit read recovers the original workspace", await lost.LoadAsync() is { SidebarWidth: 327, Favorites.Count: 1 });
            await lost.UpdateAsync(current => { current!.SidebarWidth = 328; return current; });
            Check("saving resumes after a successful read and retains the pins and orders",
                await lost.LoadAsync() is { SidebarWidth: 328, Favorites.Count: 1, FolderSorts.Count: 1 });

            var updatePath = Path.Combine(root, "update-only.json");
            await File.WriteAllBytesAsync(updatePath, bytes);
            var updateOnly = new WorkspaceStore(updatePath);
            called = false;
            using (var held = new FileStream(updatePath, FileMode.Open, FileAccess.Read, FileShare.None))
                Check("an update without a previous load refuses an unread original before calling its merge",
                    await Pr4WriteRefusedAsync(() => updateOnly.UpdateAsync(_ => { called = true; return new WorkspaceState(); })) && !called);
            Check("the failed update leaves its original byte for byte and no save temporary",
                (await File.ReadAllBytesAsync(updatePath)).AsSpan().SequenceEqual(bytes)
                && Directory.GetFiles(root, "*.tmp").Length == 0);
            Check("a later read can recover an update-only store", await updateOnly.LoadAsync() is { Favorites.Count: 1 });

            var cancelledPath = Path.Combine(root, "cancelled.json");
            await File.WriteAllBytesAsync(cancelledPath, bytes);
            var cancelled = new WorkspaceStore(cancelledPath);
            using (var held = new FileStream(cancelledPath, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var stop = new CancellationTokenSource())
            {
                var loading = cancelled.LoadAsync(stop.Token);
                stop.Cancel();
                var sawCancellation = false;
                try { await loading; }
                catch (OperationCanceledException) { sawCancellation = true; }
                Check("cancellation interrupts the pending read retry", sawCancellation);
            }
            Check("a cancelled retry still protects the original when the lock is gone",
                await Pr4WriteRefusedAsync(() => cancelled.SaveAsync(new WorkspaceState()))
                && (await File.ReadAllBytesAsync(cancelledPath)).AsSpan().SequenceEqual(bytes));
            Check("a cancelled read releases the store for a successful recovery read", await cancelled.LoadAsync() is { SidebarWidth: 327 });

            var corruptPath = Path.Combine(root, "corrupt.json");
            var corrupt = new byte[] { (byte)'{', (byte)'x' };
            await File.WriteAllBytesAsync(corruptPath, corrupt);
            var damaged = new WorkspaceStore(corruptPath);
            using (var held = new FileStream(corruptPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Check("malformed JSON still loads as none when its backup rename is blocked", await damaged.LoadAsync() is null);
            Check("failed quarantine cannot permit a later direct save to destroy the damaged original",
                await Pr4WriteRefusedAsync(() => damaged.SaveAsync(new WorkspaceState()))
                && (await File.ReadAllBytesAsync(corruptPath)).AsSpan().SequenceEqual(corrupt));
            Check("failed quarantine also protects the production update path",
                await Pr4WriteRefusedAsync(() => damaged.UpdateAsync(_ => new WorkspaceState())));
            Check("a later read can quarantine the damaged file once its rename is allowed", await damaged.LoadAsync() is null && !File.Exists(corruptPath));
            var backups = Directory.GetFiles(root, "corrupt.json.corrupt-*");
            Check("the quarantined original is retained byte for byte", backups.Length == 1 && (await File.ReadAllBytesAsync(backups[0])).AsSpan().SequenceEqual(corrupt));
            await damaged.SaveAsync(original);
            Check("saving a new workspace is allowed after the original was safely backed up", await damaged.LoadAsync() is { Favorites.Count: 1 });

            var deniedPath = Path.Combine(root, "directory.json");
            Directory.CreateDirectory(deniedPath);
            var denied = new WorkspaceStore(deniedPath);
            Check("an inaccessible file target is treated as unknown even when File.Exists says false", await denied.LoadAsync() is null);
            Directory.Delete(deniedPath);
            await File.WriteAllBytesAsync(deniedPath, bytes);
            Check("an earlier access failure cannot overwrite a workspace that appeared afterward",
                await Pr4WriteRefusedAsync(() => denied.UpdateAsync(_ => new WorkspaceState()))
                && (await File.ReadAllBytesAsync(deniedPath)).AsSpan().SequenceEqual(bytes));

            var missingPath = Path.Combine(root, "new", "workspace.json");
            var missing = new WorkspaceStore(missingPath);
            Check("a genuinely absent workspace loads as none", await missing.LoadAsync() is null);
            await missing.UpdateAsync(_ => original);
            Check("a missing workspace can be created through the actual update path", await missing.LoadAsync() is { SidebarWidth: 327, Favorites.Count: 1 });
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<bool> Pr4WriteRefusedAsync(Func<Task> save)
    {
        try { await save(); return false; }
        catch (IOException) { return true; }
    }

    private static async Task Pr4SearchFixChecks()
    {
        Section("PR4: Everything side timeouts retain the primary answer");
        var everywhere = new EverythingPage(1_000, [
            new EverythingItem("needle-everywhere.txt", @"Q:\Elsewhere", false, 1, null, "needle"),
            new EverythingItem("needle-duplicate.txt", @"Q:\Here", false, 1, null, "needle")]);
        var under = new EverythingPage(30, [
            new EverythingItem("needle-local.txt", @"Q:\Here", false, 1, null, "needle"),
            everywhere.Items[1]]);
        var prefix = new EverythingPage(7, [new EverythingItem("needle-prefix.txt", @"Q:\Elsewhere", false, 1, null, "needle")]);
        foreach (var (near, starts, name) in new[]
        {
            ((EverythingPage?)null, (EverythingPage?)prefix, "under-folder"),
            ((EverythingPage?)under, (EverythingPage?)null, "prefix"),
            ((EverythingPage?)null, (EverythingPage?)null, "both side")
        })
        {
            var replies = new Queue<EverythingPage?>([everywhere, near, starts]);
            var snapshots = new List<SearchSnapshot>();
            var asked = new List<(int Limit, uint Sort)>();
            var engine = new SearchEngine(EverythingClient.Shared, VolumeResolver.FromSystem, (_, limit, _, sort) =>
            {
                asked.Add((limit, sort));
                return Task.FromResult(replies.Dequeue());
            });
            var answered = await Pr4AskEverythingAsync(engine, snapshots.Add);
            var snapshot = snapshots.SingleOrDefault();
            Check($"a {name} timeout still produces the indexed primary answer without requesting a drive walk",
                answered && snapshot is { IsFinal: true, Source: SearchSource.Everything, EverythingFailed: false }
                && snapshot.Hits.Any(hit => hit.Name == "needle-everywhere.txt"));
            Check($"a {name} timeout retains the other successful side answer and deduplicates overlapping hits",
                snapshot is not null && snapshot.Hits.Count == 2 + (near is null ? 0 : 1) + (starts is null ? 0 : 1)
                && snapshot.Hits.Count(hit => hit.Name == "needle-duplicate.txt") == 1);
            Check($"a {name} timeout keeps the primary total and newest-first request limits",
                snapshot is not null && snapshot.HereTotal + snapshot.ElsewhereTotal == 1_000
                && asked.SequenceEqual([(SearchEngine.EverywhereLimit, EverythingClient.SortNewestFirst),
                    (SearchEngine.HereLimit, EverythingClient.SortNewestFirst), (SearchEngine.PrefixLimit, EverythingClient.SortNameAscending)]));
        }

        var primaryCalls = 0;
        var absent = new SearchEngine(EverythingClient.Shared, VolumeResolver.FromSystem, (_, _, _, _) =>
        {
            primaryCalls++;
            return Task.FromResult<EverythingPage?>(null);
        });
        var absentSnapshots = new List<SearchSnapshot>();
        Check("a missing primary answer still requests the caller's walk fallback without asking side questions",
            !await Pr4AskEverythingAsync(absent, absentSnapshots.Add) && primaryCalls == 1 && absentSnapshots.Count == 0);

        var cancelledCalls = 0;
        var cancelled = new SearchEngine(EverythingClient.Shared, VolumeResolver.FromSystem, (_, _, _, _) =>
        {
            cancelledCalls++;
            return cancelledCalls == 1 ? Task.FromResult<EverythingPage?>(everywhere)
                : Task.FromException<EverythingPage?>(new OperationCanceledException());
        });
        var cancelledSnapshots = new List<SearchSnapshot>();
        Check("cancelling a side query does not publish a stale primary answer or request a drive walk",
            await Pr4AskEverythingAsync(cancelled, cancelledSnapshots.Add) && cancelledCalls == 2 && cancelledSnapshots.Count == 0);

        var completeCalls = 0;
        var complete = new SearchEngine(EverythingClient.Shared, VolumeResolver.FromSystem, (_, _, _, _) =>
        {
            completeCalls++;
            return Task.FromResult<EverythingPage?>(everywhere with { Total = everywhere.Items.Count });
        });
        var completeSnapshots = new List<SearchSnapshot>();
        Check("a complete primary answer still skips the two unnecessary side queries",
            await Pr4AskEverythingAsync(complete, completeSnapshots.Add) && completeCalls == 1 && completeSnapshots.Count == 1);
    }

    private static Task<bool> Pr4AskEverythingAsync(SearchEngine engine, Action<SearchSnapshot> publish) =>
        (Task<bool>)typeof(SearchEngine).GetMethod("AskEverythingAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(engine, [SearchQuery.Parse("needle"), @"Q:\Here", publish, CancellationToken.None])!;
}
