using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace UltraExplorer.Picker.Integration;

public partial class NativeOptionsView : UserControl
{
    internal event Action<bool>? UseWindows;
    public NativeOptionsView() { InitializeComponent(); }

    internal void SetOptions(string application, FileDialogMode mode, ObservableCollection<NativeOptionViewModel> options)
    {
        var name = NativeDialogRules.ApplicationName(application, ReadDescription(application));
        ApplicationLabel.Text = mode switch
        {
            FileDialogMode.Save => $"Saving for {name}, which still asks before replacing a file.",
            FileDialogMode.PickFolder => $"Choosing a folder for {name}.",
            _ => $"Opening for {name}."
        };
        OptionsHeading.Text = $"Options from {name}";
        ExcludeButton.Content = $"Always use Windows for {name}";
        AutomationProperties.SetName(ExcludeButton, $"Always use Windows for {name}");
        UseWindowsButton.ToolTip = $"Close this and continue in {name}'s own Windows dialog. Nothing has been chosen yet.";
        ExcludeButton.ToolTip = $"{name} always shows its Windows dialog from now on. Settings, File dialogs, Application exceptions undoes it.";
        OptionsList.ItemsSource = options;
        options.CollectionChanged += (_, _) => ShowOptions(options.Count);
        ShowOptions(options.Count);
    }

    private void ShowOptions(int count) => OptionsPanel.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private static string? ReadDescription(string application)
    {
        try { return FileVersionInfo.GetVersionInfo(application).FileDescription; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private void UseWindows_Click(object sender, RoutedEventArgs e) => UseWindows?.Invoke(false);
    private void Exclude_Click(object sender, RoutedEventArgs e) => UseWindows?.Invoke(true);
}

public sealed class NativeOptionTemplateSelector : DataTemplateSelector
{
    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not NativeOptionViewModel option || container is not FrameworkElement element) return null;
        var key = option.Kind switch
        {
            NativeOptionKind.CheckBox => "NativeCheck", NativeOptionKind.RadioButton => "NativeRadio",
            NativeOptionKind.ComboBox => "NativeCombo", NativeOptionKind.TextBox => "NativeText", _ => "NativeLabel"
        };
        return element.FindResource(key) as DataTemplate;
    }
}
