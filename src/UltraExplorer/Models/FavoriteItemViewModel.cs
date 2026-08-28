using UltraExplorer.Infrastructure;

namespace UltraExplorer.Models;

public sealed class FavoriteItemViewModel : ObservableObject
{
    private bool _isCustom;

    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Glyph { get; init; }
    public string AccentHex { get; init; } = "#98A2B3";

    public bool IsCustom
    {
        get => _isCustom;
        init => _isCustom = value;
    }
}
