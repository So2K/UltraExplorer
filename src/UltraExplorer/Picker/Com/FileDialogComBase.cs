using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace UltraExplorer.Picker.Com;

/// <summary>
/// UltraExplorer behind the standard <c>IFileDialog</c> vtable.  A program that
/// already talks to the Windows dialog only has to create a different CLSID:
/// every call it knows lands on the same request object the command line builds,
/// and the window that appears is the ordinary UltraExplorer canvas.
///
/// Every call arrives on the apartment thread that registered the class, which
/// is also the thread the window lives on, so nothing here marshals.
/// </summary>
internal abstract class FileDialogComBase
{
    private readonly FileDialogRequest _request = new() { Options = FileDialogOptions.None };
    private readonly Dictionary<uint, IFileDialogEvents> _sinks = [];

    private readonly ConcurrentQueue<Action> _callerWork = new();

    private FileDialogSession? _session;
    private MainWindow? _window;
    private volatile bool _dialogRunning;
    private FileDialogResult _result = FileDialogResult.Cancelled();
    private uint _nextCookie = 1;
    private int _closeResult;
    private bool _closeRequested;

    protected FileDialogRequest Request => _request;

    /// <summary>
    /// A managed COM object is agile, so calls arrive on whichever RPC thread
    /// COM happens to use rather than on the apartment that registered the
    /// class.  Anything that touches the window has to be moved here first.
    /// </summary>
    private static Dispatcher Ui => Application.Current.Dispatcher;

    /// <summary>
    /// Posts UI work without waiting.  A caller may well be inside one of our
    /// own event callbacks, and waiting on the thread that is calling it would
    /// deadlock; everything read back afterwards comes from a snapshot instead.
    /// </summary>
    private static void Post(Action action)
    {
        if (Application.Current is { } application)
        {
            _ = application.Dispatcher.BeginInvoke(action);
        }
    }

    // ---- IModalWindow ------------------------------------------------------

    /// <summary>
    /// Runs the dialog and blocks the caller until it is answered.
    ///
    /// The window has to be built on the UI thread, but every callback into the
    /// caller has to leave from <em>this</em> thread: COM tied the caller's call
    /// to it, and a single-threaded caller waiting here will only accept an
    /// incoming call that belongs to that same call.  A callback sent from any
    /// other thread is queued by COM until the caller is free, which it never
    /// will be.  So the window posts its callbacks back here and this loop makes
    /// them.
    /// </summary>
    public int Show(IntPtr owner)
    {
        using var busy = ComServerHost.EnterShow();

        var result = Hresult.Cancelled;
        using var finished = new ManualResetEventSlim(false);

        _dialogRunning = true;
        _ = Ui.BeginInvoke(() =>
        {
            try
            {
                result = ShowOnUi(owner);
            }
            finally
            {
                _dialogRunning = false;
                finished.Set();
            }
        });

        while (!finished.Wait(25))
        {
            DrainCallerWork();
        }

        DrainCallerWork();
        return result;
    }

