using System.IO;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ViewAllSmoke;

/// <summary>
/// What the release packages are made of, read from the files that make
/// them - the workflow's publish and zip, the project and the installer
/// script - since a publish is too slow for a check: the exe is not
/// compressed, so no process inflates a private copy of it into memory; the
/// native WPF libraries sit beside it in the zip and in Setup rather than
/// being unpacked into %TEMP% at every start; and Setup over the folder
/// build scripts/install.ps1 makes clears all of that build away.
/// </summary>
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
            Check($"the workflow, the project and the installer script are found beside these checks' sources ({root})", false);
            return Task.CompletedTask;
        }

        var workflow = File.ReadAllLines(workflowPath);
        var publish = PackagingCommand(workflow, "dotnet publish");
        var zip = PackagingCommand(workflow, "Compress-Archive");
        var setup = File.ReadAllLines(setupPath);
        var selfExtract = XDocument.Load(projectPath).Descendants("IncludeNativeLibrariesForSelfExtract").Select(element => element.Value.Trim()).LastOrDefault();

        Check("the release is published as one self-contained exe", publish.Contains("-p:PublishSingleFile=true", StringComparison.OrdinalIgnoreCase)
            && publish.Contains("--self-contained true", StringComparison.OrdinalIgnoreCase));
        Check("uncompressed: no process, a forwarded folder open included, inflates a private copy of it into memory",
            !publish.Contains("EnableCompressionInSingleFile=true", StringComparison.OrdinalIgnoreCase));
        Check($"and nothing in it is unpacked into %TEMP% to start: the native libraries stay beside it (the project says {selfExtract ?? "nothing"})",
            !string.Equals(selfExtract, "true", StringComparison.OrdinalIgnoreCase)
            && !publish.Contains("IncludeNativeLibrariesForSelfExtract=true", StringComparison.OrdinalIgnoreCase));

        // The native libraries a self-contained WPF app carries, as this
        // process's own WPF has them: the ones a single-file publish leaves
        // beside the exe once they are not packed into it.
        var natives = Directory.GetFiles(Path.GetDirectoryName(typeof(System.Windows.Window).Assembly.Location)!, "*_cor3.dll")
            .Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var zipped = Regex.Match(zip, @"-Path\s+(.+?)\s+-DestinationPath", RegexOptions.IgnoreCase).Groups[1].Value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var installed = PackagingEntries(setup, "Files").Select(entry => PackagingValue(entry, "Source")).OfType<string>().ToArray();
        var notZipped = natives.Where(native => !zipped.Any(item => PackagingCovers(item, "publish/", native))).ToArray();
        var notInstalled = natives.Where(native => !installed.Any(item => PackagingCovers(item, @"{#PublishDir}\", native))).ToArray();
        Check($"the portable zip carries each native library beside the exe ({natives.Length} libraries, missing: {string.Join(", ", notZipped)})",
            natives.Length >= 4 && notZipped.Length == 0);
        Check($"and Setup installs each of them beside it (missing: {string.Join(", ", notInstalled)})",
            natives.Length >= 4 && notInstalled.Length == 0);

        // A folder build of scripts/install.ps1 is the .NET runtime, WPF and
        // the app's libraries side by side, their .json files, the dump
        // helper and a folder of resources for each language the project
        // keeps; this process's own runtime has the same files.
        var languages = XDocument.Load(projectPath).Descendants("SatelliteResourceLanguages").Select(element => element.Value).LastOrDefault() ?? string.Empty;
        var folderBuild = new[] { Path.GetDirectoryName(typeof(object).Assembly.Location)!, Path.GetDirectoryName(typeof(System.Windows.Window).Assembly.Location)! }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(directory => Directory.GetFiles(directory))
            .Select(Path.GetFileName).OfType<string>()
            .Where(name => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || name.Equals("createdump.exe", StringComparison.OrdinalIgnoreCase))
            .Concat(languages.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(language => !language.Equals("en", StringComparison.OrdinalIgnoreCase))
                .Select(language => Path.Combine(language, "PresentationCore.resources.dll")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var deletes = PackagingEntries(setup, "InstallDelete")
            .Select(entry => (Type: PackagingValue(entry, "Type"), Name: PackagingValue(entry, "Name")))
            .Where(entry => entry.Name?.StartsWith(@"{app}\", StringComparison.OrdinalIgnoreCase) == true)
            .Select(entry => (entry.Type, Pattern: entry.Name![@"{app}\".Length..]))
            .ToArray();
        // A pattern takes in files of its own folder only; a language folder
        // goes whole with a filesandordirs entry for it.
        var left = folderBuild.Where(file => !deletes.Any(entry =>
            (file.Contains('\\') == entry.Pattern.Contains('\\') && FileSystemName.MatchesSimpleExpression(entry.Pattern, file))
            || (string.Equals(entry.Type, "filesandordirs", StringComparison.OrdinalIgnoreCase) && file.Contains('\\')
                && FileSystemName.MatchesSimpleExpression(entry.Pattern, file.AsSpan(0, file.IndexOf('\\')))))).ToArray();
        Check($"Setup over a folder build of scripts/install.ps1 leaves none of its runtime behind ({left.Length} of {folderBuild.Length} left{(left.Length > 0 ? ", " + string.Join(", ", left.Take(3)) + "..." : string.Empty)})",
            folderBuild.Length > 100 && left.Length == 0);
        return Task.CompletedTask;
    }

    /// <summary>The repository these checks were built from: two folders up from this source file.</summary>
    private static string PackagingRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, "..", ".."));

    /// <summary>One PowerShell command of the workflow, its lines continued with a backtick joined into one.</summary>
    private static string PackagingCommand(string[] workflow, string starts)
    {
        var at = Array.FindIndex(workflow, line => line.TrimStart().StartsWith(starts, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
        {
            return string.Empty;
        }

        var command = new List<string>();
        for (var index = at; index < workflow.Length; index++)
        {
            var line = workflow[index].Trim();
            command.Add(line.TrimEnd('`').Trim());
            if (!line.EndsWith('`'))
            {
                break;
            }
        }

        return string.Join(' ', command);
    }

    /// <summary>The entries of one [Section] of an Inno Setup script, comments and blank lines left out.</summary>
    private static IEnumerable<string> PackagingEntries(string[] script, string section)
    {
        var inside = false;
        foreach (var raw in script)
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inside = line[1..^1].Equals(section, StringComparison.OrdinalIgnoreCase);
            }
            else if (inside && line.Length > 0 && !line.StartsWith(';'))
            {
                yield return line;
            }
        }
    }

    /// <summary>One parameter of an Inno Setup entry - <c>Name: "{app}\x"</c> - without its quotes; null when the entry has none.</summary>
    private static string? PackagingValue(string entry, string parameter)
    {
        var match = Regex.Match(entry, @"(?:^|;)\s*" + parameter + @":\s*(""[^""]*""|[^;]*)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim().Trim('"') : null;
    }

    /// <summary>Whether <paramref name="item"/>, a path in <paramref name="folder"/> that may hold wildcards, takes in <paramref name="file"/>.</summary>
    private static bool PackagingCovers(string item, string folder, string file) =>
        item.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
        && FileSystemName.MatchesSimpleExpression(item.AsSpan(folder.Length), file);
}
