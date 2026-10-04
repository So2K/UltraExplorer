using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltraExplorer.Controls;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

/// <summary>
/// An icon the Shell answered for the nested canvas, waiting in
/// <see cref="ShellIconService.CanvasArrivals"/> for the canvas's next frame:
/// the key it was asked under, and the icon, which may be null when the Shell
/// had none.  The canvas only needs to know that something arrived - the
/// icon itself is in the service's cache for the next lookup - but a test
/// can see what did.
/// </summary>
internal readonly record struct IconArrival(string Key, ImageSource? Icon);

/// <summary>
/// Shell icons, cached by file type, or by file for the few types whose icon
/// differs from file to file.
///
/// <para>Resolution happens on a dedicated STA worker because SHGetFileInfo can
/// take milliseconds per distinct executable, and doing that inline while a
/// folder of several thousand entries is being materialised freezes the UI
/// thread for however long the folder is big.  Callers get the cached icon
/// immediately if there is one and are told about the rest.  The Shell can
/// also hang on one file - a shortcut to a share that does not answer holds
/// it for the network's timeout - so when every worker has been on one icon
/// for a few seconds while others wait, another is started, as the GPU
/// canvas's extractor does (IconExtractor), and takes the rest.</para>
///
/// <para><b>Two ways of being told.</b>  The nested canvas asks through
/// <see cref="GetForCanvas"/>, and what the Shell answers goes into one queue,
/// <see cref="CanvasArrivals"/> - one per canvas when the view is split
/// (<see cref="SubscribeCanvas"/>) - that the canvas takes in at the start of its
/// next frame: two hundred programs in System32 are one wake of the frame loop
/// and one redraw of the names, not two hundred dispatcher operations each
/// redrawing every name on screen - which at 4K was thirteen seconds of
/// stutter after a zoom into a folder of files.  Everyone else - the folder
/// list, the graph - passes a callback to <see cref="Request"/>; those are
/// called on the UI thread in batches, at most one batch every sixteen
/// milliseconds, however fast the answers come.</para>
///
/// <para><b>Two lanes.</b>  What is on screen now goes on a stack, so the
/// newest frame's icons are answered first and a zoom that has moved on does
/// not wait for the files it flew past.  Types a folder that was just read
/// holds (<see cref="Prefetch"/>) go on a queue behind everything on screen,
/// so that by the time its names are big enough to read their icons are
/// usually there.</para>
///
/// <para><b>Which file speaks for a type.</b>  The Shell is always asked about
/// a real file, and for most types any file of the type gives the same
/// picture.  Not for all of them: the types known to give every file a
/// picture of its own are asked about file by file (<see cref="IsPathSpecificIcon"/>),
/// but an icon handler can do the same for any other type.
/// The picture a type is drawn with has always been the one of the first file
/// of it somebody showed, so a prefetch - asked about whichever file of the
/// type the folder listed first - only holds the type's place: its icon shows
/// until something on screen asks, and then the Shell is asked again about
/// that file, which replaces it when the picture differs.</para>
/// </summary>
public sealed class ShellIconService : IDisposable
{
    /// <summary>The key every folder's icon is kept under.</summary>
    private const string FolderKey = "<folder>";

    /// <summary>
    /// The canvas remembers at most this many keys it asked for; past it, it
    /// forgets them all and asks again for what it still draws.  A key it
    /// asked for is answered from the cache, so only those still on their way
    /// are asked again, and the Shell is not: the one question waits for them.
    /// </summary>
    private const int CanvasAskLimit = 20_000;

    /// <summary>
    /// At most this many icons of single files - programs, shortcuts and the
    /// like, one per path - are kept; the next one lets all of them go, to be
    /// asked for again as they are shown, so a walk through a million
    /// programs, or one search after another, cannot grow the cache without
    /// end.  Far more than a screen shows at once: what is on screen is asked
    /// for again once, not over and over.
    /// </summary>
    private const int FileIconLimit = 20_000;

    /// <summary>Callbacks are called at most this often: one batch per frame of a 60 Hz display.</summary>
    private const double CallbackIntervalMilliseconds = 16;

    /// <summary>
    /// At most this many threads ask the Shell, however many are stuck.  A
    /// new one starts only when every one is stuck, so this is how many
    /// icons the network can hold up at once: with four, four shortcuts to a
    /// server that is off took every thread, and the rows on screen waited
    /// for the network's timeout.
    /// </summary>
    private const int MaximumWorkerCount = 12;

