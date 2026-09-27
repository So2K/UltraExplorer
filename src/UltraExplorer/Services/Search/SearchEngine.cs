using System.Diagnostics;
using UltraExplorer.Services.Watch;

namespace UltraExplorer.Services.Search;

/// <summary>Which engine answered a search.</summary>
public enum SearchSource
{
    /// <summary>Everything's index: every drive, at once.</summary>
    Everything,

    /// <summary>The folders read one by one, because Everything is not there or not ready.</summary>
    Walk,
}

/// <summary>
/// What a search has found so far, in the order it is shown: the results,
/// how many match in and under the folder searched from and how many
/// elsewhere, which engine found them and whether it is done.
/// </summary>
internal sealed record SearchSnapshot(
    IReadOnlyList<SearchHit> Hits,
    long HereTotal,
    long ElsewhereTotal,
    SearchSource Source,
    bool IsFinal,
    string Status,
    double ElapsedMilliseconds);

/// <summary>
/// Runs a search over every drive, the folder it is made from first
/// (<see cref="SearchRanking"/>).  Everything answers it when it is running
/// with its index loaded: one query for everything, and when that holds
/// only the first of more matches than it can carry, two more - what is
/// under the folder, and the names that start with the word typed - so
/// neither is lost among the rest.  Otherwise the drives are walked (<see cref="FolderWalk"/>),
/// the folder first, and what is found is shown as it is found; an
/// installed Everything that is not running is started on the way, and the
/// next search uses it.
///
/// <para>A letter made with <c>subst</c> shows the same files as the folder
/// it stands for, and Everything indexes both: the spelling the search is
/// made from is kept and the other left out, so nothing is listed twice.</para>
/// </summary>
internal sealed class SearchEngine
{
    /// <summary>At most this many results from under the folder searched from…</summary>
    public const int HereLimit = 400;

    /// <summary>…this many names that start with the word…</summary>
    public const int PrefixLimit = 200;

    /// <summary>…and this many from everywhere.</summary>
    public const int EverywhereLimit = 800;

    /// <summary>The walk stops after this many results.</summary>
    public const int WalkLimit = 1_500;

    private const int WalkReportMilliseconds = 120;

    private readonly EverythingClient _everything;
    private readonly Func<char, VolumeResolver.Letter> _letters;
    private (long Stamp, IReadOnlyList<(string Letter, string Target)> Substs) _substs = (long.MinValue, []);

    public SearchEngine()
        : this(EverythingClient.Shared, VolumeResolver.FromSystem)
    {
    }

    internal SearchEngine(EverythingClient everything, Func<char, VolumeResolver.Letter> letters)
    {
        _everything = everything;
        _letters = letters;
    }

    /// <summary>Whether a search now would be Everything's.</summary>
    public static bool EverythingReady => EverythingClient.IsDatabaseLoaded;

    /// <summary>Whether Everything is installed on this computer, running or not.</summary>
    public static bool EverythingInstalled => EverythingClient.IsRunning || EverythingClient.InstalledProgram is not null;

