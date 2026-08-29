using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Nodify;
using Nodify.Events;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace UltraExplorer;

/// <summary>
/// What dragging a node means.  Over empty canvas it is a layout move, as it has
/// always been.  Released directly on a folder it is a file move into that
/// folder; released directly on a program it hands the files to that program,
/// the way dropping them on its icon does in Explorer.
///
/// The node is put back where it was picked up before either of those runs: the
/// drag was a gesture aimed at something, not a request to rearrange the canvas.
/// </summary>
public partial class MainWindow
{
    private enum NodeDropAction
    {
        None,
        MoveInto,
        OpenWith
    }

    private ViewAllNodeViewModel[] _nodeDragRoots = [];
    private Point[] _nodeDragOrigins = [];
    private bool[] _nodeDragWasManual = [];
    private HashSet<ViewAllNodeViewModel> _nodeDragExcluded = [];
    private ViewAllNodeViewModel? _nodeDropTarget;
    private NodeDropAction _nodeDropAction;

    private void ConfigureNodeDrag()
    {
        // ItemsMoved is raised only by a drag that was committed.  The drag
        // commands and IsDragging also fire when a drag is cancelled, which is
        // why the file operation hangs off this event and nothing else.
        Editor.ItemsMoved += Editor_ItemsMoved;

        DependencyPropertyDescriptor
            .FromProperty(NodifyEditor.IsDraggingProperty, typeof(NodifyEditor))
            .AddValueChanged(Editor, OnEditorIsDraggingChanged);
    }

    private void DetachNodeDrag()
    {
        Editor.ItemsMoved -= Editor_ItemsMoved;
        DependencyPropertyDescriptor
            .FromProperty(NodifyEditor.IsDraggingProperty, typeof(NodifyEditor))
            .RemoveValueChanged(Editor, OnEditorIsDraggingChanged);
    }

    private void OnEditorIsDraggingChanged(object? sender, EventArgs e)
    {
        if (Editor.IsDragging)
        {
            BeginNodeDrag([.. _viewModel.Tree.SelectedNodes]);
            return;
        }

        // Deferred, because whether this or ItemsMoved runs first is Nodify's
        // business: by the time an input-priority callback runs, a committed
        // drag has already been dealt with and this only has to tidy up.
        _ = Dispatcher.BeginInvoke(() => EndNodeDrag(committed: false), DispatcherPriority.Input);
    }

    private void Editor_ItemsMoved(object sender, ItemsMovedEventArgs e) => EndNodeDrag(committed: true);

    private void BeginNodeDrag(IReadOnlyList<ViewAllNodeViewModel> roots)
    {
        ClearNodeDropTarget();
        _nodeDragRoots = [.. roots];
        _nodeDragOrigins = [.. roots.Select(node => node.Location)];
        _nodeDragWasManual = [.. roots.Select(node => node.HasManualPosition)];

        // A drag must never land on itself or on anything it is carrying.
        _nodeDragExcluded = [];
        foreach (var root in roots)
        {
            Collect(root);
        }

        void Collect(ViewAllNodeViewModel node)
        {
            if (!_nodeDragExcluded.Add(node))
            {
                return;
            }

            foreach (var child in node.Children)
            {
                Collect(child);
            }
        }
    }

    /// <summary>Lights up whatever the pointer is over, and decides what it means.</summary>
    private void UpdateNodeDragHover(Point graphPoint)
    {
        // A file dialog opened for another program is no place to move somebody's
        // files around or launch anything.
        if (_nodeDragRoots.Length == 0 || _picker is not null)
        {
            return;
        }

        var hit = _viewModel.Tree.HitTest(graphPoint, _nodeDragExcluded.Contains);
        if (hit is null)
        {
            ClearNodeDropTarget();
            return;
        }

        var paths = _nodeDragRoots.Select(node => node.FullPath).ToArray();

        if (hit.IsDirectory)
        {
            var invalid = paths.Any(path =>
                ViewAllPath.Equals(path, hit.FullPath)
                || NativeShellService.IsInvalidMoveTarget(path, hit.FullPath));
            if (invalid)
            {
                ClearNodeDropTarget();
                return;
            }

            _viewModel.Tree.SetRunTarget(null);
            _viewModel.Tree.SetDropTarget(hit);
            _nodeDropTarget = hit;
            _nodeDropAction = NodeDropAction.MoveInto;
            return;
        }

        // Only files go to a program; a folder handed to an executable is not
        // something Explorer offers either.
        if (NativeShellService.IsExecutable(hit.FullPath)
            && _nodeDragRoots.All(node => !node.IsDirectory))
        {
            _viewModel.Tree.SetDropTarget(null);
            _viewModel.Tree.SetRunTarget(hit);
            _nodeDropTarget = hit;
            _nodeDropAction = NodeDropAction.OpenWith;
            return;
        }

        ClearNodeDropTarget();
    }

    private void ClearNodeDropTarget()
    {
        _nodeDropTarget = null;
        _nodeDropAction = NodeDropAction.None;
        _viewModel.Tree.SetDropTarget(null);
        _viewModel.Tree.SetRunTarget(null);
    }

    private void EndNodeDrag(bool committed)
    {
        if (_nodeDragRoots.Length == 0)
        {
            return;
        }

        var target = _nodeDropTarget;
        var action = _nodeDropAction;
        var roots = _nodeDragRoots;
        var origins = _nodeDragOrigins;
        var wasManual = _nodeDragWasManual;

        _nodeDragRoots = [];
        _nodeDragOrigins = [];
        _nodeDragWasManual = [];
        _nodeDragExcluded = [];
        ClearNodeDropTarget();

        if (!committed || target is null || action == NodeDropAction.None)
        {
            return;
        }

        // The gesture was aimed at the target, so the canvas goes back to how it
        // looked: setting Location carries the subtree back with it, and the
        // manual flag is only kept for a node that already had one.
        for (var index = 0; index < roots.Length && index < origins.Length; index++)
        {
            roots[index].Location = origins[index];
            if (!wasManual[index])
            {
                roots[index].ReleaseManualPosition();
            }
        }

        var paths = roots.Select(node => node.FullPath).ToArray();
        _ = CompleteNodeDropAsync(paths, target, action);
    }

    private async Task CompleteNodeDropAsync(
        IReadOnlyList<string> paths,
        ViewAllNodeViewModel target,
        NodeDropAction action)
    {
        try
        {
            if (action == NodeDropAction.MoveInto)
            {
                await _viewModel.DropIntoPathAsync(paths, target.FullPath, Keyboard.Modifiers);
                return;
            }

            NativeShellService.OpenWithProgram(target.FullPath, paths);
            await _viewModel.Toast.ShowSuccessAsync($"Opened with {target.DisplayName}");
        }
        catch (Exception exception)
        {
            _viewModel.Toast.ShowError(exception.Message);
        }
    }
}
