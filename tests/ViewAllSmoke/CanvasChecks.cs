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
        var alphaBounds = alpha.Bounds;
        var childBounds = alpha.Children.Select(child => child.Bounds).ToArray();

        Check("a folder starts visible", alpha.IsTreeVisible && !alpha.IsUserHidden);
        Check("its space is taken", graph.Index.IsOccupied(alphaBounds));

        graph.Hide(alpha);
        Check("hiding takes the folder off the canvas", !alpha.IsTreeVisible && alpha.IsUserHidden);
        Check("and everything under it", alpha.Children.All(child => !child.IsTreeVisible));

        // The point of hiding is the room, not just the pixels.
        Check("the space it took is free again", !graph.Index.IsOccupied(alphaBounds));
        Check("so is the space its children took",
            childBounds.All(bounds => !graph.Index.IsOccupied(bounds)));
        Check("it is listed as hidden",
            graph.HiddenPaths.Contains(alpha.FullPath, StringComparer.OrdinalIgnoreCase));

        // Dragging the parent offsets every descendant, hidden ones included.
        node.Location = new Point(node.Location.X + 700, node.Location.Y + 400);
        Check("dragging an ancestor does not put a hidden branch back in the way",
            !graph.Index.IsOccupied(alpha.Bounds));

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
        Check("and takes no space", reopenedAlpha is null || !reopened.Index.IsOccupied(reopenedAlpha.Bounds));
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

        Check("a wrapped folder records the lattice its children were placed on",
            wide.ChildBlocks.Count == 1);
        var block = wide.ChildBlocks[0];
        Check("the lattice has more than one row", block.Rows > 1);
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

        Check("every segment stays inside the reserved area",
            harness.Segments.All(segment =>
                block.BoundsFor(wide.Location).Contains(segment.To)
                || Math.Abs(segment.To.Y - wide.OutputAnchor.Y) < 1
                || block.BoundsFor(wide.Location).Contains(segment.From)));

        // Dragging the folder must carry the whole harness with it, because the
        // lattice is recorded as an offset rather than as absolute points.
        var before = harness.Segments.Select(segment => segment.From).ToArray();
        var delta = new Vector(300, -140);
        wide.Location = wide.Location + delta;
        var moved = ViewAllHarnessGeometry.Build(wide, wide.ChildBlocks[0], tickLimit: 64);
        Check("the harness moves with the folder",
            moved.Segments.Select(segment => segment.From)
                .Zip(before, (now, then) => (now - (then + delta)).Length)
                .All(error => error < 1e-6));

        // Past the limit a line to each child was never readable anyway.
        var few = ViewAllHarnessGeometry.Build(wide, wide.ChildBlocks[0], tickLimit: 4);
        Check("a big folder draws a trunk but no ticks", few.Ticks == 0 && few.Horizontals == 0);
        Check("and still draws its stub and trunk", few.Segments.Count == 2);

        // A child that was dragged has left the lattice and keeps its own line.
        var exile = wide.Children[0];
        exile.Location = new Point(20_000, 20_000);
        var afterExile = ViewAllHarnessGeometry.Build(wide, wide.ChildBlocks[0], tickLimit: 64);
        Check("a hand-placed child leaves the harness",
            afterExile.Ticks == wide.Children.Count - 1);

        var small = node.Children.First(child => child.DisplayName == "alpha");
        await graph.ExpandAsync(small);
        Check("a folder that fits on one row records a lattice with no lane",
            small.ChildBlocks.Count == 1 && small.ChildBlocks[0].Rows == 1 && small.ChildBlocks[0].Lane == 0);
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
