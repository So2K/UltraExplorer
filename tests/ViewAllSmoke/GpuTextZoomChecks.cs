using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using UltraExplorer.Controls;
using UltraExplorer.Rendering.Gpu;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static void GpuTextZoomChecks(FaceRegistry faces, GlyphAtlas warm)
    {
        var shaper = new TextShaper(faces);
        var target = new GpuLabelTarget(faces, shaper, warm, null);
        const string name = "TULA0057-LAV.WAV";
        shaper.TryGet(name, FaceRegistry.Regular, out var shaped);

        // A zoom scales both the text and its available room. Its logical
        // prefix must remain exactly the same across all cache-size levels.
        var stable = true;
        var cuts = new HashSet<int>();
        var legacyCuts = new HashSet<int>();
        const float roomInEm = 5.37f;
        var expected = shaped.Trim(roomInEm);
        for (var step = 0; step <= 1200; step++)
        {
            var size = 7.5 + step * (64 - 7.5) / 1200;
            var text = target.Text(name, size, Colors.White, roomInEm * size, LabelFace.Regular, scaled: true);
            stable &= text.Count >> 1 == expected.Visible;
            cuts.Add(text.Count >> 1);
            var oldLevel = Math.Pow(2, Math.Round(Math.Log2(size) * 4) / 4);
            var oldScale = size / oldLevel;
            var oldRoom = Math.Max(1, Math.Floor(roomInEm * size / oldScale / 8) * 8) * oldScale;
            legacyCuts.Add(shaped.Trim((float)(oldRoom / size)).Visible);
        }

        Check($"gpu text: proportional zoom keeps the same filename prefix over 1201 sizes ({cuts.Count} distinct cuts, formerly {legacyCuts.Count})", stable && cuts.Count == 1 && legacyCuts.Count > 1);

        var monotone = true;
        var prior = -1;
        for (var step = 0; step <= 800; step++)
        {
            var text = target.Text(name, 12, Colors.White, 12 + step * .2, LabelFace.Regular, scaled: true);
            var visible = text.Count >> 1;
            monotone &= visible >= prior;
            prior = visible;
        }

        Check("gpu text: expanding room never removes a filename character", monotone && prior == shaped.Count);

        // Restore only one real tier from a warm atlas, as a cache holding
        // a rare glyph first seen at another scale. The very first emit at
        // a new tier must keep its glyph visible, then refine asynchronously.
        foreach (var (resident, desired, fontPx) in new[] { (0, 1, 12.01), (1, 0, 11.99), (1, 2, 28.01), (2, 1, 27.99) })
        {
            using var sparse = new GlyphAtlas(faces);
            var source = warm.Snapshot();
            var entries = source.Entries.Where(item => item.Key.Tier == resident).ToList();
            var page = new byte[GlyphAtlas.PageSize * GlyphAtlas.PageSize];
            var restored = sparse.Restore(source.PageCount, source.Shelves, entries, (index, pointer) =>
            {
                Marshal.Copy(warm.PagePointer(index), page, 0, page.Length);
                Marshal.Copy(page, 0, pointer, page.Length);
                return true;
            });
            shaper.TryGet("W", FaceRegistry.Regular, out var letter);
            var quads = new GlyphQuad[4];
            var sink = new GpuTextQuadSink(quads);
            var pending = sparse.Emit(ref sink, letter, letter.Whole, 10.25, 30.5, fontPx, 0xFFFFFFFF, 0, snap: false);
            var drawn = quads[0];
            var tier = GlyphAtlas.Tiers[resident];
            Check($"gpu text: first {resident}->{desired} tier transition keeps the resident glyph instead of blinking",
                restored && pending == 1 && sink.Count == 1 && drawn.PageTier >> 8 == resident
                && Math.Abs(drawn.PxRange - 2.0 * tier.Spread / tier.Em * fontPx) < 1e-5);

            sparse.WaitForPending(TimeSpan.FromSeconds(10));
            sink = new GpuTextQuadSink(quads);
            pending = sparse.Emit(ref sink, letter, letter.Whole, 10.25, 30.5, fontPx, 0xFFFFFFFF, 0, snap: false);
            Check($"gpu text: {desired} tier replaces the fallback after the worker makes it",
                pending == 0 && sink.Count == 1 && quads[0].PageTier >> 8 == desired);
        }
    }

    private static void GpuTextZoomPixels(GpuDeviceSet set, CompiledShaders shaders, FaceRegistry faces, GlyphAtlas glyphs)
    {
        const int width = 512, height = 90;
        const uint background = 0xFF202428;
        using var offscreen = set.CreateOffscreenTarget(width, height);
        using var frame = new NestedGpuFrame(16, 16, 1024);
        frame.ClearColour = background;
        frame.SceneChanged();
        var shaper = new TextShaper(faces);
        var labels = new GpuLabelTarget(faces, shaper, glyphs, null);
        var renderer = NestedGpuRenderer.For(set, shaders);
        var complete = true;
        foreach (var boundary in new[] { 12.0, 28.0 })
        {
            double lastInk = 0;
            var minInk = double.MaxValue;
            var maxInk = 0.0;
            for (var step = -1; step <= 1; step++)
            {
                var size = boundary + step * .01;
                labels.Begin(frame, set, width, height, 1, 1, snap: false);
                var text = labels.Text("TULA0057-LAV.WAV — Щит", size, Colors.White, 490, LabelFace.Regular, scaled: true);
                labels.DrawText(text, new Point(10.25, 18.375));
                labels.End();
                renderer.Draw(offscreen.RenderTargetView, width, height, frame);
                var pixels = offscreen.ReadPixels();
                var ink = 0.0;
                for (var index = 0; index < pixels.Length; index += 4)
                {
                    ink += Math.Max(0, pixels[index + 2] - 0x20);
                }
                complete &= labels.GlyphsPending == 0 && labels.TextsPending == 0 && renderer.LastGlyphs >= 20 && ink > 0;
                minInk = Math.Min(minInk, ink);
                maxInk = Math.Max(maxInk, ink);
                lastInk = ink;
                SaveLabelShot($"gpu-text-zoom-{(set.IsWarp ? "warp" : "hardware")}-{size:F2}", pixels, width, height);
            }

            var change = maxInk / minInk - 1;
            Check($"gpu text: SDF tier boundary {boundary:F0}px stays present and retains ink on {(set.IsWarp ? "WARP" : set.AdapterName)} ({change:P2} spread)",
                complete && double.IsFinite(change) && change < .18 && lastInk > 0);
        }
    }
}
