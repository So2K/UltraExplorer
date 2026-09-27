using System.Diagnostics;
using System.Windows.Threading;
using UltraExplorer.Services;
using UltraExplorer.Services.Watch;

namespace UltraExplorer.Controls;

// Live: the change hub's side of the frame.  Changes on disk are taken in at
// the start of a frame, phase 3, within their own slice of it: the tree
// queues the folders on screen to be read again and marks the rest, the
// folder list and the tree canvas start their own reads, and the reads come
// back through the tree's inbox a few frames later, in phase 5, like any
// other.  While the canvas cannot draw, the hub's own fallback driver takes
// the changes in instead, so nothing waits for the canvas to be shown.
public sealed partial class NestedCanvas
{
    /// <summary>How often the shares on screen are noted to the hub while the view is still, so their watches stay armed.</summary>
    private static readonly TimeSpan ShareKeepAlive = TimeSpan.FromSeconds(10);

    /// <summary>The least time between two notes from frames: a moving view draws a hundred frames a second, and a share needs telling once.</summary>
    private const double ShareNoteMinMilliseconds = 1000;

    private ChangeHub? _changes;
    private IChangeSink? _changeSink;
    private DispatcherTimer? _shareKeepAlive;
    private long _sharesNotedAt;
    private readonly List<WatchRoot> _sharesOnScreen = [];

    /// <summary>The change hub whose changes this canvas takes in at the start of its frames, if any.</summary>
    internal ChangeHub? Changes => _changes;

    /// <summary>
    /// Takes over the change hub's wakes: what it has for <paramref name="sink"/>
    /// - the tree's folders, the folder list's, the tree canvas's - is taken in
    /// at the start of this canvas's frames while it can draw, and by the hub's
    /// fallback driver while it cannot.  Null for either lets go.
    /// </summary>
    internal void AttachChanges(ChangeHub? hub, IChangeSink? sink)
    {
        if (_changes is { } previous)
        {
            StopDriving(previous.Driver);
        }

        _changes = hub is not null && sink is not null ? hub : null;
        _changeSink = _changes is null ? null : sink;
        if (_changes is { } current)
        {
            Drive(current.Driver);
        }
        else
        {
            _shareKeepAlive?.Stop();
            _sharesOnScreen.Clear();
        }
    }

    /// <summary>
    /// Phase 3: the changes the hub has due, within <paramref name="budget"/>.
    /// The tree is told first where the view is, so a folder that went with
    /// the view inside it counts as on screen.  Then the shares on screen are
    /// noted, at most once a second: a share's watch is kept armed only while
    /// something of it is drawn.
    /// </summary>
    partial void DrainChangeHub(ref FrameBudget budget)
    {
        if (_tree is not { } tree)
        {
            return;
        }

        tree.CameraAnchor = _anchor;
        if (_changes is not { } hub || _changeSink is not { } sink)
        {
            return;
        }

        hub.Drain(ref budget, sink);
        if (Stopwatch.GetElapsedTime(_sharesNotedAt).TotalMilliseconds >= ShareNoteMinMilliseconds)
        {
            NoteSharesOnScreen(hub, tree);
        }
    }

    partial void PendingHubWork(ref bool pending)
    {
        if (_changes is { HasWork: true })
        {
            pending = true;
        }
    }

    /// <summary>
    /// Tells the hub which shares are on screen: those whose drive or root
    /// cell was drawn lately, or holds the folder the view is fixed to.  While
    /// any is, a timer keeps telling it every <see cref="ShareKeepAlive"/>,
    /// because a view left still draws no frames and would otherwise let the
    /// watch lapse under a user who is looking right at it.
    /// </summary>
    private void NoteSharesOnScreen(ChangeHub hub, NestedTree tree)
    {
        _sharesNotedAt = Stopwatch.GetTimestamp();
        _sharesOnScreen.Clear();
        foreach (var root in tree.Root.AllChildren)
        {
            if (root.Watch is { IsNetwork: true } share
                && !_sharesOnScreen.Contains(share)
                && (root.LastDrawnFrame >= tree.Frame - NestedTree.ExpireAfterFrames || _anchor is { } anchor && root.Contains(anchor)))
            {
                _sharesOnScreen.Add(share);
            }
        }

        foreach (var share in _sharesOnScreen)
        {
            hub.NoteDrawn(share);
        }

        if (_sharesOnScreen.Count == 0)
        {
            _shareKeepAlive?.Stop();
            return;
        }

        if (_shareKeepAlive is null)
        {
            _shareKeepAlive = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = ShareKeepAlive };
            _shareKeepAlive.Tick += (_, _) =>
            {
                if (IsVisible && _changes is { } current && _tree is { } drawn)
                {
                    NoteSharesOnScreen(current, drawn);
                }
                else
                {
                    _shareKeepAlive?.Stop();
                }
            };
        }

        if (!_shareKeepAlive.IsEnabled)
        {
            _shareKeepAlive.Start();
        }
    }
}
