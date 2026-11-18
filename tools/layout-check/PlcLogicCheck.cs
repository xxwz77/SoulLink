using System;
using System.Collections.Generic;
using SoulLink.Models;

public static class PlcLogicCheck
{
    static int fails;
    static void Check(bool ok, string text) { Console.WriteLine((ok?"ok   ":"FAIL ")+text); if(!ok)fails++; }
    static FlowNode C(string id, FlowLogicOp op=FlowLogicOp.AND, bool not=false)
        => new(){Id=id,Kind=FlowNodeKind.Condition,Type=FlowNodeType.CondTimeRange,Logic=op,Negated=not};
    static FlowNode A(string id) => new(){Id=id,Kind=FlowNodeKind.Action,Type=FlowNodeType.ActDelay};
    static void E(AutomationFlow f, string a,string b)=>FlowGraphRules.Connect(f,f.FindNode(a),f.FindNode(b),"",out _);
    public static int Main()
    {
        var f=new AutomationFlow(); var a=C("a"); var b=C("b"); var o=A("o");
        f.Nodes.AddRange(new[]{a,b,o}); E(f,"a","b"); E(f,"b","o"); f.EnsurePlcModel();
        var s=PlcLogic.Scan(f,n=>true);
        Check(s["a"].Output&&s["b"].Output&&s["o"].Input,"LD + AND + OUT 全部导通");
        s=PlcLogic.Scan(f,n=>n.Id!="a"); Check(!s["b"].Output&&s["b"].Value,"上游断：AND 输出断，但本条件成立仍亮");
        a.Negated=true; s=PlcLogic.Scan(f,n=>false); Check(s["a"].Value,"LD NOT：原条件不成立时导通");
        a.Negated=false;

        var parallel=new AutomationFlow(); var p=C("a");var q=C("b");var po=A("o");
        parallel.Nodes.AddRange(new[]{p,q,po}); E(parallel,"a","o"); E(parallel,"b","o");
        s=PlcLogic.Scan(parallel,n=>n.Id=="a"); Check(s["o"].Input,"两条独立 LD 汇入 OUT = OR");
        s=PlcLogic.Scan(parallel,n=>false); Check(!s["o"].Input,"全部条件不满足即灭");

        var or=new AutomationFlow();var x=C("a");var y=C("b",FlowLogicOp.OR);var oo=A("o");
        or.Nodes.AddRange(new[]{x,y,oo});E(or,"a","b");E(or,"b","o");or.EnsurePlcModel();
        s=PlcLogic.Scan(or,n=>n.Id=="b");Check(s["b"].Output,"OR：上游断，本触点成立也导通");
        s=PlcLogic.Scan(or,n=>n.Id=="a");Check(s["b"].Output,"OR：本触点断，上游通也导通");

        var fan=new AutomationFlow();var r=C("r");var f1=A("1");var f2=A("2");
        fan.Nodes.AddRange(new[]{r,f1,f2});Check(FlowGraphRules.Connect(fan,r,f1,"",out _)==EdgeConnectResult.Added,"出口允许第一路扇出");
        Check(FlowGraphRules.Connect(fan,r,f2,"",out _)==EdgeConnectResult.Added,"单出口允许第二路扇出，不替换");
        Check(FlowGraphRules.Connect(fan,r,f2,"",out _)==EdgeConnectResult.Duplicate,"仅拒绝完全重复线");

        var old=new AutomationFlow{EngineVersion=1};var oldc=C("c");oldc.Logic=FlowLogicOp.LD;
        var yes=A("yes");var no=A("no");old.Nodes.AddRange(new[]{oldc,yes,no});
        old.Edges.Add(new FlowEdge{From="c",To="yes",Branch="true"});
        old.Edges.Add(new FlowEdge{From="c",To="no",Branch="false"});old.EnsurePlcModel();
        Check(old.Nodes.Count==4&&old.Edges.All(e=>e.Branch==""),"旧是/否迁移为两个单出口触点");
        Check(old.Nodes.Count(n=>n.Negated)==1,"旧否分支保留为 NOT，不静默反转");
        Check(old.Nodes.Where(n=>n.IsContact).All(n=>n.Logic==FlowLogicOp.LD),"迁移的两个触点均从母线 LD 开始");
        var count=old.Nodes.Count;old.EnsurePlcModel();Check(old.Nodes.Count==count,"迁移幂等");

        var loop=new AutomationFlow();Check(!PlcLogic.IsDailyComplete(loop,new DateTime(2026,11,18)),"无 END 永不锁存");
        var endFlow=new AutomationFlow{Nodes=new List<FlowNode>{new FlowNode{Id="e",Kind=FlowNodeKind.Terminate,DayBoundaryHour=4}}};
        Check(!PlcLogic.IsDailyComplete(endFlow,new DateTime(2026,11,18,3,59,0)),"END 初始未锁存");
        PlcLogic.CompleteToday(endFlow,new DateTime(2026,11,18,3,59,0));Check(PlcLogic.IsDailyComplete(endFlow,new DateTime(2026,11,18,3,59,0)),"04:00 END 属于前一业务日");
        Check(!PlcLogic.IsDailyComplete(endFlow,new DateTime(2026,11,18,4,0,0)),"END 日界到达自动复位");

        var values=new Dictionary<string,bool>{{"x",true}};s=PlcLogic.Scan(f,n=>true);
        s=PlcLogic.Scan(f,n=>false);Check(!s["b"].Output,"每扫描重算，不把上次满足锁存");
        Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
    }
}