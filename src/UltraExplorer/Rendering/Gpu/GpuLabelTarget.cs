using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using UltraExplorer.Controls;
using UltraExplorer.Models;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// The names on the canvas as GPU instances: every call the canvas makes to
/// draw its labels becomes rectangles, icons and glyphs in the frame's label
/// lists, drawn by <see cref="NestedGpuRenderer"/> over the scene in the same
/// present - so the names never lag the cells, and WPF lays out, realises and
/// draws no text at all.
///
/// <para><b>Text.</b> A name is shaped once, in em units, by the canvas's
/// <see cref="TextShaper"/> - when first drawn, or ahead on its worker for
/// scripts the UI font lacks - and drawn at
/// whatever size the zoom asks by multiplying: each glyph is a quad over its
/// distance field in the <see cref="GlyphAtlas"/>.  Width, height and the
/// point where a long name is cut are the ones WPF's FormattedText gives -
/// the same fonts, the same kerning, CharacterEllipsis at the same character,
/// with the actual available width at every scale. The shaped run stays
/// cached in em units; changing the camera never reshapes it. A name the
/// shaper has not finished - a
/// script the UI font lacks, or one past the frame's budget of new names
/// while the camera moves (<see cref="MotionShapeBudget"/>) - is left out
/// for the frame, as a name WPF had no layout budget for was, and drawn when
/// the worker hands it back.</para>
///
/// <para><b>Icons.</b> One slice of the process's <see cref="IconAtlas"/> per
/// file type (a program's or shortcut's own where it has one), sampled
/// through its mips.  A type whose icon is not known yet is drawn without it,
/// retaining its reserved slot, and drawn with it when it arrives without
/// moving the filename.</para>
///
/// <para><b>Motion.</b> While the camera moves, text and icons sit at their
/// exact sub-pixel places and glide.  At rest (<c>snap</c>) the baseline and
/// the icons' corners are rounded to whole device pixels, so a still picture
/// is as crisp as the fields allow; the run keeps its exact sub-pixel place
/// along the line, as WPF's text does.  (The design rounded the run's origin
/// too; held against WPF's names in the text gate, keeping WPF's horizontal
/// placement was the largest single gain, so the atlas's own snapping,
/// <see cref="GlyphAtlas.Emit{TSink}"/>'s <c>snap</c>, is not used.)</para>
///
/// <para>A frame is <see cref="Begin"/>, the canvas's label calls, then
/// <see cref="End"/>, on the UI thread.  Nothing in between allocates once
/// every name on screen has been shaped.</para>
/// </summary>
internal sealed class GpuLabelTarget : LabelTarget
{
    /// <summary>Guard band past the view (pixels) inside which labels are still emitted; past it they cannot be seen.</summary>
    private const double CullMargin = 64;

    /// <summary>
    /// New names shaped on the UI thread in one frame while the camera moves
    /// - a third of a millisecond or so; the rest go to the shaper's worker
    /// and are drawn a frame or two later, the way the WPF path spreads new
    /// layouts over frames.  At rest every name is shaped at once.
    /// </summary>
    public const int MotionShapeBudget = 64;

    private readonly FaceRegistry _faces;
    private readonly TextShaper _shaper;
    private readonly GlyphAtlas _glyphs;
    private readonly IconAtlas? _icons;
    private readonly Func<GpuDeviceSet, IconAtlasTexture>? _makeIconTexture;
    private readonly Func<GpuDeviceSet, GlyphAtlasTexture> _makeGlyphTexture;
    private NestedGpuFrame? _frame;
    private GlyphListSink _sink;
    private GpuDeviceSet? _devices;
    private IconAtlasTexture? _iconTexture;
    private GlyphAtlasTexture? _glyphTexture;
    private double _scaleX = 1;
    private double _scaleY = 1;
    private double _viewWidth;
    private double _viewHeight;
    private bool _snap;
    private int _shapeBudget = int.MaxValue;
    private long _uploadTicks;

    public GpuLabelTarget(FaceRegistry faces, TextShaper shaper, GlyphAtlas glyphs, IconAtlas? icons)
    {
        _faces = faces;
        _shaper = shaper;
        _glyphs = glyphs;
        _icons = icons;
        _makeIconTexture = icons is null ? null : set => icons.CreateTexture(set.Device);
        _makeGlyphTexture = set => new GlyphAtlasTexture(set.Device, glyphs);
    }

