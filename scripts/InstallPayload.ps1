# File-only package updates. This helper has no process, registry, UI or profile
# effects, so the transaction can also be checked with owned temporary fixtures.
$UltraExplorerPackageManifest = 'UltraExplorer-package-files.txt'

function Get-UltraExplorerPayloadRoot([string]$Path) {
    if (-not [System.IO.Path]::IsPathRooted($Path)) { throw "Payload root must be absolute: $Path" }
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if ($full -eq [System.IO.Path]::GetPathRoot($full).TrimEnd('\', '/')) {
        throw "A filesystem root cannot be a payload directory: $Path"
    }
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                throw "Payload path contains a filesystem link: $cursor"
            }
        }
        $parent = Split-Path -Parent $cursor
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
    if (Test-Path -LiteralPath $full -PathType Leaf) { throw "Payload root is a file: $full" }
    return $full
}

function ConvertTo-UltraExplorerPayloadName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name) -or [System.IO.Path]::IsPathRooted($Name)) {
        throw "Package filename must be relative: $Name"
    }
    $relative = $Name.Replace('/', '\')
    foreach ($segment in $relative.Split('\')) {
        if (-not $segment -or $segment -eq '.' -or $segment -eq '..' -or
            $segment.EndsWith('.') -or $segment.EndsWith(' ') -or
            $segment.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
            $segment -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
            throw "Unsafe package filename: $Name"
        }
    }
    return $relative
}

function Get-UltraExplorerPayloadPath([string]$Root, [string]$Name) {
    $relative = ConvertTo-UltraExplorerPayloadName $Name
    $full = [System.IO.Path]::GetFullPath((Join-Path $Root $relative))
    if (-not $full.StartsWith($Root + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Package filename is outside its payload directory: $Name"
    }
    $cursor = $full
    while ($cursor.Length -ge $Root.Length) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                throw "Package filename contains a filesystem link: $cursor"
            }
        }
        if ([string]::Equals($cursor, $Root, [System.StringComparison]::OrdinalIgnoreCase)) { break }
        $cursor = Split-Path -Parent $cursor
    }
    return $full
}

function Test-UltraExplorerUninstallerName([string]$Name) {
    return $Name.Split('\')[0].StartsWith('unins', [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-UltraExplorerPayloadFiles([string]$Root) {
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            $relative = ConvertTo-UltraExplorerPayloadName $item.FullName.Substring($Root.Length + 1)
            [void](Get-UltraExplorerPayloadPath $Root $relative)
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
            else { $relative }
        }
    }
}

function New-UltraExplorerPayloadManifest([string]$StagingRoot) {
    $root = Get-UltraExplorerPayloadRoot $StagingRoot
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Payload staging directory does not exist: $root" }
    $files = @(Get-UltraExplorerPayloadFiles $root)
    foreach ($name in $files) {
        if (Test-UltraExplorerUninstallerName $name) { throw "The application payload cannot replace Setup's uninstaller: $name" }
    }
    $manifest = Get-UltraExplorerPayloadPath $root $UltraExplorerPackageManifest
    if (Test-Path -LiteralPath $manifest -PathType Container) { throw "Package manifest is a directory: $manifest" }
    $files = @(@($files) + @($UltraExplorerPackageManifest) | Sort-Object -Unique)
    [System.IO.File]::WriteAllLines($manifest, [string[]]$files, (New-Object System.Text.UTF8Encoding($true)))
}

function Read-UltraExplorerPayloadManifest([string]$Root, [switch]$AllowMissing, [switch]$Previous) {
    $manifest = Get-UltraExplorerPayloadPath $Root $UltraExplorerPackageManifest
    if (-not (Test-Path -LiteralPath $manifest)) {
        if ($AllowMissing) { return }
        throw "Package manifest is missing: $manifest"
    }
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf) -or (Get-Item -LiteralPath $manifest).Length -gt 4194304) {
        throw "Package manifest is not a bounded file: $manifest"
    }
    $names = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($line in [System.IO.File]::ReadAllLines($manifest)) {
        if (-not $line) { continue }
        $relative = ConvertTo-UltraExplorerPayloadName $line
        if (Test-UltraExplorerUninstallerName $relative) {
            if ($Previous) { continue }
            throw "The application payload cannot replace Setup's uninstaller: $relative"
        }
        [void]$names.Add($relative)
    }
    [void]$names.Add($UltraExplorerPackageManifest)
    $names | Sort-Object
}

