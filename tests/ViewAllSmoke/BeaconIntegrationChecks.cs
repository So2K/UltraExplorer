using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Media;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task BeaconIntegrationChecks()
    {
        RunOnSta("cached beacon placement", BeaconIntegrationChecksAsync);
        return Task.CompletedTask;
    }

    private static async Task BeaconIntegrationChecksAsync()
    {
        var disk = new FakeDisk();
        disk.Folder(@"Q:\");
        disk.AddFiles(@"Q:\", 800, "beacon-");
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false, PostBackground = null };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        var drive = tree.Find(@"Q:\")!;
        await tree.LoadAsync(drive);

        var canvas = new NestedCanvas
        {
            Tree = tree,
            FramesByHandForTests = true,
            DpiOverride = new DpiScale(1, 1)
        };
        canvas.Measure(new Size(1400, 900));
        canvas.Arrange(new Rect(0, 0, 1400, 900));
        canvas.UpdateLayout();
        canvas.FitAll(animated: false);

        NestedBeacon BeaconOf(NestedFile file, int index) => new(
            drive.PathOf(file),
            index == 0 ? NestedBeaconKind.Active : index == 1 ? NestedBeaconKind.Pinned : NestedBeaconKind.Colour,
            index == 0 ? Colors.DeepSkyBlue : index == 1 ? Colors.Gold : Colors.OrangeRed,
            file.Name);
        var beacons = drive.Files.Take(800).Select(BeaconOf).ToArray();
        canvas.SetBeacons(beacons);
        canvas.RenderAsFrame();
        var initialPasses = canvas.BeaconResolutionPasses;
        var initialPlaced = canvas.BeaconsPlaced;
        var initialCaptions = canvas.BeaconCaptionsForTests.ToArray();
        var hotspots = (ICollection)typeof(NestedCanvas)
            .GetField("_hotspots", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(canvas)!;
        var initialHotspots = hotspots.Count;

        canvas.RenderAsFrame();
        Check("an unchanged decor frame reuses every resolved beacon target",
            initialPasses == 1 && canvas.BeaconResolutionPasses == initialPasses
            && canvas.BeaconsPlaced == initialPlaced && initialPlaced == beacons.Length);
        Check("cached beacon frames preserve caption order and clickable hotspots",
            canvas.BeaconCaptionsForTests.SequenceEqual(initialCaptions)
            && hotspots.Count == initialHotspots && initialHotspots > 0);

        canvas.Pan(new Vector(7, 4));
        canvas.RenderAsFrame();
        Check("camera motion recomputes screen positions but not path-to-tree targets",
            canvas.BeaconResolutionPasses == initialPasses && canvas.BeaconsPlaced > 0);
        Check("the live beacon layout uses a bounded spatial comparison count",
            canvas.LastBeaconSpatialComparisons < beacons.Length * 64);

        canvas.SetBeacons([.. beacons]);
        canvas.RenderAsFrame();
        Check("a new beacon set invalidates the resolved-target cache once",
            canvas.BeaconResolutionPasses == initialPasses + 1 && canvas.BeaconsPlaced == initialPlaced);

        var beforeLayers = canvas.BeaconResolutionPasses;
        canvas.ShownLayers = CanvasLayer.All & ~CanvasLayer.Marks;
        canvas.RenderAsFrame();
        var onlyActive = canvas.BeaconsPlaced;
        canvas.ShownLayers = CanvasLayer.All;
        canvas.RenderAsFrame();
        Check("layer transitions rebuild shown targets without retaining hidden marks",
            canvas.BeaconResolutionPasses == beforeLayers + 2 && onlyActive == 1 && canvas.BeaconsPlaced == initialPlaced);

        var beforeSort = canvas.BeaconResolutionPasses;
        tree.Orders.SetDefault(new ItemSort(SortColumn.Size, true));
        canvas.RenderAsFrame();
        Check("a sort generation refreshes file indices and leaves every beacon on its file",
            canvas.BeaconResolutionPasses == beforeSort + 1 && canvas.BeaconsPlaced == initialPlaced);

        var oldFile = drive.Files[0];
        var oldPath = drive.PathOf(oldFile);
        var renamedName = "renamed-beacon.txt";
        var directory = disk.Folder(@"Q:\");
        directory.Files.RemoveAll(file => string.Equals(file.Name, oldFile.Name, StringComparison.OrdinalIgnoreCase));
        disk.AddFile(@"Q:\", renamedName, oldFile.Length);
        var versionBeforeRename = tree.Version;
        var passesBeforeRename = canvas.BeaconResolutionPasses;
        await tree.RefreshAsync(drive);
        var versionAfterRename = tree.Version;
        canvas.RenderAsFrame();
        var oldAfterRename = canvas.Resolve(oldPath);
        Check($"a refreshed rename cannot leave the removed path at its old tile "
              + $"(tree {versionBeforeRename}->{versionAfterRename}, pass {passesBeforeRename}->{canvas.BeaconResolutionPasses}, "
              + $"placed {canvas.BeaconsPlaced}/{initialPlaced}, old {(oldAfterRename is null ? "gone" : "resolved")})",
            canvas.BeaconResolutionPasses == passesBeforeRename + 1
            && canvas.BeaconsPlaced == initialPlaced - 1
            && oldAfterRename is null);

        var renamedPath = Path.Combine(@"Q:\", renamedName);
        var renamedBeacons = beacons.Select(beacon => ViewAllPath.Equals(beacon.Path, oldPath)
            ? beacon with { Path = renamedPath, Label = renamedName }
            : beacon).ToArray();
        canvas.SetBeacons(renamedBeacons);
        canvas.RenderAsFrame();
        Check("the renamed beacon resolves at its new live target",
            canvas.Resolve(renamedPath) is not null && canvas.BeaconsPlaced == initialPlaced);

        directory.Files.RemoveAll(file => string.Equals(file.Name, renamedName, StringComparison.OrdinalIgnoreCase));
        var versionBeforeRemoval = tree.Version;
        var passesBeforeRemoval = canvas.BeaconResolutionPasses;
        await tree.RefreshAsync(drive);
        var versionAfterRemoval = tree.Version;
        canvas.RenderAsFrame();
        var renamedAfterRemoval = canvas.Resolve(renamedPath);
        Check($"a removed target is evicted on the next tree version "
              + $"(tree {versionBeforeRemoval}->{versionAfterRemoval}, pass {passesBeforeRemoval}->{canvas.BeaconResolutionPasses}, "
              + $"placed {canvas.BeaconsPlaced}/{initialPlaced}, target {(renamedAfterRemoval is null ? "gone" : "resolved")})",
            canvas.BeaconResolutionPasses == passesBeforeRemoval + 1
            && renamedAfterRemoval is null && canvas.BeaconsPlaced == initialPlaced - 1);

        var compatibilityPoints = Enumerable.Range(0, 300)
            .Select(index => new Point(index % 30 * 20, index / 30 * 20))
            .ToArray();
        var expectedAssignments = NaiveBeaconClusters(compatibilityPoints, 26);
        var compatible = NestedCanvas.ClusterPointsForTests(compatibilityPoints, 26);
        Check("grid clustering keeps the original first-nearby-lead membership and order",
            compatible.Assignments.SequenceEqual(expectedAssignments));

        var sparsePoints = Enumerable.Range(0, 5_000)
            .Select(index => new Point(index % 100 * 30, index / 100 * 30))
            .ToArray();
        var sparse = NestedCanvas.ClusterPointsForTests(sparsePoints, 26);
        Check("five thousand separate badges use a bounded number of proximity comparisons",
            sparse.Assignments.Distinct().Count() == sparsePoints.Length
            && sparse.Comparisons < sparsePoints.Length * 16);
    }

    private static int[] NaiveBeaconClusters(IReadOnlyList<Point> points, double radius)
    {
        var assignments = new int[points.Count];
        var leads = new List<Point>();
        for (var index = 0; index < points.Count; index++)
        {
            var group = leads.FindIndex(lead => (lead - points[index]).Length < radius);
            if (group < 0)
            {
                group = leads.Count;
                leads.Add(points[index]);
            }

            assignments[index] = group;
        }

        return assignments;
    }
}