    /// <summary>The shaper the names are looked up in; its owner thread is the one calling this target.</summary>
    public TextShaper Shaper => _shaper;

    /// <summary>Names left out of the last frame because their shaping was not finished.</summary>
    public int TextsPending { get; private set; }

    /// <summary>Glyphs left out of the last frame because the atlas had not made them yet.</summary>
    public int GlyphsPending { get; private set; }

    /// <summary>Texts, icons and rectangles the last frame emitted.</summary>
    public int TextsDrawn { get; private set; }

    public int IconsDrawn { get; private set; }

    /// <summary>How long the last frame spent bringing the atlases' textures up to date: icons taken in, glyphs copied.</summary>
    public double LastUploadMilliseconds => _uploadTicks * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// True when the atlases have more waiting than one frame took in, so
    /// the caller should draw the labels again next frame.  (Everything that
    /// arrives later says so itself through the atlases' events.)
    /// </summary>
    public bool WantsAnotherFrame { get; private set; }

    /// <summary>
    /// Starts a frame's labels: the atlases' textures on <paramref name="devices"/>
    /// (made there the first time, from the atlases' copies in memory), the
    /// icons that arrived taken in and uploaded, and the frame's three label
    /// lists emptied.  <paramref name="viewWidth"/> and
    /// <paramref name="viewHeight"/> are the view in device pixels, outside
    /// which nothing is emitted.
    /// </summary>
    public void Begin(NestedGpuFrame frame, GpuDeviceSet devices, int viewWidth, int viewHeight, double scaleX, double scaleY, bool snap)
    {
        EnsureTextures(devices);
        var started = Stopwatch.GetTimestamp();
        _icons?.ProcessArrivals(_iconTexture, devices.Context);
        _uploadTicks = Stopwatch.GetTimestamp() - started;

        // Emptied as a new version of the lists, views let go: should the
        // frame fail before End, the renderer uploads what was filled and
        // samples no atlas, rather than draw the last upload's instances or
        // views that may be another card's.
        frame.ClearLabels();
        _frame = frame;
        _sink = new GlyphListSink(frame.Glyphs);
        _scaleX = scaleX;
        _scaleY = scaleY;
        _viewWidth = viewWidth;
        _viewHeight = viewHeight;
        _snap = snap;
        _shapeBudget = snap ? int.MaxValue : MotionShapeBudget;
        TextsPending = 0;
        GlyphsPending = 0;
        TextsDrawn = 0;
        IconsDrawn = 0;
    }

    /// <summary>
    /// Ends the frame's labels: the glyphs placed since the last frame copied
    /// to the card, the atlases' views handed to the frame, and the label
    /// lists marked new for the renderer to upload.
    /// </summary>
    public void End()
    {
        var frame = _frame ?? throw new InvalidOperationException("End without Begin.");
        var started = Stopwatch.GetTimestamp();
        if (_glyphTexture is { } glyphTexture && _devices is { } devices)
        {
            // A burst of new glyphs is copied in bounded steps; every glyph
            // emitted this frame was placed before the first of them.
            for (var round = 0; round < 8 && glyphTexture.Upload(devices.Context) == GlyphAtlasTexture.UploadBudget; round++)
            {
            }
        }

        _uploadTicks += Stopwatch.GetTimestamp() - started;
        _shaper.PostDeferred();
        frame.IconView = _iconTexture is { IsDisposed: false } icons ? icons.View : null;
        frame.GlyphView = _glyphTexture?.View;
        frame.LabelDevices = _devices;
        frame.LabelsChanged();
        WantsAnotherFrame = _icons is { HasPendingArrivals: true };
        _frame = null;
        _sink = default;
    }

