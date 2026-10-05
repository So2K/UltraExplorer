using System.Collections.Concurrent;

namespace UltraExplorer.Picker.Integration;

/// <summary>UI Automation lives on its own MTA, never the WPF thread or the
/// WinEvent callback. Providers in another application may be slow.</summary>
internal sealed class AutomationThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    public AutomationThread()
    {
        var thread = new Thread(() => { foreach (var action in _queue.GetConsumingEnumerable()) action(); })
            { IsBackground = true, Name = "UltraExplorer dialog automation" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public Task<T> Run<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                try { completion.TrySetResult(action()); }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
        }
        catch (InvalidOperationException) { completion.TrySetException(new ObjectDisposedException(nameof(AutomationThread))); }
        return completion.Task;
    }

    public Task Run(Action action) => Run(() => { action(); return true; });
    public void Dispose() => _queue.CompleteAdding();
}
