param([int]$TargetPid = 145104, [switch]$RealClicks, [string]$Dir = "E:\Documents\deepseek-harness\SoulLink\tools\focus-flash-baseline")
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class FocusFlashNative {
 public delegate bool EnumProc(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string title);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
 [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
 public struct RECT { public int L,T,R,B; }
 public static IntPtr Main(int pid) {
  IntPtr best=IntPtr.Zero; long area=0;
  EnumWindows((h,l) => {
   uint p; GetWindowThreadProcessId(h,out p);
   if(p==(uint)pid && IsWindowVisible(h)) {
    RECT r; GetWindowRect(h,out r); long a=(long)(r.R-r.L)*(r.B-r.T);
    if(r.L>-10000 && a>area) { area=a; best=h; }
   }
   return true;
  },IntPtr.Zero);
  return best;
 }
}
"@
[FocusFlashNative]::SetProcessDPIAware() | Out-Null
$expected = "E:\Documents\deepseek-harness\SoulLink\dist\SoulLink.exe"
$proc = Get-CimInstance Win32_Process -Filter "ProcessId=$TargetPid"
if ($null -eq $proc -or $proc.ExecutablePath -ne $expected -or $proc.SessionId -ne (Get-Process -Id $PID).SessionId) { throw "Target identity mismatch" }
$h = [FocusFlashNative]::Main($TargetPid)
$taskbar = [FocusFlashNative]::FindWindow("Shell_TrayWnd",$null)
if ($h -eq [IntPtr]::Zero -or $taskbar -eq [IntPtr]::Zero) { throw "Main window or taskbar not found" }
$dir = $Dir
New-Item -ItemType Directory -Path $dir -Force | Out-Null
$r = New-Object FocusFlashNative+RECT
[FocusFlashNative]::GetWindowRect($h,[ref]$r) | Out-Null
"target pid=$TargetPid hwnd=$h rect=$($r.L),$($r.T),$($r.R),$($r.B) taskbar=$taskbar"
$sw=[Diagnostics.Stopwatch]::StartNew()
$log=[Collections.Generic.List[string]]::new()
[FocusFlashNative]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 1200
for($cycle=0;$cycle -lt 8;$cycle++) {
 foreach($target in @($taskbar,$h)) {
  if ($RealClicks) {
   if ($target -eq $h) { $clickX=$r.L+520; $clickY=$r.T+72 }
   else { $clickX=1800; $clickY=1385 }
   [FocusFlashNative]::SetCursorPos($clickX,$clickY) | Out-Null
   [FocusFlashNative]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero)
   [FocusFlashNative]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero)
  } else { [FocusFlashNative]::SetForegroundWindow($target) | Out-Null }
  for($j=0;$j -lt 4;$j++) {
   [FocusFlashNative]::GetWindowRect($h,[ref]$r) | Out-Null
   $w=$r.R-$r.L; $ht=$r.B-$r.T
   $bmp=[Drawing.Bitmap]::new($w,$ht)
   $g=[Drawing.Graphics]::FromImage($bmp)
   $g.CopyFromScreen($r.L,$r.T,0,0,$bmp.Size)
   $g.Dispose()
   $name="c{0:D2}-{1}-{2}.png" -f $cycle,$(if($target -eq $h){"app"}else{"taskbar"}),$j
   $bmp.Save((Join-Path $dir $name),[Drawing.Imaging.ImageFormat]::Png)
   $bmp.Dispose()
   $fg=[FocusFlashNative]::GetForegroundWindow()
   $border=0; $hr=[FocusFlashNative]::DwmGetWindowAttribute($h,34,[ref]$border,4)
   $log.Add(("{0} ms={1} fg={2} border=0x{3:X8} hr={4}" -f $name,$sw.ElapsedMilliseconds,($fg -eq $h),$border,$hr))
   Start-Sleep -Milliseconds 25
  }
 }
}
[FocusFlashNative]::SetForegroundWindow($h) | Out-Null
$log | Set-Content (Join-Path $dir "frames.txt") -Encoding UTF8
"captured=$($log.Count); application focus restored=$(( [FocusFlashNative]::GetForegroundWindow()) -eq $h)"
