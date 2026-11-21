using System;
using System.IO;
using System.Threading;

namespace SoulLink.Services
{
    /// <summary>
    /// 注入代理的本体副本（不带管理员清单）。
    ///
    /// ★ 为什么需要：
    ///   代理是用 CreateProcessAsUser 以「目标会话的普通用户令牌」启动的。
    ///   主程序 SoulLink.exe 清单是 requireAdministrator，普通用户令牌无法满足它，
    ///   内核直接返回 ERROR_ELEVATION_REQUIRED(740)，代理根本起不来。
    ///   代理本身不需要管理员权限（只是在用户桌面收发输入），
    ///   所以这里为它准备一个 asInvoker 副本，投送时启动这个副本。
    ///
    /// ★ 与引导进程无关：引导进程仍用原版 exe（SYSTEM 任务启动，必须带清单）。
    /// </summary>
    public static class ProxyHost
    {
        private static string? _cached;

        /// <summary>
        /// 代理副本生成必须串行。
        /// ★ 会话监控每 2 秒扫一轮，A2/A3 在同一轮里并行投送，两个线程会同时走到
        ///   File.Delete + File.Copy 同一路径；先到的被后到的删掉/占用，就抛
        ///   "The process cannot access the file ... SoulLink.proxy.exe"，
        ///   表现为"只投上一个通道，另一个进重建队列"（实测多次）。
        ///   锁住整个"判定 + 生成"过程，保证并发投送只生成一次。
        /// </summary>
        private static readonly Lock ResolveGate = new();

        /// <summary>返回可用于投送的代理 exe 路径；失败时退回原 exe（保持旧行为）。</summary>
        public static string Resolve()
        {
            if (!string.IsNullOrEmpty(_cached) && File.Exists(_cached)) return _cached;

            string self = SystemLauncher.ResolveSelfExe();
            if (string.IsNullOrEmpty(self) || !File.Exists(self)) return self;

            lock (ResolveGate)
            {
                // 双重检查：等锁期间可能已被别的线程生成好。
                if (!string.IsNullOrEmpty(_cached) && File.Exists(_cached)) return _cached;
                return ResolveCore(self);
            }
        }

        private static string ResolveCore(string self)
        {
            try
            {
                string dir = Path.Combine(AppIdentity.TempRoot, "proxy");
                Directory.CreateDirectory(dir);
                string name = Path.GetFileNameWithoutExtension(self) + ".proxy.exe";
                string host = Path.Combine(dir, name);

                var src = new FileInfo(self);
                var dst = new FileInfo(host);
                // ★ 判据必须能忍受“部署后 exe 被替换、大小恰好相同”：
                //   只比大小和修改时间会永远复用旧副本（实测踩过：新旧大小都是 66,335,966，
                //   副本时间戳还比 exe 新，于是一直用旧代理）。这里改用内容哈希。
                if (!IsUpToDate(src, host))
                {
                    CopyAgentWithRetry(self, host);
                    // 改不动清单就不能用：宁可让投送失败并明确报错，也不要冒充成功。
                    if (!ManifestPatcher.MakeAsInvoker(host))
                    {
                        // 已是 asInvoker 的同版本代理可直接复用；不要重复改写清单。
                        byte[] bytes = File.ReadAllBytes(host);
                        if (!System.Text.Encoding.ASCII.GetString(bytes).Contains("level=\"asInvoker\""))
                            throw new InvalidOperationException("代理清单改写失败");
                    }
                }
                _cached = host;
                return host;
            }
            catch (Exception ex) { throw new InvalidOperationException("无法生成代理：" + ex.Message, ex); }
        }

        /// <summary>
        /// 复制代理本体，并对"文件被占用"做有限重试。
        /// 杀软扫描、上一轮投送刚退出、资源管理器缩略图都可能短暂锁住这个 exe；
        /// 一次失败就放弃会把整个通道投送判死（表现为只投上一个账号）。
        /// </summary>
        private static void CopyAgentWithRetry(string self, string host)
        {
            const int attempts = 8;
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    if (File.Exists(host)) File.SetAttributes(host, FileAttributes.Normal);
                    File.Copy(self, host, true);
                    return;
                }
                catch (IOException) when (i < attempts - 1)
                {
                    System.Threading.Thread.Sleep(120 * (i + 1));
                }
                catch (UnauthorizedAccessException) when (i < attempts - 1)
                {
                    System.Threading.Thread.Sleep(120 * (i + 1));
                }
            }
            throw new IOException("代理副本被占用，多次重试后仍无法写入：" + host);
        }

        /// <summary>副本是否确实由当前主程序生成（按哈希比对，忽略清单改写带来的微小差异）。</summary>
        private static bool IsUpToDate(FileInfo src, string host)
        {
            try
            {
                var dst = new FileInfo(host);
                if (!dst.Exists || dst.Length != src.Length) return false;
                // 比对除清单区域外的字节：清单只改 28 字节的等级属性。
                byte[] a = File.ReadAllBytes(src.FullName);
                byte[] b = File.ReadAllBytes(host);
                if (a.Length != b.Length) return false;
                int diff = 0;
                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i] == b[i]) continue;
                    if (++diff > 64) return false;   // 差异过多 → 不是同一个程序
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>作废内存缓存，下次 Resolve 时重新校验（不立即删文件）。</summary>
        public static void Invalidate() => _cached = null;

        /// <summary>
        /// 启动清理：如果磁盘上的代理副本与当前主程序不一致（版本更新过），直接删除。
        /// ★ 不这么做，旧版本副本会一直留在磁盘上，任何绕过 Resolve 的路径都可能用上它。
        /// </summary>
        public static void RemoveIfStale()
        {
            lock (ResolveGate) { RemoveIfStaleCore(); }
        }

        private static void RemoveIfStaleCore()
        {
            try
            {
                _cached = null;
                string self = SystemLauncher.ResolveSelfExe();
                if (string.IsNullOrEmpty(self) || !File.Exists(self)) return;
                string dir = Path.Combine(AppIdentity.TempRoot, "proxy");
                if (!Directory.Exists(dir)) return;
                var src = new FileInfo(self);
                foreach (var f in Directory.GetFiles(dir, "*.proxy.exe", SearchOption.TopDirectoryOnly))
                {
                    var fi = new FileInfo(f);
                    if (fi.Length == src.Length && IsUpToDate(src, f)) continue;   // 一致，保留
                    try { File.SetAttributes(f, FileAttributes.Normal); fi.Delete(); LogBus.Info("清理过期代理副本", fi.Name); } catch { }
                }
            }
            catch { }
        }
    }
}
