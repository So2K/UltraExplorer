using System.Diagnostics;
using System.IO;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;

namespace ViewAllSmoke;

/// <summary>
/// The second review's fixes to reading the nested tree: what a directory
/// read allocates, looking a path up, the read queue and its lanes, failed
/// folders read again, hidden folders and the drives.  Each check failed
/// before its fix.
/// </summary>
internal static partial class Program
{
    private static Task TreeReadingFixChecks()
    {
        RunOnSta("tree reading fixes", async () =>
        {
            Section("tree reading fixes");
            ReaderAllocationChecks();
            await NamedChildOrderChecks();
            await TildeLookupChecks();
            await FileLookupChecks();
            await FirstDrawRegistrationChecks();
            await PickChecks();
        });

        return Task.CompletedTask;
    }

    // ---- looking a path up (J033) ------------------------------------------------------

    /// <summary>
    /// A path with a '~' in it - "~$Report.docx", "$Windows.~BT" - is looked
    /// up as fast as one without: the beacons look theirs up every frame, and
    /// normalising such a path asked the file system for the long form of
    /// every name on the way.  A short name Windows made ("PROGRA~1") still
    /// finds the folder it stands for.
    /// </summary>
    private static async Task TildeLookupChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerTilde", Guid.NewGuid().ToString("N"));
        var plain = Path.Combine(root, "plain", "deeper", "leaf");
        var tilde = Path.Combine(root, "with~tilde", "deeper", "Backup~old");
        var longName = Path.Combine(root, "Long folder name");
        Directory.CreateDirectory(plain);
        Directory.CreateDirectory(tilde);
        Directory.CreateDirectory(longName);
        try
        {
            using var tree = new NestedTree();
            tree.SetRoots([new NestedRoot(root, "T", NestedFolderKind.Drive)]);
            var opened = await tree.MaterializePathAsync(tilde);
            await tree.MaterializePathAsync(plain);
            await tree.MaterializePathAsync(longName);
            Check("a folder with a '~' in its path is found by that path, and by the path of a file in it",
                opened is not null && ReferenceEquals(tree.Find(tilde), opened)
                && ReferenceEquals(tree.FindNearest(Path.Combine(tilde, "~$Report.docx")), opened));

            static double MicrosecondsPerFind(NestedTree tree, string path)
            {
                tree.Find(path);
                var started = Stopwatch.GetTimestamp();
                for (var run = 0; run < 200; run++)
                {
                    tree.Find(path);
                }

                return Stopwatch.GetElapsedTime(started).TotalMicroseconds / 200;
            }

            var plainCost = MicrosecondsPerFind(tree, Path.Combine(plain, "report.docx"));
            var tildeCost = MicrosecondsPerFind(tree, Path.Combine(tilde, "~$Report.docx"));
            Console.WriteLine($"        a look-up takes {plainCost:0.0} us without a '~' and {tildeCost:0.0} us with one");
            Check($"and looking it up costs about what a path without one does ({tildeCost:0.0} us against {plainCost:0.0} us)",
                tildeCost < 100 && tildeCost < 10 * plainCost + 20);

            var alias = ShortPath(longName);
            if (alias is not null && alias.Contains('~') && !string.Equals(alias, longName, StringComparison.OrdinalIgnoreCase))
            {
                Check($"a short name Windows made still finds the folder it stands for ({Path.GetFileName(alias)})",
                    tree.Find(longName) is { } named && ReferenceEquals(tree.Find(alias), named));
            }
            else
            {
                Console.WriteLine("        (this volume makes no short names: the short-name look-up is not checked)");
            }
        }
        finally
        {
            TryDelete(Path.GetDirectoryName(root)!);
        }
    }

    // ---- looking a file's path up (J093) -------------------------------------------------

    /// <summary>
    /// The path of a file in a folder of twenty-five thousand sub-folders -
    /// a file's beacon, looked up every frame - is found to be no folder by
    /// a binary search, not a look at every sub-folder.
    /// </summary>
    private static async Task FileLookupChecks()
    {
        const int Folders = 25_000;
        NestedEntry[] entries = [.. Enumerable.Range(0, Folders).Select(index => new NestedEntry($"f{index:D6}", false, false))];
        NestedFile[] files = [new NestedFile("aaa.txt", false, 1), new NestedFile("report.txt", false, 2), new NestedFile("zzz.txt", false, 3)];
        NestedListing Read(string path, CancellationToken token) =>
            string.Equals(path, @"Q:\", StringComparison.OrdinalIgnoreCase)
                ? new NestedListing(entries, files.Length, 0, false) { Files = files }
                : new NestedListing([], 0, 0, false);

        using var tree = new NestedTree(Read);
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        var drive = tree.Find(@"Q:\")!;
        await tree.LoadAsync(drive);

        static double MicrosecondsPerFind(NestedTree tree, string path)
        {
            tree.Find(path);
            var started = Stopwatch.GetTimestamp();
            for (var run = 0; run < 500; run++)
            {
                tree.Find(path);
            }

            return Stopwatch.GetElapsedTime(started).TotalMicroseconds / 500;
        }

        var folderCost = MicrosecondsPerFind(tree, @"Q:\f012345");
        var fileCost = MicrosecondsPerFind(tree, @"Q:\report.txt");
        Console.WriteLine($"        among {Folders:N0} sub-folders a folder is found in {folderCost:0.0} us, a file's path in {fileCost:0.0} us");
        Check("a folder among them is found, and a file's path is no folder",
            drive.AllChildren.Length == Folders && tree.Find(@"Q:\f012345") is { Name: "f012345" }
            && tree.Find(@"Q:\report.txt") is null && tree.FindNearest(@"Q:\report.txt") == drive);
        Check($"and the file's path costs about what the folder does ({fileCost:0.0} us against {folderCost:0.0} us)",
            fileCost < 25 && fileCost < 5 * folderCost + 10);
    }

    // ---- registering with the hub (J008) -------------------------------------------------

    /// <summary>
    /// Folders the canvas draws for the first time are registered with the
    /// hub when their reads begin, not in the frame that drew them: a frame
    /// that queues hundreds registers none, and the ones that leave the
    /// screen unread are never registered - only the folders read are.
    /// </summary>
    private static async Task FirstDrawRegistrationChecks()
    {
        const int Count = 300;
        using var disk = new GatedDisk();
        var names = Enumerable.Range(0, Count).Select(index => $"f{index:D3}").ToArray();
        foreach (var name in names)
        {
            disk.Disk.Folder($@"Q:\{name}");
        }

        using var hub = new ChangeHub(TimeProvider.System);
        hub.AddRootForTests(@"Q:\", arm: false);
        using var tree = new NestedTree(disk.Disk.Read) { Changes = hub };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await tree.LoadAsync(tree.Find(@"Q:\")!);
        var folders = names.Select(name => tree.Find($@"Q:\{name}")!).ToArray();
        var before = hub.Registry.NestedTargets;

        disk.Gating = true;
        tree.BeginFrame();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        foreach (var folder in folders)
        {
            tree.Request(folder, 40);
        }

        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var afterFrame = hub.Registry.NestedTargets - before;
        Console.WriteLine($"        a frame that draws {Count} folders for the first time registers {afterFrame} with the hub and allocates {allocated / Count:N0} bytes a folder");
        Check($"a frame that draws {Count} folders for the first time registers at most the {NestedTree.LocalReadSlots} it begins to read ({afterFrame})",
            folders.All(folder => folder.QueuedRead == ReadKind.Load) && afterFrame <= NestedTree.LocalReadSlots);

        // The view moves on: the rest are never read.
        tree.BeginFrame();
        tree.BeginFrame();
        tree.BeginFrame();
        disk.Open();
        await WaitUntil(() => tree.PendingCount == 0 && folders.All(folder => folder.QueuedRead == ReadKind.None), 5_000);
        var read = folders.Count(folder => folder.IsLoaded);
        var registered = hub.Registry.NestedTargets - before;
        Check($"and once they left the screen, only the folders read are registered ({read} read, {registered} registered with the hub, {tree.LiveRegisteredCount - 1} by the tree)",
            read is > 0 and < Count && registered == read && tree.LiveRegisteredCount - 1 == read);
    }

    // ---- picking the next read (J007) ----------------------------------------------------

    /// <summary>
    /// A freed slot finds its next folder among thousands waiting without a
    /// look at every one of them, and still in the order the queue always
    /// read them: something waited for first, then the widest on screen -
    /// one queued later among them included, and past the first few hundred.
    /// </summary>
    private static async Task PickChecks()
    {
        const int Waiting = 10_000;
        using (var disk = new GatedDisk())
        {
            var (tree, folders) = await QueueTreeAsync(disk, [.. Enumerable.Range(0, Waiting).Select(index => $"w{index:D5}")]);
            using var owned = tree;
            var random = new Random(5);
            disk.Gating = true;
            tree.BeginFrame();
            foreach (var folder in folders)
            {
                tree.Request(folder, 20 + random.NextDouble() * 400);
            }

            // Every one of them waiting, the reads go.
            var started = Stopwatch.GetTimestamp();
            disk.Open();
            await WaitUntil(() => folders.All(folder => folder.IsLoaded), 60_000);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var (picks, total, worst) = tree.PickCost;
            var mean = total * 1000 / Math.Max(1, picks);
            Console.WriteLine($"        {Waiting:N0} folders waiting: read in {elapsed:N0} ms, {picks:N0} picks of {mean:0.0} us on average ({total:0.0} ms in all, the worst {worst * 1000:0} us)");
            Check($"{Waiting:N0} folders waiting are all read, each pick a few microseconds rather than a pass over the queue ({mean:0.0} us on average)",
                folders.All(folder => folder.IsLoaded) && mean < 20);
        }

        // All but one of the local slots held for the whole check, so the
        // reads go one at a time and the order they begin in is the order
        // they were picked in.
        using var held = new ManualResetEventSlim();
        using var gate = new SemaphoreSlim(0);
        var order = new List<string>();
        var disk = new FakeDisk();
        disk.Hook = (path, token) =>
        {
            if (path.Length <= 3)
            {
                return null;
            }

            if (Path.GetFileName(path).StartsWith("held", StringComparison.Ordinal))
            {
                held.Wait(token);
                return null;
            }

            lock (order)
            {
                order.Add(path);
            }

            gate.Wait(token);
            return null;
        };

        var heldNames = Numbered("held", NestedTree.LocalReadSlots - 1);
        var waitingNames = Enumerable.Range(0, 600).Select(index => $"v{index:D3}").ToArray();
        foreach (var name in (string[])[.. heldNames, "opener", .. waitingNames, "late"])
        {
            disk.Folder($@"Q:\{name}");
        }

        using var ordered = new NestedTree(disk.Read);
        ordered.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await ordered.LoadAsync(ordered.Find(@"Q:\")!);
        var waiting = waitingNames.Select(name => ordered.Find($@"Q:\{name}")!).ToArray();
        var late = ordered.Find(@"Q:\late")!;
        var widths = Enumerable.Range(1, waiting.Length).Select(width => (double)width).ToArray();
        new Random(9).Shuffle(widths);
        ordered.BeginFrame();
        foreach (var name in (string[])[.. heldNames, "opener"])
        {
            ordered.Request(ordered.Find($@"Q:\{name}")!, 100_000);
        }

        for (var index = 0; index < waiting.Length; index++)
        {
            ordered.Request(waiting[index], widths[index]);
        }

        int Begun()
        {
            lock (order)
            {
                return order.Count;
            }
        }

        // The opener and 100 of them read; the 101st begins, and waits.
        await WaitUntil(() => Begun() >= 1, 3_000);
        gate.Release(101);
        await WaitUntil(() => Begun() >= 102, 3_000);
        ordered.Request(late, 1_000);
        gate.Release(10_000);
        await WaitUntil(() => waiting.All(folder => folder.IsLoaded) && late.IsLoaded, 10_000);
        held.Set();

        string[] begun;
        lock (order)
        {
            begun = [.. order.Skip(1)];
        }

        var widthOf = waiting.Select((folder, index) => (folder.FullPath, Width: widths[index]))
            .ToDictionary(pair => pair.FullPath, pair => pair.Width, StringComparer.OrdinalIgnoreCase);
        var actual = begun.Where(widthOf.ContainsKey).ToList();
        var inOrder = actual.Count == waiting.Length
            && actual.Zip(actual.Skip(1), (first, second) => widthOf[first] > widthOf[second]).All(isWider => isWider);
        var lateAt = Array.IndexOf(begun, late.FullPath);
        Check($"and {waiting.Length} folders waiting, more than a ranking holds, are read widest first ({actual.Count} read, {(inOrder ? "in order" : "out of order")})",
            inOrder);
        Check($"a wider one queued after 101 of them began is read next ({lateAt} began before it)", lateAt == 101);
    }

    /// <summary>The short form of a path, or null when the volume gives none.</summary>
    private static string? ShortPath(string path)
    {
        var buffer = new char[1024];
        var length = GetShortPathNameW(path, buffer, buffer.Length);
        return length > 0 && length < buffer.Length ? new string(buffer, 0, (int)length) : null;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, char[] shortPath, int length);

    // ---- what a read allocates (J011, J078) --------------------------------------------

    /// <summary>
    /// A read of a directory of thousands of entries allocates what its
    /// listing keeps - the names and one array of each kind - and little
    /// more: no comparer for every comparison its name sorts make (J011), and
    /// no arrays thrown away as its lists grow (J078).
    /// </summary>
    private static void ReaderAllocationChecks()
    {
        const int Files = 3_000;
        const int Folders = 1_000;
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerReadAlloc", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Names in no order the file system keeps, so the sorts do real work.
            var random = new Random(11);
            for (var index = 0; index < Files; index++)
            {
                File.WriteAllBytes(Path.Combine(root, $"File {random.Next():X8} {index}.txt"), []);
            }

            for (var index = 0; index < Folders; index++)
            {
                Directory.CreateDirectory(Path.Combine(root, $"Dir {random.Next():X8} {index}"));
            }

            // The first read on this thread is the warm-up.
            var listing = NestedDirectoryReader.Read(root, CancellationToken.None);
            var allocations = new List<long>();
            for (var run = 0; run < 5; run++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                listing = NestedDirectoryReader.Read(root, CancellationToken.None);
                allocations.Add(GC.GetAllocatedBytesForCurrentThread() - before);
            }

            allocations.Sort();
            var allocated = allocations[0];

            // What the listing keeps: every name, and the arrays that hold them.
            static long StringBytes(string text) => (22 + 2L * text.Length + 7) / 8 * 8;
            var names = listing.Files.Sum(file => StringBytes(file.Name)) + listing.Folders.Sum(entry => StringBytes(entry.Name));
            var entries = (long)Files + Folders;
            var beyond = allocated - names;
            Console.WriteLine($"        a read of {Files:N0} files and {Folders:N0} folders allocates {allocated / 1024:N0} KB: {names / 1024:N0} KB of names and {beyond / entries:N0} bytes more per entry");
            Check($"the read lists every entry in name order ({listing.Files.Count:N0} files, {listing.Folders.Count:N0} folders)",
                listing.Files.Count == Files && listing.Folders.Count == Folders
                && listing.Files.Select(file => file.Name).SequenceEqual(listing.Files.Select(file => file.Name).Order(NameOrder))
                && listing.Folders.Select(entry => entry.Name).SequenceEqual(listing.Folders.Select(entry => entry.Name).Order(NameOrder)));
            Check($"and its name sorts make no comparer per comparison: at most 160 bytes per entry besides the names ({beyond / entries:N0})",
                beyond <= 160 * entries + 64 * 1024);

            // A file is 40 bytes and a folder 24 in the arrays the listing
            // keeps; lists grown by doubling and copied leave about as much
            // again thrown away, most of it on the large object heap.
            Check($"and its lists leave nothing thrown away as they grow: at most 64 bytes per entry besides the names ({beyond / entries:N0})",
                beyond <= 64 * entries + 64 * 1024);
        }
        finally
        {
            TryDelete(Path.GetDirectoryName(root)!);
        }
    }

    /// <summary>
    /// Folders gone to by name in a parent never listed are put in their
    /// places among the ones named before - the order a listing would give -
    /// and a listing of the parent afterwards keeps every one of them.
    /// </summary>
    private static async Task NamedChildOrderChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerNamedOrder", Guid.NewGuid().ToString("N"));
        string[] names = ["delta", "Alpha", "charlie", "Échelle", "bravo", "_under", "zulu", "Bravo2", "10", "9"];
        foreach (var name in names)
        {
            Directory.CreateDirectory(Path.Combine(root, "parent", name));
        }

        try
        {
            using var tree = new NestedTree();
            tree.SetRoots([new NestedRoot(root, "root", NestedFolderKind.Drive)]);
            foreach (var name in names)
            {
                await tree.MaterializePathAsync(Path.Combine(root, "parent", name));
            }

            var parent = tree.Find(Path.Combine(root, "parent"))!;
            var named = parent.AllChildren.ToArray();
            Check($"folders named one by one sit in the order a listing gives ({string.Join(" ", named.Select(child => child.Name))})",
                named.Select(child => child.Name).SequenceEqual(names.Order(NameOrder)) && !parent.IsLoaded);
            await tree.LoadAsync(parent);
            Check("and the parent's listing keeps every one of them, in the same order",
                parent.IsLoaded && parent.AllChildren.SequenceEqual(named));
        }
        finally
        {
            TryDelete(Path.GetDirectoryName(root)!);
        }
    }

    /// <summary>The order a read lists names in: culture order ignoring case, then ordinal between names it calls equal.</summary>
    private static readonly Comparer<string> NameOrder = Comparer<string>.Create(static (left, right) =>
    {
        var order = string.Compare(left, right, StringComparison.CurrentCultureIgnoreCase);
        return order != 0 ? order : string.CompareOrdinal(left, right);
    });
}
