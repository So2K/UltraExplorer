using System.Windows;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using UltraExplorer.Services.Updates;

namespace UltraExplorer;

public partial class MainWindow
{
    private QuietUpdateService? _quietUpdates;
    private CancellationTokenSource? _quietUpdateInstallCancellation;
    private bool _quietUpdateRingSpinning;
    private bool _quietUpdateInstalling;
    private string? _quietUpdateLastInstallFailure;
    private string _quietUpdateOfferedVersion = string.Empty;

    // The application owns the service's lifetime. A window observes it only;
    // constructing a picker, a Settings page or a test never starts checks.
    internal void InitializeQuietUpdates(QuietUpdateService service)
    {
        if (ReferenceEquals(_quietUpdates, service)) return;
        DisposeQuietUpdates();
        _quietUpdates = service;
        service.SnapshotChanged += QuietUpdates_SnapshotChanged;
        RefreshQuietUpdatePresentation();
    }

    internal void DisposeQuietUpdates()
    {
        if (_quietUpdates is { } service) service.SnapshotChanged -= QuietUpdates_SnapshotChanged;
        _quietUpdates = null;
        CancelQuietUpdatePreparation();
        QuietUpdatePopover.IsOpen = false;
        SetQuietUpdateRingSpinning(false);
        QuietUpdateButton.Visibility = Visibility.Collapsed;
    }

    // Tests can exercise the explicit click without starting an installer.
    internal Func<PreparedUpdatePackage, CancellationToken, Task<UpdateApplyLaunch>>? QuietUpdateInstallLauncher { get; set; }
    internal Func<UpdateApplyLaunch, CancellationToken, Task<bool>>? QuietUpdateResultMonitor { get; set; }

