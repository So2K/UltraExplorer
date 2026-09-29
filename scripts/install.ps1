<#
.SYNOPSIS
    Builds UltraExplorer in Release and installs it for the current user, so
    Start, Windows Search and launchers such as PowerToys Run find it by name.

.DESCRIPTION
    Publishes src/UltraExplorer framework-dependent for win-x64 into
    %LOCALAPPDATA%\Programs\UltraExplorer and puts an "UltraExplorer" shortcut
    in the Start menu.  Running it again updates the installed copy in place:
    a copy started from the install folder is asked to close first (it saves
    its state as it always does), and settings stay where they live, in
    %LOCALAPPDATA%\UltraExplorer.  Nothing needs administrator rights.

.PARAMETER Launch
    Start the installed copy afterwards.

.PARAMETER Desktop
    Also put a shortcut on the desktop.
#>
param(
    [switch]$Launch,
    [switch]$Desktop
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repository 'src\UltraExplorer\UltraExplorer.csproj'
$destination = Join-Path $env:LOCALAPPDATA 'Programs\UltraExplorer'
$executable = Join-Path $destination 'UltraExplorer.exe'
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("UltraExplorer-install-" + [guid]::NewGuid().ToString('N'))

# Build into a staging folder first, so a failed build never leaves a
# half-updated install behind.
Write-Host "Building UltraExplorer (Release)..."
dotnet publish $project -c Release -r win-x64 --self-contained false -nologo -v q -o $staging --artifacts-path (Join-Path $staging '.artifacts')
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
Remove-Item -Recurse -Force -LiteralPath (Join-Path $staging '.artifacts') -ErrorAction SilentlyContinue

# Only a copy running from the install folder holds its files; other copies
# (a development build, a test copy) are left alone.
$running = Get-Process UltraExplorer -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and [string]::Equals($_.Path, $executable, [System.StringComparison]::OrdinalIgnoreCase) }
foreach ($process in $running) {
    Write-Host "Closing the installed UltraExplorer (pid $($process.Id)) so it can be updated..."
    [void]$process.CloseMainWindow()
    if (-not $process.WaitForExit(15000)) { throw "UltraExplorer (pid $($process.Id)) did not close; close it and run this again." }
}

New-Item -ItemType Directory -Force -Path $destination | Out-Null
# The Setup installer (installer/UltraExplorer.iss) uses the same folder and
# keeps its uninstaller there (unins000.exe and .dat); removing those would
# leave an entry in Apps & features whose Uninstall fails.
Get-ChildItem -LiteralPath $destination -Force |
    Where-Object { $_.Name -notlike 'unins*' } |
    Remove-Item -Recurse -Force
Copy-Item -Path (Join-Path $staging '*') -Destination $destination -Recurse -Force
Remove-Item -Recurse -Force -LiteralPath $staging

# The app sets this taskbar id on itself (App.AppUserModelId); a shortcut that
# carries the same id is one taskbar entry with the running window.  Test and
# benchmark copies use another id, so they can never take over a pin.
$appUserModelId = 'UltraExplorer.App'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class UltraExplorerShortcutId
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey { public Guid FormatId; public uint PropertyId; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, int flags, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    public static void Set(string linkPath, string appId)
    {
        Guid interfaceId = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
        IPropertyStore store;
        SHGetPropertyStoreFromParsingName(linkPath, IntPtr.Zero, 2, ref interfaceId, out store);
        try
        {
            PropertyKey key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 };
            PropVariant value = new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(appId) };
            try
            {
                Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value));
                Marshal.ThrowExceptionForHR(store.Commit());
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }
}
'@

$shell = New-Object -ComObject WScript.Shell
function Set-Shortcut([string]$path) {
    $shortcut = $shell.CreateShortcut($path)
    $shortcut.TargetPath = $executable
    $shortcut.WorkingDirectory = $destination
    $shortcut.IconLocation = "$executable,0"
    $shortcut.Description = 'UltraExplorer - a file manager on a zoomable canvas'
    $shortcut.Save()
    [UltraExplorerShortcutId]::Set($path, $appUserModelId)
}

$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'UltraExplorer.lnk'
Set-Shortcut $startMenu
Write-Host "Start menu shortcut: $startMenu"

# A taskbar pin of UltraExplorer that points at some other copy (a build
# folder, a test copy) is pointed back at the installed one.
$taskbar = Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
if (Test-Path -LiteralPath $taskbar) {
    foreach ($pin in Get-ChildItem -LiteralPath $taskbar -Filter '*.lnk') {
        $target = $shell.CreateShortcut($pin.FullName).TargetPath
        if ($target -and [System.IO.Path]::GetFileName($target) -ieq 'UltraExplorer.exe') {
            Set-Shortcut $pin.FullName
            Write-Host "Taskbar pin pointed at the installed copy: $($pin.FullName)"
        }
    }
}
if ($Desktop) {
    $desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'UltraExplorer.lnk'
    Set-Shortcut $desktopShortcut
    Write-Host "Desktop shortcut: $desktopShortcut"
}

Write-Host "Installed: $executable"
if ($Launch) {
    Start-Process -FilePath $executable -WorkingDirectory $destination
}
