@echo off
rem Stop the app, remove shortcuts and the autostart entry. History data is kept.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File uninstall.ps1
echo.
pause
