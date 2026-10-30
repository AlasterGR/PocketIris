@echo off
title PocketIris - Bluetooth debugging
echo Switch "Bluetooth debugging" On in the PocketIris app first (PocketIris tab ^> Developer).
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0bt-debug.ps1" -Smoke
echo.
pause
