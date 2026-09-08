@echo off
setlocal EnableExtensions DisableDelayedExpansion
title Path of Idle In-Game Search Uninstaller
if not exist "%~dp0scripts\uninstall.ps1" goto missing
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\uninstall.ps1" %*
set "uninstallExit=%errorlevel%"
echo.
if not "%uninstallExit%"=="0" goto failed
echo Mod removal completed. Settings and other mods were preserved.
goto done

:missing
set "uninstallExit=2"
echo Uninstaller files are missing. Extract the entire release ZIP and try again.
goto done

:failed
echo Removal failed. Please read the error above.

:done
echo.
if not defined PATHOFIDLE_INSTALL_NO_PAUSE pause
exit /b %uninstallExit%
