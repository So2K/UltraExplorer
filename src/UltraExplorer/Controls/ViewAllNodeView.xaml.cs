using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UltraExplorer.Models;
using UltraExplorer.ViewModels;

namespace UltraExplorer.Controls;

/// <summary>
/// One compact graph node.  The node body is dragged by the canvas (layout
/// only); the icon is a dedicated drag handle that starts a real file-system
/// drag operation, which is how Explorer behaves.
/// </summary>
public partial class ViewAllNodeView : UserControl
{
    private Point _dragOrigin;
    private bool _dragArmed;

    public ViewAllNodeView()
    {
        InitializeComponent();

        // The handle holds the mouse from the press: a release anywhere
        // else, or the capture taken away, lets go of the drag - left armed,
        // a later drag across the handle carried off this node's files.
        DragHandle.LostMouseCapture += (_, _) => _dragArmed = false;
    }

    private ViewAllNodeViewModel? Node => DataContext as ViewAllNodeViewModel;

    private MainViewModel? Shell => Window.GetWindow(this)?.DataContext as MainViewModel;

    private async void Chevron_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Node is { } node && Shell is { } shell)
        {
            shell.Tree.Activate(node);
            await shell.Tree.ToggleAsync(node);
        }
    }

    private async void Node_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || Node is not { } node || Shell is not { } shell)
        {
            return;
        }

        e.Handled = true;
        await shell.Tree.ToggleAsync(node);
    }

    private void Node_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Node is not { } node || Shell is not { } shell)
        {
            return;
        }

        e.Handled = true;
        if (!node.IsSelected)
        {
            shell.Tree.SelectOnly(node);
        }
        else
        {
            shell.Tree.Activate(node);
        }

        shell.ShowContextMenuFor(shell.Tree.SelectedPaths, this, e.GetPosition(this));
    }

    private async void LoadMore_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Node is { } node && Shell is { } shell)
        {
            await shell.Tree.LoadMoreAsync(node);
        }
    }

    private void DragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Node is not { } node || Shell is not { } shell)
        {
            return;
        }

        // Handled so the canvas never starts a layout drag from the handle.
        e.Handled = true;
        _dragOrigin = e.GetPosition(this);

        if (!node.IsSelected && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            shell.Tree.SelectOnly(node);
        }
        else
        {
            shell.Tree.Activate(node);
        }

        _dragArmed = DragHandle.CaptureMouse();
    }

    private void DragHandle_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Disarm();

    private void Disarm()
    {
        _dragArmed = false;
        if (DragHandle.IsMouseCaptured)
        {
            DragHandle.ReleaseMouseCapture();
        }
    }

    /// <summary>Whether a press on the handle is waiting to become a drag, for checks.</summary>
    internal bool IsDragArmedForChecks => _dragArmed;

    private void DragHandle_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed || Node is not { } node || Shell is not { } shell)
        {
            Disarm();
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        Disarm();
        var paths = shell.Tree.SelectedNodes.Count > 1 && node.IsSelected
            ? shell.Tree.SelectedNodes.Select(selected => selected.FullPath).ToArray()
            : [node.FullPath];

        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths);
        DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move);
    }
}
