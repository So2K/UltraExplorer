using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Windows.Threading;

namespace UltraExplorer.Controls;

/// <summary>
/// How much of one frame each kind of work the canvas takes in may spend on
/// the UI thread.  A frame at 120 Hz is 8.33 ms, and WPF's own work, the
/// input waiting behind it and the picture itself need most of that: what
/// other threads hand over - finished reads, icons, changes on disk - is
/// taken in slices that together leave at least half of the frame idle
/// (<see cref="UiFrameTargetMs"/>), and whatever does not fit waits for the
/// next frame rather than making this one late.
/// </summary>
internal static class FrameBudgets
{
    /// <summary>Changes on disk taken from the change hub per frame: at most this long...</summary>
    public const double HubMs = 0.5;

    /// <summary>...and at most this many folders.</summary>
    public const int HubItems = 256;

    /// <summary>Finished reads applied to the tree per frame.</summary>
    public const double ApplyMs = 2.0;

    /// <summary>Recording where a refreshed folder's children were, before the refresh moves them, per two thousand of them.</summary>
    public const double CaptureMsPer2k = 0.3;

    /// <summary>Icons copied into the GPU's icon atlas per frame.</summary>
    public const int IconUploads = 64;

    /// <summary>Names shaped for the GPU's glyphs per frame.</summary>
    public const double ShapeMs = 1.5;

    /// <summary>New text layouts per frame while the camera is still.</summary>
    public const double RestTextMs = 3.0;

    /// <summary>New text layouts per frame while the camera moves.</summary>
    public const double MotionTextMs = 1.5;

    /// <summary>At rest, a redraw only because more folders were read comes at most this often: about thirty a second.</summary>
    public const double LoadRedrawMinMs = 33;

    /// <summary>At rest, the names are drawn again for arrived icons at most this often: about fifteen a second.</summary>
    public const double IconRefreshMinMs = 66;

    /// <summary>What a whole frame should cost the UI thread while folders stream in, so half a 120 Hz frame is left for input.</summary>
    public const double UiFrameTargetMs = 4.0;

    /// <summary>What one pass of a <see cref="DispatcherFrameDriver"/> may spend, when no frame loop is there to do the work.</summary>
    public const double FallbackMs = 2.0;
}

/// <summary>
/// A slice of a frame for one kind of work: a deadline on the
/// <see cref="Stopwatch"/>'s clock and, optionally, a number of items.  The
/// work takes items one at a time, asks <see cref="Spent"/> after each, and
/// leaves the rest for the next frame once it is.  A value, so a budget costs
/// nothing to make and is handed down by reference.
/// </summary>
internal struct FrameBudget
{
    /// <summary>The <see cref="Stopwatch"/> timestamp the work must stop by.</summary>
    public long Deadline;

    /// <summary>Items the work may still take; <see cref="int.MaxValue"/> when only time counts.</summary>
    public int Items;

    /// <summary>A budget of <paramref name="milliseconds"/> from now, and at most <paramref name="items"/> items.</summary>
    public static FrameBudget Start(double milliseconds, int items = int.MaxValue) => new()
    {
        Deadline = Stopwatch.GetTimestamp() + (long)(milliseconds * Stopwatch.Frequency / 1000),
        Items = items
    };

    /// <summary>A budget that is never spent: for a test, or for work that must all be done now.</summary>
    public static FrameBudget Unlimited => new() { Deadline = long.MaxValue, Items = int.MaxValue };

    /// <summary>Whether the work must stop: its items are used up or its time is.</summary>
    public readonly bool Spent => Items <= 0 || Stopwatch.GetTimestamp() >= Deadline;

    /// <summary>Counts <paramref name="count"/> items as taken.</summary>
    public void Take(int count = 1) => Items -= count;
}

/// <summary>
/// Whatever runs the work a <see cref="FrameInbox{T}"/> has waiting: the
/// canvas's frame loop, or a dispatcher operation when no frame loop is
/// there to do it (<see cref="DispatcherFrameDriver"/>).
/// </summary>
internal interface IFrameDriver
{
    /// <summary>
    /// Asks for the waiting work to be taken in soon.  Called from any thread,
    /// never blocks, and costs next to nothing when a wake is already under
    /// way: the driver serves every wake before its next pass with that one pass.
    /// </summary>
    void Wake();
}

/// <summary>
/// Which driver an inbox wakes: the <see cref="Active"/> one - the canvas,
/// while it is there to draw - or else the <see cref="Fallback"/>, so work
/// posted while no canvas is showing is still taken in (a folder revealed
/// while the tree canvas is up, a change on disk while the folder list is
/// the only view).  Shared by every inbox that feeds the same consumer.
/// </summary>
internal sealed class FrameDriverSlot(IFrameDriver fallback)
{
    private IFrameDriver? _active;

    /// <summary>The driver that takes the work in while there is one; null leaves it to <see cref="Fallback"/>.</summary>
    public IFrameDriver? Active
    {
        get => Volatile.Read(ref _active);
        set => Volatile.Write(ref _active, value);
    }

