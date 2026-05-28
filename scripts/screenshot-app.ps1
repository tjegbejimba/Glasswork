<#
.SYNOPSIS
  Capture a screenshot of a running window by process name or PID using Win32 PrintWindow.

.DESCRIPTION
  Uses PrintWindow with PW_RENDERFULLCONTENT (Windows 8.1+) which asks the window to render
  itself into a DC. Works on occluded windows, secondary monitors, and (unlike GDI BitBlt)
  RDP-disconnected sessions. No UI Automation involvement, no desktop enumeration — just a
  direct HWND -> PNG capture. This is the approach Microsoft's UFO research agent uses.

  Intended as the "give the agent eyes" primitive for Glasswork. Saves a PNG and prints
  its absolute path on the last line of stdout so callers can capture it.

.PARAMETER ProcessName
  Process name (without .exe). Mutually exclusive with -Pid.

.PARAMETER Pid
  Process ID. Mutually exclusive with -ProcessName.

.PARAMETER OutPath
  Destination PNG. Defaults to $env:TEMP\<processname>-<timestamp>.png.

.EXAMPLE
  pwsh -File scripts\screenshot-app.ps1 -ProcessName Glasswork
#>
[CmdletBinding(DefaultParameterSetName='ByName')]
param(
    [Parameter(ParameterSetName='ByName', Mandatory)]
    [string]$ProcessName,
    [Parameter(ParameterSetName='ByPid', Mandatory)]
    [int]$ProcessId,
    [string]$OutPath
)

$ErrorActionPreference = 'Stop'

# Resolve target process
$proc = if ($PSCmdlet.ParameterSetName -eq 'ByName') {
    Get-Process -Name $ProcessName -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
} else {
    Get-Process -Id $ProcessId -ErrorAction Stop
}
if (-not $proc) { throw "No process found." }
$hwnd = $proc.MainWindowHandle
if ($hwnd -eq 0) { throw "Process $($proc.Id) has no top-level window." }

if (-not $OutPath) {
    $ts = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutPath = Join-Path $env:TEMP "$($proc.ProcessName)-$ts.png"
}

# Inline P/Invoke for PrintWindow + GetWindowRect.
$sig = @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class WinShot {
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    // PW_RENDERFULLCONTENT = 2 — handles DirectComposition (WinUI/WPF) windows.
    public const uint PW_RENDERFULLCONTENT = 2;
    // DWMWA_EXTENDED_FRAME_BOUNDS = 9 — true window bounds excluding invisible shadow margins.
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    public static void Capture(IntPtr hwnd, string path) {
        RECT r;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<RECT>()) != 0) {
            if (!GetWindowRect(hwnd, out r)) throw new Exception("GetWindowRect failed");
        }
        int w = r.Right - r.Left;
        int h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) throw new Exception("Window has zero size — minimized?");

        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bmp)) {
            IntPtr hdc = g.GetHdc();
            try {
                bool ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                if (!ok) throw new Exception("PrintWindow returned false");
            } finally {
                g.ReleaseHdc(hdc);
            }
            bmp.Save(path, ImageFormat.Png);
        }
    }
}
'@

Add-Type -TypeDefinition $sig -ReferencedAssemblies 'System.Drawing','System.Drawing.Primitives','System.Drawing.Common','System.Private.Windows.Core' -ErrorAction Stop

[WinShot]::Capture($hwnd, $OutPath)
$resolved = (Resolve-Path -LiteralPath $OutPath).Path
Write-Host "Captured PID $($proc.Id) ('$($proc.MainWindowTitle)') -> $resolved"
$resolved
