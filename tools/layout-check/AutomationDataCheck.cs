using System;using SoulLink.Services;
public static class AutomationDataCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 public static int Main()
 {
  string log="[2026-11-18 09:00:00.000] [INF] 配置组 \"A组\" 加载完成，共3个脚本，开始执行\n[2026-11-18 09:10:00.000] [INF] 配置组 \"A组\" 执行结束";
  var t=new ScriptGroupTracker();t.Update(log);var r=t.Get("A组")!;
  Check(r.State==ScriptGroupState.Finished,"配置组状态供条件与页面共用");
  Check(r.StartedAt==new DateTime(2026,11,18,9,0,0)&&r.EndedAt==new DateTime(2026,11,18,9,10,0),"开始结束使用日志真实时间而非读取时间");
  Check(r.TimeIsExact&&r.Duration==TimeSpan.FromMinutes(10),"页面可显示精确事件时间和耗时");
  var interrupted=new ScriptGroupTracker();
  interrupted.Update("[2026-11-18 10:00:00] 配置组 \"砍树\" 加载完成，共2个脚本，开始执行");
  interrupted.Update("[2026-11-18 10:05:00] 配置组 \"CD采集-额外狗粮\" 加载完成，共1个脚本，开始执行");
  Check(interrupted.Get("砍树")!.State==ScriptGroupState.Interrupted,"砍树被后续配置组接替后显示已中断而非执行中");
  Check(interrupted.Get("CD采集-额外狗粮")!.State==ScriptGroupState.Running,"只有最新启动的配置组显示执行中");
  Check(!ScriptGroupTracker.ValidGroupName("The operation was canceled."),"异常对象名不被当成配置组");
  var unknown=new DogfoodRecord();unknown.Reason="找不到记录文件";Check(!unknown.IsValid&&unknown.Route==DogfoodEndingRoute.Unknown,"无狗粮记录不会误判为 A 或 B");
  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
 }
}