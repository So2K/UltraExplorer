using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

[Flags]
public enum NestedBeaconKind
{
    None = 0,
    Colour = 1,
    Note = 2,
    Pinned = 4,
    Active = 8,
    Search = 16
}

/// <summary>
/// Something the user put on a folder so it can be found again: a colour, a
/// note, a pin in the navigation pane.  On the canvas it is a beacon - a mark
/// of fixed screen size drawn wherever the folder is, however small the folder
/// itself has become.
/// </summary>
public sealed record NestedBeacon(string Path, NestedBeaconKind Kind, Color Colour, string Label, string Note = "");

/// <summary>
/// What is under a point: a folder, or one of the files in it.  Bounds are the
/// folder's cell or the file's tile; the flag says the point is on a folder's
/// title band (always true for a file).
/// </summary>
/// <remarks>
/// A file is held by name as well as by position.  Between a press and the
/// drag it starts, the folder can be read again and its files shift; the
/// name is what makes sure the file dragged is the file pressed.
/// </remarks>
public readonly record struct NestedHit(NestedFolder Folder, Rect Bounds, bool IsOnHeader, int FileIndex = -1, string? FileName = null)
{
    public bool IsFile => FileName is not null;

    /// <summary>The file, found again by name if the folder's files moved since the hit.</summary>
    public NestedFile File
    {
        get
        {
            var files = Folder.Files;
            if (FileIndex >= 0 && FileIndex < files.Count && files[FileIndex].Name == FileName)
            {
                return files[FileIndex];
            }

            foreach (var file in files)
            {
                if (file.Name == FileName)
                {
                    return file;
                }
            }

            return new NestedFile(FileName ?? string.Empty, false, 0);
        }
    }

    /// <summary>The path of whatever was hit.</summary>
    public string Path => FileName is not null ? System.IO.Path.Combine(Folder.FullPath, FileName) : Folder.FullPath;
}

/// <summary>Bytes a frame of the nested canvas allocated on the UI thread, by layer (see <see cref="NestedCanvas.LastAllocations"/>).</summary>
public readonly record struct FrameAllocations(long Scene, long Labels, long Decor, long Present);

/// <summary>
/// The nested canvas: every folder is a cell and its sub-folders are smaller
/// cells inside it, all the way down.  See <see cref="NestedLayout"/> for the
/// geometry and <see cref="NestedTree"/> for how folders are read.
///
/// <para><b>The camera.</b> Cells shrink by a factor of two to a hundred per
/// level, so a folder fifteen levels down can be a trillionth of the drive
/// that holds it.  No single coordinate space can hold both at once - WPF
/// composes in single precision, and even a double runs out - so the camera
/// is not a point in one.  It is a folder, the <i>anchor</i>, and that
/// folder's rectangle on screen.  Every other cell is placed relative to the
/// anchor: its ancestors by walking up and dividing, everything else by
/// walking down from the nearest of those and multiplying.  After every move
/// the anchor is re-chosen as the deepest folder that still contains the
/// middle of the view and is at least half as wide as it, so the numbers in
/// play always describe things near screen size.</para>
///
/// <para><b>Drawing.</b> A big folder is tens of thousands of rectangles,
/// which WPF would charge for one call at a time, so the cells are not WPF's.
/// In a window whose graphics card is ready they are GPU instances - one
/// sixty-four byte record per cell or tile, drawn with one instanced call into
/// a texture WPF shows through a D3DImage (<see cref="GpuSink"/>,
/// <see cref="NestedGpuRenderer"/>, <see cref="NestedSurface"/>).  Anywhere
/// else - no window, no usable card, the CPU chosen, the card lost - they are
/// filled into a pixel buffer by hand (<see cref="NestedRaster"/>), the
/// picture as it always was and the one the GPU's is measured against.  The
/// walk that decides what to draw is the same for both.  The names on the
/// cells follow the cells: on the GPU they are glyphs from a distance-field
/// atlas and icons from an icon atlas, drawn in the same present
/// (<see cref="GpuLabelTarget"/>); on the CPU they are WPF's text and images,
/// as they always were.  The selection, the outlines and the beacons are
/// WPF's on top either way.  Nothing is kept between frames except the
/// camera: a frame is a walk from the root down through whatever is on
/// screen and big enough to see, so its cost is what is visible, not what
/// has been read.</para>
///
/// <para><b>Reading.</b> A folder is read the first time it is drawn wide
/// enough for its contents to be worth drawing; the walk asks the tree, and
/// the tree reads it in the background and says when it is done.</para>
/// </summary>
public sealed partial class NestedCanvas : FrameworkElement, IFrameDriver
{
    /// <summary>A folder this wide on screen (DIPs) is read, so its sub-folders can be drawn.</summary>
    public const double LoadPixels = 20;

