using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32.SafeHandles;
using UltraExplorer.Picker.Integration;

namespace UltraExplorer.Services;

internal sealed record FolderRouteReceipt(Guid Request, bool Accepted, bool Ready, uint Process, long Started,
    long Window, Guid DestinationId, string FolderPath, IReadOnlyList<string> SelectedPaths);
/// <param name="Discard">
/// An Explorer handoff given up: the window opened for its destination is
/// closed unless the user has used it (<see cref="ExplorerLaunchRouter.DiscardIfUntouchedAsync"/>).
/// </param>
internal sealed record FolderRouteRequest(Guid Id, FolderInvocation Invocation, bool WaitForReady, bool VerifyOnly = false, bool Settings = false,
    bool Discard = false);

/// <summary>One broker per state directory. Each adopted source keeps a distinct
/// destination. Offers execute only after client commit, so a timed-out offer
/// cannot later duplicate a window opened by its caller.</summary>
internal static class ExplorerLaunchRouter
{
    internal const int MaximumPacket = 128 * 1024;
    internal static string PipeName => "UltraExplorer.Folders.v2." + DialogIntegrationStore.InstanceKey;
    private static readonly List<MainWindow> Windows = [];
    private static FolderRouteHost? _host;
    private static readonly uint OwnProcess = checked((uint)Environment.ProcessId);
    private static readonly long OwnStart = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToFileTimeUtc();
    private static bool Allowed(FolderInvocation invocation) => !invocation.OriginIsShell || DialogIntegrationStore.Read().Enabled;
    internal static string FolderWorkspacePath(Guid destination) => UltraExplorer.Infrastructure.AppPaths.State(
        Path.Combine("shell-windows", destination.ToString("N") + ".workspace.json"));

    /// <summary>How Windows Explorer is opened. Checks replace it, so that the
    /// fallback can be exercised without an Explorer window opening.</summary>
    internal static Func<ProcessStartInfo, Process?> StartExplorer { get; set; } = Process.Start;

    /// <summary>How UltraExplorer is started when no broker answers. Checks
    /// replace it, so that no second UltraExplorer starts while they run.</summary>
    internal static Func<string[], Process> StartSelf { get; set; } = DialogSelfProcess.Start;

    internal static bool TryOpenFolder(string path) => DialogIntegrationStore.Read().Enabled
        && FolderCommandLine.TryOpenFolder(path, out var request, out _) && Launch(request with { OriginIsShell = true });
    internal static bool TryReveal(IEnumerable<string> paths) => DialogIntegrationStore.Read().Enabled
        && FolderCommandLine.TryReveal(paths, out var request, out _) && Launch(request with { OriginIsShell = true });
    private static bool Launch(FolderInvocation invocation)
    {
        if (ChooseDirectLaunchWindow(Application.Current?.MainWindow as MainWindow, Windows) is { } window)
        { _ = ObserveAsync(window.ApplyFolderInvocationAsync(invocation)); return true; }
        try { using var process = StartSelf(FolderCommandLine.BuildArguments(invocation)); return true; }
        catch (Exception ex) when (Failure(ex)) { DialogIntegrationStore.Log("The folder could not be opened", ex); return false; }
    }

    /// <summary>A closing or occupied main window cannot accept an in-process
    /// native folder action. Use another live normal window or the usual launcher.</summary>
    internal static MainWindow? ChooseDirectLaunchWindow(MainWindow? main, IEnumerable<MainWindow> windows)
    {
        static bool Free(MainWindow window) => !window.IsPickerMode && !window.IsFolderWindowClosing
            && !window.IsFolderInvocationPending;
        if (main is not null && Free(main)) return main;
        var candidates = windows.Where(Free).ToArray();
        return candidates.FirstOrDefault(window => window.IsFolderWindowReady) ?? candidates.FirstOrDefault();
    }

