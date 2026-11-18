using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>把 IPC / 命令行请求翻译成动作并执行（服务端执行，调用方无感）。</summary>
    public static class RequestHandler
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        public static async Task<IpcResponse> HandleAsync(InvokeRequest req, AppConfig shared)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await HandleCoreAsync(req, shared).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogBus.Error("执行异常：" + ex.Message);
                return new IpcResponse { Ok = false, Message = ex.Message };
            }
            finally
            {
                Gate.Release();
            }
        }

        private static async Task<IpcResponse> HandleCoreAsync(InvokeRequest req, AppConfig cfg)
        {
            if (req.Show && string.IsNullOrEmpty(req.Action) && string.IsNullOrEmpty(req.Process)
                && string.IsNullOrEmpty(req.User) && string.IsNullOrEmpty(req.Account))
                return new IpcResponse { Ok = true, Message = "已唤起界面。" };

            // 1) 优先按保存好的动作执行
            string? name = req.Action;
            ActionItem? saved = null;
            if (!string.IsNullOrWhiteSpace(name))
                saved = cfg.Actions.FirstOrDefault(a =>
                    string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a.Id, name, StringComparison.OrdinalIgnoreCase));

            if (saved != null)
            {
                LogBus.Write(LogLevel.Run, "命令执行动作「" + saved.Name + "」");
                var r = await ActionService.ExecuteAsync(saved, cfg).ConfigureAwait(false);
                return ToResponse(r, new List<string> { "动作：" + saved.Name });
            }

            // --action 也可能是 open / close / restart 这类类型关键字（兼容写法）
            if (!string.IsNullOrWhiteSpace(name) && IsKindKeyword(name!))
            {
                req.Kind = name!.ToLowerInvariant() switch
                {
                    "close" or "stop" or "kill" => ActionKind.Close,
                    "restart" => ActionKind.Restart,
                    _ => ActionKind.Launch
                };
                name = null;
            }

            // 指定了 --action 但没匹配到：直接报清楚，不要继续按临时目标猜
            if (!string.IsNullOrWhiteSpace(name) && string.IsNullOrEmpty(req.Process))
            {
                var names = cfg.Actions.Select(a => a.Name).Take(12).ToList();
                return new IpcResponse
                {
                    Ok = false,
                    ExitCode = 2,
                    Message = "找不到名叫「" + name + "」的动作。" +
                              (names.Count > 0
                                  ? "现有动作：" + string.Join("、", names) + "（名字里带空格时要用英文引号包起来）"
                                  : "请先在界面里新建一个动作。")
                };
            }

            // 2) 账号名也可能是动作名
            if (string.IsNullOrEmpty(req.Process) && !string.IsNullOrWhiteSpace(req.Account))
            {
                var byAccountAction = cfg.Actions.FirstOrDefault(a =>
                    string.Equals(a.Name, req.Account, StringComparison.OrdinalIgnoreCase));
                if (byAccountAction != null)
                {
                    LogBus.Write(LogLevel.Run, "命令执行动作「" + byAccountAction.Name + "」");
                    var r = await ActionService.ExecuteAsync(byAccountAction, cfg).ConfigureAwait(false);
                    return ToResponse(r, new List<string> { "动作：" + byAccountAction.Name });
                }
            }

            // 3) 临时目标：--user / --account / 位置参数
            //   （密码已废弃：投送走 SYSTEM 计划任务 + WTSQueryUserToken，不需要凭据）
            string userName = "";
            Account? account = null;

            if (!string.IsNullOrWhiteSpace(req.Account))
                account = cfg.Accounts.FirstOrDefault(a =>
                    string.Equals(a.Id, req.Account, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a.UserName, SessionService.BareName(req.Account), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a.UserName, req.Account, StringComparison.OrdinalIgnoreCase));

            if (account != null)
            {
                userName = account.UserName;
            }
            else if (!string.IsNullOrWhiteSpace(req.User))
            {
                userName = req.User!;
            }
            else if (!string.IsNullOrWhiteSpace(req.Account))
            {
                userName = req.Account!;
                account = cfg.Accounts.FirstOrDefault(a =>
                    string.Equals(a.UserName, SessionService.BareName(userName), StringComparison.OrdinalIgnoreCase));
            }

            if (string.IsNullOrWhiteSpace(userName))
                return new IpcResponse { Ok = false, Message = "缺少目标账号：请用 --user <用户名> 或指定已保存的动作名。" };

            if (string.IsNullOrWhiteSpace(req.Process))
                return new IpcResponse { Ok = false, Message = "缺少程序路径：请用 --exe <完整路径> 或在界面里保存一个快捷动作。" };

            int? sessionOverride = req.Session ?? (account != null && account.SessionId > 0 ? account.SessionId : (int?)null);

            if (!AccountScope.Allows(userName,cfg)) return new IpcResponse { Ok=false,Message="主用户已被全局挂机账号模式排除" };
            var targetAccount = account ?? new Account { UserName = userName };
            var sessions = SessionService.FilterUsers(await SessionService.QueryAsync().ConfigureAwait(false));
            SessionInfo? session = sessionOverride.HasValue
                ? sessions.FirstOrDefault(s => s.Id == sessionOverride.Value)
                : SessionService.Find(sessions, userName);

            if (session == null)
                return new IpcResponse
                {
                    Ok = false,
                    Message = "找不到用户 " + userName + " 的登录会话，请确认该用户已登录。",
                    ExitCode = 3
                };

            if (!await ActionService.EnsureToolAsync(cfg).ConfigureAwait(false))
                return new IpcResponse { Ok = false, Message = "需要管理员权限才能跨会话投送进程。" };

            var target = new ActionService.Target
            {
                Account = targetAccount,
                Session = session
            };

            if (req.Delay > 0)
            {
                LogBus.Info("延时 " + req.Delay + " 秒后执行");
                await Task.Delay(TimeSpan.FromSeconds(req.Delay)).ConfigureAwait(false);
            }

            ExecResult result;
            switch (req.Kind)
            {
                case ActionKind.Close:
                    result = await ActionService.CloseAsync(target, req.Process).ConfigureAwait(false);
                    break;
                case ActionKind.Restart:
                    result = await ActionService.RestartAsync(target, req.Process, req.Arguments).ConfigureAwait(false);
                    break;
                default:
                    result = await ActionService.LaunchAsync(target, req.Process, req.Arguments).ConfigureAwait(false);
                    break;
            }

            var detail = new List<string>
            {
                "用户：" + userName,
                "会话：" + session.Id + " (" + session.State + ")",
                "程序：" + req.Process
            };
            return ToResponse(result, detail);
        }

        private static bool IsKindKeyword(string s) => s.ToLowerInvariant() is
            "open" or "launch" or "start" or "close" or "stop" or "kill" or "restart";

        private static IpcResponse ToResponse(ExecResult r, List<string> detail)
        {
            LogBus.Write(r.Ok ? LogLevel.Ok : LogLevel.Error, r.Message);
            return new IpcResponse
            {
                Ok = r.Ok,
                Message = r.Message,
                Command = r.Command,
                ExitCode = r.Ok ? 0 : r.ExitCode == 0 ? 1 : r.ExitCode,
                ElapsedMs = r.ElapsedMs,
                AffectedCount = r.AffectedCount,
                Detail = detail
            };
        }
    }
}