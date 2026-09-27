using UltraExplorer.Models;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

/// <summary>
/// One item on the nested canvas, held by what it is rather than by where it
/// is: a sub-folder by its object, a file by its folder and its name.  An
/// order changed since, or the folder read again, moves the item's tile, not
/// the item - which is why a Shift range after a sort runs in the new order.
/// </summary>
/// <param name="Container">The folder the item is directly inside.</param>
/// <param name="Folder">The item when it is a folder, else null.</param>
/// <param name="FileName">The item's name when it is a file, else null.</param>
internal readonly record struct NestedItemKey(NestedFolder Container, NestedFolder? Folder, string? FileName)
{
    public bool IsFile => FileName is not null;

    public static NestedItemKey OfFolder(NestedFolder folder) => new(folder.Parent!, folder, null);

    public static NestedItemKey OfFile(NestedFolder container, string name) => new(container, null, name);

    /// <summary>The item's path; made on every call, so asked for once per item a gesture hands on.</summary>
    public string Path => FileName is null ? Folder!.FullPath : System.IO.Path.Combine(Container.FullPath, FileName);
}

/// <summary>
/// The files selected in one folder, as one bit per tile in the order the
/// folder shows them - the order of <see cref="FilesRef"/>, the list the bits
/// were set against.  Whether a tile on screen is selected is then one bit
/// test, and ten thousand selected files are a hundred and fifty-seven words.
/// </summary>
internal sealed class FileSet(IReadOnlyList<NestedFile> files)
{
    public IReadOnlyList<NestedFile> FilesRef { get; set; } = files;

    public ulong[] Bits { get; set; } = new ulong[(files.Count + 63) >> 6];

    public int Count { get; set; }

    public bool this[int index] =>
        (uint)index < (uint)FilesRef.Count && (Bits[index >> 6] & (1UL << index)) != 0;

    /// <summary>Sets or clears one tile's bit; true when that changed it.</summary>
    public bool Set(int index, bool selected)
    {
        if ((uint)index >= (uint)FilesRef.Count)
        {
            return false;
        }

        ref var word = ref Bits[index >> 6];
        var mask = 1UL << index;
        if ((word & mask) != 0 == selected)
        {
            return false;
        }

        word ^= mask;
        Count += selected ? 1 : -1;
        return true;
    }
}

/// <summary>
/// The window's selection (<see cref="ItemSelection"/>) as the nested canvas
/// holds it: resolved from paths to the folders and tiles it draws, once, so
/// a frame never looks a path up.  Sub-folders are held by object, files as
/// bits per folder in the order on screen; drawing asks one folder at a time
/// and only for what is on screen, so a frame with ten thousand selected
/// files costs what is visible, not what is selected.
///
/// <para>The canvas is the authority for its own gestures: a click or a
/// rectangle changes this first, draws it, and hands the same change to the
/// window as one edit in the same call.  Everything else - the list, the
/// address bar, a command - reaches it through <see cref="Load"/>, which does
/// nothing for a version it already has.</para>
///
/// <para>Paths in folders the tree has not read wait by folder until it
/// has (<see cref="ResolvePending"/>).  A folder sorted or read again since
/// its bits were set is caught up the first time they are asked for: the
/// names they stood for are found again in the new order, and names that are
/// gone drop out and are reported, so the window drops them too.  Nothing
/// here ever places a folder for the current order - that is the drawing's
/// business and its allowance's - so the bits always describe the tiles as
/// the picture shows them.</para>
/// </summary>
internal sealed class NestedSelection
{
    private readonly HashSet<NestedFolder> _folders = [];
    private readonly Dictionary<NestedFolder, int> _foldersPerContainer = [];
    private readonly Dictionary<NestedFolder, FileSet> _files = [];
    private readonly Dictionary<string, List<string>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<NestedFolder> _containers = [];
    private IReadOnlyList<NestedFile>? _indexedFiles;
    private Dictionary<string, int>? _nameIndex;
    private int _fileCount;

