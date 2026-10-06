using System.Windows;
using System.Windows.Input;

namespace UltraExplorer.Dialogs;

public partial class ConfirmDialog : Window
{
    private bool _ownerOnly;
    private bool _accepted;

    internal bool ShowOwnerModal()
    {
        _ownerOnly = true;
        OwnerOnlyModal.Show(this);
        return _accepted;
    }

    private void Complete(bool accepted)
    {
        _accepted = accepted;
        if (!_ownerOnly) DialogResult = accepted;
        Close();
    }
    /// <param name="danger">
    /// Whether the confirming button is drawn red, for what destroys
    /// something; otherwise it is the usual accent button, as opening files is.
    /// </param>
    public ConfirmDialog(string title, string message, string confirmLabel = "Delete", bool danger = true)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmLabel;
        if (!danger)
        {
            ConfirmButton.ClearValue(BackgroundProperty);
            ConfirmButton.ClearValue(ForegroundProperty);
        }
    }

    /// <summary>
    /// A message with nothing to decide: one button, and Escape closes it.  The
    /// file dialog needs this for the refusals the validator produces.
    /// </summary>
    public static void Alert(Window? owner, string title, string message)
    {
        var dialog = new ConfirmDialog(title, message, "OK") { Owner = owner };
        dialog.CancelButton.Visibility = Visibility.Collapsed;
        dialog.ShowOwnerModal();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Complete(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Complete(false);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Complete(false);
            e.Handled = true;
        }
    }
}
