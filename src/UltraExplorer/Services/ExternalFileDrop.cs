using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace UltraExplorer.Services;

/// <summary>Completes a transfer while the OLE Drop call still owns the
/// source's temporary files. Archive managers may delete them as soon as
/// Drop returns; retaining filenames or the data object does not retain bytes.
///
/// <para>Only a drop whose files lie in the temporary folder, where archive
/// managers extract, is held that way, and only until the Shell is done with
/// its files - not while the folders are read again afterwards.  Any other
/// drop returns at once and its copy goes on after: holding Drop for the
/// whole copy froze the source's window - the desktop, an Explorer window or
/// a browser - for as long as the copy ran.  A source that can wait - one
/// that offers <see cref="IDataObjectAsyncCapability"/> and has turned it on -
/// is told that the copy goes on after Drop, and later that it has
/// ended.</para></summary>
internal static class ExternalFileDrop
{
    private const int EFail = unchecked((int)0x80004005);

    /// <summary>
    /// Whether Drop has to hold the source until its files are copied: when
    /// any of <paramref name="paths"/> lies in the temporary folder, where
    /// archive managers extract and from where they delete once Drop returns.
    /// </summary>
    internal static bool MustHold(IReadOnlyList<string> paths)
    {
        var roots = TemporaryRoots();
        return paths.Any(path => roots.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The temporary folder by its long and by its short (8.3) name: a source
    /// may name its files under either.
    /// </summary>
    private static string[] TemporaryRoots()
    {
        var temp = Path.GetTempPath();
        var name = new StringBuilder(1024);
        string Named(uint length) => length is > 0 and < 1024
            ? Path.TrimEndingDirectorySeparator(name.ToString(0, (int)length)) + Path.DirectorySeparatorChar
            : temp;
        return new[] { temp, Named(GetLongPathNameW(temp, name, (uint)name.Capacity)), Named(GetShortPathNameW(temp, name, (uint)name.Capacity)) }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathNameW(string path, StringBuilder longPath, uint length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string path, StringBuilder shortPath, uint length);

    /// <summary>
    /// Starts the transfer and holds Drop - pumping the dispatcher - until the
    /// Shell has finished copying the source's files, or until the transfer
    /// has ended without copying: true when the files were copied.
    /// <paramref name="started"/> is handed the whole transfer as soon as it
    /// has started.
    /// </summary>
    internal static bool Complete(Dispatcher dispatcher, Func<Task<bool>> beginTransfer, Action<Task<bool>>? started = null)
    {
        dispatcher.VerifyAccess();

        // The transfer still uses its background disk/Shell threads.
        // Pump the dispatcher for those continuations, paint and native
        // progress/cancel UI instead of blocking it with Task.Wait().
        var frame = new DispatcherFrame(exitWhenRequested: false);

        // The copy tells the hold it has ended before the transfer itself
        // hears of it, so Drop returns before the transfer goes on to read
        // the folders again (see NativeShellService.CopyStarted).
        var copied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outer = NativeShellService.CopyStarted.Value;
        NativeShellService.CopyStarted.Value = copy => copy.ContinueWith(ended =>
        {
            copied.TrySetResult(ended.Status == TaskStatus.RanToCompletion);
            frame.Continue = false;
            return ended;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
        Task<bool> transfer;
        try
        {
            transfer = beginTransfer();
        }
        finally
        {
            NativeShellService.CopyStarted.Value = outer;
        }

        started?.Invoke(transfer);
        if (!transfer.IsCompleted && !copied.Task.IsCompleted)
        {
            _ = transfer.ContinueWith(_ => frame.Continue = false,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }

        return copied.Task.IsCompleted ? copied.Task.Result : transfer.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Starts the transfer of a drop that is not held (see
    /// <see cref="MustHold"/>).  A source that can wait is told that the
    /// transfer goes on after Drop returns, and - with what was done - once it
    /// has ended, whichever way.
    /// </summary>
    internal static Task<bool> Continue(System.Windows.IDataObject data, Func<Task<bool>> beginTransfer, DragDropEffects reported)
    {
        var source = StartOperation(data);
        Task<bool> transfer;
        try
        {
            transfer = beginTransfer();
        }
        catch (Exception)
        {
            if (source is not null) End(source, done: false, reported);
            throw;
        }

        if (source is not null) _ = EndWhenOverAsync(source, transfer, reported);
        return transfer;
    }

    /// <summary>
    /// The drop's source, told that the transfer goes on after Drop returns,
    /// when it can wait for that; null when it cannot or does not say.
    /// </summary>
    private static IDataObjectAsyncCapability? StartOperation(System.Windows.IDataObject data)
    {
        try
        {
            return SourceOf(data) is IDataObjectAsyncCapability source
                && source.GetAsyncMode(out var isAsync) >= 0 && isAsync != 0
                && source.StartOperation(IntPtr.Zero) >= 0
                    ? source
                    : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException or TargetException or FieldAccessException)
        {
            // A source that cannot say is treated as one that says no.
            return null;
        }
    }

    private static async Task EndWhenOverAsync(IDataObjectAsyncCapability source, Task<bool> transfer, DragDropEffects reported)
    {
        var done = false;
        try
        {
            done = await transfer;
        }
        catch (Exception)
        {
            // The drop's own toast says what went wrong; the source still hears that it is over.
        }

        End(source, done, reported);
    }

    private static void End(IDataObjectAsyncCapability source, bool done, DragDropEffects reported)
    {
        try
        {
            source.EndOperation(done ? 0 : EFail, IntPtr.Zero, done ? (uint)reported : 0);
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            // The source has gone; there is no one left to tell.
        }
    }

    private static readonly FieldInfo? InnerData = typeof(DataObject).GetField("_innerData", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// The source's own data object behind WPF's wrapper of it - for a drag
    /// from another program, the COM object that program offered.  WPF keeps
    /// it to itself; when its insides differ from what is looked for here, the
    /// source is taken to be one that cannot wait.
    /// </summary>
    private static object? SourceOf(System.Windows.IDataObject data)
    {
        if (data is not DataObject wrapper || InnerData?.GetValue(wrapper) is not { } inner)
        {
            return null;
        }

        return inner.GetType().GetField("_runtimeDataObject", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(inner);
    }
}

/// <summary>
/// A drag source's promise to keep what it offers until the drop target says
/// it is done with it (IDataObjectAsyncCapability, shobjidl.h): the target
/// starts the operation in Drop and ends it when its copy has ended.
/// </summary>
[ComImport]
[Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDataObjectAsyncCapability
{
    [PreserveSig]
    int SetAsyncMode(int doAsync);

    [PreserveSig]
    int GetAsyncMode(out int isAsync);

    [PreserveSig]
    int StartOperation(IntPtr bindContext);

    [PreserveSig]
    int InOperation(out int inOperation);

    [PreserveSig]
    int EndOperation(int result, IntPtr bindContext, uint effects);
}
