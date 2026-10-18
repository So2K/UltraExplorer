using System.Diagnostics;
using System.Windows.Media.Imaging;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task ThumbnailCacheLifetimeChecks()
    {
        Section("thumbnail cache: idle expiry preserves valid pixels without a lifetime timer");
        await ThumbnailIdleMixedExpiryChecks();
        await ThumbnailIdleLruExpiryChecks();
        await ThumbnailIdleRealDeadlineChecks();
        await ThumbnailIdleDisposeChecks();
    }

    private static async Task ThumbnailIdleMixedExpiryChecks()
    {
        long clock = 1000;
        var image = ThumbnailPicture();
        using var service = new FileThumbnailService(new FileThumbnailOptions
        {
            WorkerCount = 1, TickCount = () => Interlocked.Read(ref clock),
            PositiveCacheMilliseconds = 100, NegativeCacheMilliseconds = 25,
            MetadataReader = _ => ThumbnailStamp(),
            ThumbnailReader = path => path == ThumbnailPath("idle-missing") ? null : new(image, 800, 400, "retained dimensions")
        });
        var first = await ThumbnailAwait(service.GetAsync(ThumbnailPath("idle-first")));
        await ThumbnailAwait(service.GetAsync(ThumbnailPath("idle-second")));
        await ThumbnailAwait(service.GetAsync(ThumbnailPath("idle-missing")));
        var initial = service.Diagnostics;
        Check("the idle fixture retains two bounded positive entries and one negative entry",
            first is not null && initial.CacheEntries == 3 && initial.CacheBytes == 2L * 32 * 16 * 4);

        Interlocked.Exchange(ref clock, 1024);
        service.WakeIdleWorkersForChecks();
        var warm = await ThumbnailAwait(service.GetAsync(ThumbnailPath("idle-first")));
        Check("before its deadline a warm hit retains identical pixels, original dimensions and extraction count",
            first is not null && ReferenceEquals(first, warm)
            && warm is { OriginalWidth: 800, OriginalHeight: 400, Detail: "retained dimensions" }
            && warm.Image.IsFrozen && ReferenceEquals(warm.Image, first.Image)
            && ThumbnailCacheSamePixels(warm.Image, image) && service.Diagnostics.Extractions == initial.Extractions);

        var beforeExpiry = service.Diagnostics;
        Interlocked.Exchange(ref clock, 1025);
        service.WakeIdleWorkersForChecks();
        Check("at the exact negative deadline the idle worker drops only the expired negative entry",
            await ThumbnailCacheWaitAsync(() => service.Diagnostics.CacheEntries == 2)
            && service.Diagnostics.CacheBytes == beforeExpiry.CacheBytes
            && service.Diagnostics.MetadataReads == beforeExpiry.MetadataReads
            && service.Diagnostics.Extractions == beforeExpiry.Extractions);

        Interlocked.Exchange(ref clock, 1099);
        service.WakeIdleWorkersForChecks();
        var almostExpired = await ThumbnailAwait(service.GetAsync(ThumbnailPath("idle-first")));
        Check("the original fixed positive lifetime remains valid immediately before its deadline",
            ReferenceEquals(first, almostExpired) && service.Diagnostics.Extractions == initial.Extractions);
        var beforeLastExpiry = service.Diagnostics;
        Interlocked.Exchange(ref clock, 1100);
        service.WakeIdleWorkersForChecks();
        Check("at the exact positive deadline all idle expired cache entries and their byte accounting are released",
            await ThumbnailCacheWaitAsync(() => service.Diagnostics.CacheEntries == 0)
            && service.Diagnostics.CacheBytes == 0 && service.Diagnostics.MetadataReads == beforeLastExpiry.MetadataReads
            && service.Diagnostics.Extractions == beforeLastExpiry.Extractions);
        var refreshed = await ThumbnailAwait(service.GetAsync(ThumbnailPath("idle-first")));
        Check("the next hover refreshes an expired thumbnail without reducing image quality",
            first is not null && refreshed is { OriginalWidth: 800, OriginalHeight: 400, Detail: "retained dimensions" }
            && !ReferenceEquals(first, refreshed) && refreshed.Image.IsFrozen
            && ThumbnailCacheSamePixels(first.Image, refreshed.Image)
            && service.Diagnostics.Extractions == initial.Extractions + 1);
    }

    private static async Task ThumbnailIdleLruExpiryChecks()
    {
        long clock = 2000;
        var image = ThumbnailPicture();
        using var service = new FileThumbnailService(new FileThumbnailOptions
        {
            WorkerCount = 1, TickCount = () => Interlocked.Read(ref clock), PositiveCacheMilliseconds = 200,
            MetadataReader = _ => ThumbnailStamp(), ThumbnailReader = _ => new(image)
        });
        var older = await ThumbnailAwait(service.GetAsync(ThumbnailPath("idle-old")));
        Interlocked.Exchange(ref clock, 2100);
        var newer = await ThumbnailAwait(service.GetAsync(ThumbnailPath("idle-new")));
        var promoted = await ThumbnailAwait(service.GetAsync(ThumbnailPath("idle-old")));
        Check("a valid warm hit promotes its old entry without changing its content lifetime", ReferenceEquals(older, promoted));
        Interlocked.Exchange(ref clock, 2200);
        service.WakeIdleWorkersForChecks();
        Check("expiry scans beyond a fresh LRU head to remove an expired promoted entry",
            await ThumbnailCacheWaitAsync(() => service.Diagnostics.CacheEntries == 1)
            && service.Diagnostics.CacheBytes == 32L * 16 * 4);
        var count = service.Diagnostics.Extractions;
        var stillWarm = await ThumbnailAwait(service.GetAsync(ThumbnailPath("idle-new")));
        Check("idle expiry preserves the newer still-valid entry and its warm-hit behavior",
            ReferenceEquals(newer, stillWarm) && service.Diagnostics.Extractions == count);
        var reloaded = await ThumbnailAwait(service.GetAsync(ThumbnailPath("idle-old")));
        Check("only the expired old entry is extracted again after idle eviction",
            reloaded is not null && !ReferenceEquals(older, reloaded) && service.Diagnostics.Extractions == count + 1);
    }

    private static async Task ThumbnailIdleRealDeadlineChecks()
    {
        using var service = new FileThumbnailService(new FileThumbnailOptions
        {
            WorkerCount = 1, PositiveCacheMilliseconds = 80,
            MetadataReader = _ => ThumbnailStamp(), ThumbnailReader = _ => new(ThumbnailPicture())
        });
        var displayed = await ThumbnailAwait(service.GetAsync(ThumbnailPath("real-idle-expiry")));
        var before = service.Diagnostics;
        Check("the existing idle worker wakes at a real deadline without any new request or test pulse",
            await ThumbnailCacheWaitAsync(() => service.Diagnostics.CacheEntries == 0)
            && service.Diagnostics.CacheBytes == 0 && service.Diagnostics.Extractions == before.Extractions
            && service.Diagnostics.MetadataReads == before.MetadataReads);
        Check("releasing the expired cache does not invalidate a thumbnail still displayed by its caller",
            displayed is not null && displayed.Image.IsFrozen && displayed.Image.PixelWidth == 32
            && displayed.Image.PixelHeight == 16 && ThumbnailBlue(displayed.Image) == 40);
    }

    private static async Task ThumbnailIdleDisposeChecks()
    {
        var disposed = 0;
        var readsAfterDispose = 0;
        var image = ThumbnailPicture();
        using var service = new FileThumbnailService(new FileThumbnailOptions
        {
            WorkerCount = 1,
            TickCount = () =>
            {
                if (Volatile.Read(ref disposed) != 0) Interlocked.Increment(ref readsAfterDispose);
                return Environment.TickCount64;
            },
            MetadataReader = _ => ThumbnailStamp(), ThumbnailReader = _ => new(image)
        });
        await ThumbnailAwait(service.GetAsync(ThumbnailPath("dispose-idle-expiry")));
        var clock = Stopwatch.StartNew();
        service.Dispose();
        clock.Stop();
        Volatile.Write(ref disposed, 1);
        service.WakeIdleWorkersForChecks();
        var after = service.Diagnostics;
        await Task.Delay(60);
        Check("disposal releases idle cache state and wakes its worker without waiting for the expiry deadline",
            clock.ElapsedMilliseconds < 250 && after.CacheEntries == 0 && after.CacheBytes == 0
            && after.Queued == 0 && after.ActiveWorkers == 0 && Volatile.Read(ref readsAfterDispose) == 0);
        Check("a disposed expiry worker cannot restore content or schedule another extraction",
            await ThumbnailAwait(service.GetAsync(ThumbnailPath("after-idle-dispose"))) is null
            && service.Diagnostics.Extractions == after.Extractions && service.Diagnostics.CacheEntries == 0);
    }

    private static async Task<bool> ThumbnailCacheWaitAsync(Func<bool> ready)
    {
        var clock = Stopwatch.StartNew();
        while (!ready() && clock.ElapsedMilliseconds < 3000) await Task.Delay(5);
        return ready();
    }

    private static bool ThumbnailCacheSamePixels(BitmapSource first, BitmapSource second)
    {
        if (first.PixelWidth != second.PixelWidth || first.PixelHeight != second.PixelHeight || first.Format != second.Format)
            return false;
        var stride = checked((first.PixelWidth * first.Format.BitsPerPixel + 7) / 8);
        var before = new byte[checked(stride * first.PixelHeight)];
        var after = new byte[before.Length];
        first.CopyPixels(before, stride, 0);
        second.CopyPixels(after, stride, 0);
        return before.AsSpan().SequenceEqual(after);
    }
}
