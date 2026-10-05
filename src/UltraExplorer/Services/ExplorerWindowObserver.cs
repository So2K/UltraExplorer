using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using UltraExplorer.Picker.Com;
using static UltraExplorer.Services.ExplorerWindowInterop;

namespace UltraExplorer.Services;

/// <summary>A source window's identity and complete supported state. A recipient
/// must validate this again after its own folder and selection are ready.</summary>
internal sealed record ShellFolderSnapshot(
    nint RootHwnd,
    nint BrowserHwnd,
    nint ViewHwnd,
    uint SourceProcessId,
    DateTime SourceProcessStartUtc,
    uint SourceThreadId,
    string FolderPath,
    ReadOnlyMemory<byte> FolderPidl,
    IReadOnlyList<string> SelectedPaths,
    string? FocusedPath,
    long Generation)
{
    internal bool SameContentAndIdentity(ShellFolderSnapshot other) =>
        RootHwnd == other.RootHwnd && BrowserHwnd == other.BrowserHwnd && ViewHwnd == other.ViewHwnd
        && SourceProcessId == other.SourceProcessId && SourceProcessStartUtc == other.SourceProcessStartUtc
        && SourceThreadId == other.SourceThreadId && FolderPath == other.FolderPath
        && FolderPidl.Span.SequenceEqual(other.FolderPidl.Span)
        && SelectedPaths.SequenceEqual(other.SelectedPaths, StringComparer.Ordinal)
        && FocusedPath == other.FocusedPath;
}

/// <summary>Tests supply both the process and exact owned frame. Either member
/// alone would accidentally include an unrelated window in the same Explorer process.</summary>
internal sealed record ExplorerObserverScope(uint TestOnlyRootProcess, nint TestOnlyRootHwnd)
{
    internal bool Matches(uint process, nint root) =>
        TestOnlyRootProcess > 0 && TestOnlyRootHwnd != 0
        && process == TestOnlyRootProcess && root == TestOnlyRootHwnd;
}

/// <summary>A native browsing exception belongs to one frame lifetime, not to
/// explorer.exe as a whole, a folder path, or whichever tab is currently active.</summary>
internal readonly record struct ExplorerFrameIdentity(nint Root, uint Process, DateTime Started, uint Thread)
{
    internal static ExplorerFrameIdentity From(ShellFolderSnapshot snapshot) =>
        new(snapshot.RootHwnd, snapshot.SourceProcessId, snapshot.SourceProcessStartUtc, snapshot.SourceThreadId);
}

internal sealed class ExplorerNativeSessionPolicy
{
    private readonly ConcurrentDictionary<ExplorerFrameIdentity, byte> _native = new();
    private readonly Dictionary<ExplorerFrameIdentity, string> _folders = [];

    internal void ReconcileFrames(IReadOnlyCollection<ExplorerFrameIdentity> frames, bool baseline)
    {
        var current = frames.ToHashSet();
        foreach (var identity in _native.Keys.Where(identity => !current.Contains(identity))) _native.TryRemove(identity, out _);
        foreach (var identity in _folders.Keys.Where(identity => !current.Contains(identity)).ToArray()) _folders.Remove(identity);
        if (baseline) foreach (var identity in frames) Protect(identity);
    }

    internal void Protect(ExplorerFrameIdentity identity) => _native.TryAdd(identity, 0);
    internal bool IsNative(ShellFolderSnapshot snapshot) => IsNative(ExplorerFrameIdentity.From(snapshot));
    internal bool IsNative(ExplorerFrameIdentity identity) => _native.ContainsKey(identity);

    internal void ObserveFolder(ShellFolderSnapshot snapshot)
    {
        var identity = ExplorerFrameIdentity.From(snapshot);
        if (_folders.TryGetValue(identity, out var previous)
            && !string.Equals(previous, snapshot.FolderPath, StringComparison.OrdinalIgnoreCase)) Protect(identity);
        _folders[identity] = snapshot.FolderPath;
    }
}

