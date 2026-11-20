using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>某个用户当前 BGI 的解析结果。</summary>
    public sealed class BgiPathInfo
    {
        /// <summary>用户名（BgiPaths 的键）。</summary>
        public string UserName = "";

        /// <summary>最终应该使用的路径；空表示没有可用的 BGI。</summary>
        public string Path = "";

        /// <summary>这个路径是怎么来的。</summary>
        public BgiPathSource Source = BgiPathSource.None;

        /// <summary>当前是否有该用户的 BGI 进程在运行。</summary>
        public bool Running;

        /// <summary>该用户见过的全部路径（含当前这个），已规范化去重。</summary>
        public List<string> Known = new List<string>();

        /// <summary>探测到的所有运行中实例（同会话可能有多个）。</summary>
        public List<BgiLogWatcher.Instance> LiveInstances = new List<BgiLogWatcher.Instance>();

        public bool HasPath => !string.IsNullOrWhiteSpace(Path);
    }

    public enum BgiPathSource
    {
        /// <summary>没有可用路径。</summary>
        None = 0,
        /// <summary>用户手动指定（BgiPinnedUsers 里有该用户）。</summary>
        Pinned,
        /// <summary>自动探测到正在运行的 BGI。</summary>
        AutoRunning,
        /// <summary>没在运行，沿用已知路径（记忆）。</summary>
        Remembered
    }

    /// <summary>
    /// BGI 路径的统一解析入口。
    ///
    /// ★ 为什么要有这一层：
    ///   原来"启动台日志"用进程探测、"更新 BGI"用配置字典，两边来源不同，
    ///   于是出现"日志能自动发现 BGI，但更新页看不到路径"。
    ///   这里把两条来源合并，所有调用方都只认这一个结果。
    ///
    /// ★ 防错乱的三条硬规则：
    ///   1. 用户名 → 会话号 必须走 SessionService.Find，不能靠进程名猜。
    ///   2. 同一会话有多个 BGI 时全部返回，按启动时间排序，不静默覆盖。
    ///   3. 路径一律 Path.GetFullPath 规范化后比较，避免等价路径被当成两个。
    /// </summary>
    public static class BgiPathResolver
    {
        private static readonly object _sync = new object();

        /// <summary>
        /// 解析某个用户的 BGI 路径。会顺带把新发现的路径并入配置（调用方负责持久化）。
        /// </summary>
        /// <param name="userName">Windows 用户名</param>
        /// <param name="sessions">当前会话快照；传 null 则内部自行查询一次</param>
        /// <param name="persist">是否把新发现写回配置对象（不落盘）</param>
        // ── 解析结果缓存 ────────────────────────────────────────────
        //
        // ★ 实测：Resolve 占 UI 相关 CPU 的 3.5%。它被 BgiDataCenter.Get
        //   在界面刷新里每个账号每 3 秒调一次，而每次都要枚举进程 + 建对象。
        //   1 秒缓存足够：BGI 的启动/退出本来就是秒级事件。
        private static readonly object _cacheSync = new object();
        private static readonly Dictionary<string, (BgiPathInfo Info, DateTime At)> _cache
            = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(1);

        /// <summary>路径/绑定变化后立刻失效缓存。</summary>
        public static void InvalidateCache(string? userName = null)
        {
            lock (_cacheSync)
            {
                if (string.IsNullOrWhiteSpace(userName)) _cache.Clear();
                else _cache.Remove(userName);
            }
        }

        public static BgiPathInfo Resolve(string userName,
            List<SessionInfo>? sessions = null, bool persist = true)
        {
            var info = new BgiPathInfo { UserName = userName ?? "" };
            if (string.IsNullOrWhiteSpace(userName)) return info;

            // 带会话快照和 persist 的调用走原路径（不做缓存，语义不同）。
            if (sessions == null && !persist)
            {
                lock (_cacheSync)
                {
                    if (_cache.TryGetValue(userName, out var hit)
                        && (DateTime.UtcNow - hit.At) < CacheTtl && hit.Info != null)
                        return hit.Info;
                }
                var computed = ResolveCore(userName, null, false);
                lock (_cacheSync) _cache[userName] = (computed, DateTime.UtcNow);
                return computed;
            }
            return ResolveCore(userName, sessions, persist);
        }

        private static BgiPathInfo ResolveCore(string userName,
            List<SessionInfo>? sessions, bool persist)
        {
            var info = new BgiPathInfo { UserName = userName ?? "" };
            if (string.IsNullOrWhiteSpace(userName)) return info;

            var settings = App.Config.Settings;

            // ① 该用户会话号 → 该会话里正在跑的 BGI
            //
            // ★ 优先用账号状态中心的快照：它每秒统一刷新一次，
            //   所有调用方共享同一份数据，避免"每个条件各查一遍 query user + 枚举进程"。
            //   只有快照不可用（未启动 / 太旧）时才退回直查。
            try
            {
                var st = AccountStateCenter.GetByUser(userName);
                if (st != null
                    && (DateTime.Now - AccountStateCenter.LastRefresh).TotalSeconds < 3)
                {
                    // 会话号来自快照
                    if (st.HasSession && st.BgiRunning)
                    {
                        // 进程信息仍需要 Instance 里的日志文件路径，
                        // 但这只在"确实在跑"时才取，且 Discover 本身有缓存。
                        var mapQuick = BgiLogWatcher.Discover();
                        if (mapQuick.TryGetValue(st.SessionId, out var instQuick) && instQuick != null)
                        {
                            info.LiveInstances.Add(instQuick);
                            info.Running = true;
                        }
                    }

                    if (info.Running || !string.IsNullOrWhiteSpace(st.BgiPath))
                    {
                        // 走到这里说明快照可用，直接用它的结论
                        var knownSt = new List<string>();
                        if (!string.IsNullOrWhiteSpace(st.BgiPath)) knownSt.Add(st.BgiPath);
                        info.Known = knownSt;

                        bool pinnedSt = settings.BgiPinnedUsers
                            .Contains(userName, StringComparer.OrdinalIgnoreCase);
                        if (pinnedSt && !string.IsNullOrWhiteSpace(st.BgiPath))
                        {
                            info.Path = st.BgiPath;
                            info.Source = BgiPathSource.Pinned;
                        }
                        else if (info.Running && info.LiveInstances.Count > 0)
                        {
                            info.Path = Normalize(info.LiveInstances[0].ExePath);
                            info.Source = BgiPathSource.AutoRunning;
                        }
                        else if (!string.IsNullOrWhiteSpace(st.BgiPath))
                        {
                            info.Path = st.BgiPath;
                            info.Source = BgiPathSource.Remembered;
                        }
                        return info;
                    }
                }

                var list = sessions;
                if (list == null)
                {
                    try { list = SessionService.QueryAsync().GetAwaiter().GetResult(); }
                    catch { list = null; }
                }

                SessionInfo? sess = null;
                if (list != null) sess = SessionService.Find(list, userName);

                if (sess != null && sess.Id > 0)
                {
                    var map = BgiLogWatcher.Discover();
                    if (map.TryGetValue(sess.Id, out var inst) && inst != null)
                    {
                        // ★ 同一会话允许多个 BGI：Discover 只保留一个，
                        //   这里再单独取全部，避免"后启动的覆盖先启动的"这种不确定行为。
                        var all = BgiLogWatcher.DiscoverAll();
                        info.LiveInstances = all
                            .Where(x => x.SessionId == sess.Id)
                            .OrderBy(x => x.StartTime)
                            .ToList();

                        if (info.LiveInstances.Count == 0) info.LiveInstances.Add(inst);
                        info.Running = true;
                    }
                }
            }
            catch { }

            // ② 记录见过的所有路径
            var known = new List<string>();
            try
            {
                if (settings.BgiKnownPaths.TryGetValue(userName, out var saved) && saved != null)
                    foreach (var p in saved) AddKnown(known, p);
            }
            catch { }

            foreach (var inst in info.LiveInstances)
                AddKnown(known, inst.ExePath);

            string remembered = "";
            try
            {
                if (settings.BgiPaths.TryGetValue(userName, out var rp) && !string.IsNullOrWhiteSpace(rp))
                {
                    remembered = Normalize(rp);
                    AddKnown(known, remembered);
                }
            }
            catch { }

            info.Known = known;

            // ③ 选定最终路径
            bool pinned = settings.BgiPinnedUsers.Contains(userName, StringComparer.OrdinalIgnoreCase);

            if (pinned && !string.IsNullOrWhiteSpace(remembered))
            {
                // 人工优先：用户明确指定过，就用它（即使没在运行）
                info.Path = remembered;
                info.Source = BgiPathSource.Pinned;
            }
            else if (info.LiveInstances.Count > 0)
            {
                // 没指定 → 用正在运行的那个（多个时取最早启动的，行为确定）
                info.Path = Normalize(info.LiveInstances[0].ExePath);
                info.Source = BgiPathSource.AutoRunning;
            }
            else if (!string.IsNullOrWhiteSpace(remembered))
            {
                // 没在运行 → 沿用记忆
                info.Path = remembered;
                info.Source = BgiPathSource.Remembered;
            }
            else if (known.Count > 0)
            {
                info.Path = known[0];
                info.Source = BgiPathSource.Remembered;
            }

            // ④ 写回配置（只更新对象，落盘由调用方决定）
            if (persist)
            {
                lock (_sync)
                {
                    try
                    {
                        if (known.Count > 0) settings.BgiKnownPaths[userName] = known;
                        if (info.HasPath) settings.BgiPaths[userName] = info.Path;
                    }
                    catch { }
                }
            }

            return info;
        }

        /// <summary>
        /// 批量解析：传入用户名的集合与可选的会话快照，返回 用户名 → 解析结果。
        /// 一次查询会话、一次枚举进程，避免每个用户都跑一遍（列表页会用到）。
        /// </summary>
        public static Dictionary<string, BgiPathInfo> ResolveMany(
            IEnumerable<string> userNames, List<SessionInfo>? sessions = null, bool persist = true)
        {
            var result = new Dictionary<string, BgiPathInfo>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var list = sessions;
                if (list == null)
                {
                    try { list = SessionService.QueryAsync().GetAwaiter().GetResult(); }
                    catch { list = null; }
                }
                foreach (var u in userNames ?? Enumerable.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(u)) continue;
                    if (result.ContainsKey(u)) continue;
                    result[u] = Resolve(u, list, persist);
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// 取用户当前该用的 BGI 路径（空串 = 没有可用路径）。
        /// 这是给"只关心路径字符串"的调用方用的最简入口。
        /// </summary>
        public static string GetPath(string userName)
        {
            try
            {
                // ★ 读取路径绝不写回配置。
                //   原来 persist:true 会让「打开数据中心页」这个纯读动作
                //   把自动探测到的路径写进 BgiPaths，于是"用户有没有绑过"
                //   这个判断被污染（实测 A1/A4 的 BgiPinnedUsers 是空的，
                //   但 BgiPaths 里已有值）。只有 Pin() 才能写。
                var info = Resolve(userName, null, persist: false);
                return info.HasPath ? info.Path : "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// 取"用户显式绑定"的 BGI 路径；没绑定返回空串。
        ///
        /// ★ 与 <see cref="GetPath"/> 的区别：
        ///   GetPath 是"该用哪个"（可能来自自动探测/记忆），
        ///   本方法是"用户填过没有"——决定要不要读日志只看它。
        /// </summary>
        public static string GetBoundPath(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) return "";
            if (!IsPinned(userName)) return "";
            try
            {
                if (App.Config.Settings.BgiPaths.TryGetValue(userName, out var p) && !string.IsNullOrWhiteSpace(p))
                    return Normalize(p);
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 一次性迁移：把"已有路径 + 该路径对应的进程正在运行"的账号标记为已绑定。
        ///
        /// ★ 为什么需要：旧版 Resolve 自动把路径写进 BgiPaths，
        ///   导致改造后所有跑过 BGI 的账号都成了"未绑定"、整片数据不再刷新。
        ///   这里只在首次启动时补一次，之后完全由用户点按钮决定。
        /// ★ 只认**确实在运行**的，避免把历史残留路径误判成用户意图。
        /// </summary>
        public static int MigrateAutoBindings()
        {
            int n = 0;
            try
            {
                var settings = App.Config.Settings;
                if (settings.BgiMigrated) return 0;

                var live = new System.Collections.Generic.HashSet<string>(
                    System.IO.Path.GetFullPath(Environment.ProcessPath ?? ".").Length == 0
                        ? Array.Empty<string>() : Array.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var inst in BgiLogWatcher.Discover().Values)
                        if (inst != null && !string.IsNullOrWhiteSpace(inst.ExePath)) live.Add(Normalize(inst.ExePath));
                }
                catch { }

                foreach (var acc in AccountScope.Accounts(App.Config))
                {
                    string user = acc?.UserName ?? "";
                    if (user.Length == 0 || IsPinned(user)) continue;
                    if (!settings.BgiPaths.TryGetValue(user, out var saved) || string.IsNullOrWhiteSpace(saved)) continue;
                    if (!live.Contains(Normalize(saved))) continue;   // 没在跑 → 当作未绑定
                    Pin(user, saved);
                    n++;
                }

                settings.BgiMigrated = true;
                if (n > 0 || true) ConfigStore.Save(App.Config);
            }
            catch { }
            return n;
        }

        /// <summary>用户手动指定路径（会标记为 pinned）。</summary>
        public static void Pin(string userName, string fullPath)
        {
            if (string.IsNullOrWhiteSpace(userName)) return;
            var settings = App.Config.Settings;
            lock (_sync)
            {
                var norm = Normalize(fullPath);
                if (string.IsNullOrWhiteSpace(norm)) return;

                settings.BgiPaths[userName] = norm;
                if (!settings.BgiPinnedUsers.Contains(userName, StringComparer.OrdinalIgnoreCase))
                    settings.BgiPinnedUsers.Add(userName);

                List<string> list;
                if (!settings.BgiKnownPaths.TryGetValue(userName, out list!) || list == null)
                {
                    list = new List<string>();
                    settings.BgiKnownPaths[userName] = list;
                }
                AddKnown(list, norm);
            }
            InvalidateCache(userName);
        }

        /// <summary>恢复自动模式（后续以探测到的为准）。</summary>
        public static void Unpin(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) return;
            try
            {
                App.Config.Settings.BgiPinnedUsers
                    .RemoveAll(x => string.Equals(x, userName, StringComparison.OrdinalIgnoreCase));
            }
            catch { }
            InvalidateCache(userName);
        }

        /// <summary>该用户是否处于手动指定状态。</summary>
        public static bool IsPinned(string userName)
        {
            try
            {
                return App.Config.Settings.BgiPinnedUsers
                    .Contains(userName, StringComparer.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// 解析结果里是否"没在运行但配置指向旧路径"——这种情况界面上要提示，
        /// 否则用户会以为配置自动更新了。
        /// </summary>
        public static bool IsStale(BgiPathInfo info)
            => info != null && info.HasPath && !info.Running && info.Source == BgiPathSource.Remembered;

        private static void AddKnown(List<string> list, string? path)
        {
            var n = Normalize(path);
            if (string.IsNullOrWhiteSpace(n)) return;
            if (list.Contains(n, StringComparer.OrdinalIgnoreCase)) return;
            list.Add(n);
        }

        /// <summary>统一规范化：展开环境变量、转绝对路径、去掉结尾分隔符。</summary>
        public static string Normalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try
            {
                string p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                p = Path.GetFullPath(p);
                return p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { return (path ?? "").Trim(); }
        }
    }
}
