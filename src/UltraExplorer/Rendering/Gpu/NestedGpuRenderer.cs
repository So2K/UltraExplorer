using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Color4 = Vortice.Mathematics.Color4;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// Draws a <see cref="NestedGpuFrame"/> on one graphics card: the pipeline
/// objects made from <see cref="ShaderCache"/>'s bytecode, the dynamic
/// buffers the instances are copied into, and the few calls a frame is.
///
/// A frame is a clear to the canvas colour and one instanced draw per list -
/// four vertices per instance, corners from the vertex id on a triangle
/// strip - in the order the layers stack: the scene's rectangles, the label
/// layer's rectangles (pills, marks), the files' icons, then every glyph.
/// Three pipelines, four draws.  Blending is premultiplied (One /
/// InverseSourceAlpha) throughout, so an opaque shape replaces what is under
/// it, a corner pixel mixes exactly as far as it is covered, and a
/// translucent pill or an icon's soft edge lays over the cells the way WPF
/// lays a premultiplied colour.  The clear and the scene are opaque, so the
/// texture WPF shows stays opaque.
///
/// One renderer per device set, made through <see cref="For"/> and disposed
/// with the set.  <see cref="Draw"/> works against any render target on the
/// set: the shared surface WPF shows (<see cref="NestedSurface.Present"/>)
/// or an offscreen texture, which is what the tests and the warm-up draw
/// into.  UI thread once the set is handed out; the warm-up thread before.
/// </summary>
internal sealed unsafe class NestedGpuRenderer : IDisposable
{
    private const int InitialSceneInstances = 65536;
    private const int InitialLabelInstances = 4096;
    private const int InitialIconInstances = 4096;
    private const int InitialGlyphInstances = 65536;

    private readonly GpuDeviceSet _devices;
    private readonly ID3D11VertexShader _rectVertexShader;
    private readonly ID3D11PixelShader _rectPixelShader;
    private readonly ID3D11InputLayout _rectLayout;
    private readonly ID3D11VertexShader _iconVertexShader;
    private readonly ID3D11PixelShader _iconPixelShader;
    private readonly ID3D11InputLayout _iconLayout;
    private readonly ID3D11SamplerState _iconSampler;
    private readonly ID3D11VertexShader _glyphVertexShader;
    private readonly ID3D11PixelShader _glyphPixelShader;
    private readonly ID3D11InputLayout _glyphLayout;
    private readonly ID3D11SamplerState _glyphSampler;
    private readonly ID3D11BlendState _premultiplied;
    private readonly ID3D11RasterizerState _rasterizer;
    private readonly ID3D11Buffer _frameConstants;
    private readonly InstanceBuffer _sceneBuffer;
    private readonly InstanceBuffer _labelBuffer;
    private readonly InstanceBuffer _iconBuffer;
    private readonly InstanceBuffer _glyphBuffer;
    private int _constantsWidth = -1;
    private int _constantsHeight = -1;
    private bool _disposed;

    private NestedGpuRenderer(GpuDeviceSet devices, CompiledShaders shaders)
    {
        _devices = devices;
        var device = devices.Device;
        try
        {
            var rectCode = shaders["RectVS"];
            _rectVertexShader = device.CreateVertexShader(rectCode);
            _rectPixelShader = device.CreatePixelShader(shaders["RectPS"]);
            _rectLayout = device.CreateInputLayout(RectElements, rectCode);

            var iconCode = shaders["IconVS"];
            _iconVertexShader = device.CreateVertexShader(iconCode);
            _iconPixelShader = device.CreatePixelShader(shaders["IconPS"]);
            _iconLayout = device.CreateInputLayout(IconElements, iconCode);

            // Trilinear for the icons: every level of a slice is a real
            // picture of the icon.  The glyph pages have one level and the
            // field is sampled between texels, never across pages.
            _iconSampler = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, float.MaxValue));
            var glyphCode = shaders["GlyphVS"];
            _glyphVertexShader = device.CreateVertexShader(glyphCode);
            _glyphPixelShader = device.CreatePixelShader(shaders["GlyphPS"]);
            _glyphLayout = device.CreateInputLayout(GlyphElements, glyphCode);
            _glyphSampler = device.CreateSamplerState(new SamplerDescription(Filter.MinMagLinearMipPoint, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, float.MaxValue));

