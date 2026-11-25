param([Parameter(Mandatory=$true)][int]$ProcessId)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class PreviewCaptureNative {
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    public static void ClickAt(int x, int y) { SetCursorPos(x, y); mouse_event(2,0,0,0,UIntPtr.Zero); mouse_event(4,0,0,0,UIntPtr.Zero); }
}
"@
$process = Get-Process -Id $ProcessId -ErrorAction Stop
if ($process.MainWindowTitle -ne 'SoulLink · 樱雾星云｜动态背景预览') { throw 'Unexpected process identity.' }
[PreviewCaptureNative]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 700
$rect = New-Object PreviewCaptureNative+RECT
[PreviewCaptureNative]::GetWindowRect($process.MainWindowHandle, [ref]$rect) | Out-Null
& $env:SOULLINK_PREVIEW_PYTHON -c "from PIL import ImageGrab; import sys; box=tuple(map(int,sys.argv[1:5])); im=ImageGrab.grab(all_screens=True).crop(box); im.save(sys.argv[5])" $rect.Left $rect.Top $rect.Right $rect.Bottom "$PSScriptRoot/../../artifacts/backdrop-preview/sakura-nebula-ui.png"
[PreviewCaptureNative]::SetWindowPos($process.MainWindowHandle,[IntPtr]::Zero,0,0,1200,750,2) | Out-Null
Start-Sleep -Milliseconds 500
[PreviewCaptureNative]::GetWindowRect($process.MainWindowHandle,[ref]$rect) | Out-Null
Add-Type -AssemblyName System.Windows.Forms
$x = $rect.Right - [int](($rect.Right-$rect.Left)*0.114)
$y = $rect.Bottom - [int](($rect.Bottom-$rect.Top)*0.050)
[System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($x,$y)
[PreviewCaptureNative]::ClickAt($x,$y)
Start-Sleep -Milliseconds 800
& $env:SOULLINK_PREVIEW_PYTHON -c "from PIL import ImageGrab; import sys; box=tuple(map(int,sys.argv[1:5])); im=ImageGrab.grab(all_screens=True).crop(box); im.save(sys.argv[5])" $rect.Left $rect.Top $rect.Right $rect.Bottom "$PSScriptRoot/../../artifacts/backdrop-preview/sakura-nebula-pure.png"
Write-Output "Saved window-only UI and pure-background images."
