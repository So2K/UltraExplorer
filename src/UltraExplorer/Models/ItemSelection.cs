namespace UltraExplorer.Models;

/// <summary>Where a change of the selection was made, so each view can tell its own changes from everyone else's.</summary>
public enum SelectionSource
{
    /// <summary>The nested canvas: a click, a range, a rectangle drawn with the mouse, a key.</summary>
    Canvas,

    /// <summary>The folder list beside the canvas.</summary>
    List,

    /// <summary>The tree canvas's own selection, its rubber band included.</summary>
    Tree,

    /// <summary>A command that took items out: a delete, a move, a refresh that found them gone.</summary>
    Command,

    /// <summary>Going somewhere: the address bar, the sidebar, back and forward, a search result.</summary>
    Navigation
}

/// <summary>One selected file or folder: its path, and what the status bar says about it.</summary>
public readonly record struct SelectionItem(string Path, bool IsDirectory, long Size);

/// <summary>
/// One change of the selection, the whole of one gesture: a click, a range,
/// a rectangle let go of, Ctrl+A, Esc.  Everything a view needs to follow is
/// in it, so a gesture that picks ten thousand files is one edit and one
/// notification rather than ten thousand.
/// </summary>
public sealed class SelectionEdit
{
    /// <summary>Everything selected before goes first: a replacement rather than an addition.</summary>
    public bool Clear { get; init; }

    /// <summary>
    /// The folder every added and removed item is directly inside, when the
    /// gesture that made the edit knows it - a rectangle, a range and Ctrl+A
    /// always do.  Null to have each item's folder read off its path.
    /// </summary>
    public string? Container { get; init; }

    public IReadOnlyList<SelectionItem> Added { get; init; } = [];

    public IReadOnlyList<string> Removed { get; init; } = [];

    /// <summary>The item a Shift range is measured from, or null to leave it as it is.</summary>
    public string? Anchor { get; init; }

    /// <summary>The item with the keyboard focus - the one the address bar names - or null to leave it as it is.</summary>
    public string? Focus { get; init; }

    /// <summary>
    /// Whether moving the focus is going somewhere, for back and forward: a
    /// single click or a navigation is, sweeping out a range or a rectangle is
    /// not - a Shift+arrow sweep would otherwise be one history entry a key.
    /// </summary>
    public bool RecordsNavigation { get; init; }

    public SelectionSource Source { get; init; }
}

/// <summary>
/// The one selection the whole window shares: the nested canvas, the tree
/// canvas, the folder list and every command read it and change it, so
/// whatever is picked in one of them is picked in all, and Copy, Delete or a
/// drag act on exactly what the user sees lit.
///
/// <para>It is a set of paths rather than of the tree's nodes.  A rectangle
/// over a folder of ten thousand files picks ten thousand paths the tree has
/// no nodes for, and making a node per file - a trip to the disk each, and a
/// change of the whole graph each - is what made such a selection impossible
/// before.  Only the focus, the one item the address bar and the status bar
/// talk about, is given a node, by the view model.</para>
///
/// <para>Every change is one <see cref="Apply"/> - one pass over what it
/// adds and removes, one new <see cref="Version"/>, one
/// <see cref="Changed"/> - and the totals the status bar shows are kept as it
/// goes, so nothing that listens ever walks the whole set to answer a
/// question about it.  The folder each item is in is counted too, which is
/// how <see cref="Container"/> knows, without looking, whether everything
/// selected sits in one folder.</para>
/// </summary>
public sealed class ItemSelection
{
    private Dictionary<string, (bool IsDirectory, long Size)> _items = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, int> _perParent = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string>? _paths;
    private string? _anchor;
    private string? _focus;

    public int Count => _items.Count;

    public int FolderCount { get; private set; }

    public int FileCount => _items.Count - FolderCount;

    /// <summary>The selected files' sizes added up; folders count for nothing, as in Explorer.</summary>
    public long TotalBytes { get; private set; }

    /// <summary>
    /// The folder everything selected is directly inside, or null when the
    /// selection is empty, spread over several folders, or made of drives.
    /// Paste and New folder go here while more than one item is selected, as
    /// they go to the folder an Explorer window shows.
    /// </summary>
    public string? Container
    {
        get
        {
            if (_perParent.Count != 1)
            {
                return null;
            }

            foreach (var parent in _perParent.Keys)
            {
                return parent.Length > 0 ? parent : null;
            }

            return null;
        }
    }

    /// <summary>The item Shift ranges are measured from.  Kept when the selection is cleared.</summary>
    public string? Anchor => _anchor;

    /// <summary>The item with the focus.  Kept when the selection is cleared, as Explorer keeps its caret.</summary>
    public string? Focus => _focus;

