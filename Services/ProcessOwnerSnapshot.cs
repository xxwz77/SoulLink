using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace SoulLink.Services
{
    public sealed record ProcessOwnerRecord(int Pid, string Name, int SessionId, string UserName);

    /// <summary>每轮只执行一次 tasklist，所有流程共享只读进程/所有者快照。</summary>
    public static class ProcessOwnerSnapshot
    {
        private static readonly object Sync = new();
        private static ProcessOwnerRecord[] _items = Array.Empty<ProcessOwnerRecord>();
        public static bool Available { get; private set; }
        public static DateTime CapturedAt { get; private set; } = DateTime.MinValue;
        public static long RefreshCount { get; private set; }
        public static IReadOnlyList<ProcessOwnerRecord> Items { get { lock (Sync) return _items; } }
        /// <summary>
        /// 刷新进程/所有者快照。
        ///
        /// ★★ 2.17.17 重构：不再用 tasklist /V。★★
        ///
        ///   实测（本机 550 进程，2026-11-20）：
        ///     tasklist /V /FO CSV /NH   1583 ms   ← 旧实现
        ///     tasklist /FO CSV           806 ms
        ///     Process.GetProcesses()      14~25 ms  ← 现实现
        ///     逐个读 Process.SessionId     0 ms（内核已缓存，无系统调用）
        ///
        ///   旧实现单轮就要 1583ms，而 AccountStateCenter 的刷新周期是 1000ms，
        ///   于是重入保护（_refreshing）把大部分轮次直接跳过 —— 自动化条件读到的是
        ///   几十秒前的过期快照，且每轮真正跑起来时吃满一个核（交接文档 4.2 的 CPU 问题）。
        ///
        ///   现在改为内存枚举：约 20ms，每轮都能跑完，数据反而更新鲜。
        ///   用户名由会话号映射得到（见 SessionUsers），不再依赖 tasklist 的输出列。
        /// </summary>
        public static void Refresh()
        {
            Process[] procs = Array.Empty<Process>();
            try { procs = Process.GetProcesses(); }
            catch { }
            if (procs.Length == 0) { lock (Sync) Available = false; return; }

            var users = SessionUsers.Current;   // 会话号 → 用户名（由 SessionService 提供，缓存）
            var list = new List<ProcessOwnerRecord>(procs.Length);
            foreach (var p in procs)
            {
                try
                {
                    int sid = p.SessionId;
                    string name = "";
                    try { name = p.ProcessName; } catch { }
                    string user = users.TryGetValue(sid, out var u) ? u : "";
                    list.Add(new ProcessOwnerRecord(p.Id, name, sid, user));
                }
                catch { /* 已退出或权限不足，跳过 */ }
                finally { try { p.Dispose(); } catch { } }
            }

            lock (Sync)
            {
                _items = list.ToArray();
                Available = true;
                CapturedAt = DateTime.Now;
                RefreshCount++;
            }
        }

        /// <summary>
        /// 会话号 → 用户名的缓存映射。
        ///
        /// 进程枚举本身拿不到用户名，而 Exists(process, user) 这类调用需要它。
        /// 用户名与会话号的对应关系只在登录/注销/重连时变化，因此独立缓存，
        /// 且允许调用方直接喂入（AccountStateCenter 每轮本来就会查一次 query user）。
        /// </summary>
        public static class SessionUsers
        {
            private static readonly object Gate = new();
            private static Dictionary<int, string> _map = new();

            public static IReadOnlyDictionary<int, string> Current { get { lock (Gate) return _map; } }

            /// <summary>用一次 query user 的结果刷新映射（由 AccountStateCenter 轮询时调用）。</summary>
            public static void Set(IEnumerable<Models.SessionInfo> sessions)
            {
                if (sessions == null) return;
                var map = new Dictionary<int, string>();
                foreach (var s in sessions)
                {
                    if (s == null || s.Id <= 0 || string.IsNullOrWhiteSpace(s.UserName)) continue;
                    map[s.Id] = s.UserName;
                }
                lock (Gate) _map = map;
            }

            /// <summary>反查：用户名 → 当前会话号（0 表示未登录）。</summary>
            public static int SessionOf(string userName)
            {
                string bare = SessionService.BareName(userName ?? "");
                if (bare.Length == 0) return 0;
                lock (Gate)
                {
                    foreach (var kv in _map)
                        if (string.Equals(SessionService.BareName(kv.Value), bare, StringComparison.OrdinalIgnoreCase)) return kv.Key;
                }
                return 0;
            }
        }
        public static void Set(string csv, DateTime? now = null)
        {
            var items = Parse(csv);
            lock (Sync)
            {
                _items = items; Available = true; CapturedAt = now ?? DateTime.Now; RefreshCount++;
            }
        }
        public static bool Exists(string processName, string userName, int? sessionId = null)
        {
            if (string.IsNullOrWhiteSpace(processName) || (!sessionId.HasValue && string.IsNullOrWhiteSpace(userName))) return false;
            if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) processName = processName[..^4];

            // ★ 只给用户名、不给会话号时，先用"用户名 → 会话号"反查。
            //   这样用户重登导致会话号变化时无需任何配置改动，下一秒就能跟上。
            int? sid = sessionId;
            if (!sid.HasValue || sid.Value <= 0)
            {
                int resolved = SessionUsers.SessionOf(userName);
                if (resolved > 0) sid = resolved;
            }

            string user = SessionService.BareName(userName);
            lock (Sync) return _items.Any(p => p.Name.Equals(processName, StringComparison.OrdinalIgnoreCase)
                && (sid.HasValue && sid.Value > 0
                    ? p.SessionId == sid.Value
                    : string.Equals(SessionService.BareName(p.UserName), user, StringComparison.OrdinalIgnoreCase)));
        }
        internal static ProcessOwnerRecord[] Parse(string csv)
        {
            var result = new List<ProcessOwnerRecord>();
            using var reader = new StringReader(csv ?? "");
            while (reader.ReadLine() is string line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var f = Csv(line);
                if (f.Count < 7 || !int.TryParse(f[1], out int pid)) continue;
                int.TryParse(f[3], out int sid);
                result.Add(new ProcessOwnerRecord(pid, Path.GetFileNameWithoutExtension(f[0]), sid, f[6]));
            }
            return result.ToArray();
        }
        private static List<string> Csv(string line)
        {
            var values = new List<string>(); var value = new StringBuilder(); bool quoted = false;
            for (int i=0;i<line.Length;i++)
            {
                char c=line[i];
                if (c=='"') { if (quoted && i+1<line.Length && line[i+1]=='"') { value.Append('"'); i++; } else quoted=!quoted; }
                else if (c==',' && !quoted) { values.Add(value.ToString()); value.Clear(); }
                else value.Append(c);
            }
            values.Add(value.ToString()); return values;
        }
    }
}
