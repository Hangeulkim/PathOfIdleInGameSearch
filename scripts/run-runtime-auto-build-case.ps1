[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Theme,

    [Parameter(Mandatory = $true)]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$HeroId,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputPrefix,

    [switch]$VerifyRollback
)

$ErrorActionPreference = 'Stop'

$autoBuildTimeoutSeconds = 240
$partyReportTimeoutSeconds = 150
$pollMilliseconds = 500
$statusIntervalSeconds = 10
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$tempRoot = [IO.Path]::GetTempPath()
$autoRequestPath = [IO.Path]::Combine(
    $tempRoot, 'PathOfIdleInGameSearch-v1.1.8-runtime-test.request')
$autoResultPath = [IO.Path]::Combine(
    $tempRoot, 'PathOfIdleInGameSearch-v1.1.8-runtime-test.result')
$partyRequestPath = [IO.Path]::Combine(
    $tempRoot, 'PathOfIdleInGameSearch-v1.1.8-party-report.request')
$partyResultPath = [IO.Path]::Combine(
    $tempRoot, 'PathOfIdleInGameSearch-v1.1.8-party-report.result.json')

$normalizedTheme = $Theme.Trim().ToLowerInvariant()
if ([string]::IsNullOrWhiteSpace($normalizedTheme)) {
    throw 'Theme must not be blank.'
}
if ($normalizedTheme -match '[|\r\n]') {
    throw 'Theme must not contain a pipe or newline.'
}
$supportedThemes = @(
    'auto', 'physical', 'elemental', 'fire', 'ice', 'lightning',
    'minion', 'bleed', 'corrosion', 'crit', 'support', 'defense'
)
if ($supportedThemes -notcontains $normalizedTheme -and
    $normalizedTheme -notmatch '^damage:[1-9][0-9]*$') {
    throw "Unsupported theme '$normalizedTheme'. PIERCE is a hero name; use 'auto' or 'physical' for that hero."
}

$workspaceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([IO.Path]::IsPathRooted($OutputPrefix)) {
    $resolvedPrefix = [IO.Path]::GetFullPath($OutputPrefix)
}
else {
    $resolvedPrefix = [IO.Path]::GetFullPath([IO.Path]::Combine($workspaceRoot, $OutputPrefix))
}
$workspaceBoundary = $workspaceRoot + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedPrefix.StartsWith(
        $workspaceBoundary, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPrefix must resolve inside the workspace: $resolvedPrefix"
}

$outputDirectory = [IO.Path]::GetDirectoryName($resolvedPrefix)
if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
    throw "OutputPrefix has no output directory: $resolvedPrefix"
}
if (-not [IO.Directory]::Exists($outputDirectory)) {
    [void][IO.Directory]::CreateDirectory($outputDirectory)
}

$savedResultPath = "$resolvedPrefix.result.txt"
$savedPartyReportPath = "$resolvedPrefix-party-report.json"
$autoRequestContents = "APPLY JOINT BUILD v1.1.8|theme=$normalizedTheme|hero=$HeroId"
if ($VerifyRollback) { $autoRequestContents += '|verifyFailure=true' }
$partyRequestContents = 'EXPORT PARTY DATA v1.1.8'

function Write-Status {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host ('[{0:HH:mm:ss}] {1}' -f [DateTime]::Now, $Message)
}

function Write-ExactUtf8 {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Contents
    )
    [IO.File]::WriteAllText($Path, $Contents, $script:utf8NoBom)
}

function Remove-OwnedRequest {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedContents
    )
    try {
        if (-not [IO.File]::Exists($Path)) { return }
        $actual = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8).Trim()
        if ([string]::Equals($actual, $ExpectedContents, [StringComparison]::Ordinal)) {
            [IO.File]::Delete($Path)
        }
    }
    catch {
        # Cleanup is best-effort; preserve the original test failure.
    }
}

function ConvertFrom-KeyValueResult {
    param([Parameter(Mandatory = $true)][string]$Contents)
    $values = @{}
    foreach ($line in ($Contents -split "`r?`n")) {
        $separator = $line.IndexOf('=')
        if ($separator -le 0) { continue }
        $values[$line.Substring(0, $separator)] = $line.Substring($separator + 1)
    }
    return $values
}

