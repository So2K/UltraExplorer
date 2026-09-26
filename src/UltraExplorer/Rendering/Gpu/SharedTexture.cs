using Vortice.Direct3D11;
using Vortice.DXGI;
using IDirect3DSurface9 = Vortice.Direct3D9.IDirect3DSurface9;
using IDirect3DTexture9 = Vortice.Direct3D9.IDirect3DTexture9;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// One picture seen from both sides: a Direct3D 11 render target the renderer
/// draws into, and the same memory opened in Direct3D9Ex as the surface WPF's
/// D3DImage shows.
///
/// The texture uses the legacy shared handle (ResourceOptionFlags.Shared) -
/// Direct3D9Ex can open neither the NT-handle kind nor the keyed-mutex kind -
/// which also means the two APIs do not synchronise with each other: whoever
/// hands the surface to WPF must first wait for Direct3D 11 to finish drawing
/// into it (<see cref="GpuDeviceSet.WaitForGpu"/>).
/// </summary>
internal sealed class SharedTexture : IDisposable
{
    private SharedTexture(
        int width,
        int height,
        ID3D11Texture2D texture,
        ID3D11RenderTargetView renderTargetView,
        IntPtr sharedHandle,
        IDirect3DTexture9 texture9,
        IDirect3DSurface9 surface9)
    {
        Width = width;
        Height = height;
        Texture = texture;
        RenderTargetView = renderTargetView;
        SharedHandle = sharedHandle;
        Texture9 = texture9;
        Surface9 = surface9;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The Direct3D 11 side: B8G8R8A8_UNorm, render target and shader resource.</summary>
    public ID3D11Texture2D Texture { get; }

    public ID3D11RenderTargetView RenderTargetView { get; }

    /// <summary>The legacy shared handle.  Owned by the texture; never closed on its own.</summary>
    public IntPtr SharedHandle { get; }

    /// <summary>The Direct3D 9 side, kept alive for as long as WPF may hold its surface.</summary>
    public IDirect3DTexture9 Texture9 { get; }

    public IDirect3DSurface9 Surface9 { get; }

    /// <summary>What D3DImage.SetBackBuffer takes.</summary>
    public IntPtr Surface9Pointer => Surface9.NativePointer;

    /// <summary>
    /// Creates the pair on <paramref name="devices"/>, which must have a
    /// Direct3D9Ex device (<see cref="GpuDeviceSet.CanShareWithWpf"/>).
    /// Throws SharpGenException, or GpuUnavailableException when the driver
    /// gives no shared handle; the caller treats either as "this GPU cannot
    /// feed WPF".
    /// </summary>
    public static SharedTexture Create(GpuDeviceSet devices, int width, int height)
    {
        var device9 = devices.Device9 ?? throw new InvalidOperationException("This device set has no Direct3D9Ex device to share with WPF.");

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
            MiscFlags = ResourceOptionFlags.Shared
        };

        ID3D11Texture2D? texture = null;
        ID3D11RenderTargetView? view = null;
        try
        {
            texture = devices.Device.CreateTexture2D(in description);
            view = devices.Device.CreateRenderTargetView(texture);

            IntPtr handle;
            using (var resource = texture.QueryInterface<IDXGIResource>())
            {
                handle = resource.SharedHandle;
            }

            if (handle == IntPtr.Zero)
            {
                throw new GpuUnavailableException("The driver gave the shared texture no handle.");
            }

            var (texture9, surface9) = Direct3D9Bridge.OpenShared(device9, handle, width, height);
            return new SharedTexture(width, height, texture, view, handle, texture9, surface9);
        }
        catch
        {
            view?.Dispose();
            texture?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Surface9.Dispose();
        Texture9.Dispose();
        RenderTargetView.Dispose();
        Texture.Dispose();
    }
}
