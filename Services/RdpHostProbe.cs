using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>
    /// 找出「连过去能落到目标用户会话」的 RDP 地址。
    ///
    /// 为什么需要探测（实测结论，见 HANDOFF 10.6）：
    ///   本机所有环回 RDP 客户端在服务端看来**都是 127.0.0.1**（WTSClientAddress 全一样），
    ///   所以「用会话号推 127.0.0.N」并不成立：
    ///     - 127.0.0.3 实测能连上并复用 a2 的会话；
    ///     - 127.0.0.8 同样能建立 TCP，但会被要求输入凭据 / 停住。
    ///   因此地址必须**实测**，不能推算。
    /// </summary>
    public static class RdpHostProbe
    {
        private static readonly Dictionary<string, string> Cache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        /// <summary>候选地址：优先用户常用/已配置的，再按会话号。</summary>
        private static List<string> Candidates(int sessionId, string? preferred)
        {
            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(preferred)) list.Add(preferred!.Trim());
            if (sessionId > 0 && sessionId < 255) list.Add("127.0.0." + sessionId);
            for (int i = 1; i <= 10; i++)
            {
                string ip = "127.0.0." + i;
                if (!list.Contains(ip)) list.Add(ip);
            }
            return list;
        }

        /// <summary>
        /// 粗探测：挑端口通、能开始 RDP 握手的地址。
        /// 注意这只是**排除明显不通的**，真正能否落到目标会话要靠 <see cref="Remember"/> 回写。
        /// </summary>
        public static async Task<string?> ProbeAsync(int sessionId, string? preferred, CancellationToken ct = default)
        {
            string key = "s" + sessionId;
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Cache.TryGetValue(key, out var cached)) return cached;

                foreach (var ip in Candidates(sessionId, preferred))
                {
                    if (await CanConnectAsync(ip, 3389, 700, ct).ConfigureAwait(false))
                    {
                        Cache[key] = ip;
                        return ip;
                    }
                }
                return null;
            }
            finally { Gate.Release(); }
        }

        /// <summary>连接成功后回写：证明这个地址对这个会话是可用的。</summary>
        public static void Remember(int sessionId, string host)
        {
            if (sessionId <= 0 || string.IsNullOrWhiteSpace(host)) return;
            Cache["s" + sessionId] = host.Trim();
        }

        /// <summary>把某个地址标记为对某会话不可用（下次探测跳过）。</summary>
        public static void Forget(int sessionId, string host)
        {
            if (sessionId <= 0) return;
            string key = "s" + sessionId;
            if (Cache.TryGetValue(key, out var v) &&
                string.Equals(v, host, StringComparison.OrdinalIgnoreCase))
                Cache.Remove(key);
        }

        private static async Task<bool> CanConnectAsync(string ip, int port, int timeoutMs, CancellationToken ct)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                var task = client.ConnectAsync(ip, port);
                var done = await Task.WhenAny(task, Task.Delay(timeoutMs, ct)).ConfigureAwait(false);
                if (done != task) return false;
                if (task.IsFaulted) return false;
                return client.Connected;
            }
            catch { return false; }
        }
    }
}
