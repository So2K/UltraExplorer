using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// Ordering the canvas the way Explorer's column headers order a folder: by
/// name, date, type or size, either way round.  What is checked is what a
/// person would notice going wrong - an item in the wrong place, a hidden one
/// showing up, a click landing on the wrong file, the view jumping, a hitch
/// while a big tree is placed again - and that going back to names from A
/// gives back exactly the picture there was before anything was sorted.
///
/// Every order is checked against one worked out here by brute force from
/// the same data, never against what the tree itself says.  Type names come
/// from a fixed table rather than the Shell, so the expected order is the
/// same on every machine.  Everything runs on a thread with a dispatcher, as
/// in the app, because the background pass is scheduled on it.
/// </summary>
internal static partial class Program
{
    private static readonly DateTime SortEpoch = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Names from A first, then every other column both ways round.</summary>
    private static readonly ItemSort[] EverySort =
    [
        ItemSort.Default,
        new(SortColumn.Name, true),
        new(SortColumn.Modified, false),
        new(SortColumn.Modified, true),
        new(SortColumn.Type, false),
        new(SortColumn.Type, true),
        new(SortColumn.Size, false),
        new(SortColumn.Size, true)
    ];

    private static async Task NestedSortChecks()
    {
        // Every check that orders by type uses this table.  Put back in a
        // finally, because the names are cached for the whole process.
        FileTypeNames.Resolver = SortTypeName;
        try
        {
            ItemSortChecks();
            await WorkspaceSortChecks();
            FileTypeNameChecks();
            await NestedSortReaderChecks();
            await NestedSortOrderChecks(includeHidden: false);
            await NestedSortOrderChecks(includeHidden: true);
            await NestedSortLazyChecks();
            await NestedSortCanvasChecks();
            await NestedSortMarksAndFilterChecks();
            await TreeCanvasSortChecks();
            await FolderListSortChecks();
            await FolderListCutShortChecks();
            await TypeNamesOffTheUiThreadChecks();
            await NestedSortSweepChecks();
        }
        finally
        {
            FileTypeNames.Resolver = null;
        }
    }

    // ---- the choice itself -----------------------------------------------------------

    private static void ItemSortChecks()
    {
        Section("sort: the choice");

        var name = ItemSort.Default;
        Check("the default order is names from A, and it is the default value too",
            name == new ItemSort(SortColumn.Name, false) && name.IsDefault && default(ItemSort) == name);
        Check("clicking the column already chosen turns it round",
            name.Click(SortColumn.Name) == new ItemSort(SortColumn.Name, true)
            && name.Click(SortColumn.Name).Click(SortColumn.Name) == name
            && new ItemSort(SortColumn.Size, true).Click(SortColumn.Size) == new ItemSort(SortColumn.Size, false));
        Check("a first click on another column starts dates newest first and sizes largest first",
            name.Click(SortColumn.Modified) == new ItemSort(SortColumn.Modified, true)
            && name.Click(SortColumn.Size) == new ItemSort(SortColumn.Size, true));
        Check("and names and types from A",
            name.Click(SortColumn.Type) == new ItemSort(SortColumn.Type, false)
            && new ItemSort(SortColumn.Size, true).Click(SortColumn.Name) == name
            && new ItemSort(SortColumn.Name, true).Click(SortColumn.Type) == new ItemSort(SortColumn.Type, false));
        Check("only dates and sizes start from the top",
            ItemSort.DescendsFirst(SortColumn.Modified) && ItemSort.DescendsFirst(SortColumn.Size)
            && !ItemSort.DescendsFirst(SortColumn.Name) && !ItemSort.DescendsFirst(SortColumn.Type));
        Check("every order reads back from its setting as itself",
            EverySort.All(sort => ItemSort.FromSetting(sort.ToSetting()) == sort)
            && EverySort.Select(sort => sort.ToSetting()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == EverySort.Length);
        Check("the setting is plain words",
            ItemSort.Default.ToSetting() == "Name" && new ItemSort(SortColumn.Modified, true).ToSetting() == "Modified-desc");
        Check("a setting that is missing or not understood is the default",
            new[] { null, "", "   ", "Colour", "7", "Name-sideways", "Size-" }.All(text => ItemSort.FromSetting(text) == ItemSort.Default));
        Check("the columns are named as Explorer names them",
            ItemSort.Describe(SortColumn.Name) == "Name" && ItemSort.Describe(SortColumn.Modified) == "Date modified"
            && ItemSort.Describe(SortColumn.Type) == "Type" && ItemSort.Describe(SortColumn.Size) == "Size");

        // The workspace as its store reads and writes it, but in memory; the
        // store itself, on a file of the test's own, is checked below.
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var largest = new ItemSort(SortColumn.Size, true);
        var saved = JsonSerializer.Serialize(new WorkspaceState { CanvasSort = largest.ToSetting() }, json);
        Check("the chosen order is written into the workspace and read back from it",
            ItemSort.FromSetting(JsonSerializer.Deserialize<WorkspaceState>(saved, json)?.CanvasSort) == largest);
        Check("a workspace from before there was a choice, or naming one not understood, reads as names from A",
            new[] { """{"CanvasLayout":"Nested"}""", """{"CanvasSort":null}""", """{"CanvasSort":"Sideways"}""" }
                .All(text => ItemSort.FromSetting(JsonSerializer.Deserialize<WorkspaceState>(text, json)?.CanvasSort) == ItemSort.Default));
    }

    /// <summary>
    /// The workspace store itself, on a file of the test's own.  A click on a
    /// header saves once the window is idle, and closing the window saves at
    /// once: two saves can be in flight together, and the later one - the
    /// newer state - must neither fail on the earlier one's temporary file
    /// nor be overwritten by it.
    /// </summary>
    private static async Task WorkspaceSortChecks()
    {
        var folder = Path.Combine(Path.GetTempPath(), "UltraExplorerSortStore", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkspaceStore(Path.Combine(folder, "workspace.json"));
            var largest = new ItemSort(SortColumn.Size, true);
            await store.SaveAsync(new WorkspaceState { CanvasSort = largest.ToSetting() });
            Check("the store writes the chosen order and reads it back",
                ItemSort.FromSetting((await store.LoadAsync())?.CanvasSort) == largest);

            var oldest = new ItemSort(SortColumn.Modified, false);
            var failed = 0;
            var saves = new[] { largest, new ItemSort(SortColumn.Type, true), oldest }
                .Select(async sort =>
                {
                    try
                    {
                        await store.SaveAsync(new WorkspaceState { CanvasSort = sort.ToSetting() });
                    }
                    catch (IOException)
                    {
                        failed++;
                    }
                })
                .ToList();
            await Task.WhenAll(saves);
            Check("saves made at once all go through, and the last one made is what is kept",
                failed == 0 && ItemSort.FromSetting((await store.LoadAsync())?.CanvasSort) == oldest);
        }
        finally
        {
            TryDelete(folder);
        }
    }

    private static void FileTypeNameChecks()
    {
        Section("sort: type names");

        Check("a folder's type is File folder", FileTypeNames.Folder == "File folder");
        Check("a file with no extension is a plain File", FileTypeNames.Of(string.Empty) == "File");
        Check("a test can say what each type is called",
            FileTypeNames.Of("png") == "PNG image" && FileTypeNames.Of("jpeg") == FileTypeNames.Of("jpg"));
        Check("a type nobody names is its extension in capitals and File", FileTypeNames.Of("xyz") == "XYZ File");

        // The Shell itself, once: every Windows names plain text.
        FileTypeNames.Resolver = null;
        var text = FileTypeNames.Of("txt");
        FileTypeNames.Resolver = SortTypeName;
        Check($"without a test's table the Shell names the type (txt is \"{text}\")",
            text.Length > 0 && text != "TXT File");
        Check("and the table is back afterwards", FileTypeNames.Of("txt") == "Text Document");
    }

    /// <summary>What a test says each type is called; empty for the ones nobody names.</summary>
    private static string SortTypeName(string extension) => extension.ToLowerInvariant() switch
    {
        "txt" => "Text Document",
        "png" => "PNG image",
        "jpg" or "jpeg" => "JPEG image",
        "cs" => "C# Source File",
        "dll" => "Application extension",
        "pdf" => "Adobe Acrobat Document",
        "wav" => "Wave Sound",
        "mp3" => "MP3 audio",
        "md" => "Markdown Source",
        "zip" => "Compressed (zipped) Folder",
        "json" => "JSON Source",
        _ => string.Empty
    };

    // ---- dates from the disk ----------------------------------------------------------

    private static async Task NestedSortReaderChecks()
    {
        Section("sort: dates from the disk");

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerSort", Guid.NewGuid().ToString("N"));
        try
        {
            // Created first and dated afterwards: writing into a folder moves
            // its own date, so the dates go on once nothing else will.
            var dates = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < 5; index++)
            {
                var folder = Path.Combine(root, $"d{index}");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "inside.txt"), "x");
                File.WriteAllBytes(Path.Combine(root, $"f{index}.txt"), new byte[index * 10 + 1]);
            }

            for (var index = 0; index < 5; index++)
            {
                // Out of name order, so ordering by date has something to do.
                var fileDate = SortEpoch.AddDays(index * 3 % 5).AddSeconds(index);
                var folderDate = SortEpoch.AddHours(index * 2 % 5 * 7);
                File.SetLastWriteTimeUtc(Path.Combine(root, $"f{index}.txt"), fileDate);
                Directory.SetLastWriteTimeUtc(Path.Combine(root, $"d{index}"), folderDate);
                dates[$"f{index}.txt"] = fileDate;
                dates[$"d{index}"] = folderDate;
            }

            var listing = NestedDirectoryReader.Read(root, CancellationToken.None);
            Check("the reader gives every file its last-write time, in UTC ticks",
                listing.Files.Count == 5 && listing.Files.All(file => file.ModifiedTicks == dates[file.Name].Ticks));
            Check("and every sub-folder its own",
                listing.Folders.Count == 5 && listing.Folders.All(entry => entry.ModifiedTicks == dates[entry.Name].Ticks));

            using var tree = new NestedTree { IsReadingOnDemand = false };
            tree.SetRoots([new NestedRoot(root, "sort-real", NestedFolderKind.Drive)]);
            var top = tree.Find(root)!;
            await tree.LoadAsync(top);
            Check("the tree carries the dates onto its folders",
                top.AllChildren.Length == 5 && top.AllChildren.All(child => child.ModifiedTicks == dates[child.Name].Ticks));

            tree.SetSort(new ItemSort(SortColumn.Modified, true));
            tree.FlushSortWork();
            Check("ordered newest first, a real folder's sub-folders and files follow their dates",
                top.Children.Select(child => child.Name).SequenceEqual(dates.Keys.Where(key => key.StartsWith('d')).OrderByDescending(key => dates[key]))
                && top.Files.Select(file => file.Name).SequenceEqual(dates.Keys.Where(key => key.StartsWith('f')).OrderByDescending(key => dates[key])));
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- the order of every folder ------------------------------------------------------

    /// <summary>
    /// Every sort, one after the other, over a world built to tie and to cross:
    /// folders dated out of name order with ties, files of every kind with
    /// equal sizes and dates, two extensions sharing a type, names differing
    /// only in case, hidden items newest and largest of all, and the rules
    /// that hide or force items.  Without hidden items the tree runs its
    /// background pass inside SetSort, with nothing to post to; with them it
    /// posts to the dispatcher and the test flushes the pass.
    /// </summary>
    private static async Task NestedSortOrderChecks(bool includeHidden)
    {
        Section(includeHidden ? "sort: every folder's order, hidden items shown" : "sort: every folder's order");

        var disk = BuildSortWorld();
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false, IncludeHidden = includeHidden };
        if (!includeHidden)
        {
            tree.PostBackground = null;
        }

        // The drives in an order that is not name order, to see that it stays.
        tree.SetRoots(
        [
            new NestedRoot(@"R:\", "R:", NestedFolderKind.Drive),
            new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)
        ]);
        await LoadEverythingAsync(tree, _ => true);
        tree.SetUserHidden([@"Q:\hotel"]);
        tree.ForceVisible([@"Q:\.cache\wanted", @"Q:\Alpha\secret.dat"]);

