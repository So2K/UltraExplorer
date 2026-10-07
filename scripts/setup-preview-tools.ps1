param(
    [string]$Destination = (Join-Path $PSScriptRoot '../runtime/preview'),
    [string]$CacheDirectory = (Join-Path $PSScriptRoot '../artifacts/preview-tools/downloads')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Destination = [IO.Path]::GetFullPath($Destination)
$CacheDirectory = [IO.Path]::GetFullPath($CacheDirectory)
New-Item -ItemType Directory -Force -Path $Destination, $CacheDirectory | Out-Null

# Portable pinned distributions. Never run installers, change PATH, file
# associations, user configuration, or any global mpv/F3D state.
$packages = @(
    @{
        Name = 'f3d'; Version = '3.5.0'; File = 'F3D-3.5.0-Windows-x86_64.zip'
        Url = 'https://github.com/f3d-app/f3d/releases/download/v3.5.0/F3D-3.5.0-Windows-x86_64.zip'
        Hash = 'db57f9fb7e1bbe2c022ec19dab3fd1eb38545f8c7b3d29d3906a951936a2e897'
        Executable = 'f3d.exe'
    },
    @{
        Name = 'mpv'; Version = '20261006-git-6c092d978b'; File = 'mpv-x86_64-20261006-git-6c092d978b.7z'
        Url = 'https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261006/mpv-x86_64-20261006-git-6c092d978b.7z'
        Hash = 'be14ecd58dbc0a8fda31d6243c07937d6cd19a1a9dbecc04bba324bbc7ce22c2'
        Executable = 'mpv.exe'
    }
)

function Get-VerifiedArchive($Url, $File, $Hash) {
    $archive = Join-Path $CacheDirectory $File
    if (!(Test-Path -LiteralPath $archive) -or
        (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $Hash) {
        $partial = "$archive.partial"
        Invoke-WebRequest -Uri $Url -OutFile $partial
        if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $Hash) {
            throw "SHA256 mismatch for $File"
        }
        Move-Item -LiteralPath $partial -Destination $archive -Force
    }
    return $archive
}

# Official 7-Zip reduced command line extractor, used only in the download
# cache; not installed and not included in the application's runtime payload.
$extractor = Get-VerifiedArchive `
    'https://github.com/ip7z/7zip/releases/download/26.04/7zr.exe' `
    '7zr-26.04.exe' '256feca8e274e5da655e2a284fabafd9f554365eb164862089dacd4e8276d282'

foreach ($package in $packages) {
    $archive = Get-VerifiedArchive $package.Url $package.File $package.Hash
    $stage = Join-Path $CacheDirectory ("expanded-$($package.Name)-$([Guid]::NewGuid().ToString('N'))")
    New-Item -ItemType Directory -Path $stage | Out-Null
    if ($package.File.EndsWith('.zip')) {
        Expand-Archive -LiteralPath $archive -DestinationPath $stage
    } else {
        & $extractor x '-y' "-o$stage" $archive | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7-Zip extraction failed for $($package.File)" }
    }
    $executable = Get-ChildItem -LiteralPath $stage -File -Recurse -Filter $package.Executable | Select-Object -First 1
    if (!$executable) { throw "No $($package.Executable) in verified archive" }
    # F3D requires the complete bin/lib/share distribution (its dynamic reader
    # plugins and their license files), mpv ships as a standalone static exe.
    $distribution = $executable.Directory.FullName
    if ($package.Name -eq 'f3d') { $distribution = $executable.Directory.Parent.FullName }
    $target = Join-Path $Destination $package.Name
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Get-ChildItem -LiteralPath $distribution -Force | Copy-Item -Destination $target -Recurse -Force
    $relativeExe = if ($package.Name -eq 'f3d') { 'bin/f3d.exe' } else { 'mpv.exe' }
    if (!(Test-Path -LiteralPath (Join-Path $target $relativeExe))) { throw "Incomplete runtime $target" }
    @{
        version = $package.Version; source = $package.Url; archiveSha256 = $package.Hash
        executable = $relativeExe
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $target 'distribution.json') -Encoding utf8
    Write-Host "$($package.Name) $($package.Version): $target"
}

# Ship the exact mpv upstream license texts (immutable source commit).
$mpvLicenseRoot = Join-Path $Destination 'mpv/licenses'
New-Item -ItemType Directory -Force -Path $mpvLicenseRoot | Out-Null
foreach ($license in @('Copyright', 'LICENSE.GPL', 'LICENSE.LGPL')) {
    $source = "https://raw.githubusercontent.com/mpv-player/mpv/6c092d978b/$license"
    Invoke-WebRequest -Uri $source -OutFile (Join-Path $mpvLicenseRoot $license)
}
Write-Host 'Preview tools prepared. Re-run after checkout before build/publish.'
