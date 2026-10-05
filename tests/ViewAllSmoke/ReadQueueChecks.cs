using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;

namespace ViewAllSmoke;

/// <summary>
/// The nested tree's read queue: which folder a freed slot reads next, what
/// is dropped when it leaves the screen and what stays wanted, a change that
/// arrives while a read is under way, two thousand reads through eight slots
/// with the results taken in a frame at a time and nothing posted to the
/// dispatcher per read, a share's one lane - a mapped drive's included - and
/// reads asked for while no canvas draws the tree.  On a thread with a
/// dispatcher, as in the app; the disks are in memory, gated where a test
/// needs reads held in flight.
/// </summary>
internal static partial class Program
{
    private static Task ReadQueueChecks()
    {
        RunOnSta("read queue", async () =>
        {
            Section("read queue");
            await ReadOrderChecks();
            await ReadExpiryChecks();
            await ReadChangeDuringReadChecks();
            await ReadCancelChecks();
            await ReadBatchChecks();
            await ReadRefreshCostChecks();
            ReadThroughputChecks();
            await ReadNetworkLaneChecks();
            await ReadHiddenCanvasChecks();
            await ReadPathChecks();
            await ReadDirectoryTimeChecks();
            await ReadDisposeChecks();
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// A disk whose reads can be held in flight: while <see cref="Gating"/> is
    /// on, every read but the drive's own waits for a release of the gate.
    /// Records the order folders were read in and how many reads of one share
    /// ran at once.
    /// </summary>
    private sealed class GatedDisk : IDisposable
    {
        private readonly List<string> _order = [];
        private int _gating;
        private int _shareRunning;
        private int _shareMost;

        public GatedDisk()
        {
            Disk.Hook = (path, token) =>
            {
                lock (_order)
                {
                    _order.Add(path);
                }

                var onShare = path.StartsWith(@"N:\", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(@"\\server\share", StringComparison.OrdinalIgnoreCase);
                if (onShare)
                {
                    var now = Interlocked.Increment(ref _shareRunning);
                    InterlockedMax(ref _shareMost, now);
                }

                try
                {
                    if (Volatile.Read(ref _gating) == 1 && path.Length > 3)
                    {
                        Gate.Wait(token);
                    }
                }
                finally
                {
                    if (onShare)
                    {
                        Interlocked.Decrement(ref _shareRunning);
                    }
                }

                return null;
            };
        }

        public FakeDisk Disk { get; } = new();

        public SemaphoreSlim Gate { get; } = new(0);

        public bool Gating
        {
            get => Volatile.Read(ref _gating) == 1;
            set => Volatile.Write(ref _gating, value ? 1 : 0);
        }

        /// <summary>The most reads of the share (N: and \\server\share) that were ever running at once.</summary>
        public int ShareMost => Volatile.Read(ref _shareMost);

        public string[] Order
        {
            get
            {
                lock (_order)
                {
                    return [.. _order];
                }
            }
        }

        public int ReadsOf(string path) => Order.Count(read => string.Equals(read, path, StringComparison.OrdinalIgnoreCase));

        public void ClearOrder()
        {
            lock (_order)
            {
                _order.Clear();
            }
        }

        /// <summary>Lets every held read and every later one through.</summary>
        public void Open()
        {
            Gating = false;
            Gate.Release(10_000);
        }

        public void Dispose()
        {
            Open();
            Gate.Dispose();
        }

        private static void InterlockedMax(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (value > current)
            {
                var seen = Interlocked.CompareExchange(ref target, value, current);
                if (seen == current)
                {
                    return;
                }

                current = seen;
            }
        }
    }

    /// <summary>A tree over <paramref name="disk"/> with drive Q: read, and the folders named, in name order.</summary>
    private static async Task<(NestedTree Tree, NestedFolder[] Folders)> QueueTreeAsync(GatedDisk disk, params string[] names)
    {
        foreach (var name in names)
        {
            disk.Disk.Folder($@"Q:\{name}");
        }

        var tree = new NestedTree(disk.Disk.Read);
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await tree.LoadAsync(tree.Find(@"Q:\")!);
        return (tree, [.. names.Select(name => tree.Find($@"Q:\{name}")!)]);
    }

    private static string[] Numbered(string stem, int count) =>
        [.. Enumerable.Range(0, count).Select(index => $"{stem}{index:D2}")];

    // ---- order -----------------------------------------------------------------

    /// <summary>A freed slot takes a folder something is waiting for first, then the largest on screen.</summary>
    private static async Task ReadOrderChecks()
    {
        using var disk = new GatedDisk();
        var busy = Numbered("busy", NestedTree.LocalReadSlots);
        var (tree, folders) = await QueueTreeAsync(disk, [.. busy, "a", "b", "c", "d"]);
        using var _ = tree;
        var slots = folders[..busy.Length];
        var (a, b, c, d) = (folders[^4], folders[^3], folders[^2], folders[^1]);

        disk.Gating = true;
        foreach (var folder in slots)
        {
            tree.Request(folder, 1);
        }

        tree.Request(a, 5);
        tree.Request(b, 10);
        tree.Request(d, 20);
        var cLoaded = tree.LoadAsync(c);
        Check($"with every one of the {NestedTree.LocalReadSlots} local slots reading, the rest wait",
            slots.All(folder => folder.LoadState == NestedLoadState.Loading)
            && new[] { a, b, c, d }.All(folder => folder.LoadState == NestedLoadState.Queued));

        // Loading reserves a slot before its pool thread has entered the
        // reader. Wait for those readers before resetting their log, or a
        // delayed busy read is mistaken for the next queue choice.
        await WaitUntil(() => slots.All(folder => disk.ReadsOf(folder.FullPath) > 0), 3_000);
        Check("the order fixture has every reserved worker waiting in the reader",
            slots.All(folder => disk.ReadsOf(folder.FullPath) > 0));
        disk.ClearOrder();
        var picked = new List<string>();
        for (var step = 1; step <= 4; step++)
        {
            disk.Gate.Release(1);
            await WaitUntil(() => disk.Order.Length >= step, 3_000);
            picked.Add(Path.GetFileName(disk.Order.ElementAtOrDefault(step - 1) ?? "?"));
        }

        Check($"a freed slot reads a folder something waits for first, then the largest on screen ({string.Join(", ", picked)})",
            picked.SequenceEqual(["c", "d", "b", "a"]));

        disk.Open();
        await cLoaded;
        await WaitUntil(() => tree.PendingCount == 0, 3_000);
        Check("and everything asked for is read", folders.All(folder => folder.IsLoaded) && tree.PendingCount == 0);

        var cost = tree.PickCost;
        Console.WriteLine($"        {cost.Picks} picks, worst {cost.WorstMilliseconds * 1000:0.0} us");
    }

    // ---- expiry ----------------------------------------------------------------

    /// <summary>
    /// A read not asked for again for two pictures is dropped: a first read
    /// goes back to unread, a refresh stays stale; a read someone waits for stays.
    /// </summary>
    private static async Task ReadExpiryChecks()
    {
        using var disk = new GatedDisk();
        var busy = Numbered("busy", NestedTree.LocalReadSlots);
        var (tree, folders) = await QueueTreeAsync(disk, [.. busy, "x", "y", "z"]);
        using var _ = tree;
        var (x, y, z) = (folders[^3], folders[^2], folders[^1]);
        await tree.LoadAsync(y);
        await tree.LoadAsync(z);
        Check("a folder read and unchanged is not queued when drawn",
            RequestQueues(tree, y) is false && y.QueuedRead == ReadKind.None);

        disk.Gating = true;
        foreach (var folder in folders[..busy.Length])
        {
            tree.Request(folder, 1);
        }

        tree.Request(x, 1);
        y.IsStale = true;
        tree.Request(y, 1);
        Check("a stale folder that is drawn is queued for a refresh, and still drawn as loaded",
            y.QueuedRead == ReadKind.Refresh && y.LoadState == NestedLoadState.Loaded);
        tree.Refresh(z);
        Check("a refresh asked for by name is queued ahead of the canvas's", z.QueuedRead == ReadKind.Refresh && z.IsStale);

        tree.BeginFrame();
        tree.BeginFrame();
        tree.BeginFrame();
        await WaitUntil(() => folders[..busy.Length].All(folder => disk.ReadsOf(folder.FullPath) > 0), 3_000);
        Check("the expiry fixture has every reserved worker waiting in the reader",
            folders[..busy.Length].All(folder => disk.ReadsOf(folder.FullPath) > 0));
        disk.ClearOrder();
        disk.Gate.Release(1);
        await WaitUntil(() => disk.Order.Length >= 1, 3_000);
        Check("a first read not asked for in two pictures goes back to unread, off the queue",
            x.LoadState == NestedLoadState.NotLoaded && x.QueuedRead == ReadKind.None && x.QueueIndex == -1);
        Check("a refresh not asked for in two pictures is dropped but stays stale",
            y.QueuedRead == ReadKind.None && y.IsStale && y.NeedsRefresh && y.LoadState == NestedLoadState.Loaded);
        Check("a refresh asked for by name is never dropped: it is read next",
            disk.Order.FirstOrDefault() == @"Q:\z");

        tree.Request(y, 5);
        Check("drawn again, the stale folder is queued again", y.QueuedRead == ReadKind.Refresh);

        disk.Open();
        await WaitUntil(() => tree.PendingCount == 0, 3_000);
        Check("and once its refresh is applied it is up to date",
            !y.NeedsRefresh && y.QueuedRead == ReadKind.None && !z.IsStale && x.LoadState == NestedLoadState.NotLoaded);
    }

    private static bool RequestQueues(NestedTree tree, NestedFolder folder)
    {
        tree.Request(folder, 50);
        return folder.QueuedRead != ReadKind.None;
    }

    // ---- a change during a read --------------------------------------------------------

    /// <summary>
    /// The read stamps are taken when a read begins, so a change that arrives
    /// while it runs - a folder marked stale, an epoch moved on - leaves the
    /// folder to be read again; a refresh asked for meanwhile gets a read of its own.
    /// </summary>
    private static async Task ReadChangeDuringReadChecks()
    {
        using var disk = new GatedDisk();
        disk.Disk.Folder(@"Q:\f\inner");

        // Armed, as a local disk's watch is: one that is down has every read
        // take the directory's own time, from the real disk.
        var watch = new WatchRoot(@"Q:\", WatchKind.Local, isNetwork: false) { State = WatchState.Armed };
        using var tree = new NestedTree(disk.Disk.Read) { WatchRootFor = path => path == @"Q:\" ? watch : null };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        var q = tree.Find(@"Q:\")!;
        await tree.LoadAsync(q);
        var f = tree.Find(@"Q:\f")!;
        await tree.LoadAsync(f);
        Check("a drive gets its watch from the tree, and every folder below takes it from its parent",
            ReferenceEquals(q.Watch, watch) && ReferenceEquals(f.Watch, watch) && ReferenceEquals(tree.Find(@"Q:\f\inner")!.Watch, watch)
            && tree.Root.Watch is null);
        Check("a read stamps the watch's epoch, and a folder just read needs nothing", f.ReadEpoch == 0 && !f.NeedsRefresh);

        // Marked stale while its refresh is being read.
        disk.Gating = true;
        disk.ClearOrder();
        var refresh = tree.RefreshAsync(f);
        await WaitUntil(() => disk.ReadsOf(@"Q:\f") == 1, 3_000);
        Check("a folder being refreshed is still drawn as loaded", f.LoadState == NestedLoadState.Loaded && f.QueuedRead == ReadKind.Refresh);
        f.IsStale = true;
        disk.Gate.Release(1);
        await refresh;
        Check("a change that arrives while the read runs leaves the folder stale", f.IsStale && f.NeedsRefresh);

        // The epoch moves on while its refresh is being read: an overflow.
        disk.ClearOrder();
        refresh = tree.RefreshAsync(f);
        await WaitUntil(() => disk.ReadsOf(@"Q:\f") == 1, 3_000);
        Interlocked.Increment(ref watch.Epoch);
        disk.Gate.Release(1);
        await refresh;
        Check("an overflow while the read runs leaves it to be read again",
            !f.IsStale && f.ReadEpoch == 0 && f.NeedsRefresh);
        disk.Gating = false;
        disk.ClearOrder();
        tree.Request(f, 100);
        await WaitUntil(() => disk.ReadsOf(@"Q:\f") == 1 && f.QueuedRead == ReadKind.None, 3_000);
        Check("drawn, it is refreshed, and then up to date", f.ReadEpoch == 1 && !f.NeedsRefresh);

        // A refresh awaited while one is already under way.
        disk.Gating = true;
        disk.ClearOrder();
        var first = tree.RefreshAsync(f);
        await WaitUntil(() => disk.ReadsOf(@"Q:\f") == 1, 3_000);
        var second = tree.RefreshAsync(f);
        disk.Gate.Release(1);
        await first;
        await WaitUntil(() => disk.ReadsOf(@"Q:\f") == 2, 3_000);
        Check("a refresh asked for during a read is not answered by it: the folder is read again after it",
            first.IsCompletedSuccessfully && !second.IsCompleted && disk.ReadsOf(@"Q:\f") == 2);
        disk.Gate.Release(1);
        await second;
        Check("and answered by that read", second.IsCompletedSuccessfully && f.QueuedRead == ReadKind.None);

        // The same for one nobody awaits: F5 while a read runs.
        disk.ClearOrder();
        var third = tree.RefreshAsync(f);
        await WaitUntil(() => disk.ReadsOf(@"Q:\f") == 1, 3_000);
        tree.Refresh(f);
        disk.Gating = false;
        disk.Gate.Release(1);
        await third;
        await WaitUntil(() => disk.ReadsOf(@"Q:\f") == 2 && tree.PendingCount == 0, 3_000);
        Check("a refresh nobody waits for, asked for during a read, is read after it", disk.ReadsOf(@"Q:\f") == 2 && !f.NeedsRefresh);
    }

    // ---- cancelling --------------------------------------------------------------------

    /// <summary>A load given up on takes its read off the queue, or leaves the one under way to finish unapplied.</summary>
    private static async Task ReadCancelChecks()
    {
        using var disk = new GatedDisk();
        var busy = Numbered("busy", NestedTree.LocalReadSlots);
        var (tree, folders) = await QueueTreeAsync(disk, [.. busy, "x", "y"]);
        using var _ = tree;
        var (x, y) = (folders[^2], folders[^1]);
        var loaded = new List<NestedFolder>();
        tree.FolderLoaded += loaded.Add;

        disk.Gating = true;
        foreach (var folder in folders[..busy.Length])
        {
            tree.Request(folder, 1);
        }

        using (var cancel = new CancellationTokenSource())
        {
            var load = tree.LoadAsync(x, cancel.Token);
            var queued = x.LoadState == NestedLoadState.Queued;
            cancel.Cancel();
            var cancelled = await Cancelled(load);
            Check("a load cancelled while it waits ends in a cancellation", queued && cancelled);
            Check("and takes its read off the queue: the folder is unread",
                x.LoadState == NestedLoadState.NotLoaded && x.QueuedRead == ReadKind.None && x.QueueIndex == -1
                && tree.PendingCount == NestedTree.LocalReadSlots);
        }

        disk.Gate.Release(NestedTree.LocalReadSlots);
        await WaitUntil(() => tree.PendingCount == 0, 3_000);
        disk.ClearOrder();
        using (var cancel = new CancellationTokenSource())
        {
            var load = tree.LoadAsync(y, cancel.Token);
            await WaitUntil(() => disk.ReadsOf(@"Q:\y") == 1, 3_000);
            cancel.Cancel();
            var cancelled = await Cancelled(load);
            Check("a load cancelled while its read runs ends in a cancellation, with the folder unread at once",
                cancelled && y.LoadState == NestedLoadState.NotLoaded && y.QueuedRead == ReadKind.None);
            disk.Gate.Release(1);
            await WaitUntil(() => tree.PendingCount == 0, 3_000);
            await Task.Delay(30);
            Check("and what that read brings back is dropped, not applied",
                y.LoadState == NestedLoadState.NotLoaded && !loaded.Contains(y));
        }

        disk.Gating = false;
        await tree.LoadAsync(y);
        Check("a later load of it reads it", y.IsLoaded && loaded.Contains(y));

        // A listener that throws while a read is applied: the folder is marked
        // failed rather than left reading, and whoever waits hears why.
        void Throw(NestedFolder folder)
        {
            if (ReferenceEquals(folder, x))
            {
                throw new InvalidOperationException("listener failed");
            }
        }

        tree.FolderLoaded += Throw;
        Exception? heard = null;
        try
        {
            await tree.LoadAsync(x);
        }
        catch (InvalidOperationException ex)
        {
            heard = ex;
        }

        tree.FolderLoaded -= Throw;
        Check("a read whose listener throws leaves the folder failed, not reading, and the load hears why",
            heard is not null && x.LoadState == NestedLoadState.Failed && x.QueuedRead == ReadKind.None);
        await tree.RefreshAsync(x);
        Check("and a refresh reads it again", x.IsLoaded);
    }

    private static async Task<bool> Cancelled(Task task)
    {
        try
        {
            await task;
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    // ---- batches -----------------------------------------------------------------------

    /// <summary>
    /// What one drain applies is one batch and one Changed; a budget spent
    /// leaves the rest for the next frame, and whoever awaited a read hears of
    /// it only after the drain's events.
    /// </summary>
    private static async Task ReadBatchChecks()
    {
        using var disk = new GatedDisk();
        var names = Numbered("f", 50);
        var (tree, folders) = await QueueTreeAsync(disk, names);
        using var _ = tree;
        var driver = new CountingDriver();
        tree.Driver.Active = driver;
        var batches = new List<int>();
        var changes = 0;
        tree.BatchApplied += batch => batches.Add(batch.Applied.Count);
        tree.Changed += (_, _) => changes++;

        disk.ClearOrder();
        foreach (var folder in folders[..40])
        {
            tree.Request(folder, 1);
        }

        var awaited = tree.LoadAsync(folders[45]);
        var heardBeforeEvents = false;
        tree.BatchApplied += batch => heardBeforeEvents |= batch.Applied.Contains(folders[45]) && awaited.IsCompleted;
        await WaitUntil(() => disk.Order.Length == 41 && tree.PendingCount == 41 && tree.HasWork, 3_000);
        await Task.Delay(20);
        Check($"finished reads wait for the frame, waking its driver once ({driver.Wakes} wakes for 41 reads)",
            folders.All(folder => folder.LoadState != NestedLoadState.Loaded) && driver.Wakes == 1);

        var budget = FrameBudget.Start(1_000, items: 10);
        var applied = tree.DrainResults(ref budget);
        Check($"a drain applies what its budget allows, as one batch and one Changed ({applied} applied, batches {string.Join(",", batches)}, {changes} changed)",
            applied == 10 && batches.SequenceEqual([10]) && changes == 1 && tree.HasWork);

        budget = FrameBudget.Unlimited;
        applied = tree.DrainResults(ref budget);
        Check($"the next drain takes the rest, again as one batch ({applied} applied)",
            applied == 31 && batches.SequenceEqual([10, 31]) && changes == 2 && !tree.HasWork && tree.PendingCount == 0);
        await Task.Yield();
        Check("whoever awaited a load hears of it after the batch's events, not inside them",
            awaited.IsCompletedSuccessfully && !heardBeforeEvents);

        tree.Driver.Active = null;
    }

    // ---- the cost of a refresh -----------------------------------------------------------

    /// <summary>
    /// A refresh of a folder of two thousand sub-folders and two thousand
    /// files, one sub-folder new: what applying it costs the UI thread, and
    /// what the whole refresh allocates, under names from A and under newest
    /// first.  This is what decides whether refreshes need ordering off the
    /// UI thread, which pays only once an apply passes half a millisecond.
    /// </summary>
    private static async Task ReadRefreshCostChecks()
    {
        const int Entries = 2_000;
        var folders = Enumerable.Range(0, Entries).Select(index => new NestedEntry($"d{index:D4}", false, false, 638_000_000_000_000_000 + index)).ToList();
        var files = Enumerable.Range(0, Entries).Select(index => new NestedFile($"f{index:D4}.txt", false, index, 638_000_000_000_000_000 + index)).ToArray();
        var before = new NestedListing(folders, Entries, 0, false) { Files = files };
        var after = new NestedListing([.. folders, new NestedEntry("e-new", false, false)], Entries, 0, false) { Files = files };
        var current = before;
        var root = new NestedListing([new NestedEntry("w", false, false)], 0, 0, false);
        using var tree = new NestedTree((path, _) => path.Length <= 3 ? root : Volatile.Read(ref current));
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await tree.LoadAsync(tree.Find(@"Q:\")!);
        var w = tree.Find(@"Q:\w")!;
        await tree.LoadAsync(w);

        var driver = new CountingDriver();
        tree.Driver.Active = driver;
        foreach (var sort in new[] { ItemSort.Default, new ItemSort(SortColumn.Modified, Descending: true) })
        {
            tree.SetSort(sort);
            await tree.WhenSortIdleAsync();
            var applyMilliseconds = new List<double>();
            var allocated = new List<long>();
            for (var run = 0; run < 6; run++)
            {
                Volatile.Write(ref current, run % 2 == 0 ? after : before);
                var bytes = GC.GetTotalAllocatedBytes(precise: true);
                tree.Refresh(w);
                await WaitUntil(() => tree.HasWork, 3_000);
                await Task.Delay(5);
                var started = Stopwatch.GetTimestamp();
                var budget = FrameBudget.Unlimited;
                tree.DrainResults(ref budget);
                applyMilliseconds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                allocated.Add(GC.GetTotalAllocatedBytes(precise: true) - bytes);
            }

            applyMilliseconds.Sort();
            allocated.Sort();
            var median = applyMilliseconds[applyMilliseconds.Count / 2];
            var order = sort.IsDefault ? "names from A" : "newest first";
            Console.WriteLine($"        {order}: apply {string.Join(" ", applyMilliseconds.Select(ms => ms.ToString("0.000")))} ms, allocated {string.Join(" ", allocated.Select(value => $"{value / 1024} KB"))}");
            // Names from A places a refresh in the listing's own order, which
            // must stay far under the half millisecond past which ordering it
            // off the UI thread would pay; another order sorts both lists on
            // the UI thread, and is only held to twice the frame's slice.
            var limit = sort.IsDefault ? 0.5 : 2 * FrameBudgets.ApplyMs;
            Check($"a refresh of a folder of {Entries:N0} sub-folders and {Entries:N0} files, {order}, applies in under {limit} ms ({median:0.000} ms median)",
                median < limit && w.Children.Count is Entries or Entries + 1);
            Check($"and the whole refresh allocates at most 200 KB ({allocated[allocated.Count / 2] / 1024} KB median)",
                allocated[allocated.Count / 2] <= 200 * 1024);
        }

        tree.Driver.Active = null;
    }

    // ---- throughput ----------------------------------------------------------------------

    /// <summary>
    /// Two thousand reads of a millisecond each through eight slots, taken in
    /// every 8.33 ms within a 2 ms slice as the canvas's frames would: done in
    /// a third of a second, with no dispatcher operation posted for any of it.
    /// </summary>
    private static void ReadThroughputChecks()
    {
        const int Count = 2_000;
        const double FrameMilliseconds = 8.33;
        var names = Enumerable.Range(0, Count).Select(index => $"f{index:D4}").ToArray();
        var rootListing = new NestedListing([.. names.Select(name => new NestedEntry(name, false, false))], 0, 0, false);
        var empty = new NestedListing([], 0, 0, false);
        NestedListing Read(string path, CancellationToken token)
        {
            if (path.Length <= 3)
            {
                return rootListing;
            }

            var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 1_000;
            while (Stopwatch.GetTimestamp() < until)
            {
                Thread.SpinWait(20);
            }

            return empty;
        }

        var dispatcher = Dispatcher.CurrentDispatcher;
        var attempts = new List<(double Seconds, int Posted, int Drains, int Wakes, double WorstDrain, bool Complete, double MeanPick, double WorstPick)>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var tree = new NestedTree(Read);
            tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
            var q = tree.Find(@"Q:\")!;
            var driver = new CountingDriver();
            tree.Driver.Active = driver;
            tree.Request(q, 1_000);
            while (!q.IsLoaded)
            {
                Thread.Sleep(1);
                var all = FrameBudget.Unlimited;
                tree.DrainResults(ref all);
            }

            var folders = q.Children.ToArray();
            var posted = 0;
            void CountPost(object? sender, DispatcherHookEventArgs e) => posted++;
            dispatcher.Hooks.OperationPosted += CountPost;
            var drains = 0;
            var worstDrain = 0.0;
            var clock = Stopwatch.StartNew();
            foreach (var folder in folders)
            {
                tree.Request(folder, 100);
            }

            var loaded = 0;
            var nextFrame = 0.0;
            while (loaded < Count && clock.Elapsed.TotalSeconds < 3)
            {
                nextFrame += FrameMilliseconds;
                while (clock.Elapsed.TotalMilliseconds < nextFrame)
                {
                    Thread.SpinWait(50);
                }

                var started = Stopwatch.GetTimestamp();
                var budget = FrameBudget.Start(FrameBudgets.ApplyMs);
                loaded += tree.DrainResults(ref budget);
                worstDrain = Math.Max(worstDrain, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                drains++;
            }

            var seconds = clock.Elapsed.TotalSeconds;
            dispatcher.Hooks.OperationPosted -= CountPost;
            var picks = tree.PickCost;
            attempts.Add((seconds, posted, drains, driver.Wakes, worstDrain, folders.All(folder => folder.IsLoaded),
                picks.TotalMilliseconds / Math.Max(1, picks.Picks), picks.WorstMilliseconds));
            tree.Driver.Active = null;
            if (seconds <= 0.35)
            {
                break;
            }
        }

        var best = attempts.MinBy(run => run.Seconds);
        Console.WriteLine($"        runs: {string.Join("; ", attempts.Select(run => $"{run.Seconds * 1000:0} ms, {run.Drains} drains, {run.Wakes} wakes, worst drain {run.WorstDrain:0.00} ms, pick mean {run.MeanPick * 1000:0.0} us worst {run.WorstPick * 1000:0.0} us"))}");
        Check($"{Count:N0} reads of 1 ms through {NestedTree.LocalReadSlots} slots, drained every {FrameMilliseconds} ms, are all applied within 0.35 s ({best.Seconds * 1000:0} ms)",
            best.Complete && best.Seconds <= 0.35);
        Check($"with no dispatcher operation posted for any read ({best.Posted} posted)", attempts.All(run => run.Posted == 0));
        Check($"waking the driver at most once per drain ({best.Wakes} wakes, {best.Drains} drains)", attempts.All(run => run.Wakes <= run.Drains + 1));
        Check($"and each drain within its slice, give or take one apply ({best.WorstDrain:0.00} ms)", best.WorstDrain <= FrameBudgets.ApplyMs + 1);
        Check($"a freed slot finds its next folder among up to {Count:N0} waiting in well under 0.2 ms ({best.MeanPick * 1000:0.0} us on average)",
            best.MeanPick <= 0.2);
    }

    // ---- the network lane --------------------------------------------------------------

    /// <summary>
    /// A share is read one folder at a time whatever it is called - a mapped
    /// drive letter or its UNC name - while the local disks go on eight at a time.
    /// </summary>
    private static async Task ReadNetworkLaneChecks()
    {
        VolumeKinds.ResolveLetter = letter => letter == 'N' ? @"\\server\share\" : null;
        try
        {
            Check("a mapped drive is on its share, and its letter and UNC spellings are one share",
                VolumeKinds.IsNetwork(@"N:\x")
                && ReferenceEquals(VolumeKinds.ShareKey(@"N:\x"), VolumeKinds.ShareKey(@"\\SERVER\share\y\z"))
                && VolumeKinds.ShareKey(@"\\?\UNC\server\share\w") == @"\\server\share"
                && !VolumeKinds.IsNetwork(@"L:\x") && !VolumeKinds.IsNetwork(@"\\?\L:\x") && !VolumeKinds.IsNetwork("relative"));

            using var disk = new GatedDisk();
            foreach (var index in Enumerable.Range(0, 4))
            {
                disk.Disk.Folder($@"N:\n{index}");
            }

            foreach (var index in Enumerable.Range(0, 10))
            {
                disk.Disk.Folder($@"L:\l{index}");
            }

            disk.Disk.Folder(@"\\server\share\u0");
            using var tree = new NestedTree(disk.Disk.Read);
            tree.SetRoots(
            [
                new NestedRoot(@"N:\", "N:", NestedFolderKind.Drive),
                new NestedRoot(@"L:\", "L:", NestedFolderKind.Drive),
                new NestedRoot(@"\\server\share", "share", NestedFolderKind.Drive)
            ]);
            foreach (var root in tree.Root.Children)
            {
                await tree.LoadAsync(root);
            }

            var n = tree.Find(@"N:\")!.Children.ToArray();
            var l = tree.Find(@"L:\")!.Children.ToArray();
            var u = tree.Find(@"\\server\share\u0")!;
            Check("a folder on a mapped drive says it is on a share, one on a local disk that it is not",
                n.All(folder => folder.IsNetwork) && u.IsNetwork && l.All(folder => !folder.IsNetwork));

            disk.Gating = true;
            foreach (var folder in n.Concat(l).Append(u))
            {
                tree.Request(folder, 10);
            }

            Check($"the mapped drive is read one folder at a time ({n.Count(folder => folder.LoadState == NestedLoadState.Loading)} reading)",
                n.Count(folder => folder.LoadState == NestedLoadState.Loading) == 1
                && n.Count(folder => folder.LoadState == NestedLoadState.Queued) == 3);
            Check("and the same share under its UNC name waits in the same lane", u.LoadState == NestedLoadState.Queued);
            Check("while the local disk reads in all its slots",
                l.Count(folder => folder.LoadState == NestedLoadState.Loading) == NestedTree.LocalReadSlots);

            disk.Open();
            await WaitUntil(() => tree.PendingCount == 0, 3_000);
            Check($"everything is read, and the share never had more than one read at a time ({disk.ShareMost})",
                n.All(folder => folder.IsLoaded) && l.All(folder => folder.IsLoaded) && u.IsLoaded && disk.ShareMost == 1);
        }
        finally
        {
            VolumeKinds.ResolveLetter = null;
        }

        // This machine's own drives, as Windows reports them.
        var drives = DriveInfo.GetDrives();
        var mapped = drives.Where(drive => drive.DriveType == DriveType.Network).Select(drive => drive.Name).ToArray();
        var local = drives.Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable).Select(drive => drive.Name).ToArray();
        Console.WriteLine($"        this machine: network drives [{string.Join(" ", mapped)}] -> {string.Join(" ", mapped.Select(name => VolumeKinds.ShareKey(name) ?? "-"))}");
        Check("every drive letter Windows calls a network drive is on the network lane, and no local disk is",
            mapped.All(VolumeKinds.IsNetwork) && !local.Any(VolumeKinds.IsNetwork));
    }

    // ---- no canvas drawing -------------------------------------------------------------

    /// <summary>
    /// A reveal while the canvas that drives the tree is not on screen - the
    /// graph is up, the window minimised: its reads are taken in by the
    /// fallback driver, and the canvas's frame loop is never hooked for them.
    /// </summary>
    private static async Task ReadHiddenCanvasChecks()
    {
        using var disk = new GatedDisk();
        const string Deep = @"Q:\one\two\three\four";
        disk.Disk.Folder(Deep);
        using var tree = new NestedTree(disk.Disk.Read);
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        var canvas = new NestedCanvas { Tree = tree };
        Check("a canvas given the tree drives its reads", ReferenceEquals(tree.Driver.Active, canvas));

        var fallback = tree.Driver.Fallback as DispatcherFrameDriver;
        var runsBefore = fallback?.Runs ?? 0;
        var reveal = tree.RevealAsync(Deep);
        var finished = await Task.WhenAny(reveal, Task.Delay(5_000));
        Check($"a reveal while that canvas is not on screen still completes, its reads taken in by the fallback driver ({(fallback?.Runs ?? 0) - runsBefore} passes)",
            ReferenceEquals(finished, reveal) && reveal.Result?.FullPath == Deep && fallback is not null && fallback.Runs > runsBefore);
        canvas.Tree = null;

        var load = tree.RefreshAsync(tree.Find(@"Q:\one")!);
        finished = await Task.WhenAny(load, Task.Delay(5_000));
        Check("and a refresh with no canvas at all completes the same way", ReferenceEquals(finished, load));
    }

    // ---- paths -------------------------------------------------------------------------

    /// <summary>
    /// A sub-folder's path is joined from its parent's only when asked for, and
    /// is the path it always was; its hue, made without the path, is the same
    /// number the path gives.
    /// </summary>
    private static async Task ReadPathChecks()
    {
        using var disk = new GatedDisk();
        string[] names = ["alpha", "Beta Space", "trailing.", "Ünïcödé", "日本語", "x", "UPPER"];
        string[] roots = [@"Q:\", @"\\server\share", @"R:\deep\root"];
        foreach (var root in roots)
        {
            foreach (var name in names)
            {
                disk.Disk.Folder(Path.Combine(root, name, "inner"));
            }
        }

        using var tree = new NestedTree(disk.Disk.Read);
        tree.SetRoots([.. roots.Select(root => new NestedRoot(root, root, NestedFolderKind.Drive))]);
        foreach (var root in tree.Root.Children)
        {
            await tree.LoadAsync(root);
            foreach (var child in root.Children)
            {
                await tree.LoadAsync(child);
            }
        }

        var all = Descendants(tree.Root, includeHidden: true).ToList();
        var wrong = all.FirstOrDefault(folder => folder.Parent is { IsComputer: false } parent
            && folder.FullPath != Path.Combine(parent.FullPath, folder.Name));
        Check($"every folder's path is its parent's joined with its name ({all.Count} folders)", wrong is null && all.Count > 40);
        var hue = all.FirstOrDefault(folder => folder.Hue != NestedFolder.HueFor(folder.FullPath));
        Check("and its hue is the one its whole path gives, colour for colour", hue is null);
    }

    // ---- the directory's own time ----------------------------------------------------------

    /// <summary>
    /// On a polled volume, a share, or a local disk whose watch is down, a
    /// read takes the directory's own time first, for polling to compare; on
    /// a watched local disk it takes nothing.
    /// </summary>
    private static async Task ReadDirectoryTimeChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerReadQueue", Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(Path.Combine(sub, "inner"));
        try
        {
            foreach (var (kind, isNetwork, state, taken, what) in new[]
                     {
                         (WatchKind.Polling, false, WatchState.Polling, true, "polling"),
                         (WatchKind.Network, true, WatchState.Off, true, "network"),
                         (WatchKind.Local, false, WatchState.Off, true, "local, its watch down,"),
                         (WatchKind.Local, false, WatchState.Armed, false, "local")
                     })
            {
                var watch = new WatchRoot(root, kind, isNetwork) { State = state };
                using var tree = new NestedTree { WatchRootFor = _ => watch };
                tree.SetRoots([new NestedRoot(root, "root", NestedFolderKind.Drive)]);
                var top = tree.Find(root)!;
                await tree.LoadAsync(top);
                var folder = tree.Find(sub)!;
                await tree.LoadAsync(folder);
                var expected = Directory.GetLastWriteTimeUtc(sub).Ticks;
                Check(taken
                        ? $"a read on a {what} root keeps the directory's own time"
                        : "a read on a watched local disk takes no time of its own",
                    taken ? folder.DirWriteTicks == expected && top.DirWriteTicks == Directory.GetLastWriteTimeUtc(root).Ticks : folder.DirWriteTicks == 0);
            }
        }
        finally
        {
            TryDelete(Path.GetDirectoryName(root)!);
        }
    }

    // ---- going away --------------------------------------------------------------------

    /// <summary>A tree disposed with loads waiting cancels them, and reads nothing more.</summary>
    private static async Task ReadDisposeChecks()
    {
        using var disk = new GatedDisk();
        var busy = Numbered("busy", NestedTree.LocalReadSlots);
        var (tree, folders) = await QueueTreeAsync(disk, [.. busy, "x"]);
        disk.Gating = true;
        disk.ClearOrder();
        foreach (var folder in folders[..busy.Length])
        {
            tree.Request(folder, 1);
        }

        await WaitUntil(() => disk.Order.Length == busy.Length, 3_000);
        var load = tree.LoadAsync(folders[^1]);
        tree.Dispose();
        var cancelled = await Cancelled(load);
        disk.Open();
        await Task.Delay(50);
        Check("disposing the tree cancels a load still waiting, and leaves its folder unread",
            cancelled && folders[^1].LoadState == NestedLoadState.NotLoaded);
        Check("and nothing that was waiting is read after it", disk.ReadsOf(@"Q:\x") == 0 && disk.Order.Length == busy.Length);
    }
}
