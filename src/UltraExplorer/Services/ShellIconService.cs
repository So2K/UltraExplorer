using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace UltraExplorer.Services;

/// <summary>
/// Shell icons, cached by extension.
///
/// Resolution happens on a dedicated STA worker because SHGetFileInfo can take
/// milliseconds per distinct executable, and doing that inline while a folder of
/// several thousand entries is being materialised freezes the UI thread for
/// however long the folder is big.  Callers get the cached icon immediately if
/// there is one and subscribe for the rest.
/// </summary>
public sealed class ShellIconService : IDisposable
{
    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Who is waiting for an icon that is being resolved, by cache key.  It used
    /// to be a set, and a second caller asking for an icon already in flight was
    /// simply dropped - which is how a file could have its icon on the canvas and
    /// a blank glyph in the folder list, for ever, because nothing ever asked
    /// again.
    /// </summary>
    private readonly Dictionary<string, List<Action<ImageSource?>>> _pending = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();
    private readonly BlockingCollection<IconRequest> _queue = new(new ConcurrentQueue<IconRequest>());
    private readonly Thread _worker;
    private bool _disposed;

    public ShellIconService()
    {
        _worker = new Thread(Work)
        {
            IsBackground = true,
            Name = "UltraExplorer Shell icons",
            Priority = ThreadPriority.BelowNormal
        };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    /// <summary>The icon if it is already known, without touching the Shell.</summary>
    public ImageSource? GetCached(string path, bool isDirectory)
        => _cache.TryGetValue(CacheKey(path, isDirectory), out var icon) ? icon : null;

    /// <summary>
    /// Resolves in the background and calls back on the UI thread.  Returns true
    /// when the icon was already cached and <paramref name="completed"/> has
    /// been invoked synchronously.  Several callers may wait on the same icon;
    /// the Shell is asked once and all of them are told.
    /// </summary>
    public bool Request(string path, bool isDirectory, Action<ImageSource?> completed)
    {
        var key = CacheKey(path, isDirectory);
        if (_cache.TryGetValue(key, out var cached))
        {
            completed(cached);
            return true;
        }

        if (_disposed)
        {
            return false;
        }

        lock (_gate)
        {
            // Checked again inside the lock: the worker may have finished between
            // the first look and here, and then nobody would ever call back.
            if (_cache.TryGetValue(key, out cached))
            {
                completed(cached);
                return true;
            }

            if (_pending.TryGetValue(key, out var waiting))
            {
                waiting.Add(completed);
                return false;
            }

            _pending[key] = [completed];
        }

        _queue.Add(new IconRequest(key, path, isDirectory));
        return false;
    }

    /// <summary>
    /// Blocking resolution, for the handful of navigation-pane entries where the
    /// icon is part of the initial layout.
    /// </summary>
    public ImageSource? GetSmallIcon(string path, bool isDirectory)
        => _cache.GetOrAdd(CacheKey(path, isDirectory), _ => ExtractIcon(path, isDirectory));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
    }

    private void Work()
    {
        try
        {
            foreach (var request in _queue.GetConsumingEnumerable())
            {
                ImageSource? icon = null;
                try
                {
                    icon = _cache.GetOrAdd(request.Key, _ => ExtractIcon(request.Path, request.IsDirectory));
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ExternalException)
                {
                    _cache.TryAdd(request.Key, null);
                }
                List<Action<ImageSource?>>? waiting;
                lock (_gate)
                {
                    _pending.Remove(request.Key, out waiting);
                }

                var dispatcher = Application.Current?.Dispatcher;
                if (waiting is null || dispatcher is null || dispatcher.HasShutdownStarted)
                {
                    continue;
                }

                var resolved = icon;
                _ = dispatcher.InvokeAsync(
                    () =>
                    {
                        foreach (var completed in waiting)
                        {
                            completed(resolved);
                        }
                    },
                    DispatcherPriority.Background);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding raced with the enumerator; the app is closing.
        }
    }

    private static string CacheKey(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return "<folder>";
        }

        var extension = Path.GetExtension(path);
        return IsPathSpecificIcon(extension) ? path : extension.ToLowerInvariant();
    }

    private static bool IsPathSpecificIcon(string extension)
        => extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ico", StringComparison.OrdinalIgnoreCase);

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

    private readonly record struct IconRequest(string Key, string Path, bool IsDirectory);

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
