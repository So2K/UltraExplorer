using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

/// <summary>
/// What Explorer's Type column calls a file - "Application extension", "PNG
/// File", "Text Document" - by its extension, for ordering by type.
///
/// The name comes from the Shell, which reads it out of the registry and
/// often out of a resource DLL as well: a millisecond or so the first time an
/// extension is asked about, and tens of milliseconds for the very first call
/// of all, while the Shell loads.  A folder of System32's fifty extensions
/// looked up on the UI thread would be a visible stall, so every name is
/// cached for the life of the process, and every extension the canvas meets
/// is looked up in the background the moment it is first seen - by the time
/// anyone asks to sort by type, the answers are normally already here.  What
/// orders on the UI thread and cannot wait - the tree canvas, the folder list
/// - asks through <see cref="TryGet"/>, orders by a stand-in for a name not
/// here yet, and orders again once <see cref="WhenPrefetchedAsync"/> says
/// the background has it.
///
/// Safe to call from any thread.
/// </summary>
public static class FileTypeNames
{
    /// <summary>The type every folder has, so ordering by type leaves folders in name order.</summary>
    public const string Folder = "File folder";

    /// <summary>The type of a file with no extension at all, as Explorer names it.</summary>
    private const string NoExtension = "File";

    private const uint FileAttributeNormal = 0x80;
    private const uint ShgfiUseFileAttributes = 0x10;
    private const uint ShgfiTypeName = 0x400;

    /// <summary>
    /// The names found so far, each with the <see cref="_version"/> it was
    /// found under.  A test that swaps the lookup bumps the version, and an
    /// answer a background lookup was still working out with the old one is
    /// then recognised as stale instead of being trusted.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (int Version, string Name)> Names = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentQueue<string> Prefetches = new();
    private static Func<string, string>? _resolver;
    private static int _version;
    private static int _prefetching;

    /// <summary>Extensions queued for a background lookup and not yet looked up.</summary>
    private static int _outstanding;

    private static readonly object DrainedGate = new();
    private static TaskCompletionSource? _drained;

    /// <summary>
    /// Replaces the Shell lookup, for tests that want type names they can
    /// predict; null asks the Shell.  Setting it forgets every name found so
    /// far, so nothing looked up the other way survives the switch, and has
    /// every kind already met looked up again the new way in the background,
    /// so the cache is as warm afterwards as it was before.
    /// </summary>
    public static Func<string, string>? Resolver
    {
        get => Volatile.Read(ref _resolver);
        set
        {
            Volatile.Write(ref _resolver, value);
            Interlocked.Increment(ref _version);
            var known = Names.Keys.ToArray();
            Names.Clear();
            foreach (var extension in known)
            {
                Prefetch(extension);
            }
        }
    }

    /// <summary>
    /// The type name for an extension written the way <see cref="NestedFile.Extension"/>
    /// writes it - lower case, no dot - though a leading dot or capitals are
    /// taken too.  No extension is a plain "File"; an extension the Shell
    /// knows nothing about is "XYZ File", which is what Explorer shows.
    /// </summary>
    public static string Of(string extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return NoExtension;
        }

        if (extension[0] == '.')
        {
            extension = extension[1..];
            if (extension.Length == 0)
            {
                return NoExtension;
            }
        }

        var version = Volatile.Read(ref _version);
        if (Names.TryGetValue(extension, out var known) && known.Version == version)
        {
            return known.Name;
        }

