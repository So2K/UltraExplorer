using System.Windows;
using System.Windows.Media;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

/// <summary>
/// Draws every link on the canvas, at every zoom, as one harness per folder.
///
/// It replaces two different pictures of the same tree: rounded step connections
/// above 30% zoom and midpoint elbows in the batched layer below it.  Those
/// disagreed with each other, and both drew a line per child - which is what
/// turned a folder of twenty-one into a grid of crossings.  One geometry per
/// folder, grouped by colour and frozen, costs a handful of draw calls for a
/// graph of any size, and panning only moves an already uploaded visual.
/// </summary>
public sealed class ViewAllHarnessLayer : FrameworkElement
{
    private const double LinkThickness = 1.5;
    private const double FrameThickness = 1.1;

    /// <summary>How large a window to build, as a multiple of the viewport.</summary>
    private const double WindowScale = 1.6;

    private readonly List<ViewAllNodeViewModel> _scratch = [];
    private readonly HashSet<ViewAllNodeViewModel> _parents = [];
    private readonly List<KeyValuePair<Color, Geometry>> _links = [];
    private readonly List<KeyValuePair<Color, Geometry>> _frames = [];
    private readonly List<KeyValuePair<Pen, Geometry>> _drawList = [];
    private readonly List<BlockLabel> _labels = [];
    private readonly MatrixTransform _transform = new(Matrix.Identity);

    private Rect _builtWindow = Rect.Empty;
    private double _penZoom = 1;

    public ViewAllHarnessLayer()
    {
        IsHitTestVisible = false;
        ClipToBounds = false;
        RenderTransform = _transform;
    }

    /// <summary>Grid the layer reads its nodes from.</summary>
    public ViewAllSpatialIndex? Index { get; set; }

    /// <summary>Children above this many get a trunk and a frame, but no ticks.</summary>
    public int TickLimit { get; set; } = 64;

    public bool DrawFrames { get; set; } = true;

    /// <summary>
    /// A block narrower than this on screen gets no name: at that size the text
    /// would be unreadable, and a hundred of them would be noise.
    /// </summary>
    public double LabelMinimumWidth { get; set; } = 150;

    public int LabelLimit { get; set; } = 48;

    private readonly record struct BlockLabel(Point Anchor, double Width, string Text, Color Colour);

    /// <summary>Number of horizontal runs in the geometry currently drawn.</summary>
    public int DrawnHorizontals { get; private set; }

    public void InvalidateGeometry() => _builtWindow = Rect.Empty;

    public void Update(Point viewportLocation, Size viewportSize, double zoom)
    {
        if (Index is null || viewportSize.Width <= 0 || viewportSize.Height <= 0 || zoom <= 0)
        {
            return;
        }

        // ViewportSize is already in graph space; dividing by the zoom again
        // built a window 1/zoom across - twenty-five times the area at 20% - and
        // every rebuild paid for geometry nowhere near the screen.
        var viewport = new Rect(viewportLocation, viewportSize);

        if (_builtWindow.IsEmpty || !_builtWindow.Contains(viewport))
        {
            var window = viewport;
            window.Inflate(
                viewport.Width * (WindowScale - 1) / 2,
                viewport.Height * (WindowScale - 1) / 2);
            Rebuild(window);
        }

        if (Math.Abs(zoom - _penZoom) > 0.0001)
        {
            _penZoom = zoom;
            RebuildPens();
        }

        // screen = (graph - viewportLocation) * zoom
        _transform.Matrix = new Matrix(zoom, 0, 0, zoom, -viewportLocation.X * zoom, -viewportLocation.Y * zoom);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        foreach (var entry in _drawList)
        {
            drawingContext.DrawGeometry(null, entry.Key, entry.Value);
        }

        DrawLabels(drawingContext);
    }

    /// <summary>
    /// The folder's name written once above its block, at a fixed size on screen.
    /// Zoomed out the canvas otherwise shows several coloured groups with no way
    /// to tell which folder any of them is; the names are what turn a picture of
    /// the disk into something that can be navigated.
    /// </summary>
    private void DrawLabels(DrawingContext drawingContext)
    {
        var zoom = Math.Max(_penZoom, 0.01);
        var drawn = 0;

        foreach (var label in _labels)
        {
            if (drawn >= LabelLimit)
            {
                break;
            }

            if (label.Width * zoom < LabelMinimumWidth)
            {
                continue;
            }

            var brush = new SolidColorBrush(label.Colour);
            brush.Freeze();

            // Size cancels the zoom, so the name is the same height on screen
            // however far out the canvas is - the trick the pens already use.
            var text = new FormattedText(
                label.Text,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                12 / zoom,
                brush,
                pixelsPerDip: 1);

            drawingContext.DrawText(text, new Point(label.Anchor.X, label.Anchor.Y - text.Height - 3 / zoom));
            drawn++;
        }
    }

