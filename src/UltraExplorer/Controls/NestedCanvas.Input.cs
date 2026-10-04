using System.Windows;
using System.Windows.Input;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

// Input: the mouse - a press and what it turns into once it moves (a pan, a
// file drag, a selection rectangle), a click with its modifiers, the wheel,
// hover and grab - what is under a point, and the keyboard's moves over the
// grid of folders and files.  WPF's mouse events and the tests' synthetic
// ones (NestedPointer) go through the same handlers below.
public sealed partial class NestedCanvas
{
    /// <summary>What the left press under way turns into once it moves; the right and middle presses always pan.</summary>
    private PressIntent _pressIntent;

    /// <summary>The modifiers held when the button went down, which decide a click and a rectangle whatever is held later.</summary>
    private ModifierKeys _pressModifiers;

    /// <summary>The folder a rectangle started by the press under way is drawn in.</summary>
    private NestedFolder? _pressContainer;

    /// <summary>
    /// The press under way is a test's or the bench's (<see cref="NestedPointer"/>):
    /// the real mouse is not captured for it, and what the real mouse does
    /// meanwhile - WPF raises a move of its own whenever capture or layout
    /// changes - is not taken for part of it.
    /// </summary>
    private bool _pressSynthetic;

    /// <summary>Whether a real mouse event is to be let past: not while a synthetic press is under way.</summary>
    private bool TakesRealMouse => !_pressSynthetic || _press == PressKind.None;

    /// <summary>The innermost drawn folder under a point in the control.</summary>
    public NestedHit? HitTest(Point point)
    {
        EnsureCamera();
        if (_tree is null || _anchor is null)
        {
            return null;
        }

        BuildChain();
        var folder = _tree.Root;
        var (x, y, w) = _chain[folder];
        if (!Inside(point, x, y, w))
        {
            return null;
        }

        while (true)
        {
            // What is hit is what the current order puts there, drawn or not.
            Ensure(folder);
            var grid = folder.Grid;
            if (grid.IsEmpty || w * grid.Scale < MinimumCellPixels * 2)
            {
                break;
            }

            var index = grid.IndexAt((point.X - x) / w, (point.Y - y) / w);
            if (index < 0 || index >= folder.Children.Count)
            {
                break;
            }

            var child = folder.Children[index];
            if (_chain.TryGetValue(child, out var exact))
            {
                (x, y, w) = exact;
            }
            else
            {
                x += child.OffsetX * w;
                y += child.OffsetY * w;
                w *= child.Scale;
            }

            folder = child;
        }

        // Not in any sub-folder: perhaps on one of the files under them.
        var files = folder.FileGrid;
        if (!files.IsEmpty && w * files.TileWidth >= 2)
        {
            var fileIndex = files.IndexAt((point.X - x) / w, (point.Y - y) / w);
            if (fileIndex >= 0 && fileIndex < folder.Files.Count)
            {
                var (fx, fy) = files.Origin(fileIndex);
                var tile = new Rect(x + fx * w, y + fy * w, files.TileWidth * w, files.TileHeight * w);
                return new NestedHit(folder, tile, true, fileIndex, folder.Files[fileIndex].Name);
            }
        }

        var header = w * NestedLayout.HeaderHeight;
        var onHeader = header < HeaderGrabPixels || point.Y - y <= header;
        return new NestedHit(folder, new Rect(x, y, w, w * NestedLayout.CellHeight), onHeader);
    }

    /// <summary>
    /// The keys the canvas answers itself, while it has the focus.  The
    /// arrows move over the grid of the focused item's folder, Shift+arrows
    /// select from the anchor to where they move, Enter goes in and Backspace
    /// out; Ctrl+A selects everything in the current folder, and Esc lets go
    /// of a rectangle being drawn, or else of the selection - and, with
    /// nothing selected, is left to the window.  Returns whether the key
    /// meant something here.
    /// </summary>
    public bool HandleKey(Key key, ModifierKeys modifiers)
    {
        if (_tree is null)
        {
            return false;
        }

        TakeSetSelection();
        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            if (_marquee is not null)
            {
                CancelMarquee();
                return true;
            }

            return ClearSelection();
        }

