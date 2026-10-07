using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using UltraExplorer.Controls;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task PreviewToolsChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(PreviewToolsChecks), StringComparison.OrdinalIgnoreCase))
        { RunGroupInOwnProcess(nameof(PreviewToolsChecks)); return Task.CompletedTask; }
        RunOnSta("portable preview engines", PreviewToolsOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task PreviewToolsOnStaAsync()
    {
        Section("portable preview engines: actual content, native embedding, IPC and owned lifecycle");
        Check("3D formats include CAD, USD, mesh and literal extensions", PreviewTools.IsModel("piece.STEP") && PreviewTools.IsModel("asset.usdz") && PreviewTools.IsModel("mesh.obj. "));
        Check("media recognizes audio/video without claiming TypeScript or executables", PreviewTools.IsMedia("video.MP4") && PreviewTools.IsMedia("sound.flac.") && !PreviewTools.IsMedia("code.ts") && !PreviewTools.IsModel("run.exe"));
        var f3d = PreviewTools.FindF3d(); var mpv = PreviewTools.FindMpv();
        Check("pinned portable F3D and mpv runtimes are present", f3d is not null && mpv is not null);
        if (f3d is null || mpv is null) return;
        if (Application.Current is null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/UltraExplorer;component/Themes/UltraTheme.xaml", UriKind.Relative) });
        }
        var directory = Path.Combine(Path.GetTempPath(), "UltraExplorerNativePreviewChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var artifacts = Path.Combine(Environment.CurrentDirectory, "artifacts", "universal-preview", "shots");
        Directory.CreateDirectory(artifacts);
        try
        {
            var model = Path.Combine(directory, "literal mesh with spaces.obj");
            await File.WriteAllTextAsync(model, "v 0 0 0\nv 1 0 0\nv 0 1 0\nv 0 0 1\nf 1 3 2\nf 1 2 4\nf 1 4 3\nf 2 3 4\n");
            var video = Path.Combine(directory, "literal video with spaces.avi");
            PreviewToolsWriteVideo(video);
            var portrait = Path.Combine(directory, "portrait.avi"); PreviewToolsWriteVideo(portrait, 64, 96);
            var wave = Path.Combine(directory, "sound with spaces.wav"); PreviewToolsWriteWave(wave);
            var modelBefore = await File.ReadAllBytesAsync(model);
            var videoBefore = await File.ReadAllBytesAsync(video);
            var modelBitmap = await PreviewTools.RenderModelAsync(model, 256);
            var mediaBitmap = await PreviewTools.RenderMediaAsync(video, 256);
            Check("F3D produces a frozen bounded actual OBJ thumbnail", modelBitmap is { IsFrozen: true, PixelWidth: 256, PixelHeight: 256 } && PreviewToolsVariedPixels(modelBitmap));
            Check("mpv decodes actual AVI pixels into a frozen bounded frame", mediaBitmap is { IsFrozen: true } && mediaBitmap.PixelWidth <= 256 && mediaBitmap.PixelHeight <= 256 && PreviewToolsVariedPixels(mediaBitmap));
            var portraitBitmap = await PreviewTools.RenderMediaAsync(portrait, 192);
            Check("a portrait video preserves aspect ratio and both dimension bounds", portraitBitmap is { IsFrozen: true } && portraitBitmap.PixelWidth < portraitBitmap.PixelHeight && portraitBitmap.PixelWidth <= 192 && portraitBitmap.PixelHeight <= 192);
            if (modelBitmap is not null) PreviewToolsSavePicture(modelBitmap, Path.Combine(artifacts, "native-model-thumbnail.png"));
            if (mediaBitmap is not null) PreviewToolsSavePicture(mediaBitmap, Path.Combine(artifacts, "native-media-thumbnail.png"));
            var literalModel = DocumentPreviewService.LiteralPath(Path.Combine(directory, "mesh.obj. "));
            await File.WriteAllBytesAsync(literalModel, modelBefore);
            await File.WriteAllTextAsync(Path.Combine(directory, "mesh.obj"), "This normalized neighbour is not the requested mesh.");
            var literalBitmap = await PreviewTools.RenderModelAsync(literalModel, 192);
            Check("F3D renders the exact trailing-dot/space file, never its different normalized neighbour", literalBitmap is { PixelWidth: 192 } && PreviewToolsVariedPixels(literalBitmap));
            using (var cancellation = new CancellationTokenSource())
            {
                var pending = PreviewTools.RenderModelAsync(model, 1024, cancellation.Token);
                cancellation.Cancel();
                var cancelled = false;
                try { await pending; } catch (OperationCanceledException) { cancelled = true; }
                Check("render cancellation propagates and releases its owned process", cancelled && PreviewTools.ActiveProcessCount == 0);
            }
            var host = new NativePreviewHost();
            var window = new Window { Content = host, Width = 740, Height = 500, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000 };
            window.SourceInitialized += (_, _) => DialogNative.CloakOwn(new WindowInteropHelper(window).Handle, true);
            try
            {
                window.Show();
                try { await host.LoadAsync(model); }
                catch { Console.Error.WriteLine("Native attachment diagnostic: " + host.LastNativeDiagnostic); throw; }
                var modelPid = host.OwnedProcessId;
                Check("F3D is embedded under this owned viewport HWND", modelPid is not null && host.ModelWindowHandle != IntPtr.Zero && PreviewToolsGetParent(host.ModelWindowHandle) == host.ViewportHandle);
                var modelButtons = PreviewNativeDescendants(host).OfType<Button>().Where(b => b.IsVisible).ToArray();
                Check("the 3D viewport exposes only its compact fit/reset control", modelButtons.Length == 1 && modelButtons[0].Content as string == "Fit / reset");
                await Task.Delay(250);
                host.RequestModelSnapshot();
                var snapshot = host.ModelSnapshotPath!;
                var clock = Stopwatch.StartNew();
                while (!File.Exists(snapshot) && clock.ElapsedMilliseconds < 5000) await Task.Delay(40);
                var captured = File.Exists(snapshot) && new FileInfo(snapshot).Length > 100;
                Check("the embedded F3D viewport produces actual rendered pixels on a local screenshot request", captured);
                if (captured) File.Copy(snapshot, Path.Combine(artifacts, "native-model-viewport.png"), true);
                window.Width = 920; window.Height = 640; window.UpdateLayout();
                await Task.Delay(80);
                Check("the embedded model follows the preview viewport's resized client area", PreviewToolsGetClientRect(host.ViewportHandle, out var parent) && PreviewToolsGetClientRect(host.ModelWindowHandle, out var child) && parent.Right == child.Right && parent.Bottom == child.Bottom);
                await host.LoadAsync(video);
                Check("switching from model to media terminates only its previous F3D process", modelPid is not null && await PreviewToolsWaitForExit(modelPid.Value) && host.OwnedProcessId is not null && host.ModelWindowHandle == IntPtr.Zero);
                var pause = await host.MpvCommandAsync(["get_property", "pause"]);
                Check("media starts paused and its own named pipe answers JSON IPC", pause is { } response && response.TryGetProperty("data", out var paused) && paused.ValueKind == System.Text.Json.JsonValueKind.True);
                var osc = await host.MpvCommandAsync(["get_property", "options/osc"]);
                var shortcuts = await host.MpvCommandAsync(["get_property", "options/input-default-bindings"]);
                Check("the media engine has no foreign OSC or default shortcut interface", osc is { } osd && osd.GetProperty("data").ValueKind == System.Text.Json.JsonValueKind.False
                    && shortcuts is { } keys && keys.GetProperty("data").ValueKind == System.Text.Json.JsonValueKind.False);
                var mediaControls = PreviewNativeDescendants(host).ToArray();
                Check("the compact player provides its own timeline, volume and six speed choices", mediaControls.OfType<Slider>().Count(s => s.IsVisible) == 2
                    && mediaControls.OfType<ComboBox>().Single().Items.Count == 6 && mediaControls.OfType<Button>().Any(b => b.Content as string == "Stop"));
                host.UpdateLayout();
                var controlsPicture = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(host.ActualWidth)),
                    Math.Max(1, (int)Math.Ceiling(host.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
                controlsPicture.Render(host);
                PreviewToolsSavePicture(controlsPicture, Path.Combine(artifacts, "native-controls.png"));
                await host.SetSpeedAsync(2); await host.SetVolumeAsync(35);
                var speed = await host.MpvCommandAsync(["get_property", "speed"]);
                var volume = await host.MpvCommandAsync(["get_property", "volume"]);
                Check("custom speed and volume controls update only their owned media engine", speed is { } rate && Math.Abs(rate.GetProperty("data").GetDouble() - 2) < 0.001
                    && volume is { } loudness && Math.Abs(loudness.GetProperty("data").GetDouble() - 35) < 0.001);
                var frame = Path.Combine(artifacts, "native-media-viewport.png");
                var screenshot = await host.MpvCommandAsync(["screenshot-to-file", frame, "video"]);
                Check("the embedded mpv viewport can export its actual decoded video pixels", screenshot is { } shot && shot.GetProperty("error").GetString() == "success" && File.Exists(frame));
                await host.SeekAsync(0.5); await Task.Delay(180);
                var seekPosition = await host.MpvCommandAsync(["get_property", "time-pos"]);
                var stillPaused = await host.MpvCommandAsync(["get_property", "pause"]);
                var scrubFrame = Path.Combine(artifacts, "native-media-scrubbed.png");
                await host.MpvCommandAsync(["screenshot-to-file", scrubFrame, "video"]);
                Check("scrubbing a paused timeline shows the actual sought video frame", seekPosition is { } seeked && Math.Abs(seeked.GetProperty("data").GetDouble() - 0.5) < 0.075
                    && stillPaused is { } stopped && stopped.GetProperty("data").ValueKind == System.Text.Json.JsonValueKind.True
                    && File.Exists(scrubFrame) && !File.ReadAllBytes(frame).SequenceEqual(File.ReadAllBytes(scrubFrame)));
                await host.StopPlaybackAsync(); await Task.Delay(100);
                var resetPosition = await host.MpvCommandAsync(["get_property", "time-pos"]);
                var resetPause = await host.MpvCommandAsync(["get_property", "pause"]);
                Check("Stop resets the file to its first frame and keeps playback paused", resetPosition is { } reset && reset.GetProperty("data").GetDouble() < 0.05
                    && resetPause is { } resetState && resetState.GetProperty("data").ValueKind == System.Text.Json.JsonValueKind.True);
                var polls = host.PlaybackPollCount; await Task.Delay(550);
                Check("playback status polling runs at a bounded four updates per second", host.IsPlaybackTimerRunning && host.PlaybackPollCount > polls && host.PlaybackPollCount - polls <= 3);
                await host.LoadAsync(wave);
                var duration = await host.MpvCommandAsync(["get_property", "duration"]);
                var wavePause = await host.MpvCommandAsync(["get_property", "pause"]);
                Check("actual WAV audio loads in the mini player and stays paused", duration is { } audio && audio.TryGetProperty("data", out var seconds) && seconds.GetDouble() > 1.5
                    && wavePause is { } waveReply && waveReply.GetProperty("data").ValueKind == System.Text.Json.JsonValueKind.True);
                var mediaPid = host.OwnedProcessId;
                window.Close();
                Check("closing disposes the native host and terminates its owned media process", mediaPid is not null && await PreviewToolsWaitForExit(mediaPid.Value) && host.OwnedProcessId is null);
                var closedPolls = host.PlaybackPollCount; await Task.Delay(300);
                Check("closing stops playback polling and prevents stale UI updates", !host.IsPlaybackTimerRunning && host.PlaybackPollCount == closedPolls);
            }
            finally { host.Dispose(); window.Close(); }
            Check("native preview never edits the original model or video bytes", (await File.ReadAllBytesAsync(model)).SequenceEqual(modelBefore) && (await File.ReadAllBytesAsync(video)).SequenceEqual(videoBefore));
        }
        finally { TryDelete(directory); }
    }

    private static bool PreviewToolsProcessExists(int id)
    {
        try { using var process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static IEnumerable<DependencyObject> PreviewNativeDescendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in PreviewNativeDescendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static async Task<bool> PreviewToolsWaitForExit(int id)
    {
        var clock = Stopwatch.StartNew();
        while (PreviewToolsProcessExists(id) && clock.ElapsedMilliseconds < 3000) await Task.Delay(30);
        return !PreviewToolsProcessExists(id);
    }

    private static bool PreviewToolsVariedPixels(BitmapSource bitmap)
    {
        var stride = (bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;
        var bytes = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(bytes, stride, 0);
        return bytes.Distinct().Take(10).Count() >= 10;
    }

    private static void PreviewToolsSavePicture(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    // A tiny uncompressed RGB AVI, generated without external codecs or user media.
    private static void PreviewToolsWriteVideo(string path, int width = 96, int height = 64)
    {
        const int frames = 30;
        using var stream = File.Create(path); using var writer = new BinaryWriter(stream, Encoding.ASCII);
        PreviewToolsChunk(writer, "RIFF", riff =>
        {
            riff.Write(Encoding.ASCII.GetBytes("AVI "));
            PreviewToolsChunk(riff, "LIST", hdrl =>
            {
                hdrl.Write(Encoding.ASCII.GetBytes("hdrl"));
                PreviewToolsChunk(hdrl, "avih", h =>
                {
                    foreach (var value in new uint[] { 33333, (uint)(width * height * 3 * 30), 0, 0, frames, 0, 1, (uint)(width * height * 3), (uint)width, (uint)height, 0, 0, 0, 0 }) h.Write(value);
                });
                PreviewToolsChunk(hdrl, "LIST", strl =>
                {
                    strl.Write(Encoding.ASCII.GetBytes("strl"));
                    PreviewToolsChunk(strl, "strh", h =>
                    {
                        h.Write(Encoding.ASCII.GetBytes("vidsDIB ")); h.Write(0u); h.Write((ushort)0); h.Write((ushort)0);
                        foreach (var value in new uint[] { 0, 1, 30, 0, frames, (uint)(width * height * 3), uint.MaxValue, 0 }) h.Write(value);
                        h.Write((short)0); h.Write((short)0); h.Write((short)width); h.Write((short)height);
                    });
                    PreviewToolsChunk(strl, "strf", h =>
                    {
                        h.Write(40u); h.Write(width); h.Write(height); h.Write((ushort)1); h.Write((ushort)24);
                        foreach (var value in new uint[] { 0, (uint)(width * height * 3), 0, 0, 0, 0 }) h.Write(value);
                    });
                });
            });
            PreviewToolsChunk(riff, "LIST", movi =>
            {
                movi.Write(Encoding.ASCII.GetBytes("movi"));
                for (var frame = 0; frame < frames; frame++)
                    PreviewToolsChunk(movi, "00db", pixels =>
                    {
                        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
                        { pixels.Write((byte)(x * 255 / width)); pixels.Write((byte)(y * 255 / height)); pixels.Write((byte)(40 + frame * 5)); }
                    });
            });
        });
    }

    private static void PreviewToolsWriteWave(string path)
    {
        const int bytes = 8000 * 2 * 2; // two seconds of silent 16-bit mono PCM.
        using var stream = File.Create(path); using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + bytes); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(8000); writer.Write(16000);
        writer.Write((ushort)2); writer.Write((ushort)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(bytes); writer.Write(new byte[bytes]);
    }

    private static void PreviewToolsChunk(BinaryWriter writer, string type, Action<BinaryWriter> contents)
    {
        writer.Write(Encoding.ASCII.GetBytes(type)); var sizePosition = writer.BaseStream.Position; writer.Write(0u);
        contents(writer); var end = writer.BaseStream.Position; var size = checked((uint)(end - sizePosition - 4));
        writer.BaseStream.Position = sizePosition; writer.Write(size); writer.BaseStream.Position = end;
        if ((size & 1) != 0) writer.Write((byte)0);
    }

    [StructLayout(LayoutKind.Sequential)] private struct PreviewToolsRect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetParent")] private static extern IntPtr PreviewToolsGetParent(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetClientRect")] private static extern bool PreviewToolsGetClientRect(IntPtr window, out PreviewToolsRect rect);
}
