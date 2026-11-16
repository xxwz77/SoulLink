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
        private const long MaxBytes = 5L * 1024 * 1024;

        private static readonly System.Collections.Generic.HashSet<string> Rotated
            = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void Append(string path, string text)
        {
            try
            {
                // 轮转检查只在文件"长到一定程度"时做，避免每次写入都查磁盘
                if (!Rotated.Contains(path))
                {
                    try
                    {
                        var fi = new FileInfo(path);
                        if (fi.Exists && fi.Length > MaxBytes)
                        {
                            string old = path + ".old";
                            try { File.Delete(old); } catch { }
                            try { File.Move(path, old); } catch { }
                        }
                        else Rotated.Add(path);   // 未超限，本进程内不再重复检查
                    }
                    catch { }
                }
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
