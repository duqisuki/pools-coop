@echo off
rem Removes exactly what install.bat added. Saves are never touched.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Uninstall %*
echo.
pause
