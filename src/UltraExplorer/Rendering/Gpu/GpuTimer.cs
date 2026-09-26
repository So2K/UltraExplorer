using Vortice.Direct3D11;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// How long the GPU itself spent on a frame, measured with timestamp queries
/// and read back without ever waiting for them.
///
/// Each frame takes one of four slots - a disjoint query around two
/// timestamps - and the answers are collected when the GPU has them, normally
/// two frames later, into <see cref="LastGpuMilliseconds"/>.  If all four
/// slots are still in flight the frame simply goes untimed rather than
/// stalling on the oldest one.  UI thread only, like the immediate context it
/// records into.
/// </summary>
internal sealed unsafe class GpuTimer : IDisposable
{
    private const int Depth = 4;

    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Query[] _disjoint = new ID3D11Query[Depth];
    private readonly ID3D11Query[] _start = new ID3D11Query[Depth];
    private readonly ID3D11Query[] _end = new ID3D11Query[Depth];
    private readonly bool[] _inFlight = new bool[Depth];
    private int _next;
    private int _oldest;
    private bool _open;

    public GpuTimer(ID3D11Device device, ID3D11DeviceContext context)
    {
        _context = context;
        for (var slot = 0; slot < Depth; slot++)
        {
            _disjoint[slot] = device.CreateQuery(QueryType.TimestampDisjoint);
            _start[slot] = device.CreateQuery(QueryType.Timestamp);
            _end[slot] = device.CreateQuery(QueryType.Timestamp);
        }
    }

    /// <summary>The GPU time of the most recent frame whose answer has arrived; NaN until the first.</summary>
    public double LastGpuMilliseconds { get; private set; } = double.NaN;

    /// <summary>How many frames have been timed so far.</summary>
    public long Samples { get; private set; }

    /// <summary>Marks the start of a frame's GPU work.  Pair with <see cref="End"/>.</summary>
    public void Begin()
    {
        Collect();
        if (_open || _inFlight[_next])
        {
            return;
        }

        _context.Begin(_disjoint[_next]);
        _context.End(_start[_next]);
        _open = true;
    }

    /// <summary>Marks the end of the frame's GPU work.</summary>
    public void End()
    {
        if (!_open)
        {
            return;
        }

        _context.End(_end[_next]);
        _context.End(_disjoint[_next]);
        _inFlight[_next] = true;
        _next = (_next + 1) % Depth;
        _open = false;
    }

    /// <summary>Takes in every answer the GPU already has, oldest first, without flushing or waiting.</summary>
    public void Collect()
    {
        while (_inFlight[_oldest])
        {
            Vortice.Direct3D11.QueryDataTimestampDisjoint disjoint;
            ulong start;
            ulong end;
            if (_context.GetData(_disjoint[_oldest], (IntPtr)(&disjoint), (uint)sizeof(Vortice.Direct3D11.QueryDataTimestampDisjoint), AsyncGetDataFlags.DoNotFlush).Code != 0
                || _context.GetData(_start[_oldest], (IntPtr)(&start), sizeof(ulong), AsyncGetDataFlags.DoNotFlush).Code != 0
                || _context.GetData(_end[_oldest], (IntPtr)(&end), sizeof(ulong), AsyncGetDataFlags.DoNotFlush).Code != 0)
            {
                return;
            }

            if (!disjoint.Disjoint && disjoint.Frequency != 0 && end >= start)
            {
                LastGpuMilliseconds = (end - start) * 1000.0 / disjoint.Frequency;
                Samples++;
            }

            _inFlight[_oldest] = false;
            _oldest = (_oldest + 1) % Depth;
        }
    }

    public void Dispose()
    {
        for (var slot = 0; slot < Depth; slot++)
        {
            _disjoint[slot].Dispose();
            _start[slot].Dispose();
            _end[slot].Dispose();
        }
    }
}
