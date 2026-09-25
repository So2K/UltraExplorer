using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows.Media;
using UltraExplorer.Infrastructure;

namespace UltraExplorer.Services;

/// <summary>A colour label and a note, both optional.</summary>
public sealed record FolderMark(string AccentHex = "", string Note = "")
{
    public static readonly FolderMark None = new();

    public bool IsEmpty => string.IsNullOrEmpty(AccentHex) && string.IsNullOrWhiteSpace(Note);
}

/// <summary>
/// Marks belong to a path, not to a canvas node.  A folder therefore keeps its
/// colour and note when it is closed and reopened, and both canvas modes show
/// the same mark for the same folder.
/// </summary>
public sealed class FolderMarkService
{
    public const string DefaultAccentHex = "#60CDFF";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ConcurrentDictionary<string, FolderMark> _marks = new(StringComparer.OrdinalIgnoreCase);

    public FolderMarkService(string? statePath = null)
    {
        StatePath = statePath ?? AppPaths.State("folder-marks.json");
    }

    public string StatePath { get; }

    /// <summary>Raised on the thread that changed the mark; callers marshal.</summary>
    public event Action<string, FolderMark>? MarkChanged;

    public FolderMark Get(string path)
        => _marks.TryGetValue(Key(path), out var mark) ? mark : FolderMark.None;

    /// <summary>Every mark there is, for drawing them all at once (the nested canvas's beacons).</summary>
    public IReadOnlyList<KeyValuePair<string, FolderMark>> Snapshot()
        => [.. _marks.Where(pair => !pair.Value.IsEmpty)];

    public string GetAccentHex(string path, string fallback)
    {
        var accent = Get(path).AccentHex;
        return string.IsNullOrEmpty(accent) ? fallback : accent;
    }

    public Brush GetAccentBrush(string path, string fallback)
        => BrushCache.Get(GetAccentHex(path, fallback));

    public void SetAccent(string path, string? accentHex)
    {
        var key = Key(path);
        var current = Get(key);
        var updated = current with { AccentHex = accentHex ?? string.Empty };
        Store(key, updated);
    }

    public void SetNote(string path, string? note)
    {
        var key = Key(path);
        var current = Get(key);
        var updated = current with { Note = note ?? string.Empty };
        Store(key, updated);
    }

    /// <summary>Seeds a mark without raising a change (used by workspace migration).</summary>
    public void Seed(string path, string accentHex, string note)
    {
        var mark = new FolderMark(accentHex ?? string.Empty, note ?? string.Empty);
        if (mark.IsEmpty)
        {
            return;
        }

        _marks[Key(path)] = mark;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(StatePath))
        {
            return;
        }

        try
        {
            await using var stream = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16 * 1024, useAsync: true);
            var stored = await JsonSerializer.DeserializeAsync<Dictionary<string, FolderMark>>(stream, JsonOptions, cancellationToken);
            if (stored is null)
            {
                return;
            }

            foreach (var pair in stored.Where(pair => !pair.Value.IsEmpty))
            {
                _marks[Key(pair.Key)] = pair.Value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // A damaged marks file must never stop the app from opening.
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(StatePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(StatePath)}.{Guid.NewGuid():N}.tmp");
        var snapshot = _marks.Where(pair => !pair.Value.IsEmpty).ToDictionary(pair => pair.Key, pair => pair.Value);

        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, StatePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persisting a colour label is never worth surfacing an error for.
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private void Store(string key, FolderMark mark)
    {
        if (mark.IsEmpty)
        {
            _marks.TryRemove(key, out _);
        }
        else
        {
            _marks[key] = mark;
        }

        MarkChanged?.Invoke(key, mark);
    }

    private static string Key(string path)
    {
        try
        {
            return Models.ViewAllPath.Normalize(path);
        }
        catch
        {
            return path.Trim();
        }
    }
}
