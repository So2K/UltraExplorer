using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UltraExplorer.Services;

/// <summary>A content thumbnail which is safe to display from any thread.</summary>
internal sealed record ThumbnailResult(BitmapSource Image, int? OriginalWidth = null, int? OriginalHeight = null);

/// <summary>
/// Hover-only thumbnail extraction. All metadata, image decoding and Shell calls
/// run on at most two background STA threads. A slow drive or thumbnail handler
/// can occupy those threads, but never the dispatcher or an unbounded sequence
/// of replacement threads. Cancelling a waiter and disposing do not wait for it.
/// </summary>
internal sealed class FileThumbnailService : IDisposable
{
    internal const int MaximumDimension = 512;
    private readonly object _gate = new();
    private readonly Dictionary<string, Work> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Work> _queue = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly FileThumbnailOptions _options;
    private readonly Func<string, ThumbnailFileStamp?> _metadata;
    private readonly Func<string, ThumbnailResult?> _extract;
    private int _workers, _active;
    private long _cacheBytes, _extractions, _metadataReads;
    private bool _disposed;
    [ThreadStatic] private static bool _comInitialized;

    public FileThumbnailService() : this(new FileThumbnailOptions()) { }

    internal FileThumbnailService(FileThumbnailOptions options)
    {
        _options = options;
        _metadata = options.MetadataReader ?? ReadMetadata;
        _extract = options.ThumbnailReader ?? ExtractThumbnail;
    }

    /// <summary>
    /// Requests only this file, without reading it on the calling thread. Cache
    /// hits also validate length/time/attributes on a worker before being shown.
    /// Cancelling one caller does not cancel another caller of the same file.
    /// </summary>
    public Task<ThumbnailResult?> GetAsync(string path, CancellationToken token = default)
    {
        if (token.IsCancellationRequested) return Task.FromCanceled<ThumbnailResult?>(token);
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult<ThumbnailResult?>(null);
        try { path = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return Task.FromResult<ThumbnailResult?>(null); }

        var waiter = new Waiter(token);
        Work work;
        List<Waiter>? displaced = null;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult<ThumbnailResult?>(null);
            if (!_pending.TryGetValue(path, out work!))
            {
                work = new Work(path);
                _pending.Add(path, work);
                work.Node = _queue.AddLast(work);
            }
            else if (work.Node is not null)
            {
                // Another visible request promotes this file over older hovers.
                _queue.Remove(work.Node);
                work.Node = _queue.AddLast(work);
            }

            if (work.Waiters.Count >= 16)
            {
                displaced = [work.Waiters[0]];
                work.Waiters.RemoveAt(0);
                displaced[0].Finished = true;
            }
            work.Waiters.Add(waiter);
            while (_queue.Count > Math.Clamp(_options.MaximumPending, 1, 32))
            {
                var oldest = _queue.First!.Value;
                _queue.RemoveFirst();
                oldest.Node = null;
                _pending.Remove(oldest.Path);
                (displaced ??= []).AddRange(TakeWaiters(oldest));
            }
            StartWorkers();
            Monitor.PulseAll(_gate);
        }
        Complete(displaced, null);

