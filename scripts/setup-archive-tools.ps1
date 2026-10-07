param([string]$Destination = (Join-Path $PSScriptRoot '../runtime/archives'))
$ErrorActionPreference = 'Stop'
$version = '26.04'
$setupHash = 'D54BF805F9F3704D1E8DB2FA3498AE7EF2DF0312B40B558E7C71C734430A665D'
$bootstrapHash = '256FECA8E274E5DA655E2A284FABAFD9F554365EB164862089DACD4E8276D282'
$archiveWork = Join-Path $PSScriptRoot '../artifacts/archive-bootstrap'
New-Item -ItemType Directory -Path $archiveWork -Force | Out-Null
function Get-PinnedArchiveFile([string]$Name, [string]$Hash) {
    $file = Join-Path $archiveWork $Name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $Hash) {
        Invoke-WebRequest "https://github.com/ip7z/7zip/releases/download/$version/$Name" -OutFile $file
    }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $Hash) { throw "Archive runtime SHA-256 mismatch: $Name" }
    return $file
}
$setup = Get-PinnedArchiveFile '7z2604-x64.exe' $setupHash
$bootstrap = Get-PinnedArchiveFile '7zr.exe' $bootstrapHash
$unpacked = Join-Path $archiveWork 'unpacked'
& $bootstrap x $setup "-o$unpacked" -y | Out-Null
if ($LASTEXITCODE -ne 0) { throw "7-Zip package extraction failed: $LASTEXITCODE" }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
foreach ($name in @('7z.dll', '7z.exe', 'License.txt', 'readme.txt')) {
    Copy-Item -LiteralPath (Join-Path $unpacked $name) -Destination (Join-Path $Destination $name) -Force
}
Write-Output "Prepared pinned 7-Zip $version archive runtime. Source: https://github.com/ip7z/7zip/releases/tag/$version"
