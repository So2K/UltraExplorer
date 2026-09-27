using System.Windows.Media;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace UltraExplorer.Controls;

// Where the Shell icons on the file names come from on the CPU path: the
// window's lookup, the inbox the answers arrive in, and how often a frame
// draws the names again for them.
public sealed partial class NestedCanvas
{
    private FrameInbox<IconArrival>? _iconArrivals;

    /// <summary>Icons arrived that the names on screen have not been drawn with yet: a redraw is owed, held back until it is allowed.</summary>
    private bool _iconRedrawHeld;

    /// <summary>The frame time of the last redraw of the names made for icons alone; none before the first.</summary>
    private TimeSpan? _lastIconRedraw;

    /// <summary>
    /// The Shell icon for one of a folder's files - the folder, and the
    /// file's index among its shown files - or null while it is not known
    /// yet.  By folder and index rather than by path: a path is a new string,
    /// and only the few kinds of file whose icon differs from file to file
    /// need one; the window builds it for those alone.
    /// </summary>
    public Func<NestedFolder, int, ImageSource?>? IconLookup { get; set; }

    /// <summary>
    /// Where the answers to <see cref="IconLookup"/>'s questions arrive
    /// (<see cref="ShellIconService.CanvasArrivals"/>): taken in at the start
    /// of a frame, all of them, which is at most one redraw of the names
    /// however many came.  The canvas drives the inbox's slot from the moment
    /// it is set - a post wakes the frame loop, or the slot's fallback while
    /// the canvas is off screen - and gives it back when it is replaced.
    /// </summary>
    internal FrameInbox<IconArrival>? IconArrivals
    {
        get => _iconArrivals;
        set
        {
            if (ReferenceEquals(value, _iconArrivals))
            {
                return;
            }

            if (_iconArrivals is { } previous)
            {
                StopDriving(previous.Driver);
            }

            _iconArrivals = value;
            _iconRedrawHeld = false;
            if (value is not null)
            {
                Drive(value.Driver);
            }
        }
    }

    /// <summary>Redraws of the names made for arrived icons alone, for the checks and the bench.</summary>
    internal int IconRedraws { get; private set; }

    /// <summary>Arrivals taken from <see cref="IconArrivals"/>, for the checks and the bench.</summary>
    internal int IconArrivalsTaken { get; private set; }

    /// <summary>A Shell icon arrived: only the names and icons on the cells need drawing again.</summary>
    public void RefreshIcons() => RequestFrame(Layers.Labels);

    /// <summary>
    /// Phase 4 of a frame: everything that arrived is taken in, and is one
    /// redraw of the names.  A frame that draws the names anyway - the camera
    /// moved, a folder was read - takes the icons along.  Otherwise the
    /// redraw is for the icons alone, and at rest that comes at most every
    /// <see cref="FrameBudgets.IconRefreshMinMs"/>: a folder of two hundred
    /// programs answered one by one is a handful of redraws, not two hundred
    /// - at 4K on the CPU each one is every name on screen laid out again.
    /// A redraw not allowed yet is held, and the held redraw keeps the loop
    /// going (<see cref="PendingIconWork"/>) until its time comes.
    /// </summary>
    partial void DrainIconArrivals()
    {
        if (_iconArrivals is { } inbox)
        {
            inbox.Rearm();
            while (inbox.TryTake(out _))
            {
                IconArrivalsTaken++;
                _iconRedrawHeld = true;
            }
        }

        if (!_iconRedrawHeld)
        {
            return;
        }

        if ((_dirty & (Layers.Scene | Layers.Labels)) != 0)
        {
            _iconRedrawHeld = false;
            return;
        }

        var now = _clock.Now;
        if (_lastIconRedraw is { } last && (now - last).TotalMilliseconds < FrameBudgets.IconRefreshMinMs)
        {
            return;
        }

        _iconRedrawHeld = false;
        _lastIconRedraw = now;
        IconRedraws++;
        _dirty |= Layers.Labels;
    }

    /// <summary>Icons are waiting in the inbox, or a redraw for them is held back.</summary>
    partial void PendingIconWork(ref bool pending)
    {
        if (_iconRedrawHeld || _iconArrivals is { IsEmpty: false })
        {
            pending = true;
        }
    }
}
