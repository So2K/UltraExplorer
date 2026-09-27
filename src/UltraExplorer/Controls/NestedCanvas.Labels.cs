using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

// The names: folder titles, pills and notes, and file names with their
// icons and sizes - decided here in DIPs and handed to a LabelTarget,
// which is WPF's text on the CPU path and glyph and icon instances on the
// GPU - together with what is kept between frames so that a frame draws
// its names rather than makes them: text layouts and WPF's recordings of
// them, the words for sizes and counts, each folder's and file's mark, and
// the frame's allowance for making any of that anew.
public sealed partial class NestedCanvas
{
    // ---- the names on the GPU --------------------------------------------------

    /// <summary>
    /// The label layer for a frame whose cells are on the GPU: the same
    /// <see cref="DrawLabelLayer"/> the WPF layer is recorded from, into
    /// <see cref="GpuLabelTarget"/>, so the names become glyph, icon and
    /// rectangle instances presented with the cells.  A frame that only
    /// redraws the names (an icon or a glyph arrived) presents again with the
    /// scene's instances as they were uploaded.
    ///
    /// False - the names go to WPF - when the cells are not on the GPU, or
    /// the atlases the names are drawn from could not be made.
    /// </summary>
    private bool TryDrawLabelsOnGpu(bool inMotion)
    {
        if (_surface is not { IsLost: false } surface
            || _surfaceLost
            || _renderer is null
            || _gpuFrame is null
            || GpuLabelAtlases.Current is not { } atlases)
        {
            return false;
        }

        if (surface.Devices.IsDisposed)
        {
            // Handed back as lost by another canvas on the same card: nothing
            // of it may be touched.  The next frame gives the surface up.
            _surfaceLost = true;
            RequestFrame(Layers.All);
            return false;
        }

        var target = EnsureGpuLabels(atlases);
        try
        {
            target.Begin(_gpuFrame, surface.Devices, ScenePixelWidth, ScenePixelHeight, _scaleX, _scaleY, snap: !inMotion);
            DrawLabelLayer(target);

            // The glyphs placed since the last frame go to the card here - and
            // when the atlas has outgrown its texture, a larger one is made -
            // so the card can fail here as much as in Begin.
            target.End();
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or ObjectDisposedException or GpuUnavailableException)
        {
            // The card failed making or filling the atlases' textures: as good
            // as lost.  This frame's names go to WPF over whatever the GPU
            // shows; the next frame gives the surface up.
            _surfaceLost = true;
            RequestFrame(Layers.All);
            return false;
        }

        LastUploadMilliseconds += target.LastUploadMilliseconds;
        if (target.WantsAnotherFrame)
        {
            RequestFrame(Layers.Labels);
        }

        _labelsOnGpu = true;
        _presentPending = true;
        if (_wpfLabels.ShowsRuns)
        {
            // WPF's kept runs come off once the GPU's picture with the names
            // is on screen: the present clears the layer's own recording, and
            // the next frame the runs (TakeLabelMaterial) - so the one frame
            // between shows the names twice, in the same places, rather than
            // a frame with none.
            _labelVisualRecorded = true;
            RequestFrame(Layers.Labels);
        }

        return true;
    }


    /// <summary>
    /// The label layer recorded by WPF: on the CPU path, and on the GPU's
    /// cells when the names cannot go to the GPU.  Names left in the GPU's
    /// frame from before are taken out.
    ///
    /// <para>In the frame loop the names are drawn from what is kept between
    /// frames (<see cref="WpfLabelTarget"/>), within the frame's allowance for
    /// making text (<see cref="BeginTextAllowance"/>).  Anywhere else - a
    /// snapshot, a test, a canvas in no window - every name is set and
    /// recorded exactly as the canvas has always recorded it, so a picture
    /// taken of the canvas is the one it always was.  Whether the layer is
    /// drawn for motion is what the frame set the text's hinting for
    /// (<see cref="_textAnimated"/>), which is the frame's motion.</para>
    /// </summary>
    private void DrawLabelsWithWpf()
    {
        if (_labelsOnGpu)
        {
            _labelsOnGpu = false;
            if (_gpuFrame is not null)
            {
                _gpuFrame.ClearLabels();
                _presentPending |= _surface is not null;
            }
        }

        // This layer draws every name the worker has handed back so far: a
        // redraw held back for them is owed no longer.
        _namesRedrawOwed = false;
        if (_inFrameLoop)
        {
            _lastNamesDrawnMilliseconds = _clock.Now.TotalMilliseconds;
        }

        var inMotion = _textAnimated;
        BeginTextAllowance(inMotion);
        try
        {
            using var dc = _labelVisual.RenderOpen();
            _wpfLabels.Begin(dc, !_inFrameLoop ? WpfLabelMode.Exact : inMotion ? WpfLabelMode.Moving : WpfLabelMode.Kept);
            try
            {
                DrawLabelLayer(_wpfLabels);
            }
            finally
            {
                _wpfLabels.End();
            }
        }
        finally
        {
            EndTextAllowance(inMotion);
        }

        _labelVisualRecorded = true;
    }

    /// <summary>
    /// The canvas's GPU label target and its text shaper, made the first time
    /// either is needed, and listening to the atlases for what arrives.
    /// </summary>
    private GpuLabelTarget EnsureGpuLabels(LabelAtlases atlases)
    {
        if (_gpuLabels is null || !ReferenceEquals(_labelAtlases, atlases))
        {
            UnhookLabelAtlases();
            _labelAtlases = atlases;
            _gpuLabels = new GpuLabelTarget(atlases.Faces, new TextShaper(atlases.Faces), atlases.Glyphs, atlases.Icons);
        }

        if (!_labelEventsHooked)
        {
            _labelEventsHooked = true;
            _gpuLabels.Shaper.Arrived += OnLabelMaterialArrived;
            atlases.Glyphs.GlyphsArrived += OnLabelMaterialArrived;
            atlases.Icons.ArrivalsPending += OnLabelMaterialArrived;
        }

        return _gpuLabels;
    }

    private void UnhookLabelAtlases()
    {
        if (!_labelEventsHooked || _labelAtlases is null || _gpuLabels is null)
        {
            return;
        }

        _labelEventsHooked = false;
        _gpuLabels.Shaper.Arrived -= OnLabelMaterialArrived;
        _labelAtlases.Glyphs.GlyphsArrived -= OnLabelMaterialArrived;
        _labelAtlases.Icons.ArrivalsPending -= OnLabelMaterialArrived;
    }

    /// <summary>
    /// A name was shaped, a glyph made or an icon found, on one of the
    /// atlases' threads: noted, and the frame loop woken, which asks the
    /// dispatcher once however many arrive before the next frame takes them
    /// in (<see cref="TakeLabelMaterial"/>).
    /// </summary>
    private void OnLabelMaterialArrived()
    {
        Volatile.Write(ref _labelMaterialArrived, 1);
        Wake();
    }

    /// <summary>
    /// Part of a frame's fourth phase: whatever arrived for the names since
    /// the last frame is one redraw of them - glyphs, shaped names and icons
    /// when they are on the GPU, names the text worker made when WPF draws
    /// them (<see cref="TakePreparedTexts"/>), those no oftener than
    /// <see cref="RedrawNamesWhenAllowed"/> lets them be.
    /// </summary>
    private void TakeLabelMaterial()
    {
        if (Interlocked.Exchange(ref _labelMaterialArrived, 0) != 0 && _labelsOnGpu)
        {
            _dirty |= Layers.Labels;
        }

        if (_labelsOnGpu && !_labelVisualRecorded && _wpfLabels.ShowsRuns)
        {
            _wpfLabels.Hide();
        }

        if (TakePreparedTexts() && !_labelsOnGpu)
        {
            _namesRedrawOwed = true;
        }

        if (_namesRedrawOwed)
        {
            RedrawNamesWhenAllowed();
        }
    }

    // ---- redrawing the names for what the worker made --------------------------------

    /// <summary>Names came back from the text worker that the label layer has not been drawn with yet.</summary>
    private bool _namesRedrawOwed;

    /// <summary>The frame time, in milliseconds, of the last label layer WPF drew in the loop; none before the first.</summary>
    private double _lastNamesDrawnMilliseconds = double.NegativeInfinity;

    private DispatcherTimer? _namesRedrawTimer;

    /// <summary>
    /// The names the text worker made are drawn in: with the frame's own
    /// redraw of the names when it has one anyway - the camera moved, a
    /// folder was read, an icon came - and otherwise no sooner than
    /// <see cref="FrameBudgets.IconRefreshMinMs"/> after the last label layer.
    /// A jump into a folder of 1,800 files hands the worker some 1,700 names,
    /// which come back a few dozen at a time over a quarter of a second.
    /// Drawn in as they came, that was a label layer every frame, each one
    /// recording most of the kept runs again: 5 to 10 ms and most of a
    /// megabyte a frame at 4K, for twenty frames, with no frame left for
    /// input.  At most fifteen times a second, the same names are a handful
    /// of redraws, and the frames between them are free.  A redraw held back
    /// has a timer to ask for it, in case nothing else comes first.
    /// </summary>
    private void RedrawNamesWhenAllowed()
    {
        if ((_dirty & Layers.Labels) != 0)
        {
            _namesRedrawOwed = false;
            return;
        }

        var since = _clock.Now.TotalMilliseconds - _lastNamesDrawnMilliseconds;
        if (since >= FrameBudgets.IconRefreshMinMs)
        {
            _namesRedrawOwed = false;
            _dirty |= Layers.Labels;
            return;
        }

        if (_namesRedrawTimer is null)
        {
            _namesRedrawTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher);
            _namesRedrawTimer.Tick += OnNamesRedrawDue;
        }

