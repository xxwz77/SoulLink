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
