using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UltraExplorer.Infrastructure;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// Every glyph the canvas draws, as a signed distance field in one of three
/// sizes, packed onto 2048 x 2048 one-byte pages - the CPU side of the glyph
/// atlas the GPU samples.
///
/// Why three sizes (<see cref="Tiers"/>): a distance field drawn much
/// smaller than it was made loses thin strokes, and one drawn with too few
/// screen pixels across its range turns soft.  16, 32 and 64 pixels per em,
/// with fields reaching 3, 4 and 8 texels, keep both in bounds for text from
/// about 7 to 60 device pixels - 7.5 DIP names at 100 % to 30 DIP titles at
/// 200 % - picked by the size the text is drawn at (<see cref="TierFor"/>).
///
/// A glyph is found by (face, glyph index, tier) in a flat table per face
/// and tier, indexed by glyph index: one array read, no hashing, no lock.
/// A glyph not there yet is queued for the atlas's background thread and
/// skipped this frame; <see cref="GlyphsArrived"/> fires when it is placed,
/// and the canvas draws again.  Start-up rasterises the likely glyphs in
/// parallel (<see cref="WarmUp"/>) and keeps them in a file keyed by the
/// fonts, so from the second run the atlas is read, not made.
///
/// The pages stay in memory after they are uploaded.  A GPU copy
/// (<see cref="GlyphAtlasTexture"/>) is made from them for each graphics
/// card, and again after a device is lost, and follows later glyphs through
/// the append-only log of placed rectangles.
///
/// Threads: lookups and <see cref="Emit{TSink}"/> from the drawing thread
/// without locks; placing takes a lock, from the atlas's worker or the
/// warm-up's.  A published entry never changes.
/// </summary>
internal sealed unsafe class GlyphAtlas : IDisposable
{
    public const int PageSize = 2048;
    public const int InitialPages = 2;

    /// <summary>Pages the atlas may grow to (4 MB each); a glyph that finds no room past this is not drawn.</summary>
    public const int MaximumPages = 16;

    /// <summary>The cache file format; bumped whenever what a glyph's texels mean changes.</summary>
    public const int FormatVersion = 1;

    public const int TierCount = 3;

    /// <summary>What decides whether cached glyphs can be reused, folded into the font hash that names the cache file.</summary>
    public const string FormatDescription = "glyphs-v1;page=2048;tiers=16/3,32/4,64/8;sdf=fh-edt-exact-outline;raster=naturalsymmetric-nogridfit-grayscale";

    private const int Unknown = 0;
    private const int Pending = -1;
    private const int Unavailable = -2;
    private const int ChunkShift = 10;
    private const int ChunkSize = 1 << ChunkShift;
    private const int ChunkCount = 1024;

    private static readonly GlyphTier[] TierTable = [new(16, 3), new(32, 4), new(64, 8)];

    private readonly FaceRegistry _faces;
    private readonly object _gate = new();
    private readonly nint[] _pages = new nint[MaximumPages];
    private readonly ShelfPacker?[] _packers = new ShelfPacker?[MaximumPages];
    private readonly int[]?[] _tables = new int[]?[FaceRegistry.Capacity * TierCount];
    private readonly GlyphEntry[]?[] _entries = new GlyphEntry[]?[ChunkCount];
    private readonly GlyphKey[]?[] _keys = new GlyphKey[]?[ChunkCount];
    private readonly List<GlyphPlacement> _log = [];
    private readonly BlockingCollection<GlyphKey> _requests = new(new ConcurrentQueue<GlyphKey>());
    private Thread? _worker;
    private int _pageCount;
    private int _entryCount;
    private int _pendingCount;
    private int _disposed;

    public GlyphAtlas(FaceRegistry faces)
    {
        _faces = faces;
        lock (_gate)
        {
            for (var page = 0; page < InitialPages; page++)
            {
                AddPage();
            }
        }
    }

    /// <summary>The three sizes, smallest first.</summary>
    public static ReadOnlySpan<GlyphTier> Tiers => TierTable;

    /// <summary>
    /// Where the atlas keeps its glyphs between runs: the state folder's
    /// cache, named by the fonts' hash - glyphs-v1-&lt;hash&gt;.bin.
    /// </summary>
    public static string DefaultCachePath(FaceRegistry faces) =>
        AppPaths.State(Path.Combine("cache", $"glyphs-v{FormatVersion}-{faces.FontHash}.bin"));

