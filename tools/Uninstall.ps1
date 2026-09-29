<#
.SYNOPSIS
  Removes AI Usage installed by Install.ps1. No admin rights needed.

.DESCRIPTION
  Closes the installed copy (by the path it runs from, never by name), then
  removes the program folder, the Start menu shortcut, the start-at-login
  entry and the "Installed apps" entry. Your data (%LOCALAPPDATA%\AI Usage
  Native: settings, the archive, project logos) is kept unless -RemoveData is
  given.

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
  if (Test-Path $target) { Remove-Item $target -Recurse -Force }
  if ($RemoveData) {
    $data = Join-Path $env:LOCALAPPDATA 'AI Usage Native'
    if (Test-Path $data) { Remove-Item $data -Recurse -Force }
    Write-Host "Removed $data"
  }
  Write-Host 'AI Usage is uninstalled.' -ForegroundColor Green
  if (-not $RemoveData) { Write-Host 'Your settings, archive and logos in %LOCALAPPDATA%\AI Usage Native were kept.' }
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
