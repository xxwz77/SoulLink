using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>
    /// 路线更新器：把路线包里的「第一层文件夹」替换进用户指定的路线根目录。
    ///
    /// ★ 规则（用户明确要求，不要擅自扩大删除范围）：
    ///   · 目标根目录由用户指定，例如 &lt;BGI 目录&gt;\User\AutoPathing
    ///   · 只看压缩包**第一层**的文件夹；
    ///   · 目标根目录里**只有同名文件夹**会被删除，别的文件夹和文件一律不动；
    ///   · 然后把包内第一层文件夹原样放进去。
    ///
    /// ★ 安全约束：
    ///   · 删除前把被替换的文件夹移到 %LOCALAPPDATA%\SoulLink\route-backups（可回滚）；
    ///   · 不碰 BGI 进程（路线是纯数据文件夹，BGI 空闲时替换即可）。
    ///
    /// ★ 为什么不复用 BgiUpdater：
    ///   它是「整包覆盖到 BGI 目录」的语义，会把包内所有文件铺到 BGI 根目录，
    ///   和这里「只替换同名文件夹」的要求完全不同。
    /// </summary>
    public static class RouteUpdater
    {
        /// <summary>
        /// 云端下载同一时刻只允许一个（手动更新与定时更新可能同时触发）。
        /// </summary>
        private static readonly SemaphoreSlim DownloadGate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// 云端包缓存目录（<see cref="AppIdentity.DataDir"/>\route-cache）。
        ///
        /// ★ 2.21.1：跟随数据目录，不再写死 %LOCALAPPDATA%。
        ///   路线更新的下载包动辄几十 MB，压在 C 盘是用户明确反馈过的问题。
        /// </summary>
        public static string CacheRoot
        {
            get
            {
                string dir = Path.Combine(AppIdentity.DataDir, "route-cache");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>
        /// 路线更新备份目录（<see cref="AppIdentity.DataDir"/>\route-backups）。
        ///
        /// ★ 2.21.1：新增。之前这个路径在两处内联拼 %LOCALAPPDATA%，
        ///   既没跟随数据目录，也**没有任何清理机制**——每次更新路线都往
        ///   这里堆一份完整旧目录，只增不减。实测单次几十 MB 到上百 MB。
        /// </summary>
        public static string BackupRoot
        {
            get
            {
                string dir = Path.Combine(AppIdentity.DataDir, "route-backups");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>
        /// 清理旧的路线更新备份，保留最近 <paramref name="keep"/> 个（&lt;=0 = 不限制）。
        ///
        /// ★ 2.21.1 新增。之前完全没有清理，是「数据目录无限膨胀」的第二个来源
        ///   （第一个是 bgi-backups）。
        /// ★ 按目录名排序即可：名字是 yyyyMMdd_HHmmss_xxx，字典序就是时间序。
        /// </summary>
        public static int PruneBackups(int keep)
        {
            if (keep <= 0) return 0;
            int removed = 0;
            try
            {
                string root = BackupRoot;
                if (!Directory.Exists(root)) return 0;

                var dirs = Directory.GetDirectories(root);
                if (dirs.Length <= keep) return 0;

                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < dirs.Length - keep; i++)
                {
                    try { Directory.Delete(dirs[i], recursive: true); removed++; }
                    catch { /* 被占用：下次再说 */ }
                }
            }
            catch { }
            return removed;
        }

        /// <summary>路线备份当前占用（字节），给设置页显示。</summary>
        public static long GetBackupSize()
        {
            try
            {
                string root = BackupRoot;
                if (!Directory.Exists(root)) return 0;
                long sum = 0;
                foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    try { sum += new FileInfo(f).Length; } catch { }
                }
                return sum;
            }
            catch { return 0; }
        }

        // ══════════ 进度回报 ══════════

        public sealed class Progress
        {
            /// <summary>当前在做什么（显示给用户）。</summary>
            public string Text = "";
            /// <summary>0-100，-1 表示不确定。</summary>
            public int Percent = -1;
            /// <summary>true = 这行要写进日志区；false = 只更新状态/进度条。</summary>
            public bool IsLog;
        }

        /// <summary>压缩包里将要落地的第一层文件夹（更新前预览用）。</summary>
        public sealed class PackageFolder
        {
            /// <summary>文件夹名（落地到目标根目录后的名字）。</summary>
            public string Name = "";
            /// <summary>包内文件数（含子目录）。</summary>
            public int FileCount;
            /// <summary>包内总字节数。</summary>
            public long TotalBytes;
            /// <summary>目标根目录里是否已存在同名文件夹（= 会被删除替换）。</summary>
            public bool Replaces;
        }

        /// <summary>包里第一层散落文件（不会被处理，只提示）。</summary>
        public sealed class LooseFileInfo
        {
            public int Count;
            public List<string> Names = new List<string>();
        }

        /// <summary>更新结果。</summary>
        public sealed class Result
        {
            public bool Ok;
            public string Message = "";
            /// <summary>本次替换（删除后重新放入）的文件夹名。</summary>
            public List<string> Replaced = new List<string>();
            /// <summary>本次新增（目标里原本没有）的文件夹名。</summary>
            public List<string> Added = new List<string>();
            /// <summary>被替换文件夹移入的备份目录（空 = 直接删除）。</summary>
            public string BackupDir = "";
            /// <summary>本次放入的文件总数。</summary>
            public int FileCount;
        }

        // ══════════ 主流程 ══════════

        /// <summary>
        /// 执行路线更新。
        ///
        /// 流程：
        ///   ① 解压到临时目录（只解压一次）
        ///   ② 列出包内第一层文件夹，与目标根目录比对（同名 = 待替换）
        ///   ③ 把目标里同名的文件夹移进备份目录
        ///   ④ 把包内第一层文件夹整体复制进去
        ///   ⑤ 清理临时目录
        /// </summary>
        public static async Task<Result> UpdateAsync(
            string archivePath,
            string targetRoot,
            Action<string> log,
            IProgress<Progress>? progress = null,
            CancellationToken ct = default)
        {
            var result = new Result();
            void Log(string s) => log(s);

            string? tempDir = null;

            try
            {
                // ── 校验 ──
                if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                    throw new InvalidOperationException("找不到路线包：" + archivePath);

                string problem = ValidateTargetRoot(targetRoot, out targetRoot);
                if (problem.Length > 0)
                    throw new InvalidOperationException(problem);

                progress?.Report(new Progress { Text = "准备中…" });

                // ── ① 解压 ──
                Log("═══ ① 解压路线包 ═══");
                tempDir = Path.Combine(Path.GetTempPath(),
                    "SoulLink_RouteUpdate_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(tempDir);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                string contentRoot = await ExtractAsync(archivePath, tempDir, Log, progress, ct)
                    .ConfigureAwait(false);
                Log("│ ① 耗时 " + Sec(sw));

                ct.ThrowIfCancellationRequested();

                // ── ② 核对包内文件夹 ──
                Log("");
                Log("═══ ② 核对包内文件夹 ═══");

                var folders = ListTopFolders(contentRoot);
                if (folders.Count == 0)
                    throw new InvalidOperationException(
                        "路线包里没有任何第一层文件夹。\n\n" +
                        @"路线包应为：压缩包 → 文件夹 → 路线文件，例如「pathing\12小时狗粮\…」。");

                // ★ 剥掉「包裹层」：真实更新包解压后是 pathing\12小时狗粮\…，
                //   而目标是 AutoPathing\CD采集路线\12小时狗粮\…，
                //   不剥就会变成 CD采集路线\pathing\12小时狗粮（多一层，跑不起来）。
                string wrapperName = "";
                string staleLeftover = "";

                string unwrapped = StripWrapper(contentRoot, targetRoot, out wrapperName, out string stripNote);
                if (!string.Equals(unwrapped, contentRoot, StringComparison.OrdinalIgnoreCase))
                {
                    Log("│ " + stripNote);
                    folders = ListTopFolders(unwrapped);
                    if (folders.Count == 0)
                        throw new InvalidOperationException("剥掉包裹层后没有任何路线文件夹");
                }

                var loose = ListTopFiles(contentRoot);
                if (loose.Count > 0)
                {
                    Log("│ ⚠ 包内第一层还有 " + loose.Count + " 个散落文件，按规则不处理：");
                    foreach (var n in loose.Names.Take(8)) Log("│    " + n);
                    if (loose.Count > 8) Log("│    …还有 " + (loose.Count - 8) + " 个");
                }

                foreach (var dir in folders)
                {
                    ct.ThrowIfCancellationRequested();
                    var info = Fold(dir);
                    Log("│ " + info.Name + "  （" + info.FileCount + " 个文件，"
                        + Size(info.TotalBytes) + "）");
                }

                var names = new HashSet<string>(
                    folders.Select(FolderName), StringComparer.OrdinalIgnoreCase);

                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var d in Directory.GetDirectories(targetRoot))
                        existing.Add(Path.GetFileName(d));
                }
                catch { }

                var toReplace = names.Where(existing.Contains)
                                      .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                var toAdd = names.Where(n => !existing.Contains(n))
                                 .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                result.Replaced.AddRange(toReplace);
                result.Added.AddRange(toAdd);

                // ★ 自愈：清理历史遗留的「包裹层残骸」。
                //   旧版本（2.16.0）会在目标里建出一个 pathing 目录，里面是整个包的内容。
                //   它不属于路线库，留着既占空间，又会让后续每次都被误判成"要替换的同名文件夹"。
                //
                //   判定极保守，必须同时满足：
                //     · 名字等于本次包内被剥掉的包裹层名；
                //     · 子项集合与包内第一层完全一致（内容确实是这个包）；
                //     · 不在本次要正常放入的列表里。
                if (wrapperName.Length > 0 && !names.Contains(wrapperName))
                {
                    string stale = Path.Combine(targetRoot, wrapperName);
                    string pkgWrapper = Path.Combine(contentRoot, wrapperName);

                    // 拿"包里的包裹层目录"和"目标里的残骸目录"比第一层子项是否一致
                    if (Directory.Exists(stale) && Directory.Exists(pkgWrapper)
                        && SameChildren(stale, pkgWrapper))
                    {
                        Log("│ 发现旧版本遗留的包裹层目录「" + wrapperName + "」，将被清理");
                        staleLeftover = stale;
                    }
                }

                Log("│ 目标根目录：" + targetRoot);
                Log("│ 将要替换（同名）：" + (toReplace.Count == 0 ? "无" : string.Join("、", toReplace)));
                Log("│ 将要新增：" + (toAdd.Count == 0 ? "无" : string.Join("、", toAdd)));
                Log("│ 其余文件夹与文件不会改动");

                ct.ThrowIfCancellationRequested();

                // ── ③ 删除同名文件夹（先移进备份目录）──
                Log("");
                Log("═══ ③ 删除同名文件夹 ═══");

                if (toReplace.Count > 0)
                {
                    // ★ 备份目录必须按「目标 + 一次性后缀」区分：
                    //   批量更新时多个目标在同一秒执行，若只用时间戳会撞成同一个目录，
                    //   第二个目标 Move 失败 → 退化成直接删除，旧文件就再也回不来了
                    //   （2.15.0 开发期实测踩到）。
                    // ★ 2.21.1：改走 BackupRoot（跟随数据目录）
                    string bakRoot = Path.Combine(
                        BackupRoot,
                        DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_"
                        + Path.GetFileName(targetRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                        + "_" + Guid.NewGuid().ToString("N").Substring(0, 4));
                    try { Directory.CreateDirectory(bakRoot); } catch { bakRoot = ""; }

                    foreach (var name in toReplace)
                    {
                        ct.ThrowIfCancellationRequested();
                        string target = Path.Combine(targetRoot, name);

                        if (bakRoot.Length > 0)
                        {
                            try
                            {
                                Directory.Move(target, Path.Combine(bakRoot, name));
                                Log("│ 已移入备份：" + name);
                                continue;
                            }
                            catch (Exception ex)
                            {
                                Log("│ 移入备份失败（" + ex.Message + "），改为直接删除：" + name);
                            }
                        }

                        try
                        {
                            Directory.Delete(target, true);
                            Log("│ 已删除：" + name);
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException(
                                "删除同名文件夹失败：" + name + "\n" + ex.Message +
                                "\n\n如果 BGI 正在使用这些路线，请先关闭 BGI 再试。");
                        }
                    }

                    if (bakRoot.Length > 0 && Directory.Exists(bakRoot))
                    {
                        result.BackupDir = bakRoot;
                        Log("│ 旧文件夹备份在：" + bakRoot);
                    }
                }
                else
                {
                    Log("│ 没有同名文件夹需要删除");
                }

                ct.ThrowIfCancellationRequested();

                // ── ③b 清理历史残骸 ──
                if (staleLeftover.Length > 0 && Directory.Exists(staleLeftover))
                {
                    try
                    {
                        // ★ 2.21.1：改走 BackupRoot（跟随数据目录）
                        string bak2 = Path.Combine(
                            BackupRoot,
                            DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_"
                            + FolderName(targetRoot) + "_stale_" + Guid.NewGuid().ToString("N").Substring(0, 4));
                        Directory.CreateDirectory(Path.GetDirectoryName(bak2) ?? bak2);

                        try
                        {
                            Directory.Move(staleLeftover, bak2);
                            Log("│ 已把残骸目录移入备份：" + Path.GetFileName(staleLeftover));
                        }
                        catch
                        {
                            Directory.Delete(staleLeftover, true);
                            Log("│ 已删除残骸目录：" + Path.GetFileName(staleLeftover));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("│ ⚠ 清理残骸目录失败：" + ex.Message);
                    }
                }

                // ── ④ 放入包内文件夹 ──
                Log("");
                Log("═══ ④ 放入包内文件夹 ═══");
                sw.Restart();

                int done = 0;
                foreach (var src in folders)
                {
                    ct.ThrowIfCancellationRequested();

                    string name = FolderName(src);
                    string dst = Path.Combine(targetRoot, name);

                    if (Directory.Exists(dst))
                    {
                        // 理论上第 ③ 步已经删掉；这里再兜一次，避免把内容叠进旧目录。
                        Log("│ ⚠ 目标已存在同名文件夹，跳过：" + name);
                        continue;
                    }

                    string s = src;
                    int n = await Task.Run(() => CopyDir(s, dst, ct), ct).ConfigureAwait(false);

                    result.FileCount += n;
                    done++;
                    progress?.Report(new Progress
                    {
                        Text = "已放入 " + done + "/" + folders.Count + "（" + name + "）",
                        Percent = (int)(done * 100.0 / folders.Count)
                    });
                    Log("│ ✓ " + name + "（" + n + " 个文件）");
                }

                Log("│ ④ 耗时 " + Sec(sw));

                result.Ok = true;
                result.Message = "更新完成：" + (result.Replaced.Count + result.Added.Count)
                               + " 个文件夹，共 " + result.FileCount + " 个文件";
                Log("");
                Log("═══ " + result.Message + " ═══");
            }
            catch (OperationCanceledException)
            {
                result.Ok = false;
                result.Message = "已取消";
                Log("⏹ 已取消");
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Message = ex.Message;
                Log("✕ 失败：" + ex.Message);
            }
            finally
            {
                if (tempDir != null && Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }

            return result;
        }

        // ══════════ 多渠道执行（本地包 / 云端）══════════

        /// <summary>一次完整更新的汇总结果。</summary>
        public sealed class BatchResult
        {
            public bool Ok;
            public string Message = "";
            /// <summary>实际使用的包（本地选中的或云端下载的）。</summary>
            public string ArchivePath = "";
            /// <summary>从云端下载时的来源地址（本地包时为空）。</summary>
            public string SourceUrl = "";
            /// <summary>每个目标的单独结果（键 = 目标路径）。</summary>
            public List<KeyValuePair<string, Result>> Targets = new List<KeyValuePair<string, Result>>();
            /// <summary>本次云端的版本标签（本地包时为空）。</summary>
            public string NewVersion = "";
            /// <summary>true = 云端无新版本，什么都没做。</summary>
            public bool Skipped;
        }

        /// <summary>
        /// 按设置执行一次更新：先确定包（本地优先，否则云端下载），再逐个目标替换。
        ///
        /// ★ 手动「开始更新」和定时触发都走这一个入口，避免两条路径行为不一致。
        /// </summary>
        public static async Task<BatchResult> RunAsync(
            RouteUpdateSettings settings,
            Action<string> log,
            IProgress<Progress>? progress = null,
            CancellationToken ct = default)
        {
            var batch = new BatchResult();
            if (settings == null)
            {
                batch.Message = "没有更新设置";
                return batch;
            }

            // ── ① 确定路线包 ──
            string archive = (settings.LocalArchive ?? "").Trim().Trim('"');
            bool fromCloud = archive.Length == 0 || !File.Exists(archive);

            if (fromCloud)
            {
                string url = settings.EffectiveCloudUrl;
                if (url.Length == 0)
                {
                    batch.Message = archive.Length == 0
                        ? "没有可用的更新源"
                        : "本地路线包不存在";
                    log("✕ " + batch.Message);
                    return batch;
                }

                log("═══ ① 检查云端更新 ═══");

                // ★ 先只取清单判断版本，**没有新版本就不下载、不更新**。
                var probe = await ProbeAsync(url, ct).ConfigureAwait(false);
                if (!probe.ok)
                {
                    batch.Message = probe.message;
                    log("✕ " + probe.message);
                    return batch;
                }

                if (probe.version.Length > 0)
                    settings.CloudVersion = probe.version;

                if (probe.version.Length > 0
                    && string.Equals(probe.version, settings.LastAppliedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    batch.Ok = true;
                    batch.Skipped = true;
                    batch.Message = "云端已是最新版本，无需更新";
                    log("│ 与上次已应用的版本相同，跳过");
                    log("═══ " + batch.Message + " ═══");
                    return batch;
                }

                log("│ 发现新版本");
                log("");
                log("═══ ② 下载路线包 ═══");

                string? resolvedUrl = null;
                archive = await DownloadAsync(url, log, progress, ct,
                              onResolvedUrl: u => resolvedUrl = u).ConfigureAwait(false);
                batch.SourceUrl = resolvedUrl ?? url;
                batch.NewVersion = probe.version;
            }
            else
            {
                log("═══ ① 使用本地路线包 ═══");
                log("│ " + Path.GetFileName(archive));   // 本地包是用户自己选的，可以显示
            }

            batch.ArchivePath = archive;

            // ── ② 逐个目标替换 ──
            var targets = settings.ActiveTargets();
            if (targets.Count == 0)
            {
                batch.Message = "没有勾选任何更新目标";
                log("✕ " + batch.Message);
                return batch;
            }

            int okCount = 0;
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();

                log("");
                log("──── 目标：" + target + " ────");

                var r = await UpdateAsync(archive, target, log, progress, ct).ConfigureAwait(false);
                batch.Targets.Add(new KeyValuePair<string, Result>(target, r));
                if (r.Ok) okCount++;
            }

            batch.Ok = okCount == targets.Count;

            // ★ 只有全部成功才算"这个版本已应用"，否则下次仍会重试。
            if (batch.Ok && fromCloud && batch.NewVersion.Length > 0)
                settings.LastAppliedVersion = batch.NewVersion;

            // ★ 本地历史包只保留最近 N 个（默认 3），自动清理最旧的。
            if (fromCloud)
            {
                int removed = PruneArchives(settings.KeepArchives);
                if (removed > 0) log("│ 已清理 " + removed + " 个旧包，本地保留最近 "
                                   + Math.Max(1, settings.KeepArchives) + " 个");
            }

            batch.Message = okCount == targets.Count
                ? "全部完成：" + okCount + " 个目标"
                : "完成 " + okCount + " / " + targets.Count + " 个目标（其余失败，详见日志）";

            log("");
            log("═══ " + batch.Message + " ═══");
            return batch;
        }

        // ══════════ 云端下载 ══════════

        /// <summary>压缩包扩展名（用于判断下载到的文件是"清单"还是"包本身"）。</summary>
        private static readonly string[] ArchiveExts = { ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz" };

        private static bool LooksLikeArchive(string pathOrName)
        {
            string ext = Path.GetExtension(pathOrName ?? "").ToLowerInvariant();
            return Array.IndexOf(ArchiveExts, ext) >= 0;
        }

        /// <summary>
        /// 判断下载到的文件是不是「清单文本」（如 update.txt 里只写一个包名）。
        ///
        /// ★ 判据：文件很小、是纯文本、去掉空白后是一行且以压缩包扩展名结尾。
        ///   真正的压缩包是二进制，不会满足这些条件。
        /// </summary>
        private static bool LooksLikeManifest(string filePath, out string manifestText)
        {
            manifestText = "";
            try
            {
                var fi = new FileInfo(filePath);
                if (!fi.Exists || fi.Length == 0 || fi.Length > 64 * 1024) return false;

                byte[] bytes = File.ReadAllBytes(filePath);
                foreach (var b in bytes)
                {
                    // 允许 UTF-8 BOM、换行、制表符与可打印字符
                    if (b == 0xEF || b == 0xBB || b == 0xBF) continue;
                    if (b == (byte)'\r' || b == (byte)'\n' || b == (byte)'\t') continue;
                    if (b < 0x20 || b == 0x7F) return false;
                }

                string text = File.ReadAllText(filePath).Trim().Trim('"').Trim('\'').Trim();
                if (text.Length == 0) return false;

                // 只接受单行
                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length != 1) return false;

                text = lines[0].Trim();
                if (!LooksLikeArchive(text)) return false;

                manifestText = text;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 探测云端地址：只下载到"足以判断"的程度，返回给界面显示。
        ///   · 直链压缩包：返回包名与大小
        ///   · 清单文本：解析出包名、真实包地址，再对真实地址发 HEAD 取大小
        /// ★ 不写入正式缓存目录，避免一次"测试"就占住一个缓存位。
        /// </summary>
        public static async Task<(bool ok, string message, string version, long bytes)> ProbeAsync(
            string url, CancellationToken ct = default)
        {
            url = (url ?? "").Trim();
            if (url.Length == 0) return (false, "未配置更新源", "", 0);

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return (false, "更新源地址无效", "", 0);

            string? tempDir = null;
            try
            {
                tempDir = Path.Combine(Path.GetTempPath(),
                    "SoulLink_RouteProbe_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(tempDir);

                string file = await DownloadToAsync(uri, tempDir, _ => { }, null, ct).ConfigureAwait(false);
                long size = new FileInfo(file).Length;

                // 直接就是压缩包（没有清单）：用文件指纹当版本
                if (!LooksLikeManifest(file, out string entry))
                    return (true, "连接正常", Fingerprint(file), size);

                string targetUrl = ResolveManifestTarget(uri.ToString(), entry);

                // ★ 用 HEAD 拿到真实包的大小与时间，拼成指纹当版本。
                //   绝不把清单里写的包名返回给界面/配置。
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, targetUrl);
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                    return (false, "无法连接更新源", "", 0);

                long? len = resp.Content.Headers.ContentLength;
                var stamp = resp.Content.Headers.LastModified;

                string fp = "v1-" + (len ?? 0) + "-"
                          + (stamp.HasValue ? stamp.Value.UtcTicks.ToString() : "0");

                return (true, "连接正常", fp, len ?? 0);
            }
            catch (Exception ex)
            {
                // ★ 异常信息可能含主机名，界面上统一改用通用文案
                App.AppendErrorLog("RouteUpdater.Probe", ex);
                return (false, "无法连接更新源", "", 0);
            }
            finally
            {
                if (tempDir != null && Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }
        }

        /// <summary>
        /// 用一个包文件的"大小 + 修改时间"生成版本指纹。
        /// ★ 不用包名：包名会暴露服务器文件命名。
        /// </summary>
        private static string Fingerprint(string filePath)
        {
            try
            {
                var fi = new FileInfo(filePath);
                return "v1-" + fi.Length + "-" + fi.LastWriteTimeUtc.Ticks;
            }
            catch { return "v1-0-0"; }
        }

        /// <summary>
        /// 把清单里写的「包名」解析成可下载的地址。
        ///   · 清单里写完整 URL（http/https）→ 直接用
        ///   · 清单里写相对文件名（如 pathing11.19.zip）→ 相对**清单所在目录**解析
        /// </summary>
        public static string ResolveManifestTarget(string manifestUrl, string entry)
        {
            entry = (entry ?? "").Trim().Trim('"');
            if (entry.Length == 0) throw new InvalidOperationException("清单内容为空");

            if (Uri.TryCreate(entry, UriKind.Absolute, out var abs)
                && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
                return abs.ToString();

            if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var baseUri))
                throw new InvalidOperationException("清单地址无效：" + manifestUrl);

            return new Uri(baseUri, entry).ToString();
        }

        /// <summary>
        /// 从云端地址下载路线包到缓存目录，返回本地路径。
        ///
        /// ★ 设计取舍：
        ///   · 只用 http/https 直链；不支持清单/版本协商（用户未提出，不擅自引入协议）
        ///   · 文件名取自 URL 路径；URL 没有可用文件名时用时间戳
        ///   · 下载到 .part 再改名，中途失败不会留下半个包被当成本地包使用
        ///   · 只保留最近几个包，避免缓存目录无限增长
        /// </summary>
        public static async Task<string> DownloadAsync(
            string url,
            Action<string> log,
            IProgress<Progress>? progress = null,
            CancellationToken ct = default,
            Action<string>? onResolvedUrl = null)
        {
            url = (url ?? "").Trim();
            if (url.Length == 0)
                throw new InvalidOperationException("还没有填写云端地址");

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("云端地址必须是 http / https 直链：" + url);

            await DownloadGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // ★ 扁平放在缓存根目录（不再用时间戳子目录）：
                //   用户要从「选择文件」里回档，平铺一层最直观。
                string dir = CacheRoot;
                Directory.CreateDirectory(dir);

                string first = await DownloadToAsync(uri, dir, log, progress, ct).ConfigureAwait(false);

                // ★ 自动识别「清单」：
                //   cdn.example.invalid/pathing/update.txt 只有 16 字节，内容是 pathing11.19.zip。
                //   这里判断下载到的不是压缩包而是清单文本，就按清单里的名字再下一次。
                if (LooksLikeManifest(first, out string entry))
                {
                    string targetUrl = ResolveManifestTarget(uri.ToString(), entry);

                    log("│ 云端提供了新版本");

                    // 清单文件本身没用（只是个 txt），删掉免得缓存里堆垃圾
                    try { File.Delete(first); } catch { }

                    if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var realUri)
                        || (realUri.Scheme != Uri.UriSchemeHttp && realUri.Scheme != Uri.UriSchemeHttps))
                        throw new InvalidOperationException("云端清单内容无法解析");

                    onResolvedUrl?.Invoke(targetUrl);
                    first = await DownloadToAsync(realUri, dir, log, progress, ct).ConfigureAwait(false);
                }
                else
                {
                    onResolvedUrl?.Invoke(uri.ToString());
                }

                return first;
            }
            finally
            {
                DownloadGate.Release();
            }
        }

        /// <summary>
        /// 把单个 URL 下载到指定目录，返回本地文件路径。
        /// ★ 先写 .part 再改名：中途失败不会留下半个包被当成本地包使用。
        /// </summary>
        private static async Task<string> DownloadToAsync(
            Uri uri, string dir,
            Action<string> log, IProgress<Progress>? progress, CancellationToken ct)
        {
            {
                // ★ 不用服务器上的原文件名：那会暴露服务器的命名方式（用户要求不显示）。
                //   统一叫「路线包-时间.扩展名」，回档时按时间挑就行。
                string srcName = Path.GetFileName(uri.LocalPath);
                string ext = Path.GetExtension(srcName).ToLowerInvariant();
                if (ext.Length == 0 || Array.IndexOf(ArchiveExts, ext) < 0)
                    ext = ".zip";

                string name = "路线包-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ext;

                string part = Path.Combine(dir, name + ".part");
                string final = Path.Combine(dir, name);

                // ★ 日志里只写"正在下载"，不写主机名/路径
                log("│ 正在从云端下载…");
                progress?.Report(new Progress { Text = "正在从云端下载…", Percent = -1 });

                using (var http = new System.Net.Http.HttpClient
                {
                    Timeout = TimeSpan.FromMinutes(30)
                })
                using (var resp = await http.GetAsync(uri, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct)
                                             .ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException(
                            "下载失败：HTTP " + (int)resp.StatusCode + " " + resp.ReasonPhrase);

                    long? total = resp.Content.Headers.ContentLength;

                    using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None,
                        81920, useAsync: true);

                    var buffer = new byte[81920];
                    long done = 0;
                    int lastPercent = -1;
                    int n;
                    while ((n = await src.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
                        done += n;

                        if (total.HasValue && total.Value > 0)
                        {
                            int pct = (int)(done * 100 / total.Value);
                            if (pct != lastPercent)
                            {
                                lastPercent = pct;
                                progress?.Report(new Progress
                                {
                                    Text = "正在下载… " + Size(done) + " / " + Size(total.Value) + "（" + pct + "%）",
                                    Percent = pct
                                });
                            }
                        }
                        else
                        {
                            progress?.Report(new Progress
                            {
                                Text = "正在下载… " + Size(done),
                                Percent = -1
                            });
                        }
                    }
                }

                if (!File.Exists(part) || new FileInfo(part).Length == 0)
                    throw new InvalidOperationException("下载到的文件是空的");

                File.Move(part, final, overwrite: true);
                log("│ 下载完成：" + Size(new FileInfo(final).Length));
                return final;
            }
        }

        /// <summary>
        /// 清理历史包：按"包文件"计，只保留最近 N 个（默认 3）。
        ///
        /// ★ 按文件而不是按目录算：一次更新会先下清单再下包，
        ///   按目录算会把同一批误判成两条历史。
        /// </summary>
        public static int PruneArchives(int keep)
        {
            int removed = 0;
            try
            {
                keep = Math.Max(1, Math.Min(keep, 20));

                var root = new DirectoryInfo(CacheRoot);
                if (!root.Exists) return 0;

                // 所有历史包（一个时间戳目录里通常一个包）
                var files = root.GetFiles("*", SearchOption.AllDirectories)
                    .Where(f => LooksLikeArchive(f.Name) && !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();

                foreach (var f in files.Skip(keep))
                {
                    try { f.Delete(); removed++; } catch { }
                }

                // 删空目录
                foreach (var d in root.GetDirectories())
                {
                    try
                    {
                        if (!d.EnumerateFileSystemInfos().Any()) d.Delete();
                    }
                    catch { }
                }
            }
            catch { }
            return removed;
        }

        // ══════════ 预检（只读）══════════

        /// <summary>解压到临时目录，列出包内第一层文件夹并与目标根目录比对。失败时返回空列表。</summary>
        public static async Task<(List<PackageFolder> folders, LooseFileInfo loose)> InspectAsync(
            string archivePath, string targetRoot, CancellationToken ct = default)
        {
            var list = new List<PackageFolder>();
            var loose = new LooseFileInfo();

            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                return (list, loose);

            string? tempDir = null;
            try
            {
                tempDir = Path.Combine(Path.GetTempPath(),
                    "SoulLink_RouteInspect_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(tempDir);

                string contentRoot = await ExtractAsync(archivePath, tempDir, _ => { }, null, ct)
                    .ConfigureAwait(false);

                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (!string.IsNullOrWhiteSpace(targetRoot) && Directory.Exists(targetRoot))
                        foreach (var d in Directory.GetDirectories(targetRoot))
                            existing.Add(Path.GetFileName(d));
                }
                catch { }

                foreach (var dir in ListTopFolders(contentRoot))
                {
                    ct.ThrowIfCancellationRequested();
                    var info = Fold(dir);
                    info.Replaces = existing.Contains(info.Name);
                    list.Add(info);
                }

                loose = ListTopFiles(contentRoot);
            }
            catch
            {
                // 预检失败不阻断主流程：界面显示「无法预检」，真正开始更新时再报错。
            }
            finally
            {
                if (tempDir != null && Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }

            return (list, loose);
        }

        // ══════════ 解压 ══════════

        /// <summary>
        /// 解压到临时目录，返回「内容根」。
        /// ★ 复用 BgiUpdater.Find7za()，不另写一份找 exe 的逻辑。
        /// </summary>
        private static async Task<string> ExtractAsync(
            string archivePath, string tempDir,
            Action<string> log, IProgress<Progress>? progress, CancellationToken ct)
        {
            string sevenZa = BgiUpdater.Find7za();
            if (sevenZa.Length == 0)
                throw new InvalidOperationException("找不到 7za.exe（应随程序一起分发）");

            string rawDir = Path.Combine(tempDir, "_raw");
            Directory.CreateDirectory(rawDir);

            log("│ 解压中…");
            progress?.Report(new Progress { Text = "解压中…", Percent = -1 });

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = sevenZa,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("x");
            psi.ArgumentList.Add(archivePath);
            psi.ArgumentList.Add("-o" + rawDir);
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-bd");
            psi.ArgumentList.Add("-bso0");
            psi.ArgumentList.Add("-bsp0");
            psi.ArgumentList.Add("-bb0");

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) throw new InvalidOperationException("无法启动 7za.exe");

            int lastLogged = 0;
            while (!proc.HasExited)
            {
                if (ct.IsCancellationRequested)
                {
                    try { proc.Kill(true); } catch { }
                    ct.ThrowIfCancellationRequested();
                }

                try
                {
                    int now = 0;
                    foreach (var _ in Directory.EnumerateFiles(rawDir, "*", SearchOption.AllDirectories))
                        now++;

                    progress?.Report(new Progress
                    {
                        Text = "解压中…已解压 " + now + " 个文件",
                        Percent = -1
                    });

                    if (now - lastLogged >= 500)
                    {
                        lastLogged = now;
                        log("│ 解压中… " + now + " 个");
                    }
                }
                catch { }

                await Task.Delay(500, ct).ConfigureAwait(false);
            }

            if (proc.ExitCode != 0)
                throw new InvalidOperationException("7za 解压失败（退出码 " + proc.ExitCode + "）");

            int total = Directory.GetFiles(rawDir, "*", SearchOption.AllDirectories).Length;
            if (total == 0)
                throw new InvalidOperationException("解压后没有文件，包可能不对");

            progress?.Report(new Progress
            {
                Text = "解压完成，共 " + total + " 个文件",
                Percent = 100,
                IsLog = true
            });

            return rawDir;
        }

        // ══════════ 包裹层识别 ══════════

        /// <summary>
        /// 判断解压根下的唯一文件夹是不是「打包时自动生成的包裹层」。
        ///
        /// ★★ 判据只看「包」，绝不看目标目录。★
        ///
        ///   2.16.0 曾用「该名字在目标里不存在才算包裹层」的判据，那是错的：
        ///   第一次运行剥对了、把路线文件夹放进目标；但目标里一旦出现过一个叫
        ///   pathing 的文件夹（旧版本误建的、或用户自己建的），
        ///   之后每次都会认为"目标里有同名，那是要替换的对象"，于是再也不剥，
        ///   变成 pathing\12小时狗粮 整包塞进去 —— 实测就是这么坏的
        ///   （A1 的 AutoPathing\CD采集路线 下多出 1585 个文件的 pathing）。
        ///
        /// ★ 现在的判据（纯看包结构，稳定且幂等）：
        ///   1. 解压根下只有一个文件夹、没有散落文件；
        ///   2. 该文件夹下还有子文件夹（是容器而不是一条路线本身）；
        ///   3. 该文件夹名不是目标目录本身的名字（防止路径填多一层时自我吞掉）；
        ///   4. 该文件夹名不等于包名（防止"CD采集路线.zip 里装着 CD采集路线"时被误剥）。
        ///
        /// 例：pathing11.19.zip → pathing\12小时狗粮\…  → 剥掉 pathing ✅
        ///     routes.zip 里是 CD采集路线\12小时狗粮\…  → CD采集路线 就是内容，不剥 ✅
        /// </summary>
        private static string StripWrapper(string contentRoot, string targetRoot,
                                          out string wrapperName, out string note)
        {
            wrapperName = "";
            note = "";
            try
            {
                var dirs = Directory.GetDirectories(contentRoot);
                var files = Directory.GetFiles(contentRoot);
                if (files.Length != 0 || dirs.Length != 1) return contentRoot;

                string child = dirs[0];
                string name = FolderName(child);

                // 2. 必须是容器（下面还有文件夹），否则它就是一条路线，不能剥
                if (Directory.GetDirectories(child).Length == 0) return contentRoot;

                // 3. 不能是目标目录自己的名字：用户把根目录填进了 CD采集路线 里面时，
                //    若再剥一层会把本该替换的层级吞掉。
                string targetLeaf = FolderName(targetRoot ?? "");
                if (targetLeaf.Length > 0 && name.Equals(targetLeaf, StringComparison.OrdinalIgnoreCase))
                    return contentRoot;

                wrapperName = name;
                note = "包内只有「" + name + "」一个文件夹，判定为打包用的包裹层，"
                     + "已忽略它，直接更新其中的路线文件夹";
                return child;
            }
            catch { return contentRoot; }
        }

        /// <summary>
        /// 判断两个目录的第一层子项集合是否一致（用于确认"残骸目录确实就是这个包"）。
        /// </summary>
        private static bool SameChildren(string a, string b)
        {
            try
            {
                var sa = Directory.GetFileSystemEntries(a)
                    .Select(e => Path.GetFileName(e))
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                var sb = Directory.GetFileSystemEntries(b)
                    .Select(e => Path.GetFileName(e))
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

                if (sa.Count == 0 || sa.Count != sb.Count) return false;
                for (int i = 0; i < sa.Count; i++)
                    if (!string.Equals(sa[i], sb[i], StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
            catch { return false; }
        }

        // ══════════ 目录工具 ══════════

        /// <summary>取文件夹名（去掉结尾分隔符）。</summary>
        private static string FolderName(string dir)
            => Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        /// <summary>列出「第一层文件夹」的完整路径（已排除系统垃圾）。</summary>
        private static List<string> ListTopFolders(string root)
        {
            var list = new List<string>();
            try
            {
                foreach (var d in Directory.GetDirectories(root))
                {
                    if (IsJunk(Path.GetFileName(d))) continue;
                    list.Add(d);
                }
            }
            catch { }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        /// <summary>列出「第一层散落文件」（不含系统垃圾）。</summary>
        private static LooseFileInfo ListTopFiles(string root)
        {
            var info = new LooseFileInfo();
            try
            {
                foreach (var f in Directory.GetFiles(root))
                {
                    string n = Path.GetFileName(f);
                    if (IsJunk(n)) continue;
                    info.Count++;
                    if (info.Names.Count < 64) info.Names.Add(n);
                }
            }
            catch { }
            return info;
        }

        /// <summary>压缩包里常见的系统垃圾（macOS 的 __MACOSX、Windows 的 Thumbs.db 等）。</summary>
        private static bool IsJunk(string name)
            => name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
            || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);

        /// <summary>统计一个文件夹（文件数 / 总字节）。</summary>
        private static PackageFolder Fold(string dir)
        {
            var info = new PackageFolder { Name = FolderName(dir) };
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    info.FileCount++;
                    try { info.TotalBytes += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return info;
        }

        /// <summary>把源目录完整复制到目标目录（目标必须不存在）。返回复制数。</summary>
        private static int CopyDir(string src, string dst, CancellationToken ct)
        {
            int n = 0;
            var stack = new Stack<(string s, string d)>();
            stack.Push((src, dst));

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (s, d) = stack.Pop();

                Directory.CreateDirectory(d);

                foreach (var f in Directory.GetFiles(s))
                {
                    ct.ThrowIfCancellationRequested();
                    File.Copy(f, Path.Combine(d, Path.GetFileName(f)), overwrite: true);
                    n++;
                }

                foreach (var sub in Directory.GetDirectories(s))
                    stack.Push((sub, Path.Combine(d, Path.GetFileName(sub))));
            }

            return n;
        }

        /// <summary>
        /// 规范化 + 校验用户填写的路线根目录。
        /// 返回值：空字符串 = 可用；否则是给用户看的问题描述。
        /// </summary>
        public static string ValidateTargetRoot(string path, out string normalized)
        {
            normalized = "";
            try
            {
                path = (path ?? "").Trim().Trim('"');
                if (path.Length == 0) return "还没有设置路线根目录";

                normalized = Path.GetFullPath(path);
                if (!Directory.Exists(normalized)) return "目录不存在：" + normalized;

                // 防呆：直接把 BGI 安装目录当路线根目录，会误删 BGI 自己的程序文件夹。
                string leaf = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar));
                if (leaf.Equals("BetterGI", StringComparison.OrdinalIgnoreCase))
                    return @"这里看起来是 BGI 安装目录，路线通常在其中的 User\AutoPathing";

                return "";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>把字节数格式化成人能读的大小。</summary>
        public static string Size(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("F1") + " MB";
            return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("F2") + " GB";
        }

        /// <summary>Stopwatch → 一位小数秒。</summary>
        private static string Sec(System.Diagnostics.Stopwatch sw)
            => sw.Elapsed.TotalSeconds.ToString("F1") + " 秒";
    }
}
