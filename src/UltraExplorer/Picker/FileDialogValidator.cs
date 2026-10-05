namespace UltraExplorer.Picker;

/// <summary>A question the user has already answered, so it is not asked twice.</summary>
public enum FileDialogGate
{
    Overwrite,
    Create,
    ShareViolation
}

public enum FileDialogVerdictKind
{
    Accept,
    Reject,
    ConfirmOverwrite,
    ConfirmCreate,
    ConfirmShareViolation
}

/// <summary>
/// What should happen when OK is pressed.  Anything needing a yes/no goes back
/// to the window as a confirmation rather than being decided here, which keeps
/// every rule in this file testable without a UI.
/// </summary>
public sealed record FileDialogVerdict(
    FileDialogVerdictKind Kind,
    IReadOnlyList<string> Paths,
    string Message = "",
    string Caption = "")
{
    public bool IsAccept => Kind == FileDialogVerdictKind.Accept;

    public bool IsQuestion => Kind is FileDialogVerdictKind.ConfirmOverwrite
        or FileDialogVerdictKind.ConfirmCreate
        or FileDialogVerdictKind.ConfirmShareViolation;

    public FileDialogGate Gate => Kind switch
    {
        FileDialogVerdictKind.ConfirmOverwrite => FileDialogGate.Overwrite,
        FileDialogVerdictKind.ConfirmCreate => FileDialogGate.Create,
        _ => FileDialogGate.ShareViolation
    };
}

/// <summary>
/// Enforces the validation half of <c>FILEOPENDIALOGOPTIONS</c>: which items may
/// be returned, which must exist, what has to be confirmed first.
/// </summary>
public static class FileDialogValidator
{
    public static FileDialogVerdict Evaluate(
        FileDialogRequest request,
        FileDialogFilter filter,
        IReadOnlyList<string> candidates,
        IReadOnlySet<FileDialogGate>? resolved = null)
    {
        resolved ??= new HashSet<FileDialogGate>();

        if (candidates.Count == 0)
        {
            return Reject(request.PicksFolders
                ? "Choose a folder first."
                : "Type or choose a file name.");
        }

        var paths = candidates;
        if (!request.AllowsMultipleSelection && paths.Count > 1)
        {
            paths = [paths[0]];
        }

        // A replaced dialog hands a shortcut back as it is: the application's
        // own dialog follows it, or not, as that application asked it to.
        if (!request.Has(FileDialogOptions.NoDereferenceLinks) && !request.IsSave && !request.IsNativeProxy)
        {
            paths = [.. paths.Select(ShellLinkResolver.Resolve)];
        }

        if (request.Has(FileDialogOptions.NoValidate))
        {
            return new FileDialogVerdict(FileDialogVerdictKind.Accept, paths);
        }

        foreach (var path in paths)
        {
            if (!IsWellFormed(path))
            {
                return Reject($"The file name is not valid:\n{path}");
            }
        }

        return request.Mode switch
        {
            FileDialogMode.PickFolder => EvaluateFolders(request, paths),
            FileDialogMode.Save => EvaluateSave(request, filter, paths[0], resolved),
            _ => EvaluateOpen(request, filter, paths, resolved)
        };
    }

    private static FileDialogVerdict EvaluateFolders(FileDialogRequest request, IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                continue;
            }

            if (File.Exists(path))
            {
                return Reject($"{Path.GetFileName(path)} is a file, not a folder.");
            }

