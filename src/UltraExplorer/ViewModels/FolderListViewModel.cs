using System.Collections.ObjectModel;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Services;

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
/// </summary>
public sealed class FolderListViewModel : ObservableObject
{
    /// <summary>Rows that get a real Shell icon; the rest keep the glyph.</summary>
    private const int IconBudget = 300;

    private readonly Func<string, CancellationToken, Task<ViewAllDirectorySnapshot>> _read;
    private readonly Func<string, bool, Task> _activate;
    private readonly Func<string, bool> _isOnCanvas;
    private readonly ShellIconService _icons;

    private readonly List<FolderListItem> _all = [];

    /// <summary>Folders the list has been in, newest last; what Back walks.</summary>
    private readonly List<string> _history = [];
    private CancellationTokenSource? _load;

    private string _folderPath = string.Empty;
    private string _title = "No folder";
    private string _countText = string.Empty;
    private string _filter = string.Empty;
    private string _emptyText = string.Empty;
    private bool _isVisible;
    private int _held;
    private bool _isLoading;
    private bool _isTruncated;
    private FolderListItem? _selected;

    public FolderListViewModel(
        Func<string, CancellationToken, Task<ViewAllDirectorySnapshot>> read,
        Func<string, bool, Task> activate,
        Func<string, bool> isOnCanvas,
        ShellIconService icons)
    {
        _read = read;
        _activate = activate;
        _isOnCanvas = isOnCanvas;
        _icons = icons;

        ActivateCommand = new AsyncRelayCommand<FolderListItem>(item => Activate(item, open: true));
        RevealCommand = new AsyncRelayCommand<FolderListItem>(item => Activate(item, open: false));
        OpenFirstMatchCommand = new AsyncRelayCommand(() => Activate(Items.FirstOrDefault(), open: true));
        ClearFilterCommand = new RelayCommand(() => Filter = string.Empty);
        HideCommand = new RelayCommand(() => IsVisible = false);
        UpCommand = new AsyncRelayCommand(GoUpAsync, () => CanGoUp);
        BackCommand = new AsyncRelayCommand(GoBackAsync, () => CanGoBack);
    }

    /// <summary>Rows that survive the current filter, best match first.</summary>
    public ObservableCollection<FolderListItem> Items { get; } = [];

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
            if (SetProperty(ref _isVisible, value) && value && _all.Count == 0)
            {
                // Nothing was read while it was hidden, so read it now.
                _ = ReloadAsync();
            }
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
        }
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
            // still follows the selection; the folder does not move.
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

        if (FolderPath.Length > 0)
        {
            _history.Add(FolderPath);
            if (_history.Count > 64)
            {
                _history.RemoveAt(0);
            }
        }

        FolderPath = folder;
        Title = folder.Length == 0
            ? "No folder"
            : node is { IsDirectory: true } ? node.DisplayName : node?.Parent?.DisplayName ?? folder;

        _filter = string.Empty;
        OnPropertyChanged(nameof(Filter));

        _ = ReloadAsync(node);
    }

    /// <summary>
    /// Walks the list into a folder without waiting for the canvas.  The list is
    /// its own little explorer: going up a level to look at a sibling should not
    /// mean opening that whole branch out on the canvas first.
    /// </summary>
    public async Task NavigateAsync(string path, bool remember = true)
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

        await ReloadAsync();

        // The canvas comes along.  Going into a folder already moved it - the
        // row was clicked - and going back up leaving it behind was the half of
        // the gesture that felt broken.
        await _activate(normalized, false);
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

        if (FolderPath.Length == 0 || !IsVisible)
        {
            _all.Clear();
            Items.Clear();
            CountText = string.Empty;
            EmptyText = FolderPath.Length == 0 ? "Nothing is selected." : string.Empty;
            return;
        }

        var cancellation = new CancellationTokenSource();
        _load = cancellation;
        IsLoading = true;

        try
        {
            var snapshot = await _read(FolderPath, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

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
        }
        catch (OperationCanceledException)
        {
            // A newer folder is already being read.
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            _all.Clear();
            Items.Clear();
            CountText = string.Empty;
            EmptyText = exception is UnauthorizedAccessException
                ? "Access denied."
                : "This folder could not be read.";
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
    }

    private void SelectExisting(ViewAllNodeViewModel? node)
    {
        if (node is null)
        {
            return;
        }

        var match = Items.FirstOrDefault(item =>
            string.Equals(item.FullPath, node.FullPath, StringComparison.OrdinalIgnoreCase));

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
        var query = _filter.Trim();

        IEnumerable<FolderListItem> matched = query.Length == 0
            ? _all
            : _all
                .Select(item => (Item: item, Score: FolderListMatch.Score(item.DisplayName, query)))
                .Where(pair => pair.Score != FolderListMatch.NoMatch)
                .OrderBy(pair => pair.Score)
                .ThenBy(pair => pair.Item.IsDirectory ? 0 : 1)
                .ThenBy(pair => pair.Item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(pair => pair.Item);

        Items.Clear();
        var shown = 0;
        foreach (var item in matched)
        {
            item.IsOnCanvas = _isOnCanvas(item.FullPath);
            Items.Add(item);

            if (shown < IconBudget && item.Icon is null)
            {
                // Answers synchronously when the icon is already known, so a
                // folder that has been looked at once fills in with no flicker.
                _icons.Request(item.FullPath, item.IsDirectory, icon => item.Icon = icon);
            }

            shown++;
        }

        EmptyText = Items.Count == 0
            ? _all.Count == 0 ? "This folder is empty." : $"Nothing matches “{query}”."
            : string.Empty;
    }

    private Task Activate(FolderListItem? item, bool open)
        => item is null ? Task.CompletedTask : _activate(item.FullPath, open);
}
