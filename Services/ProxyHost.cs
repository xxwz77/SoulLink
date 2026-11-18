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
                if (!(dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc >= src.LastWriteTimeUtc))
                {
                    if (dst.Exists) { try { dst.IsReadOnly = false; dst.Delete(); } catch { } }
                    File.Copy(self, host, true);
                    // 改不动清单就不能用：宁可让投送失败并明确报错，也不要冒充成功。
                    if (!ManifestPatcher.MakeAsInvoker(host)) return self;
                }
                _cached = host;
                return host;
            }
            catch { return self; }
        }
    }
}
