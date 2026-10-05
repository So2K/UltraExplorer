using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

/// <summary>A small passive card in the window's visual tree; never captures input or focus.</summary>
internal sealed class HoverPreviewCard : Border
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
    private readonly TextBlock _loading = new()
    {
        Text = "Loading preview…", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _name = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _detail = new() { FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Grid _picture = new() { Margin = new Thickness(0, 0, 0, 10) };

    internal HoverPreviewCard()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Visibility = Visibility.Collapsed;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(12);
        SetResourceReference(BackgroundProperty, "SurfaceRaisedBrush");
        SetResourceReference(BorderBrushProperty, "BorderStrongBrush");
        _name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        _detail.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        _loading.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        _picture.Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18));
        _picture.Children.Add(_image);
        _picture.Children.Add(_loading);
        var body = new StackPanel();
        body.Children.Add(_picture);
        body.Children.Add(_name);
        body.Children.Add(_detail);
        Child = body;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(this, "File preview");
    }

    internal void Show(string path, ThumbnailResult? result, double availableWidth, double availableHeight)
    {
        Width = Math.Min(344, Math.Max(150, availableWidth - 16));
        _picture.Height = Math.Min(240, Math.Max(24, availableHeight - 112));
        _name.Text = System.IO.Path.GetFileName(path);
        _image.Source = result?.Image;
        _loading.Visibility = result is null ? Visibility.Visible : Visibility.Collapsed;
        _detail.Text = result is { OriginalWidth: { } width, OriginalHeight: { } height }
            ? $"{width:N0} × {height:N0}" : "Preview";
        AutomationProperties.SetName(this, $"Preview of {_name.Text}");
        Visibility = Visibility.Visible;
        Measure(new Size(Width, double.PositiveInfinity));
    }

    internal void Hide()
    {
        Visibility = Visibility.Collapsed;
        _image.Source = null;
    }

    internal static Point Place(Point pointer, Size card, Size viewport)
    {
        const double gap = 22;
        var x = pointer.X + gap;
        var y = pointer.Y + gap;
        if (x + card.Width > viewport.Width - 8) x = pointer.X - card.Width - gap;
        if (y + card.Height > viewport.Height - 8) y = pointer.Y - card.Height - gap;
        return new Point(Math.Clamp(x, 8, Math.Max(8, viewport.Width - card.Width - 8)),
            Math.Clamp(y, 8, Math.Max(8, viewport.Height - card.Height - 8)));
    }
}
