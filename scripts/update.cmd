@echo off
rem Updates WinMirroring: closes it, copies the new build, starts it again. See update.ps1 for options.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0update.ps1" %*
if errorlevel 1 (pause) else (timeout /t 3 >nul)
