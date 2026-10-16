using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private const string FolderOpenExpectedVariable = "ULTRAEXPLORER_FOLDER_OPEN_EXPECTED";

    private static Task FolderOpenPreferenceChecks()
    {
        if (Environment.GetEnvironmentVariable(FolderOpenExpectedVariable) is { } expected)
        {
            Check("another process reads the same profile's folder-opening choice",
                DialogIntegrationStore.ReadFolderOpenMode().ToString() == expected);
            return Task.CompletedTask;
        }

        if (_only is not [var alone] || !alone.Equals(nameof(FolderOpenPreferenceChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(FolderOpenPreferenceChecks));
            return Task.CompletedTask;
        }

        RunOnSta("folder opening preference", FolderOpenPreferenceOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task FolderOpenPreferenceOnStaAsync()
    {
        Section("folder opening: separate windows by default and a shared tab preference");
        Check("missing, unknown and null settings keep separate windows",
            DialogIntegrationStore.ParseFolderOpenMode(null) == FolderOpenMode.SeparateWindow
            && DialogIntegrationStore.ParseFolderOpenMode("unknown") == FolderOpenMode.SeparateWindow
            && DialogIntegrationStore.ParseFolderOpenMode(string.Empty) == FolderOpenMode.SeparateWindow
            && DialogIntegrationStore.ParseFolderOpenMode("SeparateWindow") == FolderOpenMode.SeparateWindow
            && DialogIntegrationStore.ParseFolderOpenMode("1") == FolderOpenMode.SeparateWindow);
        Check("the tab setting accepts whitespace and different letter casing",
            DialogIntegrationStore.ParseFolderOpenMode(" newtab ") == FolderOpenMode.NewTab);

        var stateOverride = Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable);
        var everydayState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer");
        var isolated = !string.IsNullOrWhiteSpace(stateOverride)
            && ViewAllPath.Equals(stateOverride!, AppPaths.StateDirectory)
            && !ViewAllPath.Equals(AppPaths.StateDirectory, everydayState)
            && Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1";
        Check("folder preference checks require isolated state and test-window mode", isolated);
        if (!isolated) return;

        Directory.CreateDirectory(AppPaths.StateDirectory);
        var path = DialogIntegrationStore.SettingsPath;
        var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var timeBefore = before is null ? (DateTime?)null : File.GetLastWriteTimeUtc(path);
        try
        {
            File.Delete(path);
            DialogIntegrationStore.RememberRead(null);
            Check("a new profile opens separate windows without creating a settings file",
                DialogIntegrationStore.ReadFolderOpenMode() == FolderOpenMode.SeparateWindow && !File.Exists(path));

            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                Enabled = true, WinEEnabled = false, WinELastError = "previous shortcut error",
                ExcludedApplications = new[] { @"C:\Tools\KeepWindows.exe" }, LastRecovery = "previous recovery"
            }));
            Check("a profile written before tabs keeps separate windows",
                DialogIntegrationStore.ReadFolderOpenMode() == FolderOpenMode.SeparateWindow);
            DialogIntegrationStore.SetFolderOpenMode(FolderOpenMode.NewTab);
            var saved = DialogIntegrationStore.Read();
            Check("choosing tabs persists without changing integration, shortcut, exclusions or recovery",
                DialogIntegrationStore.ReadFolderOpenMode() == FolderOpenMode.NewTab
                && saved.Enabled && saved.WinEEnabled == false
                && saved.WinELastError == "previous shortcut error"
                && saved.ExcludedApplications.SequenceEqual([@"C:\Tools\KeepWindows.exe"])
                && saved.LastRecovery == "previous recovery");
            await FolderOpenPreferenceChildChecks(FolderOpenMode.NewTab);

            DialogIntegrationStore.Update(current => current with { WinELastError = "new shortcut error" });
            Check("an unrelated integration edit keeps the latest tab choice",
                DialogIntegrationStore.ReadFolderOpenMode() == FolderOpenMode.NewTab
                && DialogIntegrationStore.Read().WinELastError == "new shortcut error");

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Check("a busy settings file keeps the last complete tab choice",
                    DialogIntegrationStore.ReadFolderOpenMode() == FolderOpenMode.NewTab);
                Exception? failure = null;
                try { DialogIntegrationStore.SetFolderOpenMode(FolderOpenMode.SeparateWindow); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure = ex; }
                Check("changing the choice fails safely when the existing profile cannot be read", failure is not null);
            }
            Check("the failed write leaves the existing tab and integration choices intact",
                DialogIntegrationStore.ReadFolderOpenMode() == FolderOpenMode.NewTab && DialogIntegrationStore.Read().Enabled);

            var intact = File.ReadAllBytes(path);
            foreach (var damaged in new[] { "{ broken json", "null", new string(' ', 64 * 1024 + 1) })
            {
                File.WriteAllText(path, damaged);
                var damagedBytes = File.ReadAllBytes(path);
                var damagedTime = File.GetLastWriteTimeUtc(path);
                Exception? failure = null;
                try { DialogIntegrationStore.SetFolderOpenMode(FolderOpenMode.SeparateWindow); }
                catch (JsonException ex) { failure = ex; }
                Check("a preference change preserves malformed or oversized integration data",
                    failure is not null && File.ReadAllBytes(path).SequenceEqual(damagedBytes)
                    && File.GetLastWriteTimeUtc(path) == damagedTime);
            }
            File.WriteAllBytes(path, intact);
            DialogIntegrationStore.SetFolderOpenMode(FolderOpenMode.SeparateWindow);
            await FolderOpenPreferenceChildChecks(FolderOpenMode.SeparateWindow);
            await FolderOpenPreferenceWindowChecksAsync();
            await FolderOpenPreferenceHostSettingsChecksAsync();
        }
        finally
        {
            DialogIntegrationStore.RememberRead(null);
            if (before is null) File.Delete(path);
            else
            {
                File.WriteAllBytes(path, before);
                File.SetLastWriteTimeUtc(path, timeBefore!.Value);
            }
        }
        Check("the isolated settings file is restored exactly",
            before is null ? !File.Exists(path)
                : File.ReadAllBytes(path).SequenceEqual(before) && File.GetLastWriteTimeUtc(path) == timeBefore);
    }

    private static async Task FolderOpenPreferenceChildChecks(FolderOpenMode expected)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--only");
        start.ArgumentList.Add(nameof(FolderOpenPreferenceChecks));
        start.Environment[FolderOpenExpectedVariable] = expected.ToString();
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
        }
        var details = (await output) + (await error);
        Check($"a fresh process restores the {expected} choice", child.ExitCode == 0 && !timeout.IsCancellationRequested);
        if (child.ExitCode != 0 || timeout.IsCancellationRequested) Console.WriteLine(details);
    }

    private static async Task FolderOpenPreferenceWindowChecksAsync()
    {
        Section("folder opening: accessible Settings switch applies and refreshes across windows");
        if (Application.Current is null) new App().InitializeComponent();
        Application.Current!.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        using var model = new MainViewModel();
        using var settings = new SettingsViewModel(model, true, null, (_, _, _) => false);
        using var other = new SettingsViewModel(model, true, null, (_, _, _) => false);
        var window = new SettingsWindow(settings);
        try
        {
            LayOutWindow(window, 720, 780);
            await SettingsSettle();
            Check("Settings exposes an enabled, accessible switch with both outcomes explained",
                window.IncomingFoldersTabsChoice.IsEnabled && window.IncomingFoldersTabsChoice.IsChecked == false
                && AutomationProperties.GetName(window.IncomingFoldersTabsChoice) == "Open incoming folders in tabs"
                && AutomationProperties.GetHelpText(window.IncomingFoldersTabsChoice).Contains("separate window", StringComparison.Ordinal));
            Toggle(window.IncomingFoldersTabsChoice);
            Check("the switch and explanation reflect the requested tab choice immediately",
                settings.OpenIncomingFoldersInTabs && settings.FolderOpenModeStatus.Contains("New tab", StringComparison.Ordinal));
            Check("the Settings switch durably saves the tab choice",
                await SettingsWaitFor(() => DialogIntegrationStore.ReadFolderOpenMode() == FolderOpenMode.NewTab));
            await SettingsSettle();
            other.RefreshFolderOpenMode();
            Check("an already-open second Settings model refreshes the shared choice", other.OpenIncomingFoldersInTabs);

            other.OpenIncomingFoldersInTabs = false;
            Check("the second Settings model durably saves separate windows",
                await SettingsWaitFor(() => DialogIntegrationStore.ReadFolderOpenMode() == FolderOpenMode.SeparateWindow));
            await SettingsSettle();
            settings.RefreshFolderOpenMode();
            await SettingsSettle();
            Check("the original window follows the changed choice and keeps its separate-window explanation",
                !settings.OpenIncomingFoldersInTabs && window.IncomingFoldersTabsChoice.IsChecked == false
                && settings.FolderOpenModeStatus.Contains("Separate window", StringComparison.Ordinal));

            settings.OpenIncomingFoldersInTabs = true;
            settings.OpenIncomingFoldersInTabs = false;
            settings.OpenIncomingFoldersInTabs = true;
            Check("rapid switching keeps the user's last requested choice",
                await SettingsWaitFor(() => DialogIntegrationStore.ReadFolderOpenMode() == FolderOpenMode.NewTab));
            await SettingsSettle();

            settings.OpenIncomingFoldersInTabs = false;
            Check("the final separate-window choice finishes before testing a failed save",
                await SettingsWaitFor(() => DialogIntegrationStore.ReadFolderOpenMode() == FolderOpenMode.SeparateWindow));
            await SettingsSettle();
            var path = DialogIntegrationStore.SettingsPath;
            var bytesBefore = File.ReadAllBytes(path);
            var timeBefore = File.GetLastWriteTimeUtc(path);
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Toggle(window.IncomingFoldersTabsChoice);
                Check("a failed save returns the switch and explanation to the persisted choice",
                    await SettingsWaitFor(() => !settings.OpenIncomingFoldersInTabs)
                    && settings.FolderOpenModeStatus.Contains("Separate window", StringComparison.Ordinal));
            }
            Check("the failed Settings save leaves integration data and timestamp intact",
                File.ReadAllBytes(path).SequenceEqual(bytesBefore) && File.GetLastWriteTimeUtc(path) == timeBefore);
        }
        finally { window.Close(); }
    }

    private static async Task FolderOpenPreferenceHostSettingsChecksAsync()
    {
        Section("folder tabs: tray Settings follows the active context and stays one per shell");
        var host = new MainWindow();
        var first = host.EnableFolderTabs();
        Check("the settings fixture can create a second folder context",
            FolderCommandLine.TryOpenFolder(AppPaths.StateDirectory, out var request, out _));
        if (request is null) { host.Close(); return; }
        var second = host.CreateFolderTab(request with { DestinationId = Guid.NewGuid() });
        var firstModel = (MainViewModel)first.DataContext;
        var secondModel = (MainViewModel)second.DataContext;
        var shown = new List<SettingsWindow>();
        first.SettingsPresenter = shown.Add;
        second.SettingsPresenter = shown.Add;
        try
        {
            firstModel.ShowHoverPreviews = false;
            host.SelectTabContext(first, focus: false);
            host.OpenSettings();
            Check("opening Settings on the host uses the active tab's model",
                first.OpenSettingsWindow is { } firstSettings && !firstSettings.Settings.ShowHoverPreviews
                && shown.Count == 1 && ReferenceEquals(shown[0], firstSettings));
            host.SelectTabContext(second, focus: false);
            host.OpenSettings();
            var secondSettings = second.OpenSettingsWindow;
            Check("opening Settings from another tab closes the earlier context's Settings",
                first.OpenSettingsWindow is null && secondSettings is not null
                && secondSettings.Settings.ShowHoverPreviews == secondModel.ShowHoverPreviews
                && shown.Count == 2 && !ReferenceEquals(shown[0], shown[1]));
            host.OpenSettings();
            Check("asking again reuses the active context's one Settings window",
                shown.Count == 3 && ReferenceEquals(shown[1], shown[2]));
            await host.CloseTabContextAsync(second);
            Check("closing a tab closes Settings bound to its model",
                second.OpenSettingsWindow is null && secondModel.IsDisposed && host.TabCount == 1);
        }
        finally
        {
            host.Close();
            await SettingsWaitFor(() => firstModel.IsDisposed);
        }
    }
}
