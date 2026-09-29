using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
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

    /// <summary>Set while a gesture on this pane's canvas is being applied to the shared selection, so its echo is not loaded back.</summary>
    private bool _applyingCanvasSelection;

    /// <summary>Set while the shared selection is copied into <see cref="KeptSelection"/>, which the canvas already shows.</summary>
    private bool _keepingSelection;

    /// <summary>Set while the shared selection takes what this pane kept (<see cref="Activate"/>), which the canvas already shows.</summary>
    private bool _activating;

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
        Canvas.MarkLookup = _viewModel.Marks.Get;
        Canvas.IconLookup = LookUpFileIcon;
        Canvas.IconArrivals = _iconInbox;
        Tree.FolderLoaded += OnFolderLoadedForIcons;
        Canvas.OpenRequested += OnOpenRequested;
        Canvas.ContextMenuRequested += OnContextMenuRequested;
        Canvas.ContextMenuPressed += OnContextMenuPressed;
        Canvas.DragRequested += OnDragRequested;
        Canvas.CameraChanged += OnCameraChanged;
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
            KeptCamera = Canvas.CaptureCamera() ?? KeptCamera;
            _viewModel.Tree.ScheduleSave();
        };

        // The header names the folder in view as the last picture has it, so
        // it is brought up to date once the camera has come to rest and the
        // picture has been drawn, not on the move.
        _headerTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _headerTimer.Tick += (_, _) =>
        {
            _headerTimer.Stop();
            UpdateHeader();
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
        _saveTimer?.Stop();
        _beaconTimer?.Stop();
        _filterTimer?.Stop();
        _headerTimer?.Stop();
        View.PreviewMouseDown -= OnViewPreviewMouseDown;
        View.IsKeyboardFocusWithinChanged -= OnViewKeyboardFocusWithinChanged;
        KeptSelection.Changed -= OnKeptSelectionChanged;
        _viewModel.Tree.RemoveKeptSelection(KeptSelection);
        Tree.FolderLoaded -= OnFolderLoadedForIcons;
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
        Tree.Orders = _viewModel.Orders;
        Tree.SetRoots(roots);
        Tree.IncludeHidden = _viewModel.Tree.ShowHiddenItems;
        Tree.SetUserHidden(_viewModel.Tree.HiddenPaths);
        IsReady = true;
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
        SyncSelection();
        RebuildBeacons();
        UpdateHeader();
        Dispatcher.InvokeAsync(() =>
        {
            Canvas.UpdateLayout();
            if (!_cameraRestored && RestoredCamera is { } camera)
            {
                // Not awaited: the window is usable while the folders on the
                // way are read, and the view jumps there once they have been.
                _cameraRestored = true;
                _ = Canvas.RestoreCameraAsync(camera);
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

    /// <summary>The camera as it is now, kept for the next session: the window is being closed.</summary>
    public void CaptureCamera()
    {
        if (Canvas.CaptureCamera() is { } camera)
        {
            KeptCamera = camera;
        }
    }

    private void OnCameraChanged()
    {
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
    /// </summary>
    public async Task FlyToAsync(string path, bool gentle, bool animated = true)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var folderPath = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        var folder = await Tree.RevealAsync(folderPath);
        if (folder is null)
        {
            // A share or a WSL distribution the tree has only just added.
            _host.SyncNestedRoots();
            folder = await Tree.RevealAsync(folderPath);
            if (folder is null)
            {
                return;
            }
        }

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
        await FlyToAsync(folder, gentle: false, animated);
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

        Canvas.AcknowledgeSelection(selection.Version);
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

    private void OnViewPreviewMouseDown(object sender, MouseButtonEventArgs e) => _host.ActivatePane(this);

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
        if (hit.IsFile)
        {
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
                Add(match, NestedBeaconKind.Search, SearchBeaconColour, LeafName(match));
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

    // ---- drag and drop ---------------------------------------------------------

    /// <summary>A folder or file picked up: the selection if it is part of one, otherwise just it.</summary>
    private void OnDragRequested(string path)
    {
        if (_host.IsPickerMode)
        {
            return;
        }

        var paths = DragPaths(path);
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
        var effect = MainWindow.DropEffectFor(e, paths, target.FullPath);
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

    private async void OnCanvasDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var canvas = (NestedCanvas)sender;
        canvas.DropTarget = null;
        var carriesPaths = _host.TryGetDropPaths(e.Data, out var paths);
        _host.ForgetDropPaths();
        if (!carriesPaths || ResolveDropTarget(canvas, e, paths) is not { } target)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        var effect = MainWindow.DropEffectFor(e, paths, target.FullPath);
        e.Effects = MainWindow.ReportedDropEffect(effect);
        if (effect == DragDropEffects.None)
        {
            return;
        }

        await _viewModel.DropIntoPathAsync(paths, target.FullPath, move: effect == DragDropEffects.Move);
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

        var target = hit.Folder;
        return _host.IsDropRefused(target.FullPath, () => paths.Any(path =>
            ViewAllPath.Equals(path, target.FullPath)
            || NativeShellService.IsInvalidMoveTarget(path, target.FullPath)
            || ViewAllPath.Equals(Path.GetDirectoryName(path) ?? string.Empty, target.FullPath) && _host.NestedDragPaths is not null))
            ? null
            : target;
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
        ScheduleBeacons();
    }
}