    internal static bool TryHandleShellFallback(string[] arguments)
    {
        if (!arguments.Contains(FolderCommandLine.ShellRequestSwitch, StringComparer.OrdinalIgnoreCase)) return false;
        // A destination request belongs to an Explorer frame already open.
        // If replacement was switched off meanwhile, that frame remains its
        // fallback; starting Explorer here would create a duplicate window.
        var destinationAt = Array.FindIndex(arguments, value => value.Equals(FolderCommandLine.DestinationSwitch, StringComparison.OrdinalIgnoreCase));
        if (destinationAt >= 0 && destinationAt + 1 < arguments.Length
            && Guid.TryParse(arguments[destinationAt + 1], out var destination) && destination != Guid.Empty
            && !DialogIntegrationStore.Read().Enabled) return true;
        if (DialogIntegrationStore.Read().Enabled && FolderCommandLine.TryParse(arguments, out var invocation, out _))
            return NativeExplorerNavigation.TryHandle(invocation, arguments);
        // Best effort: while Settings is still switching the mode off it holds
        // the registrations' lock, and the folder must open in Explorer anyway.
        if (!DialogIntegrationStore.Read().Enabled)
            try { DialogIntegrationStore.ReconcileRegistrations(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                or System.ComponentModel.Win32Exception)
            { DialogIntegrationStore.Log("The folder-opening registration could not be reconciled", ex); }
        var at = Array.FindIndex(arguments, value => value.Equals(FolderCommandLine.OpenFolderSwitch, StringComparison.OrdinalIgnoreCase));
        if (at >= 0 && at + 1 < arguments.Length) OpenWindows(arguments[at + 1]);
        else if (arguments.Contains(FolderCommandLine.HomeSwitch, StringComparer.OrdinalIgnoreCase)) OpenWindows(null);
        else
        {
            var paths = new List<string>();
            for (var i = 0; i + 1 < arguments.Length; i++)
                if (arguments[i].Equals(FolderCommandLine.RevealSwitch, StringComparison.OrdinalIgnoreCase)) paths.Add(arguments[++i]);
            OpenWindows(paths.FirstOrDefault(), paths.Count > 0);
        }
        return true;
    }
    internal static void OpenWindows(string? path, bool select = false)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
        if (!string.IsNullOrWhiteSpace(path)) start.Arguments = (select ? "/select," : "") + NativeShellService.BuildCommandLine([path]);
        using var process = StartExplorer(start);
    }

    /// <summary>
    /// Windows Explorer itself, for a command that names it - "Show in File
    /// Explorer", "Open in Windows Explorer" - never routed into UltraExplorer,
    /// whether or not folders open through it.  While they do, the frame
    /// Explorer opens for it is marked to stay with Windows
    /// (<see cref="NativeExplorerSessionMarker"/>) as soon as it appears,
    /// well before the observer would hand a new frame over: the user asked
    /// for Explorer by name.  A frame of the user's own opened in the same
    /// few seconds may be kept with Windows too, which is all that can happen
    /// to it.
    /// </summary>
    internal static void OpenExplorerByName(string path, bool select)
    {
        var before = DialogIntegrationStore.Read().Enabled ? ExplorerFrames() : null;
        OpenWindows(path, select);
        if (before is not null) _ = Task.Run(() => KeepNewFrameNativeAsync(before));
    }

    private static HashSet<nint> ExplorerFrames()
    {
        var frames = new HashSet<nint>();
        EnumWindows((window, _) =>
        {
            if (ExplorerWindowInterop.ClassName(window) is "CabinetWClass" or "ExploreWClass") frames.Add(window);
            return true;
        }, 0);
        return frames;
    }