        if (!_namesRedrawTimer.IsEnabled)
        {
            _namesRedrawTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, FrameBudgets.IconRefreshMinMs - since));
            _namesRedrawTimer.Start();
        }
    }

    /// <summary>The time of a held redraw of the names came with no frame to draw it: one is asked for, unless a frame drew them meanwhile or the canvas cannot draw.</summary>
    private void OnNamesRedrawDue(object? sender, EventArgs e)
    {
        _namesRedrawTimer?.Stop();
        if (_namesRedrawOwed && IsVisible)
        {
            _namesRedrawOwed = false;
            RequestFrame(Layers.Labels);
        }
    }

    /// <summary>
    /// A folder was read: its files' types go to the icon atlas's workers at
    /// once, so a zoom into it finds its icons waiting, and so do the names
    /// the UI font cannot show - Arabic, Hebrew, CJK, emoji - which are shaped
    /// on the text shaper's worker and would otherwise be left out of the
    /// frame they first appear in.  Latin, Greek and Cyrillic names, nearly
    /// all of them, are shaped when first drawn, a few microseconds each:
    /// measured, that costs the UI thread less than keeping the books on tens
    /// of thousands of names ahead of time (up to 7 ms for one folder read),
    /// and the heap does not fill with names nobody zooms to.  Only while the
    /// GPU draws or may draw the canvas (<see cref="GpuMayDrawNames"/>): on
    /// the CPU none of it would ever be used.
    /// </summary>
    private void OnFolderLoadedForGpu(NestedFolder folder)
    {
        if (!GpuMayDrawNames || GpuLabelAtlases.Current is not { } atlases || PresentationSource.FromVisual(this) is null)
        {
            return;
        }

        var files = folder.Files;
        atlases.Icons.Prefetch(files);

        List<string>? folderNames = null;
        foreach (var child in folder.Children)
        {
            if (NeedsFallbackShaping(child.Name))
            {
                (folderNames ??= []).Add(child.Name);
            }
        }

        List<string>? fileNames = null;
        for (var index = 0; index < files.Count; index++)
        {
            var name = files[index].Name;
            if (NeedsFallbackShaping(name))
            {
                (fileNames ??= []).Add(name);
            }
        }

        if (folderNames is null && fileNames is null)
        {
            return;
        }

        var shaper = EnsureGpuLabels(atlases).Shaper;
        if (folderNames is not null)
        {
            // A drive's name is set in the semibold face, a folder's in the regular.
            shaper.Prefetch(folderNames, folder.IsComputer ? (byte)LabelFace.SemiBold : (byte)LabelFace.Regular);
        }

        if (fileNames is not null)
        {
            shaper.Prefetch(fileNames, (byte)LabelFace.Regular);
        }
    }

    /// <summary>
    /// Whether the GPU draws this canvas's names or may soon: false when the
    /// CPU was chosen, when the GPU was lost too often this session, and when
    /// the last frame's answer was one that does not change by waiting - WPF
    /// below render tier 2 or in software, a remote session.  A canvas that
    /// has not drawn a frame yet, or whose card is warming up or being tried
    /// again, may.
    /// </summary>
    private bool GpuMayDrawNames =>
        GpuBootstrap.Preference != RendererPreference.Cpu
        && !GpuBootstrap.IsCpuForSession
        && RendererReason != GpuBootstrap.ReasonCpuChosen
        && RendererReason != GpuBootstrap.ReasonLostTooOften
        && RendererReason != GpuBootstrap.ReasonRenderTier
        && RendererReason != GpuBootstrap.ReasonSoftwareRendering
        && RendererReason != GpuBootstrap.ReasonRemoteSession;

    /// <summary>Whether a name holds a character past Latin, Greek and Cyrillic, which the shaper's worker lays out.</summary>
    private static bool NeedsFallbackShaping(string name)
    {
        foreach (var character in name)
        {
            if (character >= FallbackShapingFrom)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Armenian onwards: the first script past the ones Segoe UI Variable holds.</summary>
    private const char FallbackShapingFrom = (char)0x0530;

    // ---- the frame's allowance for text ------------------------------------------

    /// <summary>
    /// What the label layer may spend making text this frame - laying a name
    /// out, and recording a laid-out name for WPF to replay - as Stopwatch
    /// ticks of the making itself: <see cref="FrameBudgets.MotionTextMs"/>
    /// while the camera moves, <see cref="FrameBudgets.RestTextMs"/> at rest.
    /// Past it a name is drawn at the nearest size already laid out, or left
    /// out until it is made: at rest by the text worker, while the camera
    /// moves by the next frame, which is asked for.  A count of layouts said
    /// nothing about time: one name costs a few microseconds and another, in
    /// a script the UI font lacks, a hundred.  Only the making counts, not
    /// the drawing around it, so the allowance is the same whatever else the
    /// frame draws.  Only in the frame loop, which has a next frame; a
    /// snapshot or a test gets every name at once.
    /// </summary>
    private long _textAllowance = long.MaxValue;

    /// <summary>
    /// Phase 8's hook, just before the names: in the frame loop the count of
    /// new layouts a frame was given - <see cref="MotionTextBudget"/> while
    /// the camera moves - gives way to the clock, which the label layer keeps
    /// (<see cref="BeginTextAllowance"/>), and the few texts of the marks
    /// layer are made whenever it wants them.  Outside the loop the count
    /// stays what it always was.
    /// </summary>
    partial void StartTextBudget(bool inMotion)
    {
        if (_inFrameLoop)
        {
            _textBudget = int.MaxValue;
        }
    }

    /// <summary>Ticks spent making text since the label layer began.</summary>
    private long _textSpent;

    /// <summary>
    /// Texts a frame at rest makes whatever the clock says: a view of a few
    /// dozen names is always made whole in one frame, and a frame whose first
    /// text was slow - the first in a process loads the fonts - still gets
    /// somewhere.  Each name is two texts, its layout and its recording.
    /// While the camera moves only the clock counts, beyond the first text:
    /// the next frame is a few milliseconds away whatever happens.
    /// </summary>
    private const int TextsAlwaysAllowed = 64;

    /// <summary>Texts this label layer makes whatever the clock says (see <see cref="TextsAlwaysAllowed"/>).</summary>
    private int _textsAllowed = int.MaxValue;

    /// <summary>Texts laid out or recorded since the label layer began.</summary>
    private int _textsMade;

    /// <summary>The label layer is the first at rest after frames drawn for motion, and makes no text (see <see cref="BeginTextAllowance"/>).</summary>
    private bool _textSettling;

    /// <summary>The last label layer WPF drew was drawn for motion.</summary>
    private bool _labelsWereMoving;

    /// <summary>The last label layer WPF drew left names waiting for their text; the one being drawn does, so far.</summary>
    private bool _labelsLeftWaiting;
    private bool _labelsWaiting;

    /// <summary>This label layer lays the file names out nearest the middle of the view first (see <see cref="LayOutFileNamesFromCentre"/>).</summary>
    private bool _layOutFromCentre;

    /// <summary>
    /// This label layer is WPF's in the frame loop, which replays every text
    /// from its recording (<see cref="WpfLabelTarget"/>): a text laid out is
    /// recorded at once, both charged to the allowance, so a name made is a
    /// name drawn rather than a layout waiting a frame for its recording.
    /// </summary>
    private bool _recordingLabels;

    /// <summary>
    /// Starts the label layer's allowance for text.  The layer that follows
    /// motion - the settle, the frame that also draws every cell at full
    /// detail and turns every name's hinting crisp - makes no text of its
    /// own: its names are drawn from what motion left, at the nearest sizes
    /// laid out, and the frames after it set them exactly, the names nearest
    /// the middle of the view first, an allowance at a time, while the text
    /// worker makes the rest.  The settle that used to lay out every name on
    /// screen in one frame - up to 1,500 of them, 47 to 96 ms - is spread over
    /// short frames and another thread.
    /// </summary>
    private void BeginTextAllowance(bool inMotion)
    {
        _textsMade = 0;
        _textSpent = 0;
        _labelsWaiting = false;

        // Drawn for motion is not always moving: after the camera stops, the
        // frames up to the settle keep the motion look (see RunFrame).  The
        // view those frames show is the one the settle will, so what they
        // cannot make goes to the worker as at rest.  Only a camera still
        // moving leaves its asks behind.
        var cameraMoving = inMotion && IsMoving();
        LabelsDrawnWhileMoving = cameraMoving;
        _askTextWorker = _inFrameLoop && !cameraMoving;
        _recordingLabels = _inFrameLoop;
        if (cameraMoving)
        {
            ForgetTextAsks();
        }

        if (!_inFrameLoop)
        {
            _textAllowance = long.MaxValue;
            _textSettling = false;
            _layOutFromCentre = false;
            return;
        }

        _textSettling = !inMotion && _labelsWereMoving;
        _layOutFromCentre = !inMotion && !_textSettling && _labelsLeftWaiting;
        _textsAllowed = inMotion ? 1 : TextsAlwaysAllowed;
        _textAllowance = (long)((inMotion ? FrameBudgets.MotionTextMs : FrameBudgets.RestTextMs) * System.Diagnostics.Stopwatch.Frequency / 1000);
    }

    private void EndTextAllowance(bool inMotion)
    {
        SendTextAsks();
        _askTextWorker = false;
        _recordingLabels = false;
        _textAllowance = long.MaxValue;
        _textSettling = false;
        _layOutFromCentre = false;
        _labelsWereMoving = inMotion;
        _labelsLeftWaiting = _labelsWaiting;
    }

    /// <summary>Whether one more text may be laid out or recorded now.</summary>
    private bool MayMakeText() =>
        _textBudget > 0 && !_textSettling && (_textsMade < _textsAllowed || _textSpent < _textAllowance);

    /// <summary>A text was made, starting at <paramref name="started"/>: counted, and its time charged to the allowance.</summary>
    private void TextMade(long started)
    {
        _textsMade++;
        _textSpent += System.Diagnostics.Stopwatch.GetTimestamp() - started;
    }

    /// <summary>
    /// A text was drawn at a size near the one wanted, or not at all, for
    /// want of allowance.  When the text worker was handed it
    /// (<paramref name="askedOfWorker"/>) its coming back is what redraws
    /// the names (<see cref="RedrawNamesWhenAllowed"/>); otherwise the next
    /// frame is asked for, which makes it.  So is the next frame after a
    /// layer that makes nothing of its own - the settle - and after the first
    /// layer to leave names waiting, however they will come: the frame after
    /// either lays out the names nearest the middle of the view at once,
    /// rather than whenever the worker's first names happen to come back.
    /// </summary>
    private void TextWaits(bool askedOfWorker = false)
    {
        _labelsWaiting = true;
        if (!askedOfWorker || _textSettling || !_labelsLeftWaiting)
        {
            _textDeferred = true;
        }
    }

    // ---- names made on a worker ------------------------------------------------------

    /// <summary>This label layer hands the names it has no allowance for to the text worker: WPF's names in the frame loop, the camera still.</summary>
    private bool _askTextWorker;

    /// <summary>What this canvas has asked the text worker for and not had back yet, and where it comes back to; null until it first asks.</summary>
    private TextAsks? _textAsks;
    private readonly HashSet<TextKey> _textsAsked = [];

    /// <summary>
    /// Texts the worker handed back unmade, which the frames make themselves
    /// from then on rather than asking again; forgotten wholesale when there
    /// are many, which there never are.
    /// </summary>
    private readonly HashSet<TextKey> _textsNotForWorker = [];

    /// <summary>
    /// A name the frame had no allowance to lay out, the camera still, handed
    /// to the text worker to lay out and record while the frame goes on
    /// without it (<see cref="TextWorker"/>).  Asked once: until it comes
    /// back, or the camera moves and the view it was for is gone.  True when
    /// the worker has it, asked now or before; false when this frame cannot
    /// ask - it is not in the loop, or the camera is moving - or the worker
    /// could not make it before.
    /// </summary>
    private bool AskTextWorker(TextKey key, string text, double level, Brush brush, bool bold, bool icon, bool scaled)
    {
        if (!_askTextWorker || _textsNotForWorker.Contains(key))
        {
            return false;
        }

        if (!_textsAsked.Add(key))
        {
            return true;
        }

        if (_textAsks is null)
        {
            _textAsks = new TextAsks(new FrameInbox<PreparedText>(new FrameDriverSlot(NoFallback.Instance)));
            Drive(_textAsks.Results.Driver);
        }

        _asksThisFrame.Add(new TextRequest(
            _textAsks,
            _textAsks.Generation,
            key,
            text,
            level,
            icon ? IconFace : bold ? TextFaceBold : TextFace,
            brush,
            _scaleY,
            CultureInfo.CurrentUICulture,
            scaled,
            _labelDistance));
        return true;
    }

    /// <summary>
    /// The label layer's asks, handed to the worker at its end nearest the
    /// middle of the view first, so the worker makes the names the eye is on
    /// before the ones at the edges, as the frame's own allowance does.
    /// </summary>
    private void SendTextAsks()
    {
        if (_asksThisFrame.Count == 0)
        {
            return;
        }

        CollectionsMarshal.AsSpan(_asksThisFrame).Sort(static (first, second) => first.Distance.CompareTo(second.Distance));
        foreach (var request in _asksThisFrame)
        {
            TextWorker.Ask(request);
        }

        _asksThisFrame.Clear();
    }

    private readonly List<TextRequest> _asksThisFrame = [];

    /// <summary>
    /// How far the label being drawn is from the middle of the view, squared,
    /// in DIPs: what its asks are ordered by.  Folder titles are nought - the
    /// names of the folders the files are in come first.
    /// </summary>
    private double _labelDistance;

    /// <summary>The camera moved: what was asked for the view it left is no longer wanted, and the worker skips it.</summary>
    private void ForgetTextAsks()
    {
        if (_textAsks is null || _textsAsked.Count == 0)
        {
            return;
        }

        Interlocked.Increment(ref _textAsks.Generation);
        _textsAsked.Clear();
    }

    /// <summary>
    /// The names the text worker has made since the last frame, taken into
    /// the text layouts and their recordings as if the frame had made them:
    /// a frame at rest then finds them made.  Those made for a view or a
    /// scale the canvas has left are dropped.  True when the names want
    /// drawing again: something was taken, or the worker handed a text back
    /// unmade, which the next label layer makes itself.
    /// </summary>
    private bool TakePreparedTexts()
    {
        if (_textAsks is not { } asks)
        {
            return false;
        }

        var inbox = asks.Results;
        inbox.Rearm();
        var taken = 0;
        var unmade = false;
        while (inbox.TryTake(out var prepared))
        {
            _textsAsked.Remove(prepared.Key);
            if (prepared.Generation != Volatile.Read(ref asks.Generation) || prepared.PixelsPerDip != _scaleY)
            {
                continue;
            }

            if (prepared.Text is null)
            {
                if (_textsNotForWorker.Count >= 1024)
                {
                    _textsNotForWorker.Clear();
                }

                _textsNotForWorker.Add(prepared.Key);
                unmade = true;
                continue;
            }

            if (!TryCached(prepared.Key, out _))
            {
                _textCache[prepared.Key] = prepared.Text;
                if (prepared.Recording is { } recording)
                {
                    _textDrawings.AddOrUpdate(prepared.Text, recording);
                }

                taken++;
            }
        }

        PreparedTextsTaken += taken;
        return taken > 0 || unmade;
    }

    /// <summary>For tests: names the text worker made that the canvas took in, since it was made.</summary>
    internal int PreparedTextsTaken { get; private set; }

    /// <summary>A canvas's side of the text worker: where what it made comes back, and which view it is wanted for.</summary>
    private sealed class TextAsks(FrameInbox<PreparedText> results)
    {
        public FrameInbox<PreparedText> Results { get; } = results;

        /// <summary>Moves on when the camera does: a request made under an older one is not made at all.</summary>
        public int Generation;
    }

    /// <summary>A name the worker is asked to lay out and record, with everything the UI thread would have made it with.</summary>
    private readonly record struct TextRequest(
        TextAsks Owner,
        int Generation,
        TextKey Key,
        string Text,
        double Level,
        Typeface Face,
        Brush Brush,
        double PixelsPerDip,
        CultureInfo Culture,
        bool Scaled,
        double Distance);

    /// <summary>A name the worker made: its layout and its recording, or neither when it could not make it.</summary>
    private readonly record struct PreparedText(TextKey Key, int Generation, double PixelsPerDip, FormattedText? Text, Drawing? Recording);

    /// <summary>
    /// One thread of the process that lays names out and records them for
    /// WPF's label layer, so a frame at rest with more new names than its
    /// allowance - a jump into a folder of 1,800 files - leaves the rest to
    /// it rather than to the next forty frames of the UI thread.  The text
    /// is laid out exactly as <see cref="Layout"/> lays it out - the same
    /// face, size, brush, culture, scale and room - and recorded as
    /// <see cref="TextDrawingOf"/> records it; the layout and its frozen
    /// recording are handed back through the canvas's inbox, and are only
    /// ever touched by the UI thread from then on.  WPF lays text out with
    /// the formatter of the thread's own dispatcher, which this thread has
    /// for that alone; it shows no window and runs no message loop.
    /// </summary>
    private static class TextWorker
    {
        private static readonly System.Collections.Concurrent.BlockingCollection<TextRequest> Requests = new();
        private static int _started;

        public static void Ask(in TextRequest request)
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                var thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "nested canvas names",
                    Priority = ThreadPriority.BelowNormal
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }

            Requests.Add(request);
        }

        private static void Run()
        {
            // The canvas's faces made again here, for this thread alone: a
            // typeface looks its fonts up lazily, and the UI thread's are
            // the UI thread's.
            var faces = new Dictionary<Typeface, Typeface>(ReferenceEqualityComparer.Instance);
            foreach (var request in Requests.GetConsumingEnumerable())
            {
                var owner = request.Owner;
                if (request.Generation != Volatile.Read(ref owner.Generation))
                {
                    continue;
                }

                FormattedText? text = null;
                Drawing? recording = null;
                try
                {
                    text = new FormattedText(
                        request.Text,
                        request.Culture,
                        FlowDirection.LeftToRight,
                        FaceHere(faces, request.Face),
                        request.Level,
                        request.Brush,
                        request.PixelsPerDip)
                    {
                        MaxLineCount = 1,
                        Trimming = TextTrimming.CharacterEllipsis
                    };

                    if (request.Key.Width >= 0)
                    {
                        text.MaxTextWidth = Math.Max(1, request.Key.Width * (request.Scaled ? 8 : 6));
                    }

                    _ = text.Width;
                    var group = new DrawingGroup();
                    using (var context = group.Open())
                    {
                        context.DrawText(text, new Point(0, 0));
                    }

                    group.Freeze();
                    recording = group;
                }
                catch (Exception ex)
                {
                    // Everything is caught: an exception escaping this thread
                    // would end the program.  Handed back empty, the name is
                    // made by the canvas itself, which never asks for it again.
                    System.Diagnostics.Debug.WriteLine($"The name \"{request.Text}\" could not be laid out off the UI thread: {ex.Message}");
                    text = null;
                    recording = null;
                }

                owner.Results.Post(new PreparedText(request.Key, request.Generation, request.PixelsPerDip, text, recording));
            }
        }
    }

    /// <summary>The worker's own copy of one of the canvas's faces.</summary>
    private static Typeface FaceHere(Dictionary<Typeface, Typeface> faces, Typeface face)
    {
        if (!faces.TryGetValue(face, out var here))
        {
            here = new Typeface(new FontFamily(face.FontFamily.Source), face.Style, face.Weight, face.Stretch);
            faces[face] = here;
        }

        return here;
    }

    /// <summary>A driver for a canvas's own inbox while it cannot draw: nothing to do, the names wait for its next frame.</summary>
    private sealed class NoFallback : IFrameDriver
    {
        public static readonly NoFallback Instance = new();

        public void Wake()
        {
        }
    }

    // ---- file names --------------------------------------------------------------

    /// <summary>
    /// The names on file tiles big enough to carry one, into any target: the
    /// icon, the colour mark and the note sign, the size when there is room
    /// for it, and the name in what is left.  A frame that has names waiting
    /// from the last one lays out the ones nearest the middle of the view
    /// first, then draws them all in their order.
    /// </summary>
    private void DrawFileLabels(LabelTarget target)
    {
        if (_layOutFromCentre && ReferenceEquals(target, _wpfLabels))
        {
            LayOutFileNamesFromCentre();
        }

        FolderFacts? folderFacts = null;
        foreach (ref readonly var job in CollectionsMarshal.AsSpan(_fileLabels))
        {
            DrawFileLabel(target, job, ref folderFacts);
        }
    }

    /// <summary>
    /// One file's label.  <paramref name="folderFacts"/> carries its folder's
    /// facts from one label to the next: a folder's files are drawn one after
    /// another, so they are looked up once per folder, not once per file.
    /// </summary>
    private void DrawFileLabel(LabelTarget target, in FileLabelJob job, ref FolderFacts? folderFacts)
    {
        var folder = job.Folder;
        if (job.Index >= folder.Files.Count)
        {
            return;
        }

        target.BeginLabel(folder, job.Index);
        var dx = job.X + job.W / 2 - _viewWidth / 2;
        var dy = job.Y + job.H / 2 - _viewHeight / 2;
        _labelDistance = dx * dx + dy * dy;
        var file = folder.Files[job.Index];
        var font = Math.Clamp(job.H * 0.5, 7.5, 13);
        var cursor = job.X + Math.Min(3, job.H * 0.14) + font * 0.55;
        var right = job.X + job.W - font * 0.5;

        var iconSize = Math.Min(job.H * 0.72, 20);
        if (iconSize >= 9 && Shows(CanvasLayer.Icons)
            && target.DrawIcon(folder, job.Index, file, new Rect(cursor, job.Y + (job.H - iconSize) / 2, iconSize, iconSize)))
        {
            cursor += iconSize + font * 0.4;
        }

        // Nearly every folder holds no mark at all, and its files are not
        // looked up: no path is made for them, and nothing kept.
        if (folderFacts is null || !ReferenceEquals(folderFacts.Folder, folder))
        {
            folderFacts = FactsOf(folder);
        }

        var mark = FolderMark.None;
        if (Shows(CanvasLayer.Marks) && HoldsMarks(folderFacts))
        {
            var facts = FactsOf(folder, file);
            mark = MarkOf(folder, file, facts);
            if (facts.HasAccent)
            {
                target.FillRect(new Rect(job.X + 1, job.Y + job.H * 0.12, Math.Max(2, job.H * 0.16), job.H * 0.76), facts.Accent);
            }
        }

        if (!string.IsNullOrWhiteSpace(mark.Note))
        {
            var note = target.Text("\uE70B", font * 0.85, TextDimColour, double.MaxValue, LabelFace.Icons, scaled: true);
            right -= note.Width;
            target.DrawText(note, new Point(right, job.Y + (job.H - note.Height) / 2));
            right -= font * 0.4;
        }

        // A folder ordered by date or type is being read for exactly that, so
        // its tiles say it where they are narrower, and leave the name less.
        var column = folder.PlacedSort.Column;
        var asked = column is SortColumn.Modified or SortColumn.Type;
        var nameRoom = font * (asked ? 3.5 : 5);
        if (job.W >= (asked ? 120 : 190) && Shows(CanvasLayer.Details) && FileDetailText(column, file, dateOnly: false) is { Length: > 0 } text)
        {
            var detail = target.Text(text, font * 0.85, TextDimColour, double.MaxValue, LabelFace.Regular, scaled: true);

            // A date and a time that leave the name no room give up the time.
            if (right - detail.Width - cursor <= nameRoom && column == SortColumn.Modified)
            {
                detail = target.Text(FileDetailText(SortColumn.Modified, file, dateOnly: true), font * 0.85, TextDimColour, double.MaxValue, LabelFace.Regular, scaled: true);
            }

            if (right - detail.Width - cursor > nameRoom)
            {
                right -= detail.Width;
                target.DrawText(detail, new Point(right, job.Y + (job.H - detail.Height) / 2));
                right -= font * 0.6;
            }
        }

        if (right - cursor > font)
        {
            var faded = file.IsHidden || _filter is not null && !_filter(file.Name);
            var name = target.Text(file.Name, font, faded ? TextDimColour : TextColour, right - cursor, LabelFace.Regular, scaled: true);
            target.DrawText(name, new Point(cursor, job.Y + (job.H - name.Height) / 2));
        }
    }

    // What the names nearest the middle of the view are found by: each file
    // label's distance from it, sorted with the labels' places.  Grown as
    // needed and kept, so sorting allocates nothing.
    private double[] _centreDistances = [];
    private int[] _centreOrder = [];
    private LayoutOnlyTarget? _layoutOnly;

    /// <summary>
    /// The names still waiting for their text, laid out and recorded nearest
    /// the middle of the view first - where the eye is - until the frame's
    /// allowance runs out, without drawing anything; the frame then draws
    /// every label in its own order with what there is.  Only after a frame
    /// that left names waiting: otherwise every name has its text, and this
    /// would only be a second walk through the labels.
    /// </summary>
    private void LayOutFileNamesFromCentre()
    {
        var jobs = CollectionsMarshal.AsSpan(_fileLabels);
        if (jobs.Length == 0)
        {
            return;
        }

        if (_centreOrder.Length < jobs.Length)
        {
            _centreDistances = new double[jobs.Length * 2];
            _centreOrder = new int[jobs.Length * 2];
        }

        var middleX = _viewWidth / 2;
        var middleY = _viewHeight / 2;
        for (var index = 0; index < jobs.Length; index++)
        {
            var dx = jobs[index].X + jobs[index].W / 2 - middleX;
            var dy = jobs[index].Y + jobs[index].H / 2 - middleY;
            _centreDistances[index] = dx * dx + dy * dy;
            _centreOrder[index] = index;
        }

        Array.Sort(_centreDistances, _centreOrder, 0, jobs.Length);
        var target = _layoutOnly ??= new LayoutOnlyTarget(this, _wpfLabels);
        FolderFacts? folderFacts = null;
        for (var place = 0; place < jobs.Length && MayMakeText(); place++)
        {
            DrawFileLabel(target, jobs[_centreOrder[place]], ref folderFacts);
        }
    }

    // ---- what is kept about folders and files ------------------------------------

    private const int MaximumKeptFolders = 4096;
    private Dictionary<NestedFolder, FolderFacts> _folderFacts = [];
    private Dictionary<NestedFolder, FolderFacts> _oldFolderFacts = [];

    /// <summary>
    /// What a folder's labels need to know about it besides what the folder
    /// says itself, found once and kept while it stays in view, each with
    /// what it was found from: the words for its counts (by its state, its
    /// counts and the culture), whether it is pinned (by the pins), and
    /// whether anything in it carries a mark (by the palette stamp, which
    /// every change of a mark moves on).  A title looks these up once per
    /// change rather than once per frame, and a file's label asks its folder
    /// once for all of the folder's files.
    /// </summary>
    private sealed class FolderFacts(NestedFolder folder)
    {
        public NestedFolder Folder { get; } = folder;

        public string Detail { get; set; } = string.Empty;

        /// <summary>The counts <see cref="Detail"/> is made of, for a title with no room for its date as well.</summary>
        public string DetailCounts { get; set; } = string.Empty;

        /// <summary>The date <see cref="Detail"/> ends in, or nothing.</summary>
        public string DetailDate { get; set; } = string.Empty;

        public int DetailFolders { get; set; } = -1;

        public int DetailFiles { get; set; } = -1;

        /// <summary>The folder's time the detail shows, or zero when it shows none.</summary>
        public long DetailTicks { get; set; } = -1;

        public CultureInfo? DetailCulture { get; set; }

        public int PinStamp { get; set; } = -1;

        public bool IsPinned { get; set; }

        public int MarkStamp { get; set; } = -1;

        public bool HoldsMarks { get; set; }
    }

    /// <summary>
    /// A folder's <see cref="FolderFacts"/>.  Kept in two generations, like
    /// the text layouts, so it stays the size of what is on screen; a folder
    /// read again is the same folder, and its facts say themselves when they
    /// are out of date.
    /// </summary>
    private FolderFacts FactsOf(NestedFolder folder)
    {
        if (_folderFacts.TryGetValue(folder, out var facts))
        {
            return facts;
        }

        if (!_oldFolderFacts.Remove(folder, out facts))
        {
            facts = new FolderFacts(folder);
        }

        if (_folderFacts.Count >= MaximumKeptFolders)
        {
            (_oldFolderFacts, _folderFacts) = (_folderFacts, _oldFolderFacts);
            _folderFacts.Clear();
        }

        _folderFacts[folder] = facts;
        return facts;
    }

    /// <summary>
    /// Whether anything in a folder carries a mark: from the mark service by
    /// the folder, when the marks come from one (see
    /// <see cref="FolderMarkService.MarkedFolders"/>); with only a lookup by
    /// path, every folder may, and its files are looked up one by one.
    /// </summary>
    private bool HoldsMarks(FolderFacts facts)
    {
        if (facts.MarkStamp != _paletteStamp)
        {
            facts.MarkStamp = _paletteStamp;
            facts.HoldsMarks = _markService is { } marks ? marks.HasMarksIn(facts.Folder) : _markLookup is not null;
        }

        return facts.HoldsMarks;
    }

    // Whose pins the facts were found under: the beacons are handed over as
    // a new list whenever they change, and the pins with them.
    private IReadOnlyList<NestedBeacon>? _pinsFrom;
    private int _pinStamp;

    /// <summary>A stamp that moves on whenever the pins may have: a folder's pin is looked up again only then.</summary>
    private int PinStamp()
    {
        if (!ReferenceEquals(_pinsFrom, _beacons))
        {
            _pinsFrom = _beacons;
            _pinStamp++;
        }

        return _pinStamp;
    }

    private bool IsPinned(NestedFolder folder, FolderFacts facts, int pins)
    {
        if (facts.PinStamp != pins)
        {
            facts.PinStamp = pins;
            facts.IsPinned = IsPinned(folder.FullPath);
        }

        return facts.IsPinned;
    }

    /// <summary>
    /// What a file's label needs to know besides the file, found once and
    /// kept while its tile stays in view: its mark, and whether that has a
    /// colour.  Asked only for the files of a folder that holds a mark; kept
    /// by the very strings of the folder's path and the file's name, not
    /// their text, so a lookup hashes no characters, and holding no folder: a
    /// folder read again has new names, and simply misses.
    /// </summary>
    private sealed class FileFacts
    {
        /// <summary>The file's path, made only when the marks are looked up by path.</summary>
        public string? Path { get; set; }

        /// <summary>The <see cref="_paletteStamp"/> <see cref="Mark"/> was looked up under, or -1 before it was.</summary>
        public int MarkStamp { get; set; } = -1;

        public FolderMark Mark { get; set; } = FolderMark.None;

        /// <summary>Whether the mark has a colour, read with it, so the colour is parsed once rather than every frame.</summary>
        public bool HasAccent { get; set; }

        public Color Accent { get; set; }
    }

    /// <summary>A file's <see cref="FileFacts"/>, in two generations like the text layouts.</summary>
    private FileFacts FactsOf(NestedFolder folder, in NestedFile file)
    {
        var key = (folder.FullPath, file.Name);
        if (_fileFacts.TryGetValue(key, out var facts))
        {
            return facts;
        }

        if (!_oldFileFacts.Remove(key, out facts))
        {
            facts = new FileFacts();
        }

        if (_fileFacts.Count >= MaximumKeptFiles)
        {
            (_oldFileFacts, _fileFacts) = (_fileFacts, _oldFileFacts);
            _fileFacts.Clear();
        }

        _fileFacts[key] = facts;
        return facts;
    }

    /// <summary>
    /// A file's mark, looked up again only when marks may have changed: kept
    /// under the same stamp that keeps a folder's colours, which every change
    /// of a mark moves on (<see cref="InvalidateMarks"/>), so a file's mark is
    /// exactly as fresh as its folder's.  From the mark service by folder and
    /// name, with no path made, when the marks come from one.
    /// </summary>
    private FolderMark MarkOf(NestedFolder folder, in NestedFile file, FileFacts facts)
    {
        if (facts.MarkStamp != _paletteStamp)
        {
            facts.Mark = _markService is { } marks
                ? marks.GetIn(folder, file.Name)
                : _markLookup?.Invoke(facts.Path ??= folder.PathOf(file)) ?? FolderMark.None;
            facts.MarkStamp = _paletteStamp;
            facts.HasAccent = TryParseColour(facts.Mark.AccentHex, out var accent);
            facts.Accent = accent;
        }

        return facts.Mark;
    }

    private static readonly string[] SizeSuffixes = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>
    /// What a file's tile says on its right, by what its folder is ordered
    /// by - the thing a person sorting by it is looking for: when it was
    /// written for a date order, what kind of file it is for a type order,
    /// its size otherwise, as it always said.  Every text is made once and
    /// kept, never once per frame.
    /// </summary>
    internal string FileDetailText(SortColumn column, in NestedFile file, bool dateOnly) => column switch
    {
        SortColumn.Modified => DateText(file.ModifiedTicks, dateOnly),
        SortColumn.Type => TypeText(file.Extension),
        _ => FormatSize(file.Length)
    };

    /// <summary>
    /// The first moment a FILETIME can say, 1 January 1601 UTC, in ticks.  A
    /// file system that keeps no date hands over a FILETIME of zero, which
    /// arrives as exactly this - a time nobody gave, not one to show.
    /// </summary>
    private static readonly long FileTimeEpochTicks = DateTime.FromFileTimeUtc(0).Ticks;

    /// <summary>
    /// A time in UTC ticks as the culture writes a date and a time, or only
    /// the date, in local time; nothing for a time nobody gave.  Kept by the
    /// minute, which is all the text shows.
    /// </summary>
    internal string DateText(long utcTicks, bool dateOnly)
    {
        if (utcTicks <= FileTimeEpochTicks || utcTicks > DateTime.MaxValue.Ticks)
        {
            return string.Empty;
        }

        var minute = utcTicks / TimeSpan.TicksPerMinute;
        var texts = dateOnly ? _dayTexts : _minuteTexts;
        if (texts.TryGet(minute, out var known))
        {
            return known;
        }

        // A culture whose calendar spans only some years - Saudi Arabia's
        // Umm al-Qura, 1900 to 2077 - throws for a date outside them, and
        // this is made inside a frame, so such a date is written the
        // invariant way instead: a file from 1850 or 2100 still says when it
        // is from rather than taking the window down.
        var local = new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime();
        var culture = CultureInfo.CurrentCulture;
        var calendar = culture.DateTimeFormat.Calendar;
        if (local < calendar.MinSupportedDateTime || local > calendar.MaxSupportedDateTime)
        {
            culture = CultureInfo.InvariantCulture;
        }

        return texts.Add(minute, local.ToString(dateOnly ? "d" : "g", culture));
    }

    /// <summary>
    /// Explorer's name for a kind of file, kept per extension.  A kind the
    /// Shell has not been asked about yet reads "XYZ File" for the moment,
    /// is asked about in the background, and the tiles are drawn again with
    /// the real name once it is there - the UI thread never waits for it.
    /// </summary>
    private string TypeText(string extension)
    {
        if (_typeTexts.TryGetValue(extension, out var known))
        {
            return known;
        }

        if (_typeTexts.Count >= MaximumTypeTexts)
        {
            _typeTexts.Clear();
        }

        if (!FileTypeNames.TryGet(extension, out var name) && !_typeTextsWaiting)
        {
            _ = ForgetStandInTypesWhenKnownAsync();
        }

        _typeTexts[extension] = name;
        return name;
    }

    private const int MaximumTypeTexts = 4096;
    private readonly Dictionary<string, string> _typeTexts = new(StringComparer.Ordinal);
    private bool _typeTextsWaiting;

    private async Task ForgetStandInTypesWhenKnownAsync()
    {
        _typeTextsWaiting = true;
        try
        {
            // Never straight back into the label that asked: names that had
            // all arrived already would be forgotten before it kept its
            // stand-in, which would then stay.
            await Task.Yield();
            await FileTypeNames.WhenPrefetchedAsync();
        }
        finally
        {
            _typeTextsWaiting = false;
        }

        _typeTexts.Clear();
        RequestFrame(Layers.Labels);
    }

    /// <summary>A file's size as its tile and its tag say it, made once per size rather than once per frame.</summary>
    private string FormatSize(long bytes)
    {
        if (_sizeTexts.TryGet(bytes, out var known))
        {
            return known;
        }

        double value = bytes;
        var suffix = 0;
        while (value >= 1024 && suffix < SizeSuffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }

        return _sizeTexts.Add(bytes, suffix == 0 ? $"{bytes:N0} B" : $"{value:0.#} {SizeSuffixes[suffix]}");
    }

    // ---- folder titles -----------------------------------------------------------

    private static LabelMode LabelModeFor(double w, bool labelsAllowed)
    {
        if (!labelsAllowed)
        {
            return LabelMode.None;
        }

        var font = Math.Min(MaximumFontSize, w * NestedLayout.HeaderHeight * 0.62);
        if (font >= MinimumHeaderFont && w >= 48)
        {
            return LabelMode.Header;
        }

        return w >= PillMinimumWidth ? LabelMode.Pill : LabelMode.None;
    }

    /// <summary>
    /// The names on the cells, into any target: folder titles and pills first,
    /// then the file names over them.  The handles to grab a folder by its
    /// name are gathered again with them, so what can be grabbed is exactly
    /// what was drawn, whatever drew it.
    /// </summary>
    private void DrawLabelLayer(LabelTarget target)
    {
        _labelHotspots.Clear();
        DrawLabels(target);
        DrawFileLabels(target);
    }

    private void DrawLabels(LabelTarget target)
    {
        var pixelsPerDip = _scaleY;
        var pins = PinStamp();
        _labelDistance = 0;
        foreach (var job in _labels)
        {
            var folder = job.Folder;
            target.BeginLabel(folder, -1);
            if (job.Mode == LabelMode.Pill)
            {
                var text = target.Text(folder.Name, PillFontSize, IsFilteredOut(folder) ? TextDimColour : TextColour, Math.Max(8, job.W - 10), LabelFace.Regular, scaled: false);
                var pill = new Rect(job.X + 2, job.Y + 2, Math.Min(job.W - 4, text.Width + 8), text.Height + 2);
                if (pill.Width < 12)
                {
                    continue;
                }

                target.FillRounded(pill, 3, PillColour);
                target.DrawText(text, new Point(pill.X + 4, pill.Y + 1));
                AddGrabHotspot(folder, pill);
                continue;
            }

            var header = job.W * NestedLayout.HeaderHeight;
            if (job.Y + header < 0 || job.Y > _viewHeight)
            {
                DrawBodyNote(target, folder, job);
                continue;
            }

            var font = Math.Min(MaximumFontSize, header * 0.62);
            var stripeRight = 1 + Math.Max(2, header * 0.18) + Math.Max(2 / pixelsPerDip, Math.Min(4, header * 0.12));

            // A cell wider than the screen keeps its name at the visible edge,
            // the way a sticky column header does: its own left edge may be a
            // whole screen away, and a title nobody can see names nothing.
            var cursor = job.X >= 0 ? job.X + stripeRight + font * 0.45 : font * 0.6;
            var right = Math.Min(job.X + job.W, _viewWidth) - font * 0.6;

            // Right-hand details first, so the name knows how much room is left.
            var facts = FactsOf(folder);
            var badges = Badges(folder, facts, pins);
            if (badges.Length > 0)
            {
                var icons = target.Text(badges, font * 0.82, BadgeColour(folder, facts, pins), double.MaxValue, LabelFace.Icons, scaled: true);
                right -= icons.Width;
                if (right - cursor > font * 3)
                {
                    target.DrawText(icons, new Point(right, job.Y + (header - icons.Height) / 2));
                    right -= font * 0.4;
                }
                else
                {
                    right += icons.Width;
                }
            }

            if (job.W >= 280 && TitleDetail(folder, facts) is { Length: > 0 } detail)
            {
                var info = target.Text(detail, font * 0.78, TextDimColour, double.MaxValue, LabelFace.Regular, scaled: true);

                // Counts and a date that leave the name no room give up the
                // counts - the date is what the order is about - and then try
                // the counts alone, the way a tile gives up a time.
                if (right - info.Width - cursor <= font * 6 && ReferenceEquals(detail, facts.Detail)
                    && facts.DetailDate.Length > 0 && facts.DetailCounts.Length > 0)
                {
                    info = target.Text(facts.DetailDate, font * 0.78, TextDimColour, double.MaxValue, LabelFace.Regular, scaled: true);
                    if (right - info.Width - cursor <= font * 6)
                    {
                        info = target.Text(facts.DetailCounts, font * 0.78, TextDimColour, double.MaxValue, LabelFace.Regular, scaled: true);
                    }
                }

                if (right - info.Width - cursor > font * 6)
                {
                    right -= info.Width;
                    target.DrawText(info, new Point(right, job.Y + (header - info.Height) / 2));
                    right -= font * 0.6;
                }
            }

            if (Shows(CanvasLayer.Icons))
            {
                var glyph = target.Text(Glyph(folder), font * 0.9, GlyphColour(folder), double.MaxValue, LabelFace.Icons, scaled: true);
                if (right - cursor > glyph.Width + font)
                {
                    target.DrawText(glyph, new Point(cursor, job.Y + (header - glyph.Height) / 2 + font * 0.05));
                    cursor += glyph.Width + font * 0.4;
                }
            }

            var available = right - cursor;
            if (available > font)
            {
                var face = folder.Kind != NestedFolderKind.Folder ? LabelFace.SemiBold : LabelFace.Regular;
                var name = target.Text(folder.Name, font, IsFilteredOut(folder) ? TextDimColour : TextColour, available, face, scaled: true);
                target.DrawText(name, new Point(cursor, job.Y + (header - name.Height) / 2));
                AddGrabHotspot(folder, new Rect(job.X, job.Y, job.W, header));
            }

            DrawBodyNote(target, folder, job);
        }
    }

    /// <summary>What an empty cell says in its middle, when there is room to say it.</summary>
    private void DrawBodyNote(LabelTarget target, NestedFolder folder, LabelJob job)
    {
        var h = job.W * NestedLayout.CellHeight;
        var header = job.W * NestedLayout.HeaderHeight;
        if (h - header < 40 || job.W < 120)
        {
            return;
        }

        string message;
        var ink = TextDimColour;
        if (folder.IsReparsePoint)
        {
            message = "Link to another folder";
        }
        else if (folder.LoadState == NestedLoadState.Failed)
        {
            message = string.IsNullOrEmpty(folder.ErrorMessage) ? "Could not be read" : folder.ErrorMessage;
            ink = DangerColour;
        }
        else if (folder.LoadState is NestedLoadState.NotLoaded or NestedLoadState.Queued or NestedLoadState.Loading)
        {
            message = folder.IsComputer ? string.Empty : "Reading…";
        }
        else if (folder.Children.Count == 0 && !Shows(CanvasLayer.Files) && FilesBehindLayer(folder) is > 0 and var files)
        {
            // The files are not drawn, but the folder is not empty.
            message = files == 1 ? "1 file" : NoteText(FilesNote, files);
        }
        else if (folder.Children.Count == 0 && folder.Files.Count == 0)
        {
            message = folder.FileCount switch
            {
                0 => "Empty folder",
                1 => "1 hidden file",
                _ => NoteText(HiddenFilesNote, folder.FileCount)
            };
        }
        else if (folder.UnlistedFileCount > 0 && Shows(CanvasLayer.Files))
        {
            message = NoteText(UnlistedFilesNote, folder.UnlistedFileCount);
            var text = target.Text(message, Math.Clamp(job.W * 0.018, 9, 14), TextDimColour, job.W - 16, LabelFace.Regular, scaled: true);
            target.DrawText(text, new Point(job.X + (job.W - text.Width) / 2, job.Y + h - text.Height - 4));
            return;
        }
        else if (folder.IsTruncated)
        {
            message = NoteText(TruncatedNote, NestedTree.MaximumChildren);
            var text = target.Text(message, Math.Clamp(job.W * 0.018, 9, 14), TextDimColour, job.W - 16, LabelFace.Regular, scaled: true);
            target.DrawText(text, new Point(job.X + (job.W - text.Width) / 2, job.Y + h - text.Height - 4));
            return;
        }
        else
        {
            return;
        }

        if (message.Length == 0)
        {
            return;
        }

        var size = Math.Clamp(job.W * 0.03, 9, 18);
        var formatted = target.Text(message, size, ink, job.W - 16, LabelFace.Regular, scaled: true);
        var bodyTop = job.Y + header;
        target.DrawText(formatted, new Point(job.X + (job.W - formatted.Width) / 2, bodyTop + (h - header - formatted.Height) / 2));
    }

    private const int HiddenFilesNote = 1;
    private const int UnlistedFilesNote = 2;
    private const int TruncatedNote = 3;
    private const int FilesNote = 4;

    /// <summary>The files the files layer keeps off a folder: every one it counted, less the hidden ones while hidden items are not shown.</summary>
    private int FilesBehindLayer(NestedFolder folder) =>
        _tree is { IncludeHidden: true } ? folder.FileCount : folder.FileCount - folder.HiddenFileCount;

    /// <summary>
    /// One of the notes with a count in it, made once per count rather than
    /// once per frame.
    /// </summary>
    private string NoteText(int note, int count)
    {
        var key = (long)note << 32 | (uint)count;
        if (_noteTexts.TryGet(key, out var known))
        {
            return known;
        }

        return _noteTexts.Add(key, note switch
        {
            HiddenFilesNote => $"{count:N0} hidden files",
            UnlistedFilesNote => $"{count:N0} more files are not drawn",
            FilesNote => $"{count:N0} files",
            _ => $"Only the first {count:N0} folders are shown"
        });
    }

    /// <summary>What a title says on its right: a drive's free space, or how many folders and files a folder holds.</summary>
    internal string DetailText(NestedFolder folder) => DetailText(folder, null);

    /// <summary>
    /// <see cref="DetailText(NestedFolder, FolderFacts?)"/> as a title shows
    /// it: with the folder counts layer off, a folder's title keeps only the
    /// date its order is about; a drive's free space is no count, and stays.
    /// </summary>
    private string TitleDetail(NestedFolder folder, FolderFacts facts)
    {
        var detail = DetailText(folder, facts);
        return Shows(CanvasLayer.FolderCounts) || detail.Length == 0 || !ReferenceEquals(detail, facts.Detail)
            ? detail
            : facts.DetailDate;
    }

    /// <summary><see cref="TitleDetail"/> for a folder, for tests: what its title says on its right.</summary>
    internal string TitleDetailForTests(NestedFolder folder) => TitleDetail(folder, FactsOf(folder));

    /// <summary>
    /// <see cref="DetailText(NestedFolder)"/>, kept on the folder's facts with
    /// the counts and the culture it was written for, so a title shown frame
    /// after frame finds its words where it left them; the words themselves
    /// are made once per pair of counts, whichever folder has them.
    /// </summary>
    private string DetailText(NestedFolder folder, FolderFacts? facts)
    {
        if (folder.IsComputer)
        {
            return string.Empty;
        }

        if (folder.Kind == NestedFolderKind.Drive && !string.IsNullOrEmpty(folder.SecondaryText))
        {
            return folder.SecondaryText;
        }

        if (folder.LoadState != NestedLoadState.Loaded)
        {
            return string.Empty;
        }

        var folders = folder.Children.Count;
        var files = folder.FileCount;
        if (folders == 0 && files == 0)
        {
            return string.Empty;
        }

        // Where the folder it is in is ordered by date, when it was written
        // is what places it, and the title says it.  Not for a folder whose
        // own contents are by date and nothing more: its date comes from its
        // parent's listing, which a write inside it reads again only while
        // the parent is ordered by date - the title would show a time the
        // newest file in it had already passed.
        var ticks = folder.Parent is { IsComputer: false } parent && parent.PlacedSort.Column == SortColumn.Modified
            ? folder.ModifiedTicks
            : 0;
        facts ??= FactsOf(folder);
        var culture = CultureInfo.CurrentCulture;
        if (facts.DetailFolders == folders && facts.DetailFiles == files && facts.DetailTicks == ticks
            && ReferenceEquals(facts.DetailCulture, culture))
        {
            return facts.Detail;
        }

        var counts = CountsText(folders, files);
        var date = DateText(ticks, dateOnly: false);
        facts.Detail = date.Length == 0 ? counts : counts.Length == 0 ? date : string.Concat(counts, "  ·  ", date);
        facts.DetailCounts = counts;
        facts.DetailDate = date;
        facts.DetailFolders = folders;
        facts.DetailFiles = files;
        facts.DetailTicks = ticks;
        facts.DetailCulture = culture;
        return facts.Detail;
    }

    /// <summary>"3 folders  ·  12 files", made once per pair of counts rather than once per title per frame.</summary>
    private string CountsText(int folders, int files)
    {
        var key = (long)folders << 32 | (uint)files;
        if (_detailTexts.TryGet(key, out var known))
        {
            return known;
        }

        var parts = new List<string>(2);
        if (folders > 0)
        {
            parts.Add(folders == 1 ? "1 folder" : $"{folders:N0} folders");
        }

        if (files > 0)
        {
            parts.Add(files == 1 ? "1 file" : $"{files:N0} files");
        }

        return _detailTexts.Add(key, string.Join("  ·  ", parts));
    }

    /// <summary>
    /// A title's badges for every mix of them, written in the order a title
    /// shows them - pinned, noted, a link, unreadable - and indexed by those
    /// four as bits: a title's badges are looked up, not joined anew each frame.
    /// </summary>
    private static readonly string[] BadgeTexts = MakeBadgeTexts();

    private static string[] MakeBadgeTexts()
    {
        var texts = new string[16];
        for (var bits = 0; bits < texts.Length; bits++)
        {
            var badges = string.Empty;
            if ((bits & 1) != 0)
            {
                badges += "\uE735";
            }

            if ((bits & 2) != 0)
            {
                badges += "\uE70B";
            }

            if ((bits & 4) != 0)
            {
                badges += "\uE71B";
            }

            if ((bits & 8) != 0)
            {
                badges += "\uE72E";
            }

            texts[bits] = badges;
        }

        return texts;
    }

    /// <summary>A title's badges: its pin from its facts, its note from its colours (<see cref="NestedFolder.HasNote"/>), the rest from the folder.</summary>
    private string Badges(NestedFolder folder, FolderFacts facts, int pins) =>
        BadgeTexts[(IsPinned(folder, facts, pins) ? 1 : 0)
            | (folder.HasNote ? 2 : 0)
            | (folder.IsReparsePoint ? 4 : 0)
            | (folder.LoadState == NestedLoadState.Failed ? 8 : 0)];

    private Color BadgeColour(NestedFolder folder, FolderFacts facts, int pins) =>
        folder.LoadState == NestedLoadState.Failed ? DangerColour : IsPinned(folder, facts, pins) ? StarColour : TextDimColour;

    private static string Glyph(NestedFolder folder) => folder.Kind switch
    {
        NestedFolderKind.Computer => "",
        NestedFolderKind.Drive => folder.FullPath.StartsWith(@"\\", StringComparison.Ordinal) ? "" : "",
        _ => folder.IsReparsePoint ? "" : ""
    };

    private static Color GlyphColour(NestedFolder folder) => folder.Kind switch
    {
        NestedFolderKind.Folder when folder.HasLabel => NestedRaster.Unpack(folder.StripeColour),
        NestedFolderKind.Folder => FolderColour,
        _ => TextDimColour
    };

    private Brush BrushFor(Color colour)
    {
        if (!_brushes.TryGetValue(colour, out var brush))
        {
            brush = Frozen(colour);
            _brushes[colour] = brush;
        }

        return brush;
    }

    // ---- text ------------------------------------------------------------------

    /// <summary>
    /// Text laid out once and drawn at any size.  A name whose size follows the
    /// zoom is laid out at the nearest of a fixed ladder of sizes - eight to an
    /// octave, a mip chain for type - and drawn through a scale to the exact
    /// size wanted.  Its available width is kept in the same units, so during
    /// a zoom both grow together and the layout found in the cache is the one
    /// needed: the text is no longer laid out anew every frame.
    /// </summary>
    private ScaledText Text(string text, double size, Brush brush, double maxWidth, bool bold, bool icon = false, bool scaled = false)
    {
        size = Math.Clamp(size, 1, 400);
        var level = scaled ? LevelFor(size) : Math.Round(size * 4) / 4;

        // The whole name first.  Most names fit, and a name that fits does
        // not depend on the room it has - so it is the same layout at every
        // step of a zoom, and only the few that are cut short are laid out
        // again as their room changes.
        var natural = Layout(text, level, size, double.PositiveInfinity, brush, bold, icon, scaled);
        if (natural.Text is null || !(maxWidth < 10_000) || natural.Width <= maxWidth)
        {
            return natural;
        }

        var trimmed = Layout(text, level, size, maxWidth, brush, bold, icon, scaled);
        return trimmed.Text is null ? default : trimmed;
    }

    /// <summary>The level of the ladder a scaled text of <paramref name="size"/> is laid out at: the nearest fourth root of two.</summary>
    internal static double LevelFor(double size) =>
        Math.Pow(2, Math.Round(Math.Log2(size) * LevelsPerOctave) / LevelsPerOctave);

    /// <summary>
    /// The level <paramref name="rungs"/> steps up the ladder from
    /// <paramref name="level"/> (down, for a negative count), worked out from
    /// the rung's number exactly as <see cref="LevelFor"/> works it out, so it
    /// is bit for bit the level a layout made there is kept under.  A level
    /// multiplied by a power of the fourth root of two mostly is not, and the
    /// cache compares its levels exactly: the nearest size already laid out
    /// was then missed, and names blinked out during a zoom.
    /// </summary>
    internal static double LevelAway(double level, int rungs) =>
        Math.Pow(2, (Math.Round(Math.Log2(level) * LevelsPerOctave) + rungs) / LevelsPerOctave);

    /// <summary>
    /// One layout, from the cache or made now.  Past the frame's allowance
    /// for text (<see cref="MayMakeText"/>) a scaled text takes the nearest
    /// level already made, the way a game streams a texture - the right one
    /// arrives a frame or two later - and one never made at any size waits
    /// its turn.
    /// </summary>
    private ScaledText Layout(string text, double level, double size, double maxWidth, Brush brush, bool bold, bool icon, bool scaled)
    {
        var scale = scaled ? size / level : 1;
        var key = KeyFor(text, level, scale, maxWidth, brush, bold, icon, scaled);
        if (TryCached(key, out var cached))
        {
            return new ScaledText(cached, scale);
        }

        if (!MayMakeText())
        {
            TextWaits(askedOfWorker: AskTextWorker(key, text, level, brush, bold, icon, scaled));
            if (scaled)
            {
                for (var step = 1; step <= 3; step++)
                {
                    foreach (var direction in (ReadOnlySpan<int>)[-1, 1])
                    {
                        var near = LevelAway(level, direction * step);
                        var nearScale = size / near;
                        if (TryCached(KeyFor(text, near, nearScale, maxWidth, brush, bold, icon, scaled: true), out var neighbour))
                        {
                            return new ScaledText(neighbour, nearScale);
                        }
                    }
                }
            }

            return default;
        }

        _textBudget--;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            icon ? IconFace : bold ? TextFaceBold : TextFace,
            level,
            brush,
            _scaleY)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };

        if (key.Width >= 0)
        {
            formatted.MaxTextWidth = Math.Max(1, key.Width * (scaled ? 8 : 6));
        }

        // Laid out here rather than on first being measured, so the time is
        // charged to the allowance that let it be made.
        _ = formatted.Width;
        TextMade(started);
        NewTextLayouts++;
        _textCache[key] = formatted;
        if (_recordingLabels)
        {
            RecordText(formatted);
        }

        return new ScaledText(formatted, scale);
    }

    private static TextKey KeyFor(string text, double level, double scale, double maxWidth, Brush brush, bool bold, bool icon, bool scaled)
    {
        var widthKey = double.IsInfinity(maxWidth) || maxWidth >= 10_000
            ? -1
            : Math.Max(0, (int)Math.Floor(maxWidth / scale / (scaled ? 8 : 6)));
        return new TextKey(text, level, widthKey, brush, bold, icon);
    }

    private bool TryCached(TextKey key, out FormattedText formatted)
    {
        if (_textCache.TryGetValue(key, out formatted!))
        {
            return true;
        }

        if (_oldTextCache.Remove(key, out formatted!))
        {
            _textCache[key] = formatted;
            return true;
        }

        return false;
    }

    private static void DrawTextAt(DrawingContext dc, ScaledText text, Point origin)
    {
        if (text.Text is null)
        {
            return;
        }

        if (Math.Abs(text.Scale - 1) < 1e-9)
        {
            dc.DrawText(text.Text, origin);
            return;
        }

        dc.PushTransform(new MatrixTransform(text.Scale, 0, 0, text.Scale, origin.X * (1 - text.Scale), origin.Y * (1 - text.Scale)));
        dc.DrawText(text.Text, origin);
        dc.Pop();
    }

    /// <summary>
    /// Every laid-out text WPF has drawn in the frame loop, recorded once at
    /// the origin into a frozen drawing that is replayed wherever and at
    /// whatever size the text is drawn.  Drawing a FormattedText itself runs
    /// its line formatter again and makes new glyph runs for WPF's render
    /// thread to realise - about 17 microseconds and 5.6 KB a name, 31 ms and
    /// 10 MB for a folder of 1,800 names in view, every frame the names were
    /// drawn.  Kept as long as the layout is, and no longer.
    /// </summary>
    private readonly ConditionalWeakTable<FormattedText, Drawing> _textDrawings = new();

    /// <summary>
    /// A layout's recording (see <see cref="_textDrawings"/>), made now if
    /// the frame's allowance lets it, or null while it waits.  In the loop a
    /// layout is recorded as it is made, so only one made elsewhere - by a
    /// snapshot, say - waits here, for the next frame.
    /// </summary>
    private Drawing? TextDrawingOf(FormattedText text)
    {
        if (_textDrawings.TryGetValue(text, out var drawing))
        {
            return drawing;
        }

        if (!MayMakeText())
        {
            TextWaits();
            return null;
        }

        return RecordText(text);
    }

    /// <summary>Records a layout at the origin into a frozen drawing, kept with it and charged to the frame's allowance.</summary>
    private Drawing RecordText(FormattedText text)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            context.DrawText(text, new Point(0, 0));
        }

        group.Freeze();
        _textDrawings.AddOrUpdate(text, group);
        TextMade(started);
        return group;
    }

    /// <summary>How the WPF label layer is recorded (see <see cref="WpfLabelTarget"/>).</summary>
    private enum WpfLabelMode
    {
        /// <summary>Every text set and drawn in place, as the canvas always drew it: outside the frame loop.</summary>
        Exact,

        /// <summary>Recorded texts replayed under a transform, straight into the layer: frames in motion.</summary>
        Moving,

        /// <summary>As moving, gathered into runs of labels kept from frame to frame: frames at rest.</summary>
        Kept
    }

    /// <summary>
    /// The names drawn by WPF: laid out by <see cref="Text"/> - its ladder of
    /// sizes, its allowance for new layouts, its two generations of layouts -
    /// and recorded on the label layer, with the icons <see cref="IconLookup"/>
    /// has, each call with the brush the canvas has always drawn it with.  How
    /// it is recorded depends on the frame (<see cref="WpfLabelMode"/>):
    ///
    /// <list type="bullet">
    /// <item><b>Exact</b> - a snapshot, a test: each text is drawn in place,
    /// exactly as the canvas drew its names before the frame loop kept
    /// anything, and a picture of the canvas is the one it always was.</item>
    /// <item><b>Moving</b> - each text's frozen recording
    /// (<see cref="TextDrawingOf"/>) replayed under a transform to its place
    /// and size, straight into the layer.  The glyphs WPF shows are the ones
    /// it has already realised; nothing is formatted again.  Placed by a
    /// transform rather than laid down in place, a glyph can land a rounding
    /// apart - at most one level, in about one pixel of two hundred of a
    /// name's.</item>
    /// <item><b>Kept</b> - as moving, but the labels are gathered into runs
    /// of up to <see cref="LabelsPerRun"/>, one folder's at a time, each
    /// recorded into a visual of its own under the label layer and kept for
    /// the next frame.  A frame at rest draws the same labels with the same
    /// calls - only an arriving icon or a name's text changes one - so a run
    /// whose calls are the ones it has is left as it is, with no work at all,
    /// and only the runs that changed are recorded again: a frame of 1,800
    /// names at rest that an icon asked for records one run of sixteen.  The
    /// layer shows the runs the frame drew, in its order; what the last frame
    /// at rest kept and this one did not draw is let go of.</item>
    /// </list>
    /// </summary>
    private sealed class WpfLabelTarget(NestedCanvas canvas) : LabelTarget
    {
        /// <summary>Labels in one kept run: few enough that one label changing records little again, many enough that the layer holds few visuals.</summary>
        public const int LabelsPerRun = 16;

        private DrawingContext? _dc;
        private WpfLabelMode _mode;

        // The run being gathered: its first label, how many labels it has,
        // and their calls.
        private NestedFolder? _runFolder;
        private int _runIndex;
        private bool _runOfFiles;
        private int _runLabels;
        private readonly List<LabelCall> _calls = [];

        // Runs kept by their first label: those drawn by the frame under way,
        // and those the last frame at rest drew and this one has not reached;
        // and the runs this frame drew, in order, which the layer shows.
        private Dictionary<(NestedFolder Folder, int Index), LabelRun> _runs = [];
        private Dictionary<(NestedFolder Folder, int Index), LabelRun> _oldRuns = [];
        private readonly List<LabelRun> _drawn = [];

        /// <summary>Runs the last frame at rest left as they were, and runs it recorded.</summary>
        public int RunsKept { get; private set; }

        public int RunsRecorded { get; private set; }

        /// <summary>Icons the last frame's labels drew.</summary>
        public int IconsDrawn { get; private set; }

        /// <summary>Whether the label layer shows kept runs.</summary>
        public bool ShowsRuns => canvas._labelVisual.Children.Count > 0;

        /// <summary>
        /// Starts the frame's labels: what is not kept goes to
        /// <paramref name="dc"/>, the layer's own recording, until
        /// <see cref="End"/>.  A frame that keeps nothing takes the kept runs
        /// off the layer, which its recording then holds alone.
        /// </summary>
        public void Begin(DrawingContext dc, WpfLabelMode mode)
        {
            _dc = dc;
            _mode = mode;
            _runLabels = 0;
            _runFolder = null;
            _calls.Clear();
            _drawn.Clear();
            RunsKept = 0;
            RunsRecorded = 0;
            IconsDrawn = 0;
            if (mode == WpfLabelMode.Kept)
            {
                (_oldRuns, _runs) = (_runs, _oldRuns);
                _runs.Clear();
            }
            else if (ShowsRuns)
            {
                canvas._labelVisual.Children.Clear();
            }
        }

        public void End()
        {
            FlushRun();
            if (_mode == WpfLabelMode.Kept)
            {
                ShowDrawnRuns();
            }

            _dc = null;
        }

        /// <summary>
        /// The names are on the GPU's picture now: the kept runs come off the
        /// layer - whose recording the GPU's first present already cleared -
        /// and nothing is kept for WPF's names any more.
        /// </summary>
        public void Hide()
        {
            canvas._labelVisual.Children.Clear();
            _runs.Clear();
            _oldRuns.Clear();
            _drawn.Clear();
        }

        public override LabelText Text(string text, double size, Color ink, double maxWidth, LabelFace face, bool scaled)
        {
            var laidOut = canvas.Text(text, size, BrushOf(ink), maxWidth, bold: face == LabelFace.SemiBold, icon: face == LabelFace.Icons, scaled);
            return laidOut.Text is null ? default : new LabelText(laidOut.Text, laidOut.Width, laidOut.Height, laidOut.Scale);
        }

        public override void DrawText(in LabelText text, Point origin)
        {
            if (text.Handle is not FormattedText formatted)
            {
                return;
            }

            if (_mode == WpfLabelMode.Exact)
            {
                DrawTextAt(_dc!, new ScaledText(formatted, text.Scale), origin);
                return;
            }

            Add(new LabelCall(LabelCallKind.Text, formatted, origin.X, origin.Y, 0, 0, text.Scale));
        }

        public override void FillRect(Rect bounds, Color colour) =>
            Add(new LabelCall(LabelCallKind.Rectangle, BrushOf(colour), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0));

        public override void FillRounded(Rect bounds, double radius, Color colour) =>
            Add(new LabelCall(LabelCallKind.Rounded, BrushOf(colour), bounds.X, bounds.Y, bounds.Width, bounds.Height, radius));

        public override bool DrawIcon(NestedFolder folder, int fileIndex, in NestedFile file, Rect bounds)
        {
            if (canvas.IconLookup?.Invoke(folder, fileIndex) is not { } icon)
            {
                return false;
            }

            Add(new LabelCall(LabelCallKind.Image, icon, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0));
            IconsDrawn++;
            return true;
        }

        public override void BeginLabel(NestedFolder folder, int fileIndex)
        {
            if (_mode != WpfLabelMode.Kept)
            {
                return;
            }

            // A run is one folder's files in a row, or titles in a row, and
            // never more than LabelsPerRun of them.
            var ofFiles = fileIndex >= 0;
            if (_runLabels > 0 && (ofFiles != _runOfFiles || ofFiles && !ReferenceEquals(folder, _runFolder) || _runLabels >= LabelsPerRun))
            {
                FlushRun();
            }

            if (_runLabels == 0)
            {
                _runFolder = folder;
                _runIndex = fileIndex;
                _runOfFiles = ofFiles;
            }

            _runLabels++;
        }

        /// <summary>
        /// A call of the label under way: gathered into the run when the
        /// labels are kept, drawn at once otherwise - and at once too for a
        /// call made outside any label, which has no run to belong to.
        /// </summary>
        private void Add(in LabelCall call)
        {
            if (_mode == WpfLabelMode.Kept && _runLabels > 0)
            {
                _calls.Add(call);
                return;
            }

            Replay(_dc!, call);
        }

        /// <summary>
        /// The run gathered so far: left as the last frame recorded it when
        /// its calls are the same and every text of it was drawn - or none
        /// that was missing can be drawn yet - and recorded again otherwise.
        /// A text whose recording has to wait for a later frame's allowance is
        /// left out of the run until it has one.
        /// </summary>
        private void FlushRun()
        {
            if (_runLabels == 0)
            {
                return;
            }

            var key = (_runFolder!, _runIndex);
            _runLabels = 0;
            _runFolder = null;
            if (!_runs.TryGetValue(key, out var run) && _oldRuns.Remove(key, out run))
            {
                _runs[key] = run;
            }

            var calls = CollectionsMarshal.AsSpan(_calls);
            if (run is not null && run.Holds(calls) && !run.CanDrawMore(canvas))
            {
                RunsKept++;
            }
            else
            {
                if (run is null)
                {
                    run = new LabelRun();
                    _runs[key] = run;
                }

                run.Record(calls, canvas);
                RunsRecorded++;
            }

            _drawn.Add(run);
            _calls.Clear();
        }

        /// <summary>
        /// The layer shows the runs this frame drew, in the order it drew
        /// them; when that is what it already shows, as at rest it nearly
        /// always is, nothing is touched.
        /// </summary>
        private void ShowDrawnRuns()
        {
            var shown = canvas._labelVisual.Children;
            if (shown.Count == _drawn.Count)
            {
                var same = true;
                for (var index = 0; index < _drawn.Count && same; index++)
                {
                    same = ReferenceEquals(shown[index], _drawn[index].Visual);
                }

                if (same)
                {
                    return;
                }
            }

            shown.Clear();
            foreach (var run in _drawn)
            {
                shown.Add(run.Visual);
            }
        }

        /// <summary>
        /// One call drawn onto <paramref name="dc"/>: a text by its recording,
        /// put where the call puts it at its size, and left out while it has
        /// none; anything else as itself.  True unless it was a text left out.
        /// </summary>
        private bool Replay(DrawingContext dc, in LabelCall call)
        {
            switch (call.Kind)
            {
                case LabelCallKind.Text:
                    if (canvas.TextDrawingOf((FormattedText)call.Handle) is not { } text)
                    {
                        return false;
                    }

                    dc.PushTransform(new MatrixTransform(call.Scale, 0, 0, call.Scale, call.X, call.Y));
                    dc.DrawDrawing(text);
                    dc.Pop();
                    break;
                case LabelCallKind.Image:
                    dc.DrawImage((ImageSource)call.Handle, new Rect(call.X, call.Y, call.Width, call.Height));
                    break;
                case LabelCallKind.Rectangle:
                    dc.DrawRectangle((Brush)call.Handle, null, new Rect(call.X, call.Y, call.Width, call.Height));
                    break;
                case LabelCallKind.Rounded:
                    dc.DrawRoundedRectangle((Brush)call.Handle, null, new Rect(call.X, call.Y, call.Width, call.Height), call.Scale, call.Scale);
                    break;
            }

            return true;
        }

        /// <summary>
        /// The brush a colour was always drawn with: the canvas's own for its
        /// own colours, one shared frozen brush for any other.  The same brush
        /// is also the same key in the layouts kept, so a name is found laid
        /// out exactly when it was before.
        /// </summary>
        private Brush BrushOf(Color colour) =>
            colour == TextColour ? TextBrush
            : colour == TextDimColour ? TextDimBrush
            : colour == DangerColour ? DangerBrush
            : colour == StarColour ? StarBrush
            : colour == FolderColour ? FolderBrush
            : colour == PillColour ? PillBrush
            : canvas.BrushFor(colour);

        /// <summary>
        /// A run of labels as a frame at rest recorded it: its calls, into a
        /// visual of its own, and how many of its texts it could draw.
        /// </summary>
        private sealed class LabelRun
        {
            private LabelCall[] _calls = [];
            private int _texts;
            private int _textsDrawn;

            public DrawingVisual Visual { get; } = new();

            public bool Holds(ReadOnlySpan<LabelCall> calls)
            {
                if (calls.Length != _calls.Length)
                {
                    return false;
                }

                for (var index = 0; index < calls.Length; index++)
                {
                    if (!_calls[index].Same(calls[index]))
                    {
                        return false;
                    }
                }

                return true;
            }

            /// <summary>
            /// Whether recording the run again would draw more of it: some of
            /// its texts were left out, and one of those now has a recording,
            /// or the frame may still make one.
            /// </summary>
            public bool CanDrawMore(NestedCanvas canvas)
            {
                if (_textsDrawn == _texts)
                {
                    return false;
                }

                if (canvas.MayMakeText())
                {
                    return true;
                }

                var drawable = 0;
                foreach (var call in _calls)
                {
                    if (call.Kind == LabelCallKind.Text && canvas._textDrawings.TryGetValue((FormattedText)call.Handle, out _))
                    {
                        drawable++;
                    }
                }

                return drawable > _textsDrawn;
            }

            public void Record(ReadOnlySpan<LabelCall> calls, NestedCanvas canvas)
            {
                if (_calls.Length != calls.Length)
                {
                    _calls = new LabelCall[calls.Length];
                }

                calls.CopyTo(_calls);
                _texts = 0;
                _textsDrawn = 0;
                using var dc = Visual.RenderOpen();
                foreach (var call in _calls)
                {
                    var isText = call.Kind == LabelCallKind.Text;
                    _texts += isText ? 1 : 0;
                    if (canvas._wpfLabels.Replay(dc, call) && isText)
                    {
                        _textsDrawn++;
                    }
                }
            }
        }
    }

    private enum LabelCallKind : byte
    {
        Text,
        Image,
        Rectangle,
        Rounded
    }

    /// <summary>
    /// One drawing call of a label, as a kept run compares it with the next
    /// frame's: what to draw - a layout, an icon, a brush - by which object
    /// it is, where, and at what size; for a text, <see cref="Scale"/> is its
    /// scale, for a rounded rectangle its radius.
    /// </summary>
    private readonly struct LabelCall(LabelCallKind kind, object handle, double x, double y, double width, double height, double scale)
    {
        public LabelCallKind Kind { get; } = kind;

        public object Handle { get; } = handle;

        public double X { get; } = x;

        public double Y { get; } = y;

        public double Width { get; } = width;

        public double Height { get; } = height;

        public double Scale { get; } = scale;

        /// <summary>The same call: field by field, the objects by reference, and without boxing anything.</summary>
        public bool Same(in LabelCall other) =>
            Kind == other.Kind
            && ReferenceEquals(Handle, other.Handle)
            && X == other.X
            && Y == other.Y
            && Width == other.Width
            && Height == other.Height
            && Scale == other.Scale;
    }

    /// <summary>
    /// The file labels laid out and recorded, but not drawn: what
    /// <see cref="LayOutFileNamesFromCentre"/> walks the labels nearest the
    /// middle with, making every text they will want - within the frame's
    /// allowance - in the order they come in, so the frame's drawing then
    /// finds them made.  An icon counts as drawn when the window has it, as
    /// it does when the labels are drawn, so each name is laid out for the
    /// room it will have.
    /// </summary>
    private sealed class LayoutOnlyTarget(NestedCanvas canvas, WpfLabelTarget names) : LabelTarget
    {
        public override LabelText Text(string text, double size, Color ink, double maxWidth, LabelFace face, bool scaled) =>
            names.Text(text, size, ink, maxWidth, face, scaled);

        public override void DrawText(in LabelText text, Point origin)
        {
            if (text.Handle is FormattedText formatted)
            {
                canvas.TextDrawingOf(formatted);
            }
        }

        public override void FillRect(Rect bounds, Color colour)
        {
        }

        public override void FillRounded(Rect bounds, double radius, Color colour)
        {
        }

        public override bool DrawIcon(NestedFolder folder, int fileIndex, in NestedFile file, Rect bounds) =>
            canvas.IconLookup?.Invoke(folder, fileIndex) is not null;
    }

    /// <summary>
    /// Strings made from numbers - a size, a pair of counts - kept so that
    /// drawing the same one every frame does not make it every frame.  Two
    /// generations, like the text layouts: what is still in use is carried
    /// over, the rest goes, so it stays the size of what is on screen.  The
    /// numbers are written in the culture of the moment, and a change of
    /// culture drops everything kept.
    /// </summary>
    private sealed class NumberTexts(int capacity)
    {
        private Dictionary<long, string> _current = [];
        private Dictionary<long, string> _old = [];
        private CultureInfo? _culture;

        public bool TryGet(long key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? text)
        {
            if (!ReferenceEquals(_culture, CultureInfo.CurrentCulture))
            {
                _culture = CultureInfo.CurrentCulture;
                _current.Clear();
                _old.Clear();
                text = null;
                return false;
            }

            if (_current.TryGetValue(key, out text))
            {
                return true;
            }

            if (_old.Remove(key, out text))
            {
                _current[key] = text;
                return true;
            }

            return false;
        }

        public string Add(long key, string text)
        {
            if (_current.Count >= capacity)
            {
                (_old, _current) = (_current, _old);
                _current.Clear();
            }

            _current[key] = text;
            return text;
        }
    }

    /// <summary>
    /// Pairs of strings told apart by which strings they are rather than by
    /// what they say: a file's facts are kept by its folder's path and its
    /// name, both strings that live as long as the folder's listing, and
    /// comparing those is two pointers where comparing text is two scans.
    /// </summary>
    private sealed class ReferenceKeyComparer : IEqualityComparer<(string Folder, string Name)>
    {
        public static readonly ReferenceKeyComparer Instance = new();

        public bool Equals((string Folder, string Name) x, (string Folder, string Name) y) =>
            ReferenceEquals(x.Folder, y.Folder) && ReferenceEquals(x.Name, y.Name);

        public int GetHashCode((string Folder, string Name) key) =>
            HashCode.Combine(
                RuntimeHelpers.GetHashCode(key.Folder),
                RuntimeHelpers.GetHashCode(key.Name));
    }

    // ---- for tests -------------------------------------------------------------

    /// <summary>For tests: the file names the last walk listed for the label layer.</summary>
    internal int FileLabelCount => _fileLabels.Count;

    /// <summary>For tests: the folder titles and pills the last walk listed for the label layer.</summary>
    internal int FolderLabelCount => _labels.Count;

    /// <summary>For tests: runs of labels the last WPF label layer drew from what it kept, and runs it recorded.</summary>
    internal int LabelRunsKept => _wpfLabels.RunsKept;

    internal int LabelRunsRecorded => _wpfLabels.RunsRecorded;

    /// <summary>For tests: names asked of the text worker and not taken in yet, and names it has made that wait to be.</summary>
    internal int TextsAskedOfWorker => _textsAsked.Count;

    internal int TextsPreparedWaiting => _textAsks?.Results.Count ?? 0;

    /// <summary>For tests: texts the last WPF label layer laid out or recorded, and whether it left any waiting for the next.</summary>
    internal int LabelTextsMade => _textsMade;

    internal bool LabelsLeftWaiting => _labelsLeftWaiting;

    /// <summary>For tests: the last WPF label layer was drawn while the camera was still moving, rather than after it stopped.</summary>
    internal bool LabelsDrawnWhileMoving { get; private set; }

    /// <summary>
    /// For tests: each file label of the last walk - its place in the list,
    /// its name, how far its middle is from the view's, in DIPs - and whether
    /// its name is laid out at the size it is drawn at.
    /// </summary>
    internal List<(int Index, string Name, double Distance, bool Made)> FileNameStatesForTests()
    {
        var laidOut = new HashSet<(string, double)>();
        foreach (var key in _textCache.Keys.Concat(_oldTextCache.Keys))
        {
            laidOut.Add((key.Text, key.Size));
        }

        var states = new List<(int, string, double, bool)>(_fileLabels.Count);
        for (var index = 0; index < _fileLabels.Count; index++)
        {
            var job = _fileLabels[index];
            var name = job.Folder.Files[job.Index].Name;
            var size = Math.Clamp(job.H * 0.5, 7.5, 13);
            var level = LevelFor(size);
            var dx = job.X + job.W / 2 - _viewWidth / 2;
            var dy = job.Y + job.H / 2 - _viewHeight / 2;
            states.Add((index, name, Math.Sqrt(dx * dx + dy * dy), laidOut.Contains((name, level))));
        }

        return states;
    }

    /// <summary>For tests: the height of the file tiles the last walk labelled, in DIPs (0 without any).</summary>
    internal double FileLabelHeightForTests => _fileLabels.Count > 0 ? _fileLabels[0].H : 0;

    /// <summary>For tests: icons the last WPF label layer drew.</summary>
    internal int LabelIconsDrawn => _wpfLabels.IconsDrawn;

    /// <summary>
    /// For tests: a frame that draws the names and nothing else, as the loop
    /// draws one when only they went out of date - an icon arrived, say - or,
    /// with <paramref name="asInFrameLoop"/> false, as a snapshot draws them;
    /// <paramref name="withScene"/> draws the cells first, walking the view
    /// again, as a frame of the loop that the camera or the tree moved does.
    /// <see cref="LastAllocations"/> then says what it cost the collector.
    /// </summary>
    internal void RenderLabelsForTests(bool inMotion, bool asInFrameLoop = true, bool withScene = false)
    {
        _inFrameLoop = asInFrameLoop;
        try
        {
            if (asInFrameLoop)
            {
                TakePreparedTexts();
            }

            RenderLayers(withScene ? Layers.All : Layers.Labels, inMotion);
        }
        finally
        {
            _inFrameLoop = false;
        }
    }

    /// <summary>
    /// For tests: the calls the last walk's labels make, into a target that
    /// only notes them - each text with its face, the icons asked for and the
    /// rectangles filled.  The same calls reach WPF and the graphics card, so
    /// what a layer leaves out can be read off them for both.
    /// </summary>
    internal (List<(string Text, LabelFace Face)> Texts, int Icons, int Rectangles) RecordLabelCallsForTests()
    {
        var recorder = new RecordingTarget();
        DrawLabelLayer(recorder);
        return (recorder.Texts, recorder.Icons, recorder.Rectangles);
    }

    /// <summary>A target that draws nothing and notes every call, with every text a plain width per character and every icon found.</summary>
    private sealed class RecordingTarget : LabelTarget
    {
        public List<(string Text, LabelFace Face)> Texts { get; } = [];

        public int Icons { get; private set; }

        public int Rectangles { get; private set; }

        public override LabelText Text(string text, double size, Color ink, double maxWidth, LabelFace face, bool scaled) =>
            new(text, Math.Min(maxWidth, text.Length * size * 0.5), size * 1.3, Count: (int)face);

        public override void DrawText(in LabelText text, Point origin)
        {
            if (text.Handle is string written)
            {
                Texts.Add((written, (LabelFace)text.Count));
            }
        }

        public override void FillRect(Rect bounds, Color colour) => Rectangles++;

        public override void FillRounded(Rect bounds, double radius, Color colour)
        {
        }

        public override bool DrawIcon(NestedFolder folder, int fileIndex, in NestedFile file, Rect bounds)
        {
            Icons++;
            return true;
        }
    }

    /// <summary>A laid-out text and the scale it is drawn at.</summary>
    private readonly record struct ScaledText(FormattedText? Text, double Scale)
    {
        public double Width => Text is null ? 0 : Text.Width * Scale;

        public double Height => Text is null ? 0 : Text.Height * Scale;
    }

    private enum LabelMode
    {
        None,
        Header,
        Pill
    }

    private readonly record struct LabelJob(NestedFolder Folder, double X, double Y, double W, LabelMode Mode);

    private readonly record struct FileLabelJob(NestedFolder Folder, int Index, double X, double Y, double W, double H);

    private readonly record struct TextKey(string Text, double Size, int Width, Brush Brush, bool Bold, bool Icon);

    /// <summary>
    /// A folder's name drawn this frame, by which the folder can be grabbed:
    /// a value in a list rebuilt with every label layer, so drawing the names
    /// hands the garbage collector nothing.
    /// </summary>
    private readonly record struct LabelGrab(Rect Bounds, NestedFolder Folder);
}
