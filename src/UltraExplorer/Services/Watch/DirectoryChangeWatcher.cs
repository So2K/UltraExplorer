using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UltraExplorer.Services.Watch;

/// <summary>
/// One record of a watch's buffer as the parser hands it on.  A view into the
/// buffer, valid only during the call it is passed to.
/// </summary>
internal readonly ref struct ChangeRecord
{
    public ChangeRecord(int action, ReadOnlySpan<char> name)
    {
        Action = action;
        Name = name;
    }

    public ChangeRecord(int action, ReadOnlySpan<char> name, long size, long lastWriteTicks, uint attributes)
    {
        Action = action;
        Name = name;
        HasDetails = true;
        Size = size;
        LastWriteTicks = lastWriteTicks;
        Attributes = attributes;
    }

    /// <summary>What happened: one of the <c>WatchNative.Action…</c> values.</summary>
    public int Action { get; }

    /// <summary>The entry's path relative to the watched directory: Users\User\notes.txt.</summary>
    public ReadOnlySpan<char> Name { get; }

    /// <summary>Whether the watch said what the entry is (ReadDirectoryChangesExW's extended records).</summary>
    public bool HasDetails { get; }

    public long Size { get; }

    /// <summary>The entry's last write time as UTC <see cref="DateTime"/> ticks; zero when not given.</summary>
    public long LastWriteTicks { get; }

    public uint Attributes { get; }

    /// <summary>Whether the entry is known to be a folder: only when the watch said so.</summary>
    public bool IsDirectory => HasDetails && (Attributes & WatchNative.FileAttributeDirectory) != 0;
}

/// <summary>What a watcher tells about its directory: each record, the end of each buffer of them, an overflow, and the end of the watch.</summary>
internal interface IChangeRecordSink
{
    void Record(in ChangeRecord record);

    /// <summary>The last record of a buffer was handed on: a rename's first half never pairs across buffers.</summary>
    void EndOfBuffer();

    /// <summary>More changed than the buffer could hold, and those changes are lost: every folder under the directory may be out of date.</summary>
    void Overflowed(DirectoryChangeWatcher watcher);

    /// <summary>The watch ended with <paramref name="error"/> and its handle is closed; nothing more will be told.</summary>
    void Failed(DirectoryChangeWatcher watcher, int error);
}

/// <summary>
/// One overlapped directory handle asking Windows for every change under it,
/// recursively, through <c>ReadDirectoryChangesExW</c> - extended records,
/// which say whether an entry is a folder and give its size and date, where
/// the file system has them (NTFS, ReFS) - or <c>ReadDirectoryChangesW</c>.
///
/// <para>Written for a whole volume's worth of noise at next to no cost: the
/// handle is bound to the thread pool's completion port with one
/// <see cref="PreAllocatedOverlapped"/>, so a completion allocates nothing;
/// the records are copied out of the native buffer, the next read is asked
/// for - so Windows is never kept waiting on the parse - and only then is the
/// copy parsed, straight into the <see cref="IChangeRecordSink"/>, record by
/// record as spans of the copy.  <see cref="FileSystemWatcher"/>, which this
/// replaces, runs its handlers before it asks again, allocates two or three
/// objects per change, and cannot close its handle when Windows asks to
/// remove the device.</para>
///
/// <para>The next read is asked for at once while changes are sparse.  While
/// they stream - a completion within <see cref="StreamingGapMilliseconds"/> of
/// the last - it is asked for <see cref="GatherMilliseconds"/> later instead,
/// and Windows gathers what happens meanwhile into its own buffer, which the
/// next read then returns whole.  A read asked for at once completes on the
/// very next change, so a stream of twenty thousand changes a second would be
/// twenty thousand completions, each waking a pool thread: most of a core,
/// measured, against well under one per cent gathered.  The wait adds a few
/// milliseconds to changes that are anyway merged over 150 ms, and a local
/// volume's 256 KB holds thousands of records.</para>
///
/// <para>Completions are handled one at a time (a later one waits for the
/// earlier parse), so the sink sees one buffer after another, never two at
/// once.  <see cref="Stop"/> closes the handle before it returns, which is
/// what lets a volume go; the native memory is given back when the
/// cancelled read's completion arrives.</para>
/// </summary>
internal sealed unsafe class DirectoryChangeWatcher
{
    /// <summary>A completion this soon after the last means changes are streaming in.</summary>
    internal const double StreamingGapMilliseconds = 20;

