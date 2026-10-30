<#
.SYNOPSIS
  Connects this PC to the phone without the USB cable, in the mode you chose on the phone (PocketIris tab > Developer >
  "Connect to another device by"), then makes cdp.ps1 and smoke-test.ps1 use that connection.
.DESCRIPTION
  Modes:
    wifi    phone and PC on the same Wi-Fi. No Bluetooth, no Windows click.
    wifibt  over the Bluetooth network (the phone must also be on some Wi-Fi). Needs one Windows click to join the network.
    bt      over the Bluetooth network only, using adb's classic port 5555. With the USB cable plugged in, this script runs
            "adb tcpip 5555" for you (needed once after every phone restart).
  Examples:
    .\phone-link.ps1 -Mode wifi -Smoke
    .\phone-link.ps1 -Mode wifibt
    .\phone-link.ps1 -Mode bt
    .\phone-link.ps1 -Off          forget the connection and go back to USB
    .\phone-link.ps1 -Mode wifi -PhoneIp 192.168.1.7 -Port 40719     skip the automatic steps (manual fallback)
#>
param(
  [ValidateSet('wifi', 'wifibt', 'bt')][string]$Mode = 'wifibt',
  [string]$PhoneIp,
  [int]$Port = 0,
  [switch]$Smoke,
  [switch]$Off,
  [string]$Adb = ''
)
$ErrorActionPreference = 'Stop'
#  The shared helpers find adb when no path was given (environment variable, tools\local.ps1, PATH or the Android SDK).
. (Join-Path $PSScriptRoot 'lib.ps1')
$serialFile = Join-Path $PSScriptRoot '.adb-serial'
$lanFile = Join-Path $PSScriptRoot '.phone-lan'

#  "Off" forgets the saved connection so the tools use USB again.
if ($Off) {
  if (Test-Path $serialFile) { & $Adb disconnect (Get-Content $serialFile -Raw).Trim() | Out-Null; Remove-Item $serialFile }
  Write-Host 'Back to USB. (Choose Off for the Debugging link on the phone when you are done.)'
  return
}

#  Returns the Bluetooth network adapter if it is connected, otherwise nothing.
function Get-BtAdapter { Get-NetAdapter | Where-Object { $_.InterfaceDescription -match 'Bluetooth' -and $_.Status -eq 'Up' } | Select-Object -First 1 }

#  Asks the phone which debugging ports it offers; returns an empty list if it does not answer.
function Get-PhonePorts($ip) {
  try { return @((Invoke-RestMethod "http://${ip}:3499/" -TimeoutSec 40).ports) } catch { return @() }
}

#  Makes sure this PC is on the phone's Bluetooth network and returns the phone's address on it.
function Join-BluetoothNetwork {
  $bt = Get-BtAdapter

  #  Windows never joins the phone's network by itself and has no command for it: open Devices and Printers and wait for one click.
  if (-not $bt) {
    Write-Host 'Windows needs one click: in the window that just opened, right-click your phone > Connect using > Access point.' -ForegroundColor Yellow
    Write-Host '(I carry on by myself as soon as it connects; waiting up to 90 s.)'
    Start-Process 'shell:::{A8A91A66-3A7D-4424-8D24-04E180695C7A}'
    for ($i = 0; $i -lt 90 -and -not $bt; $i++) { $bt = Get-BtAdapter; if (-not $bt) { Start-Sleep 1 } }
  }
  if (-not $bt) { throw 'The PC did not join the phone''s Bluetooth network. Is the Debugging link on the phone set to a Bluetooth mode (and Bluetooth tethering on)?' }

  #  The gateway of that network is the phone; wait for it to get an address.
  $ip = $null
  for ($i = 0; $i -lt 15 -and -not $ip; $i++) {
    $ip = (Get-NetIPConfiguration -InterfaceIndex $bt.ifIndex).IPv4DefaultGateway.NextHop
    if (-not $ip) { Start-Sleep 1 }
  }
  if (-not $ip) { throw "The Bluetooth adapter '$($bt.Name)' has no gateway yet. Wait a few seconds and run this again." }
  Write-Host "Bluetooth network is up; the phone is at $ip"
  return $ip
}

