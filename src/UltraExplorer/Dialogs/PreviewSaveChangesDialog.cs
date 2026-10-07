using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace UltraExplorer.Dialogs;

internal enum PreviewSaveChoice { Cancel, Discard, Save }

/// <summary>The existing app's dialog palette, with an explicit safe cancel choice.</summary>
internal sealed class PreviewSaveChangesDialog : Window
{
    private PreviewSaveChoice _choice;
    private PreviewSaveChangesDialog(Window owner, string filename)
    {
        Owner = owner; Width = 470; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Save text changes?", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextBrush") });
        panel.Children.Add(new TextBlock { Text = filename, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("TextMutedBrush") });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        foreach (var (label, choice) in new[] { ("Cancel", PreviewSaveChoice.Cancel), ("Don't save", PreviewSaveChoice.Discard), ("Save", PreviewSaveChoice.Save) })
        {
            var button = new Button { Content = label, MinWidth = 82, Margin = new Thickness(4, 0, 0, 0), IsDefault = choice == PreviewSaveChoice.Cancel };
            button.SetResourceReference(StyleProperty, choice == PreviewSaveChoice.Save ? "AccentButton" : "FlatButton");
            button.Click += (_, _) => { _choice = choice; Close(); };
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons);
        Content = new Border { Child = panel, Margin = new Thickness(16), Padding = new Thickness(22), CornerRadius = new CornerRadius(12),
            Background = (Brush)FindResource("SurfaceRaisedBrush"), BorderBrush = (Brush)FindResource("BorderStrongBrush"), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 8, Opacity = .55, Color = Colors.Black } };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { _choice = PreviewSaveChoice.Cancel; Close(); e.Handled = true; } };
    }
    internal static PreviewSaveChoice Ask(Window owner, string filename)
    {
        var dialog = new PreviewSaveChangesDialog(owner, filename);
        OwnerOnlyModal.Show(dialog);
        return dialog._choice;
    }
}
