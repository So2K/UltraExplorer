using System.Windows;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

public sealed record ViewAllViewportOptions(
    double OverscanPixels = 280,
    int MaximumDetailedNodes = 1_200,
    int MaximumCompactNodes = 2_500,
    int MaximumDotNodes = 6_000);

/// <summary>
/// Produces the small ItemsSource that the canvas should actually realize.
/// This is collection-level culling, not merely Visibility=Collapsed.
/// </summary>
public sealed class ViewAllViewportService(ViewAllViewportOptions? options = null)
{
    private readonly ViewAllViewportOptions _options = options ?? new ViewAllViewportOptions();

    public ViewAllDetailLevel GetDetailLevel(double zoom) => zoom switch
    {
        // Below ~15% a 188 x 44 node is under 30 px wide, so text and icons stop
        // carrying information and only the coloured slab is worth drawing.
        < 0.15 => ViewAllDetailLevel.Dot,
        < 0.30 => ViewAllDetailLevel.Glyph,
        < 0.62 => ViewAllDetailLevel.Compact,
        _ => ViewAllDetailLevel.Detailed
    };

    public ViewAllRenderSet BuildRenderSet(
        IEnumerable<ViewAllNodeViewModel> nodes,
        IEnumerable<ViewAllEdgeViewModel> edges,
        Rect viewportInGraphCoordinates,
        double zoom)
    {
        zoom = Math.Clamp(zoom, 0.03, 8);
        var detail = GetDetailLevel(zoom);
        var overscan = _options.OverscanPixels / zoom;
        var cullingBounds = viewportInGraphCoordinates;
        cullingBounds.Inflate(overscan, overscan);
        var center = new Point(viewportInGraphCoordinates.Left + viewportInGraphCoordinates.Width / 2,
            viewportInGraphCoordinates.Top + viewportInGraphCoordinates.Height / 2);

        var allNodes = nodes.ToArray();
        var logicalNodes = allNodes.Where(node => node.IsTreeVisible).ToArray();
        var candidates = logicalNodes
            .Where(node => node.IsSelected || node.Bounds.IntersectsWith(cullingBounds))
            .OrderByDescending(node => node.IsSelected)
            .ThenBy(node => DistanceSquared(node.Bounds, center))
            .Take(GetNodeLimit(detail))
            .ToArray();
        var realizedIds = candidates.Select(node => node.Id).ToHashSet();

        foreach (var node in allNodes)
        {
            node.IsViewportRealized = realizedIds.Contains(node.Id);
            if (node.IsViewportRealized)
            {
                node.DetailLevel = detail;
            }
        }

        var realizedEdges = edges
            .Where(edge => edge.IsTreeVisible
                && realizedIds.Contains(edge.Source.Id)
                && realizedIds.Contains(edge.Target.Id))
            .ToArray();

        return new ViewAllRenderSet(candidates, realizedEdges, detail, logicalNodes.Length);
    }

    private int GetNodeLimit(ViewAllDetailLevel detail) => detail switch
    {
        ViewAllDetailLevel.Dot => _options.MaximumDotNodes,
        ViewAllDetailLevel.Glyph => _options.MaximumCompactNodes,
        ViewAllDetailLevel.Compact => _options.MaximumCompactNodes,
        _ => _options.MaximumDetailedNodes
    };

    private static double DistanceSquared(Rect bounds, Point point)
    {
        var dx = bounds.Left + bounds.Width / 2 - point.X;
        var dy = bounds.Top + bounds.Height / 2 - point.Y;
        return dx * dx + dy * dy;
    }
}
