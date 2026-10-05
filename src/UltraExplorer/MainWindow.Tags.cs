using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace UltraExplorer;

public partial class MainWindow
{
    private FolderTagProjection? _folderTags;
    private DateTime _folderTagsLastRefreshUtc;

    /// <summary>
    /// A child Loaded event can run before the window's asynchronous Loaded
    /// handler has finished. The projection therefore owns an idempotent mark
    /// reload instead of depending on that event order.
    /// </summary>
    private async void FolderTagsSection_Loaded(object sender, RoutedEventArgs e)
    {
        if (_folderTags is not null)
        {
            return;
        }

        var projection = new FolderTagProjection(_viewModel.Marks, Dispatcher, ClassifyFolderTagAsync);
        _folderTags = projection;
        FolderTagItems.ItemsSource = projection.Items;
        projection.PropertyChanged += FolderTagsOnPropertyChanged;
        _viewModel.Tree.PropertyChanged += FolderTagsTreeOnPropertyChanged;
        Activated += FolderTagsWindowActivated;
        Closed += FolderTagsWindowClosed;

        await projection.InitializeAsync();
        if (!ReferenceEquals(_folderTags, projection))
        {
            return;
        }

        projection.SetActivePath(_viewModel.Tree.ActivePath);
        UpdateFolderTagsVisibility();
    }

    private ValueTask<FolderTagPathKind> ClassifyFolderTagAsync(string path, CancellationToken cancellationToken)
    {
        if (_viewModel.Tree.TryGetNode(path, out var known))
        {
            return ValueTask.FromResult(known.IsDirectory
                ? FolderTagPathKind.Folder
                : FolderTagPathKind.File);
        }

        return FolderTagProjection.ClassifyPathAsync(path, cancellationToken);
    }

    private async void FolderTagsWindowActivated(object? sender, EventArgs e)
    {
        var projection = _folderTags;
        if (projection is null)
        {
            return;
        }

        // Activation can fire twice while Windows settles focus. One pull is
        // enough; same-process edits already arrive through MarkChanged.
        var now = DateTime.UtcNow;
        if (now - _folderTagsLastRefreshUtc < TimeSpan.FromMilliseconds(500))
        {
            return;
        }

        _folderTagsLastRefreshUtc = now;
        await projection.RefreshAsync();
        if (ReferenceEquals(_folderTags, projection))
        {
            projection.SetActivePath(_viewModel.Tree.ActivePath);
        }
    }

    private void FolderTagsTreeOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewAllViewModel.ActivePath))
        {
            _folderTags?.SetActivePath(_viewModel.Tree.ActivePath);
        }
    }

    private void FolderTagsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FolderTagProjection.HasItems))
        {
            UpdateFolderTagsVisibility();
        }
    }

    private void UpdateFolderTagsVisibility()
    {
        FolderTagsSection.Visibility = _folderTags is { HasItems: true }
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void FolderTagItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not FolderTagItemViewModel tag)
        {
            return;
        }

        // A stale tag must never select the deepest surviving parent: the
        // next Delete would then act on that parent. Exact navigation leaves
        // the current selection untouched when the folder is gone/offline.
        var outcome = await _viewModel.Tree.RevealAsync(tag.FullPath, exact: true);
        if (outcome is { IsExact: true, Node: { } node }
            && _viewModel.Marks.SetItemKind(tag.FullPath, node.IsDirectory))
        {
            _ = _viewModel.Marks.SaveAsync();
        }
    }

    private void FolderTagItem_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not FolderTagItemViewModel tag)
        {
            return;
        }

        e.Handled = true;
        ShowFolderTagMenu(element, tag, keyboard: false);
    }

    private void FolderTagItem_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var opensMenu = OpensFolderTagMenu(e.Key, e.SystemKey, Keyboard.Modifiers);
        if (!opensMenu || sender is not FrameworkElement element || element.DataContext is not FolderTagItemViewModel tag)
        {
            return;
        }

        e.Handled = true;
        ShowFolderTagMenu(element, tag, keyboard: true);
    }

    private static bool OpensFolderTagMenu(Key key, Key systemKey, ModifierKeys modifiers)
    {
        var effectiveKey = key == Key.System ? systemKey : key;
        return effectiveKey == Key.Apps
            || (effectiveKey == Key.F10 && modifiers.HasFlag(ModifierKeys.Shift));
    }

    private void ShowFolderTagMenu(FrameworkElement element, FolderTagItemViewModel tag, bool keyboard)
    {
        var colour = ColourEntry([tag.FullPath]);
        var changeColour = colour with
        {
            Label = "Change color",
            Children = colour.Children?
                .Where(entry => !string.Equals(entry.Label, "Default", StringComparison.OrdinalIgnoreCase))
                .ToArray()
        };
        var menu = new ContextMenu
        {
            PlacementTarget = element,
            Placement = keyboard ? PlacementMode.Bottom : PlacementMode.MousePoint
        };
        AddEntries(menu,
        [
            new ShellMenuEntry("Unpin", () => _viewModel.Tree.ApplyAccent([tag.FullPath], null)) { Glyph = "\uE77A" },
            changeColour
        ]);
        menu.IsOpen = true;
    }

    private void FolderTagsWindowClosed(object? sender, EventArgs e)
    {
        Activated -= FolderTagsWindowActivated;
        Closed -= FolderTagsWindowClosed;
        _viewModel.Tree.PropertyChanged -= FolderTagsTreeOnPropertyChanged;

        if (_folderTags is { } projection)
        {
            projection.PropertyChanged -= FolderTagsOnPropertyChanged;
            projection.Dispose();
            _folderTags = null;
        }
    }
}
