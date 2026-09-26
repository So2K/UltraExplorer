using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Nodify;
using Nodify.Events;
using Nodify.Interactivity;
using UltraExplorer.Controls;
using UltraExplorer.Dialogs;
using UltraExplorer.Models;
using UltraExplorer.Picker;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace UltraExplorer;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int WmNcLeftButtonUp = 0x00A2;
    private const int WmNcMouseLeave = 0x02A2;
    private const int HtMaxButton = 9;

    private static readonly Brush CaptionHoverBrush = CreateFrozenBrush(0x2D, 0x2D, 0x2D);

    private readonly MainViewModel _viewModel;
    private WindowCaptureService? _capture;
    private bool _allowClose;
    private bool _maximizeHover;
    private bool _sidebarCollapsed;
    private double _restoredSidebarWidth = 240;
    private bool _isSpaceHeld;
    private bool _isSpacePanning;
    private Point _panPointerAnchor;
    private Point _panViewportAnchor;
    private ViewAllNodeViewModel? _overviewDragNode;
    private Point _overviewDragPointerAnchor;
    private Point _overviewDragNodeOrigin;
    private bool _overviewDragMoved;
    private bool _folderListClickWasOnSelection;
    private bool _addressMayComplete;
    private bool _addressCompleting;
    private readonly DispatcherTimer _folderListRenameTimer = new(DispatcherPriority.Input)
    {
        Interval = TimeSpan.FromMilliseconds(NativeShellService.DoubleClickMilliseconds + 60)
    };

    public MainWindow()
        : this(null)
    {
    }

    /// <param name="picker">
    /// Set when another program asked UltraExplorer to pick a file or folder
    /// for it.  The window is the same one either way; only the footer, the
    /// entry rules and what activating an item does differ.
    /// </param>
    public MainWindow(FileDialogSession? picker)
    {
        _viewModel = new MainViewModel(picker is null ? null : FileDialogHost.WorkspacePath);

        InitializeComponent();
        DataContext = _viewModel;

        // A benchmark or snapshot run, or a copy started to try a build, opens
        // on a monitor nobody is using and must not take the keyboard from
        // whatever the user is doing.
        if (IsDiagnosticsRun || IsTestWindow)
        {
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        ConfigureFigmaGestures();

        _viewModel.FitAllRequested += FitAll;
        _viewModel.ZoomRequested += ApplyZoom;
        _viewModel.PromptRequested += ShowInputDialog;
        _viewModel.ConfirmRequested += ShowConfirmDialog;
        _viewModel.ContextMenuRequested += ShowContextMenu;
        _viewModel.Tree.FocusNodeRequested += FocusNode;
        _viewModel.Tree.GraphInvalidated += OnGraphInvalidated;
        _viewModel.Tree.ViewShiftRequested += OnViewShiftRequested;

        ConfigureNodeDrag();
        _folderListRenameTimer.Tick += FolderListRename_Tick;

        _viewModel.Address.EditRequested += FocusAddressBox;
        _viewModel.Address.CompletionOffered += OfferAddressCompletion;
        _viewModel.Address.PropertyChanged += Address_PropertyChanged;

        Overview.Index = _viewModel.Tree.SpatialIndex;
        Harness.Index = _viewModel.Tree.SpatialIndex;
        AttachNested();

        StateChanged += (_, _) =>
        {
            var maximized = WindowState == WindowState.Maximized;
            MaximizeGlyph.Text = maximized ? "\uE923" : "\uE922";
            AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximize");
            SetMaximizeHover(false);
        };

        if (picker is not null)
        {
            AttachPicker(picker);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // ViewportUpdated only fires for zoom and resize, so panning would leave
        // both the culling and the batched overview showing a stale viewport.
        DependencyPropertyDescriptor
            .FromProperty(NodifyEditor.ViewportLocationProperty, typeof(NodifyEditor))
            .AddValueChanged(Editor, OnViewportLocationChanged);

        var handle = new WindowInteropHelper(this).Handle;
        if (IsDiagnosticsRun || IsTestWindow)
        {
            PlaceForDiagnostics(handle, neverActivate: IsDiagnosticsRun);
        }

        var darkMode = 1;
        var cornerPreference = 2;
        _ = DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 33, ref cornerPreference, sizeof(int));
        HwndSource.FromHwnd(handle)?.AddHook(HandleWindowMessage);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // The dialog's entry rules go in before the first read, so the folder
        // the caller asked for is already filtered when it appears.
        await ApplyPickerRulesAsync();
        await _viewModel.InitializeAsync(_pickerStartFolder);
        await InitializeNestedAsync();

        _restoredSidebarWidth = _viewModel.SidebarWidth;
        SidebarColumn.Width = new GridLength(_restoredSidebarWidth);

        await Dispatcher.InvokeAsync(() =>
        {
            PushViewport();
            if (_viewModel.Tree.HasRestoredViewport)
            {
                Editor.ViewportZoom = _viewModel.Tree.RestoredViewportZoom;
                Editor.ViewportLocation = _viewModel.Tree.RestoredViewportLocation;
            }
            else
            {
                // First run: the profile branch is already open, so framing the
                // whole graph is more useful than centring one node.
                FitAll();
            }

            PushViewport();
        }, DispatcherPriority.Loaded);

        _capture = WindowCaptureService.TryCreate(this, Environment.GetCommandLineArgs());
        _capture?.Start();
        if (_picker is null && TryStartNestedDiagnostics())
        {
            return;
        }

        if (PerfLog.IsEnabled)
        {
            // A watchdog on the render loop: whatever stalls the UI thread shows
            // up here as a gap between frames, including the work no stopwatch of
            // ours wraps - WPF's own measure, arrange and container realisation.
            var lastFrame = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += (_, _) =>
            {
                var now = Stopwatch.GetTimestamp();
                var gap = Stopwatch.GetElapsedTime(lastFrame, now).TotalMilliseconds;
                lastFrame = now;
                if (gap > 40)
                {
                    PerfLog.Value("frame.gap", gap);
                    PerfLog.Value("frame.gap.zoom", Editor.ViewportZoom);
                    PerfLog.Value("frame.gap.containers", Editor.Items.Count);
                }
            };
        }

        if (_picker is not null)
        {
            await _viewModel.Tree.RevealPathAsync(_pickerStartFolder);
            PickerNameBox.Focus();
            return;
        }

        FocusCanvas();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        // However the window goes away, the caller gets an answer.
        CompletePickerOnClose();

        if (_allowClose)
        {
            DependencyPropertyDescriptor
                .FromProperty(NodifyEditor.ViewportLocationProperty, typeof(NodifyEditor))
                .RemoveValueChanged(Editor, OnViewportLocationChanged);
            DetachNodeDrag();
            DetachNested();
            _capture?.Dispose();
            _viewModel.Dispose();
            return;
        }

        e.Cancel = true;
        _viewModel.SidebarWidth = SidebarColumn.ActualWidth > 0 ? SidebarColumn.ActualWidth : _restoredSidebarWidth;
        CaptureNestedCamera();
        await _viewModel.SaveNowAsync();
        _allowClose = true;
        Close();
    }

    /// <summary>
    /// Canvas navigation matched to Figma: wheel scrolls, Shift+wheel scrolls
    /// sideways, Ctrl+wheel zooms at the pointer, the middle button drags the
    /// canvas whatever modifier is held, space turns the left button into a
    /// grab hand, and the right button is a context menu rather than a pan.
    /// </summary>
    private static void ConfigureFigmaGestures()
    {
        var editor = EditorGestures.Mappings.Editor;
        editor.ZoomModifierKey = ModifierKeys.Control;
        editor.PanWithMouseWheel = true;
        editor.PanVerticalModifierKey = ModifierKeys.None;
        editor.PanHorizontalModifierKey = ModifierKeys.Shift;

        // A MouseGesture matches one exact modifier combination, so holding
        // Ctrl to zoom used to cancel the middle-button pan. Bind them all.
        ModifierKeys[] everyModifier =
        [
            ModifierKeys.None,
            ModifierKeys.Control,
            ModifierKeys.Shift,
            ModifierKeys.Alt,
            ModifierKeys.Control | ModifierKeys.Shift,
            ModifierKeys.Control | ModifierKeys.Alt,
            ModifierKeys.Shift | ModifierKeys.Alt,
            ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt
        ];

        // Nodify's own MouseGesture is used, not WPF's: it can ignore the modifier
        // state on release, so letting go of Ctrl mid-drag cannot strand the pan.
        var middleDrag = everyModifier
            .Select(modifier => (InputGesture)new Nodify.Interactivity.MouseGesture(
                MouseAction.MiddleClick,
                modifier,
                ignoreModifierKeysOnRelease: true))
            .ToArray();
        editor.Pan.Value = new MultiGesture(MultiGesture.Match.Any, middleDrag);
    }

    private void SetSpacePanArmed(bool armed)
    {
        if (_isSpaceHeld == armed)
        {
            return;
        }

        _isSpaceHeld = armed;
        Editor.Cursor = armed ? Cursors.Hand : null;
        Nested.IsSpacePanArmed = armed;
        Nested.Cursor = armed ? Cursors.Hand : null;
    }

    /// <summary>
    /// The press has to be claimed here, in the tunnel.  By the time the
    /// bubbling MouseLeftButtonDown is raised, Nodify has already begun its own
    /// rubber-band selection, marked the event handled and captured the mouse,
    /// so a handler on the bubbling event is never even called.
    /// </summary>
    private void Editor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isSpaceHeld)
        {
            _isSpacePanning = true;
            _panPointerAnchor = e.GetPosition(Editor);
            _panViewportAnchor = Editor.ViewportLocation;
            Editor.Cursor = Cursors.SizeAll;
            Editor.CaptureMouse();
            e.Handled = true;
            return;
        }

        // Below 30% zoom only the selection still has a container, so there is
        // nothing to grab: the graph is hit-tested directly instead.  The press
        // lands on the Border inside the editor template rather than on the
        // editor, so the canvas is identified by what it is not - a node.
        if (!_viewModel.Tree.IsOverviewActive
            || FindAncestor<ViewAllNodeView>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        Editor.Focus();

        var graphPoint = Editor.GetLocationInsideEditor(e);
        if (_viewModel.Tree.HitTest(graphPoint) is not { } node)
        {
            // Empty canvas: leave it to Nodify, which draws the selection box.
            return;
        }

        _viewModel.Tree.SelectOnly(node);
        _overviewDragNode = node;
        _overviewDragPointerAnchor = graphPoint;
        _overviewDragNodeOrigin = node.Location;
        _overviewDragMoved = false;
        BeginNodeDrag([node]);
        Editor.CaptureMouse();
        e.Handled = true;
    }

    private void Editor_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_overviewDragNode is not null)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndOverviewDrag(committed: _overviewDragMoved);
                return;
            }

            var pointer = Editor.GetLocationInsideEditor(e);
            var dragged = pointer - _overviewDragPointerAnchor;

            // A click is not a drag: without this, clicking a slab would mark
            // the node hand-placed and dirty the saved layout.
            if (!_overviewDragMoved
                && Math.Abs(dragged.X) * Editor.ViewportZoom < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(dragged.Y) * Editor.ViewportZoom < SystemParameters.MinimumVerticalDragDistance)
            {
                e.Handled = true;
                return;
            }

            _overviewDragMoved = true;

            // Setting Location goes through the same path a container drag uses,
            // so the subtree is carried and the batched canvas follows.
            _overviewDragNode.Location = _overviewDragNodeOrigin + dragged;
            UpdateNodeDragHover(pointer);
            e.Handled = true;
            return;
        }

        if (_nodeDragRoots.Length > 0 && Editor.IsDragging)
        {
            // A container drag: Nodify moves the node, this only watches what
            // the pointer is over.  Handling the event would starve that drag.
            UpdateNodeDragHover(Editor.GetLocationInsideEditor(e));
            return;
        }

        if (!_isSpacePanning)
        {
            return;
        }

        var moved = e.GetPosition(Editor) - _panPointerAnchor;
        Editor.ViewportLocation = _panViewportAnchor - moved / Math.Max(Editor.ViewportZoom, 0.001);
        PushViewport();
        e.Handled = true;
    }

    private void Editor_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Cleared before the early return below: left latched, it would make
        // every later move slam the viewport and the canvas would never pan again.
        var wasPanning = _isSpacePanning;
        _isSpacePanning = false;

        if (_overviewDragNode is not null)
        {
            EndOverviewDrag(committed: _overviewDragMoved);
            e.Handled = true;
            return;
        }

        if (!wasPanning)
        {
            return;
        }

        Editor.ReleaseMouseCapture();
        Editor.Cursor = _isSpaceHeld ? Cursors.Hand : null;
        e.Handled = true;
    }

    /// <summary>A drag that loses capture - Alt+Tab, a dialog - must still end.</summary>
    private void Editor_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _isSpacePanning = false;
        EndOverviewDrag(committed: false);
    }

    private void EndOverviewDrag(bool committed)
    {
        if (_overviewDragNode is null)
        {
            return;
        }

        _overviewDragNode = null;
        _overviewDragMoved = false;
        Editor.ReleaseMouseCapture();
        EndNodeDrag(committed);
        _viewModel.Tree.ScheduleSave();
    }

    private void ZoomToSelection()
    {
        if (IsNested)
        {
            if (!string.IsNullOrEmpty(_viewModel.Tree.ActivePath))
            {
                _ = FlyNestedToAsync(_viewModel.Tree.ActivePath, gentle: false);
            }

            return;
        }

        var extent = _viewModel.Tree.GetSelectionExtent();
        if (extent.IsEmpty)
        {
            FitAll();
            return;
        }

        Editor.FitToScreen(extent);
        PushViewport();
    }

    // ---- Canvas wiring -----------------------------------------------------

    private void Editor_ViewportUpdated(object sender, RoutedEventArgs e) => PushViewport();

    private void OnViewportLocationChanged(object? sender, EventArgs e) => PushViewport();

    private void PushViewport()
    {
        using var frame = PerfLog.Measure("viewport");

        using (PerfLog.Measure("viewport.model"))
        {
            _viewModel.Tree.UpdateViewport(Editor.ViewportLocation, Editor.ViewportSize, Editor.ViewportZoom);
        }

        using (PerfLog.Measure("viewport.harness"))
        {
            Harness.Update(Editor.ViewportLocation, Editor.ViewportSize, Editor.ViewportZoom);
        }

        PerfLog.Value("viewport.zoom", Editor.ViewportZoom);

        // Cheap unless the viewport left the window the geometry was built for:
        // normally this just moves an already uploaded visual.
        using var overview = PerfLog.Measure("viewport.overview");
        Overview.Update(
            Editor.ViewportLocation,
            Editor.ViewportSize,
            Editor.ViewportZoom,
            _viewModel.Tree.DetailLevel);
    }

    /// <summary>
    /// Follows the tree when it moves under the cursor.  Opening a folder makes
    /// room for what came out of it, which shifts the folder itself; panning by
    /// the same vector leaves it exactly where the user was looking, and the new
    /// children appear beneath it rather than the whole canvas jumping.
    /// </summary>
    private void OnViewShiftRequested(Vector delta)
    {
        if (Math.Abs(delta.X) < 0.001 && Math.Abs(delta.Y) < 0.001)
        {
            return;
        }

        Editor.ViewportLocation = new Point(
            Editor.ViewportLocation.X + delta.X,
            Editor.ViewportLocation.Y + delta.Y);
        PushViewport();
    }

    /// <summary>
    /// Enter in the folder-list filter opens the best match, which is what makes
    /// the list a way of getting somewhere rather than only of looking.
    /// </summary>
    private void FolderListFilter_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.Tree.FolderList.OpenFirstMatchCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            _viewModel.Tree.FolderList.ClearFilterCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Down arrow moves into the list, so typing and picking is one gesture.
        if (e.Key == Key.Down && Keyboard.Modifiers == ModifierKeys.None && FolderListItems.Items.Count > 0)
        {
            FolderListItems.SelectedIndex = 0;
            (FolderListItems.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
            return;
        }

        // Explorer's own shortcuts, so the muscle memory carries over.  Backspace
        // is deliberately not one of them: it belongs to the text being typed.
        if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.Up)
        {
            _viewModel.Tree.FolderList.UpCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.Left)
        {
            _viewModel.Tree.FolderList.BackCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void FolderListItems_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // The second click of a double-click was a click on the selection too, so
        // the rename that was waiting for it is called off.
        _folderListRenameTimer.Stop();

        if (RowUnder(e) is { } item)
        {
            _viewModel.Tree.FolderList.ActivateCommand.Execute(item);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Remembers whether the row was already the selected one before this click,
    /// because the list itself selects it on the way down and by button-up the
    /// answer is always yes.
    /// </summary>
    private void FolderListItems_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _folderListClickWasOnSelection =
            RowUnder(e) is { } row
            && ReferenceEquals(FolderListItems.SelectedItem, row)
            && FolderListItems.IsKeyboardFocusWithin;

    /// <summary>
    /// A plain click takes the canvas to the row and nothing else: it does not
    /// go into a folder, which is what opening means.  A second click on a row
    /// that was already selected renames it, and a double-click opens it - the
    /// same three gestures Explorer has, told apart the same way, by waiting out
    /// the double-click time before starting a rename.
    ///
    /// This is on button-up rather than on the selection changing, so walking the
    /// list with the arrow keys stays a way of reading it rather than a hundred
    /// flights across the canvas.
    /// </summary>
    private void FolderListItems_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || RowUnder(e) is not { } item)
        {
            return;
        }

        if (_folderListClickWasOnSelection)
        {
            _folderListRenameTimer.Stop();
            _folderListRenameTimer.Start();
            return;
        }

        _viewModel.Tree.FolderList.RevealCommand.Execute(item);
    }

    /// <summary>
    /// Right-click gets the real Windows menu for that file - open, open with,
    /// copy, rename, delete, properties - which is what makes the list a file
    /// manager rather than a picture of one.
    ///
    /// On the button going up, not down: the Shell menu runs its own modal
    /// message loop, and opened on the press it swallows the matching release -
    /// which with TPM_RIGHTBUTTON can count as a click on whatever item the menu
    /// happened to draw under the cursor.  The canvas opens its menus the same
    /// way, and so does Explorer.
    ///
    /// The canvas half of the menu is deliberately left out.  Those entries act
    /// on the canvas selection, and making the row the canvas selection first
    /// would move the viewport, expand a branch, and - for a folder - re-point
    /// this very list into it, all while the menu was opening.
    /// </summary>
    private void FolderListItems_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (RowUnder(e) is not { } item)
        {
            return;
        }

        e.Handled = true;
        _viewModel.Tree.FolderList.Highlight(item);
        ShowContextMenu(
            [item.FullPath],
            FolderListItems,
            e.GetPosition(FolderListItems),
            includeCanvasCommands: false);

        // Rename, delete, a new file from a shell extension - the list cannot
        // know which, so it looks again either way.
        _ = _viewModel.Tree.RefreshFolderListAsync();
    }

    /// <summary>
    /// Right-clicking the empty part of the list gets the folder's own menu -
    /// New, Paste, Properties and whatever the shell extensions add - which is
    /// what Explorer does with the empty part of a folder.
    /// </summary>
    private void FolderListPanel_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (RowUnder(e) is not null)
        {
            // A row has its own menu; that handler runs first and marks it.
            return;
        }

        var folder = _viewModel.Tree.FolderList.FolderPath;
        if (folder.Length == 0 || PresentationSource.FromVisual(this) is not HwndSource source)
        {
            return;
        }

        e.Handled = true;
        var screenPoint = FolderListPanel.PointToScreen(e.GetPosition(FolderListPanel));

        try
        {
            if (NativeShellService.TryShowFolderBackgroundMenu(
                    folder,
                    source,
                    screenPoint,
                    Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
                    null,
                    out _))
            {
                // Whatever it did - a new file, a paste - the list has to look again.
                _ = _viewModel.Tree.RefreshFolderListAsync();
                return;
            }
        }
        catch (Exception exception)
        {
            _viewModel.Toast.ShowError($"Windows context menu failed: {exception.Message}");
        }

        // No Shell menu for this folder: the app's own is better than nothing.
        var menu = new ContextMenu { PlacementTarget = FolderListPanel };
        AddCommandItem(menu, "New folder", "\uE8F4", _viewModel.NewFolderCommand, "Ctrl+Shift+N");
        AddCommandItem(menu, "New text file", "\uE8A5", _viewModel.NewTextFileCommand);
        AddCommandItem(menu, "Paste", "\uE77F", _viewModel.PasteCommand, "Ctrl+V");
        menu.IsOpen = true;
    }

    /// <summary>The folder-list row the mouse is over, or null between rows.</summary>
    private FolderListItem? RowUnder(MouseButtonEventArgs e)
        => ItemsControl.ContainerFromElement(FolderListItems, e.OriginalSource as DependencyObject)
            is ListBoxItem container && container.DataContext is FolderListItem item
            ? item
            : null;

    /// <summary>
    /// Starts the rename only once the double-click time has passed without a
    /// second click, so opening something never opens a rename box first.
    /// </summary>
    private void FolderListRename_Tick(object? sender, EventArgs e)
    {
        _folderListRenameTimer.Stop();
        if (FolderListItems.SelectedItem is FolderListItem)
        {
            _viewModel.RenameCommand.Execute(null);
        }
    }

    private void OnGraphInvalidated()
    {
        Harness.InvalidateGeometry();
        Harness.Update(Editor.ViewportLocation, Editor.ViewportSize, Editor.ViewportZoom);
        Overview.InvalidateGeometry();
        Overview.Update(
            Editor.ViewportLocation,
            Editor.ViewportSize,
            Editor.ViewportZoom,
            _viewModel.Tree.DetailLevel);
    }

    private void FitAll()
    {
        if (IsNested)
        {
            Nested.FitAll();
            return;
        }

        Editor.FitToScreen(_viewModel.Tree.GetContentExtent());
        PushViewport();
    }

    private void ApplyZoom(double factor)
    {
        if (IsNested)
        {
            // "100%" has no meaning where every level has its own size; the
            // reset is the whole of This PC on screen.
            if (factor <= 0)
            {
                Nested.FitAll();
            }
            else
            {
                Nested.ZoomBy(factor > 1 ? 1.5 : 1 / 1.5);
            }

            return;
        }

        if (factor <= 0)
        {
            // Zoom about the viewport centre so resetting does not jump the view.
            var current = Editor.ViewportZoom;
            if (Math.Abs(current - 1) > 0.0005)
            {
                var centre = Editor.ViewportLocation + (Vector)Editor.ViewportSize / 2;
                Editor.ZoomAtPosition(1 / current, centre);
            }
        }
        else if (factor > 1)
        {
            Editor.ZoomIn();
        }
        else
        {
            Editor.ZoomOut();
        }

        PushViewport();
    }

    private void FocusNode(ViewAllNodeViewModel node, bool animated)
    {
        if (IsNested)
        {
            // Decided now, while the list may still be holding its folder: a row
            // picked in the list only needs to be in view, not flown into.
            var gentle = _viewModel.Tree.FolderList.IsHoldingFolder;
            _ = FlyNestedToAsync(node.FullPath, gentle, animated);
            return;
        }

        Dispatcher.InvokeAsync(() =>
        {
            var center = new Point(
                node.Location.X + node.Width / 2,
                node.Location.Y + node.Height / 2);
            Editor.BringIntoView(center, animated);
            PushViewport();
        }, DispatcherPriority.Background);
    }

    private void Minimap_Zoom(object sender, ZoomEventArgs e)
    {
        Editor.ZoomAtPosition(e.Zoom, e.Location);
        PushViewport();
    }
    private void Editor_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ViewAllNodeView>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        e.Handled = true;
        ShowCanvasMenu(e.GetPosition(Editor), Editor.GetLocationInsideEditor(e));
    }

    // ---- Drag and drop -----------------------------------------------------

    private void Editor_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!TryGetDropPaths(e.Data, out var paths))
        {
            _viewModel.Tree.SetDropTarget(null);
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var target = ResolveDropTarget(e, paths);
        _viewModel.Tree.SetDropTarget(target);
        e.Effects = target is null
            ? DragDropEffects.None
            : IsCopyOperation(paths, target.FullPath)
                ? DragDropEffects.Copy
                : DragDropEffects.Move;
        e.Handled = true;
    }

    private void Editor_PreviewDragLeave(object sender, DragEventArgs e)
    {
        if (!Editor.IsMouseOver)
        {
            _viewModel.Tree.SetDropTarget(null);
        }
    }

    private async void Editor_PreviewDrop(object sender, DragEventArgs e)
    {
        if (!TryGetDropPaths(e.Data, out var paths))
        {
            return;
        }

        var target = ResolveDropTarget(e, paths);
        _viewModel.Tree.SetDropTarget(null);
        if (target is null)
        {
            return;
        }

        e.Handled = true;
        await _viewModel.DropIntoPathAsync(paths, target.FullPath, Keyboard.Modifiers);
    }

    /// <summary>
    /// The folder under the cursor wins; otherwise the closest folder within
    /// roughly 96 device-independent pixels, which is what the drop preview
    /// highlights.
    /// </summary>
    private ViewAllNodeViewModel? ResolveDropTarget(DragEventArgs e, IReadOnlyList<string> paths)
    {
        var graphPoint = Editor.GetLocationInsideEditor(e);
        var radius = 96 / Math.Max(Editor.ViewportZoom, 0.05);
        var target = _viewModel.Tree.FindNearestDropTarget(graphPoint, radius);
        if (target is null)
        {
            return null;
        }

        // Dropping something onto itself or into its own subtree is not a move.
        return paths.Any(path =>
            ViewAllPath.Equals(path, target.FullPath)
            || NativeShellService.IsInvalidMoveTarget(path, target.FullPath))
            ? null
            : target;
    }

    private static bool IsCopyOperation(IReadOnlyList<string> paths, string targetDirectory)
        => !MainViewModel.ShouldMove(paths, targetDirectory, Keyboard.Modifiers);

    private static bool TryGetDropPaths(IDataObject data, out string[] paths)
    {
        paths = [];
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] dropped)
        {
            return false;
        }

        paths = dropped;
        return dropped.Length > 0;
    }

    // ---- Context menus -----------------------------------------------------

    private const uint HideFromCanvasCommandId = ShellContextMenu.AppCommandFirst + 1;
    private const uint ReturnToLayoutCommandId = ShellContextMenu.AppCommandFirst + 2;
    private const uint NoteCommandId = ShellContextMenu.AppCommandFirst + 3;
    private const uint PinCommandId = ShellContextMenu.AppCommandFirst + 4;
    private const uint UnpinCommandId = ShellContextMenu.AppCommandFirst + 5;
    private const uint ColourCommandFirst = ShellContextMenu.AppCommandFirst + 0x10;

    /// <summary>
    /// What the app adds to the Shell's menu for a node: the colour palette, and
    /// hiding the branch when the selection is a folder.
    /// </summary>
    private IReadOnlyList<ShellMenuEntry> BuildNodeMenuEntries()
    {
        var colours = new List<ShellMenuEntry>(CanvasColours.Length);
        for (var index = 0; index < CanvasColours.Length; index++)
        {
            colours.Add(new ShellMenuEntry(
                ColourCommandFirst + (uint)index,
                CanvasColours[index].Name));
        }

        var entries = new List<ShellMenuEntry>
        {
            new(0, "Colour", colours)
        };

        // A mark is what makes a folder findable once it is a speck on the
        // nested canvas, so making one is one right-click away.
        if (_viewModel.Tree.ActiveNode is { } active)
        {
            entries.Add(new ShellMenuEntry(NoteCommandId, active.HasNote ? "Edit note…" : "Add note…"));
            if (active.IsDirectory && !IsPickerMode)
            {
                entries.Add(_viewModel.IsPinned(active.FullPath)
                    ? new ShellMenuEntry(UnpinCommandId, "Unpin from Home")
                    : new ShellMenuEntry(PinCommandId, "Pin to Home"));
            }
        }

        if (_viewModel.Tree.SelectedNodes.Any(node => node.IsDirectory))
        {
            entries.Add(new ShellMenuEntry(HideFromCanvasCommandId, "Hide from canvas"));
        }

        // Only worth offering for something that is actually out of the layout.
        if (_viewModel.Tree.HasHandPlacedSelection)
        {
            entries.Add(new ShellMenuEntry(ReturnToLayoutCommandId, "Return to layout"));
        }

        return entries;
    }

    private void InvokeAppCommand(uint command)
    {
        if (command == HideFromCanvasCommandId)
        {
            _viewModel.HideSelectedCommand.Execute(null);
            return;
        }

        if (command == ReturnToLayoutCommandId)
        {
            _viewModel.ReturnToLayoutCommand.Execute(null);
            return;
        }

        if (command == NoteCommandId)
        {
            _viewModel.EditNoteCommand.Execute(null);
            return;
        }

        if (command == PinCommandId)
        {
            _viewModel.AddToFavoritesCommand.Execute(null);
            return;
        }

        if (command == UnpinCommandId)
        {
            if (_viewModel.Tree.ActiveNode is { } node)
            {
                _viewModel.UnpinPath(node.FullPath);
            }

            return;
        }

        var colour = (int)(command - ColourCommandFirst);
        if (colour >= 0 && colour < CanvasColours.Length)
        {
            _viewModel.SetAccentCommand.Execute(CanvasColours[colour].Hex);
        }
    }

    private void ShowContextMenu(IReadOnlyList<string> paths, FrameworkElement origin, Point point)
        => ShowContextMenu(paths, origin, point, includeCanvasCommands: true);

    private void ShowContextMenu(
        IReadOnlyList<string> paths,
        FrameworkElement origin,
        Point point,
        bool includeCanvasCommands)
    {
        if (paths.Count == 0)
        {
            return;
        }

        // The Shell menu is the whole menu for a node, so anything of ours has to
        // live inside it or it would be unreachable.  The canvas entries are left
        // out when the thing clicked has no node on the canvas: hiding or
        // recolouring a branch that is not there would act on the wrong item.
        var appCommands = includeCanvasCommands ? BuildNodeMenuEntries() : [];

        try
        {
            if (PresentationSource.FromVisual(this) is HwndSource source
                && NativeShellService.TryShowNativeContextMenu(
                    paths,
                    source,
                    origin.PointToScreen(point),
                    Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
                    appCommands,
                    out var chosen))
            {
                InvokeAppCommand(chosen);
                return;
            }
        }
        catch (Exception ex)
        {
            _viewModel.Toast.ShowError($"Windows context menu failed: {ex.Message}");
        }

        // Items spread across different folders have no single Shell menu.
        ShowSelectionMenu(origin);
    }

    private void ShowSelectionMenu(FrameworkElement placementTarget)
    {
        var menu = new ContextMenu { PlacementTarget = placementTarget };
        AddCommandItem(menu, "Open", "\uE8E5", _viewModel.OpenCommand);
        AddCommandItem(menu, "Open with\u2026", "\uE7AC", _viewModel.OpenWithCommand);
        menu.Items.Add(new Separator());
        AddCommandItem(menu, "Cut", "\uE8C6", _viewModel.CutCommand, "Ctrl+X");
        AddCommandItem(menu, "Copy", "\uE8C8", _viewModel.CopyCommand, "Ctrl+C");
        AddCommandItem(menu, "Copy as path", "\uE71B", _viewModel.CopyPathCommand, "Ctrl+Shift+C");
        AddCommandItem(menu, "Paste", "\uE77F", _viewModel.PasteCommand, "Ctrl+V");
        menu.Items.Add(new Separator());
        AddCommandItem(menu, "Duplicate", "\uE8C8", _viewModel.DuplicateCommand, "Ctrl+Shift+D");
        AddCommandItem(menu, "Delete", "\uE74D", _viewModel.DeleteCommand, "Del");
        AddCommandItem(menu, "Delete permanently", "\uE74D", _viewModel.PermanentDeleteCommand, "Shift+Del");
        menu.Items.Add(new Separator());
        AddCommandItem(menu, "Hide from canvas", "\uED1A", _viewModel.HideSelectedCommand, "Ctrl+H");
        if (_viewModel.Tree.HasHandPlacedSelection)
        {
            AddCommandItem(menu, "Return to layout", "\uE8AB", _viewModel.ReturnToLayoutCommand);
        }

        AddColourItems(menu);
        AddCommandItem(menu, "Show in File Explorer", "\uEC50", _viewModel.ShowInExplorerCommand);
        menu.IsOpen = true;
    }

    /// <summary>
    /// The way back for anything hidden.  Without a list of what is hidden, a
    /// folder put away months ago is unfindable - the classic failure of this
    /// kind of command.
    /// </summary>
    private void AddHiddenFolderItems(ContextMenu menu)
    {
        var hidden = _viewModel.HiddenPaths;
        if (hidden.Count == 0)
        {
            return;
        }

        var group = new MenuItem { Header = $"Hidden folders ({hidden.Count})" };
        foreach (var path in hidden)
        {
            var item = new MenuItem
            {
                Header = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name
                    ? name
                    : path,
                ToolTip = path
            };

            var restored = path;
            item.Click += (_, _) => _viewModel.Tree.ShowHidden(restored);
            group.Items.Add(item);
        }

        group.Items.Add(new Separator());
        var all = new MenuItem { Header = "Show all hidden" };
        all.Click += (_, _) => _viewModel.Tree.ShowAllHidden();
        group.Items.Add(all);

        menu.Items.Add(group);
    }

    private void ShowCanvasMenu(Point point, Point graphPoint)
    {
        // Every folder owns a rectangle on the canvas, so a right-click inside one
        // is a right-click "in" that folder - which is where a new file belongs.
        // Blocks nest, so this is the innermost one the click landed in.
        ShowFolderAreaMenu(Editor, _viewModel.Tree.FolderAt(graphPoint));
    }

    /// <summary>The menu for the open space of a folder: what can be made or put in it, and the canvas's own commands.</summary>
    private void ShowFolderAreaMenu(FrameworkElement placementTarget, ViewAllNodeViewModel? area)
    {
        // The folder the menu names is the folder its commands act on: New
        // folder, New text file and Paste go to the selection, so the folder
        // clicked in becomes the selection - as a click on empty space in an
        // Explorer window does.
        if (area is { IsDirectory: true })
        {
            _viewModel.Tree.SelectOnly(area);
        }

        var where = _viewModel.Tree.ActiveNode is { IsDirectory: true } folder
            ? folder.DisplayName
            : null;

        var menu = new ContextMenu { PlacementTarget = placementTarget };
        AddCommandItem(
            menu,
            where is null ? "New folder" : $"New folder in {where}",
            "\uE8F4",
            _viewModel.NewFolderCommand,
            "Ctrl+Shift+N");
        AddCommandItem(
            menu,
            where is null ? "New text file" : $"New text file in {where}",
            "\uE8A5",
            _viewModel.NewTextFileCommand);
        AddCommandItem(
            menu,
            where is null ? "Paste" : $"Paste into {where}",
            "\uE77F",
            _viewModel.PasteCommand,
            "Ctrl+V");
        menu.Items.Add(new Separator());
        AddCommandItem(menu, "Fit all", "\uE9A6", _viewModel.FitAllCommand, "Shift+1");
        if (IsNested)
        {
            AddSortItems(menu);
            AddHiddenFolderItems(menu);
            AddCommandItem(menu, "Folder list", "\uE8FD", _viewModel.ToggleFolderListCommand);
            AddLayoutItems(menu);
            menu.IsOpen = true;
            return;
        }

        AddCommandItem(menu, "Reset zoom", "\uE71E", _viewModel.ResetZoomCommand, "Ctrl+0");
        AddCommandItem(menu, "Collapse every branch", "\uE72B", _viewModel.CollapseAllCommand);
        AddCommandItem(menu, "Tidy the layout", "\uE8AB", _viewModel.RelayoutCommand);
        AddSortItems(menu);
        AddHiddenFolderItems(menu);
        AddCommandItem(menu, "Folder list", "\uE8FD", _viewModel.ToggleFolderListCommand);
        AddCommandItem(menu, "Minimap", "\uE81E", _viewModel.ToggleMinimapCommand);
        AddLayoutItems(menu);
        menu.IsOpen = true;
    }

    /// <summary>
    /// Explorer's "Sort by": the four columns, then which way round, each a
    /// checkable item showing the current choice.  Picking another column
    /// starts it its own way - names and types from A, dates and sizes from
    /// the newest and largest - as a click on its header would.
    /// </summary>
    private void AddSortItems(ItemsControl menu)
    {
        var sort = _viewModel.Sort;
        var group = new MenuItem { Header = "Sort by" };
        foreach (var column in Enum.GetValues<SortColumn>())
        {
            var item = new MenuItem
            {
                Header = ItemSort.Describe(column),
                IsCheckable = true,
                IsChecked = sort.Column == column
            };
            var chosen = column;
            item.Click += (_, _) =>
            {
                if (_viewModel.Sort.Column != chosen)
                {
                    _viewModel.Sort = _viewModel.Sort.Click(chosen);
                }
            };
            group.Items.Add(item);
        }

        group.Items.Add(new Separator());
        foreach (var descending in new[] { false, true })
        {
            var item = new MenuItem
            {
                Header = descending ? "Descending" : "Ascending",
                IsCheckable = true,
                IsChecked = sort.Descending == descending
            };
            var way = descending;
            item.Click += (_, _) => _viewModel.Sort = _viewModel.Sort with { Descending = way };
            group.Items.Add(item);
        }

        menu.Items.Add(group);
    }

    /// <summary>The two pictures of the drives, as a pair of radio items.</summary>
    private void AddLayoutItems(ContextMenu menu)
    {
        if (IsPickerMode)
        {
            return;
        }

        menu.Items.Add(new Separator());
        var nested = new MenuItem { Header = "Nested canvas", IsCheckable = true, IsChecked = IsNested, ToolTip = "Every folder inside its parent, the whole disk on one screen" };
        nested.Click += (_, _) => _viewModel.Layout = CanvasLayout.Nested;
        var tree = new MenuItem { Header = "Tree canvas", IsCheckable = true, IsChecked = !IsNested, ToolTip = "Folders opened one at a time as a top-down tree" };
        tree.Click += (_, _) => _viewModel.Layout = CanvasLayout.Tree;
        menu.Items.Add(nested);
        menu.Items.Add(tree);
    }

    private void OrganizeButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        AddCommandItem(menu, "Cut", "\uE8C6", _viewModel.CutCommand, "Ctrl+X");
        AddCommandItem(menu, "Copy", "\uE8C8", _viewModel.CopyCommand, "Ctrl+C");
        AddCommandItem(menu, "Paste", "\uE77F", _viewModel.PasteCommand, "Ctrl+V");
        AddCommandItem(menu, "Copy as path", "\uE71B", _viewModel.CopyPathCommand, "Ctrl+Shift+C");
        menu.Items.Add(new Separator());
        AddCommandItem(menu, "Rename", "\uE8AC", _viewModel.RenameCommand, "F2");
        AddCommandItem(menu, "Duplicate", "\uE8C8", _viewModel.DuplicateCommand, "Ctrl+Shift+D");
        AddCommandItem(menu, "Delete", "\uE74D", _viewModel.DeleteCommand, "Del");
        AddCommandItem(menu, "Delete permanently", "\uE74D", _viewModel.PermanentDeleteCommand, "Shift+Del");
        menu.Items.Add(new Separator());
        AddCommandItem(menu, "New text file", "\uE8A5", _viewModel.NewTextFileCommand);
        AddCommandItem(menu, "Properties", "\uE946", _viewModel.PropertiesCommand, "Alt+Enter");
        menu.IsOpen = true;
    }

    private void GiveAccessButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        AddCommandItem(menu, "Open in File Explorer", "\uEC50", _viewModel.ShowInExplorerCommand);
        AddCommandItem(menu, "Copy as path", "\uE71B", _viewModel.CopyPathCommand, "Ctrl+Shift+C");
        AddCommandItem(menu, "Sharing and security…", "\uE946", _viewModel.PropertiesCommand);
        menu.IsOpen = true;
    }

    private void LayoutButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        AddCommandItem(menu, "Fit all", "\uE9A6", _viewModel.FitAllCommand, "Shift+1");
        if (!IsNested)
        {
            AddCommandItem(menu, "Reset zoom", "\uE71E", _viewModel.ResetZoomCommand, "Ctrl+0");
            AddCommandItem(menu, "Collapse every branch", "\uE72B", _viewModel.CollapseAllCommand);
            AddCommandItem(menu, "Tidy the layout", "\uE8AB", _viewModel.RelayoutCommand);
        }

        AddHiddenFolderItems(menu);
        AddCommandItem(menu, "Folder list", "\uE8FD", _viewModel.ToggleFolderListCommand);
        if (!IsNested)
        {
            AddCommandItem(menu, "Minimap", "\uE81E", _viewModel.ToggleMinimapCommand);
        }

        AddCheckableItem(menu, "Hidden items", _viewModel.Tree.ShowHiddenItems, _viewModel.ToggleHiddenItemsCommand);
        AddSortItems(menu);
        menu.Items.Add(new Separator());
        AddColourItems(menu);
        AddCommandItem(menu, "Note…", "\uE70B", _viewModel.EditNoteCommand);
        AddCommandItem(menu, "Pin to Home", "\uE718", _viewModel.AddToFavoritesCommand);
        AddLayoutItems(menu);
        menu.IsOpen = true;
    }

    private void OverflowButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        AddCommandItem(menu, "Pin to Home", "\uE718", _viewModel.AddToFavoritesCommand);
        AddCommandItem(menu, "Show in File Explorer", "\uEC50", _viewModel.ShowInExplorerCommand);
        AddCommandItem(menu, "Refresh", "\uE72C", _viewModel.RefreshCommand, "F5");
        menu.Items.Add(new Separator());
        AddCommandItem(menu, "Properties", "\uE946", _viewModel.PropertiesCommand, "Alt+Enter");
        menu.IsOpen = true;
    }

    /// <summary>
    /// The palette offered for a folder's colour.  One list, used by the app menu
    /// and by the items added to the Shell's own menu, so the two can never drift.
    /// </summary>
    private static readonly (string Name, string Hex)[] CanvasColours =
    [
        ("Default", ""),
        ("Red", "#EF5A68"),
        ("Orange", "#F28A4B"),
        ("Yellow", "#E3B341"),
        ("Green", "#4ED6A0"),
        ("Cyan", "#4CC9D8"),
        ("Blue", "#60CDFF"),
        ("Violet", "#A979FF")
    ];

    private void AddColourItems(ItemsControl menu)
    {
        var colours = CanvasColours;

        var parent = new MenuItem { Header = "Colour" };
        foreach (var colour in colours)
        {
            var swatch = new Border
            {
                Width = 11,
                Height = 11,
                CornerRadius = new CornerRadius(6),
                Background = string.IsNullOrEmpty(colour.Hex)
                    ? Brushes.Transparent
                    : Infrastructure.BrushCache.Get(colour.Hex),
                BorderBrush = Infrastructure.BrushCache.Get("#585858"),
                BorderThickness = new Thickness(string.IsNullOrEmpty(colour.Hex) ? 1 : 0)
            };
            var item = new MenuItem { Header = colour.Name, Icon = swatch };
            var hex = colour.Hex;
            item.Click += (_, _) => _viewModel.SetAccentCommand.Execute(hex);
            parent.Items.Add(item);
        }

        menu.Items.Add(parent);
    }

    private static void AddCheckableItem(ItemsControl menu, string header, bool isChecked, ICommand command)
    {
        var item = new MenuItem
        {
            Header = header,
            IsCheckable = true,
            IsChecked = isChecked,
            Command = command
        };

        menu.Items.Add(item);
    }

    private static Brush MenuGlyphBrush =>
        Application.Current?.TryFindResource("TextBrush") as Brush ?? Brushes.White;

    private static void AddCommandItem(ItemsControl menu, string header, string glyph, ICommand command, string gesture = "")
    {
        var item = new MenuItem
        {
            Header = header,
            InputGestureText = gesture,
            Command = command,
            Icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 13,

                // A menu lives in a popup, which is its own visual tree: nothing
                // inherits from the window into it, so a colour left unsaid here
                // is the system default - black text on a dark menu.
                Foreground = MenuGlyphBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        menu.Items.Add(item);
    }

    // ---- Shell chrome ------------------------------------------------------

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximized();

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e) => Close();

    private void NewTabButton_Click(object sender, RoutedEventArgs e) => FitAll();

    private void PaneToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sidebarCollapsed)
        {
            SidebarColumn.MinWidth = 190;
            SidebarColumn.Width = new GridLength(_restoredSidebarWidth);
        }
        else
        {
            _restoredSidebarWidth = SidebarColumn.ActualWidth > 0 ? SidebarColumn.ActualWidth : _restoredSidebarWidth;
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
        }

        _sidebarCollapsed = !_sidebarCollapsed;
    }

    private void ToggleMaximized()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>
    /// Clicking the bar anywhere that is not a crumb or a button turns it into a
    /// line to type a path in - the empty strip beside the crumbs included,
    /// which is where the pointer usually is.  On the preview so that nothing
    /// inside the bar can quietly swallow the click on its way up.
    /// </summary>
    private void AddressArea_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Already a line: this click is someone putting the caret where they
        // want it, not asking for the line they are already typing in.
        if (_viewModel.Address.IsEditing)
        {
            return;
        }

        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        BeginAddressEdit();
    }

    /// <summary>
    /// Right-clicking the bar offers what can be done with the path itself,
    /// which is mostly getting it into and out of the clipboard.
    /// </summary>
    private void AddressArea_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        var address = _viewModel.Address;
        var menu = new ContextMenu { PlacementTarget = AddressArea };
        AddCommandItem(menu, "Edit address", "\uE70F", address.EditCommand, "Ctrl+L");
        AddCommandItem(menu, "Copy address", "\uE8C8", address.CopyCommand);
        AddCommandItem(menu, "Copy address as a quoted path", "\uE71B", address.CopyQuotedCommand);
        AddCommandItem(menu, "Paste and go", "\uE77F", address.PasteAndGoCommand);
        menu.Items.Add(new Separator());
        AddCommandItem(menu, "Open in Windows Explorer", "\uEC50", address.OpenInExplorerCommand);
        menu.IsOpen = true;
    }

    private void BeginAddressEdit() => _viewModel.Address.BeginEdit();

    /// <summary>
    /// The line has been opened: it needs the keyboard, and everything in it
    /// selected so that typing a fresh path replaces the old one.
    /// </summary>
    private void FocusAddressBox()
        => Dispatcher.InvokeAsync(
            () =>
            {
                AddressBox.Focus();
                AddressBox.SelectAll();
            },

            // Behind the pending input, not in front of it: the click that asked
            // for the line is still on its way through, and the text box would
            // answer it by putting the caret where the pointer is and dropping
            // the selection - so the next thing typed would land in the middle
            // of the old path instead of replacing it.
            DispatcherPriority.Background);

    /// <summary>
    /// Whatever ended the editing - Enter, Escape, a suggestion - the keyboard
    /// must land somewhere, and the canvas is where it came from.  Only when the
    /// line still holds it: focus that moved elsewhere on its own stays there.
    /// </summary>
    private void Address_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AddressBarViewModel.IsEditing)
            && !_viewModel.Address.IsEditing
            && AddressBox.IsKeyboardFocusWithin)
        {
            FocusCanvas();
        }
    }

    /// <summary>
    /// Enter goes, Escape backs out one step at a time, the arrows walk the
    /// drop-down and Tab takes the offered folder and asks for its contents -
    /// one key per level, no mouse.  This is on the preview because Tab and the
    /// arrow keys never reach a text box otherwise.
    /// </summary>
    private void AddressBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var address = _viewModel.Address;
        switch (e.Key)
        {
            case Key.Enter:
                address.GoCommand.Execute(null);
                break;

            case Key.Escape:
                // The drop-down first, the line second: one Escape should not
                // throw away a path that was only obscured by a list.
                if (address.IsDropDownOpen)
                {
                    address.IsDropDownOpen = false;
                }
                else
                {
                    address.EndEdit();
                }

                break;

            case Key.Down:
                address.MoveHighlight(1);
                AddressBox.CaretIndex = AddressBox.Text.Length;
                break;

            case Key.Up:
                address.MoveHighlight(-1);
                AddressBox.CaretIndex = AddressBox.Text.Length;
                break;

            case Key.Tab:
                address.Complete();
                AddressBox.CaretIndex = AddressBox.Text.Length;
                break;

            case Key.F4:
                address.ShowRecentCommand.Execute(null);
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Remembers whether the caret is sitting at the end of something just
    /// typed, which is the only place a completion may be offered: appending to
    /// the middle of a path, or to something being deleted, would fight the
    /// person typing.
    /// </summary>
    private void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_addressCompleting)
        {
            return;
        }

        _addressMayComplete =
            e.Changes.Any(change => change.AddedLength > 0 && change.RemovedLength == 0)
            && AddressBox.CaretIndex == AddressBox.Text.Length;
    }

    /// <summary>
    /// Finishes the typed path in place with the rest of the best match, the
    /// added part selected: carrying on typing throws it away, Right or End
    /// keeps it.
    /// </summary>
    private void OfferAddressCompletion(string completion)
    {
        var typed = AddressBox.Text;
        if (!_addressMayComplete
            || !_viewModel.Address.IsEditing
            || typed.Length == 0
            || AddressBox.SelectionLength > 0
            || AddressBox.CaretIndex != typed.Length
            || completion.Length <= typed.Length
            || !completion.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _addressCompleting = true;
        try
        {
            // What was typed is left exactly as typed, even where the folder on
            // disk is spelled with other capitals; only the tail is ours.
            AddressBox.Text = typed + completion[typed.Length..];
            AddressBox.Select(typed.Length, completion.Length - typed.Length);
        }
        finally
        {
            _addressCompleting = false;

            // The completed line asks for its own contents, and that answer must
            // not complete again - otherwise one keystroke walks the whole tree.
            _addressMayComplete = false;
        }
    }

    /// <summary>
    /// A click on a suggestion - under the line or behind a crumb's chevron -
    /// goes there.  Button-up rather than selection, so walking the list with
    /// the arrow keys stays reading rather than travelling.
    /// </summary>
    private void AddressSuggestions_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox list
            && ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is ListBoxItem container
            && container.DataContext is AddressSuggestion suggestion)
        {
            e.Handled = true;
            _viewModel.Address.AcceptCommand.Execute(suggestion);
        }
    }

    private void AddressBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Focus that lands nowhere at all is not somebody clicking elsewhere: it
        // is the keyboard leaving the window - a menu opening, a tooltip, the
        // window losing the foreground - and the line should still be there when
        // it comes back.  Clicking something else in the window names what was
        // clicked, and that does finish the line; switching away from the window
        // finishes it too, from Window_Deactivated.
        if (e.NewFocus is null)
        {
            return;
        }

        _viewModel.Address.EndEdit();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.SearchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel.CloseSearchCommand.Execute(null);
            FocusCanvas();
            e.Handled = true;
        }
    }

    private void SearchResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as ListBox)?.SelectedItem is SearchResultViewModel result)
        {
            _viewModel.OpenSearchResultCommand.Execute(result);
        }
    }

    // ---- Keyboard ----------------------------------------------------------

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;

        // WPF reports F10 and every Alt combination as Key.System with the real
        // key in SystemKey, so reading e.Key alone silently drops Alt+Left,
        // Alt+Up, Alt+Enter and Shift+F10.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ctrl+L and Alt+D are both "put the path in a line I can type in";
        // Windows has answered to either for twenty years.
        if ((modifiers == ModifierKeys.Control && key == Key.L)
            || (modifiers == ModifierKeys.Alt && key == Key.D))
        {
            BeginAddressEdit();
            e.Handled = true;
            return;
        }

        if (modifiers == ModifierKeys.None && key == Key.F4)
        {
            _viewModel.Address.ShowRecentCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.F && IsNested)
        {
            FocusCanvasFilter();
            e.Handled = true;
            return;
        }

        if (modifiers == ModifierKeys.Control && key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        // The nested canvas moves between cells with the arrows and goes in and
        // out with Enter and Backspace; the tree's meanings for those keys
        // (open, expand, collapse) do not exist there.
        if (TryHandleNestedKey(key, modifiers))
        {
            e.Handled = true;
            return;
        }

        switch (modifiers, key)
        {
            case (ModifierKeys.Control, Key.H):
                _viewModel.HideSelectedCommand.Execute(null);
                break;

            case (ModifierKeys.Control | ModifierKeys.Shift, Key.H):
                _viewModel.ShowAllHiddenCommand.Execute(null);
                break;

            case (ModifierKeys.Control, Key.C):
                _viewModel.CopyCommand.Execute(null);
                break;
            case (ModifierKeys.Control, Key.X):
                _viewModel.CutCommand.Execute(null);
                break;
            case (ModifierKeys.Control, Key.V):
                _viewModel.PasteCommand.Execute(null);
                break;
            case (ModifierKeys.Control | ModifierKeys.Shift, Key.C):
                _viewModel.CopyPathCommand.Execute(null);
                break;
            case (ModifierKeys.Control | ModifierKeys.Shift, Key.N):
                _viewModel.NewFolderCommand.Execute(null);
                break;
            case (ModifierKeys.Control | ModifierKeys.Shift, Key.D):
                _viewModel.DuplicateCommand.Execute(null);
                break;
            case (ModifierKeys.Alt, Key.Left):
                _viewModel.BackCommand.Execute(null);
                break;
            case (ModifierKeys.Alt, Key.Right):
                _viewModel.ForwardCommand.Execute(null);
                break;
            case (ModifierKeys.Alt, Key.Up):
                _viewModel.UpCommand.Execute(null);
                break;
            case (ModifierKeys.Alt, Key.Enter):
                _viewModel.PropertiesCommand.Execute(null);
                break;
            // Figma's viewport shortcuts, plus Ctrl+0 as a familiar alias.
            case (ModifierKeys.Shift, Key.D0):
            case (ModifierKeys.Shift, Key.NumPad0):
            case (ModifierKeys.Control, Key.D0):
            case (ModifierKeys.Control, Key.NumPad0):
                _viewModel.ResetZoomCommand.Execute(null);
                break;
            case (ModifierKeys.Shift, Key.D1):
            case (ModifierKeys.Shift, Key.NumPad1):
                _viewModel.FitAllCommand.Execute(null);
                break;
            case (ModifierKeys.Shift, Key.D2):
            case (ModifierKeys.Shift, Key.NumPad2):
                ZoomToSelection();
                break;
            case (ModifierKeys.Control, Key.OemPlus):
            case (ModifierKeys.Control, Key.Add):
                _viewModel.ZoomInCommand.Execute(null);
                break;
            case (ModifierKeys.Control, Key.OemMinus):
            case (ModifierKeys.Control, Key.Subtract):
                _viewModel.ZoomOutCommand.Execute(null);
                break;
            case (ModifierKeys.None, Key.Space):
                SetSpacePanArmed(true);
                break;
            case (ModifierKeys.Shift, Key.F10):
                ShowContextMenuForSelection();
                break;
            case (ModifierKeys.None, Key.F2):
                _viewModel.RenameCommand.Execute(null);
                break;
            case (ModifierKeys.None, Key.F5):
                _viewModel.RefreshCommand.Execute(null);
                break;
            case (ModifierKeys.None, Key.Delete):
                _viewModel.DeleteCommand.Execute(null);
                break;
            case (ModifierKeys.Shift, Key.Delete):
                _viewModel.PermanentDeleteCommand.Execute(null);
                break;
            case (ModifierKeys.None, Key.Enter):
                _viewModel.OpenCommand.Execute(null);
                break;
            case (ModifierKeys.None, Key.Right) when _viewModel.Tree.ActiveNode is { IsDirectory: true, IsExpanded: false } expand:
                await _viewModel.Tree.ToggleAsync(expand);
                break;
            case (ModifierKeys.None, Key.Left) when _viewModel.Tree.ActiveNode is { IsDirectory: true, IsExpanded: true } collapse:
                await _viewModel.Tree.ToggleAsync(collapse);
                break;
            case (ModifierKeys.None, Key.Escape):
                // A crumb's list of folders is the nearest thing to a menu the
                // bar has, and Escape closes menus.
                foreach (var crumb in _viewModel.Address.Breadcrumbs)
                {
                    crumb.IsMenuOpen = false;
                }

                _viewModel.CloseSearchCommand.Execute(null);
                return;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Window_Activated(object? sender, EventArgs e)
    {
        // Coming back from another window can leave focus nowhere.
        if (Keyboard.FocusedElement is null)
        {
            FocusCanvas();
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        // Alt+Tab while space is held would otherwise leave the grab hand on.
        _isSpacePanning = false;
        SetSpacePanArmed(false);

        // Going to another program puts the crumbs back, as Explorer does.
        _viewModel.Address.EndEdit();
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            SetSpacePanArmed(false);
        }
    }

    private void ShowContextMenuForSelection()
    {
        var paths = _viewModel.Tree.SelectedPaths;
        if (paths.Count == 0)
        {
            return;
        }

        if (IsNested)
        {
            var at = new Point(Nested.ActualWidth / 2, Nested.ActualHeight / 2);
            if (_nestedTree.Find(_viewModel.Tree.ActivePath) is { } folder && Nested.ScreenRectOf(folder) is { } rect)
            {
                var visible = Rect.Intersect(rect, new Rect(0, 0, Nested.ActualWidth, Nested.ActualHeight));
                if (!visible.IsEmpty)
                {
                    at = new Point(visible.X + visible.Width / 2, visible.Y + Math.Min(visible.Height / 2, 24));
                }
            }

            ShowContextMenu(paths, Nested, at);
            return;
        }

        var anchor = _viewModel.Tree.ActiveNode is { } node
            && Editor.ItemContainerGenerator.ContainerFromItem(node) is FrameworkElement container
                ? container
                : Editor;
        ShowContextMenu(paths, anchor, new Point(anchor.ActualWidth / 2, anchor.ActualHeight / 2));
    }

    // ---- Dialogs -----------------------------------------------------------

    private string? ShowInputDialog(string title, string prompt, string initialValue)
    {
        var dialog = new InputDialog(title, prompt, initialValue) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private bool ShowConfirmDialog(string title, string message)
    {
        var dialog = new ConfirmDialog(title, message) { Owner = this };
        return dialog.ShowDialog() == true;
    }

    // ---- Win32 -------------------------------------------------------------

    /// <summary>
    /// Reports the maximize button as HTMAXBUTTON so Windows 11 shows its Snap
    /// Layouts flyout, and turns the resulting non-client clicks back into a
    /// normal maximize toggle.
    /// </summary>
    private IntPtr HandleWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            // Left to the rest of the chain: WPF applies MinWidth/MinHeight to
            // the same structure afterwards and re-stores it whole, so what is
            // written here survives.
            case WmGetMinMaxInfo:
                ClampMaximizedBounds(hwnd, lParam);
                return IntPtr.Zero;

            case WmNcHitTest:
                if (IsOverMaximizeButton(lParam))
                {
                    SetMaximizeHover(true);
                    handled = true;
                    return new IntPtr(HtMaxButton);
                }

                SetMaximizeHover(false);

                // Maximized, the top edge of the window is the top edge of the
                // screen - the easiest place there is to throw the pointer at.
                // WindowChrome still called that band a resize border, and a
                // resize border on a maximized window does nothing, so every
                // quick grab at the very top missed.  There it is the caption,
                // except over the caption buttons, whose top pixel must still
                // close the window.
                if (WindowState == WindowState.Maximized && TopBandHit(lParam) is { } code)
                {
                    handled = true;
                    return new IntPtr(code);
                }

                return IntPtr.Zero;

            case WmNcLeftButtonDown when wParam.ToInt32() == HtMaxButton:
                handled = true;
                return IntPtr.Zero;

            case WmNcLeftButtonUp when wParam.ToInt32() == HtMaxButton:
                handled = true;
                ToggleMaximized();
                return IntPtr.Zero;

            case WmNcMouseLeave:
                SetMaximizeHover(false);
                return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Keeps a maximized window inside the monitor work area.
    ///
    /// The window keeps its sizing frame but has no caption, so Windows maximizes
    /// it to the work area grown by the frame width on every side.  WindowChrome
    /// then makes the client area the whole window and clips the overhang away
    /// with a region - so the outer band of the UI, including the top of the
    /// caption buttons and the bottom of the status bar, is laid out off-screen
    /// and simply cut off.  Clamping the maximized rectangle is the fix; a
    /// compensating margin would need a frame width that differs per monitor.
    ///
    /// Everything here is device pixels straight from the monitor, which is what
    /// makes it correct on a second monitor at another scale.
    /// </summary>
    private static void ClampMaximizedBounds(IntPtr window, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        bounds.MaxPosition = new PointL
        {
            X = info.Work.Left - info.Monitor.Left,
            Y = info.Work.Top - info.Monitor.Top
        };
        bounds.MaxSize = new PointL
        {
            X = info.Work.Right - info.Work.Left,
            Y = info.Work.Bottom - info.Work.Top
        };

        Marshal.StructureToPtr(bounds, lParam, false);
    }

    private const int HtClient = 1;
    private const int HtCaption = 2;

    /// <summary>
    /// For a maximized window: the caption code for a point in the top band,
    /// the client code over the caption buttons, and null anywhere else (where
    /// WindowChrome's own answer stands).
    /// </summary>
    private int? TopBandHit(IntPtr lParam)
    {
        var packed = lParam.ToInt64();
        var screenPoint = new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
        try
        {
            var local = PointFromScreen(screenPoint);
            if (local.Y < 0 || local.Y > 8)
            {
                return null;
            }

            var buttons = CaptionButtons.PointFromScreen(screenPoint);
            return buttons.X >= 0 && buttons.X <= CaptionButtons.ActualWidth ? HtClient : HtCaption;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The tab is what the eye goes to when the window is to be moved, as in a
    /// browser: pressing it anywhere but its close button picks the window up,
    /// through the caption's own machinery, so snapping and dragging a
    /// maximized window back down work exactly as on the empty strip beside it.
    /// </summary>
    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        e.Handled = true;
        if (e.ClickCount == 2)
        {
            ToggleMaximized();
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        ReleaseCapture();
        SendMessage(handle, WmNcLeftButtonDown, new IntPtr(HtCaption), IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private bool IsOverMaximizeButton(IntPtr lParam)
    {
        if (MaximizeButton is null || !MaximizeButton.IsVisible)
        {
            return false;
        }

        var packed = lParam.ToInt64();
        var screenPoint = new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));

        try
        {
            var local = MaximizeButton.PointFromScreen(screenPoint);
            return local.X >= 0
                && local.Y >= 0
                && local.X <= MaximizeButton.ActualWidth
                && local.Y <= MaximizeButton.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void SetMaximizeHover(bool hover)
    {
        if (_maximizeHover == hover || MaximizeButton is null)
        {
            return;
        }

        _maximizeHover = hover;
        MaximizeButton.Background = hover ? CaptionHoverBrush : Brushes.Transparent;
    }

    private const int MonitorDefaultToNearest = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct RectL
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PointL Reserved;
        public PointL MaxSize;
        public PointL MaxPosition;
        public PointL MinTrackSize;
        public PointL MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public RectL Monitor;
        public RectL Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private static Brush CreateFrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);
}
