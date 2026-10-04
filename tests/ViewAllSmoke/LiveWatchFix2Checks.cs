using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// What the second review found in the nested tree's and the hub's handling
/// of changes on disk (cluster k-live-watch): a deleted subtree whose gone
/// folders every apply went through (J042), a deleted and remade subtree read
/// again whole, off screen, ahead of what is on screen (J043), more than eight
/// sub-folders hidden at once never shown hidden (J044), a batch rename that
/// kept the marks of eight items alone (J062), a folder deleted with its
/// contents drawn as a red cell first (J090), and a renamed folder's order
/// lost below a folder never listed (J144).  A tree drawn by hand - a frame,
/// then the folders a canvas would draw - stands for the canvas.
/// </summary>
internal static partial class Program
{
    private static Task LiveWatchFix2Checks()
    {
        Section("live watch, round 2");
        RunOnSta("a deleted subtree's gone folders", Fix2GoneIndexChecks);
        RunOnSta("a subtree deleted and made anew", Fix2RemadeSubtreeChecks);
        RunOnSta("a folder deleted with its contents", Fix2GoneWithContentsChecks);
        RunOnSta("an order below a folder never listed", Fix2PartialRenameOrderChecks);
        RunOnSta("a dozen sub-folders hidden at once", Fix2ManyHiddenChecks);
        return Task.CompletedTask;
    }

    /// <summary>The folders a tree keeps as gone by their own path, by reflection: the count is what every apply used to copy and go through.</summary>
    private static int Fix2GoneCount(NestedTree tree)
    {
        var gone = typeof(NestedTree).GetField("_goneByPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tree)!;
        return (int)gone.GetType().GetProperty("Count")!.GetValue(gone)!;
    }

    /// <summary>
    /// npm ci, cargo clean, rm -rf of a read subtree whose parent is off
    /// screen: every read folder of it is heard to go, deepest first, and is
    /// kept as gone until the parent is read again.  Every apply anywhere
    /// then copied and went through all of them (J042).
    /// </summary>
    private static async Task Fix2GoneIndexChecks()
    {
        var disk = new FakeDisk();
        disk.AddFiles(@"Q:\top\keep", 3, "k");
        for (var outer = 0; outer < 40; outer++)
        {
            for (var inner = 0; inner < 50; inner++)
            {
                disk.Folder($@"Q:\top\big\d{outer:D2}\e{inner:D2}");
            }
        }

        using var tree = new NestedTree(disk.Read);
        tree.SetRoots([new NestedRoot(@"Q:\top", "Q", NestedFolderKind.Drive)]);
        var top = tree.Root.Children.Single();
        await tree.LoadAsync(top);
        var keep = NestedTree.FindChild(top, "keep")!;
        var big = NestedTree.FindChild(top, "big")!;
        await tree.LoadAsync(keep);
        await tree.LoadAsync(big);
        var loaded = new List<NestedFolder> { big };
        foreach (var outer in big.AllChildren)
        {
            await tree.LoadAsync(outer);
            loaded.Add(outer);
        }

        foreach (var outer in big.AllChildren)
        {
            await Task.WhenAll(outer.AllChildren.Select(inner => tree.LoadAsync(inner)));
            loaded.AddRange(outer.AllChildren);
        }

        Check($"the subtree is read ({loaded.Count} folders)", loaded.Count == 2_041 && loaded.All(folder => folder.IsLoaded));

        async Task<(double Microseconds, double Bytes)> Applies(int count)
        {
            await tree.RefreshAsync(keep);
            var clock = Stopwatch.StartNew();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < count; index++)
            {
                await tree.RefreshAsync(keep);
            }

            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            return (clock.Elapsed.TotalMicroseconds / count, (double)bytes / count);
        }

        var quiet = await Applies(300);

        // Off screen: nothing drawn in the last frames, the camera nowhere.
        for (var frame = 0; frame <= NestedTree.ExpireAfterFrames; frame++)
        {
            tree.BeginFrame();
        }

        disk.Remove(@"Q:\top\big");
        foreach (var folder in loaded.OrderByDescending(folder => folder.FullPath.Count(character => character == '\\')))
        {
            tree.OnFolderChanged(folder, new FolderChange(folder.FullPath, ChangeKinds.Gone, Stopwatch.GetTimestamp(), default, default));
        }

        // The looks at whether each name was taken again, off this thread, come back first.
        await Task.Delay(500);
        var kept = Fix2GoneCount(tree);
        var after = await Applies(300);
        Console.WriteLine($"  note  per apply: {quiet.Microseconds:F1} us, {quiet.Bytes:F0} B before; {after.Microseconds:F1} us, {after.Bytes:F0} B with {kept} folders kept as gone");
        Check($"a deleted subtree of {loaded.Count} read folders is kept as gone by its top folder alone ({kept})", kept == 1);
        Check($"and an apply anywhere costs what it did before ({after.Bytes - quiet.Bytes:F0} B more per apply)", after.Bytes - quiet.Bytes < 1_024);

        await tree.RefreshAsync(top);
        await Task.Delay(100);
        Check($"the parent read again takes the subtree off, and nothing is kept as gone ({Fix2GoneCount(tree)})",
            NestedTree.IsDetached(big) && Fix2GoneCount(tree) == 0);
    }

