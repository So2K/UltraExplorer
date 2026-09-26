using System.Diagnostics;
using UltraExplorer.Services;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// The process's two atlases for the names on the canvas - the glyphs every
/// name is drawn from and the icons of the file types - made once, at start,
/// and shared by every canvas and every graphics card.
///
/// <see cref="StartWarmUp"/> runs from the application's start on a thread of
/// its own, alongside the GPU warm-up (<see cref="GpuBootstrap"/>): the font
/// faces are resolved, the glyph atlas is read from its cache file - or, the
/// first time and after a font update, rasterised in parallel and written -
/// the icon atlas is read from its cache and the common file types queued to
/// its Shell workers, and the code that emits a frame's labels is run once so
/// it is compiled before a frame needs it.  Each card's warm-up
/// (<see cref="RegisterWarmUp"/>) then waits for that - it has nearly always
/// finished, the card takes longer - and makes the card's copies of both
/// atlases, so a card is handed out with everything the labels draw from
/// already on it.
///
/// If the atlases cannot be made (no DirectWrite, a font that will not
/// load), <see cref="Current"/> stays null and the canvas draws its cells on
/// the GPU and its names with WPF, as before the labels moved.
///
/// <see cref="Shutdown"/>, at exit, writes what the atlases learned during
/// the run: glyphs made after start and icons found, for the next start.
/// </summary>
internal static class GpuLabelAtlases
{
    /// <summary>How long a card's warm-up waits for the atlases before it goes on without them.</summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);

    private static readonly object Gate = new();
    private static readonly TaskCompletionSource<LabelAtlases?> ReadySource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Thread? _worker;
    private static volatile LabelAtlases? _current;

    /// <summary>The atlases once they are ready; null before that, and for good if they could not be made.</summary>
    public static LabelAtlases? Current => _current;

    /// <summary>Completes with the atlases, or with null when they could not be made.</summary>
    public static Task<LabelAtlases?> Ready => ReadySource.Task;

    public static bool IsStarted
    {
        get
        {
            lock (Gate)
            {
                return _worker is not null;
            }
        }
    }

    /// <summary>What the warm-up did and how long it took, for the bench report.</summary>
    public static string Report { get; private set; } = "not started";

    /// <summary>Starts making the atlases on a thread of their own, once per process.  Returns at once.</summary>
    public static void StartWarmUp()
    {
        lock (Gate)
        {
            if (_worker is not null)
            {
                return;
            }

            _worker = new Thread(WarmUp)
            {
                IsBackground = true,
                Name = "Label atlases warm-up"
            };
            _worker.Start();
        }
    }

    /// <summary>
    /// Adds the atlases to every card's warm-up: waits for them, then makes
    /// the card's texture of each from the copies in memory.  Register before
    /// the renderer's (<see cref="NestedGpuRenderer.RegisterWarmUp"/>), whose
    /// warm-up frame samples them, and before <see cref="GpuBootstrap.Start"/>.
    /// </summary>
    public static void RegisterWarmUp() => GpuBootstrap.RegisterWarmUp(AttachTo);

    /// <summary>
    /// Makes the textures of both atlases on <paramref name="devices"/> -
    /// kept by the set and disposed with it.  Waits for the atlases first;
    /// without them (they failed, or took too long) the set goes out without
    /// and the canvas draws its names with WPF.
    /// </summary>
    public static void AttachTo(GpuDeviceSet devices)
    {
        StartWarmUp();
        if (!Ready.Wait(ReadyTimeout) || Ready.Result is not { } atlases)
        {
            return;
        }

        devices.Attach(set => atlases.Icons.CreateTexture(set.Device));
        devices.Attach(set => new GlyphAtlasTexture(set.Device, atlases.Glyphs));
    }

    /// <summary>
    /// Writes what the atlases learned during the run, for the next start:
    /// the glyph cache when glyphs were made after it was read, the icon
    /// cache when icons changed.  At the application's exit, after the cards
    /// (whose textures read the atlases) have been let go.
    /// </summary>
    public static void Shutdown()
    {
        if (_current is not { } atlases)
        {
            return;
        }

        try
        {
            if (atlases.Glyphs.EntryCount > atlases.GlyphsAtStart && atlases.GlyphCachePath is { } path)
            {
                GlyphCacheFile.TrySave(atlases.Glyphs, path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Only a lost cache: the next start rasterises again.
        }

        // Stops the Shell workers and writes the icon cache if it changed.
        atlases.Icons.Dispose();
    }

    private static void WarmUp()
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var faces = FaceRegistry.Shared;
            var facesMs = clock.Elapsed.TotalMilliseconds;

            var icons = new IconAtlas();
            var iconsFromCache = icons.LoadCache();
            icons.PrefetchCommonTypes();
            var iconsMs = clock.Elapsed.TotalMilliseconds - facesMs;

            var glyphs = new GlyphAtlas(faces);
            var cachePath = GlyphAtlas.DefaultCachePath(faces);
            var warm = glyphs.WarmUp(cachePath);
            var glyphsMs = clock.Elapsed.TotalMilliseconds - facesMs - iconsMs;

            var atlases = new LabelAtlases(faces, glyphs, icons, cachePath, glyphs.EntryCount);

            // The code of a frame's labels, once, here rather than in the
            // first frame that draws them: shaping, trimming, emitting.
            var jitStarted = clock.Elapsed.TotalMilliseconds;
            new GpuLabelTarget(faces, new TextShaper(faces), glyphs, icons).WarmUp();
            var jitMs = clock.Elapsed.TotalMilliseconds - jitStarted;

            Report = $"label atlases ready in {clock.Elapsed.TotalMilliseconds:F1} ms: faces {facesMs:F1} ms, icons {iconsMs:F1} ms ({(iconsFromCache ? $"{icons.SlotCount} from the cache" : "no cache")}), "
                + $"glyphs {glyphsMs:F1} ms ({(warm.FromCache ? "from the cache" : $"{warm.Glyphs} rasterised in {warm.Elapsed.TotalMilliseconds:F1} ms, saved in {warm.Saving.TotalMilliseconds:F1} ms")}, {glyphs.PageCount} pages), first frame's code {jitMs:F1} ms";
            PerfLog.Value("gpu.atlases.ms", clock.Elapsed.TotalMilliseconds);
            _current = atlases;
            ReadySource.TrySetResult(atlases);
        }
        catch (Exception ex)
        {
            // Everything is caught: this is a thread of its own, where an
            // exception would end the program.  The names stay WPF's.
            Report = $"label atlases could not be made: {ex.Message}";
            ReadySource.TrySetResult(null);
        }
    }
}

/// <summary>The glyph and icon atlases the names on the canvas are drawn from, one of each per process.</summary>
internal sealed class LabelAtlases(FaceRegistry faces, GlyphAtlas glyphs, IconAtlas icons, string? glyphCachePath, int glyphsAtStart)
{
    public FaceRegistry Faces { get; } = faces;

    public GlyphAtlas Glyphs { get; } = glyphs;

    public IconAtlas Icons { get; } = icons;

    /// <summary>Where the glyph atlas was read from or written to at start, and is written back to at exit.</summary>
    public string? GlyphCachePath { get; } = glyphCachePath;

    /// <summary>Glyphs the atlas held after its start; more at exit means the cache is written again.</summary>
    public int GlyphsAtStart { get; } = glyphsAtStart;
}
