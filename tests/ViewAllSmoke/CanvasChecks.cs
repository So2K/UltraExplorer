using System.IO;
using System.Windows;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The canvas rules that are not about reading the disk: what hiding a branch
/// does to the space it occupied, and what turns a dragged file into a file
/// operation instead of a layout move.
/// </summary>
internal static partial class Program
{
    private static async Task HiddenBranches(string root)
    {
        Section("hidden branches");

        using var graph = new ViewAllGraphService();
        await graph.InitializeAsync();
        var node = (await graph.AddRootAsync(root))!;
        await graph.ExpandAsync(node);

        var alpha = node.Children.First(child => child.DisplayName == "alpha");
        await graph.ExpandAsync(alpha);
        Check("a folder starts visible", alpha.IsTreeVisible && !alpha.IsUserHidden);
        Check("its space is taken", IsIndexed(graph.Index, alpha));

        graph.Hide(alpha);
        Check("hiding takes the folder off the canvas", !alpha.IsTreeVisible && alpha.IsUserHidden);
        Check("and everything under it", alpha.Children.All(child => !child.IsTreeVisible));

        // The point of hiding is the room, not just the pixels: the folder leaves
        // the grid, and the tree closes over the gap it left.  Asking whether its
        // old rectangle is empty would be the wrong question now - a sibling has
        // very likely moved into it, which is exactly what should happen.
        Check("the space it took is free again", !IsIndexed(graph.Index, alpha));
        Check("so is the space its children took",
            alpha.Children.All(child => !IsIndexed(graph.Index, child)));
        Check("it is listed as hidden",
            graph.HiddenPaths.Contains(alpha.FullPath, StringComparer.OrdinalIgnoreCase));

        // Dragging the parent offsets every descendant, hidden ones included.
        node.Location = new Point(node.Location.X + 700, node.Location.Y + 400);
        Check("dragging an ancestor does not put a hidden branch back in the way",
            !IsIndexed(graph.Index, alpha));

        graph.CollapseAll();
        await graph.ExpandAsync(node);
        await graph.ExpandAsync(alpha);
        Check("re-expanding the parent does not un-hide it", !alpha.IsTreeVisible);

        await graph.RefreshBranchAsync(node);
        var alphaAfterRefresh = node.Children.First(child => child.DisplayName == "alpha");
        Check("a refresh does not un-hide it",
            alphaAfterRefresh.IsUserHidden && !alphaAfterRefresh.IsTreeVisible);

        // ---- across a restart -------------------------------------------------
        var saved = graph.CaptureState(new ViewAllViewportState(new Point(0, 0), 1));
        Check("the hide is written to the workspace",
            saved.HiddenPaths.Contains(alpha.FullPath, StringComparer.OrdinalIgnoreCase));

        using var reopened = new ViewAllGraphService();
        await reopened.InitializeAsync(saved);
        var reopenedRoot = (await reopened.AddRootAsync(root))!;
        await reopened.ExpandAsync(reopenedRoot);
        var reopenedAlpha = reopenedRoot.Children.FirstOrDefault(child => child.DisplayName == "alpha");
        Check("a hidden folder comes back hidden", reopenedAlpha is { IsUserHidden: true, IsTreeVisible: false });
        Check("and takes no space", reopenedAlpha is null || !IsIndexed(reopened.Index, reopenedAlpha));
        Check("its siblings are laid out as if it were not there",
            reopenedRoot.Children.Where(child => !child.IsUserHidden).All(child => child.HasLayoutPosition));

        // ---- and back again ---------------------------------------------------
        Check("showing it again works", reopened.Show(reopenedAlpha!.FullPath));
        Check("it is visible", reopenedAlpha.IsTreeVisible && !reopenedAlpha.IsUserHidden);
        Check("nothing is left in the hidden list", reopened.HiddenPaths.Count == 0);

        var placed = reopened.Nodes.Where(item => item.IsTreeVisible && item.HasLayoutPosition).ToArray();
        var overlapping = false;
        for (var i = 0; i < placed.Length && !overlapping; i++)
        {
            for (var j = i + 1; j < placed.Length; j++)
            {
                if (placed[i].Bounds.IntersectsWith(placed[j].Bounds))
                {
                    overlapping = true;
                    break;
                }
            }
        }

        Check("it does not come back on top of anything", !overlapping);
        Check("showing something that is not hidden is a no-op", !reopened.Show(root));
    }

