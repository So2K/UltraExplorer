using System.Collections.Concurrent;
using System.IO.Enumeration;

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
                if (first is not null && Directory.Exists(first))
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
            var entries = new FileSystemEnumerable<Entry>(
                folder,
                static (ref FileSystemEntry entry) => new Entry(
                    entry.FileName.ToString(),
                    entry.IsDirectory,
                    (entry.Attributes & FileAttributes.ReparsePoint) != 0,
                    entry.IsDirectory ? -1 : entry.Length,
                    entry.LastWriteTimeUtc.LocalDateTime),
                Options)
            {
                // Strings only for what is needed: a match, or a folder to go into.
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                    entry.IsDirectory || _query.Matches(entry.FileName, entry.Directory, isFolder: false),
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

    private void Add(SearchHit hit)
    {
        if (Interlocked.Increment(ref _hits) <= _maximum)
        {
            _found.Enqueue(hit);
        }
    }

    private readonly record struct Entry(string Name, bool IsFolder, bool IsLink, long Size, DateTime Modified);
}
