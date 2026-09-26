using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpGen.Runtime;
using Vortice.Direct3D11;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// The nested canvas's picture as WPF sees it: a D3DImage whose back buffer is
/// a texture Direct3D 11 draws into (<see cref="SharedTexture"/>).  The
/// canvas's scene layer draws this image exactly where it used to draw its
/// WriteableBitmap, so the WPF layers above it - selection, beacons, hover -
/// keep composing on top with nothing else changed.
///
/// A frame is one <see cref="Present"/>: take the image's lock, let the
/// renderer draw, wait for the GPU, mark the drawn part dirty, unlock.  WPF's
/// render thread then copies the back buffer forward in the same batch as the
/// other layers, so an outline never lags the cells under it.  Three rules
/// from WPF's D3DImage source and Microsoft's interop notes are kept:
/// <list type="bullet">
/// <item>Draw only while locked: Lock is what keeps the render thread from
/// copying a half-drawn frame.</item>
/// <item>Always Unlock, even when TryLock gave up: WPF counts the lock whether
/// or not it was obtained, and a missed Unlock freezes the image.</item>
/// <item>Wait for the GPU before unlocking: Direct3D9Ex sharing is not
/// synchronised, and Flush alone only starts the work.</item>
/// </list>
///
/// Size follows the old bitmap's policy: device pixels, grown in 256-pixel
/// steps and shrunk only when far too big, with only the view's part drawn
/// and the rest clipped by the canvas.  The DPI of a D3DImage is fixed when it
/// is made, so a DPI change means a new surface.  UI thread only.
/// </summary>
internal sealed class NestedSurface : D3DImage, IDisposable
{
    private const int GrowStep = 256;

    /// <summary>The largest texture feature level 11_0 guarantees.</summary>
    private const int MaximumSize = 16384;

    /// <summary>
    /// A frame whose GPU work has not finished in this long is not given up
    /// on: the device is asked whether it has been removed or hung, and while
    /// it says no the wait goes on - a card busy with another program's work
    /// is slow, not lost (<see cref="GpuDeviceSet.WaitForGpu(double, double, out double, out bool)"/>).
    /// </summary>
    private const double DefaultAskDeviceAfterMilliseconds = 50;

    /// <summary>
    /// The longest a frame waits for the GPU while the device keeps saying it
    /// is well: past Windows' own two seconds, after which it resets a hung
    /// card and the device reports itself removed.
    /// </summary>
    private const double GpuWaitLimitMilliseconds = 2500;

    /// <summary>
    /// How long a frame waits for WPF's render thread to finish copying the
    /// previous one.  Past that the frame is skipped and tried again on the
    /// next tick, rather than blocking the UI thread on the compositor.
    /// </summary>
    private const double LockLimitMilliseconds = 2;

    /// <summary>
    /// A lock that is tried without waiting.  TryLock with a timeout is a
    /// wait on an event that ends at the next tick of the system timer, not
    /// after the timeout - measured on this machine, a 2 ms TryLock on the UI
    /// thread came back after 8 ms on average and up to 17 - so the surface
    /// asks without waiting and does its own timing (<see cref="TryLockWithin"/>).
    /// </summary>
    private static readonly Duration Immediately = new(TimeSpan.Zero);

    private readonly GpuDeviceSet _devices;
    private SharedTexture? _current;
    private SharedTexture? _next;
    private TextureReadback? _readback;
    private bool _dirtyWhole;
    private bool _reattach;
    private bool _frontBufferMissing;
    private bool _lost;
    private bool _disposed;

    /// <param name="devices">A set that can share with WPF, on the card that drives the window's monitor.</param>
    /// <param name="scaleX">The canvas's DPI scale; a D3DImage keeps the DPI it was made with.</param>
    /// <param name="scaleY">The same, vertically.</param>
    public NestedSurface(GpuDeviceSet devices, double scaleX, double scaleY)
        : base(96 * scaleX, 96 * scaleY)
    {
        if (!devices.CanShareWithWpf)
        {
            throw new ArgumentException("The device set has no Direct3D9Ex device, so WPF could not show what it draws.", nameof(devices));
        }

        _devices = devices;
        ScaleX = scaleX;
        ScaleY = scaleY;
        IsFrontBufferAvailableChanged += OnFrontBufferAvailableChanged;
        _devices.Disposing += OnDevicesDisposing;
    }

