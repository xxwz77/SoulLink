Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class SV6 {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
"@
$procId = Get-Content "$env:TEMP\st.pid"
$proc = Get-Process -Id $procId
$h = $proc.MainWindowHandle
$fg = [SV6]::GetForegroundWindow()
$tidFg = [SV6]::GetWindowThreadProcessId($fg, [IntPtr]::Zero)
$tidMe = [SV6]::GetCurrentThreadId()
[SV6]::AttachThreadInput($tidMe,$tidFg,$true) | Out-Null
[SV6]::ShowWindow($h,9) | Out-Null
[SV6]::BringWindowToTop($h) | Out-Null
[SV6]::SetForegroundWindow($h) | Out-Null
[SV6]::AttachThreadInput($tidMe,$tidFg,$false) | Out-Null
Start-Sleep -Milliseconds 1200
$r = New-Object SV6+RECT
[SV6]::GetWindowRect($h, [ref]$r) | Out-Null
$width = $r.R - $r.L
$height = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $width, $height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
$bmp.Save($args[0], [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"saved " + $args[0] + "  " + $width + "x" + $height
