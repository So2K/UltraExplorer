# Owned temporary fixtures only: no production process closes, app launches,
# registry changes, network requests, or installation scripts.
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\ApplyDownloadedUpdate.ps1')
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ('UltraExplorer-quiet-apply-fixture-' + [guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($fixture)
$passed = 0
$total = 0
function Check-QuietApply([string]$Name, [bool]$Condition) {
    $script:total++
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:passed++
    Write-Host "PASS: $Name"
}
function New-QuietFixtureZip([string]$Name, [object[]]$Entries) {
    $path = Join-Path $fixture ($Name + '.zip')
    $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::CreateNew)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($item in $Entries) {
                $entry = $archive.CreateEntry([string]$item.Name)
                if ($null -ne $item.Attributes) { $entry.ExternalAttributes = [int]$item.Attributes }
                $output = $entry.Open()
                try {
                    $bytes = if ($item.FilePath) { [System.IO.File]::ReadAllBytes($item.FilePath) } else { [System.Text.Encoding]::UTF8.GetBytes([string]$item.Text) }
                    $output.Write($bytes, 0, $bytes.Length)
                } finally { $output.Dispose() }
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
    return [pscustomobject]@{ Path = $path; Size = (Get-Item -LiteralPath $path).Length; Hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
}
function Test-QuietZipRejected([object]$Package, [string]$Name) {
    $stage = Join-Path $fixture ('rejected-' + $Name)
    try { [void](Expand-QuietUpdatePackage $Package.Path $Package.Hash $Package.Size $stage); return $false } catch { return $true }
}
function Write-QuietFixture([string]$Root, [string]$Name, [string]$Value) {
    $path = Join-Path $Root $Name
    [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $path))
    [System.IO.File]::WriteAllText($path, $Value)
}
function New-QuietWindowFixture([int]$Id, [bool]$Exited, [bool]$WaitResult) {
    $process = [pscustomobject]@{ Id = $Id; HasExited = $Exited; WaitResult = $WaitResult; WaitCalls = 0; Disposed = $false }
    $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { param($milliseconds) $this.WaitCalls++; return $this.WaitResult }
    $process | Add-Member -MemberType ScriptMethod -Name Dispose -Value { $this.Disposed = $true }
    return [pscustomobject]@{ Process = $process; Started = [DateTime]::UtcNow; IsRole = $false }
}
try {
    $files = @(
        [pscustomobject]@{ Name = 'UltraExplorer.exe'; Text = 'fixture new exe' },
        [pscustomobject]@{ Name = 'ru/current.dll'; Text = 'new resource' },
        [pscustomobject]@{ Name = $UltraExplorerPackageManifest; Text = "UltraExplorer.exe`nru\current.dll`n$UltraExplorerPackageManifest`n" }
    )
    $package = New-QuietFixtureZip 'valid' $files
    $stage = Join-Path $fixture 'staging'
    [void](Expand-QuietUpdatePackage $package.Path $package.Hash $package.Size $stage)
    Check-QuietApply 'verified package extracts exact manifest files' (@(Get-UltraExplorerPayloadFiles $stage).Count -eq 3)
    Check-QuietApply 'normalized nested archive entry preserves bytes' ([System.IO.File]::ReadAllText((Join-Path $stage 'ru\current.dll')) -ceq 'new resource')
    $rejected = $false
    try { [void](Expand-QuietUpdatePackage $package.Path $package.Hash $package.Size $stage) } catch { $rejected = $true }
    Check-QuietApply 'staging cannot reuse an existing directory' $rejected
    $badDigest = [pscustomobject]@{ Path = $package.Path; Size = $package.Size; Hash = ('0' * 64) }
    Check-QuietApply 'apply rehash refuses a changed package' (Test-QuietZipRejected $badDigest 'hash')
    Check-QuietApply 'digest failure happens before staging creation' (-not (Test-Path -LiteralPath (Join-Path $fixture 'rejected-hash')))
    $badSize = [pscustomobject]@{ Path = $package.Path; Size = $package.Size + 1; Hash = $package.Hash }
    Check-QuietApply 'apply refuses changed package size' (Test-QuietZipRejected $badSize 'size')
    foreach ($unsafe in @('../outside.txt', 'nested/../outside.txt', '/absolute.txt', 'C:/absolute.txt', 'file.txt:stream', 'CON.txt', 'bad./file', 'bad /file', './file')) {
        $index = $total
        $bad = New-QuietFixtureZip ('unsafe-' + $index) (@($files) + @([pscustomobject]@{ Name = $unsafe; Text = 'unexpected' }))
        Check-QuietApply "unsafe archive path rejected: $unsafe" (Test-QuietZipRejected $bad ('unsafe-' + $index))
    }
    $bad = New-QuietFixtureZip 'case-collision' (@($files) + @([pscustomobject]@{ Name = 'ULTRAEXPLORER.EXE'; Text = 'duplicate' }))
    Check-QuietApply 'case-insensitive duplicate archive paths rejected' (Test-QuietZipRejected $bad 'case')
    $bad = New-QuietFixtureZip 'slash-collision' (@($files) + @([pscustomobject]@{ Name = 'ru\current.dll'; Text = 'duplicate' }))
    Check-QuietApply 'slash-normalized duplicate archive paths rejected' (Test-QuietZipRejected $bad 'slash')
    $bad = New-QuietFixtureZip 'directory-collision' (@($files) + @([pscustomobject]@{ Name = 'UltraExplorer.exe/child'; Text = 'child' }))
    Check-QuietApply 'archive file-directory collision rejected' (Test-QuietZipRejected $bad 'directory')
    $bad = New-QuietFixtureZip 'symlink' (@($files) + @([pscustomobject]@{ Name = 'linked'; Text = 'outside'; Attributes = 0x400 }))
    Check-QuietApply 'archive reparse attributes rejected' (Test-QuietZipRejected $bad 'link')
    $bad = New-QuietFixtureZip 'uninstaller' (@($files) + @([pscustomobject]@{ Name = 'unins000.exe'; Text = 'unexpected' }))
    Check-QuietApply 'archive cannot claim existing Setup uninstaller' (Test-QuietZipRejected $bad 'uninstaller')
    $bad = New-QuietFixtureZip 'unlisted' (@($files) + @([pscustomobject]@{ Name = 'extra.dll'; Text = 'not listed' }))
    Check-QuietApply 'unlisted payload entry rejected' (Test-QuietZipRejected $bad 'unlisted')
    $bad = New-QuietFixtureZip 'missing' @($files[0], $files[2])
    Check-QuietApply 'manifest claiming a missing entry rejected' (Test-QuietZipRejected $bad 'missing')
    $bad = New-QuietFixtureZip 'manifest-duplicate' @($files[0], [pscustomobject]@{ Name = $UltraExplorerPackageManifest; Text = "UltraExplorer.exe`nULTRAEXPLORER.EXE`n$UltraExplorerPackageManifest`n" })
    Check-QuietApply 'duplicate manifest names rejected' (Test-QuietZipRejected $bad 'manifest-duplicate')
    $bad = New-QuietFixtureZip 'no-manifest' @($files[0])
    Check-QuietApply 'package ownership manifest required' (Test-QuietZipRejected $bad 'no-manifest')
    $bad = New-QuietFixtureZip 'no-app' @($files[2])
    Check-QuietApply 'root application executable required' (Test-QuietZipRejected $bad 'no-app')
    $bad = New-QuietFixtureZip 'declared-bomb' $files
    $bytes = [System.IO.File]::ReadAllBytes($bad.Path)
    for ($i = 0; $i -lt $bytes.Length - 28; $i++) {
        if ([BitConverter]::ToUInt32($bytes, $i) -eq 0x02014B50) {
            [Array]::Copy([BitConverter]::GetBytes([uint32]1073741825), 0, $bytes, $i + 24, 4)
            break
        }
    }
    [System.IO.File]::WriteAllBytes($bad.Path, $bytes)
    $bad.Hash = (Get-FileHash -LiteralPath $bad.Path).Hash
    Check-QuietApply 'declared oversized entry rejected before decompression' (Test-QuietZipRejected $bad 'bomb')
    $install = Join-Path $fixture 'install'
    $backup = Join-Path $fixture 'backup'
    Write-QuietFixture $install 'UltraExplorer.exe' 'fixture old exe'
    Write-QuietFixture $install 'ru/obsolete.dll' 'old resource'
    Write-QuietFixture $install 'ru/my-notes.txt' 'untouched notes'
    Write-QuietFixture $install 'workspace.json' '{"user":"keep"}'
    Write-QuietFixture $install 'unins000.exe' 'owned by Setup'
    Write-QuietFixture $install $UltraExplorerPackageManifest "UltraExplorer.exe`nru\obsolete.dll`n$UltraExplorerPackageManifest`n"
    $oldManifest = [System.IO.File]::ReadAllText((Join-Path $install $UltraExplorerPackageManifest))
    $transaction = Invoke-UltraExplorerPayloadUpdate $stage $install $backup
    Check-QuietApply 'prepared portable payload updates the exact current folder' ([System.IO.File]::ReadAllText((Join-Path $install 'UltraExplorer.exe')) -ceq 'fixture new exe')
    Check-QuietApply 'update retains unknown workspace and nested notes' (([System.IO.File]::ReadAllText((Join-Path $install 'workspace.json')) -ceq '{"user":"keep"}') -and
        ([System.IO.File]::ReadAllText((Join-Path $install 'ru/my-notes.txt')) -ceq 'untouched notes'))
    Check-QuietApply 'update retains Setup uninstaller' ([System.IO.File]::ReadAllText((Join-Path $install 'unins000.exe')) -ceq 'owned by Setup')
    Undo-QuietPayloadUpdate $transaction
    Check-QuietApply 'failed startup rollback restores old application' ([System.IO.File]::ReadAllText((Join-Path $install 'UltraExplorer.exe')) -ceq 'fixture old exe')
    Check-QuietApply 'failed startup rollback restores obsolete managed files' ([System.IO.File]::ReadAllText((Join-Path $install 'ru/obsolete.dll')) -ceq 'old resource')
    Check-QuietApply 'failed startup rollback restores original ownership manifest' ([System.IO.File]::ReadAllText((Join-Path $install $UltraExplorerPackageManifest)) -ceq $oldManifest)
    Check-QuietApply 'failed startup rollback removes only newly installed resource' (-not (Test-Path -LiteralPath (Join-Path $install 'ru/current.dll')))
    Check-QuietApply 'failed startup rollback preserves workspace and notes' (([System.IO.File]::ReadAllText((Join-Path $install 'workspace.json')) -ceq '{"user":"keep"}') -and
        ([System.IO.File]::ReadAllText((Join-Path $install 'ru/my-notes.txt')) -ceq 'untouched notes'))
    $profile = Join-Path $fixture 'profile'
    Write-QuietFixture $profile 'dialog-integration.json' '{"Enabled":true,"ExcludedApplications":["original.exe"],"Extra":"preserve"}'
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $preference = Use-QuietIntegrationPreference $profile $sid
    Check-QuietApply 'legacy nullable Win+E resolves existing preference' ($preference.Enabled -and $preference.WinEEnabled)
    Write-QuietFixture $profile 'dialog-integration.json' '{"Enabled":false,"WinEEnabled":false,"LastRecovery":"temporary","ExcludedApplications":["later.exe"],"Extra":"latest"}'
    Use-QuietIntegrationPreference $profile $sid $preference
    $restored = Get-Content -LiteralPath (Join-Path $profile 'dialog-integration.json') -Raw | ConvertFrom-Json
    Check-QuietApply 'recovery restores original Enabled preference' ($restored.Enabled -eq $true)
    Check-QuietApply 'recovery restores original absence of nullable preference' ($null -eq $restored.PSObject.Properties['WinEEnabled'])
    Check-QuietApply 'recovery restores original absence of recovery status' ($null -eq $restored.PSObject.Properties['LastRecovery'])
    Check-QuietApply 'recovery preserves exclusions changed during operation' ($restored.ExcludedApplications[0] -ceq 'later.exe')
    Check-QuietApply 'recovery preserves unrelated latest preference fields' ($restored.Extra -ceq 'latest')
    $outside = Join-Path $fixture 'outside'
    Write-QuietFixture $outside 'sentinel.txt' 'keep'
    $linked = Join-Path $fixture 'linked'
    try {
        [void](New-Item -ItemType Junction -Path $linked -Target $outside)
        $rejected = $false
        try { [void](Expand-QuietUpdatePackage $package.Path $package.Hash $package.Size (Join-Path $linked 'stage')) } catch { $rejected = $true }
        Check-QuietApply 'staging through a junction rejected' $rejected
        Check-QuietApply 'junction rejection leaves outside sentinel unchanged' ([System.IO.File]::ReadAllText((Join-Path $outside 'sentinel.txt')) -ceq 'keep')
    } finally { if (Test-Path -LiteralPath $linked) { [System.IO.Directory]::Delete($linked, $false) } }
    $applicationRejected = $false
    try { Assert-QuietUpdateExecutable $stage '1.3.0-beta.1' } catch { $applicationRejected = $true }
    Check-QuietApply 'text disguised as executable cannot be applied' $applicationRejected
    $window = New-QuietWindowFixture 101 $false $false
    $close = Close-QuietUpdateWindows @($window) { param($ids, $starts) return 1223 }
    Check-QuietApply 'owner close veto returns cancellation' ($close.Error -match 'declined' -and -not $close.ClosedAny)
    Check-QuietApply 'owner close veto never retries the refused window' ($window.Process.WaitCalls -eq 0)
    Check-QuietApply 'cancelled close releases process observation handle' $window.Process.Disposed
    $window = New-QuietWindowFixture 102 $false $false
    $close = Close-QuietUpdateWindows @($window) { param($ids, $starts) return 0 }
    Check-QuietApply 'normal close timeout returns cancellation' ($close.Error -match 'did not close' -and -not $close.ClosedAny)
    Check-QuietApply 'normal close timeout gets no force fallback or second close' ($window.Process.WaitCalls -eq 1)
    $closed = New-QuietWindowFixture 103 $true $true
    $veto = New-QuietWindowFixture 104 $false $false
    $close = Close-QuietUpdateWindows @($closed, $veto) { param($ids, $starts) return 351 }
    Check-QuietApply 'partial close cancellation records that old application needs recovery' ($close.ClosedAny -and $close.Error -match 'declined')
    $window = New-QuietWindowFixture 105 $true $true
    $script:seenIdentity = $false
    $close = Close-QuietUpdateWindows @($window) { param($ids, $starts)
        $script:seenIdentity = $ids.Length -eq 1 -and $ids[0] -eq 105 -and $starts[0] -eq $window.Started.ToUniversalTime().ToFileTimeUtc()
        return 0
    }
    Check-QuietApply 'normal close passes exact PID and creation identity' $script:seenIdentity
    Check-QuietApply 'all gracefully exited owners allow the next file phase' ($close.ClosedAny -and -not $close.Error)
    $operation = Join-Path $fixture 'operation'
    [void][System.IO.Directory]::CreateDirectory($operation)
    $recovery = [pscustomobject]@{ WaitCalls = 0; NaturalWait = $false; ExitCode = 0 }
    $recovery | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {
        param($milliseconds)
        $this.WaitCalls++
        if ($null -eq $milliseconds) { $this.NaturalWait = $true; return }
        return $false
    }
    $rejected = $false
    try { Wait-QuietRecovery $recovery $operation '1.3.0-beta.2' } catch { $rejected = $_.Exception.Message -match 'cancelled' }
    $result = Get-Content -LiteralPath (Join-Path $operation 'result.json') -Raw | ConvertFrom-Json
    Check-QuietApply 'recovery timeout cancels all file replacement' $rejected
    Check-QuietApply 'recovery timeout writes failure status promptly' ($result.Status -ceq 'Failed' -and $result.WaitingRecovery -eq $true)
    Check-QuietApply 'recovery timeout waits for natural exit before preferences restore' ($recovery.NaturalWait -and $recovery.WaitCalls -eq 2)
    Write-QuietUpdateResult $operation ([pscustomobject]@{ Status = 'Installed' })
    $result = Get-Content -LiteralPath (Join-Path $operation 'result.json') -Raw | ConvertFrom-Json
    Check-QuietApply 'operation result can replace prior status atomically on WindowsPowerShell' ($result.Status -ceq 'Installed')
    # Exercise the full helper request/PE/version/preflight/transaction/result
    # path. Its process boundary is replaced with fixture adapters, so no app
    # or production integration is ever opened or closed.
    $application = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\UltraExplorer\bin\Release\net10.0-windows\win-x64\UltraExplorer.exe'
    if (-not (Test-Path -LiteralPath $application -PathType Leaf)) { throw 'Build UltraExplorer Release before the end-to-end apply fixture.' }
    $version = ([System.Diagnostics.FileVersionInfo]::GetVersionInfo($application).ProductVersion -split '\+', 2)[0]
    $e2e = Join-Path $fixture 'e2e'
    $destination = Join-Path $e2e 'application'
    $state = Join-Path $e2e 'profile'
    $operation = Join-Path $e2e 'operation'
    [void][System.IO.Directory]::CreateDirectory($destination)
    [void][System.IO.Directory]::CreateDirectory($state)
    [void][System.IO.Directory]::CreateDirectory($operation)
    [System.IO.File]::Copy($application, (Join-Path $destination 'UltraExplorer.exe'))
    Write-QuietFixture $destination 'notes.txt' 'portable note'
    Write-QuietFixture $destination $UltraExplorerPackageManifest "UltraExplorer.exe`n$UltraExplorerPackageManifest`n"
    $package = New-QuietFixtureZip 'e2e-payload' @(
        [pscustomobject]@{ Name = 'UltraExplorer.exe'; FilePath = $application },
        [pscustomobject]@{ Name = 'resource.dll'; Text = 'owned new resource' },
        [pscustomobject]@{ Name = $UltraExplorerPackageManifest; Text = "UltraExplorer.exe`nresource.dll`n$UltraExplorerPackageManifest`n" }
    )
    $request = [pscustomobject]@{ Format = 1; ExplicitInstall = $true; Version = $version; ZipPath = $package.Path;
        Sha256 = $package.Hash; Size = $package.Size; DestinationRoot = $destination; StateDirectory = $state;
        ResultPath = (Join-Path $operation 'result.json'); UserSid = $sid }
    $requestPath = Join-Path $operation 'request.json'
    [System.IO.File]::WriteAllText($requestPath, ($request | ConvertTo-Json))
    $originalCopies = ${function:Get-QuietTargetCopies}
    $originalStart = ${function:Start-QuietUpdateApp}
    $originalClose = ${function:Close-QuietUpdateWindows}
    $originalCim = Get-Item Function:Get-CimInstance -ErrorAction SilentlyContinue
    $script:fixtureStarts = 0
    try {
        function Get-CimInstance { param($ClassName, $Filter) return @() }
        function Get-QuietTargetCopies { param($Executable) return @() }
        function Start-QuietUpdateApp {
            param($Executable, $StateDirectory, $Role)
            $script:fixtureStarts++
            $process = [pscustomobject]@{}
            $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { param($milliseconds) return $false }
            $process | Add-Member -MemberType ScriptMethod -Name Dispose -Value { }
            return $process
        }
        Invoke-QuietDownloadedUpdate $requestPath
        $result = Get-Content -LiteralPath $request.ResultPath -Raw | ConvertFrom-Json
        Check-QuietApply 'complete explicit helper installs verified same-folder fixture' ($result.Status -ceq 'Installed' -and $result.Version -ceq $version)
        Check-QuietApply 'complete helper stages and owns the new package resource' ([System.IO.File]::ReadAllText((Join-Path $destination 'resource.dll')) -ceq 'owned new resource')
        Check-QuietApply 'complete helper preserves portable user note' ([System.IO.File]::ReadAllText((Join-Path $destination 'notes.txt')) -ceq 'portable note')
        Check-QuietApply 'complete helper restarts exactly once through isolated adapter' ($script:fixtureStarts -eq 1)
        Check-QuietApply 'complete helper removes staging after transaction' (-not (Test-Path -LiteralPath (Join-Path $operation 'staging')))
        Check-QuietApply 'disabled integration fixture creates no integration preferences' (-not (Test-Path -LiteralPath (Join-Path $state 'dialog-integration.json')))
        function Get-QuietTargetCopies { param($Executable) return @(New-QuietWindowFixture 110 $false $false) }
        function Close-QuietUpdateWindows { param($Windows, $CloseProcesses) return [pscustomobject]@{ ClosedAny = $false; Error = 'Fixture owner veto' } }
        $before = (Get-FileHash -LiteralPath (Join-Path $destination 'UltraExplorer.exe')).Hash
        Invoke-QuietDownloadedUpdate $requestPath
        $result = Get-Content -LiteralPath $request.ResultPath -Raw | ConvertFrom-Json
        Check-QuietApply 'complete helper owner veto produces failed result' ($result.Status -ceq 'Failed' -and $result.Error -match 'Fixture owner veto')
        Check-QuietApply 'complete helper owner veto preserves installed bytes' ((Get-FileHash -LiteralPath (Join-Path $destination 'UltraExplorer.exe')).Hash -ceq $before)
        Check-QuietApply 'complete helper owner veto does not restart the still-open window' ($script:fixtureStarts -eq 1)
        Check-QuietApply 'complete helper owner veto creates no file backup' (@(Get-ChildItem -LiteralPath $e2e -Directory -Filter 'UltraExplorer-update-backup-*').Count -eq 0)
    } finally {
        Set-Item Function:Get-QuietTargetCopies $originalCopies
        Set-Item Function:Start-QuietUpdateApp $originalStart
        Set-Item Function:Close-QuietUpdateWindows $originalClose
        if ($originalCim) { Set-Item Function:Get-CimInstance $originalCim.ScriptBlock } else { Remove-Item Function:Get-CimInstance }
    }
    Write-Host "RESULT: $passed/$total quiet update apply checks passed."
} finally {
    $full = Get-UltraExplorerPayloadRoot $fixture
    $temporary = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/')
    if (-not $full.StartsWith($temporary + '\', [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($full) -notmatch '^UltraExplorer-quiet-apply-fixture-[0-9a-f]{32}$') { throw 'Fixture cleanup boundary failed.' }
    [void]@(Get-UltraExplorerPayloadFiles $full)
    Remove-Item -LiteralPath $full -Recurse -Force
}
