namespace UltraExplorer.Services;

public enum FolderInvocationKind
{
    OpenFolder,
    Reveal
}

/// <summary>A normal Explorer window request, with its complete selection.</summary>
public sealed record FolderInvocation(
    FolderInvocationKind Kind,
    string FolderPath,
    IReadOnlyList<string> SelectedPaths,
    bool OriginIsShell = false,
    Guid DestinationId = default);

/// <summary>
/// Explicit filesystem launches. Windows passes quoted arguments to .NET
/// already unquoted; shell namespaces and dialog switches stay with their own
/// entry points rather than being mistaken for folders.
/// </summary>
public static class FolderCommandLine
{
    public const string OpenFolderSwitch = "--open-folder";
    public const string RevealSwitch = "--reveal";
    public const string ShellRequestSwitch = "--shell-request";
    public const string HomeSwitch = "--home";
    public const string NewWindowSwitch = "--new-window";
    public const string DestinationSwitch = "--destination-id";

    public static bool IsInvocation(IReadOnlyList<string> args)
        => args.Any(argument => IsSwitch(argument, OpenFolderSwitch) || IsSwitch(argument, RevealSwitch)
            || IsSwitch(argument, ShellRequestSwitch) || IsSwitch(argument, HomeSwitch)
            || IsSwitch(argument, NewWindowSwitch) || IsSwitch(argument, DestinationSwitch));

    public static bool TryParse(IReadOnlyList<string> args, out FolderInvocation invocation, out string error)
    {
        invocation = null!;
        error = string.Empty;
        string? folder = null;
        var shellRequest = false;
        var destination = Guid.Empty;
        var wantsNewWindow = false;
        var selected = new List<string>();
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (IsSwitch(argument, NewWindowSwitch))
            {
                if (wantsNewWindow || destination != Guid.Empty) { error = "Use one new-window destination."; return false; }
                wantsNewWindow = true;
                continue;
            }
            if (IsSwitch(argument, DestinationSwitch))
            {
                if (wantsNewWindow || destination != Guid.Empty || ++index >= args.Count
                    || !Guid.TryParse(args[index], out destination) || destination == Guid.Empty)
                { error = "--destination-id requires one nonempty GUID."; return false; }
                continue;
            }
            if (IsSwitch(argument, ShellRequestSwitch))
            {
                if (shellRequest)
                {
                    error = "--shell-request can only be specified once.";
                    return false;
                }

                shellRequest = true;
                continue;
            }

            if (IsSwitch(argument, HomeSwitch))
            {
                if (folder is not null || selected.Count > 0)
                {
                    error = "Use one --home or --open-folder action, or repeated --reveal paths.";
                    return false;
                }

                folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                continue;
            }

            var opens = IsSwitch(argument, OpenFolderSwitch);
            if (!opens && !IsSwitch(argument, RevealSwitch))
            {
                error = $"Unknown folder launch argument: {argument}";
                return false;
            }

            if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index])
                || args[index].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"{argument} requires a filesystem path.";
                return false;
            }

            if (opens)
            {
                if (folder is not null || selected.Count > 0)
                {
                    error = "Use one --open-folder path, or one --reveal switch for each selected item.";
                    return false;
                }

                folder = args[index];
            }
            else
            {
                if (folder is not null)
                {
                    error = "--open-folder and --reveal cannot be combined.";
                    return false;
                }

                selected.Add(args[index]);
            }
        }

        var parsed = folder is not null
            ? TryOpenFolder(folder, out invocation, out error)
            : TryReveal(selected, out invocation, out error);
        if (parsed) invocation = invocation with { OriginIsShell = shellRequest,
            DestinationId = wantsNewWindow ? Guid.NewGuid() : destination };
        return parsed;
    }

    public static bool TryOpenFolder(string path, out FolderInvocation invocation, out string error)
    {
        invocation = null!;
        if (!TryPath(path, out var normalized, out error)) return false;
        if (!Directory.Exists(OnDisk(normalized)))
        {
            error = $"That folder is not available: {normalized}";
            return false;
        }

        invocation = new(FolderInvocationKind.OpenFolder, normalized, Array.Empty<string>());
        return true;
    }

    public static bool TryReveal(IEnumerable<string> paths, out FolderInvocation invocation, out string error)
    {
        invocation = null!;
        error = string.Empty;
        var selected = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!TryPath(path, out var normalized, out error)) return false;
            if (!Directory.Exists(OnDisk(normalized)) && !File.Exists(OnDisk(normalized)))
            {
                error = $"That item is not available: {normalized}";
                return false;
            }

            if (seen.Add(normalized)) selected.Add(normalized);
        }

        if (selected.Count == 0)
        {
            error = "--reveal requires at least one filesystem path.";
            return false;
        }

        // A directory is revealed in its parent; opening its contents is the
        // separate --open-folder action. A drive has no parent, so it opens.
        var parent = Path.GetDirectoryName(selected[0]);
        if (string.IsNullOrEmpty(parent))
        {
            if (selected.Count == 1) return TryOpenFolder(selected[0], out invocation, out error);
            error = "A drive cannot be part of a reveal selection.";
            return false;
        }

        invocation = new(FolderInvocationKind.Reveal, parent, selected.ToArray());
        return true;
    }

    /// <summary>For ProcessStartInfo.ArgumentList: each path is its own argument.</summary>
    public static string[] BuildArguments(FolderInvocation invocation)
    {
        string[] arguments = invocation.Kind == FolderInvocationKind.OpenFolder
            ? [OpenFolderSwitch, invocation.FolderPath]
            : invocation.SelectedPaths.SelectMany(path => new[] { RevealSwitch, path }).ToArray();
        if (invocation.OriginIsShell) arguments = [ShellRequestSwitch, .. arguments];
        if (invocation.DestinationId != Guid.Empty) arguments = [DestinationSwitch, invocation.DestinationId.ToString("D"), .. arguments];
        return arguments;
    }

    /// <summary>For registry command strings and callers requiring a quoted command line.</summary>
    public static string BuildCommandLine(FolderInvocation invocation)
        => NativeShellService.BuildCommandLine(BuildArguments(invocation));

    private static bool IsSwitch(string argument, string name)
        => argument.Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>How Windows is asked whether such a name exists: through its
    /// extended-length form, the one spelling it does not cut the end off.</summary>
    private static string OnDisk(string path)
        => Models.ViewAllPath.EndsANameInDotOrSpace(path) && Path.IsPathFullyQualified(path)
            && !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal)
                ? path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path
                : path;

    private static bool TryPath(string path, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\0', '"']) >= 0)
        {
            error = "A valid filesystem path is required.";
            return false;
        }

        try
        {
            // A name ending in a dot or a space keeps it: normalising cuts it
            // off, and "backup." would open its neighbour "backup", or nothing.
            normalized = Models.ViewAllPath.KeepNameEnds(path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"That path cannot be opened: {exception.Message}";
            return false;
        }
    }
}
