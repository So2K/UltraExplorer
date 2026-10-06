using System.Windows;
using System.Windows.Threading;

namespace UltraExplorer.Dialogs;

/// <summary>Waits for one owned prompt without WPF's thread-wide modal disabling.</summary>
internal static class OwnerOnlyModal
{
    internal static void Show(Window dialog)
    {
        dialog.Dispatcher.VerifyAccess();
        var owner = dialog.Owner;
        var enabled = owner?.IsEnabled ?? false;
        var frame = new DispatcherFrame();
        var closed = false;
        void OnClosed(object? sender, EventArgs args) { closed = true; frame.Continue = false; }
        dialog.Closed += OnClosed;
        try
        {
            owner?.SetCurrentValue(UIElement.IsEnabledProperty, false);
            dialog.Show();
            if (!closed) Dispatcher.PushFrame(frame);
        }
        finally
        {
            dialog.Closed -= OnClosed;
            owner?.SetCurrentValue(UIElement.IsEnabledProperty, enabled);
        }
    }
}
