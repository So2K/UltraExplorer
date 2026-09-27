using System.Globalization;
using System.Windows.Media;
using UltraExplorer.Infrastructure;
using UltraExplorer.Services.Search;

namespace UltraExplorer.Models;

/// <summary>A row of the search results: a result, or the heading of a group of them.</summary>
public interface ISearchRow
{
    bool IsHeader { get; }
}

/// <summary>A piece of a name: matched by the search, and drawn so, or not.</summary>
public readonly record struct TextSegment(string Text, bool IsMatch);

/// <summary>The heading over a group of results: "In Downloads · 12".</summary>
public sealed class SearchHeaderRow(string title, string count, string? detail, SearchPlace place) : ISearchRow
{
    public bool IsHeader => true;

    public string Title => title;

    public string CountText => count;

    /// <summary>The full path of the folder the group is about, for its tooltip; null for "everywhere".</summary>
    public string? Detail => detail;

    public SearchPlace Place => place;
}

/// <summary>One file or folder in the search results.</summary>
public sealed class SearchResultViewModel : ObservableObject, ISearchRow
{
    private ImageSource? _icon;

    public SearchResultViewModel(string name, string fullPath, string parentPath, bool isDirectory, long size, DateTime? modified, SearchPlace place, IReadOnlyList<TextSegment> segments, string location)
    {
        Name = name;
        FullPath = fullPath;
        ParentPath = parentPath;
        IsDirectory = isDirectory;
        Size = size;
        Modified = modified;
        Place = place;
        NameSegments = segments;
        Location = location;
    }

    public bool IsHeader => false;

    public string Name { get; }

    public string FullPath { get; }

    public string ParentPath { get; }

    public bool IsDirectory { get; }

    /// <summary>Bytes, or -1 when not known.</summary>
    public long Size { get; }

    public DateTime? Modified { get; }

    public SearchPlace Place { get; }

    /// <summary>The name in pieces, the matched ones marked.</summary>
    public IReadOnlyList<TextSegment> NameSegments { get; }

    /// <summary>Where it is, as the row says it: relative to the folder searched from when inside it, shortened in the middle when long.</summary>
    public string Location { get; }

    /// <summary>Segoe Fluent glyph shown until the Shell's icon arrives.</summary>
    public string Glyph => IsDirectory ? "" : "";

    public Brush Tint => BrushCache.Get(IsDirectory ? "#E3B341" : "#8A94A6");

    public ImageSource? Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }

    public string SizeText => IsDirectory || Size < 0 ? string.Empty : Services.FileSystemService.FormatSize(Size);

    public string DateText => Modified is { } modified ? modified.ToString("g", CultureInfo.CurrentCulture) : string.Empty;
}
