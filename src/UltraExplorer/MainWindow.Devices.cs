using System.Diagnostics;
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

    /// <summary>Completed when the drives are asked to be listed again while a listing is under way: what that listing stops waiting on, so the next starts at once.</summary>
    private TaskCompletionSource? _driveRescanWake;

    /// <summary>Lists the drives again once a mapped drive missing from them answers (<see cref="RetryMissingRemoteDrivesAsync"/>).</summary>
    private DispatcherTimer? _remoteDriveRetryTimer;

    /// <summary>Mapped drives the canvas keeps although they did not answer ready when the drives were last listed (<see cref="TakeDriveAnswer"/>).</summary>
    private readonly HashSet<string> _remoteDrivesWaiting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set when the drives changed before the nested canvas was ready: they are listed again once it is.</summary>
    private bool _nestedDrivesMissed;

    /// <summary>Starts listening for volumes: called with the rest of the nested canvas's wiring, before the window has a handle.</summary>
    private void AttachDevices()
    {
        _volumeNotifications = new VolumeNotifications(_viewModel.Changes, Dispatcher);
        SourceInitialized += OnSourceInitializedForDevices;
        Activated += OnActivatedForChanges;
        _remoteDriveRetryTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RemoteDriveRetryInterval };
        _remoteDriveRetryTimer.Tick += OnRemoteDriveRetryTick;
        _remoteDriveRetryTimer.Start();
    }

    private void DetachDevices()
    {
        SourceInitialized -= OnSourceInitializedForDevices;
        Activated -= OnActivatedForChanges;
        _remoteDriveRetryTimer?.Stop();
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
    ///
    /// <para>The pane and the canvas are asked at once, and the canvas takes
    /// each drive's answer as it comes (<see cref="TakeDriveAnswersAsync"/>):
    /// one after the other, each waiting for every drive, a stick plugged in
    /// beside a drive mapped to a server that is off reached the pane after
    /// the twenty seconds that drive takes to answer and the canvas after
    /// twenty more.  Asked again meanwhile, the listing under way stops
    /// waiting and the next starts at once.</para>
    /// </summary>
    private async Task RescanDrivesAsync()
    {
        if (_closeRequested) return;
        if (_driveRescanRunning)
        {
            _driveRescanAgain = true;
            _driveRescanWake?.TrySetResult();
            return;
        }

        _driveRescanRunning = true;
        try
        {
            do
            {
                _driveRescanAgain = false;
                var again = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _driveRescanWake = again;
                VolumeKinds.Invalidate();
                _viewModel.Changes.InvalidateVolumes();
                var pane = _viewModel.RefreshDrivesAsync();
                var canvasListed = false;
                if (!_nestedReady)
                {
                    // The canvas copies the drives as it starts, maybe before
                    // this change: it is listed again for once it is ready.
                    _nestedDrivesMissed = true;
                }
                else if (await TakeDriveAnswersAsync(new ViewAllFileSystemService().AskDriveRoots(), again.Task))
                {
                    canvasListed = true;
                }
                else
                {
                    if (_closeRequested) return;
                    continue;
                }

                if (_closeRequested) return;
                await Task.WhenAny(pane, again.Task);
                if (_closeRequested) return;
                if (!pane.IsCompleted)
                {
                    // Asked again: the newer listing replaces this one in the pane.
                    continue;
                }

                await pane;
                if (canvasListed)
                {
                    DriveRescans++;
                }
            }
            while (_driveRescanAgain);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A drive that went while it was being asked about: the next message lists them again.
        }
        finally
        {
            _driveRescanWake = null;
            _driveRescanRunning = false;
        }
    }

    /// <summary>
    /// Puts the nested canvas's drives right from <paramref name="asked"/>,
    /// one answer at a time: a letter no longer there - a stick pulled out -
    /// goes at once, with nothing to ask it; a drive that answers ready joins
    /// in name order, or is described afresh; one that answers not ready
    /// goes - unless it is a mapped drive whose server does not answer just
    /// now, a NAS rebooting, which keeps its cell and everything read in it,
    /// and is asked about again (<see cref="RetryMissingRemoteDrivesAsync"/>).
    /// False when the drives were asked to be listed again before every
    /// answer came: what is still to come is let go.  The panes are given
    /// the drives at least once, as every listing gave them.
    /// </summary>
    private async Task<bool> TakeDriveAnswersAsync(IReadOnlyList<(string Path, Task<ViewAllEntryDescriptor?> Answer)> asked, Task again)
    {
        var synced = false;
        _remoteDrivesWaiting.RemoveWhere(path => !asked.Any(question => ViewAllPath.Equals(question.Path, path)));
        if (_nestedDrives.RemoveAll(drive => !asked.Any(question => ViewAllPath.Equals(question.Path, drive.FullPath))) > 0)
        {
            SyncNestedRoots();
            synced = true;
        }

        var waiting = asked.ToList();
        while (waiting.Count > 0)
        {
            await Task.WhenAny(waiting.Select(question => (Task)question.Answer).Append(again));
            if (_closeRequested || again.IsCompleted)
            {
                return false;
            }

            var changed = false;
            for (var index = waiting.Count - 1; index >= 0; index--)
            {
                var (path, answer) = waiting[index];
                if (answer.IsCompleted)
                {
                    waiting.RemoveAt(index);
                    changed |= TakeDriveAnswer(path, await answer);
                }
            }

            if (changed)
            {
                SyncNestedRoots();
                synced = true;
            }
        }

        if (!synced)
        {
            SyncNestedRoots();
        }

        return true;
    }

    /// <summary>One drive's answer into the canvas's drives; whether they changed.</summary>
    private bool TakeDriveAnswer(string path, ViewAllEntryDescriptor? answer)
    {
        var at = _nestedDrives.FindIndex(drive => ViewAllPath.Equals(drive.FullPath, path));
        if (answer is null)
        {
            if (at < 0)
            {
                return false;
            }

            if (VolumeKinds.IsNetwork(path))
            {
                _remoteDrivesWaiting.Add(path);
                return false;
            }

            _nestedDrives.RemoveAt(at);
            return true;
        }

        _remoteDrivesWaiting.Remove(path);
        var drive = new NestedRoot(answer.FullPath, answer.DisplayName, NestedFolderKind.Drive, answer.SecondaryText);
        if (at >= 0)
        {
            if (_nestedDrives[at] == drive)
            {
                return false;
            }

            _nestedDrives[at] = drive;
            return true;
        }

        var place = _nestedDrives.FindIndex(existing => string.Compare(existing.FullPath, drive.FullPath, StringComparison.OrdinalIgnoreCase) > 0);
        _nestedDrives.Insert(place < 0 ? _nestedDrives.Count : place, drive);
        return true;
    }

    /// <summary>
    /// A mapped drive whose server did not answer when the drives were
    /// listed - at logon, before the network was up; while a NAS rebooted -
    /// was left out of the canvas and the pane until the next volume came or
    /// went, which for most people meant until the next start.  Every
    /// <see cref="RemoteDriveRetryInterval"/>, each mapped letter that neither
    /// the canvas nor the navigation pane has, or that the canvas keeps
    /// without an answer, is asked whether it is ready - which letters are
    /// mapped is told by the session's own table of mappings, without asking
    /// any server - and the drives are listed again once one is.  A letter
    /// the pane has answered when the drives were last listed, and is no
    /// reason to list them again: the checks give the canvas drives of their
    /// own beside the pane's, and a window they leave open would otherwise
    /// have its drives put back.  The question is shared by every window
    /// (<see cref="AskRemoteDrive"/>).
    /// </summary>
    private async Task RetryMissingRemoteDrivesAsync()
    {
        // A listing on its way already, or about to be: put off again, it
        // would never come while the drives were asked about more often than
        // a volume message waits for the next.
        if (_closeRequested || !_nestedReady || _driveRescanRunning || _driveRescanTimer?.IsEnabled == true)
        {
            return;
        }

        var listed = _nestedDrives.Select(drive => drive.FullPath).Concat(_viewModel.Drives.Select(drive => drive.Path)).ToArray();
        var waiting = _remoteDrivesWaiting.ToArray();
        var missing = await Task.Run(() => DriveInfo.GetDrives()
            .Where(drive =>
            {
                var path = ViewAllPath.Normalize(drive.Name);
                return VolumeKinds.IsNetwork(path)
                    && (!listed.Any(known => ViewAllPath.Equals(known, path)) || waiting.Any(known => ViewAllPath.Equals(known, path)));
            })
            .ToArray());
        if (missing.Length == 0 || _closeRequested)
        {
            return;
        }

        var answers = await Task.WhenAll(missing.Select(AskRemoteDrive));
        if (answers.Any(ready => ready) && !_closeRequested)
        {
            ScheduleDriveRescan();
        }
    }

    private void OnRemoteDriveRetryTick(object? sender, EventArgs e) => _ = RetryMissingRemoteDrivesAsync();

    /// <summary>The question put to each mapped letter left out, and when it was put: one per letter for every window, at most once an interval.</summary>
    private static readonly Dictionary<string, (Task<bool> Answer, long Asked)> RemoteDriveAnswers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a mapped drive answers ready, asked as the canvas asks a drive
    /// (<see cref="ViewAllFileSystemService.DescribeDrive"/>) on a thread of
    /// its own: one whose server is off takes some twenty seconds to say it
    /// is not.  Every window of the process shares the question - the answer
    /// on its way, or one given within the last interval - so a drive left
    /// out holds one thread at a time, however many windows are open.
    /// </summary>
    private static Task<bool> AskRemoteDrive(DriveInfo drive)
    {
        lock (RemoteDriveAnswers)
        {
            if (RemoteDriveAnswers.TryGetValue(drive.Name, out var known)
                && (!known.Answer.IsCompleted || Stopwatch.GetElapsedTime(known.Asked) < RemoteDriveRetryInterval))
            {
                return known.Answer;
            }

            var answer = Task.Run(() =>
            {
                try
                {
                    return ViewAllFileSystemService.DescribeDrive(drive) is not null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            });
            RemoteDriveAnswers[drive.Name] = (answer, Stopwatch.GetTimestamp());
            return answer;
        }
    }

    /// <summary>How often the drives are listed again while a mapped drive is missing from them; tests shorten it before a window is made.</summary>
    internal static TimeSpan RemoteDriveRetryInterval { get; set; } = TimeSpan.FromSeconds(60);

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
/// of the drive on screen.  A volume being locked or dismounted - Format,
/// chkdsk /f, Eject on a card - is told on the same handle (DBT_CUSTOMEVENT),
/// and the watch is suspended until the lock goes or the volume is mounted
/// again.  Watches are armed on any thread; the notifications are registered
/// on the window's.
/// </summary>
internal sealed class VolumeNotifications : IDisposable
{
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceQueryRemove = 0x8001;
    private const int DbtDeviceQueryRemoveFailed = 0x8002;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const int DbtCustomEvent = 0x8006;
    private const int DbtDevTypeVolume = 2;
    private const int DbtDevTypeHandle = 6;
    private const int DeviceNotifyWindowHandle = 0;

    // What locking, dismounting and mounting a volume tell those holding it (ioevent.h).
    private static readonly Guid VolumeLock = new("50708874-c9af-11d1-8fef-00a0c9a06d32");
    private static readonly Guid VolumeLockFailed = new("ae2eed10-0ba8-11d2-8ffb-00a0c9a06d32");
    private static readonly Guid VolumeUnlock = new("9a8c3d68-d0cb-11d1-8fef-00a0c9a06d32");
    private static readonly Guid VolumeDismount = new("d16a55e8-1059-11d2-8ffd-00a0c9a06d32");
    private static readonly Guid VolumeDismountFailed = new("e3c5b178-105d-11d2-8ffd-00a0c9a06d32");
    private static readonly Guid VolumeMount = new("b5804878-1a96-11d2-8ffd-00a0c9a06d32");

    private readonly ChangeHub _hub;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<IntPtr, WatchRoot> _byNotification = [];
    private readonly Dictionary<WatchRoot, Registration> _byRoot = [];
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
    internal IntPtr NotificationFor(WatchRoot root) => _byRoot.TryGetValue(root, out var registration) ? registration.Notification : IntPtr.Zero;

    /// <summary>
    /// The watch handle a root's notification was registered on, or null; for
    /// tests.  Compared by reference: Windows hands out the numbers of closed
    /// handles and of unregistered notifications again, so a number alone
    /// cannot tell a registration on a new handle from one left on the old.
    /// </summary>
    internal SafeHandle? RegisteredHandleFor(WatchRoot root) => _byRoot.TryGetValue(root, out var registration) ? registration.Handle : null;

    /// <summary>Notifications registered since this was made, for tests.</summary>
    internal int Registrations { get; private set; }

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

            case DbtCustomEvent:
                return OnVolumeEvent(root, handle.EventGuid);
        }

        return DeviceChange.None;
    }

    /// <summary>
    /// A custom event about a watched volume.  Locking a volume - Format,
    /// chkdsk /f, Eject on a card, BitLocker - or dismounting it tells those
    /// holding it first, and fails with "in use" while anything still does:
    /// the watch is suspended, which closes its handle before this returns,
    /// and the notification stays for what follows.  The lock failing or let
    /// go, the dismount failing, or the volume mounted again arms it again on
    /// a new handle - a watch suspended so, and no other: one that is up keeps
    /// its handle and its notification.
    /// </summary>
    private DeviceChange OnVolumeEvent(WatchRoot root, Guid kind)
    {
        if (kind == VolumeLock || kind == VolumeDismount)
        {
            _hub.Suspend(root);
            return DeviceChange.WatchSuspended;
        }

        if ((kind == VolumeLockFailed || kind == VolumeUnlock || kind == VolumeDismountFailed || kind == VolumeMount)
            && root.State == WatchState.Suspended)
        {
            Unregister(root);
            _hub.Rearm(root);
            return DeviceChange.WatchRearmed;
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
            // Asked again when it runs: Windows may have asked whether the
            // drive can go meanwhile, and the notification brings the answer.
            _dispatcher.BeginInvoke(() =>
            {
                if (root.State != WatchState.Suspended)
                {
                    Unregister(root);
                }
            });
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
                _byRoot[root] = new Registration(notification, handle);
                Registrations++;
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
        if (_byRoot.Remove(root, out var registration))
        {
            _byNotification.Remove(registration.Notification);
            UnregisterDeviceNotification(registration.Notification);
        }
    }

    /// <summary>A notification, and the watch handle it was registered on.</summary>
    private readonly record struct Registration(IntPtr Notification, SafeHandle Handle);

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