    /// <summary>Bumped by every change; a view that loaded this version has nothing to catch up on.</summary>
    public long Version { get; private set; }

    /// <summary>Where the last change came from.</summary>
    public SelectionSource LastSource { get; private set; }

    /// <summary>Whether the last change moved the focus as a navigation (see <see cref="SelectionEdit.RecordsNavigation"/>).</summary>
    public bool LastRecordsNavigation { get; private set; }

    /// <summary>
    /// The most items the selection may hold.  A file dialog that returns one
    /// file sets 1, and a change that would pick more keeps the focus alone.
    /// </summary>
    public int MaxCount { get; set; } = int.MaxValue;

    /// <summary>
    /// Every selected path, made once per <see cref="Version"/>: the same list
    /// until the next change, however often a command or a view asks.
    /// </summary>
    public IReadOnlyList<string> Paths => _paths ??= [.. _items.Keys];

    /// <summary>Every selected item with what it is, for a view resolving them all at once.</summary>
    public IEnumerable<SelectionItem> Items
    {
        get
        {
            foreach (var (path, entry) in _items)
            {
                yield return new SelectionItem(path, entry.IsDirectory, entry.Size);
            }
        }
    }

    /// <summary>One change, whole.  Raised on the thread that made it.</summary>
    public event Action<ItemSelection>? Changed;

    public bool Contains(string path) => !string.IsNullOrEmpty(path) && _items.ContainsKey(path);

    public bool TryGetItem(string path, out SelectionItem item)
    {
        if (!string.IsNullOrEmpty(path) && _items.TryGetValue(path, out var entry))
        {
            item = new SelectionItem(path, entry.IsDirectory, entry.Size);
            return true;
        }

        item = default;
        return false;
    }

    /// <summary>How many selected items are directly inside <paramref name="folder"/>.</summary>
    public int CountIn(string folder) => _perParent.TryGetValue(folder, out var count) ? count : 0;

