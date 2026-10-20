using System.Net.Http;
using System.Text.Json;
using UltraExplorer.Services.Updates;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task QuietUpdateChannelChecks()
    {
        Section("update channels, explicit checks and installation generation");
        await QuietChannelCatalogChecks();
        await QuietChannelPreferenceChecks();
        await QuietManualCheckChecks();
        await QuietChannelCancellationChecks();
        await QuietManualCancellationCallbackChecks();
        await QuietChannelInstallChecks();
    }

    private static string QuietCatalog(params (string Tag, bool Draft, bool Prerelease)[] entries) => JsonSerializer.Serialize(entries.Select(entry => new
    {
        tag_name = entry.Tag, draft = entry.Draft, prerelease = entry.Prerelease,
        assets = new object[] { new { name = QuietUpdateHttp.ArchiveName, size = 100,
            digest = "sha256:" + new string('a', 64),
            browser_download_url = $"https://github.com/So2K/UltraExplorer/releases/download/{Uri.EscapeDataString(entry.Tag)}/{QuietUpdateHttp.ArchiveName}" } }
    }));

    private static async Task QuietChannelCatalogChecks()
    {
        var catalog = QuietCatalog(("v9.0.0", true, false), ("v2.0.0-experimental.1", false, true),
            ("v1.8.0-beta.experimental.1", false, true), ("v1.6.0-alpha.1", false, true),
            ("v1.5.0-rc.2", false, true), ("v1.4.0-beta.3", false, true), ("v1.3.5", false, false));
        using var http = new QuietUpdateHttp(new QuietHandler(_ => QuietJson(catalog)));
        var installed = UpdateVersion.Parse("1.3.0")!;
        Check("stable selects only a newer public stable release", (await http.FindAsync(installed, QuietUpdateChannel.Stable, default))?.Version.Value == "1.3.5");
        Check("beta includes RC but excludes alpha, nested experimental labels and drafts", (await http.FindAsync(installed, QuietUpdateChannel.Beta, default))?.Version.Value == "1.5.0-rc.2");
        Check("experimental includes the newest valid public experimental release", (await http.FindAsync(installed, QuietUpdateChannel.Experimental, default))?.Version.Value == "2.0.0-experimental.1");
        Check("selecting stable never downgrades an installed newer experimental version",
            await http.FindAsync(UpdateVersion.Parse("2.0.0-experimental.5")!, QuietUpdateChannel.Stable, default) is null);
        using var metadata = new QuietUpdateHttp(new QuietHandler(_ => QuietJson(QuietCatalog(("v1.3.0+newcommit", false, false)))));
        Check("different commit metadata alone is not an update in any channel",
            await metadata.FindAsync(UpdateVersion.Parse("1.3.0+oldcommit")!, QuietUpdateChannel.Experimental, default) is null);
        using var flagged = new QuietUpdateHttp(new QuietHandler(_ => QuietJson(QuietCatalog(("v1.4.0", false, true)))));
        Check("GitHub prerelease=true on a plain version remains experimental-only",
            await flagged.FindAsync(installed, QuietUpdateChannel.Stable, default) is null
            && await flagged.FindAsync(installed, QuietUpdateChannel.Beta, default) is null
            && (await flagged.FindAsync(installed, QuietUpdateChannel.Experimental, default))?.MinimumChannel == QuietUpdateChannel.Experimental);
    }

    private static async Task QuietChannelPreferenceChecks()
    {
        using var fixture = new QuietFixture();
        var store = new QuietUpdateStore(fixture.Path);
        store.SetEnabled(false);
        using (var migrated = new QuietUpdateService(fixture.Path, "1.3.1-beta.2+test", new QuietHandler(_ => throw new InvalidOperationException()), new QuietClock()))
            Check("legacy opt-out migrates to the installed beta channel without enabling checks", !migrated.Enabled && migrated.Channel == QuietUpdateChannel.Beta);
        store.SetChannel(QuietUpdateChannel.Experimental);
        store.SetEnabled(true);
        Check("changing enabled preserves the selected persisted channel", store.Enabled && store.Channel == QuietUpdateChannel.Experimental);
        store.SetEnabled(false);
        store.SetChannel(QuietUpdateChannel.Stable);
        Check("changing channel preserves a persisted opt-out", !store.Enabled && store.Channel == QuietUpdateChannel.Stable);
        foreach (var value in new[] { "\"Unknown\"", "42", "\"1\"", "\"Beta\",\"Channel\":\"Experimental\"" })
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(fixture.Path, "settings.json"), "{\"Enabled\":false,\"Channel\":" + value + "}");
            Check("invalid or duplicate channel values fail conservatively to stable while preserving off", store.Channel == QuietUpdateChannel.Stable && !store.Enabled);
        }
        var second = new QuietUpdateStore(fixture.Path + System.IO.Path.DirectorySeparatorChar);
        var preserved = true;
        for (var index = 0; index < 20; index++)
        {
            store.SetEnabled(true); store.SetChannel(QuietUpdateChannel.Stable);
            await Task.WhenAll(Task.Run(() => store.SetEnabled(false)), Task.Run(() => second.SetChannel(QuietUpdateChannel.Experimental)));
            preserved &= !store.Enabled && store.Channel == QuietUpdateChannel.Experimental;
        }
        Check("independent store writers serialize read/modify/write and retain both opt-out and channel choices", preserved);
        using var stable = new QuietFixture();
        using var stableService = new QuietUpdateService(stable.Path, "1.3.1", new QuietHandler(_ => QuietJson("[]")), new QuietClock());
        using var experimental = new QuietFixture();
        using var experimentalService = new QuietUpdateService(experimental.Path, "1.3.2-experimental.1", new QuietHandler(_ => QuietJson("[]")), new QuietClock());
        Check("fresh stable and experimental installations derive their respective default channels",
            stableService.Channel == QuietUpdateChannel.Stable && experimentalService.Channel == QuietUpdateChannel.Experimental);
    }

    private static async Task QuietManualCheckChecks()
    {
        using var fixture = new QuietFixture();
        var clock = new QuietClock();
        var handler = new QuietHandler(_ => QuietJson("[]"));
        using var service = new QuietUpdateService(fixture.Path, "1.3.0-beta.1", handler, clock);
        var first = await service.CheckNowAsync();
        var second = await service.CheckNowAsync();
        Check("explicit checks bypass both startup delay and the daily throttle", first.Status == QuietUpdateCheckStatus.UpToDate
            && second.Status == QuietUpdateCheckStatus.UpToDate && handler.Requests.Count == 2 && !service.IsChecking);
        await service.CheckOnceAsync();
        service.Channel = QuietUpdateChannel.Experimental;
        await service.CheckOnceAsync();
        Check("manual checks still persist the daily attempt and channel changes never spam automatic checks", handler.Requests.Count == 2);

        using var offFixture = new QuietFixture();
        var payload = QuietZip();
        var offHandler = QuietReleaseHandler(payload, "v1.3.0-beta.2");
        using var off = new QuietUpdateService(offFixture.Path, "1.3.0-beta.1", offHandler, clock);
        off.Enabled = false;
        var found = await off.CheckNowAsync();
        Check("manual checks with automatic updates off return metadata only without download, ready offer or opt-in",
            found == new QuietUpdateCheckResult(QuietUpdateCheckStatus.Available, "1.3.0-beta.2")
            && offHandler.Requests.Count == 1 && offHandler.Requests[0].Host == "api.github.com"
            && !off.Enabled && !new QuietUpdateStore(offFixture.Path).Enabled && !off.HasUpdate && off.State == QuietUpdateState.Idle);
        off.Enabled = true;
        var ready = await off.CheckNowAsync();
        Check("an enabled manual check downloads and verifies the normal package without installing",
            ready.Status == QuietUpdateCheckStatus.Ready && off.HasUpdate && off.State == QuietUpdateState.Ready
            && await off.PrepareInstallAsync() is not null);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Check("a canceled manual action returns the typed canceled result", (await service.CheckNowAsync(canceled.Token)).Status == QuietUpdateCheckStatus.Cancelled);
    }

    private static async Task QuietChannelCancellationChecks()
    {
        using var fixture = new QuietFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new QuietHandler(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task; // Deliberately returns a stale catalog after cancellation.
            return QuietJson(QuietCatalog(("v2.0.0-experimental.1", false, true)));
        });
        using var service = new QuietUpdateService(fixture.Path, "1.3.0-beta.1", handler, new QuietClock());
        service.Channel = QuietUpdateChannel.Experimental;
        var old = service.CheckNowAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check("manual and automatic work share one process-local in-flight operation", service.IsChecking
            && (await service.CheckNowAsync()).Status == QuietUpdateCheckStatus.Busy);
        service.Channel = QuietUpdateChannel.Stable;
        release.TrySetResult();
        Check("changing channels cancels stale metadata completion before it can download or publish ready",
            (await old.WaitAsync(TimeSpan.FromSeconds(5))).Status == QuietUpdateCheckStatus.Cancelled
            && service.Channel == QuietUpdateChannel.Stable && !service.IsChecking && !service.HasUpdate && handler.Requests.Count == 1);
    }

    private static async Task QuietChannelInstallChecks()
    {
        using var fixture = new QuietFixture();
        var payload = QuietZip();
        var handler = QuietReleaseHandler(payload, "v1.3.0-beta.2");
        using var service = new QuietUpdateService(fixture.Path, "1.3.0-beta.1", handler, new QuietClock(), (_, _) => Task.CompletedTask);
        await service.CheckNowAsync();
        var prepared = await service.PrepareInstallAsync();
        service.Channel = QuietUpdateChannel.Stable;
        service.RestoreReady();
        Check("changing to stable invalidates a prepared beta and cannot resurrect it via restore",
            prepared is not null && !service.MarkInstalling(prepared) && !service.HasUpdate && await service.PrepareInstallAsync() is null);
        service.Channel = QuietUpdateChannel.Beta;
        await service.CheckNowAsync();
        var fresh = await service.PrepareInstallAsync();
        service.Channel = QuietUpdateChannel.Experimental;
        service.Channel = QuietUpdateChannel.Beta;
        await service.CheckNowAsync();
        Check("even a compatible channel round trip invalidates an older preparation generation", fresh is not null && !service.MarkInstalling(fresh));
        var current = await service.PrepareInstallAsync();
        Check("a current verified preparation can enter installation explicitly", current is not null && service.MarkInstalling(current));
        var before = handler.Requests.Count;
        service.Enabled = false;
        service.Channel = QuietUpdateChannel.Experimental;
        var blocked = await service.CheckNowAsync();
        Check("off and channel toggles cannot remove a confirmed installation's concurrency guard",
            service.State == QuietUpdateState.Installing && !service.Enabled && service.Channel == QuietUpdateChannel.Beta
            && blocked.Status == QuietUpdateCheckStatus.Busy && handler.Requests.Count == before);
        service.Enabled = true;
        Check("re-enabling keeps the installation guard until explicit result settlement", service.State == QuietUpdateState.Installing);
        service.Enabled = false;
        service.RestoreReady();
        Check("settling a canceled or failed transaction while off exits to disabled idle", service.State == QuietUpdateState.Idle && !service.Enabled && !service.HasUpdate);
        service.Channel = QuietUpdateChannel.Experimental;
        Check("channel choices become available after installation has actually settled", service.Channel == QuietUpdateChannel.Experimental);

        using var legacyFixture = new QuietFixture();
        var legacyStore = new QuietUpdateStore(legacyFixture.Path);
        legacyStore.SetEnabled(true);
        legacyStore.SetChannel(QuietUpdateChannel.Experimental);
        var hash = QuietHash(payload);
        System.IO.File.WriteAllBytes(legacyStore.PackagePath(hash), payload);
        legacyStore.SetReady(new QuietUpdateStore.Package("1.3.1", hash, payload.Length));
        using var legacy = new QuietUpdateService(legacyFixture.Path, "1.3.0", new QuietHandler(_ => QuietJson("[]")), new QuietClock());
        await legacy.CheckOnceAsync();
        Check("legacy ready packages can still be used offline within their unchanged channel", legacy.HasUpdate);
        legacy.Channel = QuietUpdateChannel.Stable;
        legacy.RestoreReady();
        Check("explicitly narrowing a channel revalidates legacy packages whose GitHub prerelease flag was not saved",
            !legacy.HasUpdate && legacyStore.Ready is null && await legacy.PrepareInstallAsync() is null);
    }

    private static async Task QuietManualCancellationCallbackChecks()
    {
        using var caller = new CancellationTokenSource();
        using var releaseCallback = new ManualResetEventSlim();
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Register on the actual updater-owned token. HttpClient wraps its
        // handler token in its own linked source, whose Dispose deliberately
        // waits for callbacks; that separate framework contract is not what
        // the updater's deferred source disposal is intended to change.
        using var operation = new QuietUpdateService.UpdateOperation(caller.Token, CancellationToken.None);
        operation.Token.Register(() =>
        {
            callbackStarted.TrySetResult();
            releaseCallback.Wait(TimeSpan.FromSeconds(5));
            callbackFinished.TrySetResult();
        });
        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            caller.Cancel();
            timer.Stop();
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check("manual cancellation signals promptly without blocking settings on provider callbacks",
                timer.ElapsedMilliseconds < 250 && operation.IsCancellationRequested && !callbackFinished.Task.IsCompleted);
            timer.Restart();
            operation.Dispose();
            timer.Stop();
            Check("releasing the updater-owned operation does not wait for its running cancellation callback",
                timer.ElapsedMilliseconds < 250 && !callbackFinished.Task.IsCompleted);
            releaseCallback.Set();
            await callbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check("provider cancellation callbacks still complete after the check has released its operation", callbackFinished.Task.IsCompletedSuccessfully);
        }
        finally { releaseCallback.Set(); }

        using var fixture = new QuietFixture();
        using var requestCancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new QuietHandler(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return QuietJson("[]");
        });
        using var service = new QuietUpdateService(fixture.Path, "1.3.0", handler, new QuietClock());
        var check = service.CheckNowAsync(requestCancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        requestCancellation.Cancel();
        Check("canceling an in-flight manual HTTP check returns the typed result and releases the check guard",
            (await check.WaitAsync(TimeSpan.FromSeconds(5))).Status == QuietUpdateCheckStatus.Cancelled && !service.IsChecking);
    }
}
