using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using UltraExplorer.Picker.Integration;

namespace UltraExplorer.Services;

/// <summary>Registers only UltraExplorer's own filesystem views with the Shell.
/// No foreign window, process, native module or global COM class is modified.</summary>
internal static class FolderShellWindowRegistration
{
    private static readonly List<WindowHost> Hosts = [];

    internal static void Attach(MainWindow window)
    {
        if (window.IsPickerMode || !ShellReplacementRegistration.IsAllowed || Hosts.Any(host => ReferenceEquals(host.Window, window))) return;
        var host = new WindowHost(window);
        Hosts.Add(host);
        window.Closed += (_, _) => { host.Dispose(); Hosts.Remove(host); };
    }

    internal static void Refresh(bool enabled)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Refresh(enabled));
            return;
        }
        foreach (var host in Hosts.ToArray()) host.Refresh(enabled);
    }

    private sealed class WindowHost : IDisposable
    {
        internal MainWindow Window { get; }
        private FolderShellViewRegistration? _registration;
        private readonly EventHandler _folderChanged;
        private readonly EventHandler _sourceInitialized;
        private readonly Action _settingsChanged;
        private readonly HwndSourceHook _messages;
        private HwndSource? _source;
        private bool _closed;

        internal WindowHost(MainWindow window)
        {
            Window = window;
            _messages = OnMessage;
            _folderChanged = (_, _) => Refresh(DialogIntegrationStore.Read().Enabled);
            _sourceInitialized = (_, _) => Refresh(DialogIntegrationStore.Read().Enabled);
            _settingsChanged = () => Refresh(DialogIntegrationStore.Read().Enabled);
            window.FolderLocationChanged += _folderChanged;
            window.SourceInitialized += _sourceInitialized;
            DialogIntegrationController.Shared.Changed += _settingsChanged;
            Refresh(DialogIntegrationStore.Read().Enabled);
        }

        internal void Refresh(bool enabled)
        {
            if (!enabled)
            {
                _registration?.Dispose();
                _registration = null;
                return;
            }
            var handle = new WindowInteropHelper(Window).Handle;
            if (handle == 0) return;
            if (_source is null && HwndSource.FromHwnd(handle) is { } source)
            {
                _source = source;
                source.AddHook(_messages);
            }
            _registration ??= new(handle, Window.Dispatcher, Window.ApplyFolderInvocationAsync);
            _ = NavigateAsync(_registration, Window.CurrentFolderPath);
        }

        /// <summary>explorer.exe started again: it tells every top-level window
        /// that its taskbar was created. The registration with the one that ended
        /// went with it (or one tried while it was away failed), and Show in folder
        /// would not find this window until it moved to another folder, so it is
        /// made again with the one running now. Not inside the message: a COM
        /// call cannot be made from a sent one.</summary>
        private nint OnMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (message == TaskbarCreated && TaskbarCreated != 0)
                Window.Dispatcher.BeginInvoke(() =>
                {
                    if (_closed) return;
                    _registration?.Dispose();
                    _registration = null;
                    Refresh(DialogIntegrationStore.Read().Enabled);
                });
            return 0;
        }

        /// <summary>The folder is looked up off this thread (see <see cref="FolderShellViewRegistration.NavigateAsync"/>);
        /// a registration the Shell refuses is let go and written down, and the next refresh starts another.</summary>
        private async Task NavigateAsync(FolderShellViewRegistration registration, string? folder)
        {
            try { await registration.NavigateAsync(folder); }
            catch (Exception exception) when (exception is COMException or ArgumentException or InvalidCastException)
            {
                registration.Dispose();
                if (ReferenceEquals(_registration, registration)) _registration = null;
                DialogIntegrationStore.Log("The filesystem view could not register with Windows Shell", exception);
            }
        }

        public void Dispose()
        {
            _closed = true;
            Window.FolderLocationChanged -= _folderChanged;
            Window.SourceInitialized -= _sourceInitialized;
            DialogIntegrationController.Shared.Changed -= _settingsChanged;
            _source?.RemoveHook(_messages);
            _source = null;
            _registration?.Dispose();
            _registration = null;
        }
    }

    /// <summary>The message explorer.exe sends every top-level window once its taskbar exists.</summary>
    private static readonly int TaskbarCreated = unchecked((int)RegisterWindowMessage("TaskbarCreated"));

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
}

