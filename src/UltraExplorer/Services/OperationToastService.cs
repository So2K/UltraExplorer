using UltraExplorer.Infrastructure;
using System.Windows.Input;

namespace UltraExplorer.Services;

public sealed class OperationToastService : ObservableObject
{
    private string _message = string.Empty;
    private bool _isVisible;
    private bool _isBusy;
    private CancellationTokenSource? _hideDelay;

    public OperationToastService()
    {
        HideCommand = new RelayCommand(Hide);
    }

    public ICommand HideCommand { get; }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        private set => SetProperty(ref _isVisible, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public void ShowBusy(string message)
    {
        CancelHide();
        Message = message;
        IsBusy = true;
        IsVisible = true;
    }

    public async Task ShowSuccessAsync(string message)
    {
        CancelHide();
        Message = message;
        IsBusy = false;
        IsVisible = true;
        _hideDelay = new CancellationTokenSource();
        try
        {
            await Task.Delay(2600, _hideDelay.Token);
            IsVisible = false;
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void ShowError(string message)
    {
        CancelHide();
        Message = message;
        IsBusy = false;
        IsVisible = true;
    }

    public void Hide()
    {
        CancelHide();
        IsVisible = false;
    }

    private void CancelHide()
    {
        _hideDelay?.Cancel();
        _hideDelay?.Dispose();
        _hideDelay = null;
    }
}
