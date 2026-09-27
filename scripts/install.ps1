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
Get-ChildItem -LiteralPath $destination -Force | Remove-Item -Recurse -Force
Copy-Item -Path (Join-Path $staging '*') -Destination $destination -Recurse -Force
Remove-Item -Recurse -Force -LiteralPath $staging

$shell = New-Object -ComObject WScript.Shell
function New-Shortcut([string]$path) {
    $shortcut = $shell.CreateShortcut($path)
    $shortcut.TargetPath = $executable
    $shortcut.WorkingDirectory = $destination
    $shortcut.IconLocation = "$executable,0"
    $shortcut.Description = 'UltraExplorer - a file manager on a zoomable canvas'
    $shortcut.Save()
}

$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'UltraExplorer.lnk'
New-Shortcut $startMenu
Write-Host "Start menu shortcut: $startMenu"
if ($Desktop) {
    $desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'UltraExplorer.lnk'
    New-Shortcut $desktopShortcut
    Write-Host "Desktop shortcut: $desktopShortcut"
}

Write-Host "Installed: $executable"
if ($Launch) {
    Start-Process -FilePath $executable -WorkingDirectory $destination
}