    /// <summary>
    /// Whether <paramref name="frame"/>'s names were drawn by this target on
    /// its card with atlas views that are no longer the textures' own: the
    /// glyph and icon arrays are shared by every canvas on the card, and when
    /// another one's frame grew one, the old view was let go.  A frame that
    /// presented only its scene with it would bind nothing in its place, and
    /// every name and icon would vanish; the names are drawn again first,
    /// which hands the frame the views there are now.
    /// </summary>
    public bool ViewsOutOfDate(NestedGpuFrame frame) =>
        _devices is not null
        && ReferenceEquals(frame.LabelDevices, _devices)
        && (_glyphTexture is { } glyphs && frame.GlyphView is not null && !ReferenceEquals(frame.GlyphView, glyphs.View)
            || _iconTexture is { } icons && frame.IconView is not null && !ReferenceEquals(frame.IconView, icons.View));

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override LabelText Text(string text, double size, Color ink, double maxWidth, LabelFace face, bool scaled)
    {
        size = Math.Clamp(size, 1, 400);
        var faceId = (byte)face;
        var shapedBefore = _shaper.DirectShapes;
        if (!_shaper.TryGet(text, faceId, shapeHere: _shapeBudget > 0, out var shaped))
        {
            TextsPending++;
            return default;
        }

        if (_shaper.DirectShapes != shapedBefore)
        {
            _shapeBudget--;
        }

        var natural = shaped.Width * size;
        var cut = !(maxWidth < 10_000) || natural <= maxWidth
            ? shaped.Whole
            : shaped.Trim((float)(maxWidth / size));
        return new LabelText(
            shaped,
            cut.Width * size,
            _faces[faceId].LineHeight * size,
            1,
            size,
            cut.Visible << 1 | (cut.Ellipsis ? 1 : 0),
            (uint)ink.A << 24 | (uint)ink.R << 16 | (uint)ink.G << 8 | ink.B);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override void DrawText(in LabelText text, Point origin)
    {
        if (text.Handle is not ShapedText shaped || _frame is null || text.Count == 0)
        {
            return;
        }

        var left = origin.X * _scaleX;
        var top = origin.Y * _scaleY;
        if (left > _viewWidth + CullMargin
            || top > _viewHeight + CullMargin
            || left + text.Width * _scaleX < -CullMargin
            || top + text.Height * _scaleY < -CullMargin)
        {
            return;
        }

        var size = text.Size;
        var fontPx = size * _scaleY;
        var visible = text.Count >> 1;
        var cut = new TextCut(visible, visible, (text.Count & 1) != 0, (float)(text.Width / size));
        // At rest the baseline sits on a pixel row, as WPF puts it; the run
        // keeps its exact place along the line, as WPF's does.
        var baseline = top + _faces[shaped.Face].Baseline * fontPx;
        if (_snap)
        {
            baseline = Math.Round(baseline);
        }

        GlyphsPending += _glyphs.Emit(ref _sink, shaped, cut, left, baseline, fontPx, text.Ink, GpuTextTuning.BiasFor(fontPx), snap: false);
        TextsDrawn++;
    }

    public override void FillRect(Rect bounds, Color colour) => AddRounded(bounds, 0, colour);

    public override void FillRounded(Rect bounds, double radius, Color colour) => AddRounded(bounds, radius, colour);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override bool DrawIcon(NestedFolder folder, int fileIndex, in NestedFile file, Rect bounds)
    {
        if (_icons is null || _frame is null || _iconTexture is null)
        {
            return false;
        }

        // The folder's path and the name apart: no path is made for a file,
        // and none at all for a type whose icon is the same for every file.
        var slot = _icons.SlotFor(folder.FullPath, file.Name, file.Extension);
        if (slot < 0)
        {
            return false;
        }

        double left = bounds.X * _scaleX, top = bounds.Y * _scaleY;
        double width = bounds.Width * _scaleX, height = bounds.Height * _scaleY;
        if (_snap)
        {
            left = Math.Round(left);
            top = Math.Round(top);
            width = Math.Max(1, Math.Round(width));
            height = Math.Max(1, Math.Round(height));
        }

        // Culled or not, the icon counts as drawn: the name beside it is
        // placed as if it were there, as it always is.
        if (left <= _viewWidth + CullMargin && top <= _viewHeight + CullMargin && left + width >= -CullMargin && top + height >= -CullMargin)
        {
            ref var icon = ref _frame.Icons.Add();
            icon.Rect = new Vector4((float)left, (float)top, (float)(left + width), (float)(top + height));
            icon.Slot = (uint)slot;
            icon.Tint = 0xFFFFFFFF;
            IconsDrawn++;
        }

        return true;
    }

    /// <summary>
    /// A frame's worth of every call, into a throw-away frame on no device:
    /// for the warm-up thread, so the methods a frame runs are compiled -
    /// fully optimised, for those marked so - before the first frame the
    /// user sees needs them.  Glyphs the sample text lacks are asked for.
    /// </summary>
    public void WarmUp()
    {
        using var frame = new NestedGpuFrame(16, 64, 256);
        _frame = frame;
        _sink = new GlyphListSink(frame.Glyphs);
        _scaleX = _scaleY = 1.25;
        _viewWidth = _viewHeight = 512;
        foreach (var snap in (ReadOnlySpan<bool>)[false, true])
        {
            _snap = snap;
            var name = Text("Program Files (x86)", 12.5, Colors.White, 60, LabelFace.Regular, scaled: true);
            DrawText(name, new Point(20.25, 30.5));
            var bold = Text("Local Disk (C:)", 13, Colors.White, double.PositiveInfinity, LabelFace.SemiBold, scaled: true);
            DrawText(bold, new Point(20.25, 60.5));
            FillRounded(new Rect(10.5, 80.25, 60, 14), 3, Color.FromArgb(0xD8, 0x14, 0x16, 0x18));
            FillRect(new Rect(10, 100, 2, 12), Colors.Gold);
        }

        _frame = null;
        _sink = default;
    }

    private void AddRounded(Rect bounds, double radius, Color colour)
    {
        if (_frame is null || bounds.IsEmpty)
        {
            return;
        }

        var left = bounds.X * _scaleX;
        var top = bounds.Y * _scaleY;
        var right = bounds.Right * _scaleX;
        var bottom = bounds.Bottom * _scaleY;
        if (right <= left || bottom <= top || left > _viewWidth + CullMargin || top > _viewHeight + CullMargin || right < -CullMargin || bottom < -CullMargin)
        {
            return;
        }

        ref var rect = ref _frame.LabelRects.Add();
        rect.Outer = new Vector4(
            (float)Math.Max(left, -GpuSink.GuardBand),
            (float)Math.Max(top, -GpuSink.GuardBand),
            (float)Math.Min(right, _viewWidth + GpuSink.GuardBand),
            (float)Math.Min(bottom, _viewHeight + GpuSink.GuardBand));
        rect.Feature = default;
        rect.HeaderBottom = 0;
        rect.Radius = (float)Math.Min(radius * _scaleY, Math.Min(right - left, bottom - top) / 2);
        rect.InnerRadii = 0;
        rect.Kind = (uint)RectKind.Rounded;
        rect.Body = (uint)colour.A << 24 | (uint)colour.R << 16 | (uint)colour.G << 8 | colour.B;
        rect.Rim = 0;
        rect.Header = 0;
        rect.Accent = 0;
    }

    private void EnsureTextures(GpuDeviceSet devices)
    {
        if (ReferenceEquals(devices, _devices) && _iconTexture is not { IsDisposed: true })
        {
            return;
        }

        _devices = devices;
        _iconTexture = _makeIconTexture is null ? null : devices.Attach(_makeIconTexture);
        _glyphTexture = devices.Attach(_makeGlyphTexture);
    }

    /// <summary>Glyph quads straight into the frame's list: a struct, so the atlas's emission is specialised for it.</summary>
    private readonly struct GlyphListSink(InstanceList<GlyphQuad> list) : IGlyphSink
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(in GlyphQuad quad) => list.Add() = quad;
    }
}

