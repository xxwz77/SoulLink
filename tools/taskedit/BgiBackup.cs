using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SoulLink.TaskEdit
{
    /// <summary>
    /// 备份落点与保留策略（用户 2026-11-25 裁定：备份放「主程序目录/data」）。
    ///
    /// ── 落点 ───────────────────────────────────────────────────────────
    ///   &lt;DataDir&gt;\backup\scriptgroup\  配置组
    ///   &lt;DataDir&gt;\backup\onedragon\    一条龙
    ///   &lt;DataDir&gt;\backup\picklist\     拾取名单
    ///
    ///   DataDir 由 <see cref="SoulLink.Services.AppIdentity.DataDir"/> 解析
    ///   （便携模式 = exe 同目录 \data；装在 Program Files 时回退 %LOCALAPPDATA%\&lt;身份&gt;）。
    ///
    /// ── 为什么不放在 BGI 的 User\ScriptGroup / User\OneDragon ──────────
    ///   那是用户的**生产目录**，BGI 自己已经在那里堆了 43 个 .bak（.compat-backup）。
    ///   我们不能再往里加 —— 会干扰用户、也会让「配置目录只读」这条红线变味。
    ///
    /// ── 附带好处 ──────────────────────────────────────────────────────
    ///   `data\` 本来就被更新包排除（AGENTS.md：禁止把 data 打进更新包），
    ///   所以备份不会被发布流程带走，也不会污染版本库。
    ///
    /// ── 命名与保留 ────────────────────────────────────────────────────
    ///   `&lt;原名&gt;.yyyyMMdd-HHmmss.bak`（可读、可排序、可人工恢复）
    ///   每类保留最近 <see cref="KeepPerKind"/> 份，超出按时间删最旧。
    /// </summary>
    internal static class BgiBackup
    {
        /// <summary>每类对象保留最近几份。可由环境变量 SOULLINK_TASKEDIT_BACKUP_KEEP 覆盖（测试用）。</summary>
        internal static int KeepPerKind
        {
            get
            {
                try
                {
                    string env = Environment.GetEnvironmentVariable("SOULLINK_TASKEDIT_BACKUP_KEEP") ?? "";
                    if (int.TryParse(env, out int n) && n >= 1 && n <= 1000) return n;
                }
                catch { }
                return 10;
            }
        }

        internal const string KindScriptGroup = "scriptgroup";
        internal const string KindOneDragon = "onedragon";
        internal const string KindPickList = "picklist";

        /// <summary>某类备份的根目录。</summary>
        internal static string DirFor(string kind)
        {
            string safe = SanitizeKind(kind);
            return Path.Combine(SoulLink.Services.AppIdentity.DataDir, "backup", safe);
        }

        /// <summary>
        /// 备份一个文件到 &lt;DataDir&gt;\backup\&lt;kind&gt;\&lt;原名&gt;.yyyyMMdd-HHmmss.bak，
        /// 然后裁剪到 <see cref="KeepPerKind"/> 份。返回备份文件全路径（失败返回空串）。
        /// </summary>
        internal static string Save(string kind, string sourcePath, string account = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return "";
                string dir = DirFor(kind);
                if (account != null)
                {
                    // 账号名单来自配置；仍限制为单个安全目录名。
                    DragonFiles.FilePath(dir, account);
                    dir = DragonFiles.CheckPath(Path.Combine(dir, account));
                }
                Directory.CreateDirectory(dir);

                string stem = Path.GetFileNameWithoutExtension(sourcePath);
                string ext = Path.GetExtension(sourcePath);            // .json / .txt
                // ★ 时间戳精确到毫秒：同一秒内连续保存不会撞名，也就不需要 -1/-2 后缀。
                //   仍保留一个冲突兜底（极端时钟回拨/同毫秒），但正常情况下永不触发。
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                string dest = Path.Combine(dir, stem + "." + stamp + ".bak");
                int n = 1;
                while (File.Exists(dest))
                    dest = Path.Combine(dir, stem + "." + stamp + "_" + (n++) + ".bak");

                File.Copy(sourcePath, dest, false);
                PruneDirectory(dir, stem);
                return dest;
            }
            catch (Exception) { return ""; }
        }

        /// <summary>裁剪：同一原名只保留最近 KeepPerKind 份。</summary>
        internal static int Prune(string kind, string stem) => PruneDirectory(DirFor(kind), stem);

        private static int PruneDirectory(string dir, string stem)
        {
            int removed = 0;
            try
            {
                if (!Directory.Exists(dir)) return 0;
                // 精确匹配 <stem>.*.bak，避免误删别的对象
                var files = Directory.GetFiles(dir, stem + ".*.bak")
                    .Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f).Substring(stem.Length), @"^[.]\d{8}-\d{6}-\d{3}(_\d+)?[.]bak$"))
                    .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                for (int i = KeepPerKind; i < files.Count; i++)
                {
                    try { File.Delete(files[i]); removed++; } catch { }
                }
            }
            catch { }
            return removed;
        }

        /// <summary>列出某类备份（诊断/界面用）。</summary>
        internal static IReadOnlyList<string> List(string kind)
        {
            try
            {
                string dir = DirFor(kind);
                if (!Directory.Exists(dir)) return Array.Empty<string>();
                return Directory.GetFiles(dir, "*.bak")
                    .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>
        /// 从备份恢复到原位置。返回 true 表示成功。
        /// ★ 恢复前会先把当前位置再备份一次（避免恢复动作本身造成不可逆损失）。
        /// </summary>
        internal static bool Restore(string kind, string backupPath, string targetPath)
        {
            try
            {
                if (!File.Exists(backupPath)) return false;
                if (File.Exists(targetPath)) Save(kind, targetPath);   // 先保当前
                string dir = Path.GetDirectoryName(targetPath) ?? "";
                if (dir.Length > 0) Directory.CreateDirectory(dir);
                string tmp = targetPath + ".restore.tmp";
                File.Copy(backupPath, tmp, true);
                File.Move(tmp, targetPath, true);
                return true;
            }
            catch { return false; }
        }

        private static string SanitizeKind(string kind)
        {
            string k = (kind ?? "").Trim().ToLowerInvariant();
            if (k.Length == 0) return "other";
            var sb = new System.Text.StringBuilder(k.Length);
            foreach (char c in k)
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            string s = sb.ToString();
            return s.Length == 0 ? "other" : s;
        }
    }
}
