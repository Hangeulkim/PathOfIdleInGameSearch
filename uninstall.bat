@echo off
setlocal
chcp 65001 >nul
title Path of Idle In-Game Search Uninstaller
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\uninstall.ps1"
if errorlevel 1 (
  echo.
  echo 제거하지 못했습니다. 위 오류 내용을 확인해 주세요.
) else (
  echo.
  echo 모드 DLL을 제거했습니다.
)
echo.
pause

