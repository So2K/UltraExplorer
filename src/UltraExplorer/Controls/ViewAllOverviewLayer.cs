using System.Windows;
using System.Windows.Media;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

/// <summary>
/// Draws the whole graph as a handful of batched geometries instead of one
/// control per node.
///
/// Two things make this cheap.  The geometry is built in graph coordinates and
/// cached frozen, so panning only changes this element's RenderTransform — the
/// composition thread moves an already uploaded visual and nothing is
/// re-recorded.  And nodes are grouped by colour, so a hundred thousand slabs
/// cost a handful of draw calls rather than a hundred thousand visual trees.
///
/// The cached geometry covers a window larger than the viewport, so a rebuild is
/// only needed once the user pans past its margin.
/// </summary>
public sealed class ViewAllOverviewLayer : FrameworkElement
{
    private const double EdgeThickness = 1.2;
    private const int DensityTiers = 4;

    private readonly List<ViewAllNodeViewModel> _scratch = [];
    private readonly List<KeyValuePair<Brush, Geometry>> _drawList = [];
    private readonly MatrixTransform _transform = new(Matrix.Identity);

    private Rect _builtWindow = Rect.Empty;
    private OverviewMode _builtMode = OverviewMode.SlabsAndBranches;
    private double _penZoom = 1;
    private int _builtCount;

    private enum OverviewMode
    {
        /// <summary>Whole regions as density blocks; used at the furthest zoom.</summary>
        Density,
        Slabs,
        SlabsAndBranches
    }

    public ViewAllOverviewLayer()
    {
        IsHitTestVisible = false;
        ClipToBounds = false;
        RenderTransform = _transform;
    }

    /// <summary>Grid the layer reads its nodes from.</summary>
    public ViewAllSpatialIndex? Index { get; set; }

    /// <summary>Number of nodes in the geometry currently cached.</summary>
    public int DrawnNodeCount => _builtCount;

    /// <summary>Forces the next update to rebuild, after the graph changed.</summary>
    public void InvalidateGeometry() => _builtWindow = Rect.Empty;

    /// <summary>
    /// Positions the cached geometry for the current viewport, rebuilding it
    /// only when the viewport has left the window that was built for, or when
    /// the mip level changed.
    /// </summary>
    public void Update(Point viewportLocation, Size viewportSize, double zoom, ViewAllDetailLevel detail)
    {
        if (Index is null || viewportSize.Width <= 0 || viewportSize.Height <= 0 || zoom <= 0)
        {
            return;
        }

        var mode = detail switch
        {
            ViewAllDetailLevel.Cluster => OverviewMode.Density,
            ViewAllDetailLevel.Dot => OverviewMode.Slabs,
            _ => OverviewMode.SlabsAndBranches
        };

        var viewport = new Rect(viewportLocation, viewportSize);
        var safe = viewport;
        safe.Inflate(viewportSize.Width * 0.25, viewportSize.Height * 0.25);

        if (_builtWindow.IsEmpty || mode != _builtMode || !_builtWindow.Contains(safe))
        {
            var window = viewport;
            window.Inflate(viewportSize.Width, viewportSize.Height);
            Rebuild(window, mode);
        }

        // The render transform scales the stroke too, so a 1.2 px branch would
        // vanish at 10%. Compensate, but only when the zoom actually moved.
        if (Math.Abs(zoom - _penZoom) > _penZoom * 0.02)
        {
            _penZoom = zoom;
            InvalidateVisual();
        }

        // screen = (graph - viewportLocation) * zoom
        _transform.Matrix = new Matrix(zoom, 0, 0, zoom, -viewportLocation.X * zoom, -viewportLocation.Y * zoom);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        foreach (var entry in _drawList)
        {
            drawingContext.DrawGeometry(entry.Key, null, entry.Value);
        }
    }

    private void Rebuild(Rect window, OverviewMode mode)
    {
        _builtMode = mode;
        if (mode == OverviewMode.Density)
        {
            RebuildDensity(window);
            return;
        }

        _scratch.Clear();
        Index!.Query(window, _scratch);

        var geometries = new Dictionary<Color, StreamGeometry>();
        var contexts = new Dictionary<Color, StreamGeometryContext>();
        var drawn = 0;

        try
        {
            foreach (var node in _scratch)
            {
                if (!node.IsTreeVisible)
                {
                    continue;
                }

                drawn++;
                var colour = ResolveColour(node);
                if (!contexts.TryGetValue(colour, out var context))
                {
                    var geometry = new StreamGeometry();
                    geometries[colour] = geometry;
                    context = geometry.Open();
                    contexts[colour] = context;
                }

                AddSlab(context, node.Bounds, node.Scale);
            }
        }
        finally
        {
            foreach (var context in contexts.Values)
            {
                context.Close();
            }

        }

        _drawList.Clear();
        foreach (var pair in geometries)
        {
            pair.Value.Freeze();
            _drawList.Add(new KeyValuePair<Brush, Geometry>(SolidBrush(pair.Key), pair.Value));
        }

        _builtWindow = window;
        _builtCount = drawn;
        InvalidateVisual();
    }

