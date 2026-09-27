using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The GPU's rectangles against the CPU raster they replace, headless: the
/// instance layout, the instance lists, the shader cache, and then the same
/// shapes and the same canvas views drawn both ways - on WARP, which gives the
/// same pixels on every machine, and on the card behind the primary monitor
/// when there is one (offscreen only; nothing is ever shown).  Straight edges
/// must land on the very pixels the raster fills; only the anti-aliased
/// corners may differ, and only a little.  Set NESTED_SHOTS to a folder to
/// keep the GPU's pictures beside the raster's.
/// </summary>
internal static partial class Program
{
    /// <summary>A channel may differ by this much away from the rounded corners (rounding of blends).</summary>
    private const int ParityTolerance = 2;

    /// <summary>A channel may differ by this much in a rounded corner, where the GPU measures coverage differently.</summary>
    private const int CornerTolerance = 48;

    /// <summary>The shaders for these checks: compiled here, never read from or written to the state folder.</summary>
    private static CompiledShaders? _rectShaders;

    private static Task GpuRectChecks()
    {
        Section("gpu rectangles");
        GpuRectLayoutChecks();
        GpuShaderCacheChecks();
        try
        {
            _rectShaders = ShaderCache.Load(null);
        }
        catch (GpuUnavailableException ex)
        {
            Check($"the rectangle shaders compile for the parity checks ({ex.Message})", false);
            return Task.CompletedTask;
        }

        var sets = new List<GpuDeviceSet>();
        try
        {
            sets.Add(GpuDeviceSet.CreateWarp());
        }
        catch (Exception ex)
        {
            Check($"a WARP set draws the rectangle checks ({ex.Message})", false);
        }

        if (GpuDeviceSet.EnumerateAdapters().Any(adapter => !adapter.IsSoftware)
            && GpuBootstrap.AdapterLuidForMonitor(GpuBootstrap.PrimaryMonitor) is { } luid)
        {
            try
            {
                // Offscreen only: no Direct3D 9 device, nothing on the screen.
                sets.Add(GpuDeviceSet.Create(luid, shareWithWpf: false));
            }
            catch (GpuUnavailableException ex)
            {
                Console.WriteLine($"  note  the primary monitor's card cannot draw offscreen here ({ex.Message}); WARP only");
            }
        }
        else
        {
            Console.WriteLine("  (no hardware graphics adapter: the rectangle checks run on WARP only)");
        }

        try
        {
            foreach (var set in sets)
            {
                GpuSyntheticParityChecks(set);
            }

            RunOnSta("gpu rectangle parity", () => GpuCanvasParityChecksAsync(sets));
        }
        finally
        {
            foreach (var set in sets)
            {
                set.Dispose();
            }
        }

        return Task.CompletedTask;
    }

    private static void GpuRectLayoutChecks()
    {
        Check("a rectangle instance is the design's 64 bytes, a cache line",
            Marshal.SizeOf<RectInstance>() == 64 && Unsafe.SizeOf<RectInstance>() == RectInstance.Size);
        Check("its fields sit where the input layout reads them",
            Marshal.OffsetOf<RectInstance>(nameof(RectInstance.Feature)) == 16
            && Marshal.OffsetOf<RectInstance>(nameof(RectInstance.HeaderBottom)) == 32
            && Marshal.OffsetOf<RectInstance>(nameof(RectInstance.InnerRadii)) == 40
            && Marshal.OffsetOf<RectInstance>(nameof(RectInstance.Kind)) == 44
            && Marshal.OffsetOf<RectInstance>(nameof(RectInstance.Body)) == 48
            && Marshal.OffsetOf<RectInstance>(nameof(RectInstance.Accent)) == 60);
        Check("an icon instance is the design's 24 bytes", Marshal.SizeOf<IconInstance>() == IconInstance.Size);

        using var list = new InstanceList<RectInstance>(16);
        for (var index = 0; index < 5_000; index++)
        {
            list.Add().Body = (uint)index;
        }

        var intact = list.Count == 5_000 && list.Capacity >= 5_000;
        for (var index = 0; index < list.Count && intact; index++)
        {
            intact = list.AsSpan()[index].Body == (uint)index;
        }

        Check($"an instance list grows by doubling and keeps what it holds ({list.Capacity:N0} slots)", intact);
        var capacity = list.Capacity;
        list.Clear();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 5_000; index++)
        {
            list.Add().Body = (uint)index;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check($"refilled after Clear it keeps its memory and allocates nothing ({allocated} bytes)",
            list.Count == 5_000 && list.Capacity == capacity && allocated == 0);

        Check("a radius fits half the shorter side", GpuSink.Fit(6, 100, 8) == 4 && GpuSink.Fit(6, 100, 100) == 6);
        Check("and below 1.5 pixels corners are square, as the raster draws them",
            GpuSink.Fit(1.4, 100, 100) == 0 && GpuSink.Fit(6, 100, 2) == 0 && GpuSink.Fit(1.5, 100, 100) == 1.5);
    }