    /// <summary>The item Shift ranges are measured from.</summary>
    public NestedItemKey? Anchor { get; set; }

    /// <summary>The item with the focus: the one arrows move from and the focus ring is drawn on.</summary>
    public NestedItemKey? Active { get; set; }

    /// <summary>
    /// The folder Ctrl+A selects everything in: the one a rectangle was last
    /// drawn in, an item was last clicked in, a body was last clicked, or the
    /// keys last moved in.
    /// </summary>
    public NestedFolder? CurrentFolder { get; set; }

    /// <summary>Bumped by every change to what is selected here, for the layer that draws it.</summary>
    public long Version { get; private set; }

    /// <summary>The <see cref="ItemSelection.Version"/> this holds, loaded or handed over; an echo of it is ignored.</summary>
    public long LoadedVersion { get; set; } = -1;

    public int Count => _folders.Count + _fileCount;

    public bool IsEmpty => _folders.Count == 0 && _fileCount == 0;

    /// <summary>Whether anything waits for a folder the tree has not read.</summary>
    public bool HasPending => _pending.Count > 0;

    /// <summary>The paths of the folders something waits in (see <see cref="ResolvePending"/>).</summary>
    public IEnumerable<string> PendingFolders => _pending.Keys;

    /// <summary>Every folder something selected is directly inside; for drawing, which visits only these.</summary>
    public IReadOnlyList<NestedFolder> Containers
    {
        get
        {
            _containers.Clear();
            foreach (var container in _foldersPerContainer.Keys)
            {
                _containers.Add(container);
            }

            foreach (var (container, set) in _files)
            {
                if (set.Count > 0 && !_foldersPerContainer.ContainsKey(container))
                {
                    _containers.Add(container);
                }
            }

            return _containers;
        }
    }

    public bool IsSelected(NestedFolder child) => _folders.Contains(child);

    /// <summary>How many of <paramref name="container"/>'s sub-folders are selected.</summary>
    public int FoldersIn(NestedFolder container) => _foldersPerContainer.TryGetValue(container, out var count) ? count : 0;

    /// <summary>Whether an item is selected, found by what it is.</summary>
    public bool Contains(in NestedItemKey key)
    {
        if (key.Folder is { } folder)
        {
            return _folders.Contains(folder);
        }

        if (FilesOf(key.Container, null) is not { } set)
        {
            return false;
        }

        var index = IndexOf(key.Container, key.FileName!);
        return index >= 0 && set[index];
    }

    /// <summary>
    /// The selected files of <paramref name="container"/> in the order its
    /// tiles are placed now, or null when none are selected.  Caught up here,
    /// once, when the folder was sorted or read again since the bits were
    /// set; the paths of files that are gone are added to
    /// <paramref name="vanished"/>, and a folder that is gone takes its
    /// selection with it.
    /// </summary>
    public FileSet? FilesOf(NestedFolder container, List<string>? vanished)
    {
        if (!_files.TryGetValue(container, out var set))
        {
            return null;
        }

        if (NestedTree.IsDetached(container))
        {
            if (vanished is not null)
            {
                for (var index = 0; index < set.FilesRef.Count; index++)
                {
                    if (set[index])
                    {
                        vanished.Add(container.PathOf(set.FilesRef[index]));
                    }
                }
            }

            _fileCount -= set.Count;
            _files.Remove(container);
            Version++;
            return null;
        }

        if (!ReferenceEquals(set.FilesRef, container.Files))
        {
            Remap(container, set, vanished);
            if (set.Count == 0)
            {
                _files.Remove(container);
                return null;
            }
        }

        return set.Count > 0 ? set : null;
    }

