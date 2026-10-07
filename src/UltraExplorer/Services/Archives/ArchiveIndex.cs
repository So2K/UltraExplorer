using System.Runtime.InteropServices;
using UltraExplorer.Models;

namespace UltraExplorer.Services.Archives;

/// <summary>One entry of an archive: where it is in the archive's own list, and what it is.</summary>
internal readonly record struct ArchiveItem(uint Index, bool IsDirectory, long Size, long ModifiedTicks, bool IsHidden);

/// <summary>
/// Everything an archive holds, read once from its list of entries - which
/// for a zip is only its central directory - and kept: every folder inside
/// it listed the way a folder on disk is, so going into an archive and
/// around it never opens it again.  Paths inside use backslashes and no
/// leading one; the archive's own top is the empty path.
/// </summary>
internal sealed class ArchiveIndex
{
    private readonly Dictionary<string, ArchiveItem> _items;
    private readonly Dictionary<string, Folder> _folders;

    private sealed class Folder
    {
        public readonly List<(string Name, ArchiveItem Item)> Children = [];
        public NestedListing? Listing;
    }

    private ArchiveIndex(string path, long length, long writeTicks, Guid format, bool isSolid, Dictionary<string, ArchiveItem> items, Dictionary<string, Folder> folders)
    {
        Path = path;
        Length = length;
        WriteTicks = writeTicks;
        Format = format;
        IsSolid = isSolid;
        _items = items;
        _folders = folders;
    }

    /// <summary>The archive's file on disk (for one inside another, the copy taken out of it).</summary>
    public string Path { get; }

    public long Length { get; }

    public long WriteTicks { get; }

    /// <summary>The 7-Zip format that opened it.</summary>
    public Guid Format { get; }

    /// <summary>Compressed as one stream: taking any file out means decoding everything before it, so it is never split between threads.</summary>
    public bool IsSolid { get; }

    public int Count => _items.Count;

    public bool TryGetItem(string inner, out ArchiveItem item) => _items.TryGetValue(inner, out item);

    public bool IsFolder(string inner) => _folders.ContainsKey(inner);

    /// <summary>Every item at or below <paramref name="inner"/>, by its path inside the archive.</summary>
    public IEnumerable<(string Inner, ArchiveItem Item)> Below(string inner)
    {
        if (inner.Length == 0)
        {
            foreach (var pair in _items)
            {
                yield return (pair.Key, pair.Value);
            }

            yield break;
        }

        if (_items.TryGetValue(inner, out var self))
        {
            yield return (inner, self);
        }

        var prefix = inner + '\\';
        foreach (var pair in _items)
        {
            if (pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                yield return (pair.Key, pair.Value);
            }
        }
    }

    /// <summary>
    /// A folder inside the archive as the canvas reads a folder on disk; an
    /// archive inside it is offered as a folder too, when archives are
    /// opened as folders.  Made once per folder.
    /// </summary>
    public NestedListing List(string inner)
    {
        if (!_folders.TryGetValue(inner, out var folder))
        {
            return NestedListing.Failed("No longer exists");
        }

        if (folder.Listing is { } made)
        {
            return made;
        }

        var folders = new List<NestedEntry>();
        var files = new List<NestedFile>();
        var hidden = 0;
        foreach (var (name, item) in folder.Children)
        {
            if (item.IsDirectory)
            {
                folders.Add(new NestedEntry(name, item.IsHidden, false, item.ModifiedTicks));
            }
            else if (ArchiveService.BrowseArchives && ArchiveFormats.IsBrowsable(name))
            {
                folders.Add(new NestedEntry(name, item.IsHidden, false, item.ModifiedTicks, IsArchive: true));
            }
            else
            {
                files.Add(new NestedFile(name, item.IsHidden, item.Size, item.ModifiedTicks));
                hidden += item.IsHidden ? 1 : 0;
            }
        }

        folders.Sort(static (left, right) => ByName(left.Name, right.Name));
        files.Sort(static (left, right) => ByName(left.Name, right.Name));
        var listing = new NestedListing(folders, files.Count, hidden, false) { Files = files.ToArray() };
        folder.Listing = listing;
        return listing;
    }

    private static int ByName(string left, string right)
    {
        var order = StringComparer.CurrentCultureIgnoreCase.Compare(left, right);
        return order != 0 ? order : string.CompareOrdinal(left, right);
    }