    /// <summary>The driver that takes the work in when no other does.</summary>
    public IFrameDriver Fallback { get; set; } = fallback;

    /// <summary>Wakes whichever driver is in charge.  Any thread.</summary>
    public void Wake() => (Active ?? Fallback).Wake();
}

/// <summary>Takes in waiting work within a budget: what a driver runs.</summary>
internal delegate void FrameDrain(ref FrameBudget budget);

/// <summary>
/// The driver for when no frame loop is running: a hidden canvas, a tree with
/// no canvas, a window showing only the folder list or the graph.  Each wake
/// posts one operation to the dispatcher - one, however many wakes arrive
/// before it runs - at Input priority, so it goes ahead of the Background work
/// a frame loop would starve and behind nothing the user is doing.  The
/// operation drains within <see cref="FrameBudgets.FallbackMs"/>, and posts
/// itself again while work is left.
/// </summary>
internal sealed class DispatcherFrameDriver : IFrameDriver
{
    private readonly Dispatcher _dispatcher;
    private readonly FrameDrain _drain;
    private readonly Func<bool> _hasWork;
    private readonly DispatcherPriority _priority;
    private readonly Action _run;
    private int _posted;
    private int _posts;
    private int _runs;

    public DispatcherFrameDriver(Dispatcher dispatcher, FrameDrain drain, Func<bool> hasWork, DispatcherPriority priority = DispatcherPriority.Input)
    {
        _dispatcher = dispatcher;
        _drain = drain;
        _hasWork = hasWork;
        _priority = priority;
        _run = Run;
    }

    /// <summary>Operations posted so far, for tests: one per wake that found none waiting.</summary>
    public int Posts => Volatile.Read(ref _posts);

    /// <summary>Passes run so far, for tests.</summary>
    public int Runs => Volatile.Read(ref _runs);

    /// <summary>
    /// A driver for the calling thread: this one on a thread running a
    /// dispatcher, otherwise one that drains on the spot (see
    /// <see cref="ImmediateFrameDriver"/>).  A dispatcher only counts when it
    /// is the thread's synchronisation context too - a thread can have a
    /// dispatcher nobody runs, and an operation posted there would wait for ever.
    /// </summary>
    public static IFrameDriver ForCurrentThread(FrameDrain drain, Func<bool> hasWork) =>
        SynchronizationContext.Current is DispatcherSynchronizationContext && Dispatcher.FromThread(Thread.CurrentThread) is { } dispatcher
            ? new DispatcherFrameDriver(dispatcher, drain, hasWork)
            : new ImmediateFrameDriver(drain, hasWork);

    public void Wake()
    {
        if (Interlocked.Exchange(ref _posted, 1) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _posts);
        _dispatcher.BeginInvoke(_priority, _run);
    }

    private void Run()
    {
        Volatile.Write(ref _posted, 0);
        Interlocked.Increment(ref _runs);
        var budget = FrameBudget.Start(FrameBudgets.FallbackMs);
        _drain(ref budget);
        if (_hasWork())
        {
            Wake();
        }
    }
}

/// <summary>
/// The driver for a thread with no dispatcher - a console's, a test's: the
/// waiting work is drained there and then, on whichever thread woke it, one
/// thread at a time.  That is where a finished read's continuation ran on
/// such a thread before there were inboxes, the thread pool.
/// </summary>
internal sealed class ImmediateFrameDriver(FrameDrain drain, Func<bool> hasWork) : IFrameDriver
{
    private readonly Lock _gate = new();

    public void Wake()
    {
        lock (_gate)
        {
            do
            {
                var budget = FrameBudget.Unlimited;
                drain(ref budget);
            }
            while (hasWork());
        }
    }
}

/// <summary>
/// Results from other threads - a finished read, an icon, a change on disk -
/// waiting for the UI thread, which takes them in at the start of a frame
/// within its budget.  Posting never touches the dispatcher: the first post
/// after a drain wakes the driver, and every post after it until the next
/// drain only joins the queue, so a burst of ten thousand results is one wake
/// and one frame, not ten thousand operations queued behind the input.
///
/// <para>The consumer calls <see cref="Rearm"/> when it starts a drain, then
/// takes items until it runs out of them or of budget.  Whatever it leaves is
/// its to come back for (it keeps its frame loop going while
/// <see cref="IsEmpty"/> is false); whatever arrives after the rearm wakes it
/// again.</para>
/// </summary>
internal sealed class FrameInbox<T>(FrameDriverSlot driver)
{
    private readonly ConcurrentQueue<T> _items = new();
    private int _signalled;

    /// <summary>The driver a post wakes.</summary>
    public FrameDriverSlot Driver { get; } = driver;

    /// <summary>Items waiting.</summary>
    public int Count => _items.Count;

    public bool IsEmpty => _items.IsEmpty;

    /// <summary>Queues an item, and wakes the driver if nothing has since the last drain began.  Any thread.</summary>
    public void Post(in T item)
    {
        _items.Enqueue(item);
        if (Interlocked.Exchange(ref _signalled, 1) == 0)
        {
            Driver.Wake();
        }
    }

