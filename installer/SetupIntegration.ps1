# Setup runs this entire operation as its original, non-elevated user.
# Dot-sourcing exposes file-only helpers for disposable regression fixtures.
param([ValidateSet('Snapshot', 'Restore')][string]$Action, [string]$Executable,
    [string]$OperationId, [bool]$Installed = $false)
$ErrorActionPreference = 'Stop'

function Get-SetupIntegrationPath([string]$Root, [string]$Name) {
    if (-not [IO.Path]::IsPathRooted($Root) -or $Name -notmatch '\A(?:dialog-integration\.json|setup-integration-[0-9a-f]{32}\.json)\z') { throw 'Invalid integration state path.' }
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    for ($cursor = $rootPath; $cursor; $cursor = Split-Path -Parent $cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Integration state contains a filesystem link.' }
    }
    $path = Join-Path $rootPath $Name
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Integration state contains a filesystem link.' }
    return $path
}

function Read-SetupIntegrationJson([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    if ((Get-Item -LiteralPath $Path).Length -gt 65536) { throw 'Integration state is too large.' }
    $text = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    if (-not $text.TrimStart().StartsWith('{')) { throw 'Invalid integration state.' }
    $value = $text | ConvertFrom-Json
    if ($value -isnot [pscustomobject]) { throw 'Invalid integration state.' }
    return $value
}

function Write-SetupIntegrationJson([string]$Path, [object]$Value, [bool]$New = $false) {
    $temporary = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 32), (New-Object Text.UTF8Encoding($false)))
        if ($New -or -not (Test-Path -LiteralPath $Path)) { [IO.File]::Move($temporary, $Path) }
        else { [IO.File]::Replace($temporary, $Path, [NullString]::Value) }
    } finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force } }
}

function Use-SetupIntegrationLock([string]$Root, [string]$Sid, [scriptblock]$Body) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $key = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($Sid + '|' + $Root.ToUpperInvariant()))).Replace('-', '').Substring(0, 24) }
    finally { $hash.Dispose() }
    $mutex = New-Object Threading.Mutex($false, ('Local\UltraExplorer.DialogSettings.' + $key))
    $entered = $false
    try {
        try { $entered = $mutex.WaitOne(3000) } catch [Threading.AbandonedMutexException] { $entered = $true }
        if (-not $entered) { throw 'Integration settings are busy. Retry Setup later.' }
        & $Body
    } finally { if ($entered) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
}

function New-SetupIntegrationSnapshot([object]$Settings, [string]$Sid, [string]$App, [object]$Run) {
    if ($null -eq $Settings) { $Settings = [pscustomobject]@{} }
    foreach ($field in @('Enabled', 'WinEEnabled')) {
        if ($null -ne $Settings.$field -and $Settings.$field -isnot [bool]) { throw 'Invalid integration preference.' }
    }
    $values = [ordered]@{}
    foreach ($field in @('Enabled', 'WinEEnabled', 'WinELastError', 'LastRecovery')) {
        $property = $Settings.PSObject.Properties[$field]
        $values[$field] = [pscustomobject]@{ Present = $null -ne $property; Value = if ($property) { $property.Value } else { $null } }
    }
    return [pscustomobject]@{ Format = 1; Sid = $Sid; Executable = $App; Values = $values;
        Enabled = $Settings.Enabled -eq $true; WinEEnabled = if ($null -eq $Settings.WinEEnabled) { $Settings.Enabled -eq $true } else { $Settings.WinEEnabled -eq $true };
        DialogRun = if ($Run) { [string]$Run.'UltraExplorer dialogs' } else { $null };
        HadShortcutRun = $null -ne $Run -and $null -ne $Run.PSObject.Properties['UltraExplorer Win+E'] }
}

function Merge-SetupIntegrationSnapshot([object]$Settings, [object]$Snapshot, [string]$Sid, [string]$App) {
    if ($Snapshot.Format -ne 1 -or $Snapshot.Sid -cne $Sid -or -not [string]::Equals($Snapshot.Executable, $App, [StringComparison]::OrdinalIgnoreCase)) { throw 'Integration snapshot belongs to another operation owner or application.' }
    if ($null -eq $Settings) { $Settings = [pscustomobject]@{} }
    foreach ($field in @('Enabled', 'WinEEnabled', 'WinELastError', 'LastRecovery')) {
        $saved = $Snapshot.Values.$field
        if ($null -eq $saved -or $saved.Present -isnot [bool]) { throw 'Invalid integration snapshot fields.' }
        if ($saved.Present) { $Settings | Add-Member -NotePropertyName $field -NotePropertyValue $saved.Value -Force }
        else { $Settings.PSObject.Properties.Remove($field) }
    }
    return $Settings
}

function Start-SetupIntegrationRole([string]$App, [string]$Role) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $App; $start.Arguments = $Role; $start.WorkingDirectory = Split-Path -Parent $App
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($name in @($start.EnvironmentVariables.Keys)) {
        if ($name -eq 'ULTRAEXPLORER_STATE_DIR' -or $name -eq 'ULTRAEXPLORER_TEST_WINDOW' -or $name -like 'ULTRAEXPLORER_DIALOG_TEST_*') { $start.EnvironmentVariables.Remove($name) }
    }
    return [Diagnostics.Process]::Start($start)
}