    /// <summary>
    /// Raised on a background thread after glyphs that were missing have been
    /// placed and the queue is empty: the labels that skipped them can be
    /// drawn again.
    /// </summary>
    public event Action? GlyphsArrived;

    public FaceRegistry Faces => _faces;

    public int PageCount => Volatile.Read(ref _pageCount);

    /// <summary>Glyphs placed (empty ones included).</summary>
    public int EntryCount => Volatile.Read(ref _entryCount);

    /// <summary>Glyphs queued and not placed yet.</summary>
    public int PendingCount => Volatile.Read(ref _pendingCount);

    /// <summary>
    /// The tier for text drawn at <paramref name="fontPx"/> device pixels:
    /// below 12 the 16 px tier, up to 28 the 32 px tier, above that 64.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int TierFor(double fontPx) => fontPx < 12 ? 0 : fontPx <= 28 ? 1 : 2;

    /// <summary>
    /// The threshold bias for text of this size: nothing from 14 px up,
    /// rising to 0.1 at 9 px and below, a little stem darkening where a
    /// distance field without hinting would otherwise read thin.  A starting
    /// point for the A/B tuning against the WPF text, not a measured value.
    /// </summary>
    public static float BiasFor(double fontPx) => (float)Math.Clamp((14 - fontPx) / 50, 0, 0.1);

    /// <summary>
    /// The entry for a glyph once the atlas has settled it: placed, or known
    /// to have nothing to draw (no ink, too big, no room) - then the entry is
    /// empty.  A glyph not yet asked for is queued; until it is done this
    /// reports false.
    /// </summary>
    public bool TryGet(byte face, ushort glyph, int tier, out GlyphEntry entry)
    {
        var slot = SlotOf(face, glyph, tier);
        if (slot > 0)
        {
            entry = EntryAt(slot - 1);
            return true;
        }

        entry = default;
        if (slot == Unknown)
        {
            Request(face, glyph, tier);
        }

        return slot == Unavailable;
    }

