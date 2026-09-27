using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using UltraExplorer.Services;
using Vortice;
using IDirect3D9Ex = Vortice.Direct3D9.IDirect3D9Ex;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// Decides, for the whole process, whether the nested canvas draws on the GPU,
/// and keeps the GPU ready before it is needed.
///
/// <see cref="Start"/> runs on a thread of its own from the application's
/// start: it finds the card that drives the monitor the window will open on,
/// makes that card's <see cref="GpuDeviceSet"/>, runs every registered warm-up
/// against it (shaders, atlases, one throw-away frame), and only then hands it
/// out.  Two seconds later it does the same for the other cards, so moving the
/// window to a monitor on another card finds its set already made.
///
/// The card is chosen through Direct3D 9, because only Direct3D 9 says which
/// adapter drives which monitor the way WPF's compositor sees it: the monitor
/// under the window, the Direct3D 9 adapter whose monitor that is, its LUID,
/// and the DXGI adapter with that LUID.  Drawing on any other card would send
/// every frame across the bus between the two.
///
/// <see cref="Decide"/> never waits: until the right set is ready the canvas
/// keeps drawing on the CPU and asks again on a later frame.  The CPU path is
/// also what runs headless, in a remote session, on a machine WPF renders in
/// software, when the user asks for it, and for the rest of the session after
/// three device losses within a minute.
/// </summary>
internal static class GpuBootstrap
{
    /// <summary>Environment variable choosing the renderer: <c>auto</c>, <c>gpu</c> or <c>cpu</c>.</summary>
    public const string RendererVariable = "ULTRAEXPLORER_RENDERER";

    /// <summary>Command-line switch doing the same, and winning over the variable: <c>--renderer gpu</c>.</summary>
    public const string RendererSwitch = "--renderer";

    public const string ReasonReady = "the GPU is ready";
    public const string ReasonCpuChosen = "the CPU renderer was chosen";
    public const string ReasonLostTooOften = "the GPU was lost three times within a minute; the CPU draws for the rest of the session";
    public const string ReasonNotOnScreen = "the canvas is not in a window";
    public const string ReasonRenderTier = "WPF's render tier is below 2";
    public const string ReasonSoftwareRendering = "WPF renders in software";
    public const string ReasonRemoteSession = "this is a remote session";
    public const string ReasonWarmingUp = "the GPU is still being prepared";
    public const string ReasonUnavailable = "the GPU for this monitor cannot be used (see GpuBootstrap.LastFailure)";

    private const int LossesBeforeCpu = 3;
    private const int EventsKept = 64;
    private static readonly TimeSpan LossWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LongestRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OtherAdaptersDelay = TimeSpan.FromSeconds(2);

    private static readonly object Gate = new();
    private static readonly Dictionary<Luid, GpuDeviceSet> Sets = [];
    private static readonly HashSet<Luid> Pending = [];
    private static readonly Dictionary<Luid, (long At, int Count)> Failures = [];
    private static readonly Dictionary<IntPtr, Luid> MonitorAdapters = [];
    private static readonly List<Action<GpuDeviceSet>> WarmUps = [];
    private static readonly Queue<long> Losses = new();
    private static readonly Queue<string> RecentEvents = new();
    private static readonly BlockingCollection<Action> Work = new();
    private static readonly TaskCompletionSource<GpuDeviceSet?> ReadySource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly Lazy<RendererPreference?> ExplicitPreferenceValue = new(ReadExplicitPreference);
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static IDirect3D9Ex? _direct3D;
    private static bool _direct3DFailed;
    private static Thread? _worker;
    private static RendererPreference _settingPreference;
    private static volatile bool _cpuForSession;
    private static volatile string? _lastFailure;

    /// <summary>
    /// Raised on the warm-up thread each time a device set has been made,
    /// warmed and handed out.  Handlers must not use the set's immediate
    /// context; that belongs to the UI thread from now on.
    /// </summary>
    public static event Action<GpuDeviceSet>? DeviceSetReady;

