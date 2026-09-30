@echo off
rem Build MiniClip.exe with the C# compiler that ships inside Windows.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 %*
echo.
pause
