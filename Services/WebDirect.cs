using System;

namespace SoulLink.Services
{
    /// <summary>
    /// 网页直连代理的契约（主程序与代理共用）。
    ///
    /// ★★ 为什么要有直连（2026-11-22）★★
    ///
    ///   原来所有网页流量都要经过主程序：
    ///     浏览器 → 主程序 → 命名管道 → 代理（取帧）→ 写盘 → 主程序读回 → 浏览器
    ///
    ///   视频拖动、实时画面、模拟按键全部压在主程序上。
    ///   用户要求：「网页端尽量隔离主客户端」——主程序只出数据，不出流量。
    ///
    ///   现在：
    ///     浏览器 → 代理 :port（取帧 / 读录像 / 注入按键）
    ///     主程序：只返回「哪个账号在哪个端口」这张映射表。
    ///
    /// ★★ 为什么端口按账号而不是会话号分配 ★★
    ///
    ///   会话号会变（注销重登就换号：实测 A2 从 17 变成过别的号），
    ///   按它分配端口会导致网页刷新后端口乱跳。
    ///   账号 Id 是配置里的稳定主键，端口一旦分配就不变。
    ///
    /// ★★ 一个账号一个端口，不做多账号汇聚 ★★
    ///
    ///   实测：Windows 端口是**独占**的，两个进程绑同一端口时第二个
    ///   会「静默成功但收不到任何连接」（served=0，第一个进程吃掉全部请求）。
    ///   这种失败极难排查，所以宁可多开端口也不共享。
    ///   好处：某个账号的代理挂掉只影响它自己，真正的故障隔离。
    /// </summary>
    public static class WebDirect
    {
        /// <summary>
        /// 账号 → 端口分配。
        ///
        /// 规则：<c>basePort + 账号在 Accounts 里的序号</c>。
        /// 以**配置顺序**为准（不是会话号、不是字典序），
        /// 所以只要账号列表不增删、不调序，端口就稳定。
        ///
        /// ⚠ 账号调序会让端口跟着变 —— 但那只影响浏览器刷新，
        ///   不会破坏任何已保存的数据（录像按用户名存目录，与端口无关）。
        /// </summary>
        public static int PortFor(int basePort, int accountIndex)
        {
            if (basePort <= 0) return 0;
            if (accountIndex < 0) return 0;
            // 上限保护：不越过 65535，也不要无限往外扩
            if (accountIndex > 60) return 0;
            int port = basePort + accountIndex + 1;
            return port <= 65535 ? port : 0;
        }

        /// <summary>代理侧校验的请求头：共享密钥。空 = 不校验（未设口令时）。</summary>
        public const string TokenHeader = "X-SoulLink-Token";

        /// <summary>
        /// 代理侧允许的路径前缀。**只放行这几个**，其余一律 404。
        /// 代理不是通用文件服务器，路径必须白名单化。
        /// </summary>
        public static bool IsAllowedPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path == "/live") return true;
            if (path == "/stream") return true;
            if (path == "/api/input") return true;
            if (path == "/api/info") return true;
            // 注意：**没有** /file/。历史录像是磁盘文件，主程序直接读，
            //       代理不该服务它（详见 Relay/AgentWebServer.cs 的类注释）。
            return false;
        }
    }
}
