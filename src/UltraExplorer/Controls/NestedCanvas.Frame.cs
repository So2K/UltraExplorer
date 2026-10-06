using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

// The frame loop: what asks for a frame, the loop itself - one pass per
// vsync that brings the camera forward and draws, once, whatever went
// out of date since the last one - and the recording of the four layers
// that pass draws.
public sealed partial class NestedCanvas
{
    /// <summary>
    /// Draws whatever is out of date right now, rather than on the next frame.
    /// For a snapshot of the control, and for a test with no frame loop.
    /// </summary>
    public void RenderNow()
    {
        var layers = _dirty | Layers.All;
        _dirty = Layers.None;
        ForgetLoadRedraw();

        // A snapshot must hold this frame, not the last one: a GPU frame that
        // found WPF still copying the previous one is tried again at once -
        // each try waits a moment for the copy - before giving up to the loop.
        var drawn = RenderLayers(layers, inMotion: false);
        for (var attempt = 1; !drawn && attempt < 10; attempt++)
        {
            _dirty = Layers.None;
            drawn = RenderLayers(layers, inMotion: false);
        }
    }

    /// <summary>
    /// Draws everything as one frame of the canvas's own loop draws it - with
    /// the loop's allowance for placing folders after a change of order, and
    /// whatever it leaves over drawn as it was - for a test, which has no loop.
    /// </summary>
    internal void RenderAsFrame()
    {
        _dirty = Layers.None;
        ForgetLoadRedraw();
        _inFrameLoop = true;
        try
        {
            RenderLayers(Layers.All, inMotion: false);
        }
        finally
        {
            _inFrameLoop = false;
        }
    }

    // The loop's own state: its clock, the wake waiting for it, the inboxes
    // it takes in, and whether the camera's eased motion or a transition was
    // still moving on the last frame.
    private FrameClock _clock = new();
    private readonly Action _onWake;
    private readonly Func<TimeSpan> _clockNow;
    private readonly List<FrameDriverSlot> _drivenSlots = [];
    private int _wakePosted;
    private bool _cameraMotionActive;
    private bool _transitionsActive;
    private bool _cameraReadDeferred;

    /// <summary>What the last frame of the loop did, phase by phase.</summary>
    internal FrameStats LastFrameStats { get; private set; }

    /// <summary>For the bench: frames of the loop run so far, which tells a new <see cref="LastFrameStats"/> from the one seen before.</summary>
    internal long LoopFrameCount { get; private set; }

    /// <summary>For tests: whether the frame loop is hooked to WPF's frames.</summary>
    internal bool IsFrameHooked => _frameHooked;

    /// <summary>
    /// Nothing to do: no layer out of date - nor any redraw for folders read
    /// that is being held back to loading's rate - the camera still and
    /// settled, no transition running, and nothing waiting in any inbox the
    /// canvas takes in.  The loop unhooks itself on the frame this becomes
    /// true, or when all that is left is a held-back redraw whose timer will
    /// ask for its frame (<see cref="WaitForLoadRedraw"/>), and is hooked
    /// again only by a request for a frame, a wake, or that timer.  WPF's
    /// Rendering itself is let go of a couple of frames later
    /// (<see cref="LetGoAtRest"/>).
    /// </summary>
    internal bool IsIdle => IsIdleWith(IsMoving());

    /// <summary>
    /// Whether anything is still under way that a picture taken now would
    /// catch half done: eased camera motion, a transition, results waiting
    /// in an inbox - finished reads, icons, changes on disk - or a filter
    /// still being judged.  What a snapshot waits out besides the tree's
    /// reads (see <see cref="IsIdle"/>).
    /// </summary>
    internal bool HasPendingWork
    {
        get
        {
            var pending = _cameraMotionActive || _transitionsActive || _tree?.HasWork == true;
            PendingHubWork(ref pending);
            PendingIconWork(ref pending);
            PendingFilterWork(ref pending);
            return pending;
        }
    }

    private bool IsIdleWith(bool moving, bool loadRedrawTimed = false) =>
        _dirty == Layers.None
        && (_loadDirty == Layers.None || loadRedrawTimed)
        && _flight is null
        && !moving
        && !_lodDegraded
        && !_textAnimated
        && !_cameraReadDeferred
        && !HasPendingWork;

