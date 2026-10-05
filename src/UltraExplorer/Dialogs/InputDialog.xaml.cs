using System.Windows;
using System.Windows.Input;

namespace UltraExplorer.Dialogs;

public partial class InputDialog : Window
{
    /// <param name="selectStem">
    /// Picks out the text before its last dot, so typing replaces a file's
    /// name and keeps its extension.  Off, the whole text is picked out - for
    /// a folder's name or a note, where what follows a dot is no extension:
    /// "Photos 2024.06" renamed by typing "Archive" is "Archive".
    /// </param>
    public InputDialog(string title, string prompt, string initialValue, bool selectStem = true)
    {
        InitializeComponent();
        TitleText.Text = title;
        PromptText.Text = prompt;
        ValueTextBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            ValueTextBox.Focus();
            var extensionIndex = selectStem ? initialValue.LastIndexOf('.') : -1;
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
