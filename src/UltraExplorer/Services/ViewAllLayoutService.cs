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
    double CollisionPaddingY = 22,

    /// <summary>Vertical gap between two rows of one parent's children.</summary>
    double RowGap = 34,

    /// <summary>
    /// A folder small enough to read at a glance stays on one line.  Six nodes
    /// is about 1200 units, which fits a viewport; a dozen does not, and that is
    /// where a row stops reading as a row.
    /// </summary>
    int SingleRowLimit = 6,

    /// <summary>How many times wider than tall a block of children should be.</summary>
    double BlockAspect = 2.2,

    int MaximumColumns = 512,

    /// <summary>
    /// Width of the empty lane reserved down each side of a multi-row block: one
    /// for the trunk the links hang from, one for a line leaving the block.
    /// </summary>
    double TrunkLane = 26);

/// <summary>
/// Top-down tree: a parent sits above a block of its children.  Screens are
/// wider than they are tall, so growing downwards and spreading sideways fills
/// the viewport far better than a left-to-right tree.
///
/// The children of one folder wrap into a block rather than a single row.  A row
/// grows linearly with the number of children, so a folder with five thousand
/// entries was a line half a million units long - unreadable, and impossible to
/// frame.  A block grows with the square root instead: both sides of the same
/// folder come out around sixty by eighty.
///
/// Nodes shrink with depth (see <see cref="ViewAllNodeViewModel.Scale"/>), and
/// every gap, step and padding below is multiplied by that same scale, so a deep
/// branch stays compact instead of taking as much room as a root.
///
/// The layout is incremental on purpose: expanding a branch positions only the
/// newly created nodes, so a node the user dragged stays where it was put and the
/// graph never jumps.  The common case reserves the whole block with a single
/// occupancy query; only when something is already parked in the way does each
/// child pay for its own search.
/// </summary>
public sealed class ViewAllLayoutService(ViewAllLayoutOptions? options = null)
{
    /// <summary>Sideways probes before a search gives up on a row and drops down.</summary>
    private const int LanesProbed = 9;

    private const int MaximumProbes = 20_000;

