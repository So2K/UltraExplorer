# Demo recordings

These GIFs show the real UltraExplorer WPF interface and nested canvas running
over generated demonstration files. They contain no personal files.

| Recording | Scenario |
| --- | --- |
| overview.gif | Workspace overview → Projects → Aurora → overview |
| deep-zoom.gif | Overview → Exports, 12 directory levels deep → Library → Exports |
| search.gif | Search the demo workspace for `shot`, then reveal `shot-014-final.png` |
| tags.gif | Navigate between four already coloured folder tags |
| split-view.gif | Move each of two independent canvas cameras |

The recordings use the CPU fallback so the visual tree can be captured
deterministically. Frames retain their measured elapsed times; the GIFs are
not sped up. The demonstration workspace is preloaded for the navigation
sequences. Search uses the real filesystem walker restricted to that generated
workspace, followed by the product's ranking, highlighting and result UI.
These are interaction demonstrations, not disk or renderer benchmarks.

To reproduce on Windows with .NET 10 and FFmpeg:

```powershell
dotnet publish tools/DemoRecorder/DemoRecorder.csproj -c Release --self-contained true -o artifacts/demo-recorder
artifacts/demo-recorder/ViewAllSmoke.exe artifacts/public-demos
```

Each sequence contains PNG frames, `frames.txt` with measured frame durations,
and `timing.json`. Encode the frames at 15 fps and 1100 px wide with FFmpeg:

```powershell
ffmpeg -f concat -safe 0 -i artifacts/public-demos/deep-zoom/frames.txt -vf "fps=15,scale=1100:-2:flags=lanczos,split[s0][s1];[s0]palettegen=max_colors=192:stats_mode=diff[p];[s1][p]paletteuse=dither=bayer:bayer_scale=3" -loop 0 docs/media/deep-zoom.gif
```

All demo media is distributed under the repository's MIT license.
