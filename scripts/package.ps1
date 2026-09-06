[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'sha256.ps1')
$pluginText = [IO.File]::ReadAllText((Join-Path $root 'source\Plugin.cs'))
$version = [regex]::Match($pluginText, 'PluginVersion = "([0-9.]+)"').Groups[1].Value
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid source version' }
$dll = Join-Path $root 'payload\PathOfIdleInGameSearch.dll'
$hash = Get-ModSha256 $dll
$install = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'install.ps1'))
if (-not $install.Contains("`$PluginSha256 = '$hash'")) { throw 'Installer/payload hash mismatch; rebuild first' }
$bytes = [IO.File]::ReadAllBytes($dll)
$text = [Text.Encoding]::UTF8.GetString($bytes) + [Text.Encoding]::Unicode.GetString($bytes)
foreach ($marker in @('RuntimeJointBuildTestHook','RuntimeAutoBuildPreviewHook','RuntimePartyReportHook','RuntimeRejectJointBuildAfterSkills')) {
    if ($text.Contains($marker)) { throw "Diagnostic-enabled DLL: $marker" }
}
$output = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$zipPath = Join-Path $output "PathOfIdleInGameSearch-v$version.zip"
if (Test-Path -LiteralPath $zipPath) { throw 'Archive exists; use a fresh version or inspect the existing artifact.' }
$files = @('install.bat','uninstall.bat','README.md','README.ko.md','README.zh-CN.md','README.zh-TW.md','GUIDE.md','CHANGELOG.md','RELEASING.md','LICENSE','THIRD-PARTY.md','payload\BepInEx.cfg','payload\PathOfIdleInGameSearch.dll')
$files += Get-ChildItem -LiteralPath (Join-Path $root 'source') -File | Where-Object Extension -in '.cs','.csproj' | ForEach-Object { 'source\' + $_.Name }
$files += Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object Extension -in '.ps1','.json','.md' | ForEach-Object { 'scripts\' + $_.Name }
$files += @('scripts\EffectScorerAudit\EffectScorerAudit.csproj','scripts\EffectScorerAudit\Program.cs','scripts\regression-receipts\2026-09-06-healer-equip.json')
$files += @('docs\images\v1.1.9-search.jpg','docs\images\v1.1.9-auto-build-theme.jpg','docs\images\v1.1.9-auto-build.jpg','docs\images\v1.1.9-bulk-open.jpg','docs\images\v1.1.9-forge.jpg')
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in ($files | Sort-Object -Unique)) {
        $source = Join-Path $root $file
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Package file missing: $file" }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $source, $file.Replace('\','/'), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose() }
Copy-Item -LiteralPath $dll -Destination (Join-Path $output 'PathOfIdleInGameSearch.dll') -Force
$sums = "$(Get-ModSha256 $zipPath)  $([IO.Path]::GetFileName($zipPath))`n$hash  PathOfIdleInGameSearch.dll`n"
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), $sums, [Text.UTF8Encoding]::new($false))
Write-Output $sums
