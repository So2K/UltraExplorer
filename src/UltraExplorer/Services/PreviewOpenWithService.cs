using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using IDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace UltraExplorer.Services;

/// <summary>A detached Windows association snapshot. Icons are frozen before crossing apartments.</summary>
public sealed record PreviewOpenWithApp(string Id, string Name, ImageSource? Icon);

public enum PreviewOpenResult { Opened, NeedsChoice, Cancelled }

/// <summary>
/// The Windows Open with menu, rather than guessed registry commands or a full application launcher.
/// Shell COM objects never leave their owning STA. Choosing a handler does not change the default app.
/// </summary>
public static class PreviewOpenWithService
{
    private const int MaximumHandlers = 256;
    private const uint NoUi = 0x400, NoAsync = 0x100, ExecuteOnce = 0x4;
    private static readonly SemaphoreSlim Workers = new(2, 2);
    private static readonly Guid ShellItemId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid DataObjectId = new("0000010E-0000-0000-C000-000000000046");
    private static readonly Guid DataObjectHandler = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");

    // These seams intercept the last side effect only. Smoke checks still use real Windows
    // association enumeration, COM objects and file data, without opening installed applications.
    internal static Func<IDataObject, int>? InvokeForChecks { get; set; }
    internal static Func<string, IntPtr, uint, int>? ChooseForChecks { get; set; }
    internal static Func<string, IntPtr, uint, (bool Success, int Error)>? OpenDefaultForChecks { get; set; }

    public static Task<IReadOnlyList<PreviewOpenWithApp>> ListAsync(string path, CancellationToken token = default)
    {
        var extension = Extension(path);
        return OnStaAsync<IReadOnlyList<PreviewOpenWithApp>>(() =>
        {
            token.ThrowIfCancellationRequested();
            if (extension.Length == 0) return Array.Empty<PreviewOpenWithApp>();
            var items = new List<PreviewOpenWithApp>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            VisitHandlers(extension, token, handler =>
            {
                if (handler.GetName(out var id) < 0 || string.IsNullOrWhiteSpace(id) || !seen.Add(id)) return false;
                handler.GetUIName(out var name);
                if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(id);
                if (string.IsNullOrWhiteSpace(name)) name = id;
                items.Add(new PreviewOpenWithApp(id, name, ReadIcon(handler)));
                return false;
            });
            // Explorer also orders the displayed application names, not their executable paths.
            return items.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }, token);
    }

    /// <remarks>requestCurrent runs on the Shell worker and must be safe to read from that thread.</remarks>
    public static Task<PreviewOpenResult> InvokeAsync(string path, string id, IntPtr owner,
        CancellationToken token = default, Func<bool>? requestCurrent = null)
    {
        var file = FileName(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return OnStaAsync(() =>
        {
            if (!Current(token, requestCurrent)) return PreviewOpenResult.Cancelled;
            EnsureFile(file);
            var result = PreviewOpenResult.NeedsChoice;
            VisitHandlers(Path.GetExtension(file), token, handler =>
            {
                if (handler.GetName(out var candidate) < 0 || !string.Equals(id, candidate, StringComparison.OrdinalIgnoreCase)) return false;
                IDataObject? data = null;
                try
                {
                    data = CreateFileDataObject(file);
                    if (!Current(token, requestCurrent)) { result = PreviewOpenResult.Cancelled; return true; }
                    var answer = InvokeForChecks?.Invoke(data) ?? handler.Invoke(data);
                    result = HResultOutcome(answer);
                    return true;
                }
                finally { Release(data); }
            });
            return Current(token, requestCurrent) || result == PreviewOpenResult.Opened ? result : PreviewOpenResult.Cancelled;
        }, token);
    }

    /// <summary>Windows' actual app chooser. OAIF_EXEC opens this file once; registration flags are absent.</summary>
    public static Task<PreviewOpenResult> ChooseAsync(string path, IntPtr owner, CancellationToken token = default,
        Func<bool>? requestCurrent = null)
    {
        var file = FileName(path);
        return OnStaAsync(() =>
        {
            if (!Current(token, requestCurrent)) return PreviewOpenResult.Cancelled;
            EnsureFile(file);
            if (!Current(token, requestCurrent)) return PreviewOpenResult.Cancelled;
            if (ChooseForChecks is { } choose) return HResultOutcome(choose(file, owner, ExecuteOnce));
            var info = new OpenAsInfo { File = file, Flags = ExecuteOnce };
            return HResultOutcome(SHOpenWithDialog(owner, ref info));
        }, token);
    }

    /// <summary>Returns NeedsChoice when no usable default is registered, without Windows' error dialog.</summary>
    public static Task<PreviewOpenResult> OpenDefaultAsync(string path, IntPtr owner, CancellationToken token = default,
        Func<bool>? requestCurrent = null)
    {
        var file = FileName(path);
        return OnStaAsync(() =>
        {
            if (!Current(token, requestCurrent)) return PreviewOpenResult.Cancelled;
            EnsureFile(file);
            // Unknown's open verb is the chooser itself. Do not invoke it accidentally.
            if (!NativeShellService.IsExecutable(file) && !HasDefaultAssociation(Path.GetExtension(file)))
                return PreviewOpenResult.NeedsChoice;
            if (!Current(token, requestCurrent)) return PreviewOpenResult.Cancelled;
            if (OpenDefaultForChecks is { } open)
            {
                var reply = open(file, owner, NoUi | NoAsync);
                return ShellOutcome(reply.Success, reply.Error, 0);
            }
            var info = new ShellExecuteInfo
            {
                Size = Marshal.SizeOf<ShellExecuteInfo>(), Mask = NoUi | NoAsync,
                Window = owner, File = file, Show = 1
            };
            var success = ShellExecuteExW(ref info);
            var error = success ? 0 : Marshal.GetLastWin32Error();
            return ShellOutcome(success, error, info.Instance.ToInt64());
        }, token);
    }

    private static bool Current(CancellationToken token, Func<bool>? current)
        => !token.IsCancellationRequested && current?.Invoke() != false;

    private static string FileName(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.Any(character => character < ' '))
            throw new ArgumentException("A fully qualified file name is required.", nameof(path));
        return Path.GetFullPath(path);
    }

