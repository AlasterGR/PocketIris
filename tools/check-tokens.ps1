<#
.SYNOPSIS
  Fails when a colour or size literal appears outside the token files (the two-tier token rule).
.DESCRIPTION
  Native screens (app\*.cs): no hex colour, no Color.ParseColor / Color.White / Color.Black / Color.Transparent, no number inside Dp(...),
  no number after TextSize =, no number as a corner radius. Only app\Tokens.cs may contain such values.
  Our own web page (app\Assets\pocketiris\*.css, *.html, *.js, when it exists): no hex colour, no px/rem/em number, no reference to a
  tier-1 variable (--pc-raw-*); pages use tier-2 variables (--pc-sys-*) only. The generated token file tokens.css is exempt.
  The shortcut added to Yep's chat page (app\Assets\yepshortcut\*.css and *.js): no hex colour and no px/rem/em number; use Yep's own variables.
  Exit code 0 = clean, 1 = findings (each printed as file:line and the offending text).
.EXAMPLE
  tools\check-tokens.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$findings = 0

#  Prints one finding and counts it.
function Report([string]$file, [int]$line, [string]$text, [string]$why) {
  Write-Host ("{0}:{1}  {2}   <- {3}" -f $file.Substring($root.Length + 1), $line, $text.Trim(), $why) -ForegroundColor Red
  $script:findings++
}

#  Checks every line of the given files against a list of pattern and reason pairs.
function Scan([string[]]$files, [object[]]$rules) {
  foreach ($file in $files) {
    $n = 0
    foreach ($line in [System.IO.File]::ReadAllLines($file)) {
      $n++
      #  Comment-only lines are not code, so they are skipped.
      if ($line.TrimStart().StartsWith('//') -or $line.TrimStart().StartsWith('/*') -or $line.TrimStart().StartsWith('*')) { continue }
      foreach ($rule in $rules) { if ($line -match $rule[0]) { Report $file $n $line $rule[1] } }
    }
  }
}

#  Native screens: everything except the token file itself.
$native = Get-ChildItem (Join-Path $root 'app') -Filter '*.cs' | Where-Object { $_.Name -ne 'Tokens.cs' } | ForEach-Object { $_.FullName }
Scan $native @(
  @('"#[0-9A-Fa-f]{3,8}"', 'hex colour: use a Tokens.Sys colour'),
  @('Color\.(ParseColor|White|Black|Transparent|Red|Green|Blue|Gray|Yellow)\b', 'colour literal: use a Tokens.Sys colour'),
  @('\bDp\(\s*-?\d', 'size literal: use Tokens.Space, Tokens.Radius or Tokens.Size'),
  @('\bTextSize\s*=\s*\d', 'text size literal: use Tokens.Text'),
  @('SetCornerRadius\(\s*\d', 'radius literal: use Tokens.Radius')
)

#  Our own page: only tier-2 variables, no literals.
$pageDir = Join-Path $root 'app\Assets\pocketiris'
if (Test-Path $pageDir) {
  $page = Get-ChildItem $pageDir -Recurse -Include *.css, *.html, *.js | Where-Object { $_.Name -ne 'tokens.css' } | ForEach-Object { $_.FullName }
  Scan $page @(
    @('#[0-9A-Fa-f]{3,8}\b', 'hex colour: use a --pc-sys-* variable'),
    @('\b\d+(\.\d+)?(px|rem|em)\b', 'size literal: use a --pc-sys-* variable'),
    @('--pc-raw-', 'tier-1 variable used directly: use --pc-sys-*')
  )
}

#  The shortcut the app adds to Yep's chat page: Yep's own variables, no literals.
$shortcutDir = Join-Path $root 'app\Assets\yepshortcut'
if (Test-Path $shortcutDir) {
  $shortcut = Get-ChildItem $shortcutDir -Recurse -Include *.css, *.js | ForEach-Object { $_.FullName }
  Scan $shortcut @(
    @('#[0-9A-Fa-f]{3,8}', 'hex colour: use one of Yep''s variables'),
    @('\d+(\.\d+)?(px|rem|em)', 'size literal: use one of Yep''s variables')
  )
}

#  The verdict.
if ($findings -gt 0) { Write-Host "$findings token leak(s)." -ForegroundColor Red; exit 1 }
Write-Host 'No token leaks: no colour or size literal outside the token files.' -ForegroundColor Green
exit 0
