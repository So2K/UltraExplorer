# Isolated dialog/store regression checks

`dotnet run --project tests/DialogStoreStabilityProbe -c Release`

This probe links the current production store/lease predicate source but substitutes
registration facades and forbids every native-window call. It uses a fresh temporary
state directory; `DialogStartup.IsAllowed` must be false. No production files,
user settings, registry, native windows, system input or bench mutex are touched.

Every regression assertion is strict: a lost caller, temporary litter, wrong
guardian identity, escaped recovery error, or out-of-order registration fails
the executable. The first diagnostic version reproduced all four defects before
the production fixes; this version verifies their corrected contracts.

The guardian parser/predicate uses only the current process and one owned exited
child, without constructing a lease. Client checks exercise 12 simultaneous
independent store objects and nine owned writer/clear processes. Abandoned and
timed-out mutex checks use dedicated owned threads with verified unique state.
