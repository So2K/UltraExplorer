using System.Windows.Threading;

namespace UltraExplorer.Services;

/// <summary>Completes a transfer while the OLE Drop call still owns the
/// source's temporary files. Archive managers may delete them as soon as
/// Drop returns; retaining filenames or the data object does not retain bytes.</summary>
internal static class ExternalFileDrop
{
    internal static bool Complete(Dispatcher dispatcher, Task<bool> transfer)
    {
        dispatcher.VerifyAccess();
        if (!transfer.IsCompleted)
        {
            // The transfer still uses its background disk/Shell threads.
            // Pump the dispatcher for those continuations, paint and native
            // progress/cancel UI instead of blocking it with Task.Wait().
            var frame = new DispatcherFrame(exitWhenRequested: false);
            _ = transfer.ContinueWith(_ => frame.Continue = false,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }

        return transfer.GetAwaiter().GetResult();
    }
}
