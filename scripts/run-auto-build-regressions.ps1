[CmdletBinding()]
param(
    [switch]$Record,

    [switch]$InvariantOnly,

    [string]$OutputDirectory = 'runtime-regression',

    [string[]]$CaseName = @(),

    [ValidateRange(1, 5)]
    [int]$Repeat = 1,

    [string]$ManifestPath = ''
)

$ErrorActionPreference = 'Stop'
if ($Record -and $InvariantOnly) { throw 'Record and InvariantOnly are mutually exclusive.' }
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$ManifestPath = if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    Join-Path $PSScriptRoot 'auto-build-regressions.json'
}
else { $ManifestPath }
$resolvedManifest = [IO.Path]::GetFullPath($ManifestPath)
if (-not [IO.File]::Exists($resolvedManifest)) {
    throw "Regression manifest is missing: $resolvedManifest"
}
$manifest = [IO.File]::ReadAllText(
    $resolvedManifest, [Text.Encoding]::UTF8) | ConvertFrom-Json
if ($manifest.schemaVersion -ne 'PathOfIdleInGameSearch.auto-build-regressions.v2') {
    throw "Unsupported regression manifest schema: $($manifest.schemaVersion)"
}

$normalizedCaseNames = @($CaseName | ForEach-Object {
        $_ -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    })
$selectedCases = @($manifest.cases | Where-Object {
        $normalizedCaseNames.Count -eq 0 -or $normalizedCaseNames -contains $_.name
    })
if ($selectedCases.Count -eq 0) {
    throw 'No regression cases matched CaseName.'
}

function Get-ComparableSignature {
    param([Parameter(Mandatory = $true)][object]$Signature)
    return [ordered]@{
        focus = [string]$Signature.focus
        gearDefinitionIds = @($Signature.gearDefinitionIds | ForEach-Object { [int]$_ })
        baseSkillId = [int]$Signature.baseSkillId
        activeSkillIds = @($Signature.activeSkillIds | ForEach-Object { [int]$_ })
        activeSkillSetEffects = [string]$Signature.activeSkillSetEffects
        paidTalentPoints = [int]$Signature.paidTalentPoints
        talentPointBudget = [int]$Signature.talentPointBudget
        unspentTalentPoints = [int]$Signature.unspentTalentPoints
        targetSavedLevels = @($Signature.targetSavedLevels | ForEach-Object { [string]$_ })
    }
}

function ConvertTo-CanonicalJson {
    param([Parameter(Mandatory = $true)][object]$Value)
    return $Value | ConvertTo-Json -Depth 8 -Compress
}

$workspaceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$previewScript = Join-Path $PSScriptRoot 'run-runtime-auto-build-preview.ps1'
$failures = [Collections.Generic.List[string]]::new()
$results = [Collections.Generic.List[object]]::new()

foreach ($case in $selectedCases) {
    $recordedThisRun = $null
    for ($iteration = 1; $iteration -le $Repeat; $iteration++) {
        Write-Host ('[{0:HH:mm:ss}] regression {1} ({2}/{3})' -f
            [DateTime]::Now, $case.name, $iteration, $Repeat)
        $prefix = Join-Path (Join-Path $workspaceRoot $OutputDirectory) $case.name
        try {
            & $previewScript `
                -Theme ([string]$case.theme) `
                -HeroId ([int]$case.hero) `
                -OutputPrefix $prefix `
                -Seconds ([int]$case.seconds) `
                -Priority ([int]$case.priority) `
                -IncludeForgeBonuses ([bool]$case.includeForgeBonuses)
            $signaturePath = "$prefix.signature.json"
            $signature = [IO.File]::ReadAllText(
                $signaturePath, [Text.Encoding]::UTF8) | ConvertFrom-Json
            $comparable = Get-ComparableSignature $signature
            if ($case.PSObject.Properties.Name -contains 'requiredActiveSkillIds') {
                foreach ($requiredSkillId in $case.requiredActiveSkillIds) {
                    if ($comparable.activeSkillIds -notcontains [int]$requiredSkillId) {
                        throw "Required native fixture skill $requiredSkillId is missing."
                    }
                }
            }
            if ($case.PSObject.Properties.Name -contains 'requiredSavedTalentIds') {
                foreach ($requiredTalentId in $case.requiredSavedTalentIds) {
                    $savedPattern = '^' + [regex]::Escape([string]$requiredTalentId) + ':[1-9][0-9]*$'
                    if (@($comparable.targetSavedLevels | Where-Object { $_ -match $savedPattern }).Count -ne 1) {
                        throw "Required native fixture talent $requiredTalentId has no positive saved target."
                    }
                }
            }
            $canonical = ConvertTo-CanonicalJson $comparable

            if ($null -eq $recordedThisRun) {
                $recordedThisRun = $comparable
            }
            elseif ($canonical -ne (ConvertTo-CanonicalJson $recordedThisRun)) {
                $failures.Add(
                    "$($case.name): repeat $iteration differed from repeat 1")
            }

            if (-not $Record -and -not $InvariantOnly) {
                if ($null -eq $case.expected) {
                    $failures.Add(
                        "$($case.name): no expected signature is recorded; run with -Record")
                }
                elseif ($canonical -ne (ConvertTo-CanonicalJson (
                            Get-ComparableSignature $case.expected))) {
                    $expectedComparable = Get-ComparableSignature $case.expected
                    $differences = @($comparable.PSObject.Properties.Name | ForEach-Object {
                            $propertyName = $_
                            $expectedValue = ConvertTo-CanonicalJson $expectedComparable.$propertyName
                            $actualValue = ConvertTo-CanonicalJson $comparable.$propertyName
                            if ($expectedValue -ne $actualValue) {
                                "${propertyName}: expected=$expectedValue actual=$actualValue"
                            }
                        })
                    $failures.Add(
                        "$($case.name): actual signature differs from the saved baseline: $($differences -join '; ')")
                }
            }

            $results.Add([pscustomobject]@{
                    Case = $case.name
                    Iteration = $iteration
                    Success = $true
                    Focus = $comparable.focus
                    Gear = ($comparable.gearDefinitionIds -join ',')
                    Base = $comparable.baseSkillId
                    Active = ($comparable.activeSkillIds -join ',')
                    Points = "$($comparable.paidTalentPoints)/$($comparable.talentPointBudget)/$($comparable.unspentTalentPoints)"
                    Sets = $comparable.activeSkillSetEffects
                })
        }
        catch {
            $failures.Add(
                "$($case.name) iteration ${iteration}: $($_.Exception.GetBaseException().Message)")
            $results.Add([pscustomobject]@{
                    Case = $case.name
                    Iteration = $iteration
                    Success = $false
                    Focus = ''
                    Gear = ''
                    Base = 0
                    Active = ''
                    Points = ''
                    Sets = ''
                })
        }
    }
    if ($Record -and $null -ne $recordedThisRun) {
        $case.expected = [pscustomobject]$recordedThisRun
    }
}

if ($Record) {
    [IO.File]::WriteAllText(
        $resolvedManifest,
        ($manifest | ConvertTo-Json -Depth 12),
        $utf8NoBom)
    Write-Host "Recorded regression baselines: $resolvedManifest"
}

$results | Format-Table -AutoSize
if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Error $failure -ErrorAction Continue
    }
    throw "$($failures.Count) Auto Build regression check(s) failed."
}
Write-Host "Auto Build regressions passed: $($results.Count) read-only run(s)."
if ($InvariantOnly) {
    Write-Host 'Live-state invariants verified; exact comparison with the older save baseline was explicitly skipped.'
}
