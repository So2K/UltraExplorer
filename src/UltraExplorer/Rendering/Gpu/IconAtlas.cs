using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using Vortice.Direct3D11;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>How an <see cref="IconAtlas"/> is set up.  The defaults are the app's.</summary>
internal sealed class IconAtlasOptions
{
    /// <summary>The disk cache, or null for none.  By default <c>cache\icons-v1.bin</c> in the state folder.</summary>
    public string? CachePath { get; init; } = DefaultCachePath;

    /// <summary>Whether programs, shortcuts and icon files show their own icons rather than their type's.</summary>
    public bool PerFileIcons { get; init; } = true;

    /// <summary>How many slots the icons of single files may hold between them before the least recently drawn gives way.</summary>
    public int PerFileCapacity { get; init; } = 512;

    /// <summary>Slices the texture starts with; it doubles from there.</summary>
    public int InitialSlices { get; init; } = 256;

    /// <summary>The most slices there can be: Direct3D 11's limit for a texture array.</summary>
    public int MaximumSlices { get; init; } = 2048;

    /// <summary>At most this many new icons are copied to the GPU in one frame: the frame's budget for them (<see cref="Controls.FrameBudgets.IconUploads"/>).</summary>
    public int UploadsPerFrame { get; init; } = Controls.FrameBudgets.IconUploads;

    /// <summary>Extraction threads to start with.</summary>
    public int WorkerCount { get; init; } = IconExtractor.DefaultWorkerCount;

    /// <summary>How often the cache is written while it has changes; zero for only when asked and on disposal.</summary>
    public TimeSpan AutoSaveInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Replaces the Shell, for the checks; called on the extraction threads.</summary>
    internal Func<IconRequest, IconFound>? Source { get; init; }

    /// <summary>
    /// <c>cache\icons-v1.bin</c> under <see cref="AppPaths.StateDirectory"/>,
    /// so a copy started with <c>ULTRAEXPLORER_STATE_DIR</c> keeps its cache
    /// apart from the one in use.
    /// </summary>
    public static string DefaultCachePath => AppPaths.State(Path.Combine("cache", "icons-v1.bin"));
}

/// <summary>
/// Every file icon the nested canvas draws on the GPU, one slot per distinct
/// picture, keyed by file type.
///
/// <para><b>Keys.</b>  A file's lower-case extension, as <see cref="NestedFile.Extension"/>
/// has it; the canvas asks per type, which is what makes one small atlas enough.
/// Types whose icon differs from file to file - programs, shortcuts, icon and
/// cursor files, Internet shortcuts, management consoles, ClickOnce
/// references - may also have one
/// slot per file, in a region of at most <see cref="IconAtlasOptions.PerFileCapacity"/>
/// slots where the least recently drawn gives way; until a file's own icon is
/// there its type's generic one shows.  <see cref="PerFileIcons"/> switches the
/// whole region off.</para>
///
/// <para><b>Slots.</b>  Keys share slots: the extractor dedupes by the index in
/// the system image list (every type without an icon of its own has the one
/// "unknown file" index, so they are one slot), and the atlas by the picture
/// itself, which also covers what the disk cache brings back without indices.
/// A slot's pixels live here in memory as a 64-pixel chain of seven levels
/// (<see cref="IconSlotData"/>), so that a texture for any card - a new one
/// after the device was lost, or the other card's when the window moves - is
/// made complete in one call (<see cref="CreateTexture"/>).</para>
///
/// <para><b>Frames.</b>  <see cref="SlotFor(string?, string)"/> answers at once
/// and never waits: -1 while an icon is not known yet, in which case nothing is
/// drawn, as with the WPF canvas.  Asking is what queues the extraction, on
/// the extractor's own threads.  The finished icons wait in a queue until the
/// UI thread calls <see cref="ProcessArrivals(IconAtlasTexture?, ID3D11DeviceContext?, int)"/>
/// at the start of a frame, which copies at most 64 of them to the GPU and
/// only then lets <c>SlotFor</c> give their slots out - so a slot handed to the
/// renderer always holds its icon on the card it draws with.  <see cref="ArrivalsPending"/>
/// says when there is something to process.</para>
///
/// <para><b>Start-up.</b>  <see cref="LoadCache"/> brings back every icon of the
/// last run from disk before the first frame; those are checked again with the
/// Shell, one by one as they are first drawn and only when the extractor has
/// nothing else to do, and patched if they changed.  <see cref="PrefetchCommonTypes"/>
/// and <see cref="Prefetch(IReadOnlyList{NestedFile})"/> ask for types before
/// they are on screen.</para>
///
/// <para><b>Threads.</b>  Everything may be called from any thread; one lock
/// guards the tables.  The exceptions are <c>ProcessArrivals</c>, which uses
/// the immediate context and so belongs to the thread that draws, and the
/// answers of <c>SlotFor</c>, which hold for the texture passed to the latest
/// <c>ProcessArrivals</c>.</para>
/// </summary>
internal sealed class IconAtlas : IDisposable
{
    /// <summary>The side of a slice's largest level, in pixels.</summary>
    public const int SlotSize = IconSlotData.Size;

    /// <summary>Levels per slice, 64 pixels down to 1: the texture's MipLevels, and the third argument of CalculateSubResourceIndex.</summary>
    public const int MipLevels = IconSlotData.MipLevels;

    /// <summary>The stock "unknown file" icon, which a type the Shell has nothing for falls back to.</summary>
    public const string DocumentKey = "*document";

    /// <summary>The stock program icon.</summary>
    public const string ApplicationKey = "*application";

    /// <summary>The stock folder icon.</summary>
    public const string FolderKey = "*folder";

    /// <summary>A soft limit on remembered files; past it the ones that hold no slot of their own are forgotten.</summary>
    private const int FileEntryLimit = 8192;

