using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task InlineThumbnailControllerChecks()
    {
        Section("inline thumbnail viewport: bounded demand, motion settling, literal identity and lifetime");
        await InlineDemandAndHotPathChecks();
        await InlineStaleCompletionChecks();
        await InlineBudgetAndExpiryChecks();
        await InlineSlowNegativeWaveChecks();
        await InlineLiteralAndNotificationChecks();
        await InlinePresentationSizeChecks();
        await InlineNonblockingCancellationChecks();
        Check("inline eligibility includes images, documents, videos and models without executable/icon extraction",
            new[] { "png", "EXR", "pdf", "txt", "mp4", "glb", "docx" }.All(InlineThumbnailController.CanPreview)
            && new[] { "exe", "dll", "msi", "lnk", "unknown-extension" }.All(ext => !InlineThumbnailController.CanPreview(ext)));
    }

    private static async Task InlineDemandAndHotPathChecks()
    {
        var clock = new InlineThumbnailClock();
        var calls = 0;
        var active = 0;
        var peak = 0;
        var image = ThumbnailPicture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var controller = new InlineThumbnailController(async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            var count = Interlocked.Increment(ref active);
            InterlockedExtensionsMax(ref peak, count);
            await release.Task;
            Interlocked.Decrement(ref active);
            return new(image);
        }, _ => { }, () => { }, InlineOptions(clock));
        var candidates = Enumerable.Range(0, 10_000).Select(index => new InlineThumbnailCandidate($"C:\\inline-fixture\\{index}.png", 1)).ToArray();
        controller.UpdateVisible(candidates, moving: true);
        clock.Advance(1000);
        await Task.Delay(10);
        Check("10k listing candidates are capped at24 visible demands and do not decode during motion",
            controller.Diagnostics.DemandCount == 24 && calls == 0 && controller.Diagnostics.ActiveRequests == 0);
        controller.UpdateVisible(candidates, moving: false);
        clock.Advance(119);
        await Task.Delay(10);
        Check("new thumbnails wait for the full120ms settle interval", calls == 0);
        clock.Advance(1);
        Check("only two real provider requests can occupy slots at once",
            await InlineWaitUntil(() => Volatile.Read(ref calls) == 2) && controller.Diagnostics.ActiveRequests == 2 && peak == 2);
        for (var index = 0; index < 1000; index++)
        {
            controller.UpdateVisible(candidates, moving: false);
            controller.TryGet(candidates[0].Path, 1, out _);
        }
        Check("repeated render lookups and unchanged viewport updates never start extra IO", calls == 2);
        release.TrySetResult();
        Check("settled demand completes all24 bounded candidates without exceeding two active requests",
            await InlinePumpUntil(clock, () => controller.Diagnostics.CacheEntries == 24 && controller.Diagnostics.ActiveRequests == 0)
            && calls == 24 && peak == 2);
        var before = controller.Diagnostics;
        for (var index = 0; index < 2000; index++)
            controller.TryGet(candidates[0].Path, 1, out _);
        Check("a warm lookup reuses identical frozen pixels and does no provider work",
            controller.TryGet(candidates[0].Path, 1, out var found) && ReferenceEquals(found, image)
            && found!.IsFrozen && controller.Diagnostics.RequestsStarted == before.RequestsStarted);
        controller.UpdateVisible([], moving: true);
        Check("a zoomed-out empty viewport drops all demand immediately", controller.Diagnostics.DemandCount == 0);
    }

    private static async Task InlineStaleCompletionChecks()
    {
        var clock = new InlineThumbnailClock();
        var requests = new ConcurrentQueue<InlineThumbnailRequest>();
        using var controller = new InlineThumbnailController((path, token) =>
        {
            var request = new InlineThumbnailRequest(path, token);
            requests.Enqueue(request);
            return request.Completion.Task; // Deliberately ignores cancellation until the test releases it.
        }, _ => { }, () => { }, InlineOptions(clock, settle: 0));
        controller.UpdateVisible([new("C:\\inline\\a.png", 1), new("C:\\inline\\b.png", 1)], false);
        clock.Advance(1);
        Check("the stale-completion fixture starts two token-ignoring requests", await InlineWaitUntil(() => requests.Count == 2));
        var old = requests.ToArray();
        controller.UpdateVisible([new("C:\\inline\\a.png", 2)], false);
        clock.Advance(100);
        await Task.Delay(10);
        Check("offscreen and replaced-version requests are canceled without inventing replacement slots",
            old.Length == 2 && old.All(request => request.Token.IsCancellationRequested)
            && requests.Count == 2 && controller.Diagnostics.ActiveRequests == 2);
        foreach (var request in old) request.Completion.TrySetResult(new(ThumbnailPicture(blue: 31)));
        Check("late old-generation pixels cannot enter the current cache",
            await InlinePumpUntil(clock, () => requests.Count == 3)
            && !controller.TryGet("C:\\inline\\a.png", 1, out _) && !controller.TryGet("C:\\inline\\a.png", 2, out _)
            && controller.Diagnostics.CacheEntries == 0);
        var latest = requests.ToArray().Last();
        var fresh = ThumbnailPicture(blue: 101);
        latest.Completion.TrySetResult(new(fresh));
        Check("only the fresh listing version becomes visible",
            await InlineWaitUntil(() => controller.TryGet(latest.Path, 2, out _))
            && controller.TryGet(latest.Path, 2, out var shown) && ReferenceEquals(shown, fresh));

        controller.UpdateVisible([new("C:\\inline\\still-blocked.png", 1)], false);
        Check("the disposal fixture starts its remaining request", await InlinePumpUntil(clock, () => requests.Count == 4));
        var blocked = requests.ToArray().Last();
        var watch = Stopwatch.StartNew();
        controller.Dispose();
        watch.Stop();
        Check("permanent close synchronously releases cached references without waiting for a blocked provider",
            watch.ElapsedMilliseconds < 250 && controller.Diagnostics.CacheEntries == 0 && controller.Diagnostics.CacheBytes == 0
            && controller.Diagnostics.DemandCount == 0 && blocked.Token.IsCancellationRequested);
        blocked.Completion.TrySetResult(new(fresh));
        Check("a completion after permanent close cannot restore cache or demand",
            await InlineWaitUntil(() => controller.Diagnostics.ActiveRequests == 0)
            && controller.Diagnostics.CacheEntries == 0 && controller.Diagnostics.DemandCount == 0);
    }

    private static async Task InlineBudgetAndExpiryChecks()
    {
        var clock = new InlineThumbnailClock();
        var calls = 0;
        var image = ThumbnailPicture();
        using var controller = new InlineThumbnailController((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<ThumbnailResult?>(new(image));
        }, _ => { }, () => { }, new InlineThumbnailOptions
        {
            TickCount = () => clock.Now, Delay = clock.Delay, SettleMilliseconds = 0,
            MaximumCacheBytes = 4096, MaximumCacheEntries = 2, PositiveCacheMilliseconds = 1000
        });
        InlineThumbnailCandidate[] ordered = [new("center0.png", 1), new("center1.png", 1), new("edge0.png", 1), new("edge1.png", 1)];
        controller.UpdateVisible(ordered, false);
        Check("the byte-limited fixture attempts each visible candidate once", await InlinePumpUntil(clock, () => calls == 4 && controller.Diagnostics.ActiveRequests == 0));
        Check("byte and entry budgets preserve center-priority pixels when the whole viewport cannot fit",
            controller.Diagnostics.CacheBytes == 4096 && controller.Diagnostics.CacheEntries == 2
            && controller.TryGet(ordered[0].Path, 1, out _) && controller.TryGet(ordered[1].Path, 1, out _)
            && !controller.TryGet(ordered[2].Path, 1, out _) && !controller.TryGet(ordered[3].Path, 1, out _));
        for (var index = 0; index < 1000; index++) controller.UpdateVisible(ordered, false);
        clock.Advance(20);
        await Task.Delay(10);
        Check("a small cache never creates decode/eviction churn for unchanged visible files", calls == 4);
        controller.Suspend();
        Check("hidden retained panes cancel demand while keeping only valid bounded warm references",
            controller.Diagnostics.DemandCount == 0 && controller.Diagnostics.CacheBytes == 4096
            && controller.TryGet(ordered[0].Path, 1, out _));
        clock.Advance(1001);
        Check("one lazy idle deadline drops expired images without another viewport update",
            await InlineWaitUntil(() => controller.Diagnostics.CacheEntries == 0) && controller.Diagnostics.CacheBytes == 0 && calls == 4);

        var negativeClock = new InlineThumbnailClock();
        var negatives = 0;
        using var negative = new InlineThumbnailController((_, _) =>
        {
            Interlocked.Increment(ref negatives);
            return Task.FromResult<ThumbnailResult?>(null);
        }, _ => { }, () => { }, new InlineThumbnailOptions
        {
            TickCount = () => negativeClock.Now, Delay = negativeClock.Delay,
            SettleMilliseconds = 0, NegativeCacheMilliseconds = 100
        });
        negative.UpdateVisible([new("unsupported.png", 1)], false);
        Check("unsupported content produces a bounded negative cache entry",
            await InlinePumpUntil(negativeClock, () => negative.Diagnostics.CacheEntries == 1) && negatives == 1);
        for (var index = 0; index < 1000; index++) negative.UpdateVisible([new("unsupported.png", 1)], false);
        negativeClock.Advance(50);
        await Task.Delay(10);
        Check("unsupported/null results never retry every render frame", negatives == 1);
        negativeClock.Advance(100);
        await Task.Delay(10);
        negative.UpdateVisible([new("unsupported.png", 1)], false);
        Check("a stable visible negative identity remains attempted once beyond its TTL without a periodic timer",
            negatives == 1 && negative.Diagnostics.CacheEntries == 1 && negativeClock.PendingDelays == 0);
        negative.UpdateVisible([], false);
        negativeClock.Advance(1);
        Check("leaving releases an expired negative entry without starting another request",
            await InlineWaitUntil(() => negative.Diagnostics.CacheEntries == 0) && negatives == 1);
        negative.UpdateVisible([new("unsupported.png", 1)], false);
        Check("genuinely returning after an expired negative entry permits one fresh bounded attempt",
            await InlinePumpUntil(negativeClock, () => negatives == 2 && negative.Diagnostics.ActiveRequests == 0));
    }

    private static async Task InlineSlowNegativeWaveChecks()
    {
        var clock = new InlineThumbnailClock();
        var requests = new ConcurrentQueue<InlineThumbnailRequest>();
        using var controller = new InlineThumbnailController((path, token) =>
        {
            var request = new InlineThumbnailRequest(path, token);
            requests.Enqueue(request);
            return request.Completion.Task;
        }, _ => { }, () => { }, InlineOptions(clock, settle: 0));
        var candidates = Enumerable.Range(0, 24).Select(index => new InlineThumbnailCandidate($"slow-negative{index}.png", 1)).ToArray();
        controller.UpdateVisible(candidates, false);
        var allWavesStarted = true;
        for (var wave = 0; wave < 12; wave++)
        {
            if (!await InlinePumpUntil(clock, () => requests.Count >= (wave + 1) * 2))
            {
                allWavesStarted = false;
                break;
            }
            // Each two-request provider wave lasts2.1s. The whole viewport
            // takes>25s, well beyond the5s negative TTL of early centers.
            clock.Advance(2100);
            var pair = requests.ToArray().Skip(wave * 2).Take(2);
            foreach (var request in pair) request.Completion.TrySetResult(null);
        }
        Check("slow negative extraction visits all24 first attempts once without repeatedly prioritizing expired center entries",
            allWavesStarted && await InlineWaitUntil(() => controller.Diagnostics.ActiveRequests == 0)
            && requests.Count == 24 && requests.Select(request => request.Path).Distinct(StringComparer.Ordinal).Count() == 24
            && candidates.All(candidate => requests.Any(request => request.Path == candidate.Path))
            && controller.Diagnostics.CacheEntries == 24 && controller.Diagnostics.CacheBytes == 0);
        clock.Advance(60_000);
        for (var index = 0; index < 100; index++) controller.UpdateVisible(candidates, false);
        await Task.Delay(10);
        Check("a settled unsupported viewport consumes no retry timer or decoding after the complete first wave",
            requests.Count == 24 && controller.Diagnostics.RequestsStarted == 24 && clock.PendingDelays == 0);
        controller.Suspend();
        clock.Advance(1);
        Check("suspending the long-lived negative viewport releases expired state without any new viewport update",
            await InlineWaitUntil(() => controller.Diagnostics.CacheEntries == 0) && requests.Count == 24);
    }

    private static async Task InlineLiteralAndNotificationChecks()
    {
        var clock = new InlineThumbnailClock();
        var posts = new ConcurrentQueue<Action>();
        var redraws = 0;
        var calls = new ConcurrentQueue<string>();
        var lower = ThumbnailPicture(blue: 40);
        var upper = ThumbnailPicture(blue: 140);
        var literal = "C:\\case-sensitive\\A.png ";
        using var controller = new InlineThumbnailController((path, _) =>
        {
            calls.Enqueue(path);
            return Task.FromResult<ThumbnailResult?>(new(path == literal ? upper : lower));
        }, posts.Enqueue, () => Interlocked.Increment(ref redraws), InlineOptions(clock, settle: 0));
        controller.UpdateVisible([new("C:\\case-sensitive\\a.png", 1), new(literal, 1)], false);
        clock.Advance(1);
        Check("literal case twins/trailing spaces retain distinct path and frozen image identities",
            await InlineWaitUntil(() => controller.Diagnostics.CacheEntries == 2)
            && calls.Contains(literal) && controller.TryGet(literal, 1, out var upperFound) && ReferenceEquals(upperFound, upper)
            && controller.TryGet("C:\\case-sensitive\\a.png", 1, out var lowerFound) && ReferenceEquals(lowerFound, lower));
        Check("two completions share one dispatcher notification before the next render turn",
            posts.Count == 1 && controller.Diagnostics.NotificationsPosted == 1 && redraws == 0);
        controller.Suspend();
        while (posts.TryDequeue(out var stalePost)) stalePost();
        Check("a queued redraw cannot revive or invalidate a suspended viewport", redraws == 0);
        controller.UpdateVisible([new(literal, 1)], moving: true);
        Check("already-cached content remains instantly visible during panning/zooming",
            controller.TryGet(literal, 1, out var resumed) && ReferenceEquals(resumed, upper) && calls.Count == 2);
        controller.Suspend(clearCache: true);
        Check("an explicit cache-clearing suspension releases pixels immediately", controller.Diagnostics.CacheEntries == 0 && controller.Diagnostics.CacheBytes == 0);

        var invalidClock = new InlineThumbnailClock();
        var unfrozenPixels = new byte[16 * 16 * 4];
        var unfrozen = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, unfrozenPixels, 16 * 4);
        // IsFrozen on this dispatcher-owned Freezable is itself affine. The
        // async test may resume on a different pool thread, so capture the
        // fixture's initial state here and only inspect controller state later.
        var initiallyUnfrozen = !unfrozen.IsFrozen;
        using var invalid = new InlineThumbnailController((_, _) => Task.FromResult<ThumbnailResult?>(new(unfrozen)),
            _ => { }, () => { }, InlineOptions(invalidClock, settle: 0));
        invalid.UpdateVisible([new("provider-owned.png", 1)], false);
        Check("a provider image created unfrozen is safely rejected without publishing pixels across dispatchers",
            await InlinePumpUntil(invalidClock, () => invalid.Diagnostics.CacheEntries == 1)
            && initiallyUnfrozen && invalid.Diagnostics.CacheBytes == 0 && !invalid.TryGet("provider-owned.png", 1, out _));
    }

    private static async Task InlinePresentationSizeChecks()
    {
        var clock = new InlineThumbnailClock();
        var original = ThumbnailPicture(512, 256, blue: 91);
        using var controller = new InlineThumbnailController((_, _) => Task.FromResult<ThumbnailResult?>(new(original)),
            _ => { }, () => { }, InlineOptions(clock, settle: 0));
        controller.UpdateVisible([new("wide-source.png", 1)], false);
        Check("large hover pixels are downsampled once into a frozen detached256px presentation image",
            await InlinePumpUntil(clock, () => controller.TryGet("wide-source.png", 1, out _))
            && controller.TryGet("wide-source.png", 1, out var shown)
            && shown is { IsFrozen: true, PixelWidth: 256, PixelHeight: 128 }
            && shown is not TransformedBitmap && shown is not FormatConvertedBitmap
            && shown.Format == PixelFormats.Bgra32 && ThumbnailBlue(shown) == 91
            && !ReferenceEquals(shown, original) && original.PixelWidth == 512 && original.PixelHeight == 256
            && controller.Diagnostics.CacheBytes == 256L * 128 * 4);

        var unchangedClock = new InlineThumbnailClock();
        using var unchanged = new InlineThumbnailController((_, _) => Task.FromResult<ThumbnailResult?>(new(original)),
            _ => { }, () => { }, new InlineThumbnailOptions
            {
                TickCount = () => unchangedClock.Now, Delay = unchangedClock.Delay, SettleMilliseconds = 0,
                MaximumDimension = 512
            });
        unchanged.UpdateVisible([new("full-source.png", 1)], false);
        Check("the configurable512px path retains an already-fitting frozen source by reference",
            await InlinePumpUntil(unchangedClock, () => unchanged.TryGet("full-source.png", 1, out _))
            && unchanged.TryGet("full-source.png", 1, out var full) && ReferenceEquals(full, original)
            && unchanged.Diagnostics.CacheBytes == 512L * 256 * 4);

        var allClock = new InlineThumbnailClock();
        var square = ThumbnailPicture(512, 512, blue: 120);
        using var all = new InlineThumbnailController((_, _) => Task.FromResult<ThumbnailResult?>(new(square)),
            _ => { }, () => { }, InlineOptions(allClock, settle: 0));
        var candidates = Enumerable.Range(0, 24).Select(index => new InlineThumbnailCandidate($"square{index}.png", 1)).ToArray();
        all.UpdateVisible(candidates, false);
        Check("all24 worst-case square presentation thumbnails fit together within6MiB instead of evicting half the viewport",
            await InlinePumpUntil(allClock, () => all.Diagnostics.CacheEntries == 24 && all.Diagnostics.ActiveRequests == 0)
            && candidates.All(candidate => all.TryGet(candidate.Path, candidate.Version, out _))
            && all.Diagnostics.CacheBytes == 24L * 256 * 256 * 4 && all.Diagnostics.CacheBytes <= 6L * 1024 * 1024);
        allClock.Advance(60_000);
        for (var index = 0; index < 100; index++) all.UpdateVisible(candidates, false);
        Check("unchanged visible pixels stay pinned without flickering or periodic30s refresh work",
            candidates.All(candidate => all.TryGet(candidate.Path, candidate.Version, out _))
            && all.Diagnostics.RequestsStarted == 24 && all.Diagnostics.CacheEntries == 24 && allClock.PendingDelays == 0);
        all.Suspend();
        allClock.Advance(1);
        Check("once hidden the already-expired pinned images are released by one lazy cleanup wake",
            await InlineWaitUntil(() => all.Diagnostics.CacheEntries == 0) && all.Diagnostics.CacheBytes == 0);
    }

    private static async Task InlineNonblockingCancellationChecks()
    {
        var clock = new InlineThumbnailClock();
        using var releaseCallback = new ManualResetEventSlim();
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new TaskCompletionSource<ThumbnailResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var controller = new InlineThumbnailController((_, token) =>
        {
            token.Register(() =>
            {
                callbackStarted.TrySetResult();
                releaseCallback.Wait(TimeSpan.FromSeconds(5));
            });
            started.TrySetResult();
            return request.Task;
        }, _ => { }, () => { }, InlineOptions(clock, settle: 0));
        controller.UpdateVisible([new("blocked-handler.png", 1)], false);
        clock.Advance(1);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            var timer = Stopwatch.StartNew();
            controller.Suspend();
            timer.Stop();
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check("canceling a slow native provider never executes its blocking cancellation callback on the input thread",
                timer.ElapsedMilliseconds < 250 && controller.Diagnostics.ActiveRequests == 1 && controller.Diagnostics.DemandCount == 0);
            request.TrySetResult(new(ThumbnailPicture()));
            Check("an abandoned callback cannot reinsert pixels while cancellation is still finishing",
                await InlineWaitUntil(() => controller.Diagnostics.ActiveRequests == 0) && controller.Diagnostics.CacheEntries == 0);
        }
        finally { releaseCallback.Set(); request.TrySetResult(null); }
    }

    private static InlineThumbnailOptions InlineOptions(InlineThumbnailClock clock, int settle = 120) => new()
    {
        TickCount = () => clock.Now, Delay = clock.Delay, SettleMilliseconds = settle
    };

    private static async Task<bool> InlineWaitUntil(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready() && watch.ElapsedMilliseconds < 3000) await Task.Delay(5);
        return ready();
    }

    private static async Task<bool> InlinePumpUntil(InlineThumbnailClock clock, Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready() && watch.ElapsedMilliseconds < 3000) { clock.Advance(1); await Task.Delay(5); }
        return ready();
    }

    private static void InterlockedExtensionsMax(ref int target, int value)
    {
        int observed;
        do { observed = Volatile.Read(ref target); if (observed >= value) return; }
        while (Interlocked.CompareExchange(ref target, value, observed) != observed);
    }

    private sealed record InlineThumbnailRequest(string Path, CancellationToken Token)
    {
        public TaskCompletionSource<ThumbnailResult?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class InlineThumbnailClock
    {
        private readonly object _gate = new();
        private readonly List<(long Due, TaskCompletionSource Completion)> _delays = [];
        private long _now;
        public long Now => Interlocked.Read(ref _now);
        public int PendingDelays
        {
            get { lock (_gate) return _delays.Count(delay => !delay.Completion.Task.IsCompleted); }
        }

        public Task Delay(int milliseconds, CancellationToken token)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _delays.Add((Now + milliseconds, completion));
            var registration = token.Register(() => completion.TrySetCanceled(token));
            _ = completion.Task.ContinueWith(_ => registration.Unregister(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return completion.Task;
        }

        public void Advance(int milliseconds)
        {
            List<TaskCompletionSource> ready = [];
            lock (_gate)
            {
                Interlocked.Add(ref _now, milliseconds);
                for (var index = _delays.Count - 1; index >= 0; index--)
                {
                    var delay = _delays[index];
                    if (delay.Due > Now && !delay.Completion.Task.IsCompleted) continue;
                    _delays.RemoveAt(index);
                    ready.Add(delay.Completion);
                }
            }
            foreach (var completion in ready) completion.TrySetResult();
        }
    }
}
