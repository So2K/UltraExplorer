using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UltraExplorer.Services.Search;

/// <summary>One file or folder Everything found, as its reply names it.</summary>
/// <param name="Name">The entry's own name.</param>
/// <param name="Directory">The folder it is in: C:\Users\Me\Downloads.</param>
/// <param name="IsFolder">Whether it is a folder (or a drive).</param>
/// <param name="Size">Its size in bytes, or -1 when Everything does not know it (a folder, without folder sizes indexed).</param>
/// <param name="Modified">Its last write time, local; null when not known.</param>
/// <param name="Highlighted">The name with the parts the search matched between asterisks: <c>*fSpy*-Blender.zip</c>.  Empty when not given.</param>
internal readonly record struct EverythingItem(
    string Name,
    string Directory,
    bool IsFolder,
    long Size,
    DateTime? Modified,
    string Highlighted);

/// <summary>What one query to Everything answered: the items it sent, and how many matched in all.</summary>
internal sealed record EverythingPage(long Total, IReadOnlyList<EverythingItem> Items);

/// <summary>
/// Talks to Everything (voidtools) directly, the way its SDK library does -
/// over the window messages it documents in <c>everything_ipc.h</c> - so
/// nothing but Everything itself has to be installed: no
/// <c>Everything64.dll</c> beside this program.
///
/// <para><b>How a query goes.</b>  Everything's window
/// (<c>EVERYTHING_TASKBAR_NOTIFICATION</c>) is sent a <c>WM_COPYDATA</c>
/// holding an <c>EVERYTHING_IPC_QUERY2</c> and the search; it answers later
/// with a <c>WM_COPYDATA</c> of its own, an <c>EVERYTHING_IPC_LIST2</c>, sent
/// to the reply window the query named and tagged with the number the query
/// chose.  The reply window here is a message-only window on a thread of its
/// own that does nothing but pump, so a reply never waits for the UI thread,
/// and any number of queries can be out at once, each told apart by its
/// number.</para>
///
/// <para>One per process (<see cref="Shared"/>): the thread and its window
/// are made on the first query and kept.  Everything not running is an
/// ordinary answer - null - never an exception.</para>
/// </summary>
internal sealed class EverythingClient
{
    private const uint WmCopyData = 0x004A;
    private const uint WmUser = 0x0400;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;

    /// <summary><c>EVERYTHING_IPC_COPYDATA_QUERY2W</c>: a Unicode query with the reply as a list of requested columns.</summary>
    private const nint CopyDataQuery2 = 18;

    // EVERYTHING_WM_IPC commands, sent as WM_USER with the command in wParam.
    private const nint IpcGetMajorVersion = 0;
    private const nint IpcGetMinorVersion = 1;
    private const nint IpcIsDatabaseLoaded = 401;

    // EVERYTHING_IPC_QUERY2_REQUEST_*: the columns a reply carries, in this
    // order, each present when its bit is set in the reply's request flags.
    internal const uint RequestName = 0x0001;
    internal const uint RequestPath = 0x0002;
    internal const uint RequestFullPath = 0x0004;
    internal const uint RequestExtension = 0x0008;
    internal const uint RequestSize = 0x0010;
    internal const uint RequestDateCreated = 0x0020;
    internal const uint RequestDateModified = 0x0040;
    internal const uint RequestDateAccessed = 0x0080;
    internal const uint RequestAttributes = 0x0100;
    internal const uint RequestFileListName = 0x0200;
    internal const uint RequestRunCount = 0x0400;
    internal const uint RequestDateRun = 0x0800;
    internal const uint RequestDateRecentlyChanged = 0x1000;
    internal const uint RequestHighlightedName = 0x2000;
    internal const uint RequestHighlightedPath = 0x4000;
    internal const uint RequestHighlightedFullPath = 0x8000;

    /// <summary>What every query here asks for.</summary>
    internal const uint Requested = RequestName | RequestPath | RequestSize | RequestDateModified | RequestHighlightedName;

    /// <summary><c>EVERYTHING_IPC_FOLDER</c> and <c>EVERYTHING_IPC_DRIVE</c>, an item's flags.</summary>
    private const uint ItemFolder = 0x1;
    private const uint ItemDrive = 0x2;

    /// <summary><c>EVERYTHING_IPC_SORT_NAME_ASCENDING</c>: the one order Everything never has to work for.</summary>
    internal const uint SortNameAscending = 1;

