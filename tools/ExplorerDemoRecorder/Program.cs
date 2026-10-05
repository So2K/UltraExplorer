using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ExplorerDemoRecorder;

/// <summary>
/// A media recorder only. It never launches, focuses, navigates or sends input
/// to Explorer. The caller supplies an already observed HWND and drives it.
/// WGC/D3D interop follows Microsoft's Win32 composition capture sample and
/// the CsWinRT interop guide (links in README.md).
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help"))
        {
            Console.WriteLine("ExplorerDemoRecorder --window <decimal HWND or 0xHEX> --out <folder> [--seconds 120] [--fps 30] [--ffmpeg <path>] [--app-window]");
            Console.WriteLine("Records only the supplied window. Standard input accepts: mark <label>, stop.");
            return 0;
        }

        try
        {
            string Option(string key, string? fallback = null)
            {
                var at = Array.IndexOf(args, key);
                return at >= 0 && at + 1 < args.Length ? args[at + 1]
                    : fallback ?? throw new ArgumentException($"Missing {key}.");
            }
            var windowText = Option("--window");
            var window = windowText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? (nint)long.Parse(windowText.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : (nint)long.Parse(windowText, CultureInfo.InvariantCulture);
            var fps = int.Parse(Option("--fps", "30"), CultureInfo.InvariantCulture);
            var seconds = double.Parse(Option("--seconds", "120"), CultureInfo.InvariantCulture);
            if (fps is < 1 or > 60 || seconds is <= 0 or > 600)
                throw new ArgumentOutOfRangeException(nameof(args), "FPS must be 1–60; duration must be 0–600 seconds.");
            ValidateWindow(window, args.Contains("--app-window"));
            if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Windows Graphics Capture is unavailable.");
            var output = Path.GetFullPath(Option("--out"));
            Directory.CreateDirectory(output);
            if (File.Exists(Path.Combine(output, "capture.mkv")))
                throw new IOException("Output already contains capture.mkv. Use a fresh directory.");
            using var recorder = new Recorder(window, output, fps, seconds, Option("--ffmpeg", "ffmpeg.exe"));
            await recorder.RunAsync();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void ValidateWindow(nint window, bool appWindow)
    {
        if (window == 0 || !IsWindow(window)) throw new ArgumentException("The supplied HWND is not a live window.");
        GetWindowThreadProcessId(window, out var processId);
        using var process = Process.GetProcessById((int)processId);
        var className = new StringBuilder(256);
        GetClassName(window, className, className.Capacity);
        if (!appWindow && (!process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) || className.ToString() != "CabinetWClass"))
            throw new ArgumentException("The supplied HWND must be an already observed File Explorer window.");
        if (IsIconic(window)) throw new ArgumentException("Restore the Explorer window before recording.");
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int capacity);
}

internal sealed class Recorder : IDisposable
{
    private readonly object _gate = new();
    private readonly nint _window;
    private readonly string _output;
    private readonly int _fps;
    private readonly double _seconds;
    private readonly string _ffmpeg;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch _clock = new();
    private readonly List<object> _marks = [];
    private readonly List<double> _frameTimes = [];
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _staging;
    private IDirect3DDevice? _winrtDevice;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private byte[] _latest = [];
    private int _width;
    private int _height;
    private long _lastReadback;
    private int _captured;
    private Exception? _captureError;

    internal Recorder(nint window, string output, int fps, double seconds, string ffmpeg)
    {
        _window = window;
        _output = output;
        _fps = fps;
        _seconds = seconds;
        _ffmpeg = ffmpeg;
    }

