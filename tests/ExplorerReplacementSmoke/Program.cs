using System.IO;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;

namespace ExplorerReplacementSmoke;

internal static class Program
{
    private static int _checks, _failures;
    private static async Task<int> Main()
    {
        var priorState = Environment.GetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR");
        var stateParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UltraExplorerTransferRun"));
        var stateDirectory = Path.Combine(stateParent, Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR", stateDirectory);
        DialogIntegrationStore.Update(settings => settings with { Enabled = true });
        try { return await RunAllAsync(); }
        finally
        {
            Environment.SetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR", priorState);
            var resolved = Path.GetFullPath(stateDirectory);
            if (!string.Equals(Path.GetDirectoryName(resolved), stateParent, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
                throw new InvalidOperationException("Unexpected fixture state cleanup target.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private static async Task<int> RunAllAsync()
    {
        Assert(!DialogStartup.IsAllowed, "isolated state disables global integration and registry startup changes in fixtures");
        await Run("native browsing policy follows a frame across folders and tabs", async fixture =>
        {
            var policy = new ExplorerNativeSessionPolicy();
            var source = fixture.Current;
            var identity = ExplorerFrameIdentity.From(source);
            var second = await fixture.AdditionalSourceAsync();
            var secondIdentity = ExplorerFrameIdentity.From(second);
            policy.ReconcileFrames([identity], baseline: true);
            Assert(policy.IsNative(source), "an existing native filesystem window is exempt");
            policy.ReconcileFrames([identity, secondIdentity], baseline: false);
            Assert(!policy.IsNative(second), "a new external window in the same PID remains eligible");
            Assert(policy.IsNative(source with { FolderPath = "This PC", FolderPidl = ReadOnlyMemory<byte>.Empty, BrowserHwnd = 100, ViewHwnd = 101 }),
                "native intent does not depend on the current path or tab");
            Assert(policy.IsNative(source with { FolderPath = Path.Combine(fixture.Folder, "nested"), Generation = 9 }),
                "navigating within an original Explorer window stays native");
            policy.Protect(secondIdentity); // Models a newly observed virtual This PC frame.
            Assert(policy.IsNative(second with { FolderPath = @"C:\", FolderPidl = new byte[] { 2, 0, 1, 0 } }),
                "a virtual frame retains native intent when it becomes a disk view");
            Assert(!policy.IsNative(source with { SourceProcessStartUtc = source.SourceProcessStartUtc.AddSeconds(1) }),
                "HWND reuse from another process lifetime is not exempt");
            Assert(!policy.IsNative(source with { SourceThreadId = source.SourceThreadId + 1 }),
                "HWND reuse from another UI thread is not exempt");
            policy.ReconcileFrames([secondIdentity], baseline: false);
            Assert(!policy.IsNative(source) && policy.IsNative(second), "only a closed frame loses its exception");
        });

        await Run("native browsing navigation establishes a sticky exception", fixture =>
        {
            var policy = new ExplorerNativeSessionPolicy();
            var source = fixture.Current;
            policy.ReconcileFrames([ExplorerFrameIdentity.From(source)], baseline: false);
            policy.ObserveFolder(source);
            Assert(!policy.IsNative(source), "initial external folder snapshot is adoptable");
            policy.ObserveFolder(source with { SelectedPaths = [], FocusedPath = null, Generation = 2 });
            Assert(!policy.IsNative(source), "initial external selection changes do not invent native intent");
            policy.ObserveFolder(source with { FolderPath = Path.Combine(fixture.Folder, "nested"), Generation = 3 });
            Assert(policy.IsNative(source), "user navigation in an existing native frame establishes native intent");
            policy.ObserveFolder(source);
            Assert(policy.IsNative(source), "Back to the first folder retains the exception");
            return Task.CompletedTask;
        });

        await Run("native exception prevents any destination preparation", async fixture =>
        {
            using var coordinator = new ExplorerReplacementCoordinator(fixture.Actions() with { NativeSession = _ => true });
            await coordinator.TransferForChecksAsync(fixture.Current);
            Assert(fixture.SourceExists && fixture.OpenCalls == 0 && fixture.CloseCalls == 0 && fixture.ValidationCalls == 0,
                "a native session is never replaced or closed");
        });

        foreach (var stage in new[] { "ready", "destination-validation", "source-validation" })
        {
            await Run("native exception during " + stage + " preserves source", async fixture =>
            {
                var native = false;
                fixture.PauseReady = stage == "ready";
                fixture.PauseDestinationValidation = stage == "destination-validation";
                fixture.PauseFinalSourceValidation = stage == "source-validation";
                using var coordinator = new ExplorerReplacementCoordinator(fixture.Actions() with { NativeSession = _ => native });
                var transfer = coordinator.TransferForChecksAsync(fixture.Current);
                var requested = stage switch
                {
                    "ready" => fixture.ReadyRequested,
                    "destination-validation" => fixture.DestinationValidationRequested,
                    _ => fixture.FinalSourceValidationRequested
                };
                await requested.Task.WaitAsync(TimeSpan.FromSeconds(3));
                native = true;
                fixture.ReleaseReady.TrySetResult();
                fixture.ReleaseDestinationValidation.TrySetResult();
                fixture.ReleaseFinalSourceValidation.TrySetResult();
                await transfer;
                Assert(fixture.SourceExists && fixture.CloseCalls == 0 && fixture.ClaimsCleaned,
                    "late native intent cancels closing and releases the source claim");
            });
        }

        await Run("production observer keeps exact owned test scope", async fixture =>
        {
            var callbacks = 0;
            using var observer = new ExplorerWindowObserver(new(fixture.Current.SourceProcessId, fixture.Current.RootHwnd));
            observer.Start(_ => Interlocked.Increment(ref callbacks));
            var snapshots = await observer.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert(snapshots.Count == 0 && callbacks == 0, "fake owned source never relaxes real Explorer COM/process filters");
            Assert(fixture.SourceExists, "read-only scoped observer leaves owned source in place");
        });
        await Run("readiness precedes the only owned close", async fixture =>
        {
            fixture.PauseReady = true;
            using var coordinator = new ExplorerReplacementCoordinator(fixture.Actions());
            var transfer = coordinator.TransferForChecksAsync(fixture.Current);
            await fixture.ReadyRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(fixture.SourceExists && fixture.CloseCalls == 0, "source remains until destination ready");
            Assert(!fixture.SourceVisible, "message-only fixture never becomes visible");
            fixture.ReleaseReady.SetResult();
            await transfer;
            await Until(() => !fixture.SourceExists);
            Assert(fixture.CloseCalls == 1 && fixture.ValidationCalls >= 2, "unchanged source closes only after revalidation");
            Assert(fixture.ClaimDisposals == 1 && fixture.ClaimsCleaned, "source claim is cleaned");
            Assert(fixture.LastInvocation!.SelectedPaths[0] == fixture.Current.FocusedPath, "focused selected file is preserved first");
            Assert(fixture.LastReceipt!.SelectedPaths.Count == 2, "all selected files arrive at real owned pipe endpoint");
        });

        foreach (var state in new[] { "not-ready", "rejected", "wrong-destination", "wrong-folder", "partial-selection", "extra-selection", "wrong-nonce", "wrong-process-start", "dead-destination" })
        {
            await Run(state + " preserves source", async fixture =>
            {
                fixture.MutateReceipt = receipt => state switch
                {
                    "not-ready" => receipt with { Ready = false },
                    "rejected" => receipt with { Accepted = false },
                    "wrong-destination" => receipt with { DestinationId = Guid.NewGuid() },
                    "wrong-folder" => receipt with { FolderPath = Path.Combine(fixture.Folder, "elsewhere") },
                    "partial-selection" => receipt with { SelectedPaths = receipt.SelectedPaths.Take(1).ToArray() },
                    "extra-selection" => receipt with { SelectedPaths = receipt.SelectedPaths.Append(Path.Combine(fixture.Folder, "extra.txt")).ToArray() },
                    "wrong-nonce" => receipt with { Request = Guid.NewGuid() },
                    "wrong-process-start" => receipt with { Started = receipt.Started + 1 },
                    _ => receipt
                };
                if (state == "dead-destination") fixture.ExtraDestinationAlive = () => false;
                using var coordinator = new ExplorerReplacementCoordinator(fixture.Actions());
                await coordinator.TransferForChecksAsync(fixture.Current);
                Assert(fixture.SourceExists && fixture.CloseCalls == 0, "failed receipt never closes source");
                Assert(fixture.ClaimsCleaned, "failed receipt releases only its own source claim");
            });
        }

        await Run("OFF during final source await", async fixture =>
        {
            fixture.PauseFinalSourceValidation = true;
            using var coordinator = new ExplorerReplacementCoordinator(fixture.Actions());
            var transfer = coordinator.TransferForChecksAsync(fixture.Current);
            await fixture.FinalSourceValidationRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.Enabled = false;
            fixture.ReleaseFinalSourceValidation.SetResult();
            await transfer;
            Assert(fixture.SourceExists && fixture.CloseCalls == 0, "OFF is reread after final awaited source validation");
        });

        await Run("OFF during destination await", async fixture =>
        {
            fixture.PauseDestinationValidation = true;
            using var coordinator = new ExplorerReplacementCoordinator(fixture.Actions());
            var transfer = coordinator.TransferForChecksAsync(fixture.Current);
            await fixture.DestinationValidationRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.Enabled = false;
            fixture.ReleaseDestinationValidation.SetResult();
            await transfer;
            Assert(fixture.SourceExists && fixture.CloseCalls == 0, "OFF is reread after destination readiness validation");
        });

        await Run("dispose during source await", async fixture =>
        {
            fixture.PauseFinalSourceValidation = true;
            using var coordinator = new ExplorerReplacementCoordinator(fixture.Actions());
            var transfer = coordinator.TransferForChecksAsync(fixture.Current);
            await fixture.FinalSourceValidationRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));
            coordinator.Dispose();
            fixture.ReleaseFinalSourceValidation.TrySetResult();
            await transfer;
            Assert(fixture.SourceExists && fixture.CloseCalls == 0 && fixture.ClaimsCleaned, "disposal preserves source and releases claim");
        });

        foreach (var change in new[] { "tab", "view", "folder", "selection", "focus", "generation", "process-lifetime" })
        {
            await Run("changed " + change + " invalidates prepared transfer", async fixture =>
            {
                fixture.PauseReady = true;
                using var coordinator = new ExplorerReplacementCoordinator(fixture.Actions());
                var transfer = coordinator.TransferForChecksAsync(fixture.Current);
                await fixture.ReadyRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));
                fixture.Current = change switch
                {
                    "tab" => fixture.Current with { BrowserHwnd = 1 },
                    "view" => fixture.Current with { ViewHwnd = 1 },
                    "folder" => fixture.Current with { FolderPath = Path.Combine(fixture.Folder, "elsewhere") },
                    "selection" => fixture.Current with { SelectedPaths = [] },
                    "focus" => fixture.Current with { FocusedPath = null },
                    "generation" => fixture.Current with { Generation = 2 },
                    _ => fixture.Current with { SourceProcessStartUtc = fixture.Current.SourceProcessStartUtc.AddSeconds(1) }
                };
                fixture.ReleaseReady.SetResult();
                await transfer;
                Assert(fixture.SourceExists && fixture.CloseCalls == 0, "stale source snapshot never closes its frame");
            });
        }

        await Run("lost source nonce preserves source", async fixture =>
        {
            fixture.PauseReady = true;
            using var coordinator = new ExplorerReplacementCoordinator(fixture.Actions());
            var transfer = coordinator.TransferForChecksAsync(fixture.Current);
            await fixture.ReadyRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.ReplaceSourceCookie(12345);
            fixture.ReleaseReady.SetResult();
            await transfer;
            Assert(fixture.SourceExists && fixture.CloseCalls == 0, "a replaced source cookie refuses close");
            Assert(fixture.SourceCookie == 12345, "cleanup does not remove a newer claimant's cookie");
        });

        await Run("timeout and late readiness preserve source", async fixture =>
        {
            fixture.PauseReady = true;
            using var coordinator = new ExplorerReplacementCoordinator(fixture.Actions(TimeSpan.FromMilliseconds(250)));
            var transfer = coordinator.TransferForChecksAsync(fixture.Current);
            await fixture.ReadyRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await transfer.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(fixture.SourceExists && fixture.CloseCalls == 0 && fixture.ClaimsCleaned, "readiness timeout preserves original source");
            fixture.ReleaseReady.SetResult();
            await Task.Delay(100);
            Assert(fixture.SourceExists && fixture.CloseCalls == 0, "late endpoint completion cannot resurrect expired close");
        });

        await Run("pre-commit timeout creates no destination", async fixture =>
        {
            fixture.PauseOffer = true;
            using var coordinator = new ExplorerReplacementCoordinator(fixture.Actions(TimeSpan.FromMilliseconds(250)));
            var transfer = coordinator.TransferForChecksAsync(fixture.Current);
            await fixture.OfferRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await transfer.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.ReleaseOffer.SetResult();
            await Task.Delay(100);
            Assert(fixture.SourceExists && fixture.CloseCalls == 0 && fixture.DestinationCreations == 0,
                "timed-out uncommitted offer has no late window side effect");
        });

        await Run("late non-cancellable source read cannot close after deadline", async fixture =>
        {
            var actions = fixture.Actions(TimeSpan.FromMilliseconds(500));
            var reads = 0;
            var lateReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            actions = actions with
            {
                ValidateSource = async (snapshot, token) =>
                {
                    if (Interlocked.Increment(ref reads) >= 2)
                    {
                        lateReadStarted.TrySetResult();
                        // Models a remote native read which completes after its
                        // requested cancellation. The coordinator owns timeout.
                        await Task.Delay(700);
                        return await fixture.Actions().ValidateSource(snapshot, CancellationToken.None);
                    }
                    return await fixture.Actions().ValidateSource(snapshot, token);
                }
            };
            using var coordinator = new ExplorerReplacementCoordinator(actions);
            var transfer = coordinator.TransferForChecksAsync(fixture.Current);
            await lateReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await transfer.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(fixture.SourceExists && fixture.CloseCalls == 0, "expired local transfer deadline refuses late source approval");
        });

        await Run("duplicate callback has one transfer", async fixture =>
        {
            fixture.PauseReady = true;
            using var coordinator = new ExplorerReplacementCoordinator(fixture.Actions());
            var first = coordinator.TransferForChecksAsync(fixture.Current);
            await fixture.ReadyRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await coordinator.TransferForChecksAsync(fixture.Current);
            Assert(fixture.OpenCalls == 1, "a frame has one simultaneous destination request");
            fixture.ReleaseReady.SetResult();
            await first;
            Assert(fixture.CloseCalls == 1, "duplicate callbacks do not duplicate source close");
        });

        await Run("different source frames keep distinct destinations", async fixture =>
        {
            var firstSource = fixture.Current;
            var secondSource = await fixture.AdditionalSourceAsync();
            Assert(!ExplorerWindowObserver.SourceIsIdle(firstSource.RootHwnd, secondSource.ViewHwnd, firstSource.SourceThreadId),
                "an owned view from another frame cannot pass the native source idle check");
            using var coordinator = new ExplorerReplacementCoordinator(fixture.Actions() with { CloseSource = _ => false });
            await coordinator.TransferForChecksAsync(firstSource);
            var firstReceipt = fixture.LastReceipt!;
            fixture.Current = secondSource;
            await coordinator.TransferForChecksAsync(secondSource);
            var secondReceipt = fixture.LastReceipt!;
            Assert(firstReceipt.DestinationId != secondReceipt.DestinationId && firstReceipt.Window != secondReceipt.Window,
                "each source frame owns a separate native destination HWND");
            fixture.Current = firstSource with { Generation = 2 };
            await coordinator.TransferForChecksAsync(fixture.Current);
            Assert(fixture.LastReceipt!.DestinationId == firstReceipt.DestinationId && fixture.LastReceipt.Window == firstReceipt.Window,
                "retry of one source reuses its own destination identity");
        });

        await Run("disabled modal frame preserves source", async fixture =>
        {
            fixture.SetSourceEnabled(false);
            using var coordinator = new ExplorerReplacementCoordinator(fixture.Actions());
            await coordinator.TransferForChecksAsync(fixture.Current);
            Assert(fixture.SourceExists && fixture.CloseCalls == 0 && fixture.OpenCalls == 0, "disabled native source is not transferred");
        });

        await Run("modal operation begins during destination preparation", async fixture =>
        {
            fixture.PauseReady = true;
            using var coordinator = new ExplorerReplacementCoordinator(fixture.Actions());
            var transfer = coordinator.TransferForChecksAsync(fixture.Current);
            await fixture.ReadyRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.SetSourceEnabled(false);
            fixture.ReleaseReady.SetResult();
            await transfer;
            Assert(fixture.SourceExists && fixture.CloseCalls == 0, "late modal operation invalidates the final source check");
        });

        await Run("empty selection stays empty", async fixture =>
        {
            fixture.Current = fixture.Current with { SelectedPaths = [], FocusedPath = null };
            using var coordinator = new ExplorerReplacementCoordinator(fixture.Actions());
            await coordinator.TransferForChecksAsync(fixture.Current);
            Assert(fixture.LastInvocation is { Kind: FolderInvocationKind.OpenFolder, SelectedPaths.Count: 0 }
                && fixture.LastReceipt is { SelectedPaths.Count: 0 }, "opening a folder does not invent selected children");
        });

        Assert(!ExplorerWindowObserver.HasNoPendingInput(0, true, "Edit", false), "inline rename editor defers source transfer");
        Assert(!ExplorerWindowObserver.HasNoPendingInput(0, true, "RichEditD2DPT", false), "rich inline editor defers source transfer");
        Assert(ExplorerWindowObserver.HasNoPendingInput(0, false, "Edit", false), "unrelated edit outside the source frame is distinguished");
        Assert(!ExplorerWindowObserver.HasNoPendingInput(4, false, "", false), "active native menu defers transfer");
        Assert(!ExplorerWindowObserver.HasNoPendingInput(2, false, "", false), "moving or resizing source defers transfer");
        Assert(!ExplorerWindowObserver.HasNoPendingInput(0, false, "", true), "visible owned modal prompt defers transfer");
        Console.WriteLine($"{_checks - _failures}/{_checks} checks passed");
        return _failures == 0 ? 0 : 1;
    }

    private static async Task Run(string name, Func<OwnedFixture, Task> scenario)
    {
        Console.WriteLine(name);
        try
        {
            using var fixture = await OwnedFixture.CreateAsync();
            await scenario(fixture).WaitAsync(TimeSpan.FromSeconds(6));
        }
        catch (Exception ex) { Assert(false, "unexpected fixture failure: " + ex); }
    }

    private static async Task Until(Func<bool> condition)
    {
        var limit = Environment.TickCount64 + 1000;
        while (!condition() && Environment.TickCount64 < limit) await Task.Delay(10);
        Assert(condition(), "owned native close is delivered");
    }

    private static void Assert(bool condition, string name)
    {
        _checks++;
        if (!condition) _failures++;
        Console.WriteLine((condition ? "  ok   " : "  FAIL ") + name);
    }
}
