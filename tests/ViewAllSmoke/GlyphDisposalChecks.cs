using UltraExplorer.Rendering.Gpu;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task GlyphDisposalChecks()
    {
        Section("glyph atlas: disposal while its worker is still running");
        var faces = FaceRegistry.Shared;
        var glyphs = faces[FaceRegistry.Regular].Face.GetGlyphIndices([(uint)'A', (uint)'B']);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var exited = new ManualResetEventSlim();
        using var atlas = new GlyphAtlas(faces);
        var callbacks = 0;
        atlas.GlyphsArrived += () =>
        {
            Interlocked.Increment(ref callbacks);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            exited.Set();
        };
        try
        {
            atlas.TryGet(FaceRegistry.Regular, glyphs[0], 0, out _);
            var started = entered.Wait(TimeSpan.FromSeconds(5));
            Check("the owned atlas worker reaches the controlled stall", started);
            if (!started) return Task.CompletedTask;
            atlas.TryGet(FaceRegistry.Regular, glyphs[1], 0, out _);
            var entriesAtClose = atlas.EntryCount;
            atlas.Dispose();
            Check("a stalled worker retains its native pages until it stops instead of freeing them under its feet", atlas.PageCount > 0);
            release.Set();
            Check("the stalled atlas worker returns when released", exited.Wait(TimeSpan.FromSeconds(5)));
            SpinWait.SpinUntil(() => atlas.PageCount == 0 && atlas.PendingCount == 0, TimeSpan.FromSeconds(5));
            Check("queued glyphs stop at disposal and the worker releases all pages without publishing more entries",
                atlas.EntryCount == entriesAtClose && atlas.PageCount == 0 && atlas.PendingCount == 0 && callbacks == 1);
        }
        finally
        {
            release.Set();
        }
        return Task.CompletedTask;
    }
}
