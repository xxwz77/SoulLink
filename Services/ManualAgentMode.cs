using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>
    /// 手动代理模式。
    ///
    /// ★ 设计目标就是"简单"：
    ///   开关打开 → 完全不自动投送，只在本机准备管道，等用户自己启动代理。
    ///   开关关闭 → 撤销等待，恢复自动投送。
    ///
    /// 为什么不再用"检测有没有手动代理"：
    ///   手动代理与 Broker 自动代理同名同进程（SoulLink.Agent.exe），
    ///   靠进程名/命令行反推既不可靠、也不可解释。开关是用户明确声明的状态，
    ///   程序只需照着执行，不需要猜。
    /// </summary>
    public static class ManualAgentMode
    {
        /// <summary>正在等待手动代理的通道（会话号 → 通道）。</summary>
        private static readonly Dictionary<int, RelayClient> Waiting = new Dictionary<int, RelayClient>();
        private static readonly Lock Gate = new();
        private static CancellationTokenSource? _cts;

        /// <summary>
        /// 手动代理接入后的事件（会话号, 通道）。
        /// 由界面层订阅并调用 RdpConsole.AttachManualRelay 完成挂接：
        /// 只有挂接之后宿主才会把热键配置推给代理、并订阅 HotkeyPressed，
        /// 否则会表现为"显示接入成功但侧键没反应"。
        /// </summary>
        public static event Action<int, RelayClient>? Attached;

        public static bool Enabled => App.Config.Settings.ManualAgentMode;

        /// <summary>当前正在等待手动代理的会话号。</summary>
        public static List<int> WaitingSessions()
        {
            lock (Gate) return Waiting.Keys.OrderBy(x => x).ToList();
        }

        public static bool IsWaiting(int sessionId)
        {
            lock (Gate) return Waiting.ContainsKey(sessionId);
        }

        /// <summary>
        /// 打开或关闭手动模式。返回给用户看的说明。
        /// </summary>
        public static async Task<string> SetEnabledAsync(bool enabled)
        {
            App.Config.Settings.ManualAgentMode = enabled;
            try { ConfigStore.Save(App.Config); } catch { }

            if (!enabled)
            {
                await StopAllAsync().ConfigureAwait(false);
                return "已关闭手动代理模式，自动投送恢复。";
            }

            // 打开：清理自动代理 + 为所有在线账号建立等待
            int cleaned = CleanAutoAgents();
            var started = await StartForOnlineAccountsAsync().ConfigureAwait(false);
            string detail = started.Count == 0
                ? "当前没有在线账号；账号上线后会自动开始等待。"
                : "已为会话 " + string.Join("、", started) + " 开始等待手动代理。";
            string clean = cleaned > 0 ? "已清理 " + cleaned + " 个自动代理。" : "";
            return "已开启手动代理模式。" + clean + detail
                 + "\n\n现在请在目标账号的桌面里运行 SoulLink.Agent.exe。";
        }

        /// <summary>
        /// 为所有在线且已配置的账号建立等待。
        /// 返回成功的会话号列表。
        /// </summary>
        public static async Task<List<int>> StartForOnlineAccountsAsync()
        {
            var started = new List<int>();
            if (!Enabled) return started;

            List<SessionInfo> sessions;
            try { sessions = await SessionService.QueryAsync().ConfigureAwait(false); }
            catch { return started; }

            // ★ 必须走 AccountScope：它负责"仅管理挂机账号"策略（排除主用户）。
            //   直接遍历 App.Config.Accounts 会给主用户（自己的桌面会话）
            //   也建一条永远不会有人连接的管道，既浪费又误导排查。
            foreach (var acc in AccountScope.Accounts())
            {
                var s = SessionService.Find(sessions, acc.UserName);
                if (s == null || s.Id <= 0) continue;
                if (string.Equals(s.State, "Disc", StringComparison.OrdinalIgnoreCase)) continue;
                if (await StartWaitingAsync(s.Id, acc.UserName, acc.Id).ConfigureAwait(false)) started.Add(s.Id);
            }
            return started;
        }

        /// <summary>为指定会话建立"等待手动代理"，不投送任何进程。</summary>
        public static async Task<bool> StartWaitingAsync(int sessionId, string userName, string? accountId = null)
        {
            if (!Enabled || sessionId <= 0) return false;

            // 主用户不在"仅管理挂机账号"允许范围内时，不为其建管道。
            if (!AccountScope.Allows(userName))
            {
                LogBus.Info("手动代理模式：跳过主用户", "会话 " + sessionId + " · " + userName);
                return false;
            }

            RelayClient relay;
            lock (Gate)
            {
                if (Waiting.ContainsKey(sessionId)) return false;   // 已在等待
                relay = new RelayClient(userName, sessionId, accountId);
                Waiting[sessionId] = relay;
                _cts ??= new CancellationTokenSource();
            }

            LogBus.Info("手动代理模式：开始等待", "会话 " + sessionId + " · 管道 " + relay.PipeName
                + " · 请在目标会话运行 SoulLink.Agent.exe");

            // 后台等待，不阻塞调用方
            var token = _cts!.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    var (ok, message) = await relay.ListenAsync(timeoutMs: 600000).ConfigureAwait(false);
                    if (ok)
                    {
                        RelayClient.Register(sessionId, relay);
                        LogBus.Ok("手动代理已接入", "会话 " + sessionId + " · " + message);
                        try { Attached?.Invoke(sessionId, relay); }
                        catch (Exception ex) { LogBus.Warn("挂接手动代理失败", ex.Message); }
                    }
                    else
                    {
                        LogBus.Warn("手动代理等待结束", "会话 " + sessionId + " · " + message);
                    }
                }
                catch (Exception ex) { LogBus.Warn("手动代理等待异常", "会话 " + sessionId + " · " + ex.Message); }
            }, token);

            return true;
        }

        /// <summary>停止等待并释放该会话的通道。</summary>
        public static void StopWaiting(int sessionId)
        {
            RelayClient? relay = null;
            lock (Gate)
            {
                if (Waiting.TryGetValue(sessionId, out relay)) Waiting.Remove(sessionId);
            }
            if (relay == null) return;
            try { RelayClient.Unregister(sessionId); } catch { }
            try { relay.Dispose(); } catch { }
            LogBus.Info("手动代理模式：停止等待", "会话 " + sessionId);
        }

        public static Task StopAllAsync()
        {
            // ★ 顺序很重要：**先 Cancel 再 Dispose 通道**。
            //
            //   用户实测日志里出现大量
            //     「手动代理等待异常：The CancellationTokenSource has been disposed」
            //   原因：旧实现先 StopWaiting（内部 relay.Dispose()），
            //   而后台 ListenAsync 还在 await 中，两个线程同时碰同一个 CTS。
            //
            //   现在的顺序：
            //     ① 先把 CTS 取消 + 取走置空（让所有等待任务立刻退出）
            //     ② 再逐个 Dispose 通道
            //   取消是线程安全的，Dispose 只碰通道不碰 CTS。
            CancellationTokenSource? cts;
            lock (Gate)
            {
                cts = _cts;
                _cts = null;
            }
            try { cts?.Cancel(); } catch { }   // 先取消，唤醒所有 await

            List<int> ids;
            lock (Gate) ids = Waiting.Keys.ToList();
            foreach (var id in ids) StopWaiting(id);

            // Cancel 之后仍有极短的收尾窗口，Dispose 放到后台做，避免和正在退出的任务抢锁。
            if (cts != null)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(300).ConfigureAwait(false);
                    try { cts.Dispose(); } catch { }
                });
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 清理自动投送创建的代理进程。
        ///
        /// 只处理 SoulLink.proxy（计划任务通道的代理副本）：
        /// 这个文件名只有自动投送会用，绝不会和用户手动启动的副本混淆。
        ///
        /// ★ 不按进程名去杀 SoulLink.Agent：那个名字同时被 Broker 自动代理
        ///   和用户手动副本使用，杀错会把用户刚启动的代理也干掉。
        ///   Broker 通道的代理由它自己的服务管理，切到手动模式后不再新投即可。
        /// </summary>
        public static int CleanAutoAgents()
        {
            int killed = 0;
            string identity = AppIdentity.Name;
            string proxyName = identity + ".proxy";

            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    string name = proc.ProcessName;
                    bool isProxy = string.Equals(name, proxyName, StringComparison.OrdinalIgnoreCase);
                    if (!isProxy) continue;

                    proc.Kill();
                    killed++;
                    LogBus.Info("手动代理模式：清理自动代理", "PID " + proc.Id + " · " + name);
                }
                catch { }
                finally { proc.Dispose(); }
            }
            return killed;
        }
    }
}
