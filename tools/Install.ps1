<#
.SYNOPSIS
  Builds the app and installs it for the current user, with a Start menu
  shortcut. No admin rights needed.

.DESCRIPTION
  Publishes a self-contained Release build (it carries its own .NET and Windows
  App SDK runtime) into %LOCALAPPDATA%\Programs\AI Usage, and adds
  "AI Usage" to the Start menu. Re-running updates the installed copy: a
  running copy is closed first, by the path it runs from - never by name, so
  a copy running from somewhere else is left alone.

  -Uninstall removes the program folder, the shortcut and the start-at-login
  entry. It leaves your data (%LOCALAPPDATA%\AI Usage Native: settings, the
  archive, project logos) unless -RemoveData is also given.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Install.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Install.ps1 -Uninstall
#>
param(
  [switch]$Uninstall,
  [switch]$RemoveData
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$target = Join-Path $env:LOCALAPPDATA 'Programs\AI Usage'
$exe = Join-Path $target 'AIUsage.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'AI Usage.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Stop-Installed {
  Get-Process AIUsage -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and ($_.Path -ieq $exe) } |
    ForEach-Object {
      Write-Host "Closing the running copy (PID $($_.Id))"
      Stop-Process -Id $_.Id -Force
      $_.WaitForExit(5000) | Out-Null
    }
}

if ($Uninstall) {
  Stop-Installed
  if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
  $run = Get-ItemProperty -Path $runKey -Name 'AI Usage' -ErrorAction SilentlyContinue
  if ($run -and $run.'AI Usage' -like "*$exe*") { Remove-ItemProperty -Path $runKey -Name 'AI Usage' }
  if (Test-Path $target) { Remove-Item $target -Recurse -Force }
  if ($RemoveData) {
    $data = Join-Path $env:LOCALAPPDATA 'AI Usage Native'
    if (Test-Path $data) { Remove-Item $data -Recurse -Force }
    Write-Host "Removed $data"
  }
  Write-Host 'AI Usage is uninstalled.'
  return
}

$staging = Join-Path $root 'publish\AIUsage'
Write-Host 'Publishing a Release build...'
& dotnet publish (Join-Path $root 'src\UsageApp\UsageApp.csproj') -c Release -o $staging --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Stop-Installed
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $staging '*') $target -Recurse -Force

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $target
$link.IconLocation = "$exe,0"
$link.Description = 'Claude Code and Codex usage, cost and runtime'
$link.Save()

$size = (Get-ChildItem $target -Recurse | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Installed to {0} ({1:N0} MB) with a Start menu shortcut." -f $target, $size)
Write-Host 'Start it from the Start menu. "Start at login" is in its Settings menu and the tray menu.'