    /// <summary>
    /// Appends the quads of <paramref name="text"/> cut at
    /// <paramref name="cut"/> to <paramref name="sink"/>: the pen starts at
    /// <paramref name="originX"/> on the baseline <paramref name="baselineY"/>
    /// (device pixels), and em units become <paramref name="fontPx"/> pixels.
    /// With <paramref name="snap"/> the origin and baseline are rounded to
    /// whole pixels.  The canvas's label target passes false and rounds only
    /// the baseline itself, at rest, keeping WPF's sub-pixel placement along
    /// the line (see <see cref="GpuLabelTarget"/>).
    ///
    /// A trimmed text that cuts in logical order (<see cref="ShapedText.TrimsLogically"/>:
    /// it holds right-to-left text) keeps the glyphs of the clusters before
    /// the cut's first character left out, wherever they sit in the line,
    /// packed left to right in their visual order - the logical start of the
    /// name reordered as the bidi algorithm orders it - with the ellipsis
    /// after them, at the end of the left-to-right line, where WPF puts it.
    ///
    /// Glyphs not in the atlas yet are queued and left out; the count of
    /// them is returned, so the caller knows the label is not complete.
    /// Allocates nothing when every glyph is present.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int Emit<TSink>(ref TSink sink, ShapedText text, in TextCut cut, double originX, double baselineY, double fontPx, uint colour, float bias, bool snap)
        where TSink : struct, IGlyphSink
    {
        if (snap)
        {
            originX = Math.Round(originX);
            baselineY = Math.Round(baselineY);
        }

        var tier = TierFor(fontPx);
        var tierInfo = TierTable[tier];
        var pxRangePerPx = 2f * tierInfo.Spread / tierInfo.Em;
        var missing = 0;

        var glyphs = text.Glyphs;
        var faces = text.Faces;
        var prefix = text.PrefixAdvance;
        var offsetX = text.OffsetX;
        var offsetY = text.OffsetY;
        var scales = text.Scales;
        var logical = cut.Ellipsis && text.TrimsLogically;
        var characters = text.GlyphCharacter;
        var visible = logical ? glyphs.Length : Math.Min(cut.Visible, glyphs.Length);
        var packed = prefix.Length > 0 ? prefix[0] : 0f;

        GlyphQuad quad = default;
        quad.Colour = colour;
        quad.Bias = bias;

        for (var index = 0; index < visible; index++)
        {
            float pen;
            if (logical)
            {
                if (characters[index] >= cut.Visible)
                {
                    continue;
                }

                pen = packed;
                packed += Math.Max(0, prefix[index + 1] - prefix[index]);
            }
            else
            {
                pen = prefix[index];
            }

            var face = faces[index];
            var glyph = glyphs[index];
            var slot = SlotOf(face, glyph, tier);
            if (slot <= 0)
            {
                if (slot == Unknown)
                {
                    Request(face, glyph, tier);
                    missing++;
                }
                else if (slot == Pending)
                {
                    missing++;
                }

                continue;
            }

            ref readonly var entry = ref EntryRef(slot - 1);
            if (entry.IsEmpty)
            {
                continue;
            }

            var size = scales is null ? fontPx : fontPx * scales[index];
            var penX = originX + (pen + (offsetX is null ? 0 : offsetX[index])) * fontPx;
            var penY = baselineY - (offsetY is null ? 0 : offsetY[index]) * fontPx;
            quad.Rect = new Vector4(
                (float)(penX + entry.Left * size),
                (float)(penY + entry.Top * size),
                (float)(penX + entry.Right * size),
                (float)(penY + entry.Bottom * size));
            quad.UV0 = entry.UV0;
            quad.UV1 = entry.UV1;
            quad.PageTier = entry.Page | (uint)tier << 8;
            quad.PxRange = (float)(pxRangePerPx * size);
            sink.Add(in quad);
        }

        if (cut.Ellipsis)
        {
            var info = _faces[text.Face];
            if (info.EllipsisGlyph != 0)
            {
                var slot = SlotOf(text.Face, info.EllipsisGlyph, tier);
                if (slot > 0)
                {
                    ref readonly var entry = ref EntryRef(slot - 1);
                    if (!entry.IsEmpty)
                    {
                        var penX = originX + (logical ? packed : prefix[Math.Min(cut.Visible, prefix.Length - 1)]) * fontPx;
                        quad.Rect = new Vector4(
                            (float)(penX + entry.Left * fontPx),
                            (float)(baselineY + entry.Top * fontPx),
                            (float)(penX + entry.Right * fontPx),
                            (float)(baselineY + entry.Bottom * fontPx));
                        quad.UV0 = entry.UV0;
                        quad.UV1 = entry.UV1;
                        quad.PageTier = entry.Page | (uint)tier << 8;
                        quad.PxRange = (float)(pxRangePerPx * fontPx);
                        sink.Add(in quad);
                    }
                }
                else
                {
                    if (slot == Unknown)
                    {
                        Request(text.Face, info.EllipsisGlyph, tier);
                    }

                    missing += slot == Unavailable ? 0 : 1;
                }
            }
        }

        return missing;
    }

    /// <summary>
    /// Makes the start-up glyphs: read from <paramref name="cachePath"/> when
    /// it holds glyphs for these fonts, otherwise the warm set
    /// (<see cref="GlyphWarmSet"/>) for both text weights and the icon font at
    /// all three tiers, rasterised in parallel, then written to the file for
    /// next time.  Pass null to skip the file both ways.
    /// </summary>
    public GlyphWarmUpResult WarmUp(string? cachePath, int maximumParallelism = -1)
    {
        var clock = Stopwatch.StartNew();
        if (cachePath is not null && GlyphCacheFile.TryLoad(this, cachePath))
        {
            return new GlyphWarmUpResult(true, EntryCount, clock.Elapsed, TimeSpan.Zero);
        }

        var items = WarmItems();
        var options = new ParallelOptions { MaxDegreeOfParallelism = maximumParallelism };
        Parallel.For(0, items.Count, options, index =>
        {
            var key = items[index];
            if (Claim(key.Face, key.Glyph, key.Tier))
            {
                Make(key);
            }
        });

        var rasterised = clock.Elapsed;
        var saved = TimeSpan.Zero;
        if (cachePath is not null)
        {
            var saveClock = Stopwatch.StartNew();
            GlyphCacheFile.TrySave(this, cachePath);
            saved = saveClock.Elapsed;
        }

        return new GlyphWarmUpResult(false, items.Count, rasterised, saved);
    }

