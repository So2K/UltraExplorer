using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task ThumbnailServiceChecks()
    {
        Section("hover thumbnail service");
        await ThumbnailCacheChecks();
        await ThumbnailNegativeChecks();
        await ThumbnailQueueChecks();
        await ThumbnailDedupChecks();
        await ThumbnailCaseIdentityChecks();
        await ThumbnailAbandonedRejoinChecks();
        await ThumbnailBlockedDisposeChecks();
        await ThumbnailContentChecks();
        await ThumbnailDetachmentChecks();
        ThumbnailOrientationChecks();
        await ThumbnailRealFileChecks();
        await ThumbnailLiteralPathChecks();
        await ThumbnailSameStampOverwriteChecks();
    }

    private static BitmapSource ThumbnailPicture(int width = 32, int height = 16, byte blue = 40)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = blue;
            pixels[index + 1] = 80;
            pixels[index + 2] = 180;
            pixels[index + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static readonly string ThumbnailFakeRoot = Path.Combine(Path.GetTempPath(), "UltraExplorerThumbnailFake");
    private static string ThumbnailPath(string name) => Path.Combine(ThumbnailFakeRoot, name + ".png");
    private static Task<ThumbnailResult?> ThumbnailAwait(Task<ThumbnailResult?> task) => task.WaitAsync(TimeSpan.FromSeconds(5));
    private static ThumbnailFileStamp ThumbnailStamp(long version = 1, uint attributes = 0x80) => new(10, version, attributes);

    private static async Task ThumbnailCacheChecks()
    {
        long version = 1;
        var source = ThumbnailPicture();
        using var service = new FileThumbnailService(new FileThumbnailOptions
        {
            WorkerCount = 1,
            MaximumCacheBytes = 4096,
            MetadataReader = _ => ThumbnailStamp(Interlocked.Read(ref version)),
            ThumbnailReader = _ => new(source)
        });
        var first = await ThumbnailAwait(service.GetAsync(ThumbnailPath("a")));
        var reads = service.Diagnostics.MetadataReads;
        var second = await ThumbnailAwait(service.GetAsync(ThumbnailPath("a")));
        Check("cached thumbnails revalidate metadata on a worker and reuse frozen pixels",
            first is not null && first.Image.IsFrozen && ReferenceEquals(first, second)
            && service.Diagnostics.Extractions == 1 && service.Diagnostics.MetadataReads > reads);

        Interlocked.Increment(ref version);
        var changed = await ThumbnailAwait(service.GetAsync(ThumbnailPath("a")));
        Check("a replaced or edited file invalidates its previous thumbnail",
            changed is not null && !ReferenceEquals(first, changed) && service.Diagnostics.Extractions == 2);

        await ThumbnailAwait(service.GetAsync(ThumbnailPath("b")));
        await ThumbnailAwait(service.GetAsync(ThumbnailPath("a"))); // promote a over b
        await ThumbnailAwait(service.GetAsync(ThumbnailPath("c")));
        var extractions = service.Diagnostics.Extractions;
        await ThumbnailAwait(service.GetAsync(ThumbnailPath("a")));
        Check("the thumbnail LRU respects its byte budget and preserves the recently used entry",
            service.Diagnostics.CacheBytes <= 4096 && service.Diagnostics.CacheEntries == 2
            && service.Diagnostics.Extractions == extractions);
        await ThumbnailAwait(service.GetAsync(ThumbnailPath("b")));
        Check("an evicted thumbnail is extracted again", service.Diagnostics.Extractions == extractions + 1);

        using var countBound = new FileThumbnailService(new FileThumbnailOptions
        {
            WorkerCount = 1, MaximumCacheEntries = 3,
            MetadataReader = _ => ThumbnailStamp(), ThumbnailReader = _ => null
        });
        for (var index = 0; index < 12; index++)
            await ThumbnailAwait(countBound.GetAsync(ThumbnailPath($"missing-{index}")));
        Check("negative results share the bounded LRU instead of growing without limit", countBound.Diagnostics.CacheEntries == 3);
    }

    private static async Task ThumbnailNegativeChecks()
    {
        long clock = 10, version = 1;
        var available = false;
        var image = ThumbnailPicture();
        using var service = new FileThumbnailService(new FileThumbnailOptions
        {
            WorkerCount = 1, TickCount = () => Interlocked.Read(ref clock), NegativeCacheMilliseconds = 100,
            MetadataReader = _ => ThumbnailStamp(Interlocked.Read(ref version)),
            ThumbnailReader = _ => available ? new(image) : null
        });
        await ThumbnailAwait(service.GetAsync(ThumbnailPath("unsupported")));
        await ThumbnailAwait(service.GetAsync(ThumbnailPath("unsupported")));
        Check("unsupported content is negatively cached to avoid repeated decoding", service.Diagnostics.Extractions == 1);
        Interlocked.Add(ref clock, 101);
        await ThumbnailAwait(service.GetAsync(ThumbnailPath("unsupported")));
        Check("a negative cache entry expires and permits another extraction", service.Diagnostics.Extractions == 2);
        available = true;
        Interlocked.Increment(ref version);
        var recovered = await ThumbnailAwait(service.GetAsync(ThumbnailPath("unsupported")));
        Check("a formerly unsupported file can show content immediately after it changes", recovered is not null);

        using var throwing = new FileThumbnailService(new FileThumbnailOptions
        {
            MetadataReader = _ => throw new IOException("offline drive"),
            ThumbnailReader = _ => throw new InvalidOperationException("must not decode")
        });
        Check("metadata failures produce an absent thumbnail without escaping to the UI",
            await ThumbnailAwait(throwing.GetAsync(ThumbnailPath("offline"))) is null && throwing.Diagnostics.Extractions == 0);
    }

    private static async Task ThumbnailQueueChecks()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentQueue<string>();
        var image = ThumbnailPicture();
        using var service = new FileThumbnailService(new FileThumbnailOptions
        {
            WorkerCount = 1, MaximumPending = 2,
            MetadataReader = _ => ThumbnailStamp(),
            ThumbnailReader = path =>
            {
                calls.Enqueue(Path.GetFileNameWithoutExtension(path));
                if (path == ThumbnailPath("hold"))
                {
                    started.TrySetResult();
                    release.Wait(TimeSpan.FromSeconds(10));
                }
                return new(image);
            }
        });
        try
        {
            var hold = service.GetAsync(ThumbnailPath("hold"));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var evicted = service.GetAsync(ThumbnailPath("evicted"));
            var older = service.GetAsync(ThumbnailPath("older"));
            var newest = service.GetAsync(ThumbnailPath("newest"));
            Check("pending hover work is bounded and displaces the oldest unstarted request",
                service.Diagnostics.Queued == 2 && await ThumbnailAwait(evicted) is null);
            using var cancellation = new CancellationTokenSource();
            var canceled = service.GetAsync(ThumbnailPath("canceled"), cancellation.Token);
            // This displaces older, which must also complete without extraction.
            cancellation.Cancel();
            var observedCancel = false;
            try { await ThumbnailAwait(canceled); }
            catch (OperationCanceledException) { observedCancel = true; }
            Check("canceling queued hover work removes it before reading the file",
                observedCancel && service.Diagnostics.Queued == 1);
            var last = service.GetAsync(ThumbnailPath("last"));
            release.Set();
            await ThumbnailAwait(hold);
            await ThumbnailAwait(last);
            await ThumbnailAwait(newest);
            Check("a free worker serves the newest hover before older queued work",
                calls.ToArray().SequenceEqual(new[] { "hold", "last", "newest" }) && await ThumbnailAwait(older) is null);
        }
        finally { release.Set(); }
    }

    private static async Task ThumbnailDedupChecks()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var image = ThumbnailPicture();
        var sta = false;
        using var service = new FileThumbnailService(new FileThumbnailOptions
        {
            WorkerCount = 1, MetadataReader = _ => ThumbnailStamp(),
            ThumbnailReader = _ =>
            {
                sta = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;
                started.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(10));
                return new(image);
            }
        });
        try
        {
            var first = service.GetAsync(ThumbnailPath("same"));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var cancellation = new CancellationTokenSource();
            var canceled = service.GetAsync(ThumbnailPath("same"), cancellation.Token);
            var shared = Enumerable.Range(0, 6).Select(_ => service.GetAsync(ThumbnailPath("same"))).ToArray();
            cancellation.Cancel();
            var observedCancel = false;
            try { await ThumbnailAwait(canceled); }
            catch (OperationCanceledException) { observedCancel = true; }
            release.Set();
            var result = await ThumbnailAwait(first);
            var results = await Task.WhenAll(shared).WaitAsync(TimeSpan.FromSeconds(5));
            Check("same-file hovers share one extraction on an STA worker",
                sta && result is not null && results.All(value => ReferenceEquals(value, result)) && service.Diagnostics.Extractions == 1);
            Check("canceling one same-file waiter leaves the other waiters intact", observedCancel && results.All(value => value is not null));
        }
        finally { release.Set(); }
    }

    private static async Task ThumbnailCaseIdentityChecks()
    {
        var upper = ThumbnailPath("Build");
        var lower = ThumbnailPath("build");
        var upperImage = ThumbnailPicture(blue: 40);
        var lowerImage = ThumbnailPicture(blue: 210);
        var calls = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var service = new FileThumbnailService(new FileThumbnailOptions
        {
            WorkerCount = 1, MetadataReader = _ => ThumbnailStamp(),
            ThumbnailReader = path =>
            {
                calls.AddOrUpdate(path, 1, (_, count) => count + 1);
                if (path == upper)
                {
                    started.TrySetResult();
                    release.Wait(TimeSpan.FromSeconds(10));
                }
                return new(path == upper ? upperImage : lowerImage);
            }
        });
        try
        {
            var first = service.GetAsync(upper);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var same = service.GetAsync(upper);
            var twin = service.GetAsync(lower);
            Check("case twins with identical metadata queue independent work while exact-name waiters share it",
                service.Diagnostics.Queued == 1 && service.Diagnostics.Extractions == 1);
            release.Set();
            var upperResult = await ThumbnailAwait(first);
            var sameResult = await ThumbnailAwait(same);
            var lowerResult = await ThumbnailAwait(twin);
            Check("case-twin pending requests receive their own pixels and exactly one provider call per literal name",
                upperResult is not null && lowerResult is not null && ReferenceEquals(upperResult, sameResult)
                && ThumbnailBlue(upperResult.Image) == 40 && ThumbnailBlue(lowerResult.Image) == 210
                && calls.Count == 2 && calls[upper] == 1 && calls[lower] == 1);
            var upperCached = await ThumbnailAwait(service.GetAsync(upper));
            var lowerCached = await ThumbnailAwait(service.GetAsync(lower));
            Check("case-twin cache entries remain distinct despite equal size, timestamp and attributes",
                upperResult is not null && lowerResult is not null
                && ReferenceEquals(upperResult, upperCached) && ReferenceEquals(lowerResult, lowerCached)
                && service.Diagnostics.CacheEntries == 2 && service.Diagnostics.Extractions == 2);
        }
        finally { release.Set(); }
    }

    private static async Task ThumbnailBlockedDisposeChecks()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = 0;
        using var service = new FileThumbnailService(new FileThumbnailOptions
        {
            MetadataReader = _ =>
            {
                if (Interlocked.Increment(ref blocked) == 2) started.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(10));
                return ThumbnailStamp();
            },
            ThumbnailReader = _ => new(ThumbnailPicture())
        });
        var tasks = new List<Task<ThumbnailResult?>>();
        try
        {
            var watch = Stopwatch.StartNew();
            tasks.Add(service.GetAsync(ThumbnailPath("stuck1")));
            tasks.Add(service.GetAsync(ThumbnailPath("stuck2")));
            watch.Stop();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check("requesting thumbnails never performs slow metadata reads on the calling thread", watch.ElapsedMilliseconds < 250);
            for (var index = 0; index < 200; index++) tasks.Add(service.GetAsync(ThumbnailPath($"queued-{index}")));
            Check("two stuck reads cannot spawn more workers or unbounded pending work",
                service.Diagnostics.WorkerCount == 2 && service.Diagnostics.ActiveWorkers == 2 && service.Diagnostics.Queued <= 32);
            watch.Restart();
            service.Dispose();
            watch.Stop();
            var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2));
            Check("disposing resolves every waiter promptly without waiting for stuck native or disk work",
                watch.ElapsedMilliseconds < 250 && results.All(value => value is null)
                && service.Diagnostics.CacheBytes == 0 && service.Diagnostics.Queued == 0);
            Check("requests after disposal complete with no content", await ThumbnailAwait(service.GetAsync(ThumbnailPath("after"))) is null);
        }
        finally { release.Set(); }
    }

    private static async Task ThumbnailAbandonedRejoinChecks()
    {
        var oldImage = ThumbnailPicture(blue: 20);
        var newImage = ThumbnailPicture(blue: 210);
        var calls = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var barriers = new ConcurrentDictionary<string, (TaskCompletionSource Started, ManualResetEventSlim Release)>(StringComparer.OrdinalIgnoreCase);
        using var service = new FileThumbnailService(new FileThumbnailOptions
        {
            WorkerCount = 1, MetadataReader = _ => ThumbnailStamp(),
            ThumbnailReader = path =>
            {
                var attempt = calls.AddOrUpdate(path, 1, (_, count) => count + 1);
                if (attempt != 1) return new(newImage);
                var barrier = barriers[path];
                barrier.Started.TrySetResult();
                barrier.Release.Wait(TimeSpan.FromSeconds(10));
                return new(oldImage);
            }
        });

        var renewed = 0;
        var canceled = 0;
        var bounded = true;
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var path = ThumbnailPath($"rejoin-{iteration}");
            using var release = new ManualResetEventSlim();
            using var cancellation = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            barriers[path] = (started, release);
            try
            {
                var abandoned = service.GetAsync(path, cancellation.Token);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                cancellation.Cancel();
                var fresh = service.GetAsync(path);
                bounded &= service.Diagnostics.Queued == 1 && service.Diagnostics.WorkerCount == 1;
                release.Set();
                try { await ThumbnailAwait(abandoned); }
                catch (OperationCanceledException) { canceled++; }
                var result = await ThumbnailAwait(fresh);
                if (result is not null && ThumbnailBlue(result.Image) == 210 && calls[path] == 2) renewed++;
            }
            finally
            {
                release.Set();
                barriers.TryRemove(path, out _);
            }
        }
        Check("canceling every active waiter retires that work, and same-path hovers receive fresh work in 32 handoffs",
            canceled == 32 && renewed == 32 && bounded);
    }

    private static async Task ThumbnailDetachmentChecks()
    {
        var sources = new ConcurrentBag<WeakReference<BitmapSource>>();
        using var service = new FileThumbnailService(new FileThumbnailOptions
        {
            WorkerCount = 1, MaximumCacheBytes = 1024 * 1024,
            MetadataReader = _ => ThumbnailStamp(), ThumbnailReader = _ => ThumbnailRetentionSource(sources)
        });
        for (var index = 0; index < 6; index++)
            await ThumbnailAwait(service.GetAsync(ThumbnailPath($"large-source-{index}")));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Check("cached thumbnails do not retain their larger provider bitmaps after bounded pixel detachment",
            sources.Count == 6 && sources.All(source => !source.TryGetTarget(out _))
            && service.Diagnostics.CacheBytes <= 1024 * 1024 && service.Diagnostics.CacheEntries == 2);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static ThumbnailResult ThumbnailRetentionSource(ConcurrentBag<WeakReference<BitmapSource>> sources)
    {
        var image = ThumbnailPicture(2048, 1024);
        sources.Add(new WeakReference<BitmapSource>(image));
        return new(image);
    }

    private static byte ThumbnailBlue(BitmapSource image)
    {
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        return pixels[0];
    }

    private static async Task ThumbnailContentChecks()
    {
        var image = ThumbnailPicture(600, 900);
        using var bounds = new FileThumbnailService(new FileThumbnailOptions
        {
            MetadataReader = _ => ThumbnailStamp(), ThumbnailReader = _ => new(image)
        });
        var result = await ThumbnailAwait(bounds.GetAsync(ThumbnailPath("portrait")));
        Check("large portrait thumbnails are frozen, bounded and preserve aspect ratio",
            result is not null && result.Image.IsFrozen && result.Image.PixelHeight <= 512
            && result.Image.PixelWidth <= 512 && Math.Abs((double)result.Image.PixelWidth / result.Image.PixelHeight - 2d / 3) < 0.003);

        foreach (var flag in new uint[] { 0x10, 0x400, 0x1000, 0x40000, 0x400000 })
        {
            using var denied = new FileThumbnailService(new FileThumbnailOptions
            {
                MetadataReader = _ => ThumbnailStamp(attributes: flag),
                ThumbnailReader = _ => throw new InvalidOperationException("content must not be read")
            });
            Check($"directory/reparse/offline/recall attribute 0x{flag:X} prevents content access",
                await ThumbnailAwait(denied.GetAsync(ThumbnailPath("cloud"))) is null && denied.Diagnostics.Extractions == 0);
        }

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var observedCancel = false;
        try { await bounds.GetAsync(ThumbnailPath("already-canceled"), canceled.Token); }
        catch (OperationCanceledException) { observedCancel = true; }
        Check("an already canceled hover is never queued", observedCancel && bounds.Diagnostics.Extractions == 1);

        var version = 1;
        using var changing = new FileThumbnailService(new FileThumbnailOptions
        {
            MetadataReader = _ => ThumbnailStamp(version),
            ThumbnailReader = _ => { version++; return new(image); }
        });
        Check("content changed during extraction is never published or cached",
            await ThumbnailAwait(changing.GetAsync(ThumbnailPath("changing"))) is null && changing.Diagnostics.CacheEntries == 0);
    }

    private static async Task ThumbnailRealFileChecks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "UltraExplorerThumbnailChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "landscape.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(ThumbnailPicture(1200, 800)));
            using (var output = File.Create(path)) encoder.Save(output);
            using var service = new FileThumbnailService();
            var result = await ThumbnailAwait(service.GetAsync(path));
            Console.WriteLine($"  PNG fixture: original {result?.OriginalWidth}x{result?.OriginalHeight}, preview {result?.Image.PixelWidth}x{result?.Image.PixelHeight}");
            Check("real PNG content decodes with original dimensions and a bounded detached thumbnail",
                result is not null && result.Image.IsFrozen && result.OriginalWidth == 1200 && result.OriginalHeight == 800
                && result.Image.PixelWidth == 512 && result.Image.PixelHeight is >= 340 and <= 342);

            var jpegPath = Path.Combine(directory, "camera-portrait.jpg");
            var metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
            var jpeg = new JpegBitmapEncoder { QualityLevel = 90 };
            jpeg.Frames.Add(BitmapFrame.Create(ThumbnailPicture(800, 400), null, metadata, null));
            using (var output = File.Create(jpegPath)) jpeg.Save(output);
            var portrait = await ThumbnailAwait(service.GetAsync(jpegPath));
            Console.WriteLine($"  JPEG EXIF6 fixture: original {portrait?.OriginalWidth}x{portrait?.OriginalHeight}, preview {portrait?.Image.PixelWidth}x{portrait?.Image.PixelHeight}");
            Check("real JPEG camera orientation rotates the preview and displayed original dimensions",
                portrait is not null && portrait.OriginalWidth == 400 && portrait.OriginalHeight == 800
                && portrait.Image.PixelWidth == 256 && portrait.Image.PixelHeight == 512 && portrait.Image.IsFrozen);
            // OnLoad + detachment must not keep a lock on the file.
            File.Delete(path);
            Check("decoded thumbnails release the source file and deletion invalidates their cache",
                !File.Exists(path) && await ThumbnailAwait(service.GetAsync(path)) is null);

            var corrupt = Path.Combine(directory, "corrupt.png");
            await File.WriteAllTextAsync(corrupt, "not a PNG");
            var corruptResult = await ThumbnailAwait(service.GetAsync(corrupt));
            Check("a damaged image falls back to its actual readable contents", corruptResult is { Detail: not null }
                && corruptResult.Image.IsFrozen);
            var unsupported = Path.Combine(directory, "notes.txt");
            await File.WriteAllTextAsync(unsupported, "plain text does not have an image thumbnail");
            var textResult = await ThumbnailAwait(service.GetAsync(unsupported));
            Check("text uses its real content instead of a Shell file type icon", textResult is { Detail: not null }
                && textResult.Image.IsFrozen);

            var lockPath = Path.Combine(directory, "locked.png");
            File.Copy(corrupt, lockPath);
            using (var locked = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None))
                Check("locked files cannot fault the service", await ThumbnailAwait(service.GetAsync(lockPath)) is null);

            var realFolder = Environment.GetEnvironmentVariable("ULTRAEXPLORER_PREVIEW_SAMPLE_FOLDER");
            if (!string.IsNullOrWhiteSpace(realFolder) && Directory.Exists(realFolder))
            {
                var real = Directory.EnumerateFiles(realFolder, "*.png").Take(3).ToArray();
                var previews = await Task.WhenAll(real.Select(file => service.GetAsync(file))).WaitAsync(TimeSpan.FromSeconds(10));
                Check("optional external PNG samples decode read-only into frozen content thumbnails",
                    real.Length != 0 && previews.All(preview => preview is not null && preview.Image.IsFrozen
                        && Math.Max(preview.Image.PixelWidth, preview.Image.PixelHeight) <= 512));
                Console.WriteLine($"  external PNG samples checked: {real.Length}");
            }
            else Console.WriteLine("  optional external PNG samples not configured; generated PNG fixture covered decoding");
        }
        finally { TryDelete(directory); }
    }

    private static void ThumbnailOrientationChecks()
    {
        var pixels = new byte[2 * 3 * 4];
        for (var index = 0; index < 6; index++)
        {
            pixels[index * 4] = (byte)(index + 1);
            pixels[index * 4 + 3] = 255;
        }
        var source = BitmapSource.Create(2, 3, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        source.Freeze();
        byte[][] expected =
        [
            [1, 2, 3, 4, 5, 6], [2, 1, 4, 3, 6, 5], [6, 5, 4, 3, 2, 1], [5, 6, 3, 4, 1, 2],
            [1, 3, 5, 2, 4, 6], [5, 3, 1, 6, 4, 2], [6, 4, 2, 5, 3, 1], [2, 4, 6, 1, 3, 5]
        ];
        for (var orientation = 1; orientation <= 8; orientation++)
        {
            var image = FileThumbnailService.OrientImage(source, orientation);
            var actual = new byte[24];
            image.CopyPixels(actual, image.PixelWidth * 4, 0);
            Check($"EXIF orientation {orientation} preserves the expected content rotation/reflection",
                image.IsFrozen && image.PixelWidth == (orientation < 5 ? 2 : 3)
                && Enumerable.Range(0, 6).Select(index => actual[index * 4]).SequenceEqual(expected[orientation - 1]));
        }
    }

    private static async Task ThumbnailLiteralPathChecks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "UltraExplorerThumbnailLiteral", Guid.NewGuid().ToString("N"));
        var files = new List<string>();
        var folders = new List<string>();
        Directory.CreateDirectory(directory);
        try
        {
            using var service = new FileThumbnailService();
            foreach (var suffix in new[] { "", ".", " " })
            {
                var path = Path.Combine(directory, "leaf.png" + suffix);
                files.Add(path);
                ThumbnailWritePngLiteral(path, suffix == "" ? (byte)40 : suffix == "." ? (byte)210 : (byte)110);
            }
            var leafResults = await Task.WhenAll(files.Select(path => service.GetAsync(path))).WaitAsync(TimeSpan.FromSeconds(5));
            Check("literal trailing-dot and trailing-space image names show their own pixels instead of a sibling's",
                leafResults.All(result => result is not null && result.OriginalWidth == 32)
                && leafResults.Select(result => ThumbnailBlue(result!.Image)).SequenceEqual(new byte[] { 40, 210, 110 }));

            var parentPaths = new List<string>();
            foreach (var suffix in new[] { "", ".", " " })
            {
                var parent = Path.Combine(directory, "parent" + suffix);
                folders.Add(parent);
                Directory.CreateDirectory(ThumbnailExtendedPath(parent));
                var path = Path.Combine(parent, "picture.png");
                files.Add(path);
                parentPaths.Add(path);
                ThumbnailWritePngLiteral(path, suffix == "" ? (byte)40 : suffix == "." ? (byte)210 : (byte)110);
            }
            var parentResults = await Task.WhenAll(parentPaths.Select(path => service.GetAsync(path))).WaitAsync(TimeSpan.FromSeconds(5));
            Check("literal trailing-dot and trailing-space parent names remain distinct during thumbnail IO",
                parentResults.All(result => result is not null)
                && parentResults.Select(result => ThumbnailBlue(result!.Image)).SequenceEqual(new byte[] { 40, 210, 110 }));

            string? described = null;
            using var unc = new FileThumbnailService(new FileThumbnailOptions
            {
                WorkerCount = 1, MetadataReader = path => { described = path; return null; }
            });
            await ThumbnailAwait(unc.GetAsync(@"\\server\share\folder.\picture.png "));
            Check("literal UNC names use extended UNC syntax before metadata lookup without accessing a share",
                described == @"\\?\UNC\server\share\folder.\picture.png " && unc.Diagnostics.Extractions == 0);
        }
        finally
        {
            // Delete only the exact owned entries, using extended paths even
            // for the normal siblings. No normalizing recursive cleanup may
            // mistake a literal folder for its neighbour.
            foreach (var path in files) File.Delete(ThumbnailExtendedPath(path));
            foreach (var folder in folders) Directory.Delete(ThumbnailExtendedPath(folder), recursive: false);
            Directory.Delete(directory, recursive: false);
        }
    }

    private static string ThumbnailExtendedPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal)
        ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    private static void ThumbnailWritePngLiteral(string path, byte blue)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ThumbnailPicture(blue: blue)));
        using var output = new FileStream(ThumbnailExtendedPath(path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(output);
    }

    private static async Task ThumbnailSameStampOverwriteChecks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "UltraExplorerThumbnailOverwrite", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "same-metadata.bmp");
            void Write(byte blue)
            {
                var encoder = new BmpBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(ThumbnailPicture(blue: blue)));
                using var output = File.Create(path);
                encoder.Save(output);
            }
            Write(40);
            var before = new FileInfo(path);
            var length = before.Length;
            var modified = before.LastWriteTimeUtc;
            long clock = 0;
            using var service = new FileThumbnailService(new FileThumbnailOptions
            {
                WorkerCount = 1, PositiveCacheMilliseconds = 100, TickCount = () => Interlocked.Read(ref clock)
            });
            var first = await ThumbnailAwait(service.GetAsync(path));
            Write(210);
            File.SetLastWriteTimeUtc(path, modified);
            var after = new FileInfo(path);
            var reused = await ThumbnailAwait(service.GetAsync(path));
            Check("an owned overwrite fixture changes pixels while preserving file length and last-write time",
                after.Length == length && after.LastWriteTimeUtc == modified && first is not null
                && ThumbnailBlue(first.Image) == 40 && ReferenceEquals(first, reused));
            Interlocked.Exchange(ref clock, 101);
            var fresh = await ThumbnailAwait(service.GetAsync(path));
            Check("positive cache expiry refreshes same-size same-timestamp content instead of retaining stale pixels forever",
                fresh is not null && ThumbnailBlue(fresh.Image) == 210 && !ReferenceEquals(first, fresh)
                && service.Diagnostics.Extractions == 2);
        }
        finally { TryDelete(directory); }
    }
}
