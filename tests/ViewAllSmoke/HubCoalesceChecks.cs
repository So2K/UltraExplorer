using System.Diagnostics;
using System.IO;
using UltraExplorer.Controls;
using UltraExplorer.Services.Watch;

namespace ViewAllSmoke;

/// <summary>
/// The change hub's timing, on a fake clock: when a folder's merged changes
/// fall due - 150 ms after the last on a local volume, 500 ms after the first
/// however steady the stream, 300 and 1,000 ms on a share, 1 and 3 s for sizes
/// and dates - how a slow refresh spaces the next, what an immediate touch
/// does, a share's rechecks after a refresh that found nothing, the order and
/// budget of a drain, one wake for a batch, renames paired across records,
/// and what a record costs.  Records are fed in as Windows lays them out,
/// through the same parser a watch uses; one run hears a real folder.
/// </summary>
internal static partial class Program
{
    private static Task HubCoalesceChecks()
    {
        Section("change hub timing");
        var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "UltraExplorerHub", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            HubDueChecks(root);
            HubRefreshGapChecks(root);
            HubTouchChecks(root);
            HubRecheckChecks(root);
            HubDrainChecks(root);
            HubRecordChecks(root);
            HubRecordCostChecks(root);
            HubRealStreamChecks(root);
        }
        finally
        {
            TryDelete(root);
        }

