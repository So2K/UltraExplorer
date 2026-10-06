# Runs only the pure file transaction in a new owned temporary tree. It never
# invokes install.ps1, builds/runs the app, or touches user state/processes/UI.
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\InstallPayload.ps1')
$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('UltraExplorer-payload-fixture-' + [guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($fixtureRoot)
$passed = 0
$total = 0

function Check-Payload([string]$Name, [bool]$Condition) {
    $script:total++
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:passed++
    Write-Host "PASS: $Name"
}

function Write-FixtureFile([string]$Root, [string]$Name, [string]$Text) {
    $path = Join-Path $Root $Name
    [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $path))
    [System.IO.File]::WriteAllText($path, $Text)
}

function Read-FixtureFile([string]$Root, [string]$Name) {
    return [System.IO.File]::ReadAllText((Join-Path $Root $Name))
}

function New-FixtureCase([string]$Name) {
    $root = Join-Path $fixtureRoot $Name
    $stage = Join-Path $root 'stage'
    $install = Join-Path $root 'install'
    [void][System.IO.Directory]::CreateDirectory($stage)
    [void][System.IO.Directory]::CreateDirectory($install)
    return [pscustomobject]@{ Root = $root; Stage = $stage; Install = $install; Backup = (Join-Path $root 'backup') }
}

function Test-FixtureRejected([object]$Case) {
    try {
        $unexpected = Invoke-UltraExplorerPayloadUpdate -StagingRoot $Case.Stage -DestinationRoot $Case.Install -BackupRoot $Case.Backup
        Complete-UltraExplorerPayloadUpdate $unexpected
        return $false
    } catch { return $true }
}

try {
    $case = New-FixtureCase 'managed'
    Write-FixtureFile $case.Install 'UltraExplorer.exe' 'old executable'
    Write-FixtureFile $case.Install 'ru\obsolete.dll' 'old locale'
    Write-FixtureFile $case.Install 'ru\my-notes.txt' 'nested user notes'
    Write-FixtureFile $case.Install 'plugins\nested\mine.dll' 'user plugin'
    Write-FixtureFile $case.Install 'unins000.exe' 'Setup uninstaller'
    Write-FixtureFile $case.Install $UltraExplorerPackageManifest "UltraExplorer.exe`nru\obsolete.dll`nunins000.exe`n$UltraExplorerPackageManifest`n"
    Write-FixtureFile $case.Stage 'UltraExplorer.exe' 'new executable'
    Write-FixtureFile $case.Stage 'ru\current.dll' 'new locale'
    New-UltraExplorerPayloadManifest $case.Stage
    $update = Invoke-UltraExplorerPayloadUpdate -StagingRoot $case.Stage -DestinationRoot $case.Install -BackupRoot $case.Backup
    Check-Payload 'current package collision is replaced' ((Read-FixtureFile $case.Install 'UltraExplorer.exe') -eq 'new executable')
    Check-Payload 'new nested package file is installed' ((Read-FixtureFile $case.Install 'ru\current.dll') -eq 'new locale')
    Check-Payload 'user addition in managed locale directory survives' ((Read-FixtureFile $case.Install 'ru\my-notes.txt') -eq 'nested user notes')
    Check-Payload 'deep unmanaged directory survives' ((Read-FixtureFile $case.Install 'plugins\nested\mine.dll') -eq 'user plugin')
    Check-Payload 'uninstaller survives even if old manifest claims it' ((Read-FixtureFile $case.Install 'unins000.exe') -eq 'Setup uninstaller')
    Check-Payload 'obsolete managed file is removed from installation' (-not (Test-Path -LiteralPath (Join-Path $case.Install 'ru\obsolete.dll')))
    Check-Payload 'obsolete managed file remains recoverable until completion' ((Read-FixtureFile $case.Backup 'ru\obsolete.dll') -eq 'old locale')
    Check-Payload 'new manifest lists itself and only staged files' ((@(Read-UltraExplorerPayloadManifest $case.Install).Count -eq 3) -and ((Read-FixtureFile $case.Install $UltraExplorerPackageManifest) -notmatch 'my-notes|obsolete|unins'))
    Write-FixtureFile $case.Backup 'keep-user-file.txt' 'unexpected backup addition'
    Complete-UltraExplorerPayloadUpdate $update
    Check-Payload 'completion deletes only known backup files' ((Read-FixtureFile $case.Backup 'keep-user-file.txt') -eq 'unexpected backup addition')
    Check-Payload 'completion removes known obsolete backup' (-not (Test-Path -LiteralPath (Join-Path $case.Backup 'ru\obsolete.dll')))
    Remove-Item -LiteralPath (Join-Path $case.Backup 'keep-user-file.txt')
    Complete-UltraExplorerPayloadUpdate $update
    Check-Payload 'empty owned backup directories are removed' (-not (Test-Path -LiteralPath $case.Backup))

    $case = New-FixtureCase 'migration'
    Write-FixtureFile $case.Install 'UltraExplorer.exe' 'legacy executable'
    Write-FixtureFile $case.Install 'unknown-old-runtime.dll' 'unknown legacy file'
    Write-FixtureFile $case.Install 'ru\extra.txt' 'legacy nested user file'
    Write-FixtureFile $case.Install 'unins001.dat' 'Setup state'
    Write-FixtureFile $case.Stage 'UltraExplorer.exe' 'migrated executable'
    New-UltraExplorerPayloadManifest $case.Stage
    $update = Invoke-UltraExplorerPayloadUpdate -StagingRoot $case.Stage -DestinationRoot $case.Install -BackupRoot $case.Backup
    Complete-UltraExplorerPayloadUpdate $update
    Check-Payload 'installation without manifest overwrites current collision' ((Read-FixtureFile $case.Install 'UltraExplorer.exe') -eq 'migrated executable')
    Check-Payload 'installation without manifest preserves unknown legacy files' ((Read-FixtureFile $case.Install 'unknown-old-runtime.dll') -eq 'unknown legacy file')
    Check-Payload 'installation without manifest preserves nested extras and Setup state' (((Read-FixtureFile $case.Install 'ru\extra.txt') -eq 'legacy nested user file') -and ((Read-FixtureFile $case.Install 'unins001.dat') -eq 'Setup state'))
    Check-Payload 'migration establishes current ownership manifest' (@(Read-UltraExplorerPayloadManifest $case.Install).Count -eq 2)

    $case = New-FixtureCase 'rollback'
    Write-FixtureFile $case.Install 'a.dll' 'old a'
    Write-FixtureFile $case.Install 'b.dll' 'old b'
    Write-FixtureFile $case.Install 'nested\obsolete.dll' 'old obsolete'
    Write-FixtureFile $case.Install 'nested\extra.txt' 'original user extra'
    $previousManifest = "a.dll`nb.dll`nnested\obsolete.dll`n$UltraExplorerPackageManifest`n"
    Write-FixtureFile $case.Install $UltraExplorerPackageManifest $previousManifest
    Write-FixtureFile $case.Stage 'a.dll' 'new a'
    Write-FixtureFile $case.Stage 'b.dll' 'new b'
    Write-FixtureFile $case.Stage 'aa-new\new.dll' 'new only'
    New-UltraExplorerPayloadManifest $case.Stage
    $failed = $false
    try {
        $unused = Invoke-UltraExplorerPayloadUpdate -StagingRoot $case.Stage -DestinationRoot $case.Install -BackupRoot $case.Backup -BeforeCopyFile {
            param($name)
            if ($name -eq 'b.dll') {
                Write-FixtureFile $case.Install 'aa-new\during-update.txt' 'concurrent user extra'
                throw 'Owned fixture copy failure after a.dll'
            }
        }
    } catch { $failed = $_.Exception.Message -match 'Owned fixture copy failure' }
    Check-Payload 'copy failure propagates after partial payload copy' $failed
    Check-Payload 'rollback restores overwritten collisions' (((Read-FixtureFile $case.Install 'a.dll') -eq 'old a') -and ((Read-FixtureFile $case.Install 'b.dll') -eq 'old b'))
    Check-Payload 'rollback restores obsolete managed files' ((Read-FixtureFile $case.Install 'nested\obsolete.dll') -eq 'old obsolete')
    Check-Payload 'rollback restores prior manifest byte content' ((Read-FixtureFile $case.Install $UltraExplorerPackageManifest) -ceq $previousManifest)
    Check-Payload 'rollback preserves original nested unmanaged files' ((Read-FixtureFile $case.Install 'nested\extra.txt') -eq 'original user extra')
    Check-Payload 'rollback preserves an unmanaged addition in a created package directory' ((Read-FixtureFile $case.Install 'aa-new\during-update.txt') -eq 'concurrent user extra')
    Check-Payload 'rollback removes a genuinely copied new package file' (-not (Test-Path -LiteralPath (Join-Path $case.Install 'aa-new\new.dll')))
    Check-Payload 'successful rollback removes empty transaction backup' (-not (Test-Path -LiteralPath $case.Backup))

    $case = New-FixtureCase 'backup-failure'
    Write-FixtureFile $case.Install 'a.dll' 'old a'
    Write-FixtureFile $case.Install 'b.dll' 'old b'
    Write-FixtureFile $case.Install 'nested\extra.txt' 'backup failure user extra'
    Write-FixtureFile $case.Stage 'a.dll' 'new a'
    Write-FixtureFile $case.Stage 'b.dll' 'new b'
    New-UltraExplorerPayloadManifest $case.Stage
    $lockedFile = [System.IO.File]::Open((Join-Path $case.Install 'b.dll'), [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
    try { $rejected = Test-FixtureRejected $case }
    finally { $lockedFile.Dispose() }
    Check-Payload 'a backup move failure aborts before copying' $rejected
    Check-Payload 'backup move failure restores files already moved' (((Read-FixtureFile $case.Install 'a.dll') -eq 'old a') -and ((Read-FixtureFile $case.Install 'b.dll') -eq 'old b'))
    Check-Payload 'backup move failure preserves nested unmanaged extras' ((Read-FixtureFile $case.Install 'nested\extra.txt') -eq 'backup failure user extra')
    Check-Payload 'backup move failure leaves no transaction backup' (-not (Test-Path -LiteralPath $case.Backup))

    $case = New-FixtureCase 'invalid-manifest'
    Write-FixtureFile $case.Install 'a.dll' 'unchanged'
    Write-FixtureFile $case.Stage 'a.dll' 'replacement'
    New-UltraExplorerPayloadManifest $case.Stage
    foreach ($unsafeName in @('..\outside.txt', 'nested\..\outside.txt', 'C:\outside.txt', '\\server\file', 'a.dll:stream', '*.dll', 'bad.\file', '.\a.dll', 'CON.txt')) {
        Write-FixtureFile $case.Install $UltraExplorerPackageManifest ($unsafeName + "`n")
        Check-Payload "unsafe old manifest name is rejected: $unsafeName" (Test-FixtureRejected $case)
    }
    Check-Payload 'invalid manifest preflight leaves application files intact' ((Read-FixtureFile $case.Install 'a.dll') -eq 'unchanged')
    Check-Payload 'invalid manifest preflight creates no backup' (-not (Test-Path -LiteralPath $case.Backup))

    $case = New-FixtureCase 'directory-collision'
    Write-FixtureFile $case.Install 'a.dll\user.txt' 'directory collision extra'
    Write-FixtureFile $case.Stage 'a.dll' 'new file'
    New-UltraExplorerPayloadManifest $case.Stage
    Check-Payload 'package file cannot replace an existing directory' (Test-FixtureRejected $case)
    Check-Payload 'directory collision keeps the nested user file' ((Read-FixtureFile $case.Install 'a.dll\user.txt') -eq 'directory collision extra')

    $case = New-FixtureCase 'uninstaller-stage'
    Write-FixtureFile $case.Stage 'unins000.exe' 'unexpected package uninstaller'
    $rejected = $false
    try { New-UltraExplorerPayloadManifest $case.Stage } catch { $rejected = $true }
    Check-Payload 'staging cannot claim the Setup uninstaller' $rejected

    $case = New-FixtureCase 'manifest-mismatch'
    Write-FixtureFile $case.Stage 'a.dll' 'stage a'
    New-UltraExplorerPayloadManifest $case.Stage
    Write-FixtureFile $case.Stage 'unlisted.dll' 'unlisted payload'
    Check-Payload 'unlisted staged files are rejected before any update' (Test-FixtureRejected $case)

    $case = New-FixtureCase 'existing-backup'
    Write-FixtureFile $case.Stage 'a.dll' 'stage a'
    New-UltraExplorerPayloadManifest $case.Stage
    Write-FixtureFile $case.Backup 'user.txt' 'existing backup user file'
    Check-Payload 'an existing backup directory cannot be reused' (Test-FixtureRejected $case)
    Check-Payload 'rejected backup retains its existing file' ((Read-FixtureFile $case.Backup 'user.txt') -eq 'existing backup user file')

    $case = New-FixtureCase 'reparse'
    $outside = Join-Path $case.Root 'outside'
    Write-FixtureFile $outside 'outside.txt' 'outside sentinel'
    $junction = Join-Path $case.Stage 'linked'
    try {
        [void](New-Item -ItemType Junction -Path $junction -Target $outside)
        $rejected = $false
        try { New-UltraExplorerPayloadManifest $case.Stage } catch { $rejected = $_.Exception.Message -match 'filesystem link' }
        Check-Payload 'staged reparse directory is rejected without traversal' $rejected
    } finally {
        if (Test-Path -LiteralPath $junction) { [System.IO.Directory]::Delete($junction, $false) }
    }
    Write-FixtureFile $case.Stage 'a.dll' 'stage a'
    New-UltraExplorerPayloadManifest $case.Stage
    Write-FixtureFile $case.Install $UltraExplorerPackageManifest "linked\outside.txt`n"
    $junction = Join-Path $case.Install 'linked'
    try {
        [void](New-Item -ItemType Junction -Path $junction -Target $outside)
        Check-Payload 'managed installed path through a reparse directory is rejected' (Test-FixtureRejected $case)
        Check-Payload 'reparse rejection preserves the outside sentinel' ((Read-FixtureFile $outside 'outside.txt') -eq 'outside sentinel')
    } finally {
        if (Test-Path -LiteralPath $junction) { [System.IO.Directory]::Delete($junction, $false) }
    }
    Write-Host "RESULT: $passed/$total payload checks passed."
} finally {
    # The exact GUID fixture is resolved under TEMP, never a user directory.
    $resolvedFixture = Get-UltraExplorerPayloadRoot $fixtureRoot
    $temporaryBoundary = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/')
    if (-not $resolvedFixture.StartsWith($temporaryBoundary + '\', [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($resolvedFixture) -notmatch '^UltraExplorer-payload-fixture-[0-9a-f]{32}$') {
        throw 'Fixture cleanup boundary validation failed.'
    }
    [void]@(Get-UltraExplorerPayloadFiles $resolvedFixture) # Reject links before recursive fixture cleanup.
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
