using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using UltraExplorer.Services;

namespace ExplorerReplacementSmoke;

internal static partial class Program
{
    private static async Task CoordinatorRetirementChecks()
    {
        await Run("refused frames retain no destination and expired retry entries retire", async fixture =>
        {
            using var coordinator = new ExplorerReplacementCoordinator(fixture.Actions() with
            { ValidateSource = (_, _) => Task.FromResult(false) });
            for (var index = 0; index < 64; index++)
                await coordinator.TransferForChecksAsync(fixture.Current with { RootHwnd = (nint)(0x7000_0000 + index) });
            Assert(CoordinatorMap(coordinator, "_destinations").Count == 0,
                "a refused source allocates no permanent destination identity");
            var retries = (ConcurrentDictionary<(nint Root, uint Process, DateTime Start), (long Generation, long Until)>)
                typeof(ExplorerReplacementCoordinator).GetField("_retry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)!;
            var expired = (Root: (nint)0x7010_0000, Process: fixture.Current.SourceProcessId, Start: fixture.Current.SourceProcessStartUtc);
            retries[expired] = (1, Environment.TickCount64 - 1);
            await coordinator.TransferForChecksAsync(fixture.Current);
            Assert(!retries.ContainsKey(expired) && retries.Count == 65,
                "expired retry records are pruned while recent frame throttles remain");
        });

        await Run("abandoned identities retire even without a fixture discard callback", async fixture =>
        {
            using var coordinator = new ExplorerReplacementCoordinator(fixture.Actions() with { CloseSource = _ => false });
            await coordinator.TransferForChecksAsync(fixture.Current);
            Assert(fixture.OpenCalls == 1 && CoordinatorMap(coordinator, "_destinations").Count == 1,
                "an abandoned prepared destination retains its identity through the retry grace period");
            await Task.Delay(750);
            Assert(CoordinatorMap(coordinator, "_destinations").Count == 0 && fixture.SourceExists,
                "after grace the abandoned identity retires without closing the source");
        });

        await Run("accepted close does not wait on an expiring transfer deadline", async fixture =>
        {
            var accepted = 0;
            using var coordinator = new ExplorerReplacementCoordinator(fixture.Actions(TimeSpan.FromMilliseconds(400)) with
            {
                // A native close endpoint accepts WM_CLOSE immediately, while
                // the native window may survive another message-loop turn.
                // Keep only this fixture's own window alive past the deadline.
                CloseSource = _ => { accepted++; return true; }
            });
            await coordinator.TransferForChecksAsync(fixture.Current);
            Assert(accepted == 1 && fixture.LastReceipt is { Ready: true } && fixture.ClaimsCleaned,
                "an exact ready destination reaches the close endpoint and releases the source claim");
            Assert(CoordinatorMap(coordinator, "_destinations").Count == 1 && fixture.SourceExists,
                "successful handoff retains identity while accepted native closure is pending");
            var identity = fixture.LastInvocation!.DestinationId;
            fixture.Current = fixture.Current with { Generation = fixture.Current.Generation + 1 };
            await coordinator.TransferForChecksAsync(fixture.Current);
            Assert(accepted == 2 && fixture.LastInvocation?.DestinationId == identity && fixture.DestinationCreations == 1,
                "a final source generation before native destruction reuses its ready destination");
            await Task.Delay(1150);
            Assert(CoordinatorMap(coordinator, "_destinations").Count == 0 && fixture.SourceExists,
                "accepted handoff retires within bounded close grace without an expired intent deadline");
            Assert(fixture.LastInvocation?.Source is { } source && source.Window == fixture.Current.RootHwnd.ToInt64()
                && source.Process == fixture.Current.SourceProcessId
                && source.Started == fixture.Current.SourceProcessStartUtc.ToFileTimeUtc(),
                "foreground intent carries the exact native source lifetime");
        });
    }

    private static IDictionary CoordinatorMap(ExplorerReplacementCoordinator coordinator, string name)
        => (IDictionary)typeof(ExplorerReplacementCoordinator).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)!;
}