function New-UltraExplorerPayloadDirectory([string]$Root, [string]$Path, [object]$Created) {
    $missing = New-Object 'System.Collections.Generic.Stack[string]'
    $cursor = $Path
    while ($cursor.Length -ge $Root.Length -and -not (Test-Path -LiteralPath $cursor)) {
        $missing.Push($cursor)
        if ([string]::Equals($cursor, $Root, [System.StringComparison]::OrdinalIgnoreCase)) { break }
        $cursor = Split-Path -Parent $cursor
    }
    if (Test-Path -LiteralPath $cursor -PathType Leaf) { throw "Payload directory collides with a file: $cursor" }
    while ($missing.Count -gt 0) {
        $directory = $missing.Pop()
        [void][System.IO.Directory]::CreateDirectory($directory)
        [void]$Created.Add($directory)
    }
}

function Remove-UltraExplorerPayloadEmptyDirectories([string]$Root, [object]$Directories) {
    foreach ($directory in @($Directories | Sort-Object Length -Descending)) {
        if (-not (Test-Path -LiteralPath $directory)) { continue }
        if ([string]::Equals($directory, $Root, [System.StringComparison]::OrdinalIgnoreCase)) {
            [void](Get-UltraExplorerPayloadRoot $directory)
        } else {
            [void](Get-UltraExplorerPayloadPath $Root $directory.Substring($Root.Length + 1))
        }
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw "Created payload directory became a file: $directory" }
        if (@(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) { [System.IO.Directory]::Delete($directory, $false) }
    }
}

function Complete-UltraExplorerPayloadUpdate([object]$Update) {
    # Only files placed in this fresh backup by this transaction may be deleted.
    # An unexpected addition is retained, and directories are removed only empty.
    foreach ($name in $Update.BackupFiles) {
        $path = Get-UltraExplorerPayloadPath $Update.BackupRoot $name
        if (Test-Path -LiteralPath $path) {
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Backup file became a directory: $path" }
            Remove-Item -LiteralPath $path -Force
        }
    }
    Remove-UltraExplorerPayloadEmptyDirectories $Update.BackupRoot $Update.BackupDirectories
    if (Test-Path -LiteralPath $Update.BackupRoot) { Write-Warning "Additional files remain available at $($Update.BackupRoot)" }
}