    private static void GpuShaderCacheChecks()
    {
        var folder = Path.Combine(Path.GetTempPath(), "UltraExplorer-shaders-" + Guid.NewGuid().ToString("N"));
        try
        {
            CompiledShaders first;
            try
            {
                first = ShaderCache.Load(folder);
            }
            catch (GpuUnavailableException ex)
            {
                Check($"the canvas shaders compile ({ex.Message})", false);
                return;
            }

            var file = Path.Combine(folder, ShaderCache.CacheFileName);
            Check($"the canvas shaders compile with the system compiler ({first.Milliseconds:F1} ms)",
                !first.FromCache && ShaderCache.Entries.All(entry => first[entry.Name].Length > 0));
            Check("and are kept in the cache folder under a name that hashes the source", File.Exists(file)
                && ShaderCache.CacheFileName.StartsWith("shaders-", StringComparison.Ordinal));

            var second = ShaderCache.Load(folder);
            Check($"a second start reads them instead of compiling ({second.Milliseconds:F2} ms)",
                second.FromCache && ShaderCache.Entries.All(entry => first[entry.Name].AsSpan().SequenceEqual(second[entry.Name])));

            var bytes = File.ReadAllBytes(file);
            bytes[^5] ^= 0x5A;
            File.WriteAllBytes(file, bytes);
            var third = ShaderCache.Load(folder);
            Check("a damaged cache file is compiled over, not trusted",
                !third.FromCache && ShaderCache.Entries.All(entry => first[entry.Name].AsSpan().SequenceEqual(third[entry.Name]))
                && ShaderCache.Load(folder).FromCache);

            // The first name's length made no number at all (magic, version
            // and count come before it): BinaryReader's FormatException is
            // damage like any other, not an error that leaves the GPU unused.
            bytes = File.ReadAllBytes(file);
            bytes.AsSpan(12, 5).Fill(0xFF);
            File.WriteAllBytes(file, bytes);
            var fourth = ShaderCache.Load(folder);
            Check("a cache file whose names cannot be read is compiled over too", !fourth.FromCache && ShaderCache.Load(folder).FromCache);
            Check("nothing is left behind but the cache file", Directory.GetFiles(folder).Length == 1);
        }
        finally
        {
            TryDelete(folder);
        }
    }

    // ---- shapes of every kind, both ways ------------------------------------------------