        if (token.CanBeCanceled)
        {
            var registration = token.Register(() => Cancel(work, waiter));
            lock (_gate)
            {
                waiter.SetRegistration(registration);
                if (waiter.Finished) registration.Unregister();
            }
        }
        return waiter.Completion.Task;
    }

    private void StartWorkers()
    {
        var count = Math.Clamp(_options.WorkerCount, 1, 2);
        while (_workers < count)
        {
            var thread = new Thread(WorkerLoop) { IsBackground = true, Name = $"File thumbnail {_workers + 1}" };
            thread.SetApartmentState(ApartmentState.STA);
            _workers++;
            thread.Start();
        }
    }

    private void Cancel(Work work, Waiter waiter)
    {
        lock (_gate)
        {
            if (waiter.Finished) return;
            waiter.Finished = true;
            work.Waiters.Remove(waiter);
            if (work.Waiters.Count == 0 && work.Node is not null)
            {
                _queue.Remove(work.Node);
                work.Node = null;
                _pending.Remove(work.Path);
            }
        }
        waiter.Completion.TrySetCanceled(waiter.Token);
        waiter.Unregister();
    }

    private void WorkerLoop()
    {
        var initialized = CoInitializeEx(IntPtr.Zero, 2 | 4) >= 0; // STA, DISABLE_OLE1DDE
        _comInitialized = initialized;
        try
        {
            while (true)
            {
                Work work;
                lock (_gate)
                {
                    while (!_disposed && _queue.Count == 0) Monitor.Wait(_gate);
                    if (_disposed) return;
                    work = _queue.Last!.Value;
                    _queue.RemoveLast();
                    work.Node = null;
                    _active++;
                }

                ThumbnailResult? result = null;
                try { result = Execute(work); }
                catch (Exception) { /* Missing, locked, corrupt and failing handlers are an absent preview. */ }

                List<Waiter> waiters;
                lock (_gate)
                {
                    _active--;
                    if (_pending.TryGetValue(work.Path, out var pending) && ReferenceEquals(pending, work))
                        _pending.Remove(work.Path);
                    waiters = TakeWaiters(work);
                }
                Complete(waiters, result);
            }
        }
        finally
        {
            _comInitialized = false;
            if (initialized) CoUninitialize();
        }
    }

    private ThumbnailResult? Execute(Work work)
    {
        if (!StillWanted(work)) return null;
        var stamp = Metadata(work.Path);
        lock (_gate)
        {
            if (_disposed || work.Waiters.Count == 0) return null;
            if (_cache.TryGetValue(work.Path, out var existing))
            {
                if (existing.Value.Stamp == stamp
                    && (existing.Value.Result is not null || _options.TickCount() < existing.Value.Expires))
                {
                    _lru.Remove(existing);
                    _lru.AddLast(existing);
                    return existing.Value.Result;
                }
                RemoveCache(existing);
            }
        }

        if (stamp is not { CanReadContent: true })
        {
            Remember(work, stamp, null);
            return null;
        }
        if (!StillWanted(work)) return null;
        Interlocked.Increment(ref _extractions);
        ThumbnailResult? result;
        try { result = Prepare(_extract(work.Path)); }
        catch (Exception) { result = null; }
        if (!StillWanted(work)) return null;

        // Do not publish/cache an image of a file replaced during decoding.
        if (Metadata(work.Path) != stamp) return null;
        Remember(work, stamp, result);
        return result;
    }

    private ThumbnailFileStamp? Metadata(string path)
    {
        Interlocked.Increment(ref _metadataReads);
        try { return _metadata(path); }
        catch (Exception) { return null; }
    }

    private bool StillWanted(Work work)
    {
        lock (_gate) return !_disposed && work.Waiters.Count != 0;
    }

    private void Remember(Work work, ThumbnailFileStamp? stamp, ThumbnailResult? result)
    {
        var bytes = result is null ? 0 : (long)result.Image.PixelWidth * result.Image.PixelHeight * 4;
        lock (_gate)
        {
            if (_disposed || work.Waiters.Count == 0) return;
            if (_cache.TryGetValue(work.Path, out var old)) RemoveCache(old);
            var limit = Math.Clamp(_options.MaximumCacheBytes, 4, 32L * 1024 * 1024);
            if (bytes > limit) return;
            var entry = new CacheEntry(work.Path, stamp, result, bytes,
                _options.TickCount() + Math.Clamp(_options.NegativeCacheMilliseconds, 1, 30_000));
            _cache.Add(work.Path, _lru.AddLast(entry));
            _cacheBytes += bytes;
            while (_cacheBytes > limit || _cache.Count > Math.Clamp(_options.MaximumCacheEntries, 1, 96))
                RemoveCache(_lru.First!);
        }
    }

    private void RemoveCache(LinkedListNode<CacheEntry> entry)
    {
        _cache.Remove(entry.Value.Path);
        _cacheBytes -= entry.Value.Bytes;
        _lru.Remove(entry);
    }

    private static List<Waiter> TakeWaiters(Work work)
    {
        var waiters = new List<Waiter>(work.Waiters);
        work.Waiters.Clear();
        foreach (var waiter in waiters) waiter.Finished = true;
        return waiters;
    }

    private static void Complete(List<Waiter>? waiters, ThumbnailResult? result)
    {
        if (waiters is null) return;
        foreach (var waiter in waiters)
        {
            waiter.Unregister();
            if (waiter.Token.IsCancellationRequested) waiter.Completion.TrySetCanceled(waiter.Token);
            else waiter.Completion.TrySetResult(result);
        }
    }

    public void Dispose()
    {
        List<Waiter> waiters = [];
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var work in _pending.Values) waiters.AddRange(TakeWaiters(work));
            _pending.Clear();
            _queue.Clear();
            _cache.Clear();
            _lru.Clear();
            _cacheBytes = 0;
            Monitor.PulseAll(_gate);
        }
        Complete(waiters, null);
        // A Shell call cannot safely be interrupted; the background thread
        // releases its own COM/bitmap resources when that call returns.
    }

    internal FileThumbnailDiagnostics Diagnostics
    {
        get
        {
            lock (_gate) return new(_queue.Count, _active, _workers, _cache.Count, _cacheBytes,
                Interlocked.Read(ref _extractions), Interlocked.Read(ref _metadataReads));
        }
    }

    private static ThumbnailFileStamp? ReadMetadata(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        return file.Exists ? new ThumbnailFileStamp(file.Length, file.LastWriteTimeUtc.Ticks, (uint)file.Attributes) : null;
    }

    private static ThumbnailResult? ExtractThumbnail(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".ico" or ".wdp" or ".jxr")
        {
            try
            {
                // Read sharing permits images currently exported by another
                // app; OnLoad detaches the resulting thumbnail from its file.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.SequentialScan);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
                var frame = decoder.Frames[0];
                var width = frame.PixelWidth;
                var height = frame.PixelHeight;
                var orientation = ReadOrientation(frame.Metadata as BitmapMetadata);
                if (width <= 0 || height <= 0 || width > 100_000 || height > 100_000 || (long)width * height > 200_000_000)
                    return null;
                stream.Position = 0;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                // StreamSource has no URI cache. IgnoreImageCache is a URI
                // option and WPF attempts to remove a null URI when used here.
                if (Math.Max(width, height) > MaximumDimension)
                {
                    if (width >= height) bitmap.DecodePixelWidth = MaximumDimension;
                    else bitmap.DecodePixelHeight = MaximumDimension;
                }
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                var oriented = OrientImage(bitmap, orientation);
                return orientation is >= 5 and <= 8
                    ? new ThumbnailResult(oriented, height, width)
                    : new ThumbnailResult(oriented, width, height);
            }
            catch (Exception) { /* Other installed Windows codecs may support it through the Shell. */ }
        }
        return ExtractShellThumbnail(path);
    }

    private static ThumbnailResult? ExtractShellThumbnail(string path)
    {
        if (!_comInitialized) return null;
        IShellItem? item = null;
        IThumbnailCache? cache = null;
        ISharedBitmap? shared = null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item) < 0 || item is null) return null;
            var classId = new Guid("50ef4544-ac9f-4a8e-b21b-8a26180db13f");
            iid = typeof(IThumbnailCache).GUID;
            if (CoCreateInstance(ref classId, IntPtr.Zero, 1, ref iid, out cache) < 0 || cache is null) return null;
            // The thumbnail cache never substitutes a file type icon.
            // WTS_REQUIRESURROGATE also refuses legacy handlers which opt out
            // of process isolation, so their native code cannot fault the app.
            if (cache.GetThumbnail(item, MaximumDimension, 0x800, out shared, out _, out _) < 0 || shared is null)
                return null;
            if (shared.GetFormat(out var alphaType) < 0 || shared.GetSize(out var size) < 0
                || size.Width <= 0 || size.Height <= 0 || size.Width > 4096 || size.Height > 4096
                || (long)size.Width * size.Height > 4_194_304) return null;
            // Detach transfers ownership (copying a shared cache bitmap).
            // GetSharedBitmap, in contrast, returns a borrowed handle.
            if (shared.Detach(out bitmap) < 0 || bitmap == IntPtr.Zero) return null;
            BitmapSource image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero,
                Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            // WTSAT_RGB has unused alpha bytes, commonly all zero. Ignoring
            // those bytes through Bgr32 keeps an opaque thumbnail visible.
            if (alphaType == 1) image = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
            image.Freeze();
            return new ThumbnailResult(image);
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            Release(shared);
            Release(cache);
            Release(item);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    private static int ReadOrientation(BitmapMetadata? metadata)
    {
        if (metadata is null) return 1;
        foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            try
            {
                var value = metadata.GetQuery(query);
                if (value is ushort orientation && orientation is >= 1 and <= 8) return orientation;
            }
            catch (Exception) { /* A codec may not expose this metadata branch. */ }
        }
        return 1;
    }

    internal static BitmapSource OrientImage(BitmapSource image, int orientation)
    {
        // EXIF orientation maps source coordinates to the displayed image;
        // these orthogonal transforms cover both rotations and reflections.
        var matrix = orientation switch
        {
            2 => new Matrix(-1, 0, 0, 1, 0, 0),
            3 => new Matrix(-1, 0, 0, -1, 0, 0),
            4 => new Matrix(1, 0, 0, -1, 0, 0),
            5 => new Matrix(0, 1, 1, 0, 0, 0),
            6 => new Matrix(0, 1, -1, 0, 0, 0),
            7 => new Matrix(0, -1, -1, 0, 0, 0),
            8 => new Matrix(0, -1, 1, 0, 0, 0),
            _ => Matrix.Identity
        };
        if (matrix.IsIdentity) return image;
        var transformed = new TransformedBitmap(image, new MatrixTransform(matrix));
        transformed.Freeze();
        return transformed;
    }

    private static ThumbnailResult? Prepare(ThumbnailResult? result)
    {
        if (result is null || result.Image.PixelWidth <= 0 || result.Image.PixelHeight <= 0) return null;
        BitmapSource image = result.Image;
        var longest = Math.Max(image.PixelWidth, image.PixelHeight);
        if (longest > MaximumDimension)
        {
            var scale = (double)MaximumDimension / longest;
            image = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        }
        // Normalize to four bytes/pixel so accounting also covers high bit
        // depth WIC images instead of underestimating their cache footprint.
        if (image.Format != PixelFormats.Bgra32 && image.Format != PixelFormats.Pbgra32)
            image = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        // Materialize the bounded pixels. A TransformedBitmap otherwise keeps
        // its larger Shell source alive, and FormatConvertedBitmap keeps its
        // high bit depth source alive, defeating byte accounting in the LRU.
        var stride = checked(image.PixelWidth * 4);
        var pixels = new byte[checked(stride * image.PixelHeight)];
        image.CopyPixels(pixels, stride, 0);
        var detached = BitmapSource.Create(image.PixelWidth, image.PixelHeight, 96, 96,
            image.Format, null, pixels, stride);
        detached.Freeze();
        return result with { Image = detached };
    }

    private sealed class Work(string path)
    {
        public string Path { get; } = path;
        public LinkedListNode<Work>? Node;
        public List<Waiter> Waiters { get; } = [];
    }

    private sealed class Waiter(CancellationToken token)
    {
        private readonly object _registrationGate = new();
        private CancellationTokenRegistration _registration;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<ThumbnailResult?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Finished;

        public void SetRegistration(CancellationTokenRegistration registration)
        {
            lock (_registrationGate) _registration = registration;
        }

        public void Unregister()
        {
            CancellationTokenRegistration registration;
            lock (_registrationGate) registration = _registration;
            registration.Unregister();
        }
    }

    private sealed record CacheEntry(string Path, ThumbnailFileStamp? Stamp, ThumbnailResult? Result, long Bytes, long Expires);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize(int width, int height)
    {
        public readonly int Width = width;
        public readonly int Height = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeThumbnailId { public uint A, B, C, D; }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint kind, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("f676c15d-596a-4ce2-8234-33996f445db1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IThumbnailCache
    {
        [PreserveSig] int GetThumbnail(IShellItem item, uint size, uint flags,
            out ISharedBitmap? bitmap, out uint cacheFlags, out NativeThumbnailId id);
        [PreserveSig] int GetThumbnailByID(NativeThumbnailId id, uint size,
            out ISharedBitmap? bitmap, out uint cacheFlags);
    }

    [ComImport, Guid("091162a4-bc96-411f-aae8-c5122cd03363"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISharedBitmap
    {
        [PreserveSig] int GetSharedBitmap(out IntPtr bitmap);
        [PreserveSig] int GetSize(out NativeSize size);
        [PreserveSig] int GetFormat(out uint format);
        [PreserveSig] int InitializeBitmap(IntPtr bitmap, uint format);
        [PreserveSig] int Detach(out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);
    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoCreateInstance(ref Guid classId, IntPtr outer, uint context, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IThumbnailCache? cache);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr bitmap);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}

internal readonly record struct ThumbnailFileStamp(long Length, long LastWriteUtcTicks, uint Attributes)
{
    // FileAttributes lacks the recall flags on some frameworks. Never hydrate
    // a cloud placeholder merely because the pointer passed over its tile.
    public bool CanReadContent => (Attributes & (0x10u | 0x400u | 0x1000u | 0x40000u | 0x400000u)) == 0;
}

internal sealed class FileThumbnailOptions
{
    public int WorkerCount { get; init; } = 2;
    public int MaximumPending { get; init; } = 32;
    public int MaximumCacheEntries { get; init; } = 96;
    public long MaximumCacheBytes { get; init; } = 32L * 1024 * 1024;
    public int NegativeCacheMilliseconds { get; init; } = 5_000;
    public Func<long> TickCount { get; init; } = () => Environment.TickCount64;
    public Func<string, ThumbnailFileStamp?>? MetadataReader { get; init; }
    public Func<string, ThumbnailResult?>? ThumbnailReader { get; init; }
}

internal readonly record struct FileThumbnailDiagnostics(int Queued, int ActiveWorkers, int WorkerCount,
    int CacheEntries, long CacheBytes, long Extractions, long MetadataReads);
