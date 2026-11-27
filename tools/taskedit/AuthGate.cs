using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SoulLink.TaskEdit
{
    /// <summary>
    /// 对外访问的鉴权闸门（设计文档 §17.2 定稿，2026-11-26 统一凭据修订）。
    ///
    /// ── 规则 ───────────────────────────────────────────────────────────
    ///   · **仅回环**（127.0.0.1 / ::1 / localhost）免密 —— 本机用户点开就能用；
    ///   · **非回环一律强制令牌**，没有例外，也没有「口令为空就放行」的短路。
    ///   · 凭据现在**两套等效**：
    ///       ① 主程序 /api/login 下发的 soullink_sid（值 = config.json 的 webAuthToken）；
    ///       ② 本服务 /taskedit/login 下发的 soullink_te（值 = Hash(口令) 或 webAuthToken）。
    ///     两者都能通过本闸门并反代到主程序，客户端零改动。
    ///   · 会话：登录成功下发 1 年有效期的 Cookie。
    ///   · 防暴力：同一来源 IP 连续失败 ≥5 次 → 锁定 60 秒（LOCK_SECONDS）。
    ///     ★ 但**成功一次即清零**；?pw= 显式明文口令**不走锁定计数**；
    ///       锁定期在响应头带 Retry-After，客户端据此退避。
    ///
    /// ── 统一凭据（为什么不再区分主程序 / TaskEdit）──────────────────────
    ///   主程序下发的 soullink_sid 值是 config.json 的 webAuthToken；本服务此前
    ///   只在「无口令」时保留该 token，一旦用户设了口令就把它丢弃、改用 Hash(口令)，
    ///   导致主程序登录拿到的 cookie 穿过代理层时永远对不上 —— 安卓 App 恒 401。
    ///   现在：webAuthToken 始终保留并用于校验 soullink_sid；口令只负责
    ///   /taskedit/login 与 ?pw= 直传。两套凭据各自独立、同时有效。
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
            // ★ 统一凭据：webAuthToken 始终保留（主程序 soullink_sid 就是它）。
            //   用户设了口令时，口令只用于本服务自己的登录与 ?pw= 直传，
            //   不再清空 token —— 否则主程序 /api/login 下发的 soullink_sid 穿不过代理。
            if (token.Length == 0 && _password.Length == 0)
            {
                token = Guid.NewGuid().ToString("N");
                generated = true;
            }
            _token = token;
        }

        /// <summary>需要展示给用户的一次性令牌（仅当本次自动生成时非空）。</summary>
        internal string GeneratedToken => _token.Length > 0 && _password.Length == 0 ? _token : "";

        /// <summary>本服务登录会话 Cookie 的值（/taskedit/login 成功后下发）。</summary>
        internal string SessionValue => _password.Length > 0 ? Hash(_password) : _token;

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
                rejectReason = "未配置访问口令：请在主程序设置中填写网页口令后重试";
                return false;
            }

            string key = ClientKey(ctx);

            // ── ?pw= 显式明文口令：不走锁定计数（已持有明文口令，不是会话劫持猜测）──
            string pw = ctx.Request.QueryString["pw"] ?? "";
            if (_password.Length > 0 && pw.Length > 0)
            {
                if (Matches(pw, _password)) { ClearFailures(key); return true; }
                rejectReason = "口令错误";
                return false;
            }

            // ── 锁定检查（只针对会话 / token 猜测；?pw= 已在上方短路）──
            int retryAfter = 0;
            lock (_gate)
            {
                if (_buckets.TryGetValue(key, out var b) && b.LockedUntil > DateTime.UtcNow)
                {
                    retryAfter = (int)Math.Ceiling((b.LockedUntil - DateTime.UtcNow).TotalSeconds);
                    rejectReason = "尝试次数过多，已锁定 " + Math.Max(1, retryAfter) + " 秒";
                }
            }
            if (retryAfter > 0)
            {
                SetRetryAfter(ctx, retryAfter);
                return false;
            }

            if (MatchesAnyCookie(ctx)) { ClearFailures(key); return true; }

            string q = ctx.Request.QueryString["token"] ?? "";
            if (q.Length > 0 && MatchesAnyToken(q)) { ClearFailures(key); return true; }

            RecordFailure(key);
            rejectReason = "未登录：请先通过 /api/login?pw=口令 或 /taskedit/login 登录";
            return false;
        }

        /// <summary>登录尝试（供 /taskedit/login 使用）。返回 null 表示成功。</summary>
        internal string TryLogin(HttpListenerContext ctx, string password)
        {
            if (IsLoopback(ctx)) return null;
            if (!RequireToken) return "未配置访问口令：请在主程序设置中填写网页口令";
            string key = ClientKey(ctx);

            // ★ 成功一次即清零，且**正确口令不受锁定拦截**（锁只挡错误猜测）。
            bool ok = _password.Length > 0
                ? Matches(password, _password)
                : Matches(password, _token);
            if (ok) { ClearFailures(key); return null; }

            int retryAfter = 0;
            lock (_gate)
            {
                if (_buckets.TryGetValue(key, out var b) && b.LockedUntil > DateTime.UtcNow)
                    retryAfter = (int)Math.Ceiling((b.LockedUntil - DateTime.UtcNow).TotalSeconds);
            }
            if (retryAfter > 0)
            {
                SetRetryAfter(ctx, retryAfter);
                return "尝试次数过多，已锁定 " + Math.Max(1, retryAfter) + " 秒";
            }
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

        /// <summary>cookie 凭据匹配：soullink_te（本服务）或 soullink_sid（主程序）。</summary>
        internal bool MatchesAnyCookie(HttpListenerContext ctx)
        {
            try
            {
                var te = ctx.Request.Cookies[CookieName];
                if (te != null && Matches(te.Value, SessionValue)) return true;
                var sid = ctx.Request.Cookies["soullink_sid"];
                if (sid != null && _token.Length > 0 && Matches(sid.Value, _token)) return true;
                return false;
            }
            catch { return false; }
        }

        /// <summary>?token= 显式凭据：接受本服务会话值或主程序 token。</summary>
        private bool MatchesAnyToken(string actual)
        {
            if (Matches(actual, SessionValue)) return true;
            return _token.Length > 0 && Matches(actual, _token);
        }

        /// <summary>定长比较，避免计时侧信道。</summary>
        private static bool Matches(string? actual, string expected)
        {
            if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(expected)) return false;
            byte[] a = Encoding.UTF8.GetBytes(actual);
            byte[] b = Encoding.UTF8.GetBytes(expected);
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }

        private static void SetRetryAfter(HttpListenerContext ctx, int seconds)
        {
            try { ctx.Response.Headers["Retry-After"] = Math.Max(1, seconds).ToString(); } catch { }
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