    /// <summary>How often the watchdog looks while work waits.</summary>
    private const int WatchIntervalMilliseconds = 1000;

    /// <summary>A worker on one icon for longer than this (Stopwatch ticks, four seconds) is stuck: no icon takes that long but one the network holds up.</summary>
    private static readonly long StuckTicks = Stopwatch.Frequency * 4;

    /// <summary>The types whose every file has an icon of its own (<see cref="IsPathSpecificIcon"/>), the most common first.</summary>
    private static readonly string[] PathSpecificTypes = ["exe", "lnk", "ico", "url", "cur", "ani", "msc", "appref-ms"];

    private readonly ConcurrentDictionary<string, CachedIcon> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Icons asked for and not answered yet, by cache key.  One entry per key
    /// however many ask: the Shell is asked once and all of them are told.
    /// Guarded by <see cref="_gate"/>, as are the two lanes.
    /// </summary>
    private readonly Dictionary<string, PendingIcon> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Asked for by something on screen: newest first.  Each item is one
    /// icon the canvas asked for, or a run of the rows a list asked for in
    /// one go (<see cref="_openRun"/>), answered in the order they were asked.
    /// </summary>
    private readonly Stack<VisibleAsk> _visible = new();

    /// <summary>
    /// The run on <see cref="_visible"/> that the rows a list asks for join
    /// until the dispatcher operation asking them is over, or null.  A list
    /// asks top to bottom, and one at a time on the stack the top row was
    /// answered last; in a run the top row is answered first, and a newer
    /// list's run still goes before an older one's.  Guarded by <see cref="_gate"/>.
    /// </summary>
    private Queue<PendingIcon>? _openRun;

    /// <summary>Types of folders just read: first come first served, and only once nothing on screen is waiting.</summary>
    private readonly Queue<PendingIcon> _prefetch = new();

    private readonly object _gate = new();
    private readonly Func<string, bool, ImageSource?> _extract;
    private readonly Dispatcher? _callbackDispatcher;

    /// <summary>The threads asking the Shell: one, and another each time every one of them is stuck (<see cref="ReplaceStuckWorkers"/>).  Guarded by <see cref="_gate"/>.</summary>
    private readonly List<IconWorker> _workers = [];

    /// <summary>Looks once a second, while work waits, for workers stuck on one icon; disarmed while nothing waits.</summary>
    private readonly Timer _watchdog;
    private bool _watchArmed;

    // The callbacks' batches.
    private readonly ConcurrentQueue<Answer> _answers = new();
    private readonly Timer _flushTimer;
    private readonly Action _flush;
    private int _flushScheduled;
    private long _lastFlush;

    // Only ever touched on the thread the canvas draws on.
    private readonly HashSet<string> _canvasAsked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _prefetchFirsts = new(ReferenceEqualityComparer.Instance);

    /// <summary>The <see cref="_fileIconsLetGo"/> the canvas last forgot what it asked for at.</summary>
    private int _canvasLetGoSeen;

    /// <summary>Icons of single files added to the cache since they were last let go of.  Guarded by <see cref="_gate"/>.</summary>
    private int _fileIcons;

    /// <summary>How often the icons of single files were let go of (<see cref="LetFileIconsGo"/>).</summary>
    private int _fileIconsLetGo;

    private volatile bool _disposed;
    private int _extractions;
    private int _canvasArrivalsPosted;
    private int _callbackBatches;

    public ShellIconService()
        : this(ExtractIcon, callbackDispatcher: null)
    {
    }

    /// <summary>
    /// For the checks: <paramref name="extract"/> stands in for the Shell -
    /// it is called on the worker with the path asked about and whether it is
    /// a folder - and <paramref name="callbackDispatcher"/>, when given, is
    /// where the callbacks' batches run instead of the application's
    /// dispatcher.
    /// </summary>
    internal ShellIconService(Func<string, bool, ImageSource?> extract, Dispatcher? callbackDispatcher)
    {
        _extract = extract;
        _callbackDispatcher = callbackDispatcher;
        _flush = FlushAnswers;
        _flushTimer = new Timer(_ => PostFlush(), null, Timeout.Infinite, Timeout.Infinite);

        CanvasArrivals = NewCanvasInbox();
        _canvasInboxes = [CanvasArrivals];

        _watchdog = new Timer(_ => Watch(), null, Timeout.Infinite, Timeout.Infinite);
        lock (_gate)
        {
            StartWorker();
        }
    }

