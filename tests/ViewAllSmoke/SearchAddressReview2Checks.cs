using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Search;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// The second review's issues with the search and the address bar (see
/// coordination/review/20261004-claude-review2-full.md), each replayed on its
/// own, as it went wrong.
/// </summary>
internal static partial class Program
{
    private static Task SearchAddressReview2Checks()
    {
        SearchOpenQuoteExclusionChecks();
        SearchSpacedOrChecks();
        RunOnSta("search: a walk's first report", SearchWalkFirstReportAsync);
        RunOnSta("search: going to another folder while walking", SearchWalkFolderMoveAsync);
        RunOnSta("search: a walk ordered from the folder gone to", SearchWalkOrderedFromAsync);
        return Task.CompletedTask;
    }

    /// <summary>
    /// J113: with a subst letter, Everything is asked for the text with the
    /// folders it stands for left out - <c>!path:"L:\"</c> appended.  A quote
    /// still open while the phrase is typed took that into the phrase, and
    /// nothing was found until the quote was closed.
    /// </summary>
    private static void SearchOpenQuoteExclusionChecks()
    {
        Section("search: an open quote does not take in the folders left out (J113)");
        var open = SearchQuery.Parse("\"my doc").Excluding([@"L:\"]);
        Check($"the quote is closed before the folders left out ({open})", open == @"""my doc"" !path:""L:\""");
        var closed = SearchQuery.Parse("\"my doc\" x").Excluding([@"L:\"]);
        Check($"a closed one is sent as typed ({closed})", closed == @"""my doc"" x !path:""L:\""");
        var none = SearchQuery.Parse("\"my doc").Excluding([]);
        Check($"with nothing left out, the text goes as typed ({none})", none == "\"my doc");
    }

    /// <summary>
    /// J112: to Everything <c>a | b</c>, <c>a |b</c> and <c>a| b</c> are all
    /// <c>a|b</c>.  The walk that stands in for it read the bar standing alone
    /// as an OR of nothing, which no name matched - so it found nothing - and
    /// a bar at one end of a word cut that word off from its other side, so
    /// both sides had to match.
    /// </summary>
    private static void SearchSpacedOrChecks()
    {
        Section("search: the walk reads an OR with spaces around it (J112)");
        const string folder = @"C:\x";
        var spaced = SearchQuery.Parse("*.mp3 | *.wav");
        Check("'*.mp3 | *.wav' finds the one and the other, and nothing else",
            spaced.Matches("song.mp3", folder, false) && spaced.Matches("take.wav", folder, false) && !spaced.Matches("notes.txt", folder, false));
        var before = SearchQuery.Parse("foo |bar");
        Check("'foo |bar' is foo or bar, not both",
            before.Matches("foo.txt", folder, false) && before.Matches("bar.txt", folder, false) && !before.Matches("baz.txt", folder, false));
        var after = SearchQuery.Parse("foo| bar");
        Check("'foo| bar' too",
            after.Matches("foo.txt", folder, false) && after.Matches("bar.txt", folder, false) && !after.Matches("baz.txt", folder, false));
        var typing = SearchQuery.Parse("report |");
        Check("a bar typed last, with nothing after it yet, keeps what the word before it finds",
            typing.Matches("report.docx", folder, false) && !typing.Matches("notes.docx", folder, false));
        var words = SearchQuery.Parse("foo bar");
        Check("words with no bar between them must still both match",
            words.Matches("foo bar.txt", folder, false) && !words.Matches("foo.txt", folder, false));
        Check($"and both sides of the OR are lit in a name ({string.Join(",", spaced.Words)})",
            spaced.Words.Contains(".mp3") && spaced.Words.Contains(".wav"));
    }

    /// <summary>
    /// J030: a walk told the panel nothing until it found something.  Made
    /// after another search - "reportx" after "report" - the panel went on
    /// showing that one's rows, count and status, and Enter opened one of
    /// them, for as long as the walk found nothing, which can be all of it.
    /// Walked here from a drive's root, far more than a walk gets through in
    /// the second and a half given it, for a name nothing has.
    /// </summary>
    private static async Task SearchWalkFirstReportAsync()
    {
        Section("search: a walk that has found nothing yet says so at once (J030)");
        var method = typeof(SearchEngine).GetMethod("WalkAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        if (method is null || !method.GetParameters().Any(parameter => parameter.Name == "drives"))
        {
            Check("the drives a walk goes over can be given it", false);
            return;
        }

        var here = Path.GetPathRoot(Environment.SystemDirectory)!;
        var query = SearchQuery.Parse("uxnothing" + Guid.NewGuid().ToString("N")[..12]);
        using var stop = new CancellationTokenSource();
        var reports = new ConcurrentQueue<(SearchSnapshot Snapshot, long At)>();
        var clock = Stopwatch.StartNew();
        Action<SearchSnapshot> publish = snapshot =>
        {
            reports.Enqueue((snapshot, clock.ElapsedMilliseconds));
            stop.Cancel();
        };
        var arguments = method.GetParameters().Select(parameter => parameter.Name switch
        {
            "query" => query,
            "here" => here,
            "drives" => Array.Empty<string>(),
            "reason" => "checked",
            "everythingFailed" => false,
            "publish" => publish,
            "cancellationToken" => stop.Token,
            _ => (object?)null,
        }).ToArray();
        stop.CancelAfter(1_500);
        await (Task)method.Invoke(new SearchEngine(), arguments)!;
        var (first, at) = reports.TryPeek(out var report) ? report : default;
        Check($"the first report comes while the walk goes on, not at its end ({(first is null ? "none in 1.5 s" : $"after {at} ms, {first.Hits.Count} found, final: {first.IsFinal}")})",
            first is { IsFinal: false, Hits.Count: 0 });

        // That report on screen: the search before is gone, and Enter has nothing to open.
        using var icons = new ShellIconService();
        var opened = new List<string>();
        using var search = new SearchViewModel(icons, _ => Task.CompletedTask, (path, _) => opened.Add(path));
        SearchOpenForTest(search, @"C:\UxFirstReport");
        var before = SearchQuery.Parse("report");
        var old = new SearchHit("report.docx", @"C:\UxFirstReport", false, 1, null, null);
        SearchRanking.Rank([old], before, @"C:\UxFirstReport");
        SearchApplyForTest(search, new SearchSnapshot([old], 1, 0, SearchSource.Walk, true, "1 found", 1), before);
        SearchApplyForTest(search, first ?? new SearchSnapshot([], 0, 0, SearchSource.Walk, false, "Searching…", 1), SearchQuery.Parse("reportx"));
        search.OpenSelected();
        Check($"with it on screen the rows of the search before are gone ({search.Results.Count}) and it says so ({search.EmptyText})",
            search.Results.Count == 0 && search.Selected is null && search.EmptyText == "Searching…");
        Check("and Enter opens nothing", opened.Count == 0);
        search.Close();
    }

    /// <summary>
    /// A walk's snapshot of <paramref name="hits"/>, ordered from
    /// <paramref name="folder"/>, which it says it is; standing in for an
    /// Everything that failed to answer, or for one still starting.
    /// </summary>
    private static SearchSnapshot SearchWalkedFrom(string folder, SearchQuery query, bool everythingFailed, params SearchHit[] hits)
    {
        var ordered = hits.ToList();
        SearchRanking.Rank(ordered, query, folder);
        var here = ordered.Count(hit => hit.Place != SearchPlace.Elsewhere);
        var snapshot = new SearchSnapshot(ordered, here, ordered.Count - here, SearchSource.Walk, false, "Searching…", 1) { EverythingFailed = everythingFailed };
        if (typeof(SearchSnapshot).GetProperty("Folder") is { CanWrite: true } said)
        {
            said.SetValue(snapshot, folder);
        }

        return snapshot;
    }

    /// <summary>
    /// J029: without Everything, going to another folder with the panel open
    /// searched again from that folder - the walk of every drive was thrown
    /// away with all it had found and started again, at every stop while
    /// browsing, so it never got to the end.  What it has found is ordered
    /// for the folder gone to instead, and the walk goes on.  The search is
    /// made for a name nothing has, so one made again shows nothing.
    /// </summary>
    private static async Task SearchWalkFolderMoveAsync()
    {
        Section("search: going to another folder while walking orders what was found, and walks on (J029)");
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        using var icons = new ShellIconService();
        using var search = new SearchViewModel(icons, _ => Task.CompletedTask, (_, _) => { });
        var run = typeof(SearchViewModel).GetField("_run", flags)!;
        const string a = @"C:\UxMoveA", b = @"C:\UxMoveB";
        var current = a;
        search.HereFolder = () => current;
        SearchOpenForTest(search, a);
        var text = "uxmove" + Guid.NewGuid().ToString("N")[..12];
        typeof(SearchViewModel).GetField("_text", flags)!.SetValue(search, text);
        var query = SearchQuery.Parse(text);
        SearchHit InA() => new(text + "-a.txt", a, false, 1, null, null);
        SearchHit InB() => new(text + "-b.txt", b, false, 1, null, null);

        SearchApplyForTest(search, SearchWalkedFrom(a, query, true, InA(), InB()), query);
        Check("walking from A, A's result comes first", search.Results.Count == 2 && search.Results[0].Name == text + "-a.txt");

        current = b;
        search.NoteFolder(b);
        await SearchUntil(() => search.Rows.Count > 0 && search.Rows[0] is SearchHeaderRow { Title: "In UxMoveB" }, 3_000);
        await SearchUntil(() => false, 300);
        Check($"once the move to B settles, the results found are ordered for B ({string.Join(", ", search.Results.Select(result => result.Name[^5..]))})",
            search.Rows.Count > 0 && search.Rows[0] is SearchHeaderRow { Title: "In UxMoveB" }
            && search.Results.Count == 2 && search.Results[0].Name == text + "-b.txt" && search.Results[0].Place == SearchPlace.Here);
        Check("and no search was made again: the walk goes on", run.GetValue(search) is null && search.IsBusy);

        // A report the walk sent before it heard of the move.
        SearchApplyForTest(search, SearchWalkedFrom(a, query, true, InA(), InB(), new(text + "-c.txt", a, false, 1, null, null)), query);
        Check("a report ordered from A, come after the move, is ordered for B",
            search.Results.Count == 3 && search.Results[0].Name == text + "-b.txt" && search.Rows[0] is SearchHeaderRow { Title: "In UxMoveB", CountText: "1" });

        // One standing in for an Everything still starting goes to it once it
        // is ready, as a search made again did - and is not made again before.
        var waiting = (DispatcherTimer)typeof(SearchViewModel).GetField("_waitingForEverything", flags)!.GetValue(search)!;
        SearchApplyForTest(search, SearchWalkedFrom(b, query, false, InA(), InB()), query);
        current = a;
        search.NoteFolder(a);
        await SearchUntil(() => search.Rows.Count > 0 && search.Rows[0] is SearchHeaderRow { Title: "In UxMoveA" }, 3_000);
        Check("a walk standing in for an Everything still starting waits for it once the move settles",
            waiting.IsEnabled && run.GetValue(search) is null && search.Results.Count == 2 && search.Results[0].Name == text + "-a.txt");
        waiting.Stop();

        // Stopped at its limit, a walk holds only part of what there is, found
        // from the folder before: there it is searched again, as before.
        var many = Enumerable.Range(0, SearchEngine.WalkLimit).Select(index => new SearchHit($"{text}-{index}.txt", a, false, 1, null, null)).ToArray();
        SearchApplyForTest(search, SearchWalkedFrom(a, query, true, many), query);
        current = b;
        search.NoteFolder(b);
        var again = await SearchUntil(() => run.GetValue(search) is not null, 3_000);
        Check("a walk stopped at its limit is made again from the folder gone to", again);
        search.Close();
    }

    /// <summary>
    /// J029, the walk's side: told the folder the results are ordered from
    /// now, it orders what it has found and what it finds for that one.
    /// </summary>
    private static async Task SearchWalkOrderedFromAsync()
    {
        Section("search: a walk orders its results from the folder gone to while it runs (J029)");
        var method = typeof(SearchEngine).GetMethod("WalkAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        if (method is null || !method.GetParameters().Any(parameter => parameter.Name == "orderFrom"))
        {
            Check("a walk can be told the folder to order from", false);
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerSearchMove", Guid.NewGuid().ToString("N"));
        var a = Path.Combine(root, "A");
        var b = Path.Combine(root, "B");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        var text = "uxorder" + Guid.NewGuid().ToString("N")[..12];
        File.WriteAllText(Path.Combine(a, text + "-a.txt"), "x");
        File.WriteAllText(Path.Combine(b, text + "-b.txt"), "x");
        try
        {
            var reports = new ConcurrentQueue<SearchSnapshot>();
            Func<string?> orderFrom = () => b;
            var arguments = method.GetParameters().Select(parameter => parameter.Name switch
            {
                "query" => SearchQuery.Parse(text),
                "here" => a,
                "drives" => new[] { root },
                "reason" => "checked",
                "everythingFailed" => false,
                "publish" => (Action<SearchSnapshot>)reports.Enqueue,
                "orderFrom" => orderFrom,
                "cancellationToken" => CancellationToken.None,
                _ => (object?)null,
            }).ToArray();
            await (Task)method.Invoke(new SearchEngine(), arguments)!;
            var last = reports.LastOrDefault();
            var folder = last is null ? null : typeof(SearchSnapshot).GetProperty("Folder")?.GetValue(last) as string;
            Check($"the walk's last report is ordered from B ({folder}): B's result first, in it, A's elsewhere",
                last is { IsFinal: true, Hits.Count: 2, HereTotal: 1 } && string.Equals(folder, b, StringComparison.OrdinalIgnoreCase)
                && last.Hits[0].Name == text + "-b.txt" && last.Hits[0].Place == SearchPlace.Here && last.Hits[1].Place == SearchPlace.Elsewhere);
        }
        finally
        {
            TryDelete(root);
        }
    }
}
