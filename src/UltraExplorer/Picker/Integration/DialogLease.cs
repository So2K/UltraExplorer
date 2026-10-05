using System.Diagnostics;
using System.Text.Json;
using UltraExplorer.Infrastructure;

namespace UltraExplorer.Picker.Integration;

internal sealed record DialogGuardianIdentity(int Process, long StartedTicks);

internal sealed record DialogLeaseRecord
{
    public string Token { get; init; } = string.Empty;
    public long NativeWindow { get; init; }
    public uint NativeProcess { get; init; }
    public int WorkerProcess { get; init; }
    public long WorkerStarted { get; init; }
    public int Cookie { get; init; }
    public string Heartbeat { get; init; } = string.Empty;
    public long ProxyWindow { get; init; }
    public bool WasLayered { get; init; }
    public uint OriginalColour { get; init; }
    public byte OriginalAlpha { get; init; } = 255;
    public uint OriginalTransparencyFlags { get; init; } = 2;
}

/// <summary>A window is cloaked only after an independent process has taken
/// responsibility for restoring it. Every lease is tied to the exact HWND,
/// its process and a property cookie, so a reused HWND cannot be touched.</summary>
internal sealed class DialogLease : IDisposable
{
    private readonly EventWaitHandle _heartbeat;
    private bool _disposed;
    public DialogLeaseRecord Record { get; private set; }
    public string Path { get; }
    public string RecoveryPath => Path + ".recovered";
    public int GuardianProcess { get; private set; }
    public long GuardianStartedTicks { get; private set; }

