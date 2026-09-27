using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;

namespace ViewAllSmoke;

/// <summary>
/// The watchers on real folders: a change in a registered folder is heard
/// through a whole volume's watch and handed on, noise in folders nobody
/// registered costs a lookup and nothing else, renames pair, sizes and dates
/// come with the change where NTFS reports them, an overflow moves the epoch
/// on exactly once, a suspended watch lets its folder go, a failed one is
/// retried and polled meanwhile, a share is armed only while drawn, drive
/// letters resolve to one root per volume or share, and the registrations of
/// forgotten folders are swept.  Folders are made under the temp folder;
/// the clock is a fake one wherever timing is checked, so the checks wait
/// only for the disk.
/// </summary>
internal static partial class Program
{
    private static Task WatchChecks()
    {
        Section("watch");
        var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "UltraExplorerWatch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            WatchRealChecks(root);
            WatchNoiseChecks(root);
            WatchRenameChecks(root);
            WatchContentChecks(root);
            WatchOverflowChecks(root);
            WatchSuspendChecks(root);
            WatchErrorChecks(root);
            WatchNetworkChecks(root);
            WatchVolumeChecks(root);
            WatchShortNameChecks(root);
            WatchSweepChecks(root);
        }
        finally
        {
            TryDelete(root);
        }

