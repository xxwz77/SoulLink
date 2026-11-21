using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>
    /// BGI 更新：解压本地压缩包，覆盖到目标用户的 BGI 目录。
    ///
    /// ★ 铁律：
    ///   · 只处理**选中用户**的进程，绝不碰其他用户
    ///   · 关闭 BGI 沿用「先发停止快捷键 → 不行再杀进程」的方案
    ///   · 茶包助手（MultiplayerHoeingAssistant）直接结束进程
    ///   · 进程本来就不在 → 跳过，不算失败
    /// </summary>
    public static class BgiUpdater
    {
        /// <summary>茶包助手的进程名。</summary>
        public const string TeaBagProcessName = "MultiplayerHoeingAssistant";

        /// <summary>BGI 主程序进程名。</summary>
        public const string BgiProcessName = "BetterGI";

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

        // ══════════ 主流程 ══════════

        public sealed class UpdateResult
        {
            public bool Ok;
            public string Message = "";
            public List<string> Steps = new List<string>();
        }

        // ══════════ 批量更新（解压一次，并行复制）══════════

        /// <summary>一个要更新的目标。</summary>
        public sealed class Target
        {
            public string UserName = "";
            public string BgiExePath = "";
        }

        /// <summary>
        /// 批量更新多个账号。
        ///
        /// ★ 关键优化（用户反馈）：
        ///   更新包只解压**一次**到临时目录，然后并行把临时目录复制到各 BGI 目录。
        ///   之前是每个账号解压一遍同一个包 → CPU 满负载、风扇狂转、耗时翻倍。
        ///
        /// 流程：
        ///   ① 停所有目标的 BGI / 茶包助手（并行）
        ///   ② 备份所有目标的 User 目录（并行）
        ///   ③ 解压更新包到临时目录（一次）
        ///   ④ 并行复制到各 BGI 目录
        ///   ⑤ 清理临时目录
        /// </summary>
        public static async Task<List<UpdateResult>> UpdateBatchAsync(
            List<Target> targets, string archivePath, bool backupUser,
            Action<string> log,
            IProgress<Progress>? progress = null,
            CancellationToken ct = default)
        {
            var results = new Dictionary<string, UpdateResult>();
            foreach (var t in targets)
                results[t.UserName] = new UpdateResult();

            UpdateResult R(string user) => results[user];

            void Log(string s) => log(s);

            string? tempDir = null;
            var totalSw = System.Diagnostics.Stopwatch.StartNew();   // 总耗时

            try
            {
                // ── 校验 ──
                if (!File.Exists(archivePath))
                    throw new InvalidOperationException("找不到更新包：" + archivePath);

                progress?.Report(new Progress { Text = "准备中…", IsLog = false });

                // ── ① 停进程（并行）──
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Log("═══ ① 停止 BGI 和茶包助手 ═══");

                var sessions = await SessionService.QueryAsync().ConfigureAwait(false);
                var sessionMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var t in targets)
                {
                    var sess = SessionService.Find(sessions, t.UserName);
                    if (sess != null) sessionMap[t.UserName] = sess.Id;
                }

                await Task.WhenAll(targets.Select(async t =>
                {
                    if (!sessionMap.TryGetValue(t.UserName, out int sid))
                    {
                        Log("│ [" + t.UserName + "] 没有会话，跳过停机");
                        return;
                    }

                    Log("│ [" + t.UserName + "] 停止当前脚本并关闭 BGI / 茶包助手…");
                    var guard = await BgiStartGuard.BeforeStartAsync(sid, t.BgiExePath, ct).ConfigureAwait(false);
                    Log("│   [" + t.UserName + "] " + guard.Message);
                    if (!guard.Ok) throw new InvalidOperationException(t.UserName + "：" + guard.Message);
                    KillInSession(TeaBagProcessName, sid,
                        s => Log("│   [" + t.UserName + "] " + s));
                })).ConfigureAwait(false);

                ct.ThrowIfCancellationRequested();
                await Task.Delay(1200, ct).ConfigureAwait(false);
                Log("│ ① 耗时 " + Sec(sw) + " 秒");

                // ── ② 备份 User（并行）──
                if (backupUser)
                {
                    Log("");
                    Log("═══ ② 备份 User 目录 ═══");
                    sw.Restart();

                    await Task.WhenAll(targets.Select(async t =>
                    {
                        string bgiDir = Path.GetDirectoryName(t.BgiExePath) ?? "";
                        string userDir = Path.Combine(bgiDir, "User");
                        if (!Directory.Exists(userDir))
                        {
                            Log("│ [" + t.UserName + "] 没有 User 目录，跳过");
                            return;
                        }

                        Log("│ [" + t.UserName + "] 备份中…");
                        string bak = await BackupUserDirAsync(t.UserName, userDir, ct)
                                           .ConfigureAwait(false);

                        Log(bak.Length > 0
                            ? "│ [" + t.UserName + "] 已备份 → " + bak
                            : "│ [" + t.UserName + "] ⚠ 备份失败，继续");
                    })).ConfigureAwait(false);

                    Log("│ ② 耗时 " + Sec(sw) + " 秒");
                }

                ct.ThrowIfCancellationRequested();

                // ── ③ 解压一次到临时目录 ──
                Log("");
                Log("═══ ③ 解压更新包（只解压一次）═══");
                sw.Restart();

                tempDir = Path.Combine(Path.GetTempPath(),
                    "SoulLink_BgiUpdate_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(tempDir);

                var (fileCount, contentRoot) = await ExtractToTempAsync(
                    archivePath, tempDir, Log, progress, ct).ConfigureAwait(false);

                if (fileCount == 0)
                    throw new InvalidOperationException("更新包里没有文件，包可能不对");

                Log("│ ③ 耗时 " + Sec(sw) + " 秒，共 " + fileCount + " 个文件");

                // ── ④ 并行复制到各 BGI 目录 ──
                Log("");
                Log("═══ ④ 复制到各 BGI 目录（并行）═══");
                sw.Restart();

                int done = 0;

                await Task.WhenAll(targets.Select(async t =>
                {
                    var r = R(t.UserName);
                    string bgiDir = Path.GetDirectoryName(t.BgiExePath) ?? "";

                    if (bgiDir.Length == 0 || !Directory.Exists(bgiDir))
                    {
                        r.Ok = false;
                        r.Message = t.UserName + " BGI 目录不存在";
                        Log("│ [" + t.UserName + "] ✕ " + r.Message);
                        return;
                    }

                    Log("│ [" + t.UserName + "] 复制中…");

                    int n = await Task.Run(() => CopyDirOverwrite(contentRoot, bgiDir, ct), ct)
                                      .ConfigureAwait(false);

                    // 记录版本
                    string ver = "";
                    try { ver = FileVersion(t.BgiExePath); } catch { }

                    r.Ok = true;
                    r.Message = t.UserName + " 更新成功（" + n + " 个文件）"
                              + (ver.Length > 0 ? "，当前版本 " + ver : "");
                    Log("│ [" + t.UserName + "] ✓ " + r.Message);

                    int d = Interlocked.Increment(ref done);
                    progress?.Report(new Progress
                    {
                        Text = "已复制 " + d + "/" + targets.Count,
                        Percent = (int)(d * 100.0 / targets.Count)
                    });
                })).ConfigureAwait(false);

                totalSw.Stop();
                Log("│ ④ 耗时 " + Sec(sw) + " 秒");
                Log("");
                Log("═══ 全部完成，总耗时 " + Sec(totalSw) + " 秒 ═══");
            }
            catch (OperationCanceledException)
            {
                foreach (var r in results.Values)
                    if (!r.Ok) { r.Ok = false; r.Message = r.Message.Length > 0 ? r.Message : "已取消"; }
            }
            catch (Exception ex)
            {
                Log("✕ 失败：" + ex.Message);
                foreach (var r in results.Values)
                    if (!r.Ok) { r.Ok = false; r.Message = ex.Message; }
            }
            finally
            {
                // ── ⑤ 清理临时目录 ──
                if (tempDir != null && Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); }
                    catch { }
                }
            }

            return results.Values.ToList();
        }

        /// <summary>读 exe 的版本号。</summary>
        private static string FileVersion(string exePath)
        {
            try
            {
                var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
                return vi.FileVersion ?? "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// 把压缩包解压到临时目录（只做一次）。
        ///
        /// ★ 用自带的 7za.exe（官方命令行工具）：
        ///   实测 519MB / 2244 个文件 → 5.4 秒。
        ///
        /// ★ 剥顶层目录的做法：
        ///   解压到 tempDir\_raw，然后把 _raw 里唯一的那个子目录搬出来，
        ///   避免在 tempDir 里原地搬移（原地 Move 会误删已解压的内容）。
        /// </summary>
        private static async Task<(int count, string contentRoot)> ExtractToTempAsync(
            string archivePath, string tempDir,
            Action<string> log, IProgress<Progress>? progress, CancellationToken ct)
        {
            string sevenZa = Find7za();
            if (sevenZa.Length == 0)
                throw new InvalidOperationException("找不到 7za.exe（应随程序一起分发）");

            // 先解压到一个干净的子目录
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

            // ★ 7za 本身不输出进度（-bsp0），改为自己数文件：
            //   每 300ms 数一次已解压的文件数，既报进度条也写日志。
            int lastLogged = 0;
            int lastPercent = -1;

            while (!proc.HasExited)
            {
                if (ct.IsCancellationRequested)
                {
                    try { proc.Kill(true); } catch { }
                    ct.ThrowIfCancellationRequested();
                }

                // ★ 每 500ms 数一次（太频繁会拖慢解压本身）
                try
                {
                    int now = 0;
                    foreach (var _ in Directory.EnumerateFiles(rawDir, "*", SearchOption.AllDirectories))
                        now++;

                    // 进度条
                    progress?.Report(new Progress
                    {
                        Text = "解压中…已解压 " + now + " 个文件",
                        Percent = -1
                    });

                    // 日志：每多 500 个报一次
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

            // 判断实际内容根：如果 rawDir 下只有一个 BetterGI 开头的目录，就往下走一层
            string contentRoot = rawDir;
            try
            {
                var topDirs = Directory.GetDirectories(rawDir);
                var topFiles = Directory.GetFiles(rawDir);

                if (topFiles.Length == 0 && topDirs.Length == 1)
                {
                    string only = topDirs[0];
                    string name = Path.GetFileName(only);
                    if (name.StartsWith("BetterGI", StringComparison.OrdinalIgnoreCase))
                        contentRoot = only;
                }
            }
            catch { }

            int count = Directory.GetFiles(contentRoot, "*", SearchOption.AllDirectories).Length;

            if (count == 0)
                throw new InvalidOperationException("解压后没有文件，包可能不对");

            progress?.Report(new Progress
            {
                Text = "解压完成，共 " + count + " 个文件",
                Percent = 100,
                IsLog = true
            });

            return (count, contentRoot);
        }





        /// <summary>把 Stopwatch 转成一位小数的秒数。</summary>
        private static string Sec(System.Diagnostics.Stopwatch sw)
            => sw.Elapsed.TotalSeconds.ToString("F1");

        /// <summary>找到 7za.exe（随程序一起分发）。
        /// ★ 路线更新器（RouteUpdater）复用这一个实现，不要另写一份。</summary>
        internal static string Find7za()
        {
            // ★★ 2.17.47 修复：单文件发布下 AppContext.BaseDirectory 是错的。★★
            //
            //   现象：dist 里明明有 7za.exe（1.2 MB），更新却报
            //   「找不到 7za.exe（应随程序一起分发）」。
            //
            //   根因：PublishSingleFile 会把运行时解压到
            //     %TEMP%\.net\SoulLink\<随机>\
            //   而 AppContext.BaseDirectory 指向那个临时目录，不是 exe 所在目录。
            //   7za.exe 是 AfterPublish 复制到 dist\ 的，所以在临时目录里找不到。
            //
            //   修法：优先用**进程主模块所在目录**（= exe 真实位置），
            //   再回退到 AppContext.BaseDirectory（非单文件发布时两者相同）。
            var dirs = new List<string>();
            try
            {
                string exePath = Environment.ProcessPath ?? "";
                if (exePath.Length > 0)
                {
                    string exeDir = Path.GetDirectoryName(exePath) ?? "";
                    if (exeDir.Length > 0 && !dirs.Contains(exeDir, StringComparer.OrdinalIgnoreCase))
                        dirs.Add(exeDir);
                }
            }
            catch { }
            string baseDir = AppContext.BaseDirectory;
            if (!dirs.Contains(baseDir, StringComparer.OrdinalIgnoreCase)) dirs.Add(baseDir);

            foreach (var dir in dirs)
            {
                var candidates = new[]
                {
                    Path.Combine(dir, "7za.exe"),
                    Path.Combine(dir, "7z", "7za.exe"),
                    Path.Combine(dir, "tools", "7za.exe"),
                };
                foreach (var p in candidates)
                    if (File.Exists(p)) return p;
            }

            return "";
        }

        /// <summary>把源目录内容覆盖复制到目标目录（保持结构）。返回复制数。</summary>
        private static int CopyDirOverwrite(string src, string dst, CancellationToken ct)
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
                    try
                    {
                        File.Copy(f, Path.Combine(d, Path.GetFileName(f)), overwrite: true);
                        n++;
                    }
                    catch { }
                }

                foreach (var sub in Directory.GetDirectories(s))
                    stack.Push((sub, Path.Combine(d, Path.GetFileName(sub))));
            }

            return n;
        }


        private static UpdateResult Fail(UpdateResult r, string msg)
        {
            r.Ok = false;
            r.Message = msg;
            r.Steps.Add("✕ " + msg);
            return r;
        }

        // ══════════ 停 BGI（复用现状方案）══════════

        private static async Task UnusedStopBgiAsync(int sessionId, string bgiExePath,
                                               Action<string> step)
        {
            var bgis = FindInSession(BgiProcessName, sessionId);
            if (bgis.Count == 0)
            {
                step("  BGI 未运行，跳过");
                return;
            }

            string hotkey = BgiTaskPreparer.ReadCancelHotkey(bgiExePath);
            if (!string.IsNullOrEmpty(hotkey))
            {
                step("  发送停止快捷键 " + hotkey);
                BgiTaskPreparer.SendHotkeyToSessionPublic(bgis[0].Id, hotkey);

                // 等它自己退出
                for (int i = 0; i < 10; i++)
                {
                    await Task.Delay(300).ConfigureAwait(false);
                    if (FindInSession(BgiProcessName, sessionId).Count == 0)
                    {
                        step("  BGI 已退出");
                        return;
                    }
                }
                step("  快捷键没让它退出，改为结束进程");
            }
            else
            {
                step("  未配置停止快捷键，直接结束进程");
            }

            KillInSession(BgiProcessName, sessionId, step);

            // 等进程真的没了
            for (int i = 0; i < 20; i++)
            {
                if (FindInSession(BgiProcessName, sessionId).Count == 0) return;
                await Task.Delay(300).ConfigureAwait(false);
            }
            step("  ⚠ BGI 进程仍未退出");
        }

        // ══════════ 进程操作（按会话过滤）══════════

        /// <summary>只返回属于该会话的进程。</summary>
        private static List<Process> FindInSession(string name, int sessionId)
        {
            var list = new List<Process>();
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (p.SessionId == sessionId) list.Add(p);
                        else p.Dispose();
                    }
                    catch { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
            return list;
        }

        /// <summary>结束该会话里所有同名进程。</summary>
        private static void KillInSession(string name, int sessionId, Action<string> step)
        {
            var procs = FindInSession(name, sessionId);
            if (procs.Count == 0)
            {
                step("  " + name + " 未运行，跳过");
                return;
            }

            int killed = 0;
            foreach (var p in procs)
            {
                try
                {
                    p.Kill();
                    killed++;
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            step("  已结束 " + name + " × " + killed);
        }

        // ══════════ 备份 User 目录 ══════════

        /// <summary>
        /// SoulLink 的备份根目录。
        ///
        /// ★ 2.20.0：跟随数据目录（默认是 exe 同目录下的 data\bgi-backups），
        ///   不再写死 %LOCALAPPDATA%。原因见 AppIdentity.DataDir 的注释。
        /// </summary>
        public static string BackupRoot
        {
            get
            {
                string dir = Path.Combine(AppIdentity.DataDir, "bgi-backups");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>
        /// 每个账号保留最近几次备份。<b>keep &lt;= 0 表示不限制</b>（永不自动删）。
        ///
        /// ★ 2.20.0 新增。之前**没有任何清理机制**，实测 4 个账号 5 天累积了
        ///   20 次备份 / 872 MB / 14.5 万个文件，全压在 C 盘。
        ///
        /// ★ 2.21.0：备份改成 zip 文件后，本方法处理两种形态：
        ///   · User_yyyyMMdd_HHmmss.zip   （新）
        ///   · User_yyyyMMdd_HHmmss\      （旧，目录形态）
        ///   两种按名字一起排序，统一裁剪，老数据也能被清掉。
        ///
        /// 按名字排序即可——名字里带 yyyyMMdd_HHmmss，字典序就是时间序。
        /// </summary>
        public static int PruneBackups(int keep)
        {
            if (keep <= 0) return 0;   // 不限制

            int removed = 0;
            try
            {
                if (!Directory.Exists(BackupRoot)) return 0;

                foreach (var acct in Directory.GetDirectories(BackupRoot))
                {
                    try
                    {
                        var entries = new List<string>();
                        entries.AddRange(Directory.GetFiles(acct, "User_*.zip"));
                        entries.AddRange(Directory.GetDirectories(acct, "User_*"));
                        if (entries.Count <= keep) continue;

                        entries.Sort(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < entries.Count - keep; i++)
                        {
                            try
                            {
                                if (Directory.Exists(entries[i]))
                                    Directory.Delete(entries[i], recursive: true);
                                else
                                    File.Delete(entries[i]);
                                removed++;
                            }
                            catch { /* 被占用/正在用：下次再说 */ }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return removed;
        }

        /// <summary>当前备份总占用（字节），给设置页显示。</summary>
        public static long GetBackupSize()
        {
            try
            {
                if (!Directory.Exists(BackupRoot)) return 0;
                long sum = 0;
                foreach (var f in Directory.EnumerateFiles(BackupRoot, "*", SearchOption.AllDirectories))
                {
                    try { sum += new FileInfo(f).Length; } catch { }
                }
                return sum;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 把 BGI 的 User 目录（用户配置 + 自定义脚本）备份成 **zip 压缩包**。
        ///
        /// ★ 2.21.0：从「复制目录」改为「打包成 zip」。
        ///   原因：目录形态有两个硬伤——
        ///     ① 一次 User 目录 80~130 MB / 数万个文件，占空间且零碎；
        ///     ② 千万个小文件在机械盘/杀软扫描下极慢，复制一次要几十秒。
        ///   实测同样内容打成 zip 后通常只有原体积的 1/4 左右，
        ///   且是**单个文件**，复制、迁移、发给别人都方便。
        ///
        ///   放在 data\bgi-backups\<账号>\User_yyyyMMdd_HHmmss.zip
        ///   不放进 BGI 自己的目录，这样多个账号的备份集中管理。
        /// </summary>
        private static async Task<string> BackupUserDirAsync(
            string userName, string userDir, CancellationToken ct)
        {
            try
            {
                string safeUser = string.Join("_", userName.Split(Path.GetInvalidFileNameChars()));
                string folder = Path.Combine(BackupRoot, safeUser);
                Directory.CreateDirectory(folder);

                // 同一秒内重复备份时避免撞名
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string zipPath = Path.Combine(folder, "User_" + stamp + ".zip");
                int n = 1;
                while (File.Exists(zipPath))
                    zipPath = Path.Combine(folder, "User_" + stamp + "_" + (n++) + ".zip");

                await Task.Run(() => ZipDir(userDir, zipPath, ct), ct).ConfigureAwait(false);

                // ★ 备份完再清理。放在压缩之后：万一这次打包失败，
                //   至少不会先把旧备份删了。
                int keep = 3;
                try { keep = App.Config?.Settings?.BgiBackupKeep ?? 3; } catch { }

                int pruned = PruneBackups(keep);
                if (pruned > 0)
                    LogBus.Info("已清理旧备份", pruned + " 份（每账号保留 " + keep + " 次）");

                return zipPath;
            }
            catch (Exception ex)
            {
                LogBus.Warn("备份 BGI 配置失败", ex.Message);
                return "";
            }
        }

        /// <summary>
        /// 备份时要跳过的「纯资源」扩展名。
        ///
        /// ★ 2.21.0 实测数据（4 个账号各一次备份）：
        ///     原始 163.98 MB → 整包压缩 95.67 MB（63.9%，8.2 秒）
        ///   原因：BGI 的 User 目录里约一半体积是已经自身压缩过的位图
        ///   （PNG 7.46 MB + ICO 4.63 MB），zip 对它们几乎无效，
        ///   白白消耗 CPU 和打包时间。
        ///
        ///   跳过这些之后：
        ///     A1 89.14 MB → 13.70 MB   （8.2s → 2.6s）
        ///     A2 25.52 MB →  3.86 MB   （1.5s → 0.9s）
        ///     A3 22.96 MB →  3.28 MB   （2.3s → 0.7s）
        ///     A4 26.36 MB →  4.38 MB   （3.0s → 1.0s）
        ///   整体缩小到 1/3 左右，速度还快 2~3 倍。
        ///
        /// ★ 为什么跳过是**安全**的：
        ///   备份的目的是「BGI 升级出问题时能还原用户的配置和脚本」。
        ///   而这些位图/图标是**脚本自带的资产**，本来就随脚本一起分发，
        ///   丢失可以从原始脚本包重新获取；真正不可再生的是
        ///   JSON 配置（路径、任务序列、账号）、INI、脚本源码和 .bak。
        ///   这些一个不漏。
        ///
        ///   ⚠ 如果将来发现用户确实需要完整资源，把这里改成空数组即可，
        ///     或者加一个「完整备份」开关。
        /// </summary>
        private static readonly string[] SkippedBackupExtensions =
        {
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".svg",
            ".ttf", ".otf", ".woff", ".woff2",     // 字体：BGI 自带点阵字体，可再生
            ".mp3", ".wav", ".ogg", ".mp4", ".webm",
            ".zip", ".7z", ".rar", ".gz",          // 二级压缩包，本来就不该再压
            ".dll", ".exe", ".pdb", ".so", ".dylib",
        };

        /// <summary>
        /// 把一个目录打包成 zip（跳过纯资源文件，见 <see cref="SkippedBackupExtensions"/>）。
        ///
        /// ★ 路径用相对路径存进包，解压时能还原成 User\... 的目录结构。
        /// ★ 用 Optimal 而不是 SmallestSize：实测两者体积只差 0.3%（16.31 vs 16.26 MB），
        ///   但打包时间几乎一样，不值得为 0.05 MB 换更慢的解压。
        /// </summary>
        private static void ZipDir(string srcDir, string zipPath, CancellationToken ct)
        {
            if (!Directory.Exists(srcDir)) return;

            using (var zip = System.IO.Compression.ZipFile.Open(
                       zipPath, System.IO.Compression.ZipArchiveMode.Create,
                       System.Text.Encoding.UTF8))
            {
                string baseFull = Path.GetFullPath(srcDir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                foreach (var file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        string full = Path.GetFullPath(file);

                        // 跳过可再生的大体积资源
                        string ext = Path.GetExtension(full);
                        if (ext.Length > 0)
                        {
                            bool skip = false;
                            foreach (var s in SkippedBackupExtensions)
                            {
                                if (string.Equals(ext, s, StringComparison.OrdinalIgnoreCase))
                                { skip = true; break; }
                            }
                            if (skip) continue;
                        }

                        string rel = full.Substring(baseFull.Length)
                            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                        var entry = zip.CreateEntry(rel.Replace('\\', '/'),
                            System.IO.Compression.CompressionLevel.Optimal);

                        using (var dst = entry.Open())
                        using (var src = File.OpenRead(full))
                        {
                            src.CopyTo(dst, 81920);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* 单个文件读不到（被占用）就跳过，不要整包失败 */ }
                }

                // ★ 留一份说明在包里，用户解压时知道少了什么、为什么少
                try
                {
                    var note = zip.CreateEntry("_备份说明.txt",
                        System.IO.Compression.CompressionLevel.Optimal);
                    using (var w = new StreamWriter(note.Open(), new System.Text.UTF8Encoding(false)))
                    {
                        w.WriteLine("SoulLink BGI 配置备份");
                        w.WriteLine("备份时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        w.WriteLine("源目录：" + srcDir);
                        w.WriteLine();
                        w.WriteLine("本备份包含：");
                        w.WriteLine("  · JSON 配置（路径、任务序列、账号等）");
                        w.WriteLine("  · INI / TXT / MD / LOG");
                        w.WriteLine("  · JS 脚本源码");
                        w.WriteLine("  · .bak 历史备份");
                        w.WriteLine();
                        w.WriteLine("本备份**不包含**：");
                        w.WriteLine("  PNG / JPG / ICO 等图片、字体、音视频、DLL/EXE、压缩包。");
                        w.WriteLine("  这些是脚本自带的资源文件，可从原始脚本包重新获取，");
                        w.WriteLine("  重复打包只会让备份体积膨胀数倍。");
                        w.WriteLine();
                        w.WriteLine("如需完整还原，请用原始脚本包补齐资源文件。");
                    }
                }
                catch { }
            }
        }

        // ══════════ 解压覆盖 ══════════

    }
}