    /// <summary>Anything narrower than this (DIPs) is not drawn at all.</summary>
    public const double MinimumCellPixels = 0.9;

    /// <summary>Title bands at least this tall (DIPs) can be grabbed to drag the folder.</summary>
    public const double HeaderGrabPixels = 10;

    /// <summary>A file tile this tall (DIPs) carries its name, and can be picked up.</summary>
    public const double FileLabelPixels = 11;

    private const double MaximumFontSize = 30;
    private const double MinimumHeaderFont = 8.5;
    private const double PillFontSize = 9.5;
    private const double PillMinimumWidth = 56;
    private const int MaximumCellsPerFrame = 400_000;
    private const int MaximumTilesPerFrame = 250_000;
    private int _foldersDrawn;
    private long _tilesDrawn;

    private static readonly Typeface TextFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface TextFaceBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface IconFace = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly uint CanvasColour = 0xFF111315;

    // The colours of the names, as colours for whatever draws them and as the
    // brushes WPF draws them with.
    private static readonly Color TextColour = Color.FromRgb(0xF2, 0xF2, 0xF2);
    private static readonly Color TextDimColour = Color.FromRgb(0x9A, 0x9A, 0x9A);
    private static readonly Color DangerColour = Color.FromRgb(0xEF, 0x5A, 0x68);
    private static readonly Color StarColour = Color.FromRgb(0xFF, 0xD6, 0x6B);
    private static readonly Color FolderColour = Color.FromRgb(0xE3, 0xB3, 0x41);
    private static readonly Color PillColour = Color.FromArgb(0xD8, 0x14, 0x16, 0x18);
    private static readonly Brush TextBrush = Frozen(TextColour);
    private static readonly Brush TextDimBrush = Frozen(TextDimColour);
    private static readonly Brush DangerBrush = Frozen(DangerColour);
    private static readonly Brush AccentBrush = Frozen(Color.FromRgb(0x60, 0xCD, 0xFF));
    private static readonly Brush PillBrush = Frozen(PillColour);
    private static readonly Brush TipBrush = Frozen(Color.FromArgb(0xF0, 0x20, 0x22, 0x25));
    private static readonly Brush DropFillBrush = Frozen(Color.FromArgb(0x55, 0x24, 0x3E, 0x4A));
    private static readonly Brush StarBrush = Frozen(StarColour);
    private static readonly Pen SelectionPen = FrozenPen(Color.FromRgb(0x60, 0xCD, 0xFF), 2);
    private static readonly Pen ActivePen = FrozenPen(Color.FromArgb(0xB0, 0x60, 0xCD, 0xFF), 1.5);
    private static readonly Pen HoverPen = FrozenPen(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Pen DropPen = FrozenPen(Color.FromRgb(0x8D, 0xDE, 0xFF), 2);
    private static readonly Pen TipPen = FrozenPen(Color.FromRgb(0x3A, 0x3A, 0x3A), 1);
    private static readonly Pen BeaconRimPen = FrozenPen(Color.FromArgb(0xE0, 0x10, 0x10, 0x10), 1.5);
    private static readonly Pen BeaconHaloPen = FrozenPen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 1);

    private readonly NestedRaster _raster = new();
    private readonly RasterSink _rasterSink;
    private readonly WpfLabelTarget _wpfLabels;

    /// <summary>
    /// Where the walk puts its cells and tiles: the sink <see cref="WalkScene"/>
    /// was handed for the walk under way (or the last one), and never null, so
    /// the walk need not ask.
    /// </summary>
    private SceneSink _sink;

    private readonly Dictionary<NestedFolder, (double X, double Y, double W)> _chain = [];
    private readonly List<LabelJob> _labels = [];
    private readonly List<FileLabelJob> _fileLabels = [];
    private readonly Dictionary<string, (uint Body, uint Stripe, uint Speck)> _filePalette = new(StringComparer.Ordinal);
    private readonly HashSet<NestedFolder> _labelled = [];
    private readonly List<Hotspot> _hotspots = [];
    private Dictionary<TextKey, FormattedText> _textCache = [];
    private Dictionary<TextKey, FormattedText> _oldTextCache = [];

