using System.Collections.Frozen;

namespace UltraExplorer.Services.Search;

/// <summary>
/// An immutable visibility mask over the ordinary map. Paths identify the
/// existing cells; neither the listings nor their order or placement change.
/// </summary>
internal sealed class SearchMapProjection
{
    private readonly FrozenDictionary<string, FrozenSet<string>> _files;
    private readonly FrozenSet<string> _folders;
    private readonly FrozenSet<string> _directFolders;
    private readonly FrozenSet<string> _unknownAncestors;
    private readonly string[] _unknownBranches;
    private readonly SearchQuery _query;

    private SearchMapProjection(SearchQuery query, Dictionary<string, HashSet<string>> files,
        HashSet<string> folders, HashSet<string> directFolders, string[] unknownBranches, bool isComplete)
    {
        _query = query;
        _files = files.ToFrozenDictionary(pair => pair.Key,
            pair => pair.Value.ToFrozenSet(StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase);
        _folders = folders.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _directFolders = directFolders.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _unknownBranches = unknownBranches;
        var unknownAncestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var branch in unknownBranches) AddAncestors(branch, unknownAncestors);
        _unknownAncestors = unknownAncestors.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        FileCount = files.Values.Sum(names => (long)names.Count);
        FolderCount = directFolders.Count;
        IsComplete = isComplete && unknownBranches.Length == 0;
    }

    public string QueryText => _query.Text;
    public long FileCount { get; }
    public long FolderCount { get; }
    public bool IsComplete { get; }
    public IReadOnlyList<string> UnknownBranches => _unknownBranches;

    internal static SearchMapProjection Create(string query, IEnumerable<string> matchingFiles,
        IEnumerable<string>? matchingFolders = null, IEnumerable<string>? unknownBranches = null, bool isComplete = true)
    {
        var parsed = SearchQuery.Parse(query);
        var files = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in matchingFiles)
        {
            var parent = Path.GetDirectoryName(path);
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name)) continue;
            parent = Normalize(parent);
            if (!files.TryGetValue(parent, out var names)) files.Add(parent, names = new(StringComparer.Ordinal));
            if (names.Add(name)) AddAncestors(parent, folders);
        }
        if (!parsed.MapFilesOnly && matchingFolders is not null)
        {
            foreach (var path in matchingFolders)
            {
                var normalized = Normalize(path);
                if (normalized.Length > 0 && direct.Add(normalized)) AddAncestors(normalized, folders);
            }
        }
        var unknown = (unknownBranches ?? []).Select(Normalize).Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(parsed, files, folders, direct, unknown, isComplete);
    }

    public bool ContainsFolder(string path)
    {
        if (string.IsNullOrEmpty(path)) return _folders.Count > 0 || _unknownBranches.Length > 0;
        var normalized = Normalize(path);
        return _folders.Contains(normalized) || _unknownAncestors.Contains(normalized) || IsUnknown(normalized);
    }

    public bool IsFolderMatch(string path) => !string.IsNullOrEmpty(path) && _directFolders.Contains(Normalize(path));

    public bool ContainsFile(string parent, string name)
    {
        var normalized = Normalize(parent);
        if (_files.TryGetValue(normalized, out var names) && names.Contains(name)) return true;
        if (!IsUnknown(normalized)) return false;
        return !_query.SupportsLocalMatching || _query.Matches(name, normalized, isFolder: false);
    }

    internal Func<string, bool> FileMatcher(string parent)
    {
        var normalized = Normalize(parent);
        _files.TryGetValue(normalized, out var names);
        if (!IsUnknown(normalized)) return names is null ? static _ => false : names.Contains;
        return name => names?.Contains(name) == true
            || !_query.SupportsLocalMatching || _query.Matches(name, normalized, isFolder: false);
    }

    private bool IsUnknown(string path)
    {
        foreach (var branch in _unknownBranches)
            if (Within(path, branch)) return true;
        return false;
    }

    internal static bool Within(string path, string root)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        return path.Length > root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && (root.EndsWith('\\') || path[root.Length] == '\\');
    }

    internal static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var normalized = path.Contains('/') ? path.Replace('/', '\\') : path;
        var rootLength = Path.GetPathRoot(normalized)?.Length ?? 0;
        var end = normalized.Length;
        while (end > rootLength && normalized[end - 1] == '\\') end--;
        return end == normalized.Length ? normalized : normalized[..end];
    }

    private static void AddAncestors(string path, HashSet<string> folders)
    {
        for (var current = Normalize(path); current.Length > 0;)
        {
            if (!folders.Add(current)) break;
            current = Normalize(Path.GetDirectoryName(current) ?? string.Empty);
        }
    }
}
