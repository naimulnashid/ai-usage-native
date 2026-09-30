<#
.SYNOPSIS
  Regenerates the README's screenshots: full pages, on invented data.

.DESCRIPTION
  Builds the app, writes fresh demo transcripts (so the dates end today),
  captures each page top to bottom at 1440 DIPs wide, and converts the
  captures to WebP in docs/screenshots/.

  Every number in them is invented. The demo gets its own data folder
  (AIUSAGE_DATA_DIR, set by Capture-Views.ps1), so the real archive is never
  touched, and its own single-instance key, so it runs beside a copy in the
  tray. A capture of real transcripts would show real projects, paths and
  spend - never commit one.

  The width is in DIPs; the images come out at the display's scale (2160 px
  wide at 150%).
#>
param([int]$Width = 1440)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

dotnet build (Join-Path $root 'src\UsageApp') -p:Platform=x64 --nologo -v q
if ($LASTEXITCODE) { throw 'Build failed.' }

# Fresh demo data, including a fresh demo archive: an old one would keep days
# from a previous run next to the new ones.
Remove-Item -Recurse -Force (Join-Path $root 'demo-data') -ErrorAction SilentlyContinue
dotnet run --project (Join-Path $root 'src\UsageCli') -- demo-data | Out-Null
if ($LASTEXITCODE) { throw 'demo-data failed.' }

$shots = [ordered]@{
  'overview-claude' = 'claude,overview,0'
  'projects-claude' = 'claude,projects,0'
  'overview-codex'  = 'codex,overview,0'
}
& (Join-Path $PSScriptRoot 'Capture-Views.ps1') -Views ($shots.GetEnumerator() | ForEach-Object { "readme-$($_.Key)=$($_.Value)" }) -FullPage $Width -WaitSeconds 8

# One shot in the light theme. The theme is an app setting, read at launch
# from the demo's own data folder, so it is written there first - the dark
# shots above ran on the default.
$appData = Join-Path $root 'demo-data\appdata'
New-Item -ItemType Directory -Force $appData | Out-Null
Set-Content -Path (Join-Path $appData 'app-settings.json') -Value '{ "Theme": "light", "TrayNoteShown": true }'
& (Join-Path $PSScriptRoot 'Capture-Views.ps1') -Views @('readme-overview-claude-light=claude,overview,0') -FullPage $Width -WaitSeconds 8
Remove-Item (Join-Path $appData 'app-settings.json')
$shots['overview-claude-light'] = 'claude,overview,0'

$pairs = foreach ($name in $shots.Keys) {
  Join-Path $root "screenshots\readme-$name.png"
  Join-Path $root "docs\screenshots\$name.webp"
}
dotnet run (Join-Path $PSScriptRoot 'to-webp.cs') -- @pairs
