using System;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>
    /// 全局 BGI 启动守卫：所有启动入口必须走这里。
    /// 顺序固定为“停止当前脚本 → 关闭 BGI → 确认退出 → 启动”，任一步失败不启动。
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
            public Action<int, string> StopScript { get; init; } = BgiTaskPreparer.SendHotkeyToSessionPublic;
            public Func<int, CancellationToken, Task<bool>> ScriptStopped { get; init; }
                = (sid, ct) => BgiTaskPreparer.WaitForScriptStoppedAsync(sid, TimeSpan.FromSeconds(10), ct);
            public Func<int, CancellationToken, Task<bool>> CloseProcesses { get; init; }
                = (sid, ct) => BgiTaskPreparer.CloseProcessesAsync(sid, ct);
            public Func<int, CancellationToken, Task<bool>> ConfirmExited { get; init; }
                = (sid, ct) => BgiTaskPreparer.ConfirmAllExitedAsync(sid, TimeSpan.FromSeconds(15), ct);
        }

        internal static void MarkStopBaseline(DateTime value) => BgiTaskPreparer._stopBaselineUtc = value;

        public static async Task<BgiTaskPreparer.Result> BeforeStartAsync(int sessionId, string exePath,
            CancellationToken ct = default, Ports? ports = null)
        {
            var p = ports ?? new Ports();
            var result = new BgiTaskPreparer.Result();
            try
            {
                if (!p.IsRunning(sessionId, exePath))
                { result.StopMethod="none"; result.Ok=true; result.Message="BGI 未运行，无需停止"; return result; }
                string hotkey = p.Hotkey(sessionId, exePath) ?? "";
                if (hotkey.Length == 0)
                {
                    // ★★ 2.17.47：不再降级为「直接杀进程」。★★
                    //
                    //   用户明确要求：必须先「停止当前脚本」，确认脚本停了**才能**关进程。
                    //   原来是读到空快捷键就直接杀 —— 结果是 BGI 里的脚本被硬中断，
                    //   可能留下半截状态（正在写的日志、正在做的动作、未收尾的流程）。
                    //
                    //   现在：读不到停止快捷键就**直接拒绝**，把原因抛给用户，
                    //   让用户去 BGI 里设一个（通常是 F12），而不是悄悄降级。
                    //   这符合用户原话：「这是执行逻辑，必须要去执行的步骤」。
                    result.StopMethod = "none";
                    result.Ok = false;
                    result.Message = "读不到 BGI 的「停止当前脚本」快捷键，已阻止关闭和启动。";
                    result.Message += "\n请在 BGI 设置里为「停止当前脚本」设置一个快捷键（如 F12）后重试。";
                    result.Message += "\n（BGI 配置文件：" + exePath + " 同级的 User\\config.json）";
                    return result;
                }
                BgiTaskPreparer._stopBaselineUtc = DateTime.UtcNow;
                p.StopScript(sessionId, hotkey);
                result.StopMethod = "hotkey";
                if (!await p.ScriptStopped(sessionId, ct).ConfigureAwait(false))
                {
                    result.Ok = false; result.Message = "停止当前脚本未确认，已阻止关闭和启动 BGI"; return result;
                }
                if (!await p.CloseProcesses(sessionId, ct).ConfigureAwait(false))
                {
                    result.Ok = false; result.Message = "关闭 BGI 进程失败，已阻止启动"; return result;
                }
                if (!await p.ConfirmExited(sessionId, ct).ConfigureAwait(false))
                {
                    result.Ok = false; result.Message = "BGI 进程尚未全部退出，已阻止启动"; return result;
                }
                result.Ok = true;
                result.Message = "已停止当前脚本并关闭 BGI，进程已全部退出";
                return result;
            }
            catch (OperationCanceledException) { result.Ok=false;result.Message="操作已取消，未启动 BGI";return result; }
            catch (Exception ex) { result.Ok=false;result.Message="启动前置检查失败："+ex.Message;return result; }
        }
    }
}