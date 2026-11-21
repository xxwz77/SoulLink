using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>
    /// 会话发现：等价于批处理里的 `for /f "skip=1" ... query user`，
    /// 但解析更稳（中文 / 英文系统、状态单词、空闲时间里的空格都能处理）。
    /// </summary>
    public static partial class SessionService
    {
        // ★ 宽松版：不再强求登录时间的具体格式。
        //   原来要求 yyyy/MM/dd HH:mm，遇到 11/17/2026、2026-11-17、
        //   或者系统没输出时间列时整行匹配失败 → 一个会话都读不到。
        //   现在：用户 会话名 ID 状态 后面的内容全部吞掉，再单独解析空闲/登录时间。
        [GeneratedRegex(@"^\s*[>\*]?\s*(?<user>\S+)\s+(?<sess>\S*)\s+(?<id>\d+)\s+(?<state>\S+)(?<rest>.*)$", RegexOptions.Compiled)]
        private static partial Regex SessionRx();

        /// <summary>从行尾解析空闲时间和登录时间（宽松，失败也不影响会话本身）。</summary>
        private static void ParseTail(string rest, out string idle, out string logon)
        {
            idle = "";
            logon = "";

            string t = (rest ?? "").Trim();
            if (t.Length == 0) return;

            // 登录时间：兼容各种区域格式
            //   yyyy/MM/dd HH:mm   yyyy-MM-dd HH:mm   MM/dd/yyyy HH:mm
            //   11.16 4:10         （点号 + 无年份 + 单位小时，实测中文系统会这样输出）
            var mDate = Regex.Match(t,
                @"(?<d>\d{4}[/\-\.]\d{1,2}[/\-\.]\d{1,2}" +      // 带年份
                @"|\d{1,2}[/\-\.]\d{1,2}[/\-\.]\d{4}" +          // 年在后
                @"|\d{1,2}[\-\.]\d{1,2})" +                         // 无年份（11.16）
                @"\s+(?<t>\d{1,2}:\d{2}(?::\d{2})?)");
            if (mDate.Success)
            {
                logon = mDate.Groups["d"].Value + " " + mDate.Groups["t"].Value;
                t = t.Substring(0, mDate.Index).Trim();
            }

            // 空闲时间：none / . / 数字 / H:MM / D:HH:MM
            if (t.Length > 0)
                idle = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
        }

        /// <summary>调用 query user，返回当前机器上的全部交互式会话。</summary>
        public static async Task<List<SessionInfo>> QueryAsync()
        {
            // ★★ 首选：WTSEnumerateSessions（原生 API，不起子进程）★★
            //
            //   每次 Process.Start 会在 .NET 里留下约 0.6 个命名管道句柄
            //   （实测：重定向读流 60 次 +36，不重定向 60 次 +38，GC 也不回收）。
            //   状态中心启动窗口是 1 Hz，等于每天 10 万个句柄；
            //   系统管道总数一旦到几万，任何遍历 \\.\pipe\ 的代码都会慢到数秒。
            //   WTS API 不创建进程，零泄漏，且快约 500 倍。
            var viaWts = TryQueryViaWts();
            if (viaWts != null && viaWts.Count > 0) return viaWts;

            // ── 以下为兜底：WTS 不可用时退回子进程 ──
            var list = new List<SessionInfo>();

            // ★ 2.17.45：先试 qwinsta —— 它**一次**就给出会话号 + 真实大小写的用户名
            //   + 状态，不需要再起第二个子进程做大小写校正。
            //   原来每轮要起 query.exe（~32ms）再起 qwinsta（~32ms），
            //   而调用方（状态中心 1 Hz、启动台 3 秒、控制台 2 秒）各自都会触发，
            //   实测占 UI 相关 CPU 的 13%。
            var viaQwinsta = await QueryViaQwinstaAsync().ConfigureAwait(false);
            if (viaQwinsta.Count > 0) return viaQwinsta;

            string raw = await RunAsync("query.exe", "user", 8000).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return list;

            foreach (var line in raw.Replace("\r", "").Split('\n'))
            {
                string s = line.TrimEnd();
                if (s.Length == 0) continue;
                var m = SessionRx().Match(s);
                if (!m.Success) continue;
                int id;
                if (!int.TryParse(m.Groups["id"].Value, out id)) continue;
                string rawState = m.Groups["state"].Value;
                string user = m.Groups["user"].Value.TrimStart('>', '*');

                // 跳过表头（英文 USERNAME / 中文 用户名 / 分隔线）
                if (user.Equals("USERNAME", StringComparison.OrdinalIgnoreCase)
                    || user.Equals("用户名", StringComparison.Ordinal)
                    || user.StartsWith("---", StringComparison.Ordinal))
                    continue;

                ParseTail(m.Groups["rest"].Value, out string idle, out string logon);

                list.Add(new SessionInfo
                {
                    UserName = user,
                    SessionName = m.Groups["sess"].Value,
                    Id = id,
                    State = NormalizeState(rawState),
                    IdleTime = idle,
                    LogonTime = logon
                });
            }

            // ★ query user 输出的是**小写**用户名（a1/a2），而账号真实名称是大写（A1/A2）。
            //   qwinsta 给的是真实大小写，用它校正显示（匹配逻辑本来就是忽略大小写的，
            //   所以这只是显示问题，不影响自动连接/投送）。
            try { ApplyRealCasing(list); } catch { }
            return list;
        }

        /// <summary>
        /// 用 WTSEnumerateSessions 读会话。失败返回 null（调用方退回子进程）。
        ///
        /// ★ 返回的 UserName 是**真实大小写**，正好解决了原来
        ///   query user 输出小写（a1/a2）需要再起 qwinsta 校正的问题 ——
        ///   一条 API 就把两件事都办了。
        /// </summary>
        private static List<SessionInfo>? TryQueryViaWts()
        {
            try
            {
                var raw = WtsSessionQuery.Enumerate();
                if (raw == null || raw.Count == 0) return null;

                var list = new List<SessionInfo>(raw.Count);
                foreach (var r in raw)
                {
                    list.Add(new SessionInfo
                    {
                        UserName = r.UserName,
                        SessionName = r.Station,
                        Id = r.Id,
                        State = NormalizeState(r.State),
                        // WTS 不直接给空闲时长/登录时间；这两个字段只用于显示，
                        // 原来也是宽松解析、经常为空，这里留空不影响任何逻辑。
                        IdleTime = "",
                        LogonTime = "",
                    });
                }
                return list;
            }
            catch { return null; }
        }

        /// <summary>
        /// 用 qwinsta 一次性拿到会话（含真实大小写用户名）。
        /// ★ qwinsta 列顺序是「会话名 用户名 ID 状态」，会话名可能为空
        ///   （断开的会话会这样输出），所以按"第一个纯数字 = 会话号"定位，
        ///   它前一列就是用户名。
        /// </summary>
        private static async Task<List<SessionInfo>> QueryViaQwinstaAsync()
        {
            var list = new List<SessionInfo>();
            string raw;
            try { raw = await RunAsync("qwinsta", "", 5000).ConfigureAwait(false); }
            catch { return list; }
            if (string.IsNullOrWhiteSpace(raw)) return list;

            foreach (var line in raw.Replace("\r", "").Split('\n'))
            {
                if (line.Length == 0) continue;
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                int idIdx = -1;
                for (int i = 0; i < parts.Length; i++)
                    if (int.TryParse(parts[i], out _)) { idIdx = i; break; }
                if (idIdx < 1) continue;
                if (!int.TryParse(parts[idIdx], out int sid) || sid <= 0) continue;

                string user = parts[idIdx - 1].TrimStart('>', '*');
                if (user.Length == 0) continue;
                if (user.Equals("USERNAME", StringComparison.OrdinalIgnoreCase)
                    || user.Equals("用户名", StringComparison.Ordinal)
                    || user.StartsWith("---", StringComparison.Ordinal)) continue;

                string state = idIdx + 1 < parts.Length ? NormalizeState(parts[idIdx + 1]) : "";
                string sess = idIdx >= 2 ? parts[idIdx - 2] : "";

                list.Add(new SessionInfo
                {
                    UserName = user,
                    SessionName = sess,
                    Id = sid,
                    State = state,
                    IdleTime = "",
                    LogonTime = ""
                });
            }
            return list;
        }

        /// <summary>用 qwinsta 的真实用户名校正 query user 的大小写。</summary>
        private static void ApplyRealCasing(List<SessionInfo> list)
        {
            if (list.Count == 0) return;
            string raw;
            try
            {
                // 同步执行：这里已经在后台线程（QueryAsync 内部 await 之后）
                var psi = new System.Diagnostics.ProcessStartInfo("qwinsta")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = SystemLauncher.ConsoleEncoding(),
                    StandardErrorEncoding = SystemLauncher.ConsoleEncoding()
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return;
                raw = p.StandardOutput.ReadToEnd();
                if (string.IsNullOrWhiteSpace(raw)) raw = p.StandardError.ReadToEnd();
                p.WaitForExit(5000);
            }
            catch { return; }
            if (string.IsNullOrWhiteSpace(raw)) return;

            // qwinsta 行格式：<会话名>  <用户名>  <ID>  <状态>  ...
            foreach (var line in raw.Replace("\r", "").Split('\n'))
            {
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                // ★ 找数字 ID 的位置（第一个纯数字）。
                //   会话名这一列**可能是空的**（会话断开后 qwinsta 会这么输出）：
                //             A2                        8  Disc
                //   此时按空格切分结果是 [A2, 8, Disc]，用户名列落到 parts[0]，
                //   所以 idIdx 可能是 1 —— 原来要求 idIdx >= 2 把所有 Disc 会话都跳过了，
                //   导致这些会话的大小写校正永远不生效（用户实测 a2/a3 一直是小写）。
                int idIdx = -1;
                for (int i = 0; i < parts.Length; i++)
                    if (int.TryParse(parts[i], out _)) { idIdx = i; break; }
                if (idIdx < 1) continue;
                if (!int.TryParse(parts[idIdx], out int sid)) continue;

                string realUser = parts[idIdx - 1];
                if (string.IsNullOrWhiteSpace(realUser)) continue;
                realUser = realUser.TrimStart('>', '*');

                foreach (var s in list)
                {
                    if (s.Id == sid &&
                        string.Equals(s.UserName, realUser, StringComparison.OrdinalIgnoreCase))
                    {
                        s.UserName = realUser;   // 用真实大小写
                        break;
                    }
                }
            }
        }

        /// <summary>把「运行中 / 断开 / Active / Disc」统一成 Active / Disc。</summary>
        public static string NormalizeState(string raw)
        {
            string s = (raw ?? "").Trim();
            if (s.Length == 0) return "";

            if (s.Equals("Active", StringComparison.OrdinalIgnoreCase)
                || s.Equals("运行中", StringComparison.Ordinal)
                || s.Equals("活动", StringComparison.Ordinal)
                || s.StartsWith("Active", StringComparison.OrdinalIgnoreCase))
                return "Active";

            if (s.StartsWith("Disc", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("断开", StringComparison.Ordinal)
                || s.Equals("已断开", StringComparison.Ordinal))
                return "Disc";

            return s;
        }

        /// <summary>只保留真实登录用户（去掉 services、UMFD、DWM 之类）。</summary>
        public static List<SessionInfo> FilterUsers(IEnumerable<SessionInfo> all)
        {
            return all.Where(s => !s.UserName.Equals("services", StringComparison.OrdinalIgnoreCase)
                                  && !s.UserName.StartsWith("UMFD-", StringComparison.OrdinalIgnoreCase)
                                  && !s.UserName.StartsWith("DWM-", StringComparison.OrdinalIgnoreCase)
                                  && !s.UserName.Equals("NT AUTHORITY", StringComparison.OrdinalIgnoreCase)
                                  && (s.IsActive || s.IsDisconnected))
                       .OrderBy(s => s.Id)
                       .ToList();
        }

        /// <summary>按用户名找会话 ID；同名多会话时优先 Active 的那一个。</summary>
        public static SessionInfo? Find(List<SessionInfo> sessions, string user)
        {
            if (string.IsNullOrWhiteSpace(user)) return null;
            string bare = BareName(user);
            var hits = sessions.Where(s => string.Equals(s.UserName, bare, StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(BareName(s.UserName), bare, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) return null;
            return hits.FirstOrDefault(h => h.IsActive) ?? hits[0];
        }

        /// <summary>去掉 DOMAIN\ 前缀</summary>
        public static string BareName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int i = name.LastIndexOf('\\');
            return i >= 0 ? name.Substring(i + 1) : name;
        }

        /// <summary>当前进程所在会话 ID</summary>
        public static int CurrentSessionId
        {
            get
            {
                int pid = Process.GetCurrentProcess().Id;
                int sid;
                return Native.ProcessIdToSessionId(pid, out sid) ? sid : -1;
            }
        }

        /// <summary>当前用户名</summary>
        public static string CurrentUser => Environment.UserName;

        internal static async Task<string> RunAsync(string file, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = SystemLauncher.ConsoleEncoding(),
                    StandardErrorEncoding = SystemLauncher.ConsoleEncoding()
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return "";
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    var errTask = p.StandardError.ReadToEndAsync();
                    var done = await Task.WhenAny(Task.WhenAll(outTask, errTask), Task.Delay(timeoutMs)).ConfigureAwait(false);
                    if (done != (Task)outTask && !p.HasExited)
                    {
                        try { p.Kill(true); } catch { }
                        return "";
                    }
                    try { p.WaitForExit(2000); } catch { }
                    string text = (await outTask.ConfigureAwait(false));
                    string err = (await errTask.ConfigureAwait(false));
                    // query user 在无人登录时把提示写到 stderr
                    return string.IsNullOrWhiteSpace(text) ? err : text;
                }
            }
            catch
            {
                return "";
            }
        }
    }
}
