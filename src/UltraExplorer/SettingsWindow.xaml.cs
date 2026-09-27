using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using UltraExplorer.ViewModels;

namespace UltraExplorer;

/// <summary>
/// The Settings window: the choices the Canvas options menu keeps in its
/// submenus, on one page with a line on each saying what it does.  Owned by
/// the main window and centred on it.  There is nothing to confirm: each
/// change applies, and is remembered, as it is made, and Esc or the close
/// button puts the window away.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>What draws the canvas can change on its own - the card warmed up, the window moved to another monitor - so it is asked again while the window is open.</summary>
    private readonly DispatcherTimer _rendererPoll = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };

    internal SettingsWindow(SettingsViewModel settings)
    {
        InitializeComponent();
        Settings = settings;
        DataContext = settings;

        _rendererPoll.Tick += (_, _) => Settings.RefreshRendererStatus();
        Loaded += (_, _) => _rendererPoll.Start();
        Closed += (_, _) =>
        {
            _rendererPoll.Stop();
            Settings.Dispose();
        };

        // A page of settings has nothing to fill a screen with, and a
        // maximised window without a caption would hang over the monitor's
        // edges (see MainWindow.ClampMaximizedBounds): a double-click on the
        // title or Win+Up leaves it as it was.
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
            }
        };
    }

    internal SettingsViewModel Settings { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // The same dark frame and rounded corners as the main window.
        var handle = new WindowInteropHelper(this).Handle;
        var darkMode = 1;
        var cornerPreference = 2;
        _ = DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 33, ref cornerPreference, sizeof(int));

        // No maximise or minimise at all, rather than undoing them after the
        // fact: WindowChrome's system menu, a caption double-click, Win+Up and
        // Win+Down all read these styles, so none of them flashes a frame.
        var style = GetWindowLongPtr(handle, StyleIndex);
        _ = SetWindowLongPtr(handle, StyleIndex, style & ~(MaximizeBox | MinimizeBox));
    }

    private const int StyleIndex = -16;
    private const nint MaximizeBox = 0x00010000;
    private const nint MinimizeBox = 0x00020000;

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtr(IntPtr window, int index, nint value);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Esc closes an open list first, as it does everywhere else.
        if (e.Key == Key.Escape && !DefaultColumnBox.IsDropDownOpen)
        {
            e.Handled = true;
            Close();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);
}
