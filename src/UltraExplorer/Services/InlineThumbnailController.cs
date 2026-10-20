using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UltraExplorer.Services;

/// <summary>A literal listing identity; constructing it never reads the file.</summary>
internal readonly record struct InlineThumbnailCandidate(string Path, long Version = 0);

/// <summary>
/// Small viewport cache over the window's existing thumbnail service. Rendering
/// only performs TryGet; extraction starts off the dispatcher after the camera
/// settles. No decoder, native worker or per-file timer belongs to this class.
/// </summary>
internal sealed class InlineThumbnailController : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<string, CancellationToken, Task<ThumbnailResult?>> _fetch;
    private readonly Action<Action> _post;
    private readonly Action _changed;
    private readonly InlineThumbnailOptions _options;
    private readonly Dictionary<string, Demand> _demands = new(StringComparer.Ordinal);
    private readonly List<Demand> _ordered = [];
    private readonly List<InlineThumbnailCandidate> _next = [];
    private readonly HashSet<string> _nextPaths = new(StringComparer.Ordinal);
    private readonly HashSet<Work> _active = [];
    private readonly Dictionary<string, LinkedListNode<Entry>> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();
    private CancellationTokenSource? _wake;
    private long _wakeAt, _settledAt, _cacheBytes, _requests, _notifications, _epoch;
    private bool _moving, _disposed, _notificationQueued;

    public InlineThumbnailController(Func<string, CancellationToken, Task<ThumbnailResult?>> fetch,
        Action<Action> post, Action changed, InlineThumbnailOptions? options = null)
    {
        _fetch = fetch;
        _post = post;
        _changed = changed;
        _options = options ?? new InlineThumbnailOptions();
    }

    /// <summary>Center-first visible tiles only, capped independently of folder size.</summary>
    public void UpdateVisible(ReadOnlySpan<InlineThumbnailCandidate> candidates, bool moving)
    {
        List<Work>? cancel = null;
        lock (_gate)
        {
            if (_disposed) return;
            var now = _options.TickCount();
            ExpireLocked(now);
            _next.Clear();
            _nextPaths.Clear();
            foreach (var candidate in candidates)
            {
                if (_next.Count >= Math.Clamp(_options.MaximumVisible, 1, 24)) break;
                if (!string.IsNullOrWhiteSpace(candidate.Path) && _nextPaths.Add(candidate.Path))
                    _next.Add(candidate);
            }

            var different = _next.Count != _ordered.Count;
            if (!different)
                for (var index = 0; index < _next.Count; index++)
                    if (_next[index] != _ordered[index].Candidate) { different = true; break; }

            if (different)
            {
                _ordered.Clear();
                foreach (var candidate in _next)
                {
                    if (!_demands.TryGetValue(candidate.Path, out var demand) || demand.Candidate != candidate)
                        demand = new Demand(candidate);
                    _ordered.Add(demand);
                    if (_cache.TryGetValue(candidate.Path, out var cached))
                    {
                        if (cached.Value.Version != candidate.Version) RemoveLocked(cached);
                        else demand.Attempted = true; // Warm reuse counts as this demand's one attempt too.
                    }
                }
                _demands.Clear();
                foreach (var demand in _ordered) _demands.Add(demand.Candidate.Path, demand);
                foreach (var work in _active)
                    if (!WantedLocked(work)) (cancel ??= []).Add(work);
            }
            if (different || (_moving && !moving))
                _settledAt = now + Math.Max(0, _options.SettleMilliseconds);
            _moving = moving;
            ScheduleLocked(now);
        }
        Cancel(cancel);
    }

    /// <summary>A frozen reference lookup only: no metadata, decoding, task or dispatcher work.</summary>
    public bool TryGet(string path, long version, out BitmapSource? image)
    {
        lock (_gate)
        {
            image = null;
            if (_disposed || !_cache.TryGetValue(path, out var entry)
                || entry.Value.Version != version || (entry.Value.Expires <= _options.TickCount() && !PinnedLocked(entry.Value))
                || entry.Value.Image is not { } ready) return false;
            _lru.Remove(entry);
            _lru.AddFirst(entry);
            image = ready;
            return true;
        }
    }

    /// <summary>Hidden panes stop demand immediately; valid warm pixels can survive a tab switch.</summary>
    public void Suspend(bool clearCache = false)
    {
        List<Work> cancel;
        lock (_gate)
        {
            if (_disposed) return;
            ClearDemandLocked();
            cancel = [.. _active];
            if (clearCache) ClearCacheLocked();
            ScheduleLocked(_options.TickCount());
        }
        Cancel(cancel);
    }

    public void Dispose()
    {
        List<Work> cancel;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ClearDemandLocked();
            ClearCacheLocked();
            cancel = [.. _active];
            CancelWakeLocked();
        }
        // A provider which ignores cancellation still owns its real active
        // slot until it returns. Never spawn replacement work around a hang.
        Cancel(cancel);
    }

    internal InlineThumbnailDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
                return new(_ordered.Count, _active.Count, _cache.Count, _cacheBytes, _requests, _notifications);
        }
    }

    private void ClearDemandLocked()
    {
        _demands.Clear();
        _ordered.Clear();
        _next.Clear();
        _nextPaths.Clear();
        _moving = false;
        _epoch++;
        _notificationQueued = false;
    }

    private void ClearCacheLocked()
    {
        _cache.Clear();
        _lru.Clear();
        _cacheBytes = 0;
    }

    private bool WantedLocked(Work work) => !work.Token.IsCancellationRequested
        && _demands.TryGetValue(work.Candidate.Path, out var demand)
        && ReferenceEquals(demand, work.Demand);

    private bool PinnedLocked(Entry entry) => _demands.TryGetValue(entry.Path, out var demand)
        && demand.Candidate.Version == entry.Version;

    private bool HasPendingLocked()
    {
        foreach (var demand in _ordered)
            if (!demand.Attempted && !_cache.ContainsKey(demand.Candidate.Path)
                && !_active.Any(work => work.Candidate.Path == demand.Candidate.Path)) return true;
        return false;
    }

    // One lazy cancellable delay covers settling and offscreen cache expiry.
    // A stable visible identity is attempted only once: keep its bounded image
    // or negative entry pinned, without a periodic refresh timer. Besides
    // avoiding flicker, this lets slow first attempts reach the viewport edge
    // instead of repeatedly serving already-attempted center files.
    // An earlier already-scheduled wake is allowed to fire once and recheck.
    private void ScheduleLocked(long now)
    {
        if (_disposed) return;
        var next = long.MaxValue;
        foreach (var entry in _lru)
            if (!PinnedLocked(entry)) next = Math.Min(next, entry.Expires);
        if (!_moving && _active.Count < Math.Clamp(_options.MaximumActive, 1, 2) && HasPendingLocked())
            next = Math.Min(next, Math.Max(now, _settledAt));
        if (next == long.MaxValue) { CancelWakeLocked(); return; }
        if (_wake is not null && _wakeAt <= next) return;
        CancelWakeLocked();
        var wake = new CancellationTokenSource();
        _wake = wake;
        _wakeAt = next;
        _ = WakeAsync(wake, (int)Math.Clamp(next - now, 1, int.MaxValue));
    }

    private void CancelWakeLocked()
    {
        var wake = _wake;
        _wake = null;
        if (wake is null) return;
        wake.Cancel();
        // WakeAsync owns disposal after its await/cancellation continuation.
    }

    private async Task WakeAsync(CancellationTokenSource wake, int milliseconds)
    {
        try
        {
            await _options.Delay(milliseconds, wake.Token).ConfigureAwait(false);
            List<Work>? start = null;
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_wake, wake)) return;
                _wake = null;
                var now = _options.TickCount();
                ExpireLocked(now);
                if (!_moving && now >= _settledAt)
                {
                    foreach (var demand in _ordered)
                    {
                        if (_active.Count >= Math.Clamp(_options.MaximumActive, 1, 2)) break;
                        if (demand.Attempted || _cache.ContainsKey(demand.Candidate.Path)) continue;
                        // A canceled native request for this literal file may
                        // still be returning; don't attach a new generation.
                        if (_active.Any(work => work.Candidate.Path == demand.Candidate.Path)) continue;
                        demand.Attempted = true;
                        var work = new Work(demand);
                        _active.Add(work);
                        _requests++;
                        (start ??= []).Add(work);
                    }
                }
                ScheduleLocked(now);
            }
            if (start is not null)
                foreach (var work in start)
                    _ = Task.Run(() => FetchAsync(work));
        }
        catch (OperationCanceledException) when (wake.IsCancellationRequested) { }
        finally { wake.Dispose(); }
    }

    private async Task FetchAsync(Work work)
    {
        BitmapSource? image = null;
        try
        {
            work.Token.ThrowIfCancellationRequested();
            var result = await _fetch(work.Candidate.Path, work.Token).ConfigureAwait(false);
            work.Token.ThrowIfCancellationRequested();
            image = PrepareImage(result?.Image);
        }
        catch (Exception) { /* Missing/corrupt content and canceled native handlers keep their normal icon. */ }
        lock (_gate)
        {
            _active.Remove(work);
            if (!_disposed && WantedLocked(work))
            {
                var now = _options.TickCount();
                var bytes = ImageBytes(image);
                // The existing service's 512px hover cache is unchanged;
                // presentation images are bounded separately below.
                if (bytes <= 0 || bytes > Math.Max(0, _options.MaximumCacheBytes)) { image = null; bytes = 0; }
                if (_cache.TryGetValue(work.Candidate.Path, out var old)) RemoveLocked(old);
                var entry = new Entry(work.Candidate.Path, work.Candidate.Version, image, bytes,
                    now + Math.Max(1, image is null ? _options.NegativeCacheMilliseconds : _options.PositiveCacheMilliseconds));
                var node = _lru.AddFirst(entry);
                _cache.Add(entry.Path, node);
                _cacheBytes += bytes;
                TrimLocked();
                if (image is not null && _cache.ContainsKey(entry.Path)) QueueChangedLocked();
            }
            ScheduleLocked(_options.TickCount());
        }
        work.Dispose();
    }

    private BitmapSource? PrepareImage(BitmapSource? source)
    {
        if (ImageBytes(source) <= 0) return null;
        var maximum = Math.Clamp(_options.MaximumDimension, 32, FileThumbnailService.MaximumDimension);
        var longest = Math.Max(source!.PixelWidth, source.PixelHeight);
        if (longest <= maximum) return source;

        // This runs once on FetchAsync's background continuation, never in a
        // render lookup. 256px preserves detail for the <=104 DIP card slot
        // while all24 square previews fit within6MiB rather than24MiB.
        var scale = (double)maximum / longest;
        BitmapSource prepared = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        if (prepared.Format != PixelFormats.Bgra32)
            prepared = new FormatConvertedBitmap(prepared, PixelFormats.Bgra32, null, 0);
        var stride = checked(prepared.PixelWidth * 4);
        var pixels = new byte[checked(stride * prepared.PixelHeight)];
        prepared.CopyPixels(pixels, stride, 0);
        // A TransformedBitmap alone would keep the larger source alive. The
        // materialized bitmap owns only these bounded presentation pixels.
        var detached = BitmapSource.Create(prepared.PixelWidth, prepared.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        detached.Freeze();
        return detached;
    }

    private static long ImageBytes(BitmapSource? image)
    {
        if (image is null || !image.IsFrozen || image.PixelWidth <= 0 || image.PixelHeight <= 0
            || image.PixelWidth > FileThumbnailService.MaximumDimension || image.PixelHeight > FileThumbnailService.MaximumDimension)
            return 0;
        return ((long)image.PixelWidth * Math.Max(1, image.Format.BitsPerPixel) + 7) / 8 * image.PixelHeight;
    }

    private void ExpireLocked(long now)
    {
        var node = _lru.Last;
        while (node is not null)
        {
            var previous = node.Previous;
            if (node.Value.Expires <= now && !PinnedLocked(node.Value))
            {
                RemoveLocked(node);
            }
            node = previous;
        }
    }

    private void TrimLocked()
    {
        while (_cache.Count > Math.Clamp(_options.MaximumCacheEntries, 1, 32)
            || _cacheBytes > Math.Max(0, _options.MaximumCacheBytes))
        {
            var victim = _lru.Last!;
            var worstPriority = -1;
            for (var node = _lru.Last; node is not null; node = node.Previous)
            {
                var priority = _ordered.FindIndex(demand => demand.Candidate.Path == node.Value.Path);
                if (priority < 0) { victim = node; break; }
                if (priority > worstPriority) { worstPriority = priority; victim = node; }
            }
            RemoveLocked(victim);
        }
        // Evicted still-visible demands remain attempted. With a tiny budget
        // they must not decode over and over, displacing one another forever.
    }

    private void RemoveLocked(LinkedListNode<Entry> node)
    {
        _lru.Remove(node);
        _cache.Remove(node.Value.Path);
        _cacheBytes -= node.Value.Bytes;
    }

    private void QueueChangedLocked()
    {
        if (_notificationQueued || _disposed || _ordered.Count == 0) return;
        _notificationQueued = true;
        var epoch = _epoch;
        _notifications++;
        // The adapter posts at Render/Background priority; multiple completed
        // thumbnails before that dispatcher turn share one redraw request.
        try
        {
            _post(() =>
            {
                lock (_gate)
                {
                    if (_disposed || epoch != _epoch) return;
                    _notificationQueued = false;
                    if (_ordered.Count == 0) return;
                    // Serialize this very small redraw signal with Suspend /
                    // Dispose so a callback already dequeued by the dispatcher
                    // cannot outlive the viewport it belonged to.
                    _changed();
                }
            });
        }
        catch (InvalidOperationException) { _notificationQueued = false; /* Dispatcher shutting down. */ }
    }

    private static void Cancel(IEnumerable<Work>? work)
    {
        if (work is not null) foreach (var request in work) request.Cancel();
    }

    private sealed class Demand(InlineThumbnailCandidate candidate)
    {
        public InlineThumbnailCandidate Candidate { get; } = candidate;
        public bool Attempted;
    }

    private sealed class Work : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private Task? _callbacks;
        private bool _disposed;
        public Demand Demand { get; }
        public InlineThumbnailCandidate Candidate => Demand.Candidate;
        public CancellationToken Token { get; }

        // Capture the token before cancellation source disposal; callers can
        // still safely check the token of a returning abandoned request.
        public Work(Demand demand) { Demand = demand; Token = _cancellation.Token; }

        public void Cancel()
        {
            lock (_gate)
                if (!_disposed) _callbacks ??= _cancellation.CancelAsync();
        }

        public void Dispose()
        {
            Task? callbacks;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                callbacks = _callbacks;
            }
            if (callbacks is { IsCompleted: false })
                _ = callbacks.ContinueWith(_ => _cancellation.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else _cancellation.Dispose();
        }
    }

    private sealed record Entry(string Path, long Version, BitmapSource? Image, long Bytes, long Expires);

    // Extension comes from NestedFile (already lowercased/interned). This
    // whitelist only selects visual content; executable/icon handlers are not
    // invoked for every enlarged application or installer tile.
    private static readonly HashSet<string> PreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "bmp", "gif", "tif", "tiff", "ico", "wdp", "jxr", "webp", "avif", "heic", "heif",
        "jxl", "jp2", "j2k", "jpf", "jpx", "jpm", "mj2", "psd", "psb", "dds", "tga", "exr", "hdr", "pic", "pfm",
        "ppm", "pgm", "pbm", "pnm", "pcx", "pict", "pct", "sgi", "rgb", "rgba", "ras", "sun", "xbm", "xpm", "mng", "jng",
        "cr2", "cr3", "nef", "nrw", "arw", "dng", "orf", "rw2", "raf", "pef", "srw", "3fr", "svg", "pdf",
        "txt", "text", "md", "markdown", "rst", "log", "nfo", "diz", "readme", "csv", "tsv", "json", "jsonl", "jsonc",
        "xml", "yaml", "yml", "toml", "ini", "cfg", "conf", "config", "properties", "env", "gitignore", "editorconfig",
        "cs", "csx", "csproj", "vb", "fs", "fsx", "sln", "slnx", "xaml", "resx", "c", "h", "cpp", "hpp", "cc", "cxx", "hxx",
        "m", "mm", "swift", "js", "jsx", "ts", "tsx", "vue", "svelte", "html", "htm", "css", "scss", "less", "py", "pyi",
        "rb", "rs", "go", "java", "kt", "kts", "php", "lua", "dart", "ps1", "psm1", "psd1", "bat", "cmd", "sh", "bash", "zsh", "fish",
        "sql", "graphql", "gql", "proto", "hlsl", "glsl", "shader", "tex", "bib", "rtf", "doc", "docx", "odt", "ppt", "pptx", "odp",
        "xls", "xlsx", "ods", "epub", "fb2", "ipynb", "diff", "patch", "reg", "manifest",
        "3ds", "3mf", "abc", "brep", "dae", "dcm", "drc", "dxf", "fbx", "glb", "gltf", "gml", "ifc", "iges", "igs", "las", "laz",
        "mdl", "mha", "mhd", "nhdr", "nrrd", "obj", "off", "p21", "pcd", "ply", "pts", "ptx", "spz", "splat", "step", "stl", "stp",
        "stpnc", "usd", "usda", "usdc", "usdz", "vdb", "vtk", "vtkhdf", "vti", "vtm", "vtp", "vtr", "vts", "vtu", "vrml", "wrl", "x", "xbf",
        "3g2", "3gp", "aac", "ac3", "aif", "aiff", "alac", "amr", "ape", "apng", "asf", "avi", "dts", "f4v", "flac", "flv",
        "m2ts", "m4a", "m4v", "mka", "mkv", "mov", "mp2", "mp3", "mp4", "mpeg", "mpg", "mts", "oga", "ogg", "ogv", "opus", "rm", "rmvb",
        "vob", "wav", "webm", "wma", "wmv", "wv"
    };

    internal static bool CanPreview(string extensionWithoutDot) => PreviewExtensions.Contains(extensionWithoutDot);
}

internal sealed class InlineThumbnailOptions
{
    public int MaximumDimension { get; init; } = 256;
    public int MaximumVisible { get; init; } = 24;
    public int MaximumActive { get; init; } = 2;
    public int MaximumCacheEntries { get; init; } = 32;
    public long MaximumCacheBytes { get; init; } = 12L * 1024 * 1024;
    public int SettleMilliseconds { get; init; } = 120;
    public int PositiveCacheMilliseconds { get; init; } = 30_000;
    public int NegativeCacheMilliseconds { get; init; } = 5_000;
    public Func<long> TickCount { get; init; } = () => Environment.TickCount64;
    public Func<int, CancellationToken, Task> Delay { get; init; } = Task.Delay;
}

internal readonly record struct InlineThumbnailDiagnostics(int DemandCount, int ActiveRequests,
    int CacheEntries, long CacheBytes, long RequestsStarted, long NotificationsPosted);
