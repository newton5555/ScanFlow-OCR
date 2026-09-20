@echo off
setlocal
cd /d "%~dp0"
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Publish-Win-x64.ps1" %*
if %ERRORLEVEL% neq 0 (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Publish-Win-x64.ps1" %*
)
