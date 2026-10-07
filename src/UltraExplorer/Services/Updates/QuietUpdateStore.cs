using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltraExplorer.Services.Updates;

internal sealed class QuietUpdateStore
{
    internal sealed record Settings(bool Enabled = true);
    internal sealed record Attempt(DateTimeOffset LastAttemptUtc);
    internal sealed record Package(string Version, string Sha256, long Size);
    private readonly string _directory;
    public QuietUpdateStore(string directory) => _directory = Path.GetFullPath(directory);
    public string DirectoryPath => _directory;
    public string PackagePath(string sha, bool partial = false) => Path.Combine(_directory, $"package-{sha}.zip{(partial ? ".partial" : "")}");

    public bool Enabled
    {
        get
        {
            // Missing means the documented default; damaged/unreadable existing
            // preference must not silently turn somebody's opt-out back on.
            try
            {
                var path = Path.Combine(_directory, "settings.json");
                AssertNoLinks(path);
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length is <= 0 or > 65_536) return false;
                using var json = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 16 });
                if (json.RootElement.ValueKind != JsonValueKind.Object) return false;
                var fields = json.RootElement.EnumerateObject().Where(property => property.Name == nameof(Settings.Enabled)).ToArray();
                return fields.Length == 1 && fields[0].Value.ValueKind == JsonValueKind.True;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
        }
    }
    public DateTimeOffset LastAttemptUtc => Read<Attempt>("attempt.json")?.LastAttemptUtc ?? DateTimeOffset.MinValue;
    public Package? Ready => Read<Package>("ready.json");
    public void SetEnabled(bool enabled) => Write("settings.json", new Settings(enabled));
    public void RecordAttempt(DateTimeOffset utc) => Write("attempt.json", new Attempt(utc));
    public void SetReady(Package ready) => Write("ready.json", ready);
    public void ClearReady()
    {
        try { var path = Path.Combine(_directory, "ready.json"); AssertNoLinks(path); File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void PrunePackages(string keepHash) => PrunePackages(keepHash, partialsOnly: false);
    public void PrunePartialPackages(string keepHash) => PrunePackages(keepHash, partialsOnly: true);
    public void PruneOrphanedStages()
    {
        try
        {
            AssertNoLinks(_directory);
            var owned = new Regex(@"\A\.download-[0-9a-f]{32}\.partial\z", RegexOptions.CultureInvariant);
            foreach (var path in Directory.EnumerateFiles(_directory, ".download-*", SearchOption.TopDirectoryOnly))
            {
                if (!owned.IsMatch(Path.GetFileName(path))) continue;
                try { AssertNoLinks(path); File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private void PrunePackages(string keepHash, bool partialsOnly)
    {
        try
        {
            // Never follow a junction/symlink while deleting cache contents.
            AssertNoLinks(_directory);
            var ownedName = new Regex(@"\Apackage-[0-9a-f]{64}\.zip(?:\.partial)?\z", RegexOptions.CultureInvariant);
            var keep = Path.GetFileName(PackagePath(keepHash, partialsOnly));
            foreach (var path in Directory.EnumerateFiles(_directory, "package-*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (name == keep || !ownedName.IsMatch(name)) continue;
                if (partialsOnly && !name.EndsWith(".partial", StringComparison.Ordinal)) continue;
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // FileShare.None is held for the complete network/download transaction. It
    // survives multiple windows, prevents duplicate processes, and needs no daemon.
    public FileStream? TryLease()
    {
        try
        {
            AssertNoLinks(_directory);
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, "network.lock");
            AssertNoLinks(path);
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private T? Read<T>(string name)
    {
        try
        {
            var path = Path.Combine(_directory, name);
            AssertNoLinks(path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 65_536) return default;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return default; }
    }

    private void Write<T>(string name, T value)
    {
        AssertNoLinks(_directory);
        Directory.CreateDirectory(_directory);
        AssertNoLinks(Path.Combine(_directory, name));
        var temporary = Path.Combine(_directory, $".{name}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path.Combine(_directory, name), overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    internal static void AssertNoLinks(string path)
    {
        for (var cursor = Path.GetFullPath(path); !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The update cache contains a filesystem link.");
    }
}
