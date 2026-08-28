using System.Windows;
using System.Windows.Input;

namespace UltraExplorer.Dialogs;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmLabel = "Delete")
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmLabel;
    }

    /// <summary>
    /// A message with nothing to decide: one button, and Escape closes it.  The
    /// file dialog needs this for the refusals the validator produces.
    /// </summary>
    public static void Alert(Window? owner, string title, string message)
    {
        var dialog = new ConfirmDialog(title, message, "OK") { Owner = owner };
        dialog.CancelButton.Visibility = Visibility.Collapsed;
        dialog.ShowDialog();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }
}
