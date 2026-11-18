using System;using System.Diagnostics;using System.IO;using System.Linq;using SoulLink.Services;
public static class BusinessDayCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 public static int Main()
 {
  BusinessDayData.ResetForTests();var at=new DateTime(2026,11,18,2,0,0);
  string file=Path.Combine(Path.GetTempPath(),"SoulLink-day-"+Guid.NewGuid().ToString("N")+".txt");
  File.WriteAllText(file,"日期:2026/11/17，运行收尾路线收尾额外A，狗粮经验142480，摩拉38957\n日期:2026/11/18，运行收尾路线收尾额外B，狗粮经验5000，摩拉100");
  Check(DogfoodRecordReader.Read(file,at,4).Route==DogfoodEndingRoute.EndingA,"04:00 前狗粮属于前一业务日");
  Check(DogfoodRecordReader.Read(file,at,0).Route==DogfoodEndingRoute.EndingB,"同一时刻 00:00 流程使用新自然日");
  BusinessDayData.SetDogfood("a",file,DogfoodRecordReader.Parse(file));
  string previous="[2026-11-17 23:59:00.000] [INF] 配置组 \"旧组\" 加载完成，共1个脚本，开始执行\n[2026-11-17 23:59:10.000] [INF] 狗粮路线获取狗粮经验: 100\n";
  string today="[2026-11-18 01:00:00.000] [INF] 配置组 \"凌晨组\" 加载完成，共1个脚本，开始执行\n[2026-11-18 01:00:10.000] [INF] 狗粮路线获取狗粮经验: 200\n";
  BusinessDayData.Reset("a","today.log",today,previous,"previous.log");
  var v4=BusinessDayData.GetView("a",4,at);var v0=BusinessDayData.GetView("a",0,at);
  Check(v4.Groups.Get("旧组")!=null&&v4.Groups.Get("凌晨组")!=null&&v0.Groups.Get("旧组")==null&&v0.Groups.Get("凌晨组")!=null,"同一份日志同时正确投影 00:00 和 04:00 配置组");
  Check(v4.Stats.DogfoodExp==300&&v0.Stats.DogfoodExp==200,"统计分别遵循相同日界");
  BusinessDayData.Append("a","previous.log","[2026-11-17 23:59:30.000] [INF] 配置组 \"旧组\" 执行结束\n");
  Check(BusinessDayData.GetView("a",4,at).Groups.Get("旧组")!.State==ScriptGroupState.Finished,"04:00 期间上一文件增量进入旧组投影");
  long reads=BusinessDayData.SourceReads;
  foreach(int flows in new[]{1,10,100}){var sw=Stopwatch.StartNew();for(int i=0;i<flows;i++){BusinessDayData.GetView("a",i%2==0?0:4,at);}sw.Stop();Console.WriteLine(flows+" flows: "+sw.Elapsed.TotalMilliseconds.ToString("0.000")+" ms; source reads="+BusinessDayData.SourceReads);}
  Check(BusinessDayData.SourceReads==reads,"1/10/100 个流程共享缓存，源读取次数不随流程数增加");
  ProcessOwnerSnapshot.Set("\"YuanShen.exe\",\"123\",\"Console\",\"8\",\"100 K\",\"Running\",\"DOMAIN\\A2\",\"0:00:00\",\"game\"");
  Check(ProcessOwnerSnapshot.Exists("YuanShen","A2")&&ProcessOwnerSnapshot.Exists("YuanShen","",8)&&!ProcessOwnerSnapshot.Exists("YuanShen","A3"),"进程名、会话和所有者严格匹配");
  File.Delete(file);Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
 }
}