        return Task.CompletedTask;
    }

    /// <summary>A hub on a fake clock with an unarmed root on <paramref name="folder"/>, which records are fed into.</summary>
    private static (ChangeHub Hub, ManualTime Time, WatchRoot Root, WatchSink Sink) FedHub(string folder, WatchKind kind = WatchKind.Local)
    {
        Directory.CreateDirectory(folder);
        var time = new ManualTime();
        var hub = new ChangeHub(time);
        var root = hub.AddRootForTests(folder, kind, arm: false);
        var sink = new WatchSink { Clock = () => time.Milliseconds };
        return (hub, time, root, sink);
    }

    private static void Feed(ChangeHub hub, WatchRoot root, params (int Action, string Name)[] records) =>
        hub.FeedForTests(root, NotifyRecords(false, [.. records.Select(record => (record.Action, record.Name, 0L, 0u))]), details: false);

    /// <summary>Advances the clock a millisecond at a time until something is handed on; the time it was, or -1.</summary>
    private static double FirstDelivery(ChangeHub hub, ManualTime time, WatchSink sink, double limit)
    {
        var start = time.Milliseconds;
        while (time.Milliseconds - start < limit)
        {
            time.Advance(1);
            if (DrainHub(hub, sink) > 0 && sink.Changes.Count > 0)
            {
                return time.Milliseconds - start;
            }
        }

        return -1;
    }

    private static void HubDueChecks(string root)
    {
        var (hub, time, watch, sink) = FedHub(Path.Combine(root, "due"));
        using var _ = hub;
        var folder = new object();
        hub.Register(ChangeConsumer.Nested, Path.Combine(watch.Key, "A"), folder);

        Feed(hub, watch, (1, @"A\one.txt"));
        var at = FirstDelivery(hub, time, sink, 2000);
        Check($"one change in a local folder is handed on after 150 ms of quiet ({at} ms)", at == 150 && sink.Changes.Count == 1);

        // A steady stream every 100 ms never goes quiet: the longest wait wins.
        sink.Clear();
        var start = time.Milliseconds;
        var delivered = -1d;
        for (var step = 0; step < 30 && delivered < 0; step++)
        {
            Feed(hub, watch, (1, $@"A\stream{step}.txt"));
            for (var tick = 0; tick < 100 && delivered < 0; tick++)
            {
                time.Advance(1);
                DrainHub(hub, sink);
                if (sink.Changes.Count > 0)
                {
                    delivered = time.Milliseconds - start;
                }
            }
        }

        Check($"a change every 100 ms is handed on 500 ms after the first ({delivered} ms)", delivered == 500);

        // A share waits longer for quiet, and for its latest.
        var (shareHub, shareTime, shareRoot, shareSink) = FedHub(Path.Combine(root, "due-share"), WatchKind.Network);
        using var __ = shareHub;
        shareHub.Register(ChangeConsumer.Nested, Path.Combine(shareRoot.Key, "A"), folder);
        Feed(shareHub, shareRoot, (1, @"A\one.txt"));
        at = FirstDelivery(shareHub, shareTime, shareSink, 2000);
        shareSink.Clear();
        var shareStart = shareTime.Milliseconds;
        delivered = -1;
        for (var step = 0; step < 30 && delivered < 0; step++)
        {
            Feed(shareHub, shareRoot, (1, $@"A\stream{step}.txt"));
            for (var tick = 0; tick < 200 && delivered < 0; tick++)
            {
                shareTime.Advance(1);
                DrainHub(shareHub, shareSink);
                if (shareSink.Changes.Count > 0)
                {
                    delivered = shareTime.Milliseconds - shareStart;
                }
            }
        }

        Check($"on a share: 300 ms of quiet ({at} ms), at most 1,000 ms under a stream ({delivered} ms)", at == 300 && delivered == 1000);

        // Sizes and dates only: a second of quiet, three at most; a name
        // changing as well brings it forward to the names' timing.
        sink.Clear();
        Feed(hub, watch, (3, @"A\one.txt"));
        at = FirstDelivery(hub, time, sink, 5000);
        Check($"a size or date change waits a second of quiet ({at} ms), as content", at == 1000 && sink.Changes[0].Change.Kinds == ChangeKinds.Content);
        sink.Clear();
        start = time.Milliseconds;
        delivered = -1;
        for (var step = 0; step < 40 && delivered < 0; step++)
        {
            Feed(hub, watch, (3, @"A\one.txt"));
            for (var tick = 0; tick < 500 && delivered < 0; tick++)
            {
                time.Advance(1);
                DrainHub(hub, sink);
                if (sink.Changes.Count > 0)
                {
                    delivered = time.Milliseconds - start;
                }
            }
        }

        Check($"and at most three under a stream ({delivered} ms)", delivered == 3000);
        sink.Clear();
        Feed(hub, watch, (3, @"A\one.txt"));
        time.Advance(400);
        Feed(hub, watch, (1, @"A\two.txt"));
        at = FirstDelivery(hub, time, sink, 5000);
        Check($"a new name joining a size change is handed on at the names' timing ({400 + at} ms), with both kinds",
            at == 150 && sink.Changes[0].Change.Kinds == (ChangeKinds.Structural | ChangeKinds.Content));
    }

    private static void HubRefreshGapChecks(string root)
    {
        var (hub, time, watch, sink) = FedHub(Path.Combine(root, "gap"));
        using var _ = hub;
        var folder = new object();
        var path = Path.Combine(watch.Key, "A");
        hub.Register(ChangeConsumer.Nested, path, folder);

        // A slow folder: a 100 ms read and 20 ms apply space it 1.4 s.
        hub.ReportRefresh(path, 100, 20, listingChanged: true);
        Feed(hub, watch, (1, @"A\one.txt"));
        var at = FirstDelivery(hub, time, sink, 5000);
        Check($"after a refresh that read for 100 ms and applied for 20, the next waits 1,400 ms ({at} ms)", at == 1400);

        // A quick one is still held to 250 ms.
        sink.Clear();
        hub.ReportRefresh(path, 1, 0.1, listingChanged: true);
        Feed(hub, watch, (1, @"A\two.txt"));
        at = FirstDelivery(hub, time, sink, 5000);
        Check($"however quick the read, the next refresh waits 250 ms ({at} ms)", at == 250);

        // A refresh reported while a change is already pending - the tree read
        // the folder again on its own, because a sub-folder of it on screen
        // went - leaves that change its time: the folder list has not seen it.
        sink.Clear();
        time.Advance(1_000);
        Feed(hub, watch, (4, @"A\old"), (5, @"A\new"));
        time.Advance(100);
        hub.ReportRefresh(path, 1, 0.1, listingChanged: true);
        at = FirstDelivery(hub, time, sink, 5000);
        Check($"a change heard before a refresh was reported keeps its time: handed on {100 + at} ms after it was heard (150), not held to the gap",
            at == 50 && sink.Changes.Count == 1);

        // What is heard after the report still waits for the gap.
        sink.Clear();
        hub.ReportRefresh(path, 1, 0.1, listingChanged: true);
        Feed(hub, watch, (1, @"A\three.txt"));
        at = FirstDelivery(hub, time, sink, 5000);
        Check($"a change heard after the report waits for the gap ({at} ms)", at == 250);

        // A steady stream into a folder refreshed as soon as it is due.
        sink.Clear();
        var refreshes = new List<double>();
        var start = time.Milliseconds;
        for (var tick = 0; tick < 3000; tick++)
        {
            if (tick % 50 == 0)
            {
                Feed(hub, watch, (1, $@"A\s{tick}.txt"));
            }

            time.Advance(1);
            if (DrainHub(hub, sink) > 0 && sink.Changes.Count > 0)
            {
                refreshes.Add(time.Milliseconds - start);
                sink.Clear();
                hub.ReportRefresh(path, 2, 0.5, listingChanged: true);
            }
        }

        var gaps = refreshes.Zip(refreshes.Skip(1), (first, second) => second - first).ToList();
        Check($"a change every 50 ms for 3 s: first refresh at {(refreshes.Count > 0 ? refreshes[0] : -1)} ms, then every {(gaps.Count > 0 ? gaps.Min() : 0)}-{(gaps.Count > 0 ? gaps.Max() : 0)} ms ({refreshes.Count} refreshes)",
            refreshes.Count > 0 && refreshes[0] <= 500 && gaps.Count > 0 && gaps.Max() <= 700 && refreshes.Count <= 3 * 4);
    }

    private static void HubTouchChecks(string root)
    {
        var (hub, time, watch, sink) = FedHub(Path.Combine(root, "touch"));
        using var _ = hub;
        var list = new object();
        var path = Path.Combine(watch.Key, "A");
        hub.Register(ChangeConsumer.List, path, list);
        hub.ReportRefresh(path, 100, 20, listingChanged: true);

        var touched = time.Milliseconds;
        hub.Touch(path + "\\", immediate: true);
        time.Advance(0);
        DrainHub(hub, sink);
        Check("an immediate touch is handed on at once, ahead of the gap after a slow refresh", sink.Changes.Count == 1 && sink.Changes[0].At == touched);

        sink.Clear();
        hub.ReportRefresh(path, 1, 0, listingChanged: true);
        time.Advance(250);
        hub.Touch(path, immediate: false);
        var at = FirstDelivery(hub, time, sink, 2000);
        Check($"a touch that need not be at once is timed like a change heard ({at} ms)", at == 150);

        sink.Clear();
        hub.Touch(Path.Combine(watch.Key, "nobody"), immediate: true);
        time.Advance(1);
        DrainHub(hub, sink);
        Check("touching a folder nobody registered does nothing", sink.Changes.Count == 0 && hub.PendingCount == 0);
    }

    /// <summary>
    /// A share's refresh that found nothing new is read again after 2 s, and
    /// after 10.5 s if that found nothing either: the share's client may have
    /// answered from its ten-second cache.  A refresh that found something ends it.
    /// </summary>
    private static void HubRecheckChecks(string root)
    {
        var (hub, time, watch, sink) = FedHub(Path.Combine(root, "recheck"), WatchKind.Network);
        using var _ = hub;
        var folder = new object();
        var path = Path.Combine(watch.Key, "A");
        hub.Register(ChangeConsumer.Nested, path, folder);

        Feed(hub, watch, (1, @"A\one.txt"));
        FirstDelivery(hub, time, sink, 2000);
        sink.Clear();
        var reported = time.Milliseconds;
        hub.ReportRefresh(path, 3, 1, listingChanged: false);
        var first = FirstDelivery(hub, time, sink, 20_000);
        sink.Clear();
        hub.ReportRefresh(path, 3, 1, listingChanged: false);
        var second = FirstDelivery(hub, time, sink, 20_000);
        var secondAt = time.Milliseconds - reported;
        sink.Clear();
        hub.ReportRefresh(path, 3, 1, listingChanged: false);
        var third = FirstDelivery(hub, time, sink, 30_000);
        Check($"a share's refresh that found nothing is read again after {first} ms and {secondAt} ms, then left",
            first == 2000 && secondAt == 10_500 && second > 0 && third < 0);

        sink.Clear();
        Feed(hub, watch, (1, @"A\two.txt"));
        FirstDelivery(hub, time, sink, 2000);
        sink.Clear();
        hub.ReportRefresh(path, 3, 1, listingChanged: false);
        FirstDelivery(hub, time, sink, 5000);
        sink.Clear();
        hub.ReportRefresh(path, 3, 1, listingChanged: true);
        Check("a recheck that finds the change ends the rechecks", FirstDelivery(hub, time, sink, 30_000) < 0);

        // A change heard on a share while its folder was being read keeps its
        // time when the read found nothing new: the recheck the report asks
        // for rides on it rather than holding it to the gap.
        var (share, shareTime, shareRoot, shareSink) = FedHub(Path.Combine(root, "recheck-heard"), WatchKind.Network);
        using var ___ = share;
        var sharePath = Path.Combine(shareRoot.Key, "A");
        share.Register(ChangeConsumer.Nested, sharePath, folder);
        Feed(share, shareRoot, (1, @"A\one.txt"));
        FirstDelivery(share, shareTime, shareSink, 2000);
        shareSink.Clear();
        shareTime.Advance(100);
        Feed(share, shareRoot, (4, @"A\old"), (5, @"A\new"));
        shareTime.Advance(100);
        share.ReportRefresh(sharePath, 1, 0.1, listingChanged: false);
        var heardAt = FirstDelivery(share, shareTime, shareSink, 5000);
        Check($"on a share, a change heard during a read that found nothing keeps its time: handed on {100 + heardAt} ms after it was heard (300), not held to the gap",
            heardAt == 200 && shareSink.Changes.Count == 1);

        // A local folder is never rechecked.
        var (local, localTime, localRoot, localSink) = FedHub(Path.Combine(root, "recheck-local"));
        using var __ = local;
        var localPath = Path.Combine(localRoot.Key, "A");
        local.Register(ChangeConsumer.Nested, localPath, folder);
        Feed(local, localRoot, (1, @"A\one.txt"));
        FirstDelivery(local, localTime, localSink, 2000);
        localSink.Clear();
        local.ReportRefresh(localPath, 3, 1, listingChanged: false);
        Check("a local folder's refresh is never rechecked", FirstDelivery(local, localTime, localSink, 15_000) < 0);
    }

    /// <summary>
    /// Three hundred folders falling due together: one wake, the shallowest
    /// first, 256 in one drain and the rest in the next; a change due but not
    /// yet drained takes in later events rather than making a second.
    /// </summary>
    private static void HubDrainChecks(string root)
    {
        var (hub, time, watch, sink) = FedHub(Path.Combine(root, "drain"));
        using var _ = hub;
        var wakes = new WakeCounter();
        hub.Driver.Fallback = wakes;
        var records = new List<(int, string)>();
        for (var index = 0; index < 300; index++)
        {
            var depth = index % 3;
            var relative = string.Join('\\', Enumerable.Range(0, depth + 1).Select(level => level == depth ? $"f{index}" : $"d{level}"));
            hub.Register(ChangeConsumer.Nested, Path.Combine(watch.Key, relative), relative);
            records.Add((1, relative + @"\new.txt"));
        }

        Feed(hub, watch, [.. records]);
        time.Advance(150);
        Check($"three hundred folders falling due together wake the driver once ({wakes.Wakes})", wakes.Wakes == 1 && hub.HasWork);
        var budget = FrameBudget.Start(FrameBudgets.HubMs * 1000, FrameBudgets.HubItems);
        var first = hub.Drain(ref budget, sink);
        var depths = sink.Changes.Select(change => change.Change.Key.Count(character => character == '\\')).ToList();
        Check($"a drain takes {first} of them, shallowest first", first == 256 && depths.Zip(depths.Skip(1), (a, b) => a <= b).All(ordered => ordered));
        Check("and leaves the rest for the next", hub.HasWork && DrainHub(hub, sink) == 44 && sink.Changes.Count == 300 && !hub.HasWork);

        // Due and waiting: later events join it.
        sink.Clear();
        Feed(hub, watch, (1, @"f0\late1.txt"));
        time.Advance(150);
        Feed(hub, watch, (4, @"f0\a.txt"), (5, @"f0\b.txt"));
        DrainHub(hub, sink);
        Check("a change due but not yet drained takes in what came after it: one hand-on, with the rename",
            sink.Changes.Count == 1 && sink.Changes[0].Change.Renames.Span is [{ OldName: "a.txt", NewName: "b.txt" }]);
    }

    /// <summary>Renames pair only within one folder and one buffer; file changes are listed up to the limit; gone folders are told by their own path.</summary>
    private static void HubRecordChecks(string root)
    {
        var (hub, time, watch, sink) = FedHub(Path.Combine(root, "records"));
        using var _ = hub;
        var a = new object();
        var b = new object();
        var sub = new object();
        hub.Register(ChangeConsumer.List, Path.Combine(watch.Key, "A"), a);
        hub.Register(ChangeConsumer.List, Path.Combine(watch.Key, "B"), b);
        hub.Register(ChangeConsumer.Graph, Path.Combine(watch.Key, "A", "sub"), sub);

        Feed(hub, watch, (4, @"A\x"), (5, @"B\x"));
        hub.FeedForTests(watch, NotifyRecords(false, (4, @"A\y", 0, 0)), details: false);
        hub.FeedForTests(watch, NotifyRecords(false, (5, @"A\z", 0, 0)), details: false);
        time.Advance(150);
        DrainHub(hub, sink);
        Check("a rename across folders, or split across buffers, is no pair",
            sink.For(a).Single().Renames.IsEmpty && sink.For(b).Single().Renames.IsEmpty);

        sink.Clear();
        Feed(hub, watch, (2, @"A\sub"));
        time.Advance(150);
        DrainHub(hub, sink);
        Check("a folder removed is gone to its own registration and structural to its parent's",
            sink.For(sub).Single().Kinds == ChangeKinds.Gone && sink.For(a).Single().Kinds == ChangeKinds.Structural);

        sink.Clear();
        var detailed = new List<(int, string, long, uint)>();
        for (var index = 0; index < 3; index++)
        {
            detailed.Add((3, $@"A\file{index}.bin", 10 + index, 0x20));
            detailed.Add((3, $@"A\FILE{index}.bin", 100 + index, 0x20));
        }

        hub.FeedForTests(watch, NotifyRecords(true, [.. detailed]), details: true);
        time.Advance(1000);
        DrainHub(hub, sink);
        var files = sink.For(a).Single().Files.Span.ToArray();
        Check($"a file changed twice is listed once, with its last size ({string.Join(", ", files.Select(file => $"{file.Name}={file.Length}"))})",
            files.Length == 3 && files.Select(file => file.Length).SequenceEqual([100L, 101, 102]) && files[0].Name == "file0.bin"
            && files[0].ModifiedTicks == new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc).Ticks);

        sink.Clear();
        detailed.Clear();
        for (var index = 0; index < ChangeHub.MaximumDetails + 1; index++)
        {
            detailed.Add((3, $@"A\many{index}.bin", index, 0x20));
        }

        hub.FeedForTests(watch, NotifyRecords(true, [.. detailed]), details: true);
        time.Advance(1000);
        DrainHub(hub, sink);
        Check($"past {ChangeHub.MaximumDetails} changed files the list is given up, and the folder must be read",
            sink.For(a).Single().Files.IsEmpty && sink.For(a).Single().Kinds == ChangeKinds.Content);

        sink.Clear();
        hub.FeedForTests(watch, NotifyRecords(true, (3, @"A\sub2", 0, 0x10)), details: true);
        time.Advance(1000);
        DrainHub(hub, sink);
        Check("a sub-folder's own date moving is told as such", sink.For(a).Single().Kinds == ChangeKinds.DirDate && sink.For(a).Single().Files.IsEmpty);
    }

    /// <summary>
    /// What a record costs on the watcher's thread: parsed and looked up
    /// among a thousand registered folders, missed, and dropped - and the same
    /// through a share known by two spellings, which looks up each.
    /// </summary>
    private static void HubRecordCostChecks(string root)
    {
        var (hub, _, watch, _) = FedHub(Path.Combine(root, "cost"));
        using var __ = hub;
        for (var index = 0; index < 1000; index++)
        {
            hub.Register(ChangeConsumer.Nested, Path.Combine(watch.Key, "Loaded", $"folder{index}"), new object());
        }

        var noise = new (int, string, long, uint)[2000];
        for (var index = 0; index < noise.Length; index++)
        {
            noise[index] = (index % 2 == 0 ? 1 : 2, $@"AppData\Local\Temp\cache{index % 37}\entry-{index:D5}.tmp", 0, 0x20);
        }

        var buffer = NotifyRecords(true, noise);
        hub.FeedForTests(watch, buffer, details: true);
        var runs = new List<double>();
        var allocated = 0L;
        for (var run = 0; run < 15; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            hub.FeedForTests(watch, buffer, details: true);
            var elapsed = Stopwatch.GetElapsedTime(start).TotalNanoseconds / noise.Length;
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            runs.Add(elapsed);
        }

        runs.Sort();
        var median = runs[runs.Count / 2];
        Check($"a noise record costs {median:F0} ns to parse, look up and drop, median of 15 x 2,000 (budget {RecordBudgetNanoseconds:F0})",
            median < RecordBudgetNanoseconds && hub.PendingCount == 0);
        Check($"and allocates {allocated * 1000.0 / (15 * noise.Length):F1} B per 1,000 records (budget 1 KB)", allocated * 1000.0 / (15 * noise.Length) <= 1024);

        // A root with two spellings looks each one up.
        var volumes = new VolumeResolver(letter => letter == 'N'
            ? new VolumeResolver.Letter(WatchNative.DriveRemote, @"\\server\costshare", null)
            : new VolumeResolver.Letter(WatchNative.DriveNoRootDirectory, null, null));
        using var shareHub = new ChangeHub(new ManualTime(), volumes);
        var share = shareHub.RootFor(@"N:\")!;
        for (var index = 0; index < 1000; index++)
        {
            shareHub.Register(ChangeConsumer.Nested, $@"N:\Loaded\folder{index}", new object());
        }

        runs.Clear();
        shareHub.FeedForTests(share, buffer, details: true);
        for (var run = 0; run < 15; run++)
        {
            var start = Stopwatch.GetTimestamp();
            shareHub.FeedForTests(share, buffer, details: true);
            runs.Add(Stopwatch.GetElapsedTime(start).TotalNanoseconds / noise.Length);
        }

        runs.Sort();
        Check($"through a share with two spellings, {runs[runs.Count / 2]:F0} ns (budget {2 * RecordBudgetNanoseconds:F0})",
            runs[runs.Count / 2] < 2 * RecordBudgetNanoseconds && shareHub.PendingCount == 0);
    }

    /// <summary>A real folder, a file made every 100 ms of fake time: handed on 500 ms after the first, however steady.</summary>
    private static void HubRealStreamChecks(string root)
    {
        var folder = Path.Combine(root, "real-stream");
        Directory.CreateDirectory(Path.Combine(folder, "W"));
        var time = new ManualTime();
        using var hub = new ChangeHub(time);
        var sink = new WatchSink { Clock = () => time.Milliseconds };
        var watch = hub.AddRootForTests(folder);
        WaitFor(() => watch.State == WatchState.Armed);
        var target = new object();
        hub.Register(ChangeConsumer.Nested, Path.Combine(folder, "W"), target);

        var start = time.Milliseconds;
        var delivered = -1d;
        for (var step = 0; step < 10 && delivered < 0; step++)
        {
            var before = watch.Records!.Records;
            File.Create(Path.Combine(folder, "W", $"file{step}.txt")).Dispose();
            WaitForRecords(watch, before + 1, 2000);
            for (var tick = 0; tick < 10 && delivered < 0; tick++)
            {
                time.Advance(10);
                DrainHub(hub, sink);
                if (sink.Changes.Count > 0)
                {
                    delivered = time.Milliseconds - start;
                }
            }
        }

        Check($"files made every 100 ms in a real folder are handed on 500 ms after the first ({delivered} ms)", delivered == 500);
    }
}
