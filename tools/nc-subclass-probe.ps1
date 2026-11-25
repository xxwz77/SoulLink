param([int]$TargetPid,[ValidateSet('install','remove')][string]$Mode='install')
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
public static class NcSubclass {
 public delegate bool EnumProc(IntPtr h,IntPtr l);
 public delegate IntPtr SubclassProc(IntPtr h,int msg,IntPtr wp,IntPtr lp,UIntPtr id,UIntPtr data);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb,IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h,ref POINT p);
 [DllImport("comctl32.dll")] public static extern IntPtr DefSubclassProc(IntPtr h,int msg,IntPtr wp,IntPtr lp);
 [DllImport("comctl32.dll")] public static extern bool SetWindowSubclass(IntPtr h,SubclassProc cb,UIntPtr id,UIntPtr data);
 [DllImport("comctl32.dll")] public static extern bool RemoveWindowSubclass(IntPtr h,SubclassProc cb,UIntPtr id);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
 [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
 [StructLayout(LayoutKind.Sequential)] public struct NC { public RECT A,B,C; public IntPtr Pos; }
 private static readonly SubclassProc Callback=Hook;
 private static IntPtr target;
 private static IntPtr Hook(IntPtr h,int msg,IntPtr wp,IntPtr lp,UIntPtr id,UIntPtr data) {
  if(msg==0x83 && wp!=IntPtr.Zero) {
   NC n=Marshal.PtrToStructure<NC>(lp); n.A=n.B; Marshal.StructureToPtr(n,lp,false);
   DefSubclassProc(h,msg,wp,lp);
   n=Marshal.PtrToStructure<NC>(lp); n.A=n.B; Marshal.StructureToPtr(n,lp,false);
   return IntPtr.Zero;
  }
  return DefSubclassProc(h,msg,wp,lp);
 }
 public static IntPtr Main(int pid) {
  IntPtr best=IntPtr.Zero;long area=0;
  EnumWindows((h,l)=>{uint p;GetWindowThreadProcessId(h,out p);if(p==(uint)pid&&IsWindowVisible(h)){RECT r;GetWindowRect(h,out r);long a=(long)(r.R-r.L)*(r.B-r.T);if(r.L>-10000&&a>area){area=a;best=h;}}return true;},IntPtr.Zero);return best;
 }
 public static string Install(int pid) {
  target=Main(pid);if(target==IntPtr.Zero)return "NO_WINDOW";
  bool ok=SetWindowSubclass(target,Callback,(UIntPtr)0x534C,UIntPtr.Zero);
  RECT wr,cr;POINT pt=new POINT();GetWindowRect(target,out wr);GetClientRect(target,out cr);ClientToScreen(target,ref pt);
  return "installed="+ok+" hwnd="+target+" window="+(wr.R-wr.L)+"x"+(wr.B-wr.T)+" client="+cr.R+"x"+cr.B+" origin="+pt.X+","+pt.Y;
 }
}
"@
[NcSubclass]::SetProcessDPIAware()|Out-Null
if($Mode -eq 'install'){[NcSubclass]::Install($TargetPid)}
