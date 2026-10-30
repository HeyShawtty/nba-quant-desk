# Cloud runner for the NBA Quant Desk (GitHub Actions, Windows, clock set to US Eastern).
#   learn : grade yesterday (builds the game review), recalibrate on every completed game, validate, audit, data bank
#   slate : official injury report -> overrides, price today's slate (report, predictions, website data), four-leg tickets
#   test  : walk-forward backtest of the current settings plus each "--set Key=Value" variant passed in -Variants
param([string]$Task = 'slate', [string]$Date = '', [string]$Variants = '')
$ErrorActionPreference = 'Continue'
Set-Location (Split-Path -Parent $PSScriptRoot)
$cli = Join-Path (Get-Location) 'nbaquant-cli.exe'
$now = Get-Date
if (-not $Date) { if ($Task -eq 'learn') { $Date = $now.AddDays(-1).ToString('yyyy-MM-dd') } else { $Date = $now.ToString('yyyy-MM-dd') } }
Write-Host "== $Task for $Date (runner clock $($now.ToString('yyyy-MM-dd HH:mm')) ET)"

function Step($label, [scriptblock]$body) {
  Write-Host "::group::$label"
  $sw = [Diagnostics.Stopwatch]::StartNew()
  & $body
  Write-Host ("{0} took {1:N1} min" -f $label, $sw.Elapsed.TotalMinutes)
  Write-Host "::endgroup::"
}

switch ($Task) {
  'learn' {
    Step "Grade $Date" { & $cli grade $Date }
    Step 'Recalibrate (all completed games)' { & $cli recalibrate }
    Step 'Validate with parlays' { & $cli validate }
    Step 'Audit' { & $cli audit }
    Step 'Data bank' { & $cli bank }
  }
  'slate' {
    Step 'Official injury report' { & $cli injuries $Date }
    Step "Price $Date" { & $cli run $Date }
    $ymd = $Date.Replace('-', '')
    $sb = Invoke-RestMethod "https://site.api.espn.com/apis/site/v2/sports/basketball/nba/scoreboard?dates=$ymd"
    foreach ($e in $sb.events) {
      if ($e.status.type.state -ne 'pre') { continue }
      $c = $e.competitions[0]
      $hm = ($c.competitors | Where-Object homeAway -eq 'home').team.abbreviation
      $aw = ($c.competitors | Where-Object homeAway -eq 'away').team.abbreviation
      Step "Four-leg tickets $aw@$hm" { & $cli sgp4 $Date "$aw@$hm" }
    }
  }
  'test' {
    Step 'Backtest: current settings' { & $cli validate | Select-String -Pattern '^\s+(PROPS|ML|SPREAD|TOTAL|SGP|PTS|REB|AST|3PM|PRA|DD|Q3ALL)\s|Reliability' }
    foreach ($v in ($Variants -split ';' | Where-Object { $_.Trim() })) {
      $a = @(); foreach ($kv in ($v.Trim() -split '\s+')) { $a += '--set'; $a += $kv }
      Step "Backtest: $v" { & $cli validate @a | Select-String -Pattern '^\s+(PROPS|ML|SPREAD|TOTAL|SGP|PTS|REB|AST|3PM|PRA|DD|Q3ALL)\s|Reliability' }
    }
  }
}

# website for GitHub Pages: the page plus the same skeleton the claude.ai host adds
$pages = Join-Path (Get-Location) '_pages'
New-Item -ItemType Directory -Force $pages | Out-Null
if (Test-Path 'site\data.js') {
  $body = Get-Content 'site\index.html' -Raw -Encoding UTF8
  $html = '<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover"><style>:root{color-scheme:light}body{margin:0;font:14px system-ui}img{max-width:100%}[hidden]{display:none!important}</style></head><body>' + $body + '</body></html>'
  [IO.File]::WriteAllText((Join-Path $pages 'index.html'), $html, (New-Object Text.UTF8Encoding $false))
  Copy-Item 'site\data.js' (Join-Path $pages 'data.js') -Force
}
exit 0
