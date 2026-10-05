using System.IO;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task FolderRouteCacheChecks()
    {
        Section("folder broker receipt lifetime and retry deduplication");
        long now = 0;
        var cache = new FolderRouteRequestCache(capacity: 2, retentionMilliseconds: 120000, now: () => now);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var next = Guid.NewGuid();
        var pending = new TaskCompletionSource<FolderRouteReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        FolderRouteReceipt Result(Guid id) => new(id, true, true, 1, 1, 1, Guid.Empty, "fixture", []);
        Check("an offer without commit does not start or retain work", cache.CanOffer(first, "first") && cache.Count == 0 && calls == 0);
        Check("the first committed offer begins its own work", cache.TryCommit(first, "first", () => { calls++; return pending.Task; }, out var active) && calls == 1);
        Check("same request retries share unfinished work", cache.TryCommit(first, "first", () => { calls++; return Task.FromResult(Result(first)); }, out var retry)
            && ReferenceEquals(active, retry) && calls == 1);
        Check("a different payload cannot reuse an offered identity", !cache.CanOffer(first, "changed")
            && !cache.TryCommit(first, "changed", () => Task.FromResult(Result(first)), out _));
        Check("a second committed request fills the bounded cache", cache.TryCommit(second, "second", () => Task.FromResult(Result(second)), out var done));
        await done;
        Check("capacity pressure preserves active work and recent receipts", !cache.CanOffer(next, "next") && cache.Count == 2);
        now = 119999;
        Check("receipts remain available beyond the 18 second retry budget", cache.TryCommit(second, "second", () => throw new InvalidOperationException("must deduplicate"), out var same)
            && ReferenceEquals(done, same) && !cache.CanOffer(next, "next"));
        now = 120000;
        Check("expired completed receipts release capacity", cache.CanOffer(next, "next") && cache.Count == 1);
        Check("unfinished work never expires just because its lookup is old", cache.TryCommit(first, "first", () => throw new InvalidOperationException("must deduplicate"), out var stillActive)
            && ReferenceEquals(active, stillActive));
        Check("a new request can commit after the broker has reached capacity", cache.TryCommit(next, "next", () => Task.FromResult(Result(next)), out var resumed));
        await resumed;
        pending.SetResult(Result(first));
        await active;
        now += 119999;
        Check("retention starts at work completion", !cache.CanOffer(Guid.NewGuid(), "later"));
        now++;
        Check("new folder requests resume without restarting the window", cache.CanOffer(Guid.NewGuid(), "later") && cache.Count == 0);

        var churn = new FolderRouteRequestCache(capacity: 4096, retentionMilliseconds: 120000, now: () => now);
        for (var index = 0; index < 4096; index++)
        {
            var id = Guid.NewGuid();
            if (!churn.TryCommit(id, "same payload", () => Task.FromResult(Result(id)), out var task)) throw new InvalidOperationException("fixture capacity unexpectedly exhausted");
            await task;
        }
        Check("the former lifetime ceiling is reproduced with 4096 completed requests", churn.Count == 4096 && !churn.CanOffer(Guid.NewGuid(), "next"));
        now += 120000;
        Check("4097th lifetime request is accepted after receipt retention", churn.TryCommit(next, "next", () => Task.FromResult(Result(next)), out var beyond));
        await beyond;
        Check("expired receipts do not accumulate over long sessions", churn.Count == 1);

        var concurrent = new FolderRouteRequestCache();
        var common = Guid.NewGuid();
        var executions = 0;
        var results = new Task<FolderRouteReceipt>[64];
        Parallel.For(0, results.Length, index =>
        {
            if (!concurrent.TryCommit(common, "one request", () => { Interlocked.Increment(ref executions); return Task.FromResult(Result(common)); }, out results[index]))
                throw new InvalidOperationException("matching retry was refused");
        });
        await Task.WhenAll(results);
        Check("concurrent committed retries execute only once", executions == 1 && results.All(task => ReferenceEquals(task, results[0])));
        var failed = Guid.NewGuid();
        Check("failed work also retains its result for retries", concurrent.TryCommit(failed, "failure", () => throw new IOException("fixture failure"), out var fault));
        try { await fault; } catch (IOException) { }
        Check("faulted request retries cannot repeat a partially executed action", concurrent.TryCommit(failed, "failure", () => Task.FromResult(Result(failed)), out var faultRetry)
            && ReferenceEquals(fault, faultRetry));
    }
}
