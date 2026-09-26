using System.Diagnostics;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using IDirect3D9Ex = Vortice.Direct3D9.IDirect3D9Ex;
using IDirect3DDevice9Ex = Vortice.Direct3D9.IDirect3DDevice9Ex;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// Everything the canvas needs on one graphics card: the Direct3D 11 device
/// and its immediate context that draw the picture, the Direct3D9Ex device
/// that hands it to WPF, an event query to wait on, the frame timer, and
/// whatever the renderer and the atlases hang off it (<see cref="Attach{T}"/>).
///
/// One set per adapter, because on a machine with two cards - this one drives
/// its 4K monitor from the discrete card and the second monitor from the
/// integrated one - drawing on the card that does not drive the window's
/// monitor makes every frame cross between the cards.  The sets are made
/// ahead of time (<see cref="GpuBootstrap"/>) so moving the window to the
/// other monitor only swaps one set for another.
///
/// Threads: a set may be created and warmed on a background thread; after it
/// is handed out its immediate context belongs to the UI thread alone.  The
/// Direct3D 11 device is created without the single-threaded flag so the
/// atlases can still create resources from their workers.
/// </summary>
internal sealed class GpuDeviceSet : IDisposable
{
    /// <summary>Microsoft's vendor id: the Basic Render Driver (WARP) and the Basic Display Adapter.</summary>
    private const uint MicrosoftVendorId = 0x1414;

    private static readonly FeatureLevel[] PreferredLevels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
    private static readonly FeatureLevel[] BaseLevel = [FeatureLevel.Level_11_0];

    private readonly object _attachmentGate = new();
    private readonly List<IDisposable> _attachments = [];
    private readonly Dictionary<Type, IDisposable> _attachmentsByType = [];
    private ID3D11Query? _eventQuery;
    private GpuTimer? _timer;
    private int _disposed;

    private GpuDeviceSet(
        Luid adapterLuid,
        string adapterName,
        uint vendorId,
        bool isWarp,
        FeatureLevel featureLevel,
        ID3D11Device device,
        ID3D11DeviceContext context,
        IDirect3D9Ex? direct3D9,
        IDirect3DDevice9Ex? device9)
    {
        AdapterLuid = adapterLuid;
        AdapterName = adapterName;
        VendorId = vendorId;
        IsWarp = isWarp;
        FeatureLevel = featureLevel;
        Device = device;
        Context = context;
        Direct3D9 = direct3D9;
        Device9 = device9;
    }

    /// <summary>The card, as DXGI and Direct3D 9 both name it.  The key the sets are kept under.</summary>
    public Luid AdapterLuid { get; }

    public string AdapterName { get; }

    public uint VendorId { get; }

    /// <summary>The software rasteriser: deterministic and needs no display, so the tests use it.  Never shown.</summary>
    public bool IsWarp { get; }

    public FeatureLevel FeatureLevel { get; }

    public ID3D11Device Device { get; }

    /// <summary>The immediate context.  UI thread only once the set has been handed out.</summary>
    public ID3D11DeviceContext Context { get; }

    public IDirect3D9Ex? Direct3D9 { get; }

    /// <summary>The Direct3D9Ex device that opens shared textures for WPF; null for offscreen sets.</summary>
    public IDirect3DDevice9Ex? Device9 { get; }

    /// <summary>True when this set can feed a D3DImage (<see cref="NestedSurface"/>).</summary>
    public bool CanShareWithWpf => Device9 is not null;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>GPU time per frame from timestamp queries, created on first use.  UI thread only.</summary>
    public GpuTimer Timer => _timer ??= new GpuTimer(Device, Context);

    /// <summary>A one-line description for logs and reports: name, LUID, feature level.</summary>
    public string Description =>
        $"{AdapterName} [{FormatLuid(AdapterLuid)}] level {FeatureLevel}{(IsWarp ? " WARP" : "")}{(CanShareWithWpf ? " shared-with-WPF" : " offscreen")}";

    /// <summary>A LUID as the high and low parts in hex, the way dxdiag shows it.</summary>
    public static string FormatLuid(Luid luid) => $"{luid.HighPart:X8}-{luid.LowPart:X8}";

    /// <summary>Every adapter DXGI knows, hardware and software, in its order (the one driving the primary monitor first).</summary>
    public static IReadOnlyList<GpuAdapterDescription> EnumerateAdapters()
    {
        var adapters = new List<GpuAdapterDescription>();
        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);
        for (var index = 0u; factory.EnumAdapters1(index, out var adapter).Success; index++)
        {
            using (adapter)
            {
                var description = adapter.Description1;
                adapters.Add(new GpuAdapterDescription(
                    description.Luid,
                    description.Description,
                    description.VendorId,
                    description.DeviceId,
                    IsSoftwareAdapter(description),
                    (ulong)description.DedicatedVideoMemory));
            }
        }