    /// <summary>
    /// Raised once when the device behind the surface fails - a present that
    /// threw, whose device said it was removed or hung, or that was still not
    /// finished past Windows' own reset delay; a front buffer that went or
    /// came back with a lost device; or the set disposed under the surface
    /// because another canvas on the card reported it lost.
    /// The owner disposes the surface, reports the set to
    /// <see cref="GpuBootstrap.ReportDeviceLost"/>, and draws on the CPU until
    /// a new set is ready.
    /// </summary>
    public event EventHandler? DeviceLost;

    /// <summary>
    /// Raised when WPF has its front buffer again after losing it (the lock
    /// screen, a UAC prompt, a display mode change) and the device survived.
    /// Nothing on screen is current any more; the owner asks for a whole
    /// frame.
    /// </summary>
    public event EventHandler? ContentLost;

    public GpuDeviceSet Devices => _devices;

    public double ScaleX { get; }

    public double ScaleY { get; }

    /// <summary>The part of the surface the canvas shows, in device pixels (<see cref="EnsureSize"/>).</summary>
    public int ViewPixelWidth { get; private set; }

    public int ViewPixelHeight { get; private set; }

    /// <summary>The whole texture WPF holds, in device pixels; 0 before the first present.</summary>
    public int SurfaceWidth => _current?.Width ?? 0;

    public int SurfaceHeight => _current?.Height ?? 0;

    /// <summary>
    /// Changes whenever WPF is given a different back buffer.  The scene layer
    /// records <c>DrawImage(surface, DrawRect)</c> again when it sees a new
    /// value after a present, and never otherwise.
    /// </summary>
    public int SurfaceVersion { get; private set; }

    /// <summary>
    /// Where the scene layer draws the image: the whole surface in DIPs, the
    /// same rectangle the WriteableBitmap was drawn into.  Keep nearest
    /// neighbour scaling on the layer so surface pixels map 1:1 to the screen.
    /// </summary>
    public Rect DrawRect => new(0, 0, SurfaceWidth / ScaleX, SurfaceHeight / ScaleY);

    /// <summary>True once the device has failed; every later present returns <see cref="PresentResult.DeviceLost"/>.</summary>
    public bool IsLost => _lost;

    /// <summary>Frames skipped because WPF's render thread still had the previous one (see <see cref="LockLimitMilliseconds"/>).</summary>
    public int SkippedPresents { get; private set; }

    /// <summary>
    /// Frames whose GPU work took longer than the device is given before it
    /// is asked whether it is still there - and it was, so they were shown.
    /// </summary>
    public int SlowFrames { get; private set; }

    /// <summary>
    /// How long a frame's GPU work may run before the device is asked whether
    /// it has been lost; 50 ms.  Settable for the tests, which make every
    /// frame count as slow to see a healthy device kept.
    /// </summary>
    internal double AskDeviceAfterMilliseconds { get; set; } = DefaultAskDeviceAfterMilliseconds;

    /// <summary>
    /// Whether WPF composes this process in software - by choice, on a card
    /// below render tier 2, or in a remote session - where a D3DImage never
    /// has a front buffer and shows its back buffer through the software
    /// fallback instead.  Replaceable for the tests.
    /// </summary>
    internal Func<bool> IsComposingInSoftware { get; set; } = ComposesInSoftware;

    public int Presents { get; private set; }

    /// <summary>How many times WPF asked for a software copy (<see cref="CopyBackBuffer"/>): captures, printing.</summary>
    public int Captures { get; private set; }

    /// <summary>The last present's wait for the image lock, in milliseconds.</summary>
    public double LastLockMilliseconds { get; private set; }

    /// <summary>The last present's time in the renderer's draw calls, in milliseconds of UI thread.</summary>
    public double LastDrawMilliseconds { get; private set; }

    /// <summary>The last present's wait for the GPU to finish the frame, in milliseconds.</summary>
    public double LastGpuWaitMilliseconds { get; private set; }

    /// <summary>The whole of the last present on the UI thread, lock to unlock, in milliseconds.</summary>
    public double LastPresentMilliseconds { get; private set; }

    /// <summary>
    /// The device-pixel size of a view: <c>ceil(DIPs * scale)</c>, at least one
    /// pixel - the same arithmetic the CPU path sizes its bitmap with.
    /// </summary>
    public static (int Width, int Height) PixelSize(double widthDips, double heightDips, double scaleX, double scaleY) =>
        (Math.Max(1, (int)Math.Ceiling(widthDips * scaleX)), Math.Max(1, (int)Math.Ceiling(heightDips * scaleY)));