    /// <summary>
    /// A few thousand cells, tiles, specks and far-off shapes, generated to hit
    /// what the raster is particular about: edges on exact halves (which round
    /// to even), corners too big for their cell, title bands shorter than
    /// their corners, stripes, and edges trillions of pixels away.  Painted by
    /// the raster and by the GPU through the same sink calls.
    /// </summary>
    private static void GpuSyntheticParityChecks(GpuDeviceSet set)
    {
        const int width = 640;
        const int height = 480;
        const uint background = 0xFF111315;
        var calls = SyntheticScene(new Random(20260926), width, height);

        var cpu = new uint[width * height];
        var handle = GCHandle.Alloc(cpu, GCHandleType.Pinned);
        try
        {
            var raster = new NestedRaster();
            raster.Attach(handle.AddrOfPinnedObject(), width, height, width * 4);
            var sink = new RasterSink(raster);
            sink.Clear(background);
            foreach (var call in calls)
            {
                call(sink);
            }

            raster.Detach();
        }
        finally
        {
            handle.Free();
        }

        var renderer = NestedGpuRenderer.For(set, _rectShaders!);
        var name = set.IsWarp ? "WARP" : set.AdapterName;
        renderer.WarmUp();
        Check($"the warm-up frame draws every kind of rectangle on {name}", set.WaitForGpu(5_000, out _) && renderer.LastInstances == 4);

        using var frame = new NestedGpuFrame();
        using var target = set.CreateOffscreenTarget(width, height);
        var gpuSink = new GpuSink();
        gpuSink.Begin(frame.SceneRects, width, height, background);
        gpuSink.Clear(background);
        foreach (var call in calls)
        {
            call(gpuSink);
        }

        frame.ClearColour = gpuSink.ClearColour;
        frame.SceneChanged();
        renderer.Draw(target.RenderTargetView, width, height, frame);
        var gpu = target.ReadPixels();

        var result = CompareScene(cpu, gpu, width, height, frame.SceneRects.AsSpan());
        Check($"{calls.Count:N0} synthetic shapes on {name}: {result.StraightShare:P3} of pixels off the corners within ±{ParityTolerance} (worst {result.WorstStraight})",
            result.StraightShare >= 0.995);
        Check($"and every corner pixel within ±{CornerTolerance} ({result.CornerPixels:N0} corner pixels, worst {result.WorstCorner}, {result.CornerShare:P2} within ±{ParityTolerance})",
            result.WorstCorner <= CornerTolerance);
        if (result.FirstMiss.Length > 0)
        {
            Console.WriteLine($"        {result.FirstMiss}");
        }

        if (result.WorstCorner > CornerTolerance)
        {
            Console.WriteLine($"        {result.WorstCornerAt}");
        }

        SaveShots($"gpu-synthetic-{(set.IsWarp ? "warp" : "hardware")}", cpu, gpu, width, height);
        GpuLabelRectChecks(set, renderer, name);

        // The sink itself, a second time over the same shapes: nothing new.
        gpuSink.Begin(frame.SceneRects, width, height, background);
        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var call in calls)
        {
            call(gpuSink);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check($"emitting {frame.SceneRects.Count:N0} instances into a warm list allocates nothing ({allocated} bytes)", allocated == 0);

        const int rounds = 200;
        var watch = Stopwatch.StartNew();
        for (var round = 0; round < rounds; round++)
        {
            gpuSink.Begin(frame.SceneRects, width, height, background);
            foreach (var call in calls)
            {
                call(gpuSink);
            }
        }

        watch.Stop();
        var nanoseconds = watch.Elapsed.TotalMilliseconds * 1e6 / (rounds * (double)calls.Count);
        Check($"a shape costs the sink {nanoseconds:F0} ns (budget 2000 ns, Debug included)", nanoseconds < 2_000);
    }

    /// <summary>
    /// The label layer's kind of rectangle: the pill behind a small folder's
    /// name, translucent, with edges between pixels.  Laid over the scene in
    /// its own draw, it must blend exactly as WPF's premultiplied colour does
    /// inside, fade over the half pixel at an edge, and leave the scene alone
    /// outside.
    /// </summary>
    private static void GpuLabelRectChecks(GpuDeviceSet set, NestedGpuRenderer renderer, string name)
    {
        const uint background = 0xFF505A64;
        const uint pill = 0xD8141618;
        using var frame = new NestedGpuFrame();
        using var target = set.CreateOffscreenTarget(64, 32);
        frame.ClearColour = background;
        frame.SceneChanged();
        ref var rect = ref frame.LabelRects.Add();
        rect = default;
        rect.Outer = new System.Numerics.Vector4(8.5f, 6f, 56.25f, 26f);
        rect.Radius = 3;
        rect.Kind = (uint)RectKind.Rounded;
        rect.Body = pill;
        frame.LabelsChanged();
        renderer.Draw(target.RenderTargetView, 64, 32, frame);
        var pixels = target.ReadPixels();

        static uint Over(uint under, uint over, double coverage)
        {
            var alpha = (over >> 24) / 255.0 * coverage;
            uint Channel(int shift) => (uint)Math.Round(((over >> shift) & 0xFF) * alpha + ((under >> shift) & 0xFF) * (1 - alpha));
            return 0xFF000000u | Channel(16) << 16 | Channel(8) << 8 | Channel(0);
        }

        bool Near(uint actual, uint expected) =>
            Enumerable.Range(0, 4).All(channel => Math.Abs((int)((actual >> (channel * 8)) & 0xFF) - (int)((expected >> (channel * 8)) & 0xFF)) <= 1);

        Check($"a translucent label rectangle blends over the scene as WPF's premultiplied colour does on {name}",
            Near(OffscreenTarget.PixelAt(pixels, 64, 30, 15), Over(background, pill, 1)));
        Check("its edge between two pixels covers the pixel half", Near(OffscreenTarget.PixelAt(pixels, 64, 8, 15), Over(background, pill, 0.5)));
        Check("and outside it the scene is untouched", OffscreenTarget.PixelAt(pixels, 64, 7, 15) == background
            && OffscreenTarget.PixelAt(pixels, 64, 30, 4) == background && OffscreenTarget.PixelAt(pixels, 64, 57, 15) == background);
    }

