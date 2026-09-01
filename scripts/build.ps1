[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProjectDirectory = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'find-game.ps1')
$GameDirectory = Find-PathOfIdleGameDirectory
$InteropDirectory = Join-Path $GameDirectory 'BepInEx\interop'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or -not (& dotnet --list-sdks)) {
    throw '.NET 6 SDK 이상이 필요합니다.'
}
if (-not (Test-Path -LiteralPath (Join-Path $InteropDirectory 'UnityEngine.CoreModule.dll') -PathType Leaf)) {
    throw 'BepInEx를 설치하고 게임을 한 번 실행한 뒤 종료하세요.'
}

& dotnet build (Join-Path $ProjectDirectory 'source\PathOfIdleInGameSearch.csproj') `
    --configuration Release `
    -p:GameDirectory="$GameDirectory"
if ($LASTEXITCODE -ne 0) { throw '빌드에 실패했습니다.' }

$BuiltDll = Join-Path $ProjectDirectory 'source\bin\Release\net6.0\PathOfIdleInGameSearch.dll'
$PayloadDll = Join-Path $ProjectDirectory 'payload\PathOfIdleInGameSearch.dll'
Copy-Item -LiteralPath $BuiltDll -Destination $PayloadDll -Force
$PluginHash = (Get-FileHash -LiteralPath $PayloadDll -Algorithm SHA256).Hash
$InstallScript = Join-Path $PSScriptRoot 'install.ps1'
$InstallText = [IO.File]::ReadAllText($InstallScript)
$UpdatedInstallText = [regex]::Replace($InstallText, "(?m)^\`$PluginSha256 = '[A-F0-9]{64}'\s*$", "`$PluginSha256 = '$PluginHash'")
if ($UpdatedInstallText -eq $InstallText) { throw 'install.ps1의 DLL 해시 항목을 갱신하지 못했습니다.' }
[IO.File]::WriteAllText($InstallScript, $UpdatedInstallText, [Text.UTF8Encoding]::new($true))
Write-Host "빌드 완료: $BuiltDll"
Write-Host "설치 검증 해시 갱신: $PluginHash"
