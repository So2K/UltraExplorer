using System.Windows;

namespace UltraExplorer.Models;

/// <summary>
/// The lattice a folder's children were laid out on, recorded when they were
/// placed.
///
/// The links are drawn from this rather than from where the children happen to
/// sit, which matters for two reasons.  It is stored as an offset from the
/// parent, so dragging a folder moves the whole harness with it and no
/// bookkeeping is needed.  And when the layout could not reserve a clean
/// rectangle and fell back to placing children one by one, no record is written
/// at all - so a comb is never drawn pointing at a lattice that is not there.
/// </summary>
public readonly record struct ViewAllChildBlock(
    Vector Offset,
    int Columns,
    int Rows,
    int Count,
    double StepX,
    double StepY,
    double GapX,
    double GapY,
    double Lane,
    double NodesWidth,
    double Scale)
{
    /// <summary>Top-left of the reserved rectangle, in graph coordinates.</summary>
    public Point OriginFor(Point parentLocation) => parentLocation + Offset;

    /// <summary>The reserved rectangle: the lattice plus its trunk and margin lanes.</summary>
    public Rect BoundsFor(Point parentLocation) => new(
        OriginFor(parentLocation),
        new Size(NodesWidth + 2 * Lane, Rows * StepY - GapY + GapY * 1.5));

    /// <summary>Left edge of the first column of nodes.</summary>
    public double NodesLeftFor(Point parentLocation) => parentLocation.X + Offset.X + Lane;

    /// <summary>Centre of the empty lane the trunk runs down.</summary>
    public double TrunkXFor(Point parentLocation) => parentLocation.X + Offset.X + Lane / 2;

    /// <summary>Centre of the empty lane on the right, used to route a line out.</summary>
    public double MarginXFor(Point parentLocation) =>
        NodesLeftFor(parentLocation) + NodesWidth + Lane / 2;

    /// <summary>Top of the nodes in row <paramref name="row"/>.</summary>
    public double RowTopFor(Point parentLocation, int row) =>
        parentLocation.Y + Offset.Y + GapY / 2 + row * StepY;

    /// <summary>
    /// Where a row's horizontal run lives: the middle of the empty band above
    /// its nodes.  Row heights are node height plus this gap, so a run here can
    /// never touch a node.
    /// </summary>
    public double BusYFor(Point parentLocation, int row) => RowTopFor(parentLocation, row) - GapY / 2;

    /// <summary>Which row a child sits in, or -1 when it is not on this lattice.</summary>
    public int RowOf(Point parentLocation, Point childLocation)
    {
        if (StepY <= 0)
        {
            return -1;
        }

        var top = parentLocation.Y + Offset.Y + GapY / 2;
        var row = (int)Math.Round((childLocation.Y - top) / StepY);
        return row >= 0 && row < Rows && Math.Abs(top + row * StepY - childLocation.Y) < 1
            ? row
            : -1;
    }
}
