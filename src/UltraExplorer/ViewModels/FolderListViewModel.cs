using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;

namespace UltraExplorer.ViewModels;

/// <summary>
/// The current folder as a plain list, beside the canvas rather than instead of
/// it.
///
/// The canvas is good at shape: what is inside what, how big a branch is, where
/// two things sit relative to each other.  It is bad at the other half of using
/// a file manager - going down a known path, or finding one name among two
/// hundred.  A list is good at exactly that and useless at the first, so the two
/// are worth having at once: find it here, look at it there.
///
/// The list reads the directory itself rather than the canvas, so it shows what
/// is really on disk whether or not the folder has been opened out on the
/// canvas, and clicking a row that has no node yet reveals one.
///
/// It keeps up with the disk by itself too: its folder is registered with the
/// change hub (<see cref="Changes"/>), and a change there reads the folder
/// again and merges what it found into the rows already shown - rows that went
/// are taken out, new ones are put in at their place and fade in, and a row
/// whose size or date moved is updated where it is - so the scroll and what is
/// lit stay as they were.  Nothing about what is selected decides whether it
/// hears of a change; a file selected shows its folder, and the folder is
/// watched like any other.
/// </summary>
public sealed class FolderListViewModel : ObservableObject
{
    /// <summary>Rows asked for their Shell icon as the list fills; the rest ask when first shown (<see cref="FolderListItem.AskForIcon"/>).</summary>
    private const int IconBudget = 300;

    /// <summary>
    /// Rows a change on disk may add, take out or move one at a time before the
    /// list is refilled in one go instead.  The list box does a few
    /// microseconds of work for each row it is told of, so a thousand is a few
    /// milliseconds; past that a refill is cheaper, and a change that big is
    /// not one anybody follows row by row anyway.
    /// </summary>
    private const int MaximumMergedRows = 1000;

    /// <summary>How long a new row keeps its fade: longer than the fade itself, so it has always played.</summary>
    private static readonly TimeSpan NewRowTime = TimeSpan.FromMilliseconds(600);

    /// <summary>How long after a read for a change failed, its folder still there, it is tried the once more (see <see cref="LeaveGoneFolderAsync"/>).</summary>
    private static readonly TimeSpan UnreadableRetryTime = TimeSpan.FromSeconds(1);

    private readonly Func<string, ItemSort, CancellationToken, Task<ViewAllDirectorySnapshot>> _read;
    private readonly Func<string, bool, Task> _activate;
    private readonly Func<string, bool> _isOnCanvas;
    private readonly ShellIconService _icons;

    /// <summary><see cref="IconWhenShown"/>, made once: handed to every row past the ones asked for as the list fills.</summary>
    private readonly Func<FolderListItem, System.Windows.Media.ImageSource?> _iconWhenShown;

    private readonly List<FolderListItem> _all = [];

    /// <summary>The rows shown, by path: how the shared selection finds its rows without walking the list.</summary>
    private readonly Dictionary<string, FolderListItem> _byPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders the list has been in, newest last; what Back walks.</summary>
    private readonly List<string> _history = [];
    private CancellationTokenSource? _load;

    private string _folderPath = string.Empty;
    private long _folderVersion;
    private string _title = "No folder";
    private string _countText = string.Empty;
    private string _filter = string.Empty;
    private string _emptyText = string.Empty;

    /// <summary>
    /// What the list says while it has no rows read, when that is not that
    /// the folder is empty: that there is no folder, or why it could not be
    /// read.  Null once a read found the folder, empty or not.  A filter
    /// typed meanwhile, or cleared, keeps saying it (<see cref="UpdateEmptyText"/>).
    /// </summary>
    private string? _noRowsText;

    private bool _isVisible;
    private int _held;

    /// <summary>True while the list is revealing one of its own rows on the canvas.</summary>
    public bool IsHoldingFolder => _held > 0;

    /// <summary>
    /// Whether a target was asked for while the list was held, and which:
    /// a folder by path with its focus, or a node alone (<see cref="_heldFolder"/>
    /// null).  A reveal on a share that does not answer holds the list for
    /// most of a minute, and the canvas, the address bar or Back can go
    /// somewhere else meanwhile; the last of those is where the list goes
    /// once the hold ends (<see cref="ApplyHeldTarget"/>).
    /// </summary>
    private bool _heldAsked;
    private string? _heldFolder;
    private ViewAllNodeViewModel? _heldNode;

    /// <summary>What the list itself asked the canvas to go to while it may be held (<see cref="ActivateOwn"/>).</summary>
    private readonly HashSet<string> _ownTargets = new(StringComparer.OrdinalIgnoreCase);

    private bool _isLoading;
    private bool _isTruncated;
    private FolderListItem? _selected;
    private ItemSort _sort = ItemSort.Default;

    /// <summary>The order the rows were read for: which of them were kept, when the folder had more than a read holds.</summary>
    private ItemSort _readSort = ItemSort.Default;

    private ChangeHub? _changes;

    /// <summary>The folder registered with <see cref="_changes"/>, which is <see cref="FolderPath"/> once it is set.</summary>
    private string _registeredPath = string.Empty;

    /// <summary>Counts the list's full reads: a read for a change that finds it has moved on was overtaken by one.</summary>
    private int _loadGeneration;

    private bool _liveReading;
    private bool _liveAgain;

    /// <summary>A change came while the list was hidden: it is read afresh when it is shown.</summary>
    private bool _staleWhileHidden;

    /// <summary>The folder's own last-write time as a read for a change or a poll last found it; zero before either.</summary>
    private long _listedWriteTicks;

    /// <summary>
    /// A read for a change failed with the folder still there, and it is
    /// being tried once more: failing again, the list says it could not be
    /// read and waits for the next change, rather than read it again at once
    /// - which would fail again at once, thousands of times a second.
    /// </summary>
    private bool _unreadableRetried;