/// <summary>
/// The few numbers that decide how the GPU's text weighs against WPF's, set
/// by comparing the two on the snapshot scenes (the text gate): the display
/// gamma DirectWrite's alpha correction is made for, its grayscale enhanced
/// contrast, and how strongly small text is thickened.  For a tuning run,
/// <c>ULTRAEXPLORER_TEXT_TUNING=gamma=1.8;contrast=1;bias=1</c> overrides
/// them for the process.
/// </summary>
internal static class GpuTextTuning
{
    public const string Variable = "ULTRAEXPLORER_TEXT_TUNING";

    // Alpha correction coefficients per display gamma, 1.0 to 2.2 in tenths:
    // DirectWrite's own table, as lhecker/dwrite-hlsl gives it (each /4).
    private static readonly float[] GammaTable =
    [
        0.0000f, 0.0000f, 0.0000f, 0.0000f,
        0.0166f, -0.0807f, 0.2227f, -0.0751f,
        0.0350f, -0.1760f, 0.4325f, -0.1370f,
        0.0543f, -0.2821f, 0.6302f, -0.1876f,
        0.0739f, -0.3963f, 0.8167f, -0.2287f,
        0.0933f, -0.5161f, 0.9926f, -0.2616f,
        0.1121f, -0.6395f, 1.1588f, -0.2877f,
        0.1300f, -0.7649f, 1.3159f, -0.3080f,
        0.1469f, -0.8911f, 1.4644f, -0.3234f,
        0.1627f, -1.0170f, 1.6051f, -0.3347f,
        0.1773f, -1.1420f, 1.7385f, -0.3426f,
        0.1908f, -1.2652f, 1.8650f, -0.3476f,
        0.2031f, -1.3864f, 1.9851f, -0.3501f
    ];

