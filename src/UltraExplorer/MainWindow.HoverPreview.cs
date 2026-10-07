using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace UltraExplorer;

public partial class MainWindow
{
    private FileThumbnailService? _thumbnails;
    private HoverPreviewController _hoverPreview = null!;
    private HoverPreviewCard _previewCard = null!;
    private Canvas _previewOverlay = null!;
    private NestedCanvas? _previewCanvas;
    private NestedTree? _previewTree;
    private Point _previewPointer;
    private bool _previewDetached;
    private long _previewGeneration;
    private QuickPreviewWindow? _quickPreview;
    private bool _quickPreviewClosingOwner;

    internal void OpenQuickPreview(string path, bool readOnly = false)
    {
        if (_previewDetached || _closeRequested) return;
        if (!readOnly && TryBeginArchiveQuickPreview(path, PresentPhysicalQuickPreview)) return;
        CancelArchiveQuickPreview();
        PresentPhysicalQuickPreview(path, readOnly, null);
    }

    internal QuickPreviewWindow? QuickPreviewForChecks => _quickPreview;
    internal bool SuppressQuickPreviewPresentationForChecks { get; set; }

    private void PresentPhysicalQuickPreview(string path, bool readOnly, string? originPath)
    {
        if (_previewDetached || _closeRequested || _viewModel.IsDisposed) return;
        ClearHoverPreview();
        if (_quickPreview is null)
        {
            var preview = new QuickPreviewWindow { Owner = this };
            _quickPreview = preview;
            preview.Closed += (_, _) =>
            {
                CancelArchiveQuickPreview();
                if (ReferenceEquals(_quickPreview, preview)) _quickPreview = null;
            };
            _quickPreview.SetFileSequence(readOnly ? [path] : PreviewSiblingFiles(path));
            _quickPreview.OpenFile(path, readOnly, originPath);
            if (!IsTestWindow || !SuppressQuickPreviewPresentationForChecks) _quickPreview.Show();
        }
        else
        {
            _quickPreview.SetFileSequence(readOnly ? [path] : PreviewSiblingFiles(path));
            _quickPreview.OpenFile(path, readOnly, originPath);
            if (!IsTestWindow || !SuppressQuickPreviewPresentationForChecks) _quickPreview.Activate();
        }
    }

    private IReadOnlyList<string> PreviewSiblingFiles(string path)
    {
        var parent = System.IO.Path.GetDirectoryName(path);
        var list = _viewModel.Tree.FolderList;
        if (list.HasCurrentRows && string.Equals(parent, list.FolderPath, StringComparison.OrdinalIgnoreCase))
            return FolderListItems.Items.OfType<FolderListItem>().Where(item => !item.IsDirectory).Select(item => item.FullPath).ToArray();
        if (IsNested && parent is not null && ActivePane.Tree.Find(parent) is { } folder)
            return folder.Files.Select(file => System.IO.Path.Combine(folder.FullPath, file.Name)).ToArray();
        if (_viewModel.Tree.TryGetNode(path, out var node) && node.Parent is { } parentNode)
            return parentNode.Children.Where(child => child.IsFile && child.IsTreeVisible).Select(child => child.FullPath).ToArray();
        return [path];
    }

    private bool TryQuickPreviewKey(Key key, ModifierKeys modifiers)
    {
        if (key != Key.Space || modifiers != ModifierKeys.Control || Keyboard.FocusedElement is TextBox) return false;
        var target = _hoverPreview.Path;
        if (target is null && IsSelectionSurfaceFocused())
        {
            var selection = _viewModel.Tree.Selection;
            target = selection.Focus ?? _viewModel.Tree.SelectedPaths.FirstOrDefault();
            if (target is not null && selection.TryGetItem(target, out var item) && item.IsDirectory) target = null;
        }
        if (target is null) return false;
        OpenQuickPreview(target);
        return true;
    }