    /// <summary>
    /// Where the icons the canvas asked for arrive, one item per icon, for the
    /// canvas to take in at the start of a frame (NestedCanvas.IconArrivals).
    /// Its driver slot's fallback lets them go while no canvas drives it.  The
    /// first canvas's - the only one, unless the view is split: every other
    /// canvas has an inbox of its own (<see cref="SubscribeCanvas"/>).
    /// </summary>
    internal FrameInbox<IconArrival> CanvasArrivals { get; }

    /// <summary>
    /// Every canvas's inbox, <see cref="CanvasArrivals"/> first: an icon one
    /// canvas asked for arrives in all of them, since the Shell is asked once
    /// for everybody (<see cref="_canvasAsked"/>) and the other pane may be
    /// drawing a file of the same type without an icon, waiting for it.
    /// Replaced whole when a canvas comes or goes, so the worker posting an
    /// answer walks the list as it was.
    /// </summary>
    private FrameInbox<IconArrival>[] _canvasInboxes;

    /// <summary>
    /// An inbox of its own for another canvas drawing file names - the second
    /// pane of a split view - which the Shell's answers arrive in from now on,
    /// as they do in <see cref="CanvasArrivals"/>.  Given back with
    /// <see cref="UnsubscribeCanvas"/> when the canvas goes.  On the UI thread.
    /// </summary>
    internal FrameInbox<IconArrival> SubscribeCanvas()
    {
        var inbox = NewCanvasInbox();
        lock (_gate)
        {
            Volatile.Write(ref _canvasInboxes, [.. _canvasInboxes, inbox]);
        }

        return inbox;
    }

    /// <summary>
    /// An inbox from <see cref="SubscribeCanvas"/> is no longer taken in:
    /// nothing is posted to it any more.  <see cref="CanvasArrivals"/> is the
    /// service's own and stays.
    /// </summary>
    internal void UnsubscribeCanvas(FrameInbox<IconArrival> inbox)
    {
        if (ReferenceEquals(inbox, CanvasArrivals))
        {
            return;
        }

        lock (_gate)
        {
            if (Array.IndexOf(_canvasInboxes, inbox) >= 0)
            {
                Volatile.Write(ref _canvasInboxes, [.. _canvasInboxes.Where(kept => !ReferenceEquals(kept, inbox))]);
            }
        }
    }

    /// <summary>Canvases whose inboxes the answers arrive in, for the checks.</summary>
    internal int CanvasInboxCount => Volatile.Read(ref _canvasInboxes).Length;

    /// <summary>
    /// An inbox for one canvas.  With no canvas taking it in - none attached,
    /// or the canvas off screen - what arrives is only let go of: the icons
    /// are in the cache, and a canvas that comes back draws every name again
    /// anyway.
    /// </summary>
    private static FrameInbox<IconArrival> NewCanvasInbox()
    {
        FrameInbox<IconArrival>? arrivals = null;
        arrivals = new FrameInbox<IconArrival>(new FrameDriverSlot(new ImmediateFrameDriver(
            (ref FrameBudget budget) =>
            {
                arrivals!.Rearm();
                while (arrivals.TryTake(out _))
                {
                }
            },
            () => !arrivals!.IsEmpty)));
        return arrivals;
    }

    /// <summary>Icons asked for and not answered yet, both lanes; for the checks and the bench.</summary>
    internal int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>How often the Shell was asked, for the checks.</summary>
    internal int ExtractionCount => Volatile.Read(ref _extractions);

    /// <summary>Icons posted to <see cref="CanvasArrivals"/> so far, for the checks.</summary>
    internal int CanvasArrivalsPosted => Volatile.Read(ref _canvasArrivalsPosted);

    /// <summary>Batches of callbacks posted to the dispatcher so far, for the checks.</summary>
    internal int CallbackBatches => Volatile.Read(ref _callbackBatches);

    /// <summary>The icon if it is already known, without touching the Shell.</summary>
    public ImageSource? GetCached(string path, bool isDirectory)
        => _cache.TryGetValue(KeyOf(path, isDirectory), out var cached) ? cached.Icon : null;

