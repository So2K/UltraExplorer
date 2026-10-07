# Space pan: immediate input and smooth presentation

Audit: 2026-10-07. Base: installed 49de903 plus current Space preview integration in codex/universal-preview. This note records source inspection and primary-source research. No physical input or latency benchmark was run for this audit.

## Verified input issues

NestedCanvas.Input.PressLeft originally classified held Space as Pan but left the press pending. PointerMove then applied the ordinary Windows horizontal/vertical drag threshold. The threshold was SystemParameters.MinimumHorizontalDragDistance / MinimumVerticalDragDistance, rather than a hard-coded 8 pixels. Crossing it applied the whole displacement from the initial press, producing a dead zone followed by a position jump.

NestedCanvas.Camera.Pan already updates the camera immediately in screen DIPs, stops a flight, normalizes its local anchor and requests a redraw. NestedCanvas.Frame.RequestFrame coalesces redraws on CompositionTarget.Rendering: several mouse events before the next frame need one drawing pass.

The Tree canvas custom Space pan in MainWindow.xaml.cs already uses exact affine tracking: viewport equals initial viewport minus pointer displacement divided by zoom. Two additional issues require attention:

- Setting ViewportLocation invokes its subscribed dependency-property callback and PushViewport. The move handler then calls PushViewport again, repeating model notifications, save scheduling and visual updates.
- Custom mouse capture does not begin a Nodify panning operation. Its auto-pan timer can therefore see capture with IsPanning=false and add edge offsets independently of the custom pointer anchor. Nodify exposes BeginPanning and EndPanning. Its own panning state uses direct pointer displacement divided by zoom. [Pinned Nodify 7.3 implementation](https://raw.githubusercontent.com/miroiu/nodify/v7.3.0/Nodify/Editor/NodifyEditor.Panning.cs), [panning state](https://raw.githubusercontent.com/miroiu/nodify/v7.3.0/Nodify/Editor/States/Panning.cs).

SmoothMotion currently has no implementation; StepCameraMotion is an unimplemented partial hook and CameraMotionChecks is empty. Existing F/tag/folder flights use time-based van Wijk–Nuij zoom-and-pan, which serves navigation animations rather than direct pointer tracking.

## Ready algorithms considered

| Approach | Source | Suitability |
|---|---|---|
| Direct affine tracking with coalesced presentation | Existing camera and Nodify | Immediate input, exact pointer correspondence, no overshoot or release coast. Remove the known dead zone and conflicting update paths. |
| Adaptive 1€ low-pass filter | Casiez, Roussel and Vogel | Useful for measured input noise. Cutoff increases with speed to reduce lag, while a lower cutoff reduces slow-motion jitter. These remain competing goals. |
| Exact critically damped spring | Ryan Juckett | Suitable for deliberately eased following. Exact time-based integration avoids frame-rate-dependent numerical stepping. Following introduces delay; retained velocity can cross a newly moved target. |

The [1€ authors' page](https://gery.casiez.net/1euro/) gives a tuning procedure and verified implementations. The inspected [author's JavaScript implementation](https://github.com/casiez/OneEuroFilter/blob/main/javascript/OneEuroFilter.js) filters a speed estimate, adapts the cutoff, then filters position. This is a ready algorithm if jitter remains after correcting the input path. Its parameters need calibration in screen DIPs and seconds; lowering the cutoff cannot eliminate jitter without increasing lag.

[Juckett's primary derivation and reference implementation](https://www.ryanjuckett.com/damped-springs/) provide an exact critically damped update relative to a fixed target:

    xNext = (x + (v + omega*x)*dt) * exp(-omega*dt)
    vNext = (v - omega*(v + omega*x)*dt) * exp(-omega*dt)

These fixed-target equations alone do not guarantee no lag or no overshoot under arbitrary retargeting. A direct-drag spring would need clamping and an explicit release reset. Copying Juckett's reference code also requires retaining its published license and identifying modifications.

## Targeted change

The nested Space press now initializes an explicit moved Pan, its pointer anchor and cursor before capture, skips the irrelevant file hit test, and stops the current flight. The first nonzero movement reaches the existing exact camera mapping immediately. Ordinary file dragging, marquee and right-click thresholds remain intact. No new filter or frame timer is introduced.

Root owns the corresponding Tree change: begin/end the existing Nodify panning operation around custom tracking and remove redundant viewport updates. If Space movement without a mouse button is desired, initialize the same direct mapping from the pointer at keydown and avoid the ordinary mouse-press threshold.

A filter is not recommended as the default before measured jitter demonstrates a need. The concrete source defects already explain delayed registration and the initial jump.

## Acceptance evidence

ImmediatePanChecks drives the actual NestedPointer handlers over a bounded fake filesystem. It covers a first 0.125 DIP movement, stationary capture, full accumulated displacement, reverse movement, one display frame for several pointer events, no release coast, capture loss, unchanged selection/file operations and ordinary click/drag/right-menu behavior.

Root should run those checks and the adjacent selection, Space tap, F and frame suites. A real frame trace can then measure handler cost, redraw count and input-to-present time. At 120 Hz, one display interval is approximately 8.33 ms; this is not an established end-to-end latency guarantee.