    private static async Task TidyLayout(string root)
    {
        Section("tidy the layout");

        using var graph = new ViewAllGraphService();
        await graph.InitializeAsync();
        var node = (await graph.AddRootAsync(root))!;
        await graph.ExpandAsync(node);
        var wide = node.Children.First(child => child.DisplayName == "wide");
        await graph.ExpandAsync(wide);

        // One node dragged far away is what every later expansion has to route
        // around, and what a tidy is for.
        var stray = node.Children.First(child => child.DisplayName == "beta");
        stray.Location = new Point(9000, 9000);
        Check("a dragged node is an anchor", stray.HasManualPosition);

        graph.Relayout();

        Check("tidying drops every hand-placed position",
            graph.Nodes.All(item => !item.HasManualPosition));
        Check("every visible node is placed again",
            graph.Nodes.Where(item => item.IsTreeVisible).All(item => item.HasLayoutPosition));
        Check("the stray came home",
            Math.Abs(stray.Location.X - 9000) > 1000 || Math.Abs(stray.Location.Y - 9000) > 1000);
        Check("children are still below their parent",
            node.Children.All(child => child.Location.Y > node.Location.Y));

        var placed = graph.Nodes.Where(item => item.IsTreeVisible && item.HasLayoutPosition).ToArray();
        var overlapping = false;
        for (var i = 0; i < placed.Length && !overlapping; i++)
        {
            for (var j = i + 1; j < placed.Length; j++)
            {
                if (placed[i].Bounds.IntersectsWith(placed[j].Bounds))
                {
                    overlapping = true;
                    break;
                }
            }
        }

        Check("and nothing overlaps afterwards", !overlapping);
    }

