using System.IO;
using System.Windows;
using System.Windows.Input;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// Headless checks for the View All engine: lazy expansion, incremental layout,
/// viewport culling, branch refresh and persistence.  These are the parts that
/// cannot be judged from a screenshot.
/// </summary>
internal static class Program
{
    private static int _failures;
    private static int _checks;

    [STAThread]
    private static int Main()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "UltraExplorerSmoke", Guid.NewGuid().ToString("N"));
        try
        {
            BuildFixture(fixtureRoot);

            RunAsync(fixtureRoot).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FATAL {ex}");
            _failures++;
        }
        finally
        {
            TryDelete(fixtureRoot);
        }

        Console.WriteLine();
        Console.WriteLine($"{_checks - _failures}/{_checks} checks passed");
        return _failures == 0 ? 0 : 1;
    }

    private static async Task RunAsync(string fixtureRoot)
    {
        await PathHelpers(fixtureRoot);
        await LazyExpansion(fixtureRoot);
        await CollapseAndReExpand(fixtureRoot);
        await RefreshKeepsExpansion(fixtureRoot);
        await TruncationAndLoadMore(fixtureRoot);
        await LayoutIsStableAndNonOverlapping(fixtureRoot);
        await ViewportCulling(fixtureRoot);
        await DropTargets(fixtureRoot);
        await Persistence(fixtureRoot);
        await Marks(fixtureRoot);
    }

    // ---- fixture -----------------------------------------------------------

    private static void BuildFixture(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "alpha", "alpha-1"));
        Directory.CreateDirectory(Path.Combine(root, "alpha", "alpha-2"));
        Directory.CreateDirectory(Path.Combine(root, "beta"));
        Directory.CreateDirectory(Path.Combine(root, "wide"));
        File.WriteAllText(Path.Combine(root, "readme.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "alpha", "note.md"), "note");
        for (var index = 0; index < 60; index++)
        {
            File.WriteAllText(Path.Combine(root, "wide", $"item-{index:D3}.bin"), "x");
        }
    }

    // ---- checks ------------------------------------------------------------

    private static Task PathHelpers(string root)
    {
        Section("path helpers");

        var chain = ViewAllPath.AncestorChain(Path.Combine(root, "alpha", "alpha-1"));
        Check("ancestor chain is root first", chain[0] == Path.GetPathRoot(root));
        Check("ancestor chain ends at the target", ViewAllPath.Equals(chain[^1], Path.Combine(root, "alpha", "alpha-1")));
        Check("ancestor chain contains the parent", chain.Any(step => ViewAllPath.Equals(step, Path.Combine(root, "alpha"))));

        Check("drive root keeps its separator", ViewAllPath.Normalize(@"C:\") == @"C:\");
        Check("trailing separator is trimmed", ViewAllPath.Normalize(root + @"\") == ViewAllPath.Normalize(root));
        Check("node ids are deterministic",
            ViewAllNodeIdentity.FromPath(root) == ViewAllNodeIdentity.FromPath(root.ToUpperInvariant()));

        Check("same volume detected", NativeShellService.IsSameVolume(root, Path.Combine(root, "alpha")));
        Check("moving a folder into itself is rejected",
            NativeShellService.IsInvalidMoveTarget(Path.Combine(root, "alpha"), Path.Combine(root, "alpha", "alpha-1")));
        Check("moving a folder to a sibling is allowed",
            !NativeShellService.IsInvalidMoveTarget(Path.Combine(root, "alpha"), Path.Combine(root, "beta")));

        // The drag cursor and the operation that runs must come from one rule.
        string[] sameVolume = [Path.Combine(root, "readme.txt")];
        var target = Path.Combine(root, "beta");
        Check("same volume defaults to move",
            MainViewModel.ShouldMove(sameVolume, target, ModifierKeys.None));
        Check("Ctrl forces a copy",
            !MainViewModel.ShouldMove(sameVolume, target, ModifierKeys.Control));
        Check("Shift forces a move",
            MainViewModel.ShouldMove(sameVolume, target, ModifierKeys.Shift));
        Check("Shift wins over Ctrl",
            MainViewModel.ShouldMove(sameVolume, target, ModifierKeys.Control | ModifierKeys.Shift));

        var otherVolume = DriveInfo.GetDrives()
            .FirstOrDefault(drive => drive.IsReady
                && !string.Equals(drive.Name, Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase));
        if (otherVolume is not null)
        {
            Check("a different volume defaults to copy",
                !MainViewModel.ShouldMove([otherVolume.RootDirectory.FullName + "probe.txt"], target, ModifierKeys.None));
        }

        return Task.CompletedTask;
    }

    private static async Task LazyExpansion(string root)
    {
        Section("lazy expansion");
        using var graph = new ViewAllGraphService();
        await graph.InitializeAsync();

        var driveRoots = graph.Roots.Count();
        Check("drives become roots", driveRoots > 0);
        Check("no descendants are scanned up front", graph.Nodes.Count == driveRoots);

        var node = await graph.AddRootAsync(root);
        Check("an extra root can be added", node is not null);
        Check("adding a root does not read it", node!.Children.Count == 0);

        var result = await graph.ExpandAsync(node);
        Check("expansion reads exactly one level", result.WasLoaded);
        Check("children are created", node.Children.Count == 4);
        Check("folders sort before files", node.Children.Take(3).All(child => child.IsDirectory)
            && node.Children[3].IsFile);
        Check("grandchildren are not read", node.Children.All(child => child.Children.Count == 0));
        Check("each child has an edge", graph.Edges.Count(edge => edge.Source == node) == 4);

        var alpha = node.Children.First(child => child.DisplayName == "alpha");
        await graph.ExpandAsync(alpha);
        Check("second level reads its own children", alpha.Children.Count == 3);
        Check("depth is tracked", alpha.Children.All(child => child.Depth == alpha.Depth + 1));
    }

    private static async Task CollapseAndReExpand(string root)
    {
        Section("collapse and re-expand");
        using var graph = new ViewAllGraphService();
        await graph.InitializeAsync();
        var node = (await graph.AddRootAsync(root))!;
        await graph.ExpandAsync(node);
        var alpha = node.Children.First(child => child.DisplayName == "alpha");
        await graph.ExpandAsync(alpha);

        var alphaChildPositions = alpha.Children.Select(child => child.Location).ToArray();
        var loadedBefore = graph.Nodes.Count;

        graph.Collapse(node);
        Check("collapsing hides descendants", node.Children.All(child => !child.IsTreeVisible));
        Check("collapsing hides grandchildren", alpha.Children.All(child => !child.IsTreeVisible));
        Check("collapsing keeps loaded data", graph.Nodes.Count == loadedBefore);
        Check("collapsed edges are hidden", graph.Edges.All(edge => !edge.IsTreeVisible));

        var result = await graph.ExpandAsync(node);
        Check("re-expanding does not re-read the folder", !result.WasLoaded);
        Check("children are visible again", node.Children.All(child => child.IsTreeVisible));
        Check("re-expanding restores the sub-branch the user had open", alpha.Children.All(child => child.IsTreeVisible));
        Check("positions survive a collapse", alpha.Children.Select(child => child.Location).SequenceEqual(alphaChildPositions));

        graph.CollapseAll();
        Check("collapse all clears every expansion", graph.Nodes.All(item => !item.IsExpanded));
    }

    private static async Task RefreshKeepsExpansion(string root)
    {
        Section("branch refresh");
        using var graph = new ViewAllGraphService();
        await graph.InitializeAsync();
        var node = (await graph.AddRootAsync(root))!;
        await graph.ExpandAsync(node);
        var alpha = node.Children.First(child => child.DisplayName == "alpha");
        await graph.ExpandAsync(alpha);
        var alphaLocation = alpha.Location;

        var added = Path.Combine(root, "gamma");
        Directory.CreateDirectory(added);
        try
        {
            await graph.RefreshBranchAsync(node);
            Check("refresh picks up a new folder", node.Children.Any(child => child.DisplayName == "gamma"));

            var alphaAfter = node.Children.First(child => child.DisplayName == "alpha");
            Check("refresh keeps the branch expanded", alphaAfter.IsExpanded);
            Check("refresh restores grandchildren", alphaAfter.Children.Count == 3);
            Check("refresh keeps saved coordinates", alphaAfter.Location == alphaLocation);
        }
        finally
        {
            TryDelete(added);
        }
    }

    private static async Task TruncationAndLoadMore(string root)
    {
        Section("truncation");
        using var graph = new ViewAllGraphService(new ViewAllGraphOptions(MaximumChildrenPerFolder: 32));
        await graph.InitializeAsync();
        var node = (await graph.AddRootAsync(root))!;
        await graph.ExpandAsync(node);
        var wide = node.Children.First(child => child.DisplayName == "wide");

        var result = await graph.ExpandAsync(wide);
        Check("a huge folder is capped", result.IsTruncated);
        Check("the cap is respected", wide.Children.Count == 32);

        var more = await graph.LoadMoreAsync(wide, additionalChildren: 64);
        Check("load more adds the rest", wide.Children.Count == 60);
        Check("load more clears the truncation flag", !more.IsTruncated);
        Check("load more does not duplicate nodes",
            wide.Children.Select(child => child.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 60);
    }

    private static async Task LayoutIsStableAndNonOverlapping(string root)
    {
        Section("layout");
        using var graph = new ViewAllGraphService();
        await graph.InitializeAsync();
        var node = (await graph.AddRootAsync(root))!;
        await graph.ExpandAsync(node);

        Check("children are placed to the right of the parent",
            node.Children.All(child => child.Location.X > node.Location.X));

        var boxes = graph.Nodes.Where(item => item.HasLayoutPosition).Select(item => item.Bounds).ToArray();
        var overlapping = false;
        for (var i = 0; i < boxes.Length && !overlapping; i++)
        {
            for (var j = i + 1; j < boxes.Length; j++)
            {
                if (boxes[i].IntersectsWith(boxes[j]))
                {
                    overlapping = true;
                    break;
                }
            }
        }

        Check("no two nodes overlap", !overlapping);

        var before = node.Children.Select(child => child.Location).ToArray();
        var alpha = node.Children.First(child => child.DisplayName == "alpha");
        await graph.ExpandAsync(alpha);
        Check("expanding a branch does not move existing nodes",
            node.Children.Select(child => child.Location).SequenceEqual(before));

        var manual = new Point(4321, 1234);
        alpha.Location = manual;
        Check("a dragged node is marked manual", alpha.HasManualPosition);
        await graph.RefreshBranchAsync(node);
        var alphaAfter = node.Children.First(child => child.DisplayName == "alpha");
        Check("a dragged node keeps its position across a refresh", alphaAfter.Location == manual);
    }

    private static async Task ViewportCulling(string root)
    {
        Section("viewport culling");
        using var graph = new ViewAllGraphService();
        await graph.InitializeAsync();
        var node = (await graph.AddRootAsync(root))!;
        await graph.ExpandAsync(node);

        var viewport = new ViewAllViewportService();
        Check("far out is the dot level", viewport.GetDetailLevel(0.1) == ViewAllDetailLevel.Dot);
        Check("mid zoom is the glyph level", viewport.GetDetailLevel(0.2) == ViewAllDetailLevel.Glyph);
        Check("closer is the compact level", viewport.GetDetailLevel(0.45) == ViewAllDetailLevel.Compact);
        Check("full zoom is the detailed level", viewport.GetDetailLevel(1) == ViewAllDetailLevel.Detailed);

        var everything = new Rect(-10_000, -10_000, 40_000, 40_000);
        var all = viewport.BuildRenderSet(graph.Nodes, graph.Edges, everything, 1);
        Check("everything visible is realized", all.Nodes.Count == graph.Nodes.Count(item => item.IsTreeVisible));

        var elsewhere = new Rect(500_000, 500_000, 400, 400);
        var none = viewport.BuildRenderSet(graph.Nodes, graph.Edges, elsewhere, 1);
        Check("nothing off screen is realized", none.Nodes.Count == 0);
        Check("logical count ignores culling", none.LogicalNodeCount == all.LogicalNodeCount);

        node.IsSelected = true;
        var withSelection = viewport.BuildRenderSet(graph.Nodes, graph.Edges, elsewhere, 1);
        Check("a selected node is always realized", withSelection.Nodes.Contains(node));
        node.IsSelected = false;

        Check("an edge needs both ends realized",
            all.Edges.All(edge => all.Nodes.Contains(edge.Source) && all.Nodes.Contains(edge.Target)));
    }

    private static async Task DropTargets(string root)
    {
        Section("drop targets");
        using var graph = new ViewAllGraphService();
        await graph.InitializeAsync();
        var node = (await graph.AddRootAsync(root))!;
        await graph.ExpandAsync(node);

        var beta = node.Children.First(child => child.DisplayName == "beta");
        var inside = new Point(beta.Location.X + 10, beta.Location.Y + 10);
        Check("the folder under the cursor wins", graph.FindNearestDropTarget(inside, 96) == beta);

        var nearby = new Point(beta.Location.X - 40, beta.Location.Y + 10);
        Check("a nearby folder is picked up", graph.FindNearestDropTarget(nearby, 96) == beta);

        var faraway = new Point(beta.Location.X - 4000, beta.Location.Y);
        Check("nothing is picked far away", graph.FindNearestDropTarget(faraway, 96) is null);

        var readme = node.Children.First(child => child.DisplayName == "readme.txt");
        var overFile = new Point(readme.Location.X + 4, readme.Location.Y + 4);
        var target = graph.FindNearestDropTarget(overFile, 20);
        Check("a file is never a drop target", target is null || target.IsDirectory);
    }

    private static async Task Persistence(string root)
    {
        Section("persistence");
        var statePath = Path.Combine(Path.GetTempPath(), $"ultraexplorer-smoke-{Guid.NewGuid():N}.json");
        try
        {
            using var graph = new ViewAllGraphService();
            await graph.InitializeAsync();
            var node = (await graph.AddRootAsync(root))!;
            await graph.ExpandAsync(node);
            var alpha = node.Children.First(child => child.DisplayName == "alpha");
            await graph.ExpandAsync(alpha);
            alpha.Location = new Point(999, 555);

            var store = new ViewAllWorkspaceStore(statePath);
            var state = graph.CaptureState(new ViewAllViewportState(new Point(12, 34), 0.75));
            state.ActivePath = alpha.FullPath;
            await store.SaveAsync(state);
            Check("state file is written", File.Exists(statePath));

            var loaded = await store.LoadAsync();
            Check("state round-trips", loaded is not null);
            Check("viewport round-trips", loaded!.ViewportZoom == 0.75 && loaded.ViewportX == 12);
            Check("active path round-trips", ViewAllPath.Equals(loaded.ActivePath, alpha.FullPath));

            using var restored = new ViewAllGraphService();
            await restored.InitializeAsync(loaded);
            Check("restore reopens the saved branches", restored.TryGetNode(alpha.FullPath, out var restoredAlpha) && restoredAlpha.IsExpanded);
            Check("restore reapplies a manual position",
                restored.TryGetNode(alpha.FullPath, out var placed) && placed.Location == new Point(999, 555));
            Check("restore does not scan unopened folders",
                restored.TryGetNode(Path.Combine(root, "wide"), out var wide) && wide.Children.Count == 0);
        }
        finally
        {
            TryDelete(statePath);
        }
    }

    private static async Task Marks(string root)
    {
        Section("folder marks");
        var statePath = Path.Combine(Path.GetTempPath(), $"ultraexplorer-marks-{Guid.NewGuid():N}.json");
        try
        {
            var marks = new FolderMarkService(statePath);
            var raised = 0;
            marks.MarkChanged += (_, _) => raised++;

            marks.SetAccent(root, "#EF5A68");
            marks.SetNote(root, "keep");
            Check("accent is stored", marks.Get(root).AccentHex == "#EF5A68");
            Check("note is stored", marks.Get(root).Note == "keep");
            Check("changes are announced", raised == 2);
            Check("a mark is keyed by the normalised path", marks.Get(root + @"\").AccentHex == "#EF5A68");

            await marks.SaveAsync();
            var reloaded = new FolderMarkService(statePath);
            await reloaded.LoadAsync();
            Check("marks round-trip", reloaded.Get(root).Note == "keep");

            reloaded.SetAccent(root, null);
            Check("clearing the accent falls back", reloaded.GetAccentHex(root, "#123456") == "#123456");
            Check("clearing the accent keeps the note", reloaded.Get(root).Note == "keep");
        }
        finally
        {
            TryDelete(statePath);
        }
    }

    // ---- harness -----------------------------------------------------------

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"== {title} ==");
    }

    private static void Check(string description, bool condition)
    {
        _checks++;
        if (condition)
        {
            Console.WriteLine($"  ok    {description}");
        }
        else
        {
            _failures++;
            Console.WriteLine($"  FAIL  {description}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
