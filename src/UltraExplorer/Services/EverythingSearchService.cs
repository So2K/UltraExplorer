using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UltraExplorer.Models;

namespace UltraExplorer.Services;

/// <summary>
/// Search through Everything (voidtools), which keeps an index of every file on
/// every NTFS volume and answers a query in single-digit milliseconds.
///
/// Walking the tree ourselves cannot compete: finding a name under
/// <c>C:\Windows</c> means reading a hundred thousand directory entries, and
/// under <c>C:\</c> it means reading them all.  Everything has already read the
/// Master File Table and watches the USN journal, so the answer is a lookup.
///
/// This talks to it through the official SDK, which is a thin IPC client:
/// nothing is indexed here and no second copy of anything is kept.  The library
/// is looked for beside this program and in the usual install locations; when it
/// is not there, or Everything is not running, <see cref="IsAvailable"/> is false
/// and the caller falls back to walking the tree.
/// </summary>
public sealed class EverythingSearchService
{
    private const string Library = "Everything64.dll";

    private const int Ok = 0;
    private const int ErrorMemory = 1;
    private const int ErrorIpc = 2;
    private const int ErrorRegisterClass = 3;
    private const int ErrorCreateWindow = 4;
    private const int ErrorCreateThread = 5;
    private const int ErrorInvalidIndex = 6;
    private const int ErrorInvalidCall = 7;

    private const uint SortNameAscending = 1;

    /// <summary>
    /// The SDK keeps the query, the flags and the result set in one global state,
    /// so two searches at once would read each other's results.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static string? _resolvedPath;
    private static bool _resolverInstalled;

    public EverythingSearchService()
    {
        LibraryPath = Locate();
        InstallResolver();
    }

    /// <summary>Where the SDK library was found, or null.</summary>
    public string? LibraryPath { get; }

    /// <summary>
    /// True when the library is present and Everything is actually running.  Both
    /// are checked, because the library alone answers every call with
    /// <c>EVERYTHING_ERROR_IPC</c>.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            if (LibraryPath is null)
            {
                return false;
            }

