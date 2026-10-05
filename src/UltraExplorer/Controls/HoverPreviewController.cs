using System.Windows.Threading;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

/// <summary>One hovered file, with a dwell and cancellation; entirely independent of selection.</summary>
internal sealed class HoverPreviewController : IDisposable
{
    internal static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(220);
    private readonly Func<string, CancellationToken, Task<ThumbnailResult?>> _load;
    private readonly DispatcherTimer _dwell;
    private CancellationTokenSource? _request;
    private int _ticket;
    private bool _disposed;

    internal HoverPreviewController(Dispatcher dispatcher, Func<string, CancellationToken, Task<ThumbnailResult?>> load,
        TimeSpan? dwell = null)
    {
        _load = load;
        _dwell = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = dwell ?? Dwell };
        _dwell.Tick += OnDwell;
    }

    internal string? Path { get; private set; }
    internal ThumbnailResult? Result { get; private set; }
    internal bool IsLoading { get; private set; }
    internal event Action? Changed;

    internal void Hover(string? path)
    {
        if (_disposed || string.Equals(Path, path, StringComparison.OrdinalIgnoreCase)) return;
        Clear();
        Path = path;
        if (!string.IsNullOrEmpty(path)) _dwell.Start();
    }

    internal void Clear()
    {
        _ticket++;
        _dwell.Stop();
        _request?.Cancel();
        _request?.Dispose();
        _request = null;
        Path = null;
        Result = null;
        IsLoading = false;
        Changed?.Invoke();
    }

    private async void OnDwell(object? sender, EventArgs args)
    {
        _dwell.Stop();
        if (_disposed || Path is not { } path) return;
        var ticket = _ticket;
        var request = _request = new CancellationTokenSource();
        IsLoading = true;
        Changed?.Invoke();
        ThumbnailResult? result = null;
        try
        {
            // A blocked share or third-party provider never leaves a permanent loading card.
            result = await _load(path, request.Token).WaitAsync(TimeSpan.FromSeconds(3), request.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            if (ticket == _ticket) request.Cancel();
        }
        catch (Exception)
        {
            // Previews are optional; an unavailable codec must not break navigation.
        }

        if (_disposed || ticket != _ticket) return;
        Result = result;
        IsLoading = false;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
        _dwell.Tick -= OnDwell;
        Changed = null;
    }
}
