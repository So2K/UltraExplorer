using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using UltraExplorer;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private const string ExplorerFollowupOnlyVariable = "ULTRAEXPLORER_EXPLORER_FOLLOWUP_ONLY";

    // Reuse the real isolated-desktop child and its broker/state isolation.
    // This group never starts a production listener or drives desktop input.
    private static async Task ExplorerIntegrationFollowupChecks()
    {
        var previous = Environment.GetEnvironmentVariable(ExplorerFollowupOnlyVariable);
        Environment.SetEnvironmentVariable(ExplorerFollowupOnlyVariable, "1");
        try { await ExplorerReview2Checks(); }
        finally { Environment.SetEnvironmentVariable(ExplorerFollowupOnlyVariable, previous); }
    }

    private static ShellFolderSnapshot ExplorerFollowupSnapshot(string root, long generation = 1) =>
        new(0x7FF0_1071, 0x7FF0_1073, 0x7FF0_1075, 4,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 6, root,
            new byte[] { 2, 0, 0, 0 }, Array.AsReadOnly(Array.Empty<string>()), null, generation);

    private static FolderRouteReceipt ExplorerFollowupReady(FolderInvocation invocation, bool accepted = true, bool ready = true) =>
        new(Guid.NewGuid(), accepted, ready, 1, 1, 1, invocation.DestinationId, invocation.FolderPath, invocation.SelectedPaths);

    private static async Task ExplorerIntegrationFollowupOnStaAsync(string root)
    {
        Section("Explorer follow-up: rejected/faulted/canceled promises leave the source open and retire only their destination");
        foreach (var kind in new[] { "rejected", "not ready", "fault", "canceled" })
        {
            var opens = new ConcurrentQueue<FolderInvocation>();
            var discards = new ConcurrentQueue<FolderInvocation>();
            var closes = 0;
            var actions = new ExplorerTransferActions(
                () => true,
                (_, _) => Task.FromResult(true),
                async (invocation, cancellation) =>
                {
                    opens.Enqueue(invocation);
                    if (kind == "fault") throw new IOException("owned destination fixture rejected its request");
                    if (kind == "canceled") await Task.Delay(Timeout.Infinite, cancellation);
                    return ExplorerFollowupReady(invocation, accepted: kind != "rejected", ready: kind != "not ready");
                },
                (_, _, _) => Task.FromResult(true),
                _ => true,
                _ => { Interlocked.Increment(ref closes); return true; },
                Timeout: TimeSpan.FromMilliseconds(150),
                DiscardDestination: invocation => { discards.Enqueue(invocation); return Task.CompletedTask; });
            using var coordinator = new ExplorerReplacementCoordinator(actions);
            await coordinator.TransferForChecksAsync(ExplorerFollowupSnapshot(root)).WaitAsync(TimeSpan.FromSeconds(3));
            await LiveWait(() => !discards.IsEmpty, 3000);
            await Task.Delay(100);
            Check($"{kind}: no source closure; exactly one discard for the requested destination",
                closes == 0 && opens.Count == 1 && discards.Count == 1
                && opens.TryPeek(out var opened) && discards.TryPeek(out var discarded)
                && opened.DestinationId == discarded.DestinationId);
        }

        Section("Explorer follow-up: the debounce grace has epoch and GUID boundaries, not merely a longer timeout (J069/J070)");
        await ExplorerFollowupGraceReuseAsync(root);
        await ExplorerFollowupRetirementAsync(root);
        foreach (var kind in new[] { "mode off", "kept native", "validation failed" })
            await ExplorerFollowupEarlyRetryAsync(root, kind);
        await ExplorerFollowupReusedIdentityAsync(root, newProcess: true);
        await ExplorerFollowupReusedIdentityAsync(root, newProcess: false);

        ExplorerReview2TrailingNameChecks(root);
        ExplorerReview2Mode(false);
        var folder = Path.Combine(root, "real broker follow-up");
        var other = Path.Combine(root, "off during navigation");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(other);
        FolderCommandLine.TryOpenFolder(folder, out var open, out _);
        var main = new MainWindow { ShowActivated = false };
        ExplorerLaunchRouter.Attach(main);
        main.Show();
        Check("the owned broker window opens its generated folder", await main.ApplyFolderInvocationAsync(open).WaitAsync(TimeSpan.FromSeconds(60)));

        var handoff = open with { OriginIsShell = true, DestinationId = Guid.NewGuid() };
        main.FolderDestinationId = handoff.DestinationId;
        main.PrepareFolderInvocation(handoff);
        Check("a freshly prepared destination is untouched and unused", main.IsUntouchedHandoff && main.IsUnusedFolderRequest);
        main.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 0) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        Check("a wheel event makes the real destination user-owned", !main.IsUntouchedHandoff && !main.IsUnusedFolderRequest);
        main.PrepareFolderInvocation(handoff);
        main.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(main)!, Environment.TickCount, Key.None)
            { RoutedEvent = UIElement.PreviewKeyDownEvent });
        Check("a key event also makes the real destination user-owned", !main.IsUntouchedHandoff && !main.IsUnusedFolderRequest);

        Section("Explorer follow-up: mode OFF wins the final readiness boundary even inside the 100 ms read cache (J152)");
        ExplorerReview2Mode(true);
        var switchedOff = false;
        void OffAtLocation(object? _, EventArgs __)
        {
            if (main.CurrentFolderPath != other || switchedOff) return;
            switchedOff = true;
            ExplorerReview2Mode(false);
        }
        main.FolderLocationChanged += OffAtLocation;
        try
        {
            FolderCommandLine.TryOpenFolder(other, out var next, out _);
            var ready = await main.ApplyFolderInvocationAsync(next with { OriginIsShell = true }).WaitAsync(TimeSpan.FromSeconds(60));
            Check("a live folder-location event switches OFF, and the request never reports ready", switchedOff && !ready);
        }
        finally { main.FolderLocationChanged -= OffAtLocation; ExplorerReview2Mode(false); }

        Section("Explorer follow-up: an exact destination already closing is never selected for a new committed request (J069)");
        // Keep one other owned window listening while the tested destination
        // completes its real async close-time save and detaches from the host.
        var keeper = new MainWindow { ShowActivated = false };
        ExplorerLaunchRouter.Attach(keeper);
        keeper.Show();
        Check("another owned window keeps the broker alive during destination retirement",
            await keeper.ApplyFolderInvocationAsync(open).WaitAsync(TimeSpan.FromSeconds(60)));
        var oldHandle = new WindowInteropHelper(main).Handle.ToInt64();
        var destination = main.FolderDestinationId;
        main.Close();
        var closeStarted = main.IsFolderWindowClosing;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var routed = await ExplorerLaunchRouter.SendAsync(new(Guid.NewGuid(), open with { DestinationId = destination }, true), deadline.Token);
        var replacement = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(window => window.FolderDestinationId == destination && !window.IsFolderWindowClosing);
        Check("the committed request is ready in a distinct, live window, never the retiring HWND",
            closeStarted && routed.Accepted && routed.Ready && routed.Window != oldHandle
            && replacement is not null && !ReferenceEquals(replacement, main));
    }

    private static async Task ExplorerFollowupGraceReuseAsync(string root)
    {
        var opened = new ConcurrentQueue<FolderInvocation>();
        var discarded = 0;
        var closed = 0;
        var firstValidations = 0;
        var actions = new ExplorerTransferActions(
            () => true,
            (snapshot, _) => Task.FromResult(snapshot.Generation != 1 || Interlocked.Increment(ref firstValidations) == 1),
            (invocation, _) => { opened.Enqueue(invocation); return Task.FromResult<FolderRouteReceipt?>(ExplorerFollowupReady(invocation)); },
            (_, _, _) => Task.FromResult(true), _ => true,
            _ => { Interlocked.Increment(ref closed); return true; },
            DiscardDestination: _ => { Interlocked.Increment(ref discarded); return Task.CompletedTask; });
        using var coordinator = new ExplorerReplacementCoordinator(actions);
        await coordinator.TransferForChecksAsync(ExplorerFollowupSnapshot(root));
        await Task.Delay(300); // The actual observer's 250 ms stable-state debounce.
        var beforeRetry = discarded;
        await coordinator.TransferForChecksAsync(ExplorerFollowupSnapshot(root, 2));
        await Task.Delay(750); // Both old and new grace timers have had time to fire.
        var requests = opened.ToArray();
        Check("a debounced new state reuses its predecessor's GUID, then invalidates all stale discard timers",
            beforeRetry == 0 && requests.Length == 2 && requests[0].DestinationId == requests[1].DestinationId
            && closed == 1 && discarded == 0);
    }

    private static async Task ExplorerFollowupRetirementAsync(string root)
    {
        var opened = new ConcurrentQueue<FolderInvocation>();
        var discarded = new ConcurrentQueue<FolderInvocation>();
        var discarding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstValidations = 0;
        var closed = 0;
        var actions = new ExplorerTransferActions(
            () => true,
            (snapshot, _) => Task.FromResult(snapshot.Generation != 1 || Interlocked.Increment(ref firstValidations) == 1),
            (invocation, _) => { opened.Enqueue(invocation); return Task.FromResult<FolderRouteReceipt?>(ExplorerFollowupReady(invocation)); },
            (_, _, _) => Task.FromResult(true), _ => true,
            _ => { Interlocked.Increment(ref closed); return true; },
            DiscardDestination: async invocation => { discarded.Enqueue(invocation); discarding.TrySetResult(); await release.Task; });
        using var coordinator = new ExplorerReplacementCoordinator(actions);
        try
        {
            await coordinator.TransferForChecksAsync(ExplorerFollowupSnapshot(root));
            await discarding.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await coordinator.TransferForChecksAsync(ExplorerFollowupSnapshot(root, 2));
            var requests = opened.ToArray();
            Check("once discard begins, a new state gets a new GUID even while the old discard is still awaiting its broker",
                requests.Length == 2 && requests[0].DestinationId != requests[1].DestinationId
                && discarded.Count == 1 && discarded.TryPeek(out var old) && old.DestinationId == requests[0].DestinationId && closed == 1);
        }
        finally { release.TrySetResult(); }
    }

    private static async Task ExplorerFollowupEarlyRetryAsync(string root, string kind)
    {
        var enabled = true;
        var native = false;
        var opened = new ConcurrentQueue<FolderInvocation>();
        var discarded = new ConcurrentQueue<FolderInvocation>();
        var validations = 0;
        var closed = 0;
        var actions = new ExplorerTransferActions(
            () => enabled,
            (_, _) => Task.FromResult(Interlocked.Increment(ref validations) == 1),
            (invocation, _) => { opened.Enqueue(invocation); return Task.FromResult<FolderRouteReceipt?>(ExplorerFollowupReady(invocation)); },
            (_, _, _) => Task.FromResult(true), _ => true,
            _ => { Interlocked.Increment(ref closed); return true; },
            NativeSession: _ => native,
            DiscardDestination: invocation => { discarded.Enqueue(invocation); return Task.CompletedTask; });
        using var coordinator = new ExplorerReplacementCoordinator(actions);
        await coordinator.TransferForChecksAsync(ExplorerFollowupSnapshot(root));
        if (kind == "mode off") enabled = false;
        if (kind == "kept native") native = true;
        await coordinator.TransferForChecksAsync(ExplorerFollowupSnapshot(root, 2));
        await LiveWait(() => !discarded.IsEmpty, 3000);
        await Task.Delay(150);
        Check($"{kind} on the newer state still cleans up the earlier destination exactly once, without closing the source",
            opened.Count == 1 && discarded.Count == 1 && closed == 0
            && opened.TryPeek(out var old) && discarded.TryPeek(out var discard) && old.DestinationId == discard.DestinationId);
    }

    private sealed class ExplorerFollowupClaim(Func<bool> owns) : IExplorerTransferClaim
    {
        public bool OwnsSource => owns();
        public void Dispose() { }
    }

    private static async Task ExplorerFollowupReusedIdentityAsync(string root, bool newProcess)
    {
        Section($"Explorer follow-up: reused HWND with {(newProcess ? "a new PID" : "a new process lifetime")} and the same generation (J070)");
        var first = ExplorerFollowupSnapshot(root, 10);
        var sameIdentityHigherGeneration = first with { Generation = 99 };
        var reused = newProcess ? first with { SourceProcessId = first.SourceProcessId + 1u }
            : first with { SourceProcessStartUtc = first.SourceProcessStartUtc.AddSeconds(1) };
        var current = first;
        var opened = new ConcurrentQueue<FolderInvocation>();
        var closed = new ConcurrentQueue<ShellFolderSnapshot>();
        var discarded = new ConcurrentQueue<FolderInvocation>();
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleClaims = 0;
        static bool SameLifetime(ShellFolderSnapshot a, ShellFolderSnapshot b) => a.RootHwnd == b.RootHwnd
            && a.SourceProcessId == b.SourceProcessId && a.SourceProcessStartUtc == b.SourceProcessStartUtc;
        var actions = new ExplorerTransferActions(
            () => true,
            (_, _) => Task.FromResult(true),
            async (invocation, _) =>
            {
                opened.Enqueue(invocation);
                if (opened.Count == 1) { opening.TrySetResult(); await release.Task; }
                return ExplorerFollowupReady(invocation);
            },
            (_, _, _) => Task.FromResult(true), _ => true,
            snapshot => { closed.Enqueue(snapshot); return SameLifetime(snapshot, Volatile.Read(ref current)); },
            ClaimSource: snapshot => new ExplorerFollowupClaim(() =>
            {
                var owns = SameLifetime(snapshot, Volatile.Read(ref current));
                if (!owns) Interlocked.Increment(ref staleClaims);
                return owns;
            }),
            DiscardDestination: invocation => { discarded.Enqueue(invocation); return Task.CompletedTask; });
        using var coordinator = new ExplorerReplacementCoordinator(actions);
        var handoff = coordinator.TransferForChecksAsync(first);
        try
        {
            await opening.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await coordinator.TransferForChecksAsync(sameIdentityHigherGeneration);
            Volatile.Write(ref current, reused);
            await coordinator.TransferForChecksAsync(reused);
            release.TrySetResult();
            await handoff.WaitAsync(TimeSpan.FromSeconds(3));
            await LiveWait(() => !closed.IsEmpty && !discarded.IsEmpty, 3000);
            await Task.Delay(150);
            var requests = opened.ToArray();
            Check("the received new identity supersedes the old identity's larger generation and is not throttled for 30 s",
                requests.Length == 2 && requests[0].DestinationId != requests[1].DestinationId);
            Check("the old destination is discarded exactly once, independently of the reused HWND's new GUID",
                requests.Length == 2 && discarded.Count == 1 && discarded.TryPeek(out var old)
                && old.DestinationId == requests[0].DestinationId);
            Check("the old source claim loses ownership; only the exact new process identity reaches CloseSource",
                staleClaims > 0 && closed.Count == 1 && closed.TryPeek(out var allowed) && SameLifetime(allowed, reused)
                && allowed.Generation == reused.Generation);
        }
        finally { release.TrySetResult(); }
    }
}
