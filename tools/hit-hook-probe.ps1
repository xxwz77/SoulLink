param([int]$TargetPid)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class HitHook {
 public delegate bool EnumProc(IntPtr h,IntPtr l);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb,IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr h,int n);
 [DllImport("user32.dll")] public static extern IntPtr CallWindowProc(IntPtr prev,IntPtr h,int msg,IntPtr wp,IntPtr lp);
 [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
 [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h,int msg,IntPtr wp,IntPtr lp);
 public struct RECT { public int L,T,R,B; }
 public struct POINT { public int X,Y; }
 public static IntPtr WndProc;
 public static IntPtr HookWnd(IntPtr h,int msg,IntPtr wp,IntPtr lp) {
  if(msg==0x84) {
   POINT pt; GetCursorPos(out pt); RECT r; GetWindowRect(h,out r);
   const int g=9; bool l=pt.X<r.L+g,rt=pt.X>=r.R-g,t=pt.Y<r.T+g,b=pt.Y>=r.B-g;
   if(t&&l)return (IntPtr)13;if(t&&rt)return (IntPtr)14;if(b&&l)return (IntPtr)16;if(b&&rt)return (IntPtr)17;
   if(l)return (IntPtr)10;if(rt)return (IntPtr)11;if(t)return (IntPtr)12;if(b)return (IntPtr)15;
  }
  return CallWindowProc(WndProc,h,msg,wp,lp);
 }
 public static IntPtr Main(int pid) {
  IntPtr best=IntPtr.Zero;long area=0;
  EnumWindows((h,l)=>{uint p;GetWindowThreadProcessId(h,out p);if(p==(uint)pid&&IsWindowVisible(h)){RECT r;GetWindowRect(h,out r);long a=(long)(r.R-r.L)*(r.B-r.T);if(r.L>-10000&&a>area){area=a;best=h;}}return true;},IntPtr.Zero);return best;
 }
}
"@
[HitHook]::SetProcessDPIAware()|Out-Null
$expected=(Resolve-Path 'dist\SoulLink.exe').Path
$proc=Get-CimInstance Win32_Process -Filter "ProcessId=$TargetPid"
if($null -eq $proc -or $proc.ExecutablePath -ne $expected -or $proc.SessionId -ne (Get-Process -Id $PID).SessionId){throw 'Target identity mismatch'}
$h=[HitHook]::Main($TargetPid)
[HitHook]::WndProc=[HitHook]::GetWindowLongPtr($h,-4)
$cb=[HitHook+WndProc]{param($h,$m,$w,$l) [HitHook]::HookWnd($h,$m,$w,$l)}
$ptr=[Runtime.InteropServices.Marshal]::GetFunctionPointerForDelegate($cb)
[WsProbe]::SetWindowLongPtr64($h,-4,$ptr)|Out-Null
"hwnd=$h prior-proc=$([HitHook]::WndProc) hook-proc=$ptr"
