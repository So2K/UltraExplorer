using System.Windows;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

public sealed record ViewAllLayoutOptions(
    double OriginX = 120,
    double OriginY = 90,
    double SiblingGap = 26,
    double GenerationGap = 96,

    /// <summary>Clear space left between two root trees.</summary>
    double RootGap = 420,

    /// <summary>Vertical gap between two rows of one folder's children.</summary>
    double RowGap = 34,

    /// <summary>
    /// A folder with no more children than this keeps them on one line.  Four is
    /// about 830 units - still a glance.  Six was 1250, a third of a screen, and
    /// that is where a row stops reading as a row and starts reading as a queue.
    /// </summary>
    int SingleRowLimit = 4,

    /// <summary>How many times wider than tall a block of children should be.</summary>
    double BlockAspect = 2.2,

    int MaximumColumns = 512,

    /// <summary>
    /// Width of the empty lane reserved down each side of a multi-row block: one
    /// for the trunk the links hang from, one for a line leaving the block.
    /// </summary>
    double TrunkLane = 26);

/// <summary>
/// A tidy top-down tree: every folder owns a rectangle, and nothing that is not
/// inside that folder may enter it.
///
/// The rectangle - the folder's <i>extent</i> - is measured bottom-up.  A
/// collapsed folder is as big as its own node.  An open one is its node stacked
/// above the block its children occupy, and each of those children is measured
/// the same way first.  Arranging then walks back down, handing each child the
/// corner of the space it was measured for.
///
/// Everything that made the canvas unreadable with several folders open follows
/// from that one property, and follows by construction rather than by luck:
///
/// <list type="bullet">
/// <item>a child's whole subtree sits inside its parent's block, so frames nest
/// instead of crossing;</item>
/// <item>two sibling subtrees are disjoint rectangles, so no branch interleaves
/// with another;</item>
/// <item>every line a folder draws stays inside that folder's own rectangle, so
/// no link crosses a folder it has nothing to do with;</item>
/// <item>roots are spaced by the real width of their trees rather than by a
/// constant, so two drives cannot land on top of each other.</item>
/// </list>
///
/// The children of one folder wrap into a block rather than a single row.  A row
/// grows linearly with the child count, so a folder with five thousand entries
/// was a line half a million units long; a block grows with the square root, and
/// both sides come out around sixty by eighty.  A child that is itself open is
/// wider than a row can hold, so it takes a row of its own - which is exactly how
/// an outline reads: the open folder in place, its contents beneath it.
///
/// Nodes shrink with depth (see <see cref="ViewAllNodeViewModel.Scale"/>), and
/// every gap and step below is multiplied by that same scale, so a deep branch
/// stays compact instead of taking as much room as a root.
///
/// The pass is deterministic: the same tree always produces the same picture.
/// That is what lets a refresh, a reload from disk or a second run leave the
/// canvas exactly where it was, without saving a single coordinate.
/// </summary>
public sealed class ViewAllLayoutService(ViewAllLayoutOptions? options = null)
{
    private readonly ViewAllLayoutOptions _options = options ?? new ViewAllLayoutOptions();
    private readonly Dictionary<ViewAllNodeViewModel, Extent> _extents = new(ReferenceComparer.Instance);
    private readonly List<Rect> _pinnedIslands = [];

    /// <summary>The space one node's whole subtree needs, and how it is divided.</summary>
    private sealed class Extent
    {
        public double Width;
        public double Height;

        /// <summary>Widest row of children; zero when the node holds no block.</summary>
        public double NodesWidth;

        public double Lane;

        /// <summary>Where the node sits inside its extent, from the extent's left.</summary>
        public double NodeOffsetX;

        /// <summary>Where the reserved rectangle starts, from the extent's left.</summary>
        public double BlockOffsetX;

        public double GapX;
        public double GapY;
        public double GenerationGap;
        public double Scale;
        public int Count;
        public List<Row>? Rows;
    }

    private sealed class Row
    {
        public readonly List<ViewAllNodeViewModel> Children = [];
        public double Width;
        public double Height;
    }