        return adapters;
    }

    /// <summary>
    /// Creates the set on the card with this LUID.  With
    /// <paramref name="shareWithWpf"/> it also creates the Direct3D9Ex device on
    /// the same card and proves that a shared texture opens on it, which is
    /// the last of the conditions for drawing the canvas on the GPU; without
    /// it the set can only draw offscreen, which is all the tests need.
    /// Throws <see cref="GpuUnavailableException"/> saying why the card cannot
    /// be used: not found, a software adapter, feature level below 11_0, no
    /// monitor for Direct3D 9, or sharing refused.
    /// </summary>
    public static GpuDeviceSet Create(Luid adapterLuid, bool shareWithWpf)
    {
        IDXGIAdapter1? adapter;
        try
        {
            using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);
            if (factory.EnumAdapterByLuid(adapterLuid, out adapter).Failure || adapter is null)
            {
                throw new GpuUnavailableException($"No graphics adapter has LUID {FormatLuid(adapterLuid)}.");
            }
        }
        catch (SharpGenException ex)
        {
            throw new GpuUnavailableException($"DXGI could not list the graphics adapters: {ex.Message}", ex);
        }

        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        IDirect3D9Ex? direct3D = null;
        IDirect3DDevice9Ex? device9 = null;
        GpuDeviceSet? set = null;
        try
        {
            using (adapter)
            {
                var description = adapter.Description1;
                if (IsSoftwareAdapter(description))
                {
                    throw new GpuUnavailableException($"{description.Description} is a software adapter.");
                }

                (device, context, var level) = CreateDevice(adapter, DriverType.Unknown);

                if (shareWithWpf)
                {
                    direct3D = Direct3D9Bridge.CreateDirect3D();
                    var ordinal = Direct3D9Bridge.AdapterForLuid(direct3D, adapterLuid);
                    if (ordinal < 0)
                    {
                        throw new GpuUnavailableException($"{description.Description} drives no monitor, so Direct3D 9 cannot open its textures for WPF.");
                    }

                    device9 = Direct3D9Bridge.CreateDevice(direct3D, (uint)ordinal);
                }

                set = new GpuDeviceSet(adapterLuid, description.Description, description.VendorId, false, level, device, context, direct3D, device9);
            }

            if (shareWithWpf)
            {
                // Some drivers create both devices and still refuse to open a
                // shared texture; find out now, not on the first frame.
                using var probe = SharedTexture.Create(set, 16, 16);
            }

            return set;
        }
        catch (Exception ex) when (ex is not GpuUnavailableException)
        {
            DisposeParts(set, device, context, direct3D, device9);
            throw new GpuUnavailableException($"The graphics card could not be set up: {ex.Message}", ex);
        }
        catch
        {
            DisposeParts(set, device, context, direct3D, device9);
            throw;
        }
    }

    /// <summary>
    /// A set on WARP, Direct3D 11's software rasteriser.  It needs no display
    /// and gives the same pixels on every machine, which makes it the device
    /// for the headless tests.  It cannot share with Direct3D 9, so it never
    /// feeds WPF.
    /// </summary>
    public static GpuDeviceSet CreateWarp()
    {
        var (device, context, level) = CreateDevice(null, DriverType.Warp);
        try
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            var description = adapter.Description;
            return new GpuDeviceSet(description.Luid, description.Description, description.VendorId, true, level, device, context, null, null);
        }
        catch
        {
            context.Dispose();
            device.Dispose();
            throw;
        }
    }

    /// <summary>
    /// True when this set can no longer draw: Direct3D 11 reports the device
    /// removed or reset (a driver update or a timeout detection and recovery),
    /// or the Direct3D9Ex device reports itself lost or hung.  Not free - a call
    /// into the driver each - so it is asked when WPF says the front buffer
    /// came back and after a present fails, not every frame.
    /// </summary>
    public bool IsLost(IntPtr window = default)
    {
        if (IsDisposed)
        {
            return true;
        }

        try
        {
            if (Device.DeviceRemovedReason.Failure)
            {
                return true;
            }
        }
        catch (SharpGenException)
        {
            return true;
        }

        return Device9 is not null && Direct3D9Bridge.IsLost(Device9, window);
    }

    /// <summary>
    /// Flushes everything queued on the immediate context and waits until the
    /// GPU has finished it, spinning briefly and then yielding.  Direct3D9Ex
    /// does not synchronise shared textures with Direct3D 11 (and Flush alone
    /// only starts the work), so this is what makes a frame complete before
    /// WPF copies it.  False when the wait ran past
    /// <paramref name="timeoutMilliseconds"/> or the device failed; either way
    /// the caller treats the device as lost.
    /// </summary>
    public bool WaitForGpu(double timeoutMilliseconds, out double waitedMilliseconds) =>
        WaitForGpu(double.PositiveInfinity, timeoutMilliseconds, out waitedMilliseconds, out _) == GpuWaitResult.Finished;

    /// <summary>
    /// As <see cref="WaitForGpu(double, out double)"/>, for a frame that must
    /// finish before WPF may copy it, on a device that is only slow rather
    /// than gone.  A healthy card can easily take longer than a frame: another
    /// program's work time-sliced ahead of ours (a game, a video export, an
    /// inference run on the same card), our own textures paged back in after
    /// the window sat in the background, a large atlas copy queued with the
    /// frame.  None of that is a lost device, and treating it as one - the
    /// set thrown away, the canvas on the CPU for seconds, and for the session
    /// after three - costs far more than the wait.  So once the wait passes
    /// <paramref name="askAfterMilliseconds"/> the device is asked, and asked
    /// again as often, whether it has been removed or hung
    /// (<see cref="IsLost"/>); only a yes, a failed query, or a wait past
    /// <paramref name="limitMilliseconds"/> - chosen past the two seconds after
    /// which Windows resets a hung card itself - ends the wait unfinished.
    /// <paramref name="wasSlow"/> says the wait went past the first question.
    /// </summary>
    public GpuWaitResult WaitForGpu(double askAfterMilliseconds, double limitMilliseconds, out double waitedMilliseconds, out bool wasSlow)
    {
        _eventQuery ??= Device.CreateQuery(QueryType.Event);
        Context.End(_eventQuery);
        Context.Flush();

        var started = Stopwatch.GetTimestamp();
        var spinner = new SpinWait();
        var nextQuestion = askAfterMilliseconds;
        wasSlow = false;
        while (true)
        {
            var result = Context.GetData(_eventQuery, IntPtr.Zero, 0, AsyncGetDataFlags.DoNotFlush);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (result.Code == 0)
            {
                waitedMilliseconds = elapsed;
                return GpuWaitResult.Finished;
            }

            if (result.Failure)
            {
                waitedMilliseconds = elapsed;
                return GpuWaitResult.Failed;
            }

            if (elapsed > limitMilliseconds)
            {
                waitedMilliseconds = elapsed;
                return GpuWaitResult.TimedOut;
            }

            if (elapsed > nextQuestion)
            {
                wasSlow = true;
                nextQuestion = elapsed + askAfterMilliseconds;
                if (IsLost())
                {
                    waitedMilliseconds = elapsed;
                    return GpuWaitResult.Failed;
                }
            }

            // Spinning costs a core but answers within microseconds, which is
            // what a frame needs; past a millisecond the GPU is clearly busy
            // and the thread gives its time slice away instead.
            if (elapsed < 1)
            {
                spinner.SpinOnce(sleep1Threshold: -1);
            }
            else
            {
                Thread.Yield();
            }
        }
    }

    /// <summary>A texture Direct3D 11 draws into and WPF shows.  Needs <see cref="CanShareWithWpf"/>.</summary>
    public SharedTexture CreateSharedTexture(int width, int height) => SharedTexture.Create(this, width, height);

    /// <summary>A texture that is drawn into and read back but never shown; works on every set, WARP included.</summary>
    public OffscreenTarget CreateOffscreenTarget(int width, int height) => OffscreenTarget.Create(this, width, height);

    /// <summary>
    /// The one <typeparamref name="T"/> that belongs to this set, created by
    /// <paramref name="create"/> the first time it is asked for: the renderer's
    /// pipeline, the GPU copies of the icon and glyph atlases.  They live and
    /// die with the set, so when a card is lost or the window moves to the
    /// other card nothing of the old card is kept by mistake.  Disposed in the
    /// reverse of the order they were made.
    /// </summary>
    public T Attach<T>(Func<GpuDeviceSet, T> create) where T : class, IDisposable
    {
        lock (_attachmentGate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (_attachmentsByType.TryGetValue(typeof(T), out var existing))
            {
                return (T)existing;
            }

            var created = create(this);
            _attachmentsByType[typeof(T)] = created;
            _attachments.Add(created);
            return created;
        }
    }

    /// <summary>The <typeparamref name="T"/> attached to this set, if one has been made.</summary>
    public bool TryGetAttached<T>(out T? attached) where T : class, IDisposable
    {
        lock (_attachmentGate)
        {
            var found = _attachmentsByType.TryGetValue(typeof(T), out var existing);
            attached = found ? (T)existing! : null;
            return found;
        }
    }

    /// <summary>
    /// Raised once, on the thread that disposes the set, before anything of
    /// it is released.  A set is shared by every canvas on its card - more
    /// than one window can be open, the file dialog's among them - and when
    /// one of them hands it back as lost (<see cref="GpuBootstrap.ReportDeviceLost"/>)
    /// the others' surfaces still point at it; this is how they learn to stop
    /// drawing with it before their next frame touches a released device.
    /// </summary>
    public event Action<GpuDeviceSet>? Disposing;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            Disposing?.Invoke(this);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // A listener that could not be told (its dispatcher shut down)
            // has nothing left to draw with the set anyway.
        }

        Disposing = null;
        lock (_attachmentGate)
        {
            for (var index = _attachments.Count - 1; index >= 0; index--)
            {
                try
                {
                    _attachments[index].Dispose();
                }
                catch (SharpGenException)
                {
                    // A lost device fails its releases too; nothing to keep.
                }
            }

            _attachments.Clear();
            _attachmentsByType.Clear();
        }

        _timer?.Dispose();
        _eventQuery?.Dispose();
        try
        {
            Context.ClearState();
            Context.Flush();
        }
        catch (SharpGenException)
        {
        }

        Context.Dispose();
        Device.Dispose();
        Device9?.Dispose();
        Direct3D9?.Dispose();
    }

    private static (ID3D11Device Device, ID3D11DeviceContext Context, FeatureLevel Level) CreateDevice(IDXGIAdapter? adapter, DriverType driverType)
    {
        // BGRA support for DirectWrite and Direct2D on the same device; never
        // single-threaded, see the class summary.  11_1 first, and again with
        // 11_0 alone for runtimes that do not know 11_1 and reject the list.
        const DeviceCreationFlags flags = DeviceCreationFlags.BgraSupport;
        var result = D3D11.D3D11CreateDevice(adapter, driverType, flags, PreferredLevels, out var device, out var level, out var context);
        if (result.Failure)
        {
            result = D3D11.D3D11CreateDevice(adapter, driverType, flags, BaseLevel, out device, out level, out context);
        }

        if (result.Failure || device is null || context is null)
        {
            context?.Dispose();
            device?.Dispose();
            throw new GpuUnavailableException($"Direct3D 11 could not create a device ({result}).");
        }

        if (level < FeatureLevel.Level_11_0)
        {
            context.Dispose();
            device.Dispose();
            throw new GpuUnavailableException($"The graphics card only offers Direct3D feature level {level}; the canvas needs 11_0.");
        }

        return (device, context, level);
    }

    private static bool IsSoftwareAdapter(AdapterDescription1 description) =>
        (description.Flags & AdapterFlags.Software) != 0 || description.VendorId == MicrosoftVendorId;

    private static void DisposeParts(
        GpuDeviceSet? set,
        ID3D11Device? device,
        ID3D11DeviceContext? context,
        IDirect3D9Ex? direct3D,
        IDirect3DDevice9Ex? device9)
    {
        if (set is not null)
        {
            set.Dispose();
            return;
        }

        device9?.Dispose();
        direct3D?.Dispose();
        context?.Dispose();
        device?.Dispose();
    }
}

/// <summary>How <see cref="GpuDeviceSet.WaitForGpu(double, double, out double, out bool)"/> ended.</summary>
internal enum GpuWaitResult
{
    /// <summary>The GPU finished everything queued.</summary>
    Finished,

    /// <summary>The device failed the query, or said it was removed or hung when asked.</summary>
    Failed,

    /// <summary>The work was still not done at the hard limit, with the device still claiming to be well.</summary>
    TimedOut
}

/// <summary>One graphics adapter as DXGI describes it.</summary>
internal readonly record struct GpuAdapterDescription(
    Luid Luid,
    string Name,
    uint VendorId,
    uint DeviceId,
    bool IsSoftware,
    ulong DedicatedVideoMemory);

/// <summary>Why the GPU path cannot be used on a card, in words fit for a log.</summary>
internal sealed class GpuUnavailableException : Exception
{
    public GpuUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