#  Wi-Fi: work out the phone's address and ports, from the doorbell address, adb's own network search, or the last known address.
$candidates = @()
if ($Mode -eq 'wifi') {
  if (-not $PhoneIp -and (Test-Path $lanFile)) { $PhoneIp = (Get-Content $lanFile -Raw).Trim() }

  #  adb's own search (mDNS) lists the phone's Wireless debugging service together with its port.
  if (-not $PhoneIp -or $Port -le 0) {
    $found = (& $Adb mdns services 2>&1) | Select-String '_adb-tls-connect' | ForEach-Object { if ($_ -match '(\d+\.\d+\.\d+\.\d+):(\d+)') { [pscustomobject]@{ Ip = $Matches[1]; Port = [int]$Matches[2] } } }
    if ($found) {
      if (-not $PhoneIp) { $PhoneIp = @($found)[0].Ip }
      $candidates += @($found | Where-Object { $_.Ip -eq $PhoneIp } | ForEach-Object { $_.Port })
    }
  }
  if (-not $PhoneIp) { throw 'Could not find the phone on the Wi-Fi. Is the Debugging link on the phone set to Wi-Fi, and are both on the same network? (You can also pass -PhoneIp.)' }
  Set-Content -Path $lanFile -Value $PhoneIp -Encoding ascii
}

#  Bluetooth: join the network first.
if ($Mode -ne 'wifi' -and -not $PhoneIp) { $PhoneIp = Join-BluetoothNetwork }

#  Bluetooth only: adb's classic port 5555 must be open; with the USB cable plugged in this script opens it (once per phone restart).
if ($Mode -eq 'bt') {
  $usb = (& $Adb devices) | Select-String '\sdevice$' | Where-Object { $_ -notmatch ':\d+\s' } | ForEach-Object { ($_ -split '\s+')[0] } | Select-Object -First 1
  $open = (Test-NetConnection -ComputerName $PhoneIp -Port 5555 -WarningAction SilentlyContinue).TcpTestSucceeded
  if (-not $open -and $usb) {
    Write-Host "Opening adb's classic network port 5555 through the USB cable (this lasts until the phone restarts)..."
    & $Adb -s $usb tcpip 5555 | Out-Null
    Start-Sleep 4
  }
  elseif (-not $open) { Write-Host 'Port 5555 is closed. Plug in the USB cable once and run this again, so it can open the port.' -ForegroundColor Yellow }
}

#  Ask the phone which ports it offers (unless a port was given or mDNS already told us).
if ($Port -gt 0) { $candidates = @($Port) }
elseif ($candidates.Count -eq 0) {
  $candidates = Get-PhonePorts $PhoneIp
  if ($candidates.Count -eq 0 -and $Mode -eq 'bt') { $candidates = @(5555) }
  if ($candidates.Count -eq 0) { throw 'The phone did not offer a debugging port. Is the Debugging link on the phone set to this mode (and did you allow it with your lock screen)?' }
}
Write-Host "Trying the phone's ports: $($candidates -join ', ')"

#  Try the offered ports one after another until adb is really connected.
$target = $null
foreach ($candidate in $candidates) {
  $try = "${PhoneIp}:$candidate"
  & $Adb connect $try | Out-Null
  Start-Sleep 2
  $answer = (& $Adb -s $try shell 'echo link-ok' 2>&1) -join ''
  if ($answer -match 'link-ok') { $target = $try; break }
  & $Adb disconnect $try | Out-Null
}
if (-not $target) {
  throw "adb could not connect to any offered port. If Android asks 'Allow debugging?' on the phone, accept it (tick 'Always allow'). If this PC was never paired for Wireless debugging: Developer options > Wireless debugging > Pair device with pairing code, then run: adb pair <ip>:<pairing port>"
}

#  Remember the connection for the other tools and tell the user what to do for plain adb commands.
Set-Content -Path $serialFile -Value $target -Encoding ascii
Write-Host "Connected ($Mode): $target" -ForegroundColor Green
#  Say hello over the new wireless connection, so the phone can show "Connected to device NAME" under Wirelessly.
try { & $Adb -s $target shell am broadcast -a io.github.alastergr.pocketiris.HELLO -n io.github.alastergr.pocketiris/io.github.alastergr.pocketiris.HelloReceiver --es pc $env:COMPUTERNAME --es route wireless | Out-Null } catch { }
Write-Host "cdp.ps1 and smoke-test.ps1 now use it. For plain adb commands in this window: `$env:ANDROID_SERIAL = '$target'"
if ($Smoke) { & (Join-Path $PSScriptRoot 'smoke-test.ps1') }