    private static List<Action<SceneSink>> SyntheticScene(Random random, int width, int height)
    {
        var calls = new List<Action<SceneSink>>();
        double Coordinate(double range)
        {
            // A third of the edges on exact halves, which Math.Round sends to
            // the even neighbour: the body's edge, Px(left + 1), is then 0 or
            // 2 pixels in rather than 1.
            var value = random.NextDouble() * range - 40;
            return random.Next(3) == 0 ? Math.Floor(value) + 0.5 : value;
        }

        // Something far out first, the way a parent covers a deep view.
        calls.Add(sink => sink.Cell(-4e12, -3e12, 5e12, 6e12, 6, -3e12 + 4e11, true, -100_000_000, -100_000_000, -99_999_990, 100_000_000, 0xFF40454D, 0xFF1D2126, 0xFF262B31, 0xFFE3B341));
        for (var index = 0; index < 1_500; index++)
        {
            var kind = random.Next(10);
            var scale = new[] { 1.0, 1.25, 1.5, 2.0 }[random.Next(4)];
            var left = Coordinate(width + 40);
            var top = Coordinate(height + 40);
            // Colours from the canvas's own range - dark tints, rims a little
            // lighter - since how far a corner pixel may be off depends on how
            // far apart the colours on either side of the curve are.  Stripes
            // and specks take anything; they have no curves.
            var hue = random.NextDouble() * 360;
            var saturation = random.NextDouble() * 0.32;
            var rim = NestedRaster.FromHsl(hue, saturation, 0.16 + random.NextDouble() * 0.14);
            var body = NestedRaster.FromHsl(hue, saturation, 0.07 + random.NextDouble() * 0.1);
            var header = NestedRaster.FromHsl(hue, saturation, 0.12 + random.NextDouble() * 0.1);
            var accent = 0xFF000000u | (uint)random.Next(0x404040, 0xFFFFFF);
            if (kind < 5)
            {
                // A cell, sized and styled the way PaintCell does it.
                var pixelWidth = random.Next(3) == 0 ? 4 + random.NextDouble() * 60 : 40 + random.NextDouble() * 400;
                var pixelHeight = pixelWidth / 1.6;
                var right = left + pixelWidth;
                var bottom = top + pixelHeight;
                var radius = pixelWidth >= 40 ? Math.Min(6 * scale, pixelWidth * 0.03) : 0;
                if (random.Next(8) == 0)
                {
                    radius = random.NextDouble() * 12;
                }

                var headerHeight = random.Next(6) == 0 ? random.NextDouble() * 4 : pixelWidth * 0.075 * scale;
                var headerBottom = double.NaN;
                var hasStripe = false;
                int stripeLeft = 0, stripeTop = 0, stripeRight = 0, stripeBottom = 0;
                if (headerHeight >= 2)
                {
                    headerBottom = top + headerHeight;
                    if (headerHeight >= 6 && pixelWidth >= 30)
                    {
                        var inset = Math.Max(1, headerHeight * 0.2);
                        var stripe = Math.Max(2, Math.Min(4 * scale, headerHeight * 0.12));
                        var stripeStart = left + 1 + Math.Max(2 * scale, headerHeight * 0.18);
                        hasStripe = true;
                        stripeLeft = NestedRaster.Px(stripeStart);
                        stripeTop = NestedRaster.Px(top + inset);
                        stripeRight = NestedRaster.Px(stripeStart + stripe);
                        stripeBottom = NestedRaster.Px(top + headerHeight - inset);
                    }
                }

                calls.Add(sink => sink.Cell(left, top, right, bottom, radius, headerBottom, hasStripe, stripeLeft, stripeTop, stripeRight, stripeBottom, rim, body, header, accent));
            }
            else if (kind < 8)
            {
                // A file's tile, as PaintFile sizes it.
                var tileHeight = 3 + random.NextDouble() * 40;
                var tileWidth = tileHeight * 5.5;
                var right = left + tileWidth;
                var bottom = top + tileHeight;
                var stripeWidth = Math.Max(1, Math.Min(3 * scale, tileHeight * 0.14));
                var inset = Math.Max(1, tileHeight * 0.18);
                var radius = Math.Min(3 * scale, tileHeight * 0.2);
                calls.Add(sink => sink.File(left, top, right, bottom, radius,
                    NestedRaster.Px(left + 1), NestedRaster.Px(top + inset), NestedRaster.Px(left + 1 + stripeWidth), NestedRaster.Px(bottom - inset),
                    body, accent));
            }
            else
            {
                // A speck or a wash.
                var x0 = NestedRaster.PxFloor(left);
                var y0 = NestedRaster.PxFloor(top);
                var x1 = x0 + random.Next(0, kind == 8 ? 4 : 200);
                var y1 = y0 + random.Next(0, kind == 8 ? 4 : 60);
                calls.Add(sink => sink.Fill(x0, y0, x1, y1, rim));
            }
        }

        return calls;
    }

