param(
    [Parameter(Mandatory = $true)]
    [string]$Title,
    [Parameter(Mandatory = $true)]
    [string]$Out
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DpiAwareness {
    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();
}
"@
[DpiAwareness]::SetProcessDPIAware() | Out-Null
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WinCap {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int count);
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
}
"@

$hwnd = [WinCap]::FindWindow($null, $Title)
if ($hwnd -eq [IntPtr]::Zero) {
    $found = [IntPtr]::Zero
    $callback = [WinCap+EnumWindowsProc]{
        param($h, $l)
        if ([WinCap]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 512
            [WinCap]::GetWindowText($h, $sb, $sb.Capacity) | Out-Null
            if ($sb.ToString().Contains($Title)) {
                $script:found = $h
                return $false
            }
        }
        return $true
    }
    [WinCap]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
    $hwnd = $script:found
}
if ($hwnd -eq [IntPtr]::Zero) {
    throw "Window not found: $Title"
}

$rect = New-Object WinCap+RECT
[WinCap]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) {
    throw "Window has no size"
}

$bitmap = New-Object System.Drawing.Bitmap($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc()
try {
    [WinCap]::PrintWindow($hwnd, $hdc, 2) | Out-Null
}
finally {
    $graphics.ReleaseHdc($hdc)
    $graphics.Dispose()
}

$bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
Write-Host "Saved $Out ($width x $height)"
