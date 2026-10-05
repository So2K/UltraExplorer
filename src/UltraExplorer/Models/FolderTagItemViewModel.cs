using System.Windows.Media;
using UltraExplorer.Infrastructure;
using UltraExplorer.Services;

namespace UltraExplorer.Models;

/// <summary>
/// One coloured folder shown in the navigation pane's Tags section.  The
/// colour still lives in <see cref="Services.FolderMarkService"/>; this is
/// only a small, disposable projection for the sidebar.
/// </summary>
public sealed class FolderTagItemViewModel : ObservableObject
{
    private string _accentHex;
    private Brush _accentBrush;
    private bool _isActive;
    private string _parentHint = string.Empty;

    public FolderTagItemViewModel(string fullPath, string accentHex)
    {
        FullPath = fullPath;
        Name = DisplayName(fullPath);
        _accentHex = accentHex;
        _accentBrush = ColourBrush(accentHex);
    }

    public string FullPath { get; }
    public string Name { get; }
    public string ToolTipText => FullPath;
    public string AccessibleName => $"{Name}, {ColourName} tag, {FullPath}";
    public string ParentHint => _parentHint;

    public string AccentHex
    {
        get => _accentHex;
        private set => SetProperty(ref _accentHex, value);
    }

    public Brush AccentBrush
    {
        get => _accentBrush;
        private set => SetProperty(ref _accentBrush, value);
    }

    public string ColourName => NameOf(AccentHex);
    public int ColourOrder => OrderOf(AccentHex);

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
            {
                OnPropertyChanged(nameof(StateTag));
            }
        }
    }

    public string StateTag => IsActive ? "Active" : "Idle";

    public void UpdateAccent(string accentHex)
    {
        if (string.Equals(AccentHex, accentHex, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AccentHex = accentHex;
        AccentBrush = ColourBrush(accentHex);
        OnPropertyChanged(nameof(ColourName));
        OnPropertyChanged(nameof(ColourOrder));
        OnPropertyChanged(nameof(AccessibleName));
    }

    internal void SetParentHint(bool show)
    {
        var hint = show ? $"— {ParentName(FullPath)}" : string.Empty;
        SetProperty(ref _parentHint, hint, nameof(ParentHint));
    }

    public static int OrderOf(string accentHex) => accentHex.ToUpperInvariant() switch
    {
        "#EF5A68" => 0,
        "#F28A4B" => 1,
        "#E3B341" => 2,
        "#4ED6A0" => 3,
        "#4CC9D8" => 4,
        "#60CDFF" => 5,
        "#A979FF" or "#9B6BFF" => 6,
        _ => 7
    };

    private static string NameOf(string accentHex) => accentHex.ToUpperInvariant() switch
    {
        "#EF5A68" => "Red",
        "#F28A4B" => "Orange",
        "#E3B341" => "Yellow",
        "#4ED6A0" => "Green",
        "#4CC9D8" => "Cyan",
        "#60CDFF" => "Blue",
        "#A979FF" or "#9B6BFF" => "Violet",
        _ => "Custom colour"
    };

    private static Brush ColourBrush(string accentHex)
    {
        try
        {
            return BrushCache.Get(accentHex);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException)
        {
            return BrushCache.Get(FolderMarkService.DefaultAccentHex);
        }
    }

    private static string DisplayName(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static string ParentName(string path)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        if (string.IsNullOrWhiteSpace(parent))
        {
            return path;
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(parent));
        return string.IsNullOrWhiteSpace(name) ? parent : name;
    }
}
