[CmdletBinding()]
param([string]$RuntimeArchivePath, [string]$RuntimeSourcePath, [string]$RuntimeNoticesPath)
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
if (-not $RuntimeArchivePath) { $RuntimeArchivePath = Join-Path $root 'payload\BepInEx-runtime.zip' }
if (-not $RuntimeSourcePath) { $RuntimeSourcePath = Join-Path $root 'payload\BepInEx-source.zip' }
if (-not $RuntimeNoticesPath) { $RuntimeNoticesPath = Join-Path $root 'payload\BepInEx-notices.zip' }
if (-not (Test-Path -LiteralPath $RuntimeArchivePath -PathType Leaf) -or (Get-ModSha256 $RuntimeArchivePath) -ne '9753B825578A3C3A31CC10067CD45A44A7BF56D3C34C4679E24D6ADFD0FBA8EA') { throw 'Supply the exact pinned official runtime with -RuntimeArchivePath; see THIRD-PARTY.md.' }
if (-not (Test-Path -LiteralPath $RuntimeSourcePath -PathType Leaf) -or (Get-ModSha256 $RuntimeSourcePath) -ne '83292B82805F86904150E4CE4EBBF76B80EBE9F228C9BAC95DA85AA1BD1D1E4D') { throw 'Supply the matching unmodified BepInEx source archive with -RuntimeSourcePath; see THIRD-PARTY.md.' }
if (-not (Test-Path -LiteralPath $RuntimeNoticesPath -PathType Leaf) -or (Get-ModSha256 $RuntimeNoticesPath) -ne '67137DBDB57D6BEE42C92FCB8F05E4B92FDA6BEA59846BC30931BFB07308C62A') { throw 'Supply the matching dependency notices/source bundle with -RuntimeNoticesPath; see THIRD-PARTY.md.' }
$bytes = [IO.File]::ReadAllBytes($dll)
$text = [Text.Encoding]::UTF8.GetString($bytes) + [Text.Encoding]::Unicode.GetString($bytes)
foreach ($marker in @('RuntimeJointBuildTestHook','RuntimeAutoBuildPreviewHook','RuntimePartyReportHook','RuntimeRejectJointBuildAfterSkills')) {
    if ($text.Contains($marker)) { throw "Diagnostic-enabled DLL: $marker" }
}
$output = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$zipPath = Join-Path $output "PathOfIdleInGameSearch-v$version.zip"
if (Test-Path -LiteralPath $zipPath) { throw 'Archive exists; use a fresh version or inspect the existing artifact.' }
$files = @('.gitattributes','install.bat','uninstall.bat','README.md','README.ko.md','README.zh-CN.md','README.zh-TW.md','GUIDE.md','CHANGELOG.md','RELEASING.md','LICENSE','THIRD-PARTY.md','payload\BepInEx.cfg','payload\PathOfIdleInGameSearch.dll')
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
        if ([IO.Path]::GetExtension($file) -ieq '.bat') {
            # Git archives can contain LF even when the checkout uses CRLF.
            # CMD entrypoints must be ASCII with Windows line endings in the ZIP.
            $launcherBytes = [IO.File]::ReadAllBytes($source)
            if (@($launcherBytes | Where-Object { $_ -gt 127 -or $_ -eq 0 }).Count) {
                throw "Batch launcher must be ASCII without a BOM: $file"
            }
            $launcherText = [Text.Encoding]::ASCII.GetString($launcherBytes)
            $launcherText = [regex]::Replace($launcherText, '\r\n|\r|\n', "`r`n")
            $entry = $zip.CreateEntry($file.Replace('\','/'), [IO.Compression.CompressionLevel]::Optimal)
            $writer = [IO.StreamWriter]::new($entry.Open(), [Text.Encoding]::ASCII)
            try { $writer.Write($launcherText) } finally { $writer.Dispose() }
        } else {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $source, $file.Replace('\','/'), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Resolve-Path -LiteralPath $RuntimeArchivePath).Path, 'payload/BepInEx-runtime.zip', [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Resolve-Path -LiteralPath $RuntimeSourcePath).Path, 'payload/BepInEx-source.zip', [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Resolve-Path -LiteralPath $RuntimeNoticesPath).Path, 'payload/BepInEx-notices.zip', [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    $runtimeSource = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $RuntimeSourcePath).Path)
    try {
        $license = $runtimeSource.GetEntry('BepInEx-a1afbfb169610ffbc1813d83e946c9e2cc0d2d63/LICENSE')
        if (-not $license) { throw 'BepInEx source license is missing.' }
        $inputLicense = $license.Open()
        $outputLicense = $zip.CreateEntry('payload/BepInEx-LICENSE.txt').Open()
        try { $inputLicense.CopyTo($outputLicense) } finally { $inputLicense.Dispose(); $outputLicense.Dispose() }
    } finally { $runtimeSource.Dispose() }
} finally { $zip.Dispose() }
Copy-Item -LiteralPath $dll -Destination (Join-Path $output 'PathOfIdleInGameSearch.dll') -Force
$sums = "$(Get-ModSha256 $zipPath)  $([IO.Path]::GetFileName($zipPath))`n$hash  PathOfIdleInGameSearch.dll`n"
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), $sums, [Text.UTF8Encoding]::new($false))
Write-Output $sums