    /// <summary>
    /// Sets the part of the surface the next frames will draw, and makes a new
    /// texture when the current one is too small or more than three times the
    /// view plus two megapixels.  The new texture is made here, outside the
    /// image lock, and given to WPF at the next <see cref="Present"/>; the
    /// scene layer re-records when <see cref="SurfaceVersion"/> changes after
    /// that present.  True when a new texture was made.
    /// </summary>
    public bool EnsureSize(int pixelWidth, int pixelHeight)
    {
        VerifyAccess();
        if (_disposed || _lost)
        {
            return false;
        }

        if (_devices.IsDisposed)
        {
            MarkLost();
            return false;
        }

        pixelWidth = Math.Clamp(pixelWidth, 1, MaximumSize);
        pixelHeight = Math.Clamp(pixelHeight, 1, MaximumSize);
        ViewPixelWidth = pixelWidth;
        ViewPixelHeight = pixelHeight;

        var latest = _next ?? _current;
        if (latest is not null
            && latest.Width >= pixelWidth
            && latest.Height >= pixelHeight
            && (long)latest.Width * latest.Height <= 3L * pixelWidth * pixelHeight + 2_000_000)
        {
            return false;
        }

        var width = Math.Min(MaximumSize, (pixelWidth + GrowStep - 1) / GrowStep * GrowStep);
        var height = Math.Min(MaximumSize, (pixelHeight + GrowStep - 1) / GrowStep * GrowStep);
        try
        {
            var created = _devices.CreateSharedTexture(width, height);
            _next?.Dispose();
            _next = created;
            return true;
        }
        catch (Exception ex) when (ex is SharpGenException or GpuUnavailableException)
        {
            MarkLost();
            return false;
        }
    }

