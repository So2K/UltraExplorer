using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// The icon atlas on one graphics card: a Texture2DArray of 64 x 64 BGRA
/// slices with seven mip levels each, one slice per slot of
/// <see cref="IconAtlas"/>, and the one view the icon shader samples it
/// through.  An array rather than a packed sheet because every icon is the
/// same square: no packer, no gutters, and filtering and mips never reach
/// into a neighbour's pixels.
///
/// One of these per device set (<see cref="GpuDeviceSet.Attach{T}"/>), made
/// by <see cref="IconAtlas.CreateTexture"/> from the atlas's copies in memory
/// in a single CreateTexture2D, so it is complete the moment it exists - at
/// start-up straight from the disk cache, after the card is lost, and when the
/// window moves to the other card's monitor.  After that the atlas brings it
/// up to date through <see cref="IconAtlas.ProcessArrivals(IconAtlasTexture?, ID3D11DeviceContext?, int)"/>,
/// which remembers per slot which version of the icon this card has.
///
/// It starts at 256 slices (about 5.6 MB) and doubles, up to 2048, by copying
/// on the GPU into a new array; <see cref="View"/> changes when it does, so the
/// renderer takes it afresh every frame rather than keeping it.
///
/// Threads: made on any thread (the device is free-threaded); uploads and
/// growth use the immediate context and happen on the UI thread through the
/// atlas.  Disposal may come from whoever disposes the device set, so the
/// three are kept apart by a lock of their own.
/// </summary>
internal sealed class IconAtlasTexture : IDisposable
{
    private const uint MipLevels = IconSlotData.MipLevels;

    /// <summary>What an empty slice starts as: transparent, which draws nothing.</summary>
    private static readonly byte[] Blank = GC.AllocateArray<byte>(IconSlotData.MipByteCount(0), pinned: true);

    private readonly object _gate = new();
    private int[] _versions;
    private bool _disposed;

    internal IconAtlasTexture(ID3D11Device device, int capacity, IconSlotData?[] slots, int[] versions)
    {
        Device = device;
        var initial = new SubresourceData[capacity * IconSlotData.MipLevels];
        var blank = AddressOfBlank();
        for (var slice = 0; slice < capacity; slice++)
        {
            var data = slice < slots.Length ? slots[slice] : null;
            for (var mip = 0; mip < IconSlotData.MipLevels; mip++)
            {
                var index = (int)D3D11.CalculateSubResourceIndex((uint)mip, (uint)slice, MipLevels);
                var rowPitch = (uint)(IconSlotData.MipSize(mip) * 4);
                initial[index] = new SubresourceData(data?.AddressOf(mip) ?? blank, rowPitch, 0);
            }
        }

        var description = Describe(capacity);
        Texture = device.CreateTexture2D(description, initial.AsSpan());
        try
        {
            View = device.CreateShaderResourceView(Texture);
        }
        catch
        {
            Texture.Dispose();
            throw;
        }

        Capacity = capacity;
        _versions = new int[capacity];
        for (var slot = 0; slot < Math.Min(slots.Length, capacity); slot++)
        {
            if (slots[slot] is not null)
            {
                _versions[slot] = versions[slot];
            }
        }
    }

    /// <summary>The device the texture was made on.</summary>
    public ID3D11Device Device { get; }

    /// <summary>The array itself.  Replaced when it grows.</summary>
    public ID3D11Texture2D Texture { get; private set; }

    /// <summary>All slices and all levels, as the icon shader's Texture2DArray.  Replaced when it grows.</summary>
    public ID3D11ShaderResourceView View { get; private set; }

    /// <summary>How many slices the array has now.</summary>
    public int Capacity { get; private set; }

    public bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    /// <summary>The version of slot <paramref name="slot"/> this card has; zero for none.</summary>
    internal int VersionOf(int slot)
    {
        lock (_gate)
        {
            return slot < _versions.Length ? _versions[slot] : 0;
        }
    }

    /// <summary>
    /// Copies all seven levels of <paramref name="data"/> into slice
    /// <paramref name="slot"/>, which must be inside <see cref="Capacity"/>.
    /// Draws already queued that use the slice still see what it held.
    /// </summary>
    internal bool Upload(ID3D11DeviceContext context, int slot, IconSlotData data, int version)
    {
        lock (_gate)
        {
            if (_disposed || slot >= Capacity)
            {
                return false;
            }

            for (var mip = 0; mip < IconSlotData.MipLevels; mip++)
            {
                context.UpdateSubresource(
                    Texture,
                    D3D11.CalculateSubResourceIndex((uint)mip, (uint)slot, MipLevels),
                    null,
                    data.AddressOf(mip),
                    (uint)(IconSlotData.MipSize(mip) * 4),
                    0);
            }

            _versions[slot] = version;
            return true;
        }
    }

    /// <summary>
    /// Makes the array <paramref name="capacity"/> slices long: a new array,
    /// every slice this card has copied into it on the GPU, then the old one
    /// and its view let go.
    /// </summary>
    internal bool Grow(ID3D11DeviceContext context, int capacity)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            if (capacity <= Capacity)
            {
                return true;
            }

            var description = Describe(capacity);
            var grown = Device.CreateTexture2D(description);
            ID3D11ShaderResourceView view;
            try
            {
                for (var slice = 0; slice < Capacity; slice++)
                {
                    if (_versions[slice] == 0)
                    {
                        continue;
                    }

                    for (var mip = 0u; mip < MipLevels; mip++)
                    {
                        var index = D3D11.CalculateSubResourceIndex(mip, (uint)slice, MipLevels);
                        context.CopySubresourceRegion(grown, index, 0, 0, 0, Texture, index, null);
                    }
                }

                view = Device.CreateShaderResourceView(grown);
            }
            catch
            {
                grown.Dispose();
                throw;
            }

            View.Dispose();
            Texture.Dispose();
            Texture = grown;
            View = view;
            Capacity = capacity;
            Array.Resize(ref _versions, capacity);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            View.Dispose();
            Texture.Dispose();
        }
    }

    private static Texture2DDescription Describe(int capacity) => new()
    {
        Width = IconSlotData.Size,
        Height = IconSlotData.Size,
        MipLevels = MipLevels,
        ArraySize = (uint)capacity,
        Format = Format.B8G8R8A8_UNorm,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.ShaderResource,
        CPUAccessFlags = CpuAccessFlags.None,
        MiscFlags = ResourceOptionFlags.None
    };

    private static unsafe nint AddressOfBlank()
        => (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(Blank));
}
