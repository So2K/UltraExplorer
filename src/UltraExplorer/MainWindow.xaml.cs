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
    private readonly MainViewModel _viewModel = new();
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        EditorGestures.Mappings.Editor.ZoomModifierKey = ModifierKeys.Control;
        EditorGestures.Mappings.Editor.PanWithMouseWheel = true;
        EditorGestures.Mappings.Editor.PanVerticalModifierKey = ModifierKeys.None;
        EditorGestures.Mappings.Editor.PanHorizontalModifierKey = ModifierKeys.Shift;

        _viewModel.FocusNodeRequested += FocusNode;
        _viewModel.FitAllRequested += () => Editor.FitToScreen();
        _viewModel.ZoomRequested += factor => Editor.ZoomAtPosition(
            factor,
            Editor.ViewportLocation + (Vector)Editor.ViewportSize / 2);
        _viewModel.PromptRequested += ShowInputDialog;
        _viewModel.ConfirmRequested += ShowConfirmDialog;
        StateChanged += (_, _) => MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var darkMode = 1;
        var cornerPreference = 2;
        _ = DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 33, ref cornerPreference, sizeof(int));
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
        await Dispatcher.InvokeAsync(() =>
        {
            if (_viewModel.Nodes.Count == 1)
            {
                FocusNode(_viewModel.Nodes[0], false);
            }
        }, DispatcherPriority.Loaded);
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            _viewModel.Dispose();
            return;
        }

        e.Cancel = true;
        await _viewModel.SaveNowAsync();
        _allowClose = true;
        Close();
    }

    private void FocusNode(FolderNodeViewModel node, bool animated)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var width = node.ActualSize.Width > 1 ? node.ActualSize.Width : 420;
            var height = node.ActualSize.Height > 1 ? node.ActualSize.Height : 440;
            Editor.BringIntoView(new Point(node.Location.X + width / 2, node.Location.Y + height / 2), animated);
        }, DispatcherPriority.Loaded);
    }

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

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximized();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximized();
    private void CloseWindowButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximized()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private async void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FavoriteItemViewModel favorite)
        {
            await _viewModel.OpenFavoriteAsync(favorite);
        }
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.GoToAddressCommand.Execute(null);
            Editor.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            AddressBox.Text = _viewModel.ActiveNode?.FullPath ?? string.Empty;
            Editor.Focus();
            e.Handled = true;
        }
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
            Editor.Focus();
            e.Handled = true;
        }
    }

    private async void SearchResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as ListBox)?.SelectedItem is SearchResultViewModel result)
        {
            await _viewModel.OpenSearchResultAsync(result);
        }
    }

    private void Editor_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == Editor)
        {
            Keyboard.ClearFocus();
        }
    }

    private void Editor_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (FindAncestor<FolderNodeView>(e.OriginalSource as DependencyObject) is not null)
        {
            _viewModel.SetDropTarget(null);
            return;
        }

        if (!TryGetDropPaths(e.Data, out var paths))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var graphPoint = Editor.GetLocationInsideEditor(e);
        var target = _viewModel.FindNearestDropTarget(graphPoint, 96 / Editor.ViewportZoom);
        _viewModel.SetDropTarget(target);
        if (target is null)
        {
            e.Effects = DragDropEffects.None;
        }
        else
        {
            var copy = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                || (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && paths.Any(path => !NativeShellService.IsSameVolume(path, target.FullPath)));
            e.Effects = copy ? DragDropEffects.Copy : DragDropEffects.Move;
        }
        e.Handled = true;
    }

    private void Editor_PreviewDragLeave(object sender, DragEventArgs e)
    {
        if (!Editor.IsMouseOver)
        {
            _viewModel.SetDropTarget(null);
        }
    }

    private async void Editor_PreviewDrop(object sender, DragEventArgs e)
    {
        if (FindAncestor<FolderNodeView>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (!TryGetDropPaths(e.Data, out var paths))
        {
            return;
        }

        var graphPoint = Editor.GetLocationInsideEditor(e);
        var target = _viewModel.FindNearestDropTarget(graphPoint, 96 / Editor.ViewportZoom);
        _viewModel.SetDropTarget(null);
        if (target is null)
        {
            return;
        }

        e.Handled = true;
        var internalDrag = e.Data.GetDataPresent(NativeShellService.InternalDragFormat);
        await _viewModel.DropAsync(paths, target, internalDrag, Keyboard.Modifiers);
    }

    private void Minimap_Zoom(object sender, ZoomEventArgs e)
        => Editor.ZoomAtPosition(e.Zoom, e.Location);

    private void MinimapButton_Click(object sender, RoutedEventArgs e)
        => MinimapPanel.Visibility = MinimapPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        var isEditingText = Keyboard.FocusedElement is TextBox;

        if (modifiers == ModifierKeys.Control && e.Key == Key.L)
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (isEditingText)
        {
            return;
        }

        if (modifiers == ModifierKeys.Control && e.Key == Key.C)
        {
            _viewModel.CopyCommand.Execute(null);
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.X)
        {
            _viewModel.CutCommand.Execute(null);
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.V)
        {
            _viewModel.PasteCommand.Execute(null);
            e.Handled = true;
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.N)
        {
            _viewModel.NewFolderCommand.Execute(null);
            e.Handled = true;
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.D)
        {
            _viewModel.DuplicateCommand.Execute(null);
            e.Handled = true;
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.C)
        {
            var quoted = string.Join(Environment.NewLine, _viewModel.GetSelectedPaths().Select(path => $"\"{path}\""));
            if (!string.IsNullOrWhiteSpace(quoted))
            {
                Clipboard.SetText(quoted);
            }
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Alt && e.Key == Key.Left)
        {
            _viewModel.BackCommand.Execute(null);
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Alt && e.Key == Key.Right)
        {
            _viewModel.ForwardCommand.Execute(null);
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Alt && e.Key == Key.Up)
        {
            _viewModel.UpCommand.Execute(null);
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Alt && e.Key == Key.Enter)
        {
            var path = _viewModel.GetSelectedPaths().FirstOrDefault();
            if (path is not null)
            {
                NativeShellService.ShowProperties(path);
            }
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && (e.Key == Key.D0 || e.Key == Key.NumPad0))
        {
            _viewModel.ResetZoomCommand.Execute(null);
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Shift && (e.Key == Key.D1 || e.Key == Key.NumPad1))
        {
            Editor.FitToScreen();
            e.Handled = true;
        }
        else if (e.Key == Key.F2)
        {
            _viewModel.RenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _viewModel.RefreshCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            (modifiers.HasFlag(ModifierKeys.Shift) ? _viewModel.PermanentDeleteCommand : _viewModel.DeleteCommand).Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel.CloseSearchCommand.Execute(null);
        }
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

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);
}
