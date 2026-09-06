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

    [ValidateRange(0, [int]::MaxValue)]
    [int]$Seconds = 60,

    [ValidateRange(0, 100)]
    [int]$Priority = 50,

    [bool]$IncludeForgeBonuses = $true,

    [ValidateRange(30, 600)]
    [int]$TimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$pollMilliseconds = 250
$statusIntervalSeconds = 15
$tempRoot = [IO.Path]::GetTempPath()
$requestPath = [IO.Path]::Combine(
    $tempRoot, 'PathOfIdleInGameSearch-v1.1.8-auto-preview.request')
$resultPath = [IO.Path]::Combine(
    $tempRoot, 'PathOfIdleInGameSearch-v1.1.8-auto-preview.result')
$progressPath = [IO.Path]::Combine(
    $tempRoot, 'PathOfIdleInGameSearch-v1.1.8-auto-preview.progress')

$normalizedTheme = $Theme.Trim().ToLowerInvariant()
if ($normalizedTheme -match '[|\r\n]') {
    throw 'Theme must not contain a pipe or newline.'
}
$supportedThemes = @(
    'auto', 'physical', 'elemental', 'fire', 'ice', 'lightning',
    'minion', 'bleed', 'corrosion', 'crit', 'support', 'defense'
)
if ($supportedThemes -notcontains $normalizedTheme -and
    $normalizedTheme -notmatch '^damage:[1-9][0-9]*$') {
    throw "Unsupported theme '$normalizedTheme'."
}

$workspaceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$resolvedPrefix = if ([IO.Path]::IsPathRooted($OutputPrefix)) {
    [IO.Path]::GetFullPath($OutputPrefix)
}
else {
    [IO.Path]::GetFullPath([IO.Path]::Combine($workspaceRoot, $OutputPrefix))
}
$workspaceBoundary = $workspaceRoot + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedPrefix.StartsWith(
        $workspaceBoundary, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPrefix must resolve inside the workspace: $resolvedPrefix"
}
$outputDirectory = [IO.Path]::GetDirectoryName($resolvedPrefix)
if (-not [IO.Directory]::Exists($outputDirectory)) {
    [void][IO.Directory]::CreateDirectory($outputDirectory)
}
$savedResultPath = "$resolvedPrefix.result.txt"
$savedSignaturePath = "$resolvedPrefix.signature.json"
$forgeText = $IncludeForgeBonuses.ToString().ToLowerInvariant()
$requestContents = "PREVIEW AUTO BUILD v1.1.8|theme=$normalizedTheme|hero=$HeroId|seconds=$Seconds|priority=$Priority|forge=$forgeText"

