using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoulLink.Models;

namespace SoulLink.Services
{
    public sealed class BgiHotkeyItem { public string Name { get; set; } = ""; public string Key { get; set; } = ""; }
    /// <summary>一个账号的 BGI 文件与配置目录快照；整个应用共享这一份。</summary>
    public sealed class BgiAccountData
    {
        public string UserName { get; set; } = "";
        public string ExePath { get; set; } = "";
        public string ConfigPath { get; set; } = "";
        public DateTime ReadAt { get; set; }
        public bool ConfigExists { get; set; }
        public string Error { get; set; } = "";
        public string CancelTaskHotkey { get; set; } = "";
        public string CancelTaskHotkeyType { get; set; } = "";
        public List<string> OneDragonConfigs { get; set; } = new();
        public List<string> ScriptGroups { get; set; } = new();
        public Dictionary<string, string> Hotkeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>用于界面的最终快捷键：只有中文功能名和明确按键。</summary>
        public List<BgiHotkeyItem> DisplayHotkeys { get; set; } = new();
        public string RootPath => string.IsNullOrEmpty(ExePath) ? "" : Path.GetDirectoryName(ExePath) ?? "";
        public string DirectoryOf(string kind) => kind.Equals("User", StringComparison.OrdinalIgnoreCase) ? Path.Combine(RootPath, "User") : Path.Combine(RootPath, "User", kind);
        public string HotkeyText => CancelTaskHotkey.Length == 0 ? "未设置停止当前脚本快捷键" : CancelTaskHotkey;
    }

    /// <summary>
    /// 账号 BGI 共享数据中心。路径解析仍由 BgiPathResolver 负责；这里只读取该账号对应的文件。
    /// 不使用任何其它账号的目录兜底，也不把目录列表复制进节点参数。
    /// </summary>
    public static class BgiDataCenter
    {
        private static readonly object Sync = new();
        private static readonly Dictionary<string, BgiAccountData> Cache = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>每个账号上次算出的 Stamp（命中就不用再扫目录）。</summary>
        private static readonly Dictionary<string, string> _stamps = new(StringComparer.OrdinalIgnoreCase);
        public static event Action<string>? AccountDataChanged;

        public static BgiAccountData Get(string userName) => Read(userName, false);
        public static BgiAccountData Refresh(string userName) => Read(userName, true);
        public static IReadOnlyList<string> ListConfigs(string userName, string kind) => Get(userName).ScriptGroupsOrOneDragon(kind);
        private static IReadOnlyList<string> ScriptGroupsOrOneDragon(this BgiAccountData data, string kind)
            => kind.Equals("OneDragon",StringComparison.OrdinalIgnoreCase) ? data.OneDragonConfigs : data.ScriptGroups;

        public static void RefreshAll(IEnumerable<string> users)
        {
            foreach (var user in users?.Distinct(StringComparer.OrdinalIgnoreCase) ?? Enumerable.Empty<string>()) Refresh(user);
        }

        /// <summary>路径变更入口：先保存真实路径，再通知状态中心和所有页面刷新。</summary>
        public static void SetPath(string userName, string path, bool save = true)
        {
            string normalized = BgiPathResolver.Normalize(path);
            BgiPathResolver.Pin(userName, normalized);
            if (save) ConfigStore.Save(App.Config);
            Invalidate(userName);
        }

        public static void Invalidate(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) return;
            lock (Sync) Cache.Remove(userName);
            AccountStateCenter.MarkConfigurationDirty();
            AccountDataChanged?.Invoke(userName);
        }

