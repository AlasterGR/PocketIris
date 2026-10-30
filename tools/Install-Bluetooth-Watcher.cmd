@echo off
title Install the PocketIris Bluetooth watcher
rem Makes the PC start Bluetooth debugging by itself when you switch it On in the app (a shortcut in the Startup folder).
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath('Startup')) 'PocketIris Bluetooth watcher.lnk')); $s.TargetPath='powershell.exe'; $s.Arguments='-WindowStyle Hidden -NoProfile -ExecutionPolicy Bypass -File "%~dp0bt-watch.ps1"'; $s.WorkingDirectory='%~dp0'; $s.Save(); Start-Process powershell -WindowStyle Hidden -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+'%~dp0bt-watch.ps1'+'"')"
echo Done. The watcher runs now and at every Windows sign-in. When Windows Firewall asks, choose Allow.
pause
