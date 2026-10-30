<#
.SYNOPSIS
  Runs quietly in the background on the PC. When you choose a Debugging link (Wi-Fi, Wi-Fi + Bluetooth, Bluetooth only) in the
  PocketIris app, the phone sends a small "doorbell" message over Wi-Fi and this script opens phone-link.ps1 in a visible window.
.DESCRIPTION
  Started automatically at Windows sign-in by a shortcut in the Startup folder (made by Install-Bluetooth-Watcher.cmd).
  It only listens for one fixed text (plus the mode) on UDP port 3498 and only ever starts phone-link.ps1; it carries no secrets.
  The first time, Windows Firewall asks whether PowerShell may receive messages on private networks: choose Allow.
  To stop it for good: delete the shortcut "PocketIris Bluetooth watcher" from the Startup folder (Win+R, shell:startup).
#>
$port = 3498
$magic = 'pocketiris-btdebug-on'
$script = Join-Path $PSScriptRoot 'phone-link.ps1'
$last = [DateTime]::MinValue

#  Only one watcher at a time: a second copy exits straight away.
$mutex = New-Object System.Threading.Mutex($false, 'Global\PocketIrisBtWatch')
if (-not $mutex.WaitOne(0)) { return }

#  Listen for the phone's doorbell for as long as Windows is running.
$udp = New-Object System.Net.Sockets.UdpClient($port)
while ($true) {
  $from = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
  $bytes = $udp.Receive([ref]$from)
  $text = [System.Text.Encoding]::ASCII.GetString($bytes).Trim()
  $parts = $text -split '\|'

  #  Only the exact doorbell text is accepted, with one of the three known modes (older phones sent no mode: treat that as Wi-Fi + Bluetooth).
  $mode = if ($parts.Count -gt 1) { $parts[1] } else { 'wifibt' }
  if ($parts[0] -eq $magic -and @('wifi', 'wifibt', 'bt') -contains $mode -and ((Get-Date) - $last).TotalSeconds -gt 60) {
    $last = Get-Date
    $launch = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-File', "`"$script`"", '-Mode', $mode, '-Smoke')

    #  In Wi-Fi mode the doorbell's sender is the phone itself, so tell the script its address.
    if ($mode -eq 'wifi') { $launch += @('-PhoneIp', $from.Address.ToString()) }
    Start-Process powershell -ArgumentList $launch
  }
}