        private static BgiAccountData Read(string userName, bool force)
        {
            string user = userName?.Trim() ?? "";
            if (user.Length == 0) return new BgiAccountData { UserName = "", Error = "未指定账号", ReadAt = DateTime.Now };
            string path = BgiPathResolver.GetPath(user);

            // ★ 2.17.45：缓存命中时**不要**再算 Stamp()。
            //   Stamp() 会对 User、User\OneDragon、User\ScriptGroup 三个目录
            //   各做一次 Directory.GetFiles + 逐文件 stat —— 而它本来就是为了
            //   "判断要不要重新读"，缓存命中时算两遍是纯浪费。
            //   改成：1 秒内直接命中缓存；超过 1 秒才算 Stamp 比对。
            lock (Sync)
            {
                if (!force && Cache.TryGetValue(user, out var cached)
                    && cached.ExePath.Equals(path, StringComparison.OrdinalIgnoreCase)
                    && (DateTime.UtcNow - cached.ReadAt.ToUniversalTime()) < TimeSpan.FromSeconds(1))
                    return Clone(cached);
            }

            // 超过 1 秒才做一次目录比对；比对通过就复用缓存。
            string stamp = Stamp(path);
            lock (Sync)
            {
                if (!force && Cache.TryGetValue(user, out var cached2)
                    && cached2.ExePath.Equals(path, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(_stamps.GetValueOrDefault(user, ""), stamp, StringComparison.Ordinal))
                    return Clone(cached2);
                _stamps[user] = stamp;
            }
            var data = ReadFiles(user, path);
            BgiAccountData? previous;
            lock (Sync) { Cache.TryGetValue(user, out previous); Cache[user] = Clone(data); }
            if (previous != null && Signature(previous) != Signature(data)) AccountDataChanged?.Invoke(user);
            return data;
        }

        private static string Stamp(string path)
        {
            try
            {
                if (path.Length == 0) return "";
                string root = Path.GetDirectoryName(path) ?? "";
                string userDir = Path.Combine(root, "User");
                var parts = new List<string>();
                foreach (var file in new[] { Path.Combine(userDir, "config.json"), Path.Combine(userDir, "OneDragon"), Path.Combine(userDir, "ScriptGroup") })
                {
                    if (File.Exists(file)) { var f = new FileInfo(file); parts.Add(file + "|" + f.Length + "|" + f.LastWriteTimeUtc.Ticks); }
                    else if (Directory.Exists(file))
                    {
                        var d = new DirectoryInfo(file); parts.Add(file + "|" + d.LastWriteTimeUtc.Ticks);
                        foreach (var item in Directory.GetFiles(file, "*.json"))
                        { var f = new FileInfo(item); parts.Add(item + "|" + f.Length + "|" + f.LastWriteTimeUtc.Ticks); }
                    }
                }
                return string.Join(";", parts);
            }
            catch { return ""; }
        }

        private static string Signature(BgiAccountData d) => d.ExePath + "|" + d.ConfigPath + "|" + d.CancelTaskHotkey + "|" + d.CancelTaskHotkeyType + "|" + d.Error + "|" + string.Join(",", d.OneDragonConfigs) + "|" + string.Join(",", d.ScriptGroups);

        internal static BgiAccountData ReadFiles(string user, string path)
        {
            var d = new BgiAccountData { UserName = user, ExePath = path, ReadAt = DateTime.Now };
            try
            {
                string root = d.RootPath;
                if (root.Length == 0) { d.Error = "该账号尚未设置 BGI 路径"; return d; }
                d.OneDragonConfigs = ReadConfigNames(root, "OneDragon");
                d.ScriptGroups = ReadConfigNames(root, "ScriptGroup");
                d.ConfigPath = Path.Combine(root, "User", "config.json");
                if (!File.Exists(d.ConfigPath)) { d.Error = "找不到 User 配置文件 config.json，无法读取快捷键"; return d; }
                d.ConfigExists = true;
                using var stream = new FileStream(d.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var rootNode = JsonNode.Parse(stream, new JsonNodeOptions { PropertyNameCaseInsensitive = true });
                if (rootNode is not JsonObject obj) { d.Error = "BGI 配置不是有效对象"; return d; }
                ReadHotkeyObjects(obj, d);
                d.CancelTaskHotkey = Value(obj, "cancelTaskHotkey");
                d.CancelTaskHotkeyType = Value(obj, "cancelTaskHotkeyType");
                if (d.CancelTaskHotkey.Equals("< None >", StringComparison.OrdinalIgnoreCase)) d.CancelTaskHotkey = "";
                if (d.CancelTaskHotkeyType.Equals("< None >", StringComparison.OrdinalIgnoreCase)) d.CancelTaskHotkeyType = "";
            }
            catch (Exception ex) { d.Error = ex.Message; }
            return d;
        }

        private static void ReadHotkeyObjects(JsonObject obj, BgiAccountData d)
        {
            foreach (var property in obj)
            {
                if (property.Value is JsonObject nested) ReadHotkeyObjects(nested, d);
                if (!property.Key.Contains("Hotkey", StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Key.EndsWith("Type", StringComparison.OrdinalIgnoreCase)) continue;
                string key = ExplicitKey(property.Value);
                if (key.Length == 0) continue;
                d.Hotkeys[property.Key] = key;
                string name = HotkeyName(property.Key);
                if (name.Length > 0 && !d.DisplayHotkeys.Any(x => x.Name == name && x.Key == key))
                    d.DisplayHotkeys.Add(new BgiHotkeyItem { Name=name, Key=key });
            }
        }

        /// <summary>只接受明确按键；空、未设置、监听类型、对象和 JSON 均不是按键。</summary>
        internal static string ExplicitKey(JsonNode? node)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out var text)) return "";
            text = (text ?? "").Trim();
            if (text.Length == 0 || text.Equals("< None >",StringComparison.OrdinalIgnoreCase)) return "";
            if (text[0] is '{' or '[') return "";
            if (text.Contains("Hotkey",StringComparison.OrdinalIgnoreCase) || text.Contains("Monitor",StringComparison.OrdinalIgnoreCase)
                || text.Contains("Register",StringComparison.OrdinalIgnoreCase) || text.Equals("true",StringComparison.OrdinalIgnoreCase) || text.Equals("false",StringComparison.OrdinalIgnoreCase)) return "";
            return text;
        }

