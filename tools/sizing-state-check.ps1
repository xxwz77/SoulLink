param([int]$TargetPid)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SizingCheck {
 public delegate bool EnumProc(IntPtr h,IntPtr l);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb,IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int c);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr a,int x,int y,int w,int ht,uint f);
 public struct RECT { public int L,T,R,B; }
 public static IntPtr Main(int pid) {
  IntPtr best=IntPtr.Zero;long area=0;
  EnumWindows((h,l)=>{uint p;GetWindowThreadProcessId(h,out p);if(p==(uint)pid&&IsWindowVisible(h)){RECT r;GetWindowRect(h,out r);long a=(long)(r.R-r.L)*(r.B-r.T);if(r.L>-10000&&a>area){area=a;best=h;}}return true;},IntPtr.Zero);return best;
 }
}
"@
[SizingCheck]::SetProcessDPIAware()|Out-Null
$expected=(Resolve-Path 'dist\SoulLink.exe').Path
$proc=Get-CimInstance Win32_Process -Filter "ProcessId=$TargetPid"
if($null -eq $proc -or $proc.ExecutablePath -ne $expected -or $proc.SessionId -ne (Get-Process -Id $PID).SessionId){throw 'Target identity mismatch'}
$h=[SizingCheck]::Main($TargetPid)
if($h -eq [IntPtr]::Zero){throw 'Main HWND not found'}
function Show-State($label){
 $r=New-Object SizingCheck+RECT;[SizingCheck]::GetWindowRect($h,[ref]$r)|Out-Null
 "$label rect=$($r.L),$($r.T),$($r.R),$($r.B) size=$($r.R-$r.L)x$($r.B-$r.T)"
}
Show-State 'initial'
[SizingCheck]::ShowWindow($h,3)|Out-Null
Start-Sleep -Seconds 2
Show-State 'maximized'
[SizingCheck]::ShowWindow($h,9)|Out-Null
Start-Sleep -Seconds 2
Show-State 'restored'