    /// <summary>While they stream, how long Windows gathers them before the next read takes them.</summary>
    internal const double GatherMilliseconds = 10;

    private static readonly IOCompletionCallback Completion = OnCompletion;
    private static readonly long StreamingGapTicks = (long)(StreamingGapMilliseconds * Stopwatch.Frequency / 1000);

    private readonly Lock _gate = new();
    private readonly SafeFileHandle _handle;
    private readonly ThreadPoolBoundHandle _bound;
    private readonly PreAllocatedOverlapped _overlapped;
    private readonly IChangeRecordSink _sink;
    private readonly byte* _buffer;
    private readonly byte[] _copy;
    private readonly int _size;
    private NativeOverlapped* _outstanding;
    private Timer? _gather;
    private long _lastCompletion;
    private bool _gathering;
    private bool _details;
    private bool _stopping;
    private bool _released;
    private int _endedWith;

    private DirectoryChangeWatcher(SafeFileHandle handle, ThreadPoolBoundHandle bound, int size, bool details, IChangeRecordSink sink)
    {
        _handle = handle;
        _bound = bound;
        _size = size;
        _details = details;
        _sink = sink;
        _buffer = (byte*)NativeMemory.AlignedAlloc((nuint)size, 8);
        _copy = new byte[size];
        _overlapped = PreAllocatedOverlapped.UnsafeCreate(Completion, this, null);
    }

    /// <summary>The directory handle, closed once the watch stops.</summary>
    public SafeFileHandle Handle => _handle;

    /// <summary>Whether the records carry details (extended information).</summary>
    public bool HasDetails => Volatile.Read(ref _details);

    public int BufferBytes => _size;

    /// <summary>
    /// Whether the watch has ended - stopped, or failed on its own - so that
    /// nothing more will be heard from it.  A watch can fail between
    /// <see cref="Open"/> returning it and its owner taking it in, when
    /// whoever the failure is told to does not know it yet: the owner asks
    /// this, under its own lock, before it counts the watch as running.
    /// </summary>
    public bool HasEnded
    {
        get
        {
            lock (_gate)
            {
                return _stopping;
            }
        }
    }

    /// <summary>The Windows error the watch failed with on its own; zero while it runs, or when it was stopped.</summary>
    public int EndedWith
    {
        get
        {
            lock (_gate)
            {
                return _endedWith;
            }
        }
    }

    /// <summary>
    /// While set and not signalled, a completion waits before it takes its
    /// records - so a test can let changes pile up past the buffer and see an
    /// overflow for certain.
    /// </summary>
    public ManualResetEventSlim? Hold { get; set; }

    // Counters for tests and the bench, written under the gate.
    public long Completions { get; private set; }

    /// <summary>Reads asked for <see cref="GatherMilliseconds"/> late because changes were streaming.</summary>
    public long Gathered { get; private set; }
    public long RecordCount { get; private set; }
    public long Overflows { get; private set; }

    /// <summary>Stopwatch ticks spent taking completions in: copying, asking again, parsing and everything the sink did.</summary>
    public long WorkTicks { get; private set; }

    /// <summary>Of <see cref="WorkTicks"/>, the ticks spent parsing and in the sink: what each record costs.</summary>
    public long ParseTicks { get; private set; }

    /// <summary>Bytes the completions allocated on the threads they ran on.</summary>
    public long AllocatedBytes { get; private set; }

