namespace UltraExplorer.Models;

/// <summary>How the two panes of a split view are laid out.</summary>
public enum SplitOrientation
{
    /// <summary>The first pane on the left, the second on the right: the default.</summary>
    SideBySide,

    /// <summary>The first pane above, the second below.</summary>
    Stacked
}

/// <summary>
/// The split view's settings as the workspace writes them, and the limits
/// they are kept within.  A pane is never dragged narrower than a sixth or
/// so of the room: a sliver of canvas shows nothing, and a divider pushed
/// against the window's edge is hard to find again.
/// </summary>
public static class SplitLayout
{
    /// <summary>The smallest share of the room the first pane - and so the second - can be given.</summary>
    public const double MinimumRatio = 0.15;

    /// <summary>The largest share of the room the first pane can be given.</summary>
    public const double MaximumRatio = 0.85;

    /// <summary>Half each, as a split starts.</summary>
    public const double DefaultRatio = 0.5;

    /// <summary>The ratio kept within the limits; one that is not a number at all - a file edited by hand - is half.</summary>
    public static double ClampRatio(double ratio) =>
        double.IsFinite(ratio) ? Math.Clamp(ratio, MinimumRatio, MaximumRatio) : DefaultRatio;

    /// <summary>"Stacked", however it is spelled, is stacked; anything else, or nothing, side by side.</summary>
    public static SplitOrientation ParseOrientation(string? setting) =>
        string.Equals(setting?.Trim(), nameof(SplitOrientation.Stacked), StringComparison.OrdinalIgnoreCase)
            ? SplitOrientation.Stacked
            : SplitOrientation.SideBySide;

    /// <summary>What the workspace writes for an orientation.</summary>
    public static string OrientationSetting(SplitOrientation orientation) => orientation.ToString();
}
