using System.Diagnostics;
using System.IO;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;

namespace ViewAllSmoke;

/// <summary>
/// A benchmark or snapshot run started without a state folder of its own is
/// given one, rather than saving what it forced - the nested canvas, the
/// folder list hidden, names from A everywhere - over the user's layout and
/// orders; started with one, it keeps to it.
/// </summary>
internal static partial class Program
{
    /// <summary>
    /// Run twice over: once as itself, which starts this program again as a
    /// snapshot run and as a benchmark would be started, and once in each of
    /// those copies, which only asks where its state folder is - nothing is
    /// read from it or written to it, wherever it turns out to be.
    /// </summary>
    private static async Task DiagnosticsStateChecks()
    {
        var args = Environment.GetCommandLineArgs();
        var expected = Array.FindIndex(args, argument => argument == "--expect-state") is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        if (args.Any(argument => argument is "--nested-snapshots" or "--nested-bench"))
        {
            // Only compared, never looked into: on a build without the fix
            // this is the user's own folder.
            var users = ViewAllPath.Equals(AppPaths.StateDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer"));
            Check("diagnostics copy: its state folder is not the user's", !users);
            Check("diagnostics copy: its state folder is the one it was given, or a new one of its own",
                !users && (expected is not null
                    ? ViewAllPath.Equals(AppPaths.StateDirectory, expected)
                    : AppPaths.StateDirectory.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                        && !Directory.Exists(AppPaths.StateDirectory)));
            return;
        }

        Section("diagnostics runs: never on the user's own state");
        var given = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "UltraExplorerDiagnosticsState", Guid.NewGuid().ToString("N"));
        foreach (var (run, state) in new[] { ("--nested-snapshots", (string?)null), ("--nested-bench", null), ("--nested-snapshots", given) })
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("--only");
            start.ArgumentList.Add(nameof(DiagnosticsStateChecks));
            start.ArgumentList.Add(run);
            start.ArgumentList.Add(Path.Combine(given, "output"));
            start.Environment.Remove(AppPaths.StateDirectoryVariable);
            if (state is not null)
            {
                start.ArgumentList.Add("--expect-state");
                start.ArgumentList.Add(state);
                start.Environment[AppPaths.StateDirectoryVariable] = state;
            }

            using var copy = Process.Start(start)!;
            var output = await copy.StandardOutput.ReadToEndAsync();
            await copy.WaitForExitAsync();
            var passed = copy.ExitCode == 0 && output.Contains("2/2 checks passed", StringComparison.Ordinal);
            Check($"a run started with {run} {(state is null ? "and no state folder is given one of its own" : "and a state folder keeps to it")}", passed);
            if (!passed)
            {
                Console.WriteLine(output);
            }
        }

        TryDelete(given);
    }
}
