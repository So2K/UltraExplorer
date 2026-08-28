using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
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
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace UltraExplorer;

public partial class MainWindow : Window
{
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int WmNcLeftButtonUp = 0x00A2;
    private const int WmNcMouseLeave = 0x02A2;
    private const int HtMaxButton = 9;

    private static readonly Brush CaptionHoverBrush = CreateFrozenBrush(0x2D, 0x2D, 0x2D);

    private readonly MainViewModel _viewModel = new();
    private WindowCaptureService? _capture;
    private bool _allowClose;
    private bool _maximizeHover;
    private bool _sidebarCollapsed;
    private double _restoredSidebarWidth = 240;
    private bool _isSpaceHeld;
    private bool _isSpacePanning;
    private Point _panPointerAnchor;
    private Point _panViewportAnchor;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        ConfigureFigmaGestures();

        _viewModel.FitAllRequested += FitAll;
        _viewModel.ZoomRequested += ApplyZoom;
        _viewModel.PromptRequested += ShowInputDialog;
        _viewModel.ConfirmRequested += ShowConfirmDialog;
        _viewModel.ContextMenuRequested += ShowContextMenu;
        _viewModel.Tree.FocusNodeRequested += FocusNode;
        _viewModel.Tree.GraphInvalidated += OnGraphInvalidated;

        Overview.Index = _viewModel.Tree.SpatialIndex;
        if (TryFindResource("EdgeColor") is Color edgeColor)
        {
            Overview.SetEdgeColor(edgeColor);
        }

