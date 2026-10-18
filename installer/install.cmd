@echo off
rem Double-click to install AttractorLock for the current user.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
pause