function Write-Status {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host ('[{0:HH:mm:ss}] {1}' -f [DateTime]::Now, $Message)
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

function Remove-OwnedRequest {
    try {
        if (-not [IO.File]::Exists($script:requestPath)) { return }
        $actual = [IO.File]::ReadAllText($script:requestPath, [Text.Encoding]::UTF8).Trim()
        if ([string]::Equals(
                $actual, $script:requestContents, [StringComparison]::Ordinal)) {
            [IO.File]::Delete($script:requestPath)
        }
    }
    catch {
        # Cleanup is best effort; preserve the original verification failure.
    }
}

function Get-StableSignature {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Values,
        [Parameter(Mandatory = $true)][string]$Message
    )
    $focusMatch = [regex]::Match($Message, 'focus=(.*?) \u00B7 score=')
    $currentMatch = [regex]::Match($Message, ' \u00B7 current=(true|false) \u00B7 ')
    $gearMatch = [regex]::Match($Message, ' \u00B7 gear=(.*?) \u00B7 skills=')
    $baseMatch = [regex]::Match($Message, ' \u00B7 skills=base=.*?#([0-9]+); active=')
    $activeMatch = [regex]::Match($Message, '; active=(.*?); variant=')
    $skillSetsMatch = [regex]::Match($Message, '; sets=(.*?) \u00B7 points=')
    $pointsMatch = [regex]::Match(
        $Message, ' \u00B7 points=([0-9]+)/([0-9]+)/(-?[0-9]+) \u00B7 levels=')
    $levelsMatch = [regex]::Match($Message, ' \u00B7 levels=(.*?) \u00B7 sets=')
    if (-not $focusMatch.Success -or -not $currentMatch.Success -or
        -not $gearMatch.Success -or -not $baseMatch.Success -or
        -not $activeMatch.Success -or -not $skillSetsMatch.Success -or
        -not $pointsMatch.Success -or -not $levelsMatch.Success) {
        throw 'The read-only result did not contain a complete stable build signature.'
    }
    $gearIds = @([regex]::Matches($gearMatch.Groups[1].Value, '#([0-9]+)') |
        ForEach-Object { [int]$_.Groups[1].Value })
    $activeSkillIds = @([regex]::Matches($activeMatch.Groups[1].Value, '#([0-9]+)') |
        ForEach-Object { [int]$_.Groups[1].Value })
    # Native owned Alien rows and gear-granted skills may extend the package;
    # base-only plans may also be valid. The game-side preflight verifies every
    # learned skill's positive saved target before returning this preview.
    if ($gearIds.Count -ne 8 -or
        @($activeSkillIds | Select-Object -Unique).Count -ne $activeSkillIds.Count) {
        throw 'Expected 8 gear IDs and a duplicate-free native active skill package.'
    }
    $paidTalentPoints = [int]$pointsMatch.Groups[1].Value
    $talentPointBudget = [int]$pointsMatch.Groups[2].Value
    $unspentTalentPoints = [int]$pointsMatch.Groups[3].Value
    if ($unspentTalentPoints -ne 0 -or $paidTalentPoints -ne $talentPointBudget) {
        throw "The planned talent budget is incomplete: $paidTalentPoints/$talentPointBudget, $unspentTalentPoints unspent."
    }
    $levelText = $levelsMatch.Groups[1].Value.Trim()
    $targetSavedLevels = @([regex]::Matches($levelText, '([0-9]+):([0-9]+)') |
        ForEach-Object { '{0}:{1}' -f $_.Groups[1].Value, $_.Groups[2].Value })
    if ($targetSavedLevels.Count -eq 0 -or ($targetSavedLevels -join ',') -ne $levelText) {
        throw 'The planned talent level vector is missing or malformed.'
    }
    return [ordered]@{
        hero = [int]$Values['hero']
        theme = $Values['theme']
        priority = [int]$Values['theme_priority']
        includeForgeBonuses = [bool]::Parse($Values['include_forge_bonuses'])
        focus = $focusMatch.Groups[1].Value.Trim()
        current = [bool]::Parse($currentMatch.Groups[1].Value)
        gearDefinitionIds = $gearIds
        baseSkillId = [int]$baseMatch.Groups[1].Value
        activeSkillIds = $activeSkillIds
        activeSkillSetEffects = $skillSetsMatch.Groups[1].Value.Trim()
        paidTalentPoints = $paidTalentPoints
        talentPointBudget = $talentPointBudget
        unspentTalentPoints = $unspentTalentPoints
        targetSavedLevels = $targetSavedLevels
    }
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
        throw 'Another v1.1.8 runtime verification is already running.'
    }
    if ($null -eq (Get-Process -Name PathOfIdle -ErrorAction SilentlyContinue |
            Select-Object -First 1)) {
        throw 'PathOfIdle is not running.'
    }
    if ([IO.File]::Exists($requestPath)) {
        throw 'A read-only Auto Build preview request is already pending.'
    }

    [IO.File]::Delete($resultPath)
    [IO.File]::Delete($progressPath)
    $startedUtc = [DateTime]::UtcNow
    [IO.File]::WriteAllText($requestPath, $requestContents, $utf8NoBom)
    Write-Status "read-only Auto Build requested: theme=$normalizedTheme, hero=$HeroId"

    $timer = [Diagnostics.Stopwatch]::StartNew()
    $nextStatusAt = $statusIntervalSeconds
    $requestAccepted = $false
    $resultContents = $null
    while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if (-not $requestAccepted -and -not [IO.File]::Exists($requestPath)) {
            $requestAccepted = $true
            Write-Status 'request accepted; waiting for the read-only result'
        }
        if ([IO.File]::Exists($resultPath)) {
            try {
                $resultInfo = [IO.FileInfo]::new($resultPath)
                if ($resultInfo.LastWriteTimeUtc -ge $startedUtc.AddSeconds(-1)) {
                    $candidateContents = [IO.File]::ReadAllText(
                        $resultPath, [Text.Encoding]::UTF8)
                    $candidateValues = ConvertFrom-KeyValueResult $candidateContents
                    if ($candidateValues.ContainsKey('success') -and
                        $candidateValues.ContainsKey('message')) {
                        $resultContents = $candidateContents
                        break
                    }
                }
            }
            catch [IO.IOException] {
                # The hook may still be completing its single overwrite.
            }
        }
        if ($timer.Elapsed.TotalSeconds -ge $nextStatusAt) {
            $progress = if ([IO.File]::Exists($progressPath)) {
                try {
                    ([IO.File]::ReadAllText($progressPath, [Text.Encoding]::UTF8) `
                        -replace "`r?`n", ', ').Trim(', ')
                }
                catch { 'progress file is being updated' }
            }
            else { 'waiting for progress' }
            Write-Status $progress
            $nextStatusAt += $statusIntervalSeconds
        }
        Start-Sleep -Milliseconds $pollMilliseconds
    }
    if ($null -eq $resultContents) {
        throw "Read-only Auto Build preview timed out after $TimeoutSeconds seconds."
    }

    $values = ConvertFrom-KeyValueResult $resultContents
    # Keep failed evidence too, including plans that the signature validator
    # rejects. Previously the most useful reproductions were discarded.
    [IO.File]::WriteAllText($savedResultPath, $resultContents, $utf8NoBom)
    foreach ($required in @(
            'success', 'planning_succeeded', 'state_unchanged',
            'selection_restored', 'before_state', 'after_state', 'message')) {
        if (-not $values.ContainsKey($required)) {
            throw "Read-only result is missing '$required'."
        }
    }
    if ($values['success'] -ne 'true' -or
        $values['planning_succeeded'] -ne 'true' -or
        $values['state_unchanged'] -ne 'true' -or
        $values['selection_restored'] -ne 'true' -or
        $values['before_state'] -ne $values['after_state']) {
        throw "Read-only invariants failed. See $savedResultPath"
    }

    $signature = Get-StableSignature -Values $values -Message $values['message']
    [IO.File]::WriteAllText($savedResultPath, $resultContents, $utf8NoBom)
    [IO.File]::WriteAllText(
        $savedSignaturePath,
        ($signature | ConvertTo-Json -Depth 8),
        $utf8NoBom)
    Write-Status "saved result: $savedResultPath"
    Write-Status "saved signature: $savedSignaturePath"
    [pscustomobject]$signature
}
finally {
    Remove-OwnedRequest
    if ($lockTaken) {
        try { [void]$runMutex.ReleaseMutex() } catch { }
    }
    $runMutex.Dispose()
}