        var folders = SortFolders(tree).Where(folder => folder.IsComputer || folder.IsLoaded).ToList();
        var drives = tree.Root.Children.ToArray();
        var q = tree.Find(@"Q:\")!;
        var alpha = tree.Find(@"Q:\Alpha")!;
        Check($"the world is read ({folders.Count} folders) and the drives are in the order given",
            folders.Count > 20 && drives.Select(drive => drive.Name).SequenceEqual(["R:", "Q:"]));

        // Names from A is the order the tree has always shown: taken as the
        // shown items, in listing order, before anything is sorted.
        var shownByName = folders.ToDictionary(folder => folder, folder => (Children: folder.Children.ToArray(), Files: folder.Files.ToArray()));
        var before = folders.ToDictionary(folder => folder, SortSnapshotOf);
        var paths = SortFolders(tree).Where(folder => !folder.IsComputer).ToDictionary(folder => folder.FullPath, StringComparer.OrdinalIgnoreCase);
        var changes = 0;
        tree.Changed += (_, _) => changes++;

        // ---- what SetSort does first ----
        var sortChanges = 0;
        (bool Sorting, int Generation, bool AlphaPlaced, int Changes) inEvent = default;
        tree.SortChanged += () =>
        {
            sortChanges++;
            inEvent = (tree.IsSorting, tree.SortGeneration, alpha.LayoutSortGeneration == tree.SortGeneration, changes);
        };

        var changesBefore = changes;
        var generationBefore = tree.SortGeneration;
        tree.SetSort(new ItemSort(SortColumn.Name, true));
        Check("SortChanged comes first, with the tree already sorting at the new generation and nothing placed again",
            sortChanges == 1 && inEvent.Sorting && inEvent.Generation == generationBefore + 1 && inEvent.Generation == tree.SortGeneration
            && !inEvent.AlphaPlaced && inEvent.Changes == changesBefore);
        if (!includeHidden)
        {
            Check("with nothing to post to, the whole pass runs in SetSort and is announced once",
                !tree.IsSorting && changes == changesBefore + 1 && alpha.LayoutSortGeneration == tree.SortGeneration);
        }
        else
        {
            Check("with a dispatcher to post to, SetSort itself places nothing and announces nothing",
                tree.IsSorting && changes == changesBefore && alpha.LayoutSortGeneration != tree.SortGeneration);
        }

        var generation = tree.SortGeneration;
        tree.SetSort(new ItemSort(SortColumn.Name, true));
        Check("choosing the order already chosen does nothing", tree.SortGeneration == generation && sortChanges == 1);

        // ---- every order, each way round, and back ----
        // Names from Z is already chosen, so the cycle starts at the next
        // order and comes back through all of them to names from A.
        ItemSort[] cycle = [.. EverySort.Skip(2), .. EverySort.Skip(1).Reverse().Skip(1), ItemSort.Default];
        var findProblems = new List<string>();
        var drivesKept = true;
        var findsUnaffected = true;
        var rulesKept = true;
        foreach (var sort in cycle)
        {
            tree.SetSort(sort);
            tree.FlushSortWork();

            var problems = new List<string>();
            foreach (var folder in folders)
            {
                if (folder.LayoutSortGeneration != tree.SortGeneration)
                {
                    problems.Add($"{folder} is placed for generation {folder.LayoutSortGeneration}, not {tree.SortGeneration}");
                }

                if (PlacementProblem(folder) is { } misplaced)
                {
                    problems.Add(misplaced);
                }

                var (children, files) = shownByName[folder];
                if (!folder.Children.SequenceEqual(ExpectedSortedChildren(folder, children, sort)))
                {
                    problems.Add($"{folder} shows its sub-folders as {string.Join(", ", folder.Children.Select(child => child.Name))}");
                }

                if (!folder.Files.SequenceEqual(ExpectedSortedFiles(files, sort)))
                {
                    problems.Add($"{folder} shows its files as {string.Join(", ", folder.Files.Take(12).Select(file => file.Name))}");
                }

                findProblems.AddRange(FindFileProblems(tree, folder, files, sort));
            }

            Check($"ordered by {DescribeSort(sort)}, every folder's sub-folders and files are in the expected order, row by row",
                problems.Count == 0);
            foreach (var problem in problems.Take(3))
            {
                Console.WriteLine($"        {problem}");
            }

            drivesKept &= tree.Root.Children.SequenceEqual(drives);
            findsUnaffected &= paths.All(pair => ReferenceEquals(tree.Find(pair.Key), pair.Value))
                               && folders.All(folder => ReferenceEquals(folder.AllChildren, before[folder].AllChildren)
                                                        && ReferenceEquals(folder.AllFiles, before[folder].AllFiles))
                               && ReferenceEquals(NestedTree.FindChild(alpha, "S0"), tree.Find(@"Q:\Alpha\s0"));
            rulesKept &= !q.Children.Any(child => child.Name == "hotel")
                         && q.Children.Any(child => child.Name == ".cache")
                         && q.Children.Any(child => child.Name == "$secret") == includeHidden
                         && alpha.Files.Any(file => file.Name == "secret.dat")
                         && alpha.Files.Any(file => file.Name == "desktop.ini") == includeHidden;
        }

        Check("FindFileIndex finds every shown file where its tile is, and no hidden one, under every order", findProblems.Count == 0);
        foreach (var problem in findProblems.Take(3))
        {
            Console.WriteLine($"        {problem}");
        }

        Check("This PC's drives keep the order they were given in under every order", drivesKept);
        Check("the hidden, the user-hidden and the forced rules pick the same items under every order", rulesKept);
        Check("Find and FindChild find the same folders, and the listings stay as read", findsUnaffected);
        Check($"SortChanged was raised once for every change of order ({sortChanges})", sortChanges == cycle.Length + 1);

        // ---- back to names from A ----
        var differences = folders.Select(folder => SortSnapshotDifference(folder, before[folder], SortSnapshotOf(folder)))
            .Where(difference => difference is not null)
            .ToList();
        Check("back at names from A, every folder is placed exactly as it was before any sort", differences.Count == 0);
        foreach (var difference in differences.Take(3))
        {
            Console.WriteLine($"        {difference}");
        }

        // ---- ties, and what may not move ----
        tree.SetSort(new ItemSort(SortColumn.Modified, true));
        tree.FlushSortWork();
        var names = q.Children.Select(child => child.Name).ToList();
        var tied = new[] { "bravo", "delta", "juliet" }.Select(name => names.IndexOf(name)).ToArray();
        Check("folders of the same date stay in name order, newest first as well",
            tied[0] >= 0 && tied[1] == tied[0] + 1 && tied[2] == tied[1] + 1);
        tree.SetSort(new ItemSort(SortColumn.Modified, false));
        tree.FlushSortWork();
        names = [.. q.Children.Select(child => child.Name)];
        tied = [.. new[] { "bravo", "delta", "juliet" }.Select(name => names.IndexOf(name))];
        Check("and oldest first", tied[0] >= 0 && tied[1] == tied[0] + 1 && tied[2] == tied[1] + 1);

        var bySize = new ItemSort(SortColumn.Size, true);
        tree.SetSort(bySize);
        tree.FlushSortWork();
        Check("folders have no size, so ordered by size they stay in name order",
            q.Children.SequenceEqual(shownByName[q].Children) && alpha.Children.SequenceEqual(shownByName[alpha].Children));
        var alphaFiles = alpha.Files.ToList();
        var fiveThousand = alphaFiles.FindIndex(file => file.Name == "Photo.PNG");
        Check("files of the same size stay in name order, largest first as well",
            fiveThousand >= 0 && alphaFiles[fiveThousand + 1].Name == "photo2.jpg" && alphaFiles[0].Length == alphaFiles.Max(file => file.Length));

        tree.SetSort(new ItemSort(SortColumn.Type, false));
        tree.FlushSortWork();
        Check("folders all have one type, so ordered by type they stay in name order",
            q.Children.SequenceEqual(shownByName[q].Children));
        var jpeg = alpha.Files.Where(file => file.Extension is "jpg" or "jpeg").Select(file => file.Name).ToList();
        var jpegRun = alpha.Files.Select((file, index) => (file, index)).Where(pair => pair.file.Extension is "jpg" or "jpeg").Select(pair => pair.index).ToList();
        Check("two extensions with one type name are one type, in name order among themselves",
            jpeg.Count > 2 && jpeg.SequenceEqual(jpeg.Order(StringComparer.CurrentCultureIgnoreCase)) && jpegRun[^1] - jpegRun[0] == jpegRun.Count - 1);
        var charlie = tree.Find(@"Q:\Charlie")!;
        Check("a folder whose files are all of one type keeps its listing as its files",
            ReferenceEquals(charlie.Files, charlie.AllFiles) && charlie.FilePositions is null);

        // ---- showing hidden items under an order places them in it ----
        if (!includeHidden)
        {
            var newest = new ItemSort(SortColumn.Modified, true);
            tree.SetSort(newest);
            tree.FlushSortWork();
            tree.IncludeHidden = true;
            Check("hidden items shown while ordered by date take their places in that order at once",
                q.Children.Count > 0 && q.Children[0].Name == "$secret"
                && alpha.Files.SequenceEqual(ExpectedSortedFiles(alpha.AllFiles, newest))
                && PlacementProblem(q) is null && PlacementProblem(alpha) is null);
            tree.IncludeHidden = false;
            Check("and hiding them again leaves the rest in that order",
                q.Children.All(child => child.Name != "$secret")
                && q.Children.SequenceEqual(ExpectedSortedChildren(q, shownByName[q].Children, newest))
                && alpha.Files.SequenceEqual(ExpectedSortedFiles(shownByName[alpha].Files, newest)));
        }

        // ---- a folder read again keeps its object but takes its new date ----
        var lima = tree.Find(@"Q:\lima")!;
        disk.Folder(@"Q:\lima").Modified = SortEpoch.AddDays(100);
        tree.SetSort(new ItemSort(SortColumn.Modified, true));
        tree.FlushSortWork();
        await tree.RefreshAsync(q);
        Check("a folder still there after its parent is read again keeps its object and takes the new date",
            ReferenceEquals(tree.Find(@"Q:\lima"), lima) && lima.ModifiedTicks == SortEpoch.AddDays(100).Ticks);
        Check("and the new date moves it to the front, newest first",
            q.Children.Count > 0 && ReferenceEquals(q.Children[0], lima) && PlacementProblem(q) is null
            && q.LayoutSortGeneration == tree.SortGeneration);
    }

