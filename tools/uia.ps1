param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [string]$InvokeName = "",
    [string]$InvokeHelp = "",
    [string]$ClickName = "",
    [string]$ReadTextId = ""
)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SL {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f,int dx,int dy,uint d,IntPtr e);
}
'@
$proc = Get-Process -Id $ProcessId -ErrorAction Stop
$rootEl = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
if ($null -eq $rootEl) { throw 'main window UIA not found' }
if ($InvokeName -or $InvokeHelp) {
    $prop = if ($InvokeHelp) { [System.Windows.Automation.AutomationElement]::HelpTextProperty } else { [System.Windows.Automation.AutomationElement]::NameProperty }
    $value = if ($InvokeHelp) { $InvokeHelp } else { $InvokeName }
    $cond = New-Object System.Windows.Automation.PropertyCondition($prop, $value)
    $found = $rootEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($found.Count -ne 1) { throw ('expected 1 match for "' + $value + '", got ' + $found.Count) }
    $el = $found.Item(0)
    $b = $el.Current.BoundingRectangle
    if ($el.Current.IsOffscreen -or $b.Width -le 0) { throw 'target not safely invokable' }
    $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output ('INVOKED=' + $value + ' bounds=' + $b)
}
if ($ClickName) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $ClickName)
    $found = $rootEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($found.Count -ne 1) { throw ('expected 1 click match for "' + $ClickName + '", got ' + $found.Count) }
    $b = $found.Item(0).Current.BoundingRectangle
    if ($b.Width -le 0) { throw 'click target invalid' }
    $x = [int]($b.X + $b.Width / 2); $y = [int]($b.Y + $b.Height / 2)
    [void][SL]::SetCursorPos($x, $y)
    [SL]::mouse_event(2,0,0,0,[IntPtr]::Zero)
    [SL]::mouse_event(4,0,0,0,[IntPtr]::Zero)
    Write-Output ('CLICKED=' + $ClickName + ' at=' + $x + ',' + $y)
}
if ($ReadTextId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $ReadTextId)
    $el = $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($null -ne $el) { Write-Output ($ReadTextId + '=' + $el.Current.Name) }
}