    /// <summary>The rows still fading in, oldest first, each with when it came (<see cref="Environment.TickCount64"/>).</summary>
    private readonly List<(FolderListItem Row, long Since)> _newRows = [];
    private System.Windows.Threading.DispatcherTimer? _newRowsTimer;

    private bool _typeNamesWaiting;

    public FolderListViewModel(
        Func<string, CancellationToken, Task<ViewAllDirectorySnapshot>> read,
        Func<string, bool, Task> activate,
        Func<string, bool> isOnCanvas,
        ShellIconService icons)
        : this((path, _, cancellation) => read(path, cancellation), activate, isOnCanvas, icons)
    {
    }

    /// <param name="read">
    /// Reads a folder for the list, given the order the rows are shown in:
    /// a folder with more entries than one read holds is to keep the first
    /// ones in that order (see <see cref="ViewAllFileSystemService.GetChildrenAsync"/>).
    /// </param>
    public FolderListViewModel(
        Func<string, ItemSort, CancellationToken, Task<ViewAllDirectorySnapshot>> read,
        Func<string, bool, Task> activate,
        Func<string, bool> isOnCanvas,
        ShellIconService icons)
    {
        _read = read;
        _activate = activate;
        _isOnCanvas = isOnCanvas;
        _icons = icons;
        _iconWhenShown = IconWhenShown;

        ActivateCommand = new AsyncRelayCommand<FolderListItem>(item => Activate(item, open: true));
        RevealCommand = new AsyncRelayCommand<FolderListItem>(item => Activate(item, open: false));
        OpenFirstMatchCommand = new AsyncRelayCommand(() => Activate(Items.FirstOrDefault(), open: true));
        ClearFilterCommand = new RelayCommand(() => Filter = string.Empty);
        HideCommand = new RelayCommand(() => IsVisible = false);
        UpCommand = new AsyncRelayCommand(GoUpAsync, () => CanGoUp);
        BackCommand = new AsyncRelayCommand(GoBackAsync, () => CanGoBack);
    }

    /// <summary>Rows that survive the current filter, best match first.</summary>
    public ObservableCollection<FolderListItem> Items => _rows;

    private readonly RowCollection _rows = [];

    public System.Windows.Input.ICommand ActivateCommand { get; }
    public System.Windows.Input.ICommand RevealCommand { get; }
    public System.Windows.Input.ICommand OpenFirstMatchCommand { get; }
    public System.Windows.Input.ICommand ClearFilterCommand { get; }
    public System.Windows.Input.ICommand HideCommand { get; }
    public System.Windows.Input.ICommand UpCommand { get; }
    public System.Windows.Input.ICommand BackCommand { get; }

    /// <summary>True unless the list is already at the top of a drive.</summary>
    public bool CanGoUp => ParentOf(FolderPath) is not null;

