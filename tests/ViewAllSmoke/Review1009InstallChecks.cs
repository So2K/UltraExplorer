using System.IO;
using System.Text.RegularExpressions;
using UltraExplorer.Services;
using UltraExplorer.Services.Updates;

namespace ViewAllSmoke;

/// <summary>Review 2026-10-09, install and quiet-update findings. Owned temporary
/// cache folders and source text only: no installer, registry or real update.</summary>
internal static partial class Program
{
    private static async Task Review1009InstallChecks()
    {
        Section("review 2026-10-09: install, quiet update and release pipeline");

        // H3: an all-users copy never registers per-user folder verbs.
        string?[] roots = [@"C:\Program Files", @"C:\Program Files (x86)", null];
        Check("H3: a Program Files copy is an all-users installation",
            ShellReplacementRegistration.IsPerMachineLocation(@"C:\Program Files\UltraExplorer\UltraExplorer.exe", roots, null));
        Check("H3: the folder an all-users Setup recorded counts, whatever its location",
            ShellReplacementRegistration.IsPerMachineLocation(@"D:\Apps\UltraExplorer\UltraExplorer.exe", roots, @"D:\Apps\UltraExplorer\"));
        Check("H3: a per-user copy may register folder verbs",
            !ShellReplacementRegistration.IsPerMachineLocation(@"C:\Users\u\AppData\Local\Programs\UltraExplorer\UltraExplorer.exe", roots, null));
        Check("H3: a sibling folder sharing the Program Files prefix is not inside it",
            !ShellReplacementRegistration.IsPerMachineLocation(@"C:\Program Files Portable\UltraExplorer\UltraExplorer.exe", roots, null));
        Check("H3: a relative or empty path is never classified",
            !ShellReplacementRegistration.IsPerMachineLocation(@"UltraExplorer.exe", roots, @"D:\Apps\UltraExplorer")
            && !ShellReplacementRegistration.IsPerMachineLocation("", roots, null));

        var clock = new QuietClock();

        // L2: a last attempt recorded in the future must not block checks until then.
        using (var future = new QuietFixture())
        {
            new QuietUpdateStore(future.Path).RecordAttempt(clock.GetUtcNow() + TimeSpan.FromDays(400));
            var handler = new QuietHandler(_ => QuietJson("[]"));
            using var service = new QuietUpdateService(future.Path, "1.3.0", handler, clock);
            await service.CheckOnceAsync();
            Check("L2: a last attempt far in the future is treated as due now",
                handler.Requests.Count == 1 && new QuietUpdateStore(future.Path).LastAttemptUtc == clock.GetUtcNow());
        }
        using (var skew = new QuietFixture())
        {
            new QuietUpdateStore(skew.Path).RecordAttempt(clock.GetUtcNow() + TimeSpan.FromMinutes(2));
            var handler = new QuietHandler(_ => QuietJson("[]"));
            using var service = new QuietUpdateService(skew.Path, "1.3.0", handler, clock);
            await service.CheckOnceAsync();
            Check("L2: a few minutes of clock skew still counts as a recent attempt", handler.Requests.Count == 0);
        }

        // L4: the package of the version now installed is not kept until the next release.
        using (var spent = new QuietFixture())
        {
            Directory.CreateDirectory(spent.Path);
            var data = QuietZip();
            var hash = QuietHash(data);
            var store = new QuietUpdateStore(spent.Path);
            File.WriteAllBytes(store.PackagePath(hash), data);
            store.SetReady(new QuietUpdateStore.Package("1.3.1", hash, data.Length));
            store.RecordAttempt(clock.GetUtcNow());
            var notes = Path.Combine(spent.Path, "notes.txt");
            File.WriteAllText(notes, "unrelated");
            var handler = new QuietHandler(_ => QuietJson("[]"));
            using var service = new QuietUpdateService(spent.Path, "1.3.1", handler, clock);
            await service.CheckOnceAsync();
            Check("L4: the offer of the installed version is cleared", store.Ready is null && !service.HasUpdate);
            Check("L4: the installed version's package is deleted, unrelated files stay",
                !File.Exists(store.PackagePath(hash)) && File.ReadAllText(notes) == "unrelated" && handler.Requests.Count == 0);
        }
        using (var newer = new QuietFixture())
        {
            Directory.CreateDirectory(newer.Path);
            var data = QuietZip();
            var hash = QuietHash(data);
            var store = new QuietUpdateStore(newer.Path);
            File.WriteAllBytes(store.PackagePath(hash), data);
            store.SetReady(new QuietUpdateStore.Package("1.3.2", hash, data.Length));
            store.RecordAttempt(clock.GetUtcNow());
            using var service = new QuietUpdateService(newer.Path, "1.3.1", new QuietHandler(_ => QuietJson("[]")), clock);
            await service.CheckOnceAsync();
            Check("L4: a still newer prepared package is kept and offered", store.Ready is not null && File.Exists(store.PackagePath(hash)) && service.HasUpdate);
        }

        // M15: the startup report the confirmed-update helper waits for.
        using (var started = new QuietFixture())
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var service = new QuietUpdateService(started.Path, "1.4.0+abc", new QuietHandler(_ => QuietJson("[]")), clock,
                (_, token) => { reached.TrySetResult(); return Task.Delay(Timeout.Infinite, token); });
            service.Start();
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var marker = new QuietUpdateStore(started.Path).LastStartup;
            Check("M15: a started main window reports its version and process id",
                marker is not null && marker.Version == "1.4.0+abc" && marker.ProcessId == Environment.ProcessId);
        }

        var root = PackagingRoot();
        var workflow = File.ReadAllLines(Path.Combine(root, ".github", "workflows", "release.yml"));
        var uses = workflow.Where(line => Regex.IsMatch(line, @"^\s*(-\s*)?uses:")).ToArray();
        Check("M17: every workflow action is pinned to a full commit SHA with its tag noted",
            uses.Length > 0 && uses.All(line => Regex.IsMatch(line, @"uses:\s*[\w.-]+/[\w./-]+@[0-9a-f]{40}\s+#\s*v\d")));

        var setup = File.ReadAllText(Path.Combine(root, "installer", "UltraExplorer.iss"));
        Check("M16: Setup recovers live integration before replacing files",
            Regex.IsMatch(setup, @"function PrepareToInstall[\s\S]*?Result := RecoverIntegration\(\);")
            && setup.Contains("'--dialog-recover'", StringComparison.Ordinal));
        Check("M16: Setup restores the preference and agents after installing and on cancel",
            Regex.IsMatch(setup, @"ssPostInstall[\s\S]*?RestoreIntegration\(True\);")
            && Regex.IsMatch(setup, @"procedure DeinitializeSetup\(\);\s*begin\s*RestoreIntegration\(False\);")
            && setup.Contains("'--dialog-agent'", StringComparison.Ordinal) && setup.Contains("'--shortcut-agent'", StringComparison.Ordinal));
        Check("H3: the uninstaller sweeps every loaded user hive for this copy's folder verbs",
            Regex.IsMatch(setup, @"usPostUninstall[\s\S]*?ReleaseAllFolderCommands\(\);")
            && setup.Contains("RegGetSubkeyNames(HKU, '', Users)", StringComparison.Ordinal));

        var install = File.ReadAllText(Path.Combine(root, "scripts", "install.ps1"));
        var finallyBlock = install[install.IndexOf("} finally {", StringComparison.Ordinal)..];
        Check("L14a: every install.ps1 restore step runs in its own guarded step",
            Regex.Matches(finallyBlock, @"Invoke-RestoreStep '").Count == 6
            && install.Contains("cleanup step(s) failed", StringComparison.Ordinal));
    }
}
