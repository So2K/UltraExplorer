namespace UltraExplorer.Services.Watch;

/// <summary>What happened in a folder, as far as the watch could tell.</summary>
[Flags]
public enum ChangeKinds : byte
{
    /// <summary>Something in it was added, removed or renamed: its listing is different now.</summary>
    Structural = 1,

    /// <summary>A file in it changed size, date or attributes; the names are the same.</summary>
    Content = 2,

    /// <summary>
    /// A sub-folder's own date moved on - which is what writing anything
    /// inside that sub-folder does to it.  Only a listing sorted by date shows
    /// it, so a consumer may ignore it otherwise.  Told apart from
    /// <see cref="Content"/> only where the watch says whether an entry is a
    /// folder (NTFS and ReFS); elsewhere it arrives as <see cref="Content"/>.
    /// </summary>
    DirDate = 4,

    /// <summary>The folder itself was removed or renamed away: the change is keyed by its own path, not its parent's.</summary>
    Gone = 8
}

/// <summary>Which of the views a registration and a change are for.  Each is told separately and none waits for another.</summary>
public enum ChangeConsumer : byte
{
    /// <summary>The nested canvas's tree: one registration per loaded folder.</summary>
    Nested,

    /// <summary>The folder list: the folder it shows.</summary>
    List,

    /// <summary>The tree canvas: its expanded nodes.</summary>
    Graph
}

/// <summary>A sub-folder or file renamed within one folder: both names, without the folder's path.</summary>
public readonly record struct RenamePair(string OldName, string NewName);

/// <summary>
/// A file whose size or date changed, as the watch reported it (NTFS and ReFS
/// report both with every change): enough to update a listing in place
/// without reading the folder again.  <see cref="ModifiedTicks"/> is UTC
/// <see cref="DateTime"/> ticks, as the readers give them.
/// </summary>
public readonly record struct FileDelta(string Name, long Length, long ModifiedTicks)
{
    /// <summary>
    /// The file's attributes after the change.  A file that was hidden or
    /// system is never listed - its change may have been to those very
    /// attributes - but one that has just stopped being hidden cannot be told
    /// from one that never was: a consumer that shows hidden files differently
    /// should compare and read the folder when they no longer agree.
    /// </summary>
    public FileAttributes Attributes { get; init; }
}

/// <summary>
/// The changes in one folder that fell due together: every event heard in it
/// since the last time it was handed on, merged into one.
///
/// <para><see cref="Renames"/> and <see cref="Files"/> are the consumer's to
/// keep - they are not reused.  <see cref="Files"/> is complete or empty: it
/// lists every file whose size or date changed when the watch named them all
/// and there were at most <see cref="ChangeHub.MaximumDetails"/> of them,
/// none hidden or system (whose attributes may have changed what is shown);
/// empty means the folder has to be read to know what changed.  Renames beyond
/// the first <see cref="ChangeHub.MaximumDetails"/> are not listed - the
/// renamed entries are then simply new ones to whoever reads the folder.</para>
/// </summary>
public readonly struct FolderChange(string key, ChangeKinds kinds, long firstTicks, ReadOnlyMemory<RenamePair> renames, ReadOnlyMemory<FileDelta> files)
{
    /// <summary>
    /// The folder's path exactly as it was registered - for a nested folder,
    /// its <c>FullPath</c>.
    /// </summary>
    public string Key { get; } = key;

    public ChangeKinds Kinds { get; } = kinds;

    /// <summary>
    /// When the first of the merged events was heard, on the hub's
    /// <see cref="TimeProvider"/>'s timestamp clock - the
    /// <see cref="System.Diagnostics.Stopwatch"/>'s, unless a test gave the
    /// hub another - so how long a change took to reach the screen can be measured.
    /// </summary>
    public long FirstTicks { get; } = firstTicks;

    /// <summary>Sub-folders and files renamed within the folder, oldest first.</summary>
    public ReadOnlyMemory<RenamePair> Renames { get; } = renames;

    /// <summary>Files whose size or date changed; see the type's remarks for when it is empty.</summary>
    public ReadOnlyMemory<FileDelta> Files { get; } = files;
}

/// <summary>
/// Where the change hub hands what fell due: called on the thread that drains
/// the hub - the UI thread, a frame at a time - never on a watcher's.
/// </summary>
public interface IChangeSink
{
    /// <summary>
    /// Changes in a folder that <paramref name="target"/> registered for
    /// through <paramref name="consumer"/>.  One call per registration: a folder
    /// both the canvas and the list show is told twice, once each.
    /// </summary>
    void FolderChanged(ChangeConsumer consumer, object target, in FolderChange change);

    /// <summary>
    /// Changes under <paramref name="root"/> may have been missed - its watch
    /// overflowed, or was armed again after a gap - and
    /// <see cref="WatchRoot.Epoch"/> has moved on.  Every folder read under the
    /// old epoch now needs reading again, and will be when it is next drawn.
    /// </summary>
    void EpochBumped(WatchRoot root);

    /// <summary>
    /// Time to look at <paramref name="root"/>'s drawn folders for changes: its
    /// volume refuses a watch, or its watch is down and being retried.  Every
    /// five seconds, and when <see cref="ChangeHub.PollNow"/> is called.
    /// </summary>
    void PollDue(WatchRoot root);
}