    /// <summary>
    /// Opens <paramref name="directory"/> and asks for its first changes.
    /// Returns zero with the running watcher, or the Windows error it failed
    /// with.  Details are asked for when <paramref name="allowDetails"/> and
    /// the volume is NTFS or ReFS; a volume that refuses them is asked for
    /// plain records instead.  May block for as long as opening the directory
    /// does - on a share that has gone away, a long time.
    /// </summary>
    public static int Open(string directory, int bufferBytes, bool allowDetails, IChangeRecordSink sink, out DirectoryChangeWatcher? watcher)
    {
        watcher = null;
        var handle = WatchNative.CreateFileW(
            directory,
            WatchNative.FileListDirectory,
            WatchNative.FileShareAll,
            IntPtr.Zero,
            WatchNative.OpenExisting,
            WatchNative.FileFlagBackupSemantics | WatchNative.FileFlagOverlapped,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return error == 0 ? WatchNative.ErrorInvalidFunction : error;
        }

        ThreadPoolBoundHandle bound;
        try
        {
            bound = ThreadPoolBoundHandle.BindHandle(handle);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            handle.Dispose();
            return WatchNative.ErrorInvalidFunction;
        }

        var size = Math.Max(1024, (bufferBytes + 7) & ~7);
        var created = new DirectoryChangeWatcher(handle, bound, size, allowDetails && KeepsDetails(handle), sink);
        int failure;
        lock (created._gate)
        {
            failure = created.Issue();
            if (failure != 0 && created._details && WatchNative.IsRefusal(failure))
            {
                created._details = false;
                failure = created.Issue();
            }

            if (failure != 0)
            {
                created._stopping = true;
                created._handle.Dispose();
                created.Release();
            }
        }

        if (failure != 0)
        {
            return failure;
        }

        watcher = created;
        return 0;
    }

    /// <summary>
    /// Ends the watch: the read under way is cancelled and the handle closed
    /// before this returns.  Nothing more reaches the sink.  Any thread, any
    /// number of times; waits for a parse in progress to finish.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
            if (_outstanding != null)
            {
                WatchNative.CancelIoEx(_handle, _outstanding);
            }

