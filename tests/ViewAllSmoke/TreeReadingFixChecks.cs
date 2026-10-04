using System.Diagnostics;
using System.IO;
using UltraExplorer.Models;
using UltraExplorer.Services;

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
        });

        return Task.CompletedTask;
    }

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
