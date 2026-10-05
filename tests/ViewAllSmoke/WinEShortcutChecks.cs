using System.IO;
using System.Text.Json;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task WinEShortcutChecks()
    {
        Section("Win+E: independent integration settings");
        Check("the Win+E listener is a background role without opening the main window",
            DialogIntegrationRuntime.IsRole(["--shortcut-agent"])
            && !DialogIntegrationRuntime.IsRole(["--home"]));

        // AppPaths resolves once for the process. The caller must select an
        // isolated state folder before starting the harness, as for the other
        // integration checks; changing the variable here cannot move that path.
        var stateOverride = Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable);
        if (string.IsNullOrWhiteSpace(stateOverride))
        {
            Console.WriteLine("  (state checks need an isolated ULTRAEXPLORER_STATE_DIR before process startup: skipped)");
            return Task.CompletedTask;
        }

        var selectedState = Path.GetFullPath(Environment.ExpandEnvironmentVariables(stateOverride.Trim().Trim('"')))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var everydayState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer")
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var resolvedState = AppPaths.StateDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var isolated = string.Equals(selectedState, resolvedState, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(selectedState, everydayState, StringComparison.OrdinalIgnoreCase);
        Check("the settings checks use the caller's isolated state folder", isolated);
        if (!isolated) return Task.CompletedTask;

        var testWindowBefore = Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW");
        var settingsPath = DialogIntegrationStore.SettingsPath;
        var settingsBefore = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        var settingsTimeBefore = settingsBefore is null ? (DateTime?)null : File.GetLastWriteTimeUtc(settingsPath);
        var touchedSettings = false;
        Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", "1");
        try
        {
            var canWriteRegistration = DialogStartup.IsAllowed || WinEShortcutStartup.IsAllowed || ShellReplacementRegistration.IsAllowed;
            Check("an isolated copy cannot write startup or folder-opening registration", !canWriteRegistration);
            if (canWriteRegistration) return Task.CompletedTask;

            Directory.CreateDirectory(AppPaths.StateDirectory);
            touchedSettings = true;
            File.Delete(settingsPath);
            var fresh = DialogIntegrationStore.Read();
            Check("fresh settings leave general replacement and Win+E off", !fresh.Enabled && fresh.WinEEnabled == false);
            DialogIntegrationStore.Update(settings => settings with { Enabled = true });
            var generalOnly = DialogIntegrationStore.Read();
            Check("enabling general replacement in fresh settings keeps Win+E off", generalOnly.Enabled && generalOnly.WinEEnabled == false);

            foreach (var general in new[] { false, true })
            {
                foreach (var shortcut in new[] { false, true })
                {
                    var written = DialogIntegrationStore.Update(_ => new() { Enabled = general, WinEEnabled = shortcut });
                    var read = DialogIntegrationStore.Read();
                    Check($"general={general}, Win+E={shortcut} survive writing and reading",
                        written.Enabled == general && written.WinEEnabled == shortcut
                        && read.Enabled == general && read.WinEEnabled == shortcut);

                    DialogIntegrationStore.Update(settings => settings with { Enabled = !general });
                    var afterGeneral = DialogIntegrationStore.Read();
                    Check($"changing general={general} preserves explicit Win+E={shortcut}",
                        afterGeneral.Enabled == !general && afterGeneral.WinEEnabled == shortcut);

                    DialogIntegrationStore.Update(settings => settings with { Enabled = general });
                    DialogIntegrationStore.Update(settings => settings with { WinEEnabled = !shortcut });
                    var afterShortcut = DialogIntegrationStore.Read();
                    Check($"changing Win+E={shortcut} preserves general={general}",
                        afterShortcut.Enabled == general && afterShortcut.WinEEnabled == !shortcut);
                }
            }

            foreach (var oldEnabled in new[] { false, true })
            {
                File.WriteAllText(settingsPath, JsonSerializer.Serialize(new { Enabled = oldEnabled }));
                var legacy = DialogIntegrationStore.Read();
                Check($"old settings without Win+E inherit Enabled={oldEnabled}",
                    legacy.Enabled == oldEnabled && legacy.WinEEnabled == oldEnabled);
                DialogIntegrationStore.Update(settings => settings with { Enabled = !oldEnabled });
                var migrated = DialogIntegrationStore.Read();
                using var saved = JsonDocument.Parse(File.ReadAllText(settingsPath));
                Check($"the next write preserves and records the migrated Win+E={oldEnabled}",
                    migrated.Enabled == !oldEnabled && migrated.WinEEnabled == oldEnabled
                    && saved.RootElement.TryGetProperty(nameof(DialogIntegrationSettings.WinEEnabled), out var savedShortcut)
                    && savedShortcut.ValueKind is JsonValueKind.True or JsonValueKind.False
                    && savedShortcut.GetBoolean() == oldEnabled);
            }

            File.WriteAllText(settingsPath, JsonSerializer.Serialize(new { Enabled = true, WinEEnabled = false }));
            var explicitOff = DialogIntegrationStore.Read();
            Check("explicit Win+E off wins over the old general-enabled default",
                explicitOff.Enabled && explicitOff.WinEEnabled == false);
            DialogIntegrationStore.Update(settings => settings with { Enabled = false });
            DialogIntegrationStore.Update(settings => settings with { Enabled = true });
            var stillOff = DialogIntegrationStore.Read();
            Check("general replacement off and back on keeps explicit Win+E off", stillOff.Enabled && stillOff.WinEEnabled == false);

            string[] exclusions = [@"C:\Tools\KeepWindowsDialogs.exe"];
            const string previousRecovery = "A previous general recovery remains recorded.";
            const string startupFailure = "isolated shortcut startup failure";
            foreach (var general in new[] { false, true })
            {
                DialogIntegrationStore.Update(_ => new()
                {
                    Enabled = general, WinEEnabled = true, ExcludedApplications = exclusions,
                    LastRecovery = previousRecovery, WinELastError = "Previous shortcut status."
                });
                WinEShortcutAgent.RecordFailure(startupFailure);
                var failed = DialogIntegrationStore.Read();
                Check($"a Win+E failure disables only the shortcut and preserves general={general}, exclusions and recovery",
                    failed.WinEEnabled == false && failed.Enabled == general
                    && failed.ExcludedApplications.SequenceEqual(exclusions) && failed.LastRecovery == previousRecovery
                    && failed.WinELastError.Contains(startupFailure, StringComparison.Ordinal));
            }
        }
        finally
        {
            // Restore exactly what this group found, even after a failed check.
            // Restoring the file directly avoids changing any live controller.
            try
            {
                if (touchedSettings)
                {
                    if (settingsBefore is null) File.Delete(settingsPath);
                    else
                    {
                        File.WriteAllBytes(settingsPath, settingsBefore);
                        File.SetLastWriteTimeUtc(settingsPath, settingsTimeBefore!.Value);
                    }
                }
            }
            finally { Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", testWindowBefore); }
        }
        Check("the settings file is restored after independent Win+E checks",
            settingsBefore is null ? !File.Exists(settingsPath) : File.ReadAllBytes(settingsPath).SequenceEqual(settingsBefore));
        return Task.CompletedTask;
    }
}
