using System.Text.Json;
using UltraExplorer.Infrastructure;
using UltraExplorer.Services.Archives;

namespace UltraExplorer.Services;

/// <summary>One thing on the shelf: where it really is, when it was put there, and where its card lies on the board.</summary>
public sealed record ShelfEntry(string Path, DateTime AddedUtc, bool IsCopy, double X = double.NaN, double Y = double.NaN)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasPosition => double.IsFinite(X) && double.IsFinite(Y);
}

/// <summary>
/// The shelf: a board to drop things on, on the way somewhere else.  It
/// keeps links - the paths of what was dropped, not copies - so putting a
/// folder of ten gigabytes on it is instant and costs nothing.  Only what
/// would vanish by itself is copied: something taken out of an archive, or
/// anything else in the temporary folder.  Everything is let go of after a
/// week (a copy deleted with it); a link to something since moved or deleted
/// simply drops off.  Each card keeps the place it was put on the board, and
/// the shelf keeps its width.
/// </summary>
public sealed class ShelfStore
{
    /// <summary>How long something stays on the shelf.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,

        // A card with no place yet is NaN, which JSON has no number for.
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    /// <summary>Every shelf open in this process, for one window's save to reach the others' cards.</summary>
    private static readonly List<WeakReference<ShelfStore>> Open = [];

    private readonly object _gate = new();
    private readonly NativeShellService _shell = new();

    /// <summary>The file's turn among every window and copy of the app: the same name for the same shelf.</summary>
    private readonly string _turnName;

    private List<ShelfEntry> _entries = [];

    /// <summary>
    /// This window's changes not yet written.  Each window has a shelf of its
    /// own over the same file, so a save never writes back the list as this
    /// window read it - that would drop what another window put on the shelf
    /// since, or bring back what it took off.  The file is read again in its
    /// turn and these changes are replayed over it.  A save that could not
    /// read or write the file keeps them for the next.
    /// </summary>
    private readonly List<Action<List<ShelfEntry>>> _pending = [];
    private double? _pendingWidth;

    public ShelfStore() : this(System.IO.Path.Combine(AppPaths.StateDirectory, "Shelf")) { }

