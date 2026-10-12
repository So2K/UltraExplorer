using System.Globalization;
using System.IO;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task ArrivalLifecycleEdgeChecksAsync()
    {
        await ArrivalDeletedPendingChecksAsync();
        await ArrivalAncestorRenameChecksAsync();
        await ArrivalTwinIdentityChecksAsync();
        await ArrivalLaggingPendingRenameChecksAsync();
        await ArrivalStaleTwinRenameChecksAsync();
    }

    private static async Task ArrivalDeletedPendingChecksAsync()
    {
        Section("new-item arrival edges: deleting an unseen arrival frees its signal and later arrivals still flash");
        var root = ArrivalDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "baseline.txt"), "old");
            using var tree = await ArrivalTreeAsync(root);
            var parent = tree.Find(root)!;
            var canvas = ArrivalCanvas(tree);
            try
            {
                canvas.ShownLayers &= ~CanvasLayer.Files;
                canvas.FlyTo(parent, 0.94, animated: false);
                var time = await ArrivalSettleAsync(canvas, TimeSpan.FromSeconds(200));
                var removed = Path.Combine(root, "removed-before-visible.txt");
                File.WriteAllText(removed, "new then removed");
                await tree.RefreshAsync(parent);
                canvas.RunFrameForTests(time += TimeSpan.FromMilliseconds(40));
                Check("the new file really waits unseen before deletion", canvas.PendingArrivalCount == 1 && canvas.ActiveArrivalCount == 0);
                File.Delete(removed);
                await tree.RefreshAsync(parent);
                Check("a complete refresh removes the deleted file's pending signal from an attached parent", canvas.PendingArrivalCount == 0);
                var later = Path.Combine(root, "arrived-after-delete.txt");
                File.WriteAllText(later, "later arrival");
                await tree.RefreshAsync(parent);
                Check("the next real addition retains exactly one fresh signal", canvas.PendingArrivalCount == 1);
                canvas.ShownLayers |= CanvasLayer.Files;
                var bands = ArrivalPulseBands(canvas, later, time += TimeSpan.FromMilliseconds(40));
                Check("the later addition actually draws its two yellow flashes", bands == 2 && canvas.PendingArrivalCount == 0);
            }
            finally { canvas.Tree = null; }
        }
        finally { TryDelete(root); }
    }

    private static async Task ArrivalAncestorRenameChecksAsync()
    {
        Section("new-item arrival edges: an ancestor rename carries an unseen descendant's signal");
        var root = ArrivalDirectory();
        var oldTop = Path.Combine(root, "original-parent");
        var oldDeep = Path.Combine(oldTop, "middle", "deep");
        Directory.CreateDirectory(oldDeep);
        File.WriteAllText(Path.Combine(oldDeep, "baseline.txt"), "old");
        try
        {
            using var tree = await ArrivalTreeAsync(root);
            var top = tree.Find(root)!;
            var oldParent = tree.Find(oldTop)!;
            await tree.LoadAsync(oldParent);
            await tree.LoadAsync(tree.Find(Path.Combine(oldTop, "middle"))!);
            var previous = tree.Find(oldDeep)!;
            await tree.LoadAsync(previous);
            var additions = new List<NestedArrival>();
            tree.ItemsAdded += batch => additions.AddRange(batch);
            var canvas = ArrivalCanvas(tree);
            try
            {
                canvas.ShownLayers &= ~CanvasLayer.Files;
                canvas.FlyTo(top, 0.94, animated: false);
                var time = await ArrivalSettleAsync(canvas, TimeSpan.FromSeconds(300));
                File.WriteAllText(Path.Combine(oldDeep, "new-descendant.txt"), "new and still unseen");
                await tree.RefreshAsync(previous);
                canvas.RunFrameForTests(time += TimeSpan.FromMilliseconds(40));
                Check("the descendant is pending before the existing ancestor is renamed", canvas.PendingArrivalCount == 1 && additions.Count == 1);
                var newTop = Path.Combine(root, "renamed-parent");
                Directory.Move(oldTop, newTop);
                tree.OnFolderChanged(top, new FolderChange(root, ChangeKinds.Structural, 0,
                    new RenamePair[] { new(Path.GetFileName(oldTop), Path.GetFileName(newTop)) }, default));
                await tree.RefreshAsync(top);
                canvas.RunFrameForTests(time += TimeSpan.FromMilliseconds(40));
                var newDeep = Path.Combine(newTop, "middle", "deep");
                var current = tree.Find(newDeep);
                Check("the detached descendant is rebound to the carried path without an invented addition", NestedTree.IsDetached(previous)
                    && current is { IsLoaded: true } && !ReferenceEquals(current, previous) && additions.Count == 1 && canvas.PendingArrivalCount == 1);
                canvas.ShownLayers |= CanvasLayer.Files;
                canvas.FlyTo(current!, 0.94, animated: false);
                var bands = ArrivalPulseBands(canvas, Path.Combine(newDeep, "new-descendant.txt"), time += TimeSpan.FromMilliseconds(40));
                Check("the renamed path still produces two real yellow flashes when entered", bands == 2 && canvas.PendingArrivalCount == 0);
            }
            finally { canvas.Tree = null; }
        }
        finally { TryDelete(root); }
    }

    private static async Task ArrivalTwinIdentityChecksAsync()
    {
        Section("new-item arrival edges: exact case-sensitive twins differ from case-only renames");
        var names = Enumerable.Range(0, 20).Select(index => $"filler-{index:D2}.txt").Append("build.txt").ToList();
        using var tree = new NestedTree((_, _) => ArrivalEdgeListing(names)) { IsReadingOnDemand = false, PostBackground = null };
        var additions = new List<NestedArrival>();
        tree.ItemsAdded += batch => additions.AddRange(batch);
        tree.SetRoots([new NestedRoot(@"V:\", "V:", NestedFolderKind.Drive)]);
        var parent = tree.Find(@"V:\")!;
        await tree.LoadAsync(parent);
        var canvas = ArrivalCanvas(tree);
        try
        {
            canvas.ShownLayers &= ~CanvasLayer.Files;
            names.Add("Build.txt");
            await tree.RefreshAsync(parent);
            Check("a newly added exact twin emits its own arrival while the original remains", additions.Count == 1
                && additions[0].Name == "Build.txt" && canvas.PendingArrivalCount == 1);
            Check("complete-list identity resolves both twins exactly", NestedTree.TryArrivalKind(parent, "Build.txt", out var upperDirectory)
                && !upperDirectory && NestedTree.TryArrivalKind(parent, "build.txt", out var lowerDirectory) && !lowerDirectory);
            names.Remove("Build.txt");
            await tree.RefreshAsync(parent);
            Check("deleting the new twin does not retain its signal on the older caseless match", canvas.PendingArrivalCount == 0 && additions.Count == 1);
            names.Remove("build.txt");
            names.Add("BUILD.TXT");
            await tree.RefreshAsync(parent);
            Check("a sole case-only spelling change does not invent a new item without watch details", additions.Count == 1 && canvas.PendingArrivalCount == 0);
            names.Remove("BUILD.TXT");
            names.Add("Build.txt");
            tree.OnFolderChanged(parent, new FolderChange(parent.FullPath, ChangeKinds.Structural, 0,
                new RenamePair[] { new("BUILD.TXT", "Build.txt") }, default));
            await tree.RefreshAsync(parent);
            Check("a known case-only rename also leaves no arrival", additions.Count == 1 && canvas.PendingArrivalCount == 0);
            names.Add("build.txt");
            await tree.RefreshAsync(parent);
            Check("adding the lower-case twin later is a second genuine addition", additions.Count == 2
                && additions[^1].Name == "build.txt" && canvas.PendingArrivalCount == 1);
        }
        finally { canvas.Tree = null; }
    }

    private static async Task ArrivalLaggingPendingRenameChecksAsync()
    {
        Section("new-item arrival edges: an older in-flight listing cannot discard a pending rename");
        var names = new List<string> { "baseline.txt" };
        using var tree = new NestedTree((_, _) => ArrivalEdgeListing(names)) { IsReadingOnDemand = false, PostBackground = null };
        var additions = new List<NestedArrival>();
        tree.ItemsAdded += batch => additions.AddRange(batch);
        tree.SetRoots([new NestedRoot(@"W:\", "W:", NestedFolderKind.Drive)]);
        var parent = tree.Find(@"W:\")!;
        await tree.LoadAsync(parent);
        var canvas = ArrivalCanvas(tree);
        try
        {
            canvas.ShownLayers &= ~CanvasLayer.Files;
            canvas.FlyTo(parent, 0.94, animated: false);
            var time = await ArrivalSettleAsync(canvas, TimeSpan.FromSeconds(400));
            names.Add("created.tmp");
            await tree.RefreshAsync(parent);
            tree.OnFolderChanged(parent, new FolderChange(parent.FullPath, ChangeKinds.Structural, 0,
                new RenamePair[] { new("created.tmp", "ready.txt") }, default));
            // The next snapshot still predates the rename, as a network cache
            // or a read started during the previous burst can do.
            await tree.RefreshAsync(parent);
            Check("a lagging old-name listing keeps the already new signal at its final name", canvas.PendingArrivalCount == 1 && additions.Count == 1);
            names.Remove("created.tmp");
            names.Add("ready.txt");
            await tree.RefreshAsync(parent);
            Check("the final-name snapshot neither loses the signal nor creates another one", canvas.PendingArrivalCount == 1 && additions.Count == 1);
            canvas.ShownLayers |= CanvasLayer.Files;
            var bands = ArrivalPulseBands(canvas, parent.PathOf(parent.Files.Single(file => file.Name == "ready.txt")), time += TimeSpan.FromMilliseconds(40));
            Check("that signal still reaches the actual final-name tile with two flashes", bands == 2 && canvas.PendingArrivalCount == 0);
        }
        finally { canvas.Tree = null; }
    }

    private static async Task ArrivalStaleTwinRenameChecksAsync()
    {
        Section("new-item arrival edges: a deleted rename cannot be kept alive by an unrelated case twin");
        var names = Enumerable.Range(0, 20).Select(index => $"filler-{index:D2}.txt").Concat(["build.txt", "Build.txt"]).ToList();
        using (var tree = new NestedTree((_, _) => ArrivalEdgeListing(names)) { IsReadingOnDemand = false, PostBackground = null })
        {
            var additions = new List<NestedArrival>();
            tree.ItemsAdded += batch => additions.AddRange(batch);
            tree.SetRoots([new NestedRoot(@"U:\", "U:", NestedFolderKind.Drive)]);
            var parent = tree.Find(@"U:\")!;
            await tree.LoadAsync(parent);
            tree.OnFolderChanged(parent, new FolderChange(parent.FullPath, ChangeKinds.Structural, 0,
                new RenamePair[] { new("build.txt", "ready.txt") }, default));
            // The renamed target was then removed before this snapshot. Its
            // unrelated exact twin remains, and must not stand in for source.
            names.Remove("build.txt");
            await tree.RefreshAsync(parent);
            Check("renaming then deleting one twin produces no invented addition", additions.Count == 0
                && parent.Files.Any(file => file.Name == "Build.txt") && parent.Files.All(file => file.Name != "build.txt"));
            names.Remove("Build.txt");
            names.Add("ready.txt");
            await tree.RefreshAsync(parent);
            Check("a later genuinely created target is not suppressed by the discarded rename", additions.Count == 1
                && additions[0].Name == "ready.txt");
        }

        Section("new-item arrival edges: unique watcher source casing is canonicalized before a lagging snapshot");
        var knownNames = Enumerable.Range(0, 20).Select(index => $"filler-{index:D2}.txt").Append("Build.txt").ToList();
        using var knownTree = new NestedTree((_, _) => ArrivalEdgeListing(knownNames)) { IsReadingOnDemand = false, PostBackground = null };
        var knownAdditions = new List<NestedArrival>();
        knownTree.ItemsAdded += batch => knownAdditions.AddRange(batch);
        knownTree.SetRoots([new NestedRoot(@"T:\", "T:", NestedFolderKind.Drive)]);
        var knownParent = knownTree.Find(@"T:\")!;
        await knownTree.LoadAsync(knownParent);
        knownTree.OnFolderChanged(knownParent, new FolderChange(knownParent.FullPath, ChangeKinds.Structural, 0,
            new RenamePair[] { new("BUILD.TXT", "renamed.txt") }, default));
        await knownTree.RefreshAsync(knownParent);
        Check("the lagging unique source remains an existing item despite watcher casing", knownAdditions.Count == 0
            && knownParent.Files.Any(file => file.Name == "Build.txt"));
        knownNames.Remove("Build.txt");
        knownNames.Add("renamed.txt");
        await knownTree.RefreshAsync(knownParent);
        Check("its later final-name snapshot is still a rename rather than a new arrival", knownAdditions.Count == 0
            && knownParent.Files.Any(file => file.Name == "renamed.txt"));
    }

    private static NestedListing ArrivalEdgeListing(IEnumerable<string> names)
    {
        var culture = CultureInfo.CurrentCulture.CompareInfo;
        var files = names.Select(name => new NestedFile(name, false, 1)).ToArray();
        Array.Sort(files, (left, right) =>
        {
            var order = culture.Compare(left.Name, right.Name, CompareOptions.IgnoreCase);
            return order != 0 ? order : string.CompareOrdinal(left.Name, right.Name);
        });
        return new NestedListing([], files.Length, 0, false) { Files = files };
    }
}
