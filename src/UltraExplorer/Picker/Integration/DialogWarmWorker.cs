using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Windows;

namespace UltraExplorer.Picker.Integration;

/// <summary>One idle, prepared UI process. Busy or unavailable workers leave
/// the agent's per-dialog fallback available; the guardian remains independent.</summary>
internal sealed class DialogWarmClient : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _pipeName = "UltraExplorer.DialogWorker." + Guid.NewGuid().ToString("N");
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _process;
    private bool _ready, _busy, _disposed, _startupFailed;
    private readonly Action<nint, uint> _unclaimed;

    internal DialogWarmClient(Action<nint, uint> unclaimed) => _unclaimed = unclaimed;

    public bool HasExited
    {
        get
        {
            try { return _process is null || _process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
    }
    internal bool IsAvailable => !_disposed && _ready && !_busy && !HasExited;
    internal bool IsBusy => _busy;
    internal bool NeedsRestart => _startupFailed || HasExited;
    internal int ProcessId => _process?.Id ?? 0;

    public async Task StartAsync()
    {
        try
        {
            var arguments = new List<string> { "--dialog-worker", _pipeName };
            if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_DIALOG_TEST_ONLY_PROCESS") is { Length: > 0 }
                && Environment.GetEnvironmentVariable("ULTRAEXPLORER_DIALOG_TEST_CAPTURE_DIR") is { Length: > 0 } capture)
            { arguments.Add("--capture"); arguments.Add(Path.Combine(capture, "proxy.png")); }
            _process = DialogSelfProcess.Start([.. arguments]);
            _pipe = new(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await _pipe.ConnectAsync(8000, _lifetime.Token);
            _reader = new(_pipe, leaveOpen: true);
            _writer = new(_pipe, leaveOpen: true) { AutoFlush = true };
            var reply = await _reader.ReadLineAsync(_lifetime.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(12));
            if (reply != "READY") throw new IOException("The prepared dialog worker did not become ready.");
            if (_disposed) return;
            _ready = true;
            DialogIntegrationStore.Log("Prepared dialog worker is ready.");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException or ObjectDisposedException)
        {
            if (!_disposed) DialogIntegrationStore.Log("Prepared worker unavailable; per-dialog startup remains available", ex);
            _ready = false;
            _startupFailed = true;
            _pipe?.Dispose();
            // No request can have been accepted before READY. Retire only
            // this exact child if preparation itself has stopped responding.
            try { if (_process is not null && !_process.HasExited) _process.Kill(); }
            catch (Exception stop) when (stop is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    /// <param name="recognised">When the dialog was recognised (<see cref="Stopwatch.GetTimestamp"/>), for the worker's timings.</param>
    public Process? TryDispatch(nint window, long recognised = 0)
    {
        if (!IsAvailable) return null;
        _busy = true;
        Process reference;
        try { reference = Process.GetProcessById(_process!.Id); }
        catch (ArgumentException) { _busy = false; _ready = false; return null; }
        _ = DispatchAsync(window, DialogNative.ProcessId(window), recognised);
        return reference;
    }

    private async Task DispatchAsync(nint window, uint originalProcess, long recognised)
    {
        var accepted = false;
        try
        {
            await _writer!.WriteLineAsync(window.ToInt64().ToString(CultureInfo.InvariantCulture)
                + " " + recognised.ToString(CultureInfo.InvariantCulture));
            if (await _reader!.ReadLineAsync(_lifetime.Token) != "ACCEPTED")
                throw new IOException("The prepared dialog worker did not accept the request.");
            accepted = true;
            var reply = await _reader!.ReadLineAsync(_lifetime.Token);
            if (reply == "RECYCLE") { _ready = false; _pipe?.Dispose(); return; }
            if (reply != "DONE") throw new IOException("The prepared dialog worker disconnected.");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            _ready = false;
            if (!_disposed) DialogIntegrationStore.Log("Prepared dialog worker disconnected", ex);
            _pipe?.Dispose();
            if (!_disposed && !accepted) _unclaimed(window, originalProcess);
        }
        finally { _busy = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        var unclaimed = !_ready && !_busy;
        _disposed = true;
        _ready = false;
        _lifetime.Cancel();
        _pipe?.Dispose();
        if (unclaimed)
            try { if (_process is not null && !_process.HasExited) _process.Kill(); }
            catch (Exception stop) when (stop is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _process?.Dispose();
        _lifetime.Dispose();
    }
}

internal static class DialogWarmWorker
{
    internal static async Task RunAsync(Application app, string pipeName)
    {
        if (!pipeName.StartsWith("UltraExplorer.DialogWorker.", StringComparison.Ordinal)
            || !Guid.TryParseExact(pipeName["UltraExplorer.DialogWorker.".Length..], "N", out _))
            throw new ArgumentException("A valid dialog worker channel is required.");
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var prepared = new PreparedPicker(app);
        try
        {
            using var connectionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await pipe.WaitForConnectionAsync(connectionDeadline.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await PrepareAsync(prepared);
            await writer.WriteLineAsync("READY");
            var served = 0;
            while (DialogIntegrationStore.Read().Enabled)
            {
                var command = await reader.ReadLineAsync();
                if (command is null) break;
                var parts = command.Split(' ', 2);
                if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var window) || window == 0)
                    throw new IOException("The dialog worker request is invalid.");
                var recognised = parts.Length > 1 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var at) ? at : 0;
                await writer.WriteLineAsync("ACCEPTED");
                var proxy = new NativeDialogProxy(app, (nint)window, recognised, prepared);
                await proxy.RunAsync();
                served++;
                if (!proxy.CanReuseWorker) { await writer.WriteLineAsync("RECYCLE"); break; }
                if (WornOut(served) is { } reason)
                {
                    // A fresh worker takes over (the agent starts one at once);
                    // this one leaves nothing behind but what it has served.
                    DialogIntegrationStore.Log($"The prepared dialog worker is retired after {served} dialog(s): {reason}.");
                    await writer.WriteLineAsync("RECYCLE");
                    break;
                }
                // The used window is closing; the next one is made once this
                // process has nothing else to do.
                prepared.PrepareWhenIdle();
                await writer.WriteLineAsync("DONE");
            }
        }
        catch (IOException ex) { DialogIntegrationStore.Log("Prepared dialog worker channel closed", ex); }
        catch (OperationCanceledException) { DialogIntegrationStore.Log("Prepared dialog worker was not claimed by its agent."); }
    }

    /// <summary>
    /// Why this worker should make way for a fresh one after the dialogs it
    /// has served, or null. A worker lives for the whole session, and what a
    /// served dialog might leave behind in it - a thread, a hidden window, a
    /// handle - adds up over it (each picker window's icon thread once did:
    /// about three USER objects a dialog, of a per-process limit of 10,000).
    /// So it is replaced after a few hundred dialogs, or at once should its
    /// USER objects or handles ever run high.
    /// </summary>
    private static string? WornOut(int served)
    {
        var limit = int.TryParse(Environment.GetEnvironmentVariable("ULTRAEXPLORER_DIALOG_TEST_RECYCLE_AFTER"), out var asked) && asked > 0
            && Environment.GetEnvironmentVariable("ULTRAEXPLORER_DIALOG_TEST_ONLY_PROCESS") is { Length: > 0 } ? asked : 400;
        if (served >= limit) return $"{served} dialogs served";
        using var self = Process.GetCurrentProcess();
        var user = GetGuiResources(self.Handle, 1);
        if (user >= 3000) return $"{user} USER objects in use";
        return self.HandleCount >= 10000 ? $"{self.HandleCount} handles in use" : null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);

    /// <summary>
    /// Before READY: UI Automation loaded, the names known folders have in
    /// dialogs, and one picker shown cloaked (its first Show is what makes
    /// every later one fast: surface, renderer, the GPU of its monitor, JIT).
    /// READY waits for the names too, within their own deadline: until the
    /// table is there, the quick read cannot resolve "Address: Downloads" and
    /// the full read would not follow the user's own folder by name.
    /// </summary>
    private static async Task PrepareAsync(PreparedPicker prepared)
    {
        var names = Task.Run(() => KnownFolderNames.Build(KnownFolderNames.InstalledLanguages()));
        _ = names.ContinueWith(task => { if (task.IsCompletedSuccessfully) PreparedPicker.Names = task.Result; }, TaskScheduler.Default);
        _ = Task.Run(DialogLease.WarmUp);
        using (var thread = new AutomationThread())
        {
            try { await thread.Run(() => { using var automation = new NativeDialogAutomation(); }).WaitAsync(TimeSpan.FromSeconds(4)); }
            catch (Exception ex) { DialogIntegrationStore.Log("UI Automation prewarm skipped", ex); }
        }
        await prepared.PrepareAsync(TimeSpan.FromSeconds(3));
        try { PreparedPicker.Names = await names.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { DialogIntegrationStore.Log("Known folder names were not read before the worker was ready; they are used once read", ex); }
    }
}
