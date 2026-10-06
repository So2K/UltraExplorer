using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// J004: the glyph and icon atlases are shared by every canvas on the card,
/// and every canvas listens to them.  Each wave of glyphs or icons arriving
/// for one canvas - a split view's busy pane, a window zooming into a folder
/// of programs or of names in another script - made every other canvas draw
/// and present its whole label layer again, though none of it had waited
/// for anything: a second window doubled the cost of every frame of the one
/// in use.  Now a canvas draws its names again for what arrived only when
/// its last names left something out.
/// </summary>
internal static partial class Program
{
    private static Task GpuArrivalChecks()
    {
        RunOnSta("gpu arrivals: an idle canvas beside a busy one", GpuArrivalIdleCanvasAsync);
        return Task.CompletedTask;
    }

    private static async Task GpuArrivalIdleCanvasAsync()
    {
        Section("gpu arrivals: glyphs and icons arriving for one canvas leave another's names alone (J004)");
        if (TestScreen.Target() is not { } target)
        {
            Console.WriteLine("  (no secondary monitor: skipped)");
            return;
        }

        // The idle canvas: names of plain letters and files of a common type.
        var quietDisk = new FakeDisk();
        quietDisk.AddFiles(@"Q:\quiet", 40, "plain");
        using var quietTree = new NestedTree(quietDisk.Read) { IsReadingOnDemand = false };
        quietTree.SetRoots([new NestedRoot(@"Q:\", "Q:", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(quietTree, _ => true);

        // The busy one: types no icon was found for yet in this process, and
        // names in scripts no glyph was made for yet - made fresh for the run.
        var busyDisk = new FakeDisk();
        var seed = Environment.TickCount;
        var waves = new List<string>();
        for (var wave = 0; wave < 4; wave++)
        {
            var folder = $@"R:\wave{wave}";
            waves.Add(folder);
            for (var index = 0; index < 24; index++)
            {
                var character = (char)(0x4E00 + (seed + wave * 977 + index * 31) % 0x5000);
                var hangul = (char)(0xAC00 + (seed + wave * 613 + index * 17) % 0x2B00);
                busyDisk.AddFile(folder, $"{character}{hangul} {index:D2}.j{wave}{index:x2}{seed % 4096:x3}", 1024);
            }
        }

        using var busyTree = new NestedTree(busyDisk.Read) { IsReadingOnDemand = false };
        busyTree.SetRoots([new NestedRoot(@"R:\", "R:", NestedFolderKind.Drive)]);
        await LoadEverythingAsync(busyTree, _ => true);

        if (!GpuBootstrap.IsStarted)
        {
            GpuLabelAtlases.RegisterWarmUp();
            NestedGpuRenderer.RegisterWarmUp();
        }

        GpuLabelAtlases.StartWarmUp();
        var quiet = new NestedCanvas { Tree = quietTree };
        var busy = new NestedCanvas { Tree = busyTree };
        var windows = new List<Window>();
        try
        {
            windows.Add(GpuArrivalWindow(quiet, target, 40));
            windows.Add(GpuArrivalWindow(busy, target, 520));
            quiet.FlyTo(quietTree.Find(@"Q:\quiet")!, 0.95, animated: false);
            busy.FlyTo(busyTree.Find(@"R:\")!, 0.95, animated: false);
            if (!await Until(() => quiet.IsSceneOnGpu && quiet.AreLabelsOnGpu && quiet.IsIdle && busy.AreLabelsOnGpu && busy.IsIdle, 30_000))
            {
                Console.WriteLine($"  (the GPU did not draw the windows' names - {quiet.RendererReason}: skipped)");
                return;
            }

            // The idle canvas's names in full: nothing it waits for any more.
            await GpuArrivalSettledAsync(quiet, 1_500);

            // The busy canvas goes into one folder after another, each with
            // its own new glyphs and icons to wait for.
            var quietBefore = quiet.LabelLayerCount;
            var busyBefore = busy.LabelLayerCount;
            var watch = Stopwatch.StartNew();
            foreach (var wave in waves)
            {
                busy.FlyTo(busyTree.Find(wave)!, 0.95, animated: false);
                await Until(() => busy.IsIdle, 5_000);
                await Task.Delay(250);
            }

            await GpuArrivalSettledAsync(busy, 1_500);
            var busyPasses = busy.LabelLayerCount - busyBefore;
            var quietPasses = quiet.LabelLayerCount - quietBefore;
            Check($"while the busy canvas took in its glyphs and icons ({busyPasses} label passes in {watch.ElapsedMilliseconds:N0} ms), the idle one beside it drew its names {quietPasses} times",
                busyPasses > waves.Count && quietPasses == 0);

            // The idle canvas still draws its names again for what it waits for.
            var quietFolder = quietTree.Find(@"Q:\quiet")!;
            var before = quiet.LabelLayerCount;
            quietDisk.AddFile(@"Q:\quiet", $"{(char)(0x4E00 + (seed + 7) % 0x5000)}late.k{seed % 4096:x3}", 10);
            await quietTree.RefreshAsync(quietFolder);
            await GpuArrivalSettledAsync(quiet, 1_500);
            Check($"and a new name of its own, in a script and of a type it had not drawn, is drawn into it as it arrives ({quiet.LabelLayerCount - before} label passes)",
                quiet.LabelLayerCount - before >= 2);
        }
        finally
        {
            foreach (var window in windows)
            {
                window.Close();
            }

            quiet.Tree = null;
            busy.Tree = null;
        }
    }

    /// <summary>A canvas in a window of its own - cloaked, never activated, on the secondary monitor - drawing on the GPU.</summary>
    private static Window GpuArrivalWindow(NestedCanvas canvas, TestScreen.Monitor target, int left)
    {
        var window = new Window
        {
            Content = canvas,
            Title = "UltraExplorer gpu arrivals",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Width = 460,
            Height = 400
        };

        using (ActivationGuard.GuardWindowsCreated())
        {
            var handle = new WindowInteropHelper(window).EnsureHandle();
            DialogNative.CloakOwn(handle, true);
            SetWindowPos(handle, 0, target.Work.Left + left, target.Work.Top + 40, 460, 400, 0x0004 | 0x0010); // no z-order change, no activation
            window.Show();
        }

        return window;
    }

    /// <summary>Waits until <paramref name="canvas"/> has drawn no label layer for <paramref name="quietMilliseconds"/>, at most ten seconds.</summary>
    private static async Task GpuArrivalSettledAsync(NestedCanvas canvas, int quietMilliseconds)
    {
        var watch = Stopwatch.StartNew();
        var last = canvas.LabelLayerCount;
        var since = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 10_000)
        {
            await Task.Delay(50);
            if (canvas.LabelLayerCount != last)
            {
                last = canvas.LabelLayerCount;
                since.Restart();
            }
            else if (since.ElapsedMilliseconds >= quietMilliseconds && canvas.IsIdle)
            {
                return;
            }
        }
    }
}
