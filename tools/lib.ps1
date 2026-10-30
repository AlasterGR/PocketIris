<#
  Shared helpers for the PocketIris PC tools. Dot-source it at the top of a script:   . (Join-Path $PSScriptRoot 'lib.ps1')
  A script may pass its own $Adb (path to adb.exe); when it is empty, it is found here (see Resolve-Adb).
  Functions read $Adb and $package from the calling script, because dot-sourcing puts them in the caller's scope.
  Machine-specific settings go in tools\local.ps1 (not in git), for example:   $LocalAdb = 'D:\Android\platform-tools\adb.exe'
  and the phone's hardware serial (adb shell getprop ro.serialno), so no other device on the network is ever used:   $LocalPhoneSerial = 'abc123'
#>

$package = 'io.github.alastergr.pocketiris'

#  This machine's own settings, if any (kept out of git so the repository has no personal paths).
$localSettings = Join-Path $PSScriptRoot 'local.ps1'
if (Test-Path $localSettings) { . $localSettings }

#  Finds adb.exe: the POCKETIRIS_ADB variable, then $LocalAdb from local.ps1, then adb on the PATH, then the usual Android SDK folders.
function Resolve-Adb {
  $candidates = @($env:POCKETIRIS_ADB, $LocalAdb)
  $onPath = Get-Command adb -ErrorAction SilentlyContinue
  if ($onPath) { $candidates += $onPath.Source }
  $candidates += @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, (Join-Path "$env:LOCALAPPDATA" 'Android\Sdk')) | Where-Object { $_ } | ForEach-Object { Join-Path $_ 'platform-tools\adb.exe' }
  $found = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
  #  Without adb nothing here can reach the phone, so say how to fix it.
  if (-not $found) { throw 'adb.exe not found. Put it on the PATH, set POCKETIRIS_ADB, or write $LocalAdb = ''...\adb.exe'' in tools\local.ps1.' }
  return $found
}
#  Use the caller's adb when it gave one; otherwise find it.
if (-not $Adb) { $Adb = Resolve-Adb }

#  Prints a heading so the report is easy to scan.
function Step($text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }

#  Stops the script with a clear message.
function Fail($text, [int]$code = 1) { Write-Host "STOP: $text" -ForegroundColor Red; exit $code }

#  Runs a program and stops the script when it returns an error code.
function Run([string]$what, [scriptblock]$cmd) {
  & $cmd
  #  A non-zero exit code means the step failed, so stop here.
  if ($LASTEXITCODE -ne 0) { Fail "$what failed (exit code $LASTEXITCODE)." 2 }
}

#  Runs git in a given folder and returns its output lines.
function GitIn([string]$dir, [string[]]$gitArgs) { & git -C $dir @gitArgs 2>&1 }

#  The phone's own hardware serial (ro.serialno): $LocalPhoneSerial from tools\local.ps1, or learned from the cable the first time it is seen.
#  Every route must answer with it, because adb may also list other devices on the network (a TV with debugging on, another phone).
$script:PhoneId = $LocalPhoneSerial
$script:DeviceIds = @{}

#  Asks one connected device for its hardware serial (remembered per address, since it never changes).
function Get-DeviceId([string]$serial) {
  #  Ask only once per address; an empty answer is not remembered, so a slow device gets asked again.
  if (-not $script:DeviceIds[$serial]) {
    $id = ((& $Adb -s $serial shell getprop ro.serialno 2>$null) -join '').Trim()
    if ($id) { $script:DeviceIds[$serial] = $id }
  }
  return $script:DeviceIds[$serial]
}

#  Lists the connections to THE phone that are up right now, as objects with the address and the route ("cable" or "wireless").
function Get-PhoneRoutes {
  $lines = @((& $Adb devices) | Select-String '^(\S+)\s+device$')
  $all = @($lines | ForEach-Object {
    $serial = $_.Matches[0].Groups[1].Value
    #  An address with a port, or a name that starts with "adb-", is a wireless connection; anything else is the cable.
    [pscustomobject]@{ Serial = $serial; Route = $(if ($serial -match ':\d+$' -or $serial -match '^adb-') { 'wireless' } else { 'cable' }) }
  })
  #  Without a known serial, learn it from the cable when exactly one device is plugged in.
  if (-not $script:PhoneId) { $cables = @($all | Where-Object Route -eq 'cable'); if ($cables.Count -eq 1) { $script:PhoneId = Get-DeviceId $cables[0].Serial } }
  #  Still unknown: one device alone is safe to use; with several, guessing could reach the wrong one, so none is used.
  if (-not $script:PhoneId) {
    if ($all.Count -le 1) { return $all }
    Write-Host 'Several adb devices are connected and the phone is not known yet: set $LocalPhoneSerial in tools\local.ps1 (adb shell getprop ro.serialno).' -ForegroundColor Yellow
    return @()
  }
  return @($all | Where-Object { (Get-DeviceId $_.Serial) -eq $script:PhoneId })
}

