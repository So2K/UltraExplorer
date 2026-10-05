using System.Collections.Concurrent;
using System.Windows.Threading;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;

namespace ViewAllSmoke;

/// <summary>Independent logical-volume lanes and explicit transient-error
/// retry over controlled in-memory listings. No real volume/UNC is read and
/// no foreground window is created.</summary>
internal static partial class Program
{
    private static Task ReadLaneIntegrationChecks()
    {
        RunOnSta("read lane integration", async () =>
        {
            await IndependentReadLaneChecksAsync();
            await ExplicitReadRetryChecksAsync();
        });
        return Task.CompletedTask;
    }

    private static async Task IndependentReadLaneChecksAsync()
    {
        Section("read lanes: independent volumes, aliases, network caps and disposal");
        var letters = VolumeKinds.ResolveLetter;
        VolumeKinds.ResolveLetter = _ => null;
        using var release = new ManualResetEventSlim();
        var started = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var roots = new[] { @"C:\", @"D:\", @"S:\", @"R:\", @"\\owned-lane-fixture\share" };
        var rootKeys = new HashSet<string>(roots.Select(path => path.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
        var names = Enumerable.Range(0, NestedTree.LocalReadSlots + 2).Select(index => "hold-" + index).ToArray();
        var tree = new NestedTree((path, token) =>
        {
            if (rootKeys.Contains(path.TrimEnd('\\')))
                return new NestedListing(names.Select(name => new NestedEntry(name, false, false)).ToArray(), 0, 0, false);
            started.AddOrUpdate(path, 1, (_, count) => count + 1);
            release.Wait(token);
            return new NestedListing([], 0, 0, false);
        });
        var metadata = new Dictionary<char, int>();
        var devices = new Dictionary<char, int>();
        tree.LocalReadVolumeFor = letter =>
        {
            metadata[letter] = metadata.GetValueOrDefault(letter) + 1;
            return new VolumeResolver.Letter(WatchNative.DriveFixed, null, letter == 'S' ? @"C:\owned-subst" : null);
        };
        tree.LocalReadDeviceFor = letter =>
        {
            devices[letter] = devices.GetValueOrDefault(letter) + 1;
            return letter is 'C' or 'R' ? "owned-volume-a" : "owned-volume-b";
        };
        try
        {
            tree.SetRoots(roots.Select(path => new NestedRoot(path, path, NestedFolderKind.Drive)).ToArray());
            foreach (var root in roots) await tree.LoadAsync(tree.Find(root)!);
            var a = tree.Find(@"C:\")!;
            var b = tree.Find(@"D:\")!;
            var subst = tree.Find(@"S:\")!;
            var sameDevice = tree.Find(@"R:\")!;
            var network = tree.Find(@"\\owned-lane-fixture\share")!;
            Check("independent local volume identities have separate lanes", tree.ReadLaneForChecks(a) != tree.ReadLaneForChecks(b));
            Check("SUBST and another letter for the same device share the local lane",
                tree.ReadLaneForChecks(a) == tree.ReadLaneForChecks(subst) && tree.ReadLaneForChecks(a) == tree.ReadLaneForChecks(sameDevice));
            var beforeMetadata = metadata.Values.Sum();
            var beforeDevices = devices.Values.Sum();
            for (var index = 0; index < 100; index++)
            {
                _ = tree.ReadLaneForChecks(a.AllChildren[index % a.AllChildren.Length]);
                _ = tree.ReadLaneForChecks(subst.AllChildren[index % subst.AllChildren.Length]);
            }
            Check("repeated frame/queue lookups reuse letter metadata without further system questions",
                metadata.Values.Sum() == beforeMetadata && devices.Values.Sum() == beforeDevices);

            tree.BeginFrame();
            foreach (var folder in a.AllChildren.Take(NestedTree.LocalReadSlots)) tree.Request(folder, 200);
            Check("one local volume consumes its eight slots", tree.ReadLanesForChecks.LocalReads == NestedTree.LocalReadSlots);
            using var canceled = new CancellationTokenSource();
            var aliasWait = tree.LoadAsync(subst.AllChildren[0], canceled.Token);
            tree.Request(sameDevice.AllChildren[0], 1000);
            Check("aliases cannot bypass a saturated volume cap", subst.AllChildren[0].LoadState == NestedLoadState.Queued
                && sameDevice.AllChildren[0].LoadState == NestedLoadState.Queued && tree.ReadLanesForChecks.LocalReads == NestedTree.LocalReadSlots);
            foreach (var folder in b.AllChildren.Take(NestedTree.LocalReadSlots)) tree.Request(folder, 20);
            Check("the second volume starts all its slots while the first is blocked",
                b.AllChildren.Take(NestedTree.LocalReadSlots).All(folder => folder.LoadState == NestedLoadState.Loading)
                && tree.ReadLanesForChecks == (NestedTree.LocalReadSlots * 2, 0, 2));
            tree.Request(b.AllChildren[NestedTree.LocalReadSlots], 5000);
            Check("the second volume still enforces its own eight-slot cap", b.AllChildren[NestedTree.LocalReadSlots].LoadState == NestedLoadState.Queued);
            tree.Request(network.AllChildren[0], 10);
            tree.Request(network.AllChildren[1], 100);
            Check("network lanes retain their independent one-read limit",
                network.AllChildren[0].LoadState == NestedLoadState.Loading && network.AllChildren[1].LoadState == NestedLoadState.Queued
                && tree.ReadLanesForChecks.NetworkReads == NestedTree.NetworkSlotsPerShare);
            canceled.Cancel();
            try { await aliasWait; Check("the canceled alias waiter reports cancellation", false); }
            catch (OperationCanceledException) { Check("the canceled alias waiter reports cancellation", true); }
            Check("canceling a queued alias removes only that request", subst.AllChildren[0].LoadState == NestedLoadState.NotLoaded);
            release.Set();
            await WaitUntil(() => tree.PendingCount == 0, 10_000);
            Check("completion frees the originally acquired volume/network slots without leaking counts",
                tree.ReadLanesForChecks == (0, 0, 0) && tree.PendingCount == 0);
            Check("the independently started second-volume listings were applied", b.AllChildren.Take(NestedTree.LocalReadSlots).All(folder => folder.IsLoaded));
            Check("the canceled queued alias never reached the in-memory reader", !started.ContainsKey(subst.AllChildren[0].FullPath));

            // Reenter a controlled physical wait, then dispose. The reader's
            // lifetime cancellation releases its slot and discards its result.
            release.Reset();
            var later = a.AllChildren.Last();
            using var waiterCancel = new CancellationTokenSource();
            var wait = tree.LoadAsync(later, waiterCancel.Token);
            tree.Dispose();
            try { await wait; Check("disposal resolves pending explicit work", true); }
            catch (OperationCanceledException) { Check("disposal resolves pending explicit work", true); }
            release.Set();
            await WaitUntil(() => tree.ReadLanesForChecks.LocalReads == 0 && tree.ReadLanesForChecks.NetworkReads == 0, 5000);
            Check("disposed-tree workers release all frozen lane counters", tree.ReadLanesForChecks == (0, 0, 0));
        }
        finally { release.Set(); tree.Dispose(); VolumeKinds.ResolveLetter = letters; }
    }

    private static async Task ExplicitReadRetryChecksAsync()
    {
        Section("read lanes: an explicit transient failure retries before automatic backoff");
        var transientReads = 0;
        var permanentReads = 0;
        using var tree = new NestedTree((path, _) =>
        {
            if (path.EndsWith("retry", StringComparison.OrdinalIgnoreCase))
                return Interlocked.Increment(ref transientReads) == 1
                    ? NestedListing.Failed("owned transient failure") with { IsRetryable = true }
                    : new NestedListing([], 0, 0, false);
            if (path.EndsWith("denied", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref permanentReads);
                return NestedListing.Failed("owned permanent denial");
            }
            return new NestedListing([new NestedEntry("retry", false, false), new NestedEntry("denied", false, false)], 0, 0, false);
        })
        {
            LocalReadVolumeFor = _ => new VolumeResolver.Letter(WatchNative.DriveFixed, null, null),
            LocalReadDeviceFor = _ => "owned-retry-volume"
        };
        tree.SetRoots([new NestedRoot(@"Q:\", "owned", NestedFolderKind.Drive)]);
        await tree.LoadAsync(tree.Find(@"Q:\")!);
        var retry = tree.Find(@"Q:\retry")!;
        var denied = tree.Find(@"Q:\denied")!;
        await tree.LoadAsync(retry);
        Check("the first transient result records a retryable failure", transientReads == 1 && retry.LoadState == NestedLoadState.Failed && retry.IsRetryable);
        tree.BeginFrame();
        tree.Request(retry, 100);
        Check("ordinary canvas requests retain the twenty-second backoff", transientReads == 1 && retry.LoadState == NestedLoadState.Failed);
        await tree.LoadAsync(retry);
        Check("explicit navigation retries the transient error immediately without a watch/refresh event",
            transientReads == 2 && retry.IsLoaded && !retry.IsRetryable && retry.ErrorMessage.Length == 0);
        await tree.LoadAsync(denied);
        await tree.LoadAsync(denied);
        Check("a permanent failure is still not retried endlessly by explicit loads", permanentReads == 1 && denied.LoadState == NestedLoadState.Failed && !denied.IsRetryable);
    }
}
