using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using UltraExplorer.Controls;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;

namespace UltraExplorer.ViewModels;

public enum CanvasMode
{
    ViewAll,
    ViewSelect
}

/// <summary>Which picture of the drives the canvas shows.</summary>
public enum CanvasLayout
{
    /// <summary>Every folder a cell inside its parent's cell, the whole disk on one screen.</summary>
    Nested,

    /// <summary>The top-down tree of nodes, opened a folder at a time.</summary>
    Tree
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

    /// <summary>
    /// How the window hears of changes on disk: one hub for every view, with
    /// one watch per volume or share, whatever is selected (see
    /// <see cref="Changes"/>).
    /// </summary>
    private readonly ChangeHub _changes = new(TimeProvider.System);

    private CancellationTokenSource? _searchCancellation;
    private int _navigationIndex = -1;
    private bool _isInitialized;
    private bool _isDisposed;
    private bool _isNavigating;
    private string _searchText = string.Empty;
    private bool _isSearchOpen;
    private bool _isSearchBusy;
    private string _searchStatusText = string.Empty;
    private bool _isMinimapVisible;
    private double _sidebarWidth = 240;
    private CanvasMode _mode = CanvasMode.ViewAll;
    private CanvasLayout _layout = CanvasLayout.Nested;
    private string _savedLayout = nameof(CanvasLayout.Nested);
    private ItemSort _sort = ItemSort.Default;
    private string? _savedRenderer;
    private bool _sortSavePending;
    private string _nestedZoomLabel = "Fit";
    private Controls.NestedLeftDrag _leftDrag = Controls.NestedLeftDrag.SelectArea;
    private bool _leftDragHintShown;
    private readonly bool _isPickerSession;
    private string? _dialogTitle;