        return Task.CompletedTask;
    }

    // ---- shared by the watch and hub checks -------------------------------------------

    /// <summary>
    /// A clock that moves only when told, firing each timer due on the way at
    /// its own due time, on the thread that moves it.  Timestamps are
    /// <see cref="TimeSpan"/> ticks and start at one second, so that no real
    /// time is ever zero.
    /// </summary>
    private sealed class ManualTime : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _now = TimeSpan.TicksPerSecond;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _now);

        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            timer.Change(dueTime, period);
            return timer;
        }

        /// <summary>Milliseconds on this clock since it started.</summary>
        public double Milliseconds => (GetTimestamp() - TimeSpan.TicksPerSecond) / (double)TimeSpan.TicksPerMillisecond;

        public void Advance(double milliseconds) => AdvanceTo(GetTimestamp() + (long)(milliseconds * TimeSpan.TicksPerMillisecond));

        public void AdvanceTo(long target)
        {
            while (true)
            {
                ManualTimer? next = null;
                lock (_gate)
                {
                    foreach (var timer in _timers)
                    {
                        if (timer.Due <= target && (next is null || timer.Due < next.Due))
                        {
                            next = timer;
                        }
                    }
                }

                if (next is null)
                {
                    break;
                }

                Interlocked.Exchange(ref _now, Math.Max(GetTimestamp(), next.Due));
                next.Fire();
            }

            Interlocked.Exchange(ref _now, Math.Max(GetTimestamp(), target));
        }

        private sealed class ManualTimer(ManualTime time, TimerCallback callback, object? state) : ITimer
        {
            private long _period;

            public long Due { get; private set; } = long.MaxValue;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (time._gate)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : time.GetTimestamp() + Math.Max(0, dueTime.Ticks);
                    _period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                }

                return true;
            }

            public void Fire()
            {
                lock (time._gate)
                {
                    Due = _period > 0 ? Due + _period : long.MaxValue;
                }

                callback(state);
            }

            public void Dispose()
            {
                lock (time._gate)
                {
                    time._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Records what a hub hands on.</summary>
    private sealed class WatchSink : IChangeSink
    {
        public List<(ChangeConsumer Consumer, object Target, FolderChange Change, double At)> Changes { get; } = [];
        public List<WatchRoot> Bumps { get; } = [];
        public List<WatchRoot> Polls { get; } = [];

        /// <summary>What <c>At</c> is stamped with: milliseconds on a test's clock.</summary>
        public Func<double>? Clock { get; set; }

        public void FolderChanged(ChangeConsumer consumer, object target, in FolderChange change) =>
            Changes.Add((consumer, target, change, Clock?.Invoke() ?? 0));

        public void EpochBumped(WatchRoot root) => Bumps.Add(root);

        public void PollDue(WatchRoot root) => Polls.Add(root);

        public void Clear()
        {
            Changes.Clear();
            Bumps.Clear();
            Polls.Clear();
        }

        public IEnumerable<FolderChange> For(object target) => Changes.Where(change => ReferenceEquals(change.Target, target)).Select(change => change.Change);
    }

    /// <summary>A driver that counts its wakes.</summary>
    private sealed class WakeCounter : IFrameDriver
    {
        private int _wakes;

        public int Wakes => Volatile.Read(ref _wakes);

        public void Wake() => Interlocked.Increment(ref _wakes);
    }

    private static int DrainHub(ChangeHub hub, IChangeSink sink)
    {
        var budget = FrameBudget.Unlimited;
        return hub.Drain(ref budget, sink);
    }

    /// <summary>Waits for <paramref name="condition"/> in real time, draining nothing; false when it did not come.</summary>
    private static bool WaitFor(Func<bool> condition, int timeoutMilliseconds = 3000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMilliseconds)
            {
                return condition();
            }

            Thread.Sleep(2);
        }

        return true;
    }

    /// <summary>Drains <paramref name="hub"/> every few milliseconds of real time until <paramref name="condition"/> holds.</summary>
    private static bool DrainUntil(ChangeHub hub, IChangeSink sink, Func<bool> condition, int timeoutMilliseconds = 3000) =>
        WaitFor(() =>
        {
            DrainHub(hub, sink);
            return condition();
        }, timeoutMilliseconds);

    /// <summary>Waits until a root's watch has taken in at least <paramref name="records"/> records in all, and then until they stop coming for a moment.</summary>
    private static bool WaitForRecords(WatchRoot root, long records, int timeoutMilliseconds = 5000)
    {
        if (!WaitFor(() => Interlocked.Read(ref root.Records!.Records) >= records, timeoutMilliseconds))
        {
            return false;
        }

        long seen;
        do
        {
            seen = Interlocked.Read(ref root.Records!.Records);
            Thread.Sleep(40);
        }
        while (Interlocked.Read(ref root.Records!.Records) != seen);
        return true;
    }

    /// <summary>
    /// A buffer of change records laid out as Windows fills one:
    /// FILE_NOTIFY_INFORMATION, or FILE_NOTIFY_EXTENDED_INFORMATION with
    /// <paramref name="details"/>.  Each record is (action, name relative to
    /// the watched folder, size, attributes).
    /// </summary>
    private static byte[] NotifyRecords(bool details, params (int Action, string Name, long Size, uint Attributes)[] records)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        for (var index = 0; index < records.Length; index++)
        {
            var (action, name, size, attributes) = records[index];
            var nameBytes = Encoding.Unicode.GetBytes(name);
            var header = details ? 84 : 12;
            var length = (header + nameBytes.Length + 7) & ~7;
            var start = stream.Position;
            writer.Write(index == records.Length - 1 ? 0 : length);
            writer.Write(action);
            if (details)
            {
                writer.Write(0L);
                writer.Write(new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc).ToFileTimeUtc());
                writer.Write(0L);
                writer.Write(0L);
                writer.Write(size);
                writer.Write(size);
                writer.Write(attributes);
                writer.Write(0u);
                writer.Write(0L);
                writer.Write(0L);
            }

            writer.Write(nameBytes.Length);
            writer.Write(nameBytes);
            while (stream.Position < start + length)
            {
                writer.Write((byte)0);
            }
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static readonly bool WatchOptimised =
        typeof(ChangeHub).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled != true;

    /// <summary>What a record may cost: the spec's 200 ns, or three times that for an unoptimised build.</summary>
    private static double RecordBudgetNanoseconds => WatchOptimised ? 200 : 600;

    // ---- the checks -------------------------------------------------------------------

    /// <summary>A change in a registered folder, heard through a watch of the real disk, on the real clock.</summary>
    private static void WatchRealChecks(string root)
    {
        var folder = Path.Combine(root, "real");
        Directory.CreateDirectory(Path.Combine(folder, "A"));
        using var hub = new ChangeHub(TimeProvider.System);
        var sink = new WatchSink();
        var calling = Environment.CurrentManagedThreadId;
        var armedOn = 0;
        hub.RootArmed += _ => Volatile.Write(ref armedOn, Environment.CurrentManagedThreadId);
        var watch = hub.AddRootForTests(folder);
        // The state is set under the root's lock and the event raised after
        // it, on the arming thread: both are waited for, or the check reads
        // the thread a moment before the event has named it.
        var armedThread = WaitFor(() => watch.State == WatchState.Armed && Volatile.Read(ref armedOn) != 0) ? Volatile.Read(ref armedOn) : 0;
        Check($"a watch arms off the calling thread ({watch.State}, armed on thread {armedThread}, called from {calling})", armedThread != 0 && armedThread != calling);
        Check($"and asks for extended records on NTFS ({watch.Watcher?.HasDetails}), in {watch.ArmMilliseconds:F2} ms", watch.Watcher?.HasDetails == true && watch.Handle is { IsInvalid: false });
        Check("arming moves the epoch on", watch.Epoch == 1);

        var target = new object();
        hub.Register(ChangeConsumer.List, Path.Combine(folder, "A") + "\\", target);
        var made = Stopwatch.GetTimestamp();
        Directory.CreateDirectory(Path.Combine(folder, "A", "model"));
        var heard = DrainUntil(hub, sink, () => sink.Changes.Count > 0);
        var latency = Stopwatch.GetElapsedTime(made).TotalMilliseconds;
        var change = heard ? sink.Changes[0] : default;
        Check($"a folder made in a registered folder is handed on in {latency:F0} ms (quiet 150 ms, at most 1 s)",
            heard && latency >= 140 && latency < 1000 && change.Consumer == ChangeConsumer.List && ReferenceEquals(change.Target, target));
        Check("keyed by the registered path without its separator, as structural",
            heard && change.Change.Key == Path.Combine(folder, "A") && change.Change.Kinds.HasFlag(ChangeKinds.Structural));
        Check("stamped with the first event's time", heard && change.Change.FirstTicks >= made && change.Change.FirstTicks <= Stopwatch.GetTimestamp());
        Check("and nothing is left", !hub.HasWork && hub.PendingCount == 0);

        // The temp folder's whole volume, as the app would watch it.
        var volume = hub.RootFor(Path.GetFullPath(Path.GetTempPath()));
        Check($"the temp folder's volume is a local root keyed by its drive ({volume?.Key})",
            volume is { Kind: WatchKind.Local, IsNetwork: false } && volume.Key == Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath())));
    }

    /// <summary>
    /// Changes nobody registered for: every record is looked up and dropped
    /// on the watcher's thread, nothing is pending, nothing is handed on, the
    /// lookup costs next to nothing and allocates nothing.
    /// </summary>
    private static void WatchNoiseChecks(string root)
    {
        var folder = Path.Combine(root, "noise");
        var noise = Path.Combine(folder, "N");
        Directory.CreateDirectory(noise);
        Directory.CreateDirectory(Path.Combine(folder, "W"));
        using var hub = new ChangeHub(new ManualTime());
        var sink = new WatchSink();
        var watch = hub.AddRootForTests(folder);
        WaitFor(() => watch.State == WatchState.Armed);

        // A thousand registered folders elsewhere, as a loaded tree would have.
        for (var index = 0; index < 1000; index++)
        {
            hub.Register(ChangeConsumer.Nested, Path.Combine(folder, "W", $"f{index}"), new object());
        }

        const int Files = 2000;
        for (var index = 0; index < Files; index++)
        {
            File.Create(Path.Combine(noise, $"n{index}.tmp")).Dispose();
        }

        for (var index = 0; index < Files; index++)
        {
            File.Delete(Path.Combine(noise, $"n{index}.tmp"));
        }

        WaitForRecords(watch, 2 * Files);
        var watcher = watch.Watcher!;
        var records = watcher.RecordCount;
        DrainHub(hub, sink);
        Check($"{records:N0} records in an unregistered folder leave nothing pending and hand nothing on",
            records >= 2 * Files && hub.PendingCount == 0 && sink.Changes.Count == 0 && watch.Records!.Hits == 0);
        var perRecord = watcher.ParseTicks * 1e9 / Stopwatch.Frequency / records;
        var perCompletion = watcher.WorkTicks * 1e9 / Stopwatch.Frequency / records;
        // Wall time on a pool thread while this one makes files: the steady
        // figure is the fed records' median below; this only rules out
        // anything grossly worse on the real path.
        Check($"a record costs {perRecord:F0} ns to parse and drop, {perCompletion:F0} ns with the completion's own work, in {watcher.Completions:N0} completions ({watcher.Gathered:N0} gathered)",
            perRecord < 5 * RecordBudgetNanoseconds && watcher.Completions < records / 10);
        var perThousand = watcher.AllocatedBytes * 1000.0 / records;
        Check($"and allocates {perThousand:F0} B per 1,000 records (budget 1 KB)", perThousand <= 1024);
    }

    /// <summary>A rename within one folder is one pair; a move between folders is a removal from one and an addition to the other; a registered folder renamed away is gone.</summary>
    private static void WatchRenameChecks(string root)
    {
        var folder = Path.Combine(root, "rename");
        Directory.CreateDirectory(Path.Combine(folder, "R", "old"));
        Directory.CreateDirectory(Path.Combine(folder, "R", "a"));
        Directory.CreateDirectory(Path.Combine(folder, "S"));
        var time = new ManualTime();
        using var hub = new ChangeHub(time);
        var sink = new WatchSink();
        var watch = hub.AddRootForTests(folder);
        WaitFor(() => watch.State == WatchState.Armed);
        var parent = new object();
        var renamed = new object();
        var other = new object();
        hub.Register(ChangeConsumer.List, Path.Combine(folder, "R"), parent);
        hub.Register(ChangeConsumer.Graph, Path.Combine(folder, "R", "old"), renamed);
        hub.Register(ChangeConsumer.List, Path.Combine(folder, "S"), other);

        var before = watch.Records!.Records;
        Directory.Move(Path.Combine(folder, "R", "old"), Path.Combine(folder, "R", "new"));
        WaitForRecords(watch, before + 2);
        time.Advance(150);
        DrainHub(hub, sink);
        var toParent = sink.For(parent).ToList();
        var toRenamed = sink.For(renamed).ToList();
        Check("a rename within a folder is one change to it, with the pair",
            toParent.Count == 1 && toParent[0].Kinds.HasFlag(ChangeKinds.Structural)
            && toParent[0].Renames.Span is [{ OldName: "old", NewName: "new" }]);
        Check("and the renamed folder's own registration hears it is gone", toRenamed.Count == 1 && toRenamed[0].Kinds.HasFlag(ChangeKinds.Gone));

        sink.Clear();
        before = watch.Records!.Records;
        Directory.Move(Path.Combine(folder, "R", "a"), Path.Combine(folder, "S", "a"));
        WaitForRecords(watch, before + 2);
        time.Advance(150);
        DrainHub(hub, sink);
        toParent = [.. sink.For(parent)];
        var toOther = sink.For(other).ToList();
        Check("a move between folders is a change to each, and no pair",
            toParent.Count == 1 && toOther.Count == 1 && toParent[0].Renames.IsEmpty && toOther[0].Renames.IsEmpty
            && toParent[0].Kinds.HasFlag(ChangeKinds.Structural) && toOther[0].Kinds.HasFlag(ChangeKinds.Structural));
    }

    /// <summary>
    /// A file's new size and date come with the change, which waits for the
    /// content lane's quiet spell; a sub-folder's own date moving reaches its
    /// parent as <see cref="ChangeKinds.DirDate"/>.  NTFS reports a folder's
    /// date moving on its own schedule - with the folder's next change, or
    /// when the date is set - not as a file is made inside it, so the check
    /// sets the date.
    /// </summary>
    private static void WatchContentChecks(string root)
    {
        var folder = Path.Combine(root, "content");
        var files = Path.Combine(folder, "C");
        Directory.CreateDirectory(Path.Combine(files, "X"));
        var data = Path.Combine(files, "data.bin");
        File.WriteAllBytes(data, new byte[100]);
        var hidden = Path.Combine(files, "hidden.bin");
        File.WriteAllBytes(hidden, new byte[10]);
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var time = new ManualTime();
        using var hub = new ChangeHub(time);
        var sink = new WatchSink();
        var watch = hub.AddRootForTests(folder);
        WaitFor(() => watch.State == WatchState.Armed);
        var listed = new object();
        var inner = new object();
        hub.Register(ChangeConsumer.List, files, listed);
        hub.Register(ChangeConsumer.Nested, Path.Combine(files, "X"), inner);

        var before = watch.Records!.Records;
        using (var stream = new FileStream(data, FileMode.Append))
        {
            stream.Write(new byte[50]);
        }

        WaitForRecords(watch, before + 1);
        time.Advance(999);
        DrainHub(hub, sink);
        var early = sink.Changes.Count;
        time.Advance(1);
        DrainHub(hub, sink);
        var changes = sink.For(listed).ToList();
        var written = File.GetLastWriteTimeUtc(data).Ticks;
        Check("a file's size changing waits for the content lane's quiet second", early == 0 && changes.Count == 1 && changes[0].Kinds == ChangeKinds.Content);
        Check($"and carries its new size and date ({(changes.Count == 1 && changes[0].Files.Length == 1 ? changes[0].Files.Span[0].Length : -1)} B)",
            changes.Count == 1 && changes[0].Files.Span is [{ Name: "data.bin", Length: 150 } delta] && Math.Abs(delta.ModifiedTicks - written) < TimeSpan.TicksPerSecond * 2);

        sink.Clear();
        before = watch.Records!.Records;
        using (var stream = new FileStream(hidden, FileMode.Append))
        {
            stream.Write(new byte[10]);
        }

        WaitForRecords(watch, before + 1);
        time.Advance(3000);
        DrainHub(hub, sink);
        changes = [.. sink.For(listed)];
        Check("a hidden file's change lists no files, so the folder is read", changes.Count == 1 && changes[0].Kinds.HasFlag(ChangeKinds.Content) && changes[0].Files.IsEmpty);

        // Its own folders, so nothing still settling from the above joins in.
        var dated = Path.Combine(folder, "P");
        Directory.CreateDirectory(Path.Combine(dated, "X"));
        var parent = new object();
        hub.Register(ChangeConsumer.List, dated, parent);
        hub.Register(ChangeConsumer.Nested, Path.Combine(dated, "X"), inner);
        Thread.Sleep(100);
        time.Advance(5000);
        DrainHub(hub, sink);
        sink.Clear();
        before = watch.Records!.Records;
        Directory.CreateDirectory(Path.Combine(dated, "X", "made"));
        Directory.SetLastWriteTimeUtc(Path.Combine(dated, "X"), DateTime.UtcNow);
        WaitForRecords(watch, before + 2);
        time.Advance(150);
        DrainHub(hub, sink);
        var toInner = sink.For(inner).ToList();
        var toParentEarly = sink.For(parent).Count();
        time.Advance(3000);
        DrainHub(hub, sink);
        changes = [.. sink.For(parent)];
        Check("a folder made in a sub-folder is structural there", toInner.Count == 1 && toInner[0].Kinds.HasFlag(ChangeKinds.Structural));
        Check($"and reaches the parent as the sub-folder's date, in the content lane ({toParentEarly} early, then {string.Join(", ", changes.Select(change => change.Kinds))})",
            toParentEarly == 0 && changes.Count == 1 && changes[0].Kinds == ChangeKinds.DirDate);
    }

    /// <summary>
    /// More changes than a 4 KB buffer holds, while the watcher is held from
    /// taking them in: one overflow, and the epoch moves on exactly once for
    /// it, with one bump handed on.
    /// </summary>
    private static void WatchOverflowChecks(string root)
    {
        var folder = Path.Combine(root, "overflow");
        Directory.CreateDirectory(folder);
        var previous = Environment.GetEnvironmentVariable(ChangeHub.BufferVariable);
        Environment.SetEnvironmentVariable(ChangeHub.BufferVariable, "4096");
        try
        {
            using var hub = new ChangeHub(TimeProvider.System);
            var sink = new WatchSink();
            var watch = hub.AddRootForTests(folder);
            WaitFor(() => watch.State == WatchState.Armed);
            Check($"{ChangeHub.BufferVariable}=4096 sets the buffer ({watch.Watcher?.BufferBytes} B)", hub.BufferOverride == 4096 && watch.Watcher?.BufferBytes == 4096);
            hub.Register(ChangeConsumer.Nested, folder, new object());

            for (var round = 1; round <= 2; round++)
            {
                var watcher = watch.Watcher!;
                var epoch = watch.Epoch;
                var overflows = watcher.Overflows;
                using var hold = new ManualResetEventSlim(false);
                watcher.Hold = hold;
                var completions = watcher.Completions;
                File.Create(Path.Combine(folder, $"trigger{round}.txt")).Dispose();
                Thread.Sleep(150);
                for (var index = 0; index < 400; index++)
                {
                    File.Create(Path.Combine(folder, $"r{round}-{index:D3}.txt")).Dispose();
                }

                watcher.Hold = null;
                hold.Set();
                WaitFor(() => watcher.Overflows > overflows, 3000);
                Thread.Sleep(200);
                sink.Clear();
                DrainUntil(hub, sink, () => sink.Bumps.Count > 0, 1000);
                var seen = watcher.Overflows - overflows;
                Check($"round {round}: 400 files past a held 4 KB buffer overflow once ({seen}), and the epoch moves on once ({watch.Epoch - epoch})",
                    seen == 1 && watch.Epoch - epoch == 1);
                Check($"round {round}: one bump is handed on", sink.Bumps.Count == 1 && ReferenceEquals(sink.Bumps[0], watch));
            }

            Check("the watch is still armed after its overflows", watch.State == WatchState.Armed && hub.Overflows == 2);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ChangeHub.BufferVariable, previous);
        }
    }

    /// <summary>
    /// Suspending closes the handle before it returns - the folder can then
    /// be opened by someone who shares nothing - and hears nothing; re-arming
    /// opens a new handle and moves the epoch on; dropping closes it for good.
    /// </summary>
    private static void WatchSuspendChecks(string root)
    {
        var folder = Path.Combine(root, "suspend");
        Directory.CreateDirectory(folder);
        using var hub = new ChangeHub(TimeProvider.System);
        var sink = new WatchSink();
        var armed = 0;
        var closed = 0;
        hub.RootArmed += _ => Interlocked.Increment(ref armed);
        hub.RootClosed += _ => Interlocked.Increment(ref closed);
        var watch = hub.AddRootForTests(folder);
        WaitFor(() => watch.State == WatchState.Armed);
        var target = new object();
        hub.Register(ChangeConsumer.Nested, folder, target);
        Check("while armed, the folder cannot be opened exclusively", OpenExclusively(folder) == 32);

        var handle = watch.Handle;
        hub.Suspend(watch);
        Check("Suspend closes the handle before it returns", watch.State == WatchState.Suspended && watch.Handle is null && handle is { IsClosed: true });
        Check("so the folder can be opened by someone who shares nothing", OpenExclusively(folder) == 0);
        Check("and no one is told the handle closed for good", closed == 0);
        File.Create(Path.Combine(folder, "while-suspended.txt")).Dispose();
        Thread.Sleep(300);
        DrainHub(hub, sink);
        Check("nothing is heard while suspended", sink.Changes.Count == 0);

        var epoch = watch.Epoch;
        hub.Rearm(watch);
        Check("Rearm arms it again with a new handle", WaitFor(() => watch.State == WatchState.Armed) && watch.Handle is { IsClosed: false } && armed == 2);
        DrainUntil(hub, sink, () => sink.Bumps.Count > 0, 1000);
        Check("and moves the epoch on, handing the bump on", watch.Epoch == epoch + 1 && sink.Bumps.Count == 1);
        File.Create(Path.Combine(folder, "after.txt")).Dispose();
        Check("and hears changes again", DrainUntil(hub, sink, () => sink.Changes.Count > 0));

        handle = watch.Handle;
        hub.Drop(watch);
        Check("Drop closes it for good and says so", watch.State == WatchState.Off && watch.Handle is null && handle is { IsClosed: true } && closed == 1);
        Check("and the folder is no longer that root's", !ReferenceEquals(hub.RootFor(folder), watch) && OpenExclusively(folder) == 0);
    }

    /// <summary>Opens a folder sharing nothing: zero, or the error it failed with.</summary>
    private static int OpenExclusively(string folder)
    {
        using var handle = CreateFileForTest(folder, 0x80000000, 0, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        return handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileForTest(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    /// <summary>
    /// A watch that cannot be armed is tried again after 1, 2, 4 … 60 seconds
    /// and polled every five seconds meanwhile; once its folder is there, the
    /// next retry arms it.  A volume that refuses watches is polled for good.
    /// </summary>
    private static void WatchErrorChecks(string root)
    {
        var missing = Path.Combine(root, "errors", "missing");
        var time = new ManualTime();
        using var hub = new ChangeHub(time);
        var sink = new WatchSink();
        var watch = hub.AddRootForTests(missing);
        var failed = WaitFor(() => Volatile.Read(ref watch.ArmAttempts) == 1) && watch.State == WatchState.Off && watch.RetryAt != 0;
        Check($"a folder that is not there leaves the root off, to be tried again (error {watch.LastError})", failed && watch.LastError is 2 or 3);
        hub.Register(ChangeConsumer.Nested, Path.Combine(missing, "x"), new object());

        // Five seconds at a time, draining after each, as frames would.
        var delays = new List<double>();
        var polls = 0;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var retryAt = watch.RetryAt;
            var attempts = Volatile.Read(ref watch.ArmAttempts);
            delays.Add((retryAt - time.GetTimestamp()) / (double)TimeSpan.TicksPerSecond);
            while (time.GetTimestamp() < retryAt)
            {
                time.AdvanceTo(Math.Min(retryAt, time.GetTimestamp() + 5 * TimeSpan.TicksPerSecond));
                DrainHub(hub, sink);
                polls += sink.Polls.Count;
                sink.Clear();
            }

            WaitFor(() => Volatile.Read(ref watch.ArmAttempts) > attempts);
        }

        var elapsed = delays.Sum();
        Check($"retries come after {string.Join(", ", delays.Select(delay => $"{delay:F0}"))} s", delays.Select(delay => Math.Round(delay)).SequenceEqual([1d, 2, 4, 8, 16, 32, 60, 60]));
        Check($"and the root is polled meanwhile ({polls} polls in {elapsed:F0} s)", polls >= 20 && polls <= (int)(elapsed / 5) + 1);

        Directory.CreateDirectory(missing);
        var epoch = watch.Epoch;
        time.AdvanceTo(watch.RetryAt);
        Check("once the folder is there, the next retry arms it and moves the epoch on",
            WaitFor(() => watch.State == WatchState.Armed) && watch.Epoch == epoch + 1 && watch.RetryAt == 0);
        DrainUntil(hub, sink, () => sink.Bumps.Count > 0, 1000);
        Check("and hands the bump on", sink.Bumps.Count == 1);

        // A volume that refuses a watch: polled every five seconds, never retried.
        var refusing = Path.Combine(root, "errors", "refusing");
        Directory.CreateDirectory(refusing);
        hub.ArmFailureForTests = candidate => candidate.Key == refusing ? 1 : 0;
        var polled = hub.AddRootForTests(refusing);
        Check("a volume that refuses watches is polled", WaitFor(() => polled.State == WatchState.Polling) && polled.Kind == WatchKind.Polling && polled.RetryAt == 0);
        hub.Register(ChangeConsumer.Nested, Path.Combine(refusing, "y"), new object());
        sink.Clear();
        time.Advance(5000);
        DrainHub(hub, sink);
        var fromTimer = sink.Polls.Count(candidate => ReferenceEquals(candidate, polled));
        sink.Clear();
        hub.PollNow();
        DrainHub(hub, sink);
        Check($"every five seconds ({fromTimer}) and when asked ({sink.Polls.Count(candidate => ReferenceEquals(candidate, polled))})",
            fromTimer == 1 && sink.Polls.Count(candidate => ReferenceEquals(candidate, polled)) == 1);
        hub.ArmFailureForTests = null;
    }

    /// <summary>
    /// A share is armed only once drawn, off the calling thread even when
    /// opening it hangs; kept while drawn or while the list shows it; let go
    /// thirty seconds after; armed again, with a new epoch, when drawn again.
    /// </summary>
    private static void WatchNetworkChecks(string root)
    {
        var folder = Path.Combine(root, "share");
        Directory.CreateDirectory(folder);
        var time = new ManualTime();
        using var hub = new ChangeHub(time);
        var closed = 0;
        hub.RootClosed += _ => Interlocked.Increment(ref closed);
        using var hang = new ManualResetEventSlim(false);
        hub.ArmFailureForTests = _ =>
        {
            hang.Wait(2000);
            return 0;
        };
        var share = hub.AddRootForTests(folder, WatchKind.Network);
        Thread.Sleep(50);
        Check($"a share is not armed until drawn, and its buffer is SMB's 64 KB ({hub.BufferFor(share)})",
            share.State == WatchState.Off && share.IsNetwork && hub.BufferFor(share) == 64 * 1024);

        var asked = Stopwatch.GetTimestamp();
        hub.NoteDrawn(share);
        var returned = Stopwatch.GetElapsedTime(asked).TotalMilliseconds;
        Thread.Sleep(50);
        var stillOff = share.State == WatchState.Off;
        hang.Set();
        Check($"drawing it arms it on another thread: NoteDrawn returned in {returned:F2} ms while the open hung",
            returned < 20 && stillOff && WaitFor(() => share.State == WatchState.Armed) && share.Epoch == 1);
        hub.ArmFailureForTests = null;

        for (var second = 0; second < 5; second++)
        {
            time.Advance(10_000);
            hub.NoteDrawn(share);
        }

        Check("drawn every ten seconds, it stays armed", share.State == WatchState.Armed);
        time.Advance(29_000);
        var keptAt29 = share.State == WatchState.Armed;
        time.Advance(1_000);
        Check("thirty seconds after it was last drawn it is let go, and closing it is told",
            keptAt29 && share.State == WatchState.Off && share.Handle is null && closed == 1);

        hub.NoteDrawn(share);
        Check("drawn again, it arms again with a new epoch", WaitFor(() => share.State == WatchState.Armed) && share.Epoch == 2);
        var listed = new object();
        hub.Register(ChangeConsumer.List, Path.Combine(folder, "x"), listed);
        time.Advance(120_000);
        Check("while the list shows it, it stays armed undrawn", share.State == WatchState.Armed);
        hub.Unregister(ChangeConsumer.List, Path.Combine(folder, "x"), listed);
        time.Advance(29_000);
        var keptAfterList = share.State == WatchState.Armed;
        time.Advance(1_000);
        Check("and is let go thirty seconds after the list lets go", keptAfterList && share.State == WatchState.Off && closed == 2);

        // A share that cannot be reached is retried while drawn, and left
        // alone once nothing shows it, until it is drawn again.
        var gone = Path.Combine(root, "share-gone");
        var away = hub.AddRootForTests(gone, WatchKind.Network);
        hub.NoteDrawn(away);
        WaitFor(() => Volatile.Read(ref away.ArmAttempts) == 1);
        for (var second = 0; second < 20; second++)
        {
            var attempts = Volatile.Read(ref away.ArmAttempts);
            var due = away.RetryAt;
            time.Advance(1_000);
            if (due != 0 && due <= time.GetTimestamp())
            {
                WaitFor(() => Volatile.Read(ref away.ArmAttempts) > attempts);
            }
        }

        var whileDrawn = Volatile.Read(ref away.ArmAttempts);
        for (var second = 0; second < 180; second += 5)
        {
            var attempts = Volatile.Read(ref away.ArmAttempts);
            var due = away.RetryAt;
            time.Advance(5_000);
            if (due != 0 && due <= time.GetTimestamp())
            {
                WaitFor(() => Volatile.Read(ref away.ArmAttempts) > attempts || away.RetryAt == 0, 1000);
            }
        }

        var afterwards = Volatile.Read(ref away.ArmAttempts);
        Check($"a share that cannot be reached is retried while drawn ({whileDrawn} attempts), then left alone ({afterwards - whileDrawn} more in three minutes)",
            whileDrawn >= 4 && afterwards - whileDrawn <= 2 && away.RetryAt == 0 && away.State == WatchState.Off);
        Directory.CreateDirectory(gone);
        hub.NoteDrawn(away);
        Check("drawn again once it is back, it arms", WaitFor(() => away.State == WatchState.Armed));
    }

    /// <summary>
    /// Drive letters: a mapped letter and its share's UNC name are one root, a
    /// subst letter is a spelling of the volume it points into and hears its
    /// changes under its own name, a CD is not watched.  Then what this
    /// machine's own letters are, asked without watching anything.
    /// </summary>
    private static void WatchVolumeChecks(string root)
    {
        var substituted = Path.Combine(root, "subst");
        Directory.CreateDirectory(Path.Combine(substituted, "A"));
        var system = char.ToUpperInvariant(Path.GetFullPath(substituted)[0]);
        var volumes = new VolumeResolver(letter => letter switch
        {
            'N' => new VolumeResolver.Letter(WatchNative.DriveRemote, @"\\server\share", null),
            'S' => new VolumeResolver.Letter(WatchNative.DriveFixed, null, substituted),
            'R' => new VolumeResolver.Letter(WatchNative.DriveCdRom, null, null),
            _ when letter == system => new VolumeResolver.Letter(WatchNative.DriveFixed, null, null),
            _ => new VolumeResolver.Letter(WatchNative.DriveNoRootDirectory, null, null)
        });
        using var hub = new ChangeHub(TimeProvider.System, volumes);
        hub.ArmFailureForTests = candidate => candidate.IsNetwork ? WatchNative.ErrorBadNetPath : 0;
        var sink = new WatchSink();

        var mapped = hub.RootFor(@"N:\projects\x");
        var unc = hub.RootFor(@"\\SERVER\share\projects");
        Check($"a mapped letter and its share's UNC name are one network root ({mapped?.Key}: {string.Join(" ", mapped?.Prefixes ?? [])})",
            mapped is { Kind: WatchKind.Network, IsNetwork: true } && ReferenceEquals(mapped, unc) && mapped.Key == @"\\server\share"
            && mapped.Prefixes.Contains(@"N:\") && mapped.Prefixes.Contains(@"\\server\share\"));
        Check("and a share is not armed until something shows it", mapped?.State == WatchState.Off && mapped.Watcher is null);

        var cd = hub.RootFor(@"R:\");
        Thread.Sleep(50);
        Check("a CD's root is not watched", cd is { Kind: WatchKind.None, State: WatchState.Off } && cd.Watcher is null);

        var volume = hub.RootFor($@"{system}:\");
        var subst = hub.RootFor(@"S:\A");
        Check($"a subst letter is a spelling of the volume it points into ({subst?.Key}: {string.Join(" ", subst?.Prefixes ?? [])})",
            subst is { Kind: WatchKind.Local } && ReferenceEquals(subst, volume) && subst.Prefixes.Contains(@"S:\"));
        var volumeArmed = WaitFor(() => volume?.State == WatchState.Armed);
        Check($"the volume arms off the calling thread ({volume?.ArmMilliseconds:F2} ms to open and ask)", volumeArmed);
        var target = new object();
        hub.Register(ChangeConsumer.List, @"S:\A", target);
        File.Create(Path.Combine(substituted, "A", "through-subst.txt")).Dispose();
        var heard = DrainUntil(hub, sink, () => sink.For(target).Any());
        Check($"a change there is heard under the subst letter's own name ({(heard ? sink.For(target).First().Key : "nothing")})",
            heard && sink.For(target).First().Key == @"S:\A");
        Check("a path that is on no volume has no root", hub.RootFor("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}") is null && hub.RootFor(string.Empty) is null);

        // This machine's letters, asked of Windows without opening anything.
        var letters = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            var letter = char.ToUpperInvariant(drive.Name[0]);
            var found = VolumeResolver.FromSystem(letter);
            if (found.SubstTarget is { } target2)
            {
                letters.Add($"{letter}: subst of {target2}");
            }
            else if (found.DriveType == WatchNative.DriveRemote)
            {
                letters.Add($"{letter}: mapped to {found.Share ?? "?"}");
            }
        }

        var systemFound = VolumeResolver.FromSystem(system);
        Check($"this machine: {system}: is a local volume{(letters.Count > 0 ? "; " + string.Join("; ", letters) : string.Empty)}",
            systemFound.DriveType is WatchNative.DriveFixed or WatchNative.DriveRemovable && systemFound.SubstTarget is null && systemFound.Share is null);
    }

    /// <summary>A folder reported under its short 8.3 name is found under its long one.</summary>
    private static void WatchShortNameChecks(string root)
    {
        Check("short names are told from names with a tilde",
            RootRecords.HasShortName(@"C:\PROGRA~1\x") && RootRecords.HasShortName(@"Users\LONGFO~2.TXT")
            && !RootRecords.HasShortName(@"Temp\~$report.docx") && !RootRecords.HasShortName(@"a~b\c")
            && !RootRecords.HasShortName(@"AVERYLONGNAMEINDEED~1\x"));

        var folder = Path.Combine(root, "short");
        var longName = Path.Combine(folder, "Long Folder Name For Short", "Inner");
        Directory.CreateDirectory(longName);
        var shortParent = ShortPathOf(Path.Combine(folder, "Long Folder Name For Short"));
        var shortPart = Path.GetFileName(shortParent);
        if (shortPart.Equals("Long Folder Name For Short", StringComparison.OrdinalIgnoreCase) || !shortPart.Contains('~'))
        {
            Check("(8.3 names are not made on this volume; the long-name lookup is not exercised)", true);
            return;
        }

        using var hub = new ChangeHub(new ManualTime());
        var sink = new WatchSink();
        var watch = hub.AddRootForTests(folder, arm: false);
        var target = new object();
        hub.Register(ChangeConsumer.List, longName, target);
        hub.FeedForTests(watch, NotifyRecords(false, (WatchNative.ActionAdded, $@"{shortPart}\Inner\new.txt", 0, 0)), details: false);
        Check($"a change under {shortPart}\\Inner is found under the long name", watch.Records!.LongNameHits == 1 && hub.PendingCount == 1);
    }

    private static string ShortPathOf(string path)
    {
        var buffer = new char[1024];
        var length = GetShortPathNameForTest(path, buffer, buffer.Length);
        return length > 0 && length < buffer.Length ? new string(buffer, 0, (int)length) : path;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetShortPathNameForTest(string longPath, [Out] char[] shortPath, int length);

    /// <summary>
    /// Folders a refresh dropped stay registered until swept: a drain skips
    /// them and drops their registration, and once they pile up past twice
    /// what the last sweep left, a sweep off the UI thread drops them all.
    /// </summary>
    private static void WatchSweepChecks(string root)
    {
        var folder = Path.Combine(root, "sweep");
        Directory.CreateDirectory(folder);
        var time = new ManualTime();
        using var hub = new ChangeHub(time);
        var sink = new WatchSink();
        var watch = hub.AddRootForTests(folder, arm: false);

        var top = new NestedFolder(folder, "sweep", NestedFolderKind.Folder, null);
        var dropped = new NestedFolder(Path.Combine(folder, "dropped"), "dropped", NestedFolderKind.Folder, top);
        var live = new NestedFolder(Path.Combine(folder, "live"), "live", NestedFolderKind.Folder, top);
        hub.Register(ChangeConsumer.Nested, dropped.FullPath, dropped);
        hub.Register(ChangeConsumer.Nested, live.FullPath, live);
        dropped.IsForgotten = true;
        hub.FeedForTests(watch, NotifyRecords(false, (WatchNative.ActionAdded, @"dropped\a.txt", 0, 0), (WatchNative.ActionAdded, @"live\a.txt", 0, 0)), details: false);
        time.Advance(150);
        DrainHub(hub, sink);
        Check("a change in a dropped folder is not handed on, and its registration goes",
            sink.Changes.Count == 1 && ReferenceEquals(sink.Changes[0].Target, live) && hub.Registry.NestedTargets == 1 && watch.NestedInterest == 1);

        // A refresh dropped a folder with five thousand read below it.
        var gone = new NestedFolder(Path.Combine(folder, "gone"), "gone", NestedFolderKind.Folder, top);
        var below = new List<NestedFolder>();
        for (var index = 0; index < 5000; index++)
        {
            below.Add(new NestedFolder(Path.Combine(gone.FullPath, $"c{index}"), $"c{index}", NestedFolderKind.Folder, gone));
        }

        gone.IsForgotten = true;
        foreach (var child in below)
        {
            hub.Register(ChangeConsumer.Nested, child.FullPath, child);
        }

        var swept = WaitFor(() => hub.Registry.NestedTargets <= ChangeHub.SweepFloor);
        Check($"once stale registrations pile up, a sweep off the UI thread drops them ({hub.Registry.NestedTargets} of 5,001 left)",
            swept && hub.Registry.NestedTargets >= 1);
        var removed = hub.Sweep();
        Check($"and every one of them goes ({removed} more by hand), the live one stays", hub.Registry.NestedTargets == 1 && hub.Registry.Count == 1 && watch.NestedInterest == 1);
    }
}