    private void QuietUpdates_SnapshotChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted) return;
        // Turning the setting off invalidates an in-flight validation before
        // the dispatcher next paints the hidden indicator.
        if (_quietUpdates is { Enabled: false }) CancelQuietUpdatePreparation();
        if (Dispatcher.CheckAccess()) RefreshQuietUpdatePresentation();
        else _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshQuietUpdatePresentation);
    }

    private void RefreshQuietUpdatePresentation()
    {
        if (_quietUpdates is not { } service) return;
        var snapshot = service.Snapshot;
        ApplyQuietUpdatePresentation(snapshot.Enabled, snapshot.State, snapshot.ReadyVersion, snapshot.Progress);
    }

    internal void ApplyQuietUpdatePresentation(bool enabled, QuietUpdateState state, string version, double progress)
    {
        var downloading = enabled && state == QuietUpdateState.Downloading;
        var ready = enabled && state == QuietUpdateState.Ready;
        QuietUpdateButton.Visibility = downloading || ready ? Visibility.Visible : Visibility.Collapsed;
        QuietUpdateButton.IsEnabled = ready && !_quietUpdateInstalling;
        QuietUpdateRing.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        QuietUpdateArrow.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        SetQuietUpdateRingSpinning(downloading);

        var tooltip = downloading
            ? "Downloading update quietly (" + Math.Clamp(progress * 100, 0, 100).ToString("0") + "%)"
            : "Update " + version + " ready — click to install";
        QuietUpdateButton.ToolTip = tooltip;
        AutomationProperties.SetName(QuietUpdateButton, tooltip);
        QuietUpdateInstallButton.IsEnabled = ready && !_quietUpdateInstalling;

        if (!enabled)
        {
            CancelQuietUpdatePreparation();
            QuietUpdatePopover.IsOpen = false;
        }

        // Deliberately no IsOpen=true, focus, toast or modal dialog here.
        // Errors also stay silent unless the user already opened the panel.
    }

    private void SetQuietUpdateRingSpinning(bool spinning)
    {
        if (_quietUpdateRingSpinning == spinning) return;
        _quietUpdateRingSpinning = spinning;
        QuietUpdateRingRotation.BeginAnimation(RotateTransform.AngleProperty, spinning
            ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    private void QuietUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_quietUpdates is not { Enabled: true, State: QuietUpdateState.Ready } service || _quietUpdateInstalling) return;
        if (QuietUpdatePopover.IsOpen)
        {
            QuietUpdatePopover.IsOpen = false;
            return;
        }

        _quietUpdateOfferedVersion = service.ReadyVersion;
        QuietUpdateVersionText.Text = "UltraExplorer " + _quietUpdateOfferedVersion;
        QuietUpdateStatusText.Text = _quietUpdateLastInstallFailure ?? string.Empty;
        QuietUpdateStatusText.Visibility = _quietUpdateLastInstallFailure is null ? Visibility.Collapsed : Visibility.Visible;
        QuietUpdateInstallButton.IsEnabled = true;
        QuietUpdatePopover.IsOpen = true;
    }

    private async void QuietUpdateInstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_quietUpdates is not { Enabled: true, State: QuietUpdateState.Ready } service || _quietUpdateInstalling
            || !QuietUpdatePopover.IsOpen) return;

        _quietUpdateInstalling = true;
        QuietUpdateButton.IsEnabled = false;
        QuietUpdateInstallButton.IsEnabled = false;
        QuietUpdateStatusText.Text = "Preparing update…";
        QuietUpdateStatusText.Visibility = Visibility.Visible;
        _quietUpdateLastInstallFailure = null;
        var cancellation = new CancellationTokenSource();
        // The helper closes every window from this application folder. If
        // this window closes but another refuses, the running application's
        // lifetime must still restore the shared ready state on failure.
        var observationToken = service.LifetimeToken;
        _quietUpdateInstallCancellation = cancellation;
        try
        {
            var package = await service.PrepareInstallAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!service.Enabled || !ReferenceEquals(service, _quietUpdates) || package is null)
            {
                if (QuietUpdatePopover.IsOpen) QuietUpdateStatusText.Text = "The update is no longer ready. Try again later.";
                return;
            }
            if (!string.Equals(package.Version, _quietUpdateOfferedVersion, StringComparison.Ordinal))
            {
                _quietUpdateOfferedVersion = package.Version;
                QuietUpdateVersionText.Text = "UltraExplorer " + package.Version;
                QuietUpdateStatusText.Text = "The available version changed. Review it before installing.";
                return;
            }

            // This is the only installation entry: a second, explicit click
            // on Install update in a panel the user opened themselves.
            cancellation.Token.ThrowIfCancellationRequested();
            if (!service.MarkInstalling()) return;
            var launched = QuietUpdateInstallLauncher is { } launch
                ? await launch(package, cancellation.Token)
                : await UpdateInstaller.BeginAsync(package, cancellation.Token);
            QuietUpdatePopover.IsOpen = false;
            var installed = QuietUpdateResultMonitor is { } monitor
                ? await monitor(launched, observationToken)
                : await ObserveQuietUpdateResultAsync(launched, observationToken);
            if (!installed)
            {
                service.RestoreReady();
                _quietUpdateLastInstallFailure = "The update could not complete. This version is still available; try again later.";
                // Closing was declined or the helper failed. Keep the offer
                // closed; the user can press the quiet arrow whenever ready.
                if (QuietUpdatePopover.IsOpen)
                {
                    QuietUpdateStatusText.Text = _quietUpdateLastInstallFailure;
                    QuietUpdateStatusText.Visibility = Visibility.Visible;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || observationToken.IsCancellationRequested)
        {
            // Off, dismiss and window closure simply leave the old version.
            if (service.Enabled && !observationToken.IsCancellationRequested)
                service.RestoreReady();
        }
        catch
        {
            service.RestoreReady();
            _quietUpdateLastInstallFailure = "Could not install the update. This version is still available; try again later.";
            if (QuietUpdatePopover.IsOpen)
                QuietUpdateStatusText.Text = _quietUpdateLastInstallFailure;
        }
        finally
        {
            if (ReferenceEquals(_quietUpdateInstallCancellation, cancellation)) _quietUpdateInstallCancellation = null;
            cancellation.Dispose();
            _quietUpdateInstalling = false;
            RefreshQuietUpdatePresentation();
        }
    }

    private static async Task<bool> ObserveQuietUpdateResultAsync(UpdateApplyLaunch launched, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(launched.ResultPath);
                if (info is { Exists: true, Length: > 0 and <= 65_536 })
                {
                    using var file = new FileStream(launched.ResultPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var json = await JsonDocument.ParseAsync(file, cancellationToken: token);
                    if (json.RootElement.TryGetProperty("Status", out var status) && status.ValueKind == JsonValueKind.String)
                    {
                        if (status.GetString() == "Installed") return true;
                        if (status.GetString() == "Failed") return false;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            if (launched.ProcessId > 0)
            {
                try { using var helper = Process.GetProcessById(launched.ProcessId); if (helper.HasExited) return false; }
                catch (ArgumentException) { return false; }
            }
            // This observer exists only after an explicit installation click.
            // It neither checks GitHub nor survives its owning app process.
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
    }

    private void CancelQuietUpdatePreparation()
    {
        try { _quietUpdateInstallCancellation?.Cancel(); }
        catch (ObjectDisposedException) { /* A worker's Off event raced the completed validation. */ }
    }

    private void QuietUpdateDismissButton_Click(object sender, RoutedEventArgs e) => QuietUpdatePopover.IsOpen = false;

    private void QuietUpdatePopover_Closed(object? sender, EventArgs e) => CancelQuietUpdatePreparation();

    private void QuietUpdatePopover_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        QuietUpdatePopover.IsOpen = false;
    }
}
