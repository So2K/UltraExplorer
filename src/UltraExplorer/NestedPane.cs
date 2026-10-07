using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Archives;
using UltraExplorer.ViewModels;

namespace UltraExplorer;

/// <summary>
/// What a <see cref="NestedPane"/> asks of the window it is in: whatever
/// belongs to the window rather than to one pane - its menus, the drag it is
/// carrying, where the keyboard goes, the drives every pane shows and the
/// background pass every pane's tree shares.
/// </summary>
internal interface INestedPaneHost
{
    /// <summary>Whether the window is a file dialog, which never drags anything out of the canvas.</summary>
    bool IsPickerMode { get; }

    /// <summary>
    /// The folder a file dialog's view has come to rest on.  <paramref name="movedByUser"/>:
    /// the user brought it there by hand, which moves the dialog there even
    /// from the folder it was opened at, still selected for having been gone to.
    /// </summary>
    void PickerFolderChanged(string? folder, bool movedByUser);
    void PickerFileOpened(string path);
    void PickerFolderOpened(string path);
    void EnsureNestedLocation(NestedPane pane, string folder);

    /// <summary>
    /// The paths a drag that started on one of the window's canvases is
    /// carrying, while it is carried: a drop on their own folder then moves
    /// nothing anywhere.  Null while no such drag is on.
    /// </summary>
    string[]? NestedDragPaths { get; set; }

    /// <summary>Whether <paramref name="pane"/> is the pane being worked with, which the window-wide chrome follows.</summary>
    bool IsActivePane(NestedPane pane);

    /// <summary>
    /// <paramref name="pane"/> becomes the pane being worked with - it was
    /// clicked, or took the keyboard - and the window's selection, history
    /// and chrome follow it.  Nothing if it already is.
    /// </summary>
    void ActivatePane(NestedPane pane);

    /// <summary>A slice of a tree's background pass, run a frame at a time behind the frames of every pane.</summary>
    void PostSortSlice(Action slice);

    /// <summary>The drives, shares and distributions the window knows of, given to every pane's tree again.</summary>
    void SyncNestedRoots();

    /// <summary>The keyboard to <paramref name="pane"/>'s canvas, as far as the window may move it there.</summary>
    void FocusCanvas(NestedPane pane);

    /// <summary>A drag has ended: no pane's folder is lit as the place it would land any more.</summary>
    void ClearDropTargets();

    // The window's menus (MainWindow.ShellMenus.cs): the Shell's, built while
    // the right button is down, and the app's own where the Shell has none.

    /// <summary>Lets go of a menu built for a press whose release asked for another, or for none.</summary>
    void DropPreparedMenu();

    /// <summary>The Shell's menu for <paramref name="paths"/>, built now, while the right button is still down.</summary>
    void PrepareShellMenu(bool background, IReadOnlyList<string> paths);

    /// <summary>The Shell's menu for the open space of <paramref name="folder"/>: true when it was shown.</summary>
    bool ShowFolderAreaShellMenu(string folder, FrameworkElement origin, Point point);

    /// <summary>The app's own menu for the open space of a folder, or of the canvas outside every folder.</summary>
    void ShowFolderAreaMenu(FrameworkElement placementTarget, ViewAllNodeViewModel? area);

    /// <summary>The menu for items, the Shell's with the canvas's entries below it: true when it was the Shell's.</summary>
    bool ShowContextMenu(IReadOnlyList<string> paths, FrameworkElement origin, Point point, bool includeCanvasCommands, bool fallBack);

    /// <summary>The app's own menu for the selection.</summary>
    void ShowSelectionMenu(FrameworkElement placementTarget);

    // What a drag over the window carries, read once per drag whichever pane
    // it crosses (MainWindow.xaml.cs).

    /// <summary>The files the drag carries, read from it once: false when it carries none.</summary>
    bool TryGetDropPaths(IDataObject data, out string[] paths);

    /// <summary>Whether the drag's items may not go into <paramref name="folder"/>, asked once for each folder the pointer comes to.</summary>
    bool IsDropRefused(string folder, Func<bool> refuses);

    /// <summary>The drag left or was dropped: what it carried is not kept for the next one.</summary>
    void ForgetDropPaths();

    /// <summary>
    /// Runs a drop's transfer, keeping an OLE source's temporary files alive
    /// until they are copied: true when the drop is reported as
    /// <paramref name="reported"/>.
    /// </summary>
    bool CompleteExternalDrop(IDataObject data, IReadOnlyList<string> paths, DragDropEffects reported, Func<Task<bool>> beginTransfer)
        => ExternalFileDrop.Complete(Dispatcher.CurrentDispatcher, beginTransfer);
}

/// <summary>
/// One pane of the nested canvas: the canvas, the tree it draws, and
/// everything around them that is the pane's own - the camera kept between
/// sessions, the name filter and the sort headers over it, its beacons, and
/// the menus and drags that start or end on it.
///
/// <para>The canvas draws folders; everything a folder can be asked to do -
/// select it, open its menu, copy it, rename it, show its files in the list -
/// still goes through the tree view model, exactly as a click on a tree node
/// does.  Clicking a cell selects its path there, and whatever the rest of
/// the window selects (the address bar, the list, back and forward, a search
/// result) is mirrored back onto the canvas and flown to.  So the pictures
/// are views of one selection, and every command works in each.</para>
///
/// <para>In a split view that one selection is the pane being worked with's.
/// The other pane keeps its own (<see cref="KeptSelection"/>), shown on its
/// canvas, and the two change places when the other pane is clicked
/// (<see cref="Activate"/>, <see cref="Deactivate"/>): every command, the
/// address bar, the list and the status bar then follow that pane without
/// knowing there are two.  So does Back and Forward, each pane having its
/// own <see cref="History"/>.</para>
///
/// <para>What is the window's and not one pane's it asks the window for
/// (<see cref="INestedPaneHost"/>).  A window has one pane until its view
/// is split, and only then makes a second: one pane costs exactly what the
/// window's one canvas always did.</para>
/// </summary>
internal sealed class NestedPane
{
    private static readonly Color NoteBeaconColour = Color.FromRgb(0xC8, 0xD2, 0xDC);
    private static readonly Color PinBeaconColour = Color.FromRgb(0xFF, 0xD6, 0x6B);
    private static readonly Color ActiveBeaconColour = Color.FromRgb(0x60, 0xCD, 0xFF);
    private static readonly Color SearchBeaconColour = Color.FromRgb(0x4C, 0xC9, 0xD8);
    private const int SearchBeaconLimit = 100;
    private const int FilterBeaconLimit = 150;

    private readonly INestedPaneHost _host;
    private readonly MainViewModel _viewModel;

    /// <summary>Where the Shell's icons for this pane's file names arrive (see <see cref="ShellIconService.SubscribeCanvas"/>).</summary>
    private readonly FrameInbox<IconArrival> _iconInbox;

    private DispatcherTimer? _saveTimer;
    private DispatcherTimer? _beaconTimer;
    private DispatcherTimer? _filterTimer;
    private DispatcherTimer? _headerTimer;
    private bool _cameraRestored;

    /// <summary>
    /// Set while a file dialog's camera was last moved by the user's hand,
    /// until the header timer tells the dialog where it came to rest; a move
    /// of the program's own - a flight, a camera put back - clears it.
    /// </summary>
    private bool _movedByUser;

    /// <summary>
    /// Set while the camera was last moved by the user's own hand - the
    /// wheel, a drag, a zoom key - in a window or a file dialog alike; a move
    /// of the program's own clears it.
    /// </summary>
    private bool _movedByHand;

    /// <summary>
    /// The folder the view is in, known only by the way to a deeper one (a
    /// partial listing), as the user's hand left the camera in it and it is
    /// let be listed: the folder in view - the one nearest the middle of the
    /// view - and its place and size in the folder's own frame, where that is
    /// one unit wide.  The listing places it among its sisters, smaller and
    /// elsewhere, while the folder holding the view stays put: noted, it is
    /// kept where it was on screen instead (<see cref="OnFolderLoadedKeepInView"/>).
    /// </summary>
    private (NestedFolder Sparse, NestedFolder Child, double X, double Y, double Scale)? _sparseInView;

    /// <summary>
    /// Set while the camera the last session left is on its way back - the
    /// folders on the way still being read - and nothing has moved it since:
    /// the canvas shows an overview meanwhile, which is no place to keep.
    /// </summary>
    private bool _cameraRestoring;

    /// <summary>
    /// The camera the last session left, when it was on a drive that had not
    /// answered as the window started: put back once the drive is among the
    /// cells (<see cref="DriveArrived"/>), as a start that waited for the drive
    /// would have put it back - unless something has moved the camera since.
    /// </summary>
    private NestedCameraState? _cameraOnLateDrive;

    private bool _detached;

    /// <summary>Set while a gesture on this pane's canvas is being applied to the shared selection, so its echo is not loaded back.</summary>
    private bool _applyingCanvasSelection;

    /// <summary>Set while the shared selection is copied into <see cref="KeptSelection"/>, which the canvas already shows.</summary>
    private bool _keepingSelection;

    /// <summary>Set while the shared selection takes what this pane kept (<see cref="Activate"/>), which the canvas already shows.</summary>
    private bool _activating;

    /// <summary>The flight asked for last, which a flight asked for before it and still reading its way there stands down for (see <see cref="FlyToAsync"/>).</summary>
    private int _flightTicket;

    /// <summary>The second pane's camera to start from: where it was last time, or where the first pane is (see <see cref="StartAt"/>).</summary>
    private NestedCameraState? _startCamera;

    /// <summary>The folder the header last named, so a camera that moves within it changes nothing.</summary>
    private string? _headerPath = string.Empty;

    /// <summary>The selection the sort folder was last worked out from: the shared one or the kept one, whose versions are counted apart.</summary>
    private ItemSelection? _sortSelection;

    private long _sortSelectionVersion = -1;

    /// <summary>The selected folder, or the folder the selected file is in: what the headers sort, on the nested canvas while it is in view.</summary>
    private string? _sortSelectionFolder;

    // What the headers last showed: they are brought up to date on every
    // move of the camera, and nearly always nothing they show has changed.
    private string? _headerFolder;
    private ItemSort _headerSort;
    private SortScope _headerScope;
    private bool _headersShown;

