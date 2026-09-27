using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;

namespace UltraExplorer;

// Devices: volumes arriving and leaving, and a volume being asked whether it
// may go.  A USB stick plugged in shows up as a drive on the nested canvas and
// in the navigation pane; one pulled out goes from both.  And "Safely remove"
// works with a folder of the drive on screen: the change hub's watch holds a
// handle open on the drive's root, which would veto the removal, so the
// window asks Windows to say when the drive is about to go, closes the watch
// there and then, and opens it again if the removal is called off.
public partial class MainWindow
{
    /// <summary>How long after the last volume message the drives are listed again: a stick arriving is several messages.</summary>
    private static readonly TimeSpan DriveRescanDelay = TimeSpan.FromMilliseconds(400);

    private VolumeNotifications? _volumeNotifications;
    private DispatcherTimer? _driveRescanTimer;
    private bool _driveRescanRunning;
    private bool _driveRescanAgain;

    /// <summary>Starts listening for volumes: called with the rest of the nested canvas's wiring, before the window has a handle.</summary>
    private void AttachDevices()
    {
        _volumeNotifications = new VolumeNotifications(_viewModel.Changes, Dispatcher);
        SourceInitialized += OnSourceInitializedForDevices;
        Activated += OnActivatedForChanges;
    }

    private void DetachDevices()
    {
        SourceInitialized -= OnSourceInitializedForDevices;
        Activated -= OnActivatedForChanges;
        _driveRescanTimer?.Stop();
        _volumeNotifications?.Dispose();
        _volumeNotifications = null;
    }

    private void OnSourceInitializedForDevices(object? sender, EventArgs e) =>
        _volumeNotifications?.SetWindow(new WindowInteropHelper(this).Handle);

    /// <summary>
    /// Coming back to the window is when a change made somewhere else is most
    /// likely to be looked for: what is polled rather than watched - a volume
    /// that refuses a watch, a share whose watch is down - is looked at now
    /// rather than at its next turn.
    /// </summary>
    private void OnActivatedForChanges(object? sender, EventArgs e) => _viewModel.Changes.PollNow();

    /// <summary>
    /// WM_DEVICECHANGE.  A volume arriving or leaving has the drives listed
    /// again a moment later; a message about a watched drive's handle - about
    /// to go, staying after all, gone - goes to the watch.  Never handled:
    /// Windows' own answer to "may it go?" is yes, and by the time that answer
    /// is given the watch's handle is closed.
    /// </summary>
    partial void OnDeviceChangeMessage(IntPtr wParam, IntPtr lParam, ref bool handled, ref IntPtr result)
    {
        if (_volumeNotifications?.OnDeviceChange((int)wParam.ToInt64(), lParam) == DeviceChange.VolumesChanged)
        {
            ScheduleDriveRescan();
        }
    }

    private void ScheduleDriveRescan()
    {
        if (_driveRescanTimer is null)
        {
            _driveRescanTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = DriveRescanDelay };
            _driveRescanTimer.Tick += async (_, _) =>
            {
                _driveRescanTimer.Stop();
                await RescanDrivesAsync();
            };
        }

        _driveRescanTimer.Stop();
        _driveRescanTimer.Start();
    }

    /// <summary>
    /// Lists the drives again: the navigation pane's, and the nested canvas's
    /// row of drive cells, named as they were named at startup.  A drive kept
    /// keeps its cell with everything read in it; a new one appears as an
    /// unread cell; one gone is dropped.  The kinds of volume behind the
    /// letters are looked up afresh too, since a letter can now be something
    /// else.
    /// </summary>
    private async Task RescanDrivesAsync()
    {
        if (_driveRescanRunning)
        {
            _driveRescanAgain = true;
            return;
        }

        _driveRescanRunning = true;
        try
        {
            do
            {
                _driveRescanAgain = false;
                VolumeKinds.Invalidate();
                _viewModel.Changes.InvalidateVolumes();
                await _viewModel.RefreshDrivesAsync();
                if (!_nestedReady)
                {
                    continue;
                }

                var roots = await new ViewAllFileSystemService().GetDriveRootsAsync();
                _nestedDrives.Clear();
                _nestedDrives.AddRange(roots.Select(root => new NestedRoot(root.FullPath, root.DisplayName, NestedFolderKind.Drive, root.SecondaryText)));
                SyncNestedRoots();
                DriveRescans++;
            }
            while (_driveRescanAgain);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A drive that went while it was being asked about: the next message lists them again.
        }
        finally
        {
            _driveRescanRunning = false;
        }
    }

    /// <summary>Times the drives were listed again for a volume arriving or leaving.</summary>
    internal int DriveRescans { get; private set; }
}

/// <summary>What a WM_DEVICECHANGE meant to the window.</summary>
internal enum DeviceChange
{
    None,

    /// <summary>A volume arrived or left: the drives are to be listed again.</summary>
    VolumesChanged,

    /// <summary>A watched drive is about to go, and its watch was closed.</summary>
    WatchSuspended,

    /// <summary>The removal was called off, and the watch opened again.</summary>
    WatchRearmed,

    /// <summary>The drive went, and its watch was dropped.</summary>
    WatchDropped
}

/// <summary>
/// The window's side of a watch holding a drive open.  Windows vetoes the
/// removal of a volume while anything holds a handle on it, so for every
/// local watch the hub arms, a notification is registered on the watch's own
/// handle: when the drive is about to go (DBT_DEVICEQUERYREMOVE) the watch is
/// suspended, which closes the handle; if the removal is called off
/// (DBT_DEVICEQUERYREMOVEFAILED) it is armed again; when the drive has gone
/// (DBT_DEVICEREMOVECOMPLETE) the watch is dropped.  FileSystemWatcher never
/// did the first of these, which is why "Safely remove" failed with a folder
/// of the drive on screen.  Watches are armed on any thread; the
/// notifications are registered on the window's.
/// </summary>
internal sealed class VolumeNotifications : IDisposable
{
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceQueryRemove = 0x8001;
    private const int DbtDeviceQueryRemoveFailed = 0x8002;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const int DbtDevTypeVolume = 2;
    private const int DbtDevTypeHandle = 6;
    private const int DeviceNotifyWindowHandle = 0;

