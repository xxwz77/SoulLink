using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace SoulLink.Services
{
    /// <summary>某个账号"今天"的累计统计。</summary>
    public sealed class DailyStats
    {
        /// <summary>统计日期（本地日期）。</summary>
        public DateTime Day = DateTime.Today;

        // ── 狗粮路线收益 ─────────────────────────────────────────────
        // ★ 数据来自真实日志（20261114 实测）：
        //     狗粮路线获取狗粮经验: 63000       ← 主账号本轮
        //     狗粮路线获取摩拉: 19986
        //     思可获得84800经验                 ← 其他角色
        //     思获得17100摩拉
        //   一场狗粮会有多个角色各报一次，**今日获得 = 各次相加**。
        //   注意排除 "JS 通知关闭，消息被拦截: ..." 这种重复通知，
        //   否则同一个数字会被算两遍。
        /// <summary>今日狗粮获得经验（累加）。</summary>
        public long DogfoodExp;
        /// <summary>
        /// ★ 2026-11-26：**其他组**（如联机狗粮）的狗粮收益，与 DogfoodExp 分开记。
        ///   原来 DogfoodExp 只统计日志里的某些锚点，和「狗粮批发」的 records 值容易混淆。
        ///   这里单独累加，主界面可显示「records 值 + 其他组值」。
        /// </summary>
        public long DogfoodOtherExp;
        /// <summary>今日狗粮获得摩拉（累加）。</summary>
        public long DogfoodMora;
        /// <summary>★ 2026-11-26：其他组的狗粮摩拉收益（见 DogfoodOtherExp）。</summary>
        public long DogfoodOtherMora;

        /// <summary>今日获得经验（兼容旧字段名，供界面使用）。</summary>
        public long ExpGained => DogfoodExp;
        /// <summary>今日获得摩拉。</summary>
        public long MoraGained => DogfoodMora;
        /// <summary>今日拾取物品统计（物品名 → 次数）。</summary>
        public Dictionary<string, int> Picks
            = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        /// <summary>今日跑过的狗粮路线数。</summary>
        public int DogfoodRoutes;
        /// <summary>最近一条狗粮路线名。</summary>
        public string LastDogfoodRoute = "";
        /// <summary>今日跑过的配置组（去重）。</summary>
        public HashSet<string> Groups
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool HasAnything => ExpGained > 0 || MoraGained > 0 || Picks.Count > 0
                                   || DogfoodRoutes > 0 || Groups.Count > 0;

        /// <summary>拾取总数（所有物品次数之和）。</summary>
        public int PickTotal
        {
            get { int n = 0; foreach (var kv in Picks) n += kv.Value; return n; }
        }
    }

    /// <summary>
    /// 今日统计缓存：按账号保存当天的经验、摩拉、拾取、狗粮路线。
    ///
    /// ★ 为什么单独一层：
    ///   BGI 日志按天滚动，一旦跨天或文件被清理，昨天的数据就没了。
    ///   而"今日累计"必须能稳定取出（界面多次刷新、切页、重连都不能变）。
    ///   所以这里做一份内存缓存，随日志增长增量更新，跨天自动清空。
    ///
    /// ★ 数值语义（实测确认，不是猜测）：
    ///   · 「已储存经验识别成功: 660」     → 当日**累计值**，后一条比前一条大
    ///   · 「成功识别到摩拉数值: 61555438」→ 当日**结余值**，同样递增
    ///   所以正确做法是**取当天最后一条**，而不是把多条相加。
    /// </summary>
    public static partial class DailyStatsCache
    {
        private static readonly Lock _sync = new();
        private static readonly Dictionary<string, DailyStats> _map
            = new Dictionary<string, DailyStats>(StringComparer.OrdinalIgnoreCase);

        // ── 日志锚点（20261114 实测；格式由狗粮配置组的 JS 脚本打印）──
        //
        //   狗粮路线获取狗粮经验: 63000      主账号本轮
        //   狗粮路线获取摩拉: 19986
        //   思可获得84800经验                其他角色
        //   思获得17100摩拉
        //
        // ★ 必须排除 "JS 通知关闭，消息被拦截: ..." —— 那是同一条消息的重复通知，
        //   不排除会把每个数字算两次。
        [GeneratedRegex(@"完成\s*[:：]\s*校验路径组\s*[:：]\s*(?<name>[^，,]+)", RegexOptions.Compiled)]
        private static partial Regex RxRouteDone();
        [GeneratedRegex(@"配置组\s*""(?<g>[^""]+)""\s*加载完成", RegexOptions.Compiled)]
        private static partial Regex RxGroupLoad();
        [GeneratedRegex(@"狗粮", RegexOptions.Compiled)]
        private static partial Regex RxDogfoodGroup();

        public static DailyStats Calculate(string logText, DateTime businessDay)
        {
            var result = new DailyStats { Day = businessDay.Date };
            Apply(result, new BgiLogBatch(logText), true);
            return result;
        }
        public static DailyStats Get(string userName, DateTime? now = null, int boundaryHour = 0)
        {
            var day = SoulLink.Models.PlcLogic.BusinessDay(now ?? DateTime.Now, boundaryHour);
            if (string.IsNullOrWhiteSpace(userName)) return new DailyStats { Day = day };
            lock (_sync)
            {
                if (_map.TryGetValue(userName, out var s) && s.Day == day) return s;
                return new DailyStats { Day = day };
            }
        }

        /// <summary>
        /// 用一段日志文本增量更新某人今日统计。
        /// ★ 可重复调用同一段文本（幂等）：经验/摩拉取最大值，拾取按次累加需要外部只喂新内容。
        /// </summary>
        public static void Update(string userName, string logText, bool countPicks = true, DateTime? now = null, int boundaryHour = 0)
        {
            var day = SoulLink.Models.PlcLogic.BusinessDay(now ?? DateTime.Now, boundaryHour);
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(logText)) return;
            try
            {
                lock (_sync)
                {
                    if (!_map.TryGetValue(userName, out var st) || st.Day != day)
                    {
                        st = new DailyStats { Day = day };
                        _map[userName] = st;
                    }
                    Apply(st, new BgiLogBatch(logText), countPicks);
                }
            }
            catch { }
        }
        internal static void Accumulate(DailyStats st, BgiLogBatch batch) => Apply(st, batch, true);

        private static void Apply(DailyStats st, BgiLogBatch batch, bool countPicks)
        {
            foreach (Match m in batch.ExpMatches) st.DogfoodExp += BgiLogParser.RewardValue(m);
            foreach (Match m in batch.MoraMatches) st.DogfoodMora += BgiLogParser.RewardValue(m);

            foreach (Match m in RxGroupLoad().Matches(batch.Text))
            {
                string g = m.Groups["g"].Value.Trim();
                if (g.Length == 0) continue;
                st.Groups.Add(g);
                if (RxDogfoodGroup().IsMatch(g)) st.LastDogfoodRoute = g;
            }
            foreach (Match m in RxRouteDone().Matches(batch.Text))
            {
                string n = m.Groups["name"].Value.Trim();
                if (n.Length == 0) continue;
                if (RxDogfoodGroup().IsMatch(n))
                {
                    st.DogfoodRoutes++;
                    st.LastDogfoodRoute = n;
                }
            }

            if (!countPicks) return;
            foreach (Match m in batch.PickMatches)
            {
                string item = m.Groups["item"].Value.Trim();
                if (item.Length == 0) continue;
                st.Picks.TryGetValue(item, out int old);
                st.Picks[item] = old + 1;
            }
        }

        /// <summary>跨天清理：把非今天的记录丢掉。</summary>
        public static void PruneOldDays()
        {
            try
            {
                lock (_sync)
                {
                    foreach (var k in _map.Keys.ToList())
                        if (_map[k].Day != DateTime.Today) _map.Remove(k);
                }
            }
            catch { }
        }

        /// <summary>
        /// 从日志文件**整份**重建今日统计（用于首次加载 / 程序重启后恢复）。
        /// ★ 只扫当天文件，且限制读取量，避免 60MB 日志卡住。
        /// </summary>
        public static void RebuildFromFile(string userName, string logFile,
            long maxBytes = 32L * 1024 * 1024)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(logFile) || !File.Exists(logFile)) return;

                lock (_sync)
                {
                    // 重建前先清空当天统计，避免重复累加拾取
                    _map[userName] = new DailyStats { Day = DateTime.Today };
                }

                var fi = new FileInfo(logFile);
                long start = Math.Max(0, fi.Length - maxBytes);
                string text;

                using (var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    if (start > 0) fs.Seek(start, SeekOrigin.Begin);
                    var buf = new byte[fi.Length - start];
                    int got = fs.Read(buf, 0, buf.Length);
                    text = System.Text.Encoding.UTF8.GetString(buf, 0, got);
                    // 丢掉可能被截断的首行
                    if (start > 0)
                    {
                        int nl = text.IndexOf('\n');
                        if (nl >= 0) text = text.Substring(nl + 1);
                    }
                }

                Update(userName, text);
            }
            catch { }
        }
    }
}
