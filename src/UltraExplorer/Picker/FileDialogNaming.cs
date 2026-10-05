namespace UltraExplorer.Picker;

/// <summary>
/// Turns what the user typed in the file-name box into paths, the same way the
/// standard dialog does: several quoted names are several files, a bare name is
/// relative to the folder in view, environment variables expand, and a name
/// with no extension gets the one the file type implies.
/// </summary>
public static class FileDialogNaming
{
    /// <summary>
    /// Splits <c>"first.txt" "second.txt"</c> into two names.  Without quotes
    /// the whole string is one name, because spaces are legal in file names and
    /// splitting on them is how the old dialog used to lose files.
    /// </summary>
    public static IReadOnlyList<string> SplitTypedNames(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var trimmed = text.Trim();
        if (!trimmed.Contains('"'))
        {
            return [trimmed];
        }

        var names = new List<string>();
        var start = -1;
        for (var index = 0; index < trimmed.Length; index++)
        {
            if (trimmed[index] != '"')
            {
                continue;
            }

            if (start < 0)
            {
                start = index + 1;
            }
            else
            {
                var name = trimmed[start..index].Trim();
                if (name.Length > 0)
                {
                    names.Add(name);
                }

                start = -1;
            }
        }

        // An unterminated quote still contains a name the user meant.
        if (start >= 0)
        {
            var tail = trimmed[start..].Trim();
            if (tail.Length > 0)
            {
                names.Add(tail);
            }
        }

        return names.Count == 0 ? [trimmed] : names;
    }

    /// <summary>
    /// Makes one typed name absolute.  Returns null when there is nothing
    /// usable — an empty name, or a relative name with no folder in view.
    /// </summary>
    public static string? ToFullPath(string? name, string? currentFolder)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(name.Trim().Trim('"'));
        if (expanded.Length == 0)
        {
            return null;
        }

        try
        {
            if (Path.IsPathFullyQualified(expanded))
            {
                return Path.GetFullPath(expanded);
            }

            // "C:" on its own means that drive's root, not its current
            // directory, which is what GetFullPath would otherwise give.
            if (expanded.Length == 2 && expanded[1] == ':' && char.IsLetter(expanded[0]))
            {
                return expanded + Path.DirectorySeparatorChar;
            }

            if (string.IsNullOrEmpty(currentFolder))
            {
                return null;
            }

            var here = Path.GetFullPath(currentFolder);
            if (Path.IsPathRooted(expanded))
            {
                // A drive with no root ("D:mix.wav") or a root with no drive
                // ("\Exports\mix.wav") is finished from the folder in view.
                // GetFullPath would finish it from this process's own drive
                // and per-drive folders, which mean nothing to the user.
                var root = Path.GetPathRoot(expanded)!;
                expanded = root.Length == 2
                    ? Path.Combine(here.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? here : root + Path.DirectorySeparatorChar, expanded[2..])
                    : Path.GetPathRoot(here)!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + expanded;
            }

            return Path.GetFullPath(Path.Combine(here, expanded));
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when a typed name ends in a dot: "report." saves as "report", with
    /// no extension added, as in the standard dialog.
    /// </summary>
    public static bool AsksForNoExtension(string typedName) =>
        typedName.Trim().Trim('"').TrimEnd().EndsWith('.');

    /// <summary>
    /// Appends the extension a save is expected to have: the caller's default
    /// extension if it set one, otherwise the first literal extension of the
    /// selected file type.  A name that already has an extension, or that ends
    /// in a dot to say "no extension, thank you", is left alone.
    /// </summary>
    public static string ApplyDefaultExtension(string name, string? defaultExtension, FileDialogFilter? filter)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var trimmed = name.Trim();
        if (trimmed.EndsWith('.'))
        {
            return trimmed.TrimEnd('.');
        }

        if (Path.HasExtension(trimmed))
        {
            return trimmed;
        }

        var extension = !string.IsNullOrWhiteSpace(defaultExtension)
            ? defaultExtension
            : filter is { MatchesEverything: false } ? filter.PreferredExtension : null;

        return string.IsNullOrWhiteSpace(extension)
            ? trimmed
            : $"{trimmed}.{extension.TrimStart('.')}";
    }

    /// <summary>
    /// The text the file-name box should show for a selection, quoting each
    /// name only when there is more than one, as the standard dialog does.
    /// </summary>
    public static string Describe(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return string.Empty;
        }

        if (paths.Count == 1)
        {
            return Path.GetFileName(paths[0]) is { Length: > 0 } name ? name : paths[0];
        }

        return string.Join(' ', paths.Select(path =>
        {
            var name = Path.GetFileName(path);
            return $"\"{(name.Length > 0 ? name : path)}\"";
        }));
    }
}
