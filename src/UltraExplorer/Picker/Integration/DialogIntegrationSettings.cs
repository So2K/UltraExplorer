using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using UltraExplorer.Infrastructure;

namespace UltraExplorer.Picker.Integration;

internal sealed record DialogIntegrationSettings
{
    public bool Enabled { get; init; }
    public bool? WinEEnabled { get; init; }
    public string WinELastError { get; init; } = string.Empty;
    public string[] ExcludedApplications { get; init; } = [];
    public string LastRecovery { get; init; } = string.Empty;
}

/// <summary>Separate from the canvas workspace: a watchdog can switch integration
/// off even when every UI process has stopped responding.</summary>
internal static class DialogIntegrationStore
{
    internal static string DirectoryPath => AppPaths.State("dialog-integration");
    internal static string SessionsPath => Path.Combine(DirectoryPath, "sessions");
    internal static string SettingsPath => AppPaths.State("dialog-integration.json");
    internal static string LogPath => Path.Combine(DirectoryPath, "integration.log");

    /// <summary>The log is for finding out why a dialog went back to Windows,
    /// not a trace: one line per replacement and per recovery. Past this size
    /// it becomes <c>integration.log.old</c>, so both together stay under 512 KB.</summary>
    internal const long LogLimit = 256 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static string InstanceKey { get; } = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            WindowsIdentity.GetCurrent().User?.Value + "|" + AppPaths.StateDirectory.ToUpperInvariant())))[..24];

    public static DialogIntegrationSettings Read() => ReadCore(preserveOnReadFailure: false);

    private static DialogIntegrationSettings ReadCore(bool preserveOnReadFailure)
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new() { WinEEnabled = false };
            using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 64 * 1024) return new() { WinEEnabled = false };
            var settings = JsonSerializer.Deserialize<DialogIntegrationSettings>(stream, Json) ?? new();
            return settings with { WinEEnabled = settings.WinEEnabled ?? settings.Enabled, WinELastError = settings.WinELastError ?? string.Empty,
                ExcludedApplications = (settings.ExcludedApplications ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Take(128).ToArray(),
                LastRecovery = settings.LastRecovery ?? string.Empty };
        }
        catch (Exception ex) when (ex is JsonException
            || !preserveOnReadFailure && (ex is IOException or UnauthorizedAccessException))
        {
            return new() { WinEEnabled = false, LastRecovery = "The integration settings could not be read. Windows dialogs are in use." };
        }
    }

    public static DialogIntegrationSettings Update(Func<DialogIntegrationSettings, DialogIntegrationSettings> update, bool requireFolderRegistration = false)
        => WithSettingsLock(() => UpdateLocked(update, requireFolderRegistration));

    /// <summary>Startup/repair reconciles the latest durable preference under
    /// the same lock as an update, never a snapshot read before taking turns.</summary>
    internal static DialogIntegrationSettings ReconcileRegistrations() => WithSettingsLock(() =>
    {
        var current = Read();
        ApplyRegistrations(current, current.Enabled, fullReconcile: true);
        return current;
    });

    private static T WithSettingsLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, @"Local\UltraExplorer.DialogSettings." + InstanceKey);
        var entered = false;
        try
        {
            try { entered = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { entered = true; }
            if (!entered) throw new IOException("Another window is changing dialog integration. Try again.");
            return action();
        }
        finally { if (entered) mutex.ReleaseMutex(); }
    }

    private static DialogIntegrationSettings UpdateLocked(Func<DialogIntegrationSettings, DialogIntegrationSettings> update, bool requireFolderRegistration)
    {
        var before = ReadCore(preserveOnReadFailure: true);
        var settings = update(before);
        var registeredBeforeWrite = false;
        try
        {
            settings = settings with { WinEEnabled = settings.WinEEnabled ?? before.WinEEnabled ?? false };
            if (requireFolderRegistration && settings.Enabled)
            {
                // Settings must not announce ON after a failed registration.
                UltraExplorer.Services.ShellReplacementRegistration.Reconcile(true);
                registeredBeforeWrite = true;
            }
            Directory.CreateDirectory(AppPaths.StateDirectory);
            var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, settings, Json);
                    stream.Flush(true);
                }
                // Windows refuses to replace a file anything has open, and the
                // listener, the watchdog and every replacement read this one
                // several times a second: tried again for a little while, as a
                // lease is (DialogLease.Write).
                for (var attempt = 0; ; attempt++)
                {
                    try { File.Move(temporary, SettingsPath, true); break; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20) { Thread.Sleep(5); }
                }
            }
            finally { try { File.Delete(temporary); } catch (IOException) { } }
            ApplyRegistrations(settings, before.Enabled, shellAlreadyRegistered: registeredBeforeWrite);
            return settings;
        }
        catch
        {
            if (registeredBeforeWrite && !before.Enabled)
                try { UltraExplorer.Services.ShellReplacementRegistration.Reconcile(false); }
                catch (Exception error) when (RegistrationFailure(error)) { Log("Folder registration rollback could not finish", error); }
            throw;
        }
    }

    private static bool RegistrationFailure(Exception error) => error is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or System.ComponentModel.Win32Exception;

    private static void ApplyRegistrations(DialogIntegrationSettings settings, bool wasEnabled, bool fullReconcile = false, bool shellAlreadyRegistered = false)
    {
        // Starting at sign-in follows the setting, whoever changed it: the
        // switch in Settings, a recovery that paused the mode, the watchdog.
        if (fullReconcile || settings.Enabled != wasEnabled)
        {
            if (fullReconcile) DialogStartup.Reconcile(settings.Enabled);
            else DialogStartup.Apply(settings.Enabled);
            try { if (!shellAlreadyRegistered) UltraExplorer.Services.ShellReplacementRegistration.Reconcile(settings.Enabled); }
            catch (Exception ex) when (RegistrationFailure(ex))
            { Log("The Windows folder-opening defaults could not be restored or registered", ex); }
        }
        WinEShortcutStartup.Reconcile(settings.WinEEnabled == true);
    }

    public static void Log(string message, Exception? error = null)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = LogPath;
            if (new FileInfo(path) is { Exists: true, Length: > LogLimit })
                File.Move(path, path + ".old", true);
            File.AppendAllText(path, $"{DateTime.UtcNow:O} [{Environment.ProcessId}] {message}"
                + (error is null ? "" : " " + error.GetType().Name + ": " + error.Message) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static bool IsExcluded(string application) => Read().ExcludedApplications
        .Any(path => string.Equals(path, application, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The resident listener starting at sign-in, as long as the mode is on: a
/// value under HKCU\...\Run added when the user switches it on and removed
/// when it is switched off or paused. Only the everyday copy touches it - never
/// a test copy, a copy with its own state folder, one watching a fixture or a
/// benchmark or snapshot run - since the value starts whatever executable wrote
/// it, with the default state.
/// </summary>
internal static class DialogStartup
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "UltraExplorer dialogs";

    internal static bool IsAllowed =>
        DialogNative.MayActivate
        && Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable) is null
        && Environment.GetEnvironmentVariable("ULTRAEXPLORER_DIALOG_TEST_ONLY_PROCESS") is null
        && Process.GetCurrentProcess().ProcessName is not ("ViewAllSmoke" or "NativeDialogProxySmoke")
        && !IsDiagnosticsRun(Environment.GetCommandLineArgs());

    /// <summary>
    /// A benchmark or snapshot run (<c>--nested-bench</c>, <c>--nested-snapshots</c>):
    /// a throwaway build as much as a test copy is, even without a state folder
    /// of its own. Its sign-in entries, and the listeners it would start, would
    /// name that build long after it is gone.
    /// </summary>
    internal static bool IsDiagnosticsRun(IEnumerable<string> arguments) => arguments.Any(argument =>
        argument.Equals("--nested-bench", StringComparison.OrdinalIgnoreCase)
        || argument.Equals("--nested-snapshots", StringComparison.OrdinalIgnoreCase));

    internal static string Command(string executable) => "\"" + executable + "\" --dialog-agent";

    /// <summary>What the value says now, or null; reading is harmless anywhere.</summary>
    internal static string? Registered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    /// <summary>At a start of the everyday copy: the entry is there exactly when
    /// the mode is on, and names an executable that exists (an entry written by
    /// a copy since moved or removed is written again by this one).</summary>
    public static void Reconcile(bool enabled)
    {
        if (!IsAllowed) return;
        var registered = Registered();
        if (enabled == (registered is not null) && (!enabled || RegisteredExecutableExists(registered!))) return;
        Apply(enabled);
    }

    internal static bool RegisteredExecutableExists(string command)
    {
        var text = command.Trim();
        if (!text.StartsWith('"')) return false;
        var end = text.IndexOf('"', 1);
        return end > 1 && File.Exists(text[1..end]);
    }

    public static void Apply(bool enabled)
    {
        if (!IsAllowed) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key.SetValue(ValueName, Command(DialogSelfProcess.ExecutablePath), RegistryValueKind.String);
            else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            DialogIntegrationStore.Log("The sign-in start could not be " + (enabled ? "added" : "removed"), ex);
        }
    }
}

internal static class DialogSelfProcess
{
    public static string ExecutablePath => string.IsNullOrEmpty(typeof(App).Assembly.Location)
        ? Environment.ProcessPath! : Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");

    public static Process Start(params string[] arguments)
    {
        var exe = ExecutablePath;
        if (!File.Exists(exe)) throw new FileNotFoundException("UltraExplorer's executable is unavailable.", exe);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("The dialog integration process could not be started.");
    }
}