        internal static string HotkeyName(string key) => key switch
        {
            "cancelTaskHotkey" => "停止当前脚本",
            "bgiEnabledHotkey" => "启动或暂停 BGI",
            "autoPickEnabledHotkey" => "自动拾取",
            "autoSkipEnabledHotkey" => "自动跳过剧情",
            "autoSkipHangoutEnabledHotkey" => "自动跳过邀约",
            "autoWoodHotkey" => "自动伐木",
            "enhanceArtifactHotkey" => "强化圣遗物",
            "quickBuyHotkey" => "快捷购买",
            "oneKeyClaimRewardHotkey" => "一键领取奖励",
            "quickSereniteaPotHotkey" => "快捷尘歌壶",
            "quickTeleportEnabledHotkey" => "快捷传送",
            "quickTeleportTickHotkey" => "连续传送",
            "skillCdEnabledHotkey" => "技能冷却提示",
            "takeScreenshotHotkey" => "截图",
            "recognitionTemplateEditorHotkey" => "识别模板编辑器",
            "turnAroundHotkey" => "原地转圈",
            "clickGenshinConfirmButtonHotkey" => "点击确认按钮",
            "clickGenshinCancelButtonHotkey" => "点击取消按钮",
            "oneKeyFightHotkey" => "一键战斗",
            "mapPosRecordHotkey" => "记录地图位置",
            "autoMusicGameHotkey" => "自动音游",
            "autoFishingEnabledHotkey" or "autoFishingGameHotkey" => "自动钓鱼",
            "autoCookGameHotkey" => "自动烹饪",
            "autoTrackHotkey" => "自动追踪",
            "autoDomainHotkey" => "自动秘境",
            "autoFightHotkey" => "自动战斗",
            "autoGenshinInvocationHotkey" => "自动召唤",
            "logBoxDisplayHotkey" => "显示或隐藏日志",
            "suspendHotkey" => "暂停或恢复",
            "mapMaskEnabledHotkey" => "显示或隐藏地图遮罩",
            "skipPartyWaitHotkey" => "跳过队伍等待",
            _ => ""
        };

        private static string Value(JsonObject obj, string key)
        {
            foreach (var kv in obj) if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return kv.Value?.ToString().Trim() ?? "";
            return "";
        }
        private static List<string> ReadConfigNames(string root, string folder)
        {
            string path = Path.Combine(root, "User", folder);
            if (!Directory.Exists(path)) return new List<string>();
            return Directory.GetFiles(path, "*.json").Select(Path.GetFileNameWithoutExtension).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }
        private static BgiAccountData Clone(BgiAccountData d) => new()
        {
            UserName=d.UserName,ExePath=d.ExePath,ConfigPath=d.ConfigPath,ReadAt=d.ReadAt,ConfigExists=d.ConfigExists,Error=d.Error,
            CancelTaskHotkey=d.CancelTaskHotkey,CancelTaskHotkeyType=d.CancelTaskHotkeyType,OneDragonConfigs=d.OneDragonConfigs.ToList(),
            ScriptGroups=d.ScriptGroups.ToList(),Hotkeys=new Dictionary<string,string>(d.Hotkeys,StringComparer.OrdinalIgnoreCase),
            DisplayHotkeys=d.DisplayHotkeys.Select(x=>new BgiHotkeyItem{Name=x.Name,Key=x.Key}).ToList()
        };
    }
}