    /// <summary>
    /// Resolves in the background and calls back on the UI thread.  Returns true
    /// when the icon was already cached and <paramref name="completed"/> has
    /// been invoked synchronously.  Several callers may wait on the same icon;
    /// the Shell is asked once and all of them are told, in the next batch
    /// (at most one every sixteen milliseconds).
    /// </summary>
    public bool Request(string path, bool isDirectory, Action<ImageSource?> completed)
    {
        var key = KeyOf(path, isDirectory);
        if (_cache.TryGetValue(key, out var cached) && !cached.Provisional)
        {
            completed(cached.Icon);
            return true;
        }

        if (_disposed)
        {
            return false;
        }

        lock (_gate)
        {
            // Looked at again inside the lock: the worker may have finished
            // between the first look and here, and then nobody would ever call back.
            if (!_cache.TryGetValue(key, out cached) || cached.Provisional)
            {
                (AskVisible(key, path, isDirectory, inOrder: true).Callbacks ??= []).Add(completed);
                return false;
            }
        }

        completed(cached.Icon);
        return true;
    }

    /// <summary>
    /// The icon to draw one of <paramref name="folder"/>'s files with on the
    /// nested canvas - the file at <paramref name="index"/> among its shown
    /// files - or null while the Shell has not answered.  An icon not known
    /// yet is asked for, once, and its answer posted to
    /// <see cref="CanvasArrivals"/>.
    ///
    /// <para>Called for every file name on screen in every frame of names, so
    /// it builds nothing for a file whose icon is its type's: the type is
    /// the file's interned extension, already the key it is cached under.
    /// Only the few types whose icon is per file - programs, shortcuts and
    /// the like - make the file's path, which is their key.  On the thread the
    /// canvas draws on.</para>
    /// </summary>
    internal ImageSource? GetForCanvas(NestedFolder folder, int index)
    {
        var file = folder.Files[index];
        string? path = null;
        var key = TypeKeyOf(file);
        if (key is null)
        {
            path = folder.PathOf(file);
            key = KeyOf(path, isDirectory: false);
        }

        if (_cache.TryGetValue(key, out var cached) && !cached.Provisional)
        {
            return cached.Icon;
        }

        var letGo = Volatile.Read(ref _fileIconsLetGo);
        if (_canvasAsked.Count >= CanvasAskLimit || _canvasLetGoSeen != letGo)
        {
            // Too many to remember, or icons it asked for were let go of: the
            // canvas forgets what it asked, and asks again for what it draws.
            _canvasAsked.Clear();
            _canvasLetGoSeen = letGo;
        }

        if (_canvasAsked.Add(key) && AskForCanvas(key, path ?? folder.PathOf(file)) is { } answered)
        {
            return answered;
        }

        // Nothing yet, or what a prefetch found, until this file's own answer is in.
        return cached.Icon;
    }

    /// <summary>
    /// Queues the file types of a folder that was just read, behind
    /// everything on screen, so they are usually known before any of its
    /// names is big enough to carry an icon.  Extensions are shared strings,
    /// so the handful of types among fifty thousand files are told apart by
    /// reference; one path is made per type not known yet, the file the Shell
    /// is asked about.  On the thread that owns the tree.
    /// </summary>
    internal void Prefetch(NestedFolder folder)
    {
        var files = folder.Files;
        if (files.Count == 0 || _disposed)
        {
            return;
        }

        var firsts = _prefetchFirsts;
        string? last = null;
        for (var index = 0; index < files.Count; index++)
        {
            var extension = files[index].Extension;
            if (ReferenceEquals(extension, last) || firsts.ContainsKey(extension))
            {
                continue;
            }

            // A program is drawn by its path, and so is a name whose type the
            // Shell reads differently (.gitignore): neither speaks for a type.
            var key = TypeKeyOf(files[index]);
            if (key is not null)
            {
                firsts[key] = index;
            }

            if (key is not null || extension.Length > 0)
            {
                last = extension;
            }
        }

        if (firsts.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            var queued = false;
            foreach (var (key, index) in firsts)
            {
                if (_cache.ContainsKey(key) || _pending.ContainsKey(key))
                {
                    continue;
                }

                var entry = new PendingIcon(key, folder.PathOf(files[index]), isDirectory: false, visible: false);
                _pending[key] = entry;
                _prefetch.Enqueue(entry);
                queued = true;
            }

            if (queued)
            {
                WatchWhileWaiting();
                Monitor.Pulse(_gate);
            }
        }

        firsts.Clear();
    }

