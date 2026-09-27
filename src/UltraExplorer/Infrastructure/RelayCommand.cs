using System.Windows.Input;

namespace UltraExplorer.Infrastructure;

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand<T>(Action<T?> execute, Predicate<T?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(Coerce(parameter)) ?? true;

    public void Execute(object? parameter) => execute(Coerce(parameter));

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private static T? Coerce(object? parameter) => parameter is T typed ? typed : default;
}

/// <summary>
/// A command that runs a task.  By default one run at a time: the command
/// reports itself unavailable until the run in flight is over, so a slow one -
/// a delete, a paste - is not started a second time over the first.
/// </summary>
/// <param name="allowConcurrent">
/// Lets a second run start while the first is still going, for going
/// somewhere.  A command shared by every entry of a list - the navigation
/// pane's - would otherwise switch the whole list off while one share takes
/// its time to answer, and Enter in the address bar would do nothing; the
/// navigation itself sees to it that the last one asked for is the one that
/// wins.
/// </param>
public sealed class AsyncRelayCommand<T>(Func<T?, Task> execute, Predicate<T?>? canExecute = null, bool allowConcurrent = false) : ICommand
{
    private bool _isRunning;
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isRunning && (canExecute?.Invoke(Coerce(parameter)) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        if (allowConcurrent)
        {
            await execute(Coerce(parameter));
            return;
        }

        _isRunning = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await execute(Coerce(parameter));
        }
        finally
        {
            _isRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static T? Coerce(object? parameter) => parameter is T typed ? typed : default;
}

/// <inheritdoc cref="AsyncRelayCommand{T}"/>
public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, bool allowConcurrent = false) : ICommand
{
    private bool _isRunning;
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isRunning && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        if (allowConcurrent)
        {
            await execute();
            return;
        }

        _isRunning = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await execute();
        }
        finally
        {
            _isRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
