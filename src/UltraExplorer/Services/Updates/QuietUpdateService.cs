using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using UltraExplorer.Infrastructure;

namespace UltraExplorer.Services.Updates;

/// <summary>
/// A process-local download scheduler. It never launches an installer, shows a
/// window, adds a startup entry, or registers work outside the running app.
/// </summary>
public sealed class QuietUpdateService : IDisposable
{
    private static readonly Lazy<QuietUpdateService> Instance = new(() => new QuietUpdateService());
    public static QuietUpdateService Shared => Instance.Value;
    private readonly QuietUpdateStore _store;
    private readonly QuietUpdateHttp _http;
    private readonly UpdateVersion? _installed;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly long _bytesPerSecond;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _operation;
    private FileSystemWatcher? _settingsWatcher;
    private QuietUpdateSnapshot _snapshot;
    private QuietUpdateStore.Package? _verified;
    private DateTime _verifiedWriteTime;
    private int _started;
    private Task? _runTask;
    private bool _disposed;
    private bool? _unpersistedPreference;

    public QuietUpdateService(string? cacheDirectory = null)
        : this(cacheDirectory ?? Path.Combine(AppPaths.StateDirectory, "updates"),
            typeof(QuietUpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "", null, TimeProvider.System) { }

    internal QuietUpdateService(string cacheDirectory, string installedVersion, System.Net.Http.HttpMessageHandler? handler,
        TimeProvider timeProvider, Func<TimeSpan, CancellationToken, Task>? delay = null, long bytesPerSecond = 512 * 1024)
    {
        _store = new QuietUpdateStore(cacheDirectory);
        _http = new QuietUpdateHttp(handler);
        _installed = UpdateVersion.Parse(installedVersion);
        _time = timeProvider;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, _time, token));
        _bytesPerSecond = Math.Max(1, bytesPerSecond);
        _snapshot = new QuietUpdateSnapshot(_store.Enabled, QuietUpdateState.Idle, 0, "", "");
    }

    public event EventHandler? SnapshotChanged;
    public QuietUpdateSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public bool Enabled
    {
        get => Snapshot.Enabled;
        set
        {
            // Cancelling locally must not depend on a writable profile.
            lock (_gate) _unpersistedPreference = value;
            ApplyEnabled(value);
            try { _store.SetEnabled(value); lock (_gate) _unpersistedPreference = null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    public QuietUpdateState State => Snapshot.State;
    public double Progress => Snapshot.Progress;
    public string ReadyVersion => Snapshot.ReadyVersion;
    public string PreparedPackage => Snapshot.PreparedPackage;
    public bool HasUpdate => Snapshot.HasUpdate;
    internal CancellationToken LifetimeToken => _lifetime.Token;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        lock (_gate) { if (_disposed) return; _runTask = Task.Run(RunAsync); }
    }

    private async Task RunAsync()
    {
        try
        {
            // Start is called only after the normal main window is visible.
            // Even re-enabling the setting cannot bypass this first delay.
            await _delay(TimeSpan.FromSeconds(30), _lifetime.Token).ConfigureAwait(false);
            StartSettingsWatcher();
            while (!_lifetime.IsCancellationRequested)
            {
                ApplyEnabled(ReadEnabled());
                if (Enabled) await CheckOnceAsync(_lifetime.Token).ConfigureAwait(false);
                await _delay(TimeSpan.FromMinutes(1), _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException) { Publish(QuietUpdateState.Failed); }
    }

    private void StartSettingsWatcher()
    {
        try
        {
            QuietUpdateStore.AssertNoLinks(_store.DirectoryPath);
            Directory.CreateDirectory(_store.DirectoryPath);
            var watcher = new FileSystemWatcher(_store.DirectoryPath, "settings.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            watcher.Changed += SettingsChanged;
            watcher.Created += SettingsChanged;
            watcher.Renamed += SettingsChanged;
            lock (_gate)
            {
                if (_disposed) { watcher.Dispose(); return; }
                _settingsWatcher = watcher;
                watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private void SettingsChanged(object sender, FileSystemEventArgs args) => ApplyEnabled(ReadEnabled());
    private bool ReadEnabled() { lock (_gate) return _unpersistedPreference ?? _store.Enabled; }

    private void ApplyEnabled(bool enabled)
    {
        CancellationTokenSource? operation = null;
        var changed = false;
        lock (_gate)
        {
            if (_disposed || _snapshot.Enabled == enabled) return;
            operation = !enabled ? _operation : null;
            _snapshot = new QuietUpdateSnapshot(enabled, QuietUpdateState.Idle, 0, "", "");
            changed = true;
        }
        try { operation?.Cancel(); } catch (ObjectDisposedException) { }
        if (changed) Notify();
    }

    internal async Task CheckOnceAsync(CancellationToken token = default)
    {
        if (!Enabled || _installed is null || State == QuietUpdateState.Installing) return;
        using var lease = _store.TryLease();
        if (lease is null) return;
        _store.PruneOrphanedStages();
        ApplyEnabled(ReadEnabled());
        if (!Enabled) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        lock (_gate)
        {
            if (_disposed || !_snapshot.Enabled) return;
            _operation = operation;
        }
        try
        {
            await LoadReadyAsync(operation.Token).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            if (now - _store.LastAttemptUtc < TimeSpan.FromDays(1)) return;
            // Persist the attempt before HTTP, also on failure/cancellation.
            // A second process/restart/toggle therefore cannot spam GitHub.
            _store.RecordAttempt(now);
            var release = await _http.FindAsync(_installed, operation.Token).ConfigureAwait(false);
            if (release is null) return;
            if (_verified is { } ready && UpdateVersion.Parse(ready.Version)?.CompareTo(release.Version) >= 0) return;
            var sha = await _http.ExpectedHashAsync(release, operation.Token).ConfigureAwait(false);
            var package = new QuietUpdateStore.Package(release.Version.Value, sha, release.Size);
            _store.PrunePartialPackages(sha);
            var partialPath = _store.PackagePath(sha, partial: true);
            var finalPath = _store.PackagePath(sha);
            if (!await VerifyAsync(finalPath, package, operation.Token).ConfigureAwait(false))
            {
                await DownloadAsync(release, partialPath, operation.Token).ConfigureAwait(false);
                if (!await VerifyAsync(partialPath, package, operation.Token).ConfigureAwait(false))
                {
                    File.Delete(partialPath);
                    throw new InvalidDataException("Update archive checksum did not match.");
                }
                operation.Token.ThrowIfCancellationRequested();
                File.Move(partialPath, finalPath, overwrite: true);
            }
            operation.Token.ThrowIfCancellationRequested();
            _store.SetReady(package);
            RememberVerified(package, finalPath);
            PublishReady(package, finalPath);
            _store.PrunePackages(sha);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (Enabled && !_lifetime.IsCancellationRequested && State != QuietUpdateState.Installing) RestoreReady();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        {
            if (State != QuietUpdateState.Installing)
            { if (_verified is not null) RestoreReady(); else Publish(QuietUpdateState.Failed); }
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_operation, operation)) _operation = null; }
        }
    }

    private async Task LoadReadyAsync(CancellationToken token)
    {
        var package = _store.Ready;
        if (package is null || !ValidPackage(package)) return;
        var path = _store.PackagePath(package.Sha256);
        if (_verified == package && File.Exists(path) && File.GetLastWriteTimeUtc(path) == _verifiedWriteTime)
        { if (State != QuietUpdateState.Installing) PublishReady(package, path); return; }
        if (await VerifyAsync(path, package, token).ConfigureAwait(false))
        {
            RememberVerified(package, path);
            PublishReady(package, path);
        }
        else { _verified = null; _store.ClearReady(); Publish(QuietUpdateState.Idle); }
    }

    private bool ValidPackage(QuietUpdateStore.Package package)
    {
        var version = UpdateVersion.Parse(package.Version);
        return version is not null && _installed is not null && version.CompareTo(_installed) > 0
            && (_installed.IsPrerelease || !version.IsPrerelease)
            && QuietUpdateHttp.IsHash(package.Sha256) && package.Sha256 == package.Sha256.ToLowerInvariant()
            && package.Size > 0 && package.Size < QuietUpdateHttp.MaximumPackageSize;
    }

    private static async Task<bool> VerifyAsync(string path, QuietUpdateStore.Package package, CancellationToken token)
    {
        QuietUpdateStore.AssertNoLinks(path);
        if (!File.Exists(path)) return false;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length != package.Size) return false;
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(package.Sha256));
    }

    private async Task DownloadAsync(QuietUpdateHttp.Release release, string path, CancellationToken token)
    {
        QuietUpdateStore.AssertNoLinks(path);
        FileStream? prefix = null;
        string? staging = null;
        var prefixComplete = false;
        var transferred = 0L;
        try
        {
            // Open the saved prefix read-only. The length belongs to this handle,
            // and its bytes cannot change while copied. Never write/truncate an
            // existing cache file: even an NTFS hard link must remain untouched.
            if (File.Exists(path)) prefix = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var offset = prefix?.Length ?? 0;
            if (offset >= release.Size) offset = 0;
            System.Net.Http.HttpResponseMessage received;
            try { received = await _http.DownloadAsync(release.ZipUrl, offset, token).ConfigureAwait(false); }
            catch (System.Net.Http.HttpRequestException ex) when (offset > 0 && ex.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                offset = 0;
                received = await _http.DownloadAsync(release.ZipUrl, 0, token).ConfigureAwait(false);
            }
            using var response = received;
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (offset == 0 || range?.Unit != "bytes" || range.From != offset || range.To != release.Size - 1 || range.Length != release.Size)
                    throw new InvalidDataException("Invalid resumed update range.");
            }
            else if (response.StatusCode == HttpStatusCode.OK) offset = 0;
            else throw new InvalidDataException("Invalid archive response.");
            if (response.Content.Headers.ContentEncoding.Count != 0 || (response.Content.Headers.ContentLength is { } length && length != release.Size - offset))
                throw new InvalidDataException("Update archive size did not match.");
            staging = Path.Combine(_store.DirectoryPath, $".download-{Guid.NewGuid():N}.partial");
            QuietUpdateStore.AssertNoLinks(staging);
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[65_536];
                var remaining = offset;
                while (remaining > 0)
                {
                    var count = await prefix!.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                    if (count == 0) throw new InvalidDataException("Saved update prefix changed.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    remaining -= count;
                }
                prefix?.Dispose(); prefix = null;
                prefixComplete = true;
                using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var started = _time.GetTimestamp();
                Publish(QuietUpdateState.Downloading, (double)offset / release.Size);
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (!ReadEnabled()) { ApplyEnabled(false); token.ThrowIfCancellationRequested(); throw new OperationCanceledException(token); }
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    if (output.Position + count > release.Size) throw new InvalidDataException("Update archive exceeded its size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    transferred += count;
                    Publish(QuietUpdateState.Downloading, (double)output.Position / release.Size);
                    var allowed = TimeSpan.FromSeconds((double)transferred / _bytesPerSecond);
                    var elapsed = _time.GetElapsedTime(started);
                    if (allowed > elapsed) await _delay(allowed - elapsed, token).ConfigureAwait(false);
                }
                if (output.Length != release.Size) throw new InvalidDataException("Update archive was truncated.");
                await output.FlushAsync(token).ConfigureAwait(false);
            }
        }
        finally
        {
            prefix?.Dispose();
            if (staging is not null)
            {
                try
                {
                    // Cancellation while copying the saved prefix preserves the
                    // original. After network bytes arrive, save the full prefix
                    // atomically only after every stream has been closed.
                    if (prefixComplete && transferred > 0)
                    {
                        QuietUpdateStore.AssertNoLinks(path);
                        File.Move(staging, path, overwrite: true);
                    }
                }
                finally { File.Delete(staging); }
            }
        }
    }

    public async Task<PreparedUpdatePackage?> PrepareInstallAsync(CancellationToken token = default)
    {
        if (!HasUpdate) return null;
        var package = _verified;
        if (package is null || !ValidPackage(package)) return null;
        var path = _store.PackagePath(package.Sha256);
        if (!await VerifyAsync(path, package, token).ConfigureAwait(false))
        { _verified = null; _store.ClearReady(); Publish(QuietUpdateState.Failed); return null; }
        token.ThrowIfCancellationRequested();
        if (!Enabled || State != QuietUpdateState.Ready) return null;
        return new PreparedUpdatePackage(package.Version, path, package.Sha256, package.Size);
    }

    public bool MarkInstalling()
    {
        CancellationTokenSource? operation;
        lock (_gate)
        {
            if (_disposed || !_snapshot.HasUpdate) return false;
            _snapshot = _snapshot with { State = QuietUpdateState.Installing };
            operation = _operation;
        }
        try { operation?.Cancel(); } catch (ObjectDisposedException) { }
        Notify();
        return true;
    }

    public void RestoreReady()
    {
        if (_verified is { } package && Enabled) Publish(QuietUpdateState.Ready, 1, package.Version, _store.PackagePath(package.Sha256), exitInstalling: true);
        else Publish(QuietUpdateState.Idle, exitInstalling: true);
    }

    private void RememberVerified(QuietUpdateStore.Package package, string path)
    { _verified = package; _verifiedWriteTime = File.GetLastWriteTimeUtc(path); }

    private void PublishReady(QuietUpdateStore.Package package, string path) => Publish(QuietUpdateState.Ready, 1, package.Version, path);
    private void Publish(QuietUpdateState state, double progress = 0, string version = "", string path = "", bool exitInstalling = false)
    {
        lock (_gate)
        {
            if (_disposed || !_snapshot.Enabled) return;
            if (_snapshot.State == QuietUpdateState.Installing && !exitInstalling) return;
            var snapshot = new QuietUpdateSnapshot(true, state, Math.Clamp(progress, 0, 1), version, path);
            if (_snapshot == snapshot) return;
            _snapshot = snapshot;
        }
        Notify();
    }

    private void Notify()
    {
        // UI observers must dispatch their own updates. An observer cannot
        // break downloading or cause retries by throwing into the scheduler.
        foreach (var callback in SnapshotChanged?.GetInvocationList() ?? [])
            try { ((EventHandler)callback)(this, EventArgs.Empty); } catch (Exception) { }
    }

    public void Dispose()
    {
        Task? running;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _settingsWatcher?.Dispose();
            _settingsWatcher = null;
            running = _runTask;
        }
        _lifetime.Cancel();
        _http.Dispose();
        // Only App exit disposes the shared scheduler. Give its atomic partial
        // save a small bounded opportunity to finish; startup and Off never wait.
        if (running is not null && running.Id != Task.CurrentId)
            try { running.Wait(TimeSpan.FromMilliseconds(150)); } catch (AggregateException) { }
    }
}