        var resolver = Resolver;
        var name = resolver is null ? FromShell(extension) : resolver(extension);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = extension.ToUpperInvariant() + " File";
        }

        Names[extension] = (version, name);
        return name;
    }

    /// <summary>
    /// A test's table (see <see cref="Resolver"/>) answers only in the
    /// background, the way the Shell is asked: for checking that nothing on
    /// the UI thread waits for a name.  Off, a table answers <see cref="TryGet"/>
    /// at once - it costs nothing to ask.
    /// </summary>
    internal static bool DefersResolver { get; set; }

    /// <summary>
    /// The type name for an extension if it can be had without waiting: known
    /// already, or no extension at all.  Otherwise false, with the name it will
    /// have if the Shell knows nothing better - "XYZ File" - to order by for
    /// now, and the real one is looked up in the background; <see cref="WhenPrefetchedAsync"/>
    /// says when it is there.  For the UI thread, which must never wait on the
    /// Shell: the first question about an extension can take milliseconds,
    /// and the first of the process tens of them.
    /// </summary>
    internal static bool TryGet(string extension, out string name)
    {
        if (string.IsNullOrEmpty(extension) || extension is ".")
        {
            name = NoExtension;
            return true;
        }

        if (extension[0] == '.')
        {
            extension = extension[1..];
        }

        if (Names.TryGetValue(extension, out var known) && known.Version == Volatile.Read(ref _version))
        {
            name = known.Name;
            return true;
        }

        if (Resolver is not null && !DefersResolver)
        {
            name = Of(extension);
            return true;
        }

        Prefetch(extension);
        name = extension.ToUpperInvariant() + " File";
        return false;
    }

    /// <summary>
    /// Completes when every extension queued for a background lookup so far
    /// has been looked up - at once when none is waiting.  What was ordered by
    /// a stand-in from <see cref="TryGet"/> can be ordered again then.
    /// </summary>
    internal static Task WhenPrefetchedAsync()
    {
        lock (DrainedGate)
        {
            if (Volatile.Read(ref _outstanding) == 0)
            {
                return Task.CompletedTask;
            }

            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task;
        }
    }

    /// <summary>
    /// Looks an extension up in the background, so that ordering a folder by
    /// type later finds its name waiting.  Called once per extension, when
    /// the first file with it is read; costs the caller one queue entry.
    /// </summary>
    internal static void Prefetch(string extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return;
        }

        Interlocked.Increment(ref _outstanding);
        Prefetches.Enqueue(extension);
        if (Interlocked.CompareExchange(ref _prefetching, 1, 0) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static _ => DrainPrefetches(), null);
        }
    }

    /// <summary>
    /// For a thread that reads a folder, never the UI thread: every kind of
    /// file among <paramref name="fileNames"/> is either looked up here and
    /// now, when the folder is about to be shown by type, or queued for the
    /// background when it has not been met before - the way reading a folder
    /// for the nested canvas meets it - so a later change to ordering by type
    /// finds its name waiting.
    /// </summary>
    /// <param name="lookUpNow">Whether to have every name before returning, rather than only queued.</param>
    internal static void WarmNames(IEnumerable<string> fileNames, bool lookUpNow)
    {
        HashSet<string>? seen = null;
        foreach (var fileName in fileNames)
        {
            // Worked out exactly as the nested canvas works it out, which
            // queues an extension it meets for the first time on the way.
            var extension = new NestedFile(fileName, false, 0).Extension;
            if (!lookUpNow || extension.Length == 0)
            {
                continue;
            }

            seen ??= new HashSet<string>(ReferenceEqualityComparer.Instance);
            if (seen.Add(extension))
            {
                Of(extension);
            }
        }
    }

    /// <summary>
    /// Makes sure every extension among <paramref name="files"/> has its name
    /// cached, looking up the ones that have not.  For a reader's thread while
    /// the canvas is ordered by type: the folder then reaches the UI thread
    /// with nothing left for the Shell to be asked there.
    /// </summary>
    internal static void Warm(IReadOnlyList<NestedFile> files)
    {
        // Extensions are shared strings, so one per kind is found by
        // reference: a folder of fifty thousand files is a handful of kinds.
        HashSet<string>? seen = null;
        string? last = null;
        for (var index = 0; index < files.Count; index++)
        {
            var extension = files[index].Extension;
            if (string.IsNullOrEmpty(extension) || ReferenceEquals(extension, last))
            {
                continue;
            }

            last = extension;
            seen ??= new HashSet<string>(ReferenceEqualityComparer.Instance);
            if (seen.Add(extension))
            {
                Of(extension);
            }
        }
    }

    private static void DrainPrefetches()
    {
        while (true)
        {
            while (Prefetches.TryDequeue(out var extension))
            {
                try
                {
                    Of(extension);
                }
                catch (Exception)
                {
                    // Only a head start: a name that could not be found here
                    // is asked for again, and reported, when it is needed.
                }
                finally
                {
                    OnePrefetchDone();
                }
            }

            Volatile.Write(ref _prefetching, 0);

            // Something queued between the last look and the flag going down
            // would otherwise wait for the next extension to be seen.
            if (Prefetches.IsEmpty || Interlocked.CompareExchange(ref _prefetching, 1, 0) != 0)
            {
                return;
            }
        }
    }

    /// <summary>One background lookup finished: the last one outstanding lets go of whoever waits for them all.</summary>
    private static void OnePrefetchDone()
    {
        if (Interlocked.Decrement(ref _outstanding) != 0)
        {
            return;
        }

        TaskCompletionSource? drained;
        lock (DrainedGate)
        {
            // Another may have been queued since the count reached nothing;
            // then its lookup is the one to finish the wait.
            if (Volatile.Read(ref _outstanding) != 0)
            {
                return;
            }

            drained = _drained;
            _drained = null;
        }

        drained?.TrySetResult();
    }

    private static string? FromShell(string extension)
    {
        try
        {
            var info = default(ShFileInfo);
            var result = SHGetFileInfo(
                "." + extension,
                FileAttributeNormal,
                ref info,
                (uint)Marshal.SizeOf<ShFileInfo>(),
                ShgfiTypeName | ShgfiUseFileAttributes);
            return result == IntPtr.Zero ? null : info.TypeName;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            return null;
        }
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
        ref ShFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);
}
