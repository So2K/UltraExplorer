using System.Windows;
using System.Windows.Input;

namespace UltraExplorer.Dialogs;

public partial class InputDialog : Window
{
    public InputDialog(string title, string prompt, string initialValue)
    {
        InitializeComponent();
        TitleText.Text = title;
        PromptText.Text = prompt;
        ValueTextBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            ValueTextBox.Focus();
            var extensionIndex = initialValue.LastIndexOf('.');
            ValueTextBox.Select(0, extensionIndex > 0 ? extensionIndex : initialValue.Length);
        };
    }

    public string Value => ValueTextBox.Text;

    private void Ok_Click(object sender, RoutedEventArgs e)
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