/// <summary>Observes standalone filesystem Explorer windows from a dedicated STA.
/// It never hides, activates, moves, closes or otherwise writes to a source window.
/// Shell providers can be slow; callers should bound their wait and preserve the
/// source on timeout. Disposing cancels validation and further queued snapshots.</summary>
internal sealed class ExplorerWindowObserver : IDisposable
{
    private readonly ExplorerObserverScope? _scope;
    private readonly Thread _thread;
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<nint, ShellFolderSnapshot> _seen = new();
    private readonly Dictionary<nint, (ShellFolderSnapshot Snapshot, long At)> _pending = new();
    private readonly HashSet<(nint Root, long Generation)> _delivered = new();
    private readonly ExplorerNativeSessionPolicy _nativeSessions = new();
    private Action<ShellFolderSnapshot>? _callback;
    private DispatcherTimer? _timer;
    private object? _shell;
    private object? _shellWindows;
    private readonly List<nint> _hooks = [];
    private WinEventCallback? _hookCallback;
    private bool _initialized;
    private long _generation;
    private int _started;
    private int _disposed;
    private int _refreshQueued;
    private nint _foreground;
    private readonly Func<object>? _shellWindowsForChecks;
    private long _reopenAt;
    internal string? LastReadFailure { get; private set; }

