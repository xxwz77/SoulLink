using System;
using System.Collections.Generic;
using System.Linq;
using SoulLink.Models;
using SoulLink.Services;
public static class ValidatorCheck
{
    static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
    static FlowNode C(string id)=>new(){Id=id,Kind=FlowNodeKind.Condition,Type=FlowNodeType.CondTimeRange,Params=new(){{"from","00:00"},{"to","23:59"}}};
    static FlowNode A(string id)=>new(){Id=id,Kind=FlowNodeKind.Action,Type=FlowNodeType.ActDelay,Params=new(){{"seconds","0"}}};
    static FlowNode End()=>new(){Id="end",Kind=FlowNodeKind.Terminate,Type=FlowNodeType.ActEndFlow};
    static void Link(AutomationFlow f,FlowNode a,FlowNode b)=>FlowGraphRules.Connect(f,a,b,"",out _);
    static bool Error(FlowValidator.Report r,string s)=>r.Issues.Any(i=>i.IsError&&i.Message.Contains(s));
    public static int Main()
    {
        var f=new AutomationFlow();var c1=C("1");var c2=C("2");var a=A("a");f.Nodes.AddRange(new[]{c1,c2,a});Link(f,c1,a);Link(f,c2,a);
        var r=FlowValidator.Validate(f);Check(!r.HasError,"多 LD 入口汇入合法");
        Check(r.Issues.All(i=>!i.Message.Contains("否")&&!i.Message.Contains("只有「是」")&&!i.Message.Contains("超时")),"不再存在双出口/条件超时校验");
        var orphan=new AutomationFlow();orphan.Nodes.Add(A("orphan"));Check(Error(FlowValidator.Validate(orphan),"前面没有条件"),"无上游 OUT 拒绝");
        var cycle=new AutomationFlow();var x=C("x");var y=C("y");cycle.Nodes.AddRange(new[]{x,y,A("a")});cycle.Edges.Add(new FlowEdge{From="x",To="y"});cycle.Edges.Add(new FlowEdge{From="y",To="x"});Check(Error(FlowValidator.Validate(cycle),"循环"),"环拒绝");
        var e1=new AutomationFlow();var ec=C("c");var end=End();e1.Nodes.AddRange(new[]{ec,end});Link(e1,ec,end);Check(!FlowValidator.Validate(e1).HasError,"单 END 合法");
        e1.Nodes.Add(new FlowNode{Id="end2",Kind=FlowNodeKind.Terminate,Type=FlowNodeType.ActEndFlow});e1.Edges.Add(new FlowEdge{From="c",To="end2"});Check(Error(FlowValidator.Validate(e1),"最多一个"),"多 END 拒绝");
        var old=new AutomationFlow{EngineVersion=1};old.Active=true;old.Latched.Add("c");var oc=C("c");var oa=A("a");var no=A("no");old.Nodes.AddRange(new[]{oc,oa,no});old.Edges.Add(new FlowEdge{From="c",To="a",Branch="true"});old.Edges.Add(new FlowEdge{From="c",To="no",Branch="false"});old.EnsurePlcModel();Check(old.MigrationNotes.Any(x=>x.Contains("反向分支"))&&old.MigrationNotes.Any(x=>x.Contains("锁存")),"旧复杂图明确生成迁移提示");
        var cfg=new AppConfig();cfg.Actions.Add(new ActionItem{Id="action-1"});
        var edit=new AutomationFlow();var editCondition=C("edit-condition");var action=new FlowNode{Id="a",Kind=FlowNodeKind.Action,Type=FlowNodeType.ActRunAction,Params=new(){{"actionId",""}}};edit.Nodes.AddRange(new[]{editCondition,action});Link(edit,editCondition,action);
        Check(Error(FlowValidator.Validate(edit,cfg),"选择动作"),"未选动作时明确提示");
        action.Set("actionId","action-1");
        Check(!Error(FlowValidator.Validate(edit,cfg),"选择动作"),"选择动作后重新校验，不再提示未填写");
        var repeat=FlowValidator.Validate(f);
        Check(repeat.Issues.Any(i=>!i.IsError&&i.Message.StartsWith("未设置「当天完成」")),"无 END 提示循环执行");
        Check(repeat.Issues.All(i=>!i.Message.Contains("出口未连接")),"末尾动作不再误报出口未连接");
        var withEnd=new AutomationFlow();var wec=C("c");var wea=A("a");var we=End();withEnd.Nodes.AddRange(new[]{wec,wea,we});Link(withEnd,wec,wea);Link(withEnd,wea,we);
        Check(FlowValidator.Validate(withEnd).Issues.All(i=>!i.Message.Contains("循环触发")),"有当天完成时不提示循环触发");
        var duplicate=new AutomationFlow();duplicate.Nodes.AddRange(new[]{C("c"),A("a")});duplicate.Edges.Add(new FlowEdge{From="c",To="a"});duplicate.Edges.Add(new FlowEdge{From="c",To="a"});Check(Error(FlowValidator.Validate(duplicate),"重复连线"),"导入的重复连线拒绝运行");
        var json=System.Text.Json.JsonSerializer.Serialize(old);var copy=System.Text.Json.JsonSerializer.Deserialize<AutomationFlow>(json)!;Check(copy.DailyLatchKey==old.DailyLatchKey&&copy.EngineVersion==2&&copy.Nodes.Count==old.Nodes.Count,"PLC v2 配置 JSON 往返保留");
        Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
    }
}