    private void DrainCallerWork()
    {
        while (_callerWork.TryDequeue(out var work))
        {
            work();
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the thread the caller is waiting in, and
    /// does not come back until it has.  Used only for calls out to the caller's
    /// event sinks; anything the sink asks us in return is answered from a
    /// snapshot on whatever thread COM brings it in on.
    /// </summary>
    private void OnCallerThread(Action work)
    {
        if (!_dialogRunning)
        {
            work();
            return;
        }

        using var done = new ManualResetEventSlim(false);
        _callerWork.Enqueue(() =>
        {
            try
            {
                work();
            }
            finally
            {
                done.Set();
            }
        });

        done.Wait();
    }

    private int ShowOnUi(IntPtr owner)
    {
        try
        {
            ComTrace.Write($"Show(owner=0x{owner:X}) mode={_request.Mode} filters={_request.Filters.Count} initial={_request.InitialFolder}");
            _closeRequested = false;
            var session = new FileDialogSession(_request);
            ComTrace.Write($"session starts in {session.CurrentFolder}");
            _session = session;

            session.AcceptGuard = OnBeforeAccept;
            session.OverwriteGuard = OnBeforeOverwrite;
            session.FolderChanged += RaiseFolderChange;
            session.SelectionChanged += RaiseSelectionChange;
            session.FilterChanged += RaiseTypeChange;

            var window = new MainWindow(session);
            _window = window;
            FileDialogHost.AttachOwner(window, owner);

            // The caller is blocked inside this call, so its window takes no
            // input anyway; disabling it makes that visible, and the finally is
            // what guarantees it comes back.
            var disable = owner != IntPtr.Zero
                && ShellNative.IsWindow(owner)
                && ShellNative.IsWindowEnabled(owner);

            if (disable)
            {
                ShellNative.EnableWindow(owner, false);
            }

            try
            {
                window.ShowDialog();
            }
            finally
            {
                if (disable)
                {
                    ShellNative.EnableWindow(owner, true);
                    _ = ShellNative.SetForegroundWindow(owner);
                }
            }

            _result = window.PickerResult.IsCompleted
                ? window.PickerResult.Result
                : FileDialogResult.Cancelled(session.FileTypeIndex);

            _window = null;

            if (_closeRequested)
            {
                return _closeResult;
            }

            return _result.Accepted ? Hresult.Ok : Hresult.Cancelled;
        }
        catch (Exception exception)
        {
            _window = null;
            _result = FileDialogResult.Failed(exception.Message);
            ComTrace.Write($"Show failed: {exception}");
            return Marshal.GetHRForException(exception);
        }
    }

    // ---- IFileDialog -------------------------------------------------------

    public int SetFileTypes(uint count, ComDlgFilterSpec[] filters)
    {
        if (filters is null)
        {
            return Hresult.InvalidArg;
        }

        _request.Filters.Clear();
        for (var index = 0; index < count && index < filters.Length; index++)
        {
            _request.Filters.Add(new FileDialogFilterSpec(
                filters[index].Name ?? string.Empty,
                string.IsNullOrWhiteSpace(filters[index].Spec) ? "*.*" : filters[index].Spec));
        }

        return Hresult.Ok;
    }

    public int SetFileTypeIndex(uint index)
    {
        _request.FileTypeIndex = (int)Math.Max(1, index);
        if (_session is { } session)
        {
            Post(() => session.SelectedFilterIndex = _request.FileTypeIndex - 1);
        }

        return Hresult.Ok;
    }

    public int GetFileTypeIndex(out uint index)
    {
        index = (uint)(_session?.FileTypeIndex ?? Math.Max(1, _request.FileTypeIndex));
        return Hresult.Ok;
    }

    public int Advise(IFileDialogEvents events, out uint cookie)
    {
        cookie = 0;
        if (events is null)
        {
            return Hresult.InvalidArg;
        }

        cookie = _nextCookie++;
        _sinks[cookie] = events;
        return Hresult.Ok;
    }

    public int Unadvise(uint cookie)
    {
        _sinks.Remove(cookie);
        return Hresult.Ok;
    }

    public int SetOptions(uint options)
    {
        _request.Options = (FileDialogOptions)options;
        return Hresult.Ok;
    }

    public int GetOptions(out uint options)
    {
        options = (uint)_request.Options;
        return Hresult.Ok;
    }

    public int SetDefaultFolder(IShellItem folder)
    {
        _request.DefaultFolder = ShellNative.PathOf(folder) ?? string.Empty;
        return Hresult.Ok;
    }

    public int SetFolder(IShellItem folder)
    {
        var path = ShellNative.PathOf(folder);
        ComTrace.Write($"SetFolder({path ?? "<null>"})");
        if (path is null)
        {
            return Hresult.InvalidArg;
        }

        _request.InitialFolder = path;
        if (_session is not null && _window is { } window)
        {
            Post(() => window.NavigateFromCallerAsync(path));
        }

        return Hresult.Ok;
    }

    public int GetFolder(out IShellItem folder)
    {
        folder = null!;
        var path = _session?.CurrentFolder is { Length: > 0 } current
            ? current
            : _request.InitialFolder;

        if (string.IsNullOrEmpty(path) || ShellNative.ItemFor(path) is not { } item)
        {
            return Hresult.Fail;
        }

        folder = item;
        return Hresult.Ok;
    }

    public int GetCurrentSelection(out IShellItem item)
    {
        item = null!;
        var path = CurrentSelection().FirstOrDefault() ?? _result.FirstPath;
        if (path is null || ShellNative.ItemFor(path) is not { } created)
        {
            return Hresult.Fail;
        }

        item = created;
        return Hresult.Ok;
    }

    public int SetFileName(string name)
    {
        _request.FileName = name ?? string.Empty;
        if (_session is { } session)
        {
            Post(() => session.FileNameText = _request.FileName);
        }

        return Hresult.Ok;
    }

    public int GetFileName(out IntPtr name)
    {
        var text = _session?.FileNameText ?? _request.FileName;
        name = Marshal.StringToCoTaskMemUni(text ?? string.Empty);
        return name == IntPtr.Zero ? Hresult.OutOfMemory : Hresult.Ok;
    }

    public int SetTitle(string title)
    {
        _request.Title = title ?? string.Empty;
        return Hresult.Ok;
    }

    public int SetOkButtonLabel(string text)
    {
        _request.OkButtonLabel = text ?? string.Empty;
        return Hresult.Ok;
    }

    public int SetFileNameLabel(string label)
    {
        _request.FileNameLabel = label ?? string.Empty;
        return Hresult.Ok;
    }

    public int GetResult(out IShellItem item)
    {
        item = null!;
        if (_result is { Accepted: true, FirstPath: { } path })
        {
            if (ShellNative.ItemFor(path) is { } created)
            {
                item = created;
                return Hresult.Ok;
            }

            ComTrace.Write($"GetResult: no shell item for {path}");
            return Hresult.Fail;
        }

        ComTrace.Write($"GetResult: nothing accepted (accepted={_result.Accepted}, count={_result.Paths.Count})");
        return Hresult.Fail;
    }

    public int AddPlace(IShellItem place, FileDialogAddPlacement placement)
    {
        if (ShellNative.PathOf(place) is not { Length: > 0 } path)
        {
            return Hresult.InvalidArg;
        }

        _request.Places.Add(new FileDialogPlace(path, placement == FileDialogAddPlacement.Top));
        return Hresult.Ok;
    }

    public int SetDefaultExtension(string extension)
    {
        _request.DefaultExtension = extension ?? string.Empty;
        return Hresult.Ok;
    }

    /// <summary>The caller dismissing its own dialog, with the HRESULT it wants back.</summary>
    public int Close(int result)
    {
        _closeRequested = true;
        _closeResult = result;
        if (_window is { } window)
        {
            Post(window.CloseFromCaller);
        }

        return Hresult.Ok;
    }

    public int SetClientGuid(ref Guid client)
    {
        _request.ClientGuid = client;
        return Hresult.Ok;
    }

    public int ClearClientData()
    {
        new FileDialogClientStore().Clear(_request.ClientGuid);
        return Hresult.Ok;
    }

    /// <summary>
    /// <c>IShellItemFilter</c> was deprecated in the SDK in favour of
    /// <c>SetFileTypes</c>, and the system dialog answers this the same way.
    /// </summary>
    public int SetFilter(IntPtr filter) => Hresult.NotImplemented;

    // ---- IFileOpenDialog ---------------------------------------------------

    public int GetResults(out IShellItemArray items)
    {
        items = null!;
        if (!_result.Accepted || _result.Paths.Count == 0)
        {
            return Hresult.Fail;
        }

        if (ShellNative.ArrayFor(_result.Paths) is not { } array)
        {
            return Hresult.Fail;
        }

        items = array;
        return Hresult.Ok;
    }

    public int GetSelectedItems(out IShellItemArray items)
    {
        items = null!;
        var selection = CurrentSelection();
        if (selection.Count == 0 || ShellNative.ArrayFor(selection) is not { } array)
        {
            return Hresult.Fail;
        }

        items = array;
        return Hresult.Ok;
    }

    // ---- IFileSaveDialog ---------------------------------------------------

    public int SetSaveAsItem(IShellItem item)
    {
        _request.SaveAsItem = ShellNative.PathOf(item) ?? string.Empty;
        if (_request.SaveAsItem.Length > 0 && _request.FileName.Length == 0)
        {
            _request.FileName = Path.GetFileName(_request.SaveAsItem);
        }

        return Hresult.Ok;
    }

    /// <summary>
    /// The property-store half of <c>IFileSaveDialog</c>.  This dialog does not
    /// edit metadata, and saying so is better than pretending: a caller that
    /// checks the HRESULT can fall back to writing the properties itself.
    /// </summary>
    public int SetProperties(IntPtr store) => Hresult.NotImplemented;

    public int SetCollectedProperties(IntPtr list, bool appendDefault) => Hresult.NotImplemented;

    public int GetProperties(out IntPtr store)
    {
        store = IntPtr.Zero;
        return Hresult.NotImplemented;
    }

    public int ApplyProperties(IShellItem item, IntPtr store, IntPtr owner, IntPtr sink) => Hresult.NotImplemented;

    // ---- events ------------------------------------------------------------

    /// <summary>
    /// <c>OnFileOk</c>: the caller gets the last word on what it is handed, and
    /// anything other than S_OK leaves the dialog open.
    /// </summary>
    private bool OnBeforeAccept(IReadOnlyList<string> paths)
    {
        if (_sinks.Count == 0)
        {
            return true;
        }

        _result = new FileDialogResult(true, paths, _session?.FileTypeIndex ?? 1);

        var accepted = true;
        OnCallerThread(() =>
        {
            foreach (var sink in _sinks.Values.ToArray())
            {
                if (Guard(() => sink.OnFileOk(AsDialog())) != Hresult.Ok)
                {
                    accepted = false;
                    return;
                }
            }
        });

        return accepted;
    }

    /// <summary>
    /// <c>OnOverwrite</c>: a caller may accept or refuse a replacement without
    /// the user being asked at all.
    /// </summary>
    private bool? OnBeforeOverwrite(string path)
    {
        if (_sinks.Count == 0 || ShellNative.ItemFor(path) is not { } item)
        {
            return null;
        }

        bool? decision = null;
        OnCallerThread(() =>
        {
            foreach (var sink in _sinks.Values.ToArray())
            {
                var response = OverwriteResponse.Default;
                if (Guard(() => sink.OnOverwrite(AsDialog(), item, out response)) != Hresult.Ok)
                {
                    continue;
                }

                if (response == OverwriteResponse.Accept)
                {
                    decision = true;
                    return;
                }

                if (response == OverwriteResponse.Refuse)
                {
                    decision = false;
                    return;
                }
            }
        });

        return decision;
    }

    private void RaiseFolderChange()
    {
        if (_sinks.Count == 0)
        {
            return;
        }

        OnCallerThread(() =>
        {
            foreach (var sink in _sinks.Values.ToArray())
            {
                _ = Guard(() => sink.OnFolderChange(AsDialog()));
            }
        });
    }

    private void RaiseSelectionChange()
    {
        if (_sinks.Count == 0)
        {
            return;
        }

        OnCallerThread(() =>
        {
            foreach (var sink in _sinks.Values.ToArray())
            {
                _ = Guard(() => sink.OnSelectionChange(AsDialog()));
            }
        });
    }

    private void RaiseTypeChange()
    {
        if (_sinks.Count == 0)
        {
            return;
        }

        OnCallerThread(() =>
        {
            foreach (var sink in _sinks.Values.ToArray())
            {
                _ = Guard(() => sink.OnTypeChange(AsDialog()));
            }
        });
    }

    /// <summary>
    /// A sink lives in the calling process; it can be gone, or throw, and none
    /// of that is a reason to take the dialog down with it.
    /// </summary>
    private static int Guard(Func<int> call)
    {
        try
        {
            return call();
        }
        catch (COMException exception)
        {
            return exception.HResult;
        }
        catch (InvalidComObjectException)
        {
            return Hresult.Fail;
        }
    }

    /// <summary>
    /// Read from the session's snapshot rather than the canvas: this runs on a
    /// COM thread, and the canvas belongs to the window's.
    /// </summary>
    private IReadOnlyList<string> CurrentSelection() =>
        _session?.LastSelection is { Count: > 0 } selection
            ? selection
            : _result.Accepted ? _result.Paths : [];

    /// <summary>This object as the interface the event sinks expect.</summary>
    private IFileDialog AsDialog() => (IFileDialog)this;
}
