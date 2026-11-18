using System;using System.Collections.Generic;using System.Threading;using System.Threading.Tasks;using SoulLink.Services;
public static class BgiStartGuardCheck
{
 static bool running = true;
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 public static async Task<int> Main()
 {
  var steps=new List<string>();bool allowStop=true,allowClose=true,allowExit=true;
  var ports=new BgiStartGuard.Ports{IsRunning=(s,e)=>running,Hotkey=(s,e)=>"F12",StopScript=(s,k)=>steps.Add("stop-script"),ScriptStopped=(s,c)=>{steps.Add("confirm-script");return Task.FromResult(allowStop);},CloseProcesses=(s,c)=>{steps.Add("close");return Task.FromResult(allowClose);},ConfirmExited=(s,c)=>{steps.Add("confirm-exit");return Task.FromResult(allowExit);}};
  var ok=await BgiStartGuard.BeforeStartAsync(2,"BetterGI.exe",default,ports);
  Check(ok.Ok&&string.Join(",",steps)=="stop-script,confirm-script,close,confirm-exit","完整顺序：停止脚本→确认停止→关闭→确认退出");
  steps.Clear();allowStop=false;ok=await BgiStartGuard.BeforeStartAsync(2,"BetterGI.exe",default,ports);Check(!ok.Ok&&!steps.Contains("close"),"脚本未停止确认时阻止关闭和启动");
  steps.Clear();allowStop=true;allowClose=false;ok=await BgiStartGuard.BeforeStartAsync(2,"BetterGI.exe",default,ports);Check(!ok.Ok&&!steps.Contains("confirm-exit"),"关闭失败时不继续");
  steps.Clear();allowClose=true;allowExit=false;ok=await BgiStartGuard.BeforeStartAsync(2,"BetterGI.exe",default,ports);Check(!ok.Ok,"进程未退出时启动被拒绝");
  steps.Clear();ports=new BgiStartGuard.Ports{IsRunning=(s,e)=>running,Hotkey=(s,e)=>"",StopScript=(s,k)=>steps.Add("stop-script"),CloseProcesses=(s,c)=>{steps.Add("direct-close");return Task.FromResult(true);},ConfirmExited=(s,c)=>{steps.Add("exit");return Task.FromResult(true);}};
  ok=await BgiStartGuard.BeforeStartAsync(2,"BetterGI.exe",default,ports);Check(ok.Ok&&string.Join(",",steps)=="direct-close,exit","没有快捷键时直接关闭，不等待脚本停止");
  steps.Clear();running=false;ok=await BgiStartGuard.BeforeStartAsync(2,"BetterGI.exe",default,ports);Check(ok.Ok&&steps.Count==0,"BGI 未运行时直接继续");
  steps.Clear();running=true;allowStop=false;ports=new BgiStartGuard.Ports{IsRunning=(s,e)=>true,Hotkey=(s,e)=>"F12",StopScript=(s,k)=>steps.Add("stop-script"),ScriptStopped=(s,c)=>Task.FromResult(false)};ok=await BgiStartGuard.BeforeStartAsync(2,"BetterGI.exe",default,ports);Check(!ok.Ok,"有快捷键但停止失败仍阻止继续");
  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
 }
}