    /// <summary><c>EVERYTHING_IPC_SORT_DATE_MODIFIED_DESCENDING</c>: the newest first.</summary>
    internal const uint SortNewestFirst = 14;

    /// <summary>How long a reply may take before the query counts as unanswered.</summary>
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The window classes of Everything 1.4 and of 1.5's default instance.</summary>
    private static readonly string[] WindowClasses = ["EVERYTHING_TASKBAR_NOTIFICATION", "EVERYTHING_TASKBAR_NOTIFICATION_(1.5a)"];

    private static readonly Lazy<EverythingClient> Instance = new(() => new EverythingClient());

    private readonly ConcurrentDictionary<uint, TaskCompletionSource<byte[]>> _pending = new();

    /// <summary>
    /// One query out at a time: Everything answers only the latest query of
    /// a reply window and drops the one before, so a second sent before the
    /// first is answered would leave the first waiting for nothing.
    /// </summary>
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly Lock _gate = new();
    private Thread? _thread;
    private nint _window;
    private int _nextReply = 0x55450000;
    private int _startAttempted;

    private EverythingClient()
    {
    }

    /// <summary>The one client of this process.</summary>
    public static EverythingClient Shared => Instance.Value;

    /// <summary>Whether Everything's window is there to be asked: it is running.</summary>
    public static bool IsRunning => FindEverythingWindow() != 0;

    /// <summary>
    /// Whether Everything is running and has its index in memory; a query
    /// before then finds nothing, or only part of what is there.
    /// </summary>
    public static bool IsDatabaseLoaded
    {
        get
        {
            var window = FindEverythingWindow();
            return window != 0 && Ask(window, IpcIsDatabaseLoaded) is > 0;
        }
    }

    /// <summary>The running Everything's version, "1.4"; null when it is not running.</summary>
    public static string? Version
    {
        get
        {
            var window = FindEverythingWindow();
            if (window == 0 || Ask(window, IpcGetMajorVersion) is not { } major || Ask(window, IpcGetMinorVersion) is not { } minor)
            {
                return null;
            }

            return $"{major}.{minor}";
        }
    }