    /// <summary>The first Explorer frame not in <paramref name="before"/> to appear within five seconds, marked to stay with Windows.</summary>
    private static async Task KeepNewFrameNativeAsync(HashSet<nint> before)
    {
        var until = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < until)
        {
            foreach (var frame in ExplorerFrames().Where(frame => !before.Contains(frame)))
            {
                if (ExplorerWindowInterop.GetWindowThreadProcessId(frame, out var processId) == 0 || processId == 0) continue;
                try
                {
                    using var process = Process.GetProcessById(checked((int)processId));
                    if (NativeExplorerSessionMarker.Mark(frame, processId, process.StartTime.ToUniversalTime())) return;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                    or System.ComponentModel.Win32Exception or OverflowException) { }
            }
            await Task.Delay(20);
        }
    }
    internal static bool TryForward(FolderInvocation invocation)
    {
        if (!Allowed(invocation)) return false;
        if (!BrokerPipeExists()) return false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            // Startup is synchronous on WPF's STA. Run the entire IPC client
            // off that context so its I/O continuations cannot wait for the
            // dispatcher that is waiting here for the forwarding result.
            return Task.Run(() => SendAsync(new(Guid.NewGuid(), invocation, false), deadline.Token))
                .GetAwaiter().GetResult().Accepted;
        }
        catch (FolderDeliveryException ex) when (ex.Committed) { return true; }
        catch (Exception ex) when (Failure(ex)) { return false; }
    }

    /// <summary>
    /// The tray's "Open settings" (<c>--settings</c>) while a window is open:
    /// that window shows Settings.  A second whole UltraExplorer on the same
    /// workspace and marks would have its changes overwritten by the first
    /// window's next save.  False when no broker takes it: the launch opens
    /// its own window as before.
    /// </summary>
    internal static bool TryForwardSettings()
    {
        if (!BrokerPipeExists()) return false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            return Task.Run(() => SendAsync(new(Guid.NewGuid(), null!, false, Settings: true), deadline.Token))
                .GetAwaiter().GetResult().Accepted;
        }
        catch (FolderDeliveryException ex) when (ex.Committed) { return true; }
        catch (Exception ex) when (Failure(ex)) { return false; }
    }

    /// <summary>
    /// Taken by a folder launch before it looks for a broker, and kept by the
    /// one that finds none until its own broker listens.  The launches the
    /// Shell starts together for several folders then wait for that one and
    /// hand it their folders, instead of each building a whole window process
    /// on the same state.  Null when the wait runs out: the launch goes on alone.
    /// </summary>
    internal static Mutex? EnterFolderStartup()
    {
        var startup = new Mutex(false, @"Local\UltraExplorer.FolderStartup." + DialogIntegrationStore.InstanceKey);
        try { if (startup.WaitOne(TimeSpan.FromSeconds(15))) return startup; }
        catch (AbandonedMutexException) { return startup; }
        startup.Dispose();
        return null;
    }

    /// <summary>Lets the next launch look for a broker: at once, or once this
    /// process's broker listens (ten seconds at most).  Released on the thread
    /// that took it, the window thread, as a mutex must be.</summary>
    internal static void LeaveFolderStartup(Mutex? startup, bool whenListening)
    {
        if (startup is null) return;
        void Release()
        {
            try { startup.ReleaseMutex(); }
            catch (ApplicationException) { }
            startup.Dispose();
        }
        if (!whenListening || _host is not { } host) { Release(); return; }
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _ = host.Listening.WaitAsync(TimeSpan.FromSeconds(10))
            .ContinueWith(_ => dispatcher.InvokeAsync(Release), TaskScheduler.Default);
    }
    internal static void Attach(MainWindow window)
    {
        if (window.IsPickerMode || Windows.Contains(window)) return;
        if (window.FolderDestinationId == Guid.Empty) window.FolderDestinationId = Guid.NewGuid();
        Windows.Add(window);
        FolderShellWindowRegistration.Attach(window);
        if (!_pruned) { _pruned = true; _ = Task.Run(PruneFolderWorkspaces); }
        _host ??= new FolderRouteHost(window.Dispatcher);
        window.Closed += (_, _) =>
        {
            Windows.Remove(window);
            if (Windows.Count == 0) { _host?.Dispose(); _host = null; }
            DeleteFolderWorkspace(window.FolderDestinationId);
        };
    }

    private static bool _pruned;
    private static readonly string[] FolderWorkspaceSuffixes = [".workspace.json", ".workspace.json.tree.json", ".workspace.json.marks.json"];

    /// <summary>
    /// A folder window's own workspace, tree and marks are read by that window
    /// only, and its destination is never given to a window again once it has
    /// closed: they go with it, or every adopted folder left three files behind.
    /// A main window has none, so nothing of the user's workspace is touched.
    /// </summary>
    private static void DeleteFolderWorkspace(Guid destination)
    {
        var workspace = FolderWorkspacePath(destination);
        foreach (var path in new[] { workspace, workspace + ".tree.json", workspace + ".marks.json" })
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The folder window files earlier versions left behind, or a process that
    /// ended without closing its windows.  Only a destination none of whose
    /// files was written for a week goes: a window still open saves its own
    /// again whenever it changes.
    /// </summary>
    private static void PruneFolderWorkspaces()
    {
        try
        {
            var folder = Path.GetDirectoryName(FolderWorkspacePath(Guid.Empty))!;
            if (!Directory.Exists(folder)) return;
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(7);
            var destinations = Directory.EnumerateFiles(folder, "*.workspace.json*")
                .Where(path => Path.GetFileName(path) is { Length: > 32 } name && Guid.TryParseExact(name[..32], "N", out _)
                    && FolderWorkspaceSuffixes.Contains(name[32..], StringComparer.OrdinalIgnoreCase))
                .GroupBy(path => Path.GetFileName(path)[..32], StringComparer.OrdinalIgnoreCase);
            foreach (var destination in destinations)
            {
                if (destination.Any(path => File.GetLastWriteTimeUtc(path) >= cutoff)) continue;
                foreach (var path in destination)
                {
                    try { File.Delete(path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    internal static async Task<FolderRouteReceipt?> OpenAndWaitAsync(FolderInvocation invocation, CancellationToken cancellation)
    {
        if (!Allowed(invocation)) return null;
        var request = new FolderRouteRequest(Guid.NewGuid(), invocation, true);
        // A selection too large for one packet can never be delivered. No
        // broker is missing, so none is started, again on every change of it.
        if (JsonSerializer.SerializeToUtf8Bytes(request).Length > MaximumPacket) return null;
        Process? started = null;
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(18) && Allowed(invocation))
        {
            cancellation.ThrowIfCancellationRequested();
            // Only FILE_NOT_FOUND proves absence. Busy or inaccessible pipes
            // still go through SendAsync's identity/commit checks.
            if (!BrokerPipeExists())
            {
                if (started is null) started = StartBroker(invocation);
                if (StartFailed(started)) return null;
                await Task.Delay(50, cancellation);
                continue;
            }
            try
            {
                var receipt = await SendAsync(request, cancellation);
                return receipt.Accepted && receipt.Ready ? receipt : null;
            }
            catch (Exception ex) when (Failure(ex) && !cancellation.IsCancellationRequested)
            {
                // Retrying the same id retrieves its result rather than navigating again.
                // A broker that is there but slow to answer is waited for.
                if (started is null && ex is not FolderDeliveryException { Committed: true } && !BrokerPipeExists()) started = StartBroker(invocation);
                if (started is not null && StartFailed(started)) return null;
                await Task.Delay(100, cancellation);
            }
        }
        return null;
    }

    private static readonly object StartGate = new();
    private static Process? _started;
    private static long _startedAt;

    /// <summary>One UltraExplorer for every transfer that finds no broker,
    /// started for the first one's folder and window: it opens that window,
    /// never the main workspace beside it, and the transfers after it wait for
    /// its broker instead of starting a copy each. The process is shared, so
    /// it is never disposed here.</summary>
    private static Process StartBroker(FolderInvocation invocation)
    {
        lock (StartGate)
        {
            if (_started is { HasExited: false } starting && Environment.TickCount64 - _startedAt < 20000) return starting;
            _startedAt = Environment.TickCount64;
            // Windows starts no process whose command line passes 32,767
            // characters, which a selection of some hundreds of files does
            // long before its packet is too large. Such a reveal starts its
            // window on the folder alone; the request retried for the same
            // destination then selects the files in it.
            var arguments = FolderCommandLine.BuildCommandLine(invocation).Length + DialogSelfProcess.ExecutablePath.Length > 30000
                ? FolderCommandLine.BuildArguments(invocation with { Kind = FolderInvocationKind.OpenFolder, SelectedPaths = Array.Empty<string>() })
                : FolderCommandLine.BuildArguments(invocation);
            return _started = StartSelf(arguments);
        }
    }

    /// <summary>A terminated copy cannot answer when no broker remains, even
    /// if it exited successfully. An accepted forward to a live broker can.</summary>
    private static bool StartFailed(Process started)
    {
        lock (StartGate) return started.HasExited
            && (started.ExitCode != UltraExplorer.Picker.FileDialogCommandLine.ExitAccepted || !BrokerPipeExists());
    }

    /// <summary>Whether a broker has its pipe, answering or busy. Only the
    /// pipe being absent says that no UltraExplorer window is listening.</summary>
    private static bool BrokerPipeExists() => WaitNamedPipe(@"\\.\pipe\" + PipeName, 1) || Marshal.GetLastWin32Error() != 2;
    internal static async Task<bool> ValidateReadyAsync(FolderRouteReceipt receipt, FolderInvocation invocation, CancellationToken cancellation)
    {
        if (!ReceiptMatches(receipt, invocation) || !Allowed(invocation)) return false;
        try
        {
            var fresh = await SendAsync(new(Guid.NewGuid(), invocation, true, true), cancellation);
            return ReceiptMatches(fresh, invocation) && fresh.Process == receipt.Process && fresh.Started == receipt.Started
                && fresh.Window == receipt.Window && fresh.DestinationId == receipt.DestinationId && Allowed(invocation);
        }
        catch (Exception ex) when (Failure(ex)) { return false; }
    }
    /// <summary>
    /// An Explorer handoff was given up after its window was asked for - the
    /// user went on in the Explorer window, closed it, or it never settled -
    /// so that window would be a second one beside Explorer's that nobody
    /// asked for.  The broker closes it, unless the user has used it since
    /// it opened (<see cref="MainWindow.IsUntouchedHandoff"/>).  The request
    /// names the destination only: a broker that does not know this request
    /// refuses it as a folder that cannot be opened, rather than opening one.
    /// </summary>
    internal static async Task DiscardIfUntouchedAsync(FolderInvocation invocation)
    {
        if (invocation.DestinationId == Guid.Empty) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        var request = new FolderRouteRequest(Guid.NewGuid(),
            new FolderInvocation(FolderInvocationKind.OpenFolder, string.Empty, Array.Empty<string>(), true, invocation.DestinationId),
            false, Discard: true);
        try { await SendAsync(request, deadline.Token); }
        catch (Exception ex) when (Failure(ex)) { }
    }

    internal static bool ReceiptMatches(FolderRouteReceipt receipt, FolderInvocation invocation) =>
        receipt.Accepted && receipt.Ready && receipt.Window != 0 && receipt.Process != 0 && receipt.Started > 0
        && (invocation.DestinationId == Guid.Empty || receipt.DestinationId == invocation.DestinationId)
        && string.Equals(receipt.FolderPath, invocation.FolderPath, StringComparison.OrdinalIgnoreCase)
        && receipt.SelectedPaths is not null && receipt.SelectedPaths.SequenceEqual(
            invocation.Kind == FolderInvocationKind.OpenFolder ? Array.Empty<string>() : invocation.SelectedPaths, StringComparer.OrdinalIgnoreCase);

    internal static async Task<FolderRouteReceipt> SendAsync(FolderRouteRequest request, CancellationToken cancellation)
    {
        var committed = false;
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(250, cancellation);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var server)) throw new IOException("Destination identity unavailable.");
            await WritePacketAsync(pipe, request, cancellation);
            var offer = await ReadPacketAsync<FolderRouteReceipt>(pipe, cancellation);
            ValidateIdentity(offer, request.Id, server, false);
            if (!offer.Accepted) return offer;
            cancellation.ThrowIfCancellationRequested();
            // A launcher the Shell started for a double-click may bring a
            // window to the front; the broker may not. Passed on before the
            // broker acts, or its window opens the folder out of sight with
            // only its taskbar button flashing.
            if (!request.VerifyOnly && !request.Discard) _ = AllowSetForegroundWindow(server);
            // The listener is waiting for this single byte. Before it arrives,
            // a disconnect or timeout has no user-interface side effect.
            pipe.WriteByte(1);
            committed = true;
            if (!request.WaitForReady) return offer;
            var ready = await ReadPacketAsync<FolderRouteReceipt>(pipe, cancellation);
            ValidateIdentity(ready, request.Id, server, ready.Ready);
            if (ready.Ready && !ReceiptMatches(ready, request.Invocation)) throw new IOException("Destination folder or selection changed.");
            return ready;
        }
        catch (Exception ex) when (Failure(ex)) { throw new FolderDeliveryException(committed, ex); }
    }
    private static void ValidateIdentity(FolderRouteReceipt receipt, Guid request, uint server, bool window)
    {
        if (receipt.Request != request || receipt.Process != server || receipt.Started <= 0) throw new IOException("Destination identity changed.");
        // A broker that exits between its offer and this check is a failed
        // delivery like any other, which the launcher answers by opening the
        // folder itself; Windows reports it as an argument error instead.
        Process process;
        try { process = Process.GetProcessById(checked((int)server)); }
        catch (ArgumentException ex) { throw new IOException("Destination is no longer available.", ex); }
        using var owned = process;
        if (process.HasExited || process.StartTime.ToUniversalTime().ToFileTimeUtc() != receipt.Started
            || server != OwnProcess && !string.Equals(process.MainModule?.FileName, DialogSelfProcess.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            || window && (!DialogNative.IsWindow((nint)receipt.Window) || DialogNative.ProcessId((nint)receipt.Window) != server))
            throw new IOException("Destination is no longer available.");
    }
    internal static async Task WritePacketAsync<T>(Stream stream, T value, CancellationToken cancellation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length is <= 0 or > MaximumPacket) throw new IOException("Folder packet too large.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellation); await stream.WriteAsync(bytes, cancellation); await stream.FlushAsync(cancellation);
    }
    internal static async Task<T> ReadPacketAsync<T>(Stream stream, CancellationToken cancellation)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, cancellation);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is <= 0 or > MaximumPacket) throw new IOException("Invalid bounded folder packet.");
        var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, cancellation);
        return JsonSerializer.Deserialize<T>(bytes, new JsonSerializerOptions { MaxDepth = 32 }) ?? throw new IOException("Null folder packet.");
    }
    private static bool Normalize(FolderRouteRequest request, out FolderInvocation normalized)
    {
        normalized = null!;
        var invocation = request.Invocation;
        if (request.Id == Guid.Empty || invocation is null || invocation.SelectedPaths is null || invocation.FolderPath is null
            || invocation.Kind is not (FolderInvocationKind.OpenFolder or FolderInvocationKind.Reveal) || !Allowed(invocation)) return false;
        return FolderCommandLine.TryParse(FolderCommandLine.BuildArguments(invocation), out normalized, out _)
            && string.Equals(invocation.FolderPath, normalized.FolderPath, StringComparison.OrdinalIgnoreCase)
            && (invocation.Kind != FolderInvocationKind.OpenFolder || invocation.SelectedPaths.Count == 0);
    }
    private static FolderRouteReceipt Receipt(Guid id, bool accepted, bool ready, MainWindow? window = null) =>
        new(id, accepted, ready, OwnProcess, OwnStart, window is null ? 0 : new WindowInteropHelper(window).Handle.ToInt64(),
            window?.FolderDestinationId ?? Guid.Empty, window?.CurrentFolderPath ?? "", window?.CurrentFolderSelection ?? Array.Empty<string>());

    private sealed class FolderRouteHost : IDisposable
    {
        private readonly System.Windows.Threading.Dispatcher _dispatcher;
        private readonly CancellationTokenSource _stop = new();
        private readonly FolderRouteRequestCache _requests = new();
        private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Done once the pipe exists and launches can reach this broker.</summary>
        internal Task Listening => _listening.Task;
        internal FolderRouteHost(System.Windows.Threading.Dispatcher dispatcher)
        { _dispatcher = dispatcher; new Thread(RunThread) { IsBackground = true, Name = "UltraExplorer folder broker" }.Start(); }
        private void RunThread()
        {
            // Mutex ownership stays on this thread across asynchronous I/O.
            using var mutex = new Mutex(false, @"Local\UltraExplorer.FolderBroker." + DialogIntegrationStore.InstanceKey);
            while (!_stop.IsCancellationRequested)
            {
                var entered = false;
                try { entered = mutex.WaitOne(0); } catch (AbandonedMutexException) { entered = true; }
                if (!entered) { _stop.Token.WaitHandle.WaitOne(500); continue; }
                try { RunAsync().GetAwaiter().GetResult(); }
                catch (Exception ex) when (Failure(ex)) { if (!_stop.IsCancellationRequested) DialogIntegrationStore.Log("The folder broker will retry", ex); }
                finally { mutex.ReleaseMutex(); }
                if (!_stop.IsCancellationRequested) _stop.Token.WaitHandle.WaitOne(250);
            }
        }
        private async Task RunAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 16, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                _listening.TrySetResult();
                try { await pipe.WaitForConnectionAsync(_stop.Token); } catch { await pipe.DisposeAsync(); throw; }
                _ = HandleAsync(pipe);
            }
        }
        private async Task HandleAsync(NamedPipeServerStream pipe)
        {
            await using (pipe)
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); deadline.CancelAfter(3000);
                    var request = await ReadPacketAsync<FolderRouteRequest>(pipe, deadline.Token);
                    if (request.Settings) { await OpenSettingsAsync(pipe, request, deadline.Token); return; }
                    if (request.Discard) { await DiscardAsync(pipe, request, deadline.Token); return; }
                    if (!Normalize(request, out var normalized))
                    { await WritePacketAsync(pipe, Receipt(request.Id, false, false), deadline.Token); return; }
                    request = request with { Invocation = normalized };
                    var fingerprint = JsonSerializer.Serialize(request with { WaitForReady = false });
                    if (!request.VerifyOnly && !_requests.CanOffer(request.Id, fingerprint))
                    { await WritePacketAsync(pipe, Receipt(request.Id, false, false), deadline.Token); return; }
                    await WritePacketAsync(pipe, Receipt(request.Id, true, false), deadline.Token);
                    var commit = new byte[1]; await pipe.ReadExactlyAsync(commit, deadline.Token);
                    if (commit[0] != 1 || !Allowed(normalized) || _stop.IsCancellationRequested) return;
                    Task<FolderRouteReceipt> work;
                    if (request.VerifyOnly) work = ExecuteAsync(request);
                    else if (!_requests.TryCommit(request.Id, fingerprint, () => ExecuteAsync(request), out work))
                    { if (request.WaitForReady) await WritePacketAsync(pipe, Receipt(request.Id, false, false), _stop.Token); return; }
                    var result = await work;
                    if (result.Ready)
                    {
                        result = await _dispatcher.InvokeAsync(() =>
                        {
                            var window = Windows.FirstOrDefault(candidate => candidate.FolderDestinationId == result.DestinationId);
                            return Receipt(request.Id, true, window is not null && Allowed(normalized)
                                && window.MatchesFolderInvocation(normalized), window);
                        });
                    }
                    if (request.WaitForReady) await WritePacketAsync(pipe, result, _stop.Token);
                }
                catch (Exception ex) when (Failure(ex) || ex is JsonException or ArgumentException or NotSupportedException)
                {
                    if (!_stop.IsCancellationRequested && ex is not OperationCanceledException)
                        DialogIntegrationStore.Log("A folder request was refused", ex);
                }
            }
        }
        /// <summary>A forwarded <c>--settings</c>: shown by the first window that
        /// is not closing, as its own Settings button would, once it is loaded.</summary>
        private async Task OpenSettingsAsync(NamedPipeServerStream pipe, FolderRouteRequest request, CancellationToken deadline)
        {
            await WritePacketAsync(pipe, Receipt(request.Id, request.Id != Guid.Empty, false), deadline);
            if (request.Id == Guid.Empty) return;
            var commit = new byte[1]; await pipe.ReadExactlyAsync(commit, deadline);
            if (commit[0] != 1 || _stop.IsCancellationRequested) return;
            await _dispatcher.InvokeAsync(() =>
            {
                if ((Windows.FirstOrDefault(window => window.IsFolderWindowReady)
                    ?? Windows.FirstOrDefault(window => !window.IsFolderWindowClosing)) is not { } window) return;
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                if (window.IsLoaded) window.OpenSettings();
                else window.Loaded += (_, _) => window.OpenSettings();
            });
        }
        /// <summary>A handoff given up (<see cref="DiscardIfUntouchedAsync"/>): its
        /// destination's window closes if the user has not used it.</summary>
        private async Task DiscardAsync(NamedPipeServerStream pipe, FolderRouteRequest request, CancellationToken deadline)
        {
            var destination = request.Invocation?.DestinationId ?? Guid.Empty;
            var accepted = request.Id != Guid.Empty && destination != Guid.Empty;
            await WritePacketAsync(pipe, Receipt(request.Id, accepted, false), deadline);
            if (!accepted) return;
            var commit = new byte[1]; await pipe.ReadExactlyAsync(commit, deadline);
            if (commit[0] != 1 || _stop.IsCancellationRequested) return;
            await _dispatcher.InvokeAsync(() =>
            {
                if (Windows.FirstOrDefault(window => window.FolderDestinationId == destination) is { IsUntouchedHandoff: true } window)
                {
                    DialogIntegrationStore.Log("A window opened for an Explorer folder that stayed with Windows was closed.");
                    window.Close();
                }
            });
        }

        private async Task<FolderRouteReceipt> ExecuteAsync(FolderRouteRequest request)
        {
            var invocation = request.Invocation;
            if (!Allowed(invocation) || _stop.IsCancellationRequested) return Receipt(request.Id, false, false);
            MainWindow? destination = null;
            Task<bool>? navigation = null;
            var created = false;
            using var navigationDeadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            navigationDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    // A window still opening another request's folder, or
                    // saving on its way out, is not free: folders opened
                    // together each get a window, and none is dropped by a
                    // window that is closing.
                    bool Free(MainWindow window) => request.VerifyOnly || !window.IsFolderInvocationPending && !window.IsFolderWindowClosing;
                    destination = invocation.DestinationId == Guid.Empty
                        ? Windows.FirstOrDefault(window => window.IsFolderWindowReady && Free(window)) ?? Windows.FirstOrDefault(Free)
                        : Windows.FirstOrDefault(window => window.FolderDestinationId == invocation.DestinationId && !window.IsFolderWindowClosing);
                    if (destination is null && !request.VerifyOnly && Allowed(invocation) && !_stop.IsCancellationRequested)
                    {
                        var id = invocation.DestinationId == Guid.Empty ? Guid.NewGuid() : invocation.DestinationId;
                        destination = new MainWindow(null, FolderWorkspacePath(id)) { ShowActivated = false };
                        destination.FolderDestinationId = id;
                        destination.PrepareFolderInvocation(invocation);
                        Attach(destination); destination.Show(); created = true;
                    }
                    // Begun on the window's thread right away, so the window
                    // counts as taken before the next request looks for one.
                    if (destination is not null && !request.VerifyOnly)
                        navigation = destination.ApplyFolderInvocationAsync(invocation, navigationDeadline.Token);
                });
                if (destination is null) return Receipt(request.Id, false, false);
                if (navigation is not null && (!await navigation.WaitAsync(navigationDeadline.Token) || !Allowed(invocation)))
                {
                    if (created) await _dispatcher.InvokeAsync(() => { if (destination.IsUnusedFolderRequest) destination.Close(); });
                    return Receipt(request.Id, true, false);
                }
                return await _dispatcher.InvokeAsync(() => Receipt(request.Id, true, Allowed(invocation) && destination.MatchesFolderInvocation(invocation), destination));
            }
            catch (Exception ex) when (Failure(ex) || ex is ArgumentException or NotSupportedException)
            {
                DialogIntegrationStore.Log("A destination folder was not ready", ex);
                if (created && destination is not null)
                    await _dispatcher.InvokeAsync(() => { if (destination.IsUnusedFolderRequest) destination.Close(); });
                return Receipt(request.Id, true, false);
            }
        }
        public void Dispose() => _stop.Cancel();
    }
    private static async Task ObserveAsync(Task<bool> work)
    { try { await work; } catch (Exception ex) when (Failure(ex)) { DialogIntegrationStore.Log("A requested folder was not ready", ex); } }
    private static bool Failure(Exception ex) => ex is IOException or TimeoutException or OperationCanceledException
        or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception;
    private sealed class FolderDeliveryException(bool committed, Exception inner) : IOException("The folder exchange did not complete.", inner)
    { internal bool Committed { get; } = committed; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint process);
    [DllImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WaitNamedPipe(string name, uint timeout);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AllowSetForegroundWindow(uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(ExplorerWindowInterop.EnumWindowCallback callback, nint parameter);
}
