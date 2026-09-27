using System.Runtime.InteropServices;
using System.Windows;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// Redrawing only what folders read changed: a batch of folders off screen
/// draws nothing, one whose folders carry no name draws the cells alone, and
/// on the CPU only the changed cells' pixels are painted - exactly the pixels
/// a whole frame paints there, and nothing outside them.  At rest such
/// redraws wait their turn, at most one every LoadRedrawMinMs; a change that
/// is not a batch still redraws everything.
/// </summary>
internal static partial class Program
{
    private static Task FrameScopeChecks()
    {
        Section("frame scope");
        RasterClipChecks();
        RunOnSta("frame scope on a dispatcher", FrameScopeCanvasChecks);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The raster sink painting through a clip: over the random scene the GPU
    /// is measured against - edges on exact halves, parents trillions of
    /// pixels out - every clip paints inside it exactly what the whole frame
    /// paints there, and leaves every pixel outside it alone.
    /// </summary>
    private static void RasterClipChecks()
    {
        const int width = 640;
        const int height = 480;
        const uint background = 0xFF111315;
        const uint untouched = 0xFFFF00FF;
        var calls = SyntheticScene(new Random(20260927), width, height);
        var whole = PaintThroughSink(calls, width, height, background, untouched, new Int32Rect(0, 0, width, height));

        var random = new Random(7);
        var wrong = 0;
        var leaked = 0;
        var clips = 0;
        for (var run = 0; run < 40; run++)
        {
            // Corners on blocks of eight, as the canvas aligns them; the far
            // edges anywhere, the view's own edge included.
            var x0 = random.Next(0, width / 8) * 8;
            var y0 = random.Next(0, height / 8) * 8;
            var x1 = run % 5 == 0 ? width : random.Next(x0 + 1, width + 1);
            var y1 = run % 7 == 0 ? height : random.Next(y0 + 1, height + 1);
            var clip = new Int32Rect(x0, y0, x1 - x0, y1 - y0);
            var part = PaintThroughSink(calls, width, height, background, untouched, clip);
            clips++;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var inside = x >= x0 && x < x1 && y >= y0 && y < y1;
                    var pixel = part[y * width + x];
                    if (inside && pixel != whole[y * width + x])
                    {
                        wrong++;
                    }
                    else if (!inside && pixel != untouched)
                    {
                        leaked++;
                    }
                }
            }
        }

