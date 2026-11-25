param([int]$TargetPid)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ResizeDrag {
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);
 [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h,int msg,IntPtr wp,IntPtr lp);
 [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
 public struct POINT { public int X,Y; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 public struct RECT { public int L,T,R,B; }
}
"@
[ResizeDrag]::SetProcessDPIAware()|Out-Null
$expected=(Resolve-Path 'dist\SoulLink.exe').Path
$proc=Get-CimInstance Win32_Process -Filter "ProcessId=$TargetPid"
if($null -eq $proc -or $proc.ExecutablePath -ne $expected -or $proc.SessionId -ne (Get-Process -Id $PID).SessionId){throw 'Target identity mismatch'}
$h=[WsProbe]::Main($TargetPid)
if($h -eq [IntPtr]::Zero){throw 'Main HWND not found'}
function Rect($label){$r=New-Object ResizeDrag+RECT;[ResizeDrag]::GetWindowRect($h,[ref]$r)|Out-Null;Write-Host "$label rect=$($r.L),$($r.T),$($r.R),$($r.B) size=$($r.R-$r.L)x$($r.B-$r.T)";return $r}
Rect 'before'
$r=Rect 'drag-origin'
[ResizeDrag]::SetCursorPos($r.R-2,$r.B-2)|Out-Null
$pt=New-Object ResizeDrag+POINT;[ResizeDrag]::GetCursorPos([ref]$pt)|Out-Null
$lp=[IntPtr](($pt.Y -shl 16) -bor ($pt.X -band 0xffff))
$hit=[ResizeDrag]::SendMessage($h,0x84,[IntPtr]::Zero,$lp)
Write-Host "pre-drag cursor=$($pt.X),$($pt.Y) hit=$hit"
[ResizeDrag]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 200
for($i=1;$i -le 20;$i++){[ResizeDrag]::SetCursorPos($r.R-2+[int]($i*140/20),$r.B-2+[int]($i*100/20))|Out-Null;Start-Sleep -Milliseconds 40}
Rect 'after-enlarge-before-release'
[ResizeDrag]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 500
$grown=Rect 'grown'
if($grown.R-$grown.L -le $r.R-$r.L -or $grown.B-$grown.T -le $r.B-$r.T){[ResizeDrag]::mouse_event(4,0,0,0,[UIntPtr]::Zero);throw 'Resize drag did not enlarge the window'}
[ResizeDrag]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 200
for($i=1;$i -le 20;$i++){[ResizeDrag]::SetCursorPos($grown.R-2-[int]($i*140/20),$grown.B-2-[int]($i*100/20))|Out-Null;Start-Sleep -Milliseconds 40}
Rect 'drag-back-while-held'
[ResizeDrag]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 500
Rect 'restored'
[ResizeDrag]::SetCursorPos(1280,900)|Out-Null
