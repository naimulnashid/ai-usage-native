<#
.SYNOPSIS
  Removes AI Usage installed by Install.ps1. No admin rights needed.

.DESCRIPTION
  Closes the installed copy (by the path it runs from, never by name), then
  removes the program folder, the Start menu shortcut, the start-at-login
  entry and the "Installed apps" entry. Your data (settings, the archive,
  project logos) is kept unless -RemoveData is given. It is in the folder
  AIUSAGE_DATA_DIR names when that is set, as the app itself reads it, else in
  %LOCALAPPDATA%\AI Usage Native.

  -RemoveData deletes only the files and folders the app writes there, then
  the folder itself if that leaves it empty. AIUSAGE_DATA_DIR can name a folder
  that holds other things too, and those are never touched.

  Install.ps1 copies this script into the program folder and registers it as
  the uninstall command, so Settings -> Apps -> Installed apps (and Control
  Panel's Programs and Features) can remove the app without the source repo.
  Run from there, it first copies itself to %TEMP% and hands over, because it
  is about to delete the folder it lives in.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Uninstall.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Uninstall.ps1 -RemoveData
#>
param(
  [switch]$RemoveData,
  # Close without the final "press a key" pause - for Install.ps1 -Uninstall and scripted use.
  [switch]$Quiet
)
$ErrorActionPreference = 'Stop'

$target = Join-Path $env:LOCALAPPDATA 'Programs\AI Usage'
$exe = Join-Path $target 'AIUsage.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'AI Usage.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AIUsage'

# The data folder as the app resolves it (UsageCore AppPaths.DataDir).
function Get-DataFolder {
  if ($env:AIUSAGE_DATA_DIR) { return $env:AIUSAGE_DATA_DIR }
  return (Join-Path $env:LOCALAPPDATA 'AI Usage Native')
}

# Everything the app writes at the top of its data folder, and the .tmp files
# its atomic writes leave if interrupted. Keep in step with AppPaths and the
# settings stores under src\.
function Get-AppDataEntries([string]$Folder) {
  $names = @('config', 'history', 'logs', 'project-logos', 'app-settings.json',
    'hidden-projects.json', 'codex-hidden-projects.json',
    'model-settings.json', 'codex-model-settings.json',
    'project-colors.json', 'codex-project-colors.json',
    'project-settings.json', 'codex-project-settings.json')
  if (-not (Test-Path -LiteralPath $Folder -PathType Container)) { return @() }
  return @(Get-ChildItem -LiteralPath $Folder -Force | Where-Object {
    $names -contains $_.Name -or ($_.Name -like '*.json.tmp' -and $names -contains $_.Name.Substring(0, $_.Name.Length - 4))
  })
}

# Deletes the app's entries, and the folder only if nothing else is in it.
function Remove-AppData([string]$Folder) {
  $entries = Get-AppDataEntries $Folder
  foreach ($entry in $entries) { Remove-Item -LiteralPath $entry.FullName -Recurse -Force }
  if (-not (Test-Path -LiteralPath $Folder)) { return "No app data found in $Folder" }
  $left = @(Get-ChildItem -LiteralPath $Folder -Force)
  if ($left.Count -eq 0) {
    Remove-Item -LiteralPath $Folder -Force
    return "Removed $Folder"
  }
  if ($entries.Count -eq 0) { return "No app data found in $Folder" }
  return "Removed the app's data from $Folder; $($left.Count) other item(s) there were left alone."
}

# Running from inside the folder about to be deleted: continue from a copy.
if ($PSCommandPath -and $PSCommandPath.StartsWith($target, [StringComparison]::OrdinalIgnoreCase)) {
  $copy = Join-Path $env:TEMP "AIUsage-Uninstall-$PID.ps1"
  Copy-Item $PSCommandPath $copy -Force
  $forward = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$copy`"")
  if ($RemoveData) { $forward += '-RemoveData' }
  if ($Quiet) { $forward += '-Quiet' }
  Set-Location $env:TEMP
  $p = Start-Process powershell.exe -ArgumentList $forward -Wait -PassThru
  Remove-Item $copy -Force -ErrorAction SilentlyContinue
  exit $p.ExitCode
}

try {
  Get-Process AIUsage -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and ($_.Path -ieq $exe) } |
    ForEach-Object {
      Write-Host "Closing the running copy (PID $($_.Id))"
      Stop-Process -Id $_.Id -Force
      $_.WaitForExit(5000) | Out-Null
    }

  if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
  $run = Get-ItemProperty -Path $runKey -Name 'AI Usage' -ErrorAction SilentlyContinue
  if ($run -and $run.'AI Usage' -like "*$exe*") { Remove-ItemProperty -Path $runKey -Name 'AI Usage' }
  if (Test-Path $uninstallKey) { Remove-Item $uninstallKey -Recurse -Force }
  # Left by the release's setup.exe, if it installed here before Install.ps1 did.
  $setupKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AIUsage_is1'
  if (Test-Path $setupKey) { Remove-Item $setupKey -Recurse -Force }
  if (Test-Path $target) { Remove-Item $target -Recurse -Force }
  $data = Get-DataFolder
  if ($RemoveData) { Write-Host (Remove-AppData $data) }
  Write-Host 'AI Usage is uninstalled.' -ForegroundColor Green
  if (-not $RemoveData) { Write-Host "Your settings, archive and logos in $data were kept." }
  $code = 0
}
catch {
  Write-Host "Uninstall failed: $($_.Exception.Message)" -ForegroundColor Red
  $code = 1
}

# Launched from Installed apps, this is a console window of its own: leave the
# result on screen long enough to read.
if (-not $Quiet) { Start-Sleep -Seconds 4 }
exit $code
