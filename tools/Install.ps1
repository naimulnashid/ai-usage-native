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

  It also registers the app under Settings -> Apps -> Installed apps (and
  Control Panel's Programs and Features), per user, with Uninstall.ps1 copied
  into the program folder as its uninstall command - so it can be removed from
  there without this repo.

  -Uninstall runs that same Uninstall.ps1: it removes the program folder, the
  shortcut, the start-at-login entry and the Installed apps entry. It leaves
  your data (%LOCALAPPDATA%\AI Usage Native: settings, the archive, project
  logos) unless -RemoveData is also given.

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
  # One implementation, shared with the Installed apps entry.
  & (Join-Path $PSScriptRoot 'Uninstall.ps1') -RemoveData:$RemoveData -Quiet
  return
}

$staging = Join-Path $root 'publish\AIUsage'
Write-Host 'Publishing a Release build...'
& dotnet publish (Join-Path $root 'src\UsageApp\UsageApp.csproj') -c Release -o $staging --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
# Without its own resources.pri the app dies at startup (0xC000027B) - which
# is what a publish without EnableMsixTooling produced. Never install that.
if (-not (Test-Path (Join-Path $staging 'AIUsage.pri'))) {
  throw 'The published build has no AIUsage.pri, so it would crash at startup. Check EnableMsixTooling in UsageApp.csproj.'
}

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

Copy-Item (Join-Path $PSScriptRoot 'Uninstall.ps1') (Join-Path $target 'Uninstall.ps1') -Force

$bytes = (Get-ChildItem $target -Recurse | Measure-Object Length -Sum).Sum
$size = $bytes / 1MB

# Settings -> Apps -> Installed apps, per user (HKCU needs no admin rights).
# The uninstall command runs the copy in the program folder, which hands over
# to a copy in %TEMP% before deleting the folder it came from.
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$uninstallCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $target 'Uninstall.ps1')`""
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AIUsage'
New-Item -Path $key -Force | Out-Null
$entry = @{
  DisplayName = 'AI Usage'
  DisplayVersion = [string]$version
  Publisher = 'Naimul Nashid'
  DisplayIcon = "$exe,0"
  InstallLocation = $target
  UninstallString = $uninstallCommand
  QuietUninstallString = "$uninstallCommand -Quiet"
  URLInfoAbout = 'https://github.com/naimulnashid/ai-usage-native'
  InstallDate = (Get-Date -Format 'yyyyMMdd')
}
foreach ($name in $entry.Keys) { New-ItemProperty -Path $key -Name $name -Value $entry[$name] -PropertyType String -Force | Out-Null }
# No Modify or Repair buttons: re-running this script is the repair.
foreach ($name in 'NoModify', 'NoRepair') { New-ItemProperty -Path $key -Name $name -Value 1 -PropertyType DWord -Force | Out-Null }
New-ItemProperty -Path $key -Name 'EstimatedSize' -Value ([int]($bytes / 1KB)) -PropertyType DWord -Force | Out-Null

Write-Host ("Installed to {0} ({1:N0} MB) with a Start menu shortcut and an Installed apps entry." -f $target, $size)
Write-Host 'Start it from the Start menu. "Start at login" is in its Settings menu and the tray menu.'