    private readonly ChangeHub _hub;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<IntPtr, WatchRoot> _byNotification = [];
    private readonly Dictionary<WatchRoot, IntPtr> _byRoot = [];
    private readonly List<WatchRoot> _waitingForWindow = [];
    private IntPtr _window;
    private bool _disposed;

    public VolumeNotifications(ChangeHub hub, Dispatcher dispatcher)
    {
        _hub = hub;
        _dispatcher = dispatcher;
        _hub.RootArmed += OnRootArmed;
        _hub.RootClosed += OnRootClosed;
    }

    /// <summary>Watches with a notification registered right now, for tests.</summary>
    internal int RegisteredCount => _byRoot.Count;

    /// <summary>The notification registered on a watch's handle, or zero; for tests, which hand it back in the messages they make up.</summary>
    internal IntPtr NotificationFor(WatchRoot root) => _byRoot.GetValueOrDefault(root);

    /// <summary>The window the notifications are sent to; watches armed before it had one are registered now.</summary>
    public void SetWindow(IntPtr window)
    {
        _window = window;
        foreach (var root in _waitingForWindow)
        {
            Register(root);
        }

        _waitingForWindow.Clear();
    }

    /// <summary>
    /// A WM_DEVICECHANGE, on the window's thread: <paramref name="kind"/> is its
    /// wParam, <paramref name="data"/> its lParam - a DEV_BROADCAST_HDR, or
    /// nothing.  Says what it meant; the watches it concerns are dealt with here.
    /// </summary>
    public DeviceChange OnDeviceChange(int kind, IntPtr data)
    {
        if (data == IntPtr.Zero || _disposed)
        {
            return DeviceChange.None;
        }

        var header = Marshal.PtrToStructure<BroadcastHeader>(data);
        if (header.DeviceType == DbtDevTypeVolume)
        {
            return kind is DbtDeviceArrival or DbtDeviceRemoveComplete ? DeviceChange.VolumesChanged : DeviceChange.None;
        }

        if (header.DeviceType != DbtDevTypeHandle)
        {
            return DeviceChange.None;
        }

        var handle = Marshal.PtrToStructure<BroadcastHandle>(data);
        if (!_byNotification.TryGetValue(handle.Notification, out var root))
        {
            return DeviceChange.None;
        }

        switch (kind)
        {
            case DbtDeviceQueryRemove:
                // The handle must be closed before this returns, or the
                // removal is refused.  The notification stays registered: it
                // still brings the outcome.
                _hub.Suspend(root);
                return DeviceChange.WatchSuspended;

            case DbtDeviceQueryRemoveFailed:
                // Armed again on a new handle, which brings a notification of
                // its own; this one is done with.
                Unregister(root);
                _hub.Rearm(root);
                return DeviceChange.WatchRearmed;

            case DbtDeviceRemoveComplete:
                Unregister(root);
                _hub.Drop(root);
                return DeviceChange.WatchDropped;
        }

        return DeviceChange.None;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hub.RootArmed -= OnRootArmed;
        _hub.RootClosed -= OnRootClosed;
        foreach (var root in _byRoot.Keys.ToArray())
        {
            Unregister(root);
        }
    }

    private void OnRootArmed(WatchRoot root)
    {
        // Only a local volume can be removed; a share's handle has nothing to say.
        if (root.Kind != WatchKind.Local)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            Register(root);
        }
        else
        {
            _dispatcher.BeginInvoke(() => Register(root));
        }
    }

    private void OnRootClosed(WatchRoot root)
    {
        // Suspended for a removal, the notification is still wanted for the outcome.
        if (root.State == WatchState.Suspended)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            Unregister(root);
        }
        else
        {
            _dispatcher.BeginInvoke(() => Unregister(root));
        }
    }

    private void Register(WatchRoot root)
    {
        if (_disposed)
        {
            return;
        }

        if (_window == IntPtr.Zero)
        {
            if (!_waitingForWindow.Contains(root))
            {
                _waitingForWindow.Add(root);
            }

            return;
        }

        if (root.Handle is not { IsInvalid: false, IsClosed: false } handle)
        {
            return;
        }

        Unregister(root);
        var filter = new BroadcastHandle
        {
            Size = Marshal.SizeOf<BroadcastHandle>(),
            DeviceType = DbtDevTypeHandle,
            Handle = handle.DangerousGetHandle()
        };
        var buffer = Marshal.AllocHGlobal(filter.Size);
        try
        {
            Marshal.StructureToPtr(filter, buffer, fDeleteOld: false);
            var notification = RegisterDeviceNotification(_window, buffer, DeviceNotifyWindowHandle);
            if (notification != IntPtr.Zero)
            {
                _byNotification[notification] = root;
                _byRoot[root] = notification;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void Unregister(WatchRoot root)
    {
        _waitingForWindow.Remove(root);
        if (_byRoot.Remove(root, out var notification))
        {
            _byNotification.Remove(notification);
            UnregisterDeviceNotification(notification);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BroadcastHeader
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
    }

    /// <summary>DEV_BROADCAST_HANDLE.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct BroadcastHandle
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
        public IntPtr Handle;
        public IntPtr Notification;
        public Guid EventGuid;
        public int NameOffset;
        public byte Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, IntPtr filter, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterDeviceNotification(IntPtr notification);
}
