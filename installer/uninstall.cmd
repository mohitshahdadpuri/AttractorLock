@echo off
rem Double-click to uninstall AttractorLock (keeps your password; add -RemoveData to delete it).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" %*
pause