    private void AttachHoverPreviews()
    {
        _previewOverlay = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        Grid.SetRowSpan(_previewOverlay, ((Grid)Content).RowDefinitions.Count);
        Panel.SetZIndex(_previewOverlay, 100);
        _previewCard = new HoverPreviewCard();
        _previewOverlay.Children.Add(_previewCard);
        ((Grid)Content).Children.Add(_previewOverlay);
        _hoverPreview = new HoverPreviewController(Dispatcher,
            (path, token) => (_thumbnails ??= new FileThumbnailService()).GetAsync(path, token));
        _hoverPreview.Changed += RefreshHoverPreview;
        AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(PreviewHoverMove), handledEventsToo: true);
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(PreviewHoverPress), handledEventsToo: true);
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(PreviewHoverRelease), handledEventsToo: true);
        AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(PreviewHoverWheel), handledEventsToo: true);
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(PreviewHoverKey), handledEventsToo: true);
        AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(SpacePreviewFocusLost), handledEventsToo: true);
        AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(SpacePreviewCaptureLost), handledEventsToo: true);
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(PreviewHoverScroll), handledEventsToo: true);
        AddHandler(ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler(PreviewHoverMenu), handledEventsToo: true);
        AddHandler(ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler(PreviewHoverTooltip), handledEventsToo: true);
        AddHandler(DragDrop.PreviewDragOverEvent, new DragEventHandler(PreviewHoverDrag), handledEventsToo: true);
        MouseLeave += PreviewHoverLeave;
        Deactivated += PreviewHoverDeactivate;
        SizeChanged += PreviewHoverResize;
        _viewModel.PropertyChanged += PreviewPreferenceChanged;
        _viewModel.Tree.FolderList.PropertyChanged += PreviewListChanged;
        _viewModel.Tree.FolderList.Items.CollectionChanged += PreviewListItemsChanged;
        _viewModel.Tree.PathRefreshed += PreviewPathRefreshed;
        Closed += PreviewHoverClosed;
        Closing += PreviewHoverClosing;
    }

    private void PreviewHoverMove(object sender, MouseEventArgs args)
    {
        if (_previewDetached) return;
        if (HandleSpacePointerMove(args)) { args.Handled = true; ClearHoverPreview(); return; }
        if (!_viewModel.ShowHoverPreviews || _closeRequested || args.LeftButton == MouseButtonState.Pressed
            || args.MiddleButton == MouseButtonState.Pressed || args.RightButton == MouseButtonState.Pressed
            || Mouse.Captured is not null || _isSpaceHeld || _isSpacePanning)
        {
            ClearHoverPreview();
            return;
        }

        var source = args.OriginalSource as DependencyObject;
        // The canvas's own visual hit rules include labels drawn over smaller folders.
        if (FindAncestor<NestedCanvas>(source) is { } canvas)
        {
            var hit = canvas.HoverPreviewHit(args.GetPosition(canvas));
            SetHoverPreviewTarget(hit is { IsFile: true } file ? file.Path : null,
                args.GetPosition(_previewOverlay), canvas);
            return;
        }

        if (FindAncestor<ListBoxItem>(source) is { DataContext: FolderListItem row }
            && FindAncestor<SelectionListBox>(source) == FolderListItems)
        {
            SetHoverPreviewTarget(HoverPreviewListTarget(_viewModel.Tree.FolderList, row), args.GetPosition(_previewOverlay));
            return;
        }

        if (FindAncestor<Nodify.NodifyEditor>(source) == Editor && !IsNested)
        {
            var node = _viewModel.Tree.HitTest(Editor.GetLocationInsideEditor(args));
            SetHoverPreviewTarget(node is { IsFile: true } ? node.FullPath : null, args.GetPosition(_previewOverlay));
            return;
        }

        ClearHoverPreview();
    }

    internal void SetHoverPreviewTarget(string? path, Point pointer, NestedCanvas? canvas = null)
    {
        if (_previewDetached || !_viewModel.ShowHoverPreviews || _closeRequested) path = null;
        var pathChanged = !string.Equals(_hoverPreview.Path, path, StringComparison.Ordinal);
        var canvasChanged = !ReferenceEquals(_previewCanvas, path is null ? null : canvas);
        if (pathChanged || canvasChanged)
        {
            _previewPointer = pointer;
            SetPreviewCanvas(path is null ? null : canvas);
        }
        _hoverPreview.Hover(path);
        if (!pathChanged && canvasChanged) RefreshHoverPreview();
    }

    private void SetPreviewCanvas(NestedCanvas? canvas)
    {
        if (_previewCanvas == canvas) return;
        if (_previewCanvas is { } old)
        {
            old.SuppressFileHoverTip = false;
            old.CameraChanged -= ClearHoverPreview;
            old.Unloaded -= PreviewHoverUnloaded;
        }
        if (_previewTree is { } oldTree) oldTree.Changed -= PreviewTreeChanged;
        _previewCanvas = canvas;
        _previewTree = canvas?.Tree;
        if (canvas is not null)
        {
            canvas.CameraChanged += ClearHoverPreview;
            canvas.Unloaded += PreviewHoverUnloaded;
            if (_previewTree is { } tree) tree.Changed += PreviewTreeChanged;
        }
    }

    private void RefreshHoverPreview()
    {
        if (_previewCanvas is { } canvas) canvas.SuppressFileHoverTip = _hoverPreview.IsLoading || _hoverPreview.Result is not null;
        if (_hoverPreview.Path is not { } path || !_viewModel.ShowHoverPreviews
            || (!_hoverPreview.IsLoading && _hoverPreview.Result is null))
        {
            _previewCard.Hide();
            return;
        }

        var size = new Size(_previewOverlay.ActualWidth, _previewOverlay.ActualHeight);
        if (size.Width < 150 || size.Height < 150) return;
        _previewCard.Show(path, _hoverPreview.Result, size.Width, size.Height);
        var position = HoverPreviewCard.Place(_previewPointer, _previewCard.DesiredSize, size);
        Canvas.SetLeft(_previewCard, position.X);
        Canvas.SetTop(_previewCard, position.Y);
    }

    private void ClearHoverPreview()
    {
        _previewGeneration++;
        SetPreviewCanvas(null);
        _hoverPreview.Clear();
    }

    internal static string? HoverPreviewListTarget(FolderListViewModel list, FolderListItem row) =>
        list.IsCurrentRow(row) && !row.IsDirectory ? row.FullPath : null;

    private void PreviewPreferenceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainViewModel.Layout) or nameof(MainViewModel.IsSplit)
            or nameof(MainViewModel.SplitOrientation) or nameof(MainViewModel.SplitRatio)) CancelSpacePreview(disarm: true);
        if (args.PropertyName is nameof(MainViewModel.ShowHoverPreviews) or nameof(MainViewModel.Layout)
            or nameof(MainViewModel.Layers) or nameof(MainViewModel.IsSplit)
            or nameof(MainViewModel.SplitOrientation) or nameof(MainViewModel.SplitRatio)) ClearHoverPreview();
    }

    private void PreviewListChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(FolderListViewModel.IsVisible) or nameof(FolderListViewModel.FolderPath)) ClearHoverPreview();
    }

    private void PreviewPathRefreshed(string path)
    {
        if (_hoverPreview.Path is { } file && (string.Equals(file, path, StringComparison.OrdinalIgnoreCase)
            || file.StartsWith(path.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase))) ClearHoverPreview();
    }

    private void PreviewHoverPress(object sender, MouseButtonEventArgs args) { StopSpacePointerPan(); CancelSpacePreview(); ClearHoverPreview(); }
    private void PreviewHoverRelease(object sender, MouseButtonEventArgs args)
    {
        // After a click, holding still over the selected item is a hover too.
        // Wait until the item's own release has finished selection/navigation.
        QueueHoverPreviewAfterRelease(args.ChangedButton, () =>
        {
            if (!IsMouseOver) return;
            PreviewHoverMove(this, new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = Mouse.PreviewMouseMoveEvent, Source = Mouse.DirectlyOver
            });
        });
    }

    /// <summary>
    /// Only an ordinary left release may resume a stationary hover. A right
    /// release is opening a native menu, whose nested message pump can run
    /// dispatcher work before the menu returns. Every clear invalidates work
    /// queued before a camera/key/menu/deactivation/close change.
    /// </summary>
    internal void QueueHoverPreviewAfterRelease(MouseButton button, Action reevaluate)
    {
        if (button != MouseButton.Left || _previewDetached || _closeRequested || !_viewModel.ShowHoverPreviews) return;
        var generation = _previewGeneration;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (_previewDetached || _closeRequested || !_viewModel.ShowHoverPreviews || generation != _previewGeneration) return;
            reevaluate();
        });
    }
    private void PreviewHoverWheel(object sender, MouseWheelEventArgs args) { CancelSpacePreview(); ClearHoverPreview(); }
    private void PreviewHoverKey(object sender, KeyEventArgs args)
    {
        if ((args.Key == Key.System ? args.SystemKey : args.Key) != Key.Space) CancelSpacePreview();
        ClearHoverPreview();
    }
    private void PreviewHoverDrag(object sender, DragEventArgs args) { CancelSpacePreview(); ClearHoverPreview(); }
    private void PreviewHoverMenu(object sender, ContextMenuEventArgs args) { CancelSpacePreview(); ClearHoverPreview(); }
    private void PreviewHoverTooltip(object sender, ToolTipEventArgs args)
    {
        if (_hoverPreview.IsLoading || _hoverPreview.Result is not null) args.Handled = true;
    }
    private void PreviewHoverScroll(object sender, ScrollChangedEventArgs args)
    {
        if (args.HorizontalChange != 0 || args.VerticalChange != 0) ClearHoverPreview();
    }
    private void PreviewTreeChanged(object? sender, EventArgs args) => ClearHoverPreview();
    private void PreviewListItemsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_previewCanvas is null) ClearHoverPreview();
    }
    private void PreviewHoverLeave(object sender, MouseEventArgs args) => ClearHoverPreview();
    private void PreviewHoverUnloaded(object sender, RoutedEventArgs args) => ClearHoverPreview();
    private void PreviewHoverDeactivate(object? sender, EventArgs args) { CancelSpacePreview(disarm: true); ClearHoverPreview(); }
    private void PreviewHoverResize(object sender, SizeChangedEventArgs args) { CancelSpacePreview(); ClearHoverPreview(); }
    private void PreviewHoverClosing(object? sender, CancelEventArgs args) { CancelSpacePreview(disarm: true); ClearHoverPreview(); }

    private void PreviewHoverClosed(object? sender, EventArgs args)
    {
        _previewDetached = true;
        CancelSpacePreview(disarm: true);
        ClearHoverPreview();
        _hoverPreview.Dispose();
        _thumbnails?.Dispose();
        _viewModel.PropertyChanged -= PreviewPreferenceChanged;
        _viewModel.Tree.FolderList.PropertyChanged -= PreviewListChanged;
        _viewModel.Tree.FolderList.Items.CollectionChanged -= PreviewListItemsChanged;
        _viewModel.Tree.PathRefreshed -= PreviewPathRefreshed;
        RemoveHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(PreviewHoverMove));
        RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(PreviewHoverPress));
        RemoveHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(PreviewHoverRelease));
        RemoveHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(PreviewHoverWheel));
        RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(PreviewHoverKey));
        RemoveHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(SpacePreviewFocusLost));
        RemoveHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(SpacePreviewCaptureLost));
        RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(PreviewHoverScroll));
        RemoveHandler(ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler(PreviewHoverMenu));
        RemoveHandler(ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler(PreviewHoverTooltip));
        RemoveHandler(DragDrop.PreviewDragOverEvent, new DragEventHandler(PreviewHoverDrag));
    }

    internal HoverPreviewController HoverPreviewsForChecks => _hoverPreview;
    internal HoverPreviewCard PreviewCardForChecks => _previewCard;
}
