# Zoom text rendering review

Reviewed 2026-10-01 against the installed source before the zoom fixes. This is a
source review and a verification plan; historical benchmark results are not
measurements of the current change.

## Shipped zoom corrections

Filenames reserve an icon slot before its texture arrives. Metadata receives a
continuous bounded share of extra room; a full timestamp is admitted only when
the entire filename also fits. Compact dates use their full width when they fit.
The GPU trims scale-independent shaped text at its exact available width; WPF
retains bounded caching with 1/8-DIP width bins. A pending preferred SDF tier
uses the nearest resident tier's own bounds and pixel range until detail arrives.
No shader/atlas format or font fallback was replaced.

Final qualification: 1094/1094 combined GPU, CPU label, frame, picker, settings
and normal folder routing checks; 53/53 native Open/custom Save/folder/early
Cancel checks. 27,045 continuous layouts across Latin/Cyrillic/CJK/emoji, three
cultures and three sorts had no filename-prefix regression, metadata overlap,
icon-arrival shift or origin jump. GPU proportional zoom across 1201 sizes
kept one logical prefix (the previous buckets produced two). WARP and hardware
SDF tests covered both tier boundaries and four cold-tier transitions with no
missing glyph. Builds have zero warnings/errors. Visual captures at 4.2, 5.1,
6.1 and 7.3 with Modified sorting are in artifacts/label-zoom-layout-shots.

The first combined run had three synthetic split-pane keyboard assertions fail;
current and old app DLLs each passed the same fresh Settings harness 259/259,
and GPU→Settings passed 372/372. No production split change was justified.
The complete fresh final run passed 1094/1094; the original failed log is retained.

## The reported jump

The screenshots show a filename getting shorter while the tile grows. In
`Controls/NestedCanvas.Labels.cs`, `DrawFileLabel` previously enabled metadata at
120 DIPs for date/type order and 190 DIPs otherwise. It then switched between a
short date and date plus time according to the available room. Both transitions
could take width away from the filename while zooming in. This is a layout
transition, independently of glyph antialiasing.

`Rendering/Gpu/GpuLabelTarget.cs`, `TrimRoom`, also imported the WPF cache's
logarithmic font-size ladder and 8-DIP width buckets into the GPU path. GPU
`ShapedText` already stores scale-independent advances in em units; its `Trim`
uses a cluster-safe binary search without allocating. Exact available width can
therefore be used for the GPU path without creating a new shaped run per zoom
step. WPF cache quantization still has a purpose and should be tested separately.

Regression checks should follow the same long filename through a continuous
width sweep, not merely compare four isolated screenshots. Increasing tile
width at the same capped font size must not reduce the filename's visible
prefix. Metadata must not overlap the name, and missing text prepared by a
worker must not suddenly reclaim room from an already visible name.

## Techniques already shipping

| Technique | Implementation |
| --- | --- |
| GPU batching | `NestedGpuRenderer`: instanced quads, normally four draws for scene, pills, icons and glyphs |
| Distance-field text | `GlyphAtlas`, `GlyphRasterizer`, `SdfGenerator`: R8 fields at 16/32/64 em, DirectWrite grayscale outlines without grid fitting |
| Baked texture data | Glyph and icon disk caches; shader bytecode cache; startup pipeline warm-up |
| Mipmaps | `IconSlotData`: seven premultiplied BGRA levels from 64 to 1 pixel; trilinear GPU sampling |
| Worker preparation | `TextShaper`, `GlyphAtlas`: bounded synchronous shaping and deferred shaping/rasterization |
| Low allocation | Native reusable instance lists; reusable scratch arrays; cached scale-independent advances |
| Avoid redundant uploads | Per-canvas instance buffers skip unchanged frame/version/count; atlas updates upload newly placed rectangles |
| GPU timing without stalls | Four timestamp-query slots, reads with `DoNotFlush`; a busy slot remains untimed |

