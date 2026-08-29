using System.Windows;

namespace UltraExplorer.Models;

/// <summary>One straight run of a folder's harness, in graph coordinates.</summary>
public readonly record struct ViewAllHarnessSegment(Point From, Point To)
{
    public bool IsHorizontal => Math.Abs(From.Y - To.Y) < 0.001;

    public bool IsVertical => Math.Abs(From.X - To.X) < 0.001;
}

/// <summary>What one folder's harness is made of, and the frame around it.</summary>
public sealed record ViewAllHarness(
    IReadOnlyList<ViewAllHarnessSegment> Segments,
    Rect Frame,
    int Horizontals,
    int Ticks);

/// <summary>
/// Draws the links from one folder to its children as a single harness rather
/// than as a line each.
///
/// The old picture was an elbow per child: down, across, down.  Once children
/// wrap into several rows, every row needs its own horizontal run and those runs
/// land inside the block - so twenty-one children produced twenty-one horizontal
/// lines, four of them cutting straight through rows of nodes.  That is what
/// made a folder unreadable.
///
/// Here there is one trunk down an empty lane reserved on the block's left, one
/// horizontal run per row living in the middle of the empty band above that
/// row's nodes, and a short tick from that run down into each child.  A row is
/// node height plus that band, so a run in the middle of it cannot touch a node:
/// the crossings are gone by construction rather than by luck.  Twenty-one
/// children come out as six horizontal runs, none of them over a node.
/// </summary>
public static class ViewAllHarnessGeometry
{
    /// <summary>
    /// Builds the harness for one lattice.  Past <paramref name="tickLimit"/>
    /// children the interior is left out and only the trunk and the frame remain:
    /// a line to each of four thousand files was never readable, and the frame
    /// plus the branch colour already say what belongs to what.
    /// </summary>
    public static ViewAllHarness Build(
        ViewAllNodeViewModel parent,
        in ViewAllChildBlock block,
        int tickLimit)
    {
        var segments = new List<ViewAllHarnessSegment>(Math.Min(block.Count, tickLimit) * 2 + 8);
        var horizontals = 0;
        var ticks = 0;

        var location = parent.Location;
        var trunkX = block.TrunkXFor(location);
        var firstBusY = block.BusYFor(location, 0);
        var lastBusY = block.BusYFor(location, Math.Max(0, block.Rows - 1));

        // The parent drops straight down into the first run, through the empty
        // band between generations, so it meets nothing on the way.
        segments.Add(new ViewAllHarnessSegment(
            parent.OutputAnchor,
            new Point(parent.OutputAnchor.X, firstBusY)));

        if (block.Rows > 1)
        {
            segments.Add(new ViewAllHarnessSegment(
                new Point(trunkX, firstBusY),
                new Point(trunkX, lastBusY)));
        }

        if (block.Count <= tickLimit)
        {
            var rows = CollectRows(parent, block);
            for (var row = 0; row < rows.Length; row++)
            {
                var members = rows[row];
                if (members is null || members.Count == 0)
                {
                    continue;
                }

                var busY = block.BusYFor(location, row);

                // The run stops at the last node of its own row, so a short final
                // row never reaches further than it has to.
                var rightmost = members.Max(child => child.InputAnchor.X);
                segments.Add(new ViewAllHarnessSegment(
                    new Point(trunkX, busY),
                    new Point(rightmost, busY)));
                horizontals++;

                foreach (var child in members)
                {
                    segments.Add(new ViewAllHarnessSegment(
                        new Point(child.InputAnchor.X, busY),
                        child.InputAnchor));
                    ticks++;
                }
            }
        }

        return new ViewAllHarness(segments, block.BoundsFor(location), horizontals, ticks);
    }

    /// <summary>
    /// The children sitting on this lattice, by row.  A child the user dragged is
    /// not on it any more and is left out, so it keeps a line of its own and
    /// visibly detaches instead of being claimed by a harness it has left.
    /// </summary>
    private static List<ViewAllNodeViewModel>?[] CollectRows(
        ViewAllNodeViewModel parent,
        in ViewAllChildBlock block)
    {
        var rows = new List<ViewAllNodeViewModel>?[Math.Max(1, block.Rows)];
        var location = parent.Location;
        var left = block.NodesLeftFor(location) - 1;
        var right = left + block.NodesWidth + 2;

        foreach (var child in parent.Children)
        {
            if (child.HasManualPosition || !child.IsTreeVisible || !child.HasLayoutPosition)
            {
                continue;
            }

            if (child.Location.X < left || child.Location.X > right)
            {
                continue;
            }

            var row = block.RowOf(location, child.Location);
            if (row < 0)
            {
                continue;
            }

            (rows[row] ??= []).Add(child);
        }

        return rows;
    }
}
