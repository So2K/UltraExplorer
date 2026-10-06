using System.Runtime.InteropServices;
using UltraExplorer.Picker.Com;

namespace UltraExplorer.Services;

/// <summary>
/// Explorer's modern file-operation backend. IFileOperation replaces the
/// legacy SHFileOperation string buffer and accepts robust Shell items, so a
/// long source or destination never enters the legacy MAX_PATH implementation.
/// It must be called from an STA (NativeShellService's operation thread is one).
///
/// Interface order and constants follow the Windows SDK ShObjIdl_core.h and
/// https://learn.microsoft.com/windows/win32/api/shobjidl_core/nn-shobjidl_core-ifileoperation.
/// </summary>
internal static class ShellFileOperation
{
    internal static ShellFileOperationFlags TransferFlags(bool renameOnCollision) =>
        ShellFileOperationFlags.AllowUndo
        | ShellFileOperationFlags.AddUndoRecord
        | ShellFileOperationFlags.NoConnectedElements
        | (renameOnCollision
            ? ShellFileOperationFlags.RenameOnCollision | ShellFileOperationFlags.PreserveFileExtensions
            : 0);

    internal static ShellFileOperationFlags DeleteFlags(bool permanently) =>
        ShellFileOperationFlags.NoConnectedElements
        | (permanently
            ? ShellFileOperationFlags.NoConfirmation
            : ShellFileOperationFlags.AllowUndo
              | ShellFileOperationFlags.AddUndoRecord
              | ShellFileOperationFlags.WantNukeWarning
              | ShellFileOperationFlags.RecycleOnDelete);

