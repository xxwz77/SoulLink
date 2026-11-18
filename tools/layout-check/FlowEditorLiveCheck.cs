using System;using System.Collections.Generic;using System.IO;using System.Linq;using System.Text.Json;using System.Threading.Tasks;using System.Windows;using System.Windows.Threading;using SoulLink.Models;using SoulLink.Services;
public static class FlowEditorLiveCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 [STAThread]public static int Main()
 {
  var app=new Application();
  var cfg=new AppConfig();var account=new Account{Id="a",UserName="tester"};cfg.Accounts.Add(account);
  app.Startup+=async(_,__)=>{
   try
   {
    bool timeOk=true;
    var nodes=new List<FlowNode>{ new FlowNode{Id="t",Kind=FlowNodeKind.Condition,Type=FlowNodeType.CondTimeRange,
        Params=new Dictionary<string,string>{{"from","00:00"},{"to","23:59"}}},
        new FlowNode{Id="bad",Kind=FlowNodeKind.Action,Type=FlowNodeType.ActRunAction,Params=new(){{"actionId",""}}} };
    var flow=new AutomationFlow{AccountId="a",Nodes=nodes};
    flow.Edges.Add(new FlowEdge{From="t",To="bad"});
    var before=JsonSerializer.Serialize(flow);
    var states=await FlowPreview.EvaluateAsync(flow,0,cfg);
    Check(states.TryGetValue("t",out var s)&&s.Value&&s.Available,"未保存/校验失败的流程也能求值状态灯");
    Check(before==JsonSerializer.Serialize(flow),"状态预览不改运行或配置字段");
    var invalid=FlowValidator.Validate(flow,cfg);
    Check(invalid.HasError&&states["t"].Value,"校验失败不影响条件灯求值");
    var off=new AutomationFlow{AccountId="a",Nodes=new List<FlowNode>{new FlowNode{Id="n",Kind=FlowNodeKind.Condition,Type=FlowNodeType.CondTimeRange,Params=new(){{"from","03:00"},{"to","03:01"}}}}};
    var st2=await FlowPreview.EvaluateAsync(off,0,cfg,new DateTime(2026,11,18,12,0,0));
    Check(!st2["n"].Value,"条件不满足时灯不亮");
   }
   catch(Exception ex){Console.WriteLine("FAIL "+ex);fails++;}
   finally{app.Shutdown();}
  };
  app.Run();return fails;
 }
}
