<#
.SYNOPSIS
  Inspect and drive the page inside the PocketIris app over USB debugging (Chrome DevTools protocol).
.DESCRIPTION
  Requires debugging switched on once:  adb shell am start -n io.github.alastergr.pocketiris/io.github.alastergr.pocketiris.ControlActivity --es cmd debug-on
  Examples:
    .\cdp.ps1 -Expr 'document.title'
    .\cdp.ps1 -Expr 'document.querySelector(".session-header-inner").getBoundingClientRect().height'
    .\cdp.ps1 -Screenshot C:\path\page.png
    .\cdp.ps1 -Click 'button[aria-label="New session"]'
    .\cdp.ps1 -Type 'hello' -Selector 'textarea'
#>
param(
  [string]$Expr,
  [string]$Screenshot,
  [string]$Click,
  [string]$Type,
  [string]$Selector,
  [int]$Port = 9333,
  [string]$UrlLike = 'http://127.0.0.1:3400*',
  [string]$PageTitle = '',
  [string]$Adb = ''
)
$ErrorActionPreference = 'Stop'
#  Use the saved Wi-Fi or Bluetooth address only while that phone is really connected; otherwise adb picks the USB phone.
. (Join-Path $PSScriptRoot 'lib.ps1')
Use-SavedPhone
$procId = (& $Adb shell "pidof $package").Trim()
if (-not $procId) { throw "$package is not running" }
& $Adb forward "tcp:$Port" "localabstract:webview_devtools_remote_$procId" | Out-Null
$pages = Invoke-RestMethod "http://127.0.0.1:$Port/json" -TimeoutSec 5
$page = $pages | Where-Object { $_.type -eq 'page' -and ($(if ($PageTitle) { $_.title -like $PageTitle } else { $_.url -like $UrlLike })) } | Select-Object -First 1
#  No silent fallback to another page: a check meant for the chat must never run on the PocketIris page (or the reverse) and pass by accident.
if (-not $page) { throw "No page matches $(if ($PageTitle) { "title '$PageTitle'" } else { "address '$UrlLike'" }) (is the chat loaded? is debugging on? run: --es cmd debug-on, then reopen the app)" }

$ws = New-Object System.Net.WebSockets.ClientWebSocket
$ws.ConnectAsync([Uri]$page.webSocketDebuggerUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
$script:id = 0
function Send-Cdp([string]$method, $params = @{}) {
  $script:id++
  $json = @{ id = $script:id; method = $method; params = $params } | ConvertTo-Json -Depth 10 -Compress
  $bytes = [Text.Encoding]::UTF8.GetBytes($json)
  $ws.SendAsync([ArraySegment[byte]]$bytes, 'Text', $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
  $buffer = New-Object byte[] 1048576
  while ($true) {
    $sb = New-Object Text.StringBuilder
    do {
      $r = $ws.ReceiveAsync([ArraySegment[byte]]$buffer, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
      [void]$sb.Append([Text.Encoding]::UTF8.GetString($buffer, 0, $r.Count))
    } until ($r.EndOfMessage)
    $msg = $sb.ToString() | ConvertFrom-Json
    if ($msg.id -eq $script:id) { return $msg }
  }
}
function Eval-Js([string]$js) {
  $res = Send-Cdp 'Runtime.evaluate' @{ expression = $js; returnByValue = $true; awaitPromise = $true }
  if ($res.result.exceptionDetails) { throw ($res.result.exceptionDetails.exception.description) }
  return $res.result.result.value
}

try {
  if ($Screenshot) {
    $res = Send-Cdp 'Page.captureScreenshot' @{ format = 'png' }
    [IO.File]::WriteAllBytes($Screenshot, [Convert]::FromBase64String($res.result.data))
    "saved $Screenshot"
  }
  if ($Click) {
    $sel = $Click | ConvertTo-Json -Compress
    Eval-Js "(()=>{const e=document.querySelector($sel); if(!e) return 'not found'; e.click(); return 'clicked '+(e.textContent||'').slice(0,40)})()"
  }
  if ($Type) {
    $sel = ($(if ($Selector) { $Selector } else { 'textarea' })) | ConvertTo-Json -Compress
    $txt = $Type | ConvertTo-Json -Compress
    Eval-Js "(()=>{const e=document.querySelector($sel); if(!e) return 'not found'; e.focus(); const set=Object.getOwnPropertyDescriptor(e.__proto__,'value').set; set.call(e,$txt); e.dispatchEvent(new Event('input',{bubbles:true})); return 'typed'})()"
  }
  if ($Expr) {
    $v = Eval-Js $Expr
    if ($v -is [string]) { $v } else { $v | ConvertTo-Json -Depth 6 -Compress }
  }
} finally {
  $ws.Dispose()
  & $Adb forward --remove "tcp:$Port" | Out-Null
}
