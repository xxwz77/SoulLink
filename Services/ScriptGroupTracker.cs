using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

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

        private static readonly Regex RxStart = new Regex(
            @"配置组\s*""(?<g>[^""]+)""\s*加载完成[，,]\s*共\s*(?<n>\d+)\s*个脚本[，,]\s*开始执行",
            RegexOptions.Compiled);

        private static readonly Regex RxFinish = new Regex(
            @"配置组\s*""(?<g>[^""]+)""\s*执行结束", RegexOptions.Compiled);

        private static readonly Regex RxFail = new Regex(
            @"配置组\s*""(?<g>[^""]+)""\s*未成功完成[，,]?\s*结果\s*[:：]\s*(?<r>\S*)",
            RegexOptions.Compiled);

        private static readonly Regex RxCancel = new Regex(
            @"取消执行配置组\s*[:：]\s*""(?<g>[^""]+)""", RegexOptions.Compiled);

        private readonly object _sync = new object();

        /// <summary>清空（跨天/换文件时调用）。</summary>
        public void Reset()
        {
            lock (_sync) _map.Clear();
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
            if (string.IsNullOrEmpty(logText)) return;
            try
            {
                lock (_sync)
                {
                    // 各事件按在文本中出现的位置排序处理，保证先后关系正确
                    var events = new List<(int pos, Action apply)>();

                    foreach (Match m in RxStart.Matches(logText))
                    {
                        string g = m.Groups["g"].Value.Trim();
                        int n = int.TryParse(m.Groups["n"].Value, out int c) ? c : 0;
                        if (!ValidGroupName(g)) continue;
                        events.Add((m.Index, () =>
                        {
                            var r = Ensure(g);
                            foreach (var other in _map.Values)
                                if (other.State == ScriptGroupState.Running && other.Name != g)
                                { other.State = ScriptGroupState.Interrupted; other.Detail = "后续配置组已开始，未记录正常结束"; }
                            r.State = ScriptGroupState.Running;
                            r.StartedAt = EventTime(logText, m.Index); r.TimeIsExact = HasEventTime(logText, m.Index);
                            r.EndedAt = DateTime.MinValue;
                            r.ScriptCount = n;
                            r.Detail = "";
                        }));
                    }

                    foreach (Match m in RxFinish.Matches(logText))
                    {
                        string g = m.Groups["g"].Value.Trim();
                        if (!ValidGroupName(g)) continue;
                        events.Add((m.Index, () =>
                        {
                            var r = Ensure(g);
                            r.State = ScriptGroupState.Finished;
                            r.EndedAt = EventTime(logText, m.Index); r.TimeIsExact = HasEventTime(logText, m.Index);
                        }));
                    }

                    foreach (Match m in RxFail.Matches(logText))
                    {
                        string g = m.Groups["g"].Value.Trim();
                        string res = m.Groups["r"].Value.Trim();
                        if (!ValidGroupName(g)) continue;
                        events.Add((m.Index, () =>
                        {
                            var r = Ensure(g);
                            r.State = ScriptGroupState.Failed;
                            r.EndedAt = EventTime(logText, m.Index); r.TimeIsExact = HasEventTime(logText, m.Index);
                            r.Detail = string.IsNullOrEmpty(res) ? "Failed" : res;
                        }));
                    }

                    foreach (Match m in RxCancel.Matches(logText))
                    {
                        string g = m.Groups["g"].Value.Trim();
                        if (!ValidGroupName(g)) continue;
                        events.Add((m.Index, () =>
                        {
                            var r = Ensure(g);
                            r.State = ScriptGroupState.Cancelled;
                            r.EndedAt = EventTime(logText, m.Index); r.TimeIsExact = HasEventTime(logText, m.Index);
                        }));
                    }

                    events.Sort((a, b) => a.pos.CompareTo(b.pos));
                    foreach (var e in events) e.apply();
                }
            }
            catch { }
        }

        internal static bool ValidGroupName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return !name.Equals("The operation was canceled.",StringComparison.OrdinalIgnoreCase)
                && !name.Equals("OperationCanceledException",StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Exception",StringComparison.OrdinalIgnoreCase);
        }

        internal static DateTime EventTime(string text, int position)
        {
            int start = text.LastIndexOf('\n', Math.Max(0, position - 1));
            start = start < 0 ? 0 : start + 1;
            string prefix = text.Substring(start, Math.Min(90, Math.Max(0, position - start)));
            var m = Regex.Match(prefix, @"\[(?<d>\d{4}-\d{2}-\d{2}[ T])?(?<t>\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)\]");
            if (!m.Success) return DateTime.MinValue;
            string stamp = (m.Groups["d"].Success ? m.Groups["d"].Value.Trim() + " " : DateTime.Now.ToString("yyyy-MM-dd") + " ") + m.Groups["t"].Value;
            return DateTime.TryParse(stamp, out var at) ? at : DateTime.MinValue;
        }
        private static bool HasEventTime(string text, int position) => EventTime(text, position) != DateTime.MinValue;

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
