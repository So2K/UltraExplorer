using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task BatchCommandLineChecks()
    {
        Section("program drops: cmd data transport and bounded shortcut metadata");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerBatchArguments", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var script = Path.Combine(root, "capture & percent%.cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\nsetlocal DisableDelayedExpansion\r\necho ONE=[\"%~1\"]\r\necho TWO=[\"%~2\"]\r\necho THREE=[\"%~3\"]\r\necho FOUR=[\"%~4\"]\r\n");
        var arguments = new[]
        {
            @"C:\owned\a&ver", @"C:\owned\100%UE_BATCH_TEST_EXPAND%!.txt",
            @"C:\owned\caret^(parenthesis);.txt", @"C:\owned\two words\"
        };
        var original = NativeShellService.ProgramMetadataReaderForChecks;
        var originalLauncher = NativeShellService.ProgramDropLauncherForChecks;
        try
        {
            var start = NativeShellService.CreateBatchDropStartInfo(script, arguments, root);
            Check("batch launches use the system cmd with no ShellExecute or raw file names in its command text",
                !start.UseShellExecute && start.FileName == Path.Combine(Environment.SystemDirectory, "cmd.exe")
                && start.Arguments.StartsWith("/d /v:off /s /c ", StringComparison.Ordinal)
                && arguments.All(path => !start.Arguments.Contains(path, StringComparison.Ordinal)));
            start.Environment["UE_BATCH_TEST_EXPAND"] = "MUST_NOT_EXPAND";
            var output = await BatchCaptureAsync(start);
            Check("ampersand does not split the dropped file into an extra command", output.Contains("ONE=[\"" + arguments[0] + "\"]", StringComparison.Ordinal)
                && !output.Contains("Microsoft Windows", StringComparison.Ordinal));
            Check("percent and exclamation remain literal, not environment or delayed expansion", output.Contains("TWO=[\"" + arguments[1] + "\"]", StringComparison.Ordinal)
                && !output.Contains("MUST_NOT_EXPAND", StringComparison.Ordinal));
            Check("caret, parentheses and semicolon remain data", output.Contains("THREE=[\"" + arguments[2] + "\"]", StringComparison.Ordinal));
            Check("spaces and a final backslash round-trip through cmd rather than CRT escaping", output.Contains("FOUR=[\"" + arguments[3] + "\"]", StringComparison.Ordinal));

            foreach (var invalid in new[] { "", "a\"&ver", "a\r\nb", "a\0b" })
            {
                var rejected = false;
                try { NativeShellService.CreateBatchDropStartInfo(script, [invalid]); }
                catch (ArgumentException) { rejected = true; }
                Check("an unrepresentable batch file argument is rejected before launch", rejected);
            }
            var executable = NativeShellService.CreateProgramDropStartInfo(new(@"C:\owned\editor.EXE", "--mode plain", root), arguments);
            Check("ordinary EXE arguments preserve the existing CRT builder and configured prefix", executable.UseShellExecute
                && executable.Arguments == "--mode plain " + NativeShellService.BuildCommandLine(arguments) && executable.WorkingDirectory == root);
            Check("the generic CRT builder remains unchanged for IPC/executables", NativeShellService.BuildCommandLine([@"C:\owned\a&ver"]) == @"C:\owned\a&ver");

            var actualLink = Path.Combine(root, "actual-batch.lnk");
            RunOnSta("owned batch shortcut metadata", () =>
            {
                object? shortcutShell = null;
                object? shortcut = null;
                try
                {
                    shortcutShell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
                    dynamic source = shortcutShell!;
                    shortcut = source.CreateShortcut(actualLink);
                    dynamic linkData = shortcut;
                    linkData.TargetPath = script;
                    linkData.WorkingDirectory = root;
                    linkData.Save();
                }
                finally
                {
                    if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                    if (shortcutShell is not null && Marshal.IsComObject(shortcutShell)) Marshal.FinalReleaseComObject(shortcutShell);
                }
                return Task.CompletedTask;
            });
            NativeShellService.ProgramMetadataReaderForChecks = null;
            var fromActualLink = await NativeShellService.CreateProgramDropStartInfoAsync(actualLink, arguments);
            Check("the real Windows shortcut reader retains the owned batch working directory", fromActualLink.WorkingDirectory == root);
            Check("an actual batch .lnk carries literal dropped arguments into the owned script", (await BatchCaptureAsync(fromActualLink)).Contains("ONE=[\"" + arguments[0] + "\"]", StringComparison.Ordinal));

            foreach (var host in new[] { "cmd.exe", "powershell.exe", "pwsh.exe", "mshta.exe", "bash.exe", "sh.exe" })
            {
                var rejected = false;
                try { NativeShellService.CreateProgramDropStartInfo(new(Path.Combine(root, host), "", root), arguments); }
                catch (IOException) { rejected = true; }
                Check("a direct command-text host cannot interpret dropped names as code: " + host, rejected);
            }
            var link = Path.Combine(root, "batch.lnk");
            NativeShellService.ProgramMetadataReaderForChecks = (_, _) => new(script, "", root);
            var fromLink = await NativeShellService.CreateProgramDropStartInfoAsync(link, arguments);
            fromLink.Environment["UE_BATCH_TEST_EXPAND"] = "MUST_NOT_EXPAND";
            Check("a batch shortcut without a preset prefix uses the same safe transport", (await BatchCaptureAsync(fromLink)).Contains("ONE=[\"" + arguments[0] + "\"]", StringComparison.Ordinal));
            NativeShellService.ProgramMetadataReaderForChecks = (_, _) => new(script, "--preset", root);
            Check("a batch shortcut with an unverified preset fails explicitly", await BatchRejectedAsync(link, arguments));
            NativeShellService.ProgramMetadataReaderForChecks = (_, _) => new(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c preset", root);
            Check("a command-prefix shortcut fails before shell launch", await BatchRejectedAsync(link, arguments));
            NativeShellService.ProgramMetadataReaderForChecks = (_, _) => new(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "", "");
            Check("a custom cmd-mediated script association is not silently treated as an EXE", await BatchRejectedAsync(Path.Combine(root, "script.custom"), arguments));
            NativeShellService.ProgramMetadataReaderForChecks = (_, _) => new(Path.Combine(Environment.SystemDirectory, "wscript.exe"), "", "");
            Check("ordinary WSH script associations retain ShellExecute behavior", (await NativeShellService.CreateProgramDropStartInfoAsync(Path.Combine(root, "script.vbs"), arguments)).UseShellExecute);
            NativeShellService.ProgramMetadataReaderForChecks = (_, _) => new(Path.Combine(Environment.SystemDirectory, "powershell.exe"), "powershell.exe -File \"%1\" %*", "");
            Check("a literal PowerShell -File association keeps ordinary Shell opening", (await NativeShellService.CreateProgramDropStartInfoAsync(Path.Combine(root, "script.ps1"), arguments)).UseShellExecute);
            NativeShellService.ProgramMetadataReaderForChecks = (_, _) => new(Path.Combine(Environment.SystemDirectory, "powershell.exe"), "powershell.exe -Command \"%1\" %*", "");
            Check("a PowerShell command-text association cannot interpret dropped names as code", await BatchRejectedAsync(Path.Combine(root, "script.code"), arguments));
            foreach (var codeFlag in new[] { "-ec", "-cwa", "-enc", "-CommandWithArgs" })
            {
                NativeShellService.ProgramMetadataReaderForChecks = (_, _) => new(Path.Combine(Environment.SystemDirectory, "pwsh.exe"), $"pwsh.exe -File \"%1\" {codeFlag} code %*", "");
                Check("a mixed File/command alias association is rejected by the full grammar: " + codeFlag,
                    await BatchRejectedAsync(Path.Combine(root, "mixed-" + codeFlag + ".ps1"), arguments));
            }
            NativeShellService.ProgramMetadataReaderForChecks = (_, shortcut) => shortcut
                ? new(Path.Combine(root, "script.custom"), "", root)
                : new(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c %1 %*", "");
            Check("a shortcut to a script cannot bypass its association's command-host guard", await BatchRejectedAsync(Path.Combine(root, "associated.lnk"), arguments));

            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var reads = 0;
            NativeShellService.ProgramMetadataReaderForChecks = (_, _) =>
            {
                Interlocked.Increment(ref reads);
                held.Set();
                release.Wait(TimeSpan.FromSeconds(8));
                return new(script, "", root);
            };
            var slow = NativeShellService.CreateProgramDropStartInfoAsync(Path.Combine(root, "slow.lnk"), arguments);
            Check("shortcut metadata runs on a bounded background worker", await Task.Run(() => held.Wait(TimeSpan.FromSeconds(3))));
            var repeat = NativeShellService.CreateProgramDropStartInfoAsync(Path.Combine(root, "slow.lnk"), arguments);
            var another = NativeShellService.CreateProgramDropStartInfoAsync(Path.Combine(root, "second.lnk"), arguments);
            var capRejected = await BatchRejectedAsync(Path.Combine(root, "third.lnk"), arguments);
            Check("repeated metadata is single-flight and a third path does not create another worker", capRejected && reads <= 2);
            var timed = false;
            try { await slow; } catch (TimeoutException) { timed = true; }
            Check("a blocked metadata request returns at its caller deadline instead of waiting for the OS", timed);
            release.Set();
            try { await repeat; } catch (TimeoutException) { }
            try { await another; } catch (TimeoutException) { }
            await WaitUntil(() => NativeShellService.ProgramMetadataPendingForChecks == 0, 3_000);
            NativeShellService.ProgramMetadataReaderForChecks = (_, _) => new(script, "", root);
            var launches = 0;
            NativeShellService.ProgramDropLauncherForChecks = _ => { launches++; return null; };
            await NativeShellService.OpenWithProgramAsync(Path.Combine(root, "closed.lnk"), arguments, () => false);
            Check("a closed caller discards the prepared launch", launches == 0);
            var context = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new BatchContextTrap());
                NativeShellService.OpenWithProgram(Path.Combine(root, "sync.lnk"), arguments);
                Check("the synchronous compatibility API does not post its metadata continuation to a blocked caller", launches == 1);
            }
            finally { SynchronizationContext.SetSynchronizationContext(context); }
            RunOnSta("invalid copy source outcome", () => BatchMissingSourcesOnStaAsync(root));
        }
        finally
        {
            NativeShellService.ProgramMetadataReaderForChecks = original;
            NativeShellService.ProgramDropLauncherForChecks = originalLauncher;
            TryDelete(root);
        }
    }

    private sealed class BatchContextTrap : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
            => throw new InvalidOperationException("An async metadata continuation tried to return to the blocked caller.");
    }

    private static async Task BatchMissingSourcesOnStaAsync(string root)
    {
        Section("file drops: invalid requested sources never report copied success");
        var folder = Path.Combine(root, "sources");
        Directory.CreateDirectory(folder);
        var present = Path.Combine(folder, "present.txt");
        await File.WriteAllTextAsync(present, "keep this source");
        var missing = Path.Combine(folder, "missing.txt");
        var inaccessible = Path.Combine(folder, "unreadable.txt");
        var destination = Path.Combine(root, "never-created-destination");
        var shell = new NativeShellService();
        var originalExists = NativeShellService.OperationItemExists;
        var uiThread = Environment.CurrentManagedThreadId;
        var probes = 0;
        var onUi = false;
        Exception? error = null;
        Task<bool> Transfer(string[] sources, string target, bool move) => TransferCore(sources, target, move);
        async Task<bool> TransferCore(string[] sources, string target, bool move)
        {
            try { await shell.CopyOrMoveAsync(sources, target, move); return true; }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            { error = failure; return false; }
        }
        async Task<bool> Drop(string[] sources, string target, bool move)
        {
            Task<bool>? transfer = null;
            var accepted = ExternalFileDrop.Complete(Dispatcher.CurrentDispatcher,
                () => Transfer(sources, target, move), pending => transfer = pending);
            // Drop releases the source as soon as the actual Shell task ends,
            // before the caller's error/refresh continuation. Observe that
            // retained transfer too before inspecting its error in this fixture.
            if (transfer is not null) await transfer;
            return accepted;
        }
        try
        {
            NativeShellService.OperationItemExists = path =>
            {
                Interlocked.Increment(ref probes);
                if (Environment.CurrentManagedThreadId == uiThread) onUi = true;
                if (path == inaccessible) throw new UnauthorizedAccessException("Owned unavailable-source fixture");
                return File.Exists(path) || Directory.Exists(path);
            };
            var accepted = await Drop([missing], destination, false);
            Check("a disappeared sole source returns failed Drop without creating its target", !accepted
                && error is FileNotFoundException && !Directory.Exists(destination));
            error = null;
            accepted = await Drop([present, missing], destination, false);
            Check("a mixed existing/missing batch fails before copying any existing source", !accepted
                && error is FileNotFoundException && !Directory.Exists(destination) && File.ReadAllText(present) == "keep this source");
            error = null;
            accepted = await Drop([present, inaccessible], destination, true);
            Check("an inaccessible source cannot become successful Move completion", !accepted
                && error is UnauthorizedAccessException && !Directory.Exists(destination) && File.Exists(present));
            error = null;
            var before = probes;
            accepted = await Drop([present], folder, true);
            Check("a same-target move remains an intentional validated no-op", accepted && error is null
                && probes > before && File.ReadAllText(present) == "keep this source");
            error = null;
            accepted = await Drop([missing], folder, true);
            Check("a missing same-target move is not mistaken for that intentional no-op", !accepted && error is FileNotFoundException);
            Check("requested-source availability is checked off the caller's dispatcher", probes >= 7 && !onUi);
        }
        finally { NativeShellService.OperationItemExists = originalExists; }
    }

    private static async Task<bool> BatchRejectedAsync(string path, string[] arguments)
    {
        try { await NativeShellService.CreateProgramDropStartInfoAsync(path, arguments); return false; }
        catch (IOException) { return true; }
    }

    private static async Task<string> BatchCaptureAsync(ProcessStartInfo start)
    {
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var text = await output;
        if (process.ExitCode != 0) throw new IOException(await error);
        return text;
    }
}
