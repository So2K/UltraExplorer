using System.IO;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task SparseLiveChecks(string root)
    {
        Section("live changes under partial physical ancestors");
        var ownedRoot = Path.Combine(Path.GetTempPath(), "UltraExplorerSparseLive", Guid.NewGuid().ToString("N"));
        try
        {
            RunOnSta("sparse live changes on the owning dispatcher", () => SparseLiveScenarioChecks(ownedRoot));
        }
        finally
        {
            TryDelete(ownedRoot);
        }
        return Task.CompletedTask;
    }

    private static async Task SparseLiveScenarioChecks(string root)
    {
        var volume = Path.Combine(root, "sparse-live-volume");
        var profile = Path.Combine(volume, "Users", "Fixture");
        var leafPath = Path.Combine(profile, "Chosen");
        var siblingPath = Path.Combine(profile, "Unchanged");
        Directory.CreateDirectory(leafPath);
        Directory.CreateDirectory(siblingPath);
        File.WriteAllText(Path.Combine(leafPath, "old.txt"), "old");
        File.WriteAllText(Path.Combine(siblingPath, "keep.txt"), "keep");
        var readPaths = new List<string>();
        using var tree = new NestedTree((path, token) =>
        {
            lock (readPaths) readPaths.Add(path);
            return NestedDirectoryReader.Read(path, token);
        }) { IsReadingOnDemand = false };
        tree.SetRoots([new(volume, "C:", NestedFolderKind.Drive)]);
        var leaf = (await tree.MaterializePathAsync(leafPath))!;
        var sibling = (await tree.MaterializePathAsync(siblingPath))!;
        await tree.LoadAsync(leaf);
        await tree.LoadAsync(sibling);
        var parent = leaf.Parent!;
        tree.CameraAnchor = leaf;
        Check("sparse live fixture loads only named leaves", readPaths.Count == 2
            && parent is { HasPartialListing: true, IsLoaded: false });

        Directory.Delete(leafPath, recursive: true);
        tree.OnFolderChanged(leaf, SparseGone(leaf));
        await SparseLiveWait(() => NestedTree.IsDetached(leaf));
        Check("a deleted loaded leaf is removed from an unread parent", tree.Find(leafPath) is null
            && NestedTree.IsDetached(leaf) && !parent.AllChildren.Contains(leaf));
        Check("dropping a sparse leaf preserves its sibling object and listing", ReferenceEquals(tree.Find(siblingPath), sibling)
            && sibling.Files.Any(file => file.Name == "keep.txt") && !NestedTree.IsDetached(sibling));
        Check("a sparse leaf deletion does not enumerate any ancestors", readPaths.Count == 2 && !parent.IsLoaded);

        Directory.CreateDirectory(leafPath);
        File.WriteAllText(Path.Combine(leafPath, "before.txt"), "before");
        leaf = (await tree.MaterializePathAsync(leafPath))!;
        await tree.LoadAsync(leaf);
        tree.CameraAnchor = leaf;
        Directory.Delete(leafPath, recursive: true);
        Directory.CreateDirectory(leafPath);
        File.WriteAllText(Path.Combine(leafPath, "after.txt"), "after");
        tree.OnFolderChanged(leaf, SparseGone(leaf));
        await SparseLiveWait(() => leaf.Files.Any(file => file.Name == "after.txt"));
        Check("a delayed Gone preserves a recreated path and refreshes its own contents", ReferenceEquals(tree.Find(leafPath), leaf)
            && !NestedTree.IsDetached(leaf) && leaf.Files.All(file => file.Name != "before.txt"));
        Check("recreated-path refresh still leaves its sparse parent unread", !parent.IsLoaded
            && readPaths.All(path => path == leafPath || path == siblingPath));

        // The parent's listing can complete while the named Gone check awaits
        // metadata. Both paths must converge without dropping other children.
        Directory.Delete(leafPath, recursive: true);
        tree.OnFolderChanged(leaf, SparseGone(leaf));
        await tree.LoadAsync(parent);
        await SparseLiveWait(() => NestedTree.IsDetached(leaf));
        Check("a concurrent full parent listing removes the deleted leaf", tree.Find(leafPath) is null && parent.IsLoaded);
        Check("parent listing race preserves an unchanged sibling and its cached files", ReferenceEquals(tree.Find(siblingPath), sibling)
            && sibling.Files.Any(file => file.Name == "keep.txt"));

        var separate = Path.Combine(volume, "Sparse", "Parent", "Target");
        Directory.CreateDirectory(separate);
        File.WriteAllText(Path.Combine(separate, "cached.txt"), "cached");
        var target = (await tree.MaterializePathAsync(separate))!;
        await tree.LoadAsync(target);
        var goneParent = target.Parent!;
        var goneAncestor = goneParent.Parent!;
        tree.CameraAnchor = target;
        var before = readPaths.Count;
        Directory.Delete(goneAncestor.FullPath, recursive: true);
        tree.OnFolderChanged(target, SparseGone(target, ancestor: true));
        await SparseLiveWait(() => NestedTree.IsDetached(goneAncestor));
        Check("AncestorGone drops the highest metadata-confirmed missing sparse ancestor", tree.Find(goneAncestor.FullPath) is null
            && NestedTree.IsDetached(target) && NestedTree.IsDetached(goneParent));
        Check("ancestor deletion never enumerates missing ancestors or the drive", readPaths.Count == before
            && tree.Find(volume) is { IsLoaded: false });
        Check("ancestor deletion preserves unrelated loaded branches", ReferenceEquals(tree.Find(siblingPath), sibling)
            && sibling.Files.Any(file => file.Name == "keep.txt"));
    }

    private static FolderChange SparseGone(NestedFolder folder, bool ancestor = false) =>
        new(folder.FullPath, ChangeKinds.Gone | (ancestor ? ChangeKinds.AncestorGone : 0), 0, default, default);

    private static async Task SparseLiveWait(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(10);
    }
}
