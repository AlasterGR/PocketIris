<#
.SYNOPSIS
  Sends a built chat-engine tarball to the phone, updates the server, runs the smoke test and rolls back on failure.
.DESCRIPTION
  1. Saves ~/.yep-anywhere on the phone (a new version may migrate it, and an old version cannot read a migrated file).
  2. Pushes the Termux scripts and the tarball, then runs the app's update command.
  3. Runs tools\smoke-test.ps1. Pass: exit 0.
  4. Fail (a failed check or a crash inside the test): moves the new tarball away, restores the saved data, reinstalls the newest remaining tarball, tests again.
     Exit 6 = rolled back and healthy, 7 = rolled back and still failing.
.EXAMPLE
  tools\deploy-yep.ps1 -Tarball deploy\yepanywhere-0.11.0.tgz
#>
param(
  [Parameter(Mandatory)][string]$Tarball,
  [string]$Adb = ''
)
$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
#  The version is the part of the file name between "yepanywhere-" and ".tgz".
$version = [System.IO.Path]::GetFileNameWithoutExtension($Tarball) -replace '^yepanywhere-', ''
#  Stop early when the tarball is missing.
if (-not (Test-Path $Tarball)) { Write-Host "STOP: $Tarball not found." -ForegroundColor Red; exit 2 }

#  Load the shared helpers and pick the phone.
. (Join-Path $PSScriptRoot 'lib.ps1')
Use-SavedPhone

Step 'Saving the server data on the phone first (the new version may migrate it)'
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$snapshot = "/sdcard/_Review/backups/pre-deploy-$stamp.tar.gz"
$snapOut = Invoke-Termux 'tmp-snapshot.sh' @"
#!/data/data/com.termux/files/usr/bin/bash
# Save the server data before an upgrade test.
mkdir -p /sdcard/_Review/backups
cd "`$HOME" && tar czf "$snapshot" .yep-anywhere && ls -la "$snapshot"
"@
Write-Host $snapOut
#  Stop when the snapshot was not written, because a rollback would have no data to go back to.
if ($snapOut -notmatch 'pre-deploy-') { Fail 'The data snapshot on the phone failed. Nothing was changed.' 6 }

Step 'Sending the Termux scripts to the phone (ca-update installs them)'
#  Push every ca-* script, so a new helper such as ca-defaults reaches the phone together with the new build.
Get-ChildItem (Join-Path (Split-Path -Parent $here) 'termux') -Filter 'ca-*' | ForEach-Object { & $Adb push $_.FullName /sdcard/Documents/ca/ | Out-Null }
& $Adb push (Join-Path (Split-Path -Parent $here) 'termux\install.sh') /sdcard/Documents/ca/ | Out-Null
#  The known-good list goes along, so the app's version check compares with the PC's current copy.
& $Adb push (Join-Path (Split-Path -Parent $here) 'compat.json') /sdcard/Documents/ca/ | Out-Null

#  Push the ready-made helper scripts that the app's Scripts view shows (backup, restore, cache cleaning, status checks).
Get-ChildItem (Join-Path (Split-Path -Parent $here) 'termux\scripts') -File | ForEach-Object { & $Adb push $_.FullName /sdcard/Documents/ca/scripts/ | Out-Null }

Step 'Sending the tarball to the phone'
Send-FileResumable $Tarball ('/sdcard/Documents/' + (Split-Path $Tarball -Leaf))

Step 'Updating the server on the phone (about 100 seconds)'
Send-AppCommand 'update'
Start-Sleep -Seconds 100
Send-AppCommand 'reload'
Start-Sleep -Seconds 10

Step 'Smoke test'
#  Only a smoke test that finished with every check passing keeps the new version; a failure or a crash inside it rolls back.
if (Invoke-SmokeTest) {
  Write-Host "PASSED on the phone with $version." -ForegroundColor Green
  exit 0
}

#  The test failed: move the new tarball off the phone and reinstall the previous one.
Step 'Smoke test failed: rolling back the code and the data'
& $Adb shell "mkdir -p /sdcard/_Review; mv /sdcard/Documents/yepanywhere-$version.tgz /sdcard/_Review/"
$restoreOut = Invoke-Termux 'tmp-rollback.sh' @"
#!/data/data/com.termux/files/usr/bin/bash
# Put the server data back as it was before the upgrade test, keeping the newer copy aside.
export PATH="`$HOME/bin:`$PATH"
ca-stop
mkdir -p /sdcard/_Review/backups/migrated-by-test
mv "`$HOME/.yep-anywhere" "/sdcard/_Review/backups/migrated-by-test/yep-anywhere-$stamp" && echo "moved newer data aside"
cd "`$HOME" && tar xzf "$snapshot" .yep-anywhere && echo "restored data"
"@
Write-Host $restoreOut
Send-AppCommand 'update'
Start-Sleep -Seconds 100
Send-AppCommand 'reload'
Start-Sleep -Seconds 10
#  Say whether the old version is working again, because the user needs to know.
if (Invoke-SmokeTest) { Fail "The new build failed on the phone; the previous version and the saved data are back and pass." 6 }
Fail "The new build failed AND the rollback test did not pass. Data snapshot: $snapshot on the phone. Check tools\smoke-test.ps1 and ca-status." 7
