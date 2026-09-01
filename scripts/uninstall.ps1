[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'find-game.ps1')
$GameDirectory = Find-PathOfIdleGameDirectory
$PluginDestination = Join-Path $GameDirectory 'BepInEx\plugins\PathOfIdleInGameSearch.dll'

if (Get-Process -Name PathOfIdle -ErrorAction SilentlyContinue) {
    throw 'Path of Idle이 실행 중입니다. 게임을 완전히 종료한 뒤 다시 실행하세요.'
}
if (Test-Path -LiteralPath $PluginDestination -PathType Leaf) {
    Remove-Item -LiteralPath $PluginDestination -Force
    Write-Host '모드 DLL을 제거했습니다. 검색 설정 파일과 다른 모드는 그대로 유지했습니다.'
}
else {
    Write-Host '설치된 모드 DLL이 없습니다.'
}