function Wait-AutoBuildResult {
    param(
        [Parameter(Mandatory = $true)][DateTime]$NotBeforeUtc,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $requestAccepted = $false
    $nextStatusAt = $script:statusIntervalSeconds
    while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if (-not $requestAccepted -and -not [IO.File]::Exists($script:autoRequestPath)) {
            $requestAccepted = $true
            Write-Status 'auto-build request accepted; waiting for the bounded result'
        }

        if ([IO.File]::Exists($script:autoResultPath)) {
            try {
                $resultInfo = [IO.FileInfo]::new($script:autoResultPath)
                if ($resultInfo.LastWriteTimeUtc -ge $NotBeforeUtc.AddSeconds(-1)) {
                    $contents = [IO.File]::ReadAllText(
                        $script:autoResultPath, [Text.Encoding]::UTF8)
                    $values = ConvertFrom-KeyValueResult -Contents $contents
                    if ($values.ContainsKey('success') -and $values.ContainsKey('message')) {
                        return $contents
                    }
                }
            }
            catch [IO.IOException] {
                # The hook may still be completing its single overwrite.
            }
        }

        if ($timer.Elapsed.TotalSeconds -ge $nextStatusAt) {
            $acceptedText = if ($requestAccepted) { 'yes' } else { 'no' }
            Write-Status ('auto-build waiting: {0}s, accepted={1}' -f
                [Math]::Floor($timer.Elapsed.TotalSeconds), $acceptedText)
            $nextStatusAt += $script:statusIntervalSeconds
        }
        Start-Sleep -Milliseconds $script:pollMilliseconds
    }
    throw "Auto-build result timed out after $TimeoutSeconds seconds."
}

function Wait-PartyReport {
    param(
        [Parameter(Mandatory = $true)][DateTime]$NotBeforeUtc,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $requestAccepted = $false
    $nextStatusAt = $script:statusIntervalSeconds
    while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if (-not $requestAccepted -and -not [IO.File]::Exists($script:partyRequestPath)) {
            $requestAccepted = $true
            Write-Status 'party-report request accepted; waiting for the snapshot'
        }

        if ([IO.File]::Exists($script:partyResultPath)) {
            try {
                $resultInfo = [IO.FileInfo]::new($script:partyResultPath)
                if ($resultInfo.LastWriteTimeUtc -ge $NotBeforeUtc.AddSeconds(-1)) {
                    $contents = [IO.File]::ReadAllText(
                        $script:partyResultPath, [Text.Encoding]::UTF8)
                    $report = $contents | ConvertFrom-Json -ErrorAction Stop
                    if ($null -ne $report.schemaVersion -and $null -ne $report.success) {
                        return $report
                    }
                }
            }
            catch [IO.IOException] {
                # The background writer may still have the report open.
            }
            catch [ArgumentException] {
                # A just-created JSON file can be visible before its final byte.
            }
        }

        if ($timer.Elapsed.TotalSeconds -ge $nextStatusAt) {
            $acceptedText = if ($requestAccepted) { 'yes' } else { 'no' }
            Write-Status ('party-report waiting: {0}s, accepted={1}' -f
                [Math]::Floor($timer.Elapsed.TotalSeconds), $acceptedText)
            $nextStatusAt += $script:statusIntervalSeconds
        }
        Start-Sleep -Milliseconds $script:pollMilliseconds
    }
    throw "Party-report result timed out after $TimeoutSeconds seconds."
}

$runMutex = [Threading.Mutex]::new(
    $false, 'Local\PathOfIdleInGameSearch-v1.1.8-runtime-runner')
