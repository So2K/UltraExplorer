using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services.Updates;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task QuietUpdateUiChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(QuietUpdateUiChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(QuietUpdateUiChecks));
            return Task.CompletedTask;
        }
        if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1"
            || !DialogFixtureProcessScope.IsIsolatedDirectory(AppPaths.StateDirectory))
            throw new InvalidOperationException("Quiet update UI checks require isolated state and test-window mode.");
        RunOnSta("quiet update presentation, unshown window", QuietUpdateUiOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task QuietUpdateUiOnStaAsync()
    {
        Section("quiet updates: no automatic panel, explicit install gate, shared setting");
        if (Application.Current is null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        }
        var owned = Path.Combine(AppPaths.StateDirectory, "quiet-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(owned);
        using var forbiddenNetwork = new QuietUiForbiddenNetworkHandler();
        using var service = new QuietUpdateService(Path.Combine(owned, "updates"), "1.3.0-beta.1", forbiddenNetwork, TimeProvider.System);
        var window = new MainWindow(null, Path.Combine(owned, "workspace.json"));
        var shell = (MainViewModel)window.DataContext;
        shell.SuppressShellWrites = true;
        using var settings = new SettingsViewModel(shell, true, null, (_, _, _) => false, quietUpdates: service);
        try
        {
            window.InitializeQuietUpdates(service);
            Check("construction observes local state without checking GitHub", forbiddenNetwork.Requests == 0 && service.State == QuietUpdateState.Idle);
            Check("idle has no visible indicator or panel", window.QuietUpdateButton.Visibility == Visibility.Collapsed && !window.QuietUpdatePopover.IsOpen);

            foreach (var phase in Enum.GetValues<QuietUpdateState>())
            {
                window.ApplyQuietUpdatePresentation(true, phase, "1.3.0-beta.2", .25);
                Check("automatic " + phase + " never opens an update panel or native window",
                    !window.QuietUpdatePopover.IsOpen && !window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero);
            }
            window.ApplyQuietUpdatePresentation(true, QuietUpdateState.Downloading, "", .25);
            QuietUiSaveImage(LayOutWindow(window, 1200, 760), "download-header", headerOnly: true);
            Check("download shows only the muted ring and cannot be pressed",
                window.QuietUpdateButton.Visibility == Visibility.Visible && window.QuietUpdateRing.Visibility == Visibility.Visible
                && window.QuietUpdateArrow.Visibility == Visibility.Collapsed && !window.QuietUpdateButton.IsEnabled);
            window.ApplyQuietUpdatePresentation(true, QuietUpdateState.Ready, "1.3.0-beta.2", 1);
            QuietUiSaveImage(LayOutWindow(window, 1200, 760), "ready-header", headerOnly: true);
            Check("ready shows the small arrow without opening the offer",
                window.QuietUpdateButton.IsEnabled && window.QuietUpdateArrow.Visibility == Visibility.Visible
                && window.QuietUpdateRing.Visibility == Visibility.Collapsed && !window.QuietUpdatePopover.IsOpen
                && window.QuietUpdateButton.Width <= 24 && window.QuietUpdateButton.Height <= 24);

            var launches = 0;
            window.QuietUpdateInstallLauncher = (_, _) => { launches++; return Task.FromResult(new UpdateApplyLaunch(0, Path.Combine(owned, "unused-result.json"))); };
            window.QuietUpdateInstallButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check("an install event without an explicitly opened ready panel cannot launch anything", launches == 0);

            // A generated, owned cached payload allows real service events with
            // no network and no global preference changes.
            var payload = "generated quiet update UI fixture"u8.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            var store = new QuietUpdateStore(Path.Combine(owned, "updates"));
            Directory.CreateDirectory(store.DirectoryPath);
            File.WriteAllBytes(store.PackagePath(hash), payload);
            store.SetReady(new QuietUpdateStore.Package("1.3.0-beta.2", hash, payload.Length));
            store.RecordAttempt(DateTimeOffset.UtcNow);
            await service.CheckOnceAsync();
            await SettingsSettle();
            Check("a real worker ready event keeps the offer closed", service.HasUpdate && window.QuietUpdateButton.IsEnabled && !window.QuietUpdatePopover.IsOpen);
            Check("cached preparation stayed offline", forbiddenNetwork.Requests == 0);

            // WPF defers an attached Popup's IsOpen until its owner is loaded.
            // This owner is deliberately unshown. Detach the actual Popup from
            // that logical tree and its placement binding for simulated clicks;
            // use an empty off-screen popup with no capture or visible content.
            // The production click handlers and their IsOpen gate stay intact.
            var panel = window.QuietUpdatePopover.Child;
            var popupParent = (Panel)LogicalTreeHelper.GetParent(window.QuietUpdatePopover);
            var popupIndex = popupParent.Children.IndexOf(window.QuietUpdatePopover);
            var placementBinding = BindingOperations.GetBinding(window.QuietUpdatePopover, Popup.PlacementTargetProperty);
            var placement = window.QuietUpdatePopover.Placement;
            var staysOpen = window.QuietUpdatePopover.StaysOpen;
            popupParent.Children.Remove(window.QuietUpdatePopover);
            window.QuietUpdatePopover.Child = null;
            window.QuietUpdatePopover.PlacementTarget = null;
            window.QuietUpdatePopover.Placement = PlacementMode.AbsolutePoint;
            window.QuietUpdatePopover.HorizontalOffset = -32000;
            window.QuietUpdatePopover.VerticalOffset = -32000;
            window.QuietUpdatePopover.StaysOpen = true;
            try
            {
                window.QuietUpdateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check("only a ready-arrow click opens the named version and still does not install", window.QuietUpdatePopover.IsOpen
                    && window.QuietUpdateVersionText.Text == "UltraExplorer 1.3.0-beta.2" && launches == 0);
                if (panel is FrameworkElement previewPanel) QuietUiSavePanel(window, previewPanel);
                window.QuietUpdatePopover.IsOpen = false;
                Check("dismissing the offer keeps this version indefinitely", service.State == QuietUpdateState.Ready && launches == 0);
                var validating = false;
                var validationCancelled = false;
                window.QuietUpdateInstallLauncher = async (_, token) =>
                {
                    validating = true;
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    finally { validationCancelled = token.IsCancellationRequested; }
                    launches++;
                    return new UpdateApplyLaunch(0, Path.Combine(owned, "unused-result.json"));
                };
                window.QuietUpdateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                window.QuietUpdateInstallButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await QuietUiWaitAsync(() => validating);
                Check("the cancellation fixture holds validation before its process launch", validating);
                service.Enabled = false;
                await QuietUiWaitAsync(() => validationCancelled);
                await SettingsSettle();
                Check("Off invalidates pending preparation before a helper can start", launches == 0 && !window.QuietUpdatePopover.IsOpen && !service.Enabled);
                service.Enabled = true;
                await service.CheckOnceAsync();
                await SettingsSettle();
                window.QuietUpdateInstallLauncher = (_, _) => { launches++; return Task.FromResult(new UpdateApplyLaunch(0, Path.Combine(owned, "unused-result.json"))); };
                window.QuietUpdateResultMonitor = (_, _) => Task.FromResult(false);
                window.QuietUpdateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                window.QuietUpdateInstallButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await QuietUiWaitAsync(() => launches == 1 && service.State == QuietUpdateState.Ready && window.QuietUpdateButton.IsEnabled);
                Check("the explicit Install click alone invokes the launcher", launches == 1);
                Check("a declined or failed install restores the arrow without reopening the panel", service.State == QuietUpdateState.Ready
                    && window.QuietUpdateButton.IsEnabled && !window.QuietUpdatePopover.IsOpen);
                window.QuietUpdateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check("failure details appear only when the user opens the offer again", window.QuietUpdatePopover.IsOpen
                    && window.QuietUpdateStatusText.Visibility == Visibility.Visible && window.QuietUpdateStatusText.Text.Length > 0);

                // ExplorerLaunchRouter can keep two normal windows in one
                // process. Restart Manager may close the initiating window
                // while another vetoes close; the process must still observe
                // the helper's failure and restore its surviving indicator.
                var surviving = new MainWindow(null, Path.Combine(owned, "surviving-workspace.json"));
                var survivingShell = (MainViewModel)surviving.DataContext;
                survivingShell.SuppressShellWrites = true;
                surviving.InitializeQuietUpdates(service);
                var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var monitoring = false;
                var resultToken = CancellationToken.None;
                try
                {
                    window.QuietUpdatePopover.IsOpen = false;
                    service.Enabled = false;
                    service.Enabled = true;
                    var beforeUnconfirmedClose = launches;
                    window.DisposeQuietUpdates();
                    await SettingsSettle();
                    Check("closing before any confirmation leaves idle with no launcher or automatic offer", service.State == QuietUpdateState.Idle
                        && launches == beforeUnconfirmedClose && !window.QuietUpdatePopover.IsOpen && !surviving.QuietUpdatePopover.IsOpen
                        && surviving.QuietUpdateButton.Visibility == Visibility.Collapsed);
                    window.InitializeQuietUpdates(service);
                    await service.CheckOnceAsync();
                    await SettingsSettle();

                    var validatingBeforeStart = false;
                    var cancelledBeforeStart = false;
                    window.QuietUpdateInstallLauncher = async (_, token) =>
                    {
                        validatingBeforeStart = true;
                        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                        finally { cancelledBeforeStart = token.IsCancellationRequested; }
                        launches++;
                        return new UpdateApplyLaunch(0, Path.Combine(owned, "unused-result.json"));
                    };
                    window.QuietUpdateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    window.QuietUpdateInstallButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await QuietUiWaitAsync(() => validatingBeforeStart && service.State == QuietUpdateState.Installing);
                    Check("the two-window prelaunch fixture holds before starting a process", validatingBeforeStart
                        && service.State == QuietUpdateState.Installing && launches == 1);
                    window.DisposeQuietUpdates();
                    await QuietUiWaitAsync(() => cancelledBeforeStart && service.State == QuietUpdateState.Ready && surviving.QuietUpdateButton.IsEnabled);
                    Check("initiator close cancels prelaunch validation and restores the surviving arrow", cancelledBeforeStart
                        && launches == 1 && service.State == QuietUpdateState.Ready && surviving.QuietUpdateButton.IsEnabled);
                    Check("prelaunch cancellation stays quiet and leaves the closed initiator detached", !window.QuietUpdatePopover.IsOpen
                        && !surviving.QuietUpdatePopover.IsOpen && window.QuietUpdateButton.Visibility == Visibility.Collapsed);
                    window.InitializeQuietUpdates(service);
                    window.QuietUpdateInstallLauncher = (_, _) => { launches++; return Task.FromResult(new UpdateApplyLaunch(0, Path.Combine(owned, "unused-result.json"))); };
                    window.QuietUpdateResultMonitor = (_, token) =>
                    {
                        monitoring = true;
                        resultToken = token;
                        return result.Task.WaitAsync(token);
                    };
                    window.QuietUpdateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    window.QuietUpdateInstallButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await QuietUiWaitAsync(() => monitoring && service.State == QuietUpdateState.Installing);
                    Check("two windows observe one explicit installation while its result is pending", monitoring
                        && service.State == QuietUpdateState.Installing && launches == 2 && !surviving.QuietUpdatePopover.IsOpen);
                    window.DisposeQuietUpdates();
                    Check("closing the initiating window keeps the confirmed result observer alive", !resultToken.IsCancellationRequested
                        && service.State == QuietUpdateState.Installing && window.QuietUpdateButton.Visibility == Visibility.Collapsed);
                    result.SetResult(false);
                    await QuietUiWaitAsync(() => service.State == QuietUpdateState.Ready && surviving.QuietUpdateButton.IsEnabled);
                    Check("a failed partial close restores the surviving window's quiet ready arrow", service.State == QuietUpdateState.Ready
                        && surviving.QuietUpdateButton.IsEnabled && surviving.QuietUpdateArrow.Visibility == Visibility.Visible);
                    Check("partial-close recovery never opens either offer or reattaches a closed window", !surviving.QuietUpdatePopover.IsOpen
                        && !window.QuietUpdatePopover.IsOpen && window.QuietUpdateButton.Visibility == Visibility.Collapsed);
                }
                finally
                {
                    result.TrySetResult(false);
                    surviving.DisposeQuietUpdates();
                    survivingShell.Dispose();
                    window.InitializeQuietUpdates(service);
                }
            }
            finally
            {
                window.QuietUpdatePopover.IsOpen = false;
                window.QuietUpdatePopover.Child = panel;
                window.QuietUpdatePopover.Placement = placement;
                window.QuietUpdatePopover.HorizontalOffset = -4;
                window.QuietUpdatePopover.VerticalOffset = 5;
                window.QuietUpdatePopover.StaysOpen = staysOpen;
                if (placementBinding is not null) BindingOperations.SetBinding(window.QuietUpdatePopover, Popup.PlacementTargetProperty, placementBinding);
                popupParent.Children.Insert(popupIndex, window.QuietUpdatePopover);
            }

            var page = new SettingsWindow(settings);
            LayOutWindow(page, 720, 780);
            await SettingsSettle();
            Check("About exposes the receive-updates switch with the service's current value", page.ReceiveUpdatesChoice.IsChecked == true);
            Toggle(page.ReceiveUpdatesChoice);
            await SettingsSettle();
            Check("turning the switch off stops the service and hides the indicator", !service.Enabled && !settings.ReceiveUpdates
                && window.QuietUpdateButton.Visibility == Visibility.Collapsed && !window.QuietUpdatePopover.IsOpen);
            Check("off is remembered in the owned profile", !store.Enabled);
            service.Enabled = true;
            await SettingsSettle();
            Check("a setting changed elsewhere updates the open page", settings.ReceiveUpdates && page.ReceiveUpdatesChoice.IsChecked == true);
            Check("changing settings never starts a request", forbiddenNetwork.Requests == 0);
            window.DisposeQuietUpdates();
            service.Enabled = false;
            await SettingsSettle();
            Check("disposing a window detaches its update presentation", window.QuietUpdateButton.Visibility == Visibility.Collapsed && !window.QuietUpdatePopover.IsOpen);
        }
        finally
        {
            window.DisposeQuietUpdates();
            shell.Dispose();
        }
    }

    private static async Task QuietUiWaitAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(10);
        await SettingsSettle();
    }

    private static void QuietUiSavePanel(MainWindow window, FrameworkElement panel)
    {
        if (Environment.GetEnvironmentVariable("QUIET_UPDATE_SHOTS") is not { Length: > 0 }) return;
        var host = new Border { Background = window.Background, Padding = new Thickness(7), Child = panel };
        TextElement.SetFontFamily(host, window.FontFamily);
        TextElement.SetFontSize(host, window.FontSize);
        host.Measure(new Size(284, 400));
        var height = Math.Ceiling(host.DesiredSize.Height);
        host.Arrange(new Rect(0, 0, 284, height));
        host.UpdateLayout();
        var bitmap = new RenderTargetBitmap(284, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        QuietUiSaveImage(bitmap, "ready-offer");
        host.Child = null;
    }

    private static void QuietUiSaveImage(BitmapSource bitmap, string name, bool headerOnly = false)
    {
        if (Environment.GetEnvironmentVariable("QUIET_UPDATE_SHOTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        var image = headerOnly ? new CroppedBitmap(bitmap, new Int32Rect(0, 0, 420, 72)) : bitmap;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(output);
    }

    private sealed class QuietUiForbiddenNetworkHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("The quiet UI fixture must stay offline.");
        }
    }
}