    /// <summary>
    /// Searches for <paramref name="query"/>, the folder <paramref name="here"/>
    /// first, telling <paramref name="publish"/> - on whatever thread - what it
    /// has found: once for Everything, as it goes for a walk, the last time
    /// with <see cref="SearchSnapshot.IsFinal"/>.
    /// </summary>
    public async Task RunAsync(SearchQuery query, string? here, Action<SearchSnapshot> publish, CancellationToken cancellationToken)
    {
        if (query.IsEmpty)
        {
            publish(new SearchSnapshot([], 0, 0, SearchSource.Everything, true, string.Empty, 0));
            return;
        }

        here = SearchRanking.Normalize(here);
        if (EverythingClient.IsDatabaseLoaded)
        {
            var answered = await AskEverythingAsync(query, here, publish, cancellationToken).ConfigureAwait(false);
            if (answered || cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        string reason;
        if (EverythingClient.IsRunning)
        {
            reason = "Everything is still reading the drives - walking folders meanwhile.";
        }
        else if (_everything.TryStartInstalled())
        {
            reason = "Starting Everything - walking folders meanwhile.";
        }
        else if (EverythingClient.InstalledProgram is not null)
        {
            reason = "Everything is installed but not running - walking folders.";
        }
        else
        {
            reason = "Walking folders. Install Everything (voidtools.com) for instant results.";
        }

        await WalkAsync(query, here, reason, publish, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> AskEverythingAsync(SearchQuery query, string? here, Action<SearchSnapshot> publish, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var substs = Substs();
        var excluded = ExcludedFolders(here, substs);

        // Everywhere first: when that is every match there is - most
        // searches past the first letters - the other two would only find
        // again what it already holds, and are not asked.  When it is not,
        // the newest are the part of it worth having; the names that start
        // with the word are asked for in name order, where the word itself
        // comes first.
        EverythingPage? under, starting, everywhere;
        var none = new EverythingPage(0, []);
        try
        {
            everywhere = await _everything.QueryAsync(query.Excluding(excluded), EverywhereLimit, cancellationToken, EverythingClient.SortNewestFirst).ConfigureAwait(false);
            if (everywhere is null)
            {
                return false;
            }

            var complete = everywhere.Items.Count >= everywhere.Total;
            under = here is null || complete
                ? none
                : await _everything.QueryAsync(query.Under(here), HereLimit, cancellationToken, EverythingClient.SortNewestFirst).ConfigureAwait(false);
            starting = complete || query.PrefixExcluding(excluded) is not { } prefix
                ? none
                : await _everything.QueryAsync(prefix, PrefixLimit, cancellationToken).ConfigureAwait(false);
            if (under is null || starting is null)
            {
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            return true;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hits = new List<SearchHit>(under.Items.Count + starting.Items.Count + everywhere.Items.Count);
        foreach (var page in new[] { under, starting, everywhere })
        {
            foreach (var item in page.Items)
            {
                var hit = new SearchHit(item.Name, item.Directory, item.IsFolder, item.Size, item.Modified, item.Highlighted);
                if (seen.Add(hit.FullPath))
                {
                    hits.Add(hit);
                }
            }
        }

        SearchRanking.Rank(hits, query, here);
        var total = everywhere.Total;
        var hereTotal = here is null ? 0
            : ReferenceEquals(under, none) ? hits.Count(hit => hit.Place != SearchPlace.Elsewhere)
            : under.Total;
        var elapsed = clock.Elapsed.TotalMilliseconds;
        var version = EverythingClient.Version is { } known ? $"Everything {known}" : "Everything";
        publish(new SearchSnapshot(
            hits,
            hereTotal,
            Math.Max(0, total - hereTotal),
            SearchSource.Everything,
            true,
            $"{total:N0} found on all drives · {elapsed:0} ms · {version}",
            elapsed));
        return true;
    }

    private async Task WalkAsync(SearchQuery query, string? here, string reason, Action<SearchSnapshot> publish, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var substs = Substs();
        var roots = DriveInfo.GetDrives()
            .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
            .Select(drive => drive.RootDirectory.FullName)
            .Where(root => !substs.Any(subst => root.StartsWith(subst.Letter, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        // What the folder searched from stands for, when it is on a subst
        // letter: its files are walked under that spelling, not again under
        // the other.
        var skip = new List<string>();
        if (here is not null && SubstTargetOf(here, substs) is { } target)
        {
            skip.Add(target);
        }

        var walk = new FolderWalk(query, WalkLimit);
        var running = walk.RunAsync(here, roots, skip, cancellationToken);
        var hits = new List<SearchHit>();
        var folder = SearchRanking.Normalize(here);
        while (true)
        {
            var finished = await Task.WhenAny(running, Task.Delay(WalkReportMilliseconds, CancellationToken.None)).ConfigureAwait(false) == running;
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var fresh = walk.Drain();
            if (fresh.Count > 0 || finished)
            {
                foreach (var hit in fresh)
                {
                    SearchRanking.Measure(hit, query, folder);
                }

                hits.AddRange(fresh);
                hits.Sort(SearchRanking.Compare);
                var hereTotal = hits.Count(hit => hit.Place != SearchPlace.Elsewhere);
                var status = finished
                    ? $"{hits.Count:N0} found{(walk.IsFull ? " (stopped at the limit)" : string.Empty)} · {walk.FoldersRead:N0} folders in {clock.Elapsed.TotalSeconds:0.0} s · {reason}"
                    : $"Searching… {walk.FoldersRead:N0} folders read · {reason}";
                publish(new SearchSnapshot([.. hits], hereTotal, hits.Count - hereTotal, SearchSource.Walk, finished, status, clock.Elapsed.TotalMilliseconds));
            }

            if (finished)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The folders Everything's answers leave out: the subst letters, when the
    /// search is made from a real path, or the folder a subst letter stands
    /// for, when it is made from under that letter.
    /// </summary>
    internal static IReadOnlyCollection<string> ExcludedFolders(string? here, IReadOnlyList<(string Letter, string Target)> substs)
    {
        if (substs.Count == 0)
        {
            return [];
        }

        var excluded = new List<string>();
        foreach (var (letter, target) in substs)
        {
            var fromHere = here is not null && here.StartsWith(letter, StringComparison.OrdinalIgnoreCase);
            excluded.Add(fromHere ? target : letter);
        }

        return excluded;
    }

    private static string? SubstTargetOf(string path, IReadOnlyList<(string Letter, string Target)> substs)
    {
        foreach (var (letter, target) in substs)
        {
            if (path.StartsWith(letter, StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }
        }

        return null;
    }

    /// <summary>
    /// The subst letters that stand for a folder on a local drive, as
    /// (<c>L:\</c>, <c>E:\AiControl</c>); asked again at most every thirty
    /// seconds.
    /// </summary>
    private IReadOnlyList<(string Letter, string Target)> Substs()
    {
        var now = Stopwatch.GetTimestamp();
        if (_substs.Stamp != long.MinValue && Stopwatch.GetElapsedTime(_substs.Stamp, now).TotalSeconds < 30)
        {
            return _substs.Substs;
        }

        var found = new List<(string, string)>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            var letter = drive.Name[0];
            if (!char.IsAsciiLetter(letter))
            {
                continue;
            }

            try
            {
                if (_letters(letter).SubstTarget is { Length: >= 3 } target && target[1] == ':')
                {
                    found.Add(($"{char.ToUpperInvariant(letter)}:\\", target));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }

        _substs = (now, found);
        return found;
    }
}
