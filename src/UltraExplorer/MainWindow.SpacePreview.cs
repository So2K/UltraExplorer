using System.Windows;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using UltraExplorer.Controls;
using UltraExplorer.Models;

namespace UltraExplorer;

public partial class MainWindow
{
    private PreviewSpaceGesture _spacePreviewGesture = new();
    private ItemSelection? _spacePreviewSelection;
    private NestedPane? _spacePreviewPane;
    private NestedCanvas? _spacePreviewCamera;
    private long _spacePreviewVersion;
    private bool _spacePreviewNested;
    private FrameworkElement? _spacePointerSurface;
    private Point _spacePointerLast;
    private bool _spacePointerCaptured;

    internal static string? SelectedPreviewFile(ItemSelection selection)
    {
        var path = selection.Focus;
        if (path is null && selection.Count == 1) path = selection.Paths[0];
        return path is not null && selection.TryGetItem(path, out var item) && !item.IsDirectory ? item.Path : null;
    }

    private bool IsSpacePreviewSurface(DependencyObject? origin)
    {
        var source = origin ?? Keyboard.FocusedElement as DependencyObject;
        if (IsFocusTextInput(source) || FindAncestor<ButtonBase>(source) is not null) return false;
        var fromList = IsInsideFocusSurface(source, FolderListItems);
        return (source is null || ReferenceEquals(source, this) || IsInsideFocusSurface(source, Editor)
            || IsInsideFocusSurface(source, ActivePane.Canvas) || fromList)
            && (!fromList || _viewModel.Tree.FolderList.HasCurrentRows);
    }

    /// <summary>Space arms pan immediately; the release decides whether it was an untouched short tap.</summary>
    internal bool TryBeginSpacePreview(Key key, ModifierKeys modifiers, bool isRepeat = false,
        DependencyObject? inputOrigin = null, Action? markHandled = null)
    {
        if (key != Key.Space || modifiers != ModifierKeys.None || _closeRequested || _previewDetached
            || IsFocusTextInput(Keyboard.FocusedElement as DependencyObject) || !IsSpacePreviewSurface(inputOrigin)) return false;
        markHandled?.Invoke();
        if (!_spacePreviewGesture.IsPressed)
        {
            _spacePreviewSelection = _viewModel.Tree.Selection;
            _spacePreviewVersion = _spacePreviewSelection.Version;
            _spacePreviewPane = ActivePane;
            _spacePreviewNested = IsNested;
            // A repeat arriving after focus was lost has no original tap to finish.
            _spacePreviewGesture.Begin(isRepeat ? null : SelectedPreviewFile(_spacePreviewSelection));
            var source = inputOrigin ?? Keyboard.FocusedElement as DependencyObject;
            _spacePointerSurface = IsInsideFocusSurface(source, ActivePane.Canvas) ? ActivePane.Canvas
                : IsInsideFocusSurface(source, Editor) ? Editor : null;
            if (_spacePointerSurface is { } surface) _spacePointerLast = Mouse.GetPosition(surface);
            if (IsNested)
            {
                _spacePreviewCamera = ActivePane.Canvas;
                _spacePreviewCamera.CameraChanged += CancelSpacePreviewFromCamera;
            }
        }
        SetSpacePanArmed(true);
        return true;
    }

    internal string? FinishSpacePreview(ModifierKeys modifiers, DependencyObject? inputOrigin = null)
    {
        var path = _spacePreviewGesture.Release();
        StopSpacePointerPan();
        EndTreeSpacePan();
        SetSpacePanArmed(false);
        var current = path is not null && modifiers == ModifierKeys.None && !_closeRequested && !_previewDetached
            && IsSpacePreviewSurface(inputOrigin) && !IsFocusTextInput(Keyboard.FocusedElement as DependencyObject)
            && ReferenceEquals(_spacePreviewSelection, _viewModel.Tree.Selection)
            && _spacePreviewSelection.Version == _spacePreviewVersion
            && ReferenceEquals(_spacePreviewPane, ActivePane) && _spacePreviewNested == IsNested
            && string.Equals(path, SelectedPreviewFile(_viewModel.Tree.Selection), StringComparison.Ordinal);
        DetachSpacePreviewCamera();
        _spacePreviewSelection = null;
        _spacePreviewPane = null;
        return current ? path : null;
    }

