using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;
using Color4 = Vortice.Mathematics.Color4;

namespace ViewAllSmoke;

/// <summary>
/// The GPU path's plumbing, headless: device sets on WARP and on the card
/// that drives the primary monitor, textures shared with Direct3D 9, clears
/// read back to the exact colour, the frame timer, and a NestedSurface
/// presented and captured through RenderTargetBitmap without any window.
/// Nothing here is ever shown; the hardware checks are skipped on a machine
/// with no graphics card (a virtual machine, a remote session without one).
/// </summary>
internal static partial class Program
{
    private static Task GpuDeviceChecks()
    {
        Section("gpu devices");
        GpuPreferenceChecks();
        RunOnSta("gpu decision", () =>
        {
            GpuDecisionChecks();
            return Task.CompletedTask;
        });
        GpuWarpChecks();

        var hardware = GpuDeviceSet.EnumerateAdapters().Where(adapter => !adapter.IsSoftware).ToList();
        if (hardware.Count == 0)
        {
            Console.WriteLine("  (no hardware graphics adapter: the hardware checks are skipped)");
            return Task.CompletedTask;
        }

        // The card behind the primary monitor, as the app would pick it for a
        // window there.  Used headlessly only: nothing is put on its screen.
        var luid = GpuBootstrap.AdapterLuidForMonitor(GpuBootstrap.PrimaryMonitor);
        Check("Direct3D 9 names the card that drives the primary monitor", luid is not null);
        Check("that card is one DXGI lists as hardware", luid is { } found && hardware.Any(adapter => adapter.Luid.Equals(found)));
        if (luid is null)
        {
            return Task.CompletedTask;
        }

        GpuHardwareChecks(luid.Value);
        RunOnSta("gpu surface", () =>
        {
            GpuSurfaceChecks(luid.Value);
            GpuSurfaceLifetimeChecks(luid.Value);
            return Task.CompletedTask;
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// What a surface does when its card is slow, when its set is disposed
    /// under it by another canvas on the same card, and when WPF's front
    /// buffer goes away - composing on the card (the lock screen) and in
    /// software (a remote or software-rendered session).
    /// </summary>
    private static void GpuSurfaceLifetimeChecks(Vortice.Luid luid)
    {
        using var set = GpuDeviceSet.Create(luid, shareWithWpf: true);

        // Enough GPU work that it is not finished when first asked.
        SurfaceDrawer heavy = (in SurfaceFrame frame) =>
        {
            for (var round = 0; round < 40; round++)
            {
                frame.Context.ClearRenderTargetView(frame.Target, ToColor4(0xFF000000u | (uint)(round * 0x030201)));
            }
        };

        SurfaceDrawer plain = (in SurfaceFrame frame) => frame.Context.ClearRenderTargetView(frame.Target, ToColor4(0xFF203040u));
        var slow = new NestedSurface(set, 1.0, 1.0) { AskDeviceAfterMilliseconds = 0 };
        var slowLost = 0;
        slow.DeviceLost += (_, _) => slowLost++;
        try
        {
            slow.EnsureSize(3000, 2000);
            var results = new List<PresentResult>();
            for (var frame = 0; frame < 5; frame++)
            {
                results.Add(slow.Present(heavy));
            }

            Check($"a frame whose GPU work outlasts the time the device is given is waited for, not taken for a lost device ({slow.SlowFrames} slow of 5, {string.Join(", ", results.Distinct())})",
                results.All(result => result == PresentResult.Presented) && slow.SlowFrames > 0 && !slow.IsLost && slowLost == 0 && !set.IsDisposed);
        }
        finally
        {
            slow.Dispose();
        }

        // Two canvases on one card share its set; one of them reports it lost.
        var shared = GpuDeviceSet.Create(luid, shareWithWpf: true);
        var first = new NestedSurface(shared, 1.0, 1.0);
        var second = new NestedSurface(shared, 1.0, 1.0);
        var secondLost = 0;
        second.DeviceLost += (_, _) => secondLost++;
        try
        {
            first.EnsureSize(200, 100);
            second.EnsureSize(200, 100);
            var before = first.Present(plain) == PresentResult.Presented && second.Present(plain) == PresentResult.Presented;
            first.Dispose();
            shared.Dispose();
            Check("a surface hears that its set was disposed under it, once", before && second.IsLost && secondLost == 1);

            PresentResult after;
            try
            {
                after = second.Present(plain);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  present on a disposed set threw {ex.GetType().Name}: {ex.Message}");
                after = PresentResult.Presented;
            }

            Check("and its next present reports the device lost without touching the set", after == PresentResult.DeviceLost && secondLost == 1);

            var capture = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(second, new Rect(0, 0, 200, 100));
            }

            var captured = true;
            try
            {
                capture.Render(visual);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  capture on a disposed set threw {ex.GetType().Name}: {ex.Message}");
                captured = false;
            }

            Check("and a capture of it does not read the disposed card", captured && second.Captures == 0);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
            shared.Dispose();
        }

        // The front buffer goes away.
        var screen = new NestedSurface(set, 1.0, 1.0) { IsComposingInSoftware = () => false };
        var software = new NestedSurface(set, 1.0, 1.0) { IsComposingInSoftware = () => true };
        var contentLost = 0;
        screen.ContentLost += (_, _) => contentLost++;
        try
        {
            screen.EnsureSize(128, 128);
            software.EnsureSize(128, 128);
            screen.Present(plain);
            software.Present(plain);
            screen.SimulateFrontBufferAvailable(false);
            software.SimulateFrontBufferAvailable(false);
            Check("composing on the card, a lost front buffer (the lock screen) stops the presents",
                screen.Present(plain) == PresentResult.Unavailable && !screen.IsLost);
            var version = screen.SurfaceVersion;
            screen.SimulateFrontBufferAvailable(true);
            Check("and its return asks for a whole frame, which is presented on the surface given to WPF again",
                contentLost == 1 && screen.Present(plain) == PresentResult.Presented && screen.SurfaceVersion == version + 1);
            var presented = software.Presents;
            Check("composing in software, where there is never a front buffer, the presents go on through the software fallback",
                software.Present(plain) == PresentResult.Presented && software.Presents == presented + 1 && !software.IsLost);
        }
        finally
        {
            screen.Dispose();
            software.Dispose();
        }
    }

    private static void GpuPreferenceChecks()
    {
        Check("the renderer setting reads gpu in any case", GpuBootstrap.ParsePreference(" GPU ") == RendererPreference.Gpu);
        Check("the renderer setting reads cpu and auto", GpuBootstrap.ParsePreference("cpu") == RendererPreference.Cpu
            && GpuBootstrap.ParsePreference("auto") == RendererPreference.Auto);
        Check("anything else is no preference", GpuBootstrap.ParsePreference("fast") is null && GpuBootstrap.ParsePreference(null) is null);
        Check("the device-pixel size rounds up", NestedSurface.PixelSize(100.2, 50, 1.5, 1.5) == (151, 75)
            && NestedSurface.PixelSize(0, 0, 1, 1) == (1, 1));

        // The saved setting is read before anything of the GPU starts, from
        // the workspace file the store writes.
        var folder = Path.Combine(Path.GetTempPath(), "UltraExplorerRenderer", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(folder, "workspace.json");
            new WorkspaceStore(path).SaveAsync(new WorkspaceState { CanvasRenderer = "Cpu", CanvasSort = "name" }).GetAwaiter().GetResult();
            Check("the saved renderer is read on its own, before the workspace is loaded",
                GpuBootstrap.ParsePreference(WorkspaceStore.PeekCanvasRenderer(path)) == RendererPreference.Cpu);
            File.WriteAllText(path, """{"CanvasLayout":"Nested"}""");
            var none = WorkspaceStore.PeekCanvasRenderer(path);
            File.WriteAllText(path, "{ not json");
            var broken = WorkspaceStore.PeekCanvasRenderer(path);
            Check("a workspace without the setting, a broken one, or none at all reads as no setting",
                none is null && broken is null && WorkspaceStore.PeekCanvasRenderer(Path.Combine(folder, "missing.json")) is null);
        }
        finally
        {
            TryDelete(folder);
        }
    }

    /// <summary>Headless, nothing is on screen: the canvas stays on the CPU raster and no device is made for it.</summary>
    private static void GpuDecisionChecks()
    {
        var decision = GpuBootstrap.Decide(new DrawingVisual());
        Check($"a visual in no window is drawn on the CPU ({decision.Reason})", !decision.UseGpu
            && (decision.Reason == GpuBootstrap.ReasonNotOnScreen || decision.Reason == GpuBootstrap.ReasonCpuChosen));
        Check("deciding for a visual in no window starts no warm-up", !GpuBootstrap.IsStarted);
    }

    private static void GpuWarpChecks()
    {
        GpuDeviceSet warp;
        try
        {
            warp = GpuDeviceSet.CreateWarp();
        }
        catch (Exception ex)
        {
            Check($"a WARP device set can be made ({ex.Message})", false);
            return;
        }

        var probe = new AttachProbe();
        using (warp)
        {
            Check($"a WARP device set can be made ({warp.Description})", warp.IsWarp && warp.FeatureLevel >= Vortice.Direct3D.FeatureLevel.Level_11_0);
            Check("WARP cannot feed WPF", !warp.CanShareWithWpf && warp.Device9 is null);
            Check("a fresh WARP set is not lost", !warp.IsLost());

            GpuClearAndReadBack(warp, 0xFF20C040u);

            // The timer's answer arrives without waiting, once the GPU is done.
            warp.Timer.Begin();
            using (var target = warp.CreateOffscreenTarget(256, 256))
            {
                warp.Context.ClearRenderTargetView(target.RenderTargetView, ToColor4(0xFF101010u));
            }

            warp.Timer.End();
            Check("waiting for the GPU returns once the work is done", warp.WaitForGpu(1000, out var waited) && waited < 1000);
            warp.Timer.Collect();
            Check($"the frame timer has an answer after the wait ({warp.Timer.LastGpuMilliseconds:F3} ms)", !double.IsNaN(warp.Timer.LastGpuMilliseconds) && warp.Timer.Samples == 1);

            var first = warp.Attach(_ => probe);
            var second = warp.Attach(_ => new AttachProbe());
            Check("one attachment of a type per set", ReferenceEquals(first, second) && warp.TryGetAttached<AttachProbe>(out var attached) && ReferenceEquals(attached, probe));
        }

        Check("attachments are disposed with the set", probe.IsDisposed && warp.IsDisposed && warp.IsLost());
    }

    private static void GpuHardwareChecks(Vortice.Luid luid)
    {
        GpuDeviceSet set;
        try
        {
            set = GpuDeviceSet.Create(luid, shareWithWpf: true);
        }
        catch (GpuUnavailableException ex)
        {
            Check($"a device set can be made on the primary monitor's card ({ex.Message})", false);
            return;
        }

        using (set)
        {
            Check($"a device set can be made on the primary monitor's card ({set.Description})",
                !set.IsWarp && set.FeatureLevel >= Vortice.Direct3D.FeatureLevel.Level_11_0 && set.AdapterLuid.Equals(luid));
            Check("it has the Direct3D9Ex device that feeds WPF", set.CanShareWithWpf && set.Device9 is not null);
            Check("a fresh hardware set is not lost", !set.IsLost());

            using (var shared = set.CreateSharedTexture(1600, 1000))
            {
                var description = shared.Surface9.Description;
                Check("a shared texture has a handle and a Direct3D 9 surface",
                    shared.SharedHandle != IntPtr.Zero && shared.Surface9Pointer != IntPtr.Zero);
                Check("the Direct3D 9 surface is the texture's size and format",
                    description.Width == 1600 && description.Height == 1000 && description.Format == Vortice.Direct3D9.Format.A8R8G8B8);

                // What Direct3D 11 draws into the shared texture is what the
                // Direct3D 9 side - WPF's side - holds.
                set.Context.ClearRenderTargetView(shared.RenderTargetView, ToColor4(0xFF3060A0u));
                Check("the shared texture's clear is finished before it is handed on", set.WaitForGpu(1000, out _));
                using var readback = new TextureReadback(set);
                var pixels = readback.ReadPixels(shared.Texture, out var width, out _);
                Check("the shared texture reads back as cleared", OffscreenTarget.PixelAt(pixels, width, 1599, 999) == 0xFF3060A0u);
            }

            GpuClearAndReadBack(set, 0xFF336699u);
        }
    }

    /// <summary>A NestedSurface without a window: sized, presented, captured the way --capture captures it, grown, shrunk.</summary>
    private static void GpuSurfaceChecks(Vortice.Luid luid)
    {
        using var set = GpuDeviceSet.Create(luid, shareWithWpf: true);
        var colour = 0xFF224466u;
        SurfaceDrawer drawer = (in SurfaceFrame frame) => frame.Context.ClearRenderTargetView(frame.Target, ToColor4(colour));
        var surface = new NestedSurface(set, 1.0, 1.0);
        try
        {
            Check("a surface with no size has nothing to present", surface.Present(drawer) == PresentResult.Unavailable);
            Check("the first size makes a texture", surface.EnsureSize(300, 200));
            Check("the texture reaches WPF only at the present", surface.SurfaceWidth == 0 && surface.SurfaceVersion == 0);

            var result = surface.Present(drawer);
            Check($"the first present is drawn ({result})", result == PresentResult.Presented && surface.Presents == 1);
            Check("the texture grows in 256-pixel steps", surface.SurfaceWidth == 512 && surface.SurfaceHeight == 256
                && surface.PixelWidth == 512 && surface.PixelHeight == 256);
            Check("the view is the size asked for", surface.ViewPixelWidth == 300 && surface.ViewPixelHeight == 200);
            Check("the scene layer draws the whole texture", surface.DrawRect == new Rect(0, 0, 512, 256) && surface.SurfaceVersion == 1);
            Check("a present always ends unlocked", !surface.IsLost && surface.SkippedPresents == 0);

            // --capture, --nested-snapshots and printing see the image
            // through RenderTargetBitmap, which asks the surface for a copy.
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(surface, surface.DrawRect);
            }

            var capture = new RenderTargetBitmap(512, 256, 96, 96, PixelFormats.Pbgra32);
            capture.Render(visual);
            var pixels = new byte[512 * 256 * 4];
            capture.CopyPixels(pixels, 512 * 4, 0);
            Check("a capture goes through the surface's own copy", surface.Captures >= 1);
            Check("the capture shows the last frame", OffscreenTarget.PixelAt(pixels, 512, 5, 5) == colour
                && OffscreenTarget.PixelAt(pixels, 512, 299, 199) == colour
                && OffscreenTarget.PixelAt(pixels, 512, 511, 255) == colour);

            Check("a wider view makes a new texture", surface.EnsureSize(700, 200));
            Check("WPF keeps the old one until the next present", surface.SurfaceWidth == 512);
            colour = 0xFF8844AAu;
            surface.Present(drawer);
            Check("the next present hands WPF the new texture", surface.SurfaceWidth == 768 && surface.SurfaceVersion == 2);
            Check("a smaller view keeps the texture", !surface.EnsureSize(100, 100) && surface.SurfaceVersion == 2);

            Check("a large view makes a large texture", surface.EnsureSize(3000, 2000));
            surface.Present(drawer);
            Check("rounded up to whole steps", surface.SurfaceWidth == 3072 && surface.SurfaceHeight == 2048);
            Check("a texture far bigger than the view is replaced", surface.EnsureSize(300, 300));
            surface.Present(drawer);
            Check("by one the view's size in steps", surface.SurfaceWidth == 512 && surface.SurfaceHeight == 512 && surface.SurfaceVersion == 4);

            var readback = new RenderTargetBitmap(512, 512, 96, 96, PixelFormats.Pbgra32);
            var redrawn = new DrawingVisual();
            using (var dc = redrawn.RenderOpen())
            {
                dc.DrawImage(surface, surface.DrawRect);
            }

            readback.Render(redrawn);
            var after = new byte[512 * 512 * 4];
            readback.CopyPixels(after, 512 * 4, 0);
            Check("the capture follows the new texture and colour", OffscreenTarget.PixelAt(after, 512, 256, 256) == 0xFF8844AAu);
        }
        finally
        {
            surface.Dispose();
        }

        Check("a disposed surface lets go of its texture", surface.SurfaceWidth == 0 && surface.PixelWidth == 0);
        Check("a disposed surface presents nothing", surface.Present(drawer) == PresentResult.DeviceLost);

        // A removed device fails the next Direct3D call with an HRESULT; a
        // drawer that throws the same exception takes the same path.
        var failing = new NestedSurface(set, 1.0, 1.0);
        var lostEvents = 0;
        failing.DeviceLost += (_, _) => lostEvents++;
        try
        {
            failing.EnsureSize(64, 64);
            failing.Present(drawer);
            var failed = failing.Present((in SurfaceFrame _) => throw new SharpGen.Runtime.SharpGenException(SharpGen.Runtime.Result.Fail));
            Check("a present whose GPU calls fail reports the device lost", failed == PresentResult.DeviceLost && failing.IsLost);
            Check("and says so once", lostEvents == 1);
            Check("and takes the surface away from WPF", failing.PixelWidth == 0);
            Check("a lost surface presents nothing more", failing.Present(drawer) == PresentResult.DeviceLost
                && !failing.EnsureSize(128, 128) && lostEvents == 1);
        }
        finally
        {
            failing.Dispose();
        }
    }

    private static void GpuClearAndReadBack(GpuDeviceSet set, uint colour)
    {
        using var target = set.CreateOffscreenTarget(64, 48);
        set.Context.ClearRenderTargetView(target.RenderTargetView, ToColor4(colour));
        var pixels = target.ReadPixels();
        Check($"an offscreen clear reads back exactly on {set.AdapterName}",
            pixels.Length == 64 * 48 * 4
            && OffscreenTarget.PixelAt(pixels, 64, 0, 0) == colour
            && OffscreenTarget.PixelAt(pixels, 64, 63, 47) == colour);

        var bitmap = target.ReadBitmap();
        var row = new byte[64 * 4];
        bitmap.CopyPixels(new Int32Rect(0, 20, 64, 1), row, row.Length, 0);
        Check("and as a frozen Pbgra32 bitmap", bitmap.IsFrozen && bitmap.Format == PixelFormats.Pbgra32
            && bitmap.PixelWidth == 64 && OffscreenTarget.PixelAt(row, 64, 33, 0) == colour);
    }

    private static Color4 ToColor4(uint argb) => new(
        ((argb >> 16) & 0xFF) / 255f,
        ((argb >> 8) & 0xFF) / 255f,
        (argb & 0xFF) / 255f,
        (argb >> 24) / 255f);

    private sealed class AttachProbe : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
