using System;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>
    /// 全局 BGI 启动守卫：所有启动入口必须走这里。
    ///
    /// ★★ 3.1.11 按用户口述的启动逻辑重写 ★★
    ///
    ///   用户原话（本次要求）：
    ///     「bgi没有快捷键，直接杀进程。
    ///       bgi按下停止当前脚本快捷键，然后日志会出现提示按下了这个快捷键，
    ///       然后就可以杀进程，进程消失后就可以启动
    ///       这才是正确启动逻辑，如果用户选择了同时关闭原神，
    ///       需要关闭同会话的原神」
    ///     「（有快捷键但没等到日志）直接杀进程与游戏」
    ///
    ///   于是本方法的语义是：
    ///     ① BGI 没在跑                          → 直接放行
    ///     ② 读不到「停止当前脚本」快捷键          → 直接杀进程（不再拒绝）
    ///     ③ 有快捷键：投到真实 pid → 等日志出现停止证据（10 秒）
    ///          ├─ 等到 → 杀进程
    ///          └─ 没等到/超时 → **仍然杀进程与游戏**，但如实说明"未等到停止确认"
    ///     ④ 杀进程后必须确认全部退出（15 秒）    → 没退干净才中止启动
    ///
    ///   ★ 与 2.17.47 的差异：那版"读不到快捷键就拒绝启动"，
    ///     与用户现在的要求正好相反（无快捷键应降级直接杀）。已按新要求改回。
    ///   ★ 唯一仍然中止启动的情形是「进程没退干净」——
    ///     那时覆盖/启动会撞文件占用，必须挡住。
    ///
    ///   ★ 顺序固定为「停止当前脚本 → 关闭 BGI → 确认退出 → 启动」，
    ///     不再把"停止当前脚本"快捷键当成"关闭进程"快捷键。
    /// </summary>
    public static class BgiStartGuard
    {
        public sealed class Ports
        {
            public Func<int, string, bool> IsRunning { get; init; } = (sessionId, exePath) => BgiTaskPreparer.HasRunningProcess(sessionId, exePath);
            /// <summary>
            /// 取「停止当前脚本」快捷键。
            ///
            /// ★★ 2.17.47：改成**直接从数据中心**读。★★
            ///   原来走 BgiTaskPreparer.ReadCancelHotkey(exePath) —— 内部还要
            ///   「遍历 Accounts 反查账号 → 再从 exePath 推 User\config.json → 正则抓 F12」，
            ///   而数据中心（BgiDataCenter）本来就持有每个账号的 CancelTaskHotkey，
            ///   界面上也是它在显示 F12。绕一圈既有两套解析、又容易因账号未绑定而读到空。
            ///   现在只有一个真相来源。
            /// </summary>
            public Func<int, string, string> Hotkey { get; init; } = (sessionId, exePath) =>
                BgiTaskPreparer.ReadCancelHotkeyFor(sessionId, exePath);

            /// <summary>
            /// 取该会话中目标 BGI 的**进程 Id**（找不到返回 0）。
            /// ★★ 3.1.11 新增：PostMessage 兜底必须用真实 pid。★★
            ///   原来是 StopScript(sessionId, hotkey)，把会话号当成了进程号，
            ///   FindMainWindow 永远返回 IntPtr.Zero → 快捷键静默丢弃。
            /// </summary>
            public Func<int, string, int> FindPid { get; init; } = (sessionId, exePath) =>
                BgiTaskPreparer.FindBgiPid(sessionId, exePath);

            /// <summary>
            /// 发送「停止当前脚本」快捷键 —— **投送通道优先**，PostMessage 兜底。
            /// 参数：(sessionId, pid, hotkey, ct) → (是否发出, 走的哪条通道, 说明)
            /// </summary>
            public Func<int, int, string, CancellationToken, Task<(bool Sent, string Via, string Note)>> StopScript { get; init; }
                = (sid, pid, hk, ct) => BgiTaskPreparer.SendStopHotkeyAsync(sid, pid, hk, ct);

            /// <summary>等「脚本已停」证据；参数带 hotkey —— 日志出现该按键名即判定按下。</summary>
            public Func<int, string, CancellationToken, Task<bool>> ScriptStopped { get; init; }
                = (sid, hk, ct) => BgiTaskPreparer.WaitForScriptStoppedAsync(sid, TimeSpan.FromSeconds(10), ct, hk);

            public Func<int, string, CancellationToken, Task<bool>> CloseProcesses { get; init; }
                = (sid, exe, ct) => BgiTaskPreparer.CloseProcessesAsync(sid, exe, ct);

            public Func<int, string, CancellationToken, Task<bool>> ConfirmExited { get; init; }
                = (sid, exe, ct) => BgiTaskPreparer.ConfirmAllExitedAsync(sid, exe, TimeSpan.FromSeconds(15), ct);
        }

        internal static void MarkStopBaseline(DateTime value) => BgiTaskPreparer._stopBaselineUtc = value;

        public static async Task<BgiTaskPreparer.Result> BeforeStartAsync(int sessionId, string exePath,
            CancellationToken ct = default, Ports? ports = null)
        {
            var p = ports ?? new Ports();
            var result = new BgiTaskPreparer.Result();
            try
            {
                // ① BGI 没在跑：无需清场
                if (!p.IsRunning(sessionId, exePath))
                { result.StopMethod = "none"; result.Ok = true; result.Message = "BGI 未运行，无需停止"; return result; }

                string hotkey = p.Hotkey(sessionId, exePath) ?? "";
                bool stopConfirmed = false;
                string stopNote;

                if (hotkey.Length == 0)
                {
                    // ② 没有快捷键 → 按用户要求**直接杀进程**（不等待、不拒绝）
                    stopNote = "BGI 未设置「停止当前脚本」快捷键";
                    result.StopMethod = "kill";
                }
                else
                {
                    int pid = p.FindPid(sessionId, exePath);

                    // ③ 发快捷键（投送通道优先）→ 等日志出现「按下了这个快捷键」
                    BgiTaskPreparer._stopBaselineUtc = DateTime.UtcNow;
                    var sent = await p.StopScript(sessionId, pid, hotkey, ct).ConfigureAwait(false);
                    if (!sent.Sent)
                    {
                        // 两条通道都发不出去：如实记录，然后按用户要求直接杀进程
                        stopNote = sent.Note;
                        result.StopMethod = "none";
                    }
                    else
                    {
                        stopConfirmed = await p.ScriptStopped(sessionId, hotkey, ct).ConfigureAwait(false);
                        stopNote = sent.Note + (stopConfirmed
                            ? "，已确认脚本停止"
                            : "，但 10 秒内未在日志中看到停止确认");
                        result.StopMethod = sent.Via;
                    }
                }

                // ④ 杀进程（无论脚本是否确认停止都继续 —— 用户明确要求"直接杀进程与游戏"）
                if (!await p.CloseProcesses(sessionId, exePath, ct).ConfigureAwait(false))
                {
                    result.Ok = false;
                    result.Message = stopNote + "；但关闭 BGI 进程失败，已阻止启动";
                    return result;
                }

                // ⑤ 确认全部退出：这一步**没退干净必须中止**（文件被占用时启动/覆盖会失败）
                if (!await p.ConfirmExited(sessionId, exePath, ct).ConfigureAwait(false))
                {
                    result.Ok = false;
                    result.Message = stopNote + "；但 BGI 进程尚未全部退出，已阻止启动";
                    return result;
                }

                result.Ok = true;
                result.Message = stopNote + "；已关闭 BGI 且进程已全部退出";

                // 未等到停止确认时，消息里明确标出来（用户在日志/界面上能看见原因）
                if (hotkey.Length > 0 && !stopConfirmed)
                    result.Message += "（注意：没有等到脚本停止日志，已按要求直接结束进程与游戏）";
                return result;
            }
            catch (OperationCanceledException) { result.Ok = false; result.Message = "操作已取消，未启动 BGI"; return result; }
            catch (Exception ex) { result.Ok = false; result.Message = "启动前置检查失败：" + ex.Message; return result; }
        }
    }
}
