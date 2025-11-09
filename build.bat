@echo off
REM Simple wrapper to launch the PowerShell build script
REM This keeps the terminal open and provides better control

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build_single_exe.ps1"
