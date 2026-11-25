param([int]$TargetPid)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class HitProbe {
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h,int msg,IntPtr wp,IntPtr lp);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
 [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 public struct RECT { public int L,T,R,B; }
 public struct POINT { public int X,Y; }
}
"@
[HitProbe]::SetProcessDPIAware()|Out-Null
$expected=(Resolve-Path 'dist\SoulLink.exe').Path
$proc=Get-CimInstance Win32_Process -Filter "ProcessId=$TargetPid"
if($null -eq $proc -or $proc.ExecutablePath -ne $expected -or $proc.SessionId -ne (Get-Process -Id $PID).SessionId){throw 'Target identity mismatch'}
$h=[WsProbe]::Main($TargetPid)
[HitProbe]::SetForegroundWindow($h)|Out-Null
Start-Sleep -Milliseconds 600
$r=New-Object HitProbe+RECT;[HitProbe]::GetWindowRect($h,[ref]$r)|Out-Null
foreach($p in @(@(1,1),@(3,3),@(6,6),@(9,9),@(12,12),@(20,20))){
 $x=$r.R-$p[0];$y=$r.B-$p[1]
 $hit=[HitProbe]::SendMessage($h,0x84,[IntPtr]::Zero,[IntPtr](($y -shl 16) -bor ($x -band 0xffff)))
 "offset=$($p[0]),$($p[1]) point=$x,$y hit=$hit"
}
[HitProbe]::SetCursorPos($r.R-2,$r.B-2)|Out-Null
Start-Sleep -Milliseconds 300
$pt=New-Object HitProbe+POINT;[HitProbe]::GetCursorPos([ref]$pt)|Out-Null
"cursor=$($pt.X),$($pt.Y) window-from-point=$([HitProbe]::WindowFromPoint($pt)) target=$h"