    /// <summary>
    /// Drops selected folders that a refresh took away, adding their paths to
    /// <paramref name="vanished"/>.  Only worth a look when the tree changed.
    /// </summary>
    public void DropDetachedFolders(List<string> vanished)
    {
        if (_folders.Count == 0)
        {
            return;
        }

        List<NestedFolder>? gone = null;
        foreach (var folder in _folders)
        {
            if (NestedTree.IsDetached(folder))
            {
                (gone ??= []).Add(folder);
            }
        }

        if (gone is null)
        {
            return;
        }

        foreach (var folder in gone)
        {
            vanished.Add(folder.FullPath);
            SetFolder(folder, false);
        }
    }

    /// <summary>A file's tile among <paramref name="container"/>'s files as placed now, by name, or -1.</summary>
    public int IndexOf(NestedFolder container, string name) =>
        NameIndex(container.Files).TryGetValue(name, out var found) ? found : -1;

    /// <summary>Nothing selected; anchor and focus kept.</summary>
    public void Clear()
    {
        if (IsEmpty && _pending.Count == 0)
        {
            return;
        }

        _folders.Clear();
        _foldersPerContainer.Clear();
        _files.Clear();
        _pending.Clear();
        _fileCount = 0;
        Version++;
    }

    /// <summary>Takes every item out of one folder, leaving the rest.</summary>
    public void ClearIn(NestedFolder container)
    {
        var changed = false;
        if (_foldersPerContainer.Remove(container))
        {
            _folders.RemoveWhere(folder => ReferenceEquals(folder.Parent, container));
            changed = true;
        }

        if (_files.Remove(container, out var set))
        {
            _fileCount -= set.Count;
            changed = true;
        }

        if (_pending.Remove(container.FullPath))
        {
            changed = true;
        }

        if (changed)
        {
            Version++;
        }
    }

    public bool SetFolder(NestedFolder child, bool selected)
    {
        if (child.Parent is not { } container)
        {
            return false;
        }

        if (selected ? !_folders.Add(child) : !_folders.Remove(child))
        {
            return false;
        }

        var count = FoldersIn(container) + (selected ? 1 : -1);
        if (count > 0)
        {
            _foldersPerContainer[container] = count;
        }
        else
        {
            _foldersPerContainer.Remove(container);
        }

        Version++;
        return true;
    }

    /// <summary>Selects or deselects the file at <paramref name="index"/> among the container's files as placed now.</summary>
    public bool SetFile(NestedFolder container, int index, bool selected)
    {
        if ((uint)index >= (uint)container.Files.Count)
        {
            return false;
        }

        if (!_files.TryGetValue(container, out var set))
        {
            if (!selected)
            {
                return false;
            }

            set = new FileSet(container.Files);
            _files[container] = set;
        }
        else if (!ReferenceEquals(set.FilesRef, container.Files))
        {
            Remap(container, set, null);
        }

        var before = set.Count;
        if (!set.Set(index, selected))
        {
            return false;
        }

        _fileCount += set.Count - before;
        if (set.Count == 0)
        {
            _files.Remove(container);
        }

        Version++;
        return true;
    }

    /// <summary>
    /// The container's file bits, made or caught up with the order its tiles
    /// are placed in now, for a gesture that sets thousands at once - a
    /// rectangle, a range, Ctrl+A - without a lookup per file.  Set bits on it
    /// with <see cref="FileSet.Set"/>, then hand it to <see cref="Settle"/>
    /// with the count it had.
    /// </summary>
    public FileSet FilesFor(NestedFolder container)
    {
        if (!_files.TryGetValue(container, out var set))
        {
            set = new FileSet(container.Files);
            _files[container] = set;
        }
        else if (!ReferenceEquals(set.FilesRef, container.Files))
        {
            Remap(container, set, null);
        }

        return set;
    }

    /// <summary>After bits were set straight on <see cref="FilesFor"/>'s set: the totals and the version follow.</summary>
    public void Settle(NestedFolder container, FileSet set, int countBefore)
    {
        _fileCount += set.Count - countBefore;
        if (set.Count == 0)
        {
            _files.Remove(container);
        }

        Version++;
    }

