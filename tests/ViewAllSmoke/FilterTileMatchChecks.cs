using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// J012: with the name filter on, a folder of twenty thousand files in view
/// asked the filter about every tile on every frame - a regular expression
/// per tile for a wildcard, and for patterns joined by ';' a closure per
/// tile too - which added some six milliseconds and over a megabyte to each
/// frame.  Each file is now asked once per filter, the first time its tile
/// or its name is drawn, and later frames draw the very same picture from
/// what was found.
/// </summary>
internal static partial class Program
{
    private static Task FilterTileMatchChecks()
    {
        RunOnSta("filter tiles: each file judged once per filter", FilterTileMatchOnceAsync);
        return Task.CompletedTask;
    }

    private const int FilterTileFiles = 20_000;

    private static async Task FilterTileMatchOnceAsync()
    {
        Section("filter tiles: a filtered folder of 20,000 files draws without asking the filter every frame (J012)");
        var disk = new FakeDisk();
        for (var index = 0; index < FilterTileFiles; index++)
        {
            var extension = index % 7 == 0 ? "jpg" : index % 11 == 0 ? "png" : "txt";
            disk.AddFile(@"Q:\big", $"f{index:D5}.{extension}", index);
        }

        disk.AddFiles(@"Q:\small", 24, "item");
        disk.AddFile(@"Q:\small", "picture.png", 10);
        disk.AddFile(@"Q:\small", "photo.jpg", 10);
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(tree, _ => true);
        var canvas = new NestedCanvas { Tree = tree };
        canvas.Measure(new Size(1200, 800));
        canvas.Arrange(new Rect(0, 0, 1200, 800));
        canvas.UpdateLayout();
        try
        {
            var big = tree.Find(@"Q:\big")!;
            canvas.FlyTo(big, 0.98, animated: false);
            canvas.RenderNow();

            // Unfiltered, for the time a frame of these tiles takes anyway.
            var plain = FilterTileFrames(canvas, 7);

            canvas.SetFilter("*.jpg;*.png");
            canvas.RenderNow();
            var first = FilterTilePixels(canvas);
            var filtered = FilterTileFrames(canvas, 7);
            var again = FilterTilePixels(canvas);
            Check($"a frame of {FilterTileFiles:N0} filtered tiles after the first hands the collector next to nothing ({filtered.SceneBytes:N0} bytes in the scene, {filtered.LabelBytes:N0} in the names)",
                filtered.SceneBytes < 64 * 1024 && filtered.LabelBytes < 64 * 1024);
            Check($"and costs about what an unfiltered one does (scene {filtered.SceneMs:F2} ms filtered, {plain.SceneMs:F2} ms not)",
                filtered.SceneMs < plain.SceneMs + 2.5);
            Check($"and draws the very picture the first filtered frame drew (scene {first.Scene:X16}, canvas {first.Canvas:X16})",
                first == again);
            Console.WriteLine($"  note  pictures: big folder filtered {first.Scene:X16} / {first.Canvas:X16}");

            // A new filter is a new judging: what was found for the last one
            // is not used for it, and the tiles that match it light up.
            canvas.SetFilter("*.txt");
            canvas.RenderNow();
            var text = FilterTilePixels(canvas);
            canvas.SetFilter("*.jpg;*.png");
            canvas.RenderNow();
            var back = FilterTilePixels(canvas);
            Check("another filter draws another picture, and the first one back draws the first picture again",
                text != first && back == first);

            // Close enough to read the names: a faded file's name is drawn
            // dim, a matching one's bright, the same on every frame.
            var small = tree.Find(@"Q:\small")!;
            canvas.FlyTo(small, 0.98, animated: false);
            canvas.RenderNow();
            var named = FilterTilePixels(canvas);
            canvas.RenderNow();
            var namedAgain = FilterTilePixels(canvas);
            Check($"with the names on the tiles, every frame draws the same picture ({named.Canvas:X16})",
                named == namedAgain && canvas.FileLabelCount > 20);
            Console.WriteLine($"  note  pictures: small folder filtered {named.Scene:X16} / {named.Canvas:X16}");

            // A folder read again is a new list of files, judged afresh.
            disk.AddFile(@"Q:\small", "newer.jpg", 10);
            await tree.RefreshAsync(small);
            canvas.RenderNow();
            var reread = FilterTilePixels(canvas);
            Check($"a folder read again with a new matching file draws it lit ({small.Files.Count} files)",
                reread != named && small.Files.Count == 27);
        }
        finally
        {
            canvas.SetFilter(null);
            canvas.Tree = null;
        }
    }

    /// <summary>
    /// <paramref name="count"/> frames of the view as it is, drawn whole, and
    /// the median time and the most bytes the scene and the names took.
    /// </summary>
    private static (double SceneMs, long SceneBytes, long LabelBytes) FilterTileFrames(NestedCanvas canvas, int count)
    {
        var times = new List<double>(count);
        long sceneBytes = 0;
        long labelBytes = 0;
        for (var frame = 0; frame < count; frame++)
        {
            canvas.RenderNow();
            times.Add(canvas.LastWalkMilliseconds);
            if (frame > 0)
            {
                sceneBytes = Math.Max(sceneBytes, canvas.LastAllocations.Scene);
                labelBytes = Math.Max(labelBytes, canvas.LastAllocations.Labels);
            }
        }

        times.Sort();
        return (times[times.Count / 2], sceneBytes, labelBytes);
    }

    /// <summary>A hash of the scene's own pixels, and one of the whole canvas - cells, names and marks - drawn into a bitmap.</summary>
    private static (ulong Scene, ulong Canvas) FilterTilePixels(NestedCanvas canvas)
    {
        var scene = 0UL;
        if (canvas.SceneBitmap is { } bitmap)
        {
            var width = canvas.ScenePixelWidth;
            var height = canvas.ScenePixelHeight;
            var pixels = new int[width * height];
            bitmap.CopyPixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
            scene = FilterTileHash(pixels);
        }

        var whole = new RenderTargetBitmap((int)canvas.ActualWidth, (int)canvas.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        whole.Render(canvas);
        var all = new int[whole.PixelWidth * whole.PixelHeight];
        whole.CopyPixels(all, whole.PixelWidth * 4, 0);
        return (scene, FilterTileHash(all));
    }

    private static ulong FilterTileHash(int[] pixels)
    {
        var hash = 14695981039346656037UL;
        foreach (var pixel in pixels)
        {
            hash = (hash ^ (uint)pixel) * 1099511628211UL;
        }

        return hash;
    }
}
