using System.IO;
using UltraExplorer.Models;
using UltraExplorer.Picker;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The file-dialog half of UltraExplorer: what a caller can ask for, what the
/// picker shows, and what it is allowed to hand back.  All of it runs without a
/// window, which is the point — the rules are where the bugs would be.
/// </summary>
internal static partial class Program
{
    private static Task PickerFilters()
    {
        Section("picker file types");

        var all = FileDialogFilter.Parse("*.*");
        Check("*.* matches anything", all.Matches("readme") && all.Matches("a.tar.gz"));
        Check("*.* is recognised as no filter at all", all.MatchesEverything);

        var media = FileDialogFilter.Parse("*.mp4;*.mov; *.mkv");
        Check("a listed extension matches", media.Matches("clip.MP4") && media.Matches("a.mkv"));
        Check("an unlisted extension does not", !media.Matches("clip.avi"));
        Check("spaces around a pattern are ignored", media.Matches("holiday.mkv"));
        Check("the first extension is the one a save would append", media.PreferredExtension == "mp4");

        var irregular = FileDialogFilter.Parse("data??.log;report*2026*.csv");
        Check("? matches exactly one character", irregular.Matches("data01.log") && !irregular.Matches("data1.log"));
        Check("several wildcards in one pattern work", irregular.Matches("report-q1-2026-final.csv"));
        Check("a near miss is rejected", !irregular.Matches("report-2025.csv"));

        var exact = FileDialogFilter.Parse("makefile");
        Check("a pattern with no wildcard is an exact name", exact.Matches("Makefile") && !exact.Matches("makefile.txt"));

        var piped = FileDialogFilter.ParseSpecs("Video|*.mp4;*.mov|All Files|*.*");
        Check("a pipe-separated filter splits into entries", piped.Count == 2);
        Check("names and patterns land in the right places",
            piped[0].Name == "Video" && piped[0].Pattern == "*.mp4;*.mov" && piped[1].Pattern == "*.*");
        Check("the combo shows the patterns next to the name",
            piped[0].DisplayText == "Video (*.mp4;*.mov)");

        var nullSeparated = FileDialogFilter.ParseSpecs("Text\0*.txt\0All\0*.*\0");
        Check("an OPENFILENAME double-null filter parses too",
            nullSeparated.Count == 2 && nullSeparated[0].Pattern == "*.txt");

        Check("a wildcard is recognised as a pattern", FileDialogFilter.LooksLikePattern("*.log"));
        Check("an ordinary name is not", !FileDialogFilter.LooksLikePattern("notes.log"));

        return Task.CompletedTask;
    }

