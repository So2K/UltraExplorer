namespace UltraExplorer.Services;

/// <summary>Deduplicates committed folder requests through the caller's retry
/// window. Completed receipts expire; a long-lived broker must not stop
/// accepting folders merely because it has served 4,096 earlier requests.</summary>
internal sealed class FolderRouteRequestCache
{
    private sealed class Entry(string fingerprint)
    {
        internal readonly string Fingerprint = fingerprint;
        internal Lazy<Task<FolderRouteReceipt>> Work = null!;
        internal long? CompletedAt;
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Func<long> _now;
    private readonly int _capacity;
    private readonly long _retention;

    internal FolderRouteRequestCache(int capacity = 4096, long retentionMilliseconds = 120000, Func<long>? now = null)
    {
        if (capacity <= 0 || retentionMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _retention = retentionMilliseconds;
        _now = now ?? (() => Environment.TickCount64);
    }

    internal bool CanOffer(Guid id, string fingerprint)
    {
        lock (_gate)
        {
            Prune();
            return _entries.TryGetValue(id, out var entry) ? entry.Fingerprint == fingerprint : _entries.Count < _capacity;
        }
    }

    internal bool TryCommit(Guid id, string fingerprint, Func<Task<FolderRouteReceipt>> execute, out Task<FolderRouteReceipt> work)
    {
        Entry entry;
        lock (_gate)
        {
            Prune();
            if (_entries.TryGetValue(id, out entry!))
            {
                if (entry.Fingerprint != fingerprint) { work = null!; return false; }
            }
            else
            {
                if (_entries.Count >= _capacity) { work = null!; return false; }
                entry = new(fingerprint);
                var captured = entry;
                entry.Work = new(() => ExecuteAsync(captured, execute));
                _entries.Add(id, entry);
            }
        }
        work = entry.Work.Value;
        return true;
    }

    private async Task<FolderRouteReceipt> ExecuteAsync(Entry entry, Func<Task<FolderRouteReceipt>> execute)
    {
        try { return await execute().ConfigureAwait(false); }
        finally { lock (_gate) entry.CompletedAt = _now(); }
    }

    private void Prune()
    {
        var now = _now();
        foreach (var id in _entries.Where(pair => pair.Value.CompletedAt is { } completed && now - completed >= _retention)
                     .Select(pair => pair.Key).ToArray())
            _entries.Remove(id);
    }

    internal int Count { get { lock (_gate) return _entries.Count; } }
}
