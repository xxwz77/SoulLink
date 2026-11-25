param([int]$TargetPid)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class BorderGeometry {
 public delegate bool EnumProc(IntPtr h,IntPtr l);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb,IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h,ref POINT p);
 [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h,int msg,IntPtr wp,IntPtr lp);
 [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr a,int x,int y,int w,int ht,uint f);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 public struct RECT { public int L,T,R,B; }
 public struct POINT { public int X,Y; }
 public static IntPtr Main(int pid) {
  IntPtr best=IntPtr.Zero;long area=0;
  EnumWindows((h,l)=>{uint p;GetWindowThreadProcessId(h,out p);if(p==(uint)pid&&IsWindowVisible(h)){RECT r;GetWindowRect(h,out r);long a=(long)(r.R-r.L)*(r.B-r.T);if(r.L>-10000&&a>area){area=a;best=h;}}return true;},IntPtr.Zero);return best;
 }
}
"@
[BorderGeometry]::SetProcessDPIAware()|Out-Null
$expected=(Resolve-Path 'dist\SoulLink.exe').Path
$proc=Get-CimInstance Win32_Process -Filter "ProcessId=$TargetPid"
if($null -eq $proc -or $proc.ExecutablePath -ne $expected -or $proc.SessionId -ne (Get-Process -Id $PID).SessionId){throw 'Target identity mismatch'}
$h=[BorderGeometry]::Main($TargetPid)
if($h -eq [IntPtr]::Zero){throw 'Main HWND not found'}
[BorderGeometry]::SetForegroundWindow($h)|Out-Null
Start-Sleep -Milliseconds 500
[BorderGeometry]::SetWindowPos($h,[IntPtr]::Zero,474,146,1200,900,0x0004)|Out-Null
Start-Sleep -Seconds 2
$r=New-Object BorderGeometry+RECT
[BorderGeometry]::GetWindowRect($h,[ref]$r)|Out-Null
$bmp=[Drawing.Bitmap]::new($r.R-$r.L,$r.B-$r.T)
$g=[Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L,$r.T,0,0,$bmp.Size)
$g.Dispose()
$out='E:\Documents\deepseek-harness\SoulLink\tools\real-small-before-hitfix.png'
$bmp.Save($out,[Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"rect=$($r.L),$($r.T),$($r.R),$($r.B) saved=$out"
