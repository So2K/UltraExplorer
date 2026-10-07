using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
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
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _session;
    private CancellationTokenRegistration _processCancellation;
    private Process? _process;
    private string? _pipeName;
    private string? _sessionDirectory;
    private IntPtr _modelWindow;
    private int _generation;
    private bool _disposed;
    internal string? LastError { get; private set; }
    internal string? LastNativeDiagnostic { get; private set; }
    internal int? OwnedProcessId => _process is { } process && !process.HasExited ? process.Id : null;
    internal IntPtr ViewportHandle => _surface.Window;
    internal IntPtr ModelWindowHandle => _modelWindow;
    internal string? ModelSnapshotPath => _sessionDirectory is null ? null : Path.Combine(_sessionDirectory, "viewport.png");
    internal void RequestModelSnapshot() => _surface.ModelKey(0x7B); // F12, sent only to the owned viewport.

    internal NativePreviewHost()
    {
        SetResourceReference(BackgroundProperty, "CanvasBrush");
        SetResourceReference(ForegroundProperty, "TextMutedBrush");
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        var header = new DockPanel { LastChildFill = true, Height = 42 };
        header.SetResourceReference(Panel.BackgroundProperty, "SurfaceBrush");
        DockPanel.SetDock(_tools, Dock.Right);
        header.Children.Add(_tools);
        header.Children.Add(_status);
        grid.Children.Add(header);
        Grid.SetRow(_surface, 1);
        grid.Children.Add(_surface);
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
                    "--idle=yes", "--force-window=yes", "--keep-open=yes", "--pause=yes", "--osc=yes", "--hwdec=auto-safe",
                    "--input-default-bindings=yes", "--input-vo-keyboard=yes", "--input-cursor=yes",
                    "--sub-auto=no", "--audio-file-auto=no", "--save-position-on-quit=no", "--write-filename-in-watch-later-config=no",
                    "--script-opts=osc-visibility=always", "--", DocumentPreviewService.LiteralPath(path)];
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
                AddButton("Reset view", () => _surface.ModelKey(0x0D));
                AddButton("Edges", () => _surface.ModelKey('E'));
                AddButton("Animate", () => _surface.ModelKey(0x20));
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
                _status.Text = "mpv · playback controls below";
                AddButton("Play / pause", () => _ = SafeMpvCommandAsync(["cycle", "pause"]));
                AddButton("−10s", () => _ = SafeMpvCommandAsync(["seek", -10, "relative"]));
                AddButton("+10s", () => _ = SafeMpvCommandAsync(["seek", 10, "relative"]));
                AddButton("Mute", () => _ = SafeMpvCommandAsync(["cycle", "mute"]));
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
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
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (IOException) { }
        return null;
    }

    private async Task SafeMpvCommandAsync(object[] command)
    {
        try { await MpvCommandAsync(command, _lifetime.Token); }
        catch (OperationCanceledException) { }
    }

    private void StopSession()
    {
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