    // What the names are made of, kept from frame to frame: the paths and
    // marks of the files on screen, and the words for sizes and counts.
    private const int MaximumKeptFiles = 8192;
    private Dictionary<(string Folder, string Name), FileFacts> _fileFacts = new(ReferenceKeyComparer.Instance);
    private Dictionary<(string Folder, string Name), FileFacts> _oldFileFacts = new(ReferenceKeyComparer.Instance);
    private readonly NumberTexts _sizeTexts = new(8192);
    private readonly NumberTexts _detailTexts = new(2048);
    private readonly NumberTexts _noteTexts = new(256);
    private double _minCell = MinimumCellPixels;

    private const int FilterSelf = 1;
    private const int FilterInside = 2;
    private const int FilterUnknown = 4;
    private static readonly uint FilterColour = 0xFF4CC9D8;
    private static readonly Pen FilterPen = FrozenPen(Color.FromRgb(0x4C, 0xC9, 0xD8), 1.5);
    private Func<string, bool>? _filter;
    private string _filterText = string.Empty;
    private int _filterStamp;
    private int _filterCursor = -1;

    /// <summary>
    /// The <see cref="NestedTree.SortGeneration"/> the matches are listed in
    /// the order of, or -1 when they were gathered from folders placed for
    /// different orders - while the tree's pass after a change was still
    /// going.  Anything else and stepping through them would jump around a
    /// picture the user has just had put in another order.
    /// </summary>
    private int _filterOrderGeneration = -1;
    private bool _filterReorderWaiting;
    private readonly List<string> _filterMatches = [];
    private readonly HashSet<string> _filterMatchSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Rect> _filterOutlines = [];

    /// <summary>Text sizes laid out per octave: every fourth root of two, each within 9 % of any size drawn.</summary>
    private const int LevelsPerOctave = 4;

    /// <summary>New text layouts allowed in one frame while the camera moves.</summary>
    private const int MotionTextBudget = 48;

    private int _textBudget = int.MaxValue;
    private bool _textDeferred;
    private double _minTile = 2.5;
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _resolving = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unresolvable = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pinned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Color, Brush> _brushes = [];

    // The picture is four layers, each redrawn only when what it shows
    // changed: the cells (the GPU's surface, or a bitmap), the names on them,
    // the marks and outlines over them, and the pointer's outline and tag on
    // top.  A click that only moves the selection redraws the third; an icon
    // arriving, the second; moving the mouse, the fourth.  Only the camera or
    // the tree moving repaints the cells.
    private readonly DrawingVisual _sceneVisual = new();
    private readonly DrawingVisual _labelVisual = new();
    private readonly DrawingVisual _decorVisual = new();
    private readonly DrawingVisual _overlay = new();
    private readonly List<LabelGrab> _labelHotspots = [];
    private Layers _dirty = Layers.All;
    private bool _frameHooked;
    private WriteableBitmap? _shownBitmap;
    private long _lastMotion;
    private double _lod = 1;
    private bool _lodDegraded;
    private bool _textAnimated;
    private IReadOnlyList<NestedBeacon> _beacons = [];

    private NestedTree? _tree;
    private WriteableBitmap? _bitmap;

    // The scene on the GPU (see "the GPU" below): the surface WPF shows in
    // place of the bitmap, the renderer and instance lists that fill it, and
    // which surface and texture the scene layer was last recorded with.
    private NestedSurface? _surface;
    private NestedGpuRenderer? _renderer;
    private NestedGpuFrame? _gpuFrame;
    private GpuSink? _gpuSink;
    private SurfaceDrawer? _drawSurface;
    private NestedSurface? _shownSurface;
    private int _shownSurfaceVersion;
    private bool _surfaceLost;
    private bool _presentPending;
    private bool _gpuHooked;

    // The names on the GPU (see "the names on the GPU" below): the target
    // that turns the label calls into instances, the atlases it draws from,
    // whether the last label layer went there, and whether WPF's label layer
    // still holds a recording to be cleared once the GPU's is on screen.
    private GpuLabelTarget? _gpuLabels;
    private LabelAtlases? _labelAtlases;
    private bool _labelEventsHooked;
    private bool _labelsOnGpu;
    private bool _labelVisualRecorded;
    private int _labelMaterialArrived;
    private Window? _gpuWindow;
    private DispatcherTimer? _monitorCheck;
    private DispatcherTimer? _gpuRetry;
    private double _scaleX = 1;
    private double _scaleY = 1;
    private double _viewWidth;
    private double _viewHeight;
    private int _paletteStamp;

