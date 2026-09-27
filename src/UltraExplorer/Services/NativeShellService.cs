using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
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

    public Task CopyOrMoveAsync(IEnumerable<string> sources, string targetDirectory, bool move)
    {
        var sourceArray = sources.Where(PathExists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (sourceArray.Length == 0)
        {
            return Task.CompletedTask;
        }

        Directory.CreateDirectory(targetDirectory);
        return RunStaAsync(() =>
        {
            foreach (var source in sourceArray)
            {
                var target = Path.Combine(targetDirectory, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)));
                if (PathsEqual(source, target))
                {
                    continue;
                }

                if (Directory.Exists(source))
                {
                    if (move)
                    {
                        FileSystem.MoveDirectory(source, target, UIOption.AllDialogs, UICancelOption.ThrowException);
                    }
                    else
                    {
                        FileSystem.CopyDirectory(source, target, UIOption.AllDialogs, UICancelOption.ThrowException);
                    }
                }
                else if (File.Exists(source))
                {
                    if (move)
                    {
                        FileSystem.MoveFile(source, target, UIOption.AllDialogs, UICancelOption.ThrowException);
                    }
                    else
                    {
                        FileSystem.CopyFile(source, target, UIOption.AllDialogs, UICancelOption.ThrowException);
                    }
                }
            }
        });
    }

    public Task DeleteAsync(IEnumerable<string> paths, bool permanently)
    {
        var pathArray = paths.Where(PathExists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return RunStaAsync(() =>
        {
            foreach (var path in pathArray)
            {
                var recycle = permanently ? RecycleOption.DeletePermanently : RecycleOption.SendToRecycleBin;
                if (Directory.Exists(path))
                {
                    FileSystem.DeleteDirectory(path, UIOption.AllDialogs, recycle, UICancelOption.ThrowException);
                }
                else if (File.Exists(path))
                {
                    FileSystem.DeleteFile(path, UIOption.AllDialogs, recycle, UICancelOption.ThrowException);
                }
            }
        });
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
        if (PathExists(target))
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

    public static bool IsInvalidMoveTarget(string source, string targetDirectory)
    {
        if (!Directory.Exists(source))
        {
            return false;
        }

        var sourceWithSeparator = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var targetWithSeparator = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return targetWithSeparator.StartsWith(sourceWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private const string PreferredDropEffectFormat = "Preferred DropEffect";
    private const byte DropEffectCopy = 1;
    private const byte DropEffectMove = 2;

    public sealed record ClipboardPayload(string[] Paths, bool Cut);

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

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
}
