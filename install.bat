@echo off
setlocal
chcp 65001 >nul
title Path of Idle In-Game Search Installer
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install.ps1"
if errorlevel 1 (
  echo.
  echo 설치하지 못했습니다. 위 오류 내용을 확인해 주세요.
) else (
  echo.
  echo 설치가 끝났습니다. 이제 Path of Idle을 실행하세요.
)
echo.
pause