    private static async Task Harness(string root)
    {
        Section("link harness");

        using var graph = new ViewAllGraphService();
        await graph.InitializeAsync();
        var node = (await graph.AddRootAsync(root))!;
        await graph.ExpandAsync(node);

        var wide = node.Children.First(child => child.DisplayName == "wide");
        await graph.ExpandAsync(wide);

        Check("a wrapped folder records the rectangle its children were placed in",
            wide.ChildBlock is not null);
        var block = wide.ChildBlock!.Value;
        Check("the block has more than one row", block.Rows > 1);
        Check("it reserves a lane on each side", block.Lane > 0);
        Check("its bounds hold every child",
            wide.Children.All(child => block.BoundsFor(wide.Location).Contains(child.Bounds)));

        var harness = ViewAllHarnessGeometry.Build(wide, block, tickLimit: 64);
        Check("the harness is drawn", harness.Segments.Count > 0);
        Check("every child gets a tick", harness.Ticks == wide.Children.Count);

        // The complaint was one horizontal run per child, four of them cutting
        // through the block.  One per row is the whole point.
        Check("there is one horizontal run per row, not one per child",
            harness.Horizontals == block.Rows && harness.Horizontals < wide.Children.Count);

        var horizontals = harness.Segments.Where(segment => segment.IsHorizontal).ToArray();
        Check("no horizontal run passes over a node",
            horizontals.All(run => wide.Children.All(child =>
                run.From.Y <= child.Bounds.Top - 0.001 || run.From.Y >= child.Bounds.Bottom + 0.001)));

        var trunkX = block.TrunkXFor(wide.Location);
        Check("the trunk lane holds no child",
            wide.Children.All(child => child.Bounds.Left > trunkX + 0.001));

        // The link down from the folder has to land on a run, not next to one.
        var stub = harness.Segments[0];
        var firstRun = harness.Segments.First(segment =>
            segment.IsHorizontal && Math.Abs(segment.From.Y - stub.To.Y) < 0.001);
        Check("the link down from the folder lands on the first run",
            stub.To.X >= Math.Min(firstRun.From.X, firstRun.To.X) - 0.001
            && stub.To.X <= Math.Max(firstRun.From.X, firstRun.To.X) + 0.001);

        Check("every segment stays inside the reserved area",
            harness.Segments.All(segment =>
                block.BoundsFor(wide.Location).Contains(segment.To)
                || Math.Abs(segment.To.Y - wide.OutputAnchor.Y) < 1
                || block.BoundsFor(wide.Location).Contains(segment.From)));

        // Dragging the folder must carry the whole harness with it, because the
        // rectangle is recorded as an offset rather than as absolute points.
        var before = harness.Segments.Select(segment => segment.From).ToArray();
        var delta = new Vector(300, -140);
        wide.Location = wide.Location + delta;
        var moved = ViewAllHarnessGeometry.Build(wide, wide.ChildBlock!.Value, tickLimit: 64);
        Check("the harness moves with the folder",
            moved.Segments.Select(segment => segment.From)
                .Zip(before, (now, then) => (now - (then + delta)).Length)
                .All(error => error < 1e-6));

        // Past the limit a line to each child was never readable anyway.
        var few = ViewAllHarnessGeometry.Build(wide, wide.ChildBlock!.Value, tickLimit: 4);
        Check("a big folder draws a trunk but no ticks", few.Ticks == 0 && few.Horizontals == 0);
        Check("and still draws its stub and trunk", few.Segments.Count == 2);

        // A child that was dragged has left the block and keeps its own line.
        var exile = wide.Children[0];
        exile.Location = new Point(20_000, 20_000);
        var afterExile = ViewAllHarnessGeometry.Build(wide, wide.ChildBlock!.Value, tickLimit: 64);
        Check("a hand-placed child leaves the harness",
            afterExile.Ticks == wide.Children.Count - 1);

        var small = node.Children.First(child => child.DisplayName == "alpha");
        await graph.ExpandAsync(small);
        Check("a folder that fits on one row records a block with no lane",
            small.ChildBlock is { Rows: 1, Lane: 0 });
    }

