using System;using System.Collections.Generic;using System.Threading;using System.Threading.Tasks;using SoulLink.Services;
public static class BgiStartGuardCheck
{
 static bool running = true;
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 public static async Task<int> Main()
 {
  var steps=new List<string>();bool allowStop=true,allowClose=true,allowExit=true;
  string via="relay";
  var ports=new BgiStartGuard.Ports{
    IsRunning=(s,e)=>running, Hotkey=(s,e)=>"F12", FindPid=(s,e)=>{steps.Add("find-pid");return 4321;},
    StopScript=(sid,pid,hk,c)=>{steps.Add("send:"+via+":"+pid);return Task.FromResult((true,via,via));},
    ScriptStopped=(s,hk,c)=>{steps.Add("confirm-script:"+hk);return Task.FromResult(allowStop);},
    CloseProcesses=(s,e,c)=>{steps.Add("close");return Task.FromResult(allowClose);},
    ConfirmExited=(s,e,c)=>{steps.Add("confirm-exit:"+e);return Task.FromResult(allowExit);}};

  var ok=await BgiStartGuard.BeforeStartAsync(2,"BetterGI.exe",default,ports);
  // ★ hotkey 会一路传到 ScriptStopped（日志按键名判定要用），所以断言里带 :F12
  Check(ok.Ok&&string.Join(",",steps)=="find-pid,send:relay:4321,confirm-script:F12,close,confirm-exit:BetterGI.exe","完整顺序：找pid→投送通道发键→等F12确认→关闭→确认退出");
  Check(ok.StopMethod=="relay","StopMethod 记录为 relay");

  // 投送不通 → 兜底消息
  steps.Clear(); via="postmessage";
  ok=await BgiStartGuard.BeforeStartAsync(2,"BetterGI.exe",default,ports);
  Check(ok.Ok&&steps.Contains("send:postmessage:4321"),"投送通道不可用时改用窗口消息");
  Check(ok.StopMethod=="postmessage","StopMethod 记录为 postmessage");

  // 有快捷键但没等到日志 → 仍然杀进程（用户要求）
  steps.Clear(); via="relay"; allowStop=false;
  ok=await BgiStartGuard.BeforeStartAsync(2,"BetterGI.exe",default,ports);
  Check(ok.Ok&&steps.Contains("close")&&steps.Contains("confirm-exit:BetterGI.exe"),"没等到停止日志仍然杀进程并确认退出");

  // 两条都发不出去 → 不崩，仍然杀进程
  steps.Clear(); allowStop=true;
  var noSend=new BgiStartGuard.Ports{
    IsRunning=(s,e)=>true, Hotkey=(s,e)=>"F12", FindPid=(s,e)=>0,
    StopScript=(sid,pid,hk,c)=>{steps.Add("send-failed");return Task.FromResult((false,"none","两条都不通"));},
    CloseProcesses=(s,e,c)=>{steps.Add("close");return Task.FromResult(true);},
    ConfirmExited=(s,e,c)=>{steps.Add("exit");return Task.FromResult(true);}};
  ok=await BgiStartGuard.BeforeStartAsync(2,"BetterGI.exe",default,noSend);
  Check(ok.Ok&&steps.Contains("close"),"两条通道都失败时仍然杀进程");

  // 关闭失败 → 阻止
  steps.Clear(); allowClose=false;
  ok=await BgiStartGuard.BeforeStartAsync(2,"BetterGI.exe",default,ports);
  Check(!ok.Ok&&!steps.Contains("confirm-exit:BetterGI.exe"),"关闭失败时不继续");

  // 没退干净 → 阻止
  steps.Clear();allowClose=true;allowExit=false;
  ok=await BgiStartGuard.BeforeStartAsync(2,"BetterGI.exe",default,ports);
  Check(!ok.Ok,"进程未退出时启动被拒绝");

  // 没有快捷键 → 直接杀进程
  steps.Clear();
  var noKey=new BgiStartGuard.Ports{
    IsRunning=(s,e)=>running, Hotkey=(s,e)=>"", FindPid=(s,e)=>{steps.Add("find-pid");return 4321;},
    StopScript=(sid,pid,hk,c)=>{steps.Add("send");return Task.FromResult((true,"x","x"));},
    CloseProcesses=(s,e,c)=>{steps.Add("direct-close");return Task.FromResult(true);},
    ConfirmExited=(s,e,c)=>{steps.Add("exit");return Task.FromResult(true);}};
  ok=await BgiStartGuard.BeforeStartAsync(2,"BetterGI.exe",default,noKey);
  Check(ok.Ok&&string.Join(",",steps)=="direct-close,exit","没有快捷键时直接关闭");

  steps.Clear();running=false;
  ok=await BgiStartGuard.BeforeStartAsync(2,"BetterGI.exe",default,noKey);
  Check(ok.Ok&&steps.Count==0,"BGI 未运行时直接继续");

  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
 }
}