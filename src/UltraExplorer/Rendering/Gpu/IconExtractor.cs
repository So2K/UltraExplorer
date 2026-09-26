using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>What an icon key names.</summary>
internal enum IconKeyKind : byte
{
    /// <summary>A file type: its lower-case extension without the dot, or empty for files without one.</summary>
    Type,

    /// <summary>One file whose icon is its own (a program, a shortcut, an icon file): its full path.</summary>
    File,

    /// <summary>One of the Shell's stock icons, by a name that starts with '*' so it can never be an extension.</summary>
    Stock
}

/// <summary>Which of the extractor's queues a request waits in.</summary>
internal enum IconPriority : byte
{
    /// <summary>On screen now: taken newest first, before anything else.</summary>
    Visible,

    /// <summary>Seen in a folder that was read, or one of the common types asked for at start-up.</summary>
    Prefetch,

    /// <summary>Already shown from the disk cache; asked again only to see whether it has changed.</summary>
    Revalidate
}

/// <summary>One icon to extract.</summary>
internal readonly record struct IconRequest(string Key, IconKeyKind Kind, IconPriority Priority);

/// <summary>
/// What the Shell said about one icon: its index in the system image list
/// (-1 when it had none) and its pixels (null when they could not be had).
/// </summary>
internal readonly record struct IconFound(int SystemIndex, IconSlotData? Pixels);

/// <summary>
/// A finished request, as the atlas receives it.  <see cref="Dropped"/> means
/// the request was never worked on: the on-screen queue overflowed and its
/// oldest entry was let go, and the atlas may ask again if it still needs it.
/// </summary>
internal sealed class IconExtraction(IconRequest request, IconFound found, int threadId, bool dropped)
{
    public IconRequest Request { get; } = request;

    public int SystemIndex { get; } = found.SystemIndex;

    public IconSlotData? Pixels { get; } = found.Pixels;

    /// <summary>The managed id of the thread the work was done on.</summary>
    public int ThreadId { get; } = threadId;

    public bool Dropped { get; } = dropped;
}

/// <summary>
/// Gets icons out of the Shell for the atlas, never on the thread that asks.
///
/// Two threads by default, each a single-threaded apartment with COM started
/// and running below normal priority: most icon handlers are
/// apartment-threaded, so an STA avoids marshalling, and the Shell's own
/// guidance is that these calls belong on a background thread.  Every Shell
/// call is wrapped so that a broken icon handler costs one icon, not a worker.
/// A handler can also hang - a shortcut to a server that is offline - so when
/// every worker has been stuck on one request for several seconds while work
/// waits, another is started, up to six; the stuck ones finish or not in their
/// own time.  A timer looks once a second, so this happens even when nothing
/// new is asked for.
///
/// Three queues: what is on screen now, newest first, because by the time a
/// long queue drains the view has moved on; then what folders that were read
/// contain; then, when there is nothing else, re-checks of icons that came
/// from the disk cache.  The on-screen queue is bounded, and a request that
/// falls off its end is reported as dropped so the atlas can ask again.
///
/// Pixels: the index in the system image list first (SHGetFileInfo), which is
/// what many types share - every type with no icon of its own has the one
/// "unknown file" index - so a second type with a known index costs no pixel
/// work at all.  Then the 256-pixel jumbo image, or the extra-large one when
/// the jumbo image turns out to be a 48-pixel icon in the corner of an empty
/// canvas; and the large and small images when they are exactly the
/// hand-drawn 32 and 16 pixels.  See <see cref="IconPixels"/> for the rest.
/// </summary>
internal sealed class IconExtractor : IDisposable
{
    public const int DefaultWorkerCount = 2;

    private const int MaximumWorkerCount = 6;
    private const int VisibleCapacity = 1024;
    private const int IndexCacheLimit = 1024;

    private static readonly long StuckTicks = Stopwatch.Frequency * 4;

    private readonly object _gate = new();
    private readonly LinkedList<IconRequest> _visible = new();
    private readonly Queue<IconRequest> _prefetch = new();
    private readonly Queue<IconRequest> _revalidate = new();
    private readonly List<Worker> _workers = [];
    private readonly Action<IconExtraction> _completed;
    private readonly Func<IconRequest, IconFound> _source;

