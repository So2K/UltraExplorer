using System.Diagnostics;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// Owned primitive checks of menu preflight, not Windows app automation. No
/// MainWindow is shown, no real UNC is probed, and no Shell extension is run.
/// The production gate/wait/timer seams take controlled tasks and a dispatcher.
/// Native COM building remains the unchanged caller after that gate succeeds;
/// these checks do not claim a timeout for the COM build itself.
/// </summary>
internal static partial class Program
{
    private static Task ShellMenuReview2IntegrationChecks()
    {
        RunOnSta("shell menu review2 integration", ShellMenuReview2IntegrationOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task ShellMenuReview2IntegrationOnStaAsync()
    {
        Section("shell menu review2: bounded share preflight and canceled preparation");
        var dispatcher = Dispatcher.CurrentDispatcher;
        Check("the release preflight budget is exactly 1.5 seconds", MainWindow.ShellSharePreflightBudget == TimeSpan.FromMilliseconds(1500));
        Check("zero and over-100 preparations do no Shell work; 100 is allowed",
            !MainWindow.ShellMenuCountAllowed(0, true) && MainWindow.ShellMenuCountAllowed(100, true)
            && !MainWindow.ShellMenuCountAllowed(101, true));
        Check("native menus cap at 2000 items, including a request built on release",
            MainWindow.ShellMenuCountAllowed(2000, false) && !MainWindow.ShellMenuCountAllowed(2001, false)
            && !MainWindow.ShellMenuCountAllowed(int.MaxValue, false));

        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = 0;
        var gate = new MainWindow.ShellSharePreflight(_ => { probes++; return pending.Task; });
        var firstKey = VolumeKinds.ShareKey(@"\\owned-menu-fixture\share\first");
        var otherKey = VolumeKinds.ShareKey(@"\\OWNED-MENU-FIXTURE\SHARE\second");
        Check("different paths/casing of one share produce one preflight key", string.Equals(firstKey, otherKey, StringComparison.OrdinalIgnoreCase));
        var first = gate.Ask(firstKey!);
        var repeated = gate.Ask(otherKey!);
        Check("a still pending share is single-flight and its repeat cannot wait again",
            first.NewlyStarted && !repeated.NewlyStarted && ReferenceEquals(first.Answer, repeated.Answer) && probes == 1);
        var resolveLetter = VolumeKinds.ResolveLetter;
        try
        {
            VolumeKinds.ResolveLetter = letter => letter == 'Q' ? firstKey : null;
            var mapped = gate.Ask(VolumeKinds.ShareKey(@"Q:\owned-child")!);
            Check("a mapped letter and the UNC spelling share the same unfinished physical question",
                !mapped.NewlyStarted && ReferenceEquals(first.Answer, mapped.Answer) && probes == 1);
        }
        finally { VolumeKinds.ResolveLetter = resolveLetter; }
        var dispatched = false;
        _ = dispatcher.BeginInvoke(DispatcherPriority.Background, () => dispatched = true);
        Check("the repeated pending request falls back without pumping another nested frame",
            !MainWindow.WaitForShellShare(dispatcher, repeated, MainWindow.ShellSharePreflightBudget, () => true) && !dispatched);
        await Dispatcher.Yield(DispatcherPriority.Background);
        Check("the unrelated queued dispatcher callback remains runnable", dispatched);

        var heartbeat = false;
        var pulse = new DispatcherTimer(DispatcherPriority.Input, dispatcher) { Interval = TimeSpan.FromMilliseconds(5) };
        pulse.Tick += (_, _) => { heartbeat = true; pulse.Stop(); };
        pulse.Start();
        var waiting = Stopwatch.StartNew();
        try
        {
            Check("a new unanswered probe returns fallback at its own deadline while UI callbacks keep running",
                !MainWindow.WaitForShellShare(dispatcher, first, TimeSpan.FromMilliseconds(45), () => true)
                && heartbeat && waiting.Elapsed >= TimeSpan.FromMilliseconds(45));
        }
        finally { pulse.Stop(); }
        Check("timing out leaves the same one physical probe owned until it actually finishes",
            gate.PendingCount == 1 && probes == 1 && !gate.Ask(firstKey!).NewlyStarted);
        pending.SetResult(true);
        Check("a later positive answer is usable on the original dispatcher without another probe",
            MainWindow.WaitForShellShare(dispatcher, gate.Ask(firstKey!), TimeSpan.Zero, () => true)
            && dispatcher.CheckAccess() && probes == 1);
        var nativeContinuationThread = 0;
        if (MainWindow.WaitForShellShare(dispatcher, gate.Ask(firstKey!), TimeSpan.Zero, () => true))
            nativeContinuationThread = Environment.CurrentManagedThreadId;
        Check("the positive preflight keeps the native-builder continuation on the calling COM apartment",
            nativeContinuationThread == Environment.CurrentManagedThreadId);

        var stalled = new Dictionary<string, TaskCompletionSource<bool>>(StringComparer.OrdinalIgnoreCase);
        var starts = 0;
        var bounded = new MainWindow.ShellSharePreflight(key =>
        {
            starts++;
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            stalled.Add(key, answer);
            return answer.Task;
        }, maximumPending: 2, maximumKeys: 4);
        bounded.Ask("owned-a");
        bounded.Ask("owned-b");
        for (var index = 0; index < 20; index++)
        {
            var rejected = bounded.Ask("owned-other-" + index);
            Check("capacity saturation starts no extra physical probe " + index,
                !rejected.NewlyStarted && rejected.Answer.IsCompletedSuccessfully && !rejected.Answer.Result);
        }
        Check("two stalled shares cap both outstanding work and retained pending keys", starts == 2 && bounded.PendingCount == 2 && bounded.KeyCount == 2);
        stalled["owned-a"].SetResult(false);
        Check("a freed physical slot allows another share to be checked", bounded.Ask("owned-c").NewlyStarted && starts == 3 && bounded.PendingCount == 2);
        foreach (var answer in stalled.Values) answer.TrySetResult(false);

        long now = 0;
        var completedStarts = 0;
        var cached = new MainWindow.ShellSharePreflight(_ => { completedStarts++; return Task.FromResult(true); }, maximumPending: 1, maximumKeys: 3, now: () => now);
        cached.Ask("positive");
        Check("a completed positive stays cached without repeating reachability work", !cached.Ask("POSITIVE").NewlyStarted && completedStarts == 1);
        now = 10_001;
        Check("a completed cached answer can be retried after expiry", cached.Ask("positive").NewlyStarted && completedStarts == 2);
        for (var index = 0; index < 20; index++) cached.Ask("complete-" + index);
        Check("completed-question eviction bounds the key cache", cached.KeyCount == 3 && cached.PendingCount == 0);

        var protectedPending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var protectedGate = new MainWindow.ShellSharePreflight(key => key == "protected" ? protectedPending.Task : Task.FromResult(true), maximumPending: 2, maximumKeys: 2);
        var protectedQuestion = protectedGate.Ask("protected");
        for (var index = 0; index < 20; index++) protectedGate.Ask("answer-" + index);
        Check("cache eviction never discards an unfinished question and starts it again",
            protectedGate.KeyCount == 2 && protectedGate.PendingCount == 1
            && !protectedGate.Ask("protected").NewlyStarted && ReferenceEquals(protectedQuestion.Answer, protectedGate.Ask("protected").Answer));
        protectedPending.SetResult(false);

        var generation = 1;
        var abandonedAnswer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandoned = new MainWindow.ShellSharePreflight(_ => abandonedAnswer.Task).Ask("owned-stale");
        var invalidate = new DispatcherTimer(DispatcherPriority.Input, dispatcher) { Interval = TimeSpan.FromMilliseconds(5) };
        invalidate.Tick += (_, _) => { generation++; invalidate.Stop(); };
        invalidate.Start();
        try
        {
            Check("a superseded/closed request stops waiting even before the physical query answers",
                !MainWindow.WaitForShellShare(dispatcher, abandoned, MainWindow.ShellSharePreflightBudget, () => generation == 1));
        }
        finally { invalidate.Stop(); }
        abandonedAnswer.SetResult(true);
        Check("a late success cannot revive an abandoned or closed request",
            !MainWindow.WaitForShellShare(dispatcher, abandoned, TimeSpan.Zero, () => false));

        var outerAnswer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var innerAnswer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outerQuestion = new MainWindow.ShellSharePreflight(_ => outerAnswer.Task).Ask("owned-outer");
        var innerQuestion = new MainWindow.ShellSharePreflight(_ => innerAnswer.Task).Ask("owned-inner");
        var innerFellBack = false;
        var nestedPulse = new DispatcherTimer(DispatcherPriority.Input, dispatcher) { Interval = TimeSpan.FromMilliseconds(5) };
        nestedPulse.Tick += (_, _) =>
        {
            nestedPulse.Stop();
            var pumpedAgain = false;
            _ = dispatcher.BeginInvoke(DispatcherPriority.Background, () => pumpedAgain = true);
            innerFellBack = !MainWindow.WaitForShellShare(dispatcher, innerQuestion, MainWindow.ShellSharePreflightBudget, () => true) && !pumpedAgain;
            outerAnswer.TrySetResult(false);
        };
        nestedPulse.Start();
        try
        {
            Check("a reentered other-window request cannot stack a second dispatcher wait",
                !MainWindow.WaitForShellShare(dispatcher, outerQuestion, MainWindow.ShellSharePreflightBudget, () => true) && innerFellBack);
        }
        finally { nestedPulse.Stop(); innerAnswer.TrySetResult(false); }

        var reported = 0;
        var failed = new MainWindow.ShellSharePreflight(_ => throw new InvalidOperationException("owned probe failure"), report: _ => reported++);
        var failedQuestion = failed.Ask("owned-failure");
        Check("an unexpected probe fault is observed/reported and returns fallback rather than escaping a timer",
            reported == 1 && failedQuestion.Answer.IsFaulted
            && !MainWindow.WaitForShellShare(dispatcher, failedQuestion, TimeSpan.Zero, () => true));

        var fired = 0;
        var still = MainWindow.CreateShellMenuStillTimer(dispatcher, () => fired++);
        still.Start();
        still.Stop();
        await Task.Delay(100);
        Check("pan/release/close can cancel the actual preparation timer before it builds anything", fired == 0);
        still.Start();
        await Task.Delay(100);
        Check("an uncanceled 50 ms preparation fires once", fired == 1 && !still.IsEnabled);
        await Task.Delay(80);
        Check("the preparation timer never repeats COM construction", fired == 1);
        still.Stop();
    }
}