    /// <summary>
    /// Reads an archive's list of entries.  Tries the formats its extension
    /// names first, then every format the library has, as 7-Zip's own window
    /// does - a .zip that is really a 7z still opens.
    /// </summary>
    public static ArchiveIndex Open(string path, string? password, CancellationToken cancellation)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The archive is gone.", path);
        }

        using var session = ArchiveSession.Open(path, password, cancellation);
        var archive = session.Archive;
        Marshal.ThrowExceptionForHR(archive.GetNumberOfItems(out var count));
        var items = new Dictionary<string, ArchiveItem>((int)Math.Min(count, 1_000_000), StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, Folder>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = new Folder() };
        var fallbackName = System.IO.Path.GetFileNameWithoutExtension(path);
        for (uint index = 0; index < count; index++)
        {
            if ((index & 1023) == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            if (Property.Bool(archive, index, PropId.IsAnti))
            {
                continue;
            }

            var inner = Clean(Property.String(archive, index, PropId.Path));
            if (inner.Length == 0)
            {
                // A gzip, an xz: one stream with no name of its own.
                inner = fallbackName;
            }

            var isDirectory = Property.Bool(archive, index, PropId.IsDir);
            var attributes = (FileAttributes)Property.UInt(archive, index, PropId.Attrib);
            if (!isDirectory && (attributes & FileAttributes.Directory) != 0 && Property.ULong(archive, index, PropId.Size) == 0)
            {
                isDirectory = true;
            }

            var item = new ArchiveItem(
                index,
                isDirectory,
                isDirectory ? 0 : (long)Property.ULong(archive, index, PropId.Size),
                Property.FileTimeTicks(archive, index, PropId.MTime),
                (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 && (int)attributes != -1);

            if (items.TryGetValue(inner, out var earlier))
            {
                // The same name twice - a tar appended to, a folder listed
                // before its files: the later file wins, as extracting it
                // would leave it; a folder stays a folder.
                if (earlier.IsDirectory && !isDirectory || !earlier.IsDirectory && isDirectory)
                {
                    continue;
                }

                items[inner] = item;
                ReplaceChild(folders, inner, item);
                continue;
            }

            items[inner] = item;
            AddToParent(folders, items, inner, item);
            if (isDirectory)
            {
                folders.TryAdd(inner, new Folder());
            }
        }

        return new ArchiveIndex(path, info.Length, info.LastWriteTimeUtc.Ticks, session.Format, session.IsSolid, items, folders);
    }

    /// <summary>Adds an entry to its folder, making every folder above it that the archive does not list itself.</summary>
    private static void AddToParent(Dictionary<string, Folder> folders, Dictionary<string, ArchiveItem> items, string inner, ArchiveItem item)
    {
        var slash = inner.LastIndexOf('\\');
        var parent = slash < 0 ? string.Empty : inner[..slash];
        if (!folders.TryGetValue(parent, out var folder))
        {
            folder = new Folder();
            folders[parent] = folder;
            if (!items.ContainsKey(parent))
            {
                var made = new ArchiveItem(uint.MaxValue, true, 0, 0, false);
                items[parent] = made;
                AddToParent(folders, items, parent, made);
            }
        }

        folder.Children.Add((slash < 0 ? inner : inner[(slash + 1)..], item));
    }

    private static void ReplaceChild(Dictionary<string, Folder> folders, string inner, ArchiveItem item)
    {
        var slash = inner.LastIndexOf('\\');
        var parent = slash < 0 ? string.Empty : inner[..slash];
        var name = slash < 0 ? inner : inner[(slash + 1)..];
        if (folders.TryGetValue(parent, out var folder))
        {
            var at = folder.Children.FindIndex(child => string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                folder.Children[at] = (name, item);
            }
        }
    }

    /// <summary>
    /// An entry's path made safe to show and to extract: forward slashes
    /// turned round, no leading separator or drive, and no "." or ".." -
    /// an entry named "..\..\Windows\x" stays inside the folder it goes to.
    /// </summary>
    internal static string Clean(string raw)
    {
        if (raw.Length == 0)
        {
            return raw;
        }

        var parts = raw.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var name = part.TrimEnd(' ', '.');
            if (name.Length == 0 || name == "..")
            {
                continue;
            }

            if (kept.Count == 0 && name.Length == 2 && name[1] == ':')
            {
                continue;
            }

            foreach (var bad in System.IO.Path.GetInvalidFileNameChars())
            {
                if (name.Contains(bad))
                {
                    name = name.Replace(bad, '_');
                }
            }

            kept.Add(name);
        }

        return string.Join('\\', kept);
    }
}

/// <summary>Reading one property of one entry.</summary>
internal static unsafe class Property
{
    public static string String(IInArchive archive, uint index, uint id)
    {
        PropVariant value = default;
        try
        {
            return archive.GetProperty(index, id, (IntPtr)(&value)) == 0 && value.Type == PropVariant.Bstr && value.Pointer != IntPtr.Zero
                ? Marshal.PtrToStringBSTR(value.Pointer)
                : string.Empty;
        }
        finally
        {
            SevenZipLibrary.PropVariantClear(&value);
        }
    }

    public static bool Bool(IInArchive archive, uint index, uint id)
    {
        PropVariant value = default;
        try
        {
            return archive.GetProperty(index, id, (IntPtr)(&value)) == 0 && value.Type == PropVariant.Boolean && value.Bool != 0;
        }
        finally
        {
            SevenZipLibrary.PropVariantClear(&value);
        }
    }

    public static uint UInt(IInArchive archive, uint index, uint id)
    {
        PropVariant value = default;
        try
        {
            if (archive.GetProperty(index, id, (IntPtr)(&value)) != 0)
            {
                return 0;
            }

            return value.Type switch
            {
                PropVariant.UI4 or PropVariant.I4 => value.UInt,
                PropVariant.UI2 => value.UInt & 0xFFFF,
                PropVariant.UI1 => value.UInt & 0xFF,
                _ => 0
            };
        }
        finally
        {
            SevenZipLibrary.PropVariantClear(&value);
        }
    }