#  Chooses the connection to use: the cable when it is up, otherwise the wireless link. Returns the chosen route, or $null when there is none.
function Select-PhoneRoute {
  $routes = Get-PhoneRoutes
  $pick = ($routes | Where-Object Route -eq 'cable' | Select-Object -First 1)
  #  Fall back to the wireless link when there is no cable.
  if (-not $pick) { $pick = ($routes | Select-Object -First 1) }
  #  The phone is not connected. adb would then use any single device it sees (a TV, another phone), so name one that cannot exist:
  #  every adb call fails with "not found" instead of reaching the wrong device.
  if (-not $pick) { $env:ANDROID_SERIAL = 'pocketiris-phone-not-connected'; Write-Host 'The phone is not connected (no cable, no wireless link to it).' -ForegroundColor Yellow; return $null }
  $env:ANDROID_SERIAL = $pick.Serial
  return $pick.Route
}

#  Keeps the wireless link ready as a standby while the cable is in use, so that pulling the cable costs nothing: it reconnects the saved wireless address if it dropped.
function Connect-Standby {
  $serialFile = Join-Path $PSScriptRoot '.adb-serial'
  #  Nothing to keep ready when no wireless address was ever saved.
  if (-not (Test-Path $serialFile)) { return }
  $saved = (Get-Content $serialFile -Raw).Trim()
  #  Only an address with a port can be reconnected, and only when it is not already connected.
  if ($saved -match ':\d+$' -and -not ((Get-PhoneRoutes).Serial -contains $saved)) {
    $job = Start-Job { param($adb, $addr) & $adb connect $addr } -ArgumentList $Adb, $saved
    #  Give it a few seconds; an unreachable phone must never hold the tools up.
    if (-not (Wait-Job $job -Timeout 4)) { Stop-Job $job }
    Remove-Job $job -Force
  }
}

#  Tells the phone which PC is talking to it, once over every connection that is up (cable and wireless), so the app can show "Connected to device NAME" for each.
function Send-Hello([string]$route) {
  #  The hello is a courtesy: a failure to deliver it never stops the real work.
  try {
    foreach ($r in (Get-PhoneRoutes)) {
      & $Adb -s $r.Serial shell am broadcast -a "$package.HELLO" -n "$package/$package.HelloReceiver" --es pc $env:COMPUTERNAME --es route $r.Route | Out-Null
    }
  } catch { }
}

#  Picks the phone and the route to talk to (the cable first, the wireless link as the fallback), keeps the standby ready and says hello.
function Use-SavedPhone {
  #  A phone the caller already chose is left alone, as long as it is still connected.
  if ($env:ANDROID_SERIAL -and ((Get-PhoneRoutes).Serial -notcontains $env:ANDROID_SERIAL)) { $env:ANDROID_SERIAL = $null }
  #  With the cable up, make sure the wireless standby is connected too before choosing.
  if (-not $env:ANDROID_SERIAL) {
    if ((Get-PhoneRoutes | Where-Object Route -eq 'cable')) { Connect-Standby }
    [void](Select-PhoneRoute)
  }
  $route = if ($env:ANDROID_SERIAL -match ':\d+$' -or $env:ANDROID_SERIAL -match '^adb-') { 'wireless' } else { 'cable' }
  #  Say hello only when a phone is actually connected.
  if ($env:ANDROID_SERIAL) { Send-Hello $route }
}

#  Runs an adb action; if it fails because the connection dropped (for example the cable was pulled), switches to the other route and runs it once more.
function Invoke-AdbRoute([scriptblock]$action) {
  $out = Invoke-AdbQuiet $action
  #  A failure with the chosen connection gone means "hop to the other route and try again".
  if ($LASTEXITCODE -ne 0 -and ((Get-PhoneRoutes).Serial -notcontains $env:ANDROID_SERIAL)) {
    $route = Select-PhoneRoute
    #  Retry only when another route really exists.
    if ($route) { Write-Host "Connection lost; continuing over the $route route." -ForegroundColor Yellow; Send-Hello $route; $out = Invoke-AdbQuiet $action }
  }
  return $out
}

