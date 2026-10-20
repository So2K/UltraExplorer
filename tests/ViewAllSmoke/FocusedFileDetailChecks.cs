using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task FocusedFileDetailChecks()
    {
        Section("focused file detail: one bounded high-resolution or crisp-text result");
        await FocusedDetailDemandChecks();
        await FocusedDetailStaleChecks();
        await FocusedDetailPayloadChecks();
        await FocusedDetailProviderChecks();
    }

    private static FocusedFileDetailOptions FocusedOptions(InlineThumbnailClock clock, int settle = 120, long bytes = 16L * 1024 * 1024) => new()
    {
        TickCount = () => clock.Now, Delay = clock.Delay, SettleMilliseconds = settle, MaximumCacheBytes = bytes
    };

    private static async Task FocusedDetailDemandChecks()
    {
        var clock = new InlineThumbnailClock();
        var calls = 0;
        var posts = new ConcurrentQueue<Action>();
        var redraws = 0;
        var image = ThumbnailPicture(1024, 512);
        using var controller = new FocusedFileDetailController((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<FocusedFileDetailResult?>(new(image, null, 4096, 2048));
        }, posts.Enqueue, () => redraws++, FocusedOptions(clock));
        var focus = new InlineThumbnailCandidate("C:\\detail\\photo.png", 1);
        controller.Update(focus, true);
        clock.Advance(1000);
        await Task.Delay(10);
        Check("deep detail starts no extraction during camera motion", calls == 0 && controller.Diagnostics.ActiveRequests == 0);
        controller.Update(focus, false);
        clock.Advance(119);
        await Task.Delay(10);
        Check("a focused file settles for120ms before high-resolution extraction", calls == 0);
        clock.Advance(1);
        Check("settled focus retains one frozen high-resolution result and its original dimensions",
            await InlineWaitUntil(() => controller.TryGet(focus.Path, focus.Version, out _))
            && controller.TryGet(focus.Path, focus.Version, out var result) && ReferenceEquals(result!.Image, image)
            && result.OriginalWidth == 4096 && result.OriginalHeight == 2048 && controller.Diagnostics.CacheBytes == 1024L * 512 * 4);
        for (var index = 0; index < 2000; index++)
        {
            controller.Update(focus, false);
            controller.TryGet(focus.Path, focus.Version, out _);
        }
        clock.Advance(120_000);
        Check("warm detail lookup is memory-only and unchanged focus has no periodic retry", calls == 1 && clock.PendingDelays == 0);
        controller.Suspend();
        while (posts.TryDequeue(out var stale)) stale();
        Check("leaving deep detail releases its image immediately and invalidates queued redraws",
            !controller.Diagnostics.HasDemand && controller.Diagnostics.CacheBytes == 0 && redraws == 0
            && !controller.TryGet(focus.Path, focus.Version, out _));
    }

    private static async Task FocusedDetailStaleChecks()
    {
        var clock = new InlineThumbnailClock();
        var calls = new ConcurrentQueue<(string Path, CancellationToken Token, TaskCompletionSource<FocusedFileDetailResult?> Completion)>();
        using var controller = new FocusedFileDetailController((path, token) =>
        {
            var completion = new TaskCompletionSource<FocusedFileDetailResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            calls.Enqueue((path, token, completion));
            return completion.Task;
        }, _ => { }, () => { }, FocusedOptions(clock, 0));
        controller.Update(new("C:\\detail\\a.png", 1), false);
        Check("the token-ignoring detail fixture starts one actual provider", await InlinePumpUntil(clock, () => calls.Count == 1));
        var old = calls.First();
        controller.Update(new("C:\\detail\\A.png ", 2), false);
        clock.Advance(500);
        await Task.Delay(10);
        Check("changing literal focus cancels old demand but cannot invent a replacement slot around a blocked provider",
            old.Token.IsCancellationRequested && calls.Count == 1 && controller.Diagnostics.ActiveRequests == 1);
        old.Completion.TrySetResult(new(ThumbnailPicture(blue: 40), null));
        Check("old literal/version completion is discarded before the new focus can display it",
            await InlinePumpUntil(clock, () => calls.Count == 2) && controller.Diagnostics.CacheBytes == 0
            && !controller.TryGet(old.Path, 1, out _) && !controller.TryGet("C:\\detail\\A.png ", 2, out _));
        var fresh = calls.Last();
        var image = ThumbnailPicture(blue: 140);
        fresh.Completion.TrySetResult(new(image, null));
        Check("literal case and trailing spaces are preserved in the sole detail request",
            fresh.Path == "C:\\detail\\A.png " && await InlineWaitUntil(() => controller.TryGet(fresh.Path, 2, out _))
            && controller.TryGet(fresh.Path, 2, out var shown) && ReferenceEquals(shown!.Image, image));
        controller.Update(new(fresh.Path, 3), false);
        Check("an edited listing identity starts a fresh bounded request", await InlinePumpUntil(clock, () => calls.Count == 3));
        var blocked = calls.Last();
        controller.Dispose();
        Check("permanent detail disposal releases retained pixels without waiting for a provider",
            !controller.Diagnostics.HasDemand && controller.Diagnostics.CacheBytes == 0 && blocked.Token.IsCancellationRequested);
        blocked.Completion.TrySetResult(new(image, null));
        Check("late detail completion after disposal cannot revive content",
            await InlineWaitUntil(() => controller.Diagnostics.ActiveRequests == 0) && controller.Diagnostics.CacheBytes == 0);
    }

    private static async Task FocusedDetailPayloadChecks()
    {
        var clock = new InlineThumbnailClock();
        var text = new string('x', 40_000);
        using var controller = new FocusedFileDetailController((_, _) => Task.FromResult<FocusedFileDetailResult?>(
            new(null, new DocumentPreviewData(text, "UTF-8", true))), _ => { }, () => { }, FocusedOptions(clock, 0));
        controller.Update(new("large.txt", 1), false);
        Check("detail text remains data for crisp rendering and is bounded to32768characters",
            await InlinePumpUntil(clock, () => controller.TryGet("large.txt", 1, out _))
            && controller.TryGet("large.txt", 1, out var result) && result!.Image is null
            && result.Document is { Text.Length: 32_768, Truncated: true, CanEdit: false }
            && controller.Diagnostics.CacheBytes < 70_000);

        var oversizedClock = new InlineThumbnailClock();
        var calls = 0;
        using var oversized = new FocusedFileDetailController((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<FocusedFileDetailResult?>(new(ThumbnailPicture(), null));
        }, _ => { }, () => { }, FocusedOptions(oversizedClock, 0, bytes: 100));
        oversized.Update(new("over-budget.png", 1), false);
        Check("an over-budget payload is rejected without retaining image references",
            await InlinePumpUntil(oversizedClock, () => calls == 1 && oversized.Diagnostics.ActiveRequests == 0)
            && oversized.Diagnostics.CacheBytes == 0 && !oversized.TryGet("over-budget.png", 1, out _));
        oversizedClock.Advance(60_000);
        oversized.Update(new("over-budget.png", 1), false);
        Check("unsupported or oversized detail is attempted only once per stable focus", calls == 1 && oversizedClock.PendingDelays == 0);

        var invalidClock = new InlineThumbnailClock();
        var invalidPixels = new byte[16 * 16 * 4];
        var invalid = BitmapSource.Create(16, 16, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, invalidPixels, 16 * 4);
        using var unsafeSource = new FocusedFileDetailController((_, _) => Task.FromResult<FocusedFileDetailResult?>(new(invalid, null)),
            _ => { }, () => { }, FocusedOptions(invalidClock, 0));
        unsafeSource.Update(new("unfrozen.png", 1), false);
        Check("an unfrozen dispatcher-owned result cannot escape even when validation throws on a foreign thread",
            await InlinePumpUntil(invalidClock, () => unsafeSource.Diagnostics.RequestsStarted == 1 && unsafeSource.Diagnostics.ActiveRequests == 0)
            && unsafeSource.Diagnostics.CacheBytes == 0 && !unsafeSource.TryGet("unfrozen.png", 1, out _));
    }

    private static async Task FocusedDetailProviderChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerFocusedDetail", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "wide.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(ThumbnailPicture(4096, 1024, blue: 73)));
            using (var file = File.Create(path)) encoder.Save(file);
            var image = await FocusedFileDetailProvider.GetAsync(path);
            Check("the real image provider decodes2048px detail rather than enlarging a256px thumbnail",
                image is { Image.IsFrozen: true, Image.PixelWidth: 2048, Image.PixelHeight: 512, OriginalWidth: 4096, OriginalHeight: 1024 }
                && image.Image is not TransformedBitmap && image.Image is not FormatConvertedBitmap && ThumbnailBlue(image.Image) == 73);
            var textPath = Path.Combine(root, "notes.txt");
            await File.WriteAllTextAsync(textPath, "Файл с заметками\n" + new string('z', 40_000), new UTF8Encoding(true));
            var document = await FocusedFileDetailProvider.GetAsync(textPath);
            Check("the real text provider preserves Unicode and returns bounded text instead of rasterized glyphs",
                document is { Image: null, Document.Truncated: true, Document.IsBinary: false }
                && document.Document.Text.StartsWith("Файл с заметками\n", StringComparison.Ordinal)
                && document.Document.Text.Length <= FocusedFileDetailProvider.MaximumCharacters);
            Check("deep detail does not launch native model/media or executable handlers",
                !FocusedFileDetailProvider.Supports("model.glb") && !FocusedFileDetailProvider.Supports("movie.mp4")
                && !FocusedFileDetailProvider.Supports("installer.exe") && FocusedFileDetailProvider.Supports("document.pdf"));
            Check("missing focused files quietly retain the ordinary thumbnail fallback",
                await FocusedFileDetailProvider.GetAsync(Path.Combine(root, "missing.png")) is null);
        }
        finally { TryDelete(root); }
    }
}
