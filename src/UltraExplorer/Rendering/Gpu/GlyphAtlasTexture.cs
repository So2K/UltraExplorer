using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using Vortice.DXGI;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// The glyph atlas on one graphics card: an R8 Texture2DArray with a slice
/// per atlas page, and the view the glyph shader samples.
///
/// It is made from the atlas's CPU pages in one CreateTexture2D call, so a
/// second card, or the same card after a lost device, has every glyph at
/// once.  After that <see cref="Upload"/>, called once per frame on the
/// thread that owns the context, copies the rectangles placed since with
/// UpdateSubresource - each a few hundred bytes to a few kilobytes.  When the
/// atlas has grown more pages than the array has slices - the first name in
/// a script the start-up set does not hold, CJK most of all - a larger array
/// is made on the card and the slices it has are copied across by the GPU
/// (<see cref="Grow"/>), the way the icon atlas grows: making it again from
/// the CPU pages would copy 16 to 64 MB through the runtime on the UI
/// thread, in the middle of a frame, and hold that frame's present for the
/// transfer.
///
/// Not thread-safe: the immediate context belongs to one thread.
/// </summary>
internal sealed unsafe class GlyphAtlasTexture : IDisposable
{
    /// <summary>Rectangles uploaded per call at most, so a burst of new glyphs is spread over frames.</summary>
    public const int UploadBudget = 2048;

    private readonly ID3D11Device _device;
    private readonly GlyphAtlas _atlas;
    private readonly GlyphPlacement[] _pending = new GlyphPlacement[UploadBudget];
    private int _uploaded;

    public GlyphAtlasTexture(ID3D11Device device, GlyphAtlas atlas)
    {
        _device = device;
        _atlas = atlas;
        Recreate();
    }

    public ID3D11Texture2D Texture { get; private set; } = null!;

    public ID3D11ShaderResourceView View { get; private set; } = null!;

    /// <summary>Slices in the array; at least the atlas's page count after <see cref="Upload"/>.</summary>
    public int Capacity { get; private set; }

    /// <summary>
    /// Brings the texture up to date with the atlas: grown first when the
    /// atlas has more pages than it has slices (<see cref="View"/> is then a
    /// new view, which the caller hands the frame after this), then the
    /// rectangles placed since, at most <see cref="UploadBudget"/> of them.
    /// Returns the number of rectangles copied.
    /// </summary>
    public int Upload(ID3D11DeviceContext context)
    {
        if (_atlas.PageCount > Capacity)
        {
            Grow(context);
        }

        var count = _atlas.CopyPlacements(_uploaded, _pending);
        for (var index = 0; index < count; index++)
        {
            var placement = _pending[index];
            if (placement.Page >= Capacity)
            {
                // On a page the worker opened after the texture grew above:
                // the next upload grows it again and copies this one then.
                count = index;
                break;
            }

            if (placement.Width <= 0 || placement.Height <= 0)
            {
                continue;
            }

            var source = (byte*)_atlas.PagePointer(placement.Page) + (long)placement.Y * GlyphAtlas.PageSize + placement.X;
            var box = new Box(placement.X, placement.Y, 0, placement.X + placement.Width, placement.Y + placement.Height, 1);
            context.UpdateSubresource(
                Texture,
                D3D11.CalculateSubResourceIndex(0, (uint)placement.Page, 1),
                box,
                (nint)source,
                GlyphAtlas.PageSize,
                0);
        }

        _uploaded += count;
        return count;
    }