    /// <summary>Marks layers out of date and makes sure the next frame draws them; with none, only that there is a next frame.</summary>
    private void RequestFrame(Layers layers)
    {
        _dirty |= layers;
        if (_frameHooked)
        {
            return;
        }

        // A canvas in a window that cannot be seen - collapsed, or inside
        // something that is - has nothing to draw into: a frame would walk
        // the whole scene and ask for reads of folders nobody is looking at.
        // What went out of date waits in _dirty, and is drawn when the canvas
        // is shown again (OnIsVisibleChanged).  One in no window at all is a
        // test's or a snapshot's, which draws its frames by hand.
        if (!IsVisible && PresentationSource.FromVisual(this) is not null)
        {
            return;
        }

        // Frames that keep failing are not tried again for every post, read
        // or icon that asks for one: what went out of date waits in _dirty
        // for the next try, which input or the back-off timer makes
        // (see ContainFailedFrame).
        if (FramesFailing)
        {
            ScheduleFrameRetry();
            return;
        }

        _frameHooked = true;
        _framesToLetPass = 0;
        if (!FramesByHandForTests && !_hookedToRendering)
        {
            _hookedToRendering = true;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    /// <summary>Stops the loop and lets go of WPF's Rendering at once: the canvas is leaving the screen, or its frames keep failing.</summary>
    private void UnhookFrame()
    {
        _frameHooked = false;
        _framesToLetPass = 0;
        if (_hookedToRendering)
        {
            _hookedToRendering = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    /// <summary>
    /// The loop has nothing left to do, found by one of its own frames: it
    /// stops, but keeps WPF's Rendering for <see cref="FramesLetPassAtRest"/>
    /// more of WPF's frames, in which it draws nothing, before it lets go.
    ///
    /// <para>The frame that finds the loop idle is usually the one that drew
    /// the settled picture, and WPF commits that drawing after the Rendering
    /// handlers.  With no handler left, WPF leaves its interlocked
    /// presentation there and then, and first waits for the render thread to
    /// show the frame still on its way - the UI thread blocked for a hundred
    /// milliseconds or more after every stop of the camera, just when the
    /// next wheel step or click comes.  A frame later nothing is on its way,
    /// and letting go costs nothing.</para>
    /// </summary>
    private void LetGoAtRest(TimeSpan renderingTime)
    {
        if (!_hookedToRendering)
        {
            UnhookFrame();
            return;
        }

        _frameHooked = false;
        _framesToLetPass = FramesLetPassAtRest;
        _letPassFrameTime = renderingTime;
    }

    /// <summary>WPF's frames that pass with nothing drawn between the loop stopping and it letting go of Rendering (see <see cref="LetGoAtRest"/>).</summary>
    private const int FramesLetPassAtRest = 2;

    /// <summary>Frames still to pass before the loop lets go of Rendering; nought while it runs, or has let go.</summary>
    private int _framesToLetPass;

    /// <summary>The time of the last of WPF's frames counted as passed, so Rendering raised again within one is not counted twice.</summary>
    private TimeSpan _letPassFrameTime;

    /// <summary>
    /// For tests: the loop is hooked and unhooked as always, but only
    /// <see cref="RunFrameForTests"/> runs its frames - never WPF's Rendering,
    /// which on a test's dispatcher would run a frame of its own, at a time of
    /// its own, whenever the test awaits something.
    /// </summary>
    internal bool FramesByHandForTests { get; set; }

    private bool _hookedToRendering;

    /// <summary>
    /// The canvas is leaving the screen: the loop stops, and whatever it was
    /// woken for and has not taken in yet is handed to the inboxes' fallback
    /// drivers, which take in what is posted from now on anyway.
    /// </summary>
    private void StopFrames()
    {
        UnhookFrame();
        _loadRedrawTimer?.Stop();
        _frameRetry?.Stop();
        if (Interlocked.Exchange(ref _wakePosted, 0) != 0)
        {
            HandOffWake();
        }
    }

    /// <summary>
    /// The canvas was hidden - its own panel collapsed, or one around it - or
    /// shown again.  Hidden, the loop stops as it does when the canvas leaves
    /// the screen, and frames asked for meanwhile only mark what they would
    /// have drawn (<see cref="RequestFrame"/>); shown, all of it is drawn.
    /// </summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        ShareDrivenSlots(IsVisible);
        if (IsVisible)
        {
            RequestFrame(Layers.All);
        }
        else if (PresentationSource.FromVisual(this) is not null)
        {
            StopFrames();
        }
    }

    // ---- waking --------------------------------------------------------------

    /// <summary>
    /// Takes over waking for <paramref name="slot"/>: the work its inboxes are
    /// sent is taken in at the start of this canvas's frames while the canvas
    /// can draw, and handed to the slot's fallback driver while it cannot.
    /// The slot of the tree the canvas draws brings that tree's batches of
    /// applied reads with it: whoever takes a tree's reads in is who redraws
    /// for them, and only as much as they changed (<see cref="OnBatchApplied"/>).
    /// </summary>
    internal void Drive(FrameDriverSlot slot)
    {
        if (!_drivenSlots.Contains(slot))
        {
            _drivenSlots.Add(slot);
        }

        slot.Claim(this);
        if (_tree is { } tree && ReferenceEquals(slot, tree.Driver))
        {
            FollowBatches(tree);
        }
    }

    /// <summary>
    /// Gives <paramref name="slot"/> back, with anything it was woken for: to
    /// the other canvas that drives it too, if one does - the other pane of a
    /// split view, which takes over the work from its next frame - and
    /// otherwise to the slot's fallback driver.
    /// </summary>
    internal void StopDriving(FrameDriverSlot slot)
    {
        if (_batchTree is { } followed && ReferenceEquals(slot, followed.Driver))
        {
            FollowBatches(null);
        }

        _drivenSlots.Remove(slot);
        slot.Release(this);
        slot.Wake();
    }

    /// <summary>
    /// Hidden, the canvas lets any other canvas driving the same slots be the
    /// one woken - the other pane, which can draw - and wakes it for whatever
    /// is waiting; shown again, it takes them back.  Alone in a slot, as a
    /// single pane always is, nothing changes: its wakes go to the fallback
    /// while it is hidden (<see cref="OnWake"/>), as they always did.
    /// </summary>
    private void ShareDrivenSlots(bool shown)
    {
        foreach (var slot in _drivenSlots)
        {
            if (shown)
            {
                slot.Claim(this);
                continue;
            }

            slot.Yield(this);
            if (slot.Active is { } other && !ReferenceEquals(other, this))
            {
                other.Wake();
            }
        }
    }

    void IFrameDriver.Wake() => Wake();

    /// <summary>
    /// Something was posted to an inbox this canvas takes in.  Any thread,
    /// never blocks: at most one operation is queued to the dispatcher until
    /// the frame it asks for has run, however many wakes arrive meanwhile -
    /// at Render priority, which WPF serves in the same pass as a frame.
    /// </summary>
    internal void Wake()
    {
        if (Interlocked.Exchange(ref _wakePosted, 1) == 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, _onWake);
        }
    }

    /// <summary>
    /// A wake on the UI thread: the loop is hooked, nothing marked out of
    /// date, and the frame's first phases take in what was posted.  A canvas
    /// that cannot draw - collapsed, or in no window - would never run that
    /// frame, so the work goes to the fallback drivers instead; and so does
    /// one whose frames keep failing, or every read and icon arriving would
    /// run another frame that fails.
    /// </summary>
    private void OnWake()
    {
        if (IsVisible && !FramesFailing)
        {
            RequestFrame(Layers.None);
            return;
        }

        Volatile.Write(ref _wakePosted, 0);
        HandOffWake();
    }

    private void HandOffWake()
    {
        foreach (var slot in _drivenSlots)
        {
            slot.Fallback.Wake();
        }
    }

    // ---- the frame -----------------------------------------------------------

    /// <summary>Frames of the loop in a row that ended in an exception; nought after any that did not.</summary>
    private int _failedFrames;

    /// <summary>After this many frames in a row fail, the loop stops trying until input or the back-off timer tries again.</summary>
    private const int MaximumFailedFrames = 3;

    /// <summary>The first wait before frames that kept failing are tried again; it doubles with every try that fails too.</summary>
    private static readonly TimeSpan FirstFrameRetry = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between tries.</summary>
    private static readonly TimeSpan LastFrameRetry = TimeSpan.FromSeconds(30);

    /// <summary>What tries a frame again while frames keep failing; null until one has.</summary>
    private DispatcherTimer? _frameRetry;

    /// <summary>How long the next wait for a try is.</summary>
    private TimeSpan _frameRetryDelay = FirstFrameRetry;

    /// <summary>Whether the loop has let go since the last frame that worked, which is logged once.</summary>
    private bool _framesLetGo;

    /// <summary>
    /// Whether frames have failed so often in a row that the loop has let go:
    /// wakes go to the fallback drivers, and a frame is only tried again for
    /// input on the canvas or when the back-off timer says.
    /// </summary>
    private bool FramesFailing => _failedFrames >= MaximumFailedFrames;

    /// <summary>For tests: frames of the loop that still fail on purpose, from the start, as a bug would.</summary>
    internal int FailFramesForTests { get; set; }

    /// <summary>For tests: where a failed frame is written instead of the crash log.</summary>
    internal Action<string, Exception>? FrameFailureLogForTests { get; set; }

    /// <summary>For tests: whether the loop has let go after frames that kept failing.</summary>
    internal bool FramesFailingForTests => FramesFailing;

    /// <summary>For tests: one frame of the loop the way WPF's Rendering runs it, failures contained.</summary>
    internal void RunContainedFrameForTests(TimeSpan renderingTime) => RunContainedFrame(renderingTime);

    /// <summary>For tests: the back-off timer's tick, now.</summary>
    internal void RetryFramesForTests() => OnFrameRetryDue(null, EventArgs.Empty);

    /// <summary>
    /// WPF's Rendering: one frame of the loop.  Nothing above this catches
    /// what a frame throws - it would end the app - so a frame that fails is
    /// contained here (<see cref="ContainFailedFrame"/>) rather than taking
    /// the window down with it.  Tests run <see cref="RunFrame"/> directly,
    /// and see every exception.
    /// </summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        var renderingTime = e is RenderingEventArgs rendering ? rendering.RenderingTime : _clock.Now;
        if (!_frameHooked)
        {
            // Stopped, and letting WPF's frames pass before letting go (LetGoAtRest).
            if (renderingTime != _letPassFrameTime)
            {
                _letPassFrameTime = renderingTime;
                if (--_framesToLetPass <= 0)
                {
                    UnhookFrame();
                }
            }

            return;
        }

        RunContainedFrame(renderingTime);
    }

    private void RunContainedFrame(TimeSpan renderingTime)
    {
        try
        {
            RunFrame(renderingTime);
            if (_failedFrames != 0)
            {
                FramesRecovered();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContainFailedFrame(ex);
        }
    }

    /// <summary>
    /// A frame of the loop threw part way through.  It is written to the
    /// crash log - the first of a run of them, and the one that makes the
    /// loop let go - and dropped, and what it left half done is put straight:
    /// the flags only a frame being drawn holds are cleared, and since the
    /// layers it had taken to draw were taken off <see cref="_dirty"/> before
    /// it failed, everything is marked to be drawn again on the next frame.
    ///
    /// A failure that comes back every frame would spin the loop at the
    /// display's rate, throwing each time: after a few in a row the loop lets
    /// go instead.  The inboxes' fallback drivers then take in what the frames
    /// would have - a wake no longer asks for a frame (<see cref="OnWake"/>),
    /// nor does anything else that asks for one (<see cref="RequestFrame"/>) -
    /// and a frame is tried again only for input on the canvas
    /// (<see cref="RetryFailedFrames"/>) or when a back-off timer says, one
    /// second at first and twice as long after every try that fails too, up
    /// to half a minute.  The first frame that works puts all of it back.
    /// </summary>
    private void ContainFailedFrame(Exception ex)
    {
        if (_failedFrames == 0)
        {
            LogFrameFailure("in a frame of the nested canvas; the frame was dropped", ex);
        }

        _inFrameLoop = false;
        _sceneChangedArea = Rect.Empty;
        _dirty |= Layers.All;
        if (++_failedFrames < MaximumFailedFrames)
        {
            RequestFrame(Layers.All);
            return;
        }

        if (!_framesLetGo)
        {
            _framesLetGo = true;
            LogFrameFailure($"in a frame of the nested canvas, {MaximumFailedFrames} in a row; frames are tried again for input or after a pause", ex);
        }

        _failedFrames = MaximumFailedFrames;
        UnhookFrame();
        _loadRedrawTimer?.Stop();
        HandOffWake();
        ScheduleFrameRetry();
    }

    private void LogFrameFailure(string where, Exception ex)
    {
        if (FrameFailureLogForTests is { } log)
        {
            log(where, ex);
            return;
        }

        CrashReporter.Log(where, ex);
    }

    /// <summary>Makes sure the back-off timer will try a frame again, while frames keep failing.</summary>
    private void ScheduleFrameRetry()
    {
        if (_frameRetry is null)
        {
            _frameRetry = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _frameRetry.Tick += OnFrameRetryDue;
        }

        if (!_frameRetry.IsEnabled)
        {
            _frameRetry.Interval = _frameRetryDelay;
            _frameRetry.Start();
        }
    }

    private void OnFrameRetryDue(object? sender, EventArgs e)
    {
        _frameRetry?.Stop();
        var doubled = _frameRetryDelay + _frameRetryDelay;
        _frameRetryDelay = doubled < LastFrameRetry ? doubled : LastFrameRetry;
        RetryFailedFrames();
    }

    /// <summary>
    /// Input on the canvas, a new size, or the back-off timer: while frames
    /// keep failing, one more frame is tried, drawing everything.  One that
    /// fails again lets go at once and waits for the next try.
    /// </summary>
    private void RetryFailedFrames()
    {
        if (!FramesFailing)
        {
            return;
        }

        _failedFrames = MaximumFailedFrames - 1;
        RequestFrame(Layers.All);
    }

    /// <summary>A frame worked after some that failed: the loop, its wakes and the back-off are as they always were.</summary>
    private void FramesRecovered()
    {
        _failedFrames = 0;
        _framesLetGo = false;
        _frameRetryDelay = FirstFrameRetry;
        _frameRetry?.Stop();
    }

    /// <summary>For tests: one frame of the loop at <paramref name="renderingTime"/>, as WPF's Rendering would run it.</summary>
    internal void RunFrameForTests(TimeSpan renderingTime) => RunFrame(renderingTime);

    /// <summary>
    /// One frame of the canvas's own loop, the way a game engine runs one, in
    /// phases whose order is the point:
    ///
    /// <list type="number">
    /// <item>the clock takes the frame's time, and tells a new frame from WPF
    /// raising Rendering again within the same one;</item>
    /// <item>the camera moves, once per frame - a flight, or eased motion -
    /// so everything after this sees where it is now;</item>
    /// <item>changes on disk are taken from the change hub;</item>
    /// <item>what arrived for the names - icons, glyphs, shaped names;</item>
    /// <item>finished reads are applied to the tree;</item>
    /// <item>transitions step to the frame's time, once per frame;</item>
    /// <item>events held back while all that came in are raised once;</item>
    /// <item>whatever went out of date since the last frame is drawn - once,
    /// however many things asked for it: a hundred folders read in one frame
    /// are one redraw, of only what they changed, and at rest no more than
    /// one every <see cref="FrameBudgets.LoadRedrawMinMs"/>;</item>
    /// <item>and with nothing left to do, the loop unhooks itself.</item>
    /// </list>
    ///
    /// Phases 3 to 7 take in work other threads finished, each within its
    /// budget (<see cref="FrameBudgets"/>): what does not fit waits for the
    /// next frame, and the loop stays hooked until it has all been taken in.
    /// Each phase another part of the canvas owns is one of the hooks below.
    /// </summary>
    private void RunFrame(TimeSpan renderingTime)
    {
        // A wake that came since the last frame is served by this one.
        Volatile.Write(ref _wakePosted, 0);
        LoopFrameCount++;

        // 1. The clock.
        var fresh = _clock.Advance(renderingTime, _cameraMotionActive || _transitionsActive);
        var stats = new FrameStats { Fresh = fresh };
        try
        {
            if (FailFramesForTests > 0)
            {
                FailFramesForTests--;
                throw new InvalidOperationException("A frame failed on purpose, for a test.");
            }

            // 2. The camera.
            var phase = System.Diagnostics.Stopwatch.GetTimestamp();
            if (fresh)
            {
                AdvanceCamera();
            }

            stats.CameraActive = _flight is not null || _cameraMotionActive;
            stats.CameraMs = Lap(ref phase);

            // 3. Changes on disk.
            var hub = FrameBudget.Start(FrameBudgets.HubMs, FrameBudgets.HubItems);
            DrainChangeHub(ref hub);
            stats.HubItems = FrameBudgets.HubItems - hub.Items;
            stats.HubMs = Lap(ref phase);

            // 4. Icons, glyphs and shaped names.
            TakeLabelMaterial();
            DrainIconArrivals();
            stats.IconMs = Lap(ref phase);

            // 5. Finished reads.
            if (_tree is { } tree)
            {
                var apply = FrameBudget.Start(FrameBudgets.ApplyMs);
                stats.Applied = tree.DrainResults(ref apply);
            }

            stats.ApplyMs = Lap(ref phase);

            // 6. Transitions.
            if (fresh)
            {
                var active = false;
                StepTransitions(_clock.Now, ref active);
                _transitionsActive = active;
            }

            stats.TransitionMs = Lap(ref phase);

            // 7. The events the intake held back.
            FlushFrameEvents();

            // A precise F flight can reach its old endpoint in phase 2 while
            // phases 3 or 5 move that file's tile. Validate once more after
            // all tree intake, before this frame is allowed to settle.
            FinishPreciseFlightAfterIntake();

            // 8. Settle, and draw what is out of date.
            var moving = IsMoving();
            var retryCameraReads = !moving && _cameraReadDeferred;
            if (retryCameraReads)
            {
                // A folder visible during transit may have deliberately
                // refused its listing. Retry the final viewport once, even
                // if motion needed neither degraded geometry nor text.
                _cameraReadDeferred = false;
                _dirty |= Layers.All;
            }

            // The camera has come to rest after frames drawn with less detail, or
            // with text set for motion: one more frame, at full quality.  Not the
            // moment it stops - drawing every name crisp costs WPF's render thread
            // a frame or two - but once it has been still for a third of a second,
            // when a frame that takes longer shows as nothing at all, and the
            // user is not in the middle of the next move.
            var settled = !moving && _lastMotion != 0
                && System.Diagnostics.Stopwatch.GetElapsedTime(_lastMotion).TotalMilliseconds >= SettleMilliseconds;
            if (settled && (_lodDegraded || _textAnimated))
            {
                _dirty |= Layers.All;
            }

            // What folders read since the last frame need redrawn - scoped
            // when they were read (OnBatchApplied) - joins the frame, unless
            // it is held back to loading's own rate.
            var scoped = TakeLoadRedraw(moving);
            if (retryCameraReads) scoped = false;

            stats.Skipped = _dirty == Layers.None;
            LastFrameLayers = Layers.None;
            if (_dirty != Layers.None)
            {
                var layers = _dirty;
                _dirty = Layers.None;

                // Only a frame of the loop has a next frame to leave work to; a
                // snapshot or a test drawing on demand gets everything placed.
                _inFrameLoop = true;
                var frameStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    // Between stopping and settling, the frames that are drawn keep
                    // the motion look, so nothing flips back and forth.
                    RenderLayers(layers, moving || !settled && (_lodDegraded || _textAnimated), scoped);
                }
                finally
                {
                    _inFrameLoop = false;
                    _sceneChangedArea = Rect.Empty;
                    LastFrameMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds;
                    stats.RenderMs = LastFrameMilliseconds;
                }
            }

            // 9. Nothing left to do: stop until something asks again.  A
            // loading redraw waiting its turn has a timer to ask for it.
            if (IsIdleWith(moving, loadRedrawTimed: _loadRedrawTimer is { IsEnabled: true }))
            {
                LetGoAtRest(renderingTime);
            }
        }
        finally
        {
            _clock.EndFrame();
            LastFrameStats = stats;
        }
    }

    /// <summary>Milliseconds since <paramref name="since"/>, which becomes now: a phase's time, and the start of the next one's.</summary>
    private static double Lap(ref long since)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = (now - since) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        since = now;
        return elapsed;
    }

