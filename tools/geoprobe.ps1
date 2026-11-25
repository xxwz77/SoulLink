
param([string]$Exe = "E:\Documents\deepseek-harness\SoulLink\dist\SoulLink.exe")
$ErrorActionPreference = "Stop"
Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class Geo {
  public delegate bool EP(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool EnumWindows(EP cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref P p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(P p);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x,int y,int cx,int cy,uint f);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out R r, int sz);
  [DllImport("dwmapi.dll", EntryPoint="DwmGetWindowAttribute")] public static extern int DwmAttrInt(IntPtr h, int a, out int v, int sz);
  public struct R { public int L,T,Rr,B; }
  public struct P { public int X,Y; }

  public static IntPtr Main(int pid){
    IntPtr best = IntPtr.Zero; int bestArea = 0;
    EnumWindows(delegate(IntPtr h, IntPtr l){
      uint p; GetWindowThreadProcessId(h, out p);
      if((int)p==pid && IsWindowVisible(h)){
        R r; GetWindowRect(h, out r);
        int a = (r.Rr-r.L)*(r.B-r.T);
        if(a > bestArea && (r.Rr-r.L) > 400 && (r.B-r.T) > 300){ best = h; bestArea = a; }
      }
      return true;
    }, IntPtr.Zero);
    return best;
  }

  public static string Dump(IntPtr h, string tag){
    var sb = new StringBuilder();
    R wr, cr; P pt;
    GetWindowRect(h, out wr); GetClientRect(h, out cr);
    pt = new P(); ClientToScreen(h, ref pt);
    int style = GetWindowLong(h, -16), ex = GetWindowLong(h, -20);
    sb.AppendLine("===== " + tag + " =====");
    sb.AppendLine(string.Format("WindowRect  {0},{1} .. {2},{3}   {4} x {5}", wr.L,wr.T,wr.Rr,wr.B, wr.Rr-wr.L, wr.B-wr.T));
    sb.AppendLine(string.Format("ClientRect  0,0 .. {0},{1}        {2} x {3}", cr.Rr,cr.B, cr.Rr, cr.B));
    sb.AppendLine(string.Format("ClientOrigin(screen) {0},{1}", pt.X, pt.Y));
    sb.AppendLine("--- NC 厚度 ---");
    sb.AppendLine("  left=" + (pt.X-wr.L) + " top=" + (pt.Y-wr.T) + " right=" + (wr.Rr-(pt.X+cr.Rr)) + " bottom=" + (wr.B-(pt.Y+cr.B)));
    sb.AppendLine("style=0x" + style.ToString("X8") + " ex=0x" + ex.ToString("X8")
      + " TOPMOST=" + ((ex & 0x8)!=0) + " LAYERED=" + ((ex & 0x80000)!=0));
    R ext; int hr = DwmGetWindowAttribute(h, 9, out ext, 16);
    sb.AppendLine(string.Format("DWM extFrame hr={0}  {1},{2}..{3},{4}", hr, ext.L, ext.T, ext.Rr, ext.B));
    int v;
    DwmAttrInt(h, 33, out v, 4); sb.AppendLine("  CORNER_PREF = " + v);
    DwmAttrInt(h, 34, out v, 4); sb.AppendLine("  BORDER_COLOR = 0x" + v.ToString("X8"));
    DwmAttrInt(h, 2,  out v, 4); sb.AppendLine("  NCRENDERING = " + v);
    sb.AppendLine("metrics: sizeframe=" + GetSystemMetrics(32) + "/" + GetSystemMetrics(33)
      + " padded=" + GetSystemMetrics(92) + " border=" + GetSystemMetrics(5) + "/" + GetSystemMetrics(6));
    // 在 8px 带内的一个点上问 WindowFromPoint
    var q = new P(); q.X = wr.L + 6; q.Y = wr.T + (wr.B-wr.T)/2;
    IntPtr hit = WindowFromPoint(q);
    var cb = new StringBuilder(256); GetClassName(hit, cb, 256);
    sb.AppendLine(string.Format("WindowFromPoint({0},{1}) = 0x{2:X} cls={3} sameWindow={4}",
      q.X, q.Y, hit.ToInt64(), cb.ToString(), hit==h));
    return sb.ToString();
  }
}
"@
[Geo]::SetProcessDPIAware() | Out-Null

$p = Start-Process -FilePath $Exe -PassThru
"launched pid=$($p.Id)"
$h = [IntPtr]::Zero
for($i=0;$i -lt 60 -and $h -eq [IntPtr]::Zero;$i++){ Start-Sleep -Milliseconds 250; $h = [Geo]::Main($p.Id) }
if($h -eq [IntPtr]::Zero){ "NO_MAIN_WINDOW"; exit 2 }
"main hwnd = 0x$($h.ToInt64().ToString('X'))"

Start-Sleep -Milliseconds 2500
[Geo]::SetWindowPos($h,[IntPtr](-1),0,0,0,0,0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
[Geo]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 1200
Write-Host ([Geo]::Dump($h, "聚焦 (focused)")) -NoNewline

[Geo]::SetForegroundWindow([Geo]::GetDesktopWindow()) | Out-Null
Start-Sleep -Milliseconds 1500
Write-Host ([Geo]::Dump($h, "失焦 (deactivated)")) -NoNewline

"still pid=$($p.Id) hwnd=0x$($h.ToInt64().ToString('X'))"
