using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>把「账号 + 动作」翻译成真正的跨会话投送调用（SYSTEM 通道）。</summary>
    public static class ActionService
    {
        public sealed class Target
        {
            public Account Account { get; set; } = new Account();
            public SessionInfo Session { get; set; } = new SessionInfo();
            public string Password { get; set; } = "";
        }

        /// <summary>解析账号对应的会话；失败的友好提示。</summary>
        public static Target? Resolve(Account account, IEnumerable<SessionInfo> sessions, AppConfig cfg, out string error)
        {
            error = "";
            var list = sessions.ToList();

            SessionInfo? s = account.SessionId > 0
                ? list.FirstOrDefault(x => x.Id == account.SessionId)
                : SessionService.Find(list, account.UserName);

            // 固定会话失效时退回按用户名查找
            if (s == null && account.SessionId > 0)
                s = SessionService.Find(list, account.UserName);

            if (s == null && cfg.Settings.AllowCurrentSessionFallback
                && string.Equals(SessionService.BareName(account.UserName), SessionService.CurrentUser,
                    StringComparison.OrdinalIgnoreCase))
            {
                int sid = SessionService.CurrentSessionId;
                s = list.FirstOrDefault(x => x.Id == sid) ?? new SessionInfo
                {
                    UserName = account.UserName, Id = sid, State = "Active"
                };
            }

            if (s == null)
            {
                error = "找不到用户 " + account.UserName + " 的登录会话，请确认该用户已登录。";
                return null;
            }

            return new Target { Account = account, Session = s, Password = ConfigStore.GetPassword(account) };
        }

        /// <summary>检查投送通道是否就绪（需要管理员权限）。</summary>
        public static Task<bool> EnsureToolAsync(AppConfig cfg, CancellationToken ct = default)
        {
            SystemLauncher.EnablePrivileges();
            return Task.FromResult(SystemLauncher.IsElevated);
        }

        // ── 打开 ──────────────────────────────────────────────────────────────

        public static async Task<ExecResult> LaunchAsync(Target t, string exePath, string? arguments, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(exePath))
                return ExecResult.Fail("没有配置要启动的程序路径。");

            string exe = exePath.Trim().Trim('"');
            if (!File.Exists(exe))
                return ExecResult.Fail("目标程序不存在：" + exe);

            // 走 SYSTEM 通道（schtasks /ru SYSTEM → CreateProcessAsUser），不依赖任何外部工具
            var launch = await SystemLauncher.LaunchAsync(
                t.Session.Id, exe, arguments ?? "",
                workingDir: Path.GetDirectoryName(exe) ?? "").ConfigureAwait(false);

            var result = new ExecResult
            {
                Command = "SYSTEM → session " + t.Session.Id + " : " + Path.GetFileName(exe)
                          + (string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments),
                ExitCode = launch.Ok ? 0 : 1,
                ElapsedMs = launch.ElapsedMs
            };

            if (!launch.Ok)
            {
                result.Ok = false;
                result.Message = launch.Message;
                return result;
            }

            await Task.Delay(600, ct).ConfigureAwait(false);
            var running = ProcessService.MatchByExe(ProcessService.List(t.Session.Id, true), exe);

            result.Ok = true;
            result.AffectedCount = running.Count;
            result.Message = "已在 " + t.Account.UserName + " 的桌面（会话 " + t.Session.Id + "）打开 "
                             + Path.GetFileName(exe) + " · PID " + launch.Pid
                             + (running.Count > 0 ? "" : "（进程已退出或已改名，请确认）");
            return result;
        }

        // ── 关闭 ──────────────────────────────────────────────────────────────

        public static async Task<ExecResult> CloseAsync(Target t, string exePath, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(exePath))
                return ExecResult.Fail("没有配置要关闭的程序路径。");

            string exe = exePath.Trim().Trim('"');
            var all = ProcessService.List(t.Session.Id, true);
            var matches = ProcessService.MatchByExe(all, exe);

            if (matches.Count == 0)
                return ExecResult.Success("会话 " + t.Session.Id + " 中没有运行中的 " + Path.GetFileName(exe) + "，无需关闭。");

            int killed = 0;
            var errors = new List<string>();
            foreach (var m in matches)
            {
                var r = await ProcessService.KillAsync(m, ct).ConfigureAwait(false);
                if (r.Ok) killed++;
                else errors.Add(r.Message);
            }

            var res = new ExecResult
            {
                Ok = killed > 0,
                AffectedCount = killed,
                Command = "taskkill /f  " + Path.GetFileName(exe) + "  @session " + t.Session.Id
            };
            res.Message = killed > 0
                ? "已在 " + t.Account.UserName + " 的桌面关闭 " + Path.GetFileName(exe) +
                  (matches.Count > killed ? "（" + killed + "/" + matches.Count + "）" : "（" + killed + " 个进程）")
                : "关闭失败：" + (errors.FirstOrDefault() ?? "未知原因");
            return res;
        }

        // ── 重启 ──────────────────────────────────────────────────────────────

        public static async Task<ExecResult> RestartAsync(Target t, string exePath, string? arguments, CancellationToken ct = default)
        {
            var close = await CloseAsync(t, exePath, ct).ConfigureAwait(false);
            await Task.Delay(900, ct).ConfigureAwait(false);
            var open = await LaunchAsync(t, exePath, arguments, ct).ConfigureAwait(false);
            return new ExecResult
            {
                Ok = open.Ok,
                Message = "重启完成 · " + open.Message,
                Command = open.Command,
                ExitCode = open.ExitCode,
                ElapsedMs = close.ElapsedMs + open.ElapsedMs,
                AffectedCount = open.AffectedCount
            };
        }

        /// <summary>执行一条快捷动作。</summary>
        public static async Task<ExecResult> ExecuteAsync(ActionItem action, AppConfig cfg, CancellationToken ct = default)
        {
            var account = cfg.Accounts.FirstOrDefault(a => a.Id == action.AccountId);
            if (account == null) return ExecResult.Fail("动作「" + action.Name + "」绑定的账号不存在。");

            if (action.DelaySeconds > 0)
            {
                LogBus.Info("等待 " + action.DelaySeconds + " 秒后执行「" + action.Name + "」");
                await Task.Delay(TimeSpan.FromSeconds(action.DelaySeconds), ct).ConfigureAwait(false);
            }

            var sessions = SessionService.FilterUsers(await SessionService.QueryAsync().ConfigureAwait(false));
            var target = Resolve(account, sessions, cfg, out string error);
            if (target == null) return ExecResult.Fail(error);

            if (!await EnsureToolAsync(cfg, ct).ConfigureAwait(false))
                return ExecResult.Fail("需要管理员权限：请右键 SoulLink.exe →「以管理员身份运行」。");

            switch (action.Kind)
            {
                case ActionKind.Close:
                    return await CloseAsync(target, action.ExePath, ct).ConfigureAwait(false);
                case ActionKind.Restart:
                    return await RestartAsync(target, action.ExePath, action.Arguments, ct).ConfigureAwait(false);
                default:
                    return await LaunchAsync(target, action.ExePath, action.Arguments, ct).ConfigureAwait(false);
            }
        }

        // ── 辅助 ──────────────────────────────────────────────────────────────

        /// <summary>日志 / 界面里一律隐藏密码。</summary>
        public static string MaskPassword(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return "";
            return System.Text.RegularExpressions.Regex.Replace(commandLine,
                "(-p\\s+)(\"(?:[^\"]*)\"|\\S+)", "$1***");
        }

        /// <summary>从输出里解析出新进程 PID；解析不到返回 -1。</summary>
        public static int ParseStartedPid(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            var m = System.Text.RegularExpressions.Regex.Match(text,
                @"(?:process ID|进程\s*ID|PID)\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int pid) && pid > 0) return pid;
            return -1;
        }



        public static string? TranslateText(string text)
        {
            string msg = (text ?? "").ToLowerInvariant();
            if (msg.Contains("access is denied") || msg.Contains("拒绝访问"))
                return "访问被拒绝：请确认已用「以管理员身份运行」启动 SoulLink，且账号有登录权限。";
            if (msg.Contains("logon failure") || msg.Contains("登录失败") || msg.Contains("1326"))
                return "用户名或密码不正确（登录失败 1326）。";
            if (msg.Contains("could not start pssexesvc") || msg.Contains("无法启动 psexesvc"))
                return "投送通道启动失败：请检查是否以管理员身份运行，以及杀软是否拦截了计划任务。";
            if (msg.Contains("could not start") || msg.Contains("无法启动") || msg.Contains("启动失败"))
            {
                string line = ProcessService.FirstLine(text ?? "");
                return string.IsNullOrEmpty(line) ? "启动失败。" : "启动失败：" + line;
            }
            if (msg.Contains("no process is on the other end"))
                return "目标会话已断开或不可交互。";
            if (msg.Contains("not found") || msg.Contains("找不到"))
                return "目标程序路径不存在，请检查程序路径（目标端视角）。";
            return null;
        }
    }
}