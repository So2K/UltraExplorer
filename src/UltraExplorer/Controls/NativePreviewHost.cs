using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

/// <summary>
/// A native child viewport, with a dedicated F3D or mpv process per preview.
/// It never attaches to existing engine windows or sends global keyboard input.
/// </summary>
internal sealed class NativePreviewHost : UserControl, IDisposable
{
    private readonly NativeSurface _surface = new();
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0), FontSize = 12 };
    private readonly StackPanel _tools = new() { Orientation = Orientation.Horizontal };
    private readonly Grid _mediaControls = new() { Margin = new Thickness(12, 5, 12, 10), Visibility = Visibility.Collapsed };
    private readonly Slider _timeline = new() { Minimum = 0, Maximum = 1, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 100, Value = 100, Width = 110, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Volume" };
    private readonly TextBlock _position = new() { Text = "0:00", VerticalAlignment = VerticalAlignment.Center, MinWidth = 46 };
    private readonly TextBlock _duration = new() { Text = "0:00", VerticalAlignment = VerticalAlignment.Center, MinWidth = 46, TextAlignment = TextAlignment.Right };
    private readonly Button _playPause = new() { Content = "Play" };
    private readonly Button _mute = new() { Content = "Mute" };
    private readonly ComboBox _speed = new() { Width = 76, Height = 28, ToolTip = "Playback speed" };
    private readonly DispatcherTimer _playbackTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _seekTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _session;
    private CancellationTokenRegistration _processCancellation;
    private Process? _process;
    private string? _pipeName;
    private string? _sessionDirectory;
    private IntPtr _modelWindow;
    private int _generation;
    private bool _disposed;
    private bool _refreshingPlayback, _syncingControls, _scrubbing, _resumeAfterScrub;
    private double? _pendingSeek;
    private CancellationTokenSource? _seekRequest;
    private long _playbackPolls;
    internal string? LastError { get; private set; }
    internal string? LastNativeDiagnostic { get; private set; }
    internal int? OwnedProcessId => _process is { } process && !process.HasExited ? process.Id : null;
    internal IntPtr ViewportHandle => _surface.Window;
    internal IntPtr ModelWindowHandle => _modelWindow;
    internal bool IsPlaybackTimerRunning => _playbackTimer.IsEnabled;
    internal long PlaybackPollCount => _playbackPolls;
    internal string? ModelSnapshotPath => _sessionDirectory is null ? null : Path.Combine(_sessionDirectory, "viewport.png");
    internal void RequestModelSnapshot() => _surface.ModelKey(0x7B); // F12, sent only to the owned viewport.

    internal NativePreviewHost()
    {
        SetResourceReference(BackgroundProperty, "CanvasBrush");
        SetResourceReference(ForegroundProperty, "TextMutedBrush");
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new DockPanel { LastChildFill = true, Height = 42 };
        header.SetResourceReference(Panel.BackgroundProperty, "SurfaceBrush");
        DockPanel.SetDock(_tools, Dock.Right);
        header.Children.Add(_tools);
        header.Children.Add(_status);
        grid.Children.Add(header);
        Grid.SetRow(_surface, 1);
        grid.Children.Add(_surface);
        BuildMediaControls();
        Grid.SetRow(_mediaControls, 2);
        grid.Children.Add(_mediaControls);
        _playbackTimer.Tick += PlaybackTick;
        _seekTimer.Tick += SeekTick;
        Content = grid;
        Unloaded += (_, _) => Dispose();
    }

    internal async Task LoadAsync(string path, CancellationToken token = default)
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopSession();
        var generation = ++_generation;
        LastError = null;
        _mediaControls.Visibility = Visibility.Collapsed;
        _status.Text = "Loading preview…";
        var model = PreviewTools.IsModel(path);
        var executable = model ? PreviewTools.FindF3d() : PreviewTools.FindMpv();
        if (executable is null)
        {
            LastError = model ? "The 3D engine is unavailable. Reinstall UltraExplorer to restore it."
                : "The media player is unavailable. Reinstall UltraExplorer to restore it.";
            _status.Text = LastError;
            throw new FileNotFoundException(LastError);
        }
        var session = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        _session = session;
        using var loading = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        loading.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await _surface.Ready.WaitAsync(loading.Token);
            if (generation != _generation) throw new OperationCanceledException(session.Token);
            _tools.Children.Clear();
            _sessionDirectory = Path.Combine(Path.GetTempPath(), "UltraExplorerPreview", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sessionDirectory);
            IEnumerable<string> arguments;
            if (model)
            {
                var modelInput = await PreviewTools.PrepareModelPathAsync(path, _sessionDirectory, loading.Token);
                arguments = ["--no-config", "--verbose=error", "--resolution=800,500", "--position=-32000,-32000", "--dpi-aware",
                    "--background-color=0.067,0.075,0.082", "--camera-direction=1,-0.65,-1", "--camera-zoom-factor=0.85",
                    "--light-intensity=1.5", "--axis", "--grid", $"--screenshot-filename={ModelSnapshotPath}", $"--input={modelInput}"];
            }
            else
            {
                _pipeName = "UltraExplorerPreview-" + Guid.NewGuid().ToString("N");
                arguments = ["--no-config", "--load-scripts=no", "--ytdl=no", "--terminal=no", "--msg-level=all=no",
                    $"--wid={_surface.Window.ToInt64()}", $"--input-ipc-server=\\\\.\\pipe\\{_pipeName}",
                    "--idle=yes", "--force-window=yes", "--keep-open=yes", "--pause=yes", "--osc=no", "--hwdec=auto-safe",
                    "--input-default-bindings=no", "--input-builtin-bindings=no", "--input-vo-keyboard=no", "--input-cursor=no",
                    "--load-stats-overlay=no", "--load-console=no", "--load-commands=no", "--load-select=no", "--osd-level=0",
                    "--sub-auto=no", "--audio-file-auto=no", "--save-position-on-quit=no", "--write-filename-in-watch-later-config=no",
                    "--", DocumentPreviewService.LiteralPath(path)];
            }
            var process = PreviewTools.Start(executable, arguments, _sessionDirectory);
            _process = process;
            _processCancellation = session.Token.Register(() => PreviewTools.Kill(process));
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_disposed && generation == _generation)
                {
                    _status.Text = "Preview closed";
                    _playbackTimer.Stop();
                    _seekTimer.Stop();
                    _surface.Detach();
                }
            }));
            if (model)
            {
                var candidate = IntPtr.Zero;
                var stableSamples = 0;
                while (_modelWindow == IntPtr.Zero)
                {
                    loading.Token.ThrowIfCancellationRequested();
                    if (process.HasExited) throw new IOException("F3D could not open this model.");
                    var found = NativeSurface.FindOwnedWindow(process.Id);
                    stableSamples = found != IntPtr.Zero && found == candidate ? stableSamples + 1 : 0;
                    candidate = found;
                    // VTK can recreate the initial HWND while initializing WGL
                    // and DPI. Attach only after a real sized window is stable.
                    if (stableSamples >= 2 && _surface.TryAttach(found, out var diagnostic))
                        _modelWindow = found;
                    else
                    {
                        if (stableSamples >= 2) LastNativeDiagnostic = _surface.LastAttachDiagnostic;
                        await Task.Delay(40, loading.Token);
                    }
                }
                _status.Text = "Drag to orbit · wheel to zoom · Shift + drag to pan";
                AddButton("Fit / reset", () => _surface.ModelKey(0x0D));
            }
            else
            {
                // A pipe being created proves only that mpv started. Wait until
                // an actual file is loaded so corrupt codecs get a clear fallback.
                while (true)
                {
                    loading.Token.ThrowIfCancellationRequested();
                    if (process.HasExited) throw new IOException("mpv could not open this media file.");
                    var response = await MpvCommandAsync(["get_property", "idle-active"], loading.Token);
                    if (response is { } reply && reply.TryGetProperty("data", out var idle) && idle.ValueKind == JsonValueKind.False) break;
                    await Task.Delay(80, loading.Token);
                }
                _status.Text = "Drag the timeline to preview a frame";
                _mediaControls.Visibility = Visibility.Visible;
                await RefreshPlaybackAsync(loading.Token);
                _playbackTimer.Start();
            }
        }
        catch (Exception ex)
        {
            var ownTimeout = ex is OperationCanceledException && !session.IsCancellationRequested;
            if (generation == _generation)
            {
                LastError = ownTimeout
                    ? "The preview engine took too long to load this file." : ex.Message;
                _status.Text = LastError;
                StopSession();
            }
            if (ownTimeout) throw new TimeoutException("The preview engine took too long to load this file.", ex);
            throw;
        }
    }

    private void AddButton(string caption, Action action)
    {
        var button = new Button { Content = caption, Margin = new Thickness(3, 6, 3, 6), Padding = new Thickness(8, 2, 8, 2), MinWidth = 40 };
        button.SetResourceReference(FrameworkElement.StyleProperty, "FlatButton");
        button.Click += (_, _) => action();
        _tools.Children.Add(button);
    }

    internal async Task<JsonElement?> MpvCommandAsync(object[] command, CancellationToken token = default)
    {
        var pipeName = _pipeName;
        if (pipeName is null || _disposed) return null;
        var sessionToken = _session?.Token ?? _lifetime.Token;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token, sessionToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { command, request_id = 1 }) + "\n");
            await pipe.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            for (var index = 0; index < 64; index++)
            {
                var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line is null) return null;
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.TryGetProperty("request_id", out var id) && id.GetInt32() == 1)
                    return json.RootElement.Clone();
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !sessionToken.IsCancellationRequested && !_lifetime.IsCancellationRequested) { }
        catch (IOException) { }
        return null;
    }

    private void BuildMediaControls()
    {
        _mediaControls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _mediaControls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var seek = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        seek.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        seek.ColumnDefinitions.Add(new ColumnDefinition());
        seek.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _timeline.Margin = new Thickness(10, 0, 10, 0);
        Grid.SetColumn(_timeline, 1); Grid.SetColumn(_duration, 2);
        seek.Children.Add(_position); seek.Children.Add(_timeline); seek.Children.Add(_duration);
        _mediaControls.Children.Add(seek);
        var buttons = new DockPanel { LastChildFill = false };
        Grid.SetRow(buttons, 1); _mediaControls.Children.Add(buttons);
        StylePlaybackButton(_playPause); _playPause.Click += (_, _) => RunPlaybackAction(() => TogglePlaybackAsync());
        var stop = new Button { Content = "Stop" }; StylePlaybackButton(stop); stop.Click += (_, _) => RunPlaybackAction(() => StopPlaybackAsync());
        buttons.Children.Add(_playPause); buttons.Children.Add(stop);
        buttons.Children.Add(new TextBlock { Text = "Speed", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 7, 0) });
        _speed.SetResourceReference(Control.ForegroundProperty, "TextMutedBrush");
        _speed.Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ComboBox">
              <Grid>
                <ToggleButton Foreground="{DynamicResource TextMutedBrush}" Focusable="False" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                  <ToggleButton.Template><ControlTemplate TargetType="ToggleButton">
                    <Border Background="{DynamicResource SurfaceRaisedBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="4" Padding="8,3">
                      <DockPanel><TextBlock DockPanel.Dock="Right" Text="⌄" Foreground="{DynamicResource TextMutedBrush}" Margin="8,0,0,0"/>
                        <ContentPresenter Content="{Binding SelectionBoxItem, RelativeSource={RelativeSource AncestorType=ComboBox}}"/>
                      </DockPanel>
                    </Border>
                  </ControlTemplate></ToggleButton.Template>
                </ToggleButton>
                <Popup Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False">
                  <Border Background="{DynamicResource SurfaceRaisedBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" Padding="3">
                    <ScrollViewer CanContentScroll="True"><ItemsPresenter/></ScrollViewer>
                  </Border>
                </Popup>
              </Grid>
            </ControlTemplate>
            """);
        foreach (var value in new[] { 0.25, 0.5, 1, 1.5, 2, 3 })
        {
            var item = new ComboBoxItem { Content = value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×", Tag = value };
            item.SetResourceReference(Control.ForegroundProperty, "TextMutedBrush");
            _speed.Items.Add(item);
        }
        _speed.SelectedIndex = 2;
        _speed.SelectionChanged += (_, _) =>
        {
            if (!_syncingControls && _speed.SelectedItem is ComboBoxItem { Tag: double value }) RunPlaybackAction(() => SetSpeedAsync(value));
        };
        buttons.Children.Add(_speed);
        DockPanel.SetDock(_mute, Dock.Right); StylePlaybackButton(_mute);
        _mute.Click += (_, _) => RunPlaybackAction(async () => { await MpvCommandAsync(["cycle", "mute"]); await RefreshPlaybackAsync(); });
        buttons.Children.Add(_mute);
        DockPanel.SetDock(_volume, Dock.Right); _volume.Margin = new Thickness(10, 0, 4, 0); buttons.Children.Add(_volume);
        var volumeLabel = new TextBlock { Text = "Volume", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(volumeLabel, Dock.Right); buttons.Children.Add(volumeLabel);
        _volume.ValueChanged += (_, _) => { if (!_syncingControls) RunPlaybackAction(() => SetVolumeAsync(_volume.Value)); };
        _timeline.ValueChanged += (_, _) => { if (!_syncingControls) QueueSeek(_timeline.Value); };
        _timeline.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) =>
        {
            _scrubbing = true; _resumeAfterScrub = _playPause.Content as string == "Pause";
            RunPlaybackAction(async () => { await MpvCommandAsync(["set_property", "pause", true]); });
        }));
        _timeline.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            var resume = _resumeAfterScrub; var target = _timeline.Value;
            var token = _session?.Token ?? _lifetime.Token;
            _scrubbing = false; ClearPendingSeek();
            RunPlaybackAction(async () => { await SeekAsync(target, token); if (resume) await MpvCommandAsync(["set_property", "pause", false], token); });
        }));
    }

    private static void StylePlaybackButton(Button button)
    {
        button.Margin = new Thickness(0, 1, 5, 1); button.Padding = new Thickness(9, 4, 9, 4);
        button.SetResourceReference(FrameworkElement.StyleProperty, "FlatButton");
    }

    private async void RunPlaybackAction(Func<Task> action)
    {
        if (_disposed || _pipeName is null) return;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    internal async Task TogglePlaybackAsync(CancellationToken token = default)
    {
        await MpvCommandAsync(["cycle", "pause"], token); await RefreshPlaybackAsync(token);
    }

    internal async Task StopPlaybackAsync(CancellationToken token = default)
    {
        ClearPendingSeek();
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token, _session?.Token ?? _lifetime.Token);
        await MpvCommandAsync(["set_property", "pause", true], session.Token);
        await SeekAsync(0, session.Token);
        await RefreshPlaybackAsync(session.Token);
    }

    internal async Task SetSpeedAsync(double speed, CancellationToken token = default)
    {
        if (!double.IsFinite(speed)) return;
        await MpvCommandAsync(["set_property", "speed", Math.Clamp(speed, 0.25, 3)], token);
        await RefreshPlaybackAsync(token);
    }

    internal async Task SetVolumeAsync(double volume, CancellationToken token = default)
    {
        if (!double.IsFinite(volume)) return;
        await MpvCommandAsync(["set_property", "volume", Math.Clamp(volume, 0, 100)], token);
    }

    internal async Task SeekAsync(double seconds, CancellationToken token = default)
    {
        if (!double.IsFinite(seconds)) return;
        await MpvCommandAsync(["seek", Math.Max(0, seconds), "absolute+exact"], token);
    }

    internal async Task RefreshPlaybackAsync(CancellationToken token = default)
    {
        if (_disposed || _pipeName is null || _refreshingPlayback) return;
        _refreshingPlayback = true;
        var generation = _generation;
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token, _session?.Token ?? _lifetime.Token);
        try
        {
            var position = await MpvCommandAsync(["get_property", "time-pos"], session.Token);
            var duration = await MpvCommandAsync(["get_property", "duration"], session.Token);
            var pause = await MpvCommandAsync(["get_property", "pause"], session.Token);
            var speed = await MpvCommandAsync(["get_property", "speed"], session.Token);
            var volume = await MpvCommandAsync(["get_property", "volume"], session.Token);
            var mute = await MpvCommandAsync(["get_property", "mute"], session.Token);
            if (_disposed || generation != _generation || session.IsCancellationRequested) return;
            _syncingControls = true;
            try
            {
                var seconds = PropertyNumber(position); var total = PropertyNumber(duration);
                _timeline.Maximum = Math.Max(0.001, total); _timeline.IsEnabled = total > 0;
                if (!_scrubbing && _pendingSeek is null) _timeline.Value = Math.Clamp(seconds, 0, _timeline.Maximum);
                _position.Text = FormatTime(_scrubbing ? _timeline.Value : seconds); _duration.Text = FormatTime(total);
                _playPause.Content = PropertyBool(pause) ? "Play" : "Pause";
                _mute.Content = PropertyBool(mute) ? "Unmute" : "Mute";
                _volume.Value = Math.Clamp(PropertyNumber(volume), 0, 100);
                _volume.ToolTip = $"Volume: {_volume.Value:0}%";
                var currentSpeed = PropertyNumber(speed);
                _speed.SelectedItem = _speed.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Tag is double value && Math.Abs(value - currentSpeed) < 0.001);
                _playbackPolls++;
            }
            finally { _syncingControls = false; }
        }
        finally { _refreshingPlayback = false; }
    }

    private static double PropertyNumber(JsonElement? response) => response is { } root
        && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Number && data.TryGetDouble(out var value) && double.IsFinite(value) ? value : 0;
    private static bool PropertyBool(JsonElement? response) => response is { } root
        && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.True;
    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }

    private async void PlaybackTick(object? sender, EventArgs e)
    {
        try { await RefreshPlaybackAsync(_lifetime.Token); }
        catch (OperationCanceledException) { }
    }

    private void QueueSeek(double seconds)
    {
        if (_disposed || _pipeName is null) return;
        _pendingSeek = seconds; _position.Text = FormatTime(seconds);
        _seekRequest?.Cancel();
        if (!_seekTimer.IsEnabled) _seekTimer.Start();
    }

    private async void SeekTick(object? sender, EventArgs e)
    {
        _seekTimer.Stop();
        if (_pendingSeek is not { } seconds || _disposed) return;
        _pendingSeek = null;
        _seekRequest?.Cancel(); _seekRequest?.Dispose();
        var request = CancellationTokenSource.CreateLinkedTokenSource(_session?.Token ?? _lifetime.Token);
        _seekRequest = request;
        try { await SeekAsync(seconds, request.Token); }
        catch (OperationCanceledException) { }
    }

    private void ClearPendingSeek()
    {
        _seekTimer.Stop(); _pendingSeek = null;
        _seekRequest?.Cancel(); _seekRequest?.Dispose(); _seekRequest = null;
    }

    private void StopSession()
    {
        _playbackTimer.Stop(); ClearPendingSeek(); _scrubbing = false;
        _session?.Cancel();
        _session?.Dispose();
        _session = null;
        _processCancellation.Dispose();
        PreviewTools.Kill(_process);
        _process?.Dispose();
        _process = null;
        _pipeName = null;
        _modelWindow = IntPtr.Zero;
        _surface.Detach();
        if (_sessionDirectory is { } directory)
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _sessionDirectory = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ++_generation;
        _lifetime.Cancel();
        StopSession();
        _surface.Dispose();
        _lifetime.Dispose();
    }

    private sealed class NativeSurface : HwndHost
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IntPtr _window, _child;
        internal string? LastAttachDiagnostic { get; private set; }
        internal Task Ready => _ready.Task;
        internal IntPtr Window => _window;
        internal bool TryAttach(IntPtr child, out string? diagnostic)
        {
            diagnostic = null;
            if (!IsWindow(child) || !IsWindow(_window))
            {
                diagnostic = LastAttachDiagnostic = $"invalid HWND: host={_window}, child={child}, hostValid={IsWindow(_window)}, childValid={IsWindow(child)}";
                return false;
            }
            var originalStyle = GetWindowLongPtr(child, -16);
            var originalExtended = GetWindowLongPtr(child, -20);
            var style = originalStyle.ToInt64();
            style = (style & ~(0x00C00000L | 0x00040000L | 0x80000000L)) | 0x40000000L | 0x10000000L;
            SetWindowLongPtr(child, -16, new IntPtr(style));
            var extended = originalExtended.ToInt64();
            SetWindowLongPtr(child, -20, new IntPtr(extended & ~0x00040000L));
            Marshal.SetLastPInvokeError(0);
            SetParent(child, _window);
            var error = Marshal.GetLastPInvokeError();
            var parent = GetParent(child);
            if (parent != _window)
            {
                diagnostic = LastAttachDiagnostic = $"SetParent Win32={error}, host={_window}, child={child}, actualParent={parent}, hostValid={IsWindow(_window)}, childValid={IsWindow(child)}";
                if (IsWindow(child))
                {
                    SetWindowLongPtr(child, -16, originalStyle);
                    SetWindowLongPtr(child, -20, originalExtended);
                }
                return false;
            }
            _child = child;
            ResizeChild();
            ShowWindow(child, 4); // SW_SHOWNOACTIVATE; no focus change on user's desktop.
            return true;
        }
        internal void Detach() { _child = IntPtr.Zero; }
        internal void ModelKey(int key)
        {
            if (_child == IntPtr.Zero) return;
            PostMessage(_child, 0x0100, new IntPtr(key), IntPtr.Zero);
            PostMessage(_child, 0x0101, new IntPtr(key), IntPtr.Zero);
        }
        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            _window = CreateWindowEx(0, "static", "UltraExplorerPreviewViewport", 0x40000000 | 0x10000000 | 0x02000000,
                0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_window == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _ready.TrySetResult();
            return new HandleRef(this, _window);
        }
        protected override void OnWindowPositionChanged(Rect rcBoundingBox)
        {
            base.OnWindowPositionChanged(rcBoundingBox);
            ResizeChild();
        }
        private void ResizeChild()
        {
            if (_window == IntPtr.Zero || _child == IntPtr.Zero) return;
            if (GetClientRect(_window, out var rect))
                SetWindowPos(_child, IntPtr.Zero, 0, 0, Math.Max(1, rect.Right), Math.Max(1, rect.Bottom), 0x0010 | 0x0004 | 0x0020);
        }
        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            Detach();
            DestroyWindow(hwnd.Handle);
            _window = IntPtr.Zero;
        }
        internal static IntPtr FindOwnedWindow(int processId)
        {
            var found = IntPtr.Zero;
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var owner);
                if (owner == (uint)processId)
                {
                    // STARTF_USESHOWWINDOW keeps the initial F3D window hidden.
                    // Its OpenGL driver/IME helper windows share the PID, so
                    // match the actual VTK viewport class, not visibility.
                    var className = new StringBuilder(256);
                    GetClassName(window, className, className.Capacity);
                    if (className.ToString() == "vtkOpenGL" && GetClientRect(window, out var rectangle)
                        && rectangle.Right >= 64 && rectangle.Bottom >= 64) { found = window; return false; }
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }
        private delegate bool EnumWindowCallback(IntPtr hwnd, IntPtr parameter);
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maximumCount);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    }
}
