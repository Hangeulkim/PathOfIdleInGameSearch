[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$NotesFile)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$repo = 'Hangeulkim/PathOfIdleInGameSearch'
$safe = 'safe.directory=' + $root.Replace('\','/')
. (Join-Path $PSScriptRoot 'sha256.ps1')
$origin = & git -c $safe -C $root remote get-url origin
if ($LASTEXITCODE -or $origin -notin @("https://github.com/$repo.git","git@github.com:$repo.git")) { throw 'Wrong publication repository; refusing release.' }
$remoteJson = & gh repo view $repo --json nameWithOwner,isFork
if ($LASTEXITCODE) { throw 'Repository identity lookup failed' }
$remote = $remoteJson | ConvertFrom-Json
if ($remote.nameWithOwner -cne $repo -or $remote.isFork) { throw 'Expected standalone project, not a fork' }
if ((& git -c $safe -C $root status --porcelain)) { throw 'Commit source, payload and docs before release' }
$commit = & git -c $safe -C $root rev-parse HEAD
$remoteHead = & gh api "repos/$repo/commits/main" --jq .sha
if ($LASTEXITCODE -or $remoteHead -ne $commit) { throw 'Push the exact source commit to main before publishing' }
$version = [regex]::Match([IO.File]::ReadAllText((Join-Path $root 'source\Plugin.cs')), 'PluginVersion = "([0-9.]+)"').Groups[1].Value
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version' }
$artifacts = Join-Path $root 'artifacts'
$zip = Join-Path $artifacts "PathOfIdleInGameSearch-v$version.zip"
$dll = Join-Path $artifacts 'PathOfIdleInGameSearch.dll'
$sums = Join-Path $artifacts 'SHA256SUMS.txt'
foreach ($file in @($zip,$dll,$sums,$NotesFile)) { if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing release input: $file" } }
$sumText = [IO.File]::ReadAllText($sums)
foreach ($file in @($zip,$dll)) { if (-not $sumText.Contains("$(Get-ModSha256 $file)  $([IO.Path]::GetFileName($file))")) { throw 'Release asset checksum mismatch' } }
if ((Get-ModSha256 $dll) -ne (Get-ModSha256 (Join-Path $root 'payload\PathOfIdleInGameSearch.dll'))) { throw 'Asset does not match committed payload' }
& gh release create "v$version" $zip $dll $sums --repo $repo --target $commit --title "Path of Idle In-Game Search v$version" --notes-file $NotesFile --latest
if ($LASTEXITCODE) { throw 'Release publication failed; inspect before retrying' }
