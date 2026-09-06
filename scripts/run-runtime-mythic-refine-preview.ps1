param(
    [int]$TimeoutSeconds = 120,
    [switch]$VerifyChangedCandidateGuard
)

$ErrorActionPreference = 'Stop'
$tempRoot = [System.IO.Path]::GetTempPath()
$requestPath = Join-Path $tempRoot 'PathOfIdleInGameSearch-v1.1.8-mythic-preview.request'
$resultPath = Join-Path $tempRoot 'PathOfIdleInGameSearch-v1.1.8-mythic-preview.result'
$progressPath = Join-Path $tempRoot 'PathOfIdleInGameSearch-v1.1.8-mythic-preview.progress'

foreach ($path in @($requestPath, $resultPath, $progressPath)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
    }
}

$request = 'PREVIEW MYTHIC REFINE BATCH v1.1.8|hero=none'
if ($VerifyChangedCandidateGuard) {
    $request += '|guard=changed-candidate-set'
}

[System.IO.File]::WriteAllText(
    $requestPath,
    $request,
    [System.Text.UTF8Encoding]::new($false))

$deadline = [DateTime]::UtcNow.AddSeconds([Math]::Max(1, $TimeoutSeconds))
while ([DateTime]::UtcNow -lt $deadline) {
    if (Test-Path -LiteralPath $resultPath) {
        Get-Content -LiteralPath $resultPath -Encoding UTF8 -Raw
        exit 0
    }
    Start-Sleep -Milliseconds 200
}

if (Test-Path -LiteralPath $progressPath) {
    Get-Content -LiteralPath $progressPath -Encoding UTF8 -Raw
}
throw "Timed out waiting for the read-only Mythic refinement preview receipt."