    /// <summary>
    /// Phase 2: a flight puts the camera where it is at this moment of its
    /// path; with none, eased motion takes its step of the frame's time.
    /// </summary>
    private void AdvanceCamera()
    {
        _completedPreciseFlight = null;
        if (_flight is not { } flight)
        {
            var active = false;
            StepCameraMotion(_clock.DeltaMilliseconds, ref active);
            _cameraMotionActive = active;
            return;
        }

        if (NestedTree.IsDetached(flight.Target) || !NestedTree.IsOnCanvas(flight.Target))
        {
            StopFlight();
            return;
        }

        if (!PreparePreciseFlight(flight, out flight))
        {
            return;
        }

        var done = flight.Sample(_viewWidth, _viewHeight, out var x, out var y, out var w);
        _anchor = flight.Target;
        _ax = x;
        _ay = y;
        _aw = w;
        _hasCamera = true;
        Normalize();
        if (done)
        {
            if (flight.PreciseFile is not null)
            {
                // Keep its metadata until the change/read intake later in this
                // frame has had its chance to move the exact file tile.
                _completedPreciseFlight = flight;
            }
            else
            {
                StopFlight();
            }
        }

        AfterCameraMove();
    }

    private Flight? _completedPreciseFlight;

    private void FinishPreciseFlightAfterIntake()
    {
        var completed = _completedPreciseFlight;
        _completedPreciseFlight = null;
        if (completed is null || !ReferenceEquals(_flight, completed))
        {
            return;
        }

        if (!PreparePreciseFlight(completed, out var prepared))
        {
            return;
        }

        if (ReferenceEquals(completed, prepared))
        {
            StopFlight();
        }
    }

