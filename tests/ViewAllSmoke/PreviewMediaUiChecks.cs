using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltraExplorer.Controls;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task PreviewMediaUiChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(PreviewMediaUiChecks), StringComparison.OrdinalIgnoreCase))
        { RunGroupInOwnProcess(nameof(PreviewMediaUiChecks)); return Task.CompletedTask; }
        RunOnSta("compact media preview interface", PreviewMediaUiOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task PreviewMediaUiOnStaAsync()
    {
        Section("media preview: actual track classification, compact controls, focus and owned IPC");
        using var coverTracks = JsonDocument.Parse("""{"data":[{"type":"audio"},{"type":"video","albumart":true}]}""");
        using var realVideoTracks = JsonDocument.Parse("""{"data":[{"type":"audio"},{"type":"video","albumart":false}]}""");
        using var subtitleTracks = JsonDocument.Parse("""{"data":[{"type":"sub"}]}""");
        Check("audio cover art is not mistaken for a moving video viewport", NativePreviewHost.HasOnlyAudio(coverTracks.RootElement, "cover.mp4"));
        Check("actual video tracks override an audio file extension", !NativePreviewHost.HasOnlyAudio(realVideoTracks.RootElement, "renamed.wav"));
        Check("a recognized track list with no audio never claims an audio preview", !NativePreviewHost.HasOnlyAudio(subtitleTracks.RootElement, "empty.wav"));
        Check("the filename fallback handles literal uppercase audio and excludes movies", NativePreviewHost.HasOnlyAudio(null, "sound.WAV. ") && !NativePreviewHost.HasOnlyAudio(null, "movie.mp4"));
        var readinessPath = Path.Combine(Path.GetTempPath(), "readiness-fixture.wav");
        static JsonElement Success(object data) => JsonSerializer.SerializeToElement(new { data, error = "success" });
        var selectedVideo = Success(new[] { new { type = "video", selected = true, albumart = false } });
        var unselectedVideo = Success(new[] { new { type = "video", selected = false, albumart = false } });
        var correctPath = Success(DocumentPreviewService.LiteralPath(readinessPath));
        var decodedVideo = Success(new { w = 160, h = 96 });
        var startPosition = Success(0);
        Check("an empty or unselected track list never passes media readiness", !NativePreviewHost.MediaPropertiesReady(Success(Array.Empty<object>()), correctPath, decodedVideo, null, startPosition, readinessPath)
            && !NativePreviewHost.MediaPropertiesReady(unselectedVideo, correctPath, decodedVideo, null, startPosition, readinessPath));
        Check("file headers without decoded parameters or a real position never pass readiness", !NativePreviewHost.MediaPropertiesReady(selectedVideo, correctPath, null, null, startPosition, readinessPath)
            && !NativePreviewHost.MediaPropertiesReady(selectedVideo, correctPath, decodedVideo, null, null, readinessPath));
        Check("decoded media for a different path cannot satisfy the current file readiness", !NativePreviewHost.MediaPropertiesReady(selectedVideo, Success(Path.Combine(Path.GetTempPath(), "other.wav")), decodedVideo, null, startPosition, readinessPath));
        Check("actual decoded selected video parameters pass readiness even with an audio extension", NativePreviewHost.MediaPropertiesReady(selectedVideo, correctPath, decodedVideo, null, startPosition, readinessPath));
        using (var lineStream = new MemoryStream(Encoding.UTF8.GetBytes("first\nsecond\r\n" + new string('x', 256 * 1024 + 1) + "\n")))
        using (var textReader = new StreamReader(lineStream, Encoding.UTF8))
        {
            var boundedLines = new NativePreviewHost.BoundedMediaLineReader(textReader);
            var firstLine = await boundedLines.ReadAsync(CancellationToken.None);
            var secondLine = await boundedLines.ReadAsync(CancellationToken.None);
            Check("the persistent IPC reader preserves buffered lines and CRLF boundaries", firstLine == "first" && secondLine == "second");
            var rejectedOversize = false;
            try { await boundedLines.ReadAsync(CancellationToken.None); }
            catch (IOException) { rejectedOversize = true; }
            Check("an oversized native JSON line is rejected before parsing or log allocation", rejectedOversize);
        }
        var mpv = PreviewTools.FindMpv();
        Check("the media UI fixtures use the actual bundled mpv engine", mpv is not null);
        if (mpv is null) return;
        if (Application.Current is null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/UltraExplorer;component/Themes/UltraTheme.xaml", UriKind.Relative) });
        }
        var directory = Path.Combine(Path.GetTempPath(), "UltraExplorerMediaUiChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var artifacts = Path.Combine(Environment.CurrentDirectory, "artifacts", "preview-usability", "shots");
        Directory.CreateDirectory(artifacts);
        try
        {
            var audio = Path.Combine(directory, "Recorded voice note.wav"); PreviewToolsWriteWave(audio);
            // Intentionally misleading suffix proves that the visible layout
            // follows decoded tracks rather than the extension alone.
            var video = Path.Combine(directory, "Moving colour sample.wav"); PreviewToolsWriteVideo(video, 160, 96);
            var audioBefore = await File.ReadAllBytesAsync(audio);
            var videoBefore = await File.ReadAllBytesAsync(video);
            var host = new NativePreviewHost();
            var window = new Window { Content = host, Width = 500, Height = 430, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000 };
            window.SourceInitialized += (_, _) => DialogNative.CloakOwn(new WindowInteropHelper(window).Handle, true);
            try
            {
                window.Show(); await MediaUiLoadAsync(host, audio, "audio", artifacts); host.UpdateLayout();
                var fixtureOutput = await host.MpvCommandAsync(["get_property", "current-ao"]);
                Check("isolated generated media fixtures use a null output without a physical audio device",
                    fixtureOutput is { } outputReply && outputReply.TryGetProperty("data", out var audioOutput)
                    && audioOutput.ValueKind == JsonValueKind.String && audioOutput.GetString() == "null");
                var audioPid = host.OwnedProcessId;
                Check("decoded WAV shows a named audio card and hides its unused native canvas", host.IsAudioOnly && !host.IsNativeViewportVisible
                    && host.AudioDisplayName == "Recorded voice note.wav" && host.AudioDetail.Contains("WAV audio", StringComparison.Ordinal));
                Check("audio has no video-frame help header", !host.IsMediaHeaderVisible);
                Check("audio starts paused with an accessible glyph Play action", AutomationProperties.GetName(host.PlayPauseControl) == "Play"
                    && host.PlayPauseControl.Content is TextBlock { Text.Length: 1 });
                Check("successful media readiness keeps its actual decoded track snapshot and owned event observer", host.IsMediaObserverRunning
                    && host.InitialMediaTracks is { } audioTracks && audioTracks.TryGetProperty("data", out var actualTracks) && actualTracks.GetArrayLength() > 0);
                var sliderTrack = host.TimelineControl.Template.FindName("PART_Track", host.TimelineControl) as Track;
                var volumeTrack = host.VolumeControl.Template.FindName("PART_Track", host.VolumeControl) as Track;
                Check("both media sliders use custom tracks and round thumb templates", sliderTrack?.Thumb.Template is not null && volumeTrack?.Thumb.Template is not null
                    && host.TimelineControl.IsMoveToPointEnabled && host.VolumeControl.IsMoveToPointEnabled);
                Check("the two-row player fits the small preview without overlap or clipped controls", MediaUiControlsFit(host));
                Check("the controls stay compact instead of occupying the audio canvas", host.PlaybackControls.ActualHeight <= 82 && host.PlaybackControls.ActualWidth <= host.ActualWidth);
                Check("every media action has an accessible name and the speed list stays at six choices", PreviewNativeDescendants(host.PlaybackControls).OfType<Button>()
                    .All(button => !string.IsNullOrEmpty(AutomationProperties.GetName(button))) && host.SpeedControl.Items.Count == 6
                    && AutomationProperties.GetName(host.TimelineControl) == "Playback position" && AutomationProperties.GetName(host.VolumeControl) == "Volume");
                var speedText = PreviewNativeDescendants(host.SpeedControl).OfType<TextBlock>().FirstOrDefault(text => text.Text == "1×");
                Check("the visible playback speed has readable contrast on the actual dark canvas", speedText?.Foreground is SolidColorBrush textBrush
                    && host.Background is SolidColorBrush canvasBrush && MediaUiContrast(textBrush.Color, canvasBrush.Color) >= 4.5);
                Check("focused playback controls are identified without claiming the canvas or unrelated controls", host.OwnsPlaybackControl(host.TimelineControl)
                    && host.OwnsPlaybackControl(host.VolumeControl) && host.OwnsPlaybackControl(host.SpeedControl)
                    && host.OwnsPlaybackControl(host.PlayPauseControl.Content as DependencyObject)
                    && !host.OwnsPlaybackControl(host) && !host.OwnsPlaybackControl(new Slider()));
                var ownedSpeedItem = (ComboBoxItem)host.SpeedControl.Items[2];
                var unrelatedSpeed = new ComboBox(); var unrelatedItem = new ComboBoxItem { Content = "1×" }; unrelatedSpeed.Items.Add(unrelatedItem);
                Check("speed popup item ownership is recognized without swallowing a foreign dropdown", host.OwnsPlaybackControl(ownedSpeedItem)
                    && !host.OwnsPlaybackControl(unrelatedItem));
                MediaUiSave(host, Path.Combine(artifacts, "media-audio-compact.png"));
                // A 340 DIP Quick Look leaves roughly 230 DIP for the native
                // media host once its caption, top actions and status are laid out.
                // Set an exact host height to include its own playback controls.
                host.Height = 230; host.UpdateLayout(); window.UpdateLayout();
                var minimumCardFits = MediaUiAudioCardFits(host);
                var minimumControlsFit = MediaUiControlsFit(host);
                var minimumNameMatches = host.AudioDisplayName == "Recorded voice note.wav";
                var minimumDurationMatches = host.AudioDetail.Contains("0:02", StringComparison.Ordinal);
                if (!minimumCardFits || !minimumControlsFit || !minimumNameMatches || !minimumDurationMatches)
                {
                    Console.WriteLine($"Media minimum failure: card={minimumCardFits}; controls={minimumControlsFit}; name={minimumNameMatches}; durationLabel={minimumDurationMatches}; nameText={host.AudioDisplayName}; detailText={host.AudioDetail}");
                    Console.WriteLine("Media minimum layout: " + MediaUiLayoutDiagnostic(host));
                    await MediaUiEngineDiagnostic(host, "minimum");
                }
                Check("the audio card and all controls fit a minimum-height preview without clipping", minimumCardFits && minimumControlsFit && minimumNameMatches && minimumDurationMatches);
                MediaUiSave(host, Path.Combine(artifacts, "media-audio-minimum-height.png"));
                host.Height = double.NaN; window.UpdateLayout();
                await host.SetVolumeAsync(0); await host.RefreshPlaybackAsync();
                Check("zero volume reaches the owned engine without confusing mute state", MediaUiNumber(await host.MpvCommandAsync(["get_property", "volume"])) == 0);
                host.MuteControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await MediaUiWaitAsync(() => AutomationProperties.GetName(host.MuteControl) == "Unmute");
                Check("mute changes the icon name from the engine state", AutomationProperties.GetName(host.MuteControl) == "Unmute"
                    && MediaUiBool(await host.MpvCommandAsync(["get_property", "mute"])));
                await host.MpvCommandAsync(["set_property", "mute", false]);
                await host.SetSpeedAsync(1.5);
                await MediaUiWaitAsync(() => host.SpeedControl.SelectedItem is ComboBoxItem { Tag: 1.5 });
                Check("playback speed remains bound to mpv and visible selection", MediaUiNumber(await host.MpvCommandAsync(["get_property", "speed"])) == 1.5
                    && host.SpeedControl.SelectedItem is ComboBoxItem { Tag: 1.5 });
                await host.TogglePlaybackAsync();
                await MediaUiWaitAsync(() => AutomationProperties.GetName(host.PlayPauseControl) == "Pause");
                Check("Play updates to an accessible Pause action", AutomationProperties.GetName(host.PlayPauseControl) == "Pause");
                await host.StopPlaybackAsync();
                await MediaUiWaitAsync(() => AutomationProperties.GetName(host.PlayPauseControl) == "Play");
                var stopPause = await host.MpvCommandAsync(["get_property", "pause"]);
                var stopPosition = await host.MpvCommandAsync(["get_property", "time-pos"]);
                var stopUiName = AutomationProperties.GetName(host.PlayPauseControl);
                if (!MediaUiBool(stopPause) || !(MediaUiNumber(stopPosition) < 0.05) || stopUiName != "Play")
                {
                    Console.WriteLine($"Media Stop failure: pause={MediaUiJson(stopPause)}; time-pos={MediaUiJson(stopPosition)}; action={stopUiName}");
                    await MediaUiEngineDiagnostic(host, "stop");
                }
                Check("Stop pauses and rewinds audio through its own IPC", MediaUiBool(stopPause) && MediaUiNumber(stopPosition) < 0.05 && stopUiName == "Play");
                await MediaUiLoadAsync(host, video, "video", artifacts); host.UpdateLayout();
                var audioExited = audioPid is not null && await PreviewToolsWaitForExit(audioPid.Value);
                if (host.IsAudioOnly || !host.IsNativeViewportVisible || host.IsMediaHeaderVisible || !audioExited)
                {
                    Console.WriteLine($"Media video switch failure: audioOnly={host.IsAudioOnly}; nativeVisible={host.IsNativeViewportVisible}; headerVisible={host.IsMediaHeaderVisible}; cardVisible={host.AudioCard.Visibility}; oldPid={audioPid}; oldPidExited={audioExited}; newPid={host.OwnedProcessId}; error={host.LastError}");
                    Console.WriteLine("Media video switch initial tracks: " + MediaUiJson(host.InitialMediaTracks));
                    await MediaUiEngineDiagnostic(host, "video-switch");
                }
                Check("a real video with an audio suffix restores the native viewport and removes the card", !host.IsAudioOnly && host.IsNativeViewportVisible
                    && !host.IsMediaHeaderVisible && audioExited);
                window.Width = 900; window.Height = 550; window.UpdateLayout();
                Check("resizing preserves compact controls and lets the video canvas dominate", MediaUiControlsFit(host)
                    && host.PlaybackControls.ActualHeight <= 82 && PreviewNativeDescendants(host).OfType<HwndHost>().Single().ActualHeight > host.ActualHeight * 0.7);
                var firstFrame = Path.Combine(artifacts, "media-video-first-frame.png");
                await host.MpvCommandAsync(["screenshot-to-file", firstFrame, "video"]);
                await host.SeekAsync(0.5); await Task.Delay(180);
                var secondFrame = Path.Combine(artifacts, "media-video-scrubbed-frame.png");
                await host.MpvCommandAsync(["screenshot-to-file", secondFrame, "video"]);
                Check("the styled timeline still seeks an actual changed frame while paused", File.Exists(firstFrame) && File.Exists(secondFrame)
                    && !File.ReadAllBytes(firstFrame).SequenceEqual(File.ReadAllBytes(secondFrame)) && MediaUiBool(await host.MpvCommandAsync(["get_property", "pause"])));
                MediaUiSave(host, Path.Combine(artifacts, "media-video-controls.png"));
                if (File.Exists(secondFrame)) MediaUiSaveVideoComposite(host, secondFrame, Path.Combine(artifacts, "media-video-interface.png"));
                var polls = host.PlaybackPollCount; await Task.Delay(560);
                Check("the restyled interface retains bounded four-per-second playback polling", host.IsPlaybackTimerRunning && host.PlaybackPollCount - polls is > 0 and <= 3);
                var invalid = Path.Combine(directory, "invalid.wav");
                await File.WriteAllTextAsync(invalid, "This is not an audio or video stream.");
                var failureClock = Stopwatch.StartNew();
                IOException? invalidFailure = null;
                try { await host.LoadAsync(invalid); }
                catch (IOException error) { invalidFailure = error; }
                if (invalidFailure is not null)
                    await File.WriteAllTextAsync(Path.Combine(artifacts, "media-invalid-engine.log"), host.LastNativeDiagnostic ?? "<missing native diagnostic>");
                Check("a real undecodable file is rejected by native events without claiming a ready player", invalidFailure is not null && !host.IsMediaLoaded
                    && failureClock.Elapsed < TimeSpan.FromSeconds(20) && !string.IsNullOrEmpty(host.LastNativeDiagnostic));
                var failedPid = host.LastStartedProcessId;
                var failedExited = failedPid is not null && await PreviewToolsWaitForExit(failedPid.Value);
                await MediaUiWaitAsync(() => !host.IsMediaObserverRunning);
                Check("a native load failure releases its own process and persistent event observer", failedExited && host.OwnedProcessId is null
                    && !host.IsMediaObserverRunning && !host.IsPlaybackTimerRunning);
                await MediaUiLoadAsync(host, video, "recovery", artifacts); host.UpdateLayout();
                var videoPid = host.OwnedProcessId; window.Close();
                // A bare test Window unloads its content on the next dispatcher
                // pass. Wait for that real lifecycle notification before checking
                // disposal; never call Dispose to make this assertion succeed.
                await MediaUiWaitAsync(() => !host.IsPlaybackTimerRunning);
                var ownedExited = videoPid is not null && await PreviewToolsWaitForExit(videoPid.Value);
                await MediaUiWaitAsync(() => !host.IsMediaObserverRunning);
                Check("closing the player stops its poller and only its owned process", !host.IsPlaybackTimerRunning && ownedExited && host.OwnedProcessId is null && !host.IsMediaObserverRunning);
            }
            finally { host.Dispose(); window.Close(); }
            var audioAfter = await File.ReadAllBytesAsync(audio);
            var videoAfter = await File.ReadAllBytesAsync(video);
            Check("media interaction leaves both original files byte-identical", audioBefore.AsSpan().SequenceEqual(audioAfter)
                && videoBefore.AsSpan().SequenceEqual(videoAfter));
        }
        finally { TryDelete(directory); }
    }

    private static bool MediaUiControlsFit(NativePreviewHost host)
    {
        host.UpdateLayout();
        var controls = new FrameworkElement[] { host.PlayPauseControl, host.SpeedControl, host.MuteControl, host.VolumeControl };
        var rects = controls.Select(control => control.TransformToAncestor(host.PlaybackControls).TransformBounds(new Rect(control.RenderSize))).ToArray();
        return rects.All(rect => rect.Left >= -0.5 && rect.Top >= -0.5 && rect.Right <= host.PlaybackControls.ActualWidth + 0.5 && rect.Bottom <= host.PlaybackControls.ActualHeight + 0.5)
            && rects.Zip(rects.Skip(1), (left, right) => left.Right <= right.Left + 0.5).All(value => value)
            && host.TimelineControl.ActualWidth > 180;
    }

    private static bool MediaUiAudioCardFits(NativePreviewHost host)
    {
        host.UpdateLayout();
        var viewport = PreviewNativeDescendants(host).OfType<HwndHost>().Single();
        var area = viewport.TransformToAncestor(host).TransformBounds(new Rect(viewport.RenderSize));
        var card = host.AudioCard.TransformToAncestor(host).TransformBounds(new Rect(host.AudioCard.RenderSize));
        return card.Left >= area.Left - 0.5 && card.Right <= area.Right + 0.5 && card.Top >= area.Top - 0.5 && card.Bottom <= area.Bottom + 0.5
            && PreviewNativeDescendants(host.AudioCard).OfType<TextBlock>().Where(text => !string.IsNullOrEmpty(text.Text))
                .All(text =>
                {
                    var bounds = text.TransformToAncestor(host.AudioCard).TransformBounds(new Rect(text.RenderSize));
                    return bounds.Left >= -0.5 && bounds.Top >= -0.5 && bounds.Right <= card.Width + 0.5 && bounds.Bottom <= card.Height + 0.5;
                });
    }

    private static async Task MediaUiWaitAsync(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(30);
    }

    private static async Task MediaUiLoadAsync(NativePreviewHost host, string path, string stage, string artifacts)
    {
        try { await host.LoadAsync(path); }
        catch
        {
            var native = host.LastNativeDiagnostic ?? "<missing native diagnostic>";
            Console.WriteLine($"Media {stage} load failure native diagnostic: {native}");
            await File.WriteAllTextAsync(Path.Combine(artifacts, $"media-{stage}-load-error.log"), native);
            throw;
        }
    }

    private static string MediaUiLayoutDiagnostic(NativePreviewHost host)
    {
        object Bounds(FrameworkElement element) => new { width = element.ActualWidth, height = element.ActualHeight,
            rect = element.TransformToAncestor(host).TransformBounds(new Rect(element.RenderSize)).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var viewport = PreviewNativeDescendants(host).OfType<HwndHost>().Single();
        var scale = VisualTreeHelper.GetDpi(host);
        return JsonSerializer.Serialize(new { host = new { width = host.ActualWidth, height = host.ActualHeight }, dpiX = scale.DpiScaleX, dpiY = scale.DpiScaleY,
            body = Bounds(viewport), card = Bounds(host.AudioCard), cardPadding = ((Border)host.AudioCard).Padding.ToString(), cardMargin = host.AudioCard.Margin.ToString(),
            controls = Bounds(host.PlaybackControls), timeline = Bounds(host.TimelineControl), volume = Bounds(host.VolumeControl),
            text = PreviewNativeDescendants(host.AudioCard).OfType<TextBlock>().Select(block => new { text = block.Text, bounds = Bounds(block) }).ToArray() });
    }

    private static async Task MediaUiEngineDiagnostic(NativePreviewHost host, string stage)
    {
        foreach (var property in new[] { "duration", "time-pos", "pause", "seeking", "eof-reached", "idle-active", "core-idle", "track-list", "audio-params", "video-params" })
            Console.WriteLine($"Media {stage} {property}: {MediaUiJson(await host.MpvCommandAsync(["get_property", property]))}");
    }

    private static string MediaUiJson(JsonElement? response) => response?.GetRawText() ?? "<missing reply>";

    private static double MediaUiNumber(JsonElement? response) => response is { } root && root.TryGetProperty("data", out var data)
        && data.ValueKind == JsonValueKind.Number ? data.GetDouble() : double.NaN;
    private static bool MediaUiBool(JsonElement? response) => response is { } root && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.True;

    private static void MediaUiSave(FrameworkElement host, string path)
    {
        // Rendering a centered element directly keeps its offset in the parent
        // and clips the bottom. A visual brush renders its own complete bounds.
        var visual = new DrawingVisual();
        using (var canvas = visual.RenderOpen())
            canvas.DrawRectangle(new VisualBrush(host) { Stretch = Stretch.Fill }, null, new Rect(0, 0, host.ActualWidth, host.ActualHeight));
        var image = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); PreviewToolsSavePicture(image, path);
    }

    private static double MediaUiContrast(Color foreground, Color background)
    {
        static double Component(byte value)
        {
            var normalized = value / 255.0;
            return normalized <= 0.04045 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color color) => 0.2126 * Component(color.R) + 0.7152 * Component(color.G) + 0.0722 * Component(color.B);
        var first = Luminance(foreground); var second = Luminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private static void MediaUiSaveVideoComposite(NativePreviewHost host, string framePath, string path)
    {
        // RenderTargetBitmap cannot capture an HWND. Compose the actual decoded
        // mpv screenshot over its corresponding WPF viewport for visual review.
        var baseImage = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        baseImage.Render(host);
        var frame = new BitmapImage(); frame.BeginInit(); frame.CacheOption = BitmapCacheOption.OnLoad; frame.UriSource = new Uri(framePath); frame.EndInit(); frame.Freeze();
        var surface = PreviewNativeDescendants(host).OfType<HwndHost>().Single();
        var viewport = surface.TransformToAncestor(host).TransformBounds(new Rect(surface.RenderSize));
        var scale = Math.Min(viewport.Width / frame.PixelWidth, viewport.Height / frame.PixelHeight);
        var decoded = new Rect(viewport.Left + (viewport.Width - frame.PixelWidth * scale) / 2, viewport.Top + (viewport.Height - frame.PixelHeight * scale) / 2,
            frame.PixelWidth * scale, frame.PixelHeight * scale);
        var visual = new DrawingVisual();
        using (var canvas = visual.RenderOpen())
        {
            canvas.DrawImage(baseImage, new Rect(0, 0, host.ActualWidth, host.ActualHeight));
            canvas.DrawRectangle(Brushes.Black, null, viewport); canvas.DrawImage(frame, decoded);
        }
        var image = new RenderTargetBitmap(baseImage.PixelWidth, baseImage.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); PreviewToolsSavePicture(image, path);
    }
}
