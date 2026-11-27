using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SoulLink.TaskEdit
{
    /// <summary>
    /// 配置读取层（设计文档 §A）。只读/写 BGI 配置，绝不启停 BGI。
    ///
    /// ── 目录约定 ───────────────────────────────────────────────────
    ///   配置组  <bgiRoot>\User\ScriptGroup\*.json
    ///   一条龙  <bgiRoot>\User\OneDragon\*.json
    ///   JS 脚本 <bgiRoot>\User\JsScript\**（每目录含 manifest.json）
    ///   地图追踪 <bgiRoot>\User\AutoPathing\**（*.json 路线）
    ///   拾取名单 <bgiRoot>\User\pick_black_lists.txt / pick_fuzzy_black_lists.txt
    /// </summary>
    internal static class BgiStore
    {
        internal static string ScriptGroupDir(string root) => Path.Combine(root, "User", "ScriptGroup");
        internal static string OneDragonDir(string root) => Path.Combine(root, "User", "OneDragon");
        internal static string JsScriptDir(string root) => Path.Combine(root, "User", "JsScript");
        internal static string AutoPathingDir(string root) => Path.Combine(root, "User", "AutoPathing");

        // ── 枚举 ──────────────────────────────────────────────────────

        internal static List<string> ListGroups(string root)
        {
            return ListJson(ScriptGroupDir(root));
        }

        internal static List<string> ListOneDragons(string root)
        {
            return ListJson(DragonFiles.CheckPath(OneDragonDir(root)));
        }

        private static List<string> ListJson(string dir)
        {
            var result = new List<string>();
            if (!Directory.Exists(dir)) return result;
            foreach (string f in Directory.GetFiles(dir, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;   // 跳过隐藏/临时
                result.Add(name);
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        // ── 读取单个 ──────────────────────────────────────────────────

        internal static JsonNode ReadGroup(string root, string name)
        {
            string file = DragonFiles.FilePath(ScriptGroupDir(root), name);
            if (!File.Exists(file)) throw new FileNotFoundException("配置组不存在：" + name);
            return BgiJson.Read(file);
        }

        internal static JsonNode ReadOneDragon(string root, string name)
        {
            string file = DragonFiles.FilePath(OneDragonDir(root), name);
            if (!File.Exists(file)) throw new FileNotFoundException("一条龙不存在：" + name);
            return BgiJson.Read(file);
        }

        // ── 可添加项 ──────────────────────────────────────────────────

        /// <summary>JS 脚本可添加列表：JsScript 下含 manifest.json 的子目录。</summary>
        internal static List<string> ListPickableJs(string root)
        {
            var result = new List<string>();
            string dir = JsScriptDir(root);
            if (!Directory.Exists(dir)) return result;
            foreach (string sub in Directory.GetDirectories(dir))
            {
                if (File.Exists(Path.Combine(sub, "manifest.json"))) result.Add(Path.GetFileName(sub));
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>
        /// 地图追踪路线可添加列表。BGI 的「添加地图追踪任务」扫描
        /// User\AutoPathing 下的**顶层 *.json + 一层子目录内的 *.json**（相对路径），
        /// 不无限递归（更深层是「当前线路版本」等管理目录，不是可添加路线）。
        /// 实测 A1：顶层 11 个 json + 9 个分类子目录（CD采集路线/地方传奇/敌人与魔物…）。
        /// </summary>
        internal static List<string> ListPickablePathing(string root)
        {
            var result = new List<string>();
            string dir = AutoPathingDir(root);
            if (!Directory.Exists(dir)) return result;

            // 顶层 json
            foreach (string f in Directory.GetFiles(dir, "*.json"))
                result.Add(Path.GetFileName(f));

            // 一层子目录内的 json（相对路径）
            foreach (string sub in Directory.GetDirectories(dir))
            {
                string subName = Path.GetFileName(sub);
                if (subName.StartsWith(".", StringComparison.Ordinal)) continue;
                if (subName.Equals("current", StringComparison.OrdinalIgnoreCase)) continue;
                if (subName.Equals("installed", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string f in Directory.GetFiles(sub, "*.json", SearchOption.TopDirectoryOnly))
                    result.Add(subName + "/" + Path.GetFileName(f));
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>
        /// 独立任务可添加列表：内置 8 项 + 配置里已出现的自定义 SoloTask 名。
        /// （JS 注册的自定义项来源见设计文档 §13.2，注册表精确机制待实现任务定位；
        ///   本层退化为「内置 8 项 + 现有配置里见过的 SoloTask 名」合并去重。）
        /// </summary>
        internal static List<string> ListPickableSoloTasks(string root)
        {
            var result = new List<string>
            {
                "AutoGeniusInvokation", "AutoWood", "AutoFight", "AutoDomain",
                "AutoTrack", "AutoTrackPath", "AutoMusicGame", "AutoPathing",
            };
            // 从所有配置组里收集已出现的 SoloTask 名（folderName 或 name）
            foreach (string g in ListGroups(root))
            {
                try
                {
                    JsonNode node = ReadGroup(root, g);
                    if (node["projects"] is JsonArray arr)
                    {
                        foreach (var p in arr.OfType<JsonObject>())
                        {
                            string type = p["type"]?.GetValue<string>() ?? "";
                            if (type != "SoloTask") continue;
                            string soloName = p["folderName"]?.GetValue<string>() ?? "";
                            if (string.IsNullOrWhiteSpace(soloName)) soloName = p["name"]?.GetValue<string>() ?? "";
                            if (!string.IsNullOrWhiteSpace(soloName) && !result.Contains(soloName, StringComparer.OrdinalIgnoreCase))
                                result.Add(soloName);
                        }
                    }
                }
                catch { }
            }
            return result;
        }

        // ── 写入 ──────────────────────────────────────────────────────

        internal static string WriteGroup(string root, string name, JsonNode node, string backupDir)
        {
            string file = DragonFiles.FilePath(ScriptGroupDir(root), name);
            string json = BgiJson.Serialize(node);
            return BgiJson.WriteAtomic(file, json, backupDir);
        }

    }
}
