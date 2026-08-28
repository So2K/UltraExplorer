<#
.SYNOPSIS
    Asks UltraExplorer to pick files or a folder.

.EXAMPLE
    . .\pick.ps1
    Select-UltraPath -Mode folder -Title 'Куда импортировать'
    Select-UltraPath -Filter 'Component|*.component;*.json|All Files|*.*' -MultiSelect
    Select-UltraPath -Mode save -FileName 'export' -Extension txt
#>

function Select-UltraPath {
    [CmdletBinding()]
    param(
        [ValidateSet('open', 'save', 'folder')]
        [string] $Mode = 'open',

        [string] $Title,
        [string] $Filter,
        [string] $FileName,
        [string] $Extension,
        [string] $StartIn,
        [string] $OkLabel,
        [switch] $MultiSelect,
        [switch] $ShowHidden,

        # Give each caller its own guid and the picker will come back to the
        # folder and file type that caller used last.
        [string] $ClientGuid,

        [string] $Executable = 'UltraExplorer.exe'
    )

    $resultFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "ultrapick-$([guid]::NewGuid()).json")

    $arguments = @('--pick', '--mode', $Mode, '--result', $resultFile)
    if ($Title)      { $arguments += @('--title', $Title) }
    if ($Filter)     { $arguments += @('--filter', $Filter) }
    if ($FileName)   { $arguments += @('--file-name', $FileName) }
    if ($Extension)  { $arguments += @('--ext', $Extension) }
    if ($StartIn)    { $arguments += @('--start', $StartIn) }
    if ($OkLabel)    { $arguments += @('--ok-label', $OkLabel) }
    if ($ClientGuid) { $arguments += @('--client-guid', $ClientGuid) }
    if ($MultiSelect) { $arguments += '--multiselect' }
    if ($ShowHidden)  { $arguments += @('--flag', 'ForceShowHidden') }

    # Quote every argument: a filter is full of spaces, semicolons and
    # parentheses, and Start-Process joins the list with spaces otherwise.
    $line = ($arguments | ForEach-Object { '"{0}"' -f ($_ -replace '"', '\"') }) -join ' '

    try {
        $process = Start-Process -FilePath $Executable -ArgumentList $line -PassThru -Wait
        if ($process.ExitCode -ne 0 -or -not (Test-Path $resultFile)) {
            return $null
        }

        $result = Get-Content $resultFile -Raw | ConvertFrom-Json
        if (-not $result.accepted) { return $null }
        return $result.paths
    }
    finally {
        if (Test-Path $resultFile) { Remove-Item $resultFile -Force }
    }
}
