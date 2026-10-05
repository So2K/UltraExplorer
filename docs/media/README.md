# Demo recordings

These APNGs and GIFs show the real UltraExplorer WPF interface and nested canvas running
over generated demonstration files. They contain no personal files.

| Recording | Scenario |
| --- | --- |
| overview.gif | Workspace overview → Projects → Aurora → overview |
| deep-zoom.gif | Overview → Exports, 12 directory levels deep → Library → Exports |
| search.gif | Search the demo workspace for `shot`, then reveal `shot-014-final.png` |
| tags.gif | Navigate between four already coloured folder tags |
| split-view.gif | Move each of two independent canvas cameras |
| explorer-comparison.png | Real Explorer Details view and UltraExplorer on the same 12-level destination |

The primary APNGs use Windows Graphics Capture at 30 fps. The app runs its CPU
fallback; the recording and lossless video encoding run separately from its UI
thread. The measured written rate is 30.00–30.10 fps. Frame timings are retained;
playback is not sped up. The demonstration workspace is preloaded for the navigation
sequences. Search uses the real filesystem walker restricted to that generated
workspace, followed by the product's ranking, highlighting and result UI.
These are interaction demonstrations, not disk or renderer benchmarks. The
comparison's left pane is an actual Windows Explorer Details view of Exports;
the right pane shows the real animated camera. Both programs can paste the
same full path. Explorer has no artificial delay, and the comparison does not
measure which program reads a folder faster. Its sidebar is cropped from the
public recording to omit personal pins.

To reproduce on Windows with .NET 10 and FFmpeg:

```powershell
dotnet publish tools/DemoRecorder/DemoRecorder.csproj -c Release --self-contained true -o artifacts/demo-recorder
dotnet build tools/ExplorerDemoRecorder/ExplorerDemoRecorder.csproj -c Release
$env:ULTRAEXPLORER_DEMO_CAPTURE = (Resolve-Path tools/ExplorerDemoRecorder/bin/Release/net10.0-windows10.0.19041.0/win-x64/ExplorerDemoRecorder.exe).Path
artifacts/demo-recorder/ViewAllSmoke.exe artifacts/public-demos
```

Each native-capture sequence contains a lossless `capture.mkv` and `timing.json`
with elapsed timestamps. Encode the primary APNG at its recorded 30 fps:

```powershell
ffmpeg -i artifacts/public-demos/deep-zoom/capture.mkv -vf "scale=1100:-2:flags=lanczos,format=rgba" -c:v apng -compression_level 9 -pred mixed -plays 0 -f apng docs/media/deep-zoom.png
```

The `.png` extension lets GitHub embed the APNG as an ordinary image. GIF
fallbacks use 15 fps and a smaller palette to limit download size. Without the
capture helper, DemoRecorder retains an offline bitmap-recording fallback.

All demo media is distributed under the repository's MIT license.