#  Runs an adb action and returns only its normal output. adb writes progress lines ("1 file pushed") to stderr, which Windows PowerShell
#  turns into errors that stop a script running with ErrorActionPreference 'Stop'; here they are kept apart and shown only when adb really failed.
function Invoke-AdbQuiet([scriptblock]$action) {
  $ErrorActionPreference = 'Continue'
  $all = @(& $action 2>&1)
  $out = @($all | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] })
  $err = @($all | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
  #  Show adb's own words only when the command failed; on success they are just progress noise.
  if ($LASTEXITCODE -ne 0 -and $err.Count) { Write-Host ($err -join "`n") -ForegroundColor Yellow }
  return $out
}

#  Returns the size in bytes of a file on the phone (0 when it does not exist), asking over whichever route works.
function Get-RemoteSize([string]$path) {
  $text = (Invoke-AdbRoute { & $Adb shell "stat -c %s '$path' 2>/dev/null || echo 0" }) -join ''
  $n = 0L
  [void][long]::TryParse($text.Trim(), [ref]$n)
  return $n
}

#  Copies a big file to the phone in small pieces and keeps going when the connection drops: the next piece simply goes over whichever route is up
#  (the cable first, otherwise the wireless link), after checking how much has really arrived. A drop costs a short hiccup, never a restart.
#  The finished file is checked against the PC's copy (SHA-256) before it gets its real name.
function Send-FileResumable([string]$local, [string]$remote, [int]$chunkMb = 4) {
  $chunk = [long]$chunkMb * 1MB
  $total = (Get-Item $local).Length
  $part = "$remote.part"
  $stage = '/sdcard/.pc-chunk'
  $tmp = Join-Path $env:TEMP ('pc-chunk-' + [guid]::NewGuid().ToString('N'))
  $size = Get-RemoteSize $part
  #  A leftover that is not a whole number of pieces cannot be continued safely, so it starts again.
  if ($size % $chunk -ne 0 -or $size -gt $total) { Invoke-AdbRoute { & $Adb shell "rm -f '$part'" } | Out-Null; $size = 0 }
  $stream = [System.IO.File]::OpenRead($local)
  try {
    $index = [int]($size / $chunk)
    #  Send one piece after another until the whole file is there.
    while ($size -lt $total) {
      $offset = [long]$index * $chunk
      $len = [Math]::Min($chunk, $total - $offset)
      $buffer = New-Object byte[] $len
      [void]$stream.Seek($offset, 'Begin')
      [void]$stream.Read($buffer, 0, $len)
      [System.IO.File]::WriteAllBytes($tmp, $buffer)
      $done = $false

      #  Try the piece again, over whichever route is up, until it has really arrived (a few short pauses at most).
      for ($try = 1; $try -le 12 -and -not $done; $try++) {
        $have = Get-RemoteSize $part
        #  Already there (the answer was lost, not the data): nothing to send.
        if ($have -eq $offset + $len) { $done = $true; break }
        #  A half-written piece is cut back to where the last whole piece ended.
        if ($have -ne $offset) { Invoke-AdbRoute { & $Adb shell "head -c $offset '$part' > '$part.t' && mv '$part.t' '$part'" } | Out-Null }
        Invoke-AdbRoute { & $Adb push $tmp $stage } | Out-Null
        #  Append the staged piece to the growing file, then check the new size.
        if ($LASTEXITCODE -eq 0) { Invoke-AdbRoute { & $Adb shell "cat '$stage' >> '$part'" } | Out-Null }
        if ((Get-RemoteSize $part) -eq $offset + $len) { $done = $true } else {
          Write-Host "Connection hiccup at $([int](100 * $offset / $total)) %; looking for a route and continuing." -ForegroundColor Yellow
          Start-Sleep -Seconds 2
          [void](Select-PhoneRoute)
        }
      }
      #  Give up only after many tries in a row, and say where it stopped.
      if (-not $done) { throw "Could not continue the transfer at byte $offset; the part already sent stays on the phone, so running it again resumes." }
      $size = $offset + $len
      $index++
      #  A hook for tests: it can break the connection between two pieces.
      if ($script:ChunkHook) { & $script:ChunkHook $index }
    }
  } finally { $stream.Dispose(); Remove-Item $tmp -ErrorAction SilentlyContinue }

  #  The whole file must be identical to the PC's copy before it gets its real name.
  $want = (Get-FileHash $local -Algorithm SHA256).Hash.ToLower()
  $got = ((Invoke-AdbRoute { & $Adb shell "sha256sum '$part'" }) -join '').Split(' ')[0].ToLower()
  #  A mismatch removes the part so nothing wrong is ever used.
  if ($got -ne $want) { Invoke-AdbRoute { & $Adb shell "rm -f '$part'" } | Out-Null; throw 'The file on the phone does not match the original (checksum). It was removed; run the transfer again.' }
  Invoke-AdbRoute { & $Adb shell "mv '$part' '$remote'" } | Out-Null
  Invoke-AdbRoute { & $Adb shell "rm -f '$stage'" } | Out-Null
}