            if (request.Has(FileDialogOptions.PathMustExist))
            {
                return Reject($"{path}\nThat folder does not exist.");
            }
        }

        return new FileDialogVerdict(FileDialogVerdictKind.Accept, paths);
    }

    private static FileDialogVerdict EvaluateOpen(
        FileDialogRequest request,
        FileDialogFilter filter,
        IReadOnlyList<string> paths,
        IReadOnlySet<FileDialogGate> resolved)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                return Reject($"{Path.GetFileName(path)} is a folder. Open it to choose a file inside it.");
            }

            if (request.Has(FileDialogOptions.PathMustExist) && !DirectoryOfExists(path))
            {
                return Reject($"{path}\nThe path does not exist.");
            }

            // CREATEPROMPT (OFN_CREATEPROMPT) is above all an Open flag: a name
            // that is not there yet is offered for creation, and that offer
            // is what the caller asked for even where FILEMUSTEXIST would
            // otherwise refuse it.
            var exists = File.Exists(path);
            if (!exists
                && request.Has(FileDialogOptions.CreatePrompt)
                && !resolved.Contains(FileDialogGate.Create))
            {
                return new FileDialogVerdict(
                    FileDialogVerdictKind.ConfirmCreate,
                    [path],
                    $"{Path.GetFileName(path)} does not exist.\nDo you want to create it?",
                    "Confirm Open");
            }

            if (request.Has(FileDialogOptions.FileMustExist) && !exists
                && !(request.Has(FileDialogOptions.CreatePrompt) && resolved.Contains(FileDialogGate.Create)))
            {
                return Reject($"{path}\nFile not found. Check the file name and try again.");
            }

            if (request.Has(FileDialogOptions.StrictFileTypes)
                && !filter.Matches(Path.GetFileName(path)))
            {
                return Reject($"{Path.GetFileName(path)}\nThat name does not match the selected file type.");
            }

            if (request.Has(FileDialogOptions.NoReadOnlyReturn) && exists && IsReadOnly(path))
            {
                return Reject($"{Path.GetFileName(path)}\nThat file is read-only.");
            }
        }

        return new FileDialogVerdict(FileDialogVerdictKind.Accept, paths);
    }

    private static FileDialogVerdict EvaluateSave(
        FileDialogRequest request,
        FileDialogFilter filter,
        string path,
        IReadOnlySet<FileDialogGate> resolved)
    {
        if (Directory.Exists(path))
        {
            return Reject($"{Path.GetFileName(path)}\nA folder with that name already exists.");
        }

        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return Reject($"{path}\nThe path does not exist.");
        }

        if (request.Has(FileDialogOptions.StrictFileTypes)
            && !filter.Matches(Path.GetFileName(path)))
        {
            return Reject($"{Path.GetFileName(path)}\nThat name does not match the selected file type.");
        }

        var exists = File.Exists(path);

        if (exists && request.Has(FileDialogOptions.NoReadOnlyReturn) && IsReadOnly(path))
        {
            return Reject($"{Path.GetFileName(path)}\nThat file is read-only and cannot be replaced.");
        }

        if (exists
            && request.Has(FileDialogOptions.OverwritePrompt)
            && !resolved.Contains(FileDialogGate.Overwrite))
        {
            return new FileDialogVerdict(
                FileDialogVerdictKind.ConfirmOverwrite,
                [path],
                $"{Path.GetFileName(path)} already exists.\nDo you want to replace it?",
                "Confirm Save As");
        }

        if (exists
            && request.Has(FileDialogOptions.ShareAware)
            && !resolved.Contains(FileDialogGate.ShareViolation)
            && IsLocked(path))
        {
            return new FileDialogVerdict(
                FileDialogVerdictKind.ConfirmShareViolation,
                [path],
                $"{Path.GetFileName(path)} is open in another program.\nSave to it anyway?",
                "File In Use");
        }

        if (!exists
            && request.Has(FileDialogOptions.CreatePrompt)
            && !resolved.Contains(FileDialogGate.Create))
        {
            return new FileDialogVerdict(
                FileDialogVerdictKind.ConfirmCreate,
                [path],
                $"{Path.GetFileName(path)} does not exist.\nDo you want to create it?",
                "Confirm Save As");
        }

        if (!exists && !request.Has(FileDialogOptions.NoTestFileCreate) && !CanCreateIn(folder))
        {
            return Reject($"{folder}\nYou do not have permission to save in this folder.");
        }

        return new FileDialogVerdict(FileDialogVerdictKind.Accept, [path]);
    }

    private static FileDialogVerdict Reject(string message) =>
        new(FileDialogVerdictKind.Reject, [], message, "UltraExplorer");

    private static bool DirectoryOfExists(string path)
    {
        var folder = Path.GetDirectoryName(path);
        return !string.IsNullOrEmpty(folder) && Directory.Exists(folder);
    }

    private static bool IsWellFormed(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // Path.GetInvalidFileNameChars covers the separators too, so only the
        // last segment is checked against it.
        var name = Path.GetFileName(path);
        return name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static bool IsReadOnly(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsLocked(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Proves the folder is writable.  Windows tests the target name itself;
    /// this writes a uniquely named probe next to it instead, which answers the
    /// same question without ever touching a file the user did not choose.
    /// </summary>
    private static bool CanCreateIn(string folder)
    {
        var probe = Path.Combine(folder, $".ultraexplorer-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose))
            {
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch (IOException)
            {
                // DeleteOnClose already handles the normal path.
            }
        }
    }
}
