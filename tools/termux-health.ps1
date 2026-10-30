<#
.SYNOPSIS
  Termux health from the PC (read-only): versions and Termux packages against compat.json (ca-health on the phone), and whether
  Termux, Termux:Boot, Termux:API and Termux:Widget all come from one source (same signing key, as recorded in compat.json).
.DESCRIPTION
  Changes nothing. Exit 0 when everything matches, 1 when something differs.
  Package upgrades stay a deliberate step: termux\scripts\pkg-upgrade.sh (Scripts view in the app) saves a snapshot first.
#>
param([string]$Adb = '')
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'lib.ps1')
Use-SavedPhone
$root = Split-Path -Parent $here
$differ = 0

Step 'Versions and packages (ca-health on the phone)'
#  The phone compares with the PC's current list, so send it first.
Invoke-AdbRoute { & $Adb push (Join-Path $root 'compat.json') /sdcard/Documents/ca/compat.json } | Out-Null
Invoke-AdbRoute { & $Adb push (Join-Path $root 'termux\ca-health') /sdcard/Documents/ca/ca-health } | Out-Null
$log = Invoke-Termux 'tmp-health.sh' "#!/data/data/com.termux/files/usr/bin/bash`nbash /sdcard/Documents/ca/ca-health`n"
Write-Host $log
#  Any DIFF line, or no result at all, counts as a difference.
if ($log -match '(?m)^DIFF' -or $log -notmatch 'known good') { $differ++ }

Step 'Termux apps: one source (same signing key)'
$want = (Get-Content (Join-Path $root 'compat.json') -Raw | ConvertFrom-Json).knownGood.termuxApps
foreach ($app in Get-TermuxApps) {
  $wantVersion = $want.($app.App)
  $ok = $app.Key -eq $want.signingKey -and $app.Version -eq $wantVersion
  #  A missing app, another key or another version is a difference.
  if (-not $ok) { $differ++ }
  Write-Host ('{0}  {1,-18} {2,-10} key {3,-10} known good: {4}, key {5}' -f $(if ($ok) { 'OK  ' } else { 'DIFF' }), $app.App, $app.Version, $app.Key, $wantVersion, $want.signingKey)
}

#  Say the result plainly and set the exit code.
if ($differ -eq 0) { Write-Host "`nTermux is healthy and matches the known-good list." -ForegroundColor Green; exit 0 }
Write-Host "`n$differ difference(s). Nothing was changed; decide whether to update compat.json (after a passing smoke test) or to fix the phone." -ForegroundColor Yellow
exit 1
