using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SoulLink.TaskEdit;

internal static class BgiTemplatePick
{
    internal const string TemplateBlackFile = "mojang_black_lists.txt";
    internal const string BlackFile = "pick_black_lists.txt";
    internal const string FuzzyBlackFile = "pick_fuzzy_black_lists.txt";
    internal const string BlackAllowFile = "pick_white_lists.txt";
    internal const string WhiteAllowFile = "pick_whitelist_mode_pick_lists.txt";
    internal const string WhiteExcludeFile = "pick_whitelist_mode_do_not_pick_lists.txt";
    internal const string WhiteFuzzyFile = "pick_whitelist_mode_do_not_pick_fuzzy_lists.txt";
    internal const string DefaultWhite = "Assets/Config/Pick/default_pick_white_lists.json";
    internal const string BlackFuzzyDefault = "Assets/Config/Pick/default_pick_fuzzy_black_lists.json";
    internal const string DefaultBlack = "Assets/Config/Pick/default_pick_black_lists.json";
    internal const string DefaultFuzzyWhite = "Assets/Config/Pick/default_pick_fuzzy_white_lists.json";
    internal sealed record TemplateInfo(string Name, string ItemName, string Color, string Category, string Region, int Len)
    {
        public string DisplayName => BgiPickTemplateCategories.DisplayName(ItemName);
        public string? AlternateItemName => BgiPickTemplateCategories.AlternateItemName(ItemName);
    }
    internal sealed class Model
    {
        public string Root = ""; public string Warning = ""; public string Mode = "Blacklist";
        public List<string> BlackList = new(); public List<string> FuzzyBlackList = new(); public List<string> BlackAllowList = new();
        public List<string> WhiteAllowList = new(); public List<string> WhiteExcludeList = new(); public List<string> WhiteFuzzyExcludeList = new(); public List<string> DefaultBlackList = new(); public List<string> DefaultBlackFuzzy = new();
        public List<string> DefaultWhiteList = new(); public Dictionary<string,string> DefaultWhiteFuzzy = new(StringComparer.OrdinalIgnoreCase);
        public List<string> TemplateBlackList;
        public List<TemplateInfo> Templates = new(); public JsonObject AutoPickConfig = new();
    }
    private static string Asset(string root, string relative)
    {
        var p = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(p)) return p;
        p = Path.Combine(root, "GameTask", relative.Replace('/', Path.DirectorySeparatorChar));
        return p;
    }
    private static List<string> ReadJsonList(string path)
    {
        try { using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8)); if (doc.RootElement.ValueKind == JsonValueKind.Array) return doc.RootElement.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToList(); } catch { }
        return new();
    }
    private static Dictionary<string,string> ReadJsonMap(string path)
    {
        try { using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8)); if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc.RootElement.EnumerateObject().Where(x => x.Value.ValueKind == JsonValueKind.String).ToDictionary(x => x.Name, x => x.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase); } catch { }
        return new(StringComparer.OrdinalIgnoreCase);
    }
    private static string LegacyCategory(string item, string metadata, string templatePath, string color)
    {
        if (!string.IsNullOrWhiteSpace(metadata)) return metadata.Trim();
        var folders = templatePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (folders.Length > 2 && folders[0] == color) return folders[1];
        if (folders.Length > 1 && folders[0] != color && folders[0] is not ("灰" or "绿" or "蓝" or "紫" or "金" or "白"))
            return folders[0];
        if (item == "妖精绿艾") return "地方特产";
        if (BgiPickTemplateCategories.IsArtifactPiece(item)) return BgiPickTemplateCategories.ArtifactCategory;
        if (item == "火腿") return "食材";
        if (item.StartsWith("精锻用", StringComparison.Ordinal)) return "武器强化材料";
        if (item.Contains("武器原胚", StringComparison.Ordinal)) return "武器材料";
        if (item is "大英雄的经验" or "冒险家的经验" or "流浪者的经验") return "角色经验材料";
        return "未知";
    }

    internal static Model Read(string root)
    {
        var m = new Model { Root = root ?? "" };
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) { m.Warning = "BGI 根目录不可用"; return m; }
        var user = Path.Combine(root, "User");
        foreach (var f in new[] { BlackFile, FuzzyBlackFile, BlackAllowFile, WhiteAllowFile, WhiteExcludeFile, WhiteFuzzyFile })
        {
            var values = BgiPickList.ReadLines(user, f, strict: true);
            if (f == BlackFile) m.BlackList = values; else if (f == FuzzyBlackFile) m.FuzzyBlackList = values; else if (f == BlackAllowFile) m.BlackAllowList = values; else if (f == WhiteAllowFile) m.WhiteAllowList = values; else if (f == WhiteExcludeFile) m.WhiteExcludeList = values; else m.WhiteFuzzyExcludeList = values;
        }
        m.TemplateBlackList = BgiPickList.ReadLines(user, TemplateBlackFile, strict: true);
        var cfgPath = Path.Combine(user, "config.json");
        if (File.Exists(cfgPath))
        {
            try
            {
                var doc = BgiJson.Read(cfgPath) as JsonObject;
                var ap = doc?["autoPickConfig"] as JsonObject ?? doc?["AutoPickConfig"] as JsonObject;
                if (ap != null)
                {
                    foreach (var entry in ap)
                        m.AutoPickConfig[JsonNamingPolicy.CamelCase.ConvertName(entry.Key)] = entry.Value?.DeepClone();
                    m.Mode = ModeOf(m.AutoPickConfig);
                }
                else m.Warning = "config.json 中未找到 AutoPickConfig";
            }
            catch (Exception ex) { m.Warning = "读取 AutoPickConfig 失败：" + ex.GetType().Name; }
        }
        else m.Warning = "未找到 User/config.json";
        m.DefaultBlackList = ReadJsonList(Asset(root, DefaultBlack));
        m.DefaultBlackFuzzy = ReadJsonList(Asset(root, BlackFuzzyDefault));
        m.DefaultWhiteList = ReadJsonList(Asset(root, DefaultWhite));
        m.DefaultWhiteFuzzy = ReadJsonMap(Asset(root, DefaultFuzzyWhite));
        var candidates = new[] { Path.Combine(root, "GameTask", "AutoPick", "Assets", "莫版模板", "templates.json"), Path.Combine(root, "BetterGenshinImpact", "GameTask", "AutoPick", "Assets", "莫版模板", "templates.json") };
        var templatePath = candidates.FirstOrDefault(File.Exists);
        if (templatePath == null) { m.Warning = string.IsNullOrEmpty(m.Warning) ? "未找到此 BGI 安装版本的莫版模板目录" : m.Warning + "；未找到莫版模板目录"; return m; }
        try
        {
            if (new FileInfo(templatePath).Length > 4 * 1024 * 1024) { m.Warning = "模板目录文件超过安全读取大小"; return m; }
            using var doc = JsonDocument.Parse(File.ReadAllText(templatePath, Encoding.UTF8));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) { m.Warning = "模板目录格式无效"; return m; }
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                string Get(string key) { foreach (var p in row.EnumerateObject()) if (p.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString() ?? ""; return ""; }
                var item = Get("itemName"); if (string.IsNullOrWhiteSpace(item)) item = Get("name"); if (string.IsNullOrWhiteSpace(item)) continue;
                var lenValue = row.EnumerateObject().FirstOrDefault(p => p.Name.Equals("len", StringComparison.OrdinalIgnoreCase)).Value;
                var color = Get("color");
                var classification = BgiPickTemplateCategories.Find(item);
                m.Templates.Add(new TemplateInfo(Get("name"), item, color,
                    classification?.Category ?? LegacyCategory(item, Get("category"), Get("file"), color),
                    classification?.Region ?? BgiPickTemplateCategories.CommonRegion,
                    lenValue.ValueKind == JsonValueKind.Number && lenValue.TryGetInt32(out var n) ? n : 0));
            }
            m.Templates = m.Templates.GroupBy(x => (x.ItemName, x.Color)).Select(x => x.First())
                .OrderBy(x => x.ItemName, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) { m.Warning = "读取模板目录失败：" + ex.GetType().Name; }
        return m;
    }

    internal static string ModeOf(JsonObject config) =>
        config["mode"] is JsonValue mode && mode.TryGetValue<string>(out var value) &&
        string.Equals(value, "Whitelist", StringComparison.OrdinalIgnoreCase) ? "Whitelist" : "Blacklist";

    internal static string Write(string root, Model model)
    {
        var user = Path.Combine(root, "User");
        var cfgPath = Path.Combine(user, "config.json");
        var rootNode = BgiJson.Read(cfgPath) as JsonObject
            ?? throw new InvalidOperationException("config.json 格式无效，已中止写入");
        var key = rootNode.ContainsKey("autoPickConfig") ? "autoPickConfig" : "AutoPickConfig";
        var current = rootNode[key] as JsonObject
            ?? throw new InvalidOperationException("config.json 中未找到 AutoPickConfig，已中止写入");
        var updated = (JsonObject)current.DeepClone();
        foreach (var entry in model.AutoPickConfig)
        {
            if (entry.Key.Equals("templatePick", StringComparison.OrdinalIgnoreCase)) continue;
            var diskKey = current.Select(x => x.Key).FirstOrDefault(x => x.Equals(entry.Key, StringComparison.OrdinalIgnoreCase)) ?? entry.Key;
            updated[diskKey] = entry.Value?.DeepClone();
        }

        var lists = new (string File, List<string> Items)[]
        {
            (BlackFile, model.BlackList), (FuzzyBlackFile, model.FuzzyBlackList),
            (BlackAllowFile, model.BlackAllowList), (WhiteAllowFile, model.WhiteAllowList),
            (WhiteExcludeFile, model.WhiteExcludeList), (WhiteFuzzyFile, model.WhiteFuzzyExcludeList),
            (TemplateBlackFile, model.TemplateBlackList),
        };
        var changedLists = lists.Where(list => list.Items != null &&
            !BgiPickList.ReadLines(user, list.File, strict: true).SequenceEqual(list.Items)).ToArray();
        var configChanged = !JsonNode.DeepEquals(current, updated);
        if (configChanged) rootNode[key] = updated;
        var configText = configChanged ? BgiJson.Serialize(rootNode) : null;

        foreach (var list in changedLists) Backup(Path.Combine(user, list.File));
        if (configChanged) Backup(cfgPath);
        foreach (var list in changedLists)
            BgiPickList.WriteLines(Path.Combine(user, list.File), list.Items);
        if (configText != null) BgiPickList.WriteAtomic(cfgPath, configText);
        model.Mode = ModeOf(new JsonObject { ["mode"] = (updated["mode"] ?? updated["Mode"])?.DeepClone() });
        return BgiBackup.DirFor(BgiBackup.KindPickList);

        static void Backup(string path)
        {
            if (File.Exists(path) && string.IsNullOrEmpty(BgiBackup.Save(BgiBackup.KindPickList, path)))
                throw new IOException("无法备份拾取配置，已中止写入");
        }
    }
}
