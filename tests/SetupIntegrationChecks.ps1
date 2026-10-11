# Original-user adapter fixtures only: no Setup/app, live profile, registry or UI.
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'installer/SetupIntegration.ps1')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('UltraExplorer-setup-fixture-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$passed = 0
function Check-Setup([string]$Name, [bool]$Condition) {
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:passed++
    Write-Host "PASS: $Name"
}
function Reject-Setup([string]$Name, [scriptblock]$Body) {
    $rejected = $false
    try { & $Body } catch { $rejected = $true }
    Check-Setup $Name $rejected
}
try {
    $script:originalRoot = Join-Path $fixture 'original-A'
    $adminRoot = Join-Path $fixture 'elevated-B'
    [void][IO.Directory]::CreateDirectory($script:originalRoot)
    [void][IO.Directory]::CreateDirectory($adminRoot)
    $app = Join-Path $fixture 'UltraExplorer.exe'
    $script:settingsFile = Join-Path $script:originalRoot 'dialog-integration.json'
    $adminFile = Join-Path $adminRoot 'dialog-integration.json'
    [IO.File]::WriteAllText($adminFile, '{"Enabled":false,"WinEEnabled":false,"ExcludedApplications":["admin.exe"]}')
    $adminBytes = [IO.File]::ReadAllText($adminFile)
    $script:runState = [pscustomobject]@{ 'UltraExplorer dialogs' = '"old.exe" --dialog-agent'; 'UltraExplorer Win+E' = '"old.exe" --shortcut-agent' }
    $script:started = New-Object 'System.Collections.Generic.List[string]'
    $script:recoverFailure = $false
    $script:startFailure = $false
    $script:foreignSid = $false
    function Get-SetupIntegrationContext { [pscustomobject]@{ Root = $script:originalRoot; Sid = $(if ($script:foreignSid) { 'B' } else { 'A' }) } }
    function Read-SetupIntegrationRun { return $script:runState }
    function Restore-SetupIntegrationRun([string]$Command) { $script:runState.'UltraExplorer dialogs' = $Command }
    function Get-SetupIntegrationRoles { param($App, $Sid) return @() }
    function Start-SetupIntegrationRole([string]$App, [string]$Role) {
        [void]$script:started.Add($Role)
        if ($script:startFailure -and $Role -ne '--dialog-recover') { throw 'Fixture role start failed.' }
        if ($Role -eq '--dialog-recover') {
            $settings = Read-SetupIntegrationJson $script:settingsFile
            $settings.Enabled = $false; $settings.WinEEnabled = $false
            Write-SetupIntegrationJson $script:settingsFile $settings
        }
        $process = [pscustomobject]@{ ExitCode = $(if ($script:recoverFailure) { 1 } else { 0 }) }
        $process | Add-Member ScriptMethod WaitForExit { param($milliseconds) return $true }
        $process | Add-Member ScriptMethod Dispose { }
        $process | Add-Member ScriptMethod Kill { }
        return $process
    }
    [IO.File]::WriteAllText($script:settingsFile, '{"Enabled":true,"WinEEnabled":true,"ExcludedApplications":["original.exe"],"Extra":"keep"}')
    $id = [guid]::NewGuid().ToString('N')
    Invoke-SetupIntegration 'Snapshot' $app $id $false
    $record = Get-SetupIntegrationPath $script:originalRoot ('setup-integration-' + $id + '.json')
    $snapshot = Read-SetupIntegrationJson $record
    Check-Setup 'snapshot belongs to original A, with its enabled Win+E' ($snapshot.Sid -ceq 'A' -and $snapshot.WinEEnabled -and $snapshot.Enabled)
    Check-Setup 'the recovery ran once under the same adapter context' ($script:started.Count -eq 1 -and $script:started[0] -ceq '--dialog-recover')
    Check-Setup 'the original profile was paused' (-not (Read-SetupIntegrationJson $script:settingsFile).WinEEnabled)
    Check-Setup 'elevated B profile was never read or changed by the operation' ([IO.File]::ReadAllText($adminFile) -ceq $adminBytes)
    $settings = Read-SetupIntegrationJson $script:settingsFile
    $settings.ExcludedApplications = @('changed-while-paused.exe')
    Write-SetupIntegrationJson $script:settingsFile $settings
    [IO.File]::WriteAllText($app, 'fixture file, never executed')
    Invoke-SetupIntegration 'Restore' $app $id $true
    $restored = Read-SetupIntegrationJson $script:settingsFile
    Check-Setup 'upgrade restores original A preferences, not admin B preferences' ($restored.Enabled -and $restored.WinEEnabled)
    Check-Setup 'restore merges current exclusions and unrelated fields' ($restored.ExcludedApplications[0] -ceq 'changed-while-paused.exe' -and $restored.Extra -ceq 'keep')
    Check-Setup 'upgrade starts both originally enabled roles' ($script:started.Count -eq 3 -and $script:started[1] -ceq '--dialog-agent' -and $script:started[2] -ceq '--shortcut-agent')
    Check-Setup 'upgrade registers the installed executable in the original context' ($script:runState.'UltraExplorer dialogs' -ceq ('"' + $app + '" --dialog-agent'))
    Check-Setup 'completed restore removes only its owned record' (-not (Test-Path -LiteralPath $record))
    $startsBefore = $script:started.Count
    Invoke-SetupIntegration 'Restore' $app $id $true
    Check-Setup 'duplicate restore is a no-op' ($script:started.Count -eq $startsBefore)
    Check-Setup 'elevated B profile remains unchanged after restoration' ([IO.File]::ReadAllText($adminFile) -ceq $adminBytes)

    $script:runState.'UltraExplorer dialogs' = '"previous.exe" --dialog-agent'
    $id = [guid]::NewGuid().ToString('N')
    $script:recoverFailure = $true
    Reject-Setup 'a recovery error refuses the upgrade' { Invoke-SetupIntegration 'Snapshot' $app $id $false }
    $record = Get-SetupIntegrationPath $script:originalRoot ('setup-integration-' + $id + '.json')
    Check-Setup 'failed recovery retains its original preference record' (Test-Path -LiteralPath $record)
    $script:recoverFailure = $false
    Invoke-SetupIntegration 'Restore' $app $id $false
    Check-Setup 'cancelled upgrade restores enabled original Win+E' ((Read-SetupIntegrationJson $script:settingsFile).WinEEnabled)
    Check-Setup 'cancelled upgrade restores the original Run command' ($script:runState.'UltraExplorer dialogs' -ceq '"previous.exe" --dialog-agent')

    $id = [guid]::NewGuid().ToString('N')
    Invoke-SetupIntegration 'Snapshot' $app $id $false
    $record = Get-SetupIntegrationPath $script:originalRoot ('setup-integration-' + $id + '.json')
    $script:startFailure = $true
    Reject-Setup 'an agent start failure is reported' { Invoke-SetupIntegration 'Restore' $app $id $true }
    Check-Setup 'failed restoration retains its record for a retry' (Test-Path -LiteralPath $record)
    $script:startFailure = $false
    $script:foreignSid = $true
    Reject-Setup 'another SID cannot consume the original snapshot' { Invoke-SetupIntegration 'Restore' $app $id $true }
    Check-Setup 'wrong SID leaves the original snapshot available' (Test-Path -LiteralPath $record)
    $script:foreignSid = $false
    Invoke-SetupIntegration 'Restore' $app $id $true
    Check-Setup 'a retry finishes the retained restore' (-not (Test-Path -LiteralPath $record) -and (Read-SetupIntegrationJson $script:settingsFile).WinEEnabled)

    $absent = New-SetupIntegrationSnapshot ([pscustomobject]@{ Enabled = $false }) 'A' $app $null
    $merged = Merge-SetupIntegrationSnapshot ([pscustomobject]@{ Enabled = $true; WinEEnabled = $true; Extra = 7 }) $absent 'A' $app
    Check-Setup 'restore preserves absent nullable fields and unrelated values' (-not $merged.Enabled -and $null -eq $merged.PSObject.Properties['WinEEnabled'] -and $merged.Extra -eq 7)
    $legacy = New-SetupIntegrationSnapshot ([pscustomobject]@{ Enabled = $true }) 'A' $app $null
    Check-Setup 'legacy nullable Win+E follows Enabled' ($legacy.WinEEnabled)
    Reject-Setup 'another executable cannot consume a snapshot' { [void](Merge-SetupIntegrationSnapshot $merged $legacy 'A' ($app + '.other')) }
    Reject-Setup 'invalid boolean settings refuse capture' { [void](New-SetupIntegrationSnapshot ([pscustomobject]@{ Enabled = 'true' }) 'A' $app $null) }
    Reject-Setup 'parent traversal cannot name an integration record' { [void](Get-SetupIntegrationPath $script:originalRoot '../dialog-integration.json') }
    Reject-Setup 'a non-GUID operation cannot name a record' { Invoke-SetupIntegration 'Snapshot' $app '../wrong' $false }
    $invalid = Join-Path $fixture 'invalid.json'
    [IO.File]::WriteAllText($invalid, '[]')
    Reject-Setup 'a non-object profile refuses read' { [void](Read-SetupIntegrationJson $invalid) }
    [IO.File]::WriteAllText($invalid, ('x' * 65537))
    Reject-Setup 'oversized profile refuses read' { [void](Read-SetupIntegrationJson $invalid) }
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes([IO.File]::ReadAllText((Join-Path (Split-Path -Parent $PSScriptRoot) 'installer/SetupIntegration.ps1'))))
    Check-Setup 'UTF16 helper encoding fits a normal Windows command line' ($encoded.Length + 1500 -lt 32767)
    Check-Setup 'UTF16 helper encoding roundtrips' ([Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($encoded)) -ceq [IO.File]::ReadAllText((Join-Path (Split-Path -Parent $PSScriptRoot) 'installer/SetupIntegration.ps1')))
    Write-Host "$passed/$passed Setup integration checks passed"
} finally {
    $fullFixture = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if (-not $fullFixture.StartsWith($tempRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($fullFixture) -notmatch '^UltraExplorer-setup-fixture-[0-9a-f]{32}$') { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $fullFixture -Recurse -Force
}