            try
            {
                return Everything_GetMajorVersion() > 0;
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                return false;
            }
        }
    }

    /// <summary>What to tell the user when this cannot be used.</summary>
    public string UnavailableReason => LibraryPath is null
        ? $"{Library} was not found. Install Everything from voidtools.com, or put {Library} next to UltraExplorer.exe."
        : "Everything is installed but not running.";

    /// <summary>
    /// Runs one query.  <paramref name="scope"/> limits it to a folder; passing
    /// null searches every indexed volume, which is the thing Everything is for.
    /// </summary>
    public async Task<IReadOnlyList<SearchResultViewModel>> SearchAsync(
        string query,
        string? scope,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run<IReadOnlyList<SearchResultViewModel>>(
                () => Query(query, scope, maximumResults, cancellationToken),
                cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static IReadOnlyList<SearchResultViewModel> Query(
        string query,
        string? scope,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        try
        {
            Everything_SetSearchW(BuildSearch(query, scope));
            Everything_SetMatchCase(false);
            Everything_SetMatchWholeWord(false);
            Everything_SetRegex(false);

            // The path has to be matched for a folder-scoped search to mean
            // anything; without it the scope term would be looked for in names.
            Everything_SetMatchPath(!string.IsNullOrEmpty(scope));
            Everything_SetSort(SortNameAscending);
            Everything_SetOffset(0);
            Everything_SetMax((uint)Math.Max(1, maximumResults));

            if (!Everything_QueryW(true))
            {
                return [];
            }

            cancellationToken.ThrowIfCancellationRequested();

            var count = Everything_GetNumResults();
            var results = new List<SearchResultViewModel>((int)count);
            var buffer = new StringBuilder(1024);

            for (uint index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                buffer.Clear();
                if (Everything_GetResultFullPathNameW(index, buffer, (uint)buffer.Capacity) == 0)
                {
                    continue;
                }

                var path = buffer.ToString();
                if (path.Length == 0)
                {
                    continue;
                }

                var isDirectory = Everything_IsFolderResult(index);
                results.Add(new SearchResultViewModel(
                    Path.GetFileName(path) is { Length: > 0 } name ? name : path,
                    path,
                    Path.GetDirectoryName(path) ?? string.Empty,
                    isDirectory,
                    isDirectory ? "\uE8B7" : "\uE8A5"));
            }

            return results;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return [];
        }
        finally
        {
            try
            {
                Everything_CleanUp();
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                // Nothing to clean up if the library was never there.
            }
        }
    }

    /// <summary>
    /// Everything's own query syntax.  A folder scope becomes a path term, which
    /// is how Everything restricts a search to a subtree.
    /// </summary>
    internal static string BuildSearch(string query, string? scope)
    {
        var trimmed = query.Trim();
        if (string.IsNullOrWhiteSpace(scope))
        {
            return trimmed;
        }

        var folder = scope.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return $"path:\"{folder}\" {trimmed}";
    }

    internal static string DescribeError(int code) => code switch
    {
        Ok => "ok",
        ErrorMemory => "Everything could not allocate memory for the query.",
        ErrorIpc => "Everything is not running.",
        ErrorRegisterClass => "Everything could not register its reply window class.",
        ErrorCreateWindow => "Everything could not create its reply window.",
        ErrorCreateThread => "Everything could not create its query thread.",
        ErrorInvalidIndex => "Everything was asked for a result that does not exist.",
        ErrorInvalidCall => "Everything was called out of order.",
        _ => $"Everything reported error {code}."
    };

    /// <summary>
    /// Beside this program first, then where Everything installs itself, then
    /// beside a running copy of it - which covers a portable install.
    /// </summary>
    private static string? Locate()
    {
        if (_resolvedPath is not null)
        {
            return _resolvedPath;
        }

        foreach (var directory in CandidateDirectories())
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(directory, Library);
                if (File.Exists(candidate))
                {
                    _resolvedPath = candidate;
                    return candidate;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or PathTooLongException)
            {
                // A malformed environment variable is not worth failing over.
            }
        }

        return null;

        static IEnumerable<string?> CandidateDirectories()
        {
            yield return AppContext.BaseDirectory;
            yield return Path.GetDirectoryName(Environment.ProcessPath);
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything");
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything");
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "Everything");

            foreach (var directory in RunningEverythingDirectories())
            {
                yield return directory;
            }
        }

        static IEnumerable<string> RunningEverythingDirectories()
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName("Everything");
            }
            catch (InvalidOperationException)
            {
                yield break;
            }

            foreach (var process in processes)
            {
                string? directory = null;
                try
                {
                    directory = Path.GetDirectoryName(process.MainModule?.FileName);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Reading another process's module needs rights we may not
                    // have; the install locations above still cover the usual case.
                }
                finally
                {
                    process.Dispose();
                }

                if (!string.IsNullOrEmpty(directory))
                {
                    yield return directory;
                }
            }
        }
    }

    /// <summary>
    /// Points the P/Invokes at wherever the library was found, so it does not
    /// have to sit next to this program.
    /// </summary>
    private static void InstallResolver()
    {
        if (_resolverInstalled)
        {
            return;
        }

        _resolverInstalled = true;
        NativeLibrary.SetDllImportResolver(
            typeof(EverythingSearchService).Assembly,
            (name, assembly, path) =>
            {
                if (!string.Equals(name, Library, StringComparison.OrdinalIgnoreCase)
                    || _resolvedPath is null)
                {
                    return IntPtr.Zero;
                }

                return NativeLibrary.TryLoad(_resolvedPath, out var handle) ? handle : IntPtr.Zero;
            });
    }

    [DllImport(Library, CharSet = CharSet.Unicode)]
    private static extern void Everything_SetSearchW(string search);

    [DllImport(Library)]
    private static extern void Everything_SetMatchPath([MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport(Library)]
    private static extern void Everything_SetMatchCase([MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport(Library)]
    private static extern void Everything_SetMatchWholeWord([MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport(Library)]
    private static extern void Everything_SetRegex([MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport(Library)]
    private static extern void Everything_SetMax(uint maximumResults);

    [DllImport(Library)]
    private static extern void Everything_SetOffset(uint offset);

    [DllImport(Library)]
    private static extern void Everything_SetSort(uint sort);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Everything_QueryW([MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport(Library)]
    private static extern uint Everything_GetNumResults();

    [DllImport(Library)]
    private static extern uint Everything_GetTotResults();

    [DllImport(Library, CharSet = CharSet.Unicode)]
    private static extern uint Everything_GetResultFullPathNameW(uint index, StringBuilder path, uint maximumCount);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Everything_IsFolderResult(uint index);

    [DllImport(Library)]
    private static extern uint Everything_GetMajorVersion();

    [DllImport(Library)]
    private static extern int Everything_GetLastError();

    [DllImport(Library)]
    private static extern void Everything_CleanUp();
}
