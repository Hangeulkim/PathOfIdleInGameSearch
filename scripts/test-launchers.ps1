[CmdletBinding()]
param([string]$PackagePath, [string]$SourceDirectory, [switch]$IncludeFreshRuntime)
$ErrorActionPreference = 'Stop'
if (-not $SourceDirectory) { $SourceDirectory = Split-Path -Parent $PSScriptRoot }
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('PathOfIdle-launcher-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
. (Join-Path $PSScriptRoot 'sha256.ps1')

function Invoke-HiddenLauncher {
    param([string]$Launcher, [int]$CodePage = 65001, [string]$Arguments = '', [int]$TimeoutSeconds = 15, [switch]$Direct)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $env:ComSpec
    $invocation = 'call '
    if ($Direct) { $invocation = '' }
    $start.Arguments = '/d /s /c "chcp ' + $CodePage + ' >nul & ' + $invocation + '"' + $Launcher + '" ' + $Arguments + ' <nul"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $start.EnvironmentVariables['PATHOFIDLE_INSTALL_NO_PAUSE'] = '1'
    $process = [Diagnostics.Process]::Start($start)
    try {
        $outTask = $process.StandardOutput.ReadToEndAsync()
        $errTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            # Stop only this test's process tree, never an existing game process.
            & taskkill.exe /PID $process.Id /T /F | Out-Null
            throw "Launcher exceeded $TimeoutSeconds seconds"
        }
        $output = $outTask.GetAwaiter().GetResult() + $errTask.GetAwaiter().GetResult()
        return [pscustomobject]@{ExitCode=$process.ExitCode;Output=$output}
    } finally { $process.Dispose() }
}