    // ---- the canvas's own views, both ways -------------------------------------------------

    private static async Task GpuCanvasParityChecksAsync(IReadOnlyList<GpuDeviceSet> sets)
    {
        var disk = BuildNestedWorld(out var chain);
        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots(
        [
            new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive, "1.2 TB free"),
            new NestedRoot(@"R:\", "R:", NestedFolderKind.Drive, "80 GB free")
        ]);
        var onChain = new HashSet<string>(chain, StringComparer.OrdinalIgnoreCase);
        await LoadEverythingAsync(tree, folder =>
            !folder.FullPath.StartsWith(@"Q:\chain\", StringComparison.OrdinalIgnoreCase) || onChain.Contains(folder.FullPath));

        var canvas = new NestedCanvas { Tree = tree };
        canvas.Measure(new Size(ViewWidth, ViewHeight));
        canvas.Arrange(new Rect(0, 0, ViewWidth, ViewHeight));
        canvas.UpdateLayout();

        canvas.FitAll(animated: false);
        Render(canvas);
        Check($"with no window the canvas draws its scene on the CPU ({canvas.RendererReason})",
            !canvas.IsSceneOnGpu && canvas.RendererReason == GpuBootstrap.ReasonNotOnScreen && canvas.SceneBitmap is not null);

        var files = tree.Find(@"Q:\files")!;
        var views = new (string Name, Action Set)[]
        {
            ("fit-all", () => canvas.FitAll(animated: false)),
            ("mid-zoom", () => canvas.FlyTo(tree.Find(@"Q:\mixed")!, 0.9, animated: false)),
            ("files", () => canvas.FlyTo(files, 0.9, animated: false)),
            ("between-folders", () =>
            {
                canvas.FitAll(animated: false);
                canvas.ZoomAt(new Point(533.5, 377.25), 6);
            }),
            ("forty-levels-down", () => canvas.FlyTo(tree.Find(chain[^1])!, 0.9, animated: false)),
            ("filtered-with-hidden", () =>
            {
                tree.IncludeHidden = true;
                canvas.SetFilter("report");
                canvas.FlyTo(files, 0.9, animated: false);
            })
        };

        try
        {
            foreach (var set in sets)
            {
                var renderer = NestedGpuRenderer.For(set, _rectShaders!);
                var name = set.IsWarp ? "WARP" : set.AdapterName;
                foreach (var scale in new[] { 1.0, 1.5 })
                {
                    canvas.DpiOverride = new DpiScale(scale, scale);
                    var worstStraight = 1.1;
                    var worstCorner = 0;
                    var worstView = string.Empty;
                    var firstMiss = string.Empty;
                    var instances = 0;
                    foreach (var (view, setView) in views)
                    {
                        tree.IncludeHidden = false;
                        canvas.SetFilter(null);
                        setView();
                        Render(canvas);
                        var width = canvas.ScenePixelWidth;
                        var height = canvas.ScenePixelHeight;
                        if (canvas.SceneBitmap is not { } bitmap)
                        {
                            Check($"the raster drew {view} at {scale:0.##}x", false);
                            continue;
                        }

                        var cpu = new uint[width * height];
                        bitmap.CopyPixels(new Int32Rect(0, 0, width, height), cpu, width * 4, 0);

                        using var frame = new NestedGpuFrame();
                        using var target = set.CreateOffscreenTarget(width, height);
                        canvas.RenderOffscreen(renderer, target, frame);
                        var gpu = target.ReadPixels();
                        instances += frame.SceneRects.Count;

                        var result = CompareScene(cpu, gpu, width, height, frame.SceneRects.AsSpan());
                        if (result.StraightShare <= worstStraight)
                        {
                            worstStraight = result.StraightShare;
                            worstView = view;
                        }

                        worstCorner = Math.Max(worstCorner, result.WorstCorner);
                        if (firstMiss.Length == 0 && result.FirstMiss.Length > 0)
                        {
                            firstMiss = $"{view}: {result.FirstMiss}";
                        }

                        if (scale == 1.5 || view == "fit-all")
                        {
                            SaveShots($"gpu-{(set.IsWarp ? "warp" : "hardware")}-{scale.ToString("0.0", CultureInfo.InvariantCulture)}-{view}", cpu, gpu, width, height);
                        }
                    }

                    Check($"the canvas's {views.Length} views at {scale:0.##}x on {name}: at worst {worstStraight:P3} of pixels off the corners within ±{ParityTolerance} ({worstView}; {instances:N0} instances)",
                        worstStraight >= 0.995);
                    Check($"and every corner pixel within ±{CornerTolerance} (worst {worstCorner})", worstCorner <= CornerTolerance);
                    if (firstMiss.Length > 0)
                    {
                        Console.WriteLine($"        {firstMiss}");
                    }
                }

                // A frame of the scene, once warm: the walk into the sink and
                // the draw.  Nothing of it may reach the garbage collector.
                canvas.DpiOverride = new DpiScale(1, 1);
                tree.IncludeHidden = false;
                canvas.SetFilter(null);
                canvas.FitAll(animated: false);
                Render(canvas);
                using (var frame = new NestedGpuFrame())
                using (var target = set.CreateOffscreenTarget(canvas.ScenePixelWidth, canvas.ScenePixelHeight))
                {
                    for (var warm = 0; warm < 3; warm++)
                    {
                        canvas.RenderOffscreen(renderer, target, frame);
                    }

                    set.WaitForGpu(5_000, out _);
                    var watch = Stopwatch.StartNew();
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    const int frames = 20;
                    for (var index = 0; index < frames; index++)
                    {
                        canvas.RenderOffscreen(renderer, target, frame);
                    }

                    var allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / frames;
                    set.WaitForGpu(5_000, out _);
                    watch.Stop();
                    Check($"a fit-all scene frame on {name} - walk, {frame.SceneRects.Count:N0} instances, upload and draw - allocates nothing ({allocated} bytes per frame, {watch.Elapsed.TotalMilliseconds / frames:F2} ms)",
                        allocated == 0);
                }
            }
        }
        finally
        {
            canvas.DpiOverride = null;
            canvas.SetFilter(null);
            tree.IncludeHidden = false;
            canvas.Tree = null;
        }
    }

    // ---- comparing -------------------------------------------------------------------------

    private readonly record struct SceneComparison(
        double StraightShare,
        int WorstStraight,
        int CornerPixels,
        int WorstCorner,
        double CornerShare,
        string FirstMiss,
        string WorstCornerAt);

    /// <summary>
    /// Every pixel of the raster's picture against the GPU's.  Pixels in the
    /// rounded corners of any cell or tile - the squares a corner's curve and
    /// the body's and band's curves one or two pixels in can reach - are
    /// counted apart, since that is where the two measure coverage
    /// differently; everywhere else they should agree to the rounding of a
    /// blend.
    /// </summary>
    private static SceneComparison CompareScene(uint[] cpu, byte[] gpu, int width, int height, ReadOnlySpan<RectInstance> instances)
    {
        var corner = new bool[width * height];
        foreach (var instance in instances)
        {
            var kind = (RectKind)(instance.Kind & 0xFF);
            if (kind is not (RectKind.Cell or RectKind.File) || instance.Radius <= 0)
            {
                continue;
            }

            var side = (int)Math.Ceiling(instance.Radius) + 2;
            var (left, top, right, bottom) = ((int)instance.Outer.X, (int)instance.Outer.Y, (int)instance.Outer.Z, (int)instance.Outer.W);
            MarkSquare(corner, width, height, left, top, side);
            MarkSquare(corner, width, height, right - side, top, side);
            MarkSquare(corner, width, height, left, bottom - side, side);
            MarkSquare(corner, width, height, right - side, bottom - side, side);
        }

        long straight = 0, straightGood = 0, corners = 0, cornersGood = 0;
        int worstStraight = 0, worstCorner = 0;
        var firstMiss = string.Empty;
        var worstCornerAt = string.Empty;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                var expected = cpu[index];
                var actual = OffscreenTarget.PixelAt(gpu, width, x, y);
                var difference = 0;
                for (var shift = 0; shift < 32; shift += 8)
                {
                    difference = Math.Max(difference, Math.Abs((int)((expected >> shift) & 0xFF) - (int)((actual >> shift) & 0xFF)));
                }

                if (corner[index])
                {
                    corners++;
                    cornersGood += difference <= ParityTolerance ? 1 : 0;
                    if (difference > worstCorner)
                    {
                        worstCorner = difference;
                        worstCornerAt = $"worst corner pixel at ({x}, {y}): raster #{expected:X8}, GPU #{actual:X8}{DescribeCovering(instances, x, y)}";
                    }
                }
                else
                {
                    straight++;
                    if (difference <= ParityTolerance)
                    {
                        straightGood++;
                    }
                    else if (firstMiss.Length == 0)
                    {
                        firstMiss = $"first pixel off at ({x}, {y}): raster #{expected:X8}, GPU #{actual:X8}";
                    }

                    worstStraight = Math.Max(worstStraight, difference);
                }
            }
        }

        return new SceneComparison(
            straight == 0 ? 1 : (double)straightGood / straight,
            worstStraight,
            (int)corners,
            worstCorner,
            corners == 0 ? 1 : (double)cornersGood / corners,
            firstMiss,
            worstCornerAt);
    }

