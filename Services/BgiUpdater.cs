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

                    Log("│ [" + t.UserName + "] 停止 BGI / 茶包助手…");
                    await StopBgiAsync(sid, t.BgiExePath,
                        s => Log("│   [" + t.UserName + "] " + s)).ConfigureAwait(false);
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

        /// <summary>找到 7za.exe（随程序一起分发）。</summary>
        private static string Find7za()
        {
            string baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "7za.exe"),
                Path.Combine(baseDir, "7z", "7za.exe"),
                Path.Combine(baseDir, "tools", "7za.exe"),
            };

            foreach (var p in candidates)
                if (File.Exists(p)) return p;

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

        /// <summary>
        /// 停止目标会话的 BGI。
        /// 先发该账号配置的停止快捷键，等 3 秒；不行再杀进程。
        /// 进程不存在直接返回。
        /// </summary>
        private static async Task StopBgiAsync(int sessionId, string bgiExePath,
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
        /// %LOCALAPPDATA%\SoulLink\bgi-backups
        /// </summary>
        public static string BackupRoot
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SoulLink", "bgi-backups");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>
        /// 把 BGI 的 User 目录（用户配置 + 自定义脚本）复制一份。
        ///
        /// ★ 备份到 SoulLink 自己的数据目录，不放在 BGI 目录里：
        ///   %LOCALAPPDATA%\SoulLink\bgi-backups\<账号>\User_yyyyMMdd_HHmmss
        ///   这样多个账号的备份集中管理，BGI 目录也不会被塞满。
        /// </summary>
        private static async Task<string> BackupUserDirAsync(
            string userName, string userDir, CancellationToken ct)
        {
            try
            {
                string safeUser = string.Join("_", userName.Split(Path.GetInvalidFileNameChars()));
                string bak = Path.Combine(BackupRoot, safeUser,
                    "User_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));

                await Task.Run(() => CopyDirRecursive(userDir, bak, ct), ct).ConfigureAwait(false);
                return bak;
            }
            catch { return ""; }
        }

        /// <summary>递归复制目录。</summary>
        private static void CopyDirRecursive(string src, string dst, CancellationToken ct)
        {
            Directory.CreateDirectory(dst);

            foreach (var file in Directory.GetFiles(src))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
                }
                catch { }
            }

            foreach (var dir in Directory.GetDirectories(src))
            {
                ct.ThrowIfCancellationRequested();
                CopyDirRecursive(dir, Path.Combine(dst, Path.GetFileName(dir)), ct);
            }
        }

        // ══════════ 解压覆盖 ══════════

    }
}