    /// <summary>How long a file that could not have a slot waits before asking again.</summary>
    private const int DeniedRetryFrames = 120;

    /// <summary>At most this many arrivals are looked at in one frame, however many of them cost no upload.</summary>
    private const int ArrivalsPerFrameLimit = 1024;

    /// <summary>
    /// With more than one canvas drawing from the atlas, a file slot drawn
    /// within this long (Stopwatch ticks, half a second) may still be on one
    /// of their screens, however many frames ago that was (<see cref="RecentlyDrawnElsewhere"/>).
    /// </summary>
    private static readonly long RecentlyDrawnTicks = Stopwatch.Frequency / 2;

    /// <summary>
    /// Asked for at start-up, before any folder is read: the types most
    /// folders have, so a first view of a fresh install already has icons.
    /// </summary>
    private static readonly string[] CommonTypes =
    [
        "", "txt", "log", "md", "ini", "cfg", "conf", "json", "xml", "yaml", "yml", "csv", "tsv", "toml",
        "html", "htm", "css", "js", "ts", "tsx", "jsx", "mjs", "cs", "csproj", "sln", "props", "targets", "resx", "xaml",
        "vb", "fs", "cpp", "c", "h", "hpp", "py", "rb", "go", "rs", "java", "kt", "php", "lua", "sh",
        "ps1", "psm1", "bat", "cmd", "reg", "sql", "db", "sqlite",
        "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "rtf", "epub", "chm",
        "png", "jpg", "jpeg", "gif", "bmp", "tif", "tiff", "webp", "ico", "svg", "psd", "heic", "raw", "cr2", "nef", "dng",
        "mp3", "wav", "flac", "ogg", "m4a", "aac", "wma", "mid", "opus",
        "mp4", "mkv", "avi", "mov", "wmv", "webm", "flv", "m4v", "mpg", "m2ts", "srt",
        "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "cab", "msi", "msix", "appx",
        "exe", "dll", "sys", "lnk", "url", "ttf", "otf", "woff", "woff2", "fon",
        "dmp", "tmp", "bak", "dat", "bin", "cer", "crt", "pem", "jar", "apk", "vhd", "vhdx",
        "eml", "msg", "ics", "vcf", "torrent", "nfo", "cue", "m3u", "pdb", "obj", "lib", "nupkg"
    ];

    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly IconAtlasOptions _options;
    private readonly IconExtractor _extractor;
    private readonly ConcurrentQueue<IconExtraction> _arrivals = new();

    private readonly Dictionary<string, KeyEntry> _types = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, KeyEntry>.AlternateLookup<ReadOnlySpan<char>> _typesBySpan;
    private readonly Dictionary<string, KeyEntry> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, KeyEntry>.AlternateLookup<ReadOnlySpan<char>> _filesBySpan;
    private readonly Dictionary<ulong, int> _byHash = [];

    // Per slot.  Slot numbers are texture slices.
    private readonly IconSlotData?[] _slotData;
    private readonly int[] _slotVersion;
    private readonly SlotRegion[] _slotRegion;
    private readonly int[] _slotReferences;
    private readonly List<string>?[] _slotFileKeys;
    private readonly long[] _lastUsedFrame;
    private readonly long[] _lastUsedTicks;
    private readonly int[] _lruPrevious;
    private readonly int[] _lruNext;
    private readonly Stack<int> _freeSlots = new();

    private readonly Timer? _autoSave;

    private int _lruHead = -1;
    private int _lruTail = -1;
    private int _nextSlot;
    private int _typeSlotCount;
    private int _fileSlotCount;
    private long _frame;

    /// <summary>When the current frame's <c>ProcessArrivals</c> began (Stopwatch ticks); what a slot drawn in it is stamped with.</summary>
    private long _frameTicks;

    private bool _dirty;
    private bool _perFileIcons;
    private int _signalled;
    private int _disposed;
    private long _extractions;
    private IconAtlasTexture? _active;

    /// <summary>Who <see cref="ArrivalsPending"/> wakes, and how many of them: one per canvas drawing from the atlas.  Changed under the gate.</summary>
    private Action? _arrivalsPending;
    private int _listeners;

    public IconAtlas(IconAtlasOptions? options = null)
    {
        _options = options ?? new IconAtlasOptions();
        _perFileIcons = _options.PerFileIcons;
        _typesBySpan = _types.GetAlternateLookup<ReadOnlySpan<char>>();
        _filesBySpan = _files.GetAlternateLookup<ReadOnlySpan<char>>();

        var slices = Math.Clamp(_options.MaximumSlices, 1, 2048);
        _slotData = new IconSlotData?[slices];
        _slotVersion = new int[slices];
        _slotRegion = new SlotRegion[slices];
        _slotReferences = new int[slices];
        _slotFileKeys = new List<string>?[slices];
        _lastUsedFrame = new long[slices];
        _lastUsedTicks = new long[slices];
        _lruPrevious = new int[slices];
        _lruNext = new int[slices];
        Array.Fill(_lruPrevious, -1);
        Array.Fill(_lruNext, -1);

        _extractor = new IconExtractor(OnExtracted, _options.WorkerCount, _options.Source);
        if (_options.CachePath is not null && _options.AutoSaveInterval > TimeSpan.Zero)
        {
            _autoSave = new Timer(_ => SaveQuietly(), null, _options.AutoSaveInterval, _options.AutoSaveInterval);
        }
    }

