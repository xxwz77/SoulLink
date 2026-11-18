using System;using SoulLink.Models;
public static class TimeoutCheck
{
 public static int Main()
 {
  int fails=0;void Check(bool ok,string text){Console.WriteLine((ok?"ok   ":"FAIL ")+text);if(!ok)fails++;}
  var f=new AutomationFlow();f.Active=true;f.Latched.Add("old");f.Arm();
  Check(f.Active&&f.Latched.Count==1&&!f.IsLatched("old")&&!f.Latch("old"),"旧 Arm/Latch 不再改变实时条件或启动超时");
  Check(!f.SatisfiedContacts.Contains("old"),"旧状态不会进入本轮瞬时信号");
  f.Disarm();Check(!f.Active&&!f.ArmedAt.HasValue&&f.Latched.Count==0,"旧兼容状态安全清理");
  var a=new AutomationFlow();var b=new AutomationFlow();a.SatisfiedContacts.Add("momentary");Check(b.SatisfiedContacts.Count==0,"瞬时等待状态按流程独立");
  Check(PlcLogic.DayKey(new DateTime(2026,11,18,3,59,0),4)=="2026-11-17","无等待超时，跨日由业务日键决定");
  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
 }
}
