using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// The glyph atlas between runs: glyphs-v1-&lt;font hash&gt;.bin in the
/// state folder's cache.  With it, start-up reads a few megabytes instead of
/// rasterising a few thousand glyphs, and the first frame has every Latin
/// and Cyrillic glyph at every tier.
///
/// Layout, little-endian:
/// - magic "UXGA", the file's layout version, format version, font hash
///   (length-prefixed UTF-8), page size, tier count and each tier's em and
///   spread - anything that differs from this build's atlas and the file is
///   ignored;
/// - page count, then per page its used height and shelves (top, height,
///   filled width), so placing continues after the loaded glyphs;
/// - entry count, then per entry face, tier, glyph index, page, texel
///   rectangle and the em box;
/// - per page, its used rows of texels;
/// - a SHA-256 of everything before it.
/// Only the three fixed faces' entries are kept: fallback faces get their
/// ids in the order a run meets them, so their ids mean nothing next time -
/// and nor is the room their glyphs took (<see cref="GlyphAtlas.Snapshot"/>).
///
/// Written to a temporary file, flushed to the disk and moved over the old
/// one, so a crash mid-write never leaves half a file to be read.  Read
/// whole and checked - the hash, then every number against the page it
/// describes - before anything goes into the atlas: a shelf off its page
/// would have the next glyph written outside the page's memory.  Any read
/// error, short file, mismatch or bad hash means "no cache": the atlas is
/// untouched and rasterised as if the file were not there.
/// </summary>
internal static class GlyphCacheFile
{
    private const uint Magic = 0x41475855; // "UXGA"

    /// <summary>
    /// The file's own layout, apart from what the texels mean
    /// (<see cref="GlyphAtlas.FormatVersion"/>).  2 added the hash; a file
    /// without one reads as another version and is made again.
    /// </summary>
    private const int LayoutVersion = 2;

    private const int HashBytes = 32;

    /// <summary>
    /// More than any file this build writes - sixteen full pages, a million
    /// entries - so a bigger one is not even read into memory.
    /// </summary>
    private const long MaximumFileBytes = 128L << 20;

    /// <summary>Loads <paramref name="path"/> into a fresh atlas; false when there is no usable file.</summary>
    public static unsafe bool TryLoad(GlyphAtlas atlas, string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            byte[] file;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                if (stream.Length <= HashBytes || stream.Length > MaximumFileBytes)
                {
                    return false;
                }

                file = new byte[stream.Length];
                stream.ReadExactly(file);
            }

            var payloadLength = file.Length - HashBytes;
            if (!SHA256.HashData(file.AsSpan(0, payloadLength)).AsSpan().SequenceEqual(file.AsSpan(payloadLength)))
            {
                return false;
            }

            using var reader = new BinaryReader(new MemoryStream(file, 0, payloadLength, writable: false), Encoding.UTF8);
            if (reader.ReadUInt32() != Magic
                || reader.ReadInt32() != LayoutVersion
                || reader.ReadInt32() != GlyphAtlas.FormatVersion
                || reader.ReadString() != atlas.Faces.FontHash
                || reader.ReadInt32() != GlyphAtlas.PageSize
                || reader.ReadInt32() != GlyphAtlas.TierCount)
            {
                return false;
            }

            foreach (var tier in GlyphAtlas.Tiers)
            {
                if (reader.ReadInt32() != tier.Em || reader.ReadInt32() != tier.Spread)
                {
                    return false;
                }
            }

            var pageCount = reader.ReadInt32();
            if (pageCount < 1 || pageCount > GlyphAtlas.MaximumPages)
            {
                return false;
            }

