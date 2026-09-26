using SharpGen.Runtime;
using Vortice.Direct3D9;
using VorticeLuid = Vortice.Luid;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// Everything the GPU path needs from Direct3D 9, in one place.  WPF can only
/// show a Direct3D 9 surface (D3DImage takes an IDirect3DSurface9), so the
/// picture is drawn by Direct3D 11 into a texture shared with a Direct3D9Ex
/// device and handed to WPF through that device.  Direct3D 9 is also the
/// only API that says which adapter drives which monitor in the order WPF's
/// own compositor sees them, which is why the adapter is chosen here too.
///
/// Kept apart from the Direct3D 11 code because the two APIs share type names
/// (Format, Usage, Luid, QueryType) that would otherwise need aliases in
/// every file.
/// </summary>
internal static class Direct3D9Bridge
{
    /// <summary>
    /// Creates the Direct3D9Ex object.  It is cheap once d3d9.dll is loaded,
    /// but its list of adapters is a snapshot: after a monitor is plugged in
    /// or out a new one is needed to see the change.
    /// </summary>
    public static IDirect3D9Ex CreateDirect3D() => D3D9.Direct3DCreate9Ex();

    /// <summary>The adapter's LUID in the form DXGI and the rest of the GPU code use.</summary>
    public static VorticeLuid LuidOf(IDirect3D9Ex direct3D, uint adapter)
    {
        var luid = direct3D.GetAdapterLuid(adapter);
        return new VorticeLuid(luid.LowPart, luid.HighPart);
    }

    /// <summary>
    /// The Direct3D 9 adapter ordinal that drives <paramref name="monitor"/>,
    /// or -1.  Each monitor is its own ordinal, so two monitors on one card are
    /// two ordinals with the same LUID.
    /// </summary>
    public static int AdapterForMonitor(IDirect3D9Ex direct3D, IntPtr monitor)
    {
        var count = direct3D.AdapterCount;
        for (var adapter = 0u; adapter < count; adapter++)
        {
            if (direct3D.GetAdapterMonitor(adapter) == monitor)
            {
                return (int)adapter;
            }
        }

        return -1;
    }

    /// <summary>The first Direct3D 9 adapter ordinal on the card with this LUID, or -1.</summary>
    public static int AdapterForLuid(IDirect3D9Ex direct3D, VorticeLuid luid)
    {
        var count = direct3D.AdapterCount;
        for (var adapter = 0u; adapter < count; adapter++)
        {
            if (LuidOf(direct3D, adapter).Equals(luid))
            {
                return (int)adapter;
            }
        }

        return -1;
    }

    /// <summary>
    /// A Direct3D9Ex device that is never presented: it only opens the shared
    /// textures so WPF can take their surfaces.  The flags are the ones WPF's
    /// interop documentation asks for.  Multithreaded because WPF's render
    /// thread copies from the surface through this device while the UI thread
    /// draws; FpuPreserve so Direct3D does not drop the thread's floating point
    /// precision under the managed code.  The desktop window is only a focus
    /// window for a device that never goes full screen.
    /// </summary>
    public static IDirect3DDevice9Ex CreateDevice(IDirect3D9Ex direct3D, uint adapter)
    {
        var desktop = GetDesktopWindow();
        var parameters = new PresentParameters
        {
            Windowed = true,
            SwapEffect = SwapEffect.Discard,
            BackBufferWidth = 1,
            BackBufferHeight = 1,
            BackBufferFormat = Format.Unknown,
            DeviceWindowHandle = desktop,
            PresentationInterval = PresentInterval.Immediate
        };

        return direct3D.CreateDeviceEx(
            adapter,
            DeviceType.Hardware,
            desktop,
            CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
            parameters);
    }

    /// <summary>
    /// Opens a Direct3D 11 texture made with the legacy shared handle as a
    /// Direct3D 9 render target, and takes its only level as the surface WPF
    /// will be given.  A8R8G8B8 is what B8G8R8A8_UNorm is called in Direct3D 9.
    /// </summary>
    public static (IDirect3DTexture9 Texture, IDirect3DSurface9 Surface) OpenShared(
        IDirect3DDevice9Ex device, IntPtr sharedHandle, int width, int height)
    {
        var handle = sharedHandle;
        var texture = device.CreateTexture((uint)width, (uint)height, 1, Usage.RenderTarget, Format.A8R8G8B8, Pool.Default, ref handle);
        try
        {
            return (texture, texture.GetSurfaceLevel(0));
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    /// <summary>
    /// True when the device can no longer be used: removed, hung or reset.
    /// The "occluded" and "mode changed" answers are successes and mean only
    /// that nothing is on screen for the moment.
    /// </summary>
    public static bool IsLost(IDirect3DDevice9Ex device, IntPtr window)
    {
        try
        {
            device.CheckDeviceState(window);
            return false;
        }
        catch (SharpGenException)
        {
            return true;
        }
    }

    /// <summary>A short description of an adapter for logs and reports.</summary>
    public static string DescribeAdapter(IDirect3D9Ex direct3D, uint adapter)
    {
        try
        {
            var identifier = direct3D.GetAdapterIdentifier(adapter);
            return $"{identifier.Description} ({identifier.DeviceName})";
        }
        catch (SharpGenException)
        {
            return $"adapter {adapter}";
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();
}