    internal async Task RunAsync()
    {
        var itemInterop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        // The default-interface IID, rather than a projected runtime-class GUID.
        var itemId = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        Marshal.ThrowExceptionForHR(itemInterop.CreateForWindow(_window, in itemId, out var itemPointer));
        GraphicsCaptureItem item;
        try { item = MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPointer); }
        finally { Marshal.Release(itemPointer); }
        _width = item.Size.Width;
        _height = item.Size.Height;
        if (_width <= 0 || _height <= 0) throw new InvalidOperationException("Capture target has no drawable size.");
        _latest = new byte[checked(_width * _height * 4)];
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0], out _device, out _, out _context).CheckError();
        using (var dxgiDevice = _device!.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var devicePointer));
            try { _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(devicePointer); }
            finally { Marshal.Release(devicePointer); }
        }
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        _pool.FrameArrived += OnFrame;
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = false;
        // Keep Windows' visible capture border. No access/permission requests.
        _session.StartCapture();
        await _firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var start = new ProcessStartInfo(_ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-f", "rawvideo", "-pixel_format", "bgra",
            "-video_size", $"{_width}x{_height}", "-framerate", _fps.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0",
            "-an", "-c:v", "ffv1", "-level", "3", "-g", "1", "-threads", "4", "-pix_fmt", "bgra", Path.Combine(_output, "capture.mkv") })
            start.ArgumentList.Add(argument);
        using var encoder = Process.Start(start) ?? throw new IOException("FFmpeg could not start.");
        var encoderErrors = encoder.StandardError.ReadToEndAsync();
        _clock.Start();
        var input = encoder.StandardInput.BaseStream;
        var pixels = new byte[_latest.Length];
        Console.WriteLine($"READY: recording supplied HWND {_window} at {_width}x{_height}, {_fps} fps. Use stdin 'mark <label>' or 'stop'.");
        // Console's synchronized TextReader may block before ReadLineAsync
        // returns a Task. Keep stdin entirely off the sampling loop thread.
        _ = Task.Run(ReadCommandsAsync);
        try
        {
            for (var index = 0; !_stop.IsCancellationRequested && _clock.Elapsed.TotalSeconds < _seconds; index++)
            {
                var target = index / (double)_fps;
                var wait = target - _clock.Elapsed.TotalSeconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), _stop.Token);
                lock (_gate) _latest.CopyTo(pixels, 0);
                _frameTimes.Add(_clock.Elapsed.TotalSeconds);
                // Complete a whole raw frame even when stop arrives midway;
                // cancelling this write would leave a truncated FFmpeg packet.
                await input.WriteAsync(pixels);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            _clock.Stop();
            input.Close();
            await encoder.WaitForExitAsync();
            var errorText = await encoderErrors;
            await File.WriteAllTextAsync(Path.Combine(_output, "ffmpeg.log"), errorText);
            object[] marks;
            lock (_gate) marks = _marks.ToArray();
            await File.WriteAllTextAsync(Path.Combine(_output, "timing.json"), JsonSerializer.Serialize(new
            {
                source = "Windows.Graphics.Capture, supplied real window",
                input = "no simulated input; caller drives the window",
                speed = "real time",
                width = _width, height = _height, requestedFps = _fps,
                writtenFrames = _frameTimes.Count, readbackFrames = _captured,
                seconds = _clock.Elapsed.TotalSeconds,
                actualWrittenFps = _frameTimes.Count / Math.Max(.001, _clock.Elapsed.TotalSeconds),
                frameTimes = _frameTimes, marks,
                error = _captureError?.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (encoder.ExitCode != 0) throw new IOException($"FFmpeg exited {encoder.ExitCode}: {errorText}");
        }
        if (_captureError is not null) throw new InvalidOperationException("Capture was interrupted.", _captureError);
        Console.WriteLine($"DONE: {_frameTimes.Count} frames over {_clock.Elapsed.TotalSeconds:F3} seconds; {_output}");
    }

    private async Task ReadCommandsAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var command = await Console.In.ReadLineAsync();
            if (command is null) return;
            if (command.Equals("stop", StringComparison.OrdinalIgnoreCase)) { _stop.Cancel(); return; }
            if (command.StartsWith("mark ", StringComparison.OrdinalIgnoreCase))
            {
                lock (_gate) _marks.Add(new { seconds = _clock.Elapsed.TotalSeconds, label = command[5..] });
                Console.WriteLine($"MARK {_clock.Elapsed.TotalSeconds:F3}: {command[5..]}");
            }
        }
    }

    private unsafe void OnFrame(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;
            lock (_gate)
            {
                if (_stop.IsCancellationRequested) return;
                if (frame.ContentSize.Width != _width || frame.ContentSize.Height != _height)
                    throw new InvalidOperationException("The recorded window was resized. Keep it at a fixed size for a comparison.");
                var timestamp = Stopwatch.GetTimestamp();
                if (_lastReadback != 0 && (timestamp - _lastReadback) / (double)Stopwatch.Frequency < 1d / _fps * .9) return;
                _lastReadback = timestamp;
                var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
                var textureId = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
                Marshal.ThrowExceptionForHR(access.GetInterface(in textureId, out var texturePointer));
                using var texture = new ID3D11Texture2D(texturePointer);
                if (_staging is null)
                {
                    var description = texture.Description;
                    description.Usage = ResourceUsage.Staging;
                    description.BindFlags = BindFlags.None;
                    description.CPUAccessFlags = CpuAccessFlags.Read;
                    description.MiscFlags = ResourceOptionFlags.None;
                    _staging = _device!.CreateTexture2D(in description);
                }
                _context!.CopyResource(_staging, texture);
                var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    fixed (byte* destination = _latest)
                    {
                        for (var row = 0; row < _height; row++)
                            Buffer.MemoryCopy((byte*)mapped.DataPointer + row * mapped.RowPitch, destination + row * _width * 4,
                                _width * 4, _width * 4);
                    }
                }
                finally { _context.Unmap(_staging, 0); }
                _captured++;
                _firstFrame.TrySetResult();
            }
        }
        catch (Exception error)
        {
            _captureError = error;
            _firstFrame.TrySetException(error);
            _stop.Cancel();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _session?.Dispose();
        if (_pool is not null) { _pool.FrameArrived -= OnFrame; _pool.Dispose(); }
        lock (_gate)
        {
            _staging?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
            _winrtDevice?.Dispose();
        }
        _stop.Dispose();
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(nint window, in Guid iid, out nint item);
        [PreserveSig] int CreateForMonitor(nint monitor, in Guid iid, out nint item);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig] int GetInterface(in Guid iid, out nint result);
    }
}
