using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace UltraExplorer.Services;

/// <summary>Completes a transfer while the OLE Drop call still owns the
/// source's temporary files. Archive managers may delete them as soon as
/// Drop returns; retaining filenames or the data object does not retain bytes.
///
/// <para>A source that has enabled async extraction and accepts
/// <see cref="IDataObjectAsyncCapability.StartOperation"/> keeps its bytes
/// until EndOperation, so Drop can return immediately, regardless of where
/// those bytes are staged. Without that agreement Drop holds the source only
/// until the Shell operation finishes, not through the folder reads after it.
/// A filename outside our temporary directory is not an ownership agreement.</para></summary>
internal static class ExternalFileDrop
{
    private const int EFail = unchecked((int)0x80004005);

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

        var copied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transfer = BeginTransfer(beginTransfer, copied, () => frame.Continue = false);
        started?.Invoke(transfer);
        if (!copied.Task.IsCompleted)
        {
            Dispatcher.PushFrame(frame);
        }

        return copied.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Starts a transfer only after the source agrees to retain its data for
    /// async extraction. False means no transfer was started and the caller
    /// must keep Drop active while completing it. EndOperation follows the
    /// actual Shell copy, before any later directory refreshes.
    /// </summary>
    internal static bool TryContinue(System.Windows.IDataObject data, Func<Task<bool>> beginTransfer,
        DragDropEffects reported, out Task<bool> transfer)
    {
        var source = StartOperation(data);
        if (source is null)
        {
            transfer = Task.FromResult(false);
            return false;
        }

        var copied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            transfer = BeginTransfer(beginTransfer, copied);
        }
        catch (Exception)
        {
            End(source, done: false, reported);
            throw;
        }

        _ = EndWhenOverAsync(source, copied.Task, reported);
        return true;
    }

    /// <summary>
    /// Observes the Shell operation inside this transfer. Its completion owns
    /// the source lifetime; the whole transfer also includes refreshes and is
    /// separately retained by Close. If validation ends before a Shell copy
    /// starts, that transfer outcome releases the source instead.
    /// </summary>
    private static Task<bool> BeginTransfer(Func<Task<bool>> beginTransfer, TaskCompletionSource<bool> copied,
        Action? releaseHold = null)
    {
        var outer = NativeShellService.CopyStarted.Value;
        NativeShellService.CopyStarted.Value = copy => copy.ContinueWith(ended =>
        {
            copied.TrySetResult(ended.Status == TaskStatus.RanToCompletion);
            // Request the frame exit before Unwrap can resume the transfer's
            // UI continuation. A second asynchronous continuation could let
            // that continuation enter a slow refresh while Drop still held.
            releaseHold?.Invoke();
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

        _ = CompleteWithoutCopyAsync(transfer, copied, releaseHold);
        return transfer;
    }

    private static async Task CompleteWithoutCopyAsync(Task<bool> transfer, TaskCompletionSource<bool> copied,
        Action? releaseHold)
    {
        var done = false;
        try { done = await transfer; }
        catch (Exception) { /* The transfer reports its own error. */ }
        if (copied.TrySetResult(done)) releaseHold?.Invoke();
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
        if (data is IDataObjectAsyncCapability source)
        {
            return source;
        }

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
