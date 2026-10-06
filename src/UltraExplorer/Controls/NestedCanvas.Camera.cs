using System.Windows;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

// The camera: a folder and that folder's rectangle on screen (see the
// class's own summary) - how it zooms, pans and flies, how it is kept in
// bounds and re-anchored after every move, and how any other folder's
// rectangle is worked out from it.
public sealed partial class NestedCanvas
{
    /// <summary>
    /// Whether a wheel notch, a key step or a released drag eases into place
    /// over a few frames rather than landing at once.  Off - every move
    /// instant, as it has always been - until eased motion is built in;
    /// flights are animated either way.
    /// </summary>
    public bool SmoothMotion { get; set; }

    // A named camera move may still be reading when the user chooses another
    // destination. Only the latest request may apply its result.
    private long _cameraRequest;

    /// <summary>
    /// What the camera being put back reads, on its way and once it is back
    /// (<see cref="RestoreCameraAsync"/>); null when nothing is.  Let go of by
    /// any newer request for the camera.
    /// </summary>
    private CancellationTokenSource? _cameraReads;

    /// <summary>
    /// A new request for the camera - a move of the user's, a flight, a
    /// camera put back, another tree: only the newest may move the camera once
    /// its reads are done, and whatever a camera being put back still had to
    /// read for it is let go.
    /// </summary>
    private long NextCameraRequest()
    {
        if (_cameraReads is { } reads)
        {
            _cameraReads = null;
            reads.Cancel();
        }

        return ++_cameraRequest;
    }

    // ---- camera --------------------------------------------------------------

    /// <summary>Shows all of This PC.</summary>
    public void FitAll(bool animated = true)
    {
        NextCameraRequest();
        if (_tree is null)
        {
            return;
        }

        if (_viewWidth <= 0)
        {
            // No size yet: This PC would be framed in a view of none - a
            // speck 40 DIPs wide - and kept that way, as a camera somebody
            // chose.  It is framed instead once the canvas knows its size
            // (EnsureCamera), and framed again for each size until somebody
            // moves it, as the first view always is.
            StopFlight();
            _anchor = _tree.Root;
            _hasCamera = false;
            RequestFrame(Layers.All);
            return;
        }

        if (!animated)
        {
            StopFlight();
            _anchor = _tree.Root;
            (_ax, _ay, _aw) = OverviewFitRect();
            _hasCamera = true;
            _cameraTouched = true;
            AfterCameraMove();
            return;
        }

        FlyTo(_tree.Root, 1, animated: true);
    }

    /// <summary>
    /// Moves the camera so <paramref name="target"/> fills <paramref name="fill"/>
    /// of the view, along the path that keeps the most of both ends in sight -
    /// out, across and in again when they are far apart.
    /// </summary>
    public bool FlyTo(NestedFolder target, double fill = 0.92, bool animated = true)
    {
        EnsureCamera();
        var current = ScreenRectOf(target);
        if (current is null || _viewWidth <= 0)
        {
            return false;
        }

        return FlyToRect(target, target.IsComputer && fill >= 1 ? OverviewFitRect() : FitRect(fill), animated);
    }

    /// <summary>Moves the camera so <paramref name="target"/> ends up exactly at <paramref name="end"/> on screen.</summary>
    private bool FlyToRect(NestedFolder target, (double X, double Y, double W) end, bool animated = true)
    {
        EnsureCamera();
        var current = ScreenRectOf(target);
        if (current is null || _viewWidth <= 0 || !(end.W > 0)
            || !double.IsFinite(end.X) || !double.IsFinite(end.Y) || !double.IsFinite(end.W))
        {
            return false;
        }

        NextCameraRequest();
        if (!animated)
        {
            StopFlight();
            _anchor = target;
            _ax = end.X;
            _ay = end.Y;
            _aw = end.W;
            _cameraTouched = true;
            AfterCameraMove();
            return true;
        }

        _flight = Flight.Create(target, current.Value, end, _viewWidth, _viewHeight);
        _cameraTouched = true;
        RequestFrame(Layers.All);
        return true;
    }

    /// <summary>
    /// Flies to where a folder or a file can actually be read: a file with its
    /// tile tall enough for its name, a folder a third of the view wide - each
    /// centred.  Fitting the container instead left a file among thousands
    /// just as unreadable as before, and every arrow press flew to the same
    /// view again.
    /// </summary>
    private void FlyToReadable(NestedFolder folder, int fileIndex)
    {
        // The tile comes from the folder's own placing and the cell from its
        // parent's: both for the current order.
        Ensure(folder);
        if (folder.Parent is { } holder)
        {
            Ensure(holder);
        }

        if (fileIndex >= 0 && fileIndex < folder.Files.Count && folder.FileGrid is { IsEmpty: false } files)
        {
            var width = 2 * FileLabelPixels / files.TileHeight;
            width = Math.Max(width, FitRect(0.92).W);
            var (fx, fy) = files.Origin(fileIndex);
            var cx = fx + files.TileWidth / 2;
            var cy = fy + files.TileHeight / 2;
            FlyToRect(folder, (_viewWidth / 2 - cx * width, _viewHeight / 2 - cy * width, width));
            return;
        }

        if (folder.Parent is not { IsComputer: false } parent)
        {
            FlyTo(folder, 0.6);
            return;
        }

        var wanted = Math.Max(160, Math.Min(_viewWidth, _viewHeight * NestedLayout.Aspect) * 0.35);
        var parentWidth = wanted / folder.Scale;
        var centreX = folder.OffsetX + folder.Scale / 2;
        var centreY = folder.OffsetY + folder.Scale * NestedLayout.CellHeight / 2;
        FlyToRect(parent, (_viewWidth / 2 - centreX * parentWidth, _viewHeight / 2 - centreY * parentWidth, parentWidth));
    }

