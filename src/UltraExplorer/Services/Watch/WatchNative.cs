using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UltraExplorer.Services.Watch;

/// <summary>
/// The Windows calls the watchers are made of: a directory handle opened for
/// overlapped change notifications, the two notification calls, and what the
/// volume resolver asks about drive letters.  Every signature is blittable
/// apart from the handle and the strings, so the call a watcher makes on each
/// completion marshals nothing and allocates nothing.
/// </summary>
internal static unsafe class WatchNative
{
    // ---- opening a directory for notifications ------------------------------------------

    public const uint FileListDirectory = 0x0001;
    public const uint FileShareAll = 0x1 | 0x2 | 0x4;
    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000;
    public const uint FileFlagOverlapped = 0x40000000;

    /// <summary>
    /// What a watch listens for: names of files and folders coming, going and
    /// being renamed, and a file's size, date or attributes changing - what a
    /// listing shows, and nothing it does not (no access times, no security).
    /// </summary>
    public const uint NotifyFilter = 0x1 /* FILE_NAME */ | 0x2 /* DIR_NAME */ | 0x4 /* ATTRIBUTES */ | 0x8 /* SIZE */ | 0x10 /* LAST_WRITE */;

    public const int ReadDirectoryNotifyInformation = 1;
    public const int ReadDirectoryNotifyExtendedInformation = 2;

    // ---- what a record says happened ----------------------------------------------------

    public const int ActionAdded = 1;
    public const int ActionRemoved = 2;
    public const int ActionModified = 3;
    public const int ActionRenamedOld = 4;
    public const int ActionRenamedNew = 5;

    public const uint FileAttributeHidden = 0x2;
    public const uint FileAttributeSystem = 0x4;
    public const uint FileAttributeDirectory = 0x10;

    /// <summary>Where a record's fields sit in a FILE_NOTIFY_INFORMATION.</summary>
    public const int PlainNameLengthOffset = 8;
    public const int PlainNameOffset = 12;

    /// <summary>Where a record's fields sit in a FILE_NOTIFY_EXTENDED_INFORMATION.</summary>
    public const int ExtendedLastWriteOffset = 16;
    public const int ExtendedSizeOffset = 48;
    public const int ExtendedAttributesOffset = 56;
    public const int ExtendedNameLengthOffset = 80;
    public const int ExtendedNameOffset = 84;

    /// <summary>A FILETIME's count of 100 ns since 1601 plus this is the same instant in <see cref="DateTime"/> ticks.</summary>
    public const long FileTimeToDateTimeTicks = 504911232000000000L;

    // ---- errors ---------------------------------------------------------------------------

    public const int ErrorInvalidFunction = 1;
    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;
    public const int ErrorNotReady = 21;
    public const int ErrorSharingViolation = 32;
    public const int ErrorNotSupported = 50;
    public const int ErrorBadNetPath = 53;
    public const int ErrorUnexpectedNetworkError = 59;
    public const int ErrorNetworkNameDeleted = 64;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorOperationAborted = 995;
    public const int ErrorIoPending = 997;
    public const int ErrorNotifyEnumDir = 1022;
    public const int ErrorConnectionUnavailable = 1201;

    /// <summary>
    /// Whether a volume refused the watch itself rather than failed to keep
    /// one: the file system does not do change notifications (WSL's, some
    /// network redirectors), so asking again will not help and the volume is
    /// polled instead.
    /// </summary>
    public static bool IsRefusal(int error) =>
        error is ErrorInvalidFunction or ErrorNotSupported or ErrorInvalidParameter;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    public static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern int ReadDirectoryChangesW(
        SafeFileHandle directory, void* buffer, uint bufferLength, int watchSubtree, uint notifyFilter,
        uint* bytesReturned, NativeOverlapped* overlapped, IntPtr completionRoutine);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern int ReadDirectoryChangesExW(
        SafeFileHandle directory, void* buffer, uint bufferLength, int watchSubtree, uint notifyFilter,
        uint* bytesReturned, NativeOverlapped* overlapped, IntPtr completionRoutine, int notifyInformationClass);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern int CancelIoEx(SafeFileHandle file, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern int GetVolumeInformationByHandleW(
        SafeFileHandle file, char* volumeName, uint volumeNameSize, uint* serialNumber,
        uint* maximumComponentLength, uint* fileSystemFlags, char* fileSystemName, uint fileSystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern uint GetLongPathNameW(char* shortPath, char* longPath, uint bufferLength);

    // ---- drive letters --------------------------------------------------------------------

    public const uint DriveUnknown = 0;
    public const uint DriveNoRootDirectory = 1;
    public const uint DriveRemovable = 2;
    public const uint DriveFixed = 3;
    public const uint DriveRemote = 4;
    public const uint DriveCdRom = 5;
    public const uint DriveRamDisk = 6;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint GetDriveTypeW(string rootPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    public static extern uint QueryDosDeviceW(string deviceName, [Out] char[] targetPath, int maximumLength);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int WNetGetConnectionW(string localName, [Out] char[] remoteName, ref int length);
}