        StateChanged += (_, _) => MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
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
        var darkMode = 1;
        var cornerPreference = 2;
        _ = DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 33, ref cornerPreference, sizeof(int));
        HwndSource.FromHwnd(handle)?.AddHook(HandleWindowMessage);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();

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

        Editor.Focus();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            DependencyPropertyDescriptor
                .FromProperty(NodifyEditor.ViewportLocationProperty, typeof(NodifyEditor))
                .RemoveValueChanged(Editor, OnViewportLocationChanged);
            _capture?.Dispose();
            _viewModel.Dispose();
            return;
        }

        e.Cancel = true;
        _viewModel.SidebarWidth = SidebarColumn.ActualWidth > 0 ? SidebarColumn.ActualWidth : _restoredSidebarWidth;
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
    }

    private void Editor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isSpaceHeld)
        {
            return;
        }

        _isSpacePanning = true;
        _panPointerAnchor = e.GetPosition(Editor);
        _panViewportAnchor = Editor.ViewportLocation;
        Editor.Cursor = Cursors.SizeAll;
        Editor.CaptureMouse();
        e.Handled = true;
    }

    private void Editor_PreviewMouseMove(object sender, MouseEventArgs e)
    {
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
        if (!_isSpacePanning)
        {
            return;
        }

        _isSpacePanning = false;
        Editor.ReleaseMouseCapture();
        Editor.Cursor = _isSpaceHeld ? Cursors.Hand : null;
        e.Handled = true;
    }

    private void ZoomToSelection()
    {
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
        _viewModel.Tree.UpdateViewport(Editor.ViewportLocation, Editor.ViewportSize, Editor.ViewportZoom);

        // Cheap unless the viewport left the window the geometry was built for:
        // normally this just moves an already uploaded visual.
        Overview.Update(
            Editor.ViewportLocation,
            Editor.ViewportSize,
            Editor.ViewportZoom,
            _viewModel.Tree.DetailLevel);
    }

    private void OnGraphInvalidated()
    {
        Overview.InvalidateGeometry();
        Overview.Update(
            Editor.ViewportLocation,
            Editor.ViewportSize,
            Editor.ViewportZoom,
            _viewModel.Tree.DetailLevel);
    }

    private void FitAll()
    {
        Editor.FitToScreen(_viewModel.Tree.GetContentExtent());
        PushViewport();
    }

    private void ApplyZoom(double factor)
    {
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

    private void Editor_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != Editor)
        {
            return;
        }

        // Focus moves to the canvas rather than being cleared: with no focused
        // element WPF has nowhere to route key events, and every shortcut except
        // the Alt ones (which go through system-key handling) stops working.
        Editor.Focus();

        // While the overview is drawing, there are no node controls to click,
        // so hit-test the graph directly and keep selection working.
        if (_viewModel.Tree.IsOverviewActive
            && _viewModel.Tree.HitTest(Editor.GetLocationInsideEditor(e)) is { } node)
        {
            _viewModel.Tree.SelectOnly(node);
            e.Handled = true;
        }
    }

    private void Editor_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ViewAllNodeView>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        e.Handled = true;
        ShowCanvasMenu(e.GetPosition(Editor));
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

    private void ShowContextMenu(IReadOnlyList<string> paths, FrameworkElement origin, Point point)
    {
        if (paths.Count == 0)
        {
            return;
        }

        try
        {
            if (PresentationSource.FromVisual(this) is HwndSource source
                && NativeShellService.TryShowNativeContextMenu(
                    paths,
                    source,
                    origin.PointToScreen(point),
                    Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
            {
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
        AddColourItems(menu);
        AddCommandItem(menu, "Show in File Explorer", "\uEC50", _viewModel.ShowInExplorerCommand);
        menu.IsOpen = true;
    }

    private void ShowCanvasMenu(Point point)
    {
        var menu = new ContextMenu { PlacementTarget = Editor };
        AddCommandItem(menu, "New folder", "\uE8F4", _viewModel.NewFolderCommand, "Ctrl+Shift+N");
        AddCommandItem(menu, "New text file", "\uE8A5", _viewModel.NewTextFileCommand);
        AddCommandItem(menu, "Paste", "\uE77F", _viewModel.PasteCommand, "Ctrl+V");
        menu.Items.Add(new Separator());
        AddCommandItem(menu, "Fit all", "\uE9A6", _viewModel.FitAllCommand, "Shift+1");
        AddCommandItem(menu, "Reset zoom", "\uE71E", _viewModel.ResetZoomCommand, "Ctrl+0");
        AddCommandItem(menu, "Collapse every branch", "\uE72B", _viewModel.CollapseAllCommand);
        AddCommandItem(menu, "Minimap", "\uE81E", _viewModel.ToggleMinimapCommand);
        menu.IsOpen = true;
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
        AddCommandItem(menu, "Reset zoom", "\uE71E", _viewModel.ResetZoomCommand, "Ctrl+0");
        AddCommandItem(menu, "Collapse every branch", "\uE72B", _viewModel.CollapseAllCommand);
        AddCommandItem(menu, "Minimap", "\uE81E", _viewModel.ToggleMinimapCommand);
        AddCheckableItem(menu, "Hidden items", _viewModel.Tree.ShowHiddenItems, _viewModel.ToggleHiddenItemsCommand);
        menu.Items.Add(new Separator());
        AddColourItems(menu);
        AddCommandItem(menu, "Note…", "\uE70B", _viewModel.EditNoteCommand);
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

    private void AddColourItems(ItemsControl menu)
    {
        var colours = new (string Name, string Hex)[]
        {
            ("Default", string.Empty),
            ("Red", "#EF5A68"),
            ("Orange", "#F28A4B"),
            ("Yellow", "#E3B341"),
            ("Green", "#4ED6A0"),
            ("Cyan", "#4CC9D8"),
            ("Blue", "#60CDFF"),
            ("Violet", "#A979FF")
        };

        var parent = new MenuItem { Header = "Colour label" };
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

    private void AddressArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { TemplatedParent: Button })
        {
            return;
        }

        BeginAddressEdit();
    }

    private void BeginAddressEdit()
    {
        _viewModel.AddressText = _viewModel.Tree.ActivePath;
        _viewModel.IsAddressEditing = true;
        Dispatcher.InvokeAsync(() =>
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.GoToAddressCommand.Execute(null);
            _viewModel.IsAddressEditing = false;
            Editor.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel.IsAddressEditing = false;
            Editor.Focus();
            e.Handled = true;
        }
    }

    private void AddressBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => _viewModel.IsAddressEditing = false;

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
            Editor.Focus();
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

        if (modifiers == ModifierKeys.Control && key == Key.L)
        {
            BeginAddressEdit();
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

        switch (modifiers, key)
        {
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
            Editor.Focus();
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        // Alt+Tab while space is held would otherwise leave the grab hand on.
        _isSpacePanning = false;
        SetSpacePanArmed(false);
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
            case WmNcHitTest:
                if (IsOverMaximizeButton(lParam))
                {
                    SetMaximizeHover(true);
                    handled = true;
                    return new IntPtr(HtMaxButton);
                }

                SetMaximizeHover(false);
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
