using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UltraExplorer.Services;

public sealed class ShellIconService
{
    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ImageSource? GetSmallIcon(string path, bool isDirectory)
    {
        var cacheKey = isDirectory
            ? "<folder>"
            : IsPathSpecificIcon(path) ? path : Path.GetExtension(path).ToLowerInvariant();

        return _cache.GetOrAdd(cacheKey, _ => ExtractIcon(path, isDirectory));
    }

    private static bool IsPathSpecificIcon(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ico", StringComparison.OrdinalIgnoreCase);
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
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.IconHandle);
        }
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
