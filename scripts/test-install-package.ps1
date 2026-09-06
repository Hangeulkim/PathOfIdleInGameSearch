[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'sha256.ps1')
if (Get-Process -Name PathOfIdle -ErrorAction SilentlyContinue) { throw 'Close the game before isolated installer tests.' }
$taskDir = Join-Path ([IO.Path]::GetTempPath()) ('PathOfIdle-package-test-' + [Guid]::NewGuid().ToString('N'))
$extract = Join-Path $taskDir 'package with spaces'
$game = Join-Path $taskDir 'game with spaces'
$tampered = Join-Path $taskDir 'tampered package'
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
New-Item -ItemType Directory -Path $taskDir | Out-Null
[IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -LiteralPath $PackagePath).Path, $extract)
$dll = Join-Path $extract 'payload\PathOfIdleInGameSearch.dll'
$hash = Get-ModSha256 $dll
$tree = @(Get-ChildItem -LiteralPath $extract -Recurse -File)
if ($tree | Where-Object { $_.Extension -in '.pdb','.es3' -or $_.Name -eq 'GameAssembly.dll' -or $_.FullName -match '\\(?:bin|obj)\\' }) { throw 'Unexpected private/debug/game package file' }
$core = Join-Path $game 'BepInEx\core'
$config = Join-Path $game 'BepInEx\config'
New-Item -ItemType Directory -Path $core,$config,(Join-Path $game 'PathOfIdle_Data') -Force | Out-Null
foreach ($file in @('PathOfIdle.exe','GameAssembly.dll','winhttp.dll','BepInEx\core\BepInEx.Unity.IL2CPP.dll')) {
    [IO.File]::WriteAllBytes((Join-Path $game $file), [byte[]]@(0))
}
$userConfig = Join-Path $config 'local.pathofidle.ingame-search.cfg'
$runtimeConfig = Join-Path $config 'BepInEx.cfg'
[IO.File]::WriteAllText($userConfig, 'SENTINEL USER SETTINGS')
[IO.File]::WriteAllText($runtimeConfig, 'SENTINEL RUNTIME SETTINGS')
$userHash = Get-ModSha256 $userConfig
$runtimeHash = Get-ModSha256 $runtimeConfig
$installScript = Join-Path $extract 'scripts\install.ps1'
$normal = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installScript -GameDirectory $game 2>&1
if ($LASTEXITCODE) { throw "Existing-runtime install failed: $normal" }
$installed = Join-Path $game 'BepInEx\plugins\PathOfIdleInGameSearch.dll'
if ((Get-ModSha256 $installed) -ne $hash -or (Get-ModSha256 $userConfig) -ne $userHash -or (Get-ModSha256 $runtimeConfig) -ne $runtimeHash) { throw 'Install content/settings mismatch' }
Copy-Item -LiteralPath $extract -Destination $tampered -Recurse
[IO.File]::WriteAllBytes((Join-Path $tampered 'payload\PathOfIdleInGameSearch.dll'), [byte[]]@(1,2,3))
$ErrorActionPreference = 'Continue'
$tamperOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $tampered 'scripts\install.ps1') -GameDirectory $game 2>&1
$tamperExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($tamperExit -eq 0 -or (Get-ModSha256 $installed) -ne $hash) { throw 'Tampered payload was not rejected safely' }
Remove-Item -LiteralPath (Join-Path $core 'BepInEx.Unity.IL2CPP.dll') -Force
$ErrorActionPreference = 'Continue'
$partialOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installScript -GameDirectory $game 2>&1
$partialExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($partialExit -eq 0 -or (Get-ModSha256 $installed) -ne $hash) { throw 'Partial runtime was not rejected safely' }
Write-Output ('PASS extracted ZIP, existing BepInEx, settings preservation, tampered payload rejection, partial BepInEx rejection. DLL SHA256=' + $hash)
$resolved = [IO.Path]::GetFullPath($taskDir)
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
if (-not $resolved.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $resolved) -notlike 'PathOfIdle-package-test-*') { throw 'Unsafe test cleanup target' }
Remove-Item -LiteralPath $resolved -Recurse -Force
