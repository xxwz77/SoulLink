using System;
using System.Linq;
using SoulLink.Models;
public static class BranchCheck
{
    static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
    static FlowNode N(string id,FlowNodeKind kind=FlowNodeKind.Condition)=>new(){Id=id,Kind=kind,Type=kind==FlowNodeKind.Action?FlowNodeType.ActRunAction:FlowNodeType.CondTimeRange};
    public static int Main()
    {
        foreach(var t in Enum.GetValues<FlowNodeType>())Check(FlowNodeCatalog.Get(t)!=null,"目录覆盖 "+t);
        var numeric=Enum.GetValues<FlowNodeType>().Select(t=>(int)t).ToList();
        Check(numeric.Distinct().Count()==numeric.Count,"所有枚举数值唯一");
        Check(FlowNodeCatalog.All().Count==numeric.Count,"目录条目数覆盖所有枚举值");
        var f=new AutomationFlow();var c=N("c");var a=N("a",FlowNodeKind.Action);var b=N("b",FlowNodeKind.Action);f.Nodes.AddRange(new[]{c,a,b});
        Check(FlowGraphRules.Connect(f,c,a,"true",out _)==EdgeConnectResult.Added&&f.Edges[0].Branch=="","所有新线忽略旧分支参数");
        Check(FlowGraphRules.Connect(f,c,b,"false",out _)==EdgeConnectResult.Added&&f.Edges.Count==2,"单出口可扇出，不再替换分支");
        Check(FlowGraphRules.Connect(f,c,b,"",out _)==EdgeConnectResult.Duplicate,"重复线拒绝");
        Check(FlowGraphRules.Connect(f,a,c,"",out _)==EdgeConnectResult.Cycle,"环拒绝");
        var end=N("end",FlowNodeKind.Terminate);end.Type=FlowNodeType.ActEndFlow;f.Nodes.Add(end);
        Check(FlowGraphRules.Connect(f,end,N("orphan",FlowNodeKind.Action), "",out _)==EdgeConnectResult.Invalid,"END 无输出");
        Check(FlowNodeCatalog.ByKind(FlowNodeKind.Condition).All(d=>d.Kind==FlowNodeKind.Condition),"目录没有 Trigger 分类");
        Check(FlowNodeCatalog.ByKind(FlowNodeKind.Condition).All(d=>d.Params.All(p=>p.Key!="edge"&&p.Key!="pollSeconds")),"无 edge/pollSeconds 死参数");
        Check(FlowNodeCatalog.Get(FlowNodeType.TrigScriptGroup)==FlowNodeCatalog.Get(FlowNodeType.CondScriptGroupState),"旧配置组触发器复用同一触点");
        Check(FlowNodeCatalog.Get(FlowNodeType.ActStartGroups)!.Params.Any(p=>p.Key=="groups"&&p.Kind==FlowParamKind.ScriptGroupPicker),"执行配置组使用目录下拉选择器");
        Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
    }
}