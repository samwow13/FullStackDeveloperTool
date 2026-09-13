@echo off
if exist "%~dp0FullStackLauncher.exe" (
    start "" "%~dp0FullStackLauncher.exe" --codex-monitor %*
) else (
    call "%~dp0launch-codex-monitor.cmd" %*
)
