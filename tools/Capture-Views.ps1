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
  [int]$WaitSeconds = 7
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root "src\UsageApp\bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64\AIUsage.exe"
$env:CLAUDE_CONFIG_DIR = Join-Path $root 'demo-data\claude'
$env:CODEX_HOME = Join-Path $root 'demo-data\codex'
$env:AIUSAGE_DATA_DIR = Join-Path $root 'demo-data\appdata'

foreach ($view in $Views) {
  $name, $spec = $view -split '=', 2
  $env:AIUSAGE_DEBUG_VIEW = $spec
  # Windows PowerShell 5.1, which has System.Drawing built in; PowerShell 7
  # would need System.Drawing.Common referenced by hand.
  powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Capture-Window.ps1') -Exe $exe -Out (Join-Path $root "screenshots\$name.png") -Width $Width -Height $Height -WaitSeconds $WaitSeconds -Close
  Start-Sleep -Milliseconds 800
}
