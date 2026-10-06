using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UltraExplorer.Picker.Integration;

[assembly: InternalsVisibleTo("ExplorerReplacementSmoke")]

namespace UltraExplorer.Services;

internal interface IExplorerTransferClaim : IDisposable { bool OwnsSource { get; } }
internal sealed record ExplorerTransferActions(
    Func<bool> Enabled,
    Func<ShellFolderSnapshot, CancellationToken, Task<bool>> ValidateSource,
    Func<FolderInvocation, CancellationToken, Task<FolderRouteReceipt?>> OpenDestination,
    Func<FolderRouteReceipt, FolderInvocation, CancellationToken, Task<bool>> ValidateDestination,
    Func<FolderRouteReceipt, bool> DestinationAlive,
    Func<ShellFolderSnapshot, bool> CloseSource,
    Func<ShellFolderSnapshot, IExplorerTransferClaim?>? ClaimSource = null,
    TimeSpan? Timeout = null,
    Func<ShellFolderSnapshot, bool>? NativeSession = null,
    Func<FolderInvocation, Task>? DiscardDestination = null);

/// <summary>Source frames remain usable through preparation. Each frame has its
/// own destination; closure requires an exact ready receipt, unchanged source
/// view and selection, integration still enabled, and a matching ownership cookie.</summary>
internal sealed class ExplorerReplacementCoordinator : IDisposable
{
    private readonly ExplorerWindowObserver? _observer;
    private readonly ExplorerTransferActions _actions;
    private readonly ConcurrentDictionary<nint, byte> _busy = new();
    /// <summary>The newest state of a frame that arrived while it was being handed
    /// off. The observer delivers each state once, so it is kept, not dropped.</summary>
    private readonly ConcurrentDictionary<nint, ShellFolderSnapshot> _newer = new();
    private readonly ConcurrentDictionary<(nint Root, uint Process, DateTime Start), (long Generation, long Until)> _retry = new();
    private readonly object _destinationGate = new();
    private readonly Dictionary<(nint Root, uint Process, DateTime Start), DestinationState> _destinations = [];
    private sealed class DestinationState
    {
        internal readonly Guid Id = Guid.NewGuid();
        internal long Epoch;
        internal FolderInvocation? Opened;
    }
    private readonly CancellationTokenSource _stop = new();
    private volatile bool _disposed;

    internal ExplorerReplacementCoordinator(ExplorerObserverScope? testScope = null)
    {
        _observer = new(testScope);
        _actions = new(
            () => DialogIntegrationStore.Read().Enabled,
            (snapshot, token) => _observer.ValidateSnapshotAsync(snapshot).WaitAsync(TimeSpan.FromSeconds(3), token),
            ExplorerLaunchRouter.OpenAndWaitAsync,
            ExplorerLaunchRouter.ValidateReadyAsync,
            DestinationAlive,
            CloseSource,
            SourceClaim.TryCreate,
            NativeSession: _observer.IsNativeSession,
            DiscardDestination: ExplorerLaunchRouter.DiscardIfUntouchedAsync);
    }
    internal ExplorerReplacementCoordinator(ExplorerTransferActions actions) => _actions = actions;

    internal void Start()
    {
        if (_observer is null) throw new InvalidOperationException("A fixture coordinator is driven by its own snapshots.");
        _observer.Start(snapshot => _ = TransferAsync(snapshot));
    }
    private bool Enabled() => !_disposed && !_stop.IsCancellationRequested && _actions.Enabled();
    internal Task TransferForChecksAsync(ShellFolderSnapshot snapshot) => TransferAsync(snapshot);
    private static (nint Root, uint Process, DateTime Start) SourceKey(ShellFolderSnapshot snapshot)
        => (snapshot.RootHwnd, snapshot.SourceProcessId, snapshot.SourceProcessStartUtc);

