using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// A growable array of instances in native memory, filled afresh every frame
/// and copied as it is into a GPU buffer.
///
/// A frame of the canvas is tens of thousands of cells at sixty-four bytes
/// each; a managed list of structs would be the same bytes on the garbage
/// collector's heap, and a big one lands on the large object heap, where
/// every growth is a gen 2 allocation.  Here the memory is the list's own:
/// it grows by doubling, is never given back while the list lives, and a
/// frame that fits in what earlier frames needed allocates nothing at all.
/// <see cref="Add"/> hands back a reference to the new slot so the caller
/// writes the instance in place rather than copying it in.
///
/// Not thread-safe: one list belongs to the thread that fills it (the UI
/// thread, for the canvas).
/// </summary>
internal sealed unsafe class InstanceList<T> : IDisposable where T : unmanaged
{
    private T* _items;
    private int _capacity;
    private int _count;

    public InstanceList(int initialCapacity = 1024)
    {
        _capacity = Math.Max(16, initialCapacity);
        _items = (T*)NativeMemory.Alloc((nuint)_capacity, (nuint)sizeof(T));
    }

    ~InstanceList() => Free();

    /// <summary>Instances added since the last <see cref="Clear"/>.</summary>
    public int Count => _count;

    /// <summary>How many fit before the next growth.</summary>
    public int Capacity => _capacity;

    /// <summary>The first instance; valid until the next <see cref="Add"/> that grows the list.</summary>
    public T* Pointer => _items;

    /// <summary>The bytes <see cref="Count"/> instances take.</summary>
    public long ByteCount => (long)_count * sizeof(T);

    /// <summary>The instances, for reading; valid until the next <see cref="Add"/>.</summary>
    public ReadOnlySpan<T> AsSpan() => new(_items, _count);

    /// <summary>A new instance at the end, returned by reference to be written in place.  Its bytes are whatever was there.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Add()
    {
        if (_count == _capacity)
        {
            Grow();
        }

        return ref _items[_count++];
    }

    /// <summary>Empties the list and keeps its memory.</summary>
    public void Clear() => _count = 0;

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow()
    {
        ObjectDisposedException.ThrowIf(_items is null, this);
        var capacity = checked(_capacity * 2);
        _items = (T*)NativeMemory.Realloc(_items, checked((nuint)capacity * (nuint)sizeof(T)));
        _capacity = capacity;
    }

    private void Free()
    {
        if (_items is not null)
        {
            NativeMemory.Free(_items);
            _items = null;
            _capacity = 0;
            _count = 0;
        }
    }
}
