using System.Collections.Concurrent;
using System.IO.Enumeration;
using UltraExplorer.Infrastructure;

namespace UltraExplorer.Services.Search;

/// <summary>
/// The search without Everything: the folders read one by one, on a few
/// threads of low priority, the folder searched from first and then every
/// fixed drive.  Each folder is one listing call that also gives every
/// entry's kind, size and date - nothing is asked of the disk per entry - and
/// a name is made into a string only when it matches or is a folder to go
/// into.  Hits are collected as they are found, for whoever is showing them
/// to take in batches (<see cref="Drain"/>).
/// </summary>
internal sealed class FolderWalk
{
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024,
    };

    private readonly SearchQuery _query;
    private readonly int _maximum;
    private readonly ConcurrentQueue<SearchHit> _found = new();
    private readonly ConcurrentQueue<string> _folders = new();
    private int _hits;
    private long _foldersRead;
    private int _outstanding;

    /// <summary>Set once a folder's failure has been written to the log (see <see cref="Work"/>).</summary>
    private int _failureLogged;

    public FolderWalk(SearchQuery query, int maximum)
    {
        _query = query;
        _maximum = maximum;
    }

    /// <summary>Folders listed so far.</summary>
    public long FoldersRead => Interlocked.Read(ref _foldersRead);

    /// <summary>Whether as many hits were found as were wanted, and the walk stopped for it.</summary>
    public bool IsFull => Volatile.Read(ref _hits) >= _maximum;

    /// <summary>The hits found since the last call.</summary>
    public List<SearchHit> Drain()
    {
        var hits = new List<SearchHit>();
        while (_found.TryDequeue(out var hit))
        {
            hits.Add(hit);
        }

        return hits;
    }

    /// <summary>
    /// Walks <paramref name="first"/>, when there is one, to the end, then
    /// each of <paramref name="roots"/>, not going into <paramref name="first"/>
    /// again nor into any of <paramref name="skip"/>.  Returns when all is
    /// walked, enough was found, or <paramref name="cancellationToken"/> is
    /// cancelled.
    /// </summary>
    public Task RunAsync(string? first, IReadOnlyList<string> roots, IReadOnlyCollection<string> skip, CancellationToken cancellationToken)
    {
        var excluded = new HashSet<string>(skip.Select(Trim), StringComparer.OrdinalIgnoreCase);
        return Task.Factory.StartNew(
            () =>
            {
                if (first is not null && Directory.Exists(ExtendedLength(first)))
                {
                    Walk([first], excluded, cancellationToken);
                    excluded.Add(Trim(first));
                }

                if (!IsFull && !cancellationToken.IsCancellationRequested)
                {
                    Walk(roots.Where(root => !excluded.Contains(Trim(root))).ToArray(), excluded, cancellationToken);
                }
            },
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private static string Trim(string path) => path.Length > 3 ? path.TrimEnd('\\') : path;

    /// <summary>Lists every folder under <paramref name="starts"/> on a few threads, each taking the next folder queued.</summary>
    private void Walk(IReadOnlyList<string> starts, HashSet<string> excluded, CancellationToken cancellationToken)
    {
        if (starts.Count == 0)
        {
            return;
        }

        foreach (var start in starts)
        {
            Interlocked.Increment(ref _outstanding);
            _folders.Enqueue(start);
        }

        var workers = Math.Clamp(Environment.ProcessorCount / 4, 2, 6);
        var threads = new Thread[workers];
        for (var index = 0; index < workers; index++)
        {
            threads[index] = new Thread(() => Work(excluded, cancellationToken))
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "Search walk",
            };
            threads[index].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        // Stopped early: what was queued is dropped for the next walk.
        _folders.Clear();
        Volatile.Write(ref _outstanding, 0);
    }

    private void Work(HashSet<string> excluded, CancellationToken cancellationToken)
    {
        var idle = 0;
        while (Volatile.Read(ref _outstanding) > 0 && !cancellationToken.IsCancellationRequested && !IsFull)
        {
            if (!_folders.TryDequeue(out var folder))
            {
                // Another thread is still listing a folder that may queue more.
                Thread.Sleep(idle++ < 10 ? 0 : 1);
                continue;
            }

            idle = 0;
            try
            {
                List(folder, excluded, cancellationToken);
            }
            catch (Exception exception)
            {
                // Whatever else one folder throws, it costs that folder only:
                // on this thread an exception nobody catches ends the process,
                // and every window with it.  Written down the first time, so a
                // walk that meets many such folders does not fill the log.
                if (Interlocked.Exchange(ref _failureLogged, 1) == 0)
                {
                    CrashReporter.Log($"search walk: a folder could not be read ({folder})", exception);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _outstanding);
            }
        }
    }

    private void List(string folder, HashSet<string> excluded, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _foldersRead);
        try
        {
            // Listed by its extended-length name, and its hits put in the
            // folder as it is spelled everywhere else.
            var entries = new FileSystemEnumerable<Entry>(
                ExtendedLength(folder),
                static (ref FileSystemEntry entry) => new Entry(
                    entry.FileName.ToString(),
                    entry.IsDirectory,
                    entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) != 0 && IsLink(ref entry),
                    entry.IsDirectory ? -1 : entry.Length,
                    Modified(ref entry)),
                Options)
            {
                // Strings only for what is needed: a match, or a folder to go into.
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                    entry.IsDirectory || _query.Matches(entry.FileName, folder, isFolder: false),
            };

            foreach (var entry in entries)
            {
                if (cancellationToken.IsCancellationRequested || IsFull)
                {
                    return;
                }

                if (entry.IsFolder)
                {
                    if (_query.Matches(entry.Name, folder, isFolder: true))
                    {
                        Add(new SearchHit(entry.Name, folder, true, -1, entry.Modified, null));
                    }

                    // A junction or a link leads to folders read elsewhere, or
                    // round in a circle.
                    if (!entry.IsLink)
                    {
                        var path = SearchHit.Join(folder, entry.Name);
                        if (!excluded.Contains(path))
                        {
                            Interlocked.Increment(ref _outstanding);
                            _folders.Enqueue(path);
                        }
                    }

                    continue;
                }

                Add(new SearchHit(entry.Name, folder, false, entry.Size, entry.Modified, null));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }

    /// <summary>
    /// Whether a folder with a reparse point is a link - a junction, a symbolic
    /// link, a mount point - rather than a cloud placeholder (OneDrive files
    /// on demand: Documents and Desktop, once moved there) or a projected
    /// folder, which carry the same attribute but are ordinary folders to
    /// walk.  Only links report a target; as <see cref="NestedDirectoryReader"/>
    /// decides it for the canvas.
    /// </summary>
    private static bool IsLink(ref FileSystemEntry entry)
    {
        try
        {
            return entry.ToFileSystemInfo().LinkTarget is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// The path in its extended-length form for a local drive or a share
    /// (<c>\\server\share\...</c> as <c>\\?\UNC\server\share\...</c>).
    /// Without it the enumerator normalises the path, which strips a trailing
    /// dot or space - and a folder named "dup." would be listed as its twin
    /// "dup", one named "lone " as nothing.  A path in either form already,
    /// or a device's, is left as it is.  As <see cref="NestedDirectoryReader"/>
    /// reads its folders.
    /// </summary>
    private static string ExtendedLength(string path)
    {
        if (path.Length >= 3 && path[1] == ':' && path[2] == Path.DirectorySeparatorChar)
        {
            return @"\\?\" + path;
        }

        if (path.Length > 2 && path[0] == Path.DirectorySeparatorChar && path[1] == Path.DirectorySeparatorChar
            && !(path.Length > 3 && path[2] is ('?' or '.') && path[3] == Path.DirectorySeparatorChar))
        {
            return @"\\?\UNC\" + path[2..];
        }

        return path;
    }

    /// <summary>
    /// An entry's last-write time, local, or null - no date, as Everything
    /// gives for one it does not know - when it is past what a date can hold:
    /// a tool or a damaged volume can leave such a time on a file, and reading
    /// it throws, which would end the walk and the process every time a search
    /// came to that folder.  As <see cref="NestedDirectoryReader"/>'s
    /// WriteTicks does for the canvas.
    /// </summary>
    private static DateTime? Modified(ref FileSystemEntry entry)
    {
        try
        {
            return entry.LastWriteTimeUtc.LocalDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private void Add(SearchHit hit)
    {
        if (Interlocked.Increment(ref _hits) <= _maximum)
        {
            _found.Enqueue(hit);
        }
    }

    private readonly record struct Entry(string Name, bool IsFolder, bool IsLink, long Size, DateTime? Modified);
}
