using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltraExplorer.Models;
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
public readonly record struct NestedHit(NestedFolder Folder, Rect Bounds, bool IsOnHeader, int FileIndex = -1)
{
    public bool IsFile => FileIndex >= 0 && FileIndex < Folder.Files.Count;

    public NestedFile File => Folder.Files[FileIndex];

    /// <summary>The path of whatever was hit.</summary>
    public string Path => IsFile ? Folder.PathOf(Folder.Files[FileIndex]) : Folder.FullPath;
}

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
/// <para><b>Drawing.</b> The cells themselves are filled into a pixel buffer
/// by hand (<see cref="NestedRaster"/>): a big folder is tens of thousands of
/// rectangles, which WPF would charge for one call at a time.  Text, the
/// selection and the beacons are drawn by WPF on top, where anti-aliasing and
/// type matter.  Nothing is kept between frames except the camera: a frame is
/// a walk from the root down through whatever is on screen and big enough to
/// see, so its cost is what is visible, not what has been read.</para>
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

    private static readonly Typeface TextFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface TextFaceBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface IconFace = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly uint CanvasColour = 0xFF111315;
    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF2));
    private static readonly Brush TextDimBrush = Frozen(Color.FromRgb(0x9A, 0x9A, 0x9A));
    private static readonly Brush DangerBrush = Frozen(Color.FromRgb(0xEF, 0x5A, 0x68));
    private static readonly Brush AccentBrush = Frozen(Color.FromRgb(0x60, 0xCD, 0xFF));
    private static readonly Brush PillBrush = Frozen(Color.FromArgb(0xD8, 0x14, 0x16, 0x18));
    private static readonly Brush TipBrush = Frozen(Color.FromArgb(0xF0, 0x20, 0x22, 0x25));
    private static readonly Brush DropFillBrush = Frozen(Color.FromArgb(0x55, 0x24, 0x3E, 0x4A));
    private static readonly Brush StarBrush = Frozen(Color.FromRgb(0xFF, 0xD6, 0x6B));
    private static readonly Pen SelectionPen = FrozenPen(Color.FromRgb(0x60, 0xCD, 0xFF), 2);
    private static readonly Pen ActivePen = FrozenPen(Color.FromArgb(0xB0, 0x60, 0xCD, 0xFF), 1.5);
    private static readonly Pen HoverPen = FrozenPen(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Pen DropPen = FrozenPen(Color.FromRgb(0x8D, 0xDE, 0xFF), 2);
    private static readonly Pen TipPen = FrozenPen(Color.FromRgb(0x3A, 0x3A, 0x3A), 1);
    private static readonly Pen BeaconRimPen = FrozenPen(Color.FromArgb(0xE0, 0x10, 0x10, 0x10), 1.5);
    private static readonly Pen BeaconHaloPen = FrozenPen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 1);

    private readonly NestedRaster _raster = new();
    private readonly Dictionary<NestedFolder, (double X, double Y, double W)> _chain = [];
    private readonly List<LabelJob> _labels = [];
    private readonly List<FileLabelJob> _fileLabels = [];
    private readonly Dictionary<string, (uint Body, uint Stripe, uint Speck)> _filePalette = new(StringComparer.Ordinal);
    private readonly HashSet<NestedFolder> _labelled = [];
    private readonly List<Hotspot> _hotspots = [];
    private readonly Dictionary<TextKey, FormattedText> _textCache = [];
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _resolving = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unresolvable = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pinned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Color, Brush> _brushes = [];
    private readonly DrawingVisual _overlay = new();
    private IReadOnlyList<NestedBeacon> _beacons = [];

    private NestedTree? _tree;
    private WriteableBitmap? _bitmap;
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

    private Flight? _flight;
    private bool _isRenderingHooked;

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
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        AddVisualChild(_overlay);
        Unloaded += (_, _) => StopRenderingHook();
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) =>
        index == 0 ? _overlay : throw new ArgumentOutOfRangeException(nameof(index));

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
            }

            _tree = value;
            _hasCamera = false;
            if (_tree is not null)
            {
                _tree.Changed += OnTreeChanged;
            }

            InvalidateVisual();
        }
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
                InvalidateVisual();
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

    /// <summary>Frames built so far; a benchmark tells a new frame from an old one by it.</summary>
    public long RenderCount { get; private set; }

    /// <summary>What the rest of the app has selected; drawn with the selection outline.</summary>
    public void SetSelection(IEnumerable<string> paths, string activePath)
    {
        _selected.Clear();
        foreach (var path in paths)
        {
            _selected.Add(path);
        }

        _activePath = activePath ?? string.Empty;
        InvalidateVisual();
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

        InvalidateVisual();
        _ = ResolveBeaconsAsync();
    }

    /// <summary>Marks changed: every cell's colours are worked out again on its next draw.</summary>
    public void InvalidateMarks()
    {
        _paletteStamp++;
        InvalidateVisual();
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

        var (endX, endY, endW) = FitRect(fill);
        if (!animated)
        {
            StopFlight();
            _anchor = target;
            _ax = endX;
            _ay = endY;
            _aw = endW;
            _cameraTouched = true;
            AfterCameraMove();
            return true;
        }

        _flight = Flight.Create(target, current.Value, (endX, endY, endW), _viewWidth, _viewHeight);
        _cameraTouched = true;
        StartRenderingHook();
        return true;
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

    public NestedCameraState? CaptureCamera()
    {
        if (_anchor is null || !_hasCamera || _viewWidth <= 0 || _anchor.IsComputer)
        {
            return null;
        }

        var centreX = _viewWidth / 2;
        var centreY = _viewHeight / 2;
        return new NestedCameraState(
            _anchor.FullPath,
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
        if (_tree is null || string.IsNullOrWhiteSpace(state.AnchorPath) || state.Width <= 0 || double.IsNaN(state.Width))
        {
            return;
        }

        _cameraTouched = false;
        var folder = await _tree.RevealAsync(state.AnchorPath);
        if (folder is null || _cameraTouched || !ViewAllPath.Equals(folder.FullPath, state.AnchorPath) || _viewWidth <= 0)
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
                return new NestedHit(folder, tile, true, fileIndex);
            }
        }

        var header = w * NestedLayout.HeaderHeight;
        var onHeader = header < HeaderGrabPixels || point.Y - y <= header;
        return new NestedHit(folder, new Rect(x, y, w, w * NestedLayout.CellHeight), onHeader);
    }

    /// <summary>
    /// The folder a path names, or the folder and index of the file it names,
    /// if that folder has been read.  Files are sorted as the reader sorted
    /// them, so finding one is a binary search, not a scan.
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

        var index = FindFile(parent, Path.GetFileName(path));
        return index >= 0 ? (parent, index) : null;
    }

    private static int FindFile(NestedFolder folder, string name)
    {
        var files = folder.Files;
        var low = 0;
        var high = files.Count - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var order = StringComparer.CurrentCultureIgnoreCase.Compare(files[middle].Name, name);
            if (order == 0)
            {
                return middle;
            }

            if (order < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        // Culture order and the file system can disagree about odd names; a
        // miss is checked the slow way rather than reported as absent.
        for (var index = 0; index < files.Count; index++)
        {
            if (string.Equals(files[index].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>A folder's cell or a file's tile on screen.</summary>
    private Rect? TargetRect(NestedFolder folder, int fileIndex)
    {
        if (RectOf(folder) is not { } r)
        {
            return null;
        }

        if (fileIndex < 0)
        {
            return new Rect(r.X, r.Y, r.W, r.W * NestedLayout.CellHeight);
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
                var hit = new NestedHit(target.Folder, default, true, target.FileIndex);
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

        // A folder that was refreshed away or filtered out cannot be the anchor.
        while (_anchor.Parent is not null && (_anchor.IsForgotten || _anchor.Index < 0))
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

        var isDeadEnd = _anchor.Children.Count == 0
            && (_anchor.LoadState is NestedLoadState.Loaded or NestedLoadState.Failed || _anchor.IsReparsePoint);
        var maximum = isDeadEnd ? 3 * _viewWidth : 1e7 * _viewWidth;
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

    private (double X, double Y, double W)? RectOf(NestedFolder folder)
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
            var step = path[index];
            rect = (rect.X + step.OffsetX * rect.W, rect.Y + step.OffsetY * rect.W, rect.W * step.Scale);
        }

        return rect;
    }

    private void AfterCameraMove()
    {
        InvalidateVisual();
        CameraChanged?.Invoke();
    }

    private static bool Inside(Point point, double x, double y, double w) =>
        point.X >= x && point.X <= x + w && point.Y >= y && point.Y <= y + w * NestedLayout.CellHeight;

    // ---- flights -------------------------------------------------------------

    private void StopFlight()
    {
        _flight = null;
        StopRenderingHook();
    }

    private void StartRenderingHook()
    {
        if (_isRenderingHooked)
        {
            return;
        }

        _isRenderingHooked = true;
        CompositionTarget.Rendering += OnRenderingTick;
    }

    private void StopRenderingHook()
    {
        if (!_isRenderingHooked)
        {
            return;
        }

        _isRenderingHooked = false;
        CompositionTarget.Rendering -= OnRenderingTick;
    }

    private void OnRenderingTick(object? sender, EventArgs e)
    {
        if (_flight is not { } flight)
        {
            StopRenderingHook();
            return;
        }

        if (flight.Target.IsForgotten)
        {
            StopFlight();
            return;
        }

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
                flight._r0 = Math.Log(-b0 + Math.Sqrt(b0 * b0 + 1));
                var r1 = Math.Log(-b1 + Math.Sqrt(b1 * b1 + 1));
                flight._length = (r1 - flight._r0) / Rho;
            }

            if (double.IsNaN(flight._length) || double.IsInfinity(flight._length))
            {
                flight._isPureZoom = true;
                flight._length = 0;
            }

            // Long journeys take longer, but never so long that it feels slow.
            flight._duration = Math.Clamp(0.22 + 0.16 * Math.Abs(flight._length), 0.25, 1.1);
            flight._started = Environment.TickCount64;
            return flight;
        }

        /// <summary>The target's rectangle at this moment; true once the flight has arrived.</summary>
        public bool Sample(double viewWidth, double viewHeight, out double x, out double y, out double w)
        {
            var t = (Environment.TickCount64 - _started) / 1000.0 / _duration;
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
        if (_hasCamera && sizeInfo.PreviousSize.Width > 0 && sizeInfo.PreviousSize.Height > 0)
        {
            // Keep the middle of the view on the same spot of the same folder.
            var dx = (sizeInfo.NewSize.Width - sizeInfo.PreviousSize.Width) / 2;
            var dy = (sizeInfo.NewSize.Height - sizeInfo.PreviousSize.Height) / 2;
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

        InvalidateVisual();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _bitmap = null;
        _textCache.Clear();
        InvalidateVisual();
    }

    private void OnTreeChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        _viewWidth = ActualWidth;
        _viewHeight = ActualHeight;
        if (_viewWidth < 1 || _viewHeight < 1 || _tree is null)
        {
            return;
        }

        using var frame = PerfLog.Measure("nested.frame");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        EnsureCamera();
        Normalize();
        BuildChain();
        _tree.BeginFrame();

        var dpi = VisualTreeHelper.GetDpi(this);
        _scaleX = dpi.DpiScaleX;
        _scaleY = dpi.DpiScaleY;
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(_viewWidth * _scaleX));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(_viewHeight * _scaleY));
        if (_bitmap is null || _bitmap.PixelWidth != pixelWidth || _bitmap.PixelHeight != pixelHeight)
        {
            _bitmap = new WriteableBitmap(pixelWidth, pixelHeight, 96 * _scaleX, 96 * _scaleY, PixelFormats.Pbgra32, null);
        }

        _labels.Clear();
        _labelled.Clear();
        _fileLabels.Clear();
        DrawnCellCount = 0;

        _bitmap.Lock();
        try
        {
            _raster.Attach(_bitmap.BackBuffer, pixelWidth, pixelHeight, _bitmap.BackBufferStride);
            _raster.Clear(CanvasColour);
            var (x, y, w) = _chain[_tree.Root];
            DrawCell(_tree.Root, x, y, w, labelsAllowed: true);
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, pixelWidth, pixelHeight));
        }
        finally
        {
            _raster.Detach();
            _bitmap.Unlock();
        }

        dc.DrawImage(_bitmap, new Rect(0, 0, pixelWidth / _scaleX, pixelHeight / _scaleY));

        _hotspots.Clear();
        DrawLabels(dc);
        DrawFileLabels(dc);
        DrawSelection(dc);
        DrawDropTarget(dc);
        DrawBeacons(dc);
        DrawTrail(dc);
        RenderOverlay();

        PerfLog.Value("nested.cells", DrawnCellCount);
        if (_textCache.Count > 6000)
        {
            _textCache.Clear();
        }

        LastRenderMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        RenderCount++;
    }

    private void DrawCell(NestedFolder folder, double x, double y, double w, bool labelsAllowed)
    {
        var h = w * NestedLayout.CellHeight;
        if (x >= _viewWidth || y >= _viewHeight || x + w <= 0 || y + h <= 0 || w < MinimumCellPixels)
        {
            return;
        }

        if (++DrawnCellCount > MaximumCellsPerFrame)
        {
            return;
        }

        EnsurePalette(folder);
        PaintCell(folder, x, y, w, h);

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
        if (grid.IsEmpty || w * grid.Scale < MinimumCellPixels)
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
    private void DrawFiles(NestedFolder folder, double x, double y, double w, bool labelsAllowed)
    {
        var grid = folder.FileGrid;
        if (grid.IsEmpty)
        {
            return;
        }

        var tileWidth = grid.TileWidth * w;
        var tileHeight = grid.TileHeight * w;
        if (tileWidth * _scaleX < 2.5)
        {
            if (w * _scaleX < 12)
            {
                return;
            }

            var usedWidth = Math.Min(NestedLayout.ContentWidth, grid.Columns * grid.StepX - grid.Gap);
            var usedHeight = Math.Min(
                NestedLayout.CellHeight - NestedLayout.Padding - grid.Top,
                grid.Rows * grid.StepY - grid.Gap);
            _raster.Fill(
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
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var index = row * grid.Columns + column;
                if (index >= files.Count)
                {
                    break;
                }

                if (++DrawnCellCount > MaximumCellsPerFrame)
                {
                    return;
                }

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
            _raster.Fill(
                NestedRaster.PxFloor(left),
                NestedRaster.PxFloor(top),
                Math.Max(NestedRaster.PxFloor(left) + 1, NestedRaster.Px(right)),
                Math.Max(NestedRaster.PxFloor(top) + 1, NestedRaster.Px(bottom)),
                speck);
            return;
        }

        var height = bottom - top;
        _raster.FillRounded(left, top, right, bottom, Math.Min(3 * _scaleX, height * 0.2), body);

        // The coloured edge says what kind of file it is before the name can.
        var stripeWidth = Math.Max(1, Math.Min(3 * _scaleX, height * 0.14));
        var inset = Math.Max(1, height * 0.18);
        _raster.Fill(
            NestedRaster.Px(left + 1),
            NestedRaster.Px(top + inset),
            NestedRaster.Px(left + 1 + stripeWidth),
            NestedRaster.Px(bottom - inset),
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

    private void DrawFileLabels(DrawingContext dc)
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
            var path = job.Folder.PathOf(file);

            var iconSize = Math.Min(job.H * 0.72, 20);
            if (iconSize >= 9 && IconLookup?.Invoke(path) is { } icon)
            {
                dc.DrawImage(icon, new Rect(cursor, job.Y + (job.H - iconSize) / 2, iconSize, iconSize));
                cursor += iconSize + font * 0.4;
            }

            var mark = _markLookup?.Invoke(path) ?? FolderMark.None;
            if (TryParseColour(mark.AccentHex, out var accent))
            {
                dc.DrawRectangle(BrushFor(accent), null, new Rect(job.X + 1, job.Y + job.H * 0.12, Math.Max(2, job.H * 0.16), job.H * 0.76));
            }

            if (!string.IsNullOrWhiteSpace(mark.Note))
            {
                var note = Text("\uE70B", font * 0.85, TextDimBrush, double.MaxValue, bold: false, icon: true);
                right -= note.Width;
                dc.DrawText(note, new Point(right, job.Y + (job.H - note.Height) / 2));
                right -= font * 0.4;
            }

            if (job.W >= 190)
            {
                var size = Text(FormatSize(file.Length), font * 0.85, TextDimBrush, double.MaxValue, bold: false);
                if (right - size.Width - cursor > font * 5)
                {
                    right -= size.Width;
                    dc.DrawText(size, new Point(right, job.Y + (job.H - size.Height) / 2));
                    right -= font * 0.6;
                }
            }

            if (right - cursor > font)
            {
                var name = Text(file.Name, font, file.IsHidden ? TextDimBrush : TextBrush, right - cursor, bold: false);
                dc.DrawText(name, new Point(cursor, job.Y + (job.H - name.Height) / 2));
            }
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var suffix = 0;
        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }

        return suffix == 0 ? $"{bytes:N0} B" : $"{value:0.#} {suffixes[suffix]}";
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
            _raster.Fill(
                NestedRaster.PxFloor(left),
                NestedRaster.PxFloor(top),
                Math.Max(NestedRaster.PxFloor(left) + 1, NestedRaster.Px(right)),
                Math.Max(NestedRaster.PxFloor(top) + 1, NestedRaster.Px(bottom)),
                folder.HasLabel ? folder.StripeColour : folder.RimColour);
            return;
        }

        var radius = pixelWidth >= 40 ? Math.Min(6 * _scaleX, pixelWidth * 0.03) : 0;
        _raster.FillRounded(left, top, right, bottom, radius, folder.RimColour);
        _raster.FillRounded(left + 1, top + 1, right - 1, bottom - 1, Math.Max(0, radius - 1), folder.BodyColour);

        var header = w * NestedLayout.HeaderHeight * _scaleY;
        if (header >= 2)
        {
            _raster.FillRounded(left + 1, top + 1, right - 1, top + header, Math.Max(0, radius - 1), folder.HeaderColour, roundBottom: false);
            if (header >= 6 && pixelWidth >= 30)
            {
                // The stripe down the title, in the folder's own colour: what
                // the stripe on a tree node was, a sign of whose this is.
                var inset = Math.Max(1, header * 0.2);
                var stripe = Math.Max(2, Math.Min(4 * _scaleX, header * 0.12));
                var stripeLeft = left + 1 + Math.Max(2 * _scaleX, header * 0.18);
                _raster.Fill(
                    NestedRaster.Px(stripeLeft),
                    NestedRaster.Px(top + inset),
                    NestedRaster.Px(stripeLeft + stripe),
                    NestedRaster.Px(top + header - inset),
                    folder.StripeColour);
            }
        }
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

        folder.BodyColour = body;
        folder.HeaderColour = header;
        folder.RimColour = rim;
        folder.StripeColour = stripe;
    }

    private void DrawLabels(DrawingContext dc)
    {
        var pixelsPerDip = _scaleY;
        foreach (var job in _labels)
        {
            var folder = job.Folder;
            if (job.Mode == LabelMode.Pill)
            {
                var text = Text(folder.Name, PillFontSize, TextBrush, Math.Max(8, job.W - 10), bold: false);
                var pill = new Rect(job.X + 2, job.Y + 2, Math.Min(job.W - 4, text.Width + 8), text.Height + 2);
                if (pill.Width < 12)
                {
                    continue;
                }

                dc.DrawRoundedRectangle(PillBrush, null, pill, 3, 3);
                dc.DrawText(text, new Point(pill.X + 4, pill.Y + 1));
                AddGrabHotspot(folder, pill);
                continue;
            }

            var header = job.W * NestedLayout.HeaderHeight;
            if (job.Y + header < 0 || job.Y > _viewHeight)
            {
                DrawBodyNote(dc, folder, job);
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
                var icons = Text(badges, font * 0.82, BadgeBrush(folder), double.MaxValue, bold: false, icon: true);
                right -= icons.Width;
                if (right - cursor > font * 3)
                {
                    dc.DrawText(icons, new Point(right, job.Y + (header - icons.Height) / 2));
                    right -= font * 0.4;
                }
                else
                {
                    right += icons.Width;
                }
            }

            var detail = DetailText(folder);
            if (detail.Length > 0 && job.W >= 280)
            {
                var info = Text(detail, font * 0.78, TextDimBrush, double.MaxValue, bold: false);
                if (right - info.Width - cursor > font * 6)
                {
                    right -= info.Width;
                    dc.DrawText(info, new Point(right, job.Y + (header - info.Height) / 2));
                    right -= font * 0.6;
                }
            }

            var glyph = Text(Glyph(folder), font * 0.9, GlyphBrush(folder), double.MaxValue, bold: false, icon: true);
            if (right - cursor > glyph.Width + font)
            {
                dc.DrawText(glyph, new Point(cursor, job.Y + (header - glyph.Height) / 2 + font * 0.05));
                cursor += glyph.Width + font * 0.4;
            }

            var available = right - cursor;
            if (available > font)
            {
                var name = Text(folder.Name, font, TextBrush, available, bold: folder.Kind != NestedFolderKind.Folder);
                dc.DrawText(name, new Point(cursor, job.Y + (header - name.Height) / 2));
                AddGrabHotspot(folder, new Rect(job.X, job.Y, job.W, header));
            }

            DrawBodyNote(dc, folder, job);
        }
    }

    /// <summary>What an empty cell says in its middle, when there is room to say it.</summary>
    private void DrawBodyNote(DrawingContext dc, NestedFolder folder, LabelJob job)
    {
        var h = job.W * NestedLayout.CellHeight;
        var header = job.W * NestedLayout.HeaderHeight;
        if (h - header < 40 || job.W < 120)
        {
            return;
        }

        string message;
        var brush = TextDimBrush;
        if (folder.IsReparsePoint)
        {
            message = "Link to another folder";
        }
        else if (folder.LoadState == NestedLoadState.Failed)
        {
            message = string.IsNullOrEmpty(folder.ErrorMessage) ? "Could not be read" : folder.ErrorMessage;
            brush = DangerBrush;
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
                _ => $"{folder.FileCount:N0} hidden files"
            };
        }
        else if (folder.UnlistedFileCount > 0)
        {
            message = $"{folder.UnlistedFileCount:N0} more files are not drawn";
            var text = Text(message, Math.Clamp(job.W * 0.018, 9, 14), TextDimBrush, job.W - 16, bold: false);
            dc.DrawText(text, new Point(job.X + (job.W - text.Width) / 2, job.Y + h - text.Height - 4));
            return;
        }
        else if (folder.IsTruncated)
        {
            message = $"Only the first {NestedTree.MaximumChildren:N0} folders are shown";
            var text = Text(message, Math.Clamp(job.W * 0.018, 9, 14), TextDimBrush, job.W - 16, bold: false);
            dc.DrawText(text, new Point(job.X + (job.W - text.Width) / 2, job.Y + h - text.Height - 4));
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
        var formatted = Text(message, size, brush, job.W - 16, bold: false);
        var bodyTop = job.Y + header;
        dc.DrawText(formatted, new Point(job.X + (job.W - formatted.Width) / 2, bodyTop + (h - header - formatted.Height) / 2));
    }

    private static string DetailText(NestedFolder folder)
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

        var parts = new List<string>(2);
        if (folders > 0)
        {
            parts.Add(folders == 1 ? "1 folder" : $"{folders:N0} folders");
        }

        if (files > 0)
        {
            parts.Add(files == 1 ? "1 file" : $"{files:N0} files");
        }

        return string.Join("  ·  ", parts);
    }

    private string Badges(NestedFolder folder)
    {
        var badges = string.Empty;
        if (IsPinned(folder.FullPath))
        {
            badges += "";
        }

        if (folder.HasNote)
        {
            badges += "";
        }

        if (folder.IsReparsePoint)
        {
            badges += "";
        }

        if (folder.LoadState == NestedLoadState.Failed)
        {
            badges += "";
        }

        return badges;
    }

    private Brush BadgeBrush(NestedFolder folder) =>
        folder.LoadState == NestedLoadState.Failed ? DangerBrush : IsPinned(folder.FullPath) ? StarBrush : TextDimBrush;

    private static string Glyph(NestedFolder folder) => folder.Kind switch
    {
        NestedFolderKind.Computer => "",
        NestedFolderKind.Drive => folder.FullPath.StartsWith(@"\\", StringComparison.Ordinal) ? "" : "",
        _ => folder.IsReparsePoint ? "" : ""
    };

    private Brush GlyphBrush(NestedFolder folder) => folder.Kind switch
    {
        NestedFolderKind.Folder when folder.HasLabel => BrushFor(NestedRaster.Unpack(folder.StripeColour)),
        NestedFolderKind.Folder => FolderBrush,
        _ => TextDimBrush
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

    private static readonly Brush FolderBrush = Frozen(Color.FromRgb(0xE3, 0xB3, 0x41));

    private void DrawSelection(DrawingContext dc)
    {
        if (_tree is null)
        {
            return;
        }

        foreach (var path in _selected)
        {
            if (Resolve(path) is not { } target || TargetRect(target.Folder, target.FileIndex) is not { } rect || rect.Width < 3)
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
        if (_dropTarget is null || ScreenRect(_dropTarget) is not { } rect)
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
        using var dc = _overlay.RenderOpen();
        DrawHover(dc);
        DrawHoverTip(dc);
    }

    private void DrawHover(DrawingContext dc)
    {
        if (_hover is not { } hover || _press != PressKind.None || TargetRect(hover.Folder, hover.FileIndex) is not { } rect || rect.Width < 3)
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
            dc.DrawText(tipText, new Point(tipBox.X + 8, tipBox.Y + 4));
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
        var sub = second.Length > 0 ? Text(second, 11, noteText.Length > 0 ? TextBrush : TextDimBrush, 360, bold: false) : null;

        var width = Math.Max(title.Width, sub?.Width ?? 0) + 16;
        var height = title.Height + (sub?.Height ?? 0) + 10;
        var x = _hoverPoint.X + 16;
        var y = _hoverPoint.Y + 18;
        if (x + width > _viewWidth - 4) x = _hoverPoint.X - width - 8;
        if (y + height > _viewHeight - 4) y = _hoverPoint.Y - height - 8;
        var box = new Rect(Math.Max(4, x), Math.Max(4, y), width, height);
        dc.DrawRoundedRectangle(TipBrush, TipPen, box, 5, 5);
        dc.DrawText(title, new Point(box.X + 8, box.Y + 5));
        if (sub is not null)
        {
            dc.DrawText(sub, new Point(box.X + 8, box.Y + 5 + title.Height));
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
                    .Where(path => !_unresolvable.Contains(path) && (Resolve(path) is not { } found || !NestedTree.IsOnCanvas(found.Folder)))
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
                        if (Resolve(path) is null)
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

                InvalidateVisual();
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
            if (Resolve(beacon.Path) is not { } target || TargetRect(target.Folder, target.FileIndex) is not { } rect)
            {
                continue;
            }

            // Anything big enough to carry its own name carries its own mark.
            if (target.FileIndex < 0 ? _labelled.Contains(target.Folder) && rect.Width >= 90 : rect.Height >= FileLabelPixels)
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
            var home = clusters.FirstOrDefault(cluster => (cluster[0].Centre - pin.Centre).Length < 13);
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
                    dc.DrawText(icon, new Point(centre.X - icon.Width / 2, centre.Y - icon.Height / 2));
                }
            }
            else
            {
                var count = Text(cluster.Count.ToString(CultureInfo.CurrentCulture), 10, BeaconGlyphBrush(lead.Beacon.Colour), double.MaxValue, bold: true);
                var radius = Math.Max(8.5, count.Width / 2 + 5);
                dc.DrawEllipse(null, BeaconHaloPen, centre, radius + 2.5, radius + 2.5);
                dc.DrawEllipse(brush, BeaconRimPen, centre, radius, radius);
                dc.DrawText(count, new Point(centre.X - count.Width / 2, centre.Y - count.Height / 2));
            }

            var hit = new Rect(centre.X - 10, centre.Y - 10, 20, 20);
            var members = cluster.Select(pin => (pin.Folder, pin.FileIndex)).ToList();
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
            dc.DrawText(text, new Point(labelRect.X + 5, labelRect.Y + 2));
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
                dc.DrawText(count, new Point(at.X - count.Width / 2, at.Y - count.Height / 2));
            }

            var targets = members.Select(pin => (pin.Folder, pin.FileIndex)).ToList();
            _hotspots.Add(new Hotspot(new Rect(at.X - 11, at.Y - 11, 22, 22), () => OnBeaconClicked(targets), null, EdgeTip(members)));
        }
    }

    private static string EdgeTip(List<Pin> members) =>
        members.Count == 1 ? members[0].Beacon.Label : $"{members[0].Beacon.Label} and {members.Count - 1} more";

    private void OnBeaconClicked(IReadOnlyList<(NestedFolder Folder, int FileIndex)> targets)
    {
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

            // A file is flown to in its folder, big enough to read.
            FlyTo(folder, fileIndex >= 0 ? 0.92 : 0.6);
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

        var trail = new List<NestedFolder>();
        var folder = _tree.Root;
        var (x, y, w) = _chain[folder];
        var probe = new Point(_viewWidth / 2, Math.Min(_viewHeight / 2, 80));
        while (true)
        {
            if (y + w * NestedLayout.HeaderHeight < 0 && !folder.IsComputer)
            {
                trail.Add(folder);
            }

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
        var shown = trail.Count <= 5 ? trail : [trail[0], null!, .. trail.Skip(trail.Count - 3)];
        const double pad = 8;
        var cursor = 14 + pad;
        var top = 12.0;
        var pieces = new List<(FormattedText Text, NestedFolder? Folder)>();
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
            dc.DrawText(text, at);
            if (step is not null)
            {
                var target = step;
                _hotspots.Add(new Hotspot(new Rect(at, new Size(text.Width, text.Height)), () => FlyTo(target), null));
            }

            cursor += text.Width;
            if (index < pieces.Count - 1)
            {
                dc.DrawText(separator, new Point(cursor, top + 4));
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

            if (HitTest(point) is { } hit && !hit.Folder.IsComputer)
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
        _pressHit = HitTest(point);
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
        return _hotspots.Any(spot => spot.Grab is not null && ReferenceEquals(spot.Grab, hit.Folder) && spot.Bounds.Contains(point));
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
        var hit = HitTest(point);
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
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover is not null)
        {
            _hover = null;
            RenderOverlay();
        }
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
        if (spot is { Tip: not null } || wasOnTip)
        {
            _hover = null;
            RenderOverlay();
            return;
        }

        var hit = spot is null ? HitTest(point) : null;
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
        _hotspots.Add(new Hotspot(bounds, null, folder));

    /// <summary>Shows a click as selected at once, before the rest of the window catches up.</summary>
    private void MarkSelected(string path)
    {
        _selected.Clear();
        _selected.Add(path);
        _activePath = path;
        InvalidateVisual();
    }

    /// <summary>
    /// The next folder or file in the direction of an arrow: sub-folders move
    /// over their parent's grid, files over their folder's.
    /// </summary>
    private (NestedFolder Folder, int FileIndex)? Neighbour((NestedFolder Folder, int FileIndex)? active, Key key)
    {
        if (active is not { } current || current.FileIndex < 0 && (current.Folder.Parent is null || current.Folder.Index < 0))
        {
            // Nothing selected yet: start at the first folder in the one in view.
            return _anchor is { Children.Count: > 0 } anchor ? (anchor.Children[0], -1) : null;
        }

        if (current.FileIndex >= 0)
        {
            var files = current.Folder.FileGrid;
            var nextFile = Step(current.FileIndex, files.Columns, key);
            return nextFile >= 0 && nextFile < current.Folder.Files.Count ? (current.Folder, nextFile) : null;
        }

        var parent = current.Folder.Parent!;
        var next = Step(current.Folder.Index, parent.Grid.Columns, key);
        return next >= 0 && next < parent.Children.Count ? (parent.Children[next], -1) : null;
    }

    private static int Step(int index, int columns, Key key) => key switch
    {
        Key.Left => index - 1,
        Key.Right => index + 1,
        Key.Up => index - columns,
        _ => index + columns
    };

    private void EnsureVisible(NestedFolder folder, int fileIndex = -1)
    {
        var rect = TargetRect(folder, fileIndex);
        var tooSmall = rect is null || (fileIndex >= 0 ? rect.Value.Height < FileLabelPixels : rect.Value.Width < 40);
        if (tooSmall)
        {
            var frame = fileIndex >= 0 ? folder : folder.Parent is { IsComputer: false } parent ? parent : folder;
            FlyTo(frame, 0.92);
            return;
        }

        var view = new Rect(0, 0, _viewWidth, _viewHeight);
        if (!view.Contains(rect!.Value))
        {
            if (fileIndex >= 0)
            {
                FlyTo(folder, Math.Min(0.92, ScreenRect(folder)?.Width / _viewWidth ?? 0.92));
            }
            else
            {
                FlyTo(folder, Math.Min(0.92, rect.Value.Width / _viewWidth));
            }
        }
    }

    private Rect? ScreenRect(NestedFolder folder)
    {
        var result = RectOf(folder);
        return result is { } r ? new Rect(r.X, r.Y, r.W, r.W * NestedLayout.CellHeight) : null;
    }

    // ---- text ------------------------------------------------------------------

    private FormattedText Text(string text, double size, Brush brush, double maxWidth, bool bold, bool icon = false)
    {
        size = Math.Clamp(Math.Round(size * 4) / 4, 1, 400);
        var widthKey = double.IsInfinity(maxWidth) || maxWidth >= 10_000 ? -1 : (int)Math.Floor(maxWidth / 6);
        var key = new TextKey(text, size, widthKey, brush, bold, icon);
        if (_textCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            icon ? IconFace : bold ? TextFaceBold : TextFace,
            size,
            brush,
            _scaleY)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };

        if (widthKey >= 0)
        {
            formatted.MaxTextWidth = Math.Max(1, widthKey * 6);
        }

        _textCache[key] = formatted;
        return formatted;
    }

    private static bool TryParseColour(string hex, out Color colour)
    {
        colour = default;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        try
        {
            colour = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch (FormatException)
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
}