    private static string Extension(string path) => Path.GetExtension(FileName(path));

    private static void EnsureFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
            throw new IOException("Open with requires a file.");
    }

    private static bool HasDefaultAssociation(string extension)
    {
        if (extension.Length == 0) return false;
        // Respect user defaults, including Store apps whose launch is a COM delegate or drop target.
        foreach (var kind in new uint[] { 1, 18, 17 }) // ASSOCSTR_COMMAND / DELEGATEEXECUTE / DROPTARGET
        {
            uint length = 0;
            AssocQueryStringW(0x400, kind, extension, null, null, ref length); // INIT_IGNOREUNKNOWN
            if (length is < 2 or > 32_768) continue;
            var value = new StringBuilder((int)length);
            if (AssocQueryStringW(0x400, kind, extension, null, value, ref length) == 0 && value.Length > 0) return true;
        }
        return false;
    }

    private static void VisitHandlers(string extension, CancellationToken token, Func<IAssocHandler, bool> visitor)
    {
        if (extension.Length == 0) return;
        IEnumAssocHandlers? enumerator = null;
        try
        {
            // NONE includes the registered non-recommended apps shown by Explorer's submenu.
            var answer = SHAssocEnumHandlers(extension, 0, out enumerator);
            if (answer < 0) { Marshal.ThrowExceptionForHR(answer); return; }
            if (enumerator is null) return;
            for (var index = 0; index < MaximumHandlers; index++)
            {
                token.ThrowIfCancellationRequested();
                IAssocHandler? handler = null;
                try
                {
                    answer = enumerator.Next(1, out handler, out var fetched);
                    if (answer < 0) Marshal.ThrowExceptionForHR(answer);
                    if (fetched == 0 || handler is null) break;
                    if (visitor(handler)) break;
                }
                finally { Release(handler); }
            }
        }
        finally { Release(enumerator); }
    }

    private static ImageSource? ReadIcon(IAssocHandler handler)
    {
        IntPtr icon = IntPtr.Zero;
        try
        {
            if (handler.GetIconLocation(out var path, out var index) < 0 || string.IsNullOrWhiteSpace(path)) return null;
            // Microsoft recommends the Shell cache here; it also supplies the generic fallback icon.
            var cachedIndex = Shell_GetCachedImageIndexW(Environment.ExpandEnvironmentVariables(path), index, 0);
            if (cachedIndex < 0) return null;
            var info = new ShellFileInfo();
            var list = SHGetFileInfoW("application.exe", 0x80, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x4000 | 0x10 | 1);
            if (list == IntPtr.Zero) return null;
            icon = ImageList_GetIcon(list, cachedIndex, 1);
            if (icon == IntPtr.Zero) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException) { return null; }
        finally { if (icon != IntPtr.Zero) DestroyIcon(icon); }
    }

    private static IDataObject CreateFileDataObject(string path)
    {
        IShellItem? item = null;
        IntPtr data = IntPtr.Zero;
        try
        {
            var itemId = ShellItemId;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemId, out item));
            var handlerId = DataObjectHandler;
            var dataId = DataObjectId;
            Marshal.ThrowExceptionForHR(item.BindToHandler(IntPtr.Zero, ref handlerId, ref dataId, out data));
            return (IDataObject)Marshal.GetObjectForIUnknown(data);
        }
        finally
        {
            if (data != IntPtr.Zero) Marshal.Release(data);
            Release(item);
        }
    }

    private static PreviewOpenResult HResultOutcome(int answer)
    {
        if (answer >= 0) return answer == 1 ? PreviewOpenResult.Cancelled : PreviewOpenResult.Opened;
        if (answer is unchecked((int)0x800704C7) or unchecked((int)0x80004004)) return PreviewOpenResult.Cancelled;
        if (answer == unchecked((int)0x80070483)) return PreviewOpenResult.NeedsChoice;
        Marshal.ThrowExceptionForHR(answer);
        return PreviewOpenResult.Cancelled;
    }

    private static PreviewOpenResult ShellOutcome(bool success, int error, long legacy)
    {
        if (success) return PreviewOpenResult.Opened;
        if (error == 1223) return PreviewOpenResult.Cancelled;
        if (error == 1155 || legacy is 31 or 27) return PreviewOpenResult.NeedsChoice;
        throw new Win32Exception(error);
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    private static async Task<T> OnStaAsync<T>(Func<T> action, CancellationToken token)
    {
        await Workers.WaitAsync(token).ConfigureAwait(false);
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var initialized = false;
            try
            {
                token.ThrowIfCancellationRequested();
                Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 0x2 | 0x4)); // STA, disable OLE1 DDE
                initialized = true;
                done.TrySetResult(action());
            }
            catch (OperationCanceledException) { done.TrySetCanceled(token); }
            catch (Exception error) { done.TrySetException(error); }
            finally
            {
                if (initialized) CoUninitialize();
                Workers.Release();
            }
        }) { IsBackground = true, Name = "UltraExplorer Open with" };
        thread.SetApartmentState(ApartmentState.STA);
        try { thread.Start(); }
        catch { Workers.Release(); throw; }
        // An OS extension cannot be forcibly interrupted. Discard its eventual result and release
        // objects on the worker; cancelled windows never wait for registry/icon metadata on the UI.
        _ = done.Task.ContinueWith(failed => { _ = failed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await done.Task.WaitAsync(token).ConfigureAwait(false);
    }

    [ComImport, Guid("973810AE-9599-4B88-9E4D-6EE98C9552DA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumAssocHandlers
    {
        [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IAssocHandler? handler, out uint fetched);
    }

    [ComImport, Guid("F04061AC-1659-4A3F-A954-775AA57FC083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAssocHandler
    {
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string? name);
        [PreserveSig] int GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string? name);
        [PreserveSig] int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string? path, out int index);
        [PreserveSig] int IsRecommended();
        [PreserveSig] int MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string description);
        [PreserveSig] int Invoke(IDataObject data);
        [PreserveSig] int CreateInvoker(IDataObject data, out IntPtr invoker);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr context, ref Guid handler, ref Guid requested, out IntPtr result);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Window;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
        public int Show;
        public IntPtr Instance, IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public IntPtr ClassKey;
        public uint HotKey;
        public IntPtr IconOrMonitor, Process;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHAssocEnumHandlers(string extension, uint filter, out IEnumAssocHandlers? handlers);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid requested, out IShellItem item);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHOpenWithDialog(IntPtr owner, ref OpenAsInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellExecuteExW(ref ShellExecuteInfo info);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AssocQueryStringW(uint flags, uint kind, string association, string? verb, StringBuilder? output, ref uint length);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int Shell_GetCachedImageIndexW(string path, int iconIndex, uint simulateDocument);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr SHGetFileInfoW(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("comctl32.dll", ExactSpelling = true)] private static extern IntPtr ImageList_GetIcon(IntPtr list, int index, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
}