function Assert-Test {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

try {
    $package = $SourceDirectory
    if ($PackagePath) {
        Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
        $package = Join-Path $testRoot 'extracted package'
        [IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -LiteralPath $PackagePath).Path, $package)
    }
    foreach ($name in @('install.bat','uninstall.bat')) {
        $bytes = [IO.File]::ReadAllBytes((Join-Path $package $name))
        Assert-Test (@($bytes | Where-Object { $_ -gt 127 -or $_ -eq 0 }).Count -eq 0) "$name must be ASCII without BOM"
        $text = [Text.Encoding]::ASCII.GetString($bytes)
        Assert-Test ($text -notmatch '(?<!\r)\n|\r(?!\n)') "$name must use CRLF"
    }
    $unicodeDirectory = [string][char]0xD55C + [char]0xAE00 + ' & (symbols)'
    $count = 0
    foreach ($directory in @('package with spaces', $unicodeDirectory)) {
        $fixture = Join-Path $testRoot $directory
        $scriptDirectory = Join-Path $fixture 'scripts'
        New-Item -ItemType Directory -Path $scriptDirectory | Out-Null
        foreach ($name in @('install','uninstall')) {
            $launcher = Join-Path $fixture "$name.bat"
            Copy-Item -LiteralPath (Join-Path $package "$name.bat") -Destination $launcher
            $script = Join-Path $scriptDirectory "$name.ps1"
            foreach ($exitCode in @(0,23)) {
                # Only the delegated script is replaced; the source/ZIP BAT is exact.
                $stub = 'param([string]$GameDirectory)' + "`r`n" +
                    'if ($GameDirectory -ne $PSScriptRoot) { exit 88 }' + "`r`n" +
                    'Write-Output "LAUNCHER_STUB_REACHED"' + "`r`nexit $exitCode`r`n"
                [IO.File]::WriteAllText($script, $stub, [Text.Encoding]::ASCII)
                foreach ($page in @(437,949,65001)) {
                  foreach ($direct in @($false,$true)) {
                    $result = Invoke-HiddenLauncher $launcher $page ('-GameDirectory "' + $scriptDirectory + '"') -Direct:$direct
                    Assert-Test ($result.ExitCode -eq $exitCode) "$name exit propagation failed for $page / $directory : $($result.Output)"
                    Assert-Test ($result.Output.Contains('LAUNCHER_STUB_REACHED')) "$name did not invoke its script"
                    Assert-Test ($result.Output -notmatch 'not recognized|9009') "$name was misparsed by CMD"
                    Assert-Test (($result.Output -match 'completed') -eq ($exitCode -eq 0)) "$name reported false success"
                    $count++
                  }
                }
            }
            Remove-Item -LiteralPath $script -Force
            foreach ($page in @(437,949,65001)) {
              foreach ($direct in @($false,$true)) {
                $result = Invoke-HiddenLauncher $launcher $page -Direct:$direct
                Assert-Test ($result.ExitCode -eq 2 -and $result.Output.Contains('files are missing')) "$name missing-script handling failed"
                $count++
              }
            }
        }
    }
    Write-Output "PASS $count exact BAT launcher cases: install/uninstall, direct/CALL, CP437/949/65001, spaces/Unicode/ampersand/parentheses, success/failure/missing-script, argument and exit propagation."

    # Exercise the real scripts through the real BAT files against a disposable
    # game-directory fixture. No installed game files, registry or UI are changed.
    if (Get-Process -Name PathOfIdle -ErrorAction SilentlyContinue) { throw 'Game running: real-script integration not executed; launcher cases passed.' }
    $game = Join-Path $testRoot ($unicodeDirectory + '\game with spaces')
    foreach ($directory in @('PathOfIdle_Data','BepInEx\core','BepInEx\config','BepInEx\plugins')) {
        New-Item -ItemType Directory -Path (Join-Path $game $directory) -Force | Out-Null
    }
    foreach ($file in @('PathOfIdle.exe','GameAssembly.dll','winhttp.dll','BepInEx\core\BepInEx.Unity.IL2CPP.dll')) {
        [IO.File]::WriteAllBytes((Join-Path $game $file), [byte[]]@(0))
    }
    $protected = @('BepInEx\config\local.pathofidle.ingame-search.cfg','BepInEx\config\BepInEx.cfg','BepInEx\plugins\OtherMod.dll')
    $before = @{}
    foreach ($file in $protected) {
        $path = Join-Path $game $file
        [IO.File]::WriteAllText($path, 'SENTINEL ' + $file)
        $before[$file] = Get-ModSha256 $path
    }
    $realArgs = '-GameDirectory "' + $game + '"'
    $result = Invoke-HiddenLauncher (Join-Path $package 'install.bat') 65001 $realArgs -Direct
    Assert-Test ($result.ExitCode -eq 0) "Actual BAT install failed: $($result.Output)"
    $installed = Join-Path $game 'BepInEx\plugins\PathOfIdleInGameSearch.dll'
    Assert-Test ((Get-ModSha256 $installed) -eq (Get-ModSha256 (Join-Path $package 'payload\PathOfIdleInGameSearch.dll'))) 'Installed payload mismatch'
    $result = Invoke-HiddenLauncher (Join-Path $package 'uninstall.bat') 949 $realArgs -Direct
    Assert-Test ($result.ExitCode -eq 0 -and -not (Test-Path -LiteralPath $installed)) "Actual BAT uninstall failed: $($result.Output)"
    foreach ($file in $protected) { Assert-Test ((Get-ModSha256 (Join-Path $game $file)) -eq $before[$file]) "Preserved file changed: $file" }
    $invalidArgs = '-GameDirectory "' + (Join-Path $testRoot 'not a game') + '"'
    foreach ($name in @('install','uninstall')) {
        $result = Invoke-HiddenLauncher (Join-Path $package "$name.bat") 437 $invalidArgs -Direct
        Assert-Test ($result.ExitCode -ne 0 -and $result.Output -notmatch 'completed') "Invalid path reported success: $name"
    }
    Write-Output 'PASS actual BAT -> actual PowerShell install/uninstall, payload checksum, existing settings/other-mod preservation and invalid-game-directory rejection. No game window was launched.'
    if ($IncludeFreshRuntime) {
        $freshGame = Join-Path $testRoot 'fresh runtime game'
        New-Item -ItemType Directory -Path (Join-Path $freshGame 'PathOfIdle_Data') -Force | Out-Null
        foreach ($file in @('PathOfIdle.exe','GameAssembly.dll')) { [IO.File]::WriteAllBytes((Join-Path $freshGame $file), [byte[]]@(0)) }
        Write-Output 'Checking first-time bundled runtime installation through the exact BAT (60-second limit, no game launch)...'
        $result = Invoke-HiddenLauncher (Join-Path $package 'install.bat') 65001 ('-GameDirectory "' + $freshGame + '"') 60 -Direct
        Assert-Test ($result.ExitCode -eq 0) "Fresh runtime installation failed: $($result.Output)"
        Assert-Test ($result.Output.Contains('Using bundled BepInEx runtime.')) 'Fresh install did not use the verified bundled archive.'
        foreach ($file in @('winhttp.dll','BepInEx\core\BepInEx.Unity.IL2CPP.dll','BepInEx\config\BepInEx.cfg')) {
            Assert-Test (Test-Path -LiteralPath (Join-Path $freshGame $file) -PathType Leaf) "Fresh runtime file missing: $file"
        }
        Assert-Test ((Get-ModSha256 (Join-Path $freshGame 'BepInEx\plugins\PathOfIdleInGameSearch.dll')) -eq (Get-ModSha256 (Join-Path $package 'payload\PathOfIdleInGameSearch.dll'))) 'Fresh runtime payload mismatch'
        Write-Output 'PASS first-time official pinned BepInEx archive verification, extraction and DLL installation through the actual BAT without a network download. Runtime/game execution is not part of this installer test.'
        $damagedPackage = Join-Path $testRoot 'damaged runtime package'
        Copy-Item -LiteralPath $package -Destination $damagedPackage -Recurse
        $damagedArchive = Join-Path $damagedPackage 'payload\BepInEx-runtime.zip'
        $emptyGame = Join-Path $testRoot 'empty game for runtime errors'
        New-Item -ItemType Directory -Path (Join-Path $emptyGame 'PathOfIdle_Data') -Force | Out-Null
        foreach ($file in @('PathOfIdle.exe','GameAssembly.dll')) { [IO.File]::WriteAllBytes((Join-Path $emptyGame $file), [byte[]]@(0)) }
        foreach ($mode in @('corrupt','missing')) {
            if ($mode -eq 'corrupt') { [IO.File]::WriteAllBytes($damagedArchive, [byte[]]@(1,2,3)) }
            else { Remove-Item -LiteralPath $damagedArchive -Force }
            $result = Invoke-HiddenLauncher (Join-Path $damagedPackage 'install.bat') 65001 ('-GameDirectory "' + $emptyGame + '"') -Direct
            Assert-Test ($result.ExitCode -ne 0 -and $result.Output.Contains('Bundled BepInEx runtime') -and $result.Output -notmatch 'completed') "Invalid bundled runtime reported success: $mode"
            Assert-Test (-not (Test-Path -LiteralPath (Join-Path $emptyGame 'BepInEx')) -and -not (Test-Path -LiteralPath (Join-Path $emptyGame 'winhttp.dll'))) "Invalid runtime wrote game files: $mode"
        }
        Write-Output 'PASS corrupt/missing bundled runtime rejection with no runtime/plugin files installed.'
    }
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempBase,[StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $resolved) -like 'PathOfIdle-launcher-test-*') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    } else { throw 'Refusing unsafe test cleanup' }
}