            _handle.Dispose();
            if (_outstanding == null)
            {
                Release();
            }
        }
    }

    /// <summary>
    /// Hands every record of <paramref name="buffer"/> - as Windows fills it,
    /// extended or plain - to <paramref name="sink"/>, and returns how many
    /// there were.  Stops at the first record that does not fit, rather than
    /// reading past the buffer.
    /// </summary>
    internal static int Parse(ReadOnlySpan<byte> buffer, bool details, IChangeRecordSink sink)
    {
        var nameLengthAt = details ? WatchNative.ExtendedNameLengthOffset : WatchNative.PlainNameLengthOffset;
        var nameAt = details ? WatchNative.ExtendedNameOffset : WatchNative.PlainNameOffset;
        var count = 0;
        var offset = 0;
        while (offset >= 0 && offset + nameAt <= buffer.Length)
        {
            var record = buffer[offset..];
            var next = BinaryPrimitives.ReadInt32LittleEndian(record);
            var action = BinaryPrimitives.ReadInt32LittleEndian(record[4..]);
            var nameBytes = BinaryPrimitives.ReadInt32LittleEndian(record[nameLengthAt..]);
            if (nameBytes < 0 || nameAt + nameBytes > record.Length)
            {
                break;
            }

            var name = MemoryMarshal.Cast<byte, char>(record.Slice(nameAt, nameBytes & ~1));
            if (details)
            {
                var fileTime = BinaryPrimitives.ReadInt64LittleEndian(record[WatchNative.ExtendedLastWriteOffset..]);
                sink.Record(new ChangeRecord(
                    action,
                    name,
                    BinaryPrimitives.ReadInt64LittleEndian(record[WatchNative.ExtendedSizeOffset..]),
                    fileTime > 0 ? fileTime + WatchNative.FileTimeToDateTimeTicks : 0,
                    BinaryPrimitives.ReadUInt32LittleEndian(record[WatchNative.ExtendedAttributesOffset..])));
            }
            else
            {
                sink.Record(new ChangeRecord(action, name));
            }

            count++;
            if (next <= 0)
            {
                break;
            }

            offset += next;
        }

        return count;
    }

    /// <summary>Whether the volume keeps what extended records report: NTFS and ReFS do.</summary>
    private static bool KeepsDetails(SafeFileHandle handle)
    {
        var name = stackalloc char[64];
        if (WatchNative.GetVolumeInformationByHandleW(handle, null, 0, null, null, null, name, 64) == 0)
        {
            return false;
        }

        var fileSystem = new ReadOnlySpan<char>(name, 64);
        fileSystem = fileSystem[..Math.Max(0, fileSystem.IndexOf('\0'))];
        return fileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || fileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase);
    }

    private static void OnCompletion(uint errorCode, uint byteCount, NativeOverlapped* overlapped)
    {
        var watcher = (DirectoryChangeWatcher)ThreadPoolBoundHandle.GetNativeOverlappedState(overlapped)!;
        watcher.Complete((int)errorCode, byteCount, overlapped);
    }

    private void Complete(int error, uint byteCount, NativeOverlapped* overlapped)
    {
        Hold?.Wait();
        var start = Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var overflowed = false;
        var failure = 0;
        lock (_gate)
        {
            _bound.FreeNativeOverlapped(overlapped);
            _outstanding = null;
            if (_stopping)
            {
                Release();
                return;
            }

            Completions++;
            var streaming = start - _lastCompletion < StreamingGapTicks;
            _lastCompletion = start;
            if (error == 0 && byteCount > 0)
            {
                var length = (int)Math.Min(byteCount, (uint)_size);
                new ReadOnlySpan<byte>(_buffer, length).CopyTo(_copy);
                if (streaming)
                {
                    Gather();
                }
                else
                {
                    failure = Issue();
                }

                var parsing = Stopwatch.GetTimestamp();
                RecordCount += Parse(_copy.AsSpan(0, length), _details, _sink);
                _sink.EndOfBuffer();
                ParseTicks += Stopwatch.GetTimestamp() - parsing;
            }
            else if (error is 0 or WatchNative.ErrorNotifyEnumDir)
            {
                // Success with nothing in the buffer is how Windows says the
                // changes outgrew it; they are gone, and a fresh read begins.
                // One overflow on the heels of another - a stream past the
                // buffer, or a share that answers every read so - is asked
                // again as a stream is, a moment later, rather than at once
                // round and round.
                overflowed = true;
                Overflows++;
                if (streaming)
                {
                    Gather();
                }
                else
                {
                    failure = Issue();
                }
            }
            else if (_details && WatchNative.IsRefusal(error))
            {
                _details = false;
                failure = Issue();
            }
            else
            {
                failure = error;
            }

            if (failure != 0)
            {
                _stopping = true;
                _endedWith = failure;
                _handle.Dispose();
                if (_outstanding == null)
                {
                    Release();
                }
            }

            WorkTicks += Stopwatch.GetTimestamp() - start;
            AllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
        }

        if (overflowed)
        {
            _sink.Overflowed(this);
        }

        if (failure != 0)
        {
            _sink.Failed(this, failure);
        }
    }

    /// <summary>Asks for the next changes <see cref="GatherMilliseconds"/> from now, letting Windows gather them meanwhile.  Under the gate.</summary>
    private void Gather()
    {
        Gathered++;
        _gathering = true;
        _gather ??= new Timer(static state => ((DirectoryChangeWatcher)state!).IssueGathered(), this, Timeout.Infinite, Timeout.Infinite);
        _gather.Change(TimeSpan.FromMilliseconds(GatherMilliseconds), Timeout.InfiniteTimeSpan);
    }

    /// <summary>The gathering wait is over: asks for what Windows gathered.</summary>
    private void IssueGathered()
    {
        int failure;
        lock (_gate)
        {
            if (_stopping || !_gathering)
            {
                return;
            }

            _gathering = false;
            failure = Issue();
            if (failure != 0)
            {
                _stopping = true;
                _endedWith = failure;
                _handle.Dispose();
                Release();
            }
        }

        if (failure != 0)
        {
            _sink.Failed(this, failure);
        }
    }

    /// <summary>Asks for the next changes; zero, or the error the request failed with.  Under the gate.</summary>
    private int Issue()
    {
        var overlapped = _bound.AllocateNativeOverlapped(_overlapped);
        var asked = _details
            ? WatchNative.ReadDirectoryChangesExW(_handle, _buffer, (uint)_size, 1, WatchNative.NotifyFilter, null, overlapped, IntPtr.Zero, WatchNative.ReadDirectoryNotifyExtendedInformation)
            : WatchNative.ReadDirectoryChangesW(_handle, _buffer, (uint)_size, 1, WatchNative.NotifyFilter, null, overlapped, IntPtr.Zero);
        if (asked == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != WatchNative.ErrorIoPending)
            {
                _bound.FreeNativeOverlapped(overlapped);
                return error == 0 ? WatchNative.ErrorInvalidFunction : error;
            }
        }

        _outstanding = overlapped;
        return 0;
    }

    /// <summary>Gives back the native memory and the binding, once no read can still write into them.  Under the gate.</summary>
    private void Release()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        _gathering = false;
        _gather?.Dispose();
        _overlapped.Dispose();
        _bound.Dispose();
        NativeMemory.AlignedFree(_buffer);
    }
}
