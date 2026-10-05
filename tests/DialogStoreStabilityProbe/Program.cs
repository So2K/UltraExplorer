using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;

internal static class Program
{
    private static int _checks;
    private static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "--owned-client") return await ClientChild(args);
        var ownParent = Path.Combine(Path.GetTempPath(), "UltraExplorerDialogStoreProbe");
        var ownRoot = Path.Combine(ownParent, Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(AppPaths.StateDirectoryVariable, ownRoot);
        Directory.CreateDirectory(ownRoot);
        try
        {
            if (DialogStartup.IsAllowed || AppPaths.StateDirectory != ownRoot) throw new InvalidOperationException("Probe isolation failed.");
            await ReconciliationProbe();
            Win32FailureProbe();
            GuardianIdentityProbe(ownRoot);
            await ClientStoreProbe(ownRoot);
            await CrossProcessClients(ownRoot);
            await MutexFailureChecks(ownRoot);
            UnreadableClientStateChecks(ownRoot);
            UnreadableSettingsChecks();
            Console.WriteLine($"PASS: {_checks} dialog/store regression assertions; isolated state and owned child processes, no native UI/registry/input actions.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            ShellReplacementRegistration.OnReconcile = null;
            var resolved = Path.GetFullPath(ownRoot);
            if (!string.Equals(Path.GetDirectoryName(resolved), Path.GetFullPath(ownParent), StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _)) throw new InvalidOperationException("Unexpected probe cleanup target.");
            Directory.Delete(resolved, true);
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        _checks++;
        Console.WriteLine("  ok " + name);
    }

    private static void InitialSettings() => File.WriteAllText(DialogIntegrationStore.SettingsPath,
        JsonSerializer.Serialize(new DialogIntegrationSettings { Enabled = false, WinEEnabled = false }));

    private static async Task ReconciliationProbe()
    {
        InitialSettings();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        ShellReplacementRegistration.OnReconcile = enabled =>
        {
            if (!enabled) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Owned reconciliation stall timed out.");
        };
        var first = Task.Run(() => DialogIntegrationStore.Update(settings => settings with { Enabled = true, WinEEnabled = true }));
        if (!entered.Wait(TimeSpan.FromSeconds(3))) throw new InvalidOperationException("First update did not reach registration seam.");
        var second = Task.Run(() => DialogIntegrationStore.Update(settings => settings with { Enabled = false, WinEEnabled = false }));
        var overlapped = await Task.WhenAny(second, Task.Delay(1000)) == second;
        release.Set();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(6));
        ShellReplacementRegistration.OnReconcile = null;
        var durable = DialogIntegrationStore.Read();
        Check(!overlapped, "a second settings write waits for the first registration reconciliation");
        Check(!durable.Enabled && !ShellReplacementRegistration.Applied && durable.WinEEnabled == false && !WinEShortcutStartup.Applied,
            "latest durable OFF and both registrations agree after concurrent updates");
        ShellReplacementRegistration.Applied = true;
        WinEShortcutStartup.Applied = true;
        var repaired = DialogIntegrationStore.ReconcileRegistrations();
        Check(!repaired.Enabled && !ShellReplacementRegistration.Applied && !WinEShortcutStartup.Applied,
            "startup repair reads the current preference while holding the same lock");
    }

    private static void Win32FailureProbe()
    {
        InitialSettings();
        ShellReplacementRegistration.OnReconcile = _ => throw new Win32Exception(5, "Owned registry facade failure");
        var escaped = false;
        try { DialogIntegrationStore.Update(settings => settings with { Enabled = true }); }
        catch (Win32Exception) { escaped = true; }
        finally { ShellReplacementRegistration.OnReconcile = null; }
        Check(!escaped, "an expected raw registry Win32Exception does not escape recovery settings writes");
        InitialSettings();
        ShellReplacementRegistration.OnReconcile = _ => throw new Win32Exception(5, "Owned strict-enable failure");
        var strictFailed = false;
        try { DialogIntegrationStore.Update(settings => settings with { Enabled = true }, requireFolderRegistration: true); }
        catch (Win32Exception) { strictFailed = true; }
        finally { ShellReplacementRegistration.OnReconcile = null; }
        Check(strictFailed && !DialogIntegrationStore.Read().Enabled, "strict UI enable refuses a failed association before persisting ON");

        InitialSettings();
        var rolledBack = false;
        ShellReplacementRegistration.OnReconcile = enabled =>
        {
            if (enabled) { File.Delete(DialogIntegrationStore.SettingsPath); Directory.CreateDirectory(DialogIntegrationStore.SettingsPath); }
            else rolledBack = true;
        };
        var writeFailed = false;
        try { DialogIntegrationStore.Update(settings => settings with { Enabled = true }, requireFolderRegistration: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { writeFailed = true; }
        finally { ShellReplacementRegistration.OnReconcile = null; Directory.Delete(DialogIntegrationStore.SettingsPath); }
        Check(writeFailed && rolledBack && !ShellReplacementRegistration.Applied,
            "failed durable write after strict registration rolls associations back under the settings lock");
    }

    private static void GuardianIdentityProbe(string ownRoot)
    {
        using var own = Process.GetCurrentProcess();
        var identity = new DialogGuardianIdentity(own.Id, own.StartTime.ToUniversalTime().Ticks);
        var ready = Path.Combine(ownRoot, "never-armed.ready");
        Check(DialogLease.GuardianAlive(identity), "a matching owned process start identity is alive");
        Check(!DialogLease.GuardianAlive(identity with { StartedTicks = identity.StartedTicks + 1 }), "same live PID with wrong start cannot protect a lease");
        Check(!DialogLease.GuardianAlive(new(0, identity.StartedTicks)) && !DialogLease.GuardianAlive(new(identity.Process, 0)), "missing guardian PID/start is rejected");
        Check(DialogLease.ReadGuardianIdentity(ready) is null, "a missing ready marker does not arm a lease");
        foreach (var malformed in new[] { "1", "{}", "null", "[]", "{\"Process\":0,\"StartedTicks\":1}", "{\"Process\":1,\"StartedTicks\":0}", new string('x', 4097) })
        {
            File.WriteAllText(ready, malformed);
            Check(DialogLease.ReadGuardianIdentity(ready) is null, "malformed/legacy/unbounded guardian marker is rejected");
        }
        File.WriteAllText(ready, JsonSerializer.Serialize(identity));
        Check(DialogLease.ReadGuardianIdentity(ready) == identity, "ready marker preserves PID and UTC start ticks exactly");
        using var dead = StartOwnedChild("--owned-client", ready, Guid.NewGuid().ToString("D"), "exit", ownRoot);
        dead.WaitForExit();
        Check(!DialogLease.GuardianAlive(new(dead.Id, identity.StartedTicks)), "an exited owned PID is rejected without escaping a process error");
    }

    private static async Task ClientStoreProbe(string ownRoot)
    {
        const int count = 12;
        ThreadPool.GetMinThreads(out var oldWorkers, out var io);
        ThreadPool.SetMinThreads(Math.Max(oldWorkers, count + 2), io);
        try
        {
            for (var run = 0; run < 3; run++)
            {
                var path = Path.Combine(ownRoot, "clients-" + run + ".json");
                var seed = Enumerable.Range(0, 512).ToDictionary(_ => Guid.NewGuid().ToString("D"),
                    _ => new FileDialogClientState("Owned seed", 1, [new string('x', 2048)]));
                File.WriteAllText(path, JsonSerializer.Serialize(seed));
                var clients = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
                using var barrier = new Barrier(count);
                var saves = clients.Select((client, index) => Task.Run(() =>
                {
                    if (!barrier.SignalAndWait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Owned writer barrier timed out.");
                    new FileDialogClientStore(path).Save(client, new("Owned caller " + index, 1));
                })).ToArray();
                await Task.WhenAll(saves).WaitAsync(TimeSpan.FromSeconds(15));
                var store = new FileDialogClientStore(path);
                var retained = clients.Count(client => store.Load(client) is not null);
                var litter = Directory.GetFiles(ownRoot, "clients-" + run + ".json.*.tmp").Length;
                Check(retained == count, $"independent simultaneous client stores retain all {count} callers, run {run}");
                Check(litter == 0, "client stores leave no temporary file litter");
            }
        }
        finally { ThreadPool.SetMinThreads(oldWorkers, io); }
    }

    private static Process StartOwnedChild(params string[] args)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in args) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("Owned probe child did not start.");
    }

    private static async Task<int> ClientChild(string[] args)
    {
        if (args.Length != 5) return 2;
        if (args[3] == "exit") return 0;
        var path = Path.GetFullPath(args[1]);
        var root = Path.GetFullPath(args[4]);
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UltraExplorerDialogStoreProbe"));
        if (Path.GetDirectoryName(root) != expectedParent || !Guid.TryParseExact(Path.GetFileName(root), "N", out _)
            || Path.GetDirectoryName(path) != root || !Guid.TryParse(args[2], out var client)) return 2;
        File.WriteAllText(Path.Combine(root, client.ToString("N") + ".child-ready"), "ready");
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(root, "children-go")))
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(10)) return 3;
            await Task.Delay(10);
        }
        if (args[3] == "save") new FileDialogClientStore(path).Save(client, new("Owned cross-process caller", 1));
        else new FileDialogClientStore(path).Clear(client);
        return 0;
    }

    private static async Task CrossProcessClients(string ownRoot)
    {
        var path = Path.Combine(ownRoot, "cross-process.json");
        var retained = Guid.NewGuid();
        var cleared = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        var saved = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        var store = new FileDialogClientStore(path);
        store.Save(retained, new("Owned sentinel", 1));
        foreach (var client in cleared) store.Save(client, new("Owned clear target", 1));
        var children = new List<Process>();
        try
        {
            foreach (var client in saved) children.Add(StartOwnedChild("--owned-client", path, client.ToString("D"), "save", ownRoot));
            foreach (var client in cleared) children.Add(StartOwnedChild("--owned-client", path, client.ToString("D"), "clear", ownRoot));
            var all = saved.Concat(cleared).ToArray();
            var ready = Stopwatch.StartNew();
            while (all.Any(client => !File.Exists(Path.Combine(ownRoot, client.ToString("N") + ".child-ready"))))
            {
                if (ready.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Owned children did not reach their file barrier.");
                await Task.Delay(10);
            }
            File.WriteAllText(Path.Combine(ownRoot, "children-go"), "go");
            await Task.WhenAll(children.Select(child => child.WaitForExitAsync())).WaitAsync(TimeSpan.FromSeconds(10));
            Check(children.All(child => child.ExitCode == 0), "all nine owned writer/clear processes finish successfully");
            Check(saved.All(client => store.Load(client) is not null), "six independent processes retain every caller update");
            Check(cleared.All(client => store.Load(client) is null), "three simultaneous clear processes do not resurrect cleared callers");
            Check(store.Load(retained)?.Folder == "Owned sentinel", "concurrent cross-process mutations preserve unrelated client state");
            Check(Directory.GetFiles(ownRoot, "cross-process.json.*.tmp").Length == 0, "cross-process mutations leave no temporary litter");
        }
        finally
        {
            foreach (var child in children)
            {
                if (!child.HasExited) { child.Kill(); child.WaitForExit(1000); }
                child.Dispose();
            }
        }
    }

    private static async Task MutexFailureChecks(string ownRoot)
    {
        var settingsMutex = @"Local\UltraExplorer.DialogSettings." + DialogIntegrationStore.InstanceKey;
        using (var keptAlive = new Mutex(false, settingsMutex))
        {
            var abandonedOwner = new Thread(() => { using var held = Mutex.OpenExisting(settingsMutex); held.WaitOne(); });
            abandonedOwner.Start();
            if (!abandonedOwner.Join(1000)) throw new TimeoutException("Owned abandoned-mutex thread did not exit.");
            DialogIntegrationStore.Update(settings => settings with { Enabled = false });
            Check(!DialogIntegrationStore.Read().Enabled, "settings update recovers an abandoned owned mutex");
        }
        var path = Path.Combine(ownRoot, "mutex-clients.json");
        var store = new FileDialogClientStore(path);
        var clientMutex = (string)typeof(FileDialogClientStore).GetField("_mutexName", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        using (var keptAlive = new Mutex(false, clientMutex))
        {
            var abandonedOwner = new Thread(() => { using var held = Mutex.OpenExisting(clientMutex); held.WaitOne(); });
            abandonedOwner.Start();
            if (!abandonedOwner.Join(1000)) throw new TimeoutException("Owned abandoned client-mutex thread did not exit.");
            var client = Guid.NewGuid();
            store.Save(client, new("Owned after abandonment", 1));
            Check(store.Load(client)?.Folder == "Owned after abandonment", "client save recovers an abandoned owned mutex");
        }
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var held = new Mutex(false, clientMutex);
            held.WaitOne();
            entered.Set();
            try { release.Wait(TimeSpan.FromSeconds(8)); }
            finally { held.ReleaseMutex(); }
        });
        holder.Start();
        if (!entered.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("Owned held-mutex thread did not enter.");
        var refused = Guid.NewGuid();
        try
        {
            await Task.Run(() => store.Save(refused, new("Owned timed-out write", 1))).WaitAsync(TimeSpan.FromSeconds(5));
            Check(store.Load(refused) is null, "a client mutex timeout leaves the previous state untouched and does not fail the dialog");
        }
        finally { release.Set(); holder.Join(1000); }
        store.Save(refused, new("Owned after release", 1));
        Check(store.Load(refused)?.Folder == "Owned after release", "a client mutex timeout does not prevent a later save");
    }

    private static void UnreadableClientStateChecks(string ownRoot)
    {
        var path = Path.Combine(ownRoot, "blocked-clients.json");
        var store = new FileDialogClientStore(path);
        var oldClients = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var client in oldClients) store.Save(client, new("Owned preserved client", 1));
        var before = File.ReadAllBytes(path);
        var newer = Guid.NewGuid();
        // Existing content cannot be read, but File.Move replacement would be
        // permitted by this handle. Treating a read error as empty lost clients.
        using (var blocker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete))
        {
            Check(store.Load(oldClients[0]) is null, "read-only load safely reports unavailable locked client state");
            store.Save(newer, new("Owned after failed read", 1));
            store.Clear(oldClients[1]);
        }
        Check(File.ReadAllBytes(path).SequenceEqual(before) && oldClients.All(client => store.Load(client) is not null)
            && store.Load(newer) is null, "failed mutation reads preserve the entire existing client cache byte for byte");
        store.Save(newer, new("Owned after release", 1));
        Check(store.Load(newer)?.Folder == "Owned after release" && oldClients.All(client => store.Load(client) is not null),
            "client saving resumes after read access returns and preserves old callers");
        Check(Directory.GetFiles(ownRoot, "blocked-clients.json.*.tmp").Length == 0, "failed client reads create no temporary litter");
    }

    private static void UnreadableSettingsChecks()
    {
        DialogIntegrationStore.Update(settings => settings with
        { Enabled = true, WinEEnabled = true, ExcludedApplications = ["Owned exclusion sentinel"] });
        var before = File.ReadAllBytes(DialogIntegrationStore.SettingsPath);
        var registrationBefore = ShellReplacementRegistration.Applied;
        var shortcutBefore = WinEShortcutStartup.Applied;
        var refused = false;
        using (var blocker = new FileStream(DialogIntegrationStore.SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete))
        {
            Check(!DialogIntegrationStore.Read().Enabled, "read-only settings access falls back safely while the owned file is unreadable");
            try { DialogIntegrationStore.Update(settings => settings with { LastRecovery = "Owned attempted update" }); }
            catch (IOException) { refused = true; }
        }
        var preserved = DialogIntegrationStore.Read();
        Check(refused && File.ReadAllBytes(DialogIntegrationStore.SettingsPath).SequenceEqual(before)
            && preserved.Enabled && preserved.WinEEnabled == true && preserved.ExcludedApplications.SequenceEqual(["Owned exclusion sentinel"]),
            "failed mutation settings read preserves both toggles, exclusions and exact bytes");
        Check(ShellReplacementRegistration.Applied == registrationBefore && WinEShortcutStartup.Applied == shortcutBefore,
            "a refused settings mutation applies no stale registration effects");
        DialogIntegrationStore.Update(settings => settings with { LastRecovery = "Owned after access returned" });
        Check(DialogIntegrationStore.Read() is { Enabled: true, WinEEnabled: true, LastRecovery: "Owned after access returned" },
            "settings writes resume after access returns without resetting preferences");
    }
}