    /// <param name="treeStatePath">
    /// Overrides where the canvas layout is saved; a picker session keeps its
    /// own so it cannot rearrange the user's workspace.
    /// </param>
    public MainViewModel(string? treeStatePath = null)
    {
        _fileSystemService = new FileSystemService(_iconService);

        // A file dialog lists files on its canvas and its rules are built around
        // the tree, so it keeps that picture whatever the user chose for their
        // own window.
        _isPickerSession = treeStatePath is not null;
        if (_isPickerSession)
        {
            _layout = CanvasLayout.Tree;
        }

        Tree = new ViewAllViewModel(_marks, _iconService, treeStatePath);
        Tree.PropertyChanged += OnTreePropertyChanged;
        Tree.MessageRequested += OnTreeMessage;

        // The hub's changes go to the tree view model, which hands each to
        // whoever registered its folder.  The nested canvas takes them in at
        // the start of its frames while it draws; otherwise one dispatcher
        // operation at input priority takes in everything that is due.
        _changes.Driver.Fallback = DispatcherFrameDriver.ForCurrentThread(
            (ref FrameBudget budget) => _changes.Drain(ref budget, Tree),
            () => _changes.HasWork);
        Tree.Changes = _changes;

        BackCommand = new RelayCommand(GoBack);
        ForwardCommand = new RelayCommand(GoForward);
        UpCommand = new AsyncRelayCommand(GoUpAsync);
        HomeCommand = new AsyncRelayCommand(GoHomeAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshActiveAsync);
        OpenSidebarItemCommand = new AsyncRelayCommand<FavoriteItemViewModel>(OpenSidebarItemAsync);

        Address = new AddressBarViewModel(
            path => Tree.RevealPathAsync(path),
            RecentLocations,
            (message, isError) => OnTreeMessage(message, isError));

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
        ToggleFolderListCommand = new RelayCommand(
            () => Tree.FolderList.IsVisible = !Tree.FolderList.IsVisible);
        CollapseAllCommand = new RelayCommand(() => Tree.CollapseAll());
        RelayoutCommand = new RelayCommand(() => Tree.RelayoutCanvas());
        HideSelectedCommand = new AsyncRelayCommand(Tree.HideSelectedAsync);
        ReturnToLayoutCommand = new RelayCommand(() => Tree.ReturnSelectionToLayout());
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

    /// <summary>
    /// The address bar: crumbs, the path line they turn into, and the folders it
    /// offers while it is being typed in.
    /// </summary>
    public AddressBarViewModel Address { get; }

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
    public ObservableCollection<SearchResultViewModel> SearchResults { get; } = [];

    public ICommand BackCommand { get; }
    public ICommand ForwardCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand HomeCommand { get; }
    public ICommand RefreshCommand { get; }
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
    public ICommand ToggleFolderListCommand { get; }
    public ICommand CollapseAllCommand { get; }
    public ICommand RelayoutCommand { get; }
    public ICommand HideSelectedCommand { get; }
    public ICommand ReturnToLayoutCommand { get; }
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

    public CanvasLayout Layout
    {
        get => _layout;
        set
        {
            if (_isPickerSession || !SetProperty(ref _layout, value))
            {
                return;
            }

            _savedLayout = value.ToString();
            OnPropertyChanged(nameof(IsNestedLayout));
            OnPropertyChanged(nameof(IsTreeLayout));
            OnPropertyChanged(nameof(ZoomLabel));
            OnPropertyChanged(nameof(IsMinimapShown));
            _ = SaveNowAsync();
        }
    }

    public bool IsNestedLayout => _layout == CanvasLayout.Nested;

    /// <summary>
    /// The Canvas options setting for what draws the nested canvas.  Taking
    /// effect at once: the canvas moves to the renderer chosen at its next
    /// frame.  The command line and the environment variable still win over
    /// it (<see cref="Rendering.Gpu.GpuBootstrap.ExplicitPreference"/>).
    /// </summary>
    internal Rendering.Gpu.RendererPreference Renderer
    {
        get => Rendering.Gpu.GpuBootstrap.ParsePreference(_savedRenderer) ?? Rendering.Gpu.RendererPreference.Auto;
        set
        {
            if (value == Renderer)
            {
                return;
            }

            _savedRenderer = value.ToString();
            Rendering.Gpu.GpuBootstrap.SetSettingPreference(value);
            OnPropertyChanged(nameof(Renderer));
            _ = SaveNowAsync();
        }
    }

    public bool IsTreeLayout => _layout == CanvasLayout.Tree;

    /// <summary>
    /// The one order every picture of the drives shares - the sub-folders and
    /// files inside each cell of the nested canvas, each folder's children on
    /// the tree, and the rows of the folder list - chosen like Explorer's
    /// column headers and remembered with the rest of the workspace.  The tree
    /// and the list are ordered from here; the nested canvas has a tree of its
    /// own, which the window keeps in step through this property's change.
    ///
    /// A file dialog starts from names from A and never writes its choice
    /// back: being somebody else's dialog is no reason to rearrange the
    /// user's own window.
    /// </summary>
    public ItemSort Sort
    {
        get => _sort;
        set
        {
            if (!SetProperty(ref _sort, value))
            {
                return;
            }

            Tree.Sort = value;
            SaveSortWhenIdle();
        }
    }

    /// <summary>
    /// Writes the workspace once the window has nothing better to do.  A click
    /// on a header is followed by frames of the canvas reordering, and the
    /// first save of a session is milliseconds of serialiser warming up that
    /// have no business in any of them; clicking through the headers is also
    /// one write, not one per click.
    /// </summary>
    private void SaveSortWhenIdle()
    {
        if (_sortSavePending)
        {
            return;
        }

        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            _ = SaveNowAsync();
            return;
        }

        _sortSavePending = true;
        dispatcher.InvokeAsync(
            async () =>
            {
                _sortSavePending = false;
                await SaveNowAsync();
            },
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// What a left drag does on the nested canvas where it does not pick
    /// something up: select an area, as in Explorer (the default), or pan, as
    /// the canvas used to.  Remembered with the workspace.
    /// </summary>
    public Controls.NestedLeftDrag LeftDrag
    {
        get => _leftDrag;
        set
        {
            if (SetProperty(ref _leftDrag, value))
            {
                _ = SaveNowAsync();
            }
        }
    }

    /// <summary>
    /// Whether the hint that left-drag now selects has been shown - once, at
    /// the first rectangle drawn, and never again.
    /// </summary>
    public bool LeftDragHintShown
    {
        get => _leftDragHintShown;
        set
        {
            if (_leftDragHintShown != value)
            {
                _leftDragHintShown = value;
                _ = SaveNowAsync();
            }
        }
    }

    /// <summary>The zoom shown on the canvas controls, from whichever canvas is showing.</summary>
    public string ZoomLabel => IsNestedLayout ? _nestedZoomLabel : Tree.ZoomLabel;

    /// <summary>Set by the nested canvas as it moves.</summary>
    public string NestedZoomLabel
    {
        get => _nestedZoomLabel;
        set
        {
            if (SetProperty(ref _nestedZoomLabel, value) && IsNestedLayout)
            {
                OnPropertyChanged(nameof(ZoomLabel));
            }
        }
    }

    /// <summary>The minimap draws the tree's nodes; the nested canvas is its own overview.</summary>
    public bool IsMinimapShown => IsMinimapVisible && IsTreeLayout;

    /// <summary>Colour labels and notes, by path.</summary>
    public FolderMarkService Marks => _marks;

    /// <summary>Shell icons, shared by every view in the window.</summary>
    public ShellIconService Icons => _iconService;

    /// <summary>
    /// The window's change hub.  Every view registers the folders it shows -
    /// the nested canvas's tree every folder it read, the list its folder, the
    /// tree canvas the folders it shows children of - and hears of changes in
    /// them from here, whatever is selected.  The window's own file operations
    /// touch it (<see cref="ViewAllViewModel.RefreshPathAsync"/>), so what they
    /// change is read again once, together with what the watch saw.
    /// </summary>
    public ChangeHub Changes => _changes;

    public bool IsPinned(string path)
        => QuickAccess.Any(item => item.IsCustom && ViewAllPath.Equals(item.Path, path));

    /// <summary>Takes a folder the user pinned off Home.</summary>
    public void UnpinPath(string path)
    {
        if (QuickAccess.FirstOrDefault(item => item.IsCustom && ViewAllPath.Equals(item.Path, path)) is { } item)
        {
            RemoveFavorite(item);
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
        set
        {
            if (SetProperty(ref _isMinimapVisible, value))
            {
                OnPropertyChanged(nameof(IsMinimapShown));
            }
        }
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
            Tree.FolderList.IsVisible = state.IsFolderListVisible;
            _savedLayout = string.IsNullOrWhiteSpace(state.CanvasLayout) ? nameof(CanvasLayout.Nested) : state.CanvasLayout;
            if (!_isPickerSession && Enum.TryParse<CanvasLayout>(_savedLayout, ignoreCase: true, out var layout) && layout != _layout)
            {
                _layout = layout;
                OnPropertyChanged(nameof(Layout));
                OnPropertyChanged(nameof(IsNestedLayout));
                OnPropertyChanged(nameof(IsTreeLayout));
                OnPropertyChanged(nameof(ZoomLabel));
                OnPropertyChanged(nameof(IsMinimapShown));
            }

            _savedRenderer = state.CanvasRenderer;
            Rendering.Gpu.GpuBootstrap.SetSettingPreference(Renderer);
            _leftDrag = WorkspaceState.ParseLeftDrag(state.NestedLeftDrag);
            _leftDragHintShown = state.NestedLeftDragHintShown;

            // Handed to the tree before it builds its first layout, so the
            // drives open already in the remembered order.
            var sort = _isPickerSession ? ItemSort.Default : ItemSort.FromSetting(state.CanvasSort);
            if (sort != _sort)
            {
                _sort = sort;
                Tree.Sort = sort;
                OnPropertyChanged(nameof(Sort));
            }

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

        // While the nested canvas is the picture, the tree behind it only has
        // to hold the path of what is selected, not open every folder on the
        // way; and what only feeds its hidden editor waits until it is shown.
        Tree.PreferLightReveal = IsNestedLayout;
        Tree.IsCanvasShown = IsTreeLayout;
        await Tree.InitializeAsync(initialPath);
        Address.SetPath(Tree.ActivePath);
        UpdateSidebarSelection();
    }

    public void ShowContextMenuFor(IReadOnlyList<string> paths, FrameworkElement origin, Point point)
        => ContextMenuRequested?.Invoke(paths, origin, point);

    /// <summary>
    /// A volume arrived or left: the navigation pane's drives are listed
    /// again.  Which drives are ready is asked off the UI thread - a disc
    /// spinning up takes seconds to say - and the list is only replaced when
    /// it changed.
    /// </summary>
    public async Task RefreshDrivesAsync()
    {
        var ready = await Task.Run(FileSystemService.ListReadyDrives);
        if (_isDisposed)
        {
            return;
        }

        var same = ready.Count == Drives.Count
            && ready.Select(drive => drive.Path).SequenceEqual(Drives.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
        if (!same)
        {
            Drives.Clear();
            foreach (var drive in _fileSystemService.GetDrives(ready))
            {
                Drives.Add(drive);
            }

            UpdateSidebarSelection();
        }
    }

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

        // A file dialog never changes the layout or the order, and must not put
        // back the ones it read at its start over a choice made meanwhile in
        // the window.
        var layout = _savedLayout;
        string? sort = (_isPickerSession ? ItemSort.Default : _sort).ToSetting();
        var renderer = _savedRenderer;
        var leftDrag = WorkspaceState.LeftDragSetting(_leftDrag);
        var hintShown = _leftDragHintShown;
        if (_isPickerSession && await _workspaceStore.LoadAsync() is { } current)
        {
            layout = current.CanvasLayout;
            sort = current.CanvasSort;
            renderer = current.CanvasRenderer;
            leftDrag = current.NestedLeftDrag;
            hintShown = current.NestedLeftDragHintShown;
        }

        var state = new WorkspaceState
        {
            SidebarWidth = SidebarWidth,
            IsMinimapVisible = IsMinimapVisible,
            IsFolderListVisible = Tree.FolderList.IsVisible,
            CanvasLayout = layout,
            CanvasSort = sort,
            CanvasRenderer = renderer,
            NestedLeftDrag = leftDrag,
            NestedLeftDragHintShown = hintShown,
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
        Address.Dispose();
        Tree.Dispose();
        _changes.Dispose();
    }

    private void OnTreePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewAllViewModel.StatusCountText):
                OnPropertyChanged(nameof(StatusCountText));
                break;
            case nameof(ViewAllViewModel.ZoomLabel):
                if (IsTreeLayout)
                {
                    OnPropertyChanged(nameof(ZoomLabel));
                }

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
                Address.SetPath(Tree.ActivePath);
                UpdateSidebarSelection();

                // Sweeping out a range or a rectangle moves the focus too,
                // and is not going anywhere: only a click or a navigation
                // is a step back and forward can retrace.
                if (Tree.FocusRecordsNavigation)
                {
                    RecordNavigation(Tree.ActivePath);
                }

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

    /// <summary>
    /// Where this window has already been, newest first and each place once.
    /// The address bar offers these when there is nothing typed to work from.
    /// </summary>
    private IReadOnlyList<string> RecentLocations()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recent = new List<string>();
        for (var index = _navigationHistory.Count - 1; index >= 0; index--)
        {
            var path = _navigationHistory[index];
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
            {
                recent.Add(path);
            }
        }

        return recent;
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
            if (move)
            {
                // Moved away: nothing that was selected there is any more.
                Tree.Selection.Remove(safePaths, SelectionSource.Command);
            }

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
        // What is selected and still there: a file deleted from outside since
        // it was selected is not the Shell's to be asked about.
        var paths = Tree.SelectedPaths.Where(path => File.Exists(path) || Directory.Exists(path)).ToArray();
        if (paths.Length == 0)
        {
            if (Tree.SelectedPaths.Count > 0)
            {
                Tree.Selection.Remove(Tree.SelectedPaths, SelectionSource.Command);
            }

            return;
        }

        if (permanently
            && ConfirmRequested?.Invoke("Permanently delete", $"Permanently delete {paths.Length} item(s)? This cannot be undone.") != true)
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

            // Let go of explicitly: a folder the nested canvas selected in
            // need not have a node for its refresh to prune.
            Tree.Selection.Remove(paths, SelectionSource.Command);
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

    /// <summary>
    /// Properties of everything selected: one sheet for the lot, as Explorer
    /// shows for several items in one folder; for items spread over several
    /// folders, which the Shell has no one sheet for, the first one's.
    /// </summary>
    private void ShowProperties()
    {
        var paths = Tree.SelectedOrActivePaths;
        if (paths.Count == 0)
        {
            return;
        }

        try
        {
            if (paths.Count > 1 && ShellSelection.ShowProperties(paths))
            {
                return;
            }

            NativeShellService.ShowProperties(paths[0]);
        }
        catch (Exception ex)
        {
            Toast.ShowError(ex.Message);
        }
    }

    /// <summary>Above this many files, opening them all is asked about first.</summary>
    private const int OpenWithoutAsking = 15;

    /// <summary>
    /// Opens what is selected: with several items, every selected file - past
    /// fifteen only once the user says so, as Explorer asks; with one, the
    /// focus, a folder going in on the tree and a file opening.
    /// </summary>
    private void OpenSelection()
    {
        if (Tree.Selection.Count > 1)
        {
            var files = Tree.Selection.Items.Where(item => !item.IsDirectory).Select(item => item.Path).ToArray();
            if (files.Length > 0)
            {
                if (files.Length > OpenWithoutAsking
                    && ConfirmRequested?.Invoke("Open", $"Open all {files.Length:N0} selected files?") != true)
                {
                    return;
                }

                foreach (var file in files)
                {
                    try
                    {
                        NativeShellService.Open(file);
                    }
                    catch (Exception ex)
                    {
                        Toast.ShowError($"Could not open {Path.GetFileName(file)}: {ex.Message}");
                        return;
                    }
                }

                return;
            }
        }

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

    /// <summary>Explorer, on the folder the selection is in with all of it selected there, or on the one item.</summary>
    private void ShowSelectionInExplorer()
    {
        var paths = Tree.SelectedOrActivePaths;
        if (paths.Count > 1 && ShellSelection.ShowInExplorer(paths))
        {
            return;
        }

        if (paths.FirstOrDefault() is { } path)
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
        var paths = Tree.SelectedOrActivePaths;
        if (paths.Count == 0)
        {
            return;
        }

        Tree.ApplyAccent(paths, string.IsNullOrEmpty(accentHex) ? null : accentHex);
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

/// <summary>
/// The Shell's own answers for several items at once, which it gives only for
/// items in one folder: one properties sheet for all of them, and an Explorer
/// window on the folder with all of them selected.  False when the items are
/// spread over several folders, or the Shell will not, and the caller falls
/// back to the first item alone.
/// </summary>
internal static class ShellSelection
{
    private static readonly Guid IidDataObject = new("0000010e-0000-0000-C000-000000000046");

    public static bool ShowProperties(IReadOnlyList<string> paths) =>
        WithItems(paths, (folder, items) =>
        {
            var iid = IidDataObject;
            if (SHCreateDataObject(folder, (uint)items.Length, items, IntPtr.Zero, ref iid, out var data) != 0 || data == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                return SHMultiFileProperties(data, 0) == 0;
            }
            finally
            {
                Marshal.Release(data);
            }
        });

    public static bool ShowInExplorer(IReadOnlyList<string> paths) =>
        WithItems(paths, (folder, items) => SHOpenFolderAndSelectItems(folder, (uint)items.Length, items, 0) == 0);

    /// <summary>
    /// The folder the paths share and each item as the Shell names it inside
    /// that folder, for as long as <paramref name="action"/> runs.
    /// </summary>
    private static bool WithItems(IReadOnlyList<string> paths, Func<IntPtr, IntPtr[], bool> action)
    {
        var parent = Path.GetDirectoryName(paths[0]);
        if (string.IsNullOrEmpty(parent)
            || paths.Any(path => !string.Equals(Path.GetDirectoryName(path), parent, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var owned = new List<IntPtr>(paths.Count + 1);
        try
        {
            if (SHParseDisplayName(parent, IntPtr.Zero, out var folder, 0, out _) != 0 || folder == IntPtr.Zero)
            {
                return false;
            }

            owned.Add(folder);
            var items = new IntPtr[paths.Count];
            for (var index = 0; index < paths.Count; index++)
            {
                if (SHParseDisplayName(paths[index], IntPtr.Zero, out var item, 0, out _) != 0 || item == IntPtr.Zero)
                {
                    return false;
                }

                owned.Add(item);
                items[index] = ILFindLastID(item);
            }

            return action(folder, items);
        }
        finally
        {
            foreach (var pidl in owned)
            {
                ILFree(pidl);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern int SHCreateDataObject(IntPtr folder, uint count, IntPtr[] items, IntPtr inner, ref Guid iid, out IntPtr dataObject);

    [DllImport("shell32.dll")]
    private static extern int SHMultiFileProperties(IntPtr dataObject, uint flags);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr[] items, uint flags);
}