    /// <summary>The (face, glyph, tier) triples of the warm set, without duplicates.</summary>
    public List<GlyphKey> WarmItems()
    {
        var items = new List<GlyphKey>();
        var seen = new HashSet<GlyphKey>();
        void AddFace(byte face, int[] codePoints)
        {
            var info = _faces[face];
            var points = new uint[codePoints.Length];
            for (var index = 0; index < points.Length; index++)
            {
                points[index] = (uint)codePoints[index];
            }

            var glyphs = info.Face.GetGlyphIndices(points);
            for (var tier = 0; tier < TierCount; tier++)
            {
                foreach (var glyph in glyphs)
                {
                    var key = new GlyphKey(face, glyph, (byte)tier);
                    if (glyph != 0 && seen.Add(key))
                    {
                        items.Add(key);
                    }
                }

                var ellipsis = new GlyphKey(face, info.EllipsisGlyph, (byte)tier);
                if (info.EllipsisGlyph != 0 && seen.Add(ellipsis))
                {
                    items.Add(ellipsis);
                }
            }
        }

        AddFace(FaceRegistry.Regular, GlyphWarmSet.TextCodePoints);
        AddFace(FaceRegistry.SemiBold, GlyphWarmSet.TextCodePoints);
        AddFace(FaceRegistry.Icons, GlyphWarmSet.IconCodePoints);
        return items;
    }

    /// <summary>Waits until no glyph is queued, for tests and warm-up.  Returns false on timeout.</summary>
    public bool WaitForPending(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (PendingCount > 0)
        {
            if (clock.Elapsed > timeout)
            {
                return false;
            }

            Thread.Sleep(1);
        }

        return true;
    }

    // ---- what the GPU copies and the cache file read -----------------------------

    /// <summary>The CPU copy of a page: <see cref="PageSize"/> rows of <see cref="PageSize"/> bytes.  Valid until the atlas is disposed.</summary>
    internal nint PagePointer(int page) => _pages[page];

    /// <summary>Rectangles placed so far; a GPU copy uploads those past the count it has seen.</summary>
    internal int PlacementCount
    {
        get
        {
            lock (_gate)
            {
                return _log.Count;
            }
        }
    }

    /// <summary>Copies placements from <paramref name="start"/> into <paramref name="into"/>; returns how many.</summary>
    internal int CopyPlacements(int start, Span<GlyphPlacement> into)
    {
        lock (_gate)
        {
            var count = Math.Clamp(_log.Count - start, 0, into.Length);
            for (var index = 0; index < count; index++)
            {
                into[index] = _log[start + index];
            }

            return count;
        }
    }

    /// <summary>Everything the cache file stores, taken under the lock: the packers' shelves and the placed entries of the fixed faces.</summary>
    internal GlyphAtlasSnapshot Snapshot()
    {
        lock (_gate)
        {
            var shelves = new List<ShelfPacker.Shelf>[_pageCount];
            var usedHeights = new int[_pageCount];
            for (var page = 0; page < _pageCount; page++)
            {
                shelves[page] = [.. _packers[page]!.Shelves];
                usedHeights[page] = _packers[page]!.UsedHeight;
            }

            var entries = new List<(GlyphKey Key, GlyphEntry Entry)>();
            for (var index = 0; index < _entryCount; index++)
            {
                var key = _keys[index >> ChunkShift]![index & (ChunkSize - 1)];
                if (key.Face < FaceRegistry.FirstFallback)
                {
                    entries.Add((key, _entries[index >> ChunkShift]![index & (ChunkSize - 1)]));
                }
            }

            return new GlyphAtlasSnapshot(_pageCount, usedHeights, shelves, entries);
        }
    }

