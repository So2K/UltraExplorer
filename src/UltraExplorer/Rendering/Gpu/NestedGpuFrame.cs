using Vortice.Direct3D11;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// One frame of the nested canvas as instances, filled on the UI thread and
/// drawn by <see cref="NestedGpuRenderer"/>: the scene's rectangles - cells,
/// tiles, specks, washes - and the label layer's rectangles, icons and glyphs,
/// the scene apart from the labels so a frame that only redraws the names (an
/// icon arrived) leaves the scene's upload alone.
///
/// The lists live in native memory and keep their size from frame to frame,
/// so filling a frame allocates nothing once the view has been seen.  The
/// versions say when a list was filled afresh; the renderer uploads a list
/// only when its version moved.  The three label lists are filled together
/// and share <see cref="LabelVersion"/>.
/// </summary>
internal sealed class NestedGpuFrame : IDisposable
{
    public NestedGpuFrame(int sceneCapacity = 16384, int labelCapacity = 1024, int glyphCapacity = 16384)
    {
        SceneRects = new InstanceList<RectInstance>(sceneCapacity);
        LabelRects = new InstanceList<RectInstance>(labelCapacity);
        Icons = new InstanceList<IconInstance>(labelCapacity);
        Glyphs = new InstanceList<GlyphQuad>(glyphCapacity);
    }

    /// <summary>The cells, tiles, specks and washes, in painting order.</summary>
    public InstanceList<RectInstance> SceneRects { get; }

    /// <summary>The label layer's rectangles - pills, marks - drawn over the scene.</summary>
    public InstanceList<RectInstance> LabelRects { get; }

    /// <summary>The files' icons, drawn over the label rectangles.</summary>
    public InstanceList<IconInstance> Icons { get; }

    /// <summary>Every glyph of every name, drawn last.</summary>
    public InstanceList<GlyphQuad> Glyphs { get; }

    /// <summary>
    /// The icon atlas as the card this frame is drawn on holds it
    /// (<see cref="IconAtlasTexture.View"/>), set with the labels each frame -
    /// the view changes when the array grows.  No icons are drawn without it.
    /// </summary>
    public ID3D11ShaderResourceView? IconView { get; set; }

    /// <summary>The glyph atlas on the same card (<see cref="GlyphAtlasTexture.View"/>), likewise.</summary>
    public ID3D11ShaderResourceView? GlyphView { get; set; }

    /// <summary>
    /// The device set <see cref="IconView"/> and <see cref="GlyphView"/> were
    /// taken from, set with them.  A view is only good on the card it was
    /// made on: after the canvas moves to another card, a frame that draws
    /// the scene but not the names again still holds the old card's views,
    /// and the renderer leaves the icons and glyphs out rather than bind them
    /// on a device they do not belong to.
    /// </summary>
    public GpuDeviceSet? LabelDevices { get; set; }

    /// <summary>The colour the frame starts from: the bare canvas where no folder covers the view.</summary>
    public uint ClearColour { get; set; } = 0xFF111315;

    /// <summary>Moves on each time <see cref="SceneRects"/> is filled afresh.</summary>
    public int SceneVersion { get; private set; }

    /// <summary>Moves on each time <see cref="LabelRects"/> is filled afresh.</summary>
    public int LabelVersion { get; private set; }

    /// <summary>Says <see cref="SceneRects"/> holds a new frame's scene.</summary>
    public void SceneChanged() => SceneVersion++;

    /// <summary>Says <see cref="LabelRects"/>, <see cref="Icons"/> and <see cref="Glyphs"/> hold a new frame's labels.</summary>
    public void LabelsChanged() => LabelVersion++;

    /// <summary>
    /// Empties the three label lists and lets go of the atlases' views: for a
    /// frame whose names are drawn some other way (or not at all), and at the
    /// start of every frame's names, so lists left half filled by a frame
    /// that failed are uploaded as they are rather than taken for the last
    /// upload.
    /// </summary>
    public void ClearLabels()
    {
        LabelRects.Clear();
        Icons.Clear();
        Glyphs.Clear();
        IconView = null;
        GlyphView = null;
        LabelDevices = null;
        LabelsChanged();
    }

    public void Dispose()
    {
        SceneRects.Dispose();
        LabelRects.Dispose();
        Icons.Dispose();
        Glyphs.Dispose();
    }
}
