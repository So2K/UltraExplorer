using System.IO;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ViewAllSmoke;

/// <summary>Release configuration contracts: both downloads carry the entire
/// self-contained folder. Updates remove only obsolete owned files, never a
/// directory/profile or all current runtime DLLs. No publishing or deletion is
/// performed by these checks.</summary>
internal static partial class Program
{
    private static Task ReleasePackagingChecks()
    {
        Section("release packaging");
        var root = PackagingRoot();
        var workflowPath = Path.Combine(root, ".github", "workflows", "release.yml");
        var projectPath = Path.Combine(root, "src", "UltraExplorer", "UltraExplorer.csproj");
        var setupPath = Path.Combine(root, "installer", "UltraExplorer.iss");
        if (!File.Exists(workflowPath) || !File.Exists(projectPath) || !File.Exists(setupPath))
        {
            Check($"the workflow, project and installer are beside the sources ({root})", false);
            return Task.CompletedTask;
        }
        var workflow = File.ReadAllLines(workflowPath);
        var workflowText = string.Join('\n', workflow);
        var publish = PackagingCommand(workflow, "dotnet publish");
        var zip = PackagingCommand(workflow, "Compress-Archive");
        var setup = File.ReadAllLines(setupPath);
        var setupText = string.Join('\n', setup);
        var project = XDocument.Load(projectPath);
        var selfExtractDefault = project.Descendants("IncludeNativeLibrariesForSelfExtract").Select(element => element.Value.Trim()).LastOrDefault();

        Check("the release is a self-contained folder with its .NET/WPF runtime",
            publish.Contains("--self-contained true", StringComparison.OrdinalIgnoreCase)
            && publish.Contains("-p:PublishSingleFile=false", StringComparison.OrdinalIgnoreCase));
        Check("the release requests no in-exe compression",
            publish.Contains("-p:EnableCompressionInSingleFile=false", StringComparison.OrdinalIgnoreCase)
            && !publish.Contains("-p:EnableCompressionInSingleFile=true", StringComparison.OrdinalIgnoreCase));
        Check($"publish overrides the native self-extraction default ({selfExtractDefault ?? "unset"})",
            publish.Contains("-p:IncludeNativeLibrariesForSelfExtract=false", StringComparison.OrdinalIgnoreCase)
            && !publish.Contains("-p:IncludeNativeLibrariesForSelfExtract=true", StringComparison.OrdinalIgnoreCase));

        var zipped = Regex.Match(zip, @"-Path\s+(.+?)\s+-DestinationPath", RegexOptions.IgnoreCase).Groups[1].Value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var files = PackagingEntries(setup, "Files")
            .Select(entry => (Source: PackagingValue(entry, "Source"), Flags: PackagingValue(entry, "Flags") ?? ""))
            .Where(entry => entry.Source is not null).ToArray();
        bool ZipContains(string file) => zipped.Any(item => PackagingCovers(item.Trim('\'', '"'), "publish/", file));
        bool InstallerContains(string file) => files.Any(entry =>
            !PackagingFlag(entry.Flags, "dontcopy") && PackagingCovers(entry.Source!, @"{#PublishDir}\", file)
            && (!file.Contains('\\') || PackagingFlag(entry.Flags, "recursesubdirs")));

        var desktopRuntime = Path.GetDirectoryName(typeof(System.Windows.Window).Assembly.Location)!;
        var natives = Directory.GetFiles(desktopRuntime, "*_cor3.dll")
            .Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var missingZipNative = natives.Where(file => !ZipContains(file)).ToArray();
        var missingSetupNative = natives.Where(file => !InstallerContains(file)).ToArray();
        Check($"the ZIP covers all WPF native libraries ({natives.Length}; missing: {string.Join(", ", missingZipNative)})",
            natives.Length >= 4 && missingZipNative.Length == 0);
        Check($"Setup actually copies all WPF native libraries (missing: {string.Join(", ", missingSetupNative)})",
            natives.Length >= 4 && missingSetupNative.Length == 0);

        // Runtime and locale files must be copied, not all deleted to suit
        // the old single-file layout. This process has the same .NET/WPF set.
        var languages = project.Descendants("SatelliteResourceLanguages").Select(element => element.Value).LastOrDefault() ?? "";
        var runtimeFiles = new[] { Path.GetDirectoryName(typeof(object).Assembly.Location)!, desktopRuntime }
            .Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(directory => Directory.GetFiles(directory))
            .Select(Path.GetFileName).OfType<string>()
            .Where(name => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || name.Equals("createdump.exe", StringComparison.OrdinalIgnoreCase))
            .Concat(languages.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(language => !language.Equals("en", StringComparison.OrdinalIgnoreCase))
                .Select(language => Path.Combine(language, "PresentationCore.resources.dll")))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var missingZip = runtimeFiles.Where(file => !ZipContains(file)).ToArray();
        var missingSetup = runtimeFiles.Where(file => !InstallerContains(file)).ToArray();
        Check($"the ZIP covers the complete runtime/dependencies and configured satellites ({runtimeFiles.Length}; missing: {string.Join(", ", missingZip.Take(5))})",
            runtimeFiles.Length > 100 && missingZip.Length == 0);
        Check($"Setup covers the same complete layout ({runtimeFiles.Length}; missing: {string.Join(", ", missingSetup.Take(5))})",
            runtimeFiles.Length > 100 && missingSetup.Length == 0);
        Check("Setup preserves locale subdirectories",
            files.Any(entry => PackagingCovers(entry.Source!, @"{#PublishDir}\", @"ru\PresentationCore.resources.dll")
                && PackagingFlag(entry.Flags, "recursesubdirs") && PackagingFlag(entry.Flags, "createallsubdirs")));

        const string manifest = "UltraExplorer-package-files.txt";
        Check("the ownership manifest contains relative publish paths and itself",
            workflowText.Contains("[IO.Path]::GetRelativePath($publishRoot, $_.FullName)", StringComparison.Ordinal)
            && workflowText.Contains("$relativeFiles += '" + manifest + "'", StringComparison.Ordinal)
            && workflowText.Contains("utf8BOM", StringComparison.OrdinalIgnoreCase));
        Check("the ZIP is reopened and checked against every relative publish file",
            workflowText.Contains("[IO.Compression.ZipFile]::OpenRead", StringComparison.Ordinal)
            && workflowText.Contains("$relativeFiles | Where-Object { $_ -notin $entries }", StringComparison.Ordinal)
            && workflowText.Contains("if ($missing.Count) { throw", StringComparison.Ordinal));
        Check("Setup supplies the new manifest for preparation and copies it with the payload",
            files.Any(entry => entry.Source!.EndsWith(@"{#PackageManifest}", StringComparison.Ordinal) && PackagingFlag(entry.Flags, "dontcopy"))
            && InstallerContains(manifest) && setupText.Contains("#define PackageManifest \"" + manifest + "\"", StringComparison.Ordinal));

        var unsafeDeletes = PackagingEntries(setup, "InstallDelete").Concat(PackagingEntries(setup, "UninstallDelete"))
            .Where(entry => !PackagingExplicitOwnedFileDelete(entry)).ToArray();
        Check($"delete sections contain no directory, wildcard or profile cleanup ({unsafeDeletes.Length} unsafe entries)", unsafeDeletes.Length == 0);
        Check("installer code contains no recursive directory deletion", !Regex.IsMatch(setupText, @"\bDelTree\s*\(", RegexOptions.IgnoreCase));
        var deleteCalls = Regex.Matches(setupText, @"\bDeleteFile\s*\(([^\r\n]*)", RegexOptions.IgnoreCase);
        Check("the sole file deletion is guarded by current membership and validated old ownership under app",
            deleteCalls.Count == 1
            && deleteCalls[0].Value.Contains("AddBackslash(ExpandConstant('{app}')) + RelativeName", StringComparison.Ordinal)
            && setupText.Contains("if not PackageContains(RelativeName) and SafeObsoletePackageFile(RelativeName) then", StringComparison.Ordinal)
            && setupText.Contains("for I := 0 to GetArrayLength(PreviousPackageFiles) - 1 do", StringComparison.Ordinal));
        var approvedLegacyFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LICENSE.txt", "UltraExplorer.pdb", "Vanara.Windows.Shell.dll", "Vanara.PInvoke.Shared.dll",
            "Vanara.PInvoke.Shell32.dll", "Vanara.PInvoke.User32.dll"
        };
        var cleanupArguments = Regex.Matches(setupText, @"\bRemoveObsoletePackageFile\s*\(([^)]*)\)")
            .Select(match => match.Groups[1].Value.Trim())
            .Where(argument => !argument.StartsWith("const ", StringComparison.OrdinalIgnoreCase)).ToArray();
        Check("obsolete cleanup callers use the old manifest or only approved legacy package filenames",
            cleanupArguments.Length > 0 && cleanupArguments.All(argument =>
                argument == "PreviousPackageFiles[I]"
                || argument.Length >= 2 && argument.StartsWith('\'') && argument.EndsWith('\'')
                    && approvedLegacyFiles.Contains(argument[1..^1])));
        Check("obsolete cleanup rejects absolute/wildcard/traversal names and reparse points",
            setupText.Contains("Pos(':', RelativeName)", StringComparison.Ordinal) && setupText.Contains("Pos('/', RelativeName)", StringComparison.Ordinal)
            && setupText.Contains("Pos('*', RelativeName)", StringComparison.Ordinal) && setupText.Contains("Pos('?', RelativeName)", StringComparison.Ordinal)
            && setupText.Contains("(RelativeName[1] = '\\')", StringComparison.Ordinal)
            && setupText.Contains("(Segment = '.') or (Segment = '..')", StringComparison.Ordinal)
            && setupText.Contains("PackageFileAttributes(Prefix)", StringComparison.Ordinal)
            && setupText.Contains("((Attributes and $400) <> 0)", StringComparison.Ordinal));
        Check("ownership is loaded before update; cleanup happens only after successful installation",
            setupText.Contains("LoadStringsFromFile(ExpandConstant('{app}\\{#PackageManifest}'), PreviousPackageFiles)", StringComparison.Ordinal)
            && setupText.Contains("LoadStringsFromFile(ExpandConstant('{tmp}\\{#PackageManifest}'), CurrentPackageFiles)", StringComparison.Ordinal)
            && Regex.IsMatch(setupText, @"if\s+CurStep\s*=\s*ssPostInstall\s+then", RegexOptions.IgnoreCase));
        Check("release packages carry SHA-256 checksums",
            workflowText.Contains("Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256", StringComparison.Ordinal)
            && workflowText.Contains("packages/SHA256SUMS.txt", StringComparison.Ordinal) && workflowText.Contains("path: packages/*", StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    private static bool PackagingFlag(string flags, string flag) =>
        flags.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(flag, StringComparer.OrdinalIgnoreCase);

    /// <summary>Explicit legacy file cleanup is allowed; broad profile/directory/glob cleanup is not.</summary>
    private static bool PackagingExplicitOwnedFileDelete(string entry)
    {
        var kind = PackagingValue(entry, "Type");
        var name = PackagingValue(entry, "Name");
        const string app = @"{app}\";
        if (!string.Equals(kind, "files", StringComparison.OrdinalIgnoreCase) || name is null || !name.StartsWith(app, StringComparison.OrdinalIgnoreCase)) return false;
        var relative = name[app.Length..];
        return relative.Length > 0 && !relative.Contains(':') && !relative.Contains('/') && !relative.Contains('*') && !relative.Contains('?')
            && relative.Split('\\').All(segment => segment.Length > 0 && segment is not "." and not "..");
    }

    private static string PackagingRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source) ?? "", "..", ".."));

    /// <summary>One PowerShell command, with backtick continuation lines joined.</summary>
    private static string PackagingCommand(string[] workflow, string starts)
    {
        var at = Array.FindIndex(workflow, line => line.TrimStart().StartsWith(starts, StringComparison.OrdinalIgnoreCase));
        if (at < 0) return "";
        var command = new List<string>();
        for (var index = at; index < workflow.Length; index++)
        {
            var line = workflow[index].Trim();
            command.Add(line.TrimEnd((char)0x60).Trim());
            if (!line.EndsWith((char)0x60)) break;
        }
        return string.Join(' ', command);
    }

    private static IEnumerable<string> PackagingEntries(string[] script, string section)
    {
        var inside = false;
        foreach (var raw in script)
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) inside = line[1..^1].Equals(section, StringComparison.OrdinalIgnoreCase);
            else if (inside && line.Length > 0 && !line.StartsWith(';')) yield return line;
        }
    }

    private static string? PackagingValue(string entry, string parameter)
    {
        var match = Regex.Match(entry, @"(?:^|;)\s*" + parameter + @":\s*(""[^""]*""|[^;]*)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim().Trim('"') : null;
    }

    private static bool PackagingCovers(string item, string folder, string file)
    {
        item = item.Replace('\\', '/');
        folder = folder.Replace('\\', '/');
        file = file.Replace('\\', '/');
        return item.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
            && FileSystemName.MatchesSimpleExpression(item.AsSpan(folder.Length), file, ignoreCase: true);
    }
}