/// <summary>A view's COM identity and PIDL live on its WPF STA. RegisterPending
/// precedes Register so a new folder launch can receive native selection calls.</summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class FolderShellViewRegistration : IFolderShellView, IDisposable
{
    private readonly nint _window;
    private readonly Dispatcher _dispatcher;
    private readonly Func<FolderInvocation, Task<bool>> _apply;
    private readonly DispatcherTimer _selectionTimer;
    private readonly List<string> _pendingSelection = [];
    private readonly FolderBrowserIdentity _browser;
    private IFolderShellWindows? _shellWindows;
    private nint _folderPidl;
    private string? _folder;
    private int? _cookie;
    private bool _disposed;
    private int _generation;

    /// <summary>The folder last asked for while it is not registered: being looked up, or waiting to be tried again.</summary>
    private string? _wanted;
    /// <summary>The look-up of <see cref="_wanted"/> under way, if one is.</summary>
    private Task<bool>? _lookup;
    /// <summary>Counts the folders asked for; a look-up that ends after another was asked for is let go.</summary>
    private int _request;
    private readonly DispatcherTimer _retryTimer;
    private TimeSpan _retryDelay = FirstRetry;
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LastRetry = TimeSpan.FromMinutes(1);

    internal string? Folder => _folder;
    internal bool IsRegistered => _cookie is not null;
    internal int LastResult { get; private set; } = FolderShellNative.Ok;
    internal Task<bool>? LastSelection { get; private set; }

    /// <summary>How a folder is asked whether it is there. A test puts a slow answer here
    /// to see that the UI thread does not wait for it.</summary>
    internal Func<string, bool> FolderExists { get; set; } = Directory.Exists;

    /// <summary>How Explorer's ShellWindows is reached. A test puts one here that
    /// plays an explorer.exe which has ended and started again.</summary>
    internal Func<IFolderShellWindows> OpenShellWindows { get; set; } = () =>
        (IFolderShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(FolderShellNative.ShellWindows, throwOnError: true)!)!;

    internal FolderShellViewRegistration(nint window, Dispatcher dispatcher, Func<FolderInvocation, Task<bool>> apply)
    {
        if (!dispatcher.CheckAccess() || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Shell views must be created on their window's STA.");
        _window = window;
        _dispatcher = dispatcher;
        _apply = apply;
        _browser = new(window, new FolderViewDocument(this), () => _folder);
        _selectionTimer = new(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(35) };
        _selectionTimer.Tick += (_, _) => FlushSelection();
        _retryTimer = new(DispatcherPriority.Background, dispatcher);
        _retryTimer.Tick += (_, _) => _ = RetryAsync();
    }

    /// <summary>
    /// Follows the window to <paramref name="folder"/> without this thread waiting on the disk: every
    /// window of the process shares it, and a share that has gone takes as long as the network allows
    /// to say so. Whether the folder is there, and its PIDL, are asked on a worker; only the PIDL comes
    /// back here, where the Shell registration lives. A folder that could not be registered is not
    /// looked up again each time the window refreshes: it is tried again by itself after a pause that
    /// doubles each time, from a second to a minute, for as long as the window stays there.
    /// </summary>
    internal Task<bool> NavigateAsync(string? folder)
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return Task.FromResult(false);
        if (Normalize(folder) is not { } target)
        {
            Forget();
            Revoke();
            return Task.FromResult(false);
        }
        if (_cookie is not null && Same(_folder, target)) return Task.FromResult(true);
        if (Same(_wanted, target)) return _lookup ?? Task.FromResult(false);
        Forget();
        Revoke();
        _wanted = target;
        return _lookup = LookUpAsync(target, _request);
    }

    /// <summary>The same as <see cref="NavigateAsync"/>, looking the folder up on this thread.</summary>
    internal bool Navigate(string? folder)
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return false;
        Forget();
        if (Normalize(folder) is not { } target)
        {
            Revoke();
            return false;
        }
        if (_cookie is not null && Same(_folder, target)) return true;
        if (!FolderExists(target)) { Revoke(); return false; }
        Revoke();
        return Register(target, Parse(target));
    }

    private static string? Normalize(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) return null;
        folder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        if (folder.Length == 2 && folder[1] == ':') folder += Path.DirectorySeparatorChar;
        return folder;
    }

    private static bool Same(string? folder, string other) => string.Equals(folder, other, StringComparison.OrdinalIgnoreCase);

    /// <summary>Lets go of the folder waiting to be registered: a look-up still under way is dropped when it ends.</summary>
    private void Forget()
    {
        ++_request;
        _wanted = null;
        _lookup = null;
        _retryTimer.Stop();
        _retryDelay = FirstRetry;
    }

    /// <summary>Looks <paramref name="folder"/> up on a worker - null when it is not there - and hands the answer to this STA.</summary>
    private async Task<bool> LookUpAsync(string folder, int request)
    {
        var exists = FolderExists;
        var found = await Task.Run(() => exists(folder) ? Parse(folder) : (Parsed?)null).ConfigureAwait(false);
        try { return await _dispatcher.InvokeAsync(() => Take(folder, request, found)); }
        catch (OperationCanceledException)
        {
            // The window's thread has ended; the PIDL never reached it.
            if (found is { Pidl: not 0 } parsed) Marshal.FreeCoTaskMem(parsed.Pidl);
            return false;
        }
    }

    /// <summary>A look-up's answer, on this STA: registered if its folder is still the one asked for,
    /// otherwise let go. A folder that could not be registered is tried again later.</summary>
    private bool Take(string folder, int request, Parsed? found)
    {
        if (_disposed || request != _request)
        {
            if (found is { Pidl: not 0 } parsed) Marshal.FreeCoTaskMem(parsed.Pidl);
            return false;
        }
        _lookup = null;
        var registered = false;
        try { return registered = found is { } there && Register(folder, there); }
        finally
        {
            if (registered) Forget();
            else RetryLater();
        }
    }

    private void RetryLater()
    {
        if (_disposed || _wanted is null) return;
        _retryTimer.Interval = _retryDelay;
        _retryDelay = _retryDelay * 2 < LastRetry ? _retryDelay * 2 : LastRetry;
        _retryTimer.Start();
    }

    private async Task RetryAsync()
    {
        _retryTimer.Stop();
        if (_disposed || _cookie is not null || _lookup is not null || _wanted is not { } folder) return;
        try { await (_lookup = LookUpAsync(folder, _request)); }
        catch (Exception exception) when (exception is COMException or ArgumentException or InvalidCastException)
        {
            DialogIntegrationStore.Log("The filesystem view could not register with Windows Shell", exception);
        }
    }

    /// <summary>The PIDL of a folder that is there, or none for one that is not a filesystem folder. Asks the disk.</summary>
    private static Parsed Parse(string folder)
    {
        var result = FolderShellNative.SHParseDisplayName(folder, 0, out var pidl, 0x60000000, out var attributes);
        if (result < 0 || pidl == 0) return new(result, 0);
        if ((attributes & 0x60000000) != 0x60000000)
        {
            Marshal.FreeCoTaskMem(pidl);
            return new(result, 0);
        }
        return new(result, pidl);
    }

    private readonly record struct Parsed(int Result, nint Pidl);

    private bool Register(string folder, Parsed parsed)
    {
        LastResult = parsed.Result;
        if (LastResult < 0 || parsed.Pidl == 0) return false;
        _folderPidl = parsed.Pidl;
        _folder = folder;
        try
        {
            _shellWindows ??= OpenShellWindows();
            object location = FolderShellNative.PidlVariant(_folderPidl);
            object? empty = null;
            LastResult = _shellWindows.RegisterPending(unchecked((int)FolderShellNative.GetCurrentThreadId()), ref location, ref empty, 1, out var pending);
            if (LastResult < 0) return Refused();
            _cookie = pending;
            LastResult = _shellWindows.Register(_browser, unchecked((int)_window), 1, out var registered);
            if (LastResult < 0) return Refused();
            if (pending != registered) _shellWindows.Revoke(pending);
            _cookie = registered;
            LastResult = _shellWindows.OnNavigate(registered, ref location);
            if (LastResult < 0) return Refused();
            return true;
        }
        catch
        {
            Revoke();
            throw;
        }

        bool Refused()
        {
            Revoke();
            // explorer.exe ended or restarted and its ShellWindows with it. The
            // proxy refuses every later call too; the retry opens the one running now.
            if (FolderShellNative.IsDisconnected(LastResult)) ReleaseShellWindows();
            return false;
        }
    }

    private void ReleaseShellWindows()
    {
        if (_shellWindows is not null && Marshal.IsComObject(_shellWindows)) Marshal.ReleaseComObject(_shellWindows);
        _shellWindows = null;
    }

    private void Revoke()
    {
        ++_generation;
        _selectionTimer.Stop();
        _pendingSelection.Clear();
        if (_cookie is { } cookie)
        {
            _cookie = null;
            try { _shellWindows?.Revoke(cookie); }
            catch (COMException) { }
        }
        _folder = null;
        if (_folderPidl != 0) { Marshal.FreeCoTaskMem(_folderPidl); _folderPidl = 0; }
    }

    public int SelectItem(nint relativeItem, uint flags)
    {
        // Managed COM callable wrappers can receive RPC on another apartment.
        // Copy the borrowed PIDL before dispatching; a timeout must never leave
        // a queued callback reading a pointer whose caller has already returned.
        if (!_dispatcher.CheckAccess())
        {
            if (_disposed || _dispatcher.HasShutdownStarted || relativeItem == 0) return FolderShellNative.Fail;
            var bytes = (byte[])FolderShellNative.PidlVariant(relativeItem);
            try
            {
                return _dispatcher.Invoke(() =>
                {
                    var copy = Marshal.AllocCoTaskMem(bytes.Length);
                    try { Marshal.Copy(bytes, 0, copy, bytes.Length); return SelectItem(copy, flags); }
                    finally { Marshal.FreeCoTaskMem(copy); }
                }, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or InvalidOperationException)
            { return FolderShellNative.Fail; }
        }
        // Unsupported edit, positioning, check state and no-focus operations
        // retain their failure HRESULT rather than falsely reporting success.
        const uint supported = 1 | 4 | 8 | 16 | 64;
        if (_disposed || _cookie is null || _folderPidl == 0 || _folder is null || !_dispatcher.CheckAccess()) return FolderShellNative.Fail;
        if (relativeItem == 0 || (flags & 1) == 0 || (flags & ~supported) != 0) return FolderShellNative.NotImplemented;
        nint absolute = 0;
        try
        {
            absolute = FolderShellNative.ILCombine(_folderPidl, relativeItem);
            if (absolute == 0) return FolderShellNative.Fail;
            var path = FolderShellNative.FileSystemPath(absolute);
            if (path is null || !string.Equals(Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar),
                    _folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return FolderShellNative.NotImplemented;
            if ((flags & 4) != 0) _pendingSelection.Clear();
            if (!_pendingSelection.Contains(path, StringComparer.OrdinalIgnoreCase)) _pendingSelection.Add(path);
            if ((flags & 16) != 0)
            {
                _pendingSelection.Remove(path);
                _pendingSelection.Insert(0, path);
            }
            _selectionTimer.Stop();
            _selectionTimer.Start();
            return FolderShellNative.Ok;
        }
        catch (Exception exception) when (exception is ArgumentException or COMException)
        { return Marshal.GetHRForException(exception); }
        finally { if (absolute != 0) Marshal.FreeCoTaskMem(absolute); }
    }

    private void FlushSelection()
    {
        _selectionTimer.Stop();
        if (_disposed || _cookie is null || _folder is null || _pendingSelection.Count == 0) return;
        var invocation = new FolderInvocation(FolderInvocationKind.Reveal, _folder, _pendingSelection.ToArray(), OriginIsShell: true);
        _pendingSelection.Clear();
        var generation = _generation;
        LastSelection = ApplySelectionAsync(invocation, generation);
    }

    private async Task<bool> ApplySelectionAsync(FolderInvocation invocation, int generation)
    {
        try { return !_disposed && generation == _generation && await _apply(invocation); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DialogIntegrationStore.Log("The Shell selection could not be applied", exception);
            return false;
        }
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        Forget();
        Revoke();
        _disposed = true;
        ReleaseShellWindows();
    }

    public int GetWindow(out nint window) { window = _window; return FolderShellNative.Ok; }
    public int ContextSensitiveHelp(bool enter) => FolderShellNative.NotImplemented;
    public int TranslateAccelerator(nint message) => FolderShellNative.NotImplemented;
    public int EnableModeless(bool enable) => FolderShellNative.NotImplemented;
    public int UIActivate(uint state) => FolderShellNative.NotImplemented;
    public int Refresh() => FolderShellNative.NotImplemented;
    public int CreateViewWindow(nint previous, nint settings, nint browser, nint rectangle, out nint window) { window = 0; return FolderShellNative.NotImplemented; }
    public int DestroyViewWindow() => FolderShellNative.NotImplemented;
    public int GetCurrentInfo(nint settings) => FolderShellNative.NotImplemented;
    public int AddPropertySheetPages(uint reserved, nint callback, nint parameter) => FolderShellNative.NotImplemented;
    public int SaveViewState() => FolderShellNative.NotImplemented;
    public int GetItemObject(uint item, ref Guid requestedInterface, out nint result) { result = 0; return FolderShellNative.NoInterface; }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.AutoDispatch)]