    /// <summary>
    /// Raised on an extraction thread when finished icons start waiting for
    /// <see cref="ProcessArrivals(IconAtlasTexture?, ID3D11DeviceContext?, int)"/>,
    /// once until that next runs - the canvas wakes its frame loop, which
    /// takes them in with its next frame of labels: however many icons arrive
    /// between two frames, one wake and no dispatcher operation of their own.
    /// Must not block.  Every canvas drawing from the atlas listens, so how
    /// many listen is also how many canvases share its frames.
    /// </summary>
    public event Action? ArrivalsPending
    {
        add
        {
            lock (_gate)
            {
                _arrivalsPending += value;
                _listeners = _arrivalsPending?.GetInvocationList().Length ?? 0;
            }
        }

        remove
        {
            lock (_gate)
            {
                _arrivalsPending -= value;
                _listeners = _arrivalsPending?.GetInvocationList().Length ?? 0;
            }
        }
    }

    /// <summary>Whether finished icons are waiting; true after a <c>ProcessArrivals</c> that hit its limit.</summary>
    public bool HasPendingArrivals => !_arrivals.IsEmpty;

    /// <summary>Slots in use, both regions.</summary>
    public int SlotCount
    {
        get
        {
            lock (_gate)
            {
                return _typeSlotCount + _fileSlotCount;
            }
        }
    }

    /// <summary>Slots in use by the icons of single files.</summary>
    public int FileSlotCount
    {
        get
        {
            lock (_gate)
            {
                return _fileSlotCount;
            }
        }
    }

    /// <summary>How many requests the extractor has finished, for the checks and the bench.</summary>
    public long ExtractionCount => Interlocked.Read(ref _extractions);

    /// <summary>The extraction threads' managed ids, for the checks.</summary>
    internal IReadOnlyList<int> WorkerThreadIds => _extractor.WorkerThreadIds;

    /// <summary>Keys asked for whose icons have not been taken in yet, for the checks.</summary>
    internal int PendingKeyCount
    {
        get
        {
            lock (_gate)
            {
                return _types.Values.Count(entry => entry.State == KeyState.Pending)
                    + _files.Values.Count(entry => entry.State == KeyState.Pending);
            }
        }
    }

    /// <summary>
    /// Whether programs, shortcuts and the like show their own icons.  Turning
    /// it off lets go of every slot that only a single file used.
    /// </summary>
    public bool PerFileIcons
    {
        get => Volatile.Read(ref _perFileIcons);
        set
        {
            lock (_gate)
            {
                if (_perFileIcons == value)
                {
                    return;
                }

                _perFileIcons = value;
                if (!value)
                {
                    ForgetFiles();
                }
            }
        }
    }

    /// <summary>
    /// Whether a file with this extension is drawn with an icon of its own,
    /// so the caller knows to pass its path.  Accepts the extension with or
    /// without its dot, in any case.
    /// </summary>
    public bool UsesPerFileIcon(string extension) => PerFileIcons && IsPerFileType(Trim(extension));

    // ---- asking --------------------------------------------------------------------------

    /// <summary>
    /// The slot of a file type's icon, or -1 while it is not known; asking is
    /// what queues it.  <paramref name="extension"/> may have its dot or not,
    /// in any case: "txt", ".txt" and "TXT" are one key.
    /// </summary>
    public int SlotFor(string extension)
    {
        var trimmed = Trim(extension);
        lock (_gate)
        {
            return TypeSlot(trimmed);
        }
    }

    /// <summary>
    /// The slot to draw a file with, or -1 while nothing for it is known.  For
    /// a type whose icons differ per file, the file's own icon once it has
    /// been extracted and its type's until then; for every other type, the
    /// type's.  <paramref name="path"/> is the file's full path and may be null
    /// when only the type is wanted.
    /// </summary>
    public int SlotFor(string? path, string extension)
    {
        var trimmed = Trim(extension);
        lock (_gate)
        {
            if (path is not null && _perFileIcons && IsPerFileType(trimmed))
            {
                var slot = FileSlot(path, path);
                if (slot >= 0)
                {
                    return slot;
                }
            }

            return TypeSlot(trimmed);
        }
    }

    /// <summary>
    /// As <see cref="SlotFor(string?, string)"/> for the file
    /// <paramref name="fileName"/> in <paramref name="folderPath"/>, without
    /// building its path as a string: a view full of programs asks for
    /// hundreds a frame, and the canvas has the folder and the name apart.
    /// The two are joined the way <see cref="Path.Join(string?, string?)"/> joins them.
    /// </summary>
    public int SlotFor(string folderPath, string fileName, string extension)
    {
        var trimmed = Trim(extension);
        if (trimmed.IsEmpty && Path.GetExtension(fileName.AsSpan()) is { Length: > 1 } named)
        {
            // A name that starts with its only dot (.gitignore), or whose type
            // is longer than the tree keeps: the tree gives it no extension,
            // but the Shell reads its type from the name, and so do Explorer
            // and the folder list (ShellIconService.KeyOf).
            trimmed = named[1..];
        }

        if (!PerFileIcons || !IsPerFileType(trimmed))
        {
            lock (_gate)
            {
                return TypeSlot(trimmed);
            }
        }

        var separator = folderPath.Length > 0 && folderPath[^1] is not ('\\' or '/') ? 1 : 0;
        var length = folderPath.Length + separator + fileName.Length;
        Span<char> path = length <= 512 ? stackalloc char[length] : new char[length];
        folderPath.AsSpan().CopyTo(path);
        if (separator == 1)
        {
            path[folderPath.Length] = '\\';
        }

        fileName.AsSpan().CopyTo(path[(folderPath.Length + separator)..]);
        lock (_gate)
        {
            if (_perFileIcons)
            {
                var slot = FileSlot(path, null);
                if (slot >= 0)
                {
                    return slot;
                }
            }

            return TypeSlot(trimmed);
        }
    }

    /// <summary>Queues a type's icon before it is on screen, behind everything that is.</summary>
    public void Prefetch(string extension)
    {
        var trimmed = Trim(extension);
        lock (_gate)
        {
            PrefetchType(trimmed);
        }
    }

