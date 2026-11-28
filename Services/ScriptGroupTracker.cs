using System;
using System.Collections.Generic;
using System.Threading;

namespace SoulLink.Services
{
    /// <summary>配置组的运行状态。</summary>
    public enum ScriptGroupState
    {
        /// <summary>今天日志里从未出现过。</summary>
        Never = 0,
        /// <summary>正在执行中。</summary>
        Running = 1,
        /// <summary>正常执行结束。</summary>
        Finished = 2,
        /// <summary>执行失败（未成功完成 / Failed）。</summary>
        Failed = 3,
        /// <summary>被取消（取消执行配置组）。</summary>
        Cancelled = 4,
        /// <summary>只有开始，没有自己的收尾；后续其它配置组已开始，视为中断而非执行中。</summary>
        Interrupted = 5
    }

    /// <summary>一个配置组的运行记录。</summary>
    public sealed class ScriptGroupRun
    {
        /// <summary>配置组名。</summary>
        public string Name = "";
        /// <summary>当前状态。</summary>
        public ScriptGroupState State = ScriptGroupState.Never;
        /// <summary>最近一次"开始执行"的时间。</summary>
        public DateTime StartedAt = DateTime.MinValue;
        /// <summary>最近一次结束（成功/失败/取消）的时间。</summary>
        public DateTime EndedAt = DateTime.MinValue;
        /// <summary>脚本数量（加载完成时给出）。</summary>
        public int ScriptCount;
        /// <summary>失败原因原文（如有）。</summary>
        public string Detail = "";
        /// <summary>时间来自日志事件，而不是程序后来读到它的时间。</summary>
        public bool TimeIsExact;

        /// <summary>耗时；未结束返回空。</summary>
        public TimeSpan? Duration
            => EndedAt > StartedAt && StartedAt != DateTime.MinValue
               ? EndedAt - StartedAt : (TimeSpan?)null;
    }

    /// <summary>
    /// 配置组运行状态跟踪器。
    ///
    /// ★ 为什么放在数据中心：
    ///   配置组的运行情况是"当天日志里的事件序列"，单个条件判定时
    ///   不可能去扫整份日志（几十 MB）。数据中心增量解析一次，
    ///   所有条件共享同一份结果。
    ///
    /// ★ 日志锚点（实测自 20261117 日志）：
    ///   配置组 "联机锄地-混合精英" 加载完成，共3个脚本，开始执行     → Running
    ///   配置组 "联机锄地-小怪-队员" 未成功完成，结果: Failed         → Failed
    ///   配置组 "进入-灭绝提瓦特" 执行结束                            → Finished
    ///   取消执行配置组: "X"                                          → Cancelled
    ///
    /// ★ 幂等：同一段文本重复喂入不会产生错误状态（按最后一条事件为准）。
    /// </summary>
    public sealed class ScriptGroupTracker
    {
        private readonly Dictionary<string, ScriptGroupRun> _map
            = new Dictionary<string, ScriptGroupRun>(StringComparer.OrdinalIgnoreCase);

        private readonly Lock _sync = new();

        /// <summary>清空（跨天/换文件时调用）。</summary>
        public void Reset()
        {
            lock (_sync) _map.Clear();
        }

        /// <summary>
        /// 从权威数据源（SQLite run 表）重建今天的运行状态。
        ///
        /// ★ 2026-11-26 修复「数据中心改造后，自动化读不到配置组运行状态」：
        ///   冷启动/首次绑定时，持久化游标已在 EOF，Pump 不会重读历史日志 →
        ///   本 Tracker 一直是空的 → CondScriptGroupState / TrigScriptGroup 永远
        ///   读到 Never。这里用 SQLite 里已入库的今日 run 把内存状态补齐，
        ///   与「实时增量」无缝衔接（后续日志增量仍走 Update）。
        ///
        /// ★ 传入的每条 run 已带**权威终态**（Finished/Failed/Interrupted/…），
        ///   中断接替关系在 RunRecordCollector 落库时已经算好，这里**只做拷贝**，
        ///   绝不重新推导「后续组开始 → 前一组中断」——那会污染历史真值。
        ///
        /// ★ 调用方必须按 **start 升序** 传入；同名组后出现的覆盖先出现的
        ///   （与实时 Tracker「同名只留最新一次状态」语义一致）。
        /// </summary>
        public void Seed(IEnumerable<ScriptGroupRun> runsChronological)
        {
            if (runsChronological == null) return;
            lock (_sync)
            {
                foreach (var r in runsChronological)
                {
                    if (r == null || string.IsNullOrWhiteSpace(r.Name)) continue;
                    var e = Ensure(r.Name);
                    e.State = r.State;
                    e.StartedAt = r.StartedAt;
                    e.EndedAt = r.EndedAt;
                    e.ScriptCount = r.ScriptCount;
                    e.Detail = r.Detail ?? "";
                    e.TimeIsExact = r.TimeIsExact;
                }
            }
        }

        /// <summary>取某个配置组的运行记录；从未出现返回 null。</summary>
        public ScriptGroupRun? Get(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            lock (_sync)
                return _map.TryGetValue(name.Trim(), out var r) ? r : null;
        }

        /// <summary>全部记录（供界面展示）。</summary>
        public List<ScriptGroupRun> All()
        {
            lock (_sync) return new List<ScriptGroupRun>(_map.Values);
        }

        /// <summary>
        /// 用一段日志文本更新状态。
        /// ★ 调用方应只喂**新增**内容，避免重复计数；但即便重复喂也不会错——
        ///   这里只按事件更新状态，不做累加。
        /// </summary>
        public void Update(string logText)
        {
            if (!string.IsNullOrEmpty(logText)) Update(new BgiLogBatch(logText));
        }

        internal void Update(BgiLogBatch batch)
        {
            try
            {
                lock (_sync)
                {
                    foreach (var e in batch.GroupEvents)
                    {
                        if (!ValidGroupName(e.Name)) continue;
                        var at = batch.TimestampAt(e.Position);
                        var run = Ensure(e.Name);
                        run.TimeIsExact = at != DateTime.MinValue;
                        switch (e.Kind)
                        {
                            case BgiGroupEventKind.Start:
                                foreach (var other in _map.Values)
                                    if (other.State == ScriptGroupState.Running && other.Name != e.Name)
                                    {
                                        other.State = ScriptGroupState.Interrupted;
                                        other.Detail = "后续配置组已开始，未记录正常结束";
                                    }
                                run.State = ScriptGroupState.Running;
                                run.StartedAt = at;
                                run.EndedAt = DateTime.MinValue;
                                run.ScriptCount = e.ScriptCount;
                                run.Detail = "";
                                break;
                            case BgiGroupEventKind.Finish:
                                run.State = ScriptGroupState.Finished;
                                run.EndedAt = at;
                                break;
                            case BgiGroupEventKind.Fail:
                                run.State = ScriptGroupState.Failed;
                                run.EndedAt = at;
                                run.Detail = string.IsNullOrEmpty(e.Result) ? "Failed" : e.Result;
                                break;
                            case BgiGroupEventKind.Cancel:
                                run.State = ScriptGroupState.Cancelled;
                                run.EndedAt = at;
                                break;
                        }
                    }
                }
            }
            catch { }
        }

        internal static bool ValidGroupName(string name) => BgiLogParser.ValidGroupName(name);

        private ScriptGroupRun Ensure(string name)
        {
            if (!_map.TryGetValue(name, out var r))
            {
                r = new ScriptGroupRun { Name = name };
                _map[name] = r;
            }
            return r;
        }
    }
}
