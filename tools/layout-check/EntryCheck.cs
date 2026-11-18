using System;
using System.Collections.Generic;
using System.Linq;

// 复现 AutomationFlow.Trigger 的选取逻辑
public static class EntryCheck
{
    class Node { public string Id=""; public string Kind=""; public string Title=""; }
    class Edge { public string From=""; public string To=""; }

    class Flow
    {
        public List<Node> Nodes = new();
        public List<Edge> Edges = new();
        public Node? Trigger
        {
            get
            {
                foreach (var n in Nodes) if (n.Kind == "Trigger") return n;
                var up = new HashSet<string>(Edges.Select(e => e.To));
                foreach (var n in Nodes)
                {
                    if (up.Contains(n.Id)) continue;
                    if (!Edges.Any(e => e.From == n.Id)) continue;
                    return n;
                }
                return null;
            }
        }
    }

    public static int Main()
    {
        int fails = 0;
        void Check(bool ok, string what) { Console.WriteLine((ok?"ok   ":"FAIL ")+what); if(!ok) fails++; }

        // ① 老流程：显式触发器优先
        var f1 = new Flow();
        f1.Nodes.Add(new Node{Id="t", Kind="Trigger", Title="定时"});
        f1.Nodes.Add(new Node{Id="c", Kind="Condition", Title="条件"});
        f1.Edges.Add(new Edge{From="t", To="c"});
        Check(f1.Trigger?.Id == "t", "老流程仍用显式触发器");

        // ② 新流程：第一个模块是条件，无上游
        var f2 = new Flow();
        f2.Nodes.Add(new Node{Id="c1", Kind="Condition", Title="狗粮收尾B"});
        f2.Nodes.Add(new Node{Id="a1", Kind="Action", Title="启动一条龙"});
        f2.Edges.Add(new Edge{From="c1", To="a1"});
        Check(f2.Trigger?.Id == "c1", "自动把第一个条件当作入口");

        // ③ 孤立节点不算入口
        var f3 = new Flow();
        f3.Nodes.Add(new Node{Id="x", Kind="Condition", Title="孤立"});
        Check(f3.Trigger == null, "孤立节点不当入口");

        // ④ 串联时取最前面那个
        var f4 = new Flow();
        f4.Nodes.Add(new Node{Id="a", Kind="Condition"});
        f4.Nodes.Add(new Node{Id="b", Kind="Condition"});
        f4.Nodes.Add(new Node{Id="c", Kind="Action"});
        f4.Edges.Add(new Edge{From="a", To="b"});
        f4.Edges.Add(new Edge{From="b", To="c"});
        Check(f4.Trigger?.Id == "a", "串联取最前节点");

        Console.WriteLine(fails == 0 ? "ALL PASSED" : "FAILURES=" + fails);
        return fails;
    }
}
