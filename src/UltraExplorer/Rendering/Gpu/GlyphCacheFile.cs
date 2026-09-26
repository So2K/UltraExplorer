using System.Diagnostics;
using System.Text;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// The glyph atlas between runs: glyphs-v1-&lt;font hash&gt;.bin in the
/// state folder's cache.  With it, start-up reads a few megabytes instead of
/// rasterising a few thousand glyphs, and the first frame has every Latin
/// and Cyrillic glyph at every tier.
///
/// Layout, little-endian:
/// - magic "UXGA", format version, font hash (length-prefixed UTF-8), page
///   size, tier count and each tier's em and spread - anything that differs
///   from this build's atlas and the file is ignored;
/// - page count, then per page its used height and shelves (top, height,
///   filled width), so placing continues after the loaded glyphs;
/// - entry count, then per entry face, tier, glyph index, page, texel
///   rectangle and the em box;
/// - per page, its used rows of texels.
/// Only the three fixed faces' entries are kept: fallback faces get their
/// ids in the order a run meets them, so their ids mean nothing next time.
///
/// Written to a temporary file and moved over the old one, so a crash
/// mid-write never leaves half a file to be read.  Any read error, short
/// file or mismatch means "no cache": the atlas is rasterised as if the
/// file were not there.
/// </summary>
internal static class GlyphCacheFile
{
    private const uint Magic = 0x41475855; // "UXGA"

    /// <summary>Loads <paramref name="path"/> into a fresh atlas; false when there is no usable file.</summary>
    public static unsafe bool TryLoad(GlyphAtlas atlas, string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadUInt32() != Magic
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
            for (var page = 0; page < pageCount; page++)
            {
                usedHeights[page] = reader.ReadInt32();
                var shelfCount = reader.ReadInt32();
                if (usedHeights[page] < 0 || usedHeights[page] > GlyphAtlas.PageSize || shelfCount < 0 || shelfCount > GlyphAtlas.PageSize)
                {
                    return false;
                }

                shelves[page] = new List<ShelfPacker.Shelf>(shelfCount);
                for (var shelf = 0; shelf < shelfCount; shelf++)
                {
                    shelves[page].Add(new ShelfPacker.Shelf(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()));
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
            }

            return atlas.Restore(pageCount, shelves, entries, (page, pointer) =>
            {
                var bytes = usedHeights[page] * GlyphAtlas.PageSize;
                var span = new Span<byte>((void*)pointer, bytes);
                return stream.ReadAtLeast(span, bytes, throwOnEndOfStream: false) == bytes;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            Debug.WriteLine($"The glyph cache {path} could not be read: {ex.Message}");
            return false;
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
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
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
                    stream.Write(new ReadOnlySpan<byte>((void*)atlas.PagePointer(page), bytes));
                }
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
