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
            return list;
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