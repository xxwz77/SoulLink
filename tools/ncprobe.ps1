
param([int]$TargetPid)
$ErrorActionPreference = "Stop"
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class NCP {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref P p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern IntPtr GetClassLongPtr(IntPtr h, int idx);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out R r, int sz);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttributeI(IntPtr h, int a, out int v, int sz);
  [DllImport("dwmapi.dll", EntryPoint="DwmGetWindowAttribute")] public static extern int DwmGetWindowAttributeInt(IntPtr h, int a, out int v, int sz);
  public struct R { public int L,T,Rr,B; }
  public struct P { public int X,Y; }
}
"@
[NCP]::SetProcessDPIAware() | Out-Null

$found = New-Object System.Collections.ArrayList
$cb = [NCP+EnumProc]{
  param($h,$l)
  $q = [uint32]0
  [NCP]::GetWindowThreadProcessId($h,[ref]$q) | Out-Null
  if($q -eq [uint32]$TargetPid -and [NCP]::IsWindowVisible($h)){
    $r = New-Object NCP+R
    [NCP]::GetWindowRect($h,[ref]$r) | Out-Null
    if(($r.Rr-$r.L) -gt 400 -and ($r.B-$r.T) -gt 300){
      $sb = New-Object System.Text.StringBuilder 256
      [NCP]::GetClassName($h,$sb,256) | Out-Null
      $null = $found.Add([pscustomobject]@{H=$h; Cls=$sb.ToString(); R=$r})
    }
  }
  return $true
}
[NCP]::EnumWindows($cb,[IntPtr]::Zero) | Out-Null

if($found.Count -eq 0){ "NO_WINDOW for pid $TargetPid"; exit 2 }
$w = $found[0]
$h = $w.H
"class = $($w.Cls)"
"hwnd  = $h"

$wr = New-Object NCP+R; [NCP]::GetWindowRect($h,[ref]$wr) | Out-Null
$cr = New-Object NCP+R; [NCP]::GetClientRect($h,[ref]$cr) | Out-Null
$p  = New-Object NCP+P; $p.X=0; $p.Y=0
[NCP]::ClientToScreen($h,[ref]$p) | Out-Null

""
"WindowRect : $($wr.L),$($wr.T) .. $($wr.Rr),$($wr.B)   size $($wr.Rr-$wr.L) x $($wr.B-$wr.T)"
"ClientRect : 0,0 .. $($cr.Rr),$($cr.B)   size $($cr.Rr) x $($cr.B)"
"ClientOrigin(screen) = $($p.X),$($p.Y)"
""
"=== 非客户区厚度（系统认定的边框）==="
"  left   = " + ($p.X - $wr.L)
"  top    = " + ($p.Y - $wr.T)
"  right  = " + ($wr.Rr - ($p.X + $cr.Rr))
"  bottom = " + ($wr.B  - ($p.Y + $cr.B))
""
"=== 系统度量 (DPI=" + $([NCP]::GetSystemMetrics(2)) + "?) ==="
"  SM_CXSIZEFRAME(32)   = " + [NCP]::GetSystemMetrics(32)
"  SM_CYSIZEFRAME(33)   = " + [NCP]::GetSystemMetrics(33)
"  SM_CXPADDEDBORDER(92)= " + [NCP]::GetSystemMetrics(92)
"  SM_CXBORDER(5)       = " + [NCP]::GetSystemMetrics(5)
""
$ext = New-Object NCP+R
$hr = [NCP]::DwmGetWindowAttribute($h, 9, [ref]$ext, 16)
"=== DWM ==="
"  DWMWA_EXTENDED_FRAME_BOUNDS hr=$hr  $($ext.L),$($ext.T)..$($ext.Rr),$($ext.B)"
$v = 0
$hr2 = [NCP]::DwmGetWindowAttributeInt($h, 33, [ref]$v, 4)
"  DWMWA_WINDOW_CORNER_PREFERENCE hr=$hr2 val=$v"
$v2 = 0
$hr3 = [NCP]::DwmGetWindowAttributeInt($h, 34, [ref]$v2, 4)
"  DWMWA_BORDER_COLOR hr=$hr3 val=0x$('{0:X8}' -f $v2)"
$v3 = 0
$hr4 = [NCP]::DwmGetWindowAttributeInt($h, 2, [ref]$v3, 4)
"  DWMWA_NCRENDERING_POLICY hr=$hr4 val=$v3"
