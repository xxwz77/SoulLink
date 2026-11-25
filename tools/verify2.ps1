
param()
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;using System.Text;using System.Runtime.InteropServices;
public class MF {
  public delegate bool EP(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool EnumWindows(EP cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x,int y,int cx,int cy,uint f);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  public struct R { public int L,T,Rr,B; }
  public static IntPtr Main(int pid){
    IntPtr best=IntPtr.Zero; int area=0;
    EnumWindows(delegate(IntPtr h, IntPtr l){
      uint p; GetWindowThreadProcessId(h,out p);
      if((int)p==pid && IsWindowVisible(h)){
        R r; GetWindowRect(h,out r);
        int w=r.Rr-r.L, ht=r.B-r.T;
        if(r.L>-10000 && w>800 && ht>600){ int a=w*ht; if(a>area){area=a;best=h;} }
      }
      return true; }, IntPtr.Zero);
    return best;
  }
}
"@
[MF]::SetProcessDPIAware() | Out-Null

$exe = "E:\\Documents\\deepseek-harness\\SoulLink\\dist\\SoulLink.exe"
$p = Start-Process -FilePath $exe -PassThru
"launched pid=$($p.Id)"
$h=[IntPtr]::Zero
for($i=0;$i -lt 80 -and $h -eq [IntPtr]::Zero;$i++){ Start-Sleep -Milliseconds 250; $h=[MF]::Main($p.Id) }
if($h -eq [IntPtr]::Zero){ "NO MAIN WINDOW"; Stop-Process -Id $p.Id -Force; exit 2 }
"hwnd=0x$($h.ToInt64().ToString('X'))"

[MF]::ShowWindow($h,9) | Out-Null
[MF]::SetWindowPos($h,[IntPtr](-1),0,0,0,0,0x0001 -bor 0x0040) | Out-Null
[MF]::SetForegroundWindow($h) | Out-Null

function Cap([string]$n){
  $r=New-Object MF+R
  [MF]::GetWindowRect($h,[ref]$r) | Out-Null
  $w=$r.Rr-$r.L; $ht=$r.B-$r.T
  $bmp=[System.Drawing.Bitmap]::new($w,$ht)
  $g=[System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($r.L,$r.T,0,0,$bmp.Size)
  $g.Dispose(); $bmp.Save($n,[System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  "  saved $n $w x $ht"
}

Start-Sleep -Milliseconds 2500
"fg=" + ([MF]::GetForegroundWindow() -eq $h)
Cap "E:\Documents\deepseek-harness\SoulLink\tools\seq\v2-A.png"

[MF]::SetForegroundWindow([MF]::GetDesktopWindow()) | Out-Null
Start-Sleep -Milliseconds 1800
"fg=" + ([MF]::GetForegroundWindow() -eq $h)
Cap "E:\Documents\deepseek-harness\SoulLink\tools\seq\v2-D.png"

[MF]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 1800
Cap "E:\Documents\deepseek-harness\SoulLink\tools\seq\v2-A2.png"

Stop-Process -Id $p.Id -Force
"closed pid=$($p.Id)"
