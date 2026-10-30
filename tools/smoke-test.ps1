<#
.SYNOPSIS
  A quick check that PocketIris still works after a change. Needs the phone connected over USB (or adb over Wi-Fi),
  unlocked, with page debugging on (adb shell am start -n io.github.alastergr.pocketiris/io.github.alastergr.pocketiris.ControlActivity --es cmd debug-on).
.DESCRIPTION
  Checks: the app is running, the chat server answers, the page bridges exist, the automatic self-tests pass
  (slips, address checks, phone info), and the tool server answers Claude (with the secret key) and refuses everyone else.
  It never taps anything on the phone and never downloads anything. Prints PASS or FAIL for each step.
#>
param([string]$Adb = '')
$ErrorActionPreference = 'Stop'
$failed = 0
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
#  Load the shared helpers and pick the phone (a saved Wi-Fi or Bluetooth address only while it is really connected).
. (Join-Path $here 'lib.ps1')
Use-SavedPhone

#  Prints one result line and counts the failures.
function Check($name, [bool]$ok, $detail = '') {
  if ($ok) { Write-Host "PASS  $name $detail" -ForegroundColor Green } else { Write-Host "FAIL  $name $detail" -ForegroundColor Red; $script:failed++ }
}

#  The two-tier token rule: no colour or size literal outside the token files (a static check, needs no phone).
& (Join-Path $here 'check-tokens.ps1') | Out-Null
Check 'no token leaks (colour and size literals only in the token files)' ($LASTEXITCODE -eq 0)

#  The app must be running; start it if it is not.
$procId = (& $Adb shell "pidof $package").Trim()
if (-not $procId) { & $Adb shell am start -n "$package/$package.MainActivity" | Out-Null; Start-Sleep 20; $procId = (& $Adb shell "pidof $package").Trim() }
Check 'app is running' ([bool]$procId)