    /// <summary>
    /// Lays out every root and everything open beneath it.  This is the only
    /// entry point: a tidy tree is a property of the whole tree, so there is no
    /// such thing as laying out one branch and leaving the rest alone.
    /// </summary>
    public void Arrange(IReadOnlyList<ViewAllNodeViewModel> roots)
    {
        _extents.Clear();
        _pinnedIslands.Clear();

        var visible = roots
            .Where(root => !root.IsUserHidden)
            .OrderBy(root => root.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var root in visible)
        {
            Measure(root);
        }

        // A root the user dragged keeps its place and its tree flows below it;
        // the rest are laid out along the top, stepping over anything pinned so
        // an automatic tree never lands on a hand-placed one.
        foreach (var root in visible.Where(root => root.HasManualPosition))
        {
            ArrangePinned(root);
            _pinnedIslands.Add(IslandOf(root));
        }

        var x = _options.OriginX;
        foreach (var root in visible.Where(root => !root.HasManualPosition))
        {
            var extent = _extents[root];
            x = ClearOfPinned(x, extent);
            ArrangeExtent(root, new Point(x, _options.OriginY));
            x += extent.Width + _options.RootGap;
        }
    }

    /// <summary>
    /// Where one node's whole subtree sits, once it has been arranged.  Used to
    /// keep automatic roots clear of hand-placed ones.
    /// </summary>
    private Rect IslandOf(ViewAllNodeViewModel node)
    {
        var extent = _extents[node];
        return new Rect(
            new Point(node.Location.X - (extent.Width - node.Width) / 2, node.Location.Y),
            new Size(extent.Width, extent.Height));
    }

    private double ClearOfPinned(double x, Extent extent)
    {
        // Root trees all start at the same line, so one sweep is enough: push
        // right past every pinned island the candidate would run into.
        for (var attempt = 0; attempt < _pinnedIslands.Count + 1; attempt++)
        {
            var candidate = new Rect(
                new Point(x, _options.OriginY),
                new Size(extent.Width, extent.Height));

            var blocked = false;
            foreach (var island in _pinnedIslands)
            {
                if (island.IntersectsWith(candidate))
                {
                    x = island.Right + _options.RootGap;
                    blocked = true;
                    break;
                }
            }

            if (!blocked)
            {
                break;
            }
        }

        return x;
    }

    /// <summary>
    /// Drops every automatic position so the next pass places the node afresh.
    /// A position the user chose is left alone: that one was a decision.
    /// </summary>
    public void ReleaseAutomaticLayout(IEnumerable<ViewAllNodeViewModel> nodes)
    {
        foreach (var node in nodes.Where(node => !node.HasManualPosition))
        {
            node.ReleaseAutomaticLocation();
            node.ChildBlock = null;
        }
    }

    // ---- measuring ---------------------------------------------------------

