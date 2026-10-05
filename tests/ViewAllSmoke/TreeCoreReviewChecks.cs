using System.IO;
using UltraExplorer.Models;
using UltraExplorer.Picker;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The review's findings about the nested tree's own core: finding a folder
/// by name where a case-sensitive folder holds names that differ in case
/// alone, or where a name ends in a dot or a space; a folder whose read
/// failed and that its parent's listing names again; a folder gone to by
/// name while its parent's listing was being taken; a picker's file type
/// bound again as it was; ordering by type before the Shell has named a
/// kind; and a share read by a name ending in a dot.
/// </summary>
internal static partial class Program
{
    private static Task TreeCoreReviewChecks()
    {
        RunOnSta("tree core review", async () =>
        {
            await TreeCaseTwinChecks();
            await TreeNameEndChecks();
            await TreeFailedChildChecks();
            await TreeNamedDuringReadChecks();
            TreeFilterRebindChecks();
            await TreeTypeNameChecks();
        });
        TreeShareNameEndChecks();
        return Task.CompletedTask;
    }

    /// <summary>
    /// A disk held as listings by exact path, case and all - the in-memory
    /// disk the other checks use matches paths ignoring case, which is what a
    /// case-sensitive folder must not.  Folders are listed the way the real
    /// reader sorts them; a path with no listing is gone.
    /// </summary>
    private sealed class ExactDisk
    {
        private readonly Dictionary<string, (string[] Folders, string[] Files)> _listings = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public void Set(string path, string[] folders, params string[] files)
        {
            lock (_gate)
            {
                _listings[path] = (folders, files);
            }
        }

        public void Remove(string path)
        {
            lock (_gate)
            {
                _listings.Remove(path);
            }
        }

        public NestedListing Read(string path, CancellationToken cancellationToken)
        {
            (string[] Folders, string[] Files) listing;
            lock (_gate)
            {
                if (!_listings.TryGetValue(path, out listing))
                {
                    return NestedListing.Failed("No longer exists");
                }
            }

            static int Order(string left, string right)
            {
                var order = StringComparer.CurrentCultureIgnoreCase.Compare(left, right);
                return order != 0 ? order : string.CompareOrdinal(left, right);
            }

            var folders = listing.Folders.ToList();
            folders.Sort(Order);
            var files = listing.Files.ToList();
            files.Sort(Order);
            return new NestedListing([.. folders.Select(name => new NestedEntry(name, false, false))], files.Count, 0, false)
            {
                Files = [.. files.Select(name => new NestedFile(name, false, 1))]
            };
        }
    }

