param([int]$TargetPid)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class BorderCursorRead {
 public delegate bool EnumProc(IntPtr h,IntPtr l);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb,IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO info);
 [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr instance,IntPtr id);
 [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h,int msg,IntPtr wp,IntPtr lp);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 public struct RECT { public int L,T,R,B; }
 public struct POINT { public int X,Y; }
 public struct CURSORINFO { public int Size,Flags; public IntPtr Cursor; public POINT Pos; }
 public static IntPtr Main(int pid) {
  IntPtr best=IntPtr.Zero;long area=0;
  EnumWindows((h,l)=>{uint p;GetWindowThreadProcessId(h,out p);if(p==(uint)pid&&IsWindowVisible(h)){RECT r;GetWindowRect(h,out r);long a=(long)(r.R-r.L)*(r.B-r.T);if(r.L>-10000&&a>area){area=a;best=h;}}return true;},IntPtr.Zero);return best;
 }
}
"@
[BorderCursorRead]::SetProcessDPIAware()|Out-Null
$expected=(Resolve-Path 'dist\SoulLink.exe').Path
$proc=Get-CimInstance Win32_Process -Filter "ProcessId=$TargetPid"
if($null -eq $proc -or $proc.ExecutablePath -ne $expected -or $proc.SessionId -ne (Get-Process -Id $PID).SessionId){throw 'Target identity mismatch'}
$h=[BorderCursorRead]::Main($TargetPid)
if($h -eq [IntPtr]::Zero){throw 'Main HWND not found'}
[BorderCursorRead]::SetForegroundWindow($h)|Out-Null
Start-Sleep -Milliseconds 400
$r=New-Object BorderCursorRead+RECT
[BorderCursorRead]::GetWindowRect($h,[ref]$r)|Out-Null
$points=[Collections.Generic.List[object]]::new()
$points.Add([Tuple]::Create($r.R-2,$r.B-2,'corner'))
$points.Add([Tuple]::Create($r.R-2,[int](($r.T+$r.B)/2),'right'))
$points.Add([Tuple]::Create([int](($r.L+$r.R)/2),$r.B-2,'bottom'))
$points.Add([Tuple]::Create($r.L+400,$r.T+20,'title'))
$names=@{[IntPtr](32640)='arrow';[IntPtr](32642)='we';[IntPtr](32643)='nwse';[IntPtr](32644)='ns';[IntPtr](32645)='nesw'}
foreach($pt in $points){
 [BorderCursorRead]::SetCursorPos($pt.Item1,$pt.Item2)|Out-Null
 Start-Sleep -Milliseconds 300
 $info=New-Object BorderCursorRead+CURSORINFO
 $info.Size=[Runtime.InteropServices.Marshal]::SizeOf($info)
 [BorderCursorRead]::GetCursorInfo([ref]$info)|Out-Null
 $lp=[IntPtr](($pt.Item2 -shl 16) -bor ($pt.Item1 -band 0xffff))
 $hit=[BorderCursorRead]::SendMessage($h,0x84,[IntPtr]::Zero,$lp)
 "place=$($pt.Item3) pos=$($pt.Item1),$($pt.Item2) hit=$hit cursor=$($info.Cursor) kind=$($names[$info.Cursor])"
}
[BorderCursorRead]::SetCursorPos(1280,900)|Out-Null
