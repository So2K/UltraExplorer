using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security;

namespace UltraExplorer.Services.Search;

/// <summary>A complete set of index matches, or the readable part of a filesystem scan.</summary>
internal sealed record SearchMapScanResult(
    IReadOnlyList<string> MatchingFiles,
    IReadOnlyList<string> MatchingFolders,
    IReadOnlyList<string> UnknownBranches);

internal sealed record SearchMapProjectionResult(
    SearchMapProjection? Projection,
    bool IsComplete,
    bool Unsupported,
    SearchSource Source,
    string Status)
{
    public long FileCount => Projection?.FileCount ?? 0;
    public long FolderCount => Projection?.FolderCount ?? 0;
}

/// <summary>
/// Collects map membership independently of the result panel's display limit.
/// The index is paged; a fallback enumerates only the requested roots and
/// retains unreadable and skipped link branches as unknown, rather than empty.
/// </summary>
internal sealed class SearchMapProjectionBuilder
{
    internal const int PageSize = 4096;
    private static readonly SemaphoreSlim ScanLane = new(1, 1);
    private static readonly EnumerationOptions ScanOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024
    };

    private readonly Func<string, int, uint, CancellationToken, uint, Task<EverythingPage?>> _queryPage;
    private readonly Func<bool> _indexReady;
    private readonly Func<SearchQuery, IReadOnlyList<string>, CancellationToken, Task<SearchMapScanResult>> _scan;

    internal SearchMapProjectionBuilder(
        Func<string, int, uint, CancellationToken, uint, Task<EverythingPage?>>? queryPage = null,
        Func<bool>? indexReady = null,
        Func<SearchQuery, IReadOnlyList<string>, CancellationToken, Task<SearchMapScanResult>>? scan = null)
    {
        _queryPage = queryPage ?? EverythingClient.Shared.QueryMapPageAsync;
        _indexReady = indexReady ?? (() => EverythingClient.IsDatabaseLoaded);
        _scan = scan ?? ScanAsync;
    }

    internal async Task<SearchMapProjectionResult> RunAsync(string text, IReadOnlyList<string> scopeRoots, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scopeRoots);
        ct.ThrowIfCancellationRequested();
        var query = SearchQuery.Parse(text);
        if (query.IsEmpty)
            return new(null, true, false, SearchSource.Everything, string.Empty);
        var roots = ScopeRoots(scopeRoots);
        if (roots.Length == 0)
            return new(null, false, false, SearchSource.Walk, "Choose a filesystem folder to search on the map.");

        var indexFailed = false;
        if (_indexReady())
        {
            // A ready local index does not prove coverage of a network share,
            // WSL or a non-indexed filesystem. Probe only real ready drives;
            // absent fixture letters remain usable by the injected index seam.
            var preferWalk = query.SupportsLocalMatching
                && await Task.Run(() => roots.Any(PreferFilesystemScope), ct).ConfigureAwait(false);
            var indexed = preferWalk ? null : await ReadIndexAsync(query, roots, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (indexed is not null)
            {
                return new(indexed, indexed.IsComplete, false, SearchSource.Everything,
                    $"{MatchStatus(indexed)} · Everything index · Only indexed locations are covered.");
            }
            indexFailed = !preferWalk;
        }

        if (!query.SupportsLocalMatching)
            return new(null, false, true, SearchSource.Walk,
                "This search requires Everything to return a complete result set. The full map remains visible.");

        var scanned = await _scan(query, roots, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var walked = Project(query, roots, scanned, ct);
        var incomplete = walked.IsComplete ? string.Empty : " · Some folders could not be searched";
        var fallback = indexFailed ? " · Everything did not return a complete result set" : string.Empty;
        return new(walked, walked.IsComplete, false, SearchSource.Walk,
            $"{MatchStatus(walked)} · Folder scan{incomplete}{fallback}");
    }

    private static SearchMapProjection Project(SearchQuery query, string[] roots, SearchMapScanResult result, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Scope is checked again for injected scanners and index replies, so a
        // query containing OR can never admit another drive into this map.
        var projection = new SearchMapProjection.Accumulator(query, ct);
        foreach (var path in result.MatchingFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (roots.Any(root => IsInside(path, root)))
                projection.AddFile(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileName(path));
        }
        if (!query.MapFilesOnly)
            foreach (var path in result.MatchingFolders)
            {
                ct.ThrowIfCancellationRequested();
                if (roots.Any(root => IsInside(path, root))) projection.AddFolder(path);
            }
        AddScopeFolders(projection, query, roots, ct);
        foreach (var path in result.UnknownBranches)
        {
            ct.ThrowIfCancellationRequested();
            if (roots.Any(root => IsInside(path, root))) projection.AddUnknown(path);
        }
        return projection.Build();
    }

    private static void AddScopeFolders(SearchMapProjection.Accumulator projection, SearchQuery query, string[] roots, CancellationToken ct)
    {
        if (!query.MapFilesOnly && query.SupportsLocalMatching)
        {
            // An empty scope folder is a real matching directory too. Neither
            // a scoped index query nor enumerating its children includes it.
            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested();
                var named = root.TrimEnd('\\');
                var name = Path.GetFileName(named);
                if (name.Length == 0) name = named;
                if (query.Matches(name, Path.GetDirectoryName(named) ?? string.Empty, isFolder: true)) projection.AddFolder(root);
            }
        }
    }

    private static string MatchStatus(SearchMapProjection projection) => projection.FolderCount > 0
        ? $"{projection.FileCount:N0} matching files · {projection.FolderCount:N0} matching folders"
        : $"{projection.FileCount:N0} matching files";

    private static bool PreferFilesystemScope(string root)
    {
        if (root.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try
        {
            var driveRoot = Path.GetPathRoot(root);
            if (string.IsNullOrEmpty(driveRoot)) return false;
            var drive = new DriveInfo(driveRoot);
            if (drive.DriveType == DriveType.Network) return true;
            if (!drive.IsReady) return false;
            var format = drive.DriveFormat;
            return !format.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
                && !format.Equals("ReFS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false; // Unknown coverage is disclosed in the indexed status.
        }
    }

    private async Task<SearchMapProjection?> ReadIndexAsync(SearchQuery query, string[] roots, CancellationToken ct)
    {
        var projection = new SearchMapProjection.Accumulator(query, ct);
        try
        {
            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested();
                var search = query.Under(root);
                uint offset = 0;
                long? total = null;
                // Ordinal parent IDs plus ordinal leaf names preserve the
                // former full-path duplicate proof without retaining every
                // concatenated path. Keep it separate from the projection's
                // case-insensitive parent grouping. The separator bit also
                // distinguishes a root-relative record from a bare name.
                var directories = new Dictionary<(string Parent, bool HasSeparator), int>();
                var records = new HashSet<(int Directory, string Name, bool IsFolder)>();
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var page = await _queryPage(search, PageSize, offset, ct, EverythingClient.SortNameAscending).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (page is null || !page.IsComplete || page.Offset != offset
                        || page.Total < 0 || page.Total > uint.MaxValue || page.Items.Count > PageSize
                        || total is not null && total.Value != page.Total)
                        return null;
                    total ??= page.Total;
                    var remaining = total.Value - offset;
                    if (remaining < 0 || page.Items.Count > remaining || page.Items.Count == 0 && remaining != 0)
                        return null;
                    foreach (var item in page.Items)
                    {
                        ct.ThrowIfCancellationRequested();
                        var path = LogicalPath(SearchHit.Join(item.Directory, item.Name));
                        var parent = Path.GetDirectoryName(path) ?? string.Empty;
                        var separator = path.LastIndexOf('\\');
                        var exactParent = path.AsSpan(0, Math.Max(0, separator));
                        var recordParent = exactParent.SequenceEqual(parent) ? parent : exactParent.ToString();
                        var directoryKey = (recordParent, separator >= 0);
                        var directoryId = GetDirectoryId(directories, directoryKey);
                        var exactName = path.AsSpan(separator + 1);
                        var recordName = exactName.SequenceEqual(item.Name) ? item.Name : exactName.ToString();
                        // A duplicate across pages can hide a missing result
                        // even if an index change kept the total unchanged.
                        if (!records.Add((directoryId, recordName, item.IsFolder))) return null;
                        if (!IsInside(path, root)) continue;
                        if (item.IsFolder)
                        {
                            projection.AddFolder(path);
                        }
                        else
                        {
                            var name = Path.GetFileName(path.AsSpan());
                            projection.AddFile(parent, name.SequenceEqual(recordName) ? recordName : name.ToString());
                        }
                    }
                    var next = (long)offset + page.Items.Count;
                    if (next == total.Value) break;
                    if (next <= offset || next > uint.MaxValue) return null;
                    offset = (uint)next;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException)
        {
            return null;
        }
        AddScopeFolders(projection, query, roots, ct);
        return projection.Build();
    }

    private static int GetDirectoryId(Dictionary<(string Parent, bool HasSeparator), int> directories,
        (string Parent, bool HasSeparator) directory)
    {
        // Obtain or insert with one lookup. This local dictionary only grows,
        // so its new entry's ID is Count - 1. Existence is explicit: zero is a
        // valid ID. Copy it before returning; no dictionary reference crosses
        // an await or survives another mutation/resize.
        ref var id = ref CollectionsMarshal.GetValueRefOrAddDefault(directories, directory, out var exists);
        if (!exists) id = directories.Count - 1;
        return id;
    }

    private static async Task<SearchMapScanResult> ScanAsync(SearchQuery query, IReadOnlyList<string> roots, CancellationToken ct)
    {
        // One filesystem worker across builders. Canceled queued requests leave
        // this lane before starting, so rapid edits do not multiply disk scans.
        await ScanLane.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var thread = Thread.CurrentThread;
                var priority = thread.Priority;
                try
                {
                    thread.Priority = ThreadPriority.BelowNormal;
                    return Scan(query, roots, ct);
                }
                finally { thread.Priority = priority; }
            }, ct).ConfigureAwait(false);
        }
        finally { ScanLane.Release(); }
    }

    private static SearchMapScanResult Scan(SearchQuery query, IReadOnlyList<string> roots, CancellationToken ct)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(roots.Reverse());
        var rootSet = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        while (pending.TryPop(out var folder))
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(folder)) continue;
            try
            {
                if (rootSet.Contains(folder))
                {
                    var info = new DirectoryInfo(ExtendedLength(folder));
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null)
                    {
                        unknown.Add(folder);
                        continue;
                    }
                }
                var entries = new FileSystemEnumerable<ScanEntry>(ExtendedLength(folder),
                    static (ref FileSystemEntry entry) => new ScanEntry(entry.FileName.ToString(), entry.IsDirectory,
                        entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) != 0 && IsLink(ref entry)), ScanOptions)
                {
                    ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        return entry.IsDirectory || query.Matches(entry.FileName, folder, isFolder: false);
                    }
                };
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var path = SearchHit.Join(folder, entry.Name);
                    if (!entry.IsDirectory) { files.Add(path); continue; }
                    if (!query.MapFilesOnly && query.Matches(entry.Name, folder, isFolder: true)) folders.Add(path);
                    if (entry.IsLink) unknown.Add(path);
                    else pending.Push(path);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException
                or ArgumentException or NotSupportedException)
            {
                unknown.Add(folder);
            }
        }
        ct.ThrowIfCancellationRequested();
        return new(files.ToArray(), folders.ToArray(), unknown.ToArray());
    }

    private static bool IsLink(ref FileSystemEntry entry)
    {
        try { return entry.ToFileSystemInfo().LinkTarget is not null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { return true; }
    }

    private static string[] ScopeRoots(IReadOnlyList<string> scopeRoots)
    {
        var roots = new List<string>();
        foreach (var value in scopeRoots.Where(value => !string.IsNullOrWhiteSpace(value)).OrderBy(value => value.Length))
        {
            var root = LogicalPath(value);
            if (!Path.IsPathFullyQualified(root) || root.Contains('\0') || roots.Any(parent => IsInside(root, parent))) continue;
            roots.Add(root);
        }
        return roots.ToArray();
    }

    private static bool IsInside(string path, string root)
    {
        path = LogicalPath(path);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root.EndsWith('\\') ? root : root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string LogicalPath(string path)
    {
        path = path.Replace('/', '\\');
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return path.Length > 3 ? path.TrimEnd('\\') : path;
    }

    private static string ExtendedLength(string path)
    {
        if (path.Length >= 3 && path[1] == ':' && path[2] == '\\') return @"\\?\" + path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
        return path;
    }

    private readonly record struct ScanEntry(string Name, bool IsDirectory, bool IsLink);
}
