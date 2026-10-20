# File content at deep zoom

Zooming in over a file remembers that literal file independently of selection
and hover. Its card can grow beyond the ordinary file-folder limit. Zooming
toward a folder uses the existing folder navigation; zooming out of a file
remains gradual. A file's grid position, selection and hit area do not change.
The expanded file suppresses its duplicate floating hover card so it cannot
cover the content being inspected. Other files retain their ordinary hover previews.

At sufficiently large card size, the content region grows with the card.
The filename stays at 13 DIP in a header within the visible card. Images retain
their aspect ratio and their position during pointer zoom. Documents use
direct text drawing at 14 DIP with 20 DIP lines, so further zoom reveals a
larger reading area without enlarging the letters. Text layout is cached and
replayed without scaling during motion.

Ordinary sibling cards retain their small thumbnails. Only the file being
inspected requests higher quality content after 120 ms of rest: images/PDF
pages up to 2048 pixels, or at most 32,768 document characters. The focused
presentation cache holds one result and is limited to 16 MiB; decoder working
memory can be larger during extraction. Other formats reuse the window's
existing bounded background thumbnail service.

The render thread does not read files or decode content. Cancellation and
listing-version checks discard stale results; an uninterruptible provider
continues to own its actual slot until it returns. A retained hidden pane
keeps its camera/target but drops detail pixels and text drawings. Closing a
pane releases the target and controller. A stable file is not repeatedly
decoded on a timer.

`FocusedFileDetailChecks` covers the bounded controller and actual image/text
decoders. `FileDetailZoomChecks` covers camera targeting, content growth,
fixed glyph sizes, selection, neighboring folders, real CPU/GPU composition
and hidden/closed lifetime using isolated owned fixtures without user input.
