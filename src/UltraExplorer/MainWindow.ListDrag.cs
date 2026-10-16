using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Archives;

namespace UltraExplorer;

public partial class MainWindow
{
    private FolderListItem? _folderListDragRow;
    private Point _folderListDragOrigin;
    private string? _folderListDragFolder;
    private string[]? _folderListDragKeptPaths;
    private long _folderListDragSelectionVersion;
    private bool _folderListDragDeferred;
    private ModifierKeys _folderListDragModifiers;
    private bool _folderListDragConsumed;
    private Border? _folderListDropOutline;
    private Brush? _folderListDropOldBrush;
    private Thickness _folderListDropOldThickness;
    private Thickness _folderListDropOldPadding;
    private bool _folderListDropHadBrush;
    private bool _folderListDropHadThickness;
    private bool _folderListDropHadPadding;

    internal Func<DependencyObject, IDataObject, DragDropEffects, DragDropEffects>? FolderListDragRunnerForChecks { get; set; }
    internal string? CurrentFolderListDropTarget { get; private set; }

    private void AttachFolderListDrag()
    {
        if (IsPickerMode) return;
        FolderListItems.AllowDrop = true;
        FolderListItems.AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(FolderListDragMove), handledEventsToo: true);
        FolderListItems.DragEnter += FolderListDragOver;
        FolderListItems.DragOver += FolderListDragOver;
        FolderListItems.DragLeave += FolderListDragLeave;
        FolderListItems.Drop += FolderListDrop;
    }

    private void DetachFolderListDrag()
    {
        CancelFolderListDragPress();
        FolderListItems.RemoveHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(FolderListDragMove));
        FolderListItems.DragEnter -= FolderListDragOver;
        FolderListItems.DragOver -= FolderListDragOver;
        FolderListItems.DragLeave -= FolderListDragLeave;
        FolderListItems.Drop -= FolderListDrop;
    }

    private bool BeginFolderListDragPress(MouseButtonEventArgs e)
    {
        if (RowUnder(e) is { } row) return BeginFolderListDragPress(row, e.GetPosition(FolderListItems), Keyboard.Modifiers, e.ClickCount);
        CancelFolderListDragPress();
        return false;
    }

    internal bool BeginFolderListDragPress(FolderListItem row, Point point, ModifierKeys modifiers, int clickCount = 1)
    {
        CancelFolderListDragPress();
        _folderListDragConsumed = false;
        if (IsPickerMode || _closeRequested || clickCount != 1 || !IsCurrentDragRow(row)) return false;
        _folderListDragRow = row;
        _folderListDragOrigin = point;
        _folderListDragFolder = _viewModel.Tree.FolderList.FolderPath;
        var selection = _viewModel.Tree.Selection;
        _folderListDragModifiers = modifiers;
        _folderListDragDeferred = selection.Contains(row.FullPath)
            && (modifiers == ModifierKeys.None && selection.Count > 1 || modifiers == ModifierKeys.Control);
        if (_folderListDragDeferred)
        {
            _folderListDragKeptPaths = [.. selection.Paths];
            _folderListDragSelectionVersion = selection.Version;
            _folderListMouseDown = true;
            _folderListClickWasOnSelection = false;
        }
        return _folderListDragDeferred;
    }

    private bool IsCurrentDragRow(FolderListItem row) => _viewModel.Tree.FolderList.HasCurrentRows
        && ReferenceEquals(_viewModel.Tree.FolderList.RowFor(row.FullPath), row);

    private void FolderListDragMove(object sender, MouseEventArgs e)
    {
        if (TryStartFolderListDrag(e.GetPosition(FolderListItems), e.LeftButton == MouseButtonState.Pressed)) e.Handled = true;
    }

    internal bool TryStartFolderListDrag(Point point, bool leftPressed)
    {
        if (_folderListDragRow is not { } row) return false;
        if (!leftPressed || _closeRequested || !IsTabPresented || !IsCurrentDragRow(row)
            || !ViewAllPath.Equals(_folderListDragFolder ?? string.Empty, _viewModel.Tree.FolderList.FolderPath))
        { CancelFolderListDragPress(); return false; }
        if (Math.Abs(point.X - _folderListDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - _folderListDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return false;
        var selection = _viewModel.Tree.Selection;
        if (_folderListDragKeptPaths is not null && selection.Version != _folderListDragSelectionVersion)
        { CancelFolderListDragPress(); return false; }
        var original = _folderListDragKeptPaths ?? (selection.Contains(row.FullPath) ? [.. selection.Paths] : new[] { row.FullPath });
        var paths = original;
        _folderListDragRow = null;
        _folderListDragConsumed = true;
        _folderListMouseDown = false;
        _folderListClickWasOnSelection = false;
        _folderListRenameTimer.Stop();
        _folderListRenameRow = null;
        try
        {
            if (paths.Any(ArchiveService.IsInsideArchive))
            {
                if (_viewModel.ExtractForDrag(paths) is not { } extracted) return true;
                paths = extracted;
            }
            ((INestedPaneHost)this).NestedDragPaths = paths;
            var data = new DataObject(DataFormats.FileDrop, paths);
            if (FolderListDragRunnerForChecks is { } run) run(FolderListItems, data, DragDropEffects.Copy | DragDropEffects.Move);
            else DragDrop.DoDragDrop(FolderListItems, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch (Exception error) { _viewModel.Toast.ShowError(error.Message); }
        finally
        {
            ((INestedPaneHost)this).NestedDragPaths = null;
            ((INestedPaneHost)this).ClearDropTargets();
            ClearFolderListDropTarget();
        }
        if (FolderListDragRunnerForChecks is null) _ = RefreshFolderListDragSourcesAsync(original);
        return true;
    }

    private async Task RefreshFolderListDragSourcesAsync(IReadOnlyList<string> paths)
    {
        var folders = paths.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var delay in new[] { 300, 1500 })
        {
            await Task.Delay(delay);
            if (_closeRequested || _viewModel.IsDisposed) return;
            foreach (var folder in folders) await _viewModel.Tree.RefreshPathAsync(folder);
        }
    }

    private bool FinishFolderListDragPress(MouseButtonEventArgs e)
    {
        var consumed = FinishFolderListDragPress(RowUnder(e), Keyboard.Modifiers, e.ClickCount);
        if (consumed) e.Handled = true;
        return consumed;
    }

    internal bool FinishFolderListDragPress(FolderListItem? row, ModifierKeys modifiers, int clickCount = 1)
    {
        var consumed = _folderListDragConsumed;
        var deferred = _folderListDragDeferred && !consumed && clickCount == 1 && modifiers == _folderListDragModifiers
            && row is not null && ReferenceEquals(row, _folderListDragRow) && IsCurrentDragRow(row);
        var toggle = deferred && modifiers == ModifierKeys.Control;
        var collapse = deferred && modifiers == ModifierKeys.None;
        CancelFolderListDragPress();
        _folderListDragConsumed = false;
        if (toggle)
        {
            using (_viewModel.Tree.FolderList.HoldFolder())
                _viewModel.Tree.Selection.Apply(new SelectionEdit
                {
                    Removed = [row!.FullPath], Anchor = row.FullPath, Focus = row.FullPath, Source = SelectionSource.List
                });
        }
        else if (collapse)
        {
            using (_viewModel.Tree.FolderList.HoldFolder())
                _viewModel.Tree.Selection.ReplaceSingle(row!.FullPath, row.IsDirectory, row.Entry.SizeBytes ?? 0, SelectionSource.List);
            _folderListClickWasOnSelection = false;
        }
        return consumed;
    }

    internal void CancelFolderListDragPress()
    {
        _folderListDragRow = null;
        _folderListDragKeptPaths = null;
        _folderListDragFolder = null;
        _folderListDragDeferred = false;
        _folderListMouseDown = false;
        ClearFolderListDropTarget();
    }

    private FolderListItem? DropRow(DragEventArgs e) => FindAncestor<ListBoxItem>(
        FolderListItems.InputHitTest(e.GetPosition(FolderListItems)) as DependencyObject)?.DataContext as FolderListItem;

    internal bool TryResolveFolderListDropTarget(FolderListItem? row, IReadOnlyList<string> paths, DragDropEffects effect, out string folder)
    {
        folder = string.Empty;
        var list = _viewModel.Tree.FolderList;
        if (IsPickerMode || _closeRequested || !list.HasCurrentRows || effect == DragDropEffects.None
            || row is not null && !IsCurrentDragRow(row)) return false;
        var target = row is { IsDirectory: true } ? row.FullPath : list.FolderPath;
        if (string.IsNullOrWhiteSpace(target) || ArchiveService.IsInsideArchive(target)
            || ArchiveService.MayContainArchiveName(target) && ArchiveService.IsArchiveFile(target, archivesEnabled: true)
            || paths.Any(path => ViewAllPath.Equals(path, target) || NativeShellService.IsInvalidMoveTarget(path, target)
                || effect == DragDropEffects.Move && ViewAllPath.Equals(Path.GetDirectoryName(path) ?? string.Empty, target))) return false;
        folder = target;
        return true;
    }

    private void FolderListDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        var row = DropRow(e);
        if (!_viewModel.Tree.FolderList.HasCurrentRows || _closeRequested || !TryGetDropPaths(e.Data, out var paths)) { ClearFolderListDropTarget(); return; }
        var target = row is { IsDirectory: true } ? row.FullPath : _viewModel.Tree.FolderList.FolderPath;
        var effect = DropEffectFor(e, paths, target);
        if (!TryResolveFolderListDropTarget(row, paths, effect, out var folder)) { ClearFolderListDropTarget(); return; }
        SetFolderListDropTarget(folder, row);
        e.Effects = effect;
    }

    private void FolderListDragLeave(object sender, DragEventArgs e)
    {
        ClearFolderListDropTarget();
        ForgetDropPaths();
    }

    private void FolderListDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        ClearFolderListDropTarget();
        try
        {
            ForgetDropPaths();
            if (!_viewModel.Tree.FolderList.HasCurrentRows || _closeRequested || !TryGetDropPaths(e.Data, out var paths)) return;
            ForgetDropPaths();
            var row = DropRow(e);
            var target = row is { IsDirectory: true } ? row.FullPath : _viewModel.Tree.FolderList.FolderPath;
            var effect = DropEffectFor(e, paths, target);
            if (!TryResolveFolderListDropTarget(row, paths, effect, out var folder)) return;
            if (CompleteExternalDrop(e.Data, paths, ReportedDropEffect(effect), () =>
                    _viewModel.DropIntoPathWithResultAsync(paths, folder, move: effect == DragDropEffects.Move)))
                e.Effects = ReportedDropEffect(effect);
        }
        catch (Exception error) { _viewModel.Toast.ShowError(error.Message); }
        finally { ForgetDropPaths(); }
    }

    private void SetFolderListDropTarget(string folder, FolderListItem? row)
    {
        Border? outline = FolderListPanel;
        if (row is { IsDirectory: true })
        {
            var container = FolderListItems.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem;
            outline = container is null ? null : container.Template?.FindName("Chrome", container) as Border;
        }
        if (ViewAllPath.Equals(CurrentFolderListDropTarget ?? string.Empty, folder) && ReferenceEquals(outline, _folderListDropOutline)) return;
        ClearFolderListDropTarget();
        CurrentFolderListDropTarget = folder;
        if (outline is null) return;
        _folderListDropOutline = outline;
        _folderListDropOldBrush = outline.BorderBrush;
        _folderListDropOldThickness = outline.BorderThickness;
        _folderListDropOldPadding = outline.Padding;
        _folderListDropHadBrush = outline.ReadLocalValue(Border.BorderBrushProperty) != DependencyProperty.UnsetValue;
        _folderListDropHadThickness = outline.ReadLocalValue(Border.BorderThicknessProperty) != DependencyProperty.UnsetValue;
        _folderListDropHadPadding = outline.ReadLocalValue(Border.PaddingProperty) != DependencyProperty.UnsetValue;
        outline.SetCurrentValue(Border.BorderBrushProperty, (Brush)FindResource("AccentBrush"));
        if (!ReferenceEquals(outline, FolderListPanel))
        {
            // Use the row's existing chrome without covering selection or
            // changing its size: the new border takes one unit of its padding.
            outline.SetCurrentValue(Border.BorderThicknessProperty, new Thickness(1));
            outline.SetCurrentValue(Border.PaddingProperty, new Thickness(
                Math.Max(0, _folderListDropOldPadding.Left - 1), Math.Max(0, _folderListDropOldPadding.Top - 1),
                Math.Max(0, _folderListDropOldPadding.Right - 1), Math.Max(0, _folderListDropOldPadding.Bottom - 1)));
        }
    }

    private void ClearFolderListDropTarget()
    {
        CurrentFolderListDropTarget = null;
        if (_folderListDropOutline is { } outline)
        {
            // SetCurrentValue preserves live bindings/resource expressions;
            // ClearValue is used only for properties that had no local value.
            if (_folderListDropHadBrush) outline.SetCurrentValue(Border.BorderBrushProperty, _folderListDropOldBrush);
            else outline.ClearValue(Border.BorderBrushProperty);
            if (_folderListDropHadThickness) outline.SetCurrentValue(Border.BorderThicknessProperty, _folderListDropOldThickness);
            else outline.ClearValue(Border.BorderThicknessProperty);
            if (_folderListDropHadPadding) outline.SetCurrentValue(Border.PaddingProperty, _folderListDropOldPadding);
            else outline.ClearValue(Border.PaddingProperty);
        }
        _folderListDropOutline = null;
    }
}
