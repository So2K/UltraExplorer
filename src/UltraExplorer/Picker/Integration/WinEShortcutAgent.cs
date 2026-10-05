using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using UltraExplorer.Infrastructure;
using UltraExplorer.Services;

namespace UltraExplorer.Picker.Integration;

internal sealed record WinEShortcutStatus(int Process, long Started, bool Ready, string? Error);

internal static class WinEShortcutAgent
{
    internal static void RecordFailure(string error) => DialogIntegrationStore.Update(settings => settings with
    {
        WinEEnabled = false,
        WinELastError = $"Win+E stopped ({error}). Windows keeps the shortcut."
    });

    private static string MutexName => @"Local\UltraExplorer.WinEShortcut." + DialogIntegrationStore.InstanceKey;
    private static string StatusPath => AppPaths.State("wine-shortcut-status.json");
    internal static bool IsRunning
    {
        get { if (!Mutex.TryOpenExisting(MutexName, out var existing)) return false; existing.Dispose(); return true; }
    }
    internal static bool IsReady
    {
        get
        {
            try
            {
                if (!IsRunning || !File.Exists(StatusPath)) return false;
                using var file = new FileStream(StatusPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length > 4096) return false;
                var status = JsonSerializer.Deserialize<WinEShortcutStatus>(file);
                if (status is not { Ready: true, Process: > 0 }) return false;
                using var process = Process.GetProcessById(status.Process);
                return !process.HasExited && process.StartTime.ToUniversalTime().ToFileTimeUtc() == status.Started;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        }
    }

    internal static async Task RunAsync(Application app)
    {
        if (!WinEShortcutStartup.IsAllowed || DialogIntegrationStore.Read().WinEEnabled != true) return;
        using var mutex = new Mutex(false, MutexName);
        bool owned; try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) return;
        using var process = Process.GetCurrentProcess();
        var identity = new WinEShortcutStatus(process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc(), false, null);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new WinExplorerShortcutService(() =>
        {
            if (DialogIntegrationStore.Read().WinEEnabled != true) return;
            try { using var launched = DialogSelfProcess.Start(FolderCommandLine.HomeSwitch); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                DialogIntegrationStore.Log("The Win+E folder window could not start", ex);
                throw; // The service releases the shortcut and reports the launch failure.
            }
        });
        var poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        try
        {
            if ((await ReconcileAtStartAsync(DialogIntegrationStore.ReconcileRegistrations, TimeSpan.FromSeconds(1))).WinEEnabled != true) return;
            if (!service.Start())
            {
                Write(identity with { Error = service.LastError });
                DialogIntegrationStore.Update(settings => settings with { WinEEnabled = false,
                    WinELastError = $"Win+E could not be registered ({service.LastError}). Windows keeps the shortcut." });
                return;
            }
            Write(identity with { Ready = true });
            DialogIntegrationStore.Log("Independent Win+E shortcut is ready.");
            poll.Tick += (_, _) =>
            {
                if (DialogIntegrationStore.Read().WinEEnabled != true || !service.IsReady)
                {
                    // A busy settings lock must not end this process from a timer
                    // tick; the hook is gone either way and the listener stops.
                    if (!service.IsReady && DialogIntegrationStore.Read().WinEEnabled == true)
                        try
                        {
                            DialogIntegrationStore.Update(settings => settings with { WinEEnabled = false,
                                WinELastError = service.LastError ?? "The Win+E shortcut stopped. Windows keeps the shortcut." });
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { DialogIntegrationStore.Log("The Win+E failure could not be saved", ex); }
                    service.Dispose();
                    done.TrySetResult();
                }
            };
            poll.Start();
            await done.Task;
        }
        finally
        {
            poll.Stop();
            service.Dispose();
            Write(identity with { Ready = false, Error = service.LastError });
            mutex.ReleaseMutex();
        }
    }

    /// <summary>Sign-in starts every role at once, and each takes the settings
    /// lock in turn: a wait for it that runs out is tried again, and is not Win+E
    /// failing. Only a lasting failure reaches the caller, which switches it off.</summary>
    internal static async Task<DialogIntegrationSettings> ReconcileAtStartAsync(Func<DialogIntegrationSettings> reconcile, TimeSpan pause)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return reconcile(); }
            catch (IOException ex) when (attempt < 3)
            {
                DialogIntegrationStore.Log("The Win+E listener waits for the integration settings", ex);
                await Task.Delay(pause);
            }
        }
    }

    private static void Write(WinEShortcutStatus status)
    {
        Directory.CreateDirectory(AppPaths.StateDirectory);
        var temporary = StatusPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(status)); File.Move(temporary, StatusPath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal static class WinEShortcutStartup
{
    internal const string ValueName = "UltraExplorer Win+E";
    internal static bool IsAllowed => DialogStartup.IsAllowed;
    internal static void Reconcile(bool enabled)
    {
        if (!IsAllowed) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(DialogStartup.RunKey, writable: true);
            if (enabled)
            {
                if (!KeepsRegistered(key.GetValue(ValueName) as string))
                    key.SetValue(ValueName, "\"" + DialogSelfProcess.ExecutablePath + "\" --shortcut-agent", RegistryValueKind.String);
            }
            else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { DialogIntegrationStore.Log("The Win+E sign-in setting could not be changed", ex); }
    }

    /// <summary>Whether a sign-in entry already there stays, as
    /// <see cref="DialogStartup.Reconcile"/> leaves the dialog listener's: one that
    /// starts the Win+E listener of an executable that exists. Another copy
    /// starting (a build folder, a portable zip) is not the user choosing it, and
    /// its entry would name an executable that goes with the next rebuild.</summary>
    internal static bool KeepsRegistered(string? registered) =>
        registered is not null && registered.TrimEnd().EndsWith("\" --shortcut-agent", StringComparison.Ordinal)
        && DialogStartup.RegisteredExecutableExists(registered);
}
