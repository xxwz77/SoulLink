using System;
using System.IO;

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

        /// <summary>返回可用于投送的代理 exe 路径；失败时退回原 exe（保持旧行为）。</summary>
        public static string Resolve()
        {
            if (!string.IsNullOrEmpty(_cached) && File.Exists(_cached)) return _cached;

            string self = SystemLauncher.ResolveSelfExe();
            if (string.IsNullOrEmpty(self) || !File.Exists(self)) return self;

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
                    if (File.Exists(host)) { try { File.SetAttributes(host, FileAttributes.Normal); File.Delete(host); } catch { } }
                    File.Copy(self, host, true);
                    // 改不动清单就不能用：宁可让投送失败并明确报错，也不要冒充成功。
                    if (!ManifestPatcher.MakeAsInvoker(host)) return self;
                }
                _cached = host;
                return host;
            }
            catch { return self; }
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
