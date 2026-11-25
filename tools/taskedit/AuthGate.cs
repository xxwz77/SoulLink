using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SoulLink.TaskEdit
{
    /// <summary>
    /// 对外访问的鉴权闸门（设计文档 §17.2 定稿）。
    ///
    /// ── 规则 ───────────────────────────────────────────────────────────
    ///   · **仅回环**（127.0.0.1 / ::1 / localhost）免密 —— 本机用户点开就能用；
    ///   · **非回环一律强制令牌**，没有例外，也没有「口令为空就放行」的短路。
    ///     主程序里 IsAuthorized 的 <c>PasswordEnabled()==false → return true</c>
    ///     是既有安全隐患（暴露到局域网就等于裸奔），本服务**不复制该行为**。
    ///   · 令牌来源优先级：config.json 的 webPassword → 已生成的持久 webAuthToken
    ///     → 启动时**自动生成**一个新的并打印到控制台（同时尝试写回配置）。
    ///   · 会话：登录成功下发 1 年有效期的 Cookie（与主程序 IssueSession 同思路）。
    ///   · 防暴力：同一来源 IP 连续失败 ≥5 次 → 锁定 60 秒（LOCK_SECONDS）。
    ///
    /// ── 为什么按 IP 而不是全局 ────────────────────────────────────────
    ///   一个攻击者不该把合法用户也一起锁死；按来源 IP 隔离，误伤面最小。
    ///   NAT 后的多人共享同一出口 IP 时会连坐 —— 在「家用/小团队远程访问」
    ///   这个真实场景下接受这个取舍（比全局锁好得多）。
    /// </summary>
    internal sealed class AuthGate
    {
        internal const string CookieName = "soullink_te";
        private const int MaxFailures = 5;
        private const int LockSeconds = 60;

        private sealed class Bucket
        {
            internal int Failures;
            internal DateTime LockedUntil = DateTime.MinValue;
        }

        private readonly object _gate = new();
        private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _password;
        private readonly string _token;

        internal AuthGate(string password, string existingToken, out bool generated)
        {
            _password = (password ?? "").Trim();
            string token = (existingToken ?? "").Trim();
            generated = false;
            if (!string.IsNullOrWhiteSpace(_password)) token = "";  // 用户设了口令 → 口令即凭据
            if (token.Length == 0 && _password.Length == 0)
            {
                token = Guid.NewGuid().ToString("N");
                generated = true;
            }
            _token = token;
        }

        /// <summary>需要展示给用户的一次性令牌（仅当本次自动生成时非空）。</summary>
        internal string GeneratedToken => _token.Length > 0 && _password.Length == 0 ? _token : "";

        /// <summary>本次会话 Cookie 的值（登录成功后下发）。</summary>
        internal string SessionValue => _token.Length > 0 ? _token : Hash(_password);

        /// <summary>是否处于「强制鉴权」状态（非回环必须过这一关）。</summary>
        internal bool RequireToken => _token.Length > 0 || _password.Length > 0;

        /// <summary>是否回环来源（免密）。</summary>
        internal static bool IsLoopback(HttpListenerContext ctx)
        {
            try
            {
                var ep = ctx.Request.RemoteEndPoint;
                if (ep == null) return false;
                var addr = ep.Address;
                if (IPAddress.IsLoopback(addr)) return true;
                // IPv4-mapped IPv6（::ffff:127.0.0.1）
                if (addr.IsIPv4MappedToIPv6) return IPAddress.IsLoopback(addr.MapToIPv4());
                return false;
            }
            catch { return false; }
        }

        /// <summary>来源标识（用于失败计数；回环统一记 "loopback"）。</summary>
        internal static string ClientKey(HttpListenerContext ctx)
        {
            try { return ctx.Request.RemoteEndPoint?.Address?.ToString() ?? "?"; }
            catch { return "?"; }
        }

        /// <summary>
        /// 判定这次请求是否已授权。
        /// <paramref name="rejectReason"/> 非空表示要拒绝（已经带好给用户看的中文原因）。
        /// </summary>
        internal bool Check(HttpListenerContext ctx, out string rejectReason)
        {
            rejectReason = "";
            if (IsLoopback(ctx)) return true;            // ★ 唯一的免密例外
            if (!RequireToken)
            {
                // 令牌与口令都为空 = 服务无法判定身份。这种情况**拒绝**，
                // 绝不复制主程序「没设密码就放行」的短路。
                rejectReason = "未配置访问口令：请在主程序设置中填写网页口令后重试";
                return false;
            }

            string key = ClientKey(ctx);
            lock (_gate)
            {
                if (_buckets.TryGetValue(key, out var b) && b.LockedUntil > DateTime.UtcNow)
                {
                    int left = (int)Math.Ceiling((b.LockedUntil - DateTime.UtcNow).TotalSeconds);
                    rejectReason = "尝试次数过多，已锁定 " + Math.Max(1, left) + " 秒";
                    return false;
                }
            }

            if (Matches(ctx, SessionValue)) { ClearFailures(key); return true; }
            // 也接受通过 ?token= 显式传入（curl/脚本场景，避免先登录再回放 cookie）
            string q = ctx.Request.QueryString["token"] ?? "";
            if (q.Length > 0 && Matches(q, SessionValue)) { ClearFailures(key); return true; }
            string pw = ctx.Request.QueryString["pw"] ?? "";
            if (_password.Length > 0 && pw.Length > 0 && Matches(pw, _password)) { ClearFailures(key); return true; }

            RecordFailure(key);
            rejectReason = "未登录：请先访问 /taskedit/login 输入口令";
            return false;
        }

        /// <summary>登录尝试（供 /taskedit/login 使用）。返回 null 表示成功。</summary>
        internal string TryLogin(HttpListenerContext ctx, string password)
        {
            if (IsLoopback(ctx)) return null;
            if (!RequireToken) return "未配置访问口令：请在主程序设置中填写网页口令";
            string key = ClientKey(ctx);
            lock (_gate)
            {
                if (_buckets.TryGetValue(key, out var b) && b.LockedUntil > DateTime.UtcNow)
                {
                    int left = (int)Math.Ceiling((b.LockedUntil - DateTime.UtcNow).TotalSeconds);
                    return "尝试次数过多，已锁定 " + Math.Max(1, left) + " 秒";
                }
            }
            bool ok = _password.Length > 0
                ? Matches(password, _password)
                : Matches(password, _token);
            if (ok) { ClearFailures(key); return null; }
            RecordFailure(key);
            return "口令错误";
        }

        private void ClearFailures(string key)
        {
            lock (_gate) { _buckets.Remove(key); }
        }

        private void RecordFailure(string key)
        {
            lock (_gate)
            {
                if (!_buckets.TryGetValue(key, out var b)) { b = new Bucket(); _buckets[key] = b; }
                b.Failures++;
                if (b.Failures >= MaxFailures)
                {
                    b.LockedUntil = DateTime.UtcNow.AddSeconds(LockSeconds);
                    b.Failures = 0;   // 锁定后清零，解封再给 5 次
                }
                // 简单防爆：桶太多时清掉已解锁的
                if (_buckets.Count > 512)
                {
                    var drop = new List<string>();
                    foreach (var kv in _buckets) if (kv.Value.LockedUntil <= DateTime.UtcNow) drop.Add(kv.Key);
                    foreach (string k in drop) _buckets.Remove(k);
                }
            }
        }

        /// <summary>是否已经有登录 Cookie。</summary>
        private static bool Matches(HttpListenerContext ctx, string expected)
        {
            try
            {
                var c = ctx.Request.Cookies[CookieName];
                if (c != null && Matches(c.Value, expected)) return true;
                // 也接受主程序原有的 soullink_sid（用户在旧页面登录过）
                var legacy = ctx.Request.Cookies["soullink_sid"];
                return legacy != null && Matches(legacy.Value, expected);
            }
            catch { return false; }
        }

        /// <summary>定长比较，避免计时侧信道。</summary>
        private static bool Matches(string? actual, string expected)
        {
            if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(expected)) return false;
            byte[] a = Encoding.UTF8.GetBytes(actual);
            byte[] b = Encoding.UTF8.GetBytes(expected);
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }

        /// <summary>设置持久 Cookie（1 年）。</summary>
        internal void IssueCookie(HttpListenerContext ctx)
        {
            try
            {
                ctx.Response.Headers["Set-Cookie"] =
                    CookieName + "=" + SessionValue
                    + "; Path=/; Expires=" + DateTime.UtcNow.AddYears(1).ToString("R")
                    + "; Max-Age=31536000; SameSite=Lax";
            }
            catch { }
        }

        /// <summary>口令 → 会话值（口令不直接出现在 Cookie 里）。</summary>
        private static string Hash(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes("soullink-taskedit|" + value));
            return Convert.ToHexString(h)[..32].ToLowerInvariant();
        }
    }
}