    /// <summary>
    /// Draws one frame: takes the image lock for at most 2 ms, gives WPF a new
    /// back buffer if <see cref="EnsureSize"/> made one, calls
    /// <paramref name="draw"/> with the render target, waits for the GPU and
    /// marks the view dirty.  All CPU work for the frame - the walk, the
    /// labels, filling the instance lists - belongs before this call; only GPU
    /// commands belong inside <paramref name="draw"/>.  Pass the same cached
    /// delegate every frame so presenting allocates nothing.
    ///
    /// <see cref="PresentResult.Skipped"/> means the render thread still had
    /// the previous frame: keep the layers dirty and ask for another frame.
    /// <see cref="PresentResult.DeviceLost"/> has already raised
    /// <see cref="DeviceLost"/>.
    /// </summary>
    public PresentResult Present(SurfaceDrawer draw)
    {
        VerifyAccess();
        if (_disposed || _lost)
        {
            return PresentResult.DeviceLost;
        }

        if (_devices.IsDisposed)
        {
            // Another canvas on the same card handed the set back as lost
            // (the Disposing event normally says so first): nothing of it may
            // be touched, not even the frame timer.
            MarkLost();
            return PresentResult.DeviceLost;
        }

        if (_frontBufferMissing || (_current is null && _next is null))
        {
            return PresentResult.Unavailable;
        }

        var started = Stopwatch.GetTimestamp();
        var locked = TryLockWithin(LockLimitMilliseconds);
        var failed = false;
        try
        {
            LastLockMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (!locked)
            {
                SkippedPresents++;
                return PresentResult.Skipped;
            }

            if (_next is not null)
            {
                // WPF lets go of the old surface inside SetBackBuffer, so the
                // old texture can go straight away.
                SetBackBuffer(D3DResourceType.IDirect3DSurface9, _next.Surface9Pointer, enableSoftwareFallback: true);
                _current?.Dispose();
                _current = _next;
                _next = null;
                _dirtyWhole = true;
                _reattach = false;
                SurfaceVersion++;
            }
            else if (_reattach)
            {
                // With software fallback on, WPF keeps the old pointer through
                // a front buffer loss and ignores the same pointer again, so
                // it has to be cleared before it can be set.
                SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero, enableSoftwareFallback: true);
                SetBackBuffer(D3DResourceType.IDirect3DSurface9, _current!.Surface9Pointer, enableSoftwareFallback: true);
                _dirtyWhole = true;
                _reattach = false;
                SurfaceVersion++;
            }

            var surface = _current!;
            var drawStarted = Stopwatch.GetTimestamp();
            _devices.Timer.Begin();
            draw(new SurfaceFrame(_devices.Context, surface.RenderTargetView, surface.Texture, ViewPixelWidth, ViewPixelHeight, surface.Width, surface.Height));
            _devices.Timer.End();
            LastDrawMilliseconds = Stopwatch.GetElapsedTime(drawStarted).TotalMilliseconds;

            // The frame must be finished before WPF may copy it, so a slow
            // card is waited for; only a device that says it is gone, or
            // one still not done past Windows' own reset delay, is lost.
            var finished = _devices.WaitForGpu(AskDeviceAfterMilliseconds, GpuWaitLimitMilliseconds, out var waited, out var slow);
            LastGpuWaitMilliseconds = waited;
            if (slow)
            {
                SlowFrames++;
            }

            if (finished != GpuWaitResult.Finished)
            {
                failed = true;
                DetachUnderLock();
                return PresentResult.DeviceLost;
            }

            AddDirtyRect(_dirtyWhole
                ? new Int32Rect(0, 0, surface.Width, surface.Height)
                : new Int32Rect(0, 0, Math.Min(ViewPixelWidth, surface.Width), Math.Min(ViewPixelHeight, surface.Height)));
            _dirtyWhole = false;
            Presents++;
            return PresentResult.Presented;
        }
        catch (Exception ex) when (ex is SharpGenException or COMException or ObjectDisposedException)
        {
            // A removed device fails whatever call comes next - ours through
            // Vortice, WPF's SetBackBuffer and AddDirtyRect as a COM error -
            // and so does a bad call, or a renderer or atlas texture of a set
            // that went away.  Either way this surface cannot be trusted with
            // another frame, and three of these a minute send the canvas back
            // to the CPU for the session.
            failed = true;
            DetachUnderLock();
            return PresentResult.DeviceLost;
        }
        finally
        {
            Unlock();
            LastPresentMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (failed)
            {
                MarkLost();
            }
        }
    }

    /// <summary>
    /// What WPF's RenderTargetBitmap (and printing) sees of this image.  The
    /// default goes through a software copy of the Direct3D 9 surface that may
    /// come back empty; this reads the Direct3D 11 texture instead.  Every
    /// present waits for the GPU, so the texture already holds the last frame.
    /// The whole surface is returned, matching <see cref="DrawRect"/>.
    /// </summary>
    protected override BitmapSource CopyBackBuffer()
    {
        var surface = _current;
        if (_disposed || _lost || surface is null || _devices.IsDisposed)
        {
            return base.CopyBackBuffer();
        }

        try
        {
            _readback ??= new TextureReadback(_devices);
            var copy = _readback.ReadBitmap(surface.Texture, 96 * ScaleX, 96 * ScaleY);
            Captures++;
            return copy;
        }
        catch (SharpGenException)
        {
            return base.CopyBackBuffer();
        }
    }

    /// <summary>
    /// Takes the surface away from WPF and releases the textures.  The device
    /// set is not the surface's to dispose.
    /// </summary>
    public void Dispose()
    {
        VerifyAccess();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsFrontBufferAvailableChanged -= OnFrontBufferAvailableChanged;
        _devices.Disposing -= OnDevicesDisposing;
        if (_current is not null)
        {
            // Bounded, like every lock here: a render thread that never lets
            // go must not hang the window on the way out.
            TryLock(new Duration(TimeSpan.FromMilliseconds(200)));
            try
            {
                SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
            }
            finally
            {
                Unlock();
            }
        }

        _readback?.Dispose();
        _next?.Dispose();
        _current?.Dispose();
        _readback = null;
        _next = null;
        _current = null;
    }

    private void OnFrontBufferAvailableChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        FrontBufferChanged((bool)e.NewValue);

    /// <summary>For the tests: what the surface does when WPF says its front buffer went or came back.</summary>
    internal void SimulateFrontBufferAvailable(bool available) => FrontBufferChanged(available);

    private void FrontBufferChanged(bool available)
    {
        if (_disposed || _lost)
        {
            return;
        }

        if (!available)
        {
            if (_devices.IsLost())
            {
                // A driver reset takes the front buffer with the device.
                MarkLost();
                return;
            }

            // Composing in software, WPF never has a front buffer for a
            // D3DImage and shows the back buffer through the software
            // fallback SetBackBuffer turned on - so the frames go on, and a
            // failing device shows itself in the present as always.
            // Composing on the card, it means the lock screen or a UAC
            // prompt: nothing drawn now would be seen, and the compositor
            // may not answer a present until it is back.  Stop until then.
            _frontBufferMissing = !IsComposingInSoftware();
            return;
        }

        _frontBufferMissing = false;
        if (_devices.IsLost())
        {
            MarkLost();
            return;
        }

        // Re-attached inside the next present's lock rather than here: a
        // present sent while there was no front buffer may never have been
        // answered, and a blocking Lock now could wait for it forever.
        _reattach = _current is not null;
        _dirtyWhole = true;
        ContentLost?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Takes the image lock if WPF's render thread lets go of the previous
    /// frame within <paramref name="milliseconds"/>, asking without waiting
    /// and spinning in between: a wait with a timeout ends at the system
    /// timer's next tick, many milliseconds late (<see cref="Immediately"/>).
    /// A try that fails still counts as a lock in WPF's books, so every one
    /// but the last is unlocked here; the last - taken or not - is unlocked by
    /// the caller, as every TryLock must be.
    /// </summary>
    private bool TryLockWithin(double milliseconds)
    {
        var started = Stopwatch.GetTimestamp();
        var spinner = new SpinWait();
        while (true)
        {
            if (TryLock(Immediately))
            {
                return true;
            }

            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= milliseconds)
            {
                return false;
            }

            Unlock();
            spinner.SpinOnce(sleep1Threshold: -1);
        }
    }

    /// <summary>
    /// The set this surface draws with is being disposed - another canvas on
    /// the same card reported it lost, or the application is closing.  The
    /// surface is lost with it: the owner lets it go at its next frame and
    /// draws on the CPU until the bootstrap has a new set.
    /// </summary>
    private void OnDevicesDisposing(GpuDeviceSet devices)
    {
        if (Dispatcher.CheckAccess())
        {
            if (!_disposed)
            {
                MarkLost();
            }

            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
            {
                MarkLost();
            }
        });
    }

    private static bool ComposesInSoftware() =>
        RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly
        || RenderCapability.Tier >> 16 < 2
        || SystemParameters.IsRemoteSession;

    private void DetachUnderLock()
    {
        try
        {
            SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
        }
    }

    private void MarkLost()
    {
        if (_lost)
        {
            return;
        }

        _lost = true;
        DeviceLost?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>What a present gives the renderer: where to draw and how much of it is on screen.</summary>
internal readonly struct SurfaceFrame
{
    public SurfaceFrame(
        ID3D11DeviceContext context,
        ID3D11RenderTargetView target,
        ID3D11Texture2D texture,
        int width,
        int height,
        int surfaceWidth,
        int surfaceHeight)
    {
        Context = context;
        Target = target;
        Texture = texture;
        Width = width;
        Height = height;
        SurfaceWidth = surfaceWidth;
        SurfaceHeight = surfaceHeight;
    }

    /// <summary>The set's immediate context.</summary>
    public ID3D11DeviceContext Context { get; }

    /// <summary>The render target view of the surface WPF shows.</summary>
    public ID3D11RenderTargetView Target { get; }

    public ID3D11Texture2D Texture { get; }

    /// <summary>The part on screen, in device pixels from the top left: the viewport to draw into.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>The whole texture; the part past <see cref="Width"/> x <see cref="Height"/> is clipped by the canvas.</summary>
    public int SurfaceWidth { get; }

    public int SurfaceHeight { get; }
}

/// <summary>The renderer's part of a present: GPU commands only, into <see cref="SurfaceFrame.Target"/>.</summary>
internal delegate void SurfaceDrawer(in SurfaceFrame frame);

/// <summary>How a <see cref="NestedSurface.Present"/> went.</summary>
internal enum PresentResult
{
    /// <summary>The frame is drawn and will be on screen with WPF's next composition.</summary>
    Presented,

    /// <summary>WPF's render thread still had the previous frame; nothing was drawn.  Try again next tick.</summary>
    Skipped,

    /// <summary>Nothing can be shown now: no surface size yet, or WPF has no front buffer (lock screen).</summary>
    Unavailable,

    /// <summary>The device failed; <see cref="NestedSurface.DeviceLost"/> has been raised.</summary>
    DeviceLost
}