#  The chat server must answer on the phone.
& $Adb forward tcp:3400 tcp:3400 | Out-Null
$code = (curl.exe -s -o NUL -w '%{http_code}' --max-time 5 http://127.0.0.1:3400/)
& $Adb forward --remove tcp:3400 | Out-Null
Check 'chat server answers' ($code -eq '200') "(HTTP $code)"

#  The chat page must see only the small trash bridge, and none of the powerful ones.
#  A chat page that is not loaded at all is a failure of this check, said in words, not a crash of the whole test.
try { $bridges = & "$here\cdp.ps1" -Expr 'typeof window.PocketTrash + "," + typeof window.PocketIris + "," + typeof window.PocketViews' } catch { $bridges = "no chat page: $($_.Exception.Message)" }
Check 'chat page has only the trash bridge' ($bridges -eq 'object,undefined,undefined') "($bridges)"

#  The PocketIris page must be loaded with its bridge, show an appearance, and reach the helper (the trash count proves it).
$pc = & "$here\cdp.ps1" -PageTitle 'PocketIris' -Expr 'typeof window.PocketIris + "|" + JSON.parse(PocketIris.getAppearance()).effective + "|" + document.getElementById("trashTab").textContent'
Check 'PocketIris page works and reaches the helper' ($pc -match '^object\|(phone|tablet|desktop)\|In trash \(\d+\)$') "($pc)"

#  Version awareness: the PC's known-good list goes to the phone, and the app must be able to compare it with what Termux recorded.
#  A difference is reported but is not a failure (it is what the banner is for); "unknown" means a file is missing and fails.
#  An update done from the phone's Updates card records its new versions in the phone's copy (recordedBy "phone ..."); that copy then wins and comes
#  back into the repository, so the PC never overwrites a newer known-good list with an older one.
$compatPath = Join-Path (Split-Path -Parent $here) 'compat.json'
$phoneCompat = (Invoke-AdbRoute { & $Adb shell cat /sdcard/Documents/ca/compat.json }) -join "`n"
$phoneData = $null
try { $phoneData = $phoneCompat | ConvertFrom-Json } catch { $phoneData = $null }
if ($phoneData -and "$($phoneData.recordedBy)" -like 'phone*' -and ($phoneData.knownGood | ConvertTo-Json -Depth 5) -ne ((Get-Content $compatPath -Raw | ConvertFrom-Json).knownGood | ConvertTo-Json -Depth 5)) {
  [IO.File]::WriteAllText($compatPath, $phoneCompat.TrimEnd() + "`n")
  Write-Host "compat.json updated from the phone ($($phoneData.recordedBy), $($phoneData.recordedOn)); commit it." -ForegroundColor Cyan
}
Invoke-AdbRoute { & $Adb push $compatPath /sdcard/Documents/ca/compat.json } | Out-Null
$versions = & "$here\cdp.ps1" -PageTitle 'PocketIris' -Expr 'PocketIris.versionStatus()'
Check 'app compares versions with the known-good list' ($versions -match '^(ok|mismatch)\|') "($versions)"

#  Termux and its add-on apps must come from one source: mixing GitHub and F-Droid builds (different signing keys) breaks them.
$keys = @(Get-TermuxApps | Where-Object Key | Select-Object -ExpandProperty Key -Unique)
Check 'Termux, Boot, API and Widget come from one source' ($keys.Count -eq 1) "(signing keys: $($keys -join ', '))"

#  The tests that need no touch: ask the app to run them and read the report from the phone log.
& $Adb logcat -c
foreach ($t in 'slip', 'guards', 'info') { & "$here\cdp.ps1" -PageTitle 'PocketIris' -Expr "window.PocketIris.runTest('$t'); 'ok'" | Out-Null }

#  Wait for all three reports to appear in the phone log (up to 40 seconds, because the Bluetooth link can be slow).
$log = ''
for ($i = 0; $i -lt 20; $i++) {
  $log = (& $Adb logcat -d -s PocketIris) -join "`n"
  if ($log -match 'self-test slip ->' -and $log -match 'self-test guards ->' -and $log -match 'self-test info ->') { break }
  Start-Sleep 2
}
Check 'slip self-test' ($log -match 'self-test slip -> PASS')
Check 'address self-test' ($log -match 'self-test guards -> Address checks: 127\.0\.0\.1: loopback=True, private=True')
Check 'phone info self-test' ($log -match 'self-test info -> Android')

#  The tool server is checked from inside Termux, where Claude really connects from and where the secret key lives:
#  it must list its tools and answer a phone tool for Claude, and refuse everyone else (no key, wrong key, web pages, wrong host, wrong type, huge body).
#  Then the Android shims that keep Yep unpatched are checked the same way (preload loaded by the server, bcrypt twin, hard-link fallback).
foreach ($set in @(@('ca-selftest-mcp', 'tmp-selftest-mcp.sh', 'tool server'), @('ca-selftest-shims', 'tmp-selftest-shims.sh', 'android shims'), @('ca-selftest-sidecar', 'tmp-selftest-sidecar.sh', 'sidecar'), @('ca-selftest-official', 'tmp-selftest-official.sh', 'official yep'), @('ca-selftest-claude-version', 'tmp-selftest-claude-version.sh', 'claude versions (on a copy)'), @('ca-selftest-updates', 'tmp-selftest-updates.sh', 'updates (on a copy)'))) {
  $log = Invoke-TermuxScript (Join-Path (Join-Path (Split-Path -Parent $here) 'termux') $set[0]) $set[1] 120
  $lines = ($log -split "`n") | Where-Object { $_ -match '^(PASS|FAIL) ' }
  #  A run that printed no results at all counts as a failure, because nothing was proven.
  if (-not $lines) { Check ($set[2] + ' checks ran') $false '(no output from Termux)' }
  foreach ($line in $lines) { Check ($set[2] + ': ' + $line.Substring(5)) $line.StartsWith('PASS') }
}

#  After a full pass, keep a spare copy of the known-good Claude Code version outside the launcher's folder, so its cleanup can never remove
#  the version that was tested (about 290 MB, once per version; ca-claude-version says when it is already kept or there is no room).
if ($failed -eq 0) {
  $keep = Invoke-Termux 'tmp-keep.sh' "#!/data/data/com.termux/files/usr/bin/bash`n`$HOME/bin/ca-claude-version keep 2>&1`n" 240
  Write-Host ("Spare Claude Code: " + ((($keep -split "`n") | Where-Object { $_ -match '^(Kept|Already kept|Cannot keep|Not enough)' }) -join ' ')) -ForegroundColor Cyan
}

#  Say the result and set the exit code explicitly, so a caller never reads a leftover code from the last adb call.
if ($failed -eq 0) { Write-Host "`nAll checks passed." -ForegroundColor Green; exit 0 } else { Write-Host "`n$failed check(s) failed." -ForegroundColor Red; exit 1 }
