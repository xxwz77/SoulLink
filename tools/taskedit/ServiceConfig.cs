using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SoulLink.TaskEdit
{
    /// <summary>
    /// 独立服务的配置来源。
    ///
    /// ── 为什么自己读 config.json，而不是引用主程序程序集 ────────────────
    ///   主程序是 WPF 工程（SoulLink.csproj，UseWPF=true + UseWindowsForms=true）。
    ///   一旦 ProjectReference 它，本服务会被迫拖进 PresentationFramework /
    ///   PresentationCore / WindowsBase / System.Windows.Forms 全套 UI 依赖，
    ///   单文件包从 ~0.2 MB 膨胀到 ~5 MB 以上 —— 与「框架依赖 + 单文件，体积小」
    ///   的发布目标直接冲突，也违背「本服务不引用 WPF」的硬约束。
    ///   共享库方案（新建 SoulLink.Shared）需要把 AppSettings 从 WPF 工程里整个
    ///   搬出来，改动面覆盖全部既有调用点，风险远大于收益。
    ///   → 结论：**只读这几个标量字段**，用 JsonDocument 直接取，零依赖、零副作用。
    ///     每个字段都有独立回退，config.json 不存在/损坏时给出可用默认并明确警告。
    ///
    /// ── 数据目录判定（与 <see cref="SoulLink.Services.AppIdentity"/> 完全一致）──
    ///   ① 环境变量 SOULLINK_DATA_DIR
    ///   ② exe 同目录 \data
    ///   ③ %LOCALAPPDATA%\&lt;身份名&gt;
    ///   直接复用 AppIdentity（它是纯路径逻辑，零 WPF 依赖），不自己重写一遍。
    ///
    /// ── 读什么、不读什么 ──────────────────────────────────────────────
    ///   读：WebPort / WebPassword / WebAuthToken（+ 报告里要用的 WebDirectToAgent）
    ///   不读也不写：任何账号、BGI 路径、User/ScriptGroup 等配置内容
    ///   （那是 P1 数据面的事；P0 只做通道，绝不碰配置）。
    /// </summary>
    internal sealed class ServiceConfig
    {
        /// <summary>对外端口（0 = 用户没启用网页，本服务不应启动）。</summary>
        internal int WebPort { get; private set; }

        /// <summary>内部端口（主程序回环监听端口），由主程序通过参数/文件告知。</summary>
        internal int InternalPort { get; private set; }

        /// <summary>主程序网页口令（非空则非回环访问必须带令牌）。</summary>
        internal string WebPassword { get; private set; } = "";

        /// <summary>持久化会话令牌（cookie 值）。</summary>
        internal string WebAuthToken { get; private set; } = "";

        /// <summary>config.json 的绝对路径（日志里可以给用户看，不含任何凭据）。</summary>
        internal string ConfigPath { get; private set; } = "";

        /// <summary>读取过程中产生的问题（启动时打印，便于排查）。</summary>
        internal readonly List<string> Warnings = new();

        /// <summary>
        /// 读取配置。
        /// </summary>
        /// <param name="internalPort">主程序告知的内部端口；0 = 未知（反代会直接降级报错）。</param>
        internal static ServiceConfig Load(int internalPort)
        {
            var cfg = new ServiceConfig { InternalPort = internalPort };
            string dir = SoulLink.Services.AppIdentity.DataDir;
            cfg.ConfigPath = Path.Combine(dir, "config.json");

            string json = "";
            try
            {
                if (File.Exists(cfg.ConfigPath)) json = File.ReadAllText(cfg.ConfigPath, Encoding.UTF8);
                else
                {
                    // 便携模式：SOULLINK_CONFIG 可指向任意位置（与主程序 ConfigStore 同一规则）
                    string env = Environment.GetEnvironmentVariable("SOULLINK_CONFIG") ?? "";
                    if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
                    { json = File.ReadAllText(env, Encoding.UTF8); cfg.ConfigPath = env; }
                }
            }
            catch (Exception ex)
            {
                cfg.Warnings.Add("读取 config.json 失败：" + ex.GetType().Name);
            }

            if (json.Length == 0) { cfg.Warnings.Add("config.json 不存在或为空：" + cfg.ConfigPath); return cfg; }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("settings", out var s) &&
                    !doc.RootElement.TryGetProperty("Settings", out s))
                { cfg.Warnings.Add("config.json 里没有 settings 节点"); return cfg; }

                cfg.WebPort = Int(s, "webPort");
                cfg.WebPassword = Str(s, "webPassword");
                cfg.WebAuthToken = Str(s, "webAuthToken");

                // 内部端口也可以由主程序写进配置（作为参数之外的兜底通道）
                if (cfg.InternalPort <= 0) cfg.InternalPort = Int(s, "webInternalPort");
            }
            catch (JsonException)
            {
                cfg.Warnings.Add("config.json 格式无效");
            }
            return cfg;
        }

        /// <summary>把会话令牌写回 config.json（仅当原本为空、本次生成了新令牌时调用）。</summary>
        internal bool TryPersistAuthToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(ConfigPath)) return false;
            try
            {
                if (!File.Exists(ConfigPath)) return false;
                string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                // 极小侵入：只在确实没有该键时插入，不改动其它任何内容。
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.TryGetProperty("settings", out var s) &&
                        s.TryGetProperty("webAuthToken", out _)) return false;
                }
                int at = json.IndexOf("\"webPort\"", StringComparison.Ordinal);
                if (at < 0) return false;
                int anchor = json.IndexOf(':', at);
                if (anchor < 0) return false;
                anchor = json.IndexOf(',', anchor);
                if (anchor < 0) return false;
                string insert = "\"webAuthToken\": " + JsonSerializer.Serialize(token) + ",";
                string updated = json.Substring(0, anchor + 1) + insert + json.Substring(anchor + 1);
                string tmp = ConfigPath + ".taskedit.tmp";
                File.WriteAllText(tmp, updated, new UTF8Encoding(false));
                File.Move(tmp, ConfigPath, true);
                return true;
            }
            catch { return false; }
        }

        private static int Int(JsonElement parent, string name)
        {
            foreach (var p in parent.EnumerateObject())
            {
                if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int v)) return v;
                if (p.Value.ValueKind == JsonValueKind.String && int.TryParse(p.Value.GetString(), out int s)) return s;
            }
            return 0;
        }

        private static string Str(JsonElement parent, string name)
        {
            foreach (var p in parent.EnumerateObject())
            {
                if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString() ?? "";
            }
            return "";
        }
    }
}
