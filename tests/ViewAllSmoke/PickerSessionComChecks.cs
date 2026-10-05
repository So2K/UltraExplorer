using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using UltraExplorer.Models;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Com;

namespace ViewAllSmoke;

/// <summary>
/// The picker's rules that need no window: how a typed name becomes a path,
/// what the footer says about a choice, what changing the save type does to
/// the name, what a shortcut means, and what the COM dialog answers a caller
/// that hands it something that is not a folder on disk.
/// </summary>
internal static partial class Program
{
    private static Task PickerSessionComChecks(string root)
    {
        Section("picker session and COM: names, shortcuts and the COM object");

        PickerRootedNameChecks();
        PickerDriveRootFooterChecks(root);
        PickerSaveTypeChecks(root);
        PickerProxyTrailingDotChecks(root);
        PickerShortcutChecks(root);
        PickerComShellItemChecks();
        PickerComIdlePruneChecks();
        return Task.CompletedTask;
    }

    /// <summary>
    /// A name rooted without a drive, or a drive with no root, is completed
    /// from the folder in view, never from this process's own drive and
    /// per-drive folders.  The drive letters are ones this process is not on,
    /// so its own current folder can play no part.
    /// </summary>
    private static void PickerRootedNameChecks()
    {
        var own = char.ToUpperInvariant(Path.GetPathRoot(Environment.CurrentDirectory)![0]);
        var drive = own == 'Q' ? 'R' : 'Q';
        var other = own == 'S' ? 'T' : 'S';
        var music = $@"{drive}:\Music";

        Check("a name starting with a backslash goes on the drive of the folder in view",
            FileDialogNaming.ToFullPath(@"\Exports\mix.wav", music) == $@"{drive}:\Exports\mix.wav");
        Check("and on the share of the folder in view",
            FileDialogNaming.ToFullPath(@"\mix.wav", @"\\server\share\Music") == @"\\server\share\mix.wav");
        Check("a drive with a name and no root goes in the folder in view when that is on the drive",
            FileDialogNaming.ToFullPath($"{drive}:mix.wav", music) == $@"{drive}:\Music\mix.wav");
        Check("and in that drive's root when the folder in view is elsewhere",
            FileDialogNaming.ToFullPath($"{other}:mix.wav", music) == $@"{other}:\mix.wav");
        Check("with no folder in view a name with no drive resolves to nothing",
            FileDialogNaming.ToFullPath(@"\mix.wav", null) is null);
        Check("a drive letter alone still means its root",
            FileDialogNaming.ToFullPath($"{other}:", music) == $@"{other}:\");
    }

    private static void PickerDriveRootFooterChecks(string root)
    {
        var drive = Path.GetPathRoot(root)!;
        var folders = new FileDialogSession(new FileDialogRequest { Mode = FileDialogMode.PickFolder, InitialFolder = root });
        folders.ReportSelection([new SelectionItem(drive, true, 0)]);
        Check($"choosing a drive in a folder picker names the drive in the footer ({folders.PendingSelectionLabel})",
            folders.PendingSelectionLabel == $"Selected folder: {drive}");
        Check($"and says no location, which a drive does not have ({folders.PendingSelectionLocation})",
            folders.PendingSelectionLocation.Length == 0);

        var alpha = Path.Combine(root, "alpha");
        folders.ReportSelection([new SelectionItem(alpha, true, 0)]);
        Check("a folder in a folder still shows its name and where it is",
            folders.PendingSelectionLabel == "Selected folder: alpha" && folders.PendingSelectionLocation == $"Location: {root}");
    }

    private static void PickerSaveTypeChecks(string root)
    {
        var request = new FileDialogRequest { Mode = FileDialogMode.Save, InitialFolder = root };
        request.Filters.Add(new FileDialogFilterSpec("All Files", "*.*"));
        request.Filters.Add(new FileDialogFilterSpec("PDF", "*.pdf"));
        request.Filters.Add(new FileDialogFilterSpec("Text", "*.txt"));
        var save = new FileDialogSession(request);

        save.FileNameText = "Report 01.10.2026";
        save.SelectedFilterIndex = 1;
        Check($"choosing a type after All Files keeps a name with dots whole and adds the type ({save.FileNameText})",
            save.FileNameText == "Report 01.10.2026.pdf");
        Check("and OK saves it with that type",
            save.Prepare([]) is { Kind: FileDialogActionKind.Accept } saved
            && Path.GetFileName(saved.Paths[0]) == "Report 01.10.2026.pdf");

        save.SelectedFilterIndex = 0;
        save.FileNameText = "notes.txt";
        save.SelectedFilterIndex = 1;
        Check($"after All Files, an extension one of the dialog's types names is still retyped ({save.FileNameText})",
            save.FileNameText == "notes.pdf");
    }

    private static void PickerProxyTrailingDotChecks(string root)
    {
        var request = new FileDialogRequest
        {
            IsNativeProxy = true, Mode = FileDialogMode.Save, InitialFolder = root,
            Options = FileDialogOptions.ForceFileSystem | FileDialogOptions.PathMustExist | FileDialogOptions.NoTestFileCreate
        };
        request.Filters.Add(new FileDialogFilterSpec("Text documents (*.txt)", "*.txt"));
        var proxy = new FileDialogSession(request);
        proxy.FileNameText = "README.";
        Check("a replaced Save dialog hands back a typed trailing dot, which asks the application for no extension",
            proxy.Prepare([]) is { Kind: FileDialogActionKind.Accept } typed
            && typed.Paths.Count == 1 && typed.Paths[0] == Path.Combine(root, "README."));
        proxy.FileNameText = "README.md";
        Check("an ordinary name goes back as typed",
            proxy.Prepare([]) is { Kind: FileDialogActionKind.Accept } plain && plain.Paths[0] == Path.Combine(root, "README.md"));
    }

    /// <summary>
    /// A shortcut to a folder opens the folder, in an Open and in a Save
    /// dialog alike, unless the caller asked for shortcuts themselves.  A
    /// replaced dialog hands a shortcut to a file back as it is: the
    /// application's own dialog follows it, or not, as that application asked.
    /// </summary>
    private static void PickerShortcutChecks(string root)
    {
        var links = Path.Combine(root, "review-links");
        Directory.CreateDirectory(links);
        try
        {
            var alpha = Path.Combine(root, "alpha");
            var readme = Path.Combine(root, "readme.txt");
            var projects = Path.Combine(links, "Projects.lnk");
            var note = Path.Combine(links, "note.lnk");
            if (!TryMakeShortcut(projects, alpha) || !TryMakeShortcut(note, readme))
            {
                Check("the shortcuts for the checks could be made", false);
                return;
            }

            foreach (var mode in new[] { FileDialogMode.Open, FileDialogMode.Save })
            {
                var session = new FileDialogSession(new FileDialogRequest { Mode = mode, InitialFolder = links });
                session.FileNameText = "Projects.lnk";
                var typed = session.Prepare([]);
                Check($"{mode}: typing the name of a shortcut to a folder opens the folder",
                    typed.Kind == FileDialogActionKind.Navigate && ViewAllPath.Equals(typed.Folder, alpha));

                session.FileNameText = string.Empty;
                session.ReportSelection([projects]);
                var chosen = session.Prepare([projects]);
                Check($"{mode}: OK on a chosen shortcut to a folder opens the folder",
                    chosen.Kind == FileDialogActionKind.Navigate && ViewAllPath.Equals(chosen.Folder, alpha));
            }

            var literal = new FileDialogSession(new FileDialogRequest
            {
                InitialFolder = links, Options = FileDialogOptions.NoDereferenceLinks | FileDialogOptions.PathMustExist
            });
            literal.FileNameText = "Projects.lnk";
            Check("a caller that asked for shortcuts themselves gets the shortcut",
                literal.Prepare([]) is { Kind: FileDialogActionKind.Accept } kept && kept.Paths[0] == projects);

            var any = FileDialogFilter.MatchAll;
            var direct = new FileDialogRequest { Options = FileDialogOptions.PathMustExist }.Normalize();
            Check("the app's own Open dialog still returns what a shortcut to a file points at",
                FileDialogValidator.Evaluate(direct, any, [note]) is { IsAccept: true } resolved
                && ViewAllPath.Equals(resolved.Paths[0], readme));
            var proxy = new FileDialogRequest
            {
                IsNativeProxy = true,
                Options = FileDialogOptions.ForceFileSystem | FileDialogOptions.PathMustExist | FileDialogOptions.NoTestFileCreate
            }.Normalize();
            Check("a replaced Open dialog hands the shortcut itself to the application's dialog",
                FileDialogValidator.Evaluate(proxy, any, [note]) is { IsAccept: true } passed && passed.Paths[0] == note);
        }
        finally
        {
            TryDelete(links);
        }
    }

    private static bool TryMakeShortcut(string path, string target)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.Save();
            return File.Exists(path);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or ArgumentException)
        {
            Console.WriteLine($"  shortcut {path}: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// This PC, a library, a phone: nothing the canvas can open, and nothing
    /// the system dialog refuses.  A caller that stops at its first failed
    /// HRESULT must still reach Show.
    /// </summary>
    private static void PickerComShellItemChecks()
    {
        var id = ShellNative.ShellItemId;
        IShellItem thisPc;
        try
        {
            thisPc = ShellNative.SHCreateItemFromParsingName("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", IntPtr.Zero, ref id);
        }
        catch (COMException exception)
        {
            Check($"This PC has a shell item ({exception.Message})", false);
            return;
        }

        var dialog = new UltraFileOpenDialog();
        Check("SetFolder with This PC is accepted, as the system dialog accepts it",
            dialog.SetFolder(thisPc) == Hresult.Ok);
        Check("AddPlace with This PC is accepted too", dialog.AddPlace(thisPc, FileDialogAddPlacement.Top) == Hresult.Ok);
        Check("and neither becomes a folder the canvas would try to open",
            dialog.GetFolder(out _) == Hresult.Fail);
        Check("no item at all is still an invalid argument", dialog.SetFolder(null!) == Hresult.InvalidArg
            && dialog.AddPlace(null!, FileDialogAddPlacement.Bottom) == Hresult.InvalidArg);
        Marshal.ReleaseComObject(thisPc);
    }

    /// <summary>
    /// An object its client has let go of is let go of here at the next look,
    /// whatever else the server is doing: while another dialog is on screen or
    /// a client holds a lock, it would otherwise keep its dialog's whole
    /// window alive.
    /// </summary>
    private static void PickerComIdlePruneChecks()
    {
        var host = typeof(ComServerHost);
        var live = (List<IntPtr>)host.GetField("Live", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var checkIdle = host.GetMethod("CheckIdle", BindingFlags.Static | BindingFlags.NonPublic)!;

        // One reference, which the server takes over: the client is gone.
        var released = Marshal.GetIUnknownForObject(new object());
        ComServerHost.Track(released);
        ComServerHost.Lock(true);
        try
        {
            checkIdle.Invoke(null, [null]);
            Check("an object its client released is let go of while the server is locked", !live.Contains(released));
        }
        finally
        {
            ComServerHost.Lock(false);
            if (live.Remove(released))
            {
                Marshal.Release(released);
            }
        }
    }
}