    /// <param name="iconInbox">
    /// The inbox the Shell's icons for this pane arrive in: the icon
    /// service's own for the first pane, one of its own for any other.
    /// </param>
    /// <param name="index">0 for the first pane, 1 for the second of a split view: which of the workspace's cameras is the pane's.</param>
    /// <param name="history">Where the pane has been: the window's own for the first pane, a new one for the second.</param>
    public NestedPane(INestedPaneHost host, MainViewModel viewModel, NestedPaneView view, FrameInbox<IconArrival> iconInbox, int index = 0, NavigationHistory? history = null)
    {
        _host = host;
        _viewModel = viewModel;
        _iconInbox = iconInbox;
        View = view;
        Index = index;
        History = history ?? new NavigationHistory();
    }

    /// <summary>The pane's parts: the header, the strip and the canvas.</summary>
    public NestedPaneView View { get; }

    /// <summary>The pane's canvas, in its view.</summary>
    public NestedCanvas Canvas => View.Canvas;

    /// <summary>The folders this pane's canvas draws, read for it alone.</summary>
    public NestedTree Tree { get; } = new();

    /// <summary>0 for the first pane, 1 for the second of a split view.</summary>
    public int Index { get; }

    /// <summary>Where this pane has been, for Back and Forward while it is the pane being worked with.</summary>
    public NavigationHistory History { get; }

    /// <summary>
    /// What this pane has selected while another pane is being worked with:
    /// its canvas shows it, and the window's selection takes it back when
    /// the pane is activated.  Stale - not looked at - while the pane is the
    /// one being worked with, whose selection is the window's.
    /// </summary>
    public ItemSelection KeptSelection { get; } = new();

    /// <summary>This pane's selection: the window's while it is the pane being worked with, its kept one otherwise.</summary>
    public ItemSelection Selection => IsActive ? _viewModel.Tree.Selection : KeptSelection;

    /// <summary>Whether this is the pane being worked with.</summary>
    public bool IsActive => _host.IsActivePane(this);

    /// <summary>
    /// The path this pane is at: the window's active path while it is the
    /// pane being worked with; otherwise what it keeps in focus, or the one
    /// thing it has selected, or the folder in view.
    /// </summary>
    public string? FocusPath => IsActive
        ? _viewModel.Tree.ActivePath is { Length: > 0 } active ? active : null
        : KeptSelection.Focus ?? (KeptSelection.Count > 0 ? KeptSelection.Paths[0] : null) ?? Canvas.FolderInView?.FullPath;

    /// <summary>
    /// For the checks: flights (<see cref="FlyToAsync"/>) still reading the
    /// folders on their way, the camera not yet told where to go.
    /// </summary>
    internal int FlightsUnderWay { get; private set; }

    /// <summary>Set once the pane has its drives, orders and hidden rules: until then it has nothing to show and gathers nothing.</summary>
    public bool IsReady { get; private set; }

    private bool IsNested => _viewModel.IsNestedLayout;

    private Dispatcher Dispatcher => View.Dispatcher;