function Get-SetupIntegrationRoles([string]$App, [string]$Sid) {
    foreach ($entry in Get-CimInstance Win32_Process -Filter "Name = 'UltraExplorer.exe'" -OperationTimeoutSec 5) {
        if (-not [string]::Equals($entry.ExecutablePath, $App, [StringComparison]::OrdinalIgnoreCase) -or $entry.CommandLine -notmatch '(?i)(?:^|\s)"?--(?:dialog-(agent|guardian|worker|proxy|recover)|shortcut-agent)"?(?:\s|$)') { continue }
        if ((Invoke-CimMethod -InputObject $entry -MethodName GetOwnerSid -OperationTimeoutSec 5).Sid -cne $Sid) { continue }
        try { $process = Get-Process -Id $entry.ProcessId -ErrorAction Stop } catch [Microsoft.PowerShell.Commands.ProcessCommandException] { continue }
        if ($process.HasExited -or -not [string]::Equals($process.Path, $App, [StringComparison]::OrdinalIgnoreCase)) { $process.Dispose(); continue }
        [pscustomobject]@{ Process = $process; Started = $process.StartTime }
    }
}

function Stop-SetupIntegrationRoles([string]$App, [string]$Sid) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $roles = @(Get-SetupIntegrationRoles $App $Sid)
        try {
            if ($roles.Count -eq 0) { return }
            if ([DateTime]::UtcNow -ge $deadline) {
                foreach ($role in $roles) {
                    $process = $role.Process
                    if (-not $process.HasExited -and $process.StartTime -eq $role.Started -and [string]::Equals($process.Path, $App, [StringComparison]::OrdinalIgnoreCase)) {
                        $process.Kill()
                        if (-not $process.WaitForExit(5000)) { throw 'An installed background process did not stop.' }
                    }
                }
                return
            }
        } finally { foreach ($role in $roles) { $role.Process.Dispose() } }
        Start-Sleep -Milliseconds 100
    } while ($true)
}

function Get-SetupIntegrationContext {
    [pscustomobject]@{ Root = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'UltraExplorer'; Sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value }
}

function Read-SetupIntegrationRun {
    Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue
}

function Restore-SetupIntegrationRun([string]$Command) {
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    [void](New-Item -Path $key -Force)
    [void](New-ItemProperty -LiteralPath $key -Name 'UltraExplorer dialogs' -Value $Command -PropertyType String -Force)
}

function Invoke-SetupIntegration([string]$Mode, [string]$App, [string]$Id, [bool]$NewInstalled) {
    if ($Id -notmatch '\A[0-9a-f]{32}\z' -or -not [IO.Path]::IsPathRooted($App) -or [IO.Path]::GetFileName($App) -cne 'UltraExplorer.exe') { throw 'Invalid Setup integration operation.' }
    $appPath = [IO.Path]::GetFullPath($App)
    $context = Get-SetupIntegrationContext
    $root = $context.Root
    $sid = $context.Sid
    $record = Get-SetupIntegrationPath $root ('setup-integration-' + $Id + '.json')
    $settingsPath = Get-SetupIntegrationPath $root 'dialog-integration.json'
    if ($Mode -eq 'Snapshot') {
        $snapshot = Use-SetupIntegrationLock $root $sid {
            $settings = Read-SetupIntegrationJson $settingsPath
            $run = Read-SetupIntegrationRun
            $captured = New-SetupIntegrationSnapshot $settings $sid $appPath $run
            [void][IO.Directory]::CreateDirectory($root)
            Write-SetupIntegrationJson $record $captured $true
            return $captured
        }
        $roles = @(Get-SetupIntegrationRoles $appPath $sid)
        try { $needed = $snapshot.Enabled -or $snapshot.WinEEnabled -or $roles.Count -gt 0 }
        finally { foreach ($role in $roles) { $role.Process.Dispose() } }
        if ($needed) {
            $recovery = Start-SetupIntegrationRole $appPath '--dialog-recover'
            try {
                if (-not $recovery.WaitForExit(15000)) { $recovery.Kill(); [void]$recovery.WaitForExit(5000); throw 'Dialog recovery timed out; no application files were replaced.' }
                if ($recovery.ExitCode -ne 0) { throw 'Dialog recovery failed; no application files were replaced.' }
            } finally { $recovery.Dispose() }
            Stop-SetupIntegrationRoles $appPath $sid
        }
        return
    }
    $snapshot = Use-SetupIntegrationLock $root $sid {
        $saved = Read-SetupIntegrationJson $record
        if ($null -eq $saved) { return $null }
        $settings = Merge-SetupIntegrationSnapshot (Read-SetupIntegrationJson $settingsPath) $saved $sid $appPath
        Write-SetupIntegrationJson $settingsPath $settings
        return $saved
    }
    if ($null -eq $snapshot) { return }
    if ($snapshot.Enabled) {
        $command = if ($NewInstalled -or -not $snapshot.DialogRun) { '"' + $appPath + '" --dialog-agent' } else { $snapshot.DialogRun }
        Restore-SetupIntegrationRun $command
        if (Test-Path -LiteralPath $appPath -PathType Leaf) { $agent = Start-SetupIntegrationRole $appPath '--dialog-agent'; $agent.Dispose() }
    }
    if ($snapshot.WinEEnabled -and ($NewInstalled -or $snapshot.HadShortcutRun) -and (Test-Path -LiteralPath $appPath -PathType Leaf)) { $agent = Start-SetupIntegrationRole $appPath '--shortcut-agent'; $agent.Dispose() }
    Remove-Item -LiteralPath $record -Force
}

if ($Action) {
    try { Invoke-SetupIntegration $Action $Executable $OperationId $Installed; exit 0 }
    catch { Write-Error $_ -ErrorAction Continue; exit 1 }
}
