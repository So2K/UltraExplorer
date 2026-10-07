using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using UltraExplorer.Models;

namespace UltraExplorer.Services.Archives;

/// <summary>Where a path inside an archive is: the archive's index, the path within it, and the archive's own path as the canvas names it.</summary>
internal readonly record struct ArchiveLocation(ArchiveIndex Index, string Inner, string ArchivePath);

/// <summary>How far an extraction has got, in bytes of what it writes.</summary>
public readonly record struct ArchiveProgress(long Done, long Total);

/// <summary>
/// Archives as folders.  A zip, a 7z, a rar - anything 7-Zip opens - is a
/// folder on the canvas: zoomed into, read like any other, its files opened
/// with a double-click, dragged or copied out.  Under the hood every archive
/// is listed once and the list kept until the file changes; taking files out
/// runs on several threads at once wherever the format lets one file be
/// decoded without the ones before it, which is every format but a solid one.
///
/// Paths inside an archive are the archive's path and the path within it:
/// C:\x\a.zip\docs\readme.txt.  An archive inside an archive is taken out to
/// the temporary folder the first time it is gone into, and gone into from
/// there - C:\x\a.zip\b.tar.gz\b.tar\src is a path like any other.
/// </summary>
public static class ArchiveService
{
    private const int CachedIndexes = 48;
    internal const long MaximumAutomaticNestedBytes = 512L << 20;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (ArchiveIndex Index, long Used)> Indexes = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> Passwords = new(StringComparer.Ordinal);
    private static readonly object PasswordGate = new();
    private static long _uses;
    private static int _sweptTemp;
    private static readonly string TempSession = Guid.NewGuid().ToString("N");
    private static readonly ConcurrentDictionary<string, object> ExtractionLocks = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ArchiveIndex> LocatedArchives = new(StringComparer.Ordinal);

    /// <summary>Whether archives are shown as folders at all (Settings).  When off, they are files again.</summary>
    public static bool BrowseArchives { get; set; }

    /// <summary>Whether 7-Zip's library was found.  Without it archives stay files.</summary>
    public static bool IsAvailable => SevenZipLibrary.IsAvailable;

    /// <summary>
    /// Asks for an archive's password, given the archive's name; null when
    /// the user would rather not.  Called from any thread - whoever sets it
    /// brings the question to the window's own.
    /// </summary>
    public static Func<string, string?>? PasswordPrompt { get; set; }
    private static readonly Func<string, string?> NoPasswordPrompt = static _ => null;
    private static Func<string, string?> CapturePrompt(Func<string, string?>? prompt) => prompt ?? PasswordPrompt ?? NoPasswordPrompt;