    /// <summary>
    /// The property the whole layout rests on: every folder owns a rectangle,
    /// and nothing that is not inside that folder may enter it.  With several
    /// folders open at once this is the difference between a tree and a soup -
    /// it is what stops two branches interleaving, two frames crossing, and a
    /// link running through a folder it has nothing to do with.
    /// </summary>
    private static async Task TidyTree()
    {
        Section("tidy tree");

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerTree", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            BuildTreeFixture(root);

            using var graph = new ViewAllGraphService();
            await graph.InitializeAsync();
            var node = (await graph.AddRootAsync(root))!;
            await graph.ExpandAsync(node);

            // Several folders open at once is the case that used to collapse.
            foreach (var name in new[] { "one", "two", "three" })
            {
                await graph.ExpandAsync(node.Children.First(child => child.DisplayName == name));
            }

            var visible = graph.Nodes.Where(item => item.IsTreeVisible && item.HasLayoutPosition).ToArray();
            Check("every folder that was opened has a block",
                visible.Where(item => item.IsExpanded && item.Children.Count > 0)
                    .All(item => item.ChildBlock is not null));

            // 1. A child's whole subtree is inside its parent's block.
            var contained = true;
            foreach (var parent in visible.Where(item => item.ChildBlock is not null))
            {
                var bounds = parent.ChildBlock!.Value.BoundsFor(parent.Location);
                foreach (var child in parent.Children.Where(item => item.IsTreeVisible))
                {
                    if (!bounds.Contains(SubtreeBounds(child)))
                    {
                        contained = false;
                    }
                }
            }

            Check("a child's whole subtree stays inside its parent's block", contained);

            // 2. No two subtrees that are not related overlap.  This is the one
            //    that used to fail: blocks reserved room the index never knew
            //    about, so two folders interlocked as long as no node touched a
            //    node.
            var boxes = visible.Select(item => (Node: item, Box: SubtreeBounds(item))).ToArray();
            var crossing = 0;
            for (var i = 0; i < boxes.Length; i++)
            {
                for (var j = i + 1; j < boxes.Length; j++)
                {
                    if (IsRelated(boxes[i].Node, boxes[j].Node))
                    {
                        continue;
                    }

                    if (boxes[i].Box.IntersectsWith(boxes[j].Box))
                    {
                        crossing++;
                    }
                }
            }

            Check("two unrelated subtrees never overlap", crossing == 0);

            // 3. No link a folder draws touches a node that is not its own child.
            var struck = 0;
            foreach (var parent in visible.Where(item => item.ChildBlock is { Rows: > 1 }))
            {
                var harness = ViewAllHarnessGeometry.Build(parent, parent.ChildBlock!.Value, 64);
                foreach (var segment in harness.Segments)
                {
                    foreach (var other in visible)
                    {
                        if (ReferenceEquals(other, parent) || ReferenceEquals(other.Parent, parent))
                        {
                            continue;
                        }

                        if (Crosses(segment, other.Bounds))
                        {
                            struck++;
                        }
                    }
                }
            }

            Check("no link crosses a node it does not belong to", struck == 0);

            // 4. The pass is deterministic, which is what lets a refresh, a
            //    reload or a second run leave the canvas exactly where it was.
            var before = visible.ToDictionary(item => item.FullPath, item => item.Location);
            graph.Relayout();
            Check("laying out again produces the same canvas",
                visible.All(item => (item.Location - before[item.FullPath]).Length < 1e-9));

            var refreshed = node.Children.First(child => child.DisplayName == "two");
            await graph.RefreshBranchAsync(refreshed);
            Check("refreshing a branch does not rearrange the canvas",
                graph.Nodes.Where(item => item.IsTreeVisible && before.ContainsKey(item.FullPath))
                    .All(item => (item.Location - before[item.FullPath]).Length < 1e-9));

            // 5. The harness and the per-child lines are two pictures of the
            //    same link.  Drawing both is what put a line across every block,
            //    and it came back the moment the layout was held over a batch of
            //    expansions - which is exactly what restoring a session does.
            Check("a link the harness carries is not drawn twice", DoubledLinks(graph) == 0);

            var saved = graph.CaptureState(new ViewAllViewportState(new Point(0, 0), 1));
            using (var reopened = new ViewAllGraphService())
            {
                await reopened.InitializeAsync(saved);
                Check("a restored session opens the same folders",
                    reopened.Nodes.Count(item => item.IsExpanded) == graph.Nodes.Count(item => item.IsExpanded));
                Check("and does not draw its links twice", DoubledLinks(reopened) == 0);
                Check("and every folder it opened has a block",
                    reopened.Nodes
                        .Where(item => item.IsExpanded && item.IsTreeVisible && item.Children.Count > 0)
                        .All(item => item.ChildBlock is not null));
            }

            // 6. A collapsed branch stops reserving room.  It used to stay in
            //    the grid and push later expansions around nodes nobody could
            //    see.
            var closed = node.Children.First(child => child.DisplayName == "one");
            var indexedBefore = graph.Index.Count;
            graph.Collapse(closed);
            Check("collapsing gives the space back",
                graph.Index.Count == indexedBefore - closed.Children.Count);

            // 7. The root still holds everything it started with.
            var rowsOf = node.ChildBlock!.Value;
            Check("the root still holds every child in one block",
                node.Children.Where(child => child.IsTreeVisible)
                    .All(child => rowsOf.BoundsFor(node.Location).Contains(child.Bounds)));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void BuildTreeFixture(string root)
    {
        for (var index = 0; index < 9; index++)
        {
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(
                Path.Combine(root, "one")).FullName, $"one-{index}.txt"), "x");
        }