    /// <summary>Explicit state directory for isolated checks and portable profiles.</summary>
    internal ShelfStore(string root)
    {
        Root = System.IO.Path.GetFullPath(root);
        _turnName = @"Local\UltraExplorer.Shelf." + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Root.ToUpperInvariant())));
        Sync(write: false);
        lock (Open)
        {
            Open.RemoveAll(reference => !reference.TryGetTarget(out _));
            Open.Add(new WeakReference<ShelfStore>(this));
        }
    }

    /// <summary>The shelf's own folder: its list, and the copies it keeps.</summary>
    public string Root { get; }

    private string ListPath => System.IO.Path.Combine(Root, "shelf.json");

    /// <summary>How wide the shelf was last pulled, in DIPs.</summary>
    public double Width { get; private set; } = 520;

    /// <summary>Raised on any change to what is on the shelf.</summary>
    public event Action? Changed;

    public IReadOnlyList<ShelfEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Puts things on the shelf: a link to each, or a copy of what lives in
    /// the temporary folder.  Returns how many are new, and the shelf's path
    /// of every one of them - those already on it too - for the board to lay
    /// their cards out where they were dropped.
    /// </summary>
    public async Task<(int Added, IReadOnlyList<string> Paths)> AddAsync(IReadOnlyList<string> paths)
    {
        var added = 0;
        var placed = new List<string>();
        var temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        try
        {
            foreach (var raw in paths)
            {
                var path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(raw));
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    continue;
                }

                if (FindEntry(path) is { } already)
                {
                    placed.Add(already.Path);
                    continue;
                }

                var entry = new ShelfEntry(path, DateTime.UtcNow, IsCopy: false);
                if (IsWithin(path, temp) || IsWithin(path, ArchiveService.TempRoot))
                {
                    // Each copy in a folder of its own: let go of, it goes with it.
                    var copies = System.IO.Path.Combine(Root, "copies", Guid.NewGuid().ToString("N")[..12]);
                    Directory.CreateDirectory(copies);
                    try
                    {
                        await _shell.CopyOrMoveAsync([path], copies, move: false);
                    }
                    catch
                    {
                        DeleteCopyDirectory(copies);
                        throw;
                    }
                    var copy = System.IO.Path.Combine(copies, System.IO.Path.GetFileName(path));
                    if (!File.Exists(copy) && !Directory.Exists(copy))
                    {
                        DeleteCopyDirectory(copies);
                        continue;
                    }

                    entry = entry with { Path = copy, IsCopy = true };
                }

                Change(list =>
                {
                    if (!list.Any(other => string.Equals(other.Path, entry.Path, StringComparison.OrdinalIgnoreCase))) list.Add(entry);
                });

                placed.Add(entry.Path);
                added++;
            }
            return (added, placed);
        }
        finally
        {
            // Earlier accepted items remain durable if a later input fails.
            if (added > 0) { Save(); Changed?.Invoke(); }
        }
    }

    private ShelfEntry? FindEntry(string path)
    {
        lock (_gate)
        {
            return _entries.FirstOrDefault(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Applies a change to this window's list now, and to the file's at the next save.</summary>
    private void Change(Action<List<ShelfEntry>> change)
    {
        lock (_gate)
        {
            change(_entries);
            _pending.Add(change);
        }
    }

    /// <summary>Cards moved on the board: their places kept.</summary>
    public void SetPositions(IEnumerable<(string Path, double X, double Y)> places)
    {
        var moved = places.ToList();
        Change(list =>
        {
            foreach (var (path, x, y) in moved)
            {
                var at = list.FindIndex(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));
                if (at >= 0)
                {
                    list[at] = list[at] with { X = x, Y = y };
                }
            }
        });

        Save();
    }

    /// <summary>The shelf pulled wider or narrower.</summary>
    public void SetWidth(double width)
    {
        lock (_gate)
        {
            Width = double.IsFinite(width) ? Math.Clamp(width, 300, 4000) : 520;
            _pendingWidth = Width;
        }

        Save();
    }

    /// <summary>Takes items off the shelf; their copies, if any, are deleted - a link leaves the real thing alone.</summary>
    public void Remove(IEnumerable<string> paths)
    {
        var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        List<ShelfEntry> removed;
        lock (_gate)
        {
            removed = _entries.Where(entry => wanted.Contains(entry.Path)).ToList();
            if (removed.Count > 0) Change(list => list.RemoveAll(entry => wanted.Contains(entry.Path)));
        }

        if (removed.Count == 0)
        {
            return;
        }

        DeleteCopies(removed);
        Save();
        Changed?.Invoke();
    }

    public void Clear()
    {
        List<ShelfEntry> all;
        lock (_gate)
        {
            all = [.. _entries];

            // Only what this window showed: a card another window or copy
            // put on the shelf since - and its copy - is not this Clear's.
            var shown = all.Select(entry => (entry.Path.ToUpperInvariant(), entry.AddedUtc)).ToHashSet();
            Change(list => list.RemoveAll(entry => shown.Contains((entry.Path.ToUpperInvariant(), entry.AddedUtc))));
        }

        DeleteCopies(all);
        Save();
        Changed?.Invoke();
    }

    /// <summary>Lets go of what is past its week, or gone from where it was.  True when anything went.</summary>
    public bool Prune()
    {
        List<ShelfEntry> snapshot;
        lock (_gate)
        {
            snapshot = [.. _entries];
        }

        // A disconnected share can wait a long time. Its disk query never
        // owns the entries lock that the UI needs for its count and cards.
        var now = DateTime.UtcNow;
        var gone = snapshot.Where(entry => now - entry.AddedUtc > KeepFor || DefinitelyMissing(entry.Path)).ToList();
        if (gone.Count == 0) return false;
        var candidates = gone.ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        bool Expired(ShelfEntry entry) => candidates.TryGetValue(entry.Path, out var candidate)
            && entry.AddedUtc == candidate.AddedUtc && entry.IsCopy == candidate.IsCopy;
        lock (_gate)
        {
            gone = _entries.Where(Expired).ToList();
            if (gone.Count > 0) Change(list => list.RemoveAll(Expired));
        }
        if (gone.Count == 0) return false;

        DeleteCopies(gone);
        Save();
        Changed?.Invoke();
        return true;
    }

    internal static bool DefinitelyMissing(string path)
    {
        try { _ = File.GetAttributes(path); return false; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // A missing path is conclusive only while its volume/share is
            // available. Ejecting a USB drive does not remove its shelf card.
            try
            {
                var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
                return !string.IsNullOrEmpty(root) && Directory.Exists(root);
            }
            catch (Exception invalid) when (invalid is ArgumentException or NotSupportedException or IOException) { return false; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private sealed class Saved
    {
        public double Width { get; set; } = 520;
        public List<ShelfEntry> Entries { get; set; } = [];
    }

    /// <summary>
    /// Reads the file in its turn and lays this window's pending changes over
    /// it; with <paramref name="write"/>, writes the result back.  True when
    /// it was written.  A file that cannot be read now is never written over:
    /// the list as this window has it could be empty or old.
    /// </summary>
    private bool Sync(bool write)
    {
        var dropped = new List<ShelfEntry>();
        var foreign = false;
        lock (_gate)
        {
            Mutex? turn = null;
            var entered = false;
            try
            {
                // A copy that hangs in its turn is not waited for long; the
                // read, merge and write still go over the file as it is now.
                try
                {
                    turn = new Mutex(false, _turnName);
                    entered = turn.WaitOne(TimeSpan.FromSeconds(3));
                }
                catch (AbandonedMutexException) { entered = true; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { }

                if (!TryRead(out var stored, out var width))
                {
                    return false;
                }

                var merged = new List<ShelfEntry>(stored);
                foreach (var change in _pending)
                {
                    change(merged);
                }

                width = _pendingWidth ?? width;
                if (write)
                {
                    if (!Write(merged, width))
                    {
                        return false;
                    }

                    _pending.Clear();
                    _pendingWidth = null;

                    // What this window's changes took off - a card another
                    // window put there too - goes with its copy.
                    dropped = stored.Where(entry => entry.IsCopy
                        && !merged.Any(kept => string.Equals(kept.Path, entry.Path, StringComparison.OrdinalIgnoreCase))).ToList();
                }

                Width = width;
                foreign = !merged.SequenceEqual(_entries);
                _entries = merged;
            }
            finally
            {
                if (entered)
                {
                    turn!.ReleaseMutex();
                }

                turn?.Dispose();
            }
        }

        DeleteCopies(dropped);
        if (foreign)
        {
            // Another window's or copy's changes: the cards follow.
            Changed?.Invoke();
        }

        return write;
    }

    private bool TryRead(out List<ShelfEntry> entries, out double width)
    {
        entries = [];
        width = Width;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(ListPath))
                {
                    var text = File.ReadAllText(ListPath);
                    if (text.TrimStart().StartsWith('['))
                    {
                        entries = JsonSerializer.Deserialize<List<ShelfEntry>>(text, JsonOptions) ?? [];
                    }
                    else if (JsonSerializer.Deserialize<Saved>(text, JsonOptions) is { } saved)
                    {
                        entries = saved.Entries ?? [];
                        width = double.IsFinite(saved.Width) ? Math.Clamp(saved.Width, 300, 4000) : 520;
                    }
                }

                break;
            }
            catch (JsonException)
            {
                // Not to be understood: set aside, and the shelf starts over.
                StateFiles.Quarantine(ListPath);
                entries = [];
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held a moment by a scanner or a backup: tried again briefly.
                if (attempt >= 2)
                {
                    return false;
                }

                Thread.Sleep(40 * (attempt + 1));
            }
        }

        entries = entries.Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Path))
            .DistinctBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToList();
        return true;
    }

    private bool Write(List<ShelfEntry> entries, double width)
    {
        var temporary = System.IO.Path.Combine(Root, $".shelf.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Saved { Width = width, Entries = entries }, JsonOptions));
            File.Move(temporary, ListPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    /// <summary>Writes this window's changes; the other windows' shelves take them up.</summary>
    private void Save()
    {
        if (!Sync(write: true))
        {
            return;
        }

        List<ShelfStore> others;
        lock (Open)
        {
            others = Open.Select(reference => reference.TryGetTarget(out var store) ? store : null)
                .OfType<ShelfStore>()
                .Where(store => store != this && string.Equals(store.Root, Root, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        foreach (var other in others)
        {
            other.Sync(write: false);
        }
    }

    private void DeleteCopies(IEnumerable<ShelfEntry> entries)
    {
        foreach (var entry in entries.Where(entry => entry.IsCopy))
        {
            try
            {
                var fullPath = System.IO.Path.GetFullPath(entry.Path);
                if (System.IO.Path.GetDirectoryName(fullPath) is { } parent)
                    DeleteCopyDirectory(parent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }
        }
    }

    private void DeleteCopyDirectory(string directory)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(directory).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            var copies = System.IO.Path.Combine(Root, "copies");
            var leaf = System.IO.Path.GetFileName(full);
            // A persisted flag never authorizes deleting a link's parent, a
            // prefix sibling, the shelf root, or a reparse-point destination.
            if (!string.Equals(System.IO.Path.GetDirectoryName(full), copies, StringComparison.OrdinalIgnoreCase)
                || leaf.Length != 12 || !leaf.All(Uri.IsHexDigit) || !Directory.Exists(full)) return;
            if (new[] { Root, copies, full }.Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)) return;
            Directory.Delete(full, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }

    internal static bool IsWithin(string path, string directory)
    {
        var prefix = System.IO.Path.GetFullPath(directory).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            + System.IO.Path.DirectorySeparatorChar;
        return System.IO.Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static string SizeText(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:N1} MB",
        _ => $"{bytes / 1073741824.0:N2} GB"
    };
}
