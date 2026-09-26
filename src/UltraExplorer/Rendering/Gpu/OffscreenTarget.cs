using System.Windows.Media.Imaging;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// A render target that is never shown: the same format and size rules as the
/// surface WPF displays, but a plain texture on any device set - WARP
/// included, which cannot share with Direct3D 9.  The tests draw into it and
/// read the pixels back to compare the GPU's picture with the CPU raster's,
/// and the start-up warm-up draws one frame into a small one so the driver
/// compiles its shaders before the first real frame needs them.
/// </summary>
internal sealed class OffscreenTarget : IDisposable
{
    private readonly GpuDeviceSet _devices;
    private TextureReadback? _readback;

    private OffscreenTarget(GpuDeviceSet devices, int width, int height, ID3D11Texture2D texture, ID3D11RenderTargetView view)
    {
        _devices = devices;
        Width = width;
        Height = height;
        Texture = texture;
        RenderTargetView = view;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>B8G8R8A8_UNorm, render target and shader resource, one sample.</summary>
    public ID3D11Texture2D Texture { get; }

    public ID3D11RenderTargetView RenderTargetView { get; }

    public static OffscreenTarget Create(GpuDeviceSet devices, int width, int height)
    {
        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };

        var texture = devices.Device.CreateTexture2D(in description);
        try
        {
            return new OffscreenTarget(devices, width, height, texture, devices.Device.CreateRenderTargetView(texture));
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    /// <summary>The pixels as stored, BGRA, rows packed at <see cref="Width"/> * 4 bytes.</summary>
    public byte[] ReadPixels()
    {
        _readback ??= new TextureReadback(_devices);
        return _readback.ReadPixels(Texture, out _, out _);
    }

    /// <summary>The target as a frozen Pbgra32 bitmap, for writing a PNG or comparing with WPF's pictures.</summary>
    public BitmapSource ReadBitmap(double dpiX = 96, double dpiY = 96)
    {
        _readback ??= new TextureReadback(_devices);
        return _readback.ReadBitmap(Texture, dpiX, dpiY);
    }

    /// <summary>One pixel of a <see cref="ReadPixels"/> result as 0xAARRGGBB, the packing NestedRaster uses.</summary>
    public static uint PixelAt(byte[] pixels, int width, int x, int y)
    {
        var index = (y * width + x) * 4;
        return (uint)(pixels[index + 3] << 24 | pixels[index + 2] << 16 | pixels[index + 1] << 8 | pixels[index]);
    }

    public void Dispose()
    {
        _readback?.Dispose();
        RenderTargetView.Dispose();
        Texture.Dispose();
    }
}
