
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Fg {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
}
"@
$procId = Get-Content "$env:TEMP\st.pid"
$proc = Get-Process -Id $procId
$h = $proc.MainWindowHandle
$fg = [Fg]::GetForegroundWindow()
$tidFg = [Fg]::GetWindowThreadProcessId($fg, [IntPtr]::Zero)
$tidMe = [Fg]::GetCurrentThreadId()
[Fg]::AttachThreadInput($tidMe, $tidFg, $true) | Out-Null
[Fg]::ShowWindow($h, 9) | Out-Null      # SW_RESTORE
[Fg]::BringWindowToTop($h) | Out-Null
[Fg]::SetForegroundWindow($h) | Out-Null
[Fg]::AttachThreadInput($tidMe, $tidFg, $false) | Out-Null
Start-Sleep -Milliseconds 900
"foreground now = " + [Fg]::GetForegroundWindow() + "  (target=$h)"
