using System.Globalization;
using System.Text;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// The icon atlas on disk, <c>cache\icons-v1.bin</c> in the state folder, so
/// the next start has every icon it had before in its first frame without a
/// single Shell call - the same idea as Explorer's own icon cache.
///
/// Layout, little-endian:
/// <list type="bullet">
/// <item>a header: magic "UXIC", version, slot size, level count, then how many slots and how many keys;</item>
/// <item>the keys: for each, its kind, the slot it shows and the key itself
/// (an extension, a stock name, or a file's path) - many keys usually share a slot;</item>
/// <item>the slots: for each, the hash of its pixels and the whole seven-level chain;</item>
/// <item>the magic again, so a file cut short is never taken for a whole one.</item>
/// </list>
/// Slots are stored once however many keys show them, which keeps the file
/// near 22 KB per distinct icon; a start with a few hundred icons reads a few
/// megabytes in one sequential pass.
///
/// Writes go to a temporary file beside the real one, are flushed to disk,
/// and replace it in one move, so a crash or a second copy of the app writing
/// at the same moment leaves either the old file or the new one, never a mix.
/// Reading never throws: a file that is missing, from another version, or
/// damaged (every slot's hash is checked) is simply no cache.
/// </summary>
internal static class IconAtlasCache
{
    private const uint Magic = 0x43495855;
    private const int FormatVersion = 1;
    private const int KeyLimit = 1_000_000;

    /// <summary>One key as stored: what it names, and the index of its slot in the file.</summary>
    internal readonly record struct CachedKey(string Key, IconKeyKind Kind, int Slot);

    /// <summary>A file's contents: its slots in file order, and the keys that refer to them.</summary>
    internal sealed record Contents(IconSlotData[] Slots, CachedKey[] Keys);

    /// <summary>The file at <paramref name="path"/>, or null when there is none worth using.</summary>
    public static Contents? Read(string path, int slotLimit)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt32() != Magic
                || reader.ReadInt32() != FormatVersion
                || reader.ReadUInt16() != IconSlotData.Size
                || reader.ReadUInt16() != IconSlotData.MipLevels)
            {
                return null;
            }

            var slotCount = reader.ReadInt32();
            var keyCount = reader.ReadInt32();
            if (slotCount < 0 || slotCount > slotLimit || keyCount < 0 || keyCount > KeyLimit
                || stream.Length < stream.Position + (long)slotCount * (sizeof(ulong) + IconSlotData.ByteCount))
            {
                return null;
            }

            var keys = new CachedKey[keyCount];
            for (var index = 0; index < keyCount; index++)
            {
                var kind = (IconKeyKind)reader.ReadByte();
                var slot = reader.ReadInt32();
                var key = reader.ReadString();
                if (kind > IconKeyKind.Stock || slot < 0 || slot >= slotCount)
                {
                    return null;
                }

                keys[index] = new CachedKey(key, kind, slot);
            }

            var slots = new IconSlotData[slotCount];
            for (var index = 0; index < slotCount; index++)
            {
                var hash = reader.ReadUInt64();
                var pixels = IconSlotData.NewPixelBuffer();
                stream.ReadExactly(pixels);
                if (IconSlotData.HashOf(pixels) != hash)
                {
                    return null;
                }

                slots[index] = IconSlotData.Adopt(pixels, hash);
            }

            return reader.ReadUInt32() == Magic ? new Contents(slots, keys) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or FormatException or System.Security.SecurityException)
        {
            // EndOfStreamException is an IOException: a file cut short.  A
            // FormatException is a damaged string length.
            return null;
        }
    }

    /// <summary>
    /// Writes <paramref name="slots"/> and <paramref name="keys"/> to
    /// <paramref name="path"/> atomically.  False when it could not; the file
    /// that was there, if any, is then left as it was.
    /// </summary>
    public static bool Write(string path, IReadOnlyList<IconSlotData> slots, IReadOnlyList<CachedKey> keys)
    {
        var temporary = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(Magic);
                    writer.Write(FormatVersion);
                    writer.Write((ushort)IconSlotData.Size);
                    writer.Write((ushort)IconSlotData.MipLevels);
                    writer.Write(slots.Count);
                    writer.Write(keys.Count);
                    foreach (var key in keys)
                    {
                        writer.Write((byte)key.Kind);
                        writer.Write(key.Slot);
                        writer.Write(key.Key);
                    }

                    foreach (var slot in slots)
                    {
                        writer.Write(slot.Hash);
                        writer.Write(slot.Pixels);
                    }

                    writer.Write(Magic);
                }

                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
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
