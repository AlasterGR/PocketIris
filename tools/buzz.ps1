<#
.SYNOPSIS
  Makes the phone buzz three times to get the user's attention. Run it BEFORE anything that needs a touch on the phone
  (Allow, a fingerprint, unlocking, switching the accessibility service on), then say what is needed.
.EXAMPLE
  tools\buzz.ps1 -Why "touch the fingerprint sensor to start the trusted session"
#>
param([string]$Why = '', [string]$Adb = '')
. (Join-Path $PSScriptRoot 'lib.ps1')
Use-SavedPhone
Request-UserAttention $Why