    /// <summary>Raised on the calling thread when <see cref="Preference"/> changes.</summary>
    public static event EventHandler? PreferenceChanged;

    /// <summary>
    /// Raised on the warm-up thread when the monitors have been matched to
    /// their cards again after a display change.  A canvas whose monitor is
    /// now on another card than the one drawing it redraws on the right one.
    /// </summary>
    public static event Action? MonitorsMatched;

    /// <summary>
    /// Completes when the first device set - for the monitor passed to
    /// <see cref="Start"/> - is ready, or with null when it could not be made
    /// or the CPU was chosen.  Never completes if Start was not called.
    /// </summary>
    public static Task<GpuDeviceSet?> Ready => ReadySource.Task;

    public static bool IsStarted
    {
        get
        {
            lock (Gate)
            {
                return _worker is not null;
            }
        }
    }

    /// <summary>What the command line or <see cref="RendererVariable"/> asked for, if anything; it wins over the setting.</summary>
    public static RendererPreference? ExplicitPreference => ExplicitPreferenceValue.Value;

    /// <summary>The renderer in force: the command line, else the environment, else the setting.</summary>
    public static RendererPreference Preference => ExplicitPreference ?? _settingPreference;

    /// <summary>True after three losses within a minute: the CPU draws until the app is restarted.</summary>
    public static bool IsCpuForSession => _cpuForSession;

    /// <summary>Why the last device set could not be made or was dropped, for logs and the settings page.</summary>
    public static string? LastFailure => _lastFailure;

    /// <summary>The device sets handed out so far, one per card.</summary>
    public static IReadOnlyList<GpuDeviceSet> DeviceSets
    {
        get
        {
            lock (Gate)
            {
                return [.. Sets.Values];
            }
        }
    }

    /// <summary>What the bootstrap did, newest last, for the bench report and bug reports.</summary>
    public static IReadOnlyList<string> Events
    {
        get
        {
            lock (Gate)
            {
                return [.. RecentEvents];
            }
        }
    }

    /// <summary>Reads <c>auto</c>, <c>gpu</c> or <c>cpu</c>, in any case; null for anything else.</summary>
    public static RendererPreference? ParsePreference(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "auto" => RendererPreference.Auto,
        "gpu" => RendererPreference.Gpu,
        "cpu" => RendererPreference.Cpu,
        _ => null
    };

    /// <summary>
    /// The Canvas options setting.  The command line and the environment
    /// variable still win over it.  Choosing a GPU renderer starts the
    /// warm-up if it has not run.
    /// </summary>
    public static void SetSettingPreference(RendererPreference preference)
    {
        var before = Preference;
        _settingPreference = preference;
        if (Preference != before)
        {
            Note($"renderer preference is now {Preference}");
            PreferenceChanged?.Invoke(null, EventArgs.Empty);
        }

        if (Preference != RendererPreference.Cpu)
        {
            Start();
        }
    }

    /// <summary>
    /// The setting as saved, read at the application's start before anything
    /// is started (<see cref="Start"/>), so a saved choice of the CPU keeps
    /// the GPU from being prepared at all: no devices, no shaders, no atlases.
    /// Unlike <see cref="SetSettingPreference"/> it starts nothing and raises
    /// nothing; the workspace, once loaded, sets the same value through that.
    /// </summary>
    public static void UseSavedPreference(RendererPreference preference) => _settingPreference = preference;

    /// <summary>
    /// Adds a step every new device set goes through on the warm-up thread
    /// before it is handed out: compiling or loading shaders, creating the
    /// pipeline and the atlases' textures, drawing a throw-away frame.  A step
    /// that throws makes that card unusable (the reason goes to
    /// <see cref="LastFailure"/>).  Must be registered before <see cref="Start"/>,
    /// because a set already handed out belongs to the UI thread and cannot be
    /// warmed from here.
    /// </summary>
    public static void RegisterWarmUp(Action<GpuDeviceSet> warmUp)
    {
        lock (Gate)
        {
            if (_worker is not null)
            {
                throw new InvalidOperationException("Warm-up steps must be registered before GpuBootstrap.Start.");
            }

            WarmUps.Add(warmUp);
        }
    }

