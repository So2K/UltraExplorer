using System.Windows;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

public sealed record ViewAllLayoutOptions(
    double OriginX = 80,
    double OriginY = 80,
    double HorizontalGap = 112,
    double VerticalGap = 18,
    double RootGap = 280,
    double CollisionPaddingX = 30,
    double CollisionPaddingY = 16);

/// <summary>
/// Stable incremental layout: expanding a branch positions only newly created
/// nodes. Existing automatic positions and every user-dragged position remain
/// untouched, avoiding the usual graph "jump" on expansion.
/// </summary>
public sealed class ViewAllLayoutService(ViewAllLayoutOptions? options = null)
{
    private readonly ViewAllLayoutOptions _options = options ?? new ViewAllLayoutOptions();

    public void PlaceRoots(
        IEnumerable<ViewAllNodeViewModel> roots,
        IEnumerable<ViewAllNodeViewModel> allNodes)
    {
        var occupied = allNodes.Where(node => node.HasLayoutPosition).Select(node => node.Bounds).ToList();
        var index = 0;
        foreach (var root in roots.OrderBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            if (root.HasLayoutPosition)
            {
                index++;
                continue;
            }

            var preferred = new Point(_options.OriginX, _options.OriginY + index * _options.RootGap);
            var location = FindFreeVerticalSlot(preferred, occupied);
            root.SetAutomaticLocation(location);
            occupied.Add(root.Bounds);
            index++;
        }
    }

    public void PlaceChildren(
        ViewAllNodeViewModel parent,
        IEnumerable<ViewAllNodeViewModel> children,
        IEnumerable<ViewAllNodeViewModel> allNodes)
    {
        var unplaced = children
            .Where(child => !child.HasLayoutPosition)
            .OrderBy(child => child.Kind)
            .ThenBy(child => child.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (unplaced.Length == 0)
        {
            return;
        }

        var occupied = allNodes
            .Where(node => node.HasLayoutPosition && !unplaced.Contains(node))
            .Select(node => node.Bounds)
            .ToList();
        var step = ViewAllNodeViewModel.DefaultHeight + _options.VerticalGap;
        var branchHeight = (unplaced.Length - 1) * step;
        var startY = parent.Location.Y - branchHeight / 2;
        var childX = parent.Location.X + ViewAllNodeViewModel.DefaultWidth + _options.HorizontalGap;

        for (var index = 0; index < unplaced.Length; index++)
        {
            var preferred = new Point(childX, startY + index * step);
            var location = FindFreeVerticalSlot(preferred, occupied);
            unplaced[index].SetAutomaticLocation(location);
            occupied.Add(unplaced[index].Bounds);
        }
    }

    /// <summary>
    /// Clears only automatic locations. Manual nodes remain anchors; the graph
    /// service can then call PlaceRoots/PlaceChildren in depth order.
    /// </summary>
    public void ReleaseAutomaticLayout(IEnumerable<ViewAllNodeViewModel> nodes)
    {
        foreach (var node in nodes.Where(node => !node.HasManualPosition))
        {
            node.ReleaseAutomaticLocation();
        }
    }

    private Point FindFreeVerticalSlot(Point preferred, IReadOnlyList<Rect> occupied)
    {
        var candidate = preferred;
        var step = ViewAllNodeViewModel.DefaultHeight + _options.VerticalGap;
        for (var attempt = 0; attempt < 20_000; attempt++)
        {
            var bounds = new Rect(candidate, new Size(ViewAllNodeViewModel.DefaultWidth, ViewAllNodeViewModel.DefaultHeight));
            bounds.Inflate(_options.CollisionPaddingX, _options.CollisionPaddingY);
            if (occupied.All(other => !other.IntersectsWith(bounds)))
            {
                return candidate;
            }

            // Alternate above and below the preferred point before drifting down.
            var ring = attempt / 2 + 1;
            candidate.Y = preferred.Y + (attempt % 2 == 0 ? ring : -ring) * step;
        }

        return candidate;
    }
}