    /// <summary>
    /// Two drives.  Q: holds a dozen folders dated out of name order with
    /// ties, a hidden one newer than all of them, a hidden one on the way to
    /// a folder asked for by name, and a link; Alpha holds every kind of
    /// file with equal sizes and dates, names differing only in case and
    /// hidden files, one of them asked for by name; Charlie's files are all
    /// one kind, delta's all one date and size; bravo has one of each.
    /// </summary>
    private static FakeDisk BuildSortWorld()
    {
        var disk = new FakeDisk();
        disk.Folder(@"Q:\");
        disk.Folder(@"R:\");

        string[] names = ["Alpha", "bravo", "Charlie", "delta", "Echo", "foxtrot", "Golf", "hotel", "India", "juliet", "Kilo", "lima"];
        int[] days = [5, 3, 9, 3, 0, 7, 9, 1, 4, 3, 8, 2];
        for (var index = 0; index < names.Length; index++)
        {
            disk.Folder($@"Q:\{names[index]}").Modified = SortEpoch.AddDays(days[index]);
        }

        var secret = disk.Folder(@"Q:\$secret");
        secret.IsHidden = true;
        secret.Modified = SortEpoch.AddDays(30);
        disk.AddFile(@"Q:\$secret", "inside.txt", 1, modified: SortEpoch);
        var cache = disk.Folder(@"Q:\.cache");
        cache.IsHidden = true;
        cache.Modified = SortEpoch.AddDays(6);
        disk.Folder(@"Q:\.cache\wanted").Modified = SortEpoch;
        var link = disk.Folder(@"Q:\link");
        link.IsReparsePoint = true;
        link.Modified = SortEpoch.AddDays(11);

        (string Name, long Length, int Day)[] named =
        [
            ("report.txt", 300, 2), ("Photo.PNG", 5_000, 1), ("photo2.jpg", 5_000, 5), ("scan.jpeg", 120, 5),
            ("notes.md", 40, 0), ("Makefile", 0, 3), ("build.cs", 900, 2), ("lib.dll", 70_000, 7),
            ("song.wav", 70_000, 4), ("manual.pdf", 2_400, 6), ("readme", 10, 3), ("data.xyz", 55, 1),
            ("Case.txt", 5, 8), ("case.TXT", 5, 8)
        ];
        foreach (var (name, length, day) in named)
        {
            disk.AddFile(@"Q:\Alpha", name, length, modified: SortEpoch.AddDays(day));
        }

        disk.AddFile(@"Q:\Alpha", "desktop.ini", 100, hidden: true, modified: SortEpoch.AddDays(40));
        disk.AddFile(@"Q:\Alpha", "thumbs.db", 900_000, hidden: true, modified: SortEpoch.AddDays(41));
        disk.AddFile(@"Q:\Alpha", "secret.dat", 1, hidden: true, modified: SortEpoch.AddDays(10));

        string[] kinds = ["txt", "png", "cs", "jpg", "jpeg", "", "md", "wav", "pdf", "dll", "xyz"];
        for (var index = 0; index < 40; index++)
        {
            var kind = kinds[index % kinds.Length];
            disk.AddFile(
                @"Q:\Alpha",
                kind.Length == 0 ? $"item{index:D2}" : $"item{index:D2}.{kind}",
                index * 37 % 11 * 100,
                modified: SortEpoch.AddHours(index * 7 % 13));
        }

        for (var index = 0; index < 8; index++)
        {
            var sub = disk.Folder($@"Q:\Alpha\s{index}");
            sub.Modified = SortEpoch.AddMinutes(index * 5 % 4);
            sub.IsHidden = index == 3;
            disk.AddFile($@"Q:\Alpha\s{index}", "a.txt", index, modified: SortEpoch.AddDays(index % 2));
            disk.AddFile($@"Q:\Alpha\s{index}", "b.png", 8 - index, modified: SortEpoch);
        }

        for (var index = 0; index < 3; index++)
        {
            disk.Folder($@"Q:\Alpha\s0\t{index}").Modified = SortEpoch.AddDays(3 - index);
            disk.AddFile($@"Q:\Alpha\s0\t{index}", $"deep{index}.cs", 10 * index, modified: SortEpoch.AddDays(index));
            disk.AddFile($@"Q:\Alpha\s0\t{index}", $"deeper{index}.md", 5, modified: SortEpoch.AddDays(9 - index));
        }

        disk.Folder(@"Q:\Alpha\s0\t0\u0").Modified = SortEpoch;

        disk.Folder(@"Q:\bravo\only").Modified = SortEpoch;
        disk.AddFile(@"Q:\bravo", "one.txt", 5, modified: SortEpoch);

        for (var index = 0; index < 10; index++)
        {
            disk.AddFile(@"Q:\Charlie", $"c{index}.txt", (9 - index) * 10, modified: SortEpoch.AddDays(index % 3));
        }

        foreach (var name in new[] { "one.txt", "two.png", "three.cs", "four", "five.pdf", "six.dll" })
        {
            disk.AddFile(@"Q:\delta", name, 7, modified: SortEpoch);
        }

        disk.Folder(@"Q:\Echo");
        for (var index = 0; index < 3; index++)
        {
            var name = new[] { "one", "two", "three" }[index];
            disk.Folder($@"R:\{name}").Modified = SortEpoch.AddDays(3 - index);
            disk.AddFile($@"R:\{name}", "x.txt", index, modified: SortEpoch.AddDays(index));
            disk.AddFile($@"R:\{name}", "y.png", 3 - index, modified: SortEpoch);
        }

        return disk;
    }

    /// <summary>Where each file is under <paramref name="sort"/>, as FindFileIndex tells it; a line for each it gets wrong.</summary>
    private static IEnumerable<string> FindFileProblems(NestedTree tree, NestedFolder folder, NestedFile[] shownByName, ItemSort sort)
    {
        var shown = new HashSet<NestedFile>(shownByName);
        foreach (var file in folder.AllFiles)
        {
            var found = tree.FindFileIndex(folder, file.Name);
            var right = shown.Contains(file)
                ? found >= 0 && found < folder.Files.Count && string.Equals(folder.Files[found].Name, file.Name, StringComparison.OrdinalIgnoreCase)
                : found == -1;
            if (!right)
            {
                yield return $"{DescribeSort(sort)}: {folder.PathOf(file)} was found at {found}";
            }
        }

        if (tree.FindFileIndex(folder, "no such file.zz") != -1)
        {
            yield return $"{DescribeSort(sort)}: a file {folder} does not hold was found";
        }
    }

    // ---- placing on demand, and the background pass ---------------------------------------

    private static async Task NestedSortLazyChecks()
    {
        Section("sort: placing on demand, and the background pass");

        var disk = BuildSortWorld();
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive), new NestedRoot(@"R:\", "R:", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        Check("a tree made on a thread with a dispatcher does its pass in the background", tree.PostBackground is not null);

        var q = tree.Find(@"Q:\")!;
        var r = tree.Find(@"R:\")!;
        var alpha = tree.Find(@"Q:\Alpha")!;
        var deep = tree.Find(@"Q:\Alpha\s0\t0")!;
        var link = tree.Find(@"Q:\link")!;
        var alphaChildren = alpha.Children.ToArray();
        var alphaFiles = alpha.Files.ToArray();
        var rChildren = r.Children.ToArray();
        var changes = 0;
        tree.Changed += (_, _) => changes++;

        var backwards = new ItemSort(SortColumn.Name, true);
        tree.SetSort(backwards);
        Check("straight after SetSort nothing is placed again and nothing is announced",
            tree.IsSorting && changes == 0 && alpha.LayoutSortGeneration != tree.SortGeneration
            && alpha.Children.SequenceEqual(alphaChildren) && alpha.Files.SequenceEqual(alphaFiles));
        Check("a folder not placed yet keeps its previous layout, whole and consistent",
            PlacementProblem(alpha) is null && PlacementProblem(q) is null);

        var idle = tree.WhenSortIdleAsync();
        Check("the pass has not finished yet", !idle.IsCompleted);

        tree.EnsureLayout(alpha);
        Check("EnsureLayout places it for the new order at once",
            alpha.LayoutSortGeneration == tree.SortGeneration && PlacementProblem(alpha) is null
            && alpha.Children.SequenceEqual(Enumerable.Reverse(alphaChildren)) && alpha.Files.SequenceEqual(Enumerable.Reverse(alphaFiles)));
        Check("and announces nothing", changes == 0);

        tree.EnsurePathLayout(deep);
        var path = new List<NestedFolder>();
        for (var folder = deep; folder is not null; folder = folder.Parent)
        {
            path.Add(folder);
        }

        Check("EnsurePathLayout places every folder from This PC down to the one asked for",
            path.All(folder => folder.LayoutSortGeneration == tree.SortGeneration && PlacementProblem(folder) is null));
        Check("while folders elsewhere wait for the pass",
            r.LayoutSortGeneration != tree.SortGeneration && r.Children.SequenceEqual(rChildren) && tree.IsSorting);

        Check("WhenSortIdleAsync completes once the pass is done", await FinishesWithin(idle, 10_000) && !tree.IsSorting);
        var folders = SortFolders(tree).ToList();
        Check("by then every folder read is placed for the current order",
            folders.All(folder => folder.LayoutSortGeneration < 0 || folder.LayoutSortGeneration == tree.SortGeneration)
            && folders.Where(folder => folder.IsLoaded).All(folder => PlacementProblem(folder) is null)
            && r.Children.SequenceEqual(Enumerable.Reverse(rChildren)));
        Check("a folder never read is left alone", link.LayoutSortGeneration < 0 && !link.IsLoaded);
        Check($"and the pass announced what it moved on the canvas ({changes} times)", changes >= 1);
        Check("when nothing is sorting, WhenSortIdleAsync is already complete", tree.WhenSortIdleAsync().IsCompleted);

        // ---- a change while the pass runs, and finishing it at once ----
        var sortChanges = 0;
        tree.SortChanged += () => sortChanges++;
        tree.SetSort(new ItemSort(SortColumn.Modified, true));
        tree.SetSort(new ItemSort(SortColumn.Size, false));
        var waiting = tree.WhenSortIdleAsync();
        var changesBeforeFlush = changes;
        tree.FlushSortWork();
        Check("FlushSortWork finishes the pass here and now, for the latest order",
            sortChanges == 2 && !tree.IsSorting && waiting.IsCompleted
            && folders.All(folder => folder.LayoutSortGeneration < 0 || folder.LayoutSortGeneration == tree.SortGeneration));
        Check("and announces it once", changes == changesBeforeFlush + 1);
        var changesAfterFlush = changes;
        await Task.Delay(50);
        Check("a slice still queued from before changes nothing", changes == changesAfterFlush && !tree.IsSorting);

        // ---- giving up waiting, and the tree going away ----
        tree.SetSort(new ItemSort(SortColumn.Type, false));
        using (var cancel = new CancellationTokenSource())
        {
            var cancelled = tree.WhenSortIdleAsync(cancel.Token);
            cancel.Cancel();
            await FinishesWithin(cancelled, 1_000);
            Check("a wait for the pass can be given up", cancelled.IsCanceled);
        }

        Check("and the pass still finishes", await FinishesWithin(tree.WhenSortIdleAsync(), 10_000) && !tree.IsSorting);

        tree.SetSort(ItemSort.Default);
        var abandoned = tree.WhenSortIdleAsync();
        tree.Dispose();
        Check("disposing the tree stops the pass and lets whoever waits for it go",
            !tree.IsSorting && await FinishesWithin(abandoned, 1_000));
    }

    // ---- on the canvas ------------------------------------------------------------------

    /// <summary>
    /// The canvas over a world whose folders and files all have dates and
    /// sizes: the view stays still when the order changes, clicks and keys
    /// follow the new places at once, and names from A afterwards draws the
    /// very same pixels as before anything was sorted.
    /// </summary>
    private static async Task NestedSortCanvasChecks()
    {
        Section("sort: on the canvas");

        var disk = BuildSortCanvasWorld();
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };

        // Where a slot is, and which way the keys go from it, are checked
        // along rows here; read down first, by the checks of folders' own orders.
        tree.Orders.Flow = LayoutOrder.AcrossThenDown;
        tree.SetRoots(
        [
            new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive, "1.2 TB free"),
            new NestedRoot(@"R:\", "R:", NestedFolderKind.Drive, "80 GB free")
        ]);
        await LoadEverythingAsync(tree, _ => true);

        var canvas = new NestedCanvas { Tree = tree };
        canvas.Measure(new Size(ViewWidth, ViewHeight));
        canvas.Arrange(new Rect(0, 0, ViewWidth, ViewHeight));
        canvas.UpdateLayout();
        string? selected = null;
        canvas.SelectRequested += (selectedPath, _) => selected = selectedPath;

        var mixed = tree.Find(@"Q:\mixed")!;
        var files = tree.Find(@"Q:\files")!;
        var m03 = tree.Find(@"Q:\mixed\m03")!;
        var mixedByName = mixed.Children.ToArray();
        var filesByName = files.Files.ToArray();

        // Pictures under names from A before anything was ever sorted.
        canvas.FitAll(animated: false);
        var fitBefore = Shoot(canvas, "nested-sort-fit-before.png");
        canvas.FlyTo(files, 0.9, animated: false);
        var filesBefore = Shoot(canvas, "nested-sort-files-before.png");

        // ---- looking over a folder's grid: the folder stays, its cells trade places ----
        // At six tenths of the view no one folder fills it: the view is a
        // look over the grid of the folder around it, which covers the view.
        canvas.FlyTo(m03, 0.6, animated: false);
        Render(canvas);
        Check("flown to a sub-folder at six tenths of the view, the folder covering the view is its parent",
            ReferenceEquals(CoverOf(canvas, canvas.Anchor ?? m03), mixed));
        var mixedBefore = canvas.ScreenRectOf(mixed)!.Value;
        var m03IndexBefore = m03.Index;

        var byDate = new ItemSort(SortColumn.Modified, true);
        var byNameChildren = SortFolders(tree).ToDictionary(folder => folder, folder => folder.Children.ToArray());
        var m03FilesByName = m03.Files.ToArray();
        tree.SetSort(byDate);

        // Asked before anything is drawn and before the pass has run: what a
        // click finds has to be placed for the new order on the spot.  Each
        // slot is checked against the brute-force order, never against the
        // folder's own list, which is what might be out of date.
        var slots = SlotHits(canvas, mixed, folder => ExpectedSortedChildren(folder, byNameChildren[folder], byDate));
        Check($"straight after a change, before anything is drawn, a click on each slot finds the folder the new order put there ({slots.Tested})",
            slots.Tested >= 6 && slots.FirstMiss.Length == 0 && tree.IsSorting);
        if (slots.FirstMiss.Length > 0)
        {
            Console.WriteLine($"        first miss: {slots.FirstMiss}");
        }

        Render(canvas);
        Check("after a change of order the folder covering the view stays exactly where it was",
            SameRect(canvas.ScreenRectOf(mixed), mixedBefore, 1e-9));
        Check("and its sub-folders take their places in the new order inside it",
            mixed.Children.SequenceEqual(ExpectedSortedChildren(mixed, mixedByName, byDate)) && PlacementProblem(mixed) is null);
        var m03Slot = new Rect(
            mixedBefore.X + m03.OffsetX * mixedBefore.Width,
            mixedBefore.Y + m03.OffsetY * mixedBefore.Width,
            m03.Scale * mixedBefore.Width,
            m03.Scale * mixedBefore.Width * NestedLayout.CellHeight);
        Check("so the sub-folder the camera was on is drawn in its new slot, not where it was",
            m03.Index != m03IndexBefore && SameRect(canvas.ScreenRectOf(m03), m03Slot, 1e-9));
        Check("what was drawn was placed without waiting for the background pass",
            tree.IsSorting && mixed.LayoutSortGeneration == tree.SortGeneration && tree.Find(@"Q:\")!.LayoutSortGeneration == tree.SortGeneration);

        var folderHits = HitTestFolders(canvas, mixed, minimumWidth: 4);
        Check($"every sub-folder on screen is hit as the folder the new order put there ({folderHits.Tested})",
            folderHits.Tested >= 3 && folderHits.HeaderMisses == 0 && folderHits.CentreMisses == 0);
        if (folderHits.FirstMiss.Length > 0)
        {
            Console.WriteLine($"        first miss: {folderHits.FirstMiss}");
        }

        var start = mixed.Children[2];
        canvas.SetSelection([start.FullPath], start.FullPath);
        Check("Right moves to the next sub-folder in the new order",
            canvas.HandleKey(Key.Right, ModifierKeys.None) && selected == mixed.Children[3].FullPath);
        Check("and Down to the one a row below in the new order",
            canvas.HandleKey(Key.Down, ModifierKeys.None) && selected == mixed.Children[3 + mixed.Grid.Columns].FullPath);

        // ---- looking into one folder: that folder stays, its contents move inside it ----
        // Flown into, a folder fills nine tenths of the view with its parent
        // covering the rest.  Holding the parent would put a sibling where
        // the folder was, in front of the user; the folder itself is held.
        tree.SetSort(ItemSort.Default);
        Check("the pass back to names from A finishes", await FinishesWithin(tree.WhenSortIdleAsync(), 10_000));
        canvas.SetSelection([], string.Empty);
        canvas.FlyTo(m03, 0.9, animated: false);
        Render(canvas);
        var m03Before = canvas.ScreenRectOf(m03)!.Value;
        tree.SetSort(byDate);
        Render(canvas);
        Check("flown into a sub-folder, a change of order holds that folder exactly where it was",
            SameRect(canvas.ScreenRectOf(m03), m03Before, 1e-9));
        Check("and its own sub-folders and files take their places in the new order inside it",
            m03.Children.SequenceEqual(ExpectedSortedChildren(m03, byNameChildren[m03], byDate))
            && m03.Files.SequenceEqual(ExpectedSortedFiles(m03FilesByName, byDate)) && PlacementProblem(m03) is null);
        var mixedAround = canvas.ScreenRectOf(mixed);
        var mixedWidth = m03Before.Width / m03.Scale;
        Check("while the folder around it is placed around its new slot",
            SameRect(mixedAround, new Rect(m03Before.X - m03.OffsetX * mixedWidth, m03Before.Y - m03.OffsetY * mixedWidth, mixedWidth, mixedWidth * NestedLayout.CellHeight), 1e-9));

        // ---- a folder of files, zoomed in until it covers the view ----
        tree.SetSort(ItemSort.Default);
        Check("the pass back to names from A finishes", await FinishesWithin(tree.WhenSortIdleAsync(), 10_000));
        canvas.FlyTo(files, 0.9, animated: false);
        var flown = canvas.ScreenRectOf(files)!.Value;
        var grid = files.FileGrid;
        var zoneCentre = new Point(
            flown.X + (grid.Left + (grid.Columns * grid.StepX - grid.Gap) / 2) * flown.Width,
            flown.Y + (grid.Top + (grid.Rows * grid.StepY - grid.Gap) / 2) * flown.Width);
        canvas.ZoomAt(zoneCentre, 2.5);
        Render(canvas);
        Check("zoomed into a folder of files, that folder covers the view",
            ReferenceEquals(CoverOf(canvas, canvas.Anchor ?? files), files));
        var filesRect = canvas.ScreenRectOf(files)!.Value;

        var bySize = new ItemSort(SortColumn.Size, true);
        tree.SetSort(bySize);
        Render(canvas);
        Check("ordered by size, the folder of files stays exactly where it was", SameRect(canvas.ScreenRectOf(files), filesRect, 1e-9));
        Check("and its files are laid out largest first, row by row", files.Files.SequenceEqual(ExpectedSortedFiles(filesByName, bySize)));
        var fileHits = HitTestFiles(canvas, files);
        Check($"every file tile on screen is hit, and its path resolved, as the file the new order put there ({fileHits.Tested})",
            fileHits.Tested >= 30 && fileHits.Misses == 0);
        if (fileHits.FirstMiss.Length > 0)
        {
            Console.WriteLine($"        first miss: {fileHits.FirstMiss}");
        }

        Check("a hidden file still resolves to nothing", canvas.Resolve(@"Q:\files\desktop.ini") is null);

        var onScreen = OnScreenFileIndex(canvas, files);
        canvas.SetSelection([files.PathOf(files.Files[onScreen])], files.PathOf(files.Files[onScreen]));
        Check("Right on a file moves to the next file in the new order",
            canvas.HandleKey(Key.Right, ModifierKeys.None) && selected == files.PathOf(files.Files[onScreen + 1]));
        Check("and, the next tile being in plain view, leaves the view alone", SameRect(canvas.ScreenRectOf(files), filesRect, 1e-9));

        Check("the background pass finishes", await FinishesWithin(tree.WhenSortIdleAsync(), 10_000));
        Render(canvas);
        Check("and nothing on screen moves when it does", SameRect(canvas.ScreenRectOf(files), filesRect, 1e-9));

        // ---- straight after a change, before anything is drawn ----
        var fileName = files.Files[onScreen].Name;
        canvas.SetSelection([files.PathOf(files.Files[onScreen])], files.PathOf(files.Files[onScreen]));
        var byType = new ItemSort(SortColumn.Type, false);
        tree.SetSort(byType);
        var typeOrder = ExpectedSortedFiles(filesByName, byType);
        var typePlace = typeOrder.FindIndex(file => file.Name == fileName);
        var nextByType = typePlace + 1 < typeOrder.Count ? files.PathOf(typeOrder[typePlace + 1]) : null;
        Check("straight after a change, before anything is drawn, a file resolves to its tile in the new order",
            canvas.Resolve(files.PathOf(typeOrder[typePlace])) is { } resolved && resolved.FileIndex == typePlace);
        Check("and Right moves to the next file in the new order",
            nextByType is not null && canvas.HandleKey(Key.Right, ModifierKeys.None) && selected == nextByType);
        Check("which the pass then finishes", await FinishesWithin(tree.WhenSortIdleAsync(), 10_000));

        // ---- every order, and back to exactly the picture there was ----
        RenderTargetBitmap? sizeShot = null;
        foreach (var sort in EverySort.Skip(1))
        {
            tree.SetSort(sort);
            Render(canvas);
            await FinishesWithin(tree.WhenSortIdleAsync(), 10_000);
            if (sort == bySize)
            {
                canvas.FlyTo(files, 0.9, animated: false);
                sizeShot = Shoot(canvas, "nested-sort-files-by-size.png");
            }
        }

        Check("ordered by size, the same view of a folder of files is a different picture",
            sizeShot is not null && DifferentPixels(filesBefore, sizeShot) > 1_000);

        tree.SetSort(ItemSort.Default);
        Render(canvas);
        Check("the pass back to names from A finishes", await FinishesWithin(tree.WhenSortIdleAsync(), 10_000));

        // Nothing selected, as before anything was sorted, and anything that
        // fades one picture into the next long finished.
        canvas.SetSelection([], string.Empty);
        await Task.Delay(400);
        canvas.FitAll(animated: false);
        var fitAfter = Shoot(canvas, "nested-sort-fit-after.png");
        canvas.FlyTo(files, 0.9, animated: false);
        var filesAfter = Shoot(canvas, "nested-sort-files-after.png");
        var fitDifferent = DifferentPixels(fitBefore, fitAfter);
        var filesDifferent = DifferentPixels(filesBefore, filesAfter);
        Check($"back at names from A after every order, fit-all draws exactly the pixels it drew before any sort ({fitDifferent} differ)",
            fitDifferent == 0);
        Check($"and so does a folder of files close up ({filesDifferent} differ)", filesDifferent == 0);

        canvas.Tree = null;
    }

    /// <summary>
    /// What is drawn over the picture, and the filter's steps through it,
    /// after a change of order.  Marks and the selection outline are drawn
    /// where the picture has their folders, so drawing them places nothing:
    /// placing every marked folder for them was work with no allowance, in
    /// the very frame after a click.  The filter's matches are stepped
    /// through in the order on screen straight after a change, and are listed
    /// in it once the tree has placed everything - also when the filter was
    /// typed while it was still placing.
    /// </summary>
    private static async Task NestedSortMarksAndFilterChecks()
    {
        Section("sort: marks, and the filter's matches");

        var disk = BuildSortCanvasWorld();
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots(
        [
            new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive, "1.2 TB free"),
            new NestedRoot(@"R:\", "R:", NestedFolderKind.Drive, "80 GB free")
        ]);
        await LoadEverythingAsync(tree, _ => true);

        var canvas = new NestedCanvas { Tree = tree };
        canvas.Measure(new Size(ViewWidth, ViewHeight));
        canvas.Arrange(new Rect(0, 0, ViewWidth, ViewHeight));
        canvas.UpdateLayout();
        string? selected = null;
        canvas.SelectRequested += (selectedPath, _) => selected = selectedPath;

        var files = tree.Find(@"Q:\files")!;
        var mixed = tree.Find(@"Q:\mixed")!;
        var m05 = tree.Find(@"Q:\mixed\m05")!;
        var m07 = tree.Find(@"Q:\mixed\m07")!;
        var g03 = tree.Find(@"Q:\g03")!;
        var filesByName = files.Files.ToArray();
        var queue = new Queue<Action>();
        var dispatcherPost = tree.PostBackground;
        tree.PostBackground = queue.Enqueue;
        try
        {
            // ---- marks and the selection on folders the picture does not show ----
            canvas.FlyTo(files, 0.9, animated: false);
            var flown = canvas.ScreenRectOf(files)!.Value;
            var grid = files.FileGrid;
            canvas.ZoomAt(
                new Point(
                    flown.X + (grid.Left + (grid.Columns * grid.StepX - grid.Gap) / 2) * flown.Width,
                    flown.Y + (grid.Top + (grid.Rows * grid.StepY - grid.Gap) / 2) * flown.Width),
                2.5);
            canvas.SetBeacons(
            [
                new NestedBeacon(@"Q:\mixed\m05\m003.txt", NestedBeaconKind.Colour, System.Windows.Media.Colors.Orange, "m003"),
                new NestedBeacon(@"Q:\g03\h02", NestedBeaconKind.Pinned, System.Windows.Media.Colors.Gold, "h02")
            ]);
            canvas.SetSelection([@"Q:\mixed\m07\m001.txt"], @"Q:\mixed\m07\m001.txt");
            await Task.Delay(50);
            Render(canvas);
            Check("zoomed into a folder of files, its siblings with the marks are off screen",
                ReferenceEquals(CoverOf(canvas, canvas.Anchor ?? files), files));

            tree.SetSort(new ItemSort(SortColumn.Modified, true));
            Render(canvas);
            Check("drawing marks and the selection on folders off screen places none of them for the new order",
                files.LayoutSortGeneration == tree.SortGeneration
                && new[] { mixed, m05, m07, g03 }.All(folder => folder.LayoutSortGeneration != tree.SortGeneration));
            Check("while a mark is still found where the new order puts it, as a click on it finds it",
                canvas.Resolve(@"Q:\mixed\m05\m003.txt") is { } marked && ReferenceEquals(marked.Folder, m05)
                && m05.Files[marked.FileIndex].Name == "m003.txt" && m05.LayoutSortGeneration == tree.SortGeneration);

            RunSlices(queue);
            canvas.SetBeacons([]);
            canvas.SetSelection([], string.Empty);

            // ---- stepping through the matches straight after a change ----
            tree.SetSort(ItemSort.Default);
            tree.FlushSortWork();
            RunSlices(queue);
            canvas.FitAll(animated: false);
            Render(canvas);
            canvas.SetFilter("*.png");
            List<string> PngsIn(IEnumerable<NestedFile> order) =>
                [.. order.Where(file => file.Extension == "png").Select(file => files.PathOf(file))];
            var byName = PngsIn(filesByName);
            Check($"the filter lists its matches in walking order, names from A ({byName.Count})",
                byName.Count >= 20 && canvas.FilterMatches.SequenceEqual(byName));

            canvas.GoToMatch(1);
            var start = selected!;
            var bySize = new ItemSort(SortColumn.Size, true);
            tree.SetSort(bySize);
            var sizeOrder = PngsIn(ExpectedSortedFiles(filesByName, bySize));
            var at = sizeOrder.IndexOf(start);
            var steps = new List<string?>();
            for (var step = 0; step < 3; step++)
            {
                canvas.GoToMatch(1);
                steps.Add(selected);
            }

            Check("straight after a change of order, Enter steps to the next match in the new order",
                at >= 0 && at + 3 < sizeOrder.Count && steps.SequenceEqual(sizeOrder.Skip(at + 1).Take(3)) && tree.IsSorting);
            canvas.GoToMatch(-1);
            Check("and Shift+Enter back to the one before it in the new order", at + 2 < sizeOrder.Count && selected == sizeOrder[at + 2]);

            // ---- the list itself, once the tree has placed everything ----
            var filterChanges = 0;
            canvas.FilterChanged += () => filterChanges++;
            var current = selected;
            var byDate = new ItemSort(SortColumn.Modified, false);
            tree.SetSort(byDate);
            var dateOrder = PngsIn(ExpectedSortedFiles(filesByName, byDate));
            var listedBefore = canvas.FilterMatches.SequenceEqual(dateOrder);
            RunSlices(queue);

            var listed = await Until(() => canvas.FilterMatches.SequenceEqual(dateOrder), 2_000);
            Check("once the tree has placed everything, the matches are listed in the new order, the step the user is on kept",
                !listedBefore && listed && filterChanges > 0
                && canvas.FilterCursor >= 0 && canvas.FilterMatches[canvas.FilterCursor] == current);

            // ---- a filter typed while the tree is still placing ----
            // From an overview, so the change of order does not place the
            // folder of files for the camera's sake before the filter reads it.
            canvas.FitAll(animated: false);
            canvas.SetFilter(null);
            tree.SetSort(bySize);
            canvas.SetFilter("*.png");
            var typedDuring = canvas.FilterMatches.SequenceEqual(sizeOrder);
            RunSlices(queue);

            Check("a filter typed while the tree is still placing lists its matches in the new order once it is done",
                !typedDuring && await Until(() => canvas.FilterMatches.SequenceEqual(sizeOrder), 2_000));
        }
        finally
        {
            canvas.SetFilter(null);
            canvas.Tree = null;
            tree.FlushSortWork();
            RunSlices(queue);
            tree.PostBackground = dispatcherPost;
        }
    }

    /// <summary>Waits, letting the dispatcher run, until <paramref name="condition"/> holds; false if it never did in time.</summary>
    private static async Task<bool> Until(Func<bool> condition, int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > milliseconds)
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
    }

    /// <summary>
    /// Two drives.  Q:\mixed has twenty-five folders dated in a shuffled order,
    /// each with files and folders of its own; Q:\files has three hundred
    /// files whose sizes, dates and kinds are all out of name order, and two
    /// hidden ones; Q:'s own folders are dated out of name order too.
    /// </summary>
    private static FakeDisk BuildSortCanvasWorld()
    {
        var disk = new FakeDisk();
        disk.Folder(@"Q:\");
        disk.Folder(@"R:\");

        for (var outer = 0; outer < 25; outer++)
        {
            var name = $@"Q:\mixed\m{outer:D2}";
            disk.Folder(name).Modified = SortEpoch.AddDays((outer * 11 + 5) % 25);
            for (var file = 0; file < 8; file++)
            {
                disk.AddFile(name, $"m{file:D3}.txt", 100 + file * 3 % 8, modified: SortEpoch.AddHours(file * 5 % 8));
            }

            for (var inner = 0; inner < 4; inner++)
            {
                disk.Folder($@"{name}\n{inner:D2}").Modified = SortEpoch.AddDays(inner * 3 % 4);
                disk.AddFiles($@"{name}\n{inner:D2}", 3, "n");
            }
        }

        disk.AddFiles(@"Q:\mixed", 40, "mixed-");

        string[] stems = ["report", "photo", "notes", "build", "track"];
        string[] extensions = ["txt", "png", "md", "cs", "wav", "pdf"];
        for (var index = 0; index < 300; index++)
        {
            disk.AddFile(
                @"Q:\files",
                $"{stems[index % stems.Length]}-{index:D3}.{extensions[index % extensions.Length]}",
                1024L * (index * 37 % 300),
                modified: SortEpoch.AddMinutes(index * 13 % 300));
        }

        disk.AddFile(@"Q:\files", "desktop.ini", 900_000, hidden: true, modified: SortEpoch.AddDays(90));
        disk.AddFile(@"Q:\files", "thumbs.db", 800_000, hidden: true, modified: SortEpoch.AddDays(91));
        for (var index = 0; index < 6; index++)
        {
            disk.Folder($@"Q:\files\sub{index}").Modified = SortEpoch.AddDays(index * 5 % 6);
            disk.AddFiles($@"Q:\files\sub{index}", 3, "s");
        }

        disk.Folder(@"Q:\mixed").Modified = SortEpoch.AddDays(1);
        disk.Folder(@"Q:\files").Modified = SortEpoch.AddDays(9);
        disk.Folder(@"Q:\empty").Modified = SortEpoch.AddDays(5);
        for (var outer = 0; outer < 6; outer++)
        {
            disk.Folder($@"Q:\g{outer:D2}").Modified = SortEpoch.AddDays(outer * 4 % 7);
            for (var inner = 0; inner < 8; inner++)
            {
                disk.Folder($@"Q:\g{outer:D2}\h{inner:D2}").Modified = SortEpoch.AddDays(inner * 3 % 8);
                disk.AddFiles($@"Q:\g{outer:D2}\h{inner:D2}", 5, "g");
            }
        }

        foreach (var (name, day) in new[] { ("one", 3), ("two", 2), ("three", 1) })
        {
            disk.Folder($@"R:\{name}").Modified = SortEpoch.AddDays(day);
            disk.AddFiles($@"R:\{name}", 5, name);
        }

        return disk;
    }

    /// <summary>
    /// The folder the canvas draws everything else inside: the deepest one,
    /// from <paramref name="start"/> up, that covers the whole view with the
    /// canvas's own eight pixels to spare.
    /// </summary>
    private static NestedFolder? CoverOf(NestedCanvas canvas, NestedFolder start)
    {
        for (var folder = start; folder is not null; folder = folder.Parent)
        {
            if (canvas.ScreenRectOf(folder) is { } rect
                && rect.Left <= -8 && rect.Top <= -8 && rect.Right >= ViewWidth + 8 && rect.Bottom >= ViewHeight + 8)
            {
                return folder;
            }
        }

        return null;
    }

    /// <summary>
    /// Hit tests the title band of every slot on screen in <paramref name="folder"/>'s
    /// grid, and a level further down in the sub-folders that are on screen,
    /// and names the first slot whose hit is not the folder <paramref name="expected"/>
    /// puts there.  The grid depends only on how many there are, so where a
    /// slot is can be read before anything is placed again; which folder is
    /// in it is the question.
    /// </summary>
    private static (int Tested, string FirstMiss) SlotHits(
        NestedCanvas canvas,
        NestedFolder folder,
        Func<NestedFolder, IReadOnlyList<NestedFolder>> expected)
    {
        var view = new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight);
        var (tested, firstMiss) = (0, string.Empty);
        var level = new List<NestedFolder> { folder };
        for (var depth = 0; depth < 2; depth++)
        {
            var next = new List<NestedFolder>();
            foreach (var current in level)
            {
                if (canvas.ScreenRectOf(current) is not { } rect)
                {
                    continue;
                }

                var order = expected(current);
                var grid = current.Grid;
                for (var slot = 0; slot < grid.Count && slot < order.Count; slot++)
                {
                    var (x, y) = grid.Origin(slot);
                    var width = grid.Scale * rect.Width;
                    var cell = new Rect(rect.X + x * rect.Width, rect.Y + y * rect.Width, width, width * NestedLayout.CellHeight);
                    var title = new Point(cell.X + cell.Width / 2, cell.Y + cell.Width * NestedLayout.HeaderHeight / 2);
                    if (width < 8 || !view.Contains(title))
                    {
                        continue;
                    }

                    tested++;
                    if (canvas.HitTest(title) is not { IsFile: false } hit || !ReferenceEquals(hit.Folder, order[slot]))
                    {
                        firstMiss = firstMiss.Length > 0
                            ? firstMiss
                            : $"slot {slot} of {current} hit {canvas.HitTest(title)?.Path ?? "nothing"}, expected {order[slot]}";
                    }

                    if (view.IntersectsWith(cell))
                    {
                        next.Add(order[slot]);
                    }
                }
            }

            level = next;
        }

        return (tested, firstMiss);
    }

    /// <summary>
    /// A file whose tile, and the next one's, are wholly on screen and well
    /// clear of its edges: an arrow key onto a tile near an edge slides the
    /// view to show it, which is right, but not what a check of the order
    /// wants to happen at the same time.
    /// </summary>
    private static int OnScreenFileIndex(NestedCanvas canvas, NestedFolder folder)
    {
        const double margin = 60;
        var view = new Rect(margin, margin, canvas.ActualWidth - 2 * margin, canvas.ActualHeight - 2 * margin);
        var rect = canvas.ScreenRectOf(folder)!.Value;
        var grid = folder.FileGrid;
        bool OnScreen(int index)
        {
            var (x, y) = grid.Origin(index);
            return view.Contains(new Rect(
                rect.X + x * rect.Width,
                rect.Y + y * rect.Width,
                grid.TileWidth * rect.Width,
                grid.TileHeight * rect.Width));
        }

        for (var index = 0; index + 1 < folder.Files.Count; index++)
        {
            if (OnScreen(index) && OnScreen(index + 1))
            {
                return index;
            }
        }

        return 0;
    }

    private static int DifferentPixels(BitmapSource first, BitmapSource second)
    {
        if (first.PixelWidth != second.PixelWidth || first.PixelHeight != second.PixelHeight)
        {
            return int.MaxValue;
        }

        var area = new Int32Rect(0, 0, first.PixelWidth, first.PixelHeight);
        var left = PixelsOf(first, area);
        var right = PixelsOf(second, area);
        var different = 0;
        for (var index = 0; index < left.Length; index++)
        {
            if (left[index] != right[index])
            {
                different++;
            }
        }

        return different;
    }

    // ---- the tree canvas and the folder list ----------------------------------------------

    /// <summary>
    /// The tree canvas: a folder's children are laid out folders first, then
    /// files, each in the chosen order, row by row - first over nodes made
    /// here, then through the graph over a real folder, where a change of
    /// order must leave a node placed by hand alone, report how far the node
    /// in view was carried, and put everything back exactly on names from A.
    /// </summary>
    private static async Task TreeCanvasSortChecks()
    {
        Section("sort: the tree canvas");

        var parent = new ViewAllNodeViewModel(ViewEntry(@"Q:\tree", "tree", ViewAllEntryKind.Folder, null, 0), 0) { IsExpanded = true };
        foreach (var entry in ViewEntries(@"Q:\tree"))
        {
            parent.Children.Add(new ViewAllNodeViewModel(entry, 1, parent));
        }

        var layout = new ViewAllLayoutService();
        Check("the tree canvas starts at names from A", layout.Sort == ItemSort.Default);
        var wrong = new List<string>();
        foreach (var sort in EverySort)
        {
            layout.Sort = sort;
            layout.Arrange([parent]);
            var shown = RowMajor(parent.Children).Select(node => node.DisplayName).ToList();
            var expected = ExpectedEntryOrder(parent.Children.Select(node => node.Entry), sort).Select(entry => entry.DisplayName).ToList();
            if (!shown.SequenceEqual(expected))
            {
                wrong.Add($"{DescribeSort(sort)}: {string.Join(", ", shown)}");
            }
        }

        Check("it lays a folder's children out folders first, then files, each in every order, row by row", wrong.Count == 0);
        foreach (var line in wrong.Take(3))
        {
            Console.WriteLine($"        {line}");
        }

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerSortTree", Guid.NewGuid().ToString("N"));
        try
        {
            WriteViewEntries(root);
            using var graph = new ViewAllGraphService();
            await graph.InitializeAsync();
            var node = (await graph.AddRootAsync(root))!;
            await graph.ExpandAsync(node);
            Check("a real folder of folders and files is read into the graph", node.Children.Count == ViewEntries(root).Count);

            var placed = graph.Nodes.Where(item => item.HasLayoutPosition).ToDictionary(item => item, item => item.Location);
            wrong.Clear();
            foreach (var sort in EverySort.Skip(1))
            {
                graph.SetSort(sort);
                var shown = RowMajor(node.Children).Select(child => child.DisplayName).ToList();
                var expected = ExpectedEntryOrder(node.Children.Select(child => child.Entry), sort).Select(entry => entry.DisplayName).ToList();
                if (graph.Sort != sort || !shown.SequenceEqual(expected))
                {
                    wrong.Add($"{DescribeSort(sort)}: {string.Join(", ", shown)}");
                }
            }

            Check("the graph lays the real folder out in every order, by the dates and sizes read from the disk", wrong.Count == 0);
            foreach (var line in wrong.Take(3))
            {
                Console.WriteLine($"        {line}");
            }

            graph.SetSort(ItemSort.Default);
            Check("back at names from A, every node is exactly where it was before any sort",
                placed.All(pair => pair.Key.Location == pair.Value));

            var shifts = new List<Vector>();
            graph.LayoutShifted += shifts.Add;
            var anchor = node.Children.First(child => child.DisplayName == "alpha.cs");
            var anchorBefore = anchor.Location;
            graph.SetSort(new ItemSort(SortColumn.Size, true), anchor);
            Check("the node in view is followed: how far the new order carried it is reported once",
                anchor.Location != anchorBefore && shifts.Count == 1 && (anchorBefore + shifts[0] - anchor.Location).Length < 1e-6);

            var dragged = node.Children.First(child => child.DisplayName == "gamma");
            var manual = new Point(9_000, 7_000);
            dragged.Location = manual;
            graph.SetSort(new ItemSort(SortColumn.Modified, true));
            Check("a node placed by hand keeps its place when the order changes",
                dragged.Location == manual && dragged.HasManualPosition);
            graph.SetSort(ItemSort.Default);

            await TreeModelSortChecks(root);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// The tree's view model while the nested canvas is the one on screen: a
    /// click on a header there must not spend its time laying out a tree
    /// nobody can see, and the tree must come back in the order chosen.
    /// </summary>
    private static async Task TreeModelSortChecks(string root)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "UltraExplorerSortModel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            using var icons = new ShellIconService();
            using var model = NewTree(scratch, icons);
            model.IsCanvasShown = false;
            await model.InitializeAsync(root);
            if (!model.TryGetNode(root, out var folder))
            {
                Check("the tree's view model opens a real folder", false);
                return;
            }

            await model.ExpandAsync(folder);
            var before = folder.Children.ToDictionary(child => child, child => child.Location);
            var largest = new ItemSort(SortColumn.Size, true);
            var expected = ExpectedEntryOrder(folder.Children.Select(child => child.Entry), largest).Select(entry => entry.DisplayName);
            model.Sort = largest;
            Check("with the tree canvas away, a change of order orders the list and leaves the tree as it was",
                model.Sort == largest && model.FolderList.Sort == largest
                && before.Count == ViewEntries(root).Count && before.All(pair => pair.Key.Location == pair.Value));

            model.IsCanvasShown = true;
            Check("and the tree comes back laid out in that order",
                RowMajor(folder.Children).Select(child => child.DisplayName).SequenceEqual(expected));

            model.Sort = ItemSort.Default;
            Check("while it is showing, a change of order lays it out at once",
                RowMajor(folder.Children).Select(child => child.DisplayName)
                    .SequenceEqual(ExpectedEntryOrder(folder.Children.Select(child => child.Entry), ItemSort.Default).Select(entry => entry.DisplayName)));

            model.Orders.SetFolder(folder.FullPath, largest);
            Check("a folder's own order lays out that folder's children on the tree at once",
                model.Sort == ItemSort.Default && RowMajor(folder.Children).Select(child => child.DisplayName).SequenceEqual(expected));
            model.Orders.ResetFolder(folder.FullPath);
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    /// <summary>
    /// The folder list: with nothing typed, folders then files, each in the
    /// chosen order; with something typed, the best match first whatever the
    /// order; and a change of order re-reads nothing and keeps the row lit.
    /// </summary>
    private static async Task FolderListSortChecks()
    {
        Section("sort: the folder list");

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerSortList", Guid.NewGuid().ToString("N"));
        try
        {
            WriteViewEntries(root);
            var files = new ViewAllFileSystemService();
            using var icons = new ShellIconService();
            var options = new ViewAllGraphOptions();
            var reads = 0;
            var list = new FolderListViewModel(
                (path, cancellation) =>
                {
                    reads++;
                    return files.GetChildrenAsync(path, options, cancellation);
                },
                (_, _) => Task.CompletedTask,
                _ => false,
                icons)
            {
                IsVisible = true
            };

            await list.NavigateAsync(root);
            var byName = list.Items.ToList();
            Check("the list starts at names from A, folders first, as it always read",
                list.Sort == ItemSort.Default && byName.Count == ViewEntries(root).Count
                && byName.Select(item => item.DisplayName).SequenceEqual(ExpectedEntryOrder(byName.Select(item => item.Entry), ItemSort.Default).Select(entry => entry.DisplayName)));

            var readsBefore = reads;
            var wrong = new List<string>();
            foreach (var sort in EverySort.Skip(1).Append(ItemSort.Default))
            {
                list.Sort = sort;
                var shown = list.Items.Select(item => item.DisplayName).ToList();
                var expected = ExpectedEntryOrder(byName.Select(item => item.Entry), sort).Select(entry => entry.DisplayName).ToList();
                if (!shown.SequenceEqual(expected))
                {
                    wrong.Add($"{DescribeSort(sort)}: {string.Join(", ", shown)}");
                }
            }

            Check("with nothing typed, the rows are folders then files, each in every order", wrong.Count == 0);
            foreach (var line in wrong.Take(3))
            {
                Console.WriteLine($"        {line}");
            }

            Check("and changing the order reads nothing again", reads == readsBefore);
            Check("back at names from A the rows are the very rows there were", list.Items.SequenceEqual(byName));

            // A list box takes each row a collection announces as work of its
            // own; a folder of thousands of files reordered row by row was tens
            // of milliseconds of a header click.
            var announced = new List<NotifyCollectionChangedAction>();
            void Announced(object? sender, NotifyCollectionChangedEventArgs e) => announced.Add(e.Action);
            list.Items.CollectionChanged += Announced;
            list.Sort = new ItemSort(SortColumn.Modified, true);
            list.Items.CollectionChanged -= Announced;
            Check("a change of order reaches the list box as one change, not one per row",
                announced.SequenceEqual([NotifyCollectionChangedAction.Reset]));
            list.Sort = ItemSort.Default;

            list.Selected = list.Items.First(item => item.DisplayName == "notes.md");
            list.Sort = new ItemSort(SortColumn.Size, true);
            Check("the highlighted row stays highlighted when the order changes", list.Selected?.DisplayName == "notes.md");

            list.Sort = ItemSort.Default;
            list.Filter = "a";
            var matched = list.Items.Select(item => item.DisplayName).ToList();
            var unchanged = true;
            foreach (var sort in EverySort.Skip(1))
            {
                list.Sort = sort;
                unchanged &= list.Items.Select(item => item.DisplayName).SequenceEqual(matched);
            }

            Check($"with something typed, the best match stays first whatever the order ({matched.Count} rows)",
                matched.Count >= 4 && unchanged);
            list.Filter = string.Empty;
            list.Sort = ItemSort.Default;
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// A folder with more entries than one read of the list holds.  The rows
    /// kept are the first ones in the order shown - newest first starts at
    /// the newest file of all, not the newest of the first names read - and
    /// a change of order reads them again for the new one; below the cap a
    /// change of order still reads nothing.
    /// </summary>
    private static async Task FolderListCutShortChecks()
    {
        Section("sort: a folder bigger than the list reads");

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerSortCut", Guid.NewGuid().ToString("N"));
        try
        {
            // Forty files, dated so the newest are last by name: the first
            // thirty-two by name leave the eight newest out.
            Directory.CreateDirectory(root);
            var dates = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < 40; index++)
            {
                var name = $"f{index:D2}.bin";
                var path = Path.Combine(root, name);
                File.WriteAllBytes(path, new byte[(index * 7) % 40]);
                dates[name] = SortEpoch.AddDays(index);
                File.SetLastWriteTimeUtc(path, dates[name]);
            }

            var files = new ViewAllFileSystemService();
            using var icons = new ShellIconService();
            var options = new ViewAllGraphOptions(MaximumChildrenPerFolder: 32);
            var reads = 0;
            var list = new FolderListViewModel(
                (path, sort, cancellation) =>
                {
                    reads++;
                    return files.GetChildrenAsync(path, options, cancellation, sort, keepFirstShown: true);
                },
                (_, _) => Task.CompletedTask,
                _ => false,
                icons)
            {
                IsVisible = true
            };

            // Which thirty-two names the first read keeps is up to the order
            // the file system hands them out in; that they are in name order
            // is what the list promises.
            bool InNameOrder() =>
                list.Items.Count == 32
                && list.Items.Select(item => item.DisplayName).SequenceEqual(list.Items.Select(item => item.DisplayName).Order(StringComparer.CurrentCultureIgnoreCase));

            await list.NavigateAsync(root);
            Check("cut short under names from A, the list holds as many as one read does, by name, as it always did",
                InNameOrder() && list.CountText == "32+");

            var readsBefore = reads;
            list.Selected = list.Items.First(item => item.DisplayName == "f30.bin");
            list.Sort = new ItemSort(SortColumn.Modified, true);
            var newest = dates.Keys.OrderByDescending(name => dates[name]).Take(32).ToList();
            Check("ordered newest first, the list starts with the newest entry of all, read again for it",
                await Until(() => !list.IsLoading && list.Items.Select(item => item.DisplayName).SequenceEqual(newest), 5_000)
                && reads == readsBefore + 1);
            Check("and the row that was lit is lit again", list.Selected?.DisplayName == "f30.bin");

            list.Sort = ItemSort.Default;
            Check("back at names from A it is read again, and holds names in their order again",
                await Until(() => !list.IsLoading && reads == readsBefore + 2 && InNameOrder(), 5_000));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// Ordering the tree canvas and the folder list by type never asks the
    /// Shell on the UI thread: a kind of file not named yet is ordered by the
    /// name it would have if the Shell knew nothing better, looked up in the
    /// background, and the rows and nodes take their places once it is in.
    /// A test's table stands in for the Shell here, made to answer only in
    /// the background, and only when the test lets it.
    /// </summary>
    private static async Task TypeNamesOffTheUiThreadChecks()
    {
        Section("sort: type names off the UI thread");

        // Kinds no other check meets, named so that their real order is not
        // the order of their stand-ins: ZZA File, ZZB File, ZZC File.
        static string Named(string extension) => extension.ToLowerInvariant() switch
        {
            "zza" => "Zulu kind",
            "zzb" => "Alpha kind",
            "zzc" => "Mike kind",
            _ => SortTypeName(extension)
        };

        var uiThread = Environment.CurrentManagedThreadId;
        var askedHere = 0;
        using var gate = new ManualResetEventSlim(false);
        string Deferred(string extension)
        {
            if (Environment.CurrentManagedThreadId == uiThread)
            {
                Interlocked.Increment(ref askedHere);
            }
            else
            {
                gate.Wait(10_000);
            }

            return Named(extension);
        }

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerSortKinds", Guid.NewGuid().ToString("N"));
        FileTypeNames.DefersResolver = true;
        FileTypeNames.Resolver = Deferred;
        try
        {
            Check("with the lookup in the background, a kind not named yet answers its stand-in at once",
                !FileTypeNames.TryGet("zzq", out var standIn) && standIn == "ZZQ File" && askedHere == 0);

            Directory.CreateDirectory(Path.Combine(root, "sub"));
            string[] names = ["a1.zza", "b1.zzb", "c1.zzc", "a2.zza", "c2.zzc", "b2.zzb", "plain"];
            foreach (var name in names)
            {
                File.WriteAllText(Path.Combine(root, name), "x");
            }

            List<string> OrderedBy(Func<string, string> kindName) =>
            [
                "sub",
                .. names
                    .Order(StringComparer.CurrentCultureIgnoreCase)
                    .Select((name, index) => (Name: name, Index: index))
                    .OrderBy(pair => kindName(pair.Name), StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(pair => pair.Index)
                    .Select(pair => pair.Name)
            ];

            string KindOf(string name, bool real)
            {
                var extension = new NestedFile(name, false, 0).Extension;
                return extension.Length == 0 ? "File" : real ? Named(extension) : extension.ToUpperInvariant() + " File";
            }

            var standIns = OrderedBy(name => KindOf(name, real: false));
            var real = OrderedBy(name => KindOf(name, real: true));

            // The list, and the tree canvas over the same folder.
            var files = new ViewAllFileSystemService();
            using var icons = new ShellIconService();
            var list = new FolderListViewModel(
                (path, cancellation) => files.GetChildrenAsync(path, new ViewAllGraphOptions(), cancellation),
                (_, _) => Task.CompletedTask,
                _ => false,
                icons)
            {
                IsVisible = true
            };
            await list.NavigateAsync(root);

            using var graph = new ViewAllGraphService();
            await graph.InitializeAsync();
            var node = (await graph.AddRootAsync(root))!;
            await graph.ExpandAsync(node);

            var byType = new ItemSort(SortColumn.Type, false);
            list.Sort = byType;
            graph.SetSort(byType);
            List<string> Rows() => [.. list.Items.Select(item => item.DisplayName)];
            List<string> Nodes() => [.. RowMajor(node.Children).Select(child => child.DisplayName)];
            Check("ordered by type before the kinds are named, the list and the tree order by the stand-ins, and ask nothing here",
                Rows().SequenceEqual(standIns) && Nodes().SequenceEqual(standIns) && askedHere == 0);

            // What each row says under a type order: the stand-in for now,
            // and the real name as soon as it is in - said again, since the
            // list shows what a row said until the row tells it otherwise.
            var fileRows = list.Items.Where(item => !item.IsDirectory).ToList();
            var toldAgain = new HashSet<FolderListItem>();
            void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(FolderListItem.Detail) && sender is FolderListItem row)
                {
                    lock (toldAgain)
                    {
                        toldAgain.Add(row);
                    }
                }
            }

            foreach (var row in fileRows)
            {
                row.PropertyChanged += OnRowChanged;
            }

            Check("a row says the stand-in of its kind while the name is looked up",
                fileRows.Count == names.Length && fileRows.All(row => row.Detail == KindOf(row.DisplayName, real: false)));

            gate.Set();
            Check("once the names are in, both take their places by the real names",
                await Until(() => Rows().SequenceEqual(real) && Nodes().SequenceEqual(real), 5_000) && askedHere == 0);
            bool ToldAgain(FolderListItem row)
            {
                lock (toldAgain)
                {
                    return toldAgain.Contains(row);
                }
            }

            Check("and every row that said a stand-in says the real name, and tells the list so",
                await Until(
                    () => fileRows.All(row => row.Detail == KindOf(row.DisplayName, real: true))
                        && fileRows.Where(row => new NestedFile(row.DisplayName, false, 0).Extension.Length > 0).All(ToldAgain),
                    5_000));
            foreach (var row in fileRows)
            {
                row.PropertyChanged -= OnRowChanged;
            }
            Check("and the tree canvas and the list order a kind the same way as each other", Rows().SequenceEqual(Nodes()));

            list.Sort = ItemSort.Default;
            graph.SetSort(ItemSort.Default);
        }
        finally
        {
            gate.Set();
            FileTypeNames.DefersResolver = false;
            FileTypeNames.Resolver = SortTypeName;
            TryDelete(root);
        }
    }

    /// <summary>
    /// A folder's entries as a listing would give them: folders named out of
    /// case order, files of every type with sizes and dates that tie and cross.
    /// </summary>
    private static List<ViewAllEntryDescriptor> ViewEntries(string root)
    {
        (string Name, ViewAllEntryKind Kind, long? Size, int Day)[] entries =
        [
            ("gamma", ViewAllEntryKind.Folder, null, 2),
            ("Alpha", ViewAllEntryKind.Folder, null, 7),
            ("beta", ViewAllEntryKind.Folder, null, 2),
            ("Delta", ViewAllEntryKind.Folder, null, 4),
            ("zeta.txt", ViewAllEntryKind.File, 10, 3),
            ("Beta.png", ViewAllEntryKind.File, 5_000, 1),
            ("alpha.cs", ViewAllEntryKind.File, 700, 6),
            ("notes.md", ViewAllEntryKind.File, 700, 3),
            ("image.jpg", ViewAllEntryKind.File, 1, 8),
            ("image2.jpeg", ViewAllEntryKind.File, 5_000, 0),
            ("Makefile", ViewAllEntryKind.File, 0, 5),
            ("data.xyz", ViewAllEntryKind.File, 42, 9)
        ];
        return [.. entries.Select(entry => ViewEntry(Path.Combine(root, entry.Name), entry.Name, entry.Kind, entry.Size, entry.Day))];
    }

    private static ViewAllEntryDescriptor ViewEntry(string path, string name, ViewAllEntryKind kind, long? size, int day) =>
        new(path, name, kind, false, false, size, SortEpoch.AddDays(day));

    /// <summary>The same entries on the disk with their sizes and dates, the folders dated last so that nothing moves them again.</summary>
    private static void WriteViewEntries(string root)
    {
        var entries = ViewEntries(root);
        Directory.CreateDirectory(root);
        foreach (var entry in entries.Where(entry => entry.Kind == ViewAllEntryKind.File))
        {
            File.WriteAllBytes(entry.FullPath, new byte[entry.SizeBytes ?? 0]);
            File.SetLastWriteTimeUtc(entry.FullPath, entry.ModifiedUtc);
        }

        foreach (var entry in entries.Where(entry => entry.Kind == ViewAllEntryKind.Folder))
        {
            Directory.CreateDirectory(entry.FullPath);
            Directory.SetLastWriteTimeUtc(entry.FullPath, entry.ModifiedUtc);
        }
    }

    /// <summary>Nodes in the order they are laid out: row by row from the top, and left to right along each row.</summary>
    private static List<ViewAllNodeViewModel> RowMajor(IEnumerable<ViewAllNodeViewModel> nodes) =>
        [.. nodes.OrderBy(node => Math.Round(node.Location.Y, 6)).ThenBy(node => node.Location.X)];

    /// <summary>
    /// Entries in the order a sort should give them, worked out from nothing
    /// but the entries: folders before files, each by name from A, then by
    /// the column - dates for both, sizes and types for files only - with
    /// ties left in name order.
    /// </summary>
    private static List<ViewAllEntryDescriptor> ExpectedEntryOrder(IEnumerable<ViewAllEntryDescriptor> entries, ItemSort sort)
    {
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        var ordered = new List<ViewAllEntryDescriptor>();
        foreach (var group in entries.GroupBy(entry => entry.Kind).OrderBy(group => group.Key))
        {
            var byName = group.OrderBy(entry => entry.DisplayName, comparer).ToList();
            var isFile = group.Key == ViewAllEntryKind.File;
            ordered.AddRange(sort.Column switch
            {
                SortColumn.Name => sort.Descending ? Enumerable.Reverse(byName).ToList() : byName,
                SortColumn.Modified => SortedBy(byName, entry => entry.ModifiedUtc.Ticks, Comparer<long>.Default, sort.Descending),
                SortColumn.Size when isFile => SortedBy(byName, entry => entry.SizeBytes ?? 0, Comparer<long>.Default, sort.Descending),
                SortColumn.Type when isFile => SortedBy(byName, entry => FileTypeNames.Of(new NestedFile(entry.DisplayName, false, 0).Extension), comparer, sort.Descending),
                _ => byName
            });
        }

        return ordered;
    }

    // ---- what the background pass costs --------------------------------------------------

    /// <summary>
    /// A disk the size of the canvas performance check - forty folders of
    /// thirty-five of thirty-five, about fifty thousand, each with up to eight
    /// files - placed again for every order.  The slices are run one at a
    /// time here, from a queue of the test's own, so each can be timed; the
    /// collector's pauses are taken out of them, because a collection that
    /// happens to land in a slice is the runtime's cost, not the slice's.
    /// Then one folder as big as a listing gets, which is placed in one go.
    /// </summary>
    private static async Task NestedSortSweepChecks()
    {
        Section("sort: what the background pass costs");

        using var tree = new NestedTree(SortPerformanceRead) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"P:\", "P:", NestedFolderKind.Drive, "4 TB free")]);
        await LoadEverythingAsync(tree, _ => true);
        var folders = SortFolders(tree).Count() - 1;
        var files = SortFolders(tree).Sum(folder => folder.AllFiles.Length);
        Check($"the synthetic disk holds {folders:N0} folders and {files:N0} files", folders >= 50_000 && files >= 190_000);

        foreach (var extension in new[] { "txt", "png", "cs", "mp3", "pdf", "zip", "json", "" })
        {
            FileTypeNames.Of(extension);
        }

        var dispatcherPost = tree.PostBackground;
        var queue = new Queue<Action>();
        tree.PostBackground = queue.Enqueue;

        // One whole pass for one order, its slices run one at a time and
        // timed, with the collector's pauses taken out of each.
        (double SetSort, int Slices, double Worst, double WorstRaw, double Total) Sweep(ItemSort sort)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var started = Stopwatch.GetTimestamp();
            tree.SetSort(sort);
            var setting = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            var (slices, worst, worstRaw) = (0, 0.0, 0.0);
            var sweep = Stopwatch.StartNew();
            while (queue.TryDequeue(out var slice))
            {
                var pausedBefore = GC.GetTotalPauseDuration();
                var sliceStarted = Stopwatch.GetTimestamp();
                slice();
                var elapsed = Stopwatch.GetElapsedTime(sliceStarted).TotalMilliseconds;
                var paused = (GC.GetTotalPauseDuration() - pausedBefore).TotalMilliseconds;
                worstRaw = Math.Max(worstRaw, elapsed);
                worst = Math.Max(worst, elapsed - paused);
                slices++;
            }

            return (setting, slices, worst, worstRaw, sweep.Elapsed.TotalMilliseconds);
        }

        var slowestSetSort = 0.0;
        var slowestSweep = (Milliseconds: 0.0, Sort: ItemSort.Default);
        var previous = ItemSort.Default;
        ItemSort[] orders = [.. EverySort.Skip(1), ItemSort.Default];
        foreach (var sort in orders)
        {
            var result = Sweep(sort);

            // A machine busy with something else can take the thread away in
            // the middle of a slice.  A slice slow for reasons of its own is
            // slow the second time too; one that was only interrupted is not.
            if (result.Worst > 8 || result.Total > 1_500 || result.SetSort > 2)
            {
                Sweep(previous);
                var again = Sweep(sort);
                Console.WriteLine($"  note  {DescribeSort(sort)} measured again: slowest slice {result.Worst:0.0} then {again.Worst:0.0} ms, pass {result.Total:0} then {again.Total:0} ms");
                result = (
                    Math.Min(result.SetSort, again.SetSort),
                    again.Slices,
                    Math.Min(result.Worst, again.Worst),
                    Math.Min(result.WorstRaw, again.WorstRaw),
                    Math.Min(result.Total, again.Total));
            }

            previous = sort;
            slowestSetSort = Math.Max(slowestSetSort, result.SetSort);
            if (result.Total > slowestSweep.Milliseconds)
            {
                slowestSweep = (result.Total, sort);
            }

            Report($"placing them all for {DescribeSort(sort)}: the slowest of {result.Slices} slices ({result.Worst:0.0} ms less collector pauses, {result.WorstRaw:0.0} ms with them)",
                (long)Math.Ceiling(result.Worst), 8);
        }

        Check("every folder read is then placed for the current order",
            !tree.IsSorting && SortFolders(tree).All(folder => folder.LayoutSortGeneration < 0 || folder.LayoutSortGeneration == tree.SortGeneration));
        Report($"the slowest whole pass ({DescribeSort(slowestSweep.Sort)}, {slowestSweep.Milliseconds:0} ms exactly)",
            (long)Math.Ceiling(slowestSweep.Milliseconds), 1_500);
        Report($"SetSort itself, the slowest of {orders.Length} ({slowestSetSort:0.000} ms exactly)", (long)Math.Ceiling(slowestSetSort), 2);

        // The same pass as the app runs it: slices queued on the dispatcher
        // below input and rendering.
        tree.PostBackground = dispatcherPost;
        var throughDispatcher = Stopwatch.StartNew();
        tree.SetSort(new ItemSort(SortColumn.Modified, true));
        var finished = await FinishesWithin(tree.WhenSortIdleAsync(), 10_000);
        throughDispatcher.Stop();
        Check("the pass through the dispatcher finishes", finished && !tree.IsSorting);
        Report("the whole pass through the dispatcher", throughDispatcher.ElapsedMilliseconds, 1_500);

        NestedSortFrameChecks(tree);

        // ---- one folder as big as a listing gets ----
        // A folder is placed in one go, so the biggest one is the longest the
        // pass can hold the window: System32 is about this.
        var random = new Random(5);
        string[] kinds = ["dll", "exe", "txt", "png", "mui", "sys", "cpl", "msc", "xml", "dat", "", "ini"];
        var bigFiles = new NestedFile[NestedTree.MaximumFiles];
        for (var index = 0; index < bigFiles.Length; index++)
        {
            var kind = kinds[random.Next(kinds.Length)];
            bigFiles[index] = new NestedFile(
                kind.Length == 0 ? $"f{index:D5}" : $"f{index:D5}.{kind}",
                random.Next(10) == 0,
                random.Next(1_000_000),
                SortEpoch.Ticks + random.Next(5_000) * TimeSpan.TicksPerMinute);
        }

        foreach (var kind in kinds)
        {
            FileTypeNames.Of(kind);
        }

        var bigFolders = Enumerable.Range(0, 3_000)
            .Select(index => new NestedEntry($"d{index:D4}", index % 7 == 0, false, SortEpoch.Ticks + random.Next(100) * TimeSpan.TicksPerHour))
            .ToList();
        using var bigTree = new NestedTree((path, _) => string.Equals(path, @"B:\", StringComparison.OrdinalIgnoreCase)
            ? new NestedListing(bigFolders, bigFiles.Length, bigFiles.Count(file => file.IsHidden), false) { Files = bigFiles }
            : new NestedListing([], 0, 0, false))
        {
            IsReadingOnDemand = false
        };
        bigTree.SetRoots([new NestedRoot(@"B:\", "B:", NestedFolderKind.Drive)]);
        var drive = bigTree.Find(@"B:\")!;
        await bigTree.LoadAsync(drive);
        var shownNames = new HashSet<string>(drive.Files.Select(file => file.Name), StringComparer.Ordinal);

        var slowestPlacing = (Milliseconds: 0.0, Sort: ItemSort.Default);
        var slowestFinding = (Milliseconds: 0.0, Sort: ItemSort.Default);
        var wrongFinds = 0;
        foreach (var sort in EverySort.Skip(1))
        {
            bigTree.SetSort(sort);
            var placing = Stopwatch.GetTimestamp();
            bigTree.EnsureLayout(drive);
            var placed = Stopwatch.GetElapsedTime(placing).TotalMilliseconds;
            if (placed > slowestPlacing.Milliseconds)
            {
                slowestPlacing = (placed, sort);
            }

            // Every tenth file, hidden ones among them, looked for by name.
            var finding = Stopwatch.GetTimestamp();
            for (var index = 0; index < bigFiles.Length; index += 10)
            {
                var name = bigFiles[index].Name;
                var found = bigTree.FindFileIndex(drive, name);
                if (shownNames.Contains(name) ? found < 0 || drive.Files[found].Name != name : found != -1)
                {
                    wrongFinds++;
                }
            }

            var finds = Stopwatch.GetElapsedTime(finding).TotalMilliseconds;
            if (finds > slowestFinding.Milliseconds)
            {
                slowestFinding = (finds, sort);
            }
        }

        bigTree.SetSort(ItemSort.Default);
        bigTree.FlushSortWork();
        Check($"in a folder of {bigFiles.Length:N0} files and {bigFolders.Count:N0} folders every file is found where its tile is under every order, a hidden one nowhere",
            wrongFinds == 0 && PlacementProblem(drive) is null);
        Report($"placing that folder for the slowest order ({DescribeSort(slowestPlacing.Sort)}, {slowestPlacing.Milliseconds:0.0} ms exactly)",
            (long)Math.Ceiling(slowestPlacing.Milliseconds), 25);
        Report($"finding {bigFiles.Length / 10:N0} of its files by name under the slowest order ({DescribeSort(slowestFinding.Sort)}, {slowestFinding.Milliseconds:0.0} ms exactly)",
            (long)Math.Ceiling(slowestFinding.Milliseconds), 100);
    }

    /// <summary>
    /// What one frame of the canvas places after a change of order, and what
    /// the tree's pass does behind it, over the performance disk.  A frame
    /// has an allowance for placing and draws what is past it as it was; the
    /// allowance is for the placing alone, so a picture that takes longer to
    /// draw than the allowance lasts still places some of it every frame, and
    /// frames on their own get through all of it.  The pass places first what
    /// the picture could not, shares its allowance with what was placed on
    /// demand since its last slice, and says once, at the end, that it moved
    /// something - not after every slice, which redrew the whole canvas every
    /// frame for folders nobody was looking at.
    /// </summary>
    private static void NestedSortFrameChecks(NestedTree tree)
    {
        Section("sort: a frame's placing, and the pass behind it");

        var dispatcherPost = tree.PostBackground;
        var queue = new Queue<Action>();
        tree.PostBackground = queue.Enqueue;
        var canvas = new NestedCanvas { Tree = tree };
        try
        {
            tree.SetSort(ItemSort.Default);
            tree.FlushSortWork();
            RunSlices(queue);

            canvas.Measure(new Size(ViewWidth, ViewHeight));
            canvas.Arrange(new Rect(0, 0, ViewWidth, ViewHeight));
            canvas.UpdateLayout();
            canvas.FitAll(animated: false);
            Render(canvas);

            // ---- frames on their own, the pass held back ----
            // The whole disk: tens of thousands of cells, which take longer
            // to draw than a frame's allowance for placing lasts.
            tree.SetSort(new ItemSort(SortColumn.Size, true));
            var (frames, idle, worstPlacing, worstRaw, firstDrawing) = (0, 0, 0.0, 0.0, 0.0);
            while (frames < 400)
            {
                // A collection that lands in the middle of placing is the
                // runtime's time, not the frame's: taken out, as for slices.
                var pausedBefore = GC.GetTotalPauseDuration();
                canvas.RenderAsFrame();
                var paused = (GC.GetTotalPauseDuration() - pausedBefore).TotalMilliseconds;
                frames++;
                firstDrawing = frames == 1 ? canvas.LastRenderMilliseconds : firstDrawing;
                worstPlacing = Math.Max(worstPlacing, canvas.LastPlacingMilliseconds - paused);
                worstRaw = Math.Max(worstRaw, canvas.LastPlacingMilliseconds);
                if (!canvas.DrewOutOfDate)
                {
                    break;
                }

                if (canvas.LastPlacingMilliseconds <= 0)
                {
                    idle++;
                }
            }

            Check($"with the pass held back, frames of the loop place all they draw by themselves, some every frame ({frames} frames, the first drawn in {firstDrawing:0.0} ms)",
                !canvas.DrewOutOfDate && idle == 0);
            Report($"the longest one of those frames spent placing ({worstPlacing:0.00} ms less collector pauses, {worstRaw:0.00} ms with them, allowance 3 ms)",
                (long)Math.Ceiling(worstPlacing), 6);

            // ---- what the picture could not place goes first ----
            var byLevel = new List<NestedFolder>();
            var level = new List<NestedFolder> { tree.Root };
            while (level.Count > 0)
            {
                byLevel.AddRange(level);
                level = [.. level.SelectMany(folder => folder.AllChildren)];
            }

            // The last folders the pass would come to on its own.
            var last = byLevel.TakeLast(150).ToList();
            tree.SetSort(new ItemSort(SortColumn.Modified, false));
            tree.PlaceFirst(last);
            queue.Dequeue()();
            Check("the next slice places what the picture drew out of date before anything else",
                last.All(folder => folder.LayoutSortGeneration == tree.SortGeneration) && tree.IsSorting && queue.Count == 1);
            tree.FlushSortWork();
            RunSlices(queue);

            // ---- a slice shares its allowance with placing on demand ----
            tree.SetSort(new ItemSort(SortColumn.Size, false));
            var drive = tree.Find(@"P:\")!;
            var placingTicks = 0L;
            var realPlacings = 0;
            var wantedTicks = Stopwatch.Frequency * 12 / 1000;
            var next = byLevel.Count - 1;
            while (placingTicks < wantedTicks && next > 1)
            {
                var folder = byLevel[next--];
                // A loop's wall time includes stamp-only folders, collection
                // pauses between calls and scheduling. None of that consumes
                // the pass's shared allowance. Count only calls that must
                // really reorder at least two files for the changed sort.
                var reordersFiles = folder.Files.Count >= 2
                    && folder.LayoutSortGeneration != tree.SortGeneration
                    && folder.PlacedSort != tree.SortOf(folder);
                var started = reordersFiles ? Stopwatch.GetTimestamp() : 0;
                tree.EnsureLayout(folder);
                if (reordersFiles)
                {
                    placingTicks += Stopwatch.GetTimestamp() - started;
                    realPlacings++;
                }
            }

            var placedMilliseconds = placingTicks * 1000.0 / Stopwatch.Frequency;
            Check($"the shared-budget fixture performs more than a 4 ms slice of real file placing ({placedMilliseconds:0.0} ms, {realPlacings:N0} folders)",
                placedMilliseconds > 4 && drive.LayoutSortGeneration != tree.SortGeneration);
            queue.Dequeue()();
            Check("after more than a slice's worth of placing on demand, the next slice places nothing and waits a frame",
                drive.LayoutSortGeneration != tree.SortGeneration && tree.IsSorting && queue.Count == 1);
            queue.Dequeue()();
            Check("and the slice after it goes on as usual", drive.LayoutSortGeneration == tree.SortGeneration);

            // ---- one announcement, at the end ----
            // The shared-budget fixture has already placed many folders and
            // consumed two slices. Its remainder can fit in one slice after
            // the runtime warms up. Observe a complete new pass, including
            // its first slice, rather than depending on that remainder's size.
            tree.FlushSortWork();
            RunSlices(queue);
            var changes = 0;
            var early = false;
            void Changed(object? sender, EventArgs e)
            {
                changes++;
                early |= tree.IsSorting;
            }

            tree.Changed += Changed;
            tree.SetSort(new ItemSort(SortColumn.Type, true));
            var slices = RunSlices(queue);

            tree.Changed -= Changed;
            Check($"a pass of {slices} slices says the canvas changed once, when it is done, not after every slice",
                slices > 1 && changes == 1 && !early && !tree.IsSorting);
        }
        finally
        {
            canvas.Tree = null;
            tree.FlushSortWork();
            RunSlices(queue);
            tree.PostBackground = dispatcherPost;
        }
    }

    /// <summary>
    /// Runs the slices a test's own queue holds, including one left over from
    /// a pass that was finished at once: the tree posts its next slice only
    /// after the last one it posted has run, so one never run holds up every
    /// pass after it.
    /// </summary>
    private static int RunSlices(Queue<Action> queue)
    {
        var count = 0;
        while (queue.TryDequeue(out var slice))
        {
            slice();
            count++;
        }

        return count;
    }

    /// <summary>
    /// The canvas performance disk with dates, sizes and kinds of file mixed
    /// in every folder, so that every order has real work to do.
    /// </summary>
    private static NestedListing SortPerformanceRead(string path, CancellationToken cancellationToken)
    {
        var listing = PerformanceRead(path, cancellationToken);
        var hash = 2166136261u;
        foreach (var character in path)
        {
            hash = (hash ^ character) * 16777619u;
        }

        var folders = listing.Folders
            .Select((entry, index) => entry with { ModifiedTicks = SortEpoch.Ticks + hash * (uint)(index + 7) % 100_000 * TimeSpan.TicksPerSecond })
            .ToList();

        string[] extensions = ["txt", "png", "cs", "mp3", "pdf", "zip", "json", ""];
        var files = new List<NestedFile>(listing.Files.Count);
        for (var index = 0; index < listing.Files.Count; index++)
        {
            var extension = extensions[(hash >> (index + 3)) % (uint)extensions.Length];
            files.Add(new NestedFile(
                extension.Length == 0 ? $"file{index}" : $"file{index}.{extension}",
                false,
                1000L * ((hash >> index) % 13),
                SortEpoch.Ticks + (hash >> index) % 1000 * TimeSpan.TicksPerMinute));
        }

        files.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
        return new NestedListing(folders, listing.FileCount, 0, false) { Files = files };
    }

    // ---- helpers -----------------------------------------------------------------------

    /// <summary>This PC and every folder the tree knows, hidden ones included.</summary>
    private static IEnumerable<NestedFolder> SortFolders(NestedTree tree) => Descendants(tree.Root, includeHidden: true).Prepend(tree.Root);

    private static string DescribeSort(ItemSort sort) => sort.Column switch
    {
        SortColumn.Name => sort.Descending ? "name from Z" : "name from A",
        SortColumn.Modified => sort.Descending ? "date, newest first" : "date, oldest first",
        SortColumn.Size => sort.Descending ? "size, largest first" : "size, smallest first",
        _ => sort.Descending ? "type from Z" : "type from A"
    };

    /// <summary>
    /// The sub-folders in the order a sort should show them, from the ones
    /// shown under names from A: names from Z is that list backwards; dates
    /// move them; size and type do not, since every folder is the same there.
    /// This PC's drives never move.
    /// </summary>
    private static List<NestedFolder> ExpectedSortedChildren(NestedFolder folder, IReadOnlyList<NestedFolder> shownByName, ItemSort sort)
    {
        if (folder.IsComputer)
        {
            return [.. shownByName];
        }

        return sort.Column switch
        {
            SortColumn.Name when sort.Descending => [.. Enumerable.Reverse(shownByName)],
            SortColumn.Modified => SortedBy(shownByName, child => child.ModifiedTicks, Comparer<long>.Default, sort.Descending),
            _ => [.. shownByName]
        };
    }

    /// <summary>The files in the order a sort should show them, from the ones shown under names from A.</summary>
    private static List<NestedFile> ExpectedSortedFiles(IReadOnlyList<NestedFile> shownByName, ItemSort sort) => sort.Column switch
    {
        SortColumn.Name => sort.Descending ? [.. Enumerable.Reverse(shownByName)] : [.. shownByName],
        SortColumn.Modified => SortedBy(shownByName, file => file.ModifiedTicks, Comparer<long>.Default, sort.Descending),
        SortColumn.Size => SortedBy(shownByName, file => file.Length, Comparer<long>.Default, sort.Descending),
        _ => SortedBy(shownByName, file => FileTypeNames.Of(file.Extension), StringComparer.CurrentCultureIgnoreCase, sort.Descending)
    };

    /// <summary>
    /// Items by a key, either way round, and those with equal keys in the
    /// order they came in - which, coming from a listing, is name order.
    /// </summary>
    private static List<T> SortedBy<T, TKey>(IReadOnlyList<T> items, Func<T, TKey> key, IComparer<TKey> comparer, bool descending)
    {
        var direction = descending ? Comparer<TKey>.Create((left, right) => comparer.Compare(right, left)) : comparer;
        return [.. items
            .Select((item, index) => (Item: item, Index: index))
            .OrderBy(pair => key(pair.Item), direction)
            .ThenBy(pair => pair.Index)
            .Select(pair => pair.Item)];
    }

    /// <summary>Everything about where a folder's contents are, for comparing before and after.</summary>
    private sealed record SortSnapshot(
        int Index,
        double OffsetX,
        double OffsetY,
        double Scale,
        NestedFolder[] Children,
        NestedGrid Grid,
        NestedFileGrid FileGrid,
        bool FilesAreTheListing,
        NestedFile[] Files,
        bool HasPositions,
        NestedFolder[] AllChildren,
        NestedFile[] AllFiles);

    private static SortSnapshot SortSnapshotOf(NestedFolder folder) => new(
        folder.Index,
        folder.OffsetX,
        folder.OffsetY,
        folder.Scale,
        [.. folder.Children],
        folder.Grid,
        folder.FileGrid,
        ReferenceEquals(folder.Files, folder.AllFiles),
        [.. folder.Files],
        folder.FilePositions is not null,
        folder.AllChildren,
        folder.AllFiles);

    private static string? SortSnapshotDifference(NestedFolder folder, SortSnapshot before, SortSnapshot after)
    {
        if (before.Index != after.Index || before.OffsetX != after.OffsetX || before.OffsetY != after.OffsetY || before.Scale != after.Scale)
        {
            return $"{folder}: at {after.Index} ({after.OffsetX}, {after.OffsetY}) x{after.Scale}, was {before.Index} ({before.OffsetX}, {before.OffsetY}) x{before.Scale}";
        }

        if (!before.Children.SequenceEqual(after.Children) || before.Grid != after.Grid)
        {
            return $"{folder}: its sub-folders are not placed as they were";
        }

        if (before.FilesAreTheListing != after.FilesAreTheListing || !before.Files.SequenceEqual(after.Files) || before.FileGrid != after.FileGrid)
        {
            return $"{folder}: its files are not placed as they were";
        }

        if (after.HasPositions)
        {
            return $"{folder}: still keeps positions for another order";
        }

        return null;
    }

    private static async Task<bool> FinishesWithin(Task task, int milliseconds) =>
        await Task.WhenAny(task, Task.Delay(milliseconds)) == task;
}
