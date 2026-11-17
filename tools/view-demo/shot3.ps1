Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class SW3 {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
"@
$procId = Get-Content "$env:TEMP\st.pid"
$proc = Get-Process -Id $procId
$h = $proc.MainWindowHandle
[SW3]::ShowWindow($h, 9) | Out-Null
[SW3]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 900
$r = New-Object SW3+RECT
[SW3]::GetWindowRect($h, [ref]$r) | Out-Null
$wd = $r.R - $r.L; $ht = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $wd, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
$bmp.Save($args[0], [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"saved"
