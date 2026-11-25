using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SoulLink.TaskEdit
{
    /// <summary>
    /// 拾取名单读写（设计文档 §16.1）。
    ///
    /// ── 真实文件布局（A2 实测）──────────────────────────────────────────
    ///   &lt;BGI根&gt;\User\config.json → `autoPickConfig`（21 个字段）
    ///       · `mode`: "Blacklist" | "Whitelist"
    ///       · `enabled`: 基础拾取总开关
    ///       · `blacklistModePickEnabled` / `whitelistModeDoNotPickEnabled`: 两个模式内的**反向**开关
    ///   &lt;BGI根&gt;\User\pick_black_lists.txt        黑名单（UTF-8 无 BOM，**每行一个物品名**）
    ///   &lt;BGI根&gt;\User\pick_fuzzy_black_lists.txt  模糊黑名单（关键字匹配）
    ///
    /// ── 安全 ─────────────────────────────────────────────────────────
    ///   · 只碰上面三个已知路径，**绝不做路径拼接**（文件名是常量）；
    ///   · 写前备份到 `User\.taskedit-backup\`（与配置组写入同一套）；
    ///   · 原子写（临时文件 + File.Move overwrite）；
    ///   · 行内容做最小清洗：去首尾空白、丢弃空行、**拒绝含换行的项**（防注入多行）。
    /// </summary>
    internal static class BgiPickList
    {
        internal sealed class Model
        {
            internal bool Enabled;
            internal string Mode = "Blacklist";
            internal bool BlacklistModePickEnabled;
            internal bool WhitelistModeDoNotPickEnabled;
            internal List<string> BlackList = new();
            internal List<string> FuzzyBlackList = new();
            /// <summary>config.json 里 autoPickConfig 的其它字段原样保留（不丢用户设置）。</summary>
            internal JsonObject OtherConfig = new();
        }

        private const string BlackFile = "pick_black_lists.txt";
        private const string FuzzyFile = "pick_fuzzy_black_lists.txt";

        /// <summary>读取名单。BGI 根为空时返回 null。</summary>
        internal static Model? Read(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
            string userDir = Path.Combine(root, "User");
            var model = new Model();

            // ── config.json 的 autoPickConfig ──
            string cfgPath = Path.Combine(userDir, "config.json");
            if (File.Exists(cfgPath))
            {
                try
                {
                    var rootNode = JsonNode.Parse(File.ReadAllText(cfgPath, Encoding.UTF8)) as JsonObject;
                    if (rootNode?["autoPickConfig"] is JsonObject ap)
                    {
                        model.Enabled = ap["enabled"]?.GetValue<bool>() ?? false;
                        model.Mode = ap["mode"]?.GetValue<string>() ?? "Blacklist";
                        model.BlacklistModePickEnabled = ap["blacklistModePickEnabled"]?.GetValue<bool>() ?? false;
                        model.WhitelistModeDoNotPickEnabled = ap["whitelistModeDoNotPickEnabled"]?.GetValue<bool>() ?? false;
                        foreach (var kv in ap.ToList()) model.OtherConfig[kv.Key] = kv.Value?.DeepClone();
                    }
                }
                catch (Exception) { /* 配置坏了不阻断：名单文件仍可读写 */ }
            }

            model.BlackList = ReadLines(userDir, BlackFile);
            model.FuzzyBlackList = ReadLines(userDir, FuzzyFile);
            return model;
        }

        /// <summary>
        /// 写入名单。返回备份目录（失败抛异常，由 API 层转 500）。
        /// </summary>
        internal static string Write(string root, Model model, string backupDir)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("BGI 根目录未确定");
            string userDir = Path.Combine(root, "User");
            Directory.CreateDirectory(userDir);
            Directory.CreateDirectory(backupDir);

            string cfgPath = Path.Combine(userDir, "config.json");

            // ── ① 备份（三个文件都备）──
            // ★ t27：备份改到 <DataDir>\backup\picklist\（用户裁定「备份放主程序目录/data」），
            //   不再写进 BGI 的 User\.taskedit-backup —— 那里是用户生产目录。
            BgiBackup.Save(BgiBackup.KindPickList, cfgPath);
            BgiBackup.Save(BgiBackup.KindPickList, Path.Combine(userDir, BlackFile));
            BgiBackup.Save(BgiBackup.KindPickList, Path.Combine(userDir, FuzzyFile));

            // ── ② 写名单文本（UTF-8 无 BOM，行尾 CRLF （与 BGI 一致）──
            WriteLines(Path.Combine(userDir, BlackFile), model.BlackList);
            WriteLines(Path.Combine(userDir, FuzzyFile), model.FuzzyBlackList);

            // ── ③ 更新 config.json 的 autoPickConfig（保留其余字段）──
            JsonObject rootObj;
            if (File.Exists(cfgPath))
            {
                try { rootObj = JsonNode.Parse(File.ReadAllText(cfgPath, Encoding.UTF8)) as JsonObject ?? new JsonObject(); }
                catch (Exception) { throw new InvalidOperationException("config.json 格式无效，已中止写入（原文件未改）"); }
            }
            else rootObj = new JsonObject();

            var ap = rootObj["autoPickConfig"] as JsonObject ?? new JsonObject();
            // 先在已有的 autoPickConfig 上覆盖我们管的 4 个字段，再把 OtherConfig 里缺的补上
            foreach (var kv in model.OtherConfig)
                if (ap[kv.Key] == null) ap[kv.Key] = kv.Value?.DeepClone();
            ap["enabled"] = model.Enabled;
            ap["mode"] = string.Equals(model.Mode, "Whitelist", StringComparison.OrdinalIgnoreCase) ? "Whitelist" : "Blacklist";
            ap["blacklistModePickEnabled"] = model.BlacklistModePickEnabled;
            ap["whitelistModeDoNotPickEnabled"] = model.WhitelistModeDoNotPickEnabled;
            rootObj["autoPickConfig"] = ap;

            WriteAtomic(cfgPath, rootObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return BgiBackup.DirFor(BgiBackup.KindPickList);
        }

        // ── 行级读写 ────────────────────────────────────────────────

        internal static List<string> ReadLines(string userDir, string file)
        {
            var list = new List<string>();
            string path = Path.Combine(userDir, file);
            if (!File.Exists(path)) return list;
            try
            {
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = (raw ?? "").Trim();
                    if (line.Length == 0) continue;           // BGI 文件里可能有空行
                    list.Add(line);
                }
            }
            catch (Exception) { }
            return list;
        }

        internal static void WriteLines(string path, List<string> items)
        {
            var sb = new StringBuilder();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in items ?? new List<string>())
            {
                string line = Sanitize(raw);
                if (line.Length == 0) continue;
                if (!seen.Add(line)) continue;                // 去重
                sb.Append(line).Append("\r\n");              // BGI 用 CRLF
            }
            WriteAtomic(path, sb.ToString());
        }

        /// <summary>
        /// ★ 单行清洗：去掉换行/制表等控制字符，防止用户输入把一行拆成多行
        ///   从而伪造出额外的名单项。
        /// </summary>
        internal static string Sanitize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw.Trim())
            {
                if (c == '\r' || c == '\n' || c == '\t' || char.IsControl(c)) continue;
                sb.Append(c);
            }
            string s = sb.ToString().Trim();
            return s.Length > 64 ? s.Substring(0, 64) : s;    // 物品名不该超过 64 字符
        }

        // ── 原子写 + 备份 ────────────────────────────────────────────

        internal static void WriteAtomic(string path, string content)
        {
            string tmp = path + ".taskedit.tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            File.Move(tmp, path, true);
        }

        private static void Backup(string path, string backupDir, string label, string stamp)
        {
            try
            {
                if (!File.Exists(path)) return;
                string dest = Path.Combine(backupDir, label + "." + stamp + ".bak");
                File.Copy(path, dest, true);
                Prune(backupDir, label, 3);
            }
            catch (Exception) { /* 备份失败不阻断，但写入方仍会继续（与原 BgiStore 一致） */ }
        }

        private static void Prune(string backupDir, string label, int keep)
        {
            try
            {
                var files = Directory.GetFiles(backupDir, label + ".*.bak")
                    .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                for (int i = keep; i < files.Count; i++)
                    try { File.Delete(files[i]); } catch { }
            }
            catch { }
        }
    }
}
