using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>只读条件预览。禁止写流程状态、持久化、调度游标或执行任何动作。</summary>
    public static class FlowPreview
    {
        public static async Task<Dictionary<string, PlcNodeState>> EvaluateAsync(
            AutomationFlow source, int sessionId, AppConfig cfg, DateTime? now = null)
        {
            var time = now ?? DateTime.Now;
            var flow = new AutomationFlow
            {
                Id=source.Id, AccountId=source.AccountId, Enabled=source.Enabled, Paused=source.Paused,
                DayBoundaryHour=source.DayBoundaryHour, EngineVersion=2, LifecycleVersion=1,
                DailyLatchKey=source.DailyLatchKey, RunComplete=source.RunComplete, RunDayKey=source.RunDayKey,
                Nodes=source.Nodes.Select(n => new FlowNode
                {
                    Id=n.Id, Kind=n.Kind, Type=n.Type, X=n.X, Y=n.Y, Logic=n.Logic, Negated=n.Negated,
                    BranchMode=n.BranchMode, DayBoundaryHour=n.DayBoundaryHour, Params=new Dictionary<string,string>(n.Params ?? new())
                }).ToList(),
                Edges=source.Edges.Select(e => new FlowEdge{From=e.From,To=e.To,Branch=e.Branch}).ToList()
            };
            var values = new Dictionary<string, PlcSignal>(StringComparer.OrdinalIgnoreCase);
            foreach(var n in flow.Nodes.Where(n => n.IsContact))
            {
                PlcSignal signal;
                try { signal = await FlowRunner.ReadPreviewContactAsync(n, flow, sessionId, cfg, time).ConfigureAwait(false); }
                catch { signal = new PlcSignal(false, false); }
                values[n.Id] = new PlcSignal(signal.Available && (n.Negated ? !signal.Value : signal.Value), signal.Available);
            }
            var states = new Dictionary<string, PlcNodeState>(StringComparer.OrdinalIgnoreCase);
            foreach(var n in PlcLogic.Topological(flow))
            {
                var sources = flow.SourcesOf(n.Id).Where(states.ContainsKey).Select(id => states[id]).ToList();
                if (n.IsContact)
                {
                    var signal = values[n.Id];
                    var state = PlcLogic.Evaluate(n, sources, signal.Value);
                    state.Available = signal.Available;
                    state.Value = signal.Value;
                    states[n.Id] = state;
                    continue;
                }
                // 动作/END 只点亮导线，不代表已执行，也不写任何运行状态。
                bool powered = sources.Count > 0 && sources.Any(s => s.Output);
                states[n.Id] = new PlcNodeState { Value = powered, Input = powered, Output = powered, Available = true };
            }
            return states;
        }
    }
}
