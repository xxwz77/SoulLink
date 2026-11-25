param([int]$TargetPid,[switch]$Shrink)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ResizeShrink {
 public delegate bool EnumProc(IntPtr h,IntPtr l);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb,IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);
 [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h,int msg,IntPtr wp,IntPtr lp);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 public struct RECT { public int L,T,R,B; }
 public static IntPtr Main(int pid) {
  IntPtr best=IntPtr.Zero;long area=0;
  EnumWindows((h,l)=>{uint p;GetWindowThreadProcessId(h,out p);if(p==(uint)pid&&IsWindowVisible(h)){RECT r;GetWindowRect(h,out r);long a=(long)(r.R-r.L)*(r.B-r.T);if(r.L>-10000&&a>area){area=a;best=h;}}return true;},IntPtr.Zero);return best;
 }
}
"@
[ResizeShrink]::SetProcessDPIAware()|Out-Null
$expected=(Resolve-Path 'dist\SoulLink.exe').Path
$proc=Get-CimInstance Win32_Process -Filter "ProcessId=$TargetPid"
if($null -eq $proc -or $proc.ExecutablePath -ne $expected -or $proc.SessionId -ne (Get-Process -Id $PID).SessionId){throw 'Target identity mismatch'}
$h=[ResizeShrink]::Main($TargetPid)
if($h -eq [IntPtr]::Zero){throw 'Main HWND not found'}
[ResizeShrink]::SetForegroundWindow($h)|Out-Null
Start-Sleep -Milliseconds 400
$r=New-Object ResizeShrink+RECT;[ResizeShrink]::GetWindowRect($h,[ref]$r)|Out-Null
$startX=$r.R-2;$startY=$r.B-2
[ResizeShrink]::SetCursorPos($startX,$startY)|Out-Null
[ResizeShrink]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 250
for($i=1;$i -le 20;$i++){
 [ResizeShrink]::SetCursorPos($startX-[int]($i*413/20),$startY-[int]($i*176/20))|Out-Null
 Start-Sleep -Milliseconds 40
}
$s=New-Object ResizeShrink+RECT;[ResizeShrink]::GetWindowRect($h,[ref]$s)|Out-Null
Write-Host "drag-shrink=$($s.L),$($s.T),$($s.R),$($s.B) size=$($s.R-$s.L)x$($s.B-$s.T)"
if($s.R-$s.L -ge $r.R-$r.L -or $s.B-$s.T -ge $r.B-$r.T){[ResizeShrink]::mouse_event(4,0,0,0,[UIntPtr]::Zero);throw 'Shrink drag failed'}
$bmp=[Drawing.Bitmap]::new($s.R-$s.L,$s.B-$s.T)
$g=[Drawing.Graphics]::FromImage($bmp);$g.CopyFromScreen($s.L,$s.T,0,0,$bmp.Size);$g.Dispose()
$path='E:\Documents\deepseek-harness\SoulLink\tools\real-shrink-hitfix.png'
$bmp.Save($path,[Drawing.Imaging.ImageFormat]::Png);$bmp.Dispose()
$hit=[ResizeShrink]::SendMessage($h,0x84,[IntPtr]::Zero,[IntPtr](($s.B-2 -shl 16)-bor(($s.R-2)-band 0xffff)))
Write-Host "small-corner-hit=$hit saved=$path"
[ResizeShrink]::SetCursorPos($s.R-2,$s.B-2)|Out-Null
Start-Sleep -Milliseconds 200
for($i=1;$i -le 20;$i++){[ResizeShrink]::SetCursorPos($s.R-2+[int]($i*413/20),$s.B-2+[int]($i*176/20))|Out-Null;Start-Sleep -Milliseconds 40}
[ResizeShrink]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 600
$final=New-Object ResizeShrink+RECT;[ResizeShrink]::GetWindowRect($h,[ref]$final)|Out-Null
Write-Host "restored=$($final.L),$($final.T),$($final.R),$($final.B) size=$($final.R-$final.L)x$($final.B-$final.T)"
[ResizeShrink]::SetCursorPos(1280,900)|Out-Null