    /// <summary>
    /// Makes the array larger on the card: a new array with no initial data,
    /// every slice this one holds copied into it by the GPU, and the slices
    /// past them cleared - through a render target view, also on the GPU -
    /// because bilinear sampling at a glyph's edge reads the texel of gap
    /// beside it, which must be empty.  Nothing comes from the CPU pages:
    /// what they hold that the texture does not is in the placement log,
    /// which <see cref="Upload"/> copies next, as it does every frame.  If
    /// the card cannot make the array, the exception goes to the caller - as
    /// good as a lost device - and this texture stays as it was.
    /// </summary>
    internal void Grow(ID3D11DeviceContext context)
    {
        var capacity = CapacityFor(_atlas.PageCount);
        if (capacity <= Capacity)
        {
            return;
        }

        var description = Describe(capacity, BindFlags.ShaderResource | BindFlags.RenderTarget);
        var grown = _device.CreateTexture2D(in description);
        ID3D11ShaderResourceView? view = null;
        try
        {
            using (var clear = _device.CreateRenderTargetView(grown, new RenderTargetViewDescription
            {
                Format = Format.R8_UNorm,
                ViewDimension = RenderTargetViewDimension.Texture2DArray,
                Texture2DArray = new Texture2DArrayRenderTargetView
                {
                    MipSlice = 0,
                    FirstArraySlice = (uint)Capacity,
                    ArraySize = (uint)(capacity - Capacity)
                }
            }))
            {
                context.ClearRenderTargetView(clear, new Color4(0, 0, 0, 0));
            }

            for (var slice = 0u; slice < (uint)Capacity; slice++)
            {
                var index = D3D11.CalculateSubResourceIndex(0, slice, 1);
                context.CopySubresourceRegion(grown, index, 0, 0, 0, Texture, index, null);
            }

            view = CreateView(grown, capacity);
        }
        catch
        {
            view?.Dispose();
            grown.Dispose();
            throw;
        }

        View.Dispose();
        Texture.Dispose();
        Texture = grown;
        View = view;
        Capacity = capacity;
        Grown++;
    }

    /// <summary>How many times the array has been made larger on the card since it was made.</summary>
    public int Grown { get; private set; }

    /// <summary>
    /// Makes the array from the CPU pages: when the texture is first made on
    /// a card.  The count of placements is taken before the pages are read,
    /// so a glyph placed meanwhile is uploaded again by the next
    /// <see cref="Upload"/> rather than missed.
    /// </summary>
    private void Recreate()
    {
        var seen = _atlas.PlacementCount;
        var pages = _atlas.PageCount;
        var capacity = CapacityFor(pages);

        // Slices past the atlas's pages start empty, read from a zero page.
        var zeroPage = capacity > pages ? (nint)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)GlyphAtlas.PageSize * GlyphAtlas.PageSize) : 0;
        try
        {
            var initial = new SubresourceData[capacity];
            for (var slice = 0; slice < capacity; slice++)
            {
                initial[slice] = slice < pages
                    ? new SubresourceData(_atlas.PagePointer(slice), GlyphAtlas.PageSize, GlyphAtlas.PageSize * GlyphAtlas.PageSize)
                    : new SubresourceData(zeroPage, GlyphAtlas.PageSize, GlyphAtlas.PageSize * GlyphAtlas.PageSize);
            }

            var description = Describe(capacity, BindFlags.ShaderResource);
            var texture = _device.CreateTexture2D(in description, initial);
            ID3D11ShaderResourceView view;
            try
            {
                view = CreateView(texture, capacity);
            }
            catch
            {
                texture.Dispose();
                throw;
            }

            View?.Dispose();
            Texture?.Dispose();
            Texture = texture;
            View = view;
            Capacity = capacity;
            _uploaded = seen;
        }
        finally
        {
            if (zeroPage != 0)
            {
                System.Runtime.InteropServices.NativeMemory.Free((void*)zeroPage);
            }
        }
    }

    public void Dispose()
    {
        View?.Dispose();
        Texture?.Dispose();
    }

    /// <summary>Slices for this many pages: a power of two, at least the atlas's first pages, at most its last.</summary>
    private static int CapacityFor(int pages) =>
        Math.Min(GlyphAtlas.MaximumPages, Math.Max(GlyphAtlas.InitialPages, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, pages))));

    private static Texture2DDescription Describe(int capacity, BindFlags bindFlags) => new(
        Format.R8_UNorm,
        GlyphAtlas.PageSize,
        GlyphAtlas.PageSize,
        (uint)capacity,
        1,
        bindFlags,
        ResourceUsage.Default,
        CpuAccessFlags.None,
        1,
        0,
        ResourceOptionFlags.None);

    private ID3D11ShaderResourceView CreateView(ID3D11Texture2D texture, int capacity) =>
        _device.CreateShaderResourceView(texture, new ShaderResourceViewDescription
        {
            Format = Format.R8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2DArray,
            Texture2DArray = new Texture2DArrayShaderResourceView
            {
                MostDetailedMip = 0,
                MipLevels = 1,
                FirstArraySlice = 0,
                ArraySize = (uint)capacity
            }
        });
}
