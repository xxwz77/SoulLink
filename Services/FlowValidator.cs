using System;
using System.Collections.Generic;
using System.Linq;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>PLC v2 结构校验：多 LD 入口合法；实时条件与本轮瞬时信号分开处理。</summary>
    public static class FlowValidator
    {
        public sealed class Issue { public string Message = ""; public bool IsError; public FlowNode? Node; public string ParamKey = ""; public string Target = ""; }
        public sealed class Report
        {
            public List<Issue> Issues = new();
            public bool HasError => Issues.Exists(i => i.IsError);
            public bool Ok => Issues.Count == 0;
            public string ToText() => string.Join("\n", Issues.Select(i => (i.IsError ? "✕ " : "⚠ ") + i.Message));
        }
        public static Report Validate(AutomationFlow flow, AppConfig? cfg = null)
        {
            var r = new Report(); if (flow == null) { r.Issues.Add(new Issue { IsError=true, Message="流程为空" }); return r; }
            flow.EnsurePlcModel();
            void Error(FlowNode? n,string m) => r.Issues.Add(new Issue { Node=n,IsError=true,Message=m });
            void Field(FlowNode n,string key,string m) => r.Issues.Add(new Issue { Node=n,IsError=true,Message=m,ParamKey=key,Target="field" });
            var ids = flow.Nodes.Select(n=>n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (flow.Edges.GroupBy(e => e.From + "|" + e.To, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                Error(null,"存在重复连线，请删除多余连线");
            if (ids.Count != flow.Nodes.Count) Error(null,"节点 Id 重复");
            if (flow.Nodes.Count == 0) Error(null,"没有条件：请先添加一个判断模块");
            foreach (var e in flow.Edges)
            {
                if (!ids.Contains(e.From) || !ids.Contains(e.To)) Error(null,"存在指向已删除模块的连线");
                if (e.Branch.Length > 0) Error(flow.FindNode(e.From),"旧是/否分支尚未迁移");
                if (flow.FindNode(e.From)?.Kind == FlowNodeKind.Terminate) Error(flow.FindNode(e.From),"「当天完成」是最后一步，不能有出口");
            }
            foreach (var n in flow.Nodes)
            {
                if (!n.IsContact && flow.SourcesOf(n.Id).Count == 0) Error(n,"「"+n.Title+"」前面没有条件，不会自行执行");
                if (n.IsContact && n.Logic == FlowLogicOp.LD && flow.SourcesOf(n.Id).Count > 0) Error(n,"起始条件不能再连接上游");
                // 只有条件需要继续连接；动作放在末尾是正常结构。
                if (n.IsContact && flow.TargetsOf(n.Id).Count == 0)
                    r.Issues.Add(new Issue { Node=n,Message="条件「"+n.Title+"」后面还没有连接动作" });
                var def = FlowNodeCatalog.Get(n.Type);
                if (def == null) { Error(n,"未知模块："+n.Type); continue; }
                foreach (var p in def.Params)
                    if (p.Required && p.Default.Length == 0 && string.IsNullOrWhiteSpace(n.Get(p.Key))) Field(n,p.Key,"「"+n.Title+"」的「"+p.Label+"」没有填写");
                // ★ 只校验"账号存在"，**不**按 AccountScope 排除主用户。
                //   原来这处会让引用主用户的流程直接校验失败 → 整个流程拒绝运行。
                //   用户原话：「不能停啊，它是主控」—— 主控自己的自动化必须能跑。
                //   （"不把主用户当投送目标"由 SystemLauncher / RequestHandler 负责。）
                if (n.Get("user").Length > 0 && cfg != null
                    && !cfg.Accounts.Any(a => string.Equals(a.UserName, SessionService.BareName(n.Get("user")), StringComparison.OrdinalIgnoreCase)))
                    Field(n,"user","「"+n.Title+"」的目标账号不存在；不会回退操作其它账号");
                if (n.Type == FlowNodeType.TrigSchedule && !TimeSpan.TryParse(n.Get("time"),out _)) Error(n,"时刻必须是 HH:mm");
                if (n.Type == FlowNodeType.CondTimeRange && (!TimeSpan.TryParse(n.Get("from"),out _) || !TimeSpan.TryParse(n.Get("to"),out _))) Error(n,"时间段必须是 HH:mm");
                foreach (var p in def.Params.Where(p=>p.Kind==FlowParamKind.Number && n.Get(p.Key).Length>0))
                    if (!double.TryParse(n.Get(p.Key),out var v) || v<0) Error(n,"「"+n.Title+"」的「"+p.Label+"」必须是非负数字");
                if (n.Type == FlowNodeType.ActDelay && n.GetInt("seconds",0)>3600) Error(n,"等待最长 3600 秒");
                if (n.GetBool("isRegex",false) && n.Get("pattern").Length>0)
                    try { _=new System.Text.RegularExpressions.Regex(n.Get("pattern")); } catch(Exception ex) { Error(n,"正则无效："+ex.Message); }
                if (UsesBgi(n.Type) && cfg != null)
                {
                    string user = n.Get("user");
                    if (user.Length == 0) user=cfg.Accounts.FirstOrDefault(a=>a.Id==flow.AccountId)?.UserName ?? "";
                    string path=BgiPathResolver.GetPath(user);
                    if (path.Length == 0) r.Issues.Add(new Issue{Node=n,IsError=true,Message="「"+n.Title+"」找不到目标账号 BGI 路径",Target="bgi"});
                    else if (!System.IO.File.Exists(path)) r.Issues.Add(new Issue{Node=n,IsError=true,Message="「"+n.Title+"」BGI 路径不存在："+path,Target="bgi"});
                }
                if (n.Type == FlowNodeType.ActRunAction && cfg != null && !cfg.Actions.Any(a=>a.Id==n.Get("actionId"))) Field(n,"actionId","快捷动作不存在");
            }
            if (HasCycle(flow)) Error(null,"连线形成循环，请改成从上到下依次执行");
            var reachable=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var ep in flow.EntryPoints){reachable.Add(ep.Id);Reach(flow,ep.Id,reachable);}
            foreach(var n in flow.Nodes)if(!reachable.Contains(n.Id))Error(n,"「"+n.Title+"」没有连接到起始条件");
            if (!flow.Nodes.Any(n=>n.Kind==FlowNodeKind.Action||n.Kind==FlowNodeKind.Terminate))Error(null,"还没有动作或「当天完成」模块");
            var ends=flow.Nodes.Where(n=>n.Kind==FlowNodeKind.Terminate).ToList();
            if (ends.Count == 0 && flow.Nodes.Any(n => n.Kind == FlowNodeKind.Action))
                r.Issues.Add(new Issue { Message = "未设置「当天完成」：流程会循环执行，完成后重新获取条件" });
            if(ends.Count>1)Error(null,"每个流程最多一个「当天完成」，避免部分动作尚未执行就停止");
            if(ends.Count==1)
                foreach(var n in flow.Nodes.Where(n=>n.Kind==FlowNodeKind.Action))
                    if(n.Id!=ends[0].Id&&!FlowGraphRules.CanReach(flow,n.Id,ends[0].Id))
                        r.Issues.Add(new Issue{Node=n,Message="动作「"+n.Title+"」不在「当天完成」前面；到达完成后，这个动作今天不会再执行"});
            return r;
        }
        private static bool UsesBgi(FlowNodeType t)=>t==FlowNodeType.ActStartBgi||t==FlowNodeType.ActStartOneDragon||t==FlowNodeType.ActStartGroups||t==FlowNodeType.ActStopBgi;
        private static void Reach(AutomationFlow f,string id,HashSet<string> seen){foreach(var t in f.TargetsOf(id))if(seen.Add(t))Reach(f,t,seen);}
        private static bool HasCycle(AutomationFlow f)
        {
            var state=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
            bool Dfs(string id){if(state.TryGetValue(id,out var s))return s==1;state[id]=1;foreach(var t in f.TargetsOf(id))if(Dfs(t))return true;state[id]=2;return false;}
            return f.Nodes.Any(n=>!state.ContainsKey(n.Id)&&Dfs(n.Id));
        }
    }
}