    private NestedFolder? _anchor;
    private double _ax;
    private double _ay;
    private double _aw;
    private bool _hasCamera;
    private bool _cameraTouched;

    /// <summary>
    /// How long one frame may spend placing folders again for a new order
    /// before it draws the rest as they were.  A change of order leaves every
    /// folder on screen to be placed again, and at an overview that is
    /// thousands of them; the frame that does them all at once is the hitch
    /// the change must not have.  What is left over is drawn in its previous
    /// order - still a whole, consistent picture - and handed to the tree to
    /// place first in its background pass; the next frame places the next
    /// few milliseconds' worth.
    ///
    /// Only the placing itself counts, never the drawing around it.  Counted
    /// from the start of the frame, a scene that took longer than this to
    /// paint before its walk reached a folder would never place that folder
    /// at all - and the next frame, walking the same way, would not either.
    /// </summary>
    private static readonly long DrawRelayoutTicks = System.Diagnostics.Stopwatch.Frequency * 3 / 1000;

    /// <summary>At most this many folders drawn out of date are handed to the tree to place first; the rest wait their turn in its pass.</summary>
    private const int MaximumPlacedFirst = 20_000;

    private long _relayoutAllowance = long.MaxValue;
    private long _relayoutSpent;
    private bool _drewStale;
    private bool _inFrameLoop;
    private readonly List<NestedFolder> _drawnStale = [];

    private Flight? _flight;

    private PressKind _press;
    private Point _pressPoint;
    private Point _panLast;
    private bool _pressMoved;
    private NestedHit? _pressHit;
    private Hotspot? _pressHotspot;

    private NestedHit? _hover;
    private Point _hoverPoint;
    private NestedFolder? _dropTarget;
    private string _activePath = string.Empty;
    private Func<string, FolderMark>? _markLookup;
    private bool _beaconResolverRunning;

