<#
.SYNOPSIS
  Downloads the OFFICIAL Yep Anywhere package from npm, checks it against the registry's checksum, and puts it in deploy\.
.DESCRIPTION
  PocketIris no longer changes Yep: the phone installs exactly the file npm publishes. This script fetches that file
  (npm pack yepanywhere@<version>), compares its SHA-512 with the value the registry states, and stops if they differ.
  The file keeps the official name (yepanywhere-<version>.tgz); ca-update installs the newest one found on the phone.
  Afterwards: tools\deploy-yep.ps1 -Tarball <file> (saves data first, tests on the phone, rolls back on failure).
.PARAMETER Version
  A version such as 0.11.0, "latest", or "stable" (the newest release that has been out for at least -StableDays days; Yep has no
  stable channel of its own, so time is the test). Default: the version pinned in tools\yep-version.txt.
.EXAMPLE
  tools\fetch-yep.ps1                  # the pinned version
  tools\fetch-yep.ps1 -Version latest  # the newest official release
  tools\fetch-yep.ps1 -Version stable  # the newest release that is at least 7 days old
#>
param(
  [string]$Version = '',
  [int]$StableDays = 7,
  [string]$DeployDir = (Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) 'deploy')
)
$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'lib.ps1')

#  Use the pinned version unless one was asked for.
if (-not $Version) { $Version = (Get-Content (Join-Path $here 'yep-version.txt') -Raw).Trim(); Write-Host "Using the pinned version $Version" }

#  "stable": the newest plain release (no -beta and the like) published at least StableDays days ago.
if ($Version -eq 'stable') {
  $times = (& npm view yepanywhere time --json 2>$null | Out-String | ConvertFrom-Json)
  $cutoff = (Get-Date).ToUniversalTime().AddDays(-$StableDays)
  $old = $times.PSObject.Properties | Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' -and ([datetime]$_.Value).ToUniversalTime() -le $cutoff }
  $pick = $old | Sort-Object { [version]$_.Name } | Select-Object -Last 1
  #  Stop when no release is old enough (or the registry did not answer).
  if (-not $pick) { Fail "No yepanywhere release is at least $StableDays days old (or npm did not answer)." }
  $Version = $pick.Name
  Write-Host "Stable channel: $Version (published $(([datetime]$pick.Value).ToString('yyyy-MM-dd')), at least $StableDays days ago)"
}

Step 'Asking the npm registry about the official package'
$info = (& npm view "yepanywhere@$Version" version dist.integrity --json 2>$null | Out-String | ConvertFrom-Json)
#  Stop when the registry does not know that version.
if (-not $info -or -not $info.version) { Fail "npm does not know yepanywhere@$Version." }
$real = if ($info.version -is [array]) { $info.version[-1] } else { $info.version }
$integrity = if ($info.'dist.integrity' -is [array]) { $info.'dist.integrity'[-1] } else { $info.'dist.integrity' }
Write-Host "Official version: $real"
Write-Host "Registry checksum: $integrity"

Step 'Downloading it'
New-Item -ItemType Directory -Force $DeployDir | Out-Null
Push-Location $DeployDir
try { & npm pack "yepanywhere@$real" --pack-destination $DeployDir 2>&1 | Select-Object -Last 1 | Out-Host }
finally { Pop-Location }
$file = Join-Path $DeployDir "yepanywhere-$real.tgz"
#  Stop when the download did not produce the expected file.
if (-not (Test-Path $file)) { Fail "Expected $file but it was not created." 2 }

Step 'Checking it against the registry'
$sha = [System.Security.Cryptography.SHA512]::Create()
$bytes = [System.IO.File]::ReadAllBytes($file)
$mine = 'sha512-' + [Convert]::ToBase64String($sha.ComputeHash($bytes))
Write-Host "File checksum:     $mine"
#  The two checksums must be identical, otherwise the file is not the official one.
if ($mine -ne $integrity) { Fail 'The downloaded file does NOT match the registry checksum. Not using it.' 3 }
Write-Host "OK: $file is exactly the official package." -ForegroundColor Green
Write-Host 'Next: tools\deploy-yep.ps1 -Tarball' $file