    /// <summary>
    /// The furthest mip level: instead of one slab per object, the occupied grid
    /// cells are merged into blocks and shaded by how full they are.  The cost is
    /// proportional to the grid, not to the number of nodes, so this level does
    /// not care whether the graph holds ten thousand nodes or ten million.
    /// </summary>
    private void RebuildDensity(Rect window)
    {
        const int coarseness = 4;
        var block = ViewAllSpatialIndex.CellExtent * coarseness;
        var blocks = new Dictionary<(long X, long Y), int>();

        Index!.QueryCellCounts(window, (cell, count) =>
        {
            var key = ((long)Math.Floor(cell.X / block), (long)Math.Floor(cell.Y / block));
            blocks[key] = blocks.TryGetValue(key, out var running) ? running + count : count;
        });

        _drawList.Clear();

        if (blocks.Count == 0)
        {
            _builtWindow = window;
            _builtCount = 0;
            InvalidateVisual();
            return;
        }

        var busiest = blocks.Values.Max();
        var tiers = new StreamGeometry[DensityTiers];
        var contexts = new StreamGeometryContext[DensityTiers];
        for (var tier = 0; tier < DensityTiers; tier++)
        {
            tiers[tier] = new StreamGeometry();
            contexts[tier] = tiers[tier].Open();
        }

        var total = 0;
        try
        {
            foreach (var pair in blocks)
            {
                total += pair.Value;
                var fill = busiest <= 1 ? 1d : pair.Value / (double)busiest;
                var tier = Math.Clamp((int)(Math.Sqrt(fill) * DensityTiers), 0, DensityTiers - 1);
                var bounds = new Rect(pair.Key.X * block, pair.Key.Y * block, block, block);
                bounds.Inflate(-block * 0.06, -block * 0.06);
                AddRectangle(contexts[tier], bounds);
            }
        }
        finally
        {
            foreach (var context in contexts)
            {
                context.Close();
            }
        }

        for (var tier = 0; tier < DensityTiers; tier++)
        {
            if (tiers[tier].IsEmpty())
            {
                continue;
            }

            tiers[tier].Freeze();
            _drawList.Add(new KeyValuePair<Brush, Geometry>(DensityBrush(tier), tiers[tier]));
        }

        _builtWindow = window;
        _builtCount = total;
        InvalidateVisual();
    }

    private static Brush DensityBrush(int tier)
    {
        // Dim grey for a sparse region up to the folder accent for a dense one.
        var amount = (tier + 1) / (double)DensityTiers;
        var color = Color.FromRgb(
            (byte)(0x3A + (0xE3 - 0x3A) * amount),
            (byte)(0x3A + (0xB3 - 0x3A) * amount),
            (byte)(0x3A + (0x41 - 0x3A) * amount));
        return SolidBrush(color);
    }

    private static void AddRectangle(StreamGeometryContext context, Rect bounds)
    {
        context.BeginFigure(bounds.TopLeft, isFilled: true, isClosed: true);
        context.LineTo(bounds.TopRight, isStroked: false, isSmoothJoin: false);
        context.LineTo(bounds.BottomRight, isStroked: false, isSmoothJoin: false);
        context.LineTo(bounds.BottomLeft, isStroked: false, isSmoothJoin: false);
    }

    private static void AddSlab(StreamGeometryContext context, Rect bounds, double scale)
    {
        // Inset so neighbouring slabs stay visually separate when zoomed out,
        // scaled with the node so deep branches keep the same proportions.
        var left = bounds.Left + 2 * scale;
        var top = bounds.Top + 6 * scale;
        var right = bounds.Right - 2 * scale;
        var bottom = bounds.Bottom - 6 * scale;

        context.BeginFigure(new Point(left, top), isFilled: true, isClosed: true);
        context.LineTo(new Point(right, top), isStroked: false, isSmoothJoin: false);
        context.LineTo(new Point(right, bottom), isStroked: false, isSmoothJoin: false);
        context.LineTo(new Point(left, bottom), isStroked: false, isSmoothJoin: false);
    }

    /// <summary>
    /// Zoomed out, a slab is coloured by the folder it belongs to rather than by
    /// what kind of thing it is: at this size the shape already says folder or
    /// file, and what is hard to see is which branch something is part of.
    /// </summary>
    private static Color ResolveColour(ViewAllNodeViewModel node)
        => node.FamilyBrush is SolidColorBrush brush ? brush.Color : Colors.Gray;

    private static Brush SolidBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
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
