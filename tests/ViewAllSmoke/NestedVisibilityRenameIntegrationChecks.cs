using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task NestedVisibilityRenameIntegrationChecks()
    {
        RunOnSta("visibility and rename integration", async () =>
        {
            await NvrOpenedHiddenChecks();
            await NvrLargeNamedVisibilityChecks();
            await NvrFilterVisibilityChecks();
            await NvrBeaconProvenanceChecks();
            await NvrCameraRenameChecks();
            await NvrDefinitiveRenameMissingChecks();
        });
        return Task.CompletedTask;
    }

    private static NestedCanvas NvrCanvas(NestedTree tree)
    {
        var canvas = new NestedCanvas { Tree = tree, FramesByHandForTests = true, DpiOverride = new DpiScale(1, 1) };
        canvas.Measure(new Size(1200, 800));
        canvas.Arrange(new Rect(0, 0, 1200, 800));
        canvas.UpdateLayout();
        canvas.FitAll(animated: false);
        return canvas;
    }

    private static async Task<NestedTree> NvrTree(FakeDisk disk)
    {
        var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false, PostBackground = null };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        return tree;
    }

    private static async Task NvrOpenedHiddenChecks()
    {
        Section("J056: unrelated hide/show keeps named hidden ancestors open");
        var disk = new FakeDisk();
        disk.Folder(@"Q:\AppData\Roaming\Foo\node_modules");
        disk.Folder(@"Q:\AppData2\Work");
        disk.Folder(@"Q:\Elsewhere");
        disk.Folder(@"Q:\AppData").IsHidden = true;
        using var tree = await NvrTree(disk);
        tree.SetUserHidden([@"Q:\AppData"]);
        var target = await tree.MaterializePathAsync(@"Q:\AppData\Roaming\Foo");
        Check("an explicitly named folder under a user-hidden ancestor is visible", target is not null && NestedTree.IsOnCanvas(target));
        if (target is null) return;
        var canvas = NvrCanvas(tree);
        try
        {
            canvas.FlyTo(target, 0.98, animated: false);
            var before = canvas.CaptureCamera();
            tree.SetUserHidden([@"Q:\AppData", @"Q:\AppData\Roaming\Foo\node_modules"]);
            canvas.RenderAsFrame();
            Check("hiding a child does not re-hide the named ancestor or change the camera",
                NestedTree.IsOnCanvas(target) && canvas.CaptureCamera() == before && !NestedTree.IsOnCanvas(tree.Find(@"Q:\AppData\Roaming\Foo\node_modules")!));
            tree.SetUserHidden([@"Q:\AppData"]);
            canvas.RenderAsFrame();
            Check("showing that child again retains the explicitly opened path", NestedTree.IsOnCanvas(target) && canvas.CaptureCamera() == before);
            tree.SetUserHidden([@"Q:\AppData", @"Q:\AppData2"]);
            Check("a sibling sharing the name prefix is not an ancestor of the open path", NestedTree.IsOnCanvas(target));
            var version = tree.VisibilityVersion;
            tree.SetUserHidden([@"q:\APPDATA", @"q:\appdata2"]);
            Check("repeating the same hidden set is a visibility no-op", tree.VisibilityVersion == version && NestedTree.IsOnCanvas(target));
            tree.HideFromCanvas([@"Q:\AppData2"]);
            Check("explicitly rehiding an unrelated already-hidden folder preserves this named path", NestedTree.IsOnCanvas(target) && canvas.CaptureCamera() == before);
            tree.HideFromCanvas([@"Q:\AppData"]);
            Check("explicitly rehiding the already-hidden ancestor revokes only its own named override", tree.IsUserHidden(@"Q:\AppData") && !NestedTree.IsOnCanvas(target));
            await tree.MaterializePathAsync(target.FullPath);
            tree.SetUserHidden([@"Q:\AppData", @"Q:\AppData2", @"Q:\AppData\Roaming"]);
            Check("explicitly hiding an actual ancestor removes only its named override", !NestedTree.IsOnCanvas(target));
        }
        finally { canvas.Tree = null; }
    }

    private static FakeDisk NvrFilterDisk()
    {
        var disk = new FakeDisk();
        disk.AddFile(@"Q:\Visible", "a.log");
        disk.AddFile(@"Q:\Visible", "secret.log", hidden: true);
        disk.AddFile(@"Q:\Hidden", "b.log");
        disk.Folder(@"Q:\Hidden").IsHidden = true;
        disk.AddFile(@"Q:\User", "c.log");
        return disk;
    }

    private static async Task NvrLargeNamedVisibilityChecks()
    {
        Section("J056: mass hide invalidation is bounded by path depth");
        var disk = new FakeDisk();
        disk.Folder(@"Q:\");
        using var tree = await NvrTree(disk);
        var opened = (HashSet<string>)typeof(NestedTree).GetField("_openedByName", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(tree)!;
        for (var index = 0; index < 10_000; index++) opened.Add($@"Q:\open\p{index:D4}\child");
        opened.Add(@"Q:\open\p0000x\child");
        opened.Add(@"\\synthetic\share\deep\child");
        opened.Add(@"\\synthetic\share2\deep\child");
        opened.Add(@"R:\deep\child");
        string[] roots = [.. Enumerable.Range(0, 5_000).Select(index => $@"Q:\unrelated\h{index:D4}"),
            .. Enumerable.Range(0, 200).Select(index => $@"Q:\open\p{index:D4}"), @"\\synthetic\share", @"R:\"];
        tree.SetUserHidden(roots);
        Check("10k named paths and 5k unrelated hidden roots remove only the 200 actual descendants plus exact UNC/drive descendants",
            opened.Count == 9_802 && !opened.Contains(@"Q:\open\p0000\child") && opened.Contains(@"Q:\open\p0000x\child")
            && !opened.Contains(@"\\synthetic\share\deep\child") && opened.Contains(@"\\synthetic\share2\deep\child") && !opened.Contains(@"R:\deep\child"));
        Check($"mass-hide lookup probes follow path depth, not the product of both set sizes ({tree.OpenedHiddenAncestorProbes:N0} probes)",
            tree.OpenedHiddenAncestorProbes <= 10_004 * 5);
    }

    private static async Task NvrFilterVisibilityChecks()
    {
        Section("J058: visibility changes rejudge counts and navigation");
        var disk = NvrFilterDisk();
        using var tree = await NvrTree(disk);
        var canvas = NvrCanvas(tree);
        try
        {
            canvas.SetFilter("*.log");
            Check("the filter initially counts only two visible log files", canvas.FilterMatches.Count == 2);
            tree.IncludeHidden = true;
            Check("show hidden immediately adds both hidden matches without a read", canvas.FilterMatches.Count == 4);
            tree.IncludeHidden = false;
            Check("hide hidden immediately removes both hidden matches", canvas.FilterMatches.Count == 2);
            tree.SetUserHidden([@"Q:\User"]);
            Check("a user-hidden subtree is removed from the match list", canvas.FilterMatches.SequenceEqual(new[] { @"Q:\Visible\a.log" }));
            string? selected = null;
            canvas.SelectRequested += (path, _) => selected = path;
            Check("Next selects a visible match instead of the hidden subtree", canvas.GoToMatch(1) && selected == @"Q:\Visible\a.log");
            canvas.FolderLoadedForTests(tree.Find(@"Q:\User")!);
            Check("a late read under a hidden ancestor cannot restore its matches", canvas.FilterMatches.Count == 1 && !canvas.FilterMatches.Contains(@"Q:\User\c.log"));
            tree.SetUserHidden([]);
            Check("showing the cached subtree adds its match again", canvas.FilterMatches.Count == 2);
            tree.FileNameFilter = name => name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
            Check("a picker type filter removes cached name-filter matches immediately", canvas.FilterMatches.Count == 0 && !canvas.GoToMatch(1));
            tree.FileNameFilter = null;
            Check("clearing the picker type restores only visible cached matches", canvas.FilterMatches.Count == 2);
            var stamp = canvas.PaletteStampForTests;
            var reads = disk.Reads;
            for (var frame = 0; frame < 8; frame++) canvas.RenderAsFrame();
            Check("unchanged frames do not restart filter judging or reread the tree", canvas.PaletteStampForTests == stamp && disk.Reads == reads);
        }
        finally { canvas.SetFilter(null); canvas.Tree = null; }
    }

    private static async Task NvrBeaconProvenanceChecks()
    {
        Section("J058: filter beacons cannot resurrect hidden matches");
        var disk = NvrFilterDisk();
        using var tree = await NvrTree(disk);
        var canvas = NvrCanvas(tree);
        try
        {
            canvas.SetFilter("*.log");
            var hidden = tree.Find(@"Q:\Hidden")!;
            var reads = disk.Reads;
            canvas.SetBeacons([new(@"Q:\Hidden\b.log", NestedBeaconKind.Filter, Colors.Gold, "hidden filter")]);
            canvas.RenderAsFrame();
            Check("a stale filter-only beacon neither forces its hidden ancestor visible nor places a pin", !NestedTree.IsOnCanvas(hidden) && canvas.BeaconsPlaced == 0 && disk.Reads == reads);
            canvas.SetBeacons([new(@"Q:\Visible\a.log", NestedBeaconKind.Filter, Colors.Gold, "valid filter")]);
            canvas.RenderAsFrame();
            Check("a current filter-only hit is retained by the search beacon target cache", NvrResolvedBeaconCount(canvas) == 1);
            var passes = canvas.BeaconResolutionPasses;
            canvas.SetFilter("*.txt");
            canvas.RenderAsFrame();
            Check("a filter change invalidates cached pure-filter targets before the beacon timer replaces them", canvas.BeaconsPlaced == 0 && canvas.BeaconResolutionPasses == passes + 1);
            canvas.SetBeacons([new(@"Q:\Visible\a.log", NestedBeaconKind.Filter | NestedBeaconKind.Search, Colors.Gold, "also global")]);
            canvas.RenderAsFrame();
            Check("a merged indexed-search hit survives the stale filter source", NvrResolvedBeaconCount(canvas) == 1);
            canvas.ShownLayers = CanvasLayer.All & ~CanvasLayer.Marks;
            canvas.RenderAsFrame();
            Check("global/filter search beacons preserve marks-off visibility", NvrResolvedBeaconCount(canvas) == 1);
            canvas.ShownLayers = CanvasLayer.All;
            canvas.SetBeacons([new(@"Q:\Hidden", NestedBeaconKind.Search, Colors.Gold, "global")]);
            canvas.RenderAsFrame();
            Check("ordinary global search keeps its independent materialization intent", NestedTree.IsOnCanvas(hidden) && NvrResolvedBeaconCount(canvas) == 1);
            canvas.SetBeacons([new(@"Q:\Visible\a.log", NestedBeaconKind.Filter | NestedBeaconKind.Colour, Colors.Red, "also marked")]);
            canvas.RenderAsFrame();
            Check("a marked path is not suppressed merely because its filter match is stale", NvrResolvedBeaconCount(canvas) == 1);
        }
        finally { canvas.SetFilter(null); canvas.Tree = null; }
    }

    private static async Task NvrCameraRenameChecks()
    {
        Section("J063: a camera follows renamed identity, not the old slot");
        var disk = new FakeDisk();
        disk.AddFile(@"Q:\P\Alpha\B\C", "focus.txt", 17);
        disk.AddFile(@"Q:\P\Alpha\B\C", "trace.log", 23);
        disk.AddFile(@"Q:\P\Alpha\B\Sibling", "other.txt");
        disk.AddFile(@"Q:\P\Alpha\Extra", "other.txt");
        disk.AddFile(@"Q:\P\Beta", "beta.txt");
        using var tree = await NvrTree(disk);
        tree.Orders.SetFolder(@"Q:\P\Alpha\B\C", new ItemSort(SortColumn.Size, true));
        var canvas = NvrCanvas(tree);
        try
        {
            var old = tree.Find(@"Q:\P\Alpha\B\C")!;
            canvas.FlyTo(old, 0.98, animated: false);
            var before = canvas.CaptureCamera()!;
            var parent = tree.Find(@"Q:\P")!;
            var reads = disk.Reads;
            NvrMoveDisk(disk, @"Q:\P\Alpha", @"Q:\P\Zeta", [@"Q:\P\Alpha", @"Q:\P\Alpha\B", @"Q:\P\Alpha\B\C", @"Q:\P\Alpha\B\Sibling", @"Q:\P\Alpha\Extra"]);
            tree.OnFolderChanged(parent, new FolderChange(parent.FullPath, ChangeKinds.Structural, Stopwatch.GetTimestamp(), new[] { new RenamePair("Alpha", "Zeta") }, default));
            await tree.RefreshAsync(parent);
            canvas.RenderAsFrame();
            var after = canvas.CaptureCamera()!;
            var renamed = tree.Find(@"Q:\P\Zeta\B\C")!;
            Check("an ancestor rename reanchors a deep view on the same descendant under its new path", after.AnchorPath == @"Q:\P\Zeta\B\C" && canvas.FolderInView == renamed && !NestedTree.IsDetached(renamed));
            Check("the renamed deep anchor retains its exact normalized screen rectangle", after.X == before.X && after.Y == before.Y && after.Width == before.Width);
            Check("following the rename uses the known branch, not a new disk walk", disk.Reads == reads + 1);
            Check("the carried target preserves file metadata, kind and its renamed per-folder sort", renamed.Kind == NestedFolderKind.Folder && renamed.Files.Count == 2 && tree.Orders.SortOf(renamed.FullPath) == new ItemSort(SortColumn.Size, true));
            var rect = canvas.ScreenRectOf(renamed)!.Value;
            var hit = canvas.HitTest(new Point(rect.X + rect.Width / 2, rect.Y + 3));
            Check("the header hit follows the renamed identity rather than Beta", hit is { IsOnHeader: true } && hit.Value.Path == renamed.FullPath);
            canvas.SetFilter("trace.log");
            string? selected = null;
            canvas.SelectRequested += (path, _) => selected = path;
            Check("filter navigation and selection after the rename use the new file path", canvas.GoToMatch(1) && selected == @"Q:\P\Zeta\B\C\trace.log");

            // List the sparse parent normally, then rename the target twice
            // without normalizing between: the weak bridge follows the chain.
            var holder = tree.Find(@"Q:\P\Zeta\B")!;
            await tree.LoadAsync(holder);
            canvas.FlyTo(renamed, 0.98, animated: false);
            foreach (var (from, to) in new[] { ("C", "Renamed"), ("Renamed", "Final") })
            {
                var oldPath = Path.Combine(holder.FullPath, from);
                NvrMoveDisk(disk, oldPath, Path.Combine(holder.FullPath, to), [oldPath]);
                tree.OnFolderChanged(holder, new FolderChange(holder.FullPath, ChangeKinds.Structural, Stopwatch.GetTimestamp(), new[] { new RenamePair(from, to) }, default));
                await tree.RefreshAsync(holder);
            }
            canvas.RenderAsFrame();
            Check("successive direct renames before a frame preserve the final identity", canvas.CaptureCamera()?.AnchorPath == @"Q:\P\Zeta\B\Final" && canvas.FolderInView?.FullPath == @"Q:\P\Zeta\B\Final");

            var gone = tree.Find(@"Q:\P\Zeta\B\Final")!;
            disk.Remove(gone.FullPath);
            await tree.RefreshAsync(holder);
            Check("deletion without a rename pair creates no successor and cannot revive a removed folder", tree.RenameSuccessor(gone) is null && tree.Find(gone.FullPath) is null);
            var weak = NvrWeakBridgeProbe(tree);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Check("the weak bridge does not keep an otherwise unreferenced old node alive", !weak.IsAlive);
        }
        finally { canvas.SetFilter(null); canvas.Tree = null; }
    }

    private static void NvrMoveDisk(FakeDisk disk, string from, string to, string[] paths)
    {
        var contents = paths.Select(path => (Path: path, Directory: disk.Folder(path), Files: disk.Folder(path).Files.ToArray())).ToArray();
        disk.Remove(from);
        foreach (var item in contents)
        {
            var destination = to + item.Path[from.Length..];
            var copy = disk.Folder(destination);
            copy.IsHidden = item.Directory.IsHidden;
            copy.IsReparsePoint = item.Directory.IsReparsePoint;
            copy.Modified = item.Directory.Modified;
            foreach (var file in item.Files) copy.Files.Add(file);
        }
    }

    private static async Task NvrDefinitiveRenameMissingChecks()
    {
        Section("J063: a definitive replacement listing outranks an old camera path");
        var disk = new FakeDisk();
        disk.AddFile(@"Q:\P\Alpha\B\C", "old.txt");
        disk.AddFile(@"Q:\P\Beta", "other.txt");
        using var tree = await NvrTree(disk);
        var canvas = NvrCanvas(tree);
        try
        {
            var old = tree.Find(@"Q:\P\Alpha\B\C")!;
            canvas.FlyTo(old, 0.98, animated: false);
            var parent = tree.Find(@"Q:\P")!;
            NvrMoveDisk(disk, @"Q:\P\Alpha", @"Q:\P\Zeta", [@"Q:\P\Alpha", @"Q:\P\Alpha\B", @"Q:\P\Alpha\B\C"]);
            tree.OnFolderChanged(parent, new FolderChange(parent.FullPath, ChangeKinds.Structural, Stopwatch.GetTimestamp(), new[] { new RenamePair("Alpha", "Zeta") }, default));
            await tree.RefreshAsync(parent);
            disk.Remove(@"Q:\P\Zeta\B\C");
            await tree.LoadAsync(tree.Find(@"Q:\P\Zeta\B")!);
            Check("a definitive new parent listing cannot resurrect a descendant removed since the rename", tree.RenameSuccessor(old) is null && tree.Find(@"Q:\P\Zeta\B\C") is null);
            canvas.RenderAsFrame();
            Check("the camera falls back to the renamed surviving ancestor, never Beta's old slot", canvas.CaptureCamera()?.AnchorPath == @"Q:\P\Zeta\B" && canvas.FolderInView?.FullPath == @"Q:\P\Zeta\B");
        }
        finally { canvas.Tree = null; }
    }

    // Large labeled tiles deliberately carry their own mark instead of a tiny
    // pin. Provenance is a target-resolution contract, independent of that LOD.
    private static int NvrResolvedBeaconCount(NestedCanvas canvas) =>
        ((System.Collections.ICollection)typeof(NestedCanvas).GetField("_resolvedBeacons", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(canvas)!).Count;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference NvrWeakBridgeProbe(NestedTree tree)
    {
        var old = new NestedFolder(@"Q:\unreferenced", "unreferenced", NestedFolderKind.Folder, tree.Root) { IsForgotten = true };
        typeof(NestedTree).GetMethod("RememberRenameSuccessor", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(tree, [old, tree.Root]);
        return new WeakReference(old);
    }
}
