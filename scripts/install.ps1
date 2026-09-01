[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$ProjectDirectory = Split-Path -Parent $PSScriptRoot
$PluginSource = Join-Path $ProjectDirectory 'payload\PathOfIdleInGameSearch.dll'
$BepInExConfigSource = Join-Path $ProjectDirectory 'payload\BepInEx.cfg'
$BepInExUrl = 'https://builds.bepinex.dev/projects/bepinex_be/760/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.760%2Ba1afbfb.zip'
$BepInExSha256 = '9753B825578A3C3A31CC10067CD45A44A7BF56D3C34C4679E24D6ADFD0FBA8EA'
$PluginSha256 = '376DC09CE21EAC845B6E831644CCDAEEFAF5FA14E97D51FC0B08416F3F7381B9'
. (Join-Path $PSScriptRoot 'find-game.ps1')
$GameDirectory = Find-PathOfIdleGameDirectory
$GameExecutable = Join-Path $GameDirectory 'PathOfIdle.exe'
$PluginDestination = Join-Path $GameDirectory 'BepInEx\plugins\PathOfIdleInGameSearch.dll'
$BepInExCore = Join-Path $GameDirectory 'BepInEx\core\BepInEx.Unity.IL2CPP.dll'
$DoorstopLoader = Join-Path $GameDirectory 'winhttp.dll'

if (-not (Test-Path -LiteralPath $PluginSource -PathType Leaf)) {
    throw '배포 파일에 모드 DLL이 없습니다. ZIP을 완전히 푼 뒤 다시 실행하세요.'
}
if ((Get-FileHash -LiteralPath $PluginSource -Algorithm SHA256).Hash -ne $PluginSha256) {
    throw '모드 DLL 무결성 검사에 실패했습니다. 릴리스 ZIP을 다시 받아 주세요.'
}
if (Get-Process -Name PathOfIdle -ErrorAction SilentlyContinue) {
    throw 'Path of Idle이 실행 중입니다. 게임을 완전히 종료한 뒤 다시 실행하세요.'
}

$HasCore = Test-Path -LiteralPath $BepInExCore -PathType Leaf
$HasLoader = Test-Path -LiteralPath $DoorstopLoader -PathType Leaf
if ($HasCore -xor $HasLoader) {
    throw '불완전한 BepInEx 설치가 발견되어 안전을 위해 중단했습니다.'
}

if (-not $HasCore) {
    foreach ($unexpected in @('BepInEx', 'dotnet', 'doorstop_config.ini', '.doorstop_version')) {
        if (Test-Path -LiteralPath (Join-Path $GameDirectory $unexpected)) {
            throw "기존 모드 파일과 충돌할 수 있어 중단했습니다: $unexpected"
        }
    }

    $TempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $TempDirectory = Join-Path $TempBase ('PathOfIdleInGameSearch-' + [Guid]::NewGuid().ToString('N'))
    $ArchivePath = Join-Path $TempDirectory 'BepInEx.zip'
    $ExtractDirectory = Join-Path $TempDirectory 'extracted'
    New-Item -ItemType Directory -Path $ExtractDirectory -Force | Out-Null
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Write-Host 'BepInEx 공식 고정 버전을 받는 중...'
        Invoke-WebRequest -UseBasicParsing -Uri $BepInExUrl -OutFile $ArchivePath
        if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $BepInExSha256) {
            throw 'BepInEx 다운로드 무결성 검사에 실패했습니다.'
        }
        Expand-Archive -LiteralPath $ArchivePath -DestinationPath $ExtractDirectory -Force

        foreach ($name in @('BepInEx', 'dotnet', 'doorstop_config.ini', 'winhttp.dll', '.doorstop_version')) {
            $source = Join-Path $ExtractDirectory $name
            if (-not (Test-Path -LiteralPath $source)) { throw "BepInEx 압축에 필요한 파일이 없습니다: $name" }
            Copy-Item -LiteralPath $source -Destination $GameDirectory -Recurse -Force
        }
        Write-Host 'BepInEx를 설치했습니다.'
    }
    finally {
        $ResolvedTemp = [IO.Path]::GetFullPath($TempDirectory)
        if ($ResolvedTemp.StartsWith($TempBase, [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $ResolvedTemp) -like 'PathOfIdleInGameSearch-*' -and
            (Test-Path -LiteralPath $ResolvedTemp)) {
            Remove-Item -LiteralPath $ResolvedTemp -Recurse -Force
        }
    }
}
else {
    Write-Host '기존 BepInEx 설치를 그대로 사용합니다.'
}

$ConfigDestination = Join-Path $GameDirectory 'BepInEx\config\BepInEx.cfg'
if (-not (Test-Path -LiteralPath $ConfigDestination -PathType Leaf)) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $ConfigDestination) -Force | Out-Null
    Copy-Item -LiteralPath $BepInExConfigSource -Destination $ConfigDestination
}

New-Item -ItemType Directory -Path (Split-Path -Parent $PluginDestination) -Force | Out-Null
Copy-Item -LiteralPath $PluginSource -Destination $PluginDestination -Force
if ((Get-FileHash -LiteralPath $PluginDestination -Algorithm SHA256).Hash -ne $PluginSha256) {
    throw '설치된 모드 DLL 검증에 실패했습니다.'
}

Write-Host "게임 위치: $GameDirectory"
Write-Host 'Path of Idle In-Game Search 1.1.5 설치 완료.'
Write-Host '처음 실행은 BepInEx 준비 때문에 평소보다 오래 걸릴 수 있습니다.'
