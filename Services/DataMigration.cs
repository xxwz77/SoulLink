using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>
    /// 旧版数据目录迁移。
    ///
    /// ★ 背景（2.21.0）：
    ///   2.20.0 把数据目录从 %LOCALAPPDATA%\&lt;身份&gt; 改到了 exe 同目录下的 data\。
    ///   但**老用户的 800 多 MB 数据还留在 C 盘旧位置**，程序不会自己去读。
    ///   用户升级后会以为"配置全丢了"，这是必须处理的体验问题。
    ///
    /// ★ 为什么不在启动时自动迁移：
    ///   ① 数据量大（实测 872 MB / 14.5 万文件），搬一半断电会留下烂摊子
    ///   ② 旧目录可能正被另一个版本（如 SoulLink-Stable.exe）使用
    ///   ③ 用户可能根本不想搬（C 盘空间够）
    ///   所以：**由用户显式点「迁移」**，且迁移过程可中断、结果可核对。
    ///
    /// ★ 迁移策略：复制而不是移动。
    ///   搬完旧数据仍在原地，用户确认新位置正常后再自己删。
    ///   宁可多占一份空间，也不能让用户丢失唯一一份数据。
    /// </summary>
    public static class DataMigration
    {
        public sealed class Plan
        {
            /// <summary>旧目录是否存在且有内容。</summary>
            public bool HasLegacy;
            /// <summary>旧目录路径。</summary>
            public string LegacyDir = "";
            /// <summary>旧目录总字节数。</summary>
            public long LegacyBytes;
            /// <summary>旧目录文件数。</summary>
            public int LegacyFiles;
            /// <summary>新目录路径。</summary>
            public string TargetDir = "";
            /// <summary>新目录是否已有数据（有的话迁移会跳过同名文件）。</summary>
            public bool TargetHasData;
            /// <summary>整体是否可迁移（旧有新无、且新旧不是同一个目录）。</summary>
            public bool CanMigrate;
            /// <summary>不能迁移时的原因。</summary>
            public string Reason = "";
        }

        public sealed class Result
        {
            public bool Ok;
            public int CopiedFiles;
            public int SkippedFiles;
            public int FailedFiles;
            public long CopiedBytes;
            public string Message = "";
        }

        /// <summary>分析当前是否可以迁移，供设置页展示。</summary>
        public static Plan Inspect()
        {
            var plan = new Plan();
            try
            {
                plan.LegacyDir = AppIdentity.LegacyConfigDir;
                plan.TargetDir = AppIdentity.DataDir;

                if (!Directory.Exists(plan.LegacyDir))
                {
                    plan.Reason = "没有找到旧版数据目录。";
                    return plan;
                }

                // 新旧是同一个目录（说明当前就在用旧位置），不需要迁移
                bool same;
                try
                {
                    same = string.Equals(
                        Path.GetFullPath(plan.LegacyDir).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(plan.TargetDir).TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase);
                }
                catch { same = false; }

                if (same)
                {
                    plan.Reason = "当前数据目录就是旧版本的位置，无需迁移。";
                    return plan;
                }

                // 统计旧目录
                try
                {
                    foreach (var f in Directory.EnumerateFiles(plan.LegacyDir, "*", SearchOption.AllDirectories))
                    {
                        plan.LegacyFiles++;
                        try { plan.LegacyBytes += new FileInfo(f).Length; } catch { }
                    }
                }
                catch { }

                plan.HasLegacy = plan.LegacyFiles > 0;

                try
                {
                    plan.TargetHasData = Directory.Exists(plan.TargetDir)
                        && Directory.EnumerateFileSystemEntries(plan.TargetDir).GetEnumerator().MoveNext();
                }
                catch { }

                if (!plan.HasLegacy)
                {
                    plan.Reason = "旧数据目录是空的。";
                    return plan;
                }

                plan.CanMigrate = true;
                return plan;
            }
            catch (Exception ex)
            {
                plan.Reason = "检查失败：" + ex.Message;
                return plan;
            }
        }

        /// <summary>
        /// 执行迁移：把旧目录整个复制到新目录。
        ///
        /// ★ 已存在的同名文件**不覆盖**：
        ///   新目录里的文件是程序刚建的（config.json / boot.log 等），
        ///   用户的真实配置在旧目录里 —— 但这不代表可以无脑覆盖，
        ///   万一新目录已经被用户改过呢？跳过并计数，让用户自己判断。
        ///   真正的取舍：宁可少拷，不可错盖。
        /// </summary>
        public static Result Migrate(IProgress<string>? progress = null, CancellationToken ct = default)
        {
            var r = new Result();
            var plan = Inspect();
            if (!plan.CanMigrate)
            {
                r.Message = plan.Reason;
                return r;
            }

            try
            {
                Directory.CreateDirectory(plan.TargetDir);
                CopyTree(plan.LegacyDir, plan.TargetDir, plan.LegacyDir, r, progress, ct);

                r.Ok = true;
                r.Message = "已复制 " + r.CopiedFiles + " 个文件（"
                          + FormatSize(r.CopiedBytes) + "）"
                          + (r.SkippedFiles > 0 ? "，跳过 " + r.SkippedFiles + " 个已存在文件" : "")
                          + (r.FailedFiles > 0 ? "，" + r.FailedFiles + " 个失败" : "")
                          + "。旧目录未删除，确认无误后可手动清理。";
            }
            catch (OperationCanceledException)
            {
                r.Message = "迁移已取消（已复制的文件保留）。";
            }
            catch (Exception ex)
            {
                r.Message = "迁移失败：" + ex.Message;
            }
            return r;
        }

        private static void CopyTree(string srcRoot, string dstRoot, string baseRoot,
                                     Result r, IProgress<string>? progress, CancellationToken ct)
        {
            foreach (var file in Directory.EnumerateFiles(srcRoot))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string full = Path.GetFullPath(file);
                    string baseFull = Path.GetFullPath(baseRoot)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string rel = full.Substring(baseFull.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string dst = Path.Combine(dstRoot, rel);

                    if (File.Exists(dst))
                    {
                        r.SkippedFiles++;
                        continue;
                    }

                    string? parent = Path.GetDirectoryName(dst);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent!);

                    long size = 0;
                    try { size = new FileInfo(full).Length; } catch { }

                    File.Copy(full, dst, overwrite: false);
                    r.CopiedFiles++;
                    r.CopiedBytes += size;

                    progress?.Report("正在复制：" + rel);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    r.FailedFiles++;   // 单个文件失败（被占用）不中断整体
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(srcRoot))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string name = Path.GetFileName(dir);
                    string dst = Path.Combine(dstRoot, name);
                    Directory.CreateDirectory(dst);
                    CopyTree(dir, dst, baseRoot, r, progress, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch { r.FailedFiles++; }
            }
        }

        /// <summary>
        /// 迁移完成后把旧目录里的**目录形态备份**转成 zip。
        ///
        /// ★ 2.21.1 新增。老用户的 bgi-backups 里是一堆 User_xxx\ 目录，
        ///   即使全部复制到新目录也不会自动变成压缩包。
        ///   这里在迁移后顺手转一遍，否则「改 zip」对老用户等于没生效。
        ///
        /// ★ 只转换已经在新数据目录里的，旧目录保持原样直到用户确认删除。
        /// </summary>
        public static string ConvertNewDirBackups(IProgress<string>? progress = null,
                                                  CancellationToken ct = default)
        {
            try
            {
                int n = BgiUpdater.ConvertLegacyDirsToZip(progress, ct);
                return n > 0 ? ("已把 " + n + " 份旧格式备份转成压缩包。") : "";
            }
            catch { return ""; }
        }

        /// <summary>迁移完成后清理旧目录（用户手动触发，二次确认过才调）。</summary>
        public static Result DeleteLegacy()
        {
            var r = new Result();
            try
            {
                string legacy = AppIdentity.LegacyConfigDir;
                if (!Directory.Exists(legacy))
                {
                    r.Ok = true;
                    r.Message = "旧数据目录已不存在。";
                    return r;
                }

                if (string.Equals(
                        Path.GetFullPath(legacy).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(AppIdentity.DataDir).TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                {
                    r.Message = "当前数据目录就是旧目录，不能删除。";
                    return r;
                }

                Directory.Delete(legacy, recursive: true);
                r.Ok = true;
                r.Message = "旧数据目录已删除：" + legacy;
            }
            catch (Exception ex)
            {
                r.Message = "删除失败（可能有文件被占用）：" + ex.Message;
            }
            return r;
        }

        public static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024 / 1024).ToString("F2") + " GB";
            if (bytes >= 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("F1") + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            return bytes + " B";
        }
    }
}