    private DialogLease(nint window, nint proxy)
    {
        var token = Guid.NewGuid().ToString("N");
        var eventName = @"Local\UltraExplorer.DialogBeat." + token;
        _heartbeat = new(false, EventResetMode.AutoReset, eventName);
        var transparency = DialogNative.Transparency(window);
        Record = new()
        {
            Token = token, NativeWindow = window, NativeProcess = DialogNative.ProcessId(window),
            WorkerProcess = Environment.ProcessId, WorkerStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
            Cookie = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue), Heartbeat = eventName,
            WasLayered = transparency.Layered, OriginalColour = transparency.Colour, OriginalAlpha = transparency.Alpha,
            OriginalTransparencyFlags = transparency.Flags, ProxyWindow = proxy
        };
        Directory.CreateDirectory(DialogIntegrationStore.SessionsPath);
        Path = System.IO.Path.Combine(DialogIntegrationStore.SessionsPath, token + ".json");
        try
        {
            var marked = false;
            var error = 0;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (!DialogNative.IsWindow(window) || DialogNative.ProcessId(window) != Record.NativeProcess) break;
                if (DialogNative.GetProp(window, DialogNative.LeaseProperty) != 0) break;
                marked = DialogNative.SetProp(window, DialogNative.LeaseProperty, Record.Cookie);
                error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                if (marked || error != 8) break;
                // Window-property atom allocation can fail transiently while
                // the native chooser is initialising. Never hide on a failure.
                Thread.Sleep(50);
            }
            if (!marked || !BelongsToWindow(Record))
            {
                throw new System.ComponentModel.Win32Exception(error,
                    $"Windows refused the recovery marker (error {error}, window {window:X}). Its dialog has been left visible.");
            }
            Write();
            DialogGuardian.Wake();
        }
        catch
        {
            if (BelongsToWindow(Record)) DialogNative.RemoveProp(window, DialogNative.LeaseProperty);
            _heartbeat.Dispose();
            throw;
        }
    }

    /// <param name="proxy">The replacement's window, when it exists already: written with the lease itself.</param>
    public static async Task<DialogLease> ArmAsync(nint window, nint proxy = 0)
    {
        var lease = new DialogLease(window, proxy);
        try
        {
            DialogGuardian.EnsureRunning();
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(4))
            {
                lease.Pulse();
                if (ReadGuardianIdentity(lease.Path + ".ready") is { } guardian && GuardianAlive(guardian))
                {
                    lease.GuardianProcess = guardian.Process;
                    lease.GuardianStartedTicks = guardian.StartedTicks;
                    return lease;
                }
                // The guardian is woken by the lease itself and answers within
                // a few milliseconds; a guardian only starting takes longer.
                await Task.Delay(clock.ElapsedMilliseconds < 200 ? 4 : 40);
            }
            throw new TimeoutException("The recovery process is unavailable. The Windows dialog has been left visible.");
        }
        catch { lease.Dispose(); throw; }
    }

    /// <summary>The guardian that took the lease, once its ready marker is
    /// there; written whole (a temporary file moved into place), and read
    /// again later if it is being replaced right now.</summary>
    internal static DialogGuardianIdentity? ReadGuardianIdentity(string readyPath)
    {
        try
        {
            using var file = new FileStream(readyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length is <= 0 or > 4096) return null;
            var identity = JsonSerializer.Deserialize<DialogGuardianIdentity>(file);
            return identity is { Process: > 0, StartedTicks: > 0 } ? identity : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static bool GuardianAlive(DialogGuardianIdentity identity)
    {
        if (identity.Process <= 0 || identity.StartedTicks <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(identity.Process);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == identity.StartedTicks
                && string.Equals(process.MainModule?.FileName, DialogSelfProcess.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    public void AttachProxy(nint handle) { Record = Record with { ProxyWindow = handle }; Write(); }
    public void Pulse() { if (!_disposed) _heartbeat.Set(); }

    public bool IsProtected
    {
        get
        {
            return !_disposed && GuardianAlive(new(GuardianProcess, GuardianStartedTicks)) && !File.Exists(RecoveryPath);
        }
    }

    private void Write()
    {
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Record));
        // A file written a moment ago may still be open to whatever scans new
        // files; replacing it is tried again for a little while.
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(temp, Path, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20) { Thread.Sleep(5); }
        }
    }

    public void Restore(bool activate = false) => RestoreRecord(Record, activate);

    internal static bool BelongsToWindow(DialogLeaseRecord record) =>
        record.Cookie > 0 && record.NativeWindow != 0 && DialogNative.IsWindow((nint)record.NativeWindow)
        && DialogNative.ProcessId((nint)record.NativeWindow) == record.NativeProcess
        && DialogNative.GetProp((nint)record.NativeWindow, DialogNative.LeaseProperty) == record.Cookie;

    internal static void RestoreRecord(DialogLeaseRecord record, bool activate = false)
    {
        if (!BelongsToWindow(record)) return;
        DialogNative.RestoreTransparency((nint)record.NativeWindow, record.WasLayered, record.OriginalColour, record.OriginalAlpha, record.OriginalTransparencyFlags);
        DialogNative.Restore((nint)record.NativeWindow, activate);
        if (activate) DialogNative.BringOwnedPromptForward((nint)record.NativeWindow);
    }

    /// <summary>Whether the UltraExplorer process that wrote the lease is still
    /// the same running process (its id may have been reused since).</summary>
    internal static bool WorkerAlive(DialogLeaseRecord record)
    {
        try
        {
            using var worker = Process.GetProcessById(record.WorkerProcess);
            return !worker.HasExited && worker.StartTime.ToUniversalTime().Ticks == record.WorkerStarted;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>
    /// The lease's JSON written and read once, in memory, so that the first
    /// real lease does not also pay for the serializer's first use (some
    /// twenty milliseconds, on the way to "ready to accept"). By the guardian
    /// as it starts, and by a prepared worker before it says it is ready.
    /// </summary>
    internal static void WarmUp()
    {
        try
        {
            var text = JsonSerializer.Serialize(new DialogLeaseRecord { Token = Guid.NewGuid().ToString("N") });
            _ = JsonSerializer.Deserialize<DialogLeaseRecord>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)));
        }
        catch (JsonException) { }
    }

    internal static DialogLeaseRecord? ReadRecord(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 4096) return null;
            var record = JsonSerializer.Deserialize<DialogLeaseRecord>(stream);
            if (record is null || record.Token is not { Length: 32 } || !Guid.TryParseExact(record.Token, "N", out _)
                || System.IO.Path.GetFileName(path) != record.Token + ".json"
                || record.Heartbeat != @"Local\UltraExplorer.DialogBeat." + record.Token) return null;
            return record;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Restore();
        if (BelongsToWindow(Record)) DialogNative.RemoveProp((nint)Record.NativeWindow, DialogNative.LeaseProperty);
        _heartbeat.Dispose();
        foreach (var suffix in new[] { "", ".ready", ".ready.tmp", ".recovered", ".tmp" })
            try { File.Delete(Path + suffix); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

internal static class DialogGuardian
{
    internal static string MutexName => @"Local\UltraExplorer.DialogGuardian." + DialogIntegrationStore.InstanceKey;

    /// <summary>Set by a new lease: the guardian looks at once instead of at its next tenth of a second.</summary>
    private static string WakeName => @"Local\UltraExplorer.DialogGuardianWake." + DialogIntegrationStore.InstanceKey;

    private static EventWaitHandle? CreateWake()
    {
        try { return new EventWaitHandle(false, EventResetMode.AutoReset, WakeName); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            DialogIntegrationStore.Log("The guardian's wake signal is unavailable; it looks for new leases every 100 ms", ex);
            return null;
        }
    }

    internal static void Wake()
    {
        try { if (EventWaitHandle.TryOpenExisting(WakeName, out var wake)) using (wake) wake.Set(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException) { }
    }

    public static void EnsureRunning()
    {
        if (Mutex.TryOpenExisting(MutexName, out var existing)) { existing.Dispose(); return; }
        using var process = DialogSelfProcess.Start("--dialog-guardian");
    }

    /// <summary>More lease files than this in one folder can only be litter;
    /// a pass never reads further, and the litter is removed as it is read.</summary>
    private const int LeaseScanLimit = 512;

    /// <summary>Litter younger than this may still be in the middle of being written.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(1);

    public static void RestoreAll(bool disable, string reason)
    {
        try
        {
            if (Directory.Exists(DialogIntegrationStore.SessionsPath))
                foreach (var path in Directory.EnumerateFiles(DialogIntegrationStore.SessionsPath, "*.json").Take(LeaseScanLimit).ToArray())
                {
                    if (DialogLease.ReadRecord(path) is not { } record || !DialogLease.BelongsToWindow(record))
                    {
                        if (Age(path) > StaleAfter) Discard(path);
                        continue;
                    }
                    DialogLease.RestoreRecord(record, true);
                    try { File.WriteAllText(path + ".recovered", reason); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { DialogIntegrationStore.Log("Could not mark a restored dialog", ex); }
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { DialogIntegrationStore.Log("Could not enumerate dialog leases", ex); }
        finally
        {
            // Pausing what is already off changes nothing, and writes nothing:
            // an uninstall's --dialog-recover leaves no trace of a mode never used.
            if (disable && DialogIntegrationStore.Read().Enabled)
                DialogIntegrationStore.Update(settings => settings with { Enabled = false, LastRecovery = reason });
        }
    }

    /// <summary>
    /// Run at every start of UltraExplorer and of its listener, and by the
    /// listener as it goes. A dialog whose replacement process is gone - it
    /// crashed, was ended, the session was logging off - is put back as the
    /// application left it, even when the watchdog that should have done that
    /// is gone too. A lease whose replacement is still running is left to it
    /// and its watchdog. Lease files of windows that no longer exist, and
    /// half-written ones, are removed so they can never pile up.
    /// </summary>
    /// <returns>How many dialogs were put back.</returns>
    public static int Heal()
    {
        var healed = 0;
        try
        {
            if (Directory.Exists(DialogIntegrationStore.SessionsPath))
            {
                foreach (var path in Directory.EnumerateFiles(DialogIntegrationStore.SessionsPath).Take(LeaseScanLimit * 4).ToArray())
                {
                    if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        // .ready, .recovered, .tmp: kept only beside their lease.
                        var lease = LeaseOf(path);
                        if ((lease is null || !File.Exists(lease)) && Age(path) > StaleAfter) TryDelete(path);
                        continue;
                    }
                    if (DialogLease.ReadRecord(path) is not { } record)
                    {
                        if (Age(path) > StaleAfter) Discard(path);
                        continue;
                    }
                    if (DialogLease.WorkerAlive(record)) continue;
                    if (DialogLease.BelongsToWindow(record))
                    {
                        DialogLease.RestoreRecord(record);
                        DialogNative.RemoveProp((nint)record.NativeWindow, DialogNative.LeaseProperty);
                        healed++;
                        DialogIntegrationStore.Log("A dialog left hidden by a replacement that is no longer running was put back.");
                    }
                    Discard(path);
                }
            }

            // A replacement's canvas is never saved; anything of it left behind
            // by one that crashed is removed once it is certainly not in use.
            if (Directory.Exists(DialogIntegrationStore.DirectoryPath))
                foreach (var path in Directory.EnumerateFiles(DialogIntegrationStore.DirectoryPath, "proxy-*").Take(LeaseScanLimit).ToArray())
                    if (Age(path) > TimeSpan.FromHours(12)) TryDelete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { DialogIntegrationStore.Log("Could not check dialog leases", ex); }
        return healed;
    }

    private static string? LeaseOf(string path)
    {
        foreach (var suffix in new[] { ".ready.tmp", ".ready", ".recovered", ".tmp" })
            if (path.EndsWith(".json" + suffix, StringComparison.OrdinalIgnoreCase)) return path[..^suffix.Length];
        return null;
    }

    private static TimeSpan Age(string path)
    {
        try { return DateTime.UtcNow - File.GetLastWriteTimeUtc(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return TimeSpan.Zero; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static Task RunAsync() => Task.Factory.StartNew(Run, CancellationToken.None,
        TaskCreationOptions.LongRunning, TaskScheduler.Default);

    // A Windows mutex is thread-affine. Keep its ownership on this dedicated
    // thread, rather than holding it across async continuations on pool threads.
    private static void Run()
    {
        using var ownProcess = Process.GetCurrentProcess();
        var identity = new DialogGuardianIdentity(ownProcess.Id, ownProcess.StartTime.ToUniversalTime().Ticks);
        using var mutex = new Mutex(false, MutexName);
        // Optional: without it the guardian looks every tenth of a second, as it always did.
        using var wake = CreateWake();
        var entered = false;
        var active = new Dictionary<string, ProtectedDialog>();
        try
        {
            try { entered = mutex.WaitOne(0); } catch (AbandonedMutexException) { entered = true; }
            if (!entered) return;
            DialogLease.WarmUp();
            Heal();
            Directory.CreateDirectory(DialogIntegrationStore.SessionsPath);
            var turn = Environment.TickCount64;
            while (true)
            {
                // The clock goes on while the computer sleeps or hibernates,
                // and while this process is held up otherwise; a replacement
                // cannot answer a watchdog that is not looking. Of the time
                // between two turns of this loop, only a second counts against
                // it - waking up is not mistaken for a replacement that stopped,
                // and a watchdog that only runs slowly still ends a hung one.
                // From the start of one turn to the start of the next, so that
                // a hold anywhere in a turn is counted here too; a heartbeat
                // taken after such a hold is never moved past the present.
                var now = Environment.TickCount64;
                if (now - turn > 1000) foreach (var protection in active.Values) protection.LastBeat = Math.Min(protection.LastBeat + now - turn - 1000, now);
                turn = now;
                var enabled = DialogIntegrationStore.Read().Enabled;
                if (!enabled && active.Count == 0) break;
                foreach (var path in Directory.EnumerateFiles(DialogIntegrationStore.SessionsPath, "*.json").Take(LeaseScanLimit).ToArray())
                {
                    if (active.ContainsKey(path)) continue;
                    var record = DialogLease.ReadRecord(path);
                    if (record is null || !DialogLease.BelongsToWindow(record)) { Discard(path); continue; }
                    if (File.Exists(path + ".recovered"))
                    {
                        if (DialogLease.WorkerAlive(record)) continue;
                        DialogLease.RestoreRecord(record);
                        DialogNative.RemoveProp((nint)record.NativeWindow, DialogNative.LeaseProperty);
                        Discard(path);
                        continue;
                    }
                    try
                    {
                        var protection = new ProtectedDialog(record);
                        active.Add(path, protection);
                        // Whole or not at all: the proxy reads it the moment it appears.
                        File.WriteAllText(path + ".ready.tmp", JsonSerializer.Serialize(identity));
                        File.Move(path + ".ready.tmp", path + ".ready", true);
                    }
                    catch (Exception ex) when (ex is ArgumentException or WaitHandleCannotBeOpenedException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        var wasHidden = DialogNative.IsHidden((nint)record.NativeWindow);
                        DialogLease.RestoreRecord(record, true);
                        if (DialogLease.BelongsToWindow(record)) DialogNative.RemoveProp((nint)record.NativeWindow, DialogNative.LeaseProperty);
                        Discard(path);
                        DialogIntegrationStore.Log("A recovery lease could not be armed", ex);
                        if (wasHidden) Pause("A recovery lease was unavailable. Windows dialogs are restored and replacement is paused.");
                    }
                }

                foreach (var (path, protection) in active.ToArray())
                {
                    var record = protection.Record;
                    if (!File.Exists(path) || !DialogLease.BelongsToWindow(record))
                    { active.Remove(path); protection.Dispose(); continue; }
                    if (protection.Beat.WaitOne(0)) protection.LastBeat = Environment.TickCount64;
                    var hidden = DialogNative.IsHidden((nint)record.NativeWindow);
                    // Building a graph and WPF window can take longer than the
                    // heartbeat interval. The original is still usable then;
                    // the timeout starts only after it is actually hidden.
                    if (!hidden)
                    {
                        protection.LastBeat = Environment.TickCount64;
                        if (protection.Worker.HasExited)
                        {
                            DialogNative.RemoveProp((nint)record.NativeWindow, DialogNative.LeaseProperty);
                            Discard(path);
                            active.Remove(path);
                            protection.Dispose();
                            continue;
                        }
                    }
                    var exited = protection.Worker.HasExited;
                    // At the turn's own time: a hold since then is the next turn's to count.
                    var expired = hidden && (exited || now - protection.LastBeat > 5000);
                    var prompt = hidden && !DialogNative.IsWindowEnabled((nint)record.NativeWindow);
                    if (!enabled || expired || prompt)
                    {
                        var reason = expired ? (exited ? "A replacement dialog closed unexpectedly." : "A replacement dialog stopped responding.")
                                + " The Windows dialog is back and replacement is paused."
                            : prompt ? "The application opened additional native options. Continue in its Windows dialog."
                            : "Windows dialogs restored.";
                        DialogLease.RestoreRecord(record, true);
                        DialogIntegrationStore.Log(reason);
                        try { File.WriteAllText(path + ".recovered", reason); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { DialogIntegrationStore.Log("Could not mark a recovered dialog", ex); }
                        if (expired && !protection.Worker.HasExited)
                        {
                            // Only the exact UltraExplorer proxy that registered
                            // this lease, never the application whose dialog it owns.
                            try { protection.Worker.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                        }
                        // After the replacement is ended: pausing writes the
                        // settings, which can wait on another process or fail.
                        if (expired) Pause(reason);
                        active.Remove(path);
                        protection.Dispose();
                    }
                }
                if (wake is null) Thread.Sleep(100);
                else wake.WaitOne(100);
            }
        }
        finally
        {
            foreach (var protection in active.Values) { DialogLease.RestoreRecord(protection.Record, true); protection.Dispose(); }
            if (entered) mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// Replacement paused after a recovery. Settings another process holds for
    /// longer than the lock waits, or cannot be written, are logged: the
    /// watchdog itself goes on, since other dialogs may still depend on it.
    /// </summary>
    private static void Pause(string reason)
    {
        try { DialogIntegrationStore.Update(settings => settings with { Enabled = false, LastRecovery = reason }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { DialogIntegrationStore.Log("Could not pause replacement after a recovery", ex); }
    }

    private static void Discard(string path)
    {
        foreach (var suffix in new[] { "", ".ready", ".ready.tmp", ".recovered", ".tmp" })
            try { File.Delete(path + suffix); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed class ProtectedDialog : IDisposable
    {
        public DialogLeaseRecord Record { get; }
        public Process Worker { get; }
        public EventWaitHandle Beat { get; }
        public long LastBeat { get; set; } = Environment.TickCount64;
        public ProtectedDialog(DialogLeaseRecord record)
        {
            Record = record;
            Worker = Process.GetProcessById(record.WorkerProcess);
            if (Worker.StartTime.ToUniversalTime().Ticks != record.WorkerStarted) { Worker.Dispose(); throw new ArgumentException("A worker process was reused."); }
            if (!string.Equals(Worker.MainModule?.FileName, DialogSelfProcess.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            { Worker.Dispose(); throw new ArgumentException("This is not an UltraExplorer proxy process."); }
            try { Beat = EventWaitHandle.OpenExisting(record.Heartbeat); }
            catch { Worker.Dispose(); throw; }
        }
        public void Dispose() { Beat.Dispose(); Worker.Dispose(); }
    }
}
