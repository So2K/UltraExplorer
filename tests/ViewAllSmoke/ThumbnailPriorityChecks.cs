using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task ThumbnailPriorityChecks()
    {
        Section("thumbnail priorities: automatic content reserves a worker for interactive previews");
        await ThumbnailBackgroundReservationChecks();
        await ThumbnailForegroundQueuePriorityChecks();
        await ThumbnailForegroundPromotionChecks();
        await ThumbnailPriorityQueueBoundChecks();
        await ThumbnailBackgroundDisposeChecks();
    }

    private static async Task ThumbnailBackgroundReservationChecks()
    {
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentQueue<string>();
        var image = ThumbnailPicture();
        using var service = new FileThumbnailService(new FileThumbnailOptions
        {
            MetadataReader = _ => ThumbnailStamp(),
            ThumbnailReader = path =>
            {
                calls.Enqueue(Path.GetFileNameWithoutExtension(path));
                if (path == ThumbnailPath("background-blocked"))
                {
                    started.TrySetResult();
                    release.Wait(TimeSpan.FromSeconds(10)); // Deliberately ignores cancellation.
                }
                return new(image);
            }
        });
        try
        {
            var first = service.GetBackgroundAsync(ThumbnailPath("background-blocked"), cancellation.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var next = service.GetBackgroundAsync(ThumbnailPath("background-next"));
            await Task.Delay(40);
            Check("one automatic extraction leaves the second existing worker free rather than starting another background file",
                service.Diagnostics.WorkerCount == 2 && service.Diagnostics.ActiveWorkers == 1
                && service.Diagnostics.Queued == 1 && calls.Count == 1);
            var foreground = await ThumbnailAwait(service.GetAsync(ThumbnailPath("foreground-hover")));
            Check("a real foreground preview completes on the reserved worker before a blocked automatic extraction returns",
                foreground is not null && !release.IsSet && !calls.Contains("background-next")
                && calls.Contains("foreground-hover"));

            var timer = Stopwatch.StartNew();
            cancellation.Cancel();
            var canceled = false;
            try { await ThumbnailAwait(first); }
            catch (OperationCanceledException) { canceled = true; }
            timer.Stop();
            await Task.Delay(40);
            Check("canceling the background waiter is prompt but does not free the actual occupied native background slot",
                canceled && timer.ElapsedMilliseconds < 250 && service.Diagnostics.ActiveWorkers == 1
                && !calls.Contains("background-next") && !next.IsCompleted);
            var later = await ThumbnailAwait(service.GetAsync(ThumbnailPath("foreground-after-cancel")));
            Check("interactive work still proceeds while an abandoned native background call remains blocked",
                later is not null && !calls.Contains("background-next") && service.Diagnostics.WorkerCount == 2);
            release.Set();
            Check("the next automatic file starts only after the previous native execution actually returns",
                await ThumbnailAwait(next) is not null && calls.Count(name => name == "background-next") == 1);
        }
        finally { release.Set(); }
    }

    private static async Task ThumbnailForegroundQueuePriorityChecks()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentQueue<string>();
        var image = ThumbnailPicture();
        using var service = new FileThumbnailService(new FileThumbnailOptions
        {
            WorkerCount = 1, MetadataReader = _ => ThumbnailStamp(),
            ThumbnailReader = path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                calls.Enqueue(name);
                if (name == "priority-hold") { started.TrySetResult(); release.Wait(TimeSpan.FromSeconds(10)); }
                return new(image);
            }
        });
        try
        {
            var hold = service.GetAsync(ThumbnailPath("priority-hold"));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var older = service.GetAsync(ThumbnailPath("foreground-older"));
            var newest = service.GetAsync(ThumbnailPath("foreground-newest"));
            var automatic = service.GetBackgroundAsync(ThumbnailPath("background-even-newer"));
            release.Set();
            await Task.WhenAll(hold, older, newest, automatic).WaitAsync(TimeSpan.FromSeconds(5));
            Check("foreground files always precede newer automatic work while preserving foreground-only newest-first ordering",
                calls.SequenceEqual(new[] { "priority-hold", "foreground-newest", "foreground-older", "background-even-newer" }));
            Check("a one-worker configuration remains bounded without creating a replacement pool", service.Diagnostics.WorkerCount == 1);
        }
        finally { release.Set(); }
    }

    private static async Task ThumbnailForegroundPromotionChecks()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentQueue<string>();
        var image = ThumbnailPicture();
        using var service = new FileThumbnailService(new FileThumbnailOptions
        {
            WorkerCount = 1, MetadataReader = _ => ThumbnailStamp(),
            ThumbnailReader = path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                calls.Enqueue(name);
                if (name == "promotion-hold") { started.TrySetResult(); release.Wait(TimeSpan.FromSeconds(10)); }
                return new(image);
            }
        });
        try
        {
            var hold = service.GetAsync(ThumbnailPath("promotion-hold"));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var background = service.GetBackgroundAsync(ThumbnailPath("promoted-shared"));
            var foreground = service.GetAsync(ThumbnailPath("promoted-shared"));
            var newBackground = service.GetBackgroundAsync(ThumbnailPath("newer-background"));
            release.Set();
            var results = await Task.WhenAll(hold, background, foreground, newBackground).WaitAsync(TimeSpan.FromSeconds(5));
            Check("a foreground waiter promotes queued automatic work, sharing exactly one extraction and result",
                calls.SequenceEqual(new[] { "promotion-hold", "promoted-shared", "newer-background" })
                && ReferenceEquals(results[1], results[2]) && results[1] is not null);
        }
        finally { release.Set(); }

        using var activeRelease = new ManualResetEventSlim();
        using var backgroundCancellation = new CancellationTokenSource();
        var activeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeCalls = new ConcurrentQueue<string>();
        using var activeService = new FileThumbnailService(new FileThumbnailOptions
        {
            MetadataReader = _ => ThumbnailStamp(),
            ThumbnailReader = path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                activeCalls.Enqueue(name);
                if (name == "active-shared") { activeStarted.TrySetResult(); activeRelease.Wait(TimeSpan.FromSeconds(10)); }
                return new(image);
            }
        });
        try
        {
            var original = activeService.GetBackgroundAsync(ThumbnailPath("active-shared"), backgroundCancellation.Token);
            await activeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var foreground = activeService.GetAsync(ThumbnailPath("active-shared"));
            var queued = activeService.GetBackgroundAsync(ThumbnailPath("background-after-promotion"));
            backgroundCancellation.Cancel();
            try { await ThumbnailAwait(original); } catch (OperationCanceledException) { }
            await Task.Delay(40);
            Check("an active same-path promotion keeps foreground ownership and the original native slot until completion",
                activeCalls.Count == 1 && !foreground.IsCompleted && !queued.IsCompleted
                && activeService.Diagnostics.ActiveWorkers == 1);
            activeRelease.Set();
            Check("canceling only the automatic waiter does not cancel its joined foreground waiter",
                await ThumbnailAwait(foreground) is not null && await ThumbnailAwait(queued) is not null
                && activeCalls.Count(name => name == "active-shared") == 1);
        }
        finally { activeRelease.Set(); }
    }

    private static async Task ThumbnailPriorityQueueBoundChecks()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentQueue<string>();
        var image = ThumbnailPicture();
        using var service = new FileThumbnailService(new FileThumbnailOptions
        {
            WorkerCount = 1, MaximumPending = 2, MetadataReader = _ => ThumbnailStamp(),
            ThumbnailReader = path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                calls.Enqueue(name);
                if (name == "bounded-hold") { started.TrySetResult(); release.Wait(TimeSpan.FromSeconds(10)); }
                return new(image);
            }
        });
        try
        {
            var hold = service.GetAsync(ThumbnailPath("bounded-hold"));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var older = service.GetAsync(ThumbnailPath("protected-older"));
            var newer = service.GetAsync(ThumbnailPath("protected-newer"));
            var rejected = service.GetBackgroundAsync(ThumbnailPath("excess-automatic"));
            Check("automatic queue pressure never evicts foreground requests to admit an automatic file",
                await ThumbnailAwait(rejected) is null && service.Diagnostics.Queued == 2);
            release.Set();
            var results = await Task.WhenAll(hold, older, newer).WaitAsync(TimeSpan.FromSeconds(5));
            Check("foreground-only pending bounds and order remain intact after an automatic request is rejected",
                results.All(result => result is not null) && calls.SequenceEqual(new[] { "bounded-hold", "protected-newer", "protected-older" }));
        }
        finally { release.Set(); }
    }

    private static async Task ThumbnailBackgroundDisposeChecks()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var image = ThumbnailPicture();
        using var service = new FileThumbnailService(new FileThumbnailOptions
        {
            MetadataReader = _ => ThumbnailStamp(),
            ThumbnailReader = _ =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(10));
                return new(image);
            }
        });
        try
        {
            var first = service.GetBackgroundAsync(ThumbnailPath("dispose-background"));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var queued = service.GetBackgroundAsync(ThumbnailPath("dispose-queued"));
            var timer = Stopwatch.StartNew();
            service.Dispose();
            var results = await Task.WhenAll(first, queued).WaitAsync(TimeSpan.FromSeconds(2));
            timer.Stop();
            Check("disposing resolves automatic waiters promptly without waiting for the native provider",
                timer.ElapsedMilliseconds < 250 && results.All(result => result is null)
                && service.Diagnostics.CacheBytes == 0 && service.Diagnostics.Queued == 0);
            Check("neither foreground nor automatic requests recreate source work after disposal",
                await ThumbnailAwait(service.GetAsync(ThumbnailPath("after-dispose-foreground"))) is null
                && await ThumbnailAwait(service.GetBackgroundAsync(ThumbnailPath("after-dispose-background"))) is null
                && calls == 1);
            release.Set();
            Check("the disposed native worker eventually retires without publishing pixels",
                await ThumbnailPriorityUntil(() => service.Diagnostics.ActiveWorkers == 0)
                && service.Diagnostics.CacheEntries == 0 && calls == 1);
        }
        finally { release.Set(); }
    }

    private static async Task<bool> ThumbnailPriorityUntil(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready() && watch.ElapsedMilliseconds < 3000) await Task.Delay(5);
        return ready();
    }
}
