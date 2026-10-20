namespace UltraExplorer.Services;

/// <summary>
/// One detail demand and one bounded retained result. Renderer lookups never
/// touch metadata or decoders. A canceled provider still owns its real active
/// slot until it returns, so panning cannot create replacement work around it.
/// </summary>
internal sealed class FocusedFileDetailController : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<string, CancellationToken, Task<FocusedFileDetailResult?>> _fetch;
    private readonly Action<Action> _post;
    private readonly Action _changed;
    private readonly FocusedFileDetailOptions _options;
    private InlineThumbnailCandidate? _wanted;
    private FocusedFileDetailResult? _result;
    private Work? _active;
    private CancellationTokenSource? _settle;
    private long _generation, _settledAt, _bytes, _requests;
    private bool _moving, _attempted, _disposed, _notificationQueued;

    public FocusedFileDetailController(Func<string, CancellationToken, Task<FocusedFileDetailResult?>> fetch,
        Action<Action> post, Action changed, FocusedFileDetailOptions? options = null)
    {
        _fetch = fetch;
        _post = post;
        _changed = changed;
        _options = options ?? new FocusedFileDetailOptions();
    }

    public void Update(InlineThumbnailCandidate? focus, bool moving)
    {
        Work? cancel = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (focus is { } candidate && string.IsNullOrWhiteSpace(candidate.Path)) focus = null;
            var changed = focus != _wanted;
            if (changed)
            {
                _wanted = focus;
                _generation++;
                _attempted = false;
                _result = null;
                _bytes = 0;
                _notificationQueued = false;
                cancel = _active;
                CancelSettleLocked();
            }
            if (changed || _moving && !moving)
                _settledAt = _options.TickCount() + Math.Max(0, _options.SettleMilliseconds);
            _moving = moving;
            ScheduleLocked();
        }
        cancel?.Cancel();
    }

    public bool TryGet(string path, long version, out FocusedFileDetailResult? result)
    {
        lock (_gate)
        {
            result = null;
            if (_disposed || _wanted is not { } wanted || wanted.Path != path || wanted.Version != version || _result is null)
                return false;
            result = _result;
            return true;
        }
    }

    public void Suspend() => Update(null, moving: false);

    public void Dispose()
    {
        Work? cancel;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _wanted = null;
            _result = null;
            _bytes = 0;
            _notificationQueued = false;
            CancelSettleLocked();
            cancel = _active;
        }
        cancel?.Cancel();
    }

    internal FocusedFileDetailDiagnostics Diagnostics
    {
        get { lock (_gate) return new(_wanted is not null, _active is null ? 0 : 1, _bytes, _requests); }
    }

    private void ScheduleLocked()
    {
        if (_disposed || _moving || _wanted is null || _attempted || _active is not null)
        {
            CancelSettleLocked();
            return;
        }
        if (_settle is not null) return;
        var settle = new CancellationTokenSource();
        _settle = settle;
        _ = SettleAsync(settle, (int)Math.Clamp(_settledAt - _options.TickCount(), 1, int.MaxValue));
    }

    private void CancelSettleLocked()
    {
        var settle = _settle;
        _settle = null;
        settle?.Cancel();
    }

    private async Task SettleAsync(CancellationTokenSource settle, int delay)
    {
        try
        {
            await _options.Delay(delay, settle.Token).ConfigureAwait(false);
            Work start;
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_settle, settle)) return;
                _settle = null;
                if (_options.TickCount() < _settledAt) { ScheduleLocked(); return; }
                if (_moving || _wanted is not { } wanted || _attempted || _active is not null) return;
                _active = start = new Work(wanted, _generation);
                _attempted = true;
                _requests++;
            }
            _ = Task.Run(() => FetchAsync(start));
        }
        catch (OperationCanceledException) when (settle.IsCancellationRequested) { }
        finally { settle.Dispose(); }
    }

    private async Task FetchAsync(Work work)
    {
        FocusedFileDetailResult? result = null;
        long bytes = 0;
        try
        {
            work.Token.ThrowIfCancellationRequested();
            result = await _fetch(work.Candidate.Path, work.Token).ConfigureAwait(false);
            work.Token.ThrowIfCancellationRequested();
            result = Validate(result, out bytes);
        }
        catch (Exception)
        {
            // Also discard a provider result whose dispatcher-owned bitmap
            // throws during validation; it must never escape into rendering.
            result = null;
            bytes = 0;
        }
        lock (_gate)
        {
            if (ReferenceEquals(_active, work)) _active = null;
            if (!_disposed && !work.Token.IsCancellationRequested && work.Generation == _generation && _wanted == work.Candidate)
            {
                _result = result;
                _bytes = bytes;
                if (result is not null) QueueChangedLocked();
            }
            ScheduleLocked();
        }
        work.Dispose();
    }

    private FocusedFileDetailResult? Validate(FocusedFileDetailResult? result, out long bytes)
    {
        bytes = 0;
        if (result is null) return null;
        if (result.Image is { } image)
        {
            if (!image.IsFrozen || image.PixelWidth <= 0 || image.PixelHeight <= 0
                || image.PixelWidth > _options.MaximumDimension || image.PixelHeight > _options.MaximumDimension) return null;
            bytes = ((long)image.PixelWidth * Math.Max(1, image.Format.BitsPerPixel) + 7) / 8 * image.PixelHeight;
        }
        if (result.Document is { } document)
        {
            var text = document.Text;
            var limit = Math.Clamp(_options.MaximumCharacters, 1, FocusedFileDetailProvider.MaximumCharacters);
            if (text.Length > limit)
            {
                if (char.IsHighSurrogate(text[limit - 1])) limit--;
                document = document with { Text = text[..limit], CanEdit = false, Truncated = true };
            }
            if (document.Detail.Length > 512) document = document with { Detail = document.Detail[..512] };
            result = result with { Document = document };
            bytes += (long)(document.Text.Length + document.Detail.Length) * sizeof(char);
        }
        if (result.Image is null && result.Document is null || bytes > Math.Max(0, _options.MaximumCacheBytes))
        { bytes = 0; return null; }
        return result;
    }

    private void QueueChangedLocked()
    {
        if (_notificationQueued || _disposed) return;
        _notificationQueued = true;
        var generation = _generation;
        try
        {
            _post(() =>
            {
                lock (_gate)
                {
                    if (_disposed || generation != _generation) return;
                    _notificationQueued = false;
                    if (_wanted is not null) _changed();
                }
            });
        }
        catch (InvalidOperationException) { _notificationQueued = false; }
    }

    private sealed class Work : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _source = new();
        private Task? _callbacks;
        private bool _disposed;
        public InlineThumbnailCandidate Candidate { get; }
        public long Generation { get; }
        public CancellationToken Token { get; }
        public Work(InlineThumbnailCandidate candidate, long generation)
        { Candidate = candidate; Generation = generation; Token = _source.Token; }
        public void Cancel()
        {
            lock (_gate) if (!_disposed) _callbacks ??= _source.CancelAsync();
        }
        public void Dispose()
        {
            Task? callbacks;
            lock (_gate) { if (_disposed) return; _disposed = true; callbacks = _callbacks; }
            if (callbacks is { IsCompleted: false })
                _ = callbacks.ContinueWith(_ => _source.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else _source.Dispose();
        }
    }
}

internal sealed class FocusedFileDetailOptions
{
    public int SettleMilliseconds { get; init; } = 120;
    public int MaximumDimension { get; init; } = FocusedFileDetailProvider.MaximumDimension;
    public int MaximumCharacters { get; init; } = FocusedFileDetailProvider.MaximumCharacters;
    public long MaximumCacheBytes { get; init; } = 16L * 1024 * 1024;
    public Func<long> TickCount { get; init; } = () => Environment.TickCount64;
    public Func<int, CancellationToken, Task> Delay { get; init; } = Task.Delay;
}

internal readonly record struct FocusedFileDetailDiagnostics(bool HasDemand, int ActiveRequests, long CacheBytes, long RequestsStarted);