    /// <summary>
    /// At the start of a drain: the next post wakes the driver again.  A full
    /// fence, so a post that finds the inbox still signalled is always one
    /// whose item this drain will see.
    /// </summary>
    public void Rearm() => Interlocked.Exchange(ref _signalled, 0);

    public bool TryTake([MaybeNullWhen(false)] out T item) => _items.TryDequeue(out item);
}

/// <summary>
/// The canvas's clock, read from WPF's frames rather than from the moment a
/// handler happens to run: <see cref="Advance"/> takes each Rendering call's
/// RenderingTime - the time the frame will be shown, free of the
/// millisecond or two of jitter a <see cref="Stopwatch"/> read in the handler
/// carries - tells a new frame from WPF raising Rendering again within the
/// same one, and says how far motion should move this frame.
///
/// <para>The step is the gap since the last frame, capped at
/// <see cref="DtMaxMilliseconds"/> so one long frame does not jump.  The
/// first frame after a rest steps one nominal frame instead, so motion that
/// starts on it moves at once; the nominal frame follows the display's rate
/// (8.33 ms at 120 Hz, 4.18 at 239) from every ordinary gap.</para>
/// </summary>
internal struct FrameClock
{
    /// <summary>The nominal frame before any has been seen: 120 Hz.</summary>
    public const double InitialNominalMilliseconds = 8.33;

    /// <summary>The longest step a frame takes, however long it was since the last one.</summary>
    public const double DtMaxMilliseconds = 33;

    private TimeSpan _frameTime;
    private long _frameStamp;
    private double _nominal;
    private bool _started;
    private bool _inFrame;

    public FrameClock()
    {
        _frameStamp = Stopwatch.GetTimestamp();
        _nominal = InitialNominalMilliseconds;
    }

    /// <summary>Whether the frame <see cref="Advance"/> last took was a new one rather than Rendering raised again within it.</summary>
    public bool Fresh { get; private set; }

    /// <summary>How far motion moves this frame, in milliseconds; zero on a repeated call.</summary>
    public double DeltaMilliseconds { get; private set; }

    /// <summary>What a frame is taken to last on this display, in milliseconds.</summary>
    public readonly double NominalMilliseconds => _nominal > 0 ? _nominal : InitialNominalMilliseconds;

    /// <summary>
    /// The time on the frames' clock: inside a frame, that frame's
    /// RenderingTime, the same however often it is asked; between frames -
    /// work done by a fallback driver, say - the last frame's time plus what
    /// the <see cref="Stopwatch"/> says has passed since.
    /// </summary>
    public readonly TimeSpan Now => _inFrame ? _frameTime : _frameTime + Stopwatch.GetElapsedTime(_frameStamp);

    /// <summary>
    /// Takes a Rendering call's time and starts the frame; true when it is a
    /// new frame.  <paramref name="wasActive"/> says whether anything moved
    /// on the last frame: after a rest the step is one nominal frame.
    /// </summary>
    public bool Advance(TimeSpan renderingTime, bool wasActive)
    {
        _inFrame = true;
        if (_started && renderingTime == _frameTime)
        {
            Fresh = false;
            DeltaMilliseconds = 0;
            return false;
        }

        var nominal = NominalMilliseconds;
        var gap = _started ? (renderingTime - _frameTime).TotalMilliseconds : double.PositiveInfinity;
        if (gap > 2 && gap < 50)
        {
            nominal += 0.1 * (gap - nominal);
        }

        _nominal = nominal;
        DeltaMilliseconds = !wasActive || gap > 100 ? nominal : Math.Clamp(gap, 0, DtMaxMilliseconds);
        _frameTime = renderingTime;
        _frameStamp = Stopwatch.GetTimestamp();
        _started = true;
        Fresh = true;
        return true;
    }

    /// <summary>Ends the frame <see cref="Advance"/> started: <see cref="Now"/> runs on from it.</summary>
    public void EndFrame() => _inFrame = false;
}

/// <summary>
/// What one frame of the canvas's loop did, phase by phase: for the bench's
/// per-frame columns, and for tests.  Filled by the loop as it goes; a phase
/// whose owner is not built in leaves its fields at zero.
/// </summary>
internal struct FrameStats
{
    /// <summary>Whether the frame was a new one, rather than Rendering raised again within one.</summary>
    public bool Fresh;

    public double CameraMs;
    public double HubMs;
    public double IconMs;
    public double ApplyMs;
    public double CaptureMs;
    public double TransitionMs;

    /// <summary>The layers' drawing, when the frame drew any (<see cref="NestedCanvas.LastFrameMilliseconds"/>).</summary>
    public double RenderMs;

    /// <summary>Folders taken from the change hub.</summary>
    public int HubItems;

    /// <summary>Finished reads applied.</summary>
    public int Applied;

    /// <summary>Transitions under way, for whatever runs them to count.</summary>
    public int Transitions;

    /// <summary>Whether a flight or eased motion moved the camera.</summary>
    public bool CameraActive;

    /// <summary>Whether the frame drew nothing: nothing was out of date.</summary>
    public bool Skipped;
}