    /// <summary>
    /// npm ci, a clean build: a read subtree deleted, every folder of it heard
    /// to go, and made anew under the same names.  The parent on screen is
    /// read again and still has the name, so the top folder is read again as
    /// the new one - and every level's listing used to ask about the next,
    /// so the whole old subtree was read again, off screen, each read put
    /// ahead of the folders on screen (J043).
    /// </summary>
    private static async Task Fix2RemadeSubtreeChecks()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "UltraExplorerRemade", Guid.NewGuid().ToString("N"));
        var topPath = Path.Combine(baseDirectory, "top");
        var modules = Path.Combine(topPath, "node_modules");
        var shown = Path.Combine(topPath, "shown");
        void MakeModules()
        {
            for (var outer = 0; outer < 20; outer++)
            {
                for (var inner = 0; inner < 20; inner++)
                {
                    Directory.CreateDirectory(Path.Combine(modules, $"p{outer:D2}", $"q{inner:D2}"));
                }
            }
        }

        MakeModules();
        Directory.CreateDirectory(Path.Combine(shown, "inside"));
        var readsBelow = 0;
        var below = modules + Path.DirectorySeparatorChar;
        try
        {
            using var tree = new NestedTree((path, token) =>
            {
                if (path.StartsWith(below, StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref readsBelow);
                }

                return NestedDirectoryReader.Read(path, token);
            });
            tree.SetRoots([new NestedRoot(baseDirectory, "N", NestedFolderKind.Drive)]);
            var top = await tree.RevealAsync(topPath);
            if (top is null || !ViewAllPath.Equals(top.FullPath, topPath))
            {
                Check("the remade-subtree fixture is in the tree", false);
                return;
            }

            await tree.LoadAsync(top);
            var nm = NestedTree.FindChild(top, "node_modules")!;
            var shownFolder = NestedTree.FindChild(top, "shown")!;
            await tree.LoadAsync(nm);
            await tree.LoadAsync(shownFolder);
            var loaded = new List<NestedFolder> { nm };
            foreach (var outer in nm.AllChildren)
            {
                await tree.LoadAsync(outer);
                loaded.Add(outer);
            }

            foreach (var outer in nm.AllChildren)
            {
                await Task.WhenAll(outer.AllChildren.Select(inner => tree.LoadAsync(inner)));
                loaded.AddRange(outer.AllChildren);
            }

            // What is on screen: the parent and a folder beside the subtree.
            void Draw()
            {
                tree.BeginFrame();
                tree.Request(top, 900);
                tree.Request(shownFolder, 400);
            }

            for (var frame = 0; frame <= NestedTree.ExpireAfterFrames; frame++)
            {
                Draw();
            }

            Check($"the subtree is read ({loaded.Count} folders)", loaded.Count == 421 && loaded.All(folder => folder.IsLoaded));

            Directory.Delete(modules, recursive: true);
            foreach (var folder in loaded.OrderByDescending(folder => folder.FullPath.Count(character => character == '\\')))
            {
                tree.OnFolderChanged(folder, new FolderChange(folder.FullPath, ChangeKinds.Gone, Stopwatch.GetTimestamp(), default, default));
            }

            MakeModules();
            Interlocked.Exchange(ref readsBelow, 0);
            tree.OnFolderChanged(top, new FolderChange(top.FullPath, ChangeKinds.Structural, Stopwatch.GetTimestamp(), default, default));
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 2_000)
            {
                Draw();
                await Task.Delay(8);
            }

            var outerFolder = NestedTree.FindChild(nm, "p00");
            Check($"a subtree deleted and made anew is not read again folder by folder while off screen ({Volatile.Read(ref readsBelow)} of {loaded.Count - 1} read)",
                Volatile.Read(ref readsBelow) == 0);
            Check("its folders stay, marked to be read again when drawn",
                !NestedTree.IsDetached(nm) && outerFolder is { IsLoaded: true, NeedsRefresh: true }
                && outerFolder.AllChildren.Length > 0 && outerFolder.AllChildren[0].NeedsRefresh);

            // Drawn, a folder of it is read as the folder there now.
            var drawnRead = Stopwatch.StartNew();
            var read = false;
            while (drawnRead.ElapsedMilliseconds < 3_000 && !read)
            {
                Draw();
                if (outerFolder is not null)
                {
                    tree.Request(outerFolder, 300);
                }

                read = outerFolder is { NeedsRefresh: false, IsLoaded: true };
                await Task.Delay(8);
            }

            Check("and one drawn is read again", read);
        }
        finally
        {
            TryDelete(baseDirectory);
        }
    }

    /// <summary>
    /// Shift+Del, or rm -rf, of a folder on screen: it hears its contents go
    /// and its own path go in one change.  Read for its contents, it failed
    /// and was drawn as a red cell saying it no longer exists until its
    /// parent's listing took it off (J090).
    /// </summary>
    private static async Task Fix2GoneWithContentsChecks()
    {
        var disk = new FakeDisk();
        disk.AddFiles(@"Q:\del\P\X", 5, "x");
        disk.Folder(@"Q:\del\P\Y");
        using var tree = new NestedTree(disk.Read);
        tree.SetRoots([new NestedRoot(@"Q:\del", "Q", NestedFolderKind.Drive)]);
        var top = tree.Root.Children.Single();
        await tree.LoadAsync(top);
        var parent = NestedTree.FindChild(top, "P")!;
        await tree.LoadAsync(parent);
        var gone = NestedTree.FindChild(parent, "X")!;
        await tree.LoadAsync(gone);

        void Draw()
        {
            tree.BeginFrame();
            tree.Request(parent, 900);
            if (!NestedTree.IsDetached(gone))
            {
                tree.Request(gone, 400);
            }
        }

        for (var frame = 0; frame <= NestedTree.ExpireAfterFrames; frame++)
        {
            Draw();
        }

        // The parent's listing takes its moment, as a real one does: a read
        // of the folder itself, failing at once, is applied before it.
        disk.Hook = (path, _) =>
        {
            if (path.Equals(@"Q:\del\P", StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(150);
            }

            return null;
        };
        disk.Remove(@"Q:\del\P\X");
        var rereads = tree.LiveRereadsAsked;
        tree.OnFolderChanged(gone, new FolderChange(gone.FullPath, ChangeKinds.Structural | ChangeKinds.Gone, Stopwatch.GetTimestamp(), default, default));
        var asked = tree.LiveRereadsAsked - rereads;
        var failedSeen = false;
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 3_000 && !NestedTree.IsDetached(gone))
        {
            Draw();
            failedSeen |= gone.LoadState == NestedLoadState.Failed;
            await Task.Delay(4);
        }

        failedSeen |= gone.LoadState == NestedLoadState.Failed;
        Check($"a folder on screen deleted with its contents is not read for them ({asked} reads asked)", asked == 0);
        Check($"and leaves the canvas without ever being drawn as no longer existing (failed seen: {failedSeen})",
            NestedTree.IsDetached(gone) && !failedSeen);
    }

    /// <summary>
    /// A window opened on a path has the folders above it reached by name,
    /// never listed: a sorted sub-folder renamed in one of them kept its
    /// order under the old name, and lost it (J144).
    /// </summary>
    private static async Task Fix2PartialRenameOrderChecks()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "UltraExplorerPartialOrder", Guid.NewGuid().ToString("N"));
        var parentPath = Path.Combine(baseDirectory, "work");
        var projectPath = Path.Combine(parentPath, "Proj");
        var target = Path.Combine(projectPath, "Sub");
        Directory.CreateDirectory(target);
        try
        {
            var orders = new FolderOrders();
            using var tree = new NestedTree { Orders = orders };
            tree.SetRoots([new NestedRoot(baseDirectory, "W", NestedFolderKind.Drive)]);
            var folder = await tree.MaterializePathAsync(target);
            var parent = folder?.Parent?.Parent;
            if (folder is null || parent is null || !ViewAllPath.Equals(parent.FullPath, parentPath))
            {
                Check("the folder named is in the tree", false);
                return;
            }

            var byDate = new ItemSort(SortColumn.Modified, true);
            orders.SetFolder(projectPath, byDate);
            Check("the folder holding the sorted one was reached by name and never listed", parent is { HasPartialListing: true, IsLoaded: false });

            var renamedPath = Path.Combine(parentPath, "Proj2");
            tree.OnFolderChanged(parent, new FolderChange(parent.FullPath, ChangeKinds.Structural, Stopwatch.GetTimestamp(), new[] { new RenamePair("Proj", "Proj2") }, default));
            Check($"a sorted folder renamed below a folder never listed keeps its order under its new name ({orders.SortOf(renamedPath).Column})",
                orders.SortOf(renamedPath) == byDate && !orders.HasOwnOrder(projectPath));
            var renames = (ICollection)typeof(NestedTree).GetField("_renames", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tree)!;
            Check("and nothing waits for a listing the folder may never have", renames.Count == 0);
        }
        finally
        {
            TryDelete(baseDirectory);
        }
    }

    /// <summary>
    /// attrib +h * /d, or the Hidden box on a dozen folders at once: each
    /// is told only as its own date moving, and past eight the change lists
    /// none of them - so none was seen to be hidden, and the canvas showed
    /// all of them as before (J044).
    /// </summary>
    private static async Task Fix2ManyHiddenChecks()
    {
        foreach (var count in new[] { 1, 11 })
        {
            var disk = new FakeDisk();
            for (var index = 0; index < count; index++)
            {
                disk.Folder($@"Q:\hid\P\f{index:D2}");
            }

            var time = new ManualTime();
            using var hub = new ChangeHub(time);
            var watch = hub.AddRootForTests(@"Q:\hid", arm: false);
            using var tree = new NestedTree(disk.Read);
            tree.Changes = hub;
            tree.SetRoots([new NestedRoot(@"Q:\hid", "Q", NestedFolderKind.Drive)]);
            var top = tree.Root.Children.Single();
            await tree.LoadAsync(top);
            var parent = NestedTree.FindChild(top, "P")!;
            await tree.LoadAsync(parent);

            void Draw()
            {
                tree.BeginFrame();
                tree.Request(parent, 900);
            }

            for (var frame = 0; frame <= NestedTree.ExpireAfterFrames; frame++)
            {
                Draw();
            }

            var records = new List<(int, string, long, uint)>();
            for (var index = 0; index < count; index++)
            {
                disk.Folder($@"Q:\hid\P\f{index:D2}").IsHidden = true;
                records.Add((3, $@"P\f{index:D2}", 0, 0x10 | 0x2));
            }

            hub.FeedForTests(watch, NotifyRecords(true, [.. records]), details: true);
            time.Advance(3_000);
            DrainHub(hub, tree);
            var clock = Stopwatch.StartNew();
            int Hidden() => parent.AllChildren.Count(child => child.IsHidden);
            while (clock.ElapsedMilliseconds < 3_000 && Hidden() < count)
            {
                Draw();
                await Task.Delay(8);
            }

            Check($"{count} sub-folder{(count == 1 ? string.Empty : "s")} hidden at once {(count == 1 ? "is" : "are")} hidden on the canvas ({Hidden()} of {count})", Hidden() == count);
        }
    }
}