    private static async Task TreeCaseTwinChecks()
    {
        Section("review: folders whose names differ in case alone");
        var disk = new ExactDisk();
        disk.Set(@"Q:\", ["w", "many"]);
        disk.Set(@"Q:\w", ["Build", "build"]);
        disk.Set(@"Q:\w\Build", ["other"]);
        disk.Set(@"Q:\w\build", ["inner"]);

        // Enough names for a binary search, with the twins in the middle.
        disk.Set(@"Q:\many", [.. Enumerable.Range(0, 20).Select(index => $"f{index:D2}"), "Build", "build"]);
        using var tree = new NestedTree(disk.Read);
        tree.SetRoots([new(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        var q = tree.Root.Children.Single();
        await tree.LoadAsync(q);
        await tree.LoadAsync(tree.Find(@"Q:\w")!);
        await tree.LoadAsync(tree.Find(@"Q:\many")!);

        Check("a short listing gives the folder spelt exactly as asked, either way round",
            tree.Find(@"Q:\w\build")?.Name == "build" && tree.Find(@"Q:\w\Build")?.Name == "Build");
        Check("and so does a long one, searched by halves",
            tree.Find(@"Q:\many\build")?.Name == "build" && tree.Find(@"Q:\many\Build")?.Name == "Build");
        Check("a name spelt like neither still finds one of them",
            tree.Find(@"Q:\w\BUILD") is { Name: "Build" or "build" } && tree.Find(@"Q:\many\bUILD") is { Name: "Build" or "build" });
        var inner = await tree.RevealAsync(@"Q:\w\build\inner");
        Check($"revealing a path through one twin goes through that one ({inner?.FullPath})",
            inner?.FullPath == @"Q:\w\build\inner" && inner.Parent?.Name == "build");
    }

    private static async Task TreeNameEndChecks()
    {
        Section("review: folders whose names end in a dot or a space");
        var disk = new ExactDisk();
        disk.Set(@"Q:\", ["d"]);
        disk.Set(@"Q:\d", ["backup", "backup.", "notes", "notes "]);
        disk.Set(@"Q:\d\backup", ["plain"]);
        disk.Set(@"Q:\d\backup.", ["inside"]);
        using var tree = new NestedTree(disk.Read);
        tree.SetRoots([new(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await tree.LoadAsync(tree.Root.Children.Single());
        var d = tree.Find(@"Q:\d")!;
        await tree.LoadAsync(d);

        Check("a name ending in a dot is found as itself, not as its neighbour without one",
            tree.Find(@"Q:\d\backup.")?.Name == "backup." && tree.Find(@"Q:\d\backup")?.Name == "backup");
        Check("and so is one ending in a space",
            tree.Find(@"Q:\d\notes ")?.Name == "notes " && tree.Find(@"Q:\d\notes")?.Name == "notes");
        var inside = await tree.RevealAsync(@"Q:\d\backup.\inside");
        Check($"revealing a path through it goes through it ({inside?.FullPath})", inside?.FullPath == @"Q:\d\backup.\inside");
        Check("a path spelt any other way is normalised as ever",
            tree.Find(@"q:\D\backup\")?.Name == "backup" && tree.Find(@"Q:/d//backup")?.Name == "backup");

        tree.SetUserHidden([@"Q:\d\backup."]);
        var dotted = tree.Find(@"Q:\d\backup.")!;
        var plain = tree.Find(@"Q:\d\backup")!;
        Check("hiding it from the canvas hides it and not its neighbour",
            dotted.Index < 0 && plain.Index >= 0
            && tree.IsUserHidden(@"Q:\d\backup.") && !tree.IsUserHidden(@"Q:\d\backup"));
        tree.SetUserHidden([]);
    }

    private static async Task TreeFailedChildChecks()
    {
        Section("review: a folder whose read failed and that comes back");
        var disk = new ExactDisk();
        disk.Set(@"Q:\", ["p"]);
        disk.Set(@"Q:\p", ["dist", "out", "web"]);
        using var tree = new NestedTree(disk.Read);
        tree.SetRoots([new(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await tree.LoadAsync(tree.Root.Children.Single());
        var p = tree.Find(@"Q:\p")!;
        await tree.LoadAsync(p);

        // Deleted by a build tool before its first read, and made again: the
        // parent's next listing names it once more, as the same folder.
        var dist = tree.Find(@"Q:\p\dist")!;
        var output = tree.Find(@"Q:\p\out")!;
        var web = tree.Find(@"Q:\p\web")!;
        await tree.LoadAsync(dist);
        await tree.LoadAsync(output);
        await tree.LoadAsync(web);
        Check("read while it was gone, it failed",
            dist.LoadState == NestedLoadState.Failed && output.LoadState == NestedLoadState.Failed && web.LoadState == NestedLoadState.Failed);
        tree.Request(dist, 800);
        await Task.Delay(30);
        Check("and is not read again on its own while nothing changed", dist.LoadState == NestedLoadState.Failed);

        disk.Set(@"Q:\p\dist", [], "bundle.js");
        disk.Set(@"Q:\p\out", ["sub"]);
        disk.Set(@"Q:\p\web", [], "index.html");
        await tree.RefreshAsync(p);
        Check("the parent's next listing keeps the same folders",
            ReferenceEquals(tree.Find(@"Q:\p\dist"), dist) && ReferenceEquals(tree.Find(@"Q:\p\out"), output));
        tree.Request(dist, 800);
        Check("drawn again, it is read again and shows what it holds now",
            await Until(() => dist.IsLoaded, 2_000) && dist.Files is [{ Name: "bundle.js" }] && dist.ErrorMessage.Length == 0);
        await tree.LoadAsync(web);
        Check("loaded by name, it is read again too", web.IsLoaded && web.Files is [{ Name: "index.html" }]);
        var sub = await tree.RevealAsync(@"Q:\p\out\sub");
        Check($"and a path into it reaches its folder ({sub?.FullPath})", sub?.FullPath == @"Q:\p\out\sub");
    }

    private static async Task TreeNamedDuringReadChecks()
    {
        Section("review: a folder named while its parent is being read");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerTreeCore", Guid.NewGuid().ToString("N"));
        var parent = Path.Combine(root, "p");
        var freshPath = Path.Combine(parent, "fresh");
        Directory.CreateDirectory(parent);
        using var taken = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        var holding = 0;
        try
        {
            // The parent's listing is taken, and held back until the folder
            // has been made and gone to by name.
            using var tree = new NestedTree((path, token) =>
            {
                var listing = NestedDirectoryReader.Read(path, token);
                if (string.Equals(path, parent, StringComparison.OrdinalIgnoreCase) && Interlocked.Exchange(ref holding, 0) == 1)
                {
                    taken.Release();
                    release.Wait(TimeSpan.FromSeconds(10), token);
                }

                return listing;
            });
            tree.SetRoots([new(root, "fixture", NestedFolderKind.Drive)]);
            await tree.LoadAsync(tree.Root.Children.Single());
            var p = tree.Find(parent)!;
            await tree.LoadAsync(p);

            Volatile.Write(ref holding, 1);
            var refresh = tree.RefreshAsync(p);
            var held = await taken.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.CreateDirectory(freshPath);
            var fresh = await tree.MaterializePathAsync(freshPath);
            release.Release();
            await refresh;
            Check($"a folder gone to by name while its parent's listing was being taken is kept by that listing ({held}, {fresh?.IsForgotten})",
                held && fresh is { IsForgotten: false } && ReferenceEquals(tree.Find(freshPath), fresh) && NestedTree.IsOnCanvas(fresh));

            await tree.RefreshAsync(p);
            Check("the next read keeps it as any folder still there", ReferenceEquals(tree.Find(freshPath), fresh) && !fresh!.IsForgotten);
            Directory.Delete(freshPath);
            await tree.RefreshAsync(p);
            Check("and one that began after it was gone drops it", fresh!.IsForgotten && tree.Find(freshPath) is null);
        }
        finally
        {
            release.Release();
            TryDelete(root);
        }
    }

    private static async Task TreeTypeNameChecks()
    {
        Section("review: ordering by type before the Shell has named a kind");
        static string Named(string extension) => extension switch
        {
            "zza" => "Zulu kind",
            "zzb" => "Alpha kind",
            "zzc" => "Mike kind",
            _ => string.Empty
        };

        var treeThread = Environment.CurrentManagedThreadId;
        var askedHere = 0;
        using var gate = new ManualResetEventSlim(false);
        string Deferred(string extension)
        {
            if (Environment.CurrentManagedThreadId == treeThread)
            {
                Interlocked.Increment(ref askedHere);
            }
            else
            {
                gate.Wait(10_000);
            }

            return Named(extension);
        }

        var previous = FileTypeNames.Resolver;
        FileTypeNames.DefersResolver = true;
        FileTypeNames.Resolver = Deferred;
        try
        {
            var disk = new FakeDisk();
            disk.Folder(@"Q:\logs");
            foreach (var name in new[] { "a1.zza", "b1.zzb", "c1.zzc", "a2.zza", "c2.zzc", "b2.zzb" })
            {
                disk.AddFile(@"Q:\logs", name);
            }

            using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
            tree.SetRoots([new(@"Q:\", "Q:", NestedFolderKind.Drive)]);
            await tree.LoadAsync(tree.Root.Children.Single());
            var logs = tree.Find(@"Q:\logs")!;
            await tree.LoadAsync(logs);
            tree.SetSort(new ItemSort(SortColumn.Type, false));
            tree.EnsureLayout(logs);
            List<string> Names() => [.. logs.Files.Select(file => file.Name)];
            Check($"placed by type, the tree's thread asks the Shell nothing, and a kind not named yet goes by its stand-in ({askedHere}: {string.Join(", ", Names())})",
                askedHere == 0 && Names().SequenceEqual(["a1.zza", "a2.zza", "b1.zzb", "b2.zzb", "c1.zzc", "c2.zzc"]));
            gate.Set();
            Check("once the names are in, the folder is placed by them",
                await Until(() => Names().SequenceEqual(["b1.zzb", "b2.zzb", "c1.zzc", "c2.zzc", "a1.zza", "a2.zza"]), 5_000) && askedHere == 0);
            tree.SetSort(ItemSort.Default);
        }
        finally
        {
            gate.Set();
            FileTypeNames.DefersResolver = false;
            FileTypeNames.Resolver = previous;
        }
    }

    private static void TreeFilterRebindChecks()
    {
        Section("review: a picker's file type bound again");
        var disk = new FakeDisk();
        disk.Folder(@"Q:\docs");
        disk.AddFile(@"Q:\docs", "a.txt");
        disk.AddFile(@"Q:\docs", "b.png");
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        var filter = FileDialogFilter.Parse("*.txt");
        tree.FileNameFilter = filter.Matches;
        var version = tree.Version;
        tree.FileNameFilter = filter.Matches;
        Check("binding the filter it already has again re-filters nothing", tree.Version == version);
        tree.FileNameFilter = FileDialogFilter.Parse("*.png").Matches;
        Check("another one re-filters as ever", tree.Version != version);
        tree.FileNameFilter = null;
    }

    private static void TreeShareNameEndChecks()
    {
        Section("review: a share's folder whose name ends in a dot");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerTreeCore", Guid.NewGuid().ToString("N"));
        var share = root.Length > 3 && root[1] == ':' ? $@"\\localhost\{root[0]}$" + root[2..] : null;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "x"));
            File.WriteAllText(Path.Combine(root, "x", "a.txt"), "a");

            // Only the extended-length form can make a name ending in a dot.
            Directory.CreateDirectory(@"\\?\" + Path.Combine(root, "x."));
            File.WriteAllText(@"\\?\" + Path.Combine(root, "x.", "b.txt"), "b");
            if (share is null || !Directory.Exists(share))
            {
                Console.WriteLine($"  skip  the drive's administrative share cannot be reached here ({share})");
                return;
            }

            var dotted = NestedDirectoryReader.Read(share + @"\x.", CancellationToken.None);
            Check($"read through the share, 'x.' lists its own files, not those of 'x' ({string.Join(", ", dotted.Files.Select(file => file.Name))}{dotted.ErrorMessage})",
                dotted.ErrorMessage.Length == 0 && dotted.Files is [{ Name: "b.txt" }]);
            var plain = NestedDirectoryReader.Read(share + @"\x", CancellationToken.None);
            Check("and 'x' lists its own", plain.ErrorMessage.Length == 0 && plain.Files is [{ Name: "a.txt" }]);
            Check("the share's folder has its own time", NestedDirectoryReader.DirectoryWriteTicks(share + @"\x.") > 0);
        }
        finally
        {
            TryDelete(@"\\?\" + root);
        }
    }
}