    /// <summary>Wires the canvas, the tree and the strip together, and to the window.  Once, when the pane is made.</summary>
    public void Attach()
    {
        Tree.PostBackground = _host.PostSortSlice;

        // Changes on disk: before the drives go in, so every drive takes its
        // watch from the hub.  The tree registers what it reads; the canvas
        // takes the hub's changes in at the start of its frames and hands them
        // to the tree view model, which gives each pane's tree its own.
        Tree.Changes = _viewModel.Changes;
        _viewModel.Tree.AddNestedChanges(Tree);
        Canvas.AttachChanges(_viewModel.Changes, _viewModel.Tree);

        Canvas.Tree = Tree;

        // A partial folder the view is inside was only needed on the way to
        // a deeper one, and is listed once the camera goes into it.  Any
        // other drawn - beside the folder in view, known by a beacon's
        // chain - is listed as every folder drawn there is.
        // The one the view is in, listed where the user's hand brought the
        // camera to rest, keeps the folder in view where it is on screen
        // (NoteSparseInView).
        Tree.PartialListingReadAllowed = folder =>
        {
            var allowed = !Canvas.IsCameraMoving && Canvas.Anchor is { } anchor
                && (anchor.IsComputer ? !_host.IsPickerMode
                    : MainWindow.IsNestedPathInside(folder.FullPath, anchor.FullPath)
                        || !MainWindow.IsNestedPathInside(anchor.FullPath, folder.FullPath));
            if (allowed && ReferenceEquals(folder, Canvas.Anchor))
            {
                NoteSparseInView(folder);
            }

            return allowed;
        };
        Canvas.MarkLookup = _viewModel.Marks.Get;
        Canvas.IconLookup = LookUpFileIcon;
        Canvas.IconArrivals = _iconInbox;
        Tree.FolderLoaded += OnFolderLoadedForIcons;
        Tree.FolderLoaded += OnFolderLoadedKeepInView;
        Canvas.OpenRequested += OnOpenRequested;
        Canvas.FavoriteLinkRequested += OnFavoriteLinkRequested;
        Canvas.ContextMenuRequested += OnContextMenuRequested;
        Canvas.ContextMenuPressed += OnContextMenuPressed;
        Canvas.DragRequested += OnDragRequested;
        Canvas.CopyPathRequested += OnCopyPathRequested;
        Canvas.CameraChanged += OnCameraChanged;
        Canvas.UserCameraMoved += OnUserCameraMoved;
        Canvas.FilterChanged += OnFilterChanged;
        Canvas.SelectionCommitted += OnSelectionCommitted;
        Canvas.MarqueePreview += OnMarqueePreview;
        Canvas.MarqueeStarted += OnMarqueeStarted;
        Canvas.DragOver += OnCanvasDragOver;
        Canvas.DragLeave += OnCanvasDragLeave;
        Canvas.Drop += OnCanvasDrop;

        View.CanvasFilterBox.TextChanged += OnFilterTextChanged;
        View.CanvasFilterBox.PreviewKeyDown += OnFilterPreviewKeyDown;
        View.CanvasFilterPrevious.Click += (_, _) => Canvas.GoToMatch(-1);
        View.CanvasFilterNext.Click += (_, _) => Canvas.GoToMatch(1);
        View.CanvasFilterClear.Click += (_, _) => ClearFilter();
        foreach (var header in View.SortHeaders.Children.OfType<Button>())
        {
            header.Click += OnSortHeaderClick;
        }

        // Clicked anywhere - the canvas, its strip, its header - or given the
        // keyboard, the pane is the one being worked with.  On the way down,
        // so the canvas's own press already acts on the pane's selection.
        View.PreviewMouseDown += OnViewPreviewMouseDown;
        View.IsKeyboardFocusWithinChanged += OnViewKeyboardFocusWithinChanged;
        KeptSelection.Changed += OnKeptSelectionChanged;

        _filterTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(140) };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            Canvas.SetFilter(View.CanvasFilterBox.Text);
        };

        _saveTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            CaptureCamera();
            _viewModel.Tree.ScheduleSave();
        };

        // The header names the folder in view as the last picture has it, so
        // it is brought up to date once the camera has come to rest and the
        // picture has been drawn, not on the move.  So is a file dialog's
        // folder, once the user has moved the camera by hand.
        _headerTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _headerTimer.Tick += (_, _) =>
        {
            _headerTimer.Stop();
            UpdateHeader();
            if (!_host.IsPickerMode)
            {
                return;
            }

            if (_movedByUser)
            {
                _movedByUser = false;
                _host.PickerFolderChanged(FolderAtRest(), movedByUser: true);
            }
            else if (View.PaneHeader.Visibility == Visibility.Visible)
            {
                _host.PickerFolderChanged(Canvas.FolderInView?.FullPath, movedByUser: false);
            }
        };

        _beaconTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _beaconTimer.Tick += (_, _) =>
        {
            _beaconTimer.Stop();
            RebuildBeacons();
        };

        UpdateSortHeaders();
    }

    /// <summary>Lets go of the hub, the icons and the tree: the window is going, or the pane is.</summary>
    public void Detach()
    {
        if (_detached) return;
        _detached = true;
        IsReady = false;
        _flightTicket++;
        _saveTimer?.Stop();
        _beaconTimer?.Stop();
        _filterTimer?.Stop();
        _headerTimer?.Stop();
        View.PreviewMouseDown -= OnViewPreviewMouseDown;
        View.IsKeyboardFocusWithinChanged -= OnViewKeyboardFocusWithinChanged;
        KeptSelection.Changed -= OnKeptSelectionChanged;
        _viewModel.Tree.RemoveKeptSelection(KeptSelection);
        Tree.FolderLoaded -= OnFolderLoadedForIcons;
        Tree.FolderLoaded -= OnFolderLoadedKeepInView;
        Canvas.OpenRequested -= OnOpenRequested;
        Canvas.FavoriteLinkRequested -= OnFavoriteLinkRequested;
        Canvas.ContextMenuRequested -= OnContextMenuRequested;
        Canvas.ContextMenuPressed -= OnContextMenuPressed;
        Canvas.DragRequested -= OnDragRequested;
        Canvas.CopyPathRequested -= OnCopyPathRequested;
        Canvas.CameraChanged -= OnCameraChanged;
        Canvas.UserCameraMoved -= OnUserCameraMoved;
        Canvas.FilterChanged -= OnFilterChanged;
        Canvas.SelectionCommitted -= OnSelectionCommitted;
        Canvas.MarqueePreview -= OnMarqueePreview;
        Canvas.MarqueeStarted -= OnMarqueeStarted;
        Canvas.DragOver -= OnCanvasDragOver;
        Canvas.DragLeave -= OnCanvasDragLeave;
        Canvas.Drop -= OnCanvasDrop;
        View.CanvasFilterBox.TextChanged -= OnFilterTextChanged;
        View.CanvasFilterBox.PreviewKeyDown -= OnFilterPreviewKeyDown;
        foreach (var header in View.SortHeaders.Children.OfType<Button>()) header.Click -= OnSortHeaderClick;
        Canvas.Tree = null;
        Canvas.IconArrivals = null;
        _viewModel.Icons.UnsubscribeCanvas(_iconInbox);
        Canvas.AttachChanges(null, null);
        _viewModel.Tree.RemoveNestedChanges(Tree);
        Tree.Changes = null;
        Tree.Dispose();
    }

    /// <summary>
    /// Once the window has its state: the remembered orders before the first
    /// drive goes in, so nothing is ever placed in name order only to be
    /// placed again - the same orders as the tree and the list, so a folder
    /// sorted anywhere is sorted everywhere - then the drives as the first row
    /// of cells, and the hidden-folder rules.
    /// </summary>
    public void Initialize(IReadOnlyList<NestedRoot> roots)
    {
        ApplyArchivePreference();
        Tree.Orders = _viewModel.Orders;
        Tree.SetRoots(roots);
        Tree.IncludeHidden = _viewModel.Tree.ShowHiddenItems;
        Tree.SetUserHidden(_viewModel.Tree.HiddenPaths);
        IsReady = true;
        Canvas.ShowFavoriteLinks = _viewModel.ShowFavoriteLinks;
        RebuildBeacons();
    }

    /// <summary>Applies the opt-in mode and leaves a virtual archive before its tile becomes a regular file.</summary>
    public void ApplyArchivePreference()
    {
        var enabled = _viewModel.BrowseArchives && IsNested && !_host.IsPickerMode;
        if (!enabled)
        {
            _flightTicket++;
            var viewed = Canvas.FolderInView;
            while (viewed is { IsInArchive: true }) viewed = viewed.Parent;
            if (viewed is not null && Canvas.FolderInView?.IsInArchive == true)
                Canvas.FlyTo(viewed, animated: false);

            var selection = Selection;
            bool IsVirtual(string? path)
            {
                if (string.IsNullOrEmpty(path) || Tree.FindNearest(path) is not { IsInArchive: true } container) return false;
                return !container.IsArchive || !container.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase) || container.Parent?.IsInArchive == true;
            }

            string? PhysicalParent(string? path)
            {
                var folder = string.IsNullOrEmpty(path) ? null : Tree.FindNearest(path);
                while (folder is { IsInArchive: true }) folder = folder.Parent;
                return folder is { IsComputer: false } ? folder.FullPath : null;
            }

            var kept = selection.Items.Where(item => !IsVirtual(item.Path))
                .Select(item => item.IsDirectory && Tree.Find(item.Path) is { IsArchive: true }
                    ? item with { IsDirectory = false, Size = _viewModel.Tree.TryGetNode(item.Path, out var node) ? node.Entry.SizeBytes ?? 0 : 0 }
                    : item).ToArray();
            var focus = IsVirtual(selection.Focus)
                ? kept.FirstOrDefault().Path ?? PhysicalParent(selection.Focus) ?? viewed?.FullPath ?? string.Empty
                : selection.Focus;
            var anchor = IsVirtual(selection.Anchor) ? focus ?? string.Empty : selection.Anchor;
            if (kept.Length != selection.Count || kept.Any(item => selection.TryGetItem(item.Path, out var old) && old.IsDirectory != item.IsDirectory)
                || !string.Equals(focus, selection.Focus, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(anchor, selection.Anchor, StringComparison.OrdinalIgnoreCase))
            {
                selection.Apply(new SelectionEdit { Clear = true, Added = kept,
                    // Null means "leave unchanged" to ItemSelection. Empty explicitly clears a ghost focus.
                    Focus = focus ?? string.Empty, Anchor = anchor ?? string.Empty,
                    Source = SelectionSource.Command, RecordsNavigation = false });
            }
        }

        Tree.ArchivesEnabled = enabled;
    }

    private async void OnFavoriteLinkRequested(string path)
    {
        try
        {
            _host.ActivatePane(this);
            await OpenAsync(path, animated: true);
            if (_detached) return;
            _host.FocusCanvas(this);
        }
        catch (Exception exception)
        {
            _viewModel.Toast.ShowError(exception.Message);
        }
    }

    /// <summary>
    /// The pane coming into view, at startup or from the tree.  Its marks are
    /// gathered only now - while it is hidden it reads nothing - and the
    /// camera goes back to where it was, or to the selection.  Deferred to
    /// after layout, so the view is framed for the size it really has.
    /// </summary>
    /// <param name="focus">Whether the keyboard goes to the pane: to the one being worked with, not to the other of a split.</param>
    /// <param name="fly">
    /// Whether a pane whose camera is back already goes to its selection: the
    /// one being worked with does, as it always has, while the other of a
    /// split view stays where it was left.
    /// </param>
    public void Enter(bool fromStartup, bool focus = true, bool fly = true)
    {
        if (_detached) return;
        SyncSelection();
        RebuildBeacons();
        UpdateHeader();
        Dispatcher.InvokeAsync(() =>
        {
            if (_detached) return;
            Canvas.UpdateLayout();
            if (!_cameraRestored && RestoredCamera is { } camera)
            {
                // Not awaited: the window is usable while the folders on the
                // way are read, and the view jumps there once they have been.
                _cameraRestored = true;
                _ = RestoreCameraAsync(camera);
            }
            else if (fly && FocusPath is { Length: > 0 } path)
            {
                _ = FlyToAsync(path, gentle: !fromStartup, animated: false);
            }
            else if (!_cameraRestored && FocusPath is { Length: > 0 } place)
            {
                // A pane that stays where it is left still has to be somewhere
                // the first time: at its selection.
                _cameraRestored = true;
                _ = FlyToAsync(place, gentle: false, animated: false);
            }

            if (focus)
            {
                _host.FocusCanvas(this);
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Where the second pane of a split view starts, before it is first
    /// entered: the camera it goes to - where it was last time, or where the
    /// first pane is - and what it has selected: what it had selected last
    /// time, or only a place, <paramref name="focus"/>.  The pane is not the
    /// one being worked with; its selection is kept (see <see cref="Deactivate"/>).
    /// </summary>
    public void StartAt(NestedCameraState? camera, IReadOnlyList<SelectionItem> selected, string? focus)
    {
        _startCamera = camera;
        _cameraRestored = false;
        _keepingSelection = true;
        try
        {
            KeptSelection.Apply(new SelectionEdit
            {
                Clear = true,
                Added = selected,
                Anchor = focus,
                Focus = focus,
                Source = SelectionSource.Navigation
            });
        }
        finally
        {
            _keepingSelection = false;
        }

        _viewModel.Tree.AddKeptSelection(KeptSelection);
        if (focus is { Length: > 0 })
        {
            History.Record(focus);
        }

        // The canvas shows it from the start: a pane made the one being
        // worked with before it is first entered - a split put back with it
        // worked with - tells its canvas it already does (see Activate).
        SyncSelection();
    }

    // ---- the camera -------------------------------------------------------------

    /// <summary>
    /// Where the last session left this pane's camera, if anywhere: the
    /// workspace's for the first pane; for the second, where it was told to
    /// start (<see cref="StartAt"/>).
    /// </summary>
    private NestedCameraState? RestoredCamera => Index == 0 ? _viewModel.Tree.RestoredNestedCamera : _startCamera;

    /// <summary>This pane's camera as the canvas workspace keeps it, for the next session.</summary>
    private NestedCameraState? KeptCamera
    {
        get => Index == 0 ? _viewModel.Tree.NestedCamera : _viewModel.Tree.SecondPane?.NestedCamera;
        set
        {
            var tree = _viewModel.Tree;
            if (Index == 0)
            {
                tree.NestedCamera = value;
            }
            else
            {
                tree.SecondPane = (tree.SecondPane ?? new NestedPaneState(null, null)) with { NestedCamera = value };
            }
        }
    }

    /// <summary>
    /// The camera as it is now, kept for the next session: it came to rest,
    /// or the window or the pane is being closed.  One still on its way back
    /// keeps where it was going, and so does one waiting for its drive or
    /// share to answer (<see cref="_cameraOnLateDrive"/>): the overview shown
    /// meanwhile, saved, would stand for the place from then on.
    /// </summary>
    public void CaptureCamera()
    {
        if (_cameraOnLateDrive is { } waiting)
        {
            KeptCamera = waiting;
            return;
        }

        if (!_cameraRestoring && Canvas.CaptureCamera() is { } camera)
        {
            KeptCamera = camera;
        }
    }

    /// <summary>The camera the last session left put back, and noted as on its way meanwhile (see <see cref="_cameraRestoring"/>).</summary>
    private async Task RestoreCameraAsync(NestedCameraState camera)
    {
        _cameraRestoring = true;
        try
        {
            await Canvas.RestoreCameraAsync(camera);

            // On no drive among the cells: one not answered yet, put back when it has.
            if (!_detached && camera.AnchorPath is { Length: > 0 } anchor && Tree.Chain(anchor).Count == 0)
            {
                _cameraOnLateDrive = camera;
            }
        }
        finally
        {
            _cameraRestoring = false;
        }
    }

    /// <summary>
    /// A drive that had not answered as the window started is among the cells
    /// now: the camera the last session left on it is put back, unless
    /// something has moved the camera since.
    /// </summary>
    public void DriveArrived(string drive)
    {
        if (!_detached && _cameraOnLateDrive is { } camera && MainWindow.IsNestedPathInside(camera.AnchorPath, drive))
        {
            _cameraOnLateDrive = null;
            _ = RestoreCameraAsync(camera);
        }
    }

    private void OnCameraChanged()
    {
        // Moved, the camera is where somebody wants it, back or not.
        _cameraRestoring = false;
        _cameraOnLateDrive = null;

        // Whose move it was is told after it (OnUserCameraMoved): one of the
        // program's own, a flight's frame, takes the dialog nowhere.
        _movedByUser = false;
        _movedByHand = false;

        if (_host.IsActivePane(this))
        {
            _viewModel.NestedZoomLabel = Canvas.ZoomText;
        }

        // With nothing selected, the headers are for the folder in view.
        UpdateSortHeaders();
        ScheduleHeader();
        _saveTimer?.Stop();
        _saveTimer?.Start();
    }

    /// <summary>
    /// The user moved the camera by hand - the wheel, a drag, a zoom key.  In
    /// a file dialog the folder they bring it to is where the dialog is, as a
    /// folder opened in Windows' own: Save writes there and Select Folder
    /// answers it.  Told once the camera has come to rest, as the header is,
    /// whether the header is shown or not.
    /// </summary>
    private void OnUserCameraMoved()
    {
        _movedByHand = true;
        if (!_host.IsPickerMode)
        {
            return;
        }

        _movedByUser = true;
        _headerTimer?.Stop();
        _headerTimer?.Start();
    }

    /// <summary>How much of the view's width or height a folder takes up to be the one a file dialog's view rests on.</summary>
    private const double FolderAtRestShare = 0.75;

    /// <summary>
    /// The folder a file dialog's view rests on once the user has moved it:
    /// the innermost folder under the middle of the view that fills at least
    /// three quarters of its width or of its height.  Either way round, so a
    /// folder framed whole in a view far wider than it is tall counts, where
    /// <see cref="NestedCanvas.FolderInView"/> - the folder covering all of
    /// the view - would be its parent.  Null on This PC.
    /// </summary>
    private string? FolderAtRest()
    {
        var width = Canvas.ActualWidth;
        var height = Canvas.ActualHeight;
        if (!(width > 0) || !(height > 0) || Canvas.HitTest(new Point(width / 2, height / 2)) is not { } hit)
        {
            return null;
        }

        for (var folder = hit.Folder; folder is { IsComputer: false }; folder = folder.Parent)
        {
            if (Canvas.ScreenRectOf(folder) is { } cell
                && (cell.Width >= width * FolderAtRestShare || cell.Height >= height * FolderAtRestShare))
            {
                return folder.FullPath;
            }
        }

        return null;
    }

    // ---- the header ---------------------------------------------------------------

    /// <summary>
    /// The pane's header names the folder in view, as a short trail, while
    /// the view is split; nothing is worked out while the header is hidden.
    /// Asked after every move of the camera, it changes the words only when
    /// the folder does.
    /// </summary>
    public void UpdateHeader()
    {
        if (View.PaneHeader.Visibility != Visibility.Visible)
        {
            _headerPath = string.Empty;
            return;
        }

        var path = Canvas.FolderInView?.FullPath;
        if (string.Equals(path, _headerPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _headerPath = path;
        View.ShowLocation(ShortLocation(path), path ?? "This PC");
    }

    /// <summary>The header brought up to date a moment from now, once the picture it names has been drawn; nothing while it is hidden.</summary>
    public void ScheduleHeader()
    {
        if (View.PaneHeader.Visibility == Visibility.Visible)
        {
            _headerTimer?.Stop();
            _headerTimer?.Start();
        }
    }

    /// <summary>
    /// A folder as the pane's header names it: each step of its path, the
    /// drive first - "C: › Users › Me" - and for a deep one the drive and
    /// the last three, the rest an ellipsis, so the folder itself is never
    /// what is cut off.  This PC for none.
    /// </summary>
    internal static string ShortLocation(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "This PC";
        }

        IReadOnlyList<string> chain;
        try
        {
            chain = ViewAllPath.AncestorChain(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }

        var names = chain.Select(step => MainWindow.FolderName(step).TrimEnd(Path.DirectorySeparatorChar)).ToList();
        if (names.Count > 4)
        {
            names = [names[0], "…", .. names[^3..]];
        }

        return string.Join("  ›  ", names);
    }

    /// <summary>
    /// The nested version of "bring this node into view".  Navigation - the
    /// address bar, the sidebar, back and forward - flies to the folder.  A row
    /// picked in the list only needs to be visible, and usually already is.
    ///
    /// <para>The folders on the way are read first, which for a folder of
    /// thousands or a share that takes its time is a while, and another
    /// flight may be asked for meanwhile: a favourite clicked, the canvas
    /// coming back from the tree, a navigation of this pane landing here
    /// after the other pane was clicked (<see cref="Land"/>).  The camera
    /// goes where the last one asked, whichever finishes reading first - as
    /// a navigation ends where the last one asked (see
    /// <see cref="ViewAllViewModel.RevealAsync"/>).  An earlier flight that
    /// finished later took the camera back to where nobody wanted it any
    /// more, and one that jumped there stopped the flight under way.</para>
    /// </summary>
    public async Task FlyToAsync(string path, bool gentle, bool animated = true, Func<bool>? requestCurrent = null, bool isDirectory = false)
    {
        if (_detached || requestCurrent?.Invoke() == false || string.IsNullOrEmpty(path))
        {
            return;
        }

        var ticket = ++_flightTicket;
        FlightsUnderWay++;
        try
        {
            // A folder invocation already validated this directory off the UI
            // thread. Do not probe a network path synchronously a second time.
            // A folder the tree already has is one too; anything else is asked
            // about off the interface thread, where a share gone to sleep holds
            // up this flight alone, not the window.  An archive, and a folder
            // inside one, is a folder here.
            var folderPath = isDirectory || Tree.Find(path) is not null || await Task.Run(() => Tree.ArchivesEnabled
                ? ArchiveService.IsFolderLikeForNavigation(path) : Directory.Exists(path))
                ? path
                : Path.GetDirectoryName(path) ?? path;

            // Superseded while the disk was asked: nothing of this path is
            // brought into the tree for a flight nobody wants any more.
            if (_detached || ticket != _flightTicket || requestCurrent?.Invoke() == false)
            {
                return;
            }

            var folder = await Tree.MaterializePathAsync(folderPath);
            if (folder is null && !_detached && ticket == _flightTicket && requestCurrent?.Invoke() != false)
            {
                // A share or a WSL distribution the tree has only just added.
                _host.EnsureNestedLocation(this, folderPath);
                folder = await Tree.MaterializePathAsync(folderPath);
            }

            // Nowhere to go, or superseded: a flight asked for since is the one that counts.
            if (folder is null || _detached || ticket != _flightTicket || requestCurrent?.Invoke() == false)
            {
                return;
            }

            // A named destination may be a partial ancestor. Its own contents
            // are requested explicitly while every other ancestor stays lazy.
            _ = Tree.LoadAsync(folder);

            var view = new Rect(0, 0, Canvas.ActualWidth, Canvas.ActualHeight);
            if (Canvas.ScreenRectOf(folder) is { } rect)
            {
                if (gentle && rect.Width >= 24 && view.IntersectsWith(rect))
                {
                    return;
                }

                if (!gentle && view.Contains(rect) && rect.Width >= view.Width * 0.45)
                {
                    return;
                }
            }

            if (gentle && folder.Parent is { IsComputer: false } parent)
            {
                Canvas.FlyTo(parent, 0.92, animated);
            }
            else
            {
                Canvas.FlyTo(folder, 0.8, animated);
            }
            // Normal navigation defers other drive reads only until its
            // destination is framed. Its later overview reads visible roots;
            // a file picker continues to keep unvisited drive contents lazy.
            if (!_host.IsPickerMode) Canvas.LoadUnfocusedRoots = true;
        }
        finally
        {
            FlightsUnderWay--;
        }
    }

    /// <summary>Precise F focus in this active pane; no selection, navigation history or other pane is changed.</summary>
    public async Task<bool> FocusSelectionPathAsync(string path, bool? isDirectory = null,
        Func<bool>? requestCurrent = null, bool animated = false)
    {
        if (_detached || !IsActive || string.IsNullOrWhiteSpace(path) || requestCurrent?.Invoke() == false) return false;
        var ticket = ++_flightTicket;
        var tree = Tree;
        bool Current() => !_detached && IsActive && ticket == _flightTicket
            && ReferenceEquals(Canvas.Tree, tree) && requestCurrent?.Invoke() != false;
        FlightsUnderWay++;
        try
        {
            if (tree.Chain(path).Count == 0 && Current())
                _host.EnsureNestedLocation(this, isDirectory == false ? Path.GetDirectoryName(path) ?? path : path);
            if (!Current()) return false;
            // A previous hand-adjusted sparse view is no longer the requested
            // camera; its deferred keep-in-view correction must not cancel F.
            _movedByHand = false;
            return await Canvas.FocusPathAsync(path, isDirectory, Current, animated);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or ObjectDisposedException)
        {
            return false;
        }
        finally { FlightsUnderWay--; }
    }

    /// <summary>
    /// Open in other pane: this pane goes to <paramref name="folder"/> as the
    /// pane being worked with goes to a place typed in the address bar - the
    /// folder becomes what it has selected and a step of its Back and
    /// Forward, and the camera flies to it - while the pane being worked with
    /// stays the one it was.  The place asked for wins over a camera still to
    /// be put back: a pane only just made by the split goes there, not to
    /// where the last split left it.
    /// </summary>
    /// <param name="animated">Whether the camera flies there or is simply there: a pane only just made has nothing to fly from.</param>
    public async Task OpenAsync(string folder, bool animated)
    {
        if (_detached) return;
        if (IsActive)
        {
            await _viewModel.Tree.RevealPathAsync(folder);
            return;
        }

        _cameraRestored = true;
        KeptSelection.Apply(new SelectionEdit
        {
            Clear = true,
            Added = [new SelectionItem(folder, true, 0)],
            Anchor = folder,
            Focus = folder,
            Source = SelectionSource.Navigation
        });
        History.Record(folder);
        _viewModel.Tree.ScheduleSave();

        // After the pane's own entering, which a pane only just made has
        // waiting at this priority, and once it has its size.
        await Dispatcher.InvokeAsync(Canvas.UpdateLayout, DispatcherPriority.Loaded);
        if (_detached) return;
        await FlyToAsync(folder, gentle: false, animated);
    }

    /// <summary>
    /// A navigation asked for while this pane was the one worked with - a
    /// favourite, Back, a path typed in - got there after another pane had
    /// become it: this pane goes there as it would have.  What was to be
    /// selected becomes what it has selected and, unless it was a step back
    /// or forward - whose place in the history is taken already - a step of
    /// its history; the camera flies there.  The pane being worked with is
    /// left as it is.
    /// </summary>
    /// <param name="select">Whether the navigation selects what it got to, or only flies there.</param>
    /// <param name="fly">Whether the camera goes there.</param>
    /// <param name="records">Whether going there is a step for Back and Forward.</param>
    public void Land(string path, bool isDirectory, long size, bool select, bool fly, bool records)
    {
        if (_detached || IsActive)
        {
            return;
        }

        if (select)
        {
            KeptSelection.ReplaceSingle(path, isDirectory, size, SelectionSource.Navigation);
            if (records)
            {
                History.Record(path);
            }
        }

        if (fly && IsNested && IsReady)
        {
            _cameraRestored = true;
            _ = FlyToAsync(path, gentle: false, isDirectory: isDirectory);
        }
    }

    // ---- the selection, both ways ----------------------------------------------------
    //
    // The canvas's gestures reach the shared selection as edits, and every
    // other change reaches the canvas: see MainWindow.Selection.cs.

    /// <summary>The pane's selection, shown on the canvas as it is now - unless it is the one the canvas already shows being handed over.</summary>
    public void SyncSelection()
    {
        if (!_activating)
        {
            Canvas.LoadSelection(Selection);
        }
    }

    /// <summary>
    /// A gesture on the canvas: applied to the pane's selection - the shared
    /// one, the pane being the one worked with - in the same call, so a drag
    /// or a menu straight after it already acts on it, and the version it
    /// made noted as the canvas's own.
    /// </summary>
    private void OnSelectionCommitted(SelectionEdit edit)
    {
        var selection = Selection;
        _applyingCanvasSelection = true;
        try
        {
            selection.Apply(edit);
        }
        finally
        {
            _applyingCanvasSelection = false;
        }

        // A single-file picker may clamp a range, marquee or Ctrl+A to one
        // item. That normalized selection must also be what the tiles show.
        if (Canvas.SelectedCount != selection.Count)
            Canvas.LoadSelection(selection);
        else Canvas.AcknowledgeSelection(selection.Version);
    }

    /// <summary>Any other change of the shared selection: the canvas takes it in, unless it is not the picture on show.</summary>
    public void OnSharedSelectionChanged(ItemSelection selection)
    {
        // The headers are for the folder selected, or the one the selection is in.
        UpdateSortHeaders();

        if (!_applyingCanvasSelection && IsNested && IsReady)
        {
            Canvas.LoadSelection(selection);
        }
    }

    /// <summary>
    /// The selection this pane keeps while another is being worked with
    /// changed - something in it was deleted, moved away or renamed: its
    /// canvas and headers follow, as the pane being worked with's follow the
    /// shared one.
    /// </summary>
    private void OnKeptSelectionChanged(ItemSelection kept)
    {
        if (_keepingSelection || IsActive)
        {
            return;
        }

        UpdateSortHeaders();
        if (!_applyingCanvasSelection && IsNested && IsReady)
        {
            Canvas.LoadSelection(kept);
        }

        ScheduleBeacons();
    }

    /// <summary>
    /// Another pane is to be worked with: what the window's selection holds
    /// is this pane's, and is kept (<see cref="KeptSelection"/>) - the canvas
    /// already shows it, and is told it holds the kept copy - and followed
    /// through deletes and renames while it waits.
    /// </summary>
    public void Deactivate()
    {
        var shared = _viewModel.Tree.Selection;
        _keepingSelection = true;
        try
        {
            KeptSelection.Apply(new SelectionEdit
            {
                Clear = true,
                Added = [.. shared.Items],
                Anchor = shared.Anchor,
                Focus = shared.Focus ?? FocusPath,
                Source = SelectionSource.Navigation
            });
        }
        finally
        {
            _keepingSelection = false;
        }

        Canvas.AcknowledgeSelection(KeptSelection.Version);
        _viewModel.Tree.AddKeptSelection(KeptSelection);
    }

    /// <summary>
    /// This pane is to be worked with: the window's selection takes what the
    /// pane kept, which its canvas already shows, not as a step for Back and
    /// Forward.  With nothing selected, the pane's place is the folder it has
    /// in view, which the address bar and the list go to - without selecting
    /// it.  Called with the window's active pane already this one.
    /// </summary>
    public void Activate()
    {
        _viewModel.Tree.RemoveKeptSelection(KeptSelection);
        var kept = KeptSelection;
        var focus = kept.Count > 0 ? kept.Focus : Canvas.FolderInView?.FullPath ?? kept.Focus;
        var shared = _viewModel.Tree.Selection;
        _activating = true;
        _applyingCanvasSelection = true;
        try
        {
            shared.Apply(new SelectionEdit
            {
                Clear = true,
                Added = [.. kept.Items],
                Anchor = kept.Anchor ?? focus,
                Focus = focus,
                RecordsNavigation = false,
                Source = SelectionSource.Navigation
            });
        }
        finally
        {
            _applyingCanvasSelection = false;
            _activating = false;
        }

        Canvas.AcknowledgeSelection(shared.Version);
    }

    /// <summary>
    /// A press anywhere on the pane makes it the one being worked with, on
    /// the way down, before the canvas's own press acts on its selection -
    /// and brings the keyboard with it.  The canvas and the filter box take
    /// the keyboard themselves; the rest of the pane cannot - its header,
    /// the strip, the sort headers, the filter's arrows - and a press there
    /// would leave the keyboard on the other pane's canvas: the arrows and
    /// Enter would go on moving that pane's selection while Delete, F2 and
    /// Ctrl+X acted on this one's.
    /// </summary>
    private void OnViewPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var wasActive = IsActive;
        _host.ActivatePane(this);
        if (!wasActive && IsActive && !View.IsKeyboardFocusWithin && !TakesKeyboardItself(e.OriginalSource as DependencyObject))
        {
            _host.FocusCanvas(this);
        }
    }

    /// <summary>Whether a press on <paramref name="source"/> moves the keyboard there itself: it is on the canvas, or in a text box.</summary>
    private bool TakesKeyboardItself(DependencyObject? source)
    {
        for (var current = source; current is not null && !ReferenceEquals(current, View); current = MainWindow.ParentOf(current))
        {
            if (ReferenceEquals(current, Canvas) || current is TextBoxBase)
            {
                return true;
            }
        }

        return false;
    }

    private void OnViewKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            _host.ActivatePane(this);
        }
    }

    private void OnMarqueePreview(int count) => _viewModel.Tree.ShowSelectionPreview(count);

    /// <summary>
    /// The first rectangle ever drawn says, once, that left-drag used to pan
    /// and where the old behaviour is: the muscle memory is the pan.
    /// </summary>
    private void OnMarqueeStarted()
    {
        if (_viewModel.LeftDragHintShown || MainWindow.IsDiagnosticsRun)
        {
            return;
        }

        _viewModel.LeftDragHintShown = true;
        _ = _viewModel.Toast.ShowSuccessAsync(
            "Left-drag now selects. Pan with the right or middle button, Space+drag or the wheel — Settings (Ctrl+,) ▸ Mouse.");
    }

    // ---- what the canvas asks for -----------------------------------------------------

    /// <summary>
    /// A double-click on a folder has already flown into it.  A file opens -
    /// through the tree, so a file dialog's own "this is the answer" still
    /// applies.  A link has nothing inside it to fly into, so it goes where it
    /// points instead.
    /// </summary>
    private async void OnOpenRequested(NestedHit hit)
    {
        // A file inside an archive: taken out to the temporary folder, opened from there.
        if (Tree.ArchivesEnabled && hit.IsFile && hit.Folder.IsInArchive)
        {
            await _viewModel.OpenFromArchiveAsync(hit.Path);
            return;
        }

        // An archive whose names are encrypted lists once it has its password.
        if (Tree.ArchivesEnabled && !hit.IsFile && hit.Folder.IsInArchive && hit.Folder.LoadState == NestedLoadState.Failed
            && hit.Folder.ErrorMessage.Contains("Password", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (await ArchiveService.AskPasswordAsync(hit.Folder.FullPath, _viewModel.AskArchivePassword))
                    await Tree.RefreshAsync(hit.Folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
            { _viewModel.Toast.ShowError(ex.Message); }

            return;
        }

        if (!Tree.ArchivesEnabled && hit.Folder.IsInArchive) return;

        if (hit.IsFile)
        {
            if (_host.IsPickerMode)
            {
                _host.PickerFileOpened(hit.Path);
                return;
            }
            try
            {
                if (await _viewModel.Tree.SelectPathAsync(hit.Path) is { } node)
                {
                    await _viewModel.Tree.ToggleAsync(node);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _viewModel.Toast.ShowError(ex.Message);
            }

            return;
        }

        var folder = hit.Folder;
        if (_host.IsPickerMode) _host.PickerFolderOpened(folder.FullPath);
        if (!folder.IsReparsePoint)
        {
            return;
        }

        try
        {
            var target = Directory.ResolveLinkTarget(folder.FullPath, returnFinalTarget: true)?.FullName;
            if (!string.IsNullOrEmpty(target) && Directory.Exists(target))
            {
                await _viewModel.Tree.RevealPathAsync(target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _viewModel.Toast.ShowError($"Could not follow {folder.Name}: {ex.Message}");
        }
    }

    /// <summary>The folder <paramref name="path"/> is in, read again: the path was found gone.</summary>
    private async Task RefreshStaleAsync(string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } parent && Tree.Find(parent) is { } folder)
        {
            try
            {
                await Tree.RefreshAsync(folder);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
            {
            }
        }
    }

    /// <summary>
    /// F5: the folder is read again at once, and whatever the canvas read below
    /// it is out of date too - read again as it is drawn.  A change on disk, or
    /// a file operation of the window's own, needs nothing from here: the
    /// change hub brings it to the tree like any other.
    /// </summary>
    public void RefreshDeep(string path)
    {
        if (Tree.Find(path) is { } folder)
        {
            Tree.RefreshDeep(folder);
        }
    }

    /// <summary>
    /// A file's icon for the canvas, the file named by its folder and its
    /// index among the folder's shown files: whatever the icon service already
    /// has, and a question for the rest, whose answer arrives in the canvas's
    /// icon inbox (<see cref="ShellIconService.GetForCanvas"/>).  Icons are
    /// per type, looked up by the file's shared extension, so a folder of a
    /// thousand photos asks once and a frame of their names builds nothing;
    /// only programs, shortcuts and icon files are asked about one by one.
    /// </summary>
    private ImageSource? LookUpFileIcon(NestedFolder folder, int index) => _viewModel.Icons.GetForCanvas(folder, index);

    /// <summary>
    /// A folder was read: the types among its files are asked for behind
    /// everything on screen, so their icons are usually there before any of
    /// its names is big enough to carry one.
    /// </summary>
    private void OnFolderLoadedForIcons(NestedFolder folder) => _viewModel.Icons.Prefetch(folder);

    /// <summary>
    /// The folder the view is in is let be listed, though known only by the
    /// way to a deeper one: if the user's hand brought the camera there, the
    /// folder in view - the one nearest the middle of the view - is noted
    /// with its place, for <see cref="OnFolderLoadedKeepInView"/>.  Asked
    /// while the canvas draws, so only the camera and the places are read.
    /// </summary>
    private void NoteSparseInView(NestedFolder sparse)
    {
        _sparseInView = null;
        if (!_movedByHand || Canvas.CaptureCamera() is not { Width: > 0 } camera)
        {
            return;
        }

        // The middle of the view, in the folder's own frame.
        var x = -camera.X / camera.Width;
        var y = -camera.Y / camera.Width;
        NestedFolder? nearest = null;
        var best = double.MaxValue;
        foreach (var child in sparse.Children)
        {
            var dx = Math.Max(0, Math.Max(child.OffsetX - x, x - child.OffsetX - child.Scale));
            var dy = Math.Max(0, Math.Max(child.OffsetY - y, y - child.OffsetY - child.Scale * NestedLayout.CellHeight));
            var distance = dx * dx + dy * dy;
            if (distance < best)
            {
                best = distance;
                nearest = child;
            }
        }

        if (nearest is not null)
        {
            _sparseInView = (sparse, nearest, nearest.OffsetX, nearest.OffsetY, nearest.Scale);
        }
    }

    /// <summary>
    /// The folder the view is in was listed where the user's hand left the
    /// camera (<see cref="NoteSparseInView"/>).  It stays put on screen, as
    /// the folder holding the view always does, and the folder in view went
    /// from filling it to one cell among its sisters, smaller and elsewhere:
    /// a jump the moment the camera came to rest.  The camera follows the
    /// folder in view instead, so it stays where it was, as it was, and its
    /// sisters come in around it.  Not after a flight, which went to the
    /// folder itself, nor while the camera moves on.  Told as the listing is
    /// taken in, before the frame that shows it.
    /// </summary>
    private void OnFolderLoadedKeepInView(NestedFolder folder)
    {
        if (_sparseInView is not { } noted || !ReferenceEquals(noted.Sparse, folder))
        {
            return;
        }

        _sparseInView = null;
        if (!_movedByHand || Canvas.IsCameraMoving || !ReferenceEquals(Canvas.Anchor, folder)
            || !ReferenceEquals(noted.Child.Parent, folder) || noted.Child.Index < 0
            || Canvas.ScreenRectOf(folder) is not { Width: > 0 } cell
            || Canvas.ScreenRectOf(noted.Child) is not { Width: > 0 } now)
        {
            return;
        }

        var was = new Rect(cell.X + noted.X * cell.Width, cell.Y + noted.Y * cell.Width,
            noted.Scale * cell.Width, noted.Scale * cell.Width * NestedLayout.CellHeight);
        // Nothing moves on screen: a file dialog still takes the folder the
        // user's hand brought the view to, as it would have without this.
        var movedByUser = _movedByUser;
        var factor = was.Width / now.Width;
        if (Math.Abs(factor - 1) > 1e-6)
        {
            // Zoomed about the one point that takes it from where it is now
            // to where it was.
            Canvas.ZoomAt(new Point((was.X - factor * now.X) / (1 - factor), (was.Y - factor * now.Y) / (1 - factor)), factor);
        }
        else if (Math.Abs(was.X - now.X) > 0.01 || Math.Abs(was.Y - now.Y) > 0.01)
        {
            Canvas.Pan(new Vector(was.X - now.X, was.Y - now.Y));
        }

        _movedByUser = movedByUser;
    }

    // ---- the order ---------------------------------------------------------------

    /// <summary>
    /// A header over the canvas: sort the current folder (<see cref="SortFolder"/>)
    /// by its column, or turn its order round if it already is - or every
    /// folder, when folders are all sorted the same or there is no folder.
    /// </summary>
    private void OnSortHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<SortColumn>(tag, out var column))
        {
            var folder = SortFolder();
            var orders = _viewModel.Orders;
            orders.Choose(folder, orders.SortOf(folder).Click(column));
        }
    }

    /// <summary>
    /// The folder the headers show and change the order of, and Canvas
    /// options' Sort by with them.  With several things selected, the one
    /// with the focus decides.  Null for none - This PC in view with nothing
    /// selected - where a header orders every folder.
    ///
    /// On the nested canvas: the folder being worked with - the folder
    /// selected, or the one the selected file is in - else the folder in
    /// view (see <see cref="MainWindow.NestedSortFolder"/>).  A sub-folder
    /// picked is the one sorted, not the folder around it: whoever selects a
    /// folder and clicks a header means that folder's contents.
    ///
    /// On the tree: the folder selected, whose children open out around it,
    /// else the folder the selected files are in, else the active one.
    /// </summary>
    public string? SortFolder()
    {
        // Worked out once per change of the selection: this is asked after
        // every move of the camera.  The pane's own - the shared one or the
        // one it keeps, whose versions are counted apart.
        var selection = Selection;
        if (selection.Version != _sortSelectionVersion || !ReferenceEquals(selection, _sortSelection))
        {
            _sortSelection = selection;
            _sortSelectionVersion = selection.Version;
            _sortSelectionFolder = null;
            if (selection.Count > 0)
            {
                var primary = selection.Focus is { } focus && selection.Contains(focus) ? focus : selection.Paths[0];
                if (selection.TryGetItem(primary, out var item))
                {
                    _sortSelectionFolder = item.IsDirectory ? primary : Path.GetDirectoryName(primary);
                }
            }
        }

        if (IsNested)
        {
            return MainWindow.NestedSortFolder(_sortSelectionFolder, Canvas.FolderInView?.FullPath);
        }

        if (_sortSelectionFolder is { } selected)
        {
            return selected;
        }

        return _viewModel.Tree.ActiveNode is { } node
            ? node.IsDirectory ? node.FullPath : node.Parent?.FullPath
            : null;
    }

    /// <summary>
    /// Lights the header the current folder is ordered by and points its
    /// arrow the way the order runs - up for A to Z, oldest or smallest
    /// first; down for the other way - as Explorer's column headers do.
    /// Called whenever the folder or an order may have changed; does nothing
    /// when neither did.
    /// </summary>
    public void UpdateSortHeaders()
    {
        // The search puts first what is in the folder the active pane's headers are for.
        if (_viewModel.Search.IsOpen && _host.IsActivePane(this))
        {
            _viewModel.Search.NoteFolder(SortFolder());
        }

        var orders = _viewModel.Orders;
        var folder = orders.Scope == SortScope.AllFolders ? null : SortFolder();
        var sort = orders.SortOf(folder);
        if (_headersShown && sort == _headerSort && orders.Scope == _headerScope
            && string.Equals(folder, _headerFolder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _headersShown = true;
        _headerFolder = folder;
        _headerSort = sort;
        _headerScope = orders.Scope;
        var where = folder is null
            ? orders.Scope == SortScope.AllFolders ? string.Empty : " in every folder"
            : $" in {MainWindow.FolderName(folder)}";
        (Button Header, TextBlock Arrow, SortColumn Column)[] headers =
        [
            (View.SortByName, View.SortByNameArrow, SortColumn.Name),
            (View.SortByModified, View.SortByModifiedArrow, SortColumn.Modified),
            (View.SortByType, View.SortByTypeArrow, SortColumn.Type),
            (View.SortBySize, View.SortBySizeArrow, SortColumn.Size)
        ];

        foreach (var (header, arrow, column) in headers)
        {
            var name = ItemSort.Describe(column);
            if (column == sort.Column)
            {
                header.Foreground = (Brush)View.FindResource("TextBrush");
                arrow.Text = sort.Descending ? "" : "";
                arrow.Visibility = Visibility.Visible;
                header.ToolTip = $"Sorted by {name}, {ItemSort.DescribeDirection(column, sort.Descending)}{where} (click to reverse)";
            }
            else
            {
                // Back to the style's muted text, which its hover can light.
                header.ClearValue(Control.ForegroundProperty);
                arrow.Visibility = Visibility.Hidden;
                header.ToolTip = $"Sort by {name}{where} (click again to reverse)";
            }
        }
    }

    // ---- beacons -------------------------------------------------------------

    public void ScheduleBeacons()
    {
        // Hidden, the canvas has no use for marks, and gathering them would
        // read every folder on the way to each one for nothing.
        if (!IsNested || !IsReady)
        {
            return;
        }

        _beaconTimer?.Stop();
        _beaconTimer?.Start();
    }

    /// <summary>
    /// Everything the user has put on a folder, gathered into one list of
    /// beacons: colours and notes from the mark store, folders pinned to Home,
    /// the folder that is selected, what this pane's filter matches, and while
    /// a search is open, what it found.  A mark on a file shows on the folder
    /// it is in.  With the marks layer off, the colours, notes and pins are
    /// left out.
    /// </summary>
    public void RebuildBeacons()
    {
        Canvas.SetFavoriteLinks(_viewModel.QuickAccess.Where(item => !item.OpensInShell)
            .Select(item => new NestedFavoriteLink(item.Path, item.Name,
                TryParse(item.AccentHex, out var colour) ? colour : PinBeaconColour)).ToArray());
        var marks = _viewModel.IsLayerShown(CanvasLayer.Marks);
        var beacons = new Dictionary<string, (NestedBeaconKind Kind, Color Colour, string Label, string Note)>(StringComparer.OrdinalIgnoreCase);

        void Add(string path, NestedBeaconKind kind, Color? colour, string label, string note = "")
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (beacons.TryGetValue(path, out var existing))
            {
                beacons[path] = (
                    existing.Kind | kind,
                    existing.Kind.HasFlag(NestedBeaconKind.Colour) ? existing.Colour : colour ?? existing.Colour,
                    existing.Label,
                    existing.Note.Length > 0 ? existing.Note : note);
                return;
            }

            beacons[path] = (kind, colour ?? NoteBeaconColour, label, note);
        }

        foreach (var (path, mark) in marks ? _viewModel.Marks.Snapshot() : [])
        {
            var kind = NestedBeaconKind.None;
            Color? colour = null;
            if (!string.IsNullOrEmpty(mark.AccentHex) && TryParse(mark.AccentHex, out var parsed))
            {
                kind |= NestedBeaconKind.Colour;
                colour = parsed;
            }

            if (!string.IsNullOrWhiteSpace(mark.Note))
            {
                kind |= NestedBeaconKind.Note;
            }

            if (kind != NestedBeaconKind.None)
            {
                Add(path, kind, colour, LeafName(path), mark.Note);
            }
        }

        foreach (var pinned in _viewModel.QuickAccess.Where(item => marks && item.IsCustom))
        {
            Add(pinned.Path, NestedBeaconKind.Pinned, PinBeaconColour, pinned.Name);
        }

        if (Canvas.IsFiltering)
        {
            foreach (var match in Canvas.FilterMatches.Take(FilterBeaconLimit))
            {
                Add(match, NestedBeaconKind.Filter, SearchBeaconColour, LeafName(match));
            }
        }

        if (_viewModel.Search.IsOpen)
        {
            foreach (var result in _viewModel.Search.Results.Take(SearchBeaconLimit))
            {
                Add(result.FullPath, NestedBeaconKind.Search, SearchBeaconColour, result.Name);
            }
        }

        // The pane's own focus: the window's while it is being worked with.
        if (IsActive)
        {
            if (_viewModel.Tree.ActiveNode is { } active)
            {
                Add(active.FullPath, NestedBeaconKind.Active, ActiveBeaconColour, active.DisplayName);
            }
        }
        else if (KeptSelection.Focus is { } focus)
        {
            Add(focus, NestedBeaconKind.Active, ActiveBeaconColour, LeafName(focus));
        }

        Canvas.SetBeacons([.. beacons.Select(pair => new NestedBeacon(pair.Key, pair.Value.Kind, pair.Value.Colour, pair.Value.Label, pair.Value.Note))]);
    }

    private static string LeafName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static bool TryParse(string hex, out Color colour)
    {
        colour = default;
        if (!NestedCanvas.IsHexColour(hex))
        {
            return false;
        }

        try
        {
            colour = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    // ---- menus -----------------------------------------------------------------

    /// <summary>
    /// Right-click on a folder's title is the folder's own Windows menu.
    /// Right-click in the open space of a big folder is "in" that folder: the
    /// Windows menu of its open space - New, Paste - with the folder's own
    /// settings.  Outside every cell is the canvas's own menu.
    /// </summary>
    private async void OnContextMenuRequested(NestedHit? hit, bool onBackground, Point point)
    {
        var tree = _viewModel.Tree;
        try
        {
            if (hit is not { } target)
            {
                _host.DropPreparedMenu();
                _host.ShowFolderAreaMenu(Canvas, null);
                return;
            }

            if (onBackground && target.Folder.IsInArchive)
            {
                tree.Selection.ReplaceSingle(target.Folder.FullPath, true, 0, SelectionSource.Canvas);
                ShowArchiveMenu([target.Folder.FullPath], isFolderArea: true);
                return;
            }

            if (onBackground)
            {
                // The folder clicked in becomes the selection, as a click on the
                // empty part of an Explorer window makes it the current folder.
                var folder = target.Folder.FullPath;
                tree.Selection.ReplaceSingle(folder, true, 0, SelectionSource.Canvas);
                if (_host.ShowFolderAreaShellMenu(folder, Canvas, point))
                {
                    return;
                }

                var area = await tree.MaterializeAsync(folder);
                if (_detached) return;
                _host.ShowFolderAreaMenu(Canvas, area);
                return;
            }

            // The whole selection when the item is part of it.
            await ShowItemMenuAsync(target, point);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _viewModel.Toast.ShowError(ex.Message);
        }
    }

    /// <summary>
    /// The right button went down on the canvas: the menu its release would
    /// show - the selection's when the item is part of it, the item's alone
    /// otherwise, or the open space of the folder - is built while it is held.
    /// </summary>
    private void OnContextMenuPressed(NestedHit? hit, bool onBackground, Point point)
    {
        if (hit is not { } target)
        {
            return;
        }

        if (onBackground)
        {
            _host.PrepareShellMenu(background: true, [target.Folder.FullPath]);
            return;
        }

        var selection = _viewModel.Tree.Selection;
        _host.PrepareShellMenu(background: false, selection.Contains(target.Path) ? selection.Paths : [target.Path]);
    }

    /// <summary>
    /// A right-click on an item of the canvas: an item outside the selection
    /// becomes the selection, as in Explorer; one inside it keeps the whole
    /// set and takes the focus.  The menu then acts on everything selected -
    /// the Shell's own for items in one folder, built while the button was
    /// down.  Its entries of our own go by path, so nothing waits for the
    /// window's tree to know the item.  An item found gone - the Shell cannot
    /// make its menu and it is not on disk - is let go of instead, and its
    /// folder read again.
    /// </summary>
    private async Task ShowItemMenuAsync(NestedHit target, Point point)
    {
        var selection = _viewModel.Tree.Selection;
        var path = target.Path;
        if (!selection.Contains(path))
        {
            selection.ReplaceSingle(path, !target.IsFile, target.IsFile ? target.File.Length : 0, SelectionSource.Canvas);
        }
        else
        {
            selection.Apply(new SelectionEdit { Focus = path, Source = SelectionSource.Canvas });
        }

        // Inside an archive the Shell has no menu - nothing there is on disk.
        if (selection.Paths.Any(ArchiveService.IsInsideArchive))
        {
            ShowArchiveMenu(selection.Paths, isFolderArea: false);
            return;
        }

        if (_host.ShowContextMenu(selection.Paths, Canvas, point, includeCanvasCommands: true, fallBack: false))
        {
            return;
        }

        if (!Directory.Exists(path) && !File.Exists(path))
        {
            selection.Remove([path], SelectionSource.Command);
            await RefreshStaleAsync(path);
            return;
        }

        _host.ShowSelectionMenu(Canvas);
    }

    /// <summary>
    /// The menu inside an archive, where the Shell has none: open, copy and
    /// extract - out of it, never into it.  For a folder's open space, the
    /// folder (or the whole archive, at its top) is what it acts on.
    /// </summary>
    private void ShowArchiveMenu(IReadOnlyList<string> paths, bool isFolderArea)
    {
        var entries = new List<ShellMenuEntry>();
        var single = paths.Count == 1 ? paths[0] : null;
        var isArchiveItself = single is not null && ArchiveService.IsArchiveFile(single, Tree.ArchivesEnabled);
        var isFile = single is not null && !isArchiveItself && !isFolderArea && !ArchiveService.IsFolderInArchive(single);
        if (isFile)
        {
            entries.Add(new ShellMenuEntry("Open", () => _ = _viewModel.OpenFromArchiveAsync(single!)) { Glyph = "", Shortcut = "Enter" });
            entries.Add(ShellMenuEntry.Separator);
        }

        if (isArchiveItself)
        {
            var stem = ArchiveService.StemOf(single!);
            entries.Add(new ShellMenuEntry("Extract here", () => _ = _viewModel.ExtractArchivesAsync(paths, ownFolder: false)) { Glyph = "" });
            entries.Add(new ShellMenuEntry($"Extract to \"{stem}\\\"", () => _ = _viewModel.ExtractArchivesAsync(paths, ownFolder: true)) { Glyph = "" });
        }
        else
        {
            var what = isFolderArea ? "this folder" : paths.Count == 1 ? "this" : $"{paths.Count} items";
            entries.Add(new ShellMenuEntry($"Extract {what} next to the archive", () => _ = _viewModel.ExtractBesideArchiveAsync(paths)) { Glyph = "" });
        }

        entries.Add(new ShellMenuEntry("Extract to…", () => _ = _viewModel.ExtractToChosenFolderAsync(paths)) { Glyph = "" });
        entries.Add(new ShellMenuEntry("Copy", () => _ = _viewModel.CopyFromArchiveAsync(paths)) { Glyph = "", Shortcut = "Ctrl+C" });
        entries.Add(ShellMenuEntry.Separator);
        entries.Add(new ShellMenuEntry("Show the archive in File Explorer", () =>
        {
            var archive = MainViewModel.ArchiveBeside(paths[0]) is { } folder ? OutermostArchive(paths[0], folder) : null;
            if (archive is not null)
            {
                NativeShellService.ShowInExplorer(archive);
            }
        }) { Glyph = "" });

        var menu = new ContextMenu { PlacementTarget = Canvas };
        MainWindow.AddEntries(menu, entries);
        menu.IsOpen = true;
    }

    /// <summary>The archive on disk directly inside <paramref name="folder"/> that <paramref name="path"/> goes through.</summary>
    private static string? OutermostArchive(string path, string folder)
    {
        var rest = path.Length > folder.Length ? path[(folder.Length + 1)..] : string.Empty;
        var slash = rest.IndexOf('\\');
        return rest.Length == 0 ? null : Path.Combine(folder, slash < 0 ? rest : rest[..slash]);
    }

    /// <summary>The copy-path button on a folder's title or a file's tile: its full path on the clipboard.</summary>
    private void OnCopyPathRequested(string path)
    {
        try
        {
            Clipboard.SetText(path);
            _ = _viewModel.Toast.ShowSuccessAsync($"Copied: {path}");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _viewModel.Toast.ShowError("Another application is holding the clipboard — try again.");
        }
    }

    // ---- drag and drop ---------------------------------------------------------

    /// <summary>A folder or file picked up: the selection if it is part of one, otherwise just it.</summary>
    private void OnDragRequested(string path)
    {
        if (_host.IsPickerMode)
        {
            return;
        }

        var paths = DragPaths(path);

        // Out of an archive, what is dragged is a copy taken out first: a
        // drop anywhere - Explorer, the desktop, a program - gets real files.
        if (paths.Any(ArchiveService.IsInsideArchive))
        {
            if (_viewModel.ExtractForDrag(paths) is not { } extracted)
            {
                return;
            }

            paths = extracted;
        }

        _host.NestedDragPaths = paths;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, paths);
            DragDrop.DoDragDrop(Canvas, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            _host.NestedDragPaths = null;
            _host.ClearDropTargets();
        }

        // Moved somewhere else - into Explorer, onto the desktop - the items
        // are gone from where they were, and nothing else will say so.
        // Explorer often finishes a move after the drop has returned, so the
        // folders are looked at again now and once more a little later.
        _ = RefreshSourcesAsync(paths);
    }

    /// <summary>What a drag of <paramref name="path"/> carries: the whole selection if it is part of it, else the item alone.</summary>
    private string[] DragPaths(string path)
    {
        var selection = Selection;
        return selection.Count > 1 && selection.Contains(path) ? [.. selection.Paths] : [path];
    }

    private async Task RefreshSourcesAsync(IReadOnlyList<string> paths)
    {
        var parents = paths
            .Select(Path.GetDirectoryName)
            .Where(parent => !string.IsNullOrEmpty(parent))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var delay in new[] { 300, 1500 })
        {
            await Task.Delay(delay);
            if (_detached) return;
            foreach (var parent in parents)
            {
                await _viewModel.Tree.RefreshPathAsync(parent!);
            }
        }
    }

    // A drag crosses both panes of a split view - from one to the other is
    // the point of it - so each handler works with the canvas that raised
    // it: the folder under the pointer is found in that canvas's own
    // coordinates and camera, and lit there, never in the other pane.

    private void OnCanvasDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var canvas = (NestedCanvas)sender;
        if (!_host.TryGetDropPaths(e.Data, out var paths) || ResolveDropTarget(canvas, e, paths) is not { } target)
        {
            canvas.DropTarget = null;
            e.Effects = DragDropEffects.None;
            return;
        }

        // The keys and what the source allows, as the tree canvas has them (see DropEffectFor).
        var effect = DropEffectOver(e, paths, target.FullPath);
        canvas.DropTarget = effect == DragDropEffects.None ? null : target;
        e.Effects = effect;
    }

    /// <summary>
    /// The drag left the canvas - for the other pane, or out of the window -
    /// and its folder is not lit any more.  Whatever the mouse is said to be
    /// over: during a drag nothing tells WPF where the mouse is, so the pane
    /// the drag started on would still count as under it, and stay lit while
    /// the drag hovers over the other.  Nothing inside the canvas raises a
    /// leave of its own: what it draws is visuals, not elements.
    /// </summary>
    private void OnCanvasDragLeave(object sender, DragEventArgs e)
    {
        _host.ForgetDropPaths();
        ((NestedCanvas)sender).DropTarget = null;
    }

    private void OnCanvasDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        var canvas = (NestedCanvas)sender;
        canvas.DropTarget = null;
        try
        {
            // Archive sources can replace their preview paths when extraction
            // finishes on mouse release. Read the final list while Drop is active.
            _host.ForgetDropPaths();
            var carriesPaths = _host.TryGetDropPaths(e.Data, out var paths);
            _host.ForgetDropPaths();
            if (!carriesPaths || ResolveDropTarget(canvas, e, paths) is not { } target) return;

            var effect = MainWindow.DropEffectFor(e, paths, target.FullPath);
            if (effect == DragDropEffects.None) return;

            if (_host.CompleteExternalDrop(e.Data, paths, MainWindow.ReportedDropEffect(effect), () => _viewModel.DropIntoPathWithResultAsync(
                    paths, target.FullPath, move: effect == DragDropEffects.Move)))
                e.Effects = MainWindow.ReportedDropEffect(effect);
        }
        catch (Exception error)
        {
            _host.ForgetDropPaths();
            _viewModel.Toast.ShowError(error.Message);
        }
    }

    /// <summary>
    /// The innermost folder of <paramref name="canvas"/> under the pointer -
    /// a folder's open space is the folder - unless it is one of the things
    /// being dropped or inside one, or the folder a drag from this window
    /// carries them out of.
    /// </summary>
    private NestedFolder? ResolveDropTarget(NestedCanvas canvas, DragEventArgs e, IReadOnlyList<string> paths)
    {
        if (canvas.HitTest(e.GetPosition(canvas)) is not { } hit || hit.Folder.IsComputer)
        {
            return null;
        }

        // Nothing goes into an archive: it is read-only here.
        var target = hit.Folder;
        if (target.IsInArchive)
        {
            return null;
        }

        return _host.IsDropRefused(target.FullPath, () =>
            _dropCarried.GetValue(paths, DropCarried.Gather).Refuses(target.FullPath, fromThisWindow: _host.NestedDragPaths is not null) is { } refused
                ? refused
                : paths.Any(path =>
                    ViewAllPath.Equals(path, target.FullPath)
                    || NativeShellService.IsInvalidMoveTarget(path, target.FullPath)
                    || ViewAllPath.Equals(Path.GetDirectoryName(path) ?? string.Empty, target.FullPath) && _host.NestedDragPaths is not null))
            ? null
            : target;
    }

    /// <summary>What each drag's items are, by name (<see cref="DropCarried"/>): gathered once for the list the window keeps for the drag, and let go with it.</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<string>, DropCarried> _dropCarried = new();

    /// <summary>
    /// What a drag carries, by name, for <see cref="ResolveDropTarget"/>: the
    /// items, the folders they are in, and the items as folders a target may
    /// be inside, each in the full form the item-by-item comparison works out.
    /// That worked every one out again for each folder the pointer came to -
    /// a tenth of a second and more for fifty thousand photos, a stall at
    /// every folder; gathered once for the drag, a folder is answered by a
    /// few lookups, the same answer.  A drag with an item whose full form
    /// cannot be worked out, or a folder whose own cannot, is compared item
    /// by item, as ever.  The drop effect last worked out for it is kept here
    /// too (<see cref="DropEffectOver"/>).
    /// </summary>
    private sealed class DropCarried
    {
        private readonly HashSet<string> _items = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _parents = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Each item as a folder something may be inside, ending in a separator, as NativeShellService.IsInvalidMoveTarget has it: the item, by it.</summary>
        private readonly Dictionary<string, string> _containers = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Any item that comes to the same folder as one before it, named another way.</summary>
        private List<KeyValuePair<string, string>>? _moreContainers;

        private bool _usable = true;

        /// <summary>The drop effect last worked out for the drag, over a folder with the keys held and the effects allowed then.</summary>
        public (string Folder, DragDropKeyStates Keys, DragDropEffects Allowed, DragDropEffects Effect)? LastEffect { get; set; }

        public static DropCarried Gather(IReadOnlyList<string> paths)
        {
            var carried = new DropCarried();
            var parents = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var path in paths)
                {
                    carried._items.Add(ViewAllPath.Normalize(path));

                    // An item with no folder of its own - a drive - is
                    // compared by an empty name, which no folder has.
                    if (Path.GetDirectoryName(path) is { Length: > 0 } parent && parents.Add(parent))
                    {
                        carried._parents.Add(ViewAllPath.Normalize(parent));
                    }

                    if (path.Length > 0)
                    {
                        var container = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        if (!carried._containers.TryAdd(container, path))
                        {
                            (carried._moreContainers ??= []).Add(new(container, path));
                        }
                    }
                }
            }
            catch
            {
                // Whatever working out a full form throws, the item-by-item
                // comparison meets it as it always has.
                carried._usable = false;
            }

            return carried;
        }

        /// <summary>
        /// Whether a drop into <paramref name="folder"/> is refused: it is one
        /// of the items, or inside one that is a folder, or - for a drag from
        /// this window - the folder an item is in.  Null where the items are
        /// to be compared one by one instead.
        /// </summary>
        public bool? Refuses(string folder, bool fromThisWindow)
        {
            if (!_usable || folder.Length == 0)
            {
                return null;
            }

            string normal;
            string inside;
            try
            {
                normal = ViewAllPath.Normalize(folder);
                inside = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            }
            catch
            {
                return null;
            }

            if (_items.Contains(normal) || fromThisWindow && _parents.Contains(normal))
            {
                return true;
            }

            // Inside an item if the item is the folder or one of the folders
            // it is in - and that item is a folder on disk.
            for (var end = inside.IndexOf(Path.DirectorySeparatorChar); end >= 0; end = inside.IndexOf(Path.DirectorySeparatorChar, end + 1))
            {
                var container = inside[..(end + 1)];
                if (_containers.TryGetValue(container, out var item)
                    && (Directory.Exists(item)
                        || _moreContainers?.Any(more => string.Equals(more.Key, container, StringComparison.OrdinalIgnoreCase) && Directory.Exists(more.Value)) == true))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// The drop effect over <paramref name="folder"/>, as
    /// <see cref="MainWindow.DropEffectFor"/> works it out, kept until the
    /// pointer comes to another folder or the keys or the effects allowed
    /// change: it asks whether every item is on the folder's drive -
    /// milliseconds for fifty thousand - and DragOver comes many times a
    /// second while the pointer stays.
    /// </summary>
    private DragDropEffects DropEffectOver(DragEventArgs e, IReadOnlyList<string> paths, string folder)
    {
        var carried = _dropCarried.GetValue(paths, DropCarried.Gather);
        var keys = e.KeyStates & (DragDropKeyStates.ShiftKey | DragDropKeyStates.ControlKey | DragDropKeyStates.AltKey);
        if (carried.LastEffect is { } last && last.Keys == keys && last.Allowed == e.AllowedEffects
            && string.Equals(last.Folder, folder, StringComparison.Ordinal))
        {
            return last.Effect;
        }

        var effect = MainWindow.DropEffectFor(e, paths, folder);
        carried.LastEffect = (folder, keys, e.AllowedEffects, effect);
        return effect;
    }

    // ---- the name filter ---------------------------------------------------------

    /// <summary>Typing narrows the canvas a moment after the last key, not on every one.</summary>
    private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        View.CanvasFilterHint.Visibility = View.CanvasFilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _filterTimer?.Stop();
        _filterTimer?.Start();
    }

    /// <summary>Enter goes to the next match, Shift+Enter the previous, Escape clears and goes back to the canvas.</summary>
    private void OnFilterPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                ApplyFilterNow();
                Canvas.GoToMatch((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
                e.Handled = true;
                break;
            case Key.Down:
                ApplyFilterNow();
                Canvas.GoToMatch(1);
                e.Handled = true;
                break;
            case Key.Up:
                ApplyFilterNow();
                Canvas.GoToMatch(-1);
                e.Handled = true;
                break;
            case Key.Escape:
                ClearFilter();
                e.Handled = true;
                break;
        }
    }

    private void ApplyFilterNow()
    {
        if (_filterTimer is { IsEnabled: true })
        {
            _filterTimer.Stop();
            Canvas.SetFilter(View.CanvasFilterBox.Text);
        }
    }

    private void ClearFilter()
    {
        View.CanvasFilterBox.Text = string.Empty;
        _filterTimer?.Stop();
        Canvas.SetFilter(null);
        _host.FocusCanvas(this);
    }

    /// <summary>The keyboard to the filter box, its text selected to be typed over: Ctrl+Shift+F.</summary>
    public void FocusFilter()
    {
        View.CanvasFilterBox.Focus();
        View.CanvasFilterBox.SelectAll();
    }

    /// <summary>
    /// The count beside the strip, and the matches as beacons: a match deep
    /// in the tree is a speck, and a speck has to be findable like any mark.
    /// </summary>
    private void OnFilterChanged()
    {
        var matches = Canvas.FilterMatches;
        var active = Canvas.IsFiltering;
        View.CanvasFilterCount.Text = !active
            ? string.Empty
            : matches.Count == 0
                ? "No matches among the folders read so far"
                : Canvas.FilterCursor >= 0
                    ? $"{Canvas.FilterCursor + 1:N0} of {matches.Count:N0}"
                    : matches.Count == 1 ? "1 match" : $"{matches.Count:N0} matches";
        var navigation = active && matches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        View.CanvasFilterPrevious.Visibility = navigation;
        View.CanvasFilterNext.Visibility = navigation;
        View.CanvasFilterClear.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

        // While folders are read the matches change frame after frame, each
        // frame saying so: the beacons are gathered as the timer comes round,
        // not put off again by every frame for as long as the reading goes on.
        if (_beaconTimer is not { IsEnabled: true })
        {
            ScheduleBeacons();
        }
    }
}
