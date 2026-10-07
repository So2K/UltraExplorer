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

    private readonly object _gate = new();
    private readonly NativeShellService _shell = new();
    private List<ShelfEntry> _entries = [];

    public ShelfStore() : this(System.IO.Path.Combine(AppPaths.StateDirectory, "Shelf")) { }

    /// <summary>Explicit state directory for isolated checks and portable profiles.</summary>
    internal ShelfStore(string root)
    {
        Root = System.IO.Path.GetFullPath(root);
        Load();
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

                lock (_gate)
                {
                    _entries.Add(entry);
                }

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

    private void Replace(ShelfEntry old, ShelfEntry now)
    {
        lock (_gate)
        {
            var at = _entries.IndexOf(old);
            if (at >= 0)
            {
                _entries[at] = now;
            }
        }
    }

    /// <summary>Cards moved on the board: their places kept.</summary>
    public void SetPositions(IEnumerable<(string Path, double X, double Y)> places)
    {
        lock (_gate)
        {
            foreach (var (path, x, y) in places)
            {
                var at = _entries.FindIndex(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));
                if (at >= 0)
                {
                    _entries[at] = _entries[at] with { X = x, Y = y };
                }
            }
        }

        Save();
    }

    /// <summary>The shelf pulled wider or narrower.</summary>
    public void SetWidth(double width)
    {
        Width = double.IsFinite(width) ? Math.Clamp(width, 300, 4000) : 520;
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
            _entries = _entries.Except(removed).ToList();
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
            all = _entries;
            _entries = [];
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
        lock (_gate)
        {
            gone = _entries.Where(entry => candidates.TryGetValue(entry.Path, out var candidate)
                && entry.AddedUtc == candidate.AddedUtc && entry.IsCopy == candidate.IsCopy).ToList();
            _entries = _entries.Except(gone).ToList();
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

    private void Load()
    {
        try
        {
            if (File.Exists(ListPath))
            {
                var text = File.ReadAllText(ListPath);
                if (text.TrimStart().StartsWith('['))
                {
                    _entries = JsonSerializer.Deserialize<List<ShelfEntry>>(text, JsonOptions) ?? [];
                }
                else if (JsonSerializer.Deserialize<Saved>(text, JsonOptions) is { } saved)
                {
                    _entries = saved.Entries ?? [];
                    Width = double.IsFinite(saved.Width) ? Math.Clamp(saved.Width, 300, 4000) : 520;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _entries = [];
        }

        _entries = _entries.Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Path))
            .DistinctBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Root);
                var temporary = ListPath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new Saved { Width = Width, Entries = [.. _entries] }, JsonOptions));
                File.Move(temporary, ListPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
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
