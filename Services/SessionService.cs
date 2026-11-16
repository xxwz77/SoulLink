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
    public static class SessionService
    {
        // 中英文系统都能吃：状态列在中文系统里是「运行中 / 断开」，英文是 Active / Disc
        private static readonly Regex SessionRx = new Regex(
            @"^\s*[>\*]?\s*(?<user>\S+)\s+(?<sess>\S*)\s+(?<id>\d+)\s+(?<state>\S+)\s+(?<idle>\S+)\s+(?<logon>\d{4}[/\-]\d{1,2}[/\-]\d{1,2}\s+\d{1,2}:\d{2})\s*$",
            RegexOptions.Compiled);

        /// <summary>调用 query user，返回当前机器上的全部交互式会话。</summary>
        public static async Task<List<SessionInfo>> QueryAsync()
        {
            var list = new List<SessionInfo>();
            string raw = await RunAsync("query.exe", "user", 8000).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return list;

            foreach (var line in raw.Replace("\r", "").Split('\n'))
            {
                string s = line.TrimEnd();
                if (s.Length == 0) continue;
                var m = SessionRx.Match(s);
                if (!m.Success) continue;
                int id;
                if (!int.TryParse(m.Groups["id"].Value, out id)) continue;
                string rawState = m.Groups["state"].Value;
                list.Add(new SessionInfo
                {
                    UserName = m.Groups["user"].Value.TrimStart('>', '*'),
                    SessionName = m.Groups["sess"].Value,
                    Id = id,
                    State = NormalizeState(rawState),
                    IdleTime = m.Groups["idle"].Value.Trim(),
                    LogonTime = m.Groups["logon"].Value
                });
            }

            // ★ query user 输出的是**小写**用户名（a1/a2），而账号真实名称是大写（A1/A2）。
            //   qwinsta 给的是真实大小写，用它校正显示（匹配逻辑本来就是忽略大小写的，
            //   所以这只是显示问题，不影响自动连接/投送）。
            try
            {
                ApplyRealCasing(list);
                SoulLink.Controls.RdpConsole.Debug("大小写校正后异常检查："
                    + string.Join(", ", list.ConvertAll(s => s.UserName)));
            }
            catch (Exception ex)
            {
                SoulLink.Controls.RdpConsole.Debug("大小写校正失败异常：" + ex.Message);
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