    /// <summary>Queues every type among <paramref name="extensions"/> that is not known or asked for yet.</summary>
    public void Prefetch(IEnumerable<string> extensions)
    {
        lock (_gate)
        {
            foreach (var extension in extensions)
            {
                PrefetchType(Trim(extension));
            }
        }
    }

    /// <summary>
    /// Queues the type of every file of a folder that was just read, before
    /// any of it is drawn.  Extensions are shared strings, so the handful of
    /// types in a folder of fifty thousand files are told apart by reference
    /// before the lock is taken.
    /// </summary>
    public void Prefetch(IReadOnlyList<NestedFile> files)
    {
        HashSet<string>? distinct = null;
        string? last = null;
        for (var index = 0; index < files.Count; index++)
        {
            var extension = files[index].Extension;
            if (ReferenceEquals(extension, last))
            {
                continue;
            }

            last = extension;
            (distinct ??= new HashSet<string>(ReferenceEqualityComparer.Instance)).Add(extension);
        }

        if (distinct is not null)
        {
            Prefetch(distinct);
        }
    }

    /// <summary>The stock icons and about a hundred and twenty common types, for start-up.</summary>
    public void PrefetchCommonTypes()
        => Prefetch([DocumentKey, ApplicationKey, FolderKey, .. CommonTypes]);

    // ---- frames --------------------------------------------------------------------------

    /// <inheritdoc cref="ProcessArrivals(IconAtlasTexture?, ID3D11DeviceContext?, int)"/>
    public int ProcessArrivals(IconAtlasTexture? texture, ID3D11DeviceContext? context)
        => ProcessArrivals(texture, context, _options.UploadsPerFrame);

    /// <summary>
    /// Once per frame, on the thread that draws, before the icons are laid
    /// out: takes the icons that have arrived, gives them slots, copies at
    /// most <paramref name="maximumUploads"/> new ones into
    /// <paramref name="texture"/>, and from then on hands their slots out.
    /// Returns how many arrivals it took; when it stopped at the limit,
    /// <see cref="HasPendingArrivals"/> stays true and the caller should
    /// draw another frame.
    ///
    /// <paramref name="texture"/> is the one the frame samples.  When it is
    /// not the one of the previous frame - the window moved to the other card,
    /// or the device was made anew - every slot it lacks is copied first, in
    /// full.  Null works on the tables alone, as the checks do before a
    /// device exists.
    /// </summary>
    public int ProcessArrivals(IconAtlasTexture? texture, ID3D11DeviceContext? context, int maximumUploads)
    {
        Volatile.Write(ref _signalled, 0);
        var taken = 0;
        var uploads = 0;
        lock (_gate)
        {
            _frame++;
            _frameTicks = Stopwatch.GetTimestamp();
            Activate(texture, context);
            while (uploads < maximumUploads && taken < ArrivalsPerFrameLimit && _arrivals.TryDequeue(out var arrival))
            {
                taken++;
                if (Apply(arrival, context))
                {
                    uploads++;
                }
            }
        }

        return taken;
    }

    /// <summary>
    /// A texture of the whole atlas on <paramref name="device"/>, made in one
    /// call from the copies in memory - for a device set to keep with
    /// <see cref="GpuDeviceSet.Attach{T}"/>.  Any thread.  Throws what the
    /// device throws when it cannot.
    /// </summary>
    public IconAtlasTexture CreateTexture(ID3D11Device device)
    {
        IconSlotData?[] slots;
        int[] versions;
        int capacity;
        lock (_gate)
        {
            slots = _slotData[.._nextSlot];
            versions = _slotVersion[.._nextSlot];
            capacity = CapacityFor(_nextSlot);
        }

        return new IconAtlasTexture(device, capacity, slots, versions);
    }

    // ---- the disk cache ---------------------------------------------------------------------

    /// <summary>
    /// Brings back the icons of the last run from the disk cache.  For
    /// start-up, before anything is asked: an atlas that already holds icons
    /// keeps them and ignores the file.  Every key read shows at once, and is
    /// checked against the Shell again the first time it is drawn.  True when
    /// the file was there and used.
    /// </summary>
    public bool LoadCache()
    {
        var path = _options.CachePath;
        if (path is null)
        {
            return false;
        }

        var contents = IconAtlasCache.Read(path, _slotData.Length);
        if (contents is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (_nextSlot > 0 || _types.Count > 0 || _files.Count > 0)
            {
                return false;
            }

            var count = contents.Slots.Length;
            var region = new SlotRegion[count];
            foreach (var key in contents.Keys)
            {
                if (key.Kind != IconKeyKind.File)
                {
                    region[key.Slot] = SlotRegion.Type;
                }
            }

            // Files were written most recently drawn first; the region keeps
            // its limit even if the file came from a run that allowed more.
            var fileBudget = _perFileIcons ? _options.PerFileCapacity : 0;
            var fileSlots = new List<int>();
            var keptFiles = new List<IconAtlasCache.CachedKey>();
            foreach (var key in contents.Keys)
            {
                if (key.Kind != IconKeyKind.File || !_perFileIcons)
                {
                    continue;
                }

                if (region[key.Slot] == SlotRegion.Free)
                {
                    if (fileSlots.Count >= fileBudget)
                    {
                        continue;
                    }

                    region[key.Slot] = SlotRegion.File;
                    fileSlots.Add(key.Slot);
                }

                keptFiles.Add(key);
            }

            for (var slot = 0; slot < count; slot++)
            {
                if (region[slot] == SlotRegion.Free)
                {
                    continue;
                }

                var data = contents.Slots[slot];
                _slotData[slot] = data;
                _slotVersion[slot] = 1;
                _slotRegion[slot] = region[slot];
                _byHash.TryAdd(data.Hash, slot);
                if (region[slot] == SlotRegion.Type)
                {
                    _typeSlotCount++;
                }
            }

            foreach (var slot in fileSlots)
            {
                LinkAtTail(slot);
                _fileSlotCount++;
            }

            _nextSlot = count;
            for (var slot = count - 1; slot >= 0; slot--)
            {
                if (region[slot] == SlotRegion.Free)
                {
                    _freeSlots.Push(slot);
                }
            }

            foreach (var key in contents.Keys)
            {
                if (key.Kind != IconKeyKind.File)
                {
                    _types[key.Key] = new KeyEntry(key.Key, key.Slot, KeyState.Cached, IconPriority.Revalidate);
                    Reference(key.Slot, key.Key, file: false);
                }
            }

            foreach (var key in keptFiles)
            {
                _files[key.Key] = new KeyEntry(key.Key, key.Slot, KeyState.Cached, IconPriority.Revalidate);
                Reference(key.Slot, key.Key, file: true);
            }

            // Whatever texture was current has none of this; the next frame
            // brings it up to date before a slot is handed out for it.
            _active = null;
            _dirty = false;
            return true;
        }
    }

