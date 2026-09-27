namespace UltraExplorer.Services.Search;

/// <summary>
/// The order search results are shown in.  First what is right in the
/// folder the search was made from, then what is inside its folders, then
/// everything else on the drives.  Within each, places nobody searches on
/// purpose - caches, package stores, the Windows component store - after
/// the ones people keep their files in, however well their names match:
/// a thousand README.md files in node_modules are not what "readme" is
/// after.  Then the names that are what was typed before the names that
/// start with it, those before a word inside them, and those before the
/// rest; elsewhere, folders near the one searched from before far ones;
/// folders before files; shallow before deep; and by name.
/// </summary>
internal static class SearchRanking
{
    /// <summary>
    /// Parts of a path that mark a place whose results sink below the rest:
    /// not hidden, since sometimes that is exactly what is looked for.
    /// </summary>
    private static readonly string[] NoisyParts =
    [
        @"\$Recycle.Bin\",
        @"\System Volume Information\",
        @"\Windows\WinSxS\",
        @"\Windows\servicing\",
        @"\Windows\Installer\",
        @"\Windows\SoftwareDistribution\",
        @"\Windows\assembly\",
        @"\Windows\Microsoft.NET\",
        @"\node_modules\",
        @"\.git\",
        @"\__pycache__\",
        @"\site-packages\",
        @"\.nuget\packages\",
        @"\AppData\",
        @"\obj\",
        @"\.cache\",
        @"\.vs\",
    ];

    /// <summary>Works out every hit's place and measures, and sorts them.</summary>
    public static void Rank(List<SearchHit> hits, SearchQuery query, string? here)
    {
        var folder = Normalize(here);
        foreach (var hit in hits)
        {
            Measure(hit, query, folder);
        }

        hits.Sort(Compare);
    }

    internal static void Measure(SearchHit hit, SearchQuery query, string? folder)
    {
        hit.Place = PlaceOf(hit, folder);
        hit.Quality = query.Quality(hit.Name);
        hit.Noise = NoiseOf(hit.FullPath);
        hit.Proximity = folder is null ? 0 : CommonDepth(hit.FullPath, folder);
        hit.Depth = hit.FullPath.AsSpan().TrimEnd('\\').Count('\\');
    }

    internal static int Compare(SearchHit a, SearchHit b)
    {
        var order = a.Place.CompareTo(b.Place);
        if (order != 0)
        {
            return order;
        }

        order = a.Noise.CompareTo(b.Noise);
        if (order != 0)
        {
            return order;
        }

        order = a.Quality.CompareTo(b.Quality);
        if (order != 0)
        {
            return order;
        }

        if (a.Place == SearchPlace.Elsewhere)
        {
            order = b.Proximity.CompareTo(a.Proximity);
            if (order != 0)
            {
                return order;
            }
        }

        order = b.IsFolder.CompareTo(a.IsFolder);
        if (order != 0)
        {
            return order;
        }

        order = a.Depth.CompareTo(b.Depth);
        if (order != 0)
        {
            return order;
        }

        order = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return order != 0 ? order : string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The folder without a separator at its end, except a drive's (C:\).</summary>
    internal static string? Normalize(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        var trimmed = folder.TrimEnd('\\', '/');
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + "\\" : trimmed;
    }

    internal static SearchPlace PlaceOf(SearchHit hit, string? folder)
    {
        if (folder is null)
        {
            return SearchPlace.Elsewhere;
        }

        var directory = Normalize(hit.Directory);
        if (directory is not null && directory.Equals(folder, StringComparison.OrdinalIgnoreCase))
        {
            return SearchPlace.Here;
        }

        return IsUnder(hit.FullPath, folder) ? SearchPlace.Below : SearchPlace.Elsewhere;
    }

    /// <summary>Whether <paramref name="path"/> is inside <paramref name="folder"/> (not the folder itself).</summary>
    internal static bool IsUnder(string path, string folder)
    {
        if (path.Length <= folder.Length || !path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return folder.EndsWith('\\') || path[folder.Length] == '\\';
    }

    internal static int NoiseOf(string fullPath)
    {
        foreach (var part in NoisyParts)
        {
            if (fullPath.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }
        }

        return 0;
    }

    /// <summary>How many parts two paths share from the start: C:\A\B and C:\A\C share two (C: and A).</summary>
    internal static int CommonDepth(string a, string b)
    {
        var shared = 0;
        var index = 0;
        while (true)
        {
            var endA = a.IndexOf('\\', index);
            var endB = b.IndexOf('\\', index);
            var partA = endA < 0 ? a.AsSpan(index) : a.AsSpan(index, endA - index);
            var partB = endB < 0 ? b.AsSpan(index) : b.AsSpan(index, endB - index);
            if (partA.Length == 0 || !partA.Equals(partB, StringComparison.OrdinalIgnoreCase))
            {
                return shared;
            }

            shared++;
            if (endA < 0 || endB < 0 || endA != endB)
            {
                return shared;
            }

            index = endA + 1;
        }
    }
}
