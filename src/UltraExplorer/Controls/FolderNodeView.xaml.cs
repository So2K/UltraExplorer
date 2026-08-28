using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using UltraExplorer.Dialogs;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace UltraExplorer.Controls;

public partial class FolderNodeView : UserControl
{
    private Point _dragStart;
    private FileItemViewModel? _dragCandidate;
    private FileItemViewModel? _hoveredFolder;

    public FolderNodeView()
    {
        InitializeComponent();
    }

    private FolderNodeViewModel? Node => DataContext as FolderNodeViewModel;
    private MainViewModel? Main => Window.GetWindow(this)?.DataContext as MainViewModel;

    private async void ExpandButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Node is not null && Main is not null && (sender as FrameworkElement)?.DataContext is FileItemViewModel item)
        {
            await Main.ExpandItemAsync(Node, item, false);
        }
    }

    private async void ItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Node is null || Main is null || GetItemUnderPointer(e.OriginalSource as DependencyObject) is not { } item)
        {
            return;
        }

        e.Handled = true;
        await Main.ExpandItemAsync(Node, item, true);
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Node is null || Main is null)
        {
            return;
        }

        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Main.ClearFileSelectionExcept(Node);
        }

        Main.ActivateNode(Node, recordHistory: false, animated: false);
        Main.UpdateSelection();
    }

    private void ItemsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(ItemsList);
        _dragCandidate = GetItemUnderPointer(e.OriginalSource as DependencyObject);
    }

    private void ItemsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null || Main is null)
        {
            return;
        }

        var current = e.GetPosition(ItemsList);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (!_dragCandidate.IsSelected)
        {
            ItemsList.SelectedItems.Clear();
            _dragCandidate.IsSelected = true;
        }

        var paths = Main.GetSelectedFileItems().Select(item => item.FullPath).ToArray();
        if (paths.Length == 0)
        {
            paths = [_dragCandidate.FullPath];
        }

        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths);
        data.SetData(NativeShellService.InternalDragFormat, true);
        _dragCandidate = null;
        DragDrop.DoDragDrop(ItemsList, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
    }

    private void ItemsList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = GetItemUnderPointer(e.OriginalSource as DependencyObject);
        if (item is null || Main is null)
        {
            return;
        }

        e.Handled = true;
        if (!item.IsSelected)
        {
            ItemsList.SelectedItems.Clear();
            item.IsSelected = true;
        }

        Main.UpdateSelection();
        ShowShellContextMenu(Main.GetSelectedFileItems().Select(selected => selected.FullPath).ToArray(), e.GetPosition(this));
    }

    private void HeaderOrCard_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || Node is null)
        {
            return;
        }

        e.Handled = true;
        ShowShellContextMenu([Node.FullPath], e.GetPosition(this));
    }

    private void ShowShellContextMenu(IReadOnlyList<string> paths, Point localPoint)
    {
        var window = Window.GetWindow(this);
        if (window is null)
        {
            return;
        }

        try
        {
            var screenPoint = PointToScreen(localPoint);
            var handle = new WindowInteropHelper(window).Handle;
            NativeShellService.ShowNativeContextMenu(paths, screenPoint, handle, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }
        catch (Exception ex)
        {
            Main?.Toast.ShowError($"Windows context menu failed: {ex.Message}");
        }
    }

    private void NoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (Node is not null)
        {
            Main?.ToggleNodeNote(Node);
        }
    }

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || Node is null || Main is null)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = button };
        var colors = new (string Name, string Hex)[]
        {
            ("Neutral", "#687386"), ("Red", "#EF5A68"), ("Orange", "#F28A4B"),
            ("Yellow", "#F1B84B"), ("Green", "#4ED6A0"), ("Cyan", "#4CC9D8"),
            ("Blue", "#4F7FFF"), ("Violet", "#A979FF")
        };

        foreach (var color in colors)
        {
            var dot = new Border
            {
                Width = 11,
                Height = 11,
                CornerRadius = new CornerRadius(6),
                Background = (Brush)new BrushConverter().ConvertFromString(color.Hex)!
            };
            var item = new MenuItem { Header = color.Name, Icon = dot, Tag = color.Hex };
            item.Click += (_, _) => Main.SetNodeAccent(Node, color.Hex);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || Node is null || Main is null)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = button };
        AddMenuItem(menu, "New folder", "Ctrl+Shift+N", async () => Main.NewFolderCommand.Execute(null));
        AddMenuItem(menu, "New text file", string.Empty, async () => Main.NewNoteFileCommand.Execute(null));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "Paste", "Ctrl+V", async () => await Main.PasteIntoAsync(Node.FullPath));
        AddMenuItem(menu, "Add to Favorites", string.Empty, () => { Main.AddFavorite(Node.FullPath); return Task.CompletedTask; });
        AddMenuItem(menu, Node.IsNoteVisible ? "Hide note" : "Add note", string.Empty, () => { Main.ToggleNodeNote(Node); return Task.CompletedTask; });
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "Open in File Explorer", string.Empty, () => { NativeShellService.ShowInExplorer(Node.FullPath); return Task.CompletedTask; });
        AddMenuItem(menu, "Refresh", "F5", async () => await Main.RefreshNodeAsync(Node));
        AddMenuItem(menu, "Close branch", string.Empty, () => { Main.CloseNode(Node); return Task.CompletedTask; });
        menu.IsOpen = true;
    }

    private static void AddMenuItem(ItemsControl menu, string header, string gesture, Func<Task> action)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture };
        item.Click += async (_, _) => await action();
        menu.Items.Add(item);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (Node is not null)
        {
            Main?.CloseNode(Node);
        }
    }

    private void Node_DragEnter(object sender, DragEventArgs e) => UpdateDirectDropTarget(e);

    private void Node_DragOver(object sender, DragEventArgs e) => UpdateDirectDropTarget(e);

    private void Node_DragLeave(object sender, DragEventArgs e)
    {
        if (!IsMouseOver)
        {
            ClearDirectDropTarget();
        }
    }

    private async void Node_Drop(object sender, DragEventArgs e)
    {
        if (Main is null || Node is null || !TryGetDropPaths(e.Data, out var paths))
        {
            return;
        }

        var targetPath = _hoveredFolder?.FullPath ?? Node.FullPath;
        var internalDrag = e.Data.GetDataPresent(NativeShellService.InternalDragFormat);
        ClearDirectDropTarget();
        e.Handled = true;
        await Main.DropIntoPathAsync(paths, targetPath, internalDrag, Keyboard.Modifiers);
    }

    private void UpdateDirectDropTarget(DragEventArgs e)
    {
        if (Node is null || Main is null || !TryGetDropPaths(e.Data, out var paths))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        var hovered = GetItemUnderPointer(e.OriginalSource as DependencyObject);
        SetHoveredFolder(hovered is { IsDirectory: true } ? hovered : null);
        Node.IsDropTarget = _hoveredFolder is null;
        var targetPath = _hoveredFolder?.FullPath ?? Node.FullPath;
        var copy = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            || (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && paths.Any(path => !NativeShellService.IsSameVolume(path, targetPath)));
        e.Effects = copy ? DragDropEffects.Copy : DragDropEffects.Move;
        e.Handled = true;
    }

    private void SetHoveredFolder(FileItemViewModel? item)
    {
        if (_hoveredFolder == item)
        {
            return;
        }

        if (_hoveredFolder is not null)
        {
            _hoveredFolder.IsDropTarget = false;
        }
        _hoveredFolder = item;
        if (_hoveredFolder is not null)
        {
            _hoveredFolder.IsDropTarget = true;
        }
    }

    private void ClearDirectDropTarget()
    {
        if (Node is not null)
        {
            Node.IsDropTarget = false;
        }
        SetHoveredFolder(null);
    }

    private FileItemViewModel? GetItemUnderPointer(DependencyObject? source)
    {
        while (source is not null && source != ItemsList)
        {
            if (source is ListBoxItem container)
            {
                return container.DataContext as FileItemViewModel;
            }
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

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
}
