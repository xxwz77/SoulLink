param([int]$TargetPid)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class CursorProbe {
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern IntPtr GetCursor();
 [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO info);
 [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr instance,int id);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 public struct RECT { public int L,T,R,B; }
 public struct POINT { public int X,Y; }
 public struct CURSORINFO { public int Size,Flags; public IntPtr Cursor; public POINT Pos; }
}
"@
[CursorProbe]::SetProcessDPIAware()|Out-Null
$expected=(Resolve-Path 'dist\SoulLink.exe').Path
$proc=Get-CimInstance Win32_Process -Filter "ProcessId=$TargetPid"
if($null -eq $proc -or $proc.ExecutablePath -ne $expected -or $proc.SessionId -ne (Get-Process -Id $PID).SessionId){throw 'Target identity mismatch'}
$h=[WsProbe]::Main($TargetPid)
$r=New-Object CursorProbe+RECT;[CursorProbe]::GetWindowRect($h,[ref]$r)|Out-Null
$nw=[CursorProbe]::LoadCursor([IntPtr]::Zero,32643)
foreach($pt in @(@($r.R-1,$r.B-1),@($r.R-3,$r.B-3),@($r.R-7,$r.B-7),@($r.R-15,$r.B-15),@($r.L+400,$r.T+20))){
 [CursorProbe]::SetCursorPos($pt[0],$pt[1])|Out-Null;Start-Sleep -Milliseconds 250
 $i=New-Object CursorProbe+CURSORINFO;$i.Size=[Runtime.InteropServices.Marshal]::SizeOf($i);[CursorProbe]::GetCursorInfo([ref]$i)|Out-Null
 "point=$($pt[0]),$($pt[1]) cursor=$($i.Cursor) sizeNWSE=$($i.Cursor -eq $nw)"
}
[CursorProbe]::SetCursorPos(1280,900)|Out-Null
