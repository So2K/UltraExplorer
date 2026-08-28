using System.Windows;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

public sealed record ViewAllLayoutOptions(
    double OriginX = 120,
    double OriginY = 90,
    double SiblingGap = 26,
    double GenerationGap = 96,
    double RootGap = 420,
    double CollisionPaddingX = 14,
    double CollisionPaddingY = 22);

/// <summary>
/// Top-down tree: a parent sits above a row of its children.  Screens are wider
/// than they are tall, so growing downwards and spreading sideways fills the
/// viewport far better than a left-to-right tree.
///
/// Nodes shrink with depth (see <see cref="ViewAllNodeViewModel.Scale"/>), and
/// the gaps shrink with them, so a deep branch stays compact instead of taking
/// as much room as a root.
///
/// The layout is incremental on purpose: expanding a branch positions only the
/// newly created nodes, so existing automatic positions and every user-dragged
/// position stay where they are and the graph never jumps.  Collision tests go
/// through the spatial index, which keeps placing a folder with thousands of
/// children linear instead of quadratic.
/// </summary>
public sealed class ViewAllLayoutService(ViewAllLayoutOptions? options = null)
{
    private readonly ViewAllLayoutOptions _options = options ?? new ViewAllLayoutOptions();

    public void PlaceRoots(IEnumerable<ViewAllNodeViewModel> roots, ViewAllSpatialIndex index)
    {
        var slot = 0;
        foreach (var root in roots.OrderBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            if (root.HasLayoutPosition)
            {
                slot++;
                continue;
            }

            var preferred = new Point(_options.OriginX + slot * _options.RootGap, _options.OriginY);
            root.SetAutomaticLocation(FindFreeHorizontalSlot(preferred, root, index));
            slot++;
        }
    }

    public void PlaceChildren(
        ViewAllNodeViewModel parent,
        IEnumerable<ViewAllNodeViewModel> children,
        ViewAllSpatialIndex index)
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

        var childScale = unplaced[0].Scale;
        var childWidth = ViewAllNodeViewModel.DefaultWidth * childScale;
        var step = childWidth + _options.SiblingGap * childScale;
        var rowWidth = (unplaced.Length - 1) * step;

        var parentCentre = parent.Location.X + parent.Width / 2;
        var startX = parentCentre - childWidth / 2 - rowWidth / 2;
        var childY = parent.Location.Y + parent.Height + _options.GenerationGap * childScale;

        for (var position = 0; position < unplaced.Length; position++)
        {
            var preferred = new Point(startX + position * step, childY);
            unplaced[position].SetAutomaticLocation(FindFreeHorizontalSlot(preferred, unplaced[position], index));
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

    private Point FindFreeHorizontalSlot(Point preferred, ViewAllNodeViewModel node, ViewAllSpatialIndex index)
    {
        var size = new Size(node.Width, node.Height);
        var step = node.Width + _options.SiblingGap * node.Scale;
        var padX = _options.CollisionPaddingX * node.Scale;
        var padY = _options.CollisionPaddingY * node.Scale;

        var candidate = preferred;
        for (var attempt = 0; attempt < 20_000; attempt++)
        {
            var bounds = new Rect(candidate, size);
            bounds.Inflate(padX, padY);
            if (!index.IsOccupied(bounds))
            {
                return candidate;
            }

            // Alternate right and left of the preferred point so a row stays
            // centred on its parent instead of drifting in one direction.
            var ring = attempt / 2 + 1;
            candidate.X = preferred.X + (attempt % 2 == 0 ? ring : -ring) * step;
        }

        return candidate;
    }
}
