using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.VisualBasic.FileIO;
using Vanara.PInvoke;
using Vanara.Windows.Shell;
using static Vanara.PInvoke.Shell32;

namespace UltraExplorer.Services;

public sealed class NativeShellService
{
    public static void ShowNativeContextMenu(IReadOnlyList<string> paths, Point screenPoint, IntPtr ownerHandle, bool extended)
    {
        var existingPaths = paths.Where(PathExists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (existingPaths.Length == 0)
        {
            return;
        }

        var options = extended ? CMF.CMF_NORMAL | CMF.CMF_EXTENDEDVERBS : CMF.CMF_NORMAL;
        if (existingPaths.Length == 1)
        {
            // Let ShellItem own the context menu and its PIDL keep-alive bundle. This is
            // Vanara's tested lifetime path and avoids double-freeing Shell resources.
            // Construct the base ShellItem explicitly. ShellItem.Open returns a
            // ShellFolder for directories whose ContextMenu represents the folder
            // background ("New", view commands, etc.) instead of the selected item.
            using var item = new ShellItem(existingPaths[0]);
            item.ContextMenu.ShowContextMenu(
                new POINT((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)),
                options,
                hWnd: new HWND(ownerHandle));
            return;
        }

        var items = existingPaths.Select(ShellItem.Open).ToArray();
        try
        {
            using var menu = ShellContextMenu.CreateFromItems(items, out var keepAlive);
            menu.ShowContextMenu(
                new POINT((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)),
                options,
                hWnd: new HWND(ownerHandle));
            GC.KeepAlive(keepAlive);
        }
        finally
        {
            foreach (var shellItem in items)
            {
                shellItem.Dispose();
            }
        }
    }

    public static void Open(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
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

    public static void CopyPathsToClipboard(IEnumerable<string> paths, bool cut)
    {
        var pathArray = paths.Where(PathExists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (pathArray.Length == 0)
        {
            return;
        }

        var collection = new StringCollection();
        collection.AddRange(pathArray);
        var data = new DataObject();
        data.SetFileDropList(collection);
        data.SetData("Preferred DropEffect", new MemoryStream([(byte)(cut ? 2 : 1), 0, 0, 0]));
        data.SetData(InternalClipboardFormat, new ClipboardPayload(pathArray, cut));
        Clipboard.SetDataObject(data, true);
    }

    public static ClipboardPayload? GetClipboardPayload()
    {
        try
        {
            if (Clipboard.GetDataObject() is not IDataObject data)
            {
                return null;
            }

            if (data.GetDataPresent(InternalClipboardFormat)
                && data.GetData(InternalClipboardFormat) is ClipboardPayload internalPayload)
            {
                return internalPayload;
            }

            if (!data.GetDataPresent(DataFormats.FileDrop)
                || data.GetData(DataFormats.FileDrop) is not string[] paths)
            {
                return null;
            }

            var cut = false;
            if (data.GetDataPresent("Preferred DropEffect") && data.GetData("Preferred DropEffect") is MemoryStream stream)
            {
                cut = stream.ReadByte() == 2;
                stream.Position = 0;
            }

            return new ClipboardPayload(paths, cut);
        }
        catch (ExternalException)
        {
            return null;
        }
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
                        FileSystem.MoveDirectory(source, target, UIOption.AllDialogs, UICancelOption.DoNothing);
                    }
                    else
                    {
                        FileSystem.CopyDirectory(source, target, UIOption.AllDialogs, UICancelOption.DoNothing);
                    }
                }
                else if (File.Exists(source))
                {
                    if (move)
                    {
                        FileSystem.MoveFile(source, target, UIOption.AllDialogs, UICancelOption.DoNothing);
                    }
                    else
                    {
                        FileSystem.CopyFile(source, target, UIOption.AllDialogs, UICancelOption.DoNothing);
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
                    FileSystem.DeleteDirectory(path, UIOption.AllDialogs, recycle, UICancelOption.DoNothing);
                }
                else if (File.Exists(path))
                {
                    FileSystem.DeleteFile(path, UIOption.AllDialogs, recycle, UICancelOption.DoNothing);
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
                FileSystem.CopyDirectory(path, target, UIOption.AllDialogs, UICancelOption.DoNothing);
            }
            else
            {
                FileSystem.CopyFile(path, target, UIOption.AllDialogs, UICancelOption.DoNothing);
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

    public const string InternalClipboardFormat = "UltraExplorer.FileClipboard.v1";
    public const string InternalDragFormat = "UltraExplorer.InternalFileDrag.v1";

    [Serializable]
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
        }) { IsBackground = true, Name = "UltraExplorer Shell operation" };
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
        }) { IsBackground = true, Name = "UltraExplorer Shell operation" };
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