    /// <summary>Rows a block will descend past an obstruction before giving up.</summary>
    private const int DescentsProbed = 256;

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
            var stepX = root.Width + _options.SiblingGap * root.Scale;
            var stepY = root.Height + _options.RowGap * root.Scale;
            root.SetAutomaticLocation(FindFreeSlot(preferred, root, index, stepX, stepY));
            slot++;
        }
    }

    public void PlaceChildren(
        ViewAllNodeViewModel parent,
        IEnumerable<ViewAllNodeViewModel> children,
        ViewAllSpatialIndex index)
    {
        var unplaced = children
            .Where(child => !child.HasLayoutPosition && !child.IsUserHidden)
            .OrderBy(child => child.Kind)
            .ThenBy(child => child.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (unplaced.Length == 0)
        {
            return;
        }

        // One parent means one depth, so one scale for every step and gap.
        var count = unplaced.Length;
        var scale = unplaced[0].Scale;
        var gapX = _options.SiblingGap * scale;
        var gapY = _options.RowGap * scale;
        var stepX = ViewAllNodeViewModel.DefaultWidth * scale + gapX;
        var stepY = ViewAllNodeViewModel.DefaultHeight * scale + gapY;

        var columns = ColumnsFor(count, stepX, stepY);
        var rows = (count + columns - 1) / columns;
        var nodesWidth = columns * stepX - gapX;
        var blockHeight = rows * stepY - gapY;

        // A multi-row block reserves an empty lane down each side: the left one
        // carries the trunk every link hangs from, the right one is the way out
        // for a line leaving the block.  A single row needs neither - its links
        // already fan straight down from the parent, and that reads fine.
        var lane = rows > 1 ? _options.TrunkLane * scale : 0;

        var origin = new Point(
            parent.Location.X + parent.Width / 2 - nodesWidth / 2 - lane,
            parent.Location.Y + parent.Height + _options.GenerationGap * scale - gapY / 2);

        var padX = _options.CollisionPaddingX * scale;
        var padY = _options.CollisionPaddingY * scale;
        var reservedWidth = nodesWidth + 2 * lane;
        var reservedHeight = blockHeight + gapY * 1.5;

        // The whole block at once, dropped straight down past anything in the
        // way.  Keeping the family together is the point: scattering the children
        // of one folder around a neighbouring branch is what made the canvas
        // unreadable, and the tree grows downwards anyway.
        if (TryReserveBlock(origin, reservedWidth, reservedHeight, padX, padY, stepX, stepY, index, out var placed))
        {
            var nodeOrigin = new Point(placed.X + lane, placed.Y + gapY / 2);
            for (var position = 0; position < count; position++)
            {
                unplaced[position].SetAutomaticLocation(
                    Slot(position, nodeOrigin, columns, count, nodesWidth, stepX, stepY, gapX));
            }

            parent.ChildBlocks.Add(new ViewAllChildBlock(
                placed - parent.Location,
                columns,
                rows,
                count,
                stepX,
                stepY,
                gapX,
                gapY,
                lane,
                nodesWidth,
                scale));
            return;
        }

        // Nowhere within reach holds the whole block, so each child claims its
        // own slot from where it would have been.  No lattice is recorded: a
        // comb drawn over children that are no longer on it would be a lie.
        var fallbackOrigin = new Point(origin.X + lane, origin.Y + gapY / 2);
        for (var position = 0; position < count; position++)
        {
            var preferred = Slot(position, fallbackOrigin, columns, count, nodesWidth, stepX, stepY, gapX);
            unplaced[position].SetAutomaticLocation(
                FindFreeSlot(preferred, unplaced[position], index, stepX, stepY));
        }
    }

    /// <summary>
    /// Looks for somewhere the whole block fits, starting under the parent and
    /// descending a row at a time.  One occupancy query per attempt, and the
    /// first attempt succeeds whenever the space below the parent is clear.
    /// </summary>
    private static bool TryReserveBlock(
        Point preferred,
        double blockWidth,
        double blockHeight,
        double padX,
        double padY,
        double stepX,
        double stepY,
        ViewAllSpatialIndex index,
        out Point origin)
    {
        origin = preferred;
        for (var descent = 0; descent < DescentsProbed; descent++)
        {
            // Sideways first, a column at a time and alternating: one node parked
            // in the way should cost a nudge, not a whole floor.  Only when no
            // nearby column fits does the block drop to the next row.
            for (var lane = 0; lane < LanesProbed; lane++)
            {
                var ring = (lane + 1) / 2;
                var candidate = new Point(
                    preferred.X + (lane % 2 == 1 ? ring : -ring) * stepX,
                    preferred.Y + descent * stepY);

                var area = new Rect(candidate, new Size(blockWidth, blockHeight));
                area.Inflate(padX, padY);
                if (!index.IsOccupied(area))
                {
                    origin = candidate;
                    return true;
                }
            }
        }

        return false;
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
            node.ChildBlocks.Clear();
        }
    }

    /// <summary>
    /// Columns that make the block about <see cref="ViewAllLayoutOptions.BlockAspect"/>
    /// times wider than it is tall.  Both sides then grow with the square root of
    /// the child count: five thousand children are 64 x 79, never 5000 x 1 and
    /// never 1 x 5000.  A folder small enough to read at a glance is left on one
    /// row, so opening a handful of files does not look like a grid.
    /// </summary>
    private int ColumnsFor(int count, double stepX, double stepY)
    {
        if (count <= _options.SingleRowLimit)
        {
            return count;
        }

        var ideal = (int)Math.Ceiling(Math.Sqrt(_options.BlockAspect * count * stepY / stepX));
        return Math.Clamp(ideal, 1, Math.Min(count, _options.MaximumColumns));
    }

    /// <summary>
    /// Where one child sits on the block's lattice.  Full rows start at the
    /// block's left edge; only the short last row is centred inside it, which
    /// keeps the block symmetric under its parent instead of ragged.
    /// </summary>
    private static Point Slot(
        int position,
        Point origin,
        int columns,
        int count,
        double blockWidth,
        double stepX,
        double stepY,
        double gapX)
    {
        var row = position / columns;
        var column = position % columns;
        var inRow = Math.Min(columns, count - row * columns);
        var indent = (blockWidth - (inRow * stepX - gapX)) / 2;

        return new Point(origin.X + indent + column * stepX, origin.Y + row * stepY);
    }

    /// <summary>
    /// The nearest free lattice slot to <paramref name="preferred"/>: sideways
    /// first, alternating right and left so a row stays centred on its parent,
    /// and only then down a row - which is the direction the tree grows anyway.
    /// </summary>
    private Point FindFreeSlot(
        Point preferred,
        ViewAllNodeViewModel node,
        ViewAllSpatialIndex index,
        double stepX,
        double stepY)
    {
        var size = new Size(node.Width, node.Height);
        var padX = _options.CollisionPaddingX * node.Scale;
        var padY = _options.CollisionPaddingY * node.Scale;

        var candidate = preferred;
        for (var attempt = 0; attempt < MaximumProbes; attempt++)
        {
            var lane = attempt / LanesProbed;
            var slot = attempt % LanesProbed;
            var ring = (slot + 1) / 2;
            candidate = new Point(
                preferred.X + (slot % 2 == 1 ? ring : -ring) * stepX,
                preferred.Y + lane * stepY);

            var bounds = new Rect(candidate, size);
            bounds.Inflate(padX, padY);
            if (!index.IsOccupied(bounds))
            {
                return candidate;
            }
        }

        return candidate;
    }
}