    /// <summary>Where files taken out to be opened, dragged or copied are put.</summary>
    public static string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "UltraExplorer", "Archives");

    // ---- where a path is ---------------------------------------------------------------

    /// <summary>
    /// Whether any part of the path is named like an archive - the test that
    /// costs nothing, made before anything touches the disk.
    /// </summary>
    public static bool MayInvolveArchive(string path)
    {
        return BrowseArchives && MayContainArchiveName(path);
    }

    internal static bool MayContainArchiveName(string path)
    {
        if (path.Length < 4)
        {
            return false;
        }

        var span = path.AsSpan();
        var start = 0;
        while (start < span.Length)
        {
            var end = span[start..].IndexOf('\\');
            var segment = end < 0 ? span[start..] : span.Slice(start, end);
            if (segment.Length > 2 && ArchiveFormats.IsBrowsable(segment))
            {
                return true;
            }

            if (end < 0)
            {
                break;
            }

            start += end + 1;
        }

        return false;
    }

    /// <summary>Whether the path is inside an archive - a file or a folder that is not on disk itself, but in one.</summary>
    public static bool IsInsideArchive(string path)
    {
        if (!MayContainArchiveName(path) || !IsAvailable || File.Exists(path) || Directory.Exists(path))
        {
            return false;
        }

        return ArchiveFileOf(path) is not null;
    }

    /// <summary>Whether any of the paths is inside an archive.</summary>
    public static bool AnyInsideArchive(IEnumerable<string> paths) => paths.Any(IsInsideArchive);

    /// <summary>Whether the file on disk is an archive the canvas opens as a folder.</summary>
    public static bool IsArchiveFile(string path) => IsArchiveFile(path, BrowseArchives);
    public static bool IsArchiveFile(string path, bool archivesEnabled) =>
        archivesEnabled && IsAvailable && ArchiveFormats.IsBrowsable(Path.GetFileName(path.AsSpan())) && File.Exists(path);

    /// <summary>The archive on disk a path goes through, or null: the first part of it that is a file named like one.</summary>
    private static string? ArchiveFileOf(string path)
    {
        var start = 0;
        while (start < path.Length)
        {
            var end = path.IndexOf('\\', start);
            var stop = end < 0 ? path.Length : end;
            if (stop - start > 2 && ArchiveFormats.IsBrowsable(path.AsSpan(start, stop - start)))
            {
                var prefix = path[..stop];
                if (File.Exists(prefix))
                {
                    return prefix;
                }
            }

            if (end < 0)
            {
                break;
            }

            start = end + 1;
        }

        return null;
    }

    /// <summary>
    /// Finds the archive a path is in and the path inside it, opening (or
    /// taking out, for one inside another) whatever archives it goes
    /// through.  False when the path goes through no archive.  Slow the
    /// first time for each archive; off the UI thread.
    /// </summary>
    internal static bool TryLocate(string path, out ArchiveLocation location, CancellationToken cancellation = default, bool allowPrompt = true, bool acceptedOperation = false, Func<string, string?>? passwordPrompt = null)
    {
        location = default;
        if ((!acceptedOperation && !BrowseArchives) || !MayContainArchiveName(path) || !IsAvailable || ArchiveFileOf(path) is not { } file)
        {
            return false;
        }

        var index = IndexOf(file, cancellation);
        var archivePath = file;
        LocatedArchives[archivePath] = index;
        var rest = path.Length > file.Length ? path[(file.Length + 1)..].TrimEnd('\\') : string.Empty;

        // An archive inside this one: taken out, and gone on into.
        while (rest.Length > 0)
        {
            var nested = -1;
            for (var at = 0; at <= rest.Length; at++)
            {
                if (at < rest.Length && rest[at] != '\\')
                {
                    continue;
                }

                var prefix = rest[..at];
                var nameStart = prefix.LastIndexOf('\\') + 1;
                if (ArchiveFormats.IsBrowsable(prefix.AsSpan(nameStart))
                    && index.TryGetItem(prefix, out var item) && !item.IsDirectory)
                {
                    nested = at;
                    break;
                }
            }

            if (nested < 0)
            {
                break;
            }

            var inner = rest[..nested];
            var copy = TakeOutNested(index, inner, cancellation, allowPrompt, passwordPrompt);
            index = IndexOf(copy, cancellation);
            archivePath = archivePath + '\\' + inner;
            LocatedArchives[archivePath] = index;
            rest = nested < rest.Length ? rest[(nested + 1)..] : string.Empty;
        }

        location = new ArchiveLocation(index, rest, archivePath);
        return true;
    }

    /// <summary>An archive's index, from the cache while the file has not changed.</summary>
    private static ArchiveIndex IndexOf(string file, CancellationToken cancellation)
    {
        var info = new FileInfo(file);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The archive is gone.", file);
        }

        lock (Gate)
        {
            if (Indexes.TryGetValue(file, out var cached) && cached.Index.Length == info.Length && cached.Index.WriteTicks == info.LastWriteTimeUtc.Ticks)
            {
                Indexes[file] = (cached.Index, ++_uses);
                return cached.Index;
            }
        }

        Passwords.TryGetValue(file, out var password);
        var index = ArchiveIndex.Open(file, password, cancellation);
        lock (Gate)
        {
            Indexes[file] = (index, ++_uses);
            if (Indexes.Count > CachedIndexes)
            {
                var oldest = Indexes.MinBy(pair => pair.Value.Used).Key;
                var removedIndex = Indexes[oldest].Index;
                Indexes.Remove(oldest);
                foreach (var pair in LocatedArchives.Where(pair => ReferenceEquals(pair.Value, removedIndex)))
                    LocatedArchives.TryRemove(pair.Key, out _);
            }
        }

        return index;
    }

    /// <summary>An archive inside an archive, taken out to the temporary folder once and kept there while its outer one does not change.</summary>
    private static string TakeOutNested(ArchiveIndex outer, string inner, CancellationToken cancellation, bool allowPrompt, Func<string, string?>? passwordPrompt)
    {
        outer.TryGetItem(inner, out var item);
        if (!allowPrompt && item.Size > MaximumAutomaticNestedBytes)
            throw new InvalidDataException("This nested archive is larger than 512 MiB. Extract it to a folder first to browse it.");
        var folder = Path.Combine(TempRoot, "nested", TempSession + "-" + Stamp(outer.Path, outer.WriteTicks, inner, outer.Length));
        var copy = Path.Combine(folder, Path.GetFileName(inner));
        lock (ExtractionLocks.GetOrAdd(folder, static _ => new object()))
        {
            cancellation.ThrowIfCancellationRequested();
            EnsureSafeOutput(copy);
            if (File.Exists(copy) && new FileInfo(copy).Length == item.Size)
            {
                ArchiveZoneHelper.Propagate(outer.Path, copy);
                return copy;
            }
            Directory.CreateDirectory(folder);
            var staging = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".partial");
            try
            {
                ExtractItems(outer, [(item, staging)], cancellation, null, allowPrompt, passwordPrompt);
                File.Move(staging, copy, overwrite: true);
            }
            finally
            {
                if (File.Exists(staging)) File.Delete(staging);
            }

            return copy;
        }
    }

    // ---- reading -----------------------------------------------------------------------

    /// <summary>
    /// The canvas's read of a folder inside an archive, or of an archive
    /// itself; null for a path that goes through no archive, which is read
    /// from disk as always.
    /// </summary>
    public static NestedListing? TryRead(string path, CancellationToken cancellation) => TryRead(path, cancellation, BrowseArchives);

    public static NestedListing? TryRead(string path, CancellationToken cancellation, bool archivesEnabled)
    {
        if (!archivesEnabled || !MayContainArchiveName(path) || !IsAvailable)
        {
            return null;
        }

        try
        {
            if (!TryLocate(path, out var location, cancellation, allowPrompt: false, acceptedOperation: true))
            {
                return null;
            }

            return location.Index.List(location.Inner);
        }
        catch (ArchivePasswordException)
        {
            return NestedListing.Failed("Password protected — double-click to enter it");
        }
        catch (InvalidDataException ex)
        {
            return NestedListing.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or COMException)
        {
            return NestedListing.Failed(ex.Message) with { IsRetryable = ex is IOException and not FileNotFoundException };
        }
    }

    /// <summary>Asks for the password of an archive whose names are encrypted, and keeps it; true when one was given.</summary>
    public static bool AskPassword(string archivePath, Func<string, string?>? passwordPrompt = null)
    {
        var file = ArchiveFileOf(archivePath) ?? archivePath;
        if ((passwordPrompt ?? PasswordPrompt)?.Invoke(Path.GetFileName(archivePath)) is not { } password)
        {
            return false;
        }

        Passwords[file] = password;
        lock (Gate)
        {
            Indexes.Remove(file);
        }

        return true;
    }

    /// <summary>Resolves encrypted nested headers off the window thread, then stores the password against their physical cache file.</summary>
    public static async Task<bool> AskPasswordAsync(string archivePath, Func<string, string?>? passwordPrompt = null)
    {
        var prompt = CapturePrompt(passwordPrompt);
        var file = await Task.Run(() =>
        {
            try
            {
                return TryLocate(archivePath, out var location, allowPrompt: false, acceptedOperation: true) ? location.Index.Path : archivePath;
            }
            catch (ArchivePasswordException protectedArchive)
            {
                return protectedArchive.ArchivePath;
            }
        });
        return AskPassword(file, prompt);
    }

    /// <summary>The size and date of a file inside an archive, for a selection that wants to show them.</summary>
    public static bool TryGetFile(string path, out long size, out long modifiedTicks)
    {
        size = modifiedTicks = 0;
        try
        {
            if (TryLocateCached(path, out var location) && location.Index.TryGetItem(location.Inner, out var item))
            {
                size = item.Size;
                modifiedTicks = item.ModifiedTicks;
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or COMException)
        {
        }

        return false;
    }

    /// <summary>
    /// Whether a path is something the canvas shows as a folder: a folder on
    /// disk, an archive, or a folder inside an archive.
    /// </summary>
    public static bool IsFolderLike(string path) => IsFolderLike(path, BrowseArchives);
    public static bool IsFolderLike(string path, bool archivesEnabled) =>
        Directory.Exists(path) || IsArchiveFile(path, archivesEnabled) || archivesEnabled && IsInsideArchive(path) && IsFolderInArchive(path);

    /// <summary>Whether a path inside an archive is a folder there.</summary>
    public static bool IsFolderInArchive(string path)
    {
        try
        {
            return TryLocateCached(path, out var location) && location.Index.IsFolder(location.Inner);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or COMException)
        {
            return false;
        }
    }

    /// <summary>Used only on a worker for direct navigation before a folder's listing has been cached.</summary>
    internal static bool IsFolderLikeForNavigation(string path) => Directory.Exists(path) || IsArchiveFile(path, archivesEnabled: true)
        || IsInsideArchive(path) && TryLocate(path, out var location, allowPrompt: false, acceptedOperation: true) && location.Index.IsFolder(location.Inner);

    private static bool TryLocateCached(string path, out ArchiveLocation location)
    {
        // Selection and context-menu metadata runs on the window thread. It must never extract a nested archive or wait on a password worker.
        location = default;
        var best = string.Empty;
        ArchiveIndex? index = null;
        foreach (var pair in LocatedArchives)
        {
            if (pair.Key.Length > best.Length && (path.Equals(pair.Key, StringComparison.Ordinal)
                || path.StartsWith(pair.Key + '\\', StringComparison.Ordinal)))
            {
                best = pair.Key;
                index = pair.Value;
            }
        }

        if (index is null) return false;
        location = new ArchiveLocation(index, path.Length == best.Length ? string.Empty : path[(best.Length + 1)..].TrimEnd('\\'), best);
        return true;
    }

    // ---- taking things out ---------------------------------------------------------------

    /// <summary>
    /// A file inside an archive, taken out to be opened.  A program - an
    /// .exe, a script, an installer - comes out with everything in its
    /// folder, the way it would run unpacked.  Taken out again only when the
    /// archive has changed.
    /// </summary>
    public static async Task<string> ExtractForOpenAsync(string path, IProgress<ArchiveProgress>? progress, CancellationToken cancellation, Func<string, string?>? passwordPrompt = null)
    {
        var prompt = CapturePrompt(passwordPrompt);
        return await Task.Run(() =>
        {
            SweepTempOnce();
            if (!TryLocate(path, out var location, cancellation, acceptedOperation: true, passwordPrompt: prompt) || !location.Index.TryGetItem(location.Inner, out var item))
            {
                throw new FileNotFoundException("Not found in the archive.", path);
            }

            var root = Path.Combine(TempRoot, "open", TempSession + "-" + Stamp(location.Index.Path, location.Index.WriteTicks, string.Empty, location.Index.Length));
            var target = Path.Combine(root, location.Inner);
            lock (ExtractionLocks.GetOrAdd(root, static _ => new object()))
            {
                cancellation.ThrowIfCancellationRequested();
                EnsureSafeOutput(target);
                if (File.Exists(target) && new FileInfo(target).Length == item.Size)
                {
                    ArchiveZoneHelper.Propagate(location.Index.Path, target);
                    return target;
                }

                var staging = root + ".partial-" + Guid.NewGuid().ToString("N");
                var slash = location.Inner.LastIndexOf('\\');
                var parent = slash < 0 ? string.Empty : location.Inner[..slash];
                var work = new List<(ArchiveItem, string)>();
                if (NativeShellService.IsExecutable(path) || IsInstaller(path))
                {
                    foreach (var (inner, below) in location.Index.Below(parent))
                        work.Add((below, Path.Combine(staging, inner)));
                }
                else
                {
                    work.Add((item, Path.Combine(staging, location.Inner)));
                }

                try
                {
                    ExtractItems(location.Index, work, cancellation, progress, passwordPrompt: prompt);
                    foreach (var (_, staged) in work)
                    {
                        var output = Path.Combine(root, Path.GetRelativePath(staging, staged));
                        EnsureSafeOutput(output);
                        if (Directory.Exists(staged)) Directory.CreateDirectory(output);
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                            File.Move(staged, output, overwrite: true);
                        }
                    }

                    return target;
                }
                finally
                {
                    if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                }
            }
        }, cancellation);
    }

    private static bool IsInstaller(string path) =>
        Path.GetExtension(path) is { } extension
        && (extension.Equals(".msi", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Items inside archives copied into a folder on disk, each under its own
    /// name - made unique when the folder has one already - with everything
    /// below it.  The items can come from different archives.  Returns what
    /// was made, one path per item.
    /// </summary>
    public static Task<IReadOnlyList<string>> ExtractToAsync(IReadOnlyList<string> paths, string destination, IProgress<ArchiveProgress>? progress, CancellationToken cancellation, Func<string, string?>? passwordPrompt = null)
    {
        var prompt = CapturePrompt(passwordPrompt);
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            var made = new List<string>();
            var groups = new Dictionary<ArchiveIndex, List<(ArchiveItem, string)>>(ReferenceEqualityComparer.Instance);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                if (!TryLocate(path, out var location, cancellation, acceptedOperation: true, passwordPrompt: prompt))
                {
                    throw new FileNotFoundException("Not inside an archive.", path);
                }

                var name = location.Inner.Length == 0 ? StemOf(location.ArchivePath) : Path.GetFileName(location.Inner);
                var top = UniquePath(destination, name, taken);
                made.Add(top);
                if (!groups.TryGetValue(location.Index, out var work))
                {
                    groups[location.Index] = work = [];
                }

                var cut = location.Inner.Length == 0 ? 0 : location.Inner.Length + 1;
                foreach (var (inner, item) in location.Index.Below(location.Inner))
                {
                    var rest = inner.Length > cut ? inner[cut..] : string.Empty;
                    work.Add((item, rest.Length == 0 && location.Inner.Length > 0 ? top : Path.Combine(top, rest)));
                }

                if (location.Inner.Length == 0)
                {
                    Directory.CreateDirectory(top);
                }
            }

            var total = new ProgressSum(progress, groups.Values.Sum(work => work.Sum(entry => entry.Item1.Size)));
            foreach (var (index, work) in groups)
            {
                ExtractItems(index, work, cancellation, total, passwordPrompt: prompt);
            }

            return made;
        }, cancellation);
    }

    /// <summary>
    /// A whole archive unpacked next to it (or into <paramref name="destination"/>):
    /// what is inside straight into the folder when it is one folder or one file
    /// already, else into a new folder named after the archive - so unpacking
    /// never scatters files, and never makes a folder in a folder.
    /// Returns the folder or file that came out.
    /// </summary>
    public static Task<string> ExtractArchiveAsync(string archivePath, string? destination, bool alwaysOwnFolder, IProgress<ArchiveProgress>? progress, CancellationToken cancellation, Func<string, string?>? passwordPrompt = null)
    {
        var prompt = CapturePrompt(passwordPrompt);
        return Task.Run(() =>
        {
            if (!TryLocate(archivePath, out var location, cancellation, acceptedOperation: true, passwordPrompt: prompt) || location.Inner.Length != 0)
            {
                throw new InvalidDataException("Not an archive.");
            }

            var index = location.Index;
            destination ??= Path.GetDirectoryName(archivePath) is { Length: > 0 } beside && !IsInsideArchive(beside)
                ? beside
                : throw new InvalidOperationException("No folder to unpack into.");
            var top = index.List(string.Empty);
            var single = top.Folders.Count + top.Files.Count == 1 && top.FileCount == top.Files.Count;
            var work = new List<(ArchiveItem, string)>();
            string result;
            if (single && !alwaysOwnFolder)
            {
                var name = top.Folders.Count == 1 ? top.Folders[0].Name : top.Files[0].Name;
                result = UniquePath(destination, name, null);
                var cut = name.Length + 1;
                foreach (var (inner, item) in index.Below(name))
                {
                    work.Add((item, inner.Length > cut ? Path.Combine(result, inner[cut..]) : result));
                }
            }
            else
            {
                result = UniquePath(destination, StemOf(archivePath), null);
                Directory.CreateDirectory(result);
                foreach (var (inner, item) in index.Below(string.Empty))
                {
                    work.Add((item, Path.Combine(result, inner)));
                }
            }

            ExtractItems(index, work, cancellation, new ProgressSum(progress, work.Sum(entry => entry.Item1.Size)), passwordPrompt: prompt);
            return result;
        }, cancellation);
    }

    /// <summary>Items inside archives taken out to a fresh temporary folder: what a drag or a copy to the clipboard carries.</summary>
    public static async Task<IReadOnlyList<string>> ExtractToTempAsync(IReadOnlyList<string> paths, IProgress<ArchiveProgress>? progress, CancellationToken cancellation, Func<string, string?>? passwordPrompt = null)
    {
        var prompt = CapturePrompt(passwordPrompt);
        SweepTempOnce();
        var folder = Path.Combine(TempRoot, "out", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder);
        return await ExtractToAsync(paths, folder, progress, cancellation, prompt);
    }

    /// <summary>
    /// Takes items out of one archive.  Folders are made first, then the files
    /// are decoded: for an archive that is not solid and holds enough to be
    /// worth it, by several threads, each with its own handle on the archive
    /// and its share of the bytes - the biggest files dealt out first, so the
    /// threads finish together.
    /// </summary>
    private static void ExtractItems(ArchiveIndex index, IReadOnlyList<(ArchiveItem Item, string Output)> work, CancellationToken cancellation, IProgress<ArchiveProgress>? progress, bool allowPrompt = true, Func<string, string?>? passwordPrompt = null)
    {
        var files = new List<(ArchiveItem Item, string Output)>(work.Count);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (item, output) in work)
        {
            cancellation.ThrowIfCancellationRequested();
            EnsureSafeOutput(output);
            if (!outputNames.Add(output))
                throw new InvalidDataException("Archive entries have conflicting output names. Extract them individually to preserve both files.");
            if (item.IsDirectory || item.Index == uint.MaxValue)
            {
                folders.Add(output);
                continue;
            }

            if (Path.GetDirectoryName(output) is { Length: > 0 } parent)
            {
                folders.Add(parent);
            }

            files.Add((item, output));
        }

        foreach (var folder in folders)
        {
            Directory.CreateDirectory(folder);
        }

        if (files.Count == 0)
        {
            return;
        }

        var sum = progress as ProgressSum ?? new ProgressSum(progress, files.Sum(file => file.Item.Size));
        var bytes = files.Sum(file => file.Item.Size);
        var threads = index.IsSolid ? 1 : Math.Min(Math.Min(Environment.ProcessorCount, 8), Math.Max(1, (int)Math.Min(files.Count / 4, bytes / (8L << 20))));
        if (threads <= 1)
        {
            RunExtract(index, files, cancellation, sum, allowPrompt, passwordPrompt);
            return;
        }

        var shares = Enumerable.Range(0, threads).Select(_ => new List<(ArchiveItem, string)>()).ToArray();
        var loads = new long[threads];
        foreach (var file in files.OrderByDescending(file => file.Item.Size))
        {
            var least = Array.IndexOf(loads, loads.Min());
            shares[least].Add(file);
            loads[least] += Math.Max(file.Item.Size, 4096);
        }

        var failures = new ConcurrentQueue<Exception>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Parallel.ForEach(shares, new ParallelOptions { MaxDegreeOfParallelism = threads }, share =>
        {
            try
            {
                RunExtract(index, share, stop.Token, sum, allowPrompt, passwordPrompt);
            }
            catch (Exception ex)
            {
                failures.Enqueue(ex);
                stop.Cancel();
            }
        });

        cancellation.ThrowIfCancellationRequested();
        if (failures.FirstOrDefault(ex => ex is not OperationCanceledException) is { } failure)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>One handle on the archive, one call to 7-Zip for every file of a share - in the archive's own order, which is the fast one.</summary>
    private static void RunExtract(ArchiveIndex index, IReadOnlyList<(ArchiveItem Item, string Output)> files, CancellationToken cancellation, ProgressSum progress, bool allowPrompt, Func<string, string?>? passwordPrompt)
    {
        for (var attempt = 0; ; attempt++)
        {
            var archiveFile = index.Path;
            var zone = ArchiveZoneHelper.Capture(archiveFile);
            using var session = ArchiveSession.Open(archiveFile, Passwords.GetValueOrDefault(archiveFile), cancellation, index.Format);
            var targets = new Dictionary<uint, (string Output, long Ticks, long Size)>(files.Count);
            foreach (var (item, output) in files)
            {
                if (!targets.TryAdd(item.Index, (output, item.ModifiedTicks, item.Size)))
                {
                    // Parent+child or duplicate selections can request the same archive entry at multiple destinations.
                    RunExtract(index, [(item, output)], cancellation, progress, allowPrompt, passwordPrompt);
                }
            }

            var indices = targets.Keys.Order().ToArray();
            var callback = new ExtractCallback(targets, () => PasswordFor(archiveFile, allowPrompt, passwordPrompt), progress, cancellation, zone);
            int result;
            try
            {
                result = session.Archive.Extract(indices, (uint)indices.Length, 0, callback);
            }
            finally
            {
                callback.CloseCurrent(failed: true);
            }

            cancellation.ThrowIfCancellationRequested();
            if (callback.WrongPassword || result == HResult.Abort && callback.PasswordRefused)
            {
                Passwords.TryRemove(archiveFile, out _);
                if (callback.PasswordRefused || attempt > 0)
                {
                    throw new ArchivePasswordException(archiveFile);
                }

                continue;
            }

            if (result != HResult.Ok)
            {
                throw new IOException($"7-Zip could not extract from {Path.GetFileName(archiveFile)} (0x{result:X8}).");
            }

            if (callback.Failed > 0)
            {
                throw new InvalidDataException($"{callback.Failed:N0} file(s) in {Path.GetFileName(archiveFile)} could not be extracted: {callback.FirstProblem}.");
            }

            return;
        }
    }

    /// <summary>The password for an archive, asked for once and shared by every thread taking files out of it.</summary>
    private static string? PasswordFor(string archiveFile, bool allowPrompt, Func<string, string?>? passwordPrompt)
    {
        if (Passwords.TryGetValue(archiveFile, out var known))
        {
            return known;
        }

        if (!allowPrompt) return null;

        lock (PasswordGate)
        {
            if (Passwords.TryGetValue(archiveFile, out known))
            {
                return known;
            }

            if (passwordPrompt?.Invoke(Path.GetFileName(archiveFile)) is not { } given)
            {
                return null;
            }

            Passwords[archiveFile] = given;
            return given;
        }
    }

    // ---- names -------------------------------------------------------------------------

    /// <summary>An archive's name without its archive extensions: "site.tar.gz" is "site", "x.7z.001" is "x".</summary>
    public static string StemOf(string archivePath)
    {
        var name = Path.GetFileName(archivePath.TrimEnd('\\'));
        while (Path.GetExtension(name) is { Length: > 1 } extension && ArchiveFormats.IsBrowsable("x" + extension) || name.EndsWith(".001", StringComparison.Ordinal))
        {
            var shorter = Path.GetFileNameWithoutExtension(name);
            if (shorter.Length == 0)
            {
                break;
            }

            name = shorter;
        }

        return name;
    }

    /// <summary><paramref name="name"/> in <paramref name="folder"/>, or "name (2)" and on when that is taken - on disk or by an earlier item of the same copy.</summary>
    private static string UniquePath(string folder, string name, HashSet<string>? taken)
    {
        var path = Path.Combine(folder, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var number = 2; File.Exists(path) || Directory.Exists(path) || taken is not null && taken.Contains(path); number++)
        {
            path = Path.Combine(folder, $"{stem} ({number}){extension}");
        }

        taken?.Add(path);
        return path;
    }

    /// <summary>Never follow a pre-existing file, junction or symbolic-link output to another location.</summary>
    internal static void EnsureSafeOutput(string output)
    {
        for (var current = Path.GetFullPath(output); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // OneDrive placeholders and projected folders carry a reparse point too,
                    // but remain ordinary paths. Only actual links/junctions report a target.
                    FileSystemInfo entry = (attributes & FileAttributes.Directory) != 0
                        ? new DirectoryInfo(current) : new FileInfo(current);
                    if (entry.LinkTarget is not null)
                        throw new IOException($"Cannot extract through a symbolic link or junction: {current}");
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static string Stamp(string path, long ticks, string inner, long length)
    {
        var text = $"{path}|{ticks}|{length}|{inner}";
        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>What earlier sessions left in the temporary folder, cleared once a session, in the background.</summary>
    private static void SweepTempOnce()
    {
        if (Interlocked.Exchange(ref _sweptTemp, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var kind in new[] { "out", "open", "nested" })
            {
                try
                {
                    var folder = new DirectoryInfo(Path.Combine(TempRoot, kind));
                    if (!folder.Exists)
                    {
                        continue;
                    }

                    foreach (var old in folder.EnumerateDirectories().Where(entry => entry.LastWriteTimeUtc < cutoff))
                    {
                        try
                        {
                            old.Delete(recursive: true);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        });
    }

    /// <summary>Progress over several handles and threads, summed and passed on as one.</summary>
    internal sealed class ProgressSum(IProgress<ArchiveProgress>? inner, long total) : IProgress<ArchiveProgress>
    {
        private long _done;

        public void Add(long bytes)
        {
            var done = Interlocked.Add(ref _done, bytes);
            inner?.Report(new ArchiveProgress(done, total));
        }

        public void Report(ArchiveProgress value) => inner?.Report(value);
    }
}

/// <summary>
/// 7-Zip asking where each file goes: a file opened with a large buffer and
/// its full size set aside up front, so writing never fragments, then given
/// the archive's date when it is done.  A file that fails is removed rather
/// than left half written.
/// </summary>
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ExtractCallback(
    Dictionary<uint, (string Output, long Ticks, long Size)> targets,
    Func<string?> password,
    ArchiveService.ProgressSum progress,
    CancellationToken cancellation,
    ArchiveZoneHelper? zone = null) : IArchiveExtractCallback, ICryptoGetTextPassword
{
    private FileStream? _current;
    private (string Output, long Ticks, long Size) _target;
    private ulong _completed;
    private bool _passwordGiven;

    public int Failed { get; private set; }

    public string FirstProblem { get; private set; } = string.Empty;

    public bool WrongPassword { get; private set; }

    public bool PasswordRefused { get; private set; }

    public int SetTotal(ulong total) => HResult.Ok;

    public unsafe int SetCompleted(IntPtr completeValue)
    {
        if (completeValue != IntPtr.Zero)
        {
            var now = *(ulong*)completeValue;
            if (now > _completed)
            {
                progress.Add((long)(now - _completed));
                _completed = now;
            }
        }

        return cancellation.IsCancellationRequested ? HResult.Abort : HResult.Ok;
    }

    public int GetStream(uint index, out ISequentialOutStream? outStream, int askExtractMode)
    {
        outStream = null;
        if (askExtractMode != 0 || !targets.TryGetValue(index, out var target))
        {
            return HResult.Ok;
        }

        try
        {
            if (cancellation.IsCancellationRequested) return HResult.Abort;
            ArchiveService.EnsureSafeOutput(target.Output);
            var stream = new FileStream(target.Output, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            _current = stream;
            _target = target;
            if (target.Size > 0)
            {
                stream.SetLength(target.Size);
            }

            outStream = new StreamOutStream(stream);
            return HResult.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CloseCurrent(failed: true);
            Failed++;
            if (FirstProblem.Length == 0)
            {
                FirstProblem = ex.Message;
            }

            return HResult.Fail;
        }
    }

    public int PrepareOperation(int askExtractMode) => HResult.Ok;

    public int SetOperationResult(int operationResult)
    {
        // 9 is a wrong password; a data or CRC error with one given is
        // what an encrypted zip says instead.
        if (operationResult == 9 || _passwordGiven && operationResult is 2 or 3)
        {
            WrongPassword = true;
        }

        if (operationResult != 0)
        {
            Failed++;
            if (FirstProblem.Length == 0)
            {
                FirstProblem = operationResult switch
                {
                    1 => "unsupported compression method",
                    2 => "data error",
                    3 => "CRC failed",
                    4 => "data unavailable",
                    5 => "unexpected end of data",
                    9 => "wrong password",
                    _ => $"error {operationResult}"
                };
            }
        }

        CloseCurrent(failed: operationResult != 0);
        return HResult.Ok;
    }

    public void CloseCurrent(bool failed)
    {
        if (_current is not { } stream)
        {
            return;
        }

        _current = null;
        try
        {
            if (!failed && stream.Length != stream.Position)
            {
                stream.SetLength(stream.Position);
            }

            if (!failed && _target.Ticks > 0)
            {
                File.SetLastWriteTimeUtc(stream.SafeFileHandle, new DateTime(_target.Ticks, DateTimeKind.Utc));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        finally
        {
            stream.Dispose();
        }

        if (!failed && zone is not null)
        {
            try
            {
                // Mark the completed file before staging can publish it or
                // an Open/drag operation can return a physical path.
                zone.Apply(_target.Output);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                failed = true;
                Failed++;
                if (FirstProblem.Length == 0)
                    FirstProblem = $"Could not preserve the archive's Windows origin metadata: {ex.Message}";
            }
            if (!failed && _target.Ticks > 0)
            {
                try { File.SetLastWriteTimeUtc(_target.Output, new DateTime(_target.Ticks, DateTimeKind.Utc)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }

        if (failed)
        {
            try
            {
                ArchiveService.EnsureSafeOutput(_target.Output);
                // Callback targets are fresh UniquePath outputs or private
                // staging files. Only these failed outputs may be removed.
                var attributes = File.GetAttributes(_target.Output);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(_target.Output, attributes & ~FileAttributes.ReadOnly);
                File.Delete(_target.Output);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public int CryptoGetTextPassword(out string? text)
    {
        text = password();
        _passwordGiven = text is not null;
        PasswordRefused = text is null;
        return text is null ? HResult.Abort : HResult.Ok;
    }
}
