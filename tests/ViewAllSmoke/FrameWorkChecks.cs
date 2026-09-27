using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The frame work every later part of the canvas stands on: inboxes that wake
/// their driver once however many results are posted, budgets that stop on
/// time, the frame clock, a hidden canvas handing its inboxes to the
/// Input-priority fallback, the frame loop letting go the frame it has nothing
/// left to do, and the tree's batch of applied folders.
/// </summary>
internal static partial class Program
{
    private static Task FrameWorkChecks()
    {
        Section("frame work");
        FrameInboxChecks();
        FrameBudgetChecks();
        FrameClockChecks();
        RunOnSta("frame work on a dispatcher", FrameWorkDispatcherChecks);
        return Task.CompletedTask;
    }

    /// <summary>A driver that only counts its wakes, and lets a consumer thread wait for one.</summary>
    private sealed class CountingDriver : IFrameDriver
    {
        private int _wakes;

        public SemaphoreSlim Woken { get; } = new(0);

        public int Wakes => Volatile.Read(ref _wakes);

        public void Wake()
        {
            Interlocked.Increment(ref _wakes);
            Woken.Release();
        }
    }

    private static void FrameInboxChecks()
    {
        // Ten thousand posts from four threads, nobody draining: one wake.
        var driver = new CountingDriver();
        var inbox = new FrameInbox<int>(new FrameDriverSlot(driver));
        Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, producer =>
        {
            for (var index = 0; index < 2_500; index++)
            {
                inbox.Post(producer * 2_500 + index);
            }
        });
        Check($"10,000 posts from 4 threads wake the driver once ({driver.Wakes})", driver.Wakes == 1 && inbox.Count == 10_000);

        inbox.Rearm();
        var taken = 0;
        while (inbox.TryTake(out _))
        {
            taken++;
        }

        inbox.Post(1);
        inbox.Post(2);
        Check("a drain takes them all, and the next posts wake it once more", taken == 10_000 && driver.Wakes == 2 && inbox.Count == 2);

