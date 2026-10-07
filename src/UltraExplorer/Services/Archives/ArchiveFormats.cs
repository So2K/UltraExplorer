namespace UltraExplorer.Services.Archives;

/// <summary>
/// Which files the canvas opens as folders.  Archives only - not every file
/// 7-Zip can look inside: an .exe, a .docx or a .jar stays a file, opened by
/// its program as it always was.
/// </summary>
internal static class ArchiveFormats
{
    private static readonly HashSet<string> Browsable = new(StringComparer.OrdinalIgnoreCase)
    {
        "zip", "7z", "rar", "tar", "tgz", "tbz", "tbz2", "txz", "tzst", "tlz", "taz",
        "gz", "bz2", "xz", "zst", "lz", "lzma", "z",
        "cab", "iso", "wim", "arj", "lzh", "lha", "cpio", "rpm", "deb", "dmg", "vhd", "vhdx", "squashfs",
        "001"
    };

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> Lookup = Browsable.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>Whether a file of this name is shown as a folder.</summary>
    public static bool IsBrowsable(ReadOnlySpan<char> name)
    {
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
        {
            return false;
        }

        if (!Lookup.Contains(name[(dot + 1)..]))
        {
            return false;
        }

        // A multi-part RAR is opened by its first part only: the rest are
        // the same archive again.
        if (name[(dot + 1)..].Equals("rar", StringComparison.OrdinalIgnoreCase))
        {
            var stem = name[..dot];
            var part = stem.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase);
            if (part > 0 && int.TryParse(stem[(part + 5)..], out var number))
            {
                return number == 1;
            }
        }

        // ".001" only as the first volume of a split archive: "x.7z.001".
        if (name[(dot + 1)..].SequenceEqual("001"))
        {
            var inner = name[..dot];
            var innerDot = inner.LastIndexOf('.');
            return innerDot > 0 && Lookup.Contains(inner[(innerDot + 1)..]);
        }

        return true;
    }

    /// <summary>The extensions a file's name carries, last first: "a.tar.gz" is gz, then tar.</summary>
    public static IEnumerable<string> ExtensionsOf(string path)
    {
        var name = Path.GetFileName(path);
        var parts = name.Split('.');
        for (var index = parts.Length - 1; index >= 1; index--)
        {
            yield return parts[index];
        }
    }
}
