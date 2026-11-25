Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class Live {
  public delegate bool EP(IntPtr h,IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EP p,IntPtr a);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f,int dx,int dy,uint d,UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public static string[] Titles(uint pid){ var l=new List<string>();
    EnumWindows((h,a)=>{ uint p; GetWindowThreadProcessId(h,out p);
      if(p==pid){ var s=new StringBuilder(512); GetWindowText(h,s,512); if(IsWindowVisible(h)) l.Add(h+"|"+s); }
      return true; }, IntPtr.Zero); return l.ToArray(); }
  public static void Click(int x,int y){ SetCursorPos(x,y); System.Threading.Thread.Sleep(120);
    mouse_event(2,0,0,0,UIntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(4,0,0,0,UIntPtr.Zero); }
  public static void Drag(int x1,int y1,int x2,int y2){ SetCursorPos(x1,y1); System.Threading.Thread.Sleep(150);
    mouse_event(2,0,0,0,UIntPtr.Zero); System.Threading.Thread.Sleep(150);
    for(int i=1;i<=10;i++){ SetCursorPos(x1+(x2-x1)*i/10,y1+(y2-y1)*i/10); System.Threading.Thread.Sleep(45); }
    System.Threading.Thread.Sleep(120); mouse_event(4,0,0,0,UIntPtr.Zero); }
}
"@
function Get-Root([IntPtr]$h){ return [Windows.Automation.AutomationElement]::FromHandle($h) }
function Find-ByHelp($root,[string]$help){
  $c=New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::HelpTextProperty,$help)
  return $root.FindFirst([Windows.Automation.TreeScope]::Descendants,$c)
}