    /// <summary>
    /// Blocking resolution, for the handful of navigation-pane entries where the
    /// icon is part of the initial layout.
    /// </summary>
    public ImageSource? GetSmallIcon(string path, bool isDirectory)
        => _cache.GetOrAdd(KeyOf(path, isDirectory), _ => new CachedIcon(_extract(path, isDirectory), Provisional: false)).Icon;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Monitor.PulseAll(_gate);
        }

        _flushTimer.Dispose();
        _watchdog.Dispose();
    }

    /// <summary>
    /// Whether a file's icon is the file's own rather than its type's:
    /// programs, shortcuts, icon and cursor files, Internet shortcuts,
    /// management consoles and ClickOnce references - each of them asked
    /// about by its type would show the first one's picture for all.  With or
    /// without the dot, in any case - ".EXE" is a program too.  The only test
    /// of its kind - the GPU canvas's atlas asks it too - so the canvases'
    /// keys and the cache's never disagree.
    /// </summary>
    internal static bool IsPathSpecificIcon(ReadOnlySpan<char> extension)
    {
        if (extension.Length > 0 && extension[0] == '.')
        {
            extension = extension[1..];
        }

        if (extension.Length is < 3 or > 9)
        {
            return false;
        }

        foreach (var type in PathSpecificTypes)
        {
            if (extension.Equals(type, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The key an icon is cached under: one for every folder, the path for a
    /// file whose icon is its own, and otherwise the file's type - its
    /// extension, lower case and without the dot, or empty for none - the
    /// same string as <see cref="NestedFile.Extension"/>.
    /// </summary>
    internal static string KeyOf(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return FolderKey;
        }

        var extension = Path.GetExtension(path.AsSpan());
        if (IsPathSpecificIcon(extension))
        {
            return path;
        }

        if (extension.Length <= 1)
        {
            return string.Empty;
        }

        var type = extension[1..];
        Span<char> lower = type.Length <= 64 ? stackalloc char[type.Length] : new char[type.Length];
        type.ToLowerInvariant(lower);
        return new string(lower);
    }

    /// <summary>
    /// The key a file of the canvas is cached under when that is its type -
    /// its interned extension, so looking it up makes nothing - or null when
    /// it has to be keyed by its path: a program, shortcut or the like, or a
    /// name whose type the Shell reads from more than the tree keeps (one
    /// that starts with its only dot, or an extension past 32 characters).
    /// </summary>
    internal static string? TypeKeyOf(in NestedFile file)
    {
        var extension = file.Extension;
        if (extension.Length > 0)
        {
            return IsPathSpecificIcon(extension) ? null : extension;
        }

        return Path.GetExtension(file.Name.AsSpan()).IsEmpty ? string.Empty : null;
    }

    // ---- asking ----------------------------------------------------------------------

    /// <summary>
    /// The canvas asks for <paramref name="key"/>, about the file at
    /// <paramref name="path"/>: the icon when it has just become known,
    /// otherwise null and its answer is posted to <see cref="CanvasArrivals"/>.
    /// </summary>
    private ImageSource? AskForCanvas(string key, string path)
    {
        if (_disposed)
        {
            return null;
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached) && !cached.Provisional)
            {
                return cached.Icon;
            }

            AskVisible(key, path, isDirectory: false).ForCanvas = true;
            return null;
        }
    }

    /// <summary>
    /// Something on screen waits for <paramref name="key"/>: the entry that
    /// will answer it, on top of the stack - or, <paramref name="inOrder"/>,
    /// behind the rows asked for before it in the same run.  A type only a
    /// prefetch had asked for is asked about this file instead - the file on
    /// screen is the one that speaks for its type.  Inside the lock.
    /// </summary>
    private PendingIcon AskVisible(string key, string path, bool isDirectory, bool inOrder = false)
    {
        if (_pending.TryGetValue(key, out var entry))
        {
            if (entry.Visible)
            {
                return entry;
            }

            entry.Visible = true;
            entry.Path = path;
            entry.IsDirectory = isDirectory;
        }
        else
        {
            entry = new PendingIcon(key, path, isDirectory, visible: true);
            _pending[key] = entry;
        }

        if (inOrder && JoinRun() is { } run)
        {
            run.Enqueue(entry);
        }
        else
        {
            _visible.Push(new VisibleAsk(entry, null));
        }

        WatchWhileWaiting();
        Monitor.Pulse(_gate);
        return entry;
    }

    /// <summary>
    /// The run a row asked for now joins: the open one, or a new one on top
    /// of the stack that is closed once the dispatcher operation asking is
    /// over.  Null on a thread without a dispatcher, where nothing would
    /// close it: the row goes on the stack alone.  Inside the lock.
    /// </summary>
    private Queue<PendingIcon>? JoinRun()
    {
        if (_openRun is { } open)
        {
            return open;
        }

        var dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return null;
        }

        var run = new Queue<PendingIcon>();
        _openRun = run;
        _visible.Push(new VisibleAsk(null, run));
        dispatcher.BeginInvoke(DispatcherPriority.Send, () => CloseRun(run));
        return run;
    }

    /// <summary>The operation that asked for <paramref name="run"/>'s rows is over: the next rows asked for are a newer run.</summary>
    private void CloseRun(Queue<PendingIcon> run)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_openRun, run))
            {
                _openRun = null;
            }
        }
    }

    // ---- the worker ------------------------------------------------------------------

    private void StartWorker()
    {
        var worker = new IconWorker(new Thread(Work)
        {
            IsBackground = true,
            Name = _workers.Count == 0 ? "UltraExplorer Shell icons" : $"UltraExplorer Shell icons {_workers.Count + 1}",
            Priority = ThreadPriority.BelowNormal
        });
        worker.Thread.SetApartmentState(ApartmentState.STA);
        _workers.Add(worker);
        worker.Thread.Start(worker);
    }

    /// <summary>
    /// Work was queued: every worker stuck on one icon is made up for now,
    /// and the watchdog looks again in a second, in case they get stuck with
    /// nothing new asked.  Under the gate.
    /// </summary>
    private void WatchWhileWaiting()
    {
        ReplaceStuckWorkers();
        if (!_watchArmed && !_disposed)
        {
            _watchArmed = true;
            _watchdog.Change(WatchIntervalMilliseconds, Timeout.Infinite);
        }
    }

    /// <summary>The watchdog's round: while work waits, workers stuck on one icon are made up for, and it looks again.</summary>
    private void Watch()
    {
        lock (_gate)
        {
            _watchArmed = false;
            if (!_disposed && _visible.Count + _prefetch.Count > 0)
            {
                WatchWhileWaiting();
            }
        }
    }

    /// <summary>
    /// Starts one more worker when every one is stuck: busy on a single icon
    /// for longer than any icon should take.  The stuck ones finish or not in
    /// their own time.  Under the gate.
    /// </summary>
    private void ReplaceStuckWorkers()
    {
        if (_workers.Count >= MaximumWorkerCount)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        foreach (var worker in _workers)
        {
            var since = Volatile.Read(ref worker.BusySince);
            if (since == 0 || now - since < StuckTicks)
            {
                return;
            }
        }

        StartWorker();
    }

    private void Work(object? state)
    {
        var worker = (IconWorker)state!;
        while (TryTakeNext(worker, out var entry, out var path, out var isDirectory, out var visible))
        {
            // Known meanwhile, from GetSmallIcon: the Shell is not asked twice.
            if (!_cache.TryGetValue(entry.Key, out var known) || known.Provisional)
            {
                ImageSource? icon = null;
                try
                {
                    Interlocked.Increment(ref _extractions);
                    icon = _extract(path, isDirectory);
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ExternalException)
                {
                }
                finally
                {
                    Volatile.Write(ref worker.BusySince, 0);
                }

                Complete(entry, icon, visible);
            }
            else
            {
                Volatile.Write(ref worker.BusySince, 0);
                Complete(entry, known.Icon, visible);
            }
        }
    }

    /// <summary>
    /// The next entry to answer, with what to ask the Shell as it stands now:
    /// the newest on-screen one first, then the oldest prefetch.  An entry met
    /// in both lanes, or already answered, is skipped.  Marks
    /// <paramref name="worker"/> busy from now.  False once disposed.
    /// </summary>
    private bool TryTakeNext(IconWorker worker, out PendingIcon entry, out string path, out bool isDirectory, out bool visible)
    {
        lock (_gate)
        {
            while (!_disposed)
            {
                if (TryTakeVisible(out entry!) || _prefetch.TryDequeue(out entry!))
                {
                    if (!_pending.TryGetValue(entry.Key, out var current) || !ReferenceEquals(current, entry))
                    {
                        continue;
                    }

                    path = entry.Path;
                    isDirectory = entry.IsDirectory;
                    visible = entry.Visible;
                    Volatile.Write(ref worker.BusySince, Stopwatch.GetTimestamp());
                    return true;
                }

                Monitor.Wait(_gate);
            }
        }

        entry = null!;
        path = string.Empty;
        isDirectory = false;
        visible = false;
        return false;
    }

    /// <summary>
    /// The newest on-screen entry: the top of the stack, or the first row of
    /// the run on top, which leaves the stack with its last row.  Inside the lock.
    /// </summary>
    private bool TryTakeVisible(out PendingIcon entry)
    {
        while (_visible.TryPeek(out var top))
        {
            if (top.Run is not { } run)
            {
                _visible.Pop();
                entry = top.Entry!;
                return true;
            }

            var taken = run.TryDequeue(out entry!);
            if (run.Count == 0)
            {
                _visible.Pop();
                if (ReferenceEquals(run, _openRun))
                {
                    _openRun = null;
                }
            }

            if (taken)
            {
                return true;
            }
        }

        entry = null!;
        return false;
    }

    /// <summary>
    /// The Shell answered <paramref name="entry"/>, asked as it stood when it
    /// was taken (<paramref name="visible"/>): into the cache, and to whoever
    /// waits.  A prefetch's answer for a type something on screen asked for
    /// while the Shell was busy with it only holds the type's place: the
    /// entry stays, and is asked again about the file on screen.
    /// </summary>
    private void Complete(PendingIcon entry, ImageSource? icon, bool visible)
    {
        List<Action<ImageSource?>>? callbacks;
        bool tellCanvas;
        lock (_gate)
        {
            if (!_pending.TryGetValue(entry.Key, out var current) || !ReferenceEquals(current, entry))
            {
                return;
            }

            if (entry.Visible != visible)
            {
                _cache.TryAdd(entry.Key, new CachedIcon(icon, Provisional: true));
                return;
            }

            _pending.Remove(entry.Key);
            var had = _cache.TryGetValue(entry.Key, out var previous);
            var settled = visible;
            if (had && !previous.Provisional)
            {
                // Someone else's answer came first (GetSmallIcon): it stays.
                icon = previous.Icon;
                settled = true;
            }
            else if (had && SamePixels(previous.Icon, icon))
            {
                // The file on screen looks as the prefetched one did: the icon
                // already drawn stays, so nothing needs drawing again.
                icon = previous.Icon;
            }

            if (!had && IsPathSpecificIcon(Path.GetExtension(entry.Key.AsSpan())) && ++_fileIcons > FileIconLimit)
            {
                LetFileIconsGo();
                _fileIcons = 1;
            }

            _cache[entry.Key] = new CachedIcon(icon, Provisional: !settled);
            callbacks = entry.Callbacks;
            tellCanvas = entry.ForCanvas && (!had || !ReferenceEquals(previous.Icon, icon));
        }

        if (tellCanvas)
        {
            Interlocked.Increment(ref _canvasArrivalsPosted);
            var arrival = new IconArrival(entry.Key, icon);
            foreach (var inbox in Volatile.Read(ref _canvasInboxes))
            {
                inbox.Post(arrival);
            }
        }

        if (callbacks is not null)
        {
            _answers.Enqueue(new Answer(callbacks, icon));
            ScheduleFlush();
        }
    }

    /// <summary>
    /// Lets go of every icon of a single file in the cache, once there are
    /// more than <see cref="FileIconLimit"/>: each is asked about again when
    /// it is shown again, and the canvas forgets having asked
    /// (<see cref="_fileIconsLetGo"/>).  Whoever was given one keeps it.
    /// Under the gate.
    /// </summary>
    private void LetFileIconsGo()
    {
        foreach (var (key, _) in _cache)
        {
            if (IsPathSpecificIcon(Path.GetExtension(key.AsSpan())))
            {
                _cache.TryRemove(key, out _);
            }
        }

        Interlocked.Increment(ref _fileIconsLetGo);
    }

    // ---- the callbacks' batches -----------------------------------------------------

    /// <summary>
    /// Makes sure a batch of callbacks runs soon: at once when the last one
    /// ran long enough ago, otherwise when sixteen milliseconds have passed
    /// since it did.  Any thread; one batch is scheduled at a time.
    /// </summary>
    private void ScheduleFlush()
    {
        if (Interlocked.Exchange(ref _flushScheduled, 1) != 0)
        {
            return;
        }

        var last = Interlocked.Read(ref _lastFlush);
        var wait = last == 0 ? 0 : CallbackIntervalMilliseconds - Stopwatch.GetElapsedTime(last).TotalMilliseconds;
        if (wait > 0)
        {
            try
            {
                _flushTimer.Change((int)Math.Ceiling(wait), Timeout.Infinite);
                return;
            }
            catch (ObjectDisposedException)
            {
                // Disposed meanwhile: posted now instead, or dropped below.
            }
        }

        PostFlush();
    }

    private void PostFlush()
    {
        var dispatcher = _callbackDispatcher ?? Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            // Nobody to call back on - no application, or it is closing: the
            // answers are only let go of; they are in the cache for the next ask.
            Volatile.Write(ref _flushScheduled, 0);
            _answers.Clear();
            return;
        }

        Interlocked.Increment(ref _callbackBatches);
        dispatcher.BeginInvoke(DispatcherPriority.Background, _flush);
    }

    /// <summary>
    /// A batch, on the UI thread: every answer waiting is handed to its
    /// callbacks.  The next batch may be scheduled from the moment this one
    /// starts - a full fence, so an answer that found a batch already
    /// scheduled is always one this batch sees.
    /// </summary>
    private void FlushAnswers()
    {
        Interlocked.Exchange(ref _lastFlush, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _flushScheduled, 0);
        while (_answers.TryDequeue(out var answer))
        {
            foreach (var completed in answer.Waiting)
            {
                completed(answer.Icon);
            }
        }
    }

    // ---- the Shell -------------------------------------------------------------------

    /// <summary>Whether two icons show the same picture: the same object, both none, or the same pixels.  Any thread; they are frozen.</summary>
    private static bool SamePixels(ImageSource? first, ImageSource? second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        if (first is not BitmapSource a || second is not BitmapSource b
            || a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight || a.Format != b.Format
            || a.Format.BitsPerPixel % 8 != 0)
        {
            return false;
        }

        var stride = a.PixelWidth * a.Format.BitsPerPixel / 8;
        var left = new byte[stride * a.PixelHeight];
        var right = new byte[left.Length];
        a.CopyPixels(left, stride, 0);
        b.CopyPixels(right, stride, 0);
        return left.AsSpan().SequenceEqual(right);
    }

    private static ImageSource? ExtractIcon(string path, bool isDirectory)
    {
        var attributes = isDirectory ? FileAttributes.Directory : FileAttributes.Normal;
        var flags = Shgfi.Icon | Shgfi.SmallIcon | Shgfi.UseFileAttributes;

        var result = SHGetFileInfo(path, (uint)attributes, out var info, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
        if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.IconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            // Frozen so the UI thread can bind to an icon decoded off-thread.
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.IconHandle);
        }
    }

    /// <summary>
    /// An icon in the cache.  <see cref="Provisional"/> marks a type's icon
    /// that a prefetch found, which shows until a file of the type on screen
    /// has been asked about.
    /// </summary>
    private readonly record struct CachedIcon(ImageSource? Icon, bool Provisional);

    /// <summary>Callbacks waiting for one icon, and the icon, for the next batch.</summary>
    private readonly record struct Answer(List<Action<ImageSource?>> Waiting, ImageSource? Icon);

    /// <summary>
    /// One icon asked for and not answered yet: which file the Shell is to be
    /// asked about, whether something on screen waits for it (else it is a
    /// prefetch), and who is to be told.  Guarded by the service's lock.
    /// </summary>
    private sealed class PendingIcon(string key, string path, bool isDirectory, bool visible)
    {
        public readonly string Key = key;
        public string Path = path;
        public bool IsDirectory = isDirectory;
        public bool Visible = visible;
        public bool ForCanvas;
        public List<Action<ImageSource?>>? Callbacks;
    }

    /// <summary>One item of the on-screen stack: an icon asked for alone, or a run of rows answered in the order they were asked.</summary>
    private readonly record struct VisibleAsk(PendingIcon? Entry, Queue<PendingIcon>? Run);

    /// <summary>A thread asking the Shell, and since when it has been on its current icon (Stopwatch ticks), or zero while it waits for one.</summary>
    private sealed class IconWorker(Thread thread)
    {
        public readonly Thread Thread = thread;
        public long BusySince;
    }

    [Flags]
    private enum Shgfi : uint
    {
        Icon = 0x000000100,
        SmallIcon = 0x000000001,
        UseFileAttributes = 0x000000010
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        out ShFileInfo fileInfo,
        uint fileInfoSize,
        Shgfi flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);
}
