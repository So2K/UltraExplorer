namespace UltraExplorer.Models;

/// <summary>Whether each folder keeps an order of its own, or every folder shares one.</summary>
public enum SortScope
{
    /// <summary>
    /// A header or a Sort by choice orders the folder it is used on, and only
    /// that one: a folder sorted by date to find the newest download stays
    /// that way, and every other folder keeps the order it had.
    /// </summary>
    PerFolder,

    /// <summary>One order for every folder, as it was before folders had their own.</summary>
    AllFolders
}

/// <summary>Which way a folder's items fill its grid of rows and columns.</summary>
public enum LayoutOrder
{
    /// <summary>
    /// Down the first column, then down the next, as Explorer's List view
    /// reads: the newest file at the top of the first column and the next
    /// newest under it, so an order is read the way a list is.
    /// </summary>
    DownThenAcross,

    /// <summary>Along the first row, then along the next, as a page of text reads: the only way there was before.</summary>
    AcrossThenDown
}

/// <summary>
/// Which order each folder's sub-folders and files are shown in - on the
/// nested canvas, on the tree and in the folder list alike - and which way
/// that order fills a folder's grid.
///
/// <para>Every folder follows <see cref="Default"/> unless it has an order of
/// its own, and a folder has one only when it was sorted itself: nothing is
/// inherited, so sorting a folder by date leaves its sub-folders as they
/// were.  Under <see cref="SortScope.AllFolders"/> the folders' own orders
/// are kept but not used, so switching back finds them where they were.</para>
///
/// <para>Only the <see cref="MaximumFolders"/> folders sorted most recently
/// are remembered: a folder not sorted for that long goes back to the
/// default, and the workspace file stays the size of a few hundred
/// kilobytes at worst.  A folder sorted into the default order lets its own
/// go and follows the default from then on.  A folder whose own order the
/// default only later comes to match keeps it: it was sorted that way
/// itself, and stays that way when the default moves on again.</para>
///
/// <para>A folder renamed where the canvas can see it takes its own order,
/// and those of the folders inside it, to its new name (<see cref="Move"/>).</para>
///
/// <para>Paths are the normalised full paths the views already hold
/// (<see cref="ViewAllPath.Normalize"/>), compared without regard to case.
/// Belongs to the UI thread, like everything that reads it.</para>
/// </summary>
public sealed class FolderOrders
{
    /// <summary>How many folders' own orders are remembered, the most recently sorted ones.</summary>
    public const int MaximumFolders = 5000;

    private readonly Dictionary<string, Entry> _folders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many of the folders' own orders are by each column, so whether any is by type is a lookup.</summary>
    private readonly int[] _byColumn = new int[Enum.GetValues<SortColumn>().Length];

    private long _clock;
    private ItemSort _default = ItemSort.Default;
    private SortScope _scope = SortScope.PerFolder;
    private LayoutOrder _flow = LayoutOrder.DownThenAcross;

    /// <summary>
    /// Raised after every change: with a folder's path when that one folder's
    /// order is all that changed, with null when the change can reach any
    /// folder - the default, the scope, the way the grids fill, or every
    /// folder's own order let go of at once.
    /// </summary>
    public event Action<string?>? Changed;

    /// <summary>The order of every folder that has none of its own, and of all of them under <see cref="SortScope.AllFolders"/>.</summary>
    public ItemSort Default => _default;

    public SortScope Scope
    {
        get => _scope;
        set
        {
            if (_scope == value)
            {
                return;
            }

            _scope = value;
            Changed?.Invoke(null);
        }
    }

    /// <summary>Which way every folder's grids fill, This PC's drives apart.</summary>
    public LayoutOrder Flow
    {
        get => _flow;
        set
        {
            if (_flow == value)
            {
                return;
            }

            _flow = value;
            Changed?.Invoke(null);
        }
    }

    /// <summary>Whether grids fill a column at a time: <see cref="LayoutOrder.DownThenAcross"/>.</summary>
    public bool DownFirst => _flow == LayoutOrder.DownThenAcross;

    /// <summary>How many folders have an order of their own, used or not.</summary>
    public int Count => _folders.Count;

