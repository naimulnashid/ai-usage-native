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
  # Move the pointer to this point (physical pixels from the window's top-left)
  # before capturing, to photograph a hover state. -1 leaves it alone.
  [int]$HoverX = -1,
  [int]$HoverY = -1,
  # Maximise instead of sizing to -Width x -Height.
  [switch]$Maximize,
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
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
  [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
  [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }

  // SendInput rather than SetCursorPos: only real input produces the pointer
  // events WinUI's hover states listen for.
  static void MoveTo(int x, int y) {
    int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
    var input = new INPUT { type = 0 };
    input.mi.dx = (int)((x - vx) * 65535.0 / (vw - 1));
    input.mi.dy = (int)((y - vy) * 65535.0 / (vh - 1));
    input.mi.flags = 0x0001 | 0x8000 | 0x4000; // MOVE | ABSOLUTE | VIRTUALDESK
    SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
  }

  public static void Hover(IntPtr h, int x, int y) {
    RECT r; GetWindowRect(h, out r);
    for (int i = 6; i >= 0; i--) {
      MoveTo(r.L + x - i * 4, r.T + y - i * 2);
      System.Threading.Thread.Sleep(40);
    }
  }
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
if ($Maximize) {
  [void][WindowCapture]::ShowWindow($h, 3) # SW_MAXIMIZE
} elseif ($HoverX -ge 0) {
  # Topmost for a hover capture: Windows will not hand focus to a background
  # launch, so without this the pointer lands on whatever window is in front.
  [void][WindowCapture]::SetWindowPos($h, [IntPtr]::new(-1), 40, 40, $Width, $Height, 0)
} else {
  [void][WindowCapture]::SetWindowPos($h, [IntPtr]::Zero, 40, 40, $Width, $Height, 0x0004) # SWP_NOZORDER
}
[void][WindowCapture]::SetForegroundWindow($h)
Start-Sleep -Seconds $WaitSeconds
if ($HoverX -ge 0 -and $HoverY -ge 0) {
  [WindowCapture]::Hover($h, $HoverX, $HoverY)
  Start-Sleep -Milliseconds 1200
}

$full = [System.IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force (Split-Path $full) | Out-Null
[WindowCapture]::Save($h, $full)
Write-Output "Saved $full (PID $($proc.Id))"

if ($Close) {
  # By PID, never by name: another copy may be the one someone is using.
  Stop-Process -Id $proc.Id -Force
}
