using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// Reads a GPU texture back into memory: a staging copy of it, mapped for
/// reading.  Used where WPF needs the picture as plain pixels - a
/// RenderTargetBitmap of the window (<c>--capture</c>, <c>--nested-snapshots</c>,
/// printing) - and by the tests, which compare what the GPU drew with what the
/// CPU raster draws.
///
/// The staging texture is made the first time and kept while the size stays
/// the same, because a capture run reads the same surface many times.  Mapping
/// waits for the GPU to finish everything queued before it, so what comes
/// back is always the last complete frame.  Only for 32-bit formats with one
/// sample; the canvas uses nothing else.  Not for use during ordinary frames:
/// it stalls the CPU on the GPU.
/// </summary>
internal sealed class TextureReadback : IDisposable
{
    private readonly GpuDeviceSet _devices;
    private ID3D11Texture2D? _staging;
    private Texture2DDescription _stagingDescription;

    public TextureReadback(GpuDeviceSet devices)
    {
        _devices = devices;
    }

    /// <summary>
    /// The texture as a frozen Pbgra32 bitmap of its full size.  The canvas
    /// draws opaque pixels, so the premultiplied reading of them is exact.
    /// </summary>
    public BitmapSource ReadBitmap(ID3D11Texture2D source, double dpiX, double dpiY)
    {
        var mapped = Map(source, out var width, out var height);
        try
        {
            var bitmap = BitmapSource.Create(
                width,
                height,
                dpiX,
                dpiY,
                PixelFormats.Pbgra32,
                null,
                mapped.DataPointer,
                checked((int)(mapped.RowPitch * (uint)height)),
                (int)mapped.RowPitch);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            _devices.Context.Unmap(_staging!, 0);
        }
    }

    /// <summary>
    /// The texture's pixels as they are stored (BGRA for the canvas's
    /// textures), rows packed without padding: width * 4 bytes per row.
    /// </summary>
    public byte[] ReadPixels(ID3D11Texture2D source, out int width, out int height)
    {
        var mapped = Map(source, out width, out height);
        try
        {
            var rowBytes = width * 4;
            var pixels = new byte[rowBytes * height];
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(mapped.DataPointer + (nint)(row * mapped.RowPitch), pixels, row * rowBytes, rowBytes);
            }

            return pixels;
        }
        finally
        {
            _devices.Context.Unmap(_staging!, 0);
        }
    }

    private MappedSubresource Map(ID3D11Texture2D source, out int width, out int height)
    {
        var description = source.Description;
        width = (int)description.Width;
        height = (int)description.Height;
        if (_staging is null
            || _stagingDescription.Width != description.Width
            || _stagingDescription.Height != description.Height
            || _stagingDescription.Format != description.Format)
        {
            // Let go before the new one is made, and the description kept
            // only with a texture made from it: a card that refuses the new
            // texture leaves none, so the next capture tries again rather
            // than copy into the released one.
            _staging?.Dispose();
            _staging = null;
            var stagingDescription = new Texture2DDescription
            {
                Width = description.Width,
                Height = description.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = description.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            };
            _staging = _devices.Device.CreateTexture2D(in stagingDescription);
            _stagingDescription = stagingDescription;
        }

        _devices.Context.CopyResource(_staging, source);
        return _devices.Context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
    }

    public void Dispose()
    {
        _staging?.Dispose();
        _staging = null;
    }
}