    private void Measure(ViewAllNodeViewModel node)
    {
        var extent = new Extent
        {
            Width = node.Width,
            Height = node.Height,
            Scale = node.Scale
        };
        _extents[node] = extent;

        if (!node.IsExpanded)
        {
            return;
        }

        var children = VisibleChildren(node);
        if (children.Count == 0)
        {
            return;
        }

        foreach (var child in children)
        {
            Measure(child);
        }

        // A pinned child has left the flow: its slot is not reserved and its own
        // tree is arranged where the user put it, as an island.
        var flow = children.Where(child => !child.HasManualPosition).ToList();
        if (flow.Count == 0)
        {
            return;
        }

        // One parent means one depth, so one scale for every step and gap.
        var scale = flow[0].Scale;
        extent.GapX = _options.SiblingGap * scale;
        extent.GapY = _options.RowGap * scale;
        extent.GenerationGap = _options.GenerationGap * scale;

        // Closed children are packed on their own, and the open ones after them.
        // Mixed rows are as tall as the subtree in them, so one closed folder
        // sharing a row with an open one wasted a screen of blank space below
        // itself; and a folder reads better as its own contents first, then the
        // branches that were opened out of it.
        var closed = flow.Where(child => _extents[child].Rows is null).ToList();
        var open = flow.Where(child => _extents[child].Rows is not null).ToList();

        var rows = new List<Row>();
        if (flow.Count <= _options.SingleRowLimit)
        {
            Pack(rows, flow, double.MaxValue, extent);
        }
        else
        {
            Pack(rows, closed, TargetWidth(closed, extent), extent);
            Pack(rows, open, TargetWidth(open, extent), extent);
        }

        extent.Rows = rows;
        extent.Count = flow.Count;
        extent.NodesWidth = rows.Max(item => item.Width);
        extent.Lane = rows.Count > 1 ? _options.TrunkLane * scale : 0;

        var blockHeight = rows.Sum(item => item.Height) + extent.GapY * (rows.Count - 1);
        var blockWidth = extent.NodesWidth + 2 * extent.Lane;
        extent.Width = Math.Max(node.Width, blockWidth);
        extent.BlockOffsetX = (extent.Width - blockWidth) / 2;

        // The folder hangs over its first row rather than over the whole block.
        // Rows are left-aligned, so with a compact grid of closed children above
        // a much wider open branch the two would otherwise drift apart: the link
        // down from the folder has to land on the first row's run, and it can
        // only do that if the folder is above that row.
        extent.NodeOffsetX = Math.Clamp(
            extent.BlockOffsetX + extent.Lane + rows[0].Width / 2 - node.Width / 2,
            0,
            Math.Max(0, extent.Width - node.Width));

        // Half a gap of clear air below the last row, matching the half above the
        // first, so the reserved rectangle is the frame and two siblings never
        // touch.
        extent.Height = node.Height + extent.GenerationGap + blockHeight + extent.GapY / 2;
    }

    /// <summary>
    /// Fills rows left to right, wrapping when the next child would take the row
    /// past <paramref name="target"/>.  The first child of a row always goes on
    /// however wide it is, so a child that is itself open - wider than any target
    /// - takes a row of its own instead of being dropped.
    /// </summary>
    private void Pack(
        List<Row> rows,
        List<ViewAllNodeViewModel> children,
        double target,
        Extent extent)
    {
        if (children.Count == 0)
        {
            return;
        }

        var row = new Row();
        foreach (var child in children)
        {
            var childExtent = _extents[child];

            // Half a unit of slack: the target is a whole number of steps and the
            // row is the same arithmetic accumulated one child at a time, so
            // rounding must not cost a full row its last column.
            if (row.Children.Count > 0 && row.Width + extent.GapX + childExtent.Width > target + 0.5)
            {
                rows.Add(row);
                row = new Row();
            }

            row.Width += (row.Children.Count > 0 ? extent.GapX : 0) + childExtent.Width;
            row.Height = Math.Max(row.Height, childExtent.Height);
            row.Children.Add(child);
        }

        rows.Add(row);
    }

    /// <summary>
    /// How wide to let a row grow before it wraps, so the block comes out about
    /// <see cref="ViewAllLayoutOptions.BlockAspect"/> times wider than it is
    /// tall.  It is the square root of the area the children need, which for a
    /// folder of same-sized entries is exactly a column count: both sides then
    /// grow with the square root of the entry count, so five thousand files are
    /// 64 x 79 and never 5000 x 1.
    ///
    /// Measuring area rather than counting children is what keeps a folder
    /// readable once some of its children are open.  An open child is a whole
    /// subtree - a hundred times the area of a collapsed one - and a target
    /// derived from the count alone would put one per row and turn the folder
    /// into a column thousands of units tall.
    /// </summary>
    private double TargetWidth(List<ViewAllNodeViewModel> children, Extent extent)
    {
        if (children.Count == 0)
        {
            return 0;
        }

        var area = 0.0;
        var line = 0.0;
        var uniform = true;
        var first = _extents[children[0]];

        foreach (var child in children)
        {
            var childExtent = _extents[child];
            area += (childExtent.Width + extent.GapX) * (childExtent.Height + extent.GapY);
            line += childExtent.Width + extent.GapX;
            uniform &= Math.Abs(childExtent.Width - first.Width) < 0.001
                && Math.Abs(childExtent.Height - first.Height) < 0.001;
        }

        // A handful of items reads better as a line than as a grid.
        if (children.Count <= _options.SingleRowLimit)
        {
            return line;
        }

        var target = Math.Sqrt(_options.BlockAspect * area);

        // Closed children are all exactly the same size, and for those the target
        // has to be a whole number of columns: rounded down, eighteen entries go
        // into three columns of six rather than the four of five the aspect asked
        // for, and the block comes out taller than it is wide.
        if (uniform)
        {
            var step = first.Width + extent.GapX;
            var columns = Math.Max(1, (int)Math.Ceiling((target + extent.GapX) / step - 0.001));
            target = columns * step - extent.GapX;
        }

        return Math.Min(line, target);
    }

