using System.Windows.Media;
using UltraExplorer.Infrastructure;

namespace UltraExplorer.Models;

/// <summary>What an address suggestion points at, which is what decides its glyph.</summary>
public enum AddressSuggestionKind
{
    Folder,
    File,
    Drive,

    /// <summary>Somewhere this session has already been.</summary>
    Recent
}

/// <summary>
/// One line under the address bar: a place the half-typed path could be finished
/// with.  Folders and drives come from the folder that was typed, recents from
/// where the window has already been.
/// </summary>
public sealed record AddressSuggestion(string Name, string FullPath, AddressSuggestionKind Kind)
{
    public string Glyph => Kind switch
    {
        AddressSuggestionKind.Drive => "",
        AddressSuggestionKind.File => "",
        AddressSuggestionKind.Recent => "",
        _ => ""
    };

    public Brush Tint => BrushCache.Get(Kind switch
    {
        AddressSuggestionKind.File => "#8A94A6",
        AddressSuggestionKind.Recent => "#9A9A9A",
        _ => "#E3B341"
    });

    /// <summary>
    /// The path spelled out, for the rows where the name alone does not say where
    /// the thing is.  A folder under the folder that was typed needs no such
    /// explanation; somewhere visited an hour ago does.
    /// </summary>
    public string Detail => Kind == AddressSuggestionKind.Recent ? FullPath : string.Empty;
}
