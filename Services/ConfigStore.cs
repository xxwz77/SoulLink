using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoulLink.Core;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>
    /// 配置读写。默认放在 %LOCALAPPDATA%\SoulLink\config.json（无需管理员写权限），
    /// 也支持用 SOULLINK_CONFIG 环境变量或 exe 同目录的 config.portable.json 做便携模式。
    /// </summary>
    public static class ConfigStore
    {
        private static readonly JsonSerializerOptions Opt = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() }
        };

        public static string ConfigPath
        {
            get
            {
                string? env = Environment.GetEnvironmentVariable("SOULLINK_CONFIG");
                if (!string.IsNullOrWhiteSpace(env)) return env!;

                string portable = Path.Combine(AppContext.BaseDirectory, "config.portable.json");
                if (File.Exists(portable)) return portable;

                return Path.Combine(ConfigDir, "config.json");
            }
        }

        // ★ 配置目录同样按身份隔离，两个版本互不干扰各自的账号与设置。
        public static string ConfigDir => AppIdentity.ConfigDir;

        public static string StatePath => Path.Combine(ConfigDir, "state.json");

        /// <summary>
        /// 按日期分文件的日志路径：rdp-debug-20261116.log
        ///
        /// ★ 为什么按天分：长期挂机日志量虽已大幅压缩，但单文件久了仍然不好查，
        ///   也不利于用户只发某一天的文件过来。
        /// </summary>
        public static string DailyLogPath(string prefix)
        {
            try
            {
                return Path.Combine(ConfigDir, prefix + "-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            }
            catch { return Path.Combine(ConfigDir, prefix + ".log"); }
        }

        /// <summary>清掉超过 N 天的历史日志（保留最近 N 天）。</summary>
        public static void PruneDailyLogs(string prefix, int keepDays = 7)
        {
            try
            {
                if (!Directory.Exists(ConfigDir)) return;
                var cutoff = DateTime.Now.Date.AddDays(-keepDays);
                foreach (var f in Directory.GetFiles(ConfigDir, prefix + "-*.log"))
                {
                    try
                    {
                        string name = Path.GetFileNameWithoutExtension(f);   // rdp-debug-20261116
                        int dash = name.LastIndexOf('-');
                        if (dash < 0) continue;
                        string day = name.Substring(dash + 1);
                        if (day.Length != 8) continue;
                        if (!DateTime.TryParseExact(day, "yyyyMMdd", null,
                                System.Globalization.DateTimeStyles.None, out var d)) continue;
                        if (d < cutoff) File.Delete(f);
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static AppConfig Load()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return Defaults();
                string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, Opt);
                if (cfg == null) return Defaults();
                if (cfg.Settings == null) cfg.Settings = new AppSettings();
                if (cfg.Accounts == null) cfg.Accounts = new List<Account>();
                if (cfg.Actions == null) cfg.Actions = new List<ActionItem>();
            if (cfg.AutomationProfiles == null) cfg.AutomationProfiles = new List<AutomationProfile>();
                return cfg;
            }
            catch
            {
                // 配置损坏时备份后重建，避免程序打不开
                try
                {
                    if (File.Exists(ConfigPath))
                        File.Copy(ConfigPath, ConfigPath + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
                }
                catch { }
                return Defaults();
            }
        }

        public static void Save(AppConfig cfg)
        {
            string path = ConfigPath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Opt), new UTF8Encoding(false));
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch
            {
                try { File.Copy(tmp, path, true); File.Delete(tmp); } catch { }
            }
        }

        public static AppConfig Defaults()
        {
            var cfg = new AppConfig();

            return cfg;
        }

        // ── 账号密码便捷方法 ───────────────────────────────────────────────

        public static string GetPassword(Account a) => SecretBox.Unprotect(a.PasswordCipher);

        public static void SetPassword(Account a, string plain)
        {
            a.PasswordCipher = string.IsNullOrEmpty(plain) ? "" : SecretBox.Protect(plain);
        }

        // ── 运行期状态（窗口位置、上次选中的动作）──────────────────────────

        public class UiState
        {
            public double Width { get; set; } = 1180;
            public double Height { get; set; } = 780;
            public string SelectedActionId { get; set; } = "";
            public string SelectedAccountId { get; set; } = "";
            public bool Maximized { get; set; }
            /// <summary>左侧导航是否收起</summary>
            public bool NavCollapsed { get; set; }
            /// <summary>左侧导航是否启用「自动收起」（鼠标悬停展开）</summary>
            public bool NavAutoHide { get; set; }
        }

        public static UiState LoadState()
        {
            try
            {
                if (!File.Exists(StatePath)) return new UiState();
                return JsonSerializer.Deserialize<UiState>(File.ReadAllText(StatePath, Encoding.UTF8), Opt) ?? new UiState();
            }
            catch { return new UiState(); }
        }

        public static void SaveState(UiState s)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                File.WriteAllText(StatePath, JsonSerializer.Serialize(s, Opt), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
