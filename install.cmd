@echo off
rem Create desktop + start-menu shortcut, register autostart, then launch.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1
echo.
pause
