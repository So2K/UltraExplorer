using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

public enum FolderTagPathKind
{
    Unknown,
    Folder,
    File
}

/// <summary>
/// Turns non-default folder colours into a compact navigation-pane list.
/// Files are classified away on background workers; no filesystem probe is
/// ever made from the dispatcher thread.
/// </summary>
public sealed class FolderTagProjection : ObservableObject, IDisposable
{
    private const int ClassificationConcurrency = 3;
    private const int MaximumCachedDirectoryProbes = 512;
    private const int UnderlyingDirectoryProbeConcurrency = 4;
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly Lock DirectoryProbeGate = new();
    private static readonly Dictionary<string, DirectoryProbe> DirectoryProbes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim UnderlyingLocalProbeSlots = new(
        UnderlyingDirectoryProbeConcurrency,
        UnderlyingDirectoryProbeConcurrency);

    private readonly FolderMarkService _marks;
    private readonly Dispatcher _dispatcher;
    private readonly Func<string, CancellationToken, ValueTask<FolderTagPathKind>> _classifyPath;
    private readonly SemaphoreSlim _classificationSlots = new(ClassificationConcurrency, ClassificationConcurrency);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, long> _revisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FolderTagItemViewModel> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<FolderTagItemViewModel> _items = [];
    private readonly object _taskGate = new();
    private Task? _initializeTask;
    private Task? _refreshTask;
    private CancellationTokenSource? _kindSaveDebounce;
    private string? _activePath;
    private MarksFileStamp _marksFileStamp;
    private DateTime _lastReconcileUtc;
    private long _revisionSequence;
    private bool _subscribed;
    private bool _disposed;

    public FolderTagProjection(
        FolderMarkService marks,
        Dispatcher dispatcher,
        Func<string, CancellationToken, ValueTask<FolderTagPathKind>>? classifyPath = null)
    {
        _marks = marks;
        _dispatcher = dispatcher;
        _classifyPath = classifyPath ?? ClassifyPathAsync;
        Items = new ReadOnlyObservableCollection<FolderTagItemViewModel>(_items);
    }

    public ReadOnlyObservableCollection<FolderTagItemViewModel> Items { get; }
    public bool HasItems => _items.Count > 0;

    /// <summary>
    /// Idempotent and deliberately independent of MainViewModel's Loaded
    /// order. FolderMarkService serializes its reads, so the second load is a
    /// safe no-op unless another process wrote newer marks meanwhile.
    /// </summary>
    public Task InitializeAsync()
    {
        lock (_taskGate)
        {
            return _initializeTask ??= InitializeCoreAsync();
        }
    }

    /// <summary>
    /// Pulls changes written by another process. Same-process changes arrive
    /// immediately through <see cref="FolderMarkService.MarkChanged"/>.
    /// Concurrent activation refreshes share one read.
    /// </summary>
    public async Task RefreshAsync()
    {
        await InitializeAsync();
        if (_disposed)
        {
            return;
        }

        Task refresh;
        lock (_taskGate)
        {
            if (_refreshTask is { IsCompleted: false })
            {
                refresh = _refreshTask;
            }
            else
            {
                refresh = _refreshTask = RefreshCoreAsync();
            }
        }

        await refresh;
    }

    public void SetActivePath(string? activePath)
    {
        if (_disposed)
        {
            return;
        }

        _ = OnDispatcherAsync(() =>
        {
            _activePath = activePath;
            foreach (var item in _items)
            {
                item.IsActive = !string.IsNullOrWhiteSpace(activePath)
                    && ViewAllPath.Equals(item.FullPath, activePath);
            }
        });
    }