    static GpuTextTuning()
    {
        Gamma = DefaultGamma;
        Contrast = DefaultContrast;
        BiasScale = DefaultBiasScale;
        Sharpness = DefaultSharpness;
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } text)
        {
            return;
        }

        foreach (var part in text.Split(';', ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !float.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            switch (pair[0].ToLowerInvariant())
            {
                case "gamma":
                    Gamma = Math.Clamp(value, 1, 2.2f);
                    break;
                case "contrast":
                    Contrast = Math.Clamp(value, 0, 10);
                    break;
                case "bias":
                    BiasScale = Math.Clamp(value, 0, 10);
                    break;
                case "sharpness":
                    Sharpness = Math.Clamp(value, 0.5f, 4);
                    break;
            }
        }
    }

    // Set by the text gate: the snapshot scenes (--nested-snapshots) drawn
    // both ways at 100 % and at an emulated 150 % (--bench-scale 1.5), and
    // fourteen crops of names from 9 to 30 pixels - file names, sizes,
    // counts, pills, titles - held against WPF's.  With these the GPU's names
    // carry the same ink as WPF's (within 1 % at 100 %, 2.5 % at 150 %), as
    // many solid pixels, and edges as steep; DirectWrite's own defaults (1.8,
    // 1, and no sharpening or extra bias) left light names a little soft and
    // dim ones a little heavy.  Small names were not soft enough after this
    // to need exact-size bitmap glyphs at rest.
    public const float DefaultGamma = 2.2f;
    public const float DefaultContrast = 0.5f;
    public const float DefaultBiasScale = 1.5f;
    public const float DefaultSharpness = 1.3f;

    /// <summary>The display gamma the alpha correction is for.</summary>
    public static float Gamma { get; }

    /// <summary>DirectWrite's grayscale enhanced contrast; light text on the dark canvas is spared most of it.</summary>
    public static float Contrast { get; }

    /// <summary>How much of <see cref="GlyphAtlas.BiasFor"/> small text is thickened by.</summary>
    public static float BiasScale { get; }

    /// <summary>How many times steeper than a pixel's width a glyph's edge goes from clear to solid.</summary>
    public static float Sharpness { get; }

    /// <summary>The threshold bias for text drawn at <paramref name="fontPx"/> device pixels.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float BiasFor(double fontPx) => GlyphAtlas.BiasFor(fontPx) * BiasScale;

    /// <summary>DirectWrite's alpha correction coefficients for a display gamma, between the table's tenths by straight lines.</summary>
    public static Vector4 GammaRatios(float gamma)
    {
        var position = Math.Clamp((gamma - 1f) * 10f, 0f, 12f);
        var low = (int)MathF.Floor(position);
        var high = Math.Min(12, low + 1);
        var t = position - low;
        Vector4 At(int row) => new Vector4(GammaTable[row * 4], GammaTable[row * 4 + 1], GammaTable[row * 4 + 2], GammaTable[row * 4 + 3]) / 4f;
        return Vector4.Lerp(At(low), At(high), t);
    }
}
