using System.Windows;
using System.Windows.Input;
using UltraExplorer.Dialogs;
using UltraExplorer.Infrastructure;
using UltraExplorer.ViewModels;

namespace UltraExplorer;

/// <summary>
/// The way to the Settings window: the gear in the command bar, the first
/// item of the More menu, the last of Canvas options, and Ctrl+, as in most
/// Windows programs.  One window at a time; asking again brings it forward.
/// </summary>
public partial class MainWindow
{
    private SettingsWindow? _settingsWindow;
    private RelayCommand? _openSettingsCommand;

    /// <summary>What the Settings items of the menus run.</summary>
    internal ICommand OpenSettingsCommand => _openSettingsCommand ??= new RelayCommand(OpenSettings);

    /// <summary>
    /// Shows the Settings window in place of <see cref="OpenSettings"/>'s own
    /// showing, when set: the checks open it through every way in without a
    /// window ever appearing.
    /// </summary>
    internal Action<SettingsWindow>? SettingsPresenter { get; set; }

    /// <summary>The Settings window while it is open.</summary>
    internal SettingsWindow? OpenSettingsWindow => _settingsWindow;

    /// <summary>Whether a key press is the Settings shortcut, Ctrl+comma.</summary>
    internal static bool IsSettingsGesture(Key key, ModifierKeys modifiers) =>
        modifiers == ModifierKeys.Control && key == Key.OemComma;

    /// <summary>Opens Settings when the key is its shortcut; what the window's key handler asks first.</summary>
    internal bool TryOpenSettingsFromKey(Key key, ModifierKeys modifiers)
    {
        if (!IsSettingsGesture(key, modifiers))
        {
            return false;
        }

        OpenSettings();
        return true;
    }

    /// <summary>
    /// Opens the Settings window over this one, or brings it forward if it is
    /// open.  It is sized to fit inside this window, so on a small screen it
    /// never runs off the bottom.
    /// </summary>
    internal void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            var settings = new SettingsViewModel(
                _viewModel,
                canChooseLayout: !IsPickerMode,
                rendererNow: DescribeRendererNow,
                confirm: ConfirmFromSettings);
            _settingsWindow = new SettingsWindow(settings);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        var window = _settingsWindow;
        if (SettingsPresenter is { } present)
        {
            present(window);
            return;
        }

        if (window.IsVisible)
        {
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
            return;
        }

        window.Owner = this;
        window.Height = Math.Clamp(ActualHeight - 60, window.MinHeight, window.Height);

        // A test or diagnostics copy on the other monitor must not take the
        // keyboard from whatever the user is doing.
        window.ShowActivated = !(IsTestWindow || IsDiagnosticsRun);
        window.Show();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>What draws the nested canvas now - the pane being worked with - for the Renderer setting.</summary>
    private string DescribeRendererNow()
    {
        var canvas = ActivePane.Canvas;
        return SettingsViewModel.DescribeRendererNow(IsNested, canvas.IsSceneOnGpu, canvas.RendererAdapter, canvas.RendererReason);
    }

    /// <summary>The app's confirmation, over the Settings window when that is what asked.</summary>
    private bool ConfirmFromSettings(string title, string message, string confirmLabel)
    {
        Window owner = _settingsWindow is { IsVisible: true } settings ? settings : this;
        var dialog = new ConfirmDialog(title, message, confirmLabel) { Owner = owner };
        return dialog.ShowOwnerModal();
    }
}
