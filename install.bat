@echo off
rem POOLS Co-op installer. Double-click it, or: install.bat -Build flat  /  install.bat -GameDir "D:\Games\Pools"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