public sealed class FolderViewDocument : IFolderServiceProvider
{
    private readonly FolderShellViewRegistration _view;
    internal FolderViewDocument(FolderShellViewRegistration view) => _view = view;
    public int QueryService(ref Guid service, ref Guid requestedInterface, out nint result)
    {
        result = 0;
        if (service != FolderShellNative.FolderView || requestedInterface != FolderShellNative.ShellView || !_view.IsRegistered)
            return FolderShellNative.NoInterface;
        result = Marshal.GetComInterfaceForObject(_view, typeof(IFolderShellView));
        return FolderShellNative.Ok;
    }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None), ComDefaultInterface(typeof(IFolderWebBrowserApp))]
public sealed class FolderBrowserIdentity : IFolderWebBrowserApp
{
    private readonly nint _window;
    private readonly FolderViewDocument _document;
    private readonly Func<string?> _folder;
    internal FolderBrowserIdentity(nint window, FolderViewDocument document, Func<string?> folder)
    { _window = window; _document = document; _folder = folder; }
    public int get_Document(out object? value) { value = _document; return FolderShellNative.Ok; }
    public int get_HWND(out nint value) { value = _window; return FolderShellNative.Ok; }
    public int get_FullName(out string? value) { value = Environment.ProcessPath; return FolderShellNative.Ok; }
    public int get_Path(out string? value) { value = Path.GetDirectoryName(Environment.ProcessPath); return FolderShellNative.Ok; }
    public int get_Name(out string? value) { value = "UltraExplorer"; return FolderShellNative.Ok; }
    public int get_LocationName(out string? value) { value = _folder(); return FolderShellNative.Ok; }
    public int get_LocationURL(out string? value) { value = _folder() is { } path ? new Uri(path).AbsoluteUri : null; return FolderShellNative.Ok; }
    public int get_Busy(out bool value) { value = false; return FolderShellNative.Ok; }
    public int get_Visible(out bool value) { value = true; return FolderShellNative.Ok; }
    public int get_TopLevelContainer(out bool value) { value = true; return FolderShellNative.Ok; }
    public int GoBack() => FolderShellNative.NotImplemented;
    public int GoForward() => FolderShellNative.NotImplemented;
    public int GoHome() => FolderShellNative.NotImplemented;
    public int GoSearch() => FolderShellNative.NotImplemented;
    public int Navigate(string url, nint flags, nint frame, nint data, nint headers) => FolderShellNative.NotImplemented;
    public int Refresh() => FolderShellNative.NotImplemented;
    public int Refresh2(nint level) => FolderShellNative.NotImplemented;
    public int Stop() => FolderShellNative.NotImplemented;
    public int get_Application(out object? value) { value = null; return FolderShellNative.NotImplemented; }
    public int get_Parent(out object? value) { value = null; return FolderShellNative.NotImplemented; }
    public int get_Container(out object? value) { value = null; return FolderShellNative.NotImplemented; }
    public int get_Type(out string? value) { value = null; return FolderShellNative.NotImplemented; }
    public int get_Left(out int value) { value = 0; return FolderShellNative.NotImplemented; }
    public int put_Left(int value) => FolderShellNative.NotImplemented;
    public int get_Top(out int value) { value = 0; return FolderShellNative.NotImplemented; }
    public int put_Top(int value) => FolderShellNative.NotImplemented;
    public int get_Width(out int value) { value = 0; return FolderShellNative.NotImplemented; }
    public int put_Width(int value) => FolderShellNative.NotImplemented;
    public int get_Height(out int value) { value = 0; return FolderShellNative.NotImplemented; }
    public int put_Height(int value) => FolderShellNative.NotImplemented;
    public int Quit() => FolderShellNative.NotImplemented;
    public int ClientToWindow(ref int width, ref int height) => FolderShellNative.NotImplemented;
    public int PutProperty(string name, object value) => FolderShellNative.NotImplemented;
    public int GetProperty(string name, out object? value) { value = null; return FolderShellNative.NotImplemented; }
    public int put_Visible(bool value) => FolderShellNative.NotImplemented;
    public int get_StatusBar(out bool value) { value = false; return FolderShellNative.NotImplemented; }
    public int put_StatusBar(bool value) => FolderShellNative.NotImplemented;
    public int get_StatusText(out string? value) { value = null; return FolderShellNative.NotImplemented; }
    public int put_StatusText(string value) => FolderShellNative.NotImplemented;
    public int get_ToolBar(out int value) { value = 0; return FolderShellNative.NotImplemented; }
    public int put_ToolBar(int value) => FolderShellNative.NotImplemented;
    public int get_MenuBar(out bool value) { value = false; return FolderShellNative.NotImplemented; }
    public int put_MenuBar(bool value) => FolderShellNative.NotImplemented;
    public int get_FullScreen(out bool value) { value = false; return FolderShellNative.NotImplemented; }
    public int put_FullScreen(bool value) => FolderShellNative.NotImplemented;
}