    private static List<ViewAllNodeViewModel> VisibleChildren(ViewAllNodeViewModel node) =>
        node.Children
            .Where(child => child is { IsUserHidden: false, IsTreeVisible: true })
            .OrderBy(child => child.Kind)
            .ThenBy(child => child.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    // ---- arranging ---------------------------------------------------------

    /// <summary>Places a node in the space it was measured for.</summary>
    private void ArrangeExtent(ViewAllNodeViewModel node, Point extentTopLeft)
    {
        var extent = _extents[node];
        node.SetAutomaticLocation(new Point(
            extentTopLeft.X + extent.NodeOffsetX,
            extentTopLeft.Y));
        ArrangeBlock(node, extent, extentTopLeft.X);
    }

    /// <summary>Arranges the tree of a node whose own position the user chose.</summary>
    private void ArrangePinned(ViewAllNodeViewModel node)
    {
        var extent = _extents[node];
        ArrangeBlock(node, extent, node.Location.X - extent.NodeOffsetX);
    }

    private void ArrangeBlock(ViewAllNodeViewModel node, Extent extent, double extentLeft)
    {
        node.ChildBlock = null;

        foreach (var pinned in node.Children.Where(child =>
                     child is { IsUserHidden: false, IsTreeVisible: true, HasManualPosition: true }))
        {
            ArrangePinned(pinned);
        }

        if (extent.Rows is not { Count: > 0 })
        {
            return;
        }

        var blockWidth = extent.NodesWidth + 2 * extent.Lane;
        var blockLeft = extentLeft + extent.BlockOffsetX;
        var nodesLeft = blockLeft + extent.Lane;
        var top = node.Location.Y + node.Height + extent.GenerationGap;

        var y = top;
        foreach (var row in extent.Rows)
        {
            // Every row starts at the same left edge, hard against the lane the
            // trunk runs down.  Centring them looked tidier for a plain grid but
            // left a narrow row stranded in the middle of a wide block, with its
            // run crossing the empty half to reach it; and columns that line up
            // read better anyway.
            var cursor = nodesLeft;
            foreach (var child in row.Children)
            {
                ArrangeExtent(child, new Point(cursor, y));
                cursor += _extents[child].Width + extent.GapX;
            }

            y += row.Height + extent.GapY;
        }

        var blockHeight = y - extent.GapY - top;
        node.ChildBlock = new ViewAllChildBlock(
            new Vector(blockLeft - node.Location.X, top - extent.GapY / 2 - node.Location.Y),
            blockWidth,
            blockHeight + extent.GapY,
            extent.NodesWidth,
            extent.Lane,
            extent.GapY,
            extent.Rows.Count,
            extent.Count,
            extent.Scale);
    }

    /// <summary>
    /// Nodes are compared by identity here, never by value: two different nodes
    /// must never share a measurement.
    /// </summary>
    private sealed class ReferenceComparer : IEqualityComparer<ViewAllNodeViewModel>
    {
        public static readonly ReferenceComparer Instance = new();

        public bool Equals(ViewAllNodeViewModel? left, ViewAllNodeViewModel? right) =>
            ReferenceEquals(left, right);

        public int GetHashCode(ViewAllNodeViewModel node) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(node);
    }
}
