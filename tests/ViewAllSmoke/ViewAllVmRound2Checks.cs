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
/// thread; and colouring or clearing a large selection on the tree canvas
/// rebuilds its batched layers once, not once an item.
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
            await OnDispatcher(() => VmRound2ColourBatchAsync(root));
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

    // ---- J064: colouring a large selection on the tree canvas ----------------------------

    private static async Task VmRound2ColourBatchAsync(string root)
    {
        Section("view model round 2: colouring a large selection on the tree canvas");
        const int count = 2_000;
        var folder = Path.Combine(root, "colours");
        var scratch = Path.Combine(root, "colours-state");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(scratch);
        for (var index = 0; index < count; index++)
        {
            File.WriteAllText(Path.Combine(folder, $"item-{index:D4}.txt"), "x");
        }

        using var icons = new ShellIconService();
        using var tree = NewTree(scratch, icons);
        await tree.InitializeAsync(folder);
        if (!tree.TryGetNode(folder, out var node))
        {
            Check("the folder is on the tree", false);
            return;
        }

        await tree.ExpandAsync(node);
        var extent = tree.GetContentExtent();
        const double zoom = 0.05;
        tree.UpdateViewport(extent.Location, extent.Size, zoom);
        await Settle(tree);

        // The window's two batched layers, rebuilt on every invalidation the
        // way the window rebuilds them.
        var overview = new ViewAllOverviewLayer { Index = tree.SpatialIndex };
        var harness = new ViewAllHarnessLayer { Index = tree.SpatialIndex };
        var invalidations = 0;
        tree.GraphInvalidated += () =>
        {
            invalidations++;
            harness.InvalidateGeometry();
            harness.Update(extent.Location, extent.Size, zoom);
            overview.InvalidateGeometry();
            overview.Update(extent.Location, extent.Size, zoom, tree.DetailLevel);
        };

        var paths = node.Children.Select(child => child.FullPath).ToArray();
        var watch = Stopwatch.StartNew();
        tree.ApplyAccent(paths, "#EF5A68");
        watch.Stop();
        Check($"colouring {paths.Length:N0} items rebuilds the batched layers once ({invalidations:N0} times)", invalidations == 1);
        Check("and every one of them wears the colour", paths.Length == count && node.Children.All(child => child.AccentHex == "#EF5A68"));
        Check("and the layers drew them", overview.DrawnNodeCount > 0);
        Report($"colouring {paths.Length:N0} items on the tree canvas", watch.ElapsedMilliseconds, 1_000);

        invalidations = 0;
        watch.Restart();
        tree.ApplyAccent(paths, null);
        watch.Stop();
        Check($"clearing them rebuilds the layers once too ({invalidations:N0} times)", invalidations == 1);
        Check("and every one of them loses it", node.Children.All(child => string.IsNullOrEmpty(child.AccentHex)));
        Report($"clearing the colour of {paths.Length:N0} items on the tree canvas", watch.ElapsedMilliseconds, 1_000);

        invalidations = 0;
        tree.ApplyAccent([paths[0]], "#3FA34D");
        Check("one item coloured rebuilds the layers at once, as ever",
            invalidations == 1 && node.Children.First(child => child.FullPath == paths[0]).AccentHex == "#3FA34D");
    }
}
