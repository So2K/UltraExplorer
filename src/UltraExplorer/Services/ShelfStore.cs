using System.Text.Json;
using UltraExplorer.Infrastructure;
using UltraExplorer.Services.Archives;

namespace UltraExplorer.Services;

/// <summary>One thing on the shelf: where it really is, when it was put there, and where its card lies on the board.</summary>
public sealed record ShelfEntry(string Path, DateTime AddedUtc, bool IsCopy, double X = double.NaN, double Y = double.NaN)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasPosition => !double.IsNaN(X) && !double.IsNaN(Y);
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

    public ShelfStore()
    {
        Root = System.IO.Path.Combine(AppPaths.StateDirectory, "Shelf");
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
        foreach (var raw in paths)
        {
            var path = System.IO.Path.GetFullPath(raw);
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
            if (path.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || path.StartsWith(ArchiveService.TempRoot, StringComparison.OrdinalIgnoreCase))
            {
                // Each copy in a folder of its own: let go of, it goes with it.
                var copies = System.IO.Path.Combine(Root, "copies", Guid.NewGuid().ToString("N")[..12]);
                Directory.CreateDirectory(copies);
                await _shell.CopyOrMoveAsync([path], copies, move: false);
                var copy = System.IO.Path.Combine(copies, System.IO.Path.GetFileName(path));
                if (!File.Exists(copy) && !Directory.Exists(copy))
                {
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

        Save();
        Changed?.Invoke();
        return (added, placed);
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
        Width = width;
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
        List<ShelfEntry> gone;
        lock (_gate)
        {
            gone = _entries.Where(entry => DateTime.UtcNow - entry.AddedUtc > KeepFor || !File.Exists(entry.Path) && !Directory.Exists(entry.Path)).ToList();
            if (gone.Count == 0)
            {
                return false;
            }

            _entries = _entries.Except(gone).ToList();
        }

        DeleteCopies(gone);
        Save();
        Changed?.Invoke();
        return true;
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
                    _entries = saved.Entries;
                    Width = saved.Width;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _entries = [];
        }

        _entries = _entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Path)).ToList();
    }

    private void Save()
    {
        var saved = new Saved { Width = Width };
        lock (_gate)
        {
            saved.Entries = [.. _entries];
        }

        try
        {
            Directory.CreateDirectory(Root);
            var temporary = ListPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(saved, JsonOptions));
            File.Move(temporary, ListPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void DeleteCopies(IEnumerable<ShelfEntry> entries)
    {
        foreach (var entry in entries.Where(entry => entry.IsCopy && entry.Path.StartsWith(Root, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                Directory.Delete(System.IO.Path.GetDirectoryName(entry.Path)!, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    internal static string SizeText(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:N1} MB",
        _ => $"{bytes / 1073741824.0:N2} GB"
    };
}
