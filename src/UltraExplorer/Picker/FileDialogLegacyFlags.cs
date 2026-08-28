namespace UltraExplorer.Picker;

/// <summary>
/// <c>OPENFILENAME.Flags</c> from <c>commdlg.h</c>.  Programs written against
/// <c>GetOpenFileName</c> still outnumber the ones using <c>IFileDialog</c>, and
/// they already have this mask in hand.
/// </summary>
[Flags]
public enum OpenFileNameFlags : uint
{
    None = 0,
    ReadOnly = 0x00000001,
    OverwritePrompt = 0x00000002,
    HideReadOnly = 0x00000004,
    NoChangeDir = 0x00000008,
    ShowHelp = 0x00000010,
    EnableHook = 0x00000020,
    EnableTemplate = 0x00000040,
    EnableTemplateHandle = 0x00000080,
    NoValidate = 0x00000100,
    AllowMultiSelect = 0x00000200,
    ExtensionDifferent = 0x00000400,
    PathMustExist = 0x00000800,
    FileMustExist = 0x00001000,
    CreatePrompt = 0x00002000,
    ShareAware = 0x00004000,
    NoReadOnlyReturn = 0x00008000,
    NoTestFileCreate = 0x00010000,
    NoNetworkButton = 0x00020000,
    NoLongNames = 0x00040000,
    Explorer = 0x00080000,
    NoDereferenceLinks = 0x00100000,
    LongNames = 0x00200000,
    EnableIncludeNotify = 0x00400000,
    EnableSizing = 0x00800000,
    DontAddToRecent = 0x02000000,
    ForceShowHidden = 0x10000000
}

/// <summary>
/// <c>BROWSEINFO.ulFlags</c> from <c>shlobj_core.h</c>, used by every program
/// that still calls <c>SHBrowseForFolder</c> to ask for a directory.
/// </summary>
[Flags]
public enum BrowseInfoFlags : uint
{
    None = 0,
    ReturnOnlyFileSystemDirectories = 0x00000001,
    DontGoBelowDomain = 0x00000002,
    StatusText = 0x00000004,
    ReturnFileSystemAncestors = 0x00000008,
    EditBox = 0x00000010,
    Validate = 0x00000020,
    NewDialogStyle = 0x00000040,
    BrowseIncludeUrls = 0x00000080,
    UsageHint = 0x00000100,
    NoNewFolderButton = 0x00000200,
    NoTranslateTargets = 0x00000400,
    BrowseForComputer = 0x00001000,
    BrowseForPrinter = 0x00002000,
    BrowseIncludeFiles = 0x00004000,
    Shareable = 0x00008000,
    BrowseFileJunctions = 0x00010000
}

/// <summary>
/// Translates the two legacy flag sets into <see cref="FileDialogOptions"/>.
///
/// Flags describing chrome that the old dialogs had and this one does not —
/// hook procedures, dialog templates, the help button, the read-only checkbox —
/// are dropped deliberately rather than approximated.
/// </summary>
public static class FileDialogLegacyFlags
{
    public static void ApplyOpenFileName(FileDialogRequest request, OpenFileNameFlags flags)
    {
        var options = request.Options;

        Map(OpenFileNameFlags.OverwritePrompt, FileDialogOptions.OverwritePrompt);
        Map(OpenFileNameFlags.NoChangeDir, FileDialogOptions.NoChangeDir);
        Map(OpenFileNameFlags.NoValidate, FileDialogOptions.NoValidate);
        Map(OpenFileNameFlags.AllowMultiSelect, FileDialogOptions.AllowMultiSelect);
        Map(OpenFileNameFlags.PathMustExist, FileDialogOptions.PathMustExist);
        Map(OpenFileNameFlags.FileMustExist, FileDialogOptions.FileMustExist);
        Map(OpenFileNameFlags.CreatePrompt, FileDialogOptions.CreatePrompt);
        Map(OpenFileNameFlags.ShareAware, FileDialogOptions.ShareAware);
        Map(OpenFileNameFlags.NoReadOnlyReturn, FileDialogOptions.NoReadOnlyReturn);
        Map(OpenFileNameFlags.NoTestFileCreate, FileDialogOptions.NoTestFileCreate);
        Map(OpenFileNameFlags.NoDereferenceLinks, FileDialogOptions.NoDereferenceLinks);
        Map(OpenFileNameFlags.DontAddToRecent, FileDialogOptions.DontAddToRecent);
        Map(OpenFileNameFlags.ForceShowHidden, FileDialogOptions.ForceShowHidden);

        // OFN_FILEMUSTEXIST only means anything alongside OFN_PATHMUSTEXIST,
        // which is how the old dialog documented it.
        if ((flags & OpenFileNameFlags.FileMustExist) != 0)
        {
            options |= FileDialogOptions.PathMustExist;
        }

        request.Options = options;

        void Map(OpenFileNameFlags legacy, FileDialogOptions modern)
        {
            if ((flags & legacy) != 0)
            {
                options |= modern;
            }
        }
    }

    public static void ApplyBrowseInfo(FileDialogRequest request, BrowseInfoFlags flags)
    {
        request.Mode = FileDialogMode.PickFolder;
        request.Options |= FileDialogOptions.PickFolders;

        if ((flags & BrowseInfoFlags.ReturnOnlyFileSystemDirectories) != 0)
        {
            request.Options |= FileDialogOptions.ForceFileSystem;
        }

        if ((flags & BrowseInfoFlags.Validate) != 0)
        {
            request.Options |= FileDialogOptions.PathMustExist;
        }

        if ((flags & BrowseInfoFlags.NoTranslateTargets) != 0)
        {
            request.Options |= FileDialogOptions.NoDereferenceLinks;
        }

        if ((flags & BrowseInfoFlags.BrowseIncludeFiles) != 0)
        {
            request.ShowFilesWhilePickingFolders = true;
        }

        if ((flags & BrowseInfoFlags.NoNewFolderButton) != 0)
        {
            request.HideNewFolderButton = true;
        }
    }
}
