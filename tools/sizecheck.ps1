
param([int]$TargetPid = 0)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;using System.Text;using System.Collections.Generic;using System.Runtime.InteropServices;
public class MW2 {
  public delegate bool EP(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool EnumWindows(EP cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref P p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x,int y,int cx,int cy,uint f);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  public struct R { public int L,T,Rr,B; }
  public struct P { public int X,Y; }

  public static IntPtr Main(int pid, out string info){
    IntPtr best=IntPtr.Zero; int area=0;
    var names=new List<string>();
    EnumWindows(delegate(IntPtr h, IntPtr l){
      uint p; GetWindowThreadProcessId(h,out p);
      if((int)p==pid){
        R r; GetWindowRect(h,out r);
        int w=r.Rr-r.L, ht=r.B-r.T;
        if(w<=0||ht<=0) return true;
        var cb=new StringBuilder(256); GetClassName(h,cb,256);
        names.Add(string.Format("0x{0:X} vis={1} {2}x{3}@{4},{5}", h.ToInt64(), IsWindowVisible(h), w, ht, r.L, r.T));
        if(IsWindowVisible(h) && r.L > -10000 && w>800 && ht>600){
          int a=w*ht; if(a>area){ area=a; best=h; }
        }
      }
      return true;
    }, IntPtr.Zero);
    info=string.Join(" | ", names.ToArray());
    return best;
  }
  public static string Geo(IntPtr h){
    R w,c; P p; p.X=0;p.Y=0;
    GetWindowRect(h,out w); GetClientRect(h,out c); ClientToScreen(h,ref p);
    return string.Format("window {0}x{1}@{2},{3} | client {4}x{5}@{6},{7} | NC l={8} t={9} r={10} b={11}",
      w.Rr-w.L,w.B-w.T,w.L,w.T, c.Rr,c.B,p.X,p.Y,
      p.X-w.L, p.Y-w.T, w.Rr-(p.X+c.Rr), w.B-(p.Y+c.B));
  }
}
"@
[MW2]::SetProcessDPIAware() | Out-Null

if($TargetPid -eq 0){ $procId = (Get-Process SoulLink -ErrorAction SilentlyContinue | Sort-Object StartTime -Desc | Select-Object -First 1).Id } else { $procId = $TargetPid }
$info = ""
$h = [MW2]::Main($procId, [ref]$info)
Write-Host "候选: $info"
if($h -eq [IntPtr]::Zero){ "NO MAIN WINDOW"; exit 2 }
"选定 hwnd=0x$($h.ToInt64().ToString('X'))"
[MW2]::ShowWindow($h,9) | Out-Null
[MW2]::SetWindowPos($h,[IntPtr](-1),0,0,0,0,0x0001 -bor 0x0040) | Out-Null
[MW2]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 2000
[MW2]::Geo($h)

function Shot([string]$name, [IntPtr]$hh){
  $r = New-Object MW2+R
  [MW2]::GetWindowRect($hh,[ref]$r) | Out-Null
  $w=$r.Rr-$r.L; $ht=$r.B-$r.T
  if($w -le 0 -or $ht -le 0){ "  bad size"; return }
  $bmp=[System.Drawing.Bitmap]::new($w,$ht)
  $g=[System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($r.L,$r.T,0,0,$bmp.Size)
  $g.Dispose()
  $bmp.Save($name,[System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  "  saved $name  $w x $ht  @$($r.L),$($r.T)"
}
Shot "E:\\Documents\\deepseek-harness\\SoulLink\\tools\\seq\\sz-big.png" $h

"--- 缩到 900x600 ---"
[MW2]::SetWindowPos($h,[IntPtr]::Zero,0,0,900,600,0x0004 -bor 0x0010) | Out-Null
Start-Sleep -Milliseconds 1500
[MW2]::Geo($h)
Shot "E:\\Documents\\deepseek-harness\\SoulLink\\tools\\seq\\sz-small.png" $h

"--- 恢复 1613x1076 ---"
[MW2]::SetWindowPos($h,[IntPtr]::Zero,0,0,1613,1076,0x0004 -bor 0x0010) | Out-Null
Start-Sleep -Milliseconds 1200
[MW2]::Geo($h)
Shot "E:\\Documents\\deepseek-harness\\SoulLink\\tools\\seq\\sz-back.png" $h