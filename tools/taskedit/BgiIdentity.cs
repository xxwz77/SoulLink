using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SoulLink.TaskEdit
{
    /// <summary>
    /// BGI 安装目录与版本识别（设计文档 §6.1 / §17.1）。
    ///
    /// ── 版本判定（用户 2026-11-25 定稿）────────────────────────────
    ///   ProductVersion 含 "lcb"（不分大小写）→ 茶包版；否则公版。
    ///   基础版本号单独解析（如 0.67.2）。
    ///   识别不到 exe / 读不出版本 → 未知（按最小公版能力集）。
    /// </summary>
    internal static class BgiIdentity
    {
        /// <summary>一次版本识别结果。</summary>
        internal sealed class Info
        {
            internal string ExePath = "";
            internal string ProductVersion = "";
            internal string FileVersion = "";
            internal string Company = "";
            internal string Channel = "unknown";   // teabag / upstream / unknown
            internal string BaseVersion = "";       // 如 0.67.2
            internal string Fingerprint = "";
            /// <summary>探测时试过的候选路径（失败排查用）。</summary>
            internal readonly List<string> TriedPaths = new();
        }

        /// <summary>按「文件大小 + 最后写入时间 + 版本串」做指纹，exe 更新后重读。</summary>
        private static readonly Dictionary<string, (string fp, Info info)> Cache
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 显式账号只使用配置中的绑定；未指定账号的旧入口保留本机发现逻辑。
        /// 绑定不可用时返回空串。
        /// </summary>
        internal static string ResolveRoot(string account, string configJsonPath)
        {
            string root = "";
            try
            {

                // ② config.json 的 BgiPaths[账号]
                if (root.Length == 0 && !string.IsNullOrWhiteSpace(account) && File.Exists(configJsonPath))
                {
                    string json = File.ReadAllText(configJsonPath, Encoding.UTF8);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if ((doc.RootElement.TryGetProperty("Settings", out var s) || doc.RootElement.TryGetProperty("settings", out s)) &&
                        (s.TryGetProperty("BgiPaths", out var paths) || s.TryGetProperty("bgiPaths", out paths)))
                    {
                        foreach (var p in paths.EnumerateObject())
                            if (string.Equals(p.Name, account, StringComparison.OrdinalIgnoreCase))
                            { root = p.Value.GetString() ?? ""; break; }
                    }
                }

                if (root.Length > 0)
                {
                    // BgiPaths 存的是 BetterGI.exe 完整路径，取目录
                    if (File.Exists(root)) root = Path.GetDirectoryName(root) ?? root;
                    // 验证目录下确有 BetterGI.exe 且能读出版本
                    if (TryDetect(root, out var _)) return root;
                    // 读不出 → 走候选探测
                    root = "";
                }
            }
            catch { }

            // ③ 候选路径探测（父目录推导）
            return string.IsNullOrWhiteSpace(account) ? ProbeCandidateRoots(account, configJsonPath) : "";
        }

        /// <summary>
        /// 按候选路径列表依次探测。候选来自：
        ///   · 父目录推断（config.json 所在目录、account 所在父目录）；
        ///   · 固定布局 <父目录><账号>BetterGI、<父目录>BetterGI；
        ///   · 已知账号同级模式（AAA挂机专区下的 A1/A2/A3/A4 + 根级 BetterGI）。
        /// 验证标准：BetterGI.exe 存在且能读出版本（ProductVersion 非空）。
        /// </summary>
        private static string ProbeCandidateRoots(string account, string configJsonPath)
        {
            var candidates = new List<string>();

            string configDir = "";
            try { configDir = Path.GetDirectoryName(Path.GetFullPath(configJsonPath)) ?? ""; } catch { }
            string acct = string.IsNullOrWhiteSpace(account) ? "" : account.Trim();

            void Add(string dir)
            {
                if (string.IsNullOrWhiteSpace(dir)) return;
                try { dir = Path.GetFullPath(dir); } catch { return; }
                if (candidates.Exists(x => string.Equals(x, dir, StringComparison.OrdinalIgnoreCase))) return;
                candidates.Add(dir);
            }

            // 从 configDir 往上找「账号目录」与「父目录」
            // 例：config = D:\\AAA挂机专区\\data\\config.json → 父目录 D:\\AAA挂机专区
            string parent = configDir;
            for (int i = 0; i < 3 && parent.Length > 0; i++)
            {
                Add(Path.Combine(parent, acct, "BetterGI"));   // <父>/<账号>/BetterGI
                Add(Path.Combine(parent, "BetterGI"));         // <父>/BetterGI
                try { parent = Path.GetDirectoryName(parent) ?? ""; } catch { break; }
            }

            // 已知账号同级模式：AAA挂机专区 下的常见账号 + 根级 BetterGI
            var knownAccounts = new[] { "A1", "A2", "A3", "A4", "root" };
            foreach (string drive in new[] { @"D:\AAA挂机专区", @"D:\", @"E:\" })
            {
                if (!Directory.Exists(drive)) continue;
                Add(Path.Combine(drive, "BetterGI"));   // 根级
                foreach (string ka in knownAccounts)
                    Add(Path.Combine(drive, ka, "BetterGI"));
            }

            foreach (string dir in candidates)
            {
                if (TryDetect(dir, out var info))
                {
                    // 记录试过路径到缓存里（以便后续 Detect 直接命中）
                    return dir;
                }
                _ = info;
            }
            return "";
        }

        /// <summary>目录下 BetterGI.exe 存在且能读出版本。返回 true 表示命中。</summary>
        private static bool TryDetect(string dir, out Info info)
        {
            info = null;
            if (string.IsNullOrWhiteSpace(dir)) return false;
            try
            {
                string exe = Path.Combine(dir, "BetterGI.exe");
                if (!File.Exists(exe)) return false;
                info = Detect(dir);
                return info != null && !string.IsNullOrWhiteSpace(info.ProductVersion);
            }
            catch { return false; }
        }

        /// <summary>读取 exe 版本信息（带指纹缓存）。</summary>
        internal static Info Detect(string bgiRoot)
        {
            if (string.IsNullOrWhiteSpace(bgiRoot)) return new Info();
            string exe = Path.Combine(bgiRoot, "BetterGI.exe");
            if (!File.Exists(exe)) return new Info { ExePath = exe, TriedPaths = { exe } };

            string fp;
            try
            {
                var fi = new FileInfo(exe);
                fp = fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            }
            catch { fp = ""; }

            lock (Cache)
            {
                if (Cache.TryGetValue(exe, out var hit) && hit.fp == fp) return hit.info;
            }

            var info = new Info { ExePath = exe };
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(exe);
                info.ProductVersion = vi.ProductVersion ?? "";
                info.FileVersion = vi.FileVersion ?? "";
                info.Company = vi.CompanyName ?? "";
            }
            catch { }

            // 渠道判定
            string pv = info.ProductVersion;
            if (pv.IndexOf("lcb", StringComparison.OrdinalIgnoreCase) >= 0) info.Channel = "teabag";
            else if (pv.Length > 0) info.Channel = "upstream";
            else info.Channel = "unknown";

            // 基础版本号：取开头的 x.y.z
            info.BaseVersion = ParseBaseVersion(pv);
            info.Fingerprint = fp;
            lock (Cache) { Cache[exe] = (fp, info); }
            return info;
        }

        internal static string ParseBaseVersion(string productVersion)
        {
            if (string.IsNullOrWhiteSpace(productVersion)) return "";
            int end = 0, dots = 0;
            for (int i = 0; i < productVersion.Length; i++)
            {
                char c = productVersion[i];
                if (char.IsDigit(c)) { continue; }
                if (c == '.' && dots < 2) { dots++; continue; }
                end = i; break;
            }
            if (end == 0) end = productVersion.Length;
            return productVersion.Substring(0, end).TrimEnd('.');
        }
    }
}