    /// <summary>
    /// Pixels already made, by system image list index, for types and stock
    /// icons only: there are a few hundred of those at most.  Files are left
    /// out because every program has an index of its own, and keeping theirs
    /// would grow without end.
    /// </summary>
    private readonly ConcurrentDictionary<int, IconSlotData> _byIndex = new();

    private readonly Timer _watchdog;
    private bool _disposed;

    /// <param name="completed">Called on a worker thread with every finished or dropped request.  Must not block.</param>
    /// <param name="workerCount">Threads to start with.</param>
    /// <param name="source">
    /// Replaces the Shell, for the checks: called on the worker threads
    /// exactly where the Shell would be.
    /// </param>
    public IconExtractor(Action<IconExtraction> completed, int workerCount = DefaultWorkerCount, Func<IconRequest, IconFound>? source = null)
    {
        _completed = completed;
        _source = source ?? ExtractFromShell;
        lock (_gate)
        {
            for (var index = 0; index < Math.Clamp(workerCount, 1, MaximumWorkerCount); index++)
            {
                StartWorker();
            }
        }

        _watchdog = new Timer(_ => Watch(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>The managed ids of the worker threads, for the checks.</summary>
    public IReadOnlyList<int> WorkerThreadIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _workers.Select(worker => worker.Thread.ManagedThreadId)];
            }
        }
    }

    /// <summary>How many requests are waiting, in all three queues.</summary>
    public int QueuedCount
    {
        get
        {
            lock (_gate)
            {
                return _visible.Count + _prefetch.Count + _revalidate.Count;
            }
        }
    }

    /// <summary>Queues <paramref name="request"/>.  Any thread; never blocks on the Shell.</summary>
    public void Enqueue(IconRequest request)
    {
        IconRequest? dropped = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            switch (request.Priority)
            {
                case IconPriority.Visible:
                    _visible.AddLast(request);
                    if (_visible.Count > VisibleCapacity)
                    {
                        dropped = _visible.First!.Value;
                        _visible.RemoveFirst();
                    }

                    break;
                case IconPriority.Prefetch:
                    _prefetch.Enqueue(request);
                    break;
                default:
                    _revalidate.Enqueue(request);
                    break;
            }

            ReplaceStuckWorkers();
            Monitor.Pulse(_gate);
        }

        if (dropped is { } lost)
        {
            Report(new IconExtraction(lost, new IconFound(-1, null), Environment.CurrentManagedThreadId, dropped: true));
        }
    }

    public void Dispose()
    {
        _watchdog.Dispose();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _visible.Clear();
            _prefetch.Clear();
            _revalidate.Clear();
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// The Shell's icon for <paramref name="request"/>, on the calling thread,
    /// which must be a single-threaded apartment - the workers are.  Never
    /// throws: whatever goes wrong comes back as no index, or no pixels.
    /// </summary>
    public IconFound ExtractFromShell(IconRequest request)
    {
        int index;
        try
        {
            StartShellImageLists();
            index = SystemIndexOf(request);
            if (index < 0)
            {
                // Once more: the Shell can still say no while another thread
                // of the process - the folder list's icon service - is making
                // something it needs.  A file that is really gone costs one
                // extra failed call.
                Thread.Sleep(1);
                index = SystemIndexOf(request);
            }
        }
        catch (Exception)
        {
            return new IconFound(-1, null);
        }

        if (index < 0)
        {
            return new IconFound(-1, null);
        }

        if (_byIndex.TryGetValue(index, out var known))
        {
            return new IconFound(index, known);
        }

        IconSlotData? pixels;
        try
        {
            pixels = PixelsOf(index);
        }
        catch (Exception)
        {
            pixels = null;
        }

        if (pixels is not null && request.Kind != IconKeyKind.File)
        {
            if (_byIndex.Count >= IndexCacheLimit)
            {
                _byIndex.Clear();
            }

            _byIndex[index] = pixels;
        }

        return new IconFound(index, pixels);
    }

    // ---- workers -----------------------------------------------------------------------

    private sealed class Worker
    {
        public required Thread Thread { get; init; }

        /// <summary>When the current request was taken (Stopwatch ticks), or zero while waiting.</summary>
        public long BusySince;
    }

    private void StartWorker()
    {
        var worker = new Worker
        {
            Thread = new Thread(Run)
            {
                IsBackground = true,
                Name = $"UltraExplorer icon atlas {_workers.Count + 1}",
                Priority = ThreadPriority.BelowNormal
            }
        };
        worker.Thread.SetApartmentState(ApartmentState.STA);
        _workers.Add(worker);
        worker.Thread.Start(worker);
    }

    /// <summary>The watchdog's round: while work waits, workers stuck on one request are replaced.</summary>
    private void Watch()
    {
        lock (_gate)
        {
            if (!_disposed && _visible.Count + _prefetch.Count + _revalidate.Count > 0)
            {
                ReplaceStuckWorkers();
            }
        }
    }

    /// <summary>
    /// Starts one more worker when every one is stuck: busy on a single
    /// request for longer than any icon should take.  Under the gate.
    /// </summary>
    private void ReplaceStuckWorkers()
    {
        if (_workers.Count >= MaximumWorkerCount)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        foreach (var worker in _workers)
        {
            var since = Volatile.Read(ref worker.BusySince);
            if (since == 0 || now - since < StuckTicks)
            {
                return;
            }
        }

        StartWorker();
    }

    private void Run(object? state)
    {
        var worker = (Worker)state!;

        // The runtime has already entered the apartment for a thread started
        // as STA; this makes sure of it and is balanced below either way.
        var comStarted = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded) >= 0;
        try
        {
            while (TryTake(worker, out var request))
            {
                IconFound found;
                try
                {
                    found = _source(request);
                }
                catch (Exception)
                {
                    found = new IconFound(-1, null);
                }
                finally
                {
                    Volatile.Write(ref worker.BusySince, 0);
                }

                Report(new IconExtraction(request, found, Environment.CurrentManagedThreadId, dropped: false));
            }
        }
        finally
        {
            if (comStarted)
            {
                CoUninitialize();
            }
        }
    }

    private bool TryTake(Worker worker, out IconRequest request)
    {
        lock (_gate)
        {
            while (true)
            {
                if (_disposed)
                {
                    request = default;
                    return false;
                }

                if (_visible.Last is { } newest)
                {
                    request = newest.Value;
                    _visible.RemoveLast();
                    break;
                }

                if (_prefetch.TryDequeue(out request) || _revalidate.TryDequeue(out request))
                {
                    break;
                }

                Monitor.Wait(_gate);
            }

            Volatile.Write(ref worker.BusySince, Stopwatch.GetTimestamp());
            return true;
        }
    }

    private void Report(IconExtraction extraction)
    {
        try
        {
            _completed(extraction);
        }
        catch (Exception)
        {
            // The atlas only queues it; nothing it does should take a worker down.
        }
    }

    // ---- the Shell ---------------------------------------------------------------------

    private static readonly object ShellStartGate = new();
    private static bool _shellStarted;

    /// <summary>
    /// The sides of the small, large and extra-large lists.  They follow the
    /// process's DPI: 16, 32 and 48 pixels when it is not DPI-aware, 24, 48
    /// and 72 in this app at 150%.  Read once, since they never change while
    /// the process runs.
    /// </summary>
    private static int _smallSize = 16;
    private static int _largeSize = 32;
    private static int _extraLargeSize = IconPixels.SmallFrameInJumbo;

    /// <summary>
    /// Makes the system image lists before any worker asks for an index.  The
    /// Shell makes them for whichever thread asks first, and when two threads
    /// ask at the same moment all but one are told there is no index at all -
    /// measured here: of four first calls made together, three failed.  So
    /// the first worker makes them under a lock, and the others wait for it.
    /// </summary>
    private static void StartShellImageLists()
    {
        if (Volatile.Read(ref _shellStarted))
        {
            return;
        }

        lock (ShellStartGate)
        {
            if (_shellStarted)
            {
                return;
            }

            try
            {
                FileIconInit(true);
                var info = default(ShFileInfo);
                SHGetFileInfo(".txt", FileAttributeNormal, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiSystemIconIndex | ShgfiUseFileAttributes);
                _smallSize = SizeOf(ShilSmall, _smallSize);
                _largeSize = SizeOf(ShilLarge, _largeSize);
                _extraLargeSize = SizeOf(ShilExtraLarge, _extraLargeSize);
                SizeOf(ShilJumbo, 256);
            }
            catch (Exception)
            {
                // Each request still asks for itself, and retries once.
            }

            Volatile.Write(ref _shellStarted, true);
        }
    }

    /// <summary>The sides of the small, large and extra-large lists, once the first extraction has read them.</summary>
    internal static (int Small, int Large, int ExtraLarge) ListSizes
        => (Volatile.Read(ref _smallSize), Volatile.Read(ref _largeSize), Volatile.Read(ref _extraLargeSize));

    /// <summary>Makes image list <paramref name="list"/> and returns the side of its images, or <paramref name="fallback"/>.</summary>
    private static unsafe int SizeOf(int list, int fallback)
    {
        var interfaceId = ImageListInterfaceId;
        if (SHGetImageList(list, ref interfaceId, out var imageList) < 0 || imageList == IntPtr.Zero)
        {
            return fallback;
        }

        try
        {
            int width = 0, height = 0;
            var getIconSize = (delegate* unmanaged[Stdcall]<IntPtr, int*, int*, int>)(*(void***)imageList)[ImageListGetIconSizeSlot];
            return getIconSize(imageList, &width, &height) >= 0 && width > 0 && width == height ? width : fallback;
        }
        finally
        {
            Marshal.Release(imageList);
        }
    }

    private static int SystemIndexOf(IconRequest request)
    {
        switch (request.Kind)
        {
            case IconKeyKind.Stock:
            {
                var stock = request.Key switch
                {
                    IconAtlas.DocumentKey => StockDocumentNoAssociation,
                    IconAtlas.ApplicationKey => StockApplication,
                    IconAtlas.FolderKey => StockFolder,
                    _ => -1
                };
                if (stock < 0)
                {
                    return -1;
                }

                var info = new StockIconInfo { Size = (uint)Marshal.SizeOf<StockIconInfo>() };
                return SHGetStockIconInfo((uint)stock, ShgsiSystemIconIndex, ref info) >= 0 ? info.SystemImageIndex : -1;
            }

            case IconKeyKind.File:
            {
                // The file itself is read: a program's own icon, a shortcut's target's.
                var info = default(ShFileInfo);
                var result = SHGetFileInfo(request.Key, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiSystemIconIndex);
                return result != IntPtr.Zero ? info.IconIndex : -1;
            }

            default:
            {
                // Only the name is looked at; nothing on disk is touched.
                var name = request.Key.Length == 0 ? "file" : "." + request.Key;
                var info = default(ShFileInfo);
                var result = SHGetFileInfo(
                    name,
                    FileAttributeNormal,
                    ref info,
                    (uint)Marshal.SizeOf<ShFileInfo>(),
                    ShgfiSystemIconIndex | ShgfiUseFileAttributes);
                return result != IntPtr.Zero ? info.IconIndex : -1;
            }
        }
    }

    /// <summary>
    /// The slot for system image <paramref name="index"/>, or null.  Every
    /// frame read on the way is given back to the pool before this returns
    /// (<see cref="RawFrame"/>); the slot is the only thing it leaves.
    /// </summary>
    internal static IconSlotData? PixelsOf(int index)
    {
        // A type with no 256-pixel frame comes back from the jumbo list as its
        // extra-large image in the corner of an empty canvas: 48 pixels in a
        // process that is not DPI-aware, 72 in this app at 150%.
        var corner = Math.Max(IconPixels.SmallFrameInJumbo, Volatile.Read(ref _extraLargeSize));
        RawFrame? image = null;
        RawFrame? frame32 = null;
        RawFrame? frame16 = null;
        try
        {
            image = Frame(ShilJumbo, index, requiredSize: 0);
            if (image is null || IconPixels.HoldsOnlyTopLeftFrame(image.Pixels, image.Width, image.Height, corner))
            {
                var larger = Frame(ShilExtraLarge, index, requiredSize: 0);
                if (larger is not null)
                {
                    image?.Dispose();
                    image = larger;
                }
                else if (image is not null && corner < image.Width)
                {
                    var cropped = RawFrame.Of(IconPixels.Crop(image.Pixels, image.Width, corner), corner, corner);
                    image.Dispose();
                    image = cropped;
                }
            }

            if (image is null)
            {
                return null;
            }

            image.Normalise();

            // The hand-drawn frames only when the lists hold them at exactly 32
            // and 16 pixels; at other DPIs the lists are scaled and level 0
            // halved is as good.
            frame32 = Volatile.Read(ref _largeSize) == 32 ? Frame(ShilLarge, index, requiredSize: 32) : null;
            frame32?.Normalise();
            frame16 = Volatile.Read(ref _smallSize) == 16 ? Frame(ShilSmall, index, requiredSize: 16) : null;
            frame16?.Normalise();
            return IconPixels.BuildSlot(
                image.Pixels,
                image.Width,
                image.Height,
                frame32 is null ? default : frame32.Pixels,
                frame16 is null ? default : frame16.Pixels);
        }
        finally
        {
            image?.Dispose();
            frame32?.Dispose();
            frame16?.Dispose();
        }
    }

    /// <summary>
    /// Image <paramref name="index"/> of one of the system image lists, or null.
    /// With <paramref name="requiredSize"/> the list is only used when its
    /// images are exactly that size, and nothing is extracted otherwise.
    /// </summary>
    private static unsafe RawFrame? Frame(int list, int index, int requiredSize)
    {
        var interfaceId = ImageListInterfaceId;
        if (SHGetImageList(list, ref interfaceId, out var imageList) < 0 || imageList == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var table = *(void***)imageList;
            if (requiredSize > 0)
            {
                int width = 0, height = 0;
                var getIconSize = (delegate* unmanaged[Stdcall]<IntPtr, int*, int*, int>)table[ImageListGetIconSizeSlot];
                if (getIconSize(imageList, &width, &height) < 0 || width != requiredSize || height != requiredSize)
                {
                    return null;
                }
            }

            var icon = IntPtr.Zero;
            var getIcon = (delegate* unmanaged[Stdcall]<IntPtr, int, uint, IntPtr*, int>)table[ImageListGetIconSlot];
            if (getIcon(imageList, index, IldTransparent, &icon) < 0 || icon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return ReadIcon(icon);
            }
            finally
            {
                DestroyIcon(icon);
            }
        }
        finally
        {
            Marshal.Release(imageList);
        }
    }

    /// <summary>An icon's colour bitmap as top-down 32-bit BGRA, and its mask when the colour has no alpha.</summary>
    private static unsafe RawFrame? ReadIcon(IntPtr icon)
    {
        if (!GetIconInfo(icon, out var info))
        {
            return null;
        }

        try
        {
            if (info.ColourBitmap == IntPtr.Zero
                || GetObject(info.ColourBitmap, Marshal.SizeOf<BitmapHeader>(), out var bitmap) == 0)
            {
                return null;
            }

            var width = bitmap.Width;
            var height = Math.Abs(bitmap.Height);
            if (width <= 0 || height <= 0 || width > 1024 || height > 1024)
            {
                return null;
            }

            var screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero)
            {
                return null;
            }

            var frame = RawFrame.Rent(width, height);
            var kept = false;
            try
            {
                if (!ReadBitmap(screen, info.ColourBitmap, width, height, frame.PixelArray))
                {
                    return null;
                }

                if (info.MaskBitmap != IntPtr.Zero && !HasAlpha(frame.Pixels) && !ReadBitmap(screen, info.MaskBitmap, width, height, frame.RentMask()))
                {
                    frame.DropMask();
                }

                kept = true;
                return frame;
            }
            finally
            {
                _ = ReleaseDC(IntPtr.Zero, screen);
                if (!kept)
                {
                    frame.Dispose();
                }
            }
        }
        finally
        {
            if (info.ColourBitmap != IntPtr.Zero)
            {
                DeleteObject(info.ColourBitmap);
            }

            if (info.MaskBitmap != IntPtr.Zero)
            {
                DeleteObject(info.MaskBitmap);
            }
        }
    }

    private static unsafe bool ReadBitmap(IntPtr deviceContext, IntPtr bitmap, int width, int height, byte[] target)
    {
        var header = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)sizeof(BitmapInfoHeader),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = 0
            }
        };

        fixed (byte* bits = target)
        {
            return GetDIBits(deviceContext, bitmap, 0, (uint)height, bits, &header, DibRgbColours) == height;
        }
    }

    private static bool HasAlpha(ReadOnlySpan<byte> bgra)
    {
        for (var index = 3; index < bgra.Length; index += 4)
        {
            if (bgra[index] != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Pixels straight from an icon, before anything decides what their alpha means.</summary>
    /// <summary>
    /// One frame of an icon as read from the Shell: its pixels and, for an
    /// icon without alpha, its mask - in arrays rented from the shared pool
    /// and given back by <see cref="Dispose"/>.  A jumbo frame is 256 KB, past
    /// the large object heap's threshold, and a new array for every one - and
    /// a second for a mask - made a burst of extractions (the first zooms of
    /// a session, a folder of programs or shortcuts, each with an icon of its
    /// own) tens of megabytes of large objects, each counting towards a full
    /// collection.  Rented and returned on the same worker thread, the same
    /// arrays serve every icon.
    /// </summary>
    private sealed class RawFrame : IDisposable
    {
        private byte[]? _pixels;
        private byte[]? _mask;
        private readonly bool _rented;

        private RawFrame(byte[] pixels, int width, int height, bool rented)
        {
            _pixels = pixels;
            Width = width;
            Height = height;
            _rented = rented;
        }

        public int Width { get; }

        public int Height { get; }

        /// <summary>The frame's bytes: 32-bit BGRA, top down, exactly <see cref="Width"/> x <see cref="Height"/>.</summary>
        public Span<byte> Pixels => _pixels.AsSpan(0, Width * Height * 4);

        /// <summary>The array under <see cref="Pixels"/>, which may be longer, for reading a bitmap into.</summary>
        public byte[] PixelArray => _pixels!;

        public static RawFrame Rent(int width, int height) => new(ArrayPool<byte>.Shared.Rent(width * height * 4), width, height, rented: true);

        /// <summary>A frame over an array of its own, not the pool's.</summary>
        public static RawFrame Of(byte[] pixels, int width, int height) => new(pixels, width, height, rented: false);

        /// <summary>An array for the mask, the frame's size or longer, kept and given back with the frame.</summary>
        public byte[] RentMask() => _mask ??= ArrayPool<byte>.Shared.Rent(Width * Height * 4);

        public void DropMask()
        {
            if (_mask is not null)
            {
                ArrayPool<byte>.Shared.Return(_mask);
                _mask = null;
            }
        }

        public void Normalise() => IconPixels.Normalise(Pixels, _mask is null ? default : _mask.AsSpan(0, Width * Height * 4));

        public void Dispose()
        {
            DropMask();
            if (_rented && _pixels is not null)
            {
                ArrayPool<byte>.Shared.Return(_pixels);
            }

            _pixels = null;
        }
    }

    // ---- interop -----------------------------------------------------------------------

    private const uint ShgfiSystemIconIndex = 0x000004000;
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private const uint FileAttributeNormal = 0x80;
    private const uint ShgsiSystemIconIndex = 0x000004000;
    private const int StockDocumentNoAssociation = 0;
    private const int StockApplication = 2;
    private const int StockFolder = 3;
    private const int ShilLarge = 0;
    private const int ShilSmall = 1;
    private const int ShilExtraLarge = 2;
    private const int ShilJumbo = 4;
    private const uint IldTransparent = 0x1;
    private const uint DibRgbColours = 0;
    private const uint CoInitApartmentThreaded = 0x2;

    /// <summary>IImageList's vtable: IUnknown's three, then Add, ReplaceIcon, SetOverlayImage, Replace, AddMasked, Draw, Remove, GetIcon.</summary>
    private const int ImageListGetIconSlot = 10;

    /// <summary>... GetImageInfo, Copy, Merge, Clone, GetImageRect, GetIconSize.</summary>
    private const int ImageListGetIconSizeSlot = 16;

    private static readonly Guid ImageListInterfaceId = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StockIconInfo
    {
        public uint Size;
        public IntPtr IconHandle;
        public int SystemImageIndex;
        public int IconIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr MaskBitmap;
        public IntPtr ColourBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColoursUsed;
        public uint ColoursImportant;
    }

    /// <summary>The header with room for a full colour table after it, which GetDIBits may write for some sources.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public fixed uint Colours[256];
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, ref ShFileInfo fileInfo, uint fileInfoSize, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetStockIconInfo(uint stockIcon, uint flags, ref StockIconInfo info);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int imageList, ref Guid interfaceId, out IntPtr list);

    /// <summary>Makes (or restores from the Shell's cache) the system image list.  Exported by ordinal only.</summary>
    [DllImport("shell32.dll", EntryPoint = "#660")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FileIconInit([MarshalAs(UnmanagedType.Bool)] bool restoreCache);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(IntPtr handle, int size, out BitmapHeader bitmap);

    [DllImport("gdi32.dll")]
    private static extern unsafe int GetDIBits(IntPtr deviceContext, IntPtr bitmap, uint startScan, uint scanLines, void* bits, BitmapInfo* info, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
