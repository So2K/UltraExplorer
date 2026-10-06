using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using UltraExplorer.Models;

namespace UltraExplorer;

public partial class MainWindow
{
    private long _selectionFocusRequest;

    /// <summary>Letters belong to an editor or a combo, including its descendants.</summary>
    internal static bool IsFocusTextInput(DependencyObject? source) =>
        FindAncestor<TextBoxBase>(source) is not null
        || FindAncestor<PasswordBox>(source) is not null
        || FindAncestor<ComboBox>(source) is not null;

    /// <summary>The same explicit chord decision is used by routed input and isolated tests.</summary>
    internal bool TryHandleFocusSelectionKey(Key key, ModifierKeys modifiers, bool isRepeat = false,
        DependencyObject? inputOrigin = null, Action? markHandled = null)
    {
        var source = inputOrigin ?? Keyboard.FocusedElement as DependencyObject;
        var fromList = IsInsideFocusSurface(source, FolderListItems);
        var onSelection = source is null || ReferenceEquals(source, this)
            || IsInsideFocusSurface(source, Editor) || fromList
            || IsInsideFocusSurface(source, ActivePane.Canvas);
        if (key != Key.F || modifiers != ModifierKeys.None || _closeRequested
            || IsFocusTextInput(inputOrigin) || IsFocusTextInput(Keyboard.FocusedElement as DependencyObject)
            || !onSelection
            || fromList && !_viewModel.Tree.FolderList.HasCurrentRows
            || _viewModel.Tree.Selection.Count == 0)
            return false;

        // Claim the routed event before any materialization can yield. Holding
        // F must not restart a camera request or queue another disk question.
        markHandled?.Invoke();
        if (!isRepeat) _ = FocusSelectionAsync();
        return true;
    }

    private static bool IsInsideFocusSurface(DependencyObject? source, DependencyObject surface)
    {
        for (var at = source; at is not null; at = ParentOf(at))
            if (ReferenceEquals(at, surface)) return true;
        return false;
    }

    /// <summary>Frames one selected item; it never selects its parent or records navigation.</summary>
    internal async Task<bool> FocusSelectionAsync(bool animated = false)
    {
        if (_closeRequested) return false;
        var tree = _viewModel.Tree;
        var selection = tree.Selection;
        if (selection.Count == 0) return false;
        var path = selection.Focus is { } focused && selection.Contains(focused)
            ? focused : selection.Paths[0];
        if (!selection.TryGetItem(path, out var item)) return false;
        var pane = ActivePane;
        var version = selection.Version;
        var ticket = ++_selectionFocusRequest;
        var nested = IsNested;
        bool Current() => !_closeRequested && ticket == _selectionFocusRequest && nested == IsNested
            && ReferenceEquals(pane, ActivePane) && _panes.Contains(pane)
            && ReferenceEquals(selection, tree.Selection) && selection.Version == version && selection.Contains(path);

        try
        {
            if (nested)
                return await pane.FocusSelectionPathAsync(path, item.IsDirectory, Current, animated);

            // The tree's by-name object can be replaced while its ancestors
            // load. Resolve it freshly without editing selection or history.
            var outcome = await tree.RevealAsync(path, focus: false, select: false, records: false,
                exact: true, selectionVersion: version);
            if (!Current() || !outcome.IsExact || outcome.Superseded || !tree.TryGetNode(path, out var node)
                || !node.IsTreeVisible || !node.HasLayoutPosition) return false;
            var bounds = node.Bounds;
            bounds.Inflate(Math.Max(8, node.Width * 0.08), Math.Max(8, node.Height * 0.2));
            Editor.FitToScreen(bounds);
            PushViewport();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            if (Current()) _viewModel.Toast.ShowError($"Could not focus {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }
}