    private static readonly Typeface LabelTypeface = new("Segoe UI Semibold");

    private void Rebuild(Rect window)
    {
        _scratch.Clear();
        _parents.Clear();
        _links.Clear();
        _frames.Clear();
        _labels.Clear();
        DrawnHorizontals = 0;

        Index!.Query(window, _scratch);

        // The folders whose children are in view, plus the folders whose own
        // block is: a block taller than the viewport must keep its harness when
        // the parent has scrolled off the top.
        foreach (var node in _scratch)
        {
            if (node.IsTreeVisible && node.Parent is { IsTreeVisible: true, IsExpanded: true } parent)
            {
                _parents.Add(parent);
            }

            if (node.IsExpanded && node.IsTreeVisible && node.ChildBlock is not null)
            {
                _parents.Add(node);
            }
        }

        var links = new Dictionary<Color, StreamGeometry>();
        var contexts = new Dictionary<Color, StreamGeometryContext>();
        var frames = new Dictionary<Color, GeometryGroup>();

        try
        {
            foreach (var parent in _parents)
            {
                // One row of children already fans straight down from the parent
                // and reads fine; those keep their own connections.
                if (parent.ChildBlock is not { Rows: > 1 } block || !parent.IsExpanded)
                {
                    continue;
                }

                if (!window.IntersectsWith(block.BoundsFor(parent.Location)))
                {
                    continue;
                }

                var built = ViewAllHarnessGeometry.Build(parent, block, TickLimit);
                DrawnHorizontals += built.Horizontals;

                var colour = parent.BranchColor;
                if (!contexts.TryGetValue(colour, out var context))
                {
                    var geometry = new StreamGeometry();
                    links[colour] = geometry;
                    context = geometry.Open();
                    contexts[colour] = context;
                }

                foreach (var segment in built.Segments)
                {
                    context.BeginFigure(segment.From, isFilled: false, isClosed: false);
                    context.LineTo(segment.To, isStroked: true, isSmoothJoin: false);
                }

                _labels.Add(new BlockLabel(
                    new Point(built.Frame.Left, built.Frame.Top),
                    built.Frame.Width,
                    parent.DisplayName,
                    colour));

                if (DrawFrames)
                {
                    if (!frames.TryGetValue(colour, out var group))
                    {
                        group = new GeometryGroup();
                        frames[colour] = group;
                    }

                    var corner = 6 * block.Scale;
                    group.Children.Add(new RectangleGeometry(built.Frame, corner, corner));
                }
            }
        }
        finally
        {
            foreach (var context in contexts.Values)
            {
                context.Close();
            }
        }

        foreach (var pair in links)
        {
            pair.Value.Freeze();
            _links.Add(new KeyValuePair<Color, Geometry>(pair.Key, pair.Value));
        }

        foreach (var pair in frames)
        {
            pair.Value.Freeze();
            _frames.Add(new KeyValuePair<Color, Geometry>(pair.Key, pair.Value));
        }

        _builtWindow = window;
        RebuildPens();
        InvalidateVisual();
    }

    /// <summary>
    /// Thickness cancels the zoom, so a link stays a hairline however far out the
    /// canvas is - the same trick the batched slab layer uses.
    /// </summary>
    private void RebuildPens()
    {
        _drawList.Clear();
        var zoom = Math.Max(_penZoom, 0.01);

        foreach (var pair in _frames)
        {
            var colour = pair.Key;
            colour.A = 90;
            _drawList.Add(new KeyValuePair<Pen, Geometry>(CreatePen(colour, FrameThickness / zoom), pair.Value));
        }

        foreach (var pair in _links)
        {
            _drawList.Add(new KeyValuePair<Pen, Geometry>(CreatePen(pair.Key, LinkThickness / zoom), pair.Value));
        }
    }

    private static Pen CreatePen(Color color, double thickness)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, Math.Max(thickness, 0.1));
        pen.Freeze();
        return pen;
    }
}
