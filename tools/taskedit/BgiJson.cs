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
    /// BGI 配置的序列化与原子写（设计文档 §16.2，字节级兼容）。
    ///
    /// ── 实测 A1 真实文件的序列化风格（必须逐字节对齐）──────────────
    ///   · 编码 UTF-8 无 BOM；换行 CRLF（\r\n）；
    ///   · 缩进 2 空格；字段顺序 = 类声明顺序；
    ///   · null 保留（不省略）；中文原样输出（不做 \uXXXX 转义）；
    ///   · 结尾无换行；顶层无末尾逗号。
    ///
    /// ── 为什么不用强类型模型 + 为什么用 JsonNode ─────────────────
    ///   配置组/一条龙的字段会随 BGI 版本演进，硬编码 POCO 必然丢新字段。
    ///   用 JsonNode 读入、原样改动、再写回，未知字段天然保留。
    /// </summary>
    internal static class BgiJson
    {
        /// <summary>
        /// 与 BGI ConfigService.JsonOptions 对齐的序列化选项：
        ///   CamelCase（字段是小写开头）、UnsafeRelaxedJsonEscaping（中文不转义）、
        ///   WriteIndented（2 空格缩进）。<b>不设 Encoder 会转义中文，必须显式 UnsafeRelaxed</b>。
        /// </summary>
        internal static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
        };

        /// <summary>
        /// 用 CRLF 改写 JsonNode 序列化结果（System.Text.Json 默认 LF），
        /// 使其与 BGI 实机写出的文件逐字节一致。
        /// </summary>
        internal static string Serialize(JsonNode root)
        {
            string json = root.ToJsonString(Options);
            // 2 空格缩进 + CRLF + 无末尾换行
            return json.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        /// <summary>读 JSON 为 JsonNode，保留未知字段与 null。</summary>
        internal static JsonNode Read(string path)
        {
            string text = File.ReadAllText(path, new UTF8Encoding(false));
            JsonNode node = JsonNode.Parse(text, nodeOptions: null, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) ?? new JsonObject();
            return node;
        }

        /// <summary>
        /// 原子写：临时文件 + File.Move(overwrite:true)（对齐 BGI WriteToFileAtomically）。
        /// 写前先备份；写失败从备份回滚。
        /// </summary>
        internal static string WriteAtomic(string path, string json, string backupDir)
        {
            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full) ?? throw new ArgumentException("路径无效", nameof(path));
            Directory.CreateDirectory(dir);

            // ★ 备份落点（t27 按用户裁定改为 <DataDir>\backup\<类型>\）：
            //   调用方传的 backupDir 参数保留以兼容旧调用，但**不再使用** ——
            //   原先它指向 BGI 的 User\.taskedit-backup，那会往用户生产目录里堆文件。
            //   类型由目标文件所在目录推断（ScriptGroup / OneDragon / 其它）。
            string backup = BgiBackup.Save(KindOf(full), full);

            string tmp = Path.Combine(dir, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                File.Move(tmp, full, overwrite: true);
                return backup;
            }
            catch
            {
                // 回滚：从备份恢复
                try { if (File.Exists(backup)) File.Copy(backup, full, overwrite: true); } catch { }
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }

        /// <summary>
        /// 按目标文件路径推断备份类型（决定放进 backup\scriptgroup 还是 backup\onedragon）。
        /// </summary>
        internal static string KindOf(string targetPath)
        {
            string p = (targetPath ?? "").Replace('\\', '/');
            if (p.IndexOf("/ScriptGroup/", StringComparison.OrdinalIgnoreCase) >= 0) return BgiBackup.KindScriptGroup;
            if (p.IndexOf("/OneDragon/", StringComparison.OrdinalIgnoreCase) >= 0) return BgiBackup.KindOneDragon;
            if (p.EndsWith("pick_black_lists.txt", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith("pick_fuzzy_black_lists.txt", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith("/config.json", StringComparison.OrdinalIgnoreCase)) return BgiBackup.KindPickList;
            return "other";
        }

        /// <summary>备份到 backupDir（带时间戳），保留最近 N 份。</summary>
        internal static string Backup(string path, string backupDir, int keep = 3)
        {
            if (!File.Exists(path)) return "";
            Directory.CreateDirectory(backupDir);
            string name = Path.GetFileName(path);
            string stamp = DateTime.Now.ToString("yyyyMMddHHmmssfff");
            string dest = Path.Combine(backupDir, name + ".bak-" + stamp);
            File.Copy(path, dest, overwrite: true);

            // 清理旧备份：同名文件只留最近 keep 份
            var olds = Directory.GetFiles(backupDir, name + ".bak-*")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .Skip(keep)
                .ToList();
            foreach (string old in olds) { try { File.Delete(old); } catch { } }
            return dest;
        }
    }
}
