namespace UltraExplorer.Services.Search;

/// <summary>Where a result is, seen from the folder the search was made in.</summary>
public enum SearchPlace
{
    /// <summary>Right in the folder.</summary>
    Here,

    /// <summary>In a folder inside it, however deep.</summary>
    Below,

    /// <summary>Anywhere else.</summary>
    Elsewhere,
}

/// <summary>
/// One file or folder a search found, with what the order of the results is
/// worked out from (<see cref="SearchRanking"/>).
/// </summary>
internal sealed class SearchHit
{
    public SearchHit(string name, string directory, bool isFolder, long size, DateTime? modified, string? highlighted)
    {
        Name = name;
        Directory = directory;
        IsFolder = isFolder;
        Size = size;
        Modified = modified;
        Highlighted = string.IsNullOrEmpty(highlighted) ? null : highlighted;
        FullPath = Join(directory, name);
    }

    public string Name { get; }

    /// <summary>The folder it is in; empty for a drive.</summary>
    public string Directory { get; }

    public string FullPath { get; }

    public bool IsFolder { get; }

    /// <summary>Bytes, or -1 when not known.</summary>
    public long Size { get; }

    public DateTime? Modified { get; }

    /// <summary>Everything's highlighted name: the matched parts between asterisks.  Null when the search did not come from Everything.</summary>
    public string? Highlighted { get; }

    public SearchPlace Place { get; set; } = SearchPlace.Elsewhere;

    /// <summary>How well the name matches: 0 is exactly, 4 not by name at all (<see cref="SearchQuery.Quality"/>).</summary>
    public int Quality { get; set; }

    /// <summary>1 for a place results are rarely wanted from - caches, packages, the Windows component store - so they sink.</summary>
    public int Noise { get; set; }

    /// <summary>How many folders its path shares with the folder searched from, from the drive down.</summary>
    public int Proximity { get; set; }

    /// <summary>How many separators its path has.</summary>
    public int Depth { get; set; }

    /// <summary>The folder and the name as one path: a drive's own name is C:\.</summary>
    internal static string Join(string directory, string name)
    {
        if (directory.Length == 0)
        {
            return name.Length == 2 && name[1] == ':' ? name + "\\" : name;
        }

        return directory.EndsWith('\\') ? directory + name : directory + "\\" + name;
    }
}
