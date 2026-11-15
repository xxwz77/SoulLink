using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>会话内进程枚举 / 结束。管理员 + SeDebugPrivilege 即可跨会话操作。</summary>
    public static class ProcessService
    {
        /// <summary>列出某会话中的可见用户进程。</summary>
        public static List<RemoteProcess> List(int sessionId, bool includeSystem = false)
        {
            var result = new List<RemoteProcess>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id < 8) continue;
                    if (p.SessionId != sessionId) continue;

                    string name = SafeName(p);
                    if (!includeSystem && IsSystemProcess(name)) continue;

                    string path = SafePath(p);
                    if (!includeSystem && string.IsNullOrEmpty(path)) continue;

                    result.Add(new RemoteProcess
                    {
                        Pid = p.Id,
                        Name = name,
                        ExePath = path,
                        Title = SafeTitle(p),
                        StartTime = SafeStart(p)
                    });
                }
                catch { }
                finally { p.Dispose(); }
            }
            return result.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static readonly HashSet<string> SystemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "svchost","dwm","csrss","wininit","winlogon","services","lsass","smss","fontdrvhost",
            "sihost","ctfmon","taskhostw","runtimebroker","searchHost","ShellExperienceHost",
            "StartMenuExperienceHost","TextInputHost","LockApp","LogonUI","WmiPrvSE","conhost"
        };

        private static bool IsSystemProcess(string name)
        {
            string n = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            return SystemNames.Contains(n);
        }

        private static string SafeName(Process p)
        {
            try { return p.ProcessName + ".exe"; } catch { return ""; }
        }

        private static string SafePath(Process p)
        {
            try { return p.MainModule?.FileName ?? ""; } catch { return ""; }
        }

        private static string SafeTitle(Process p)
        {
            try { return p.MainWindowTitle ?? ""; } catch { return ""; }
        }

        private static string SafeStart(Process p)
        {
            try { return p.StartTime.ToString("yyyy-MM-dd HH:mm:ss"); } catch { return ""; }
        }

        /// <summary>根据路径 / 文件名匹配进程（大小写不敏感，全路径优先，退化到文件名）。</summary>
        public static List<RemoteProcess> MatchByExe(IEnumerable<RemoteProcess> list, string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return new List<RemoteProcess>();
            string full = exePath.Trim().Trim('"');
            string fileName = Path.GetFileName(full);

            var byPath = list.Where(p => !string.IsNullOrEmpty(p.ExePath)
                                         && string.Equals(Normalize(p.ExePath), Normalize(full), StringComparison.OrdinalIgnoreCase))
                             .ToList();
            if (byPath.Count > 0) return byPath;

            // 路径没拿到（权限不足）时按文件名兜底
            return list.Where(p => string.Equals(p.Name, fileName, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public static string Normalize(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd('\\'); } catch { return path.Trim().TrimEnd('\\'); }
        }

        /// <summary>
        /// 结束进程。管理员 + SeDebugPrivilege 就足以跨会话 Kill，不需要任何外部工具。
        /// </summary>
        public static async Task<ExecResult> KillAsync(RemoteProcess target, CancellationToken ct = default)
        {
            SystemLauncher.EnablePrivileges();
            try
            {
                using (var p = Process.GetProcessById(target.Pid))
                {
                    int sid = p.SessionId;
                    p.Kill(true);
                    await p.WaitForExitAsync(ct).ConfigureAwait(false);
                    return ExecResult.Success("已结束 " + target.Name + " (PID " + target.Pid + " · 会话 " + sid + ")");
                }
            }
            catch (Exception ex)
            {
                var (ok, msg) = SystemLauncher.Kill(target.Pid);
                if (ok) return ExecResult.Success(msg);
                return ExecResult.Fail("结束 PID " + target.Pid + " 失败：" + ex.Message
                    + (string.IsNullOrEmpty(msg) ? "" : " / " + msg));
            }
        }

        public static string FirstLine(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            foreach (var line in s.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length > 0) return t.Length > 220 ? t.Substring(0, 220) + "…" : t;
            }
            return "";
        }
    }
}