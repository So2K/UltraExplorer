using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace UltraExplorer.Services.Archives;

// The interfaces 7z.dll speaks, as 7-Zip's own CPP/7zip/IStream.h, IProgress.h,
// Archive/IArchive.h and IPassword.h declare them.  Only what the canvas uses:
// listing an archive, and extracting from it.  Every method keeps its HRESULT
// (PreserveSig), so a failure inside 7-Zip is a number to look at, not an
// exception thrown through native frames.

[ComImport, Guid("23170F69-40C1-278A-0000-000300010000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISequentialInStream
{
    [PreserveSig] int Read(IntPtr data, uint size, IntPtr processedSize);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000300030000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInStream
{
    [PreserveSig] int Read(IntPtr data, uint size, IntPtr processedSize);
    [PreserveSig] int Seek(long offset, uint seekOrigin, IntPtr newPosition);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000300020000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISequentialOutStream
{
    [PreserveSig] int Write(IntPtr data, uint size, IntPtr processedSize);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600100000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IArchiveOpenCallback
{
    [PreserveSig] int SetTotal(IntPtr files, IntPtr bytes);
    [PreserveSig] int SetCompleted(IntPtr files, IntPtr bytes);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600300000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IArchiveOpenVolumeCallback
{
    [PreserveSig] int GetProperty(uint propId, IntPtr value);
    [PreserveSig] int GetStream([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Interface)] out IInStream? inStream);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600200000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IArchiveExtractCallback
{
    // IProgress
    [PreserveSig] int SetTotal(ulong total);
    [PreserveSig] int SetCompleted(IntPtr completeValue);

    [PreserveSig] int GetStream(uint index, [MarshalAs(UnmanagedType.Interface)] out ISequentialOutStream? outStream, int askExtractMode);
    [PreserveSig] int PrepareOperation(int askExtractMode);
    [PreserveSig] int SetOperationResult(int operationResult);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000500100000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICryptoGetTextPassword
{
    [PreserveSig] int CryptoGetTextPassword([MarshalAs(UnmanagedType.BStr)] out string? password);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600600000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInArchive
{
    [PreserveSig] int Open(IInStream stream, IntPtr maxCheckStartPosition, [MarshalAs(UnmanagedType.Interface)] IArchiveOpenCallback? openCallback);
    [PreserveSig] int Close();
    [PreserveSig] int GetNumberOfItems(out uint numItems);
    [PreserveSig] int GetProperty(uint index, uint propId, IntPtr value);
    [PreserveSig] int Extract([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] uint[]? indices, uint numItems, int testMode, [MarshalAs(UnmanagedType.Interface)] IArchiveExtractCallback extractCallback);
    [PreserveSig] int GetArchiveProperty(uint propId, IntPtr value);
}

/// <summary>The PROPVARIANT 7-Zip answers with: a type and eight bytes of value, or a pointer.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public long Long;
    [FieldOffset(8)] public IntPtr Pointer;
    [FieldOffset(8)] public uint UInt;
    [FieldOffset(8)] public short Bool;

    public const ushort Empty = 0, I4 = 3, Bstr = 8, Boolean = 11, UI1 = 17, UI2 = 18, UI4 = 19, I8 = 20, UI8 = 21, FileTime = 64;
}

internal static class PropId
{
    public const uint Path = 3, Name = 4, IsDir = 6, Size = 7, Attrib = 9, MTime = 12, Solid = 13, Encrypted = 15, IsAnti = 21;
}

internal static class HResult
{
    public const int Ok = 0, False = 1, Abort = unchecked((int)0x80004004), Fail = unchecked((int)0x80004005), NotImpl = unchecked((int)0x80004001);
}

/// <summary>
/// 7z.dll itself: found where 7-Zip is installed, else beside the app, loaded
/// once.  Knows every format the library has and which extensions name them.
/// </summary>
internal static unsafe class SevenZipLibrary
{
    private static readonly Lazy<Loaded?> Instance = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    internal sealed class Loaded
    {
        public required string Path { get; init; }
        public required IntPtr CreateObject { get; init; }

        /// <summary>Every format, in the library's order: name and class id.</summary>
        public required IReadOnlyList<(string Name, Guid ClassId)> Formats { get; init; }

        /// <summary>Formats that claim an extension, most specific first.</summary>
        public required IReadOnlyDictionary<string, List<Guid>> ByExtension { get; init; }
    }

    public static bool IsAvailable => Instance.Value is not null;

    public static string? LoadedFrom => Instance.Value?.Path;

    internal static Loaded? Library => Instance.Value;

    private static readonly Guid InArchiveId = typeof(IInArchive).GUID;

    /// <summary>A fresh handler of one format, or null when the library will not make one.</summary>
    public static IInArchive? Create(Guid classId)
    {
        if (Instance.Value is not { } library)
        {
            return null;
        }

        var iid = InArchiveId;
        IntPtr pointer;
        if (((delegate* unmanaged[Stdcall]<Guid*, Guid*, IntPtr*, int>)library.CreateObject)(&classId, &iid, &pointer) != 0 || pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return (IInArchive)Marshal.GetObjectForIUnknown(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private static Loaded? Load()
    {
        foreach (var candidate in Candidates())
        {
            try
            {
                if (!File.Exists(candidate) || !NativeLibrary.TryLoad(candidate, out var handle))
                {
                    continue;
                }

                if (!NativeLibrary.TryGetExport(handle, "CreateObject", out var create)
                    || !NativeLibrary.TryGetExport(handle, "GetNumberOfFormats", out var count)
                    || !NativeLibrary.TryGetExport(handle, "GetHandlerProperty2", out var property))
                {
                    continue;
                }

                var formats = ReadFormats(
                    (delegate* unmanaged[Stdcall]<uint*, int>)count,
                    (delegate* unmanaged[Stdcall]<uint, uint, PropVariant*, int>)property,
                    out var byExtension);
                if (formats.Count == 0)
                {
                    continue;
                }

                return new Loaded
                {
                    Path = candidate,
                    CreateObject = create,
                    Formats = formats,
                    ByExtension = byExtension
                };
            }
            catch (Exception ex) when (ex is BadImageFormatException or DllNotFoundException or EntryPointNotFoundException)
            {
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        yield return System.IO.Path.Combine(AppContext.BaseDirectory, "archives", "7z.dll");
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Default })
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                string? folder = null;
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var key = root.OpenSubKey(@"SOFTWARE\7-Zip");
                    folder = key?.GetValue("Path64") as string ?? key?.GetValue("Path") as string;
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                }

                if (!string.IsNullOrEmpty(folder))
                {
                    yield return System.IO.Path.Combine(folder, "7z.dll");
                }
            }
        }

        yield return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.dll");
        yield return System.IO.Path.Combine(AppContext.BaseDirectory, "7z.dll");
    }

    private static List<(string, Guid)> ReadFormats(
        delegate* unmanaged[Stdcall]<uint*, int> getCount,
        delegate* unmanaged[Stdcall]<uint, uint, PropVariant*, int> getProperty,
        out Dictionary<string, List<Guid>> byExtension)
    {
        const uint NameProperty = 0, ClassIdProperty = 1, ExtensionProperty = 2;
        var formats = new List<(string, Guid)>();
        byExtension = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
        uint count;
        if (getCount(&count) != 0)
        {
            return formats;
        }

        for (uint index = 0; index < count; index++)
        {
            PropVariant value = default;
            string name = string.Empty;
            if (getProperty(index, NameProperty, &value) == 0 && value.Type == PropVariant.Bstr)
            {
                name = Marshal.PtrToStringBSTR(value.Pointer);
            }

            PropVariantClear(&value);
            value = default;
            Guid? classId = null;
            if (getProperty(index, ClassIdProperty, &value) == 0 && value.Type == PropVariant.Bstr && value.Pointer != IntPtr.Zero)
            {
                classId = *(Guid*)value.Pointer;
            }

            PropVariantClear(&value);
            value = default;
            var extensions = string.Empty;
            if (getProperty(index, ExtensionProperty, &value) == 0 && value.Type == PropVariant.Bstr)
            {
                extensions = Marshal.PtrToStringBSTR(value.Pointer);
            }

            PropVariantClear(&value);
            if (classId is not { } id)
            {
                continue;
            }

            formats.Add((name, id));
            foreach (var extension in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!byExtension.TryGetValue(extension, out var list))
                {
                    byExtension[extension] = list = [];
                }

                list.Add(id);
            }
        }

        return formats;
    }

    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(PropVariant* value);
}

/// <summary>A file on disk as 7-Zip reads it: seekable, read-only, shared.</summary>
[ClassInterface(ClassInterfaceType.None)]
internal sealed unsafe class FileInStream : IInStream, ISequentialInStream, IDisposable
{
    private readonly FileStream _stream;

    public FileInStream(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
    }

    public int Read(IntPtr data, uint size, IntPtr processedSize)
    {
        try
        {
            var read = size == 0 ? 0 : _stream.Read(new Span<byte>((void*)data, (int)Math.Min(size, int.MaxValue)));
            if (processedSize != IntPtr.Zero)
            {
                *(uint*)processedSize = (uint)read;
            }

            return HResult.Ok;
        }
        catch (IOException)
        {
            return HResult.Fail;
        }
    }

    public int Seek(long offset, uint seekOrigin, IntPtr newPosition)
    {
        try
        {
            var position = _stream.Seek(offset, (SeekOrigin)seekOrigin);
            if (newPosition != IntPtr.Zero)
            {
                *(ulong*)newPosition = (ulong)position;
            }

            return HResult.Ok;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return HResult.Fail;
        }
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>Where one extracted item's bytes go.</summary>
[ClassInterface(ClassInterfaceType.None)]
internal sealed unsafe class StreamOutStream(Stream stream) : ISequentialOutStream
{
    public Stream Stream { get; } = stream;

    public int Write(IntPtr data, uint size, IntPtr processedSize)
    {
        try
        {
            if (size > 0)
            {
                Stream.Write(new ReadOnlySpan<byte>((void*)data, (int)size));
            }

            if (processedSize != IntPtr.Zero)
            {
                *(uint*)processedSize = size;
            }

            return HResult.Ok;
        }
        catch (IOException)
        {
            return HResult.Fail;
        }
    }
}

/// <summary>
/// Opening an archive: asks nothing of anyone, so a background read never
/// stops for a password - an archive whose names are encrypted just fails to
/// list until it is opened with one (<see cref="Password"/>).  Answers for the
/// other volumes of a multi-part archive by opening them beside the first.
/// </summary>
[ClassInterface(ClassInterfaceType.None)]
internal sealed class OpenCallback(string firstVolume, string? password, CancellationToken cancellation)
    : IArchiveOpenCallback, IArchiveOpenVolumeCallback, ICryptoGetTextPassword, IDisposable
{
    private readonly List<FileInStream> _volumes = [];
    private readonly string _first = firstVolume;
    private string _current = firstVolume;

    public bool PasswordWasAsked { get; private set; }

    public int SetTotal(IntPtr files, IntPtr bytes) => HResult.Ok;

    public int SetCompleted(IntPtr files, IntPtr bytes) => cancellation.IsCancellationRequested ? HResult.Abort : HResult.Ok;

    public unsafe int GetProperty(uint propId, IntPtr value)
    {
        var variant = (PropVariant*)value;
        *variant = default;
        if (propId == PropId.Name)
        {
            variant->Type = PropVariant.Bstr;
            variant->Pointer = Marshal.StringToBSTR(Path.GetFileName(_current));
        }

        return HResult.Ok;
    }

    public int GetStream(string name, out IInStream? inStream)
    {
        inStream = null;
        var path = Path.Combine(Path.GetDirectoryName(_first) ?? string.Empty, Path.GetFileName(name));
        if (!File.Exists(path))
        {
            return HResult.False;
        }

        try
        {
            var stream = new FileInStream(path);
            _volumes.Add(stream);
            _current = path;
            inStream = stream;
            return HResult.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HResult.False;
        }
    }

    public int CryptoGetTextPassword(out string? text)
    {
        PasswordWasAsked = true;
        text = password;
        return password is null ? HResult.Abort : HResult.Ok;
    }

    public void Dispose()
    {
        foreach (var volume in _volumes)
        {
            volume.Dispose();
        }
    }
}
