<#
.SYNOPSIS
  Launches the app (or attaches to a running copy), captures its window to a
  PNG, and optionally closes it again.

.DESCRIPTION
  Used while developing to see the UI without a person at the screen. Captures
  with PrintWindow's PW_RENDERFULLCONTENT flag, which is what includes WinUI's
  composition-rendered content; a plain screen grab would also pick up any
  window lying on top of it.

  Screenshots go to screenshots/, which is gitignored: pointed at real
  transcripts, they show real projects and spend.
#>
param(
  [string]$Exe,
  [string]$Out = 'screenshots\window.png',
  [int]$Width = 1400,
  [int]$Height = 1000,
  [int]$WaitSeconds = 6,
  [string[]]$Arguments = @(),
  [switch]$Close
)

$ErrorActionPreference = 'Stop'

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class WindowCapture {
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
  [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

  public static void Save(IntPtr h, string path) {
    RECT r; GetWindowRect(h, out r);
    using (var bmp = new Bitmap(r.R - r.L, r.B - r.T))
    using (var g = Graphics.FromImage(bmp)) {
      IntPtr dc = g.GetHdc();
      PrintWindow(h, dc, 2); // PW_RENDERFULLCONTENT
      g.ReleaseHdc(dc);
      bmp.Save(path, ImageFormat.Png);
    }
  }
}
'@

# Without this PowerShell is DPI-unaware, so on a scaled display the window
# rectangle comes back in logical pixels and the capture is cropped. -4 is
# DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2. -Width/-Height are then physical.
[void][WindowCapture]::SetProcessDpiAwarenessContext([IntPtr]::new(-4))

$proc = $null
if ($Exe) {
  $proc = if ($Arguments.Count) { Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru } else { Start-Process -FilePath $Exe -PassThru }
} else {
  $proc = Get-Process -Name AIUsage -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $proc) { throw 'No -Exe given and no running AIUsage process.' }
}

$deadline = (Get-Date).AddSeconds(30)
while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) {
  Start-Sleep -Milliseconds 250
  $proc.Refresh()
  if ($proc.HasExited) { throw "The app exited with code $($proc.ExitCode) before showing a window." }
}
if ($proc.MainWindowHandle -eq 0) { throw 'No window appeared within 30 seconds.' }

$h = $proc.MainWindowHandle
[void][WindowCapture]::SetWindowPos($h, [IntPtr]::Zero, 40, 40, $Width, $Height, 0x0004) # SWP_NOZORDER
[void][WindowCapture]::SetForegroundWindow($h)
Start-Sleep -Seconds $WaitSeconds

$full = [System.IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force (Split-Path $full) | Out-Null
[WindowCapture]::Save($h, $full)
Write-Output "Saved $full (PID $($proc.Id))"

if ($Close) {
  # By PID, never by name: another copy may be the one someone is using.
  Stop-Process -Id $proc.Id -Force
}