            var usedHeights = new int[pageCount];
            var shelves = new List<ShelfPacker.Shelf>[pageCount];
            long shelved = 0;
            long named = 0;
            for (var page = 0; page < pageCount; page++)
            {
                usedHeights[page] = reader.ReadInt32();
                var shelfCount = reader.ReadInt32();
                if (usedHeights[page] < 0 || usedHeights[page] > GlyphAtlas.PageSize || shelfCount < 0 || shelfCount > GlyphAtlas.PageSize)
                {
                    return false;
                }

                // The shelves as the packer makes them: one under the other
                // from the top row down to the used height, none filled past
                // the page's width.  The packer places the next glyphs on
                // them, so one that is not would put texels off the page.
                shelves[page] = new List<ShelfPacker.Shelf>(shelfCount);
                var bottom = 0;
                for (var index = 0; index < shelfCount; index++)
                {
                    var shelf = new ShelfPacker.Shelf(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                    if (shelf.Top != bottom
                        || shelf.Height <= 0
                        || shelf.Height > usedHeights[page] - bottom
                        || shelf.Used < 0
                        || shelf.Used > GlyphAtlas.PageSize)
                    {
                        return false;
                    }

                    bottom += shelf.Height;
                    shelved += (long)shelf.Used * shelf.Height;
                    shelves[page].Add(shelf);
                }

                if (bottom != usedHeights[page])
                {
                    return false;
                }
            }

            var entryCount = reader.ReadInt32();
            if (entryCount < 0 || entryCount > 1 << 20)
            {
                return false;
            }

            var entries = new List<(GlyphKey, GlyphEntry)>(entryCount);
            for (var index = 0; index < entryCount; index++)
            {
                var key = new GlyphKey(reader.ReadByte(), reader.ReadUInt16(), reader.ReadByte());
                var entry = new GlyphEntry(
                    reader.ReadUInt16(),
                    reader.ReadUInt16(),
                    reader.ReadUInt16(),
                    reader.ReadUInt16(),
                    reader.ReadUInt16(),
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle());
                if (entry.Page >= pageCount || entry.X + entry.Width > GlyphAtlas.PageSize || entry.Y + entry.Height > usedHeights[entry.Page])
                {
                    return false;
                }

                entries.Add((key, entry));
                named += entry.Width == 0 ? 0 : (long)(entry.Width + 1) * (entry.Height + 1);
            }

            // A file whose shelves are mostly room no entry names is made
            // again rather than read: one the cache wrote before it left
            // fallback fonts' room out (see GlyphAtlas.Snapshot), grown with
            // every run that met CJK or emoji names, perhaps to all sixteen
            // pages - read, no new glyph would find room.  In a file of named
            // glyphs alone they fill nearly nine tenths of the shelves.
            if (named * 3 < shelved)
            {
                return false;
            }

            // The texels fill the rest of the file exactly; restoring then
            // only copies, so the atlas takes all of the file or none of it.
            var pageStarts = new int[pageCount];
            var texelsAt = (int)reader.BaseStream.Position;
            for (var page = 0; page < pageCount; page++)
            {
                pageStarts[page] = texelsAt;
                texelsAt += usedHeights[page] * GlyphAtlas.PageSize;
            }

            if (texelsAt != payloadLength)
            {
                return false;
            }

            return atlas.Restore(pageCount, shelves, entries, (page, pointer) =>
            {
                var bytes = usedHeights[page] * GlyphAtlas.PageSize;
                file.AsSpan(pageStarts[page], bytes).CopyTo(new Span<byte>((void*)pointer, bytes));
                return true;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or InvalidDataException or ArgumentException or NotSupportedException or FormatException)
        {
            Debug.WriteLine($"The glyph cache {path} could not be read: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Deletes the glyph cache files beside <paramref name="path"/> that are
    /// not it: each font or format update names a new file, 6 MB and up, and
    /// the old ones are never read again.  So are temporary files a write
    /// cut short left; only this file's own is spared, which another copy
    /// of the program may be writing.  Nothing else in the folder is touched,
    /// and a file that will not go is left for next time.
    /// </summary>
    public static void DeleteOthers(string path)
    {
        try
        {
            var current = Path.GetFullPath(path);
            var temporary = current + ".tmp";
            var folder = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "glyphs-v*"))
            {
                var name = Path.GetFileName(file);
                if (!name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".bin.tmp", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(file, current, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(file, temporary, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"The old glyph cache {file} could not be deleted: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            Debug.WriteLine($"The glyph cache folder of {path} could not be tidied: {ex.Message}");
        }
    }

    /// <summary>Writes the atlas's fixed-face glyphs to <paramref name="path"/>; false when the file could not be written.</summary>
    public static unsafe bool TrySave(GlyphAtlas atlas, string path)
    {
        var temporary = path + ".tmp";
        try
        {
            var snapshot = atlas.Snapshot();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(LayoutVersion);
                writer.Write(GlyphAtlas.FormatVersion);
                writer.Write(atlas.Faces.FontHash);
                writer.Write(GlyphAtlas.PageSize);
                writer.Write(GlyphAtlas.TierCount);
                foreach (var tier in GlyphAtlas.Tiers)
                {
                    writer.Write(tier.Em);
                    writer.Write(tier.Spread);
                }

                writer.Write(snapshot.PageCount);
                for (var page = 0; page < snapshot.PageCount; page++)
                {
                    writer.Write(snapshot.UsedHeights[page]);
                    writer.Write(snapshot.Shelves[page].Count);
                    foreach (var shelf in snapshot.Shelves[page])
                    {
                        writer.Write(shelf.Top);
                        writer.Write(shelf.Height);
                        writer.Write(shelf.Used);
                    }
                }

                writer.Write(snapshot.Entries.Count);
                foreach (var (key, entry) in snapshot.Entries)
                {
                    writer.Write(key.Face);
                    writer.Write(key.Glyph);
                    writer.Write(key.Tier);
                    writer.Write(entry.Page);
                    writer.Write(entry.X);
                    writer.Write(entry.Y);
                    writer.Write(entry.Width);
                    writer.Write(entry.Height);
                    writer.Write(entry.Left);
                    writer.Write(entry.Top);
                    writer.Write(entry.Right);
                    writer.Write(entry.Bottom);
                }

                writer.Flush();
                for (var page = 0; page < snapshot.PageCount; page++)
                {
                    var bytes = snapshot.UsedHeights[page] * GlyphAtlas.PageSize;
                    stream.Write(snapshot.Texels is { } texels
                        ? texels[page].AsSpan(0, bytes)
                        : new ReadOnlySpan<byte>((void*)atlas.PagePointer(page), bytes));
                }

                // The hash of what was written, read back from the cache the
                // system has just filled, closes the file; then all of it
                // goes to the disk before the move makes it the one read.
                stream.Position = 0;
                var hash = SHA256.HashData(stream);
                stream.Write(hash);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Debug.WriteLine($"The glyph cache {path} could not be written: {ex.Message}");
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            return false;
        }
    }
}
