param([int]$TargetPid, [ValidateSet('read','noresizestyle','restore')][string]$Mode='read')
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WsProbe {
 public delegate bool EnumProc(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern int GetWindowLongPtr(IntPtr h, int n);
 [DllImport("user32.dll",EntryPoint="SetWindowLongPtr")] public static extern IntPtr SetWindowLongPtr64(IntPtr h,int n,IntPtr v);
 [DllImport("user32.dll",EntryPoint="SetWindowLong")] public static extern IntPtr SetWindowLong32(IntPtr h,int n,int v);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int ht,uint flags);
 public struct RECT { public int L,T,R,B; }
 public static IntPtr Main(int pid) {
  IntPtr best=IntPtr.Zero; long area=0;
  EnumWindows((h,l)=>{uint p;GetWindowThreadProcessId(h,out p);if(p==(uint)pid&&IsWindowVisible(h)){RECT r;GetWindowRect(h,out r);long a=(long)(r.R-r.L)*(r.B-r.T);if(r.L>-10000&&a>area){area=a;best=h;}}return true;},IntPtr.Zero);return best;
 }
 public static IntPtr Set(IntPtr h,int value) { return IntPtr.Size==8?SetWindowLongPtr64(h,-16,new IntPtr(value)):SetWindowLong32(h,-16,value); }
}
"@
[WsProbe]::SetProcessDPIAware()|Out-Null
$expected=(Resolve-Path 'dist\SoulLink.exe').Path
$proc=Get-CimInstance Win32_Process -Filter "ProcessId=$TargetPid"
if($null -eq $proc -or $proc.ExecutablePath -ne $expected -or $proc.SessionId -ne (Get-Process -Id $PID).SessionId){throw 'Target identity mismatch'}
$h=[WsProbe]::Main($TargetPid)
if($h -eq [IntPtr]::Zero){throw 'Main HWND not found'}
$style=[WsProbe]::GetWindowLongPtr($h,-16)
"mode=$Mode pid=$TargetPid hwnd=$h style-before=0x$('{0:X8}' -f $style) fg=$([WsProbe]::GetForegroundWindow() -eq $h)"
if($Mode -ne 'read') {
 if($Mode -eq 'noresizestyle'){$new=$style -band (-1 -bxor 0x00040000)}else{$new=$style -bor 0x00040000}
 [WsProbe]::Set($h,$new)|Out-Null
 [WsProbe]::SetWindowPos($h,[IntPtr]::Zero,0,0,0,0,0x0001 -bor 0x0002 -bor 0x0004 -bor 0x0020)|Out-Null
 "style-after=0x$('{0:X8}' -f [WsProbe]::GetWindowLongPtr($h,-16))"
}