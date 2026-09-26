using System.Globalization;
using System.Windows;
using System.Windows.Input;
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
public sealed class NestedCanvas : FrameworkElement
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
    private int _labelMaterialQueued;
    private readonly Action _requestLabelsFrame;
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
        _requestLabelsFrame = RequestLabelsFrame;
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
            UnhookFrame();
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

    /// <summary>The Shell icon for a file, or null while it is not known yet.</summary>
    public Func<string, ImageSource?>? IconLookup { get; set; }

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
            }

            _tree = value;
            _hasCamera = false;
            if (_tree is not null)
            {
                _tree.Changed += OnTreeChanged;
                _tree.FolderLoaded += OnFolderLoadedForFilter;
                _tree.FolderLoaded += OnFolderLoadedForGpu;
                _tree.SortChanged += OnTreeSortChanged;
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

    /// <summary>Where a colour and a note for a path come from.</summary>
    public Func<string, FolderMark>? MarkLookup
    {
        get => _markLookup;
        set
        {
            _markLookup = value;
            InvalidateMarks();
        }
    }

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

    /// <summary>Marks changed: every cell's colours are worked out again on its next draw.</summary>
    public void InvalidateMarks()
    {
        _paletteStamp++;
        RequestFrame(Layers.All);
    }

    /// <summary>Everything again, from the cells up.</summary>
    public void Redraw() => RequestFrame(Layers.All);

    // ---- filter ---------------------------------------------------------------------

    /// <summary>
    /// Narrows the canvas to names: what matches is lit, what holds a match
    /// keeps its colour so the way to it shows, and everything else fades.
    /// Nothing moves - a filter that re-laid the drive out would lose the one
    /// thing a spatial view is for, knowing where things are.
    ///
    /// Plain text matches anywhere in a name, ignoring case; * and ? make it a
    /// wildcard pattern for the whole name (*.png); several patterns can be
    /// separated by ';'.  Only what has been read can match: a folder not read
    /// yet stays half faded until it is, and then it is judged like the rest.
    /// </summary>
    public void SetFilter(string? text)
    {
        var matcher = CompileFilter(text);
        if (matcher is null && _filter is null)
        {
            return;
        }

        _filter = matcher;
        _filterText = matcher is null ? string.Empty : text!.Trim();
        _filterStamp++;
        _filterCursor = -1;
        _filterMatches.Clear();
        _filterMatchSet.Clear();
        if (matcher is not null && _tree is not null)
        {
            Evaluate(_tree.Root);

            // Gathered while the tree was still placing folders for a new
            // order, the list is in a mixture of orders: it is put in the
            // current one when the placing is done.
            _filterOrderGeneration = _tree.IsSorting ? -1 : _tree.SortGeneration;
            if (_tree.IsSorting)
            {
                _ = ReorderFilterWhenPlacedAsync();
            }
        }

        _paletteStamp++;
        RequestFrame(Layers.All);
        FilterChanged?.Invoke();
    }

    /// <summary>Raised when the filter's matches changed: new text, or folders read since.</summary>
    public event Action? FilterChanged;

    public bool IsFiltering => _filter is not null;

    /// <summary>
    /// Paths of everything matching the filter among what has been read, in
    /// walking order: each folder, then its files and sub-folders as the
    /// current order shows them, left to right and top to bottom.
    /// </summary>
    public IReadOnlyList<string> FilterMatches => _filterMatches;

    /// <summary>Which match the last step went to, or -1.</summary>
    public int FilterCursor => _filterCursor;

    /// <summary>
    /// Goes to the next match (or the previous one), selects it and flies to
    /// where it can be read.  False when nothing matches.  Next is next in
    /// the order on screen, even straight after the order changed.
    /// </summary>
    public bool GoToMatch(int direction)
    {
        if (_filterMatches.Count == 0)
        {
            return false;
        }

        if (_tree is not null && _filterOrderGeneration != _tree.SortGeneration)
        {
            ReorderFilterMatches();
        }

        _filterCursor = ((_filterCursor < 0 && direction < 0 ? 0 : _filterCursor) + direction + _filterMatches.Count) % _filterMatches.Count;
        var path = _filterMatches[_filterCursor];
        if (Resolve(path) is not { } target)
        {
            return false;
        }

        MarkSelected(path);
        SelectRequested?.Invoke(path, false);
        FlyToReadable(target.Folder, target.FileIndex);
        FilterChanged?.Invoke();
        return true;
    }

    private static Func<string, bool>? CompileFilter(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tests = new List<Func<string, bool>>(parts.Length);
        foreach (var part in parts)
        {
            if (part.IndexOfAny(['*', '?']) >= 0)
            {
                var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(part).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
                var regex = new System.Text.RegularExpressions.Regex(
                    pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                tests.Add(regex.IsMatch);
            }
            else
            {
                var fragment = part;
                tests.Add(name => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (tests.Count == 0)
        {
            return null;
        }

        return tests.Count == 1 ? tests[0] : name => tests.Any(test => test(name));
    }

    /// <summary>
    /// Works out, below one folder, what matches and what holds a match.
    /// Returns whether anything in or under it matches.
    /// </summary>
    private bool Evaluate(NestedFolder folder)
    {
        var matcher = _filter!;
        var state = 0;
        if (!folder.IsComputer && matcher(folder.Name))
        {
            state |= FilterSelf;
            AddMatch(folder.FullPath);
        }

        foreach (var file in folder.Files)
        {
            if (matcher(file.Name))
            {
                state |= FilterInside;
                AddMatch(folder.PathOf(file));
            }
        }

        foreach (var child in folder.Children)
        {
            if (Evaluate(child))
            {
                state |= FilterInside;
            }
        }

        if (!folder.IsComputer && !folder.IsLoaded)
        {
            state |= FilterUnknown;
        }

        folder.FilterStamp = _filterStamp;
        folder.FilterState = state;
        return (state & (FilterSelf | FilterInside)) != 0;
    }

    private void AddMatch(string path)
    {
        if (_filterMatchSet.Add(path))
        {
            _filterMatches.Add(path);
        }
    }

    /// <summary>
    /// A folder was read while a filter is on: judge what came in, and let
    /// every folder above it know if something in it matched.
    /// </summary>
    private void OnFolderLoadedForFilter(NestedFolder folder)
    {
        if (_filter is null)
        {
            return;
        }

        var before = _filterMatches.Count;
        if (Evaluate(folder))
        {
            for (var parent = folder.Parent; parent is not null; parent = parent.Parent)
            {
                if (parent.FilterStamp == _filterStamp)
                {
                    parent.FilterState |= FilterInside;
                }
            }
        }

        _paletteStamp++;
        if (_filterMatches.Count != before)
        {
            FilterChanged?.Invoke();
        }
    }

    /// <summary>
    /// Puts the matches in walking order for the current order, keeping the
    /// step the user is on.  Only folders that match or hold a match are
    /// walked - what the filter already found out about each folder says
    /// which - and each is placed for the current order before its files and
    /// sub-folders are read, so this is right even while the tree is still
    /// placing the rest.  A match the walk no longer reaches - its folder
    /// hidden since - keeps its place at the end, as it would have before.
    /// </summary>
    private void ReorderFilterMatches()
    {
        if (_tree is null || _filter is null)
        {
            return;
        }

        var current = _filterCursor >= 0 && _filterCursor < _filterMatches.Count ? _filterMatches[_filterCursor] : null;
        var ordered = new List<string>(_filterMatches.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectMatches(_tree.Root, ordered, seen);
        foreach (var path in _filterMatches)
        {
            if (seen.Add(path))
            {
                ordered.Add(path);
            }
        }

        _filterMatches.Clear();
        _filterMatches.AddRange(ordered);
        _filterMatchSet.Clear();
        _filterMatchSet.UnionWith(ordered);
        _filterCursor = current is null ? -1 : _filterMatches.FindIndex(path => string.Equals(path, current, StringComparison.OrdinalIgnoreCase));
        _filterOrderGeneration = _tree.SortGeneration;
    }

    /// <summary>The matches in and under one folder, in the order <see cref="Evaluate"/> walks it.</summary>
    private void CollectMatches(NestedFolder folder, List<string> into, HashSet<string> seen)
    {
        var state = FilterStateOf(folder);
        if (!folder.IsComputer && (state & FilterSelf) != 0 && seen.Add(folder.FullPath))
        {
            into.Add(folder.FullPath);
        }

        if ((state & FilterInside) == 0)
        {
            return;
        }

        Ensure(folder);
        foreach (var file in folder.Files)
        {
            if (_filter!(file.Name))
            {
                var path = folder.PathOf(file);
                if (seen.Add(path))
                {
                    into.Add(path);
                }
            }
        }

        foreach (var child in folder.Children)
        {
            if ((FilterStateOf(child) & (FilterSelf | FilterInside)) != 0)
            {
                CollectMatches(child, into, seen);
            }
        }
    }

    /// <summary>
    /// Once the tree has placed every folder for its current order, puts the
    /// matches in that order - the list the window shows marks for, and
    /// counts through, then follows what is on screen.  One wait at a time;
    /// an order changed again meanwhile is simply waited for too.
    /// </summary>
    private async Task ReorderFilterWhenPlacedAsync()
    {
        if (_filterReorderWaiting)
        {
            return;
        }

        _filterReorderWaiting = true;
        try
        {
            while (_tree is { } tree && _filter is not null && _filterOrderGeneration != tree.SortGeneration)
            {
                await tree.WhenSortIdleAsync();
                if (!ReferenceEquals(_tree, tree) || _filter is null || tree.IsSorting)
                {
                    continue;
                }

                if (_filterOrderGeneration != tree.SortGeneration)
                {
                    ReorderFilterMatches();
                    FilterChanged?.Invoke();
                }
            }
        }
        finally
        {
            _filterReorderWaiting = false;
        }
    }

    private int FilterStateOf(NestedFolder folder) =>
        folder.FilterStamp == _filterStamp ? folder.FilterState : FilterUnknown;

    /// <summary>Whether a folder is faded by the filter: neither a match nor on the way to one.</summary>
    private bool IsFilteredOut(NestedFolder folder) =>
        _filter is not null && !folder.IsComputer && (FilterStateOf(folder) & (FilterSelf | FilterInside)) == 0;

    /// <summary>A Shell icon arrived: only the names and icons on the cells need drawing again.</summary>
    public void RefreshIcons() => RequestFrame(Layers.Labels);

    /// <summary>
    /// Draws whatever is out of date right now, rather than on the next frame.
    /// For a snapshot of the control, and for a test with no frame loop.
    /// </summary>
    public void RenderNow()
    {
        var layers = _dirty | Layers.All;
        _dirty = Layers.None;

        // A snapshot must hold this frame, not the last one: a GPU frame that
        // found WPF still copying the previous one is tried again at once -
        // each try waits a moment for the copy - before giving up to the loop.
        var drawn = RenderLayers(layers, inMotion: false);
        for (var attempt = 1; !drawn && attempt < 10; attempt++)
        {
            _dirty = Layers.None;
            drawn = RenderLayers(layers, inMotion: false);
        }
    }

    /// <summary>
    /// Draws everything as one frame of the canvas's own loop draws it - with
    /// the loop's allowance for placing folders after a change of order, and
    /// whatever it leaves over drawn as it was - for a test, which has no loop.
    /// </summary>
    internal void RenderAsFrame()
    {
        _dirty = Layers.None;
        _inFrameLoop = true;
        try
        {
            RenderLayers(Layers.All, inMotion: false);
        }
        finally
        {
            _inFrameLoop = false;
        }
    }

    // ---- camera --------------------------------------------------------------

    /// <summary>Shows all of This PC.</summary>
    public void FitAll(bool animated = true)
    {
        if (_tree is null)
        {
            return;
        }

        if (!animated || _viewWidth <= 0)
        {
            StopFlight();
            _anchor = _tree.Root;
            var width = FitWidth();
            _aw = width;
            _ax = (_viewWidth - width) / 2;
            _ay = (_viewHeight - width * NestedLayout.CellHeight) / 2;
            _hasCamera = true;
            _cameraTouched = true;
            AfterCameraMove();
            return;
        }

        FlyTo(_tree.Root, 1, animated: true);
    }

    /// <summary>
    /// Moves the camera so <paramref name="target"/> fills <paramref name="fill"/>
    /// of the view, along the path that keeps the most of both ends in sight -
    /// out, across and in again when they are far apart.
    /// </summary>
    public bool FlyTo(NestedFolder target, double fill = 0.92, bool animated = true)
    {
        EnsureCamera();
        var current = ScreenRectOf(target);
        if (current is null || _viewWidth <= 0)
        {
            return false;
        }

        return FlyToRect(target, FitRect(fill), animated);
    }

    /// <summary>Moves the camera so <paramref name="target"/> ends up exactly at <paramref name="end"/> on screen.</summary>
    private bool FlyToRect(NestedFolder target, (double X, double Y, double W) end, bool animated = true)
    {
        EnsureCamera();
        var current = ScreenRectOf(target);
        if (current is null || _viewWidth <= 0 || !(end.W > 0) || double.IsInfinity(end.W))
        {
            return false;
        }

        if (!animated)
        {
            StopFlight();
            _anchor = target;
            _ax = end.X;
            _ay = end.Y;
            _aw = end.W;
            _cameraTouched = true;
            AfterCameraMove();
            return true;
        }

        _flight = Flight.Create(target, current.Value, end, _viewWidth, _viewHeight);
        _cameraTouched = true;
        RequestFrame(Layers.All);
        return true;
    }

    /// <summary>
    /// Flies to where a folder or a file can actually be read: a file with its
    /// tile tall enough for its name, a folder a third of the view wide - each
    /// centred.  Fitting the container instead left a file among thousands
    /// just as unreadable as before, and every arrow press flew to the same
    /// view again.
    /// </summary>
    private void FlyToReadable(NestedFolder folder, int fileIndex)
    {
        // The tile comes from the folder's own placing and the cell from its
        // parent's: both for the current order.
        Ensure(folder);
        if (folder.Parent is { } holder)
        {
            Ensure(holder);
        }

        if (fileIndex >= 0 && fileIndex < folder.Files.Count && folder.FileGrid is { IsEmpty: false } files)
        {
            var width = 2 * FileLabelPixels / files.TileHeight;
            width = Math.Max(width, FitRect(0.92).W);
            var (fx, fy) = files.Origin(fileIndex);
            var cx = fx + files.TileWidth / 2;
            var cy = fy + files.TileHeight / 2;
            FlyToRect(folder, (_viewWidth / 2 - cx * width, _viewHeight / 2 - cy * width, width));
            return;
        }

        if (folder.Parent is not { IsComputer: false } parent)
        {
            FlyTo(folder, 0.6);
            return;
        }

        var wanted = Math.Max(160, Math.Min(_viewWidth, _viewHeight * NestedLayout.Aspect) * 0.35);
        var parentWidth = wanted / folder.Scale;
        var centreX = folder.OffsetX + folder.Scale / 2;
        var centreY = folder.OffsetY + folder.Scale * NestedLayout.CellHeight / 2;
        FlyToRect(parent, (_viewWidth / 2 - centreX * parentWidth, _viewHeight / 2 - centreY * parentWidth, parentWidth));
    }

    /// <summary>Reads everything on the way to <paramref name="path"/>, then flies to it.</summary>
    public async Task<bool> FlyToPathAsync(string path, double fill = 0.72, bool animated = true)
    {
        if (_tree is null)
        {
            return false;
        }

        var folder = await _tree.RevealAsync(path);
        if (folder is null)
        {
            return false;
        }

        return FlyTo(folder, fill, animated);
    }

    /// <summary>Zooms by <paramref name="factor"/> keeping the point under <paramref name="at"/> still.</summary>
    public void ZoomAt(Point at, double factor)
    {
        EnsureCamera();
        if (_anchor is null)
        {
            return;
        }

        StopFlight();
        ZoomAround(at, factor);
        Normalize();
        ClampZoom(at);
        ClampPan();
        _cameraTouched = true;
        AfterCameraMove();
    }

    public void ZoomBy(double factor) => ZoomAt(new Point(_viewWidth / 2, _viewHeight / 2), factor);

    public void Pan(Vector delta)
    {
        EnsureCamera();
        if (_anchor is null)
        {
            return;
        }

        StopFlight();
        _ax += delta.X;
        _ay += delta.Y;
        Normalize();
        ClampPan();
        _cameraTouched = true;
        AfterCameraMove();
    }

    /// <summary>
    /// The camera as something that can be saved, or null when there is none
    /// to save (never shown, no size yet).  A camera on This PC itself is saved
    /// with an empty path, so an overview comes back as an overview.
    /// </summary>
    public NestedCameraState? CaptureCamera()
    {
        if (_anchor is null || !_hasCamera || _viewWidth <= 0)
        {
            return null;
        }

        var centreX = _viewWidth / 2;
        var centreY = _viewHeight / 2;
        return new NestedCameraState(
            _anchor.IsComputer ? string.Empty : _anchor.FullPath,
            (_ax - centreX) / _viewWidth,
            (_ay - centreY) / _viewWidth,
            _aw / _viewWidth);
    }

    /// <summary>
    /// Puts the camera back where a previous session left it, once the folders
    /// on the way there have been read - unless the user has moved it since.
    /// </summary>
    public async Task RestoreCameraAsync(NestedCameraState state)
    {
        if (_tree is null || !(state.Width > 0) || double.IsInfinity(state.Width))
        {
            return;
        }

        _cameraTouched = false;
        var isRoot = string.IsNullOrWhiteSpace(state.AnchorPath);
        var folder = isRoot ? _tree.Root : await _tree.RevealAsync(state.AnchorPath);
        if (folder is null
            || _cameraTouched
            || !isRoot && !ViewAllPath.Equals(folder.FullPath, state.AnchorPath)
            || _viewWidth <= 0)
        {
            return;
        }

        StopFlight();
        _anchor = folder;
        _aw = state.Width * _viewWidth;
        _ax = _viewWidth / 2 + state.X * _viewWidth;
        _ay = _viewHeight / 2 + state.Y * _viewWidth;
        _hasCamera = true;
        Normalize();
        ClampZoom(new Point(_viewWidth / 2, _viewHeight / 2));
        ClampPan();
        AfterCameraMove();
    }

    /// <summary>The cell of <paramref name="folder"/> on screen, or null when it is not one of the cells.</summary>
    public Rect? ScreenRectOf(NestedFolder folder)
    {
        EnsureCamera();
        if (_anchor is null)
        {
            return null;
        }

        BuildChain();
        var result = RectOf(folder);
        return result is { } r ? new Rect(r.X, r.Y, r.W, r.W * NestedLayout.CellHeight) : null;
    }

    /// <summary>The innermost drawn folder under a point in the control.</summary>
    public NestedHit? HitTest(Point point)
    {
        EnsureCamera();
        if (_tree is null || _anchor is null)
        {
            return null;
        }

        BuildChain();
        var folder = _tree.Root;
        var (x, y, w) = _chain[folder];
        if (!Inside(point, x, y, w))
        {
            return null;
        }

        while (true)
        {
            // What is hit is what the current order puts there, drawn or not.
            Ensure(folder);
            var grid = folder.Grid;
            if (grid.IsEmpty || w * grid.Scale < MinimumCellPixels * 2)
            {
                break;
            }

            var index = grid.IndexAt((point.X - x) / w, (point.Y - y) / w);
            if (index < 0 || index >= folder.Children.Count)
            {
                break;
            }

            var child = folder.Children[index];
            if (_chain.TryGetValue(child, out var exact))
            {
                (x, y, w) = exact;
            }
            else
            {
                x += child.OffsetX * w;
                y += child.OffsetY * w;
                w *= child.Scale;
            }

            folder = child;
        }

        // Not in any sub-folder: perhaps on one of the files under them.
        var files = folder.FileGrid;
        if (!files.IsEmpty && w * files.TileWidth >= 2)
        {
            var fileIndex = files.IndexAt((point.X - x) / w, (point.Y - y) / w);
            if (fileIndex >= 0 && fileIndex < folder.Files.Count)
            {
                var (fx, fy) = files.Origin(fileIndex);
                var tile = new Rect(x + fx * w, y + fy * w, files.TileWidth * w, files.TileHeight * w);
                return new NestedHit(folder, tile, true, fileIndex, folder.Files[fileIndex].Name);
            }
        }

        var header = w * NestedLayout.HeaderHeight;
        var onHeader = header < HeaderGrabPixels || point.Y - y <= header;
        return new NestedHit(folder, new Rect(x, y, w, w * NestedLayout.CellHeight), onHeader);
    }

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

    /// <summary>A folder's cell or a file's tile on screen.</summary>
    /// <param name="place">Whether what it reads is placed for the current order first (see <see cref="RectOf"/>).</param>
    private Rect? TargetRect(NestedFolder folder, int fileIndex, bool place = true)
    {
        if (RectOf(folder, place) is not { } r)
        {
            return null;
        }

        if (fileIndex < 0)
        {
            return new Rect(r.X, r.Y, r.W, r.W * NestedLayout.CellHeight);
        }

        if (place)
        {
            Ensure(folder);
        }

        var files = folder.FileGrid;
        if (fileIndex >= files.Count)
        {
            return null;
        }

        var (fx, fy) = files.Origin(fileIndex);
        return new Rect(r.X + fx * r.W, r.Y + fy * r.W, files.TileWidth * r.W, files.TileHeight * r.W);
    }

    /// <summary>
    /// Keyboard navigation over the grid of the active folder's siblings.
    /// Returns whether the key meant something here.
    /// </summary>
    public bool HandleKey(Key key, ModifierKeys modifiers)
    {
        if (_tree is null || modifiers != ModifierKeys.None)
        {
            return false;
        }

        var active = Resolve(_activePath);
        switch (key)
        {
            case Key.Enter when active is { } target:
                var hit = new NestedHit(
                    target.Folder,
                    default,
                    true,
                    target.FileIndex,
                    target.FileIndex >= 0 ? target.Folder.Files[target.FileIndex].Name : null);
                OpenRequested?.Invoke(hit);
                if (!hit.IsFile)
                {
                    FlyTo(target.Folder);
                }

                return true;
            case Key.Back when active is { } target:
                // Out of a file is its folder; out of a folder is its parent.
                var up = target.FileIndex >= 0 ? target.Folder : target.Folder.Parent;
                if (up is null || up.IsComputer)
                {
                    return true;
                }

                MarkSelected(up.FullPath);
                SelectRequested?.Invoke(up.FullPath, false);
                FlyTo(up);
                return true;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                if (Neighbour(active, key) is not { } next)
                {
                    return active is not null;
                }

                var path = next.FileIndex >= 0 ? next.Folder.PathOf(next.Folder.Files[next.FileIndex]) : next.Folder.FullPath;
                MarkSelected(path);
                SelectRequested?.Invoke(path, false);
                EnsureVisible(next.Folder, next.FileIndex);
                return true;
            default:
                return false;
        }
    }

    // ---- camera internals ----------------------------------------------------

    private void EnsureCamera()
    {
        if (_hasCamera || _tree is null)
        {
            return;
        }

        _viewWidth = ActualWidth;
        _viewHeight = ActualHeight;
        if (_viewWidth <= 0)
        {
            _viewWidth = 1200;
            _viewHeight = 800;
        }

        _anchor = _tree.Root;
        var width = FitWidth();
        _aw = width;
        _ax = (_viewWidth - width) / 2;
        _ay = (_viewHeight - width * NestedLayout.CellHeight) / 2;
        _hasCamera = true;
    }

    private double FitWidth()
    {
        const double margin = 18;
        return Math.Max(40, Math.Min(_viewWidth - 2 * margin, (_viewHeight - 2 * margin) * NestedLayout.Aspect));
    }

    private (double X, double Y, double W) FitRect(double fill)
    {
        var width = fill >= 1
            ? FitWidth()
            : Math.Min(_viewWidth * fill, _viewHeight * fill * NestedLayout.Aspect);
        return ((_viewWidth - width) / 2, (_viewHeight - width * NestedLayout.CellHeight) / 2, width);
    }

    private void ZoomAround(Point at, double factor)
    {
        _ax = at.X + (_ax - at.X) * factor;
        _ay = at.Y + (_ay - at.Y) * factor;
        _aw *= factor;
    }

    /// <summary>Re-chooses the anchor so it is the deepest folder that holds the view's centre and is at least half as wide as the view.</summary>
    private void Normalize()
    {
        if (_anchor is null)
        {
            return;
        }

        // Walking up divides by the anchor's own place in its parent, and
        // walking down reads its children's: all of them for the current order.
        EnsureAnchorPath();

        // A folder that was refreshed away or filtered out cannot be the anchor.
        // Filtered out, or inside something that was: hiding a folder the
        // view is deep inside must take the view out of it.
        while (_anchor.Parent is not null && (!NestedTree.IsOnCanvas(_anchor) || NestedTree.IsDetached(_anchor)))
        {
            Up();
        }

        var centreX = _viewWidth / 2;
        var centreY = _viewHeight / 2;
        var half = _viewWidth / 2;
        for (var guard = 0; guard < 512; guard++)
        {
            if (_anchor.Parent is not null && (_aw < half || !Inside(new Point(centreX, centreY), _ax, _ay, _aw)))
            {
                Up();
                continue;
            }

            Ensure(_anchor);
            var grid = _anchor.Grid;
            if (!grid.IsEmpty)
            {
                var index = grid.IndexAt((centreX - _ax) / _aw, (centreY - _ay) / _aw);
                if (index >= 0 && index < _anchor.Children.Count)
                {
                    var child = _anchor.Children[index];
                    var childWidth = _aw * child.Scale;
                    if (childWidth >= half)
                    {
                        _ax += child.OffsetX * _aw;
                        _ay += child.OffsetY * _aw;
                        _aw = childWidth;
                        _anchor = child;
                        continue;
                    }
                }
            }

            break;
        }
    }

    private void Up()
    {
        var folder = _anchor!;
        var parentWidth = _aw / folder.Scale;
        _ax -= folder.OffsetX * parentWidth;
        _ay -= folder.OffsetY * parentWidth;
        _aw = parentWidth;
        _anchor = folder.Parent;
    }

    /// <summary>
    /// Keeps This PC from shrinking to a dot, and the view from diving into a
    /// folder with nothing in it: there is nothing further in to see.
    /// </summary>
    private void ClampZoom(Point at)
    {
        var root = RootRect();
        if (root is { } rect)
        {
            var minimum = FitWidth() * 0.25;
            if (rect.W < minimum)
            {
                ZoomAround(at, minimum / rect.W);
                Normalize();
            }
        }

        if (_anchor is null)
        {
            return;
        }

        // Nothing further in: an empty folder need not fill more than the
        // screen.  A folder of files goes as far in as it takes for its
        // smallest names to be read and picked up.
        Ensure(_anchor);
        var isDeadEnd = _anchor.Children.Count == 0
            && (_anchor.LoadState is NestedLoadState.Loaded or NestedLoadState.Failed || _anchor.IsReparsePoint);
        var maximum = !isDeadEnd
            ? 1e7 * _viewWidth
            : _anchor.FileGrid is { IsEmpty: false } files
                ? Math.Max(3 * _viewWidth, 4 * FileLabelPixels / files.TileHeight)
                : 3 * _viewWidth;
        if (_aw > maximum)
        {
            ZoomAround(at, maximum / _aw);
            Normalize();
        }
    }

    /// <summary>Keeps at least a strip of This PC on screen, so the view cannot be lost in the dark.</summary>
    private void ClampPan()
    {
        var root = RootRect();
        if (root is not { } rect)
        {
            return;
        }

        const double keep = 48;
        var height = rect.W * NestedLayout.CellHeight;
        var dx = 0.0;
        var dy = 0.0;
        if (rect.X > _viewWidth - keep) dx = _viewWidth - keep - rect.X;
        if (rect.X + rect.W < keep) dx = keep - rect.X - rect.W;
        if (rect.Y > _viewHeight - keep) dy = _viewHeight - keep - rect.Y;
        if (rect.Y + height < keep) dy = keep - rect.Y - height;
        if (dx != 0 || dy != 0)
        {
            _ax += dx;
            _ay += dy;
            Normalize();
        }
    }

    private (double X, double Y, double W)? RootRect()
    {
        if (_anchor is null)
        {
            return null;
        }

        EnsureAnchorPath();
        var (x, y, w) = (_ax, _ay, _aw);
        for (var folder = _anchor; folder.Parent is not null; folder = folder.Parent)
        {
            var parentWidth = w / folder.Scale;
            x -= folder.OffsetX * parentWidth;
            y -= folder.OffsetY * parentWidth;
            w = parentWidth;
        }

        return (x, y, w);
    }

    /// <summary>The anchor and every folder above it, each with its exact rectangle.</summary>
    private void BuildChain()
    {
        EnsureAnchorPath();
        BuildChainAsPlaced();
    }

    /// <summary>
    /// <see cref="BuildChain"/> from wherever the folders on the way happen to
    /// be placed, without placing any for the current order first: the chain
    /// as the last frame drew it, which is what a change of order has to take
    /// its bearings from before anything moves.
    /// </summary>
    private void BuildChainAsPlaced()
    {
        _chain.Clear();
        if (_anchor is null)
        {
            return;
        }

        var (x, y, w) = (_ax, _ay, _aw);
        var folder = _anchor;
        _chain[folder] = (x, y, w);
        while (folder.Parent is not null)
        {
            var parentWidth = w / folder.Scale;
            x -= folder.OffsetX * parentWidth;
            y -= folder.OffsetY * parentWidth;
            w = parentWidth;
            folder = folder.Parent;
            _chain[folder] = (x, y, w);
        }
    }

    /// <param name="place">
    /// Whether each folder on the way is placed for the current order first.
    /// What the user acts on is found where the current order puts it; what
    /// is only drawn over the picture - a mark, the selection's outline - is
    /// drawn where the picture has it, which for a folder the frame had no
    /// allowance left to place is where it was.
    /// </param>
    private (double X, double Y, double W)? RectOf(NestedFolder folder, bool place = true)
    {
        var path = new List<NestedFolder>();
        var current = folder;
        (double X, double Y, double W) rect;
        while (!_chain.TryGetValue(current, out rect))
        {
            if (current.Index < 0 || current.IsForgotten || current.Parent is null)
            {
                return null;
            }

            path.Add(current);
            current = current.Parent;
        }

        for (var index = path.Count - 1; index >= 0; index--)
        {
            // Each step's place is its parent's placing, taken from the top
            // down so every offset is for the current order.
            var step = path[index];
            if (place)
            {
                Ensure(step.Parent!);
            }

            rect = (rect.X + step.OffsetX * rect.W, rect.Y + step.OffsetY * rect.W, rect.W * step.Scale);
        }

        return rect;
    }

    /// <summary>
    /// Has the tree place a folder for the current order before its children,
    /// grids or files are read.  Nearly always it already is, and this is one
    /// comparison; a folder never read has nothing to place.
    /// </summary>
    private void Ensure(NestedFolder folder)
    {
        var stamp = folder.LayoutSortGeneration;
        if (stamp >= 0 && _tree is { } tree && stamp != tree.SortGeneration)
        {
            tree.EnsureLayout(folder);
        }
    }

    /// <summary>
    /// <see cref="Ensure"/> for the anchor and every folder above it, from the
    /// top down: the camera divides its way up through their places, so a
    /// chain placed for two different orders would put the view somewhere
    /// neither of them has it.
    /// </summary>
    private void EnsureAnchorPath()
    {
        if (_anchor is not null && _tree is not null)
        {
            _tree.EnsurePathLayout(_anchor);
        }
    }

    /// <summary>
    /// The order changed, and nothing has moved yet.  The folder being looked
    /// at is held where it is on screen - at the rectangle it has right now -
    /// and only then is its way up placed for the new order, so its contents
    /// reorder inside it and whatever moves above it moves around it.
    ///
    /// Which folder that is: the anchor when it fills most of the view, which
    /// is what flying into a folder leaves - the folder at nine tenths of the
    /// screen, its parent covering the rest.  Holding the parent there instead
    /// would put a sibling in the folder's place, in front of the user, the
    /// moment they clicked a header.  Otherwise the view is a look over a
    /// folder's grid rather than into one of its cells, and the deepest folder
    /// covering the view is held, its cells trading places inside it.  Only an
    /// overview covered by nothing is held on This PC, whose drives never
    /// change places.
    /// </summary>
    private void OnTreeSortChanged()
    {
        // A hover names a tile by its index, and the tile it names is about to
        // hold another file.
        _hover = null;
        if (_tree is { } tree && _hasCamera && _anchor is not null && _flight is null && _viewWidth > 0)
        {
            BuildChainAsPlaced();
            var (cover, x, y, w, _) = CoverCell();
            if (_aw < _viewWidth * FillsViewShare && !ReferenceEquals(cover, _anchor))
            {
                _anchor = cover;
                _ax = x;
                _ay = y;
                _aw = w;
            }

            tree.EnsurePathLayout(_anchor);
            BuildChainAsPlaced();
        }

        // The matches were listed in the order before: stepping through them
        // follows the new one at once (see GoToMatch), and the list itself is
        // put in it once the tree has placed everything.
        if (_filter is not null)
        {
            _ = ReorderFilterWhenPlacedAsync();
        }

        RequestFrame(Layers.All);
    }

    /// <summary>How much of the view's width a folder takes up for a change of order to hold it, rather than the folder around it, still.</summary>
    private const double FillsViewShare = 0.75;

    private void AfterCameraMove()
    {
        _lastMotion = System.Diagnostics.Stopwatch.GetTimestamp();
        RequestFrame(Layers.All);
        CameraChanged?.Invoke();
    }

    private static bool Inside(Point point, double x, double y, double w) =>
        point.X >= x && point.X <= x + w && point.Y >= y && point.Y <= y + w * NestedLayout.CellHeight;

    // ---- flights -------------------------------------------------------------

    private void StopFlight() => _flight = null;

    /// <summary>Marks layers out of date and makes sure the next frame draws them.</summary>
    private void RequestFrame(Layers layers)
    {
        _dirty |= layers;
        if (_frameHooked)
        {
            return;
        }

        _frameHooked = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void UnhookFrame()
    {
        if (!_frameHooked)
        {
            return;
        }

        _frameHooked = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    /// <summary>
    /// One frame of the canvas's own loop, the way a game engine runs one: the
    /// camera advances if a flight is under way, then whatever went out of date
    /// since the last frame is drawn - once, however many things asked for it.
    /// A hundred folders finishing their reads in one frame are one redraw.
    /// The loop unhooks itself when there is nothing left to do.
    /// </summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        if (_flight is { } flight)
        {
            if (NestedTree.IsDetached(flight.Target) || !NestedTree.IsOnCanvas(flight.Target))
            {
                StopFlight();
            }
            else
            {
                var done = flight.Sample(_viewWidth, _viewHeight, out var x, out var y, out var w);
                _anchor = flight.Target;
                _ax = x;
                _ay = y;
                _aw = w;
                _hasCamera = true;
                Normalize();
                if (done)
                {
                    StopFlight();
                }

                AfterCameraMove();
            }
        }

        var moving = IsMoving();

        // The camera has come to rest after frames drawn with less detail, or
        // with text set for motion: one more frame, at full quality.  Not the
        // moment it stops - drawing every name crisp costs WPF's render thread
        // a frame or two - but once it has been still for a third of a second,
        // when a frame that takes longer shows as nothing at all, and the
        // user is not in the middle of the next move.
        var settled = !moving && _lastMotion != 0
            && System.Diagnostics.Stopwatch.GetElapsedTime(_lastMotion).TotalMilliseconds >= SettleMilliseconds;
        if (settled && (_lodDegraded || _textAnimated))
        {
            _dirty |= Layers.All;
        }

        if (_dirty != Layers.None)
        {
            var layers = _dirty;
            _dirty = Layers.None;

            // Only a frame of the loop has a next frame to leave work to; a
            // snapshot or a test drawing on demand gets everything placed.
            _inFrameLoop = true;
            var frameStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                // Between stopping and settling, the frames that are drawn keep
                // the motion look, so nothing flips back and forth.
                RenderLayers(layers, moving || !settled && (_lodDegraded || _textAnimated));
            }
            finally
            {
                _inFrameLoop = false;
                LastFrameMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds;
            }
        }

        if (_dirty == Layers.None && _flight is null && !_lodDegraded && !_textAnimated && !moving)
        {
            UnhookFrame();
        }
    }

    /// <summary>How long the camera has to be still before names are drawn crisp again.</summary>
    private const double SettleMilliseconds = 350;

    /// <summary>Whether the camera moved in the last few frames.</summary>
    private bool IsMoving() =>
        _flight is not null
        || _lastMotion != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_lastMotion).TotalMilliseconds < 140;

    /// <summary>
    /// A smooth zoom-and-pan in the sense of van Wijk and Nuij: the path
    /// through (centre, width) space along which the apparent motion is
    /// least.  Expressed in the target's own frame, where the target is one
    /// unit wide, so the numbers stay sane however far apart the ends are.
    /// </summary>
    private sealed class Flight
    {
        private const double Rho = 1.42;
        private double _c0x, _c0y, _c1x, _c1y, _w0, _w1, _u1, _r0, _length;
        private bool _isPureZoom;
        private long _started;
        private double _duration;

        public required NestedFolder Target { get; init; }

        public static Flight Create(
            NestedFolder target,
            Rect current,
            (double X, double Y, double W) end,
            double viewWidth,
            double viewHeight)
        {
            // World units are target widths: the target spans [0, 1].
            var flight = new Flight { Target = target };
            var centreX = viewWidth / 2;
            var centreY = viewHeight / 2;
            flight._w0 = viewWidth / current.Width;
            flight._c0x = (centreX - current.X) / current.Width;
            flight._c0y = (centreY - current.Y) / current.Width;
            flight._w1 = viewWidth / end.W;
            flight._c1x = (centreX - end.X) / end.W;
            flight._c1y = (centreY - end.Y) / end.W;

            var dx = flight._c1x - flight._c0x;
            var dy = flight._c1y - flight._c0y;
            flight._u1 = Math.Sqrt(dx * dx + dy * dy);
            if (flight._u1 < 1e-9 * Math.Max(flight._w0, flight._w1))
            {
                flight._isPureZoom = true;
                flight._length = Math.Abs(Math.Log(flight._w1 / flight._w0)) / Rho;
            }
            else
            {
                var rho2 = Rho * Rho;
                var rho4 = rho2 * rho2;
                var b0 = (flight._w1 * flight._w1 - flight._w0 * flight._w0 + rho4 * flight._u1 * flight._u1)
                    / (2 * flight._w0 * rho2 * flight._u1);
                var b1 = (flight._w1 * flight._w1 - flight._w0 * flight._w0 - rho4 * flight._u1 * flight._u1)
                    / (2 * flight._w1 * rho2 * flight._u1);
                // log(sqrt(b^2 + 1) - b) is -asinh(b), and only the second
                // survives the ratios a deep jump produces: at b ~ 1e8 the first
                // cancels to nothing and the flight collapses into a cut.
                flight._r0 = -Math.Asinh(b0);
                var r1 = -Math.Asinh(b1);
                flight._length = (r1 - flight._r0) / Rho;
            }

            if (double.IsNaN(flight._length) || double.IsInfinity(flight._length))
            {
                flight._isPureZoom = true;
                flight._length = Math.Abs(Math.Log(flight._w1 / flight._w0)) / Rho;
                if (double.IsNaN(flight._length) || double.IsInfinity(flight._length))
                {
                    flight._length = 0;
                }
            }

            // Long journeys take longer, but never so long that it feels slow.
            flight._duration = Math.Clamp(0.22 + 0.16 * Math.Abs(flight._length), 0.25, 1.1);
            flight._started = System.Diagnostics.Stopwatch.GetTimestamp();
            return flight;
        }

        /// <summary>The target's rectangle at this moment; true once the flight has arrived.</summary>
        public bool Sample(double viewWidth, double viewHeight, out double x, out double y, out double w)
        {
            // The system tick moves in 15.6 ms steps: at 120 Hz most frames
            // would read the same time and the camera would stutter.
            var t = System.Diagnostics.Stopwatch.GetElapsedTime(_started).TotalSeconds / _duration;
            var done = t >= 1;
            t = Math.Clamp(t, 0, 1);

            // Ease in and out, so the view does not jolt at either end.
            var eased = t * t * (3 - 2 * t);
            var s = eased * _length;

            double centreX, centreY, width;
            if (done)
            {
                (centreX, centreY, width) = (_c1x, _c1y, _w1);
            }
            else if (_isPureZoom)
            {
                // Straight in or out: the width changes geometrically, which is
                // what looks like a steady zoom.
                width = _length == 0
                    ? _w0 + (_w1 - _w0) * eased
                    : _w0 * Math.Exp(Math.Log(_w1 / _w0) * eased);
                centreX = _c0x + (_c1x - _c0x) * eased;
                centreY = _c0y + (_c1y - _c0y) * eased;
            }
            else
            {
                var rho2 = Rho * Rho;
                var coshR0 = Math.Cosh(_r0);
                var u = _w0 / rho2 * coshR0 * Math.Tanh(Rho * s + _r0) - _w0 / rho2 * Math.Sinh(_r0);
                width = _w0 * coshR0 / Math.Cosh(Rho * s + _r0);
                var fraction = u / _u1;
                centreX = _c0x + (_c1x - _c0x) * fraction;
                centreY = _c0y + (_c1y - _c0y) * fraction;
            }

            w = viewWidth / width;
            x = viewWidth / 2 - centreX * w;
            y = viewHeight / 2 - centreY * w;
            return done;
        }
    }

    // ---- rendering -----------------------------------------------------------

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        // Collapsed (the tree canvas is showing): nothing to frame, and the
        // camera stays as it was for when the canvas comes back.
        if (sizeInfo.NewSize.Width <= 0 || sizeInfo.NewSize.Height <= 0)
        {
            return;
        }

        if (_hasCamera && _viewWidth > 0 && _viewHeight > 0)
        {
            // Keep the middle of the view on the same spot of the same folder -
            // measured from the size the camera was actually set up for, which
            // before the first layout is a placeholder, not the previous size.
            var dx = (sizeInfo.NewSize.Width - _viewWidth) / 2;
            var dy = (sizeInfo.NewSize.Height - _viewHeight) / 2;
            _ax += dx;
            _ay += dy;
        }

        _viewWidth = sizeInfo.NewSize.Width;
        _viewHeight = sizeInfo.NewSize.Height;

        // The first "everything" was framed before the control knew its size;
        // until somebody moves it, frame it again for the size it really is.
        if (!_cameraTouched && _tree is not null && ReferenceEquals(_anchor, _tree.Root))
        {
            _hasCamera = false;
        }

        RequestFrame(Layers.All);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _bitmap = null;
        _textCache.Clear();
        _oldTextCache.Clear();
        RequestFrame(Layers.All);
    }

    private void OnTreeChanged(object? sender, EventArgs e) => RequestFrame(Layers.All);

    /// <summary>
    /// The control's own drawing is only a transparent sheet, so every point
    /// of it takes the mouse; the picture is in the layers above.  Outside a
    /// window - a test, a snapshot - there is no frame loop, so the layers are
    /// drawn here and then.
    /// </summary>
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (PresentationSource.FromVisual(this) is null)
        {
            _dirty = Layers.None;
            RenderLayers(Layers.All, inMotion: false);
        }
        else
        {
            RequestFrame(Layers.All);
        }
    }

    /// <summary>
    /// Takes the canvas's DPI scale, from the window or from a test's
    /// <see cref="DpiOverride"/>; true when it changed, which makes the
    /// bitmap, the GPU surface and every text layout out of date.
    /// </summary>
    private bool UpdateScale()
    {
        var dpi = DpiOverride ?? VisualTreeHelper.GetDpi(this);
        if (dpi.DpiScaleX == _scaleX && dpi.DpiScaleY == _scaleY)
        {
            return false;
        }

        _scaleX = dpi.DpiScaleX;
        _scaleY = dpi.DpiScaleY;
        _bitmap = null;
        _textCache.Clear();
        _oldTextCache.Clear();
        return true;
    }

    /// <summary>
    /// A DPI scale to draw at instead of the window's: for tests, and for the
    /// bench's stand-in for a 150 % monitor on a 100 % one (<c>--bench-scale</c>).
    /// </summary>
    internal DpiScale? DpiOverride { get; set; }

    /// <summary>
    /// Draws the layers asked for; false when the frame could not be shown -
    /// the GPU's previous frame was still being copied - and was left for the
    /// next tick, which is already asked for.
    /// </summary>
    private bool RenderLayers(Layers layers, bool inMotion)
    {
        // Hidden or not laid out yet: draw nothing, and keep the size the
        // camera was set up for rather than forgetting it.
        if (ActualWidth < 1 || ActualHeight < 1 || _tree is null)
        {
            return true;
        }

        _viewWidth = ActualWidth;
        _viewHeight = ActualHeight;
        if (UpdateScale())
        {
            layers = Layers.All;
        }

        EnsureCamera();
        Normalize();
        BuildChain();

        // Text is set for motion while the camera moves: WPF then scales the
        // glyphs it already has instead of rendering every size afresh, which
        // is most of what a zoom over a folder of names used to cost.
        var animated = inMotion;
        if (animated != _textAnimated)
        {
            _textAnimated = animated;
            var mode = animated ? TextHintingMode.Animated : TextHintingMode.Auto;
            TextOptions.SetTextHintingMode(_labelVisual, mode);
            TextOptions.SetTextHintingMode(_decorVisual, mode);
            layers |= Layers.Labels | Layers.Decor;
        }

        _presentPending = false;
        LastUploadMilliseconds = 0;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var sceneAllocated = 0L;
        if ((layers & Layers.Scene) != 0)
        {
            RenderScene(inMotion);
            layers |= Layers.Labels | Layers.Decor | Layers.Overlay;
            sceneAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        }

        allocated = GC.GetAllocatedBytesForCurrentThread();
        var layerStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        NewTextLayouts = 0;
        _textBudget = inMotion ? MotionTextBudget : int.MaxValue;
        _textDeferred = false;
        if ((layers & Layers.Labels) != 0 && !TryDrawLabelsOnGpu(inMotion))
        {
            DrawLabelsWithWpf();
        }

        LastLabelsMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(layerStarted).TotalMilliseconds;
        var labelsAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

        // On the CPU the marks and the pointer's layer are recorded here, as
        // they always were.  Over the GPU's picture they wait for the present
        // below: a present WPF's render thread was too late for leaves the
        // surface showing the previous frame, and outlines, beacons and the
        // hover recorded for the new camera would sit offset from the cells
        // under them for a frame.  Recorded only once the frame they belong
        // to is shown, they never lag the cells or run ahead of them.
        var decorAfterPresent = _presentPending;
        var decorAllocated = decorAfterPresent ? 0 : RecordDecorAndOverlay(layers);
        allocated = GC.GetAllocatedBytesForCurrentThread();

        // The GPU's scene last, once the frame's other CPU work is done: the
        // image lock waits for WPF's render thread to finish copying the
        // previous frame, and the walk and the names gave it that time.
        var shown = !_presentPending || PresentScene();
        var presentAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (decorAfterPresent)
        {
            if (shown)
            {
                decorAllocated = RecordDecorAndOverlay(layers);
            }
            else
            {
                LastDecorMilliseconds = 0;
            }
        }

        LastAllocations = new FrameAllocations(sceneAllocated, labelsAllocated, decorAllocated, presentAllocated);
        return shown;
    }

    /// <summary>
    /// The frame's marks - filter outlines, selection, drop target, beacons,
    /// trail - and the pointer's layer, when they are among
    /// <paramref name="layers"/>; the text cache's generation turned when it
    /// is full.  Returns what it handed the garbage collector.
    /// </summary>
    private long RecordDecorAndOverlay(Layers layers)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var layerStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        if ((layers & Layers.Decor) != 0)
        {
            _hotspots.Clear();
            using var dc = _decorVisual.RenderOpen();
            foreach (var outline in _filterOutlines)
            {
                var radius = outline.Width >= 40 ? Math.Min(6, outline.Width * 0.03) : 1;
                dc.DrawRoundedRectangle(null, FilterPen, outline, radius, radius);
            }

            DrawSelection(dc);
            DrawDropTarget(dc);
            DrawBeacons(dc);
            DrawTrail(dc);
        }

        LastDecorMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(layerStarted).TotalMilliseconds;
        if (_textDeferred)
        {
            // Some names waited for their layout: the next frame has budget again.
            RequestFrame(Layers.Labels | Layers.Decor);
        }

        if ((layers & Layers.Overlay) != 0)
        {
            RenderOverlay();
        }

        if (_textCache.Count > 4000)
        {
            // Two generations rather than one clear: what is still in use is
            // carried over on its next lookup, so no frame has to lay out
            // every name on screen at once.
            (_oldTextCache, _textCache) = (_textCache, _oldTextCache);
            _textCache.Clear();
        }

        return GC.GetAllocatedBytesForCurrentThread() - allocated;
    }

    /// <summary>What the last frame handed the garbage collector, layer by layer: the scene, the names, the marks and the pointer's layer together, and the present.</summary>
    public FrameAllocations LastAllocations { get; private set; }

    /// <summary>
    /// The cells: one walk from the outermost folder that still covers the
    /// whole view down through everything on screen and big enough to see,
    /// filling rectangles into the frame's target - the GPU's instances when
    /// the canvas is in a window whose card is ready, the raster's bitmap
    /// otherwise, or a test's offscreen texture.  The GPU's frame is
    /// presented at the end of the frame (<see cref="PresentScene"/>).
    /// </summary>
    private void RenderScene(bool inMotion, OffscreenScene? offscreen = null)
    {
        using var frame = PerfLog.Measure("nested.frame");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _tree!.BeginFrame();

        var pixelWidth = Math.Max(1, (int)Math.Ceiling(_viewWidth * _scaleX));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(_viewHeight * _scaleY));

        // Motion LOD, the way a game drops detail it cannot afford in a frame:
        // while the camera moves, cells and file tiles below a size that grows
        // when frames run long are left out; the frame after it stops draws
        // them all again.  At rest the picture is always complete.
        var lod = inMotion ? _lod : 1;
        _minCell = MinimumCellPixels * lod;
        _minTile = 2.5 * lod;
        _relayoutAllowance = _inFrameLoop ? DrawRelayoutTicks : long.MaxValue;

        // The one place a frame's target is chosen: the GPU when it can be
        // used, else the bitmap painted by hand - and if the GPU fails half
        // way through a frame, the bitmap for that same frame.  Each brings
        // its own sink to WalkScene; everything before and after is the same
        // for all.
        if (offscreen is { } scene)
        {
            PaintSceneOffscreen(scene, pixelWidth, pixelHeight, inMotion);
        }
        else if (!TryWalkSceneForGpu(pixelWidth, pixelHeight))
        {
            PaintSceneIntoBitmap(pixelWidth, pixelHeight);
        }

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (inMotion)
        {
            // A 120 Hz frame is 8 ms, and the labels and WPF's own work need
            // their share of it.
            _lod = elapsed > 4.5 ? Math.Min(_lod * 1.6, 16) : elapsed < 2 ? Math.Max(1, _lod / 1.25) : _lod;
        }
        else
        {
            _lod = 1;
        }

        _lodDegraded = lod > 1.0001;
        PerfLog.Value("nested.cells", DrawnCellCount);
        LastRenderMilliseconds = elapsed;
        LastPlacingMilliseconds = _relayoutSpent * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        DrewOutOfDate = _drewStale;
        RenderCount++;
        if (_drewStale)
        {
            // Some folders were drawn in their previous order: the tree's pass
            // places those first, and the next frame places the next few
            // milliseconds' worth itself.
            _tree.PlaceFirst(_drawnStale);
            _drawnStale.Clear();
            RequestFrame(Layers.Scene);
        }
    }

    /// <summary>
    /// The scene painted into the bitmap the scene layer shows, through
    /// <see cref="RasterSink"/>: the canvas's own picture, and the one any
    /// other target is measured against.
    /// </summary>
    private void PaintSceneIntoBitmap(int pixelWidth, int pixelHeight)
    {
        // Grown in steps and reused.  Dragging a window edge is a new size on
        // every step, and a new full-screen bitmap each time - tens of
        // megabytes of native memory freed only by a full collection - was
        // what made a live resize stutter.  The part past the control is
        // simply never drawn into, and clipped away.
        if (_bitmap is null
            || _bitmap.PixelWidth < pixelWidth
            || _bitmap.PixelHeight < pixelHeight
            || (long)_bitmap.PixelWidth * _bitmap.PixelHeight > 3L * pixelWidth * pixelHeight + 2_000_000)
        {
            _bitmap = new WriteableBitmap(
                (pixelWidth + 255) / 256 * 256,
                (pixelHeight + 255) / 256 * 256,
                96 * _scaleX,
                96 * _scaleY,
                PixelFormats.Pbgra32,
                null);
        }

        if (!ReferenceEquals(_shownBitmap, _bitmap))
        {
            // The layer holds the bitmap itself; from then on only its pixels
            // change, and the layer never has to be recorded again.
            _shownBitmap = _bitmap;
            _shownSurface = null;
            using var dc = _sceneVisual.RenderOpen();
            dc.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelWidth / _scaleX, _bitmap.PixelHeight / _scaleY));
        }

        _bitmap.Lock();
        try
        {
            _raster.Attach(_bitmap.BackBuffer, pixelWidth, pixelHeight, _bitmap.BackBufferStride);
            var walkStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            WalkScene(_rasterSink);
            LastWalkMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(walkStarted).TotalMilliseconds;
            LastPresentMilliseconds = 0;
            LastLockMilliseconds = 0;
            LastGpuWaitMilliseconds = 0;
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, pixelWidth, pixelHeight));
        }
        finally
        {
            _raster.Detach();
            _bitmap.Unlock();
        }
    }

    // ---- the GPU -----------------------------------------------------------------

    /// <summary>
    /// The scene for the GPU, when <see cref="GpuBootstrap.Decide"/> says the
    /// card that drives the window's monitor is ready: the walk fills the
    /// frame's instances through <see cref="GpuSink"/>, to be put on the
    /// texture the scene layer shows - where the bitmap used to be - by
    /// <see cref="PresentScene"/> at the end of the frame.  The names go to
    /// the same texture through <see cref="GpuLabelTarget"/> once the atlases
    /// are ready (<see cref="TryDrawLabelsOnGpu"/>); the marks, outlines,
    /// trail and pointer's layer above it stay WPF's.
    ///
    /// False when the GPU does not draw this frame, and the caller paints the
    /// bitmap instead: no window, the CPU chosen, or the card not ready yet -
    /// the canvas moves over at the first frame after it is.  A surface that
    /// is drawing already keeps drawing while the card for the window's
    /// monitor is still being prepared - the monitors being matched again
    /// after a display change, the window just moved onto another card's
    /// monitor - rather than dropping a working picture to the CPU for the
    /// moment; the canvas moves to the right set when it is ready.
    /// </summary>
    private bool TryWalkSceneForGpu(int pixelWidth, int pixelHeight)
    {
        if (_surfaceLost)
        {
            ReleaseGpu(lost: true);
        }

        var decision = GpuBootstrap.Decide(this);
        RendererReason = decision.Reason;
        var devices = decision.DeviceSet;
        if (devices is null
            && ReferenceEquals(decision.Reason, GpuBootstrap.ReasonWarmingUp)
            && _surface is { IsLost: false } kept
            && !kept.Devices.IsDisposed
            && kept.ScaleX == _scaleX
            && kept.ScaleY == _scaleY)
        {
            devices = kept.Devices;
            ScheduleGpuRetry();
        }

        if (devices is null || !EnsureSurface(devices))
        {
            if (_surface is not null)
            {
                ReleaseGpu(lost: false);
            }

            if (IsWorthAskingAgain(RendererReason))
            {
                ScheduleGpuRetry();
            }

            return false;
        }

        var surface = _surface!;
        surface.EnsureSize(pixelWidth, pixelHeight);
        if (surface.IsLost)
        {
            ReleaseGpu(lost: true);
            ScheduleGpuRetry();
            return false;
        }

        var frame = _gpuFrame ??= new NestedGpuFrame();
        var sink = _gpuSink ??= new GpuSink();
        var walkStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        sink.Begin(frame.SceneRects, pixelWidth, pixelHeight, CanvasColour);
        WalkScene(sink);
        frame.ClearColour = sink.ClearColour;
        frame.SceneChanged();
        LastWalkMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(walkStarted).TotalMilliseconds;
        _presentPending = true;
        return true;
    }

    /// <summary>
    /// Puts the frame's instances on screen: one present of the surface -
    /// locked, drawn, waited for, unlocked - in the same WPF batch as the
    /// layers recorded above it, so the names never lag the cells.  False
    /// when WPF's render thread still held the previous frame; the whole
    /// frame is drawn again on the next tick.  A device lost here costs
    /// nothing on screen: the same frame is painted into the bitmap at once.
    /// </summary>
    private bool PresentScene()
    {
        _presentPending = false;
        if (_surface is not { } surface)
        {
            return true;
        }

        var result = surface.Present(_drawSurface ??= DrawSurface);
        LastPresentMilliseconds = surface.LastPresentMilliseconds;
        LastLockMilliseconds = surface.LastLockMilliseconds;
        LastGpuWaitMilliseconds = surface.LastGpuWaitMilliseconds;
        LastRenderMilliseconds += surface.LastPresentMilliseconds;
        if (!surface.Devices.IsDisposed)
        {
            LastGpuMilliseconds = surface.Devices.Timer.LastGpuMilliseconds;
        }

        switch (result)
        {
            case PresentResult.Presented:
                LastGpuInstances = _renderer!.LastInstances;
                LastGpuGlyphs = _renderer.LastGlyphs;
                LastGpuIcons = _renderer.LastIcons;
                LastUploadMilliseconds += _renderer.LastUploadMilliseconds;
                ShowSurface(surface);
                if (_labelsOnGpu && _labelVisualRecorded)
                {
                    // The names are in the GPU's picture from this frame on;
                    // WPF's recording of them goes in the same batch, so no
                    // frame shows both or neither.
                    _labelVisual.RenderOpen().Close();
                    _labelVisualRecorded = false;
                }

                return true;

            case PresentResult.Skipped:
                // The names drawn over this frame are for a camera the cells
                // do not show yet; the next tick draws both again.
                SkippedPresents++;
                RequestFrame(Layers.All);
                return false;

            case PresentResult.Unavailable:
                // WPF has no front buffer - the lock screen, a UAC prompt -
                // so nothing drawn now would be seen.  The surface says when
                // it is back (ContentLost), and a whole frame follows.
                return true;

            default:
                // Lost in the middle of this frame: the same frame goes into
                // the bitmap - the walk again - and its names to WPF, and the
                // GPU is tried again once the bootstrap has a new set for the
                // card.
                var labelsWereOnGpu = _labelsOnGpu;
                ReleaseGpu(lost: true);
                PaintSceneIntoBitmap(ScenePixelWidth, ScenePixelHeight);
                if (labelsWereOnGpu)
                {
                    DrawLabelsWithWpf();
                }

                ScheduleGpuRetry();
                return true;
        }
    }

    /// <summary>
    /// A surface on <paramref name="devices"/> at the canvas's DPI, made anew
    /// when the card or the DPI changed - the window moved to a monitor on
    /// another card or of another scale - together with the card's renderer.
    /// False when the card cannot make them, which counts as losing it.
    /// </summary>
    private bool EnsureSurface(GpuDeviceSet devices)
    {
        if (_surface is { IsLost: false } current
            && ReferenceEquals(current.Devices, devices)
            && current.ScaleX == _scaleX
            && current.ScaleY == _scaleY)
        {
            return true;
        }

        ReleaseGpu(lost: _surface?.IsLost == true);
        try
        {
            _renderer = NestedGpuRenderer.For(devices);
            _surface = new NestedSurface(devices, _scaleX, _scaleY);
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or GpuUnavailableException or ObjectDisposedException or ArgumentException)
        {
            // Shaders that will not load or buffers the card will not make
            // are as good as a lost device: the bootstrap tries the card
            // again later, and after three failures a minute leaves the
            // canvas on the CPU for the session.
            _renderer = null;
            RendererReason = GpuBootstrap.ReasonUnavailable;
            if (!devices.IsDisposed)
            {
                GpuBootstrap.ReportDeviceLost(devices);
            }

            return false;
        }

        _surface.DeviceLost += OnSurfaceDeviceLost;
        _surface.ContentLost += OnSurfaceContentLost;
        return true;
    }

    /// <summary>
    /// Records the scene layer with the surface, once per surface and per
    /// texture it hands WPF - after the present that handed it over, so the
    /// rectangle drawn is always the texture WPF holds.  The bitmap is not
    /// drawn while the GPU is, and its tens of megabytes go with it.
    /// </summary>
    private void ShowSurface(NestedSurface surface)
    {
        if (ReferenceEquals(_shownSurface, surface) && _shownSurfaceVersion == surface.SurfaceVersion)
        {
            return;
        }

        _shownSurface = surface;
        _shownSurfaceVersion = surface.SurfaceVersion;
        _shownBitmap = null;
        _bitmap = null;
        using var dc = _sceneVisual.RenderOpen();
        dc.DrawImage(surface, surface.DrawRect);
    }

    /// <summary>The renderer's part of a present: GPU commands only, the instances filled before the lock.</summary>
    private void DrawSurface(in SurfaceFrame frame) => _renderer!.Draw(frame.Target, frame.Width, frame.Height, _gpuFrame!);

    /// <summary>
    /// Gives up the surface: back to the bitmap, which the next CPU frame
    /// records in the scene layer again.  A lost card is also handed back to
    /// the bootstrap, which throws its set away and makes a new one.
    /// </summary>
    private void ReleaseGpu(bool lost)
    {
        var surface = _surface;
        _surface = null;
        _renderer = null;
        _surfaceLost = false;
        if (surface is null)
        {
            return;
        }

        surface.DeviceLost -= OnSurfaceDeviceLost;
        surface.ContentLost -= OnSurfaceContentLost;
        var devices = surface.Devices;
        surface.Dispose();
        if (lost && !devices.IsDisposed)
        {
            GpuBootstrap.ReportDeviceLost(devices);
        }
    }

    /// <summary>The surface's device failed: the next frame lets it go and draws on the CPU.</summary>
    private void OnSurfaceDeviceLost(object? sender, EventArgs e)
    {
        _surfaceLost = true;
        RequestFrame(Layers.All);
    }

    /// <summary>WPF's front buffer came back (after the lock screen, say): nothing on the surface can be trusted.</summary>
    private void OnSurfaceContentLost(object? sender, EventArgs e) => RequestFrame(Layers.All);

    /// <summary>
    /// Listens, while the canvas is in a window, for what can change where
    /// its scene is drawn without anything on the canvas changing: a card
    /// finishing its warm-up, the renderer setting, the window moving to a
    /// monitor on another card, and the monitors matched to their cards
    /// again after a display change.
    /// </summary>
    private void HookGpu()
    {
        if (_gpuHooked)
        {
            return;
        }

        _gpuHooked = true;
        GpuBootstrap.DeviceSetReady += OnDeviceSetReady;
        GpuBootstrap.PreferenceChanged += OnRendererPreferenceChanged;
        GpuBootstrap.MonitorsMatched += OnMonitorsMatched;
        _gpuWindow = Window.GetWindow(this);
        if (_gpuWindow is not null)
        {
            _gpuWindow.LocationChanged += OnWindowMoved;
        }
    }

    private void UnhookGpu()
    {
        if (_gpuHooked)
        {
            _gpuHooked = false;
            GpuBootstrap.DeviceSetReady -= OnDeviceSetReady;
            GpuBootstrap.PreferenceChanged -= OnRendererPreferenceChanged;
            GpuBootstrap.MonitorsMatched -= OnMonitorsMatched;
            if (_gpuWindow is not null)
            {
                _gpuWindow.LocationChanged -= OnWindowMoved;
                _gpuWindow = null;
            }
        }

        _monitorCheck?.Stop();
        _gpuRetry?.Stop();
        UnhookLabelAtlases();
        ReleaseGpu(lost: false);
    }

    /// <summary>Whether the GPU may be ready later without anything else happening: a card warming up, or one to be tried again.</summary>
    private static bool IsWorthAskingAgain(string reason) =>
        ReferenceEquals(reason, GpuBootstrap.ReasonWarmingUp) || ReferenceEquals(reason, GpuBootstrap.ReasonUnavailable);

    /// <summary>
    /// Asks again in a moment whether the GPU can draw.  A card still warming
    /// up says when it is ready, but one that failed or was lost is only made
    /// again when somebody asks after the bootstrap's wait - and a canvas at
    /// rest draws no frames to ask in.  Without this, a canvas that lost its
    /// card while the user was reading would stay on the CPU until the next
    /// time the view moved.
    /// </summary>
    private void ScheduleGpuRetry()
    {
        if (_gpuRetry is null)
        {
            _gpuRetry = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
            _gpuRetry.Tick += OnGpuRetry;
        }

        if (!_gpuRetry.IsEnabled)
        {
            _gpuRetry.Start();
        }
    }

    private void OnGpuRetry(object? sender, EventArgs e)
    {
        _gpuRetry?.Stop();
        var decision = GpuBootstrap.Decide(this);
        if (IsSceneOnGpu && ReferenceEquals(decision.DeviceSet, _surface?.Devices))
        {
            return;
        }

        if (decision.UseGpu)
        {
            RequestFrame(Layers.All);
        }
        else if (IsWorthAskingAgain(decision.Reason))
        {
            _gpuRetry?.Start();
        }
    }

    /// <summary>
    /// A card is warm (the warm-up thread says so): a canvas still on the
    /// CPU, or drawing on another card's set while this one was prepared,
    /// moves over at its next frame.
    /// </summary>
    private void OnDeviceSetReady(GpuDeviceSet devices) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (!IsSceneOnGpu || !ReferenceEquals(GpuBootstrap.Decide(this).DeviceSet, _surface?.Devices))
            {
                RequestFrame(Layers.All);
            }
        });

    /// <summary>After a display change the monitors have been matched to their cards again: redraw if this one's card is not the one drawing it.</summary>
    private void OnMonitorsMatched() => Dispatcher.InvokeAsync(() => OnMonitorCheck(null, EventArgs.Empty));

    private void OnRendererPreferenceChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            RequestFrame(Layers.All);
        }
        else
        {
            Dispatcher.InvokeAsync(() => RequestFrame(Layers.All));
        }
    }

    /// <summary>
    /// The window moved.  Once it has stayed put for 150 ms, the canvas asks
    /// whether its monitor is driven by another card than the one drawing it
    /// and, if so, redraws - on that card's set, made ahead of time.  Not on
    /// every step of a drag across the boundary between two monitors.
    /// </summary>
    private void OnWindowMoved(object? sender, EventArgs e)
    {
        if (_monitorCheck is null)
        {
            _monitorCheck = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
            _monitorCheck.Tick += OnMonitorCheck;
        }

        _monitorCheck.Stop();
        _monitorCheck.Start();
    }

    private void OnMonitorCheck(object? sender, EventArgs e)
    {
        _monitorCheck?.Stop();
        if (!ReferenceEquals(GpuBootstrap.Decide(this).DeviceSet, _surface?.Devices))
        {
            RequestFrame(Layers.All);
        }
    }

    // ---- the names on the GPU --------------------------------------------------

    /// <summary>
    /// The label layer for a frame whose cells are on the GPU: the same
    /// <see cref="DrawLabelLayer"/> the WPF layer is recorded from, into
    /// <see cref="GpuLabelTarget"/>, so the names become glyph, icon and
    /// rectangle instances presented with the cells.  A frame that only
    /// redraws the names (an icon or a glyph arrived) presents again with the
    /// scene's instances as they were uploaded.
    ///
    /// False - the names go to WPF - when the cells are not on the GPU, or
    /// the atlases the names are drawn from could not be made.
    /// </summary>
    private bool TryDrawLabelsOnGpu(bool inMotion)
    {
        if (_surface is not { IsLost: false } surface
            || _surfaceLost
            || _renderer is null
            || _gpuFrame is null
            || GpuLabelAtlases.Current is not { } atlases)
        {
            return false;
        }

        if (surface.Devices.IsDisposed)
        {
            // Handed back as lost by another canvas on the same card: nothing
            // of it may be touched.  The next frame gives the surface up.
            _surfaceLost = true;
            RequestFrame(Layers.All);
            return false;
        }

        var target = EnsureGpuLabels(atlases);
        try
        {
            target.Begin(_gpuFrame, surface.Devices, ScenePixelWidth, ScenePixelHeight, _scaleX, _scaleY, snap: !inMotion);
            DrawLabelLayer(target);

            // The glyphs placed since the last frame go to the card here - and
            // when the atlas has outgrown its texture, a larger one is made -
            // so the card can fail here as much as in Begin.
            target.End();
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or ObjectDisposedException or GpuUnavailableException)
        {
            // The card failed making or filling the atlases' textures: as good
            // as lost.  This frame's names go to WPF over whatever the GPU
            // shows; the next frame gives the surface up.
            _surfaceLost = true;
            RequestFrame(Layers.All);
            return false;
        }

        LastUploadMilliseconds += target.LastUploadMilliseconds;
        if (target.WantsAnotherFrame)
        {
            RequestFrame(Layers.Labels);
        }

        _labelsOnGpu = true;
        _presentPending = true;
        return true;
    }

    /// <summary>
    /// The label layer recorded by WPF, as the canvas has always drawn it:
    /// on the CPU path, and on the GPU's cells when the names cannot go to
    /// the GPU.  Names left in the GPU's frame from before are taken out.
    /// </summary>
    private void DrawLabelsWithWpf()
    {
        if (_labelsOnGpu)
        {
            _labelsOnGpu = false;
            if (_gpuFrame is not null)
            {
                _gpuFrame.ClearLabels();
                _presentPending |= _surface is not null;
            }
        }

        using (var dc = _labelVisual.RenderOpen())
        {
            _wpfLabels.Begin(dc);
            try
            {
                DrawLabelLayer(_wpfLabels);
            }
            finally
            {
                _wpfLabels.End();
            }
        }

        _labelVisualRecorded = true;
    }

    /// <summary>
    /// The canvas's GPU label target and its text shaper, made the first time
    /// either is needed, and listening to the atlases for what arrives.
    /// </summary>
    private GpuLabelTarget EnsureGpuLabels(LabelAtlases atlases)
    {
        if (_gpuLabels is null || !ReferenceEquals(_labelAtlases, atlases))
        {
            UnhookLabelAtlases();
            _labelAtlases = atlases;
            _gpuLabels = new GpuLabelTarget(atlases.Faces, new TextShaper(atlases.Faces), atlases.Glyphs, atlases.Icons);
        }

        if (!_labelEventsHooked)
        {
            _labelEventsHooked = true;
            _gpuLabels.Shaper.Arrived += OnLabelMaterialArrived;
            atlases.Glyphs.GlyphsArrived += OnLabelMaterialArrived;
            atlases.Icons.ArrivalsPending += OnLabelMaterialArrived;
        }

        return _gpuLabels;
    }

    private void UnhookLabelAtlases()
    {
        if (!_labelEventsHooked || _labelAtlases is null || _gpuLabels is null)
        {
            return;
        }

        _labelEventsHooked = false;
        _gpuLabels.Shaper.Arrived -= OnLabelMaterialArrived;
        _labelAtlases.Glyphs.GlyphsArrived -= OnLabelMaterialArrived;
        _labelAtlases.Icons.ArrivalsPending -= OnLabelMaterialArrived;
    }

    /// <summary>
    /// A name was shaped, a glyph made or an icon found, on one of the
    /// atlases' threads: one request to redraw the names reaches the UI
    /// thread, however many arrive before it is served.
    /// </summary>
    private void OnLabelMaterialArrived()
    {
        if (Interlocked.Exchange(ref _labelMaterialQueued, 1) == 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, _requestLabelsFrame);
        }
    }

    private void RequestLabelsFrame()
    {
        Volatile.Write(ref _labelMaterialQueued, 0);
        if (_labelsOnGpu)
        {
            RequestFrame(Layers.Labels);
        }
    }

    /// <summary>
    /// A folder was read: its files' types go to the icon atlas's workers at
    /// once, so a zoom into it finds its icons waiting, and so do the names
    /// the UI font cannot show - Arabic, Hebrew, CJK, emoji - which are shaped
    /// on the text shaper's worker and would otherwise be left out of the
    /// frame they first appear in.  Latin, Greek and Cyrillic names, nearly
    /// all of them, are shaped when first drawn, a few microseconds each:
    /// measured, that costs the UI thread less than keeping the books on tens
    /// of thousands of names ahead of time (up to 7 ms for one folder read),
    /// and the heap does not fill with names nobody zooms to.  Only while the
    /// GPU draws or may draw the canvas (<see cref="GpuMayDrawNames"/>): on
    /// the CPU none of it would ever be used.
    /// </summary>
    private void OnFolderLoadedForGpu(NestedFolder folder)
    {
        if (!GpuMayDrawNames || GpuLabelAtlases.Current is not { } atlases || PresentationSource.FromVisual(this) is null)
        {
            return;
        }

        var files = folder.Files;
        atlases.Icons.Prefetch(files);

        List<string>? folderNames = null;
        foreach (var child in folder.Children)
        {
            if (NeedsFallbackShaping(child.Name))
            {
                (folderNames ??= []).Add(child.Name);
            }
        }

        List<string>? fileNames = null;
        for (var index = 0; index < files.Count; index++)
        {
            var name = files[index].Name;
            if (NeedsFallbackShaping(name))
            {
                (fileNames ??= []).Add(name);
            }
        }

        if (folderNames is null && fileNames is null)
        {
            return;
        }

        var shaper = EnsureGpuLabels(atlases).Shaper;
        if (folderNames is not null)
        {
            // A drive's name is set in the semibold face, a folder's in the regular.
            shaper.Prefetch(folderNames, folder.IsComputer ? (byte)LabelFace.SemiBold : (byte)LabelFace.Regular);
        }

        if (fileNames is not null)
        {
            shaper.Prefetch(fileNames, (byte)LabelFace.Regular);
        }
    }

    /// <summary>
    /// Whether the GPU draws this canvas's names or may soon: false when the
    /// CPU was chosen, when the GPU was lost too often this session, and when
    /// the last frame's answer was one that does not change by waiting - WPF
    /// below render tier 2 or in software, a remote session.  A canvas that
    /// has not drawn a frame yet, or whose card is warming up or being tried
    /// again, may.
    /// </summary>
    private bool GpuMayDrawNames =>
        GpuBootstrap.Preference != RendererPreference.Cpu
        && !GpuBootstrap.IsCpuForSession
        && RendererReason != GpuBootstrap.ReasonCpuChosen
        && RendererReason != GpuBootstrap.ReasonLostTooOften
        && RendererReason != GpuBootstrap.ReasonRenderTier
        && RendererReason != GpuBootstrap.ReasonSoftwareRendering
        && RendererReason != GpuBootstrap.ReasonRemoteSession;

    /// <summary>Whether a name holds a character past Latin, Greek and Cyrillic, which the shaper's worker lays out.</summary>
    private static bool NeedsFallbackShaping(string name)
    {
        foreach (var character in name)
        {
            if (character >= FallbackShapingFrom)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Armenian onwards: the first script past the ones Segoe UI Variable holds.</summary>
    private const char FallbackShapingFrom = (char)0x0530;

    /// <summary>
    /// For tests: the current view walked through <see cref="GpuSink"/> into
    /// <paramref name="frame"/> and drawn by <paramref name="renderer"/> into
    /// <paramref name="target"/>, which must be at least
    /// <see cref="ScenePixelWidth"/> x <see cref="ScenePixelHeight"/> - the
    /// cells, and with <paramref name="labels"/> the names too, drawn through
    /// that target as a frame at rest (or in motion, with
    /// <paramref name="inMotion"/>) draws them.  Nothing is shown and no
    /// window is needed; the same walk the canvas's own frames make, so the
    /// picture can be held against the raster's <see cref="SceneBitmap"/> and
    /// WPF's names of the view.
    /// </summary>
    internal void RenderOffscreen(NestedGpuRenderer renderer, OffscreenTarget target, NestedGpuFrame frame, GpuLabelTarget? labels = null, bool inMotion = false)
    {
        if (ActualWidth < 1 || ActualHeight < 1 || _tree is null)
        {
            return;
        }

        _viewWidth = ActualWidth;
        _viewHeight = ActualHeight;
        UpdateScale();
        EnsureCamera();
        Normalize();
        BuildChain();
        RenderScene(inMotion, new OffscreenScene(renderer, target, frame, labels));
    }

    private void PaintSceneOffscreen(OffscreenScene offscreen, int pixelWidth, int pixelHeight, bool inMotion)
    {
        var sink = _gpuSink ??= new GpuSink();
        var frame = offscreen.Frame;
        var walkStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        sink.Begin(frame.SceneRects, pixelWidth, pixelHeight, CanvasColour);
        WalkScene(sink);
        frame.ClearColour = sink.ClearColour;
        frame.SceneChanged();
        LastWalkMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(walkStarted).TotalMilliseconds;
        if (offscreen.Labels is { } labels)
        {
            var labelsStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            labels.Begin(frame, offscreen.Renderer.Devices, pixelWidth, pixelHeight, _scaleX, _scaleY, snap: !inMotion);
            DrawLabelLayer(labels);
            labels.End();
            LastLabelsMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(labelsStarted).TotalMilliseconds;
        }

        offscreen.Renderer.Draw(offscreen.Target.RenderTargetView, pixelWidth, pixelHeight, frame);
        LastGpuInstances = offscreen.Renderer.LastInstances;
        LastGpuGlyphs = offscreen.Renderer.LastGlyphs;
        LastGpuIcons = offscreen.Renderer.LastIcons;
    }

    /// <summary>For tests: the bitmap the raster painted the last CPU frame's scene into, or null.</summary>
    internal WriteableBitmap? SceneBitmap => _bitmap;

    /// <summary>For tests: the layers the cells and the names are recorded in, to draw the WPF picture of a view without its marks and outlines.</summary>
    internal Visual SceneLayer => _sceneVisual;

    internal Visual LabelLayer => _labelVisual;

    /// <summary>For tests: the folder names the last label layer drew, by which folders can be grabbed.</summary>
    internal int LabelGrabCount => _labelHotspots.Count;

    /// <summary>For the bench: the shaper the GPU's names come from, once there is one.</summary>
    internal TextShaper? GpuTextShaper => _gpuLabels?.Shaper;

    /// <summary>For tests: the scene's size in device pixels, the part of the bitmap or surface a frame draws.</summary>
    internal int ScenePixelWidth => Math.Max(1, (int)Math.Ceiling(_viewWidth * _scaleX));

    internal int ScenePixelHeight => Math.Max(1, (int)Math.Ceiling(_viewHeight * _scaleY));

    /// <summary>Where a test's offscreen frame goes: the renderer, the texture, the instance lists and, when the names are wanted, their target.</summary>
    private readonly record struct OffscreenScene(NestedGpuRenderer Renderer, OffscreenTarget Target, NestedGpuFrame Frame, GpuLabelTarget? Labels);

    /// <summary>
    /// The walk itself, into whichever sink the frame draws with: the bare
    /// canvas where no folder covers the view, then the covering folder and
    /// everything on screen inside it.  Besides the shapes it hands the sink,
    /// it leaves the frame's labels, file labels and filter outlines listed
    /// for the layers above, and asks the tree for what should be read -
    /// the same whatever the sink.
    /// </summary>
    private void WalkScene(SceneSink sink)
    {
        // Everything a walk leaves behind starts empty, so a frame whose GPU
        // failed half way can be walked again into the bitmap.
        _labels.Clear();
        _labelled.Clear();
        _fileLabels.Clear();
        _filterOutlines.Clear();
        DrawnCellCount = 0;
        _foldersDrawn = 0;
        _tilesDrawn = 0;
        _relayoutSpent = 0;
        _drewStale = false;
        _drawnStale.Clear();

        _sink = sink;
        var (cover, x, y, w, covers) = CoverCell();
        if (!covers)
        {
            sink.Clear(CanvasColour);
        }

        DrawCell(cover, x, y, w, labelsAllowed: true);
    }

    /// <summary>
    /// The deepest folder on the anchor's line whose cell covers the whole
    /// view.  Everything outside it is off screen, so the walk starts there:
    /// deep in, the ten folders above it would each fill the screen with a
    /// colour only to be painted over by the next.
    /// </summary>
    private (NestedFolder Folder, double X, double Y, double W, bool Covers) CoverCell()
    {
        for (var folder = _anchor; folder is not null; folder = folder.Parent)
        {
            if (!_chain.TryGetValue(folder, out var rect))
            {
                break;
            }

            if (Covers(rect.X, rect.Y, rect.W))
            {
                return (folder, rect.X, rect.Y, rect.W, true);
            }
        }

        var (rx, ry, rw) = _chain[_tree!.Root];
        return (_tree.Root, rx, ry, rw, false);
    }

    /// <summary>
    /// Whether a cell covers the view with room to spare for its rim and its
    /// rounded corners (at most 6 DIPs), so that nothing of what lies outside
    /// it can show at the view's corners.
    /// </summary>
    private bool Covers(double x, double y, double w) =>
        x <= -8 && y <= -8 && x + w >= _viewWidth + 8 && y + w * NestedLayout.CellHeight >= _viewHeight + 8;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private void DrawCell(NestedFolder folder, double x, double y, double w, bool labelsAllowed)
    {
        var h = w * NestedLayout.CellHeight;
        if (x >= _viewWidth || y >= _viewHeight || x + w <= 0 || y + h <= 0 || w < _minCell)
        {
            return;
        }

        DrawnCellCount++;
        if (++_foldersDrawn > MaximumCellsPerFrame)
        {
            return;
        }

        // Placed for the current order before its children and files are
        // walked - within the frame's allowance for placing; past it, as it
        // was placed, and noted for the tree to place first.
        var stamp = folder.LayoutSortGeneration;
        if (stamp >= 0 && stamp != _tree!.SortGeneration)
        {
            if (_relayoutSpent < _relayoutAllowance)
            {
                var placing = System.Diagnostics.Stopwatch.GetTimestamp();
                _tree.EnsureLayout(folder);
                _relayoutSpent += System.Diagnostics.Stopwatch.GetTimestamp() - placing;
            }
            else
            {
                _drewStale = true;
                if (_drawnStale.Count < MaximumPlacedFirst)
                {
                    _drawnStale.Add(folder);
                }
            }
        }

        EnsurePalette(folder);
        PaintCell(folder, x, y, w, h);
        if (_filter is not null && w >= 6 && _filterOutlines.Count < 600 && (FilterStateOf(folder) & FilterSelf) != 0)
        {
            _filterOutlines.Add(new Rect(x, y, w, h));
        }

        if (w >= LoadPixels && folder.CanLoad)
        {
            _tree!.Request(folder, w);
        }

        var labelMode = LabelModeFor(w, labelsAllowed);
        if (labelMode != LabelMode.None)
        {
            _labels.Add(new LabelJob(folder, x, y, w, labelMode));
            _labelled.Add(folder);
        }

        var childLabels = labelMode == LabelMode.Header;
        DrawFiles(folder, x, y, w, childLabels);

        var grid = folder.Grid;
        if (grid.IsEmpty || w * grid.Scale < _minCell)
        {
            return;
        }

        var (firstColumn, lastColumn, firstRow, lastRow) = grid.Overlapping(
            -x / w,
            -y / w,
            (_viewWidth - x) / w,
            (_viewHeight - y) / w);
        var children = folder.Children;
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var index = row * grid.Columns + column;
                if (index >= children.Count)
                {
                    break;
                }

                var child = children[index];
                if (_chain.TryGetValue(child, out var exact))
                {
                    DrawCell(child, exact.X, exact.Y, exact.W, childLabels);
                }
                else
                {
                    DrawCell(child, x + child.OffsetX * w, y + child.OffsetY * w, w * child.Scale, childLabels);
                }
            }
        }
    }

    /// <summary>
    /// A folder's files, as tiles under its sub-folders.  Too small to tell
    /// apart, they are one faint wash over the strip they occupy: the folder
    /// still visibly holds files, without drawing a thousand specks.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private void DrawFiles(NestedFolder folder, double x, double y, double w, bool labelsAllowed)
    {
        var grid = folder.FileGrid;
        if (grid.IsEmpty)
        {
            return;
        }

        var tileWidth = grid.TileWidth * w;
        var tileHeight = grid.TileHeight * w;
        if (tileWidth * _scaleX < _minTile)
        {
            if (w * _scaleX < 12)
            {
                return;
            }

            var usedWidth = Math.Min(NestedLayout.ContentWidth, grid.Columns * grid.StepX - grid.Gap);
            var usedHeight = Math.Min(
                NestedLayout.CellHeight - NestedLayout.Padding - grid.Top,
                grid.Rows * grid.StepY - grid.Gap);
            _sink.Fill(
                NestedRaster.Px((x + grid.Left * w) * _scaleX),
                NestedRaster.Px((y + grid.Top * w) * _scaleY),
                NestedRaster.Px((x + (grid.Left + usedWidth) * w) * _scaleX),
                NestedRaster.Px((y + (grid.Top + usedHeight) * w) * _scaleY),
                NestedRaster.Mix(folder.BodyColour, 0xFF8A8F96, 0.1));
            return;
        }

        var (firstColumn, lastColumn, firstRow, lastRow) = grid.Overlapping(
            -x / w,
            -y / w,
            (_viewWidth - x) / w,
            (_viewHeight - y) / w);
        var files = folder.Files;
        var labels = labelsAllowed && tileHeight >= FileLabelPixels;
        var visibleTiles = (long)(lastColumn - firstColumn + 1) * (lastRow - firstRow + 1);
        if (_tilesDrawn + visibleTiles > MaximumTilesPerFrame)
        {
            // Past the frame's budget for tiles, a folder's files are its wash:
            // detail is what goes, never a whole folder further along.
            var zoneWidth = Math.Min(NestedLayout.ContentWidth, grid.Columns * grid.StepX - grid.Gap);
            var zoneHeight = Math.Min(NestedLayout.CellHeight - NestedLayout.Padding - grid.Top, grid.Rows * grid.StepY - grid.Gap);
            _sink.Fill(
                NestedRaster.Px((x + grid.Left * w) * _scaleX),
                NestedRaster.Px((y + grid.Top * w) * _scaleY),
                NestedRaster.Px((x + (grid.Left + zoneWidth) * w) * _scaleX),
                NestedRaster.Px((y + (grid.Top + zoneHeight) * w) * _scaleY),
                NestedRaster.Mix(folder.BodyColour, 0xFF8A8F96, 0.1));
            return;
        }

        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var index = row * grid.Columns + column;
                if (index >= files.Count)
                {
                    break;
                }

                DrawnCellCount++;
                _tilesDrawn++;

                var fx = x + (grid.Left + column * grid.StepX) * w;
                var fy = y + (grid.Top + row * grid.StepY) * w;
                PaintFile(files[index], fx, fy, tileWidth, tileHeight);
                if (labels)
                {
                    _fileLabels.Add(new FileLabelJob(folder, index, fx, fy, tileWidth, tileHeight));
                }
            }
        }
    }

    private void PaintFile(NestedFile file, double x, double y, double w, double h)
    {
        var (body, stripe, speck) = FilePalette(file.Extension);
        if (_filter is not null)
        {
            if (_filter(file.Name))
            {
                body = NestedRaster.Mix(body, FilterColour, 0.3);
                stripe = FilterColour;
                speck = FilterColour;
            }
            else
            {
                body = NestedRaster.Mix(body, CanvasColour, 0.72);
                stripe = NestedRaster.Mix(stripe, CanvasColour, 0.72);
                speck = NestedRaster.Mix(speck, CanvasColour, 0.72);
            }
        }

        if (file.IsHidden)
        {
            body = NestedRaster.Mix(body, CanvasColour, 0.45);
            stripe = NestedRaster.Mix(stripe, CanvasColour, 0.45);
            speck = NestedRaster.Mix(speck, CanvasColour, 0.45);
        }

        var left = x * _scaleX;
        var top = y * _scaleY;
        var right = (x + w) * _scaleX;
        var bottom = (y + h) * _scaleY;
        if (right - left < 6 || bottom - top < 3)
        {
            _sink.Fill(
                NestedRaster.PxFloor(left),
                NestedRaster.PxFloor(top),
                Math.Max(NestedRaster.PxFloor(left) + 1, NestedRaster.Px(right)),
                Math.Max(NestedRaster.PxFloor(top) + 1, NestedRaster.Px(bottom)),
                speck);
            return;
        }

        // The coloured edge says what kind of file it is before the name can.
        var height = bottom - top;
        var stripeWidth = Math.Max(1, Math.Min(3 * _scaleX, height * 0.14));
        var inset = Math.Max(1, height * 0.18);
        _sink.File(
            left,
            top,
            right,
            bottom,
            Math.Min(3 * _scaleX, height * 0.2),
            NestedRaster.Px(left + 1),
            NestedRaster.Px(top + inset),
            NestedRaster.Px(left + 1 + stripeWidth),
            NestedRaster.Px(bottom - inset),
            body,
            stripe);
    }

    /// <summary>
    /// A file's colours come from its extension, the way a folder's come from
    /// its path: every .png the same hue, every .cs another, stable across runs.
    /// </summary>
    private (uint Body, uint Stripe, uint Speck) FilePalette(string extension)
    {
        if (_filePalette.TryGetValue(extension, out var palette))
        {
            return palette;
        }

        var hash = 2166136261u;
        foreach (var character in extension)
        {
            hash = (hash ^ character) * 16777619u;
        }

        var hue = extension.Length == 0 ? 210 : hash % 360u;
        var saturation = extension.Length == 0 ? 0.05 : 0.14;
        var body = NestedRaster.FromHsl(hue, saturation, 0.19);
        var stripe = NestedRaster.FromHsl(hue, extension.Length == 0 ? 0.08 : 0.6, 0.6);
        palette = (body, stripe, NestedRaster.Mix(body, stripe, 0.45));
        _filePalette[extension] = palette;
        return palette;
    }

    /// <summary>
    /// The names on file tiles big enough to carry one, into any target: the
    /// icon, the colour mark and the note sign, the size when there is room
    /// for it, and the name in what is left.
    /// </summary>
    private void DrawFileLabels(LabelTarget target)
    {
        foreach (var job in _fileLabels)
        {
            if (job.Index >= job.Folder.Files.Count)
            {
                continue;
            }

            var file = job.Folder.Files[job.Index];
            var font = Math.Clamp(job.H * 0.5, 7.5, 13);
            var cursor = job.X + Math.Min(3, job.H * 0.14) + font * 0.55;
            var right = job.X + job.W - font * 0.5;
            var facts = FactsOf(job.Folder, file);

            var iconSize = Math.Min(job.H * 0.72, 20);
            if (iconSize >= 9 && target.DrawIcon(facts.Path, file.Extension, new Rect(cursor, job.Y + (job.H - iconSize) / 2, iconSize, iconSize)))
            {
                cursor += iconSize + font * 0.4;
            }

            var mark = MarkOf(facts);
            if (facts.HasAccent)
            {
                target.FillRect(new Rect(job.X + 1, job.Y + job.H * 0.12, Math.Max(2, job.H * 0.16), job.H * 0.76), facts.Accent);
            }

            if (!string.IsNullOrWhiteSpace(mark.Note))
            {
                var note = target.Text("\uE70B", font * 0.85, TextDimColour, double.MaxValue, LabelFace.Icons, scaled: true);
                right -= note.Width;
                target.DrawText(note, new Point(right, job.Y + (job.H - note.Height) / 2));
                right -= font * 0.4;
            }

            if (job.W >= 190)
            {
                var size = target.Text(FormatSize(file.Length), font * 0.85, TextDimColour, double.MaxValue, LabelFace.Regular, scaled: true);
                if (right - size.Width - cursor > font * 5)
                {
                    right -= size.Width;
                    target.DrawText(size, new Point(right, job.Y + (job.H - size.Height) / 2));
                    right -= font * 0.6;
                }
            }

            if (right - cursor > font)
            {
                var faded = file.IsHidden || _filter is not null && !_filter(file.Name);
                var name = target.Text(file.Name, font, faded ? TextDimColour : TextColour, right - cursor, LabelFace.Regular, scaled: true);
                target.DrawText(name, new Point(cursor, job.Y + (job.H - name.Height) / 2));
            }
        }
    }

    /// <summary>
    /// What a file's label needs to know besides the file, found once and
    /// kept while its tile stays in view: its path, joined from its folder's
    /// and its name, and its mark.  Every name on screen needs both each frame
    /// - the path for its icon, the mark for its colour and note - and asking
    /// afresh was new strings per name per frame, both for the join and for
    /// the mark lookup's own tidying of the path.
    /// </summary>
    private sealed class FileFacts(string path)
    {
        public string Path { get; } = path;

        /// <summary>The <see cref="_paletteStamp"/> <see cref="Mark"/> was looked up under, or -1 before it was.</summary>
        public int MarkStamp { get; set; } = -1;

        public FolderMark Mark { get; set; } = FolderMark.None;

        /// <summary>Whether the mark has a colour, read with it, so the colour is parsed once rather than every frame.</summary>
        public bool HasAccent { get; set; }

        public Color Accent { get; set; }
    }

    /// <summary>
    /// A file's <see cref="FileFacts"/>.  Found by the very strings of the
    /// folder's path and the file's name, not their text, so a lookup hashes
    /// no characters, and holds no folder: a folder read again has new names,
    /// and simply misses.  Two generations, like the text layouts, so it stays
    /// the size of what is on screen.
    /// </summary>
    private FileFacts FactsOf(NestedFolder folder, in NestedFile file)
    {
        var key = (folder.FullPath, file.Name);
        if (_fileFacts.TryGetValue(key, out var facts))
        {
            return facts;
        }

        if (!_oldFileFacts.Remove(key, out facts))
        {
            facts = new FileFacts(folder.PathOf(file));
        }

        if (_fileFacts.Count >= MaximumKeptFiles)
        {
            (_oldFileFacts, _fileFacts) = (_fileFacts, _oldFileFacts);
            _fileFacts.Clear();
        }

        _fileFacts[key] = facts;
        return facts;
    }

    /// <summary>
    /// A file's mark, looked up again only when marks may have changed: kept
    /// under the same stamp that keeps a folder's colours, which every change
    /// of a mark moves on (<see cref="InvalidateMarks"/>), so a file's mark is
    /// exactly as fresh as its folder's.
    /// </summary>
    private FolderMark MarkOf(FileFacts facts)
    {
        if (facts.MarkStamp != _paletteStamp)
        {
            facts.Mark = _markLookup?.Invoke(facts.Path) ?? FolderMark.None;
            facts.MarkStamp = _paletteStamp;
            facts.HasAccent = TryParseColour(facts.Mark.AccentHex, out var accent);
            facts.Accent = accent;
        }

        return facts.Mark;
    }

    private static readonly string[] SizeSuffixes = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>A file's size as its tile and its tag say it, made once per size rather than once per frame.</summary>
    private string FormatSize(long bytes)
    {
        if (_sizeTexts.TryGet(bytes, out var known))
        {
            return known;
        }

        double value = bytes;
        var suffix = 0;
        while (value >= 1024 && suffix < SizeSuffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }

        return _sizeTexts.Add(bytes, suffix == 0 ? $"{bytes:N0} B" : $"{value:0.#} {SizeSuffixes[suffix]}");
    }

    private static LabelMode LabelModeFor(double w, bool labelsAllowed)
    {
        if (!labelsAllowed)
        {
            return LabelMode.None;
        }

        var font = Math.Min(MaximumFontSize, w * NestedLayout.HeaderHeight * 0.62);
        if (font >= MinimumHeaderFont && w >= 48)
        {
            return LabelMode.Header;
        }

        return w >= PillMinimumWidth ? LabelMode.Pill : LabelMode.None;
    }

    private void PaintCell(NestedFolder folder, double x, double y, double w, double h)
    {
        var left = x * _scaleX;
        var top = y * _scaleY;
        var right = (x + w) * _scaleX;
        var bottom = (y + h) * _scaleY;
        var pixelWidth = right - left;

        if (pixelWidth < 4)
        {
            // A speck: one colour, the rim's, which reads against any parent.
            _sink.Fill(
                NestedRaster.PxFloor(left),
                NestedRaster.PxFloor(top),
                Math.Max(NestedRaster.PxFloor(left) + 1, NestedRaster.Px(right)),
                Math.Max(NestedRaster.PxFloor(top) + 1, NestedRaster.Px(bottom)),
                folder.HasLabel ? folder.StripeColour : folder.RimColour);
            return;
        }

        var radius = pixelWidth >= 40 ? Math.Min(6 * _scaleX, pixelWidth * 0.03) : 0;

        // The title band, when the cell is tall enough to show one, and on it
        // the stripe in the folder's own colour: what the stripe on a tree
        // node was, a sign of whose this is.
        var headerBottom = double.NaN;
        var hasStripe = false;
        int stripeLeft = 0, stripeTop = 0, stripeRight = 0, stripeBottom = 0;
        var header = w * NestedLayout.HeaderHeight * _scaleY;
        if (header >= 2)
        {
            headerBottom = top + header;
            if (header >= 6 && pixelWidth >= 30)
            {
                var inset = Math.Max(1, header * 0.2);
                var stripe = Math.Max(2, Math.Min(4 * _scaleX, header * 0.12));
                var stripeStart = left + 1 + Math.Max(2 * _scaleX, header * 0.18);
                hasStripe = true;
                stripeLeft = NestedRaster.Px(stripeStart);
                stripeTop = NestedRaster.Px(top + inset);
                stripeRight = NestedRaster.Px(stripeStart + stripe);
                stripeBottom = NestedRaster.Px(top + header - inset);
            }
        }

        _sink.Cell(
            left,
            top,
            right,
            bottom,
            radius,
            headerBottom,
            hasStripe,
            stripeLeft,
            stripeTop,
            stripeRight,
            stripeBottom,
            folder.RimColour,
            folder.BodyColour,
            folder.HeaderColour,
            folder.StripeColour);
    }

    /// <summary>
    /// A cell's colours: a dark tint of the folder's own hue, one step lighter
    /// on every other level so a child reads against its parent, and the
    /// user's colour label over the title when there is one.
    /// </summary>
    private void EnsurePalette(NestedFolder folder)
    {
        if (folder.PaletteStamp == _paletteStamp)
        {
            return;
        }

        folder.PaletteStamp = _paletteStamp;
        var mark = folder.IsComputer ? FolderMark.None : _markLookup?.Invoke(folder.FullPath) ?? FolderMark.None;
        folder.HasNote = !string.IsNullOrWhiteSpace(mark.Note);

        var hue = folder.Hue;
        var saturation = folder.Kind switch
        {
            NestedFolderKind.Computer => 0.0,
            NestedFolderKind.Drive => 0.08,
            _ => 0.2
        };
        var lightness = folder.IsComputer ? 0.075 : 0.112 + 0.03 * (folder.Depth % 2);
        var body = NestedRaster.FromHsl(hue, saturation, lightness);
        var header = NestedRaster.FromHsl(hue, saturation + 0.06, lightness + 0.05);
        var rim = NestedRaster.FromHsl(hue, Math.Min(0.32, saturation + 0.12), folder.IsComputer ? 0.16 : 0.27);
        var stripe = NestedRaster.FromHsl(hue, 0.52, Math.Clamp(0.66 - folder.Depth * 0.022, 0.42, 0.66));

        folder.HasLabel = false;
        if (TryParseColour(mark.AccentHex, out var label))
        {
            var packed = NestedRaster.Pack(label);
            header = NestedRaster.Mix(header, packed, 0.5);
            body = NestedRaster.Mix(body, packed, 0.1);
            rim = NestedRaster.Mix(rim, packed, 0.9);
            stripe = packed;
            folder.HasLabel = true;
        }

        if (folder.IsHidden)
        {
            body = NestedRaster.Mix(body, CanvasColour, 0.4);
            header = NestedRaster.Mix(header, CanvasColour, 0.4);
            rim = NestedRaster.Mix(rim, CanvasColour, 0.4);
        }

        if (_filter is not null && !folder.IsComputer)
        {
            var state = FilterStateOf(folder);
            if ((state & FilterSelf) != 0)
            {
                // A match: its own colours, and a title in the filter's colour.
                header = NestedRaster.Mix(header, FilterColour, 0.45);
                rim = FilterColour;
            }
            else if ((state & FilterInside) == 0)
            {
                // Faded: far for what is known not to match, less for what
                // has not been read and still might.
                var fade = (state & FilterUnknown) != 0 ? 0.45 : 0.72;
                body = NestedRaster.Mix(body, CanvasColour, fade);
                header = NestedRaster.Mix(header, CanvasColour, fade);
                rim = NestedRaster.Mix(rim, CanvasColour, fade);
                stripe = NestedRaster.Mix(stripe, CanvasColour, fade);
            }
        }

        folder.BodyColour = body;
        folder.HeaderColour = header;
        folder.RimColour = rim;
        folder.StripeColour = stripe;
    }

    /// <summary>
    /// The names on the cells, into any target: folder titles and pills first,
    /// then the file names over them.  The handles to grab a folder by its
    /// name are gathered again with them, so what can be grabbed is exactly
    /// what was drawn, whatever drew it.
    /// </summary>
    private void DrawLabelLayer(LabelTarget target)
    {
        _labelHotspots.Clear();
        DrawLabels(target);
        DrawFileLabels(target);
    }

    private void DrawLabels(LabelTarget target)
    {
        var pixelsPerDip = _scaleY;
        foreach (var job in _labels)
        {
            var folder = job.Folder;
            if (job.Mode == LabelMode.Pill)
            {
                var text = target.Text(folder.Name, PillFontSize, IsFilteredOut(folder) ? TextDimColour : TextColour, Math.Max(8, job.W - 10), LabelFace.Regular, scaled: false);
                var pill = new Rect(job.X + 2, job.Y + 2, Math.Min(job.W - 4, text.Width + 8), text.Height + 2);
                if (pill.Width < 12)
                {
                    continue;
                }

                target.FillRounded(pill, 3, PillColour);
                target.DrawText(text, new Point(pill.X + 4, pill.Y + 1));
                AddGrabHotspot(folder, pill);
                continue;
            }

            var header = job.W * NestedLayout.HeaderHeight;
            if (job.Y + header < 0 || job.Y > _viewHeight)
            {
                DrawBodyNote(target, folder, job);
                continue;
            }

            var font = Math.Min(MaximumFontSize, header * 0.62);
            var stripeRight = 1 + Math.Max(2, header * 0.18) + Math.Max(2 / pixelsPerDip, Math.Min(4, header * 0.12));

            // A cell wider than the screen keeps its name at the visible edge,
            // the way a sticky column header does: its own left edge may be a
            // whole screen away, and a title nobody can see names nothing.
            var cursor = job.X >= 0 ? job.X + stripeRight + font * 0.45 : font * 0.6;
            var right = Math.Min(job.X + job.W, _viewWidth) - font * 0.6;

            // Right-hand details first, so the name knows how much room is left.
            var badges = Badges(folder);
            if (badges.Length > 0)
            {
                var icons = target.Text(badges, font * 0.82, BadgeColour(folder), double.MaxValue, LabelFace.Icons, scaled: true);
                right -= icons.Width;
                if (right - cursor > font * 3)
                {
                    target.DrawText(icons, new Point(right, job.Y + (header - icons.Height) / 2));
                    right -= font * 0.4;
                }
                else
                {
                    right += icons.Width;
                }
            }

            if (job.W >= 280 && DetailText(folder) is { Length: > 0 } detail)
            {
                var info = target.Text(detail, font * 0.78, TextDimColour, double.MaxValue, LabelFace.Regular, scaled: true);
                if (right - info.Width - cursor > font * 6)
                {
                    right -= info.Width;
                    target.DrawText(info, new Point(right, job.Y + (header - info.Height) / 2));
                    right -= font * 0.6;
                }
            }

            var glyph = target.Text(Glyph(folder), font * 0.9, GlyphColour(folder), double.MaxValue, LabelFace.Icons, scaled: true);
            if (right - cursor > glyph.Width + font)
            {
                target.DrawText(glyph, new Point(cursor, job.Y + (header - glyph.Height) / 2 + font * 0.05));
                cursor += glyph.Width + font * 0.4;
            }

            var available = right - cursor;
            if (available > font)
            {
                var face = folder.Kind != NestedFolderKind.Folder ? LabelFace.SemiBold : LabelFace.Regular;
                var name = target.Text(folder.Name, font, IsFilteredOut(folder) ? TextDimColour : TextColour, available, face, scaled: true);
                target.DrawText(name, new Point(cursor, job.Y + (header - name.Height) / 2));
                AddGrabHotspot(folder, new Rect(job.X, job.Y, job.W, header));
            }

            DrawBodyNote(target, folder, job);
        }
    }

    /// <summary>What an empty cell says in its middle, when there is room to say it.</summary>
    private void DrawBodyNote(LabelTarget target, NestedFolder folder, LabelJob job)
    {
        var h = job.W * NestedLayout.CellHeight;
        var header = job.W * NestedLayout.HeaderHeight;
        if (h - header < 40 || job.W < 120)
        {
            return;
        }

        string message;
        var ink = TextDimColour;
        if (folder.IsReparsePoint)
        {
            message = "Link to another folder";
        }
        else if (folder.LoadState == NestedLoadState.Failed)
        {
            message = string.IsNullOrEmpty(folder.ErrorMessage) ? "Could not be read" : folder.ErrorMessage;
            ink = DangerColour;
        }
        else if (folder.LoadState is NestedLoadState.NotLoaded or NestedLoadState.Queued or NestedLoadState.Loading)
        {
            message = folder.IsComputer ? string.Empty : "Reading…";
        }
        else if (folder.Children.Count == 0 && folder.Files.Count == 0)
        {
            message = folder.FileCount switch
            {
                0 => "Empty folder",
                1 => "1 hidden file",
                _ => NoteText(HiddenFilesNote, folder.FileCount)
            };
        }
        else if (folder.UnlistedFileCount > 0)
        {
            message = NoteText(UnlistedFilesNote, folder.UnlistedFileCount);
            var text = target.Text(message, Math.Clamp(job.W * 0.018, 9, 14), TextDimColour, job.W - 16, LabelFace.Regular, scaled: true);
            target.DrawText(text, new Point(job.X + (job.W - text.Width) / 2, job.Y + h - text.Height - 4));
            return;
        }
        else if (folder.IsTruncated)
        {
            message = NoteText(TruncatedNote, NestedTree.MaximumChildren);
            var text = target.Text(message, Math.Clamp(job.W * 0.018, 9, 14), TextDimColour, job.W - 16, LabelFace.Regular, scaled: true);
            target.DrawText(text, new Point(job.X + (job.W - text.Width) / 2, job.Y + h - text.Height - 4));
            return;
        }
        else
        {
            return;
        }

        if (message.Length == 0)
        {
            return;
        }

        var size = Math.Clamp(job.W * 0.03, 9, 18);
        var formatted = target.Text(message, size, ink, job.W - 16, LabelFace.Regular, scaled: true);
        var bodyTop = job.Y + header;
        target.DrawText(formatted, new Point(job.X + (job.W - formatted.Width) / 2, bodyTop + (h - header - formatted.Height) / 2));
    }

    private const int HiddenFilesNote = 1;
    private const int UnlistedFilesNote = 2;
    private const int TruncatedNote = 3;

    /// <summary>
    /// One of the notes with a count in it, made once per count rather than
    /// once per frame.
    /// </summary>
    private string NoteText(int note, int count)
    {
        var key = (long)note << 32 | (uint)count;
        if (_noteTexts.TryGet(key, out var known))
        {
            return known;
        }

        return _noteTexts.Add(key, note switch
        {
            HiddenFilesNote => $"{count:N0} hidden files",
            UnlistedFilesNote => $"{count:N0} more files are not drawn",
            _ => $"Only the first {count:N0} folders are shown"
        });
    }

    /// <summary>
    /// What a title says on its right: a drive's free space, or how many
    /// folders and files a folder holds.  The counts' words are made once per
    /// pair of counts rather than once per title per frame.
    /// </summary>
    private string DetailText(NestedFolder folder)
    {
        if (folder.IsComputer)
        {
            return string.Empty;
        }

        if (folder.Kind == NestedFolderKind.Drive && !string.IsNullOrEmpty(folder.SecondaryText))
        {
            return folder.SecondaryText;
        }

        if (folder.LoadState != NestedLoadState.Loaded)
        {
            return string.Empty;
        }

        var folders = folder.Children.Count;
        var files = folder.FileCount;
        if (folders == 0 && files == 0)
        {
            return string.Empty;
        }

        var key = (long)folders << 32 | (uint)files;
        if (_detailTexts.TryGet(key, out var known))
        {
            return known;
        }

        var parts = new List<string>(2);
        if (folders > 0)
        {
            parts.Add(folders == 1 ? "1 folder" : $"{folders:N0} folders");
        }

        if (files > 0)
        {
            parts.Add(files == 1 ? "1 file" : $"{files:N0} files");
        }

        return _detailTexts.Add(key, string.Join("  ·  ", parts));
    }

    /// <summary>
    /// A title's badges for every mix of them, written in the order a title
    /// shows them - pinned, noted, a link, unreadable - and indexed by those
    /// four as bits: a title's badges are looked up, not joined anew each frame.
    /// </summary>
    private static readonly string[] BadgeTexts = MakeBadgeTexts();

    private static string[] MakeBadgeTexts()
    {
        var texts = new string[16];
        for (var bits = 0; bits < texts.Length; bits++)
        {
            var badges = string.Empty;
            if ((bits & 1) != 0)
            {
                badges += "\uE735";
            }

            if ((bits & 2) != 0)
            {
                badges += "\uE70B";
            }

            if ((bits & 4) != 0)
            {
                badges += "\uE71B";
            }

            if ((bits & 8) != 0)
            {
                badges += "\uE72E";
            }

            texts[bits] = badges;
        }

        return texts;
    }

    private string Badges(NestedFolder folder) =>
        BadgeTexts[(IsPinned(folder.FullPath) ? 1 : 0)
            | (folder.HasNote ? 2 : 0)
            | (folder.IsReparsePoint ? 4 : 0)
            | (folder.LoadState == NestedLoadState.Failed ? 8 : 0)];

    private Color BadgeColour(NestedFolder folder) =>
        folder.LoadState == NestedLoadState.Failed ? DangerColour : IsPinned(folder.FullPath) ? StarColour : TextDimColour;

    private static string Glyph(NestedFolder folder) => folder.Kind switch
    {
        NestedFolderKind.Computer => "",
        NestedFolderKind.Drive => folder.FullPath.StartsWith(@"\\", StringComparison.Ordinal) ? "" : "",
        _ => folder.IsReparsePoint ? "" : ""
    };

    private static Color GlyphColour(NestedFolder folder) => folder.Kind switch
    {
        NestedFolderKind.Folder when folder.HasLabel => NestedRaster.Unpack(folder.StripeColour),
        NestedFolderKind.Folder => FolderColour,
        _ => TextDimColour
    };

    private Brush BrushFor(Color colour)
    {
        if (!_brushes.TryGetValue(colour, out var brush))
        {
            brush = Frozen(colour);
            _brushes[colour] = brush;
        }

        return brush;
    }

    private static readonly Brush FolderBrush = Frozen(FolderColour);

    private void DrawSelection(DrawingContext dc)
    {
        if (_tree is null)
        {
            return;
        }

        foreach (var path in _selected)
        {
            if (ResolveAsPlaced(path) is not { } target || TargetRect(target.Folder, target.FileIndex, place: false) is not { } rect || rect.Width < 3)
            {
                continue;
            }

            var radius = target.FileIndex >= 0 ? Math.Min(3, rect.Height * 0.2) : rect.Width >= 40 ? Math.Min(6, rect.Width * 0.03) : 1;
            rect.Inflate(1, 1);
            dc.DrawRoundedRectangle(null, SelectionPen, rect, radius, radius);
        }
    }

    private void DrawDropTarget(DrawingContext dc)
    {
        if (_dropTarget is null || ScreenRect(_dropTarget, place: false) is not { } rect)
        {
            return;
        }

        if (rect.Width < 6)
        {
            rect = new Rect(rect.X + rect.Width / 2 - 8, rect.Y + rect.Height / 2 - 8, 16, 16);
        }

        dc.DrawRoundedRectangle(DropFillBrush, DropPen, rect, 5, 5);
    }

    /// <summary>
    /// The pointer's outline and name tag live on their own layer: moving the
    /// mouse redraws two shapes there, not the whole canvas under them.
    /// </summary>
    private void RenderOverlay()
    {
        // A tag is one or two texts; it never waits on the frame's budget.
        var budget = _textBudget;
        _textBudget = int.MaxValue;
        using (var dc = _overlay.RenderOpen())
        {
            DrawHover(dc);
            DrawHoverTip(dc);
        }

        _textBudget = budget;
    }

    private void DrawHover(DrawingContext dc)
    {
        if (_hover is not { } hover || _press != PressKind.None || TargetRect(hover.Folder, hover.FileIndex, place: false) is not { } rect || rect.Width < 3)
        {
            return;
        }

        var radius = hover.IsFile ? Math.Min(3, rect.Height * 0.2) : rect.Width >= 40 ? Math.Min(6, rect.Width * 0.03) : 1;
        dc.DrawRoundedRectangle(null, HoverPen, rect, radius, radius);
    }

    /// <summary>
    /// A name next to the pointer for a folder too small to carry its own:
    /// with the whole disk on screen almost everything is that small, and
    /// this is how it is explored without zooming into every speck.
    /// </summary>
    private void DrawHoverTip(DrawingContext dc)
    {
        if (_hover is null && _press == PressKind.None && HotspotAt(_hoverPoint) is { Tip: { Length: > 0 } tip })
        {
            var tipText = Text(tip, 12, TextBrush, 360, bold: false);
            var tipBox = new Rect(
                Math.Clamp(_hoverPoint.X - tipText.Width / 2 - 8, 4, Math.Max(4, _viewWidth - tipText.Width - 20)),
                Math.Clamp(_hoverPoint.Y + 16, 4, Math.Max(4, _viewHeight - tipText.Height - 14)),
                tipText.Width + 16,
                tipText.Height + 8);
            dc.DrawRoundedRectangle(TipBrush, TipPen, tipBox, 5, 5);
            DrawTextAt(dc, tipText, new Point(tipBox.X + 8, tipBox.Y + 4));
            return;
        }

        if (_hover is not { } hover || _press != PressKind.None || hover.Folder.IsComputer)
        {
            return;
        }

        string name;
        string detail;
        if (hover.IsFile)
        {
            if (hover.Bounds.Height >= FileLabelPixels)
            {
                return;
            }

            name = hover.File.Name;
            detail = FormatSize(hover.File.Length);
        }
        else
        {
            if (_labelled.Contains(hover.Folder))
            {
                return;
            }

            name = hover.Folder.Name;
            detail = DetailText(hover.Folder);
        }

        var title = Text(name, 12, TextBrush, 360, bold: true);
        var mark = _markLookup?.Invoke(hover.Path) ?? FolderMark.None;
        var noteText = string.IsNullOrWhiteSpace(mark.Note) ? string.Empty : mark.Note.Trim();
        var second = noteText.Length > 0 ? noteText : detail;
        ScaledText? sub = second.Length > 0 ? Text(second, 11, noteText.Length > 0 ? TextBrush : TextDimBrush, 360, bold: false) : null;

        var width = Math.Max(title.Width, sub?.Width ?? 0) + 16;
        var height = title.Height + (sub?.Height ?? 0) + 10;
        var x = _hoverPoint.X + 16;
        var y = _hoverPoint.Y + 18;
        if (x + width > _viewWidth - 4) x = _hoverPoint.X - width - 8;
        if (y + height > _viewHeight - 4) y = _hoverPoint.Y - height - 8;
        var box = new Rect(Math.Max(4, x), Math.Max(4, y), width, height);
        dc.DrawRoundedRectangle(TipBrush, TipPen, box, 5, 5);
        DrawTextAt(dc, title, new Point(box.X + 8, box.Y + 5));
        if (sub is not null)
        {
            DrawTextAt(dc, sub.Value, new Point(box.X + 8, box.Y + 5 + title.Height));
        }
    }

    // ---- beacons -------------------------------------------------------------

    private bool IsPinned(string path) => _pinned.Count > 0 && _pinned.Contains(path);

    /// <summary>
    /// Reads the folders on the way to every beacon, one path at a time, so a
    /// marked folder deep in the tree has a place before anyone zooms to it.
    /// </summary>
    private async Task ResolveBeaconsAsync()
    {
        if (_beaconResolverRunning || _tree is null)
        {
            return;
        }

        _beaconResolverRunning = true;
        try
        {
            for (var pass = 0; pass < 4; pass++)
            {
                var pending = _beacons
                    .Select(beacon => beacon.Path)
                    .Where(path => !_unresolvable.Contains(path) && (ResolveAsPlaced(path) is not { } found || !NestedTree.IsOnCanvas(found.Folder)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (pending.Count == 0)
                {
                    break;
                }

                foreach (var path in pending)
                {
                    if (!_resolving.Add(path))
                    {
                        continue;
                    }

                    try
                    {
                        // A folder resolves to itself; a file to the folder it
                        // is in, once that folder's listing has it.
                        await _tree.RevealAsync(path);
                        if (ResolveAsPlaced(path) is null)
                        {
                            _unresolvable.Add(path);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
                    {
                        _unresolvable.Add(path);
                    }
                    finally
                    {
                        _resolving.Remove(path);
                    }
                }

                RequestFrame(Layers.Decor);
            }
        }
        finally
        {
            _beaconResolverRunning = false;
        }
    }

    private void DrawBeacons(DrawingContext dc)
    {
        if (_tree is null || _beacons.Count == 0)
        {
            return;
        }

        var pins = new List<Pin>();
        var offscreen = new List<Pin>();
        foreach (var beacon in _beacons)
        {
            if (ResolveAsPlaced(beacon.Path) is not { } target || TargetRect(target.Folder, target.FileIndex, place: false) is not { } rect)
            {
                continue;
            }

            // Anything big enough to carry its own name carries its own mark,
            // and a folder the whole view is inside is not "somewhere else".
            if (target.FileIndex < 0
                    ? _labelled.Contains(target.Folder) && rect.Width >= 90 || Covers(rect.X, rect.Y, rect.Width)
                    : rect.Height >= FileLabelPixels)
            {
                continue;
            }

            var centre = new Point(rect.X + rect.Width / 2, rect.Y + Math.Min(rect.Height / 2, 10));
            if (centre.X < -8 || centre.Y < -8 || centre.X > _viewWidth + 8 || centre.Y > _viewHeight + 8)
            {
                offscreen.Add(new Pin(beacon, target.Folder, target.FileIndex, centre, Priority(beacon.Kind)));
                continue;
            }

            pins.Add(new Pin(beacon, target.Folder, target.FileIndex, centre, Priority(beacon.Kind)));
        }

        DrawEdgeMarkers(dc, offscreen);
        if (pins.Count == 0)
        {
            return;
        }

        // Nearby pins merge: twenty marks inside one small folder are one
        // badge with a count, not twenty dots on top of each other.
        pins.Sort((left, right) => right.Priority.CompareTo(left.Priority));
        var clusters = new List<List<Pin>>();
        foreach (var pin in pins)
        {
            // Close enough that their circles would overlap: one badge.
            var home = clusters.FirstOrDefault(cluster => (cluster[0].Centre - pin.Centre).Length < 26);
            if (home is null)
            {
                clusters.Add([pin]);
            }
            else
            {
                home.Add(pin);
            }
        }

        var placedLabels = new List<Rect>();
        foreach (var cluster in clusters)
        {
            var lead = cluster[0];
            var brush = BrushFor(lead.Beacon.Colour);
            var centre = lead.Centre;
            if (cluster.Count == 1)
            {
                dc.DrawEllipse(null, BeaconHaloPen, centre, 8.5, 8.5);
                dc.DrawEllipse(brush, BeaconRimPen, centre, 6, 6);
                var glyph = BeaconGlyph(lead.Beacon.Kind);
                if (glyph.Length > 0)
                {
                    var icon = Text(glyph, 7.5, BeaconGlyphBrush(lead.Beacon.Colour), double.MaxValue, bold: false, icon: true);
                    DrawTextAt(dc, icon, new Point(centre.X - icon.Width / 2, centre.Y - icon.Height / 2));
                }
            }
            else
            {
                var count = Text(cluster.Count.ToString(CultureInfo.CurrentCulture), 10, BeaconGlyphBrush(lead.Beacon.Colour), double.MaxValue, bold: true);
                var radius = Math.Max(8.5, count.Width / 2 + 5);
                dc.DrawEllipse(null, BeaconHaloPen, centre, radius + 2.5, radius + 2.5);
                dc.DrawEllipse(brush, BeaconRimPen, centre, radius, radius);
                DrawTextAt(dc, count, new Point(centre.X - count.Width / 2, centre.Y - count.Height / 2));
            }

            var hit = new Rect(centre.X - 10, centre.Y - 10, 20, 20);
            var members = cluster.ToList();
            _hotspots.Add(new Hotspot(hit, () => OnBeaconClicked(members), null));

            // A label beside it, if it does not run into one already placed.
            var caption = cluster.Count == 1
                ? lead.Beacon.Label
                : $"{lead.Beacon.Label} +{cluster.Count - 1}";
            var text = Text(caption, 11, TextBrush, 220, bold: false);
            var labelRect = new Rect(centre.X + 12, centre.Y - text.Height / 2 - 2, text.Width + 10, text.Height + 4);
            if (labelRect.Right > _viewWidth - 2)
            {
                labelRect.X = centre.X - 12 - labelRect.Width;
            }

            if (placedLabels.Any(placed => placed.IntersectsWith(labelRect)) || clusters.Any(other => other != cluster && labelRect.Contains(other[0].Centre)))
            {
                continue;
            }

            placedLabels.Add(labelRect);
            dc.DrawRoundedRectangle(PillBrush, null, labelRect, 4, 4);
            DrawTextAt(dc, text, new Point(labelRect.X + 5, labelRect.Y + 2));
            _hotspots.Add(new Hotspot(labelRect, () => OnBeaconClicked(members), null));
        }
    }

    /// <summary>
    /// Marks that are off screen, as arrows on the edge of the view pointing
    /// the way to them.  Deep inside one folder, everything the user marked
    /// elsewhere would otherwise be out of sight and out of mind; this is the
    /// "it is over there" that makes zooming out to look for it unnecessary.
    /// </summary>
    private void DrawEdgeMarkers(DrawingContext dc, List<Pin> offscreen)
    {
        if (offscreen.Count == 0 || _viewWidth < 80 || _viewHeight < 80)
        {
            return;
        }

        const double inset = 16;
        var centre = new Point(_viewWidth / 2, _viewHeight / 2);
        var halfWidth = _viewWidth / 2 - inset;
        var halfHeight = _viewHeight / 2 - inset;

        // Where the ray from the middle of the view to each mark leaves the
        // inset frame; marks in the same direction share one arrow.
        offscreen.Sort((left, right) => right.Priority.CompareTo(left.Priority));
        var groups = new List<(Point At, Vector Direction, List<Pin> Members)>();
        foreach (var pin in offscreen)
        {
            var direction = pin.Centre - centre;
            if (double.IsNaN(direction.X) || double.IsNaN(direction.Y) || direction.Length < 1e-9)
            {
                continue;
            }

            var scale = Math.Min(
                Math.Abs(direction.X) < 1e-12 ? double.MaxValue : halfWidth / Math.Abs(direction.X),
                Math.Abs(direction.Y) < 1e-12 ? double.MaxValue : halfHeight / Math.Abs(direction.Y));
            var at = new Point(centre.X + direction.X * scale, centre.Y + direction.Y * scale);
            direction.Normalize();

            var home = groups.FindIndex(group => (group.At - at).Length < 22);
            if (home < 0)
            {
                groups.Add((at, direction, [pin]));
            }
            else
            {
                groups[home].Members.Add(pin);
            }
        }

        foreach (var (at, direction, members) in groups)
        {
            var lead = members[0];
            var brush = BrushFor(lead.Beacon.Colour);

            // A small arrowhead pointing out of the view, and the mark's dot behind it.
            var tip = at + direction * 9;
            var side = new Vector(-direction.Y, direction.X) * 5;
            var arrow = new StreamGeometry();
            using (var context = arrow.Open())
            {
                context.BeginFigure(tip, isFilled: true, isClosed: true);
                context.LineTo(at + direction * 2 + side, isStroked: false, isSmoothJoin: false);
                context.LineTo(at + direction * 2 - side, isStroked: false, isSmoothJoin: false);
            }

            arrow.Freeze();
            dc.DrawGeometry(brush, null, arrow);
            dc.DrawEllipse(brush, BeaconRimPen, at, 5.5, 5.5);
            if (members.Count > 1)
            {
                var count = Text(members.Count.ToString(CultureInfo.CurrentCulture), 8.5, BeaconGlyphBrush(lead.Beacon.Colour), double.MaxValue, bold: true);
                DrawTextAt(dc, count, new Point(at.X - count.Width / 2, at.Y - count.Height / 2));
            }

            _hotspots.Add(new Hotspot(new Rect(at.X - 11, at.Y - 11, 22, 22), () => OnBeaconClicked(members), null, EdgeTip(members)));
        }
    }

    private static string EdgeTip(List<Pin> members) =>
        members.Count == 1 ? members[0].Beacon.Label : $"{members[0].Beacon.Label} and {members.Count - 1} more";

    private void OnBeaconClicked(IReadOnlyList<Pin> pins)
    {
        // Found again by path, for the current order: a mark is drawn where
        // the picture had its folder, and the folder may have been placed
        // again since - the tile a file's mark was drawn on can hold another
        // file by the time it is clicked.
        var targets = new List<(NestedFolder Folder, int FileIndex)>(pins.Count);
        foreach (var pin in pins)
        {
            if (Resolve(pin.Beacon.Path) is { } target)
            {
                targets.Add(target);
            }
        }

        if (targets.Count == 0)
        {
            return;
        }

        if (targets.Count == 1)
        {
            var (folder, fileIndex) = targets[0];
            var path = fileIndex >= 0 && fileIndex < folder.Files.Count ? folder.PathOf(folder.Files[fileIndex]) : folder.FullPath;
            MarkSelected(path);
            SelectRequested?.Invoke(path, false);
            if (fileIndex >= 0)
            {
                FlyToReadable(folder, fileIndex);
            }
            else
            {
                FlyTo(folder, 0.6);
            }

            return;
        }

        // Several marks in one spot: go to the smallest folder that holds all of them.
        var common = targets[0].Folder;
        foreach (var (folder, _) in targets.Skip(1))
        {
            while (!common.Contains(folder) && common.Parent is not null)
            {
                common = common.Parent;
            }
        }

        FlyTo(common, 0.92);
    }

    private static int Priority(NestedBeaconKind kind) =>
        ((kind & NestedBeaconKind.Active) != 0 ? 16 : 0)
        + ((kind & NestedBeaconKind.Pinned) != 0 ? 8 : 0)
        + ((kind & NestedBeaconKind.Note) != 0 ? 4 : 0)
        + ((kind & NestedBeaconKind.Colour) != 0 ? 2 : 0)
        + ((kind & NestedBeaconKind.Search) != 0 ? 1 : 0);

    private static string BeaconGlyph(NestedBeaconKind kind) =>
        (kind & NestedBeaconKind.Pinned) != 0 ? ""
        : (kind & NestedBeaconKind.Note) != 0 ? ""
        : (kind & NestedBeaconKind.Search) != 0 ? ""
        : string.Empty;

    private static Brush BeaconGlyphBrush(Color colour)
    {
        var luminance = 0.2126 * colour.R + 0.7152 * colour.G + 0.0722 * colour.B;
        return luminance > 140 ? Brushes.Black : Brushes.White;
    }

    // ---- the trail -------------------------------------------------------------

    /// <summary>
    /// The folders the view is inside whose names have scrolled off the top:
    /// deep in, every visible cell belongs to something whose title is far
    /// above the screen, and this says what.
    /// </summary>
    private void DrawTrail(DrawingContext dc)
    {
        if (_tree is null)
        {
            return;
        }

        var trail = _trail;
        trail.Clear();
        var folder = _tree.Root;
        var (x, y, w) = _chain[folder];
        var probe = new Point(_viewWidth / 2, Math.Min(_viewHeight / 2, 80));
        while (true)
        {
            if (y + w * NestedLayout.HeaderHeight < 0 && !folder.IsComputer)
            {
                trail.Add(folder);
            }

            // As placed, not placed here: the trail names the folders the
            // picture shows under the probe, and a folder the frame drew in
            // its previous order is still in its previous place there.
            var grid = folder.Grid;
            if (grid.IsEmpty)
            {
                break;
            }

            var index = grid.IndexAt((probe.X - x) / w, (probe.Y - y) / w);
            if (index < 0 || index >= folder.Children.Count)
            {
                break;
            }

            var child = folder.Children[index];
            if (_chain.TryGetValue(child, out var exact))
            {
                (x, y, w) = exact;
            }
            else
            {
                (x, y, w) = (x + child.OffsetX * w, y + child.OffsetY * w, w * child.Scale);
            }

            if (y + w * NestedLayout.HeaderHeight >= 0)
            {
                break;
            }

            folder = child;
        }

        if (trail.Count == 0)
        {
            return;
        }

        // Long trails keep the ends: where it starts and where the view is.
        var shown = _trailShown;
        shown.Clear();
        if (trail.Count <= 5)
        {
            shown.AddRange(trail);
        }
        else
        {
            shown.Add(trail[0]);
            shown.Add(null);
            for (var index = trail.Count - 3; index < trail.Count; index++)
            {
                shown.Add(trail[index]);
            }
        }

        if (!_labelsOnGpu)
        {
            DrawTrailPieces(dc, shown, _hotspots);
            return;
        }

        // With the names on the GPU the trail is the one text a moving frame
        // still has WPF lay out and draw.  It only changes when the folders
        // in it do, so it is recorded once and replayed: no text is formatted
        // again for a frame that shows the same trail.
        if (_trailDrawing is null || !IsSameTrail(shown))
        {
            _trailHotspots.Clear();
            var drawing = new DrawingGroup();

            // At most seven fixed-size texts, laid out whatever the frame's
            // budget of new layouts: the beacons before it may have spent it
            // all, and a trail recorded without its names would be replayed
            // empty, and unclickable, until the folders in it change.
            var budget = _textBudget;
            _textBudget = int.MaxValue;
            try
            {
                using var context = drawing.Open();
                DrawTrailPieces(context, shown, _trailHotspots);
            }
            finally
            {
                _textBudget = budget;
            }

            drawing.Freeze();
            _trailDrawing = drawing;
            _trailScale = _scaleY;
            _trailKey.Clear();
            foreach (var step in shown)
            {
                _trailKey.Add((step, step?.Name));
            }
        }

        dc.DrawDrawing(_trailDrawing);
        _hotspots.AddRange(_trailHotspots);
    }

    private readonly List<NestedFolder> _trail = [];
    private readonly List<NestedFolder?> _trailShown = [];
    private readonly List<(NestedFolder? Folder, string? Name)> _trailKey = [];
    private readonly List<Hotspot> _trailHotspots = [];
    private DrawingGroup? _trailDrawing;
    private double _trailScale;

    /// <summary>Whether the recorded trail shows these folders, by these names, at this scale.</summary>
    private bool IsSameTrail(List<NestedFolder?> shown)
    {
        if (_trailKey.Count != shown.Count || _trailScale != _scaleY)
        {
            return false;
        }

        for (var index = 0; index < shown.Count; index++)
        {
            var step = shown[index];
            if (!ReferenceEquals(_trailKey[index].Folder, step) || !ReferenceEquals(_trailKey[index].Name, step?.Name))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The trail's pill, names and separators, and a hotspot on each name to fly to its folder.</summary>
    private void DrawTrailPieces(DrawingContext dc, List<NestedFolder?> shown, List<Hotspot> hotspots)
    {
        const double pad = 8;
        var cursor = 14 + pad;
        var top = 12.0;
        var pieces = new List<(ScaledText Text, NestedFolder? Folder)>();
        foreach (var step in shown)
        {
            var label = step is null ? "…" : step.Name;
            pieces.Add((Text(label, 12, step is null ? TextDimBrush : TextBrush, 240, bold: step is not null), step));
        }

        var separator = Text("  ›  ", 12, TextDimBrush, double.MaxValue, bold: false);
        var total = pieces.Sum(piece => piece.Text.Width) + separator.Width * (pieces.Count - 1) + 2 * pad;
        var height = pieces.Max(piece => piece.Text.Height) + 8;
        dc.DrawRoundedRectangle(PillBrush, null, new Rect(14, top, total, height), 6, 6);
        for (var index = 0; index < pieces.Count; index++)
        {
            var (text, step) = pieces[index];
            var at = new Point(cursor, top + 4);
            DrawTextAt(dc, text, at);
            if (step is not null)
            {
                var target = step;
                hotspots.Add(new Hotspot(new Rect(at, new Size(text.Width, text.Height)), () => FlyTo(target), null));
            }

            cursor += text.Width;
            if (index < pieces.Count - 1)
            {
                DrawTextAt(dc, separator, new Point(cursor, top + 4));
                cursor += separator.Width;
            }
        }
    }

    // ---- input -----------------------------------------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var point = e.GetPosition(this);
        if (e.ClickCount == 2 && !IsSpacePanArmed)
        {
            e.Handled = true;
            if (HotspotAt(point) is not null)
            {
                return;
            }

            if (PointerHit(point) is { } hit && !hit.Folder.IsComputer)
            {
                OpenRequested?.Invoke(hit);
                if (!hit.IsFile)
                {
                    FlyTo(hit.Folder);
                }
            }

            return;
        }

        _press = PressKind.Left;
        _pressPoint = point;
        _pressMoved = false;
        _pressHotspot = IsSpacePanArmed ? null : HotspotAt(point);
        _pressHit = PointerHit(point);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        Focus();
        _press = PressKind.Middle;
        _pressPoint = e.GetPosition(this);
        _pressMoved = true;
        _panLast = _pressPoint;
        StopFlight();
        Cursor = Cursors.SizeAll;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (_press == PressKind.None)
        {
            UpdateHover(point);
            return;
        }

        if (!_pressMoved)
        {
            if (Math.Abs(point.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(point.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _pressMoved = true;
            if (_press == PressKind.Left && !IsSpacePanArmed && _pressHit is { } hit && CanGrab(hit, _pressPoint))
            {
                // Grabbing a folder by its title, or a file by its tile, is
                // picking it up.
                _press = PressKind.None;
                ReleaseMouseCapture();
                DragRequested?.Invoke(hit.Path);
                return;
            }

            StopFlight();
            _panLast = _pressPoint;
            Cursor = Cursors.SizeAll;
            _hover = null;
        }

        Pan(point - _panLast);
        _panLast = point;
        e.Handled = true;
    }

    private bool CanGrab(NestedHit hit, Point point)
    {
        if (hit.IsFile)
        {
            return hit.Bounds.Height >= FileLabelPixels;
        }

        if (hit.Folder.Kind != NestedFolderKind.Folder)
        {
            return false;
        }

        var header = hit.Bounds.Width * NestedLayout.HeaderHeight;
        if (header >= HeaderGrabPixels && point.Y - hit.Bounds.Y <= header)
        {
            return true;
        }

        // A small cell's name sits on it as a pill; that pill is its handle.
        foreach (var spot in _labelHotspots)
        {
            if (ReferenceEquals(spot.Folder, hit.Folder) && spot.Bounds.Contains(point))
            {
                return true;
            }
        }

        return false;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_press != PressKind.Left)
        {
            return;
        }

        var wasMoved = _pressMoved;
        EndPress();
        e.Handled = true;
        if (wasMoved)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (_pressHotspot is { Click: { } click } && _pressHotspot.Bounds.Contains(point))
        {
            click();
            return;
        }

        if (_pressHit is { } hit && !hit.Folder.IsComputer)
        {
            var additive = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            var path = hit.Path;
            if (!additive)
            {
                MarkSelected(path);
            }

            SelectRequested?.Invoke(path, additive);
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Middle && _press == PressKind.Middle)
        {
            EndPress();
            e.Handled = true;
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        e.Handled = true;
        var point = e.GetPosition(this);
        var hit = PointerHit(point);
        if (hit is null || hit.Value.Folder.IsComputer)
        {
            ContextMenuRequested?.Invoke(null, true, point);
            return;
        }

        var onBackground = !hit.Value.IsFile
            && !hit.Value.IsOnHeader
            && hit.Value.Bounds.Width * NestedLayout.HeaderHeight >= HeaderGrabPixels;
        ContextMenuRequested?.Invoke(hit.Value, onBackground, point);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_press != PressKind.None)
        {
            _press = PressKind.None;
            ClearValue(CursorProperty);
            RequestFrame(Layers.Overlay);
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        _hoverPoint = new Point(double.NaN, double.NaN);
        RenderOverlay();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;
        var point = e.GetPosition(this);
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            ZoomAt(point, Math.Pow(1.2, e.Delta / 120.0));
        }
        else if ((modifiers & ModifierKeys.Shift) != 0)
        {
            Pan(new Vector(e.Delta * 0.8, 0));
        }
        else
        {
            Pan(new Vector(0, e.Delta * 0.8));
        }

        UpdateHover(point);
    }

    private void EndPress()
    {
        _press = PressKind.None;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        ClearValue(CursorProperty);
    }

    private void UpdateHover(Point point)
    {
        var wasOnTip = HotspotAt(_hoverPoint) is { Tip: not null };
        _hoverPoint = point;
        var spot = HotspotAt(point);
        if (spot is { Tip: not null })
        {
            _hover = null;
            RenderOverlay();
            return;
        }

        if (wasOnTip)
        {
            // Off an arrow: its tag goes, and the ordinary hover takes over.
            _hover = null;
            RenderOverlay();
        }

        var hit = spot is null ? PointerHit(point) : null;
        var same = hit is { } now && _hover is { } before
            && ReferenceEquals(now.Folder, before.Folder)
            && now.FileIndex == before.FileIndex;
        var showsTip = hit is { } shown && (shown.IsFile ? shown.Bounds.Height < FileLabelPixels : !_labelled.Contains(shown.Folder));
        if (!same || showsTip || hit is null != _hover is null)
        {
            _hover = hit;
            RenderOverlay();
        }
    }

    /// <summary>
    /// What the pointer is on, as the user sees it: a small folder's name is
    /// drawn over the top of its first sub-folders, and a press on that name
    /// means the folder whose name it is, not whatever lies under the letters.
    /// </summary>
    private NestedHit? PointerHit(Point point)
    {
        for (var index = _labelHotspots.Count - 1; index >= 0; index--)
        {
            var spot = _labelHotspots[index];
            if (spot.Bounds.Contains(point) && ScreenRect(spot.Folder) is { } cell)
            {
                return new NestedHit(spot.Folder, cell, IsOnHeader: true);
            }
        }

        return HitTest(point);
    }

    private Hotspot? HotspotAt(Point point)
    {
        for (var index = _hotspots.Count - 1; index >= 0; index--)
        {
            if (_hotspots[index].Click is not null && _hotspots[index].Bounds.Contains(point))
            {
                return _hotspots[index];
            }
        }

        return null;
    }

    private void AddGrabHotspot(NestedFolder folder, Rect bounds) =>
        _labelHotspots.Add(new LabelGrab(bounds, folder));

    /// <summary>Shows a click as selected at once, before the rest of the window catches up.</summary>
    private void MarkSelected(string path)
    {
        _selected.Clear();
        _selected.Add(path);
        _activePath = path;
        RequestFrame(Layers.Decor);
    }

    /// <summary>
    /// The next folder or file in the direction of an arrow: sub-folders move
    /// over their parent's grid, files over their folder's.
    /// </summary>
    private (NestedFolder Folder, int FileIndex)? Neighbour((NestedFolder Folder, int FileIndex)? active, Key key)
    {
        // Next along is next in the order on screen: the folders whose grids
        // are walked are placed for it first.
        if (active is { } placed)
        {
            Ensure(placed.Folder);
            if (placed.Folder.Parent is { } holder)
            {
                Ensure(holder);
            }
        }

        if (active is not { } current || current.FileIndex < 0 && (current.Folder.Parent is null || current.Folder.Index < 0))
        {
            // Nothing selected yet: start at the first folder in the one in
            // view, or its first file if it holds only files.
            if (_anchor is not null)
            {
                Ensure(_anchor);
            }

            return _anchor switch
            {
                { Children.Count: > 0 } anchor => (anchor.Children[0], -1),
                { Files.Count: > 0 } anchor => (anchor, 0),
                _ => null
            };
        }

        if (current.FileIndex >= 0)
        {
            var folder = current.Folder;
            var files = folder.FileGrid;
            var nextFile = Step(current.FileIndex, files.Columns, key);
            if (nextFile >= 0 && nextFile < folder.Files.Count)
            {
                return (folder, nextFile);
            }

            // Up from the first row of files is the last row of sub-folders
            // above them, the one nearest along the row.
            if (key == Key.Up && folder.Children.Count > 0 && current.FileIndex < files.Columns)
            {
                var (fx, _) = files.Origin(current.FileIndex);
                return (Nearest(folder.Children, folder.Grid, fx + files.TileWidth / 2, lastRow: true), -1);
            }

            return null;
        }

        var parent = current.Folder.Parent!;
        var grid = parent.Grid;
        var next = Step(current.Folder.Index, grid.Columns, key);
        if (next >= 0 && next < parent.Children.Count)
        {
            return (parent.Children[next], -1);
        }

        // Down past the last row of sub-folders is the first row of the files under them.
        if (key == Key.Down && parent.Files.Count > 0 && current.Folder.Index / grid.Columns == grid.Rows - 1)
        {
            var centre = current.Folder.OffsetX + current.Folder.Scale / 2;
            var files = parent.FileGrid;
            var column = Math.Clamp((int)Math.Floor((centre - files.Left) / files.StepX), 0, Math.Max(0, files.Columns - 1));
            return (parent, Math.Min(column, parent.Files.Count - 1));
        }

        return null;
    }

    /// <summary>The child in a grid's first or last row nearest to <paramref name="x"/>.</summary>
    private static NestedFolder Nearest(IReadOnlyList<NestedFolder> children, NestedGrid grid, double x, bool lastRow)
    {
        var row = lastRow ? grid.Rows - 1 : 0;
        var first = row * grid.Columns;
        var last = Math.Min(children.Count - 1, first + grid.Columns - 1);
        var column = Math.Clamp((int)Math.Floor((x - grid.Left) / grid.StepX), 0, last - first);
        return children[first + column];
    }

    private static int Step(int index, int columns, Key key) => key switch
    {
        Key.Left => index - 1,
        Key.Right => index + 1,
        Key.Up => index - columns,
        _ => index + columns
    };

    /// <summary>
    /// Brings what an arrow key moved to into view.  Readable but off screen,
    /// the view only slides - the zoom the user chose stays; too small to read,
    /// it flies to where it can be read.
    /// </summary>
    private void EnsureVisible(NestedFolder folder, int fileIndex = -1)
    {
        var rect = TargetRect(folder, fileIndex);
        var readable = rect is { } r && (fileIndex >= 0 ? r.Height >= FileLabelPixels : r.Width >= 48);
        if (!readable)
        {
            FlyToReadable(folder, fileIndex);
            return;
        }

        const double margin = 24;
        var target = rect!.Value;
        var dx = Shift(target.Left, target.Right, _viewWidth, margin);
        var dy = Shift(target.Top, target.Bottom, _viewHeight, margin);
        if (dx == 0 && dy == 0)
        {
            return;
        }

        if (ScreenRect(folder) is { } cell)
        {
            FlyToRect(folder, (cell.X + dx, cell.Y + dy, cell.Width));
        }
    }

    /// <summary>The smallest slide along one axis that brings [low, high] inside [margin, size - margin].</summary>
    private static double Shift(double low, double high, double size, double margin)
    {
        if (high - low > size - 2 * margin)
        {
            // Bigger than the view: line its start up with the view's.
            return margin - low;
        }

        if (low < margin)
        {
            return margin - low;
        }

        return high > size - margin ? size - margin - high : 0;
    }

    private Rect? ScreenRect(NestedFolder folder, bool place = true)
    {
        var result = RectOf(folder, place);
        return result is { } r ? new Rect(r.X, r.Y, r.W, r.W * NestedLayout.CellHeight) : null;
    }

    // ---- text ------------------------------------------------------------------

    /// <summary>
    /// Text laid out once and drawn at any size.  A name whose size follows the
    /// zoom is laid out at the nearest of a fixed ladder of sizes - eight to an
    /// octave, a mip chain for type - and drawn through a scale to the exact
    /// size wanted.  Its available width is kept in the same units, so during
    /// a zoom both grow together and the layout found in the cache is the one
    /// needed: the text is no longer laid out anew every frame.
    /// </summary>
    private ScaledText Text(string text, double size, Brush brush, double maxWidth, bool bold, bool icon = false, bool scaled = false)
    {
        size = Math.Clamp(size, 1, 400);
        var level = scaled
            ? Math.Pow(2, Math.Round(Math.Log2(size) * LevelsPerOctave) / LevelsPerOctave)
            : Math.Round(size * 4) / 4;

        // The whole name first.  Most names fit, and a name that fits does
        // not depend on the room it has - so it is the same layout at every
        // step of a zoom, and only the few that are cut short are laid out
        // again as their room changes.
        var natural = Layout(text, level, size, double.PositiveInfinity, brush, bold, icon, scaled);
        if (natural.Text is null || !(maxWidth < 10_000) || natural.Width <= maxWidth)
        {
            return natural;
        }

        var trimmed = Layout(text, level, size, maxWidth, brush, bold, icon, scaled);
        return trimmed.Text is null ? default : trimmed;
    }

    /// <summary>
    /// One layout, from the cache or made now.  While the camera moves only a
    /// few are made per frame; past that a scaled text takes the nearest level
    /// already made, the way a game streams a texture - the right one arrives
    /// a frame or two later - and one never made at any size waits its turn.
    /// </summary>
    private ScaledText Layout(string text, double level, double size, double maxWidth, Brush brush, bool bold, bool icon, bool scaled)
    {
        var scale = scaled ? size / level : 1;
        var key = KeyFor(text, level, scale, maxWidth, brush, bold, icon, scaled);
        if (TryCached(key, out var cached))
        {
            return new ScaledText(cached, scale);
        }

        if (_textBudget <= 0)
        {
            if (scaled)
            {
                for (var step = 1; step <= 3; step++)
                {
                    foreach (var direction in (ReadOnlySpan<int>)[-1, 1])
                    {
                        var near = level * Math.Pow(2, direction * step / (double)LevelsPerOctave);
                        var nearScale = size / near;
                        if (TryCached(KeyFor(text, near, nearScale, maxWidth, brush, bold, icon, scaled: true), out var neighbour))
                        {
                            return new ScaledText(neighbour, nearScale);
                        }
                    }
                }
            }

            _textDeferred = true;
            return default;
        }

        _textBudget--;
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            icon ? IconFace : bold ? TextFaceBold : TextFace,
            level,
            brush,
            _scaleY)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };

        if (key.Width >= 0)
        {
            formatted.MaxTextWidth = Math.Max(1, key.Width * (scaled ? 8 : 6));
        }

        NewTextLayouts++;
        _textCache[key] = formatted;
        return new ScaledText(formatted, scale);
    }

    private static TextKey KeyFor(string text, double level, double scale, double maxWidth, Brush brush, bool bold, bool icon, bool scaled)
    {
        var widthKey = double.IsInfinity(maxWidth) || maxWidth >= 10_000
            ? -1
            : Math.Max(0, (int)Math.Floor(maxWidth / scale / (scaled ? 8 : 6)));
        return new TextKey(text, level, widthKey, brush, bold, icon);
    }

    private bool TryCached(TextKey key, out FormattedText formatted)
    {
        if (_textCache.TryGetValue(key, out formatted!))
        {
            return true;
        }

        if (_oldTextCache.Remove(key, out formatted!))
        {
            _textCache[key] = formatted;
            return true;
        }

        return false;
    }

    private static void DrawTextAt(DrawingContext dc, ScaledText text, Point origin)
    {
        if (text.Text is null)
        {
            return;
        }

        if (Math.Abs(text.Scale - 1) < 1e-9)
        {
            dc.DrawText(text.Text, origin);
            return;
        }

        dc.PushTransform(new MatrixTransform(text.Scale, 0, 0, text.Scale, origin.X * (1 - text.Scale), origin.Y * (1 - text.Scale)));
        dc.DrawText(text.Text, origin);
        dc.Pop();
    }

    /// <summary>
    /// The names drawn by WPF, as the canvas has always drawn them: laid out
    /// by <see cref="Text"/> - its ladder of sizes, its budget of new layouts
    /// while the camera moves, its two generations of layouts - and recorded
    /// on the label layer's drawing context, with the icons
    /// <see cref="IconLookup"/> has.  Each call is the drawing call the canvas
    /// made itself before targets existed, with the same brush, so the layer
    /// records the same drawing and shows the same pixels.
    /// </summary>
    private sealed class WpfLabelTarget(NestedCanvas canvas) : LabelTarget
    {
        private DrawingContext? _dc;

        /// <summary>Starts a recording: everything drawn until <see cref="End"/> goes to <paramref name="dc"/>.</summary>
        public void Begin(DrawingContext dc) => _dc = dc;

        public void End() => _dc = null;

        public override LabelText Text(string text, double size, Color ink, double maxWidth, LabelFace face, bool scaled)
        {
            var laidOut = canvas.Text(text, size, BrushOf(ink), maxWidth, bold: face == LabelFace.SemiBold, icon: face == LabelFace.Icons, scaled);
            return laidOut.Text is null ? default : new LabelText(laidOut.Text, laidOut.Width, laidOut.Height, laidOut.Scale);
        }

        public override void DrawText(in LabelText text, Point origin) =>
            DrawTextAt(_dc!, new ScaledText(text.Handle as FormattedText, text.Scale), origin);

        public override void FillRect(Rect bounds, Color colour) =>
            _dc!.DrawRectangle(BrushOf(colour), null, bounds);

        public override void FillRounded(Rect bounds, double radius, Color colour) =>
            _dc!.DrawRoundedRectangle(BrushOf(colour), null, bounds, radius, radius);

        public override bool DrawIcon(string path, string extension, Rect bounds)
        {
            if (canvas.IconLookup?.Invoke(path) is not { } icon)
            {
                return false;
            }

            _dc!.DrawImage(icon, bounds);
            return true;
        }

        /// <summary>
        /// The brush a colour was always drawn with: the canvas's own for its
        /// own colours, one shared frozen brush for any other.  The same brush
        /// is also the same key in the layouts kept, so a name is found laid
        /// out exactly when it was before.
        /// </summary>
        private Brush BrushOf(Color colour) =>
            colour == TextColour ? TextBrush
            : colour == TextDimColour ? TextDimBrush
            : colour == DangerColour ? DangerBrush
            : colour == StarColour ? StarBrush
            : colour == FolderColour ? FolderBrush
            : colour == PillColour ? PillBrush
            : canvas.BrushFor(colour);
    }

    /// <summary>
    /// Strings made from numbers - a size, a pair of counts - kept so that
    /// drawing the same one every frame does not make it every frame.  Two
    /// generations, like the text layouts: what is still in use is carried
    /// over, the rest goes, so it stays the size of what is on screen.  The
    /// numbers are written in the culture of the moment, and a change of
    /// culture drops everything kept.
    /// </summary>
    private sealed class NumberTexts(int capacity)
    {
        private Dictionary<long, string> _current = [];
        private Dictionary<long, string> _old = [];
        private CultureInfo? _culture;

        public bool TryGet(long key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? text)
        {
            if (!ReferenceEquals(_culture, CultureInfo.CurrentCulture))
            {
                _culture = CultureInfo.CurrentCulture;
                _current.Clear();
                _old.Clear();
                text = null;
                return false;
            }

            if (_current.TryGetValue(key, out text))
            {
                return true;
            }

            if (_old.Remove(key, out text))
            {
                _current[key] = text;
                return true;
            }

            return false;
        }

        public string Add(long key, string text)
        {
            if (_current.Count >= capacity)
            {
                (_old, _current) = (_current, _old);
                _current.Clear();
            }

            _current[key] = text;
            return text;
        }
    }

    /// <summary>
    /// Pairs of strings told apart by which strings they are rather than by
    /// what they say: a file's facts are kept by its folder's path and its
    /// name, both strings that live as long as the folder's listing, and
    /// comparing those is two pointers where comparing text is two scans.
    /// </summary>
    private sealed class ReferenceKeyComparer : IEqualityComparer<(string Folder, string Name)>
    {
        public static readonly ReferenceKeyComparer Instance = new();

        public bool Equals((string Folder, string Name) x, (string Folder, string Name) y) =>
            ReferenceEquals(x.Folder, y.Folder) && ReferenceEquals(x.Name, y.Name);

        public int GetHashCode((string Folder, string Name) key) =>
            HashCode.Combine(
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Folder),
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Name));
    }

    /// <summary>
    /// Only #RRGGBB or #AARRGGBB.  The converter also takes names and 'sc#'
    /// forms, and some of those throw exceptions it does not document - from
    /// inside a frame, where an exception ends the program.  A mark only ever
    /// holds a hex colour from the palette, so anything else is ignored.
    /// </summary>
    internal static bool IsHexColour(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex[0] != '#' || hex.Length is not (7 or 9))
        {
            return false;
        }

        for (var index = 1; index < hex.Length; index++)
        {
            if (!char.IsAsciiHexDigit(hex[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseColour(string hex, out Color colour)
    {
        colour = default;
        if (!IsHexColour(hex))
        {
            return false;
        }

        try
        {
            colour = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

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

    [Flags]
    private enum Layers
    {
        None = 0,
        Scene = 1,
        Labels = 2,
        Decor = 4,
        Overlay = 8,
        All = Scene | Labels | Decor | Overlay
    }

    /// <summary>A laid-out text and the scale it is drawn at.</summary>
    private readonly record struct ScaledText(FormattedText? Text, double Scale)
    {
        public double Width => Text is null ? 0 : Text.Width * Scale;

        public double Height => Text is null ? 0 : Text.Height * Scale;
    }

    private enum PressKind
    {
        None,
        Left,
        Middle
    }

    private enum LabelMode
    {
        None,
        Header,
        Pill
    }

    private readonly record struct LabelJob(NestedFolder Folder, double X, double Y, double W, LabelMode Mode);

    private readonly record struct Pin(NestedBeacon Beacon, NestedFolder Folder, int FileIndex, Point Centre, int Priority);

    private readonly record struct FileLabelJob(NestedFolder Folder, int Index, double X, double Y, double W, double H);

    private readonly record struct TextKey(string Text, double Size, int Width, Brush Brush, bool Bold, bool Icon);

    /// <summary>A clickable spot drawn this frame (a beacon, a trail step) or a handle to grab a folder by.</summary>
    private sealed record Hotspot(Rect Bounds, Action? Click, NestedFolder? Grab, string? Tip = null);

    /// <summary>
    /// A folder's name drawn this frame, by which the folder can be grabbed:
    /// a value in a list rebuilt with every label layer, so drawing the names
    /// hands the garbage collector nothing.
    /// </summary>
    private readonly record struct LabelGrab(Rect Bounds, NestedFolder Folder);
}
