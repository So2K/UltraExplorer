using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

// The scene: one walk from the folder that covers the view down through
// everything on screen and big enough to see, the cells and file tiles it
// hands a sink, and the two places that picture goes - the bitmap the CPU
// raster fills, or the GPU's surface, with everything it takes to keep
// that surface on the card that drives the window's monitor.
public sealed partial class NestedCanvas
{
    /// <summary>
    /// The cells: one walk from the outermost folder that still covers the
    /// whole view down through everything on screen and big enough to see,
    /// filling rectangles into the frame's target - the GPU's instances when
    /// the canvas is in a window whose card is ready, the raster's bitmap
    /// otherwise, or a test's offscreen texture.  The GPU's frame is
    /// presented at the end of the frame (<see cref="PresentScene"/>).
    /// </summary>
    private void RenderScene(bool inMotion, OffscreenScene? offscreen = null)
    {
        using var frame = PerfLog.Measure("nested.frame");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _tree!.BeginFrame();

        var pixelWidth = Math.Max(1, (int)Math.Ceiling(_viewWidth * _scaleX));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(_viewHeight * _scaleY));

        // Motion LOD, the way a game drops detail it cannot afford in a frame:
        // while the camera moves, cells and file tiles below a size that grows
        // when frames run long are left out; the frame after it stops draws
        // them all again.  At rest the picture is always complete.
        var lod = inMotion ? _lod : 1;
        _minCell = MinimumCellPixels * lod;
        _minTile = 2.5 * lod;
        _relayoutAllowance = _inFrameLoop ? DrawRelayoutTicks : long.MaxValue;

        // The one place a frame's target is chosen: the GPU when it can be
        // used, else the bitmap painted by hand - and if the GPU fails half
        // way through a frame, the bitmap for that same frame.  Each brings
        // its own sink to WalkScene; everything before and after is the same
        // for all.
        if (offscreen is { } scene)
        {
            PaintSceneOffscreen(scene, pixelWidth, pixelHeight, inMotion);
        }
        else if (!TryWalkSceneForGpu(pixelWidth, pixelHeight))
        {
            PaintSceneIntoBitmap(pixelWidth, pixelHeight, inMotion ? null : ChangedPixels(pixelWidth, pixelHeight));
        }

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (inMotion)
        {
            // A 120 Hz frame is 8 ms, and the labels and WPF's own work need
            // their share of it.
            _lod = elapsed > 4.5 ? Math.Min(_lod * 1.6, 16) : elapsed < 2 ? Math.Max(1, _lod / 1.25) : _lod;
        }
        else
        {
            _lod = 1;
        }

