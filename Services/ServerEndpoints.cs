using System;

namespace SoulLink.Services
{
    /// <summary>
    /// ★ 单一服务器地址设置：联机大厅 / 路线更新 / 客户端更新三处共用的唯一来源。
    ///
    ///   · 代码里不再硬编码任何域名（原 myorg 域名从源码中移除）；
    ///   · 地址只来自 <c>App.Config.Settings.ServerBaseUrl</c>；
    ///   · 未配置时所有派生地址为空串，消费点据此静默短路，不发任何请求；
    ///   · 三处子路径的拼接规则保持不变：
    ///        联机大厅   = {base}/api/…
    ///        客户端更新 = {base}/SoulLink/latest.json、{base}/SoulLink/{包名}
    ///        路线更新   = {base}/pathing/update.txt
    /// </summary>
    public static class ServerEndpoints
    {
        /// <summary>归一化基地址：去首尾空白、去末尾 '/'。空串原样返回。</summary>
        public static string NormalizeBase(string? value)
        {
            string v = (value ?? "").Trim();
            while (v.EndsWith("/") && v.Length > 0)
                v = v.Substring(0, v.Length - 1);
            return v;
        }

        /// <summary>当前生效的服务器基地址（未配置 = 空串）。</summary>
        public static string Base
        {
            get
            {
                try { return NormalizeBase(App.Config?.Settings?.ServerBaseUrl); }
                catch { return ""; }
            }
        }

        /// <summary>是否已配置服务器地址。</summary>
        public static bool IsConfigured => Base.Length > 0;

        // ── 联机大厅：直接用 Base，路径前缀 /api/… ──

        // ── 客户端更新：{base}/SoulLink/… ──
        /// <summary>更新根目录（含尾斜杠）。未配置时为空串。</summary>
        public static string UpdateBase => IsConfigured ? Base + "/SoulLink/" : "";

        /// <summary>更新清单地址。</summary>
        public static string UpdateManifest => IsConfigured ? UpdateBase + "latest.json" : "";

        // ── 路线更新：{base}/pathing/update.txt ──
        /// <summary>路线清单地址。</summary>
        public static string RouteManifest => IsConfigured ? Base + "/pathing/update.txt" : "";
    }
}
