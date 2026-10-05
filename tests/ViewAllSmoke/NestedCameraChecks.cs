using System.Windows;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task NestedCameraChecks()
    {
        RunOnSta("camera requests", CameraRequestChecksAsync);
        return Task.CompletedTask;
    }

    private static async Task CameraRequestChecksAsync()
    {
        Section("camera requests: late reads and invalid coordinates");
        var disk = new FakeDisk();
        disk.Folder(@"Q:\slow\child");
        disk.Folder(@"Q:\ready");
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await tree.LoadAsync(tree.Find(@"Q:\")!);
        var canvas = new NestedCanvas { Tree = tree };
        canvas.Measure(new Size(1000, 700));
        canvas.Arrange(new Rect(0, 0, 1000, 700));
        canvas.UpdateLayout();
        canvas.FitAll(animated: false);
        using var gate = new SemaphoreSlim(0);
        var entered = 0;
        disk.Hook = (path, token) =>
        {
            if (path == @"Q:\slow")
            {
                Interlocked.Increment(ref entered);
                gate.Wait(token);
            }
            return null;
        };

        var delayed = canvas.FlyToPathAsync(@"Q:\slow\child", animated: false);
        await WaitUntil(() => Volatile.Read(ref entered) == 1, 3000);
        Check("the first camera destination really waits for a folder read", !delayed.IsCompleted && entered == 1);
        await canvas.FlyToPathAsync(@"Q:\ready", animated: false);
        var newerCamera = canvas.CaptureCamera();
        gate.Release();
        var oldAccepted = await delayed;
        Check("an older slow path cannot move the camera after the newer destination completed",
            !oldAccepted && canvas.CaptureCamera() == newerCamera);

        // A fresh tree guarantees that this second scenario also waits on a real read.
        using var replacement = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        replacement.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await replacement.LoadAsync(replacement.Find(@"Q:\")!);
        canvas.Tree = replacement;
        canvas.FitAll(animated: false);
        entered = 0;
        var oldTreeRequest = canvas.FlyToPathAsync(@"Q:\slow\child", animated: false);
        await WaitUntil(() => Volatile.Read(ref entered) == 1, 3000);
        canvas.Tree = tree;
        canvas.FitAll(animated: false);
        var switchedCamera = canvas.CaptureCamera();
        gate.Release();
        Check("a path finishing after its canvas changed trees is rejected without moving the replacement view",
            !await oldTreeRequest && canvas.CaptureCamera() == switchedCamera);

        using var restoreTree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        restoreTree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await restoreTree.LoadAsync(restoreTree.Find(@"Q:\")!);
        canvas.Tree = restoreTree;
        canvas.FitAll(animated: false);
        entered = 0;
        var slowRestore = canvas.RestoreCameraAsync(new NestedCameraState(@"Q:\slow\child", -0.3, -0.3, 0.8));
        await WaitUntil(() => Volatile.Read(ref entered) == 1, 3000);
        await canvas.RestoreCameraAsync(new NestedCameraState(@"Q:\ready", -0.3, -0.3, 0.8));
        var restoredNewer = canvas.CaptureCamera();
        gate.Release();
        await slowRestore;
        Check("two overlapping saved-camera restores keep the newer choice when the earlier read finishes last",
            canvas.CaptureCamera() == restoredNewer);

        disk.Hook = null;
        foreach (var bad in new[]
        {
            new NestedCameraState("", double.NaN, 0, 1),
            new NestedCameraState("", 0, double.PositiveInfinity, 1),
            new NestedCameraState("", double.MaxValue, 0, 1),
            new NestedCameraState("", 0, 0, double.MaxValue)
        })
        {
            var before = canvas.CaptureCamera();
            await canvas.RestoreCameraAsync(bad);
            Check("a nonfinite or overflowed saved camera leaves the existing view intact", canvas.CaptureCamera() == before);
        }
        foreach (var factor in new[] { 0d, -1d, double.NaN, double.PositiveInfinity, double.MaxValue })
        {
            var before = canvas.CaptureCamera();
            canvas.ZoomAt(new Point(500, 350), factor);
            Check("an invalid or overflowed zoom leaves the existing camera intact", canvas.CaptureCamera() == before);
        }
        foreach (var delta in new[] { new Vector(double.NaN, 0), new Vector(0, double.NegativeInfinity) })
        {
            var before = canvas.CaptureCamera();
            canvas.Pan(delta);
            Check("a nonfinite pan leaves the existing camera intact", canvas.CaptureCamera() == before);
        }
        canvas.Tree = null;
    }
}