    /// <summary>Writes the disk cache now.  False when there is no cache or it could not be written.</summary>
    public bool Save() => Save(force: true);

    /// <summary>Writes the disk cache if anything changed since it was last written or read.</summary>
    public bool SaveIfDirty() => Save(force: false);

    /// <summary>Stops extracting, writes the cache if it changed, and lets go.  Textures belong to their device sets.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _autoSave?.Dispose();
        _extractor.Dispose();
        SaveQuietly();
        lock (_gate)
        {
            _active = null;
        }
    }

    /// <summary>A slot's pixels as the atlas holds them, for the checks.</summary>
    internal IconSlotData? DataOf(int slot)
    {
        lock (_gate)
        {
            return slot >= 0 && slot < _slotData.Length ? _slotData[slot] : null;
        }
    }

    // ---- inside the lock -----------------------------------------------------------------

    private int TypeSlot(ReadOnlySpan<char> extension)
    {
        ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_typesBySpan, extension);
        if (Unsafe.IsNullRef(ref entry))
        {
            var key = TypeKey(extension);
            _types[key] = new KeyEntry(key, -1, KeyState.Pending, IconPriority.Visible);
            _extractor.Enqueue(new IconRequest(key, KindOf(key), IconPriority.Visible));
            return -1;
        }

        switch (entry.State)
        {
            case KeyState.Cached:
                entry.State = KeyState.Ready;
                _extractor.Enqueue(new IconRequest(entry.Key, KindOf(entry.Key), IconPriority.Revalidate));
                return entry.Slot;
            case KeyState.Pending when entry.Queued != IconPriority.Visible:
                // Waiting behind the prefetches, but on screen now: ask again
                // at the front.  Whichever answer comes first is used.
                entry.Queued = IconPriority.Visible;
                _extractor.Enqueue(new IconRequest(entry.Key, KindOf(entry.Key), IconPriority.Visible));
                return -1;
            default:
                return entry.Slot;
        }
    }

    private int FileSlot(ReadOnlySpan<char> path, string? pathString)
    {
        ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_filesBySpan, path);
        if (Unsafe.IsNullRef(ref entry))
        {
            if (_files.Count >= FileEntryLimit)
            {
                TrimFiles();
            }

            var key = pathString ?? path.ToString();
            _files[key] = new KeyEntry(key, -1, KeyState.Pending, IconPriority.Visible);
            _extractor.Enqueue(new IconRequest(key, IconKeyKind.File, IconPriority.Visible));
            return -1;
        }

        switch (entry.State)
        {
            case KeyState.Ready:
                Touch(entry.Slot);
                return entry.Slot;
            case KeyState.Cached:
                entry.State = KeyState.Ready;
                Touch(entry.Slot);
                _extractor.Enqueue(new IconRequest(entry.Key, IconKeyKind.File, IconPriority.Revalidate));
                return entry.Slot;
            case KeyState.Denied when _frame - entry.Frame > DeniedRetryFrames:
                entry.State = KeyState.Pending;
                _extractor.Enqueue(new IconRequest(entry.Key, IconKeyKind.File, IconPriority.Visible));
                return -1;
            default:
                return -1;
        }
    }

    private void PrefetchType(ReadOnlySpan<char> extension)
    {
        if (_typesBySpan.ContainsKey(extension))
        {
            return;
        }

        var key = TypeKey(extension);
        _types[key] = new KeyEntry(key, -1, KeyState.Pending, IconPriority.Prefetch);
        _extractor.Enqueue(new IconRequest(key, KindOf(key), IconPriority.Prefetch));
    }

    /// <summary>
    /// Makes <paramref name="texture"/> the one slots are handed out for,
    /// copying into it first every slot whose current version it lacks.
    /// </summary>
    private void Activate(IconAtlasTexture? texture, ID3D11DeviceContext? context)
    {
        if (texture is not null && (context is null || texture.IsDisposed))
        {
            texture = null;
        }

        if (ReferenceEquals(texture, _active))
        {
            return;
        }

        _active = texture;
        if (texture is null || context is null)
        {
            return;
        }

        try
        {
            if (texture.Capacity < _nextSlot && !texture.Grow(context, CapacityFor(_nextSlot)))
            {
                _active = null;
                return;
            }

            for (var slot = 0; slot < _nextSlot; slot++)
            {
                if (_slotData[slot] is { } data && texture.VersionOf(slot) != _slotVersion[slot])
                {
                    texture.Upload(context, slot, data, _slotVersion[slot]);
                }
            }
        }
        catch (SharpGenException)
        {
            // The card is gone; the device set is rebuilt and brings a new texture.
            _active = null;
        }
    }

    /// <summary>Takes one arrival.  True when it put new pixels in a slot.</summary>
    private bool Apply(IconExtraction arrival, ID3D11DeviceContext? context)
        => arrival.Request.Kind == IconKeyKind.File ? ApplyFile(arrival, context) : ApplyType(arrival, context);

    private bool ApplyType(IconExtraction arrival, ID3D11DeviceContext? context)
    {
        var key = arrival.Request.Key;
        if (!_types.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (arrival.Dropped)
        {
            if (entry.State == KeyState.Pending)
            {
                _types.Remove(key);
            }

            return false;
        }

        var pixels = arrival.Pixels;
        if (pixels is null)
        {
            if (entry.Slot < 0)
            {
                // The Shell had nothing for it: the generic document icon, as Explorer shows.
                if (key != DocumentKey && _types.TryGetValue(DocumentKey, out var document) && document.Slot >= 0)
                {
                    MapKey(ref entry, document.Slot, file: false);
                }
                else
                {
                    entry.State = KeyState.Failed;
                }
            }
            else if (entry.State == KeyState.Pending)
            {
                entry.State = KeyState.Ready;
            }

            _types[key] = entry;
            return false;
        }

        if (entry.Slot >= 0 && _slotData[entry.Slot] is { } current && current.SamePixels(pixels))
        {
            if (entry.State == KeyState.Pending)
            {
                entry.State = KeyState.Ready;
            }

            _types[key] = entry;
            return false;
        }

        var uploaded = false;
        var slot = FindSame(pixels);
        if (slot >= 0)
        {
            if (_slotRegion[slot] == SlotRegion.File)
            {
                Promote(slot);
            }
        }
        else
        {
            slot = Allocate(SlotRegion.Type);
            if (slot < 0)
            {
                if (entry.Slot < 0)
                {
                    entry.State = KeyState.Failed;
                }

                _types[key] = entry;
                return false;
            }

            Store(slot, pixels, context);
            uploaded = true;
        }

        MapKey(ref entry, slot, file: false);
        _types[key] = entry;
        return uploaded;
    }

    private bool ApplyFile(IconExtraction arrival, ID3D11DeviceContext? context)
    {
        var key = arrival.Request.Key;
        if (!_files.TryGetValue(key, out var entry))
        {
            // Forgotten while it was being extracted; asked again if it is drawn again.
            return false;
        }

        if (arrival.Dropped)
        {
            if (entry.State == KeyState.Pending)
            {
                _files.Remove(key);
            }

            return false;
        }

        var pixels = arrival.Pixels;
        if (pixels is null)
        {
            entry.State = entry.Slot >= 0 ? KeyState.Ready : KeyState.UseType;
            _files[key] = entry;
            return false;
        }

        if (entry.Slot >= 0 && _slotData[entry.Slot] is { } current && current.SamePixels(pixels))
        {
            entry.State = KeyState.Ready;
            _files[key] = entry;
            return false;
        }

        // A program without an icon of its own, or a second shortcut to the
        // same program: the picture is already in a slot.
        var same = FindSame(pixels);
        if (same >= 0)
        {
            MapKey(ref entry, same, file: true);
            _files[key] = entry;
            Touch(same);
            return false;
        }

        if (!_perFileIcons)
        {
            entry.State = KeyState.UseType;
            _files[key] = entry;
            return false;
        }

        // A picture of its own.  Let go of any old slot first, so a changed
        // icon can reuse the slot it had.
        if (entry.Slot >= 0)
        {
            Release(entry.Slot, key, file: true);
            entry.Slot = -1;
        }

        var slot = _fileSlotCount < _options.PerFileCapacity ? Allocate(SlotRegion.File) : -1;
        if (slot < 0)
        {
            var victim = _lruTail;
            if (victim < 0 || _lastUsedFrame[victim] >= _frame - 1 || RecentlyDrawnElsewhere(victim))
            {
                // Every file slot was drawn in the last frame, or may still be
                // on another canvas's screen: taking one would only have it
                // asked for again.  Its type's icon shows for now.
                entry.State = KeyState.Denied;
                entry.Frame = _frame;
                _files[key] = entry;
                return false;
            }

            Evict(victim);
            slot = victim;
            LinkAtHead(slot);
        }

        Store(slot, pixels, context);
        MapKey(ref entry, slot, file: true);
        _files[key] = entry;
        Touch(slot);
        return true;
    }

    /// <summary>Points a key at <paramref name="slot"/>, letting go of the one it had.</summary>
    private void MapKey(ref KeyEntry entry, int slot, bool file)
    {
        var old = entry.Slot;
        if (old != slot)
        {
            Reference(slot, entry.Key, file);
            entry.Slot = slot;
            if (old >= 0)
            {
                Release(old, entry.Key, file);
            }

            _dirty = true;
        }

        entry.State = KeyState.Ready;
    }

    private void Reference(int slot, string key, bool file)
    {
        _slotReferences[slot]++;
        if (file && _slotRegion[slot] == SlotRegion.File)
        {
            (_slotFileKeys[slot] ??= []).Add(key);
        }
    }

    private void Release(int slot, string key, bool file)
    {
        if (file && _slotRegion[slot] == SlotRegion.File)
        {
            _slotFileKeys[slot]?.Remove(key);
        }

        if (--_slotReferences[slot] <= 0)
        {
            Free(slot);
        }
    }

    private int FindSame(IconSlotData pixels)
        => _byHash.TryGetValue(pixels.Hash, out var slot) && _slotData[slot] is { } existing && existing.SamePixels(pixels)
            ? slot
            : -1;

    /// <summary>
    /// A free slot for <paramref name="region"/>, or -1.  Types may use all
    /// but the files' share of the array, so a folder of ten thousand programs
    /// can never crowd them out.
    /// </summary>
    private int Allocate(SlotRegion region)
    {
        if (region == SlotRegion.Type
            && _typeSlotCount >= _slotData.Length - (_perFileIcons ? Math.Min(_options.PerFileCapacity, _slotData.Length / 2) : 0))
        {
            return -1;
        }

        var slot = _freeSlots.Count > 0 ? _freeSlots.Pop() : _nextSlot < _slotData.Length ? _nextSlot++ : -1;
        if (slot < 0)
        {
            return -1;
        }

        _slotRegion[slot] = region;
        _slotReferences[slot] = 0;
        if (region == SlotRegion.Type)
        {
            _typeSlotCount++;
        }
        else
        {
            _fileSlotCount++;
            LinkAtHead(slot);
        }

        return slot;
    }

    /// <summary>Puts new pixels in a slot and, when a texture is current, on the card too.</summary>
    private void Store(int slot, IconSlotData pixels, ID3D11DeviceContext? context)
    {
        if (_slotData[slot] is { } old && _byHash.TryGetValue(old.Hash, out var owner) && owner == slot)
        {
            _byHash.Remove(old.Hash);
        }

        _slotData[slot] = pixels;
        _slotVersion[slot]++;
        _byHash.TryAdd(pixels.Hash, slot);
        _lastUsedFrame[slot] = _frame;
        _lastUsedTicks[slot] = _frameTicks;
        _dirty = true;

        var texture = _active;
        if (texture is null || context is null)
        {
            return;
        }

        try
        {
            if (slot >= texture.Capacity && !texture.Grow(context, CapacityFor(slot + 1)))
            {
                _active = null;
                return;
            }

            if (!texture.Upload(context, slot, pixels, _slotVersion[slot]))
            {
                _active = null;
            }
        }
        catch (SharpGenException)
        {
            _active = null;
        }
    }

    /// <summary>
    /// Takes a file slot back for reuse: every file shown with it forgets it,
    /// and is asked for again if it is drawn again.
    /// </summary>
    private void Evict(int slot)
    {
        if (_slotFileKeys[slot] is { } keys)
        {
            foreach (var key in keys)
            {
                _files.Remove(key);
            }

            keys.Clear();
        }

        if (_slotData[slot] is { } old && _byHash.TryGetValue(old.Hash, out var owner) && owner == slot)
        {
            _byHash.Remove(old.Hash);
        }

        _slotReferences[slot] = 0;
        Unlink(slot);
        _dirty = true;
    }

    private void Free(int slot)
    {
        if (_slotRegion[slot] == SlotRegion.File)
        {
            Unlink(slot);
            _fileSlotCount--;
        }
        else if (_slotRegion[slot] == SlotRegion.Type)
        {
            _typeSlotCount--;
        }

        if (_slotData[slot] is { } old && _byHash.TryGetValue(old.Hash, out var owner) && owner == slot)
        {
            _byHash.Remove(old.Hash);
        }

        _slotData[slot] = null;
        _slotRegion[slot] = SlotRegion.Free;
        _slotReferences[slot] = 0;
        _slotFileKeys[slot] = null;
        _freeSlots.Push(slot);
        _dirty = true;
    }

    /// <summary>A file slot whose picture a type turned out to share: it is a type's now, and never evicted.</summary>
    private void Promote(int slot)
    {
        Unlink(slot);
        _fileSlotCount--;
        _typeSlotCount++;
        _slotRegion[slot] = SlotRegion.Type;
        _slotFileKeys[slot] = null;
    }

    /// <summary>Marks a slot as drawn this frame and, for a file slot, the most recently drawn.</summary>
    private void Touch(int slot)
    {
        _lastUsedFrame[slot] = _frame;
        _lastUsedTicks[slot] = _frameTicks;
        if (_slotRegion[slot] == SlotRegion.File && _lruHead != slot)
        {
            Unlink(slot);
            LinkAtHead(slot);
        }
    }

    /// <summary>
    /// Whether a file slot not drawn in the frame just gone may still be on
    /// screen in another canvas.  Frames are counted for every canvas
    /// together - each one's <c>ProcessArrivals</c> is one - so with two
    /// drawing, the panes of a split view or two windows, each one's icons are
    /// two frames old by the time its next frame takes an arrival in; counting
    /// frames alone, the two took each other's slots on screen in turn, for
    /// good, and the Shell was never left alone.  With more than one canvas
    /// listening, a slot drawn within the last half second is held too.  A
    /// canvas alone keeps counting frames, as it always has.  Under the gate.
    /// </summary>
    private bool RecentlyDrawnElsewhere(int slot)
        => _listeners > 1 && _frameTicks - _lastUsedTicks[slot] < RecentlyDrawnTicks;

    /// <summary>
    /// Forgets the files that hold no slot of their own - those shown with
    /// their type's icon, or with a picture a type shares - once there are
    /// too many to keep.  They are asked for again if they are drawn again.
    /// </summary>
    private void TrimFiles()
    {
        List<string>? forget = null;
        foreach (var (key, entry) in _files)
        {
            if (entry.State == KeyState.Pending || entry.Slot >= 0 && _slotRegion[entry.Slot] == SlotRegion.File)
            {
                continue;
            }

            (forget ??= []).Add(key);
        }

        if (forget is null)
        {
            return;
        }

        foreach (var key in forget)
        {
            if (_files.Remove(key, out var entry) && entry.Slot >= 0)
            {
                Release(entry.Slot, key, file: true);
            }
        }
    }

    private void ForgetFiles()
    {
        foreach (var (key, entry) in _files)
        {
            if (entry.Slot >= 0)
            {
                Release(entry.Slot, key, file: true);
            }
        }

        _files.Clear();
    }

    private void LinkAtHead(int slot)
    {
        _lruPrevious[slot] = -1;
        _lruNext[slot] = _lruHead;
        if (_lruHead >= 0)
        {
            _lruPrevious[_lruHead] = slot;
        }

        _lruHead = slot;
        if (_lruTail < 0)
        {
            _lruTail = slot;
        }
    }

    private void LinkAtTail(int slot)
    {
        _lruNext[slot] = -1;
        _lruPrevious[slot] = _lruTail;
        if (_lruTail >= 0)
        {
            _lruNext[_lruTail] = slot;
        }

        _lruTail = slot;
        if (_lruHead < 0)
        {
            _lruHead = slot;
        }
    }

    private void Unlink(int slot)
    {
        var previous = _lruPrevious[slot];
        var next = _lruNext[slot];
        if (previous >= 0)
        {
            _lruNext[previous] = next;
        }
        else if (_lruHead == slot)
        {
            _lruHead = next;
        }

        if (next >= 0)
        {
            _lruPrevious[next] = previous;
        }
        else if (_lruTail == slot)
        {
            _lruTail = previous;
        }

        _lruPrevious[slot] = -1;
        _lruNext[slot] = -1;
    }

    private int CapacityFor(int slots)
    {
        var capacity = Math.Clamp(_options.InitialSlices, 1, _slotData.Length);
        while (capacity < slots && capacity < _slotData.Length)
        {
            capacity = Math.Min(capacity * 2, _slotData.Length);
        }

        return capacity;
    }

    // ---- outside the lock ----------------------------------------------------------------

    private void OnExtracted(IconExtraction extraction)
    {
        // Queued before it is counted, so whoever sees the count also finds
        // the arrival waiting.
        _arrivals.Enqueue(extraction);
        if (!extraction.Dropped)
        {
            Interlocked.Increment(ref _extractions);
        }

        if (Interlocked.Exchange(ref _signalled, 1) == 0)
        {
            try
            {
                Volatile.Read(ref _arrivalsPending)?.Invoke();
            }
            catch (Exception)
            {
                // A subscriber's failure is not the extractor's.
            }
        }
    }

    private bool Save(bool force)
    {
        var path = _options.CachePath;
        if (path is null)
        {
            return false;
        }

        lock (_saveGate)
        {
            List<IconSlotData> slots = [];
            List<IconAtlasCache.CachedKey> keys = [];
            lock (_gate)
            {
                if (!force && !_dirty)
                {
                    return false;
                }

                // Slots are renumbered densely in file order, and only the
                // ones some key shows are written.
                var renumbered = new int[_nextSlot];
                Array.Fill(renumbered, -1);
                int Number(int slot)
                {
                    if (renumbered[slot] < 0)
                    {
                        renumbered[slot] = slots.Count;
                        slots.Add(_slotData[slot]!);
                    }

                    return renumbered[slot];
                }

                foreach (var (key, entry) in _types)
                {
                    if (entry.Slot >= 0 && _slotData[entry.Slot] is not null && entry.State is KeyState.Ready or KeyState.Cached)
                    {
                        keys.Add(new IconAtlasCache.CachedKey(key, KindOf(key), Number(entry.Slot)));
                    }
                }

                // Files most recently drawn first, so a smaller region next
                // time keeps the ones that matter.
                for (var slot = _lruHead; slot >= 0; slot = _lruNext[slot])
                {
                    if (_slotFileKeys[slot] is not { Count: > 0 } files || _slotData[slot] is null)
                    {
                        continue;
                    }

                    foreach (var key in files)
                    {
                        keys.Add(new IconAtlasCache.CachedKey(key, IconKeyKind.File, Number(slot)));
                    }
                }

                _dirty = false;
            }

            if (IconAtlasCache.Write(path, slots, keys))
            {
                return true;
            }

            lock (_gate)
            {
                _dirty = true;
            }

            return false;
        }
    }

    private void SaveQuietly()
    {
        try
        {
            SaveIfDirty();
        }
        catch (Exception)
        {
            // A timer thread or the app closing: a cache that could not be
            // written is only a slower next start.
        }
    }

    private static ReadOnlySpan<char> Trim(string extension)
        => extension.Length > 0 && extension[0] == '.' ? extension.AsSpan(1) : extension.AsSpan();

    /// <summary>
    /// The one list the folder list's icons are keyed by as well
    /// (<see cref="Services.ShellIconService.IsPathSpecificIcon"/>), so a file
    /// shows its own icon in every view or in none.
    /// </summary>
    private static bool IsPerFileType(ReadOnlySpan<char> extension) => Services.ShellIconService.IsPathSpecificIcon(extension);

    private static string TypeKey(ReadOnlySpan<char> extension)
    {
        Span<char> lower = extension.Length <= 64 ? stackalloc char[extension.Length] : new char[extension.Length];
        extension.ToLowerInvariant(lower);
        return new string(lower);
    }

    private static IconKeyKind KindOf(string key) => key.StartsWith('*') ? IconKeyKind.Stock : IconKeyKind.Type;

    private enum KeyState : byte
    {
        /// <summary>Asked for, not arrived.</summary>
        Pending,

        /// <summary>Has its slot.</summary>
        Ready,

        /// <summary>Has its slot from the disk cache; checked with the Shell the first time it is drawn.</summary>
        Cached,

        /// <summary>A type the Shell had nothing for, and no generic icon to fall back to.</summary>
        Failed,

        /// <summary>A file the Shell had no icon of its own for: its type's shows.</summary>
        UseType,

        /// <summary>A file that arrived when every file slot was on screen; asked again later.</summary>
        Denied
    }

    private enum SlotRegion : byte
    {
        Free,
        Type,
        File
    }

    private struct KeyEntry(string key, int slot, KeyState state, IconPriority queued)
    {
        public readonly string Key = key;
        public int Slot = slot;
        public KeyState State = state;
        public IconPriority Queued = queued;
        public long Frame;
    }
}