        // Posting and draining at once: a consumer drains on every wake while
        // four producers post.  A lost wake would leave it waiting for ever;
        // each wake is one drain, and every item arrives, in order per producer.
        var live = new CountingDriver();
        var liveInbox = new FrameInbox<(int Producer, int Sequence)>(new FrameDriverSlot(live));
        const int perProducer = 25_000;
        var last = new[] { -1, -1, -1, -1 };
        var received = 0;
        var inOrder = true;
        var cycles = 0;
        var consumer = Task.Run(() =>
        {
            while (received < 4 * perProducer)
            {
                if (!live.Woken.Wait(TimeSpan.FromSeconds(5)))
                {
                    return false;
                }

                cycles++;
                liveInbox.Rearm();
                while (liveInbox.TryTake(out var item))
                {
                    inOrder &= item.Sequence == last[item.Producer] + 1;
                    last[item.Producer] = item.Sequence;
                    received++;
                }
            }

            return true;
        });
        Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, producer =>
        {
            for (var sequence = 0; sequence < perProducer; sequence++)
            {
                liveInbox.Post((producer, sequence));
            }
        });
        var finished = consumer.Wait(TimeSpan.FromSeconds(30)) && consumer.Result;
        Check($"posting while draining loses no wake and no item ({received:N0} of {4 * perProducer:N0} in {cycles:N0} drains)",
            finished && received == 4 * perProducer && inOrder);
        Check($"and wakes it at most once per drain ({live.Wakes:N0} wakes)", live.Wakes <= cycles + 1);
    }

    private static void FrameBudgetChecks()
    {
        // A budget spun on until it is spent stops just after its deadline.
        var overshoots = new List<double>();
        var early = false;
        for (var run = 0; run < 21; run++)
        {
            var budget = FrameBudget.Start(1.0);
            while (!budget.Spent)
            {
            }

            var now = Stopwatch.GetTimestamp();
            early |= now < budget.Deadline;
            overshoots.Add((now - budget.Deadline) * 1000.0 / Stopwatch.Frequency);
        }

        overshoots.Sort();
        var median = overshoots[overshoots.Count / 2];
        Check($"a budget stops within 0.1 ms of its deadline (median {median:0.000} ms past it)", !early && median <= 0.1);

        var items = FrameBudget.Start(1_000, items: 5);
        var counted = 0;
        while (!items.Spent)
        {
            items.Take();
            counted++;
        }

        Check("a budget of five items is spent after five", counted == 5);
        Check("an unlimited budget is never spent", !FrameBudget.Unlimited.Spent);
    }

    private static void FrameClockChecks()
    {
        var clock = new FrameClock();
        var start = TimeSpan.FromSeconds(10);
        var fresh = clock.Advance(start, wasActive: false);
        Check("the clock's first frame is new, and steps one nominal frame",
            fresh && Math.Abs(clock.DeltaMilliseconds - FrameClock.InitialNominalMilliseconds) < 1e-9);
        Check("inside a frame, now is the frame's own time", clock.Now == start);

        var again = clock.Advance(start, wasActive: true);
        Check("Rendering raised again within the frame is not a new frame, and steps nothing", !again && clock.DeltaMilliseconds == 0);
        clock.EndFrame();
        Check("between frames, now runs on from the last frame", clock.Now >= start);

        // A steady 239 Hz: the nominal frame follows it, and each step is the gap.
        var time = start;
        var gap = TimeSpan.FromTicks(41_841);
        for (var frame = 0; frame < 120; frame++)
        {
            time += gap;
            clock.Advance(time, wasActive: true);
            clock.EndFrame();
        }

        Check($"at 239 Hz the nominal frame settles on the display's ({clock.NominalMilliseconds:0.000} ms)",
            Math.Abs(clock.NominalMilliseconds - gap.TotalMilliseconds) < 0.01);
        Check("and a moving frame steps its gap", Math.Abs(clock.DeltaMilliseconds - gap.TotalMilliseconds) < 1e-9);

        time += TimeSpan.FromMilliseconds(60);
        clock.Advance(time, wasActive: true);
        Check($"a long frame steps at most {FrameClock.DtMaxMilliseconds} ms", clock.DeltaMilliseconds == FrameClock.DtMaxMilliseconds);
        var nominal = clock.NominalMilliseconds;
        Check("and does not move the nominal frame", Math.Abs(nominal - gap.TotalMilliseconds) < 0.01);

        time += TimeSpan.FromMilliseconds(250);
        clock.Advance(time, wasActive: true);
        Check("after a pause, a step is one nominal frame", clock.DeltaMilliseconds == nominal);

        time += gap;
        clock.Advance(time, wasActive: false);
        Check("and so is the first step after a rest", clock.DeltaMilliseconds == clock.NominalMilliseconds);
    }

    private static async Task FrameWorkDispatcherChecks()
    {
        await HiddenCanvasFallbackChecks();
        await FrameLoopUnhookChecks();
        await TreeBatchChecks();
    }

    /// <summary>
    /// A canvas in no window cannot run frames: what its inboxes are sent is
    /// taken in by their fallback driver, a dispatcher operation at Input
    /// priority - ahead of the Background work a frame loop would starve.
    /// </summary>
    private static async Task HiddenCanvasFallbackChecks()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        FrameInbox<int>? inbox = null;
        var taken = 0;
        var takenBeforeBackground = -1;
        var fallback = new DispatcherFrameDriver(
            dispatcher,
            (ref FrameBudget budget) =>
            {
                inbox!.Rearm();
                while (!budget.Spent && inbox.TryTake(out _))
                {
                    taken++;
                }
            },
            () => !inbox!.IsEmpty);
        var slot = new FrameDriverSlot(fallback);
        inbox = new FrameInbox<int>(slot);
        var canvas = new NestedCanvas();
        canvas.Drive(slot);
        Check("a canvas that drives a slot is its active driver", ReferenceEquals(slot.Active, canvas));

        // Posted before the dispatcher gets a look in, behind a Background
        // operation already waiting: the fallback's pass must still come first.
        _ = dispatcher.BeginInvoke(DispatcherPriority.Background, () => takenBeforeBackground = taken);
        for (var index = 0; index < 1_000; index++)
        {
            inbox.Post(index);
        }

        var clock = Stopwatch.StartNew();
        while ((taken < 1_000 || takenBeforeBackground < 0) && clock.ElapsedMilliseconds < 5_000)
        {
            await Task.Delay(10);
        }

        Check($"a hidden canvas hands its inboxes to the fallback, which takes everything in ({taken:N0} of 1,000, {fallback.Posts} operations)",
            taken == 1_000 && fallback.Runs >= 1 && fallback.Posts <= 3);
        Check("at Input priority: ahead of Background work queued before it", takenBeforeBackground == 1_000);
        Check("and the canvas never hooked its frame loop for it", !canvas.IsFrameHooked);

        canvas.StopDriving(slot);
        Check("given back, the slot wakes its fallback again", slot.Active is null);
    }

    /// <summary>
    /// The frame loop lets go of WPF's frames on the very frame it has nothing
    /// left to do, and a request for a frame hooks it again.  The frames are
    /// run by hand, at times of the test's choosing, as WPF's Rendering would.
    /// </summary>
    private static async Task FrameLoopUnhookChecks()
    {
        var disk = new FakeDisk();
        disk.Folder(@"Q:\one");
        disk.Folder(@"Q:\two");
        disk.AddFiles(@"Q:\one", 5, "file");
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await tree.LoadAsync(tree.Find(@"Q:\")!);

        var canvas = new NestedCanvas { Tree = tree };
        canvas.Measure(new Size(800, 500));
        canvas.Arrange(new Rect(0, 0, 800, 500));
        canvas.UpdateLayout();
        Check("the canvas drives its tree's finished reads, on its own clock",
            ReferenceEquals(tree.Driver.Active, canvas) && !ReferenceEquals(tree.Clock, NestedTree.StopwatchClock));

        var time = TimeSpan.FromSeconds(100);
        canvas.Redraw();
        Check("asking for a frame hooks the loop", canvas.IsFrameHooked);
        canvas.RunFrameForTests(time);
        Check("a frame that draws everything, with the camera still, unhooks the loop at its end",
            !canvas.IsFrameHooked && canvas.IsIdle && canvas.LastFrameStats is { Fresh: true, Skipped: false });

        canvas.Redraw();
        canvas.RunFrameForTests(time);
        Check("Rendering raised again within a frame draws but is not a new frame",
            !canvas.IsFrameHooked && canvas.LastFrameStats is { Fresh: false, Skipped: false });

        time += TimeSpan.FromMilliseconds(8.33);
        canvas.RunFrameForTests(time);
        Check("a frame with nothing out of date draws nothing", canvas.LastFrameStats is { Fresh: true, Skipped: true } && !canvas.IsFrameHooked);

        canvas.Tree = null;
        Check("let go of, the tree's reads go back to their fallback, and its clock to the stopwatch",
            tree.Driver.Active is null && ReferenceEquals(tree.Clock, NestedTree.StopwatchClock));
    }

    /// <summary>
    /// The tree says which folders a batch of applied reads changed, just
    /// before the Changed the same reads raise, and when its roots were replaced.
    /// </summary>
    private static async Task TreeBatchChecks()
    {
        var disk = new FakeDisk();
        disk.Folder(@"Q:\one");
        disk.AddFiles(@"Q:\", 3, "file");
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        var events = new List<string>();
        tree.BatchApplied += batch => events.Add($"batch {batch.RootsChanged} {string.Join(",", batch.Applied.Select(folder => folder.Name))}");
        tree.Changed += (_, _) => events.Add("changed");

        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        Check($"replacing the roots is a batch that says so, before Changed ({string.Join("; ", events)})",
            events is ["batch True ", "changed"]);

        events.Clear();
        await tree.LoadAsync(tree.Find(@"Q:\")!);
        Check($"a read applied is a batch of that folder, before Changed ({string.Join("; ", events)})",
            events is ["batch False Q:", "changed"]);
    }
}
