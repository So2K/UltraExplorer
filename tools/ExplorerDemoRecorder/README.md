# File Explorer comparison recorder

A capture helper, not an automation tool. It records only the exact, already
observed File Explorer window supplied by HWND. It sends no keyboard/mouse
input, changes no window state, and never launches Explorer. Drive the window
manually or through the project's Computer Use workflow over generated files.
For an application's own test harness, `--app-window` accepts its exact HWND
instead. This flag still performs no app input or window manipulation.

```powershell
dotnet build tools/ExplorerDemoRecorder/ExplorerDemoRecorder.csproj -c Release -p:SelfContained=true
tools/ExplorerDemoRecorder/bin/Release/net10.0-windows10.0.19041.0/win-x64/ExplorerDemoRecorder.exe --window 123456 --out artifacts/explorer-comparison --seconds 120 --fps 30
```

The output is a lossless BGRA FFV1 `capture.mkv`, `timing.json` with real frame
times and markers, and `ffmpeg.log`. FFmpeg must be available on PATH or passed
with `--ffmpeg`. Use a new output folder for each take. Keep the target window
at the same size during recording. Standard input accepts `mark <label>` and
`stop`; those commands only affect recording metadata.

The recorder uses Windows.Graphics.Capture and D3D11, including composited
content. Windows' visible recording border remains enabled. It makes no
permission requests. Static content is repeated at the requested output rate;
the JSON records actual frame-delivery times, so do not claim smoothness or
timings without inspecting those values.

Convert the lossless recording to an APNG without accelerating its timeline:

```powershell
ffmpeg -i artifacts/explorer-comparison/capture.mkv -vf "fps=30,scale=1280:-1:flags=lanczos" -plays 0 -f apng artifacts/explorer-comparison/explorer.png
```

Interop adapted from primary Microsoft references:

- [Win32 capture sample](https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/tree/master/dotnet/WPF/ScreenCapture)
- [C#/WinRT interop guide](https://github.com/microsoft/CsWinRT/blob/master/docs/interop.md)
- [Capture an HWND](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow)