These are the same families of techniques demonstrated by game renderers.
Valve's distance-field paper stores distance in an 8-bit channel and reconstructs
edges with bilinear interpolation. [Valve paper](https://cdn.akamai.steamstatic.com/apps/valve/2007/SIGGRAPH2007_AlphaTestedMagnification.pdf).

## Ready implementations compared

| Candidate | Inspected revision | License | Fit and limitation |
| --- | --- | --- | --- |
| TinySDF | `45865e7f2d7613ebcb95ad459993b7e78febfa3f` | BSD-2-Clause | Existing generator already follows this distance-transform family. Browser Canvas input would replace working DirectWrite shaping/fallback without addressing layout jumps. |
| msdfgen | `1c106ed8117893bf943e577f62eb0665fb271e46` | MIT | Three-channel fields improve sharp corners; introduces outline/font loading and RGB atlas/shader changes. A quality option for extreme magnification, not a fix for metadata layout. |
| bgfx font system | `7346c3e731bd65f35c7e6a99819840e554f5b748` | BSD-2-Clause project license | Demonstrates cached SDF glyphs and scaled fonts. Replacing an already batched D3D11/WPF bridge would add backend integration work. |
| Unity TextMeshPro | package documentation 3.2 | Unity package terms | Validates SDF font assets as a way to preserve transformed text contours; it is a Unity engine subsystem rather than a drop-in WPF module. |

TinySDF's own documentation describes a system-font SDF generated with the
Felzenszwalb/Huttenlocher transform. [Pinned source](https://github.com/mapbox/tiny-sdf/tree/45865e7f2d7613ebcb95ad459993b7e78febfa3f),
[license](https://github.com/mapbox/tiny-sdf/blob/45865e7f2d7613ebcb95ad459993b7e78febfa3f/LICENSE.txt).

msdfgen reconstructs RGB fields with a median and requires linear rather than
sRGB channels. Its documentation explicitly permits a precomputed pixel-range
value for 2D rendering: fragment derivatives are useful when scale varies across
a perspective image, not a requirement for this uniform 2D canvas.
[Pinned source and shader explanation](https://github.com/Chlumsky/msdfgen/tree/1c106ed8117893bf943e577f62eb0665fb271e46),
[license](https://github.com/Chlumsky/msdfgen/blob/1c106ed8117893bf943e577f62eb0665fb271e46/LICENSE.txt).

bgfx example 11 renders a single distance-field font at varying sizes; the font
manager caches glyph data and scaled font references.
[Examples](https://bkaradzic.github.io/bgfx/examples.html#fontsdf),
[pinned font manager](https://github.com/bkaradzic/bgfx/blob/7346c3e731bd65f35c7e6a99819840e554f5b748/examples/common/font/font_manager.cpp),
[license](https://github.com/bkaradzic/bgfx/blob/7346c3e731bd65f35c7e6a99819840e554f5b748/LICENSE).
TextMeshPro similarly stores glyph contour distance in its font atlases.
[SDF font assets](https://docs.unity3d.com/Packages/com.unity.textmeshpro@3.2/manual/FontAssetsSDF.html).

No external runtime was added by this review. Reusing the existing working
distance-field renderer plus correcting layout is the shortest stable path.

## Mip strategy

Keep icon mipmaps: every array slice is one complete icon, so mip filtering
cannot sample a neighboring icon. They are generated once during preparation
and cached, rather than regenerated during camera motion.

Keep the independently generated glyph size tiers as the current glyph LOD.
Unlike an icon, each glyph page contains many packed fields. Blindly generating
mips across a whole page can blend neighboring fields once their padding shrinks.
Also, averaging distance values is not the same operation as generating a new
distance field from the lower-resolution outline. This is an engineering
inference from the representation and packing, not a prohibition in D3D11.
A future glyph mip experiment needs per-glyph padding, consistent distance
units, independent contour/coverage tests and measured minification benefit.

D3D11 `GenerateMips` recursively generates the lower levels and requires render
target, shader resource and generate-mips flags on the resource. The current
single-level glyph array was deliberately not created with those flags.
[Microsoft API requirements](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-generatemips).

## Follow-up candidates, ordered by risk

1. Correct metadata width allocation and exact GPU trim. Measure filename-prefix
   monotonicity and frame cost together. No extra texture or dependency needed.
2. Avoid a missing-glyph gap at a 12/28-pixel tier boundary: if a requested glyph
   tier is pending, temporarily use an already prepared neighboring tier with
   that tier's own range and metrics. Keep requesting the optimal tier. Test
   fallback scripts, ellipsis, async arrival and all three tiers.
3. If a cold script shows upload hitches, measure `GlyphAtlasTexture.Upload`
   before changing its 2,048-rectangle budget. Adjacent dirty rectangles on a
   page can potentially be coalesced, but larger copies may cost more than
   individual updates. This is a benchmark-dependent candidate.
4. World-space reusable glyph/scene batches could reduce motion uploads, but
   current output is screen-space, clipped and has size-dependent labels. This
   needs a separate benchmark-backed change, not an unverified rewrite.

Baking the entire scene is not an immediate improvement: the scene changes as
directories arrive and sort, and text/detail visibility depends on scale. A
full scene texture would require invalidation and increasing-resolution rebuilds
while moving the camera. Existing geometry instancing and SDF atlases preserve
sharpness and update individual data instead.

## Reproducible checks and benchmarks

Build and run from an isolated artifact/state directory. These commands are
templates, not a claim that they were run by this review. Run GPU benchmarks
sequentially, on the same monitor and adapter, with warm-up complete. Record
both cold and warm runs; compare medians of at least three warm runs.

```powershell
dotnet build tests/ViewAllSmoke/ViewAllSmoke.csproj -c Release --artifacts-path artifacts/zoom-review -p:UseSharedCompilation=false
$env:ULTRAEXPLORER_STATE_DIR = 'E:\AiControl\UltraExplorer\artifacts\zoom-review-state'
$env:ULTRAEXPLORER_TEST_WINDOW = '1'
# Use the generated ViewAllSmoke.exe output path:
ViewAllSmoke.exe --only GpuTextChecks,GpuLabelChecks,GpuIconAtlasChecks,LabelCostChecks,FrameWorkChecks,FrameScopeChecks
# Use the generated UltraExplorer.exe output path:
UltraExplorer.exe --nested-bench E:\AiControl\UltraExplorer\artifacts\zoom-review-default.tsv --bench-window 1920x1040 --bench-scale 1.5 --bench-layout 0.742
UltraExplorer.exe --nested-snapshots E:\AiControl\UltraExplorer\artifacts\zoom-review-shots --bench-window 1920x1040 --bench-scale 1.5 --bench-layout 0.742
```

The built-in report includes frame p50/p95/max, UI and label costs, upload time,
GPU time, allocation/collection counts, hitches and input-probe delays. Its
`.frames.tsv` contains per-frame values. Verify that both scene and labels
actually ran on the GPU, and compare zoom, pan, settled and dense-file phases
separately. The existing zero-allocation and upload-skip tests are relevant
regression gates. Free-space values and live directory content can change
between snapshots and should not be mistaken for rendering differences.

## Paired application measurements

Ran three alternating before/after pairs of the existing default benchmark,
not an offscreen approximation. The before binary was copied from the installed
`32c9943` build; the after binary was built in Release from the combined zoom
fixes after the layout agent froze its changes. Build completed with zero
warnings/errors. No ordinary user window was controlled or closed.

Each benchmark used a new isolated state folder seeded from the same existing
GPU/icon/JIT cache directory. The owned diagnostics window used DISPLAY2, the
secondary monitor, `ULTRAEXPLORER_TEST_WINDOW=1`, and the application's
`ShowActivated=false` / `WS_EX_NOACTIVATE` diagnostic placement. Settings and
the main workspace were not shared. Window parameters were `1920x1040`, scale
`1.5`, layout `0.742`; the renderer reported `3395x1685` actual canvas pixels on
an NVIDIA GeForce RTX 4070 Ti. Each process exited normally in 11–20 seconds.
Across all 66 measured phase rows, scene and labels used the GPU for every
rendered frame.

Raw reports, per-frame reports and the comparison are in
`artifacts/zoom-paired/`. `run-pair.ps1` records the exact invocation and caps
only its owned process at 120 seconds. `comparison.csv` gives the median of
three per-run values plus their observed minimum/maximum.

| Phase / metric | Before median | After median |
| --- | ---: | ---: |
| Dense file zoom, frame p50 | 1.20 ms | 1.02 ms |
| Dense file zoom, frame p95 | 2.45 ms | 1.81 ms |
| Dense file zoom, labels p50 | 0.16 ms | 0.15 ms |
| Dense file zoom, upload p50 | 0.03 ms | 0.03 ms |
| Dense file zoom, GPU p50 | 0.423 ms | 0.403 ms |
| Dense file zoom, allocation per frame | 26.3 KB | 27.6 KB |
| Dense file zoom, glyph count | 6,261 | 6,696 |
| File pan, labels p50 | 0.05 ms | 0.03 ms |
| Whole-tree zoom in, frame p95 | 2.83 ms | 2.57 ms |

These runs show no material observed rendering-cost regression and inexpensive
label/upload work with more readable text. They do **not** establish a causal
percentage speedup: this route reads live system directories and its loaded
scene/glyph counts vary; the first before run also had approximately 15.6 ms
frame intervals while later runs often had approximately 4.2 ms intervals.
GPU wait/present times consequently vary substantially. The allocation figure
includes the application's whole frame, not solely the allocation-free glyph
emitter. Keep the narrower measured claim and the separate functional zoom
regression tests; use a fixed loaded synthetic scene and controlled compositor
cadence for a future throughput comparison.

Binary SHA256 identifiers:

- Before `UltraExplorer.dll`: `F65D3FA90572AF69E31BDE07022620783A7EC04EBE4DC73D33F864E6DB94224D`.
- After `UltraExplorer.dll`: `175A4661AA67C29ECFC786041EAD5D1A2F2788A4AE27CBAC38E826BD661D7385`.

The optional camera/stream/live partial route declarations exist in the main
source, but their implementation files were absent from this checkout, so the
paired test used the implemented default route.