    /// <summary>
    /// Starts the warm-up thread, once per process; later calls do nothing.
    /// <paramref name="monitor"/> is where the window will open (from its saved
    /// placement, see <see cref="MonitorForScreenRect"/>); zero means the
    /// primary monitor.  Returns at once.  Does not make any device when the
    /// CPU renderer is chosen; <see cref="SetSettingPreference"/> starts it
    /// later if that changes.
    /// </summary>
    public static void Start(IntPtr monitor = default)
    {
        lock (Gate)
        {
            if (_worker is not null)
            {
                return;
            }

            _worker = new Thread(RunWork)
            {
                IsBackground = true,
                Name = "GPU warm-up"
            };
            _worker.Start();
        }

        try
        {
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            // Without the notification a monitor plugged in later is simply
            // not matched until the next start; nothing else depends on it.
        }

        AddWork(() => WarmFirst(monitor));
    }

    /// <summary>
    /// Whether the canvas should draw on the GPU now, and on which device
    /// set.  Cheap and never blocking - it reads a few flags, finds the
    /// window's monitor and looks up a dictionary - so the canvas can ask on
    /// every relevant frame: when warm-up finishes, when the window moves to
    /// another monitor, after a device loss.  When the right set is not ready
    /// it asks the warm-up thread to make it and answers CPU for now.  UI
    /// thread only.
    /// </summary>
    public static GpuDecision Decide(Visual canvas)
    {
        var preference = Preference;
        if (preference == RendererPreference.Cpu)
        {
            return new GpuDecision(null, ReasonCpuChosen);
        }

        if (_cpuForSession)
        {
            return new GpuDecision(null, ReasonLostTooOften);
        }

        if (PresentationSource.FromVisual(canvas) is not HwndSource source || source.IsDisposed)
        {
            return new GpuDecision(null, ReasonNotOnScreen);
        }

        // Asked for explicitly, the GPU is tried even where WPF would not
        // accelerate: D3DImage then reaches the screen through its software
        // fallback, which is slow but shows whether the GPU path works.
        if (preference == RendererPreference.Auto)
        {
            if (RenderCapability.Tier >> 16 < 2)
            {
                return new GpuDecision(null, ReasonRenderTier);
            }

            if (RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly
                || source.CompositionTarget?.RenderMode == RenderMode.SoftwareOnly)
            {
                return new GpuDecision(null, ReasonSoftwareRendering);
            }

            if (SystemParameters.IsRemoteSession)
            {
                return new GpuDecision(null, ReasonRemoteSession);
            }
        }

        var monitor = MonitorOfWindow(source.Handle);
        var set = TryGetDeviceSet(monitor);
        if (set is not null)
        {
            return new GpuDecision(set, ReasonReady);
        }

        return new GpuDecision(null, HasFailed(monitor) ? ReasonUnavailable : ReasonWarmingUp);
    }

    /// <summary>
    /// The ready device set for the card that drives <paramref name="monitor"/>,
    /// or null - in which case, unless that card failed moments ago, the
    /// warm-up thread has been asked to make it.  Never blocks.
    /// </summary>
    public static GpuDeviceSet? TryGetDeviceSet(IntPtr monitor)
    {
        if (!IsStarted)
        {
            Start(monitor);
        }

        Luid luid;
        lock (Gate)
        {
            // Made on the warm-up thread, at the start and again after a
            // display change: even with d3d9.dll loaded it takes about 1.5 ms,
            // too much for a frame.  Until then the answer is "not yet".
            if (_direct3D is null || !TryMapMonitor(monitor, out luid))
            {
                return null;
            }

            if (Sets.TryGetValue(luid, out var set))
            {
                return set;
            }

            if (Pending.Contains(luid) || FailedRecently(luid))
            {
                return null;
            }

            Pending.Add(luid);
        }

        AddWork(() => CreateAndPublish(luid));
        return null;
    }

