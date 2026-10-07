using System.Text;

namespace UltraExplorer.Services.Archives;

/// <summary>
/// The origin stream belongs to the archive file, not to its compressed
/// entries. The 7-Zip decoder writes only the entry bytes; its caller must
/// retain this Windows metadata on every physical extracted file.
/// </summary>
internal sealed class ArchiveZoneHelper
{
    internal const string StreamSuffix = ":Zone.Identifier";
    // 7-Zip's Windows extraction UI also bounds this small metadata stream.
    internal const int MaximumBytes = 32 * 1024;
    private readonly byte[] _bytes;

    private ArchiveZoneHelper(byte[] bytes) => _bytes = bytes;

    /// <summary>Only an absent stream means unmarked. IO/permission failures are not silently downgraded.</summary>
    internal static ArchiveZoneHelper? Capture(string archive)
    {
        try
        {
            using var stream = new FileStream(archive + StreamSuffix, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0) return null;
            if (stream.Length > MaximumBytes)
                throw new InvalidDataException("The archive's origin metadata is too large to preserve safely.");
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return new ArchiveZoneHelper(bytes);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 50 or 87 or 123)
        {
            // Filesystems without named streams cannot carry a source mark.
            // Apply never swallows these errors for a known marked source.
            return null;
        }
    }

    /// <summary>Copy the stream byte for byte; a more restrictive existing mark stays intact.</summary>
    internal void Apply(string output)
    {
        ArchiveService.EnsureSafeOutput(output);
        if ((File.GetAttributes(output) & FileAttributes.Directory) != 0)
            throw new IOException("Origin metadata must be applied to an extracted file.");
        var existing = Capture(output);
        if (existing is not null && Zone(existing._bytes) is { } prior
            && Zone(_bytes) is { } source && prior > source) return;
        var written = File.GetLastWriteTimeUtc(output);
        using (var stream = new FileStream(output + StreamSuffix, FileMode.Create, FileAccess.Write, FileShare.Read))
            stream.Write(_bytes);
        // Origin metadata must not invalidate archive cache identities.
        // Only timestamp restoration is best-effort; mark writes stay fatal.
        try { File.SetLastWriteTimeUtc(output, written); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    internal static void Propagate(string archive, string output) => Capture(archive)?.Apply(output);

    private static int? Zone(byte[] bytes)
    {
        // Normal streams are ASCII/UTF-8. Preserve UTF-16 streams as well;
        // decoding here is used only to avoid weakening an existing mark.
        var text = bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) ? Encoding.Unicode.GetString(bytes)
            : bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }) ? Encoding.BigEndianUnicode.GetString(bytes)
            : Encoding.UTF8.GetString(bytes);
        foreach (var line in text.Split('\n'))
        {
            var value = line.Trim();
            if (value.StartsWith("ZoneId=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value.AsSpan(7), out var zone) && zone is >= 0 and <= 4) return zone;
        }
        return null;
    }
}
