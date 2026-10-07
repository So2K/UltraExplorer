<#
.SYNOPSIS
    Builds UltraExplorer in Release and installs it for the current user, so
    Start, Windows Search and launchers such as PowerToys Run find it by name.

.DESCRIPTION
    Publishes src/UltraExplorer self-contained for win-x64 into
    %LOCALAPPDATA%\Programs\UltraExplorer and puts an "UltraExplorer" shortcut
    in the Start menu.  Running it again updates the installed copy in place:
    installed dialog integration is recovered and stopped first, and ordinary
    installed windows close normally and save their state. The prior dialog
    preference is restored after the update. Settings stay where they live, in
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
. (Join-Path $PSScriptRoot 'InstallPayload.ps1')
$repository = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repository 'src\UltraExplorer\UltraExplorer.csproj'
& (Join-Path $PSScriptRoot 'setup-archive-tools.ps1')
if (!(Test-Path -LiteralPath (Join-Path $repository 'runtime/preview/f3d/bin/f3d.exe')) -or
    !(Test-Path -LiteralPath (Join-Path $repository 'runtime/preview/mpv/mpv.exe'))) {
    & (Join-Path $PSScriptRoot 'setup-preview-tools.ps1')
}
$programsRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
$destination = Join-Path $programsRoot 'UltraExplorer'
$executable = Join-Path $destination 'UltraExplorer.exe'
$temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$staging = Join-Path $temporaryRoot ("UltraExplorer-install-" + [guid]::NewGuid().ToString('N'))
$backup = Join-Path $programsRoot ("UltraExplorer-update-backup-" + [guid]::NewGuid().ToString('N'))
$stateDirectory = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'UltraExplorer'))
$settingsPath = Join-Path $stateDirectory 'dialog-integration.json'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runName = 'UltraExplorer dialogs'

# Restart Manager closes owned modal/settings windows through the application's
# existing WM_QUERYENDSESSION save handler. Never use force-shutdown flags.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class UltraExplorerGracefulUpdate
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low, High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess { public uint Id; public FileTime Started; }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, uint flags, [Out] StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[] fileNames,
        uint processes, UniqueProcess[] processNames, uint services, string[] serviceNames);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmShutdown(uint session, uint flags, IntPtr callback);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);
    public static int Close(uint process, long started)
    {
        uint session;
        var result = RmStartSession(out session, 0, new StringBuilder(33));
        if (result != 0) return result;
        try
        {
            var identity = new UniqueProcess { Id = process,
                Started = new FileTime { Low = (uint)started, High = (uint)((ulong)started >> 32) } };
            result = RmRegisterResources(session, 0, null, 1, new[] { identity }, 0, null);
            return result == 0 ? RmShutdown(session, 0, IntPtr.Zero) : result;
        }
        finally { RmEndSession(session); }
    }
}
'@

function Assert-UpdatePath([string]$path, [string]$root) {
    $full = [System.IO.Path]::GetFullPath($path)
    $boundary = [System.IO.Path]::GetFullPath($root).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    if (-not $full.StartsWith($boundary + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Update path is outside its intended directory: $full"
    }
    # Do not traverse a junction/symlink during recursive removal or moving.
    $cursor = $full
    while ($cursor.Length -ge $boundary.Length) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                throw "Update path contains a filesystem link: $cursor"
            }
        }
        if ([string]::Equals($cursor, $boundary, [System.StringComparison]::OrdinalIgnoreCase)) { break }
        $cursor = Split-Path -Parent $cursor
    }
    if (Test-Path -LiteralPath $full -PathType Container) {
        $pending = New-Object 'System.Collections.Generic.Stack[string]'
        $pending.Push($full)
        while ($pending.Count -gt 0) {
            foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
                if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                    throw "Update directory contains a filesystem link: $($item.FullName)"
                }
                if ($item.PSIsContainer) { $pending.Push($item.FullName) }
            }
        }
    }
}

