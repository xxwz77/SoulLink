
param(
  [string]$Exe  = "E:\Documents\deepseek-harness\SoulLink\dist\SoulLink.exe",
  [string]$Dir  = "E:\Documents\deepseek-harness\SoulLink\tools\seq",
  [int]$Seconds = 24,
  [int]$ToggleMs = 1500
)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class SQ {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x,int y,int cx,int cy,uint f);
  [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr h);
  public struct R { public int L,T,Rr,B; }
}
"@
[SQ]::SetProcessDPIAware() | Out-Null

if(!(Test-Path $Dir)){ New-Item -ItemType Directory -Path $Dir | Out-Null }
Get-ChildItem $Dir -Filter *.png -ErrorAction SilentlyContinue | Remove-Item -Force

# 1) 启动
$proc = Start-Process -FilePath $Exe -PassThru
"launched pid=$($proc.Id)"
$target = [IntPtr]::Zero
for($i=0; $i -lt 40 -and $target -eq [IntPtr]::Zero; $i++){
  Start-Sleep -Milliseconds 250
  $script:hits = @()
  $cb = [SQ+EnumProc]{ param($h,$l)
    $q=0; [SQ]::GetWindowThreadProcessId($h,[ref]$q) | Out-Null
    if([int]$q -eq $proc.Id -and [SQ]::IsWindowVisible($h)){
      $r = New-Object SQ+R; [SQ]::GetWindowRect($h,[ref]$r) | Out-Null
      if(($r.Rr-$r.L) -gt 400 -and ($r.B-$r.T) -gt 300){
        $sb = New-Object System.Text.StringBuilder 256
        [SQ]::GetClassName($h,$sb,256) | Out-Null
        if($sb.ToString() -like "HwndWrapper*"){ $script:hits += $h }
      }
    }
    return $true }
  [SQ]::EnumWindows($cb,[IntPtr]::Zero) | Out-Null
  if($script:hits.Count -gt 0){ $target = [IntPtr]$script:hits[0] }
}
if($target -eq [IntPtr]::Zero){ "NO_WINDOW"; exit 2 }
$r0 = New-Object SQ+R; [SQ]::GetWindowRect($target,[ref]$r0) | Out-Null
"window hwnd=$target rect=$($r0.L),$($r0.T) $($r0.Rr-$r0.L)x$($r0.B-$r0.T)"

$log = New-Object System.Collections.Generic.List[string]
$margin = 16
$deadline = (Get-Date).AddSeconds($Seconds)
$idx = 0
$lastToggle = Get-Date
$activeNow = $false

while((Get-Date) -lt $deadline){
  $r = New-Object SQ+R; [SQ]::GetWindowRect($target,[ref]$r) | Out-Null
  $w = $r.Rr-$r.L; $h = $r.B-$r.T
  if($w -gt 0 -and $h -gt 0){
    $x = [Math]::Max(0, $r.L - $margin); $y = [Math]::Max(0, $r.T - $margin)
    $ww = $w + $margin*2; $hh = $h + $margin*2
    try {
      $bmp = New-Object System.Drawing.Bitmap $ww,$hh
      $g = [System.Drawing.Graphics]::FromImage($bmp)
      $g.CopyFromScreen($x,$y,0,0,$bmp.Size)
      $g.Dispose()
      $fg = [SQ]::GetForegroundWindow()
      $act = ($fg -eq $target)
      $name = "f{0:D4}{1}.png" -f $idx, $(if($act){"-A"}else{"-D"})
      $bmp.Save((Join-Path $Dir $name), [System.Drawing.Imaging.ImageFormat]::Png)
      $bmp.Dispose()
      $log.Add(("{0:D4} {1:HH:mm:ss.fff} act={2} rect={3},{4} {5}x{6} src={7},{8}" -f $idx,(Get-Date),$act,$r.L,$r.T,$w,$h,$x,$y))
    } catch { $log.Add(("{0:D4} CAPTURE_FAIL {1}" -f $idx,$_.Exception.Message)) }
  }
  $idx++

  # 2) 变焦
  if(((Get-Date) - $lastToggle).TotalMilliseconds -ge $ToggleMs){
    $lastToggle = Get-Date
    if([SQ]::GetForegroundWindow() -eq $target){
      [SQ]::SetForegroundWindow([SQ]::GetDesktopWindow()) | Out-Null
      $activeNow = $false
    } else {
      [SQ]::SetWindowPos($target,[IntPtr](-1),0,0,0,0,0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
      [SQ]::BringWindowToTop($target) | Out-Null
      [SQ]::SetForegroundWindow($target) | Out-Null
      $activeNow = $true
    }
  }
  Start-Sleep -Milliseconds 120
}

$log | Set-Content (Join-Path $Dir "frames.txt") -Encoding UTF8
"frames=$idx saved to $Dir"
"still running pid=$($proc.Id)"
