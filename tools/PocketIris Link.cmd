@echo off
title PocketIris - link the phone to this PC
echo.
echo  Which link did you choose on the phone (PocketIris tab ^> Developer ^> Connect to another device by)?
echo.
echo    1 = Wi-Fi
echo    2 = Wi-Fi + Bluetooth
echo    3 = Bluetooth only
echo    4 = Off (go back to the USB cable)
echo.
choice /c 1234 /n /m "Press 1, 2, 3 or 4: "
if errorlevel 4 goto off
if errorlevel 3 goto bt
if errorlevel 2 goto wifibt
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0phone-link.ps1" -Mode wifi -Smoke
goto end
:wifibt
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0phone-link.ps1" -Mode wifibt -Smoke
goto end
:bt
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0phone-link.ps1" -Mode bt -Smoke
goto end
:off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0phone-link.ps1" -Off
:end
echo.
pause