    /// <summary>
    /// Where Everything is installed, when it is: its program, found where its
    /// installers put it or where its start-up entry points.  Null when it is
    /// not installed.
    /// </summary>
    public static string? InstalledProgram
    {
        get
        {
            foreach (var candidate in ProgramCandidates())
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
                {
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Starts an installed Everything that is not running, in the background
    /// as its own start-up entry does (<c>-startup</c>: no window, only its
    /// tray icon).  Tried once per session: someone who closed Everything
    /// on purpose is not overruled at every keystroke.  True when it was
    /// started just now.
    /// </summary>
    public bool TryStartInstalled()
    {
        if (IsRunning || Interlocked.Exchange(ref _startAttempted, 1) != 0 || InstalledProgram is not { } program)
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(program, "-startup")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(program) ?? string.Empty,
            });
            return process is not null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Asks Everything for the first <paramref name="maximum"/> entries that
    /// match <paramref name="search"/> - written in Everything's own syntax -
    /// in the order <paramref name="sort"/> names (an
    /// <c>EVERYTHING_IPC_SORT_*</c> value), with how many match in all.
    /// Null when Everything is not running or did not answer in time.
    /// </summary>
    public async Task<EverythingPage?> QueryAsync(string search, int maximum, CancellationToken cancellationToken, uint sort = SortNameAscending)
    {
        ArgumentNullException.ThrowIfNull(search);
        var everything = FindEverythingWindow();
        if (everything == 0)
        {
            return null;
        }

        var reply = EnsureWindow();
        if (reply == 0)
        {
            return null;
        }

        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        var id = unchecked((uint)Interlocked.Increment(ref _nextReply));
        var answer = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            // Off the caller's thread: sending waits for Everything to take
            // the query, which is quick but is still another process.
            var sent = await Task.Run(() => Send(everything, reply, id, search, (uint)Math.Max(0, maximum), sort), cancellationToken).ConfigureAwait(false);
            if (!sent)
            {
                return null;
            }

            var bytes = await answer.Task.WaitAsync(ReplyTimeout, cancellationToken).ConfigureAwait(false);
            return Parse(bytes);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            _pending.TryRemove(id, out _);
            _oneAtATime.Release();
        }
    }

    /// <summary>
    /// Reads an <c>EVERYTHING_IPC_LIST2</c>: five counters, then an item
    /// header - flags and the offset of its data - per item, then the data,
    /// each column present when its bit is set in the list's request flags.
    /// Anything that does not add up ends the list where it stops making
    /// sense rather than throwing.
    /// </summary>
    internal static EverythingPage Parse(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 20)
        {
            return new EverythingPage(0, []);
        }

        var total = BinaryPrimitives.ReadUInt32LittleEndian(reply);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(reply[4..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(reply[12..]);
        var items = new List<EverythingItem>((int)Math.Min(count, 4096));
        for (var index = 0; index < count; index++)
        {
            var header = 20 + (index * 8);
            if (header + 8 > reply.Length)
            {
                break;
            }

            var itemFlags = BinaryPrimitives.ReadUInt32LittleEndian(reply[header..]);
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(reply[(header + 4)..]);
            if (TryReadItem(reply, offset, flags, itemFlags, out var item))
            {
                items.Add(item);
            }
        }

        return new EverythingPage(total, items);
    }

    private static bool TryReadItem(ReadOnlySpan<byte> reply, int offset, uint flags, uint itemFlags, out EverythingItem item)
    {
        item = default;
        if (offset < 0 || offset > reply.Length)
        {
            return false;
        }

        var data = reply[offset..];
        string name = string.Empty, directory = string.Empty, highlighted = string.Empty;
        long size = -1;
        DateTime? modified = null;
        for (var bit = 1u; bit <= RequestHighlightedFullPath; bit <<= 1)
        {
            if ((flags & bit) == 0)
            {
                continue;
            }

            switch (bit)
            {
                case RequestName:
                    if (!TryText(ref data, out name))
                    {
                        return false;
                    }

                    break;
                case RequestPath:
                    if (!TryText(ref data, out directory))
                    {
                        return false;
                    }

                    break;
                case RequestHighlightedName:
                    if (!TryText(ref data, out highlighted))
                    {
                        return false;
                    }

                    break;
                case RequestFullPath or RequestExtension or RequestFileListName or RequestHighlightedPath or RequestHighlightedFullPath:
                    if (!TryText(ref data, out _))
                    {
                        return false;
                    }

                    break;
                case RequestSize:
                    if (data.Length < 8)
                    {
                        return false;
                    }

                    size = BinaryPrimitives.ReadInt64LittleEndian(data);
                    data = data[8..];
                    break;
                case RequestDateModified:
                    if (data.Length < 8)
                    {
                        return false;
                    }

                    modified = FromFileTime(BinaryPrimitives.ReadInt64LittleEndian(data));
                    data = data[8..];
                    break;
                case RequestDateCreated or RequestDateAccessed or RequestDateRun or RequestDateRecentlyChanged:
                    if (data.Length < 8)
                    {
                        return false;
                    }

                    data = data[8..];
                    break;
                case RequestAttributes or RequestRunCount:
                    if (data.Length < 4)
                    {
                        return false;
                    }

                    data = data[4..];
                    break;
            }
        }

        if (name.Length == 0 && directory.Length == 0)
        {
            return false;
        }

        var isFolder = (itemFlags & (ItemFolder | ItemDrive)) != 0;
        item = new EverythingItem(name, directory, isFolder, isFolder && size <= 0 ? -1 : size, modified, highlighted);
        return true;
    }

    /// <summary>A column of text: its length in characters, then the characters and a terminating null.</summary>
    private static bool TryText(ref ReadOnlySpan<byte> data, out string text)
    {
        text = string.Empty;
        if (data.Length < 4)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var bytes = ((long)length + 1) * 2;
        if (length > 32_768 || 4 + bytes > data.Length)
        {
            return false;
        }

        text = new string(MemoryMarshal.Cast<byte, char>(data.Slice(4, (int)length * 2)));
        data = data[(int)(4 + bytes)..];
        return true;
    }

    private static DateTime? FromFileTime(long fileTime)
    {
        if (fileTime <= 0 || fileTime == long.MaxValue)
        {
            return null;
        }

        try
        {
            return DateTime.FromFileTime(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static nint FindEverythingWindow()
    {
        foreach (var windowClass in WindowClasses)
        {
            var window = FindWindowW(windowClass, null);
            if (window != 0)
            {
                return window;
            }
        }

        return 0;
    }

    /// <summary>One EVERYTHING_WM_IPC command; null when Everything did not answer within a second.</summary>
    private static long? Ask(nint window, nint command)
    {
        const uint abortIfHung = 0x0002;
        return SendMessageTimeoutW(window, WmUser, command, 0, abortIfHung, 1000, out var result) == 0 ? null : result;
    }

    private static unsafe bool Send(nint everything, nint reply, uint id, string search, uint maximum, uint sort)
    {
        // EVERYTHING_IPC_QUERY2: seven DWORDs, then the search and its null.
        var size = 28 + ((search.Length + 1) * 2);
        var buffer = new byte[size];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, unchecked((uint)reply));
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], id);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], maximum);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], Requested);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], sort);
        MemoryMarshal.AsBytes(search.AsSpan()).CopyTo(span[28..]);

        fixed (byte* data = buffer)
        {
            var copy = new CopyDataStruct { Data = CopyDataQuery2, Size = (uint)size, Pointer = (nint)data };
            const uint abortIfHung = 0x0002;
            return SendMessageTimeoutW(everything, WmCopyData, reply, (nint)(&copy), abortIfHung, 3000, out var accepted) != 0 && accepted != 0;
        }
    }

    /// <summary>The reply window, made with its thread on first use.</summary>
    private nint EnsureWindow()
    {
        lock (_gate)
        {
            if (_thread is not null)
            {
                return _window;
            }

            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() => Pump(ready))
            {
                IsBackground = true,
                Name = "Everything replies",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
            return _window;
        }
    }

    private unsafe void Pump(ManualResetEventSlim ready)
    {
        var className = "UltraExplorer.EverythingReply." + Environment.ProcessId;
        fixed (char* name = className)
        {
            var instance = GetModuleHandleW(null);
            var windowClass = new WindowClass
            {
                Size = (uint)sizeof(WindowClass),
                Procedure = &WindowProcedure,
                Instance = instance,
                ClassName = name,
            };
            RegisterClassExW(&windowClass);

            // HWND_MESSAGE: a window that only receives messages, never shown
            // and never enumerated.
            _window = CreateWindowExW(0, name, name, 0, 0, 0, 0, 0, -3, 0, instance, 0);
        }

        ready.Set();
        if (_window == 0)
        {
            return;
        }

        Message message;
        while (GetMessageW(&message, 0, 0, 0) > 0)
        {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == WmCopyData && lParam != 0)
        {
            var copy = (CopyDataStruct*)lParam;
            var id = unchecked((uint)copy->Data);
            if (Instance.IsValueCreated && Instance.Value._pending.TryGetValue(id, out var answer))
            {
                // The data is Everything's only for as long as this message
                // lasts: copied out now, read later.
                var bytes = copy->Pointer == 0 || copy->Size == 0
                    ? []
                    : new ReadOnlySpan<byte>((void*)copy->Pointer, (int)copy->Size).ToArray();
                answer.TrySetResult(bytes);
                return 1;
            }

            return 0;
        }

        if (message == WmClose)
        {
            DestroyWindow(window);
            return 0;
        }

        if (message == WmDestroy)
        {
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }

    private static IEnumerable<string?> ProgramCandidates()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(programFiles, "Everything", "Everything.exe");
        yield return Path.Combine(programFilesX86, "Everything", "Everything.exe");
        yield return Path.Combine(local, "Programs", "Everything", "Everything.exe");
        yield return Path.Combine(programFiles, "Everything 1.5a", "Everything64.exe");

        // Wherever its start-up entry says it lives: a portable copy, a
        // different drive.
        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            string? command = null;
            try
            {
                using var run = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                command = run?.GetValue("Everything") as string;
            }
            catch (Exception exception) when (exception is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
            }

            yield return ProgramOf(command);
        }
    }

    /// <summary>The program a start-up command runs: <c>"C:\x\Everything.exe" -startup</c> is C:\x\Everything.exe.</summary>
    internal static string? ProgramOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }

        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public nint Data;
        public uint Size;
        public nint Pointer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WindowClass
    {
        public uint Size;
        public uint Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern nint SendMessageTimeoutW(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    [DllImport("user32.dll")]
    private static extern unsafe ushort RegisterClassExW(WindowClass* windowClass);

    [DllImport("user32.dll")]
    private static extern unsafe nint CreateWindowExW(uint extendedStyle, char* className, char* windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern unsafe int GetMessageW(Message* message, nint window, uint first, uint last);

    [DllImport("user32.dll")]
    private static extern unsafe int TranslateMessage(Message* message);

    [DllImport("user32.dll")]
    private static extern unsafe nint DispatchMessageW(Message* message);

    [DllImport("user32.dll")]
    private static extern int DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);
}
