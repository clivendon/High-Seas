@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-HighSeasMedia.ps1"
if errorlevel 1 (
  echo.
  echo Installation failed. See the message above for details.
  pause
)
endlocal