    /// <summary>The last few instances whose quad covers a pixel, painted last first: what to look at when a pixel is off.</summary>
    private static string DescribeCovering(ReadOnlySpan<RectInstance> instances, int x, int y)
    {
        var text = new System.Text.StringBuilder();
        var found = 0;
        for (var index = instances.Length - 1; index >= 0 && found < 3; index--)
        {
            var instance = instances[index];
            if (x + 0.5 >= instance.Outer.X && x + 0.5 < instance.Outer.Z && y + 0.5 >= instance.Outer.Y && y + 0.5 < instance.Outer.W)
            {
                found++;
                text.Append(CultureInfo.InvariantCulture,
                    $"{Environment.NewLine}          #{index} kind {instance.Kind & 0xFF} flags {instance.Kind >> 8:X} outer {instance.Outer} feature {instance.Feature} band {instance.HeaderBottom} radius {instance.Radius} inner {BitConverter.UInt16BitsToHalf((ushort)instance.InnerRadii)}/{BitConverter.UInt16BitsToHalf((ushort)(instance.InnerRadii >> 16))} body #{instance.Body:X8} rim #{instance.Rim:X8}");
            }
        }

        return text.ToString();
    }

    private static void MarkSquare(bool[] mask, int width, int height, int left, int top, int side)
    {
        for (var y = Math.Max(0, top); y < Math.Min(height, top + side); y++)
        {
            for (var x = Math.Max(0, left); x < Math.Min(width, left + side); x++)
            {
                mask[y * width + x] = true;
            }
        }
    }

