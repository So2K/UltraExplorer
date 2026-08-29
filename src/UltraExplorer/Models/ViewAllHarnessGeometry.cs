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
///
/// The rows are read back off the children - the ones sharing a top edge - which
/// is what lets a row be as tall as an open child's whole subtree while the run
/// above it stays exactly where the empty band is.
/// </summary>
public static class ViewAllHarnessGeometry
{
    /// <summary>
    /// Builds the harness for one block.  Past <paramref name="tickLimit"/>
    /// children the interior is left out and only the trunk and the frame remain:
    /// a line to each of four thousand files was never readable, and the frame
    /// plus the branch colour already say what belongs to what.
    /// </summary>
    public static ViewAllHarness Build(
        ViewAllNodeViewModel parent,
        in ViewAllChildBlock block,
        int tickLimit)
    {
        var location = parent.Location;
        var frame = block.BoundsFor(location);
        var trunkX = block.TrunkXFor(location);
        var segments = new List<ViewAllHarnessSegment>(Math.Min(block.Count, tickLimit) * 2 + 8);
        var horizontals = 0;
        var ticks = 0;

        double firstBusY;
        double lastBusY;
        List<Row>? rows = null;

        if (block.Count <= tickLimit)
        {
            rows = CollectRows(parent, block);
            if (rows.Count == 0)
            {
                // Every child has been dragged out of the block; each keeps its
                // own line, and there is nothing left for a harness to carry.
                return new ViewAllHarness([], frame, 0, 0);
            }

            firstBusY = block.BusYFor(rows[0].Top);
            lastBusY = block.BusYFor(rows[^1].Top);
        }
        else
        {
            // Too many children to walk per redraw.  A folder that large is a
            // uniform grid of collapsed entries, so the two ends of the trunk
            // come off the rectangle instead: the first band is the top of it,
            // and the last is one row up from the bottom.
            firstBusY = frame.Top;
            lastBusY = frame.Bottom - block.BusGap - ViewAllNodeViewModel.DefaultHeight * block.Scale;
        }

        // The parent drops straight down into the first run, through the empty
        // band between generations, so it meets nothing on the way.
        segments.Add(new ViewAllHarnessSegment(
            parent.OutputAnchor,
            new Point(parent.OutputAnchor.X, firstBusY)));

        if (lastBusY > firstBusY + 0.001)
        {
            segments.Add(new ViewAllHarnessSegment(
                new Point(trunkX, firstBusY),
                new Point(trunkX, lastBusY)));
        }

        if (rows is null)
        {
            return new ViewAllHarness(segments, frame, horizontals, ticks);
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var busY = block.BusYFor(row.Top);

            // The run stops at the last node of its own row, so a short final row
            // never reaches further than it has to.  The first row is the one
            // exception: it also has to reach the link coming down from the
            // folder, which is above the row but not always inside it.
            var rightmost = row.Members.Max(child => child.InputAnchor.X);
            if (index == 0)
            {
                rightmost = Math.Max(rightmost, parent.OutputAnchor.X);
            }

            segments.Add(new ViewAllHarnessSegment(
                new Point(trunkX, busY),
                new Point(rightmost, busY)));
            horizontals++;

            foreach (var child in row.Members)
            {
                segments.Add(new ViewAllHarnessSegment(
                    new Point(child.InputAnchor.X, busY),
                    child.InputAnchor));
                ticks++;
            }
        }

        return new ViewAllHarness(segments, frame, horizontals, ticks);
    }

    private readonly record struct Row(double Top, List<ViewAllNodeViewModel> Members);

    /// <summary>
    /// The children still sitting in the block, gathered into the rows they
    /// share a top edge with.  A quarter of a unit of tolerance is enough: the
    /// layout puts a row's children on exactly the same line.
    /// </summary>
    private static List<Row> CollectRows(ViewAllNodeViewModel parent, in ViewAllChildBlock block)
    {
        var location = parent.Location;
        var lanes = new Dictionary<long, List<ViewAllNodeViewModel>>();

        foreach (var child in parent.Children)
        {
            if (!block.Holds(location, child))
            {
                continue;
            }

            var key = (long)Math.Round(child.Location.Y * 4);
            if (!lanes.TryGetValue(key, out var members))
            {
                members = [];
                lanes[key] = members;
            }

            members.Add(child);
        }

        var rows = new List<Row>(lanes.Count);
        foreach (var members in lanes.Values)
        {
            rows.Add(new Row(members[0].Location.Y, members));
        }

        rows.Sort(static (left, right) => left.Top.CompareTo(right.Top));
        return rows;
    }
}