    /// <summary>How long the camera has to be still before names are drawn crisp again.</summary>
    private const double SettleMilliseconds = 350;

    /// <summary>Whether the camera moved in the last few frames.</summary>
    /// <summary>Transient camera frames must not turn a passing ancestor into a visited folder.</summary>
    internal bool IsCameraMoving => IsMoving();

    private bool IsMoving() =>
        _flight is not null
        || _lastMotion != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_lastMotion).TotalMilliseconds < 140;

    // ---- the phases' hooks ---------------------------------------------------
    //
    // A phase of the frame another part of the canvas owns is a partial method:
    // declared here, next to the order it runs in, and written in that part's
    // own file.  One nobody has written compiles to nothing, call and all.

    /// <summary>
    /// Phase 2, eased camera motion (NestedCanvas.Camera.cs): moves the camera
    /// <paramref name="dt"/> milliseconds on and says whether it is still
    /// moving.  Called on new frames only, and not while a flight is under way.
    /// </summary>
    partial void StepCameraMotion(double dt, ref bool active);

    /// <summary>
    /// Phase 3, changes on disk (NestedCanvas.Live.cs): takes what the change
    /// hub has for this canvas within <paramref name="budget"/>, one item of
    /// it per folder taken.
    /// </summary>
    partial void DrainChangeHub(ref FrameBudget budget);

    /// <summary>
    /// Phase 4, icons (NestedCanvas.Icons.cs): takes in the icons that arrived
    /// and asks for the names to be drawn again for them, as often as that is
    /// allowed.
    /// </summary>
    partial void DrainIconArrivals();

    /// <summary>
    /// Phase 6, soft updates (NestedCanvas.Transitions.cs): brings every
    /// transition under way to <paramref name="now"/>, ends the finished ones,
    /// and says whether any is still running.  New frames only.
    /// </summary>
    partial void StepTransitions(TimeSpan now, ref bool active);

    /// <summary>
    /// Phase 7 (NestedCanvas.Palette.cs): raises, once, the events the frame's
    /// intake held back - the filter's matches changed, say - after everything
    /// that could change them has come in.
    /// </summary>
    partial void FlushFrameEvents();

    /// <summary>Sets <paramref name="pending"/> when the change hub has work for this canvas (NestedCanvas.Live.cs); never clears it.</summary>
    partial void PendingHubWork(ref bool pending);

    /// <summary>Sets <paramref name="pending"/> when icons are waiting, or a redraw for them is held back (NestedCanvas.Icons.cs); never clears it.</summary>
    partial void PendingIconWork(ref bool pending);

    /// <summary>Sets <paramref name="pending"/> while a new filter is still being judged, a slice at a time (NestedCanvas.Palette.cs); never clears it.</summary>
    partial void PendingFilterWork(ref bool pending);

    /// <summary>
    /// Phase 8, the names (NestedCanvas.Labels.cs): sets the frame's allowance
    /// for laying out new text, just after it has been set to the count it
    /// always was - <see cref="MotionTextBudget"/> in motion, no limit at rest -
    /// so a time allowance can take its place.  Called once per frame that
    /// draws, before the names are drawn.
    /// </summary>
    partial void StartTextBudget(bool inMotion);

    /// <summary>
    /// Sets <paramref name="touches"/> when something the marks layer draws -
    /// a selection kept somewhere other than the canvas's own set of paths,
    /// say - is at or under <paramref name="folder"/>, just read, and so may
    /// have moved, appeared or gone; never clears it.  The canvas's own
    /// selection, drop target, beacons and trail are already asked about
    /// (<see cref="DecorReachesInto"/>).
    /// </summary>
    partial void DecorTouches(NestedFolder folder, ref bool touches);

    // ---- redrawing for folders read ------------------------------------------
    //
    // A folder read changes nothing outside its own cell: its sub-folders, its
    // files, the counts beside its name.  Most folders read are off screen, or
    // specks, or have no name drawn; redrawing the whole picture for every one
    // of them - the cells, thousands of names, the marks, and on the CPU a
    // full-screen raster and upload - was most of what a view full of folders
    // streaming in cost, frame after frame.  So each batch of applied reads is
    // asked what it touches, and only that is drawn: nothing at all for
    // folders not on screen, the cells alone for folders without a name on
    // screen, and on the CPU only the pixels of the cells that changed.  At
    // rest these redraws come at most every LoadRedrawMinMs, whatever the rate
    // folders arrive at; while the camera or a transition moves, they go with
    // the frame that is being drawn anyway.

    /// <summary>The tree whose batches are scoped here: the one whose reads this canvas drives (<see cref="Drive"/>).</summary>
    private NestedTree? _batchTree;

    /// <summary>
    /// The version the tree is left at by the change that ends the last scoped
    /// batch, which <see cref="OnTreeChanged"/> is to let pass; -1 for none.
    /// </summary>
    private int _batchVersion = -1;

    /// <summary>The layers folders read since the last loading redraw need drawn again, held back until it is their turn.</summary>
    private Layers _loadDirty;

    /// <summary>Where on screen, in DIPs, the cells of those folders are: the part of the scene they changed.</summary>
    private Rect _loadDirtyArea = Rect.Empty;

    /// <summary>Whether some of those folders changed more than their own cells - the filter's lights and fades around them - so the redraw is the whole picture.</summary>
    private bool _loadDirtyEverywhere;

    /// <summary>When, on the frame clock, the last loading redraw was drawn.</summary>
    private double _lastLoadRedrawMilliseconds = double.NegativeInfinity;

    /// <summary>What asks for the frame a held loading redraw is waiting for (<see cref="WaitForLoadRedraw"/>).</summary>
    private DispatcherTimer? _loadRedrawTimer;

    /// <summary>
    /// The part of the scene, in DIPs, the frame being drawn changed - the
    /// cells of the folders read - or empty when it is the whole picture.
    /// Only a frame whose scene is there for folders read alone has one.
    /// </summary>
    private Rect _sceneChangedArea = Rect.Empty;

    /// <summary>At most this many selected paths or beacons are each held against a folder read; with more, the marks are simply recorded again.</summary>
    private const int MaximumScopedMarks = 64;

    /// <summary>For tests and the bench: the layers the last frame drew (none when it drew nothing).</summary>
    internal Layers LastFrameLayers { get; private set; }

    /// <summary>For tests and the bench: how many times the names have been drawn.</summary>
    internal long LabelLayerCount { get; private set; }

    /// <summary>For the bench: batches of applied reads with nothing on screen to redraw.</summary>
    internal long LoadBatchesUnseen { get; private set; }

    /// <summary>For the bench: frames that held a loading redraw back to loading's rate.</summary>
    internal long LoadRedrawsHeld { get; private set; }

    /// <summary>For the bench: loading redraws drawn.</summary>
    internal long LoadRedraws { get; private set; }

    /// <summary>Starts or stops listening to a tree's batches of applied reads.</summary>
    private void FollowBatches(NestedTree? tree)
    {
        if (ReferenceEquals(_batchTree, tree))
        {
            return;
        }

        if (_batchTree is { } previous)
        {
            previous.BatchApplied -= OnBatchApplied;
        }

        _batchTree = tree;
        _batchVersion = -1;
        ForgetLoadRedraw();
        if (tree is not null)
        {
            tree.BatchApplied += OnBatchApplied;
        }
    }

    /// <summary>
    /// A batch of reads was applied to the tree: works out what on screen
    /// they touched, and marks only that to be drawn - see "redrawing for
    /// folders read" above.  Scoped by where each folder's cell is now, on
    /// the camera the last frame drew with: when the camera has moved since,
    /// the frame draws everything anyway.
    ///
    /// <list type="bullet">
    /// <item>A folder off screen, or narrower than any cell drawn, touches
    /// nothing.</item>
    /// <item>One on screen touches the scene - its cell's contents - and the
    /// pointer's layer, whose outline and tag may be on something in it.</item>
    /// <item>The names only if its own name was drawn: nothing inside a folder
    /// carries a name unless the folder does.</item>
    /// <item>The marks only if something they show is in it: the selection,
    /// the drop target, a beacon, or the trail, which follows the grids of
    /// the folders whose titles have scrolled off the top.</item>
    /// </list>
    ///
    /// Everything is drawn again when there is nothing to scope against: at
    /// once when the drives and roots were replaced or no frame has been
    /// drawn yet, and as a loading redraw, at loading's rate, when the name
    /// filter is on, which lights and fades the folders around one read as
    /// well as the folder itself, and when a folder above the camera's own
    /// was placed anew, which moves everything round the camera's folder
    /// (<see cref="MovedAroundAnchor"/>).
    /// </summary>
    private void OnBatchApplied(NestedChangeBatch batch)
    {
        if (_batchTree is not { } tree || !ReferenceEquals(tree, _tree))
        {
            return;
        }

        // The change this batch ends with says the same thing again, and less
        // exactly: it is let pass.
        _batchVersion = tree.Version + 1;
        if (batch.RootsChanged || !_hasCamera || _chain.Count == 0)
        {
            RequestFrame(Layers.All);
            return;
        }

        if (_filter is not null)
        {
            // Everything, but still a loading redraw: at rest, at loading's rate.
            _loadDirtyEverywhere = true;
            HoldLoadRedraw(Layers.All);
            return;
        }

        var layers = Layers.None;
        foreach (var folder in batch.Applied)
        {
            if (MovedAroundAnchor(folder))
            {
                _loadDirtyEverywhere = true;
                HoldLoadRedraw(Layers.All);
                return;
            }

            if (!TryDrawnCell(folder, out var cell))
            {
                continue;
            }

            layers |= Layers.Scene | Layers.Overlay;
            _loadDirtyArea.Union(cell);
            if (_labelled.Contains(folder))
            {
                layers |= Layers.Labels;
            }

            if ((layers & Layers.Decor) == 0 && DecorReachesInto(folder, cell))
            {
                layers |= Layers.Decor;
            }
        }

        if (layers == Layers.None)
        {
            LoadBatchesUnseen++;
            return;
        }

        HoldLoadRedraw(layers);
    }

    /// <summary>
    /// Whether <paramref name="folder"/>, just read, is one of the folders
    /// above the camera's own and placed the way down to it anew - a sub-folder
    /// more, say, so the one the view is in is smaller.  The camera holds its
    /// folder still, so then this folder and everything round it moved: every
    /// cell, name and mark on screen outside the camera's folder, not only
    /// what is in this folder's cell.  Worked out from the folder below it on
    /// the chain the last frame drew, exactly as that frame worked it out.
    /// </summary>
    private bool MovedAroundAnchor(NestedFolder folder)
    {
        if (ReferenceEquals(folder, _anchor) || !_chain.TryGetValue(folder, out var drawn))
        {
            return false;
        }

        for (var below = _anchor; below?.Parent is { } parent; below = parent)
        {
            if (ReferenceEquals(parent, folder))
            {
                if (!_chain.TryGetValue(below, out var rect))
                {
                    return true;
                }

                PlaceParent(below, rect.X, rect.Y, rect.W, out var x, out var y, out var w);
                return x != drawn.X || y != drawn.Y || w != drawn.W;
            }
        }

        return false;
    }

    /// <summary>
    /// Adds <paramref name="layers"/> to the loading redraw and makes sure a
    /// frame will come for it - unless one already waiting its turn takes
    /// this with it, whose timer asks for the frame.
    /// </summary>
    private void HoldLoadRedraw(Layers layers)
    {
        _loadDirty |= layers;
        if (_loadRedrawTimer is not { IsEnabled: true })
        {
            RequestFrame(Layers.None);
        }
    }

    /// <summary>
    /// Phase 8: the redraw folders read are waiting for joins this frame, and
    /// says whether the frame's scene is there for them alone (it is
    /// <i>scoped</i>).  At rest it waits its turn - at most one every
    /// <see cref="FrameBudgets.LoadRedrawMinMs"/> - unless the frame draws what
    /// it needs anyway; with the camera or a transition moving it never waits.
    /// </summary>
    private bool TakeLoadRedraw(bool moving)
    {
        if (_loadDirty == Layers.None)
        {
            return false;
        }

        var now = _clock.Now.TotalMilliseconds;
        var wholeScene = (_dirty & Layers.Scene) != 0;

        // Never held from a frame that draws the names of folders just read:
        // the names are drawn from the last walk's tiles, which point into
        // the files as they were before the read - held, the frame would
        // write a neighbour's name and icon on a tile.  Joined, the walk is
        // done again first.
        if (!moving
            && !_transitionsActive
            && !wholeScene
            && (_dirty & _loadDirty & Layers.Labels) == 0
            && (_loadDirty & ~_dirty) != 0
            && now - _lastLoadRedrawMilliseconds < FrameBudgets.LoadRedrawMinMs)
        {
            LoadRedrawsHeld++;
            WaitForLoadRedraw(FrameBudgets.LoadRedrawMinMs - (now - _lastLoadRedrawMilliseconds));
            return false;
        }

        var scoped = !wholeScene && !_loadDirtyEverywhere;
        _sceneChangedArea = scoped ? _loadDirtyArea : Rect.Empty;
        _dirty |= _loadDirty;
        _lastLoadRedrawMilliseconds = now;
        LoadRedraws++;
        ForgetLoadRedraw();
        return scoped;
    }

    /// <summary>
    /// Holds a loading redraw back for <paramref name="milliseconds"/> with a
    /// timer that asks for a frame then, rather than with the loop.  A loop
    /// kept hooked frame after frame with nothing to draw is not free: WPF
    /// treats the canvas as animating, and folders whose reads come back
    /// through the dispatcher were measured coming back at half the rate.
    /// </summary>
    private void WaitForLoadRedraw(double milliseconds)
    {
        if (_loadRedrawTimer is null)
        {
            _loadRedrawTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher);
            _loadRedrawTimer.Tick += OnLoadRedrawDue;
        }

        if (!_loadRedrawTimer.IsEnabled)
        {
            _loadRedrawTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, milliseconds));
            _loadRedrawTimer.Start();
        }
    }

    private void OnLoadRedrawDue(object? sender, EventArgs e)
    {
        _loadRedrawTimer?.Stop();
        if (_loadDirty != Layers.None)
        {
            RequestFrame(Layers.None);
        }
    }

    /// <summary>Drops the redraw folders read were waiting for: everything is being drawn anyway, or the tree is another.</summary>
    private void ForgetLoadRedraw()
    {
        _loadRedrawTimer?.Stop();
        _loadDirty = Layers.None;
        _loadDirtyArea = Rect.Empty;
        _loadDirtyEverywhere = false;
    }

    /// <summary>
    /// Where <paramref name="folder"/>'s cell is on screen, if it is one of the
    /// cells a frame would draw now: on screen, and at least
    /// <see cref="MinimumCellPixels"/> wide.  As the last frame placed it -
    /// the same steps the walk takes down from the camera's chain.
    /// </summary>
    private bool TryDrawnCell(NestedFolder folder, out Rect cell)
    {
        cell = Rect.Empty;
        if (!TryPlacedCell(folder, out var x, out var y, out var w) || w < MinimumCellPixels)
        {
            return false;
        }

        var h = w * NestedLayout.CellHeight;
        if (x >= _viewWidth || y >= _viewHeight || x + w <= 0 || y + h <= 0)
        {
            return false;
        }

        cell = new Rect(x, y, w, h);
        return true;
    }

    /// <summary>
    /// A folder's cell, from the chain if it is on it and otherwise placed in
    /// its parent's, as the walk places it; false for a folder that is not a
    /// cell at all - hidden, forgotten, or cut off above.  Allocates nothing:
    /// a batch can be hundreds of folders, and this runs for each.
    /// </summary>
    private bool TryPlacedCell(NestedFolder folder, out double x, out double y, out double w)
    {
        if (_chain.TryGetValue(folder, out var known))
        {
            (x, y, w) = known;
            return true;
        }

        x = y = w = 0;
        if (folder.Index < 0 || folder.IsForgotten || folder.Parent is not { } parent
            || !TryPlacedCell(parent, out var parentX, out var parentY, out var parentWidth))
        {
            return false;
        }

        Place(parent, folder, parentX, parentY, parentWidth, out x, out y, out w);
        return true;
    }

    /// <summary>
    /// Whether anything the marks layer draws may have changed with
    /// <paramref name="folder"/>, whose <paramref name="cell"/> is on screen,
    /// being read: something selected, dropped on or marked with a beacon at
    /// or under it, or the trail - which walks down the grids under a point
    /// near the top of the view through every folder whose title is above
    /// the top edge - passing through it.  With many selected paths or
    /// beacons, rather than hold each one against the folder, yes.
    /// </summary>
    private bool DecorReachesInto(NestedFolder folder, Rect cell)
    {
        var trailProbe = new Point(_viewWidth / 2, Math.Min(_viewHeight / 2, 80));
        if (cell.Y + cell.Width * NestedLayout.HeaderHeight < 0 && cell.Contains(trailProbe))
        {
            return true;
        }

        for (var drop = _dropTarget; drop is not null; drop = drop.Parent)
        {
            if (ReferenceEquals(drop, folder))
            {
                return true;
            }
        }

        var root = folder.FullPath;
        if (_selected.Count > MaximumScopedMarks || _beacons.Count > MaximumScopedMarks)
        {
            return true;
        }

        foreach (var path in _selected)
        {
            if (IsAtOrUnder(path, root))
            {
                return true;
            }
        }

        foreach (var beacon in _beacons)
        {
            if (IsAtOrUnder(beacon.Path, root))
            {
                return true;
            }
        }

        var touches = false;
        DecorTouches(folder, ref touches);
        return touches;
    }

    /// <summary>Whether <paramref name="path"/> is the folder <paramref name="root"/> or anything under it.</summary>
    private static bool IsAtOrUnder(string path, string root)
    {
        if (root.Length == 0)
        {
            return true;
        }

        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && (path.Length == root.Length
                || root[^1] == Path.DirectorySeparatorChar
                || path[root.Length] == Path.DirectorySeparatorChar);
    }

    // ---- rendering -----------------------------------------------------------

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _bitmap = null;
        _textCache.Clear();
        _oldTextCache.Clear();
        RequestFrame(Layers.All);
    }

    /// <summary>
    /// The tree changed: everything again - except for the change that ends a
    /// batch of applied reads, which the batch itself has already said the
    /// scope of (<see cref="OnBatchApplied"/>).  That one is told apart by
    /// the version it leaves the tree at, not by a flag, so a batch no change
    /// followed can never swallow the next, unrelated one.
    /// </summary>
    private void OnTreeChanged(object? sender, EventArgs e)
    {
        if (sender is NestedTree visibility && ReferenceEquals(visibility, _tree))
            RejudgeFilterVisibility(visibility);

        if (sender is NestedTree tree && ReferenceEquals(tree, _batchTree) && tree.Version == _batchVersion)
        {
            _batchVersion = -1;
            return;
        }

        // A file type or hidden rule can bring back selected names from a
        // cached listing without loading its folder again. Those names have
        // waited in the selection since the tiles disappeared; FolderLoaded
        // will not run for this visibility-only change.
        if (sender is NestedTree changed && ReferenceEquals(changed, _tree))
            ReselectWaitingFiles(changed);

        RequestFrame(Layers.All);
    }

    /// <summary>
    /// The control's own drawing is only a transparent sheet, so every point
    /// of it takes the mouse; the picture is in the layers above.  Outside a
    /// window - a test, a snapshot - there is no frame loop, so the layers are
    /// drawn here and then.
    /// </summary>
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (PresentationSource.FromVisual(this) is null)
        {
            _dirty = Layers.None;
            ForgetLoadRedraw();
            RenderLayers(Layers.All, inMotion: false);
        }
        else
        {
            RequestFrame(Layers.All);
        }
    }

    /// <summary>
    /// Takes the canvas's DPI scale, from the window or from a test's
    /// <see cref="DpiOverride"/>; true when it changed, which makes the
    /// bitmap, the GPU surface and every text layout out of date.
    /// </summary>
    private bool UpdateScale()
    {
        var dpi = DpiOverride ?? VisualTreeHelper.GetDpi(this);
        if (dpi.DpiScaleX == _scaleX && dpi.DpiScaleY == _scaleY)
        {
            return false;
        }

        _scaleX = dpi.DpiScaleX;
        _scaleY = dpi.DpiScaleY;
        _bitmap = null;
        _textCache.Clear();
        _oldTextCache.Clear();
        return true;
    }

    /// <summary>
    /// A DPI scale to draw at instead of the window's: for tests, and for the
    /// bench's stand-in for a 150 % monitor on a 100 % one (<c>--bench-scale</c>).
    /// </summary>
    internal DpiScale? DpiOverride { get; set; }

    /// <summary>
    /// Draws the layers asked for; false when the frame could not be shown -
    /// the GPU's previous frame was still being copied - and was left for the
    /// next tick, which is already asked for.
    ///
    /// A new scene is a new picture, so it takes the names, marks and
    /// pointer's layer with it - unless the frame is <paramref name="scoped"/>:
    /// its scene is only there for folders read, whose batch said which of the
    /// other layers they touch (<see cref="OnBatchApplied"/>), and the rest are
    /// left as they are - all but the names, when they were in a GPU picture
    /// this scene is no longer drawn on.
    /// </summary>
    private bool RenderLayers(Layers layers, bool inMotion, bool scoped = false)
    {
        // Hidden or not laid out yet: draw nothing, and keep the size the
        // camera was set up for rather than forgetting it.  Nothing is left
        // drawn with less detail or with text set for motion either: the
        // loop waits for a frame at full quality before it lets go
        // (IsIdleWith), and with nothing drawn that frame would never come -
        // the loop would stay hooked to every frame WPF draws.
        if (ActualWidth < 1 || ActualHeight < 1 || _tree is null)
        {
            _lod = 1;
            _lodDegraded = false;
            if (_textAnimated)
            {
                _textAnimated = false;
                TextOptions.SetTextHintingMode(_labelVisual, TextHintingMode.Auto);
                TextOptions.SetTextHintingMode(_decorVisual, TextHintingMode.Auto);
            }

            return true;
        }

        _viewWidth = ActualWidth;
        _viewHeight = ActualHeight;
        if (UpdateScale())
        {
            layers = Layers.All;
            scoped = false;
        }

        EnsureCamera();
        Normalize();
        BuildChain();

        // Text is set for motion while the camera moves: WPF then scales the
        // glyphs it already has instead of rendering every size afresh, which
        // is most of what a zoom over a folder of names used to cost.
        var animated = inMotion;
        if (animated != _textAnimated)
        {
            _textAnimated = animated;
            var mode = animated ? TextHintingMode.Animated : TextHintingMode.Auto;
            TextOptions.SetTextHintingMode(_labelVisual, mode);
            TextOptions.SetTextHintingMode(_decorVisual, mode);
            layers |= Layers.Labels | Layers.Decor;
        }

        _presentPending = false;
        LastUploadMilliseconds = 0;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var sceneAllocated = 0L;
        if ((layers & Layers.Scene) != 0)
        {
            RenderScene(inMotion);
            if (!scoped)
            {
                layers |= Layers.Labels | Layers.Decor | Layers.Overlay;
            }
            else if (_labelsOnGpu && !_presentPending)
            {
                // The names were in the GPU's picture alone, and this frame's
                // cells went to the bitmap - the card let go of since the last
                // frame: they are drawn with WPF now, or there would be none
                // until something else drew them.
                layers |= Layers.Labels;
            }

            sceneAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        }

        allocated = GC.GetAllocatedBytesForCurrentThread();
        var layerStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        NewTextLayouts = 0;
        _textBudget = inMotion ? MotionTextBudget : int.MaxValue;
        _textDeferred = false;
        StartTextBudget(inMotion);
        if ((layers & Layers.Labels) != 0)
        {
            LabelLayerCount++;
            if (!TryDrawLabelsOnGpu(inMotion))
            {
                DrawLabelsWithWpf();
            }
        }

        LastLabelsMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(layerStarted).TotalMilliseconds;
        var labelsAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

        // On the CPU the marks and the pointer's layer are recorded here, as
        // they always were.  Over the GPU's picture they wait for the present
        // below: a present WPF's render thread was too late for leaves the
        // surface showing the previous frame, and outlines, beacons and the
        // hover recorded for the new camera would sit offset from the cells
        // under them for a frame.  Recorded only once the frame they belong
        // to is shown, they never lag the cells or run ahead of them.
        var decorAfterPresent = _presentPending;
        var decorAllocated = decorAfterPresent ? 0 : RecordDecorAndOverlay(layers);
        allocated = GC.GetAllocatedBytesForCurrentThread();

        // The GPU's scene last, once the frame's other CPU work is done: the
        // image lock waits for WPF's render thread to finish copying the
        // previous frame, and the walk and the names gave it that time.
        var shown = !_presentPending || PresentScene();
        var presentAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (decorAfterPresent)
        {
            if (shown)
            {
                decorAllocated = RecordDecorAndOverlay(layers);
            }
            else
            {
                LastDecorMilliseconds = 0;
            }
        }

        // The names and the marks the hover is taken from are drawn for this
        // camera now, and shown: a hover worked out for the camera before -
        // by the wheel, or none at all for a flight - is worked out again.
        if (_hoverStale && (layers & (Layers.Labels | Layers.Decor)) == (Layers.Labels | Layers.Decor) && (!decorAfterPresent || shown))
        {
            RefreshHover();
        }

        LastAllocations = new FrameAllocations(sceneAllocated, labelsAllocated, decorAllocated, presentAllocated);
        LastFrameLayers = layers;
        return shown;
    }

    /// <summary>
    /// The frame's marks - filter outlines, selection, drop target, beacons,
    /// trail - and the pointer's layer, when they are among
    /// <paramref name="layers"/>; the text cache's generation turned when it
    /// is full.  Returns what it handed the garbage collector.
    /// </summary>
    private long RecordDecorAndOverlay(Layers layers)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var layerStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        if ((layers & Layers.Decor) != 0)
        {
            _hotspots.Clear();
            using var dc = _decorVisual.RenderOpen();
            foreach (var outline in _filterOutlines)
            {
                var radius = outline.Width >= 40 ? Math.Min(6, outline.Width * 0.03) : 1;
                dc.DrawRoundedRectangle(null, FilterPen, outline, radius, radius);
            }

            DrawSelection(dc);
            DrawDropTarget(dc);
            DrawBeacons(dc, _tree?.Version ?? -1, _tree?.SortGeneration ?? -1);
            DrawTrail(dc);
            DrawFavoriteLinks(dc);
        }

        LastDecorMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(layerStarted).TotalMilliseconds;
        if (_textDeferred)
        {
            // Some names waited for their layout: the next frame has budget again.
            RequestFrame(Layers.Labels | Layers.Decor);
        }

        if ((layers & Layers.Overlay) != 0)
        {
            RenderOverlay();
        }

        if (_textCache.Count > 4000)
        {
            // Two generations rather than one clear: what is still in use is
            // carried over on its next lookup, so no frame has to lay out
            // every name on screen at once.
            (_oldTextCache, _textCache) = (_textCache, _oldTextCache);
            _textCache.Clear();
        }

        return GC.GetAllocatedBytesForCurrentThread() - allocated;
    }

    /// <summary>What the last frame handed the garbage collector, layer by layer: the scene, the names, the marks and the pointer's layer together, and the present.</summary>
    public FrameAllocations LastAllocations { get; private set; }

    /// <summary>The four layers the picture is drawn in, each redrawn only when what it shows changed.</summary>
    [Flags]
    internal enum Layers
    {
        None = 0,
        Scene = 1,
        Labels = 2,
        Decor = 4,
        Overlay = 8,
        All = Scene | Labels | Decor | Overlay
    }
}
