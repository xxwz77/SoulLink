using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>有界后台日志：输入钩子与 UI 不等待文件 IO。</summary>
    public static class AsyncLog
    {
        private sealed record Entry(string Path, string Text, bool Rewrite);
        private static readonly BlockingCollection<Entry> Queue = new(512);
        private static readonly Task Worker = Task.Run(() =>
        {
            try
            {
                foreach (var entry in Queue.GetConsumingEnumerable())
                {
                    try
                    {
                        string? dir = Path.GetDirectoryName(entry.Path);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        if (entry.Rewrite) File.WriteAllText(entry.Path, entry.Text, new UTF8Encoding(false));
                        else File.AppendAllText(entry.Path, entry.Text, new UTF8Encoding(false));
                    }
                    catch { }
                }
            }
            catch { }
        });

        /// <summary>
        /// 日志文件大小上限（5 MB）。超过就轮转：保留一份 .old，其余丢弃。
        /// ★ 长期挂机必须限制：原来日志无上限，按实际写入速度一年可涨到数 GB。
        /// </summary>
        internal const long MaxBytes = 5L * 1024 * 1024;

        private static readonly System.Collections.Generic.HashSet<string> Rotated
            = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 超大就轮转：把 path 改名为 path.old（旧的 .old 先删）。
        ///
        /// ★ 抽出来给两处共用（2026-11-24，t15）：
        ///   · AsyncLog.Append（本类，走队列的那条路）
        ///   · App.AppendErrorLog（error.log —— 全仓唯一「故障态自动升级为持续写盘」的路径，
        ///     原先无上限无轮转；TickHub 每秒一条、每次带完整 ex.ToString()，
        ///     持续异常时约 3.6~18 MB/小时，一天就能把盘写满）
        ///
        /// ★ 语义刻意保持与原来完全一致：**只在文件首次被发现超限时轮转一次**，
        ///   之后本进程内不再重复检查（靠 seen 集合记住），避免每次写都 stat 一次磁盘。
        ///   轮转失败（被占用/权限）就静默跳过 —— 宁可日志大一点，绝不能让写日志抛异常。
        /// </summary>
        /// <param name="path">目标日志文件。</param>
        /// <param name="seen">本进程内已确认「未超限」的路径集合，命中即跳过检查。</param>
        internal static void RotateIfOversized(string path, System.Collections.Generic.HashSet<string> seen)
        {
            try
            {
                if (seen.Contains(path)) return;
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    string old = path + ".old";
                    try { File.Delete(old); } catch { }
                    try { File.Move(path, old); } catch { }
                }
                else seen.Add(path);   // 未超限，本进程内不再重复检查
            }
            catch { }
        }

        public static void Append(string path, string text)
        {
            try
            {
                RotateIfOversized(path, Rotated);
                Queue.TryAdd(new Entry(path, text, false));
            }
            catch { }
        }

        public static void Write(string path, string text)
        {
            try { Queue.TryAdd(new Entry(path, text, true)); } catch { }
        }

        public static async Task FlushAsync(int timeoutMs = 1500)
        {
            if (Queue.IsCompleted) return;
            Queue.CompleteAdding();
            try { await Worker.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)).ConfigureAwait(false); } catch { }
        }
    }
}
