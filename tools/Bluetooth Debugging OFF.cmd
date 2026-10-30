@echo off
title PocketIris - Bluetooth debugging OFF
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0bt-debug.ps1" -Off
echo.
echo Now switch "Bluetooth debugging" Off in the PocketIris app too.
pause
