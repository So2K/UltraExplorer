# Capacity above a drive

The nested canvas shows a small rounded capacity meter just above each drive
card. Its filled portion represents used space: cyan below 90%, amber from
90%, and red from 95%. A completely full drive has a full red meter. Unknown,
zero or negative capacity data does not fabricate a full drive. Normal folder
cards have no meter.

The meter stays 6–9 DIP high while zooming. On a drive wider than the viewport,
its complete proportion remains visible over the visible part of the card.
It uses the same label target for CPU and GPU and leaves titles, selection,
hit areas and folder layout unchanged.

Numeric capacity comes from the existing background drive-description sample,
also used for the free-space caption. Initial, late and device-rescan roots
carry it through to retained nested roots. No extra drive queries, timers or
render-thread filesystem work are introduced. Refresh and unavailable-drive
behavior follow the existing drive-description lifecycle; this is not a new
continuous storage monitor. Ordinary folders do not allocate a capacity object.

`DriveCapacityChecks` uses synthetic drives, pixel checks at DPI 1 and 1.5,
the actual WARP label path and retained-root updates without moving selection
or the camera. `DRIVE_CAPACITY_CAPTURE_DIR` saves those fixture images.
