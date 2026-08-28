namespace UltraExplorer.Models;

public sealed record SearchResultViewModel(
    string Name,
    string FullPath,
    string ParentPath,
    bool IsDirectory,
    string Glyph);