function Invoke-UltraExplorerPayloadUpdate {
    param(
        [Parameter(Mandatory = $true)][string]$StagingRoot,
        [Parameter(Mandatory = $true)][string]$DestinationRoot,
        [Parameter(Mandatory = $true)][string]$BackupRoot,
        # An owned fixture can fail a chosen copy without installing/running app.
        [scriptblock]$BeforeCopyFile
    )
    $stage = Get-UltraExplorerPayloadRoot $StagingRoot
    $destination = Get-UltraExplorerPayloadRoot $DestinationRoot
    $backup = Get-UltraExplorerPayloadRoot $BackupRoot
    $roots = @($stage, $destination, $backup)
    for ($i = 0; $i -lt $roots.Count; $i++) {
        for ($j = 0; $j -lt $roots.Count; $j++) {
            if ($i -ne $j -and ($roots[$i] -eq $roots[$j] -or $roots[$i].StartsWith($roots[$j] + '\', [System.StringComparison]::OrdinalIgnoreCase))) {
                throw 'Payload staging, installation and backup directories must not overlap.'
            }
        }
    }
    if (Test-Path -LiteralPath $backup) { throw "Payload backup directory must be new: $backup" }
    $newFiles = @(Read-UltraExplorerPayloadManifest $stage)
    $actualFiles = @(Get-UltraExplorerPayloadFiles $stage)
    if (@(Compare-Object $newFiles $actualFiles).Count -gt 0) { throw 'Package manifest does not match the staged files.' }
    $previousFiles = @(Read-UltraExplorerPayloadManifest $destination -AllowMissing -Previous)
    $managedFiles = @(@($previousFiles) + @($newFiles) | Sort-Object -Unique)
    # Complete validation precedes the first move. Unknown paths and directories
    # never become owned merely because they are inside the installation folder.
    foreach ($name in $managedFiles) {
        $path = Get-UltraExplorerPayloadPath $destination $name
        if (Test-Path -LiteralPath $path -PathType Container) { throw "Package file collides with an installed directory: $path" }
        $parent = Split-Path -Parent $path
        while ($parent.Length -ge $destination.Length) {
            if (Test-Path -LiteralPath $parent -PathType Leaf) { throw "Package directory collides with an installed file: $parent" }
            if ($parent -eq $destination) { break }
            $parent = Split-Path -Parent $parent
        }
        [void](Get-UltraExplorerPayloadPath $backup $name)
    }
    $update = [pscustomobject]@{
        DestinationRoot = $destination; BackupRoot = $backup
        BackupFiles = (New-Object 'System.Collections.Generic.List[string]')
        CopiedFiles = (New-Object 'System.Collections.Generic.List[string]')
        TemporaryFiles = (New-Object 'System.Collections.Generic.List[string]')
        BackupDirectories = (New-Object 'System.Collections.Generic.List[string]')
        DestinationDirectories = (New-Object 'System.Collections.Generic.List[string]')
    }
    try {
        New-UltraExplorerPayloadDirectory $destination $destination $update.DestinationDirectories
        foreach ($name in $managedFiles) {
            $path = Get-UltraExplorerPayloadPath $destination $name
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
            $saved = Get-UltraExplorerPayloadPath $backup $name
            New-UltraExplorerPayloadDirectory $backup (Split-Path -Parent $saved) $update.BackupDirectories
            [System.IO.File]::Move($path, $saved)
            [void]$update.BackupFiles.Add($name)
        }
        foreach ($name in $newFiles) {
            if ($BeforeCopyFile) { [void](& $BeforeCopyFile $name) }
            $source = Get-UltraExplorerPayloadPath $stage $name
            $target = Get-UltraExplorerPayloadPath $destination $name
            New-UltraExplorerPayloadDirectory $destination (Split-Path -Parent $target) $update.DestinationDirectories
            $temporaryName = $name + '.update-' + [guid]::NewGuid().ToString('N') + '.tmp'
            $temporary = Get-UltraExplorerPayloadPath $destination $temporaryName
            $inputStream = [System.IO.File]::OpenRead($source)
            try {
                $outputStream = [System.IO.File]::Open($temporary, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
                [void]$update.TemporaryFiles.Add($temporaryName)
                try { $inputStream.CopyTo($outputStream); $outputStream.Flush($true) }
                finally { $outputStream.Dispose() }
            } finally { $inputStream.Dispose() }
            [System.IO.File]::SetLastWriteTimeUtc($temporary, [System.IO.File]::GetLastWriteTimeUtc($source))
            [void](Get-UltraExplorerPayloadPath $destination $name)
            [System.IO.File]::Move($temporary, $target)
            [void]$update.CopiedFiles.Add($name)
            [void]$update.TemporaryFiles.Remove($temporaryName)
        }
        return $update
    } catch {
        $originalError = $_
        $rollbackErrors = New-Object 'System.Collections.Generic.List[string]'
        foreach ($name in @($update.TemporaryFiles) + @($update.CopiedFiles)) {
            try {
                $path = Get-UltraExplorerPayloadPath $destination $name
                if (Test-Path -LiteralPath $path) {
                    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Rollback file became a directory: $path" }
                    Remove-Item -LiteralPath $path -Force
                }
            } catch { [void]$rollbackErrors.Add($_.Exception.Message) }
        }
        foreach ($name in $update.BackupFiles) {
            try {
                $saved = Get-UltraExplorerPayloadPath $backup $name
                $target = Get-UltraExplorerPayloadPath $destination $name
                [System.IO.File]::Move($saved, $target)
            } catch { [void]$rollbackErrors.Add($_.Exception.Message) }
        }
        try { Remove-UltraExplorerPayloadEmptyDirectories $destination $update.DestinationDirectories }
        catch { [void]$rollbackErrors.Add($_.Exception.Message) }
        try { Remove-UltraExplorerPayloadEmptyDirectories $backup $update.BackupDirectories }
        catch { [void]$rollbackErrors.Add($_.Exception.Message) }
        if ($rollbackErrors.Count -gt 0) {
            throw "Payload update failed: $($originalError.Exception.Message) Rollback needs attention; previous files remain at $backup. $($rollbackErrors -join ' ')"
        }
        throw $originalError
    }
}