    private async Task TransferAsync(ShellFolderSnapshot snapshot)
    {
        var key = SourceKey(snapshot);
        if (!Enabled() || _actions.NativeSession?.Invoke(snapshot) == true || _retry.TryGetValue(key, out var retry)
            && retry.Generation == snapshot.Generation && Environment.TickCount64 < retry.Until)
        {
            ScheduleKnownDestinationCleanup(key);
            return;
        }
        if (!_busy.TryAdd(snapshot.RootHwnd, 0))
        {
            // 'explorer /select' shows the folder first and selects a moment
            // later. The handoff under way fails its final check against that
            // selection; this state then runs, against the same destination.
            _newer.AddOrUpdate(snapshot.RootHwnd, snapshot, (_, held) =>
                SourceKey(held) == key && held.Generation > snapshot.Generation ? held : snapshot);
            if (!_busy.ContainsKey(snapshot.RootHwnd) && _newer.TryRemove(snapshot.RootHwnd, out var missed)) _ = TransferAsync(missed);
            return;
        }
        DestinationState destinationState;
        long epoch;
        FolderInvocation? opened;
        lock (_destinationGate)
        {
            if (!_destinations.TryGetValue(key, out destinationState!))
                _destinations.Add(key, destinationState = new DestinationState());
            epoch = ++destinationState.Epoch;
            opened = destinationState.Opened;
        }
        var handedOff = false;
        // A queued retry may fail before asking for a destination itself.
        // It still owns cleanup of the earlier attempt's opened window.
        IExplorerTransferClaim? claim = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        deadline.CancelAfter(_actions.Timeout ?? TimeSpan.FromSeconds(20));
        bool Current() => Enabled() && !deadline.IsCancellationRequested && _actions.NativeSession?.Invoke(snapshot) != true;
        try
        {
            if (!await _actions.ValidateSource(snapshot, deadline.Token).WaitAsync(deadline.Token) || !Current()) return;
            claim = _actions.ClaimSource is null ? new FixtureClaim() : _actions.ClaimSource(snapshot);
            if (claim is null || !claim.OwnsSource || !Current()) return;
            var selected = snapshot.SelectedPaths.ToList();
            if (snapshot.FocusedPath is { } focus && selected.Remove(focus)) selected.Insert(0, focus);
            var destination = destinationState.Id;
            var invocation = selected.Count > 0
                ? new FolderInvocation(FolderInvocationKind.Reveal, snapshot.FolderPath, selected, true, destination)
                : new FolderInvocation(FolderInvocationKind.OpenFolder, snapshot.FolderPath, [], true, destination);

            opened = invocation;
            lock (_destinationGate) destinationState.Opened = invocation;
            var receipt = await _actions.OpenDestination(invocation, deadline.Token).WaitAsync(deadline.Token);
            if (!Current() || !claim.OwnsSource || receipt is null
                || !ExplorerLaunchRouter.ReceiptMatches(receipt, invocation) || !_actions.DestinationAlive(receipt)) return;
            if (!await _actions.ValidateDestination(receipt, invocation, deadline.Token).WaitAsync(deadline.Token)
                || !Current() || !claim.OwnsSource || !_actions.DestinationAlive(receipt)) return;
            // Last awaited operation re-reads the native tab, PIDL, complete
            // selection, focus and idle state. OFF during that wait still wins.
            if (!await _actions.ValidateSource(snapshot, deadline.Token).WaitAsync(deadline.Token)
                || !Current() || !claim.OwnsSource || !_actions.DestinationAlive(receipt)) return;
            if (Current() && claim.OwnsSource && _actions.CloseSource(snapshot))
            {
                handedOff = true;
                lock (_destinationGate) destinationState.Opened = null;
                DialogIntegrationStore.Log("An Explorer folder and its exact selection were opened through UltraExplorer.");
                var closed = Stopwatch.StartNew();
                while (SourceIdentityMatches(snapshot) && closed.ElapsedMilliseconds < 1000)
                    await Task.Delay(20, deadline.Token);
                if (!SourceIdentityMatches(snapshot))
                {
                    lock (_destinationGate)
                        if (_destinations.TryGetValue(key, out var held) && ReferenceEquals(held, destinationState))
                            _destinations.Remove(key);
                }
            }
        }
        catch (Exception ex) when (Failure(ex)) { if (!_disposed) DialogIntegrationStore.Log("A folder window remains with Windows", ex); }
        finally
        {
            claim?.Dispose();
            _busy.TryRemove(snapshot.RootHwnd, out _);
            _retry[key] = (snapshot.Generation, Environment.TickCount64 + 30000);
            var retried = false;
            if (_newer.TryRemove(snapshot.RootHwnd, out var newer)
                && (SourceKey(newer) != key || !handedOff && newer.Generation != snapshot.Generation))
            {
                // Generations are comparable only within a process lifetime.
                // A reused HWND starts a separate destination and must not
                // inherit either the old throttle or cleanup responsibility.
                retried = SourceKey(newer) == key;
                _ = TransferAsync(newer);
            }

            // Given up once its window was asked for: the Explorer frame stays
            // (the user went on in it, closed it, or it never settled), and the
            // window opened for it would be a second, unasked-for one beside
            // it.  It is closed - unless the user has started to use it.  A
            // newer state of the frame going to the same window keeps it.
            if (opened is not null && !handedOff && !retried)
                _ = DiscardAfterGraceAsync(key, destinationState, epoch, opened);
        }
    }

