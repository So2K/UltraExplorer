using System.Windows;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

public sealed record ViewAllViewportOptions(
    double OverscanPixels = 320,
    int MaximumDetailedNodes = 900,
    int MaximumCompactNodes = 2_000);

/// <summary>
/// Decides what the canvas actually realizes.
///
/// Above the overview threshold the editor gets a small, viewport-culled set of
/// real containers.  Below it the editor gets almost nothing and the whole graph
/// is drawn as a handful of batched geometries instead, which is what keeps a
/// graph of millions of nodes navigable — a WPF control per node is the wall
/// every node canvas hits first.
/// </summary>
public sealed class ViewAllViewportService(ViewAllViewportOptions? options = null)
{
    private readonly ViewAllViewportOptions _options = options ?? new ViewAllViewportOptions();
    private readonly HashSet<ViewAllNodeViewModel> _realized = [];
    private readonly List<ViewAllNodeViewModel> _candidates = [];

    /// <summary>
    /// The mip pyramid, from a whole-drive overview down to full detail. Each
    /// step drops whatever has stopped being legible at that size: metadata,
    /// then the name, then the icon, then the individual object itself.
    /// </summary>
    public ViewAllDetailLevel GetDetailLevel(double zoom) => zoom switch
    {
        // Under 6% a node is ~11 px: individual slabs turn into noise, so whole
        // regions are drawn as density blocks instead.
        < 0.06 => ViewAllDetailLevel.Cluster,
        // Below ~15% a 188 x 44 node is under 30 px wide, so text and icons stop
        // carrying information and only the coloured slab is worth drawing.
        < 0.15 => ViewAllDetailLevel.Dot,
        < 0.30 => ViewAllDetailLevel.Glyph,
        < 0.62 => ViewAllDetailLevel.Compact,
        _ => ViewAllDetailLevel.Detailed
    };

    /// <summary>
    /// True when the graph is drawn as batched geometry instead of controls.
    /// </summary>
    public static bool UsesOverview(ViewAllDetailLevel level)
        => level <= ViewAllDetailLevel.Glyph;

    public ViewAllRenderSet BuildRenderSet(
        ViewAllSpatialIndex index,
        IReadOnlyList<ViewAllEdgeViewModel> edges,
        IReadOnlyCollection<ViewAllNodeViewModel> alwaysRealized,
        Rect viewportInGraphCoordinates,
        double zoom,
        int logicalNodeCount,
        IReadOnlyDictionary<Guid, ViewAllEdgeViewModel>? incomingEdges = null)
    {
        zoom = Math.Clamp(zoom, 0.01, 8);
        var detail = GetDetailLevel(zoom);

        _candidates.Clear();

        if (UsesOverview(detail))
        {
            // Only the selection keeps a container, so selecting stays possible
            // and the editor never drops the selection while zoomed out.
            foreach (var node in alwaysRealized)
            {
                _candidates.Add(node);
            }

            SyncRealizedFlags(detail);
            return new ViewAllRenderSet(_candidates.ToArray(), [], detail, logicalNodeCount);
        }

        var overscan = _options.OverscanPixels / zoom;
        var cullingBounds = viewportInGraphCoordinates;
        cullingBounds.Inflate(overscan, overscan);
        index.Query(cullingBounds, _candidates);

        _candidates.RemoveAll(node => !node.IsTreeVisible);

        if (alwaysRealized.Count > 0)
        {
            var present = new HashSet<ViewAllNodeViewModel>(_candidates);
            foreach (var node in alwaysRealized)
            {
                if (present.Add(node))
                {
                    _candidates.Add(node);
                }
            }
        }

        var limit = detail == ViewAllDetailLevel.Detailed
            ? _options.MaximumDetailedNodes
            : _options.MaximumCompactNodes;

        if (_candidates.Count > limit)
        {
            var center = new Point(
                viewportInGraphCoordinates.Left + viewportInGraphCoordinates.Width / 2,
                viewportInGraphCoordinates.Top + viewportInGraphCoordinates.Height / 2);
            _candidates.Sort((left, right) => DistanceSquared(left.Bounds, center)
                .CompareTo(DistanceSquared(right.Bounds, center)));
            _candidates.RemoveRange(limit, _candidates.Count - limit);
        }

        SyncRealizedFlags(detail);

        var realizedNodes = _candidates.ToArray();
        var realizedSet = new HashSet<ViewAllNodeViewModel>(realizedNodes);
        var realizedEdges = new List<ViewAllEdgeViewModel>(Math.Min(edges.Count, realizedNodes.Length));
        if (incomingEdges is not null)
        {
            foreach (var node in realizedNodes)
            {
                if (incomingEdges.TryGetValue(node.Id, out var edge)
                    && edge.IsTreeVisible && realizedSet.Contains(edge.Source))
                {
                    realizedEdges.Add(edge);
                }
            }

            realizedEdges.Sort(static (left, right) => left.PaintOrder.CompareTo(right.PaintOrder));
        }
        else
        {
            // Compatibility for callers supplying a standalone edge list.
            foreach (var edge in edges)
            {
                if (edge.IsTreeVisible && realizedSet.Contains(edge.Source) && realizedSet.Contains(edge.Target))
                {
                    realizedEdges.Add(edge);
                }
            }
        }

        return new ViewAllRenderSet(realizedNodes, realizedEdges, detail, logicalNodeCount);
    }

    /// <summary>
    /// Only the nodes that entered or left the realized set are touched, so a
    /// pan does not raise a property change for every node in the graph.
    /// </summary>
    private void SyncRealizedFlags(ViewAllDetailLevel detail)
    {
        var current = new HashSet<ViewAllNodeViewModel>(_candidates);

        foreach (var node in _realized)
        {
            if (!current.Contains(node))
            {
                node.IsViewportRealized = false;
            }
        }

        foreach (var node in _candidates)
        {
            node.DetailLevel = detail;
            node.IsViewportRealized = true;
        }

        _realized.Clear();
        _realized.UnionWith(current);
    }

    private static double DistanceSquared(Rect bounds, Point point)
    {
        var dx = bounds.Left + bounds.Width / 2 - point.X;
        var dy = bounds.Top + bounds.Height / 2 - point.Y;
        return dx * dx + dy * dy;
    }
}
