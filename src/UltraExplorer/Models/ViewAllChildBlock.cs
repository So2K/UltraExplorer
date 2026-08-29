using System.Windows;

namespace UltraExplorer.Models;

/// <summary>
/// The rectangle one folder's children were laid out in, recorded when they were
/// placed.
///
/// It is stored as an offset from the folder rather than as absolute points, so
/// dragging a folder carries its whole harness along and no bookkeeping is
/// needed.  Rows are deliberately <i>not</i> recorded: a row is simply the
/// children that share a top edge, and reading it back off the children keeps
/// the record honest when one of them has been dragged out of it, and lets rows
/// be different heights - which they are as soon as one child is open.
/// </summary>
/// <param name="Offset">Top-left of the reserved rectangle, relative to the folder.</param>
/// <param name="Width">Reserved width, both lanes included.</param>
/// <param name="Height">Reserved height, both half-gap bands included.</param>
/// <param name="NodesWidth">Width of the widest row of children.</param>
/// <param name="Lane">Width of the empty lane down each side; zero for a single row.</param>
/// <param name="BusGap">
/// Vertical gap between rows.  A row's horizontal run lives half of this above
/// the row's top edge, which is the middle of the empty band.
/// </param>
/// <param name="Rows">How many rows the children were packed into.</param>
/// <param name="Count">How many children the block was laid out for.</param>
/// <param name="Scale">The size the children are drawn at, relative to a root.</param>
public readonly record struct ViewAllChildBlock(
    Vector Offset,
    double Width,
    double Height,
    double NodesWidth,
    double Lane,
    double BusGap,
    int Rows,
    int Count,
    double Scale)
{
    /// <summary>Top-left of the reserved rectangle, in graph coordinates.</summary>
    public Point OriginFor(Point parentLocation) => parentLocation + Offset;

    public Rect BoundsFor(Point parentLocation) =>
        new(OriginFor(parentLocation), new Size(Width, Height));

    /// <summary>Left edge of the first column of nodes.</summary>
    public double NodesLeftFor(Point parentLocation) => parentLocation.X + Offset.X + Lane;

    /// <summary>Centre of the empty lane the trunk runs down.</summary>
    public double TrunkXFor(Point parentLocation) => parentLocation.X + Offset.X + Lane / 2;

    /// <summary>Centre of the empty lane on the right, used to route a line out.</summary>
    public double MarginXFor(Point parentLocation) =>
        NodesLeftFor(parentLocation) + NodesWidth + Lane / 2;

    /// <summary>
    /// Where the horizontal run for a row of children whose top edge is at
    /// <paramref name="rowTop"/> belongs: the middle of the empty band above
    /// them.  A row is a node plus that band, so a run here cannot touch a node.
    /// </summary>
    public double BusYFor(double rowTop) => rowTop - BusGap / 2;

    /// <summary>
    /// Whether this child is still one of the children the block was laid out
    /// for.  A child the user dragged is not: it keeps a line of its own and
    /// visibly detaches, instead of being claimed by a harness it has left.
    /// </summary>
    public bool Holds(Point parentLocation, ViewAllNodeViewModel child) =>
        !child.HasManualPosition
        && child.HasLayoutPosition
        && child.IsTreeVisible
        && !child.IsUserHidden
        && BoundsFor(parentLocation).Contains(child.Location);
}