    private void ScheduleKnownDestinationCleanup((nint Root, uint Process, DateTime Start) key)
    {
        lock (_destinationGate)
            if (_destinations.TryGetValue(key, out var state) && state.Opened is { } opened)
                _ = DiscardAfterGraceAsync(key, state, state.Epoch, opened);
    }

    /// <summary>
    /// The observer debounces new source state for 250 ms. Keep an abandoned
    /// destination through that gap; a newer attempt advances its epoch and
    /// cancels cleanup. Retirement and GUID removal are atomic, so a request
    /// arriving after discard begins never reuses a closing destination.
    /// </summary>
    private async Task DiscardAfterGraceAsync((nint Root, uint Process, DateTime Start) key,
        DestinationState state, long epoch, FolderInvocation opened)
    {
        if (_actions.DiscardDestination is not { } discard) return;
        await Task.Delay(600);
        lock (_destinationGate)
        {
            if (!_destinations.TryGetValue(key, out var current) || !ReferenceEquals(current, state)
                || state.Epoch != epoch || state.Opened is null || _busy.ContainsKey(key.Root)) return;
            _destinations.Remove(key);
        }
        await DiscardAsync(discard, opened);
    }

    private static async Task DiscardAsync(Func<FolderInvocation, Task> discard, FolderInvocation opened)
    {
        try { await discard(opened); }
        catch (Exception ex) when (Failure(ex)) { DialogIntegrationStore.Log("A window opened for an abandoned handoff could not be closed", ex); }
    }
    private sealed class FixtureClaim : IExplorerTransferClaim { public bool OwnsSource => true; public void Dispose() { } }
    private static bool Failure(Exception ex) => ex is IOException or TimeoutException or OperationCanceledException
        or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException;

    private static bool DestinationAlive(FolderRouteReceipt receipt)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)receipt.Process));
            return !process.HasExited && process.StartTime.ToUniversalTime().ToFileTimeUtc() == receipt.Started
                && (receipt.Process == Environment.ProcessId || string.Equals(process.MainModule?.FileName,
                    DialogSelfProcess.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                && DialogNative.IsWindow((nint)receipt.Window) && DialogNative.ProcessId((nint)receipt.Window) == receipt.Process;
        }
        catch (Exception ex) when (Failure(ex)) { return false; }
    }
    private static bool SourceIdentityMatches(ShellFolderSnapshot snapshot)
    {
        try
        {
            var thread = ExplorerWindowInterop.GetWindowThreadProcessId(snapshot.RootHwnd, out var processId);
            if (!DialogNative.IsWindow(snapshot.RootHwnd) || processId != snapshot.SourceProcessId || thread != snapshot.SourceThreadId) return false;
            using var process = Process.GetProcessById(checked((int)processId));
            return !process.HasExited && process.StartTime.ToUniversalTime() == snapshot.SourceProcessStartUtc;
        }
        catch (Exception ex) when (Failure(ex)) { return false; }
    }
    private static bool CloseSource(ShellFolderSnapshot snapshot) =>
        DialogIntegrationStore.Read().Enabled && SourceIdentityMatches(snapshot)
        && ExplorerWindowObserver.SourceIsIdle(snapshot.RootHwnd, snapshot.ViewHwnd, snapshot.SourceThreadId)
        && PostMessage(snapshot.RootHwnd, 0x0010, 0, 0);

    private sealed class SourceClaim(ShellFolderSnapshot snapshot, nint cookie) : IExplorerTransferClaim
    {
        private static string PropertyName => "UltraExplorer.FolderTransfer." + DialogIntegrationStore.InstanceKey;
        public bool OwnsSource => SourceIdentityMatches(snapshot) && GetProp(snapshot.RootHwnd, PropertyName) == cookie;
        internal static IExplorerTransferClaim? TryCreate(ShellFolderSnapshot snapshot)
        {
            if (!SourceIdentityMatches(snapshot) || GetProp(snapshot.RootHwnd, PropertyName) != 0) return null;
            var value = (nint)(BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8)) | 1);
            if (!SetProp(snapshot.RootHwnd, PropertyName, value)) return null;
            var claim = new SourceClaim(snapshot, value);
            if (claim.OwnsSource) return claim;
            claim.Dispose(); return null;
        }
        public void Dispose() { if (OwnsSource) RemoveProp(snapshot.RootHwnd, PropertyName); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _stop.Cancel(); _observer?.Dispose();
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetProp(nint window, string name, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint RemoveProp(nint window, string name);
}
