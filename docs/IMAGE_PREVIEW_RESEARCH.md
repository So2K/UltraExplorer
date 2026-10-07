# Image preview engine: ImageGlass inspection

Checked on 2026-10-07 against ImageGlass `develop` commit
`2cf91deeec36310ff1b05d0a9039519179d5534b` and the official Magick.NET releases.

## Ready components

ImageGlass currently builds its viewer library on .NET 10, Avalonia, SkiaSharp,
Magick.NET and SVG controls. `ImageGlass.Lib` is a composed application library
rather than a standalone WPF image control. Its source license is GPLv3. The
independent Magick.NET decoder is available as a NuGet library under Apache-2.0;
using that component does not require copying ImageGlass application code.

The inspected loader uses single-frame read settings, JPEG downsample hints,
embedded-preview checks, thumbnail resizing and EXIF auto-orientation. It keeps
metadata/decode work off the UI and uses Magick as its broad-format fallback.
Those existing library operations fit UltraExplorer's thumbnail workers and
owned Quick Look window without launching a second image-viewer application.

## Integration choice

Keep Windows WIC for PNG/JPEG/BMP/TIFF/ICO and related Windows codecs. They
already have bounded output, detached file handles and verified EXIF handling.
Use `Magick.NET-Q8-x64` **14.17.2** as a lazy fallback for WebP, AVIF, HEIC, JXL,
PSD, DDS, TGA, EXR and supported camera/scientific images. Q8 is appropriate for
an 8-bit WPF preview and uses fewer resources than ImageGlass's Q16 HDRI build.
No claim of measured speed is made before fixture benchmarking.

The official NuGet version index and release verify 14.17.2, published
2026-09-27. Cached package search results still showed 14.16.0; do not use them
as the current version. Version 14.17.2 includes ImageMagick 7.1.2-32 and fixes
in EXR, PSD and other native decoders.

The adapter should read only one frame, keep the maximum output dimension,
check source dimensions, limit native memory/disk/thread resources, prohibit
external delegates and script/filter execution, and honor cancellation before
and after native work. Native decoder internals are not universally preemptible.
SVG needs an explicit restricted renderer/policy or a separate safe parser;
ordinary SVG must never open a browser or execute scripts. Animation can reuse
the existing owned mpv viewport rather than allocate a whole frame collection.

## Primary sources

- ImageGlass project: https://github.com/d2phap/ImageGlass
- Inspected viewer dependencies: https://github.com/d2phap/ImageGlass/blob/2cf91deeec36310ff1b05d0a9039519179d5534b/source/ImageGlass.Lib/ImageGlass.Lib.csproj
- Loader: https://github.com/d2phap/ImageGlass/blob/2cf91deeec36310ff1b05d0a9039519179d5534b/source/ImageGlass.Lib/Common/Photoing/Codecs/MagickCodecs/MagickCodec.cs
- ImageGlass source license: https://github.com/d2phap/ImageGlass/blob/2cf91deeec36310ff1b05d0a9039519179d5534b/LICENSE
- Magick.NET package choices: https://github.com/dlemstra/Magick.NET/blob/main/docs/Readme.md
- Pinned release: https://github.com/dlemstra/Magick.NET/releases/tag/14.17.2
- Version index: https://api.nuget.org/v3-flatcontainer/magick.net-q8-x64/index.json
- Resource/policy guidance: https://imagemagick.org/security-policy/