function Get-InstalledCopies {
    foreach ($entry in Get-CimInstance Win32_Process -Filter "Name = 'UltraExplorer.exe'") {
        if (-not [string]::Equals($entry.ExecutablePath, $executable, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        try { $process = Get-Process -Id $entry.ProcessId -ErrorAction Stop }
        catch [Microsoft.PowerShell.Commands.ProcessCommandException] { continue }
        if ($process.HasExited -or -not [string]::Equals($process.Path, $executable, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        if (-not $entry.CommandLine) { throw "Cannot identify installed UltraExplorer process $($process.Id)." }
        [pscustomobject]@{ Process = $process; Started = $process.StartTime; IsRole =
            $entry.CommandLine -match '(?i)(?:^|\s)"?--(?:dialog-(agent|guardian|worker|proxy|recover)|shortcut-agent)"?(?:\s|$)' }
    }
}

# A copy built against the shared .NET Desktop runtime cannot start once that
# runtime is gone from the machine: its host asks the user to download .NET and
# waits on that prompt. Reading its runtimeconfig tells beforehand.
function Test-CanStart([string]$path) {
    $config = [System.IO.Path]::ChangeExtension($path, '.runtimeconfig.json')
    if (-not (Test-Path -LiteralPath $config -PathType Leaf)) { return $true }
    try { $options = (Get-Content -LiteralPath $config -Raw | ConvertFrom-Json).runtimeOptions } catch { return $true }
    $frameworks = @(@($options.frameworks) + @($options.framework) | Where-Object { $null -ne $_ -and $_.name })
    $roots = @($env:DOTNET_ROOT, (Join-Path $env:ProgramFiles 'dotnet')) | Where-Object { $_ }
    foreach ($framework in $frameworks) {
        $major = ([string]$framework.version -split '\.')[0]
        $present = $false
        foreach ($root in $roots) {
            $shared = Join-Path $root "shared\$($framework.name)"
            if ((Test-Path -LiteralPath $shared) -and @(Get-ChildItem -LiteralPath $shared -Directory | Where-Object { $_.Name -like "$major.*" }).Count -gt 0) { $present = $true; break }
        }
        if (-not $present) { return $false }
    }
    return $true
}

function Start-InstalledRole([string]$argument, [string]$file = $executable) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $file
    $start.Arguments = $argument
    $start.WorkingDirectory = $destination
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    # An installer invoked from a test shell must still address the installed
    # user's state, and must not inherit a fixture-only integration listener.
    foreach ($name in @($start.EnvironmentVariables.Keys)) {
        if ($name -eq 'ULTRAEXPLORER_STATE_DIR' -or $name -eq 'ULTRAEXPLORER_TEST_WINDOW' -or $name -like 'ULTRAEXPLORER_DIALOG_TEST_*') {
            $start.EnvironmentVariables.Remove($name)
        }
    }
    [System.Diagnostics.Process]::Start($start)
}

# The same identity and mutex as DialogIntegrationStore.Update. Read the
# current document again when restoring, so exclusions changed meanwhile stay.
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value + '|' + $stateDirectory.ToUpperInvariant()
$sha = [System.Security.Cryptography.SHA256]::Create()
try { $instanceKey = [System.BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($identity))).Replace('-', '').Substring(0, 24) }
finally { $sha.Dispose() }
$settingsMutexName = 'Local\UltraExplorer.DialogSettings.' + $instanceKey
function Use-IntegrationPreference([object]$restore = $null) {
    $mutex = New-Object System.Threading.Mutex($false, $settingsMutexName)
    $entered = $false
    try {
        try { $entered = $mutex.WaitOne(3000) } catch [System.Threading.AbandonedMutexException] { $entered = $true }
        if (-not $entered) { throw 'Another window is changing dialog integration. Try the update again.' }
        $settings = if (Test-Path -LiteralPath $settingsPath) {
            if ((Get-Item -LiteralPath $settingsPath).Length -gt 65536) { throw 'Dialog integration settings are too large to update safely.' }
            Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        } else { [pscustomobject]@{ Enabled = $false; ExcludedApplications = @(); LastRecovery = '' } }
        if ($settings -isnot [pscustomobject] -or ($null -ne $settings.Enabled -and $settings.Enabled -isnot [bool])) {
            throw 'Dialog integration settings contain an invalid Enabled preference.'
        }
        if ($null -ne $settings.WinEEnabled -and $settings.WinEEnabled -isnot [bool]) {
            throw 'Dialog integration settings contain an invalid Win+E preference.'
        }
        if ($null -eq $restore) { return [pscustomobject]@{ Enabled = $settings.Enabled -eq $true;
            WinEEnabled = if ($null -eq $settings.WinEEnabled) { $settings.Enabled -eq $true } else { $settings.WinEEnabled -eq $true };
            WinELastError = [string]$settings.WinELastError; LastRecovery = [string]$settings.LastRecovery } }
        $settings | Add-Member -NotePropertyName Enabled -NotePropertyValue ([bool]$restore.Enabled) -Force
        $settings | Add-Member -NotePropertyName WinEEnabled -NotePropertyValue ([bool]$restore.WinEEnabled) -Force
        $settings | Add-Member -NotePropertyName WinELastError -NotePropertyValue ([string]$restore.WinELastError) -Force
        $settings | Add-Member -NotePropertyName LastRecovery -NotePropertyValue ([string]$restore.LastRecovery) -Force
        New-Item -ItemType Directory -Force -Path $stateDirectory | Out-Null
        $temporary = $settingsPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
        try {
            $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes(($settings | ConvertTo-Json -Depth 32))
            $stream = New-Object System.IO.FileStream($temporary, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
            try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
            if (Test-Path -LiteralPath $settingsPath) { [System.IO.File]::Replace($temporary, $settingsPath, [NullString]::Value) }
            else { [System.IO.File]::Move($temporary, $settingsPath) }
        } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
    } finally { if ($entered) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
}

# Build into a staging folder first, so a failed build never leaves a
# half-updated install behind.
Write-Host "Building UltraExplorer (Release)..."
$preference = $null
$recovered = $false
$payloadUpdate = $null
$installed = $false
$oldRunValue = $null
$oldShortcutRunValue = $null
try {
    # Self-contained: the app carries its own .NET, so it starts on a machine
    # whose shared .NET Desktop runtime is missing, removed or out of date.
    dotnet publish $project -c Release -r win-x64 --self-contained true -nologo -v q -o $staging --artifacts-path (Join-Path $staging '.artifacts')
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
    $buildArtifacts = Join-Path $staging '.artifacts'
    if (Test-Path -LiteralPath $buildArtifacts) { Assert-UpdatePath $buildArtifacts $staging; Remove-Item -Recurse -Force -LiteralPath $buildArtifacts }
    Assert-UpdatePath $staging $temporaryRoot
    # Retain the existing conservative link preflight: any installed junction
    # aborts before recovery or replacement, including an unmanaged addition.
    Assert-UpdatePath $destination $programsRoot
    Assert-UpdatePath $backup $programsRoot
    if (-not (Test-Path -LiteralPath (Join-Path $staging 'UltraExplorer.exe') -PathType Leaf)) { throw 'Publish produced no UltraExplorer executable.' }
    New-UltraExplorerPayloadManifest $staging
    $preference = Use-IntegrationPreference
    # Get-ItemPropertyValue throws for an absent value even with
    # SilentlyContinue. Reading the optional property from the key does not.
    $runProperties = Get-ItemProperty -LiteralPath $runKey -ErrorAction SilentlyContinue
    $runProperty = if ($null -ne $runProperties) { $runProperties.PSObject.Properties[$runName] } else { $null }
    $oldRunValue = if ($null -ne $runProperty) { $runProperty.Value } else { $null }
    $shortcutProperty = if ($null -ne $runProperties) { $runProperties.PSObject.Properties['UltraExplorer Win+E'] } else { $null }
    $oldShortcutRunValue = if ($null -ne $shortcutProperty) { $shortcutProperty.Value } else { $null }

    $roles = @(Get-InstalledCopies | Where-Object IsRole)
    # With the mode on, Windows opens every folder through the installed
    # executable even when no background role is running. Pause it all the
    # same, so folders open in Windows Explorer while the files are replaced.
    if ($roles.Count -gt 0 -or ($preference.Enabled -and (Test-Path -LiteralPath $executable -PathType Leaf))) {
        # Recovery uses the shared per-user integration state. A different
        # executable may share that state, so do not pause its listener indirectly.
        $foreignRoles = @(Get-CimInstance Win32_Process -Filter "Name = 'UltraExplorer.exe'" | Where-Object {
            $_.ExecutablePath -and -not [string]::Equals($_.ExecutablePath, $executable, [System.StringComparison]::OrdinalIgnoreCase) -and
            $_.CommandLine -match '(?i)(?:^|\s)"?--(?:dialog-(agent|guardian|worker|proxy|recover)|shortcut-agent)"?(?:\s|$)'
        })
        if ($foreignRoles.Count -gt 0) {
            throw 'Another UltraExplorer copy has background integration running. Finish that copy before updating installed integration; no processes or application files were changed.'
        }
        Write-Host 'Restoring Windows dialogs and stopping installed background integration...'
        $recovered = $true
        # The installed copy runs recovery unless it can no longer start; then
        # the new build, which carries its own runtime, does the same work.
        $recoveryExecutable = if (Test-CanStart $executable) { $executable } else { Join-Path $staging 'UltraExplorer.exe' }
        $recovery = Start-InstalledRole '--dialog-recover' $recoveryExecutable
        try {
            if (-not $recovery.WaitForExit(15000)) {
                # End only the recovery helper started by this invocation;
                # leave every existing guardian/proxy responsible for its originals.
                $recovery.Kill()
                [void]$recovery.WaitForExit(5000)
                throw 'Installed dialog recovery did not finish; no application files were replaced.'
            }
            if ($recovery.ExitCode -ne 0) { throw "Installed dialog recovery failed with exit code $($recovery.ExitCode); no application files were replaced." }
        } finally { $recovery.Dispose() }
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        do {
            $roles = @(Get-InstalledCopies | Where-Object IsRole)
            if ($roles.Count -eq 0) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        foreach ($role in $roles) {
            $process = $role.Process
            if (-not $process.HasExited -and $process.StartTime -eq $role.Started -and
                [string]::Equals($process.Path, $executable, [System.StringComparison]::OrdinalIgnoreCase)) {
                # Recovery has already restored originals. A lingering installed
                # background provider may be ended without discarding canvas state.
                Stop-Process -InputObject $process -Force
                if (-not $process.WaitForExit(5000)) { throw "Installed background process $($process.Id) did not stop." }
            }
        }
    }
    foreach ($copy in @(Get-InstalledCopies | Where-Object { -not $_.IsRole })) {
        $process = $copy.Process
        if ($process.HasExited) { continue }
        Write-Host "Closing the installed UltraExplorer (pid $($process.Id)) so it can be updated..."
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(15000)) {
            if ($process.StartTime -ne $copy.Started -or -not [string]::Equals($process.Path, $executable, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw 'The installed window process changed during its graceful close.'
            }
            Write-Host 'Finishing the installed window through Windows Restart Manager...'
            $closeResult = [UltraExplorerGracefulUpdate]::Close([uint32]$process.Id, $copy.Started.ToUniversalTime().ToFileTimeUtc())
            if ($closeResult -ne 0 -or -not $process.WaitForExit(10000)) {
                throw "UltraExplorer (pid $($process.Id)) did not close gracefully (Windows error $closeResult); no application files were replaced."
            }
        }
    }
    if (@(Get-InstalledCopies).Count -gt 0) { throw 'An installed copy started during the update; no application files were replaced.' }

    # The old manifest plus current staged paths identify owned files. Unknown
    # files, including additions in runtime/locale folders, stay in place.
    # The helper backs up and rolls back individual files, never directories.
    $payloadUpdate = Invoke-UltraExplorerPayloadUpdate -StagingRoot $staging -DestinationRoot $destination -BackupRoot $backup
    $installed = $true
} finally {
    if ($recovered -and $null -ne $preference) {
        Use-IntegrationPreference $preference
    }
    if ($null -ne $preference -and $preference.Enabled -and ($installed -or $recovered)) {
        New-Item -Path $runKey -Force | Out-Null
        $value = if ($installed -or $null -eq $oldRunValue) { '"' + $executable + '" --dialog-agent' } else { $oldRunValue }
        if ($null -ne $value) { New-ItemProperty -LiteralPath $runKey -Name $runName -Value $value -PropertyType String -Force | Out-Null }
    }
    if ($null -ne $preference -and $preference.Enabled -and ($installed -or $recovered) -and (Test-Path -LiteralPath $executable) -and (Test-CanStart $executable)) {
        $resident = Start-InstalledRole '--dialog-agent'
        $resident.Dispose()
    }
    # A failed first upgrade still has the older executable, which does not
    # recognise --shortcut-agent and would open an ordinary window instead.
    if ($null -ne $preference -and $preference.WinEEnabled -and ($installed -or ($recovered -and $null -ne $oldShortcutRunValue)) -and (Test-Path -LiteralPath $executable) -and (Test-CanStart $executable)) {
        $shortcut = Start-InstalledRole '--shortcut-agent'
        $shortcut.Dispose()
    }
    if (Test-Path -LiteralPath $staging) { Assert-UpdatePath $staging $temporaryRoot; Remove-Item -LiteralPath $staging -Recurse -Force }
    if ($installed -and $null -ne $payloadUpdate) { Complete-UltraExplorerPayloadUpdate $payloadUpdate }
}

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
