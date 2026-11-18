using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace SoulLink.Services
{
    /// <summary>
    /// 启动时清理投送残留。
    /// ★ 为什么需要：正常路径会删计划任务和 job/结果文件，但如果进程被强杀
    ///   （任务管理器结束、崩溃、断电），finally 不会执行，反复重启会累积残留。
    ///   这里只清"确定属于本应用且已过期"的东西，不碰其它程序。
    /// </summary>
    public static class LaunchSweeper
    {
        public static void Sweep()
        {
            try { SweepTasks(); } catch { }
            try { SweepFiles(); } catch { }
            // ★ 版本更新后，磁盘上的旧代理副本必须删除；它大小可能与新程序相同，
            //   只比大小/时间会一直复用它（实测踩过：新旧都是 66,335,966，哈希却不同）。
            try { ProxyHost.RemoveIfStale(); } catch { }
        }

        /// <summary>删除本应用前缀、且已超过 10 分钟的一次性投送任务。</summary>
        private static void SweepTasks()
        {
            string query = Run("schtasks.exe", "/query /fo csv /nh");
            if (query.Length == 0) return;
            var prefix = AppIdentity.TaskPrefix;
            foreach (var raw in query.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || !line.StartsWith("\"", StringComparison.Ordinal)) continue;
                int close = line.IndexOf('"', 1);
                if (close <= 1) continue;
                string name = line.Substring(1, close - 1);
                // schtasks 的 CSV 里任务名带根路径前缀，例如 \SoulLinkRun_abc
                if (name.StartsWith("\\", StringComparison.Ordinal)) name = name.Substring(1);
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                // 只处理"一次性投送任务"，自启动任务名不同，不会误删
                Run("schtasks.exe", "/delete /tn \"" + name + "\" /f");
                LogBus.Warn("清理遗留投送任务", name);
            }
        }

        /// <summary>清理 jobs 目录里过期的 job/结果文件，以及不再使用的引导宿主副本。</summary>
        private static void SweepFiles()
        {
            var root = AppIdentity.TempRoot;
            if (!Directory.Exists(root)) return;
            var cutoff = DateTime.UtcNow.AddMinutes(-30);
            int removed = 0;
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.LastWriteTimeUtc > cutoff) continue;
                    // ★ 必须包含 proxy：旧代理副本不清会在版本更新后继续被复用（实测踩过）。
                    if (!(fi.Directory!.Name == "jobs" || fi.Directory.Name == "boot" || fi.Directory.Name == "proxy")) continue;
                    fi.IsReadOnly = false;
                    fi.Delete();
                    removed++;
                }
                catch { }
            }
            if (removed > 0) LogBus.Info("清理投送临时文件", removed + " 个");
        }

        private static string Run(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using var p = Process.Start(psi);
                if (p == null) return "";
                string o = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                return o;
            }
            catch { return ""; }
        }
    }
}
