using System.Windows;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

/// <summary>
/// Uniform grid over the graph.  Viewport culling and layout collision tests are
/// the two operations that would otherwise scan every node, which is what turns
/// a large tree from interactive into unusable; both become proportional to the
/// number of nodes actually near the query instead.
/// </summary>
public sealed class ViewAllSpatialIndex
{
    private const double CellSize = 640;

    private readonly Dictionary<long, List<ViewAllNodeViewModel>> _cells = [];
    private readonly Dictionary<ViewAllNodeViewModel, long[]> _placement = [];

    public int Count => _placement.Count;

    public void AddOrUpdate(ViewAllNodeViewModel node)
    {
        var keys = KeysFor(node.Bounds);
        if (_placement.TryGetValue(node, out var existing))
        {
            if (SameKeys(existing, keys))
            {
                return;
            }

            RemoveFromCells(node, existing);
        }

        foreach (var key in keys)
        {
            if (!_cells.TryGetValue(key, out var bucket))
            {
                bucket = [];
                _cells[key] = bucket;
            }

            bucket.Add(node);
        }

        _placement[node] = keys;
    }

    public void Remove(ViewAllNodeViewModel node)
    {
        if (_placement.Remove(node, out var keys))
        {
            RemoveFromCells(node, keys);
        }
    }

    public void Clear()
    {
        _cells.Clear();
        _placement.Clear();
    }

    /// <summary>Appends every node whose bounds intersect <paramref name="area"/>.</summary>
    public void Query(Rect area, ICollection<ViewAllNodeViewModel> results)
    {
        if (area.IsEmpty)
        {
            return;
        }

        var minX = (long)Math.Floor(area.Left / CellSize);
        var maxX = (long)Math.Floor(area.Right / CellSize);
        var minY = (long)Math.Floor(area.Top / CellSize);
        var maxY = (long)Math.Floor(area.Bottom / CellSize);

        // A node straddling a cell border is registered in each cell it touches,
        // so the same node can be visited more than once.
        PerfLog.Value("index.querycells", (maxX - minX + 1) * (double)(maxY - minY + 1));
        var seen = new HashSet<ViewAllNodeViewModel>();
        for (var x = minX; x <= maxX; x++)
        {
            for (var y = minY; y <= maxY; y++)
            {
                if (!_cells.TryGetValue(Key(x, y), out var bucket))
                {
                    continue;
                }

                foreach (var node in bucket)
                {
                    if (node.Bounds.IntersectsWith(area) && seen.Add(node))
                    {
                        results.Add(node);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Visits the occupied cells overlapping <paramref name="area"/> without
    /// touching the nodes inside them.  This is what lets the furthest zoom
    /// level stay proportional to the grid rather than to the node count.
    /// </summary>
    public void QueryCellCounts(Rect area, Action<Rect, int> visit)
    {
        if (area.IsEmpty)
        {
            return;
        }

        var minX = (long)Math.Floor(area.Left / CellSize);
        var maxX = (long)Math.Floor(area.Right / CellSize);
        var minY = (long)Math.Floor(area.Top / CellSize);
        var maxY = (long)Math.Floor(area.Bottom / CellSize);

        for (var x = minX; x <= maxX; x++)
        {
            for (var y = minY; y <= maxY; y++)
            {
                if (_cells.TryGetValue(Key(x, y), out var bucket) && bucket.Count > 0)
                {
                    visit(new Rect(x * CellSize, y * CellSize, CellSize, CellSize), bucket.Count);
                }
            }
        }
    }

    /// <summary>Side of one grid cell in graph units.</summary>
    public static double CellExtent => CellSize;

    /// <summary>True when anything already occupies <paramref name="area"/>.</summary>
    public bool IsOccupied(Rect area, ViewAllNodeViewModel? ignore = null)
    {
        var minX = (long)Math.Floor(area.Left / CellSize);
        var maxX = (long)Math.Floor(area.Right / CellSize);
        var minY = (long)Math.Floor(area.Top / CellSize);
        var maxY = (long)Math.Floor(area.Bottom / CellSize);

        for (var x = minX; x <= maxX; x++)
        {
            for (var y = minY; y <= maxY; y++)
            {
                if (!_cells.TryGetValue(Key(x, y), out var bucket))
                {
                    continue;
                }

                foreach (var node in bucket)
                {
                    if (!ReferenceEquals(node, ignore) && node.Bounds.IntersectsWith(area))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>The node under a point, preferring the one whose centre is nearest.</summary>
    public ViewAllNodeViewModel? HitTest(Point point, Predicate<ViewAllNodeViewModel>? filter = null)
    {
        var probe = new Rect(point.X - 1, point.Y - 1, 2, 2);
        var candidates = new List<ViewAllNodeViewModel>(8);
        Query(probe, candidates);

        ViewAllNodeViewModel? best = null;
        var bestDistance = double.MaxValue;
        foreach (var node in candidates)
        {
            if (filter is not null && !filter(node))
            {
                continue;
            }

            var bounds = node.Bounds;
            var dx = bounds.Left + bounds.Width / 2 - point.X;
            var dy = bounds.Top + bounds.Height / 2 - point.Y;
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = node;
            }
        }

        return best;
    }

    private void RemoveFromCells(ViewAllNodeViewModel node, long[] keys)
    {
        foreach (var key in keys)
        {
            if (!_cells.TryGetValue(key, out var bucket))
            {
                continue;
            }

            bucket.Remove(node);
            if (bucket.Count == 0)
            {
                _cells.Remove(key);
            }
        }
    }

    private static long[] KeysFor(Rect bounds)
    {
        var minX = (long)Math.Floor(bounds.Left / CellSize);
        var maxX = (long)Math.Floor(bounds.Right / CellSize);
        var minY = (long)Math.Floor(bounds.Top / CellSize);
        var maxY = (long)Math.Floor(bounds.Bottom / CellSize);

        var width = (int)(maxX - minX + 1);
        var height = (int)(maxY - minY + 1);
        var keys = new long[width * height];
        var index = 0;
        for (var x = minX; x <= maxX; x++)
        {
            for (var y = minY; y <= maxY; y++)
            {
                keys[index++] = Key(x, y);
            }
        }

        return keys;
    }

    private static bool SameKeys(long[] left, long[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }

    private static long Key(long x, long y) => (x << 32) ^ (y & 0xFFFFFFFFL);
}
