using System.Diagnostics;
using System.IO;
using System.Reflection;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// What the second review of the canvas's view model found, and what was done
/// about it: opening a file asks the disk and the Shell off the interface
/// thread.
/// </summary>
internal static partial class Program
{
    private static async Task ViewAllVmRound2Checks()
    {
        var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "UltraExplorerVmRound2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await OnDispatcher(() => VmRound2OpenFileAsync(root));
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- J035: opening a file -----------------------------------------------------------

    private static async Task VmRound2OpenFileAsync(string root)
    {
        Section("view model round 2: opening a file on a share that does not answer");
        var folder = Path.Combine(root, "open");
        var scratch = Path.Combine(root, "open-state");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(scratch);

        using var icons = new ShellIconService();
        using var tree = NewTree(scratch, icons);
        tree.PreferLightReveal = true;
        tree.IsCanvasShown = false;
        await tree.InitializeAsync(folder);
        var messages = new List<string>();
        tree.MessageRequested += (message, _) => messages.Add(message);

        static ViewAllNodeViewModel FileNode(string path) =>
            new(new ViewAllEntryDescriptor(path, Path.GetFileName(path), ViewAllEntryKind.File, false, false, null, DateTime.UtcNow), 0);

        // An address nothing answers at, from the range kept for examples: the
        // first question about it waits out the network's whole timeout, about
        // twenty seconds.  A fresh one each run, as an answer is remembered.
        var host = $"203.0.113.{Random.Shared.Next(1, 255)}";
        var asleep = FileNode($@"\\{host}\ultraexplorer-unreachable\report.docx");
        var watch = Stopwatch.StartNew();
        tree.OpenInDefaultApplication(asleep);
        watch.Stop();
        Report($"opening a file on a share that does not answer ({host}) holds the interface thread", watch.ElapsedMilliseconds, 250);

        // A file that is not there - nothing to start, and nothing shown but
        // the message - still says it could not be opened.
        var missing = FileNode(Path.Combine(folder, "missing.ultraexplorer-round2"));
        tree.OpenInDefaultApplication(missing);
        await WaitUntil(() => messages.Any(message => message.Contains("missing.ultraexplorer-round2", StringComparison.Ordinal)), 10_000);
        Check("a file that cannot be opened still says so",
            messages.Any(message => message.StartsWith("Could not open missing.ultraexplorer-round2", StringComparison.Ordinal)));
    }
}
