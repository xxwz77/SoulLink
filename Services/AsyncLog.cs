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

        public static void Append(string path, string text)
        {
            try { Queue.TryAdd(new Entry(path, text, false)); } catch { }
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
