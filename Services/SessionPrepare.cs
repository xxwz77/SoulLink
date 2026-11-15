using System;
using System.Linq;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>
    /// 让目标会话「可被 RDP 复用」。
    ///
    /// 实测规律（见 HANDOFF 10.3）：
    ///   目标 Active → RDP 连过去会**新建空会话**（挂机进程还在原会话里，画面却是空的）
    ///   目标 Disc   → RDP 连过去**复用原会话**，画面就是真实挂机桌面
    ///
    /// 所以内嵌 RDP 之前，必须先把目标会话踢成 Disc。
    /// </summary>
    public static class SessionPrepare
    {
        /// <summary>
        /// 把指定会话断开（等价于关掉远程窗口但不注销）。
        ///
        /// ★ 必须只在会话 **Active** 时调用。
        ///   实测：对一个已经 Disc 的会话再执行 tsdiscon，会把会话**直接注销**，
        ///   之后再连 RDP 只会看到「你已从此远程计算机注销」，挂机进程全丢。
        /// </summary>
        public static async Task<(bool ok, string message)> DisconnectAsync(int sessionId)
        {
            if (sessionId <= 0) return (false, "无效的会话 ID：" + sessionId);

            // 已经断开就不要再碰它，否则会注销
            var now = await SessionService.QueryAsync().ConfigureAwait(false);
            var cur = now.Find(s => s.Id == sessionId);
            if (cur != null && cur.IsDisconnected)
                return (true, "会话 " + sessionId + " 本来就是断开状态，无需处理");

            string output = await SessionService.RunAsync("tsdiscon.exe", sessionId.ToString(), 8000)
                .ConfigureAwait(false);
            string trimmed = (output ?? "").Trim();

            if (trimmed.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
                return (false, "会话 " + sessionId + " 不存在");

            // 断开后回读一次，确认它真的变成了 Disc
            await Task.Delay(300).ConfigureAwait(false);
            var sessions = await SessionService.QueryAsync().ConfigureAwait(false);
            var target = sessions.Find(s => s.Id == sessionId);

            if (target == null) return (false, "会话 " + sessionId + " 已消失");
            if (target.IsDisconnected) return (true, "会话 " + sessionId + " 已断开，可安全接管");
            if (target.IsActive) return (true, "会话 " + sessionId + " 仍为活动（可能已被接管）");

            return (!string.IsNullOrEmpty(trimmed), trimmed.Length > 0 ? trimmed : "已请求断开");
        }

        /// <summary>
        /// 注销会话（关掉里面所有程序）。**不可恢复**，调用前必须二次确认。
        /// </summary>
        public static async Task<(bool ok, string message)> LogoffAsync(int sessionId)
        {
            if (sessionId <= 0) return (false, "无效的会话 ID");

            var sessions = await SessionService.QueryAsync().ConfigureAwait(false);
            var target = sessions.Find(s => s.Id == sessionId);
            if (target == null) return (true, "会话 " + sessionId + " 已不存在");

            string output = await SessionService.RunAsync("logoff.exe", sessionId.ToString(), 10000)
                .ConfigureAwait(false);

            await Task.Delay(1200).ConfigureAwait(false);
            var after = await SessionService.QueryAsync().ConfigureAwait(false);
            bool gone = !after.Any(s => s.Id == sessionId);

            return gone
                ? (true, "会话 " + sessionId + " 已注销")
                : (false, "注销命令已执行，但会话 " + sessionId + " 仍在（可能被其他进程占用）"
                          + (string.IsNullOrWhiteSpace(output) ? "" : "：" + output.Trim()));
        }

        /// <summary>
        /// 为 RDP 接管做准备：目标已断开就直接用；还连着就先踢下线。
        /// </summary>
        public static async Task<(bool ok, string message, int sessionId)> PrepareForRdpAsync(string userName)
        {
            var sessions = await SessionService.QueryAsync().ConfigureAwait(false);
            var s = SessionService.Find(sessions, userName);
            if (s == null) return (false, "找不到用户 " + userName + " 的登录会话", 0);

            if (s.IsDisconnected)
                return (true, "会话 " + s.Id + " 已断开，直接复用", s.Id);

            var (ok, msg) = await DisconnectAsync(s.Id).ConfigureAwait(false);
            return (ok, msg, s.Id);
        }
    }
}
