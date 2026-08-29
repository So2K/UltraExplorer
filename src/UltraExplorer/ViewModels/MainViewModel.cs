using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace UltraExplorer.ViewModels;

public enum CanvasMode
{
    ViewAll,
    ViewSelect
}

/// <summary>
/// The Explorer-like shell around the canvas: navigation pane, address bar,
/// command bar, search and status bar.  Everything that concerns the graph
/// itself is delegated to <see cref="ViewAllViewModel"/>.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ShellIconService _iconService = new();
    private readonly NativeShellService _shellService = new();
    private readonly WorkspaceStore _workspaceStore = new();
    private readonly FolderMarkService _marks = new();
    private readonly FileSystemService _fileSystemService;
    private readonly EverythingSearchService _everything = new();
    private readonly List<string> _navigationHistory = [];

    private CancellationTokenSource? _searchCancellation;
    private int _navigationIndex = -1;
    private bool _isInitialized;
    private bool _isDisposed;
    private bool _isNavigating;
    private string _addressText = string.Empty;
    private string _searchText = string.Empty;
    private bool _isAddressEditing;
    private bool _isSearchOpen;
    private bool _isSearchBusy;
    private string _searchStatusText = string.Empty;
    private bool _isMinimapVisible;
    private double _sidebarWidth = 240;
    private CanvasMode _mode = CanvasMode.ViewAll;
    private string? _dialogTitle;

    /// <param name="treeStatePath">
    /// Overrides where the canvas layout is saved; a picker session keeps its
    /// own so it cannot rearrange the user's workspace.
    /// </param>
    public MainViewModel(string? treeStatePath = null)
    {
        _fileSystemService = new FileSystemService(_iconService);
        Tree = new ViewAllViewModel(_marks, _iconService, treeStatePath);
        Tree.PropertyChanged += OnTreePropertyChanged;
        Tree.MessageRequested += OnTreeMessage;

        BackCommand = new RelayCommand(GoBack);
        ForwardCommand = new RelayCommand(GoForward);
        UpCommand = new AsyncRelayCommand(GoUpAsync);
        HomeCommand = new AsyncRelayCommand(GoHomeAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshActiveAsync);
        GoToAddressCommand = new AsyncRelayCommand(GoToAddressAsync);
        OpenBreadcrumbCommand = new AsyncRelayCommand<BreadcrumbSegment>(OpenBreadcrumbAsync);
        OpenSidebarItemCommand = new AsyncRelayCommand<FavoriteItemViewModel>(OpenSidebarItemAsync);

        NewFolderCommand = new AsyncRelayCommand(CreateFolderAsync);
        NewTextFileCommand = new AsyncRelayCommand(CreateTextFileAsync);
        CutCommand = new RelayCommand(() => CopySelection(true));
        CopyCommand = new RelayCommand(() => CopySelection(false));
        CopyPathCommand = new RelayCommand(CopySelectionPath);
        PasteCommand = new AsyncRelayCommand(PasteAsync);
        RenameCommand = new AsyncRelayCommand(RenameSelectionAsync);
        DuplicateCommand = new AsyncRelayCommand(DuplicateSelectionAsync);
        DeleteCommand = new AsyncRelayCommand(() => DeleteSelectionAsync(false));
        PermanentDeleteCommand = new AsyncRelayCommand(() => DeleteSelectionAsync(true));
        PropertiesCommand = new RelayCommand(ShowProperties);
        OpenCommand = new RelayCommand(OpenSelection);
        OpenWithCommand = new RelayCommand(OpenSelectionWith);
        ToggleHiddenItemsCommand = new AsyncRelayCommand(ToggleHiddenItemsAsync);
        ShowInExplorerCommand = new RelayCommand(ShowSelectionInExplorer);
        AddToFavoritesCommand = new RelayCommand(AddSelectionToFavorites);
        RemoveFavoriteCommand = new RelayCommand<FavoriteItemViewModel>(RemoveFavorite);

        SearchCommand = new AsyncRelayCommand(SearchAsync);
        CloseSearchCommand = new RelayCommand(CloseSearch);
        OpenSearchResultCommand = new AsyncRelayCommand<SearchResultViewModel>(OpenSearchResultAsync);

        FitAllCommand = new RelayCommand(() => FitAllRequested?.Invoke());
        ZoomInCommand = new RelayCommand(() => ZoomRequested?.Invoke(1.25));
        ZoomOutCommand = new RelayCommand(() => ZoomRequested?.Invoke(1 / 1.25));
        ResetZoomCommand = new RelayCommand(() => ZoomRequested?.Invoke(0));
        ToggleMinimapCommand = new RelayCommand(() => IsMinimapVisible = !IsMinimapVisible);
        CollapseAllCommand = new RelayCommand(() => Tree.CollapseAll());
        RelayoutCommand = new RelayCommand(() => Tree.RelayoutCanvas());
        HideSelectedCommand = new RelayCommand(() => Tree.HideSelected());
        ShowAllHiddenCommand = new RelayCommand(() => Tree.ShowAllHidden());
        ShowHiddenCommand = new RelayCommand<string>(path =>
        {
            if (!string.IsNullOrEmpty(path))
            {
                Tree.ShowHidden(path);
            }
        });
        SetAccentCommand = new RelayCommand<string>(SetSelectionAccent);
        EditNoteCommand = new AsyncRelayCommand(EditSelectionNoteAsync);
    }

    public ViewAllViewModel Tree { get; }
    public OperationToastService Toast { get; } = new();

    /// <summary>Home sits alone above the first divider, as in Explorer.</summary>
    public ObservableCollection<FavoriteItemViewModel> HomeItems { get; } = [];

    public ObservableCollection<FavoriteItemViewModel> QuickAccess { get; } = [];

    /// <summary>
    /// Folders the calling program pinned with <c>IFileDialog::AddPlace</c>.
    /// Empty, and the section hidden, outside a picker session.
    /// </summary>
    public ObservableCollection<FavoriteItemViewModel> PickerPlaces { get; } = [];
    public ObservableCollection<FavoriteItemViewModel> Drives { get; } = [];
    public ObservableCollection<FavoriteItemViewModel> NetworkLocations { get; } = [];
    public ObservableCollection<BreadcrumbSegment> Breadcrumbs { get; } = [];
    public ObservableCollection<SearchResultViewModel> SearchResults { get; } = [];

    public ICommand BackCommand { get; }
    public ICommand ForwardCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand HomeCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand GoToAddressCommand { get; }
    public ICommand OpenBreadcrumbCommand { get; }
    public ICommand OpenSidebarItemCommand { get; }
    public ICommand NewFolderCommand { get; }
    public ICommand NewTextFileCommand { get; }
    public ICommand CutCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand PermanentDeleteCommand { get; }
    public ICommand PropertiesCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand OpenWithCommand { get; }
    public ICommand ToggleHiddenItemsCommand { get; }
    public ICommand ShowInExplorerCommand { get; }
    public ICommand AddToFavoritesCommand { get; }
    public ICommand RemoveFavoriteCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand CloseSearchCommand { get; }
    public ICommand OpenSearchResultCommand { get; }
    public ICommand FitAllCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ResetZoomCommand { get; }
    public ICommand ToggleMinimapCommand { get; }
    public ICommand CollapseAllCommand { get; }
    public ICommand RelayoutCommand { get; }
    public ICommand HideSelectedCommand { get; }
    public ICommand ShowAllHiddenCommand { get; }
    public ICommand ShowHiddenCommand { get; }
    public ICommand SetAccentCommand { get; }
    public ICommand EditNoteCommand { get; }

    public event Action? FitAllRequested;

    /// <summary>Zoom factor, or 0 to reset the canvas to 100%.</summary>
    public event Action<double>? ZoomRequested;

    public event Func<string, string, string, string?>? PromptRequested;
    public event Func<string, string, bool>? ConfirmRequested;
    public event Action<IReadOnlyList<string>, FrameworkElement, Point>? ContextMenuRequested;

    public CanvasMode Mode
    {
        get => _mode;
        set
        {
            if (SetProperty(ref _mode, value))
            {
                OnPropertyChanged(nameof(IsViewAll));
                OnPropertyChanged(nameof(IsViewSelect));
            }
        }
    }

    public bool IsViewAll
    {
        get => _mode == CanvasMode.ViewAll;
        set
        {
            if (value)
            {
                Mode = CanvasMode.ViewAll;
            }
        }
    }

    public bool IsViewSelect
    {
        get => _mode == CanvasMode.ViewSelect;
        set
        {
            if (value)
            {
                Mode = CanvasMode.ViewSelect;
            }
        }
    }

    public string AddressText
    {
        get => _addressText;
        set => SetProperty(ref _addressText, value);
    }

    public bool IsAddressEditing
    {
        get => _isAddressEditing;
        set => SetProperty(ref _isAddressEditing, value);
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public string SearchPlaceholder
        => Tree.ActiveNode is { } node ? $"Search {node.DisplayName}" : "Search this PC";

    public bool IsSearchOpen
    {
        get => _isSearchOpen;
        private set => SetProperty(ref _isSearchOpen, value);
    }

    public bool IsSearchBusy
    {
        get => _isSearchBusy;
        private set => SetProperty(ref _isSearchBusy, value);
    }

    /// <summary>Which engine answered, so a slow search is never a mystery.</summary>
    public string SearchStatusText
    {
        get => _searchStatusText;
        private set => SetProperty(ref _searchStatusText, value);
    }

    /// <summary>True when Everything is installed and running.</summary>
    public bool IsEverythingAvailable => _everything.IsAvailable;

    public bool IsMinimapVisible
    {
        get => _isMinimapVisible;
        set => SetProperty(ref _isMinimapVisible, value);
    }

    public double SidebarWidth
    {
        get => _sidebarWidth;
        set => SetProperty(ref _sidebarWidth, value);
    }

    /// <summary>
    /// Set while acting as somebody else's file dialog, so the tab says what is
    /// being asked for rather than which folder happens to be open.
    /// </summary>
    public string? DialogTitle
    {
        get => _dialogTitle;
        set
        {
            if (SetProperty(ref _dialogTitle, value))
            {
                OnPropertyChanged(nameof(TabTitle));
            }
        }
    }

    public string TabTitle => DialogTitle is { Length: > 0 } dialog
        ? dialog
        : Tree.ActiveNode?.DisplayName is { Length: > 0 } name ? name : "This PC";

    /// <summary>Folders hidden from the canvas, newest last, for the restore menu.</summary>
    public IReadOnlyList<string> HiddenPaths => Tree.HiddenPaths;

    public int HiddenCount => Tree.HiddenCount;

    public string StatusCountText => Tree.StatusCountText;

    public string StatusPathText => Tree.StatusPathText;

    public async Task InitializeAsync(string? initialPath = null)
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;

        await _marks.LoadAsync();
        var state = await _workspaceStore.LoadAsync();
        if (state is not null)
        {
            SidebarWidth = Math.Clamp(state.SidebarWidth <= 0 ? 240 : state.SidebarWidth, 190, 340);
            IsMinimapVisible = state.IsMinimapVisible;
            foreach (var legacy in state.Nodes)
            {
                _marks.Seed(legacy.Path, legacy.AccentHex, legacy.Note);
            }
        }

        var quickAccess = _fileSystemService.GetQuickAccess();
        foreach (var item in quickAccess)
        {
            if (item.Name == "Home")
            {
                HomeItems.Add(item);
            }
            else
            {
                QuickAccess.Add(item);
            }
        }

        if (state is not null)
        {
            foreach (var favorite in state.Favorites.Where(favorite => Directory.Exists(favorite.Path)))
            {
                if (HomeItems.Concat(QuickAccess).All(existing => !ViewAllPath.Equals(existing.Path, favorite.Path)))
                {
                    QuickAccess.Add(new FavoriteItemViewModel
                    {
                        Name = favorite.Name,
                        Path = favorite.Path,
                        Glyph = favorite.Glyph,
                        AccentHex = favorite.AccentHex,
                        IsCustom = true
                    });
                }
            }
        }

        _fileSystemService.AttachIcons(HomeItems.Concat(QuickAccess).ToArray());

        foreach (var drive in _fileSystemService.GetDrives())
        {
            Drives.Add(drive);
        }

        foreach (var location in _fileSystemService.GetNetworkLocations())
        {
            NetworkLocations.Add(location);
        }

        await Tree.InitializeAsync(initialPath);
        UpdateBreadcrumbs();
        UpdateSidebarSelection();
    }

    public void ShowContextMenuFor(IReadOnlyList<string> paths, FrameworkElement origin, Point point)
        => ContextMenuRequested?.Invoke(paths, origin, point);

    public async Task DropIntoPathAsync(
        IReadOnlyList<string> paths,
        string targetDirectory,
        ModifierKeys modifiers)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var move = ShouldMove(paths, targetDirectory, modifiers);
        await TransferAsync(paths, targetDirectory, move, move ? "Moving" : "Copying");
    }

    /// <summary>
    /// Explorer's rule, used both for the drag cursor and for the operation that
    /// actually runs, so the two can never disagree: Ctrl copies, Shift moves,
    /// otherwise same volume moves and a different volume copies.
    /// </summary>
    public static bool ShouldMove(IReadOnlyList<string> paths, string targetDirectory, ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            return true;
        }

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            return false;
        }

        return paths.All(path => NativeShellService.IsSameVolume(path, targetDirectory));
    }

    public async Task SaveNowAsync()
    {
        if (!_isInitialized)
        {
            return;
        }

        var state = new WorkspaceState
        {
            SidebarWidth = SidebarWidth,
            IsMinimapVisible = IsMinimapVisible,
            Favorites = QuickAccess
                .Where(favorite => favorite.IsCustom)
                .Select(favorite => new FavoriteState(favorite.Name, favorite.Path, favorite.Glyph, favorite.AccentHex))
                .ToList()
        };

        try
        {
            await _workspaceStore.SaveAsync(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        await Tree.SaveAsync();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        Tree.PropertyChanged -= OnTreePropertyChanged;
        Tree.MessageRequested -= OnTreeMessage;
        Tree.Dispose();
    }

    private void OnTreePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewAllViewModel.StatusCountText):
                OnPropertyChanged(nameof(StatusCountText));
                break;
            case nameof(ViewAllViewModel.StatusPathText):
                OnPropertyChanged(nameof(StatusPathText));
                break;
            case nameof(ViewAllViewModel.HiddenPaths):
                OnPropertyChanged(nameof(HiddenPaths));
                break;
            case nameof(ViewAllViewModel.HiddenCount):
                OnPropertyChanged(nameof(HiddenCount));
                break;
            case nameof(ViewAllViewModel.ActivePath):
                OnPropertyChanged(nameof(TabTitle));
                OnPropertyChanged(nameof(SearchPlaceholder));
                AddressText = Tree.ActivePath;
                UpdateBreadcrumbs();
                UpdateSidebarSelection();
                RecordNavigation(Tree.ActivePath);
                break;
        }
    }

    private void OnTreeMessage(string message, bool isError)
    {
        if (isError)
        {
            Toast.ShowError(message);
        }
        else
        {
            _ = Toast.ShowSuccessAsync(message);
        }
    }

    private void UpdateBreadcrumbs()
    {
        Breadcrumbs.Clear();
        var path = Tree.ActivePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IReadOnlyList<string> chain;
        try
        {
            chain = ViewAllPath.AncestorChain(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        for (var index = 0; index < chain.Count; index++)
        {
            var segment = chain[index];
            var name = Path.GetFileName(segment.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(name))
            {
                name = segment;
            }

            Breadcrumbs.Add(new BreadcrumbSegment(name, segment, index == chain.Count - 1));
        }
    }

    private void UpdateSidebarSelection()
    {
        var active = Tree.ActivePath;
        foreach (var item in HomeItems.Concat(QuickAccess).Concat(Drives).Concat(NetworkLocations))
        {
            item.IsActive = !item.OpensInShell
                && !string.IsNullOrEmpty(active)
                && ViewAllPath.Equals(item.Path, active);
        }
    }

    private void RecordNavigation(string path)
    {
        if (_isNavigating || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (_navigationIndex >= 0
            && _navigationIndex < _navigationHistory.Count
            && ViewAllPath.Equals(_navigationHistory[_navigationIndex], path))
        {
            return;
        }

        if (_navigationIndex < _navigationHistory.Count - 1)
        {
            _navigationHistory.RemoveRange(_navigationIndex + 1, _navigationHistory.Count - _navigationIndex - 1);
        }

        _navigationHistory.Add(path);
        if (_navigationHistory.Count > 100)
        {
            _navigationHistory.RemoveAt(0);
        }

        _navigationIndex = _navigationHistory.Count - 1;
    }

    private void GoBack()
    {
        if (_navigationIndex <= 0)
        {
            return;
        }

        _navigationIndex--;
        _ = NavigateHistoryAsync(_navigationHistory[_navigationIndex]);
    }

    private void GoForward()
    {
        if (_navigationIndex >= _navigationHistory.Count - 1)
        {
            return;
        }

        _navigationIndex++;
        _ = NavigateHistoryAsync(_navigationHistory[_navigationIndex]);
    }

    private async Task NavigateHistoryAsync(string path)
    {
        _isNavigating = true;
        try
        {
            await Tree.RevealPathAsync(path);
        }
        finally
        {
            _isNavigating = false;
        }
    }

    private async Task GoUpAsync()
    {
        var current = Tree.ActivePath;
        if (string.IsNullOrWhiteSpace(current))
        {
            return;
        }

        var parent = Path.GetDirectoryName(current);
        if (!string.IsNullOrEmpty(parent))
        {
            await Tree.RevealPathAsync(parent);
        }
    }

    private async Task GoHomeAsync()
        => await Tree.RevealPathAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private async Task RefreshActiveAsync()
    {
        if (Tree.ActiveNode is { } node)
        {
            await Tree.RefreshAsync(node.IsDirectory ? node : node.Parent ?? node);
        }
    }

    private async Task GoToAddressAsync()
    {
        var expanded = Environment.ExpandEnvironmentVariables(AddressText.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(expanded))
        {
            return;
        }

        if (File.Exists(expanded))
        {
            await Tree.RevealPathAsync(expanded);
            return;
        }

        if (!Directory.Exists(expanded))
        {
            Toast.ShowError("That location does not exist.");
            return;
        }

        IsAddressEditing = false;
        await Tree.RevealPathAsync(expanded);
    }

    private async Task OpenBreadcrumbAsync(BreadcrumbSegment? segment)
    {
        if (segment is not null)
        {
            await Tree.RevealPathAsync(segment.FullPath);
        }
    }

    private async Task OpenSidebarItemAsync(FavoriteItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.OpensInShell)
        {
            try
            {
                NativeShellService.Open(item.Path);
            }
            catch (Exception ex)
            {
                Toast.ShowError(ex.Message);
            }

            return;
        }

        if (item.Kind == SidebarItemKind.Network)
        {
            await Tree.AddRootAsync(item.Path);
            return;
        }

        await Tree.RevealPathAsync(item.Path);
    }

    private async Task CreateFolderAsync()
    {
        if (Tree.TargetDirectory is not { } target)
        {
            Toast.ShowError("Select a folder on the canvas first.");
            return;
        }

        var name = PromptRequested?.Invoke("New folder", "Folder name", "New folder");
        if (name is null)
        {
            return;
        }

        try
        {
            var path = NativeShellService.CreateFolder(target, name);
            await Tree.RefreshPathAsync(target);
            await Toast.ShowSuccessAsync($"Created {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Toast.ShowError(ex.Message);
        }
    }

    private async Task CreateTextFileAsync()
    {
        if (Tree.TargetDirectory is not { } target)
        {
            Toast.ShowError("Select a folder on the canvas first.");
            return;
        }

        var name = PromptRequested?.Invoke("New text file", "File name", "New note.txt");
        if (name is null)
        {
            return;
        }

        try
        {
            var path = NativeShellService.CreateNoteFile(target, name);
            await Tree.RefreshPathAsync(target);
            NativeShellService.Open(path);
        }
        catch (Exception ex)
        {
            Toast.ShowError(ex.Message);
        }
    }

    private void CopySelection(bool cut)
    {
        var paths = Tree.SelectedPaths;
        if (paths.Count == 0)
        {
            return;
        }

        if (!NativeShellService.CopyPathsToClipboard(paths, cut))
        {
            Toast.ShowError("Another application is holding the clipboard — try again.");
            return;
        }

        _ = Toast.ShowSuccessAsync(cut ? $"Cut {paths.Count} item(s)" : $"Copied {paths.Count} item(s)");
    }

    private void CopySelectionPath()
    {
        var quoted = string.Join(Environment.NewLine, Tree.SelectedOrActivePaths.Select(path => $"\"{path}\""));
        if (string.IsNullOrWhiteSpace(quoted))
        {
            return;
        }

        try
        {
            Clipboard.SetText(quoted);
            _ = Toast.ShowSuccessAsync("Path copied");
        }
        catch (Exception ex)
        {
            Toast.ShowError(ex.Message);
        }
    }

    private async Task PasteAsync()
    {
        if (Tree.TargetDirectory is not { } target)
        {
            Toast.ShowError("Select a folder on the canvas first.");
            return;
        }

        var payload = NativeShellService.GetClipboardPayload();
        if (payload is null || payload.Paths.Length == 0)
        {
            Toast.ShowError("The clipboard does not contain files or folders.");
            return;
        }

        await TransferAsync(payload.Paths, target, payload.Cut, payload.Cut ? "Moving" : "Copying");
    }

    private async Task TransferAsync(IReadOnlyList<string> paths, string targetDirectory, bool move, string verb)
    {
        var safePaths = paths
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Where(path => !NativeShellService.IsInvalidMoveTarget(path, targetDirectory))
            .ToArray();
        if (safePaths.Length == 0)
        {
            Toast.ShowError("This drop target is not valid for the selected item(s).");
            return;
        }

        var sourceDirectories = safePaths
            .Select(Path.GetDirectoryName)
            .Where(directory => !string.IsNullOrEmpty(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        try
        {
            Toast.ShowBusy($"{verb} {safePaths.Length} item(s)…");
            await _shellService.CopyOrMoveAsync(safePaths, targetDirectory, move);
            await Tree.RefreshPathAsync(targetDirectory);
            if (move)
            {
                foreach (var directory in sourceDirectories)
                {
                    await Tree.RefreshPathAsync(directory!);
                }
            }

            var targetName = Path.GetFileName(targetDirectory.TrimEnd(Path.DirectorySeparatorChar));
            await Toast.ShowSuccessAsync(
                $"{(move ? "Moved" : "Copied")} {safePaths.Length} item(s) to {(string.IsNullOrEmpty(targetName) ? targetDirectory : targetName)}");
        }
        catch (OperationCanceledException)
        {
            await RefreshAfterOperationAsync(targetDirectory, sourceDirectories);
            await Toast.ShowSuccessAsync($"{(move ? "Move" : "Copy")} cancelled");
        }
        catch (Exception ex)
        {
            Toast.ShowError(ex.Message);
        }
    }

    private async Task RefreshAfterOperationAsync(string? targetDirectory, IEnumerable<string?> sourceDirectories)
    {
        if (!string.IsNullOrEmpty(targetDirectory))
        {
            await Tree.RefreshPathAsync(targetDirectory);
        }

        foreach (var directory in sourceDirectories.Where(directory => !string.IsNullOrEmpty(directory)))
        {
            await Tree.RefreshPathAsync(directory!);
        }
    }

    private async Task RenameSelectionAsync()
    {
        var paths = Tree.SelectedPaths;
        if (paths.Count != 1)
        {
            Toast.ShowError("Select exactly one item to rename.");
            return;
        }

        var path = paths[0];
        var currentName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        var newName = PromptRequested?.Invoke("Rename", "Enter a new name", currentName);
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, currentName, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var renamed = NativeShellService.Rename(path, newName);
            var parent = Path.GetDirectoryName(renamed);
            if (!string.IsNullOrEmpty(parent))
            {
                await Tree.RefreshPathAsync(parent);
                await Tree.RevealPathAsync(renamed, focus: false);
            }

            await Toast.ShowSuccessAsync($"Renamed to {Path.GetFileName(renamed)}");
        }
        catch (Exception ex)
        {
            Toast.ShowError(ex.Message);
        }
    }

    private async Task DuplicateSelectionAsync()
    {
        var paths = Tree.SelectedPaths;
        if (paths.Count == 0)
        {
            return;
        }

        try
        {
            Toast.ShowBusy($"Duplicating {paths.Count} item(s)…");
            foreach (var path in paths)
            {
                await _shellService.DuplicateAsync(path);
            }

            foreach (var directory in paths.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(directory))
                {
                    await Tree.RefreshPathAsync(directory);
                }
            }

            await Toast.ShowSuccessAsync($"Duplicated {paths.Count} item(s)");
        }
        catch (OperationCanceledException)
        {
            await RefreshAfterOperationAsync(null, paths.Select(Path.GetDirectoryName));
            await Toast.ShowSuccessAsync("Duplicate cancelled");
        }
        catch (Exception ex)
        {
            Toast.ShowError(ex.Message);
        }
    }

    private async Task DeleteSelectionAsync(bool permanently)
    {
        var paths = Tree.SelectedPaths;
        if (paths.Count == 0)
        {
            return;
        }

        if (permanently
            && ConfirmRequested?.Invoke("Permanently delete", $"Permanently delete {paths.Count} item(s)? This cannot be undone.") != true)
        {
            return;
        }

        var parents = paths
            .Select(Path.GetDirectoryName)
            .Where(directory => !string.IsNullOrEmpty(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        try
        {
            Toast.ShowBusy(permanently ? "Deleting permanently…" : "Moving to Recycle Bin…");
            await _shellService.DeleteAsync(paths, permanently);
            foreach (var parent in parents)
            {
                await Tree.RefreshPathAsync(parent!);
            }

            await Toast.ShowSuccessAsync(permanently ? "Deleted" : "Moved to Recycle Bin");
        }
        catch (OperationCanceledException)
        {
            await RefreshAfterOperationAsync(null, parents);
            await Toast.ShowSuccessAsync("Delete cancelled");
        }
        catch (Exception ex)
        {
            Toast.ShowError(ex.Message);
        }
    }

    private void ShowProperties()
    {
        if (Tree.SelectedOrActivePaths.FirstOrDefault() is { } path)
        {
            try
            {
                NativeShellService.ShowProperties(path);
            }
            catch (Exception ex)
            {
                Toast.ShowError(ex.Message);
            }
        }
    }

    private void OpenSelection()
    {
        if (Tree.ActiveNode is { } node)
        {
            if (node.IsDirectory)
            {
                _ = Tree.ToggleAsync(node);
            }
            else
            {
                Tree.OpenInDefaultApplication(node);
            }
        }
    }

    private void OpenSelectionWith()
    {
        if (Tree.SelectedOrActivePaths.FirstOrDefault() is { } path && File.Exists(path))
        {
            try
            {
                NativeShellService.OpenWith(path);
            }
            catch (Exception ex)
            {
                Toast.ShowError(ex.Message);
            }
        }
        else
        {
            Toast.ShowError("Select a file first.");
        }
    }

    private async Task ToggleHiddenItemsAsync()
        => await Tree.SetShowHiddenItemsAsync(!Tree.ShowHiddenItems);

    private void ShowSelectionInExplorer()
    {
        if (Tree.SelectedOrActivePaths.FirstOrDefault() is { } path)
        {
            try
            {
                NativeShellService.ShowInExplorer(path);
            }
            catch (Exception ex)
            {
                Toast.ShowError(ex.Message);
            }
        }
    }

    private void AddSelectionToFavorites()
    {
        foreach (var path in Tree.SelectedOrActivePaths.Where(Directory.Exists))
        {
            if (QuickAccess.Any(item => ViewAllPath.Equals(item.Path, path)))
            {
                continue;
            }

            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            var item = new FavoriteItemViewModel
            {
                Name = string.IsNullOrEmpty(name) ? path : name,
                Path = path,
                Glyph = "\uE8B7",
                AccentHex = "#E3B341",
                IsCustom = true
            };
            _fileSystemService.AttachIcons([item]);
            QuickAccess.Add(item);
        }

        _ = SaveNowAsync();
    }

    private void RemoveFavorite(FavoriteItemViewModel? favorite)
    {
        if (favorite is { IsCustom: true })
        {
            QuickAccess.Remove(favorite);
            _ = SaveNowAsync();
        }
    }

    private void SetSelectionAccent(string? accentHex)
    {
        var nodes = Tree.SelectedNodes.Count > 0
            ? Tree.SelectedNodes.ToArray()
            : Tree.ActiveNode is null ? [] : [Tree.ActiveNode];
        if (nodes.Length == 0)
        {
            return;
        }

        Tree.ApplyAccent(nodes, string.IsNullOrEmpty(accentHex) ? null : accentHex);
    }

    private Task EditSelectionNoteAsync()
    {
        if (Tree.ActiveNode is not { } node)
        {
            return Task.CompletedTask;
        }

        var note = PromptRequested?.Invoke("Note", $"Note for {node.DisplayName}", node.Note);
        if (note is not null)
        {
            Tree.ApplyNote(node, note);
        }

        return Task.CompletedTask;
    }

    private async Task SearchAsync()
    {
        var query = SearchText.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            CloseSearch();
            return;
        }

        var root = Tree.ActiveNode is { IsDirectory: true } node
            ? node.FullPath
            : Path.GetDirectoryName(Tree.ActivePath);
        if (string.IsNullOrWhiteSpace(root))
        {
            Toast.ShowError("Select a folder to search in.");
            return;
        }

        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;

        IsSearchOpen = true;
        IsSearchBusy = true;
        SearchResults.Clear();

        // Everything has already read the file table of every volume, so it
        // answers a query outright.  Walking the tree is the fallback, and the
        // difference on a folder like C:\Windows is seconds against nothing.
        if (_everything.IsAvailable)
        {
            SearchStatusText = "Asking Everything…";
            try
            {
                var found = await _everything.SearchAsync(query, root, 1_000, token);
                if (!token.IsCancellationRequested)
                {
                    foreach (var result in found)
                    {
                        SearchResults.Add(result);
                    }

                    SearchStatusText = found.Count > 0
                        ? $"Everything · {found.Count} result(s) under {Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))}"
                        : $"Everything found nothing under {Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))}";
                    IsSearchBusy = false;
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                // A broken index or a version mismatch is a reason to fall back,
                // not a reason to fail the search.
                SearchStatusText = $"Everything failed ({exception.Message}) — walking the folder tree instead.";
            }
        }
        else
        {
            SearchStatusText = $"Walking the folder tree. {_everything.UnavailableReason}";
        }

        // Progress<T> marshals back to the UI thread, so matches appear while the
        // walk is still running instead of all at once at the end.
        var progress = new Progress<SearchResultViewModel>(result =>
        {
            if (!token.IsCancellationRequested)
            {
                SearchResults.Add(result);
            }
        });

        try
        {
            await _fileSystemService.SearchAsync(root, query, 200, progress, token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsSearchBusy = false;
                if (!_everything.IsAvailable)
                {
                    SearchStatusText = $"{SearchResults.Count} result(s) — walked the folder tree. {_everything.UnavailableReason}";
                }
            }
        }
    }

    private void CloseSearch()
    {
        _searchCancellation?.Cancel();
        IsSearchOpen = false;
        IsSearchBusy = false;
        SearchResults.Clear();
    }

    private async Task OpenSearchResultAsync(SearchResultViewModel? result)
    {
        if (result is null)
        {
            return;
        }

        await Tree.RevealPathAsync(result.FullPath);
        CloseSearch();
    }
}