    public bool Set(in NestedItemKey key, bool selected)
    {
        if (key.Folder is { } folder)
        {
            return SetFolder(folder, selected);
        }

        var index = IndexOf(key.Container, key.FileName!);
        return index >= 0 && SetFile(key.Container, index, selected);
    }

    /// <summary>
    /// Replaces everything with the window's selection, unless this already
    /// holds that version.  Paths are grouped by folder straight off the
    /// strings, each folder is found once, and names are looked up in one
    /// dictionary per folder listing - about a millisecond for ten thousand.
    /// </summary>
    public bool Load(ItemSelection selection, NestedTree? tree)
    {
        if (selection.Version == LoadedVersion)
        {
            return false;
        }

        LoadedVersion = selection.Version;
        _folders.Clear();
        _foldersPerContainer.Clear();
        _files.Clear();
        _pending.Clear();
        _fileCount = 0;
        Anchor = null;
        Active = null;
        Version++;
        if (tree is null)
        {
            return true;
        }

        var parents = new Dictionary<string, NestedFolder?>(StringComparer.OrdinalIgnoreCase);
        var byParent = parents.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (var item in selection.Items)
        {
            var path = item.Path;
            var parentSpan = ItemSelection.ParentOf(path);
            if (!byParent.TryGetValue(parentSpan, out var parent))
            {
                var parentPath = parentSpan.ToString();
                parent = parentPath.Length == 0 ? tree.Root : tree.Find(parentPath);
                parents[parentPath] = parent;
            }

            var name = NameAfter(path, parentSpan.Length);
            if (parent is null || !parent.IsLoaded && !parent.IsComputer)
            {
                Wait(parent?.FullPath ?? parentSpan.ToString(), name.ToString());
                continue;
            }

            if (!Resolve(parent, name, item.IsDirectory))
            {
                Wait(parent.FullPath, name.ToString());
            }
        }

        Anchor = KeyFor(selection.Anchor, tree);
        Active = KeyFor(selection.Focus, tree);
        return true;
    }

    /// <summary>
    /// A folder was read: what waited for it is selected now.  True when
    /// anything was, and the layer has something new to draw.
    /// </summary>
    public bool ResolvePending(NestedFolder folder)
    {
        if (_pending.Count == 0 || !_pending.Remove(folder.FullPath, out var names))
        {
            return false;
        }

        var any = false;
        List<string>? still = null;
        foreach (var name in names)
        {
            if (Resolve(folder, name, isDirectory: false) || Resolve(folder, name, isDirectory: true))
            {
                any = true;
            }
            else
            {
                (still ??= []).Add(name);
            }
        }

        if (still is not null)
        {
            // Not shown - a hidden file while hidden items are hidden - but
            // still selected in the window; tried again when it may be.
            _pending[folder.FullPath] = still;
        }

        return any;
    }

    /// <summary>The item a path names, when its folder has been read and the item is there.</summary>
    public NestedItemKey? KeyFor(string? path, NestedTree tree)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var parentSpan = ItemSelection.ParentOf(path);
        var parent = parentSpan.Length == 0 ? tree.Root : tree.Find(parentSpan.ToString());
        if (parent is null)
        {
            return null;
        }

        var name = NameAfter(path, parentSpan.Length).ToString();
        if (NestedTree.FindChild(parent, name) is { IsComputer: false } folder)
        {
            return NestedItemKey.OfFolder(folder);
        }