            _premultiplied = device.CreateBlendState(new BlendDescription(Blend.One, Blend.InverseSourceAlpha, Blend.One, Blend.InverseSourceAlpha));
            _rasterizer = device.CreateRasterizerState(RasterizerDescription.CullNone);
            _frameConstants = device.CreateBuffer(new BufferDescription(
                (uint)sizeof(FrameConstants),
                BindFlags.ConstantBuffer,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write,
                ResourceOptionFlags.None,
                0));
            _sceneBuffer = new InstanceBuffer(device, InitialSceneInstances * RectInstance.Size);
            _labelBuffer = new InstanceBuffer(device, InitialLabelInstances * RectInstance.Size);
            _iconBuffer = new InstanceBuffer(device, InitialIconInstances * IconInstance.Size);
            _glyphBuffer = new InstanceBuffer(device, InitialGlyphInstances * GlyphQuadSize);
        }
        catch
        {
            DisposeParts();
            throw;
        }
    }

    private const int GlyphQuadSize = 40;

    /// <summary>The per-instance layout of <see cref="RectInstance"/>, slot 0, one step per instance.</summary>
    private static readonly InputElementDescription[] RectElements =
    [
        new("OUTER", 0, Format.R32G32B32A32_Float, 0, 0, InputClassification.PerInstanceData, 1),
        new("FEATURE", 0, Format.R32G32B32A32_Float, 16, 0, InputClassification.PerInstanceData, 1),
        new("HEADERBOTTOM", 0, Format.R32_Float, 32, 0, InputClassification.PerInstanceData, 1),
        new("RADIUS", 0, Format.R32_Float, 36, 0, InputClassification.PerInstanceData, 1),
        new("INNERRADII", 0, Format.R32_UInt, 40, 0, InputClassification.PerInstanceData, 1),
        new("KIND", 0, Format.R32_UInt, 44, 0, InputClassification.PerInstanceData, 1),
        new("COLOURS", 0, Format.R32G32B32A32_UInt, 48, 0, InputClassification.PerInstanceData, 1)
    ];

    /// <summary>The per-instance layout of <see cref="IconInstance"/>.</summary>
    private static readonly InputElementDescription[] IconElements =
    [
        new("RECT", 0, Format.R32G32B32A32_Float, 0, 0, InputClassification.PerInstanceData, 1),
        new("SLOT", 0, Format.R32_UInt, 16, 0, InputClassification.PerInstanceData, 1),
        new("TINT", 0, Format.R32_UInt, 20, 0, InputClassification.PerInstanceData, 1)
    ];

    /// <summary>The per-instance layout of <see cref="GlyphQuad"/>.</summary>
    private static readonly InputElementDescription[] GlyphElements =
    [
        new("RECT", 0, Format.R32G32B32A32_Float, 0, 0, InputClassification.PerInstanceData, 1),
        new("UVMIN", 0, Format.R32_UInt, 16, 0, InputClassification.PerInstanceData, 1),
        new("UVMAX", 0, Format.R32_UInt, 20, 0, InputClassification.PerInstanceData, 1),
        new("COLOUR", 0, Format.R32_UInt, 24, 0, InputClassification.PerInstanceData, 1),
        new("PAGETIER", 0, Format.R32_UInt, 28, 0, InputClassification.PerInstanceData, 1),
        new("PXRANGE", 0, Format.R32_Float, 32, 0, InputClassification.PerInstanceData, 1),
        new("BIAS", 0, Format.R32_Float, 36, 0, InputClassification.PerInstanceData, 1)
    ];

    /// <summary>The set this renderer draws on.</summary>
    public GpuDeviceSet Devices => _devices;

    /// <summary>Instance bytes copied to the GPU by the last <see cref="Draw"/>: nothing when neither list changed.</summary>
    public long LastUploadBytes { get; private set; }

    /// <summary>How long the last <see cref="Draw"/> spent copying instances into the GPU's buffers, on the calling thread.</summary>
    public double LastUploadMilliseconds { get; private set; }

    /// <summary>Instances drawn by the last <see cref="Draw"/>, every list together.</summary>
    public int LastInstances { get; private set; }

    /// <summary>Glyphs drawn by the last <see cref="Draw"/>.</summary>
    public int LastGlyphs { get; private set; }

    /// <summary>Icons drawn by the last <see cref="Draw"/>.</summary>
    public int LastIcons { get; private set; }

    /// <summary>
    /// The renderer of <paramref name="devices"/>, made the first time it is
    /// asked for - from the shaders' bytecode, loaded or compiled once per
    /// process - and disposed with the set.
    /// </summary>
    public static NestedGpuRenderer For(GpuDeviceSet devices) => For(devices, ShaderCache.Shared);

    /// <summary>
    /// The same, made from the given bytecode if it has not been made yet:
    /// for tests, which compile the shaders without touching the state
    /// folder's cache.
    /// </summary>
    public static NestedGpuRenderer For(GpuDeviceSet devices, CompiledShaders shaders) =>
        devices.Attach(set => new NestedGpuRenderer(set, shaders));

    /// <summary>
    /// Registers the renderer's part of every device set's warm-up with
    /// <see cref="GpuBootstrap"/>: the shaders, the pipelines, and one frame
    /// through every one of them drawn offscreen, so the driver has compiled
    /// its own shaders before the first frame the user sees.  Call once,
    /// before <see cref="GpuBootstrap.Start"/>, and after the atlases'
    /// warm-up (<see cref="GpuLabelAtlases.RegisterWarmUp"/>) so the frame can
    /// sample their textures.
    /// </summary>
    public static void RegisterWarmUp() => GpuBootstrap.RegisterWarmUp(set => For(set).WarmUp());

    /// <summary>
    /// Draws <paramref name="frame"/> into <paramref name="target"/>: clears
    /// all of it to the frame's colour, then draws the instances into the
    /// top-left <paramref name="width"/> x <paramref name="height"/> pixels.
    /// Copies a list to the GPU only when it changed since the last frame
    /// drawn from it.  Icons and glyphs are drawn only when the frame names
    /// the atlas views to sample.  GPU commands only; nothing waits for the
    /// GPU here, and nothing is allocated.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Draw(ID3D11RenderTargetView target, int width, int height, NestedGpuFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var context = _devices.Context;
        var uploadStarted = Stopwatch.GetTimestamp();
        UpdateConstants(context, width, height);
        LastUploadBytes = _sceneBuffer.Upload(context, frame, frame.SceneRects, frame.SceneVersion)
            + _labelBuffer.Upload(context, frame, frame.LabelRects, frame.LabelVersion)
            + _iconBuffer.Upload(context, frame, frame.Icons, frame.LabelVersion)
            + _glyphBuffer.Upload(context, frame, frame.Glyphs, frame.LabelVersion);
        LastUploadMilliseconds = Stopwatch.GetElapsedTime(uploadStarted).TotalMilliseconds;

        context.ClearRenderTargetView(target, ToColor4(frame.ClearColour));
        context.OMSetRenderTargets(target);
        context.RSSetViewport(0, 0, width, height);
        context.RSSetState(_rasterizer);
        context.OMSetBlendState(_premultiplied);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        context.VSSetConstantBuffer(0, _frameConstants);
        context.PSSetConstantBuffer(0, _frameConstants);

        var scene = frame.SceneRects.Count;
        var labels = frame.LabelRects.Count;
        if (scene > 0 || labels > 0)
        {
            context.IASetInputLayout(_rectLayout);
            context.VSSetShader(_rectVertexShader);
            context.PSSetShader(_rectPixelShader);
        }

        if (scene > 0)
        {
            context.IASetVertexBuffer(0, _sceneBuffer.Buffer, RectInstance.Size);
            context.DrawInstanced(4, (uint)scene, 0, 0);
        }

        if (labels > 0)
        {
            context.IASetVertexBuffer(0, _labelBuffer.Buffer, RectInstance.Size);
            context.DrawInstanced(4, (uint)labels, 0, 0);
        }

        var icons = frame.IconView is null ? 0 : frame.Icons.Count;
        if (icons > 0)
        {
            context.IASetInputLayout(_iconLayout);
            context.VSSetShader(_iconVertexShader);
            context.PSSetShader(_iconPixelShader);
            context.PSSetShaderResource(0, frame.IconView!);
            context.PSSetSampler(0, _iconSampler);
            context.IASetVertexBuffer(0, _iconBuffer.Buffer, IconInstance.Size);
            context.DrawInstanced(4, (uint)icons, 0, 0);
        }

        var glyphs = frame.GlyphView is null ? 0 : frame.Glyphs.Count;
        if (glyphs > 0)
        {
            context.IASetInputLayout(_glyphLayout);
            context.VSSetShader(_glyphVertexShader);
            context.PSSetShader(_glyphPixelShader);
            context.PSSetShaderResource(1, frame.GlyphView!);
            context.PSSetSampler(1, _glyphSampler);
            context.IASetVertexBuffer(0, _glyphBuffer.Buffer, GlyphQuadSize);
            context.DrawInstanced(4, (uint)glyphs, 0, 0);
        }

        LastInstances = scene + labels + icons + glyphs;
        LastIcons = icons;
        LastGlyphs = glyphs;

        // Nothing stays bound: a surface given up after this frame is freed
        // at once rather than when some later frame happens to replace it.
        context.ClearState();
    }

    /// <summary>
    /// One throw-away frame through every pipeline, drawn into a small
    /// offscreen texture: every kind of rectangle, and an icon and a glyph
    /// from the atlas textures attached to the set when they are - makes the
    /// driver compile the pipelines now, on the warm-up thread, instead of
    /// inside the first frame of a zoom.  The caller waits for the GPU (the
    /// bootstrap does after every warm-up).
    /// </summary>
    public void WarmUp()
    {
        using var target = _devices.CreateOffscreenTarget(256, 256);
        using var frame = new NestedGpuFrame(64, 16, 16);
        var sink = new GpuSink();
        sink.Begin(frame.SceneRects, 256, 256, 0xFF111315);
        sink.Clear(0xFF111315);
        sink.Cell(4.5, 4.5, 250.5, 160.5, 6, 22.5, true, 10, 9, 13, 19, 0xFF3A4450, 0xFF1C2127, 0xFF252B33, 0xFF60CDFF);
        sink.File(20, 60, 130, 80, 3, 21, 64, 24, 76, 0xFF2A2F36, 0xFFE3B341);
        sink.Fill(140, 60, 143, 63, 0xFF8A8F96);
        frame.SceneChanged();
        ref var pill = ref frame.LabelRects.Add();
        pill = default;
        pill.Outer = new Vector4(30.25f, 100.5f, 120.75f, 114.25f);
        pill.Radius = 3;
        pill.Kind = (uint)RectKind.Rounded;
        pill.Body = 0xD8141618;

        if (_devices.TryGetAttached<IconAtlasTexture>(out var icons) && icons is { IsDisposed: false })
        {
            frame.IconView = icons.View;
            ref var icon = ref frame.Icons.Add();
            icon.Rect = new Vector4(22, 62, 38, 78);
            icon.Slot = 0;
            icon.Tint = 0xFFFFFFFF;
        }

        if (_devices.TryGetAttached<GlyphAtlasTexture>(out var glyphs) && glyphs is not null)
        {
            frame.GlyphView = glyphs.View;
            ref var glyph = ref frame.Glyphs.Add();
            glyph.Rect = new Vector4(40.5f, 62.25f, 52.5f, 78.25f);
            glyph.UV0 = 0;
            glyph.UV1 = 16u | 16u << 16;
            glyph.Colour = 0xFFF2F2F2;
            glyph.PageTier = 0;
            glyph.PxRange = 2;
            glyph.Bias = 0;
        }

        frame.LabelsChanged();
        Draw(target.RenderTargetView, 256, 256, frame);
        _devices.Context.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeParts();
    }

    private void DisposeParts()
    {
        _glyphBuffer?.Dispose();
        _iconBuffer?.Dispose();
        _labelBuffer?.Dispose();
        _sceneBuffer?.Dispose();
        _frameConstants?.Dispose();
        _rasterizer?.Dispose();
        _premultiplied?.Dispose();
        _glyphSampler?.Dispose();
        _glyphLayout?.Dispose();
        _glyphPixelShader?.Dispose();
        _glyphVertexShader?.Dispose();
        _iconSampler?.Dispose();
        _iconLayout?.Dispose();
        _iconPixelShader?.Dispose();
        _iconVertexShader?.Dispose();
        _rectLayout?.Dispose();
        _rectPixelShader?.Dispose();
        _rectVertexShader?.Dispose();
    }

    private void UpdateConstants(ID3D11DeviceContext context, int width, int height)
    {
        if (width == _constantsWidth && height == _constantsHeight)
        {
            return;
        }

        var mapped = context.Map(_frameConstants, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            *(FrameConstants*)mapped.DataPointer = new FrameConstants
            {
                ViewportPx = new Vector2(width, height),
                InverseViewportPx = new Vector2(1f / width, 1f / height),
                Scale = 1,
                TextGamma = GpuTextTuning.Gamma,
                TextContrast = GpuTextTuning.Contrast,
                TextSharpness = GpuTextTuning.Sharpness,
                GammaRatios = GpuTextTuning.GammaRatios(GpuTextTuning.Gamma)
            };
        }
        finally
        {
            context.Unmap(_frameConstants, 0);
        }

        _constantsWidth = width;
        _constantsHeight = height;
    }

    private static Color4 ToColor4(uint argb) => new(
        ((argb >> 16) & 0xFF) / 255f,
        ((argb >> 8) & 0xFF) / 255f,
        (argb & 0xFF) / 255f,
        (argb >> 24) / 255f);

    /// <summary>The constant buffer b0 of Nested.hlsl.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct FrameConstants
    {
        public Vector2 ViewportPx;
        public Vector2 InverseViewportPx;
        public float Scale;
        public float TextGamma;
        public float TextContrast;
        public float TextSharpness;
        public Vector4 GammaRatios;
    }

    /// <summary>
    /// A dynamic vertex buffer holding one list's instances.  Rewritten whole
    /// (Map with WriteDiscard, so the GPU keeps reading the old copy while the
    /// new one is written) and only when the list was filled afresh; grown by
    /// doubling when a frame needs more, which is the only time it allocates.
    /// </summary>
    private sealed class InstanceBuffer(ID3D11Device device, int initialBytes) : IDisposable
    {
        private object? _frame;
        private int _version = int.MinValue;

        public ID3D11Buffer Buffer { get; private set; } = Create(device, initialBytes);

        public int Capacity { get; private set; } = initialBytes;

        /// <summary>Copies <paramref name="list"/> in if it changed; the bytes copied.</summary>
        public long Upload<T>(ID3D11DeviceContext context, object frame, InstanceList<T> list, int version) where T : unmanaged
        {
            if (ReferenceEquals(frame, _frame) && version == _version)
            {
                return 0;
            }

            var bytes = list.ByteCount;
            if (bytes > Capacity)
            {
                var capacity = Capacity;
                while (capacity < bytes)
                {
                    capacity = checked(capacity * 2);
                }

                Buffer.Dispose();
                Buffer = Create(device, capacity);
                Capacity = capacity;
            }

            if (bytes > 0)
            {
                var mapped = context.Map(Buffer, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    System.Buffer.MemoryCopy(list.Pointer, (void*)mapped.DataPointer, Capacity, bytes);
                }
                finally
                {
                    context.Unmap(Buffer, 0);
                }
            }

            _frame = frame;
            _version = version;
            return bytes;
        }

        public void Dispose() => Buffer.Dispose();

        private static ID3D11Buffer Create(ID3D11Device device, int bytes) =>
            device.CreateBuffer(new BufferDescription(
                (uint)bytes,
                BindFlags.VertexBuffer,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write,
                ResourceOptionFlags.None,
                0));
    }
}
