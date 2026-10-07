# Space pan: immediate input and smooth presentation

Source inspection and algorithm research: 2026-10-07. This note describes the
Space-pan implementation used for the v1.3.0 beta and the alternatives examined.
No physical input or end-to-end latency benchmark was run for this research.

## Input and presentation

The earlier nested Space press remained pending until pointer movement crossed
the ordinary Windows drag threshold. Applying the accumulated displacement
then produced a dead zone followed by a jump. Space now arms panning
immediately, without that file-drag threshold or the 200 ms tap-classification
delay. Holding Space and moving the pointer also works without a mouse button.
Ordinary file dragging, marquee and right-click thresholds retain their roles.

The nested camera consumes pointer deltas in screen DIPs and stops an active
navigation flight. The tree canvas uses direct affine tracking:

```text
viewport = initialViewport - (pointer - initialPointer) / zoom
```

The tree gesture begins and ends Nodify's panning state and disables independent
edge auto-pan while custom tracking is active. The viewport dependency-property
callback updates the model once; the move handler does not repeat that update.
Release and capture loss end the gesture without retained pan velocity.

The existing `CompositionTarget.Rendering` path coalesces drawing requests, so
several pointer events before a frame require one drawing pass. Navigation with
`F`, Tags and folders keeps its time-based van Wijk–Nuij camera flight; direct
pointer tracking uses the current pointer position.

Nodify references: [pinned 7.3 editor implementation](https://raw.githubusercontent.com/miroiu/nodify/v7.3.0/Nodify/Editor/NodifyEditor.Panning.cs)
and [panning state](https://raw.githubusercontent.com/miroiu/nodify/v7.3.0/Nodify/Editor/States/Panning.cs).

## Algorithms considered

| Approach | Source | Assessment |
| --- | --- | --- |
| Direct affine tracking with coalesced presentation | Existing camera and Nodify | Preserves pointer correspondence and adds no release coast. This is the implemented approach. |
| Adaptive 1€ low-pass filter | Casiez, Roussel and Vogel | Can reduce measured input noise. A lower cutoff reduces slow-motion jitter while increasing lag; calibration needs screen DIPs and seconds. |
| Exact critically damped spring | Ryan Juckett | Useful for deliberately eased following. Exact time-based integration avoids numerical frame-rate dependence, but following introduces delay and retargeting needs explicit rules. |

The [1€ authors' page](https://gery.casiez.net/1euro/) provides tuning guidance;
the [reference JavaScript implementation](https://github.com/casiez/OneEuroFilter/blob/main/javascript/OneEuroFilter.js)
filters speed, adapts the cutoff and then filters position. A filter would need
measured jitter evidence before replacing direct tracking.

[Juckett's derivation and implementation](https://www.ryanjuckett.com/damped-springs/)
give this exact critically damped update relative to a fixed target:

```text
xNext = (x + (v + omega*x)*dt) * exp(-omega*dt)
vNext = (v - omega*(v + omega*x)*dt) * exp(-omega*dt)
```

These equations do not guarantee zero lag or no overshoot under arbitrary
retargeting. A direct-drag spring would need clamping and a release reset.
Reusing the reference code also requires retaining its license and identifying
modifications. No filter or spring is added by the current pan implementation.

## Verification and measurement limits

`ImmediatePanChecks` drives the actual nested pointer handlers over a bounded
fake filesystem. Its scenarios include a first 0.125 DIP movement, exact
accumulated and reverse displacement, one drawing frame for several events,
release/capture loss, selection preservation and ordinary pointer actions.
`SpacePreviewWindowChecks` covers the window's selected-file tap and held-Space
pointer path. Run these with adjacent selection, focus and frame checks using
the isolated harness described in [BUILD.md](../BUILD.md).

These fixtures describe behavior and test methods; they are not physical
pointer measurements or a completed result for every build. A real frame trace
is needed to measure handler cost, redraw count and input-to-present latency.
At 120 Hz one display interval is about 8.33 ms, which is not an end-to-end
latency guarantee.