        for (var index = 0; index < 14; index++)
        {
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(
                Path.Combine(root, "two")).FullName, $"two-{index}.txt"), "x");
        }

        for (var index = 0; index < 5; index++)
        {
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(
                Path.Combine(root, "three")).FullName, $"three-{index}.txt"), "x");
        }

        Directory.CreateDirectory(Path.Combine(root, "four"));
        for (var index = 0; index < 8; index++)
        {
            File.WriteAllText(Path.Combine(root, $"plain-{index}.txt"), "x");
        }
    }

    /// <summary>
    /// Links that are drawn by the harness and by their own connection at the
    /// same time.  There should never be any: one link, one line.
    /// </summary>
    private static int DoubledLinks(ViewAllGraphService graph) =>
        graph.Edges.Count(edge =>
            edge.IsTreeVisible
            && edge.Source.ChildBlock is { Rows: > 1 } block
            && block.Holds(edge.Source.Location, edge.Target));

    /// <summary>
    /// Whether the grid still holds this node.  The grid is what culling, hit
    /// testing and drop targets read, so "is it out of the way" means "is it out
    /// of the grid" - not "is its old rectangle still empty".
    /// </summary>
    private static bool IsIndexed(ViewAllSpatialIndex index, ViewAllNodeViewModel node)
    {
        var found = new List<ViewAllNodeViewModel>();
        index.Query(node.Bounds, found);
        return found.Contains(node);
    }

    /// <summary>The rectangle a node and everything visible under it occupies.</summary>
    private static Rect SubtreeBounds(ViewAllNodeViewModel node)
    {
        var bounds = node.Bounds;
        foreach (var child in node.Children)
        {
            if (child.IsTreeVisible && child.HasLayoutPosition)
            {
                bounds.Union(SubtreeBounds(child));
            }
        }

        return bounds;
    }

    private static bool IsRelated(ViewAllNodeViewModel left, ViewAllNodeViewModel right)
    {
        for (var walk = left; walk is not null; walk = walk.Parent)
        {
            if (ReferenceEquals(walk, right))
            {
                return true;
            }
        }

        for (var walk = right; walk is not null; walk = walk.Parent)
        {
            if (ReferenceEquals(walk, left))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a straight run passes through a rectangle.  Every run is either
    /// horizontal or vertical, so this is two interval tests rather than a
    /// general segment-rectangle intersection.
    /// </summary>
    private static bool Crosses(ViewAllHarnessSegment segment, Rect box)
    {
        var left = Math.Min(segment.From.X, segment.To.X);
        var right = Math.Max(segment.From.X, segment.To.X);
        var top = Math.Min(segment.From.Y, segment.To.Y);
        var bottom = Math.Max(segment.From.Y, segment.To.Y);

        const double Tolerance = 0.001;
        return right > box.Left + Tolerance
            && left < box.Right - Tolerance
            && bottom > box.Top + Tolerance
            && top < box.Bottom - Tolerance;
    }

    private static async Task FolderColours(string root)
    {
        Section("folder colours");

        using var graph = new ViewAllGraphService();
        await graph.InitializeAsync();
        var node = (await graph.AddRootAsync(root))!;
        await graph.ExpandAsync(node);
        var alpha = node.Children.First(child => child.DisplayName == "alpha");
        await graph.ExpandAsync(alpha);

        var beta = node.Children.First(child => child.DisplayName == "beta");
        Check("two folders get two colours", alpha.BranchColor != beta.BranchColor);

        // The hue has to come from the path, not from a per-process hash, or the
        // whole canvas would be repainted differently on every launch.
        var twin = new ViewAllGraphService();
        await twin.InitializeAsync();
        var twinRoot = (await twin.AddRootAsync(root))!;
        await twin.ExpandAsync(twinRoot);
        var twinAlpha = twinRoot.Children.First(child => child.DisplayName == "alpha");
        Check("the same folder is the same colour every run", twinAlpha.BranchColor == alpha.BranchColor);
        twin.Dispose();

        var before = alpha.BranchColor;
        var childBrush = alpha.Children[0].FamilyBrush;
        var edge = graph.Edges.First(item => ReferenceEquals(item.Source, alpha));
        Check("a child wears its parent colour", ReferenceEquals(childBrush, alpha.BranchBrush));
        Check("so does the line to it", ReferenceEquals(edge.Stroke, alpha.BranchBrush));

        var notified = new List<string>();
        edge.PropertyChanged += (_, e) => notified.Add(e.PropertyName ?? string.Empty);

        alpha.AccentHex = "#4ED6A0";

        Check("a chosen colour takes over the branch", alpha.BranchColor != before);
        Check("the line follows it", ReferenceEquals(edge.Stroke, alpha.BranchBrush));
        Check("and says so, or the canvas would not repaint", notified.Contains("Stroke"));
        Check("the children follow it too",
            ReferenceEquals(alpha.Children[0].FamilyBrush, alpha.BranchBrush));
        Check("a sibling is untouched", beta.BranchColor != alpha.BranchColor);

        alpha.AccentHex = string.Empty;
        Check("clearing it goes back to the colour of the path", alpha.BranchColor == before);
    }

    private static Task EverythingSearch()
    {
        Section("Everything search");

        var everything = new EverythingSearchService();
        Check($"the SDK is looked for and reported ({everything.LibraryPath ?? "not found"})",
            everything.LibraryPath is null || File.Exists(everything.LibraryPath));
        Check("availability implies a library",
            !everything.IsAvailable || everything.LibraryPath is not null);
        Check("an unavailable engine says why",
            everything.IsAvailable || everything.UnavailableReason.Length > 0);

        // Everything restricts a search to a subtree with a path term; getting
        // this wrong silently searches the whole disk instead of the folder.
        Check("an unscoped search is the query itself",
            EverythingSearchService.BuildSearch("  report  ", null) == "report");
        Check("a scoped search becomes a path term",
            EverythingSearchService.BuildSearch("report", @"D:\Games")
                == @"path:""D:\Games"" report");
        Check("a trailing separator is trimmed off the scope",
            EverythingSearchService.BuildSearch("report", @"D:\Games\")
                == @"path:""D:\Games"" report");
        Check("a drive root keeps its letter",
            EverythingSearchService.BuildSearch("report", @"D:\")
                .StartsWith(@"path:""D:""", StringComparison.Ordinal));

        Check("IPC failure is explained in words", EverythingSearchService.DescribeError(2).Contains("not running"));
        Check("an unknown code still says something", EverythingSearchService.DescribeError(99).Contains("99"));

        // Asking an unavailable engine must be harmless, not an exception.
        var empty = everything.IsAvailable
            ? []
            : everything.SearchAsync("anything", null, 10, CancellationToken.None).GetAwaiter().GetResult();
        Check("an unavailable engine returns nothing rather than throwing", empty.Count == 0);

        return Task.CompletedTask;
    }

    private static Task ProgramTargets()
    {
        Section("dropping onto a program");

        Check("an exe is a program", NativeShellService.IsExecutable(@"C:\Windows\notepad.exe"));
        Check("so is a shortcut", NativeShellService.IsExecutable(@"C:\x\thing.lnk"));
        Check("and a batch file", NativeShellService.IsExecutable(@"C:\x\build.cmd"));
        Check("a document is not", !NativeShellService.IsExecutable(@"C:\x\notes.txt"));
        Check("neither is something with no extension", !NativeShellService.IsExecutable(@"C:\x\LICENSE"));

        // A path ending in a backslash is the case a naive pair of quotes eats:
        // the backslash escapes the closing quote and the argument runs on.
        Check("a plain name needs no quotes",
            NativeShellService.BuildCommandLine(["notes.txt"]) == "notes.txt");
        Check("a name with a space is quoted",
            NativeShellService.BuildCommandLine([@"C:\my files\notes.txt"]) == "\"C:\\my files\\notes.txt\"");
        Check("a trailing backslash cannot escape the closing quote",
            NativeShellService.BuildCommandLine([@"C:\my files\"]) == "\"C:\\my files\\\\\"");
        Check("several arguments are separated by one space",
            NativeShellService.BuildCommandLine(["a.txt", "b.txt"]) == "a.txt b.txt");
        Check("an embedded quote is escaped",
            NativeShellService.BuildCommandLine(["say \"hi\""]) == "\"say \\\"hi\\\"\"");

        return Task.CompletedTask;
    }
}