#  Lists Termux and its add-on apps as Android knows them: version and signing key (the key shows the source: GitHub and F-Droid builds differ).
function Get-TermuxApps {
  foreach ($name in 'com.termux', 'com.termux.boot', 'com.termux.api', 'com.termux.widget') {
    $dump = Invoke-AdbRoute { & $Adb shell "dumpsys package $name" }
    $key = ($dump | Select-String 'signatures:\[([^\]]*)\]' | Select-Object -First 1)
    $ver = ($dump | Select-String 'versionName=(\S+)' | Select-Object -First 1)
    [pscustomobject]@{ App = $name; Version = $(if ($ver) { $ver.Matches[0].Groups[1].Value } else { '' }); Key = $(if ($key) { $key.Matches[0].Groups[1].Value } else { '' }) }
  }
}

#  Runs the smoke test (or another test script given for testing this helper) and returns $true only when it finished and every check passed.
#  A crash inside the test (a thrown error) counts as a failure instead of stopping the caller, so a deploy still rolls back.
function Invoke-SmokeTest([string]$script = (Join-Path $PSScriptRoot 'smoke-test.ps1')) {
  $global:LASTEXITCODE = 1
  #  Any error thrown inside the test is reported and treated as "did not pass".
  try { & $script | Out-Host; return ($LASTEXITCODE -eq 0) }
  catch { Write-Host "The smoke test crashed, which counts as a failure: $($_.Exception.Message)" -ForegroundColor Red; return $false }
}

#  Sends a command to the app through its protected control entry point (only the adb shell is allowed to use it).
function Send-AppCommand([string]$command, [string[]]$extra = @()) {
  Invoke-AdbRoute { & $Adb shell am start -n "$package/$package.ControlActivity" --es cmd $command @extra } | Out-Null
}

#  Pushes a shell script to the phone, runs it inside Termux through the app, and returns what it printed. Use names like tmp-something.sh (ca-* names are installed as commands).
function Invoke-TermuxScript([string]$localFile, [string]$name, [int]$timeoutSeconds = 180) {
  Invoke-AdbRoute { & $Adb push $localFile "/sdcard/Documents/ca/$name" } | Out-Null
  Send-AppCommand 'run' @('--es', 'script', $name)
  $deadline = (Get-Date).AddSeconds($timeoutSeconds)
  $log = ''
  #  Keep reading the log until the script reports that it ended, or time runs out.
  do {
    Start-Sleep -Seconds 3
    $log = (Invoke-AdbRoute { & $Adb shell cat /sdcard/Documents/ca/run.log }) -join "`n"
  } while ($log -notmatch "(?s)$([regex]::Escape($name)).*== exit" -and (Get-Date) -lt $deadline)
  #  The helper file stays where it is: it has a fixed "tmp-" name, so the next run simply overwrites it and nothing piles up.
  return $log
}

#  Runs a small shell script given as text inside Termux and returns what it printed.
function Invoke-Termux([string]$name, [string]$body, [int]$timeoutSeconds = 180) {
  $local = Join-Path $env:TEMP $name
  #  Termux needs Unix line endings, so write the script without carriage returns.
  [System.IO.File]::WriteAllText($local, ($body -replace "`r`n", "`n"))
  $log = Invoke-TermuxScript $local $name $timeoutSeconds
  Remove-Item $local -ErrorAction SilentlyContinue
  return $log
}

#  Makes the phone buzz three times, so the user notices that something on the phone needs their touch (a fingerprint, an Allow button, an unlock).
function Request-UserAttention([string]$why = '') {
  & $Adb shell 'cmd vibrator_manager synced -f -d PocketIrisNeedsYou waveform 0 250 150 250 150 250' | Out-Null
  #  Also say why, in the console, so the reason is on record next to the buzz.
  if ($why) { Write-Host "ATTENTION (phone buzzed): $why" -ForegroundColor Yellow }
}
