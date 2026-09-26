using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// One icon as the atlas keeps it: 64 x 64 premultiplied BGRA and the six
/// smaller levels under it, down to 1 x 1, packed one after another in a
/// single array.  That is exactly what one slice of the atlas texture holds,
/// in the order Direct3D numbers its subresources, so a slot goes to the GPU
/// as seven plain copies and comes back from the disk cache as one read.
///
/// Immutable once made.  A slot whose icon changes is given a new one rather
/// than having its pixels overwritten, so a copy handed to the disk cache
/// writer, or to a texture being built on another thread for the other
/// graphics card, never changes under it.
///
/// The array lives on the pinned object heap: its address can be handed
/// straight to Direct3D - as initial data for a whole texture, or to
/// UpdateSubresource - without a pinning handle or a copy.  About 21 KB, so a
/// thousand icons are 22 MB, which is the budget the design gives the atlas.
/// </summary>
internal sealed class IconSlotData
{
    /// <summary>The side of the largest level, in pixels.</summary>
    public const int Size = 64;

    /// <summary>64, 32, 16, 8, 4, 2 and 1 pixels.</summary>
    public const int MipLevels = 7;

    /// <summary>Four bytes a pixel over all seven levels: 4 x (4096 + 1024 + 256 + 64 + 16 + 4 + 1).</summary>
    public const int ByteCount = 4 * 5461;

    private static readonly int[] Offsets = [0, 16384, 20480, 21504, 21760, 21824, 21840];

    private IconSlotData(byte[] pixels, ulong hash)
    {
        Pixels = pixels;
        Hash = hash;
    }

    /// <summary>
    /// All seven levels, largest first, rows packed without padding.  Never
    /// written after the slot is made.
    /// </summary>
    public byte[] Pixels { get; }

    /// <summary>
    /// A 64-bit hash of <see cref="Pixels"/>: how two keys that show the same
    /// icon are found to be one slot, and how a revalidated icon is found to
    /// have changed.  Stored in the disk cache, so it must stay the same from
    /// one run to the next - which is why it is not <see cref="HashCode"/>,
    /// whose seed changes every run.
    /// </summary>
    public ulong Hash { get; }

    /// <summary>The side of level <paramref name="mip"/>: 64 for level 0, 1 for level 6.</summary>
    public static int MipSize(int mip) => Size >> mip;

    /// <summary>Where level <paramref name="mip"/> starts in <see cref="Pixels"/>.</summary>
    public static int MipOffset(int mip) => Offsets[mip];

    /// <summary>How many bytes level <paramref name="mip"/> takes.</summary>
    public static int MipByteCount(int mip)
    {
        var size = MipSize(mip);
        return size * size * 4;
    }

    /// <summary>A fresh array for a slot's pixels, on the pinned heap and not cleared.</summary>
    public static byte[] NewPixelBuffer() => GC.AllocateUninitializedArray<byte>(ByteCount, pinned: true);

    /// <summary>
    /// Takes ownership of <paramref name="pixels"/>, which must come from
    /// <see cref="NewPixelBuffer"/> and hold a finished chain, and works out
    /// its hash.  The caller must not write to the array afterwards.
    /// </summary>
    public static IconSlotData Adopt(byte[] pixels)
    {
        if (pixels.Length != ByteCount)
        {
            throw new ArgumentException($"A slot is {ByteCount} bytes, not {pixels.Length}.", nameof(pixels));
        }

        return new IconSlotData(pixels, HashOf(pixels));
    }

    /// <summary>
    /// As <see cref="Adopt(byte[])"/>, with the hash already known - the disk
    /// cache stores it beside the pixels and checks it as it reads them.
    /// </summary>
    public static IconSlotData Adopt(byte[] pixels, ulong hash)
    {
        if (pixels.Length != ByteCount)
        {
            throw new ArgumentException($"A slot is {ByteCount} bytes, not {pixels.Length}.", nameof(pixels));
        }

        return new IconSlotData(pixels, hash);
    }

    /// <summary>Level <paramref name="mip"/>'s pixels.</summary>
    public ReadOnlySpan<byte> Mip(int mip) => Pixels.AsSpan(Offsets[mip], MipByteCount(mip));

    /// <summary>
    /// The address of level <paramref name="mip"/>'s first byte.  Stable for
    /// the life of the slot because the array is on the pinned heap.
    /// </summary>
    public unsafe nint AddressOf(int mip)
        => (nint)Unsafe.AsPointer(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Pixels), Offsets[mip]));

    /// <summary>
    /// Whether the two hold the same picture.  The hash decides almost every
    /// case; equal hashes are confirmed byte by byte, so a collision can never
    /// put one type's icon on another.
    /// </summary>
    public bool SamePixels(IconSlotData other)
        => ReferenceEquals(this, other)
            || other.Hash == Hash && Pixels.AsSpan().SequenceEqual(other.Pixels);

    /// <summary>
    /// A fast 64-bit hash: eight bytes at a time through a multiply and a
    /// rotation, so every bit of the input reaches every bit of the state, and
    /// MurmurHash3's finaliser at the end.  Not for security - only to tell
    /// icons apart, with <see cref="SamePixels"/> checking the bytes behind it.
    /// </summary>
    public static ulong HashOf(ReadOnlySpan<byte> pixels)
    {
        const ulong Multiplier = 0x9E3779B97F4A7C15UL;
        var hash = 0x243F6A8885A308D3UL ^ (ulong)pixels.Length;
        var words = MemoryMarshal.Cast<byte, ulong>(pixels);
        foreach (var word in words)
        {
            hash = BitOperations.RotateLeft((hash ^ word) * Multiplier, 29);
        }

        for (var index = words.Length * sizeof(ulong); index < pixels.Length; index++)
        {
            hash = BitOperations.RotateLeft((hash ^ pixels[index]) * Multiplier, 29);
        }

        hash ^= hash >> 33;
        hash *= 0xFF51AFD7ED558CCDUL;
        hash ^= hash >> 33;
        hash *= 0xC4CEB9FE1A85EC53UL;
        hash ^= hash >> 33;
        return hash;
    }
}
