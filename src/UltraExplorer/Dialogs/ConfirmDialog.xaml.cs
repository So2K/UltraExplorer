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
