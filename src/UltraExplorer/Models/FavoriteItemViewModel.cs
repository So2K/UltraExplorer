using System.Windows.Media;
using UltraExplorer.Infrastructure;

namespace UltraExplorer.Models;

public enum SidebarItemKind
{
    QuickAccess,
    Drive,
    Network
}

public sealed class FavoriteItemViewModel : ObservableObject
{
    private bool _isActive;
    private ImageSource? _icon;

    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Glyph { get; init; }
    public string AccentHex { get; init; } = "#98A2B3";
    public SidebarItemKind Kind { get; init; } = SidebarItemKind.QuickAccess;
    public bool IsCustom { get; init; }

    /// <summary>Set when the entry opens in Explorer instead of the graph.</summary>
    public bool OpensInShell { get; init; }

    public string ToolTipText => OpensInShell ? $"{Path} — opens in File Explorer" : Path;

    public ImageSource? Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }

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

    /// <summary>Drives the selected visual of <c>SidebarItem</c>.</summary>
    public string StateTag => _isActive ? "Active" : "Idle";
}
