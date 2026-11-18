using System;using System.Collections.Generic;using SoulLink.Models;using SoulLink.Services;
public static class LifecycleCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 public static int Main()
 {
  var f=new AutomationFlow();var a=new FlowNode{Id="a",Kind=FlowNodeKind.Action};var b=new FlowNode{Id="b",Kind=FlowNodeKind.Action};f.Nodes.AddRange(new[]{a,b});
  var at=new DateTime(2026,11,18,3,0,0);FlowRunLifecycle.StartRun(f,at);Check(f.RunDayKey=="2026-11-18"&&!f.RunComplete,"无 END 运行不锁存");
  var r=new Dictionary<string,FlowActionResult>{{"a",new FlowActionResult{Ok=true}}};
  Check(!FlowRunLifecycle.AllActivePathsComplete(f,r,new HashSet<string>()),"并行缺一条时不收尾");
  Check(FlowRunLifecycle.AllActivePathsComplete(f,r,new HashSet<string>{"a"}),"选择分支只等待选中一路");
  r["b"]=new FlowActionResult{Ok=true};Check(FlowRunLifecycle.AllActivePathsComplete(f,r,new HashSet<string>()),"并行全部动作完成后收尾");
  FlowRunLifecycle.CompleteRun(f,at,false);Check(f.DailyLatchKey==""&&!f.IsDailyComplete,"无 END 完成清空状态，可再次执行");

  var g=new AutomationFlow();var c=new FlowNode{Id="c",Kind=FlowNodeKind.Action};var end=new FlowNode{Id="e",Kind=FlowNodeKind.Terminate,DayBoundaryHour=4};g.Nodes.AddRange(new[]{c,end});
  FlowRunLifecycle.CompleteRun(g,at,true);
  Console.WriteLine("  END latch="+g.DailyLatchKey+" at="+at.ToString("HH:mm")+" boundary="+PlcLogic.EndBoundaryHour(g));
  Check(PlcLogic.IsDailyComplete(g,new DateTime(2026,11,18,3,59,0)),"END 04:00 前保持今日已执行");
  Check(!PlcLogic.IsDailyComplete(g,new DateTime(2026,11,18,4,0,0)),"END 04:00 到达自动释放");
  Check(!PlcLogic.IsDailyComplete(g,new DateTime(2026,11,19,12,0,0)),"END 隔日重新可执行");
  PlcLogic.CompleteToday(g,new DateTime(2026,11,18,12,0,0));PlcLogic.ClearDailyLatch(g);Check(!g.IsDailyComplete,"手动清除今日标记立即解除锁存");
  end.DayBoundaryHour=0;PlcLogic.ClearDailyLatch(g);PlcLogic.CompleteToday(g,new DateTime(2026,11,18,1,0,0));
  Check(PlcLogic.IsDailyComplete(g,new DateTime(2026,11,18,1,0,0))&&PlcLogic.EndBoundaryHour(g)==0,"00:00 END 日界使用自然日");

  // “今日是否会运行”的统一描述
  var loop=new AutomationFlow();
  var a1=loop.TodayRunState(new DateTime(2026,11,18,12,0,0));
  Check(a1.WillRun&&a1.Text.Contains("会运行")&&a1.Text.Contains("重复"),"无 END：今日会运行且可重复");
  var latched=new AutomationFlow{Nodes=new List<FlowNode>{new FlowNode{Id="e",Kind=FlowNodeKind.Terminate,DayBoundaryHour=4}}};
  latched.DailyLatchKey=PlcLogic.DayKey(new DateTime(2026,11,18,12,0,0),4);
  var a2=latched.TodayRunState(new DateTime(2026,11,18,12,0,0));
  Check(!a2.WillRun&&a2.Text.Contains("不会再运行")&&a2.Text.Contains("04:00"),"今日已执行：明确说明日界后恢复");
  var a3=new AutomationFlow().TodayRunState();
  var disabled=new AutomationFlow{Enabled=false};
  Check(!disabled.TodayRunState().WillRun&&disabled.TodayRunState().Text.Contains("未启用"),"未启用：今日不会运行");
  var paused=new AutomationFlow{Paused=true,PauseReason="手动暂停"};
  Check(!paused.TodayRunState().WillRun&&paused.TodayRunState().Text.Contains("已暂停"),"已暂停：今日不会运行并带原因");
  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
 }
}
