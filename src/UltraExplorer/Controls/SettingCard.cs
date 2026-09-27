using System.Windows;
using System.Windows.Controls;

namespace UltraExplorer.Controls;

/// <summary>
/// One row of the Settings window, laid out like a card in Windows 11's
/// Settings: a glyph, a title and a line saying what the option does on the
/// left, and the control that changes it (the card's content) on the right.
/// The look is the window's implicit style; this class only carries what the
/// row says.
/// </summary>
public sealed class SettingCard : ContentControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(SettingCard), new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SettingCard), new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingCard), new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(string), typeof(SettingCard), new FrameworkPropertyMetadata(string.Empty));

    /// <summary>A Segoe Fluent Icons glyph shown at the left of the card.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>The option's name.</summary>
    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>One line saying what the option does, under its name.</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>What is in force now, when that can differ from what was chosen; nothing shown when empty.</summary>
    public string Status
    {
        get => (string)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }
}

/// <summary>
/// Lays out a setting's words (the first child, its glyph included) and its
/// control (the second): side by side while the words keep at least
/// <see cref="MinimumTextWidth"/>, and the control under the words when the
/// window is too narrow for that, so a wide choice never squeezes its
/// explanation into a column one word wide.  Under the words the control
/// starts where they do, <see cref="StackedIndent"/> in from the glyph.  A
/// card with no control gives its words the whole width.
/// </summary>
public sealed class SettingCardPanel : Panel
{
    /// <summary>The space between the words and a control beside them.</summary>
    public double Gap { get; set; } = 20;

    /// <summary>The space between the words and a control under them.</summary>
    public double StackedGap { get; set; } = 10;

    /// <summary>How far in a control under the words starts: past the glyph, where the words do.</summary>
    public double StackedIndent { get; set; }

    /// <summary>The narrowest the words, glyph included, may become before the control moves under them.</summary>
    public double MinimumTextWidth { get; set; } = 248;

    /// <summary>Whether the last layout put the control under the words; for the checks.</summary>
    internal bool IsStacked { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count == 0)
        {
            return default;
        }

        var text = InternalChildren[0];
        var control = InternalChildren.Count > 1 ? InternalChildren[1] : null;
        control?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var controlSize = control is { Visibility: not Visibility.Collapsed } ? control.DesiredSize : default;

        if (controlSize.Width <= 0)
        {
            IsStacked = false;
            text.Measure(availableSize);
            return text.DesiredSize;
        }

        var besideWidth = availableSize.Width - controlSize.Width - Gap;
        IsStacked = !double.IsInfinity(availableSize.Width) && besideWidth < MinimumTextWidth;
        if (IsStacked)
        {
            text.Measure(availableSize);
            control!.Measure(new Size(Math.Max(0, availableSize.Width - StackedIndent), double.PositiveInfinity));
            return new Size(
                Math.Max(text.DesiredSize.Width, StackedIndent + control.DesiredSize.Width),
                text.DesiredSize.Height + StackedGap + control.DesiredSize.Height);
        }

        text.Measure(new Size(Math.Max(0, besideWidth), availableSize.Height));
        return new Size(
            text.DesiredSize.Width + Gap + controlSize.Width,
            Math.Max(text.DesiredSize.Height, controlSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count == 0)
        {
            return finalSize;
        }

        var text = InternalChildren[0];
        var control = InternalChildren.Count > 1 ? InternalChildren[1] : null;
        var controlSize = control is { Visibility: not Visibility.Collapsed } ? control.DesiredSize : default;
        if (controlSize.Width <= 0)
        {
            text.Arrange(new Rect(0, (finalSize.Height - text.DesiredSize.Height) / 2, finalSize.Width, text.DesiredSize.Height));
            control?.Arrange(default);
            return finalSize;
        }

        if (IsStacked)
        {
            text.Arrange(new Rect(0, 0, finalSize.Width, text.DesiredSize.Height));
            control!.Arrange(new Rect(
                StackedIndent,
                text.DesiredSize.Height + StackedGap,
                Math.Min(control.DesiredSize.Width, Math.Max(0, finalSize.Width - StackedIndent)),
                control.DesiredSize.Height));
            return finalSize;
        }

        var textWidth = Math.Max(0, finalSize.Width - controlSize.Width - Gap);
        text.Arrange(new Rect(0, (finalSize.Height - text.DesiredSize.Height) / 2, textWidth, text.DesiredSize.Height));
        control!.Arrange(new Rect(
            finalSize.Width - controlSize.Width,
            (finalSize.Height - controlSize.Height) / 2,
            controlSize.Width,
            controlSize.Height));
        return finalSize;
    }
}
