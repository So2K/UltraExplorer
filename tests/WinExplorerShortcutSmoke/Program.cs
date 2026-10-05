using System.Diagnostics;
using System.Runtime.InteropServices;
using UltraExplorer.Services;

internal static class Program
{
    private static int _checks;
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Contains("--registration-probe")) return ProbeRegistration();
            CheckPolicy();
            CheckDummyInput();
            await CheckOwnedPumpAsync();
            Console.WriteLine($"PASS: {_checks} checks; no global keyboard hook, input or focus changes.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void CheckPolicy()
    {
        var policy = new WinExplorerShortcutPolicy();
        for (var key = 0; key <= 255; key++)
        {
            if (key == 0x45) continue;
            Assert(policy.Process(key, true, false, WinExplorerShortcutModifiers.Windows) == WinExplorerShortcutDecision.Pass,
                $"unrelated key 0x{key:X2} down passes");
            Assert(policy.Process(key, false, false, WinExplorerShortcutModifiers.Windows) == WinExplorerShortcutDecision.Pass,
                $"unrelated key 0x{key:X2} up passes");
        }
        for (var modifiers = 0; modifiers < 16; modifiers++)
        {
            policy.PrimeE(false);
            var expected = modifiers == 1 ? WinExplorerShortcutDecision.Launch : WinExplorerShortcutDecision.Pass;
            Assert(policy.Process(0x45, true, false, (WinExplorerShortcutModifiers)modifiers) == expected,
                $"only exact Windows modifier claims E ({modifiers})");
            var release = modifiers == 1 ? WinExplorerShortcutDecision.Suppress : WinExplorerShortcutDecision.Pass;
            Assert(policy.Process(0x45, false, false, WinExplorerShortcutModifiers.None) == release,
                "E release pairs with its original decision, even after Win release");
        }
        policy.PrimeE(false);
        Assert(policy.Process(0x45, true, false, WinExplorerShortcutModifiers.Windows) == WinExplorerShortcutDecision.Launch, "first E down launches");
        for (var repeat = 0; repeat < 1000; repeat++)
            Assert(policy.Process(0x45, true, false, WinExplorerShortcutModifiers.Windows) == WinExplorerShortcutDecision.Suppress, "autorepeat never relaunches or reaches Windows");
        Assert(policy.Process(0x5B, false, false, WinExplorerShortcutModifiers.None) == WinExplorerShortcutDecision.Pass, "Win release stays untouched after a claim");
        Assert(policy.Process(0x45, false, false, WinExplorerShortcutModifiers.None) == WinExplorerShortcutDecision.Suppress, "claimed E up suppressed");
        Assert(policy.Process(0x45, true, false, WinExplorerShortcutModifiers.None) == WinExplorerShortcutDecision.Pass, "ordinary typing resumes after release");
        Assert(policy.Process(0x45, true, false, WinExplorerShortcutModifiers.Windows) == WinExplorerShortcutDecision.Pass, "adding Win to already-held E does not steal a repeat");
        policy.PrimeE(true);
        Assert(policy.Process(0x45, true, false, WinExplorerShortcutModifiers.Windows) == WinExplorerShortcutDecision.Pass, "E held before activation stays with Windows");
        policy.Process(0x45, false, false, WinExplorerShortcutModifiers.None);
        Assert(policy.Process(0x45, true, true, WinExplorerShortcutModifiers.Windows) == WinExplorerShortcutDecision.Pass, "injected E is not intercepted");
        Assert(policy.Process(0x45, true, false, WinExplorerShortcutModifiers.Windows) == WinExplorerShortcutDecision.Launch, "injected event did not latch a physical key");
        policy.CancelClaim();
        Assert(policy.Process(0x45, false, false, WinExplorerShortcutModifiers.None) == WinExplorerShortcutDecision.Pass, "failed dispatch leaves the press with Windows");
    }

    private static void CheckDummyInput()
    {
        var input = WinExplorerShortcutService.CreateMenuDismissInput();
        Assert(Marshal.SizeOf<WinExplorerShortcutService.NativeInput>() == (nint.Size == 8 ? 40 : 28), "INPUT matches native union alignment");
        Assert(input.Type == 1 && input.Data.Keyboard.VirtualKey == 0xFF && input.Data.Keyboard.Scan == 0
            && input.Data.Keyboard.Flags == 2 && input.Data.Keyboard.Time == 0 && input.Data.Keyboard.ExtraInfo != 0,
            "menu-dismiss input is one tagged nonexistent virtual-key release, with no text or modifier change");
    }

    private static async Task CheckOwnedPumpAsync()
    {
        using var launched = new ManualResetEventSlim();
        using var releaseLaunch = new ManualResetEventSlim();
        var callbacks = 0;
        using var service = WinExplorerShortcutService.CreateForChecks(() =>
        {
            Interlocked.Increment(ref callbacks);
            launched.Set();
            releaseLaunch.Wait(TimeSpan.FromSeconds(3));
        });
        Assert(!service.IsReady && service.LastError is null, "new service is OFF without error");
        Assert(service.Start() && service.IsReady, "owned Win32 message queue reports ready");
        Assert(service.Start(), "Start is idempotent on the same live service");
        Assert(service.PostLaunchForChecks() && launched.Wait(TimeSpan.FromSeconds(3)), "owned launch message reaches worker");
        Assert(service.PostLaunchForChecks(), "launch work does not block the dedicated message pump");
        var stopwatch = Stopwatch.StartNew();
        service.Dispose();
        Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(1) && !service.IsReady && service.ThreadStoppedForChecks,
            "OFF immediately stops the pump while launch work is held on its worker");
        Assert(!service.PostLaunchForChecks(), "OFF refuses any new launch message");
        releaseLaunch.Set();
        await Task.Delay(50);
        Assert(callbacks == 1, "queued launch cannot run after OFF");
        service.Dispose();
        using var failed = WinExplorerShortcutService.CreateForChecks(() => throw new InvalidOperationException("must not launch"), failStartup: true);
        Assert(!failed.Start() && !failed.IsReady && failed.LastError is not null,
            "startup failure is observable and never enables shortcut suppression");
        await UntilAsync(() => failed.ThreadStoppedForChecks);
        for (var index = 0; index < 30; index++)
        {
            using var rapid = WinExplorerShortcutService.CreateForChecks(() => throw new InvalidOperationException("must not launch"));
            var start = Task.Run(rapid.Start);
            rapid.Dispose();
            try { await start; } catch (ObjectDisposedException) { }
            Assert(!rapid.IsReady && rapid.ThreadStoppedForChecks, "OFF wins a concurrent startup");
        }
        using var throwing = WinExplorerShortcutService.CreateForChecks(() => throw new InvalidOperationException("fixture callback failure"));
        Assert(throwing.Start() && throwing.PostLaunchForChecks(), "failure fixture starts owned pump");
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (throwing.IsReady && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert(!throwing.IsReady && throwing.LastError == "fixture callback failure", "launch failure restores OFF and exposes error");
        await UntilAsync(() => throwing.ThreadStoppedForChecks);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert(condition(), "owned message thread terminates within its deadline");
    }

    private static int ProbeRegistration()
    {
        var result = "";
        var thread = new Thread(() =>
        {
            var registered = RegisterHotKey(0, 0x5545, 0x4008, 0x45);
            var error = registered ? 0 : Marshal.GetLastWin32Error();
            var cleaned = !registered || UnregisterHotKey(0, 0x5545);
            result = $"Windows {Environment.OSVersion.Version}: RegisterHotKey Win+E={registered}; error={error}; cleaned={cleaned}. No key presses.";
        });
        thread.Start();
        thread.Join();
        Console.WriteLine(result);
        return 0;
    }

    private static void Assert(bool condition, string description)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(description);
    }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(nint window, int id);
}