    /// <summary>With NESTED_SHOTS set, keeps the raster's picture, the GPU's, and their difference (amplified) as PNGs.</summary>
    private static void SaveShots(string name, uint[] cpu, byte[] gpu, int width, int height)
    {
        if (Environment.GetEnvironmentVariable("NESTED_SHOTS") is not { Length: > 0 } folder)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        var cpuBytes = new byte[width * height * 4];
        Buffer.BlockCopy(cpu, 0, cpuBytes, 0, cpuBytes.Length);
        var difference = new byte[width * height * 4];
        for (var index = 0; index < difference.Length; index += 4)
        {
            var most = 0;
            for (var channel = 0; channel < 3; channel++)
            {
                most = Math.Max(most, Math.Abs(cpuBytes[index + channel] - gpu[index + channel]));
            }

            var shade = (byte)Math.Min(255, most * 8);
            difference[index] = shade;
            difference[index + 1] = shade;
            difference[index + 2] = most > ParityTolerance ? (byte)255 : shade;
            difference[index + 3] = 255;
        }

        foreach (var (suffix, pixels) in new[] { ("cpu", cpuBytes), ("gpu", gpu), ("diff", difference) })
        {
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(folder, $"{name}-{suffix}.png");
            using var stream = File.Create(path);
            encoder.Save(stream);
            ShotsWritten.Add(path);
        }
    }
}