    public bool CanGoBack => _history.Count > 0;

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value) && value && (_all.Count == 0 || _staleWhileHidden))
            {
                // Nothing was read while it was hidden, or the folder changed
                // meanwhile, so read it now.
                _ = ReloadAsync();
            }
        }
    }

    /// <summary>
    /// The change hub the list's folder is registered with, or null while
    /// nothing watches the disk (a test's list).  The folder is registered
    /// whether or not the list is showing: a change while it is hidden is
    /// remembered, and the folder is read afresh when it is shown.
    /// </summary>
    internal ChangeHub? Changes
    {
        get => _changes;
        set
        {
            if (ReferenceEquals(_changes, value))
            {
                return;
            }

            UnregisterFolder();
            _changes = value;
            RegisterFolder();
        }
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string FolderPath
    {
        get => _folderPath;
        private set
        {
            if (SetProperty(ref _folderPath, value))
            {
                _folderVersion++;
                // A folder has an order of its own: the list is read in it.
                // Nothing to reorder - the rows of the folder before are
                // about to be replaced.
                if (SortOf?.Invoke(value) is { } own && own != _sort)
                {
                    _sort = own;
                    OnPropertyChanged(nameof(Sort));
                }

                UnregisterFolder();
                RegisterFolder();
                _staleWhileHidden = false;
                _listedWriteTicks = 0;
                _unreadableRetried = false;
                OnPropertyChanged(nameof(CanGoUp));
                OnPropertyChanged(nameof(CanGoBack));
                (UpCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (BackCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string CountText
    {
        get => _countText;
        private set => SetProperty(ref _countText, value);
    }

    /// <summary>What to say instead of an empty list, and only then.</summary>
    public string EmptyText
    {
        get => _emptyText;
        private set => SetProperty(ref _emptyText, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>
    /// The order of the rows while nothing is typed: folders, then files, each
    /// in this order (see <see cref="ViewAllEntryOrder"/>).  What is typed
    /// orders by how well each name answers it, whatever this is - the best
    /// match first is the point of typing.  Changing it reorders the rows
    /// already read and keeps the highlighted one; nothing is read again -
    /// unless the folder had more entries than one read holds, when the rows
    /// read were the first ones in the order before, and the first ones in
    /// this order are read to replace them.
    /// </summary>
    public ItemSort Sort
    {
        get => _sort;
        set
        {
            // Nothing read, nothing to reorder - and refilling would put "empty"
            // where the list says nothing is selected.  A read under way
            // orders what it brings in by this when it lands.
            if (!SetProperty(ref _sort, value) || _all.Count == 0)
            {
                return;
            }

            Reorder();
            if (_isTruncated && _readSort != value)
            {
                _ = RereadKeepingHighlightAsync();
            }
        }
    }

    /// <summary>The rows read, put in the current order again, the highlighted one kept.</summary>
    private void Reorder()
    {
        // The list box keeps its highlighted row through a refill; this one
        // is announced again all the same, so a row highlighted from here
        // alone is lit in its new place too.
        var highlighted = _selected;
        ApplyFilter();
        if (highlighted is not null && Items.Contains(highlighted))
        {
            _selected = highlighted;
            OnPropertyChanged(nameof(Selected));
        }
    }

    /// <summary>Reads the folder again, for another order's first rows, and lights the row that was lit if it is still among them.</summary>
    private async Task RereadKeepingHighlightAsync()
    {
        var highlighted = _selected?.FullPath;
        var version = _folderVersion;
        await ReloadAsync();
        if (version != _folderVersion) return;
        var match = highlighted is null
            ? null
            : Items.FirstOrDefault(item => string.Equals(item.FullPath, highlighted, StringComparison.OrdinalIgnoreCase));
        if (highlighted is not null && !ReferenceEquals(_selected, match))
        {
            _selected = match;
            OnPropertyChanged(nameof(Selected));
        }
    }

    /// <summary>
    /// Orders the rows again once the Shell has named every kind of file the
    /// last ordering by type had to rank by a stand-in (see
    /// <see cref="ViewAllEntryOrder.Sort{T}(IEnumerable{T}, Func{T, ViewAllEntryDescriptor}, ItemSort, out bool)"/>).
    /// Only on a thread that can be come back to - the window's.
    /// </summary>
    private async Task ReorderWhenTypeNamesArriveAsync()
    {
        if (_typeNamesWaiting || SynchronizationContext.Current is null)
        {
            return;
        }

        _typeNamesWaiting = true;
        try
        {
            // Never straight back into the refill that asked.
            await Task.Yield();
            await FileTypeNames.WhenPrefetchedAsync();
        }
        finally
        {
            _typeNamesWaiting = false;
        }

        if (_sort.Column == SortColumn.Type && _all.Count > 0 && _filter.Trim().Length == 0)
        {
            Reorder();
        }
    }

    /// <summary>
    /// The highlighted row.  Setting it only highlights: driving the canvas is
    /// left to an actual click, because arrowing down a folder of two hundred
    /// files would otherwise reveal and centre on every one of them on the way
    /// past.
    /// </summary>
    public FolderListItem? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    /// <summary>Highlights a row without touching the canvas - what a right-click does.</summary>
    public void Highlight(FolderListItem? item)
    {
        if (ReferenceEquals(_selected, item))
        {
            return;
        }

        _selected = item;
        OnPropertyChanged(nameof(Selected));
    }

    /// <summary>
    /// The selection the whole window shares.  The rows lit in the list are
    /// its paths that are rows here; a path filtered out of the list, or past
    /// what one read holds, stays selected all the same.
    /// </summary>
    public ItemSelection? SharedSelection { get; init; }

    /// <summary>
    /// A folder's own order, by its path: taken as the list goes to the
    /// folder, before it is read.  Null, and the list keeps <see cref="Sort"/>
    /// from folder to folder.
    /// </summary>
    public Func<string, ItemSort>? SortOf { get; init; }

    /// <summary>
    /// The rows the shared selection lights changed - it changed, from
    /// somewhere other than the list - and the list box should show them.
    /// </summary>
    public event Action? SelectionRowsChanged;

    /// <summary>The rows are about to be replaced: what the list box then says about its selection is not the user's doing.</summary>
    public event Action? RowsReplacing;

    /// <summary>The rows were replaced, and the shared selection's rows should be lit among the new ones.</summary>
    public event Action? RowsReplaced;

    /// <summary>The row for a path, when it is shown.</summary>
    public FolderListItem? RowFor(string? path) =>
        path is not null && _byPath.TryGetValue(path, out var row) ? row : null;

    /// <summary>
    /// The rows the shared selection lights: whichever is smaller is walked,
    /// the selection or the rows, so ten thousand selected files in another
    /// folder cost nothing here.
    /// </summary>
    public IReadOnlyList<FolderListItem> SelectedRows()
    {
        if (SharedSelection is not { Count: > 0 } selection || _byPath.Count == 0 || selection.CountIn(FolderPath) == 0)
        {
            return [];
        }

        var rows = new List<FolderListItem>(Math.Min(selection.Count, _byPath.Count));
        if (selection.Count <= _byPath.Count)
        {
            foreach (var path in selection.Paths)
            {
                if (_byPath.TryGetValue(path, out var row))
                {
                    rows.Add(row);
                }
            }
        }
        else
        {
            foreach (var row in Items)
            {
                if (selection.Contains(row.FullPath))
                {
                    rows.Add(row);
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// The shared selection changed.  A change the list made itself is on
    /// screen already; any other is handed to the list box to show.
    /// </summary>
    public void OnSelectionChanged(ItemSelection selection)
    {
        if (selection.LastSource != SelectionSource.List)
        {
            SelectionRowsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Stops the list following the canvas while something the list itself did is
    /// still settling.  Clicking a folder row selects that folder on the canvas,
    /// and following that selection would take the list straight into the folder
    /// - so a single click would open it, which is not what a single click does.
    /// </summary>
    public IDisposable HoldFolder() => new Hold(this);

    private sealed class Hold : IDisposable
    {
        private readonly FolderListViewModel _list;
        private bool _released;

        public Hold(FolderListViewModel list)
        {
            _list = list;
            _list._held++;
        }

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            _list._held--;
            if (_list._held == 0)
            {
                _list.ApplyHeldTarget();
            }
        }
    }

    /// <summary>
    /// The hold is over: the list goes where the last target asked for while
    /// it was held points (<see cref="NoteHeldTarget"/>), as it would have had
    /// nothing held it.
    /// </summary>
    private void ApplyHeldTarget()
    {
        _ownTargets.Clear();
        if (!_heldAsked)
        {
            return;
        }

        var folder = _heldFolder;
        var node = _heldNode;
        ForgetHeldTarget();
        if (folder is not null)
        {
            SetTarget(folder, node);
            return;
        }

        SetTarget(node);
    }

    /// <summary>
    /// A target asked for while the list is held, remembered for when the
    /// hold ends - unless it is the list's own doing, which leaves nothing to
    /// go to and is newer than whatever was asked before it: the folder the
    /// list is in, or an item in it; a folder that is one of its rows, picked
    /// there - going into it would make a single click open it; or what the
    /// list itself asked the canvas to go to, however late the canvas gets
    /// there - after the list went on somewhere else, going back to it would
    /// undo the list's own newer step; or a focus the list's own pick is
    /// still bringing in (a key, Ctrl or Shift on a row), which a step the
    /// list took itself meanwhile outranks.  Likewise a folder picked, under a
    /// hold of its own, among the rows of the folder already asked for by path
    /// - a reveal of a folder's items points the list at the folder, then
    /// selects them so held: had nothing held the list, it would be in that
    /// folder by then, and the folder picked would be lit there, not gone into.
    /// </summary>
    private void NoteHeldTarget(string? folder, ViewAllNodeViewModel? node)
    {
        var goesTo = folder ?? (node is null ? string.Empty : node.IsDirectory ? node.FullPath : node.Parent?.FullPath ?? string.Empty);
        if (string.Equals(goesTo, FolderPath, StringComparison.OrdinalIgnoreCase)
            || folder is null && node is { IsDirectory: true } && string.Equals(ParentOf(node.FullPath), FolderPath, StringComparison.OrdinalIgnoreCase)
            || (folder ?? node?.FullPath) is { } path && _ownTargets.Contains(path)
            || SharedSelection?.LastSource == SelectionSource.List)
        {
            ForgetHeldTarget();
            return;
        }

        if (_held > 1 && folder is null && node is { IsDirectory: true } && _heldAsked && _heldFolder is { } asked
            && string.Equals(ParentOf(node.FullPath), asked, StringComparison.OrdinalIgnoreCase))
        {
            _heldNode = node;
            return;
        }

        _heldAsked = true;
        _heldFolder = folder;
        _heldNode = node;
    }

    /// <summary>
    /// Asks the canvas to go to <paramref name="path"/> for the list, noting
    /// it as the list's own (see <see cref="NoteHeldTarget"/>): kept for as
    /// long as a hold may still see the canvas get there.
    /// </summary>
    private Task ActivateOwn(string path, bool open)
    {
        if (_held == 0)
        {
            _ownTargets.Clear();
        }

        _ownTargets.Add(path);
        return _activate(path, open);
    }

    /// <summary>A target asked for while held is older than the list going somewhere itself.</summary>
    private void ForgetHeldTarget()
    {
        _heldAsked = false;
        _heldFolder = null;
        _heldNode = null;
    }

    /// <summary>
    /// Points the list at a folder.  A file is taken as its parent folder with
    /// the file picked out, which is what makes selecting a node on the canvas
    /// scroll the list to the matching row.
    /// </summary>
    public void SetTarget(ViewAllNodeViewModel? node)
    {
        if (_held > 0)
        {
            // The list is driving the canvas, not the other way round.  The row
            // still follows the selection; the folder does not move until the
            // hold ends.
            NoteHeldTarget(null, node);
            SelectExisting(node);
            return;
        }

        var folder = node is null
            ? string.Empty
            : node.IsDirectory
                ? node.FullPath
                : node.Parent?.FullPath ?? string.Empty;

        if (string.Equals(folder, FolderPath, StringComparison.OrdinalIgnoreCase))
        {
            SelectExisting(node);
            return;
        }

        GoTo(
            folder,
            folder.Length == 0
                ? "No folder"
                : node is { IsDirectory: true } ? node.DisplayName : node?.Parent?.DisplayName ?? folder,
            node);
    }

    /// <summary>
    /// Points the list at a folder by path, with <paramref name="focus"/>'s
    /// row picked out: the folder several selected items share, which the
    /// list shows with all of them lit.
    /// </summary>
    public void SetTarget(string folderPath, ViewAllNodeViewModel? focus)
    {
        if (_held > 0)
        {
            NoteHeldTarget(folderPath, focus);
        }

        if (_held > 0 || string.Equals(folderPath, FolderPath, StringComparison.OrdinalIgnoreCase))
        {
            SelectExisting(focus);
            return;
        }

        GoTo(folderPath, NameOf(folderPath), focus);
    }

    private void GoTo(string folder, string title, ViewAllNodeViewModel? focus)
    {
        ForgetHeldTarget();
        if (FolderPath.Length > 0)
        {
            _history.Add(FolderPath);
            if (_history.Count > 64)
            {
                _history.RemoveAt(0);
            }
        }

        FolderPath = folder;
        Title = title;

        _filter = string.Empty;
        OnPropertyChanged(nameof(Filter));

        _ = ReloadAsync(focus);
    }

    /// <summary>
    /// Walks the list into a folder without waiting for the canvas.  The list is
    /// its own little explorer: going up a level to look at a sibling should not
    /// mean opening that whole branch out on the canvas first.
    /// </summary>
    /// <param name="moveCanvas">
    /// Whether the canvas comes along once the folder is read.  Not when the
    /// list only leaves a folder that went from disk (<see cref="LeaveGoneFolderAsync"/>):
    /// the canvas going there selects the folder above, and the next Delete -
    /// meant for something in the folder that went - would recycle it whole.
    /// </param>
    public async Task NavigateAsync(string path, bool remember = true, bool moveCanvas = true)
    {
        string normalized;
        try
        {
            normalized = ViewAllPath.Normalize(path);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException or PathTooLongException)
        {
            return;
        }

        if (string.Equals(normalized, FolderPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ForgetHeldTarget();
        if (remember && FolderPath.Length > 0)
        {
            _history.Add(FolderPath);
            if (_history.Count > 64)
            {
                _history.RemoveAt(0);
            }
        }

        FolderPath = normalized;
        Title = NameOf(normalized);

        // A filter belongs to the folder it was typed in; carrying it into the
        // next one hides everything and looks like an empty folder.
        _filter = string.Empty;
        OnPropertyChanged(nameof(Filter));

        var version = _folderVersion;
        await ReloadAsync();
        if (version != _folderVersion || !ViewAllPath.Equals(normalized, FolderPath) || !moveCanvas) return;

        // The canvas comes along.  Going into a folder already moved it - the
        // row was clicked - and going back up leaving it behind was the half of
        // the gesture that felt broken.
        await ActivateOwn(normalized, false);
    }

    private Task GoUpAsync() =>
        ParentOf(FolderPath) is { } parent ? NavigateAsync(parent) : Task.CompletedTask;

    private Task GoBackAsync()
    {
        if (_history.Count == 0)
        {
            return Task.CompletedTask;
        }

        var previous = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        return NavigateAsync(previous, remember: false);
    }

    /// <summary>The folder above this one, or null at the top of a drive.</summary>
    private static string? ParentOf(string path)
    {
        if (path.Length == 0)
        {
            return null;
        }

        try
        {
            var parent = Path.GetDirectoryName(path.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(parent) ? null : parent;
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException)
        {
            return null;
        }
    }

    private static string NameOf(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        return name.Length > 0 ? name : path;
    }

    /// <summary>Re-reads the folder; the canvas changing underneath is enough reason.</summary>
    public async Task ReloadAsync(ViewAllNodeViewModel? select = null)
    {
        _load?.Cancel();
        _load?.Dispose();
        _load = null;
        _loadGeneration++;

        if (FolderPath.Length == 0 || !IsVisible)
        {
            _all.Clear();
            _byPath.Clear();
            ReplaceRows([]);
            CountText = string.Empty;
            EmptyText = _noRowsText = FolderPath.Length == 0 ? "Nothing is selected." : string.Empty;
            return;
        }

        var cancellation = new CancellationTokenSource();
        _load = cancellation;
        IsLoading = true;

        try
        {
            var sort = _sort;
            var snapshot = await _read(FolderPath, sort, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            _readSort = sort;
            _staleWhileHidden = false;
            _unreadableRetried = false;
            _noRowsText = null;

            _all.Clear();
            foreach (var entry in snapshot.Entries)
            {
                _all.Add(new FolderListItem(entry));
            }

            _isTruncated = snapshot.IsTruncated;
            CountText = $"{_all.Count:N0}{(_isTruncated ? "+" : string.Empty)}";
            EmptyText = _all.Count == 0 ? "This folder is empty." : string.Empty;
            ApplyFilter();
            SelectExisting(select);

            // The order changed while this was being read, and the folder is
            // bigger than a read: these are the first rows of the order before.
            if (_isTruncated && _readSort != _sort)
            {
                _ = RereadKeepingHighlightAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer folder is already being read.
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // An overtaken provider can fail after the new folder succeeded.
            // Its error must not erase that folder or its current status.
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_load, cancellation)) return;
            _all.Clear();
            _byPath.Clear();
            ReplaceRows([]);
            CountText = string.Empty;
            EmptyText = _noRowsText = exception is UnauthorizedAccessException
                ? "Access denied."
                : "This folder could not be read.";

            // Gone before the list came to it - the canvas still pointing at
            // it, the change that said it went heard before the list was
            // there: no change is coming to take the list out of it, so it
            // leaves as it would have, for the nearest folder above.
            if (exception is DirectoryNotFoundException)
            {
                _ = LeaveGoneFolderAsync(FolderPath, exception);
            }
        }
        finally
        {
            if (ReferenceEquals(_load, cancellation))
            {
                IsLoading = false;
                _load = null;
            }

            cancellation.Dispose();
        }

        // A change that came while this read was under way may have come
        // after the directory was listed: it is read once more, merged.
        if (_liveAgain && _load is null && !_liveReading)
        {
            _ = RefreshLiveAsync();
        }
    }

    private void SelectExisting(ViewAllNodeViewModel? node)
    {
        if (node is null)
        {
            return;
        }

        var match = RowFor(node.FullPath);

        // Assigned to the field, not the property: this is the canvas telling the
        // list what is selected, and answering by driving the canvas back to it
        // would be a loop.
        if (!ReferenceEquals(_selected, match))
        {
            _selected = match;
            OnPropertyChanged(nameof(Selected));
        }
    }

    private void ApplyFilter()
    {
        var rows = OrderedRows(out var typeNamesPending);
        ReplaceRows(rows);
        UpdateEmptyText();

        if (typeNamesPending)
        {
            _ = ReorderWhenTypeNamesArriveAsync();
        }
    }

    /// <summary>
    /// What to say when no row is shown, and nothing when one is.  A folder
    /// with more entries than one read holds was filtered among the rows read
    /// only, and says so: a name past them is not "nothing".  With no rows
    /// read, why (<see cref="_noRowsText"/>) - "Access denied." is not "This
    /// folder is empty." for having had a filter typed over it.
    /// </summary>
    private void UpdateEmptyText() =>
        EmptyText = Items.Count == 0
            ? _all.Count == 0 ? _noRowsText ?? "This folder is empty."
                : _isTruncated ? $"Nothing matches “{_filter.Trim()}” among the first {_all.Count:N0}."
                : $"Nothing matches “{_filter.Trim()}”."
            : string.Empty;

    /// <summary>
    /// The rows that survive the filter, in the order they are shown, with
    /// the lookup by path filled for them and icons asked for the first ones.
    /// </summary>
    private List<FolderListItem> OrderedRows(out bool typeNamesPending)
    {
        var query = _filter.Trim();

        // Read as folders then files, each by name from A: the default order
        // is the listing as it came.
        typeNamesPending = false;
        IEnumerable<FolderListItem> matched = query.Length == 0
            ? _sort.IsDefault ? _all : ViewAllEntryOrder.Sort(_all, item => item.Entry, _sort, out typeNamesPending)
            : _all
                .Select(item => (Item: item, Score: FolderListMatch.Score(item.DisplayName, query)))
                .Where(pair => pair.Score != FolderListMatch.NoMatch)
                .OrderBy(pair => pair.Score)
                .ThenBy(pair => pair.Item.IsDirectory ? 0 : 1)
                .ThenBy(pair => pair.Item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(pair => pair.Item);

        var rows = new List<FolderListItem>(_all.Count);
        _byPath.Clear();
        foreach (var item in matched)
        {
            item.IsOnCanvas = _isOnCanvas(item.FullPath);
            item.DetailColumn = _sort.Column;
            rows.Add(item);
            _byPath.TryAdd(item.FullPath, item);

            if (rows.Count <= IconBudget)
            {
                item.AskForIcon = null;
                if (item.Icon is null)
                {
                    // Answers synchronously when the icon is already known, so a
                    // folder that has been looked at once fills in with no flicker.
                    _icons.Request(item.FullPath, item.IsDirectory, icon => item.Icon = icon);
                }
            }
            else
            {
                // Past those, a row asks when the list box first shows it.
                item.AskForIcon = _iconWhenShown;
            }
        }

        return rows;
    }

    /// <summary>
    /// A row's icon, asked for as the list box first shows it
    /// (<see cref="FolderListItem.AskForIcon"/>).  One already known is
    /// handed back at once, while the binding that asked is still reading it
    /// - told through the row, it would be a change in the middle of that
    /// read; one the Shell has yet to answer comes through the row later.
    /// </summary>
    private System.Windows.Media.ImageSource? IconWhenShown(FolderListItem item)
    {
        System.Windows.Media.ImageSource? known = null;
        var asking = true;
        _icons.Request(item.FullPath, item.IsDirectory, icon =>
        {
            if (asking)
            {
                known = icon;
            }
            else
            {
                item.Icon = icon;
            }
        });

        asking = false;
        return known;
    }

    private Task Activate(FolderListItem? item, bool open)
        => item is null ? Task.CompletedTask : ActivateOwn(item.FullPath, open);

    // ---- changes on disk ---------------------------------------------------------

    private void RegisterFolder()
    {
        if (_changes is { } hub && FolderPath.Length > 0)
        {
            _registeredPath = FolderPath;
            hub.Register(ChangeConsumer.List, _registeredPath, this);
        }
    }

    private void UnregisterFolder()
    {
        if (_changes is { } hub && _registeredPath.Length > 0)
        {
            hub.Unregister(ChangeConsumer.List, _registeredPath, this);
        }

        _registeredPath = string.Empty;
    }

    /// <summary>
    /// Something changed in the list's folder (the change hub, once per burst
    /// of changes).  Shown, the folder is read again and merged into the rows;
    /// hidden, it is noted and read when the list is shown.  The folder itself
    /// gone - deleted, renamed, moved away - takes the list to the nearest
    /// folder above it that is still there.
    /// </summary>
    internal void OnFolderChanged(in FolderChange change)
    {
        if (FolderPath.Length == 0 || !string.Equals(change.Key, FolderPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        LiveChangesTaken++;
        if ((change.Kinds & ChangeKinds.Gone) != 0)
        {
            _ = LeaveGoneFolderAsync(FolderPath);
            return;
        }

        RefreshForChange();
    }

    /// <summary>Changes under <paramref name="root"/> may have been missed: the list's folder, if it is under it, is read again.</summary>
    internal void OnEpochBumped(WatchRoot root)
    {
        if (FolderPath.Length > 0 && IsUnder(FolderPath, root))
        {
            RefreshForChange();
        }
    }

    /// <summary>
    /// A watch that is polled is due its look: when the list's folder is under
    /// it, its last-write time is compared, off this thread, with the one the
    /// last look found, and a folder that moved on is read again.  With no
    /// time kept yet - the first look since the list came to the folder, or
    /// since its watch went down - there is nothing to compare, and a change
    /// made between the list's read and this look would never be shown: the
    /// folder is read again once, as the tree does.
    /// </summary>
    internal void OnPollDue(WatchRoot root)
    {
        if (FolderPath.Length == 0 || !IsUnder(FolderPath, root) || !IsVisible)
        {
            return;
        }

        var path = FolderPath;
        var ticks = _listedWriteTicks;
        _ = PollAsync();

        async Task PollAsync()
        {
            long now;
            try
            {
                now = await Task.Run(() => NestedDirectoryReader.DirectoryWriteTicks(path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return;
            }

            if (now == 0 || !string.Equals(path, FolderPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _listedWriteTicks = now;
            if (now != ticks)
            {
                RefreshForChange();
            }
        }
    }

    /// <summary>Changes taken in for the list's folder, for tests.</summary>
    internal int LiveChangesTaken { get; private set; }

    /// <summary>Reads for changes merged into the rows, for tests.</summary>
    internal int LiveMerges { get; private set; }

    /// <summary>Of those, the ones too big to merge, which refilled the rows instead.</summary>
    internal int LiveRefills { get; private set; }

    private void RefreshForChange()
    {
        if (!IsVisible)
        {
            _staleWhileHidden = true;
            return;
        }

        _ = RefreshLiveAsync();
    }

    private static bool IsUnder(string path, WatchRoot root)
    {
        foreach (var prefix in root.Prefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && (path.Length == prefix.Length || prefix.EndsWith(Path.DirectorySeparatorChar) || path[prefix.Length] == Path.DirectorySeparatorChar))
            {
                return true;
            }

            // The root itself, as the list spells it: a share's root is
            // \\server\share, without the separator its prefix ends in.
            if (prefix.EndsWith(Path.DirectorySeparatorChar)
                && prefix.AsSpan(0, prefix.Length - 1).Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the folder again for a change and merges what it found into the
    /// rows.  One read at a time: a change that comes while one is under way
    /// has it followed by another, and a full read under way - a new folder,
    /// a new order - is left to finish and followed by one.  A read that finds
    /// the list has moved to another folder meanwhile is dropped - but not a
    /// change that came for the folder the list is in now while it was out:
    /// that one is read next.  Hidden before a change that came is read, the
    /// list reads its folder afresh when it is shown.
    /// </summary>
    private async Task RefreshLiveAsync()
    {
        if (_liveReading || _load is not null)
        {
            _liveAgain = true;
            return;
        }

        _liveReading = true;
        try
        {
            do
            {
                _liveAgain = false;
                var path = FolderPath;
                var sort = _sort;
                var generation = _loadGeneration;
                ViewAllDirectorySnapshot snapshot;
                long writeTicks;
                try
                {
                    writeTicks = await Task.Run(() => NestedDirectoryReader.DirectoryWriteTicks(path));
                    snapshot = await _read(path, sort, CancellationToken.None);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Gone between the change and the read, most likely.
                    if (generation == _loadGeneration && string.Equals(path, FolderPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _ = LeaveGoneFolderAsync(path, exception);
                        return;
                    }

                    if (_load is not null || !_liveAgain || FolderPath.Length == 0)
                    {
                        return;
                    }

                    continue;
                }

                if (generation != _loadGeneration || _load is not null || !string.Equals(path, FolderPath, StringComparison.OrdinalIgnoreCase))
                {
                    // The list moved on while this was read.  The full read of
                    // where it is now covers what changed before that read; a
                    // change since is still to be read - by the full read under
                    // way, when there is one, as it lands, otherwise here.
                    if (_load is not null || !_liveAgain || FolderPath.Length == 0)
                    {
                        return;
                    }

                    continue;
                }

                _listedWriteTicks = writeTicks;
                _unreadableRetried = false;
                Merge(snapshot, sort);
            }
            while (_liveAgain && IsVisible);

            if (_liveAgain && !IsVisible)
            {
                _liveAgain = false;
                _staleWhileHidden = true;
            }
        }
        finally
        {
            _liveReading = false;
        }
    }

    /// <summary>
    /// Puts a new read of the folder into the list without starting it again:
    /// a row still there keeps its object - its icon, its highlight, its place
    /// on screen - with its size and date brought up to date; a new one is
    /// made and fades in; the rows shown then follow the new ones a row at a
    /// time (<see cref="MergeRows"/>).
    /// </summary>
    private void Merge(ViewAllDirectorySnapshot snapshot, ItemSort sort)
    {
        LiveMerges++;
        _readSort = sort;
        _noRowsText = null;
        var previous = new Dictionary<string, FolderListItem>(_all.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var item in _all)
        {
            previous.TryAdd(item.FullPath, item);
        }

        _all.Clear();
        var now = Environment.TickCount64;
        foreach (var entry in snapshot.Entries)
        {
            var isDirectory = entry.Kind is ViewAllEntryKind.Drive or ViewAllEntryKind.Folder;
            if (previous.Remove(entry.FullPath, out var kept) && kept.IsDirectory == isDirectory)
            {
                kept.Entry = entry;
                _all.Add(kept);
                continue;
            }

            var added = new FolderListItem(entry) { IsNew = true };
            _newRows.Add((added, now));
            _all.Add(added);
        }

        _isTruncated = snapshot.IsTruncated;
        CountText = $"{_all.Count:N0}{(_isTruncated ? "+" : string.Empty)}";
        var rows = OrderedRows(out var typeNamesPending);
        MergeRows(rows);
        UpdateEmptyText();
        EndNewRowsLater();

        if (typeNamesPending)
        {
            _ = ReorderWhenTypeNamesArriveAsync();
        }
    }

    /// <summary>
    /// Brings the rows shown to <paramref name="rows"/> with as few steps as
    /// the list box has to be told of: the rows that went are taken out, a row
    /// that moved - its date changed under an order by date - is taken out and
    /// put back in its place, and the new ones are put in where they belong.
    /// A row kept is never touched, so the scroll stays where it was and what
    /// is lit stays lit.  A change bigger than <see cref="MaximumMergedRows"/>
    /// refills the list in one go instead.
    /// </summary>
    private void MergeRows(List<FolderListItem> rows)
    {
        var shown = _rows;
        var wanted = new Dictionary<FolderListItem, int>(rows.Count, ReferenceEqualityComparer.Instance);
        for (var index = 0; index < rows.Count; index++)
        {
            wanted[rows[index]] = index;
        }

        // Which shown rows stay where they are: those still wanted, in the
        // order they are wanted in.  A kept row behind one that is now after
        // it has moved.
        var leaving = new HashSet<FolderListItem>(ReferenceEqualityComparer.Instance);
        var furthest = -1;
        foreach (var row in shown)
        {
            if (!wanted.TryGetValue(row, out var place) || place < furthest)
            {
                leaving.Add(row);
                continue;
            }

            furthest = place;
        }

        var arriving = rows.Count - (shown.Count - leaving.Count);
        if (shown.Count == 0 || leaving.Count + arriving > MaximumMergedRows)
        {
            LiveRefills++;
            ReplaceRows(rows);
            return;
        }

        for (var index = shown.Count - 1; index >= 0; index--)
        {
            if (leaving.Contains(shown[index]))
            {
                shown.RemoveAt(index);
            }
        }

        var litArrived = false;
        for (var index = 0; index < rows.Count; index++)
        {
            if (index < shown.Count && ReferenceEquals(shown[index], rows[index]))
            {
                continue;
            }

            shown.Insert(index, rows[index]);
            litArrived |= SharedSelection?.Contains(rows[index].FullPath) == true;
        }

        // A row that is selected and only just came - or moved - has to be lit;
        // the list box lights what it is told to.
        if (litArrived)
        {
            SelectionRowsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Ends the fade of the rows that came with this change a moment from now.
    /// Each row's moment is its own, counted from when it came: a timer put
    /// back to the start by every change never ran out while a folder kept
    /// changing - a download, a log - and every row added kept fading.
    /// </summary>
    private void EndNewRowsLater()
    {
        if (_newRows.Count == 0)
        {
            return;
        }

        if (_newRowsTimer is null)
        {
            _newRowsTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = NewRowTime };
            _newRowsTimer.Tick += (_, _) =>
            {
                _newRowsTimer!.Stop();
                var now = Environment.TickCount64;
                var ended = 0;
                while (ended < _newRows.Count && now - _newRows[ended].Since >= (long)NewRowTime.TotalMilliseconds)
                {
                    _newRows[ended].Row.IsNew = false;
                    ended++;
                }

                _newRows.RemoveRange(0, ended);
                if (_newRows.Count > 0)
                {
                    // Oldest first: the next to end is the first left.
                    _newRowsTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _newRows[0].Since + (long)NewRowTime.TotalMilliseconds - now));
                    _newRowsTimer.Start();
                }
            };
        }

        if (!_newRowsTimer.IsEnabled)
        {
            _newRowsTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _newRows[0].Since + (long)NewRowTime.TotalMilliseconds - Environment.TickCount64));
            _newRowsTimer.Start();
        }
    }

    /// <summary>
    /// The list's folder went from disk.  The list goes to the nearest folder
    /// above it that is still there, as Explorer does when the folder it shows
    /// is deleted - the list alone: taken along, the canvas would select that
    /// folder, for the next Delete to recycle it whole, and what the canvas
    /// had selected in the folder that went is the tree's to let go of.  So
    /// is a folder found gone as it is read (<see cref="ReloadAsync"/>) left.
    /// A folder only renamed or replaced in the same place and back already
    /// is simply read again.  One still there that a read could not list
    /// (<paramref name="failure"/>) - a dangling junction, a
    /// folder taken out of reach - is tried once more a moment later, and
    /// failing again says so and waits for the next change: read again at
    /// once, it would fail again at once, over and over.  With nothing above
    /// it there either - its drive or share went with it - the list keeps no
    /// rows that lead nowhere.
    /// </summary>
    private async Task LeaveGoneFolderAsync(string path, Exception? failure = null)
    {
        var generation = _loadGeneration;
        string? nearest;
        try
        {
            nearest = await Task.Run(() =>
            {
                if (Directory.Exists(path))
                {
                    return path;
                }

                for (var parent = ParentOf(path); parent is not null; parent = ParentOf(parent))
                {
                    if (Directory.Exists(parent))
                    {
                        return parent;
                    }
                }

                return null;
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (!string.Equals(path, FolderPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // A full read since - F5, another order - says for itself what it found.
        var readSince = generation != _loadGeneration || _load is not null;
        if (string.Equals(nearest, path, StringComparison.OrdinalIgnoreCase))
        {
            if (failure is null)
            {
                RefreshForChange();
            }
            else if (!readSince && !_unreadableRetried)
            {
                // Once more, a moment later: a share that blinked, a folder
                // replaced just as it was read.
                _unreadableRetried = true;
                await Task.Delay(UnreadableRetryTime);
                if (generation == _loadGeneration && _load is null && string.Equals(path, FolderPath, StringComparison.OrdinalIgnoreCase))
                {
                    RefreshForChange();
                }
            }
            else if (!readSince)
            {
                _unreadableRetried = false;
                ShowEmpty(failure is UnauthorizedAccessException ? "Access denied." : "This folder could not be read.");
            }

            return;
        }

        if (nearest is null)
        {
            if (!readSince)
            {
                ShowEmpty("This location is no longer available.");
            }

            return;
        }

        LeftGoneFolders++;
        await NavigateAsync(nearest, remember: false, moveCanvas: false);
    }

    /// <summary>
    /// No rows, and why: as a full read of the folder that fails leaves the
    /// list.  The folder's time the last good read found is let go of with
    /// its rows: a share that comes back, or a folder readable again, keeps
    /// that time, and a poll comparing with it would never read it again.
    /// </summary>
    private void ShowEmpty(string reason)
    {
        _all.Clear();
        _byPath.Clear();
        ReplaceRows([]);
        CountText = string.Empty;
        EmptyText = _noRowsText = reason;
        _listedWriteTicks = 0;
    }

    /// <summary>Times the list left a folder that went from disk, for tests.</summary>
    internal int LeftGoneFolders { get; private set; }

    /// <summary>
    /// The rows, replaced in one go, with the list box told before and after:
    /// whatever it drops from its selection meanwhile - rows filtered away,
    /// another folder - is its own doing, not the user's, and the shared
    /// selection's rows are lit again among the new ones.
    /// </summary>
    private void ReplaceRows(List<FolderListItem> rows)
    {
        RowsReplacing?.Invoke();
        try
        {
            _rows.ReplaceAll(rows);
        }
        finally
        {
            RowsReplaced?.Invoke();
        }
    }

    /// <summary>
    /// The rows, refilled in one go.  Row by row, an observable collection
    /// tells the list box about every row it gains, and the list box does work
    /// for each: refilling a folder of five thousand files that way - a new
    /// filter, a new order - was twenty milliseconds before a single row was
    /// drawn.  Refilled here, the list box hears once that everything changed
    /// and makes rows only for what is on screen.  A row still there keeps
    /// its highlight, as it would have kept its place in the list.
    /// </summary>
    private sealed class RowCollection : ObservableCollection<FolderListItem>
    {
        public void ReplaceAll(List<FolderListItem> rows)
        {
            CheckReentrancy();
            Items.Clear();
            foreach (var row in rows)
            {
                Items.Add(row);
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
