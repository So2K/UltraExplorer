using System.Windows;
using System.Windows.Input;
using UltraExplorer.Models;

namespace UltraExplorer.Dialogs;

public partial class InputDialog : Window
{
    private bool _ownerOnly;
    private bool _accepted;
    private readonly bool _password;
    private readonly CreateItemPrompt? _creation;

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
    /// <param name="password">Shows masked input and keeps its value out of the ordinary text field.</param>
    public InputDialog(string title, string prompt, string initialValue, bool selectStem = true, bool password = false, CreateItemPrompt? creation = null)
    {
        InitializeComponent();
        _password = password;
        _creation = creation;
        Title = title;
        TitleText.Text = title;
        PromptText.Text = prompt;
        if (password)
        {
            ValueTextBox.Visibility = Visibility.Collapsed;
            ValuePasswordBox.Visibility = Visibility.Visible;
            ValuePasswordBox.Password = initialValue;
        }
        else
        {
            ValueTextBox.Text = initialValue;
        }
        if (creation is not null)
        {
            Width = 510;
            DestinationPanel.Visibility = Visibility.Visible;
            DestinationTextBox.Text = creation.TargetDirectory;
            DestinationTextBox.ToolTip = creation.TargetDirectory;
            ValueTextBox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
                creation.IsFolder ? "Folder name" : "File name");
            ConfirmButton.Content = creation.ConfirmLabel;
            ValueTextBox.TextChanged += (_, _) => UpdateNameValidation();
            UpdateNameValidation();
        }
        Loaded += (_, _) =>
        {
            if (_password)
            {
                ValuePasswordBox.Focus();
                ValuePasswordBox.SelectAll();
                return;
            }
            ValueTextBox.Focus();
            var extensionIndex = selectStem ? initialValue.LastIndexOf('.') : -1;
            ValueTextBox.Select(0, extensionIndex > 0 ? extensionIndex : initialValue.Length);
        };
    }

    public string Value => _password ? ValuePasswordBox.Password : ValueTextBox.Text;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_creation is not null && !UpdateNameValidation()) return;
        Complete(true);
    }

    private bool UpdateNameValidation()
    {
        var error = _creation?.ValidationError(ValueTextBox.Text);
        ConfirmButton.IsEnabled = error is null;
        NameErrorText.Text = error ?? string.Empty;
        NameErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        return error is null;
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
