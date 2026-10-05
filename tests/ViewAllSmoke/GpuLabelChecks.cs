using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The names on the GPU, headless: the icon and glyph pipelines on their own
/// (a glyph's solid stem is the text colour exactly, an icon lays over the
/// cells premultiplied, with no dark fringe), then the canvas's own views with
/// their names drawn both ways - WPF's label layer over the raster, and the
/// GPU's scene and label instances offscreen - on WARP and on the card behind
/// the primary monitor (offscreen only; nothing is shown).  Text is never the
/// same pixel for pixel - one is WPF's rasteriser, the other a distance field
/// - so the checks hold the two to the same ink, in the same places, with the
/// same names drawn and grabbable.  Then: a frame of names allocates nothing,
/// and what is not ready - a name still being shaped, a glyph not yet made,
/// an icon not yet found - is left out for the frame and drawn once it
/// arrives.  NESTED_SHOTS keeps the pictures.
/// </summary>
internal static partial class Program
{
    private static Task GpuLabelChecks()
    {
        Section("gpu labels");
        GpuLabelLayoutChecks();
        GpuLabelMarkLookupChecks();

        CompiledShaders shaders;
        try
        {
            shaders = ShaderCache.Load(null);
        }
        catch (GpuUnavailableException ex)
        {
            Check($"the label shaders compile ({ex.Message})", false);
            return Task.CompletedTask;
        }

        var faces = FaceRegistry.Shared;
        using var glyphs = new GlyphAtlas(faces);
        var warm = glyphs.WarmUp(null);
        Check($"a glyph atlas for the checks is warm ({warm.Glyphs} glyphs in {warm.Elapsed.TotalMilliseconds:F0} ms)", warm.Glyphs > 1000);

        var sets = new List<GpuDeviceSet>();
        try
        {
            sets.Add(GpuDeviceSet.CreateWarp());
        }
        catch (Exception ex)
        {
            Check($"a WARP set draws the label checks ({ex.Message})", false);
        }

        if (GpuDeviceSet.EnumerateAdapters().Any(adapter => !adapter.IsSoftware)
            && GpuBootstrap.AdapterLuidForMonitor(GpuBootstrap.PrimaryMonitor) is { } luid)
        {
            try
            {
                sets.Add(GpuDeviceSet.Create(luid, shareWithWpf: false));
            }
            catch (GpuUnavailableException ex)
            {
                Console.WriteLine($"  note  the primary monitor's card cannot draw offscreen here ({ex.Message}); WARP only");
            }
        }

        var source = new LabelIconSource();
        using var icons = new IconAtlas(new IconAtlasOptions { CachePath = null, AutoSaveInterval = TimeSpan.Zero, Source = source.Find });
        try
        {
            foreach (var set in sets)
            {
                GpuLabelPipelineChecks(set, shaders, faces, glyphs, icons);
                GpuTextZoomPixels(set, shaders, faces, glyphs);
            }

            RunOnSta("gpu label parity", () => GpuLabelCanvasChecksAsync(sets, shaders, faces, glyphs));
            RunOnSta("gpu label misses", () => GpuLabelMissChecksAsync(shaders, faces));
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

    private static void GpuLabelLayoutChecks()
    {
        Check("the shader cache compiles the icon and glyph pipelines with the rectangles'",
            new[] { "RectVS", "RectPS", "IconVS", "IconPS", "GlyphVS", "GlyphPS" }.All(name => ShaderCache.Entries.Any(entry => entry.Name == name)));
        Check("an icon instance is 24 bytes and a glyph quad 40, as their input layouts read them",
            System.Runtime.InteropServices.Marshal.SizeOf<IconInstance>() == 24 && System.Runtime.InteropServices.Marshal.SizeOf<GlyphQuad>() == 40);
        var ratios = GpuTextTuning.GammaRatios(1.8f);
        Check("DirectWrite's alpha correction for gamma 1.8 is its table's row, quartered",
            Math.Abs(ratios.X - 0.1469f / 4) < 1e-6 && Math.Abs(ratios.Y + 0.8911f / 4) < 1e-6 && Math.Abs(ratios.Z - 1.4644f / 4) < 1e-6 && Math.Abs(ratios.W + 0.3234f / 4) < 1e-6);
        var between = GpuTextTuning.GammaRatios(1.85f);
        Check("and between two rows it runs straight from one to the other",
            Math.Abs(between.X - (0.1469f + 0.1627f) / 8) < 1e-5);
        Check($"the text is tuned as the gate found (gamma {GpuTextTuning.Gamma}, contrast {GpuTextTuning.Contrast}, sharpness {GpuTextTuning.Sharpness}, bias x{GpuTextTuning.BiasScale})",
            Environment.GetEnvironmentVariable(GpuTextTuning.Variable) is { Length: > 0 }
            || GpuTextTuning.Gamma == GpuTextTuning.DefaultGamma && GpuTextTuning.Sharpness == GpuTextTuning.DefaultSharpness);
    }

    /// <summary>
    /// The canvas asks for the mark of every folder it draws, tens of
    /// thousands the first time a big folder comes into view - and with the
    /// cells on the GPU they are no longer thinned out while the camera
    /// moves.  A path already in the form marks are kept under is looked up
    /// as it is: that may only be said of a path normalising would not change.
    /// </summary>
    private static void GpuLabelMarkLookupChecks()
    {
        var paths = new List<string> { @"C:\", @"c:\windows", @"C:\Windows\System32", @"D:\a b\c-d_e (1)\f.g.h" };
        foreach (var directory in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.System) })
        {
            try
            {
                paths.AddRange(Directory.EnumerateFileSystemEntries(directory).Take(400));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        var claimed = paths.Where(FolderMarkService.IsKeyForm).ToList();
        var wrong = claimed.Where(path => !string.Equals(ViewAllPath.Normalize(path), path, StringComparison.OrdinalIgnoreCase)).ToList();
        Check($"a path taken as it is for a mark lookup is one normalising leaves alone ({claimed.Count} of {paths.Count} taken, {wrong.Count} wrong{(wrong.Count > 0 ? ": " + wrong[0] : "")})",
            claimed.Count > paths.Count / 2 && wrong.Count == 0);

        var quoted = '"' + @"C:\a" + '"';
        string[] longWay = [@"C:\Windows\", @"C:/Windows", @"C:\a\.\b", @"C:\a\..\b", @"C:\a.", @"C:\a ", @"C:\con", @"C:\x\COM1.txt", @"C:\x\lpt9",
            @"%WINDIR%\x", @"\\server\share\x", @"C:\a\\b", @"C:\a:stream", quoted, @"C:\a?", "C:", "C:x", @" C:\a"];
        Check("and one it could change - trailing or doubled separators, slashes, dots, spaces, devices, variables, streams, quotes, shares - goes the long way",
            longWay.All(path => !FolderMarkService.IsKeyForm(path)));

        var marks = new FolderMarkService(Path.Combine(Path.GetTempPath(), "marks-" + Guid.NewGuid().ToString("N") + ".json"));
        Check("with nothing marked, nothing is found", marks.Get(@"C:\Windows").IsEmpty);
        marks.SetAccent(@"C:\Marked\Folder", "#E3B341");
        Check("a marked folder is found however its path is written, and nothing else is",
            new[] { @"C:\Marked\Folder", @"c:\marked\folder", @"C:\Marked\Folder\", @"C:/Marked/Folder", @"C:\Marked\.\Folder" }.All(path => marks.Get(path).AccentHex == "#E3B341")
            && marks.Get(@"C:\Marked").IsEmpty && marks.Get(@"C:\Marked\Folder\Inside").IsEmpty);
    }

    // ---- the pipelines on their own ------------------------------------------------------

    private static void GpuLabelPipelineChecks(GpuDeviceSet set, CompiledShaders shaders, FaceRegistry faces, GlyphAtlas glyphs, IconAtlas icons)
    {
        const int width = 256;
        const int height = 96;
        const uint background = 0xFF202428;
        var name = set.IsWarp ? "WARP" : set.AdapterName;
        var renderer = NestedGpuRenderer.For(set, shaders);
        var target = new GpuLabelTarget(faces, new TextShaper(faces), glyphs, icons);
        using var frame = new NestedGpuFrame(16, 16, 256);
        using var offscreen = set.CreateOffscreenTarget(width, height);
        frame.ClearColour = background;
        frame.SceneChanged();

        // The icon is found on the atlas's worker: frames until it is there.
        // A file of a made-up type, in a drive's root, as the canvas names it.
        var iconFolder = new NestedFolder(@"Q:\", "Q:", NestedFolderKind.Drive, null);
        var iconFile = new NestedFile("icon.fake", false, 0);
        var iconBounds = new Rect(180, 20, 32, 32);
        var drawnIcon = false;
        var clock = Stopwatch.StartNew();
        LabelText text = default;
        while (clock.ElapsedMilliseconds < 10_000)
        {
            target.Begin(frame, set, width, height, 1, 1, snap: true);
            text = target.Text("Il", 40, Color.FromRgb(0xF2, 0xF2, 0xF2), double.PositiveInfinity, LabelFace.Regular, scaled: true);
            target.DrawText(text, new Point(10, 10));
            drawnIcon = target.DrawIcon(iconFolder, 0, iconFile, iconBounds);
            target.End();
            if (drawnIcon)
            {
                break;
            }

            Thread.Sleep(5);
        }

        renderer.Draw(offscreen.RenderTargetView, width, height, frame);
        var pixels = offscreen.ReadPixels();
        Check($"a name and an icon draw through the glyph and icon pipelines on {name} ({renderer.LastGlyphs} glyphs, {renderer.LastIcons} icon)",
            drawnIcon && renderer.LastGlyphs == 2 && renderer.LastIcons == 1);

        // The glyphs: solid where the stems are, nothing outside their box.
        var solid = 0;
        var outside = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < 150; x++)
            {
                var pixel = OffscreenTarget.PixelAt(pixels, width, x, y);
                var inBox = x >= 9 && x <= 11 + text.Width && y >= 9 && y <= 11 + text.Height;
                if (pixel == 0xFFF2F2F2)
                {
                    solid++;
                }

                if (!inBox && pixel != background)
                {
                    outside++;
                }
            }
        }

        Check($"a 40 px stem is the text colour exactly ({solid} solid pixels) and nothing is drawn outside the name's box ({outside})", solid >= 100 && outside == 0);

        // The icon: its colour in the middle, the cells untouched in its clear
        // border, and every pixel of the edge between the two - premultiplied
        // data blended as premultiplied, no dark fringe.
        var icon = LabelIconSource.Colour;
        var centre = OffscreenTarget.PixelAt(pixels, width, 196, 36);
        var corner = OffscreenTarget.PixelAt(pixels, width, 180, 20);
        var fringe = true;
        for (var x = 180; x < 212; x++)
        {
            var pixel = OffscreenTarget.PixelAt(pixels, width, x, 36);
            for (var shift = 0; shift < 24; shift += 8)
            {
                var low = Math.Min((background >> shift) & 0xFF, (icon >> shift) & 0xFF);
                var high = Math.Max((background >> shift) & 0xFF, (icon >> shift) & 0xFF);
                var value = (pixel >> shift) & 0xFF;
                fringe &= value + 2 >= low && value <= high + 2;
            }
        }

        Check($"the icon is its colour inside (#{centre:X8}), the cell's in its clear border (#{corner:X8}), and its edge blends without a dark fringe",
            GpuLabelNear(centre, icon, 2) && GpuLabelNear(corner, background, 2) && fringe);

        // A frame whose names were drawn again leaves the scene's upload alone.
        frame.LabelsChanged();
        renderer.Draw(offscreen.RenderTargetView, width, height, frame);
        var labelsOnly = renderer.LastUploadBytes;
        renderer.Draw(offscreen.RenderTargetView, width, height, frame);
        var nothing = renderer.LastUploadBytes;
        Check($"a frame of new names uploads only the names ({labelsOnly} bytes), an unchanged one nothing ({nothing})",
            labelsOnly == frame.LabelRects.ByteCount + frame.Icons.ByteCount + frame.Glyphs.ByteCount && nothing == 0);
        SaveLabelShot($"gpu-labels-pipeline-{(set.IsWarp ? "warp" : "hardware")}", pixels, width, height);
    }

    // ---- the canvas's views, both ways ------------------------------------------------------

    private static async Task GpuLabelCanvasChecksAsync(IReadOnlyList<GpuDeviceSet> sets, CompiledShaders shaders, FaceRegistry faces, GlyphAtlas glyphs)
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
                var renderer = NestedGpuRenderer.For(set, shaders);
                var name = set.IsWarp ? "WARP" : set.AdapterName;

                // No icons on either side: the canvas has no Shell icons here,
                // so neither draws them and the names start at the same place.
                var labels = new GpuLabelTarget(faces, new TextShaper(faces), glyphs, icons: null);
                foreach (var scale in new[] { 1.0, 1.5 })
                {
                    canvas.DpiOverride = new DpiScale(scale, scale);
                    var worstInk = 0.0;
                    var worstPlace = 1.0;
                    var worstMean = 0.0;
                    var grabsAgree = true;
                    var complete = true;
                    var summary = string.Empty;
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

                        var sceneOnly = new uint[width * height];
                        bitmap.CopyPixels(new Int32Rect(0, 0, width, height), sceneOnly, width * 4, 0);
                        var wpf = GpuLabelWpfPicture(canvas, width, height, scale);
                        var wpfGrabs = canvas.LabelGrabCount;

                        using var frame = new NestedGpuFrame();
                        using var target = set.CreateOffscreenTarget(width, height);
                        canvas.RenderOffscreen(renderer, target, frame, labels);
                        var gpu = target.ReadPixels();
                        grabsAgree &= canvas.LabelGrabCount == wpfGrabs;
                        complete &= labels.TextsPending == 0 && labels.GlyphsPending == 0;

                        var result = GpuLabelCompare(sceneOnly, wpf, gpu, width, height);
                        worstInk = Math.Max(worstInk, Math.Abs(result.InkRatio - 1));
                        worstPlace = Math.Min(worstPlace, Math.Min(result.RowCorrelation, result.ColumnCorrelation));
                        worstMean = Math.Max(worstMean, result.MeanDifference);
                        summary += $" {view} {result.InkRatio - 1:+0.0%;-0.0%} ink, {result.MeanDifference:F2} mean;";
                        if (scale == 1.5 || view == "files")
                        {
                            SaveLabelShot($"gpu-labels-{(set.IsWarp ? "warp" : "hardware")}-{scale.ToString("0.0", CultureInfo.InvariantCulture)}-{view}-wpf", GpuLabelBytes(wpf), width, height);
                            SaveLabelShot($"gpu-labels-{(set.IsWarp ? "warp" : "hardware")}-{scale.ToString("0.0", CultureInfo.InvariantCulture)}-{view}-gpu", gpu, width, height);
                        }
                    }

                    Check($"the names of {views.Length} views at {scale:0.##}x on {name} carry WPF's ink within 8 % (worst {worstInk:P1})", worstInk <= 0.08);
                    Console.WriteLine($"       {summary.Trim()}");
                    Check($"and lie where WPF's lie (row and column ink profiles correlate at worst {worstPlace:F3})", worstPlace >= 0.95);
                    Check($"the pictures differ by at most {worstMean:F2} levels on average, text and all", worstMean <= 3);
                    Check("the same folder names are drawn, and grabbable, as WPF draws", grabsAgree);
                    Check("every name was shaped and every glyph was in the atlas", complete);
                }

                if (set.IsWarp)
                {
                    await GpuLayerChecksAsync(canvas, tree, files, renderer, set, labels);
                }

                // A frame at rest and one in motion, warm: the walk, the names
                // and the draw, and nothing for the garbage collector.
                canvas.DpiOverride = new DpiScale(1.5, 1.5);
                tree.IncludeHidden = false;
                canvas.SetFilter(null);
                canvas.FlyTo(files, 0.9, animated: false);
                Render(canvas);
                using (var frame = new NestedGpuFrame())
                using (var target = set.CreateOffscreenTarget(canvas.ScenePixelWidth, canvas.ScenePixelHeight))
                {
                    for (var warmFrame = 0; warmFrame < 4; warmFrame++)
                    {
                        canvas.RenderOffscreen(renderer, target, frame, labels, inMotion: warmFrame % 2 == 1);
                    }

                    set.WaitForGpu(5_000, out _);
                    var labelsMs = 0.0;
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    const int frames = 20;
                    for (var index = 0; index < frames; index++)
                    {
                        canvas.RenderOffscreen(renderer, target, frame, labels, inMotion: index % 2 == 1);
                        labelsMs += canvas.LastLabelsMilliseconds;
                    }

                    var allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / frames;
                    set.WaitForGpu(5_000, out _);
                    Check($"a frame of {canvas.LastGpuGlyphs:N0} glyphs over {frame.SceneRects.Count:N0} cells and tiles on {name} allocates nothing ({allocated} bytes per frame; names {labelsMs / frames:F2} ms)",
                        allocated == 0 && canvas.LastGpuGlyphs > 1000);
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

    /// <summary>
    /// What is not ready is left out and drawn when it comes: a folder whose
    /// name the UI font cannot show (shaped on the worker), glyphs of a
    /// fresh atlas (made on its thread), icons of a slow Shell.  The first
    /// frame draws what it has; frames later - with nothing but time between
    /// them - draw everything.
    /// </summary>
    private static async Task GpuLabelMissChecksAsync(CompiledShaders shaders, FaceRegistry faces)
    {
        var disk = new FakeDisk();
        disk.Folder(@"Q:\");
        var foreign = new string(new[] { (char)0x65E5, (char)0x672C, (char)0x8A9E, (char)0x306E, (char)0x30D5, (char)0x30A9, (char)0x30EB, (char)0x30C0 });
        disk.AddFiles(@"Q:\" + foreign, 4, "doc");
        disk.AddFiles(@"Q:\plain", 4, "doc");
        for (var index = 0; index < 20; index++)
        {
            disk.AddFile(@"Q:\files", $"report-{index:D2}.slow", 2048L * index);
        }

        for (var index = 0; index < 400; index++)
        {
            disk.AddFile(@"Q:\many", $"note {index:D3} of many.txt", 64L * index);
        }

        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive, "1 TB free")]);
        await LoadEverythingAsync(tree, _ => true);

        var canvas = new NestedCanvas { Tree = tree };
        canvas.Measure(new Size(ViewWidth, ViewHeight));
        canvas.Arrange(new Rect(0, 0, ViewWidth, ViewHeight));
        canvas.UpdateLayout();
        canvas.FlyTo(tree.Find(@"Q:\")!, 0.95, animated: false);
        Render(canvas);

        using var set = GpuDeviceSet.CreateWarp();
        using var cold = new GlyphAtlas(faces);
        var source = new LabelIconSource { Delay = TimeSpan.FromMilliseconds(150) };
        using var icons = new IconAtlas(new IconAtlasOptions { CachePath = null, AutoSaveInterval = TimeSpan.Zero, Source = source.Find });
        var shaper = new TextShaper(faces);
        var labels = new GpuLabelTarget(faces, shaper, cold, icons);
        var renderer = NestedGpuRenderer.For(set, shaders);
        using var frame = new NestedGpuFrame();
        using var target = set.CreateOffscreenTarget(canvas.ScenePixelWidth, canvas.ScenePixelHeight);

        canvas.RenderOffscreen(renderer, target, frame, labels);
        var firstTexts = labels.TextsPending;
        var firstGlyphs = labels.GlyphsPending;
        var firstDrawn = canvas.LastGpuGlyphs;
        Check($"a name the UI font lacks is left out of the first frame while it is shaped ({firstTexts} pending)", firstTexts >= 1);
        Check($"glyphs a fresh atlas has not made are left out of it too ({firstGlyphs} pending, {firstDrawn} drawn)", firstGlyphs > 0);

        // Only time passes: the worker shapes, the atlas makes its glyphs.
        canvas.FlyTo(tree.Find(@"Q:\files")!, 0.95, animated: false);
        canvas.RenderOffscreen(renderer, target, frame, labels);
        var iconsFirst = labels.IconsDrawn;
        canvas.FlyTo(tree.Find(@"Q:\")!, 0.95, animated: false);
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 15_000)
        {
            canvas.RenderOffscreen(renderer, target, frame, labels);
            if (labels.TextsPending == 0 && labels.GlyphsPending == 0)
            {
                break;
            }

            await Task.Delay(10);
        }

        Check($"frames later the name and every glyph are drawn ({canvas.LastGpuGlyphs} glyphs against {firstDrawn}, in {clock.ElapsedMilliseconds} ms)",
            labels.TextsPending == 0 && labels.GlyphsPending == 0 && canvas.LastGpuGlyphs > firstDrawn);
        var foreignFace = shaper.TryGet(foreign, (byte)LabelFace.Regular, out var shaped) ? shaped : null;
        Check("the name came back shaped from a fallback font", foreignFace is { Count: > 0 } && foreignFace.Faces.Any(face => face >= FaceRegistry.FirstFallback));

        canvas.FlyTo(tree.Find(@"Q:\files")!, 0.95, animated: false);
        clock.Restart();
        while (clock.ElapsedMilliseconds < 15_000)
        {
            canvas.RenderOffscreen(renderer, target, frame, labels);
            if (labels.IconsDrawn >= 20 && labels.GlyphsPending == 0)
            {
                break;
            }

            await Task.Delay(10);
        }

        Check($"icons not found yet are left out ({iconsFirst} in the first frame) and drawn when they arrive ({labels.IconsDrawn} of 20)",
            iconsFirst == 0 && labels.IconsDrawn == 20);

        // Hundreds of names new at once, while the camera moves: a budget of
        // them is shaped in the frame, the rest on the worker for the frames
        // after - never all of them inside one frame.
        var fresh = new TextShaper(faces);
        var moving = new GpuLabelTarget(faces, fresh, cold, icons: null);
        canvas.FlyTo(tree.Find(@"Q:\many")!, 0.95, animated: false);
        canvas.RenderOffscreen(renderer, target, frame, moving, inMotion: true);
        var shapedInFrame = fresh.DirectShapes;
        var leftForLater = moving.TextsPending;
        clock.Restart();
        while (clock.ElapsedMilliseconds < 15_000 && (moving.TextsPending > 0 || moving.GlyphsPending > 0))
        {
            await Task.Delay(10);
            canvas.RenderOffscreen(renderer, target, frame, moving, inMotion: true);
        }

        Check($"a frame in motion shapes at most {GpuLabelTarget.MotionShapeBudget} new names ({shapedInFrame}) and leaves the rest to the worker ({leftForLater})",
            shapedInFrame <= GpuLabelTarget.MotionShapeBudget && leftForLater > 100);
        Check($"which the frames after draw ({fresh.DeferredShapes} shaped on the worker, {moving.TextsDrawn} names drawn, in {clock.ElapsedMilliseconds} ms)",
            moving.TextsPending == 0 && moving.TextsDrawn > 200 && fresh.DeferredShapes > 100);
        canvas.Tree = null;
    }

    /// <summary>
    /// The layers on the graphics card: the names it draws leave out what
    /// WPF's leave out - less ink with the details and counts off, the two
    /// pictures still alike - and with the files off its cells are the
    /// raster's, the sub-folders filling the folder the files were in.
    /// </summary>
    private static async Task GpuLayerChecksAsync(NestedCanvas canvas, NestedTree tree, NestedFolder files, NestedGpuRenderer renderer, GpuDeviceSet set, GpuLabelTarget labels)
    {
        canvas.DpiOverride = new DpiScale(1, 1);
        tree.IncludeHidden = false;
        canvas.SetFilter(null);

        // Near enough to the files that their tiles have room for their sizes.
        canvas.FlyTo(files, 0.9, animated: false);
        canvas.ZoomAt(new Point(ViewWidth * 0.3, ViewHeight * 0.7), 2.5);

        (LabelComparison Result, double Ink) Draw()
        {
            Render(canvas);
            var width = canvas.ScenePixelWidth;
            var height = canvas.ScenePixelHeight;
            var sceneOnly = new uint[width * height];
            canvas.SceneBitmap!.CopyPixels(new Int32Rect(0, 0, width, height), sceneOnly, width * 4, 0);
            var wpf = GpuLabelWpfPicture(canvas, width, height, 1);
            using var frame = new NestedGpuFrame();
            using var target = set.CreateOffscreenTarget(width, height);
            canvas.RenderOffscreen(renderer, target, frame, labels);
            var gpu = target.ReadPixels();
            return (GpuLabelCompare(sceneOnly, wpf, gpu, width, height), GpuInk(sceneOnly, gpu, width, height));
        }

        try
        {
            var all = Draw();
            canvas.ShownLayers = CanvasLayer.All & ~CanvasLayer.Details & ~CanvasLayer.FolderCounts;
            var fewer = Draw();
            Check($"with the details and counts off, the graphics card writes less ({fewer.Ink / all.Ink:P0} of the ink) and still what WPF writes (within {Math.Abs(fewer.Result.InkRatio - 1):P1})",
                fewer.Ink < all.Ink * 0.97 && Math.Abs(fewer.Result.InkRatio - 1) <= 0.08 && fewer.Result.MeanDifference <= 3);

            canvas.ShownLayers = CanvasLayer.All & ~CanvasLayer.Files;
            await tree.WhenSortIdleAsync();
            var folders = Draw();
            Check($"with the files off, the graphics card draws the raster's picture of sub-folders alone ({folders.Result.MeanDifference:F2} levels apart on average)",
                files.Files.Count == 0 && folders.Result.MeanDifference <= 3 && Math.Abs(folders.Result.InkRatio - 1) <= 0.08);
        }
        finally
        {
            canvas.ShownLayers = CanvasLayer.All;
            await tree.WhenSortIdleAsync();
        }
    }

    /// <summary>How much lighter than the cells under them the graphics card's picture is, summed: the ink of its names.</summary>
    private static double GpuInk(uint[] scene, byte[] gpu, int width, int height)
    {
        static double Luminance(uint pixel) => 0.2126 * ((pixel >> 16) & 0xFF) + 0.7152 * ((pixel >> 8) & 0xFF) + 0.0722 * (pixel & 0xFF);
        var ink = 0.0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                ink += Math.Max(0, Luminance(OffscreenTarget.PixelAt(gpu, width, x, y)) - Luminance(scene[y * width + x]));
            }
        }

        return ink;
    }

    // ---- pictures ------------------------------------------------------------------------------

    /// <summary>WPF's picture of the view: the raster's cells and the label layer over them, without marks or outlines.</summary>
    private static uint[] GpuLabelWpfPicture(NestedCanvas canvas, int width, int height, double scale)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(canvas.SceneLayer);
        bitmap.Render(canvas.LabelLayer);
        var pixels = new uint[width * height];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private readonly record struct LabelComparison(double InkRatio, double RowCorrelation, double ColumnCorrelation, double MeanDifference);

    /// <summary>
    /// The names' ink both ways - how much lighter than the cells under them
    /// each picture is, summed - with its profile along the rows and along
    /// the columns, which only agree when the names are in the same places,
    /// and the plain mean difference of the two pictures.
    /// </summary>
    private static LabelComparison GpuLabelCompare(uint[] scene, uint[] wpf, byte[] gpu, int width, int height)
    {
        static double Luminance(uint pixel) => 0.2126 * ((pixel >> 16) & 0xFF) + 0.7152 * ((pixel >> 8) & 0xFF) + 0.0722 * (pixel & 0xFF);
        var rowsWpf = new double[height];
        var rowsGpu = new double[height];
        var columnsWpf = new double[width];
        var columnsGpu = new double[width];
        double inkWpf = 0, inkGpu = 0, difference = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                var under = Luminance(scene[index]);
                var gpuPixel = OffscreenTarget.PixelAt(gpu, width, x, y);
                var a = Math.Max(0, Luminance(wpf[index]) - under);
                var b = Math.Max(0, Luminance(gpuPixel) - under);
                inkWpf += a;
                inkGpu += b;
                rowsWpf[y] += a;
                rowsGpu[y] += b;
                columnsWpf[x] += a;
                columnsGpu[x] += b;
                for (var shift = 0; shift < 24; shift += 8)
                {
                    difference += Math.Abs((int)((wpf[index] >> shift) & 0xFF) - (int)((gpuPixel >> shift) & 0xFF));
                }
            }
        }

        return new LabelComparison(
            inkWpf == 0 ? 1 : inkGpu / inkWpf,
            GpuLabelCorrelation(rowsWpf, rowsGpu),
            GpuLabelCorrelation(columnsWpf, columnsGpu),
            difference / (3.0 * width * height));
    }

    private static double GpuLabelCorrelation(double[] a, double[] b)
    {
        var meanA = a.Average();
        var meanB = b.Average();
        double covariance = 0, varianceA = 0, varianceB = 0;
        for (var index = 0; index < a.Length; index++)
        {
            covariance += (a[index] - meanA) * (b[index] - meanB);
            varianceA += (a[index] - meanA) * (a[index] - meanA);
            varianceB += (b[index] - meanB) * (b[index] - meanB);
        }

        return varianceA == 0 || varianceB == 0 ? 1 : covariance / Math.Sqrt(varianceA * varianceB);
    }

    private static bool GpuLabelNear(uint actual, uint expected, int tolerance)
    {
        for (var shift = 0; shift < 32; shift += 8)
        {
            if (Math.Abs((int)((actual >> shift) & 0xFF) - (int)((expected >> shift) & 0xFF)) > tolerance)
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] GpuLabelBytes(uint[] pixels)
    {
        var bytes = new byte[pixels.Length * 4];
        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static void SaveLabelShot(string name, byte[] pixels, int width, int height)
    {
        if (Environment.GetEnvironmentVariable("NESTED_SHOTS") is not { Length: > 0 } folder)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(folder, name + ".png");
        using var stream = File.Create(path);
        encoder.Save(stream);
        ShotsWritten.Add(path);
    }

    /// <summary>
    /// Stands in for the Shell for the label checks: one icon for every key -
    /// a square of <see cref="Colour"/> with a clear border an eighth of its
    /// side wide - after <see cref="Delay"/>, on the atlas's worker.
    /// </summary>
    private sealed class LabelIconSource
    {
        public const uint Colour = 0xFF3060C0;

        private IconSlotData? _pixels;

        public TimeSpan Delay { get; init; }

        public IconFound Find(IconRequest request)
        {
            if (Delay > TimeSpan.Zero)
            {
                Thread.Sleep(Delay);
            }

            return new IconFound(4242, _pixels ??= Make());
        }

        private static IconSlotData Make()
        {
            var image = new byte[64 * 64 * 4];
            for (var y = 8; y < 56; y++)
            {
                for (var x = 8; x < 56; x++)
                {
                    var index = (y * 64 + x) * 4;
                    image[index] = unchecked((byte)Colour);
                    image[index + 1] = unchecked((byte)(Colour >> 8));
                    image[index + 2] = unchecked((byte)(Colour >> 16));
                    image[index + 3] = 255;
                }
            }

            return IconPixels.BuildSlot(image, 64, 64, [], []);
        }
    }
}
