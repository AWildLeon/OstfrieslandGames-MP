@echo off
rem Doppelklick-Installer für Windows: ruft install.ps1 auf (optional mit Spielordner als Argument).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