    internal static void Run(
        IntPtr owner,
        ShellFileOperationKind kind,
        IReadOnlyList<string> sources,
        string? targetDirectory,
        ShellFileOperationFlags flags,
        IReadOnlyList<string?>? targetNames = null)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("Windows file operations require an STA thread.");
        }

        IFileOperation? operation = null;
        IShellItem? destination = null;
        var items = new List<IShellItem>(sources.Count);
        try
        {
            if (targetNames is not null && targetNames.Count != sources.Count)
            {
                throw new ArgumentException("Each source needs one destination name.", nameof(targetNames));
            }

            // The Windows 11 error UI reached from a too-long recursive
            // destination is itself the legacy crashing path this backend is
            // replacing. For any long operation, ask IFileOperation to return
            // its HRESULT without native error UI and to stop on the first
            // failure rather than continuing a partial tree. Successful long
            // operations retain their normal progress and undo records. This
            // deliberately inspects only the paths already supplied: a short
            // source root can contain an unknown too-deep descendant, but
            // discovering that here would recursively scan the user's tree
            // before the Shell operation and change its progress/cancel model.
            if (IsLongOperation(sources, targetDirectory, targetNames))
            {
                flags |= ShellFileOperationFlags.NoErrorUi | ShellFileOperationFlags.EarlyFailure;
            }

            // A ComImport coclass is activated as an RCW; the object bridge is
            // required because C# quite correctly sees no managed inheritance
            // relationship between the sealed coclass declaration and its
            // requested COM interface.
            operation = (IFileOperation)(object)new FileOperationCom();
            ThrowQueueFailure(operation.SetOperationFlags((uint)flags), "set operation options");
            if (owner != IntPtr.Zero)
            {
                ThrowQueueFailure(operation.SetOwnerWindow(owner), "set the operation window");
            }

            if (kind is ShellFileOperationKind.Copy or ShellFileOperationKind.Move)
            {
                if (string.IsNullOrWhiteSpace(targetDirectory))
                {
                    throw new ArgumentException("A destination folder is required.", nameof(targetDirectory));
                }

                destination = ItemFor(targetDirectory);
            }

            for (var index = 0; index < sources.Count; index++)
            {
                var source = sources[index];
                var item = ItemFor(source);
                items.Add(item);
                var targetName = targetNames?[index];
                var result = kind switch
                {
                    ShellFileOperationKind.Copy => operation.CopyItem(item, destination!, targetName, IntPtr.Zero),
                    ShellFileOperationKind.Move => operation.MoveItem(item, destination!, targetName, IntPtr.Zero),
                    _ => operation.DeleteItem(item, IntPtr.Zero)
                };
                ThrowQueueFailure(result, kind.ToString().ToLowerInvariant() + " an item");
            }

            var performed = operation.PerformOperations();
            var abortResult = operation.GetAnyOperationsAborted(out var aborted);
            ThrowForCompletion(performed, abortResult, aborted);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ExternalException)
        {
            throw new IOException("Windows could not finish the file operation.", ex);
        }
        finally
        {
            foreach (var item in items)
            {
                Release(item);
            }

            Release(destination);
            Release(operation);
        }
    }

    /// <summary>Maps PerformOperations plus its mandatory aborted query to the service's existing exceptions.</summary>
    internal static void ThrowForCompletion(int performResult, int abortResult, bool aborted)
    {
        if (aborted || IsCancelled(performResult))
        {
            throw new OperationCanceledException();
        }

        ThrowQueueFailure(performResult, "perform the operation");
        ThrowQueueFailure(abortResult, "read the operation result");
    }

    private static IShellItem ItemFor(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var id = ShellNative.ShellItemId;
        Exception? first = null;
        foreach (var parsingName in ParsingNames(full))
        {
            try
            {
                return ShellNative.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref id);
            }
            catch (Exception ex) when (ex is COMException or FileNotFoundException
                or DirectoryNotFoundException or ArgumentException or NotSupportedException)
            {
                first ??= ex;
            }
        }

        throw new IOException($"Windows could not address {full} as a Shell item.", first);
    }

    /// <summary>The extended parsing name first for a long filesystem path; the ordinary name is a safe modern-parser fallback.</summary>
    internal static IReadOnlyList<string> ParsingNames(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)
            || full.StartsWith(@"\\.\", StringComparison.Ordinal)
            || full.Length < 260)
        {
            return [full];
        }

        var extended = full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full[2..]
            : @"\\?\" + full;
        return [extended, full];
    }

    internal static bool IsLongOperation(
        IReadOnlyList<string> sources,
        string? targetDirectory,
        IReadOnlyList<string?>? targetNames)
    {
        if (sources.Any(IsLong) || targetDirectory is not null && IsLong(targetDirectory))
        {
            return true;
        }

        if (targetDirectory is null)
        {
            return false;
        }

        // CopyItem/MoveItem with no explicit new name keeps the source's own
        // name.  Source and destination directories can each be below
        // MAX_PATH while that ordinary resulting path crosses it; those need
        // the same prompt-free early-failure flags as an explicitly named
        // destination.
        for (var index = 0; index < sources.Count; index++)
        {
            var name = targetNames?[index];
            if (string.IsNullOrEmpty(name))
            {
                name = Path.GetFileName(Path.TrimEndingDirectorySeparator(sources[index]));
            }

            if (name.Length > 0 && IsLong(Path.Combine(targetDirectory, name)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLong(string path) => Path.GetFullPath(path).Length >= 260;

    private static void ThrowQueueFailure(int result, string action)
    {
        if (result >= 0)
        {
            return;
        }

        if (IsCancelled(result))
        {
            throw new OperationCanceledException();
        }

        if (result == CopyEnginePathTooDeepDestination)
        {
            throw new IOException(
                "Windows cannot copy this folder tree because a destination path would exceed the Windows Shell limit. "
                + "Shorten one of the folder names or move the folder closer to the drive root, then try again.",
                Marshal.GetExceptionForHR(result));
        }

        throw new IOException($"Windows could not {action} (HRESULT 0x{result:X8}).", Marshal.GetExceptionForHR(result));
    }

    private static bool IsCancelled(int result) => result is ErrorCancelled or ErrorAbort;

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.ReleaseComObject(value);
        }
    }

    private const int ErrorCancelled = unchecked((int)0x800704C7); // HRESULT_FROM_WIN32(ERROR_CANCELLED).
    private const int ErrorAbort = unchecked((int)0x80004004); // E_ABORT.
    private const int CopyEnginePathTooDeepDestination = unchecked((int)0x8027001E); // COPYENGINE_E_PATH_TOO_DEEP_DEST.

    [ComImport]
    [Guid("3ad05575-8857-4850-9277-11b85bdb8e09")]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class FileOperationCom
    {
    }

    [ComImport]
    [Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr progressSink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint operationFlags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr progressDialog);
        [PreserveSig] int SetProperties(IntPtr propertyChangeArray);
        [PreserveSig] int SetOwnerWindow(IntPtr owner);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr progressSink);
        [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? newName, IntPtr progressSink);
        [PreserveSig] int MoveItems(IntPtr items, IShellItem destinationFolder);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? copyName, IntPtr progressSink);
        [PreserveSig] int CopyItems(IntPtr items, IShellItem destinationFolder);
        [PreserveSig] int DeleteItem(IShellItem item, IntPtr progressSink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IShellItem destinationFolder, uint fileAttributes,
            [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string? templateName,
            IntPtr progressSink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}

internal enum ShellFileOperationKind
{
    Move,
    Copy,
    Delete
}

[Flags]
internal enum ShellFileOperationFlags : uint
{
    RenameOnCollision = 0x0008,
    NoConfirmation = 0x0010,
    AllowUndo = 0x0040,
    NoErrorUi = 0x0400,
    NoConnectedElements = 0x2000,
    WantNukeWarning = 0x4000,
    RecycleOnDelete = 0x00080000,
    EarlyFailure = 0x00100000,
    PreserveFileExtensions = 0x00200000,
    AddUndoRecord = 0x20000000
}
