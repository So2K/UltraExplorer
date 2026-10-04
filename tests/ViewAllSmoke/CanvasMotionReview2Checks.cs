using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// What the second review found in the canvas's camera, input and frame
/// loop, and what was done about it.  An arrow key held down keeps the item
/// it moves to on screen at the zoom it had, rather than restarting a flight
/// from a standstill on every repeat.
/// </summary>
internal static partial class Program
{
    private static Task CanvasMotionReview2Checks()
    {
        RunOnSta("canvas motion: an arrow key held down", CanvasMotionKeyRepeatAsync);
        return Task.CompletedTask;
    }

    // ---- an arrow key held down (J002) ---------------------------------------------------------

    private const double MotionViewWidth = 1200;
    private const double MotionViewHeight = 800;

    /// <summary>
    /// A folder of fifteen thousand files, a file clicked and flown to where
    /// its name can be read, then Down held for three seconds and Right for
    /// one, at the keyboard's repeat rate.  Each repeat brings the next tile
    /// into view: it may run a row past the edge between two repeats, never
    /// more, and the tiles keep the height the first press gave them.
    /// </summary>
    private static async Task CanvasMotionKeyRepeatAsync()
    {
        Section("canvas motion: an arrow key held down keeps the item on screen, at its zoom");
        var disk = new FakeDisk();
        disk.AddFiles(@"Q:\many", 15_000, "f");
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        var canvas = new NestedCanvas { FramesByHandForTests = true, DpiOverride = new DpiScale(1, 1), Tree = tree };
        canvas.Measure(new Size(MotionViewWidth, MotionViewHeight));
        canvas.Arrange(new Rect(0, 0, MotionViewWidth, MotionViewHeight));
        canvas.UpdateLayout();
        var many = tree.Find(@"Q:\many")!;
        var time = TimeSpan.FromSeconds(1000);
        string? selected = null;
        canvas.SelectRequested += (path, _) => selected = path;
        try
        {
            foreach (var (key, seconds) in new[] { (Key.Down, 3.0), (Key.Right, 1.0) })
            {
                canvas.FlyTo(many, 0.92, animated: false);
                time = await MotionFramesAsync(canvas, time, 1);
                var cell = canvas.ScreenRectOf(many)!.Value;
                canvas.Pointer.Click(TilePoint(cell, many, 0));
                selected = null;

                // The first press flies to where the next file can be read.
                canvas.HandleKey(key, ModifierKeys.None);
                time = await MotionSettleAsync(canvas, time);
                var first = MotionTile(canvas, many, selected);
                if (first is not { } start)
                {
                    Check($"{key}: the first press selects a file ({selected ?? "nothing"})", false);
                    continue;
                }

                var rowStep = key == Key.Down
                    ? many.FileGrid.StepY / many.FileGrid.TileHeight * start.Height
                    : many.FileGrid.StepX / many.FileGrid.TileWidth * start.Width;
                var worstOutside = 0.0;
                var smallest = start.Height;
                var presses = 0;
                var held = Stopwatch.StartNew();
                var nextPress = 0.0;
                while (held.Elapsed.TotalSeconds < seconds)
                {
                    if (held.Elapsed.TotalMilliseconds >= nextPress)
                    {
                        // Just before the repeat: how far the tile the last one
                        // moved to has got from the view.
                        if (presses > 0 && MotionTile(canvas, many, selected) is { } before)
                        {
                            worstOutside = Math.Max(worstOutside, Outside(before));
                            smallest = Math.Min(smallest, before.Height);
                        }

                        canvas.HandleKey(key, ModifierKeys.None);
                        presses++;
                        nextPress += 33;
                    }

                    canvas.RunFrameForTests(time += Frame);
                    await Task.Delay(1);
                }

                time = await MotionSettleAsync(canvas, time);
                var end = MotionTile(canvas, many, selected);
                Console.WriteLine($"        {key} held {seconds:0} s, {presses} repeats: the tile was at most {worstOutside:0} DIPs outside the view (a step is {rowStep:0}), "
                    + $"its height {start.Height:0.0} -> {smallest:0.0} while held, {end?.Height:0.0} at rest; at rest it is at {end?.X:0},{end?.Y:0}");
                Check($"{key} held: between repeats the selected tile is never more than a step outside the view ({worstOutside:0} DIPs, a step {rowStep:0})",
                    worstOutside <= rowStep + 1);
                Check($"{key} held: the tiles keep the height the first press gave them ({start.Height:0.0} DIPs, smallest {smallest:0.0})",
                    smallest >= start.Height * 0.97 && end is { } last && Math.Abs(last.Height - start.Height) <= start.Height * 0.03);
                Check($"{key} let go: the view comes to rest with the selected tile inside it ({end?.X:0},{end?.Y:0} {end?.Width:0}x{end?.Height:0})",
                    end is { } rest && rest.Left >= -1 && rest.Top >= -1 && rest.Right <= MotionViewWidth + 1 && rest.Bottom <= MotionViewHeight + 1);
            }
        }
        finally
        {
            canvas.Tree = null;
        }

        static double Outside(Rect tile) => Math.Max(
            Math.Max(-tile.Top, tile.Bottom - MotionViewHeight),
            Math.Max(-tile.Left, tile.Right - MotionViewWidth));
    }

    /// <summary>The tile of the file at <paramref name="path"/> on screen, or null.</summary>
    private static Rect? MotionTile(NestedCanvas canvas, NestedFolder folder, string? path)
    {
        if (path is null || canvas.Resolve(path) is not { FileIndex: >= 0 } found || !ReferenceEquals(found.Folder, folder)
            || canvas.ScreenRectOf(folder) is not { } cell)
        {
            return null;
        }

        var grid = folder.FileGrid;
        var (x, y) = grid.Origin(found.FileIndex);
        return new Rect(cell.X + x * cell.Width, cell.Y + y * cell.Width, grid.TileWidth * cell.Width, grid.TileHeight * cell.Width);
    }

    /// <summary>Frames until the camera has stopped and settled: returns the time of the last.</summary>
    private static async Task<TimeSpan> MotionSettleAsync(NestedCanvas canvas, TimeSpan time)
    {
        var watch = Stopwatch.StartNew();
        while (canvas.IsCameraMoving && watch.ElapsedMilliseconds < 5_000)
        {
            canvas.RunFrameForTests(time += Frame);
            await Task.Delay(1);
        }

        return await MotionFramesAsync(canvas, time, 2);
    }

    private static async Task<TimeSpan> MotionFramesAsync(NestedCanvas canvas, TimeSpan time, int frames)
    {
        for (var frame = 0; frame < frames; frame++)
        {
            canvas.RunFrameForTests(time += Frame);
            await Task.Delay(1);
        }

        return time;
    }
}