        if (key == Key.A && modifiers == ModifierKeys.Control)
        {
            SelectAll();
            return true;
        }

        var isArrow = key is Key.Left or Key.Right or Key.Up or Key.Down;
        var extend = modifiers == ModifierKeys.Shift && isArrow;
        if (modifiers != ModifierKeys.None && !extend)
        {
            return false;
        }

        var active = ActiveTarget();
        switch (key)
        {
            case Key.Enter when active is { } target:
                var hit = new NestedHit(
                    target.Folder,
                    default,
                    true,
                    target.FileIndex,
                    target.FileIndex >= 0 ? target.Folder.Files[target.FileIndex].Name : null);
                OpenRequested?.Invoke(hit);
                if (!hit.IsFile)
                {
                    FlyTo(target.Folder);
                    _selection.CurrentFolder = target.Folder;
                }

                return true;
            case Key.Back when active is { } target:
                // Out of a file is its folder; out of a folder is its parent.
                var up = target.FileIndex >= 0 ? target.Folder : target.Folder.Parent;
                if (up is null || up.IsComputer)
                {
                    return true;
                }

                MarkSelected(up.FullPath);
                SelectRequested?.Invoke(up.FullPath, false);
                FlyTo(up);

                // The view is on the folder gone up to now: Ctrl+A fills it.
                _selection.CurrentFolder = up;
                return true;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                if (Neighbour(active, key) is not { } next)
                {
                    return active is not null;
                }

                var nextKey = KeyOf(next);
                var extended = extend && SelectRange(
                    nextKey,
                    add: false,
                    start: _selection.Anchor is { } anchor && ReferenceEquals(anchor.Container, nextKey.Container)
                        ? anchor
                        : active is { } previous ? KeyOf(previous) : null);
                if (!extended)
                {
                    SelectOnly(nextKey);
                    SelectRequested?.Invoke(nextKey.Path, false);
                }

                _selection.CurrentFolder = nextKey.Container;
                EnsureVisible(next.Folder, next.FileIndex);
                return true;
            default:
                return false;
        }
    }

    // ---- the mouse -----------------------------------------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
        Focus();
        PointerDown(MouseButton.Left, e.GetPosition(this), Keyboard.Modifiers, e.ClickCount);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        e.Handled = true;
        Focus();
        PointerDown(MouseButton.Middle, e.GetPosition(this), Keyboard.Modifiers, e.ClickCount);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        e.Handled = true;
        Focus();
        PointerDown(MouseButton.Right, e.GetPosition(this), Keyboard.Modifiers, e.ClickCount);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (TakesRealMouse && PointerMove(e.GetPosition(this)))
        {
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (TakesRealMouse && PointerUp(MouseButton.Left, e.GetPosition(this)))
        {
            e.Handled = true;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Middle && TakesRealMouse && PointerUp(MouseButton.Middle, e.GetPosition(this)))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// The menu opens on the button going up, never down (see the window's
    /// list menu for why) - and not at all when the press dragged the view.
    /// </summary>
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        e.Handled = true;
        if (TakesRealMouse)
        {
            PointerUp(MouseButton.Right, e.GetPosition(this));
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (TakesRealMouse)
        {
            PointerLost();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        _hoverPoint = new Point(double.NaN, double.NaN);
        RenderOverlay();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;
        if (TakesRealMouse)
        {
            PointerWheel(e.GetPosition(this), e.Delta, Keyboard.Modifiers);
        }
    }

    /// <summary>
    /// A button went down.  The left one is looked at where it landed and
    /// decided on (see <see cref="ClassifyLeftPress"/>), but nothing happens
    /// until it moves or comes up; the middle one pans at once; the right one
    /// waits to see whether it is a click, for the menu, or a drag, to pan.
    /// While one button is down the others are ignored.  (The keyboard focus
    /// comes to the canvas with every real press, in the handlers above; a
    /// synthetic one leaves it where it is.)
    /// </summary>
    internal void PointerDown(MouseButton button, Point point, ModifierKeys modifiers, int clickCount, bool synthetic = false)
    {
        // Somebody is using the canvas: frames that kept failing get one more try.
        RetryFailedFrames();
        if (_press != PressKind.None)
        {
            return;
        }

        _pressSynthetic = synthetic;
        TakeSetSelection();
        switch (button)
        {
            case MouseButton.Left:
                PressLeft(point, modifiers, clickCount);
                break;
            case MouseButton.Middle:
                _press = PressKind.Middle;
                _pressIntent = PressIntent.Pan;
                _pressPoint = point;
                _pressMoved = true;
                _panLast = point;
                StopFlight();
                Cursor = Cursors.SizeAll;
                TakeMouse();
                RequestFrame(Layers.Overlay);
                break;
            case MouseButton.Right:
                _press = PressKind.Right;
                _pressIntent = PressIntent.Pan;
                _pressPoint = point;
                _pressMoved = false;
                _panLast = point;
                TakeMouse();
                RequestFrame(Layers.Overlay);
                if (ContextMenuPressed is { } pressed)
                {
                    var (target, onBackground) = MenuTargetAt(point);
                    pressed(target, onBackground, point);
                }

                break;
        }
    }

    private void PressLeft(Point point, ModifierKeys modifiers, int clickCount)
    {
        if (clickCount == 2 && !IsSpacePanArmed)
        {
            if (HotspotAt(point) is { } shortcut)
            {
                shortcut.DoubleClick?.Invoke();
                return;
            }

            // Opened only when the first press was on it too: a first click
            // on a beacon or a circle flies the view, and whatever has slid
            // under the pointer since is not what was double-clicked.
            if (_pressHotspot is null
                && PointerHit(point) is { } hit && !hit.Folder.IsComputer
                && _pressHit is { } first && ReferenceEquals(first.Folder, hit.Folder) && first.FileName == hit.FileName)
            {
                OpenRequested?.Invoke(hit);
                if (!hit.IsFile)
                {
                    FlyTo(hit.Folder);
                    _selection.CurrentFolder = hit.Folder;
                }
            }

            return;
        }

        _press = PressKind.Left;
        _pressPoint = point;
        _pressMoved = false;
        _pressModifiers = modifiers;
        _pressHotspot = IsSpacePanArmed ? null : HotspotAt(point);
        _pressHit = PointerHit(point);
        (_pressIntent, _pressContainer) = ClassifyLeftPress(_pressHit, point, modifiers);
        TakeMouse();
        RequestFrame(Layers.Overlay);
    }

    /// <summary>Captures the mouse for a real press; a synthetic one leaves the real mouse alone.</summary>
    private void TakeMouse()
    {
        if (!_pressSynthetic)
        {
            CaptureMouse();
        }
    }

    /// <summary>
    /// What a left press becomes if it moves.  With Space held, a pan.  In
    /// the "select area" mode: something that can be picked up - a file tile
    /// big enough to carry its name, a folder's title band or its name pill -
    /// is dragged; anywhere else inside a folder draws a rectangle over that
    /// folder - its open space, a speck or a small tile inside it, This PC
    /// over its drives; outside This PC, a pan.  In the "pan" mode, as the
    /// canvas always was - a drag, or else a pan - unless Shift is held,
    /// which draws a rectangle wherever the other mode would have picked
    /// something up too.  A folder too small on screen to draw a rectangle in
    /// hands it to the folder around it.
    /// </summary>
    private (PressIntent Intent, NestedFolder? Container) ClassifyLeftPress(NestedHit? hit, Point point, ModifierKeys modifiers)
    {
        if (IsSpacePanArmed)
        {
            return (PressIntent.Pan, null);
        }

        var panMode = LeftDrag == NestedLeftDrag.Pan;
        if (panMode && (modifiers & ModifierKeys.Shift) == 0)
        {
            return (hit is { } grabbed && CanGrab(grabbed, point) ? PressIntent.Drag : PressIntent.Pan, null);
        }

        if (hit is not { } target || _tree is null)
        {
            return (PressIntent.Pan, null);
        }

        NestedFolder container;
        if (target.IsFile)
        {
            if (!panMode && CanGrab(target, point))
            {
                return (PressIntent.Drag, null);
            }

            container = target.Folder;
        }
        else if (!target.IsOnHeader || target.Folder.IsComputer)
        {
            container = target.Folder;
        }
        else if (!panMode && CanGrab(target, point))
        {
            return (PressIntent.Drag, null);
        }
        else
        {
            container = target.Folder.Parent ?? _tree.Root;
        }

        while (container.Parent is not null && CellOf(container, place: true) is { } cell && cell.W < MinimumContainerPixels)
        {
            container = container.Parent;
        }

        return (PressIntent.Marquee, container);
    }

    /// <summary>
    /// The pointer moved.  With no button down it is only hover.  A press
    /// that has not yet moved far enough - the system's drag distance for the
    /// left button, a little more for the right, so a shaky right click still
    /// gets its menu - stays a click; past it, it becomes what it was
    /// classified as.  A rectangle only takes the pointer here; what it
    /// touches is worked out in the frame.
    /// </summary>
    internal bool PointerMove(Point point)
    {
        if (_press == PressKind.None)
        {
            UpdateHover(point);
            return false;
        }

        if (!_pressMoved)
        {
            var dx = Math.Abs(point.X - _pressPoint.X);
            var dy = Math.Abs(point.Y - _pressPoint.Y);
            var still = _press == PressKind.Right
                ? dx < RightPanThreshold && dy < RightPanThreshold
                : dx < SystemParameters.MinimumHorizontalDragDistance && dy < SystemParameters.MinimumVerticalDragDistance;
            if (still)
            {
                return true;
            }

            _pressMoved = true;
            if (_press == PressKind.Left)
            {
                switch (_pressIntent)
                {
                    case PressIntent.Drag when _pressHit is { } hit:
                        // Grabbing a folder by its title, or a file by its
                        // tile, is picking it up.
                        _press = PressKind.None;
                        ReleaseMouseCapture();
                        DragRequested?.Invoke(hit.Path);
                        return true;
                    case PressIntent.Marquee when _pressContainer is { } container:
                        if (StartMarquee(container, _pressPoint, ModeFor(_pressModifiers)))
                        {
                            _marquee!.Pointer = point;
                            return true;
                        }

                        // Nowhere to draw it after all: a pan, as it always was.
                        _pressIntent = PressIntent.Pan;
                        break;
                    case PressIntent.Drag or PressIntent.Marquee:
                        _pressIntent = PressIntent.Pan;
                        break;
                }
            }

            if (_pressIntent == PressIntent.Pan)
            {
                StopFlight();
                _panLast = _pressPoint;
                Cursor = Cursors.SizeAll;
                _hover = null;
            }
        }

        if (_marquee is { } marquee)
        {
            marquee.Pointer = point;
            RequestFrame(Layers.Overlay);
            return true;
        }

        if (_pressIntent == PressIntent.Pan)
        {
            Pan(point - _panLast);
            _panLast = point;
            UserCameraMoved?.Invoke();
        }

        return true;
    }

    /// <summary>
    /// A button came up.  The left one lets go of a rectangle, or ends a pan,
    /// or - if it never moved - is a click; the right one ends its pan, or
    /// opens the menu if it never moved.  True when the event was the canvas's.
    /// </summary>
    internal bool PointerUp(MouseButton button, Point point)
    {
        switch (button)
        {
            case MouseButton.Left:
                if (_press != PressKind.Left)
                {
                    return false;
                }

                if (_marquee is { } marquee)
                {
                    marquee.Pointer = point;
                    CommitMarquee();
                    EndPress();
                    UpdateHover(point);
                    return true;
                }

                var moved = _pressMoved;
                var spent = _pressIntent == PressIntent.Spent;
                EndPress();
                if (moved || spent)
                {
                    UpdateHover(point);
                    return true;
                }

                if (_pressHotspot is { Click: { } click } && _pressHotspot.Bounds.Contains(point))
                {
                    click();
                    return true;
                }

                Click(_pressHit, _pressModifiers);
                return true;
            case MouseButton.Middle:
                if (_press != PressKind.Middle)
                {
                    return false;
                }

                EndPress();
                UpdateHover(point);
                return true;
            case MouseButton.Right:
                if (_press != PressKind.Right)
                {
                    // Not a right press of the canvas's own: one that went
                    // down somewhere else and came up here, one that went down
                    // while the left button was held, or one whose mouse was
                    // taken away (PointerLost) - none of them a click, so no
                    // menu.  Taken while another button's press is under way.
                    return _press != PressKind.None;
                }

                var dragged = _pressMoved;
                EndPress();
                if (dragged)
                {
                    UpdateHover(point);
                    return true;
                }

                RequestContextMenu(point);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The mouse was taken away mid-press - another window, Alt+Tab.  A
    /// rectangle keeps what it shows, as Explorer's does; anything else just
    /// stops, with no click.
    /// </summary>
    internal void PointerLost()
    {
        if (_press == PressKind.None)
        {
            return;
        }

        if (_marquee is not null)
        {
            CommitMarquee();
        }

        _press = PressKind.None;
        _pressIntent = PressIntent.Pan;
        _pressContainer = null;
        RestoreCursor();
        RequestFrame(Layers.Overlay);
    }

    /// <summary>
    /// The wheel, as Figma has it: Ctrl zooms at the pointer, Shift pans
    /// sideways, nothing pans up and down.  During a rectangle too - its
    /// start stays on its content, and its mode stays what the press made it.
    /// </summary>
    internal void PointerWheel(Point point, int delta, ModifierKeys modifiers)
    {
        RetryFailedFrames();
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            ZoomAt(point, Math.Pow(1.2, delta / 120.0));
        }
        else if ((modifiers & ModifierKeys.Shift) != 0)
        {
            Pan(new Vector(delta * 0.8, 0));
        }
        else
        {
            Pan(new Vector(0, delta * 0.8));
        }

        UserCameraMoved?.Invoke();
        UpdateHover(point);
    }

    /// <summary>
    /// The camera was moved by the user's own hand - the wheel, a drag that
    /// pans, a zoom key - and not by a flight, a camera put back or a change
    /// of size.  Raised after <see cref="CameraChanged"/> for the same move.
    /// A file dialog takes the folder the user brings it to for where it is.
    /// </summary>
    public event Action? UserCameraMoved;

    /// <summary>A zoom key or the window's zoom buttons: a step in or out about the middle of the view.</summary>
    internal void ZoomStep(bool zoomIn)
    {
        ZoomBy(zoomIn ? 1.5 : 1 / 1.5);
        UserCameraMoved?.Invoke();
    }

    /// <summary>For tests: what the press under way is, in words.</summary>
    internal string PressState =>
        _press == PressKind.None ? "idle" : $"{_press} {(_pressMoved ? "moved" : "pending")} {_pressIntent}{(_marquee is null ? string.Empty : " marquee")}";

    /// <summary>A right press this far from where it started pans: the drag distance, but never under 4 DIPs, so a shaky click still gets its menu.</summary>
    private static double RightPanThreshold => Math.Max(SystemParameters.MinimumHorizontalDragDistance, 4);

    /// <summary>
    /// A rectangle's mode from the keys held when it started: Ctrl toggles,
    /// Shift adds, nothing replaces.  In the "pan" mode Shift is what makes a
    /// rectangle at all, so there it replaces.
    /// </summary>
    private NestedSelectMode ModeFor(ModifierKeys modifiers) =>
        (modifiers & ModifierKeys.Control) != 0 ? NestedSelectMode.Toggle
        : (modifiers & ModifierKeys.Shift) != 0 && LeftDrag == NestedLeftDrag.SelectArea ? NestedSelectMode.Add
        : NestedSelectMode.Replace;

    /// <summary>
    /// A left click that never moved.  On an item: alone, or toggled with
    /// Ctrl, or a range from the anchor with Shift - added to the selection
    /// with Ctrl+Shift.  On This PC or outside it, a plain click lets go of
    /// the selection.  A click on a folder's open space selects the folder
    /// and makes it the one Ctrl+A fills.
    /// </summary>
    private void Click(NestedHit? pressed, ModifierKeys modifiers)
    {
        var ctrl = (modifiers & ModifierKeys.Control) != 0;
        var shift = (modifiers & ModifierKeys.Shift) != 0;
        if (pressed is not { } hit || hit.Folder.IsComputer || KeyOf(hit) is not { } key)
        {
            if (!ctrl && !shift)
            {
                ClearSelection();
            }

            return;
        }

        if (!shift || !SelectRange(key, add: ctrl))
        {
            if (ctrl)
            {
                ToggleItem(key);
            }
            else
            {
                SelectOnly(key);
            }

            SelectRequested?.Invoke(key.Path, ctrl);
        }

        _selection.CurrentFolder = !hit.IsFile && !hit.IsOnHeader ? hit.Folder : key.Container;
    }

    /// <summary>A right click that never moved: the menu for what is under it, as the window decides.</summary>
    private void RequestContextMenu(Point point)
    {
        var (target, onBackground) = MenuTargetAt(point);
        ContextMenuRequested?.Invoke(target, onBackground, point);
    }

    /// <summary>
    /// What a right-click at a point is about: null outside every cell or on
    /// This PC; otherwise what is under it, and whether that is a folder's
    /// open space - big enough to have one - rather than the folder itself.
    /// </summary>
    private (NestedHit? Hit, bool OnBackground) MenuTargetAt(Point point)
    {
        if (PointerHit(point) is not { } hit || hit.Folder.IsComputer)
        {
            return (null, true);
        }

        var onBackground = !hit.IsFile
            && !hit.IsOnHeader
            && hit.Bounds.Width * NestedLayout.HeaderHeight >= HeaderGrabPixels;
        return (hit, onBackground);
    }

    private bool CanGrab(NestedHit hit, Point point)
    {
        if (hit.IsFile)
        {
            return hit.Bounds.Height >= FileLabelPixels;
        }

        if (hit.Folder.Kind != NestedFolderKind.Folder)
        {
            return false;
        }

        var header = hit.Bounds.Width * NestedLayout.HeaderHeight;
        if (header >= HeaderGrabPixels && point.Y - hit.Bounds.Y <= header)
        {
            return true;
        }

        // A small cell's name sits on it as a pill; that pill is its handle.
        foreach (var spot in _labelHotspots)
        {
            if (ReferenceEquals(spot.Folder, hit.Folder) && spot.Bounds.Contains(point))
            {
                return true;
            }
        }

        return false;
    }

    private void EndPress()
    {
        _press = PressKind.None;
        _pressIntent = PressIntent.Pan;
        _pressContainer = null;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        RestoreCursor();
        RequestFrame(Layers.Overlay);
    }

    /// <summary>
    /// The cursor once a press is over: the open hand while Space is still
    /// held - the window set it when Space went down, and only puts it back
    /// when Space comes up - otherwise the canvas's own.
    /// </summary>
    private void RestoreCursor()
    {
        if (IsSpacePanArmed)
        {
            Cursor = Cursors.Hand;
        }
        else
        {
            ClearValue(CursorProperty);
        }
    }

    private void UpdateHover(Point point)
    {
        var wasOnTip = HotspotAt(_hoverPoint) is { Tip: not null };
        _hoverPoint = point;
        var spot = HotspotAt(point);
        if (spot is { Tip: not null })
        {
            _hover = null;
            RenderOverlay();
            return;
        }

        if (wasOnTip)
        {
            // Off an arrow: its tag goes, and the ordinary hover takes over.
            _hover = null;
            RenderOverlay();
        }

        var hit = spot is null ? PointerHit(point) : null;
        var same = hit is { } now && _hover is { } before
            && ReferenceEquals(now.Folder, before.Folder)
            && now.FileIndex == before.FileIndex;
        var showsTip = hit is { } shown && (shown.IsFile ? shown.Bounds.Height < FileLabelPixels : !_labelled.Contains(shown.Folder));
        if (!same || showsTip || hit is null != _hover is null)
        {
            _hover = hit;
            RenderOverlay();
        }
    }

    /// <summary>
    /// What the pointer is on, as the user sees it: a small folder's name is
    /// drawn over the top of its first sub-folders, and a press on that name
    /// means the folder whose name it is, not whatever lies under the letters.
    /// </summary>
    private NestedHit? PointerHit(Point point)
    {
        for (var index = _labelHotspots.Count - 1; index >= 0; index--)
        {
            var spot = _labelHotspots[index];
            if (spot.Bounds.Contains(point) && ScreenRect(spot.Folder) is { } cell)
            {
                return new NestedHit(spot.Folder, cell, IsOnHeader: true);
            }
        }

        return HitTest(point);
    }

    private Hotspot? HotspotAt(Point point)
    {
        for (var index = _hotspots.Count - 1; index >= 0; index--)
        {
            if (_hotspots[index].Click is not null && _hotspots[index].Bounds.Contains(point))
            {
                return _hotspots[index];
            }
        }

        return null;
    }

    private void AddGrabHotspot(NestedFolder folder, Rect bounds) =>
        _labelHotspots.Add(new LabelGrab(bounds, folder));

    /// <summary>
    /// Selects one path alone, at once - a match or a mark gone to, the folder
    /// Backspace goes up to - and hands the window the change.  A path the
    /// canvas cannot find is left to the window.
    /// </summary>
    private void MarkSelected(string path)
    {
        TakeSetSelection();
        if (Resolve(path) is not { } target || target.FileIndex < 0 && target.Folder.Parent is null)
        {
            return;
        }

        var key = KeyOf(target);
        SelectOnly(key);
        _selection.CurrentFolder = key.Container;
    }

    /// <summary>
    /// The next folder or file in the direction of an arrow: sub-folders move
    /// over their parent's grid, files over their folder's.
    /// </summary>
    private (NestedFolder Folder, int FileIndex)? Neighbour((NestedFolder Folder, int FileIndex)? active, Key key)
    {
        // Next along is next in the order on screen: the folders whose grids
        // are walked are placed for it first.
        if (active is { } placed)
        {
            Ensure(placed.Folder);
            if (placed.Folder.Parent is { } holder)
            {
                Ensure(holder);
            }
        }

        if (active is not { } current || current.FileIndex < 0 && (current.Folder.Parent is null || current.Folder.Index < 0))
        {
            // Nothing selected yet: start at the first folder in the one in
            // view, or its first file if it holds only files.  The focus on
            // an item that went - deleted, moved away - starts in the folder
            // it was in instead, wherever the view is.
            var start = active is null && _selection.Active is { Container: var left }
                && !NestedTree.IsDetached(left) && NestedTree.IsOnCanvas(left)
                ? left
                : _anchor;
            if (start is not null)
            {
                Ensure(start);
            }

            return start switch
            {
                { Children.Count: > 0 } anchor => (anchor.Children[0], -1),
                { Files.Count: > 0 } anchor => (anchor, 0),
                _ => null
            };
        }

        if (current.FileIndex >= 0)
        {
            var folder = current.Folder;
            var files = folder.FileGrid;
            var nextFile = Step(current.FileIndex, files.Columns, files.Rows, files.DownFirst, folder.Files.Count, key);
            if (nextFile >= 0 && nextFile < folder.Files.Count)
            {
                return (folder, nextFile);
            }

            if (key == Key.Up && folder.Children.Count > 0)
            {
                // Read down first, up from the first file is the sub-folder
                // before it in the order: the last one.
                if (files.DownFirst)
                {
                    return current.FileIndex == 0 ? (folder.Children[^1], -1) : null;
                }

                // Across, up from the first row of files is the last row of
                // sub-folders above them, the one nearest along the row.
                if (current.FileIndex < files.Columns)
                {
                    var (fx, _) = files.Origin(current.FileIndex);
                    return (Nearest(folder.Children, folder.Grid, fx + files.TileWidth / 2, lastRow: true), -1);
                }
            }

            return null;
        }

        var parent = current.Folder.Parent!;
        var grid = parent.Grid;
        var next = Step(current.Folder.Index, grid.Columns, grid.Rows, grid.DownFirst, parent.Children.Count, key);
        if (next >= 0 && next < parent.Children.Count)
        {
            return (parent.Children[next], -1);
        }

        // Read down first, down from the last sub-folder is the first file,
        // the next item in the order.
        if (grid.DownFirst)
        {
            return key == Key.Down && parent.Files.Count > 0 && current.Folder.Index == parent.Children.Count - 1
                ? (parent, 0)
                : null;
        }

        // Down past the last row of sub-folders is the first row of the files under them.
        if (key == Key.Down && parent.Files.Count > 0 && current.Folder.Index / grid.Columns == grid.Rows - 1)
        {
            var centre = current.Folder.OffsetX + current.Folder.Scale / 2;
            var files = parent.FileGrid;
            var column = Math.Clamp((int)Math.Floor((centre - files.Left) / files.StepX), 0, Math.Max(0, files.Columns - 1));
            return (parent, Math.Min(column, parent.Files.Count - 1));
        }

        return null;
    }

    /// <summary>The child in a grid's first or last row nearest to <paramref name="x"/>.</summary>
    private static NestedFolder Nearest(IReadOnlyList<NestedFolder> children, NestedGrid grid, double x, bool lastRow)
    {
        var row = lastRow ? grid.Rows - 1 : 0;
        var first = row * grid.Columns;
        var last = Math.Min(children.Count - 1, first + grid.Columns - 1);
        var column = Math.Clamp((int)Math.Floor((x - grid.Left) / grid.StepX), 0, last - first);
        return children[first + column];
    }

    /// <summary>
    /// The place an arrow moves to in a grid of <paramref name="count"/>
    /// items, before it is checked against the ends.  Read across, left and
    /// right are the item before and after and up and down a row's length
    /// away; read down first, up and down are the item before and after and
    /// left and right a column's length away - and right from a row the
    /// short last column does not reach is its last item, as Explorer's
    /// List view has it, rather than nowhere.
    /// </summary>
    internal static int Step(int index, int columns, int rows, bool downFirst, int count, Key key)
    {
        if (!downFirst)
        {
            return key switch
            {
                Key.Left => index - 1,
                Key.Right => index + 1,
                Key.Up => index - columns,
                _ => index + columns
            };
        }

        switch (key)
        {
            case Key.Up:
                return index - 1;
            case Key.Down:
                return index + 1;
            case Key.Left:
                return index - rows;
            default:
                var next = index + rows;
                return next >= count && rows > 0 && index / rows < (count - 1) / rows ? count - 1 : next;
        }
    }

    /// <summary>
    /// Brings what an arrow key moved to into view.  Readable but off screen,
    /// the view only slides - the zoom the user chose stays; too small to read,
    /// it flies to where it can be read.
    ///
    /// <para>A flight still under way - a key held down repeats faster than
    /// one lands - lands at once first, so the move is measured from where
    /// the view was going.  Started from the middle of the last flight
    /// instead, every repeat began from a standstill and from the zoom that
    /// flight's arc had dipped to: a held key left the item further behind
    /// with each repeat, and the view shrank a little more each time.</para>
    /// </summary>
    private void EnsureVisible(NestedFolder folder, int fileIndex = -1)
    {
        LandFlight();
        var rect = TargetRect(folder, fileIndex);
        var readable = rect is { } r && (fileIndex >= 0 ? r.Height >= FileLabelPixels : r.Width >= 48);
        if (!readable)
        {
            FlyToReadable(folder, fileIndex);
            return;
        }

        const double margin = 24;
        var target = rect!.Value;
        var dx = Shift(target.Left, target.Right, _viewWidth, margin);
        var dy = Shift(target.Top, target.Bottom, _viewHeight, margin);
        if (dx == 0 && dy == 0)
        {
            return;
        }

        if (ScreenRect(folder) is { } cell)
        {
            FlyToRect(folder, (cell.X + dx, cell.Y + dy, cell.Width));
        }
    }

    /// <summary>The smallest slide along one axis that brings [low, high] inside [margin, size - margin].</summary>
    private static double Shift(double low, double high, double size, double margin)
    {
        if (high - low > size - 2 * margin)
        {
            // Bigger than the view: line its start up with the view's.
            return margin - low;
        }

        if (low < margin)
        {
            return margin - low;
        }

        return high > size - margin ? size - margin - high : 0;
    }

    /// <summary>Which button's press is under way.</summary>
    private enum PressKind
    {
        None,
        Left,
        Middle,
        Right
    }

    /// <summary>What a press turns into once it moves past the drag distance.</summary>
    private enum PressIntent
    {
        /// <summary>The view follows the pointer.</summary>
        Pan,

        /// <summary>The item pressed is picked up for a real file drag.</summary>
        Drag,

        /// <summary>A selection rectangle over <see cref="_pressContainer"/>.</summary>
        Marquee,

        /// <summary>A rectangle Esc let go of while the button is still down: the rest of the press does nothing.</summary>
        Spent
    }
}