    /// <summary>Whether any folder's own order is in use - false, and every folder is in <see cref="Default"/>, is the case to make cheap.</summary>
    public bool HasFolderOrders => _scope == SortScope.PerFolder && _folders.Count > 0;

    /// <summary>
    /// The order a folder is shown in: its own, if it has one and folders
    /// have their own, otherwise the default.  A dictionary lookup at most,
    /// and not even that while no folder has an order of its own.
    /// </summary>
    public ItemSort SortOf(string? path) =>
        HasFolderOrders && !string.IsNullOrEmpty(path) && _folders.TryGetValue(path, out var entry)
            ? entry.Sort
            : _default;

    /// <summary>Whether a folder has an order of its own that is being used, which "Reset to the default order" would undo.</summary>
    public bool HasOwnOrder(string? path) =>
        HasFolderOrders && !string.IsNullOrEmpty(path) && _folders.ContainsKey(path);

    /// <summary>Whether <paramref name="column"/> orders any folder: the default, or a folder's own order in use.</summary>
    public bool Uses(SortColumn column) =>
        _default.Column == column || _scope == SortScope.PerFolder && _byColumn[(int)column] > 0;

    /// <summary>
    /// What a header or a Sort by choice does: orders <paramref name="path"/>
    /// by <paramref name="sort"/>, or - with no folder, or every folder the
    /// same - every folder.
    /// </summary>
    public void Choose(string? path, ItemSort sort)
    {
        if (_scope == SortScope.AllFolders || string.IsNullOrEmpty(path))
        {
            SetDefault(sort);
            return;
        }

        SetFolder(path, sort);
    }

    /// <summary>
    /// Changes the order every folder without its own follows.  The folders'
    /// own orders all stay, even one the default now matches: letting that
    /// go would have the folder follow the next default instead of the order
    /// it was given - a Downloads sorted by date would go back to names the
    /// moment every folder was sorted by date and then by name again.
    /// </summary>
    public void SetDefault(ItemSort sort)
    {
        if (_default == sort)
        {
            return;
        }

        _default = sort;
        Changed?.Invoke(null);
    }

    /// <summary>"Use this order for all folders": the default becomes it, and every folder's own order is let go of.</summary>
    public void UseEverywhere(ItemSort sort)
    {
        if (_default == sort && _folders.Count == 0)
        {
            return;
        }

        _default = sort;
        _folders.Clear();
        Array.Clear(_byColumn);
        Changed?.Invoke(null);
    }

    /// <summary>
    /// Gives one folder an order of its own - the default lets its own go -
    /// and makes it the most recently sorted, which is the last to be
    /// forgotten when there are more than <see cref="MaximumFolders"/>.
    /// </summary>
    public void SetFolder(string path, ItemSort sort)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        if (sort == _default)
        {
            if (!Forget(path))
            {
                return;
            }
        }
        else
        {
            if (_folders.TryGetValue(path, out var entry))
            {
                _byColumn[(int)entry.Sort.Column]--;
            }
            else if (_folders.Count >= MaximumFolders)
            {
                ForgetOldest();
            }

            _folders[path] = new Entry(sort, ++_clock);
            _byColumn[(int)sort.Column]++;
        }