    private async Task InitializeCoreAsync()
    {
        if (_disposed)
        {
            return;
        }

        _marks.MarkChanged += OnMarkChanged;
        _subscribed = true;
        try
        {
            var observedBeforeLoad = await ReadMarksFileStampAsync(_lifetime.Token);
            await _marks.LoadAsync(_lifetime.Token);
            // A replacement landing during Load remains newer than the stamp
            // acknowledged here and is therefore picked up next activation.
            _marksFileStamp = observedBeforeLoad;
            await ReconcileAsync(_lifetime.Token, revalidateExisting: false);
            _lastReconcileUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshCoreAsync()
    {
        try
        {
            var stamp = await ReadMarksFileStampAsync(_lifetime.Token);
            var now = DateTime.UtcNow;
            var fileChanged = stamp != _marksFileStamp;
            var periodicRetry = now - _lastReconcileUtc >= ReconcileInterval;
            if (!fileChanged && !periodicRetry)
            {
                return;
            }

            // Periodic reads heal same-length/same-timestamp replacements and
            // transient read failures. A file that changes during this load
            // is deliberately not acknowledged: stamp was captured before it.
            await _marks.LoadAsync(_lifetime.Token);
            _marksFileStamp = stamp;

            // Existing rows are not probed again: a sleeping share must not
            // vanish from Tags. Exact navigation handles a path that really
            // disappeared; this pass only retries marks that could not yet be
            // proven to be folders.
            await ReconcileAsync(_lifetime.Token, revalidateExisting: periodicRetry);
            _lastReconcileUtc = now;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken, bool revalidateExisting)
    {
        var coloured = _marks.Snapshot()
            .Where(pair => !string.IsNullOrEmpty(pair.Value.AccentHex))
            .ToArray();
        var desired = coloured.Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var revision in _revisions.Where(pair => !desired.Contains(pair.Key)).ToArray())
        {
            ForgetDirectoryProbe(revision.Key);
            _revisions.TryRemove(revision);
        }

        await OnDispatcherAsync(() =>
        {
            foreach (var path in _byPath.Keys.Where(path => !desired.Contains(path)).ToArray())
            {
                Remove(path);
            }
        });

        await Task.WhenAll(coloured.Select(pair => EnsureCurrentAsync(
            pair.Key,
            pair.Value.AccentHex,
            NextRevision(pair.Key),
            cancellationToken,
            revalidateExisting)));
    }

    private void OnMarkChanged(string path, FolderMark mark)
    {
        if (_disposed)
        {
            return;
        }

        var revision = NextRevision(path);
        if (string.IsNullOrEmpty(mark.AccentHex))
        {
            ForgetDirectoryProbe(path);
            _ = OnDispatcherAsync(() =>
            {
                if (IsCurrent(path, revision))
                {
                    Remove(path);
                    _revisions.TryRemove(new KeyValuePair<string, long>(path, revision));
                }
            });
            return;
        }

        _ = EnsureCurrentAsync(path, mark.AccentHex, revision, _lifetime.Token, revalidateExisting: false);
    }

    private async Task EnsureCurrentAsync(
        string path,
        string expectedAccent,
        long revision,
        CancellationToken cancellationToken,
        bool revalidateExisting)
    {
        if (_disposed || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (_marks.Get(path).IsDirectory is { } knownKind)
        {
            await OnDispatcherAsync(() =>
            {
                if (!IsCurrent(path, revision))
                {
                    return;
                }

                var current = _marks.Get(path);
                if (string.IsNullOrEmpty(current.AccentHex) || !knownKind)
                {
                    Remove(path);
                }
                else
                {
                    Upsert(path, current.AccentHex);
                }
            });
            return;
        }

        // A known tag is already a known folder: changing red to blue must be
        // instant and must not touch a drive again.
        var updated = false;
        await OnDispatcherAsync(() =>
        {
            if (IsCurrent(path, revision) && _byPath.TryGetValue(path, out var existing))
            {
                var current = _marks.Get(path).AccentHex;
                if (!string.IsNullOrEmpty(current))
                {
                    if (!string.Equals(existing.AccentHex, current, StringComparison.OrdinalIgnoreCase))
                    {
                        Reinsert(existing, current);
                    }
                    updated = !revalidateExisting;
                }
            }
        });
        if (updated || _disposed || !IsCurrent(path, revision))
        {
            return;
        }

        try
        {
            await _classificationSlots.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var kind = FolderTagPathKind.Unknown;
        try
        {
            if (_disposed || !IsCurrent(path, revision))
            {
                return;
            }

            var classification = _classifyPath(path, cancellationToken).AsTask();
            var timeout = Task.Delay(ProbeTimeout, cancellationToken);
            if (await Task.WhenAny(classification, timeout) != classification)
            {
                cancellationToken.ThrowIfCancellationRequested();
                kind = FolderTagPathKind.Unknown;
            }
            else
            {
                kind = await classification;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            kind = FolderTagPathKind.Unknown;
        }
        finally
        {
            _classificationSlots.Release();
        }

        if (_disposed || !IsCurrent(path, revision))
        {
            return;
        }

        // The colour may have changed or been cleared while the slow folder
        // probe was running. Re-read it rather than resurrecting stale data.
        var currentAccent = _marks.Get(path).AccentHex;
        if (string.IsNullOrEmpty(currentAccent))
        {
            await OnDispatcherAsync(() => Remove(path));
            return;
        }

        if (!string.Equals(currentAccent, expectedAccent, StringComparison.OrdinalIgnoreCase)
            && !IsCurrent(path, revision))
        {
            return;
        }

        await OnDispatcherAsync(() =>
        {
            if (_disposed || !IsCurrent(path, revision))
            {
                return;
            }

            if (kind != FolderTagPathKind.File)
            {
                Upsert(path, currentAccent);
            }
            else
            {
                Remove(path);
            }
        });

        // Unknown means unavailable, missing or a legacy remote mark: keep it
        // visible and retryable, never turn a transient failure into a
        // permanent "file" decision.
        if (!_disposed
            && kind != FolderTagPathKind.Unknown
            && _marks.SetItemKind(path, kind == FolderTagPathKind.Folder))
        {
            ScheduleKindSave();
        }
    }

    private void Upsert(string path, string accentHex)
    {
        if (_byPath.TryGetValue(path, out var existing))
        {
            Reinsert(existing, accentHex);
            return;
        }

        var item = new FolderTagItemViewModel(path, accentHex);
        item.IsActive = !string.IsNullOrWhiteSpace(_activePath)
            && ViewAllPath.Equals(item.FullPath, _activePath);
        _byPath[path] = item;
        _items.Insert(InsertionIndex(item), item);
        UpdateDuplicateHints();
        OnPropertyChanged(nameof(HasItems));
    }

    private void Reinsert(FolderTagItemViewModel item, string accentHex)
    {
        if (string.Equals(item.AccentHex, accentHex, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var oldIndex = _items.IndexOf(item);
        if (oldIndex >= 0)
        {
            _items.RemoveAt(oldIndex);
        }

        item.UpdateAccent(accentHex);
        _items.Insert(InsertionIndex(item), item);
    }

    private void Remove(string path)
    {
        if (!_byPath.Remove(path, out var item))
        {
            return;
        }

        _items.Remove(item);
        UpdateDuplicateHints();
        OnPropertyChanged(nameof(HasItems));
    }

    private void UpdateDuplicateHints()
    {
        var duplicates = _items
            .GroupBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Where(group => group.Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        foreach (var item in _items)
        {
            item.SetParentHint(duplicates.Contains(item.Name));
        }
    }

    private int InsertionIndex(FolderTagItemViewModel item)
    {
        var low = 0;
        var high = _items.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (Compare(_items[middle], item) <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static int Compare(FolderTagItemViewModel left, FolderTagItemViewModel right)
    {
        var colour = left.ColourOrder.CompareTo(right.ColourOrder);
        if (colour != 0)
        {
            return colour;
        }

        var name = StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
        return name != 0 ? name : StringComparer.OrdinalIgnoreCase.Compare(left.FullPath, right.FullPath);
    }

    private long NextRevision(string path)
    {
        var revision = Interlocked.Increment(ref _revisionSequence);
        _revisions[path] = revision;
        return revision;
    }

    private bool IsCurrent(string path, long revision)
        => _revisions.TryGetValue(path, out var current) && current == revision;

    private async Task OnDispatcherAsync(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            await _dispatcher.InvokeAsync(action, DispatcherPriority.DataBind, _lifetime.Token).Task;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (TaskCanceledException) when (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
        }
    }

    private void ScheduleKindSave()
    {
        CancellationToken token;
        lock (_taskGate)
        {
            _kindSaveDebounce?.Cancel();
            _kindSaveDebounce?.Dispose();
            _kindSaveDebounce = new CancellationTokenSource();
            token = _kindSaveDebounce.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, token);
                await _marks.SaveAsync(token);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async ValueTask<MarksFileStamp> ReadMarksFileStampAsync(CancellationToken cancellationToken)
        => await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(_marks.StatePath);
                return info.Exists
                    ? new MarksFileStamp(info.Length, info.LastWriteTimeUtc.Ticks)
                    : default;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return default;
            }
        }, cancellationToken);

    /// <summary>
    /// One OS probe per path for the whole process.  The caller stops waiting
    /// after a short budget and releases its own queue slot; an unreachable
    /// share can therefore neither retain a window nor starve later local
    /// tags.  If Windows eventually answers, the next periodic retry reuses
    /// the completed task immediately.
    /// </summary>
    internal static async ValueTask<FolderTagPathKind> ClassifyPathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (IsRemotePath(path))
        {
            // Tags are primarily folder marks.  Unknown marks written by an
            // older version are shown rather than blocking start-up on a UNC
            // call that Windows cannot cancel. New marks carry IsDirectory,
            // so coloured remote files are still excluded.
            return FolderTagPathKind.Unknown;
        }

        var probe = GetOrCreateDirectoryProbe(path);
        if (probe is null)
        {
            return FolderTagPathKind.Unknown;
        }

        try
        {
            return await probe.Result.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return FolderTagPathKind.Unknown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return FolderTagPathKind.Unknown;
        }
    }

    private static DirectoryProbe? GetOrCreateDirectoryProbe(string path)
    {
        lock (DirectoryProbeGate)
        {
            var now = DateTime.UtcNow;
            if (DirectoryProbes.TryGetValue(path, out var existing))
            {
                if (!existing.Result.IsCompleted || now - existing.StartedUtc < ReconcileInterval)
                {
                    return existing;
                }

                DirectoryProbes.Remove(path);
                existing.CancelAndDisposeWhenDone();
            }

            SweepDirectoryProbes(now);
            if (DirectoryProbes.Count >= MaximumCachedDirectoryProbes)
            {
                return null;
            }

            var created = new DirectoryProbe(
                path,
                now,
                UnderlyingLocalProbeSlots);
            DirectoryProbes[path] = created;
            return created;
        }
    }

    private static void ForgetDirectoryProbe(string path)
    {
        DirectoryProbe? probe;
        lock (DirectoryProbeGate)
        {
            if (!DirectoryProbes.Remove(path, out probe))
            {
                return;
            }
        }

        probe.CancelAndDisposeWhenDone();
    }

    private static void SweepDirectoryProbes(DateTime now)
    {
        foreach (var (path, probe) in DirectoryProbes
                     .Where(pair => pair.Value.Result.IsCompleted && now - pair.Value.StartedUtc >= ReconcileInterval)
                     .ToArray())
        {
            DirectoryProbes.Remove(path);
            probe.CancelAndDisposeWhenDone();
        }

        if (DirectoryProbes.Count < MaximumCachedDirectoryProbes)
        {
            return;
        }

        foreach (var (path, probe) in DirectoryProbes
                     .Where(pair => pair.Value.Result.IsCompleted)
                     .OrderBy(pair => pair.Value.StartedUtc)
                     .Take(DirectoryProbes.Count - MaximumCachedDirectoryProbes + 1)
                     .ToArray())
        {
            DirectoryProbes.Remove(path);
            probe.CancelAndDisposeWhenDone();
        }
    }

    private static bool IsRemotePath(string path)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrWhiteSpace(root)
                && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static async Task<FolderTagPathKind> RunDirectoryProbeAsync(
        string path,
        SemaphoreSlim lane,
        CancellationToken cancellationToken)
    {
        await lane.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(() =>
            {
                var attributes = File.GetAttributes(path);
                return attributes.HasFlag(FileAttributes.Directory)
                    ? FolderTagPathKind.Folder
                    : FolderTagPathKind.File;
            }, CancellationToken.None);
        }
        finally
        {
            lane.Release();
        }
    }

    private sealed class DirectoryProbe
    {
        private readonly CancellationTokenSource _cancellation = new();

        public DirectoryProbe(string path, DateTime startedUtc, SemaphoreSlim lane)
        {
            StartedUtc = startedUtc;
            Result = RunDirectoryProbeAsync(path, lane, _cancellation.Token);
        }

        public Task<FolderTagPathKind> Result { get; }
        public DateTime StartedUtc { get; }

        public void CancelAndDisposeWhenDone()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Result.ContinueWith(
                task =>
                {
                    _ = task.Exception;
                    _cancellation.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private readonly record struct MarksFileStamp(long Length, long LastWriteTicks);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_subscribed)
        {
            _marks.MarkChanged -= OnMarkChanged;
            _subscribed = false;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
        CancellationTokenSource? kindSave;
        lock (_taskGate)
        {
            kindSave = _kindSaveDebounce;
            _kindSaveDebounce = null;
        }

        if (kindSave is not null)
        {
            kindSave.Cancel();
            kindSave.Dispose();
            _ = _marks.SaveAsync();
        }
        // A Directory.Exists call cannot be cancelled once Windows is inside
        // it. Leave the tiny semaphore for GC rather than disposing it under
        // a late probe whose finally block still has to Release it.
    }
}
