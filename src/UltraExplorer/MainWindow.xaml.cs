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
using UltraExplorer.Infrastructure;
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

    /// <summary>Set once a close has started saving, so it saves once.</summary>
    private bool _closeRequested;

    /// <summary>How long a session that Windows is ending may take to save.</summary>
    private static readonly TimeSpan SessionEndSaveTimeout = TimeSpan.FromSeconds(3);
    private bool _maximizeHover;
    private bool _sidebarCollapsed;
    private double _restoredSidebarWidth = 240;

    /// <summary>Set once the start has put the saved width on the sidebar (see <see cref="CaptureStateForSave"/>).</summary>
    private bool _sidebarWidthRestored;
    private bool _isSpaceHeld;
    private bool _isSpacePanning;
    private Point _panPointerAnchor;
    private Point _panViewportAnchor;
    private ViewAllNodeViewModel? _overviewDragNode;
    private Point _overviewDragPointerAnchor;
    private Point _overviewDragNodeOrigin;
    private bool _overviewDragMoved;
    private bool _folderListClickWasOnSelection;
    private bool _folderListMouseDown;
    private bool _addressMayComplete;
    private bool _addressCompleting;
    private readonly DispatcherTimer _folderListRenameTimer = new(DispatcherPriority.Input)
    {
        Interval = TimeSpan.FromMilliseconds(NativeShellService.DoubleClickMilliseconds + 60)
    };

    /// <summary>The row a click on the selection asked to rename, once the double-click time has passed.</summary>
    private FolderListItem? _folderListRenameRow;

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
        : this(picker, null)
    {
    }

    public MainWindow(FileDialogSession? picker, string? normalWorkspacePath)
    {
        _viewModel = new MainViewModel(picker?.Request.IsNativeProxy == true
            ? Infrastructure.AppPaths.State("dialog-integration/proxy-" + Guid.NewGuid().ToString("N") + ".workspace.json")
            : picker is null ? normalWorkspacePath is null ? null : normalWorkspacePath + ".tree.json" : FileDialogHost.WorkspacePath,
            nestedPicker: picker?.Request.IsNativeProxy == true,
            normalWorkspacePath: picker is null ? normalWorkspacePath : null);
        _viewModel.SuppressShellWrites = picker?.Request.IsNativeProxy == true;

        InitializeComponent();
        DataContext = _viewModel;
        AttachFolderInvocationState();

        // A search puts first what is in the folder the headers would sort:
        // the one selected, the one the selected file is in, or the one in view.
        _viewModel.Search.HereFolder = SortFolder;

        // A benchmark or snapshot run, or a copy started to try a build, opens
        // on a monitor nobody is using and must not take the keyboard from
        // whatever the user is doing.
        if (IsDiagnosticsRun || IsTestWindow)
        {
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        ConfigureFigmaGestures();

        if (Application.Current is { } application)
        {
            application.SessionEnding += OnSessionEnding;
        }

        _viewModel.FitAllRequested += FitAll;
        _viewModel.ZoomRequested += ApplyZoom;
        _viewModel.PromptRequested += ShowInputDialog;

        // The shelf comes out for any drag over the window - one of its own or
        // one from outside - and goes back once the drag is over.
        Shelf.Initialize(new ShelfStore(), _viewModel);
        Shelf.CompleteDrop = CompleteExternalDrop;
        Shelf.Enabled = _viewModel.ShowDropShelf && !IsPickerMode;
        AddHandler(DragDrop.PreviewDragEnterEvent, new DragEventHandler((_, _) => Shelf.NotifyDragOver()), handledEventsToo: true);
        AddHandler(DragDrop.PreviewDragOverEvent, new DragEventHandler((_, _) => Shelf.NotifyDragOver()), handledEventsToo: true);
        _viewModel.ConfirmRequested += ShowConfirmDialog;
        _viewModel.ContextMenuRequested += ShowContextMenu;
        _viewModel.Tree.FocusNodeRequested += FocusNode;
        _viewModel.Tree.GraphInvalidated += OnGraphInvalidated;
        _viewModel.Tree.ViewShiftRequested += OnViewShiftRequested;

        ConfigureNodeDrag();
        ConfigureAutoPanning();
        _folderListRenameTimer.Tick += FolderListRename_Tick;

        _viewModel.Address.EditRequested += FocusAddressBox;
        _viewModel.Address.CompletionOffered += OfferAddressCompletion;
        _viewModel.Address.PropertyChanged += Address_PropertyChanged;

        Overview.Index = _viewModel.Tree.SpatialIndex;
        Harness.Index = _viewModel.Tree.SpatialIndex;
        AttachNested();
        AttachSelection();
        AttachHoverPreviews();

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
            ConfigurePickerSelection();
        }

        UpdateSplitControls();
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
            PlaceForDiagnostics(handle, neverActivate: true);
        }

        PlaceOverCaller?.Invoke(handle);
        FitIntoWorkArea(handle);

        var darkMode = 1;
        var cornerPreference = 2;
        _ = DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 33, ref cornerPreference, sizeof(int));
        HwndSource.FromHwnd(handle)?.AddHook(HandleWindowMessage);

        // The window owns the context menus: dark, like it.
        DarkMenus.AllowForWindow(handle);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_closeRequested) return;
        // The dialog's entry rules go in before the first read, so the folder
        // the caller asked for is already filtered when it appears.
        await ApplyPickerRulesAsync();
        if (_closeRequested) return;
        await _viewModel.InitializeAsync(_folderInitialPath ?? _pickerStartFolder);
        if (_closeRequested) return;
        foreach (var pane in _panes)
        {
            pane.Canvas.LeftDrag = _viewModel.LeftDrag;

            // Before the drives go in, so a canvas without its files is never
            // placed with them first.
            pane.Canvas.ShownLayers = _viewModel.Layers;
        }

        await InitializeNestedAsync();
        if (_closeRequested) return;
        if (_picker is not null && IsNested) await ApplyPickerRulesAsync();
        if (_closeRequested) return;

        _restoredSidebarWidth = _viewModel.SidebarWidth;
        SidebarColumn.Width = new GridLength(_restoredSidebarWidth);
        _sidebarWidthRestored = true;

        await Dispatcher.InvokeAsync(() =>
        {
            if (_closeRequested) return;
            PushViewport();
            if (IsPickerMode && IsNested && ActivePane.Tree.Find(_pickerStartFolder) is { } start)
            {
                ActivePane.Canvas.FlyTo(start, 0.92, animated: false);
            }
            else if (_viewModel.Tree.HasRestoredViewport)
            {
                Editor.ViewportZoom = _viewModel.Tree.RestoredViewportZoom;
                Editor.ViewportLocation = _viewModel.Tree.RestoredViewportLocation;
            }
            else if (_folderInitialPath is not null)
            {
                // A window opened for a folder - every Explorer-replacement
                // window - has no saved view either, but its folder is on its
                // way (ApplyFolderInvocationAsync) and the start has put the
                // camera on it already.  Framing This PC here flew out from the
                // folder, and the flight ran on until the folder landed and the
                // view snapped back to it.
            }
            else
            {
                // First run: the profile branch is already open, so framing the
                // whole graph is more useful than centring one node.
                FitAll();
            }

            PushViewport();
        }, DispatcherPriority.Loaded);

        // The window is whole from here: its state read, its canvas up, its
        // layout restored.  A failure before this ends the process instead of
        // leaving a half-built window that would save its defaults on close.
        CrashReporter.MarkStarted();
        _folderLaunchReady.TrySetResult();

        // The shares and WSL distributions the workspace lists are asked for
        // only now, all at once: one whose server is off takes as long as the
        // network allows to say so, and the window does not wait on it.  Each
        // that answers joins the canvases as it does.
        _viewModel.Tree.ExtraRootAdded += OnExtraRootAdded;
        if (!IsPickerMode) _ = _viewModel.Tree.ProbeExtraRootsAsync();

        _capture = WindowCaptureService.TryCreate(this, Environment.GetCommandLineArgs());

        // A prepared picker is pictured once a dialog has it (CaptureForTests).
        if (!_keyboardWithheld)
        {
            _capture?.Start();
        }

        if (_picker is null && TryStartNestedDiagnostics())
        {
            return;
        }

        // Once the window is up and has nothing else to do, the Shell's menu
        // handlers are loaded on a thread of their own and kept loaded, so
        // that no right-click waits for them.  Not in a replacement for
        // another program's dialog: it lives in a resident worker, where the
        // handlers would stay loaded for the whole session for the rare
        // right-click in a picker, which builds its menu cold instead.
        if (_picker?.Request.IsNativeProxy != true)
        {
            _ = Dispatcher.InvokeAsync(
                () => ShellMenuWarmUp.Start(Infrastructure.AppPaths.State("shell-menus")),
                DispatcherPriority.ApplicationIdle);
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
            if (IsNested)
            {
                _picker.CurrentFolder = _pickerStartFolder;
            }
            else if (await _viewModel.Tree.RevealPathAsync(_pickerStartFolder, focus: false) is { } requestedFolder)
            {
                // A fresh picker has no saved camera, so FitAll frames every
                // drive. Put the requested folder and its files at a readable
                // size before the user starts choosing.
                await _viewModel.Tree.ExpandAsync(requestedFolder);
                Editor.ViewportZoom = Math.Max(Editor.ViewportZoom, 1.25);
                FocusNode(requestedFolder, false);
            }
            _pickerNestedReady = IsNested;
            if (WithholdsKeyboard && !IsActive) FocusManager.SetFocusedElement(this, PickerNameBox);
            else PickerNameBox.Focus();
            return;
        }

        // A test copy on the other monitor must not take the keyboard: focusing
        // an element activates its window, which ShowActivated alone does not stop.
        if (!IsTestWindow)
        {
            FocusCanvas();
        }

        await RunSelectionDemoAsync();
        await RunMenuDemoAsync();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose && _quickPreview is { HasUnsavedChanges: true } preview)
        {
            e.Cancel = true;
            if (_quickPreviewClosingOwner) return;
            _quickPreviewClosingOwner = true;
            try
            {
                if (await preview.ConfirmCloseAsync()) _ = Dispatcher.InvokeAsync(Close);
            }
            finally { _quickPreviewClosingOwner = false; }
            return;
        }
        // However the window goes away, the caller gets an answer.
        CompletePickerOnClose();

        if (_allowClose)
        {
            if (Application.Current is { } application)
            {
                application.SessionEnding -= OnSessionEnding;
            }

            DependencyPropertyDescriptor
                .FromProperty(NodifyEditor.ViewportLocationProperty, typeof(NodifyEditor))
                .RemoveValueChanged(Editor, OnViewportLocationChanged);
            _viewModel.Tree.ExtraRootAdded -= OnExtraRootAdded;
            DetachNodeDrag();
            DetachSelection();
            DetachNested();
            DropPreparedMenu();
            _capture?.Dispose();
            _viewModel.Dispose();
            return;
        }

        e.Cancel = true;

        // One save per close: a second click on the close button while the
        // first save is still being written waits for that one.
        if (_closeRequested)
        {
            return;
        }

        _closeRequested = true;
        try
        {
            // A nested drop pumps UI messages while its transfer completes.
            // A Close received there must not dispose the model/dispatcher
            // before it has acquired and copied an archive source's bytes.
            if (_pendingExternalDrop is { } transfer)
            {
                // Said at once: the copy can take minutes, and a window whose
                // close button and Alt+F4 did nothing that long looked hung.
                if (!transfer.IsCompleted) _viewModel.Toast.ShowBusy("Closing once the drop in progress has finished…");
                try { await transfer; }
                catch (Exception) { /* The drop handler reports the transfer error; still save the window. */ }
            }
            CaptureStateForSave();
            await _viewModel.SaveNowAsync();
        }
        catch (Exception exception)
        {
            // A window that cannot be closed is worse than a session that
            // could not be written, so the failure is recorded and the close
            // goes on.
            CrashReporter.Log("saving the session on close", exception);
        }
        finally
        {
            _allowClose = true;

            // Posted, never called from here: the save returns at once when
            // there is nothing to save yet, and Close() from inside Closing
            // throws.
            _ = Dispatcher.InvokeAsync(Close);
        }
    }

    /// <summary>
    /// What the window holds that the model does not, put into it before a
    /// save.  The sidebar's width only once the start has put the saved width
    /// on it: a window closed - or a session ended - while it was still
    /// starting read the 240 the sidebar has until then, and saved that over
    /// the width the user had dragged it to.  Until then the model still
    /// holds the width it read.
    /// </summary>
    private void CaptureStateForSave()
    {
        if (_sidebarWidthRestored)
        {
            _viewModel.SidebarWidth = SidebarColumn.ActualWidth > 0 ? SidebarColumn.ActualWidth : _restoredSidebarWidth;
        }

        CaptureNestedCamera();
    }

    /// <summary>
    /// Logoff, shutdown, or an installer closing the app through Restart
    /// Manager to update it.  WPF then shuts down without honouring the
    /// Cancel that <see cref="Window_Closing"/> uses to save first, so that
    /// save would be cut off.  It is made here instead, while Windows waits for
    /// the answer to WM_QUERYENDSESSION: the dispatcher keeps running until the
    /// save is on disk, for a few seconds at most.
    /// </summary>
    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        if (_allowClose || _closeRequested)
        {
            return;
        }

        _closeRequested = true;
        try
        {
            if (_quickPreview is { HasUnsavedChanges: true } preview)
            {
                var noteSave = preview.CommitForCloseAsync();
                if (!noteSave.IsCompleted)
                {
                    var noteFrame = new DispatcherFrame();
                    _ = noteSave.ContinueWith(_ => noteFrame.Continue = false, TaskScheduler.Default);
                    using var noteTimeout = new Timer(_ => noteFrame.Continue = false, null, SessionEndSaveTimeout, Timeout.InfiniteTimeSpan);
                    Dispatcher.PushFrame(noteFrame);
                }
                if (!noteSave.IsCompletedSuccessfully || !noteSave.Result)
                {
                    e.Cancel = true;
                    _closeRequested = false;
                    return;
                }
            }
            CaptureStateForSave();
            var saving = _viewModel.SaveNowAsync();
            if (!saving.IsCompleted)
            {
                var frame = new DispatcherFrame();
                _ = saving.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                using var giveUp = new Timer(_ => frame.Continue = false, null, SessionEndSaveTimeout, Timeout.InfiniteTimeSpan);
                Dispatcher.PushFrame(frame);
            }

            if (saving.IsFaulted)
            {
                CrashReporter.Log("saving the session as Windows ends it", saving.Exception);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            CrashReporter.Log("saving the session as Windows ends it", exception);
        }
        finally
        {
            // The Closing that follows only tidies up.
            _allowClose = !e.Cancel;
        }
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

    /// <summary>
    /// Nodify's editor pans by itself while something is dragged against its
    /// edge, and it checks for that on a timer of a millisecond - which Windows
    /// runs at every tick of its clock, 64 times a second - from the moment its
    /// template is applied, dragged or not, shown or not.  It only ever pans
    /// while the mouse is captured inside it, so the timer runs only then:
    /// the same panning, and a window at rest that wakes nobody.
    /// </summary>
    private void ConfigureAutoPanning()
    {
        Editor.DisableAutoPanning = true;
        Editor.IsMouseCaptureWithinChanged += (_, e) => Editor.DisableAutoPanning = _isSpacePanning || e.NewValue is not true;
    }

    private void SetSpacePanArmed(bool armed)
    {
        if (_isSpaceHeld == armed)
        {
            return;
        }

        _isSpaceHeld = armed;
        Editor.Cursor = armed ? Cursors.Hand : null;
        foreach (var pane in _panes)
        {
            pane.Canvas.IsSpacePanArmed = armed;
            pane.Canvas.Cursor = armed ? Cursors.Hand : null;
        }
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
            Editor.BeginPanning();
            Editor.DisableAutoPanning = true;
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
        // The viewport dependency-property callback updates the model once.
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

        Editor.EndPanning();
        Editor.ReleaseMouseCapture();
        Editor.DisableAutoPanning = !Editor.IsMouseCaptureWithin;
        Editor.Cursor = _isSpaceHeld ? Cursors.Hand : null;
        e.Handled = true;
    }

    /// <summary>A drag that loses capture - Alt+Tab, a dialog - must still end.</summary>
    private void Editor_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isSpacePanning) Editor.EndPanning();
        _isSpacePanning = false;
        Editor.DisableAutoPanning = !Editor.IsMouseCaptureWithin;
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
            if (_viewModel.Tree.FocusedPath is { Length: > 0 } focused)
            {
                _ = FlyNestedToAsync(focused, gentle: false);
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
        if (!IsNested) CancelSpacePreview();
        if (_hoverPreview is not null) ClearHoverPreview();
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
    /// A tilt wheel or a touchpad's sideways swipe pans the canvas the pointer
    /// is over, as Shift+wheel does - the other way round, because tilting
    /// right means "show me what is to the right", which is Shift+wheel down.
    /// It goes where the upright wheel would: to the canvas only when the
    /// canvas is what is under the pointer, so over the folder list, a panel
    /// or another program it is left to them.
    /// </summary>
    partial void OnHorizontalWheelMessage(IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // The distance is the signed high word of wParam, positive for right;
        // the pointer is in screen pixels in lParam, as for the upright wheel.
        var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        var packed = lParam.ToInt64();
        var screenPoint = new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
        if (delta == 0)
        {
            return;
        }

        Point local;
        try
        {
            local = PointFromScreen(screenPoint);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        // What is under the pointer can be a run of text inside a node rather
        // than an element, so the canvas is looked for among its ancestors.
        var over = InputHitTest(local) as DependencyObject;
        var pane = IsNested ? PaneAt(over) : null;
        var overCanvas = IsNested
            ? pane is not null
            : ReferenceEquals(FindAncestor<NodifyEditor>(over), Editor);
        if (!overCanvas)
        {
            return;
        }

        if (pane is not null)
        {
            // The canvas's own Shift+wheel, turned round, on the pane under the pointer.
            pane.Canvas.PointerWheel(pane.Canvas.PointFromScreen(screenPoint), -delta, ModifierKeys.Shift);
        }
        else
        {
            // Nodify's Shift+wheel moves a notch's worth of 60 pixels on screen
            // whatever the notch says; here the distance follows the delta, so a
            // touchpad's small steps stay small and a tilt notch of 120 comes to
            // the same 60 pixels.
            var zoom = Math.Max(Editor.ViewportZoom, 0.001);
            Editor.UpdatePanning(new Vector(-delta / 2.0 / zoom, 0));
        }

        handled = true;
    }

    /// <summary>
    /// Enter in the folder-list filter opens the best match, which is what makes
    /// the list a way of getting somewhere rather than only of looking.  On the
    /// preview, because the text box keeps the arrow keys for its caret and
    /// they never bubble up to a KeyDown handler.
    /// </summary>
    private void FolderListFilter_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Every Alt combination arrives as Key.System with the real key in
        // SystemKey, so reading e.Key alone never sees Alt+Up or Alt+Left.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Enter)
        {
            _viewModel.Tree.FolderList.OpenFirstMatchCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            _viewModel.Tree.FolderList.ClearFilterCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Down arrow moves into the list, so typing and picking is one gesture.
        if (key == Key.Down && Keyboard.Modifiers == ModifierKeys.None && FolderListItems.Items.Count > 0)
        {
            NoteListInput();
            FolderListItems.SelectedIndex = 0;
            (FolderListItems.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
            return;
        }

        // Explorer's own shortcuts, so the muscle memory carries over.  Backspace
        // is deliberately not one of them: it belongs to the text being typed.
        if (Keyboard.Modifiers == ModifierKeys.Alt && key == Key.Up)
        {
            _viewModel.Tree.FolderList.UpCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Alt && key == Key.Left)
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
    /// Remembers whether the row was already the one selected row before this
    /// click, because the list itself selects it on the way down and by
    /// button-up the answer is always yes.  A click with Ctrl or Shift is
    /// never the start of a rename.  From here until the button comes up,
    /// what the list box selects is the user's doing (see
    /// <see cref="FolderListItems_SelectionChanged"/>).  Any press calls off
    /// a rename still waiting: a click on another row is not a rename of
    /// whichever row that one selects.
    /// </summary>
    private void FolderListItems_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _folderListRenameTimer.Stop();
        _folderListMouseDown = true;
        NoteListInput();
        _folderListClickWasOnSelection =
            RowUnder(e) is { } row
            && Keyboard.Modifiers == ModifierKeys.None
            && FolderListItems.SelectedItems.Count == 1
            && ReferenceEquals(FolderListItems.SelectedItem, row)
            && FolderListItems.IsKeyboardFocusWithin;
    }

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
        _folderListMouseDown = false;
        if (!_viewModel.Tree.FolderList.HasCurrentRows) return;

        // With Ctrl or Shift the click was about the selection - the list box
        // has made it, and the canvas shows it - not about going anywhere.
        if (e.ClickCount != 1 || RowUnder(e) is not { } item || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        if (_folderListClickWasOnSelection)
        {
            _folderListRenameRow = item;
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
        if (!_viewModel.Tree.FolderList.HasCurrentRows) return;
        if (RowUnder(e) is not { } item)
        {
            return;
        }

        // A row outside the selection becomes the selection, as in Explorer;
        // one inside it keeps the whole set, and the menu is for all of it.
        e.Handled = true;
        var selection = _viewModel.Tree.Selection;
        if (!selection.Contains(item.FullPath))
        {
            using (_viewModel.Tree.FolderList.HoldFolder())
            {
                selection.ReplaceSingle(item.FullPath, item.IsDirectory, item.Entry.SizeBytes ?? 0, SelectionSource.List);
            }
        }

        _viewModel.Tree.FolderList.Highlight(item);
        ShowContextMenu(
            selection.Count > 0 ? selection.Paths : [item.FullPath],
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
    /// what Explorer does with the empty part of a folder, with the folder's
    /// own settings and the canvas's commands below, as on the canvas.
    /// </summary>
    private void FolderListPanel_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (RowUnder(e) is not null)
        {
            // A row has its own menu; that handler runs first and marks it.
            return;
        }

        var folder = _viewModel.Tree.FolderList.FolderPath;
        if (folder.Length == 0)
        {
            return;
        }

        e.Handled = true;
        if (ShowFolderAreaShellMenu(folder, FolderListPanel, e.GetPosition(FolderListPanel)))
        {
            // Whatever it did - a new file, a paste - the list has to look again.
            _ = _viewModel.Tree.RefreshFolderListAsync();
            return;
        }

        // No Shell menu for this folder: the app's own is better than nothing.
        var menu = new ContextMenu { PlacementTarget = FolderListPanel };
        AddCommandItem(menu, "New folder", "\uE8F4", _viewModel.NewFolderCommand, "Ctrl+Shift+N");
        AddCommandItem(menu, "New text file", "\uE8A5", _viewModel.NewTextFileCommand);
        AddCommandItem(menu, "Paste", "\uE77F", _viewModel.PasteCommand, "Ctrl+V");
        menu.IsOpen = true;
    }

    /// <summary>
    /// The right button going down on the list: the menu its release will ask
    /// for - the row's, which is the selection's when the row is part of it,
    /// or the folder's open space - is built meanwhile.
    /// </summary>
    private void FolderListPanel_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.Tree.FolderList.HasCurrentRows) return;
        if (RowUnder(e) is { } item)
        {
            var selection = _viewModel.Tree.Selection;
            PrepareShellMenu(background: false, selection.Contains(item.FullPath) && selection.Count > 0 ? selection.Paths : [item.FullPath]);
            return;
        }

        if (_viewModel.Tree.FolderList.FolderPath is { Length: > 0 } folder)
        {
            PrepareShellMenu(background: true, [folder]);
        }
    }

    /// <summary>The folder-list row the mouse is over, or null between rows.</summary>
    private FolderListItem? RowUnder(MouseButtonEventArgs e)
        => ItemsControl.ContainerFromElement(FolderListItems, e.OriginalSource as DependencyObject)
            is ListBoxItem container && container.DataContext is FolderListItem item
            ? item
            : null;

    /// <summary>
    /// Starts the rename only once the double-click time has passed without a
    /// second click, so opening something never opens a rename box first -
    /// and only of the row that was clicked, while it is still the one row
    /// selected.  The selection moving on meanwhile, by a key or by the
    /// canvas, made it a rename of whatever was selected by then.
    /// </summary>
    private void FolderListRename_Tick(object? sender, EventArgs e)
    {
        _folderListRenameTimer.Stop();
        var clicked = _folderListRenameRow;
        _folderListRenameRow = null;
        if (FolderListItems.SelectedItems.Count == 1
            && FolderListItems.SelectedItem is FolderListItem row
            && clicked is not null
            && _viewModel.Tree.FolderList.IsCurrentRow(row)
            && ViewAllPath.Equals(row.FullPath, clicked.FullPath))
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
            ActivePane.Canvas.FitAll();
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
                ActivePane.Canvas.FitAll();
            }
            else
            {
                ActivePane.Canvas.ZoomStep(factor > 1);
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
    /// <summary>
    /// The right button going down on the tree canvas: the menu its release
    /// will ask for - a node's, which is the selection's when the node is
    /// part of it, or the open space of the folder under the pointer - is
    /// built meanwhile.
    /// </summary>
    private void Editor_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ViewAllNodeView>(e.OriginalSource as DependencyObject) is { DataContext: ViewAllNodeViewModel node })
        {
            PrepareShellMenu(background: false, node.IsSelected ? _viewModel.Tree.SelectedPaths : [node.FullPath]);
            return;
        }

        if (_viewModel.Tree.FolderAt(Editor.GetLocationInsideEditor(e)) is { IsDirectory: true } area)
        {
            PrepareShellMenu(background: true, [area.FullPath]);
        }
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

    /// <summary>The data object of the drag being read, and the paths it was found to carry (see <see cref="TryGetDropPaths"/>).</summary>
    private IDataObject? _dropData;
    private string[] _dropPaths = [];
    private Task<bool>? _pendingExternalDrop;
    private bool _holdingExternalDrop;

    bool INestedPaneHost.CompleteExternalDrop(IDataObject data, IReadOnlyList<string> paths, DragDropEffects reported, Func<Task<bool>> beginTransfer)
        => CompleteExternalDrop(data, paths, reported, beginTransfer);

    /// <summary>
    /// Runs a drop's transfer: true when the drop is to be reported as done.
    /// An affirmatively async source retains its bytes after Drop returns;
    /// every other source is held only until its files are copied (see
    /// <see cref="ExternalFileDrop"/>). A drop that would have to be held
    /// while another is held is refused: its frame inside the first one's
    /// would keep the first source blocked until the second ended.  Close
    /// waits for every transfer still running.
    /// </summary>
    private bool CompleteExternalDrop(IDataObject data, IReadOnlyList<string> paths, DragDropEffects reported, Func<Task<bool>> beginTransfer)
    {
        if (_closeRequested) return false;
        if (ExternalFileDrop.TryContinue(data, beginTransfer, reported, out var asyncTransfer))
        {
            TrackExternalDrop(asyncTransfer);
            return true;
        }

        if (_holdingExternalDrop) return false;
        _holdingExternalDrop = true;
        try { return ExternalFileDrop.Complete(Dispatcher, beginTransfer, TrackExternalDrop); }
        finally { _holdingExternalDrop = false; }
    }

    /// <summary>Keeps a dropped transfer in what Close waits for until it has ended.</summary>
    private void TrackExternalDrop(Task<bool> transfer)
    {
        var all = _pendingExternalDrop is { } earlier ? AfterBothAsync(earlier, transfer) : transfer;
        _pendingExternalDrop = all;
        _ = ForgetExternalDropAsync(all);
    }

    private async Task ForgetExternalDropAsync(Task<bool> all)
    {
        try { await all; }
        catch (Exception) { /* The drop's own toast reports the failure. */ }
        if (ReferenceEquals(_pendingExternalDrop, all)) _pendingExternalDrop = null;
    }

    private static async Task<bool> AfterBothAsync(Task<bool> first, Task<bool> second)
    {
        try { await first; }
        catch (Exception) { /* Reported by its own drop. */ }
        return await second;
    }

    /// <summary>The last folder the drag was over, and whether its items may not go in (see <see cref="IsDropRefused"/>).</summary>
    private string? _dropCheckedFolder;
    private bool _dropFolderRefused;

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
        var effect = target is null ? DragDropEffects.None : DropEffectFor(e, paths, target.FullPath);
        _viewModel.Tree.SetDropTarget(effect == DragDropEffects.None ? null : target);
        e.Effects = effect;
        e.Handled = true;
    }

    private void Editor_PreviewDragLeave(object sender, DragEventArgs e)
    {
        ForgetDropPaths();
        if (!Editor.IsMouseOver)
        {
            _viewModel.Tree.SetDropTarget(null);
        }
    }

    private void Editor_PreviewDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        try
        {
            // Re-read the final paths: a source can have completed extraction
            // since DragOver supplied its preview list.
            ForgetDropPaths();
            var carriesPaths = TryGetDropPaths(e.Data, out var paths);
            ForgetDropPaths();
            if (!carriesPaths) return;

            var target = ResolveDropTarget(e, paths);
            _viewModel.Tree.SetDropTarget(null);
            if (target is null) return;

            var effect = DropEffectFor(e, paths, target.FullPath);
            if (effect == DragDropEffects.None) return;

            if (CompleteExternalDrop(e.Data, paths, ReportedDropEffect(effect), () => _viewModel.DropIntoPathWithResultAsync(
                    paths, target.FullPath, move: effect == DragDropEffects.Move)))
                e.Effects = ReportedDropEffect(effect);
        }
        catch (Exception error)
        {
            ForgetDropPaths();
            _viewModel.Tree.SetDropTarget(null);
            _viewModel.Toast.ShowError(error.Message);
        }
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
        return IsDropRefused(target.FullPath, () => paths.Any(path =>
            ViewAllPath.Equals(path, target.FullPath)
            || NativeShellService.IsInvalidMoveTarget(path, target.FullPath)))
            ? null
            : target;
    }

    /// <summary>
    /// What dropping here does: Explorer's rule (<see cref="MainViewModel.ShouldMove"/>)
    /// with the keys the drag itself reports, then kept to what the drag's
    /// source allows - what the keys ask for if it may, otherwise a copy,
    /// otherwise a move, otherwise nothing.  A source that only lets its items
    /// be copied - a browser, a mail attachment, a zip file - is not offered a
    /// move it would refuse.
    ///
    /// The keys are the drag's, not <see cref="Keyboard.Modifiers"/>: while
    /// another program runs the drag, this window is never given the keyboard,
    /// and its own idea of what is held down is whatever it was when it last
    /// had it - so Ctrl held over a drag from Explorer went unseen, and a copy
    /// on the same drive was made a move.
    /// </summary>
    internal static DragDropEffects DropEffectFor(DragEventArgs e, IReadOnlyList<string> paths, string targetDirectory)
    {
        var modifiers = ModifierKeys.None;
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0)
        {
            modifiers |= ModifierKeys.Shift;
        }

        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0)
        {
            modifiers |= ModifierKeys.Control;
        }

        if ((e.KeyStates & DragDropKeyStates.AltKey) != 0)
        {
            modifiers |= ModifierKeys.Alt;
        }

        var wanted = MainViewModel.ShouldMove(paths, targetDirectory, modifiers)
            ? DragDropEffects.Move
            : DragDropEffects.Copy;
        foreach (var effect in new[] { wanted, DragDropEffects.Copy, DragDropEffects.Move })
        {
            if ((e.AllowedEffects & effect) == effect)
            {
                return effect;
            }
        }

        return DragDropEffects.None;
    }

    /// <summary>
    /// What the source of a drop is told was done, which some sources act on.
    /// A copy is said as a copy, so the originals are left alone.  A move is
    /// made here, whole, and only once the drop has returned: the source is
    /// told there is nothing left for it to do, because a source that hears
    /// "moved" may delete what it takes to be its leftover originals - before
    /// this move has even picked them up.
    /// </summary>
    internal static DragDropEffects ReportedDropEffect(DragDropEffects effect)
        => effect == DragDropEffects.Copy ? DragDropEffects.Copy : DragDropEffects.None;

    /// <summary>
    /// The files a drag carries, read from it once per drag.  DragOver comes
    /// with every move of the pointer, and each read of a drag from another
    /// program is a call into that program and a fresh copy of every path it
    /// carries.  A drag's data object stays the same one from the moment it
    /// comes over the window to its drop, so the answer is kept against it;
    /// leaving or dropping lets it go (see <see cref="ForgetDropPaths"/>).
    /// </summary>
    private bool TryGetDropPaths(IDataObject data, out string[] paths)
    {
        if (!ReferenceEquals(data, _dropData))
        {
            ForgetDropPaths();
            _dropData = data;
            _dropPaths = data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] dropped
                ? dropped
                : [];
        }

        paths = _dropPaths;
        return paths.Length > 0;
    }

    /// <summary>
    /// Whether the drag's items may not go into <paramref name="folder"/>, as
    /// <paramref name="refuses"/> decides - asked once for each folder the
    /// pointer comes to rather than on every DragOver while it stays there,
    /// because the answer compares every item the drag carries.
    /// </summary>
    private bool IsDropRefused(string folder, Func<bool> refuses)
    {
        if (!string.Equals(folder, _dropCheckedFolder, StringComparison.OrdinalIgnoreCase))
        {
            _dropCheckedFolder = folder;
            _dropFolderRefused = refuses();
        }

        return _dropFolderRefused;
    }

    /// <summary>The drag left or was dropped: what it carried is not kept for the next one.</summary>
    private void ForgetDropPaths()
    {
        _dropData = null;
        _dropPaths = [];
        _dropCheckedFolder = null;
        _dropFolderRefused = false;
    }

    // ---- Context menus -----------------------------------------------------

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

        // The split view's, as the Shell's menu has them below its own.
        var selection = _viewModel.Tree.Selection;
        var focus = selection.Focus is { } focused && selection.TryGetItem(focused, out var item) && item.IsDirectory ? focused : null;
        var panes = OtherPaneEntries(selection.Paths, focus);
        if (panes.Count > 0)
        {
            menu.Items.Add(new Separator());
            AddEntries(menu, panes);
        }

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
    /// Canvas options ▸ Order and Sort: which way a folder's items fill its
    /// grid - down each column, as a list reads (the default), or along each
    /// row as before - and whether a header sorts the folder it is used on
    /// (the default) or every folder the same, as before.
    /// </summary>
    private void AddArrangeItems(ItemsControl menu)
    {
        var orders = _viewModel.Orders;
        var flow = new MenuItem { Header = "Order" };
        foreach (var (choice, name, tip) in new[]
        {
            (LayoutOrder.DownThenAcross, "Down, then across", "Each folder's items go down the first column, then down the next, as a list reads"),
            (LayoutOrder.AcrossThenDown, "Across, then down", "Each folder's items go along the first row, then along the next")
        })
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = orders.Flow == choice, ToolTip = tip };
            var chosen = choice;
            item.Click += (_, _) => orders.Flow = chosen;
            flow.Items.Add(item);
        }

        menu.Items.Add(flow);

        var scope = new MenuItem { Header = "Sort" };
        foreach (var (choice, name, tip) in new[]
        {
            (SortScope.PerFolder, "Each folder separately", "A header or Sort by orders the folder it is used on; every other folder keeps its own order"),
            (SortScope.AllFolders, "All folders the same", "A header or Sort by orders every folder; the folders' own orders wait until this is switched back")
        })
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = orders.Scope == choice, ToolTip = tip };
            var chosen = choice;
            item.Click += (_, _) => orders.Scope = chosen;
            scope.Items.Add(item);
        }

        menu.Items.Add(scope);
    }

    /// <summary>
    /// What draws the nested canvas: automatic (the graphics card where it
    /// can, the processor elsewhere), the card, or the processor - and, at the
    /// top, what is drawing it now and why.  When the command line or
    /// ULTRAEXPLORER_RENDERER chose for this run, the choice is shown and
    /// cannot be changed here.
    /// </summary>
    private void AddRendererItems(ItemsControl menu)
    {
        var group = new MenuItem { Header = "Renderer" };
        var canvas = ActivePane.Canvas;
        var now = canvas.IsSceneOnGpu
            ? $"Drawn by {canvas.RendererAdapter}"
            : $"Drawn by the processor: {canvas.RendererReason}";
        group.Items.Add(new MenuItem { Header = now, IsEnabled = false });
        group.Items.Add(new Separator());

        var forced = Rendering.Gpu.GpuBootstrap.ExplicitPreference;
        var current = forced ?? _viewModel.Renderer;
        foreach (var (choice, name, tip) in new[]
        {
            (Rendering.Gpu.RendererPreference.Auto, "Automatic", "The graphics card wherever it can draw the canvas, the processor elsewhere"),
            (Rendering.Gpu.RendererPreference.Gpu, "Graphics card", "Always the graphics card, even where Windows draws in software"),
            (Rendering.Gpu.RendererPreference.Cpu, "Processor", "Always the processor: the canvas exactly as it was drawn before the graphics card did it")
        })
        {
            var item = new MenuItem
            {
                Header = name,
                IsCheckable = true,
                IsChecked = current == choice,
                IsEnabled = forced is null,
                ToolTip = forced is null ? tip : "Chosen for this run by --renderer or ULTRAEXPLORER_RENDERER"
            };
            var chosen = choice;
            item.Click += (_, _) => _viewModel.Renderer = chosen;
            group.Items.Add(item);
        }

        menu.Items.Add(group);
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

    private void LayoutButton_Click(object sender, RoutedEventArgs e) =>
        BuildCanvasOptionsMenu((UIElement)sender).IsOpen = true;

    /// <summary>Canvas options: the canvas's commands, the choices of how it looks and behaves, and the way to all of them in Settings.</summary>
    internal ContextMenu BuildCanvasOptionsMenu(UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        AddCommandItem(menu, "Fit all", "\uE9A6", _viewModel.FitAllCommand, "Shift+1");
        AddActionItem(menu, "Focus selected item", "\uE9A6", () => _ = FocusSelectionAsync(), "F");
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

        var layers = new MenuItem { Header = "Layers" };
        AddLayerItems(layers, includeMinimap: false);
        menu.Items.Add(layers);
        AddSortItems(menu, SortFolder());
        AddArrangeItems(menu);
        if (IsNested)
        {
            AddLeftDragItems(menu);
            AddRendererItems(menu);
        }

        menu.Items.Add(new Separator());
        AddColourItems(menu);
        AddCommandItem(menu, "Note…", "\uE70B", _viewModel.EditNoteCommand);
        AddCommandItem(menu, "Pin to Home", "\uE718", _viewModel.AddToFavoritesCommand);
        AddLayoutItems(menu);
        if (!IsPickerMode)
        {
            var split = new MenuItem { Header = "Split view", InputGestureText = "Ctrl+\\" };
            AddSplitItems(split);
            menu.Items.Add(split);
        }

        menu.Items.Add(new Separator());
        AddCommandItem(menu, "Settings…", "\uE713", OpenSettingsCommand, "Ctrl+,");
        return menu;
    }

    private void OverflowButton_Click(object sender, RoutedEventArgs e) =>
        BuildOverflowMenu((UIElement)sender).IsOpen = true;

    /// <summary>The More menu: Settings first, where Windows' own apps keep it, then the commands with no button of their own.</summary>
    internal ContextMenu BuildOverflowMenu(UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        AddCommandItem(menu, "Settings", "\uE713", OpenSettingsCommand, "Ctrl+,");
        menu.Items.Add(new Separator());
        AddCommandItem(menu, "Pin to Home", "\uE718", _viewModel.AddToFavoritesCommand);
        AddCommandItem(menu, "Show in File Explorer", "\uEC50", _viewModel.ShowInExplorerCommand);
        AddCommandItem(menu, "Refresh", "\uE72C", _viewModel.RefreshCommand, "F5");
        menu.Items.Add(new Separator());
        AddCommandItem(menu, "Properties", "\uE946", _viewModel.PropertiesCommand, "Alt+Enter");
        return menu;
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

    /// <summary>A switch that runs a command, showing whether it is on; handed back for anything more it needs.</summary>
    private static MenuItem AddCheckableItem(ItemsControl menu, string header, bool isChecked, ICommand command)
    {
        var item = new MenuItem
        {
            Header = header,
            IsCheckable = true,
            IsChecked = isChecked,
            Command = command
        };

        menu.Items.Add(item);
        return item;
    }

    /// <summary>An item that runs <paramref name="action"/>, with a glyph as the command items have.</summary>
    private static void AddActionItem(ItemsControl menu, string header, string glyph, Action action, string gesture = "") =>
        AddCommandItem(menu, header, glyph, new Infrastructure.RelayCommand(action), gesture);

    private static Brush MenuGlyphBrush =>
        Application.Current?.TryFindResource("TextBrush") as Brush ?? Brushes.White;

    private static void AddCommandItem(ItemsControl menu, string header, string glyph, ICommand command, string gesture = "")
    {
        var item = new MenuItem
        {
            Header = header,
            InputGestureText = gesture,
            Command = command,
            Icon = GlyphIcon(glyph)
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

        // A press in the list of folders behind a crumb's chevron comes here
        // too: the list is in a popup, and what is pressed in a popup goes on
        // through it to the crumb it belongs to.  It is a press on that list,
        // not on the bar, and it opened the line under the open list.
        var pressed = e.OriginalSource as DependencyObject;
        while (pressed is not null and not Visual)
        {
            pressed = ParentOf(pressed);
        }

        if (pressed is null || !AddressArea.IsAncestorOf(pressed))
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

    /// <summary>
    /// A path longer than the bar has room for keeps its end in view - the
    /// folder the window is in, and the ones just above it - as Explorer's
    /// bar does.  The strip has no scroll bar and no wheel, and stayed at its
    /// start, so the drive and the first folders showed and the folder the
    /// user was in was cut off.  New crumbs, and a bar made narrower or
    /// wider, go back to the end; a path that fits is shown from its start
    /// as before.  The lists behind the chevrons scroll on their own, and
    /// their scrolling comes up through here too: it is left alone.
    /// </summary>
    private void CrumbStrip_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender)
            && (e.ExtentWidthChange != 0 || e.ViewportWidthChange != 0))
        {
            ((ScrollViewer)sender).ScrollToRightEnd();
        }
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

    /// <summary>
    /// The search box keeps the keyboard while its results are up: the
    /// arrows go through the results, Enter shows the chosen one on the
    /// canvas and Ctrl+Enter (or Shift+Enter) opens it, and Escape puts the
    /// search away.  Everything else is typing.
    /// </summary>
    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var search = _viewModel.Search;
        var modifiers = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Down when search.IsOpen:
                search.MoveSelection(1);
                break;
            case Key.Up when search.IsOpen:
                search.MoveSelection(-1);
                break;
            case Key.PageDown when search.IsOpen:
                search.MoveSelection(8);
                break;
            case Key.PageUp when search.IsOpen:
                search.MoveSelection(-8);
                break;
            case Key.Enter when (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0:
                search.OpenSelected();
                break;
            case Key.Enter:
                if (!search.IsOpen || search.Results.Count == 0)
                {
                    search.SearchNow();
                }
                else
                {
                    _ = search.RevealSelectedAsync();
                }

                break;
            case Key.Escape:
                search.Close();
                FocusCanvas();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResultsList.SelectedItem is { } selected)
        {
            SearchResultsList.ScrollIntoView(selected);
        }
    }

    /// <summary>A click on a result shows it on the canvas; the results stay up.</summary>
    private void SearchResults_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (SearchResultUnder(e) is { } result)
        {
            _ = _viewModel.Search.RevealAsync(result);
        }
    }

    /// <summary>A double-click opens it: a file in its program, a folder by going into it.</summary>
    private void SearchResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchResultUnder(e) is { } result)
        {
            _viewModel.Search.OpenCommand.Execute(result);
            e.Handled = true;
        }
    }

    /// <summary>
    /// A right-click is the Shell's menu for the result, as it would be
    /// anywhere else.  Where the Shell has none - the result was deleted or
    /// moved since the search - there is no menu at all: the app's own would
    /// act on the canvas's selection, which is not what was clicked, and its
    /// Delete would recycle the wrong items.
    /// </summary>
    private void SearchResults_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (SearchResultUnder(e) is not { } result)
        {
            return;
        }

        e.Handled = true;
        _viewModel.Search.Selected = result;
        if (!ShowContextMenu([result.FullPath], SearchResultsList, e.GetPosition(SearchResultsList), includeCanvasCommands: false, fallBack: false)
            && !Path.Exists(result.FullPath))
        {
            _viewModel.Toast.ShowError($"{result.Name} is no longer there.");
        }
    }

    private static SearchResultViewModel? SearchResultUnder(MouseEventArgs e)
    {
        // Highlighted text raises mouse events from Run/Span content elements,
        // whose parents belong to the logical tree rather than the visual tree.
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = ParentOf(element))
        {
            if (element is ListBoxItem { DataContext: SearchResultViewModel result })
            {
                return result;
            }
        }

        return null;
    }

    /// <summary>The panel's left edge widens it, as far as the canvas allows.</summary>
    private void SearchPanelEdge_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        var room = SearchPanel.Parent is FrameworkElement area ? area.ActualWidth - 28 : 1200;
        SearchPanel.Width = Math.Clamp(SearchPanel.Width - e.HorizontalChange, SearchPanel.MinWidth, Math.Max(SearchPanel.MinWidth, room));
    }

    // ---- Keyboard ----------------------------------------------------------

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;

        // WPF reports F10 and every Alt combination as Key.System with the real
        // key in SystemKey, so reading e.Key alone silently drops Alt+Left,
        // Alt+Up, Alt+Enter and Shift+F10.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // A key calls off a rename the folder list is still waiting to start:
        // it is not what was typed next that should go into its dialog.
        _folderListRenameTimer.Stop();

        // Whatever the key, it is for the pane the keyboard is in.
        FollowKeyboardToPane();

        if (TryQuickPreviewKey(key, modifiers))
        {
            e.Handled = true;
            return;
        }

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

        // Before the text boxes keep their keys: a comma typed with Ctrl is
        // nothing a text box does anything with.
        if (TryOpenSettingsFromKey(key, modifiers))
        {
            e.Handled = true;
            return;
        }

        // The split's keys too: F6 from the filter box goes to the other pane
        // as it does from a canvas.
        if (TryHandleSplitKey(key, modifiers))
        {
            e.Handled = true;
            return;
        }

        if (IsFocusTextInput(Keyboard.FocusedElement as DependencyObject)
            || IsFocusTextInput(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (TryHandleFocusSelectionKey(key, modifiers, e.IsRepeat,
                e.OriginalSource as DependencyObject, () => e.Handled = true))
            return;

        if (TryBeginSpacePreview(key, modifiers, e.IsRepeat,
                e.OriginalSource as DependencyObject, () => e.Handled = true))
            return;

        // The nested canvas moves between cells with the arrows and goes in and
        // out with Enter and Backspace; the tree's meanings for those keys
        // (open, expand, collapse) do not exist there.
        if (TryHandleNestedKey(key, modifiers))
        {
            e.Handled = true;
            return;
        }

        // The keys that act on the selection only do so from where the
        // selection is shown - either canvas, the folder list, or nowhere in
        // particular.  Anything else holding the keyboard has keys of its own:
        // a dialog's buttons take Enter and Space, the search box takes Enter
        // for its results, and Delete typed there must never reach a selection
        // the user is not even looking at.  (The window's own buttons never
        // take the keyboard, so a click on one leaves these keys working.)
        // Going places, zooming and refreshing work from anywhere, as they
        // always have.
        var onSelection = IsSelectionSurfaceFocused()
            && (!FolderListItems.IsKeyboardFocusWithin || _viewModel.Tree.FolderList.HasCurrentRows);

        // Shift+F5 and Shift+F6: what is selected, to the other pane.
        if (onSelection && TryHandlePaneTransferKey(key, modifiers))
        {
            e.Handled = true;
            return;
        }

        switch (modifiers, key)
        {
            case (ModifierKeys.Control, Key.H) when onSelection:
                _viewModel.HideSelectedCommand.Execute(null);
                break;

            case (ModifierKeys.Control | ModifierKeys.Shift, Key.H):
                _viewModel.ShowAllHiddenCommand.Execute(null);
                break;

            case (ModifierKeys.Control, Key.C) when onSelection:
                _viewModel.CopyCommand.Execute(null);
                break;
            case (ModifierKeys.Control, Key.X) when onSelection:
                _viewModel.CutCommand.Execute(null);
                break;

            // Paste goes into the folder selected on the canvas, which is not
            // where a search result or a button is.
            case (ModifierKeys.Control, Key.V) when onSelection:
                _viewModel.PasteCommand.Execute(null);
                break;
            case (ModifierKeys.Control | ModifierKeys.Shift, Key.C) when onSelection:
                _viewModel.CopyPathCommand.Execute(null);
                break;
            case (ModifierKeys.Control | ModifierKeys.Shift, Key.N):
                _viewModel.NewFolderCommand.Execute(null);
                break;
            case (ModifierKeys.Control | ModifierKeys.Shift, Key.D) when onSelection:
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
            case (ModifierKeys.Alt, Key.Enter) when onSelection:
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
            // The Menu key is the other way Windows has always had to ask for
            // the menu of what has the keyboard.
            case (ModifierKeys.Shift, Key.F10) when onSelection:
            case (ModifierKeys.None, Key.Apps) when onSelection:
                ShowContextMenuForSelection();
                break;
            case (ModifierKeys.None, Key.F2) when onSelection:
                _viewModel.RenameCommand.Execute(null);
                break;
            case (ModifierKeys.None, Key.F5):
                _viewModel.RefreshCommand.Execute(null);
                break;
            case (ModifierKeys.None, Key.Delete) when onSelection:
                _viewModel.DeleteCommand.Execute(null);
                break;
            case (ModifierKeys.Shift, Key.Delete) when onSelection:
                _viewModel.PermanentDeleteCommand.Execute(null);
                break;
            case (ModifierKeys.None, Key.Enter) when onSelection:
                _viewModel.OpenCommand.Execute(null);
                break;

            // Claimed before the wait, not after it: an async handler goes back
            // to its caller at the first await, and a key still unhandled then
            // goes on to Nodify, which moves the selection with it as well.
            case (ModifierKeys.None, Key.Right) when onSelection && _viewModel.Tree.ActiveNode is { IsDirectory: true, IsExpanded: false } expand:
                e.Handled = true;
                await ToggleFromKeyboardAsync(expand);
                return;
            case (ModifierKeys.None, Key.Left) when onSelection && _viewModel.Tree.ActiveNode is { IsDirectory: true, IsExpanded: true } collapse:
                e.Handled = true;
                await ToggleFromKeyboardAsync(collapse);
                return;
            case (ModifierKeys.None, Key.Escape):
                // A crumb's list of folders is the nearest thing to a menu the
                // bar has, and Escape closes menus.
                // The search's results stay up while the canvas is used; its
                // own box, or its close button, puts them away.
                foreach (var crumb in _viewModel.Address.Breadcrumbs)
                {
                    crumb.IsMenuOpen = false;
                }

                return;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Whether the keyboard is on something that shows the one selection -
    /// either canvas or the folder list - or on nothing in particular, which
    /// is the window itself.  A search result, a button, a drop-down: each of
    /// those is a choice of its own, and not what the selection's keys are for.
    /// </summary>
    private bool IsSelectionSurfaceFocused()
    {
        var focused = Keyboard.FocusedElement;
        return focused is null
            || ReferenceEquals(focused, this)
            || Editor.IsKeyboardFocusWithin
            || IsNestedCanvasFocused()
            || FolderListItems.IsKeyboardFocusWithin;
    }

    /// <summary>
    /// Opens or closes a folder from the arrow keys.  Nothing above an async
    /// key handler would catch a folder that could not be read, and an
    /// exception there ends the program, so the failure is said instead.
    /// </summary>
    private async Task ToggleFromKeyboardAsync(ViewAllNodeViewModel node)
    {
        try
        {
            await _viewModel.Tree.ToggleAsync(node);
        }
        catch (Exception exception)
        {
            _viewModel.Toast.ShowError(exception.Message);
        }
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
        CancelSpacePreview(disarm: true);
        if (_isSpacePanning)
        {
            Editor.EndPanning();
            Editor.ReleaseMouseCapture();
        }
        // Alt+Tab while space is held would otherwise leave the grab hand on.
        _isSpacePanning = false;
        SetSpacePanArmed(false);

        // Nor does a rename the folder list was about to start open its
        // dialog over the program the user has gone to.
        _folderListRenameTimer.Stop();

        // Going to another program puts the crumbs back, as Explorer does.
        _viewModel.Address.EndEdit();
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Space)
        {
            var owned = _spacePreviewGesture.IsPressed;
            var path = FinishSpacePreview(Keyboard.Modifiers, e.OriginalSource as DependencyObject);
            if (owned) e.Handled = true;
            if (path is not null) OpenQuickPreview(path);
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
            var pane = ActivePane;
            var canvas = pane.Canvas;
            var at = new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2);
            if (pane.Tree.Find(_viewModel.Tree.ActivePath) is { } folder && canvas.ScreenRectOf(folder) is { } rect)
            {
                var visible = Rect.Intersect(rect, new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight));
                if (!visible.IsEmpty)
                {
                    at = new Point(visible.X + visible.Width / 2, visible.Y + Math.Min(visible.Height / 2, 24));
                }
            }

            ShowContextMenu(paths, canvas, at);
            return;
        }

        var anchor = _viewModel.Tree.ActiveNode is { } node
            && Editor.ItemContainerGenerator.ContainerFromItem(node) is FrameworkElement container
                ? container
                : Editor;
        ShowContextMenu(paths, anchor, new Point(anchor.ActualWidth / 2, anchor.ActualHeight / 2));
    }

    // ---- Dialogs -----------------------------------------------------------

    private string? ShowInputDialog(string title, string prompt, string initialValue, bool selectStem)
    {
        var dialog = new InputDialog(title, prompt, initialValue, selectStem,
            password: title.Equals("Password", StringComparison.Ordinal)) { Owner = this };
        return dialog.ShowOwnerModal() ? dialog.Value : null;
    }

    private bool ShowConfirmDialog(string title, string message, string confirmLabel)
    {
        var dialog = new ConfirmDialog(title, message, confirmLabel, danger: confirmLabel == "Delete") { Owner = this };
        return dialog.ShowOwnerModal();
    }

    // ---- Win32 -------------------------------------------------------------

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

        // The point (0, 0) is on the primary monitor, by definition.
        var primary = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromPoint(default, MonitorDefaultToPrimary), ref primary))
        {
            primary = info;
        }

        var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        FitMaximizedBounds(ref bounds, info, primary.Work);
        Marshal.StructureToPtr(bounds, lParam, false);
    }

    /// <summary>
    /// What <see cref="ClampMaximizedBounds"/> writes for a window maximized on
    /// the monitor <paramref name="target"/> describes, the primary monitor's
    /// work area being <paramref name="primaryWork"/>.
    ///
    /// <para>Windows takes the maximized size as meant for the primary monitor,
    /// and on any other one grows a size at least as large as the primary by
    /// the difference between the two monitors.  So on a monitor larger than
    /// the primary - a 4K screen beside a 1080p one - the work area written
    /// here grew by that difference, and the caption buttons, the right end of
    /// the toolbar and the status bar were laid out past the edges.  No size
    /// written here can come out as the work area there, but a maximized
    /// window is never made larger than its tracking size, so that is held to
    /// the work area.  Only there: the work area is compared with the
    /// primary's work area, the smaller of what Windows may compare it with,
    /// and a window on the primary monitor, or on a smaller one, may still be
    /// resized across monitors as before.</para>
    /// </summary>
    internal static void FitMaximizedBounds(ref MinMaxInfo bounds, MonitorInfo target, RectL primaryWork)
    {
        bounds.MaxPosition = new PointL
        {
            X = target.Work.Left - target.Monitor.Left,
            Y = target.Work.Top - target.Monitor.Top
        };
        bounds.MaxSize = new PointL
        {
            X = target.Work.Right - target.Work.Left,
            Y = target.Work.Bottom - target.Work.Top
        };

        if ((target.Flags & MonitorInfoPrimary) == 0
            && bounds.MaxSize.X >= primaryWork.Right - primaryWork.Left
            && bounds.MaxSize.Y >= primaryWork.Bottom - primaryWork.Top)
        {
            bounds.MaxTrackSize = new PointL
            {
                X = Math.Min(bounds.MaxTrackSize.X, bounds.MaxSize.X),
                Y = Math.Min(bounds.MaxTrackSize.Y, bounds.MaxSize.Y)
            };
        }
    }

    /// <summary>
    /// Keeps a window that is about to be shown for the first time wholly on
    /// the monitor it opens on, wherever it was put.  It is 1520 by 900 and
    /// centred on the work area whether or not that fits: on a 1080p screen
    /// at 125 % the 900 is 1125 pixels, and the top went 46 pixels above the
    /// screen, with the caption buttons and the strip the window is dragged
    /// by.  On a small screen at 150 % even the minimum height, 620, is
    /// taller than the screen, and a picker's OK and Cancel were below its
    /// edge.  So the minimum is lowered to what the work area holds, the
    /// window made no larger than the work area, and moved inside it by as
    /// little as it takes; a window that fits is left where it was put.
    /// </summary>
    private void FitIntoWorkArea(IntPtr window)
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) || !GetWindowRect(window, out var bounds))
        {
            return;
        }

        var work = info.Work;
        var dpi = VisualTreeHelper.GetDpi(this);
        var widest = Math.Floor((work.Right - work.Left) / dpi.DpiScaleX);
        var tallest = Math.Floor((work.Bottom - work.Top) / dpi.DpiScaleY);
        if (MinWidth > widest)
        {
            MinWidth = widest;
        }

        if (MinHeight > tallest)
        {
            MinHeight = tallest;
        }

        var width = Math.Min(bounds.Right - bounds.Left, work.Right - work.Left);
        var height = Math.Min(bounds.Bottom - bounds.Top, work.Bottom - work.Top);
        var left = Math.Clamp(bounds.Left, work.Left, work.Right - width);
        var top = Math.Clamp(bounds.Top, work.Top, work.Bottom - height);
        if (WindowStartupLocation == WindowStartupLocation.CenterScreen)
        {
            (left, top) = CentreInWorkArea(window, work, left, top, width, height, dpi.DpiScaleY);
        }

        if (left != bounds.Left || top != bounds.Top || width != bounds.Right - bounds.Left || height != bounds.Bottom - bounds.Top)
        {
            SetWindowPos(window, IntPtr.Zero, left, top, width, height, SwpNoZOrder | SwpNoActivate);
        }
    }

    /// <summary>
    /// Where a window that opens in the middle of the screen goes, being
    /// <paramref name="width"/> by <paramref name="height"/> on the work area
    /// <paramref name="work"/>, from where it is (<paramref name="left"/>,
    /// <paramref name="top"/>).  WPF centres it at the scale it was made at -
    /// the primary monitor's - before it moves over to its own monitor and
    /// takes that monitor's scale: on a 100 % monitor beside a 150 % primary
    /// it was centred at one and a half times its size, shrank, and was
    /// pushed into the monitor's corner.  So it is centred again at the size
    /// it has now; one that WPF centred already, within its rounding, stays
    /// exactly where it is.  And another window of the app in that very spot
    /// - several folders opened together each open one - moves it a caption
    /// lower and further right, as many times as it takes and the work area
    /// allows: exactly over the other, the windows looked like one.
    /// </summary>
    private (int Left, int Top) CentreInWorkArea(IntPtr window, RectL work, int left, int top, int width, int height, double scale)
    {
        var centredLeft = work.Left + (work.Right - work.Left - width) / 2;
        var centredTop = work.Top + (work.Bottom - work.Top - height) / 2;
        if (Math.Abs(left - centredLeft) > 1 || Math.Abs(top - centredTop) > 1)
        {
            left = centredLeft;
            top = centredTop;
        }

        var step = (int)Math.Round((System.Windows.Shell.WindowChrome.GetWindowChrome(this)?.CaptionHeight ?? 40) * scale);
        while (step > 0 && left + step + width <= work.Right && top + step + height <= work.Bottom
            && IsAnotherWindowAt(window, left, top))
        {
            left += step;
            top += step;
        }

        return (left, top);
    }

    /// <summary>
    /// Whether another window of the app that is shown, neither maximized nor
    /// minimized and not on its way out, has its top left corner at
    /// (<paramref name="left"/>, <paramref name="top"/>).  Only the app's own
    /// thread lists its windows: a window made on another one compares with
    /// none.
    /// </summary>
    private static bool IsAnotherWindowAt(IntPtr window, int left, int top) =>
        Application.Current is { } application
        && application.CheckAccess()
        && application.Windows.OfType<MainWindow>().Any(other =>
            other.IsVisible
            && !other._closeRequested
            && other.WindowState == WindowState.Normal
            && new WindowInteropHelper(other).Handle is var handle
            && handle != IntPtr.Zero
            && handle != window
            && GetWindowRect(handle, out var bounds)
            && Math.Abs(bounds.Left - left) <= 1
            && Math.Abs(bounds.Top - top) <= 1);

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

    private const int MonitorDefaultToPrimary = 0x00000001;
    private const int MonitorDefaultToNearest = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RectL
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PointL
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MinMaxInfo
    {
        public PointL Reserved;
        public PointL MaxSize;
        public PointL MaxPosition;
        public PointL MinTrackSize;
        public PointL MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
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

            current = ParentOf(current);
        }

        return null;
    }

    /// <summary>What <paramref name="element"/> is in: its visual parent, or for text and other content, its logical one.</summary>
    internal static DependencyObject? ParentOf(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);
}