        var tile = IndexOf(parent, name);
        return tile >= 0 ? NestedItemKey.OfFile(parent, parent.Files[tile].Name) : null;
    }

    /// <summary>Every item selected here, for a gesture that has to name them all.</summary>
    public IEnumerable<NestedItemKey> Items()
    {
        foreach (var folder in _folders)
        {
            yield return NestedItemKey.OfFolder(folder);
        }

        foreach (var (container, set) in _files)
        {
            for (var index = 0; index < set.FilesRef.Count; index++)
            {
                if (set[index])
                {
                    yield return NestedItemKey.OfFile(container, set.FilesRef[index].Name);
                }
            }
        }
    }

    private bool Resolve(NestedFolder parent, ReadOnlySpan<char> name, bool isDirectory)
    {
        if (isDirectory)
        {
            if (NestedTree.FindChild(parent, name.ToString()) is { IsComputer: false } folder)
            {
                SetFolder(folder, true);
                return true;
            }

            return false;
        }

        if (NameIndex(parent.Files).GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out var tile))
        {
            SetFile(parent, tile, true);
            return true;
        }

        return false;
    }

    private void Wait(string parent, string name)
    {
        if (!_pending.TryGetValue(parent, out var names))
        {
            names = [];
            _pending[parent] = names;
        }

        names.Add(name);
    }

    /// <summary>The name part of <paramref name="path"/> after its folder's <paramref name="parentLength"/> characters.</summary>
    private static ReadOnlySpan<char> NameAfter(string path, int parentLength)
    {
        var name = path.AsSpan(parentLength);
        while (name.Length > 0 && (name[0] == '\\' || name[0] == '/'))
        {
            name = name[1..];
        }

        while (name.Length > 1 && (name[^1] == '\\' || name[^1] == '/') && !(name.Length == 3 && name[1] == ':'))
        {
            name = name[..^1];
        }

        return name;
    }

    /// <summary>
    /// The bits of a folder sorted or read again since they were set, moved
    /// to where the same names are now, through one dictionary of the new
    /// list built once and kept while that list is the one asked about.
    /// A name no longer shown but still in the listing - a hidden file while
    /// hidden items are hidden - waits to be shown again; one not in the
    /// listing at all is gone.
    /// </summary>
    private void Remap(NestedFolder container, FileSet set, List<string>? vanished)
    {
        var old = set.FilesRef;
        var oldBits = set.Bits;
        var current = container.Files;
        var index = NameIndex(current);
        var bits = new ulong[(current.Count + 63) >> 6];
        var count = 0;
        HashSet<string>? listed = null;
        for (var word = 0; word < oldBits.Length; word++)
        {
            var remaining = oldBits[word];
            while (remaining != 0)
            {
                var at = (word << 6) + System.Numerics.BitOperations.TrailingZeroCount(remaining);
                remaining &= remaining - 1;
                if (at >= old.Count)
                {
                    break;
                }

                var name = old[at].Name;
                if (index.TryGetValue(name, out var tile))
                {
                    if ((bits[tile >> 6] & (1UL << tile)) == 0)
                    {
                        bits[tile >> 6] |= 1UL << tile;
                        count++;
                    }

                    continue;
                }

                listed ??= new HashSet<string>(container.AllFiles.Select(file => file.Name), StringComparer.OrdinalIgnoreCase);
                if (listed.Contains(name))
                {
                    Wait(container.FullPath, name);
                }
                else
                {
                    vanished?.Add(container.PathOf(old[at]));
                }
            }
        }

        _fileCount += count - set.Count;
        set.FilesRef = current;
        set.Bits = bits;
        set.Count = count;
        Version++;

        // Hidden items shown again bring back what waited for them.
        if (_pending.Count > 0 && _pending.ContainsKey(container.FullPath))
        {
            ResolvePending(container);
        }
    }

    /// <summary>Name to tile for one listing, built once per list and kept while it is the one asked about.</summary>
    private Dictionary<string, int> NameIndex(IReadOnlyList<NestedFile> files)
    {
        if (ReferenceEquals(files, _indexedFiles) && _nameIndex is not null)
        {
            return _nameIndex;
        }

        var index = new Dictionary<string, int>(files.Count, StringComparer.OrdinalIgnoreCase);
        for (var tile = 0; tile < files.Count; tile++)
        {
            index.TryAdd(files[tile].Name, tile);
        }

        _indexedFiles = files;
        _nameIndex = index;
        return index;
    }
}
