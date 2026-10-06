using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

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

    /// <summary>
    /// Opens an item in its default program.  A caller that already knows
    /// whether the item is a folder passes that answer so this method does not
    /// ask a sleeping disk or share again on the caller's thread.  The null
    /// default keeps the public API's previous behaviour for callers without
    /// a trustworthy item kind.
    /// </summary>
    public static void Open(string path, bool? isDirectory = null)
    {
        if ((isDirectory ?? Directory.Exists(path)) && ExplorerLaunchRouter.TryOpenFolder(path)) return;
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
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
    /// Explorer. Ordinary executables keep Shell execution; batch files use
    /// a separate literal-data cmd transport and shortcuts are classified first.
    /// </summary>
    public static void OpenWithProgram(string programPath, IReadOnlyList<string> arguments)
    {
        // Legacy bounded synchronous API: builder continuations never require
        // this UI context. Production drag callers use the asynchronous API.
        var start = CreateProgramDropStartInfoAsync(programPath, arguments).GetAwaiter().GetResult();
        using var process = StartProgramDrop(start);
    }

    /// <summary>Reads shortcut/association metadata off the dispatcher, then launches only if the caller still owns the request.</summary>
    public static async Task OpenWithProgramAsync(string programPath, IReadOnlyList<string> arguments, Func<bool>? requestCurrent = null)
    {
        var start = await CreateProgramDropStartInfoAsync(programPath, arguments);
        if (requestCurrent?.Invoke() == false) return;
        using var process = StartProgramDrop(start);
    }

    internal static async Task<ProcessStartInfo> CreateProgramDropStartInfoAsync(string programPath, IReadOnlyList<string> arguments)
    {
        var paths = arguments.ToArray();
        var launch = new ProgramShortcut(programPath, "", Path.GetDirectoryName(programPath) ?? "");
        var extension = Path.GetExtension(programPath);
        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            launch = await ReadProgramMetadataAsync(programPath, shortcut: true).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var targetExtension = Path.GetExtension(launch.Target);
        if (!IsBatchProgram(launch.Target) && !targetExtension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            && !targetExtension.Equals(".com", StringComparison.OrdinalIgnoreCase))
        {
            var association = await ReadProgramMetadataAsync(launch.Target, shortcut: false).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (IsCommandTextHost(association.Target) && !IsLiteralPowerShellFileAssociation(association))
                throw new IOException("This script association uses a command shell and cannot safely receive dropped file names. Use its .cmd/.bat file directly.");
        }
        return CreateProgramDropStartInfo(launch, paths);
    }

    internal sealed record ProgramShortcut(string Target, string Arguments, string WorkingDirectory);
    internal static Func<ProcessStartInfo, Process?>? ProgramDropLauncherForChecks { get; set; }
    private static Process? StartProgramDrop(ProcessStartInfo start)
        => ProgramDropLauncherForChecks is { } launcher ? launcher(start) : Process.Start(start);

    internal static ProcessStartInfo CreateProgramDropStartInfo(ProgramShortcut launch, IReadOnlyList<string> paths)
    {
        if (IsBatchProgram(launch.Target))
        {
            if (!string.IsNullOrWhiteSpace(launch.Arguments))
                throw new IOException("A batch shortcut with preset arguments cannot safely receive dropped file names. Drop onto its .cmd/.bat file instead.");
            return CreateBatchDropStartInfo(launch.Target, paths, launch.WorkingDirectory);
        }
        if (!string.IsNullOrWhiteSpace(launch.Arguments) && IsCommandScriptHost(launch.Target))
            throw new IOException("This script-host shortcut has preset commands. Drop onto the script itself instead of passing file names through that command prefix.");
        if (IsCommandTextHost(launch.Target))
            throw new IOException("This program interprets command text and cannot safely receive dropped file names. Use its script file or a program with literal file arguments instead.");
        return new ProcessStartInfo(launch.Target)
        {
            UseShellExecute = true,
            WorkingDirectory = launch.WorkingDirectory,
            Arguments = (string.IsNullOrWhiteSpace(launch.Arguments) ? "" : launch.Arguments.TrimEnd() + " ") + BuildCommandLine(paths)
        };
    }

    private static bool IsBatchProgram(string path) => Path.GetExtension(path).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".bat", StringComparison.OrdinalIgnoreCase);

    private static bool IsCommandScriptHost(string path) => Path.GetFileNameWithoutExtension(path).ToLowerInvariant()
        is "cmd" or "powershell" or "pwsh" or "wscript" or "cscript" or "mshta" or "bash" or "sh";

    private static bool IsCommandTextHost(string path) => Path.GetFileNameWithoutExtension(path).ToLowerInvariant()
        is "cmd" or "powershell" or "pwsh" or "mshta" or "bash" or "sh";

    private static bool IsLiteralPowerShellFileAssociation(ProgramShortcut association)
    {
        var host = Path.GetFileNameWithoutExtension(association.Target);
        return (host.Equals("powershell", StringComparison.OrdinalIgnoreCase) || host.Equals("pwsh", StringComparison.OrdinalIgnoreCase))
            // Full fail-closed grammar, not a blacklist of abbreviated code
            // switches. Only these literal File templates are accepted.
            && System.Text.RegularExpressions.Regex.IsMatch(association.Arguments,
                """^\s*(?:"[^"]+"|[^\s"]+)\s+(?:(?:"?-NoProfile"?|"?-NonInteractive"?)\s+)*"?-File"?\s+"%[1lL]"\s+%\*\s*$""",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    internal static ProcessStartInfo CreateBatchDropStartInfo(string script, IReadOnlyList<string> paths, string? workingDirectory = null)
    {
        // cmd syntax is not the C-runtime quoting used by EXEs/IPC. Only fixed
        // quoted variable tokens enter the command line; % values introduced
        // by that expansion are not rescanned. /v:off keeps ! literal as well.
        static void ValidFileArgument(string value)
        {
            if (value.Length == 0 || value.Any(character => character == '"' || character < ' '))
                throw new ArgumentException("A batch file cannot safely receive an empty file name or one containing quotes/control characters.");
        }
        ValidFileArgument(script);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(script) ?? ""
        };
        var prefix = "UE_DROP_" + Guid.NewGuid().ToString("N") + "_";
        start.Environment[prefix + "SCRIPT"] = script;
        var command = new System.Text.StringBuilder("/d /v:off /s /c \"\"%").Append(prefix).Append("SCRIPT%\"");
        for (var index = 0; index < paths.Count; index++)
        {
            ValidFileArgument(paths[index]);
            var key = prefix + index;
            start.Environment[key] = paths[index];
            command.Append(" \"%").Append(key).Append("%\"");
        }
        start.Arguments = command.Append('"').ToString();
        return start;
    }

    private static readonly object ProgramMetadataGate = new();
    private static readonly Dictionary<string, Task<ProgramShortcut>> ProgramMetadataReads = new(StringComparer.OrdinalIgnoreCase);
    internal static Func<string, bool, ProgramShortcut>? ProgramMetadataReaderForChecks { get; set; }
    internal static int ProgramMetadataPendingForChecks { get { lock (ProgramMetadataGate) return ProgramMetadataReads.Count; } }

    private static Task<ProgramShortcut> ReadProgramMetadataAsync(string path, bool shortcut)
    {
        var key = (shortcut ? "link|" : "association|") + path;
        lock (ProgramMetadataGate)
        {
            if (ProgramMetadataReads.TryGetValue(key, out var pending)) return pending;
            if (ProgramMetadataReads.Count >= 2)
                throw new IOException("Shortcut metadata is still being read. Try again after that request finishes.");
            var answer = new TaskCompletionSource<ProgramShortcut>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = answer.Task.ContinueWith(failed => { _ = failed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            ProgramMetadataReads.Add(key, answer.Task);
            var thread = new Thread(() =>
            {
                ProgramShortcut? metadata = null;
                Exception? failure = null;
                try { metadata = ProgramMetadataReaderForChecks?.Invoke(path, shortcut)
                    ?? (shortcut ? ReadProgramShortcut(path) : ReadProgramAssociation(path)); }
                catch (Exception error) { failure = error; }
                // Finished metadata must stop being "in flight" before its
                // continuation can prepare a second launch of the same link.
                lock (ProgramMetadataGate) ProgramMetadataReads.Remove(key);
                if (failure is not null) answer.TrySetException(failure);
                else answer.TrySetResult(metadata!);
            }) { IsBackground = true, Name = "UltraExplorer program metadata" };
            thread.SetApartmentState(ApartmentState.STA);
            try { thread.Start(); }
            catch { ProgramMetadataReads.Remove(key); throw; }
            return answer.Task;
        }
    }

    private static ProgramShortcut ReadProgramShortcut(string path)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException("The shortcut is no longer available.", path);
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new IOException("Windows shortcut metadata is unavailable."));
            dynamic source = shell!;
            shortcut = source.CreateShortcut(path);
            dynamic link = shortcut;
            var target = Environment.ExpandEnvironmentVariables((string)link.TargetPath);
            if (string.IsNullOrWhiteSpace(target)) throw new IOException("This shortcut has no filesystem program target for dropped files.");
            var directory = (string)link.WorkingDirectory;
            return new(target, (string)link.Arguments, string.IsNullOrWhiteSpace(directory) ? Path.GetDirectoryName(path) ?? "" : directory);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static ProgramShortcut ReadProgramAssociation(string path)
    {
        uint length = 32768;
        var executable = new System.Text.StringBuilder((int)length);
        if (AssocQueryString(0, 2, Path.GetExtension(path), "open", executable, ref length) != 0)
            throw new IOException("Windows could not identify this program's file association safely.");
        length = 32768;
        var command = new System.Text.StringBuilder((int)length);
        if (AssocQueryString(0, 1, Path.GetExtension(path), "open", command, ref length) != 0)
            throw new IOException("Windows could not identify this script association's command safely.");
        return new(executable.ToString(), command.ToString(), "");
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, EntryPoint = "AssocQueryStringW")]
    private static extern int AssocQueryString(uint flags, uint query, string association, string extra, System.Text.StringBuilder output, ref uint characters);

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

    /// <summary>The Open with Shell dialog on its own STA, away from the shared WPF dispatcher.</summary>
    public static Task OpenWithAsync(string path) => RunStaAsync(() => OpenWith(path));

    /// <summary>
    /// Windows Explorer itself on <paramref name="path"/> - a folder opened,
    /// anything else selected in its folder - for the commands that name it:
    /// never routed into UltraExplorer, even while folders open through it
    /// (<see cref="ExplorerLaunchRouter.OpenExplorerByName"/>).
    /// </summary>
    public static void ShowInWindowsExplorer(string path)
        => ExplorerLaunchRouter.OpenExplorerByName(path, select: !Directory.Exists(path));

    public static void ShowInExplorer(string path)
    {
        if (Directory.Exists(path) ? ExplorerLaunchRouter.TryOpenFolder(path) : ExplorerLaunchRouter.TryReveal([path])) return;
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
        var pathArray = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (pathArray.Length == 0)
        {
            return false;
        }

        // This check is only string arithmetic.  Besides being cheap, it is
        // essential for a path from WSL/git whose last dot or space Windows
        // would silently drop when another program later consumes CF_HDROP.
        ThrowIfAnyNameEndDropped(pathArray, "copied");

        var collection = new StringCollection();
        collection.AddRange(pathArray);
        var data = new DataObject();
        data.SetFileDropList(collection);
        data.SetData(PreferredDropEffectFormat, new MemoryStream([(byte)(cut ? DropEffectMove : DropEffectCopy), 0, 0, 0]));

        if (ClipboardPublisherForTests is { } publisher)
        {
            return publisher(data);
        }

        return PublishClipboard(data);
    }

    /// <summary>
    /// Test seam at the last boundary: validation and data construction remain
    /// production code, while checks never replace the user's real clipboard.
    /// </summary>
    internal static Func<IDataObject, bool>? ClipboardPublisherForTests { get; set; }

    private static bool PublishClipboard(IDataObject data)
    {
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
        var requested = sources.ToArray();
        ThrowIfAnyNameEndDropped(requested, move ? "moved" : "copied");
        ThrowIfAnyNameEndDropped([targetDirectory], move ? "moved into" : "copied into");
        var distinct = requested
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Into the folder it is already in.  Moved, there is nothing to do,
        // and the Shell would only say that the source and the destination
        // are the same.  Copied, it is copied beside itself - "report -
        // Copy.docx" - as Explorer's paste does: in an operation of its own
        // that names the copies, so a clash of anything else is still asked
        // about rather than renamed.
        bool InTarget(string source) =>
            PathsEqual(source, Path.Combine(targetDirectory, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar))));
        var sourceArray = distinct.Where(source => !InTarget(source)).ToArray();
        string[] beside = move ? [] : [.. distinct.Where(InTarget)];

        // Moved, an item inside another one moved with it is gone by the time
        // the Shell comes to it (see WithoutNested).
        if (move)
        {
            sourceArray = WithoutNested(sourceArray);
        }

        if (distinct.Length == 0)
        {
            return Started(Task.CompletedTask);
        }

        ThrowIfAnyRoot([.. sourceArray, .. beside], move ? "moved" : "copied");
        var owner = OwnerWindowHandle();
        var flags = ShellFileOperation.TransferFlags(renameOnCollision: false);

        // What asks the disk - each item still there, the target made - on
        // the operation's own thread (see OperationItemExists).
        return Started(RunStaAsync(() =>
        {
            // Validate the complete requested batch before creating a target or
            // starting its first copy. A disappearing/inaccessible item is not
            // successful copying: external OLE sources rely on this result to
            // decide whether the drop transferred their data.
            foreach (var source in distinct)
                if (!OperationItemExists(source))
                    throw new FileNotFoundException("The source is no longer available; no files were copied or moved.", source);

            // A validated move into the same directory is intentionally a no-op.
            if (sourceArray.Length == 0 && beside.Length == 0)
            {
                return;
            }

            Directory.CreateDirectory(targetDirectory);
            if (sourceArray.Length > 0)
            {
                RunShellOperation(owner, move ? ShellFileOperationKind.Move : ShellFileOperationKind.Copy, sourceArray, targetDirectory, flags);
            }

            if (beside.Length > 0)
            {
                RunShellOperation(owner, ShellFileOperationKind.Copy, beside, targetDirectory,
                    ShellFileOperation.TransferFlags(renameOnCollision: true));
            }
        }));
    }

    /// <summary>
    /// Set by a drop that holds its source program until the source's files
    /// have been copied (see <see cref="ExternalFileDrop"/>), for the copy or
    /// move <see cref="CopyOrMoveAsync"/> starts within that drop: handed the
    /// Shell's operation, it lets the source go the moment the operation
    /// ends - before the drop goes on to read the folders again - and hands
    /// back what the drop then waits on.  Unset, the operation is waited on
    /// as it is.
    /// </summary>
    internal static AsyncLocal<Func<Task, Task>?> CopyStarted { get; } = new();

    private static Task Started(Task operation) => CopyStarted.Value is { } started ? started(operation) : operation;

    /// <summary>
    /// Deletes items as one operation of the Shell's own.  To the Recycle Bin,
    /// Windows asks - or not - as the user has told the Recycle Bin to, once
    /// for the lot, and warns before anything that cannot be recycled is
    /// destroyed instead.  Permanently, the app has already asked, so Windows
    /// does not ask again for every item.
    /// </summary>
    public Task DeleteAsync(IEnumerable<string> paths, bool permanently)
    {
        var requested = paths.ToArray();
        ThrowIfAnyNameEndDropped(requested, "deleted");
        var pathArray = WithoutNested(requested.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        if (pathArray.Length == 0)
        {
            return Task.CompletedTask;
        }

        ThrowIfAnyRoot(pathArray, "deleted");
        var flags = ShellFileOperation.DeleteFlags(permanently);
        var owner = OwnerWindowHandle();

        // Whether each item is still there is asked on the operation's own
        // thread (see OperationItemExists).
        return RunStaAsync(() =>
        {
            var present = pathArray.Where(OperationItemExists).ToArray();
            if (present.Length > 0)
            {
                RunShellOperation(owner, ShellFileOperationKind.Delete, present, null, flags);
            }
        });
    }

    public Task<string> DuplicateAsync(string path)
    {
        ThrowIfAnyNameEndDropped([path], "duplicated");
        var owner = OwnerWindowHandle();
        return RunStaAsync(() =>
        {
            var target = GetDuplicatePath(path);
            var destination = Path.GetDirectoryName(target)
                ?? throw new InvalidOperationException("The parent folder is unavailable.");
            ShellFileOperation.Run(
                owner,
                ShellFileOperationKind.Copy,
                [path],
                destination,
                ShellFileOperation.TransferFlags(renameOnCollision: false),
                [Path.GetFileName(target)]);
            return target;
        });
    }

    public static string Rename(string path, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("The name contains characters that Windows does not allow.", nameof(newName));
        }

        ThrowIfAnyNameEndDropped([path], "renamed");
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
        ThrowIfAnyNameEndDropped([parentPath], "added to");
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
        ThrowIfAnyNameEndDropped([parentPath], "added to");
        var name = string.IsNullOrWhiteSpace(requestedName) ? "New note.txt" : requestedName.Trim();

        // A file's name, as a folder's is: a colon would make a stream of
        // another file, and a separator a file in another folder.
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("The file name contains invalid characters.");
        }

        // What follows the last dot is an extension only when it reads as
        // one: "Notes 10.03" and "Plan v2. draft" are names, and get .txt
        // like a name without a dot, so the file opens as text.
        var typed = Path.GetExtension(name);
        if (typed.Length < 2 || typed.Contains(' ') || !typed.Skip(1).Any(char.IsLetter))
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
    /// How a copy, a move or a delete asks whether each item it was given is
    /// still there.  Thousands of items on a share are as many questions to
    /// the network, so they are asked on the operation's own thread, never the
    /// UI thread every window of the process shares.  A test puts its own
    /// answer here to see where it is asked.
    /// </summary>
    internal static Func<string, bool> OperationItemExists { get; set; } = PathExists;

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
    /// A name ending in a dot or a space - "dup.", "notes " - which a WSL
    /// tree, git or a share can leave.  Windows drops the dot or the space
    /// from every name of a path it is handed, so the Shell, and .NET's own
    /// file calls with it, would reach "dup" - another item, or none: delete
    /// it, rename it, move it, or put something in it.  The whole operation
    /// is refused before anything is touched, as for a root.
    /// </summary>
    private static void ThrowIfAnyNameEndDropped(IEnumerable<string> paths, string verb)
    {
        if (paths.FirstOrDefault(Models.ViewAllPath.EndsANameInDotOrSpace) is not { } path)
        {
            return;
        }

        var names = Path.TrimEndingDirectorySeparator(path).Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var name = names.Length > 0 ? names[^1] : path;
        var folder = names.SkipLast(1).FirstOrDefault(part => part[^1] is '.' or ' ');
        throw new IOException(folder is null
            ? $"{name} cannot be {verb}: its name ends in a dot or a space, which Windows drops, so the operation would reach another item."
            : $"{name} cannot be {verb}: the folder {folder} on its way ends in a dot or a space, which Windows drops, so the operation would reach another item.");
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
    /// One modern IFileOperation over every item. Stopped by the user - Cancel,
    /// or No to one of its questions - it is an
    /// <see cref="OperationCanceledException"/>, which callers have always
    /// taken as "cancelled"; any other failure is an <see cref="IOException"/>.
    /// Shell items, rather than SHFileOperation's legacy double-null path
    /// buffer, keep long paths out of the crashing MAX_PATH implementation.
    /// </summary>
    private static void RunShellOperation(
        IntPtr owner,
        ShellFileOperationKind operation,
        IReadOnlyList<string> sources,
        string? targetDirectory,
        ShellFileOperationFlags flags)
        => ShellFileOperation.Run(owner, operation, sources, targetDirectory, flags);

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
