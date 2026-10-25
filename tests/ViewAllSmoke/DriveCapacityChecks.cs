using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task DriveCapacityChecks()
    {
        RunOnSta("drive capacity strip", async () =>
        {
            Section("drive capacity: typed snapshots, shared CPU/GPU strip and retained roots");
            var disk = new FakeDisk();
            var values = new (string Name, long? Total, long? Free, uint? Ink)[]
            {
                ("Healthy", 100, 50, 0xFF56B7E8), ("Low space", 100, 10, 0xFFE9B35D),
                ("Nearly full", 100, 5, 0xFFF06A72), ("Full", 100, 0, 0xFFF06A72),
                ("Unknown", null, null, null), ("Zero capacity", 0, 0, null),
                ("Invalid free", 100, -1, null), ("Free beyond total", 100, 110, null)
            };
            var roots = values.Select((value, index) => new NestedRoot($@"Q:\capacity-{index}", value.Name,
                NestedFolderKind.Drive, "Owned capacity fixture", value.Total, value.Free)).ToArray();
            foreach (var root in roots) disk.Folder(root.FullPath);
            using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
            tree.SetRoots(roots);
            foreach (var folder in tree.Root.Children) await tree.LoadAsync(folder);
            using var owned = new InlineCanvasScope(new NestedCanvas { Tree = tree, FramesByHandForTests = true });
            var canvas = owned.Canvas;
            canvas.Measure(new Size(1200, 800)); canvas.Arrange(new Rect(0, 0, 1200, 800)); canvas.UpdateLayout();
            var reads = disk.Reads;
            foreach (var scale in new[] { 1.0, 1.5 })
            {
                canvas.DpiOverride = new DpiScale(scale, scale);
                canvas.FitAll(animated: false); canvas.RenderNow();
                var picture = DriveCapacityPicture(canvas, scale);
                var valid = true; var unknown = true;
                for (var index = 0; index < roots.Length; index++)
                {
                    var cell = canvas.ScreenRectOf(tree.Find(roots[index].FullPath)!)!.Value;
                    var track = DriveCapacityTrack(cell);
                    if (values[index].Ink is { } ink)
                    {
                        var fraction = 1 - Math.Clamp((double)values[index].Free!.Value / values[index].Total!.Value, 0, 1);
                        valid &= DriveCapacityFillMatches(picture, track, fraction, ink, scale);
                    }
                    else if (values[index].Total is null or <= 0 || values[index].Free is null or < 0)
                    {
                        var point = new Point(track.X + track.Width * .4, track.Y + track.Height / 2);
                        unknown &= SameWithin(Pixel(picture, (int)(point.X * scale), (int)(point.Y * scale)),
                            Pixel(canvas.SceneBitmap!, (int)(point.X * scale), (int)(point.Y * scale)), 1);
                    }
                    else
                    {
                        var point = new Point(track.X + track.Width * .4, track.Y + track.Height / 2);
                        valid &= !SameWithin(Pixel(picture, (int)(point.X * scale), (int)(point.Y * scale)), 0xFF56B7E8, 4);
                    }
                }
                Check($"the actual CPU picture shows proportional cyan, warning and danger strips at {scale:0.0} DPI", valid);
                Check($"unknown, zero and invalid capacity never fabricate a full strip at {scale:0.0} DPI", unknown);
                DriveCapacitySave(picture, $"drive-capacity-overview-{scale:0.0}.png");
            }

            canvas.DpiOverride = new DpiScale(1, 1);
            var kept = tree.Find(roots[0].FullPath)!;
            canvas.FlyTo(kept, .85, animated: false); canvas.SetSelection([kept.FullPath], kept.FullPath); canvas.RenderNow();
            var camera = canvas.CaptureCamera(); var cellBefore = canvas.ScreenRectOf(kept);
            var selected = canvas.SelectionState.Items().Select(item => item.Path).ToArray();
            roots[0] = roots[0] with { DriveFreeBytes = 3 };
            tree.SetRoots(roots); canvas.RenderNow();
            Check("updating a retained root's capacity preserves object, camera, rectangle, hit and selection",
                ReferenceEquals(tree.Find(kept.FullPath), kept) && kept.DriveTotalBytes == 100 && kept.DriveFreeBytes == 3
                && camera == canvas.CaptureCamera() && canvas.ScreenRectOf(kept) == cellBefore
                && selected.SequenceEqual(canvas.SelectionState.Items().Select(item => item.Path))
                && canvas.HitTest(new Point(cellBefore!.Value.X + 30, cellBefore.Value.Y + 20))?.Folder == kept);
            // Scene+label composition excludes the intentional selected-cell
            // Decor tint, so strip colours can be held to the GPU's same ink.
            var updated = DriveCapacityPicture(canvas, 1);
            Check("the kept root paints its fresh capacity instead of a cached previous bar",
                DriveCapacityFillMatches(updated, DriveCapacityTrack(cellBefore!.Value), .97, 0xFFF06A72, 1));
            DriveCapacitySave(updated, "drive-capacity-zoomed.png");
            for (var index = 0; index < 8; index++) canvas.RenderNow();
            Check("drawing and updating capacity consume cached snapshots without another directory or drive read", disk.Reads == reads);

            try
            {
                using var devices = GpuDeviceSet.CreateWarp();
                var renderer = NestedGpuRenderer.For(devices, ShaderCache.Load(null));
                var faces = FaceRegistry.Shared;
                using var glyphs = new GlyphAtlas(faces);
                var labels = new GpuLabelTarget(faces, new TextShaper(faces), glyphs, icons: null);
                using var frame = new NestedGpuFrame();
                using var target = devices.CreateOffscreenTarget(canvas.ScenePixelWidth, canvas.ScenePixelHeight);
                canvas.RenderOffscreen(renderer, target, frame, labels);
                var bytes = target.ReadPixels();
                var gpu = BitmapSource.Create(canvas.ScenePixelWidth, canvas.ScenePixelHeight, 96, 96,
                    PixelFormats.Bgra32, null, bytes, canvas.ScenePixelWidth * 4);
                gpu.Freeze();
                Check("the actual WARP offscreen shared label path draws the same updated danger strip",
                    DriveCapacityFillMatches(gpu, DriveCapacityTrack(canvas.ScreenRectOf(kept)!.Value), .97, 0xFFF06A72, 1));
                DriveCapacitySave(gpu, "drive-capacity-warp.png");
            }
            catch (GpuUnavailableException error) { Console.WriteLine($"  note  drive strip WARP unavailable: {error.Message}"); }
        });
        return Task.CompletedTask;
    }

    private static Rect DriveCapacityTrack(Rect cell)
    {
        var height = Math.Clamp(cell.Width * .007, 6, 9);
        var left = Math.Max(6, cell.X + 6);
        var right = Math.Min(1200 - 6, cell.Right - 6);
        return new Rect(left + 1, cell.Y - 3 - height + 1, right - left - 2, height - 2);
    }

    private static bool DriveCapacityFillMatches(BitmapSource picture, Rect track, double fraction, uint ink, double scale)
    {
        if (track.Width < 12 || fraction <= 0) return false;
        var y = (int)((track.Y + track.Height / 2) * scale);
        var filled = (int)((track.X + Math.Min(12, track.Width * fraction / 2)) * scale);
        if (!SameWithin(Pixel(picture, filled, y), ink, 4)) return false;
        if (fraction >= 1) return SameWithin(Pixel(picture, (int)((track.Right - 8) * scale), y), ink, 4);
        var before = (int)Math.Floor((track.X + track.Width * fraction - track.Height) * scale);
        var after = (int)Math.Ceiling((track.X + track.Width * fraction + track.Height) * scale);
        return SameWithin(Pixel(picture, before, y), ink, 4) && !SameWithin(Pixel(picture, after, y), ink, 4);
    }

    private static RenderTargetBitmap DriveCapacityPicture(NestedCanvas canvas, double scale)
    {
        canvas.RenderNow();
        var picture = new RenderTargetBitmap(canvas.ScenePixelWidth, canvas.ScenePixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        picture.Render(canvas.SceneLayer); picture.Render(canvas.LabelLayer); picture.Freeze();
        return picture;
    }

    private static void DriveCapacitySave(BitmapSource picture, string name)
    {
        if (Environment.GetEnvironmentVariable("DRIVE_CAPACITY_CAPTURE_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(picture));
        using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
        Console.WriteLine($"  note  synthetic drive strip capture: {Path.Combine(directory, name)}");
    }
}