    /// <summary>
    /// Puts back what the cache file held.  Only into a fresh atlas: the
    /// pages are overwritten and the entries published as they are read.
    /// </summary>
    internal bool Restore(int pageCount, IReadOnlyList<List<ShelfPacker.Shelf>> shelves, IReadOnlyList<(GlyphKey Key, GlyphEntry Entry)> entries, Func<int, nint, bool> fillPage)
    {
        lock (_gate)
        {
            if (_entryCount != 0 || pageCount > MaximumPages)
            {
                return false;
            }

            while (_pageCount < pageCount)
            {
                AddPage();
            }

            for (var page = 0; page < pageCount; page++)
            {
                if (!fillPage(page, _pages[page]))
                {
                    return false;
                }

                _packers[page]!.Restore(shelves[page]);
                _log.Add(new GlyphPlacement(page, 0, 0, PageSize, PageSize));
            }

            foreach (var (key, entry) in entries)
            {
                if (key.Face >= FaceRegistry.FirstFallback || key.Tier >= TierCount)
                {
                    continue;
                }

                var table = EnsureTableLocked(key.Face, key.Tier);
                if (key.Glyph >= table.Length || table[key.Glyph] != Unknown)
                {
                    continue;
                }

                var index = AppendEntryLocked(key, entry);
                Volatile.Write(ref table[key.Glyph], index + 1);
            }

            return true;
        }
    }