    /// <summary>
    /// The LUID of the card that drives <paramref name="monitor"/>, as
    /// Direct3D 9 sees it; null when no adapter claims the monitor.  Makes the
    /// Direct3D9Ex object on first use if the warm-up thread has not.
    /// </summary>
    public static Luid? AdapterLuidForMonitor(IntPtr monitor)
    {
        lock (Gate)
        {
            EnsureDirect3D();
            return TryMapMonitor(monitor, out var luid) ? luid : null;
        }
    }

    /// <summary>The monitor most of the window is on (the nearest one if it is on none).</summary>
    public static IntPtr MonitorOfWindow(IntPtr window) => MonitorFromWindow(window, MonitorDefaultToNearest);

    /// <summary>The monitor nearest a rectangle in screen pixels - a saved window placement.</summary>
    public static IntPtr MonitorForScreenRect(int left, int top, int right, int bottom)
    {
        var rect = new NativeRect { Left = left, Top = top, Right = right, Bottom = bottom };
        return MonitorFromRect(ref rect, MonitorDefaultToNearest);
    }

    public static IntPtr PrimaryMonitor => MonitorFromPoint(new NativePoint(), MonitorDefaultToPrimary);

    /// <summary>
    /// Every monitor with the card that drives it: for the bench report and
    /// the tests that check the adapter matching.  Calls into Direct3D 9 and
    /// DXGI, so not for every frame.
    /// </summary>
    public static IReadOnlyList<GpuMonitorAdapter> DescribeMonitors()
    {
        var monitors = new List<(IntPtr Handle, string Name, NativeRect Bounds, bool IsPrimary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(handle, ref info))
            {
                monitors.Add((handle, info.DeviceName, info.Monitor, (info.Flags & MonitorInfoPrimary) != 0));
            }

            return true;
        }, IntPtr.Zero);

        var names = GpuDeviceSet.EnumerateAdapters().ToDictionary(adapter => adapter.Luid, adapter => adapter.Name);
        var described = new List<GpuMonitorAdapter>();
        lock (Gate)
        {
            EnsureDirect3D();
            foreach (var (handle, name, bounds, isPrimary) in monitors)
            {
                var ordinal = Direct3D9Bridge.AdapterForMonitor(_direct3D!, handle);
                Luid? luid = ordinal >= 0 ? Direct3D9Bridge.LuidOf(_direct3D!, (uint)ordinal) : null;
                var adapterName = luid is { } known && names.TryGetValue(known, out var found) ? found : "";
                described.Add(new GpuMonitorAdapter(
                    handle,
                    name,
                    new Int32Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top),
                    isPrimary,
                    ordinal,
                    luid,
                    adapterName));
            }
        }

        return described;
    }

    /// <summary>
    /// Takes back a device set whose surface reported it lost.  Call it after
    /// disposing the surface that found the loss: the set is disposed here -
    /// the other canvases' surfaces on it hear of it first through
    /// <see cref="GpuDeviceSet.Disposing"/> and give it up at their next
    /// frame without touching it - a new one for the same card is made when
    /// <see cref="Decide"/> next asks (after a few seconds, which a driver
    /// reset needs), and the third loss within a minute leaves the canvas on
    /// the CPU for the rest of the session.  Only a device that failed counts:
    /// a frame that is merely slow is waited for, never reported.  UI thread.
    /// </summary>
    public static void ReportDeviceLost(GpuDeviceSet set)
    {
        lock (Gate)
        {
            if (Sets.TryGetValue(set.AdapterLuid, out var known) && ReferenceEquals(known, set))
            {
                Sets.Remove(set.AdapterLuid);
            }

            var now = Clock.ElapsedTicks;
            Losses.Enqueue(now);
            while (Losses.Count > 0 && TicksToTime(now - Losses.Peek()) > LossWindow)
            {
                Losses.Dequeue();
            }

            if (Losses.Count >= LossesBeforeCpu)
            {
                _cpuForSession = true;
            }

            RecordFailure(set.AdapterLuid, now);
            _lastFailure = $"{set.AdapterName} was lost";
            NoteLocked($"lost {set.Description}; {Losses.Count} loss(es) in the last minute{(_cpuForSession ? ", CPU for the rest of the session" : "")}");
        }

        set.Dispose();
    }

    /// <summary>Releases every device set, for the application's exit.  UI thread.</summary>
    public static void Shutdown()
    {
        GpuDeviceSet[] sets;
        lock (Gate)
        {
            sets = [.. Sets.Values];
            Sets.Clear();
            Work.CompleteAdding();
        }

        foreach (var set in sets)
        {
            set.Dispose();
        }
    }

    private static void RunWork()
    {
        foreach (var job in Work.GetConsumingEnumerable())
        {
            try
            {
                job();
            }
            catch (Exception ex)
            {
                // A background thread that throws takes the process with it;
                // whatever went wrong here only costs the GPU path.
                _lastFailure = ex.Message;
                Note($"warm-up job failed: {ex.Message}");
            }
        }
    }

    private static void WarmFirst(IntPtr monitor)
    {
        try
        {
            // Made even for the CPU renderer: it is not a device, and with it
            // made here a later switch to the GPU never makes it on the UI thread.
            var target = monitor != IntPtr.Zero ? monitor : PrimaryMonitor;
            Luid? luid;
            lock (Gate)
            {
                EnsureDirect3D();
                luid = TryMapMonitor(target, out var found) ? found : null;
            }

            if (Preference == RendererPreference.Cpu)
            {
                Note("CPU renderer chosen; no device made");
                ReadySource.TrySetResult(null);
                return;
            }

            GpuDeviceSet? set = null;
            if (luid is { } first)
            {
                set = CreateAndPublish(first);
            }
            else
            {
                _lastFailure = "Direct3D 9 names no adapter for the window's monitor.";
                Note(_lastFailure);
            }

            ReadySource.TrySetResult(set);
            _ = Task.Delay(OtherAdaptersDelay).ContinueWith(_ => AddWork(WarmOthers), TaskScheduler.Default);
        }
        finally
        {
            // Direct3D 9 or the monitor's adapter failing above (the job's
            // error goes to LastFailure) still ends the start: whoever waits
            // for the first set hears there is none rather than wait for ever.
            // After a set was handed out this does nothing.
            ReadySource.TrySetResult(null);
        }
    }

    /// <summary>Makes the sets of the cards that drive the other monitors, so a move between monitors finds them ready.</summary>
    private static void WarmOthers()
    {
        var luids = new List<Luid>();
        lock (Gate)
        {
            EnsureDirect3D();
            var count = _direct3D!.AdapterCount;
            for (var ordinal = 0u; ordinal < count; ordinal++)
            {
                var luid = Direct3D9Bridge.LuidOf(_direct3D, ordinal);
                if (!luids.Contains(luid) && !Sets.ContainsKey(luid) && !Pending.Contains(luid) && !Failures.ContainsKey(luid))
                {
                    luids.Add(luid);
                }
            }
        }

        foreach (var luid in luids)
        {
            CreateAndPublish(luid);
        }
    }

    private static GpuDeviceSet? CreateAndPublish(Luid luid)
    {
        Action<GpuDeviceSet>[] warmUps;
        lock (Gate)
        {
            if (Sets.TryGetValue(luid, out var existing))
            {
                return existing;
            }

            Pending.Add(luid);
            warmUps = [.. WarmUps];
        }

        var started = Stopwatch.GetTimestamp();
        GpuDeviceSet? set = null;
        try
        {
            set = GpuDeviceSet.Create(luid, shareWithWpf: true);
            foreach (var warmUp in warmUps)
            {
                warmUp(set);
            }

            // Whatever the warm-ups queued runs now, on this thread, and not
            // inside the first frame that uses the set.
            if (!set.WaitForGpu(5000, out _))
            {
                throw new GpuUnavailableException($"{set.AdapterName} did not finish its warm-up frame.");
            }
        }
        catch (Exception ex)
        {
            set?.Dispose();
            lock (Gate)
            {
                Pending.Remove(luid);
                RecordFailure(luid, Clock.ElapsedTicks);
                _lastFailure = ex.Message;
                NoteLocked($"no device set for {GpuDeviceSet.FormatLuid(luid)}: {ex.Message}");
            }

            return null;
        }

        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        lock (Gate)
        {
            Pending.Remove(luid);
            Failures.Remove(luid);
            Sets[luid] = set;
            NoteLocked($"ready in {elapsed:F1} ms: {set.Description}");
        }

        PerfLog.Value("gpu.deviceset.ms", elapsed);
        DeviceSetReady?.Invoke(set);
        return set;
    }

    private static void AddWork(Action job)
    {
        try
        {
            Work.Add(job);
        }
        catch (InvalidOperationException)
        {
            // Shut down already.
        }
    }

    private static void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // A monitor added, removed or moved to another card: the monitor
        // handles and Direct3D 9's adapter list may both be stale now.  The
        // matching in use stays until its replacement is made, so a canvas
        // drawing through this - a resolution or scale change, a monitor
        // waking up - keeps its set rather than dropping to the CPU while
        // the warm-up thread is busy with something else.
        Note("display settings changed; monitors will be matched again");
        AddWork(MatchMonitorsAgain);
    }

    /// <summary>
    /// A new Direct3D9Ex object - its adapter list is fixed when it is made -
    /// swapped in for the old one and the monitors' cards forgotten, to be
    /// looked up again on the next <see cref="Decide"/>.  Made outside
    /// <see cref="Gate"/>, which the UI thread takes every frame; swapped
    /// under it; the old object released after, when nobody can hold it.
    /// </summary>
    internal static void MatchMonitorsAgain()
    {
        IDirect3D9Ex? fresh = null;
        try
        {
            fresh = Direct3D9Bridge.CreateDirect3D();
        }
        catch (Exception ex) when (ex is GpuUnavailableException or SharpGen.Runtime.SharpGenException or DllNotFoundException)
        {
            // The old list goes on answering: a monitor it does not know is
            // simply not drawn on the GPU until the next change.
            Note($"Direct3D 9 could not be made again after a display change: {ex.Message}");
        }

        IDirect3D9Ex? old;
        lock (Gate)
        {
            old = _direct3D;
            if (fresh is not null)
            {
                _direct3D = fresh;
            }

            MonitorAdapters.Clear();
            Failures.Clear();
            NoteLocked("monitors matched to their cards again");
        }

        if (fresh is not null && !ReferenceEquals(old, fresh))
        {
            old?.Dispose();
        }

        MonitorsMatched?.Invoke();
    }

    /// <summary>
    /// Caller holds <see cref="Gate"/>.  A failure is remembered, so that
    /// <see cref="Decide"/> says the GPU cannot be used instead of that it is
    /// still being prepared - which, with nothing left to prepare it, it would
    /// say for the rest of the session.  A display change makes it again
    /// (<see cref="MatchMonitorsAgain"/>).
    /// </summary>
    private static void EnsureDirect3D()
    {
        if (_direct3D is not null)
        {
            return;
        }

        try
        {
            _direct3D = Direct3D9Bridge.CreateDirect3D();
            _direct3DFailed = false;
        }
        catch
        {
            _direct3DFailed = true;
            throw;
        }
    }

    /// <summary>Caller holds <see cref="Gate"/> and has made the Direct3D9Ex object.</summary>
    private static bool TryMapMonitor(IntPtr monitor, out Luid luid)
    {
        if (MonitorAdapters.TryGetValue(monitor, out luid))
        {
            return true;
        }

        var ordinal = Direct3D9Bridge.AdapterForMonitor(_direct3D!, monitor);
        if (ordinal < 0)
        {
            return false;
        }

        luid = Direct3D9Bridge.LuidOf(_direct3D!, (uint)ordinal);
        MonitorAdapters[monitor] = luid;
        NoteLocked($"monitor {monitor:X} is on {Direct3D9Bridge.DescribeAdapter(_direct3D!, (uint)ordinal)} [{GpuDeviceSet.FormatLuid(luid)}]");
        return true;
    }

    private static bool HasFailed(IntPtr monitor)
    {
        lock (Gate)
        {
            if (_direct3D is null)
            {
                return _direct3DFailed;
            }

            return MonitorAdapters.TryGetValue(monitor, out var luid) && FailedRecently(luid);
        }
    }

    /// <summary>Caller holds <see cref="Gate"/>.</summary>
    private static bool FailedRecently(Luid luid) =>
        Failures.TryGetValue(luid, out var failure) && TicksToTime(Clock.ElapsedTicks - failure.At) < RetryDelay(failure.Count);

    /// <summary>
    /// Five seconds after the first failure - long enough for a driver reset
    /// to finish - then twice as long after each one in a row, up to five
    /// minutes, so a card that can never be used is not set up again and
    /// again in the background.  A display change forgets them all.
    /// </summary>
    private static TimeSpan RetryDelay(int failuresInARow) =>
        TimeSpan.FromTicks(Math.Min(LongestRetryDelay.Ticks, RetryAfterFailure.Ticks << Math.Min(failuresInARow - 1, 10)));

    /// <summary>Caller holds <see cref="Gate"/>.</summary>
    private static void RecordFailure(Luid luid, long now) =>
        Failures[luid] = (now, Failures.TryGetValue(luid, out var earlier) ? earlier.Count + 1 : 1);

    private static TimeSpan TicksToTime(long stopwatchTicks) => TimeSpan.FromSeconds((double)stopwatchTicks / Stopwatch.Frequency);

    private static RendererPreference? ReadExplicitPreference()
    {
        var args = Environment.GetCommandLineArgs();
        for (var index = 1; index < args.Length; index++)
        {
            if (string.Equals(args[index], RendererSwitch, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                return ParsePreference(args[index + 1]);
            }

            if (args[index].StartsWith(RendererSwitch + "=", StringComparison.OrdinalIgnoreCase))
            {
                return ParsePreference(args[index][(RendererSwitch.Length + 1)..]);
            }
        }

        return ParsePreference(Environment.GetEnvironmentVariable(RendererVariable));
    }

    private static void Note(string message)
    {
        lock (Gate)
        {
            NoteLocked(message);
        }
    }

    private static void NoteLocked(string message)
    {
        RecentEvents.Enqueue($"{Clock.Elapsed.TotalMilliseconds,9:F1} ms  {message}");
        while (RecentEvents.Count > EventsKept)
        {
            RecentEvents.Dequeue();
        }
    }

    private const uint MonitorDefaultToPrimary = 1;
    private const uint MonitorDefaultToNearest = 2;
    private const int MonitorInfoPrimary = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
}

/// <summary>Which renderer draws the nested canvas.</summary>
internal enum RendererPreference
{
    /// <summary>The GPU where WPF itself is accelerated and a device set could be made; the CPU otherwise.</summary>
    Auto,

    /// <summary>The GPU wherever a device set can be made, even in a remote or software-rendered session.</summary>
    Gpu,

    /// <summary>Always the CPU raster: today's exact pixels.</summary>
    Cpu
}

/// <summary>
/// The answer of <see cref="GpuBootstrap.Decide"/>: the device set to draw
/// with, or null and why not.  The reasons are fixed strings, so asking
/// every frame allocates nothing.
/// </summary>
internal readonly record struct GpuDecision(GpuDeviceSet? DeviceSet, string Reason)
{
    public bool UseGpu => DeviceSet is not null;
}

/// <summary>One monitor and the card Direct3D 9 says drives it.</summary>
internal readonly record struct GpuMonitorAdapter(
    IntPtr Monitor,
    string DeviceName,
    Int32Rect Bounds,
    bool IsPrimary,
    int Direct3D9Adapter,
    Luid? AdapterLuid,
    string AdapterName);
