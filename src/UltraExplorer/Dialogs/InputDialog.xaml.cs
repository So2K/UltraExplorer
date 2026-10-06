using System.Windows;
using System.Windows.Input;

namespace UltraExplorer.Dialogs;

public partial class InputDialog : Window
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
