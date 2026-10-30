<#
.SYNOPSIS
  Old name, kept so existing shortcuts keep working: connects in the "Wi-Fi + Bluetooth" mode. Use phone-link.ps1 for all modes.
#>
param(
  [string]$PhoneIp,
  [int]$Port = 0,
  [switch]$Smoke,
  [switch]$Off
)

#  Hand everything over to the new script, in the Wi-Fi + Bluetooth mode.
& (Join-Path $PSScriptRoot 'phone-link.ps1') -Mode wifibt @PSBoundParameters