        Changed?.Invoke(path);
    }

    /// <summary>"Reset to the default order": its own order let go of.</summary>
    public void ResetFolder(string path)
    {
        if (Forget(path))
        {
            Changed?.Invoke(path);
        }
    }

    /// <summary>
    /// A folder was renamed: its own order, and those of the folders inside
    /// it, go with it to <paramref name="newPath"/>, each still as recently
    /// sorted as it was.  Orders are kept by path, and a folder that took a
    /// new name would otherwise be back in the default the next time it was
    /// placed.  A walk over at most <see cref="MaximumFolders"/> entries, and
    /// only for a rename; nothing at all while no folder has an order of its own.
    /// </summary>
    public void Move(string oldPath, string newPath)
    {
        if (_folders.Count == 0 || string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath)
            || string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        List<(string Path, Entry Entry)>? moving = null;
        foreach (var (path, entry) in _folders)
        {
            if (IsSameOrInside(path, oldPath))
            {
                (moving ??= []).Add((path, entry));
            }
        }

        if (moving is null)
        {
            return;
        }

        foreach (var (path, entry) in moving)
        {
            _folders.Remove(path);
            _byColumn[(int)entry.Sort.Column]--;
        }

        foreach (var (path, entry) in moving)
        {
            var moved = string.Concat(newPath, path.AsSpan(oldPath.Length));
            if (_folders.Remove(moved, out var replaced))
            {
                _byColumn[(int)replaced.Sort.Column]--;
            }

            _folders[moved] = entry;
            _byColumn[(int)entry.Sort.Column]++;
        }

        Changed?.Invoke(newPath);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or somewhere inside it.</summary>
    private static bool IsSameOrInside(string path, string folder) =>
        path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
        && (path.Length == folder.Length || folder.EndsWith('\\') || path[folder.Length] == '\\');

    /// <summary>
    /// Puts back what the workspace file held, in one change: the default,
    /// the scope, the way grids fill and the folders' own orders, the oldest
    /// first as <see cref="Saved"/> writes them - one the same as the
    /// default too, which the folder keeps for the reason <see cref="SetDefault"/>
    /// keeps it.  Anything the file had wrong is left out rather than
    /// failing the rest.
    /// </summary>
    public void Load(ItemSort sort, SortScope scope, LayoutOrder flow, IEnumerable<FolderSortState>? folders)
    {
        _default = sort;
        _scope = scope;
        _flow = flow;
        _folders.Clear();
        Array.Clear(_byColumn);
        _clock = 0;
        foreach (var state in folders ?? [])
        {
            if (state is null || string.IsNullOrWhiteSpace(state.Path))
            {
                continue;
            }

            var own = ItemSort.FromSetting(state.Sort);
            if (_folders.TryGetValue(state.Path, out var replaced))
            {
                _byColumn[(int)replaced.Sort.Column]--;
            }
            else if (_folders.Count >= MaximumFolders)
            {
                ForgetOldest();
            }

            _folders[state.Path] = new Entry(own, ++_clock);
            _byColumn[(int)own.Column]++;
        }

        Changed?.Invoke(null);
    }

    /// <summary>The folders' own orders for the workspace file, the least recently sorted first.</summary>
    public List<FolderSortState> Saved() =>
        [.. _folders
            .OrderBy(pair => pair.Value.Stamp)
            .Select(pair => new FolderSortState(pair.Key, pair.Value.Sort.ToSetting()))];

    /// <summary>The scope as the workspace file writes it: its name, which reads back whatever order the values are ever put in.</summary>
    public static string ScopeSetting(SortScope scope) => scope.ToString();

    public static SortScope ParseScope(string? text) => ParseName(text, SortScope.PerFolder);

    public static string FlowSetting(LayoutOrder flow) => flow.ToString();

    public static LayoutOrder ParseFlow(string? text) => ParseName(text, LayoutOrder.DownThenAcross);

    /// <summary>A value by its name only - a number is nothing this file writes - or the fallback for anything else.</summary>
    private static T ParseName<T>(string? text, T fallback)
        where T : struct, Enum
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return fallback;
        }

        foreach (var value in Enum.GetValues<T>())
        {
            if (string.Equals(value.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return fallback;
    }

    private bool Forget(string path)
    {
        if (!_folders.Remove(path, out var entry))
        {
            return false;
        }

        _byColumn[(int)entry.Sort.Column]--;
        return true;
    }

    /// <summary>
    /// Lets go of the folder sorted longest ago.  A walk over at most
    /// <see cref="MaximumFolders"/> entries, and only when a folder not
    /// sorted before is sorted with the memory full - far from every click.
    /// </summary>
    private void ForgetOldest()
    {
        string? oldest = null;
        var stamp = long.MaxValue;
        foreach (var (path, entry) in _folders)
        {
            if (entry.Stamp < stamp)
            {
                stamp = entry.Stamp;
                oldest = path;
            }
        }

        if (oldest is not null)
        {
            Forget(oldest);
        }
    }

    private readonly record struct Entry(ItemSort Sort, long Stamp);
}

/// <summary>One folder's own order in the workspace file: its path, and the order as <see cref="ItemSort.ToSetting"/> writes it.</summary>
public sealed record FolderSortState(string Path, string Sort);
