using System.Windows;
using System.Windows.Input;
using UltraExplorer.Dialogs;
using UltraExplorer.ViewModels;

namespace UltraExplorer;

public partial class SearchBuilderWindow : Window
{
    private bool _ownerOnly;
    private bool _accepted;
    public SearchBuilderViewModel ViewModel { get; }
    public string QueryText => ViewModel.QueryText;

    public SearchBuilderWindow(string? existingQuery, string? currentFolder = null)
    {
        ViewModel = new(existingQuery, currentFolder);
        InitializeComponent();
        DataContext = ViewModel;
        var work = SystemParameters.WorkArea;
        MaxHeight = Math.Max(420, work.Height - 32);
        MaxWidth = Math.Max(400, work.Width - 32);
        Height = Math.Min(Height, MaxHeight);
        Width = Math.Min(Width, MaxWidth);
        if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1") ShowActivated = false;
        Loaded += (_, _) =>
        {
            if (!IsActive) return;
            NameInput.Focus();
            NameInput.SelectAll();
        };
    }

    internal bool ShowOwnerModal()
    {
        _ownerOnly = true;
        OwnerOnlyModal.Show(this);
        return _accepted;
    }
    private void Finish(bool accepted)
    {
        _accepted = accepted;
        if (!_ownerOnly) DialogResult = accepted;
        Close();
    }
    private void Apply_Click(object sender, RoutedEventArgs e) { if (ViewModel.CanApply) Finish(true); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(false);
    private void UseCurrent_Click(object sender, RoutedEventArgs e) => ViewModel.UseCurrentFolder();
    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Finish(false);
    }
}
