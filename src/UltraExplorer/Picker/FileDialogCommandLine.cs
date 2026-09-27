namespace UltraExplorer.Picker;

/// <summary>A parsed <c>--pick</c> command line.</summary>
public sealed record PickerInvocation(
    FileDialogRequest Request,
    string? ResultPath,
    bool PrintJson,
    bool PrintPathsOnly);

/// <summary>
/// The universal entry point: any program in any language can run
/// <c>UltraExplorer.exe --pick …</c> and read the answer back.  Every capability
/// of <c>IFileDialog</c> has a switch here, and the two legacy flag words have
/// one each, so a caller can forward the mask it already built.
/// </summary>
public static class FileDialogCommandLine
{
    public const string PickSwitch = "--pick";

    /// <summary>Accepted, cancelled and failed, as process exit codes.</summary>
    public const int ExitAccepted = 0;
    public const int ExitCancelled = 1;
    public const int ExitError = 2;

    public static bool IsPickerInvocation(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (IsSwitch(args[index], "pick"))
            {
                return true;
            }
        }

        return false;
    }

    public static bool WantsHelp(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (IsSwitch(args[index], "help") || IsSwitch(args[index], "?"))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryParse(IReadOnlyList<string> args, out PickerInvocation invocation, out string error)
    {
        invocation = null!;
        error = string.Empty;

        var request = new FileDialogRequest { Options = FileDialogOptions.None };
        var flagNames = new List<string>();
        var rawOptions = (string?)null;
        string? resultPath = null;
        var printJson = false;
        var printPathsOnly = false;

        try
        {
            for (var index = 0; index < args.Count; index++)
            {
                var (name, inlineValue) = Split(args[index]);
                if (name is null)
                {
                    continue;
                }

                switch (name)
                {
                    case "pick":
                    case "help":
                    case "?":
                        break;

                    case "request":
                        MergeRequestFile(request, Value());
                        break;

                    case "mode":
                        request.Mode = FileDialogJson.ParseMode(Value());
                        break;

                    case "open":
                        request.Mode = FileDialogMode.Open;
                        break;

                    case "save":
                        request.Mode = FileDialogMode.Save;
                        break;

                    case "folder":
                    case "pick-folder":
                        request.Mode = FileDialogMode.PickFolder;
                        break;

                    case "multiselect":
                    case "multi":
                        request.Options |= FileDialogOptions.AllowMultiSelect;
                        break;

                    case "options":
                        rawOptions = Value();
                        break;

                    case "flag":
                        flagNames.Add(Value());
                        break;

                    case "ofn":
                    case "open-file-name-flags":
                        if (!FileDialogJson.TryParseMask(Value(), out var ofn))
                        {
                            throw new FormatException($"Not a flag mask: {args[index]}");
                        }

                        FileDialogLegacyFlags.ApplyOpenFileName(request, (OpenFileNameFlags)ofn);
                        break;

                    case "bif":
                    case "browse-info-flags":
                        if (!FileDialogJson.TryParseMask(Value(), out var bif))
                        {
                            throw new FormatException($"Not a flag mask: {args[index]}");
                        }

                        FileDialogLegacyFlags.ApplyBrowseInfo(request, (BrowseInfoFlags)bif);
                        break;

                    case "filter":
                        foreach (var spec in FileDialogFilter.ParseSpecs(Unescape(Value())))
                        {
                            request.Filters.Add(spec);
                        }

                        break;

                    case "type":
                        request.Filters.Add(ParseType(Value()));
                        break;

                    case "filter-index":
                    case "type-index":
                        request.FileTypeIndex = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture);
                        break;

                    case "title":
                        request.Title = Value();
                        break;

                    case "ok-label":
                        request.OkButtonLabel = Value();
                        break;

                    case "name-label":
                        request.FileNameLabel = Value();
                        break;

                    case "file-name":
                    case "name":
                        request.FileName = Value();
                        break;

                    case "default-extension":
                    case "ext":
                        request.DefaultExtension = Value();
                        break;

                    case "folder-path":
                    case "start":
                    case "initial-folder":
                        request.InitialFolder = Value();
                        break;

                    case "default-folder":
                        request.DefaultFolder = Value();
                        break;

                    case "save-as-item":
                        request.SaveAsItem = Value();
                        break;

                    case "place":
                        request.Places.Add(new FileDialogPlace(Value(), Top: false));
                        break;

                    case "place-top":
                        request.Places.Add(new FileDialogPlace(Value(), Top: true));
                        break;

                    case "client-guid":
                        request.ClientGuid = Guid.Parse(Value());
                        break;

                    case "owner":
                        request.OwnerHandle = (nint)ParseHandle(Value());
                        break;

                    case "show-files":
                        request.ShowFilesWhilePickingFolders = true;
                        break;

                    case "no-new-folder":
                        request.HideNewFolderButton = true;
                        break;

                    case "result":
                    case "out":
                        resultPath = Value();
                        break;

                    case "json":
                        printJson = true;
                        break;

                    case "capture":
                        // Belongs to the screenshot harness, not to the dialog,
                        // but it has to be allowed through so a picker session
                        // can be captured like any other window.
                        Value();
                        break;

                    case "print":
                        printPathsOnly = true;
                        break;

                    default:
                        throw new FormatException($"Unknown switch: {args[index]}");
                }

                continue;

                string Value()
                {
                    if (inlineValue is not null)
                    {
                        return inlineValue;
                    }

                    if (index + 1 >= args.Count)
                    {
                        throw new FormatException($"{args[index]} needs a value");
                    }

                    return args[++index];
                }
            }
        }
        catch (Exception exception) when (exception is FormatException or OverflowException
            or IOException or InvalidDataException or System.Text.Json.JsonException
            or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = exception.Message;
            return false;
        }

        request.Options |= FileDialogJson.ParseOptions(rawOptions, flagNames);

        // Normalized first, so --flag PickFolders is a folder pick below as
        // much as --folder is.
        request.Normalize();

        // Nothing said otherwise, so behave like the standard dialog does out
        // of the box: an opened file exists (unless the caller offers to
        // create it), and a picked folder exists.
        if (request.PicksFolders
            && !request.Has(FileDialogOptions.NoValidate))
        {
            request.Options |= FileDialogOptions.PathMustExist;
        }
        else if (request.Mode == FileDialogMode.Open
            && !request.Has(FileDialogOptions.NoValidate)
            && (request.Options & (FileDialogOptions.FileMustExist | FileDialogOptions.PathMustExist)) == 0)
        {
            request.Options |= request.Has(FileDialogOptions.CreatePrompt)
                ? FileDialogOptions.PathMustExist
                : FileDialogOptions.FileMustExist | FileDialogOptions.PathMustExist;
        }

        if (request.Mode == FileDialogMode.Save
            && !request.Has(FileDialogOptions.NoValidate)
            && !request.Has(FileDialogOptions.OverwritePrompt))
        {
            request.Options |= FileDialogOptions.OverwritePrompt;
        }

        invocation = new PickerInvocation(request.Normalize(), resultPath, printJson, printPathsOnly);
        return true;
    }

    public static string Usage =>
        """
        UltraExplorer --pick [options]

        Shows the UltraExplorer canvas as a file dialog and prints what was
        chosen.  Exit code 0 accepted, 1 cancelled, 2 error.

        What is being picked
          --mode open|save|folder     job to do (default open)
          --open | --save | --folder  the same, spelled shorter
          --multiselect               allow more than one result
          --show-files                show files while picking a folder
          --no-new-folder             hide the New folder command

        File types
          --filter "Name|*.a;*.b|Other|*.c"   whole list in one string
          --type "Name|*.a;*.b"               one entry, repeatable
          --filter-index N                    one-based starting entry
          --ext txt                           extension added to a bare name

        Text
          --title s        --ok-label s        --name-label s
          --file-name s    pre-filled name

        Where to start
          --start path            open here
          --default-folder path   open here the first time only
          --save-as-item path     the file a Save As is re-saving
          --place path            pin into the sidebar (--place-top for the top)

        Options
          --options 0x1000        raw FOS_ mask
          --flag FileMustExist    named FOS_ flag, repeatable
          --ofn 0x1000            OPENFILENAME flags, translated
          --bif 0x41              BROWSEINFO flags, translated
          --client-guid g         remember this caller's folder and file type
          --owner 0x00040A12      owner window; the picker goes modal on it

        Answering back
          --result path   write the result as JSON to this file
          --json          print the result as JSON
          --print         print one chosen path per line (the default)
          --request path  read every setting above from a JSON file
        """;

    /// <summary>
    /// Takes what the file says and only that: a file that names no mode or
    /// file type leaves <c>--save</c> or <c>--filter-index</c> as they were,
    /// and a file with no file types adds no "All Files" in front of the
    /// command line's own <c>--type</c> entries.
    /// </summary>
    private static void MergeRequestFile(FileDialogRequest request, string path)
    {
        var document = FileDialogJson.ReadRequestDocument(path);
        var loaded = FileDialogJson.Build(document);

        if (document.Mode is not null)
        {
            request.Mode = loaded.Mode;
        }

        if (document.FileTypeIndex is not null)
        {
            request.FileTypeIndex = loaded.FileTypeIndex;
        }

        request.Options |= loaded.Options;

        Take(loaded.Title, value => request.Title = value);
        Take(loaded.OkButtonLabel, value => request.OkButtonLabel = value);
        Take(loaded.FileNameLabel, value => request.FileNameLabel = value);
        Take(loaded.FileName, value => request.FileName = value);
        Take(loaded.DefaultExtension, value => request.DefaultExtension = value);
        Take(loaded.InitialFolder, value => request.InitialFolder = value);
        Take(loaded.DefaultFolder, value => request.DefaultFolder = value);
        Take(loaded.SaveAsItem, value => request.SaveAsItem = value);

        if (loaded.ClientGuid != Guid.Empty)
        {
            request.ClientGuid = loaded.ClientGuid;
        }

        if (loaded.OwnerHandle != 0)
        {
            request.OwnerHandle = loaded.OwnerHandle;
        }

        request.ShowFilesWhilePickingFolders |= loaded.ShowFilesWhilePickingFolders;
        request.HideNewFolderButton |= loaded.HideNewFolderButton;
        request.Filters.AddRange(loaded.Filters);
        request.Places.AddRange(loaded.Places);

        static void Take(string value, Action<string> assign)
        {
            if (!string.IsNullOrEmpty(value))
            {
                assign(value);
            }
        }
    }

    private static FileDialogFilterSpec ParseType(string text)
    {
        var separator = text.IndexOf('|');
        if (separator < 0)
        {
            // "*.png" alone is a perfectly clear file type.
            return new FileDialogFilterSpec(text.Trim(), text.Trim());
        }

        var name = text[..separator].Trim();
        var pattern = text[(separator + 1)..].Trim();
        return new FileDialogFilterSpec(
            name.Length == 0 ? pattern : name,
            pattern.Length == 0 ? "*.*" : pattern);
    }

    /// <summary>
    /// A command line cannot carry the NUL bytes an <c>OPENFILENAME</c> filter
    /// is made of, so callers write them as <c>\0</c> and we put them back.
    /// </summary>
    private static string Unescape(string text) =>
        text.Contains("\\0", StringComparison.Ordinal)
            ? text.Replace("\\0", "\0", StringComparison.Ordinal)
            : text;

    private static long ParseHandle(string text)
    {
        var cleaned = text.Trim();
        return cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(cleaned[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture)
            : long.Parse(cleaned, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool IsSwitch(string argument, string name) =>
        Split(argument).Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true;

    private static (string? Name, string? Value) Split(string argument)
    {
        if (argument.Length < 2 || (argument[0] != '-' && argument[0] != '/'))
        {
            return (null, null);
        }

        var body = argument.TrimStart('-', '/');
        var separator = body.IndexOfAny(['=', ':']);

        // A path value like "C:\x" must not be mistaken for name:value, so only
        // a separator before any backslash counts.
        if (separator > 0)
        {
            var backslash = body.IndexOf('\\');
            if (backslash < 0 || separator < backslash)
            {
                return (body[..separator].ToLowerInvariant(), body[(separator + 1)..]);
            }
        }

        return (body.ToLowerInvariant(), null);
    }
}
