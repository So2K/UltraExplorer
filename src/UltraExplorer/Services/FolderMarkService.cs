using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media;
using UltraExplorer.Infrastructure;

namespace UltraExplorer.Services;

/// <summary>
/// A colour label and a note, both optional.  <paramref name="IsDirectory"/>
/// is nullable for state written before Tags existed; once an item is seen,
/// its kind is kept beside the mark so a cold start never probes a dead share
/// merely to decide whether the sidebar should list it.
/// </summary>
public sealed record FolderMark(string AccentHex = "", string Note = "", bool? IsDirectory = null)
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

    // The folders that hold a mark, rebuilt whole the first time they are
    // asked for after a change, not at every change - marks change a few
    // times a session, or thousands at once when a large selection is
    // coloured, and are asked about thousands of times a frame - and handed
    // out as a set nobody changes once it is published, so the canvas reads
    // it on the UI thread without a lock while a mark is set on another.
    private readonly Lock _markedFoldersGate = new();
    private volatile HashSet<string> _markedFolders = new(StringComparer.OrdinalIgnoreCase);
    private int _markedFoldersStale;
    private static long _pathsNormalised;

    /// <summary>
    /// What this copy changed since it last saved: the halves of each mark,
    /// by key.  Other copies of the app - another window's process, a
    /// replaced dialog, the prepared picker - keep the same file and save it
    /// too, so a save writes only these over the file as it is then, never
    /// the whole set this copy read at its start: that set would put back
    /// marks cleared elsewhere since, and erase every one made there.
    /// </summary>
    private readonly Dictionary<string, MarkHalves> _changed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _changedGate = new();

    /// <summary>
    /// One save at a time.  The debounced save and the one made on closing can
    /// overlap, and the one that started with the older snapshot must not be
    /// the one that lands last.  A reading of the file waits its turn too
    /// (see <see cref="LoadAsync"/>).
    /// </summary>
    private readonly SemaphoreSlim _saving = new(1, 1);

    /// <summary>The file's turn among every copy of the app: the same name in every process for the same file.</summary>
    private readonly string _turnName;

    public FolderMarkService(string? statePath = null)
    {
        StatePath = statePath ?? AppPaths.State("folder-marks.json");
        _turnName = @"Local\UltraExplorer.FolderMarks." + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(StatePath.ToUpperInvariant())));
    }

    [Flags]
    private enum MarkHalves
    {
        Accent = 1,
        Note = 2,
        Kind = 4,
        Both = Accent | Note | Kind
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
    public IReadOnlySet<string> MarkedFolders => CurrentMarkedFolders();

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
        var marked = CurrentMarkedFolders();
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
        => SetAccent(path, accentHex, isDirectory: null);

    public void SetAccent(string path, string? accentHex, bool? isDirectory)
    {
        var key = Key(path);
        var current = Get(key);
        var updated = current with
        {
            AccentHex = accentHex ?? string.Empty,
            IsDirectory = isDirectory ?? current.IsDirectory
        };
        var halves = MarkHalves.Accent | (isDirectory.HasValue ? MarkHalves.Kind : 0);
        Store(key, updated, halves);
    }

    /// <summary>
    /// Records the kind discovered for an existing colour/note without
    /// changing either. Empty paths are not retained solely for metadata.
    /// </summary>
    public bool SetItemKind(string path, bool isDirectory)
    {
        var key = Key(path);
        var current = Get(key);
        if (current.IsEmpty || current.IsDirectory == isDirectory)
        {
            return false;
        }

        Store(key, current with { IsDirectory = isDirectory }, MarkHalves.Kind);
        return true;
    }

    public void SetNote(string path, string? note)
    {
        var key = Key(path);
        var current = Get(key);
        var updated = current with { Note = note ?? string.Empty };
        Store(key, updated, MarkHalves.Note);
    }

    /// <summary>Seeds a mark without raising a change (used by workspace migration).</summary>
    public void Seed(string path, string accentHex, string note)
    {
        var mark = new FolderMark(accentHex ?? string.Empty, note ?? string.Empty);
        if (mark.IsEmpty)
        {
            return;
        }

        // Saved like any change of this copy's: the old workspace it came
        // from no longer carries it once the window has saved.
        var key = Key(path);
        lock (_changedGate)
        {
            _marks[key] = mark;
            _changed[key] = MarkHalves.Accent | MarkHalves.Note;
        }

        MarkFoldersStale();
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // In a save's turn: a save has taken its changes before it writes
        // them, and a file read in between holds neither them nor anything
        // left to lay over it - the mark would be cleared here, and that
        // cleared mark is what the save would then write.
        StateFiles.SweepTemporaries(StatePath, hidden: true);
        await _saving.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(StatePath))
            {
                return;
            }

            Dictionary<string, FolderMark>? stored;
            try
            {
                await using var stream = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16 * 1024, useAsync: true);
                stored = await JsonSerializer.DeserializeAsync<Dictionary<string, FolderMark>>(stream, JsonOptions, cancellationToken);
            }
            catch (JsonException)
            {
                // A damaged marks file must never stop the app from opening - but
                // a save written over it would leave every note in it gone for
                // good.  It is set aside instead, where it can still be recovered
                // by hand.
                StateFiles.Quarantine(StatePath);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A marks file that cannot be read must never stop the app from opening.
                return;
            }

            if (stored is null)
            {
                return;
            }

            // Read again - a prepared picker bound to a new dialog - the marks are
            // the file's as it is now, with this copy's own changes not saved yet
            // laid over them: a mark cleared elsewhere since goes, and one made
            // here and not written yet stays.
            var loaded = Complete(stored);
            lock (_changedGate)
            {
                foreach (var (key, halves) in _changed)
                {
                    Apply(loaded, key, halves);
                }

                foreach (var key in _marks.Keys)
                {
                    if (!loaded.ContainsKey(key))
                    {
                        _marks.TryRemove(key, out _);
                    }
                }

                foreach (var (key, mark) in loaded)
                {
                    _marks[key] = mark;
                }
            }

            MarkFoldersStale();
        }
        finally
        {
            _saving.Release();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        // One save at a time, and the one started last is the one written last:
        // it takes its changes only once the one before has finished.
        await _saving.WaitAsync(cancellationToken);
        try
        {
            var changes = TakeChanges();
            if (changes.Count == 0)
            {
                // Nothing of this copy's to write: what the file holds is
                // another's, and newer than what this copy read.
                return;
            }

            // On a thread of its own: the file's turn is a mutex, which
            // belongs to the thread that took it, and the reading, merging and
            // writing all happen while it is held.  A save that could not be
            // written leaves its changes to the next.
            if (!await Task.Run(() => WriteChanges(changes), CancellationToken.None))
            {
                PutBack(changes);
            }
        }
        finally
        {
            _saving.Release();
        }
    }

    /// <summary>
    /// A file's marks as they are kept: a file edited by hand can hold a null
    /// for a mark, or for either half of one, which counts as no mark rather
    /// than a crash on the way in; and a path not in the form keys are kept
    /// in is normalised to it.
    /// </summary>
    private static Dictionary<string, FolderMark> Complete(Dictionary<string, FolderMark> stored)
    {
        var marks = new Dictionary<string, FolderMark>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, mark) in stored)
        {
            if (string.IsNullOrWhiteSpace(path) || mark is null)
            {
                continue;
            }

            var complete = new FolderMark(mark.AccentHex ?? string.Empty, mark.Note ?? string.Empty, mark.IsDirectory);
            if (!complete.IsEmpty)
            {
                marks[IsKeyForm(path) ? path : Key(path)] = complete;
            }
        }

        return marks;
    }

    /// <summary>
    /// Lays this copy's mark of <paramref name="key"/> over
    /// <paramref name="marks"/>, only the halves it changed: a note written
    /// in another copy stays beside a colour chosen in this one.
    /// </summary>
    private void Apply(Dictionary<string, FolderMark> marks, string key, MarkHalves halves)
    {
        var stored = marks.GetValueOrDefault(key) ?? FolderMark.None;
        var here = _marks.TryGetValue(key, out var mark) ? mark : FolderMark.None;
        var merged = new FolderMark(
            (halves & MarkHalves.Accent) != 0 ? here.AccentHex : stored.AccentHex,
            (halves & MarkHalves.Note) != 0 ? here.Note : stored.Note,
            (halves & MarkHalves.Kind) != 0 ? here.IsDirectory : stored.IsDirectory);
        if (merged.IsEmpty)
        {
            marks.Remove(key);
        }
        else
        {
            marks[key] = merged;
        }
    }

    private Dictionary<string, MarkHalves> TakeChanges()
    {
        lock (_changedGate)
        {
            var changes = new Dictionary<string, MarkHalves>(_changed, StringComparer.OrdinalIgnoreCase);
            _changed.Clear();
            return changes;
        }
    }

    private void PutBack(Dictionary<string, MarkHalves> changes)
    {
        lock (_changedGate)
        {
            foreach (var (key, halves) in changes)
            {
                _changed[key] = _changed.GetValueOrDefault(key) | halves;
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="changes"/> over the file as it is now, in the
    /// file's turn among every copy of the app: read, merged and replaced
    /// while no other copy does the same, so neither writes back the file as
    /// it was before the other's save.  False when it could not be written.
    /// </summary>
    private bool WriteChanges(Dictionary<string, MarkHalves> changes)
    {
        Mutex? turn = null;
        var entered = false;
        try
        {
            // A copy that hangs while it holds the turn is not waited for
            // long, and one made by an elevated copy cannot be opened from
            // here: either way the changes still go over the file as it is
            // now, which only a save landing in the same moment could undo.
            try
            {
                turn = new Mutex(false, _turnName);
                entered = turn.WaitOne(TimeSpan.FromSeconds(3));
            }
            catch (AbandonedMutexException) { entered = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { }

            var marks = ReadForMerge();
            foreach (var (key, halves) in changes)
            {
                Apply(marks, key, halves);
            }

            return Write(marks);
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

    /// <summary>
    /// The marks the file holds now, for this copy's changes to go over.  A
    /// file that is not there holds none.  One that cannot be understood is
    /// set aside, as at the start (see <see cref="LoadAsync"/>), and one that
    /// cannot be read leaves what this copy has: its own marks, which is all
    /// a save ever wrote before.
    /// </summary>
    private Dictionary<string, FolderMark> ReadForMerge()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return new Dictionary<string, FolderMark>(StringComparer.OrdinalIgnoreCase);
            }

            using var stream = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024);
            if (JsonSerializer.Deserialize<Dictionary<string, FolderMark>>(stream, JsonOptions) is { } stored)
            {
                return Complete(stored);
            }
        }
        catch (JsonException)
        {
            StateFiles.Quarantine(StatePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        return _marks.Where(pair => !pair.Value.IsEmpty).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private bool Write(Dictionary<string, FolderMark> marks)
    {
        var directory = Path.GetDirectoryName(StatePath)!;
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(StatePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);

            // Written through to the disk before it replaces the old file: a
            // move that reaches the disk ahead of the data it names leaves an
            // empty or torn file behind after a power cut, where the old one
            // would at least have been whole.
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, marks, JsonOptions);
                stream.Flush();
            }

            File.Move(temporaryPath, StatePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persisting a colour label is never worth surfacing an error for.
            return false;
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

    private void Store(string key, FolderMark mark, MarkHalves halves)
    {
        lock (_changedGate)
        {
            if (mark.IsEmpty)
            {
                _marks.TryRemove(key, out _);
            }
            else
            {
                _marks[key] = mark;
            }

            _changed[key] = _changed.GetValueOrDefault(key) | halves;
        }

        MarkFoldersStale();
        MarkChanged?.Invoke(key, mark);
    }

    /// <summary>
    /// A mark changed: <see cref="MarkedFolders"/> is made again the next time
    /// it is asked for, once for however many changes came before - ten
    /// thousand files coloured at once would otherwise read every mark ten
    /// thousand times.
    /// </summary>
    private void MarkFoldersStale() => Volatile.Write(ref _markedFoldersStale, 1);

    private HashSet<string> CurrentMarkedFolders() =>
        Volatile.Read(ref _markedFoldersStale) == 0 ? _markedFolders : IndexMarkedFolders();

    /// <summary>
    /// <see cref="MarkedFolders"/> made again from every mark there is.
    /// Under a lock, so two threads that ask at once make it once; and the
    /// change it answers is taken before the marks are read, so a mark set on
    /// another thread while they are has it made again next time, never left
    /// out for good.
    /// </summary>
    private HashSet<string> IndexMarkedFolders()
    {
        lock (_markedFoldersGate)
        {
            if (Interlocked.Exchange(ref _markedFoldersStale, 0) == 0)
            {
                return _markedFolders;
            }

            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _marks)
            {
                if (!pair.Value.IsEmpty && Path.GetDirectoryName(pair.Key) is { Length: > 0 } parent)
                {
                    folders.Add(parent);
                }
            }

            _markedFolders = folders;
            return folders;
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