    private static Task PickerCommandLine()
    {
        Section("picker command line");

        Check("--pick is what turns the app into a dialog",
            FileDialogCommandLine.IsPickerInvocation(["--pick", "--folder"]));
        Check("without it the app is just the app",
            !FileDialogCommandLine.IsPickerInvocation(["--capture", "shot.png"]));

        var parsed = FileDialogCommandLine.TryParse(
            [
                "--pick", "--mode", "save", "--filter", "Text|*.txt|All Files|*.*",
                "--filter-index", "2", "--file-name", "notes", "--ext", ".txt",
                "--title", "Export component", "--ok-label", "Export",
                "--result", "out.json", "--json"
            ],
            out var invocation,
            out var error);

        Check($"a full command line parses ({error})", parsed);
        Check("the mode comes through", invocation.Request.Mode == FileDialogMode.Save);
        Check("both file types come through", invocation.Request.Filters.Count == 2);
        Check("the starting file type is one-based", invocation.Request.FileTypeIndex == 2);
        Check("the leading dot is trimmed off the default extension",
            invocation.Request.DefaultExtension == "txt");
        Check("the title and button label come through",
            invocation.Request.Title == "Export component" && invocation.Request.EffectiveOkLabel == "Export");
        Check("a save asks before overwriting unless told otherwise",
            invocation.Request.Has(FileDialogOptions.OverwritePrompt));
        Check("the result file is remembered", invocation.ResultPath == "out.json" && invocation.PrintJson);

        FileDialogCommandLine.TryParse(["--pick"], out var bare, out _);
        Check("an open dialog defaults to requiring the file to exist",
            bare.Request.Has(FileDialogOptions.FileMustExist) && bare.Request.Has(FileDialogOptions.PathMustExist));
        Check("an open dialog with no file types offers All Files",
            bare.Request.Filters.Count == 1 && bare.Request.Filters[0].Pattern == "*.*");

        FileDialogCommandLine.TryParse(["--pick", "--folder", "--start", @"C:\Windows"], out var folder, out _);
        Check("folder mode sets the PickFolders option",
            folder.Request.PicksFolders && folder.Request.Has(FileDialogOptions.PickFolders));
        Check("a folder dialog is not given file types", folder.Request.Filters.Count == 0);
        Check("a path value is not mistaken for name:value",
            folder.Request.InitialFolder == @"C:\Windows");

        // OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_ALLOWMULTISELECT | OFN_HIDEREADONLY
        FileDialogCommandLine.TryParse(["--pick", "--ofn", "0x1A04"], out var legacy, out _);
        Check("OPENFILENAME flags translate",
            legacy.Request.Has(FileDialogOptions.FileMustExist)
            && legacy.Request.Has(FileDialogOptions.PathMustExist)
            && legacy.Request.Has(FileDialogOptions.AllowMultiSelect));
        Check("flags with no modern equivalent are dropped, not guessed",
            !legacy.Request.Has(FileDialogOptions.NoValidate));

        // BIF_RETURNONLYFSDIRS | BIF_USENEWUI | BIF_BROWSEINCLUDEFILES | BIF_NONEWFOLDERBUTTON
        FileDialogCommandLine.TryParse(["--pick", "--bif", "0x4251"], out var browse, out _);
        Check("BROWSEINFO flags mean a folder dialog", browse.Request.Mode == FileDialogMode.PickFolder);
        Check("BIF_RETURNONLYFSDIRS becomes ForceFileSystem",
            browse.Request.Has(FileDialogOptions.ForceFileSystem));
        Check("BIF_BROWSEINCLUDEFILES still shows the files",
            browse.Request.ShowFilesWhilePickingFolders && browse.Request.ShowsFiles);
        Check("BIF_NONEWFOLDERBUTTON hides the New folder command",
            browse.Request.HideNewFolderButton);

        FileDialogCommandLine.TryParse(
            ["--pick", "--options", "0x1000", "--flag", "FOS_ALLOWMULTISELECT", "--flag", "hidemruplaces"],
            out var flags,
            out _);
        Check("a raw FOS mask is honoured", flags.Request.Has(FileDialogOptions.FileMustExist));
        Check("flags spelled the SDK way are understood",
            flags.Request.Has(FileDialogOptions.AllowMultiSelect));
        Check("flag names are case-insensitive", flags.Request.Has(FileDialogOptions.HideMruPlaces));

        Check("an unknown switch is an error, not a silent no-op",
            !FileDialogCommandLine.TryParse(["--pick", "--frobnicate"], out _, out _));
        Check("a switch with no value is an error",
            !FileDialogCommandLine.TryParse(["--pick", "--title"], out _, out _));

        Check("--type adds one entry at a time",
            FileDialogCommandLine.TryParse(["--pick", "--type", "Images|*.png;*.jpg"], out var typed, out _)
            && typed.Request.Filters.Count == 1
            && typed.Request.Filters[0].Name == "Images");

        // A request file is how a caller sends a filter full of quotes and
        // semicolons without fighting the command line.
        var requestPath = Path.Combine(Path.GetTempPath(), $"picker-request-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(requestPath, """
                {
                  "mode": "open",
                  "flags": ["AllowMultiSelect", "FileMustExist"],
                  "filters": [
                    { "name": "Component", "pattern": "*.component;*.json" },
                    { "name": "All Files", "pattern": "*.*" }
                  ],
                  "title": "Import a component",
                  "folder": "C:\\Windows",
                  "clientGuid": "2b3d0f56-4f1e-4a3a-9c2c-9d1a5f6e7a10"
                }
                """);

            var ok = FileDialogCommandLine.TryParse(["--pick", "--request", requestPath], out var fromFile, out var readError);
            Check($"a JSON request loads ({readError})", ok);
            Check("its file types load", fromFile.Request.Filters.Count == 2);
            Check("its named flags load",
                fromFile.Request.AllowsMultipleSelection && fromFile.Request.Has(FileDialogOptions.FileMustExist));
            Check("its client guid loads",
                fromFile.Request.ClientGuid == Guid.Parse("2b3d0f56-4f1e-4a3a-9c2c-9d1a5f6e7a10"));

            var result = FileDialogJson.WriteResult(new FileDialogResult(true, [@"C:\a.txt", @"C:\b.txt"], 2));
            Check("a result serialises both the list and the first path",
                result.Contains("\"paths\"") && result.Contains("\"path\"") && result.Contains("\"fileTypeIndex\": 2"));
        }
        finally
        {
            TryDelete(requestPath);
        }

        return Task.CompletedTask;
    }

    private static Task PickerNames()
    {
        Section("picker names");

        Check("one name stays one name",
            FileDialogNaming.SplitTypedNames("my report.txt") is [{ } single] && single == "my report.txt");
        Check("quoted names split",
            FileDialogNaming.SplitTypedNames("\"a.txt\" \"b c.txt\"") is [{ } first, { } second]
            && first == "a.txt" && second == "b c.txt");
        Check("an unterminated quote still yields its name",
            FileDialogNaming.SplitTypedNames("\"a.txt") is [{ } lone] && lone == "a.txt");
        Check("nothing typed is no names", FileDialogNaming.SplitTypedNames("   ").Count == 0);

        Check("a bare name resolves against the folder in view",
            FileDialogNaming.ToFullPath("notes.txt", @"C:\Windows") == @"C:\Windows\notes.txt");
        Check("an absolute path is taken as is",
            FileDialogNaming.ToFullPath(@"C:\Windows\notepad.exe", @"D:\") == @"C:\Windows\notepad.exe");
        Check("a drive letter alone means its root",
            FileDialogNaming.ToFullPath("C:", @"D:\somewhere") == @"C:\");
        Check("environment variables expand",
            FileDialogNaming.ToFullPath("%WINDIR%", @"C:\") is { } expanded
            && expanded.EndsWith("Windows", StringComparison.OrdinalIgnoreCase));
        Check("a relative name with no folder in view resolves to nothing",
            FileDialogNaming.ToFullPath("notes.txt", null) is null);

        var text = FileDialogFilter.Parse("*.txt");
        Check("a bare name gets the default extension",
            FileDialogNaming.ApplyDefaultExtension("report", "txt", text) == "report.txt");
        Check("a name that already has one is left alone",
            FileDialogNaming.ApplyDefaultExtension("report.csv", "txt", text) == "report.csv");
        Check("a trailing dot means the user wants no extension",
            FileDialogNaming.ApplyDefaultExtension("report.", "txt", text) == "report");
        Check("with no default extension the file type supplies one",
            FileDialogNaming.ApplyDefaultExtension("report", null, text) == "report.txt");
        Check("All Files supplies none",
            FileDialogNaming.ApplyDefaultExtension("report", null, FileDialogFilter.MatchAll) == "report");

        Check("one selected file shows as its name",
            FileDialogNaming.Describe([@"C:\Windows\notepad.exe"]) == "notepad.exe");
        Check("several show quoted",
            FileDialogNaming.Describe([@"C:\a.txt", @"C:\b.txt"]) == "\"a.txt\" \"b.txt\"");

        return Task.CompletedTask;
    }

    private static Task PickerSessionRules(string root)
    {
        Section("picker session");

        var clientsPath = Path.Combine(Path.GetTempPath(), $"picker-clients-{Guid.NewGuid():N}.json");
        var clients = new FileDialogClientStore(clientsPath);
        try
        {
            var request = new FileDialogRequest
            {
                Mode = FileDialogMode.Open,
                InitialFolder = root,
                Options = FileDialogOptions.FileMustExist | FileDialogOptions.PathMustExist
            };
            request.Filters.Add(new FileDialogFilterSpec("Text", "*.txt"));
            request.Filters.Add(new FileDialogFilterSpec("All Files", "*.*"));

            var session = new FileDialogSession(request, clients);
            Check("the session starts in the folder that was asked for",
                ViewAllPath.Equals(session.CurrentFolder, root));
            Check("the first file type is selected", session.SelectedFilterIndex == 0 && session.FileTypeIndex == 1);
            Check("the graph filter is the selected file type",
                session.GraphFilter.Matches("readme.txt") && !session.GraphFilter.Matches("readme.md"));

            session.SelectedFilterIndex = 1;
            Check("choosing All Files stops filtering", session.GraphFilter.MatchesEverything);

            session.FileNameText = "*.md";
            var patternAction = session.Prepare([]);
            Check("a typed wildcard asks for a re-filter, not a file",
                patternAction.Kind == FileDialogActionKind.Filter && patternAction.Pattern == "*.md");

            session.ApplyTypedPattern("*.md");
            Check("the typed pattern becomes the filter",
                session.CurrentFilter.Matches("note.md") && !session.CurrentFilter.Matches("note.txt"));
            Check("applying it clears the name box", session.FileNameText.Length == 0);

            session.SelectedFilterIndex = 0;
            Check("picking a file type again drops the typed pattern",
                session.CurrentFilter.Matches("readme.txt"));

            session.FileNameText = "alpha";
            var navigate = session.Prepare([]);
            Check("typing a folder name navigates into it",
                navigate.Kind == FileDialogActionKind.Navigate
                && ViewAllPath.Equals(navigate.Folder, Path.Combine(root, "alpha")));

            session.FileNameText = "readme.txt";
            var accept = session.Prepare([]);
            Check("typing a file name accepts it",
                accept.Kind == FileDialogActionKind.Accept
                && ViewAllPath.Equals(accept.Paths[0], Path.Combine(root, "readme.txt")));

            session.FileNameText = "\"readme.txt\" \"missing.txt\"";
            Check("without multi-select only the first typed name survives",
                session.Prepare([]).Paths.Count == 1);

            // ---- multi-select ------------------------------------------------
            var multiRequest = new FileDialogRequest
            {
                InitialFolder = root,
                Options = FileDialogOptions.AllowMultiSelect
            };
            var multi = new FileDialogSession(multiRequest, clients);
            multi.FileNameText = "\"readme.txt\" \"other.txt\"";
            Check("with multi-select every typed name survives", multi.Prepare([]).Paths.Count == 2);

            multi.FileNameText = string.Empty;
            multi.ReportSelection([Path.Combine(root, "readme.txt"), Path.Combine(root, "alpha", "note.md")]);
            Check("a canvas selection fills the name box quoted",
                multi.FileNameText == "\"readme.txt\" \"note.md\"");

            // ---- saving ------------------------------------------------------
            var saveRequest = new FileDialogRequest
            {
                Mode = FileDialogMode.Save,
                InitialFolder = root,
                DefaultExtension = "txt"
            };
            saveRequest.Filters.Add(new FileDialogFilterSpec("Text", "*.txt"));
            saveRequest.Filters.Add(new FileDialogFilterSpec("Markdown", "*.md"));

            var save = new FileDialogSession(saveRequest, clients);
            save.FileNameText = "export";
            var saved = save.Prepare([]);
            Check("a save appends the default extension",
                saved.Kind == FileDialogActionKind.Accept
                && Path.GetFileName(saved.Paths[0]) == "export.txt");

            save.FileNameText = "export.txt";
            save.SelectedFilterIndex = 1;
            Check("changing the file type retypes the name, it does not stack extensions",
                save.FileNameText == "export.md");

            save.FileNameText = "keep.tar.gz";
            save.SelectedFilterIndex = 0;
            Check("a name the user spelled out is left alone", save.FileNameText == "keep.tar.gz");

            // ---- folder picking ----------------------------------------------
            var folderRequest = new FileDialogRequest
            {
                Mode = FileDialogMode.PickFolder,
                InitialFolder = root
            };
            var folder = new FileDialogSession(folderRequest, clients);
            Check("a folder dialog offers no file types", !folder.ShowsFileTypes);
            Check("a folder dialog does not filter the graph", folder.GraphFilter.MatchesEverything);
            Check("its label says folder", folder.FileNameLabel == "Folder:" && folder.OkLabel == "Select Folder");

            var here = folder.Prepare([]);
            Check("with nothing chosen it returns the folder in view",
                here.Kind == FileDialogActionKind.Accept && ViewAllPath.Equals(here.Paths[0], root));

            folder.ReportSelection([Path.Combine(root, "readme.txt")]);
            Check("a file cannot fill a folder dialog's name box", folder.FileNameText.Length == 0);
            folder.ReportSelection([Path.Combine(root, "alpha")]);
            Check("a folder can", folder.FileNameText == "alpha");

            // ---- the OK button that has to be earned --------------------------
            var guarded = new FileDialogSession(
                new FileDialogRequest
                {
                    InitialFolder = root,
                    FileName = "readme.txt",
                    Options = FileDialogOptions.OkButtonNeedsInteraction
                },
                clients);
            Check("OK starts disabled when the caller asked for interaction first", !guarded.CanAccept);
            guarded.MarkInteraction();
            Check("touching something enables it", guarded.CanAccept);

            // ---- remembering the caller ---------------------------------------
            var client = Guid.NewGuid();
            var first = new FileDialogSession(
                new FileDialogRequest { ClientGuid = client, InitialFolder = root },
                clients);
            first.CurrentFolder = Path.Combine(root, "alpha");
            first.RememberName("note.md");
            first.Remember(first.CurrentFolder);

            var second = new FileDialogSession(
                new FileDialogRequest { ClientGuid = client },
                clients);
            Check("the same caller comes back to where it was",
                ViewAllPath.Equals(second.CurrentFolder, Path.Combine(root, "alpha")));
            Check("and to the names it used", second.RecentNames.Contains("note.md"));

            var anonymous = new FileDialogSession(new FileDialogRequest(), clients);
            Check("a caller with no guid gets no history", anonymous.RecentNames.Count == 0);

            var forgetful = new FileDialogSession(
                new FileDialogRequest
                {
                    ClientGuid = client,
                    Options = FileDialogOptions.DontAddToRecent,
                    InitialFolder = root
                },
                clients);
            Check("DontAddToRecent means exactly that",
                ViewAllPath.Equals(forgetful.CurrentFolder, root) && forgetful.RecentNames.Count == 0);
        }
        finally
        {
            TryDelete(clientsPath);
        }

        return Task.CompletedTask;
    }

    private static Task PickerValidation(string root)
    {
        Section("picker validation");

        var existing = Path.Combine(root, "readme.txt");
        var missing = Path.Combine(root, "nope.txt");
        var text = FileDialogFilter.Parse("*.txt");

        var open = new FileDialogRequest
        {
            Options = FileDialogOptions.FileMustExist | FileDialogOptions.PathMustExist
        }.Normalize();

        Check("an existing file is accepted",
            FileDialogValidator.Evaluate(open, text, [existing]).IsAccept);
        Check("a missing file is refused when it must exist",
            FileDialogValidator.Evaluate(open, text, [missing]).Kind == FileDialogVerdictKind.Reject);
        Check("choosing nothing is refused",
            FileDialogValidator.Evaluate(open, text, []).Kind == FileDialogVerdictKind.Reject);
        Check("a folder is not a file",
            FileDialogValidator.Evaluate(open, text, [Path.Combine(root, "alpha")]).Kind == FileDialogVerdictKind.Reject);

        var lenient = new FileDialogRequest { Options = FileDialogOptions.NoValidate }.Normalize();
        Check("NoValidate hands back whatever was chosen",
            FileDialogValidator.Evaluate(lenient, text, [missing]).IsAccept);

        var strict = new FileDialogRequest
        {
            Options = FileDialogOptions.FileMustExist | FileDialogOptions.StrictFileTypes
        }.Normalize();
        Check("StrictFileTypes refuses a name the file type does not match",
            FileDialogValidator.Evaluate(strict, FileDialogFilter.Parse("*.md"), [existing]).Kind
                == FileDialogVerdictKind.Reject);

        var single = new FileDialogRequest { Options = FileDialogOptions.FileMustExist }.Normalize();
        Check("without multi-select only the first choice is returned",
            FileDialogValidator.Evaluate(single, text, [existing, existing]).Paths.Count == 1);

        // ---- folders --------------------------------------------------------
        var folder = new FileDialogRequest
        {
            Mode = FileDialogMode.PickFolder,
            Options = FileDialogOptions.PathMustExist
        }.Normalize();
        Check("an existing folder is accepted",
            FileDialogValidator.Evaluate(folder, FileDialogFilter.MatchAll, [Path.Combine(root, "beta")]).IsAccept);
        Check("a file is refused by a folder dialog",
            FileDialogValidator.Evaluate(folder, FileDialogFilter.MatchAll, [existing]).Kind
                == FileDialogVerdictKind.Reject);
        Check("a folder that does not exist is refused when it must",
            FileDialogValidator.Evaluate(folder, FileDialogFilter.MatchAll, [Path.Combine(root, "ghost")]).Kind
                == FileDialogVerdictKind.Reject);

        // ---- saving ---------------------------------------------------------
        var save = new FileDialogRequest
        {
            Mode = FileDialogMode.Save,
            Options = FileDialogOptions.OverwritePrompt | FileDialogOptions.PathMustExist
        }.Normalize();

        var overwrite = FileDialogValidator.Evaluate(save, text, [existing]);
        Check("saving over a file asks first", overwrite.Kind == FileDialogVerdictKind.ConfirmOverwrite);
        Check("the question names the file", overwrite.Message.Contains("readme.txt"));
        Check("answering yes lets it through",
            FileDialogValidator.Evaluate(save, text, [existing], new HashSet<FileDialogGate> { FileDialogGate.Overwrite })
                .IsAccept);
        Check("a new name needs no question",
            FileDialogValidator.Evaluate(save, text, [missing]).IsAccept);
        Check("saving onto a folder is refused",
            FileDialogValidator.Evaluate(save, text, [Path.Combine(root, "alpha")]).Kind
                == FileDialogVerdictKind.Reject);
        Check("saving into a folder that does not exist is refused",
            FileDialogValidator.Evaluate(save, text, [Path.Combine(root, "ghost", "x.txt")]).Kind
                == FileDialogVerdictKind.Reject);

        var createPrompt = new FileDialogRequest
        {
            Mode = FileDialogMode.Save,
            Options = FileDialogOptions.CreatePrompt | FileDialogOptions.PathMustExist
        }.Normalize();
        Check("CreatePrompt asks before making a new file",
            FileDialogValidator.Evaluate(createPrompt, text, [missing]).Kind == FileDialogVerdictKind.ConfirmCreate);

        var readOnly = Path.Combine(root, "locked.txt");
        File.WriteAllText(readOnly, "x");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        try
        {
            var noReadOnly = new FileDialogRequest
            {
                Mode = FileDialogMode.Save,
                Options = FileDialogOptions.NoReadOnlyReturn | FileDialogOptions.PathMustExist
            }.Normalize();
            Check("NoReadOnlyReturn refuses a read-only file",
                FileDialogValidator.Evaluate(noReadOnly, text, [readOnly]).Kind == FileDialogVerdictKind.Reject);
        }
        finally
        {
            File.SetAttributes(readOnly, FileAttributes.Normal);
            File.Delete(readOnly);
        }

        Check("an illegal name is refused",
            FileDialogValidator.Evaluate(open, text, [Path.Combine(root, "bad|name.txt")]).Kind
                == FileDialogVerdictKind.Reject);

        return Task.CompletedTask;
    }

    private static async Task PickerGraphRules(string root)
    {
        Section("picker graph rules");

        var hiddenSystem = Path.Combine(root, "hidden-system.txt");
        File.WriteAllText(hiddenSystem, "x");
        File.SetAttributes(hiddenSystem, FileAttributes.Hidden | FileAttributes.System);

        // A second file type next to readme.txt, so the filter has something to
        // leave out that sits in the same folder.
        var component = Path.Combine(root, "component.json");
        File.WriteAllText(component, "{}");

        try
        {
            var graph = new ViewAllGraphService(new ViewAllGraphOptions(
                ShowFiles: true,
                FileFilter: FileDialogFilter.Parse("*.txt")));
            await graph.InitializeAsync();

            var node = await Reveal(graph, root);
            Check("the folder opened", node is not null);

            var names = node!.Children.Select(child => child.DisplayName).ToArray();
            Check("a file the type matches is shown", names.Contains("readme.txt"));
            Check("a file it does not match is not", !names.Contains("component.json"));
            Check("folders are never filtered out",
                names.Contains("alpha") && names.Contains("beta") && names.Contains("wide"));
            Check("a hidden system file stays hidden", !names.Contains("hidden-system.txt"));

            // Re-reading a branch rebuilds its nodes, so the folder has to be
            // looked up again after every change of options.
            await graph.ApplyOptionsAsync(graph.Options with { IncludeHidden = true });
            node = Refetch(graph, root) ?? await Reveal(graph, root);
            Check("showing hidden items reveals it",
                node!.Children.Any(child => child.DisplayName == "hidden-system.txt"));

            await graph.ApplyOptionsAsync(graph.Options with { ShowFiles = false, IncludeHidden = false });
            node = Refetch(graph, root) ?? await Reveal(graph, root);
            Check("a folder dialog shows no files at all",
                node!.Children.Count > 0 && node.Children.All(child => child.IsDirectory));
            Check("but still every folder", node.Children.Count == 3);

            await graph.ApplyOptionsAsync(graph.Options with { ShowFiles = true, FileFilter = null });
            node = Refetch(graph, root) ?? await Reveal(graph, root);
            Check("dropping the filter brings the files back",
                node!.Children.Any(child => child.DisplayName == "component.json")
                && node.Children.Any(child => child.DisplayName == "readme.txt"));

            graph.Dispose();
        }
        finally
        {
            File.SetAttributes(hiddenSystem, FileAttributes.Normal);
            File.Delete(hiddenSystem);
            File.Delete(component);
        }
    }

    private static ViewAllNodeViewModel? Refetch(ViewAllGraphService graph, string path)
        => graph.TryGetNode(path, out var node) ? node : null;

    /// <summary>Opens every folder from a drive down to <paramref name="path"/>.</summary>
    private static async Task<ViewAllNodeViewModel?> Reveal(ViewAllGraphService graph, string path)
    {
        ViewAllNodeViewModel? current = null;
        foreach (var step in ViewAllPath.AncestorChain(path))
        {
            // A big folder on the way - a temp folder of thousands - lists
            // only its first few thousand at first; the rest is asked for.
            while (!graph.TryGetNode(step, out _) && current is { IsTruncated: true })
            {
                await graph.LoadMoreAsync(current);
            }

            if (!graph.TryGetNode(step, out var node))
            {
                return null;
            }

            await graph.ExpandAsync(node);
            current = node;
        }

        return current;
    }
}
