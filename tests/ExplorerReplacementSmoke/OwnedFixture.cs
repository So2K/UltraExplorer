using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using UltraExplorer.Services;

namespace ExplorerReplacementSmoke;

/// <summary>All native windows and pipes belong to this fixture process. No
/// Explorer window is created, looked up, hidden, moved, selected or closed.</summary>
internal sealed class OwnedFixture : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<Dispatcher> _dispatcherReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<nint, byte> _windows = new();
    private readonly Dictionary<Guid, nint> _destinations = [];
    private readonly Dictionary<Guid, FolderInvocation> _destinationStates = [];
    private readonly Task _server;
    private readonly string _pipe = ExplorerLaunchRouter.PipeName;
    private readonly long _started = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToFileTimeUtc();
    private const string CookieProperty = "UltraExplorer.OwnedTransferFixture.Cookie";

    internal string Folder { get; } = Path.Combine(Path.GetTempPath(), "UltraExplorerTransferFixture", Guid.NewGuid().ToString("N"));
    internal bool Enabled = true;
    internal bool PauseReady;
    internal bool PauseOffer;
    internal bool PauseFinalSourceValidation;
    internal bool PauseDestinationValidation;
    internal int ValidationCalls;
    internal int DestinationValidationCalls;
    internal int OpenCalls;
    internal int CloseCalls;
    internal int ClaimDisposals;
    internal int ServerRequests;
    internal int DestinationCreations;
    internal bool ClaimsCleaned = true;
    internal ShellFolderSnapshot Current = null!;
    internal FolderInvocation? LastInvocation;
    internal FolderRouteReceipt? LastReceipt;
    internal Func<FolderRouteReceipt, FolderRouteReceipt>? MutateReceipt;
    internal Func<bool>? ExtraDestinationAlive;
    internal TaskCompletionSource ReadyRequested { get; } = NewSignal();
    internal TaskCompletionSource OfferRequested { get; } = NewSignal();
    internal TaskCompletionSource ReleaseOffer { get; } = NewSignal();
    internal TaskCompletionSource ReleaseReady { get; } = NewSignal();
    internal TaskCompletionSource FinalSourceValidationRequested { get; } = NewSignal();
    internal TaskCompletionSource ReleaseFinalSourceValidation { get; } = NewSignal();
    internal TaskCompletionSource DestinationValidationRequested { get; } = NewSignal();
    internal TaskCompletionSource ReleaseDestinationValidation { get; } = NewSignal();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static async Task<OwnedFixture> CreateAsync(bool selected = true)
    {
        var fixture = new OwnedFixture();
        Directory.CreateDirectory(fixture.Folder);
        File.WriteAllText(Path.Combine(fixture.Folder, "a.txt"), "a");
        File.WriteAllText(Path.Combine(fixture.Folder, "b.txt"), "b");
        var dispatcher = await fixture._dispatcherReady.Task;
        var identity = await dispatcher.InvokeAsync(() =>
        {
            var root = fixture.CreateOwnWindow(0);
            var browser = fixture.CreateOwnWindow(root);
            var view = fixture.CreateOwnWindow(browser);
            var thread = Native.GetWindowThreadProcessId(root, out var process);
            return (root, browser, view, thread, process);
        });
        fixture.Current = new(identity.root, identity.browser, identity.view, identity.process,
            DateTime.FromFileTimeUtc(fixture._started), identity.thread, fixture.Folder,
            new byte[] { 2, 0, 0, 0 }, selected ? new[] { Path.Combine(fixture.Folder, "a.txt"), Path.Combine(fixture.Folder, "b.txt") } : [],
            selected ? Path.Combine(fixture.Folder, "b.txt") : null, 1);
        return fixture;
    }

    private OwnedFixture()
    {
        _thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _dispatcherReady.SetResult(dispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Owned transfer fixture windows" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _server = Task.Run(ServeAsync);
    }

    internal ExplorerTransferActions Actions(TimeSpan? timeout = null) => new(
        () => Enabled,
        ValidateSourceAsync,
        OpenDestinationAsync,
        ValidateDestinationAsync,
        DestinationAlive,
        CloseSource,
        snapshot => new OwnClaim(this, snapshot),
        timeout ?? TimeSpan.FromSeconds(3));

    private async Task<bool> ValidateSourceAsync(ShellFolderSnapshot snapshot, CancellationToken cancellation)
    {
        var call = Interlocked.Increment(ref ValidationCalls);
        if (PauseFinalSourceValidation && call >= 2)
        {
            FinalSourceValidationRequested.TrySetResult();
            await ReleaseFinalSourceValidation.Task.WaitAsync(cancellation);
        }
        cancellation.ThrowIfCancellationRequested();
        return Current.Generation == snapshot.Generation && Current.SameContentAndIdentity(snapshot)
            && OwnsWindow(snapshot.RootHwnd) && OwnsWindow(snapshot.BrowserHwnd) && OwnsWindow(snapshot.ViewHwnd)
            && ExplorerWindowObserver.SourceIsIdle(snapshot.RootHwnd, snapshot.ViewHwnd, snapshot.SourceThreadId);
    }

    private async Task<FolderRouteReceipt?> OpenDestinationAsync(FolderInvocation invocation, CancellationToken cancellation)
    {
        Interlocked.Increment(ref OpenCalls);
        LastInvocation = invocation;
        var receipt = await ExplorerLaunchRouter.SendAsync(new(Guid.NewGuid(), invocation, true), cancellation);
        LastReceipt = receipt;
        return receipt;
    }

    private async Task<bool> ValidateDestinationAsync(FolderRouteReceipt receipt, FolderInvocation invocation, CancellationToken cancellation)
    {
        Interlocked.Increment(ref DestinationValidationCalls);
        if (PauseDestinationValidation)
        {
            DestinationValidationRequested.TrySetResult();
            await ReleaseDestinationValidation.Task.WaitAsync(cancellation);
        }
        cancellation.ThrowIfCancellationRequested();
        return DestinationAlive(receipt) && await ExplorerLaunchRouter.ValidateReadyAsync(receipt, invocation, cancellation);
    }

    private bool DestinationAlive(FolderRouteReceipt receipt) => receipt.Process == Environment.ProcessId
        && receipt.Started == _started && OwnsWindow(new nint(receipt.Window)) && (ExtraDestinationAlive?.Invoke() ?? true);

    private bool CloseSource(ShellFolderSnapshot snapshot)
    {
        // This is the only close endpoint. It can target only this fixture's
        // registered handles, lifetime and source state, never a user window.
        if (!Enabled || !snapshot.SameContentAndIdentity(Current) || !OwnsWindow(snapshot.RootHwnd)
            || Native.GetProp(snapshot.RootHwnd, CookieProperty) == 0) return false;
        if (LastReceipt is not { Ready: true } || !DestinationAlive(LastReceipt)) return false;
        Interlocked.Increment(ref CloseCalls);
        return Native.PostMessage(snapshot.RootHwnd, 0x0010, 0, 0);
    }

    internal bool SourceExists => OwnsWindow(Current.RootHwnd);
    internal bool SourceVisible => Native.IsWindowVisible(Current.RootHwnd);
    internal void LoseSourceCookie() => Native.RemoveProp(Current.RootHwnd, CookieProperty);
    internal void ReplaceSourceCookie(nint cookie) => Native.SetProp(Current.RootHwnd, CookieProperty, cookie);
    internal nint SourceCookie => Native.GetProp(Current.RootHwnd, CookieProperty);
    internal void SetSourceEnabled(bool enabled) => Native.EnableWindow(Current.RootHwnd, enabled);

    internal async Task<ShellFolderSnapshot> AdditionalSourceAsync()
    {
        var dispatcher = await _dispatcherReady.Task;
        return await dispatcher.InvokeAsync(() =>
        {
            var root = CreateOwnWindow(0);
            var browser = CreateOwnWindow(root);
            var view = CreateOwnWindow(browser);
            var thread = Native.GetWindowThreadProcessId(root, out var process);
            return Current with { RootHwnd = root, BrowserHwnd = browser, ViewHwnd = view, SourceThreadId = thread, SourceProcessId = process, Generation = 1 };
        });
    }

    private bool OwnsWindow(nint window) => _windows.ContainsKey(window) && Native.IsWindow(window)
        && Native.GetWindowThreadProcessId(window, out var process) != 0 && process == Environment.ProcessId;

    private nint CreateOwnWindow(nint parent)
    {
        var window = Native.CreateWindowEx(0, "STATIC", "UltraExplorer owned transfer fixture", parent == 0 ? 0u : 0x40000000u,
            0, 0, 1, 1, parent == 0 ? new nint(-3) : parent, 0, Native.GetModuleHandle(null), 0);
        if (window == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _windows.TryAdd(window, 0);
        return window;
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                var request = await ExplorerLaunchRouter.ReadPacketAsync<FolderRouteRequest>(pipe, _stop.Token);
                if (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var client) || client != Environment.ProcessId)
                    throw new IOException("The fixture client is not owned.");
                Interlocked.Increment(ref ServerRequests);
                if (request.Id == Guid.Empty || request.Invocation.DestinationId == Guid.Empty
                    || !FolderCommandLine.TryParse(FolderCommandLine.BuildArguments(request.Invocation), out var normalized, out _))
                    throw new IOException("Invalid owned fixture request.");
                OfferRequested.TrySetResult();
                if (PauseOffer) await ReleaseOffer.Task.WaitAsync(_stop.Token);
                await ExplorerLaunchRouter.WritePacketAsync(pipe,
                    new FolderRouteReceipt(request.Id, true, false, checked((uint)Environment.ProcessId), _started, 0, Guid.Empty, "", []), _stop.Token);
                var commit = new byte[1];
                await pipe.ReadExactlyAsync(commit, _stop.Token);
                if (commit[0] != 1) throw new IOException("Fixture UI work was not committed.");
                var dispatcher = await _dispatcherReady.Task;
                var destination = await dispatcher.InvokeAsync(() =>
                {
                    if (!_destinations.TryGetValue(request.Invocation.DestinationId, out var window))
                    {
                        if (request.VerifyOnly) return 0;
                        _destinations[request.Invocation.DestinationId] = window = CreateOwnWindow(0);
                        Interlocked.Increment(ref DestinationCreations);
                    }
                    if (!request.VerifyOnly) _destinationStates[request.Invocation.DestinationId] = normalized;
                    return window;
                });
                ReadyRequested.TrySetResult();
                if (PauseReady) await ReleaseReady.Task.WaitAsync(_stop.Token);
                var state = await dispatcher.InvokeAsync(() => _destinationStates.GetValueOrDefault(request.Invocation.DestinationId));
                var receipt = new FolderRouteReceipt(request.Id, destination != 0, destination != 0, checked((uint)Environment.ProcessId), _started, destination,
                    request.Invocation.DestinationId, state?.FolderPath ?? "", state?.SelectedPaths ?? []);
                if (MutateReceipt is not null) receipt = MutateReceipt(receipt);
                await ExplorerLaunchRouter.WritePacketAsync(pipe, receipt, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (IOException) { }
        }
    }

    public void Dispose()
    {
        Enabled = false;
        _stop.Cancel();
        if (_dispatcherReady.Task.IsCompletedSuccessfully)
        {
            var dispatcher = _dispatcherReady.Task.Result;
            dispatcher.Invoke(() => { foreach (var window in _windows.Keys.ToArray()) if (OwnsWindow(window)) Native.DestroyWindow(window); });
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }
        _thread.Join(1000);
        try { _server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        // Exact fixture directory, never a computed parent/workspace target.
        var target = Path.GetFullPath(Folder);
        var fixtureParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UltraExplorerTransferFixture"));
        if (!string.Equals(Path.GetDirectoryName(target), fixtureParent, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(target), "N", out _))
            throw new InvalidOperationException("Fixture cleanup target is outside the exact owned directory.");
        if (Directory.Exists(target)) Directory.Delete(target, true);
        _stop.Dispose();
    }

    private sealed class OwnClaim : IExplorerTransferClaim
    {
        private readonly OwnedFixture _fixture;
        private readonly ShellFolderSnapshot _snapshot;
        private readonly nint _cookie = new(Random.Shared.Next(1, int.MaxValue));
        internal OwnClaim(OwnedFixture fixture, ShellFolderSnapshot snapshot)
        {
            _fixture = fixture;
            _snapshot = snapshot;
            if (!fixture.OwnsWindow(snapshot.RootHwnd) || !Native.SetProp(snapshot.RootHwnd, CookieProperty, _cookie))
                throw new InvalidOperationException("The fixture source is not owned.");
            fixture.ClaimsCleaned = false;
        }
        public bool OwnsSource => _fixture.OwnsWindow(_snapshot.RootHwnd)
            && _snapshot.SourceProcessId == Environment.ProcessId && _snapshot.SourceProcessStartUtc.ToFileTimeUtc() == _fixture._started
            && Native.GetWindowThreadProcessId(_snapshot.RootHwnd, out _) == _snapshot.SourceThreadId
            && Native.GetProp(_snapshot.RootHwnd, CookieProperty) == _cookie;
        public void Dispose()
        {
            if (OwnsSource) Native.RemoveProp(_snapshot.RootHwnd, CookieProperty);
            _fixture.ClaimsCleaned = Native.GetProp(_snapshot.RootHwnd, CookieProperty) != _cookie;
            Interlocked.Increment(ref _fixture.ClaimDisposals);
        }
    }

    private static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowEx(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnableWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool enabled);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetProp(nint window, string name, nint value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetProp(nint window, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint RemoveProp(nint window, string name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint process);
    }
}
