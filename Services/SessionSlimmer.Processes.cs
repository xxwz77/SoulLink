using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SoulLink.Models;

namespace SoulLink.Services
{
    public static partial class SessionSlimmer
    {
        public static bool CanSelectUser(string user)
        {
            string name = SessionService.BareName(user?.Trim() ?? "");
            return name.Length > 0 && !AccountScope.IsPrimaryUser(name)
                && App.Config?.Accounts?.Any(a => string.Equals(SessionService.BareName(a.UserName?.Trim() ?? ""), name, StringComparison.OrdinalIgnoreCase)) == true;
        }

        public static bool CanSelectSession(SessionInfo session)
            => session.Id > 0 && !session.IsConsoleSession && CanSelectUser(session.UserName)
                && session.Id != CurrentSessionId && (uint)session.Id != WTSGetActiveConsoleSessionId();

        private static int CurrentSessionId
        {
            get { using var process = Process.GetCurrentProcess(); return process.SessionId; }
        }

        private static string? SessionOwnerSid(int sessionId, string expectedUser)
        {
            if (sessionId <= 0 || sessionId == CurrentSessionId || (uint)sessionId == WTSGetActiveConsoleSessionId()
                || !CanSelectUser(expectedUser)) return null;
            var (user, domain) = WtsSessionQuery.AccountForSession(sessionId);
            if (!string.Equals(user, SessionService.BareName(expectedUser.Trim()), StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(domain))
                return null;
            bool configured = App.Config?.Accounts?.Any(a =>
                string.Equals(a.UserName?.Trim(), user, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.UserName?.Trim(), domain + "\\" + user, StringComparison.OrdinalIgnoreCase)) == true;
            if (!configured) return null;
            try
            {
                var expected = (SecurityIdentifier)new NTAccount(domain, user).Translate(typeof(SecurityIdentifier));
                using var current = WindowsIdentity.GetCurrent();
                return expected.Equals(current.User) ? null : expected.Value;
            }
            catch { return null; }
        }

        public static SessionStat Count(int sessionId, string expectedUser)
            => CountSessions([(sessionId, expectedUser)])[sessionId];

        internal static IReadOnlyDictionary<int, SessionStat> CountSessions(
            IReadOnlyList<(int SessionId, string ExpectedUser)> sessions)
        {
            var targets = sessions.ToDictionary(session => session.SessionId, session =>
            {
                string? owner = SessionOwnerSid(session.SessionId, session.ExpectedUser);
                return (Owner: owner, Stat: new SessionStat { SessionId = session.SessionId, OwnerVerified = owner != null });
            });
            var killNames = EffectiveKillNameSet();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        int sessionId = process.SessionId;
                        if (!targets.TryGetValue(sessionId, out var target)) continue;
                        var (owner, stat) = target;
                        string name = process.ProcessName;
                        long memory = 0;
                        try { memory = process.PrivateMemorySize64; } catch { }
                        stat.Total++;
                        stat.TotalBytes += memory;
                        if (owner != null && killNames.Contains(name))
                        {
                            using var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessTerminate, false, process.Id);
                            if (!TryReadIdentity(handle, process.Id, sessionId, owner, name, out long started, out string path))
                            {
                                stat.Unverified++;
                                continue;
                            }
                            stat.CleanupTargets.Add(new() { Pid = process.Id, Name = name, ExePath = path,
                                StartFileTime = started });
                            stat.Killable++;
                            stat.KillableBytes += memory;
                            stat.KillableDetail.TryGetValue(name, out var detail);
                            stat.KillableDetail[name] = (detail.Count + 1, detail.Bytes + memory);
                        }
                        else if (name is "SearchHost" or "Widgets" or "WidgetService")
                        {
                            stat.PolicyOnly++;
                            stat.PolicyOnlyBytes += memory;
                        }
                    }
                    catch { }
                }
            }
            return targets.ToDictionary(target => target.Key, target => target.Value.Stat);
        }

        internal static Result Slim(int sessionId, string expectedUser, IReadOnlyList<RemoteProcess> preview)
        {
            var killNames = EffectiveKillNameSet();
            var (count, errors) = EndSelected(sessionId, expectedUser, preview.Where(p => killNames.Contains(Normalize(p.Name))));
            return new Result
            {
                Killed = count,
                Message = "已请求结束 " + count + " 个外围进程"
                    + (errors.Count == 0 ? "。" : "。\n" + string.Join("\n", errors))
            };
        }

        public static (int ended, List<string> errors) EndSelected(int sessionId, string expectedUser, IEnumerable<RemoteProcess> targets)
        {
            int ended = 0;
            var errors = new List<string>();
            if (App.Config?.Settings?.SessionSlimKillEnabled != true)
            {
                errors.Add("会话瘦身清理已关闭。");
                return (ended, errors);
            }
            string? owner = SessionOwnerSid(sessionId, expectedUser);
            if (owner == null) { errors.Add("无法确认目标账号及会话。"); return (ended, errors); }
            foreach (var target in targets)
            {
                if (TryEndProcess(target.Pid, sessionId, owner, target.Name, target.StartFileTime, target.ExePath, out string error))
                    ended++;
                else
                    errors.Add(target.Name + " (" + target.Pid + ")：" + error);
            }
            return (ended, errors);
        }

        private static bool TryEndProcess(int pid, int sessionId, string owner, string name, long started,
            string? expectedPath, out string error)
        {
            error = "进程身份已变更或无法核对";
            if (pid <= 4 || started <= 0 || IsProtected(name)) return false;
            using var process = OpenProcess(ProcessQueryLimitedInformation | ProcessTerminate, false, pid);
            if (!TryReadIdentity(process, pid, sessionId, owner, name, out long actualStart, out string path)
                || actualStart != started
                || (expectedPath != null && !string.Equals(path, expectedPath, StringComparison.OrdinalIgnoreCase))) return false;
            if (TerminateProcess(process, 1)) return true;
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        private static bool TryReadIdentity(SafeProcessHandle process, int pid, int sessionId, string owner,
            string name, out long started, out string path)
        {
            started = 0;
            path = "";
            if (process.IsInvalid || !ProcessIdToSessionId(pid, out int actualSession) || actualSession != sessionId
                || !GetProcessTimes(process, out started, out _, out _, out _) || started <= 0
                || !OwnsProcess(process, owner)) return false;

            var buffer = new StringBuilder(1024);
            int length = buffer.Capacity;
            if (!QueryFullProcessImageName(process, 0, buffer, ref length)) return false;
            path = buffer.ToString();
            return string.Equals(Normalize(Path.GetFileName(path)), Normalize(name), StringComparison.OrdinalIgnoreCase);
        }

        private static bool OwnsProcess(SafeProcessHandle process, string owner)
        {
            if (!OpenProcessToken(process, 8, out var token)) return false;
            using (token)
            {
                try { return string.Equals(BrokerSecurity.ReadUserSid(token.DangerousGetHandle()), owner, StringComparison.Ordinal); }
                catch { return false; }
            }
        }

        private const uint ProcessTerminate = 0x0001;
        private const uint ProcessQueryLimitedInformation = 0x1000;

        [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(int processId, out int sessionId);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    }
}