        Check($"a clipped raster paints exactly the whole frame's pixels inside the clip ({clips} clips, {wrong} pixels differ)", wrong == 0);
        Check($"and not one pixel outside it ({leaked} painted)", leaked == 0);
    }

    /// <summary>The scene painted through a <see cref="RasterSink"/> clipped to <paramref name="clip"/>, over a buffer of <paramref name="untouched"/>.</summary>
    private static uint[] PaintThroughSink(List<Action<SceneSink>> calls, int width, int height, uint background, uint untouched, Int32Rect clip)
    {
        var pixels = new uint[width * height];
        Array.Fill(pixels, untouched);
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var sink = new RasterSink(new NestedRaster());
            sink.Begin(handle.AddrOfPinnedObject(), width * 4, clip);
            sink.Clear(background);
            foreach (var call in calls)
            {
                call(sink);
            }

            sink.End();
        }
        finally
        {
            handle.Free();
        }

        return pixels;
    }

    /// <summary>
    /// A canvas with no window, its frames run by hand at times of the test's
    /// choosing, and folders read into its tree while it looks at them - or
    /// away from them.
    /// </summary>
    private static async Task FrameScopeCanvasChecks()
    {
        var disk = new FakeDisk();
        disk.Folder(@"Q:\far");
        disk.AddFiles(@"Q:\far", 30, "far");
        disk.Folder(@"Q:\lab\inner");
        disk.AddFiles(@"Q:\lab", 12, "lab");
        for (var index = 0; index < 200; index++)
        {
            var path = $@"Q:\big\s{index:D3}";
            disk.Folder(path + @"\a");
            disk.Folder(path + @"\b");
            disk.Folder(path + @"\c");
            disk.AddFiles(path, 8, "f");
        }

        disk.AddFiles(@"Q:\big", 10, "top");
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await tree.LoadAsync(tree.Find(@"Q:\")!);
        await tree.LoadAsync(tree.Find(@"Q:\big")!);

        var canvas = new NestedCanvas { FramesByHandForTests = true, DpiOverride = new DpiScale(1.5, 1.5), Tree = tree };
        canvas.Measure(new Size(800, 500));
        canvas.Arrange(new Rect(0, 0, 800, 500));
        canvas.UpdateLayout();
        var time = TimeSpan.FromSeconds(200);
        NestedFolder Folder(string path) => tree.Find(path)!;

        // ---- a folder whose name is drawn: the cells and the names ----------
        canvas.FlyTo(Folder(@"Q:\"), 0.92, animated: false);
        time = await RunUntilStillAsync(canvas, time);
        var lab = Folder(@"Q:\lab");
        Check($"the view over Q:\\ is still, and its folders carry their names (lab {canvas.ScreenRectOf(lab)?.Width:0} DIPs wide)",
            canvas.IsIdle && !canvas.IsFrameHooked && canvas.ScreenRectOf(lab) is { Width: >= 200 });

        var scenes = canvas.RenderCount;
        var names = canvas.LabelLayerCount;
        await tree.LoadAsync(lab);
        canvas.RunFrameForTests(time += Frame);
        Check($"a labelled folder read redraws the cells and the names ({canvas.LastFrameLayers})",
            canvas.RenderCount == scenes + 1 && canvas.LabelLayerCount == names + 1
            && (canvas.LastFrameLayers & (NestedCanvas.Layers.Scene | NestedCanvas.Layers.Labels)) == (NestedCanvas.Layers.Scene | NestedCanvas.Layers.Labels));
        Check("and the loop lets go once it is drawn", canvas.IsIdle && !canvas.IsFrameHooked);

        // ---- folders too small for a name: the cells alone --------------------
        canvas.FlyTo(Folder(@"Q:\big"), 0.92, animated: false);
        time = await RunUntilStillAsync(canvas, time);
        var small = Folder(@"Q:\big\s005");
        var smallRect = canvas.ScreenRectOf(small);
        Check($"into Q:\\big its sub-folders are on screen, too narrow for a name ({smallRect?.Width:0.0} DIPs)",
            canvas.IsIdle && smallRect is { Width: > 1 and < 56 } && smallRect.Value.IntersectsWith(new Rect(0, 0, 800, 500)));

        scenes = canvas.RenderCount;
        names = canvas.LabelLayerCount;
        await tree.LoadAsync(small);
        Check("a folder read on screen asks for a frame", canvas.IsFrameHooked);
        canvas.RunFrameForTests(time += Frame);
        var loadDrawnAt = time;
        Check($"one with no name drawn redraws the cells alone, not the names ({canvas.LastFrameLayers})",
            canvas.RenderCount == scenes + 1 && canvas.LabelLayerCount == names
            && (canvas.LastFrameLayers & NestedCanvas.Layers.Scene) != 0 && (canvas.LastFrameLayers & (NestedCanvas.Layers.Labels | NestedCanvas.Layers.Decor)) == 0);
        Check("and the loop lets go once the cells are drawn", canvas.IsIdle && !canvas.IsFrameHooked);

        // ---- the CPU paints only the changed cells ---------------------------
        var before = ScenePixels(canvas);
        var first = Folder(@"Q:\big\s006");
        var second = Folder(@"Q:\big\s007");
        await tree.LoadAsync(first);
        await tree.LoadAsync(second);
        canvas.RunFrameForTests(time = loadDrawnAt + TimeSpan.FromMilliseconds(40));
        loadDrawnAt = time;
        var clip = canvas.LastSceneClip;
        var partial = ScenePixels(canvas);
        var changed = PixelRect(canvas.ScreenRectOf(first)!.Value, 1.5);
        changed.Union(PixelRect(canvas.ScreenRectOf(second)!.Value, 1.5));
        var clipRect = new Rect(clip.X, clip.Y, clip.Width, clip.Height);
        var slack = changed;
        slack.Inflate(10, 10);
        Check($"two folders read at rest are one frame that paints only their cells ({clip.X},{clip.Y} {clip.Width}x{clip.Height} of {partial.Width}x{partial.Height}, cells {changed})",
            !clip.IsEmpty && clipRect.Contains(changed) && slack.Contains(clipRect) && clip.X % 8 == 0 && clip.Y % 8 == 0);

        var outside = 0;
        var inside = 0;
        for (var y = 0; y < partial.Height; y++)
        {
            for (var x = 0; x < partial.Width; x++)
            {
                var differs = partial.Pixels[y * partial.Width + x] != before.Pixels[y * before.Width + x];
                if (x >= clip.X && x < clip.X + clip.Width && y >= clip.Y && y < clip.Y + clip.Height)
                {
                    inside += differs ? 1 : 0;
                }
                else
                {
                    outside += differs ? 1 : 0;
                }
            }
        }

        Check($"every pixel outside them is as it was ({outside} changed), and inside them the folders' contents appeared ({inside} changed)", outside == 0 && inside > 100);

        canvas.Redraw();
        canvas.RunFrameForTests(time += Frame);
        var full = ScenePixels(canvas);
        var mismatched = 0;
        for (var index = 0; index < full.Pixels.Length; index++)
        {
            mismatched += full.Pixels[index] != partial.Pixels[index] ? 1 : 0;
        }

        Check($"and the picture is the one a whole frame paints ({mismatched} pixels differ)", canvas.LastSceneClip.IsEmpty && mismatched == 0);

        // ---- at rest, loading redraws wait their turn ------------------------
        scenes = canvas.RenderCount;
        await tree.LoadAsync(Folder(@"Q:\big\s008"));
        canvas.RunFrameForTests(loadDrawnAt + TimeSpan.FromMilliseconds(20));
        Check($"a folder read within {FrameBudgets.LoadRedrawMinMs} ms of the last loading redraw waits its turn, with the loop let go meanwhile",
            canvas.RenderCount == scenes && !canvas.IsFrameHooked && !canvas.IsIdle);
        await Task.Delay(80);
        Check("and its turn asks for a frame again", canvas.IsFrameHooked);
        canvas.RunFrameForTests(time = loadDrawnAt + TimeSpan.FromMilliseconds(40));
        Check("and is drawn once its turn comes", canvas.RenderCount == scenes + 1 && canvas.IsIdle && !canvas.IsFrameHooked);

        // ---- the marks, only when they show something in a folder read -------
        var dropped = Folder(@"Q:\big\s009");
        canvas.DropTarget = dropped;
        canvas.RunFrameForTests(time += Frame);
        await tree.LoadAsync(dropped);
        canvas.RunFrameForTests(time += TimeSpan.FromMilliseconds(40));
        var withDrop = canvas.LastFrameLayers;
        await tree.LoadAsync(Folder(@"Q:\big\s010"));
        canvas.RunFrameForTests(time += TimeSpan.FromMilliseconds(40));
        var elsewhere = canvas.LastFrameLayers;
        Check($"the marks are recorded again for the folder being dropped on ({withDrop}), not for its neighbour ({elsewhere})",
            (withDrop & NestedCanvas.Layers.Decor) != 0 && (elsewhere & NestedCanvas.Layers.Scene) != 0 && (elsewhere & NestedCanvas.Layers.Decor) == 0);
        canvas.DropTarget = null;
        canvas.RunFrameForTests(time += Frame);

        // ---- a change that is not a batch still redraws everything -----------
        tree.Invalidate(Folder(@"Q:\big\s010"));
        canvas.RunFrameForTests(time += Frame);
        Check($"a change to the tree that is not a batch of reads redraws every layer ({canvas.LastFrameLayers})",
            canvas.LastFrameLayers == NestedCanvas.Layers.All);

        // ---- with the name filter on, a folder read lights folders round it ---
        canvas.SetFilter("f00");
        canvas.RunFrameForTests(time += Frame);
        await tree.LoadAsync(Folder(@"Q:\big\s011"));
        canvas.RunFrameForTests(time += TimeSpan.FromMilliseconds(40));
        Check($"with the name filter on, a folder read redraws every layer, all of the cells ({canvas.LastFrameLayers})",
            canvas.LastFrameLayers == NestedCanvas.Layers.All && canvas.LastSceneClip.IsEmpty && canvas.IsIdle);
        canvas.SetFilter(null);
        canvas.RunFrameForTests(time += Frame);

        // ---- folders off screen: nothing at all -------------------------------
        canvas.FlyTo(Folder(@"Q:\big\s000"), 0.92, animated: false);
        time = await RunUntilStillAsync(canvas, time);
        var far = Folder(@"Q:\far");
        var distant = Folder(@"Q:\big\s150");
        var view = new Rect(0, 0, 800, 500);
        Check("zoomed into one sub-folder, Q:\\far and a distant sibling are off screen",
            canvas.IsIdle && canvas.ScreenRectOf(far) is { } farRect && !farRect.IntersectsWith(view)
            && canvas.ScreenRectOf(distant) is { } distantRect && !distantRect.IntersectsWith(view));
        scenes = canvas.RenderCount;
        names = canvas.LabelLayerCount;
        await tree.LoadAsync(far);
        await tree.LoadAsync(distant);
        var hooked = canvas.IsFrameHooked;
        canvas.RunFrameForTests(time += TimeSpan.FromMilliseconds(40));
        Check("folders read off screen draw no frame at all, and never hook the loop",
            !hooked && canvas.RenderCount == scenes && canvas.LabelLayerCount == names && canvas.LastFrameStats.Skipped && canvas.IsIdle);

        canvas.Tree = null;
    }

    /// <summary>One frame at the camera's new place, then one once it has been still long enough to settle, and the frames after: returns the time of the last.</summary>
    private static async Task<TimeSpan> RunUntilStillAsync(NestedCanvas canvas, TimeSpan time)
    {
        canvas.RunFrameForTests(time += Frame);
        await Task.Delay(420);
        for (var frame = 0; frame < 3; frame++)
        {
            canvas.RunFrameForTests(time += Frame);
        }

        return time;
    }

    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(8.33);

    /// <summary>A copy of the pixels of the canvas's last CPU scene, at the view's size.</summary>
    private static (uint[] Pixels, int Width, int Height) ScenePixels(NestedCanvas canvas)
    {
        var width = canvas.ScenePixelWidth;
        var height = canvas.ScenePixelHeight;
        var pixels = new uint[width * height];
        canvas.SceneBitmap!.CopyPixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        return (pixels, width, height);
    }

    /// <summary>A cell's rectangle in DIPs as the pixels it covers, out to whole pixels.</summary>
    private static Rect PixelRect(Rect dips, double scale)
    {
        var left = Math.Floor(dips.Left * scale);
        var top = Math.Floor(dips.Top * scale);
        return new Rect(left, top, Math.Ceiling(dips.Right * scale) - left, Math.Ceiling(dips.Bottom * scale) - top);
    }
}
