using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using UltraExplorer.Services.Updates;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task QuietUpdateChecks()
    {
        Section("quiet GitHub updates");
        await QuietVersionChecks();
        await QuietTimingAndPreferenceChecks();
        await QuietDownloadChecks();
        await QuietResumeChecks();
        await QuietTrustChecks();
        await QuietCacheOwnershipChecks();
    }

    private static Task QuietVersionChecks()
    {
        var invalid = new[] { "1.2", "1.2.3.4", "01.2.3", "1.2.3-beta.01", "1.2.3-", "1.2.3+", " 1.2.3", "1.2.3\n", "v1.2.3" };
        foreach (var value in invalid) Check($"strict SemVer rejects {value.Replace('\n', ' ')}", UpdateVersion.Parse(value) is null);
        var order = new[] { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0" };
        Check("SemVer precedence follows the official prerelease ordering", order.Zip(order.Skip(1)).All(pair => UpdateVersion.Parse(pair.First)!.CompareTo(UpdateVersion.Parse(pair.Second)) < 0));
        Check("build metadata does not create an update", UpdateVersion.Parse("1.2.3+old")!.CompareTo(UpdateVersion.Parse("1.2.3+new")) == 0);
        Check("GitHub v-prefixed tags are parsed explicitly", UpdateVersion.Parse("v1.2.3-beta.2", tag: true)?.Value == "1.2.3-beta.2");
        return Task.CompletedTask;
    }

    private static async Task QuietTimingAndPreferenceChecks()
    {
        using var fixture = new QuietFixture();
        var clock = new QuietClock();
        var firstDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var durations = new List<TimeSpan>();
        Task Delay(TimeSpan duration, CancellationToken token)
        {
            lock (durations) durations.Add(duration);
            if (duration == TimeSpan.FromSeconds(30)) { firstDelay.TrySetResult(); return releaseDelay.Task.WaitAsync(token); }
            return Task.Delay(Timeout.Infinite, token);
        }
        var handler = new QuietHandler(_ => QuietJson("[]"));
        using var service = new QuietUpdateService(fixture.Path, "1.3.0-beta.1", handler, clock, Delay);
        service.Start(); service.Start();
        await firstDelay.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check("normal startup schedules exactly one 30-second first delay", durations.Count == 1 && durations[0] == TimeSpan.FromSeconds(30));
        Check("startup sends no request and creates no download/install work", handler.Requests.Count == 0 && !Directory.Exists(fixture.Path) && service.State == QuietUpdateState.Idle);
        clock.Advance(TimeSpan.FromSeconds(30)); releaseDelay.SetResult();
        await QuietWait(() => handler.Requests.Count == 1);
        Check("first network request is made only after the initial delay", handler.Requests.Count == 1);
        await QuietWait(() => durations.Count == 2);
        await service.CheckOnceAsync();
        Check("a second check on the same UTC day performs no network request", handler.Requests.Count == 1);
        using (var reopened = new QuietUpdateService(fixture.Path, "1.3.0-beta.1", handler: new QuietHandler(_ => throw new InvalidOperationException("Unexpected HTTP")), clock))
            await reopened.CheckOnceAsync();
        Check("last check survives an application restart", new QuietUpdateStore(fixture.Path).LastAttemptUtc == clock.GetUtcNow());
        clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromTicks(1)); await service.CheckOnceAsync();
        Check("23:59:59.999 does not meet the one-day interval", handler.Requests.Count == 1);
        clock.Advance(TimeSpan.FromTicks(1)); await service.CheckOnceAsync();
        Check("24 elapsed hours permit one new request", handler.Requests.Count == 2);
        service.Enabled = false;
        Check("turning updates off immediately hides every status", !service.Enabled && service.State == QuietUpdateState.Idle && !service.HasUpdate && service.Progress == 0);
        clock.Advance(TimeSpan.FromDays(3)); await service.CheckOnceAsync();
        Check("updates off prevents checks even after several days", handler.Requests.Count == 2);
        Check("off is saved independently of workspace preferences", !new QuietUpdateStore(fixture.Path).Enabled);

        using var readOnly = new QuietFixture();
        Directory.CreateDirectory(readOnly.Path);
        File.WriteAllText(System.IO.Path.Combine(readOnly.Path, "settings.json"), "{\"Enabled\":true}");
        File.SetAttributes(System.IO.Path.Combine(readOnly.Path, "settings.json"), FileAttributes.ReadOnly);
        var readOnlyHandler = new QuietHandler(_ => QuietJson("[]"));
        using (var readOnlyService = new QuietUpdateService(readOnly.Path, "1.3.0", readOnlyHandler, clock))
        {
            readOnlyService.Enabled = false;
            await readOnlyService.CheckOnceAsync();
            Check("failed preference writes keep off effective in this process", !readOnlyService.Enabled && readOnlyHandler.Requests.Count == 0);
        }
        File.SetAttributes(System.IO.Path.Combine(readOnly.Path, "settings.json"), FileAttributes.Normal);
        File.WriteAllText(System.IO.Path.Combine(readOnly.Path, "settings.json"), "broken");
        Check("damaged existing opt-out settings never default silently to on", !new QuietUpdateStore(readOnly.Path).Enabled);
        foreach (var malformedSetting in new[] { "{}", "[]", "{\"Enabled\":null}", "{\"Enabled\":1}", "{\"Enabled\":false,\"Enabled\":true}" })
        {
            File.WriteAllText(System.IO.Path.Combine(readOnly.Path, "settings.json"), malformedSetting);
            Check("existing missing, nonboolean or duplicate update preference fails closed", !new QuietUpdateStore(readOnly.Path).Enabled);
        }

        using var failed = new QuietFixture();
        var failedHandler = new QuietHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var failedService = new QuietUpdateService(failed.Path, "1.3.0", failedHandler, clock);
        await failedService.CheckOnceAsync(); failedService.Enabled = false; failedService.Enabled = true; await failedService.CheckOnceAsync();
        Check("HTTP failure plus preference toggles cannot spam retries", failedHandler.Requests.Count == 1 && new QuietUpdateStore(failed.Path).LastAttemptUtc == clock.GetUtcNow());

        using var shared = new QuietFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalledHandler = new QuietHandler(async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return QuietJson("[]"); });
        using var one = new QuietUpdateService(shared.Path, "1.3.0", stalledHandler, clock);
        var task = one.CheckOnceAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondHandler = new QuietHandler(_ => QuietJson("[]"));
        using var two = new QuietUpdateService(shared.Path, "1.3.0", secondHandler, clock);
        await two.CheckOnceAsync();
        Check("another app instance cannot enter the active network lease", stalledHandler.Requests.Count == 1 && secondHandler.Requests.Count == 0);
        one.Enabled = false; await task.WaitAsync(TimeSpan.FromSeconds(5));
        Check("off cancels an in-flight HTTP request immediately", !one.Enabled && task.IsCompletedSuccessfully);

        using var installingFixture = new QuietFixture();
        Directory.CreateDirectory(installingFixture.Path);
        var installData = QuietZip();
        var installStore = new QuietUpdateStore(installingFixture.Path);
        var installHash = QuietHash(installData);
        File.WriteAllBytes(installStore.PackagePath(installHash), installData);
        installStore.SetReady(new QuietUpdateStore.Package("1.3.1", installHash, installData.Length));
        var installEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var installHandler = new QuietHandler(async (_, token) => { installEntered.SetResult(); await Task.Delay(Timeout.Infinite, token); return QuietJson("[]"); });
        using var installing = new QuietUpdateService(installingFixture.Path, "1.3.0", installHandler, clock);
        var installCheck = installing.CheckOnceAsync(); await installEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check("explicit install can use an already verified package during a later daily check", installing.MarkInstalling());
        await installCheck.WaitAsync(TimeSpan.FromSeconds(5));
        Check("explicit installation cancels daily work without worker restoring ready", installing.State == QuietUpdateState.Installing);
    }

    private static async Task QuietDownloadChecks()
    {
        using var fixture = new QuietFixture();
        var clock = new QuietClock();
        var data = QuietZip();
        var hash = QuietHash(data);
        var handler = QuietReleaseHandler(data, "v1.3.0-beta.2");
        var delayed = TimeSpan.Zero;
        Task Delay(TimeSpan duration, CancellationToken token) { token.ThrowIfCancellationRequested(); delayed += duration; clock.Advance(duration); return Task.CompletedTask; }
        using var service = new QuietUpdateService(fixture.Path, "1.3.0-beta.1", handler, clock, Delay);
        await service.CheckOnceAsync();
        Check("a newer beta is eligible for an installed beta", service.HasUpdate && service.ReadyVersion == "1.3.0-beta.2");
        Check("ready is advertised only after archive bytes and SHA-256 match", service.Progress == 1 && File.ReadAllBytes(service.PreparedPackage).SequenceEqual(data));
        Check("the download is throttled at 512 KiB per second", delayed >= TimeSpan.FromSeconds((double)data.Length / (512 * 1024)));
        var package = await service.PrepareInstallAsync();
        Check("preparing installation only returns a verified typed package", package is not null && package.Sha256 == hash && package.Size == data.Length && package.ZipPath == service.PreparedPackage && service.State == QuietUpdateState.Ready);
        Check("installing is entered only by an explicit call", service.MarkInstalling() && service.State == QuietUpdateState.Installing);
        service.RestoreReady();
        Check("cancelled explicit installation can return to the ready indicator", service.State == QuietUpdateState.Ready);
        var noNetwork = new QuietHandler(_ => throw new InvalidOperationException("Unexpected HTTP"));
        using var restarted = new QuietUpdateService(fixture.Path, "1.3.0-beta.1", noNetwork, clock);
        await restarted.CheckOnceAsync();
        Check("cached packages survive reopening and are rehashed locally", restarted.HasUpdate && noNetwork.Requests.Count == 0);
        File.WriteAllBytes(restarted.PreparedPackage, Enumerable.Repeat((byte)0, data.Length).ToArray());
        Check("tampering after ready blocks installation", await restarted.PrepareInstallAsync() is null && !restarted.HasUpdate);

        using var stableFixture = new QuietFixture();
        var stableHandler = QuietReleaseHandler(data, "v1.4.0-beta.1");
        using var stable = new QuietUpdateService(stableFixture.Path, "1.3.0", stableHandler, clock);
        await stable.CheckOnceAsync();
        Check("stable users never receive a prerelease", !stable.HasUpdate && stableHandler.Requests.Count == 1);
        using var oldFixture = new QuietFixture();
        var oldHandler = QuietReleaseHandler(data, "v1.2.9");
        using var old = new QuietUpdateService(oldFixture.Path, "1.3.0", oldHandler, clock);
        await old.CheckOnceAsync();
        Check("older releases cannot downgrade the installed version", !old.HasUpdate && oldHandler.Requests.Count == 1);

        using var hashFixture = new QuietFixture();
        var hashHandler = QuietReleaseHandler(data, "v1.3.1", sumsHash: new string('0', 64));
        using var badHash = new QuietUpdateService(hashFixture.Path, "1.3.0", hashHandler, clock);
        await badHash.CheckOnceAsync();
        Check("a GitHub digest disagreeing with SHA256SUMS prevents download", !badHash.HasUpdate && hashHandler.Requests.Count == 2);

        using var cacheFixture = new QuietFixture();
        var cacheClock = new QuietClock();
        var oldPayload = QuietZip();
        using (var first = new QuietUpdateService(cacheFixture.Path, "1.3.0", QuietReleaseHandler(oldPayload, "v1.3.1"), cacheClock, (_, _) => Task.CompletedTask))
            await first.CheckOnceAsync();
        var oldPath = new QuietUpdateStore(cacheFixture.Path).PackagePath(QuietHash(oldPayload));
        new QuietUpdateStore(cacheFixture.Path).SetEnabled(true);
        var userFile = System.IO.Path.Combine(cacheFixture.Path, "package-user-notes.zip");
        File.WriteAllText(userFile, "must survive");
        var operation = System.IO.Path.Combine(cacheFixture.Path, "apply-existing");
        Directory.CreateDirectory(operation); File.WriteAllText(System.IO.Path.Combine(operation, "result.json"), "must survive");
        cacheClock.Advance(TimeSpan.FromDays(1));
        var newPayload = QuietZip(2000);
        using (var failedNext = new QuietUpdateService(cacheFixture.Path, "1.3.0", QuietReleaseHandler(newPayload, "v1.3.2", corruptDownload: true), cacheClock, (_, _) => Task.CompletedTask))
        {
            await failedNext.CheckOnceAsync();
            Check("failed replacement preserves the previous verified ready package", failedNext.HasUpdate && failedNext.ReadyVersion == "1.3.1" && File.Exists(oldPath));
        }
        cacheClock.Advance(TimeSpan.FromDays(1));
        using (var next = new QuietUpdateService(cacheFixture.Path, "1.3.0", QuietReleaseHandler(newPayload, "v1.3.2"), cacheClock, (_, _) => Task.CompletedTask))
        {
            await next.CheckOnceAsync();
            Check("a newly verified promotion removes only old owned archive caches", next.HasUpdate && next.ReadyVersion == "1.3.2" && !File.Exists(oldPath) && Directory.GetFiles(cacheFixture.Path, "package-*.zip").Length == 2);
            Check("cache pruning preserves unknown user files, preferences and active helper directories", File.ReadAllText(userFile) == "must survive" && File.Exists(System.IO.Path.Combine(operation, "result.json")) && new QuietUpdateStore(cacheFixture.Path).Enabled);
        }
    }

    private static async Task QuietResumeChecks()
    {
        using var fixture = new QuietFixture();
        var clock = new QuietClock();
        var data = QuietZip(180_000);
        var handler = QuietReleaseHandler(data, "v1.3.1");
        Task Delay(TimeSpan duration, CancellationToken token) { token.ThrowIfCancellationRequested(); clock.Advance(duration); return Task.CompletedTask; }
        using (var service = new QuietUpdateService(fixture.Path, "1.3.0", handler, clock, Delay))
        {
            service.SnapshotChanged += (_, _) => { if (service.State == QuietUpdateState.Downloading && service.Progress > 0) service.Enabled = false; };
            await service.CheckOnceAsync();
            Check("off during download preserves only the owned incomplete archive", !service.HasUpdate && Directory.GetFiles(fixture.Path, "*.partial").Length == 1 && !File.Exists(System.IO.Path.Combine(fixture.Path, "ready.json")));
        }
        var partial = Directory.GetFiles(fixture.Path, "*.partial").Single();
        var prefix = new FileInfo(partial).Length;
        Check("cancelled download actually stopped before completion", prefix > 0 && prefix < data.Length);
        clock.Advance(TimeSpan.FromDays(1));
        using var resumed = new QuietUpdateService(fixture.Path, "1.3.0", handler, clock, Delay);
        resumed.Enabled = true;
        await resumed.CheckOnceAsync();
        Check("same authenticated package resumes with an exact HTTP byte range", handler.Ranges.Any(range => range == prefix) && resumed.HasUpdate);
        Check("resumed bytes receive a complete SHA-256 check before ready", File.ReadAllBytes(resumed.PreparedPackage).SequenceEqual(data) && Directory.GetFiles(fixture.Path, "*.partial").Length == 0);

        using var exitFixture = new QuietFixture();
        var exitClock = new QuietClock();
        var startupDelay = true;
        Task ExitDelay(TimeSpan duration, CancellationToken token)
        {
            if (startupDelay) { startupDelay = false; exitClock.Advance(duration); return Task.CompletedTask; }
            return Task.Delay(Timeout.Infinite, token);
        }
        using var exiting = new QuietUpdateService(exitFixture.Path, "1.3.0", QuietReleaseHandler(data, "v1.3.1"), exitClock, ExitDelay);
        exiting.Start(); await QuietWait(() => exiting.Progress > 0);
        var exitTime = Stopwatch.StartNew(); exiting.Dispose(); exitTime.Stop();
        var exitPartial = new QuietUpdateStore(exitFixture.Path).PackagePath(QuietHash(data), partial: true);
        await QuietWait(() => File.Exists(exitPartial));
        Check("app exit keeps a resumable prefix and never waits indefinitely", exitTime.ElapsedMilliseconds < 1000 && new FileInfo(exitPartial).Length is > 0 && new FileInfo(exitPartial).Length < data.Length
            && Directory.GetFiles(exitFixture.Path, ".download-*").Length == 0);

        using var watcherFixture = new QuietFixture();
        var watchGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchClock = new QuietClock();
        var first = true;
        Task WatchDelay(TimeSpan duration, CancellationToken token)
        {
            if (first) { first = false; watchClock.Advance(duration); return Task.CompletedTask; }
            return Task.Delay(Timeout.Infinite, token);
        }
        var watcherHandler = new QuietHandler(async (_, token) => { watchGate.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return QuietJson("[]"); });
        using var watching = new QuietUpdateService(watcherFixture.Path, "1.3.0", watcherHandler, watchClock, WatchDelay);
        watching.Start(); await watchGate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        new QuietUpdateStore(watcherFixture.Path).SetEnabled(false);
        await QuietWait(() => !watching.Enabled);
        Check("another process changing preferences cancels and hides the active request", !watching.Enabled && watching.State == QuietUpdateState.Idle);
    }

    private static async Task QuietTrustChecks()
    {
        var tag = "v1.3.1";
        var good = $"https://github.com/So2K/UltraExplorer/releases/download/{tag}/UltraExplorer-win-x64.zip";
        Check("only the exact HTTPS asset of this repository is accepted", QuietUpdateHttp.IsRepositoryAsset(new Uri(good), tag, QuietUpdateHttp.ArchiveName));
        foreach (var url in new[] { good.Replace("https:", "http:"), good.Replace("So2K", "another"), good.Replace("github.com", "github.com.evil.test"), good.Replace("github.com", "user@github.com"), good + "?x=1", good + "#fragment", good.Replace("github.com", "github.com:444") })
            Check("foreign/insecure asset URL is refused", !QuietUpdateHttp.IsRepositoryAsset(new Uri(url), tag, QuietUpdateHttp.ArchiveName));

        var data = QuietZip();
        using var fixture = new QuietFixture();
        var redirectHandler = QuietReleaseHandler(data, tag, redirectTo: "https://evil.test/package.zip");
        using var redirectService = new QuietUpdateService(fixture.Path, "1.3.0", redirectHandler, new QuietClock());
        await redirectService.CheckOnceAsync();
        Check("an untrusted redirect is refused before sending any request to it", !redirectService.HasUpdate && redirectHandler.Requests.All(uri => uri.Host != "evil.test"));

        using var bounded = new QuietFixture();
        var boundedHandler = new QuietHandler(_ => { var response = QuietJson("[]"); response.Content.Headers.ContentLength = 3_000_000; return response; });
        using var boundedService = new QuietUpdateService(bounded.Path, "1.3.0", boundedHandler, new QuietClock());
        await boundedService.CheckOnceAsync();
        Check("oversized metadata is stopped before deserialization", !boundedService.HasUpdate && boundedService.State == QuietUpdateState.Failed);

        using var malformed = new QuietFixture();
        var malformedJson = JsonSerializer.Serialize(new[] { new { tag_name = tag, draft = false, prerelease = false, assets = new[] {
            new { name = QuietUpdateHttp.ArchiveName, size = "not a number", digest = $"sha256:{QuietHash(data)}", browser_download_url = good }
        } } });
        var malformedHandler = new QuietHandler(_ => QuietJson(malformedJson));
        using var malformedService = new QuietUpdateService(malformed.Path, "1.3.0", malformedHandler, new QuietClock());
        await malformedService.CheckOnceAsync();
        Check("a nonnumeric JSON package size is rejected without a task exception", !malformedService.HasUpdate && malformedHandler.Requests.Count == 1);

        using var wrongRoot = new QuietFixture();
        using var wrongRootService = new QuietUpdateService(wrongRoot.Path, "1.3.0", new QuietHandler(_ => QuietJson("{}")), new QuietClock());
        await wrongRootService.CheckOnceAsync();
        Check("invalid releases root is a quiet failed check", !wrongRootService.HasUpdate && wrongRootService.State == QuietUpdateState.Failed);

        using var corrupt = new QuietFixture();
        var corruptHandler = QuietReleaseHandler(data, tag, corruptDownload: true);
        using var corruptService = new QuietUpdateService(corrupt.Path, "1.3.0", corruptHandler, new QuietClock(), (_, _) => Task.CompletedTask);
        await corruptService.CheckOnceAsync();
        Check("download corruption never produces a ready package", !corruptService.HasUpdate && Directory.GetFiles(corrupt.Path, "*.partial").Length == 0);
    }

    private static async Task QuietCacheOwnershipChecks()
    {
        using var fixture = new QuietFixture();
        Directory.CreateDirectory(fixture.Path);
        var clock = new QuietClock();
        var store = new QuietUpdateStore(fixture.Path);
        var oldPayload = QuietZip();
        var oldHash = QuietHash(oldPayload);
        File.WriteAllBytes(store.PackagePath(oldHash), oldPayload);
        store.SetReady(new QuietUpdateStore.Package("1.3.1", oldHash, oldPayload.Length));
        foreach (var size in new[] { 180_000, 180_001 })
        {
            var payload = QuietZip(size);
            clock.Advance(TimeSpan.FromDays(1));
            using var stopped = new QuietUpdateService(fixture.Path, "1.3.0", QuietReleaseHandler(payload, size == 180_000 ? "v1.3.2" : "v1.3.3"), clock, (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
            stopped.Enabled = true;
            stopped.SnapshotChanged += (_, _) => { if (stopped.State == QuietUpdateState.Downloading && stopped.Progress > 0) stopped.Enabled = false; };
            await stopped.CheckOnceAsync();
            Check("failed successive release downloads keep only current partial and previous ready", Directory.GetFiles(fixture.Path, "*.partial").Length == 1
                && File.Exists(store.PackagePath(QuietHash(payload), partial: true)) && File.Exists(store.PackagePath(oldHash)) && store.Ready?.Version == "1.3.1");
        }

        using var orphanFixture = new QuietFixture();
        Directory.CreateDirectory(orphanFixture.Path);
        var orphan = System.IO.Path.Combine(orphanFixture.Path, $".download-{Guid.NewGuid():N}.partial");
        var unknown = System.IO.Path.Combine(orphanFixture.Path, ".download-user-notes.partial");
        File.WriteAllText(orphan, "interrupted owned stage"); File.WriteAllText(unknown, "user notes");
        new QuietUpdateStore(orphanFixture.Path).RecordAttempt(clock.GetUtcNow());
        var orphanHandler = new QuietHandler(_ => throw new InvalidOperationException("Unexpected HTTP"));
        using var orphanService = new QuietUpdateService(orphanFixture.Path, "1.3.0", orphanHandler, clock);
        await orphanService.CheckOnceAsync();
        Check("the exclusive lease prunes only exact owned orphan stages without HTTP", !File.Exists(orphan) && File.ReadAllText(unknown) == "user notes" && orphanHandler.Requests.Count == 0);

        using var linkedFixture = new QuietFixture();
        Directory.CreateDirectory(linkedFixture.Path);
        var outside = System.IO.Path.Combine(linkedFixture.Path, "outside");
        Directory.CreateDirectory(outside);
        var sentinelPath = System.IO.Path.Combine(outside, "notes.txt");
        var sentinel = "foreign notes must survive background updates"u8.ToArray();
        File.WriteAllBytes(sentinelPath, sentinel);
        var cacheLink = System.IO.Path.Combine(linkedFixture.Path, "cache-junction");
        Check("the owned fixture creates a real directory junction", TryCreateJunction(cacheLink, outside));
        if (Directory.Exists(cacheLink))
        {
            try
            {
                var handler = QuietReleaseHandler(QuietZip(), "v1.3.1");
                using var linked = new QuietUpdateService(cacheLink, "1.3.0", handler, clock);
                await linked.CheckOnceAsync();
                Check("a cache junction is refused without network or writes through it", !linked.Enabled && handler.Requests.Count == 0
                    && Directory.GetFiles(outside).Length == 1 && File.ReadAllBytes(sentinelPath).SequenceEqual(sentinel));
            }
            finally { Directory.Delete(cacheLink, recursive: false); }
        }

        var payloadForLinks = QuietZip(180_000);
        var linkCache = System.IO.Path.Combine(linkedFixture.Path, "hard-link-cache");
        Directory.CreateDirectory(linkCache);
        var partial = new QuietUpdateStore(linkCache).PackagePath(QuietHash(payloadForLinks), partial: true);
        Check("the owned fixture creates a real NTFS hard link", QuietCreateHardLink(partial, sentinelPath, IntPtr.Zero));
        if (File.Exists(partial))
        {
            var handler = QuietReleaseHandler(payloadForLinks, "v1.3.1");
            using var hardLinked = new QuietUpdateService(linkCache, "1.3.0", handler, clock, (_, _) => Task.CompletedTask);
            await hardLinked.CheckOnceAsync();
            Check("an existing hard-linked partial never writes into foreign notes", hardLinked.State == QuietUpdateState.Failed && !hardLinked.HasUpdate
                && File.ReadAllBytes(sentinelPath).SequenceEqual(sentinel));
        }

        var symbolicCache = System.IO.Path.Combine(linkedFixture.Path, "symbolic-cache");
        Directory.CreateDirectory(symbolicCache);
        var symbolicPartial = new QuietUpdateStore(symbolicCache).PackagePath(QuietHash(payloadForLinks), partial: true);
        try
        {
            File.CreateSymbolicLink(symbolicPartial, sentinelPath);
            using var symbolic = new QuietUpdateService(symbolicCache, "1.3.0", QuietReleaseHandler(payloadForLinks, "v1.3.1"), clock, (_, _) => Task.CompletedTask);
            await symbolic.CheckOnceAsync();
            Check("a symbolic partial cannot write into foreign notes", symbolic.State == QuietUpdateState.Failed && File.ReadAllBytes(sentinelPath).SequenceEqual(sentinel));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        { Console.WriteLine("  skip  file symbolic-link fixture: privilege unavailable"); }
        finally { if (File.Exists(symbolicPartial)) File.Delete(symbolicPartial); }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QuietCreateHardLink(string newName, string existingName, IntPtr securityAttributes);

    private static QuietHandler QuietReleaseHandler(byte[] payload, string tag, string? sumsHash = null, string? redirectTo = null, bool corruptDownload = false)
    {
        var hash = QuietHash(payload);
        var release = JsonSerializer.Serialize(new[] { new { tag_name = tag, draft = false, prerelease = tag.Contains('-'), assets = new object[] {
            new { name = QuietUpdateHttp.ArchiveName, size = payload.Length, digest = $"sha256:{hash}", browser_download_url = $"https://github.com/So2K/UltraExplorer/releases/download/{tag}/{QuietUpdateHttp.ArchiveName}" },
            new { name = QuietUpdateHttp.ChecksumsName, size = 200, browser_download_url = $"https://github.com/So2K/UltraExplorer/releases/download/{tag}/{QuietUpdateHttp.ChecksumsName}" }
        } } });
        return new QuietHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "api.github.com") return QuietJson(release);
            if (uri.AbsolutePath.EndsWith(QuietUpdateHttp.ChecksumsName)) return QuietJson($"{sumsHash ?? hash}  {QuietUpdateHttp.ArchiveName}\n");
            if (redirectTo is not null) { var redirect = new HttpResponseMessage(HttpStatusCode.Found); redirect.Headers.Location = new Uri(redirectTo); return redirect; }
            var from = request.Headers.Range?.Ranges.Single().From ?? 0;
            var bytes = corruptDownload ? new byte[payload.Length] : payload;
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ByteArrayContent(bytes[(int)from..]) };
            if (from > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, payload.Length - 1, payload.Length);
            return response;
        });
    }

    private static HttpResponseMessage QuietJson(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8) };
    private static string QuietHash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] QuietZip(int size = 1500)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("UltraExplorer.exe", CompressionLevel.NoCompression);
            using var output = entry.Open();
            var bytes = new byte[size]; new Random(37).NextBytes(bytes); output.Write(bytes);
        }
        return stream.ToArray();
    }
    private static async Task QuietWait(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class QuietFixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UltraExplorerQuietUpdateChecks", Guid.NewGuid().ToString("N"));
        public void Dispose() { TryDelete(Path); }
    }
    private sealed class QuietClock : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utc;
        public override long GetTimestamp() => _utc.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan duration) => _utc += duration;
    }
    private sealed class QuietHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        public List<Uri> Requests { get; } = [];
        public List<long> Ranges { get; } = [];
        public QuietHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : this((request, _) => Task.FromResult(send(request))) { }
        public QuietHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!);
            if (request.Headers.Range?.Ranges.Single().From is { } from) Ranges.Add(from);
            return _send(request, cancellationToken);
        }
    }
}
