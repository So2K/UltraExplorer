using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Windows.Threading;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Search;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// The search: what the typed text is read as, and what Everything is asked
/// for from it; the order results come in - the folder searched from first,
/// then inside it, then everywhere, and within each the best names first;
/// Everything's replies read from their bytes; the walk that stands in for
/// Everything, the folder first; and, when Everything is running here, a
/// real query for files made for the purpose, and the panel's rows, keys
/// and closing.
/// </summary>
internal static partial class Program
{
    private static async Task SearchChecks()
    {
        SearchQueryChecks();
        SearchRankingChecks();
        EverythingReplyChecks();
        await SearchWalkChecks();
        await LiveEverythingChecks();
        RunOnSta("search panel", SearchPanelOnStaAsync);
        RunOnSta("search result content hit-testing", SearchResultContentChecksAsync);
    }

    private static void SearchQueryChecks()
    {
        Section("search: reading what was typed");

        var plain = SearchQuery.Parse("  fspy ");
        Check("one word is plain", plain.IsPlain && plain.Text == "fspy");
        Check("and is asked for by its start as well", plain.PrefixSearch == "fspy*");
        Check("two words are plain, longest first", SearchQuery.Parse("fspy blender") is { IsPlain: true } two
            && two.Words.SequenceEqual(["blender", "fspy"]) && two.PrefixSearch is null);
        Check("wildcards are not plain", !SearchQuery.Parse("*.png").IsPlain);

        var extensions = SearchQuery.Parse("ext:png;jpg cat");
        Check("ext: keeps its extensions", extensions.Matches("cat.png", @"C:\x", false) && extensions.Matches("Cat.JPG", @"C:\x", false));
        Check("and leaves out the others", !extensions.Matches("cat.txt", @"C:\x", false));
        Check("and folders", !extensions.Matches("cat.png", @"C:\x", true));

        var excluded = SearchQuery.Parse("report !tmp");
        Check("a word with ! leaves out what has it", excluded.Matches("report.doc", @"C:\x", false) && !excluded.Matches("report.tmp", @"C:\x", false));

        var phrase = SearchQuery.Parse("\"annual report\"");
        Check("quotes keep a phrase together", phrase.Matches("my annual report.pdf", @"C:\x", false) && !phrase.Matches("annual-report.pdf", @"C:\x", false));

        var path = SearchQuery.Parse(@"photos\ .jpg");
        Check("a word with a separator looks at the path", path.Matches("a.jpg", @"C:\Photos\2024", false) && !path.Matches("a.jpg", @"C:\Docs", false));

        var either = SearchQuery.Parse("invoice|receipt");
        Check("| is either", either.Matches("receipt-3.pdf", @"C:\x", false) && either.Matches("Invoice.pdf", @"C:\x", false) && !either.Matches("letter.pdf", @"C:\x", false));

        var wildcard = SearchQuery.Parse("IMG_*.jpg");
        Check("a wildcard is the whole name", wildcard.Matches("IMG_0001.JPG", @"C:\x", false) && !wildcard.Matches("xIMG_0001.jpg", @"C:\x", false));
        Check("file: and folder: keep one kind", SearchQuery.Parse("folder:src").Matches("src", @"C:\x", true) && !SearchQuery.Parse("folder:src").Matches("src", @"C:\x", false));

        Check("under a folder is a path term with its separator",
            SearchQuery.Parse("report").Under(@"D:\Games") == @"path:""D:\Games\"" report");
        Check("under a drive keeps its root",
            SearchQuery.Parse("report").Under(@"D:\") == @"path:""D:\"" report");
        Check("a folder left out is a ! path term",
            SearchQuery.Parse("report").Excluding([@"L:\"]) == @"report !path:""L:\""");
        Check("and so for the start of the word too",
            SearchQuery.Parse("report").PrefixExcluding([@"L:\"]) == @"report* !path:""L:\""");

        Check("the name itself is the best match", SearchQuery.QualityOf("fspy", "fspy") == 0 && SearchQuery.QualityOf("fSpy.exe", "fspy") == 0);
        Check("then a name that starts with it", SearchQuery.QualityOf("fspyctx.c", "fspy") == 1);
        Check("then one where a word starts with it", SearchQuery.QualityOf("my-fspy.zip", "fspy") == 2 && SearchQuery.QualityOf("OpenFSpy", "fspy") == 2);
        Check("then one that has it inside", SearchQuery.QualityOf("xfspy", "fspy") == 3);
        Check("and last one that has it nowhere", SearchQuery.QualityOf("blender", "fspy") == 4);

        var marks = SearchQuery.Parse("spy blend").Highlights("fSpy-Blender.zip");
        Check("every word found is highlighted", marks.SequenceEqual([(1, 3), (5, 5)]));
    }

    private static void SearchRankingChecks()
    {
        Section("search: the folder first");

        var query = SearchQuery.Parse("fspy");
        var hits = new List<SearchHit>
        {
            Hit(@"D:\Tools", "fspy.exe"),
            Hit(@"C:\Work\sub", "fspy-notes.txt"),
            Hit(@"C:\Work", "aafspy.txt"),
            Hit(@"C:\Work", "fspy.txt"),
            Hit(@"C:\x\node_modules\pkg", "fspy.js"),
            Hit(@"C:\Other", "fspy.js"),
            Hit(@"E:\Deep\er\still", "fspy.js"),
            Hit(@"D:\Tools", "fspyctx.c"),
        };
        SearchRanking.Rank(hits, query, @"C:\Work\");
        var order = hits.Select(hit => hit.FullPath).ToArray();
        Check("right in the folder first, the name itself before one that has it", order[0] == @"C:\Work\fspy.txt" && order[1] == @"C:\Work\aafspy.txt");
        Check("then inside it", order[2] == @"C:\Work\sub\fspy-notes.txt" && hits[2].Place == SearchPlace.Below);
        Check("then elsewhere, near the folder before far from it", order[3] == @"C:\Other\fspy.js");
        Check("shallow before deep", order[4] == @"D:\Tools\fspy.exe" && order[5] == @"E:\Deep\er\still\fspy.js");
        Check("the name itself before the ones starting with it", order[6] == @"D:\Tools\fspyctx.c");
        Check("package folders sink below the rest, however well named", order[^1] == @"C:\x\node_modules\pkg\fspy.js");

        Check("inside is inside", SearchRanking.IsUnder(@"D:\Games\x", @"D:\Games"));
        Check("a folder with a longer name is not inside", !SearchRanking.IsUnder(@"D:\Games2\x", @"D:\Games"));
        Check("everything on a drive is under its root", SearchRanking.IsUnder(@"C:\x", @"C:\"));
        Check("paths share their parts from the drive down", SearchRanking.CommonDepth(@"C:\A\B", @"C:\A\C") == 2 && SearchRanking.CommonDepth(@"C:\A", @"D:\A") == 0);
        Check("a drive keeps its separator", SearchRanking.Normalize(@"C:") == @"C:\" && SearchRanking.Normalize(@"C:\Work\") == @"C:\Work");

        var none = new List<SearchHit> { Hit(@"C:\Work", "fspy.txt") };
        SearchRanking.Rank(none, query, null);
        Check("with no folder everything is elsewhere", none[0].Place == SearchPlace.Elsewhere);

        var substs = new List<(string, string)> { (@"L:\", @"E:\AiControl") };
        Check("a subst letter is left out of a search from a real path",
            SearchEngine.ExcludedFolders(@"C:\Work", substs).SequenceEqual([@"L:\"]) && SearchEngine.ExcludedFolders(null, substs).SequenceEqual([@"L:\"]));
        Check("and what it stands for is left out of a search from under it",
            SearchEngine.ExcludedFolders(@"L:\UltraExplorer", substs).SequenceEqual([@"E:\AiControl"]));

        var highlighted = new SearchHit("fSpy-Blender.zip", @"D:\x", false, 10, null, "*fSpy*-Blender.zip");
        var segments = SearchViewModel.Segments(highlighted, query);
        Check("Everything's highlighting is used as it is",
            segments.SequenceEqual([new TextSegment("fSpy", true), new TextSegment("-Blender.zip", false)]));
        var walked = SearchViewModel.Segments(new SearchHit("my-fspy.zip", @"D:\x", false, 10, null, null), query);
        Check("and without it the words are found in the name",
            walked.SequenceEqual([new TextSegment("my-", false), new TextSegment("fspy", true), new TextSegment(".zip", false)]));

        static SearchHit Hit(string directory, string name) => new(name, directory, false, 1, null, null);
    }

    private static void EverythingReplyChecks()
    {
        Section("search: Everything's replies");

        var reply = BuildList2(
            total: 58,
            flags: EverythingClient.Requested,
            [
                (1u, "fspykd", @"E:\src\filespy", -1L, 0L, "*fspy*kd"),
                (0u, "fSpy-Blender-1.0.3.zip", @"D:\OwnFiles\Plugins\Blender", 5432L, new DateTime(2025, 3, 27, 18, 54, 31).ToFileTime(), "*fSpy*-Blender-1.0.3.zip"),
            ]);
        var page = EverythingClient.Parse(reply);
        Check("the total is read", page.Total == 58);
        Check("every item is read", page.Items.Count == 2);
        Check("a folder is a folder, with no size", page.Items[0] is { IsFolder: true, Size: -1, Name: "fspykd", Directory: @"E:\src\filespy" });
        Check("a file has its size and date",
            page.Items[1] is { IsFolder: false, Size: 5432, Name: "fSpy-Blender-1.0.3.zip" } file && file.Modified == new DateTime(2025, 3, 27, 18, 54, 31));
        Check("and its highlighted name", page.Items[1].Highlighted == "*fSpy*-Blender-1.0.3.zip");

        var cut = EverythingClient.Parse(reply.AsSpan(0, reply.Length - 20));
        Check("a reply cut short keeps what it holds whole", cut.Items.Count == 1 && cut.Total == 58);
        Check("a reply of nothing is nothing", EverythingClient.Parse([]).Items.Count == 0);

        Check("the program is found in a start-up entry",
            EverythingClient.ProgramOf("\"C:\\Program Files\\Everything\\Everything.exe\" -startup") == @"C:\Program Files\Everything\Everything.exe"
            && EverythingClient.ProgramOf(@"D:\Tools\Everything.exe -startup") == @"D:\Tools\Everything.exe"
            && EverythingClient.ProgramOf(null) is null);
    }

    /// <summary>An EVERYTHING_IPC_LIST2 as Everything would send it.</summary>
    private static byte[] BuildList2(uint total, uint flags, IReadOnlyList<(uint Flags, string Name, string Path, long Size, long Modified, string Highlighted)> items)
    {
        var data = new List<byte[]>();
        foreach (var item in items)
        {
            var bytes = new List<byte>();
            void Text(string text)
            {
                bytes.AddRange(BitConverter.GetBytes((uint)text.Length));
                bytes.AddRange(Encoding.Unicode.GetBytes(text + "\0"));
            }

            Text(item.Name);
            Text(item.Path);
            bytes.AddRange(BitConverter.GetBytes(item.Size));
            bytes.AddRange(BitConverter.GetBytes(item.Modified));
            Text(item.Highlighted);
            data.Add([.. bytes]);
        }

        var header = 20 + (items.Count * 8);
        var buffer = new byte[header + data.Sum(item => item.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, total);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)items.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), 1);
        var offset = header;
        for (var index = 0; index < items.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20 + (index * 8)), items[index].Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(24 + (index * 8)), (uint)offset);
            data[index].CopyTo(buffer, offset);
            offset += data[index].Length;
        }

        return buffer;
    }

    private static async Task SearchWalkChecks()
    {
        Section("search: walking the folders");

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerSearch", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "a"));
            Directory.CreateDirectory(Path.Combine(root, "b", "c"));
            Directory.CreateDirectory(Path.Combine(root, "fspy-dir"));
            File.WriteAllText(Path.Combine(root, "a", "fspy.txt"), "x");
            File.WriteAllText(Path.Combine(root, "b", "c", "FSPY-notes.md"), "x");
            File.WriteAllText(Path.Combine(root, "b", "other.txt"), "x");

            var walk = new FolderWalk(SearchQuery.Parse("fspy"), 100);
            await walk.RunAsync(Path.Combine(root, "b"), [root], [], CancellationToken.None);
            var found = walk.Drain();
            Check($"every match is found ({found.Count})", found.Count == 3);
            Check("the folder searched from is walked first", found.Count > 0 && found[0].Name == "FSPY-notes.md");
            Check("and not twice", found.Count(hit => hit.Name == "FSPY-notes.md") == 1);
            Check("a folder that matches is found as a folder", found.Any(hit => hit is { Name: "fspy-dir", IsFolder: true }));
            Check("a file carries its size", found.First(hit => hit.Name == "fspy.txt").Size == 1);

            var limited = new FolderWalk(SearchQuery.Parse("fspy"), 1);
            await limited.RunAsync(null, [root], [], CancellationToken.None);
            Check("the walk stops at its limit", limited.IsFull && limited.Drain().Count == 1);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// With Everything running here, a search for files made for the purpose:
    /// found once Everything has heard of them, and the one in the folder
    /// searched from first.  Skipped where Everything is not running.
    /// </summary>
    private static async Task LiveEverythingChecks()
    {
        Section("search: Everything itself");
        if (!EverythingClient.IsDatabaseLoaded)
        {
            Console.WriteLine("  (Everything is not running here: skipped)");
            return;
        }

        var unique = "uxsearch" + Guid.NewGuid().ToString("N")[..12];
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerSearch", unique);
        var near = Path.Combine(root, "near");
        var far = Path.Combine(root, "far");
        try
        {
            Directory.CreateDirectory(near);
            Directory.CreateDirectory(far);
            File.WriteAllText(Path.Combine(far, unique + ".txt"), "x");
            File.WriteAllText(Path.Combine(near, "copy of " + unique + ".txt"), "x");

            EverythingPage? page = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 10_000)
            {
                page = await EverythingClient.Shared.QueryAsync(unique + " file:", 10, CancellationToken.None);
                if (page is { Items.Count: 2 })
                {
                    break;
                }

                await Task.Delay(100);
            }

            Check($"Everything finds new files ({page?.Items.Count ?? -1} in {clock.ElapsedMilliseconds} ms)", page is { Items.Count: 2, Total: 2 });
            Check("with their folders", page is not null && page.Items.All(item => item.Directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)));

            var snapshots = new List<SearchSnapshot>();
            clock.Restart();
            await new SearchEngine().RunAsync(SearchQuery.Parse(unique), near, snapshots.Add, CancellationToken.None);
            var last = snapshots.LastOrDefault();
            Check($"the engine asks Everything ({clock.ElapsedMilliseconds} ms)", last is { Source: SearchSource.Everything, IsFinal: true });
            Check("the file in the folder searched from comes first, though others are named exactly",
                last is { Hits.Count: >= 2 } && last.Hits[0].Directory.Equals(near, StringComparison.OrdinalIgnoreCase)
                && last.Hits[0].Place == SearchPlace.Here && last.Hits[1].Place == SearchPlace.Elsewhere);
            Check("counted in its group", last is { HereTotal: 1 } && last.ElsewhereTotal >= 1);
        }
        finally
        {
            TryDelete(Path.Combine(Path.GetTempPath(), "UltraExplorerSearch", unique));
        }
    }

    private static async Task SearchPanelOnStaAsync()
    {
        Section("search: the panel");

        var revealed = new List<string>();
        var opened = new List<(string, bool)>();
        using var icons = new ShellIconService();
        using var search = new SearchViewModel(icons, path => { revealed.Add(path); return Task.CompletedTask; }, (path, folder) => opened.Add((path, folder)));

        search.Text = "   ";
        Check("blank text opens nothing", !search.IsOpen);

        if (!EverythingClient.IsDatabaseLoaded)
        {
            Console.WriteLine("  (Everything is not running here: the rest is skipped, a walk of every drive is too slow for a check)");
            return;
        }

        var unique = "uxpanel" + Guid.NewGuid().ToString("N")[..12];
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerSearch", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "here"));
            Directory.CreateDirectory(Path.Combine(root, "there"));
            File.WriteAllText(Path.Combine(root, "here", unique + "-a.txt"), "x");
            File.WriteAllText(Path.Combine(root, "there", unique + "-b.txt"), "x");
            File.WriteAllText(Path.Combine(root, "there", unique + ".txt"), "x");
            var hereFolder = Path.Combine(root, "here");
            search.HereFolder = () => hereFolder;

            // Everything hears of new files within a moment; the panel is
            // tried once it has.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 10_000
                && await EverythingClient.Shared.QueryAsync(unique, 10, CancellationToken.None) is not { Items.Count: 3 })
            {
                await Task.Delay(100);
            }

            clock.Restart();
            search.Text = unique;
            Check("typing opens the panel at once", search.IsOpen);
            var complete = await SearchUntil(() => !search.IsBusy && search.Results.Count == 3, 3_000);
            Check($"the results come as you type ({search.Results.Count} in {clock.ElapsedMilliseconds} ms)", complete);
            Check("the folder's heading comes first", search.Rows.Count > 0 && search.Rows[0] is SearchHeaderRow { Place: SearchPlace.Here, Title: "In here" });
            Check("then its result", search.Rows.Count > 1 && search.Rows[1] is SearchResultViewModel { Place: SearchPlace.Here });
            Check("then everywhere else", search.Rows.Count > 2 && search.Rows[2] is SearchHeaderRow { Place: SearchPlace.Elsewhere, Title: "Everywhere else" });
            Check("the exact name leads elsewhere", search.Rows.Count > 3 && search.Rows[3] is SearchResultViewModel { Name: var name } && name == unique + ".txt");
            Check("the first result is chosen", ReferenceEquals(search.Selected, search.Results[0]));
            Check("its name is highlighted", search.Results[0].NameSegments.Any(segment => segment.IsMatch));

            search.MoveSelection(1);
            Check("down chooses the next result, past the heading", ReferenceEquals(search.Selected, search.Results[1]));
            search.MoveSelection(-5);
            Check("and up stops at the first", ReferenceEquals(search.Selected, search.Results[0]));
            await search.RevealSelectedAsync();
            Check("enter shows it on the canvas", revealed.LastOrDefault() == search.Results[0].FullPath);
            Check("and the results stay up", search.IsOpen && search.Results.Count == 3);
            search.OpenSelected();
            Check("ctrl+enter opens it", opened.LastOrDefault() == (search.Results[0].FullPath, false));

            // Going to a result moves the folder in view; the list stays as it is.
            var before = search.Rows;
            await search.RevealAsync(search.Results[1]);
            hereFolder = Path.Combine(root, "there");
            search.NoteFolder(hereFolder);
            await SearchUntil(() => !ReferenceEquals(search.Rows, before), 1_000);
            Check("going to a result does not reorder the list", ReferenceEquals(search.Rows, before));

            // Going somewhere oneself does, once the move settles.
            await Task.Delay(1_600);
            hereFolder = Path.Combine(root, "elsewhere");
            search.NoteFolder(hereFolder);
            hereFolder = Path.Combine(root, "there");
            search.NoteFolder(hereFolder);
            var reordered = await SearchUntil(() => search.Rows.Count > 0 && search.Rows[0] is SearchHeaderRow { Title: "In there" } && !search.IsBusy, 3_000);
            Check("going to another folder orders the results for it", reordered);
            Check("its two results come first", search.Results.Count == 3 && search.Results[0].Place == SearchPlace.Here && search.Results[1].Place == SearchPlace.Here
                && search.Results[0].Name == unique + ".txt");

            search.Close();
            Check("closing puts it all away", !search.IsOpen && search.Text.Length == 0 && search.Rows.Count == 0 && search.Selected is null);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<bool> SearchUntil(Func<bool> condition, int milliseconds)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            if (condition())
            {
                return true;
            }

            await Dispatcher.Yield(DispatcherPriority.Background);
            await Task.Delay(15);
        }

        return condition();
    }
}
