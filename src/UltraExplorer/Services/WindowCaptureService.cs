using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace UltraExplorer.Services;

/// <summary>
/// Development aid enabled only by <c>--capture &lt;path&gt;</c>: renders the
/// live visual tree to a PNG.  It writes once after the window settles and then
/// again whenever a sibling <c>&lt;path&gt;.request</c> file appears, which lets a
/// test harness snapshot any UI state without fighting DPI or window z-order.
/// Each window the main one owns that is open - Settings, say - is written
/// beside it, named after its title: <c>shot-Settings.png</c> for <c>shot.png</c>.
/// </summary>
public sealed class WindowCaptureService : IDisposable
{
    private const string CaptureSwitch = "--capture";

    private readonly Window _window;
    private readonly string _outputPath;
    private readonly string _requestPath;
    private readonly DispatcherTimer _poll;
    private bool _isDisposed;

    private WindowCaptureService(Window window, string outputPath)
    {
        _window = window;
        _outputPath = outputPath;
        _requestPath = outputPath + ".request";

        _poll = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _poll.Tick += (_, _) => CaptureIfRequested();
    }

    /// <summary>Returns null unless the process was started with --capture.</summary>
    public static WindowCaptureService? TryCreate(Window window, IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], CaptureSwitch, StringComparison.OrdinalIgnoreCase))
            {
                return new WindowCaptureService(window, arguments[index + 1]);
            }
        }

        return null;
    }

    public void Start()
    {
        _poll.Start();
        _ = _window.Dispatcher.InvokeAsync(
            () => Capture(_outputPath),
            DispatcherPriority.ContextIdle);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _poll.Stop();
    }

    private void CaptureIfRequested()
    {
        try
        {
            if (!File.Exists(_requestPath))
            {
                return;
            }

            File.Delete(_requestPath);
            Capture(_outputPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Capture(string path)
    {
        Capture(_window, path);
        foreach (Window owned in _window.OwnedWindows)
        {
            if (owned.IsVisible)
            {
                var name = string.Concat(owned.Title.Split(Path.GetInvalidFileNameChars()));
                Capture(owned, Path.Combine(
                    Path.GetDirectoryName(path) ?? string.Empty,
                    $"{Path.GetFileNameWithoutExtension(path)}-{name}{Path.GetExtension(path)}"));
            }
        }
    }

    private static void Capture(Window window, string path)
    {
        try
        {
            var source = PresentationSource.FromVisual(window);
            var scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
            var scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1;
            var width = (int)Math.Ceiling(window.ActualWidth * scaleX);
            var height = (int)Math.Ceiling(window.ActualHeight * scaleY);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var target = new RenderTargetBitmap(width, height, 96 * scaleX, 96 * scaleY, PixelFormats.Pbgra32);
            target.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            encoder.Save(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OverflowException)
        {
        }
    }
}
