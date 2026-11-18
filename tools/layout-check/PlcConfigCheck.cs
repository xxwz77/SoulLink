using System;
using System.IO;
using System.Text.Json;
using SoulLink.Models;
using SoulLink.Services;
public static class PlcConfigCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 public static int Main()
 {
  string dir=Path.Combine(Path.GetTempPath(),"SoulLinkPlcConfigCheck-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
  string path=Path.Combine(dir,"config.json");Environment.SetEnvironmentVariable("SOULLINK_CONFIG",path);
  string json="""{"Accounts":[{"Id":"a1","UserName":"tester"}],"Automations":[{"Id":"flow1","Name":"old","AccountId":"a1","Nodes":[{"Id":"c","Kind":"Condition","Type":"CondTimeRange","Params":{"from":"00:00","to":"23:59"}},{"Id":"a","Kind":"Action","Type":"ActDelay","Params":{"seconds":"0"}}],"Edges":[{"From":"c","To":"a","Branch":""}],"Active":true,"Latched":["c"]}]}""";
  File.WriteAllText(path,json);
  var cfg=ConfigStore.Load();
  Check(cfg.Accounts.Count==1&&cfg.Accounts[0].UserName=="tester","迁移保留账号");
  Check(cfg.Automations.Count==1&&cfg.Automations[0].EngineVersion==2,"旧 JSON 迁移为 v2");
  Check(File.Exists(Path.Combine(dir,"config-before-flow-migration.json")),"迁移前保留原配置备份");
  var jsonOptions=new JsonSerializerOptions{Converters={new System.Text.Json.Serialization.JsonStringEnumConverter()}};
  var saved=JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path),jsonOptions)!;
  Check(saved.Automations[0].EngineVersion==2&&!saved.Automations[0].Active&&saved.Automations[0].Latched.Count==0,"新格式落盘且清旧锁存");
  var flow=cfg.Automations[0];flow.Nodes.Add(new FlowNode{Id="end",Kind=FlowNodeKind.Terminate,Type=FlowNodeType.ActEndFlow,DayBoundaryHour=4});flow.DailyLatchKey="2026-11-17";flow.RunDayKey="2026-11-17";flow.RunComplete=true;flow.WaitingActions.Add("a");flow.RunningActions["a"]=new FlowActionResult{Ok=true,Message="done"};ConfigStore.Save(cfg);
  var reboot=ConfigStore.Load();var savedFlow=reboot.Automations[0];var at=new DateTime(2026,11,18,3,59,59);
  Check(savedFlow.DailyLatchKey=="2026-11-17"&&PlcLogic.IsRunComplete(savedFlow,at),"04:00 前 END 重启保留");
  Check(!PlcLogic.IsRunComplete(savedFlow,at.AddSeconds(1))&&PlcLogic.DayKey(at.AddSeconds(1),4)=="2026-11-18","04:00 整跨日释放");
  Check(savedFlow.EndBoundaryHour==4&&savedFlow.RunDayKey=="2026-11-17"&&savedFlow.WaitingActions.Contains("a")&&savedFlow.RunningActions.ContainsKey("a"),"END 日界与分支进度全部重启保留");
  var savedJson=File.ReadAllText(path);var doc=System.Text.Json.Nodes.JsonNode.Parse(savedJson)!.AsObject();doc["Automations"]![0]!.AsObject()["LifecycleVersion"]=0;doc["Automations"]![0]!.AsObject()["RunDayKey"]="";File.WriteAllText(path,doc.ToJsonString());
  var migrated=ConfigStore.Load().Automations[0];Check(migrated.LifecycleVersion==1&&migrated.RunDayKey.Length>0&&migrated.WaitingActions.Count==0,"旧 v2 生命周期显式迁移并保守清进度");
  Environment.SetEnvironmentVariable("SOULLINK_CONFIG",null);
  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
 }
}