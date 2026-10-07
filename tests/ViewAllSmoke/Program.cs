using System.Diagnostics;
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
internal static partial class Program
{
    private static int _failures;
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--folder-router-fixture")) return FolderRouterFixture.Run(args);
        if (args.Contains("--explorer-observer-probe")) return ExplorerObserverReadOnlyProbe(args).GetAwaiter().GetResult();
        if (args.Contains("--native-dialog-fixture")) return NativeDialogFixture.Run(args);
        // The COM client is a separate job: it talks to a running UltraExplorer
        // through the dialog interfaces rather than exercising them in-process.
        if (args.Any(argument => argument.Equals("--com-client", StringComparison.OrdinalIgnoreCase)))
        {
            return PickerComClient.Run(args);
        }

        var only = Array.FindIndex(args, argument => argument.Equals("--only", StringComparison.OrdinalIgnoreCase));
        if (only >= 0 && only + 1 < args.Length)
        {
            _only = args[only + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

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
        await Group(() => PathHelpers(fixtureRoot));
        await Group(() => LazyExpansion(fixtureRoot));
        await Group(() => CollapseAndReExpand(fixtureRoot));
        await Group(() => RefreshKeepsExpansion(fixtureRoot));
        await Group(() => TruncationAndLoadMore(fixtureRoot));
        await Group(() => LayoutIsStableAndNonOverlapping(fixtureRoot));
        await Group(() => ViewportCulling(fixtureRoot));
        await Group(() => DropTargets(fixtureRoot));
        await Group(() => Persistence(fixtureRoot));
        await Group(DepthScale);
        await Group(RealFolderExpansion);
        await Group(Performance);
        await Group(() => Marks(fixtureRoot));
        await Group(PickerFilters);
        await Group(PickerCommandLine);
        await Group(() => FolderInvocationChecks(fixtureRoot));
        await Group(FolderRouterChecks);
        await Group(FolderRouteCacheChecks);
        await Group(PickerNames);
        await Group(DialogIntegrationChecks);
        await Group(DialogReadFixChecks);
        await Group(WinEShortcutChecks);
        await Group(KnownFolderNavigationChecks);
        await Group(FavoriteLinkChecks);
        await Group(LabelZoomChecks);
        await Group(DeferredViewportReadChecks);
        await Group(() => SparseLiveChecks(fixtureRoot));
        await Group(TreeWatchStabilityChecks);
        await Group(PickerFavoritePreferencesChecks);
        await Group(ExplorerObserverChecks);
        await Group(PickerTileChecks);
        await Group(() => PickerSessionRules(fixtureRoot));
        await Group(() => PickerValidation(fixtureRoot));
        await Group(() => PickerGraphRules(fixtureRoot));
        await Group(() => PickerSessionComChecks(fixtureRoot));
        await Group(() => HiddenBranches(fixtureRoot));
        await Group(() => TidyLayout(fixtureRoot));
        await Group(() => Harness(fixtureRoot));
        await Group(TidyTree);
        await Group(FolderList);
        await Group(FolderListStabilityChecks);
        await Group(ViewModelLifetimeChecks);
        await Group(MainSaveOrderingChecks);
        await Group(AddressBar);
        await Group(() => FolderColours(fixtureRoot));
        await Group(SearchChecks);
        await Group(ProgramTargets);
        await Group(NestedLayoutChecks);
        await Group(NestedTreeChecks);
        await Group(() => NestedReaderChecks(fixtureRoot));
        await Group(NestedCanvasChecks);
        await Group(GpuDeviceChecks);
        await Group(() => LightReveal(fixtureRoot));
        await Group(GpuIconAtlasChecks);
        await Group(GpuTextChecks);
        await Group(GlyphDisposalChecks);
        await Group(GpuRectChecks);
        await Group(GpuLabelChecks);
        await Group(FrameWorkChecks);

        // Each work package's own checks, run from here so that none of them
        // needs this file: empty until the package fills its file in.
        await Group(ReadQueueChecks);
        await Group(IconInboxChecks);
        await Group(LabelCostChecks);
        await Group(FrameScopeChecks);
        await Group(WatchChecks);
        await Group(HubCoalesceChecks);
        await Group(LiveUpdateChecks);
        await Group(TreeLiveWatchChecks);
        await Group(CameraMotionChecks);
        await Group(NestedCameraChecks);
        await Group(TransitionChecks);
        await Group(NestedSelectionChecks);
        await Group(ItemSelectionChecks);
        await Group(SelectionSafetyChecks);
        await Group(FolderOrderChecks);
        await Group(LayerChecks);
        await Group(ShellMenuChecks);
        await Group(ReviewFixChecks);
        await Group(DialogRuntimeReviewChecks);
        await Group(ViewAllServiceChecks);
        await Group(TreeCoreReviewChecks);
        await Group(CanvasMiscReviewChecks);
        await Group(SplitPaneChecks);
        await Group(MarksPersistenceChecks);
        await Group(WorkspaceSaveRaceChecks);
        await Group(DiagnosticsStateChecks);
        await Group(ViewModelReviewChecks);
        await Group(LoadMoreRefreshChecks);
        await Group(AddressReviewChecks);
        await Group(IconReviewFixChecks);
        await Group(ExplorerObserverRegReviewChecks);
        await Group(MenuFileOpsReviewChecks);
        await Group(ArchiveDropChecks);
        await Group(MainViewModelReviewChecks);
        await Group(MainWindowReviewChecks);
        await Group(TextGlyphReviewChecks);
        await Group(ExplorerRoutingReviewChecks);
        await Group(ListSearchReviewChecks);
        await Group(CrossClusterRound2Checks);
        await Group(CanvasMotionReview2Checks);
        await Group(SelectionReview2Checks);
        await Group(IconGlyphRound2Checks);
        await Group(TreeReadingFixChecks);
        await Group(LiveWatchFix2Checks);
        await Group(LiveWatchFix2WindowChecks);
        await Group(GraphReviewFixChecks);
        await Group(ViewAllIoReviewChecks);
        await Group(ViewAllVmRound2Checks);
        await Group(FolderListRound2Checks);
        await Group(SearchAddressReview2Checks);
        await Group(ExternalDropReleaseChecks);
        await Group(WindowAppReviewChecks);
        await Group(PanesRound2Checks);
        await Group(PickerDialogsReviewChecks);
        await Group(PickerDialogsWindowReviewChecks);
        await Group(PickerDialogsAgentReviewChecks);
        await Group(FilterTileMatchChecks);
        await Group(FilterRereadChecks);
        await Group(FilterStepChecks);
        await Group(TypeFilterNoteChecks);
        await Group(TrailParentChecks);
        await Group(GpuArrivalChecks);
        await Group(ExplorerReview2Checks);
        await Group(ReleasePackagingChecks);
        await Group(MarkLookupCostChecks);
        await Group(DrivePaneChecks);
        await Group(SlowPlacesChecks);
        await Group(SharedMarksChecks);
        await Group(CarriedMarksChecks);
        await Group(Review2IntegrationChecks);
        await Group(ShellMenuReview2IntegrationChecks);
        await Group(FastEntryPointChecks);
        await Group(BeaconIntegrationChecks);
        await Group(ReadLaneIntegrationChecks);
        await Group(LongPathShellOperationChecks);
        await Group(ExplorerIntegrationFollowupChecks);
        await Group(FileCommandReview2Checks);
        await Group(NestedVisibilityRenameIntegrationChecks);
        await Group(FocusSelectionChecks);
        await Group(BatchCommandLineChecks);
        await Group(PublicationLifecycleChecks);
        await Group(RefreshIdentityConsumerChecks);
        await Group(ArchiveChecks);

        // Last: the Settings window needs the app's theme, which means the
        // app itself, and a process can only ever have the one.
        await Group(SettingsChecks);
        await Group(PickerSessionComWindowChecks);
        await Group(ProxyPaneChecks);
        await Group(CrossClusterReviewChecks);
        await Group(TopReviewFixChecks);
        await Group(DriveGateChecks);
    }

    /// <summary>
    /// Runs one group of checks, or skips it when the command line names
    /// others: <c>--only NestedSelectionChecks,FolderOrderChecks</c> runs
    /// just the groups whose call mentions one of the names, for going over
    /// one piece of work again without the whole suite.
    /// </summary>
    private static Task Group(Func<Task> group, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(group))] string name = "") =>
        _only is null || _only.Any(wanted => name.Contains(wanted, StringComparison.OrdinalIgnoreCase)) ? group() : Task.CompletedTask;

    private static string[]? _only;

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
        Check("a folder named like a variable keeps its name",
            ViewAllPath.Normalize(@"C:\data\%USERNAME%") == @"C:\data\%USERNAME%");
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

        var added = Path.Combine(root, "gamma");
        Directory.CreateDirectory(added);
        try
        {
            await graph.RefreshBranchAsync(node);
            Check("refresh picks up a new folder", node.Children.Any(child => child.DisplayName == "gamma"));

            var alphaAfter = node.Children.First(child => child.DisplayName == "alpha");
            Check("refresh keeps the branch expanded", alphaAfter.IsExpanded);
            Check("refresh restores grandchildren", alphaAfter.Children.Count == 3);

            // A refresh that finds a new folder has to make room for it, so
            // coordinates move.  A refresh that finds nothing new must leave the
            // canvas alone - that is what makes F5 safe to press.
            var settled = graph.Nodes
                .Where(item => item.IsTreeVisible && item.HasLayoutPosition)
                .ToDictionary(item => item.FullPath, item => item.Location);
            await graph.RefreshBranchAsync(node);
            Check("refreshing again moves nothing",
                graph.Nodes
                    .Where(item => item.IsTreeVisible && settled.ContainsKey(item.FullPath))
                    .All(item => (item.Location - settled[item.FullPath]).Length < 1e-9));
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

        Check("children are placed below the parent",
            node.Children.All(child => child.Location.Y > node.Location.Y));
        Check("a small folder stays on one row",
            node.Children.Select(child => child.Location.Y).Distinct().Count() == 1);

        var childWidth = node.Children[0].Width;
        var rowCentre = (node.Children.Min(child => child.Location.X)
                         + node.Children.Max(child => child.Location.X)) / 2 + childWidth / 2;
        Check("the row is centred on the parent",
            Math.Abs(rowCentre - (node.Location.X + node.Width / 2)) < 1);

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

        // Sixty entries is past the point where one line stops being readable.
        var wide = node.Children.First(child => child.DisplayName == "wide");
        await graph.ExpandAsync(wide);
        var wideRows = wide.Children.Select(child => child.Location.Y).Distinct().Count();
        Check("a large folder wraps into rows", wideRows > 1);
        Check("its rows are full before the next one starts",
            wide.Children.GroupBy(child => child.Location.Y).Count() == wideRows
            && wide.Children.GroupBy(child => child.Location.Y).Max(row => row.Count())
               <= (wide.Children.Count + wideRows - 1) / wideRows);
        Check("the block is centred on the parent",
            Math.Abs((wide.Children.Min(child => child.Location.X)
                      + wide.Children.Max(child => child.Location.X)) / 2
                     + wide.Children[0].Width / 2
                     - (wide.Location.X + wide.Width / 2)) < 1);
        Check("the block sits below its parent",
            wide.Children.All(child => child.Location.Y > wide.Location.Y));

        var alpha = node.Children.First(child => child.DisplayName == "alpha");
        var openedFrom = alpha.Location;
        var shift = new Vector(0, 0);
        var shifts = 0;
        graph.LayoutShifted += delta =>
        {
            shift = delta;
            shifts++;
        };

        await graph.ExpandAsync(alpha);

        // Opening a folder has to make room for what came out of it, so its
        // siblings move: that is what keeps the tree tidy rather than letting a
        // branch wander off to wherever there happened to be a gap.  What must
        // not happen is the thing under the cursor jumping, so the graph reports
        // how far it carried the folder and the canvas pans by the same amount.
        Check("opening a folder reports the shift at most once", shifts <= 1);
        Check("and reports it exactly",
            (alpha.Location - (openedFrom + shift)).Length < 0.01);

        var overlappingAfterExpand = false;
        var afterExpand = graph.Nodes.Where(item => item.IsTreeVisible && item.HasLayoutPosition).ToArray();
        for (var i = 0; i < afterExpand.Length && !overlappingAfterExpand; i++)
        {
            for (var j = i + 1; j < afterExpand.Length; j++)
            {
                if (afterExpand[i].Bounds.IntersectsWith(afterExpand[j].Bounds))
                {
                    overlappingAfterExpand = true;
                    break;
                }
            }
        }

        Check("and the tree is still tidy afterwards", !overlappingAfterExpand);

        // Anything caching drawn geometry has to hear about a move, otherwise a
        // drag only shows up after the next pan or zoom.
        var layoutSignals = 0;
        graph.LayoutChanged += () => layoutSignals++;

        // Dragging a node has to carry its whole subtree, so dragging a drive
        // drags its tree.
        var beforeDrag = alpha.Children.Select(child => child.Location).ToArray();
        var siblingsBefore = node.Children
            .Where(child => child != alpha)
            .ToDictionary(child => child.FullPath, child => child.Location);
        var dragDelta = new Vector(500, 300);
        alpha.Location = alpha.Location + dragDelta;
        // The delta is recovered by subtracting two large coordinates, so the
        // carried positions land within rounding of the exact offset.
        Check("dragging a node moves its children by the same delta",
            alpha.Children
                .Select((child, i) => (child.Location - (beforeDrag[i] + dragDelta)).Length)
                .All(error => error < 1e-6));
        Check("carried children keep their automatic flag",
            alpha.Children.All(child => !child.HasManualPosition));
        Check("a drag announces a layout change for the node and everything it carried",
            layoutSignals >= 1 + alpha.Children.Count);
        Check("siblings of the dragged node stay put",
            node.Children.Where(child => child != alpha)
                .All(child => child.Location == siblingsBefore[child.FullPath]));

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
        Check("the furthest zoom aggregates into clusters",
            viewport.GetDetailLevel(0.04) == ViewAllDetailLevel.Cluster);
        Check("far out is the dot level", viewport.GetDetailLevel(0.1) == ViewAllDetailLevel.Dot);
        Check("mid zoom is the glyph level", viewport.GetDetailLevel(0.2) == ViewAllDetailLevel.Glyph);
        Check("closer is the compact level", viewport.GetDetailLevel(0.45) == ViewAllDetailLevel.Compact);
        Check("full zoom is the detailed level", viewport.GetDetailLevel(1) == ViewAllDetailLevel.Detailed);
        Check("batched drawing takes over below 30%",
            ViewAllViewportService.UsesOverview(viewport.GetDetailLevel(0.2))
            && !ViewAllViewportService.UsesOverview(viewport.GetDetailLevel(0.45)));

        var visible = graph.Nodes.Count(item => item.IsTreeVisible);
        var everything = new Rect(-10_000, -10_000, 60_000, 60_000);
        var all = viewport.BuildRenderSet(graph.Index, graph.Edges, [], everything, 1, visible);
        Check("everything visible is realized", all.Nodes.Count == visible);

        var elsewhere = new Rect(500_000, 500_000, 400, 400);
        var none = viewport.BuildRenderSet(graph.Index, graph.Edges, [], elsewhere, 1, visible);
        Check("nothing off screen is realized", none.Nodes.Count == 0);
        Check("logical count ignores culling", none.LogicalNodeCount == all.LogicalNodeCount);

        var withSelection = viewport.BuildRenderSet(graph.Index, graph.Edges, [node], elsewhere, 1, visible);
        Check("a selected node is always realized", withSelection.Nodes.Contains(node));

        var farOut = viewport.BuildRenderSet(graph.Index, graph.Edges, [], everything, 0.1, visible);
        Check("the overview realizes no controls at all", farOut.Nodes.Count == 0);

        Check("an edge needs both ends realized",
            all.Edges.All(edge => all.Nodes.Contains(edge.Source) && all.Nodes.Contains(edge.Target)));
    }

    /// <summary>
    /// A deeper folder has to take proportionally less canvas, halving every
    /// eight levels rather than dropping off a cliff at some threshold.
    /// </summary>
    private static Task DepthScale()
    {
        Section("depth scale");

        static double ScaleAt(int depth)
        {
            var entry = new ViewAllEntryDescriptor(
                $@"C:\depth\{depth}",
                $"depth-{depth}",
                ViewAllEntryKind.Folder,
                false,
                false,
                null,
                DateTime.UnixEpoch);
            return new ViewAllNodeViewModel(entry, depth).Scale;
        }

        Check("a root is full size", Math.Abs(ScaleAt(0) - 1) < 1e-9);
        Check("depth 8 is half a root", Math.Abs(ScaleAt(8) - 0.5) < 1e-9);
        Check("depth 16 is half of depth 8", Math.Abs(ScaleAt(16) - ScaleAt(8) / 2) < 1e-9);
        Check("depth 24 is half of depth 16", Math.Abs(ScaleAt(24) - ScaleAt(16) / 2) < 1e-9);
        Check("depth 32 is a sixteenth", Math.Abs(ScaleAt(32) - 0.0625) < 1e-9);
        Check("shrinking stops past 32", Math.Abs(ScaleAt(64) - ScaleAt(32)) < 1e-9);
        Check("the shrink is gradual, not stepped",
            ScaleAt(0) > ScaleAt(3) && ScaleAt(3) > ScaleAt(5) && ScaleAt(5) > ScaleAt(8));

        var entry = new ViewAllEntryDescriptor(
            @"C:\depth\node",
            "node",
            ViewAllEntryKind.Folder,
            false,
            false,
            null,
            DateTime.UnixEpoch);
        var deep = new ViewAllNodeViewModel(entry, 8);
        deep.SetAutomaticLocation(new Point(0, 0));
        Check("bounds follow the scale",
            Math.Abs(deep.Bounds.Width - ViewAllNodeViewModel.DefaultWidth / 2) < 1e-9
            && Math.Abs(deep.Bounds.Height - ViewAllNodeViewModel.DefaultHeight / 2) < 1e-9);
        Check("anchors follow the scale",
            Math.Abs(deep.InputAnchor.X - deep.Width / 2) < 1e-9
            && Math.Abs(deep.OutputAnchor.Y - deep.Height) < 1e-9);

        return Task.CompletedTask;
    }

    private static async Task RealFolderExpansion()
    {
        Section("large real folder");
        var big = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        if (!Directory.Exists(big))
        {
            Check("no system folder to measure on this machine", true);
            return;
        }

        using var graph = new ViewAllGraphService();
        await graph.InitializeAsync();
        var node = (await graph.AddRootAsync(big))!;

        var watch = Stopwatch.StartNew();
        await graph.ExpandAsync(node);
        watch.Stop();

        Check("a real system folder expands in one level", node.Children.Count > 1_000);
        Report($"expanded {node.Children.Count:N0} real entries", watch.ElapsedMilliseconds, 8_000);
        Check("no descendant was scanned", node.Children.All(child => child.Children.Count == 0));
    }

    /// <summary>
    /// The whole point of the spatial index and the batched overview is that a
    /// graph far larger than any real folder stays interactive, so the numbers
    /// are asserted rather than eyeballed.
    /// </summary>
    private static Task Performance()
    {
        Section("performance");

        const int nodeCount = 300_000;
        var index = new ViewAllSpatialIndex();
        var nodes = new List<ViewAllNodeViewModel>(nodeCount);

        var build = Stopwatch.StartNew();
        for (var i = 0; i < nodeCount; i++)
        {
            var entry = new ViewAllEntryDescriptor(
                $@"C:\synthetic\{i}",
                $"node-{i}",
                i % 3 == 0 ? ViewAllEntryKind.Folder : ViewAllEntryKind.File,
                IsHidden: false,
                IsReparsePoint: false,
                SizeBytes: 0,
                ModifiedUtc: DateTime.UnixEpoch);
            var node = new ViewAllNodeViewModel(entry, depth: 1);
            node.SetAutomaticLocation(new Point(i % 800 * 214, i / 800 * 140));
            index.AddOrUpdate(node);
            nodes.Add(node);
        }

        build.Stop();
        Report($"indexed {nodeCount:N0} nodes", build.ElapsedMilliseconds, 15_000);

        var viewportRect = new Rect(40_000, 20_000, 1920, 1080);
        var hits = new List<ViewAllNodeViewModel>();
        var query = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
        {
            hits.Clear();
            index.Query(viewportRect, hits);
        }

        query.Stop();
        Check("a viewport query finds its nodes", hits.Count is > 0 and < 2_000);
        Report("100 viewport queries", query.ElapsedMilliseconds, 500);

        var viewport = new ViewAllViewportService();
        var renderSet = Stopwatch.StartNew();
        ViewAllRenderSet? last = null;
        for (var i = 0; i < 100; i++)
        {
            last = viewport.BuildRenderSet(index, [], [], viewportRect, 1, nodeCount);
        }

        renderSet.Stop();
        Check("the render set stays small", last is not null && last.Nodes.Count <= 900);
        Report("100 render sets over 300k nodes", renderSet.ElapsedMilliseconds, 2_000);

        // Moving a root drags its subtree; the index has to keep up per frame.
        var move = Stopwatch.StartNew();
        for (var i = 0; i < 20_000; i++)
        {
            nodes[i].SetAutomaticLocation(new Point(nodes[i].Location.X + 1, nodes[i].Location.Y + 1));
        }

        move.Stop();
        Report("20k node moves reindexed", move.ElapsedMilliseconds, 3_000);

        // Layout of one enormous folder: quadratic collision testing would make
        // this minutes rather than milliseconds.
        var layout = new ViewAllLayoutService();
        var layoutIndex = new ViewAllSpatialIndex();
        var parentEntry = new ViewAllEntryDescriptor(
            @"C:\synthetic",
            "synthetic",
            ViewAllEntryKind.Folder,
            false,
            false,
            null,
            DateTime.UnixEpoch);
        var parent = new ViewAllNodeViewModel(parentEntry, 0)
        {
            IsExpanded = true,
            LocationObserver = layoutIndex.AddOrUpdate
        };

        var children = new List<ViewAllNodeViewModel>(20_000);
        for (var i = 0; i < 20_000; i++)
        {
            var entry = new ViewAllEntryDescriptor(
                $@"C:\synthetic\child-{i}",
                $"child-{i}",
                ViewAllEntryKind.File,
                false,
                false,
                0,
                DateTime.UnixEpoch);
            var child = new ViewAllNodeViewModel(entry, 1, parent)
            {
                LocationObserver = layoutIndex.AddOrUpdate
            };
            children.Add(child);
            parent.Children.Add(child);
        }

        var placing = Stopwatch.StartNew();
        layout.Arrange([parent]);
        placing.Stop();
        Report("laid out 20k children", placing.ElapsedMilliseconds, 4_000);
        Check("every child was placed", children.All(child => child.HasLayoutPosition));

        // Every expansion lays the whole tree out again, so the second pass is
        // the one that matters, and it has to land on exactly the same picture:
        // otherwise opening one folder would shuffle every other one.
        var settled = children.Select(child => child.Location).ToArray();
        var second = Stopwatch.StartNew();
        layout.Arrange([parent]);
        second.Stop();
        Report("laid them out again", second.ElapsedMilliseconds, 4_000);
        Check("and put every one back where it was",
            children.Select((child, index) => (child.Location - settled[index]).Length).All(error => error < 1e-9));

        // A row grows with the child count; a block grows with its square root.
        // Neither side of twenty thousand children may be twenty thousand long.
        var columns = children.Select(child => child.Location.X).Distinct().Count();
        var rows = children.Select(child => child.Location.Y).Distinct().Count();
        Check("children wrap into a block", columns > 1 && rows > 1);
        Check("neither side grows with the child count",
            columns < 4 * Math.Sqrt(children.Count) && rows < 4 * Math.Sqrt(children.Count));

        var blockWidth = children.Max(child => child.Location.X) - children.Min(child => child.Location.X);
        var blockHeight = children.Max(child => child.Location.Y) - children.Min(child => child.Location.Y);
        Check("the block is wider than tall, but not a line",
            blockWidth > blockHeight && blockWidth < blockHeight * 8);
        Check("no child of the block overlaps another",
            children.All(child => !layoutIndex.IsOccupied(child.Bounds, child)));

        return Task.CompletedTask;
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

        // Siblings share a row, so probe below the node where nothing else sits.
        var nearby = new Point(
            beta.Location.X + ViewAllNodeViewModel.DefaultWidth / 2,
            beta.Location.Y + ViewAllNodeViewModel.DefaultHeight + 20);
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

            // A share that is offline for one session is still there for the next.
            var offline = Path.Combine(root, $"offline-share-{Guid.NewGuid():N}");
            using var once = new ViewAllGraphService();
            await once.InitializeAsync(new ViewAllWorkspaceState { ExtraRoots = [offline] });
            Check("a root out of reach at startup is written back, to be tried again",
                once.CaptureState(new ViewAllViewportState(new Point(0, 0), 1)).ExtraRoots
                    .Contains(offline, StringComparer.OrdinalIgnoreCase));

            // Nulls where lists and paths belong, as a file edited by hand can
            // have them, are passed over rather than stopping the canvas.
            using var sparse = new ViewAllGraphService();
            await sparse.InitializeAsync(new ViewAllWorkspaceState
            {
                Nodes = [null!, new ViewAllNodeState(null!, 0, 0, false, true)],
                ExtraRoots = null!,
                HiddenPaths = null!
            });
            Check("a workspace holding nulls still opens", sparse.Roots.Count > 0);

            // A damaged file is set aside before the first save can write over it.
            File.WriteAllText(statePath, "{ this is not json");
            Check("a damaged workspace file loads as none", await store.LoadAsync() is null);
            var setAside = Directory.GetFiles(Path.GetDirectoryName(statePath)!, Path.GetFileName(statePath) + ".corrupt-*");
            Check("and is set aside rather than written over", setAside.Length == 1 && !File.Exists(statePath));
            foreach (var file in setAside)
            {
                TryDelete(file);
            }
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

            // Nulls in a file edited by hand are no marks, not a crash.
            File.WriteAllText(statePath, """{ "C:\\nothing": null, "C:\\half": { "accentHex": null, "note": "kept" } }""");
            var edited = new FolderMarkService(statePath);
            await edited.LoadAsync();
            Check("a null mark in the file is passed over", edited.Get(@"C:\nothing").IsEmpty && edited.Get(@"C:\half").Note == "kept");

            // A damaged file is set aside: the next save would otherwise write
            // an empty set over every note in it.
            File.WriteAllText(statePath, "{ this is not json");
            var damaged = new FolderMarkService(statePath);
            await damaged.LoadAsync();
            var setAside = Directory.GetFiles(Path.GetDirectoryName(statePath)!, Path.GetFileName(statePath) + ".corrupt-*");
            Check("a damaged marks file is set aside rather than written over", setAside.Length == 1 && !File.Exists(statePath));
            foreach (var file in setAside)
            {
                TryDelete(file);
            }
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

    private static void Report(string description, long elapsedMilliseconds, long budgetMilliseconds)
    {
        Check($"{description}: {elapsedMilliseconds} ms (budget {budgetMilliseconds} ms)",
            elapsedMilliseconds <= budgetMilliseconds);
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
