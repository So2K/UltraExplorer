using System.Runtime.InteropServices;
using System.Text;

namespace UltraExplorer.Picker;

/// <summary>
/// Reads what a <c>.lnk</c> points at.  The common dialog returns the target of
/// a shortcut rather than the shortcut itself, unless the caller asked for the
/// opposite with <see cref="FileDialogOptions.NoDereferenceLinks"/>.
/// </summary>
internal static class ShellLinkResolver
{
    /// <summary>A shell link stores its target in at most this many characters.</summary>
    private const int MaxPath = 260;

    /// <summary>
    /// Returns the target of a shortcut, or the path unchanged when it is not
    /// one or cannot be read.  Never throws: a broken shortcut is still a
    /// legitimate answer for a caller that wanted the link itself.
    /// </summary>
    public static string Resolve(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            return path;
        }

        object? instance = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ShellLinkClassId);
            if (type is null)
            {
                return path;
            }

            instance = Activator.CreateInstance(type);
            if (instance is not IPersistFile persist || instance is not IShellLinkW link)
            {
                return path;
            }

            persist.Load(path, STGM_READ);

            // Not SLGP_RAWPATH: a raw path may still hold %SystemRoot% and the
            // like, which File.Exists and the caller cannot use.  The expand
            // below catches a link that stores its target only that way.
            var buffer = new StringBuilder(MaxPath);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
            var target = Environment.ExpandEnvironmentVariables(buffer.ToString());
            return string.IsNullOrWhiteSpace(target) ? path : target;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException
            or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return path;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                Marshal.FinalReleaseComObject(instance);
            }
        }
    }

    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");

    private const int STGM_READ = 0;

    /// <summary>
    /// Only <c>GetPath</c> is declared, and it is the first slot after
    /// <c>IUnknown</c>, so the rest of the vtable is never reached.
    /// </summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int fileLength,
            IntPtr findData,
            uint flags);
    }

    /// <summary>
    /// <c>Load</c> sits behind <c>IPersist::GetClassID</c> and
    /// <c>IPersistFile::IsDirty</c>, so both have to be declared to reach it.
    /// </summary>
    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, int mode);
    }
}