    /// <summary>
    /// Makes one change: what the edit clears, removes and adds, and where it
    /// puts the anchor and the focus.  The cost is what the edit names, never
    /// what was selected before - a replacement drops the old sets whole
    /// rather than emptying them - and an edit that changes nothing is not
    /// announced at all.
    /// </summary>
    public void Apply(SelectionEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var changed = false;
        var skipAdded = false;
        if (edit.Clear && _items.Count > 0)
        {
            if (edit.Removed.Count == 0 && IsExactly(edit.Added))
            {
                // The same items again - a view answering with what it was
                // just shown.  Nothing to redo.
                skipAdded = true;
            }
            else
            {
                _items = new Dictionary<string, (bool IsDirectory, long Size)>(Math.Max(edit.Added.Count, 4), StringComparer.OrdinalIgnoreCase);
                _perParent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                FolderCount = 0;
                TotalBytes = 0;
                changed = true;
            }
        }

        // With the folder named, its count moves once for the whole edit.
        var inContainer = 0;
        foreach (var path in edit.Removed)
        {
            if (!string.IsNullOrEmpty(path) && _items.Remove(path, out var entry))
            {
                Account(entry.IsDirectory, entry.Size, -1);
                if (edit.Container is null)
                {
                    CountParent(path, -1);
                }
                else
                {
                    inContainer--;
                }

                changed = true;
            }
        }

        if (!skipAdded)
        {
            foreach (var item in edit.Added)
            {
                if (string.IsNullOrEmpty(item.Path))
                {
                    continue;
                }

                var size = item.IsDirectory ? 0 : Math.Max(0, item.Size);
                if (_items.TryAdd(item.Path, (item.IsDirectory, size)))
                {
                    Account(item.IsDirectory, size, 1);
                    if (edit.Container is null)
                    {
                        CountParent(item.Path, 1);
                    }
                    else
                    {
                        inContainer++;
                    }

                    changed = true;
                }
            }
        }

        if (edit.Container is { } container && inContainer != 0)
        {
            Bump(container, inContainer);
        }

        if (edit.Anchor is { } anchor && !string.Equals(anchor, _anchor, StringComparison.OrdinalIgnoreCase))
        {
            _anchor = anchor;
            changed = true;
        }

        if (edit.Focus is { } focus && !string.Equals(focus, _focus, StringComparison.OrdinalIgnoreCase))
        {
            _focus = focus;
            changed = true;
        }

        if (_items.Count > MaxCount)
        {
            KeepOnly(edit);
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        Version++;
        LastSource = edit.Source;
        LastRecordsNavigation = edit.RecordsNavigation;
        _paths = null;
        Changed?.Invoke(this);
    }

    /// <summary>One item alone, anchor and focus on it: a plain click, or going somewhere.</summary>
    public void ReplaceSingle(string path, bool isDirectory, long size, SelectionSource source) =>
        Apply(new SelectionEdit
        {
            Clear = true,
            Added = [new SelectionItem(path, isDirectory, size)],
            Anchor = path,
            Focus = path,
            RecordsNavigation = true,
            Source = source
        });

    public void Remove(IEnumerable<string> paths, SelectionSource source)
    {
        var removed = paths as IReadOnlyList<string> ?? [.. paths];
        if (removed.Count > 0 && _items.Count > 0)
        {
            Apply(new SelectionEdit { Removed = removed, Source = source });
        }
    }

    /// <summary>
    /// Drops every item directly inside <paramref name="folder"/> that
    /// <paramref name="exists"/> says is no longer there - after the folder
    /// was read again - in one change.  Returns how many went.
    /// </summary>
    public int RemoveMissingUnder(string folder, Func<string, bool> exists)
    {
        if (CountIn(folder) == 0)
        {
            return 0;
        }

        List<string>? missing = null;
        foreach (var path in _items.Keys)
        {
            if (ParentOf(path).Equals(folder.AsSpan(), StringComparison.OrdinalIgnoreCase) && !exists(path))
            {
                (missing ??= []).Add(path);
            }
        }

        if (missing is null)
        {
            return 0;
        }

        Apply(new SelectionEdit { Removed = missing, Source = SelectionSource.Command });
        return missing.Count;
    }

    /// <summary>Nothing selected; the focus and the anchor stay where they were.</summary>
    public void Clear(SelectionSource source)
    {
        if (_items.Count > 0)
        {
            Apply(new SelectionEdit { Clear = true, Source = source });
        }
    }

    /// <summary>
    /// The folder a path is directly inside, as a slice of the path itself -
    /// nothing is allocated to find it.  A drive's folder is empty: This PC.
    /// </summary>
    public static ReadOnlySpan<char> ParentOf(ReadOnlySpan<char> path)
    {
        if (path.Length > 3 && (path[^1] == '\\' || path[^1] == '/'))
        {
            path = path[..^1];
        }

        var cut = path.LastIndexOfAny('\\', '/');
        if (cut < 0)
        {
            return [];
        }

        if (cut == 2 && path.Length > 1 && path[1] == ':')
        {
            // "C:\Windows" is in "C:\", with its separator; "C:\" is in This PC.
            return path.Length == 3 ? [] : path[..3];
        }

        if (cut < 2 && path.StartsWith(@"\\"))
        {
            return [];
        }

        return path[..cut];
    }

    private bool IsExactly(IReadOnlyList<SelectionItem> items)
    {
        if (items.Count != _items.Count)
        {
            return false;
        }

        foreach (var item in items)
        {
            if (!_items.ContainsKey(item.Path))
            {
                return false;
            }
        }

        return true;
    }

    private void Account(bool isDirectory, long size, int sign)
    {
        if (isDirectory)
        {
            FolderCount += sign;
        }
        else
        {
            TotalBytes += sign * size;
        }
    }

    private void CountParent(string path, int delta)
    {
        var lookup = _perParent.GetAlternateLookup<ReadOnlySpan<char>>();
        var parent = ParentOf(path);
        if (lookup.TryGetValue(parent, out var count))
        {
            if (count + delta <= 0)
            {
                lookup.Remove(parent);
            }
            else
            {
                lookup[parent] = count + delta;
            }
        }
        else if (delta > 0)
        {
            lookup[parent] = delta;
        }
    }

    private void Bump(string parent, int delta)
    {
        if (_perParent.TryGetValue(parent, out var count))
        {
            if (count + delta <= 0)
            {
                _perParent.Remove(parent);
            }
            else
            {
                _perParent[parent] = count + delta;
            }
        }
        else if (delta > 0)
        {
            _perParent[parent] = delta;
        }
    }

    /// <summary>
    /// Too many for <see cref="MaxCount"/>: the focus stays if it is among
    /// them, otherwise the last item the edit added, otherwise any one.
    /// </summary>
    private void KeepOnly(SelectionEdit edit)
    {
        string? keep = null;
        if (_focus is not null && _items.ContainsKey(_focus))
        {
            keep = _focus;
        }
        else
        {
            for (var index = edit.Added.Count - 1; index >= 0 && keep is null; index--)
            {
                if (_items.ContainsKey(edit.Added[index].Path))
                {
                    keep = edit.Added[index].Path;
                }
            }
        }

        keep ??= _items.Keys.First();
        var entry = _items[keep];
        _items = new Dictionary<string, (bool IsDirectory, long Size)>(StringComparer.OrdinalIgnoreCase) { [keep] = entry };
        _perParent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        FolderCount = 0;
        TotalBytes = 0;
        Account(entry.IsDirectory, entry.Size, 1);
        CountParent(keep, 1);
        _focus = keep;
        _anchor = keep;
    }
}
