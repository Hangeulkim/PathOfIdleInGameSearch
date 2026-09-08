@echo off
setlocal EnableExtensions DisableDelayedExpansion
title Path of Idle In-Game Search Installer
if not exist "%~dp0scripts\install.ps1" goto missing
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install.ps1" %*
set "installExit=%errorlevel%"
echo.
if not "%installExit%"=="0" goto failed
echo Installation completed. You can now start Path of Idle.
goto done

:missing
set "installExit=2"
echo Installer files are missing. Extract the entire release ZIP and try again.
goto done

:failed
echo Installation failed. Please read the error above.

:done
echo.
if not defined PATHOFIDLE_INSTALL_NO_PAUSE pause
exit /b %installExit%