    /// <param name="shellWindowsForChecks">Stands in for Explorer's ShellWindows,
    /// so a check can play an explorer.exe that restarts.</param>
    internal ExplorerWindowObserver(ExplorerObserverScope? scope = null, Func<object>? shellWindowsForChecks = null)
    {
        if (scope is not null && (scope.TestOnlyRootProcess == 0 || scope.TestOnlyRootHwnd == 0))
            throw new ArgumentException("A test scope needs both its process and exact owned window.", nameof(scope));
        _scope = scope;
        _shellWindowsForChecks = shellWindowsForChecks;
        _thread = new Thread(Run) { IsBackground = true, Name = "UltraExplorer Explorer observer" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>Existing windows form a native browsing baseline. Only new
    /// filesystem frames are eligible after a quiet interval. A native window
    /// keeps its exception through virtual folders, tabs and subsequent paths.
    /// Callback delivery is on this observer's STA, never a WinEvent callback.</summary>
    internal void Start(Action<ShellFolderSnapshot> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("The observer is already running.");
        _callback = callback;
        _thread.Start();
    }

    /// <summary>Returns read-only current eligible state, including the baseline.
    /// This does not invoke the handoff callback.</summary>
    internal async Task<IReadOnlyList<ShellFolderSnapshot>> RefreshAsync()
    {
        var dispatcher = await ReadyAsync().ConfigureAwait(false);
        return await dispatcher.InvokeAsync<IReadOnlyList<ShellFolderSnapshot>>(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return Array.Empty<ShellFolderSnapshot>();
            var current = ReadWindows();
            for (var index = 0; index < current.Count; index++)
            {
                var raw = current[index];
                var snapshot = _seen.TryGetValue(raw.RootHwnd, out var known) && raw.SameContentAndIdentity(known)
                    ? known : raw with { Generation = ++_generation };
                _seen[raw.RootHwnd] = current[index] = snapshot;
            }
            return Array.AsReadOnly(current.ToArray());
        }, DispatcherPriority.Background).Task.ConfigureAwait(false);
    }

    internal bool IsNativeSession(ShellFolderSnapshot snapshot) => _nativeSessions.IsNative(snapshot)
        || NativeExplorerSessionMarker.IsMarked(snapshot.RootHwnd, snapshot.SourceProcessId, snapshot.SourceProcessStartUtc);

    /// <summary>Re-reads the exact frame, tab, active view, process lifetime and
    /// content. False means the source must remain with Windows.</summary>
    internal async Task<bool> ValidateSnapshotAsync(ShellFolderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _started) == 0) return false;
        var dispatcher = await _ready.Task.ConfigureAwait(false);
        if (Volatile.Read(ref _disposed) != 0 || dispatcher.HasShutdownStarted) return false;
        try
        {
            return await dispatcher.InvokeAsync(() =>
            {
                if (Volatile.Read(ref _disposed) != 0
                    || !_seen.TryGetValue(snapshot.RootHwnd, out var known)
                    || known.Generation != snapshot.Generation) return false;
                var current = ReadWindows(snapshot.RootHwnd).SingleOrDefault();
                return current is not null && snapshot.SameContentAndIdentity(current);
            }, DispatcherPriority.Send).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return false; }
        catch (InvalidOperationException) when (Volatile.Read(ref _disposed) != 0) { return false; }
    }

    private async Task<Dispatcher> ReadyAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _started) == 0) throw new InvalidOperationException("Start the observer first.");
        return await _ready.Task.ConfigureAwait(false);
    }

    private void Run()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        try
        {
            _shellWindows = OpenShellWindows();
            _hookCallback = OnWinEvent;
            // Out-of-context is asynchronous and has no pre-paint guarantee.
            foreach (var range in new[] { (3u, 3u), (0x8000u, 0x8002u), (0x8006u, 0x8009u) })
            {
                var hook = SetWinEventHook(range.Item1, range.Item2, 0, _hookCallback, _scope?.TestOnlyRootProcess ?? 0, 0, 2);
                if (hook != 0) _hooks.Add(hook);
            }
            Reconcile();
            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
            _timer.Tick += (_, _) => Reconcile();
            _timer.Start();
            _ready.TrySetResult(dispatcher);
            if (Volatile.Read(ref _disposed) == 0) Dispatcher.Run();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ready.TrySetException(ex);
        }
        finally
        {
            _timer?.Stop();
            foreach (var hook in _hooks) UnhookWinEvent(hook);
            _hooks.Clear();
            _hookCallback = null;
            Release(_shellWindows);
            Release(_shell);
            _shellWindows = null;
            _shell = null;
        }
    }

    /// <summary>Explorer's ShellWindows: a proxy into explorer.exe, which dies with it.</summary>
    private object OpenShellWindows()
    {
        if (_shellWindowsForChecks is { } open) return open();
        _shell ??= Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!);
        return ((dynamic)_shell!).Windows();
    }

    /// <summary>Opens ShellWindows again after the last one was lost, at most
    /// every two seconds while explorer.exe is starting again.</summary>
    private void ReopenShellWindows()
    {
        var now = Environment.TickCount64;
        if (now < _reopenAt) return;
        _reopenAt = now + 2000;
        try { _shellWindows = OpenShellWindows(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Debug.WriteLine($"Explorer windows: {ex.Message}"); }
    }

    private void OnWinEvent(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (Volatile.Read(ref _disposed) != 0 || window == 0) return;
        var root = GetAncestor(window, 2);
        if (_scope is not null && root != _scope.TestOnlyRootHwnd) return;
        var className = ClassName(root);
        if (className is not ("CabinetWClass" or "ExploreWClass")) return;
        // No COM or enumeration here: event handlers can reenter and providers can block.
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Reconcile();
        });
    }

    private void Reconcile()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        // This inventory does not need a filesystem path or a ShellWindows
        // registration. A baseline This PC window therefore remains native
        // when the user subsequently enters an ordinary disk or folder.
        var frames = ReadFrames();
        _nativeSessions.ReconcileFrames(frames, baseline: !_initialized);
        // A frame that stays with Windows is never handed off. Reading its whole
        // selection again, item by item over COM every 300 ms, only loaded
        // Explorer's UI thread: tens of thousands of calls for 20,000 files.
        var native = frames.Where(frame => _nativeSessions.IsNative(frame)
            || NativeExplorerSessionMarker.IsMarked(frame.Root, frame.Process, frame.Started)).Select(frame => frame.Root).ToHashSet();
        var current = ReadWindows(skip: native).ToArray();
        var roots = current.Select(item => item.RootHwnd).ToHashSet();
        foreach (var root in _seen.Keys.Where(root => !roots.Contains(root)).ToArray())
        {
            _seen.Remove(root);
            _pending.Remove(root);
            _delivered.RemoveWhere(key => key.Root == root);
        }
        var foreground = GetAncestor(GetForegroundWindow(), 2);
        var now = Environment.TickCount64;
        foreach (var raw in current)
        {
            _nativeSessions.ObserveFolder(raw);
            var changed = !_seen.TryGetValue(raw.RootHwnd, out var previous) || !raw.SameContentAndIdentity(previous);
            var entered = _initialized && foreground == raw.RootHwnd && foreground != _foreground;
            var snapshot = changed || entered ? raw with { Generation = ++_generation } : previous!;
            _seen[raw.RootHwnd] = snapshot;
            if (IsNativeSession(snapshot))
            {
                _pending.Remove(raw.RootHwnd);
                continue;
            }
            if (_initialized && (changed || entered)) _pending[raw.RootHwnd] = (snapshot, now);
            if (!_initialized || !_pending.TryGetValue(raw.RootHwnd, out var pending)
                || now - pending.At < 250 || !pending.Snapshot.SameContentAndIdentity(snapshot)) continue;
            _pending.Remove(raw.RootHwnd);
            if (!_delivered.Add((snapshot.RootHwnd, snapshot.Generation))) continue;
            try { if (Volatile.Read(ref _disposed) == 0) _callback?.Invoke(snapshot); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Debug.WriteLine($"Explorer recipient: {ex}"); }
        }
        _foreground = foreground;
        _initialized = true;
    }

    private List<ExplorerFrameIdentity> ReadFrames()
    {
        var frames = new List<ExplorerFrameIdentity>();
        EnumWindows((root, _) =>
        {
            if (_scope is not null && root != _scope.TestOnlyRootHwnd) return true;
            if (ReadFrameIdentity(root) is { } identity) frames.Add(identity);
            return true;
        }, 0);
        return frames;
    }

    private ExplorerFrameIdentity? ReadFrameIdentity(nint root)
    {
        if (!IsWindow(root) || ClassName(root) is not ("CabinetWClass" or "ExploreWClass")) return null;
        var thread = GetWindowThreadProcessId(root, out var processId);
        if (thread == 0 || processId == 0 || (_scope is not null && !_scope.Matches(processId, root))) return null;
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            if (!string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) return null;
            var started = process.StartTime.ToUniversalTime();
            return IsWindow(root) && GetWindowThreadProcessId(root, out var currentProcess) == thread && currentProcess == processId
                ? new(root, processId, started, thread) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException)
        {
            return null;
        }
    }

    /// <param name="skip">Frames known to stay native: counted, never read.</param>
    private List<ShellFolderSnapshot> ReadWindows(nint onlyRoot = 0, IReadOnlySet<nint>? skip = null)
    {
        var snapshots = new List<ShellFolderSnapshot>();
        var registrations = new Dictionary<nint, int>();
        if (Volatile.Read(ref _disposed) != 0) return snapshots;
        if (_shellWindows is null) ReopenShellWindows();
        if (_shellWindows is null) return snapshots;
        try
        {
            dynamic windows = _shellWindows;
            int count = windows.Count;
            for (var index = 0; index < count && Volatile.Read(ref _disposed) == 0; index++)
            {
                object? dispatch = null;
                try
                {
                    dispatch = windows.Item(index);
                    if (dispatch is null) continue;
                    nint root = new(Convert.ToInt64(((dynamic)dispatch).HWND));
                    root = GetAncestor(root, 2);
                    if (root == 0 || (onlyRoot != 0 && root != onlyRoot)
                        || (_scope is not null && root != _scope.TestOnlyRootHwnd)) continue;
                    registrations[root] = registrations.GetValueOrDefault(root) + 1;
                    if (skip?.Contains(root) == true) continue;
                    if (ReadSnapshot(dispatch, root) is { } snapshot) snapshots.Add(snapshot);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    LastReadFailure = ex.Message;
                    Debug.WriteLine($"Explorer snapshot: {ex.Message}");
                }
                finally { Release(dispatch); }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"Explorer enumeration: {ex.Message}");
            // explorer.exe ended or restarted, and its ShellWindows with it: the
            // proxy stays dead, and no window would be adopted again until this
            // process restarted. The one running now is asked for instead.
            if (FolderShellNative.IsDisconnected(ex.HResult) || ex.InnerException is { } inner && FolderShellNative.IsDisconnected(inner.HResult))
            {
                Release(_shellWindows);
                Release(_shell);
                _shellWindows = null;
                _shell = null;
                ReopenShellWindows();
            }
        }
        // A frame can host several ShellWindows registrations, including virtual tabs.
        // Never hand off only its active tab and then let a recipient close the frame.
        snapshots.RemoveAll(item =>
        {
            if (registrations.GetValueOrDefault(item.RootHwnd) == 1) return false;
            _nativeSessions.Protect(ExplorerFrameIdentity.From(item));
            return true;
        });
        return snapshots;
    }

    private ShellFolderSnapshot? ReadSnapshot(object dispatch, nint root)
    {
        ShellFolderSnapshot? Refuse(string reason) { LastReadFailure = reason; return null; }
        if (!IsWindow(root) || !IsWindowVisible(root) || !IsWindowEnabled(root)
            || ClassName(root) is not ("CabinetWClass" or "ExploreWClass")) return Refuse("Not an enabled visible standalone Explorer frame.");
        var thread = GetWindowThreadProcessId(root, out var processId);
        if (thread == 0 || processId == 0 || (_scope is not null && !_scope.Matches(processId, root))) return null;
        DateTime started;
        using (var process = Process.GetProcessById(checked((int)processId)))
        {
            var executable = process.MainModule?.FileName;
            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            if (!string.Equals(executable, expected, StringComparison.OrdinalIgnoreCase)) return Refuse("The frame is not owned by the Windows Explorer executable.");
            started = process.StartTime.ToUniversalTime();
        }
        object? browserObject = null;
        ShellView? view = null;
        object? folderObject = null;
        object? selectionObject = null;
        nint folderPidl = 0;
        try
        {
            if (Convert.ToBoolean(((dynamic)dispatch).Busy)) return Refuse("Explorer is still navigating.");
            if (((ServiceProvider)dispatch).QueryService(TopLevelBrowser, ShellBrowserId, out browserObject) < 0) return Refuse("No top-level shell browser service.");
            var browser = (ShellBrowser)browserObject;
            if (browser.GetWindow(out var browserHwnd) < 0 || !IsWindow(browserHwnd)
                || GetAncestor(browserHwnd, 2) != root || !IsWindowVisible(browserHwnd)) return Refuse("The browser window is not an active child of this frame.");
            var tabWindows = 0;
            EnumChildWindows(root, (child, _) => { if (ClassName(child) == "ShellTabWindowClass") tabWindows++; return true; }, 0);
            if (tabWindows > 1)
            {
                _nativeSessions.Protect(new(root, processId, started, thread));
                return Refuse("The frame contains multiple tabs.");
            }
            var viewResult = browser.QueryActiveShellView(out view);
            if (viewResult < 0) return Refuse($"No active shell view: 0x{viewResult:X8}.");
            var viewWindowResult = view.GetWindow(out var viewHwnd);
            // Modern Explorer can hide its native DefView HWND while still
            // exposing it as the current Shell view. The API's active view,
            // its exact HWND/root and a second read establish its identity.
            if (viewWindowResult < 0 || !IsWindow(viewHwnd) || GetAncestor(viewHwnd, 2) != root)
                return Refuse("No valid active shell view belonging to this frame.");
            if (!SourceIsIdle(root, viewHwnd, thread))
            {
                _nativeSessions.Protect(new(root, processId, started, thread));
                return Refuse("Explorer has pending user input or a modal operation.");
            }
            var folderView = (FolderView)view;
            if (folderView.GetFolder(PersistFolderId, out folderObject) < 0
                || ((PersistFolder2)folderObject).GetCurFolder(out folderPidl) < 0 || folderPidl == 0) return Refuse("The current folder's absolute PIDL is unavailable.");
            var folderPath = FileSystemPath(folderPidl, mustBeFolder: true);
            if (folderPath is null || !Directory.Exists(folderPath))
            {
                _nativeSessions.Protect(new(root, processId, started, thread));
                return Refuse("The current namespace has no supported filesystem directory.");
            }
            var pidlSize = ILGetSize(folderPidl);
            if (pidlSize is < 2 or > 65536) return null;
            var pidlBytes = new byte[pidlSize];
            Marshal.Copy(folderPidl, pidlBytes, 0, pidlBytes.Length);
            if (folderView.ItemCount(Selection, out var selectedCount) < 0 || selectedCount < 0) return Refuse("The complete selection count is unavailable.");
            var selected = new List<string>(selectedCount);
            if (selectedCount > 0)
            {
                if (folderView.Items(Selection, ShellItemArrayId, out selectionObject) < 0) return Refuse("The complete shell selection is unavailable.");
                var array = (IShellItemArray)selectionObject;
                if (array.GetCount(out var count) < 0 || count != selectedCount) return Refuse("The selection changed during enumeration.");
                for (uint index = 0; index < count; index++)
                {
                    if (array.GetItemAt(index, out var item) < 0) return null;
                    try
                    {
                        var path = FileSystemPath(item, mustBeFolder: false);
                        if (path is null || !IsDirectChild(folderPath, path)) return Refuse("A selected namespace item cannot be represented in this physical folder.");
                        selected.Add(path);
                    }
                    finally { Release(item); }
                }
            }
            selected.Sort(StringComparer.Ordinal);
            string? focused = null;
            if (folderView.GetFocusedItem(out var focusIndex) >= 0 && focusIndex >= 0)
            {
                if (folderView.Item(focusIndex, out var childPidl) < 0 || childPidl == 0) return null;
                try
                {
                    var absolutePidl = ILCombine(folderPidl, childPidl);
                    if (absolutePidl == 0) return null;
                    try { focused = FileSystemPath(absolutePidl, mustBeFolder: false); }
                    finally { Marshal.FreeCoTaskMem(absolutePidl); }
                    if (focused is null || !IsDirectChild(folderPath, focused)) return Refuse("The focused namespace item cannot be represented in this physical folder.");
                }
                finally { Marshal.FreeCoTaskMem(childPidl); }
            }
            // Check the active view again: a tab/navigation change can happen during COM calls.
            if (browser.QueryActiveShellView(out var endView) < 0) return null;
            try { if (endView.GetWindow(out var endHwnd) < 0 || endHwnd != viewHwnd) return null; }
            finally { Release(endView); }
            if (!IsWindow(root) || GetWindowThreadProcessId(root, out var finalProcess) != thread || finalProcess != processId
                || Convert.ToBoolean(((dynamic)dispatch).Busy) || !SourceIsIdle(root, viewHwnd, thread)) return null;
            return new(root, browserHwnd, viewHwnd, processId, started, thread, folderPath, pidlBytes,
                selected.AsReadOnly(), focused, 0);
        }
        finally
        {
            if (folderPidl != 0) Marshal.FreeCoTaskMem(folderPidl);
            Release(selectionObject);
            Release(folderObject);
            Release(view);
            Release(browserObject);
        }
    }

    private static string? FileSystemPath(nint pidl, bool mustBeFolder)
    {
        if (SHCreateItemFromIDList(pidl, ShellItemId, out var item) < 0) return null;
        try { return FileSystemPath(item, mustBeFolder); }
        finally { Release(item); }
    }

    private static string? FileSystemPath(IShellItem item, bool mustBeFolder)
    {
        var needed = FileSystem | (mustBeFolder ? Folder : 0);
        if (item.GetAttributes(needed, out var attributes) < 0 || (attributes & needed) != needed
            || item.GetDisplayName(ShellItemDisplayName.FileSystemPath, out var name) < 0) return null;
        try
        {
            var path = Marshal.PtrToStringUni(name);
            return string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
                ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        finally { if (name != 0) Marshal.FreeCoTaskMem(name); }
    }

    internal static bool IsDirectChild(string folder, string path) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path) ?? string.Empty),
            Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase);

    /// <summary>Recheck immediately before any source Close. This reads only;
    /// disabled modal frames, menus/move-size, drag capture and unfinished
    /// inline/address/search input stay native.</summary>
    internal static bool SourceIsIdle(nint root, nint view, uint thread)
    {
        if (!IsWindow(root) || !IsWindowEnabled(root) || !IsWindow(view) || GetAncestor(view, 2) != root
            || GetWindowThreadProcessId(root, out _) != thread) return false;
        var info = new GuiThreadInfo { Size = checked((uint)Marshal.SizeOf<GuiThreadInfo>()) };
        if (!GetGUIThreadInfo(thread, ref info)) return false;
        var popup = GetLastActivePopup(root);
        var ownedPrompt = popup != 0 && popup != root && IsWindowVisible(popup);
        if (info.Capture != 0 && (info.Capture == root || IsChild(root, info.Capture))) return false;
        var withinSource = info.Focus != 0 && (info.Focus == root || IsChild(root, info.Focus));
        // The frame and the containers around the view (tab, DUI host) are
        // where focus rests while a new window settles; they are not chrome.
        var inChrome = withinSource && info.Focus != view && !IsChild(view, info.Focus) && !IsChild(info.Focus, view);
        return HasNoPendingInput(info.Flags, withinSource, ClassName(info.Focus), ownedPrompt, inChrome);
    }

    internal static bool HasNoPendingInput(uint guiFlags, bool focusWithinSource, string focusClass, bool visibleOwnedPrompt)
        => HasNoPendingInput(guiFlags, focusWithinSource, focusClass, visibleOwnedPrompt, focusInChrome: false);

    /// <param name="focusInChrome">Focus is in the frame, but neither in its active
    /// view nor on a window holding that view: the address bar, the search box (on this
    /// Explorer a DirectUIHWND under SearchEditBoxWrapperClass, not an Edit), the
    /// navigation or preview pane. The user is working in that window.</param>
    internal static bool HasNoPendingInput(uint guiFlags, bool focusWithinSource, string focusClass, bool visibleOwnedPrompt, bool focusInChrome)
    {
        if (visibleOwnedPrompt || (guiFlags & 0x1E) != 0) return false; // move-size or active menus
        if (focusWithinSource && focusInChrome) return false;
        return !focusWithinSource || !(focusClass.Equals("Edit", StringComparison.OrdinalIgnoreCase)
            || focusClass.Contains("RichEdit", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _callback = null;
        if (Volatile.Read(ref _started) == 0) return;
        if (_ready.Task.IsCompletedSuccessfully)
        {
            var dispatcher = _ready.Task.Result;
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }
        // Never join: a remote COM provider can be blocked. This thread is a
        // background thread, and the disposed flag prevents any eventual handoff.
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
}