    internal void CancelSpacePreview(bool disarm = false)
    {
        if (!disarm) { _spacePreviewGesture.CancelPreview(); return; }
        _spacePreviewGesture.Cancel();
        StopSpacePointerPan();
        EndTreeSpacePan();
        DetachSpacePreviewCamera();
        _spacePreviewSelection = null;
        _spacePreviewPane = null;
        SetSpacePanArmed(false);
    }
    private void CancelSpacePreviewFromCamera() => CancelSpacePreview();
    internal bool SpacePreviewPointerMoved(Point point, bool capture = true)
    {
        if (!_spacePreviewGesture.IsPressed || _spacePointerSurface is not { } surface
            || !ReferenceEquals(_spacePreviewPane, ActivePane) || _spacePreviewNested != IsNested) return false;
        var delta = point - _spacePointerLast;
        if (delta.X == 0 && delta.Y == 0) return true;
        if (!double.IsFinite(delta.X) || !double.IsFinite(delta.Y)) return false;
        _spacePointerLast = point;
        CancelSpacePreview();
        if (capture && !_spacePointerCaptured) _spacePointerCaptured = surface.CaptureMouse();
        surface.Cursor = Cursors.SizeAll;
        if (surface is NestedCanvas canvas) canvas.SpacePointerPan(delta);
        else
        {
            if (!Editor.IsPanning) Editor.BeginPanning();
            Editor.DisableAutoPanning = true;
            Editor.ViewportLocation -= delta / Math.Max(Editor.ViewportZoom, .001);
        }
        return true;
    }
    private bool HandleSpacePointerMove(MouseEventArgs args)
    {
        if (_spacePointerSurface is not { } surface || args.LeftButton != MouseButtonState.Released
            || args.MiddleButton != MouseButtonState.Released || args.RightButton != MouseButtonState.Released) return false;
        return SpacePreviewPointerMoved(args.GetPosition(surface));
    }
    private void StopSpacePointerPan()
    {
        var surface = _spacePointerSurface;
        _spacePointerSurface = null;
        var captured = _spacePointerCaptured;
        _spacePointerCaptured = false;
        if (captured && surface is { IsMouseCaptured: true }) surface.ReleaseMouseCapture();
        if (ReferenceEquals(surface, Editor) && !_isSpacePanning) Editor.EndPanning();
        Editor.DisableAutoPanning = _isSpacePanning || !Editor.IsMouseCaptureWithin;
    }
    private void SpacePreviewCaptureLost(object sender, MouseEventArgs args)
    {
        if (_spacePointerCaptured && _spacePointerSurface?.IsMouseCaptured != true) CancelSpacePreview(disarm: true);
    }
    internal Point SpacePreviewPointerAnchorForChecks => _spacePointerLast;
    private void EndTreeSpacePan()
    {
        if (!_isSpacePanning) return;
        _isSpacePanning = false;
        Editor.EndPanning();
        Editor.ReleaseMouseCapture();
        Editor.DisableAutoPanning = !Editor.IsMouseCaptureWithin;
    }
    private void DetachSpacePreviewCamera()
    {
        if (_spacePreviewCamera is { } camera) camera.CameraChanged -= CancelSpacePreviewFromCamera;
        _spacePreviewCamera = null;
    }
    private void SpacePreviewFocusLost(object sender, KeyboardFocusChangedEventArgs args)
    {
        if (_spacePreviewGesture.IsPressed && !IsSpacePreviewSurface(args.NewFocus as DependencyObject)) CancelSpacePreview(disarm: true);
    }
    internal PreviewSpaceGesture SpacePreviewGestureForChecks
    {
        get => _spacePreviewGesture;
        set { CancelSpacePreview(disarm: true); _spacePreviewGesture = value; }
    }
}