        _lodDegraded = lod > 1.0001;
        PerfLog.Value("nested.cells", DrawnCellCount);
        LastRenderMilliseconds = elapsed;
        LastPlacingMilliseconds = _relayoutSpent * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        DrewOutOfDate = _drewStale;
        RenderCount++;
        if (_drewStale)
        {
            // Some folders were drawn in their previous order: the tree's pass
            // places those first, and the next frame places the next few
            // milliseconds' worth itself.
            _tree.PlaceFirst(_drawnStale);
            _drawnStale.Clear();
            RequestFrame(Layers.Scene);
        }
    }

    /// <summary>
    /// The scene painted into the bitmap the scene layer shows, through
    /// <see cref="RasterSink"/>: the canvas's own picture, and the one any
    /// other target is measured against.
    ///
    /// With a <paramref name="clip"/> only those pixels are painted and sent
    /// to WPF, and everything else in the bitmap is left as the last frame
    /// painted it - the picture after folders were read, when only their
    /// cells changed (see <see cref="ChangedPixels"/>).  The walk is the same
    /// whole walk, so the names, outlines and reads it gathers are too; only
    /// the pixels are clipped, and inside the clip they are exactly the ones a
    /// whole frame paints there.
    /// </summary>
    private void PaintSceneIntoBitmap(int pixelWidth, int pixelHeight, Int32Rect? clip = null)
    {
        // Grown in steps and reused.  Dragging a window edge is a new size on
        // every step, and a new full-screen bitmap each time - tens of
        // megabytes of native memory freed only by a full collection - was
        // what made a live resize stutter.  The part past the control is
        // simply never drawn into, and clipped away.
        if (_bitmap is null
            || _bitmap.PixelWidth < pixelWidth
            || _bitmap.PixelHeight < pixelHeight
            || (long)_bitmap.PixelWidth * _bitmap.PixelHeight > 3L * pixelWidth * pixelHeight + 2_000_000)
        {
            _bitmap = new WriteableBitmap(
                (pixelWidth + 255) / 256 * 256,
                (pixelHeight + 255) / 256 * 256,
                96 * _scaleX,
                96 * _scaleY,
                PixelFormats.Pbgra32,
                null);
            _bitmapGeneration++;
        }

        if (!ReferenceEquals(_shownBitmap, _bitmap))
        {
            // The layer holds the bitmap itself; from then on only its pixels
            // change, and the layer never has to be recorded again.
            _shownBitmap = _bitmap;
            _shownSurface = null;
            using var dc = _sceneVisual.RenderOpen();
            dc.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelWidth / _scaleX, _bitmap.PixelHeight / _scaleY));
        }

        // A clip only means something over a whole picture of the same view
        // in the same bitmap; anything else is painted whole.
        if (clip is not null && !IsWholeRaster(pixelWidth, pixelHeight))
        {
            clip = null;
        }

        var area = clip ?? new Int32Rect(0, 0, pixelWidth, pixelHeight);
        _bitmap.Lock();
        try
        {
            _rasterSink.Begin(_bitmap.BackBuffer, _bitmap.BackBufferStride, area);
            var walkStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            WalkScene(_rasterSink);
            LastWalkMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(walkStarted).TotalMilliseconds;
            LastPresentMilliseconds = 0;
            LastLockMilliseconds = 0;
            LastGpuWaitMilliseconds = 0;
            _bitmap.AddDirtyRect(area);
        }
        finally
        {
            _rasterSink.End();
            _bitmap.Unlock();
        }

        LastSceneClip = clip ?? Int32Rect.Empty;
        if (clip is null)
        {
            _wholeRaster = (_bitmapGeneration, pixelWidth, pixelHeight);
            return;
        }

        ClippedSceneCount++;
        if (_sceneBudgetSpent || _relayoutSpent > 0 || _drewStale)
        {
            // The walk placed folders for a new order, or ran out of its
            // allowance for cells or tiles, which may have moved where others
            // ran out too: pixels outside the clip may no longer be what a
            // whole frame paints there.  The next frame paints them all.
            RequestFrame(Layers.Scene);
        }
    }

    // ---- painting only what changed ---------------------------------------------

    /// <summary>
    /// Which bitmap - by the count of bitmaps made, so a bitmap let go of is
    /// not kept alive by this - and what size in pixels the last whole raster
    /// painted: a clipped frame paints over that picture and nothing else.
    /// </summary>
    private (int Bitmap, int Width, int Height) _wholeRaster = (-1, 0, 0);

    /// <summary>Bitmaps made so far; the one in use is the last.</summary>
    private int _bitmapGeneration;

    /// <summary>
    /// Whether the walk under way ran out of its allowance for cells or file
    /// tiles, which draws what it could not afford as washes - or not at all -
    /// wherever the allowance happens to run out.
    /// </summary>
    private bool _sceneBudgetSpent;

    /// <summary>
    /// Pixels round the changed cells' own that a clipped frame paints as
    /// well: a cell's edges are rounded to pixels, and its corners shaded,
    /// within a pixel of where its rectangle says.
    /// </summary>
    private const int ChangedMarginPixels = 2;

    /// <summary>A clip is aligned to blocks of this many pixels, an even number (see <see cref="RasterSink.Begin"/>).</summary>
    private const int ChangedAlignPixels = 8;

    /// <summary>For tests and the bench: the pixels the last CPU scene painted, or empty when it painted the whole view.</summary>
    internal Int32Rect LastSceneClip { get; private set; } = Int32Rect.Empty;

    /// <summary>For the bench: CPU scenes that painted only the pixels that changed.</summary>
    internal long ClippedSceneCount { get; private set; }

    private bool IsWholeRaster(int pixelWidth, int pixelHeight) =>
        _bitmap is not null
        && _wholeRaster.Bitmap == _bitmapGeneration
        && ReferenceEquals(_shownBitmap, _bitmap)
        && _wholeRaster.Width == pixelWidth
        && _wholeRaster.Height == pixelHeight;

    /// <summary>
    /// The pixels of the frame's scene that changed, when only some did: the
    /// cells of the folders read since the last frame
    /// (<see cref="_sceneChangedArea"/>), widened by a margin and to blocks of
    /// eight pixels, within the view.  Null - paint everything - when the
    /// whole scene changed, when they are half the view or more, and whenever
    /// a folder read can change pixels outside its own cell: with the name
    /// filter on, while a change of order is still being placed, or over a
    /// picture drawn with less detail for motion.
    /// </summary>
    private Int32Rect? ChangedPixels(int pixelWidth, int pixelHeight)
    {
        var area = _sceneChangedArea;
        if (area.IsEmpty || _filter is not null || _lodDegraded || _tree is null || _tree.IsSorting)
        {
            return null;
        }

        var left = Math.Clamp(Math.Floor(area.Left * _scaleX) - ChangedMarginPixels, 0, pixelWidth);
        var top = Math.Clamp(Math.Floor(area.Top * _scaleY) - ChangedMarginPixels, 0, pixelHeight);
        var right = Math.Clamp(Math.Ceiling(area.Right * _scaleX) + ChangedMarginPixels, 0, pixelWidth);
        var bottom = Math.Clamp(Math.Ceiling(area.Bottom * _scaleY) + ChangedMarginPixels, 0, pixelHeight);
        var x0 = (int)left / ChangedAlignPixels * ChangedAlignPixels;
        var y0 = (int)top / ChangedAlignPixels * ChangedAlignPixels;
        var x1 = Math.Min(pixelWidth, ((int)right + ChangedAlignPixels - 1) / ChangedAlignPixels * ChangedAlignPixels);
        var y1 = Math.Min(pixelHeight, ((int)bottom + ChangedAlignPixels - 1) / ChangedAlignPixels * ChangedAlignPixels);
        if (x1 <= x0 || y1 <= y0 || (long)(x1 - x0) * (y1 - y0) * 2 > (long)pixelWidth * pixelHeight)
        {
            return null;
        }

        return new Int32Rect(x0, y0, x1 - x0, y1 - y0);
    }

    // ---- the GPU -----------------------------------------------------------------

    /// <summary>
    /// The scene for the GPU, when <see cref="GpuBootstrap.Decide"/> says the
    /// card that drives the window's monitor is ready: the walk fills the
    /// frame's instances through <see cref="GpuSink"/>, to be put on the
    /// texture the scene layer shows - where the bitmap used to be - by
    /// <see cref="PresentScene"/> at the end of the frame.  The names go to
    /// the same texture through <see cref="GpuLabelTarget"/> once the atlases
    /// are ready (<see cref="TryDrawLabelsOnGpu"/>); the marks, outlines,
    /// trail and pointer's layer above it stay WPF's.
    ///
    /// False when the GPU does not draw this frame, and the caller paints the
    /// bitmap instead: no window, the CPU chosen, or the card not ready yet -
    /// the canvas moves over at the first frame after it is.  A surface that
    /// is drawing already keeps drawing while the card for the window's
    /// monitor is still being prepared - the monitors being matched again
    /// after a display change, the window just moved onto another card's
    /// monitor - rather than dropping a working picture to the CPU for the
    /// moment; the canvas moves to the right set when it is ready.
    /// </summary>
    private bool TryWalkSceneForGpu(int pixelWidth, int pixelHeight)
    {
        if (_surfaceLost)
        {
            ReleaseGpu(lost: true);
        }

        var decision = GpuBootstrap.Decide(this);
        RendererReason = decision.Reason;
        var devices = decision.DeviceSet;
        if (devices is null
            && ReferenceEquals(decision.Reason, GpuBootstrap.ReasonWarmingUp)
            && _surface is { IsLost: false } kept
            && !kept.Devices.IsDisposed
            && kept.ScaleX == _scaleX
            && kept.ScaleY == _scaleY)
        {
            devices = kept.Devices;
            ScheduleGpuRetry();
        }

        if (devices is null || !EnsureSurface(devices))
        {
            if (_surface is not null)
            {
                ReleaseGpu(lost: false);
            }

            if (IsWorthAskingAgain(RendererReason))
            {
                ScheduleGpuRetry();
            }

            return false;
        }

        var surface = _surface!;
        surface.EnsureSize(pixelWidth, pixelHeight);
        if (surface.IsLost)
        {
            ReleaseGpu(lost: true);
            ScheduleGpuRetry();
            return false;
        }

        var frame = _gpuFrame ??= new NestedGpuFrame();
        var sink = _gpuSink ??= new GpuSink();
        var walkStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        sink.Begin(frame.SceneRects, pixelWidth, pixelHeight, CanvasColour);
        WalkScene(sink);
        frame.ClearColour = sink.ClearColour;
        frame.SceneChanged();
        LastWalkMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(walkStarted).TotalMilliseconds;
        _presentPending = true;
        return true;
    }

    /// <summary>
    /// Puts the frame's instances on screen: one present of the surface -
    /// locked, drawn, waited for, unlocked - in the same WPF batch as the
    /// layers recorded above it, so the names never lag the cells.  False
    /// when WPF's render thread still held the previous frame; the whole
    /// frame is drawn again on the next tick.  A device lost here costs
    /// nothing on screen: the same frame is painted into the bitmap at once.
    /// </summary>
    private bool PresentScene()
    {
        _presentPending = false;
        if (_surface is not { } surface)
        {
            return true;
        }

        var result = surface.Present(_drawSurface ??= DrawSurface);
        LastPresentMilliseconds = surface.LastPresentMilliseconds;
        LastLockMilliseconds = surface.LastLockMilliseconds;
        LastGpuWaitMilliseconds = surface.LastGpuWaitMilliseconds;
        LastRenderMilliseconds += surface.LastPresentMilliseconds;
        if (!surface.Devices.IsDisposed)
        {
            LastGpuMilliseconds = surface.Devices.Timer.LastGpuMilliseconds;
        }

        switch (result)
        {
            case PresentResult.Presented:
                LastGpuInstances = _renderer!.LastInstances;
                LastGpuGlyphs = _renderer.LastGlyphs;
                LastGpuIcons = _renderer.LastIcons;
                LastUploadMilliseconds += _renderer.LastUploadMilliseconds;
                ShowSurface(surface);
                if (_labelsOnGpu && _labelVisualRecorded)
                {
                    // The names are in the GPU's picture from this frame on;
                    // WPF's recording of them goes in the same batch, so no
                    // frame shows both or neither.
                    _labelVisual.RenderOpen().Close();
                    _labelVisualRecorded = false;
                }

                return true;

            case PresentResult.Skipped:
                // The names drawn over this frame are for a camera the cells
                // do not show yet; the next tick draws both again.
                SkippedPresents++;
                RequestFrame(Layers.All);
                return false;

            case PresentResult.Unavailable:
                // WPF has no front buffer - the lock screen, a UAC prompt -
                // so nothing drawn now would be seen.  The surface says when
                // it is back (ContentLost), and a whole frame follows.
                return true;

            default:
                // Lost in the middle of this frame: the same frame goes into
                // the bitmap - the walk again - and its names to WPF, and the
                // GPU is tried again once the bootstrap has a new set for the
                // card.
                var labelsWereOnGpu = _labelsOnGpu;
                ReleaseGpu(lost: true);
                PaintSceneIntoBitmap(ScenePixelWidth, ScenePixelHeight);
                if (labelsWereOnGpu)
                {
                    DrawLabelsWithWpf();
                }

                ScheduleGpuRetry();
                return true;
        }
    }

    /// <summary>
    /// A surface on <paramref name="devices"/> at the canvas's DPI, made anew
    /// when the card or the DPI changed - the window moved to a monitor on
    /// another card or of another scale - together with the card's renderer.
    /// False when the card cannot make them, which counts as losing it.
    /// </summary>
    private bool EnsureSurface(GpuDeviceSet devices)
    {
        if (_surface is { IsLost: false } current
            && ReferenceEquals(current.Devices, devices)
            && current.ScaleX == _scaleX
            && current.ScaleY == _scaleY)
        {
            return true;
        }

        ReleaseGpu(lost: _surface?.IsLost == true);
        try
        {
            _renderer = NestedGpuRenderer.For(devices);
            _surface = new NestedSurface(devices, _scaleX, _scaleY);
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or GpuUnavailableException or ObjectDisposedException or ArgumentException)
        {
            // Shaders that will not load or buffers the card will not make
            // are as good as a lost device: the bootstrap tries the card
            // again later, and after three failures a minute leaves the
            // canvas on the CPU for the session.
            _renderer = null;
            RendererReason = GpuBootstrap.ReasonUnavailable;
            if (!devices.IsDisposed)
            {
                GpuBootstrap.ReportDeviceLost(devices);
            }

            return false;
        }

        _surface.DeviceLost += OnSurfaceDeviceLost;
        _surface.ContentLost += OnSurfaceContentLost;
        if (_labelsOnGpu && !ReferenceEquals(_gpuFrame?.LabelDevices, devices))
        {
            // The names on the GPU were drawn for another card, whose atlas
            // views the renderer will not sample here; a frame that draws
            // only the scene (a folder read) would leave their icons and
            // glyphs out until something else drew the names again.
            RequestFrame(Layers.Labels);
        }

        return true;
    }

    /// <summary>
    /// Records the scene layer with the surface, once per surface and per
    /// texture it hands WPF - after the present that handed it over, so the
    /// rectangle drawn is always the texture WPF holds.  The bitmap is not
    /// drawn while the GPU is, and its tens of megabytes go with it.
    /// </summary>
    private void ShowSurface(NestedSurface surface)
    {
        if (ReferenceEquals(_shownSurface, surface) && _shownSurfaceVersion == surface.SurfaceVersion)
        {
            return;
        }

        _shownSurface = surface;
        _shownSurfaceVersion = surface.SurfaceVersion;
        _shownBitmap = null;
        _bitmap = null;
        using var dc = _sceneVisual.RenderOpen();
        dc.DrawImage(surface, surface.DrawRect);
    }

    /// <summary>The renderer's part of a present: GPU commands only, the instances filled before the lock.</summary>
    private void DrawSurface(in SurfaceFrame frame) => _renderer!.Draw(frame.Target, frame.Width, frame.Height, _gpuFrame!);

    /// <summary>
    /// Gives up the surface: back to the bitmap, which the next CPU frame
    /// records in the scene layer again.  A lost card is also handed back to
    /// the bootstrap, which throws its set away and makes a new one.
    /// </summary>
    private void ReleaseGpu(bool lost)
    {
        var surface = _surface;
        _surface = null;
        _renderer = null;
        _surfaceLost = false;
        if (surface is null)
        {
            return;
        }

        surface.DeviceLost -= OnSurfaceDeviceLost;
        surface.ContentLost -= OnSurfaceContentLost;
        var devices = surface.Devices;
        surface.Dispose();
        if (lost && !devices.IsDisposed)
        {
            GpuBootstrap.ReportDeviceLost(devices);
        }
    }

    /// <summary>The surface's device failed: the next frame lets it go and draws on the CPU.</summary>
    private void OnSurfaceDeviceLost(object? sender, EventArgs e)
    {
        _surfaceLost = true;
        RequestFrame(Layers.All);
    }

    /// <summary>WPF's front buffer came back (after the lock screen, say): nothing on the surface can be trusted.</summary>
    private void OnSurfaceContentLost(object? sender, EventArgs e) => RequestFrame(Layers.All);

    /// <summary>
    /// Listens, while the canvas is in a window, for what can change where
    /// its scene is drawn without anything on the canvas changing: a card
    /// finishing its warm-up, the renderer setting, the window moving to a
    /// monitor on another card, and the monitors matched to their cards
    /// again after a display change.
    /// </summary>
    private void HookGpu()
    {
        if (_gpuHooked)
        {
            return;
        }

        _gpuHooked = true;
        GpuBootstrap.DeviceSetReady += OnDeviceSetReady;
        GpuBootstrap.PreferenceChanged += OnRendererPreferenceChanged;
        GpuBootstrap.MonitorsMatched += OnMonitorsMatched;
        _gpuWindow = Window.GetWindow(this);
        if (_gpuWindow is not null)
        {
            _gpuWindow.LocationChanged += OnWindowMoved;
        }
    }

    private void UnhookGpu()
    {
        if (_gpuHooked)
        {
            _gpuHooked = false;
            GpuBootstrap.DeviceSetReady -= OnDeviceSetReady;
            GpuBootstrap.PreferenceChanged -= OnRendererPreferenceChanged;
            GpuBootstrap.MonitorsMatched -= OnMonitorsMatched;
            if (_gpuWindow is not null)
            {
                _gpuWindow.LocationChanged -= OnWindowMoved;
                _gpuWindow = null;
            }
        }

        _monitorCheck?.Stop();
        _gpuRetry?.Stop();
        UnhookLabelAtlases();
        ReleaseGpu(lost: false);
    }

    /// <summary>Whether the GPU may be ready later without anything else happening: a card warming up, or one to be tried again.</summary>
    private static bool IsWorthAskingAgain(string reason) =>
        ReferenceEquals(reason, GpuBootstrap.ReasonWarmingUp) || ReferenceEquals(reason, GpuBootstrap.ReasonUnavailable);

    /// <summary>
    /// Asks again in a moment whether the GPU can draw.  A card still warming
    /// up says when it is ready, but one that failed or was lost is only made
    /// again when somebody asks after the bootstrap's wait - and a canvas at
    /// rest draws no frames to ask in.  Without this, a canvas that lost its
    /// card while the user was reading would stay on the CPU until the next
    /// time the view moved.
    /// </summary>
    private void ScheduleGpuRetry()
    {
        if (_gpuRetry is null)
        {
            _gpuRetry = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
            _gpuRetry.Tick += OnGpuRetry;
        }

        if (!_gpuRetry.IsEnabled)
        {
            _gpuRetry.Start();
        }
    }

    private void OnGpuRetry(object? sender, EventArgs e)
    {
        _gpuRetry?.Stop();
        var decision = GpuBootstrap.Decide(this);
        if (IsSceneOnGpu && ReferenceEquals(decision.DeviceSet, _surface?.Devices))
        {
            return;
        }

        if (decision.UseGpu)
        {
            RequestFrame(Layers.All);
        }
        else if (IsWorthAskingAgain(decision.Reason))
        {
            _gpuRetry?.Start();
        }
    }

    /// <summary>
    /// A card is warm (the warm-up thread says so): a canvas still on the
    /// CPU, or drawing on another card's set while this one was prepared,
    /// moves over at its next frame.
    /// </summary>
    private void OnDeviceSetReady(GpuDeviceSet devices) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (!IsSceneOnGpu || !ReferenceEquals(GpuBootstrap.Decide(this).DeviceSet, _surface?.Devices))
            {
                RequestFrame(Layers.All);
            }
        });

    /// <summary>After a display change the monitors have been matched to their cards again: redraw if this one's card is not the one drawing it.</summary>
    private void OnMonitorsMatched() => Dispatcher.InvokeAsync(() => OnMonitorCheck(null, EventArgs.Empty));

    private void OnRendererPreferenceChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            RequestFrame(Layers.All);
        }
        else
        {
            Dispatcher.InvokeAsync(() => RequestFrame(Layers.All));
        }
    }

    /// <summary>
    /// The window moved.  Once it has stayed put for 150 ms, the canvas asks
    /// whether its monitor is driven by another card than the one drawing it
    /// and, if so, redraws - on that card's set, made ahead of time.  Not on
    /// every step of a drag across the boundary between two monitors.
    /// </summary>
    private void OnWindowMoved(object? sender, EventArgs e)
    {
        if (_monitorCheck is null)
        {
            _monitorCheck = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
            _monitorCheck.Tick += OnMonitorCheck;
        }

        _monitorCheck.Stop();
        _monitorCheck.Start();
    }

    private void OnMonitorCheck(object? sender, EventArgs e)
    {
        _monitorCheck?.Stop();
        if (!ReferenceEquals(GpuBootstrap.Decide(this).DeviceSet, _surface?.Devices))
        {
            RequestFrame(Layers.All);
        }
    }

    /// <summary>
    /// For tests: the current view walked through <see cref="GpuSink"/> into
    /// <paramref name="frame"/> and drawn by <paramref name="renderer"/> into
    /// <paramref name="target"/>, which must be at least
    /// <see cref="ScenePixelWidth"/> x <see cref="ScenePixelHeight"/> - the
    /// cells, and with <paramref name="labels"/> the names too, drawn through
    /// that target as a frame at rest (or in motion, with
    /// <paramref name="inMotion"/>) draws them.  Nothing is shown and no
    /// window is needed; the same walk the canvas's own frames make, so the
    /// picture can be held against the raster's <see cref="SceneBitmap"/> and
    /// WPF's names of the view.
    /// </summary>
    internal void RenderOffscreen(NestedGpuRenderer renderer, OffscreenTarget target, NestedGpuFrame frame, GpuLabelTarget? labels = null, bool inMotion = false)
    {
        if (ActualWidth < 1 || ActualHeight < 1 || _tree is null)
        {
            return;
        }

        _viewWidth = ActualWidth;
        _viewHeight = ActualHeight;
        UpdateScale();
        EnsureCamera();
        Normalize();
        BuildChain();
        RenderScene(inMotion, new OffscreenScene(renderer, target, frame, labels));
    }

    private void PaintSceneOffscreen(OffscreenScene offscreen, int pixelWidth, int pixelHeight, bool inMotion)
    {
        var sink = _gpuSink ??= new GpuSink();
        var frame = offscreen.Frame;
        var walkStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        sink.Begin(frame.SceneRects, pixelWidth, pixelHeight, CanvasColour);
        WalkScene(sink);
        frame.ClearColour = sink.ClearColour;
        frame.SceneChanged();
        LastWalkMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(walkStarted).TotalMilliseconds;
        if (offscreen.Labels is { } labels)
        {
            var labelsStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            labels.Begin(frame, offscreen.Renderer.Devices, pixelWidth, pixelHeight, _scaleX, _scaleY, snap: !inMotion);
            DrawLabelLayer(labels);
            labels.End();
            LastLabelsMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(labelsStarted).TotalMilliseconds;
        }

        offscreen.Renderer.Draw(offscreen.Target.RenderTargetView, pixelWidth, pixelHeight, frame);
        LastGpuInstances = offscreen.Renderer.LastInstances;
        LastGpuGlyphs = offscreen.Renderer.LastGlyphs;
        LastGpuIcons = offscreen.Renderer.LastIcons;
    }

    /// <summary>For tests: the bitmap the raster painted the last CPU frame's scene into, or null.</summary>
    internal WriteableBitmap? SceneBitmap => _bitmap;

    /// <summary>For tests: the layers the cells and the names are recorded in, to draw the WPF picture of a view without its marks and outlines.</summary>
    internal Visual SceneLayer => _sceneVisual;

    internal Visual LabelLayer => _labelVisual;

    /// <summary>For tests: the folder names the last label layer drew, by which folders can be grabbed.</summary>
    internal int LabelGrabCount => _labelHotspots.Count;

    /// <summary>For the bench: the shaper the GPU's names come from, once there is one.</summary>
    internal TextShaper? GpuTextShaper => _gpuLabels?.Shaper;

    /// <summary>For tests: the scene's size in device pixels, the part of the bitmap or surface a frame draws.</summary>
    internal int ScenePixelWidth => Math.Max(1, (int)Math.Ceiling(_viewWidth * _scaleX));

    internal int ScenePixelHeight => Math.Max(1, (int)Math.Ceiling(_viewHeight * _scaleY));

    /// <summary>Where a test's offscreen frame goes: the renderer, the texture, the instance lists and, when the names are wanted, their target.</summary>
    private readonly record struct OffscreenScene(NestedGpuRenderer Renderer, OffscreenTarget Target, NestedGpuFrame Frame, GpuLabelTarget? Labels);

    /// <summary>
    /// The walk itself, into whichever sink the frame draws with: the bare
    /// canvas where no folder covers the view, then the covering folder and
    /// everything on screen inside it.  Besides the shapes it hands the sink,
    /// it leaves the frame's labels, file labels and filter outlines listed
    /// for the layers above, and asks the tree for what should be read -
    /// the same whatever the sink.
    /// </summary>
    private void WalkScene(SceneSink sink)
    {
        // Everything a walk leaves behind starts empty, so a frame whose GPU
        // failed half way can be walked again into the bitmap.
        _labels.Clear();
        _labelled.Clear();
        _fileLabels.Clear();
        _filterOutlines.Clear();
        DrawnCellCount = 0;
        _foldersDrawn = 0;
        _tilesDrawn = 0;
        _relayoutSpent = 0;
        _drewStale = false;
        _drawnStale.Clear();
        _sceneBudgetSpent = false;

        _sink = sink;
        var (cover, x, y, w, covers) = CoverCell();
        if (!covers)
        {
            sink.Clear(CanvasColour);
        }

        DrawCell(cover, x, y, w, labelsAllowed: true);
    }

    /// <summary>
    /// The deepest folder on the anchor's line whose cell covers the whole
    /// view.  Everything outside it is off screen, so the walk starts there:
    /// deep in, the ten folders above it would each fill the screen with a
    /// colour only to be painted over by the next.
    /// </summary>
    private (NestedFolder Folder, double X, double Y, double W, bool Covers) CoverCell()
    {
        for (var folder = _anchor; folder is not null; folder = folder.Parent)
        {
            if (!_chain.TryGetValue(folder, out var rect))
            {
                break;
            }

            if (Covers(rect.X, rect.Y, rect.W))
            {
                return (folder, rect.X, rect.Y, rect.W, true);
            }
        }

        var (rx, ry, rw) = _chain[_tree!.Root];
        return (_tree.Root, rx, ry, rw, false);
    }

    /// <summary>
    /// Whether a cell covers the view with room to spare for its rim and its
    /// rounded corners (at most 6 DIPs), so that nothing of what lies outside
    /// it can show at the view's corners.
    /// </summary>
    private bool Covers(double x, double y, double w) =>
        x <= -8 && y <= -8 && x + w >= _viewWidth + 8 && y + w * NestedLayout.CellHeight >= _viewHeight + 8;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private void DrawCell(NestedFolder folder, double x, double y, double w, bool labelsAllowed)
    {
        var h = w * NestedLayout.CellHeight;
        if (x >= _viewWidth || y >= _viewHeight || x + w <= 0 || y + h <= 0 || w < _minCell)
        {
            return;
        }

        DrawnCellCount++;
        if (++_foldersDrawn > MaximumCellsPerFrame)
        {
            _sceneBudgetSpent = true;
            return;
        }

        // Placed for the current order before its children and files are
        // walked - within the frame's allowance for placing; past it, as it
        // was placed, and noted for the tree to place first.
        var stamp = folder.LayoutSortGeneration;
        if (stamp >= 0 && stamp != _tree!.SortGeneration)
        {
            if (_relayoutSpent < _relayoutAllowance)
            {
                var placing = System.Diagnostics.Stopwatch.GetTimestamp();
                _tree.EnsureLayout(folder);
                _relayoutSpent += System.Diagnostics.Stopwatch.GetTimestamp() - placing;
            }
            else
            {
                _drewStale = true;
                if (_drawnStale.Count < MaximumPlacedFirst)
                {
                    _drawnStale.Add(folder);
                }
            }
        }

        EnsurePalette(folder);
        PaintCell(folder, x, y, w, h);
        if (_filter is not null && w >= 6 && _filterOutlines.Count < 600 && (FilterStateOf(folder) & FilterSelf) != 0)
        {
            _filterOutlines.Add(new Rect(x, y, w, h));
        }

        // A link is never read for being drawn, but one already read by name
        // is drawn from what it held then, and is kept up to date like any
        // other folder (NestedTree.Request).
        if (w >= LoadPixels && (folder.CanLoad || folder.IsReparsePoint && folder.IsLoaded))
        {
            _tree!.Request(folder, w);
        }

        var labelMode = LabelModeFor(w, labelsAllowed);
        if (labelMode != LabelMode.None)
        {
            _labels.Add(new LabelJob(folder, x, y, w, labelMode));
            _labelled.Add(folder);
        }

        var childLabels = labelMode == LabelMode.Header;
        DrawFiles(folder, x, y, w, childLabels);

        var grid = folder.Grid;
        if (grid.IsEmpty || w * grid.Scale < _minCell)
        {
            return;
        }

        var (firstColumn, lastColumn, firstRow, lastRow) = grid.Overlapping(
            -x / w,
            -y / w,
            (_viewWidth - x) / w,
            (_viewHeight - y) / w);
        var children = folder.Children;
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var index = grid.IndexOf(row, column);
                if (index >= children.Count)
                {
                    break;
                }

                var child = children[index];
                if (_chain.TryGetValue(child, out var exact))
                {
                    DrawCell(child, exact.X, exact.Y, exact.W, childLabels);
                }
                else
                {
                    Place(folder, child, x, y, w, out var childX, out var childY, out var childWidth);
                    DrawCell(child, childX, childY, childWidth, childLabels);
                }
            }
        }
    }

    /// <summary>
    /// A folder's files, as tiles under its sub-folders.  Too small to tell
    /// apart, they are one faint wash over the strip they occupy: the folder
    /// still visibly holds files, without drawing a thousand specks.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private void DrawFiles(NestedFolder folder, double x, double y, double w, bool labelsAllowed)
    {
        var grid = folder.FileGrid;
        if (grid.IsEmpty)
        {
            return;
        }

        var tileWidth = grid.TileWidth * w;
        var tileHeight = grid.TileHeight * w;
        if (tileWidth * _scaleX < _minTile)
        {
            if (w * _scaleX < 12)
            {
                return;
            }

            var usedWidth = Math.Min(NestedLayout.ContentWidth, grid.Columns * grid.StepX - grid.Gap);
            var usedHeight = Math.Min(
                NestedLayout.CellHeight - NestedLayout.Padding - grid.Top,
                grid.Rows * grid.StepY - grid.Gap);
            _sink.Fill(
                NestedRaster.Px((x + grid.Left * w) * _scaleX),
                NestedRaster.Px((y + grid.Top * w) * _scaleY),
                NestedRaster.Px((x + (grid.Left + usedWidth) * w) * _scaleX),
                NestedRaster.Px((y + (grid.Top + usedHeight) * w) * _scaleY),
                NestedRaster.Mix(folder.BodyColour, 0xFF8A8F96, 0.1));
            return;
        }

        var (firstColumn, lastColumn, firstRow, lastRow) = grid.Overlapping(
            -x / w,
            -y / w,
            (_viewWidth - x) / w,
            (_viewHeight - y) / w);
        var files = folder.Files;
        var labels = labelsAllowed && tileHeight >= FileLabelPixels;
        var visibleTiles = (long)(lastColumn - firstColumn + 1) * (lastRow - firstRow + 1);
        if (_tilesDrawn + visibleTiles > MaximumTilesPerFrame)
        {
            // Past the frame's budget for tiles, a folder's files are its wash:
            // detail is what goes, never a whole folder further along.
            _sceneBudgetSpent = true;
            var zoneWidth = Math.Min(NestedLayout.ContentWidth, grid.Columns * grid.StepX - grid.Gap);
            var zoneHeight = Math.Min(NestedLayout.CellHeight - NestedLayout.Padding - grid.Top, grid.Rows * grid.StepY - grid.Gap);
            _sink.Fill(
                NestedRaster.Px((x + grid.Left * w) * _scaleX),
                NestedRaster.Px((y + grid.Top * w) * _scaleY),
                NestedRaster.Px((x + (grid.Left + zoneWidth) * w) * _scaleX),
                NestedRaster.Px((y + (grid.Top + zoneHeight) * w) * _scaleY),
                NestedRaster.Mix(folder.BodyColour, 0xFF8A8F96, 0.1));
            return;
        }

        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var index = grid.IndexOf(row, column);
                if (index >= files.Count)
                {
                    break;
                }

                DrawnCellCount++;
                _tilesDrawn++;

                PlaceFile(folder, index, x, y, w, out var fx, out var fy, out var fw, out var fh);
                PaintFile(files[index], fx, fy, fw, fh);
                if (labels)
                {
                    _fileLabels.Add(new FileLabelJob(folder, index, fx, fy, fw, fh));
                }
            }
        }
    }

    private void PaintFile(NestedFile file, double x, double y, double w, double h)
    {
        var (body, stripe, speck) = FilePalette(file.Extension);
        if (_filter is not null)
        {
            if (_filter(file.Name))
            {
                body = NestedRaster.Mix(body, FilterColour, 0.3);
                stripe = FilterColour;
                speck = FilterColour;
            }
            else
            {
                body = NestedRaster.Mix(body, CanvasColour, 0.72);
                stripe = NestedRaster.Mix(stripe, CanvasColour, 0.72);
                speck = NestedRaster.Mix(speck, CanvasColour, 0.72);
            }
        }

        if (file.IsHidden)
        {
            body = NestedRaster.Mix(body, CanvasColour, 0.45);
            stripe = NestedRaster.Mix(stripe, CanvasColour, 0.45);
            speck = NestedRaster.Mix(speck, CanvasColour, 0.45);
        }

        var left = x * _scaleX;
        var top = y * _scaleY;
        var right = (x + w) * _scaleX;
        var bottom = (y + h) * _scaleY;
        if (right - left < 6 || bottom - top < 3)
        {
            _sink.Fill(
                NestedRaster.PxFloor(left),
                NestedRaster.PxFloor(top),
                Math.Max(NestedRaster.PxFloor(left) + 1, NestedRaster.Px(right)),
                Math.Max(NestedRaster.PxFloor(top) + 1, NestedRaster.Px(bottom)),
                speck);
            return;
        }

        // The coloured edge says what kind of file it is before the name can.
        var height = bottom - top;
        var stripeWidth = Math.Max(1, Math.Min(3 * _scaleX, height * 0.14));
        var inset = Math.Max(1, height * 0.18);
        _sink.File(
            left,
            top,
            right,
            bottom,
            Math.Min(3 * _scaleX, height * 0.2),
            NestedRaster.Px(left + 1),
            NestedRaster.Px(top + inset),
            NestedRaster.Px(left + 1 + stripeWidth),
            NestedRaster.Px(bottom - inset),
            body,
            stripe);
    }

    private void PaintCell(NestedFolder folder, double x, double y, double w, double h)
    {
        var left = x * _scaleX;
        var top = y * _scaleY;
        var right = (x + w) * _scaleX;
        var bottom = (y + h) * _scaleY;
        var pixelWidth = right - left;

        if (pixelWidth < 4)
        {
            // A speck: one colour, the rim's, which reads against any parent.
            _sink.Fill(
                NestedRaster.PxFloor(left),
                NestedRaster.PxFloor(top),
                Math.Max(NestedRaster.PxFloor(left) + 1, NestedRaster.Px(right)),
                Math.Max(NestedRaster.PxFloor(top) + 1, NestedRaster.Px(bottom)),
                folder.HasLabel ? folder.StripeColour : folder.RimColour);
            return;
        }

        var radius = pixelWidth >= 40 ? Math.Min(6 * _scaleX, pixelWidth * 0.03) : 0;

        // The title band, when the cell is tall enough to show one, and on it
        // the stripe in the folder's own colour: what the stripe on a tree
        // node was, a sign of whose this is.
        var headerBottom = double.NaN;
        var hasStripe = false;
        int stripeLeft = 0, stripeTop = 0, stripeRight = 0, stripeBottom = 0;
        var header = w * NestedLayout.HeaderHeight * _scaleY;
        if (header >= 2)
        {
            headerBottom = top + header;
            if (header >= 6 && pixelWidth >= 30)
            {
                var inset = Math.Max(1, header * 0.2);
                var stripe = Math.Max(2, Math.Min(4 * _scaleX, header * 0.12));
                var stripeStart = left + 1 + Math.Max(2 * _scaleX, header * 0.18);
                hasStripe = true;
                stripeLeft = NestedRaster.Px(stripeStart);
                stripeTop = NestedRaster.Px(top + inset);
                stripeRight = NestedRaster.Px(stripeStart + stripe);
                stripeBottom = NestedRaster.Px(top + header - inset);
            }
        }

        _sink.Cell(
            left,
            top,
            right,
            bottom,
            radius,
            headerBottom,
            hasStripe,
            stripeLeft,
            stripeTop,
            stripeRight,
            stripeBottom,
            folder.RimColour,
            folder.BodyColour,
            folder.HeaderColour,
            folder.StripeColour);
    }
}
