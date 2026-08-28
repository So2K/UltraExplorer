using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace UltraExplorer.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ShellIconService _iconService = new();
    private readonly NativeShellService _shellService = new();
    private readonly WorkspaceStore _workspaceStore = new();
    private readonly FileSystemService _fileSystemService;
    private readonly Dictionary<string, FolderNodeViewModel> _nodesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, CancellationTokenSource> _nodeLoads = [];
    private readonly List<string> _navigationHistory = [];
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _saveDebounce;
    private int _navigationIndex = -1;
    private FolderNodeViewModel? _activeNode;
    private Point _viewportLocation;
    private double _viewportZoom = 1;
    private string _addressText = string.Empty;
    private string _searchText = string.Empty;
    private string _statusText = "Ready";
    private string _statusDetail = string.Empty;
    private bool _isSearchOpen;
    private bool _isSearchBusy;
    private bool _isInitialized;
    private bool _showHiddenItems = true;

    public MainViewModel()
    {
        _fileSystemService = new FileSystemService(_iconService);
        SelectedNodes.CollectionChanged += SelectedNodesOnCollectionChanged;

        BrowseCommand = new AsyncRelayCommand(BrowseAsync);
        GoToAddressCommand = new AsyncRelayCommand(GoToAddressAsync);
        BackCommand = new RelayCommand(GoBack);
        ForwardCommand = new RelayCommand(GoForward);
        UpCommand = new AsyncRelayCommand(GoUpAsync);
        HomeCommand = new AsyncRelayCommand(GoHomeAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshActiveAsync);
        FitAllCommand = new RelayCommand(() => FitAllRequested?.Invoke());
        ZoomInCommand = new RelayCommand(() => ZoomRequested?.Invoke(1.2));
        ZoomOutCommand = new RelayCommand(() => ZoomRequested?.Invoke(1 / 1.2));
        ResetZoomCommand = new RelayCommand(() => ViewportZoom = 1);
        CopyCommand = new RelayCommand(() => CopySelection(false));
        CutCommand = new RelayCommand(() => CopySelection(true));
        PasteCommand = new AsyncRelayCommand(PasteAsync);
        DeleteCommand = new AsyncRelayCommand(() => DeleteSelectionAsync(false));
        PermanentDeleteCommand = new AsyncRelayCommand(() => DeleteSelectionAsync(true));
        DuplicateCommand = new AsyncRelayCommand(DuplicateSelectionAsync);
        RenameCommand = new AsyncRelayCommand(RenameSelectionAsync);
        NewFolderCommand = new AsyncRelayCommand(CreateFolderAsync);
        NewNoteFileCommand = new AsyncRelayCommand(CreateNoteFileAsync);
        SearchCommand = new AsyncRelayCommand(SearchAsync);
        CloseSearchCommand = new RelayCommand(CloseSearch);
    }

    public ObservableCollection<FolderNodeViewModel> Nodes { get; } = [];
    public ObservableCollection<FolderConnectionViewModel> Connections { get; } = [];
    public ObservableCollection<FolderNodeViewModel> SelectedNodes { get; } = [];
    public ObservableCollection<FavoriteItemViewModel> Favorites { get; } = [];
    public ObservableCollection<FavoriteItemViewModel> Drives { get; } = [];
    public ObservableCollection<SearchResultViewModel> SearchResults { get; } = [];
    public OperationToastService Toast { get; } = new();

    public FolderNodeViewModel? ActiveNode
    {
        get => _activeNode;
        private set
        {
            if (SetProperty(ref _activeNode, value))
            {
                AddressText = value?.FullPath ?? string.Empty;
                UpdateStatus();
                OnPropertyChanged(nameof(HasActiveNode));
            }
        }
    }

    public bool HasActiveNode => ActiveNode is not null;

    public Point ViewportLocation
    {
        get => _viewportLocation;
        set
        {
            if (SetProperty(ref _viewportLocation, value))
            {
                ScheduleSave();
            }
        }
    }

    public double ViewportZoom
    {
        get => _viewportZoom;
        set
        {
            if (SetProperty(ref _viewportZoom, value))
            {
                OnPropertyChanged(nameof(ZoomLabel));
                ScheduleSave();
            }
        }
    }

    public string ZoomLabel => $"{ViewportZoom:P0}";

    public string AddressText
    {
        get => _addressText;
        set => SetProperty(ref _addressText, value);
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

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

    public bool ShowHiddenItems
    {
        get => _showHiddenItems;
        set
        {
            if (SetProperty(ref _showHiddenItems, value))
            {
                OnPropertyChanged(nameof(VisibleNodes));
            }
        }
    }

    public IEnumerable<FolderNodeViewModel> VisibleNodes => Nodes;

    public ICommand BrowseCommand { get; }
    public ICommand GoToAddressCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand ForwardCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand HomeCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand FitAllCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ResetZoomCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand CutCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand PermanentDeleteCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand NewFolderCommand { get; }
    public ICommand NewNoteFileCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand CloseSearchCommand { get; }

    public event Action<FolderNodeViewModel, bool>? FocusNodeRequested;
    public event Action? FitAllRequested;
    public event Action<double>? ZoomRequested;
    public event Func<string, string, string, string?>? PromptRequested;
    public event Func<string, string, bool>? ConfirmRequested;

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        foreach (var favorite in FileSystemService.GetSystemFavorites())
        {
            Favorites.Add(favorite);
        }

        foreach (var drive in FileSystemService.GetDrives())
        {
            Drives.Add(drive);
        }

        var state = await _workspaceStore.LoadAsync();
        if (state is not null)
        {
            ViewportLocation = new Point(state.ViewportX, state.ViewportY);
            ViewportZoom = Math.Clamp(state.ViewportZoom, 0.1, 4);

            foreach (var favorite in state.Favorites.Where(favorite => Directory.Exists(favorite.Path)))
            {
                if (Favorites.All(existing => !PathsEqual(existing.Path, favorite.Path)))
                {
                    Favorites.Add(new FavoriteItemViewModel
                    {
                        Name = favorite.Name,
                        Path = favorite.Path,
                        Glyph = favorite.Glyph,
                        AccentHex = favorite.AccentHex,
                        IsCustom = true
                    });
                }
            }

            var restored = new Dictionary<Guid, FolderNodeViewModel>();
            foreach (var savedNode in state.Nodes.Where(node => Directory.Exists(node.Path)).Take(80))
            {
                var node = CreateNode(savedNode.Path, new Point(savedNode.X, savedNode.Y), savedNode.Id);
                node.AccentHex = savedNode.AccentHex;
                node.Note = savedNode.Note;
                restored[node.Id] = node;
            }

            foreach (var savedConnection in state.Connections)
            {
                if (restored.TryGetValue(savedConnection.SourceId, out var source)
                    && restored.TryGetValue(savedConnection.TargetId, out var target))
                {
                    Connections.Add(new FolderConnectionViewModel(source, target));
                }
            }

            await Task.WhenAll(restored.Values.Select(node => LoadNodeAsync(node)));
            ActiveNode = Nodes.FirstOrDefault();
        }

        if (Nodes.Count == 0)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var node = CreateNode(home, new Point(90, 80));
            await LoadNodeAsync(node);
            ActivateNode(node, true, false);
        }

        UpdateStatus();
    }

    public async Task<FolderNodeViewModel?> OpenFolderAsync(
        string path,
        FolderNodeViewModel? parent = null,
        bool focus = true,
        bool animated = true)
    {
        string normalized;
        try
        {
            normalized = NormalizePath(path);
        }
        catch (Exception ex)
        {
            Toast.ShowError(ex.Message);
            return null;
        }

        if (!Directory.Exists(normalized))
        {
            Toast.ShowError("This folder is unavailable or no longer exists.");
            return null;
        }

        if (_nodesByPath.TryGetValue(normalized, out var existing))
        {
            if (parent is not null && Connections.All(connection => connection.Source != parent || connection.Target != existing))
            {
                Connections.Add(new FolderConnectionViewModel(parent, existing));
            }

            if (focus)
            {
                ActivateNode(existing, true, animated);
            }

            return existing;
        }

        var location = parent is null
            ? GetViewportSpawnLocation()
            : GetChildLocation(parent);
        var node = CreateNode(normalized, location);
        if (parent is not null)
        {
            Connections.Add(new FolderConnectionViewModel(parent, node));
        }

        await LoadNodeAsync(node);
        if (focus)
        {
            ActivateNode(node, true, animated);
        }

        ScheduleSave();
        return node;
    }

    public async Task ExpandItemAsync(FolderNodeViewModel parent, FileItemViewModel item, bool focus)
    {
        ActivateNode(parent, false, false);
        if (item.IsDirectory)
        {
            await OpenFolderAsync(item.FullPath, parent, focus);
            return;
        }

        try
        {
            NativeShellService.Open(item.FullPath);
        }
        catch (Exception ex)
        {
            Toast.ShowError($"Could not open {item.Name}: {ex.Message}");
        }
    }

    public void ActivateNode(FolderNodeViewModel node, bool recordHistory = true, bool animated = false)
    {
        ActiveNode = node;
        if (recordHistory)
        {
            RecordNavigation(node.FullPath);
        }

        FocusNodeRequested?.Invoke(node, animated);
    }

    public async Task OpenFavoriteAsync(FavoriteItemViewModel favorite)
        => await OpenFolderAsync(favorite.Path, focus: true, animated: true);

    public void AddFavorite(string path)
    {
        if (!Directory.Exists(path) || Favorites.Any(item => PathsEqual(item.Path, path)))
        {
            return;
        }

        Favorites.Add(new FavoriteItemViewModel
        {
            Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : path,
            Path = path,
            Glyph = "\uE8B7",
            AccentHex = "#F1B84B",
            IsCustom = true
        });
        ScheduleSave();
    }

    public void RemoveFavorite(FavoriteItemViewModel favorite)
    {
        if (favorite.IsCustom)
        {
            Favorites.Remove(favorite);
            ScheduleSave();
        }
    }

    public async Task RefreshNodeAsync(FolderNodeViewModel node)
        => await LoadNodeAsync(node);

    public void CloseNode(FolderNodeViewModel node)
    {
        var descendants = new HashSet<FolderNodeViewModel>();
        CollectExclusiveDescendants(node, descendants);
        descendants.Add(node);

        foreach (var connection in Connections.Where(connection => descendants.Contains(connection.Source) || descendants.Contains(connection.Target)).ToArray())
        {
            Connections.Remove(connection);
        }

        foreach (var remove in descendants)
        {
            _nodeLoads.Remove(remove.Id, out var load);
            load?.Cancel();
            load?.Dispose();
            remove.Dispose();
            Nodes.Remove(remove);
            _nodesByPath.Remove(NormalizePath(remove.FullPath));
        }

        if (ActiveNode is not null && descendants.Contains(ActiveNode))
        {
            ActiveNode = Nodes.FirstOrDefault();
        }

        ScheduleSave();
    }

    public void SetNodeAccent(FolderNodeViewModel node, string accentHex)
    {
        node.AccentHex = accentHex;
        ScheduleSave();
    }

    public void ToggleNodeNote(FolderNodeViewModel node)
    {
        node.IsNoteVisible = !node.IsNoteVisible;
        ScheduleSave();
    }

    public void UpdateSelection()
    {
        UpdateStatus();
    }

    public IReadOnlyList<FileItemViewModel> GetSelectedFileItems()
        => Nodes.SelectMany(node => node.Items).Where(item => item.IsSelected).ToArray();

    public IReadOnlyList<string> GetSelectedPaths()
    {
        var items = GetSelectedFileItems();
        if (items.Count > 0)
        {
            return items.Select(item => item.FullPath).ToArray();
        }

        if (SelectedNodes.Count > 0)
        {
            return SelectedNodes.Select(node => node.FullPath).ToArray();
        }

        return ActiveNode is null ? [] : [ActiveNode.FullPath];
    }

    public void ClearFileSelectionExcept(FolderNodeViewModel owner)
    {
        foreach (var node in Nodes.Where(node => node != owner))
        {
            foreach (var item in node.Items.Where(item => item.IsSelected))
            {
                item.IsSelected = false;
            }
        }

        UpdateStatus();
    }

    public void CopySelection(bool cut)
    {
        var paths = GetSelectedPaths();
        if (paths.Count == 0)
        {
            return;
        }

        NativeShellService.CopyPathsToClipboard(paths, cut);
        foreach (var item in Nodes.SelectMany(node => node.Items))
        {
            item.IsCut = cut && paths.Any(path => PathsEqual(path, item.FullPath));
        }

        _ = Toast.ShowSuccessAsync(cut ? $"Cut {paths.Count} item(s)" : $"Copied {paths.Count} item(s)");
    }

    public async Task PasteIntoAsync(string targetDirectory)
    {
        var payload = NativeShellService.GetClipboardPayload();
        if (payload is null || payload.Paths.Length == 0)
        {
            Toast.ShowError("The clipboard does not contain files or folders.");
            return;
        }

        await TransferAsync(payload.Paths, targetDirectory, payload.Cut, payload.Cut ? "Moving" : "Copying");
        if (payload.Cut)
        {
            foreach (var item in Nodes.SelectMany(node => node.Items))
            {
                item.IsCut = false;
            }
        }
    }

    public async Task DropAsync(IReadOnlyList<string> paths, FolderNodeViewModel targetNode, bool internalDrag, ModifierKeys modifiers)
        => await DropIntoPathAsync(paths, targetNode.FullPath, internalDrag, modifiers);

    public async Task DropIntoPathAsync(IReadOnlyList<string> paths, string targetDirectory, bool internalDrag, ModifierKeys modifiers)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var forceCopy = modifiers.HasFlag(ModifierKeys.Control);
        var forceMove = modifiers.HasFlag(ModifierKeys.Shift);
        var move = forceMove || (!forceCopy && internalDrag && paths.All(path => NativeShellService.IsSameVolume(path, targetDirectory)));
        await TransferAsync(paths, targetDirectory, move, move ? "Moving" : "Copying");
    }

    public FolderNodeViewModel? FindNearestDropTarget(Point graphPoint, double maxDistance = 128)
    {
        FolderNodeViewModel? nearest = null;
        var nearestDistance = double.MaxValue;
        foreach (var node in Nodes)
        {
            var width = node.ActualSize.Width > 1 ? node.ActualSize.Width : 420;
            var height = node.ActualSize.Height > 1 ? node.ActualSize.Height : 420;
            var bounds = new Rect(node.Location, new Size(width, height));
            var dx = Math.Max(bounds.Left - graphPoint.X, Math.Max(0, graphPoint.X - bounds.Right));
            var dy = Math.Max(bounds.Top - graphPoint.Y, Math.Max(0, graphPoint.Y - bounds.Bottom));
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = node;
            }
        }

        return nearestDistance <= maxDistance ? nearest : null;
    }

    public void SetDropTarget(FolderNodeViewModel? target)
    {
        foreach (var node in Nodes.Where(node => node.IsDropTarget != (node == target)))
        {
            node.IsDropTarget = node == target;
        }
    }

    public async Task OpenSearchResultAsync(SearchResultViewModel result)
    {
        var parent = await OpenFolderAsync(result.IsDirectory ? result.FullPath : result.ParentPath, focus: true, animated: true);
        if (parent is not null && !result.IsDirectory)
        {
            var match = parent.Items.FirstOrDefault(item => PathsEqual(item.FullPath, result.FullPath));
            if (match is not null)
            {
                match.IsSelected = true;
                UpdateStatus();
            }
        }

        CloseSearch();
    }

    public async Task SaveNowAsync()
    {
        if (!_isInitialized)
        {
            return;
        }

        _saveDebounce?.Cancel();
        var state = new WorkspaceState
        {
            ViewportX = ViewportLocation.X,
            ViewportY = ViewportLocation.Y,
            ViewportZoom = ViewportZoom,
            Nodes = Nodes.Select(node => new NodeState(
                node.Id,
                node.FullPath,
                node.Location.X,
                node.Location.Y,
                node.AccentHex,
                node.Note)).ToList(),
            Connections = Connections.Select(connection => new ConnectionState(connection.Source.Id, connection.Target.Id)).ToList(),
            Favorites = Favorites.Where(favorite => favorite.IsCustom).Select(favorite => new FavoriteState(
                favorite.Name,
                favorite.Path,
                favorite.Glyph,
                favorite.AccentHex)).ToList()
        };

        try
        {
            await _workspaceStore.SaveAsync(state);
        }
        catch
        {
            // Workspace persistence must never break file navigation.
        }
    }

    public void Dispose()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _saveDebounce?.Cancel();
        _saveDebounce?.Dispose();
        foreach (var load in _nodeLoads.Values)
        {
            load.Cancel();
            load.Dispose();
        }

        foreach (var node in Nodes)
        {
            node.Dispose();
        }
    }

    private FolderNodeViewModel CreateNode(string path, Point location, Guid? id = null)
    {
        var normalized = NormalizePath(path);
        var node = new FolderNodeViewModel(normalized, location, id);
        node.PropertyChanged += NodeOnPropertyChanged;
        Nodes.Add(node);
        _nodesByPath[normalized] = node;
        AttachWatcher(node);
        return node;
    }

    private async Task LoadNodeAsync(FolderNodeViewModel node)
    {
        if (_nodeLoads.Remove(node.Id, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        var cancellation = new CancellationTokenSource();
        _nodeLoads[node.Id] = cancellation;
        node.IsBusy = true;
        node.ErrorMessage = string.Empty;

        try
        {
            var selectedPaths = node.Items.Where(item => item.IsSelected).Select(item => item.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var items = await _fileSystemService.GetDirectoryItemsAsync(node.FullPath, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                node.Items.Clear();
                foreach (var item in items)
                {
                    if (!ShowHiddenItems && item.IsHidden)
                    {
                        continue;
                    }

                    item.IsSelected = selectedPaths.Contains(item.FullPath);
                    node.Items.Add(item);
                }

                node.NotifyItemsChanged();
                UpdateStatus();
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            node.ErrorMessage = ex is UnauthorizedAccessException ? "Access denied" : ex.Message;
        }
        finally
        {
            node.IsBusy = false;
            if (_nodeLoads.TryGetValue(node.Id, out var current) && current == cancellation)
            {
                _nodeLoads.Remove(node.Id);
                cancellation.Dispose();
            }
        }
    }

    private void AttachWatcher(FolderNodeViewModel node)
    {
        try
        {
            var watcher = FileSystemService.CreateWatcher(node.FullPath, () =>
            {
                _ = Application.Current.Dispatcher.InvokeAsync(async () =>
                    await node.DebounceReloadAsync(() => LoadNodeAsync(node)));
            });
            node.AttachWatcher(watcher);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            node.ErrorMessage = "Live updates unavailable";
        }
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

        try
        {
            Toast.ShowBusy($"{verb} {safePaths.Length} item(s)…");
            await _shellService.CopyOrMoveAsync(safePaths, targetDirectory, move);
            await Toast.ShowSuccessAsync($"{(move ? "Moved" : "Copied")} {safePaths.Length} item(s) to {Path.GetFileName(targetDirectory.TrimEnd(Path.DirectorySeparatorChar))}");
            if (_nodesByPath.TryGetValue(NormalizePath(targetDirectory), out var targetNode))
            {
                await LoadNodeAsync(targetNode);
            }
        }
        catch (Exception ex)
        {
            Toast.ShowError(ex.Message);
        }
    }

    private async Task BrowseAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder to place on the canvas",
            Multiselect = false,
            InitialDirectory = ActiveNode?.FullPath
        };

        if (dialog.ShowDialog() == true)
        {
            await OpenFolderAsync(dialog.FolderName, focus: true, animated: true);
        }
    }

    private async Task GoToAddressAsync()
    {
        var expanded = Environment.ExpandEnvironmentVariables(AddressText.Trim().Trim('"'));
        if (File.Exists(expanded))
        {
            NativeShellService.Open(expanded);
            return;
        }

        await OpenFolderAsync(expanded, focus: true, animated: true);
    }

    private async Task GoHomeAsync()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        await OpenFolderAsync(home, focus: true, animated: true);
    }

    private async Task GoUpAsync()
    {
        if (ActiveNode is null)
        {
            return;
        }

        var parent = Directory.GetParent(ActiveNode.FullPath)?.FullName;
        if (parent is not null)
        {
            await OpenFolderAsync(parent, focus: true, animated: true);
        }
    }

    private void GoBack()
    {
        if (_navigationIndex <= 0)
        {
            return;
        }

        _navigationIndex--;
        NavigateHistoryEntry(_navigationHistory[_navigationIndex]);
    }

    private void GoForward()
    {
        if (_navigationIndex >= _navigationHistory.Count - 1)
        {
            return;
        }

        _navigationIndex++;
        NavigateHistoryEntry(_navigationHistory[_navigationIndex]);
    }

    private void NavigateHistoryEntry(string path)
    {
        if (_nodesByPath.TryGetValue(NormalizePath(path), out var node))
        {
            ActivateNode(node, false, true);
        }
        else
        {
            _ = OpenFolderAsync(path, focus: true, animated: true);
        }
    }

    private async Task RefreshActiveAsync()
    {
        if (ActiveNode is not null)
        {
            await LoadNodeAsync(ActiveNode);
        }
    }

    private async Task PasteAsync()
    {
        if (ActiveNode is not null)
        {
            await PasteIntoAsync(ActiveNode.FullPath);
        }
    }

    private async Task DeleteSelectionAsync(bool permanently)
    {
        var paths = GetSelectedPaths();
        if (paths.Count == 0)
        {
            return;
        }

        if (permanently && ConfirmRequested?.Invoke("Permanently delete", $"Permanently delete {paths.Count} item(s)? This cannot be undone.") != true)
        {
            return;
        }

        try
        {
            Toast.ShowBusy(permanently ? "Deleting permanently…" : "Moving to Recycle Bin…");
            await _shellService.DeleteAsync(paths, permanently);
            await Toast.ShowSuccessAsync(permanently ? "Deleted" : "Moved to Recycle Bin");
        }
        catch (Exception ex)
        {
            Toast.ShowError(ex.Message);
        }
    }

    private async Task DuplicateSelectionAsync()
    {
        var items = GetSelectedPaths();
        if (items.Count == 0)
        {
            return;
        }

        try
        {
            Toast.ShowBusy($"Duplicating {items.Count} item(s)…");
            foreach (var path in items)
            {
                await _shellService.DuplicateAsync(path);
            }
            await Toast.ShowSuccessAsync($"Duplicated {items.Count} item(s)");
        }
        catch (Exception ex)
        {
            Toast.ShowError(ex.Message);
        }
    }

    private async Task RenameSelectionAsync()
    {
        var paths = GetSelectedPaths();
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
            NativeShellService.Rename(path, newName);
            await Toast.ShowSuccessAsync($"Renamed to {newName}");
        }
        catch (Exception ex)
        {
            Toast.ShowError(ex.Message);
        }
    }

    private async Task CreateFolderAsync()
    {
        if (ActiveNode is null)
        {
            return;
        }

        var name = PromptRequested?.Invoke("New folder", "Folder name", "New folder");
        if (name is null)
        {
            return;
        }

        try
        {
            var path = NativeShellService.CreateFolder(ActiveNode.FullPath, name);
            await Toast.ShowSuccessAsync($"Created {Path.GetFileName(path)}");
            await LoadNodeAsync(ActiveNode);
        }
        catch (Exception ex)
        {
            Toast.ShowError(ex.Message);
        }
    }

    private async Task CreateNoteFileAsync()
    {
        if (ActiveNode is null)
        {
            return;
        }

        var name = PromptRequested?.Invoke("New text file", "File name", "New note.txt");
        if (name is null)
        {
            return;
        }

        try
        {
            var path = NativeShellService.CreateNoteFile(ActiveNode.FullPath, name);
            await LoadNodeAsync(ActiveNode);
            NativeShellService.Open(path);
        }
        catch (Exception ex)
        {
            Toast.ShowError(ex.Message);
        }
    }

    private async Task SearchAsync()
    {
        var query = SearchText.Trim();
        if (string.IsNullOrWhiteSpace(query) || ActiveNode is null)
        {
            CloseSearch();
            return;
        }

        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        IsSearchOpen = true;
        IsSearchBusy = true;
        SearchResults.Clear();

        try
        {
            var results = await _fileSystemService.SearchAsync(ActiveNode.FullPath, query, 200, _searchCancellation.Token);
            foreach (var result in results)
            {
                SearchResults.Add(result);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsSearchBusy = false;
        }
    }

    private void CloseSearch()
    {
        _searchCancellation?.Cancel();
        IsSearchOpen = false;
        SearchResults.Clear();
    }

    private void SelectedNodesOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (SelectedNodes.LastOrDefault() is { } selected)
        {
            ActiveNode = selected;
        }

        UpdateStatus();
    }

    private void NodeOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FolderNodeViewModel.Location)
            or nameof(FolderNodeViewModel.Note)
            or nameof(FolderNodeViewModel.AccentHex))
        {
            ScheduleSave();
        }
    }

    private void RecordNavigation(string path)
    {
        if (_navigationIndex >= 0 && _navigationIndex < _navigationHistory.Count && PathsEqual(_navigationHistory[_navigationIndex], path))
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

    private Point GetViewportSpawnLocation()
    {
        var basePoint = ViewportLocation + new Vector(120, 90);
        return FindFreeLocation(basePoint);
    }

    private Point GetChildLocation(FolderNodeViewModel parent)
    {
        var width = parent.ActualSize.Width > 1 ? parent.ActualSize.Width : 420;
        var outgoing = Connections.Count(connection => connection.Source == parent);
        var basePoint = new Point(parent.Location.X + width + 140, parent.Location.Y + outgoing * 96);
        return FindFreeLocation(basePoint);
    }

    private Point FindFreeLocation(Point initial)
    {
        var candidate = initial;
        const double width = 420;
        const double height = 440;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var bounds = new Rect(candidate, new Size(width, height));
            if (Nodes.All(node =>
            {
                var nodeWidth = node.ActualSize.Width > 1 ? node.ActualSize.Width : width;
                var nodeHeight = node.ActualSize.Height > 1 ? node.ActualSize.Height : height;
                var nodeBounds = new Rect(node.Location, new Size(nodeWidth, nodeHeight));
                nodeBounds.Inflate(28, 22);
                return !nodeBounds.IntersectsWith(bounds);
            }))
            {
                return candidate;
            }

            candidate.Y += 112;
            if (attempt % 5 == 4)
            {
                candidate.X += 92;
                candidate.Y = initial.Y;
            }
        }

        return candidate;
    }

    private void CollectExclusiveDescendants(FolderNodeViewModel node, HashSet<FolderNodeViewModel> result)
    {
        foreach (var child in Connections.Where(connection => connection.Source == node).Select(connection => connection.Target).ToArray())
        {
            var incomingFromOutside = Connections.Any(connection => connection.Target == child && connection.Source != node);
            if (incomingFromOutside || !result.Add(child))
            {
                continue;
            }

            CollectExclusiveDescendants(child, result);
        }
    }

    private void UpdateStatus()
    {
        var selectedItems = GetSelectedFileItems();
        if (selectedItems.Count == 1)
        {
            var item = selectedItems[0];
            StatusText = item.FullPath;
            StatusDetail = item.IsDirectory
                ? "Folder"
                : $"{item.TypeDescription} • {item.SizeDisplay} • {item.ModifiedDisplay}";
            return;
        }

        if (selectedItems.Count > 1)
        {
            var folders = selectedItems.Count(item => item.IsDirectory);
            var size = selectedItems.Where(item => item.SizeBytes.HasValue).Sum(item => item.SizeBytes ?? 0);
            var nodes = Nodes.Count(node => node.Items.Any(item => item.IsSelected));
            StatusText = $"{selectedItems.Count:N0} selected in {nodes:N0} folder node(s)";
            StatusDetail = $"{folders:N0} folder(s) • {FileSystemService.FormatSize(size)}";
            return;
        }

        if (SelectedNodes.Count > 1)
        {
            StatusText = $"{SelectedNodes.Count:N0} folder nodes selected";
            StatusDetail = string.Empty;
            return;
        }

        if (ActiveNode is not null)
        {
            StatusText = ActiveNode.FullPath;
            StatusDetail = ActiveNode.ItemCountLabel;
            return;
        }

        StatusText = "Ready";
        StatusDetail = string.Empty;
    }

    private void ScheduleSave()
    {
        if (!_isInitialized)
        {
            return;
        }

        _saveDebounce?.Cancel();
        _saveDebounce?.Dispose();
        _saveDebounce = new CancellationTokenSource();
        var token = _saveDebounce.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(700, token);
                await Application.Current.Dispatcher.InvokeAsync(SaveNowAsync);
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
