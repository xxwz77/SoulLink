using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace SoulLink.Services
{
    /// <summary>
    /// 狗粮 records 文件的 FileSystemWatcher 桥。
    ///
    /// ── 为什么需要它 ──────────────────────────────────────────────
    ///   狗粮记录读的是 AAA 脚本写的 records 文件（不是 BGI 日志），
    ///   原来只能在 AccountStateCenter 的 30 秒慢档轮询里被动发现变化
    ///   → 脚本刚写完，界面最多要等 30 秒才显示。
    ///   挂 watcher 后：文件一写就置标记，下一拍（1 秒内）就重新读。
    ///
    /// ── 性能（★ 关键设计，沿用本仓库既有教训）────────────────────
    ///   BgiLogCenter.EnsureWatcher 的注释记录过：BGI 满速写日志时每秒触发
    ///   几十次 Changed 事件，**在事件里直接解析** 会把 CPU 打到 25%。
    ///   所以这里的铁律是：
    ///     · 事件回调**只置一个脏标记**（纳秒级，零 IO、零解析）；
    ///     · 真正的重活由 AccountStateCenter 下一拍消费（30 秒慢档 / 1 秒快档）；
    ///     · 另外 TickHub 每 5 秒兜底扫一次（watcher 在网络盘/异常时可能不触发）。
    ///
    ///   ★ 狗粮文件本身一天只写 1~2 次，比日志稀疏几个数量级，
    ///     所以「挂 watcher」对它是纯收益：空闲时零开销，有变化时秒级响应。
    ///
    /// ── 句柄生命周期 ─────────────────────────────────────────────
    ///   FileSystemWatcher 持有系统句柄，**必须 Dispose**。
    ///   BgiLogCenter.cs 里踩过「Entries.Remove 没 Dispose 导致句柄泄漏」的坑。
    ///   这里按「账号 + 文件路径」缓存，路径变化时先 Dispose 旧的再建新的；
    ///   账号被移除时由 AccountStateCenter 调 <see cref="StopFor"/> 清理。
    /// </summary>
    public static class DogfoodWatcher
    {
        /// <summary>每个账号最近的「文件已变化」标记（由 watcher 置位）。</summary>
        private static readonly ConcurrentDictionary<string, int> Dirty
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>每个账号当前跟踪的 (文件路径, watcher)。</summary>
        private static readonly ConcurrentDictionary<string, (string Path, FileSystemWatcher Watcher)> Watchers
            = new(StringComparer.OrdinalIgnoreCase);

        private static readonly Lock Gate = new();

        /// <summary>
        /// 确保某账号在跟踪指定 records 文件。
        /// ★ 同一路径重复调用是零成本（直接返回）。
        /// ★ 路径变化时自动重建（换账号 / 换 BGI 路径）。
        /// </summary>
        public static void Ensure(string account, string? recordFile)
        {
            if (string.IsNullOrWhiteSpace(account)) return;
            if (string.IsNullOrWhiteSpace(recordFile)) { StopFor(account); return; }

            // 已经跟的是同一个文件 → 不动
            if (Watchers.TryGetValue(account, out var cur))
            {
                if (string.Equals(cur.Path, recordFile, StringComparison.OrdinalIgnoreCase)) return;
                StopFor(account);
            }

            string dir = Path.GetDirectoryName(recordFile) ?? "";
            string name = Path.GetFileName(recordFile);
            if (dir.Length == 0 || name.Length == 0) return;
            // 目录还不存在（脚本还没写过）→ 不建 watcher，等下次刷新再试。
            //   ★ 为什么不监听父目录：records 目录可能整天不出现，白占句柄。
            if (!Directory.Exists(dir)) { StopFor(account); return; }

            try
            {
                var w = new FileSystemWatcher(dir)
                {
                    Filter = name,
                    // 只关心「内容/大小/文件被替换」三种；不够用才加 FileName。
                    NotifyFilter = NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.FileName,
                    IncludeSubdirectories = false,
                    // 狗粮文件写得很稀疏，默认 8KB 缓冲足够；给到 16KB 更稳。
                    InternalBufferSize = 16 * 1024,
                };
                string acc = account;
                // ★ 事件回调只置标记 —— 绝不在这里读文件（见类注释的性能铁律）。
                FileSystemEventHandler onWrite = (_, __) => Dirty[acc] = 1;
                w.Changed += onWrite;
                w.Created += onWrite;
                w.Renamed += (_, __) => Dirty[acc] = 1;
                w.Error += (_, __) =>
                {
                    // 缓冲区溢出 / 路径失效：标记脏让上层重读，并移除 watcher 等下次重建。
                    try { LogBus.Warn("狗粮·监听异常", acc + " watcher 出错，回退轮询"); } catch { }
                    try { Dirty[acc] = 1; StopFor(acc); } catch { }
                };
                w.EnableRaisingEvents = true;

                lock (Gate) { Watchers[account] = (recordFile, w); }
                // ★ 可观测：确认 watcher 真的挂上了。
                //   挂不上会静默退化为轮询（功能仍正常，只是延迟回到 30 秒），
                //   没有这条日志就无从判断「是没触发还是没挂上」。
                //   LogBus 只在内存（界面可见）—— 实测验证过效果后不再写 error.log，
                //   避免排查残留污染用户错误日志。
                try { LogBus.Info("狗粮·监听", account + " 已监听 " + name); } catch { }
            }
            catch
            {
                // watcher 建不起来不是致命问题：AccountStateCenter 的轮询仍会读，
                // 只是延迟回到轮询周期。
                StopFor(account);
            }
        }

        /// <summary>
        /// 取出并清除某账号的「已变化」标记。
        /// 返回 true 表示自上次消费以来文件被写过 → 上层应重新解析。
        ///
        /// ★ 只有「真正要重新解析的那一处」才调 ConsumeDirty。
        ///   其它地方（例如触发一轮刷新的巡检拍）必须用 <see cref="PeekDirty"/>，
        ///   否则会把标记吃掉，导致那次刷新里读不到「文件变了」而跳过重解析。
        /// </summary>
        public static bool ConsumeDirty(string account)
        {
            if (string.IsNullOrWhiteSpace(account)) return false;
            return Dirty.TryRemove(account, out _);
        }

        /// <summary>
        /// 只查看不清除的「已变化」标记。
        /// 供「是否该触发一轮刷新」这类判断使用 —— 真正的清除交给 ConsumeDirty。
        /// </summary>
        public static bool PeekDirty(string account)
        {
            if (string.IsNullOrWhiteSpace(account)) return false;
            return Dirty.ContainsKey(account);
        }

        /// <summary>停止跟踪某账号（账号被移除 / 换路径 / 程序退出时调用）。</summary>
        public static void StopFor(string account)
        {
            if (string.IsNullOrWhiteSpace(account)) return;
            try
            {
                lock (Gate)
                {
                    if (Watchers.TryRemove(account, out var cur))
                    {
                        try { cur.Watcher.EnableRaisingEvents = false; } catch { }
                        try { cur.Watcher.Dispose(); } catch { }
                    }
                }
            }
            catch { }
            Dirty.TryRemove(account, out _);
        }

        /// <summary>停止全部并释放句柄（程序退出时调用，防句柄泄漏）。</summary>
        public static void StopAll()
        {
            foreach (string acc in Watchers.Keys) StopFor(acc);
        }

        /// <summary>诊断：当前跟踪的账号数（供状态页/日志确认 watcher 是否真的挂上）。</summary>
        public static int ActiveCount => Watchers.Count;
    }
}