    public NestedCanvas()
    {
        _rasterSink = new RasterSink(_raster);
        _sink = _rasterSink;
        _wpfLabels = new WpfLabelTarget(this);
        _onWake = OnWake;
        _clockNow = () => _clock.Now;
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        // The cells are drawn pixel for pixel and must never be smoothed; the
        // icons on the names are scaled, and must be.
        RenderOptions.SetBitmapScalingMode(_sceneVisual, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetBitmapScalingMode(_labelVisual, BitmapScalingMode.HighQuality);
        ClipToBounds = true;
        AddVisualChild(_sceneVisual);
        AddVisualChild(_labelVisual);
        AddVisualChild(_decorVisual);
        AddVisualChild(_overlay);
        Unloaded += (_, _) =>
        {
            StopFrames();
            UnhookGpu();
        };
        Loaded += (_, _) =>
        {
            HookGpu();
            RequestFrame(Layers.All);
        };
    }

    protected override int VisualChildrenCount => 4;

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _sceneVisual,
        1 => _labelVisual,
        2 => _decorVisual,
        3 => _overlay,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    // ---- surface -------------------------------------------------------------

    /// <summary>A click on a folder or a file: the path to select, and whether Ctrl was held.</summary>
    public event Action<string, bool>? SelectRequested;

    /// <summary>
    /// A double-click or Enter.  On a folder the canvas has already flown into
    /// it; a file is the window's to open.
    /// </summary>
    public event Action<NestedHit>? OpenRequested;

    /// <summary>
    /// A right-click.  Null outside every cell; the flag says the click was in
    /// a folder's open space rather than on the folder itself, which is the
    /// difference between "this folder" and "in this folder".
    /// </summary>
    public event Action<NestedHit?, bool, Point>? ContextMenuRequested;

    /// <summary>A folder's title or a file's tile was dragged past the threshold: a real file drag should start.</summary>
    public event Action<string>? DragRequested;

    /// <summary>The camera moved.  Raised often; listeners debounce.</summary>
    public event Action? CameraChanged;

    public NestedTree? Tree
    {
        get => _tree;
        set
        {
            if (ReferenceEquals(_tree, value))
            {
                return;
            }

            if (_tree is not null)
            {
                _tree.Changed -= OnTreeChanged;
                _tree.FolderLoaded -= OnFolderLoadedForFilter;
                _tree.FolderLoaded -= OnFolderLoadedForGpu;
                _tree.SortChanged -= OnTreeSortChanged;
                StopDriving(_tree.Driver);
                if (ReferenceEquals(_tree.Clock, _clockNow))
                {
                    _tree.Clock = NestedTree.StopwatchClock;
                }
            }

            _tree = value;
            _hasCamera = false;
            if (_tree is not null)
            {
                _tree.Changed += OnTreeChanged;
                _tree.FolderLoaded += OnFolderLoadedForFilter;
                _tree.FolderLoaded += OnFolderLoadedForGpu;
                _tree.SortChanged += OnTreeSortChanged;

                // Its finished reads are taken in at the start of this
                // canvas's frames, and what it stamps with the time is on the
                // frames' clock.
                Drive(_tree.Driver);
                _tree.Clock = _clockNow;
            }

            RequestFrame(Layers.All);
        }
    }

    /// <summary>
    /// The order sub-folders and files are placed in, row by row, in every
    /// folder; the tree's, which is where it lives.  Setting it keeps the
    /// folder being looked at where it is on screen and reorders what is
    /// inside it.
    /// </summary>
    public ItemSort Sort
    {
        get => _tree?.Sort ?? ItemSort.Default;
        set => _tree?.SetSort(value);
    }

    /// <summary>Space is held: a left drag pans whatever it starts on.</summary>
    public bool IsSpacePanArmed { get; set; }

    /// <summary>The folder being dropped onto, lit while a drag hovers over it.</summary>
    public NestedFolder? DropTarget
    {
        get => _dropTarget;
        set
        {
            if (!ReferenceEquals(_dropTarget, value))
            {
                _dropTarget = value;
                RequestFrame(Layers.Decor);
            }
        }
    }

    /// <summary>
    /// "×12" - how far in the view is, against the whole of This PC fitting
    /// the screen.  A percentage stops meaning anything a few levels down.
    /// </summary>
    public string ZoomText
    {
        get
        {
            var root = RootRect();
            if (root is null || _viewWidth <= 0)
            {
                return "Fit";
            }

            var ratio = root.Value.W / FitWidth();
            return ratio switch
            {
                < 0.995 => $"×{ratio:0.00}",
                < 10 => $"×{ratio:0.#}",
                < 1_000 => $"×{ratio:0}",
                < 1_000_000 => $"×{ratio / 1_000:0.#}k",
                < 1_000_000_000 => $"×{ratio / 1_000_000:0.#}M",
                _ => $"×{ratio:0.#e0}"
            };
        }
    }

    /// <summary>The folder the camera is fixed to.</summary>
    public NestedFolder? Anchor => _anchor;

    /// <summary>Cells drawn in the last frame, for tests and the perf log.</summary>
    public int DrawnCellCount { get; private set; }

    /// <summary>How long the last frame took to build on the UI thread.</summary>
    public double LastRenderMilliseconds { get; private set; }

    /// <summary>How long the names and icons took to record in the last frame that drew them.</summary>
    public double LastLabelsMilliseconds { get; private set; }

    /// <summary>How long the marks, outlines and trail took to record in the last frame.</summary>
    public double LastDecorMilliseconds { get; private set; }

    /// <summary>Texts laid out afresh in the last frame, rather than found already laid out.</summary>
    public int NewTextLayouts { get; private set; }

    /// <summary>How long the last frame that drew the cells spent placing folders for a new order, drawing aside.</summary>
    public double LastPlacingMilliseconds { get; private set; }

    /// <summary>Whether the last frame that drew the cells drew some in an order since replaced, its allowance for placing spent.</summary>
    public bool DrewOutOfDate { get; private set; }

    /// <summary>Frames built so far; a benchmark tells a new frame from an old one by it.</summary>
    public long RenderCount { get; private set; }

    /// <summary>The whole of the last frame the canvas's loop drew, on the UI thread: scene, names, marks and pointer layers.</summary>
    public double LastFrameMilliseconds { get; private set; }

    /// <summary>Whether the scene layer shows the GPU's picture (true) or the CPU raster's bitmap.</summary>
    public bool IsSceneOnGpu => _shownSurface is not null && ReferenceEquals(_shownSurface, _surface);

    /// <summary>Why the last frame drew its scene where it did: the GPU is ready, or why it was not used.</summary>
    public string RendererReason { get; private set; } = GpuBootstrap.ReasonNotOnScreen;

    /// <summary>The card the scene is drawn on, or empty while it is drawn on the CPU.</summary>
    public string RendererAdapter => IsSceneOnGpu ? _surface!.Devices.AdapterName : string.Empty;

    /// <summary>How long the last scene's walk took on the UI thread, filling the GPU's instances or the raster's pixels.</summary>
    public double LastWalkMilliseconds { get; private set; }

    /// <summary>The last GPU frame's present on the UI thread, image lock to unlock (0 on the CPU path).</summary>
    public double LastPresentMilliseconds { get; private set; }

    /// <summary>How long the last GPU frame waited for WPF's render thread to let go of the previous one (the image lock).</summary>
    public double LastLockMilliseconds { get; private set; }

    /// <summary>How long the last GPU frame's present waited for the GPU to finish it.</summary>
    public double LastGpuWaitMilliseconds { get; private set; }

    /// <summary>The GPU's own time for a recent frame, from timestamps; NaN until one has been measured.</summary>
    public double LastGpuMilliseconds { get; private set; } = double.NaN;

    /// <summary>Instances drawn by the last GPU frame.</summary>
    public int LastGpuInstances { get; private set; }

    /// <summary>GPU frames skipped because WPF's render thread still held the previous one; they are drawn a frame later.</summary>
    public int SkippedPresents { get; private set; }

    /// <summary>Whether the names are drawn on the GPU with the cells (true) or by WPF over them.</summary>
    public bool AreLabelsOnGpu => _labelsOnGpu;

    /// <summary>
    /// How long the last GPU frame spent sending data to the card on the UI
    /// thread: the instances into their buffers, arrived icons and new
    /// glyphs into the atlases' textures.
    /// </summary>
    public double LastUploadMilliseconds { get; private set; }

    /// <summary>Glyphs and icons drawn by the last GPU frame's names.</summary>
    public int LastGpuGlyphs { get; private set; }

    public int LastGpuIcons { get; private set; }

    /// <summary>What the rest of the app has selected; drawn with the selection outline.</summary>
    public void SetSelection(IEnumerable<string> paths, string activePath)
    {
        _selected.Clear();
        foreach (var path in paths)
        {
            _selected.Add(path);
        }

        _activePath = activePath ?? string.Empty;
        RequestFrame(Layers.Decor);
    }

    public void SetBeacons(IReadOnlyList<NestedBeacon> beacons)
    {
        _beacons = beacons;
        _unresolvable.Clear();
        _pinned.Clear();
        foreach (var beacon in beacons)
        {
            if ((beacon.Kind & NestedBeaconKind.Pinned) != 0)
            {
                _pinned.Add(beacon.Path);
            }
        }

        RequestFrame(Layers.Labels | Layers.Decor);
        _ = ResolveBeaconsAsync();
    }

    /// <summary>Everything again, from the cells up.</summary>
    public void Redraw() => RequestFrame(Layers.All);

    /// <summary>
    /// The folder a path names, or the folder and index of the file it names,
    /// if that folder has been read.  The index is the file's tile in the
    /// current order; the tree finds it by a binary search over the names,
    /// which stay in name order whatever order the tiles are in.
    /// </summary>
    public (NestedFolder Folder, int FileIndex)? Resolve(string path)
    {
        if (_tree is null || string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (_tree.Find(path) is { } folder)
        {
            return (folder, -1);
        }

        var parentPath = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parentPath) || _tree.Find(parentPath) is not { } parent)
        {
            return null;
        }

        var index = _tree.FindFileIndex(parent, Path.GetFileName(path));
        return index >= 0 ? (parent, index) : null;
    }

    /// <summary>
    /// <see cref="Resolve"/> without placing anything for the current order:
    /// the tile a file has in its folder as the folder is placed right now.
    /// For marks drawn over the picture, which has to show them where it shows
    /// the tiles; placing every marked folder's contents for them would also
    /// be work without an allowance, dozens of big folders in the one frame
    /// after a click.
    /// </summary>
    private (NestedFolder Folder, int FileIndex)? ResolveAsPlaced(string path)
    {
        if (_tree is null || string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (_tree.Find(path) is { } folder)
        {
            return (folder, -1);
        }

        var parentPath = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parentPath) || _tree.Find(parentPath) is not { } parent)
        {
            return null;
        }

        var index = NestedTree.FileIndexAsPlaced(parent, Path.GetFileName(path));
        return index >= 0 ? (parent, index) : null;
    }

    private static readonly Brush FolderBrush = Frozen(FolderColour);

    private static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color colour, double thickness)
    {
        var pen = new Pen(Frozen(colour), thickness);
        pen.Freeze();
        return pen;
    }
}
