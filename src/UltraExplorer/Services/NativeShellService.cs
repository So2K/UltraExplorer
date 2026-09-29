using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.VisualBasic.FileIO;

namespace UltraExplorer.Services;

public sealed class NativeShellService
{
    /// <summary>
    /// How long Windows waits before two clicks stop being a double-click.  The
    /// user set it; nothing here should second-guess it with a constant.
    /// </summary>
    public static int DoubleClickMilliseconds => Math.Clamp((int)GetDoubleClickTime(), 200, 1200);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    public static void Open(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>
    /// Extensions Windows will run rather than open.  PATHEXT is what the shell
    /// itself consults, so a machine that has added to it is respected; the
    /// literal list is the fallback and shortcuts are always included.
    /// </summary>
    private static readonly HashSet<string> ExecutableExtensions = BuildExecutableExtensions();

    private static HashSet<string> BuildExecutableExtensions()
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".com", ".bat", ".cmd", ".lnk"
        };

        foreach (var entry in (Environment.GetEnvironmentVariable("PATHEXT") ?? string.Empty)
                     .Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = entry.Trim();
            if (trimmed.StartsWith('.'))
            {
                extensions.Add(trimmed);
            }
        }

        return extensions;
    }

    public static bool IsExecutable(string path)
        => ExecutableExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Hands files to a program, the way dropping them on its icon does in
    /// Explorer.  Shell execution is required: a .lnk or a .cmd cannot be
    /// started any other way.
    /// </summary>
    public static void OpenWithProgram(string programPath, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(programPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(programPath) ?? string.Empty,
            Arguments = BuildCommandLine(arguments)
        };

        using var process = Process.Start(start);
    }

    /// <summary>
    /// Quotes arguments the way the C runtime parses them back.  A naive pair of
    /// quotes corrupts anything ending in a backslash - a folder path, most of
    /// the time - because the backslash escapes the closing quote.
    /// </summary>
    internal static string BuildCommandLine(IReadOnlyList<string> arguments)
    {
        var line = new System.Text.StringBuilder();
        foreach (var argument in arguments)
        {
            if (line.Length > 0)
            {
                line.Append(' ');
            }

            if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
            {
                line.Append(argument);
                continue;
            }

            line.Append('"');
            for (var index = 0; ; index++)
            {
                var backslashes = 0;
                while (index < argument.Length && argument[index] == '\\')
                {
                    index++;
                    backslashes++;
                }

                if (index == argument.Length)
                {
                    // Doubled so the run of backslashes cannot escape the quote
                    // that closes the argument.
                    line.Append('\\', backslashes * 2);
                    break;
                }

                line.Append('\\', argument[index] == '"' ? backslashes * 2 + 1 : backslashes);
                line.Append(argument[index]);
            }

            line.Append('"');
        }

        return line.ToString();
    }

    public static void OpenWith(string path)
        => ExecuteShellVerb(path, "openas");

    public static void ShowInExplorer(string path)
    {
        var args = Directory.Exists(path)
            ? $"/e,\"{path}\""
            : $"/select,\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    public static void ShowProperties(string path)
        => ExecuteShellVerb(path, "properties", ShellExecuteMask.InvokeIdList);

    /// <summary>
    /// Writes the selection in the exact shape Explorer uses: CF_HDROP plus the
    /// "Preferred DropEffect" flag.  No proprietary format is written, because
    /// object serialization on the clipboard no longer exists on modern .NET and
    /// the standard formats already round-trip with Explorer.
    /// </summary>
    public static bool CopyPathsToClipboard(IEnumerable<string> paths, bool cut)
    {
        var pathArray = paths.Where(PathExists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (pathArray.Length == 0)
        {
            return false;
        }

        var collection = new StringCollection();
        collection.AddRange(pathArray);
        var data = new DataObject();
        data.SetFileDropList(collection);
        data.SetData(PreferredDropEffectFormat, new MemoryStream([(byte)(cut ? DropEffectMove : DropEffectCopy), 0, 0, 0]));

        // The clipboard is a shared, contended resource; another process can own
        // it for a moment right when the user presses Ctrl+C.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (ExternalException)
            {
                Thread.Sleep(60);
            }
        }

        return false;
    }

    public static ClipboardPayload? GetClipboardPayload()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Clipboard.GetDataObject() is not IDataObject data)
                {
                    return null;
                }

                if (!data.GetDataPresent(DataFormats.FileDrop)
                    || data.GetData(DataFormats.FileDrop) is not string[] paths
                    || paths.Length == 0)
                {
                    return null;
                }

                return new ClipboardPayload(paths, ReadPreferredDropEffect(data) == DropEffectMove);
            }
            catch (ExternalException)
            {
                Thread.Sleep(60);
            }
        }

        return null;
    }

    private static byte ReadPreferredDropEffect(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(PreferredDropEffectFormat)
                && data.GetData(PreferredDropEffectFormat) is MemoryStream stream)
            {
                var position = stream.Position;
                stream.Position = 0;
                var first = stream.ReadByte();
                stream.Position = position;
                return first < 0 ? DropEffectCopy : (byte)first;
            }
        }
        catch (Exception ex) when (ex is ExternalException or ObjectDisposedException or NotSupportedException)
        {
        }

        return DropEffectCopy;
    }

    /// <summary>
    /// Copies or moves items into a folder as one operation of the Shell's own,
    /// as Explorer does: one progress window, and one question for a clash,
    /// with "do this for all" and Skip, rather than one per item - and an
    /// answer of Skip or Cancel no longer leaves the rest of the batch undone
    /// half way through.  Undo in Explorer covers it too.
    /// </summary>
    public Task CopyOrMoveAsync(IEnumerable<string> sources, string targetDirectory, bool move)
    {
        var sourceArray = sources
            .Where(PathExists)
            .Distinct(StringComparer.OrdinalIgnoreCase)

            // Into the folder it is already in: nothing to do, and the Shell
            // would only say that the source and the destination are the same.
            .Where(source => !PathsEqual(source, Path.Combine(targetDirectory, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)))))
            .ToArray();

        // Moved, an item inside another one moved with it is gone by the time
        // the Shell comes to it (see WithoutNested).
        if (move)
        {
            sourceArray = WithoutNested(sourceArray);
        }

        if (sourceArray.Length == 0)
        {
            return Task.CompletedTask;
        }

        ThrowIfAnyRoot(sourceArray, move ? "moved" : "copied");
        Directory.CreateDirectory(targetDirectory);
        var owner = OwnerWindowHandle();
        return RunStaAsync(() => RunShellOperation(
            owner,
            move ? FileOperation.Move : FileOperation.Copy,
            sourceArray,
            targetDirectory,
            FileOperationFlags.AllowUndo | FileOperationFlags.NoConnectedElements));
    }

    /// <summary>
    /// Deletes items as one operation of the Shell's own.  To the Recycle Bin,
    /// Windows asks - or not - as the user has told the Recycle Bin to, once
    /// for the lot, and warns before anything that cannot be recycled is
    /// destroyed instead.  Permanently, the app has already asked, so Windows
    /// does not ask again for every item.
    /// </summary>
    public Task DeleteAsync(IEnumerable<string> paths, bool permanently)
    {
        var pathArray = WithoutNested(paths.Where(PathExists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        if (pathArray.Length == 0)
        {
            return Task.CompletedTask;
        }

        ThrowIfAnyRoot(pathArray, "deleted");
        var flags = FileOperationFlags.NoConnectedElements
            | (permanently
                ? FileOperationFlags.NoConfirmation
                : FileOperationFlags.AllowUndo | FileOperationFlags.WantNukeWarning);
        var owner = OwnerWindowHandle();
        return RunStaAsync(() => RunShellOperation(owner, FileOperation.Delete, pathArray, null, flags));
    }

    public Task<string> DuplicateAsync(string path)
        => RunStaAsync(() =>
        {
            var target = GetDuplicatePath(path);
            if (Directory.Exists(path))
            {
                FileSystem.CopyDirectory(path, target, UIOption.AllDialogs, UICancelOption.ThrowException);
            }
            else
            {
                FileSystem.CopyFile(path, target, UIOption.AllDialogs, UICancelOption.ThrowException);
            }

            return target;
        });

    public static string Rename(string path, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("The name contains characters that Windows does not allow.", nameof(newName));
        }

        var parent = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The parent folder is unavailable.");
        var target = Path.Combine(parent, newName.Trim());

        // A new name that differs only in its capitals names the same item on
        // Windows, which of course exists; that is a rename, not a clash.  The
        // moves below change the capitals and never replace anything, so a
        // folder that really does keep two such names apart still refuses.
        if (!PathsEqual(path, target) && PathExists(target))
        {
            throw new IOException("An item with this name already exists.");
        }

        if (Directory.Exists(path))
        {
            Directory.Move(path, target);
        }
        else
        {
            File.Move(path, target);
        }

        return target;
    }

    public static string CreateFolder(string parentPath, string requestedName)
    {
        var name = string.IsNullOrWhiteSpace(requestedName) ? "New folder" : requestedName.Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("The folder name contains invalid characters.");
        }

        var target = Path.Combine(parentPath, name);
        var suffix = 2;
        while (Directory.Exists(target) || File.Exists(target))
        {
            target = Path.Combine(parentPath, $"{name} ({suffix++})");
        }

        Directory.CreateDirectory(target);
        return target;
    }

    public static string CreateNoteFile(string parentPath, string requestedName)
    {
        var name = string.IsNullOrWhiteSpace(requestedName) ? "New note.txt" : requestedName.Trim();
        if (!Path.HasExtension(name))
        {
            name += ".txt";
        }

        var target = Path.Combine(parentPath, name);
        var baseName = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var suffix = 2;
        while (File.Exists(target) || Directory.Exists(target))
        {
            target = Path.Combine(parentPath, $"{baseName} ({suffix++}){extension}");
        }

        File.WriteAllText(target, string.Empty);
        return target;
    }

    public static bool IsSameVolume(string source, string targetDirectory)
        => string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(targetDirectory), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="targetDirectory"/> is <paramref name="source"/>
    /// itself or inside it, when the source is a folder.  The names are
    /// compared first and the disk is asked only when they say yes: a drag
    /// asks this for every item it carries on every move of the pointer, and
    /// a lookup each time - thousands, perhaps over a network - stalled it.
    /// </summary>
    public static bool IsInvalidMoveTarget(string source, string targetDirectory)
    {
        if (source.Length == 0 || targetDirectory.Length == 0)
        {
            return false;
        }

        var sourceWithSeparator = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var targetWithSeparator = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return targetWithSeparator.StartsWith(sourceWithSeparator, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(source);
    }

    private const string PreferredDropEffectFormat = "Preferred DropEffect";
    private const byte DropEffectCopy = 1;
    private const byte DropEffectMove = 2;

    public sealed record ClipboardPayload(string[] Paths, bool Cut);

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// <paramref name="paths"/> without any that lie inside another of them.
    /// The nested canvas can select a folder and something in it, and a
    /// clipboard can carry both: deleted or moved as one operation, the
    /// folder goes first and takes the other with it, and the Shell then asks
    /// about an item it cannot find and reports the whole operation as failed.
    /// The names alone decide - no disk is asked - and the order is kept.
    /// </summary>
    internal static string[] WithoutNested(IReadOnlyList<string> paths)
    {
        if (paths.Count < 2)
        {
            return [.. paths];
        }

        var all = new HashSet<string>(paths.Select(NestingKey), StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            var inside = false;
            for (var parent = Path.GetDirectoryName(NestingKey(path)); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            {
                if (all.Contains(NestingKey(parent)))
                {
                    inside = true;
                    break;
                }
            }

            if (!inside)
            {
                kept.Add(path);
            }
        }

        return [.. kept];
    }

    /// <summary>A path as <see cref="WithoutNested"/> compares it: full, without a trailing separator.</summary>
    private static string NestingKey(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static string GetDuplicatePath(string source)
    {
        var directory = Path.GetDirectoryName(source) ?? throw new InvalidOperationException("The parent folder is unavailable.");
        var extension = Directory.Exists(source) ? string.Empty : Path.GetExtension(source);
        var stem = Directory.Exists(source) ? Path.GetFileName(source) : Path.GetFileNameWithoutExtension(source);
        var candidate = Path.Combine(directory, $"{stem} - Copy{extension}");
        var index = 2;
        while (PathExists(candidate))
        {
            candidate = Path.Combine(directory, $"{stem} - Copy ({index++}){extension}");
        }

        return candidate;
    }

    /// <summary>
    /// A drive's or a share's root is not an item to delete, move or copy into
    /// a folder, and what the Shell would make of being handed one is not worth
    /// finding out: the whole operation is refused before anything is touched.
    /// </summary>
    private static void ThrowIfAnyRoot(IEnumerable<string> paths, string verb)
    {
        if (paths.FirstOrDefault(path => Path.GetDirectoryName(Path.GetFullPath(path)) is null) is { } root)
        {
            throw new IOException($"{root} is the root of a drive and cannot be {verb}.");
        }
    }

    /// <summary>
    /// The window the Shell's dialogs belong to - the app's window in front -
    /// asked for on the UI thread, before the operation leaves it.  Owned, a
    /// question stays in front of the window it is about.
    /// </summary>
    private static IntPtr OwnerWindowHandle()
    {
        var application = Application.Current;
        if (application is null || !application.Dispatcher.CheckAccess())
        {
            return IntPtr.Zero;
        }

        var owner = application.Windows.OfType<Window>().FirstOrDefault(candidate => candidate.IsActive) ?? application.MainWindow;
        return owner is null ? IntPtr.Zero : new WindowInteropHelper(owner).Handle;
    }

    /// <summary>
    /// One SHFileOperation over every item.  The Shell takes its items as one
    /// string, each full path ended by a null and the list by another, and a
    /// path that is not full it would read from the current directory - so
    /// every path is made full here.  Stopped by the user - Cancel, or No to
    /// one of its questions - it is an <see cref="OperationCanceledException"/>,
    /// which the callers have always taken as "cancelled"; any other failure
    /// is an <see cref="IOException"/>, once Windows has shown its own error.
    /// </summary>
    private static void RunShellOperation(
        IntPtr owner,
        FileOperation operation,
        IReadOnlyList<string> sources,
        string? targetDirectory,
        FileOperationFlags flags)
    {
        var request = new ShellFileOperation
        {
            Window = owner,
            Function = operation,
            From = DoubleNullTerminated(sources),
            To = targetDirectory is null ? null : DoubleNullTerminated([targetDirectory]),
            Flags = flags
        };

        var result = SHFileOperation(ref request);
        if (request.AnyOperationsAborted || result is ErrorCancelled or LegacyErrorCancelled)
        {
            throw new OperationCanceledException();
        }

        if (result != 0)
        {
            throw new IOException($"Windows could not finish the operation (error 0x{result:X}).");
        }
    }

    private static string DoubleNullTerminated(IEnumerable<string> paths)
        => string.Concat(paths.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + '\0')) + '\0';

    private static Task RunStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
            // Foreground on purpose: killing this thread half-way through a copy
            // would leave a truncated file behind.
        }) { IsBackground = false, Name = "UltraExplorer Shell operation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static Task<T> RunStaAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
            // Foreground on purpose: killing this thread half-way through a copy
            // would leave a truncated file behind.
        }) { IsBackground = false, Name = "UltraExplorer Shell operation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void ExecuteShellVerb(string path, string verb, ShellExecuteMask mask = ShellExecuteMask.Default)
    {
        var info = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = mask,
            Verb = verb,
            File = path,
            Show = 1
        };

        if (!ShellExecuteEx(ref info))
        {
            Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
        }
    }

    [Flags]
    private enum ShellExecuteMask : uint
    {
        Default = 0x00000100,
        InvokeIdList = 0x0000000C
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public ShellExecuteMask Mask;
        public IntPtr Window;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
        public int Show;
        public IntPtr Instance;
        public IntPtr IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public IntPtr ClassKey;
        public uint HotKey;
        public IntPtr IconOrMonitor;
        public IntPtr Process;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo executeInfo);

    private const int ErrorCancelled = 1223;
    private const int LegacyErrorCancelled = 0x75;

    private enum FileOperation : uint
    {
        Move = 0x0001,
        Copy = 0x0002,
        Delete = 0x0003
    }

    [Flags]
    private enum FileOperationFlags : ushort
    {
        NoConfirmation = 0x0010,
        AllowUndo = 0x0040,
        NoConnectedElements = 0x2000,
        WantNukeWarning = 0x4000
    }

    /// <summary>
    /// SHFILEOPSTRUCTW.  shellapi.h packs it to single bytes only on 32-bit
    /// Windows; the app is built for x64 alone (the project's
    /// RuntimeIdentifier), where it has the natural layout declared here.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileOperation
    {
        public IntPtr Window;
        public FileOperation Function;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public FileOperationFlags Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHFileOperation(ref ShellFileOperation fileOperation);
}
