<#
.SYNOPSIS
  Photographs several views of the app on the demo data, one launch each.

.DESCRIPTION
  Each entry is "name=agent,page[,projectId],scroll" (see AIUSAGE_DEBUG_VIEW in
  MainWindow). Runs against ./demo-data with its own AIUSAGE_DATA_DIR, so the
  real archive is never touched. Screenshots land in screenshots/ (gitignored).
#>
param(
  [string[]]$Views = @('overview-top=claude,overview,0'),
  [string]$Configuration = 'Debug',
  [int]$Width = 2160,
  [int]$Height = 1500,
  [int]$WaitSeconds = 7,
  # Capture each page top to bottom at this width (in DIPs): the app grows its
  # window to the page's full height (AIUSAGE_DEBUG_FULLPAGE). 0 = one screen.
  [int]$FullPage = 0
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root "src\UsageApp\bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64\AIUsage.exe"
$env:CLAUDE_CONFIG_DIR = Join-Path $root 'demo-data\claude'
$env:CODEX_HOME = Join-Path $root 'demo-data\codex'
$env:AIUSAGE_DATA_DIR = Join-Path $root 'demo-data\appdata'
$env:AIUSAGE_DEBUG_FULLPAGE = if ($FullPage -gt 0) { "$FullPage" } else { $null }
# @(...) around the if: a one-item result would otherwise unroll to a string,
# which cannot be splatted.
$sizing = @(if ($FullPage -gt 0) { '-AppSized' } else { '-Width', $Width, '-Height', $Height })

foreach ($view in $Views) {
  # "name=spec" or "name=spec@x:y" to hover at (x, y) before the capture.
  $name, $spec = $view -split '=', 2
  $hoverX = -1; $hoverY = -1
  if ($spec -match '^(.*)@(\d+):(\d+)$') { $spec = $Matches[1]; $hoverX = [int]$Matches[2]; $hoverY = [int]$Matches[3] }
  $env:AIUSAGE_DEBUG_VIEW = $spec
  # Windows PowerShell 5.1, which has System.Drawing built in; PowerShell 7
  # would need System.Drawing.Common referenced by hand.
  powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Capture-Window.ps1') -Exe $exe -Out (Join-Path $root "screenshots\$name.png") @sizing -WaitSeconds $WaitSeconds -HoverX $hoverX -HoverY $hoverY -Close
  Start-Sleep -Milliseconds 800
}
