using System;
using System.Collections.Generic;
using System.Text;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>
    /// 流程结构校验：保存前检查"链路完整且有数据、结构合法"。
    ///
    /// 校验项（用户要求）：
    ///   1. 必须有一个触发器；
    ///   2. 触发器必须连出去（否则永远不会执行）；
    ///   3. 每个条件/执行器必须至少有一条入边（孤立模块没用）；
    ///   4. 参数必填项不能为空（"有数据"）；
    ///   5. 不能成环；
    ///   6. 执行器必须能从触发器到达（否则是死代码）。
    /// </summary>
    public static class FlowValidator
    {
        public sealed class Issue
        {
            public string Message = "";
            /// <summary>true = 错误（不能运行）；false = 提醒（能运行但不推荐）</summary>
            public bool IsError;
            public FlowNode? Node;
        }

        public sealed class Report
        {
            public List<Issue> Issues = new List<Issue>();
            public bool HasError => Issues.Exists(i => i.IsError);
            public bool Ok => Issues.Count == 0;

            public string ToText()
            {
                var sb = new StringBuilder();
                foreach (var i in Issues)
                    sb.AppendLine((i.IsError ? "✕ " : "⚠ ") + i.Message);
                return sb.ToString().TrimEnd();
            }
        }

        public static Report Validate(AutomationFlow flow, AppConfig? cfg = null)
        {
            var r = new Report();
            if (flow == null)
            {
                r.Issues.Add(new Issue { Message = "流程为空", IsError = true });
                return r;
            }

            // ① 触发器
            var trigger = flow.Trigger;
            if (trigger == null)
            {
                r.Issues.Add(new Issue { Message = "没有触发器：流程永远不会开始", IsError = true });
                return r;
            }

            if (flow.Nodes.Count == 1)
            {
                r.Issues.Add(new Issue { Message = "只有触发器，还没有任何条件和执行器" });
                return r;
            }

            // ② 触发器必须连出去
            if (flow.TargetsOf(trigger.Id).Count == 0)
            {
                r.Issues.Add(new Issue
                {
                    Message = "触发器没有连到任何模块：从它右侧圆点拖一条线出来",
                    IsError = true,
                    Node = trigger
                });
            }

            // ③ 孤立模块 / 入边检查
            foreach (var n in flow.Nodes)
            {
                if (n.Id == trigger.Id) continue;

                var srcs = flow.SourcesOf(n.Id);
                if (srcs.Count == 0)
                {
                    r.Issues.Add(new Issue
                    {
                        Message = "「" + n.Title + "」没有连接到上游：它永远不会被执行",
                        IsError = true,
                        Node = n
                    });
                }
            }

            // ④ 参数必填校验
            foreach (var n in flow.Nodes)
            {
                var def = FlowNodeCatalog.Get(n.Type);
                if (def == null) continue;

                foreach (var p in def.Params)
                {
                    // "目标用户"永远可选（留空=本账号）
                    if (p.Key == "user") continue;
                    // 明确标了可选的跳过
                    if (!p.Required) continue;
                    // 有默认值的不用填
                    if (!string.IsNullOrWhiteSpace(p.Default)) continue;

                    string v = n.Get(p.Key);
                    if (string.IsNullOrWhiteSpace(v))
                    {
                        r.Issues.Add(new Issue
                        {
                            Message = "「" + n.Title + "」的「" + p.Label + "」没有填写",
                            IsError = true,
                            Node = n
                        });
                    }
                }
            }

            // ⑤ 成环检测
            if (HasCycle(flow, trigger.Id))
            {
                r.Issues.Add(new Issue { Message = "存在环形连线：流程会无限循环", IsError = true });
            }

            // ⑥ 可达性：所有执行器都应能从触发器走到
            var reachable = ReachableFrom(flow, trigger.Id);
            foreach (var n in flow.Nodes)
            {
                if (n.Id == trigger.Id) continue;
                if (reachable.Contains(n.Id)) continue;

                r.Issues.Add(new Issue
                {
                    Message = "「" + n.Title + "」从触发器走不到：检查连线",
                    IsError = true,
                    Node = n
                });
            }

            // ⑦ BGI 路径检查：用到 BGI 的节点必须能解析到真实文件
            foreach (var n in flow.Nodes)
            {
                if (!UsesBgi(n.Type)) continue;

                string user = n.Get("user").Trim();
                // ★ 统一走解析器：手动指定 > 正在运行 > 记忆
                string path = string.IsNullOrWhiteSpace(user) ? "" : BgiPathResolver.GetPath(user);
                if (string.IsNullOrWhiteSpace(path))
                {
                    // 退回：任取一个已配置的（仅用于校验放行，不写回）
                    var map = cfg?.Settings?.BgiPaths;
                    if (map != null)
                        foreach (var kv in map)
                            if (!string.IsNullOrWhiteSpace(kv.Value)) { path = kv.Value; break; }
                }

                if (string.IsNullOrWhiteSpace(path))
                {
                    r.Issues.Add(new Issue
                    {
                        Message = "「" + n.Title + "」找不到 BGI 程序：请在右侧选择 BGI 路径",
                        IsError = true,
                        Node = n
                    });
                }
                else if (!System.IO.File.Exists(path))
                {
                    r.Issues.Add(new Issue
                    {
                        Message = "「" + n.Title + "」的 BGI 路径不存在：" + path,
                        IsError = true,
                        Node = n
                    });
                }
            }

            // ⑧ 提醒：只有条件没有执行器
            int actCount = 0;
            foreach (var n in flow.Nodes)
                if (n.Kind == FlowNodeKind.Action || n.Kind == FlowNodeKind.Terminate) actCount++;
            if (actCount == 0 && flow.Nodes.Count > 1)
            {
                r.Issues.Add(new Issue { Message = "没有执行器：条件满足后什么都不会做" });
            }

            return r;
        }

        /// <summary>这些执行器要启动/停止 BGI，必须有可用的 BGI 路径。</summary>
        private static bool UsesBgi(FlowNodeType t) => t switch
        {
            FlowNodeType.ActStartBgi => true,
            FlowNodeType.ActStartOneDragon => true,
            FlowNodeType.ActStartGroups => true,
            FlowNodeType.ActStopBgi => true,
            _ => false
        };

        private static bool HasCycle(AutomationFlow flow, string startId)
        {
            var state = new Dictionary<string, int>();   // 0=未访问 1=进行中 2=完成

            bool Dfs(string id)
            {
                if (state.TryGetValue(id, out int st))
                {
                    if (st == 1) return true;    // 回到进行中的节点 = 环
                    if (st == 2) return false;
                }
                state[id] = 1;
                foreach (var t in flow.TargetsOf(id))
                    if (Dfs(t)) return true;
                state[id] = 2;
                return false;
            }

            foreach (var n in flow.Nodes)
                if (!state.ContainsKey(n.Id) && Dfs(n.Id)) return true;

            return false;
        }

        private static HashSet<string> ReachableFrom(AutomationFlow flow, string startId)
        {
            var seen = new HashSet<string>();
            var stack = new Stack<string>();
            stack.Push(startId);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (!seen.Add(cur)) continue;
                foreach (var t in flow.TargetsOf(cur)) stack.Push(t);
            }
            return seen;
        }
    }
}
