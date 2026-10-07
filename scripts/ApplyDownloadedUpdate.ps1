# Started only by a person's confirmed Install click. This one-shot helper
# never checks the network, registers startup tasks, elevates, or forces exit.
param([string]$RequestPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'InstallPayload.ps1')
Add-Type -AssemblyName System.IO.Compression

function Get-QuietUpdateFile([string]$Path) {
    if (-not [System.IO.Path]::IsPathRooted($Path)) { throw 'An update file path must be absolute.' }
    $full = [System.IO.Path]::GetFullPath($Path)
    [void](ConvertTo-UltraExplorerPayloadName ([System.IO.Path]::GetFileName($full)))
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw 'The update file path contains a filesystem link.' }
        }
        $parent = Split-Path -Parent $cursor
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
    return $full
}

function Expand-QuietUpdatePackage {
    param([string]$ZipPath, [string]$Sha256, [long]$Size, [string]$StagingRoot)
    if ($Sha256 -notmatch '\A[0-9a-fA-F]{64}\z' -or $Size -le 0 -or $Size -gt 1073741824) { throw 'Invalid update digest or size.' }
    $zip = Get-QuietUpdateFile $ZipPath
    $stage = Get-UltraExplorerPayloadRoot $StagingRoot
    if (Test-Path -LiteralPath $stage) { throw 'Update staging must be a new directory.' }
    $stream = [System.IO.File]::Open($zip, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        if ($stream.Length -ne $Size) { throw 'The update package size changed.' }
        $hash = [System.Security.Cryptography.SHA256]::Create()
        try { $actualHash = [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
        finally { $hash.Dispose() }
        if (-not [string]::Equals($actualHash, $Sha256, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'The update package digest changed.' }
        $stream.Position = 0
        $archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Read, $true)
        try {
            if ($archive.Entries.Count -eq 0 -or $archive.Entries.Count -gt 8192) { throw 'The update package has too many entries.' }
            $names = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
            $files = New-Object 'System.Collections.Generic.List[object]'
            $directories = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
            [long]$total = 0
            foreach ($entry in $archive.Entries) {
                $directory = $entry.FullName.EndsWith('/') -or $entry.FullName.EndsWith('\')
                $name = ConvertTo-UltraExplorerPayloadName $entry.FullName.TrimEnd('/', '\')
                if (-not $names.Add($name)) { throw 'The update package contains duplicate names.' }
                if (($entry.ExternalAttributes -band 0x400) -ne 0 -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) {
                    throw 'The update package contains a filesystem link.'
                }
                if (Test-UltraExplorerUninstallerName $name) { throw 'An update cannot replace the Setup uninstaller.' }
                [void](Get-UltraExplorerPayloadPath $stage $name)
                if ($directory) {
                    if ($entry.Length -ne 0) { throw 'An update directory contains file data.' }
                    [void]$directories.Add($name)
                } else {
                    if ($entry.Length -lt 0 -or $entry.CompressedLength -lt 0 -or $entry.Length -gt 1073741824 -or
                        ($entry.Length -gt 1048576 -and $entry.Length -gt $entry.CompressedLength * 1000)) {
                        throw 'The update package exceeds extraction limits.'
                    }
                    $total += $entry.Length
                    if ($total -gt 2147483648) { throw 'The update package exceeds extraction limits.' }
                    [void]$files.Add([pscustomobject]@{ Name = $name; Entry = $entry })
                    $parent = Split-Path -Parent $name
                    while ($parent) { [void]$directories.Add($parent); $parent = Split-Path -Parent $parent }
                }
            }
            foreach ($file in $files) {
                if ($directories.Contains($file.Name)) { throw 'An update file collides with a directory.' }
            }
            if (-not $names.Contains($UltraExplorerPackageManifest) -or -not $names.Contains('UltraExplorer.exe')) {
                throw 'The update package has no application or ownership manifest.'
            }
            [void][System.IO.Directory]::CreateDirectory($stage)
            foreach ($file in $files) {
                $path = Get-UltraExplorerPayloadPath $stage $file.Name
                [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $path))
                $input = $file.Entry.Open()
                try {
                    $output = [System.IO.File]::Open($path, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
                    try {
                        $buffer = New-Object byte[] 65536
                        [long]$copied = 0
                        while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                            $copied += $read
                            if ($copied -gt $file.Entry.Length) { throw 'An update entry exceeded its declared length.' }
                            $output.Write($buffer, 0, $read)
                        }
                        if ($copied -ne $file.Entry.Length) { throw 'An update entry is truncated.' }
                        $output.Flush($true)
                    } finally { $output.Dispose() }
                } finally { $input.Dispose() }
            }
            $manifest = Get-UltraExplorerPayloadPath $stage $UltraExplorerPackageManifest
            if ((Get-Item -LiteralPath $manifest).Length -gt 4194304) { throw 'The update manifest exceeds its limit.' }
            $manifestNames = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
            foreach ($line in [System.IO.File]::ReadAllLines($manifest)) {
                if (-not $line) { continue }
                $name = ConvertTo-UltraExplorerPayloadName $line
                if (-not $manifestNames.Add($name)) { throw 'The update manifest contains duplicate names.' }
            }
            if (-not $manifestNames.Contains($UltraExplorerPackageManifest) -or $manifestNames.Count -ne $files.Count) { throw 'The update manifest does not match its files.' }
            foreach ($file in $files) {
                if (-not $manifestNames.Contains($file.Name)) { throw 'The update manifest does not match its files.' }
            }
            return $stage
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
}

function Assert-QuietUpdateExecutable([string]$Root, [string]$Version) {
    $path = Get-UltraExplorerPayloadPath $Root 'UltraExplorer.exe'
    $stream = [System.IO.File]::OpenRead($path)
    try {
        $reader = New-Object System.IO.BinaryReader($stream)
        if ($reader.ReadUInt16() -ne 0x5A4D -or $stream.Length -lt 256) { throw 'The update application is not a Windows executable.' }
        $stream.Position = 60
        $header = $reader.ReadUInt32()
        if ($header -gt $stream.Length - 6) { throw 'The update executable header is invalid.' }
        $stream.Position = $header
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) { throw 'The update application is not Windows x64.' }
    } finally { $stream.Dispose() }
    $product = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion
    if (-not $product -or ($product -split '\+', 2)[0] -cne ($Version -split '\+', 2)[0]) { throw 'The update application version differs from the selected release.' }
}

function Undo-QuietPayloadUpdate([object]$Update) {
    # Used only if the new application could not start. The original transaction
    # already rolls back failed copies; its backup is retained until restart.
    foreach ($name in $Update.CopiedFiles) {
        $path = Get-UltraExplorerPayloadPath $Update.DestinationRoot $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
    }
    foreach ($name in $Update.BackupFiles) {
        $saved = Get-UltraExplorerPayloadPath $Update.BackupRoot $name
        $target = Get-UltraExplorerPayloadPath $Update.DestinationRoot $name
        [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $target))
        [System.IO.File]::Move($saved, $target)
    }
    Remove-UltraExplorerPayloadEmptyDirectories $Update.DestinationRoot $Update.DestinationDirectories
    Remove-UltraExplorerPayloadEmptyDirectories $Update.BackupRoot $Update.BackupDirectories
}

function Get-QuietTargetCopies([string]$Executable) {
    foreach ($entry in Get-CimInstance Win32_Process -Filter "Name = 'UltraExplorer.exe'") {
        if (-not [string]::Equals($entry.ExecutablePath, $Executable, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        try { $process = Get-Process -Id $entry.ProcessId -ErrorAction Stop }
        catch [Microsoft.PowerShell.Commands.ProcessCommandException] { continue }
        if ($process.HasExited -or -not [string]::Equals($process.Path, $Executable, [System.StringComparison]::OrdinalIgnoreCase)) { $process.Dispose(); continue }
        if (-not $entry.CommandLine) { $process.Dispose(); throw 'The running application cannot be identified.' }
        [pscustomobject]@{ Process = $process; Started = $process.StartTime; IsRole =
            $entry.CommandLine -match '(?i)(?:^|\s)"?--(?:dialog-(agent|guardian|worker|proxy|recover)|shortcut-agent)"?(?:\s|$)' }
    }
}

function Close-QuietUpdateWindows([object[]]$Windows, [scriptblock]$CloseProcesses) {
    $closedAny = $false
    $errorText = $null
    try {
        $ids = @($Windows | ForEach-Object { [uint32]$_.Process.Id })
        $starts = @($Windows | ForEach-Object { $_.Started.ToUniversalTime().ToFileTimeUtc() })
        $closeResult = & $CloseProcesses ([uint32[]]$ids) ([long[]]$starts)
        foreach ($window in $Windows) { if ($window.Process.HasExited) { $closedAny = $true } }
        if ($closeResult -ne 0) { throw "Application close was declined or timed out (Windows error $closeResult)." }
        foreach ($window in $Windows) {
            if (-not $window.Process.WaitForExit(10000)) { throw 'An application window did not close. The update was cancelled.' }
            $closedAny = $true
        }
    } catch { $errorText = $_.Exception.Message }
    finally { foreach ($window in $Windows) { $window.Process.Dispose() } }
    return [pscustomobject]@{ ClosedAny = $closedAny; Error = $errorText }
}

function Start-QuietUpdateApp([string]$Executable, [string]$StateDirectory, [string]$Role = '') {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.Arguments = $Role
    $start.WorkingDirectory = Split-Path -Parent $Executable
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    # Preserve the actual application's isolated or normal profile. Nothing in
    # this helper redirects a portable copy into a different installation.
    $start.EnvironmentVariables['ULTRAEXPLORER_STATE_DIR'] = $StateDirectory
    if ([string]::Equals($StateDirectory, (Join-Path $env:LOCALAPPDATA 'UltraExplorer'), [System.StringComparison]::OrdinalIgnoreCase)) {
        $start.EnvironmentVariables.Remove('ULTRAEXPLORER_STATE_DIR')
    }
    return [System.Diagnostics.Process]::Start($start)
}

function Use-QuietIntegrationPreference([string]$StateDirectory, [string]$Sid, [object]$Restore = $null) {
    $settingsPath = Get-UltraExplorerPayloadPath (Get-UltraExplorerPayloadRoot $StateDirectory) 'dialog-integration.json'
    $hash = [System.Security.Cryptography.SHA256]::Create()
    try { $key = [BitConverter]::ToString($hash.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Sid + '|' + $StateDirectory.ToUpperInvariant()))).Replace('-', '').Substring(0, 24) }
    finally { $hash.Dispose() }
    $mutex = New-Object System.Threading.Mutex($false, ('Local\UltraExplorer.DialogSettings.' + $key))
    $entered = $false
    try {
        try { $entered = $mutex.WaitOne(3000) } catch [System.Threading.AbandonedMutexException] { $entered = $true }
        if (-not $entered) { throw 'Dialog settings are busy. Retry the update later.' }
        $exists = Test-Path -LiteralPath $settingsPath -PathType Leaf
        if ($exists -and (Get-Item -LiteralPath $settingsPath).Length -gt 65536) { throw 'Dialog settings exceed their safe limit.' }
        $settings = if ($exists) { Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { [pscustomobject]@{} }
        if ($settings -isnot [pscustomobject] -or ($null -ne $settings.Enabled -and $settings.Enabled -isnot [bool]) -or
            ($null -ne $settings.WinEEnabled -and $settings.WinEEnabled -isnot [bool])) { throw 'Dialog preferences are invalid.' }
        $fields = @('Enabled', 'WinEEnabled', 'WinELastError', 'LastRecovery')
        if ($null -eq $Restore) {
            $values = @{}
            foreach ($field in $fields) {
                $property = $settings.PSObject.Properties[$field]
                $values[$field] = [pscustomobject]@{ Present = $null -ne $property; Value = if ($property) { $property.Value } else { $null } }
            }
            return [pscustomobject]@{ Values = $values; Enabled = $settings.Enabled -eq $true;
                WinEEnabled = if ($null -eq $settings.WinEEnabled) { $settings.Enabled -eq $true } else { $settings.WinEEnabled -eq $true } }
        }
        foreach ($field in $fields) {
            if ($Restore.Values[$field].Present) { $settings | Add-Member -NotePropertyName $field -NotePropertyValue $Restore.Values[$field].Value -Force }
            else { $settings.PSObject.Properties.Remove($field) }
        }
        [void][System.IO.Directory]::CreateDirectory($StateDirectory)
        $temporary = $settingsPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
        try {
            [System.IO.File]::WriteAllText($temporary, ($settings | ConvertTo-Json -Depth 32), (New-Object System.Text.UTF8Encoding($false)))
            if (Test-Path -LiteralPath $settingsPath) { [System.IO.File]::Replace($temporary, $settingsPath, [NullString]::Value) }
            else { [System.IO.File]::Move($temporary, $settingsPath) }
        } finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force } }
    } finally { if ($entered) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
}

function Write-QuietUpdateResult([string]$OperationRoot, [object]$Result) {
    $path = Get-UltraExplorerPayloadPath $OperationRoot 'result.json'
    $temporary = Get-UltraExplorerPayloadPath $OperationRoot ('result-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [System.IO.File]::WriteAllText($temporary, ($Result | ConvertTo-Json -Depth 8), (New-Object System.Text.UTF8Encoding($false)))
        [void](Get-UltraExplorerPayloadPath $OperationRoot 'result.json')
        if (Test-Path -LiteralPath $path) { [System.IO.File]::Replace($temporary, $path, [NullString]::Value) }
        else { [System.IO.File]::Move($temporary, $path) }
    } finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force } }
}

function Wait-QuietRecovery([object]$Recovery, [string]$OperationRoot, [string]$Version) {
    if (-not $Recovery.WaitForExit(15000)) {
        # A timeout cancels replacement immediately. A still-running recovery
        # can change integration later, so restoration must wait for its exit.
        # This cleanup belongs to the explicit click and retains its mutex;
        # no kill, new timer, startup role, or independent background service.
        Write-QuietUpdateResult $OperationRoot ([pscustomobject]@{ Status = 'Failed'; Version = $Version;
            Error = 'Integration recovery is still finishing. The update was cancelled; the old application will reopen when recovery finishes.';
            WaitingRecovery = $true; CompletedUtc = [DateTime]::UtcNow.ToString('o') })
        $Recovery.WaitForExit()
        throw 'Integration recovery took too long. The update was cancelled and no application files were replaced.'
    }
    if ($Recovery.ExitCode -ne 0) { throw 'Integration recovery failed. No application files were replaced.' }
}

function Invoke-QuietDownloadedUpdate([string]$Path) {
    $requestPath = Get-QuietUpdateFile $Path
    if ((Get-Item -LiteralPath $requestPath).Length -gt 16384) { throw 'The update request is too large.' }
    $request = Get-Content -LiteralPath $requestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($request.Format -ne 1 -or $request.ExplicitInstall -isnot [bool] -or $request.ExplicitInstall -ne $true -or $request.Version.Length -gt 160 -or
        $request.Version -notmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z') { throw 'The update request is invalid.' }
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if ($request.UserSid -cne $sid) { throw 'The update belongs to another Windows user.' }
    $operationRoot = Get-UltraExplorerPayloadRoot (Split-Path -Parent $requestPath)
    $resultPath = Get-UltraExplorerPayloadPath $operationRoot 'result.json'
    if (-not [string]::Equals($request.ResultPath, $resultPath, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'The update result is outside this operation.' }
    $destination = Get-UltraExplorerPayloadRoot $request.DestinationRoot
    $stateDirectory = Get-UltraExplorerPayloadRoot $request.StateDirectory
    $executable = Get-UltraExplorerPayloadPath $destination 'UltraExplorer.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'The current application is missing.' }
    if ($operationRoot.StartsWith($destination + '\', [System.StringComparison]::OrdinalIgnoreCase) -or
        $destination.StartsWith($operationRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Update state cannot overlap the application payload.' }
    $staging = Join-Path $operationRoot 'staging'
    $backup = Join-Path (Split-Path -Parent $destination) ('UltraExplorer-update-backup-' + [guid]::NewGuid().ToString('N'))
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $key = [BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($destination.ToUpperInvariant()))).Replace('-', '').Substring(0, 24) }
    finally { $sha.Dispose() }
    $mutex = New-Object System.Threading.Mutex($false, ('Local\UltraExplorer.ConfirmedUpdate.' + $key))
    $entered = $false
    $recovered = $false
    $closedAny = $false
    $installed = $false
    $restartConfirmed = $false
    $preference = $null
    $transaction = $null
    $failure = $null
    try {
        try { $entered = $mutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $entered = $true }
        if (-not $entered) { throw 'Another confirmed update is already running.' }
        [void](Expand-QuietUpdatePackage $request.ZipPath $request.Sha256 $request.Size $staging)
        Assert-QuietUpdateExecutable $staging $request.Version
        # Complete conservative path/manifest preflight BEFORE any window closes.
        [void]@(Get-UltraExplorerPayloadFiles $destination)
        [void]@(Read-UltraExplorerPayloadManifest $destination -AllowMissing -Previous)
        foreach ($name in @(Read-UltraExplorerPayloadManifest $staging)) {
            $target = Get-UltraExplorerPayloadPath $destination $name
            if (Test-Path -LiteralPath $target -PathType Container) { throw 'An update file collides with an installed directory.' }
        }
        $probe = Get-UltraExplorerPayloadPath $destination ('update-write-' + [guid]::NewGuid().ToString('N') + '.tmp')
        $probeStream = [System.IO.File]::Open($probe, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        $probeStream.Dispose()
        Remove-Item -LiteralPath $probe -Force
        $probe = Get-UltraExplorerPayloadRoot ($backup + '-permission-check')
        if (Test-Path -LiteralPath $probe) { throw 'The new backup permission probe already exists.' }
        [void][System.IO.Directory]::CreateDirectory($probe)
        [void](Get-UltraExplorerPayloadRoot $probe)
        [System.IO.Directory]::Delete($probe, $false)
        $preference = Use-QuietIntegrationPreference $stateDirectory $sid
        $foreignRoles = @(Get-CimInstance Win32_Process -Filter "Name = 'UltraExplorer.exe'" | Where-Object {
            $_.ExecutablePath -and -not [string]::Equals($_.ExecutablePath, $executable, [System.StringComparison]::OrdinalIgnoreCase) -and
            $_.CommandLine -match '(?i)(?:^|\s)"?--(?:dialog-(agent|guardian|worker|proxy|recover)|shortcut-agent)"?(?:\s|$)'
        })
        if (($preference.Enabled -or $preference.WinEEnabled) -and $foreignRoles.Count -gt 0) { throw 'Another application copy has integration running. Close it before updating.' }
        if (-not ('UltraExplorerQuietGracefulUpdate' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class UltraExplorerQuietGracefulUpdate
{
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct Identity { public uint Id; public FileTime Started; }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint session, uint flags, [Out] StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint session, uint files, string[] fileNames, uint processes, Identity[] identities, uint services, string[] serviceNames);
    [DllImport("rstrtmgr.dll")] private static extern int RmShutdown(uint session, uint flags, IntPtr callback);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint session);
    public static int Close(uint[] processes, long[] started)
    {
        if (processes.Length == 0) return 0;
        uint session;
        var result = RmStartSession(out session, 0, new StringBuilder(33));
        if (result != 0) return result;
        try
        {
            var identities = new Identity[processes.Length];
            for (var i = 0; i < identities.Length; i++) identities[i] = new Identity { Id = processes[i], Started = new FileTime { Low = (uint)started[i], High = (uint)((ulong)started[i] >> 32) } };
            result = RmRegisterResources(session, 0, null, (uint)identities.Length, identities, 0, null);
            // Flags ZERO: a veto or timeout aborts; no force flag or kill fallback.
            return result == 0 ? RmShutdown(session, 0, IntPtr.Zero) : result;
        }
        finally { RmEndSession(session); }
    }
}
'@
        }
        $windows = @(Get-QuietTargetCopies $executable | Where-Object { -not $_.IsRole })
        $close = Close-QuietUpdateWindows $windows { param($ids, $starts) [UltraExplorerQuietGracefulUpdate]::Close($ids, $starts) }
        $closedAny = $close.ClosedAny
        if ($close.Error) { throw $close.Error }
        $roles = @(Get-QuietTargetCopies $executable | Where-Object IsRole)
        if ($roles.Count -gt 0 -or $preference.Enabled -or $preference.WinEEnabled) {
            $recovered = $true
            $recovery = Start-QuietUpdateApp $executable $stateDirectory '--dialog-recover'
            try { Wait-QuietRecovery $recovery $operationRoot $request.Version }
            finally { $recovery.Dispose() }
            $deadline = [DateTime]::UtcNow.AddSeconds(15)
            do {
                foreach ($role in $roles) { $role.Process.Dispose() }
                $roles = @(Get-QuietTargetCopies $executable | Where-Object IsRole)
                if ($roles.Count -eq 0) { break }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $deadline)
            if ($roles.Count -gt 0) { throw 'Integration did not exit gracefully. The update was cancelled.' }
        }
        foreach ($role in $roles) { $role.Process.Dispose() }
        $remaining = @(Get-QuietTargetCopies $executable)
        try { if ($remaining.Count -gt 0) { throw 'An application copy started during the update. No files were replaced.' } }
        finally { foreach ($copy in $remaining) { $copy.Process.Dispose() } }
        $transaction = Invoke-UltraExplorerPayloadUpdate -StagingRoot $staging -DestinationRoot $destination -BackupRoot $backup
        $installed = $true
        if ($recovered) { Use-QuietIntegrationPreference $stateDirectory $sid $preference; $recovered = $false }
        $restart = Start-QuietUpdateApp $executable $stateDirectory
        try { if ($restart.WaitForExit(3000)) { throw 'The updated application could not start.' } }
        finally { $restart.Dispose() }
        $restartConfirmed = $true
        # Cleanup failure retains recovery files; it does not undo a running app.
        try { Complete-UltraExplorerPayloadUpdate $transaction } catch { Write-Warning $_.Exception.Message }
        Write-QuietUpdateResult $operationRoot ([pscustomobject]@{ Status = 'Installed'; Version = $request.Version; CompletedUtc = [DateTime]::UtcNow.ToString('o') })
    } catch {
        $failure = $_.Exception.Message
        if ($installed -and $transaction -and -not $restartConfirmed) {
            try { Undo-QuietPayloadUpdate $transaction; $installed = $false }
            catch { $failure += ' Rollback needs attention; the old files remain in the update backup. ' + $_.Exception.Message }
        }
        if ($recovered -and $preference) {
            try { Use-QuietIntegrationPreference $stateDirectory $sid $preference; $recovered = $false }
            catch { $failure += ' Integration preferences could not be restored. ' + $_.Exception.Message }
        }
        if ($closedAny -and -not $installed) {
            $copies = @(Get-QuietTargetCopies $executable | Where-Object { -not $_.IsRole })
            try {
                if ($copies.Count -eq 0) { $restart = Start-QuietUpdateApp $executable $stateDirectory; $restart.Dispose() }
            } catch { $failure += ' The previous application could not restart. ' + $_.Exception.Message }
            finally { foreach ($copy in $copies) { $copy.Process.Dispose() } }
        }
        Write-QuietUpdateResult $operationRoot ([pscustomobject]@{ Status = 'Failed'; Version = $request.Version; Error = $failure; CompletedUtc = [DateTime]::UtcNow.ToString('o') })
    } finally {
        # Delete only this GUID operation's enumerated staging files. No broad
        # recursive delete can follow an unexpected link or remove user extras.
        if (Test-Path -LiteralPath $staging -PathType Container) {
            try {
                $files = @(Get-UltraExplorerPayloadFiles $staging)
                foreach ($name in $files) { Remove-Item -LiteralPath (Get-UltraExplorerPayloadPath $staging $name) -Force }
                $directories = @(Get-ChildItem -LiteralPath $staging -Directory -Recurse -Force | ForEach-Object FullName) + @($staging)
                Remove-UltraExplorerPayloadEmptyDirectories $staging $directories
            } catch { Write-Warning 'The staging files were retained because cleanup could not safely finish.' }
        }
        if ($entered) { $mutex.ReleaseMutex() }; $mutex.Dispose()
    }
}

# Dot-sourcing defines only pure helpers for disposable fixture checks.
if ($RequestPath) { Invoke-QuietDownloadedUpdate $RequestPath }
