using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using UltraExplorer;
using UltraExplorer.Services.Updates;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    /// <summary>
    /// Runs inside QuietUpdateUiChecks' isolated resource-only STA. Every HTTP
    /// request uses an injected in-memory handler, and no scheduler is started.
    /// The Settings controls are arranged but never shown or given input focus.
    /// </summary>
    private static async Task QuietUpdateSettingsOnStaAsync(string owned)
    {
        Section("quiet update Settings: explicit check, persisted channels and inline results");
        Directory.CreateDirectory(owned);
        await QuietSettingsManualAndChannelsAsync(Path.Combine(owned, "available"));
        await QuietSettingsCurrentAndFailureAsync(Path.Combine(owned, "results"));
        await QuietSettingsDisposalAsync(Path.Combine(owned, "cancel"));
    }

    private static async Task QuietSettingsManualAndChannelsAsync(string owned)
    {
        Directory.CreateDirectory(owned);
        var cache = Path.Combine(owned, "updates");
        using var handler = QuietReleaseHandler(QuietZip(), "v1.3.1");
        using var service = new QuietUpdateService(cache, "1.3.0", handler, new QuietClock(),
            (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        service.Enabled = false;
        var host = new MainWindow(null, Path.Combine(owned, "workspace.json"));
        var shell = (MainViewModel)host.DataContext;
        shell.SuppressShellWrites = true;
        host.InitializeQuietUpdates(service);
        using var settings = new SettingsViewModel(shell, true, null, (_, _, _) => false, quietUpdates: service);
        var page = new SettingsWindow(settings);
        try
        {
            LayOutWindow(page, 720, 900);
            await SettingsSettle();
            Check("the update Settings page and bindings construct without making a network request",
                handler.Requests.Count == 0 && !service.IsChecking && !page.IsVisible
                && new WindowInteropHelper(page).Handle == IntPtr.Zero);
            Check("Settings exposes exactly Stable, Beta and Experimental choices with the current persisted channel",
                settings.UpdateChannels.SequenceEqual(Enum.GetValues<QuietUpdateChannel>())
                && page.UpdateChannelBox.Items.Cast<QuietUpdateChannel>().SequenceEqual(Enum.GetValues<QuietUpdateChannel>())
                && Equals(page.UpdateChannelBox.SelectedItem, service.Channel));
            Check("manual checking remains explicitly available while Receive updates is off",
                !settings.ReceiveUpdates && settings.CanCheckForUpdates && settings.CheckForUpdatesCommand.CanExecute(null)
                && page.CheckForUpdatesButton.IsEnabled && ReferenceEquals(page.CheckForUpdatesButton.Command, settings.CheckForUpdatesCommand)
                && settings.InstalledVersion == "1.3.0");

            foreach (var channel in Enum.GetValues<QuietUpdateChannel>())
            {
                page.UpdateChannelBox.SetCurrentValue(Selector.SelectedItemProperty, channel);
                await SettingsSettle();
                using var reopenHandler = new QuietUiForbiddenNetworkHandler();
                using var reopened = new QuietUpdateService(cache, "1.3.0", reopenHandler, new QuietClock());
                Check($"choosing {channel} updates the actual service, bound choice and reopened profile without checking",
                    settings.UpdateChannel == channel && service.Channel == channel && reopened.Channel == channel
                    && Equals(page.UpdateChannelBox.SelectedItem, channel)
                    && handler.Requests.Count == 0 && reopenHandler.Requests == 0 && !reopened.Enabled);
            }
            service.Channel = QuietUpdateChannel.Stable;
            await SettingsSettle();
            Check("a channel changed elsewhere reaches the open Settings selector",
                settings.UpdateChannel == QuietUpdateChannel.Stable && Equals(page.UpdateChannelBox.SelectedItem, QuietUpdateChannel.Stable));

            settings.CheckForUpdatesCommand.Execute(null);
            await QuietUiWaitAsync(() => handler.Requests.Count > 0 && !service.IsChecking && settings.CanCheckForUpdates);
            Check("an explicit check while Off reads release metadata only and leaves background updates off",
                handler.Requests.Count == 1 && handler.Requests.All(uri => uri.Host == "api.github.com")
                && !service.Enabled && !settings.ReceiveUpdates && !service.HasUpdate
                && service.State == QuietUpdateState.Idle && !Directory.EnumerateFiles(cache, "*.zip").Any());
            Check("an available version while Off is explained inline with the opt-in download step",
                settings.UpdateStatus.Contains("1.3.1", StringComparison.Ordinal)
                && settings.UpdateStatus.Contains("Receive updates", StringComparison.OrdinalIgnoreCase)
                && page.UpdateCheckCard.Status == settings.UpdateStatus);
            Check("a manual metadata result creates no header badge, automatic offer, native window or installation",
                host.QuietUpdateButton.Visibility == Visibility.Collapsed && !host.QuietUpdatePopover.IsOpen
                && !page.IsVisible && !host.IsVisible && new WindowInteropHelper(host).Handle == IntPtr.Zero);
            ScrollPage(page, 1);
            QuietUiSaveImage(LayOutWindow(page, 720, 900), "settings-manual-off");

            settings.ReceiveUpdates = true;
            await SettingsSettle();
            var before = handler.Requests.Count;
            Check("turning Receive updates on does not start a check from the Settings property setter", before == 1);
            var installs = 0;
            host.QuietUpdateInstallLauncher = (_, _) => { installs++; throw new InvalidOperationException("The test never confirms installation."); };
            settings.CheckForUpdatesCommand.Execute(null);
            await QuietUiWaitAsync(() => service.State == QuietUpdateState.Ready && settings.CanCheckForUpdates);
            Check("the enabled explicit check verifies and prepares the owned fake package without a startup delay",
                service.HasUpdate && service.ReadyVersion == "1.3.1" && handler.Requests.Count > before
                && handler.Requests.Any(uri => uri.AbsolutePath.EndsWith(QuietUpdateHttp.ArchiveName, StringComparison.Ordinal))
                && File.Exists(service.PreparedPackage));
            Check("a prepared manual result stays inline with only the quiet ready arrow and no automatic install",
                settings.UpdateStatus.Contains("1.3.1", StringComparison.Ordinal) && page.UpdateCheckCard.Status == settings.UpdateStatus
                && host.QuietUpdateArrow.Visibility == Visibility.Visible && host.QuietUpdateButton.IsEnabled
                && !host.QuietUpdatePopover.IsOpen && installs == 0);
            QuietUiSaveImage(LayOutWindow(page, 720, 900), "settings-manual-ready");
            Check("an inactive selector does not consume Escape as though a menu were open", !page.DismissOpenSettingsChoice());
            var sortOpen = true;
            var channelOpen = true;
            var consumedChannel = SettingsWindow.DismissOpenSettingsChoice(sortOpen, channelOpen,
                () => sortOpen = false, () => channelOpen = false);
            Check("Escape dismisses the update channel choice before a parent Settings close without opening a native popup",
                consumedChannel && !channelOpen && sortOpen);
            var consumedSort = SettingsWindow.DismissOpenSettingsChoice(sortOpen, channelOpen,
                () => sortOpen = false, () => channelOpen = false);
            Check("Escape also dismisses the existing sort choice before allowing the page close",
                consumedSort && !sortOpen && !SettingsWindow.DismissOpenSettingsChoice(sortOpen, channelOpen,
                    () => sortOpen = false, () => channelOpen = false));
        }
        finally { host.DisposeQuietUpdates(); shell.Dispose(); }
    }

    private static async Task QuietSettingsCurrentAndFailureAsync(string owned)
    {
        foreach (var fail in new[] { false, true })
        {
            var directory = Path.Combine(owned, fail ? "failed" : "current");
            Directory.CreateDirectory(directory);
            using var handler = new QuietHandler(_ => fail ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : QuietJson("[]"));
            using var service = new QuietUpdateService(Path.Combine(directory, "updates"), "1.3.0", handler, new QuietClock());
            service.Enabled = false;
            var host = new MainWindow(null, Path.Combine(directory, "workspace.json"));
            var shell = (MainViewModel)host.DataContext;
            shell.SuppressShellWrites = true;
            host.InitializeQuietUpdates(service);
            using var settings = new SettingsViewModel(shell, true, null, (_, _, _) => false, quietUpdates: service);
            var page = new SettingsWindow(settings);
            try
            {
                LayOutWindow(page, 720, 900);
                settings.CheckForUpdatesCommand.Execute(null);
                await QuietUiWaitAsync(() => handler.Requests.Count == 1 && !service.IsChecking && settings.CanCheckForUpdates);
                Check(fail ? "a failed explicit check returns an inline retryable result without an automatic panel"
                    : "an up-to-date explicit check reports its result inline without an automatic panel",
                    settings.UpdateStatus.Length > 0 && page.UpdateCheckCard.Status == settings.UpdateStatus
                    && (fail || settings.UpdateStatus.Contains("No newer", StringComparison.OrdinalIgnoreCase))
                    && !host.QuietUpdatePopover.IsOpen && host.QuietUpdateButton.Visibility == Visibility.Collapsed
                    && !settings.ReceiveUpdates && settings.CheckForUpdatesCommand.CanExecute(null));
            }
            finally { host.DisposeQuietUpdates(); shell.Dispose(); }
        }
    }

    private static async Task QuietSettingsDisposalAsync(string owned)
    {
        Directory.CreateDirectory(owned);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        using var handler = new QuietHandler(async (_, token) =>
        {
            using var registration = token.Register(() => cancelled = true);
            entered.TrySetResult();
            // Deliberately ignore cancellation until released: the disposed
            // page must not accept a late HTTP completion or property update.
            await release.Task;
            return QuietJson("[]");
        });
        using var service = new QuietUpdateService(Path.Combine(owned, "updates"), "1.3.0", handler, new QuietClock());
        service.Enabled = false;
        var host = new MainWindow(null, Path.Combine(owned, "workspace.json"));
        var shell = (MainViewModel)host.DataContext;
        shell.SuppressShellWrites = true;
        host.InitializeQuietUpdates(service);
        using var settings = new SettingsViewModel(shell, true, null, (_, _, _) => false, quietUpdates: service);
        var page = new SettingsWindow(settings);
        try
        {
            LayOutWindow(page, 720, 900);
            settings.CheckForUpdatesCommand.Execute(null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await SettingsSettle();
            Check("an active manual check disables repeated clicks while keeping the channel selector available",
                service.IsChecking && !settings.CanCheckForUpdates && !settings.CheckForUpdatesCommand.CanExecute(null)
                && !page.CheckForUpdatesButton.IsEnabled && page.UpdateChannelBox.IsEnabled);
            settings.CheckForUpdatesCommand.Execute(null);
            Check("a second check click cannot create another HTTP request", handler.Requests.Count == 1);
            page.Close();
            var status = settings.UpdateStatus;
            var notifications = 0;
            settings.PropertyChanged += (_, _) => notifications++;
            await QuietUiWaitAsync(() => cancelled);
            release.TrySetResult();
            await QuietUiWaitAsync(() => !service.IsChecking);
            Check("closing Settings cancels its explicit request and ignores a late provider completion",
                cancelled && !service.IsChecking && settings.UpdateStatus == status && notifications == 0
                && !settings.CheckForUpdatesCommand.CanExecute(null) && !host.QuietUpdatePopover.IsOpen
                && host.QuietUpdateButton.Visibility == Visibility.Collapsed);
        }
        finally
        {
            release.TrySetResult();
            host.DisposeQuietUpdates();
            shell.Dispose();
        }
    }
}