$lockTaken = $false
try {
    try {
        $lockTaken = $runMutex.WaitOne(0)
    }
    catch [Threading.AbandonedMutexException] {
        $lockTaken = $true
    }
    if (-not $lockTaken) {
        throw 'Another v1.1.8 runtime Auto Build case is already running.'
    }

    $gameProcess = Get-Process -Name PathOfIdle -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $gameProcess) {
        throw 'PathOfIdle is not running.'
    }
    if ([IO.File]::Exists($autoRequestPath) -or [IO.File]::Exists($partyRequestPath)) {
        throw 'A runtime diagnostic request is already pending.'
    }

    [IO.File]::Delete($autoResultPath)
    [IO.File]::Delete($partyResultPath)

    $autoStartedUtc = [DateTime]::UtcNow
    Write-ExactUtf8 -Path $autoRequestPath -Contents $autoRequestContents
    Write-Status "auto-build requested: theme=$normalizedTheme, hero=$HeroId"
    $autoResultContents = Wait-AutoBuildResult `
        -NotBeforeUtc $autoStartedUtc `
        -TimeoutSeconds $autoBuildTimeoutSeconds
    Copy-Item -LiteralPath $autoResultPath -Destination $savedResultPath -Force

    $autoValues = ConvertFrom-KeyValueResult -Contents $autoResultContents
    $autoSucceeded = [string]::Equals(
        $autoValues['success'], 'true', [StringComparison]::OrdinalIgnoreCase)
    Write-Status ('auto-build result: success={0}, elapsed_ms={1}, max_gap_ms={2}' -f
        $autoSucceeded, $autoValues['elapsed_ms'], $autoValues['max_update_gap_ms'])

    if ([IO.File]::Exists($partyRequestPath)) {
        throw 'A party-report request appeared while the Auto Build case was running.'
    }
    [IO.File]::Delete($partyResultPath)
    $partyStartedUtc = [DateTime]::UtcNow
    Write-ExactUtf8 -Path $partyRequestPath -Contents $partyRequestContents
    Write-Status 'party report requested'
    $partyReport = Wait-PartyReport `
        -NotBeforeUtc $partyStartedUtc `
        -TimeoutSeconds $partyReportTimeoutSeconds
    Copy-Item -LiteralPath $partyResultPath -Destination $savedPartyReportPath -Force

    $partySucceeded = $partyReport.success -eq $true
    $targetHero = @($partyReport.heroes | Where-Object {
            $null -ne $_.uid -and [int]$_.uid -eq $HeroId
        } | Select-Object -First 1)
    if ($targetHero.Count -gt 0) {
        Write-Status ('party report: success={0}, heroes={1}, target_remaining={2}' -f
            $partySucceeded, $partyReport.actualHeroCount,
            $targetHero[0].talentRemainPoint)
    }
    else {
        Write-Status ('party report: success={0}, heroes={1}, target hero not found' -f
            $partySucceeded, $partyReport.actualHeroCount)
    }

    Write-Status "saved result: $savedResultPath"
    Write-Status "saved party report: $savedPartyReportPath"

    $rollbackVerified = $VerifyRollback -and -not $autoSucceeded -and
        $autoValues['message'].Contains('injected verification failure after skills') -and
        $autoValues['message'].Contains('original gear, skills, Blood, progress, and skill preferences restored and verified')
    if ($VerifyRollback -and -not $rollbackVerified) {
        throw "The injected post-skill failure did not verify an exact rollback. See $savedResultPath"
    }
    if (-not $autoSucceeded -and -not $rollbackVerified) {
        throw "Auto Build reported failure. See $savedResultPath"
    }
    if (-not $partySucceeded) {
        throw "Party report reported failure. See $savedPartyReportPath"
    }
    if ($targetHero.Count -eq 0) {
        throw "Hero $HeroId was not present in the party report."
    }
    if (-not $VerifyRollback -and [int]$targetHero[0].talentRemainPoint -ne 0) {
        throw "Hero $HeroId still has $($targetHero[0].talentRemainPoint) unspent talent points."
    }

    [pscustomobject]@{
        Theme = $normalizedTheme
        HeroId = $HeroId
        Success = $true
        RollbackVerified = [bool]$rollbackVerified
        ResultPath = $savedResultPath
        PartyReportPath = $savedPartyReportPath
    }
}
finally {
    Remove-OwnedRequest -Path $autoRequestPath -ExpectedContents $autoRequestContents
    Remove-OwnedRequest -Path $partyRequestPath -ExpectedContents $partyRequestContents
    if ($lockTaken) {
        try { [void]$runMutex.ReleaseMutex() } catch { }
    }
    $runMutex.Dispose()
}
