using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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

    // The folders that hold a mark, rebuilt whole with every change - marks
    // change a few times a session, and are asked about thousands of times a
    // frame - and handed out as a set nobody changes once it is published,
    // so the canvas reads it on the UI thread without a lock while a mark is
    // set on another.
    private readonly Lock _markedFoldersGate = new();
    private volatile HashSet<string> _markedFolders = new(StringComparer.OrdinalIgnoreCase);
    private static long _pathsNormalised;

    public FolderMarkService(string? statePath = null)
    {
        StatePath = statePath ?? AppPaths.State("folder-marks.json");
    }

    public string StatePath { get; }

    /// <summary>Raised on the thread that changed the mark; callers marshal.</summary>
    public event Action<string, FolderMark>? MarkChanged;

    /// <summary>
    /// The mark of <paramref name="path"/>, or <see cref="FolderMark.None"/>.
    /// The nested canvas asks for every folder it draws - tens of thousands
    /// in one frame the first time a big folder comes into view - so the
    /// common case is kept cheap: with nothing marked there is nothing to
    /// look up, and a path already in the form the keys are kept in
    /// (<see cref="IsKeyForm"/>) is looked up as it is, rather than
    /// normalised at a microsecond and a few hundred bytes apiece.
    /// </summary>
    public FolderMark Get(string path)
    {
        if (_marks.IsEmpty)
        {
            return FolderMark.None;
        }

        return _marks.TryGetValue(IsKeyForm(path) ? path : Key(path), out var mark) ? mark : FolderMark.None;
    }

    /// <summary>
    /// Every folder that holds a mark directly inside it - on one of its files
    /// or sub-folders - by the canonical path marks are kept under, which is
    /// the one a <see cref="Models.NestedFolder.FullPath"/> is written in.
    /// The nested canvas asks this before it looks up any of a folder's files:
    /// nearly every folder holds none, and for those no file needs a path
    /// made, normalised or looked up at all.
    /// </summary>
    public IReadOnlySet<string> MarkedFolders => _markedFolders;

    /// <summary>
    /// The mark of one of the nested canvas's folders, looked up by its path
    /// as it is: a folder's path is its parent's joined to the name its
    /// listing gave it, which is the form marks are kept under, so nothing is
    /// normalised - not even checked for being normalised - however many are
    /// drawn.  A root's path came from outside the tree, as its drive or
    /// share was given, and goes the long way; there are only a few.
    /// </summary>
    public FolderMark Get(Models.NestedFolder folder)
    {
        if (_marks.IsEmpty)
        {
            return FolderMark.None;
        }

        if (IsRoot(folder))
        {
            return Get(folder.FullPath);
        }

        return _marks.TryGetValue(folder.FullPath, out var mark) ? mark : FolderMark.None;
    }

    /// <summary>
    /// Whether anything directly inside <paramref name="folder"/> - a file or
    /// a sub-folder - carries a mark (<see cref="MarkedFolders"/>).
    /// </summary>
    public bool HasMarksIn(Models.NestedFolder folder)
    {
        var marked = _markedFolders;
        return marked.Count > 0 && marked.Contains(KeyOf(folder));
    }

    /// <summary>
    /// The mark of the entry called <paramref name="name"/> directly inside
    /// <paramref name="folder"/> - one of its files - without making its
    /// path: the folder's path and the name are joined on the stack and
    /// looked up as they are, and only in a folder that holds a mark at all.
    /// </summary>
    public FolderMark GetIn(Models.NestedFolder folder, string name)
    {
        if (!HasMarksIn(folder))
        {
            return FolderMark.None;
        }

        var directory = KeyOf(folder);
        var separator = directory.EndsWith(Path.DirectorySeparatorChar) ? 0 : 1;
        var length = directory.Length + separator + name.Length;
        char[]? rented = null;
        var path = length <= 512 ? stackalloc char[length] : (rented = ArrayPool<char>.Shared.Rent(length)).AsSpan(0, length);
        try
        {
            directory.AsSpan().CopyTo(path);
            if (separator != 0)
            {
                path[directory.Length] = Path.DirectorySeparatorChar;
            }

            name.AsSpan().CopyTo(path[(directory.Length + separator)..]);
            return _marks.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(path, out var mark) ? mark : FolderMark.None;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// For tests: how many paths have been normalised to find or keep a mark
    /// under, since the process started.  A frame of the nested canvas must
    /// add none.
    /// </summary>
    internal static long PathsNormalised => Interlocked.Read(ref _pathsNormalised);

    /// <summary>A folder at the top of the canvas's tree - a drive, a share, a folder added as a root - or This PC itself.</summary>
    private static bool IsRoot(Models.NestedFolder folder) => folder.Parent is null || folder.Parent.IsComputer;

    /// <summary>The key a canvas folder's marks are kept under: its path as it is, or for a root not already in that form, normalised.</summary>
    private static string KeyOf(Models.NestedFolder folder) =>
        IsRoot(folder) && !IsKeyForm(folder.FullPath) ? Key(folder.FullPath) : folder.FullPath;

    /// <summary>
    /// Whether normalising <paramref name="path"/> would hand it back as it
    /// is: a full local path - a drive letter, a colon, a backslash - with no
    /// separator at its end unless it is the root, and nothing
    /// <see cref="Models.ViewAllPath.Normalize"/> would change or Windows
    /// would read differently: no forward slashes or doubled separators, no
    /// quotes, environment variables, stream colons or wildcards, no "." or
    /// ".." and no name ending in a dot or a space (Windows trims those), and
    /// no name of a device.  Anything else is normalised the long way.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool IsKeyForm(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != Path.DirectorySeparatorChar)
        {
            return false;
        }

        // The characters first, all at once; then name by name.
        var rest = path.AsSpan(3);
        if (rest.IndexOfAny(UnsafeCharacters) >= 0)
        {
            return false;
        }

        while (rest.Length > 0)
        {
            var end = rest.IndexOf(Path.DirectorySeparatorChar);
            var name = end < 0 ? rest : rest[..end];
            if (name.Length == 0 || name[^1] is '.' or ' ' || IsDeviceName(name))
            {
                return false;
            }

            if (end < 0)
            {
                return true;
            }

            rest = rest[(end + 1)..];
            if (rest.Length == 0)
            {
                // A separator at the end, past the root.
                return false;
            }
        }

        return true;
    }

    /// <summary>What <see cref="IsKeyForm"/> never takes as it is: slashes, variables, quotes, stream colons, wildcards and control characters.</summary>
    private static readonly SearchValues<char> UnsafeCharacters = SearchValues.Create(
        [.. Enumerable.Range(0, 32).Select(code => (char)code), '/', '%', '"', ':', '<', '>', '|', '?', '*']);

    /// <summary>CON, PRN, AUX, NUL, COM1 to 9 and LPT1 to 9, with or without an extension: names Windows gives to devices.</summary>
    private static bool IsDeviceName(ReadOnlySpan<char> name)
    {
        if (name.Length < 3 || (name[0] | 0x20) is not ('c' or 'p' or 'a' or 'n' or 'l'))
        {
            return false;
        }

        var dot = name.IndexOf('.');
        var stem = (dot >= 0 ? name[..dot] : name).TrimEnd(' ');
        return stem.Length switch
        {
            3 => stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase),
            4 => (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && (char.IsAsciiDigit(stem[3]) || stem[3] is '\u00B9' or '\u00B2' or '\u00B3'),
            _ => false
        };
    }

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
        IndexMarkedFolders();
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

            IndexMarkedFolders();
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

        IndexMarkedFolders();
        MarkChanged?.Invoke(key, mark);
    }

    /// <summary>
    /// <see cref="MarkedFolders"/> made again from every mark there is.
    /// Under a lock, so two marks set at once on two threads cannot publish a
    /// set that lacks one of them: the second to take the lock sees both.
    /// </summary>
    private void IndexMarkedFolders()
    {
        lock (_markedFoldersGate)
        {
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _marks)
            {
                if (!pair.Value.IsEmpty && Path.GetDirectoryName(pair.Key) is { Length: > 0 } parent)
                {
                    folders.Add(parent);
                }
            }

            _markedFolders = folders;
        }
    }

    private static string Key(string path)
    {
        Interlocked.Increment(ref _pathsNormalised);
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
