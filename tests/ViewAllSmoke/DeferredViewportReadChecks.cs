using System.Windows;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task DeferredViewportReadChecks()
    {
        RunOnSta("deferred viewport reads", DeferredViewportReadsOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task DeferredViewportReadsOnStaAsync()
    {
        Section("deferred viewport reads: resume after camera motion without input");
        var disk = new FakeDisk();
        disk.Folder(@"Q:\known");
        disk.Folder(@"Q:\other");
        disk.AddFiles(@"Q:\", 3, "file-");
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        var drive = tree.Find(@"Q:\")!;
        drive.HasPartialListing = true;
        var canvas = new NestedCanvas { Tree = tree, FramesByHandForTests = true };
        canvas.Measure(new Size(800, 500));
        canvas.Arrange(new Rect(0, 0, 800, 500));
        canvas.UpdateLayout();
        canvas.RenderNow();
        tree.IsReadingOnDemand = true;
        tree.PartialListingReadAllowed = _ => !canvas.IsCameraMoving;

        // RenderNow is used by actual presentation snapshots. It deliberately
        // uses crisp text even during the short camera-motion window, so it
        // cannot depend on an animated-text quality redraw to resume a read.
        canvas.FitAll(animated: false);
        canvas.RenderNow();
        Check("snapshot during recent motion leaves partial content deferred",
            canvas.IsCameraMoving && disk.Reads == 0 && drive.LoadState == NestedLoadState.NotLoaded);
        Check("a partial listing does not claim a complete folder count", canvas.DetailText(drive).Length == 0);

        var time = TimeSpan.FromSeconds(500);
        await Task.Delay(180);
        canvas.RunFrameForTests(time);
        for (var frame = 0; frame < 60 && !drive.IsLoaded; frame++)
        {
            await Task.Delay(10);
            canvas.RunFrameForTests(time += TimeSpan.FromMilliseconds(16));
        }
        Check("the final viewport loads without a click or another redraw request",
            drive.IsLoaded && drive.Children.Count == 2 && drive.Files.Count == 3);
        for (var frame = 0; frame < 60 && !canvas.IsIdle; frame++)
        {
            await Task.Delay(10);
            canvas.RunFrameForTests(time += TimeSpan.FromMilliseconds(16));
        }
        Check("deferred reads complete and the frame loop becomes idle", canvas.IsIdle && !canvas.IsFrameHooked);
        var finishedReads = disk.Reads;
        for (var frame = 0; frame < 10; frame++) canvas.RunFrameForTests(time += TimeSpan.FromMilliseconds(16));
        Check("idle frames do not repeatedly read the same contents", disk.Reads == finishedReads);

        using var pickerTree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        pickerTree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        var pickerDrive = pickerTree.Find(@"Q:\")!;
        pickerDrive.HasPartialListing = true;
        var picker = new NestedCanvas { Tree = pickerTree, FramesByHandForTests = true };
        picker.Measure(new Size(800, 500));
        picker.Arrange(new Rect(0, 0, 800, 500));
        picker.UpdateLayout();
        picker.RenderNow();
        pickerTree.IsReadingOnDemand = true;
        pickerTree.PartialListingReadAllowed = _ => false;
        picker.FitAll(animated: false);
        picker.RenderNow();
        await Task.Delay(180);
        for (var frame = 0; frame < 10; frame++) picker.RunFrameForTests(time += TimeSpan.FromMilliseconds(16));
        Check("an intentionally deferred picker ancestor remains unread and reaches idle",
            pickerDrive.LoadState == NestedLoadState.NotLoaded && disk.Reads == finishedReads
            && picker.IsIdle && !picker.IsFrameHooked);

        var gatedDisk = new FakeDisk();
        gatedDisk.Folder(@"Q:\visible child");
        using var gatedTree = new NestedTree(gatedDisk.Read) { IsReadingOnDemand = false };
        gatedTree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        var gatedDrive = gatedTree.Find(@"Q:\")!;
        var gated = new NestedCanvas { Tree = gatedTree, FramesByHandForTests = true, LoadUnfocusedRoots = false };
        gated.Measure(new Size(800, 500));
        gated.Arrange(new Rect(0, 0, 800, 500));
        gated.UpdateLayout();
        gated.RenderNow();
        gatedTree.IsReadingOnDemand = true;
        for (var frame = 0; frame < 10; frame++) gated.RunFrameForTests(time += TimeSpan.FromMilliseconds(16));
        Check("an intentionally gated normal overview performs no root read", gatedDisk.Reads == 0 && !gatedDrive.IsLoaded && gated.IsIdle);
        gated.LoadUnfocusedRoots = true;
        Check("releasing a navigation gate schedules a new scene without input", !gated.IsIdle);
        for (var frame = 0; frame < 60 && !gatedDrive.IsLoaded; frame++)
        {
            await Task.Delay(10);
            gated.RunFrameForTests(time += TimeSpan.FromMilliseconds(16));
        }
        Check("the already visible root fills after gate release without camera movement", gatedDrive.IsLoaded && gatedDrive.Children.Count == 1);
    }
}