    public static ulong ULong(IInArchive archive, uint index, uint id)
    {
        PropVariant value = default;
        try
        {
            if (archive.GetProperty(index, id, (IntPtr)(&value)) != 0)
            {
                return 0;
            }

            return value.Type switch
            {
                PropVariant.UI8 or PropVariant.I8 => (ulong)value.Long,
                PropVariant.UI4 or PropVariant.I4 => value.UInt,
                _ => 0
            };
        }
        finally
        {
            SevenZipLibrary.PropVariantClear(&value);
        }
    }

    /// <summary>A FILETIME as UTC ticks, or zero.</summary>
    public static long FileTimeTicks(IInArchive archive, uint index, uint id)
    {
        PropVariant value = default;
        try
        {
            if (archive.GetProperty(index, id, (IntPtr)(&value)) != 0 || value.Type != PropVariant.FileTime || value.Long <= 0)
            {
                return 0;
            }

            var ticks = value.Long + DateTime.FromFileTimeUtc(0).Ticks;
            return ticks is > 0 and < 3155378975999999999 ? ticks : 0;
        }
        finally
        {
            SevenZipLibrary.PropVariantClear(&value);
        }
    }

    public static bool ArchiveBool(IInArchive archive, uint id)
    {
        PropVariant value = default;
        try
        {
            return archive.GetArchiveProperty(id, (IntPtr)(&value)) == 0 && value.Type == PropVariant.Boolean && value.Bool != 0;
        }
        finally
        {
            SevenZipLibrary.PropVariantClear(&value);
        }
    }
}

/// <summary>An archive open in 7-Zip: the handler, its file and its volumes, all let go of together.</summary>
internal sealed class ArchiveSession : IDisposable
{
    private readonly FileInStream _stream;
    private readonly OpenCallback _callback;

    private ArchiveSession(IInArchive archive, FileInStream stream, OpenCallback callback, Guid format)
    {
        Archive = archive;
        _stream = stream;
        _callback = callback;
        Format = format;
        IsSolid = Property.ArchiveBool(archive, PropId.Solid);
    }

    public IInArchive Archive { get; }

    public Guid Format { get; }

    public bool IsSolid { get; }

    /// <summary>Opens with the format given, or works out which one it is.</summary>
    public static unsafe ArchiveSession Open(string path, string? password, CancellationToken cancellation, Guid? format = null)
    {
        if (SevenZipLibrary.Library is not { } library)
        {
            throw new InvalidOperationException("7-Zip is not installed (7z.dll was not found).");
        }

        var tried = new HashSet<Guid>();
        var order = new List<Guid>();
        if (format is { } known)
        {
            order.Add(known);
        }
        else
        {
            foreach (var extension in ArchiveFormats.ExtensionsOf(path))
            {
                if (library.ByExtension.TryGetValue(extension, out var claimed))
                {
                    order.AddRange(claimed);
                }
            }

            order.AddRange(library.Formats.Select(entry => entry.ClassId));
        }

        var passwordAsked = false;
        foreach (var classId in order)
        {
            if (!tried.Add(classId))
            {
                continue;
            }

            cancellation.ThrowIfCancellationRequested();
            if (SevenZipLibrary.Create(classId) is not { } archive)
            {
                continue;
            }

            var stream = new FileInStream(path);
            var callback = new OpenCallback(path, password, cancellation);
            var maximum = 1UL << 23;
            int result;
            try
            {
                result = archive.Open(stream, (IntPtr)(&maximum), callback);
            }
            catch (Exception ex) when (ex is COMException or SEHException)
            {
                result = HResult.Fail;
            }

            if (result == HResult.Ok)
            {
                return new ArchiveSession(archive, stream, callback, classId);
            }

            passwordAsked |= callback.PasswordWasAsked;
            Release(archive, stream, callback);
            if (result == HResult.Abort)
            {
                cancellation.ThrowIfCancellationRequested();
                if (passwordAsked)
                {
                    throw new ArchivePasswordException(path);
                }
            }

            if (format is not null)
            {
                break;
            }
        }

        if (passwordAsked)
        {
            throw new ArchivePasswordException(path);
        }

        throw new InvalidDataException("Not an archive 7-Zip can open, or it is damaged.");
    }

    private static void Release(IInArchive archive, FileInStream stream, OpenCallback callback)
    {
        try
        {
            archive.Close();
        }
        catch (COMException)
        {
        }

        Marshal.FinalReleaseComObject(archive);
        stream.Dispose();
        callback.Dispose();
    }

    public void Dispose() => Release(Archive, _stream, _callback);
}

/// <summary>The archive's names are encrypted, or a file in it is, and no password - or the wrong one - was given.</summary>
internal sealed class ArchivePasswordException(string path) : IOException($"{System.IO.Path.GetFileName(path)} is protected by a password.")
{
    public string ArchivePath { get; } = path;
}