    /// <summary>Frames the requested file card tightly, or a folder whole. Unlike arrow navigation this is an explicit, normally instant focus.</summary>
    public async Task<bool> FocusPathAsync(string path, bool? isDirectory = null,
        Func<bool>? requestCurrent = null, bool animated = false)
    {
        if (_tree is not { } tree || string.IsNullOrWhiteSpace(path) || requestCurrent?.Invoke() == false) return false;
        var request = NextCameraRequest();
        using var reads = new CancellationTokenSource();
        _cameraReads = reads;
        bool Current() => ReferenceEquals(tree, _tree) && request == _cameraRequest
            && !reads.IsCancellationRequested && requestCurrent?.Invoke() != false;
        try
        {
            var directory = isDirectory;
            if (directory is null)
            {
                if (tree.Find(path) is not null) directory = true;
                else if (Path.GetDirectoryName(path) is { } knownParent && tree.Find(knownParent) is { } known
                    && NestedTree.HoldsFile(known, Path.GetFileName(path))) directory = false;
                else
                {
                    var entry = await tree.DescribeNamedEntryAsync(path, reads.Token).WaitAsync(reads.Token);
                    if (!Current()) return false;
                    directory = entry.Kind != ViewAllEntryKind.File;
                }
            }
            var folderPath = directory == true ? path : Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(folderPath) || !Current()) return false;
            var folder = await tree.MaterializePathAsync(folderPath, reads.Token).WaitAsync(reads.Token);
            if (folder is null || !Current() || !ViewAllPath.Equals(folder.FullPath, folderPath)) return false;
            var index = -1;
            if (directory != true)
            {
                await tree.LoadAsync(folder, reads.Token);
                if (!Current() || !folder.IsLoaded) return false;
                if (!await tree.EnsureNamedFileAsync(folder, path, reads.Token, Current) || !Current()) return false;
                // Local reveal only. The window's stored Layers preference
                // stays unchanged, as for Show in Explorer file invocations.
                if (!Shows(CanvasLayer.Files)) ShownLayers |= CanvasLayer.Files;
                if (!Current()) return false;
                tree.EnsureLayout(folder);
                index = tree.FindFileIndex(folder, Path.GetFileName(path));
                if (index < 0) return false;
            }
            if (!Current() || NestedTree.IsDetached(folder) || !NestedTree.IsOnCanvas(folder)) return false;
            UpdateLayout();
            if (ActualWidth > 0 && ActualHeight > 0) { _viewWidth = ActualWidth; _viewHeight = ActualHeight; }
            if (!(_viewWidth > 0 && _viewHeight > 0)) return false;
            if (directory == true)
            {
                var moved = FlyTo(folder, 0.88, animated);
                AttachFocusGuard(moved, animated, tree, requestCurrent);
                return moved;
            }

            if (!TryFileFocusEnd(folder, index, out var end)) return false;
            var moved = FlyToRect(folder, end, animated);
            if (moved && animated && _flight is { } flight)
            {
                // Unlike a folder flight, a file flight aims at one tile inside
                // its folder. A sort, filter or live listing can move that tile
                // while the smooth flight is under way, and the selection or
                // active pane can change after FocusPathAsync has returned.
                // Remember just enough to validate or retarget on a later frame;
                // generic folder, tag and arrow flights keep their old path.
                flight.FocusGuard = new FocusFlightGuard(tree, _cameraRequest, requestCurrent);
                flight.PreciseFile = new PreciseFileFlight(
                    Path.GetFileName(path),
                    folder.Files,
                    folder.FileGrid,
                    folder.LayoutSortGeneration,
                    index,
                    _viewWidth,
                    _viewHeight);
            }

            return moved;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            if (ReferenceEquals(_cameraReads, reads)) _cameraReads = null;
        }
    }

    private void AttachFocusGuard(bool moved, bool animated, NestedTree tree, Func<bool>? requestCurrent)
    {
        if (moved && animated && _flight is { } flight)
        {
            flight.FocusGuard = new FocusFlightGuard(tree, _cameraRequest, requestCurrent);
        }
    }

    private bool TryFileFocusEnd(NestedFolder folder, int index, out (double X, double Y, double W) end)
    {
        var files = folder.FileGrid;
        if (files.IsEmpty || index < 0 || index >= folder.Files.Count
            || !(files.TileWidth > 0 && files.TileHeight > 0))
        {
            end = default;
            return false;
        }

        var width = Math.Min(_viewWidth * 0.75 / files.TileWidth, _viewHeight * 0.75 / files.TileHeight);
        var (x, y) = files.Origin(index);
        end = (_viewWidth / 2 - (x + files.TileWidth / 2) * width,
            _viewHeight / 2 - (y + files.TileHeight / 2) * width,
            width);
        return double.IsFinite(end.X) && double.IsFinite(end.Y) && double.IsFinite(end.W) && end.W > 0;
    }

    /// <summary>
    /// Keeps an F flight aimed at its exact file without making every flight
    /// resolve a path every frame. The common case is only reference and stamp
    /// comparisons. A changed placement does one indexed lookup and restarts
    /// the remaining smooth journey from the camera's current rectangle.
    /// </summary>
    private bool PreparePreciseFlight(Flight flight, out Flight prepared)
    {
        prepared = flight;
        var guard = flight.FocusGuard;
        if (guard is not null
            && (!ReferenceEquals(_tree, guard.Tree)
                || guard.CameraRequest != _cameraRequest
                || guard.RequestCurrent?.Invoke() == false))
        {
            StopFlight();
            return false;
        }

        if (flight.PreciseFile is not { } precise)
        {
            return true;
        }

        if (guard is null)
        {
            StopFlight();
            return false;
        }

        var folder = flight.Target;
        var tree = guard.Tree;
        if (tree.SortGeneration != folder.LayoutSortGeneration)
        {
            tree.EnsureLayout(folder);
        }

        if (ReferenceEquals(precise.Files, folder.Files)
            && precise.Grid == folder.FileGrid
            && precise.LayoutSortGeneration == folder.LayoutSortGeneration
            && precise.ViewWidth == _viewWidth
            && precise.ViewHeight == _viewHeight)
        {
            return true;
        }

        var index = tree.FindFileIndex(folder, precise.Name);
        if (index < 0 || !TryFileFocusEnd(folder, index, out var end))
        {
            StopFlight();
            return false;
        }

        // A metadata-only refresh often replaces the file array without
        // moving this tile. Acknowledge it without restarting the animation.
        if (index == precise.Index && precise.Grid == folder.FileGrid
            && precise.ViewWidth == _viewWidth && precise.ViewHeight == _viewHeight)
        {
            precise.Capture(folder, index, _viewWidth, _viewHeight);
            return true;
        }

        if (ScreenRectOf(folder) is not { } current)
        {
            StopFlight();
            return false;
        }

        var replacement = Flight.Create(folder, current, end, _viewWidth, _viewHeight);
        precise.Capture(folder, index, _viewWidth, _viewHeight);
        replacement.FocusGuard = guard;
        replacement.PreciseFile = precise;
        _flight = replacement;
        prepared = replacement;
        return true;
    }

    /// <summary>Reads everything on the way to <paramref name="path"/>, then flies to it.</summary>
    public async Task<bool> FlyToPathAsync(string path, double fill = 0.72, bool animated = true)
    {
        if (_tree is not { } tree)
        {
            return false;
        }

        var request = NextCameraRequest();
        var folder = await tree.RevealAsync(path);
        if (folder is null || !ReferenceEquals(tree, _tree) || request != _cameraRequest)
        {
            return false;
        }

        return FlyTo(folder, fill, animated);
    }

    /// <summary>Zooms by <paramref name="factor"/> keeping the point under <paramref name="at"/> still.</summary>
    public void ZoomAt(Point at, double factor)
    {
        if (!(factor > 0) || !double.IsFinite(factor) || !double.IsFinite(at.X) || !double.IsFinite(at.Y))
            return;
        EnsureCamera();
        if (_anchor is null || !double.IsFinite(_aw * factor)
            || !double.IsFinite(at.X + (_ax - at.X) * factor)
            || !double.IsFinite(at.Y + (_ay - at.Y) * factor))
        {
            return;
        }

        NextCameraRequest();
        StopFlight();
        ZoomAround(at, factor);
        Normalize();
        ClampZoom(at);
        ClampPan();
        _cameraTouched = true;
        AfterCameraMove();
    }

    public void ZoomBy(double factor) => ZoomAt(new Point(_viewWidth / 2, _viewHeight / 2), factor);

    /// <summary>
    /// Moves the view by <paramref name="delta"/>.  A pan of nothing does
    /// nothing at all: it would otherwise let go of a camera still on its way
    /// - a flight reading its way there, one being put back - for no move.
    /// </summary>
    public void Pan(Vector delta)
    {
        if (!double.IsFinite(delta.X) || !double.IsFinite(delta.Y) || delta.X == 0 && delta.Y == 0) return;
        EnsureCamera();
        if (_anchor is null || !double.IsFinite(_ax + delta.X) || !double.IsFinite(_ay + delta.Y))
        {
            return;
        }

        NextCameraRequest();
        StopFlight();
        _ax += delta.X;
        _ay += delta.Y;
        Normalize();
        ClampPan();
        _cameraTouched = true;
        AfterCameraMove();
    }

    /// <summary>
    /// The camera as something that can be saved, or null when there is none
    /// to save (never shown, no size yet).  A camera on This PC itself is saved
    /// with an empty path, so an overview comes back as an overview.
    /// </summary>
    public NestedCameraState? CaptureCamera()
    {
        if (_anchor is null || !_hasCamera || _viewWidth <= 0)
        {
            return null;
        }

        var centreX = _viewWidth / 2;
        var centreY = _viewHeight / 2;
        return new NestedCameraState(
            _anchor.IsComputer ? string.Empty : _anchor.FullPath,
            (_ax - centreX) / _viewWidth,
            (_ay - centreY) / _viewWidth,
            _aw / _viewWidth);
    }

    /// <summary>
    /// Puts the camera back where a previous session left it, once the folders
    /// on the way there have been found - unless the user has moved it since.
    ///
    /// <para>They are found by name, as a folder opened by name is: described,
    /// not listed - one look at the disk for the whole way, rather than a full
    /// listing of every folder on it, each applied a frame or more after the
    /// last while the window showed This PC and then jumped.  Once the camera
    /// is back they are listed, nearest first, so what is around it fills in
    /// as it always did (<see cref="ListFoldersOnTheWayAsync"/>).  A way that
    /// cannot be found by name - a folder gone since, a tree not on a disk -
    /// is read the whole way down as before.  The user moving the camera, or
    /// anything else asking for it, lets go of all of these reads.</para>
    /// </summary>
    public async Task RestoreCameraAsync(NestedCameraState state)
    {
        if (_tree is not { } tree || !(state.Width > 0)
            || !double.IsFinite(state.Width) || !double.IsFinite(state.X) || !double.IsFinite(state.Y))
        {
            return;
        }

        var request = NextCameraRequest();
        _cameraTouched = false;
        var isRoot = string.IsNullOrWhiteSpace(state.AnchorPath);
        var reads = new CancellationTokenSource();
        _cameraReads = reads;
        NestedFolder? folder;
        try
        {
            folder = isRoot
                ? tree.Root
                : await tree.MaterializeContainerAsync(state.AnchorPath, reads.Token) ?? await tree.RevealAsync(state.AnchorPath, reads.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // The canvas may have been handed another tree while the folders were
        // read: a folder of the old one is no camera for it.
        if (folder is null
            || !ReferenceEquals(tree, _tree)
            || request != _cameraRequest
            || _cameraTouched
            || !isRoot && !ViewAllPath.Equals(folder.FullPath, state.AnchorPath)
            || _viewWidth <= 0)
        {
            if (ReferenceEquals(_cameraReads, reads))
            {
                _cameraReads = null;
            }

            return;
        }

        // Materializing the path can outlast a split/collapse layout. Its
        // ActualSize may already be current while the deferred SizeChanged
        // notification still leaves our camera cache at the old pane width.
        // A restored normalized camera belongs to the current view, not that
        // earlier width (otherwise a late share returns at half its zoom).
        UpdateLayout();
        if (ActualWidth > 0 && ActualHeight > 0)
        {
            _viewWidth = ActualWidth;
            _viewHeight = ActualHeight;
        }
        var width = state.Width * _viewWidth;
        var x = _viewWidth / 2 + state.X * _viewWidth;
        var y = _viewHeight / 2 + state.Y * _viewWidth;
        if (!double.IsFinite(width) || !double.IsFinite(x) || !double.IsFinite(y))
        {
            _cameraReads = null;
            return;
        }

        StopFlight();
        _anchor = folder;
        _aw = width;
        _ax = x;
        _ay = y;
        _hasCamera = true;
        Normalize();
        ClampZoom(new Point(_viewWidth / 2, _viewHeight / 2));
        ClampPan();
        AfterCameraMove();

        // A camera put back is one somebody chose, like one they moved: a
        // resize keeps it rather than framing everything afresh - which an
        // overview, anchored on This PC, would otherwise lose at once.
        _cameraTouched = true;
        await ListFoldersOnTheWayAsync(tree, folder, reads);
    }

    /// <summary>
    /// The camera is back on <paramref name="folder"/>, reached by name: the
    /// folders on the way to it, found without being listed, are listed now -
    /// the nearest first, which is most of what is around the view - so the
    /// view ends with what it always had around it.  Anything asking for the
    /// camera meanwhile lets go of the rest: a folder the view is inside is
    /// then listed when the camera goes into it, as after any folder opened
    /// by name.
    /// </summary>
    private async Task ListFoldersOnTheWayAsync(NestedTree tree, NestedFolder folder, CancellationTokenSource reads)
    {
        try
        {
            for (var parent = folder.Parent; parent is { IsComputer: false }; parent = parent.Parent)
            {
                await tree.LoadAsync(parent, reads.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Let go of by a newer request for the camera.
        }
        finally
        {
            if (ReferenceEquals(_cameraReads, reads))
            {
                _cameraReads = null;
            }
        }
    }

    /// <summary>The cell of <paramref name="folder"/> on screen, or null when it is not one of the cells.</summary>
    public Rect? ScreenRectOf(NestedFolder folder)
    {
        EnsureCamera();
        if (_anchor is null)
        {
            return null;
        }

        BuildChain();
        var result = RectOf(folder);
        return result is { } r ? new Rect(r.X, r.Y, r.W, r.W * NestedLayout.CellHeight) : null;
    }

    /// <summary>A folder's cell or a file's tile on screen.</summary>
    /// <param name="place">Whether what it reads is placed for the current order first (see <see cref="RectOf"/>).</param>
    private Rect? TargetRect(NestedFolder folder, int fileIndex, bool place = true)
    {
        if (RectOf(folder, place) is not { } r)
        {
            return null;
        }

        if (fileIndex < 0)
        {
            return new Rect(r.X, r.Y, r.W, r.W * NestedLayout.CellHeight);
        }

        if (place)
        {
            Ensure(folder);
        }

        if (fileIndex >= folder.FileGrid.Count)
        {
            return null;
        }

        PlaceFile(folder, fileIndex, r.X, r.Y, r.W, out var fx, out var fy, out var fw, out var fh);
        return new Rect(fx, fy, fw, fh);
    }

    // ---- camera internals ----------------------------------------------------

    private void EnsureCamera()
    {
        if (_hasCamera || _tree is null)
        {
            return;
        }

        _viewWidth = ActualWidth;
        _viewHeight = ActualHeight;
        if (_viewWidth <= 0)
        {
            _viewWidth = 1200;
            _viewHeight = 800;
        }

        _anchor = _tree.Root;
        (_ax, _ay, _aw) = OverviewFitRect();
        _hasCamera = true;
    }

    private double FitWidth()
    {
        const double margin = 18;
        return Math.Max(40, Math.Min(_viewWidth - 2 * margin,
            (_viewHeight - 2 * margin) / (NestedLayout.CellHeight + FavoriteStripHeight)));
    }

    private (double X, double Y, double W) FitRect(double fill)
    {
        var width = fill >= 1
            ? Math.Max(40, Math.Min(_viewWidth - 36, (_viewHeight - 36) * NestedLayout.Aspect))
            : Math.Min(_viewWidth * fill, _viewHeight * fill * NestedLayout.Aspect);
        return ((_viewWidth - width) / 2, (_viewHeight - width * NestedLayout.CellHeight) / 2, width);
    }

    private void ZoomAround(Point at, double factor)
    {
        _ax = at.X + (_ax - at.X) * factor;
        _ay = at.Y + (_ay - at.Y) * factor;
        _aw *= factor;
    }

    /// <summary>Re-chooses the anchor so it is the deepest folder that holds the view's centre and is at least half as wide as the view.</summary>
    private void Normalize()
    {
        if (_anchor is null)
        {
            return;
        }

        // A rename is an identity change, not a reason to occupy the sibling
        // now in the old slot. Keep the anchor's screen rectangle unchanged.
        if (_tree is { } tree && NestedTree.IsDetached(_anchor)
            && tree.RenameSuccessor(_anchor) is { } successor)
        {
            _anchor = successor;
            _chain.Clear();
        }

        // Walking up divides by the anchor's own place in its parent, and
        // walking down reads its children's: all of them for the current order.
        EnsureAnchorPath();

        // A folder that was refreshed away or filtered out cannot be the anchor.
        // Filtered out, or inside something that was: hiding a folder the
        // view is deep inside must take the view out of it.
        while (_anchor.Parent is not null && (!NestedTree.IsOnCanvas(_anchor) || NestedTree.IsDetached(_anchor)))
        {
            // If a descendant really went away, its renamed surviving ancestor
            // is still the right place to climb to, not an unrelated sibling.
            if (_tree is { } renamedTree && renamedTree.RenameSuccessor(_anchor) is { } renamedAnchor)
            {
                _anchor = renamedAnchor;
                _chain.Clear();
                EnsureAnchorPath();
                if (NestedTree.IsOnCanvas(_anchor) && !NestedTree.IsDetached(_anchor)) break;
            }
            Up();
        }

        var centreX = _viewWidth / 2;
        var centreY = _viewHeight / 2;
        var half = _viewWidth / 2;
        for (var guard = 0; guard < 512; guard++)
        {
            if (_anchor.Parent is not null && (_aw < half || !Inside(new Point(centreX, centreY), _ax, _ay, _aw)))
            {
                Up();
                continue;
            }

            Ensure(_anchor);
            var grid = _anchor.Grid;
            if (!grid.IsEmpty)
            {
                var index = grid.IndexAt((centreX - _ax) / _aw, (centreY - _ay) / _aw);
                if (index >= 0 && index < _anchor.Children.Count)
                {
                    var child = _anchor.Children[index];
                    Place(_anchor, child, _ax, _ay, _aw, out var childX, out var childY, out var childWidth);
                    if (childWidth >= half)
                    {
                        _ax = childX;
                        _ay = childY;
                        _aw = childWidth;
                        _anchor = child;
                        continue;
                    }
                }
            }

            break;
        }
    }

    private void Up()
    {
        var folder = _anchor!;
        PlaceParent(folder, _ax, _ay, _aw, out var parentX, out var parentY, out var parentWidth);
        _ax = parentX;
        _ay = parentY;
        _aw = parentWidth;
        _anchor = folder.Parent;
    }

    /// <summary>
    /// Keeps This PC from shrinking to a dot, and the view from diving into a
    /// folder with nothing in it: there is nothing further in to see.
    /// </summary>
    private void ClampZoom(Point at)
    {
        var root = RootRect();
        if (root is { } rect)
        {
            var minimum = FitWidth() * 0.25;
            if (rect.W < minimum)
            {
                ZoomAround(at, minimum / rect.W);
                Normalize();
            }
        }

        if (_anchor is null)
        {
            return;
        }

        // Nothing further in: an empty folder need not fill more than the
        // screen.  A folder of files goes as far in as it takes for its
        // smallest names to be read and picked up.
        Ensure(_anchor);
        var isDeadEnd = _anchor.Children.Count == 0
            && (_anchor.LoadState is NestedLoadState.Loaded or NestedLoadState.Failed || _anchor.IsReparsePoint);
        var maximum = !isDeadEnd
            ? 1e7 * _viewWidth
            : _anchor.FileGrid is { IsEmpty: false } files
                ? Math.Max(3 * _viewWidth, 4 * FileLabelPixels / files.TileHeight)
                : 3 * _viewWidth;
        if (_aw > maximum)
        {
            ZoomAround(at, maximum / _aw);
            Normalize();
        }
    }

    /// <summary>Keeps at least a strip of This PC on screen, so the view cannot be lost in the dark.</summary>
    private void ClampPan()
    {
        var root = RootRect();
        if (root is not { } rect)
        {
            return;
        }

        const double keep = 48;
        var height = rect.W * NestedLayout.CellHeight;
        var dx = 0.0;
        var dy = 0.0;
        if (rect.X > _viewWidth - keep) dx = _viewWidth - keep - rect.X;
        if (rect.X + rect.W < keep) dx = keep - rect.X - rect.W;
        var top = rect.Y - rect.W * FavoriteStripHeight;
        if (top > _viewHeight - keep) dy = _viewHeight - keep - top;
        if (rect.Y + height < keep) dy = keep - rect.Y - height;
        if (dx != 0 || dy != 0)
        {
            _ax += dx;
            _ay += dy;
            Normalize();
        }
    }

    private (double X, double Y, double W)? RootRect()
    {
        if (_anchor is null)
        {
            return null;
        }

        EnsureAnchorPath();
        var (x, y, w) = (_ax, _ay, _aw);
        for (var folder = _anchor; folder.Parent is not null; folder = folder.Parent)
        {
            PlaceParent(folder, x, y, w, out x, out y, out w);
        }

        return (x, y, w);
    }

    /// <summary>The anchor and every folder above it, each with its exact rectangle.</summary>
    private void BuildChain()
    {
        EnsureAnchorPath();
        BuildChainAsPlaced();
    }

    /// <summary>
    /// <see cref="BuildChain"/> from wherever the folders on the way happen to
    /// be placed, without placing any for the current order first: the chain
    /// as the last frame drew it, which is what a change of order has to take
    /// its bearings from before anything moves.
    /// </summary>
    private void BuildChainAsPlaced()
    {
        _chain.Clear();
        if (_anchor is null)
        {
            return;
        }

        var (x, y, w) = (_ax, _ay, _aw);
        var folder = _anchor;
        _chain[folder] = (x, y, w);
        while (folder.Parent is not null)
        {
            PlaceParent(folder, x, y, w, out x, out y, out w);
            folder = folder.Parent;
            _chain[folder] = (x, y, w);
        }
    }

    /// <param name="place">
    /// Whether each folder on the way is placed for the current order first.
    /// What the user acts on is found where the current order puts it; what
    /// is only drawn over the picture - a mark, the selection's outline - is
    /// drawn where the picture has it, which for a folder the frame had no
    /// allowance left to place is where it was.
    /// </param>
    private (double X, double Y, double W)? RectOf(NestedFolder folder, bool place = true)
    {
        var path = new List<NestedFolder>();
        var current = folder;
        (double X, double Y, double W) rect;
        while (!_chain.TryGetValue(current, out rect))
        {
            if (current.Index < 0 || current.IsForgotten || current.Parent is null)
            {
                return null;
            }

            path.Add(current);
            current = current.Parent;
        }

        for (var index = path.Count - 1; index >= 0; index--)
        {
            // Each step's place is its parent's placing, taken from the top
            // down so every offset is for the current order.
            var step = path[index];
            if (place)
            {
                Ensure(step.Parent!);
            }

            Place(step.Parent!, step, rect.X, rect.Y, rect.W, out var x, out var y, out var w);
            rect = (x, y, w);
        }

        return rect;
    }

    /// <summary>
    /// Has the tree place a folder for the current order before its children,
    /// grids or files are read.  Nearly always it already is, and this is one
    /// comparison; a folder never read has nothing to place.
    /// </summary>
    private void Ensure(NestedFolder folder)
    {
        var stamp = folder.LayoutSortGeneration;
        if (stamp >= 0 && _tree is { } tree && stamp != tree.SortGeneration)
        {
            tree.EnsureLayout(folder);
        }
    }

    /// <summary>
    /// <see cref="Ensure"/> for the anchor and every folder above it, from the
    /// top down: the camera divides its way up through their places, so a
    /// chain placed for two different orders would put the view somewhere
    /// neither of them has it.
    /// </summary>
    private void EnsureAnchorPath()
    {
        if (_anchor is not null && _tree is not null)
        {
            _tree.EnsurePathLayout(_anchor);
        }
    }

    /// <summary>
    /// The order changed, and nothing has moved yet.  The folder being looked
    /// at is held where it is on screen - at the rectangle it has right now -
    /// and only then is its way up placed for the new order, so its contents
    /// reorder inside it and whatever moves above it moves around it.
    ///
    /// Which folder that is: the anchor when it fills most of the view, which
    /// is what flying into a folder leaves - the folder at nine tenths of the
    /// screen, its parent covering the rest.  Holding the parent there instead
    /// would put a sibling in the folder's place, in front of the user, the
    /// moment they clicked a header.  Otherwise the view is a look over a
    /// folder's grid rather than into one of its cells, and the deepest folder
    /// covering the view is held, its cells trading places inside it.  Only an
    /// overview covered by nothing is held on This PC, whose drives never
    /// change places.
    /// </summary>
    private void OnTreeSortChanged()
    {
        // A hover names a tile by its index, and the tile it names is about to
        // hold another file.
        _hover = null;
        if (_tree is { } tree && _hasCamera && _anchor is not null && _flight is null && _viewWidth > 0)
        {
            BuildChainAsPlaced();
            var (cover, x, y, w, _) = CoverCell();
            if (_aw < _viewWidth * FillsViewShare && !ReferenceEquals(cover, _anchor))
            {
                _anchor = cover;
                _ax = x;
                _ay = y;
                _aw = w;
            }

            tree.EnsurePathLayout(_anchor);
            BuildChainAsPlaced();
        }

        // The matches were listed in the order before: stepping through them
        // follows the new one at once (see GoToMatch), and the list itself is
        // put in it once the tree has placed everything.
        if (_filter is not null)
        {
            _ = ReorderFilterWhenPlacedAsync();
        }

        RequestFrame(Layers.All);
    }

    /// <summary>How much of the view's width a folder takes up for a change of order to hold it, rather than the folder around it, still.</summary>
    private const double FillsViewShare = 0.75;

    /// <summary>
    /// The folder the view is on, by the rule a change of order holds still
    /// (see <see cref="OnTreeSortChanged"/>): the anchor when it fills most
    /// of the view, otherwise the deepest folder covering the whole of it.
    /// Null for an overview of This PC, which is no folder.  A walk up from
    /// the anchor through the rectangles its way up has at the camera now,
    /// so it is cheap enough to ask after every move of the camera.
    /// </summary>
    public NestedFolder? FolderInView
    {
        get
        {
            if (_tree is null || !_hasCamera || _anchor is null || _viewWidth <= 0)
            {
                return null;
            }

            if (_aw >= _viewWidth * FillsViewShare)
            {
                return _anchor.IsComputer ? null : _anchor;
            }

            // The rectangles are those of the camera now, not of the last
            // picture: asked straight after a move - as the sort headers ask
            // on every one - those still had the folder just left covering
            // the view.  Taken as the folders are placed, so asking never
            // places anything, even between a change of order and the canvas
            // taking its bearings from it (OnTreeSortChanged).
            BuildChainAsPlaced();
            for (var folder = _anchor; folder is not null; folder = folder.Parent)
            {
                if (_chain.TryGetValue(folder, out var rect) && Covers(rect.X, rect.Y, rect.W))
                {
                    return folder.IsComputer ? null : folder;
                }
            }

            return null;
        }
    }

    private void AfterCameraMove()
    {
        _lastMotion = System.Diagnostics.Stopwatch.GetTimestamp();
        _hoverStale = true;
        RequestFrame(Layers.All);
        CameraChanged?.Invoke();
    }

    private static bool Inside(Point point, double x, double y, double w) =>
        point.X >= x && point.X <= x + w && point.Y >= y && point.Y <= y + w * NestedLayout.CellHeight;

    // ---- flights -------------------------------------------------------------

    private void StopFlight() => _flight = null;

    /// <summary>
    /// Puts the camera where the flight under way was taking it, at once, as
    /// the flight's last frame would have; nothing without one.  A flight to
    /// a folder that has gone since just stops, as it would on its next frame.
    /// </summary>
    private void LandFlight()
    {
        if (_flight is not { } flight)
        {
            return;
        }

        if (NestedTree.IsDetached(flight.Target) || !NestedTree.IsOnCanvas(flight.Target))
        {
            StopFlight();
            return;
        }

        // Arrow navigation lands a flight before it brings the next item into
        // view. An F flight must first honour the same stale-request guard and
        // precise file endpoint as an ordinary animation frame; otherwise the
        // arrow would cut to the old selection, or a sort/resize's old tile.
        if (!PreparePreciseFlight(flight, out flight))
        {
            return;
        }

        StopFlight();
        flight.End(_viewWidth, _viewHeight, out var x, out var y, out var w);
        _anchor = flight.Target;
        _ax = x;
        _ay = y;
        _aw = w;
        _hasCamera = true;
        Normalize();
        AfterCameraMove();
    }

    /// <summary>
    /// A smooth zoom-and-pan in the sense of van Wijk and Nuij: the path
    /// through (centre, width) space along which the apparent motion is
    /// least.  Expressed in the target's own frame, where the target is one
    /// unit wide, so the numbers stay sane however far apart the ends are.
    /// </summary>
    private sealed class Flight
    {
        private const double Rho = 1.42;
        private double _c0x, _c0y, _c1x, _c1y, _w0, _w1, _u1, _r0, _length;
        private bool _isPureZoom;
        private long _started;
        private double _duration;

        public required NestedFolder Target { get; init; }

        /// <summary>
        /// Present only for plain-F focus, so a later selection or pane wins
        /// after the asynchronous command has already started the animation.
        /// </summary>
        public FocusFlightGuard? FocusGuard { get; set; }

        /// <summary>
        /// Present only for plain-F file focus. Folder, tag, favourite and
        /// keyboard-navigation flights deliberately retain their original
        /// fixed-end behaviour.
        /// </summary>
        public PreciseFileFlight? PreciseFile { get; set; }

        public static Flight Create(
            NestedFolder target,
            Rect current,
            (double X, double Y, double W) end,
            double viewWidth,
            double viewHeight)
        {
            // World units are target widths: the target spans [0, 1].
            var flight = new Flight { Target = target };
            var centreX = viewWidth / 2;
            var centreY = viewHeight / 2;
            flight._w0 = viewWidth / current.Width;
            flight._c0x = (centreX - current.X) / current.Width;
            flight._c0y = (centreY - current.Y) / current.Width;
            flight._w1 = viewWidth / end.W;
            flight._c1x = (centreX - end.X) / end.W;
            flight._c1y = (centreY - end.Y) / end.W;

            var dx = flight._c1x - flight._c0x;
            var dy = flight._c1y - flight._c0y;
            flight._u1 = Math.Sqrt(dx * dx + dy * dy);
            if (flight._u1 < 1e-9 * Math.Max(flight._w0, flight._w1))
            {
                flight._isPureZoom = true;
                flight._length = Math.Abs(Math.Log(flight._w1 / flight._w0)) / Rho;
            }
            else
            {
                var rho2 = Rho * Rho;
                var rho4 = rho2 * rho2;
                var b0 = (flight._w1 * flight._w1 - flight._w0 * flight._w0 + rho4 * flight._u1 * flight._u1)
                    / (2 * flight._w0 * rho2 * flight._u1);
                var b1 = (flight._w1 * flight._w1 - flight._w0 * flight._w0 - rho4 * flight._u1 * flight._u1)
                    / (2 * flight._w1 * rho2 * flight._u1);
                // log(sqrt(b^2 + 1) - b) is -asinh(b), and only the second
                // survives the ratios a deep jump produces: at b ~ 1e8 the first
                // cancels to nothing and the flight collapses into a cut.
                flight._r0 = -Math.Asinh(b0);
                var r1 = -Math.Asinh(b1);
                flight._length = (r1 - flight._r0) / Rho;
            }

            if (double.IsNaN(flight._length) || double.IsInfinity(flight._length))
            {
                flight._isPureZoom = true;
                flight._length = Math.Abs(Math.Log(flight._w1 / flight._w0)) / Rho;
                if (double.IsNaN(flight._length) || double.IsInfinity(flight._length))
                {
                    flight._length = 0;
                }
            }

            // Long journeys take longer, but never so long that it feels slow.
            flight._duration = Math.Clamp(0.22 + 0.16 * Math.Abs(flight._length), 0.25, 1.1);
            flight._started = System.Diagnostics.Stopwatch.GetTimestamp();
            return flight;
        }

        /// <summary>The target's rectangle at this moment; true once the flight has arrived.</summary>
        public bool Sample(double viewWidth, double viewHeight, out double x, out double y, out double w)
        {
            // The system tick moves in 15.6 ms steps: at 120 Hz most frames
            // would read the same time and the camera would stutter.
            var t = System.Diagnostics.Stopwatch.GetElapsedTime(_started).TotalSeconds / _duration;
            var done = t >= 1;
            t = Math.Clamp(t, 0, 1);

            // Ease in and out, so the view does not jolt at either end.
            var eased = t * t * (3 - 2 * t);
            var s = eased * _length;

            double centreX, centreY, width;
            if (done)
            {
                (centreX, centreY, width) = (_c1x, _c1y, _w1);
            }
            else if (_isPureZoom)
            {
                // Straight in or out: the width changes geometrically, which is
                // what looks like a steady zoom.
                width = _length == 0
                    ? _w0 + (_w1 - _w0) * eased
                    : _w0 * Math.Exp(Math.Log(_w1 / _w0) * eased);
                centreX = _c0x + (_c1x - _c0x) * eased;
                centreY = _c0y + (_c1y - _c0y) * eased;
            }
            else
            {
                var rho2 = Rho * Rho;
                var coshR0 = Math.Cosh(_r0);
                var u = _w0 / rho2 * coshR0 * Math.Tanh(Rho * s + _r0) - _w0 / rho2 * Math.Sinh(_r0);
                width = _w0 * coshR0 / Math.Cosh(Rho * s + _r0);
                var fraction = u / _u1;
                centreX = _c0x + (_c1x - _c0x) * fraction;
                centreY = _c0y + (_c1y - _c0y) * fraction;
            }

            w = viewWidth / width;
            x = viewWidth / 2 - centreX * w;
            y = viewHeight / 2 - centreY * w;
            return done;
        }

        /// <summary>The target's rectangle once the flight has arrived: where <see cref="Sample"/> puts it at the end.</summary>
        public void End(double viewWidth, double viewHeight, out double x, out double y, out double w)
        {
            w = viewWidth / _w1;
            x = viewWidth / 2 - _c1x * w;
            y = viewHeight / 2 - _c1y * w;
        }
    }

    private sealed record FocusFlightGuard(
        NestedTree Tree,
        long CameraRequest,
        Func<bool>? RequestCurrent);

    private sealed class PreciseFileFlight(
        string name,
        IReadOnlyList<NestedFile> files,
        NestedFileGrid grid,
        int layoutSortGeneration,
        int index,
        double viewWidth,
        double viewHeight)
    {
        public string Name { get; } = name;
        public IReadOnlyList<NestedFile> Files { get; private set; } = files;
        public NestedFileGrid Grid { get; private set; } = grid;
        public int LayoutSortGeneration { get; private set; } = layoutSortGeneration;
        public int Index { get; private set; } = index;
        public double ViewWidth { get; private set; } = viewWidth;
        public double ViewHeight { get; private set; } = viewHeight;

        public void Capture(NestedFolder folder, int fileIndex, double width, double height)
        {
            Files = folder.Files;
            Grid = folder.FileGrid;
            LayoutSortGeneration = folder.LayoutSortGeneration;
            Index = fileIndex;
            ViewWidth = width;
            ViewHeight = height;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RetryFailedFrames();

        // Collapsed (the tree canvas is showing): nothing to frame, and the
        // camera stays as it was for when the canvas comes back.
        if (sizeInfo.NewSize.Width <= 0 || sizeInfo.NewSize.Height <= 0)
        {
            return;
        }

        if (_hasCamera && _viewWidth > 0 && _viewHeight > 0)
        {
            // Keep the middle of the view on the same spot of the same folder -
            // measured from the size the camera was actually set up for, which
            // before the first layout is a placeholder, not the previous size.
            var dx = (sizeInfo.NewSize.Width - _viewWidth) / 2;
            var dy = (sizeInfo.NewSize.Height - _viewHeight) / 2;
            _ax += dx;
            _ay += dy;
        }

        _viewWidth = sizeInfo.NewSize.Width;
        _viewHeight = sizeInfo.NewSize.Height;

        // The first "everything" was framed before the control knew its size;
        // until somebody moves it, frame it again for the size it really is.
        if (!_cameraTouched && _tree is not null && ReferenceEquals(_anchor, _tree.Root))
        {
            _hasCamera = false;
        }

        RequestFrame(Layers.All);
    }

    private Rect? ScreenRect(NestedFolder folder, bool place = true)
    {
        var result = RectOf(folder, place);
        return result is { } r ? new Rect(r.X, r.Y, r.W, r.W * NestedLayout.CellHeight) : null;
    }
}
