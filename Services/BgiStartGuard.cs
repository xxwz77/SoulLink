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
            public Func<int, string, string> Hotkey { get; init; } = (sessionId, exePath) => BgiTaskPreparer.ReadCancelHotkey(exePath);
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
                    // 用户没填停止快捷键时直接关闭，不发送、不等待脚本停止。
                    result.StopMethod = "kill";
                    if (!await p.CloseProcesses(sessionId, ct).ConfigureAwait(false)
                        || !await p.ConfirmExited(sessionId, ct).ConfigureAwait(false))
                    { result.Ok=false;result.Message="BGI 未设置停止快捷键，直接关闭后仍未全部退出";return result; }
                    result.Ok=true;result.Message="BGI 未设置停止快捷键，已直接关闭并确认退出";return result;
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