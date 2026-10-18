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
        HashSet<string> folders, HashSet<string> directFolders, string[] unknownBranches, bool isComplete,
        CancellationToken cancellationToken)
    {
        _query = query;
        _files = files.ToFrozenDictionary(pair => pair.Key,
            pair =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return pair.Value.ToFrozenSet(StringComparer.Ordinal);
            }, StringComparer.OrdinalIgnoreCase);
        cancellationToken.ThrowIfCancellationRequested();
        _folders = folders.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _directFolders = directFolders.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _unknownBranches = unknownBranches;
        var unknownAncestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var branch in unknownBranches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddAncestors(branch, unknownAncestors);
        }
        _unknownAncestors = unknownAncestors.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        cancellationToken.ThrowIfCancellationRequested();
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
        var result = new Accumulator(parsed);
        foreach (var path in matchingFiles)
            result.AddFile(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileName(path));
        if (!parsed.MapFilesOnly)
            foreach (var path in matchingFolders ?? []) result.AddFolder(path);
        foreach (var path in unknownBranches ?? []) result.AddUnknown(path);
        return result.Build(isComplete);
    }

    /// <summary>Consumes each page directly, without retaining another full-path result list.</summary>
    internal sealed class Accumulator(SearchQuery query, CancellationToken cancellationToken = default)
    {
        private readonly Dictionary<string, HashSet<string>> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _direct = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unknown = new(StringComparer.OrdinalIgnoreCase);
        private bool _built;

        internal void AddFile(string parent, string name)
        {
            CheckWritable();
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name)) return;
            parent = Normalize(parent);
            if (!_files.TryGetValue(parent, out var names)) _files.Add(parent, names = new(StringComparer.Ordinal));
            if (names.Add(name)) AddAncestors(parent, _folders);
        }

        internal void AddFolder(string path)
        {
            CheckWritable();
            if (query.MapFilesOnly) return;
            var normalized = Normalize(path);
            if (normalized.Length > 0 && _direct.Add(normalized)) AddAncestors(normalized, _folders);
        }

        internal void AddUnknown(string path)
        {
            CheckWritable();
            var normalized = Normalize(path);
            if (normalized.Length > 0) _unknown.Add(normalized);
        }

        internal SearchMapProjection Build(bool isComplete = true)
        {
            CheckWritable();
            var result = new SearchMapProjection(query, _files, _folders, _direct, _unknown.ToArray(), isComplete, cancellationToken);
            _built = true;
            // Frozen sets own their membership arrays. Release the mutable
            // tables even when the completed async builder remains reachable.
            _files.Clear();
            _folders.Clear();
            _direct.Clear();
            _unknown.Clear();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        private void CheckWritable()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_built) throw new InvalidOperationException("The map projection has already been completed.");
        }
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