    // ---- placing -------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int SlotOf(byte face, ushort glyph, int tier)
    {
        var table = Volatile.Read(ref _tables[face * TierCount + tier]);
        if (table is null)
        {
            return Unknown;
        }

        return glyph < table.Length ? Volatile.Read(ref table[glyph]) : Unavailable;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref readonly GlyphEntry EntryRef(int index) => ref _entries[index >> ChunkShift]![index & (ChunkSize - 1)];

    private GlyphEntry EntryAt(int index) => EntryRef(index);

    /// <summary>Marks a glyph as being made; false when it is placed, queued or unavailable already.</summary>
    private bool Claim(byte face, ushort glyph, int tier)
    {
        var table = Volatile.Read(ref _tables[face * TierCount + tier]) ?? EnsureTable(face, tier);
        return glyph < table.Length && Interlocked.CompareExchange(ref table[glyph], Pending, Unknown) == Unknown;
    }

    private void Request(byte face, ushort glyph, int tier)
    {
        if (Volatile.Read(ref _disposed) != 0 || face >= _faces.Count || !Claim(face, glyph, tier))
        {
            return;
        }

        Interlocked.Increment(ref _pendingCount);
        EnsureWorker();
        _requests.Add(new GlyphKey(face, glyph, (byte)tier));
    }

    private void EnsureWorker()
    {
        if (Volatile.Read(ref _worker) is not null)
        {
            return;
        }

        lock (_gate)
        {
            if (_worker is not null)
            {
                return;
            }

            var worker = new Thread(RunWorker)
            {
                IsBackground = true,
                Name = "Glyph atlas",
                Priority = ThreadPriority.BelowNormal
            };
            worker.Start();
            Volatile.Write(ref _worker, worker);
        }
    }

    private void RunWorker()
    {
        foreach (var key in _requests.GetConsumingEnumerable())
        {
            Make(key);
            Interlocked.Decrement(ref _pendingCount);
            if (_requests.Count == 0)
            {
                GlyphsArrived?.Invoke();
            }
        }
    }

    /// <summary>
    /// Rasterises a claimed glyph and places it.  Whatever goes wrong, the
    /// glyph ends up placed, empty or unavailable - never left pending, or
    /// the labels using it would wait for ever.
    /// </summary>
    private void Make(GlyphKey key)
    {
        var table = _tables[key.Face * TierCount + key.Tier]!;
        try
        {
            var rasterizer = GlyphRasterizer.ForThread;
            var inked = rasterizer.Rasterise(_faces.Factory, _faces[key.Face], key.Glyph, TierTable[key.Tier]);
            if (!inked)
            {
                lock (_gate)
                {
                    var index = AppendEntryLocked(key, default);
                    Volatile.Write(ref table[key.Glyph], index + 1);
                }

                return;
            }

            Place(key, table, rasterizer);
        }
        catch (Exception ex)
        {
            // Everything is caught: this runs on the atlas's own thread or the
            // warm-up's, where an escaping exception would end the program.
            Debug.WriteLine($"Glyph {key.Glyph} of face {key.Face} could not be made: {ex.Message}");
            Volatile.Write(ref table[key.Glyph], Unavailable);
        }
    }

    private void Place(GlyphKey key, int[] table, GlyphRasterizer rasterizer)
    {
        var width = rasterizer.FieldWidth;
        var height = rasterizer.FieldHeight;
        int page, x, y;
        lock (_gate)
        {
            if (!TryAllocateLocked(width, height, out page, out x, out y))
            {
                Volatile.Write(ref table[key.Glyph], Unavailable);
                return;
            }
        }

        // The rectangle is this thread's alone until it is published, so the
        // texels are copied outside the lock.
        var field = rasterizer.Field;
        var destination = (byte*)_pages[page];
        for (var row = 0; row < height; row++)
        {
            field.Slice(row * width, width).CopyTo(new Span<byte>(destination + (long)(y + row) * PageSize + x, width));
        }

        var entry = new GlyphEntry(
            (ushort)page,
            (ushort)x,
            (ushort)y,
            (ushort)width,
            (ushort)height,
            rasterizer.Left,
            rasterizer.Top,
            rasterizer.Right,
            rasterizer.Bottom);

        lock (_gate)
        {
            var index = AppendEntryLocked(key, entry);
            _log.Add(new GlyphPlacement(page, x, y, width, height));
            Volatile.Write(ref table[key.Glyph], index + 1);
        }
    }

    private bool TryAllocateLocked(int width, int height, out int page, out int x, out int y)
    {
        for (page = 0; page < _pageCount; page++)
        {
            if (_packers[page]!.TryAllocate(width, height, out x, out y))
            {
                return true;
            }
        }

        while (_pageCount < MaximumPages)
        {
            page = AddPage();
            if (_packers[page]!.TryAllocate(width, height, out x, out y))
            {
                return true;
            }
        }

        page = x = y = 0;
        return false;
    }

    private int AddPage()
    {
        var page = _pageCount;
        _pages[page] = (nint)NativeMemory.AllocZeroed((nuint)PageSize * PageSize);
        _packers[page] = new ShelfPacker(PageSize, PageSize);
        Volatile.Write(ref _pageCount, page + 1);
        return page;
    }

    private int AppendEntryLocked(GlyphKey key, in GlyphEntry entry)
    {
        var index = _entryCount;
        var chunk = index >> ChunkShift;
        if (chunk >= ChunkCount)
        {
            throw new InvalidOperationException("The glyph atlas has no room for more entries.");
        }

        _entries[chunk] ??= new GlyphEntry[ChunkSize];
        _keys[chunk] ??= new GlyphKey[ChunkSize];
        _entries[chunk]![index & (ChunkSize - 1)] = entry;
        _keys[chunk]![index & (ChunkSize - 1)] = key;
        Volatile.Write(ref _entryCount, index + 1);
        return index;
    }

    private int[] EnsureTable(byte face, int tier)
    {
        lock (_gate)
        {
            return EnsureTableLocked(face, tier);
        }
    }

    private int[] EnsureTableLocked(byte face, int tier)
    {
        ref var slot = ref _tables[face * TierCount + tier];
        if (slot is null)
        {
            Volatile.Write(ref slot, new int[Math.Max(1, (int)_faces[face].GlyphCount)]);
        }

        return slot!;
    }

    /// <summary>
    /// Stops the worker and frees the pages.  Every <see cref="GlyphAtlasTexture"/>
    /// made from this atlas reads the pages when it uploads, so those are
    /// disposed first.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _requests.CompleteAdding();
        _worker?.Join(TimeSpan.FromSeconds(2));
        lock (_gate)
        {
            for (var page = 0; page < _pageCount; page++)
            {
                NativeMemory.Free((void*)_pages[page]);
                _pages[page] = 0;
            }

            _pageCount = 0;
        }
    }
}

/// <summary>A glyph of one face at one tier: what the atlas is keyed by.</summary>
internal readonly record struct GlyphKey(byte Face, ushort Glyph, byte Tier);

/// <summary>How start-up went: read from the cache file or rasterised, how many glyphs, how long rasterising and saving took.</summary>
internal readonly record struct GlyphWarmUpResult(bool FromCache, int Glyphs, TimeSpan Elapsed, TimeSpan Saving);

/// <summary>The atlas's state for the cache file.</summary>
internal sealed record GlyphAtlasSnapshot(int PageCount, int[] UsedHeights, List<ShelfPacker.Shelf>[] Shelves, List<(GlyphKey Key, GlyphEntry Entry)> Entries);
