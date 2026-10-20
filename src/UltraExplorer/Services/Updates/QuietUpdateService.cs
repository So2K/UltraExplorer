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
    private UpdateOperation? _operation;
    private FileSystemWatcher? _settingsWatcher;
    private QuietUpdateSnapshot _snapshot;
    private QuietUpdateStore.Package? _verified;
    private DateTime _verifiedWriteTime;
    private int _started;
    private Task? _runTask;
    private bool _disposed;
    private bool? _unpersistedPreference;
    private QuietUpdateChannel? _unpersistedChannel;
    private readonly QuietUpdateChannel _defaultChannel;
    private QuietUpdateChannel _channel;
    private long _channelGeneration;
    private long _enabledGeneration;
    private bool _isChecking;

    private static readonly TimeSpan AttemptClockSkew = TimeSpan.FromMinutes(5);

    public QuietUpdateService(string? cacheDirectory = null)
        : this(cacheDirectory ?? Path.Combine(AppPaths.StateDirectory, "updates"),
            typeof(QuietUpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "", null, TimeProvider.System) { }

    internal QuietUpdateService(string cacheDirectory, string installedVersion, System.Net.Http.HttpMessageHandler? handler,
        TimeProvider timeProvider, Func<TimeSpan, CancellationToken, Task>? delay = null, long bytesPerSecond = 512 * 1024)
    {
        _store = new QuietUpdateStore(cacheDirectory);
        _http = new QuietUpdateHttp(handler);
        _installed = UpdateVersion.Parse(installedVersion);
        _defaultChannel = QuietUpdateChannels.Default(_installed);
        _channel = _store.Channel ?? _defaultChannel;
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
            lock (_gate) { _unpersistedPreference = value; _enabledGeneration++; }
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
    public string InstalledVersion => _installed?.Value ?? "";
    public bool IsChecking { get { lock (_gate) return _isChecking; } }
    public QuietUpdateChannel Channel
    {
        get { lock (_gate) return _channel; }
        set
        {
            if (!Enum.IsDefined(value)) return;
            UpdateOperation? operation;
            bool changed;
            lock (_gate)
            {
                if (_disposed || _snapshot.State == QuietUpdateState.Installing) return;
                _unpersistedChannel = value;
                changed = ChangeChannelLocked(value, out operation);
            }
            CancelOperation(operation);
            if (changed) Notify();
            try { _store.SetChannel(value); lock (_gate) _unpersistedChannel = null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
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
            // Start is called only after the normal main window is visible:
            // the confirmed-update helper keeps its rollback backup until the
            // restarted version reports that here (docs/QUIET_UPDATES.md).
            RecordStartup();
            // Even re-enabling the setting cannot bypass this first delay.
            await _delay(TimeSpan.FromSeconds(30), _lifetime.Token).ConfigureAwait(false);
            StartSettingsWatcher();
            while (!_lifetime.IsCancellationRequested)
            {
                ApplyEnabled(ReadEnabled());
                ApplyChannel(ReadChannel());
                if (Enabled) await CheckOnceAsync(_lifetime.Token).ConfigureAwait(false);
                await _delay(TimeSpan.FromMinutes(1), _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException) { Publish(QuietUpdateState.Failed); }
    }

    private void RecordStartup()
    {
        if (_installed is null) return;
        try { _store.RecordStartup(new QuietUpdateStore.Startup(_installed.Value, Environment.ProcessId, _time.GetUtcNow())); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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

    private void SettingsChanged(object sender, FileSystemEventArgs args)
    {
        ApplyEnabled(ReadEnabled());
        ApplyChannel(ReadChannel());
    }
    private bool ReadEnabled()
    {
        bool? preference;
        long generation;
        lock (_gate) { preference = _unpersistedPreference; generation = _enabledGeneration; }
        if (preference is { } pending) return pending;
        var saved = _store.Enabled;
        lock (_gate) return generation == _enabledGeneration ? saved : _unpersistedPreference ?? _snapshot.Enabled;
    }
    private QuietUpdateChannel ReadChannel()
    {
        QuietUpdateChannel? preference;
        long generation;
        lock (_gate) { preference = _unpersistedChannel; generation = _channelGeneration; }
        if (preference is { } pending) return pending;
        var saved = _store.Channel ?? _defaultChannel;
        lock (_gate) return generation == _channelGeneration ? saved : _unpersistedChannel ?? _channel;
    }

    private void ApplyChannel(QuietUpdateChannel channel)
    {
        UpdateOperation? operation;
        bool changed;
        lock (_gate)
        {
            changed = ChangeChannelLocked(channel, out operation);
        }
        CancelOperation(operation);
        if (changed) Notify();
    }

    private bool ChangeChannelLocked(QuietUpdateChannel channel, out UpdateOperation? operation)
    {
        operation = null;
        if (_disposed || _channel == channel || _snapshot.State == QuietUpdateState.Installing) return false;
        var narrowing = (int)channel < (int)_channel;
        var pending = _verified ?? _store.Ready;
        _channel = channel;
        _channelGeneration++;
        operation = _operation;
        if (pending is not null && (!ValidPackage(pending, channel) || narrowing && pending.MinimumChannel is null))
        {
            // Older ready metadata did not retain GitHub's prerelease flag.
            // On an explicit narrowing choice, recheck that legacy package
            // through the new catalog policy instead of guessing its channel.
            _verified = null;
            _store.ClearReady();
        }
        _snapshot = new QuietUpdateSnapshot(_snapshot.Enabled, QuietUpdateState.Idle, 0, "", "");
        return true;
    }

    private static void CancelOperation(UpdateOperation? operation) => operation?.Cancel();

    private void ApplyEnabled(bool enabled)
    {
        UpdateOperation? operation = null;
        var changed = false;
        lock (_gate)
        {
            if (_disposed || _snapshot.Enabled == enabled) return;
            _enabledGeneration++;
            operation = !enabled ? _operation : null;
            _snapshot = _snapshot.State == QuietUpdateState.Installing
                ? _snapshot with { Enabled = enabled }
                : new QuietUpdateSnapshot(enabled, QuietUpdateState.Idle, 0, "", "");
            changed = true;
        }
        CancelOperation(operation);
        if (changed) Notify();
    }

    internal async Task CheckOnceAsync(CancellationToken token = default)
    {
        await CheckCoreAsync(manual: false, token).ConfigureAwait(false);
    }

    /// <summary>
    /// An explicit settings action bypasses startup/daily timing. When automatic
    /// updates are off it checks metadata only, without enabling or downloading.
    /// This method never opens an offer or starts an installer.
    /// </summary>
    public Task<QuietUpdateCheckResult> CheckNowAsync(CancellationToken token = default)
        => Task.Run(() => CheckCoreAsync(manual: true, token));

    private async Task<QuietUpdateCheckResult> CheckCoreAsync(bool manual, CancellationToken token)
    {
        if (token.IsCancellationRequested) return new(QuietUpdateCheckStatus.Cancelled);
        ApplyEnabled(ReadEnabled());
        ApplyChannel(ReadChannel());
        QuietUpdateChannel channel;
        long generation;
        bool download;
        using var operation = new UpdateOperation(token, _lifetime.Token);
        lock (_gate)
        {
            if (_disposed) return new(QuietUpdateCheckStatus.Cancelled);
            if (_isChecking || _snapshot.State == QuietUpdateState.Installing) return new(QuietUpdateCheckStatus.Busy);
            if (_installed is null) return new(QuietUpdateCheckStatus.Failed);
            if (!manual && !_snapshot.Enabled) return new(QuietUpdateCheckStatus.Cancelled);
            _isChecking = true;
            _operation = operation;
            channel = _channel;
            generation = _channelGeneration;
            download = _snapshot.Enabled;
        }
        Notify();
        try
        {
            using var lease = _store.TryLease();
            if (lease is null) return new(QuietUpdateCheckStatus.Busy);
            _store.PruneOrphanedStages();
            if (download) await LoadReadyAsync(operation.Token, generation).ConfigureAwait(false);
            AssertCurrentCheck(generation, operation.Token);
            var now = _time.GetUtcNow();
            var last = _store.LastAttemptUtc;
            // A timestamp ahead of the clock (the clock was set back, a profile
            // copied from another machine) must not stop checks until that date.
            if (!manual && last <= now + AttemptClockSkew && now - last < TimeSpan.FromDays(1))
                return CurrentResult();
            // Persist the attempt before HTTP, also on failure/cancellation.
            // A second process/restart/toggle therefore cannot spam GitHub.
            _store.RecordAttempt(now);
            var release = await _http.FindAsync(_installed, channel, operation.Token).ConfigureAwait(false);
            AssertCurrentCheck(generation, operation.Token);
            if (release is null) return CurrentResult();
            if (!download) return new(QuietUpdateCheckStatus.Available, release.Version.Value);
            if (_verified is { } ready && ValidPackage(ready, channel) && UpdateVersion.Parse(ready.Version)?.CompareTo(release.Version) >= 0)
                return CurrentResult();
            var sha = await _http.ExpectedHashAsync(release, operation.Token).ConfigureAwait(false);
            AssertCurrentCheck(generation, operation.Token);
            var package = new QuietUpdateStore.Package(release.Version.Value, sha, release.Size, release.MinimumChannel);
            _store.PrunePartialPackages(sha);
            var partialPath = _store.PackagePath(sha, partial: true);
            var finalPath = _store.PackagePath(sha);
            if (!await VerifyAsync(finalPath, package, operation.Token).ConfigureAwait(false))
            {
                await DownloadAsync(release, partialPath, operation.Token, generation).ConfigureAwait(false);
                if (!await VerifyAsync(partialPath, package, operation.Token).ConfigureAwait(false))
                {
                    File.Delete(partialPath);
                    throw new InvalidDataException("Update archive checksum did not match.");
                }
                AssertCurrentCheck(generation, operation.Token);
                File.Move(partialPath, finalPath, overwrite: true);
            }
            AssertCurrentCheck(generation, operation.Token);
            lock (_gate)
            {
                AssertCurrentCheck(generation, operation.Token);
                if (!_snapshot.Enabled) throw new OperationCanceledException(operation.Token);
                _store.SetReady(package);
                RememberVerified(package, finalPath);
                _snapshot = _snapshot with { State = QuietUpdateState.Ready, Progress = 1,
                    ReadyVersion = package.Version, PreparedPackage = finalPath };
            }
            Notify();
            _store.PrunePackages(sha);
            return new(QuietUpdateCheckStatus.Ready, package.Version);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested || !CurrentGeneration(generation) || download && !Enabled)
        {
            if (CurrentGeneration(generation) && Enabled && !_lifetime.IsCancellationRequested && State != QuietUpdateState.Installing) RestoreReady();
            return new(QuietUpdateCheckStatus.Cancelled);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        {
            if (CurrentGeneration(generation) && State != QuietUpdateState.Installing)
            { if (_verified is not null) RestoreReady(); else Publish(QuietUpdateState.Failed); }
            return new(QuietUpdateCheckStatus.Failed);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_operation, operation)) _operation = null;
                _isChecking = false;
            }
            Notify();
        }
    }

    private bool CurrentGeneration(long generation) { lock (_gate) return !_disposed && generation == _channelGeneration; }
    private void AssertCurrentCheck(long generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
            if (_disposed || generation != _channelGeneration) throw new OperationCanceledException(token);
    }
    private QuietUpdateCheckResult CurrentResult() => HasUpdate
        ? new(QuietUpdateCheckStatus.Ready, ReadyVersion) : new(QuietUpdateCheckStatus.UpToDate);

    private async Task LoadReadyAsync(CancellationToken token, long generation)
    {
        var package = _store.Ready;
        if (package is null) return;
        if (!ValidPackage(package))
        {
            // The offered version is now installed (or older): its package
            // (~150 MB) and offer are spent, not kept until the next release.
            if (UpdateVersion.Parse(package.Version) is { } version && _installed is not null && version.CompareTo(_installed) <= 0)
            { _verified = null; _store.ClearReady(); _store.PrunePackages(""); }
            return;
        }
        var path = _store.PackagePath(package.Sha256);
        if (_verified == package && File.Exists(path) && File.GetLastWriteTimeUtc(path) == _verifiedWriteTime)
        { if (State != QuietUpdateState.Installing) PublishReady(package, path, generation); return; }
        if (await VerifyAsync(path, package, token).ConfigureAwait(false))
        {
            lock (_gate)
            {
                AssertCurrentCheck(generation, token);
                RememberVerified(package, path);
            }
            PublishReady(package, path, generation);
        }
        else
        {
            AssertCurrentCheck(generation, token);
            _verified = null; _store.ClearReady(); Publish(QuietUpdateState.Idle, generation: generation);
        }
    }

    private bool ValidPackage(QuietUpdateStore.Package package) => ValidPackage(package, Channel);
    private bool ValidPackage(QuietUpdateStore.Package package, QuietUpdateChannel channel)
    {
        var version = UpdateVersion.Parse(package.Version);
        return version is not null && _installed is not null && version.CompareTo(_installed) > 0
            && QuietUpdateChannels.Allows(version, channel)
            && (package.MinimumChannel is null || Enum.IsDefined(package.MinimumChannel.Value) && (int)channel >= (int)package.MinimumChannel.Value)
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

    private async Task DownloadAsync(QuietUpdateHttp.Release release, string path, CancellationToken token, long generation)
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
                Publish(QuietUpdateState.Downloading, (double)offset / release.Size, generation: generation);
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
                    Publish(QuietUpdateState.Downloading, (double)output.Position / release.Size, generation: generation);
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
        QuietUpdateStore.Package? package;
        long generation;
        lock (_gate) { package = _verified; generation = _channelGeneration; }
        if (package is null || !ValidPackage(package)) return null;
        var path = _store.PackagePath(package.Sha256);
        if (!await VerifyAsync(path, package, token).ConfigureAwait(false))
        {
            lock (_gate)
            {
                if (_disposed || generation != _channelGeneration || _verified != package) return null;
                _verified = null;
                _store.ClearReady();
            }
            Publish(QuietUpdateState.Failed, generation: generation);
            return null;
        }
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed || generation != _channelGeneration || !_snapshot.Enabled || _snapshot.State != QuietUpdateState.Ready
                || _verified != package || !ValidPackage(package, _channel)) return null;
            return new PreparedUpdatePackage(package.Version, path, package.Sha256, package.Size) { ChannelGeneration = generation };
        }
    }

    public bool MarkInstalling(PreparedUpdatePackage? prepared = null)
    {
        UpdateOperation? operation;
        lock (_gate)
        {
            if (_disposed || !_snapshot.HasUpdate || _verified is not { } package || !ValidPackage(package, _channel)) return false;
            if (prepared is not null && (prepared.ChannelGeneration != _channelGeneration || prepared.Version != package.Version
                || prepared.Sha256 != package.Sha256 || prepared.Size != package.Size
                || prepared.ZipPath != _store.PackagePath(package.Sha256))) return false;
            _snapshot = _snapshot with { State = QuietUpdateState.Installing };
            operation = _operation;
        }
        CancelOperation(operation);
        Notify();
        return true;
    }

    public void RestoreReady()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var snapshot = _snapshot.Enabled && _verified is { } package && ValidPackage(package, _channel)
                ? new QuietUpdateSnapshot(true, QuietUpdateState.Ready, 1, package.Version, _store.PackagePath(package.Sha256))
                : new QuietUpdateSnapshot(_snapshot.Enabled, QuietUpdateState.Idle, 0, "", "");
            if (_snapshot == snapshot) return;
            _snapshot = snapshot;
        }
        Notify();
    }

    private void RememberVerified(QuietUpdateStore.Package package, string path)
    { _verified = package; _verifiedWriteTime = File.GetLastWriteTimeUtc(path); }

    private void PublishReady(QuietUpdateStore.Package package, string path, long? generation = null) => Publish(QuietUpdateState.Ready, 1, package.Version, path, generation: generation);
    private void Publish(QuietUpdateState state, double progress = 0, string version = "", string path = "", bool exitInstalling = false, long? generation = null)
    {
        lock (_gate)
        {
            if (_disposed || !_snapshot.Enabled) return;
            if (generation is { } expected && expected != _channelGeneration) return;
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

    internal sealed class UpdateOperation : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _source = new();
        private readonly CancellationTokenRegistration _caller, _lifetime;
        private Task? _callbacks;
        private bool _disposed;
        public CancellationToken Token { get; }
        public bool IsCancellationRequested => Token.IsCancellationRequested;
        public UpdateOperation(CancellationToken caller, CancellationToken lifetime)
        {
            Token = _source.Token;
            // User/expiry cancellation must signal immediately but never run
            // a slow provider's callbacks on a settings/input thread.
            _caller = caller.Register(Cancel);
            _lifetime = lifetime.Register(Cancel);
        }
        public void Cancel()
        {
            lock (_gate)
                if (!_disposed) _callbacks ??= _source.CancelAsync();
        }
        public void Dispose()
        {
            Task? callbacks;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                callbacks = _callbacks;
            }
            _caller.Unregister();
            _lifetime.Unregister();
            // Dispose must not unregister callbacks which CancelAsync has
            // queued but has not executed yet. The check itself never waits
            // for those callbacks or for an unresponsive network provider.
            if (callbacks is { IsCompleted: false })
                _ = callbacks.ContinueWith(_ => _source.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else _source.Dispose();
        }
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
