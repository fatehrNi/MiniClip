@echo off
rem Start MiniClip clipboard history. Safe to double-click repeatedly: a second
rem launch just brings up the panel of the instance that is already running.
cd /d "%~dp0"
if not exist "MiniClip.exe" (
  echo MiniClip.exe not found - building first...
  powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
)
start "" "MiniClip.exe"
