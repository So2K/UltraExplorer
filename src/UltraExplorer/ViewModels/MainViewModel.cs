using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
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
    private readonly WorkspaceStore _workspaceStore;
    private readonly FolderMarkService _marks;
    private readonly bool _isFolderWindow;
    private readonly FileSystemService _fileSystemService;

    /// <summary>Where the pane being worked with has been (see <see cref="History"/>).</summary>
    private NavigationHistory _history = new();

    /// <summary>
    /// How the window hears of changes on disk: one hub for every view, with
    /// one watch per volume or share, whatever is selected (see
    /// <see cref="Changes"/>).
    /// </summary>
    private readonly ChangeHub _changes = new(TimeProvider.System);

    private bool _isInitialized;

    /// <summary>
    /// Set once the saved workspace has been read and applied.  Until then
    /// there is nothing of the user's to save, only defaults, and a save -
    /// the window closed while the workspace was still loading - would write
    /// those over the pinned folders and orders it had not yet read.
    /// </summary>
    private bool _isStateLoaded;

    private bool _isDisposed;

    /// <summary>The navigation pane's latest drive listing asked for; one older that answers after it is let go (<see cref="ShowDrivesAsync"/>).</summary>
    private int _drivesListing;

    private bool _isMinimapVisible;
    private double _sidebarWidth = 240;
    private CanvasMode _mode = CanvasMode.ViewAll;
    private CanvasLayout _layout = CanvasLayout.Nested;
    private string _savedLayout = nameof(CanvasLayout.Nested);
    private bool _loadingOrders;
    private string? _savedRenderer;
    private bool _sortSavePending;
    private string _nestedZoomLabel = "Fit";
    private Controls.NestedLeftDrag _leftDrag = Controls.NestedLeftDrag.SelectArea;
    private bool _leftDragHintShown;
    private CanvasLayer _layers = CanvasLayer.All;
    private bool _showFavoriteLinks;
    private bool _favoriteLinksChanged;
    private bool _favoritesChanged;

    /// <summary>
    /// The workspace as this window last read or wrote it: its own settings
    /// as they were then.  A save writes over the file only the settings
    /// that differ from these - the ones changed in this window since - and
    /// keeps the file's for the rest, which another window, a dialog or
    /// another process may have changed meanwhile.  Written whole from
    /// memory, a window loaded earlier put back what it had read at its
    /// start over every such change.  Null until the workspace is loaded.
    /// </summary>
    private WorkspaceState? _savedWorkspace;

    /// <summary>
    /// The pins as this window last read or wrote them.  Its pins and unpins
    /// since are laid over the pins the file has then; its list written
    /// whole took off every pin made in a dialog or another window meanwhile.
    /// </summary>
    private List<FavoriteState> _savedFavorites = [];

    private readonly SemaphoreSlim _stateSaving = new(1, 1);
    private bool? _pickerShowsFiles;
    private bool _isSplit;
    private SplitOrientation _splitOrientation = SplitOrientation.SideBySide;
    private double _splitRatio = SplitLayout.DefaultRatio;
    private int _activePaneIndex;
    private readonly bool _isPickerSession;
    private string? _dialogTitle;

    /// <param name="treeStatePath">
    /// Overrides where the canvas layout is saved; a picker session keeps its
    /// own so it cannot rearrange the user's workspace.
    /// </param>
    public MainViewModel(string? treeStatePath = null, bool nestedPicker = false)
        : this(treeStatePath, nestedPicker, null)
    {
    }

    /// <param name="normalWorkspacePath">
    /// Set for a folder window - an Explorer window taken over, or one opened
    /// with --new-window - whose own view the caller keeps under that name
    /// (<paramref name="treeStatePath"/> is made from it).  Its pins, orders,
    /// colours and settings are the user's like any other window's, and go
    /// where every window reads them: kept in files of its own, they were
    /// never read again, and lost with the window.
    /// </param>
    internal MainViewModel(string? treeStatePath, bool nestedPicker, string? normalWorkspacePath)
    {
        _isFolderWindow = normalWorkspacePath is not null;
        _workspaceStore = new WorkspaceStore();
        _marks = new FolderMarkService();
        _fileSystemService = new FileSystemService(_iconService);

        // Pickers keep their own state. Native replacement starts with the
        // tile canvas; standalone picker callers retain the original tree.
        _isPickerSession = treeStatePath is not null && !_isFolderWindow;
        if (_isPickerSession)
        {
            _layout = nestedPicker ? CanvasLayout.Nested : CanvasLayout.Tree;
        }

        Tree = new ViewAllViewModel(_marks, _iconService, treeStatePath);
        Tree.PropertyChanged += OnTreePropertyChanged;
        Tree.Orders.Changed += OnOrdersChanged;
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
        // One command for every entry of the navigation pane, so it must not
        // switch itself off while one of them is being reached: a share that
        // is slow to answer would grey out the whole pane.  The last entry
        // clicked is where the canvas ends up (see ViewAllViewModel.RevealPathAsync).
        OpenSidebarItemCommand = new AsyncRelayCommand<FavoriteItemViewModel>(OpenSidebarItemAsync, allowConcurrent: true);

        Address = new AddressBarViewModel(
            (path, ticket) => Tree.RevealAsync(path, ticket: ticket),
            Tree.BeginNavigation,
            Tree.IsLatestNavigation,
            RecentLocations,
            (message, isError) => OnTreeMessage(message, isError),
            // A crumb's chevron shows or leaves out hidden and system folders
            // as the window does.
            () => Tree.ShowHiddenItems);

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

        // A result selects only itself: one deleted or moved since the search
        // would otherwise select the folder it was in, and the next Delete
        // would recycle that folder.
        Search = new SearchViewModel(_iconService, path => Tree.RevealAsync(path, exact: true), OpenSearchResult);

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
    /// <summary>The search box and its results.</summary>
    public SearchViewModel Search { get; }

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

    /// <summary>
    /// Asks the user for a line of text: the title, the prompt, the text to
    /// start from, and whether that text is a file's name - then only what
    /// comes before its extension is picked out, so typing keeps the
    /// extension.  A folder's name or a note is picked out whole: what follows
    /// a dot in "Photos 2024.06" is no extension.
    /// </summary>
    public event Func<string, string, string, bool, string?>? PromptRequested;
    /// <summary>
    /// Asks the user before something is done: the title, the message and the
    /// confirming button's label.  "Delete" is drawn as a danger; anything
    /// else - opening sixteen files - as the usual accent button.
    /// </summary>
    public event Func<string, string, string, bool>? ConfirmRequested;
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
    /// Which order each folder is shown in, and which way its grids fill -
    /// the sub-folders and files inside each cell of the nested canvas, each
    /// folder's children on the tree, and the rows of the folder list -
    /// chosen like Explorer's column headers and remembered with the rest of
    /// the workspace.  The tree and the list are ordered from here, and the
    /// window hands the same orders to the nested canvas's tree.
    ///
    /// A file dialog starts from names from A in every folder and never
    /// writes its choices back: being somebody else's dialog is no reason to
    /// rearrange the user's own window.
    /// </summary>
    public FolderOrders Orders => Tree.Orders;

    /// <summary>The default order (<see cref="FolderOrders.Default"/>): what every folder without an order of its own is shown in.</summary>
    public ItemSort Sort
    {
        get => Orders.Default;
        set => Orders.SetDefault(value);
    }

    private void OnOrdersChanged(string? folder)
    {
        OnPropertyChanged(nameof(Sort));
        if (!_loadingOrders)
        {
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
    /// Optional round navigation links above This PC, remembered with the workspace.
    /// </summary>
    public bool ShowFavoriteLinks
    {
        get => _showFavoriteLinks;
        set
        {
            if (SetProperty(ref _showFavoriteLinks, value))
            {
                _favoriteLinksChanged = true;
                _ = SaveNowAsync();
            }
        }
    }

    /// <summary>The visible nested canvas layers, remembered with the workspace.</summary>
    public CanvasLayer Layers
    {
        get => _layers;
        set
        {
            var normalized = NormalizePickerLayers(value & CanvasLayer.All);
            if (SetProperty(ref _layers, normalized))
            {
                _ = SaveNowAsync();
            }
            else if (normalized != value) OnPropertyChanged();
        }
    }

    internal void ConfigurePickerFiles(bool shown)
    {
        _pickerShowsFiles = shown;
        _layers = NormalizePickerLayers(_layers);
        OnPropertyChanged(nameof(Layers));
    }

    private CanvasLayer NormalizePickerLayers(CanvasLayer layers) => _pickerShowsFiles switch
    {
        true => layers | CanvasLayer.Files,
        false => layers & ~CanvasLayer.Files,
        _ => layers
    };

    /// <summary>Whether a layer is showing.</summary>
    public bool IsLayerShown(CanvasLayer layer) => (_layers & layer) == layer;

    /// <summary>Shows or hides one layer, leaving the others as they are.</summary>
    public void SetLayer(CanvasLayer layer, bool shown) =>
        Layers = shown ? _layers | layer : _layers & ~layer;

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

    /// <summary>
    /// Whether the nested canvas is split into two panes, each a whole canvas
    /// at its own place on the disk, with its own camera, selection, filter
    /// and history.  Off until the user splits it; a file dialog never does.
    /// Splitting from the tree canvas goes to the nested one first: the tree
    /// is never split.  Remembered with the workspace.
    /// </summary>
    public bool IsSplit
    {
        get => _isSplit;
        set
        {
            if (_isPickerSession || _isSplit == value)
            {
                return;
            }

            // The picture first, so the panes open onto a canvas on screen.
            if (value && IsTreeLayout)
            {
                Layout = CanvasLayout.Nested;
            }

            _isSplit = value;
            OnPropertyChanged();
            _ = SaveNowAsync();
        }
    }

    /// <summary>How the two panes of a split view are laid out: side by side (the default) or one above the other.</summary>
    public SplitOrientation SplitOrientation
    {
        get => _splitOrientation;
        set
        {
            if (!_isPickerSession && SetProperty(ref _splitOrientation, value))
            {
                _ = SaveNowAsync();
            }
        }
    }

    /// <summary>
    /// The first pane's share of the room, as the divider between the panes
    /// was left - kept between <see cref="SplitLayout.MinimumRatio"/> and
    /// <see cref="SplitLayout.MaximumRatio"/>.
    /// </summary>
    public double SplitRatio
    {
        get => _splitRatio;
        set
        {
            var ratio = SplitLayout.ClampRatio(value);
            if (!_isPickerSession && Math.Abs(ratio - _splitRatio) > 0.0005)
            {
                _splitRatio = ratio;
                OnPropertyChanged();
                _ = SaveNowAsync();
            }
        }
    }

    /// <summary>
    /// Which pane of a split view is being worked with - 0 the first, 1 the
    /// second - as the window says.  Written with the rest of the workspace,
    /// not on every click between the panes.
    /// </summary>
    public int ActivePaneIndex
    {
        get => _activePaneIndex;
        set => SetProperty(ref _activePaneIndex, value is 1 ? 1 : 0);
    }

    /// <summary>
    /// Where the pane being worked with has been, which Back and Forward step
    /// through and the address bar offers.  Each pane of a split view has its
    /// own, and the window hands over the one of the pane it activates.
    /// </summary>
    internal NavigationHistory History
    {
        get => _history;
        set => _history = value ?? new NavigationHistory();
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

    /// <summary>Pins one folder to Home, whatever is selected: what a folder's own menu offers.</summary>
    public void PinPath(string path)
    {
        PinFolders([path]);
        _ = SaveNowAsync();
    }

    /// <summary>The Windows properties sheet of one item, whatever is selected.</summary>
    public void ShowPropertiesOf(string path)
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

    /// <summary>File Explorer on one item, whatever is selected: Windows Explorer itself, as the command says.</summary>
    public void ShowInExplorer(string path)
    {
        try
        {
            NativeShellService.ShowInWindowsExplorer(path);
        }
        catch (Exception ex)
        {
            Toast.ShowError(ex.Message);
        }
    }

    /// <summary>Asks for one item's note - the one it has, to edit, or a new one - and keeps what comes back.</summary>
    public void EditNoteOf(string path, string name)
    {
        var note = PromptRequested?.Invoke("Note", $"Note for {name}", _marks.Get(path).Note, false);
        if (note is not null)
        {
            Tree.ApplyNote(path, note);
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
        if (_isInitialized || _isDisposed)
        {
            return;
        }

        _isInitialized = true;

        await _marks.LoadAsync();
        if (_isDisposed) return;
        var state = await _workspaceStore.LoadAsync();
        if (_isDisposed) return;
        if (_isFolderWindow && state is not null)
        {
            state.IsSplit = false;
            state.ActivePane = 0;
            state.CanvasLayout = nameof(CanvasLayout.Nested);
        }
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
            _layers = NormalizePickerLayers(CanvasLayers.Parse(state.CanvasLayersOff));
            _showFavoriteLinks = state.ShowFavoriteLinks;
            OnPropertyChanged(nameof(ShowFavoriteLinks));

            // A file dialog shows the tree and never splits, and writes back
            // whatever the file says (see SaveNowAsync).
            if (!_isPickerSession)
            {
                _isSplit = state.IsSplit;
                _splitOrientation = SplitLayout.ParseOrientation(state.SplitOrientation);
                _splitRatio = SplitLayout.ClampRatio(state.SplitRatio);
                _activePaneIndex = state.IsSplit && state.ActivePane == 1 ? 1 : 0;
            }

            // Handed to the tree before it builds its first layout, so the
            // drives open already in the remembered orders.  A file dialog
            // starts from names from A in every folder, but reads its grids
            // the way the user chose and sorts a folder the way the user's
            // headers do: those are how the canvas works, not an order.
            _loadingOrders = true;
            try
            {
                Orders.Load(
                    _isPickerSession ? ItemSort.Default : ItemSort.FromSetting(state.CanvasSort),
                    FolderOrders.ParseScope(state.CanvasSortScope),
                    FolderOrders.ParseFlow(state.CanvasLayoutOrder),
                    _isPickerSession ? null : state.FolderSorts);
            }
            finally
            {
                _loadingOrders = false;
            }

            // A file edited by hand can hold a null for a list or an entry;
            // what cannot be read is passed over, not a reason not to start.
            foreach (var legacy in state.Nodes ?? [])
            {
                if (legacy?.Path is { } legacyPath)
                {
                    _marks.Seed(legacyPath, legacy.AccentHex, legacy.Note);
                }
            }

            // Before the tree reads anything, so the drives come up with or
            // without their hidden items as they were left.  A file dialog
            // keeps to its caller's rules, and does not write this back either.
            if (!_isPickerSession)
            {
                Tree.RestoreShowHiddenItems(state.ShowHiddenItems);
            }
        }

        // The settings as read, before the wait below: the window is shown and
        // usable while it lasts, and a setting changed in it is this window's
        // change, to be written by the next save.  Taken after the wait, it
        // counted as what the file already had and was never written.
        _savedWorkspace = CaptureWorkspace();

        // Which known folders, drives and WSL distributions are there is asked
        // off the UI thread, the three at once: a drive mapped to a server that
        // is off takes some twenty seconds to say it is not ready, and every
        // window of the process - Explorer's replacements and the pickers too -
        // shares this thread.  The drives are not waited for: the window is not
        // ready until its tree is, and the broker gives a window it made
        // fifteen seconds, so the pane's drives come in when they have answered
        // (ShowDrivesAsync), as they do when a volume arrives or leaves.
        var knownPlaces = Task.Run(FileSystemService.ListQuickAccess);
        var readyDrives = Task.Run(FileSystemService.ListReadyDrives);
        var distributions = Task.Run(FileSystemService.ListWslDistributions);
        await Task.WhenAll(knownPlaces, distributions);
        if (_isDisposed) return;

        var quickAccess = _fileSystemService.GetQuickAccess(await knownPlaces);
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
            // Every pinned folder comes back, whether or not it can be reached
            // right now.  A share on a NAS that is off, a VPN not yet up, a USB
            // disk not plugged in: checking would hold the window up for as
            // long as the network takes to give up, and dropping the pin would
            // lose it for good at the next save.  One that is still out of
            // reach says so when it is clicked.
            foreach (var favorite in state.Favorites ?? [])
            {
                if (favorite is null || string.IsNullOrWhiteSpace(favorite.Path))
                {
                    continue;
                }

                if (HomeItems.Concat(QuickAccess).All(existing => !ViewAllPath.Equals(existing.Path, favorite.Path)))
                {
                    QuickAccess.Add(new FavoriteItemViewModel
                    {
                        Name = string.IsNullOrWhiteSpace(favorite.Name) ? favorite.Path : favorite.Name,
                        Path = favorite.Path,
                        Glyph = favorite.Glyph ?? "\uE8B7",
                        AccentHex = favorite.AccentHex ?? "#E3B341",
                        IsCustom = true
                    });
                }
            }
        }

        _isStateLoaded = true;

        // The pins once they are all in the pane; the settings' baseline
        // above has none, and a save lays pins over these, never over that.
        _savedFavorites = FavoriteSnapshot();

        _fileSystemService.AttachIcons(HomeItems.Concat(QuickAccess).ToArray());

        _ = ShowDrivesAsync(readyDrives);

        foreach (var location in _fileSystemService.GetNetworkLocations(await distributions))
        {
            NetworkLocations.Add(location);
        }

        // While the nested canvas is the picture, the tree behind it only has
        // to hold the path of what is selected, not open every folder on the
        // way; and what only feeds its hidden editor waits until it is shown.
        Tree.PreferLightReveal = IsNestedLayout;
        Tree.IsCanvasShown = IsTreeLayout;
        Tree.RestoresSecondPane = _isSplit;
        await Tree.InitializeAsync(initialPath);
        if (_isDisposed) return;
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
    public Task RefreshDrivesAsync() => ShowDrivesAsync(Task.Run(FileSystemService.ListReadyDrives));

    /// <summary>
    /// Puts the drives a listing found in the navigation pane once it has
    /// answered - the start's own listing, or one made because a volume came
    /// or went.  A start's listing waits for a drive mapped to a server that
    /// is off, so a newer listing can answer first: only the latest asked for
    /// is shown, and an older one answering after it is let go.
    /// </summary>
    private async Task ShowDrivesAsync(Task<IReadOnlyList<ReadyDrive>> listing)
    {
        var ticket = ++_drivesListing;
        var ready = await listing;
        if (_isDisposed || ticket != _drivesListing)
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

    /// <summary>
    /// Copies or moves dropped items into a folder - or items sent to the
    /// other pane of a split view, into its folder.  Whether it is a move is
    /// the window's decision - the one its drag cursor showed, from the keys
    /// the drag reported and what its source allows, or the command chosen -
    /// so it is taken as given rather than worked out again here from
    /// whatever keys seem to be down.
    /// </summary>
    public Task DropIntoPathAsync(
        IReadOnlyList<string> paths,
        string targetDirectory,
        bool move)
        => DropIntoPathWithResultAsync(paths, targetDirectory, move);

    internal Task<bool> DropIntoPathWithResultAsync(
        IReadOnlyList<string> paths,
        string targetDirectory,
        bool move)
    {
        if (paths.Count == 0)
        {
            return Task.FromResult(false);
        }

        return TransferAsync(paths, targetDirectory, move, move ? "Moving" : "Copying");
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

    internal async Task RefreshPickerNavigationPreferencesAsync()
    {
        if (!_isPickerSession || _isDisposed) return;
        var state = await _workspaceStore.LoadAsync();
        if (state is null || _isDisposed) return;
        if (!_favoriteLinksChanged && _showFavoriteLinks != state.ShowFavoriteLinks)
        {
            _showFavoriteLinks = state.ShowFavoriteLinks;
            OnPropertyChanged(nameof(ShowFavoriteLinks));
        }
        if (_favoritesChanged) return;
        foreach (var item in QuickAccess.Where(item => item.IsCustom).ToArray()) QuickAccess.Remove(item);
        foreach (var favorite in state.Favorites ?? [])
        {
            if (favorite is null || string.IsNullOrWhiteSpace(favorite.Path)
                || HomeItems.Concat(QuickAccess).Any(item => ViewAllPath.Equals(item.Path, favorite.Path))) continue;
            // Link metadata needs no directory reads or Shell icons at bind time.
            QuickAccess.Add(new FavoriteItemViewModel
            {
                Name = string.IsNullOrWhiteSpace(favorite.Name) ? favorite.Path : favorite.Name,
                Path = favorite.Path,
                Glyph = favorite.Glyph ?? "\uE8B7",
                AccentHex = favorite.AccentHex ?? "#E3B341",
                IsCustom = true
            });
        }

        // Read again: the pins a later pin or unpin here is laid over.
        _savedFavorites = FavoriteSnapshot();
    }

    private List<FavoriteState> FavoriteSnapshot() => QuickAccess.Where(item => item.IsCustom)
        .Select(item => new FavoriteState(item.Name, item.Path, item.Glyph, item.AccentHex)).ToList();

    public async Task SaveNowAsync()
    {
        if (_isDisposed) return;
        // Serialize the whole snapshot/read/merge, not just the final write.
        // A delayed read must never enqueue an older captured UI state last.
        await _stateSaving.WaitAsync();
        try { await SaveNowCoreAsync(); }
        finally { _stateSaving.Release(); }
    }

    private async Task SaveNowCoreAsync()
    {
        if (!_isStateLoaded || _isDisposed)
        {
            return;
        }
        if (SuppressShellWrites)
        {
            // A native picker keeps layout/session writes suppressed, but an
            // explicit favorite preference or pin edit still needs to persist.
            await SaveNavigationPreferencesCoreAsync();

            // So does a colour or a note set in it.  Its canvas, kept from
            // writing as well, saves those and nothing else; left to the
            // canvas's own save, a second after the last click, one set just
            // before OK was lost with the dialog, which stops that save.
            if (Tree.SuppressWrites)
            {
                await Tree.SaveAsync();
            }

            return;
        }

        // Read, merged and written in the workspace's turn among every window
        // and process (see WorkspaceStore.UpdateAsync); the merge runs off
        // this thread, so it is handed everything it needs now.
        var here = CaptureWorkspace();
        var before = _savedWorkspace!;
        var favoritesBefore = _savedFavorites;
        var ownLinks = _favoriteLinksChanged;
        var ownFavorites = _favoritesChanged;
        try
        {
            await _workspaceStore.UpdateAsync(current =>
            {
                if (_isDisposed)
                {
                    return null;
                }

                if (current is null)
                {
                    return here;
                }

                // Nothing of this window's to write - a window closed after
                // another saved its change must not write that file back as
                // it read it a moment before.
                var state = MergeWorkspace(here, before, current, favoritesBefore, ownLinks, ownFavorites);
                return JsonSerializer.SerializeToUtf8Bytes(state).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(current))
                    ? null
                    : state;
            });

            _savedWorkspace = here;
            if (ownFavorites)
            {
                _savedFavorites = here.Favorites;
            }

            if (_showFavoriteLinks == here.ShowFavoriteLinks) _favoriteLinksChanged = false;
            if (FavoriteSnapshot().SequenceEqual(here.Favorites)) _favoritesChanged = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        await Tree.SaveAsync();
    }

    /// <summary>The workspace as this window has it now, before anything of the file is merged in.</summary>
    private WorkspaceState CaptureWorkspace() => new()
    {
        SidebarWidth = SidebarWidth,
        IsMinimapVisible = IsMinimapVisible,
        IsFolderListVisible = Tree.FolderList.IsVisible,

        // A file dialog's hidden items are its caller's rules, not a choice.
        ShowHiddenItems = !_isPickerSession && Tree.ShowHiddenItems,
        CanvasLayout = _savedLayout,
        CanvasSort = (_isPickerSession ? ItemSort.Default : Orders.Default).ToSetting(),
        CanvasSortScope = FolderOrders.ScopeSetting(Orders.Scope),
        CanvasLayoutOrder = FolderOrders.FlowSetting(Orders.Flow),
        FolderSorts = _isPickerSession ? null : Orders.Saved(),
        CanvasRenderer = _savedRenderer,
        NestedLeftDrag = WorkspaceState.LeftDragSetting(_leftDrag),
        NestedLeftDragHintShown = _leftDragHintShown,
        CanvasLayersOff = CanvasLayers.OffSetting(_layers),
        ShowFavoriteLinks = _showFavoriteLinks,
        IsSplit = _isSplit,
        SplitOrientation = SplitLayout.OrientationSetting(_splitOrientation),
        SplitRatio = _splitRatio,
        ActivePane = _isSplit ? _activePaneIndex : 0,
        Favorites = FavoriteSnapshot()
    };

    /// <summary>
    /// What a save writes: the workspace as the file has it now
    /// (<paramref name="there"/>), with what this window changed since it
    /// last read or wrote it (<paramref name="here"/> against
    /// <paramref name="before"/>) laid over it - a folder's order and a pin
    /// one by one - and nothing else.  Every other window, dialog and
    /// process does the same, so none puts back over another's change what
    /// it read at its start.  A file dialog never writes the canvas's
    /// settings, and a folder window never the everyday window's view: its
    /// layout and split are its own.  The pin preference and the pins are
    /// this window's to write once it changed them (<paramref name="ownLinks"/>,
    /// <paramref name="ownFavorites"/>), as a dialog's are.
    /// </summary>
    private WorkspaceState MergeWorkspace(
        WorkspaceState here,
        WorkspaceState before,
        WorkspaceState there,
        List<FavoriteState> favoritesBefore,
        bool ownLinks,
        bool ownFavorites)
    {
        var settings = !_isPickerSession;
        var view = !_isPickerSession && !_isFolderWindow;
        return new WorkspaceState
        {
            SidebarWidth = Own(here.SidebarWidth, before.SidebarWidth, there.SidebarWidth),
            IsMinimapVisible = Own(here.IsMinimapVisible, before.IsMinimapVisible, there.IsMinimapVisible),
            IsFolderListVisible = Own(here.IsFolderListVisible, before.IsFolderListVisible, there.IsFolderListVisible),
            ShowHiddenItems = settings ? Own(here.ShowHiddenItems, before.ShowHiddenItems, there.ShowHiddenItems) : there.ShowHiddenItems,
            CanvasLayout = view ? Own(here.CanvasLayout, before.CanvasLayout, there.CanvasLayout) : there.CanvasLayout,
            CanvasSort = settings ? Own(here.CanvasSort, before.CanvasSort, there.CanvasSort) : there.CanvasSort,
            CanvasSortScope = settings ? Own(here.CanvasSortScope, before.CanvasSortScope, there.CanvasSortScope) : there.CanvasSortScope,
            CanvasLayoutOrder = settings ? Own(here.CanvasLayoutOrder, before.CanvasLayoutOrder, there.CanvasLayoutOrder) : there.CanvasLayoutOrder,
            FolderSorts = settings ? MergeFolderSorts(here.FolderSorts, before.FolderSorts, there.FolderSorts) : there.FolderSorts,
            CanvasRenderer = settings ? Own(here.CanvasRenderer, before.CanvasRenderer, there.CanvasRenderer) : there.CanvasRenderer,
            NestedLeftDrag = settings ? Own(here.NestedLeftDrag, before.NestedLeftDrag, there.NestedLeftDrag) : there.NestedLeftDrag,
            NestedLeftDragHintShown = settings
                ? Own(here.NestedLeftDragHintShown, before.NestedLeftDragHintShown, there.NestedLeftDragHintShown)
                : there.NestedLeftDragHintShown,
            CanvasLayersOff = settings && !(here.CanvasLayersOff ?? []).SequenceEqual(before.CanvasLayersOff ?? [])
                ? here.CanvasLayersOff
                : there.CanvasLayersOff,
            ShowFavoriteLinks = ownLinks ? here.ShowFavoriteLinks : there.ShowFavoriteLinks,
            IsSplit = view ? Own(here.IsSplit, before.IsSplit, there.IsSplit) : there.IsSplit,
            SplitOrientation = view ? Own(here.SplitOrientation, before.SplitOrientation, there.SplitOrientation) : there.SplitOrientation,
            SplitRatio = view ? Own(here.SplitRatio, before.SplitRatio, there.SplitRatio) : there.SplitRatio,
            ActivePane = view ? Own(here.ActivePane, before.ActivePane, there.ActivePane) : there.ActivePane,
            Favorites = ownFavorites ? MergeFavorites(here.Favorites, favoritesBefore, there.Favorites) : there.Favorites ?? []
        };
    }

    /// <summary>This window's value where it changed it since <paramref name="before"/>, the file's otherwise.</summary>
    private static T Own<T>(T here, T before, T there) =>
        EqualityComparer<T>.Default.Equals(here, before) ? there : here;

    /// <summary>
    /// The folders' own orders as the file has them, with the folders this
    /// window sorted, let go of or renamed since <paramref name="before"/>
    /// taken out and this window's orders of them put in, as the most
    /// recently sorted: another window's folders keep theirs.
    /// </summary>
    private static List<FolderSortState>? MergeFolderSorts(
        List<FolderSortState>? here,
        List<FolderSortState>? before,
        List<FolderSortState>? there)
    {
        var mine = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in here ?? [])
        {
            mine[entry.Path] = entry.Sort;
        }

        var old = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in before ?? [])
        {
            old[entry.Path] = entry.Sort;
        }

        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, sort) in mine)
        {
            if (!old.TryGetValue(path, out var was) || was != sort)
            {
                changed.Add(path);
            }
        }

        changed.UnionWith(old.Keys.Where(path => !mine.ContainsKey(path)));
        if (changed.Count == 0)
        {
            return there;
        }

        var merged = (there ?? [])
            .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Path) && !changed.Contains(entry.Path))
            .ToList();
        merged.AddRange((here ?? []).Where(entry => changed.Contains(entry.Path)));
        if (merged.Count > FolderOrders.MaximumFolders)
        {
            merged.RemoveRange(0, merged.Count - FolderOrders.MaximumFolders);
        }

        return merged;
    }

    /// <summary>
    /// The pins as the file has them, with the folders this window pinned
    /// since <paramref name="before"/> added and the ones it unpinned taken
    /// off: a pin made meanwhile in a dialog or another window stays.
    /// </summary>
    private static List<FavoriteState> MergeFavorites(List<FavoriteState> here, List<FavoriteState> before, List<FavoriteState>? there)
    {
        var merged = (there ?? []).Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Path)).ToList();
        foreach (var unpinned in before.Where(old => !here.Any(item => ViewAllPath.Equals(item.Path, old.Path))))
        {
            merged.RemoveAll(item => ViewAllPath.Equals(item.Path, unpinned.Path));
        }

        foreach (var pinned in here.Where(item => !before.Any(old => ViewAllPath.Equals(old.Path, item.Path))))
        {
            if (!merged.Any(item => ViewAllPath.Equals(item.Path, pinned.Path)))
            {
                merged.Add(pinned);
            }
        }

        return merged;
    }

    private async Task SaveNavigationPreferencesCoreAsync()
    {
        if (_isDisposed || !_favoriteLinksChanged && !_favoritesChanged) return;
        var links = _showFavoriteLinks;
        var favorites = FavoriteSnapshot();
        var favoritesBefore = _savedFavorites;
        var saveLinks = _favoriteLinksChanged;
        var saveFavorites = _favoritesChanged;
        try
        {
            await _workspaceStore.UpdateAsync(current =>
            {
                if (_isDisposed) return null;
                current ??= new WorkspaceState();
                if (saveLinks) current.ShowFavoriteLinks = links;
                if (saveFavorites) current.Favorites = MergeFavorites(favorites, favoritesBefore, current.Favorites);
                return current;
            });
            if (saveLinks && _showFavoriteLinks == links) _favoriteLinksChanged = false;
            if (saveFavorites)
            {
                _savedFavorites = favorites;
                if (FavoriteSnapshot().SequenceEqual(favorites)) _favoritesChanged = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Toast.ShowError(ex.Message); }
    }

    internal bool SuppressShellWrites { get; set; }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Search.Dispose();
        Tree.PropertyChanged -= OnTreePropertyChanged;
        Tree.Orders.Changed -= OnOrdersChanged;
        Tree.MessageRequested -= OnTreeMessage;
        Address.Dispose();
        Tree.Dispose();
        _changes.Dispose();

        // Its thread (an STA of its own, with the Shell's objects and COM's
        // hidden window) waits for work until told there is no more: without
        // this, every window closed left one behind, for the life of the
        // process - one per dialog in the resident dialog worker.
        _iconService.Dispose();
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
            case nameof(ViewAllViewModel.ShowHiddenItems):
                // Remembered as it is changed, from the menu or the Settings
                // page alike.  A file dialog's rules are not written down.
                if (!_isPickerSession)
                {
                    _ = SaveNowAsync();
                }

                break;
            case nameof(ViewAllViewModel.ActivePath):
                OnPropertyChanged(nameof(TabTitle));
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
    /// Says a file command is done, and leaves the message to go by itself.
    /// Waited for, it kept the command running for the seconds the message
    /// shows, and a command still running cannot be used again: a second
    /// Delete, F2 or Ctrl+V pressed meanwhile did nothing at all.
    /// </summary>
    private void ShowDone(string message) => _ = Toast.ShowSuccessAsync(message);

    /// <summary>
    /// Where this window has already been, newest first and each place once.
    /// The address bar offers these when there is nothing typed to work from.
    /// </summary>
    private IReadOnlyList<string> RecentLocations() => _history.Recent();

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

    private void RecordNavigation(string path) => _history.Record(path);

    private void GoBack()
    {
        if (_history.Back() is { } path)
        {
            _ = NavigateHistoryAsync(path);
        }
    }

    private void GoForward()
    {
        if (_history.Forward() is { } path)
        {
            _ = NavigateHistoryAsync(path);
        }
    }

    /// <summary>
    /// A step back or forward.  Only its own selection is kept out of the
    /// history: anything asked for while it reads its way there - a favourite
    /// clicked while a share wakes up - outranks it and is recorded as usual,
    /// so the next step is taken from where the window really is.
    /// </summary>
    private Task NavigateHistoryAsync(string path) => Tree.RevealAsync(path, records: false);

    private async Task GoUpAsync()
    {
        var current = Tree.FocusedPath;
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

        var name = PromptRequested?.Invoke("New folder", "Folder name", "New folder", false);
        if (name is null)
        {
            return;
        }

        try
        {
            var path = NativeShellService.CreateFolder(target, name);
            await Tree.RefreshPathAsync(target);
            ShowDone($"Created {Path.GetFileName(path)}");
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

        var name = PromptRequested?.Invoke("New text file", "File name", "New note.txt", true);
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
            // Refused either because nothing selected is there any more -
            // deleted from outside since it was selected - or because another
            // program holds the clipboard.  Trying again only helps the second.
            Toast.ShowError(paths.Any(ItemExists)
                ? "Another application is holding the clipboard — try again."
                : paths.Count == 1
                    ? $"{(Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[0])) is { Length: > 0 } name ? name : paths[0])} is no longer there."
                    : "The selected items are no longer there.");
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

    /// <summary>
    /// How a file command asks whether an item it was given is still there.
    /// Five thousand items on a share are as many questions to the network,
    /// seconds of them on a LAN and minutes over a VPN, so they are asked off
    /// the UI thread.  A test puts a slow answer here to see that nothing
    /// waits for it there.
    /// </summary>
    internal static Func<string, bool> ItemExists { get; set; } = path => File.Exists(path) || Directory.Exists(path);

    private async Task<bool> TransferAsync(IReadOnlyList<string> paths, string targetDirectory, bool move, string verb)
    {
        var given = paths.ToArray();
        var safePaths = await Task.Run(() => given
            .Where(ItemExists)
            .Where(path => !NativeShellService.IsInvalidMoveTarget(path, targetDirectory))
            .ToArray());
        if (_isDisposed)
        {
            return false;
        }

        if (safePaths.Length == 0)
        {
            Toast.ShowError("This drop target is not valid for the selected item(s).");
            return false;
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
                // Moved away: nothing that was selected there is any more, in
                // whichever pane it was selected.
                Tree.ForgetSelected(safePaths);
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
            ShowDone(
                $"{(move ? "Moved" : "Copied")} {safePaths.Length} item(s) to {(string.IsNullOrEmpty(targetName) ? targetDirectory : targetName)}");
            return true;
        }
        catch (OperationCanceledException)
        {
            await RefreshAfterOperationAsync(targetDirectory, sourceDirectories);
            ShowDone($"{(move ? "Move" : "Copy")} cancelled");
        }
        catch (Exception ex)
        {
            Toast.ShowError(ex.Message);
        }

        return false;
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
        var isFolder = Tree.Selection.TryGetItem(path, out var selected) ? selected.IsDirectory : Directory.Exists(path);
        var newName = PromptRequested?.Invoke("Rename", "Enter a new name", currentName, !isFolder);
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

            ShowDone($"Renamed to {Path.GetFileName(renamed)}");
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

            ShowDone($"Duplicated {paths.Count} item(s)");
        }
        catch (OperationCanceledException)
        {
            await RefreshAfterOperationAsync(null, paths.Select(Path.GetDirectoryName));
            ShowDone("Duplicate cancelled");
        }
        catch (Exception ex)
        {
            Toast.ShowError(ex.Message);
        }
    }

    /// <summary>
    /// How many of the selected items the canvas being worked with draws, as
    /// the window counts them; null where it cannot say.  Hidden items or the
    /// Files layer switched off after Ctrl+A leave items selected that nothing
    /// shows, and they stay selected so that switching back on brings them
    /// back as they were.
    /// </summary>
    internal Func<int?>? ShownSelectionCount { get; set; }

    /// <summary>
    /// Whether Delete may go on: at once when the canvas shows everything
    /// selected, otherwise once the user agrees to it after being told how
    /// many of the items it would take are not shown - desktop.ini or .git
    /// were recycled with the rest without a word.
    /// </summary>
    private bool ConfirmUnshownSelection()
    {
        var count = Tree.Selection.Count;
        if (ShownSelectionCount?.Invoke() is not { } shown || shown >= count)
        {
            return true;
        }

        return ConfirmRequested?.Invoke(
            "Delete",
            $"{count - shown:N0} of the {count:N0} selected items are not shown. Delete all {count:N0}?",
            "Delete") == true;
    }

    private async Task DeleteSelectionAsync(bool permanently)
    {
        if (!ConfirmUnshownSelection())
        {
            return;
        }

        // What is selected and still there: a file deleted from outside since
        // it was selected is not the Shell's to be asked about.  Asked off
        // the UI thread (see ItemExists), about what was selected when Delete
        // was pressed, whatever is selected by the time the answers are in.
        var selected = Tree.SelectedPaths.ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        var paths = await Task.Run(() => selected.Where(ItemExists).ToArray());
        if (_isDisposed)
        {
            return;
        }

        if (paths.Length == 0)
        {
            Tree.Selection.Remove(selected, SelectionSource.Command);
            return;
        }

        if (permanently
            && ConfirmRequested?.Invoke("Permanently delete", $"Permanently delete {paths.Length} item(s)? This cannot be undone.", "Delete") != true)
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

            // Let go of explicitly, in every pane: a folder the nested canvas
            // selected in need not have a node for its refresh to prune.
            Tree.ForgetSelected(paths);
            foreach (var parent in parents)
            {
                await Tree.RefreshPathAsync(parent!);
            }

            ShowDone(permanently ? "Deleted" : "Moved to Recycle Bin");
        }
        catch (OperationCanceledException)
        {
            await RefreshAfterOperationAsync(null, parents);
            ShowDone("Delete cancelled");
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
                    && ConfirmRequested?.Invoke("Open", $"Open all {files.Length:N0} selected files?", "Open") != true)
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
                NativeShellService.ShowInWindowsExplorer(path);
            }
            catch (Exception ex)
            {
                Toast.ShowError(ex.Message);
            }
        }
    }

    private void AddSelectionToFavorites()
    {
        PinFolders(Tree.SelectedOrActivePaths);
        _ = SaveNowAsync();
    }

    /// <summary>Adds each of the folders among <paramref name="paths"/> to Home, unless it is there already.</summary>
    private void PinFolders(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(Directory.Exists))
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
            _favoritesChanged = true;
            QuickAccess.Add(item);
        }
    }

    private void RemoveFavorite(FavoriteItemViewModel? favorite)
    {
        if (favorite is { IsCustom: true })
        {
            if (!QuickAccess.Remove(favorite)) return;
            _favoritesChanged = true;
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

        var note = PromptRequested?.Invoke("Note", $"Note for {node.DisplayName}", node.Note, false);
        if (note is not null)
        {
            Tree.ApplyNote(node, note);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// What opening a search result does: a file opens in its program; a
    /// folder is shown on the canvas and gone into, as a double-click there
    /// would.
    /// </summary>
    private void OpenSearchResult(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            _ = Tree.RevealPathAsync(path);
            return;
        }

        try
        {
            NativeShellService.Open(path);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Toast.ShowError($"{Path.GetFileName(path)} could not be opened: {exception.Message}");
        }
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

    /// <summary>
    /// Windows Explorer on the folder the paths share, with all of them
    /// selected; false where it cannot be done so, for the caller to show the
    /// first.  Not while folders open through UltraExplorer: the Shell finds
    /// UltraExplorer's own windows - they answer "Show in folder" - and would
    /// select the items there, not in Explorer.
    /// </summary>
    public static bool ShowInExplorer(IReadOnlyList<string> paths) =>
        !Picker.Integration.DialogIntegrationStore.Read().Enabled
        && paths.Count > 0 && WithItems(paths, (folder, items) => SHOpenFolderAndSelectItems(folder, (uint)items.Length, items, 0) == 0);

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
