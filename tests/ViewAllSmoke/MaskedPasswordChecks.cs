using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using UltraExplorer.Dialogs;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task MaskedPasswordChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(MaskedPasswordChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(MaskedPasswordChecks));
            return Task.CompletedTask;
        }
        RunOnSta("masked archive password", () =>
        {
            if (Application.Current is null)
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/UltraExplorer;component/Themes/UltraTheme.xaml", UriKind.Relative)
                });
            }
            Section("archive password: masked value and ordinary rename behavior");
            var ordinary = new InputDialog("Rename", "Enter a new name", "report.docx");
            var folder = new InputDialog("Rename", "Enter a folder name", "Photos 2024.06", selectStem: false);
            var password = new InputDialog("Password", "Password for owned-fixture.zip", "owned.secret", selectStem: false, password: true);
            try
            {
                ordinary.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                folder.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                password.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Check("ordinary rename keeps its text box, value and stem selection",
                    ordinary.ValueTextBox.Visibility == Visibility.Visible && ordinary.ValuePasswordBox.Visibility == Visibility.Collapsed
                    && ordinary.Value == "report.docx" && ordinary.ValueTextBox.SelectionStart == 0 && ordinary.ValueTextBox.SelectionLength == 6);
                Check("folder rename still selects the whole name including its dot",
                    folder.ValueTextBox.SelectionLength == "Photos 2024.06".Length && folder.Value == "Photos 2024.06");
                Check("password mode exposes only the masked editor and keeps plaintext out of the ordinary text field",
                    password.ValueTextBox.Visibility == Visibility.Collapsed && password.ValueTextBox.Text.Length == 0
                    && password.ValuePasswordBox.Visibility == Visibility.Visible && password.ValuePasswordBox.PasswordChar == '●');
                Check("the password remains available to the accepted-value API", password.Value == "owned.secret");
                password.ValuePasswordBox.Password = "replacement.secret";
                Check("the returned value follows edits in the masked box without populating the hidden text box",
                    password.Value == "replacement.secret" && password.ValueTextBox.Text.Length == 0);
                var peer = new PasswordBoxAutomationPeer(password.ValuePasswordBox);
                Check("assistive technology identifies the field as a labelled password",
                    peer.IsPassword() && AutomationProperties.GetName(password.ValuePasswordBox) == "Password");
                var valueProtected = true;
                if (peer.GetPattern(PatternInterface.Value) is IValueProvider value)
                {
                    try { valueProtected = string.IsNullOrEmpty(value.Value); }
                    catch (InvalidOperationException) { }
                }
                Check("the automation value provider cannot return the password plaintext", valueProtected);
                password.ValuePasswordBox.ApplyTemplate();
                Check("the masked field uses the application's dark input palette and padding",
                    Equals(password.ValuePasswordBox.Background, ordinary.ValueTextBox.Background)
                    && Equals(password.ValuePasswordBox.Foreground, ordinary.ValueTextBox.Foreground)
                    && password.ValuePasswordBox.Padding == ordinary.ValueTextBox.Padding
                    && password.ValuePasswordBox.MinHeight == ordinary.ValueTextBox.MinHeight);
            }
            finally
            {
                password.Close();
                folder.Close();
                ordinary.Close();
            }
            return Task.CompletedTask;
        });
        return Task.CompletedTask;
    }
}
