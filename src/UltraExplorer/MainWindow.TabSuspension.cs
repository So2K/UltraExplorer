using System.Windows.Controls;

namespace UltraExplorer;

public partial class MainWindow
{
    /// <summary>
    /// Ends transient input when this tab is put away. Its view, navigation,
    /// selection, searches and camera remain in their own retained context.
    /// </summary>
    internal void SuspendTabContext()
    {
        _folderListRenameTimer.Stop();
        _folderListRenameRow = null;
        CancelFolderListDragPress();
        CancelSpacePreview(disarm: true);
        ClearHoverPreview();
        DropPreparedMenu();
        _viewModel.Address.EndEdit();
        foreach (var crumb in _viewModel.Address.Breadcrumbs)
        {
            crumb.IsMenuOpen = false;
        }

        foreach (var pane in _panes)
        {
            if (pane.Canvas.IsMouseCaptured) pane.Canvas.ReleaseMouseCapture();
            pane.Canvas.DropTarget = null;
            if (pane.View.ContextMenu is ContextMenu menu) menu.IsOpen = false;
        }

        if (Editor.IsMouseCaptured) Editor.ReleaseMouseCapture();
        ClearNodeDropTarget();
        ForgetDropPaths();
        UnhookSortSlices();
    }

    /// <summary>Refreshes the retained presentation without rebuilding or navigating it.</summary>
    internal void ResumeTabContext()
    {
        if (_closeRequested || _viewModel.IsDisposed) return;
        _viewModel.Changes.PollNow();
        UpdatePaneChrome();
        HookSortSlices();
    }
}
